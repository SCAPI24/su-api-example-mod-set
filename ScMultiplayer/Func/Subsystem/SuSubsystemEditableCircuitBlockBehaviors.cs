using Engine;
using Game;
using GameEntitySystem;
using System.Collections;
using System.Globalization;

namespace ScMultiplayer
{
    // Source: Survivalcraft/Game/SubsystemDispenserBlockBehavior.cs:OnInteract
    public sealed class SuSubsystemDispenserBlockBehavior : SubsystemDispenserBlockBehavior
    {
        public override bool OnInteract(TerrainRaycastResult raycastResult,
            ComponentMiner componentMiner)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.CanSubmitEditableDataEdit(componentMiner?.ComponentPlayer) != true)
                return base.OnInteract(raycastResult, componentMiner);
            Project project = GameManager.Project;
            SubsystemGameInfo gameInfo = project?.FindSubsystem<SubsystemGameInfo>(false);
            SubsystemBlockEntities entities = project?.FindSubsystem<SubsystemBlockEntities>(false);
            ComponentPlayer player = componentMiner.ComponentPlayer;
            ComponentBlockEntity blockEntity = entities?.GetBlockEntity(
                raycastResult.CellFace.X, raycastResult.CellFace.Y, raycastResult.CellFace.Z);
            ComponentDispenser dispenser = blockEntity?.Entity.FindComponent<ComponentDispenser>();
            if (gameInfo == null || entities == null || player == null || dispenser == null ||
                gameInfo.WorldSettings.GameMode == GameMode.Adventure)
                return false;
            player.ComponentGui.ModalPanelWidget = new SuDispenserWidget(
                componentMiner.Inventory, dispenser, player);
            AudioManager.PlaySound("Audio/UI/ButtonClick", 1f, 0f, 0f);
            return true;
        }
    }

    // Source: Survivalcraft/Game/DispenserWidget.cs:DispenserWidget.Update
    public sealed class SuDispenserWidget : DispenserWidget
    {
        private readonly ComponentPlayer m_componentPlayer;
        private int? m_pendingData;
        private Point3 m_pendingPoint;
        private double m_pendingSince;

        public SuDispenserWidget(IInventory inventory, ComponentDispenser dispenser,
            ComponentPlayer componentPlayer)
            : base(inventory, dispenser)
        {
            m_componentPlayer = componentPlayer;
        }

        public override void Update()
        {
            ComponentBlockEntity blockEntity = ScMultiplayer.ModManager.ModParentField
                .GetParentField<ComponentBlockEntity>(this, "m_componentBlockEntity",
                    typeof(DispenserWidget));
            SubsystemTerrain terrain = GameManager.Project?
                .FindSubsystem<SubsystemTerrain>(false);
            Point3 point = blockEntity?.Coordinates ?? default;
            if (ScMultiplayer.IsHost || ScMultiplayer.client?.IsConnected != true ||
                terrain == null || blockEntity == null ||
                !terrain.Terrain.IsCellValid(point.X, point.Y, point.Z))
            {
                base.Update();
                return;
            }

            // Source: Survivalcraft/Game/DispenserWidget.cs:DispenserWidget.Update
            // Keep online clients presentation-only until the host confirms the block data.
            ButtonWidget dispenseButton = ScMultiplayer.ModManager.ModParentField
                .GetParentField<ButtonWidget>(this, "m_dispenseButton", typeof(DispenserWidget));
            ButtonWidget shootButton = ScMultiplayer.ModManager.ModParentField
                .GetParentField<ButtonWidget>(this, "m_shootButton", typeof(DispenserWidget));
            CheckboxWidget acceptsDropsBox = ScMultiplayer.ModManager.ModParentField
                .GetParentField<CheckboxWidget>(this, "m_acceptsDropsBox", typeof(DispenserWidget));
            int value = terrain.Terrain.GetCellValue(point.X, point.Y, point.Z);
            int actualData = Terrain.ExtractData(value);
            if (m_pendingData.HasValue && m_pendingPoint == point)
            {
                if (actualData == m_pendingData.Value || Time.RealTime - m_pendingSince > 1.5)
                    m_pendingData = null;
            }
            int displayData = m_pendingData ?? actualData;
            bool changed = false;
            if (dispenseButton?.IsClicked == true)
            {
                displayData = DispenserBlock.SetMode(displayData, DispenserBlock.Mode.Dispense);
                changed = true;
            }
            if (shootButton?.IsClicked == true)
            {
                displayData = DispenserBlock.SetMode(displayData, DispenserBlock.Mode.Shoot);
                changed = true;
            }
            if (acceptsDropsBox?.IsClicked == true)
            {
                displayData = DispenserBlock.SetAcceptsDrops(displayData,
                    !DispenserBlock.GetAcceptsDrops(displayData));
                changed = true;
            }
            if (changed && displayData != actualData &&
                (!m_pendingData.HasValue || m_pendingData.Value != displayData))
            {
                m_pendingData = displayData;
                m_pendingPoint = point;
                m_pendingSince = Time.RealTime;
                ScMultiplayer.currentInstance?.TrySubmitEditableBlockData(
                    EditableDataKind.Dispenser, point, m_componentPlayer, value,
                    displayData.ToString(CultureInfo.InvariantCulture));
            }
            if (dispenseButton != null)
                dispenseButton.IsChecked = DispenserBlock.GetMode(displayData) == DispenserBlock.Mode.Dispense;
            if (shootButton != null)
                shootButton.IsChecked = DispenserBlock.GetMode(displayData) == DispenserBlock.Mode.Shoot;
            if (acceptsDropsBox != null)
                acceptsDropsBox.IsChecked = DispenserBlock.GetAcceptsDrops(displayData);
            if (!m_componentDispenserIsAdded())
                ParentWidget?.Children.Remove(this);
        }

        private bool m_componentDispenserIsAdded()
        {
            ComponentDispenser dispenser = ScMultiplayer.ModManager.ModParentField
                .GetParentField<ComponentDispenser>(this, "m_componentDispenser",
                    typeof(DispenserWidget));
            return dispenser?.IsAddedToProject == true;
        }
    }

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
