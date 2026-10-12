using Engine;
using Game;

namespace Channel
{
    /// <summary>
    /// 信道元件。
    ///
    /// 端口：
    ///   Right = X（通道号低 4 位）      Left = Y（通道号高 4 位）      Bottom = 读写使能
    ///   Top / In = 同一根导线（InputOutput，双向）
    ///
    /// Bottom 四态（与 memory bank 同款量化：v = Round(邻格输出 × 15)）：
    ///   未接线            → 默认激活通道（并入同号网）
    ///   v ≥ 8             → 激活通道
    ///   1 ≤ v ≤ 7         → 写入通道号（边沿触发），此时只当普通导线
    ///   v = 0（接了线无信号）→ 只当普通导线
    ///
    /// 常驻行为：Top/In 本地短接（线或取最大，和引擎导线域一致）；
    /// 激活时再与"同通道号"的所有已加载元件并联成一张网。
    /// 状态按 SR latch 方式存持久电压；旋转/编辑写 data 时用"交接"保住状态。
    /// </summary>
    public sealed class ChannelElectricElement : MountedElectricElement
    {
        private readonly SubsystemChannelBlockBehavior m_behavior;
        private float m_voltage;
        private bool m_writeAllowed = true;

        public ChannelElectricElement(SubsystemElectricity subsystemElectricity, CellFace cellFace)
            : base(subsystemElectricity, cellFace)
        {
            m_behavior = subsystemElectricity.Project.FindSubsystem<SubsystemChannelBlockBehavior>(throwOnError: false);

            float? stored = subsystemElectricity.ReadPersistentVoltage(cellFace.Point);
            if (stored.HasValue)
                m_voltage = stored.Value;

            // 旋转/编辑改过 data 后引擎会清掉持久电压，这里用交接快照补回来
            if (m_behavior != null && m_behavior.TryConsumeHandoff(cellFace.Point, out float handoff))
            {
                m_voltage = handoff;
                subsystemElectricity.WritePersistentVoltage(cellFace.Point, m_voltage);
            }
        }

        public override void OnAdded()
        {
            base.OnAdded();
            m_behavior?.RegisterElement(this);
        }

        public override void OnRemoved()
        {
            m_behavior?.UnregisterElement(this);
            base.OnRemoved();
        }

        public override float GetOutputVoltage(int face)
        {
            return m_voltage;
        }

        public override bool Simulate()
        {
            CellFace cellFace = CellFaces[0];
            Point3 point = cellFace.Point;
            int value = SubsystemElectricity.SubsystemTerrain.Terrain.GetCellValue(point.X, point.Y, point.Z);
            int data = Terrain.ExtractData(value);
            if (!ButtonBlock.IsChannel(data))
                return false;

            int rotation = ButtonBlock.GetRotation(data);
            int channel = ButtonBlock.GetChannelNumber(data);

            bool bottomConnected = false;
            int bottomLevel = 0;
            int lowNibble = 0;
            int highNibble = 0;
            float local = 0f;

            foreach (ElectricConnection connection in Connections)
            {
                if (connection.ConnectorType == ElectricConnectorType.Output
                    || connection.NeighborConnectorType == ElectricConnectorType.Input)
                {
                    continue;
                }

                ElectricConnectorDirection? direction = SubsystemElectricity.GetConnectorDirection(
                    cellFace.Face, rotation, connection.ConnectorFace);
                if (!direction.HasValue)
                    continue;

                float neighborVoltage = connection.NeighborElectricElement.GetOutputVoltage(connection.NeighborConnectorFace);
                switch (direction.Value)
                {
                    case ElectricConnectorDirection.Right:
                        lowNibble = (int)MathUtils.Round(neighborVoltage * 15f);
                        break;
                    case ElectricConnectorDirection.Left:
                        highNibble = (int)MathUtils.Round(neighborVoltage * 15f);
                        break;
                    case ElectricConnectorDirection.Bottom:
                        bottomConnected = true;
                        bottomLevel = (int)MathUtils.Round(neighborVoltage * 15f);
                        break;
                    case ElectricConnectorDirection.Top:
                    case ElectricConnectorDirection.In:
                        local = MathUtils.Max(local, GetExternalVoltage(connection.NeighborElectricElement,
                            connection.NeighborConnectorFace));
                        break;
                }
            }

            ChannelLogic.Inputs inputs = default(ChannelLogic.Inputs);
            inputs.BottomConnected = bottomConnected;
            inputs.BottomLevel = bottomLevel;
            inputs.LowNibble = lowNibble;
            inputs.HighNibble = highNibble;
            inputs.Channel = channel;
            inputs.WriteAllowed = m_writeAllowed;

            ChannelLogic.Outputs outputs = ChannelLogic.Decide(inputs);
            m_writeAllowed = outputs.WriteAllowed;

            bool wroteChannel = outputs.Write;
            if (wroteChannel)
            {
                m_behavior?.PreserveState(point, m_voltage);
                int newData = ButtonBlock.SetChannelNumber(data, outputs.NewChannel);
                SubsystemElectricity.SubsystemTerrain.ChangeCell(point.X, point.Y, point.Z,
                    Terrain.ReplaceData(value, newData));
            }

            float result;
            if (outputs.ChannelActive && !wroteChannel && m_behavior != null)
            {
                float bus = m_behavior.JoinChannel(this, channel, local);
                result = MathUtils.Max(local, bus);
            }
            else
            {
                // 不接入通道（Bottom 低/0V）或刚改过通道号：撤掉自己的总线贡献
                m_behavior?.LeaveChannel(this);
                result = local;
            }

            float previous = m_voltage;
            m_voltage = result;
            if (m_voltage != previous)
                SubsystemElectricity.WritePersistentVoltage(point, m_voltage);

            return m_voltage != previous || wroteChannel;
        }

