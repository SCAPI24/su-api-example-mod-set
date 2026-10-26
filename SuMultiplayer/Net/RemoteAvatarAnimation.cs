using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 本机玩家一帧的**引擎事实**（发送端的输入）。引擎侧（Engine/AvatarActionBridge.cs）填它，
    /// 纯逻辑（<see cref="PlayerActionDetector"/>）据此判定动作边沿与动画状态位。
    ///
    /// Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.PokingPhase/DigCellFace/DigProgress
    /// Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.m_aim（nullable Ray3）
    /// Source: Survivalcraft/Game/ComponentCreatureModel.cs:AttackOrder / RowLeftOrder / RowRightOrder
    /// Source: Survivalcraft/Game/ComponentLocomotion.cs:JumpOrder / TurnOrder / LastJumpOrder
    /// Source: Survivalcraft/Game/ComponentBody.cs:ComponentBody.ImmersionFactor / StandingOnValue
    /// Source: Survivalcraft/Game/ComponentOnFire.cs:ComponentOnFire.IsOnFire
    /// Source: Survivalcraft/Game/PlayerStats.cs（Jumps / BlocksDug / BlocksPlaced / BlocksInteracted /
    ///         MeleeAttacks / FoodItemsEaten —— 引擎自己的动作计数器，用来**如实分类**抬手种类）
    ///
    /// 用可写 struct（不是 class）是为了让检测器能**按值保存上一帧**，不产生每帧分配。
    /// </summary>
    public struct LocalPlayerSignals
    {
        /// <summary>本帧骑在坐骑上（ComponentRider.Mount != null；乘骑复制用）。</summary>
        public bool Riding;
        /// <summary>骑乘时的横向输入（马 TurnOrder = playerInput.Move.X）。</summary>
        public float RideTurn;
        /// <summary>骑乘时的纵向输入（船 MoveOrder / 马 SpeedOrder 档 = playerInput.Move.Z）。</summary>
        public float RideForward;
        /// <summary>骑乘时的跳跃输入（马 JumpOrder）。</summary>
        public bool RideJump;
        public bool InWorld;
        public float PokingPhase;
        public bool Digging;
        public int DigX;
        public int DigY;
        public int DigZ;
        public int DigFace;
        public float DigProgress;
        public bool Attacking;
        public bool Aiming;
        public Vector3 AimOrigin;
        public Vector3 AimDirection;
        public bool Crouching;
        public bool Flying;
        public bool Grounded;
        public bool OnFire;
        public bool Hurt;
        public bool Swimming;
        public bool HasWalkOrder;
        public Vector2 WalkOrder;
        public float JumpOrder;
        public Vector2 TurnOrder;
        public bool RowLeft;
        public bool RowRight;
        public bool Eating;
        public HeldItemAimKind HeldAimKind;
        public int ActiveSlotIndex;
        public int HeldItemValue;
        public int HeldItemCount;
        public float Immersion;
        public long Jumps;
        public long BlocksDug;
        public long BlocksPlaced;
        public long BlocksInteracted;
        public long MeleeAttacks;
        public long FoodItemsEaten;
    }

    /// <summary>
    /// 发送端的动作边沿判定（纯逻辑，可离线自检）。
    ///
    /// 判据逐条对照旧实现：
    ///   · 抬手边沿 = <c>PokingPhase &gt; 0 &amp;&amp; (上一帧 &lt;= 0 或 相位回绕)</c>
    ///     Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:222-238
    ///             （BroadcastPlayerPokeIfStarted：phase &gt; 0 且 (previousPhase &lt;= 0 或 phase + 0.05 &lt; previous)）
    ///   · 挖掘 = ComponentMiner.DigCellFace 有值（带格子/面/进度）
    ///     Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.DigCellFace/DigProgress
    ///   · 瞄准 Start/Cancel/Release = ComponentPlayer.m_aim 的边沿
    ///     Source: Mod/ScMultiplayer/Message/PlayerAimMessage.cs:PlayerAimAction
    ///   · 动作种类用**引擎统计计数器**的增量区分（MeleeAttacks / FoodItemsEaten / BlocksPlaced /
    ///     BlocksInteracted / Jumps）：这些计数器只在对应动作里自增，比"看手持物猜"可靠。
    ///   · 一帧最多产生一条动作（"coalesce per tick"），优先级：挖掘 &gt; 近战 &gt; 进食 &gt; 放置 &gt;
    ///     交互 &gt; 通用抬手 &gt; 瞄准释放/开始 &gt; 跳跃。
    /// </summary>
    public sealed class PlayerActionDetector
    {
        /// <summary>相位回绕判定阈值（旧实现 0.05）。Source: ScMultiplayerPlayerSync.cs:229</summary>
        public const float PokeRestartThreshold = 0.05f;

        /// <summary>
        /// 挖掘表现的刷新间隔（旧实现 UpdateLocalDigPresentation 的 0.125 秒）：
        /// 32Hz 脉冲下 = 每 4 拍一次，碎裂纹理/进度据此推进。
        /// Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:1692-1706
        /// </summary>
        public const int DigPresentationIntervalTicks = 4;

        private LocalPlayerSignals m_previous;
        private bool m_hasPrevious;
        private int m_sequence;
        private int m_lastDigPresentationTick = int.MinValue;

        /// <summary>已经产生的动作数（状态行/诊断）。</summary>
        public long ActionsDetected { get; private set; }

        public int Sequence => m_sequence;

        public bool HasPrevious => m_hasPrevious;

        /// <summary>换世界 / 重建本机角色后复位基线（否则会把"新角色的第一次抬手"误判成回绕）。</summary>
        public void Reset()
        {
            m_previous = default;
            m_hasPrevious = false;
            m_sequence = 0;
            m_lastDigPresentationTick = int.MinValue;
        }

        /// <summary>
        /// 观察一帧。返回 true = 这一帧有动作边沿（<paramref name="action"/> 有效）。
        /// 第一次观察只建立基线（不产生动作），避免刚进世界就播一次抬手。
        /// </summary>
        public bool TryDetect(in LocalPlayerSignals current, int serverTick,
            out PlayerActionSnapshot action)
        {
            action = default;
            if (!current.InWorld)
            {
                m_hasPrevious = false;   // 离开世界：基线作废
                return false;
            }
            LocalPlayerSignals previous = m_previous;
            bool hadPrevious = m_hasPrevious;
            m_previous = current;
            m_hasPrevious = true;
            if (!hadPrevious) return false;

            bool pokeStarted = current.PokingPhase > 0f &&
                (previous.PokingPhase <= 0f ||
                 current.PokingPhase + PokeRestartThreshold < previous.PokingPhase);
            bool digStarted = current.Digging && !previous.Digging;
            bool digCellChanged = current.Digging && previous.Digging &&
                (current.DigX != previous.DigX || current.DigY != previous.DigY ||
                 current.DigZ != previous.DigZ || current.DigFace != previous.DigFace);
            bool aimStarted = current.Aiming && !previous.Aiming;
            bool aimStopped = !current.Aiming && previous.Aiming;
            bool digRefreshDue = current.Digging &&
                (m_lastDigPresentationTick == int.MinValue ||
                 serverTick - m_lastDigPresentationTick >= DigPresentationIntervalTicks);
            bool jumped = current.Jumps > previous.Jumps ||
                (current.JumpOrder > 0.5f && previous.JumpOrder <= 0.5f);

            PlayerActionKind kind = PlayerActionKind.Unspecified;
            bool sustained = false;
            if (current.Digging && (digStarted || digCellChanged || pokeStarted || digRefreshDue))
            {
                kind = PlayerActionKind.Dig;
                sustained = true;
                m_lastDigPresentationTick = serverTick;
            }
            else if (current.MeleeAttacks > previous.MeleeAttacks)
            {
                kind = PlayerActionKind.Hit;
            }
            else if (current.FoodItemsEaten > previous.FoodItemsEaten)
            {
                kind = PlayerActionKind.Eat;
            }
            else if (current.BlocksPlaced > previous.BlocksPlaced)
            {
                kind = PlayerActionKind.Place;
            }
            else if (current.BlocksInteracted > previous.BlocksInteracted)
            {
                kind = PlayerActionKind.Use;
            }
            else if (pokeStarted)
            {
                kind = PlayerActionKind.Use;
            }
            else if (aimStopped)
            {
                kind = current.HeldAimKind != HeldItemAimKind.None
                    ? PlayerActionKind.Throw
                    : PlayerActionKind.AimRelease;
            }
            else if (aimStarted)
            {
                kind = PlayerActionKind.AimStart;
            }
            else if (jumped)
            {
                kind = PlayerActionKind.Jump;
            }
            if (kind == PlayerActionKind.Unspecified) return false;

            m_sequence = m_sequence == int.MaxValue ? 1 : m_sequence + 1;
            ActionsDetected++;
            action = new PlayerActionSnapshot(m_sequence, serverTick, kind, current.PokingPhase,
                sustained, current.ActiveSlotIndex, current.HeldItemValue, current.HeldItemCount,
                current.Aiming, current.AimOrigin, current.AimDirection,
                current.Digging, current.DigX, current.DigY, current.DigZ, current.DigFace,
                current.DigProgress);
            return true;
        }

        /// <summary>
        /// 由同一份引擎事实构造动画状态（纯函数）。
        /// 状态位口径逐条对照引擎（见 <see cref="PlayerMotionFlags"/> 的注释）。
        /// </summary>
        public static PlayerMotionSnapshot BuildMotion(in LocalPlayerSignals signals, int serverTick)
        {
            PlayerMotionFlags flags = PlayerMotionFlags.None;
            float walkOrderSquared = signals.WalkOrder.LengthSquared();
            bool walking = signals.HasWalkOrder && walkOrderSquared > 0.01f && signals.Grounded;
            if (walking) flags |= PlayerMotionFlags.Walking;
            if (walking && !signals.Crouching && walkOrderSquared > 0.99f)
            {
                // 引擎没有独立的 run 状态（旧实现也没有）：这里用"满移动输入且未蹲下"作为
                // "跑"的判据，只影响状态位/诊断，不改变任何骨骼或物理（见报告"表现边界"）。
                flags |= PlayerMotionFlags.Running;
            }
            if (signals.JumpOrder > 0.5f) flags |= PlayerMotionFlags.Jumping;
            if (signals.Swimming) flags |= PlayerMotionFlags.Swimming;
            if (signals.Flying) flags |= PlayerMotionFlags.Flying;
            if (signals.Crouching) flags |= PlayerMotionFlags.Crouching;
            if (signals.Grounded) flags |= PlayerMotionFlags.Grounded;
            if (signals.OnFire) flags |= PlayerMotionFlags.OnFire;
            if (signals.Hurt) flags |= PlayerMotionFlags.Hurt;
            if (signals.Aiming) flags |= PlayerMotionFlags.Aiming;
            if (signals.Attacking) flags |= PlayerMotionFlags.Attacking;
            if (signals.Digging) flags |= PlayerMotionFlags.Digging;
            if (signals.PokingPhase > 0f) flags |= PlayerMotionFlags.Using;
            if (signals.Eating && signals.PokingPhase > 0f) flags |= PlayerMotionFlags.Eating;
            if (signals.RowLeft) flags |= PlayerMotionFlags.RowLeft;
            if (signals.RowRight) flags |= PlayerMotionFlags.RowRight;
            if (signals.Riding) flags |= PlayerMotionFlags.Riding;
            if (signals.Riding)
            {
                // 骑乘时引擎不写 ComponentLocomotion 的 WalkOrder/TurnOrder（输入直接进
                // Steed/Boat Order，见 ComponentPlayer.cs:126-133），motion 的常规字段全是 0；
                // 主机 RidingInputResolver 读的正是 TurnOrder.X/WalkOrder.Y/JumpOrder，
                // 这里把骑乘输入映射进去（2026-10-26 客户端骑马无法控制修复）。
                return new PlayerMotionSnapshot(serverTick, flags, hasWalkOrder: true,
                    new Vector2(0f, signals.RideForward), signals.RideJump ? 1f : 0f,
                    new Vector2(signals.RideTurn, 0f), signals.Immersion, signals.ActiveSlotIndex);
            }
            return new PlayerMotionSnapshot(serverTick, flags, signals.HasWalkOrder, signals.WalkOrder,
                signals.JumpOrder, signals.TurnOrder, signals.Immersion, signals.ActiveSlotIndex);
        }
    }

    /// <summary>
    /// 接收端要写进远端化身的表现值（纯值）。由 <see cref="RemoteAnimationAdapter"/> 计算，
    /// 引擎侧（Engine/AvatarActionBridge.cs）每帧按它强制字段。
    ///
    /// 与引擎字段的对应：
    ///   PokingPhase        → ComponentMiner.&lt;PokingPhase&gt;k__BackingField（抬手/挖掘/攻击/使用动画）
    ///   AttackOrder        → ComponentCreatureModel.AttackOrder（少数由 AI 驱动的情形）
    ///   AimHandAngle       → ComponentCreatureModel.AimHandAngleOrder
    ///   InHandItemOffset   → ComponentCreatureModel.InHandItemOffsetOrder
    ///   InHandItemRotation → ComponentCreatureModel.InHandItemRotationOrder
    ///   RowLeft/RowRight   → ComponentCreatureModel.RowLeftOrder/RowRightOrder（划船）
    ///   Crouching          → ComponentBody.TargetCrouchFactor
    ///   Flying             → ComponentLocomotion.IsCreativeFlyEnabled
    ///   HurtFlash          → ComponentCreatureModel.m_injuryColorFactor（受伤闪红）
    ///   OnFire             → ComponentOnFire.SetOnFire/Extinguish（火粒子）
    /// Source: Survivalcraft/Game/ComponentHumanModel.cs:ComponentHumanModel.Update
    /// Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:78-155
    /// </summary>
    public readonly struct RemoteAvatarPresentation
    {
        public RemoteAvatarPresentation(float pokingPhase, bool attackOrder, bool aiming,
            float aimHandAngle, Vector3 inHandItemOffset, Vector3 inHandItemRotation,
            bool rowLeft, bool rowRight, bool crouching, bool flying, bool hurtFlash, bool onFire,
            int activeSlotIndex, bool hasDig, int digX, int digY, int digZ, int digFace,
            float digProgress)
        {
            PokingPhase = pokingPhase;
            AttackOrder = attackOrder;
            Aiming = aiming;
            AimHandAngle = aimHandAngle;
            InHandItemOffset = inHandItemOffset;
            InHandItemRotation = inHandItemRotation;
            RowLeft = rowLeft;
            RowRight = rowRight;
            Crouching = crouching;
            Flying = flying;
            HurtFlash = hurtFlash;
            OnFire = onFire;
            ActiveSlotIndex = activeSlotIndex;
            HasDig = hasDig;
            DigX = digX;
            DigY = digY;
            DigZ = digZ;
            DigFace = digFace;
            DigProgress = digProgress;
        }

        public float PokingPhase { get; }
        public bool AttackOrder { get; }
        public bool Aiming { get; }
        public float AimHandAngle { get; }
        public Vector3 InHandItemOffset { get; }
        public Vector3 InHandItemRotation { get; }
        public bool RowLeft { get; }
        public bool RowRight { get; }
        public bool Crouching { get; }
        public bool Flying { get; }
        public bool HurtFlash { get; }
        public bool OnFire { get; }
        public int ActiveSlotIndex { get; }
        /// <summary>正在挖这一格 → 远端矿工带 DigCellFace/DigProgress，引擎自己画碎裂纹理。</summary>
        public bool HasDig { get; }
        public int DigX { get; }
        public int DigY { get; }
        public int DigZ { get; }
        public int DigFace { get; }
        public float DigProgress { get; }
    }

    /// <summary>
    /// 瞄准时的手部姿势（逐行照搬旧实现 ApplyPersistentAimPresentation）。
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:137-190
    /// </summary>
    public static class AimPresentation
    {
        public static void Compute(bool aiming, HeldItemAimKind kind, out float aimHandAngle,
            out Vector3 inHandItemOffset, out Vector3 inHandItemRotation)
        {
            aimHandAngle = 0f;
            inHandItemOffset = Vector3.Zero;
            inHandItemRotation = Vector3.Zero;
            if (!aiming || kind == HeldItemAimKind.None) return;
            switch (kind)
            {
                case HeldItemAimKind.Bow:
                    aimHandAngle = 1.2f;
                    inHandItemOffset = Vector3.Zero;
                    inHandItemRotation = new Vector3(0f, -0.2f, 0f);
                    break;
                case HeldItemAimKind.Crossbow:
                    aimHandAngle = 1.3f;
                    inHandItemOffset = new Vector3(-0.08f, -0.1f, 0.07f);
                    inHandItemRotation = new Vector3(-1.55f, 0f, 0f);
                    break;
                case HeldItemAimKind.Musket:
                    aimHandAngle = 1.4f;
                    inHandItemOffset = new Vector3(-0.08f, -0.08f, 0.07f);
                    inHandItemRotation = new Vector3(-1.7f, 0f, 0f);
                    break;
                case HeldItemAimKind.Spear:
                    aimHandAngle = 3.2f;
                    inHandItemOffset = new Vector3(0f, -0.25f, 0f);
                    inHandItemRotation = new Vector3(3.14159f, 0f, 0f);
                    break;
                case HeldItemAimKind.Aimable:
                    aimHandAngle = 3.2f;
                    break;
            }
        }
    }

    /// <summary>
    /// 远端化身的动画状态机（纯逻辑，无引擎依赖）：收动作边沿 / 动画状态 / 手持物，
    /// 每帧推进抬手相位，产出 <see cref="RemoteAvatarPresentation"/>。
    ///
    /// 相位推进逐条对照引擎：
    ///   ComponentMiner.Update: <c>PokingPhase += num * GameTimeDelta; if (&gt; 1) 挖掘中取余, 否则归零</c>，
    ///   生存模式 <c>num = 4f</c>（Source: Survivalcraft/Game/ComponentMiner.cs:566-582）。
    ///   两端都由本状态机推进（旧实现主机侧让引擎自己推进、客户端侧强制写相位；
    ///   统一到这里后两端不会漂移，见报告"与原实现的差异"）。
    /// </summary>
    public sealed class RemoteAnimationAdapter
    {
        /// <summary>引擎的抬手相位速度（生存模式 4/s）。Source: Survivalcraft/Game/ComponentMiner.cs:568</summary>
        public const float SwingPhasePerSecond = 4f;

        /// <summary>受伤闪红持续时间（秒）：引擎的 m_injuryColorFactor 以 3/s 衰减。</summary>
        public const float HurtFlashSeconds = 0.5f;

        private sealed class Entry
        {
            public int ClientId;
            public int LastActionSequence;
            public PlayerActionKind LastActionKind;
            public float LastActionPhase;
            public long ActionsApplied;
            public bool HasMotion;
            public PlayerMotionSnapshot Motion;
            public bool HasHeldItem;
            public PlayerHeldItemSnapshot HeldItem;
            public float SwingPhase;
            public bool HasDig;
            public int DigX;
            public int DigY;
            public int DigZ;
            public int DigFace;
            public float DigProgress;
            public double HurtUntil;
            public double LastUpdateSeconds;
        }

        private readonly Dictionary<int, Entry> m_entries = new Dictionary<int, Entry>();

        /// <summary>当前有动画状态的远端化身数。</summary>
        public int Count => m_entries.Count;

        public IEnumerable<int> ClientIds => m_entries.Keys;

        /// <summary>累计应用的动作数 / 扔掉的动作数（诊断与状态行）。</summary>
        public long ActionsApplied { get; private set; }
        public long ActionsRejected { get; private set; }
        public long MotionApplied { get; private set; }
        public long HeldItemsApplied { get; private set; }

        public void Clear()
        {
            m_entries.Clear();
        }

        public bool Remove(int clientId) => m_entries.Remove(clientId);

        private Entry GetOrCreate(int clientId)
        {
            if (!m_entries.TryGetValue(clientId, out Entry entry))
            {
                entry = new Entry { ClientId = clientId };
                m_entries[clientId] = entry;
            }
            return entry;
        }

        /// <summary>
        /// 应用一条动作。返回 false + <paramref name="reason"/> = 丢弃（迟到/重复/非法），
        /// 调用方必须计数并限流写日志（不静默）。
        /// </summary>
        public bool ApplyAction(int clientId, in PlayerActionSnapshot action, double nowSeconds,
            out string reason)
        {
            reason = null;
            if (!action.HasAction)
            {
                reason = "动作种类未指定";
                ActionsRejected++;
                return false;
            }
            if (action.Sequence <= 0)
            {
                reason = "动作序号非法：" + action.Sequence;
                ActionsRejected++;
                return false;
            }
            Entry entry = GetOrCreate(clientId);
            if (action.Sequence <= entry.LastActionSequence)
            {
                reason = "动作序号不新（收到 " + action.Sequence + "，已应用 " +
                    entry.LastActionSequence + "）";
                ActionsRejected++;
                return false;
            }
            entry.LastActionSequence = action.Sequence;
            entry.LastActionKind = action.Kind;
            entry.LastActionPhase = action.SwingPhase;
            entry.LastUpdateSeconds = nowSeconds;
            switch (action.Kind)
            {
                case PlayerActionKind.Dig:
                    entry.HasDig = action.HasCell;
                    entry.DigX = action.CellX;
                    entry.DigY = action.CellY;
                    entry.DigZ = action.CellZ;
                    entry.DigFace = action.CellFace;
                    entry.DigProgress = MathUtils.Clamp(action.DigProgress, 0f, 1f);
                    entry.SwingPhase = action.SwingPhase > 0f
                        ? MathUtils.Clamp(action.SwingPhase, 0f, 1f)
                        : 0.0001f;
                    break;
                case PlayerActionKind.Place:
                case PlayerActionKind.Use:
                case PlayerActionKind.Eat:
                case PlayerActionKind.Hit:
                    // 抬手类动作：把相位接到发送端当时的相位（0 视为"从头开始一次完整抬手"）。
                    entry.SwingPhase = action.SwingPhase > 0f
                        ? MathUtils.Clamp(action.SwingPhase, 0f, 1f)
                        : 0.0001f;
                    break;
                default:
                    // 瞄准 / 跳跃 不改手臂相位（瞄准姿势由 Aiming 状态位驱动）。
                    break;
            }
            entry.ActionsApplied++;
            ActionsApplied++;
            return true;
        }

        /// <summary>应用动画状态（持续状态；值相同也接受，幂等）。</summary>
        public bool ApplyMotion(int clientId, in PlayerMotionSnapshot motion, double nowSeconds,
            out string reason)
        {
            reason = null;
            if (!motion.IsFinite)
            {
                reason = "动画状态含 NaN/Inf";
                return false;
            }
            Entry entry = GetOrCreate(clientId);
            bool wasHurt = entry.HasMotion && entry.Motion.Has(PlayerMotionFlags.Hurt);
            bool isHurt = motion.Has(PlayerMotionFlags.Hurt);
            entry.Motion = motion;
            entry.HasMotion = true;
            entry.LastUpdateSeconds = nowSeconds;
            // 挖掘结束：裂纹表现立刻归零（旧实现 ClearRemoteDigPresentation 同语义）。
            if (!motion.Has(PlayerMotionFlags.Digging)) entry.HasDig = false;
            if (isHurt && !wasHurt) entry.HurtUntil = nowSeconds + HurtFlashSeconds;
            MotionApplied++;
            return true;
        }

        /// <summary>应用手持物（决定瞄准姿势与"空手/拿东西"的差别）。</summary>
        public bool ApplyHeldItem(int clientId, in PlayerHeldItemSnapshot item, out string reason)
        {
            reason = null;
            Entry entry = GetOrCreate(clientId);
            entry.HeldItem = item;
            entry.HasHeldItem = true;
            HeldItemsApplied++;
            return true;
        }

        /// <summary>
        /// 每帧推进（两端都调用）：相位按引擎口径前进/回绕；持续挖掘由 Digging 状态位维持。
        /// </summary>
        public void Advance(float deltaTime, double nowSeconds)
        {
            if (m_entries.Count == 0) return;
            float dt = MathUtils.Clamp(deltaTime, 0f, 0.1f);
            foreach (KeyValuePair<int, Entry> pair in m_entries)
            {
                Entry entry = pair.Value;
                bool sustainedDig = entry.HasMotion && entry.Motion.Has(PlayerMotionFlags.Digging);
                if (sustainedDig || entry.SwingPhase > 0f)
                {
                    entry.SwingPhase += SwingPhasePerSecond * dt;
                    if (entry.SwingPhase > 1f)
                    {
                        entry.SwingPhase = sustainedDig
                            ? MathUtils.Remainder(entry.SwingPhase, 1f)
                            : 0f;
                    }
                }
                else
                {
                    entry.SwingPhase = 0f;
                }
                entry.LastUpdateSeconds = nowSeconds;
            }
        }

        public bool HasState(int clientId) => m_entries.ContainsKey(clientId);

        /// <summary>最近应用的一条动作（种类 + 发送端当时的相位）；诊断/自检用。</summary>
        public bool TryGetLastAction(int clientId, out PlayerActionKind kind, out float phase)
        {
            kind = PlayerActionKind.Unspecified;
            phase = 0f;
            if (!m_entries.TryGetValue(clientId, out Entry entry)) return false;
            if (entry.LastActionSequence <= 0) return false;
            kind = entry.LastActionKind;
            phase = entry.LastActionPhase;
            return true;
        }

        /// <summary>该远端化身当前的动画状态（诊断/自检用）；false = 还没收到过状态。</summary>
        /// <summary>
        /// 乘骑控制桥（2026-10-26）：clientId 当前是否骑在坐骑上 + 骑手输入量。
        /// 主机按它把输入写进 SteedBehavior / Boat 的 Order（与引擎 ComponentPlayer.cs:126-133
        /// 原生桥同口径）；false = 没在骑 / 没收到过状态。
        /// </summary>
        public bool TryGetRidingInput(int clientId, out float turn, out float forward, out bool jump)
        {
            turn = 0f;
            forward = 0f;
            jump = false;
            if (!m_entries.TryGetValue(clientId, out Entry entry) || !entry.HasMotion) return false;
            PlayerMotionSnapshot motion = entry.Motion;
            if (!motion.Has(PlayerMotionFlags.Riding)) return false;
            turn = motion.TurnOrder.X;
            forward = motion.WalkOrder.Y;
            jump = motion.JumpOrder > 0.5f;
            return true;
        }

        public bool TryGetMotion(int clientId, out PlayerMotionSnapshot motion)
        {
            motion = default;
            if (!m_entries.TryGetValue(clientId, out Entry entry) || !entry.HasMotion) return false;
            motion = entry.Motion;
            return true;
        }

        /// <summary>取某个远端化身当前要写进引擎的表现值。false = 还没有任何状态。</summary>
        public bool TryGetPresentation(int clientId, out RemoteAvatarPresentation presentation)
        {
            presentation = default;
            if (!m_entries.TryGetValue(clientId, out Entry entry)) return false;
            if (!entry.HasMotion && entry.SwingPhase <= 0f) return false;
            bool aiming = entry.HasMotion && entry.Motion.Has(PlayerMotionFlags.Aiming);
            HeldItemAimKind aimKind = entry.HasHeldItem ? entry.HeldItem.AimKind : HeldItemAimKind.None;
            AimPresentation.Compute(aiming, aimKind, out float aimHandAngle,
                out Vector3 itemOffset, out Vector3 itemRotation);
            presentation = new RemoteAvatarPresentation(entry.SwingPhase,
                entry.HasMotion && entry.Motion.Has(PlayerMotionFlags.Attacking), aiming,
                aimHandAngle, itemOffset, itemRotation,
                entry.HasMotion && entry.Motion.Has(PlayerMotionFlags.RowLeft),
                entry.HasMotion && entry.Motion.Has(PlayerMotionFlags.RowRight),
                entry.HasMotion && entry.Motion.Has(PlayerMotionFlags.Crouching),
                entry.HasMotion && entry.Motion.Has(PlayerMotionFlags.Flying),
                TimeNow() < entry.HurtUntil,
                entry.HasMotion && entry.Motion.Has(PlayerMotionFlags.OnFire),
                entry.HasHeldItem ? entry.HeldItem.ActiveSlotIndex : -1,
                entry.HasDig, entry.DigX, entry.DigY, entry.DigZ, entry.DigFace, entry.DigProgress);
            return true;
        }

        private readonly Func<double> m_clock;

        /// <summary>
        /// <paramref name="clock"/> = 秒级单调时钟（生产传 <c>() =&gt; Time.RealTime</c>，
        /// 自检传可控时钟）。不读静态时间：模块与自检跑的是同一份逻辑。
        /// </summary>
        public RemoteAnimationAdapter(Func<double> clock = null)
        {
            m_clock = clock ?? (() => 0.0);
        }

        private double TimeNow() => m_clock();

        /// <summary>状态行：应用计数 + 每个客户的最近动作（限流日志用）。</summary>
        public string Describe()
        {
            return "remote-anim entries=" + m_entries.Count +
                " actions=" + ActionsApplied.ToString(CultureInfo.InvariantCulture) +
                " actionsDropped=" + ActionsRejected.ToString(CultureInfo.InvariantCulture) +
                " motion=" + MotionApplied.ToString(CultureInfo.InvariantCulture) +
                " heldItems=" + HeldItemsApplied.ToString(CultureInfo.InvariantCulture);
        }
    }
}
