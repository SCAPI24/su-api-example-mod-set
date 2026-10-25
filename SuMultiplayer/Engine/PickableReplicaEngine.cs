using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 拾取物（Pickable）/ 投射物（Projectile）复制的**引擎适配层**
    /// （<see cref="IEntityReplicaPort"/> 的实现，组合根注入）。
    ///
    /// 主机：
    ///   · 每 32Hz 脉冲抓 <c>SubsystemPickables.Pickables</c> / <c>SubsystemProjectiles.Projectiles</c>，
    ///     按对象分配稳定的网络 id（对象消失即从映射里剪掉，模块据此发 REMOVE）；
    ///   · 执行客户端拾取请求：距离 / 创建宽限 / 背包容量都由本端引擎判断，
    ///     用引擎自己的 <c>ComponentInventoryBase.AcquireItems</c> 落地，并回传稀疏背包增量；
    ///   · 执行客户端丢弃 / 投掷 / 开火请求：校验玩家端 / 活跃槽 / 同种物品 / 距离，用引擎自己的
    ///     <c>SubsystemPickables.AddPickable</c> / <c>SubsystemProjectiles.FireProjectile</c> 生成权威实体，
    ///     消费道具 / 耐久后回传稀疏背包增量（与对应方块行为的 OnAim(Completed) 同形）；
    ///   · 把远端化身的“原生拾取”从引擎的拾取列表里摘掉：客户端拾取必须走请求链，
    ///     否则主机引擎会直接把掉落物塞进远端化身背包，客户端看不到背包变化、也没有回执。
    /// 客户端：
    ///   · 只创建 / 更新 / 移除引擎里的 Pickable / Projectile **副本**；
    ///   · 原生拾取由 <c>SuSubsystemPickables</c>（客户端清空玩家列表）抑制；
    ///   · 丢弃输入（Q / 手柄 B）由 <c>SuComponentInput</c> 在 ComponentPlayer.Update 之前拦下，
    ///     只发请求、**不本地扣物品**；投掷 / 开火由方块行为外壳只发请求、**不本地生成投射物**；
    ///   · 投射物**不跑** <c>SubsystemProjectiles.Update</c>（它会在客户端 DestroyCell / 伤人 /
    ///     生成掉落物 / 触发爆炸），位置由本适配层按主机快照插值；
    ///   · 拾取由主机引擎对化身原生计算（2026-10-26 起）：主机在拾取窗口差分化身背包，
    ///     经 <see cref="HostNativePickup"/> 通知客户端，客户端不再扫描 / 请求。
    ///
    /// 无静态状态：反射入口 / 化身表 / 计数器都挂在实例上。
    /// </summary>
    public sealed class PickableReplicaEngine : IEntityReplicaPort, IEntityDropInputSink,
        IHostBlockDropSink, IUiDropSink, IPickupHostWindowSink
    {
        private const float RestingPositionEpsilonSquared = 0.0004f;
        private const int ProblemLogMs = 5000;

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly InteractionReachGate m_reachGate;
        private readonly IModParentField m_parentField;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<string> m_loggedOnce = new HashSet<string>(StringComparer.Ordinal);

        // ---- 客户端丢弃输入拦截（SuComponentInput 每帧调用）
        /// <summary>
        /// ComponentInput.m_playerInput 的 ref 入口（BindFieldRef 一次绑定、每帧无反射）：
        /// 采集到丢弃意图后把 Drop 标志清掉，引擎的 ComponentPlayer.Update 因此不会本地扣物品 +
        /// 本地 AddPickable（那会生成一个被"外来拾取物"清理掉的幽灵，物品直接消失）。
        /// </summary>
        private readonly ModFieldRef<ComponentInput, PlayerInput> m_inputRef;
        private IEntityRequestSink m_requestSink;
        private SuComponentInput m_dropInput;
        private bool m_dropInputInjectionMissingLogged;
        private long m_dropIntentSubmissions;
        private long m_dropIntentFailures;
        private long m_fireIntentSubmissions;
        private long m_fireIntentFailures;
        private long m_dropAccepted;
        private long m_dropRejected;
        private long m_fireAccepted;
        private long m_fireRejected;
        private readonly global::Engine.Random m_requestRandom = new global::Engine.Random();

        // ---- 客户端 UI 拖出（inventory drag-to-world）拦截计数 / 安全网
        /// <summary>客户端：UI 拖出意图交给请求链的次数（uiDropIntercepted 的引擎侧口径）。</summary>
        private long m_uiDropIntents;
        /// <summary>客户端：UI 拖出请求没能发出去（模块未就绪 / 发送失败）→ 拖出已撤销，物品留在背包。</summary>
        private long m_uiDropSubmitFailures;
        /// <summary>客户端：已创建、又被安全网撤销的 UI 幽灵拾取物数量（uiDropLocalPickableCancelled）。</summary>
        private long m_uiDropLocalPickableCancelled;
        /// <summary>客户端：在**创建之前**就被拦截、没有产生本地拾取物的 UI 拖出数量（dropPhantomSuppressed）。</summary>
        private long m_dropPhantomSuppressed;
        /// <summary>客户端：落在相机视点、但无法唯一匹配背包增量的本地拾取物数量（明确计数，绝不静默）。</summary>
        private long m_uiDropPhantomUnmatched;
        /// <summary>UI 拖出安全网用的"上一帧背包快照"（Pump / 组合根每帧末刷新）。</summary>
        private int[] m_localInventorySnapshotValues;
        private int[] m_localInventorySnapshotCounts;
        private bool m_localInventorySnapshotValid;

        // ---- 引擎挂接
        private Project m_project;
        private SubsystemPickables m_pickables;
        private SubsystemProjectiles m_projectiles;
        private SubsystemTerrain m_terrainSubsystem;
        private SubsystemBlockBehaviors m_blockBehaviors;
        private SuSubsystemPickables m_pickableShell;
        private bool m_clientReplica;

        // ---- 主机：方块销毁掉落（客户端编辑补生成）
        /// <summary>GetDropValues 的复用缓冲（与引擎 DestroyCell 的 m_dropValues 同口径）。</summary>
        private readonly List<BlockDropValue> m_dropScratch = new List<BlockDropValue>();
        /// <summary>掉落者拾取宽限（按主机分配的拾取物 id；1.5s 内拒绝掉落者本人的拾取请求）。</summary>
        private readonly PickupGraceTable m_dropGrace = new PickupGraceTable();
        /// <summary>客户端：本机刚造成的掉落预测（认领主机副本 → 给副本登记拾取宽限）。</summary>
        private readonly OwnDropPredictionTable m_ownDropPredictions = new OwnDropPredictionTable();
        /// <summary>为客户端编辑补生成的拾取物个数（blockDropsSpawned）。</summary>
        private long m_blockDropsSpawned;
        /// <summary>主机拒绝掉落者本人拾取的次数（pickupGraceBlocked）。</summary>
        private long m_pickupGraceBlocked;
        /// <summary>主机生成拾取物时带上了速度的个数（dropSpawnVelocity）：
        /// 手丢 = 8 m/s × 视线方向（引擎 Drop 分支同形）；方块破坏 = 引擎 AddPickable(velocity: null) 的默认随机上抛。</summary>
        private long m_dropSpawnVelocity;
        private long m_dropSpawnFailures;
        private long m_dropPrepareFailures;

        // ---- 主机：对象 → 网络 id
        private struct HostKey
        {
            public int Id;
            public long Generation;
        }

        private readonly Dictionary<Pickable, HostKey> m_pickableKeys =
            new Dictionary<Pickable, HostKey>();
        private readonly Dictionary<int, Pickable> m_pickableById =
            new Dictionary<int, Pickable>();
        private readonly Dictionary<int, Vector3> m_publishedPickablePositions =
            new Dictionary<int, Vector3>();
        private readonly Dictionary<Projectile, HostKey> m_projectileKeys =
            new Dictionary<Projectile, HostKey>();
        private readonly Dictionary<int, Projectile> m_projectileById =
            new Dictionary<int, Projectile>();
        private long m_pickableGeneration;
        private long m_projectileGeneration;
        private int m_nextPickableId = 1;
        private int m_nextProjectileId = 1;

        // ---- 客户端：副本
        private sealed class ClientPickableReplica
        {
            public Pickable Pickable;
            public Vector3 TargetPosition;
            public Vector3 TargetVelocity;
            public bool TargetHasFlyTo;
            public Vector3 TargetFlyTo;
            public Vector3 PreviousTargetPosition;
            public bool HasPreviousTargetPosition;
            public Vector3 PresentationVelocity;
            public bool Initialized;
            public double LastUpdateTime;
        }

        private sealed class ClientProjectileReplica
        {
            public Projectile Projectile;
            public Vector3 TargetPosition;
            public Vector3 TargetVelocity;
            public Vector3 TargetRotation;
            public Vector3 TargetAngularVelocity;
            public Vector3 TargetTrailOffset;
            public bool TargetStuck;
            public bool TargetIncendiary;
            public bool Initialized;
            public double LastUpdateTime;
        }

        private readonly Dictionary<int, ClientPickableReplica> m_clientPickables =
            new Dictionary<int, ClientPickableReplica>();
        private readonly Dictionary<int, ClientProjectileReplica> m_clientProjectiles =
            new Dictionary<int, ClientProjectileReplica>();

        // ---- 主机拾取捕获窗口（IPickupHostWindowSink；见 PickableReplicaSubsystems.cs 的窗口包法）
        private bool m_pickupWindowActive;
        private readonly List<Pickable> m_windowRemovedPickables = new List<Pickable>();
        private sealed class AvatarInventorySnapshot
        {
            public int ClientId;
            public ComponentPlayer Player;
            public int[] Values;
            public int[] Counts;
        }
        private List<AvatarInventorySnapshot> m_windowAvatarInventories;

        // ---- 反射：SubsystemProjectiles 的私有列表（客户端删除副本时必须用）
        private List<Projectile> m_projectileList;
        private SubsystemProjectiles m_projectileListOwner;
        private List<Pickable> m_pickableList;
        private SubsystemPickables m_pickableListOwner;

        // ---- 计数
        private long m_hostCaptures;
        private long m_idAssignments;
        private long m_replicasCreated;
        private long m_replicasUpdated;
        private long m_replicasRemoved;
        private long m_applyFailures;
        private long m_acquireAccepted;
        private long m_acquireRejected;
        private long m_hostNativePickups;
        private long m_hostNativePickupFaults;
        private long m_avatarsInjected;
        private long m_projectileListFailures;
        private long m_pickableListFailures;
        private long m_foreignProjectilesRemoved;
        private long m_foreignPickablesRemoved;
        private long m_fireSpawnAtHost;
        private long m_fireSpawnCollided;
        private long m_dropPredictionCulled;
        private long m_dropPredictionDeferred;

        /// <summary>客户端刚提交的丢弃意图：本地若还残留预测拾取物，等主机副本出现后再清掉。</summary>
        private sealed class PendingDropPrediction
        {
            public int ItemValue;
            public int Count;
            public Vector3 Position;
            public double ExpiryTime;
        }

        private readonly List<PendingDropPrediction> m_recentDropPredictions =
            new List<PendingDropPrediction>();

        /// <summary>客户端本机有效交互距离（引擎 reach），供拾取 / 丢弃 / 开火请求携带。</summary>
        public float LocalReach => EngineReach.LocalReach();

        public PickableReplicaEngine(NetworkAvatarSpawner spawner, IModParentField parentField,
            IModuleDiagnostics diagnostics, string ownerId, InteractionReachGate reachGate)
        {
            m_spawner = spawner;
            m_reachGate = reachGate ?? throw new ArgumentNullException(nameof(reachGate));
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "entity-replication" : ownerId;
            // 丢弃拦截依赖 ComponentInput.m_playerInput：绑不上就是明确的装配失败（绝不静默）：
            // 没有这条路径，客户端按 Q 会走引擎原生掉落 → 本地扣除 + 本地拾取物被清理 → 物品消失。
            try
            {
                m_inputRef = m_parentField.BindFieldRef<ComponentInput, PlayerInput>("m_playerInput");
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "无法绑定 ComponentInput.m_playerInput：客户端丢弃请求无法在本地扣除之前拦截" +
                    "（引擎版本不匹配？）", e);
            }
        }

        /// <summary>
        /// 主机：引擎原生替某个化身拾取了物品（窗口内背包差分得到）。
        /// 模块把它编码成 <c>PickableAcquireResult(sequence=0)</c> 定向发给该客户端，
        /// 并中继拾取音（引擎已在主机播过，不再回放主机）。
        /// **2026-10-26 架构口径**：拾取由主机模拟（引擎对化身的原生 1.75m 磁吸 + 1.0m 收取，
        /// 与主机玩家同一段代码、同款手感），客户端不再扫描 / 请求 —— 物品在主机真实飞行，
        /// 经实体复制把"被吸起到身上"的动画镜像给所有端。
        /// </summary>
        public event Action<int, PickableAcquireOutcome> HostNativePickup;

        public bool IsClientReplica => m_clientReplica;

        public bool IsReplicaReady => m_project != null && m_pickables != null &&
            m_projectiles != null;

        /// <summary>模块在 Attach 里挂上的请求入口（客户端丢弃 / 投掷 / 开火）。</summary>
        public IEntityRequestSink RequestSink
        {
            get => m_requestSink;
            set => m_requestSink = value;
        }

        public long ForeignPickablesRemoved => m_foreignPickablesRemoved;
        public long FireSpawnAtHost => m_fireSpawnAtHost;
        public long FireSpawnCollided => m_fireSpawnCollided;
        public long DropPredictionCulled => m_dropPredictionCulled;
        public long DropPredictionDeferred => m_dropPredictionDeferred;

        public long ForeignProjectilesRemoved => m_foreignProjectilesRemoved;

        /// <summary>UI 拖出拦截器只在客户端副本上接管；主机 / 单机保持引擎原生拖出行为。</summary>
        public bool IsUiDropInterceptionClient => m_clientReplica;

        /// <summary>客户端：交给请求链的 UI 拖出意图数量。</summary>
        public long UiDropIntents => m_uiDropIntents;
        /// <summary>客户端：UI 拖出请求发送失败（拖出已撤销）次数。</summary>
        public long UiDropSubmitFailures => m_uiDropSubmitFailures;
        /// <summary>客户端：已生成、又被安全网撤销的 UI 幽灵拾取物数量。</summary>
        public long UiDropLocalPickableCancelled => m_uiDropLocalPickableCancelled;
        /// <summary>客户端：在创建之前就被拦截的 UI 拾取物数量（幽灵从源头抑制）。</summary>
        public long DropPhantomSuppressed => m_dropPhantomSuppressed;
        /// <summary>客户端：落在相机视点但无法唯一匹配背包增量的本地拾取物数量。</summary>
        public long UiDropPhantomUnmatched => m_uiDropPhantomUnmatched;

        // ================================================================ 挂接

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                ResetEngineState("project-change");
                m_project = project;
            }
            if (project == null) return;

            bool clientReplica = project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain
                shell && shell.ClientReplica;
            if (clientReplica != m_clientReplica)
            {
                ResetEngineState("role-change");
                m_clientReplica = clientReplica;
            }

            SubsystemPickables pickables = project.FindSubsystem<SubsystemPickables>(false);
            if (!ReferenceEquals(pickables, m_pickables))
            {
                if (m_pickables != null)
                {
                    // 换子系统（世界重载）：摘掉旧实例上的移除订阅，避免悬挂回调。
                    try { m_pickables.PickableRemoved -= OnHostPickableRemoved; }
                    catch (Exception) { }
                }
                m_pickables = pickables;
                m_pickableShell = pickables as SuSubsystemPickables;
                m_pickableList = null;
                m_pickableListOwner = null;
                m_pickableById.Clear();
                m_pickableKeys.Clear();
                m_publishedPickablePositions.Clear();
                if (pickables != null)
                {
                    // 引擎拾取物移除事件：主机拾取窗口内记录，用于原生拾取的归属与拾取音。
                    try { pickables.PickableRemoved += OnHostPickableRemoved; }
                    catch (Exception) { }
                }
                Log("已挂接拾取物子系统：" + (pickables == null
                    ? "缺失（注入可能未生效）"
                    : pickables.GetType().Name));
                if (pickables != null && !(pickables is SuSubsystemPickables))
                {
                    LogProblemOnce("pickable-shell-missing",
                        (m_clientReplica ? "客户端" : "主机") +
                        "没有注入 SuSubsystemPickables（当前：" + pickables.GetType().Name +
                        "）→ " + (m_clientReplica
                            ? "引擎会本地拾取主机物品"
                            : "远端化身会被引擎原生拾取（绕过请求链）") +
                        "；请检查 Game.SubsystemPickables 注入");
                }
            }
            SubsystemProjectiles projectiles =
                project.FindSubsystem<SubsystemProjectiles>(false);
            if (!ReferenceEquals(projectiles, m_projectiles))
            {
                m_projectiles = projectiles;
                if (m_projectiles is SuSubsystemProjectiles projectileShell)
                    projectileShell.Replica = this;
                m_projectileList = null;
                m_projectileListOwner = null;
                m_projectileById.Clear();
                m_projectileKeys.Clear();
                Log("已挂接投射物子系统：" + (projectiles == null
                    ? "缺失（注入可能未生效）"
                    : projectiles.GetType().Name));
                if (projectiles != null && !(projectiles is SuSubsystemProjectiles))
                {
                    LogProblemOnce("projectile-shell-missing",
                        (m_clientReplica ? "客户端" : "主机") +
                        "没有注入 SuSubsystemProjectiles（当前：" +
                        projectiles.GetType().Name + "）→ " + (m_clientReplica
                            ? "引擎会在客户端跑投射物模拟（写地形 / 伤人 / 生成掉落物）"
                            : "主机投射物照常模拟") + "；请检查注入");
                }
            }
            if (m_projectiles is SuSubsystemProjectiles shellProjectiles)
                shellProjectiles.Replica = this;
            // 主机掉落：GetDropValues / OnItemHarvested 需要真实的 SubsystemTerrain 与方块行为表。
            // 每帧取一次（FindSubsystem 是字典查找，代价可忽略）；换 Project 时随 ResetEngineState 清空。
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(false);
            if (!ReferenceEquals(terrain, m_terrainSubsystem))
            {
                m_terrainSubsystem = terrain;
                m_blockBehaviors = project.FindSubsystem<SubsystemBlockBehaviors>(false);
                if (!m_clientReplica && (terrain == null || m_blockBehaviors == null))
                {
                    LogProblemOnce("host-drop-subsystem-missing",
                        "主机没有挂接 SubsystemTerrain / SubsystemBlockBehaviors（当前：" +
                        (terrain == null ? "terrain=null" : "terrain=" + terrain.GetType().Name) +
                        " / " + (m_blockBehaviors == null
                            ? "blockBehaviors=null"
                            : "blockBehaviors=" + m_blockBehaviors.GetType().Name) +
                        "）→ 客户端编辑销毁方块时无法补生成掉落（会明确计 dropFromEditSkipped）");
                }
            }
            if (m_blockBehaviors == null)
                m_blockBehaviors = project.FindSubsystem<SubsystemBlockBehaviors>(false);
            // 主机：引擎对化身**原生拾取**（2026-10-26 架构口径：拾取由主机模拟）。
            // 只装"自丢宽限内"的化身抑制器（防刚丢出去被自己捡回）；拾取捕获窗口 +
            // PickableRemoved 订阅（在上方子系统换接块里）用于差分背包、发
            // PickableAcquireResult(sequence=0) 通知。客户端由 SuSubsystemPickables 自己按
            // ReplicaGates.IsClient 清空玩家列表。
            if (m_pickableShell != null)
            {
                m_pickableShell.ParentField = m_parentField;
                m_pickableShell.Replica = this;
                m_pickableShell.AvatarPlayersProvider = m_clientReplica
                    ? null
                    : EnumerateAvatarPlayers;
                m_pickableShell.PickupWindowSink = m_clientReplica ? null : this;
                LogOnce("pickup-window-wired",
                    "拾取窗口挂接：clientReplica=" + m_clientReplica +
                    " provider=" + (!m_clientReplica) + " suppressor=" + (!m_clientReplica) +
                    " window=" + (!m_clientReplica) +
                    "（主机=把化身注入引擎拾取循环原生拾取；客户端=纯表现）");
            }
            // 客户端：把丢弃输入钩子挂到本机玩家的 SuComponentInput 上（主机 / 单机不挂）。
            UpdateDropInput(project);
        }

        /// <summary>
        /// 客户端丢弃输入钩子的挂接：找到本机玩家的 <see cref="SuComponentInput"/> 并把本适配器
        /// 作为 sink 写进去（实例属性，**没有任何静态状态**）。主机会摘掉它（主机自己的丢弃
        /// 走原生路径，由引擎权威执行后被实体复制抓走）。
        /// </summary>
        private void UpdateDropInput(Project project)
        {
            if (!m_clientReplica)
            {
                DetachDropInput();
                return;
            }
            if (project == null || m_spawner == null)
            {
                DetachDropInput();
                return;
            }
            ComponentPlayer local = null;
            if (m_spawner.TryGetLocalPlayer(project, out PlayerData localData, out _))
                local = localData?.ComponentPlayer;
            ComponentInput input = local?.Entity?.FindComponent<ComponentInput>();
            if (input == null)
            {
                DetachDropInput();
                return;
            }
            if (!(input is SuComponentInput shell))
            {
                if (!m_dropInputInjectionMissingLogged)
                {
                    m_dropInputInjectionMissingLogged = true;
                    LogProblemOnce("drop-input-injection-missing",
                        "客户端没有注入 SuComponentInput（当前：" + input.GetType().Name +
                        "）→ 丢弃无法在本地扣除之前拦截，物品可能被引擎原生掉落 + 清理（请检查 " +
                        "Game.ComponentInput 注入）");
                }
                DetachDropInput();
                return;
            }
            if (!ReferenceEquals(shell, m_dropInput))
            {
                DetachDropInput();
                m_dropInput = shell;
            }
            shell.DropSink = this;
        }

        private void DetachDropInput()
        {
            if (m_dropInput == null) return;
            if (ReferenceEquals(m_dropInput.DropSink, this)) m_dropInput.DropSink = null;
            m_dropInput = null;
        }

        private void ResetEngineState(string reason)
        {
            DetachDropInput();
            RemoveAllClientReplicas(reason);
            m_pickableKeys.Clear();
            m_pickableById.Clear();
            m_publishedPickablePositions.Clear();
            m_projectileKeys.Clear();
            m_projectileById.Clear();
            m_pickableGeneration = 0;
            m_projectileGeneration = 0;
            m_nextPickableId = 1;
            m_nextProjectileId = 1;
            m_projectileList = null;
            m_projectileListOwner = null;
            m_pickableList = null;
            m_pickableListOwner = null;
            m_pickables = null;
            m_projectiles = null;
            m_pickableShell = null;
            m_terrainSubsystem = null;
            m_blockBehaviors = null;
            m_dropScratch.Clear();
            m_dropGrace.Clear();
            m_ownDropPredictions.Clear();
            m_recentDropPredictions.Clear();
        }

        /// <summary>
        /// 主机过滤器：这个 ComponentPlayer 是不是某个远端玩家端的联机化身。
        /// true ⇒ 在 <c>SuSubsystemPickables</c> 的 base.Update 期间把它从玩家列表里摘掉，
        /// 于是主机不会替客户端“本地拾取”；客户端只能通过请求链拾取。
        /// </summary>
        // ================================================================ 主机：抓取

        public bool TryCaptureHostPickables(List<PickableStateData> target, out string reason)
        {
            target.Clear();
            if (m_clientReplica)
            {
                reason = "client-replica";
                return false;
            }
            if (m_pickables == null)
            {
                reason = "no-pickables-subsystem";
                return false;
            }
            m_hostCaptures++;
            m_pickableGeneration++;
            // 顺带清掉过期的掉落者宽限条目（32Hz 抓取脉冲；有界、成本与宽限条数成正比）。
            m_dropGrace.Prune(Time.RealTime);
            ReadOnlyList<Pickable> list = m_pickables.Pickables;
            for (int i = 0; i < list.Count; i++)
            {
                Pickable pickable = list[i];
                if (pickable == null || pickable.ToRemove) continue;
                int id = EnsurePickableId(pickable);
                Vector3 velocity = pickable.Velocity;
                // Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:203-215
                // 引擎静止的拾取物 velocity 仍有 ±0.1~0.5 m/s 残余；位置不变时按 0 发布，
                // 否则客户端外推会“往地下沉、再被拉回”。
                if (!pickable.FlyToPosition.HasValue &&
                    m_publishedPickablePositions.TryGetValue(id, out Vector3 previous) &&
                    Vector3.DistanceSquared(previous, pickable.Position) <
                        RestingPositionEpsilonSquared)
                    velocity = Vector3.Zero;
                m_publishedPickablePositions[id] = pickable.Position;
                bool hasStuck = pickable.StuckMatrix.HasValue;
                Vector3 stuckPosition = Vector3.Zero;
                Quaternion stuckRotation = Quaternion.Identity;
                if (hasStuck)
                {
                    Matrix matrix = pickable.StuckMatrix.Value;
                    stuckPosition = matrix.Translation;
                    stuckRotation = Quaternion.CreateFromRotationMatrix(matrix);
                }
                target.Add(new PickableStateData(id, EntityActions.Add, pickable.Value,
                    pickable.Count, pickable.Position, velocity, hasStuck, stuckPosition,
                    stuckRotation, pickable.FlyToPosition.HasValue,
                    pickable.FlyToPosition ?? Vector3.Zero, -1));
            }
            PrunePickableKeys();
            reason = null;
            return true;
        }

        public bool TryCaptureHostProjectiles(List<ProjectileStateData> target, out string reason)
        {
            target.Clear();
            if (m_clientReplica)
            {
                reason = "client-replica";
                return false;
            }
            if (m_projectiles == null)
            {
                reason = "no-projectiles-subsystem";
                return false;
            }
            m_hostCaptures++;
            m_projectileGeneration++;
            ReadOnlyList<Projectile> list = m_projectiles.Projectiles;
            for (int i = 0; i < list.Count; i++)
            {
                Projectile projectile = list[i];
                if (projectile == null || projectile.ToRemove) continue;
                int id = EnsureProjectileId(projectile);
                int ownerClientId = ResolveOwnerClientId(projectile);
                bool stuck = projectile.Velocity.LengthSquared() < 0.25f;
                target.Add(new ProjectileStateData(id, EntityActions.Add, projectile.Value,
                    projectile.Position, projectile.Velocity, projectile.Rotation,
                    projectile.AngularVelocity, ownerClientId, stuck, projectile.IsIncendiary,
                    projectile.TrailOffset));
            }
            PruneProjectileKeys();
            reason = null;
            return true;
        }

        private int EnsurePickableId(Pickable pickable)
        {
            if (m_pickableKeys.TryGetValue(pickable, out HostKey entry))
            {
                entry.Generation = m_pickableGeneration;
                m_pickableKeys[pickable] = entry;
                return entry.Id;
            }
            int id = NextId(ref m_nextPickableId, m_pickableById);
            m_pickableKeys[pickable] = new HostKey { Id = id, Generation = m_pickableGeneration };
            m_pickableById[id] = pickable;
            m_idAssignments++;
            return id;
        }

        private int EnsureProjectileId(Projectile projectile)
        {
            if (m_projectileKeys.TryGetValue(projectile, out HostKey entry))
            {
                entry.Generation = m_projectileGeneration;
                m_projectileKeys[projectile] = entry;
                return entry.Id;
            }
            int id = NextId(ref m_nextProjectileId, m_projectileById);
            m_projectileKeys[projectile] = new HostKey
            {
                Id = id,
                Generation = m_projectileGeneration
            };
            m_projectileById[id] = projectile;
            m_idAssignments++;
            return id;
        }

        private static int NextId<T>(ref int next, Dictionary<int, T> used)
        {
            int id = next;
            if (id <= 0 || id >= int.MaxValue) id = 1;
            while (id <= 0 || id > EntityReplicationLimits.MaxNetworkId ||
                used.ContainsKey(id))
            {
                id++;
                if (id > EntityReplicationLimits.MaxNetworkId) id = 1;
            }
            next = id + 1;
            return id;
        }

        private void PrunePickableKeys()
        {
            List<Pickable> stale = null;
            foreach (KeyValuePair<Pickable, HostKey> item in m_pickableKeys)
            {
                if (item.Value.Generation == m_pickableGeneration) continue;
                if (stale == null) stale = new List<Pickable>();
                stale.Add(item.Key);
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++)
            {
                if (!m_pickableKeys.TryGetValue(stale[i], out HostKey entry)) continue;
                m_pickableKeys.Remove(stale[i]);
                m_pickableById.Remove(entry.Id);
                m_publishedPickablePositions.Remove(entry.Id);
            }
        }

        private void PruneProjectileKeys()
        {
            List<Projectile> stale = null;
            foreach (KeyValuePair<Projectile, HostKey> item in m_projectileKeys)
            {
                if (item.Value.Generation == m_projectileGeneration) continue;
                if (stale == null) stale = new List<Projectile>();
                stale.Add(item.Key);
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++)
            {
                if (!m_projectileKeys.TryGetValue(stale[i], out HostKey entry)) continue;
                m_projectileKeys.Remove(stale[i]);
                m_projectileById.Remove(entry.Id);
            }
        }

        private int ResolveOwnerClientId(Projectile projectile)
        {
            ComponentPlayer owner = projectile?.Owner?.Entity?.FindComponent<ComponentPlayer>();
            if (owner == null || owner.PlayerData == null || m_spawner == null ||
                m_project == null)
                return -1;
            foreach (int clientId in m_spawner.ClientIds)
            {
                if (m_spawner.TryGet(clientId, out PlayerData data) &&
                    ReferenceEquals(data, owner.PlayerData))
                    return clientId;
            }
            if (m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out _) &&
                ReferenceEquals(local, owner.PlayerData))
                return 0;
            return -1;
        }

        // ================================================================ 主机：执行拾取

        public bool TryAcquire(int clientId, in PickableAcquireRequestData request,
            out PickableAcquireOutcome outcome, out string reason)
        {
            try
            {
                bool accepted = TryAcquireCore(clientId, in request, out outcome, out reason);
                if (!accepted) m_acquireRejected++;
                return accepted;
            }
            catch (Exception e)
            {
                // 引擎的背包 API 抛异常也必须变成**明确拒绝**（回执带原因），
                // 不能让模块 tick 异常中断、客户端干等超时。
                outcome = default;
                reason = "host-exception-" + e.GetType().Name;
                m_acquireRejected++;
                LogRateLimited("acquire-exception:" + e.GetType().Name,
                    "执行拾取请求时引擎异常（clientId=" + clientId + " pickable=" +
                    request.PickableId + "）：" + e.GetType().Name + " " + e.Message +
                    " → 明确拒绝（不静默成功）");
                return false;
            }
        }

        private bool TryAcquireCore(int clientId, in PickableAcquireRequestData request,
            out PickableAcquireOutcome outcome, out string reason)
        {
            outcome = default;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (m_project == null)
            {
                reason = "no-project";
                return false;
            }
            if (clientId <= 0 || m_spawner == null)
            {
                reason = "not-player-peer";
                return false;
            }
            if (!m_pickableById.TryGetValue(request.PickableId, out Pickable pickable) ||
                pickable == null || pickable.ToRemove)
            {
                reason = "unknown-pickable";
                return false;
            }
            if (!m_spawner.TryGet(clientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer == null)
            {
                reason = "no-avatar";
                return false;
            }
            ComponentPlayer player = playerData.ComponentPlayer;
            ComponentHealth health = player.ComponentHealth;
            if (health == null)
            {
                reason = "no-health";
                return false;
            }
            if (health.Health <= 0f)
            {
                reason = "dead";
                return false;
            }
            ComponentBody body = player.ComponentBody;
            if (body == null)
            {
                reason = "no-body";
                return false;
            }
            SubsystemGameInfo gameInfo = m_project.FindSubsystem<SubsystemGameInfo>(false);
            if (gameInfo == null)
            {
                reason = "no-game-info";
                return false;
            }
            // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
            // 创建后 0.5s 内不允许拾取（与引擎同一条宽限）。
            if (gameInfo.TotalElapsedGameTime - pickable.CreationTime <= 0.5)
            {
                reason = "creation-grace";
                return false;
            }
            // 掉落者免疫窗口（**独家**给掉落者本人；其他玩家照常可捡）：
            // 手丢 / 方块破坏的落点就在脚下，引擎只有 0.5s 创建宽限，20Hz 的客户端扫描会在
            // 1.0m 内立刻发请求 → 刚丢出去就被自己捡回来。1.5s 内明确回 "pickup-grace"。
            if (!m_dropGrace.CanAcquire(request.PickableId, clientId, Time.RealTime))
            {
                reason = "pickup-grace";
                m_pickupGraceBlocked++;
                return false;
            }
            Vector3 eye = body.Position + new Vector3(0f, 0.75f, 0f);
            // 与引擎原生拾取口径一致：眼位到物品 <1.75m 属于"可被吸引"范围（客户端也正是先进入吸引
            // 范围、再在 1.0m 内发拾取请求）。主机阈值必须**宽于**客户端的 1.0m 触发点，否则两端
            // 插值/一个 RTT 的移动量/化身与客户端的细微差值会让合法请求被判 distance（真机复现：
            // "客户端要贴得很近才捡得起来"）。仍然只按**主机自己的**化身位置判断，客户端请求里的
            // position 不作数。
            // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
            //         （num6 < 3.0625f 吸引；num6 < 1f 真正 AcquireItems）
            if (!m_reachGate.TryCheckDistance(request.HasReach, request.Reach,
                    EngineReach.IsCreative(m_project),
                    MathUtils.Sqrt(Vector3.DistanceSquared(eye, pickable.Position)),
                    out string reachReason))
            {
                reason = reachReason;
                return false;
            }
            int contents = Game.Terrain.ExtractContents(pickable.Value);
            if (contents <= 0 || BlocksManager.Blocks == null ||
                contents >= BlocksManager.Blocks.Length)
            {
                reason = "item-illegal";
                return false;
            }
            if (contents == 248)
            {
                // 经验币：引擎口径是 AddExperience(count)，不进背包。
                // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
                if (player.ComponentLevel == null)
                {
                    reason = "no-level";
                    return false;
                }
                player.ComponentLevel.AddExperience(pickable.Count, playSound: true);
                pickable.Count = 0;
                m_acquireAccepted++;
                bool removedCoin = RemoveHostPickable(request.PickableId, pickable);
                // 经验币由引擎 AddExperience 播 Audio/ExperienceCollected，不是 PickableCollected：
                // 这里明确 PlayPickupSound=false（不冒充另一种声音）。
                outcome = new PickableAcquireOutcome(0, removedCoin, Array.Empty<int>(),
                    Array.Empty<int>(), Array.Empty<int>(), CurrentInventoryVersion(),
                    pickable.Value, pickable.Position, playPickupSound: false);
                reason = null;
                return true;
            }
            ComponentMiner miner = player.ComponentMiner;
            IInventory inventory = miner?.Inventory;
            if (inventory == null)
            {
                reason = "no-inventory";
                return false;
            }
            int acquireSlot = ComponentInventoryBase.FindAcquireSlotForItem(inventory,
                pickable.Value);
            if (acquireSlot < 0)
            {
                reason = "inventory-full";
                return false;
            }
            int slots = inventory.SlotsCount;
            var beforeValues = new int[slots];
            var beforeCounts = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                beforeValues[i] = inventory.GetSlotValue(i);
                beforeCounts[i] = inventory.GetSlotCount(i);
            }
            int previousCount = pickable.Count;
            // Source: Survivalcraft/Game/ComponentInventoryBase.cs:ComponentInventoryBase.AcquireItems
            pickable.Count = ComponentInventoryBase.AcquireItems(inventory, pickable.Value,
                pickable.Count);
            if (pickable.Count >= previousCount)
            {
                reason = "acquire-refused";
                return false;
            }
            var afterValues = new int[slots];
            var afterCounts = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                afterValues[i] = inventory.GetSlotValue(i);
                afterCounts[i] = inventory.GetSlotCount(i);
            }
            BuildInventoryDelta(beforeValues, beforeCounts, afterValues, afterCounts,
                acquireSlot, out int[] indices, out int[] values, out int[] counts);
            bool removed = pickable.Count == 0;
            if (removed) RemoveHostPickable(request.PickableId, pickable);
            m_acquireAccepted++;
            // 与 SubsystemPickables.Update 同口径：整堆拾完才播 Audio/PickableCollected
            // （Source: Survivalcraft/Game/SubsystemPickables.cs:210-213）；部分拾取不播。
            outcome = new PickableAcquireOutcome(pickable.Count, removed, indices, values, counts,
                CurrentInventoryVersion(), pickable.Value, pickable.Position,
                playPickupSound: removed && contents != 248);
            reason = null;
            return true;
        }

        private bool RemoveHostPickable(int id, Pickable pickable)
        {
            if (pickable != null) pickable.ToRemove = true;
            m_pickableById.Remove(id);
            if (pickable != null) m_pickableKeys.Remove(pickable);
            m_publishedPickablePositions.Remove(id);
            // 对象已经没了：宽限条目也一起清掉（避免 id 之后被复用时误挡新对象）。
            m_dropGrace.Remove(id);
            return pickable != null;
        }

        private int m_inventoryVersion;

        private int CurrentInventoryVersion() => ++m_inventoryVersion;

        /// <summary>
        /// 稀疏背包增量：只把真的变了的格子发回拾取者（与旧实现 TryBuildInventoryDelta 同口径）。
        /// 增量算不出来（同值格被写成同值，例如创造模式无限仓库）时，至少回“该物品所在格”的权威值。
        /// Source: Mod/ScMultiplayer/Modules/Entity/ScMultiplayerPickableProjectileHandlers.cs:345-395
        /// </summary>
        private static void BuildInventoryDelta(int[] beforeValues, int[] beforeCounts,
            int[] afterValues, int[] afterCounts, int fallbackSlot, out int[] indices,
            out int[] values, out int[] counts)
        {
            var changedIndices = new List<int>();
            var changedValues = new List<int>();
            var changedCounts = new List<int>();
            int slots = Math.Min(beforeValues.Length,
                Math.Min(beforeCounts.Length, Math.Min(afterValues.Length, afterCounts.Length)));
            for (int i = 0; i < slots; i++)
            {
                if (beforeValues[i] == afterValues[i] && beforeCounts[i] == afterCounts[i])
                    continue;
                changedIndices.Add(i);
                changedValues.Add(afterValues[i]);
                changedCounts.Add(afterCounts[i]);
            }
            if (changedIndices.Count == 0 && fallbackSlot >= 0 && fallbackSlot < slots)
            {
                changedIndices.Add(fallbackSlot);
                changedValues.Add(afterValues[fallbackSlot]);
                changedCounts.Add(afterCounts[fallbackSlot]);
            }
            indices = changedIndices.ToArray();
            values = changedValues.ToArray();
            counts = changedCounts.ToArray();
        }

        // ================================================================ 客户端：丢弃 / 投掷 / 开火意图

        /// <summary>
        /// 客户端丢弃输入（由 <see cref="SuComponentInput"/> 在 base.Update 之后调用）。
        ///
        /// 关键口径：**本地绝不扣除**。这里只采集意图交给模块发 PickableDropRequest，然后把
        /// 引擎这一帧的 <c>PlayerInput.Drop</c> 清掉，让 <c>ComponentPlayer.Update</c> 的
        /// 原生掉落分支（RemoveSlotItems + AddPickable）不执行 —— 否则客户端会扣掉物品并生成
        /// 一个被"外来拾取物"清理掉的幽灵，表现为"按 Q 东西直接消失"。
        ///
        /// 发送失败也照样吞掉原生掉落：宁可这一下没丢出去（有明确日志 + 计数），也不能丢物品。
        /// Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        /// </summary>
        bool IEntityDropInputSink.TryAcceptDrop(ComponentInput input, ComponentPlayer player)
        {
            if (!m_clientReplica || input == null || player == null || m_project == null)
                return false;
            // 只处理**本机玩家**：远端化身的 Updateable 在联机角色创建时已被摘掉，这里仍显式校验。
            if (m_spawner == null ||
                !m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out _) ||
                !ReferenceEquals(local?.ComponentPlayer, player))
                return false;
            ComponentMiner miner = player.ComponentMiner;
            IInventory inventory = miner?.Inventory;
            if (inventory == null) return false;
            int slot = inventory.ActiveSlotIndex;
            if (slot < 0 || slot >= inventory.SlotsCount) return false;
            int value = inventory.GetSlotValue(slot);
            int count = inventory.GetSlotCount(slot);
            if (value == 0 || count <= 0) return false;   // 空手：原生分支本来也不会掉东西
            ComponentBody body = player.ComponentBody;
            ComponentCreatureModel model = player.ComponentCreatureModel;
            if (body == null || model == null) return false;

            // 与引擎 Drop 分支完全相同的落点与速度（方向 = 眼睛朝向）。
            Vector3 position = body.Position +
                new Vector3(0f, body.StanceBoxSize.Y * 0.66f, 0f) + 0.25f * body.Matrix.Forward;
            Vector3 direction = Matrix.CreateFromQuaternion(model.EyeRotation).Forward;
            var intent = new PickableDropIntent(slot, value, count, position, direction);

            IEntityRequestSink sink = m_requestSink;
            bool submitted;
            string reason;
            if (sink == null)
            {
                submitted = false;
                reason = "no-request-sink";
            }
            else
            {
                try
                {
                    submitted = sink.TrySubmitDrop(in intent, out reason);
                }
                catch (Exception e)
                {
                    submitted = false;
                    reason = "submit-exception-" + e.GetType().Name;
                }
            }
            bool accept = submitted;
            if (accept)
            {
                m_dropIntentSubmissions++;
                RememberDropPrediction(in intent);
            }
            else
            {
                m_dropIntentFailures++;
                LogProblemOnce("drop-submit:" + reason,
                    "客户端丢弃意图没有发出去（" + reason + "）→ 本机**不扣物品**、也不本地生成拾取物" +
                    "（物品仍在背包里；可重试）");
            }
            // 无论发没发出去都吞掉引擎原生掉落：绝不本地扣除。
            m_inputRef(input).Drop = false;
            return accept;
        }

        private void RememberDropPrediction(in PickableDropIntent intent)
        {
            double now = Time.RealTime;
            if (m_recentDropPredictions.Count >= 16)
            {
                // 有界即可：旧条目过期后会在下一次扫描里清掉。
                m_recentDropPredictions.RemoveAll(item => item.ExpiryTime <= now);
                if (m_recentDropPredictions.Count >= 16)
                    m_recentDropPredictions.RemoveAt(0);
            }
            m_recentDropPredictions.Add(new PendingDropPrediction
            {
                ItemValue = intent.ItemValue,
                Count = intent.Count,
                Position = intent.Position,
                ExpiryTime = now + 3.0
            });
        }

        /// <summary>
        /// 客户端 UI 拖出到世界（<c>ViewWidget.DragDrop</c>）的请求入口。
        ///
        /// 由 <see cref="SuDropDragHostWidget"/> 在引擎执行 <c>ViewWidget.DragDrop</c> **之前**调用：
        /// 那时物品还在背包里，所以这里和键盘 Drop 一样只发请求、不扣物品、不生成拾取物。
        /// 成功 ⇒ 调用方必须撤销这次拖拽（<c>EndDrag</c>），于是 ViewWidget 的
        /// <c>RemoveSlotItems + AddPickable</c> 整段不执行（幽灵从源头不存在，
        /// foreignPickablesRemoved 不会因合法丢弃增长）。
        /// false + reason ⇒ 调用方仍然撤销拖拽（物品留在背包），并记录明确原因。
        /// </summary>
        public bool TrySubmitUiDrop(in PickableDropIntent intent, out string reason)
        {
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            // 走到这里说明拦截器已经接管了这次拖拽：ViewWidget.DragDrop 不会执行，
            // 本地拾取物从**创建之前**就被抑制了。
            m_dropPhantomSuppressed++;
            return SubmitUiDropRequest(in intent, out reason);
        }

        /// <summary>
        /// UI 拖出请求的公共发送段（正常拦截路径与安全网撤销路径共用）：
        /// 只做来源校验 + 交给模块 + 失败计数/限流日志，不碰本地背包与实体。
        /// </summary>
        private bool SubmitUiDropRequest(in PickableDropIntent intent, out string reason)
        {
            if (intent.Source != DropSources.Ui)
            {
                reason = "not-ui-source";
                return false;
            }
            m_uiDropIntents++;
            IEntityRequestSink sink = m_requestSink;
            bool submitted;
            if (sink == null)
            {
                submitted = false;
                reason = "no-request-sink";
            }
            else
            {
                try
                {
                    submitted = sink.TrySubmitDrop(in intent, out reason);
                }
                catch (Exception e)
                {
                    submitted = false;
                    reason = "submit-exception-" + e.GetType().Name;
                }
            }
            if (submitted) return true;
            m_uiDropSubmitFailures++;
            LogRateLimited("ui-drop-submit:" + reason,
                "客户端 UI 拖出没有发出去（" + reason + "）→ 已撤销本次拖拽：" +
                "物品仍在背包里，本地没有生成拾取物（" + intent.Describe() + "）");
            return false;
        }

        /// <summary>
        /// 客户端 **AddPickable 安全网**：引擎在本地创建了一个拾取物之后调用
        /// （<c>SuSubsystemPickables.PickableAdded</c>，单漏斗覆盖所有本地创建）。
        ///
        /// 只接管同时满足以下条件的对象（其余一律不动，交给既有的外来拾取物清理 / 容器同步）：
        ///   · 本端是客户端副本；
        ///   · 落点 == 本机玩家当前相机视点（<c>ViewWidget.DragDrop</c> 的唯一特征）；
        ///   · 背包快照里**恰好一个**槽正好减少了同样物品/数量（否则明确记 unmatched，不猜）。
        /// 接管 = 先把物品放回原槽（保证不丢），再从拾取物列表移除这个幽灵，最后走同一条
        /// UI 丢弃请求链。请求发不出去时物品同样已经在背包里（拖出被撤销）。
        /// </summary>
        public bool TryHandleLocalPickableAdded(Pickable pickable)
        {
            if (!m_clientReplica || m_pickables == null || pickable == null) return false;
            if (m_project == null || m_spawner == null) return false;
            if (!m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out _) ||
                local?.ComponentPlayer == null)
                return false;
            Camera camera = local.GameWidget?.ActiveCamera;
            IInventory inventory = local.ComponentPlayer.ComponentMiner?.Inventory;
            if (camera == null || inventory == null) return false;
            if (!UiDropPhantomPolicy.IsViewPositionDrop(pickable.Position,
                    camera.ViewPosition))
                return false;   // 不是 UI 拖出（方块掉落 / 爆炸 / 拾取物分裂等），不动
            string reason;
            if (!m_localInventorySnapshotValid)
            {
                m_uiDropPhantomUnmatched++;
                LogRateLimited("ui-drop-phantom-no-snapshot",
                    "本地拾取物出现在相机视点，但没有可用的背包快照（Pump 还没跑到本机玩家）→ " +
                    "不接管本次拖出（值=" + pickable.Value + " 数量=" + pickable.Count +
                    "）；若这是 UI 拖出，物品会按外来拾取物清理");
                return false;
            }
            int[] currentValues = new int[inventory.SlotsCount];
            int[] currentCounts = new int[inventory.SlotsCount];
            for (int i = 0; i < inventory.SlotsCount; i++)
            {
                currentValues[i] = inventory.GetSlotValue(i);
                currentCounts[i] = inventory.GetSlotCount(i);
            }
            if (!UiDropPhantomPolicy.TryFindDroppedSlot(m_localInventorySnapshotValues,
                    m_localInventorySnapshotCounts, currentValues, currentCounts,
                    pickable.Value, pickable.Count, out int slot, out reason))
            {
                m_uiDropPhantomUnmatched++;
                LogRateLimited("ui-drop-phantom-unmatched:" + reason,
                    "本地拾取物出现在相机视点，但背包增量无法唯一匹配（" + reason +
                    "；值=" + pickable.Value + " 数量=" + pickable.Count +
                    "）→ 不接管（容器 / 其它 UI 路径由各自的同步链负责；" +
                    "本机玩家背包未改动）");
                return false;
            }
            int restoreValue = m_localInventorySnapshotValues[slot];
            int restoreCount = pickable.Count;
            int before = inventory.GetSlotCount(slot);
            int afterRestore = before + restoreCount;
            if (before > 0 && inventory.GetSlotValue(slot) != restoreValue)
            {
                m_uiDropPhantomUnmatched++;
                LogRateLimited("ui-drop-phantom-slot-changed",
                    "准备撤销 UI 幽灵时槽 " + slot + " 的内容已变化（当前 " +
                    inventory.GetSlotValue(slot) + " x" + before + "，需要放回 " +
                    restoreValue + " x" + restoreCount + "）→ 不接管（物品仍在背包，未被扣掉）");
                return false;
            }
            if (afterRestore > inventory.GetSlotCapacity(slot, restoreValue))
            {
                m_uiDropPhantomUnmatched++;
                LogRateLimited("ui-drop-phantom-capacity",
                    "撤销 UI 幽灵时槽 " + slot + " 放不下（" + afterRestore + " > 容量 " +
                    inventory.GetSlotCapacity(slot, restoreValue) + "）→ 不接管");
                return false;
            }
            try
            {
                inventory.AddSlotItems(slot, restoreValue, restoreCount);
            }
            catch (Exception e)
            {
                m_uiDropPhantomUnmatched++;
                LogRateLimited("ui-drop-phantom-restore:" + e.GetType().Name,
                    "撤销 UI 幽灵时把物品放回槽 " + slot + " 失败（" + e.GetType().Name +
                    " " + e.Message + "）→ 不接管；幽灵会被外来拾取物清理，" +
                    "请检查该槽状态（值=" + pickable.Value + " 数量=" + pickable.Count + "）");
                return false;
            }
            // 物品已放回背包 → 现在安全地标记本地幽灵。只置 ToRemove、**不**从列表里硬删：
            // 本次 SubsystemPickables.Update 末尾会统一移除并触发 PickableRemoved
            // （订阅了 PickableAdded 的引擎行为不会留下悬空引用），且它不会参与渲染 / 拾取。
            pickable.ToRemove = true;
            m_uiDropLocalPickableCancelled++;
            m_dropPhantomSuppressed++;
            if (!UiDropPolicy.TryBuildIntent(slot, restoreValue, restoreCount,
                    m_localInventorySnapshotCounts != null &&
                    slot < m_localInventorySnapshotCounts.Length
                        ? m_localInventorySnapshotCounts[slot]
                        : restoreCount,
                    pickable.Position, pickable.Velocity, out PickableDropIntent intent,
                    out reason))
            {
                // 物品已经放回背包：这次拖出等价于没发生（明确记录，不静默丢物品）。
                m_uiDropSubmitFailures++;
                LogRateLimited("ui-drop-phantom-intent:" + reason,
                    "撤销 UI 幽灵后重建拖出意图失败（" + reason + "）→ 本次拖出作废，" +
                    "物品已放回背包（值=" + restoreValue + " 数量=" + restoreCount + "）");
                return true;
            }
            TrySubmitUiDrop(in intent, out reason);
            return true;
        }

        /// <summary>
        /// UI 拖出安全网的背包快照：由组合根在每帧**所有模块 tick 之后**调用
        /// （此时到下一帧控件输入之间背包不会变化，正好是"拖出前"的状态）。
        /// 只对客户端副本维护；主机 / 单机清空。
        /// </summary>
        public void CaptureUiDropInventorySnapshot()
        {
            m_localInventorySnapshotValid = false;
            if (!m_clientReplica || m_project == null || m_spawner == null)
            {
                m_localInventorySnapshotValues = null;
                m_localInventorySnapshotCounts = null;
                return;
            }
            if (!m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out _) ||
                local?.ComponentPlayer == null)
                return;
            IInventory inventory = local.ComponentPlayer.ComponentMiner?.Inventory;
            if (inventory == null || inventory.SlotsCount <= 0) return;
            // 与背包同步同一口径：普通背包（26 槽）全部槽位；创造仓库仍只取前 10 槽。
            int slots = PlayerInventoryMath.SyncSlotsFor(inventory.SlotsCount);
            if (slots <= 0) return;
            // 每帧复用缓冲（客户端每帧一次；热路径不做数组分配）。
            if (m_localInventorySnapshotValues == null ||
                m_localInventorySnapshotValues.Length != slots)
            {
                m_localInventorySnapshotValues = new int[slots];
                m_localInventorySnapshotCounts = new int[slots];
            }
            for (int i = 0; i < slots; i++)
            {
                m_localInventorySnapshotValues[i] = inventory.GetSlotValue(i);
                m_localInventorySnapshotCounts[i] = inventory.GetSlotCount(i);
            }
            m_localInventorySnapshotValid = true;
        }

        /// <summary>
        /// 客户端投掷 / 开火入口（由方块行为外壳调用）。
        /// 成功 = 请求已入队发送；客户端不会本地生成投射物，也不扣道具 / 耐久。
        /// </summary>
        public bool SubmitClientProjectileFire(in ProjectileFireIntent intent, out string reason)
        {
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            IEntityRequestSink sink = m_requestSink;
            bool submitted;
            if (sink == null)
            {
                submitted = false;
                reason = "no-request-sink";
            }
            else
            {
                try
                {
                    submitted = sink.TrySubmitProjectileFire(in intent, out reason);
                }
                catch (Exception e)
                {
                    submitted = false;
                    reason = "submit-exception-" + e.GetType().Name;
                }
            }
            if (submitted)
            {
                m_fireIntentSubmissions++;
                return true;
            }
            m_fireIntentFailures++;
            LogProblemOnce("fire-submit:" + reason,
                "客户端开火意图没有发出去 " + intent.Describe() + "（" + reason +
                "）→ 本机**不生成投射物**、也不扣道具 / 耐久");
            return false;
        }

        // ================================================================ 主机：执行丢弃请求

        public bool TryDrop(int clientId, in PickableDropRequestData request,
            out PickableDropOutcome outcome, out string reason)
        {
            try
            {
                bool accepted = TryDropCore(clientId, in request, out outcome, out reason);
                if (accepted) m_dropAccepted++;
                else m_dropRejected++;
                return accepted;
            }
            catch (Exception e)
            {
                outcome = default;
                reason = "host-exception-" + e.GetType().Name;
                m_dropRejected++;
                LogRateLimited("drop-exception:" + e.GetType().Name,
                    "执行丢弃请求时引擎异常（clientId=" + clientId + " " + request.Describe() +
                    "）：" + e.GetType().Name + " " + e.Message + " → 明确拒绝（不静默成功）");
                return false;
            }
        }

        private bool TryDropCore(int clientId, in PickableDropRequestData request,
            out PickableDropOutcome outcome, out string reason)
        {
            outcome = default;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (m_project == null)
            {
                reason = "no-project";
                return false;
            }
            if (m_pickables == null)
            {
                reason = "no-pickables";
                return false;
            }
            if (!TryResolveHostActor(m_project, clientId, out ComponentPlayer player, out _,
                    out reason))
                return false;
            IInventory inventory = player.ComponentMiner?.Inventory;
            if (inventory == null)
            {
                reason = "no-inventory";
                return false;
            }
            int slot = request.ActiveSlot;
            if (slot < 0 || slot >= inventory.SlotsCount)
            {
                reason = "slot-range";
                return false;
            }
            int value = inventory.GetSlotValue(slot);
            int count = inventory.GetSlotCount(slot);
            if (value == 0 || count <= 0)
            {
                reason = "empty-slot";
                return false;
            }
            // 槽内容必须是**同一种物品**（contents 完全一致）。data 位（拉弓进度 / 击锤 /
            // 装填状态 / 家具款式）在客户端本地会随操作每 100ms 变化，而主机镜像要等一次上传；
            // 要求整值完全相等会把合法丢弃判成 slot-mismatch。执行时一律使用**主机权威值**，
            // 客户端上报的 data 只作诊断（不一致时限流记一行，不静默）。
            if (value == 0 || request.ItemValue == 0 ||
                global::Game.Terrain.ExtractContents(value) !=
                global::Game.Terrain.ExtractContents(request.ItemValue))
            {
                reason = "slot-mismatch";
                return false;
            }
            if (value != request.ItemValue)
            {
                LogRateLimited("drop-data-mismatch:" + clientId,
                    "clientId=" + clientId + " 丢弃请求的物品 data 与主机镜像不同（请求 " +
                    request.ItemValue + " / 权威 " + value + "）：按主机权威值丢弃" +
                    "（draw/击锤等本地状态位允许一个 RTT 的滞后）");
            }
            Block block = ResolveBlock(value);
            if (block == null)
            {
                reason = "item-illegal";
                return false;
            }
            ComponentBody body = player.ComponentBody;
            if (!m_reachGate.TryCheckDistance(request.HasReach, request.Reach,
                    EngineReach.IsCreative(m_project),
                    MathUtils.Sqrt(Vector3.DistanceSquared(body.Position, request.Position)),
                    out string reachReason))
            {
                reason = reachReason;
                return false;
            }
            // 来源：键盘 / 手柄（整格、视线方向）与 UI 拖出（指定数量、拖拽方向）走**同一**请求，
            // 但速度口径不同。未知来源明确拒绝（编解码层已经拦过一次，这里兜住内部调用方）。
            bool ui = request.Source == DropSources.Ui;
            if (!DropSources.IsKnown(request.Source))
            {
                reason = "drop-source-" + request.Source;
                return false;
            }
            // UI 拖出的数量是**权威移除量**：必须 1 ≤ count ≤ 主机权威槽数量。
            // 键盘 / 手柄路径沿用引擎语义：丢整格（客户端上报的 count 只作诊断）。
            int want;
            if (ui)
            {
                if (request.Count <= 0 || request.Count > count)
                {
                    reason = "count-unavailable";
                    return false;
                }
                want = request.Count;
            }
            else
            {
                want = count;
            }
            Vector3 direction;
            Vector3 velocity;
            if (ui)
            {
                // 拖拽方向 = 屏幕上拖到世界的射线方向，与化身视线无关（引擎 ViewWidget.DragDrop
                // 用同一方向；主机只校验有限 / 非零，速度上限固定 12 m/s）。
                // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
                if (!HostDropVelocity.IsUsableDirection(request.Direction))
                {
                    reason = "direction-invalid";
                    return false;
                }
                direction = Vector3.Normalize(request.Direction);
                if (!HostDropVelocity.IsUsableDirection(direction))
                {
                    reason = "direction-invalid";
                    return false;
                }
                velocity = HostDropVelocity.ForUiDrop(direction);
            }
            else
            {
                // 方向取主机权威化身的视线（与引擎 Drop 分支同形：EyeRotation 前向），
                // 客户端上报方向只用于一致性校验，过期 / 伪造请求明确拒绝。
                if (!TryResolveHostAim(player, request.Direction, out direction, out reason))
                    return false;
                velocity = HostDropVelocity.ForManualDrop(direction);
            }
            if (!HostDropVelocity.IsUsableDirection(velocity))
            {
                reason = "drop-velocity-invalid";
                return false;
            }
            // 权威落点与引擎原生 Drop 分支同形（位置用主机化身，不用客户端上报值）：
            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
            //         （position = body + (0, StanceBoxSize.Y*0.66, 0) + 0.25*Forward）
            Vector3 position = body.Position +
                new Vector3(0f, body.StanceBoxSize.Y * 0.66f, 0f) + 0.25f * body.Matrix.Forward;
            CaptureInventorySlots(inventory, out int[] beforeValues, out int[] beforeCounts);
            int removed = inventory.RemoveSlotItems(slot, want);
            if (removed <= 0)
            {
                reason = "remove-refused";
                return false;
            }
            if (removed != want)
            {
                // 校验后仍出现短移除 = 主机背包在两步之间被改（并发请求 / 引擎插件）。
                // 不伪造数量：按实际移除量生成并明确记录（绝不多扣，也不静默）。
                LogRateLimited("drop-remove-short:" + clientId,
                    "主机移除数量与请求不符（clientId=" + clientId + " slot=" + slot +
                    " 请求=" + want + " 实际=" + removed + "）→ 按实际移除量生成拾取物" +
                    (!ui ? "；键盘丢弃整格，长度差异通常来自引擎插件的槽位规则" : string.Empty));
            }
            Pickable pickable;
            try
            {
                pickable = m_pickables.AddPickable(value, removed, position, velocity, null);
            }
            catch (Exception e)
            {
                pickable = null;
                LogRateLimited("drop-add-exception:" + e.GetType().Name,
                    "主机 AddPickable 抛异常（clientId=" + clientId + " value=" + value +
                    " count=" + removed + "）：" + e.GetType().Name + " " + e.Message);
            }
            if (pickable == null)
            {
                // 生成失败 → 把物品放回权威背包（绝不静默吞物品）。
                int leftover = ComponentInventoryBase.AcquireItems(inventory, value, removed);
                if (leftover > 0)
                {
                    LogRateLimited("drop-rollback-incomplete:" + clientId,
                        "丢弃生成失败后回滚背包不完整（clientId=" + clientId + " value=" + value +
                        " 未放回 " + leftover + "/" + removed + " 个）→ 物品可能丢失，请人工检查背包");
                }
                reason = "spawn-failed";
                return false;
            }
            CaptureInventorySlots(inventory, out int[] afterValues, out int[] afterCounts);
            BuildInventoryDelta(beforeValues, beforeCounts, afterValues, afterCounts, slot,
                out int[] indices, out int[] values, out int[] counts);
            int pickableId = EnsurePickableId(pickable);
            // 掉落者免疫窗口（1.5s）：主机对**该玩家**的这一个拾取物回 pickup-grace；
            // 其他玩家不受影响。客户端侧同时按回执里的 id 抑制自己的拾取扫描（模块里同一窗口）。
            m_dropGrace.Register(pickableId, clientId, Time.RealTime);
            m_dropSpawnVelocity++;
            outcome = new PickableDropOutcome(value, removed, pickable.Position, pickable.Velocity,
                indices, values, counts, CurrentInventoryVersion(), pickableId);
            return true;
        }

        // ================================================================ 主机：客户端编辑造成的方块销毁 → 掉落

        /// <summary>
        /// 改格之前：按引擎 <c>SubsystemTerrain.DestroyCell</c> 的同一顺序算掉落
        /// （<c>block.GetDropValues</c> → 每条掉落的 <c>OnItemHarvested</c>）。
        ///
        /// 为什么必须在改格之前：<c>SubsystemEditableItemBehavior.OnItemHarvested</c> 读该格的
        /// 方块私有数据（标牌 / 家具设计 id），而 <c>ChangeCell</c> 的 <c>OnBlockRemoved</c> 会把它删掉。
        /// 同时 <c>OnItemHarvested</c> 可以改写要写入格子的值（水草被挖 → 留下水），这里把它带回去。
        ///
        /// Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.DestroyCell
        /// Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Dig
        ///         （toolLevel = 手持方块的 Block.ToolLevel）
        /// </summary>
        public bool TryPrepareBlockDrops(int clientId, int x, int y, int z, int oldValue,
            int newValue, int toolLevel, out HostBlockDropPlan plan, out string reason)
        {
            plan = null;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (m_pickables == null)
            {
                reason = "no-pickables";
                return false;
            }
            if (m_terrainSubsystem == null || m_terrainSubsystem.Terrain == null)
            {
                reason = "no-terrain";
                return false;
            }
            if (m_blockBehaviors == null)
            {
                reason = "no-block-behaviors";
                return false;
            }
            Block block = ResolveBlock(oldValue);
            if (block == null)
            {
                reason = "no-block";
                return false;
            }
            var result = new HostBlockDropPlan
            {
                X = x,
                Y = y,
                Z = z,
                OldValue = oldValue,
                NewValue = newValue,
                ClientId = clientId
            };
            m_dropScratch.Clear();
            try
            {
                block.GetDropValues(m_terrainSubsystem, oldValue, newValue, toolLevel,
                    m_dropScratch, out bool showDebris);
                // 碎屑粒子是主机本机表现（客户端的碎屑由它自己的本地引擎播放），这里不使用。
                _ = showDebris;
            }
            catch (Exception e)
            {
                m_dropPrepareFailures++;
                reason = "get-drop-values-" + e.GetType().Name;
                LogRateLimited("block-drop-getvalues:" + e.GetType().Name,
                    "主机计算方块掉落失败（cell=" + x + "," + y + "," + z + " value=" + oldValue +
                    " toolLevel=" + toolLevel + "）：" + e.GetType().Name + " " + e.Message);
                return false;
            }
            int writtenValue = newValue;
            for (int i = 0; i < m_dropScratch.Count; i++)
            {
                BlockDropValue drop = m_dropScratch[i];
                if (drop.Count <= 0) continue;
                int dropContents = global::Game.Terrain.ExtractContents(drop.Value);
                SubsystemBlockBehavior[] behaviors =
                    m_blockBehaviors.GetBlockBehaviors(dropContents);
                for (int j = 0; j < behaviors.Length; j++)
                {
                    try
                    {
                        behaviors[j].OnItemHarvested(x, y, z, oldValue, ref drop, ref writtenValue);
                    }
                    catch (Exception e)
                    {
                        m_dropPrepareFailures++;
                        reason = "on-item-harvested-" + e.GetType().Name;
                        LogRateLimited("block-drop-harvested:" + e.GetType().Name,
                            "主机处理方块收获回调失败（cell=" + x + "," + y + "," + z +
                            " value=" + oldValue + "）：" + e.GetType().Name + " " + e.Message);
                        return false;
                    }
                }
                if (drop.Count <= 0) continue;
                if (global::Game.Terrain.ExtractContents(drop.Value) == 0) continue;
                result.Drops.Add(drop);
            }
            // OnItemHarvested 可能改写 newValue（水草 → 水）；引擎 DestroyCell 也把它写回格子。
            result.NewValue = writtenValue;
            // 记录进本次落地前的拾取物条数，改格后据此判断引擎自己是否已经在该格生成过掉落。
            result.PickableCountBefore = m_pickables.Pickables.Count;
            plan = result;
            return true;
        }

        /// <summary>
        /// 改格之后：按引擎 <c>DestroyCell</c> 的口径生成拾取物（格心 + 引擎默认速度）。
        /// 若引擎自己的落地路径已经在**同一格**生成过拾取物 → 不重复生成（engineSpawned = true）。
        /// Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.DestroyCell
        /// Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.AddPickable
        /// </summary>
        public bool TrySpawnPreparedDrops(HostBlockDropPlan plan, out int spawned,
            out bool engineSpawned, out string reason)
        {
            spawned = 0;
            engineSpawned = false;
            reason = null;
            if (plan == null)
            {
                reason = "no-plan";
                return false;
            }
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (m_pickables == null)
            {
                reason = "no-pickables";
                return false;
            }
            ReadOnlyList<Pickable> list = m_pickables.Pickables;
            var position = new Vector3(plan.X, plan.Y, plan.Z) + new Vector3(0.5f);
            if (plan.PickableCountBefore >= 0 && list.Count > plan.PickableCountBefore)
            {
                // 只认"同一格附近"的新增：方块行为可能在别的格 DestroyCell（活塞 / 磁铁），
                // 那些掉落不是本次编辑补生成的职责。
                for (int i = plan.PickableCountBefore; i < list.Count; i++)
                {
                    Pickable created = list[i];
                    if (created == null) continue;
                    if (Vector3.DistanceSquared(created.Position, position) > 0.5625f) continue;
                    engineSpawned = true;
                    break;
                }
            }
            if (engineSpawned)
            {
                reason = "engine-spawned";
                return false;
            }
            if (plan.Drops.Count == 0)
            {
                reason = "no-drop-values";
                return false;
            }
            int failures = 0;
            for (int i = 0; i < plan.Drops.Count; i++)
            {
                BlockDropValue drop = plan.Drops[i];
                try
                {
                    // 与 DestroyCell 完全同形：位置 = 格心，velocity = null（引擎随机轻微上抛），
                    // stuckMatrix = null。
                    Pickable pickable = m_pickables.AddPickable(drop.Value, drop.Count,
                        position, null, null);
                    if (pickable == null)
                    {
                        failures++;
                        continue;
                    }
                    spawned++;
                    m_blockDropsSpawned++;
                    m_dropSpawnVelocity++;
                    int pickableId = EnsurePickableId(pickable);
                    // 生成者是发起编辑的客户端：它 1.5s 内不能把这个掉落捡回去。
                    m_dropGrace.Register(pickableId, plan.ClientId, Time.RealTime);
                }
                catch (Exception e)
                {
                    failures++;
                    m_dropSpawnFailures++;
                    LogRateLimited("block-drop-add:" + e.GetType().Name,
                        "主机为方块销毁生成拾取物失败（cell=" + plan.X + "," + plan.Y + "," +
                        plan.Z + " value=" + drop.Value + " count=" + drop.Count + "）：" +
                        e.GetType().Name + " " + e.Message);
                }
            }
            if (spawned <= 0)
            {
                reason = failures > 0 ? "spawn-failed" : "no-drop-values";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 客户端：这个刚到的拾取物副本是不是本机刚造成的掉落（本地引擎预测 → 主机权威副本）？
        /// true ⇒ 模块给它登记拾取宽限；同时消费该预测（一条预测只匹配一个副本）。
        /// </summary>
        public bool TryClaimOwnDropGrace(in PickableStateData state)
        {
            if (!m_clientReplica) return false;
            if (state.Action == EntityActions.Remove) return false;
            return m_ownDropPredictions.TryClaim(
                global::Game.Terrain.ExtractContents(state.Value), state.Position, Time.RealTime);
        }

        // ================================================================ 主机：执行投掷 / 开火请求

        public bool TryFireProjectile(int clientId, in ProjectileFireRequestData request,
            out ProjectileFireOutcome outcome, out string reason)
        {
            try
            {
                bool accepted = TryFireProjectileCore(clientId, in request, out outcome,
                    out reason);
                if (accepted) m_fireAccepted++;
                else m_fireRejected++;
                return accepted;
            }
            catch (Exception e)
            {
                outcome = default;
                reason = "host-exception-" + e.GetType().Name;
                m_fireRejected++;
                LogRateLimited("fire-exception:" + e.GetType().Name,
                    "执行开火请求时引擎异常（clientId=" + clientId + " " + request.Describe() +
                    "）：" + e.GetType().Name + " " + e.Message + " → 明确拒绝（不静默成功）");
                return false;
            }
        }

        private bool TryFireProjectileCore(int clientId, in ProjectileFireRequestData request,
            out ProjectileFireOutcome outcome, out string reason)
        {
            outcome = default;
            reason = null;
            if (m_clientReplica)
            {
                reason = "not-host";
                return false;
            }
            if (m_project == null)
            {
                reason = "no-project";
                return false;
            }
            if (m_projectiles == null)
            {
                reason = "no-projectiles";
                return false;
            }
            if (!TryResolveHostActor(m_project, clientId, out ComponentPlayer player, out _,
                    out reason))
                return false;
            IInventory inventory = player.ComponentMiner?.Inventory;
            if (inventory == null)
            {
                reason = "no-inventory";
                return false;
            }
            int slot = request.ActiveSlot;
            if (slot < 0 || slot >= inventory.SlotsCount)
            {
                reason = "slot-range";
                return false;
            }
            // 引擎只在活跃槽上消费 / 扣耐久，所以请求槽必须与主机权威活跃槽一致。
            if (inventory.ActiveSlotIndex != slot)
            {
                reason = "active-slot-changed";
                return false;
            }
            if (inventory.GetSlotCount(slot) <= 0)
            {
                reason = "empty-slot";
                return false;
            }
            int itemValue = inventory.GetSlotValue(slot);
            // 同 TryDropCore：只要求同一种物品（contents 一致），data 位允许本地状态滞后；
            // 执行用的 item value 一律取主机权威值（客户端上报值只作诊断）。
            if (itemValue == 0 || request.ItemValue == 0 ||
                global::Game.Terrain.ExtractContents(itemValue) !=
                global::Game.Terrain.ExtractContents(request.ItemValue))
            {
                reason = "slot-mismatch";
                return false;
            }
            if (itemValue != request.ItemValue)
            {
                LogRateLimited("fire-data-mismatch:" + clientId,
                    "clientId=" + clientId + " 开火请求的物品 data 与主机镜像不同（请求 " +
                    request.ItemValue + " / 权威 " + itemValue + "）：按主机权威值执行");
            }
            ComponentBody body = player.ComponentBody;
            ComponentCreatureModel model = player.ComponentCreatureModel;
            if (model == null)
            {
                reason = "no-model";
                return false;
            }
            if (!m_reachGate.TryCheckDistance(request.HasReach, request.Reach,
                    EngineReach.IsCreative(m_project),
                    MathUtils.Sqrt(Vector3.DistanceSquared(model.EyePosition, request.Origin)),
                    out string reachReason))
            {
                reason = reachReason;
                return false;
            }
            // 方向一律取**主机权威化身**的视线朝向（EyeRotation = 机身旋转 + 同步的视线角）。
            // 客户端上报方向只做一致性校验：差得太远 = 过期 / 伪造请求，明确拒绝。
            Vector3 hostOrigin = model.EyePosition;
            if (!TryResolveHostAim(player, request.Direction, out Vector3 direction,
                    out reason))
                return false;
            Block block = ResolveBlock(itemValue);
            if (block == null)
            {
                reason = "item-illegal";
                return false;
            }
            CaptureInventorySlots(inventory, out int[] beforeValues, out int[] beforeCounts);
            int spawned;
            bool misfire = false;
            Vector3 firstPosition;
            Vector3 firstVelocity;
            switch (request.Kind)
            {
                case ProjectileFireKinds.Throwable:
                    if (!TryFireThrowable(player, inventory, slot, itemValue, block,
                            direction, out spawned, out firstPosition, out firstVelocity,
                            out reason))
                        return false;
                    break;
                case ProjectileFireKinds.Bow:
                    if (!TryFireBow(player, inventory, slot, itemValue, block, direction,
                            out spawned, out firstPosition, out firstVelocity, out reason))
                        return false;
                    break;
                case ProjectileFireKinds.Crossbow:
                    if (!TryFireCrossbow(player, inventory, slot, itemValue, block,
                            direction, out spawned, out firstPosition, out firstVelocity,
                            out reason))
                        return false;
                    break;
                case ProjectileFireKinds.Musket:
                    if (!TryFireMusket(player, inventory, slot, itemValue, block,
                            direction, out spawned, out firstPosition, out firstVelocity,
                            out misfire, out reason))
                        return false;
                    break;
                default:
                    reason = "kind-unknown";
                    return false;
            }
            CaptureInventorySlots(inventory, out int[] afterValues, out int[] afterCounts);
            BuildInventoryDelta(beforeValues, beforeCounts, afterValues, afterCounts, slot,
                out int[] indices, out int[] values, out int[] counts);
            outcome = new ProjectileFireOutcome(request.Kind, spawned, itemValue,
                firstPosition, firstVelocity, indices, values, counts, CurrentInventoryVersion(),
                hostOrigin, direction, misfire);
            return true;
        }

        /// <summary>
        /// 投掷：与 <c>SubsystemThrowableBlockBehavior.OnAim(Completed)</c> 同形。
        /// 先 FireProjectile（null = 被地形挡住 → 明确拒绝、不扣道具），成功后扣 1 个。
        /// </summary>
        private bool TryFireThrowable(ComponentPlayer player, IInventory inventory, int slot,
            int itemValue, Block block, Vector3 direction, out int spawned,
            out Vector3 firstPosition, out Vector3 firstVelocity, out string reason)
        {
            spawned = 0;
            firstPosition = Vector3.Zero;
            firstVelocity = Vector3.Zero;
            reason = null;
            // 爆炸物必须走 ExplosionRequest（否则同一个投掷物会既生成普通投射物又引爆）。
            if (block is BombBlock || block is IncendiaryBombBlock || block is FireworksBlock)
            {
                reason = "use-explosion-request";
                return false;
            }
            if (!block.IsAimable || block.ProjectileSpeed <= 0f)
            {
                reason = "item-not-throwable";
                return false;
            }
            ComponentBody body = player.ComponentBody;
            ComponentCreatureModel model = player.ComponentCreatureModel;
            Vector3 origin = model.EyePosition + body.Matrix.Right * 0.4f;
            float speed = block.ProjectileSpeed;
            if (player.ComponentLevel != null)
                speed *= 0.5f * (player.ComponentLevel.StrengthFactor - 1f) + 1f;
            Vector3 velocity = body.Velocity + direction * speed;
            Projectile projectile = FireHostProjectile(itemValue, origin, velocity,
                m_requestRandom.Vector3(5f, 10f), player);
            if (projectile == null)
            {
                reason = "projectile-blocked";
                return false;
            }
            int removed = inventory.RemoveSlotItems(slot, 1);
            if (removed <= 0)
            {
                // 理论上不可能（前面校验过 count>0）：把刚生成的投射物撤掉，绝不白送。
                projectile.ToRemove = true;
                reason = "consume-refused";
                return false;
            }
            spawned = 1;
            firstPosition = projectile.Position;
            firstVelocity = projectile.Velocity;
            return true;
        }

        /// <summary>
        /// 弓：与 <c>SubsystemBowBlockBehavior.OnAim(Completed)</c> 同形。
        /// 有箭才生成箭投射物；**无论是否射出**都重置 draw=0 并扣耐久
        /// （射出的箭类型同时从弓的 data 里清掉，与引擎一致）。
        /// </summary>
        private bool TryFireBow(ComponentPlayer player, IInventory inventory, int slot,
            int itemValue, Block block, Vector3 direction, out int spawned,
            out Vector3 firstPosition, out Vector3 firstVelocity, out string reason)
        {
            spawned = 0;
            firstPosition = Vector3.Zero;
            firstVelocity = Vector3.Zero;
            reason = null;
            if (!(block is BowBlock))
            {
                reason = "item-not-bow";
                return false;
            }
            int data = global::Game.Terrain.ExtractData(itemValue);
            int draw = MathUtils.Clamp(BowBlock.GetDraw(data), 0, 15);
            ArrowBlock.ArrowType? arrowType = BowBlock.GetArrowType(data);
            Projectile spawnedProjectile = null;
            if (arrowType.HasValue)
            {
                ComponentBody body = player.ComponentBody;
                ComponentCreatureModel model = player.ComponentCreatureModel;
                Vector3 origin = model.EyePosition + body.Matrix.Right * 0.3f -
                    body.Matrix.Up * 0.2f;
                Vector3 spread = Vector3.Zero;
                if (arrowType == ArrowBlock.ArrowType.WoodenArrow)
                    spread = new Vector3(0.025f, 0.025f, 0.025f);
                else if (arrowType == ArrowBlock.ArrowType.StoneArrow)
                    spread = new Vector3(0.01f, 0.01f, 0.01f);
                float speed = MathUtils.Lerp(0f, 28f, MathUtils.Pow((float)draw / 15f, 0.75f));
                if (player.ComponentLevel != null)
                    speed *= 0.5f * (player.ComponentLevel.StrengthFactor - 1f) + 1f;
                Vector3 right = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
                Vector3 up = Vector3.Normalize(Vector3.Cross(direction, right));
                Vector3 noise = m_requestRandom.Float(-spread.X, spread.X) * right +
                    m_requestRandom.Float(-spread.Y, spread.Y) * up +
                    m_requestRandom.Float(-spread.Z, spread.Z) * direction;
                Vector3 velocity = body.Velocity + (direction + noise) * speed;
                int arrowValue = global::Game.Terrain.MakeBlockValue(ArrowBlock.Index, 0,
                    ArrowBlock.SetArrowType(0, arrowType.Value));
                spawnedProjectile = FireHostProjectile(arrowValue, origin, velocity,
                    Vector3.Zero, player);
                if (spawnedProjectile != null)
                {
                    spawned = 1;
                    firstPosition = spawnedProjectile.Position;
                    firstVelocity = spawnedProjectile.Velocity;
                    data = BowBlock.SetArrowType(data, null);
                }
            }
            int damage = draw >= 15 ? 2 : (draw >= 4 ? 1 : 0);
            int resetValue = global::Game.Terrain.MakeBlockValue(BowBlock.Index, 0, BowBlock.SetDraw(data, 0));
            if (!TryReplaceHeldStack(inventory, slot, resetValue, out reason))
            {
                if (spawnedProjectile != null) spawnedProjectile.ToRemove = true;
                spawned = 0;
                return false;
            }
            player.ComponentMiner?.DamageActiveTool(damage);
            return true;
        }

        /// <summary>
        /// 弩：与 <c>SubsystemCrossbowBlockBehavior.OnAim(Completed)</c> 同形。
        /// 只有 draw==15 且装了弩箭才生成投射物；无论是否射出都重置 draw 并扣 1 点耐久（draw&gt;0）。
        /// </summary>
        private bool TryFireCrossbow(ComponentPlayer player, IInventory inventory, int slot,
            int itemValue, Block block, Vector3 direction, out int spawned,
            out Vector3 firstPosition, out Vector3 firstVelocity, out string reason)
        {
            spawned = 0;
            firstPosition = Vector3.Zero;
            firstVelocity = Vector3.Zero;
            reason = null;
            if (!(block is CrossbowBlock))
            {
                reason = "item-not-crossbow";
                return false;
            }
            int data = global::Game.Terrain.ExtractData(itemValue);
            int draw = CrossbowBlock.GetDraw(data);
            ArrowBlock.ArrowType? arrowType = CrossbowBlock.GetArrowType(data);
            Projectile spawnedProjectile = null;
            if (draw == 15 && arrowType.HasValue)
            {
                ComponentBody body = player.ComponentBody;
                ComponentCreatureModel model = player.ComponentCreatureModel;
                Vector3 origin = model.EyePosition + body.Matrix.Right * 0.3f -
                    body.Matrix.Up * 0.2f;
                Vector3 velocity = body.Velocity + 38f * direction;
                int boltValue = global::Game.Terrain.MakeBlockValue(ArrowBlock.Index, 0,
                    ArrowBlock.SetArrowType(0, arrowType.Value));
                spawnedProjectile = FireHostProjectile(boltValue, origin, velocity,
                    Vector3.Zero, player);
                if (spawnedProjectile != null)
                {
                    spawned = 1;
                    firstPosition = spawnedProjectile.Position;
                    firstVelocity = spawnedProjectile.Velocity;
                    data = CrossbowBlock.SetArrowType(data, null);
                }
            }
            int resetValue = global::Game.Terrain.MakeBlockValue(CrossbowBlock.Index, 0,
                CrossbowBlock.SetDraw(data, 0));
            if (!TryReplaceHeldStack(inventory, slot, resetValue, out reason))
            {
                if (spawnedProjectile != null) spawnedProjectile.ToRemove = true;
                spawned = 0;
                return false;
            }
            if (draw > 0) player.ComponentMiner?.DamageActiveTool(1);
            return true;
        }

        /// <summary>
        /// 火枪：与 <c>SubsystemMusketBlockBehavior.OnAim(Completed)</c> 同形。
        /// 空包弹 / 装填不足时引擎也会消耗装填并扣耐久，只是不生成投射物。
        /// </summary>
        private bool TryFireMusket(ComponentPlayer player, IInventory inventory, int slot,
            int itemValue, Block block, Vector3 direction, out int spawned,
            out Vector3 firstPosition, out Vector3 firstVelocity, out bool misfire,
            out string reason)
        {
            spawned = 0;
            misfire = false;
            firstPosition = Vector3.Zero;
            firstVelocity = Vector3.Zero;
            reason = null;
            if (!(block is MusketBlock))
            {
                reason = "item-not-musket";
                return false;
            }
            int data = global::Game.Terrain.ExtractData(itemValue);
            if (!MusketBlock.GetHammerState(data))
            {
                reason = "hammer-not-cocked";
                return false;
            }
            MusketBlock.LoadState loadState = MusketBlock.GetLoadState(data);
            BulletBlock.BulletType? bulletType = MusketBlock.GetBulletType(data);
            bool fired = false;
            int bulletValue = 0;
            int bulletCount = 0;
            float bulletSpeed = 0f;
            Vector3 bulletSpread = Vector3.Zero;
            switch (loadState)
            {
                case MusketBlock.LoadState.Empty:
                    // 没装火药：引擎只显示提示并放下击锤，不生成投射物、不扣耐久。
                    break;
                case MusketBlock.LoadState.Gunpowder:
                case MusketBlock.LoadState.Wad:
                    // 空包弹：有枪声/烟，没有投射物，但仍消耗装填 + 扣 1 点耐久。
                    fired = true;
                    break;
                case MusketBlock.LoadState.Loaded:
                    fired = true;
                    if (bulletType == BulletBlock.BulletType.Buckshot)
                    {
                        bulletValue = global::Game.Terrain.MakeBlockValue(BulletBlock.Index, 0,
                            BulletBlock.SetBulletType(0, BulletBlock.BulletType.BuckshotBall));
                        bulletCount = 8;
                        bulletSpread = new Vector3(0.04f, 0.04f, 0.25f);
                        bulletSpeed = 80f;
                    }
                    else if (bulletType == BulletBlock.BulletType.BuckshotBall)
                    {
                        bulletValue = global::Game.Terrain.MakeBlockValue(BulletBlock.Index, 0,
                            BulletBlock.SetBulletType(0, BulletBlock.BulletType.BuckshotBall));
                        bulletCount = 1;
                        bulletSpread = new Vector3(0.06f, 0.06f, 0f);
                        bulletSpeed = 60f;
                    }
                    else if (bulletType.HasValue)
                    {
                        bulletValue = global::Game.Terrain.MakeBlockValue(BulletBlock.Index, 0,
                            BulletBlock.SetBulletType(0, bulletType.Value));
                        bulletCount = 1;
                        bulletSpeed = 120f;
                    }
                    break;
            }
            var spawnedProjectiles = new List<Projectile>();
            if (fired)
            {
                ComponentBody body = player.ComponentBody;
                ComponentCreatureModel model = player.ComponentCreatureModel;
                Vector3 origin = model.EyePosition + body.Matrix.Right * 0.3f -
                    body.Matrix.Up * 0.2f;
                // 枪管进水：引擎的哑火分支只有声音/烟，不生成投射物（装填照样消耗）。
                misfire = body.ImmersionFactor > 0.4f;
                if (!misfire && bulletCount > 0)
                {
                    Vector3 right = Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
                    Vector3 up = Vector3.Normalize(Vector3.Cross(direction, right));
                    for (int i = 0; i < bulletCount; i++)
                    {
                        Vector3 noise = m_requestRandom.Float(-bulletSpread.X, bulletSpread.X) *
                            right + m_requestRandom.Float(-bulletSpread.Y, bulletSpread.Y) * up +
                            m_requestRandom.Float(-bulletSpread.Z, bulletSpread.Z) * direction;
                        Vector3 velocity = body.Velocity + bulletSpeed * (direction + noise);
                        Projectile projectile = FireHostProjectile(bulletValue, origin,
                            velocity, Vector3.Zero, player);
                        if (projectile == null) continue;
                        projectile.ProjectileStoppedAction = ProjectileStoppedAction.Disappear;
                        spawnedProjectiles.Add(projectile);
                        if (spawned == 0)
                        {
                            firstPosition = projectile.Position;
                            firstVelocity = projectile.Velocity;
                        }
                        spawned++;
                    }
                }
                data = MusketBlock.SetLoadState(data, MusketBlock.LoadState.Empty);
            }
            data = MusketBlock.SetHammerState(data, false);
            int resetValue = global::Game.Terrain.MakeBlockValue(MusketBlock.Index, 0, data);
            if (!TryReplaceHeldStack(inventory, slot, resetValue, out reason))
            {
                for (int i = 0; i < spawnedProjectiles.Count; i++)
                    spawnedProjectiles[i].ToRemove = true;
                spawned = 0;
                return false;
            }
            if (fired) player.ComponentMiner?.DamageActiveTool(1);
            return true;
        }

        // ================================================================ 主机：执行辅助

        /// <summary>
        /// 主机的权威发射辅助：每次 FireProjectile 都记一次 **host 出生点** 尝试；
        /// 返回 null = 引擎自己的出生射线被地形 / 自身包围盒挡住（fireSpawnCollided）。
        /// 所有主机开火路径都必须走这里，客户端上报的 origin 不参与生成。
        /// </summary>
        private Projectile FireHostProjectile(int value, Vector3 origin, Vector3 velocity,
            Vector3 angularVelocity, ComponentCreature owner)
        {
            m_fireSpawnAtHost++;
            Projectile projectile = m_projectiles.FireProjectile(value, origin, velocity,
                angularVelocity, owner);
            if (projectile == null) m_fireSpawnCollided++;
            return projectile;
        }

        /// <summary>
        /// 主机权威视线：<c>ComponentCreatureModel.EyeRotation</c>（机身旋转 * 同步的视线角）前向。
        /// 客户端上报方向只做一致性校验（容忍一个快照插值的滞后），差太远明确拒绝。
        /// </summary>
        private static bool TryResolveHostAim(ComponentPlayer player, Vector3 claimedDirection,
            out Vector3 direction, out string reason)
        {
            direction = Vector3.Zero;
            reason = null;
            ComponentCreatureModel model = player?.ComponentCreatureModel;
            if (model == null)
            {
                reason = "no-model";
                return false;
            }
            if (player.ComponentLocomotion == null)
            {
                reason = "no-locomotion";
                return false;
            }
            Vector3 host = Matrix.CreateFromQuaternion(model.EyeRotation).Forward;
            if (!IsFinite(host) || host.LengthSquared() < 0.25f)
            {
                reason = "host-direction-invalid";
                return false;
            }
            host = Vector3.Normalize(host);
            if (!IsFinite(claimedDirection) || claimedDirection.LengthSquared() < 1e-6f)
            {
                reason = "direction-invalid";
                return false;
            }
            Vector3 claimed = Vector3.Normalize(claimedDirection);
            // 75°：足够拦住伪造 / 严重过期请求，又不会因为主机化身的插值滞后误杀合法射击。
            // 真正用于生成的方向始终是 host（客户端方向只做一致性校验）。
            if (Vector3.Dot(claimed, host) < 0.25f)
            {
                reason = "direction-mismatch";
                return false;
            }
            direction = host;
            return true;
        }

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
            !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
            !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);

        private bool TryResolveHostActor(Project project, int clientId, out ComponentPlayer player,
            out PlayerData playerData, out string reason)
        {
            player = null;
            playerData = null;
            reason = null;
            if (m_spawner == null)
            {
                reason = "no-spawner";
                return false;
            }
            if (clientId <= 0)
            {
                reason = "not-player-peer";
                return false;
            }
            if (!m_spawner.TryGet(clientId, out playerData) || playerData == null)
            {
                reason = "no-avatar";
                return false;
            }
            player = playerData.ComponentPlayer;
            if (player == null)
            {
                reason = "no-avatar";
                return false;
            }
            if (player.ComponentBody == null)
            {
                reason = "no-body";
                return false;
            }
            if (player.ComponentHealth == null)
            {
                reason = "no-health";
                return false;
            }
            if (player.ComponentHealth.Health <= 0f)
            {
                reason = "dead";
                return false;
            }
            return true;
        }

        private static Block ResolveBlock(int value)
        {
            int contents = global::Game.Terrain.ExtractContents(value);
            if (contents <= 0 || BlocksManager.Blocks == null ||
                contents >= BlocksManager.Blocks.Length)
                return null;
            return BlocksManager.Blocks[contents];
        }

        private static void CaptureInventorySlots(IInventory inventory, out int[] values,
            out int[] counts)
        {
            int slots = inventory.SlotsCount;
            values = new int[slots];
            counts = new int[slots];
            for (int i = 0; i < slots; i++)
            {
                values[i] = inventory.GetSlotValue(i);
                counts[i] = inventory.GetSlotCount(i);
            }
        }

        /// <summary>
        /// 把活跃槽里的 1 件物品换成新值（弓 / 弩 / 火枪的重置与耐久损耗都走这条）。
        /// 返回 false + reason = 明确的引擎拒绝（调用方必须回滚它刚生成的投射物）。
        /// </summary>
        private bool TryReplaceHeldStack(IInventory inventory, int slot, int newValue,
            out string reason)
        {
            reason = null;
            int beforeValue = inventory.GetSlotValue(slot);
            int beforeCount = inventory.GetSlotCount(slot);
            if (beforeCount <= 0 || beforeValue == 0)
            {
                reason = "consume-refused";
                return false;
            }
            int removed = inventory.RemoveSlotItems(slot, 1);
            if (removed <= 0)
            {
                reason = "consume-refused";
                return false;
            }
            // 与引擎 Completed 分支完全同形：Remove 1 → Add 新值 1。
            // **不能要求结果计数 == 1**：创造模式 ComponentCreativeInventory 的
            // GetSlotCount 恒为 9999，AddSlotItems 只替换槽位值、不改变计数；
            // 旧实现把这种正常结果误判成 replace-refused，创造模式下的弓 / 弩 / 火枪
            // 全部被拒绝（15/22 的现场证据）。
            try
            {
                inventory.AddSlotItems(slot, newValue, removed);
            }
            catch (Exception e)
            {
                // 非可堆叠工具不会走到这里；真走到说明这是不支持的堆叠形状。
                // 把刚移除的 1 件放回原值，明确拒绝（绝不静默吞物品）。
                try { inventory.AddSlotItems(slot, beforeValue, removed); }
                catch (Exception rollback)
                {
                    LogRateLimited("replace-rollback:" + slot + ":" + rollback.GetType().Name,
                        "槽位 " + slot + " 替换失败后回滚也失败（" + rollback.GetType().Name +
                        " " + rollback.Message + "）→ 该槽可能少 1 件，请人工检查背包");
                }
                reason = "replace-refused-stack";
                LogRateLimited("replace-stack:" + slot + ":" + e.GetType().Name,
                    "槽位 " + slot + " 仍是旧值堆叠（value=" + beforeValue + " count=" +
                    beforeCount + "），无法直接换成新值（" + e.GetType().Name + " " +
                    e.Message + "）→ 明确拒绝并回滚");
                return false;
            }
            int afterValue = inventory.GetSlotValue(slot);
            int afterCount = inventory.GetSlotCount(slot);
            if (afterValue != newValue || afterCount <= 0)
            {
                reason = "replace-refused";
                LogRateLimited("replace-refused:" + slot,
                    "槽位 " + slot + " 替换后校验失败（期望 value=" + newValue +
                    " count>0，实际 value=" + afterValue + " count=" + afterCount +
                    "，背包类型 " + inventory.GetType().Name + "）→ 明确拒绝");
                return false;
            }
            return true;
        }

        // ================================================================ 客户端：应用副本

        public bool TryApplyPickable(in PickableStateData state, out string reason)
        {
            reason = null;
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            if (m_pickables == null)
            {
                reason = "no-pickables-subsystem";
                return false;
            }
            if (state.Action == EntityActions.Remove)
            {
                if (m_clientPickables.TryGetValue(state.Id, out ClientPickableReplica removed))
                {
                    RemovePickableReplica(removed);
                    m_clientPickables.Remove(state.Id);
                    m_replicasRemoved++;
                }
                return true;
            }
            if (m_clientPickables.TryGetValue(state.Id, out ClientPickableReplica replica) &&
                replica.Pickable != null && !replica.Pickable.ToRemove)
            {
                ApplyPickableTarget(replica, state);
                m_replicasUpdated++;
                return true;
            }
            Matrix? stuckMatrix = state.HasStuckMatrix
                ? CreateStuckMatrix(state.StuckPosition, state.StuckRotation)
                : (Matrix?)null;
            Pickable pickable;
            try
            {
                pickable = m_pickables.AddPickable(state.Value, state.Count, state.Position,
                    state.Velocity, stuckMatrix);
            }
            catch (Exception e)
            {
                reason = "add-threw-" + e.GetType().Name;
                m_applyFailures++;
                return false;
            }
            if (pickable == null)
            {
                reason = "add-returned-null";
                m_applyFailures++;
                return false;
            }
            replica = new ClientPickableReplica
            {
                Pickable = pickable,
                LastUpdateTime = Time.RealTime
            };
            ApplyPickableTarget(replica, state);
            m_clientPickables[state.Id] = replica;
            m_replicasCreated++;
            return true;
        }

        private static Matrix CreateStuckMatrix(Vector3 position, Quaternion rotation)
        {
            Matrix matrix = Matrix.CreateFromQuaternion(rotation);
            matrix.Translation = position;
            return matrix;
        }

        private static void ApplyPickableTarget(ClientPickableReplica replica,
            in PickableStateData state)
        {
            replica.PreviousTargetPosition = replica.TargetPosition;
            replica.HasPreviousTargetPosition = replica.Initialized;
            replica.TargetPosition = state.Position;
            replica.TargetVelocity = state.Velocity;
            replica.TargetHasFlyTo = state.HasFlyTo;
            replica.TargetFlyTo = state.FlyTo;
            replica.LastUpdateTime = Time.RealTime;
            Pickable pickable = replica.Pickable;
            if (pickable == null) return;
            pickable.Value = state.Value;
            pickable.Count = state.Count;
            pickable.StuckMatrix = state.HasStuckMatrix
                ? CreateStuckMatrix(state.StuckPosition, state.StuckRotation)
                : (Matrix?)null;
        }

        public bool TryApplyProjectile(in ProjectileStateData state, out string reason)
        {
            reason = null;
            if (!m_clientReplica)
            {
                reason = "not-client-replica";
                return false;
            }
            if (m_projectiles == null)
            {
                reason = "no-projectiles-subsystem";
                return false;
            }
            if (state.Action == EntityActions.Remove)
            {
                if (m_clientProjectiles.TryGetValue(state.Id, out ClientProjectileReplica removed))
                {
                    RemoveProjectileReplica(removed);
                    m_clientProjectiles.Remove(state.Id);
                    m_replicasRemoved++;
                }
                return true;
            }
            if (m_clientProjectiles.TryGetValue(state.Id, out ClientProjectileReplica replica) &&
                replica.Projectile != null && !replica.Projectile.ToRemove)
            {
                ApplyProjectileTarget(replica, state);
                m_replicasUpdated++;
                return true;
            }
            Projectile projectile;
            try
            {
                projectile = m_projectiles.AddProjectile(state.Value, state.Position,
                    state.Velocity, state.AngularVelocity, null);
            }
            catch (Exception e)
            {
                reason = "add-threw-" + e.GetType().Name;
                m_applyFailures++;
                return false;
            }
            if (projectile == null)
            {
                reason = "add-returned-null";
                m_applyFailures++;
                return false;
            }
            replica = new ClientProjectileReplica
            {
                Projectile = projectile,
                LastUpdateTime = Time.RealTime
            };
            ApplyProjectileTarget(replica, state);
            m_clientProjectiles[state.Id] = replica;
            m_replicasCreated++;
            return true;
        }

        private static void ApplyProjectileTarget(ClientProjectileReplica replica,
            in ProjectileStateData state)
        {
            replica.TargetPosition = state.Position;
            replica.TargetVelocity = state.Velocity;
            replica.TargetRotation = state.Rotation;
            replica.TargetAngularVelocity = state.AngularVelocity;
            replica.TargetTrailOffset = state.TrailOffset;
            replica.TargetStuck = state.Stuck;
            replica.TargetIncendiary = state.Incendiary;
            replica.LastUpdateTime = Time.RealTime;
            if (replica.Projectile == null) return;
            replica.Projectile.Value = state.Value;
            replica.Projectile.IsIncendiary = state.Incendiary;
            replica.Projectile.TrailOffset = state.TrailOffset;
        }

        /// <summary>
        /// 全局声音中继覆盖口（组合根注入）：武器开火 / 拾取声音在本端播放后已经由定向事件
        /// (WeaponFireEvent / PickupSoundEvent) 发给客户端，登记后全局捕获不再重复发送。
        /// 客户端上调用是空操作（全局捕获未启用）。
        /// </summary>
        public IHostSoundCoverPort SoundCover { get; set; }

        /// <summary>
        /// 武器开火表现（声音）：只播放本机音频，不生成实体、不扣物品、不造成伤害。
        /// 主机执行完客户端请求后本机播一次；其他客户端收到 WeaponFireEvent 后按同一入口播。
        /// </summary>
        public bool TryPresentWeaponFire(in WeaponFireEventData fire, out string reason)
        {
            reason = null;
            if (m_project == null)
            {
                reason = "no-project";
                return false;
            }
            switch (fire.Kind)
            {
                case ProjectileFireKinds.Throwable:
                    SoundCover?.CoverPlayedSoundRange("Audio/Throw", fire.Position,
                        0.2f, 0.3f, 2f, 1, "weapon-fire");
                    ProjectileFirePresentation.PlayThrow(m_project, null, fire.Position);
                    return true;
                case ProjectileFireKinds.Bow:
                    SoundCover?.CoverPlayedSound("Audio/Bow", fire.Position, 1f, 3f, 1,
                        "weapon-fire");
                    ProjectileFirePresentation.PlayBow(m_project, fire.Position,
                        crossbow: false);
                    return true;
                case ProjectileFireKinds.Crossbow:
                    SoundCover?.CoverPlayedSound("Audio/Bow", fire.Position, 1f, 3f, 1,
                        "weapon-fire");
                    SoundCover?.CoverPlayedSound("Audio/CrossbowBoing", fire.Position, 1f, 3f,
                        1, "weapon-fire");
                    ProjectileFirePresentation.PlayBow(m_project, fire.Position,
                        crossbow: true);
                    return true;
                case ProjectileFireKinds.Musket:
                    SoundCover?.CoverPlayedSound(
                        fire.Misfire ? "Audio/MusketMisfire" : "Audio/MusketFire",
                        fire.Position, 1f, fire.Misfire ? 3f : 10f, 1, "weapon-fire");
                    ProjectileFirePresentation.PlayMusketPresentation(m_project, fire.Position,
                        fire.Direction, fire.Misfire);
                    return true;
                default:
                    reason = "kind-unknown-" + fire.Kind;
                    return false;
            }
        }

        /// <summary>
        /// 拾取声音：只调引擎音频，参数与 <c>SubsystemPickables.Update</c> 完全一致。
        /// Source: Survivalcraft/Game/SubsystemPickables.cs:210-213
        ///   <c>PlaySound("Audio/PickableCollected", 0.7f, -0.4f, pickable.Position, 2f,
        ///   autoDelay: false)</c>
        /// </summary>
        public bool TryPresentPickupSound(in PickupSoundEventData sound, out string reason)
        {
            reason = null;
            if (m_project == null)
            {
                reason = "no-project";
                return false;
            }
            if (!PickupSoundPolicy.IsValidPosition(sound.Position))
            {
                reason = "pickup-sound-position-invalid";
                return false;
            }
            SubsystemAudio audio = m_project.FindSubsystem<SubsystemAudio>(false);
            if (audio == null)
            {
                reason = "no-subsystem-audio";
                return false;
            }
            audio.PlaySound("Audio/PickableCollected", 0.7f, -0.4f, sound.Position, 2f,
                autoDelay: false);
            SoundCover?.CoverPlayedSound("Audio/PickableCollected", sound.Position, 0.7f, 2f, 1,
                "pickup-sound");
            return true;
        }

        /// <summary>客户端：本机监听者位置（活动相机 → 本机角色）；读不到明确失败。</summary>
        public bool TryGetAudioListenerPosition(out Vector3 position, out string reason)
        {
            position = Vector3.Zero;
            reason = null;
            Project project = m_project;
            if (project == null)
            {
                reason = "no-project";
                return false;
            }
            SubsystemAudio audio = project.FindSubsystem<SubsystemAudio>(false);
            if (audio != null)
            {
                ReadOnlyList<Vector3> listeners = audio.ListenerPositions;
                if (listeners.Count > 0)
                {
                    position = listeners[0];
                    return true;
                }
            }
            if (m_spawner != null &&
                m_spawner.TryGetLocalPlayer(project, out PlayerData local, out string playerReason))
            {
                ComponentPlayer player = local?.ComponentPlayer;
                if (player?.ComponentBody != null)
                {
                    position = player.ComponentBody.Position;
                    return true;
                }
                reason = "local-player-body-missing";
                return false;
            }
            reason = "no-listener";
            return false;
        }

        /// <summary>
        /// 主机：收集在线玩家端位置（拾取声音的“附近客户端”裁剪）。
        /// 读不到位置的端仍然返回（HasPosition=false），由收件人策略明确计数。
        /// </summary>
        public bool TryCollectPlayerPeers(List<RemoteSoundPeer> target, out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "target-null";
                return false;
            }
            target.Clear();
            if (m_spawner == null)
            {
                reason = "no-spawner";
                return false;
            }
            int count = 0;
            foreach (int clientId in m_spawner.ClientIds)
            {
                bool hasPosition = m_spawner.TryGetAvatarCell(clientId, out Vector3 position,
                    out _);
                target.Add(new RemoteSoundPeer(clientId, hasPosition, position));
                count++;
            }
            if (count == 0) reason = "no-avatars";
            return count > 0;
        }

        // ================================================================ 客户端：每帧插值 + 扫描

        public void TickClient(float deltaTime)
        {
            if (!m_clientReplica) return;
            float step = MathUtils.Clamp(deltaTime, 0f, 0.05f);
            double now = Time.RealTime;
            m_ownDropPredictions.Prune(now);
            UpdatePickableReplicas(step, now);
            UpdateProjectileReplicas(step, now);
            // 2026-10-26：客户端拾取扫描 / 请求链已删 —— 拾取由主机引擎对化身原生计算
            // （1.75m 磁吸 + 1.0m 收取），主机经 HostNativePickup 通知背包增量与拾取音。
        }

        private void UpdatePickableReplicas(float step, double now)
        {
            List<int> stale = null;
            foreach (KeyValuePair<int, ClientPickableReplica> item in m_clientPickables)
            {
                Pickable pickable = item.Value.Pickable;
                if (pickable == null || pickable.ToRemove)
                {
                    if (stale == null) stale = new List<int>();
                    stale.Add(item.Key);
                    continue;
                }
                double age = MathUtils.Clamp(now - item.Value.LastUpdateTime, 0.0, 0.25);
                Vector3 predicted = item.Value.TargetPosition +
                    item.Value.TargetVelocity * (float)age;
                // 引擎静止判据：位置两帧不动就不能再按 velocity 外推（旧实现同一条注释）。
                bool hostPositionStable = item.Value.HasPreviousTargetPosition &&
                    Vector3.DistanceSquared(item.Value.TargetPosition,
                        item.Value.PreviousTargetPosition) < RestingPositionEpsilonSquared;
                bool resting = !item.Value.TargetHasFlyTo &&
                    (item.Value.TargetVelocity.LengthSquared() < 0.04f || hostPositionStable);
                if (resting)
                {
                    pickable.Position = item.Value.TargetPosition;
                    pickable.Velocity = Vector3.Zero;
                    item.Value.PresentationVelocity = Vector3.Zero;
                    item.Value.Initialized = true;
                }
                else if (!item.Value.Initialized)
                {
                    pickable.Position = item.Value.TargetPosition;
                    pickable.Velocity = item.Value.TargetVelocity;
                    item.Value.PresentationVelocity = item.Value.TargetVelocity;
                    item.Value.Initialized = true;
                }
                else
                {
                    Vector3 error = predicted - pickable.Position;
                    float blend = 1f - (float)Math.Exp(-12f * step);
                    Vector3 correction = error * blend;
                    float maxStep = 24f * step;
                    float length = correction.Length();
                    if (length > maxStep) correction *= maxStep / length;
                    pickable.Position += correction;
                    if (Vector3.DistanceSquared(pickable.Position, predicted) < 0.0004f)
                        pickable.Position = predicted;
                    Vector3 desired = item.Value.TargetVelocity + error / 0.18f;
                    float response = 10f;
                    float velocityBlend = 1f - (float)Math.Exp(-response * step);
                    item.Value.PresentationVelocity = Vector3.Lerp(
                        item.Value.PresentationVelocity, desired, velocityBlend);
                    pickable.Velocity = item.Value.PresentationVelocity;
                }
                // 主机磁吸目标优先；本地扫描（拾取动画表现）在下一段覆盖它。
                pickable.FlyToPosition = item.Value.TargetHasFlyTo
                    ? item.Value.TargetFlyTo
                    : (Vector3?)null;
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++)
            {
                m_clientPickables.Remove(stale[i]);
            }
        }

        private void UpdateProjectileReplicas(float step, double now)
        {
            List<int> stale = null;
            foreach (KeyValuePair<int, ClientProjectileReplica> item in m_clientProjectiles)
            {
                Projectile projectile = item.Value.Projectile;
                if (projectile == null || projectile.ToRemove)
                {
                    if (stale == null) stale = new List<int>();
                    stale.Add(item.Key);
                    continue;
                }
                double age = MathUtils.Clamp(now - item.Value.LastUpdateTime, 0.0, 0.35);
                float ageFloat = (float)age;
                Vector3 targetPosition = item.Value.TargetPosition +
                    item.Value.TargetVelocity * ageFloat;
                targetPosition.Y -= 5f * ageFloat * ageFloat;
                Vector3 targetVelocity = item.Value.TargetVelocity +
                    new Vector3(0f, -10f * ageFloat, 0f);
                if (item.Value.TargetStuck)
                {
                    targetPosition = item.Value.TargetPosition;
                    targetVelocity = Vector3.Zero;
                }
                if (!item.Value.Initialized)
                {
                    projectile.Position = targetPosition;
                    projectile.Velocity = targetVelocity;
                    projectile.Rotation = item.Value.TargetRotation;
                    projectile.AngularVelocity = item.Value.TargetAngularVelocity;
                    item.Value.Initialized = true;
                }
                else
                {
                    float distanceSquared = Vector3.DistanceSquared(projectile.Position,
                        targetPosition);
                    if (distanceSquared > 25f) projectile.Position = targetPosition;
                    else projectile.Position = Vector3.Lerp(projectile.Position, targetPosition,
                        0.35f);
                    projectile.Velocity = Vector3.Lerp(projectile.Velocity, targetVelocity, 0.5f);
                    projectile.Rotation = Vector3.Lerp(projectile.Rotation,
                        item.Value.TargetRotation, 0.5f);
                    projectile.AngularVelocity = Vector3.Lerp(projectile.AngularVelocity,
                        item.Value.TargetAngularVelocity, 0.5f);
                }
                projectile.TrailOffset = item.Value.TargetTrailOffset;
                projectile.IsIncendiary = item.Value.TargetIncendiary;
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++) m_clientProjectiles.Remove(stale[i]);
        }

        /// <summary>
        /// 【已删】客户端拾取扫描 / 请求链（旧 ScanPickupCandidates，20Hz / 1.0m 触发）。
        /// 2026-10-26 架构口径（用户定）：**丢出才由客户端请求；拾取由主机模拟，主机通知客户端**。
        /// 旧链的三个毛病都源于"客户端决定拾取时机"：触发线 1.0m 把引擎 1.75m 磁吸带吃掉
        /// （主机吸 3 格、客户端 1 格）、本地 FlyToPosition 假飞行动画与主机真物品脱节、
        /// 触发节奏随客户端扫描漂移（"过一会突然出现"）。现在引擎对化身原生拾取，物品在
        /// 主机真实飞行、经实体复制镜像给所有端。
        /// Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update:183-219
        /// </summary>

        // ------------------------------------------------------------ 主机：原生拾取捕获窗口

        /// <summary>引擎在窗口内移除拾取物时记录（拾取音 / 结果归属用；host 才有意义）。</summary>
        private void OnHostPickableRemoved(Pickable pickable)
        {
            if (m_pickupWindowActive) m_windowRemovedPickables.Add(pickable);
        }

        /// <summary>
        /// 主机侧化身枚举（外壳 AvatarPlayersProvider）：注入引擎拾取循环的玩家集合。
        /// 化身的 PlayerData 不在 SubsystemPlayers.m_playersData 里（建好即移除），
        /// 引擎重建玩家列表时化身会被清掉 —— 所以每一帧拾取窗口都重新注入（见外壳注释）。
        /// </summary>
        private IEnumerable<ComponentPlayer> EnumerateAvatarPlayers()
        {
            if (m_spawner == null || m_clientReplica) yield break;
            foreach (int clientId in m_spawner.ClientIds)
            {
                if (!m_spawner.TryGet(clientId, out PlayerData data)) continue;
                ComponentPlayer player = data?.ComponentPlayer;
                if (player == null) continue;
                m_avatarsInjected++;
                yield return player;
            }
        }

        public void BeginPickupWindow()
        {
            m_windowRemovedPickables.Clear();
            m_windowAvatarInventories = null;
            m_pickupWindowActive = false;
            if (m_clientReplica || m_spawner == null || m_project == null) return;
            foreach (int clientId in m_spawner.ClientIds)
            {
                if (!m_spawner.TryGet(clientId, out PlayerData data)) continue;
                ComponentPlayer player = data?.ComponentPlayer;
                IInventory inventory = player?.ComponentMiner?.Inventory;
                if (inventory == null) continue;
                // 诊断（一次性）：主机侧化身背包的真实槽数 —— 引擎 FindAcquireSlotForItem
                // 在这份背包上找槽，槽数 / 有没有同类槽决定"能不能拾取"。
                LogOnce("pickup-window-avatar:" + clientId,
                    "拾取窗口化身背包：clientId=" + clientId + " slots=" + inventory.SlotsCount);
                var snapshot = new AvatarInventorySnapshot
                {
                    ClientId = clientId,
                    Player = player,
                    Values = new int[inventory.SlotsCount],
                    Counts = new int[inventory.SlotsCount]
                };
                for (int i = 0; i < inventory.SlotsCount; i++)
                {
                    snapshot.Values[i] = inventory.GetSlotValue(i);
                    snapshot.Counts[i] = inventory.GetSlotCount(i);
                }
                (m_windowAvatarInventories ??= new List<AvatarInventorySnapshot>()).Add(snapshot);
            }
            m_pickupWindowActive = true;
        }

        public void EndPickupWindow()
        {
            m_pickupWindowActive = false;
            List<AvatarInventorySnapshot> snapshots = m_windowAvatarInventories;
            m_windowAvatarInventories = null;
            if (snapshots == null || snapshots.Count == 0)
            {
                m_windowRemovedPickables.Clear();
                return;
            }
            for (int s = 0; s < snapshots.Count; s++)
            {
                AvatarInventorySnapshot snapshot = snapshots[s];
                IInventory inventory = snapshot.Player?.ComponentMiner?.Inventory;
                if (inventory == null ||
                    inventory.SlotsCount != snapshot.Values.Length) continue;
                List<int> indices = null, values = null, counts = null;
                for (int i = 0; i < inventory.SlotsCount; i++)
                {
                    int value = inventory.GetSlotValue(i);
                    int count = inventory.GetSlotCount(i);
                    if (value == snapshot.Values[i] && count == snapshot.Counts[i]) continue;
                    (indices ??= new List<int>()).Add(i);
                    (values ??= new List<int>()).Add(value);
                    (counts ??= new List<int>()).Add(count);
                }
                if (indices == null) continue;
                // 归属：取窗口内被移除、且离该化身最近的拾取物（引擎只在整堆拾完时移除并播音）。
                Pickable attributed = null;
                float bestSquared = float.MaxValue;
                Vector3 avatarPosition = snapshot.Player.ComponentBody?.Position ??
                    Vector3.Zero;
                for (int r = 0; r < m_windowRemovedPickables.Count; r++)
                {
                    Pickable removed = m_windowRemovedPickables[r];
                    float squared = Vector3.DistanceSquared(removed.Position, avatarPosition);
                    if (squared < bestSquared)
                    {
                        bestSquared = squared;
                        attributed = removed;
                    }
                }
                int pickableId = 0;
                if (attributed != null && m_pickableKeys.TryGetValue(attributed, out HostKey key))
                    pickableId = key.Id;
                // 诊断（一次性）：窗口检出原生拾取（背包差分 + 归属）。
                LogOnce("native-pickup-detected:" + snapshot.ClientId,
                    "原生拾取检出：clientId=" + snapshot.ClientId + " 槽变更=" + indices.Count +
                    " 归属pickable=" + pickableId + " 窗口内移除=" + m_windowRemovedPickables.Count);
                var outcome = new PickableAcquireOutcome(
                    attributed?.Count ?? 0, attributed != null,
                    indices.ToArray(), values.ToArray(), counts.ToArray(),
                    CurrentInventoryVersion(), attributed?.Value ?? 0,
                    attributed?.Position ?? avatarPosition,
                    playPickupSound: attributed != null);
                m_hostNativePickups++;
                try
                {
                    HostNativePickup?.Invoke(snapshot.ClientId, outcome);
                }
                catch (Exception e)
                {
                    m_hostNativePickupFaults++;
                    LogProblemOnce("native-pickup:" + e.GetType().Name,
                        "主机原生拾取通知处理器异常（clientId=" + snapshot.ClientId + " 槽位数=" +
                        indices.Count + "）：" + e.GetType().Name + " " + e.Message +
                        " → 拾取本身已生效，只是该客户端这次没收到通知（背包会在下次关键帧对齐）");
                }
            }
            m_windowRemovedPickables.Clear();
        }

        /// <summary>
        /// 客户端：清掉**不是**主机复制来的拾取物（客户端本地预测 / UI 掉落 / 方块行为掉落）。
        /// 客户端不允许自己创建拾取物；这些对象若保留，会与主机的权威掉落重复或永远不消失。
        /// 明确计数 + 限流日志（不是静默兜底）。
        /// </summary>
        internal void CullForeignClientPickables()
        {
            if (!m_clientReplica || m_pickables == null) return;
            ReadOnlyList<Pickable> list = m_pickables.Pickables;
            if (list.Count == 0) return;
            var owned = new HashSet<Pickable>();
            foreach (KeyValuePair<int, ClientPickableReplica> item in m_clientPickables)
                if (item.Value.Pickable != null) owned.Add(item.Value.Pickable);
            double now = Time.RealTime;
            m_recentDropPredictions.RemoveAll(item => item.ExpiryTime <= now);
            List<Pickable> foreign = null;
            int deferred = 0;
            int predictionCulled = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Pickable pickable = list[i];
                if (pickable == null || owned.Contains(pickable)) continue;
                int contents = global::Game.Terrain.ExtractContents(pickable.Value);
                // 主机副本已经出现：本地预测（同物品 / 3m 内）可以安全清掉。
                bool hostReplicaExists = false;
                foreach (KeyValuePair<int, ClientPickableReplica> item in m_clientPickables)
                {
                    Pickable replica = item.Value.Pickable;
                    if (replica == null || replica == pickable) continue;
                    if (global::Game.Terrain.ExtractContents(replica.Value) != contents)
                        continue;
                    if (Vector3.DistanceSquared(replica.Position, pickable.Position) > 9f)
                        continue;
                    hostReplicaExists = true;
                    break;
                }
                if (hostReplicaExists)
                {
                    pickable.ToRemove = true;
                    if (TryGetPickableList(out List<Pickable> raw)) raw.Remove(pickable);
                    m_dropPredictionCulled++;
                    predictionCulled++;
                    continue;
                }
                // 刚提交的丢弃请求还没有对应的主机副本：先保留，等副本出现后再清
                // （绝不先清掉本地预测、又因为状态流竞态看不到主机副本 → 地上空无一物）。
                bool pendingDropMatch = false;
                for (int j = 0; j < m_recentDropPredictions.Count; j++)
                {
                    PendingDropPrediction prediction = m_recentDropPredictions[j];
                    if (global::Game.Terrain.ExtractContents(prediction.ItemValue) != contents)
                        continue;
                    if (Vector3.DistanceSquared(prediction.Position, pickable.Position) > 9f)
                        continue;
                    pendingDropMatch = true;
                    break;
                }
                if (pendingDropMatch)
                {
                    m_dropPredictionDeferred++;
                    deferred++;
                    continue;
                }
                if (foreign == null) foreign = new List<Pickable>();
                foreign.Add(pickable);
                // 这些是**本机引擎**生成的预测掉落（本机挖 / 放触发的 DestroyCell）。
                // 记下物品 + 落点：主机的权威副本到达时按它认领 → 该副本在拾取宽限内不被本机请求。
                m_ownDropPredictions.Remember(contents, pickable.Position, now);
            }
            if (foreign != null)
            {
                for (int i = 0; i < foreign.Count; i++)
                {
                    foreign[i].ToRemove = true;
                    if (TryGetPickableList(out List<Pickable> raw)) raw.Remove(foreign[i]);
                    m_foreignPickablesRemoved++;
                }
            }
            if (predictionCulled > 0)
            {
                LogRateLimited("drop-prediction-culled",
                    "主机丢弃副本已出现 → 清掉 " + predictionCulled + " 个本地预测拾取物（累计 " +
                    m_dropPredictionCulled + "）");
            }
            if (deferred > 0)
            {
                LogRateLimited("drop-prediction-deferred",
                    "主机丢弃副本还没到 → 暂缓清理 " + deferred + " 个本地预测拾取物" +
                    "（累计暂缓 " + m_dropPredictionDeferred + "；副本到达后会清掉）");
            }
            if (foreign != null)
            {
                LogRateLimited("foreign-pickable",
                    "客户端清除了 " + foreign.Count + " 个本地生成的拾取物（累计 " +
                    m_foreignPickablesRemoved + "）：拾取物只能由主机创建并经实体复制下发");
            }
        }

        /// <summary>
        /// 客户端：清掉**不是**主机复制来的投射物（通常是客户端武器本地预测）。
        /// 客户端不允许自己模拟投射物；这些对象如果没有主机快照就会永远停在空中。
        /// 明确计数 + 限流日志（不是静默兜底）。
        /// </summary>
        internal void CullForeignClientProjectiles()
        {
            if (!m_clientReplica || m_projectiles == null) return;
            ReadOnlyList<Projectile> list = m_projectiles.Projectiles;
            if (list.Count == 0) return;
            var owned = new HashSet<Projectile>();
            foreach (KeyValuePair<int, ClientProjectileReplica> item in m_clientProjectiles)
                if (item.Value.Projectile != null) owned.Add(item.Value.Projectile);
            List<Projectile> foreign = null;
            for (int i = 0; i < list.Count; i++)
            {
                Projectile projectile = list[i];
                if (projectile == null || owned.Contains(projectile)) continue;
                if (foreign == null) foreign = new List<Projectile>();
                foreign.Add(projectile);
            }
            if (foreign == null) return;
            for (int i = 0; i < foreign.Count; i++)
            {
                RemoveForeignProjectile(foreign[i]);
                m_foreignProjectilesRemoved++;
            }
            LogRateLimited("foreign-projectile",
                "客户端清除了 " + foreign.Count + " 个本地生成的投射物（累计 " +
                m_foreignProjectilesRemoved + "）：客户端不模拟投射物；" +
                "武器开火必须由主机创建并经实体复制下发");
        }

        private void RemoveForeignProjectile(Projectile projectile)
        {
            if (projectile == null) return;
            projectile.ToRemove = true;
            if (TryGetProjectileList(out List<Projectile> list)) list.Remove(projectile);
        }

        // ================================================================ 客户端：清理

        public bool ResetClientReplicas(string reason)
        {
            RemoveAllClientReplicas(reason);
            return true;
        }

        private void RemoveAllClientReplicas(string reason)
        {
            if (m_clientPickables.Count == 0 && m_clientProjectiles.Count == 0) return;
            int pickables = m_clientPickables.Count;
            int projectiles = m_clientProjectiles.Count;
            foreach (KeyValuePair<int, ClientPickableReplica> item in m_clientPickables)
                RemovePickableReplica(item.Value);
            m_clientPickables.Clear();
            foreach (KeyValuePair<int, ClientProjectileReplica> item in m_clientProjectiles)
                RemoveProjectileReplica(item.Value);
            m_clientProjectiles.Clear();
            m_replicasRemoved += pickables + projectiles;
            Log("清空实体副本（" + reason + "）：拾取物 " + pickables + " / 投射物 " + projectiles);
        }

        private void RemovePickableReplica(ClientPickableReplica replica)
        {
            if (replica?.Pickable == null) return;
            Pickable pickable = replica.Pickable;
            pickable.ToRemove = true;
            if (!TryGetPickableList(out List<Pickable> list))
            {
                m_pickableListFailures++;
                LogProblemOnce("pickable-list-remove",
                    "无法访问 SubsystemPickables.m_pickables → 拾取物副本只能置 ToRemove" +
                    "（若 base.Update 不跑就会残留；计数 " + m_pickableListFailures + "）");
                return;
            }
            list.Remove(pickable);
        }

        private bool TryGetPickableList(out List<Pickable> list)
        {
            list = null;
            if (m_pickables == null) return false;
            if (m_pickableList != null && ReferenceEquals(m_pickableListOwner, m_pickables))
            {
                list = m_pickableList;
                return true;
            }
            try
            {
                m_pickableList = m_parentField.GetParentField<List<Pickable>>(m_pickables,
                    "m_pickables", typeof(SubsystemPickables));
                m_pickableListOwner = m_pickables;
            }
            catch (Exception e)
            {
                m_pickableList = null;
                m_pickableListOwner = null;
                LogProblemOnce("pickable-list-field:" + e.GetType().Name,
                    "反射读取 SubsystemPickables.m_pickables 失败（" + e.GetType().Name + " " +
                    e.Message + "）→ 客户端无法从列表移除拾取物副本");
                return false;
            }
            if (m_pickableList == null)
            {
                LogProblemOnce("pickable-list-null",
                    "SubsystemPickables.m_pickables 为空（引擎版本差异）→ 无法移除拾取物副本");
                return false;
            }
            list = m_pickableList;
            return true;
        }

        private void RemoveProjectileReplica(ClientProjectileReplica replica)
        {
            if (replica?.Projectile == null) return;
            Projectile projectile = replica.Projectile;
            projectile.ToRemove = true;
            if (projectile.TrailParticleSystem != null)
            {
                try
                {
                    projectile.TrailParticleSystem.IsStopped = true;
                    SubsystemParticles particles =
                        m_project?.FindSubsystem<SubsystemParticles>(false);
                    // Source: Survivalcraft/Game/SubsystemProjectiles.cs:RemoveTrail
                    // 引擎自己也是 (ParticleSystemBase) 强转后交给 SubsystemParticles。
                    if (projectile.TrailParticleSystem is ParticleSystemBase trail &&
                        particles != null && particles.ContainsParticleSystem(trail))
                        particles.RemoveParticleSystem(trail);
                }
                catch (Exception)
                {
                    // 粒子清理失败不影响副本移除；下一帧重试不会造成逻辑错误。
                }
                projectile.TrailParticleSystem = null;
            }
            if (!TryGetProjectileList(out List<Projectile> list))
            {
                // 拿不到私有列表：ToRemove 已经置位；若 base.Update 永不运行就无法真正移除。
                m_projectileListFailures++;
                LogProblemOnce("projectile-list-remove",
                    "无法访问 SubsystemProjectiles.m_projectiles → 投射物副本只能置 ToRemove，" +
                    "列表里可能残留（计数可审计：" + m_projectileListFailures + "）");
                return;
            }
            list.Remove(projectile);
        }

        private bool TryGetProjectileList(out List<Projectile> list)
        {
            list = null;
            if (m_projectiles == null) return false;
            if (m_projectileList != null &&
                ReferenceEquals(m_projectileListOwner, m_projectiles))
            {
                list = m_projectileList;
                return true;
            }
            try
            {
                m_projectileList = m_parentField.GetParentField<List<Projectile>>(m_projectiles,
                    "m_projectiles", typeof(SubsystemProjectiles));
                m_projectileListOwner = m_projectiles;
            }
            catch (Exception e)
            {
                m_projectileList = null;
                m_projectileListOwner = null;
                LogProblemOnce("projectile-list-field:" + e.GetType().Name,
                    "反射读取 SubsystemProjectiles.m_projectiles 失败（" + e.GetType().Name +
                    " " + e.Message + "）→ 客户端无法从列表移除投射物副本");
                return false;
            }
            if (m_projectileList == null)
            {
                LogProblemOnce("projectile-list-null",
                    "SubsystemProjectiles.m_projectiles 为空（引擎版本差异）→ 客户端无法移除投射物副本");
                return false;
            }
            list = m_projectileList;
            return true;
        }

        // ================================================================ 诊断

        public string Describe() => "clientReplica=" + m_clientReplica +
            " ready=" + IsReplicaReady + " pickables=" + m_clientPickables.Count +
            " projectiles=" + m_clientProjectiles.Count +
            " hostCaptures=" + m_hostCaptures + " ids=" + m_idAssignments +
            " created=" + m_replicasCreated + " updated=" + m_replicasUpdated +
            " removed=" + m_replicasRemoved + " applyFailures=" + m_applyFailures +
            " acquireAccepted=" + m_acquireAccepted + " acquireRejected=" + m_acquireRejected +
            " hostNativePickups=" + m_hostNativePickups +
            " hostNativePickupFaults=" + m_hostNativePickupFaults +
            " avatarsInjected=" + m_avatarsInjected +
            " projectileListFailures=" + m_projectileListFailures +
            " foreignCulled=" + (m_foreignPickablesRemoved + m_foreignProjectilesRemoved) +
            " foreignPickablesRemoved=" + m_foreignPickablesRemoved +
            " foreignProjectilesRemoved=" + m_foreignProjectilesRemoved +
            " fireSpawnAtHost=" + m_fireSpawnAtHost +
            " fireSpawnCollided=" + m_fireSpawnCollided +
            " dropPredictionCulled=" + m_dropPredictionCulled +
            " dropPredictionDeferred=" + m_dropPredictionDeferred +
            " pickableListFailures=" + m_pickableListFailures +
            " dropIntents=" + m_dropIntentSubmissions + "/" + m_dropIntentFailures +
            " uiDropIntercepted=" + m_uiDropIntents +
            " uiDropSubmitFailures=" + m_uiDropSubmitFailures +
            " uiDropLocalPickableCancelled=" + m_uiDropLocalPickableCancelled +
            " dropPhantomSuppressed=" + m_dropPhantomSuppressed +
            " uiDropPhantomUnmatched=" + m_uiDropPhantomUnmatched +
            " uiDropSnapshot=" + (m_localInventorySnapshotValid ? "valid" : "none") +
            " fireIntents=" + m_fireIntentSubmissions + "/" + m_fireIntentFailures +
            " hostDrop=" + m_dropAccepted + "/" + m_dropRejected +
            " hostFire=" + m_fireAccepted + "/" + m_fireRejected +
            " blockDropsSpawned=" + m_blockDropsSpawned +
            " dropPrepareFailures=" + m_dropPrepareFailures +
            " dropSpawnFailures=" + m_dropSpawnFailures +
            " dropSpawnVelocity=" + m_dropSpawnVelocity +
            " pickupGraceBlocked=" + m_pickupGraceBlocked +
            " pickupGrace[" + m_dropGrace.Describe() + "]" +
            " ownDropPrediction[" + m_ownDropPredictions.Describe() + "]";

        private void Log(string message) => m_diagnostics?.Info(m_ownerId, message);

        /// <summary>一次性 INFO（每个 key 只写一行；用于窗口挂接 / 原生拾取检出的诊断证据）。</summary>
        private void LogOnce(string key, string message)
        {
            if (!m_loggedOnce.Add(key)) return;
            m_diagnostics?.Info(m_ownerId, message);
        }

        private void LogProblemOnce(string key, string message)
        {
            if (!m_loggedOnce.Add(key)) return;
            m_diagnostics?.Warn(m_ownerId, message);
        }

        private void LogRateLimited(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            if (m_lastProblemMs.Count > 512) m_lastProblemMs.Clear();
            m_lastProblemMs[key] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}
