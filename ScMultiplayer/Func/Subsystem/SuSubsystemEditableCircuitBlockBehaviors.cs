using Engine;
using Game;
using System.Collections;
using System.Globalization;

namespace ScMultiplayer
{
    public sealed class SuSubsystemAdjustableDelayGateBlockBehavior :
        SubsystemAdjustableDelayGateBlockBehavior
    {
        // Source: Survivalcraft/Game/SubsystemAdjustableDelayGateBlockBehavior.cs:
        // SubsystemAdjustableDelayGateBlockBehavior.OnEditInventoryItem
        public override bool OnEditInventoryItem(IInventory inventory, int slotIndex,
            ComponentPlayer componentPlayer)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.ShouldSuppressRemoteEditableDataEdit(componentPlayer) == true)
                return true;
            if (multiplayer?.CanSubmitEditableDataEdit(componentPlayer) != true)
                return base.OnEditInventoryItem(inventory, slotIndex, componentPlayer);
            int value = inventory.GetSlotValue(slotIndex);
            int data = Terrain.ExtractData(value);
            DialogsManager.ShowDialog(componentPlayer.GuiWidget,
                new EditAdjustableDelayGateDialog(
                    AdjustableDelayGateBlock.GetDelay(data), newDelay =>
                    {
                        int newData = AdjustableDelayGateBlock.SetDelay(data, newDelay);
                        multiplayer.TrySubmitEditableItemData(
                            EditableDataKind.AdjustableDelay, inventory, slotIndex,
                            componentPlayer, value, FormatData(newData));
                    }));
            return true;
        }

        // Source: Survivalcraft/Game/SubsystemAdjustableDelayGateBlockBehavior.cs:
        // SubsystemAdjustableDelayGateBlockBehavior.OnEditBlock
        public override bool OnEditBlock(int x, int y, int z, int value,
            ComponentPlayer componentPlayer)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.ShouldSuppressRemoteEditableDataEdit(componentPlayer) == true)
                return true;
            if (multiplayer?.CanSubmitEditableDataEdit(componentPlayer) != true)
                return base.OnEditBlock(x, y, z, value, componentPlayer);
            int data = Terrain.ExtractData(value);
            DialogsManager.ShowDialog(componentPlayer.GuiWidget,
                new EditAdjustableDelayGateDialog(
                    AdjustableDelayGateBlock.GetDelay(data), newDelay =>
                    {
                        int newData = AdjustableDelayGateBlock.SetDelay(data, newDelay);
                        multiplayer.TrySubmitEditableBlockData(
                            EditableDataKind.AdjustableDelay, new Point3(x, y, z),
                            componentPlayer, value, FormatData(newData));
                    }));
            return true;
        }

        private static string FormatData(int data) =>
            data.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class SuSubsystemSwitchBlockBehavior : SubsystemSwitchBlockBehavior
    {
        // Source: Survivalcraft/Game/SubsystemSwitchBlockBehavior.cs:
        // SubsystemSwitchBlockBehavior.OnEditInventoryItem
        public override bool OnEditInventoryItem(IInventory inventory, int slotIndex,
            ComponentPlayer componentPlayer)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.ShouldSuppressRemoteEditableDataEdit(componentPlayer) == true)
                return true;
            if (multiplayer?.CanSubmitEditableDataEdit(componentPlayer) != true)
                return base.OnEditInventoryItem(inventory, slotIndex, componentPlayer);
            int value = inventory.GetSlotValue(slotIndex);
            int data = Terrain.ExtractData(value);
            DialogsManager.ShowDialog(componentPlayer.GuiWidget,
                new EditVoltageLevelDialog(SwitchBlock.GetVoltageLevel(data), level =>
                {
                    int newData = SwitchBlock.SetVoltageLevel(data, level);
                    multiplayer.TrySubmitEditableItemData(EditableDataKind.SwitchVoltage,
                        inventory, slotIndex, componentPlayer, value, FormatData(newData));
                }));
            return true;
        }

        // Source: Survivalcraft/Game/SubsystemSwitchBlockBehavior.cs:
        // SubsystemSwitchBlockBehavior.OnEditBlock
        public override bool OnEditBlock(int x, int y, int z, int value,
            ComponentPlayer componentPlayer)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.ShouldSuppressRemoteEditableDataEdit(componentPlayer) == true)
                return true;
            if (multiplayer?.CanSubmitEditableDataEdit(componentPlayer) != true)
                return base.OnEditBlock(x, y, z, value, componentPlayer);
            int data = Terrain.ExtractData(value);
            DialogsManager.ShowDialog(componentPlayer.GuiWidget,
                new EditVoltageLevelDialog(SwitchBlock.GetVoltageLevel(data), level =>
                {
                    int newData = SwitchBlock.SetVoltageLevel(data, level);
                    multiplayer.TrySubmitEditableBlockData(EditableDataKind.SwitchVoltage,
                        new Point3(x, y, z), componentPlayer, value, FormatData(newData));
                }));
            return true;
        }

        private static string FormatData(int data) =>
            data.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class SuSubsystemButtonBlockBehavior : SubsystemButtonBlockBehavior
    {
        // Source: Survivalcraft/Game/SubsystemButtonBlockBehavior.cs:
        // SubsystemButtonBlockBehavior.OnEditInventoryItem
        public override bool OnEditInventoryItem(IInventory inventory, int slotIndex,
            ComponentPlayer componentPlayer)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.ShouldSuppressRemoteEditableDataEdit(componentPlayer) == true)
                return true;
            if (multiplayer?.CanSubmitEditableDataEdit(componentPlayer) != true)
                return base.OnEditInventoryItem(inventory, slotIndex, componentPlayer);
            int value = inventory.GetSlotValue(slotIndex);
            int data = Terrain.ExtractData(value);
            DialogsManager.ShowDialog(componentPlayer.GuiWidget,
                new EditVoltageLevelDialog(ButtonBlock.GetVoltageLevel(data), level =>
                {
                    int newData = ButtonBlock.SetVoltageLevel(data, level);
                    multiplayer.TrySubmitEditableItemData(EditableDataKind.ButtonVoltage,
                        inventory, slotIndex, componentPlayer, value, FormatData(newData));
                }));
            return true;
        }

        // Source: Survivalcraft/Game/SubsystemButtonBlockBehavior.cs:
        // SubsystemButtonBlockBehavior.OnEditBlock
        public override bool OnEditBlock(int x, int y, int z, int value,
            ComponentPlayer componentPlayer)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.ShouldSuppressRemoteEditableDataEdit(componentPlayer) == true)
                return true;
            if (multiplayer?.CanSubmitEditableDataEdit(componentPlayer) != true)
                return base.OnEditBlock(x, y, z, value, componentPlayer);
            int data = Terrain.ExtractData(value);
            DialogsManager.ShowDialog(componentPlayer.GuiWidget,
                new EditVoltageLevelDialog(ButtonBlock.GetVoltageLevel(data), level =>
                {
                    int newData = ButtonBlock.SetVoltageLevel(data, level);
                    multiplayer.TrySubmitEditableBlockData(EditableDataKind.ButtonVoltage,
                        new Point3(x, y, z), componentPlayer, value, FormatData(newData));
                }));
            return true;
        }

        private static string FormatData(int data) =>
            data.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class SuSubsystemPistonBlockBehavior : SubsystemPistonBlockBehavior,
        IUpdateable
    {
        public new UpdateOrder UpdateOrder => base.UpdateOrder;

        // Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:
        // SubsystemPistonBlockBehavior.Update
        void IUpdateable.Update(float dt)
        {
            if (ScMultiplayer.client?.IsConnected != true || ScMultiplayer.IsHost)
            {
                base.Update(dt);
                return;
            }

            // Client piston sets are visual replicas. Discard native queued Stop actions because
            // StopPiston commits blocks with drops and destruction particles; the host terrain
            // batch owns that result. Keep the native shaft/arm shape update for smooth animation.
            try
            {
                ScMultiplayer.ModManager.ModParentField.GetParentField<IDictionary>(this,
                    "m_actions", typeof(SubsystemPistonBlockBehavior)).Clear();
                ScMultiplayer.ModManager.ModParentMethod.InvokeParentMethod(this,
                    "UpdateMovableBlocks");
            }
            catch
            {
            }
        }

        // Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:
        // SubsystemPistonBlockBehavior.OnBlockRemoved
        public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
        {
            if (ScMultiplayer.client?.IsConnected == true && !ScMultiplayer.IsHost)
            {
                int contents = Terrain.ExtractContents(value);
                if (contents == PistonHeadBlock.Index)
                {
                    // Host terrain already contains every final shaft/head cell. Running the
                    // native cascade here mistakes authoritative retraction for player breaking
                    // and creates debris particles on the client.
                    return;
                }
                if (contents == Game.PistonBlock.Index)
                {
                    SubsystemMovingBlocks moving = Project?
                        .FindSubsystem<SubsystemMovingBlocks>(false);
                    IMovingBlockSet set = moving?.FindMovingBlocks(
                        "Piston", new Point3(x, y, z));
                    if (set != null)
                        moving.RemoveMovingBlockSet(set);
                    return;
                }
            }
            base.OnBlockRemoved(value, newValue, x, y, z);
        }

        // Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:
        // SubsystemPistonBlockBehavior.OnEditInventoryItem
        public override bool OnEditInventoryItem(IInventory inventory, int slotIndex,
            ComponentPlayer componentPlayer)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.ShouldSuppressRemoteEditableDataEdit(componentPlayer) == true)
                return true;
            if (multiplayer?.CanSubmitEditableDataEdit(componentPlayer) != true)
                return base.OnEditInventoryItem(inventory, slotIndex, componentPlayer);
            int value = inventory.GetSlotValue(slotIndex);
            int data = Terrain.ExtractData(value);
            DialogsManager.ShowDialog(componentPlayer.GuiWidget,
                new EditPistonDialog(data, newData =>
                {
                    multiplayer.TrySubmitEditableItemData(EditableDataKind.Piston,
                        inventory, slotIndex, componentPlayer, value, FormatData(newData));
                }));
            return true;
        }

        // Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:
        // SubsystemPistonBlockBehavior.OnEditBlock
        public override bool OnEditBlock(int x, int y, int z, int value,
            ComponentPlayer componentPlayer)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.ShouldSuppressRemoteEditableDataEdit(componentPlayer) == true)
                return true;
            if (multiplayer?.CanSubmitEditableDataEdit(componentPlayer) != true)
                return base.OnEditBlock(x, y, z, value, componentPlayer);
            int data = Terrain.ExtractData(value);
            DialogsManager.ShowDialog(componentPlayer.GuiWidget,
                new EditPistonDialog(data, newData =>
                {
                    multiplayer.TrySubmitEditableBlockData(EditableDataKind.Piston,
                        new Point3(x, y, z), componentPlayer, value, FormatData(newData));
                }));
            return true;
        }

        private static string FormatData(int data) =>
            data.ToString(CultureInfo.InvariantCulture);
    }
}
