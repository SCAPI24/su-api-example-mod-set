using System;
using System.Collections.Generic;
using Engine;
using Game;
using SuEventBus;
using TemplatesDatabase;

namespace Channel
{
    /// <summary>
    /// 替换 <see cref="SubsystemButtonBlockBehavior"/>（同一个子系统模板，只换 Class）。
    /// 职责：
    ///   1. 每通道电压表（"同通道号的导线是一根"）；
    ///   2. 按钮格/物品的编辑入口先发事件（联机 Mod 可接管），没人接管才走原版；
    ///   3. 信道格/物品的编辑入口 = 16×16 通道号对话框；
    ///   4. 旋转/编辑改 data 时的**状态交接**（引擎会清持久电压，这里补回来）。
    /// </summary>
    public sealed class SubsystemChannelBlockBehavior : SubsystemButtonBlockBehavior
    {
        private const double HandoffLifetime = 5.0;

        private SubsystemElectricity m_subsystemElectricity;

        // 每通道电压表（纯逻辑类，见 ChannelBus）；这里额外维护"通道→元件"列表用于排队
        private readonly ChannelBus m_bus = new ChannelBus();

        private readonly Dictionary<int, List<ChannelElectricElement>> m_elementsByChannel =
            new Dictionary<int, List<ChannelElectricElement>>();

        private readonly Dictionary<Point3, float> m_handoffVoltage = new Dictionary<Point3, float>();
        private readonly Dictionary<Point3, double> m_handoffTime = new Dictionary<Point3, double>();

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            m_subsystemElectricity = Project.FindSubsystem<SubsystemElectricity>(throwOnError: true);

            // 通道值变化 → 把该通道的全部元件排进下一电路步（跨距离传播靠这一步收敛）
            m_bus.ChannelChanged = HandleChannelChanged;

            // 本 Mod 的中文词条与对话框控件注册给翻译 Mod（未安装则静默跳过）
            ChannelText.RegisterWithTranslationMod();
        }

        // ───────────────────────── 通道总线 ─────────────────────────

        /// <summary>
        /// 上报本元件的本地导线电压，并取回"同通道号全部元件"的线或（取最大）。
        /// 实现见 <see cref="ChannelBus"/>：与模拟顺序无关、不会丢贡献。
        /// </summary>
        public float JoinChannel(ChannelElectricElement element, int channel, float local)
        {
            return m_bus.Join(element, channel, local);
        }

        /// <summary>
        /// 元件当前不接入通道（Bottom 低电平/0V，或刚改过通道号）时调用：
        /// 必须撤掉自己的总线贡献，否则它之前的高电平会一直挂在同通道上。
        /// </summary>
        public void LeaveChannel(ChannelElectricElement element)
        {
            m_bus.Leave(element);
        }

        private void HandleChannelChanged(int channel)
        {
            int step = m_subsystemElectricity != null ? m_subsystemElectricity.CircuitStep : 0;
            QueueChannelElements(channel, step + 1);
        }

        private void QueueChannelElements(int channel, int circuitStep)
        {
            if (m_subsystemElectricity == null)
                return;
            if (!m_elementsByChannel.TryGetValue(channel, out List<ChannelElectricElement> list))
                return;
            for (int i = 0; i < list.Count; i++)
                m_subsystemElectricity.QueueElectricElementForSimulation(list[i], circuitStep);
        }

        public void RegisterElement(ChannelElectricElement element)
        {
            int channel = GetElementChannel(element);
            if (channel < 0)
                return;
            if (!m_elementsByChannel.TryGetValue(channel, out List<ChannelElectricElement> list))
            {
                list = new List<ChannelElectricElement>();
                m_elementsByChannel.Add(channel, list);
            }
            if (!list.Contains(element))
                list.Add(element);
            // 先以 0 电压入场：不会抬高通道，首次 Simulate 会写入真实本地电压
            m_bus.Join(element, channel, 0f);
        }

        public void UnregisterElement(ChannelElectricElement element)
        {
            // 只改 value 列表、不改字典本身 → 遍历中安全
            foreach (KeyValuePair<int, List<ChannelElectricElement>> pair in m_elementsByChannel)
                pair.Value.Remove(element);

            // 退出总线：若通道值因此下降，ChannelChanged 会排队同通道剩下的元件
            m_bus.Leave(element);
        }

        private int GetElementChannel(ChannelElectricElement element)
        {
            if (SubsystemTerrain?.Terrain == null || element.CellFaces.Count == 0)
                return -1;
            CellFace cellFace = element.CellFaces[0];
            Point3 point = cellFace.Point;
            int value = SubsystemTerrain.Terrain.GetCellValue(point.X, point.Y, point.Z);
            int data = Terrain.ExtractData(value);
            return ButtonBlock.IsChannel(data) ? ButtonBlock.GetChannelNumber(data) : -1;
        }

        // ───────────────────────── 状态交接 ─────────────────────────

        /// <summary>改 data 之前调用：把当前持久电压快照下来，交给重建后的元件写回。</summary>
        public void PreserveState(Point3 point, float voltage)
        {
            PruneHandoff();
            m_handoffVoltage[point] = voltage;
            m_handoffTime[point] = Time.RealTime;
        }

        public void PreserveStateFromTerrain(Point3 point)
        {
            if (m_subsystemElectricity == null)
                return;
            float? voltage = m_subsystemElectricity.ReadPersistentVoltage(point);
            if (voltage.HasValue)
                PreserveState(point, voltage.Value);
        }

