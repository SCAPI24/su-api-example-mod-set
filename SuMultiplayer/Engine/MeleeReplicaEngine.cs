using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Creatures;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using EngineRandom = global::Engine.Random;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 近战攻击的**引擎侧适配器**（实现 <see cref="IMeleeWorldPort"/>，组合根注入）。
    ///
    /// 输入分流（主机本机角色与客户端同一条规则，见 <see cref="MeleeHitGate"/>）：
    ///   · 左键按下同一帧同时置 Dig / Hit；只有引擎等价身体射线命中身体才当攻击接管；
    ///   · 没有身体目标（准星对着方块 / 空点）→ 返回 false、**不清 Hit**：原生攻击分支
    ///     本来就 no-op，Dig 分支照常执行，与单机完全一致（不播 Poke / Swoosh、不发请求、
    ///     不消耗共享战斗流；计数 digPressNotAttack / attackFromDigSuppressed /
    ///     swooshSuppressedDig）。
    ///
    /// 客户端：
    ///   · 通过 <c>SuComponentInput.HitSink</c> 在 <c>ComponentInput.Update</c> 之后、
    ///     <c>ComponentPlayer.Update</c> 之前接管左键攻击（清空 <c>PlayerInput.Hit</c>），
    ///     原生 <c>ComponentMiner.Hit</c> 整段不执行 —— **不 roll 命中、不扣血**；
    ///   · 目标 = 本机射线命中的生物复制体 / 联机角色（解析成主机网络键 / 玩家键）；
    ///     解析不了的命中按"打空"处理（那些身体本来也不掉血）；
    ///   · 立刻展示预测：挥手 + <c>Audio/Swoosh</c> + 红色命中标记（可关）；距离预检
    ///     认为够不到时**只发空气挥击**并本地按未命中展示（计数 meleeOutOfReach）；
    ///   · 主机结果到达：命中 → 保留预测标记 + 主机的伤害数字 / 命中音；未命中 / 拒绝 →
    ///     **删掉预测标记**（计数 meleeRollback）+ 引擎的 "Miss" 标记，不播命中音。
    ///
    /// 主机：
    ///   · <see cref="TryExecuteAuthoritative"/> 解析权威攻击者 / 目标，用主机几何
    ///     （眼睛 → 目标包围盒中心）做 reach / 锥角 / 遮挡裁决，然后调**一次**
    ///     <c>ComponentMiner.Hit</c>（伤害 / 护甲 / 韧性 / 击退 / 眩晕全部走引擎）；
    ///   · 命中判定直接从引擎自己的玩家统计 <c>MeleeHits / MeleeAttacks</c> 增量读取
    ///     （不重算概率、不重 roll）；伤害 = 生命掉落 × AttackResilience（与单机伤害数字同源）；
    ///   · 主机自己角色的攻击也走同一条裁决（输入同样被接管），结果同样广播给观测端。
    ///
    /// 距离（主客一致）：allowed = min(请求 reach + 0.5 m, meleereachmax, 主机引擎 reach)；
    /// 主机引擎 reach 再夹到引擎自己的近战射线长度 15 m
    /// （Source: Survivalcraft/Game/ComponentMiner.cs:382 <c>end = start + direction * 15f</c>）。
    ///
    /// 本文件只**读**生命（写生命属于生命体征流，Engine/PlayerVitalsReplica.cs）；
    /// 客户端分支没有任何 Injure / Heal / AttackBody 调用。
    /// </summary>
    public sealed class MeleeReplicaEngine : IMeleeWorldPort, IHitInputSink
    {
        /// <summary>引擎近战冷却（秒）。Source: Survivalcraft/Game/ComponentMiner.cs:295</summary>
        private const double EngineHitCooldownSeconds = 0.66;

        /// <summary>
        /// 引擎近战射线长度（米）。<c>ComponentMiner.Raycast</c> 的 <c>end = start + direction * 15f</c>，
        /// 所以即使 reach 更大，超过 15 m 的身体 / 方块也永远不会被射线命中。
        /// Source: Survivalcraft/Game/ComponentMiner.cs:382
        /// </summary>
        public const float EngineMeleeRayLengthMeters = 15f;

        private const int ProblemLogMs = 5000;

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly CreatureSpawnModule m_creatures;
        private readonly CreatureWorldAdapter m_creatureWorld;
        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly ITransportPort m_transport;
        private readonly string m_owner;
        private readonly float m_serverMaxReach;

        private ModFieldRef<ComponentInput, PlayerInput> m_inputRef;
        private ModFieldRef<ComponentMiner, double> m_lastHitTimeRef;
        private ModFieldRef<ComponentPlayer, bool> m_isDigBlockedRef;
        private bool m_inputBindingFailed;

        private readonly List<SuComponentInput> m_attachedInputs = new List<SuComponentInput>();
        private readonly List<PlayerData> m_localPlayers = new List<PlayerData>(PlayerKey.SlotCount);
        private readonly Dictionary<ComponentPlayer, int> m_localSlots =
            new Dictionary<ComponentPlayer, int>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<string> m_loggedOnce = new HashSet<string>(StringComparer.Ordinal);

        private readonly MeleePredictionTracker m_predictions = new MeleePredictionTracker();
        private readonly Dictionary<long, ParticleSystemBase> m_predictionMarkers =
            new Dictionary<long, ParticleSystemBase>();
        private readonly List<long> m_expiredScratch = new List<long>(16);

        // ---- 共享战斗随机流 / 审计 / lag compensation groundwork ----
        private readonly CombatAttackLog m_attackLog = new CombatAttackLog();
        private readonly CombatLagCompensationBuffer m_lagCompensation =
            new CombatLagCompensationBuffer();
        private readonly Dictionary<long, long> m_sentAttackIndexes =
            new Dictionary<long, long>();
        private readonly Queue<long> m_sentAttackOrder = new Queue<long>();

        private Project m_project;
        private bool m_clientReplica;
        private float m_cachedServerMaxReach;
        private float m_cachedEngineReach;
        private bool m_active;
        private bool m_predictionEnabled = true;
        private Func<long> m_sequenceAllocator;
        private double m_lastClientAttackTime;
        private long m_authoritativeTick;
        private long m_clientTick;

        // ---------------------------------------------------------------- 计数（状态行可审计）
        private long m_captured;
        private long m_capturedHost;
        private long m_cooldownBlocked;
        private long m_clearInputFailures;
        private long m_targetUnmapped;
        private long m_localOutOfReach;
        private long m_localTargeted;
        private long m_localAir;
        private long m_predicted;
        private long m_predictionDisabled;
        private long m_predictionOverflow;
        private long m_predictionConfirmed;
        private long m_rollback;
        private long m_rollbackMarkerMissing;
        private long m_predictionTimedOut;
        private long m_predictionNotSent;
        private long m_missPresented;
        private long m_observedPresented;
        private long m_rejectionPresented;
        private long m_outOfReachPresented;
        private long m_cooldownPresented;
        private long m_targetMissingPresented;
        private long m_applyFailures;
        private long m_impactSounds;
        private long m_damagePopups;
        private long m_missMarkers;
        private long m_swooshes;
        private long m_hostExecuted;
        private long m_hostHit;
        private long m_hostMiss;
        private long m_hostRejected;
        private long m_hostAirSwing;
        private long m_hostInvulnerable;
        private long m_hostNoDamage;
        private long m_raycastFailures;
        private long m_engineHitFailures;
        private long m_hostEngineRefused = 0;

        // ---- 挖掘按下 / 攻击分流（dig-only 回归的审计口径）
        // 左键按下同一帧同时置 Dig（按住）与 Hit（按下一帧）：
        // Source: Survivalcraft/Game/ComponentInput.cs:183-184
        // 没有身体目标时原生只挖不攻击（ComponentPlayer.cs:230-242 整段 no-op），
        // 因此这些计数只在"无身体目标"时增长；有身体目标时仍走原有攻击计数。
        private long m_digPressNotAttack;
        private long m_attackFromDigSuppressed;
        private long m_swooshSuppressedDig;
        private long m_airSwingOnlyWhenIntended;
        private long m_lastDigPressLogMs;

        /// <summary>挖掘按下日志的限流窗口（10 s；与状态行 SummaryLogMs 同一口径）。</summary>
        private const int DigPressLogMs = 10000;

        private readonly EngineRandom m_audioRandom = new EngineRandom();

        public MeleeReplicaEngine(NetworkAvatarSpawner spawner, CreatureSpawnModule creatures,
            CreatureWorldAdapter creatureWorld, IModParentField parentField,
            IModuleDiagnostics diagnostics, ITransportPort transport, float serverMaxReach,
            string owner)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_creatures = creatures ?? throw new ArgumentNullException(nameof(creatures));
            m_creatureWorld = creatureWorld ?? throw new ArgumentNullException(nameof(creatureWorld));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_diagnostics = diagnostics;
            m_transport = transport;
            m_owner = string.IsNullOrEmpty(owner) ? "melee-sync" : owner;
            if (!float.IsFinite(serverMaxReach) || serverMaxReach < MeleeReachRule.MinServerMaxMeters ||
                serverMaxReach > MeleeReachRule.MaxServerMaxMeters)
            {
                throw new ArgumentOutOfRangeException(nameof(serverMaxReach), serverMaxReach,
                    "近战距离上限必须在 " + MeleeReachRule.MinServerMaxMeters + ".." +
                    MeleeReachRule.MaxServerMaxMeters + " m 之间");
            }
            m_serverMaxReach = serverMaxReach;
            m_cachedServerMaxReach = serverMaxReach;
            m_cachedEngineReach = Math.Min(EngineReach.LocalReach(), EngineMeleeRayLengthMeters);
        }

        // ================================================================ IMeleeWorldPort

        public bool IsClientReplica => m_clientReplica;

        public bool Active
        {
            get => m_active;
            set => m_active = value;
        }

        public bool PredictionEnabled
        {
            get => m_predictionEnabled;
            set => m_predictionEnabled = value;
        }

        public float ServerMaxReach => m_serverMaxReach;

        public float HostEngineReach =>
            Math.Min(EngineReach.LocalReach(), EngineMeleeRayLengthMeters);

        public long AuthoritativeTick => m_authoritativeTick;

        public Func<long> SequenceAllocator
        {
            get => m_sequenceAllocator;
            set => m_sequenceAllocator = value;
        }

        public CombatRngClientState CombatRngClient { get; set; }

        public CombatRngHostState CombatRngHost { get; set; }

        public CombatRngTelemetry CombatTelemetry { get; set; }

        public event Action<MeleeLocalAttack> LocalAttackCaptured;

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                ResetForProject(project);
                m_project = project;
            }
            if (project == null)
            {
                DetachInputs();
                return;
            }
            m_clientReplica = project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain shell &&
                shell.ClientReplica;
            SubsystemTime time = project.FindSubsystem<SubsystemTime>(false);
            if (time != null)
            {
                m_authoritativeTick = (long)Math.Floor(time.GameTime * 60.0);
                m_clientTick = m_authoritativeTick;
            }
            if (m_inputRef == null && !m_inputBindingFailed)
            {
                try
                {
                    m_inputRef = m_parentField.BindFieldRef<ComponentInput, PlayerInput>("m_playerInput");
                    m_lastHitTimeRef = m_parentField.BindFieldRef<ComponentMiner, double>("m_lastHitTime");
                    m_isDigBlockedRef = m_parentField.BindFieldRef<ComponentPlayer, bool>("m_isDigBlocked");
                }
                catch (Exception e)
                {
                    m_inputBindingFailed = true;
                    Problem("input-bind", "无法绑定 ComponentInput.m_playerInput / ComponentMiner.m_lastHitTime（" +
                        e.GetType().Name + " " + e.Message + "）→ 近战输入拦截不启用（本端走原生攻击）");
                    return;
                }
            }
            AttachInputs(project);
            ExpirePredictions();
        }

        // ---------------------------------------------------------------- 输入拦截

        public bool TryAcceptHit(ComponentInput input, ComponentPlayer player)
        {
            if (!m_active || input == null || player == null) return false;
            Func<long> allocator = m_sequenceAllocator;
            if (allocator == null) return false;
            Project project = m_project;
            if (project == null || m_inputRef == null) return false;
            Ray3? ray = input.PlayerInput.Hit;
            if (!ray.HasValue) return false;
            if (!m_localSlots.TryGetValue(player, out int slot))
            {
                LogOnce("not-local", "近战输入拦截收到非本机角色（" + SafeName(player) +
                    "）的攻击输入 → 不接管（走原生）");
                return false;
            }
            ComponentMiner miner = player.ComponentMiner;
            ComponentBody body = player.ComponentBody;
            if (miner == null || body == null || player.ComponentCreatureModel == null) return false;
            SubsystemTime time = project.FindSubsystem<SubsystemTime>(false);
            if (time == null) return false;

            Vector3 eye = player.ComponentCreatureModel.EyePosition;
            Vector3 direction = ray.Value.Direction;
            if (direction.LengthSquared() < MeleeLimits.MinDirectionLengthSquared)
                direction = body.Matrix.Forward;
            if (direction.LengthSquared() < MeleeLimits.MinDirectionLengthSquared) return false;
            direction = Vector3.Normalize(direction);

            // ---- 目标：与引擎**完全同一条**射线（BodyRaycastResult = 最近的未被遮挡的身体）。
            // 过滤条件就是原生攻击分支的那一套：排除自己 / 子身体、身体中心与视线夹角 > 0.7、
            // 眼到命中点 <= reach；地形 / 移动方块更近时 Raycast<T> 不返回 T（准星对着方块 →
            // 这里必定 null）。
            // Source: Survivalcraft/Game/ComponentMiner.cs:376-420（Raycast 过滤 + 最近命中）
            // Source: Survivalcraft/Game/ComponentPlayer.cs:230-242（原生 Hit 分支）
            BodyRaycastResult? bodyHit;
            try
            {
                bodyHit = miner.Raycast<BodyRaycastResult>(ray.Value, RaycastMode.Interaction);
            }
            catch (Exception e)
            {
                m_raycastFailures++;
                Problem("raycast", "近战目标射线查询失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 本帧不接管攻击（不发请求、不占战斗流、不播 Poke/Swoosh）；" +
                    "清空 Hit 只为避免原生分支再射线一次并重复抛异常，Dig 未动");
                // 异常路径清 Hit 是**安全兜底而不是攻击**：原生攻击分支会对同一个 Hit 再射线
                // （可能再次抛异常）；Dig 分支只读 PlayerInput.Dig，所以清 Hit 不改变挖掘。
                // 这里不分配序列号、不推进随机流、不发请求、不播表现（计数器只记 raycastFailed）。
                ConsumeHit(input);
                return true;
            }

            if (!MeleeHitGate.ShouldCapture(bodyHit.HasValue))
            {
                // 没有身体目标 = 左键按下的 Dig + Hit 是**挖掘按下**，不是攻击。
                // 原生攻击分支对无身体命中的 Hit 整段 no-op（不设 m_isDigBlocked、不调 Hit、
                // 不播 Swoosh），紧接着 Dig 分支照常执行：
                // Source: Survivalcraft/Game/ComponentPlayer.cs:230-250
                // 因此必须在 分配序列号 / 推进战斗随机流 / 清空 Hit / Poke+Swoosh / 发请求
                // **之前**退出，把 Hit 原样留给原生分支：它再射线一次仍然无身体目标 → 没有
                // 任何攻击副作用，Dig 的时序 / 标志位完全不受影响（这就是单机行为）。
                m_digPressNotAttack++;
                m_attackFromDigSuppressed++;
                m_swooshSuppressedDig++;
                LogDigPressNotAttack(input, slot);
                return false;
            }

            // ---- 冷却：与引擎 ComponentMiner.Hit 同一个 0.66 s 门槛（主机读引擎自己的字段）。
            // 放在身体目标判定之后：挖掘按下的 Hit 不属于攻击，不能被冷却吞掉 / 记为 cooldown。
            bool cooldownReady;
            if (m_clientReplica)
            {
                cooldownReady = time.GameTime - m_lastClientAttackTime > EngineHitCooldownSeconds;
            }
            else
            {
                double lastHit = m_lastHitTimeRef != null ? m_lastHitTimeRef(miner) : 0.0;
                cooldownReady = time.GameTime - lastHit > EngineHitCooldownSeconds;
            }
            if (!cooldownReady)
            {
                m_cooldownBlocked++;
                ConsumeHit(input);
                return true;
            }

            MeleeTargetRef target = MeleeTargetRef.Air;
            Vector3 hitPoint = bodyHit.Value.HitPoint();
            bool unmappedBody = false;
            // 与原生攻击分支同一条副作用：射线命中身体时本帧 / 按住期间禁止挖掘
            // （ComponentPlayer.Update 的 m_isDigBlocked；否则攻击动物会顺手挖到它身后的方块）。
            // Source: Survivalcraft/Game/ComponentPlayer.cs:235-236 / :252-255
            try
            {
                if (m_isDigBlockedRef != null) m_isDigBlockedRef(player) = true;
            }
            catch (Exception)
            {
                LogOnce("dig-block", "无法写入 ComponentPlayer.m_isDigBlocked → 攻击时可能顺带挖掘（只记录一次）");
            }
            if (!TryResolveTargetRef(bodyHit.Value.ComponentBody, out target, out string reason))
            {
                m_targetUnmapped++;
                unmappedBody = true;
                Problem("target-unmapped", "攻击射线命中的身体无法映射成网络目标（" +
                    reason + "）→ 按打空处理（不猜目标、不本地扣血）");
                target = MeleeTargetRef.Air;
            }

            // 客户端预检：同一公式 + 最近一次主机回执带回的上限值。够不到 → 只发空气挥击。
            bool outOfRangeFallback = false;
            if (m_clientReplica && !target.IsAir)
            {
                float measured = MeleeGeometry.EyeToTargetCenter(eye,
                    bodyHit.Value.ComponentBody.BoundingBox.Min,
                    bodyHit.Value.ComponentBody.BoundingBox.Max);
                MeleeReachDecision reach = MeleeReachRule.Evaluate(true,
                    EngineReach.LocalReach(), EngineReach.IsCreative(project),
                    m_cachedServerMaxReach, m_cachedEngineReach);
                if (!reach.Valid || measured > reach.AllowedDistanceMeters)
                {
                    m_localOutOfReach++;
                    outOfRangeFallback = true;
                    target = MeleeTargetRef.Air;
                }
            }

            // 走到这里 = 引擎等价射线**命中了身体**：只有"目标无法映射 / 客户端预检够不到"
            // 才退化成空气挥击。没有身体目标的挖掘按下在更上面就回原生了，永远到不了这里。
            // Source: Survivalcraft/Game/ComponentPlayer.cs:230-242（原生命中 > 2 m 也只挡挖掘）
            if (target.IsAir) m_airSwingOnlyWhenIntended++;

            // ---- 共享战斗随机流：攻击当帧取 index + 4 个槽（客户端乐观推进；主机只读下一个 index）
            long attackIndex = 0L;
            CombatSlotRolls rolls = default;
            bool hasRolls = false;
            string rngReason = null;
            if (m_clientReplica)
            {
                if (CombatRngClient != null)
                    hasRolls = CombatRngClient.TryBeginAttack(slot, out attackIndex, out rolls,
                        out rngReason);
                else
                    rngReason = "no-rng-state";
                if (!hasRolls)
                {
                    if (CombatTelemetry != null) CombatTelemetry.PredictionUnavailable++;
                    if (!outOfRangeFallback && !target.IsAir)
                        Problem("prediction-unavailable", "客户端没有战斗随机流（" +
                            (rngReason ?? "unknown") + "）→ 本次不预测命中 / Miss，" +
                            "仍上行请求；等 COMBAT_RNG_SYNC 或结果回传的流位置纠正");
                }
            }
            else
            {
                CombatRngKey hostKey = new CombatRngKey(0, slot);
                attackIndex = CombatRngHost != null ? CombatRngHost.NextIndex(in hostKey) : 0L;
                if (CombatRngHost == null)
                    Problem("host-rng-missing", "主机没有战斗随机流状态 → 主机本机攻击无法结算" +
                        "（端口未装配，不伪造 index）");
            }

            long sequence = allocator();
            if (sequence <= 0) return false;
            var request = new MeleeAttackRequestData(sequence, m_clientTick, slot, target, eye,
                direction, true, EngineReach.LocalReach(), attackIndex);
            if (!ConsumeHit(input))
            {
                if (m_clientReplica) CombatRngClient?.Rewind(slot, attackIndex);
                return false;
            }
            if (m_clientReplica) RememberSentAttackIndex(sequence, attackIndex);

            bool predicted = false;
            if (m_clientReplica)
            {
                if (outOfRangeFallback)
                {
                    // 挥手 + 挥击声 + 引擎自己的 "Miss" 标记（本地按未命中展示）；
                    // 请求是空气挥击，主机不会为它结算任何伤害。
                    m_localAir++;
                    PresentSwing(miner, player, hitPoint, direction);
                    PresentMiss(hitPoint, direction, body.Velocity);
                }
                else if (!target.IsAir)
                {
                    m_localTargeted++;
                    if (hasRolls)
                    {
                        float probability = LocalHitProbability(miner, bodyHit.Value.ComponentBody);
                        predicted = PredictWithStream(sequence, target, player, miner, hitPoint,
                            direction, attackIndex, rolls, probability);
                    }
                    else
                    {
                        PresentSwing(miner, player, hitPoint, direction);
                    }
                }
                else
                {
                    m_localAir++;
                    PresentSwing(miner, player, hitPoint, direction);
                }
                m_lastClientAttackTime = time.GameTime;
            }
            else
            {
                _ = unmappedBody;
                m_capturedHost++;
            }
            m_captured++;
            LocalAttackCaptured?.Invoke(new MeleeLocalAttack(in request, predicted,
                outOfRangeFallback));
            return true;
        }

        private bool ConsumeHit(ComponentInput input)
        {
            try
            {
                m_inputRef(input).Hit = null;
                return true;
            }
            catch (Exception e)
            {
                m_clearInputFailures++;
                Problem("clear-hit", "清空 PlayerInput.Hit 失败（" + e.GetType().Name + " " +
                    e.Message + "）→ 本次**不发请求、不做预测**（避免本地多打一次）");
                return false;
            }
        }

        private void AttachInputs(Project project)
        {
            bool collected = m_spawner.TryCollectLocalPlayers(project, m_localPlayers,
                out bool tooMany, out string reason);
            m_localSlots.Clear();
            if (!collected)
            {
                DetachInputs();
                LogOnce("locals", "近战输入拦截：本机角色枚举失败（" +
                    (reason ?? "端口没有给原因") + "）→ 本帧不接管攻击输入");
                return;
            }
            if (tooMany)
                Problem("locals-too-many", "本机角色超过 " + PlayerKey.SlotCount +
                    " 个（引擎上限）→ 只接管前 " + PlayerKey.SlotCount + " 个槽位（" +
                    (reason ?? string.Empty) + "）");
            for (int slot = 0; slot < m_localPlayers.Count && slot < PlayerKey.SlotCount; slot++)
            {
                ComponentPlayer player = m_localPlayers[slot]?.ComponentPlayer;
                if (player == null) continue;
                m_localSlots[player] = slot;
            }
            // 只在本机角色上挂 sink；多余的（换角色 / 换世界）立即摘掉。
            var keep = new List<SuComponentInput>(m_localPlayers.Count);
            foreach (KeyValuePair<ComponentPlayer, int> pair in m_localSlots)
            {
                ComponentInput input = pair.Key?.Entity?.FindComponent<ComponentInput>();
                if (input is SuComponentInput shell && ReferenceEquals(shell.HitSink, this) == false)
                {
                    shell.HitSink = this;
                }
                if (input is SuComponentInput attached) keep.Add(attached);
            }
            for (int i = m_attachedInputs.Count - 1; i >= 0; i--)
            {
                SuComponentInput shell = m_attachedInputs[i];
                if (shell == null) continue;
                if (ReferenceEquals(shell.HitSink, this) && !keep.Contains(shell)) shell.HitSink = null;
            }
            m_attachedInputs.Clear();
            m_attachedInputs.AddRange(keep);
            if (m_attachedInputs.Count == 0)
            {
                LogOnce("no-shell", "近战输入拦截：本机玩家没有注入 SuComponentInput（当前没有可接管的输入组件）" +
                    " → 本端攻击走原生路径（客户端会本地 roll 命中，需检查 Game.ComponentInput 注入）");
            }
        }

        private void DetachInputs()
        {
            for (int i = 0; i < m_attachedInputs.Count; i++)
            {
                SuComponentInput shell = m_attachedInputs[i];
                if (shell != null && ReferenceEquals(shell.HitSink, this)) shell.HitSink = null;
            }
            m_attachedInputs.Clear();
            m_localSlots.Clear();
        }

        private void ResetForProject(Project project)
        {
            DetachInputs();
            RemoveAllPredictionMarkers();
            m_predictions.Reset();
            m_sentAttackIndexes.Clear();
            m_sentAttackOrder.Clear();
            m_attackLog.Clear();
            m_lagCompensation.Clear();
            CombatRngClient?.Reset();
            if (CombatTelemetry != null)
            {
                CombatTelemetry.AttackLogStored = 0L;
                CombatTelemetry.AttackLogDropped = 0L;
                CombatTelemetry.LagCompSample = 0L;
                CombatTelemetry.LagCompBufferFull = 0L;
                CombatTelemetry.LagCompNotReady = 0L;
                CombatTelemetry.CombatRngIndex = 0L;
            }
            m_lastClientAttackTime = 0.0;
            m_cachedServerMaxReach = m_serverMaxReach;
            m_cachedEngineReach = Math.Min(EngineReach.LocalReach(), EngineMeleeRayLengthMeters);
            m_clientReplica = project?.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain shell &&
                shell.ClientReplica;
            m_localPlayers.Clear();
        }

        // ---------------------------------------------------------------- 目标解析

        private bool TryResolveTargetRef(ComponentBody body, out MeleeTargetRef target,
            out string reason)
        {
            target = MeleeTargetRef.Air;
            reason = null;
            if (body == null)
            {
                reason = "射线没有命中身体";
                return false;
            }
            Entity entity = body.Entity;
            if (entity == null)
            {
                reason = "命中的身体没有实体";
                return false;
            }
            if (m_clientReplica)
            {
                if (m_creatureWorld.TryGetReplicaKey(entity, out int replicaKey, out _))
                {
                    target = MeleeTargetRef.Creature(replicaKey);
                    return true;
                }
            }
            else if (m_creatures.TryGetHostCreatureKey(entity, out int hostKey))
            {
                target = MeleeTargetRef.Creature(hostKey);
                return true;
            }
            if (m_spawner.TryGetPlayerKey(entity, out PlayerKey playerKey, out _))
            {
                target = MeleeTargetRef.Avatar(playerKey.ClientId, playerKey.Slot);
                return true;
            }
            ComponentPlayer local = entity.FindComponent<ComponentPlayer>();
            if (local != null && m_localSlots.TryGetValue(local, out int slot))
            {
                int clientId = m_clientReplica ? (m_transport?.LocalClientId ?? -1) : 0;
                if (clientId >= 0)
                {
                    target = MeleeTargetRef.Avatar(clientId, slot);
                    return true;
                }
                reason = "本机传输编号还没分配（LocalClientId=" + clientId + "）";
                return false;
            }
            reason = "实体既不是生物复制体 / 生物，也不是联机角色或本机角色";
            return false;
        }

        // ---------------------------------------------------------------- 预测表现（客户端）

        /// <summary>
        /// 客户端用主机同一条流预测命中 / Miss，并**当帧**播表现：
        ///   · u &lt; p → 红色命中标记（predictedHit）；
        ///   · 否则 → 白色 "Miss" 标记（predictedMiss）。
        /// 结果到达后同 index + 同结果 = 确认；不同 = 回滚标记并按主机流位置重同步。
        /// </summary>
        private bool PredictWithStream(long sequence, in MeleeTargetRef target,
            ComponentPlayer player, ComponentMiner miner, Vector3 hitPoint, Vector3 direction,
            long attackIndex, in CombatSlotRolls rolls, float probability)
        {
            PresentSwing(miner, player, hitPoint, direction);
            if (!m_predictionEnabled)
            {
                m_predictionDisabled++;
                return false;
            }
            bool predictedHit = CombatHitProbability.Hit(rolls.U, probability);
            long nowMs = (long)(NowSeconds() * 1000.0);
            if (!m_predictions.Begin(sequence, in target, nowMs, predictedHit, attackIndex,
                    rolls.U, probability))
            {
                m_predictionOverflow++;
                Problem("prediction-overflow", "预测表已满（" + MeleePredictionTracker.MaxPending +
                    "）→ 本次不展示红色命中标记（结果仍按主机回执处理）");
                return false;
            }
            m_predicted++;
            ParticleSystemBase marker = null;
            try
            {
                marker = predictedHit
                    ? AddHitMarker(hitPoint, direction,
                        player.ComponentBody?.Velocity ?? Vector3.Zero, Color.Red, "*")
                    : AddHitMarker(hitPoint, direction,
                        player.ComponentBody?.Velocity ?? Vector3.Zero, Color.White, "Miss");
            }
            catch (Exception e)
            {
                Problem("prediction-marker", "预测标记创建失败（" + e.GetType().Name + " " +
                    e.Message + "）→ 表现改由主机回执驱动");
            }
            m_predictionMarkers[sequence] = marker;
            if (predictedHit)
            {
                if (CombatTelemetry != null) CombatTelemetry.PredictedHit++;
            }
            else
            {
                if (CombatTelemetry != null) CombatTelemetry.PredictedMiss++;
                m_missMarkers++;
                m_missPresented++;
            }
            return true;
        }

        /// <summary>
        /// 客户端本地命中概率（与主机结算同一个纯公式）：
        /// p = (手持物 ? block.GetMeleeHitProbability : 0.66) × (目标速度 &lt; 0.05 ? 3 : 1)。
        /// Source: Survivalcraft/Game/ComponentMiner.cs:309-318
        /// </summary>
        private static float LocalHitProbability(ComponentMiner miner, ComponentBody targetBody)
        {
            int active = miner?.ActiveBlockValue ?? 0;
            bool hasTool = active != 0;
            float blockProbability = 0f;
            if (hasTool)
            {
                int contents = Game.Terrain.ExtractContents(active);
                if (contents >= 0 && contents < BlocksManager.Blocks.Length)
                {
                    Block block = BlocksManager.Blocks[contents];
                    if (block != null) blockProbability = block.GetMeleeHitProbability(active);
                }
            }
            float speed = targetBody != null ? targetBody.Velocity.Length() : 0f;
            return CombatHitProbability.Compute(hasTool, blockProbability, speed);
        }

        private void PresentSwing(ComponentMiner miner, ComponentPlayer player, Vector3 hitPoint,
            Vector3 direction)
        {
            try { miner?.Poke(forceRestart: true); }
            catch (Exception) { }
            Project project = m_project;
            SubsystemAudio audio = project?.FindSubsystem<SubsystemAudio>(false);
            if (audio == null) return;
            Vector3 position = hitPoint;
            if ((position - (player?.ComponentCreatureModel?.EyePosition ?? position)).LengthSquared() >
                900f)
            {
                position = player?.ComponentCreatureModel?.EyePosition ?? position;
            }
            try
            {
                // Source: Survivalcraft/Game/ComponentMiner.cs:323（攻击者为玩家时总播 Swoosh）
                audio.PlaySound("Audio/Swoosh", 1f, m_audioRandom.Float(-0.2f, 0.2f), position,
                    3f, autoDelay: false);
                m_swooshes++;
            }
            catch (Exception e)
            {
                Problem("swoosh", "挥击声播放失败（" + e.GetType().Name + " " + e.Message + "）");
            }
        }

        private void PresentMiss(Vector3 hitPoint, Vector3 direction, Vector3 attackerVelocity)
        {
            ParticleSystemBase marker = AddHitMarker(hitPoint, direction, attackerVelocity,
                Color.White, "Miss");
            _ = marker;
            m_missMarkers++;
            m_missPresented++;
        }

        private ParticleSystemBase AddHitMarker(Vector3 hitPoint, Vector3 direction,
            Vector3 attackerVelocity, Color color, string text)
        {
            Project project = m_project;
            SubsystemParticles particles = project?.FindSubsystem<SubsystemParticles>(false);
            if (particles == null) return null;
            // Source: Survivalcraft/Game/ComponentMiner.cs:338 / 524（HitValueParticleSystem 的位置与速度）
            var system = new HitValueParticleSystem(hitPoint + 0.75f * direction,
                direction + attackerVelocity, color, text);
            particles.AddParticleSystem(system);
            return system;
        }

        private void RemovePredictionMarker(long sequence, bool countMissing)
        {
            if (!m_predictionMarkers.TryGetValue(sequence, out ParticleSystemBase marker)) return;
            m_predictionMarkers.Remove(sequence);
            if (marker == null) return;
            Project project = m_project;
            SubsystemParticles particles = project?.FindSubsystem<SubsystemParticles>(false);
            try
            {
                if (particles != null && ReferenceEquals(marker.SubsystemParticles, particles))
                    particles.RemoveParticleSystem(marker);
            }
            catch (Exception e)
            {
                if (countMissing)
                {
                    m_rollbackMarkerMissing++;
                    Problem("rollback-marker", "回滚预测标记失败（" + e.GetType().Name + " " +
                        e.Message + "）");
                }
            }
        }

        private void RemoveAllPredictionMarkers()
        {
            var keys = new List<long>(m_predictionMarkers.Keys);
            for (int i = 0; i < keys.Count; i++) RemovePredictionMarker(keys[i], countMissing: false);
            m_predictionMarkers.Clear();
        }

        private void ExpirePredictions()
        {
            // 即使没有粒子句柄（SubsystemParticles 缺失）也必须让预测超时，
            // 否则预测表会一直占着名额（MaxPending）。
            if (m_predictions.PendingCount == 0) return;
            long nowMs = (long)(NowSeconds() * 1000.0);
            int expired = m_predictions.CollectExpired(nowMs, m_expiredScratch);
            if (expired == 0) return;
            for (int i = 0; i < m_expiredScratch.Count; i++)
            {
                m_predictionTimedOut++;
                RemovePredictionMarker(m_expiredScratch[i], countMissing: false);
                Problem("prediction-timeout", "预测 seq=" + m_expiredScratch[i] +
                    " 在 " + MeleePredictionTracker.TimeoutMs + " ms 内没有等到主机结果 → 已回滚（" +
                    "meleePredictionTimeout 计数）");
            }
        }

        // ---------------------------------------------------------------- 主机执行

        public bool TryExecuteAuthoritative(int sourceClientId, in MeleeAttackRequestData request,
            out MeleeAttackResultData result, out string reason)
        {
            result = default;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            SubsystemTime time = project.FindSubsystem<SubsystemTime>(false);
            if (time == null)
            {
                reason = "no-time-subsystem";
                return false;
            }
            long serverTick = (long)Math.Floor(time.GameTime * 60.0);
            float serverMax = m_serverMaxReach;
            float engineReach = HostEngineReach;
            bool hostCreative = EngineReach.IsCreative(project);

            ComponentPlayer attacker = null;
            string attackerReason = null;
            bool attackerKnown = TryResolvePlayer(project, sourceClientId, request.AttackerSlot,
                out attacker, out attackerReason);
            ComponentMiner miner = attacker?.ComponentMiner;
            ComponentBody attackerBody = attacker?.ComponentBody;
            ComponentHealth attackerHealth = attacker?.ComponentHealth;
            bool attackerAlive = attackerHealth != null && attackerHealth.Health > 0f;

            ComponentBody targetBody = null;
            ComponentPlayer targetPlayer = null;
            ComponentHealth targetHealth = null;
            bool targetExists = request.Target.IsAir;
            string targetReason = null;
            if (!request.Target.IsAir)
            {
                MeleeTargetRef requestedTarget = request.Target;
                targetExists = TryResolveTarget(project, in requestedTarget, out targetBody,
                    out targetPlayer, out targetReason);
                targetHealth = targetBody?.Entity?.FindComponent<ComponentHealth>();
            }
            bool targetAlive = targetHealth == null || targetHealth.Health > 0f;
            bool targetIsSelf = targetBody != null && ReferenceEquals(targetBody, attackerBody);
            bool targetInvulnerable = targetHealth != null && targetHealth.IsInvulnerable;

            float measured = 0f;
            bool aimInCone = false;
            bool rayHitsTarget = false;
            bool rayHitsOther = false;
            Vector3 hitPoint = request.EyePosition + request.LookDirection * 2f;
            if (!request.Target.IsAir && targetBody != null && attackerBody != null &&
                attacker?.ComponentCreatureModel != null)
            {
                Vector3 eye = attacker.ComponentCreatureModel.EyePosition;
                BoundingBox box = targetBody.BoundingBox;
                measured = MeleeGeometry.EyeToTargetCenter(eye, box.Min, box.Max);
                aimInCone = MeleeGeometry.InCone(eye, request.LookDirection, box.Center());
                if (miner != null)
                {
                    try
                    {
                        BodyRaycastResult? ray = miner.Raycast<BodyRaycastResult>(
                            new Ray3(eye, request.LookDirection), RaycastMode.Interaction);
                        if (ray.HasValue)
                        {
                            hitPoint = ray.Value.HitPoint();
                            rayHitsTarget = ReferenceEquals(ray.Value.ComponentBody, targetBody);
                            rayHitsOther = !rayHitsTarget;
                        }
                    }
                    catch (Exception e)
                    {
                        m_raycastFailures++;
                        Problem("host-raycast", "主机近战射线查询失败（" + e.GetType().Name + " " +
                            e.Message + "）→ 按被遮挡处理");
                    }
                }
            }

            bool cooldownReady = true;
            if (miner != null)
            {
                double lastHit = m_lastHitTimeRef != null ? m_lastHitTimeRef(miner) : 0.0;
                cooldownReady = time.GameTime - lastHit > EngineHitCooldownSeconds;
            }
            bool toolLevelSufficient = IsToolLevelSufficient(project, attacker, miner);
            bool friendlyFireAllowed = targetPlayer == null ||
                project.FindSubsystem<SubsystemGameInfo>(false)?.WorldSettings.IsFriendlyFireEnabled != false;

            var inputs = new MeleeAdjudicationInputs(
                attackerKnown, attackerAlive, targetExists, targetAlive, targetIsSelf,
                targetInvulnerable, cooldownReady, toolLevelSufficient, friendlyFireAllowed,
                aimInCone, rayHitsTarget, rayHitsOther, measured, serverMax, engineReach,
                hostCreative);
            MeleeVerdict verdict = MeleeAdjudicator.Evaluate(in request, in inputs);

            // 战斗流：拒绝也要拿到当前位置（回执里回传），但**不消耗**。
            var rngKey = new CombatRngKey(sourceClientId, request.AttackerSlot);
            CombatRngStream rngStream = CombatRngHost?.Ensure(in rngKey);
            if (rngStream == null)
            {
                reason = "rng-unavailable";
                Problem("host-rng-missing", "主机没有战斗随机流状态（端口未装配）→ 拒绝结算 " +
                    request.Describe() + "（不伪造 index / 不调引擎 roll）");
                return false;
            }

            if (!verdict.Accepted)
            {
                m_hostRejected++;
                PresentHostSwingIfNeeded(sourceClientId, attacker, miner, hitPoint,
                    request.LookDirection, verdict.Reason);
                Problem("host-reject:" + MeleeReasons.Key(verdict.Reason),
                    "主机拒绝近战 seq=" + request.Sequence + " source=" + sourceClientId +
                    " target=" + request.Target.Describe() + "：" + verdict.Reason +
                    "（实测 " + Fmt(verdict.MeasuredMeters) + " m / " + verdict.Reach.Describe() +
                    (attackerReason == null ? "" : " · 攻击者：" + attackerReason) +
                    (targetReason == null ? "" : " · 目标：" + targetReason) + "）");
                result = MeleeResults.Rejected(in request, sourceClientId, request.AttackerSlot,
                    verdict.Reason, serverTick, verdict.MeasuredMeters,
                    verdict.Reach.AllowedDistanceMeters, serverMax, engineReach,
                    targetBody != null ? targetBody.BoundingBox.Center() : hitPoint,
                    request.LookDirection, targetHealth?.Health ?? -1f, request.AttackIndex,
                    rngStream.NextIndex);
                return true;
            }

            // 空气挥击也是**已接受攻击**：消耗一个 index（客户端镜像同一规则），但没有命中 / 伤害。
            if (request.Target.IsAir)
            {
                m_hostAirSwing++;
                CombatSlotRolls airRolls = rngStream.Consume();
                if (CombatTelemetry != null) CombatTelemetry.CombatRngIndex = rngStream.NextIndex;
                StoreAttackLog(request, sourceClientId, "air", airRolls, 0f, hit: false,
                    damage: 0f, reason: MeleeReasons.AirSwing);
                // 空气挥击没有 Hit 可调 → 主机本地必须自己挥手 + 挥击声（否则主机点空挥没有反馈）。
                PresentHostSwingIfNeeded(sourceClientId, attacker, miner, hitPoint,
                    request.LookDirection, MeleeReasons.AirSwing);
                result = MeleeResults.Executed(in request, sourceClientId, request.AttackerSlot,
                    serverTick, hit: false, damage: 0f, healthAfter: targetHealth?.Health ?? -1f,
                    healthDrop: 0f, targetInvulnerable: false, position: hitPoint,
                    direction: request.LookDirection, measuredMeters: 0f,
                    allowedMeters: verdict.Reach.AllowedDistanceMeters, serverMaxMeters: serverMax,
                    engineReachMeters: engineReach, attackIndex: airRolls.AttackIndex,
                    streamIndex: rngStream.NextIndex, rollU: airRolls.U, rollF: airRolls.F,
                    rollA: airRolls.A, rollReserved: airRolls.Reserved, hitProbability: 0f,
                    hasRolls: true);
                return true;
            }

            // ---- 执行：共享战斗流替换引擎的命中 roll / 方差，其余仍走引擎公开路径。
            if (m_lastHitTimeRef == null)
            {
                reason = "hit-time-unavailable";
                Problem("hit-time", "无法绑定 ComponentMiner.m_lastHitTime → 拒绝结算 " +
                    request.Describe() + "（否则冷却无从生效，不猜）");
                return false;
            }
            m_hostExecuted++;
            float healthBefore = targetHealth != null ? targetHealth.Health : -1f;
            PlayerStats stats = attacker.PlayerStats;

            int activeBlockValue = miner.ActiveBlockValue;
            float meleePower = 1f;
            float baseProbability = CombatHitProbability.NoToolProbability;
            if (activeBlockValue != 0)
            {
                int contents = Game.Terrain.ExtractContents(activeBlockValue);
                if (contents >= 0 && contents < BlocksManager.Blocks.Length)
                {
                    Block block = BlocksManager.Blocks[contents];
                    if (block != null)
                    {
                        meleePower = block.GetMeleePower(activeBlockValue);
                        baseProbability = block.GetMeleeHitProbability(activeBlockValue);
                    }
                }
            }
            if (!float.IsFinite(baseProbability) || !float.IsFinite(meleePower))
            {
                reason = "probability-nonfinite";
                Problem("host-probability", "近战命中概率 / 攻击力不是有限值（tool=" +
                    activeBlockValue + " p=" + Fmt(baseProbability) + " power=" + Fmt(meleePower) +
                    "）→ 拒绝结算（不 clamps 冒充）");
                return false;
            }
            float strengthFactor = attacker.ComponentLevel != null
                ? attacker.ComponentLevel.StrengthFactor : 1f;
            CombatSettlementResult settlement;
            try
            {
                settlement = CombatSettlement.Settle(rngStream, baseProbability,
                    targetBody.Velocity.Length(), meleePower, miner.AttackPower, strengthFactor);
            }
            catch (Exception e)
            {
                m_engineHitFailures++;
                Problem("settle", "共享战斗流结算失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 回执明确拒绝（绝不假装命中）");
                result = MeleeResults.Rejected(in request, sourceClientId, request.AttackerSlot,
                    "rng-settle", serverTick, measured, verdict.Reach.AllowedDistanceMeters,
                    serverMax, engineReach, hitPoint, request.LookDirection,
                    targetHealth?.Health ?? -1f, request.AttackIndex, rngStream.NextIndex);
                return true;
            }
            long settlementIndex = settlement.Rolls.AttackIndex;
            bool hit = settlement.Hit;
            try
            {
                // Source: Survivalcraft/Game/ComponentMiner.cs:323-326（玩家攻击：Swoosh +
                //   m_random.Bool(p) + StrengthFactor）——这里只用共享流替换 roll / 方差。
                PlaySwooshAt(targetBody.Position, request.LookDirection);
                m_lastHitTimeRef(miner) = time.GameTime;
                if (hit)
                {
                    // Source: Survivalcraft/Game/ComponentMiner.cs:472（AttackBody：护甲 / 韧性 /
                    //   Injure / 击退 / 眩晕）——公开静态方法，主机唯一伤害通路。
                    ComponentMiner.AttackBody(targetBody, miner.ComponentCreature, hitPoint,
                        request.LookDirection, settlement.Power, isMeleeAttack: true);
                    miner.DamageActiveTool(1);
                }
                else
                {
                    // Source: Survivalcraft/Game/ComponentMiner.cs:334-338（Miss 粒子：无工具磨损）
                    AddHitMarker(hitPoint + 0.75f * request.LookDirection,
                        request.LookDirection + attackerBody.Velocity, attackerBody.Velocity,
                        Color.White, "Miss");
                    m_missMarkers++;
                    m_missPresented++;
                }
                // Source: Survivalcraft/Game/ComponentMiner.cs:342-350（统计 + Poke）
                if (stats != null)
                {
                    stats.MeleeAttacks++;
                    if (hit) stats.MeleeHits++;
                }
                miner.Poke(forceRestart: false);
            }
            catch (Exception e)
            {
                m_engineHitFailures++;
                // 结算没完成：把流位置退回去，拒绝的攻击不消耗 index（客户端镜像同一规则）。
                rngStream.SetIndex(settlementIndex);
                Problem("engine-attack", "近战引擎路径抛异常（" + e.GetType().Name + " " +
                    e.Message + "）→ 回退战斗流 index 并明确拒绝");
                result = MeleeResults.Rejected(in request, sourceClientId, request.AttackerSlot,
                    "engine-error", serverTick, measured, verdict.Reach.AllowedDistanceMeters,
                    serverMax, engineReach, hitPoint, request.LookDirection,
                    targetHealth?.Health ?? -1f, request.AttackIndex, rngStream.NextIndex);
                return true;
            }
            if (CombatTelemetry != null) CombatTelemetry.CombatRngIndex = rngStream.NextIndex;
            float healthAfter = targetHealth != null ? targetHealth.Health : -1f;
            float healthDrop = healthBefore >= 0f && healthAfter >= 0f
                ? Math.Max(0f, healthBefore - healthAfter) : 0f;
            float damage = targetHealth != null ? healthDrop * targetHealth.AttackResilience : 0f;
            if (hit) m_hostHit++;
            else m_hostMiss++;
            if (targetInvulnerable) m_hostInvulnerable++;
            if (hit && healthDrop <= 0f) m_hostNoDamage++;
            RecordLagCompensation(serverTick, request.Target, targetBody);
            StoreAttackLog(request, sourceClientId, request.Target.Describe(), settlement.Rolls,
                settlement.Probability, hit, damage,
                hit ? MeleeReasons.None : MeleeReasons.Miss);
            result = MeleeResults.Executed(in request, sourceClientId, request.AttackerSlot,
                serverTick, hit, damage, healthAfter, healthDrop, targetInvulnerable, hitPoint,
                request.LookDirection, measured, verdict.Reach.AllowedDistanceMeters, serverMax,
                engineReach, attackIndex: settlementIndex, streamIndex: rngStream.NextIndex,
                rollU: settlement.Rolls.U, rollF: settlement.Rolls.F, rollA: settlement.Rolls.A,
                rollReserved: settlement.Rolls.Reserved, hitProbability: settlement.Probability,
                hasRolls: true);
            return true;
        }

        /// <summary>
        /// 主机**本机角色**的攻击表现：原生 <c>ComponentMiner.Hit</c> 总会 Poke + 播 Swoosh
        /// （Source: ComponentMiner.cs:320-324），但空气挥击 / 裁决拒绝 / 工具等级不足这些
        /// 路径我们**没有**调 Hit（不能为了表现去 roll 一次命中）。这里补回同一次挥手与挥击声，
        /// 命中音 / 伤害数字仍然只在真正命中后由引擎 / 结果驱动。
        /// </summary>
        private void PresentHostSwingIfNeeded(int sourceClientId, ComponentPlayer attacker,
            ComponentMiner miner, Vector3 hitPoint, Vector3 direction, string reason)
        {
            if (sourceClientId != 0 || attacker == null || miner == null) return;
            if (reason == MeleeReasons.Cooldown || reason == MeleeReasons.AttackerUnknown ||
                reason == MeleeReasons.AttackerDead || reason == "engine-error" ||
                reason == "engine-refused")
                return;
            PresentSwing(miner, attacker, hitPoint, direction);
        }

        private void StoreAttackLog(in MeleeAttackRequestData request, int sourceClientId,
            string target, in CombatSlotRolls rolls, float probability, bool hit, float damage,
            string reason)
        {
            var entry = new CombatAttackLogEntry(request.Sequence, rolls.AttackIndex, rolls.U,
                rolls.F, rolls.A, rolls.Reserved,
                target + (sourceClientId > 0 ? "@" + sourceClientId + ":" + request.AttackerSlot
                    : "@host:" + request.AttackerSlot),
                probability, hit, damage, reason);
            m_attackLog.Store(in entry);
            if (CombatTelemetry != null)
            {
                CombatTelemetry.AttackLogStored = m_attackLog.Stored;
                CombatTelemetry.AttackLogDropped = m_attackLog.Dropped;
            }
        }

        private void RecordLagCompensation(long serverTick, in MeleeTargetRef target,
            ComponentBody targetBody)
        {
            if (targetBody == null) return;
            int key;
            if (target.IsCreature) key = target.Id;
            else if (target.IsAvatar) key = -(1 + target.ClientId * 4 + target.Slot);
            else return;
            // groundwork：只记录，不参与命中裁决（未来 lag compensation rewind 的输入）。
            m_lagCompensation.Record(key, serverTick, targetBody.Position, targetBody.Velocity);
            if (CombatTelemetry != null)
            {
                CombatTelemetry.LagCompSample = m_lagCompensation.Sample;
                CombatTelemetry.LagCompBufferFull = m_lagCompensation.BufferFull;
                CombatTelemetry.LagCompNotReady = m_lagCompensation.NotReady;
            }
        }

        private void RememberSentAttackIndex(long sequence, long attackIndex)
        {
            const int capacity = 256;
            if (m_sentAttackIndexes.ContainsKey(sequence)) return;
            m_sentAttackIndexes[sequence] = attackIndex;
            m_sentAttackOrder.Enqueue(sequence);
            while (m_sentAttackOrder.Count > capacity)
                m_sentAttackIndexes.Remove(m_sentAttackOrder.Dequeue());
        }

        private bool TryTakeSentAttackIndex(long sequence, out long attackIndex)
        {
            if (m_sentAttackIndexes.TryGetValue(sequence, out attackIndex))
            {
                m_sentAttackIndexes.Remove(sequence);
                return true;
            }
            attackIndex = -1L;
            return false;
        }

        private bool TryResolvePlayer(Project project, int sourceClientId, int slot,
            out ComponentPlayer player, out string reason)
        {
            player = null;
            reason = null;
            if (slot < 0 || slot > PlayerKey.MaxSlot)
            {
                reason = "槽位 " + slot + " 非法（0.." + PlayerKey.MaxSlot + "）";
                return false;
            }
            PlayerData data;
            if (sourceClientId == 0)
            {
                if (!m_spawner.TryGetLocalPlayer(project, slot, out data, out reason)) return false;
            }
            else
            {
                if (!PlayerKey.TryCreate(sourceClientId, slot, out PlayerKey key, out reason))
                    return false;
                if (!m_spawner.TryGet(key, out data) || data == null)
                {
                    reason = "主机没有玩家键 " + key + " 的联机角色（还没建好 / 已离开）";
                    return false;
                }
            }
            player = data.ComponentPlayer;
            if (player == null)
            {
                reason = "玩家键 (clientId=" + sourceClientId + ", slot=" + slot +
                    ") 的角色没有 ComponentPlayer";
                return false;
            }
            if (player.ComponentBody == null || player.ComponentMiner == null)
            {
                reason = "玩家键 (clientId=" + sourceClientId + ", slot=" + slot +
                    ") 的角色没有 ComponentBody / ComponentMiner";
                return false;
            }
            return true;
        }

        private bool TryResolveTarget(Project project, in MeleeTargetRef target,
            out ComponentBody body, out ComponentPlayer player, out string reason)
        {
            body = null;
            player = null;
            reason = null;
            if (target.IsCreature)
            {
                if (!m_creatures.TryGetHostCreatureToken(target.Id, out object token,
                        out reason))
                    return false;
                if (!(token is Entity entity) || !entity.IsAddedToProject)
                {
                    reason = "生物网络键 " + target.Id + " 的主机实体已不在世界里";
                    return false;
                }
                body = entity.FindComponent<ComponentBody>();
                if (body == null)
                {
                    reason = "生物网络键 " + target.Id + " 的主机实体没有 ComponentBody";
                    return false;
                }
                player = entity.FindComponent<ComponentPlayer>();
                return true;
            }
            if (target.IsAvatar)
            {
                if (!TryResolvePlayer(project, target.ClientId, target.Slot, out player, out reason))
                    return false;
                body = player.ComponentBody;
                return body != null;
            }
            reason = "空气挥击没有目标实体";
            return false;
        }

        private static bool IsToolLevelSufficient(Project project, ComponentPlayer player,
            ComponentMiner miner)
        {
            // Source: Survivalcraft/Game/ComponentMiner.cs:716-727（IsLevelSufficientForTool：
            //   非创造 + 冒险生存机制开启时，玩家等级 < 方块 PlayerLevelRequired → 引擎拒绝）
            SubsystemGameInfo info = project?.FindSubsystem<SubsystemGameInfo>(false);
            if (info == null || player == null || miner == null) return true;
            if (info.WorldSettings.GameMode == GameMode.Creative ||
                !info.WorldSettings.AreAdventureSurvivalMechanicsEnabled) return true;
            int contents = Game.Terrain.ExtractContents(miner.ActiveBlockValue);
            if (contents < 0 || contents >= BlocksManager.Blocks.Length) return true;
            Block block = BlocksManager.Blocks[contents];
            return block == null || player.PlayerData.Level >= block.PlayerLevelRequired;
        }

        // ---------------------------------------------------------------- 客户端结果表现

        public bool ApplyResult(in MeleeAttackResultData result, out string reason)
        {
            reason = null;
            Project project = m_project;
            if (project == null)
            {
                reason = "no-project";
                m_applyFailures++;
                return false;
            }
            // 主机回传的权威上限：客户端下一次预检用同一组值（至少不会比主机更宽松）。
            if (result.ServerMaxReach > 0f) m_cachedServerMaxReach = result.ServerMaxReach;
            if (result.HostEngineReach > 0f) m_cachedEngineReach = result.HostEngineReach;
            m_authoritativeTick = result.ServerTick;
            int localClientId = m_transport?.LocalClientId ?? -1;
            bool isAttacker = localClientId >= 0 && result.AttackerClientId == localClientId;

            // 预测表只对**本端发起**的攻击有效：别的玩家的结果可能用同一个 sequence 数值，
            // 拿别人的结果去确认 / 回滚本端的预测会把两条攻击混在一起（显式隔离）。
            long sentAttackIndex = -1L;
            bool hasSentIndex = false;
            if (isAttacker) hasSentIndex = TryTakeSentAttackIndex(result.Sequence, out sentAttackIndex);
            MeleePredictionRecord prediction = default;
            bool hadPrediction = false;
            if (isAttacker) hadPrediction = m_predictions.TryGet(result.Sequence, out prediction);
            bool suppressMissMarker = false;
            bool indexMatches = true;
            if (hadPrediction)
            {
                indexMatches = !hasSentIndex || result.AttackIndex == prediction.AttackIndex;
                bool resultMatches = result.Accepted && result.Hit == prediction.PredictedHit;
                bool mismatch = !indexMatches || !resultMatches;
                if (!result.Accepted)
                {
                    m_predictions.Resolve(result.Sequence, accepted: false, hit: false);
                    m_rollback++;
                    if (prediction.PredictedHit && CombatTelemetry != null)
                        CombatTelemetry.PredictedHitRollback++;
                    RemovePredictionMarker(result.Sequence, countMissing: true);
                    Problem("rollback", "主机判定 seq=" + result.Sequence + " 拒绝(" +
                        result.Reason + ") → 回滚预测表现（删除预测标记、不播命中音）");
                }
                else if (mismatch)
                {
                    m_predictions.Resolve(result.Sequence, accepted: true, hit: result.Hit);
                    m_rollback++;
                    if (prediction.PredictedHit && CombatTelemetry != null)
                        CombatTelemetry.PredictedHitRollback++;
                    if (indexMatches && CombatTelemetry != null)
                    {
                        // index 相同但命中结果不同（例如目标速度跨过 0.05 阈值）：
                        // 对账失败同样回滚表现并重同步，只是流位置本身不用回退。
                        CombatTelemetry.CombatRngMismatch++;
                        CombatTelemetry.CombatRngResync++;
                    }
                    RemovePredictionMarker(result.Sequence, countMissing: true);
                    Problem("rng-prediction-mismatch", "预测对账失败 seq=" + result.Sequence +
                        "：客户端 idx=" + prediction.AttackIndex + " u=" +
                        Fmt(prediction.PredictedU) + " p=" + Fmt(prediction.PredictedProbability) +
                        " 预测" + (prediction.PredictedHit ? "命中" : "Miss") + "；主机 idx=" +
                        result.AttackIndex + " " + (result.Hit ? "命中" : "Miss") +
                        " → 回滚预测表现并按主机流位置重同步");
                }
                else
                {
                    m_predictions.Resolve(result.Sequence, accepted: true, hit: result.Hit);
                    m_predictionConfirmed++;
                    if (prediction.PredictedHit)
                    {
                        if (CombatTelemetry != null) CombatTelemetry.PredictedHitConfirmed++;
                        // 预测标记保留到粒子自然消失；只把它从"待对账"表里摘掉。
                        m_predictionMarkers.Remove(result.Sequence);
                    }
                    else
                    {
                        if (CombatTelemetry != null) CombatTelemetry.PredictedMissConfirmed++;
                        // 预测的 Miss 与主机回执的 Miss 是同一个表现：保留预测标记，
                        // 只把句柄从待回滚表里摘掉；下面的权威 flags 不再重复播 "Miss"。
                        m_predictionMarkers.Remove(result.Sequence);
                        suppressMissMarker = true;
                    }
                }
            }

            // 结果里的 stream_index 是主机权威流位置：本端攻击一律以它为准（拒绝回退 / 跳缺失）。
            if (isAttacker && CombatRngClient != null && result.StreamIndex >= 0L)
            {
                long sentIndex = hasSentIndex ? sentAttackIndex : result.AttackIndex;
                CombatRngReconcile reconcile = CombatRngClient.Reconcile(result.AttackerSlot,
                    sentIndex, result.AttackIndex, result.StreamIndex, result.Accepted);
                if (reconcile == CombatRngReconcile.NoStream && !indexMatches &&
                    CombatTelemetry != null)
                    CombatTelemetry.CombatRngMismatch++;
            }

            uint flags = result.EffectFlags;
            if (!isAttacker && (flags & MeleeEffectFlags.Swoosh) != 0)
                PlaySwooshAt(result.Position, result.Direction);
            if ((flags & MeleeEffectFlags.ImpactSound) != 0)
                PlayImpactSound(result.Position);
            if ((flags & MeleeEffectFlags.DamagePopup) != 0)
            {
                AddHitMarker(result.Position, result.Direction, Vector3.Zero, Color.White,
                    (-result.Damage).ToString("0", CultureInfo.InvariantCulture));
                m_damagePopups++;
            }
            if ((flags & MeleeEffectFlags.MissMarker) != 0 && !suppressMissMarker)
            {
                AddHitMarker(result.Position, result.Direction, Vector3.Zero, Color.White, "Miss");
                m_missMarkers++;
                m_missPresented++;
            }
            if (!isAttacker) m_observedPresented++;
            if (result.IsRejection)
            {
                m_rejectionPresented++;
                string key = MeleeReasons.Key(result.Reason);
                switch (key)
                {
                    case MeleeReasons.OutOfReach:
                        m_outOfReachPresented++;
                        Problem("result-out-of-reach", "主机拒绝 out-of-reach（实测 " +
                            Fmt(result.MeasuredDistance) + " m / 允许 " +
                            Fmt(result.AllowedDistance) + " m）→ 客户端按未命中展示（不回血、不加伤害）");
                        break;
                    case MeleeReasons.Cooldown: m_cooldownPresented++; break;
                    case MeleeReasons.TargetMissing: m_targetMissingPresented++; break;
                    default:
                        Problem("result-reject:" + key, "主机拒绝近战 seq=" + result.Sequence +
                            "：" + result.Reason + " → 客户端按未命中展示");
                        break;
                }
            }
            return true;
        }

        public void NoteRequestNotSent(in MeleeAttackRequestData request, string reason)
        {
            m_predictionNotSent++;
            if (m_predictions.TryGet(request.Sequence, out MeleePredictionRecord prediction))
            {
                m_predictions.Resolve(request.Sequence, accepted: false, hit: false);
                m_rollback++;
                if (prediction.PredictedHit && CombatTelemetry != null)
                    CombatTelemetry.PredictedHitRollback++;
                RemovePredictionMarker(request.Sequence, countMissing: false);
            }
            if (TryTakeSentAttackIndex(request.Sequence, out _))
                CombatRngClient?.Rewind(request.AttackerSlot, request.AttackIndex);
            Problem("prediction-not-sent:" + (reason ?? "unknown"), "攻击请求 seq=" +
                request.Sequence + " 没有发出（" + (reason ?? "unknown") +
                "）→ 预测已回滚、战斗流 index 已撤销（客户端绝不本地扣血）");
        }

        private void PlaySwooshAt(Vector3 position, Vector3 direction)
        {
            Project project = m_project;
            SubsystemAudio audio = project?.FindSubsystem<SubsystemAudio>(false);
            if (audio == null) return;
            try
            {
                audio.PlaySound("Audio/Swoosh", 1f, m_audioRandom.Float(-0.2f, 0.2f), position,
                    3f, autoDelay: false);
                m_swooshes++;
            }
            catch (Exception e)
            {
                Problem("swoosh-result", "观测端挥击声播放失败（" + e.GetType().Name + " " +
                    e.Message + "）");
            }
        }

        private void PlayImpactSound(Vector3 position)
        {
            Project project = m_project;
            SubsystemAudio audio = project?.FindSubsystem<SubsystemAudio>(false);
            if (audio == null) return;
            try
            {
                // Source: Survivalcraft/Game/ComponentMiner.cs:519（Audio/Impacts/Body，体积 1，最小距离 4）
                audio.PlayRandomSound("Audio/Impacts/Body", 1f,
                    m_audioRandom.Float(-0.3f, 0.3f), position, 4f, autoDelay: false);
                m_impactSounds++;
            }
            catch (Exception e)
            {
                Problem("impact-sound", "命中音播放失败（" + e.GetType().Name + " " + e.Message + "）");
            }
        }

        // ---------------------------------------------------------------- 状态行

        public string Describe() => "meleeCaptured=" + m_captured + " meleeCapturedHost=" +
            m_capturedHost + " meleeCooldownBlocked=" + m_cooldownBlocked +
            " meleeClearInputFailed=" + m_clearInputFailures +
            " meleeRaycastFailed=" + m_raycastFailures +
            " digPressNotAttack=" + m_digPressNotAttack +
            " attackFromDigSuppressed=" + m_attackFromDigSuppressed +
            " swooshSuppressedDig=" + m_swooshSuppressedDig +
            " airSwingOnlyWhenIntended=" + m_airSwingOnlyWhenIntended +
            " meleeTargetUnmapped=" + m_targetUnmapped +
            " meleeLocalTargeted=" + m_localTargeted + " meleeLocalAir=" + m_localAir +
            " meleeOutOfReachLocal=" + m_localOutOfReach +
            " meleePredicted=" + m_predicted + " meleePredictionDisabled=" +
            m_predictionDisabled + " meleePredictionOverflow=" + m_predictionOverflow +
            " meleePredictionConfirmed=" + m_predictionConfirmed +
            " meleeRollback=" + m_rollback + " meleeRollbackMarkerFailed=" +
            m_rollbackMarkerMissing + " meleePredictionTimeout=" + m_predictionTimedOut +
            " meleePredictionNotSent=" + m_predictionNotSent +
            " meleeMissPredicted=" + m_missPresented + " meleeObservedPresented=" +
            m_observedPresented + " meleeRejectionPresented=" + m_rejectionPresented +
            " meleeOutOfReach=" + m_outOfReachPresented + " meleeCooldown=" +
            m_cooldownPresented + " meleeTargetMissing=" + m_targetMissingPresented +
            " meleeApplyFailed=" + m_applyFailures + " meleeSwoosh=" + m_swooshes +
            " meleeImpactSound=" + m_impactSounds + " meleeDamagePopup=" + m_damagePopups +
            " meleeMissMarker=" + m_missMarkers +
            " meleeHostExecuted=" + m_hostExecuted + " meleeHostHit=" + m_hostHit +
            " meleeHostMiss=" + m_hostMiss + " meleeHostRejected=" + m_hostRejected +
            " meleeHostAirSwing=" + m_hostAirSwing + " meleeHostInvulnerable=" +
            m_hostInvulnerable + " meleeHostNoDamage=" + m_hostNoDamage +
            " meleeHostEngineRefused=" + m_hostEngineRefused +
            " meleeHostEngineHitFailed=" + m_engineHitFailures +
            " meleeServerMax=" + Fmt(m_serverMaxReach) + "m meleeEngineReach=" +
            Fmt(HostEngineReach) + "m meleeClientCachedReach=" +
            Fmt(m_cachedEngineReach) + "m · " + m_predictions.Describe() +
            (CombatTelemetry != null ? " · " + CombatTelemetry.Describe() : "") +
            " · " + m_attackLog.Describe() +
            " · lagCompTargets=" + m_lagCompensation.TargetCount;

        // ---------------------------------------------------------------- 工具

        private double NowSeconds() =>
            m_project?.FindSubsystem<SubsystemTime>(false)?.GameTime ?? 0.0;

        private static string Fmt(float value) =>
            float.IsFinite(value)
                ? value.ToString("0.##", CultureInfo.InvariantCulture)
                : "NaN";

        private static string SafeName(ComponentPlayer player)
        {
            try { return player?.PlayerData?.Name ?? "?"; }
            catch (Exception) { return "?"; }
        }

        /// <summary>
        /// 挖掘按下审计日志（10 s 限流）。这是**正常行为**（单机同样不播攻击声），用 Info
        /// 而不是 Warn，避免设备检查单把 dig 误判成故障；计数仍会每 10 s 出现在状态行里。
        /// </summary>
        private void LogDigPressNotAttack(ComponentInput input, int slot)
        {
            long nowMs = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
            if (nowMs - m_lastDigPressLogMs < DigPressLogMs) return;
            m_lastDigPressLogMs = nowMs;
            bool digHeld = false;
            try { digHeld = input != null && input.PlayerInput.Dig.HasValue; }
            catch (Exception) { }
            m_diagnostics?.Info(m_owner, "左键按下没有引擎等价的身体目标 → 判为挖掘按下而不是攻击" +
                "（slot=" + slot + " digHeld=" + digHeld + "）：不接管 Hit、不发攻击请求、" +
                "不消耗共享战斗流、不播 Poke/Swoosh；Hit 原样留给原生分支，Dig 照常执行" +
                "（digPressNotAttack=" + m_digPressNotAttack +
                " attackFromDigSuppressed=" + m_attackFromDigSuppressed +
                " swooshSuppressedDig=" + m_swooshSuppressedDig +
                " airSwingOnlyWhenIntended=" + m_airSwingOnlyWhenIntended + "）");
        }

        private void Problem(string key, string message)
        {
            long nowMs = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_owner, message);
        }

        private void LogOnce(string key, string message)
        {
            if (!m_loggedOnce.Add(key)) return;
            m_diagnostics?.Info(m_owner, message);
        }
    }
}
