using System;
using System.Globalization;
using Engine;
using Game;
using SuEventBus;

namespace ScMultiplayer
{
    /// <summary>
    /// 信道 Mod（Channel）通过中间件 SuEventBus 暴露的扩展点适配层。
    ///
    /// 背景：index 142（button）与 ButtonBlockBehavior 子系统现在由信道 Mod 持有；
    /// ScMP 不再抢这两样，改为订阅事件：
    ///   · CircuitElementRequest   —— 提供网络化按钮元件（原来的 ScMultiplayer.ButtonBlock.CreateElectricElement）
    ///   · ButtonEditRequest       —— 按钮电压编辑：权限判定 + 自己的对话框 + 权威提交（原 SuSubsystemButtonBlockBehavior）
    ///   · ChannelEditRequest      —— 信道通道号编辑：只负责"远端编辑的本地抑制"（UI 在信道 Mod）
    ///   · ChannelEditSubmitRequest—— 通道号修改的联机提交（服务端只应用 bit 3-10）
    ///
    /// 这些事件是**可选依赖**：没装信道 Mod 时不会有任何事件，本适配层静默无事。
    /// </summary>
    internal static class SuChannelEventAdapters
    {
        private static IDisposable s_circuitElementToken;
        private static IDisposable s_buttonEditToken;
        private static IDisposable s_channelEditToken;
        private static IDisposable s_channelSubmitToken;

        public static void Subscribe()
        {
            if (s_circuitElementToken != null)
                return;

            s_circuitElementToken = SuEventHub.Subscribe<CircuitElementRequest>(HandleCircuitElementRequest);
            s_buttonEditToken = SuEventHub.Subscribe<ButtonEditRequest>(HandleButtonEditRequest);
            s_channelEditToken = SuEventHub.Subscribe<ChannelEditRequest>(HandleChannelEditRequest);
            s_channelSubmitToken = SuEventHub.Subscribe<ChannelEditSubmitRequest>(HandleChannelEditSubmitRequest);
        }

        public static void Unsubscribe()
        {
            s_circuitElementToken?.Dispose();
            s_circuitElementToken = null;
            s_buttonEditToken?.Dispose();
            s_buttonEditToken = null;
            s_channelEditToken?.Dispose();
            s_channelEditToken = null;
            s_channelSubmitToken?.Dispose();
            s_channelSubmitToken = null;
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/SuCircuitBlocks.cs:ButtonBlock.CreateElectricElement
        private static void HandleCircuitElementRequest(CircuitElementRequest request)
        {
            if (request == null || request.Result != null || request.SubsystemElectricity == null)
                return;

            int contents = Terrain.ExtractContents(request.Value);
            Game.ButtonBlock buttonBlock = contents >= 0 && contents < BlocksManager.Blocks.Length
                ? BlocksManager.Blocks[contents] as Game.ButtonBlock
                : null;
            int face = buttonBlock != null ? buttonBlock.GetFace(request.Value)
                : Terrain.ExtractData(request.Value) & 7;
            request.Result = new SuButtonElectricElement(request.SubsystemElectricity,
                new CellFace(request.X, request.Y, request.Z, face), request.Value);
        }

        // Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemEditableCircuitBlockBehaviors.cs:
        // SuSubsystemButtonBlockBehavior.OnEditBlock / OnEditInventoryItem
        private static void HandleButtonEditRequest(ButtonEditRequest request)
        {
            if (request == null || request.Handled || request.Suppressed || request.Player == null)
                return;

            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.ShouldSuppressRemoteEditableDataEdit(request.Player) == true)
            {
                request.Suppressed = true;
                return;
            }
            // 不能提交（单机 / 无权限）→ 不接管，交回信道 Mod 走原版对话框 + 本地写
            if (multiplayer?.CanSubmitEditableDataEdit(request.Player) != true)
                return;

            if (request.IsBlock)
            {
                int value = request.Value;
                Point3 point = request.Point;
                int data = Terrain.ExtractData(value);
                DialogsManager.ShowDialog(request.Player.GuiWidget,
                    new EditVoltageLevelDialog(ButtonBlock.GetVoltageLevel(data), delegate (int level)
                    {
                        int newData = ButtonBlock.SetVoltageLevel(data, level);
                        multiplayer.TrySubmitEditableBlockData(EditableDataKind.ButtonVoltage,
                            point, request.Player, value, FormatData(newData));
                    }));
                request.Handled = true;
                return;
            }

            IInventory inventory = request.Inventory;
            int slotIndex = request.SlotIndex;
            if (inventory == null || slotIndex < 0)
                return;

            int itemValue = request.Value;
            int itemData = Terrain.ExtractData(itemValue);
            DialogsManager.ShowDialog(request.Player.GuiWidget,
                new EditVoltageLevelDialog(ButtonBlock.GetVoltageLevel(itemData), delegate (int level)
                {
                    int newData = ButtonBlock.SetVoltageLevel(itemData, level);
                    multiplayer.TrySubmitEditableItemData(EditableDataKind.ButtonVoltage,
                        inventory, slotIndex, request.Player, itemValue, FormatData(newData));
                }));
            request.Handled = true;
        }

        private static void HandleChannelEditRequest(ChannelEditRequest request)
        {
            if (request == null || request.Suppressed || request.Handled || request.Player == null)
                return;

            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.ShouldSuppressRemoteEditableDataEdit(request.Player) == true)
            {
                // 远端玩家的编辑不在本地弹窗；UI 仍由信道 Mod 负责
                request.Suppressed = true;
            }
        }

        private static void HandleChannelEditSubmitRequest(ChannelEditSubmitRequest request)
        {
            if (request == null || request.Handled || request.Player == null)
                return;

            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.CanSubmitEditableDataEdit(request.Player) != true)
                return;

            string payload = FormatData(request.NewData);
            bool submitted = request.IsBlock
                ? multiplayer.TrySubmitEditableBlockData(EditableDataKind.ChannelNumber,
                    request.Point, request.Player, request.OldValue, payload)
                : multiplayer.TrySubmitEditableItemData(EditableDataKind.ChannelNumber,
                    request.Inventory, request.SlotIndex, request.Player, request.OldValue, payload);
            if (submitted)
                request.Handled = true;
        }

        private static string FormatData(int data)
        {
            return data.ToString(CultureInfo.InvariantCulture);
        }
    }
}
