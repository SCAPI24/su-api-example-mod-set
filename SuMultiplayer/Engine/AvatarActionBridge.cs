using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Players;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 动作 / 动画状态 / 手持物 / 背包的**引擎适配层**（<see cref="IPlayerActionPort"/> 的实现）。
    ///
    /// 它只做两件事：
    ///   ① 发送端：把本机玩家的引擎事实抓成纯值（<see cref="LocalPlayerSignals"/> / 背包数组）；
    ///   ② 接收端：把模块算出的表现/背包/手持物写进**远端化身**的引擎字段。
    ///
    /// 为什么单独一个类（而不是塞进 NetworkAvatarSpawner）：远端化身表已经在 spawner 里，
    /// 这里只读它（TryGet/ClientIds），不重复维护第二份化身表；动作链要新增/删除时不动建角色流程。
    ///
    /// 旧实现对应处（逐字段照搬）：
    ///   · 发送端：ScMultiplayerPlayerSync.cs:53-135（位置快照里的 PokingPhase / AttackOrder /
    ///     WalkOrder / JumpOrder / HandItem* / ItemOffset / ItemRotation / AimHandAngle）
    ///     + :137-190（ApplyPersistentAimPresentation 的块分类）
    ///   · 接收端：ScMultiplayerMessageHandlers.cs:78-160（强制 PokingPhase / TargetCrouchFactor /
    ///     LookAngles / LastWalkOrder / AttackOrder / ItemOffset / AimHandAngle）
    ///   · 手持物绘制：Survivalcraft/Game/ComponentHumanModel.cs:DrawExtras（读 ComponentMiner.ActiveBlockValue）
    ///   · 受伤闪红：Survivalcraft/Game/ComponentCreatureModel.cs:Animate（m_injuryColorFactor）
    ///   · 着火：Survivalcraft/Game/ComponentOnFire.cs:SetOnFire/Extinguish
    ///
    /// 无静态状态：反射入口 / 诊断 / 传输端口都由组合根以实例注入。
    /// </summary>
    public sealed class AvatarActionBridge : IPlayerActionPort
    {
        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly ITransportPort m_transport;
        private readonly ITransportControlPort m_control;

        /// <summary>clientId → 上一次写进引擎的"着火"状态（只在变化时调 SetOnFire/Extinguish）。</summary>
        private readonly Dictionary<int, bool> m_remoteOnFire = new Dictionary<int, bool>();
        /// <summary>clientId → 是否已经给远端矿工设过挖掘裂纹（结束时必须显式清掉）。</summary>
        private readonly Dictionary<int, bool> m_remoteDig = new Dictionary<int, bool>();
        private readonly HashSet<string> m_reportedOnce = new HashSet<string>(StringComparer.Ordinal);

        private long m_signalsCaptured;
        private long m_inventoryCaptured;
        private long m_inventoryApplied;
        private long m_heldItemsApplied;
        private long m_presentationsApplied;
        private long m_slotDrops;

        /// <summary>
        /// 组合根在构造完 <see cref="PlayerActionModule"/> 后注入：开火复位需要模块持有的
        /// 权威广播 / 手持物缓存，而不是在这里再建一份。
        /// </summary>
        public PlayerActionModule ActionModule { get; set; }

        public AvatarActionBridge(NetworkAvatarSpawner spawner, IModParentField parentField,
            ITransportPort transport, ITransportControlPort control, IModuleDiagnostics diagnostics,
            string ownerId)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_transport = transport;
            m_control = control;
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "player-action" : ownerId;
        }

        // ---------------------------------------------------------------- 身份 / 化身

        /// <summary>
        /// 本机玩家自己的槽位：主机 = <see cref="PlayerActionModule.HostSlotKey"/>（0）；
        /// 客户端 = 握手分配的通道编号（未握手 = -1，此时任何 id 都不是本机）。
        /// </summary>
        public bool IsLocalSlot(int clientId)
        {
            if (m_control != null && m_control.IsHosting) return clientId == PlayerActionModule.HostSlotKey;
            int local = m_transport?.LocalClientId ?? -1;
            return local >= 0 && clientId == local;
        }

        public bool HasAvatar(int clientId) => m_spawner.TryGet(clientId, out _);

        // ---------------------------------------------------------------- 发送端：抓引擎事实

        public bool TryCaptureLocalSignals(out LocalPlayerSignals signals, out string reason)
        {
            signals = default;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进世界）";
                return false;
            }
            if (!m_spawner.TryGetLocalPlayer(project, out PlayerData playerData, out reason))
                return false;
            return TryCaptureSignals(playerData, out signals, out reason);
        }

        /// <summary>抓一个 PlayerData 的引擎事实（本机玩家用；宿主侧远端化身不抓 —— 它们的状态来自网络）。</summary>
        private bool TryCaptureSignals(PlayerData playerData, out LocalPlayerSignals signals,
            out string reason)
        {
            signals = default;
            reason = null;
            ComponentPlayer player = playerData?.ComponentPlayer;
            ComponentMiner miner = player?.ComponentMiner;
            ComponentCreatureModel model = player?.ComponentCreatureModel;
            ComponentLocomotion locomotion = player?.ComponentLocomotion;
            ComponentBody body = player?.ComponentBody;
            if (player == null || body == null)
            {
                reason = "本机玩家还没有 ComponentPlayer/ComponentBody";
                return false;
            }
            if (miner == null)
            {
                reason = "本机玩家还没有 ComponentMiner（背包/挖掘/抬手都取不到）";
                return false;
            }
            if (model == null || locomotion == null)
            {
                reason = "本机玩家还没有 ComponentCreatureModel/ComponentLocomotion（动作状态取不到）";
                return false;
            }

            signals.InWorld = true;
            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.PokingPhase/DigCellFace/DigProgress
            signals.PokingPhase = miner.PokingPhase;
            if (miner.DigCellFace.HasValue)
            {
                CellFace cell = miner.DigCellFace.Value;
                signals.Digging = true;
                signals.DigX = cell.X;
                signals.DigY = cell.Y;
                signals.DigZ = cell.Z;
                signals.DigFace = cell.Face;
                signals.DigProgress = miner.DigProgress;
            }
            // Source: Survivalcraft/Game/ComponentCreatureModel.cs:AttackOrder / RowLeftOrder / RowRightOrder
            signals.Attacking = model.AttackOrder;
            CaptureRowOrders(player, out bool rowLeft, out bool rowRight);
            signals.RowLeft = rowLeft;
            signals.RowRight = rowRight;
            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.m_aim（nullable Ray3）
            CaptureAim(player, out bool aiming, out Vector3 aimOrigin, out Vector3 aimDirection);
            signals.Aiming = aiming;
            signals.AimOrigin = aimOrigin;
            signals.AimDirection = aimDirection;
            // Source: Survivalcraft/Game/ComponentBody.cs:TargetCrouchFactor / StandingOnValue / ImmersionFactor
            signals.Crouching = body.TargetCrouchFactor > 0f;
            signals.Grounded = body.StandingOnValue.HasValue;
            signals.Immersion = body.ImmersionFactor;
            signals.Swimming = body.ImmersionFactor > 0.5f;
            // Source: Survivalcraft/Game/ComponentLocomotion.cs:IsCreativeFlyEnabled / LastWalkOrder / LastJumpOrder
            signals.Flying = locomotion.IsCreativeFlyEnabled;
            if (locomotion.LastWalkOrder.HasValue)
            {
                signals.HasWalkOrder = true;
                signals.WalkOrder = locomotion.LastWalkOrder.Value;
            }
            signals.JumpOrder = locomotion.LastJumpOrder;
            signals.TurnOrder = locomotion.LastTurnOrder;
            // Source: Survivalcraft/Game/ComponentOnFire.cs:ComponentOnFire.IsOnFire
            ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
            signals.OnFire = onFire != null && onFire.IsOnFire;
            // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.HealthChange
            signals.Hurt = player.ComponentHealth != null && player.ComponentHealth.HealthChange < 0f;
            // Source: Survivalcraft/Game/PlayerStats.cs（引擎动作计数器，用来如实分类抬手）
            PlayerStats stats = player.PlayerStats;
            if (stats != null)
            {
                signals.Jumps = stats.Jumps;
                signals.BlocksDug = stats.BlocksDug;
                signals.BlocksPlaced = stats.BlocksPlaced;
                signals.BlocksInteracted = stats.BlocksInteracted;
                signals.MeleeAttacks = stats.MeleeAttacks;
                signals.FoodItemsEaten = stats.FoodItemsEaten;
            }
            IInventory inventory = miner.Inventory;
            signals.ActiveSlotIndex = inventory?.ActiveSlotIndex ?? -1;
            if (inventory != null && signals.ActiveSlotIndex >= 0 &&
                signals.ActiveSlotIndex < inventory.SlotsCount)
            {
                signals.HeldItemValue = inventory.GetSlotValue(signals.ActiveSlotIndex);
                signals.HeldItemCount = inventory.GetSlotCount(signals.ActiveSlotIndex);
            }
            signals.HeldAimKind = ClassifyHeldItem(signals.HeldItemValue);
            signals.Eating = IsFood(signals.HeldItemValue);
            CountCapture("signals");
            return true;
        }

        /// <summary>
        /// 划船状态：ComponentHumanModel.Update 每帧把 RowLeftOrder/RowRightOrder **消费并清零**，
        /// 所以在 Frame.Update 里读订单字段永远是 false；读它上一帧真正用到的 m_rowLeft/m_rowRight。
        /// Source: Survivalcraft/Game/ComponentHumanModel.cs:ComponentHumanModel.Update（m_rowLeft = base.RowLeftOrder; … 清零）
        /// </summary>
        private void CaptureRowOrders(ComponentPlayer player, out bool rowLeft, out bool rowRight)
        {
            rowLeft = false;
            rowRight = false;
            ComponentHumanModel human = player.Entity.FindComponent<ComponentHumanModel>();
            if (human == null) return;
            object left = m_parentField.GetParentField(human, "m_rowLeft", typeof(ComponentHumanModel));
            object right = m_parentField.GetParentField(human, "m_rowRight", typeof(ComponentHumanModel));
            rowLeft = left is bool l && l;
            rowRight = right is bool r && r;
        }

        /// <summary>
        /// 瞄准状态：ComponentPlayer.m_aim 是可空 Ray3；SuAPI 的泛型 getter 不接受 null，
        /// 所以按旧实现的做法读 boxed 值（present 值 box 成 Ray3，空值 box 成 null）。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:146-159
        /// </summary>
        private void CaptureAim(ComponentPlayer player, out bool aiming, out Vector3 origin,
            out Vector3 direction)
        {
            aiming = false;
            origin = Vector3.Zero;
            direction = Vector3.Zero;
            object value = m_parentField.GetParentField(player, "m_aim", typeof(ComponentPlayer));
            if (!(value is Ray3 ray)) return;
            aiming = true;
            origin = ray.Position;
            direction = ray.Direction;
        }

        /// <summary>手持物分类（与旧实现 ApplyPersistentAimPresentation 的块类型判断同序）。</summary>
        private HeldItemAimKind ClassifyHeldItem(int itemValue)
        {
            if (itemValue == 0) return HeldItemAimKind.None;
            int contents = Game.Terrain.ExtractContents(itemValue);
            if (contents < 0 || contents >= BlocksManager.Blocks.Length) return HeldItemAimKind.None;
            Block block = BlocksManager.Blocks[contents];
            if (block is BowBlock) return HeldItemAimKind.Bow;
            if (block is CrossbowBlock) return HeldItemAimKind.Crossbow;
            if (block is MusketBlock) return HeldItemAimKind.Musket;
            if (block is SpearBlock) return HeldItemAimKind.Spear;
            return block.IsAimable ? HeldItemAimKind.Aimable : HeldItemAimKind.None;
        }

        private bool IsFood(int itemValue)
        {
            if (itemValue == 0) return false;
            int contents = Game.Terrain.ExtractContents(itemValue);
            if (contents < 0 || contents >= BlocksManager.Blocks.Length) return false;
            // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Eat（手持 FoodBlock 时使用会进食）
            return BlocksManager.Blocks[contents] is FoodBlock;
        }

        public bool TryCaptureLocalInventory(out int[] values, out int[] counts,
            out int activeSlotIndex, out string reason)
        {
            values = null;
            counts = null;
            activeSlotIndex = -1;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进世界）";
                return false;
            }
            if (!m_spawner.TryGetLocalPlayer(project, out PlayerData localPlayer, out reason))
                return false;
            return TryCaptureInventory(localPlayer, out values, out counts, out activeSlotIndex,
                out reason);
        }

        public bool TryCaptureInventory(int clientId, out int[] values, out int[] counts,
            out int activeSlotIndex, out string reason)
        {
            values = null;
            counts = null;
            activeSlotIndex = -1;
            reason = null;
            if (!m_spawner.TryGet(clientId, out PlayerData playerData) || playerData?.ComponentPlayer == null)
            {
                reason = "本机没有 clientId=" + clientId + " 的联机角色";
                return false;
            }
            return TryCaptureInventory(playerData, out values, out counts, out activeSlotIndex,
                out reason);
        }

        /// <summary>
        /// 抓一个角色的可用背包（前 <see cref="PlayerInventoryMath.MaxSyncSlots"/> 槽）。
        /// 创造仓库有 1622 槽：只同步快捷栏 10 槽（整包下发会重演旧实现"巨大网络尖峰"）。
        /// Source: Survivalcraft/Game/ComponentInventoryBase.cs:IInventory.GetSlotValue/GetSlotCount
        /// </summary>
        private bool TryCaptureInventory(PlayerData playerData, out int[] values, out int[] counts,
            out int activeSlotIndex, out string reason)
        {
            values = null;
            counts = null;
            activeSlotIndex = -1;
            reason = null;
            IInventory inventory = playerData?.ComponentPlayer?.ComponentMiner?.Inventory;
            if (inventory == null)
            {
                reason = "角色还没有 ComponentMiner.Inventory";
                return false;
            }
            int slots = Math.Min(inventory.SlotsCount, PlayerInventoryMath.MaxSyncSlots);
            if (slots <= 0)
            {
                reason = "背包槽数为 0";
                return false;
            }
            values = new int[slots];
            counts = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                values[i] = inventory.GetSlotValue(i);
                counts[i] = inventory.GetSlotCount(i);
            }
            activeSlotIndex = inventory.ActiveSlotIndex;
            CountCapture("inventory");
            return true;
        }

        // ---------------------------------------------------------------- 接收端：写进远端化身

        public bool TryApplyHeldItem(int clientId, in PlayerHeldItemSnapshot item, out string reason)
        {
            reason = null;
            IInventory inventory = ResolveInventory(clientId, out reason);
            if (inventory == null) return false;
            int slot = item.SlotIndex >= 0 ? item.SlotIndex : item.ActiveSlotIndex;
            if (slot < 0 || slot >= inventory.SlotsCount)
            {
                reason = "手持物槽位越界：" + slot + "（背包 " + inventory.SlotsCount + " 槽）";
                return false;
            }
            if (item.ActiveSlotIndex >= 0 && item.ActiveSlotIndex < inventory.SlotsCount)
                inventory.ActiveSlotIndex = item.ActiveSlotIndex;
            int value = item.HasItem ? item.ItemValue : 0;
            int count = item.HasItem ? item.ItemCount : 0;
            if (!SetSlot(inventory, slot, value, count, clientId, out reason)) return false;
            m_heldItemsApplied++;
            return true;
        }

        /// <summary>
        /// 主机：开火后从权威背包回读并广播手持物复位（转发给 PlayerActionModule）。
        /// 未注入模块 = 明确失败（计数 / 日志由调用方写），绝不假装已广播。
        /// </summary>
        public bool TryBroadcastHeldItem(int clientId, HeldItemAimKind aimKind, out string reason)
        {
            PlayerActionModule module = ActionModule;
            if (module == null)
            {
                reason = "no-action-module";
                return false;
            }
            return module.TryBroadcastHeldItem(clientId, aimKind, out reason);
        }

        public bool TryApplyInventory(int clientId, in PlayerInventoryDelta delta, out string reason)
        {
            reason = null;
            if (delta == null)
            {
                reason = "背包变更为空";
                return false;
            }
            IInventory inventory = ResolveInventory(clientId, out reason);
            if (inventory == null) return false;
            for (int i = 0; i < delta.Indices.Length; i++)
            {
                int index = delta.Indices[i];
                if (index < 0 || index >= inventory.SlotsCount)
                {
                    m_slotDrops++;
                    LogOnce("inventory-slot-range:" + clientId + ":" + index,
                        "clientId=" + clientId + " 的背包槽位 " + index + " 越界（本端 " +
                        inventory.SlotsCount + " 槽）→ 跳过该槽（其余槽照常落地）");
                    continue;
                }
                if (!SetSlot(inventory, index, delta.Values[i], delta.Counts[i], clientId, out reason))
                    return false;
            }
            if (delta.ActiveSlotIndex >= 0 && delta.ActiveSlotIndex < inventory.SlotsCount)
                inventory.ActiveSlotIndex = delta.ActiveSlotIndex;
            m_inventoryApplied++;
            return true;
        }

        /// <summary>
        /// 直接写一个槽（权威落地）：走引擎的 Remove/Add（会跑容量校验），
        /// 容量不足时按容量截断并限流记录（随后主机回读权威值广播，客户端不会留下超量物品）。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:1657-1705（ApplyInventory）
        /// </summary>
        private bool SetSlot(IInventory inventory, int slot, int value, int count, int clientId,
            out string reason)
        {
            reason = null;
            if (count < 0 || (count > 0 && value == 0))
            {
                reason = "槽位 " + slot + " 的值非法（value=" + value + " count=" + count + "）";
                return false;
            }
            if (count == 0) value = 0;
            int capacity = 0;
            if (count > 0)
            {
                try
                {
                    capacity = inventory.GetSlotCapacity(slot, value);
                }
                catch (Exception e)
                {
                    reason = "槽位 " + slot + " 的容量查询失败：" + e.GetType().Name + " " + e.Message;
                    return false;
                }
                if (capacity <= 0)
                {
                    m_slotDrops++;
                    LogOnce("inventory-slot-capacity:" + clientId + ":" + slot,
                        "clientId=" + clientId + " 的槽位 " + slot + " 不接受物品（capacity=0）→ 跳过该槽" +
                        "（例如远端化身的保留槽 7-9；主机回读权威值后不会出现假物品）");
                    return true;
                }
                if (count > capacity)
                {
                    m_slotDrops++;
                    WarnOnce("inventory-slot-clamp:" + clientId + ":" + slot,
                        "clientId=" + clientId + " 的槽位 " + slot + " 数量 " + count +
                        " 超过容量 " + capacity + " → 按容量落地（随后回读权威值广播）");
                    count = capacity;
                }
            }
            if (inventory.GetSlotValue(slot) == value && inventory.GetSlotCount(slot) == count)
                return true;
            inventory.RemoveSlotItems(slot, int.MaxValue);
            if (count > 0) inventory.AddSlotItems(slot, value, count);
            return true;
        }

        private IInventory ResolveInventory(int clientId, out string reason)
        {
            reason = null;
            if (IsLocalSlot(clientId))
            {
                Project project = GameManager.Project;
                if (project == null)
                {
                    reason = "没有加载中的 Project（本机玩家背包不可用）";
                    return null;
                }
                if (!m_spawner.TryGetLocalPlayer(project, out PlayerData localPlayer, out reason))
                    return null;
                IInventory localInventory = localPlayer?.ComponentPlayer?.ComponentMiner?.Inventory;
                if (localInventory == null) reason = "本机玩家还没有 ComponentMiner.Inventory";
                return localInventory;
            }
            if (!m_spawner.TryGet(clientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer == null)
            {
                reason = "本机没有 clientId=" + clientId + " 的联机角色";
                return null;
            }
            IInventory inventory = playerData.ComponentPlayer.ComponentMiner?.Inventory;
            if (inventory == null) reason = "clientId=" + clientId + " 的化身还没有 ComponentMiner.Inventory";
            return inventory;
        }

        public bool TryApplyPresentation(int clientId, in RemoteAvatarPresentation presentation,
            out string reason)
        {
            reason = null;
            if (!m_spawner.TryGet(clientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer == null)
            {
                reason = "本机没有 clientId=" + clientId + " 的联机角色";
                return false;
            }
            ComponentPlayer player = playerData.ComponentPlayer;
            ComponentBody body = player.ComponentBody;
            ComponentCreatureModel model = player.ComponentCreatureModel;
            if (body == null || model == null)
            {
                reason = "clientId=" + clientId + " 的化身还没有 ComponentBody/ComponentCreatureModel";
                return false;
            }
            ComponentMiner miner = player.ComponentMiner;
            if (miner != null)
            {
                // Human arm animation reads PokingPhase directly（旧实现同样写 backing field）。
                m_parentField.ModifyParentField(miner, "<PokingPhase>k__BackingField",
                    MathUtils.Clamp(presentation.PokingPhase, 0f, 1f), typeof(ComponentMiner));
            }
            model.AttackOrder = presentation.AttackOrder;
            model.AimHandAngleOrder = presentation.AimHandAngle;
            model.InHandItemOffsetOrder = presentation.InHandItemOffset;
            model.InHandItemRotationOrder = presentation.InHandItemRotation;
            model.RowLeftOrder = presentation.RowLeft;
            model.RowRightOrder = presentation.RowRight;
            body.TargetCrouchFactor = presentation.Crouching ? 1f : 0f;
            ComponentLocomotion locomotion = player.ComponentLocomotion;
            if (locomotion != null) locomotion.IsCreativeFlyEnabled = presentation.Flying;
            if (presentation.HurtFlash)
            {
                // 受伤闪红：引擎在 HealthChange < 0 时把 m_injuryColorFactor 置 1（远端化身的生命
                // 变化不经过本机引擎），这里按"受伤"状态位强制一次，随后由引擎自己按 3/s 衰减。
                // Source: Survivalcraft/Game/ComponentCreatureModel.cs:ComponentCreatureModel.Update
                m_parentField.ModifyParentField(model, "m_injuryColorFactor", 1f,
                    typeof(ComponentCreatureModel));
            }
            ApplyOnFire(clientId, player, presentation.OnFire);
            ApplyDigPresentation(clientId, miner, presentation);
            m_presentationsApplied++;
            return true;
        }

        /// <summary>
        /// 挖掘裂纹表现（旧实现 DigPresentationMessage 的接收侧）：
        /// 把远端矿工的 DigCellFace / m_digProgress / m_digStartTime / m_lastDigFrameIndex 写成
        /// 发送方的权威值，引擎的 ComponentDiggingCracks 就会自己画裂纹与进度。
        /// 不给远端化身"模拟挖掘"：这些字段只用于绘制，方块变化仍只来自主机的权威编辑链。
        /// Source: Mod/ScMultiplayer/Modules/Terrain/ScMultiplayerTerrainHandlers.cs:1282-1352
        /// Source: Survivalcraft/Game/ComponentDiggingCracks.cs:Draw（DigCellFace/DigProgress/DigTime）
        /// Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Update
        ///         （DigCellFace 超过 1 帧没刷新就清空 —— 所以必须同时刷新 m_lastDigFrameIndex）
        /// </summary>
        private void ApplyDigPresentation(int clientId, ComponentMiner miner,
            in RemoteAvatarPresentation presentation)
        {
            if (miner == null) return;
            bool hadDig = m_remoteDig.TryGetValue(clientId, out bool value) && value;
            if (!presentation.HasDig)
            {
                if (!hadDig) return;
                m_parentField.ModifyParentField(miner, "<DigCellFace>k__BackingField", null,
                    typeof(ComponentMiner));
                m_parentField.ModifyParentField(miner, "m_digProgress", 0f, typeof(ComponentMiner));
                m_remoteDig[clientId] = false;
                return;
            }
            m_parentField.ModifyParentField(miner, "<DigCellFace>k__BackingField",
                new CellFace(presentation.DigX, presentation.DigY, presentation.DigZ,
                    presentation.DigFace), typeof(ComponentMiner));
            m_parentField.ModifyParentField(miner, "m_digProgress",
                MathUtils.Clamp(presentation.DigProgress, 0f, 1f), typeof(ComponentMiner));
            // DigTime > 0.2 才画裂纹（旧实现同样以"已开始挖"为门槛）：保持一个稳定的已挖时长。
            // 游戏时钟在 SubsystemTime.GameTime（Engine.Time 没有 GameTime），取不到就明确记录。
            SubsystemTime clock = miner.Project?.FindSubsystem<SubsystemTime>(false);
            if (clock == null)
            {
                LogOnce("dig-no-clock",
                    "找不到 SubsystemTime → 远端化身的挖掘碎裂纹理不显示（DigTime 取不到；" +
                    "其余动作表现不受影响）");
            }
            else
            {
                m_parentField.ModifyParentField(miner, "m_digStartTime", clock.GameTime - 1.0,
                    typeof(ComponentMiner));
            }
            m_parentField.ModifyParentField(miner, "m_lastDigFrameIndex", Time.FrameIndex,
                typeof(ComponentMiner));
            m_remoteDig[clientId] = true;
        }

        private void ApplyOnFire(int clientId, ComponentPlayer player, bool onFire)
        {
            ComponentOnFire component = player.Entity.FindComponent<ComponentOnFire>();
            if (component == null) return;
            m_remoteOnFire.TryGetValue(clientId, out bool previous);
            if (onFire)
            {
                // 持续燃烧：每帧按 1 秒续上（引擎每帧 -dt；SetOnFire 是 Max 语义，不会叠加伤害）。
                component.SetOnFire(null, 1f);
            }
            else if (previous)
            {
                component.Extinguish();
                m_remoteOnFire[clientId] = false;
                return;
            }
            if (onFire != previous) m_remoteOnFire[clientId] = onFire;
        }

        // ---------------------------------------------------------------- 诊断

        public string Describe()
        {
            return "bridge(signals=" + m_signalsCaptured.ToString(CultureInfo.InvariantCulture) +
                " inventoryCaptured=" + m_inventoryCaptured.ToString(CultureInfo.InvariantCulture) +
                " inventoryApplied=" + m_inventoryApplied.ToString(CultureInfo.InvariantCulture) +
                " heldItemsApplied=" + m_heldItemsApplied.ToString(CultureInfo.InvariantCulture) +
                " presentations=" + m_presentationsApplied.ToString(CultureInfo.InvariantCulture) +
                " slotDrops=" + m_slotDrops.ToString(CultureInfo.InvariantCulture) + ")";
        }

        private void CountCapture(string kind)
        {
            if (kind == "signals") m_signalsCaptured++;
            else m_inventoryCaptured++;
        }

        private void LogOnce(string key, string message)
        {
            if (!m_reportedOnce.Add(key)) return;
            m_diagnostics?.Info(m_ownerId, message);
        }

        private void WarnOnce(string key, string message)
        {
            if (!m_reportedOnce.Add(key)) return;
            m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}