        /// <summary>
        /// 取"真实驱动源"的电压：
        ///   · 跳过信道元件自身（它的输出就是通道总线值，回读会形成自锁，悬空网络会一直保持上次电平）；
        ///   · 相邻导线域深入一层，取该域里除信道元件以外所有源的线或。
        /// Source: Survivalcraft/Game/WireDomainElectricElement.cs:Simulate（线或取最大）
        /// </summary>
        private static float GetExternalVoltage(ElectricElement element, int connectorFace)
        {
            if (element == null)
                return 0f;
            if (element is ChannelElectricElement)
                return 0f;

            if (element is WireDomainElectricElement domain)
            {
                float voltage = 0f;
                foreach (ElectricConnection connection in domain.Connections)
                {
                    if (connection.ConnectorType == ElectricConnectorType.Output
                        || connection.NeighborConnectorType == ElectricConnectorType.Input)
                    {
                        continue;
                    }
                    if (connection.NeighborElectricElement is ChannelElectricElement)
                        continue;
                    voltage = MathUtils.Max(voltage,
                        connection.NeighborElectricElement.GetOutputVoltage(connection.NeighborConnectorFace));
                }
                return voltage;
            }

            return element.GetOutputVoltage(connectorFace);
        }

        public override bool OnInteract(TerrainRaycastResult raycastResult, ComponentMiner componentMiner)
        {
            // Source: Survivalcraft/Game/RotateableElectricElement.cs:OnInteract —— 点击旋转
            CellFace cellFace = CellFaces[0];
            Point3 point = cellFace.Point;
            int value = SubsystemElectricity.SubsystemTerrain.Terrain.GetCellValue(point.X, point.Y, point.Z);
            int data = Terrain.ExtractData(value);
            if (!ButtonBlock.IsChannel(data))
                return false;

            m_behavior?.PreserveState(point, m_voltage);

            int rotation = (ButtonBlock.GetRotation(data) + 1) & 3;
            int newValue = Terrain.ReplaceData(value, ButtonBlock.SetRotation(data, rotation));
            SubsystemElectricity.SubsystemTerrain.ChangeCell(point.X, point.Y, point.Z, newValue);
            SubsystemElectricity.SubsystemAudio.PlaySound("Audio/Click", 1f, 0f,
                new Vector3(point.X, point.Y, point.Z), 2f, autoDelay: true);
            return true;
        }
    }
}