        public bool TryConsumeHandoff(Point3 point, out float voltage)
        {
            if (m_handoffVoltage.TryGetValue(point, out voltage))
            {
                m_handoffVoltage.Remove(point);
                m_handoffTime.Remove(point);
                return true;
            }
            voltage = 0f;
            return false;
        }

        private void PruneHandoff()
        {
            if (m_handoffTime.Count == 0)
                return;

            double now = Time.RealTime;
            List<Point3> stale = null;
            foreach (KeyValuePair<Point3, double> pair in m_handoffTime)
            {
                if (now - pair.Value > HandoffLifetime)
                {
                    stale ??= new List<Point3>();
                    stale.Add(pair.Key);
                }
            }
            if (stale == null)
                return;
            for (int i = 0; i < stale.Count; i++)
            {
                m_handoffTime.Remove(stale[i]);
                m_handoffVoltage.Remove(stale[i]);
            }
        }

        // ───────────────────────── 编辑入口 ─────────────────────────

        // Source: Survivalcraft/Game/SubsystemButtonBlockBehavior.cs:OnEditBlock
        public override bool OnEditBlock(int x, int y, int z, int value, ComponentPlayer componentPlayer)
        {
            if (ButtonBlock.IsChannel(Terrain.ExtractData(value)))
                return EditChannelBlock(x, y, z, value, componentPlayer);

            ButtonEditRequest request = new ButtonEditRequest
            {
                Player = componentPlayer,
                IsBlock = true,
                Point = new Point3(x, y, z),
                Value = value
            };
            SuEventHub.Publish(request);
            if (request.Suppressed || request.Handled)
                return true;

            return base.OnEditBlock(x, y, z, value, componentPlayer);
        }

        // Source: Survivalcraft/Game/SubsystemButtonBlockBehavior.cs:OnEditInventoryItem
        public override bool OnEditInventoryItem(IInventory inventory, int slotIndex, ComponentPlayer componentPlayer)
        {
            int value = inventory.GetSlotValue(slotIndex);
            int data = Terrain.ExtractData(value);
            if (ButtonBlock.IsChannel(data))
                return EditChannelInventoryItem(inventory, slotIndex, componentPlayer, value, data);

            ButtonEditRequest request = new ButtonEditRequest
            {
                Player = componentPlayer,
                IsBlock = false,
                Inventory = inventory,
                SlotIndex = slotIndex,
                Value = value
            };
            SuEventHub.Publish(request);
            if (request.Suppressed || request.Handled)
                return true;

            return base.OnEditInventoryItem(inventory, slotIndex, componentPlayer);
        }

        private bool EditChannelBlock(int x, int y, int z, int value, ComponentPlayer componentPlayer)
        {
            Point3 point = new Point3(x, y, z);
            int data = Terrain.ExtractData(value);

            ChannelEditRequest request = new ChannelEditRequest
            {
                Player = componentPlayer,
                IsBlock = true,
                Point = point,
                Value = value
            };
            SuEventHub.Publish(request);
            if (request.Suppressed || request.Handled)
                return true;

            EditChannelNumberDialog dialog = new EditChannelNumberDialog(ButtonBlock.GetChannelNumber(data),
                delegate (int newChannel)
                {
                    int newData = ButtonBlock.SetChannelNumber(data, newChannel);
                    ChannelEditSubmitRequest submit = new ChannelEditSubmitRequest
                    {
                        Player = componentPlayer,
                        IsBlock = true,
                        Point = point,
                        OldValue = value,
                        NewData = newData
                    };
                    SuEventHub.Publish(submit);
                    if (submit.Handled)
                        return;

                    PreserveStateFromTerrain(point);
                    SubsystemTerrain.ChangeCell(x, y, z, Terrain.ReplaceData(value, newData));
                });
            DialogsManager.ShowDialog(componentPlayer.GuiWidget, dialog);
            return true;
        }

        private bool EditChannelInventoryItem(IInventory inventory, int slotIndex, ComponentPlayer componentPlayer,
            int value, int data)
        {
            ChannelEditRequest request = new ChannelEditRequest
            {
                Player = componentPlayer,
                IsBlock = false,
                Inventory = inventory,
                SlotIndex = slotIndex,
                Value = value
            };
            SuEventHub.Publish(request);
            if (request.Suppressed || request.Handled)
                return true;

            EditChannelNumberDialog dialog = new EditChannelNumberDialog(ButtonBlock.GetChannelNumber(data),
                delegate (int newChannel)
                {
                    int newData = ButtonBlock.SetChannelNumber(data, newChannel);
                    ChannelEditSubmitRequest submit = new ChannelEditSubmitRequest
                    {
                        Player = componentPlayer,
                        IsBlock = false,
                        Inventory = inventory,
                        SlotIndex = slotIndex,
                        OldValue = value,
                        NewData = newData
                    };
                    SuEventHub.Publish(submit);
                    if (submit.Handled)
                        return;

                    int count = inventory.GetSlotCount(slotIndex);
                    int newValue = Terrain.ReplaceData(value, newData);
                    inventory.RemoveSlotItems(slotIndex, count);
                    inventory.AddSlotItems(slotIndex, newValue, 1);
                });
            DialogsManager.ShowDialog(componentPlayer.GuiWidget, dialog);
            return true;
        }
    }
}
