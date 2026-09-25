using Engine;
using Game;
using GameEntitySystem;
using System.Collections;
using System.Collections.Generic;
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
            if (ScMultiplayer.currentInstance?.IsNetworkSessionActive(GameManager.Project) != true ||
                ScMultiplayer.currentInstance?.IsNetworkHost(GameManager.Project) == true ||
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

        // 《玩家领地》P7d：**活塞推入他人领地 ⇒ 拒绝**（设计稿 §34.2）。
        //
        // 为什么用"改回式"（源码依据）：
        //   · 活塞的落地写入全在 `SubsystemPistonBlockBehavior` 内部（`ChangeCell` / `DestroyCell`：
        //     366/374/419 行推进、458/467 行 `StopPiston` 提交），这些方法都**不是虚方法**；
        //   · 真正驱动推进的队列是私有 `m_actions`（`QueuedAction`），`StopPiston` 也是私有的，
        //     所以没有"在行为层短路"的虚钩子可用。
        // ⇒ 与火焰（§32）、流体（§37/§39）同一范式：`base.Update(dt)` 之前快照"活塞前方路径"上
        //   落在领地内的格，跑完引擎更新后把这些格改回旧值并走统一否决通知（动作标签 `piston`）。
        //   只快照**前方路径**、不快照活塞自身那一格：活塞自身的数据位（是否伸出）变化不应被判成越权，
        //   否则拥有者自己领地里的活塞会被反复"回滚"而失效。
        private const int MaximumPistonPathCells = 8;

        private readonly Dictionary<Point3, int> m_claimSnapshot = new Dictionary<Point3, int>();
        private readonly List<Point3> m_revertCells = new List<Point3>();
        private readonly List<int> m_revertValues = new List<int>();
        private readonly List<RegionClaim> m_revertClaims = new List<RegionClaim>();

        // Source: Survivalcraft/Game/CellFace.cs —— 面序号到方向的换算（与 GmUiComponent 放置用的
        // switch 一致）：0=z+ / 1=x+ / 2=z- / 3=x- / 4=y+ / 5=y-
        private static Point3 FaceDirection(int face)
        {
            switch (face)
            {
                case 0: return new Point3(0, 0, 1);
                case 1: return new Point3(1, 0, 0);
                case 2: return new Point3(0, 0, -1);
                case 3: return new Point3(-1, 0, 0);
                case 4: return new Point3(0, 1, 0);
                case 5: return new Point3(0, -1, 0);
                default: return new Point3(0, 0, 0);
            }
        }

        private void CapturePistonPath(ScMultiplayer mod)
        {
            m_claimSnapshot.Clear();
            if (mod == null)
                return;
            SubsystemTerrain terrain = Project?.FindSubsystem<SubsystemTerrain>(false);
            if (terrain?.Terrain == null)
                return;
            // Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:
            // SubsystemPistonBlockBehavior.m_actions
            IDictionary actions = ScMultiplayer.ModManager.ModParentField.GetParentField<IDictionary>(
                this, "m_actions", typeof(SubsystemPistonBlockBehavior));
            if (actions == null || actions.Count == 0)
                return;
            foreach (object key in actions.Keys)
            {
                if (!(key is Point3 position))
                    continue;
                // 口径与 P7c 流体一致：**只挡"从领地外推入领地内"**。活塞自身若已在某块领地内，
                // 它的推进属于"领地内自己的机械"⇒ 整台跳过；否则拥有者自己领地里的活塞会被
                // 逐帧回滚而失效（`CanRegionModifyCell(0, …)` 按主机身份判定，必然不是拥有者）。
                if (mod.OwnerClaimAt(position) != null)
                    continue;
                int value = terrain.Terrain.GetCellValue(position.X, position.Y, position.Z);
                Point3 direction = FaceDirection(PistonBlock.GetFace(Terrain.ExtractData(value)));
                if (direction.X == 0 && direction.Y == 0 && direction.Z == 0)
                    continue;
                for (int step = 1; step <= MaximumPistonPathCells; step++)
                {
                    var cell = new Point3(position.X + direction.X * step,
                        position.Y + direction.Y * step, position.Z + direction.Z * step);
                    if (cell.Y < 0 || cell.Y > 255)
                        break;
                    if (m_claimSnapshot.ContainsKey(cell))
                        continue;
                    if (mod.OwnerClaimAt(cell) == null)
                        continue;
                    m_claimSnapshot[cell] = terrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z);
                }
            }
        }

        private void RevertPistonClaimWrites(ScMultiplayer mod)
        {
            if (m_claimSnapshot.Count == 0)
                return;
            SubsystemTerrain terrain = Project?.FindSubsystem<SubsystemTerrain>(false);
            if (mod == null || terrain?.Terrain == null)
            {
                m_claimSnapshot.Clear();
                return;
            }
            m_revertCells.Clear();
            m_revertValues.Clear();
            m_revertClaims.Clear();
            // 先收集再落地：ChangeCell 会触发邻居通知，可能重入（本类 Update 由子系统循环驱动，
            // 不在同一调用栈内，但仍按同一安全顺序处理）。
            foreach (KeyValuePair<Point3, int> item in m_claimSnapshot)
            {
                Point3 cell = item.Key;
                if (terrain.Terrain.GetCellValue(cell.X, cell.Y, cell.Z) == item.Value)
                    continue;
                if (mod.CanRegionModifyCell(0, cell, out RegionClaim claim, out string _))
                    continue;
                m_revertCells.Add(cell);
                m_revertValues.Add(item.Value);
                m_revertClaims.Add(claim);
            }
            m_claimSnapshot.Clear();
            Point3[] cells = m_revertCells.ToArray();
            int[] values = m_revertValues.ToArray();
            RegionClaim[] claims = m_revertClaims.ToArray();
            for (int i = 0; i < cells.Length; i++)
            {
                terrain.ChangeCell(cells[i].X, cells[i].Y, cells[i].Z, values[i]);
                mod.NotifyRegionModificationDenied(0, cells[i], claims[i], "piston", null);
            }
        }

        // Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs:
        // SubsystemPistonBlockBehavior.Update
        void IUpdateable.Update(float dt)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            bool sessionActive = mod?.IsNetworkSessionActive(Project) == true;
            if (sessionActive && mod.IsNetworkHost(Project))
            {
                CapturePistonPath(mod);
                base.Update(dt);
                RevertPistonClaimWrites(mod);
                return;
            }
            if (!sessionActive || mod.IsNetworkHost(Project))
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
            if (ScMultiplayer.currentInstance?.IsNetworkSessionActive(Project) == true &&
                ScMultiplayer.currentInstance?.IsNetworkHost(Project) != true)
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
