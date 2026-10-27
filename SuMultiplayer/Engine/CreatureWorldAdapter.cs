using System;
using System.Collections.Generic;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Creatures;
using SuMultiplayer.Modules;
using TemplatesDatabase;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 生物世界的引擎适配器：把 <see cref="ICreatureWorld"/> 端口落到真实的
    /// <c>SubsystemSpawn</c> / <c>SubsystemCreatureSpawn</c> / <c>SubsystemBodies</c> 上。
    ///
    /// 这里是**旧实现逻辑的逐段移植**，不是重新设计：
    ///   · <c>BeginRemoteSimulationViewScope</c> ← 旧 SuSubsystemCreatureSpawn 的宿主分支
    ///     （Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:834-867）；
    ///   · <c>BeginRemoteDespawnProtectionScope</c> ← 旧 SuSubsystemSpawn 的宿主分支
    ///     （同文件 :1041-1089）；
    ///   · <c>MaintainHostPopulation</c> ← 旧 MaintainRemoteCreatureSpawning
    ///     （同文件 :869-971，含 1 秒节流、48 米候选块、26 只上限、恢复 SpawnsData）；
    ///   · <c>TryCreateReplica</c> / <c>TryApplyReplicaState</c> ← 旧 EnsureRemoteAnimal /
    ///     HandleAnimalBodyUpdate（Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs）。
    ///
    /// 没有任何静态运行时状态：适配器由组合根创建，子系统的 Coordinator 由模块每帧注入。
    /// </summary>
    public sealed class CreatureWorldAdapter : ICreatureWorld, ICreatureRegionSource
    {
        private readonly IModParentField m_parentField;
        private readonly IModParentMethod m_parentMethod;
        private readonly NetworkAvatarSpawner m_avatars;
        private readonly IModuleDiagnostics m_diagnostics;

        private Project m_project;
        private SuSpawnSubsystem m_spawnSubsystem;
        private SuCreatureSpawnSubsystem m_creatureSpawnSubsystem;
        private bool m_clientReplicaMode;
        /// <summary>
        /// 客户端世界存档里自带的**非生物可骑乘实体**（目前只有船）在哪个 Project 上已经清理过。
        /// 客户端下载的世界存档包含主机世界的船；主机现在也通过生物复制链下发权威船
        ///（ComponentBoat），本地存档船如果不清理，进房后客户端会看到两条船（一条永不同步）。
        /// 2026-10-26 实机：客户端进房后船显示为两个，主机端只有一个。
        /// </summary>
        private Project m_nativeMountSweepProject;
        private long m_nativeMountsRemoved;
        /// <summary>船副本每帧速度清零次数（骑船视野抖动修复验证用）。</summary>
        private long m_boatReplicaVelocityZeroed;
        /// <summary>被识别为"本机玩家骑/站坐骑"的复制体命中次数（始终平滑插值验证用）。</summary>
        private long m_localMountReplicaHits;
        /// <summary>
        /// 客户端复制体**每帧表现泵**：挂在 SubsystemUpdate 上（UpdateOrder=Locomotion=1，
        /// 早于 Body=2 / 相机 Views=200）。表现写在世界更新内，骑手父体跟随与相机才能吃到
        /// 当帧马位姿；旧实现放在 Frame.Update（世界 tick 之后）→ 每帧差一帧 → 骑乘看马"抖"。
        /// </summary>
        private sealed class ReplicaPresentationUpdateable : IUpdateable
        {
            private readonly CreatureWorldAdapter m_owner;
            public ReplicaPresentationUpdateable(CreatureWorldAdapter owner) { m_owner = owner; }
            public UpdateOrder UpdateOrder => UpdateOrder.Locomotion;
            public void Update(float dt) => m_owner.PumpReplicaPresentations(dt);
        }

        private ReplicaPresentationUpdateable m_presentationUpdateable;
        /// <summary>
        /// 模型动画前的第二次写入：ComponentBody.Update（Body=2）会摩擦/重力改写客户端复制体的
        /// Velocity，而四足/鸟类模型的移动动画读 body.Velocity；只在 Locomotion=1 写一次会被
        /// Body 更新冲掉，客户端动物就“移动但腿不动”。本更新器在 CreatureModels-1 再写一次
        /// 位置/速度，并把 AttackOrder/FeedOrder 放到模型 Update 之前（模型随后会消费复位）。
        /// </summary>
        private sealed class ReplicaAnimationUpdateable : IUpdateable
        {
            private readonly CreatureWorldAdapter m_owner;
            public ReplicaAnimationUpdateable(CreatureWorldAdapter owner) { m_owner = owner; }
            public UpdateOrder UpdateOrder => (UpdateOrder)((int)UpdateOrder.CreatureModels - 1);
            public void Update(float dt) => m_owner.RefreshReplicaAnimationState(dt);
        }

        /// <summary>
        /// 模型 Update 之后再写一次 MovementAnimationPhase。
        /// 引擎模型的步态在 Update 中按 body.Velocity 计算；客户端复制体的 velocity 即使被我们
        /// 在模型前恢复，仍会在某些物理/碰撞帧被清零，且慢速低于 0.2 阈值时模型主动把相位清零。
        /// 这里用表现层真实位移独立推进相位，渲染阶段（AnimateCreature）直接使用该相位，
        /// 不再依赖引擎对复制体速度的判定。
        /// </summary>
        private sealed class ReplicaPhaseUpdateable : IUpdateable
        {
            private readonly CreatureWorldAdapter m_owner;
            public ReplicaPhaseUpdateable(CreatureWorldAdapter owner) { m_owner = owner; }
            public UpdateOrder UpdateOrder => (UpdateOrder)((int)UpdateOrder.CreatureModels + 1);
            public void Update(float dt) => m_owner.RefreshReplicaAnimationPhase(dt);
        }

        private ReplicaPhaseUpdateable m_phaseUpdateable;
        private ReplicaAnimationUpdateable m_animationUpdateable;
        private SubsystemUpdate m_presentationSubsystem;
        private bool m_presentationAttached;

        /// <summary>true = 引擎侧已接管每帧推进（模块 Tick 不要再推一次）。</summary>
        public bool ExternalPresentationPump => m_presentationAttached;

        /// <summary>模块注入的每帧表现推进（引擎时钟秒, dt）。</summary>
        public Action<double, float> ReplicaPresentationPump { get; set; }

        private void PumpReplicaPresentations(float dt)
        {
            Action<double, float> pump = ReplicaPresentationPump;
            if (pump == null) return;
            pump(global::Engine.Time.RealTime, dt);
        }
        private bool m_missingSubsystemLogged;
        private double m_nextMaintainTime;
        private int m_maintainCursor;

        private readonly Dictionary<int, Entity> m_replicas = new Dictionary<int, Entity>();
        /// <summary>每帧表现位置/速度（Body 更新后、模型更新前重写；只由 TryApplyReplicaPresentation 写）。</summary>
        private sealed class ReplicaPresentationSnapshot
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Velocity;
        }
        private readonly Dictionary<int, ReplicaPresentationSnapshot> m_replicaPresentation =
            new Dictionary<int, ReplicaPresentationSnapshot>();
        /// <summary>模型动画诊断用的上一帧表现位置（δ位置/δ时间 = 可见移动速度）。</summary>
        private readonly Dictionary<int, Vector3> m_lastAnimationPosition = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> m_replicaAnimationPhase = new Dictionary<int, float>();
        /// <summary>MovementAnimationPhase 自动属性 backing field（类型 → FieldInfo）。</summary>
        private readonly Dictionary<Type, FieldInfo> m_movementPhaseFields = new Dictionary<Type, FieldInfo>();
        private bool m_movementPhaseFieldLogged;
        /// <summary>客户端复制体下一次允许合成 idle 叫声的时刻（Time.RealTime 口径）。</summary>
        private readonly Dictionary<int, double> m_nextClientIdleSound = new Dictionary<int, double>();
        private readonly global::Engine.Random m_clientIdleRandom = new global::Engine.Random();
        private readonly Dictionary<int, string> m_replicaTemplates = new Dictionary<int, string>();
        /// <summary>
        /// 客户端复制体最近一次活动位（2026-10-26 攻击动画修复）。TryApplyReplicaState 写入；
        /// TryApplyReplicaPresentation 每帧用它驱动 ComponentCreatureModel.AttackOrder。
        /// </summary>
        private readonly Dictionary<int, CreatureActivity> m_replicaActivity =
            new Dictionary<int, CreatureActivity>();
        /// <summary>客户端复制体最近一次 FeedOrder（模型每帧会消费复位，必须持续回写）。</summary>
        private readonly Dictionary<int, bool> m_replicaFeedOrder = new Dictionary<int, bool>();
        /// <summary>客户端复制体最近一次主机 grounded 状态（四足模型迈腿条件）。</summary>
        private readonly Dictionary<int, bool> m_replicaGrounded = new Dictionary<int, bool>();
        /// <summary>主机模型私有 m_feedFactor 的反射缓存（类型 → 字段；没有该模型的类型不缓存）。</summary>
        private readonly Dictionary<Type, FieldInfo> m_feedFactorFields = new Dictionary<Type, FieldInfo>();

        // 主机：每只生物上一次被枚举时的状态（判断"在动 / 刚受伤"用；只影响同步频率，不上线）。
        private readonly Dictionary<object, HostCreatureActivityState> m_hostActivityStates =
            new Dictionary<object, HostCreatureActivityState>(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> m_seenHostTokens =
            new HashSet<object>(ReferenceEqualityComparer.Instance);
        private double m_now;
        /// <summary>主机游戏时钟（TotalElapsedGameTime；出生渐变进度的口径，2026-10-26）。</summary>
        private double m_gameTime;

        private long m_spawnedByMaintenance;
        private long m_restoredRecords;
        private long m_overflowTrimmed;
        private long m_unassignedRemoved;
        private long m_maintenanceRuns;
        private long m_viewScopeRuns;
        private long m_despawnScopeRuns;
        private long m_healthApplyFailures;

        // 反射读取的失败分项：每个协议/帧路径都有独立计数，日志只按限流写（绝不每帧）。
        private long m_despawnProtectionFailures;
        private long m_viewScopeFailures;
        private long m_areaByBodyReadFailures;
        /// <summary>
        /// m_spawnEntityTemplateName 的安全绑定：成员实际类型必须是 System.String；
        /// null 值 = 未赋值（合法），不是类型错误。类型/读取异常只禁用该反射路径一次。
        /// </summary>
        private readonly ReflectiveStringMember m_shapeshiftMember =
            new ReflectiveStringMember("m_spawnEntityTemplateName");

        // ---------------------------------------------------------------- 客户端复制体：分项计数
        //
        // 这些计数是"复制体到底有没有真的进世界"的唯一可审计口径，逐段对应：
        //   尝试创建 → 实体创建成功且**校验**已挂进 Project/SubsystemBodies（attached）
        //   → 每帧表现层写入成功（presentation）→ 键不在保留集合被裁掉（removedTrimmed）
        //   → 世界里不在分配集合的其它生物实体被清掉（removedUnassigned）
        //   → 创建/挂载失败（按原因分开，绝不把失败算成成功）。
        private long m_createAttempts;
        private long m_createDuplicate;
        private long m_createdRaw;
        private long m_createdAttached;
        private long m_createSkippedNoWorld;
        private long m_createRejectedRecord;
        private long m_createRejectedTemplate;
        private long m_createExceptions;
        private long m_attachFailed;
        private long m_presentationApplied;
        private long m_lookAngleWriteFailures;
        /// <summary>客户端复制体被写入攻击动作的帧数（2026-10-26 攻击动画修复；实测看这个涨）。</summary>
        private long m_attackAnimApplied;
        private long m_presentationFailed;
        private long m_removedTrimmed;
        private long m_entityLost;
        private long m_keyMismatch;

        /// <summary>复制体应该落在的半径（米；由模块告知，只用于可见性对账，不改变分配）。</summary>
        private float m_clientRegionRadius = CreatureAllocation.DefaultRegionRadiusMeters;

        // 最近一次可见性对账结果（状态行/日志用；全部是"采样值"，不是累积计数）。
        private long m_visibilityChecks;
        private int m_replicasInRegion;
        private int m_replicasOutOfRegion;
        private float m_nearestReplicaDistance = float.PositiveInfinity;
        private float m_farthestReplicaDistance = float.PositiveInfinity;

        /// <summary>限流日志：key → 上次写日志的时间（毫秒，m_now 口径）。</summary>
        private readonly Dictionary<string, long> m_lastLogTimes = new Dictionary<string, long>(StringComparer.Ordinal);

        public CreatureWorldAdapter(IModParentField parentField, IModParentMethod parentMethod,
            NetworkAvatarSpawner avatars, IModuleDiagnostics diagnostics)
        {
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_parentMethod = parentMethod ?? throw new ArgumentNullException(nameof(parentMethod));
            m_avatars = avatars;
            m_diagnostics = diagnostics;
        }

        /// <summary>两个生成子系统读取它：客户端 = 不跑原生生成。</summary>
        public bool SuppressNativeSpawning => m_clientReplicaMode;

        /// <summary>主机分配给本端的区域半径（米）：只用于"复制体是否真的在本地玩家周围"的对账。</summary>
        public void ConfigureClientRegion(float radiusMeters)
        {
            m_clientRegionRadius = radiusMeters > 0f
                ? radiusMeters
                : CreatureAllocation.DefaultRegionRadiusMeters;
        }

        public long ReplicaCount => m_replicas.Count;

        /// <summary>
        /// 世界是否真的可操作：Project 已加载、且没有被换新/卸载。
        /// 客户端在世界传输期间会先收到生物全量快照——模块靠它把整批记录延后，
        /// 而不是逐条创建失败（实机 createFail=50 / orphanUpdate=550 的根因）。
        /// </summary>
        public bool HasWorld
        {
            get
            {
                Project project = m_project;
                return project != null && ReferenceEquals(project, GameManager.Project);
            }
        }

        /// <summary>主机侧活动状态缓存（按实体对象身份；实体销毁后由枚举清理掉）。</summary>
        private sealed class HostCreatureActivityState
        {
            public bool HasPrevious;
            public Vector3 Position;
            public Quaternion Rotation = Quaternion.Identity;
            public float Health = 1f;
            public double HighPriorityUntil;
            /// <summary>
            /// 攻击动作的"粘滞"截止时刻（2026-10-26 攻击动画修复）。主机 50Hz 扫描发生在
            /// 世界更新之后：模型 Update 已经消费并把 AttackOrder / IsAttackHitMoment 复位，
            /// 单帧采样只在攻击周期穿越命中点时偶尔看到 true。观察到了就保持一段时间，
            /// 让客户端复制的攻击动画连续播完（停止攻击后最多多播 ≤1s = 收招）。
            /// </summary>
            public double AttackingUntil;
        }

        // ------------------------------------------------------------------ 生命周期 / 绑定

        public bool BeginTick(bool isHost, bool clientReplicaMode, double now, out string reason)
        {
            reason = string.Empty;
            m_now = now;
            SubsystemGameInfo gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            m_gameTime = gameInfo?.TotalElapsedGameTime ?? m_now;
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                // Project 换新（整图重下 / 重进世界）：旧实体已经随旧 Project 销毁，只丢掉引用。
                m_replicas.Clear();
                m_replicaTemplates.Clear();
                m_nextClientIdleSound.Clear();
                m_replicaActivity.Clear();
                m_hostActivityStates.Clear();
                m_seenHostTokens.Clear();
                m_spawnSubsystem = null;
                m_creatureSpawnSubsystem = null;
                m_missingSubsystemLogged = false;
                m_project = project;
            }
            m_clientReplicaMode = clientReplicaMode;
            if (project == null)
            {
                reason = "当前没有世界（GameManager.Project 为空）";
                return false;
            }
            EnsureSubsystems(project, clientReplicaMode);
            AttachPresentationPump(project, clientReplicaMode);
            if (clientReplicaMode && !ReferenceEquals(project, m_nativeMountSweepProject))
            {
                m_nativeMountSweepProject = project;
                SweepNativeMountDuplicates(project);
            }
            if (!clientReplicaMode && m_replicas.Count > 0)
            {
                RemoveAllReplicas("切换到非复制模式（主机 / 单机），清掉客户端复制体");
            }
            return true;
        }

        /// <summary>
        /// 客户端复制体表现泵的挂载/摘除（世界更新内每帧推进；见 ReplicaPresentationUpdateable）。
        /// 非客户端模式 / 换 Project 时摘掉，绝不留一个引用旧世界子系统的 updateable。
        /// </summary>
        private void AttachPresentationPump(Project project, bool clientReplicaMode)
        {
            if (!clientReplicaMode)
            {
                DetachPresentationPump();
                return;
            }
            SubsystemUpdate update = project.FindSubsystem<SubsystemUpdate>(false);
            if (update == null)
            {
                DetachPresentationPump();
                if (!m_missingSubsystemLogged)
                {
                    m_missingSubsystemLogged = true;
                    Warn("客户端没有 SubsystemUpdate → 复制体表现只能退回 Frame.Update 推进" +
                        "（骑乘时可能有 1 帧抖动，明确记录不假装成功）");
                }
                return;
            }
            if (m_presentationAttached && ReferenceEquals(m_presentationSubsystem, update)) return;
            DetachPresentationPump();
            m_presentationSubsystem = update;
            m_presentationUpdateable = new ReplicaPresentationUpdateable(this);
            update.AddUpdateable(m_presentationUpdateable);
            m_animationUpdateable = new ReplicaAnimationUpdateable(this);
            update.AddUpdateable(m_animationUpdateable);
            m_phaseUpdateable = new ReplicaPhaseUpdateable(this);
            update.AddUpdateable(m_phaseUpdateable);
            m_presentationAttached = true;
        }

        private void DetachPresentationPump()
        {
            if (!m_presentationAttached) return;
            try { m_presentationSubsystem?.RemoveUpdateable(m_presentationUpdateable); } catch (Exception) { }
            try { m_presentationSubsystem?.RemoveUpdateable(m_animationUpdateable); } catch (Exception) { }
            try { m_presentationSubsystem?.RemoveUpdateable(m_phaseUpdateable); } catch (Exception) { }
            m_presentationAttached = false;
            m_presentationSubsystem = null;
            m_presentationUpdateable = null;
            m_animationUpdateable = null;
            m_phaseUpdateable = null;
        }

        /// <summary>
        /// 客户端进房（或换世界）时清掉**世界存档自带**的非生物载具（船）：它们是主机世界的
        /// 旧副本，主机现在通过生物复制链下发权威船，本地副本必须删掉，否则世界里出现两条船
        ///（本地那条不同步，实机 2026-10-26）。只删"不在复制体集合里、没人骑"的 ComponentBoat；
        /// 普通动物由 TrimReplicasTo 里的 ComponentCreature 清理负责。
        /// </summary>
        private void SweepNativeMountDuplicates(Project project)
        {
            if (project == null) return;
            var replicaEntities = new HashSet<Entity>();
            foreach (KeyValuePair<int, Entity> pair in m_replicas)
                if (pair.Value != null) replicaEntities.Add(pair.Value);
            var entities = new List<Entity>();
            foreach (Entity entity in project.Entities) entities.Add(entity);
            int removed = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                if (entity == null || !entity.IsAddedToProject || entity.Project != project) continue;
                if (replicaEntities.Contains(entity)) continue;
                if (entity.FindComponent<ComponentBoat>() == null) continue;
                ComponentMount mount = entity.FindComponent<ComponentMount>();
                if (mount != null && mount.Rider != null) continue;
                project.RemoveEntity(entity, disposeEntity: true);
                removed++;
                m_nativeMountsRemoved++;
            }
            if (removed > 0)
            {
                m_diagnostics?.Info("creature-spawn", "客户端清理了 " + removed +
                    " 个世界存档里自带的可骑乘非生物实体（本地旧船副本；权威船由主机复制链下发，避免显示两条船）");
            }
        }

        private void EnsureSubsystems(Project project, bool clientReplicaMode)
        {
            if (m_spawnSubsystem == null)
            {
                m_spawnSubsystem = project.FindSubsystem<SuSpawnSubsystem>(false);
                if (m_spawnSubsystem != null) m_spawnSubsystem.Coordinator = this;
            }
            if (m_creatureSpawnSubsystem == null)
            {
                m_creatureSpawnSubsystem = project.FindSubsystem<SuCreatureSpawnSubsystem>(false);
                if (m_creatureSpawnSubsystem != null) m_creatureSpawnSubsystem.Coordinator = this;
            }
            if (clientReplicaMode && !m_missingSubsystemLogged &&
                (m_spawnSubsystem == null || m_creatureSpawnSubsystem == null))
            {
                m_missingSubsystemLogged = true;
                Warn("客户端没有找到注入后的生成子系统（Spawn=" +
                    (m_spawnSubsystem == null ? "缺失" : "已注入") + " / CreatureSpawn=" +
                    (m_creatureSpawnSubsystem == null ? "缺失" : "已注入") +
                    "）→ 引擎的原生随机生成无法被抑制；已收到的复制体仍会显示，" +
                    "不在主机分配集合里的生物会被每秒清理并计数（明确记录，不假装成功）");
            }
        }

        // ------------------------------------------------------------------ 客户端区域（ICreatureRegionSource）

        public IReadOnlyList<int> ClientIds
        {
            get
            {
                if (m_avatars == null) return Array.Empty<int>();
                var ids = new List<int>();
                foreach (int clientId in m_avatars.ClientIds) ids.Add(clientId);
                return ids;
            }
        }

        public bool TryGetClientPosition(int clientId, out Vector3 position, out string reason)
        {
            position = Vector3.Zero;
            reason = string.Empty;
            if (m_avatars == null)
            {
                reason = "联机角色表不可用（NetworkAvatarSpawner 未注入）";
                return false;
            }
            return m_avatars.TryGetAvatarCell(clientId, out position, out reason);
        }

        // ------------------------------------------------------------------ 主机：收集生物

        public int CaptureHostCreatures(List<CreatureStateRecord> target, out string reason)
        {
            reason = string.Empty;
            if (target == null) throw new ArgumentNullException(nameof(target));
            Project project = m_project;
            if (project == null) { reason = "当前没有世界"; return -1; }
            SubsystemBodies bodies = project.FindSubsystem<SubsystemBodies>(false);
            if (bodies == null) { reason = "世界没有 SubsystemBodies"; return -1; }
            int count = 0;
            m_seenHostTokens.Clear();
            foreach (ComponentBody body in bodies.Bodies)
            {
                Entity entity = body?.Entity;
                if (entity == null) continue;
                ComponentCreature creature = entity.FindComponent<ComponentCreature>();
                // 2026-10-26 客户端放船同步修复：生物复制链同时承担"可骑乘非生物实体"（船）的复制。
                // 船没有 ComponentCreature，但同样有 ComponentBody + ComponentMount；客户端需要
                // 一个 body 来挂骑手/渲染。主机 key → 客户端的同一条分配 / 快照 / 裁剪链路直接复用，
                // 无需再新增一套实体复制协议（船体位姿由主机物理驱动，客户端只表现）。
                ComponentBoat boat = creature == null ? entity.FindComponent<ComponentBoat>() : null;
                if (creature == null && boat == null) continue;
                if (entity.FindComponent<ComponentPlayer>() != null) continue;
                string template = entity.ValuesDictionary?.DatabaseObject?.Name;
                if (string.IsNullOrWhiteSpace(template)) continue;

                m_seenHostTokens.Add(entity);
                if (!m_hostActivityStates.TryGetValue(entity, out HostCreatureActivityState activityState))
                {
                    activityState = new HostCreatureActivityState();
                    m_hostActivityStates.Add(entity, activityState);
                }
                float health = creature?.ComponentHealth != null ? creature.ComponentHealth.Health : 1f;
                CreatureActivity activity = creature != null
                    ? ComputeHostActivity(entity, creature, body, activityState, health, m_now)
                    : ComputeBoatActivity(entity, body, activityState);
                // 静止的生物速度常有重力/碰撞残余（位置一动不动、速度却在抖）。把它按 0 发布，
                // 否则客户端会拿这份速度外推，静止的动物会往一个方向漂、下一帧再被拉回来。
                // Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs（静止拾取物同一处理）
                Vector3 publishedVelocity = (activity & CreatureActivity.Moving) != 0
                    ? body.Velocity
                    : Vector3.Zero;
                activityState.HasPrevious = true;
                activityState.Position = body.Position;
                activityState.Rotation = body.Rotation;
                activityState.Health = health;

                // 复用调用方列表里的记录对象（主机 50 Hz 扫描，不要每次都分配新对象）。
                CreatureStateRecord record;
                if (count < target.Count) record = target[count];
                else
                {
                    record = new CreatureStateRecord();
                    target.Add(record);
                }
                record.Token = entity;
                record.TemplateName = template;
                record.Position = body.Position;
                record.Rotation = body.Rotation;
                record.Velocity = publishedVelocity;
                record.Health = health;
                record.Activity = activity;
                // 出生渐变进度（蛋化动物透明度修复，2026-10-26）：
                // ComponentSpawn.SpawnDuration 固定 2s，Opacity = (游戏时钟 − SpawnTime) / 2s。
                // SpawnTime 是 TotalElapsedGameTime 口径 → 进度也必须用**游戏时钟**差值
                //（不能用 m_now/RealTime——两个时钟差任意大，进度会是随机数）。
                ComponentSpawn spawn = entity.FindComponent<ComponentSpawn>();
                record.SpawnProgress = spawn == null
                    ? 0f
                    : MathUtils.Clamp((float)(m_gameTime - spawn.SpawnTime), 0f, 60f);
                // 2026-10-26 装鞍半透明修复：把主机**真实** SpawnDuration 一起下发（鞍/船=0，生物=2s）。
                record.SpawnDuration = spawn == null ? 0f : MathUtils.Clamp(spawn.SpawnDuration, 0f, 60f);
                // 头部朝向（马/驴/骆驼被骑转向时头要转；客户端复制体没有 locomotion updateable）。
                record.LookAngles = creature?.ComponentLocomotion?.LookAngles ?? Vector2.Zero;
                record.FeedOrder = IsHostFeeding(creature?.ComponentCreatureModel);
                record.Grounded = body.StandingOnValue.HasValue || body.StandingOnBody != null;
                count++;
            }
            if (count < target.Count) target.RemoveRange(count, target.Count - count);
            // 实体销毁后清掉活动状态，避免按对象身份无限增长（实体引用本身不会复用）。
            if (m_hostActivityStates.Count > m_seenHostTokens.Count)
            {
                var stale = new List<object>();
                foreach (object token in m_hostActivityStates.Keys)
                {
                    if (!m_seenHostTokens.Contains(token)) stale.Add(token);
                }
                for (int i = 0; i < stale.Count; i++) m_hostActivityStates.Remove(stale[i]);
            }
            if (count == 0) reason = "世界里当前没有非玩家生物";
            return count;
        }

        /// <summary>
        /// 主机：船的活动标记（只用于选同步档位）。船没有 AI/模型，只按位移/转向/速度判 Moving；
        /// 不发布静止时的残余速度（同生物处理，避免客户端外推漂移）。
        /// </summary>
        private static CreatureActivity ComputeBoatActivity(Entity entity, ComponentBody body,
            HostCreatureActivityState state)
        {
            bool positionMoved = !state.HasPrevious ||
                Vector3.DistanceSquared(body.Position, state.Position) >
                    PositionMovingEpsilonSquared;
            bool rotationMoved = !state.HasPrevious ||
                1f - MathUtils.Abs(Quaternion.Dot(body.Rotation, state.Rotation)) >
                    RotationMovingThreshold;
            bool velocityMoved = body.Velocity.LengthSquared() > 0.01f;
            CreatureActivity activity = positionMoved || rotationMoved || velocityMoved
                ? CreatureActivity.Moving
                : CreatureActivity.None;
            ComponentMount mount = entity?.FindComponent<ComponentMount>();
            if (mount != null && mount.Rider != null) activity |= CreatureActivity.Ridden;
            return activity;
        }

        /// <summary>0.8° 的转向（1 - |dot| ≈ (θ/2)²）就算"在动"：原地转头也要按距离档平滑同步。</summary>
        private const float RotationMovingThreshold = 0.00005f;

        /// <summary>5 mm 的位移就算"在动"（与 CreatureSyncCadence.PositionEpsilonSquared 同口径）。</summary>
        private const float PositionMovingEpsilonSquared = 0.000025f;

        /// <summary>
        /// 计算一只生物的活动标记（决定同步档位）。逐条对应旧实现的判定：
        ///   · Aggro   ← ComponentChaseBehavior.IsActive && Target != null；
        ///   · Attacking ← ComponentCreatureModel.AttackOrder / IsAttackHitMoment（采样点在模型
        ///     Update 之后，单帧大概率已复位 → 用 1s 粘滞窗口保持，见 HostCreatureActivityState.AttackingUntil）；
        ///   · Fleeing ← 逃跑类 ComponentBehavior 处于激活状态；
        ///   · Damaged ← 生命值比上一次枚举低 0.001 以上，并把高优先级保持 3 秒
        ///               （旧实现 metadata.HighPriorityUntil = now + 3.0）；
        ///   · Moving  ← 位置/朝向变化超过 epsilon，或速度超过 0.1 m/s。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:SendAdaptiveAnimalUpdates
        /// </summary>
        /// <summary>
        /// 主机是否正在播放吃东西动画。
        /// ComponentCreatureModel.FeedOrder 是**单帧命令**：行为在 Update 里置 true，
        /// 模型 Update 末尾立刻复位 false；50Hz 扫描看到的几乎永远是 false（所以客户端
        /// 一直没有吃东西动画）。四足/走禽模型把当前进食强度保存在私有 m_feedFactor
        /// （2/s 上升、2/s 衰减），读它才是持久的“正在吃”状态。
        /// </summary>
        private bool IsHostFeeding(ComponentCreatureModel model)
        {
            if (model == null) return false;
            if (model.FeedOrder) return true;
            Type type = model.GetType();
            if (!m_feedFactorFields.TryGetValue(type, out FieldInfo field))
            {
                field = null;
                for (Type current = type; current != null && current != typeof(ComponentCreatureModel);
                     current = current.BaseType)
                {
                    field = current.GetField("m_feedFactor",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (field != null) break;
                }
                m_feedFactorFields[type] = field;
            }
            if (field == null) return false;
            try
            {
                object value = field.GetValue(model);
                return value is float factor && factor > 0.05f;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private CreatureActivity ComputeHostActivity(Entity entity, ComponentCreature creature,
            ComponentBody body, HostCreatureActivityState state, float health, double now)
        {
            bool damagedNow = state.HasPrevious && health < state.Health - 0.001f;
            if (damagedNow) state.HighPriorityUntil = now + 3.0;
            bool damaged = now < state.HighPriorityUntil;

            bool positionMoved = !state.HasPrevious ||
                Vector3.DistanceSquared(body.Position, state.Position) >
                    PositionMovingEpsilonSquared;
            bool rotationMoved = !state.HasPrevious ||
                1f - MathUtils.Abs(Quaternion.Dot(body.Rotation, state.Rotation)) >
                    RotationMovingThreshold;
            bool velocityMoved = body.Velocity.LengthSquared() > 0.01f;
            bool moving = positionMoved || rotationMoved || velocityMoved;

            ComponentChaseBehavior chase = entity.FindComponent<ComponentChaseBehavior>();
            bool aggro = chase != null && chase.IsActive && chase.Target != null;
            // 被玩家骑乘的坐骑抬到 Priority 档（50ms）：客户端看自己骑的马是最近距离、最显眼的
            // 对象，10Hz 近距档在插值下能看出"一小段一小段"（2026-10-26 骑乘平滑修复）。
            ComponentMount mount = entity.FindComponent<ComponentMount>();
            bool ridden = mount != null && mount.Rider != null;
            ComponentCreatureModel model = creature.ComponentCreatureModel;
            bool attackingNow = model != null && (model.AttackOrder || model.IsAttackHitMoment);
            if (attackingNow) state.AttackingUntil = now + 1.0;
            bool attacking = now < state.AttackingUntil;
            bool fleeing = false;
            foreach (ComponentBehavior behavior in entity.FindComponents<ComponentBehavior>())
            {
                if (behavior == null || !behavior.IsActive) continue;
                if (!IsFleeingBehavior(behavior)) continue;
                fleeing = true;
                break;
            }

            CreatureActivity activity = CreatureActivity.None;
            if (moving) activity |= CreatureActivity.Moving;
            if (aggro) activity |= CreatureActivity.Aggro;
            if (attacking) activity |= CreatureActivity.Attacking;
            if (fleeing) activity |= CreatureActivity.Fleeing;
            if (damaged) activity |= CreatureActivity.Damaged;
            if (ridden) activity |= CreatureActivity.Ridden;
            return activity;
        }

        /// <summary>
        /// 逃离类行为（引擎里没有统一的 ComponentFleeBehavior，按旧实现的行为类逐个判）。
        /// Source: Survivalcraft/Game/ComponentRunAwayBehavior.cs / ComponentMoveAwayBehavior.cs /
        ///         ComponentFlyAwayBehavior.cs / ComponentAvoidPlayerBehavior.cs /
        ///         ComponentSwimAwayBehavior.cs
        /// </summary>
        private static bool IsFleeingBehavior(ComponentBehavior behavior) =>
            behavior is ComponentRunAwayBehavior || behavior is ComponentMoveAwayBehavior ||
            behavior is ComponentFlyAwayBehavior || behavior is ComponentAvoidPlayerBehavior ||
            behavior is ComponentSwimAwayBehavior;

        // ------------------------------------------------------------------ 主机：远端生物维持

        public void MaintainHostPopulation(IReadOnlyList<CreatureRegion> regions, double now, out string reason)
        {
            reason = string.Empty;
            if (m_clientReplicaMode) return;
            Project project = m_project;
            if (project == null) { reason = "当前没有世界"; return; }
            if (regions == null || regions.Count == 0) return;
            if (now < m_nextMaintainTime) return;
            m_nextMaintainTime = now + 1.0;
            m_maintenanceRuns++;

            SubsystemGameWidgets views = project.FindSubsystem<SubsystemGameWidgets>(false);
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            SubsystemSpawn spawn = project.FindSubsystem<SubsystemSpawn>(false);
            if (views == null || terrain?.Terrain == null || spawn == null)
            {
                reason = "缺少 SubsystemGameWidgets / SubsystemTerrain / SubsystemSpawn";
                return;
            }

            var remotePositions = new List<Vector3>();
            for (int i = 0; i < regions.Count; i++)
            {
                Vector3 position = regions[i].Position;
                if (CountRemoteCreatures(project, position.XZ, 68f) < 26) remotePositions.Add(position);
            }
            if (remotePositions.Count == 0) return;

            var visiblePositions = new List<Vector2>();
            foreach (GameWidget gameWidget in views.GameWidgets)
            {
                if (gameWidget?.ActiveCamera == null) continue;
                visiblePositions.Add(gameWidget.ActiveCamera.ViewPosition.XZ);
            }

            var candidateSet = new HashSet<Point2>();
            for (int i = 0; i < remotePositions.Count; i++)
            {
                Vector2 center = remotePositions[i].XZ;
                Point2 min = Game.Terrain.ToChunk(center - new Vector2(48f));
                Point2 max = Game.Terrain.ToChunk(center + new Vector2(48f));
                for (int x = min.X; x <= max.X; x++)
                {
                    for (int z = min.Y; z <= max.Y; z++)
                    {
                        Vector2 chunkCenter = new Vector2(((float)x + 0.5f) * 16f, ((float)z + 0.5f) * 16f);
                        if (Vector2.DistanceSquared(center, chunkCenter) >= 2304f) continue;
                        bool nearVisible = false;
                        for (int v = 0; v < visiblePositions.Count; v++)
                        {
                            if (Vector2.DistanceSquared(visiblePositions[v], chunkCenter) < 2304f)
                            {
                                nearVisible = true;
                                break;
                            }
                        }
                        if (nearVisible) continue;
                        TerrainChunk chunk = terrain.Terrain.GetChunkAtCell(
                            Game.Terrain.ToCell(chunkCenter.X), Game.Terrain.ToCell(chunkCenter.Y));
                        if (chunk != null && chunk.State > TerrainChunkState.InvalidPropagatedLight)
                        {
                            candidateSet.Add(new Point2(x, z));
                        }
                    }
                }
            }
            if (candidateSet.Count == 0) return;

            var candidates = new List<Point2>(candidateSet);
            candidates.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            Point2 selectedPoint = candidates[m_maintainCursor % candidates.Count];
            m_maintainCursor = (m_maintainCursor + 1) % candidates.Count;
            Vector2 selectedChunkCenter = new Vector2(((float)selectedPoint.X + 0.5f) * 16f,
                ((float)selectedPoint.Y + 0.5f) * 16f);
            Vector2 selectedRemoteCenter = remotePositions[0].XZ;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < remotePositions.Count; i++)
            {
                Vector2 candidate = remotePositions[i].XZ;
                float distance = Vector2.DistanceSquared(candidate, selectedChunkCenter);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    selectedRemoteCenter = candidate;
                }
            }

            SpawnChunk spawnChunk = GetOrCreateSpawnChunk(spawn, selectedPoint);
            if (spawnChunk == null)
            {
                reason = "拿不到 SpawnChunk（引擎私有方法不可用）";
                return;
            }
            int localCreatureCount = CountRemoteCreatures(project, selectedRemoteCenter, 68f);
            int availableSlots = Math.Max(0, 26 - localCreatureCount);
            int recordsToRestore = Math.Min(Math.Min(2, availableSlots), spawnChunk.SpawnsData.Count);
            for (int i = 0; i < recordsToRestore; i++)
            {
                SpawnEntityData record = spawnChunk.SpawnsData[0];
                spawnChunk.SpawnsData.RemoveAt(0);
                if (SpawnEntity(spawn, record) != null) m_restoredRecords++;
            }
            localCreatureCount = CountRemoteCreatures(project, selectedRemoteCenter, 68f);
            if (spawnChunk.SpawnsData.Count != 0 || localCreatureCount >= 26) return;

            SubsystemCreatureSpawn creatureSpawn = project.FindSubsystem<SubsystemCreatureSpawn>(false);
            MethodInfo spawnChunkCreatures;
            try
            {
                spawnChunkCreatures = m_parentMethod.GetInstanceMethodInfo(
                    typeof(SubsystemCreatureSpawn), "SpawnChunkCreatures",
                    new[] { typeof(SpawnChunk), typeof(int), typeof(bool) });
            }
            catch (Exception e)
            {
                reason = "拿不到 SpawnChunkCreatures：" + e.GetType().Name + " " + e.Message;
                return;
            }
            if (creatureSpawn == null || spawnChunkCreatures == null)
            {
                reason = "SubsystemCreatureSpawn 或 SpawnChunkCreatures 不可用";
                return;
            }
            var existing = new HashSet<Entity>(FindRemoteCreatures(project, selectedRemoteCenter, 68f));
            using (BeginRemoteCreatureCountScope(project, selectedRemoteCenter, 68f))
            {
                int nonConstantAttempts = spawnChunk.IsSpawned ? 1 : 10;
                int before = CountRemoteCreatures(project, selectedRemoteCenter, 68f);
                spawnChunkCreatures.Invoke(creatureSpawn, new object[] { spawnChunk, nonConstantAttempts, false });
                int after = CountRemoteCreatures(project, selectedRemoteCenter, 68f);
                if (after > before) m_spawnedByMaintenance += after - before;
                if (after < 26)
                {
                    spawnChunkCreatures.Invoke(creatureSpawn, new object[] { spawnChunk, 2, true });
                }
                TrimRemoteCreatureOverflow(project, selectedRemoteCenter, 68f, 26, existing);
            }
            spawnChunk.IsSpawned = true;
        }

        private SpawnChunk GetOrCreateSpawnChunk(SubsystemSpawn spawn, Point2 point)
        {
            try
            {
                MethodInfo method = m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemSpawn),
                    "GetOrCreateSpawnChunk", new[] { typeof(Point2) });
                return method?.Invoke(spawn, new object[] { point }) as SpawnChunk;
            }
            catch (Exception e)
            {
                Warn("调用 GetOrCreateSpawnChunk 失败：" + e.GetType().Name + " " + e.Message);
                return null;
            }
        }

        private Entity SpawnEntity(SubsystemSpawn spawn, SpawnEntityData data)
        {
            try
            {
                MethodInfo method = m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemSpawn),
                    "SpawnEntity", new[] { typeof(SpawnEntityData) });
                return method?.Invoke(spawn, new object[] { data }) as Entity;
            }
            catch (Exception e)
            {
                Warn("恢复 SpawnsData 记录失败（模板 " + data.TemplateName + "）：" +
                    e.GetType().Name + " " + e.Message);
                return null;
            }
        }

        private int CountRemoteCreatures(Project project, Vector2 center, float radius) =>
            FindRemoteCreatures(project, center, radius).Count;

        private List<Entity> FindRemoteCreatures(Project project, Vector2 center, float radius)
        {
            var result = new List<Entity>();
            SubsystemBodies bodies = project?.FindSubsystem<SubsystemBodies>(false);
            if (bodies == null) return result;
            float radiusSquared = radius * radius;
            var seen = new HashSet<Entity>();
            foreach (ComponentBody body in bodies.Bodies)
            {
                Entity entity = body?.Entity;
                if (entity == null || !seen.Add(entity)) continue;
                if (entity.FindComponent<ComponentCreature>() == null) continue;
                if (entity.FindComponent<ComponentPlayer>() != null) continue;
                if (Vector2.DistanceSquared(body.Position.XZ, center) <= radiusSquared) result.Add(entity);
            }
            return result;
        }

        private void TrimRemoteCreatureOverflow(Project project, Vector2 center, float radius,
            int targetCount, HashSet<Entity> existingCreatures)
        {
            List<Entity> creatures = FindRemoteCreatures(project, center, radius);
            int excess = creatures.Count - targetCount;
            if (excess <= 0) return;
            int removed = 0;
            for (int i = 0; i < creatures.Count && removed < excess; i++)
            {
                Entity entity = creatures[i];
                if (existingCreatures.Contains(entity)) continue;
                if (entity != null && entity.IsAddedToProject)
                {
                    project.RemoveEntity(entity, disposeEntity: true);
                    removed++;
                }
            }
            m_overflowTrimmed += removed;
        }

        private IDisposable BeginRemoteCreatureCountScope(Project project, Vector2 center, float radius)
        {
            SubsystemBodies bodies = project?.FindSubsystem<SubsystemBodies>(false);
            if (bodies == null) return null;
            Dictionary<ComponentBody, Point2> areaByBody;
            try
            {
                // 同上：非泛型读取，避免字段为 null 时被泛型 getter 判成类型不匹配。
                object raw = m_parentField.GetParentField(bodies, "m_areaByComponentBody",
                    typeof(SubsystemBodies));
                areaByBody = raw as Dictionary<ComponentBody, Point2>;
                if (raw != null && areaByBody == null)
                {
                    m_areaByBodyReadFailures++;
                    LogThrottled("area-by-body-type",
                        "读取 SubsystemBodies.m_areaByComponentBody 得到 " +
                        (raw.GetType().FullName ?? raw.GetType().Name) +
                        "（期望 Dictionary<ComponentBody, Point2>）→ 本轮不隐藏远端块外的生物；累计 " +
                        m_areaByBodyReadFailures + " 次", 10000, error: true);
                    return null;
                }
            }
            catch (Exception e)
            {
                m_areaByBodyReadFailures++;
                LogThrottled("area-by-body-read",
                    "读取 SubsystemBodies.m_areaByComponentBody 失败 → 本轮不隐藏远端块外的生物；累计 " +
                    m_areaByBodyReadFailures + " 次：" + e.GetType().Name + " " + e.Message,
                    10000, error: true);
                return null;
            }
            if (areaByBody == null)
            {
                m_areaByBodyReadFailures++;
                LogThrottled("area-by-body-null",
                    "SubsystemBodies.m_areaByComponentBody 为空 → 本轮不隐藏远端块外的生物；累计 " +
                    m_areaByBodyReadFailures + " 次", 10000, error: true);
                return null;
            }
            float radiusSquared = radius * radius;
            var outside = new List<ComponentBody>();
            foreach (ComponentBody body in areaByBody.Keys)
            {
                if (body?.Entity == null) continue;
                if (body.Entity.FindComponent<ComponentCreature>() == null) continue;
                if (Vector2.DistanceSquared(body.Position.XZ, center) > radiusSquared) outside.Add(body);
            }
            if (outside.Count == 0) return null;
            MethodInfo removeBody;
            MethodInfo addBody;
            try
            {
                removeBody = m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemBodies),
                    "RemoveBody", new[] { typeof(ComponentBody) });
                addBody = m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemBodies),
                    "AddBody", new[] { typeof(ComponentBody) });
            }
            catch (Exception e)
            {
                Warn("拿不到 SubsystemBodies.RemoveBody/AddBody：" + e.GetType().Name + " " + e.Message);
                return null;
            }
            if (removeBody == null || addBody == null) return null;
            for (int i = 0; i < outside.Count; i++) removeBody.Invoke(bodies, new object[] { outside[i] });
            return new SubsystemBodiesScope(bodies, addBody, outside);
        }

        private sealed class SubsystemBodiesScope : IDisposable
        {
            private SubsystemBodies m_bodies;
            private MethodInfo m_addBody;
            private ComponentBody[] m_removed;

            public SubsystemBodiesScope(SubsystemBodies bodies, MethodInfo addBody, List<ComponentBody> removed)
            {
                m_bodies = bodies;
                m_addBody = addBody;
                m_removed = removed.ToArray();
            }

            public void Dispose()
            {
                if (m_bodies == null) return;
                for (int i = 0; i < m_removed.Length; i++)
                {
                    try { m_addBody.Invoke(m_bodies, new object[] { m_removed[i] }); }
                    catch (Exception) { }
                }
                m_bodies = null;
                m_addBody = null;
                m_removed = null;
            }
        }

        // ------------------------------------------------------------------ 主机：原生更新作用域

        /// <summary>
        /// 原生 SubsystemCreatureSpawn.Update 期间，把每个联机角色的 GameWidget 临时挂进
        /// SubsystemGameWidgets.m_gameWidgets，让 SpawnRandomCreature 也围绕远端角色选点。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:834-867
        /// </summary>
        public IDisposable BeginRemoteSimulationViewScope()
        {
            if (m_clientReplicaMode || m_project == null || m_avatars == null) return null;
            SubsystemGameWidgets views = m_project.FindSubsystem<SubsystemGameWidgets>(false);
            if (views == null) return null;
            // 不用 GetParentField<T>：泛型版用 `value is T` 判定，字段为 null 时会抛
            // InvalidCastException；而本方法每帧都跑 → 会变成每帧 WARNING。先取 object 再显式判型。
            List<GameWidget> list;
            try
            {
                object raw = m_parentField.GetParentField(views, "m_gameWidgets",
                    typeof(SubsystemGameWidgets));
                list = raw as List<GameWidget>;
                if (raw != null && list == null)
                {
                    m_viewScopeFailures++;
                    LogThrottled("view-scope-type",
                        "读取 SubsystemGameWidgets.m_gameWidgets 得到 " +
                        (raw.GetType().FullName ?? raw.GetType().Name) +
                        "（期望 List<GameWidget>）→ 远端模拟视图本轮不挂载；累计 " +
                        m_viewScopeFailures + " 次", 5000, error: true);
                    return null;
                }
            }
            catch (Exception e)
            {
                m_viewScopeFailures++;
                LogThrottled("view-scope-read",
                    "读取 SubsystemGameWidgets.m_gameWidgets 失败 → 远端模拟视图本轮不挂载；累计 " +
                    m_viewScopeFailures + " 次：" + e.GetType().Name + " " + e.Message,
                    5000, error: true);
                return null;
            }
            if (list == null)
            {
                m_viewScopeFailures++;
                LogThrottled("view-scope-null",
                    "SubsystemGameWidgets.m_gameWidgets 为空 → 远端模拟视图本轮不挂载；累计 " +
                    m_viewScopeFailures + " 次", 5000, error: true);
                return null;
            }
            var added = new List<GameWidget>();
            foreach (int clientId in m_avatars.ClientIds)
            {
                if (!m_avatars.TryGet(clientId, out PlayerData playerData)) continue;
                GameWidget gameWidget = playerData?.GameWidget;
                if (gameWidget == null || list.Contains(gameWidget)) continue;
                gameWidget.ActiveCamera.Update(0f);
                list.Add(gameWidget);
                added.Add(gameWidget);
            }
            if (added.Count == 0) return null;
            m_viewScopeRuns++;
            return new GameWidgetListScope(list, added);
        }

        private sealed class GameWidgetListScope : IDisposable
        {
            private List<GameWidget> m_list;
            private List<GameWidget> m_added;

            public GameWidgetListScope(List<GameWidget> list, List<GameWidget> added)
            {
                m_list = list;
                m_added = added;
            }

            public void Dispose()
            {
                if (m_list == null) return;
                for (int i = m_added.Count - 1; i >= 0; i--) m_list.Remove(m_added[i]);
                m_list = null;
                m_added = null;
            }
        }

        /// <summary>
        /// 原生 SubsystemSpawn.Update 期间保护远端角色附近的 AutoDespawn 生物，避免引擎按
        /// "本机 GameWidget" 判定把它们存回 SpawnsData 并 Despawn。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:1041-1089
        /// </summary>
        public IDisposable BeginRemoteDespawnProtectionScope()
        {
            if (m_clientReplicaMode || m_project == null || m_avatars == null) return null;
            var remotePositions = new List<Vector3>();
            foreach (int clientId in m_avatars.ClientIds)
            {
                if (m_avatars.TryGetAvatarCell(clientId, out Vector3 position, out _))
                    remotePositions.Add(position);
            }
            if (remotePositions.Count == 0) return null;
            SubsystemSpawn spawn = m_project.FindSubsystem<SubsystemSpawn>(false);
            if (spawn == null) return null;

            var protectedSpawns = new List<ComponentSpawn>();
            var allSpawns = new List<ComponentSpawn>();
            foreach (ComponentSpawn item in spawn.Spawns) allSpawns.Add(item);
            for (int i = 0; i < allSpawns.Count; i++)
            {
                ComponentSpawn componentSpawn = allSpawns[i];
                if (componentSpawn == null || !componentSpawn.AutoDespawn ||
                    componentSpawn.ComponentFrame == null) continue;
                bool nearRemote = false;
                for (int p = 0; p < remotePositions.Count; p++)
                {
                    if (Vector3.DistanceSquared(remotePositions[p], componentSpawn.ComponentFrame.Position) <= 3600f)
                    {
                        nearRemote = true;
                        break;
                    }
                }
                if (!nearRemote) continue;

                ComponentCreature creature = componentSpawn.ComponentCreature;
                bool isDead = creature != null && creature.ComponentHealth != null &&
                    creature.ComponentHealth.Health <= 0f;
                try
                {
                    bool isShapeshifting = false;
                    ComponentShapeshifter shapeshifter =
                        componentSpawn.Entity.FindComponent<ComponentShapeshifter>();
                    if (shapeshifter != null &&
                        TryReadShapeshiftTarget(shapeshifter, out string shapeshiftTarget))
                    {
                        isShapeshifting = !string.IsNullOrEmpty(shapeshiftTarget);
                    }
                    if (isShapeshifting && !componentSpawn.IsDespawning)
                    {
                        componentSpawn.Despawn();
                    }
                    else if (componentSpawn.IsDespawning && !isDead && !isShapeshifting)
                    {
                        m_parentField.ModifyParentField(componentSpawn, "<DespawnTime>k__BackingField",
                            null, typeof(ComponentSpawn));
                        RemovePersistedSpawnRecord(spawn, componentSpawn);
                    }
                    m_parentField.ModifyParentField(componentSpawn, "<AutoDespawn>k__BackingField", false,
                        typeof(ComponentSpawn));
                    protectedSpawns.Add(componentSpawn);
                }
                catch (Exception e)
                {
                    // 这里在 SubsystemSpawn.Update 的每帧路径里：只计数 + 限流一行，
                    // 绝不再拼接可能二次抛异常的实体信息，也绝不再每只生物每帧写日志。
                    m_despawnProtectionFailures++;
                    LogThrottled("despawn-protection",
                        "远端脱困保护跳过一只生物（累计 " + m_despawnProtectionFailures +
                        " 次；本行每 5 秒最多一条）：" + e.GetType().Name + " " + e.Message,
                        5000, error: true);
                }
            }
            if (protectedSpawns.Count == 0) return null;
            m_despawnScopeRuns++;
            return new AutoDespawnScope(m_parentField, protectedSpawns);
        }

        private sealed class AutoDespawnScope : IDisposable
        {
            private IModParentField m_parentField;
            private List<ComponentSpawn> m_spawns;

            public AutoDespawnScope(IModParentField parentField, List<ComponentSpawn> spawns)
            {
                m_parentField = parentField;
                m_spawns = spawns;
            }

            public void Dispose()
            {
                if (m_spawns == null || m_parentField == null) return;
                for (int i = 0; i < m_spawns.Count; i++)
                {
                    try
                    {
                        m_parentField.ModifyParentField(m_spawns[i],
                            "<AutoDespawn>k__BackingField", true, typeof(ComponentSpawn));
                    }
                    catch (Exception) { }
                }
                m_spawns = null;
                m_parentField = null;
            }
        }

        /// <summary>
        /// 绑定 ComponentShapeshifter.m_spawnEntityTemplateName 的实际类型。
        /// 搜索顺序与 IModParentField.GetParentField 一致：声明类型 → 基类，DeclaredOnly。
        /// </summary>
        private static bool TryResolveShapeshiftMemberType(out Type memberType, out string source)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type current = typeof(ComponentShapeshifter);
                 current != null && current != typeof(object); current = current.BaseType)
            {
                FieldInfo field = current.GetField("m_spawnEntityTemplateName", Flags);
                if (field != null)
                {
                    memberType = field.FieldType;
                    source = "字段 " + current.FullName;
                    return true;
                }
                PropertyInfo property = current.GetProperty("m_spawnEntityTemplateName", Flags);
                if (property != null)
                {
                    memberType = property.PropertyType;
                    source = "属性 " + current.FullName;
                    return true;
                }
            }
            memberType = null;
            source = null;
            return false;
        }

        /// <summary>
        /// 读取"这只生物是否正在变形"。返回 false = 反射路径不可用（成员缺失 / 类型不匹配 /
        /// 读取异常），调用方按"未在变形"继续做其余脱困保护 —— 不再让异常把整只生物跳过。
        /// 不匹配只记一次（Mismatches 最多 1），之后只累计 SkippedReads，日志由调用方限流。
        /// </summary>
        private bool TryReadShapeshiftTarget(ComponentShapeshifter shapeshifter, out string target)
        {
            target = null;
            if (m_shapeshiftMember.State == ReflectiveStringMember.BindingState.Unknown)
            {
                if (!TryResolveShapeshiftMemberType(out Type memberType, out string source))
                {
                    m_shapeshiftMember.TryBind(null, null);
                    Warn("远端脱困保护：找不到 ComponentShapeshifter.m_spawnEntityTemplateName → " +
                        "变形检测反射路径已禁用（其余脱困保护继续）；mismatches=" +
                        m_shapeshiftMember.Mismatches);
                    return false;
                }
                if (!m_shapeshiftMember.TryBind(memberType, source))
                {
                    Warn("远端脱困保护：m_spawnEntityTemplateName 实际类型 " +
                        m_shapeshiftMember.BoundTypeName + "（" + source + "）不是 System.String → " +
                        "变形检测反射路径已禁用（其余脱困保护继续，按\"未在变形\"处理）；" +
                        "mismatches=" + m_shapeshiftMember.Mismatches);
                    return false;
                }
                Info("远端脱困保护：m_spawnEntityTemplateName 绑定类型 = " +
                    m_shapeshiftMember.BoundTypeName + "（" + source + "）");
            }
            if (m_shapeshiftMember.State != ReflectiveStringMember.BindingState.Ready)
            {
                // 已禁用：连反射读取都不再做，只累计 skip（这是"路径已停用"的可审计证据）。
                m_shapeshiftMember.NoteSkippedRead();
                return false;
            }
            try
            {
                object raw = m_parentField.GetParentField(shapeshifter,
                    "m_spawnEntityTemplateName", typeof(ComponentShapeshifter));
                if (!m_shapeshiftMember.TryInterpret(raw, out target, out string failure,
                        out bool disabledNow))
                {
                    if (disabledNow)
                    {
                        Warn("远端脱困保护：m_spawnEntityTemplateName " + failure +
                            " → 变形检测反射路径已禁用（其余脱困保护继续，按\"未在变形\"处理）；" +
                            "mismatches=" + m_shapeshiftMember.Mismatches);
                    }
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                string reason = "读取异常 " + e.GetType().Name + " " + e.Message;
                if (m_shapeshiftMember.TryReject(reason))
                {
                    Warn("远端脱困保护：m_spawnEntityTemplateName " + reason +
                        " → 变形检测反射路径已禁用（其余脱困保护继续，按\"未在变形\"处理）；" +
                        "mismatches=" + m_shapeshiftMember.Mismatches);
                }
                return false;
            }
        }

        private void RemovePersistedSpawnRecord(SubsystemSpawn spawn, ComponentSpawn componentSpawn)
        {
            SpawnChunk chunk = spawn.GetSpawnChunk(Game.Terrain.ToChunk(componentSpawn.ComponentFrame.Position.XZ));
            if (chunk == null || chunk.SpawnsData.Count == 0) return;
            string templateName = componentSpawn.Entity.ValuesDictionary.DatabaseObject.Name;
            Vector3 position = componentSpawn.ComponentFrame.Position;
            bool constantSpawn = componentSpawn.ComponentCreature?.ConstantSpawn ?? false;
            chunk.SpawnsData.RemoveAll(record => record.TemplateName == templateName &&
                record.ConstantSpawn == constantSpawn &&
                Vector3.DistanceSquared(record.Position, position) < 0.01f);
        }

        // ------------------------------------------------------------------ 客户端：复制体

        /// <summary>
        /// **客户端**：射线命中的本地实体 → 主机分配的生物网络键（近战请求的目标标识）。
        /// 不是复制体（玩家 / 方块实体 / 本地预测实体）返回 false 并写明原因；调用方把这类
        /// 命中按"打空"处理（不猜键、不本地扣血）。
        /// </summary>
        /// <summary>乘骑复制（2026-10-26）：按网络 key 查本地复制体实体。</summary>
        public Entity GetReplicaEntity(int key)
        {
            return key != 0 && m_replicas.TryGetValue(key, out Entity entity) ? entity : null;
        }

        public bool TryGetReplicaKey(Entity entity, out int key, out string reason)
        {
            key = 0;
            reason = null;
            if (entity == null)
            {
                reason = "实体为空";
                return false;
            }
            foreach (KeyValuePair<int, Entity> pair in m_replicas)
            {
                if (!ReferenceEquals(pair.Value, entity)) continue;
                key = pair.Key;
                return key > 0;
            }
            reason = "实体不是本端的生物复制体（可能是玩家 / 方块实体 / 本地预测实体）";
            return false;
        }

        public bool TryCreateReplica(CreatureStateRecord record, out string reason)
        {
            reason = string.Empty;
            m_createAttempts++;
            if (record == null)
            {
                m_createRejectedRecord++;
                reason = "记录为空";
                return false;
            }
            Project project = m_project;
            if (project == null)
            {
                m_createSkippedNoWorld++;
                reason = "当前没有世界";
                return false;
            }
            if (!ReferenceEquals(project, GameManager.Project))
            {
                m_createSkippedNoWorld++;
                reason = "当前没有世界（Project 已卸载或换新）";
                return false;
            }
            if (!IsFinite(record))
            {
                m_createRejectedRecord++;
                reason = "记录含非有限数值";
                return false;
            }
            if (m_replicas.TryGetValue(record.Key, out Entity existing) &&
                existing != null && existing.IsAddedToProject)
            {
                m_createDuplicate++;
                return TryApplyReplicaState(record, out reason);
            }
            string template = record.TemplateName;
            if (string.IsNullOrWhiteSpace(template))
            {
                m_createRejectedTemplate++;
                reason = "记录没有生物模板名（种类未知）";
                return false;
            }
            Entity entity = null;
            try
            {
                entity = DatabaseManager.CreateEntity(project, template, new ValuesDictionary(), true);
                ComponentBody body = entity?.FindComponent<ComponentBody>();
                if (entity == null || body == null)
                {
                    m_createRejectedTemplate++;
                    reason = "模板 \"" + template + "\" 没有 ComponentBody";
                    entity?.Dispose();
                    return false;
                }
                body.Position = record.Position;
                body.Rotation = record.Rotation;
                body.Velocity = record.Velocity;
                // 复制体是纯表现层：关掉重力和地面阻力，避免引擎物理在两次插值写入之间把
                // 它往地下拉（位置每帧都会被表现层覆盖，但重力会让"当帧"看起来往下陷）。
                body.IsGravityEnabled = false;
                body.IsGroundDragEnabled = false;
                // 2026-10-26 骑船客户端视角反复抖动修复：船副本只做表现，绝不能再跑**本地水体物理**。
                // ComponentBody.Update 会在水里边给船叠加 WaterSwayAngle/WaterTurnSpeed 的旋转、
                // 并按速度再积分一次位置；而表现层每帧又把姿态拉回主机记录 → 本地物理与网络姿态
                // 互相拉扯，骑在船上的第一人称相机就会反复抖。船没有本地动画需要速度，直接冻结。
                if (entity.FindComponent<ComponentCreature>() == null &&
                    entity.FindComponent<ComponentMount>() != null)
                {
                    body.IsWaterDragEnabled = false;
                    try
                    {
                        m_parentField.ModifyParentField(body, "<WaterSwayAngle>k__BackingField",
                            0f, typeof(ComponentBody));
                        m_parentField.ModifyParentField(body, "<WaterTurnSpeed>k__BackingField",
                            0f, typeof(ComponentBody));
                    }
                    catch (Exception e)
                    {
                        LogThrottled("boat-water-physics",
                            "船副本禁用本地水摇摆/水流转向失败（" + e.GetType().Name + " " +
                            e.Message + "）→ 骑船时客户端视角可能抖动", 5000, error: true);
                    }
                }
                project.AddEntity(entity);
                m_createdRaw++;
                // Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:EnsureRemoteAnimal
                // 复制体是表现层：摘掉 AI / 移动 / 生命 / 变形 / 生成状态机，避免本地逻辑与主机权威冲突。
                SubsystemUpdate subsystemUpdate = project.FindSubsystem<SubsystemUpdate>(false);
                ComponentSpawn spawn = entity.FindComponent<ComponentSpawn>();
                if (subsystemUpdate != null)
                {
                    foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                    {
                        if (updateable is ComponentBehavior || updateable is ComponentLocomotion ||
                            updateable is ComponentHealth || updateable is ComponentShapeshifter ||
                            updateable is ComponentBoat || updateable is ComponentDamage ||
                            ReferenceEquals(updateable, spawn))
                        {
                            subsystemUpdate.RemoveUpdateable(updateable);
                        }
                    }
                }
                ApplyReplicaHealth(entity, record.Health);
                ApplyReplicaSpawnProgress(entity, record.SpawnProgress, record.SpawnDuration);
                // "AddEntity 返回了"不等于"世界/观察口真的能看到它"（桥、SubsystemBodies 都以
                // Project 实体表 + body 注册为准）。这里当场校验，失败就明确失败并回滚 ——
                // 不允许出现"适配器字典里有 20 个复制体、世界里 0 个"的静默状态。
                if (!VerifyReplicaAttached(project, entity, out string attachReason))
                {
                    m_attachFailed++;
                    if (entity.IsAddedToProject && entity.Project == project)
                    {
                        try { project.RemoveEntity(entity, disposeEntity: true); } catch (Exception) { }
                    }
                    m_replicas.Remove(record.Key);
                    m_replicaTemplates.Remove(record.Key);
                    reason = "复制体创建后没有真正进入世界（key=" + record.Key + " 模板=\"" + template +
                        "\"）：" + attachReason;
                    LogThrottled("attach-failed:" + record.Key, reason +
                        " → 已回滚，请求主机重发全量（不伪造本地生物）", 5000, error: true);
                    return false;
                }
                m_replicas[record.Key] = entity;
                m_replicaTemplates[record.Key] = template;
                m_replicaActivity[record.Key] = record.Activity;

                m_replicaFeedOrder[record.Key] = record.FeedOrder;
                m_replicaGrounded[record.Key] = record.Grounded;
                ApplyReplicaActivity(entity, record.Activity);
                m_createdAttached++;
                return true;
            }
            catch (Exception e)
            {
                m_createExceptions++;
                if (entity != null && entity.IsAddedToProject && entity.Project == project)
                {
                    try { project.RemoveEntity(entity, disposeEntity: true); } catch (Exception) { }
                }
                m_replicas.Remove(record.Key);
                m_replicaTemplates.Remove(record.Key);
                reason = "创建模板 \"" + template + "\" 失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 创建后的**挂载校验**：实体真的加进了这个 Project、真的带 ComponentBody、并且真的
        /// 注册进了 <c>SubsystemBodies</c>（桥的 <c>world entities</c> / 引擎物理 / 渲染都以它为准）。
        /// 任何一条不成立都算失败（调用方回滚 + 记 attachFail），绝不把"字典里有引用"当成成功。
        /// </summary>
        private bool VerifyReplicaAttached(Project project, Entity entity, out string reason)
        {
            if (entity == null) { reason = "实体为空"; return false; }
            if (!entity.IsAddedToProject) { reason = "实体没有加入 Project（AddEntity 未生效）"; return false; }
            if (!ReferenceEquals(entity.Project, project))
            {
                reason = "实体属于另一个 Project（世界已被换新但实体没跟着走）";
                return false;
            }
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) { reason = "实体没有 ComponentBody"; return false; }
            SubsystemBodies bodies = project.FindSubsystem<SubsystemBodies>(false);
            if (bodies == null) { reason = "Project 没有 SubsystemBodies（引擎世界不完整）"; return false; }
            foreach (ComponentBody candidate in bodies.Bodies)
            {
                if (ReferenceEquals(candidate, body)) { reason = string.Empty; return true; }
            }
            reason = "实体已加入 Project，但 SubsystemBodies 里没有它的 body（桥和物理都看不到它）";
            return false;
        }

        public bool TryApplyReplicaState(CreatureStateRecord record, out string reason)
        {
            reason = string.Empty;
            if (record == null) { reason = "记录为空"; return false; }
            if (!IsFinite(record)) { reason = "记录含非有限数值"; return false; }
            if (!m_replicas.TryGetValue(record.Key, out Entity entity) ||
                entity == null || !entity.IsAddedToProject || entity.Project != m_project)
            {
                m_replicas.Remove(record.Key);
                m_replicaTemplates.Remove(record.Key);
                reason = "复制体不存在或已随 Project 销毁";
                return false;
            }
            // 变形类生物：同一个网络 key 的模板名可能变（旧实现 SetRemoteAnimalTemplate 的处理）。
            // 模板变了就必须换实体，不能把新种类的外观套在旧实体上。
            if (!string.IsNullOrWhiteSpace(record.TemplateName) &&
                m_replicaTemplates.TryGetValue(record.Key, out string oldTemplate) &&
                !string.Equals(oldTemplate, record.TemplateName, StringComparison.Ordinal))
            {
                if (entity.IsAddedToProject && entity.Project == m_project)
                    m_project.RemoveEntity(entity, disposeEntity: true);
                m_replicas.Remove(record.Key);
                m_replicaTemplates.Remove(record.Key);
                return TryCreateReplica(record, out reason);
            }
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null)
            {
                reason = "复制体没有 ComponentBody";
                return false;
            }
            // 位置/姿态/速度**不在这里写**：收到记录就硬写 = 10 Hz 抖动（实测问题）。
            // 它们由 TryApplyReplicaPresentation 每帧按插值/外推结果驱动。
            ApplyReplicaHealth(entity, record.Health);
            // 活动位（尤其 Attacking）随记录更新，表现层每帧据此驱动攻击动画（2026-10-26）。
            m_replicaActivity[record.Key] = record.Activity;

                m_replicaFeedOrder[record.Key] = record.FeedOrder;
                m_replicaGrounded[record.Key] = record.Grounded;
            ApplyReplicaActivity(entity, record.Activity);
            return true;
        }

        /// <summary>
        /// 出生渐变对齐 v2（2026-10-26，蛋化动物修复；v1 回写 SpawnTime 的方案有缺陷——
        /// 两端 TotalElapsedGameTime 时钟不一致（客户端世界时钟落后主机一个传输时长），
        /// 回写后 Opacity 反复跳变、动物一直半透明）。
        /// v2：不回写绝对时刻，用引擎自己的语义——
        ///   · progress ≥ SpawnDuration(2s)（绝大多数：已成熟）→ SpawnDuration = 0
        ///     （ComponentCreatureModel.Opacity：Duration ≤ 0 = 恒 1，**绝不闪半透明**）；
        ///   · 主机 SpawnDuration ≤ 0（马鞍/船等行为创建的实体显式设 0）→ 客户端也不渐变；
        ///   · progress ≥ duration（已经渐变完）→ 0；
        ///   · 否则 SpawnDuration = 主机值、SpawnTime = 本地游戏时钟 − progress —— 两端时钟不同，
        ///     但 progress 是主机时钟差值，回推出本地 SpawnTime 后 Opacity 与主机一致。
        /// 2026-10-26 装鞍半透明修复：v2 假设"引擎固定 2s 渐变"，而鞍马/船在 OnUse 里显式
        /// SpawnDuration=0（SubsystemSaddleBlockBehavior.cs:31 / SubsystemBoatBlockBehavior.cs:35），
        /// 于是主机不渐变、客户端被强行 2s 渐变（实机症状：装鞍后马半透明一下）。
        /// SpawnDuration 是公开 setter；SpawnTime 是私有 setter，用 ModParentField 写 backing field。
        /// </summary>
        private void ApplyReplicaSpawnProgress(Entity entity, float spawnProgress, float spawnDuration)
        {
            ComponentSpawn spawn = entity.FindComponent<ComponentSpawn>();
            if (spawn == null) return;
            float duration = float.IsFinite(spawnDuration) ? MathUtils.Max(spawnDuration, 0f) : 0f;
            if (duration <= 0f || spawnProgress >= duration)
            {
                spawn.SpawnDuration = 0f;
                return;
            }
            spawn.SpawnDuration = duration;
            double spawnTime = m_gameTime - spawnProgress;
            try
            {
                m_parentField.ModifyParentField(spawn, "<SpawnTime>k__BackingField", spawnTime,
                    typeof(ComponentSpawn));
            }
            catch (Exception e)
            {
                LogThrottled("spawn-time-bind",
                    "写 ComponentSpawn.SpawnTime 失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 本复制体的出生渐变可能从本地时刻开始（记录一次）", 10000, error: true);
            }
        }

        public bool TryApplyReplicaPresentation(int key, Vector3 position, Quaternion rotation,
            Vector3 velocity, Vector2 lookAngles, out string reason)
        {
            reason = string.Empty;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(position.Z) || !float.IsFinite(rotation.X) ||
                !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) ||
                !float.IsFinite(rotation.W) || !float.IsFinite(velocity.X) ||
                !float.IsFinite(velocity.Y) || !float.IsFinite(velocity.Z))
            {
                m_presentationFailed++;
                reason = "插值结果含非有限数值（不写进引擎）";
                return false;
            }
            if (!m_replicas.TryGetValue(key, out Entity entity) || entity == null ||
                !entity.IsAddedToProject || !ReferenceEquals(entity.Project, m_project))
            {
                m_presentationFailed++;
                m_entityLost++;
                m_replicas.Remove(key);
                m_replicaTemplates.Remove(key);
                reason = "复制体不存在或已随 Project 销毁";
                return false;
            }
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null)
            {
                m_presentationFailed++;
                m_entityLost++;
                m_replicas.Remove(key);
                m_replicaTemplates.Remove(key);
                reason = "复制体没有 ComponentBody";
                return false;
            }
            body.Position = position;
            body.Rotation = rotation;
            body.Velocity = velocity;
            if (!m_replicaPresentation.TryGetValue(key, out ReplicaPresentationSnapshot presentation))
            {
                presentation = new ReplicaPresentationSnapshot();
                m_replicaPresentation[key] = presentation;
            }
            presentation.Position = position;
            presentation.Rotation = rotation;
            presentation.Velocity = velocity;
            // 船副本（非生物 + ComponentMount）：位置/旋转已完全由主机记录驱动，速度不允许再
            // 让本地 ComponentBody.Update 积分一次（否则骑手相机每帧"多走一点再被拉回"→抖动）。
            if (entity.FindComponent<ComponentCreature>() == null &&
                entity.FindComponent<ComponentMount>() != null)
            {
                body.Velocity = Vector3.Zero;
                m_boatReplicaVelocityZeroed++;
            }
            // 头部朝向（2026-10-26）：马/驴/骆驼被骑转向时头要转；客户端复制体摘掉了
            // ComponentLocomotion 的 updateable，只能把主机的 LookAngles 每帧写回。
            ComponentLocomotion locomotion = entity.FindComponent<ComponentLocomotion>();
            if (locomotion != null && float.IsFinite(lookAngles.X) && float.IsFinite(lookAngles.Y))
            {
                try
                {
                    m_parentField.ModifyParentField(locomotion, "m_lookAngles", lookAngles,
                        typeof(ComponentLocomotion));
                }
                catch (Exception)
                {
                    if (m_lookAngleWriteFailures++ == 0)
                    {
                        LogThrottled("replica-look-bind",
                            "写复制体 ComponentLocomotion.m_lookAngles 失败（版本不匹配？）→ " +
                            "坐骑头部转向不会跟随（只记录一次）", 10000, error: true);
                    }
                }
            }
            // 攻击动画（2026-10-26）：复制体的 AI/行为 updateable 已摘掉，AttackOrder 只能由
            // 主机的活动位驱动；每帧按最近一次活动位设置（不依赖状态更新频率）。
            if (m_replicaActivity.TryGetValue(key, out CreatureActivity activity))
                ApplyReplicaActivity(entity, activity);
            ComponentCreatureModel model = entity.FindComponent<ComponentCreatureModel>();
            if (model != null && m_replicaFeedOrder.TryGetValue(key, out bool feedOrder))
                model.FeedOrder = feedOrder;
            if (m_clientReplicaMode)
                TryPlayClientReplicaIdleSound(key, entity);
            m_presentationApplied++;
            return true;
        }

        /// <summary>
        /// 客户端本地合成复制体的 idle 叫声。
        ///
        /// 为什么不在主机端中继：引擎音频队列只有“衰减后的音量”，没有声源位置；
        /// 主机走远后猪叫在主机队列里可能低于可听阈值，或者只能用错误的主机锚点重投影
        /// （实机：主机走远能听到猪叫，客户端站在猪边上却听不到）。客户端的复制体本身
        /// 带有动物的 ComponentCreatureSounds（模板完整保留），直接让它按自己的位置
        /// 播 idle 声音即可；不依赖主机音频队列，也不会回传网络。
        /// </summary>
        /// <summary>模型 Update 之前：把当帧表现位置/速度写回，并驱动模型命令。</summary>
        private void RefreshReplicaAnimationState(float dt)
        {
            if (!m_clientReplicaMode || m_replicas.Count == 0) return;
            foreach (KeyValuePair<int, Entity> pair in m_replicas)
            {
                Entity entity = pair.Value;
                if (entity == null || !entity.IsAddedToProject ||
                    !ReferenceEquals(entity.Project, m_project)) continue;
                ComponentBody body = entity.FindComponent<ComponentBody>();
                if (body == null) continue;
                if (!m_replicaPresentation.TryGetValue(pair.Key, out ReplicaPresentationSnapshot presentation))
                    continue;
                body.Position = presentation.Position;
                body.Rotation = presentation.Rotation;
                body.Velocity = presentation.Velocity;
                if (entity.FindComponent<ComponentCreature>() == null &&
                    entity.FindComponent<ComponentMount>() != null)
                {
                    body.Velocity = Vector3.Zero;
                }
                ComponentCreatureModel model = entity.FindComponent<ComponentCreatureModel>();
                if (model == null) continue;
                if (m_replicaActivity.TryGetValue(pair.Key, out CreatureActivity activity))
                    ApplyReplicaActivity(entity, activity);
                if (m_replicaFeedOrder.TryGetValue(pair.Key, out bool feedOrder))
                    model.FeedOrder = feedOrder;
            }
        }

        private readonly Dictionary<Type, FieldInfo> m_standingFields = new Dictionary<Type, FieldInfo>();

        private FieldInfo ResolveStandingField(Type type)
        {
            if (m_standingFields.TryGetValue(type, out FieldInfo cached)) return cached;
            FieldInfo field = null;
            for (Type current = type; current != null; current = current.BaseType)
            {
                field = current.GetField("<StandingOnValue>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null) break;
            }
            m_standingFields[type] = field;
            return field;
        }

        private FieldInfo ResolveMovementPhaseField(Type type)
        {
            if (m_movementPhaseFields.TryGetValue(type, out FieldInfo cached)) return cached;
            FieldInfo field = null;
            for (Type current = type; current != null; current = current.BaseType)
            {
                field = current.GetField("<MovementAnimationPhase>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null) break;
            }
            if (field == null)
            {
                for (Type current = type; current != null; current = current.BaseType)
                {
                    foreach (FieldInfo candidate in current.GetFields(
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (candidate.Name.IndexOf("MovementAnimationPhase",
                                StringComparison.Ordinal) >= 0)
                        {
                            field = candidate;
                            break;
                        }
                    }
                    if (field != null) break;
                }
            }
            m_movementPhaseFields[type] = field;
            return field;
        }

        /// <summary>模型 Update 之后推进/写回 MovementAnimationPhase（渲染前最后一步）。</summary>
        private void RefreshReplicaAnimationPhase(float dt)
        {
            if (!m_clientReplicaMode || dt <= 0.0001f || m_replicas.Count == 0) return;
            foreach (KeyValuePair<int, Entity> pair in m_replicas)
            {
                Entity entity = pair.Value;
                if (entity == null || !entity.IsAddedToProject ||
                    !ReferenceEquals(entity.Project, m_project)) continue;
                if (!m_replicaPresentation.TryGetValue(pair.Key, out ReplicaPresentationSnapshot presentation))
                    continue;
                ComponentCreatureModel model = entity.FindComponent<ComponentCreatureModel>();
                if (model == null) continue;
                float speed = 0f;
                if (m_lastAnimationPosition.TryGetValue(pair.Key, out Vector3 previous))
                {
                    Vector3 delta = presentation.Position - previous;
                    speed = new Vector3(delta.X, 0f, delta.Z).Length() / dt;
                }
                m_lastAnimationPosition[pair.Key] = presentation.Position;
                float phase = 0f;
                if (speed > 0.02f)
                {
                    float step = MathUtils.Max(speed, 0.25f) * dt * 1.5f;
                    m_replicaAnimationPhase.TryGetValue(pair.Key, out phase);
                    phase = MathUtils.Remainder(phase + step, 1000f);
                }
                m_replicaAnimationPhase[pair.Key] = phase;
                if (phase != 0f && m_replicaGrounded.TryGetValue(pair.Key, out bool grounded) && grounded)
                {
                    // 四足/走禽模型的 AnimateCreature 只有在
                    // body.StandingOnValue.HasValue || body.ImmersionFactor > 0 时才计算腿部角度。
                    // 客户端复制体被表现层直接写位置，物理不一定会自己算出 StandingOnValue；
                    // 按主机 grounded 状态补一个合法的“站在地面”标记（只影响动画分支）。
                    ComponentBody body = entity.FindComponent<ComponentBody>();
                    if (body != null && !body.StandingOnValue.HasValue)
                    {
                        FieldInfo standingField = ResolveStandingField(body.GetType());
                        try { standingField?.SetValue(body, 0); } catch (Exception) { }
                    }
                }
                FieldInfo phaseField = ResolveMovementPhaseField(model.GetType());
                if (phaseField == null) continue;
                try
                {
                    phaseField.SetValue(model, phase);
                    if (!m_movementPhaseFieldLogged)
                    {
                        m_movementPhaseFieldLogged = true;
                        LogThrottled("movement-phase-write",
                            "客户端动物迈腿：已写 MovementAnimationPhase backing field " +
                            phaseField.DeclaringType?.Name + "." + phaseField.Name, 60000);
                    }
                }
                catch (Exception e)
                {
                    LogThrottled("movement-phase-fail:" + e.GetType().Name,
                        "写 MovementAnimationPhase 失败（" + e.GetType().Name + " " + e.Message +
                        "）→ 客户端动物不会迈腿", 10000, error: true);
                }
            }
        }

        private void TryPlayClientReplicaIdleSound(int key, Entity entity)
        {
            if (entity == null || !entity.IsAddedToProject ||
                !TryGetLocalPlayerPosition(m_project, out Vector3 localPosition)) return;
            ComponentBody body = entity.FindComponent<ComponentBody>();
            if (body == null) return;
            if (Vector3.DistanceSquared(body.Position, localPosition) > 32f * 32f) return;
            double now = Time.RealTime;
            if (m_nextClientIdleSound.TryGetValue(key, out double due) && now < due) return;
            m_nextClientIdleSound[key] = now + m_clientIdleRandom.Float(8f, 20f);
            ComponentCreatureSounds sounds = entity.FindComponent<ComponentCreatureSounds>();
            if (sounds == null) return;
            try { sounds.PlayIdleSound(skipIfRecentlyPlayed: true); }
            catch (Exception e)
            {
                LogThrottled("client-idle:" + e.GetType().Name,
                    "客户端复制体 idle 叫声失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 该复制体本次不合成声音", 10000, error: true);
            }
        }

        public bool TryRemoveReplica(int key, out string reason)
        {
            reason = string.Empty;
            if (!m_replicas.TryGetValue(key, out Entity entity))
            {
                reason = "本端没有 key=" + key + " 的复制体";
                return false;
            }
            m_replicas.Remove(key);
            m_replicaTemplates.Remove(key);
            m_replicaActivity.Remove(key);
            m_replicaFeedOrder.Remove(key);
            m_replicaGrounded.Remove(key);
            m_replicaPresentation.Remove(key);
            m_lastAnimationPosition.Remove(key);
            m_replicaAnimationPhase.Remove(key);
            if (entity != null && entity.IsAddedToProject && entity.Project == m_project)
            {
                m_project.RemoveEntity(entity, disposeEntity: true);
            }
            return true;
        }

        public int TrimReplicasTo(ICollection<int> keepKeys, out string reason)
        {
            reason = string.Empty;
            Project project = m_project;
            if (project == null) { reason = "当前没有世界"; return -1; }
            var keep = keepKeys ?? Array.Empty<int>();
            var keepSet = keep as HashSet<int> ?? new HashSet<int>(keep);
            int removed = 0;
            // 键对账（纯函数，可自检）：把"保留集合 vs 本地复制体键"的三种情况分开，
            // 键不一致/保留集合不可信时**不**按它清世界实体 —— 旧实现在这里会把刚建好的
            // 复制体当成"未分配"全部删掉，表现就是"计数器有 N 个复制体、世界里 0 个"。
            CreatureReplicaKeys.Ledger ledger = CreatureReplicaKeys.Reconcile(keepSet, m_replicas.Keys);
            int missingBefore = ledger.MissingKeys.Count;
            if (ledger.HasMismatch)
            {
                m_keyMismatch += missingBefore;
                LogThrottled("key-mismatch", "客户端的分配集合与本地复制体键不一致：" +
                    CreatureReplicaKeys.Describe(ledger) + "（主机分配的 key 必须为正整数网络 id；" +
                    "这些键只记录，不再被当成'未分配'去清世界实体）", 5000, error: true);
            }
            var replicaEntities = new HashSet<Entity>();
            foreach (KeyValuePair<int, Entity> pair in m_replicas)
            {
                if (keepSet.Contains(pair.Key))
                {
                    if (pair.Value != null) replicaEntities.Add(pair.Value);
                }
            }
            for (int i = 0; i < ledger.StaleKeys.Count; i++)
            {
                int key = ledger.StaleKeys[i];
                if (!m_replicas.TryGetValue(key, out Entity entity)) continue;
                m_replicas.Remove(key);
                m_replicaTemplates.Remove(key);
                m_replicaActivity.Remove(key);
                m_replicaFeedOrder.Remove(key);
            m_replicaGrounded.Remove(key);
                m_replicaPresentation.Remove(key);
                m_lastAnimationPosition.Remove(key);
            m_replicaAnimationPhase.Remove(key);
                if (entity != null && entity.IsAddedToProject && entity.Project == project)
                {
                    project.RemoveEntity(entity, disposeEntity: true);
                    m_removedTrimmed++;
                    removed++;
                }
                else
                {
                    // 表里有引用、世界里已经没有它：明确记 lost，不假装删除成功。
                    m_entityLost++;
                }
            }
            if (ledger.KeepSetUnusable)
            {
                // 分配集合同本地键一个都对不上 ⇒ 不可信（典型：Project 换新后本适配器的表被清空，
                // 而模块还拿着旧键）。此时禁止扫世界实体，否则会把当前世界里所有生物实体清光。
                LogThrottled("keep-set-unusable", "保留集合不可信（" + CreatureReplicaKeys.Describe(ledger) +
                    "）→ 本拍不清理世界里的生物实体（先让模块重新对齐/请求全量）", 5000, error: true);
                RefreshReplicaVisibility(project);
                return removed;
            }
            // 客户端不跑本地生成：不在复制体集合里的非玩家生物实体一律清掉（旧实现 MaintainClientWorldObjects）。
            var entities = new List<Entity>();
            foreach (Entity entity in project.Entities) entities.Add(entity);
            int unassigned = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                if (entity == null || !entity.IsAddedToProject || entity.Project != project) continue;
                if (replicaEntities.Contains(entity)) continue;
                if (entity.FindComponent<ComponentCreature>() == null)
                {
                    // 非生物载具（船）：本地存档副本同样要清（权威船由主机复制链下发）。
                    if (entity.FindComponent<ComponentBoat>() == null) continue;
                    ComponentMount nativeMount = entity.FindComponent<ComponentMount>();
                    if (nativeMount != null && nativeMount.Rider != null) continue;
                    project.RemoveEntity(entity, disposeEntity: true);
                    removed++;
                    m_nativeMountsRemoved++;
                    continue;
                }
                if (entity.FindComponent<ComponentPlayer>() != null) continue;
                project.RemoveEntity(entity, disposeEntity: true);
                removed++;
                unassigned++;
                m_unassignedRemoved++;
            }
            if (unassigned > 0)
            {
                m_diagnostics?.Info("creature-spawn", "客户端清理了 " + unassigned +
                    " 个不在主机分配集合里的生物实体（本地生成结果 / 已不再分配给本端）");
            }
            RefreshReplicaVisibility(project);
            return removed;
        }

        /// <summary>
        /// 可见性对账：复制体是否真的落在**本机玩家**周围的主机分配半径内。
        /// 这不是分配逻辑（分配仍由主机按 68m/26 计算），而是把一个真实故障变成显式日志：
        /// 主机侧联机角色位置过期时，主机分配的生物全在 150 米外，客户端"创建成功、
        /// 表现正常、计数漂亮"，屏幕上一只也没有。状态行 + 限流日志必须把这件事说出来。
        /// </summary>
        private void RefreshReplicaVisibility(Project project)
        {
            m_visibilityChecks++;
            m_replicasInRegion = 0;
            m_replicasOutOfRegion = 0;
            m_nearestReplicaDistance = float.PositiveInfinity;
            m_farthestReplicaDistance = float.PositiveInfinity;
            if (m_replicas.Count == 0) return;
            if (!TryGetLocalPlayerPosition(project, out Vector3 origin))
            {
                LogThrottled("visibility-no-player", "可见性对账跳过：本机玩家没有 ComponentBody（位置未知，不猜）", 10000);
                return;
            }
            float radiusSquared = m_clientRegionRadius * m_clientRegionRadius;
            float nearestSquared = float.PositiveInfinity;
            float farthestSquared = 0f;
            foreach (KeyValuePair<int, Entity> pair in m_replicas)
            {
                Entity entity = pair.Value;
                if (entity == null || !entity.IsAddedToProject) { m_entityLost++; continue; }
                ComponentBody body = entity.FindComponent<ComponentBody>();
                if (body == null) continue;
                Vector3 delta = body.Position - origin;
                float distanceSquared = delta.X * delta.X + delta.Z * delta.Z;
                if (distanceSquared < nearestSquared) nearestSquared = distanceSquared;
                if (distanceSquared > farthestSquared) farthestSquared = distanceSquared;
                if (distanceSquared <= radiusSquared) m_replicasInRegion++;
                else m_replicasOutOfRegion++;
            }
            m_nearestReplicaDistance = float.IsPositiveInfinity(nearestSquared)
                ? float.PositiveInfinity
                : MathUtils.Sqrt(nearestSquared);
            m_farthestReplicaDistance = MathUtils.Sqrt(farthestSquared);
            if (m_replicasInRegion == 0 && m_replicasOutOfRegion > 0)
            {
                LogThrottled("replicas-out-of-region", "可见性对账：本端 " + m_replicas.Count +
                    " 个复制体全部在主机分配半径 " + m_clientRegionRadius.ToString("0.#") +
                    " m 之外（最近 " + DescribeDistance(m_nearestReplicaDistance) + " m / 最远 " +
                    DescribeDistance(m_farthestReplicaDistance) + " m，本机玩家在该半径内看到 0 只）" +
                    " → 主机对本端的分配中心与本机玩家位置不一致：检查主机侧该联机角色的位置是否过期" +
                    "（客户端位置快照是否被主机应用），而不是继续怀疑复制体没建出来", 10000, error: true);
            }
        }

        /// <summary>
        /// 这个 key 的复制体是否就是本机玩家当前骑乘/站立的坐骑（比较引擎实体引用）。
        /// 骑/站坐骑时相机跟着它走，表现层必须始终平滑插值，不能静止贴记录。
        /// </summary>
        public bool IsLocalMountReplica(int key)
        {
            if (!m_replicas.TryGetValue(key, out Entity replica) || replica == null) return false;
            SubsystemPlayers players = m_project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return false;
            for (int i = 0; i < players.ComponentPlayers.Count; i++)
            {
                ComponentPlayer player = players.ComponentPlayers[i];
                if (player == null) continue;
                if (m_avatars != null && player.PlayerData != null &&
                    m_avatars.Contains(player.PlayerData)) continue;   // 远端化身不算本机
                ComponentRider rider = player.ComponentRider;
                if (rider?.Mount != null && ReferenceEquals(rider.Mount.Entity, replica))
                {
                    m_localMountReplicaHits++;
                    return true;
                }
                ComponentBody body = player.ComponentBody;
                if (body?.StandingOnBody != null &&
                    ReferenceEquals(body.StandingOnBody.Entity, replica))
                {
                    m_localMountReplicaHits++;
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetLocalPlayerPosition(Project project, out Vector3 position)
        {
            position = Vector3.Zero;
            SubsystemPlayers players = project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return false;
            for (int i = 0; i < players.ComponentPlayers.Count; i++)
            {
                ComponentPlayer player = players.ComponentPlayers[i];
                ComponentBody body = player?.ComponentBody;
                if (body == null) continue;
                position = body.Position;
                return true;
            }
            return false;
        }

        private void RemoveAllReplicas(string reason)
        {
            Project project = m_project;
            var keys = new List<int>(m_replicas.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                if (!m_replicas.TryGetValue(keys[i], out Entity entity)) continue;
                if (entity != null && entity.IsAddedToProject && entity.Project == project)
                {
                    project.RemoveEntity(entity, disposeEntity: true);
                }
            }
            m_replicas.Clear();
            m_replicaTemplates.Clear();
            m_replicaActivity.Clear();
            m_replicaFeedOrder.Clear();
            m_replicaGrounded.Clear();
            m_replicaPresentation.Clear();
            m_lastAnimationPosition.Clear();
            m_replicaAnimationPhase.Clear();
            m_diagnostics?.Info("creature-spawn", reason);
        }

        /// <summary>
        /// 客户端复制体的攻击动画（2026-10-26 修复）。复制体创建时摘掉了 ComponentBehavior /
        /// ComponentLocomotion 等 updateable，主机的 ComponentChaseBehavior 不会在客户端跑，
        /// 所以模型不会自己进入攻击动作；主机 ComputeHostActivity 已把
        /// "ComponentCreatureModel.AttackOrder || IsAttackHitMoment" 算成 Attacking 位随记录下发，
        /// 这里写回复制体的 AttackOrder → 播放引擎原生攻击动画（客户端能看到动物扑咬）。
        /// </summary>
        private void ApplyReplicaActivity(Entity entity, CreatureActivity activity)
        {
            ComponentCreatureModel model = entity?.FindComponent<ComponentCreatureModel>();
            if (model == null) return;
            bool attacking = (activity & CreatureActivity.Attacking) != 0;
            if (model.AttackOrder == attacking) return;
            model.AttackOrder = attacking;
            if (attacking) m_attackAnimApplied++;
        }

        private void ApplyReplicaHealth(Entity entity, float health)
        {
            ComponentHealth componentHealth = entity?.FindComponent<ComponentHealth>();
            if (componentHealth == null) return;
            try
            {
                float value = MathUtils.Saturate(health);
                m_parentField.ModifyParentField(componentHealth, "<Health>k__BackingField", value,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(componentHealth, "m_lastHealth", value,
                    typeof(ComponentHealth));
                m_parentField.ModifyParentField(componentHealth, "<HealthChange>k__BackingField", 0f,
                    typeof(ComponentHealth));
            }
            catch (Exception e)
            {
                m_healthApplyFailures++;
                LogThrottled("replica-health-apply",
                    "复制体生命值写入失败（位置/姿态仍按主机记录同步；累计 " +
                    m_healthApplyFailures + " 次）：" + e.GetType().Name + " " + e.Message,
                    5000, error: true);
            }
        }

        private static bool IsFinite(CreatureStateRecord record) =>
            float.IsFinite(record.Position.X) && float.IsFinite(record.Position.Y) &&
            float.IsFinite(record.Position.Z) &&
            float.IsFinite(record.Rotation.X) && float.IsFinite(record.Rotation.Y) &&
            float.IsFinite(record.Rotation.Z) && float.IsFinite(record.Rotation.W) &&
            float.IsFinite(record.Velocity.X) && float.IsFinite(record.Velocity.Y) &&
            float.IsFinite(record.Velocity.Z) && float.IsFinite(record.Health);

        public string StatusLine() =>
            "creature-adapter: replicas=" + m_replicas.Count +
            " maintained=" + m_maintenanceRuns +
            " spawnedByMaintain=" + m_spawnedByMaintenance +
            " restored=" + m_restoredRecords +
            " overflowTrim=" + m_overflowTrimmed +
            " created=" + m_createdRaw +
            " attached=" + m_createdAttached +
            " createTry=" + m_createAttempts +
            " createDup=" + m_createDuplicate +
            " createNoWorld=" + m_createSkippedNoWorld +
            " createBadRecord=" + m_createRejectedRecord +
            " createBadTemplate=" + m_createRejectedTemplate +
            " createThrow=" + m_createExceptions +
            " attachFail=" + m_attachFailed +
            " presentation=" + m_presentationApplied +
            " attackAnim=" + m_attackAnimApplied +
            " presentFail=" + m_presentationFailed +
            " removedTrimmed=" + m_removedTrimmed +
            " unassignedRemoved=" + m_unassignedRemoved +
            " nativeMountsRemoved=" + m_nativeMountsRemoved +
            " boatReplicaZeroVel=" + m_boatReplicaVelocityZeroed +
            " mountReplica=" + m_localMountReplicaHits +
            " entityLost=" + m_entityLost +
            " keyMismatch=" + m_keyMismatch +
            " inRegion=" + m_replicasInRegion +
            " outRegion=" + m_replicasOutOfRegion +
            " nearest=" + DescribeDistance(m_nearestReplicaDistance) +
            " farthest=" + DescribeDistance(m_farthestReplicaDistance) +
            " visibilityChecks=" + m_visibilityChecks +
            " viewScopes=" + m_viewScopeRuns +
            " despawnScopes=" + m_despawnScopeRuns +
            " healthFail=" + m_healthApplyFailures +
            " despawnProtectFail=" + m_despawnProtectionFailures +
            " viewScopeFail=" + m_viewScopeFailures +
            " areaReadFail=" + m_areaByBodyReadFailures +
            " shapeshiftType=" + m_shapeshiftMember.BoundTypeName +
            " shapeshiftState=" + m_shapeshiftMember.State +
            " shapeshiftMismatch=" + m_shapeshiftMember.Mismatches +
            " shapeshiftSkips=" + m_shapeshiftMember.SkippedReads;

        private void Warn(string message) => m_diagnostics?.Warn("creature-spawn", message);

        private void Info(string message) => m_diagnostics?.Info("creature-spawn", message);

        /// <summary>限流日志（热路径禁止每帧写日志；计数器才是每帧事实）。</summary>
        private void LogThrottled(string key, string message, int intervalMs, bool error = false)
        {
            long nowMs = (long)(m_now * 1000.0);
            if (m_lastLogTimes.TryGetValue(key, out long last) && nowMs - last < intervalMs) return;
            m_lastLogTimes[key] = nowMs;
            if (error) Warn(message); else Info(message);
        }

        private static string DescribeDistance(float distance) =>
            float.IsFinite(distance) ? distance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a";
    }
}
