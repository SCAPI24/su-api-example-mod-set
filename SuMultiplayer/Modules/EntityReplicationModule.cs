using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using PE = SuMultiplayer.Protocol.Entities;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 拾取物（掉落物 / 爆炸碎片 / 投掷物落地）与投射物（箭 / 子弹 / 炸弹）复制模块。
    ///
    /// 需求口径（主机权威）：
    ///   · 主机是**唯一**创建 / 更新 / 删除这两类实体的地方（挖方块掉落、爆炸碎片、投掷、
    ///     动物产物……都发生在主机引擎里）；
    ///   · 客户端把它们当**副本**：只创建引擎 Pickable / Projectile 用于渲染与播放，
    ///     不参与拾取结算，不写地形，不造成伤害；
    ///   · 客户端拾取 = 请求：<c>PickableAcquireRequest</c> → 主机校验 / 落地 → 广播
    ///     <c>EntityState(REMOVE)</c> 给所有端 + 只给拾取者一条带稀疏背包增量的回执；
    ///   · 客户端丢弃 = 请求：<c>PickableDropRequest</c> → 主机校验活跃槽内容 / 距离 →
    ///     从权威背包移出 → AddPickable → 回执带稀疏背包增量（客户端本地从不先扣除）；
    ///   · 客户端投掷 / 弓 / 弩 / 火枪 = 请求：<c>ProjectileFireRequest</c> → 主机用引擎自己的
    ///     FireProjectile 生成权威投射物 + 消费道具 / 耐久 → 回执带稀疏背包增量；
    ///     正常流程客户端不生成任何本地投射物（foreignCulled 计数应保持 0，只作安全网）；
    ///   · 加入 / 重连 / 主动 resync → <c>EntitySnapshot</c>；complete=true 时客户端用快照
    ///     替换副本集合，保证不会出现重复或幽灵副本。
    ///
    /// 节奏与预算（与旧实现的 8Hz 快照 / 32Hz 脉冲同一条思路，但改成**变化驱动 + 合并**）：
    ///   · 32Hz 脉冲里最多 2 条 EntityState；每条最多 128 条拾取物 + 64 条投射物；
    ///   · 待发队列有界，超出时按状态（REMOVE &gt; ADD &gt; UPDATE）保序丢弃并计数；
    ///   · 快照有界（512 / 256 条），截断时 complete=false（客户端据此不删除未列出的副本）。
    ///
    /// **逐用户兴趣（客户端只订阅自己窗口内的实体）**：
    ///   · 主机仍然模拟 / 跟踪**全部**实体（不裁剪权威），只是不再把窗口外的记录发给看不见它的端；
    ///   · mask 位序 = **稳定用户键**（userid 派生，见 <c>Net/EntityInterestNet.cs</c>），**不是 clientId**
    ///     —— clientId 每次连接都会重分配，按它投递会把数据投给下一个人；每个端自己的角色在本地
    ///     从 0 开始编号（<c>PlayerKey</c> / <c>ITransportPort.LocalClientId</c>），按传输编号对齐必然错位；
    ///   · 一次编码 + 按 mask 并发入队（<c>IPeerBroadcastPort.BroadcastToPlayersMasked</c>），不逐端重编码；
    ///   · 客户端申报的区域 = 它的兴趣窗口（中心区块 + 半径，与地形同一口径）。进入某区块时补该区块内
    ///     现有实体的 ADD，离开时补 REMOVE —— 掉落物 / 投射物本就持续更新位置，所以只在"首次进入该 chunk"
    ///     时需要补一次基线；
    ///   · 窗口未知（还没上报兴趣）的端**不发**（计数 + 限流日志），绝不退化成全世界广播。
    ///
    /// 旧实现来源：
    ///   Source: Mod/ScMultiplayer/Message/PickableSyncMessage.cs:PickableSyncMessage
    ///   Source: Mod/ScMultiplayer/Message/ProjectileSyncMessage.cs:ProjectileSyncMessage
    ///   Source: Mod/ScMultiplayer/Func/WorldObjectSynchronizer.cs（主机分配 id / 发布 / 删除）
    ///   Source: Mod/ScMultiplayer/Modules/Entity/ScMultiplayerPickableProjectileHandlers.cs
    ///           （Create / UpdatePosition / Delete / RequestAcquire / Acquire + 稀疏背包增量）
    ///   Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldSync.cs:146-300
    ///           （PickableAdded/PickableRemoved 生命周期 + 静止拾取物零速度）
    ///
    /// 本模块**无静态状态**，只依赖端口（引擎侧实现见 Engine/PickableReplicaEngine.cs）。
    /// </summary>
    public sealed class EntityReplicationModule : IMultiplayerModule, ISuMPMessageModule,
        IEntityRequestSink
    {
        private const float PulseDuration = 1f / 32f;
        private const int MaxStateMessagesPerPulse = EntityReplicationLimits.MaxStateMessagesPerPulse;
        private const int MaxPickableRecordsPerMessage =
            EntityReplicationLimits.MaxPickableRecordsPerMessage;
        private const int MaxProjectileRecordsPerMessage =
            EntityReplicationLimits.MaxProjectileRecordsPerMessage;
        private const int MaxTrackedPickables = EntityReplicationLimits.MaxTrackedPickables;
        private const int MaxTrackedProjectiles = EntityReplicationLimits.MaxTrackedProjectiles;
        private const int MaxPendingPickables = EntityReplicationLimits.MaxPendingPickableChanges;
        private const int MaxPendingProjectiles = EntityReplicationLimits.MaxPendingProjectileChanges;
        private const int MaxSnapshotPickables = EntityReplicationLimits.MaxSnapshotPickables;
        private const int MaxSnapshotProjectiles = EntityReplicationLimits.MaxSnapshotProjectiles;

        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 5000;
        private const double StaleResyncSeconds = 15.0;
        private const double ResyncCooldownSeconds = 5.0;
        private const double PickupTimeoutSeconds = 5.0;
        private const double PickupRetryCooldownSeconds = 0.75;
        private const double SnapshotQueueTimeoutSeconds = 10.0;
        private const int MaxPendingPickups = 64;
        private const double DropTimeoutSeconds = 5.0;
        private const double FireTimeoutSeconds = 5.0;
        private const int MaxPendingDropRequests = 16;
        private const int MaxPendingFireRequests = 16;
        /// <summary>
        /// 掉落者拾取宽限（秒，模块时钟）：自己刚丢出 / 刚挖出来的掉落，本机扫描在窗口内**不发请求**
        /// （主机也会用 <c>pickup-grace</c> 拒绝同一个人对同一拾取物的请求）。与主机 1.5s 同口径。
        /// </summary>
        private const double DropPickupGraceSeconds = 1.5;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IEntityReplicaPort m_port;
        private readonly IPlayerActionPort m_actionPort;
        private readonly IPlayerInventoryStore m_inventoryStore;
        private readonly EntityReplicationCounters m_counters = new EntityReplicationCounters();
        private readonly EntityStateOrderer m_orderer = new EntityStateOrderer();
        private readonly PickupSoundOrderer m_pickupSoundOrderer = new PickupSoundOrderer();
        private readonly RemoteSoundRateLimiter m_pickupSoundSendLimiter =
            new RemoteSoundRateLimiter(PickupSoundPolicy.MaxEventsPerSecond);
        private readonly RemoteSoundRateLimiter m_pickupSoundRecvLimiter =
            new RemoteSoundRateLimiter(PickupSoundPolicy.MaxReceiveEventsPerSecond);
        private readonly List<RemoteSoundPeer> m_pickupSoundPeers =
            new List<RemoteSoundPeer>(16);

        // ---- 主机：已发布状态 / 待发合并队列
        private readonly Dictionary<int, PickableStateData> m_lastPickables =
            new Dictionary<int, PickableStateData>();
        private readonly Dictionary<int, ProjectileStateData> m_lastProjectiles =
            new Dictionary<int, ProjectileStateData>();

        // ---- 主机：逐用户兴趣（mask 位序 = 稳定用户槽位，**不是 clientId**，见 Net/EntityInterestNet.cs）
        //
        // 客户端只订阅自己窗口内的同步：每条待发记录带一个"该发给哪些用户"的位图，发的时刻只编码一次，
        // 按位图并发投给对应连接。主机自己仍然模拟 / 跟踪全部实体（不裁剪），只是不再把窗口外的
        // 记录塞给看不见它的端。
        private readonly EntityInterestSlots m_userSlots = new EntityInterestSlots();
        private readonly List<PlayerPeerUser> m_userRoster = new List<PlayerPeerUser>(16);
        private readonly List<PlayerPeerUser> m_userCapture = new List<PlayerPeerUser>(16);
        private readonly List<UserWindow> m_userWindows = new List<UserWindow>(16);
        private readonly List<long> m_windowEntered = new List<long>(4096);
        private readonly List<long> m_windowLeft = new List<long>(4096);
        private readonly List<int> m_seedScratch = new List<int>(512);
        private readonly Dictionary<long, List<int>> m_pickableChunks =
            new Dictionary<long, List<int>>();
        private readonly Dictionary<long, List<int>> m_projectileChunks =
            new Dictionary<long, List<int>>();
        private readonly Dictionary<int, ulong> m_pickableKnown = new Dictionary<int, ulong>();
        private readonly Dictionary<int, ulong> m_projectileKnown = new Dictionary<int, ulong>();
        /// <summary>实体当前所在区块（用于窗口边沿补种 / 补移除；键 = EntityInterestWindowDiff.Key）。</summary>
        private readonly Dictionary<int, long> m_pickableChunkOf = new Dictionary<int, long>();
        private readonly Dictionary<int, long> m_projectileChunkOf = new Dictionary<int, long>();

        private readonly Dictionary<int, MaskedPickable> m_pendingPickables =
            new Dictionary<int, MaskedPickable>();
        private readonly Dictionary<int, MaskedProjectile> m_pendingProjectiles =
            new Dictionary<int, MaskedProjectile>();

        private readonly Dictionary<int, double> m_pendingSnapshots = new Dictionary<int, double>();
        private readonly Dictionary<int, PickupHistory> m_pickupHistory =
            new Dictionary<int, PickupHistory>();
        private readonly List<PickableStateData> m_capturePickables = new List<PickableStateData>();
        private readonly List<ProjectileStateData> m_captureProjectiles =
            new List<ProjectileStateData>();
        private readonly HashSet<int> m_capturePickableIds = new HashSet<int>();
        private readonly HashSet<int> m_captureProjectileIds = new HashSet<int>();
        private readonly List<int> m_removalScratch = new List<int>();
        private readonly List<long> m_removalScratch64 = new List<long>();
        private readonly List<int> m_batchPickableIds = new List<int>();
        private readonly List<int> m_batchProjectileIds = new List<int>();

        // ---- 客户端：副本集合 / 请求
        private readonly HashSet<int> m_clientPickableIds = new HashSet<int>();
        private readonly HashSet<int> m_clientProjectileIds = new HashSet<int>();
        private readonly Dictionary<int, PendingPickup> m_pendingPickups =
            new Dictionary<int, PendingPickup>();
        private readonly Dictionary<int, double> m_pickupCooldown = new Dictionary<int, double>();
        /// <summary>
        /// 客户端掉落者宽限：pickableId → 宽限截止（模块时钟）。来源：
        ///   ① 本机丢弃回执带 pickable_id；
        ///   ② 本机挖 / 放造成的掉落被认领（<see cref="IEntityReplicaPort.TryClaimOwnDropGrace"/>）；
        ///   ③ 主机以 pickup-grace 拒绝后补登记（回执明确原因，客户端不再连续重试）。
        /// </summary>
        private readonly Dictionary<int, double> m_ownPickupGrace = new Dictionary<int, double>();

        // ---- 客户端：丢弃 / 投掷 / 开火请求（在途 + 主机回执历史）
        private readonly Dictionary<long, PendingDropRequest> m_pendingDrops =
            new Dictionary<long, PendingDropRequest>();
        private readonly Dictionary<long, PendingFireRequest> m_pendingFires =
            new Dictionary<long, PendingFireRequest>();
        private readonly Dictionary<int, DropHistory> m_dropHistory =
            new Dictionary<int, DropHistory>();
        private readonly Dictionary<int, FireHistory> m_fireHistory =
            new Dictionary<int, FireHistory>();

        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IPeerBroadcastPort m_broadcast;
        /// <summary>
        /// 主机：读每个客户端的兴趣窗口（与地形完全同一口径：客户端上报的中心区块 + 半径）。
        /// 没有它就没法逐端裁剪 —— 明确记一行，不退化成广播。
        /// </summary>
        private IClientInterestWindowPort m_interest;
        /// <summary>主机：纯表现事件（开火声音等）按客户端兴趣窗口投递（一次编码、并发入队）。</summary>
        private IWindowBroadcastPort m_window;
        private IMessageRegistryPort m_messages;
        private Action<int, string, string, bool> m_clientJoinedHandler;
        private Action<int, string> m_clientLeftHandler;
        private bool m_attached;
        private bool m_hosting;
        private bool m_wasConnected;
        private bool m_roleInitialized;
        private bool m_clientReady;
        private bool m_snapshotCaptureOk;

        private double m_lastHostPulse;
        private long m_serverTickMs;
        private uint m_stateVersion;
        private uint m_snapshotId;
        private long m_nextPickupSequence;
        private long m_nextDropSequence;
        private long m_nextFireSequence;
        private uint m_fireSoundEventId;
        private uint m_lastFireSoundEventId;
        private uint m_pickupSoundEventId;
        private int m_inventoryVersion;
        /// <summary>客户端：主机回执已物化、等待 EntityState 确认的丢弃副本（id → 超时时刻）。</summary>
        private readonly Dictionary<int, double> m_pendingDropReplicas =
            new Dictionary<int, double>();
        private double m_lastEntityMessageMs;
        private double m_lastResyncRequestMs;
        /// <summary>最近一次 tick 的时间（模块内所有超时 / 冷却都用它，不读墙钟）。</summary>
        private double m_now;
        private long m_lastSummaryMs;

        private sealed class PickupHistory
        {
            public long LastSequence;
            public bool HasResult;
            public PickableAcquireResultData LastResult;
        }

        /// <summary>
        /// 一条待发拾取物记录 + 三种动作各自的用户位图（mask 位 = 稳定用户槽位）。
        /// 同一实体可能"对知道它的端发 UPDATE、对刚看到它的端发 ADD、对窗口移出去的端发 REMOVE"，
        /// 所以不能用单一动作字段；三个位图可以并存，发送时按 REMOVE &gt; ADD &gt; UPDATE 的顺序清位。
        /// </summary>
        private struct MaskedPickable
        {
            public PickableStateData State;
            public ulong AddMask;
            public ulong UpdateMask;
            public ulong RemoveMask;
        }

        /// <summary>一条待发投射物记录 + 三种动作各自的用户位图（同 <see cref="MaskedPickable"/>）。</summary>
        private struct MaskedProjectile
        {
            public ProjectileStateData State;
            public ulong AddMask;
            public ulong UpdateMask;
            public ulong RemoveMask;
        }

        /// <summary>
        /// 一个稳定用户（slot 下标）的兴趣窗口缓存。窗口口径与地形完全一致：
        /// 中心区块 + 半径（切比雪夫方形），来自该端 <c>InterestUpdate</c> 的上报值。
        /// </summary>
        private sealed class UserWindow
        {
            public string UserKey = string.Empty;
            public int ClientId = -1;
            /// <summary>上一次刷新时绑定的连接（换了连接 = 新的这次连接，客户端手上没有副本）。</summary>
            public int LastClientId = -1;
            public bool Valid;
            public int CenterX;
            public int CenterZ;
            public int Radius;
        }

        private sealed class DropHistory
        {
            public long LastSequence;
            public bool HasResult;
            public PickableDropResultData LastResult;
        }

        private sealed class FireHistory
        {
            public long LastSequence;
            public bool HasResult;
            public ProjectileFireResultData LastResult;
        }

        private readonly struct PendingDropRequest
        {
            public PendingDropRequest(in PickableDropRequestData request, double sentMs)
            {
                Request = request;
                SentMs = sentMs;
            }

            public PickableDropRequestData Request { get; }
            public double SentMs { get; }
        }

        private readonly struct PendingFireRequest
        {
            public PendingFireRequest(in ProjectileFireRequestData request, double sentMs)
            {
                Request = request;
                SentMs = sentMs;
            }

            public ProjectileFireRequestData Request { get; }
            public double SentMs { get; }
        }

        private readonly struct PendingPickup
        {
            public PendingPickup(long sequence, double sentMs)
            {
                Sequence = sequence;
                SentMs = sentMs;
            }

            public long Sequence { get; }
            public double SentMs { get; }
        }

        public EntityReplicationModule(IEntityReplicaPort port, IPlayerActionPort actionPort,
            IPlayerInventoryStore inventoryStore)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_actionPort = actionPort ?? throw new ArgumentNullException(nameof(actionPort));
            m_inventoryStore = inventoryStore ??
                throw new ArgumentNullException(nameof(inventoryStore));
            m_descriptor = new ModuleDescriptor("entity-replication",
                "拾取物 / 投射物复制（主机权威）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "entity.replication" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        /// <summary>自检 / 控制台可读的计数器快照（实例对象，非静态）。</summary>
        public EntityReplicationCounters Counters => m_counters;

        public string StatusLine() => "实体复制：" +
            (m_hosting ? "主机权威" : "客户端副本") +
            " · 逐用户兴趣：deliveries=" + m_counters.EntityInterestDeliveries +
            " seedAdds=" + m_counters.EntitySeedAdds +
            " seedRemoves=" + m_counters.EntitySeedRemoves +
            " outOfWindow=" + m_counters.EntityOutOfWindow +
            " windowUnknown=" + m_counters.EntityWindowUnknown +
            " slotOverflow=" + m_counters.EntityUserSlotOverflow +
            " maskedSendFail=" + m_counters.EntityMaskedSendFailures +
            " pickableAdd=" + m_counters.PickableAdd +
            " pickableUpdate=" + m_counters.PickableUpdate +
            " pickableRemove=" + m_counters.PickableRemove +
            " pickableRejected=" + m_counters.PickableRejected +
            " pickableDropped=" + m_counters.PickableDropped +
            " · projectileAdd=" + m_counters.ProjectileAdd +
            " projectileUpdate=" + m_counters.ProjectileUpdate +
            " projectileRemove=" + m_counters.ProjectileRemove +
            " projectileRejected=" + m_counters.ProjectileRejected +
            " projectileDropped=" + m_counters.ProjectileDropped +
            " · stateMsg=" + m_counters.StateMessagesSent +
            " stateFail=" + m_counters.StateSendFailures +
            " stateDropped=" + m_counters.StateDropped +
            " stateRecv=" + m_counters.StateReceived +
            " snapshotSent=" + m_counters.SnapshotsSent +
            " snapshotRecv=" + m_counters.SnapshotsReceived +
            " snapshotStale=" + m_counters.SnapshotStale +
            " · pickupReq/acc/rej=" + m_counters.PickupRequestsSent + "/" +
            m_counters.PickupAccepted + "/" + m_counters.PickupRejected +
            " persisted=" + m_counters.PickupPersisted +
            " persistFail=" + m_counters.PickupPersistFailures +
            " pickupGraceSuppressed=" + m_counters.PickupGraceSuppressed +
            " pickupGraceBlockedByHost=" + m_counters.PickupGraceBlockedByHost +
            " ownDropGrace=" + m_counters.OwnDropGraceRegistered +
            " resync=" + m_counters.ResyncRequestsSent +
            " · dropReq/acc/rej/timeout=" + m_counters.DropRequestsSent + "/" +
            m_counters.DropAccepted + "/" + m_counters.DropRejected + "/" +
            m_counters.DropTimeouts +
            " dropRej[" + m_counters.DropRejectReasons.Describe() + "]" +
            " · uiDropIntercepted=" + m_counters.UiDropIntercepted +
            " uiDropRequestSent=" + m_counters.UiDropRequestSent +
            " uiDropAccepted=" + m_counters.UiDropAccepted +
            " uiDropRejected=" + m_counters.UiDropRejected +
            " uiDropTimeouts=" + m_counters.UiDropTimeouts +
            " uiDropSuppressed=" + m_counters.UiDropSuppressed +
            " uiDropRecv=" + m_counters.UiDropRequestsReceived +
            " uiDropRej[" + m_counters.UiDropRejectReasons.Describe() + "]" +
            " · fireReq/acc/rej/timeout=" + m_counters.FireRequestsSent + "/" +
            m_counters.FireAccepted + "/" + m_counters.FireRejected + "/" +
            m_counters.FireTimeouts +
            " fireRej[" + m_counters.FireRejectReasons.Describe() + "]" +
            " fireSpawned=" + m_counters.FireProjectilesSpawned +
            " blankShots=" + m_counters.FireBlankShots +
            " fireSpawnAtHost=" + m_port.FireSpawnAtHost +
            " fireSpawnCollided=" + m_port.FireSpawnCollided +
            " fireResetSent=" + m_counters.FireResetSent +
            " fireResetApplied=" + m_counters.FireResetApplied +
            " fireResetFail=" + m_counters.FireResetFailures +
            " fireSoundRelayed=" + m_counters.FireSoundRelayed +
            " fireSoundRecv=" + m_counters.FireSoundReceived +
            " fireSoundPresented=" + m_counters.FireSoundPresented +
            " fireSoundEchoSkipped=" + m_counters.FireSoundOwnEchoSkipped +
            " fireSoundFail=" + m_counters.FireSoundFailures +
            " pickupSoundRelayed=" + m_counters.PickupSoundRelayed +
            " pickupSoundRecv=" + m_counters.PickupSoundReceived +
            " pickupSoundPresented=" + m_counters.PickupSoundPresented +
            " pickupSoundEchoSkipped=" + m_counters.PickupSoundEchoSkipped +
            " pickupSoundSkippedDistance=" + m_counters.PickupSoundSkippedDistance +
            " pickupSoundMalformed=" + m_counters.PickupSoundMalformed +
            " dropReplicaMaterialised=" + m_counters.DropReplicaMaterialised +
            " dropReplicaConfirmed=" + m_counters.DropReplicaConfirmed +
            " dropReplicaTimeouts=" + m_counters.DropReplicaTimeouts +
            " dropReplicaFail=" + m_counters.DropReplicaFailures +
            " invDeltaApplied=" + m_counters.InventoryDeltaApplied +
            " invDeltaFail=" + m_counters.InventoryDeltaFailures +
            " foreignCulled=" + (m_port.ForeignPickablesRemoved +
                m_port.ForeignProjectilesRemoved) +
            " · payload=" + m_pendingPickables.Count + "p/" + m_pendingProjectiles.Count +
            "j · " + m_port.Describe();

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 拾取物 / 投射物复制不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "实体复制不启用（不猜方向）");
                return;
            }
            if (!context.TryGetPort(out m_broadcast) || m_broadcast == null)
            {
                Log("警告：没有拿到广播端口（IPeerBroadcastPort）→ 主机无法把实体状态广播给所有客户端");
            }
            if (!context.TryGetPort(out m_interest) || m_interest == null)
            {
                Log("警告：没有拿到兴趣窗口端口（IClientInterestWindowPort）→ 主机无法逐端裁剪实体同步" +
                    "（本端不会退化成广播：没有窗口的客户端将收不到实体状态）");
            }
            if (!context.TryGetPort(out m_window) || m_window == null)
            {
                Log("警告：没有拿到窗口投递端口（IWindowBroadcastPort）→ 开火声音等纯表现事件" +
                    "无法按客户端兴趣窗口中继（明确计数，绝不退回发给所有人）");
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到实体状态 / 拾取请求");
            }
            m_port.ClientPickupSubmitted += OnClientPickupSubmitted;
            // 客户端丢弃 / 投掷 / 开火：引擎适配层采集到意图后调用本模块（IEntityRequestSink）。
            // 主机 / 单机 / 模块未上线时适配层不会调用（sink=null 会明确计失败，不本地扣物品）。
            m_port.RequestSink = this;
            m_clientJoinedHandler = HandleClientJoined;
            m_clientLeftHandler = HandleClientLeft;
            m_transport.ClientJoined += m_clientJoinedHandler;
            m_transport.ClientLeft += m_clientLeftHandler;
            m_attached = true;
            Log("实体复制模块就绪：主机 = 引擎权威（32Hz 变化驱动 + 合并 + 有界队列）；" +
                "客户端 = 只建副本、拾取走请求（不本地结算）");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.EntityState, PE.EntityState.Parser,
                new DelegateMessageHandler<PE.EntityState>(OnEntityState), ModuleId);
            registry.Register((int)MessageType.EntitySnapshot, PE.EntitySnapshot.Parser,
                new DelegateMessageHandler<PE.EntitySnapshot>(OnEntitySnapshot), ModuleId);
            registry.Register((int)MessageType.PickableAcquireRequest,
                PE.PickableAcquireRequest.Parser,
                new DelegateMessageHandler<PE.PickableAcquireRequest>(OnPickableAcquireRequest),
                ModuleId);
            registry.Register((int)MessageType.PickableAcquireResult,
                PE.PickableAcquireResult.Parser,
                new DelegateMessageHandler<PE.PickableAcquireResult>(OnPickableAcquireResult),
                ModuleId);
            registry.Register((int)MessageType.EntityResyncRequest, PE.EntityResyncRequest.Parser,
                new DelegateMessageHandler<PE.EntityResyncRequest>(OnEntityResyncRequest), ModuleId);
            registry.Register((int)MessageType.PickableDropRequest,
                PE.PickableDropRequest.Parser,
                new DelegateMessageHandler<PE.PickableDropRequest>(OnPickableDropRequest), ModuleId);
            registry.Register((int)MessageType.PickableDropResult, PE.PickableDropResult.Parser,
                new DelegateMessageHandler<PE.PickableDropResult>(OnPickableDropResult), ModuleId);
            registry.Register((int)MessageType.ProjectileFireRequest,
                PE.ProjectileFireRequest.Parser,
                new DelegateMessageHandler<PE.ProjectileFireRequest>(OnProjectileFireRequest),
                ModuleId);
            registry.Register((int)MessageType.ProjectileFireResult,
                PE.ProjectileFireResult.Parser,
                new DelegateMessageHandler<PE.ProjectileFireResult>(OnProjectileFireResult),
                ModuleId);
            registry.Register((int)MessageType.WeaponFireEvent,
                PE.WeaponFireEvent.Parser,
                new DelegateMessageHandler<PE.WeaponFireEvent>(OnWeaponFireEvent), ModuleId);
            registry.Register((int)MessageType.PickupSoundEvent,
                PE.PickupSoundEvent.Parser,
                new DelegateMessageHandler<PE.PickupSoundEvent>(OnPickupSoundEvent), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = StatusLine();
            m_port.ClientPickupSubmitted -= OnClientPickupSubmitted;
            if (ReferenceEquals(m_port.RequestSink, this)) m_port.RequestSink = null;
            if (m_transport != null)
            {
                if (m_clientJoinedHandler != null)
                    m_transport.ClientJoined -= m_clientJoinedHandler;
                if (m_clientLeftHandler != null)
                    m_transport.ClientLeft -= m_clientLeftHandler;
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            try { m_port.ResetClientReplicas("detach"); } catch (Exception) { }
            m_lastPickables.Clear();
            m_lastProjectiles.Clear();
            m_pendingPickables.Clear();
            m_pendingProjectiles.Clear();
            ResetHostInterestState();
            m_pendingSnapshots.Clear();
            m_pickupHistory.Clear();
            m_capturePickables.Clear();
            m_captureProjectiles.Clear();
            m_clientPickableIds.Clear();
            m_clientProjectileIds.Clear();
            m_pendingPickups.Clear();
            m_pickupCooldown.Clear();
            m_ownPickupGrace.Clear();
            m_pendingDropReplicas.Clear();
            m_fireSoundEventId = 0;
            m_lastFireSoundEventId = 0;
            m_pickupSoundEventId = 0;
            m_pickupSoundOrderer.Reset();
            m_pickupSoundSendLimiter.Reset();
            m_pickupSoundRecvLimiter.Reset();
            m_orderer.Reset("detach");
            m_attached = false;
            Log("实体复制模块下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_now = tick.Now;
            m_port.Pump();
            bool hosting = m_control != null && m_control.IsHosting;
            bool connected = m_transport != null && m_transport.IsConnected;
            if (!m_roleInitialized || hosting != m_hosting || connected != m_wasConnected)
                HandleRoleOrConnectionChange(hosting, connected, tick.Now);
            if (!connected) return;
            if (m_hosting) TickHost(tick.Now);
            else TickClient(tick);
            MaybeLogSummary(tick.Now);
        }

        // ================================================================ 角色 / 连接

        private void HandleRoleOrConnectionChange(bool hosting, bool connected, double now)
        {
            bool wasConnected = m_wasConnected;
            m_hosting = hosting;
            m_wasConnected = connected;
            m_roleInitialized = true;
            if (hosting)
            {
                m_orderer.Reset("host");
                m_clientPickableIds.Clear();
                m_clientProjectileIds.Clear();
                m_pendingPickups.Clear();
                m_pickupCooldown.Clear();
                m_ownPickupGrace.Clear();
                m_pendingSnapshots.Clear();
                m_pendingDrops.Clear();
                m_pendingFires.Clear();
                m_pendingDropReplicas.Clear();
                m_dropHistory.Clear();
                m_fireHistory.Clear();
                m_nextDropSequence = 0;
                m_nextFireSequence = 0;
                m_fireSoundEventId = 0;
                m_lastFireSoundEventId = 0;
                m_pickupSoundEventId = 0;
                m_pickupSoundOrderer.Reset();
                m_pickupSoundSendLimiter.Reset();
                m_pickupSoundRecvLimiter.Reset();
                m_lastHostPulse = 0.0;
                m_serverTickMs = (long)(now * 1000.0);
                m_stateVersion = 0;
                m_snapshotId = 0;
                m_lastPickables.Clear();
                m_lastProjectiles.Clear();
                m_pendingPickables.Clear();
                m_pendingProjectiles.Clear();
                ResetHostInterestState();
                m_pickupHistory.Clear();
                Log("本端 = 主机：拾取物 / 投射物由本端引擎创建与删除，" +
                    "客户端只能请求拾取（32Hz 变化驱动广播）");
                return;
            }
            if (!connected)
            {
                bool hadReplicas = m_clientPickableIds.Count > 0 || m_clientProjectileIds.Count > 0;
                int pendingDropCount = m_pendingDrops.Count;
                int pendingFireCount = m_pendingFires.Count;
                m_clientPickableIds.Clear();
                m_clientProjectileIds.Clear();
                m_pendingPickups.Clear();
                m_pickupCooldown.Clear();
                m_ownPickupGrace.Clear();
                m_pendingDrops.Clear();
                m_pendingFires.Clear();
                m_pendingDropReplicas.Clear();
                m_dropHistory.Clear();
                m_fireHistory.Clear();
                m_clientReady = false;
                m_orderer.Reset("disconnect");
                m_pendingSnapshots.Clear();
                if (pendingDropCount > 0 || pendingFireCount > 0)
                {
                    m_counters.DropTimeouts += pendingDropCount;
                    m_counters.FireTimeouts += pendingFireCount;
                    Log("与主机的连接断开 → 在途丢弃请求 " + pendingDropCount +
                        " 条 / 开火请求 " + pendingFireCount +
                        " 条永远不会回执（计数已记入 timeout；客户端本地从未扣除，物品不丢）");
                }
                if (hadReplicas || wasConnected)
                    m_port.ResetClientReplicas("disconnect");
                if (wasConnected) Log("与主机的连接断开 → 已清空拾取物 / 投射物副本");
                return;
            }
            // 连接上了（客户端）
            m_orderer.Reset("connect");
            m_clientReady = false;
            m_lastEntityMessageMs = now;
            m_lastResyncRequestMs = 0.0;
            m_nextPickupSequence = 0;
            m_nextDropSequence = 0;
            m_nextFireSequence = 0;
            m_pendingPickups.Clear();
            m_pickupCooldown.Clear();
            m_ownPickupGrace.Clear();
            m_pendingDrops.Clear();
            m_pendingFires.Clear();
            m_pendingDropReplicas.Clear();
            m_pendingSnapshots.Clear();
            m_fireSoundEventId = 0;
            m_lastFireSoundEventId = 0;
            m_pickupSoundEventId = 0;
            m_pickupSoundOrderer.Reset();
            m_pickupSoundSendLimiter.Reset();
            m_pickupSoundRecvLimiter.Reset();
            m_lastPickables.Clear();
            m_lastProjectiles.Clear();
            m_pendingPickables.Clear();
            m_pendingProjectiles.Clear();
            ResetHostInterestState();
            m_clientPickableIds.Clear();
            m_clientProjectileIds.Clear();
            Log("本端 = 客户端副本：不创建 / 不结算拾取物与投射物；" +
                "世界就绪后请求一次完整快照");
        }

        private void HandleClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!m_hosting) return;
            m_pendingSnapshots[clientId] = NowSeconds();
            m_pickupHistory.Remove(clientId);
            m_dropHistory.Remove(clientId);
            m_fireHistory.Remove(clientId);
            Log("客户端 " + clientId + " 已加入（" + (name ?? string.Empty) + "）→ 排队完整实体快照" +
                (rejoin ? "（重连）" : string.Empty));
        }

        private void HandleClientLeft(int clientId, string reason)
        {
            m_pendingSnapshots.Remove(clientId);
            m_pickupHistory.Remove(clientId);
            m_dropHistory.Remove(clientId);
            m_fireHistory.Remove(clientId);
            DropUserSlot(clientId);
        }

        /// <summary>
        /// 这个连接走了：清掉它的"已知"位与待发位 —— 它的副本随连接一起消失，位留着只会让
        /// 每拍重试投给一个不存在的端。用户槽位保留（重连还是它），重连后由快照 + 窗口补种重建。
        /// </summary>
        private void DropUserSlot(int clientId)
        {
            int slot = -1;
            for (int i = 0; i < m_userRoster.Count; i++)
            {
                if (m_userRoster[i].ClientId != clientId) continue;
                slot = i;
                break;
            }
            if (slot < 0) return;
            ulong bit = EntityInterestSlots.Bit(slot);
            DropBitFromKnown(m_pickableKnown, bit);
            DropBitFromKnown(m_projectileKnown, bit);
            DropBitFromPending(bit);
            if (slot < m_userWindows.Count)
            {
                m_userWindows[slot].ClientId = -1;
                m_userWindows[slot].Valid = false;
            }
        }

        private void DropBitFromKnown(Dictionary<int, ulong> known, ulong bit)
        {
            m_removalScratch.Clear();
            foreach (KeyValuePair<int, ulong> item in known)
            {
                if ((item.Value & bit) != 0UL) m_removalScratch.Add(item.Key);
            }
            for (int i = 0; i < m_removalScratch.Count; i++)
            {
                int id = m_removalScratch[i];
                ulong remaining = known[id] & ~bit;
                if (remaining == 0UL) known.Remove(id);
                else known[id] = remaining;
            }
        }

        private void DropBitFromPending(ulong bit)
        {
            m_removalScratch.Clear();
            foreach (KeyValuePair<int, MaskedPickable> item in m_pendingPickables)
            {
                m_removalScratch.Add(item.Key);
            }
            for (int i = 0; i < m_removalScratch.Count; i++)
            {
                int id = m_removalScratch[i];
                MaskedPickable pending = m_pendingPickables[id];
                pending.AddMask &= ~bit;
                pending.UpdateMask &= ~bit;
                pending.RemoveMask &= ~bit;
                if (pending.AddMask == 0UL && pending.UpdateMask == 0UL && pending.RemoveMask == 0UL)
                    m_pendingPickables.Remove(id);
                else
                    m_pendingPickables[id] = pending;
            }
            m_removalScratch.Clear();
            foreach (KeyValuePair<int, MaskedProjectile> item in m_pendingProjectiles)
            {
                m_removalScratch.Add(item.Key);
            }
            for (int i = 0; i < m_removalScratch.Count; i++)
            {
                int id = m_removalScratch[i];
                MaskedProjectile pending = m_pendingProjectiles[id];
                pending.AddMask &= ~bit;
                pending.UpdateMask &= ~bit;
                pending.RemoveMask &= ~bit;
                if (pending.AddMask == 0UL && pending.UpdateMask == 0UL && pending.RemoveMask == 0UL)
                    m_pendingProjectiles.Remove(id);
                else
                    m_pendingProjectiles[id] = pending;
            }
        }

        // ================================================================ 主机

        private void TickHost(double now)
        {
            if (m_lastHostPulse == 0.0 || now - m_lastHostPulse >= PulseDuration)
            {
                m_lastHostPulse = now;
                PumpHost(now);
            }
        }

        private void PumpHost(double now)
        {
            m_serverTickMs = Math.Max(m_serverTickMs, (long)(now * 1000.0));
            // 先刷新"逐用户兴趣窗口"（mask 位序 = 稳定用户槽位）：窗口变化会在这里补种 / 补移除，
            // 然后再抓实体，保证本拍算出的位图用的是最新窗口。
            RefreshHostUsers();
            m_capturePickables.Clear();
            m_captureProjectiles.Clear();
            bool okPickables;
            bool okProjectiles;
            string pickableReason;
            string projectileReason;
            try
            {
                okPickables = m_port.TryCaptureHostPickables(m_capturePickables,
                    out pickableReason);
            }
            catch (Exception e)
            {
                okPickables = false;
                pickableReason = "host-exception-" + e.GetType().Name;
            }
            try
            {
                okProjectiles = m_port.TryCaptureHostProjectiles(m_captureProjectiles,
                    out projectileReason);
            }
            catch (Exception e)
            {
                okProjectiles = false;
                projectileReason = "host-exception-" + e.GetType().Name;
            }
            m_snapshotCaptureOk = okPickables && okProjectiles;
            if (!okPickables)
            {
                m_counters.CaptureFailures++;
                LogRateLimited("capture-pickables:" + pickableReason,
                    "主机抓取拾取物失败（" + pickableReason + "）→ 本拍不广播拾取物（不把空列表当成世界空了）");
            }
            if (!okProjectiles)
            {
                m_counters.CaptureFailures++;
                LogRateLimited("capture-projectiles:" + projectileReason,
                    "主机抓取投射物失败（" + projectileReason + "）→ 本拍不广播投射物");
            }
            if (okPickables) ApplyCapturedPickables(m_capturePickables);
            if (okProjectiles) ApplyCapturedProjectiles(m_captureProjectiles);

            bool hasPeers = m_broadcast != null && m_broadcast.PlayerPeerCount > 0;
            if (!hasPeers)
            {
                DropPendingStates("no-player-peers");
                return;
            }
            FlushSnapshots(now);
            FlushPendingStates();
        }

        private void ApplyCapturedPickables(List<PickableStateData> captured)
        {
            bool truncated = captured.Count > MaxTrackedPickables;
            int count = truncated ? MaxTrackedPickables : captured.Count;
            if (truncated)
            {
                int dropped = captured.Count - MaxTrackedPickables;
                m_counters.PickableDropped += dropped;
                LogRateLimited("tracked-pickables-cap",
                    "主机拾取物数量 " + captured.Count + " 超过跟踪上限 " + MaxTrackedPickables +
                    " → 本拍忽略 " + dropped + " 条（不静默截断，计数可审计）");
            }
            if (!truncated)
            {
                m_capturePickableIds.Clear();
                for (int i = 0; i < count; i++) m_capturePickableIds.Add(captured[i].Id);
                m_removalScratch.Clear();
                foreach (int id in m_lastPickables.Keys)
                    if (!m_capturePickableIds.Contains(id)) m_removalScratch.Add(id);
                for (int i = 0; i < m_removalScratch.Count; i++)
                    RemoveTrackedPickable(m_removalScratch[i], "captured-missing");
            }
            for (int i = 0; i < count; i++)
            {
                PickableStateData current = captured[i].WithAction(EntityActions.Add);
                bool isNew = !m_lastPickables.TryGetValue(current.Id, out PickableStateData previous);
                if (isNew || !current.EqualsState(previous)) PublishPickable(current, isNew);
                m_lastPickables[current.Id] = current;
            }
        }

        private void ApplyCapturedProjectiles(List<ProjectileStateData> captured)
        {
            bool truncated = captured.Count > MaxTrackedProjectiles;
            int count = truncated ? MaxTrackedProjectiles : captured.Count;
            if (truncated)
            {
                int dropped = captured.Count - MaxTrackedProjectiles;
                m_counters.ProjectileDropped += dropped;
                LogRateLimited("tracked-projectiles-cap",
                    "主机投射物数量 " + captured.Count + " 超过跟踪上限 " +
                    MaxTrackedProjectiles + " → 本拍忽略 " + dropped + " 条");
            }
            if (!truncated)
            {
                m_captureProjectileIds.Clear();
                for (int i = 0; i < count; i++) m_captureProjectileIds.Add(captured[i].Id);
                m_removalScratch.Clear();
                foreach (int id in m_lastProjectiles.Keys)
                    if (!m_captureProjectileIds.Contains(id)) m_removalScratch.Add(id);
                for (int i = 0; i < m_removalScratch.Count; i++)
                    RemoveTrackedProjectile(m_removalScratch[i], "captured-missing");
            }
            for (int i = 0; i < count; i++)
            {
                ProjectileStateData current = captured[i].WithAction(EntityActions.Add);
                bool isNew = !m_lastProjectiles.TryGetValue(current.Id, out ProjectileStateData previous);
                if (isNew || !current.EqualsState(previous)) PublishProjectile(current, isNew);
                m_lastProjectiles[current.Id] = current;
            }
        }

        private void FlushPendingStates()
        {
            int messages = 0;
            while (messages < MaxStateMessagesPerPulse)
            {
                // 顺序固定 Remove > Add > Update：同一拍内客户端不会看到"先加后删"的矛盾。
                if (TryFlushPickables(EntityActions.Remove, ref messages)) continue;
                if (TryFlushProjectiles(EntityActions.Remove, ref messages)) continue;
                if (TryFlushPickables(EntityActions.Add, ref messages)) continue;
                if (TryFlushProjectiles(EntityActions.Add, ref messages)) continue;
                if (TryFlushPickables(EntityActions.Update, ref messages)) continue;
                if (TryFlushProjectiles(EntityActions.Update, ref messages)) continue;
                return;
            }
        }

        /// <summary>
        /// 发一条"同一动作 + 同一收件人位图"的拾取物状态：**一次编码**，按位图并发投给对应连接
        /// （<c>BroadcastToPlayersMasked</c>），不是逐端重新编码。返回 true = 本拍发掉了一条。
        /// </summary>
        private bool TryFlushPickables(int action, ref int messages)
        {
            ulong mask = 0UL;
            foreach (KeyValuePair<int, MaskedPickable> item in m_pendingPickables)
            {
                ulong candidate = PickableMaskOf(item.Value, action);
                if (candidate == 0UL) continue;
                mask = candidate;
                break;
            }
            if (mask == 0UL) return false;
            var state = new EntityStateData(NextStateVersion(), m_serverTickMs);
            m_batchPickableIds.Clear();
            foreach (KeyValuePair<int, MaskedPickable> item in m_pendingPickables)
            {
                if (state.Pickables.Count >= MaxPickableRecordsPerMessage) break;
                if (PickableMaskOf(item.Value, action) != mask) continue;
                state.Pickables.Add(item.Value.State.WithAction(action));
                m_batchPickableIds.Add(item.Key);
            }
            if (state.Pickables.Count == 0) return false;
            return SendMaskedState(state, mask, false, action, ref messages);
        }

        /// <summary>投射物版本（同 <see cref="TryFlushPickables"/>）。</summary>
        private bool TryFlushProjectiles(int action, ref int messages)
        {
            ulong mask = 0UL;
            foreach (KeyValuePair<int, MaskedProjectile> item in m_pendingProjectiles)
            {
                ulong candidate = ProjectileMaskOf(item.Value, action);
                if (candidate == 0UL) continue;
                mask = candidate;
                break;
            }
            if (mask == 0UL) return false;
            var state = new EntityStateData(NextStateVersion(), m_serverTickMs);
            m_batchProjectileIds.Clear();
            foreach (KeyValuePair<int, MaskedProjectile> item in m_pendingProjectiles)
            {
                if (state.Projectiles.Count >= MaxProjectileRecordsPerMessage) break;
                if (ProjectileMaskOf(item.Value, action) != mask) continue;
                state.Projectiles.Add(item.Value.State.WithAction(action));
                m_batchProjectileIds.Add(item.Key);
            }
            if (state.Projectiles.Count == 0) return false;
            return SendMaskedState(state, mask, true, action, ref messages);
        }

        private static ulong PickableMaskOf(in MaskedPickable pending, int action)
        {
            switch (action)
            {
                case EntityActions.Add: return pending.AddMask;
                case EntityActions.Update: return pending.UpdateMask;
                case EntityActions.Remove: return pending.RemoveMask;
                default: return 0UL;
            }
        }

        private static ulong ProjectileMaskOf(in MaskedProjectile pending, int action)
        {
            switch (action)
            {
                case EntityActions.Add: return pending.AddMask;
                case EntityActions.Update: return pending.UpdateMask;
                case EntityActions.Remove: return pending.RemoveMask;
                default: return 0UL;
            }
        }

        /// <summary>
        /// 编码一次 + 按 mask 投递；把**实际入队**的位从待发记录里清掉，没入队的位留到下一拍重试。
        /// </summary>
        private bool SendMaskedState(EntityStateData state, ulong mask, bool projectiles, int action,
            ref int messages)
        {
            byte[] payload = EntityCodec.EncodeStateBytes(state);
            int sent = m_broadcast.BroadcastToPlayersMasked(MessageType.EntityState, payload,
                m_userRoster, mask, out ulong delivered);
            int records = state.Pickables.Count + state.Projectiles.Count;
            if (sent <= 0 || delivered == 0UL)
            {
                m_counters.EntityMaskedSendFailures++;
                m_counters.StateSendFailures++;
                LogRateLimited("state-send-masked",
                    "EntityState（掩码 " + EntityInterestSlots.Describe(mask) + "，动作 " +
                    EntityActions.Name(action) + "，" + records +
                    " 条）没有任何端入队（背压 / 已断开）→ 全部保留，下一拍重试");
                return false;
            }
            m_counters.StateMessagesSent++;
            m_counters.StateRecordsSent += records;
            m_counters.EntityInterestDeliveries += (long)CountBits(delivered) * records;
            if (state.Pickables.Count > 0) CountSentPickables(state.Pickables);
            if (state.Projectiles.Count > 0) CountSentProjectiles(state.Projectiles);
            if (projectiles)
            {
                for (int i = 0; i < m_batchProjectileIds.Count; i++)
                {
                    int id = m_batchProjectileIds[i];
                    if (!m_pendingProjectiles.TryGetValue(id, out MaskedProjectile pending)) continue;
                    pending.AddMask &= ~delivered;
                    pending.UpdateMask &= ~delivered;
                    pending.RemoveMask &= ~delivered;
                    if (pending.AddMask == 0UL && pending.UpdateMask == 0UL && pending.RemoveMask == 0UL)
                        m_pendingProjectiles.Remove(id);
                    else
                        m_pendingProjectiles[id] = pending;
                }
            }
            else
            {
                for (int i = 0; i < m_batchPickableIds.Count; i++)
                {
                    int id = m_batchPickableIds[i];
                    if (!m_pendingPickables.TryGetValue(id, out MaskedPickable pending)) continue;
                    pending.AddMask &= ~delivered;
                    pending.UpdateMask &= ~delivered;
                    pending.RemoveMask &= ~delivered;
                    if (pending.AddMask == 0UL && pending.UpdateMask == 0UL && pending.RemoveMask == 0UL)
                        m_pendingPickables.Remove(id);
                    else
                        m_pendingPickables[id] = pending;
                }
            }
            messages++;
            return true;
        }

        private static int CountBits(ulong value)
        {
            int count = 0;
            while (value != 0UL)
            {
                value &= value - 1UL;
                count++;
            }
            return count;
        }


        private void FlushSnapshots(double now)
        {
            if (m_pendingSnapshots.Count == 0) return;
            if (!m_snapshotCaptureOk)
            {
                // 有一类实体没抓到：快照会缺项，complete 绝不能为 true。等下一拍。
                return;
            }
            List<int> expired = null;
            foreach (KeyValuePair<int, double> item in m_pendingSnapshots)
            {
                if (now - item.Value <= SnapshotQueueTimeoutSeconds) continue;
                // 还在下世界 / 没上报兴趣窗口的端不算"卡住"：下载世界本来就可能几分钟。
                // 这类排队一直留到它上报窗口（那时马上发），只有真发不出去的才算超时。
                if (m_interest == null ||
                    !m_interest.TryGetClientRange(item.Key, out _, out _, out _)) continue;
                if (expired == null) expired = new List<int>();
                expired.Add(item.Key);
            }
            if (expired != null)
            {
                for (int i = 0; i < expired.Count; i++)
                {
                    m_pendingSnapshots.Remove(expired[i]);
                    m_counters.SnapshotSendFailures++;
                    LogRateLimited("snapshot-expired:" + expired[i],
                        "客户端 " + expired[i] + " 的实体快照排队超过 " +
                        SnapshotQueueTimeoutSeconds + "s 仍未发出 → 丢弃排队（它可以用 " +
                        "EntityResyncRequest 重新请求）");
                }
            }
            if (m_pendingSnapshots.Count == 0) return;
            m_removalScratch.Clear();
            foreach (KeyValuePair<int, double> item in m_pendingSnapshots)
                m_removalScratch.Add(item.Key);
            for (int i = 0; i < m_removalScratch.Count; i++)
            {
                int clientId = m_removalScratch[i];
                // 快照内容按**该端自己的窗口**截取：窗口外的实体它根本不加载，没有理由下载。
                if (m_interest == null ||
                    !m_interest.TryGetClientRange(clientId, out int centerX, out int centerZ,
                        out int radius))
                {
                    m_counters.EntityWindowUnknown++;
                    continue;   // 保留排队：等它上报兴趣窗口后再发（绝不发全世界）
                }
                EntitySnapshotData snapshot = BuildSnapshotForWindow(centerX, centerZ, radius);
                byte[] payload = EntityCodec.EncodeSnapshot(snapshot).ToByteArray();
                if (!m_transport.TrySendTo(clientId, MessageType.EntitySnapshot, payload))
                {
                    m_counters.SnapshotSendFailures++;
                    LogRateLimited("snapshot-send:" + clientId,
                        "给客户端 " + clientId + " 发实体快照失败（通道背压/已断开）→ 保留下次重试");
                    continue;
                }
                m_pendingSnapshots.Remove(clientId);
                m_counters.SnapshotsSent++;
                m_counters.SnapshotRecords += snapshot.Pickables.Count + snapshot.Projectiles.Count;
                MarkSnapshotKnown(clientId, snapshot);
            }
        }

        /// <summary>快照里已经给过这个端的东西 = 它现在知道 → 之后的变化只发 UPDATE / REMOVE。</summary>
        private void MarkSnapshotKnown(int clientId, EntitySnapshotData snapshot)
        {
            int slot = -1;
            for (int i = 0; i < m_userRoster.Count; i++)
            {
                if (m_userRoster[i].ClientId != clientId) continue;
                slot = i;
                break;
            }
            if (slot < 0) return;
            ulong bit = EntityInterestSlots.Bit(slot);
            for (int i = 0; i < snapshot.Pickables.Count; i++)
            {
                int id = snapshot.Pickables[i].Id;
                m_pickableKnown.TryGetValue(id, out ulong known);
                m_pickableKnown[id] = known | bit;
            }
            for (int i = 0; i < snapshot.Projectiles.Count; i++)
            {
                int id = snapshot.Projectiles[i].Id;
                m_projectileKnown.TryGetValue(id, out ulong known);
                m_projectileKnown[id] = known | bit;
            }
        }

        /// <summary>
        /// 给**某一个端**的实体快照：内容只取该端兴趣窗口内的实体（客户端只加载自己窗口内的世界）。
        /// 窗口内超过上限才截断，并置 complete=false（客户端据此不删除未列出的旧副本）。
        /// </summary>
        private EntitySnapshotData BuildSnapshotForWindow(int centerChunkX, int centerChunkZ,
            int radiusChunks)
        {
            int pickableCount = 0;
            int projectileCount = 0;
            bool truncated = false;
            int outsideWindow = 0;
            var pickables = new List<PickableStateData>(Math.Min(m_capturePickables.Count, 64));
            for (int i = 0; i < m_capturePickables.Count; i++)
            {
                PickableStateData state = m_capturePickables[i];
                if (!ProjectileHitViewPolicy.IsCellInInterestWindow(state.Position, centerChunkX,
                        centerChunkZ, radiusChunks))
                {
                    outsideWindow++;
                    continue;
                }
                if (pickableCount >= MaxSnapshotPickables)
                {
                    truncated = true;
                    break;
                }
                pickables.Add(state.WithAction(EntityActions.Add).WithCollector(-1));
                pickableCount++;
            }
            var projectiles = new List<ProjectileStateData>(
                Math.Min(m_captureProjectiles.Count, 32));
            for (int i = 0; i < m_captureProjectiles.Count; i++)
            {
                ProjectileStateData state = m_captureProjectiles[i];
                if (!ProjectileHitViewPolicy.IsCellInInterestWindow(state.Position, centerChunkX,
                        centerChunkZ, radiusChunks))
                {
                    outsideWindow++;
                    continue;
                }
                if (projectileCount >= MaxSnapshotProjectiles)
                {
                    truncated = true;
                    break;
                }
                projectiles.Add(state.WithAction(EntityActions.Add));
                projectileCount++;
            }
            if (outsideWindow > 0) m_counters.EntityOutOfWindow += outsideWindow;
            var snapshot = new EntitySnapshotData(NextSnapshotId(), m_serverTickMs, !truncated,
                m_stateVersion);
            for (int i = 0; i < pickables.Count; i++) snapshot.Pickables.Add(pickables[i]);
            for (int i = 0; i < projectiles.Count; i++) snapshot.Projectiles.Add(projectiles[i]);
            if (truncated)
            {
                m_counters.SnapshotDropped += pickableCount + projectileCount;
                LogRateLimited("snapshot-truncated",
                    "该端窗口内的实体超过快照上限（拾取物 " + MaxSnapshotPickables +
                    "，投射物 " + MaxSnapshotProjectiles +
                    "）→ 截断并置 complete=false（其余走增量）");
            }
            return snapshot;
        }

        // ------------------------------------------------------------ 主机：逐用户兴趣（窗口订阅）

        /// <summary>
        /// 每拍刷新"稳定用户 → 槽位 → 当前连接 + 兴趣窗口"。窗口变化时立即补种 / 补移除：
        /// 客户端只订阅自己窗口内的实体，主机不为窗口外的端生成投递（主机自身仍模拟全部实体）。
        /// </summary>
        private void RefreshHostUsers()
        {
            for (int i = 0; i < m_userWindows.Count; i++) m_userWindows[i].ClientId = -1;
            m_userCapture.Clear();
            if (m_interest == null || m_broadcast == null)
            {
                m_userRoster.Clear();
                return;
            }
            m_broadcast.CapturePlayerUsers(m_userCapture);
            for (int i = 0; i < m_userCapture.Count; i++)
            {
                PlayerPeerUser peer = m_userCapture[i];
                int slot = m_userSlots.GetOrAlloc(peer.UserKey);
                if (slot < 0)
                {
                    m_counters.EntityUserSlotOverflow++;
                    LogRateLimited("entity-interest-slot-overflow",
                        "稳定用户槽位用满 " + EntityInterestSlots.MaxUsers + " 个 → clientId=" +
                        peer.ClientId + " 本拍不投递实体（明确失败，绝不投给别的用户）");
                    continue;
                }
                UserWindow window = EnsureUserWindow(slot, peer.UserKey);
                window.ClientId = peer.ClientId;
                if (window.LastClientId >= 0 && window.LastClientId != peer.ClientId)
                {
                    // 同一个人换了连接（重连）：上一轮的副本已经随旧连接消失 → 清已知位，按第一次整窗补种。
                    ulong resetBit = EntityInterestSlots.Bit(slot);
                    DropBitFromKnown(m_pickableKnown, resetBit);
                    DropBitFromKnown(m_projectileKnown, resetBit);
                    DropBitFromPending(resetBit);
                    window.Valid = false;
                }
                window.LastClientId = peer.ClientId;
                if (!m_interest.TryGetClientRange(peer.ClientId, out int cx, out int cz, out int radius))
                {
                    // 没有兴趣窗口（Hello 刚完成 / 还没进世界）：本拍不投递；下次上报按"第一次"整窗补种。
                    window.Valid = false;
                    m_counters.EntityWindowUnknown++;
                    continue;
                }
                UpdateUserWindow(window, cx, cz, radius);
            }
            RebuildRoster();
        }

        private UserWindow EnsureUserWindow(int slot, string userKey)
        {
            while (m_userWindows.Count <= slot) m_userWindows.Add(new UserWindow());
            UserWindow window = m_userWindows[slot];
            if (window.UserKey.Length == 0) window.UserKey = userKey;
            return window;
        }

        /// <summary>mask 位序 = 槽位：roster 下标必须与槽位一一对应（离线用户留 -1 占位，绝不压缩下标）。</summary>
        private void RebuildRoster()
        {
            m_userRoster.Clear();
            for (int slot = 0; slot < m_userWindows.Count; slot++)
            {
                UserWindow window = m_userWindows[slot];
                m_userRoster.Add(new PlayerPeerUser(window.ClientId, window.UserKey));
            }
        }

        private void UpdateUserWindow(UserWindow window, int centerX, int centerZ, int radius)
        {
            if (window.Valid && window.CenterX == centerX && window.CenterZ == centerZ &&
                window.Radius == radius) return;   // 窗口没变：不重算差集（每拍都会走到这里）
            int slot = m_userSlots.IndexOf(window.UserKey);
            if (slot < 0) return;
            EntityInterestWindowDiff.Compute(window.Valid, window.CenterX, window.CenterZ,
                window.Radius, centerX, centerZ, radius, m_windowEntered, m_windowLeft);
            window.Valid = true;
            window.CenterX = centerX;
            window.CenterZ = centerZ;
            window.Radius = radius;
            ulong bit = EntityInterestSlots.Bit(slot);
            if (m_windowLeft.Count > 0) DropChunksFromUser(bit, m_windowLeft);
            if (m_windowEntered.Count > 0) SeedChunksForUser(bit, m_windowEntered);
        }

        /// <summary>该用户新进入这些区块：把区块内现有实体补一条 ADD（他只见过窗口内的东西）。</summary>
        private void SeedChunksForUser(ulong bit, List<long> chunks)
        {
            for (int c = 0; c < chunks.Count; c++)
            {
                long chunkKey = chunks[c];
                if (m_pickableChunks.TryGetValue(chunkKey, out List<int> pickables))
                {
                    for (int i = 0; i < pickables.Count; i++)
                    {
                        int id = pickables[i];
                        m_pickableKnown.TryGetValue(id, out ulong known);
                        if ((known & bit) != 0UL) continue;
                        if (!m_lastPickables.TryGetValue(id, out PickableStateData state)) continue;
                        EnqueuePickable(state, EntityActions.Add, bit);
                        m_pickableKnown[id] = known | bit;
                        m_counters.EntitySeedAdds++;
                    }
                }
                if (m_projectileChunks.TryGetValue(chunkKey, out List<int> projectiles))
                {
                    for (int i = 0; i < projectiles.Count; i++)
                    {
                        int id = projectiles[i];
                        m_projectileKnown.TryGetValue(id, out ulong known);
                        if ((known & bit) != 0UL) continue;
                        if (!m_lastProjectiles.TryGetValue(id, out ProjectileStateData state)) continue;
                        EnqueueProjectile(state, EntityActions.Add, bit);
                        m_projectileKnown[id] = known | bit;
                        m_counters.EntitySeedAdds++;
                    }
                }
            }
        }

        /// <summary>该用户离开这些区块：他手上该区块的副本补一条 REMOVE。</summary>
        private void DropChunksFromUser(ulong bit, List<long> chunks)
        {
            for (int c = 0; c < chunks.Count; c++)
            {
                long chunkKey = chunks[c];
                if (m_pickableChunks.TryGetValue(chunkKey, out List<int> pickables))
                {
                    for (int i = 0; i < pickables.Count; i++)
                    {
                        int id = pickables[i];
                        m_pickableKnown.TryGetValue(id, out ulong known);
                        if ((known & bit) == 0UL) continue;
                        if (!m_lastPickables.TryGetValue(id, out PickableStateData state)) continue;
                        EnqueuePickable(state, EntityActions.Remove, bit);
                        m_pickableKnown[id] = known & ~bit;
                        m_counters.EntitySeedRemoves++;
                    }
                }
                if (m_projectileChunks.TryGetValue(chunkKey, out List<int> projectiles))
                {
                    for (int i = 0; i < projectiles.Count; i++)
                    {
                        int id = projectiles[i];
                        m_projectileKnown.TryGetValue(id, out ulong known);
                        if ((known & bit) == 0UL) continue;
                        if (!m_lastProjectiles.TryGetValue(id, out ProjectileStateData state)) continue;
                        EnqueueProjectile(state, EntityActions.Remove, bit);
                        m_projectileKnown[id] = known & ~bit;
                        m_counters.EntitySeedRemoves++;
                    }
                }
            }
        }

        /// <summary>该坐标落在哪些在线用户的窗口里（mask）。没有任何窗口命中 = 0（主机不投递）。</summary>
        private ulong ComputeWindowMask(Vector3 position, out long chunkKey)
        {
            int chunkX = EntityInterestWindowDiff.ChunkOf(position.X);
            int chunkZ = EntityInterestWindowDiff.ChunkOf(position.Z);
            chunkKey = EntityInterestWindowDiff.Key(chunkX, chunkZ);
            ulong mask = 0UL;
            for (int slot = 0; slot < m_userWindows.Count; slot++)
            {
                UserWindow window = m_userWindows[slot];
                if (!window.Valid || window.ClientId < 0) continue;
                if (!EntityInterestWindowDiff.InWindow(chunkX, chunkZ, window.CenterX,
                        window.CenterZ, window.Radius)) continue;
                mask |= EntityInterestSlots.Bit(slot);
            }
            return mask;
        }

        /// <summary>把"当前状态"翻译成逐用户投递（新增 / 变化 / 跨窗口移除），并维护区块索引。</summary>
        private void PublishPickable(PickableStateData state, bool isNew)
        {
            ulong inWindow = ComputeWindowMask(state.Position, out long chunkKey);
            m_pickableKnown.TryGetValue(state.Id, out ulong known);
            if (isNew)
            {
                if (inWindow != 0UL)
                {
                    EnqueuePickable(state, EntityActions.Add, inWindow);
                    known |= inWindow;
                }
                m_pickableKnown[state.Id] = known;
                IndexPickableChunk(state.Id, chunkKey);
                return;
            }
            ulong addMask = inWindow & ~known;
            ulong updateMask = known & inWindow;
            ulong removeMask = known & ~inWindow;
            if (removeMask != 0UL)
            {
                EnqueuePickable(state, EntityActions.Remove, removeMask);
                known &= ~removeMask;
            }
            if (addMask != 0UL)
            {
                EnqueuePickable(state, EntityActions.Add, addMask);
                known |= addMask;
            }
            if (updateMask != 0UL) EnqueuePickable(state, EntityActions.Update, updateMask);
            m_pickableKnown[state.Id] = known;
            IndexPickableChunk(state.Id, chunkKey);
        }

        /// <summary>投射物版本（同 <see cref="PublishPickable"/>）。</summary>
        private void PublishProjectile(ProjectileStateData state, bool isNew)
        {
            ulong inWindow = ComputeWindowMask(state.Position, out long chunkKey);
            m_projectileKnown.TryGetValue(state.Id, out ulong known);
            if (isNew)
            {
                if (inWindow != 0UL)
                {
                    EnqueueProjectile(state, EntityActions.Add, inWindow);
                    known |= inWindow;
                }
                m_projectileKnown[state.Id] = known;
                IndexProjectileChunk(state.Id, chunkKey);
                return;
            }
            ulong addMask = inWindow & ~known;
            ulong updateMask = known & inWindow;
            ulong removeMask = known & ~inWindow;
            if (removeMask != 0UL)
            {
                EnqueueProjectile(state, EntityActions.Remove, removeMask);
                known &= ~removeMask;
            }
            if (addMask != 0UL)
            {
                EnqueueProjectile(state, EntityActions.Add, addMask);
                known |= addMask;
            }
            if (updateMask != 0UL) EnqueueProjectile(state, EntityActions.Update, updateMask);
            m_projectileKnown[state.Id] = known;
            IndexProjectileChunk(state.Id, chunkKey);
        }

        private void RemoveTrackedPickable(int id, string reason)
        {
            if (!m_lastPickables.TryGetValue(id, out PickableStateData last))
            {
                RemovePickableChunkIndex(id);
                return;
            }
            m_lastPickables.Remove(id);
            m_pickableKnown.TryGetValue(id, out ulong known);
            m_pickableKnown.Remove(id);
            RemovePickableChunkIndex(id);
            if (m_pendingPickables.TryGetValue(id, out MaskedPickable pending))
            {
                // 还没发出去的 ADD 位 = 对端从未见过它 → 直接取消；已发出去的位 → 转成 REMOVE。
                pending.AddMask = 0UL;
                pending.RemoveMask |= pending.UpdateMask | known;
                pending.UpdateMask = 0UL;
                pending.State = last.WithAction(EntityActions.Remove).WithCollector(-1);
                if (pending.RemoveMask == 0UL) m_pendingPickables.Remove(id);
                else m_pendingPickables[id] = pending;
                return;
            }
            if (known == 0UL) return;
            EnqueuePickable(last.WithAction(EntityActions.Remove).WithCollector(-1),
                EntityActions.Remove, known);
        }

        private void RemoveTrackedProjectile(int id, string reason)
        {
            if (!m_lastProjectiles.TryGetValue(id, out ProjectileStateData last))
            {
                RemoveProjectileChunkIndex(id);
                return;
            }
            m_lastProjectiles.Remove(id);
            m_projectileKnown.TryGetValue(id, out ulong known);
            m_projectileKnown.Remove(id);
            RemoveProjectileChunkIndex(id);
            if (m_pendingProjectiles.TryGetValue(id, out MaskedProjectile pending))
            {
                pending.AddMask = 0UL;
                pending.RemoveMask |= pending.UpdateMask | known;
                pending.UpdateMask = 0UL;
                pending.State = last.WithAction(EntityActions.Remove);
                if (pending.RemoveMask == 0UL) m_pendingProjectiles.Remove(id);
                else m_pendingProjectiles[id] = pending;
                return;
            }
            if (known == 0UL) return;
            EnqueueProjectile(last.WithAction(EntityActions.Remove), EntityActions.Remove, known);
        }

        // ------------------------------------------------------------ 区块索引（窗口边沿补种用）

        private void IndexPickableChunk(int id, long chunkKey) =>
            IndexChunk(m_pickableChunks, m_pickableChunkOf, id, chunkKey);

        private void RemovePickableChunkIndex(int id) =>
            RemoveChunkIndex(m_pickableChunks, m_pickableChunkOf, id);

        private void IndexProjectileChunk(int id, long chunkKey) =>
            IndexChunk(m_projectileChunks, m_projectileChunkOf, id, chunkKey);

        private void RemoveProjectileChunkIndex(int id) =>
            RemoveChunkIndex(m_projectileChunks, m_projectileChunkOf, id);

        private static void IndexChunk(Dictionary<long, List<int>> index,
            Dictionary<int, long> owner, int id, long chunkKey)
        {
            if (owner.TryGetValue(id, out long previous))
            {
                if (previous == chunkKey) return;
                DetachChunk(index, previous, id);
            }
            owner[id] = chunkKey;
            if (!index.TryGetValue(chunkKey, out List<int> bucket))
            {
                bucket = new List<int>(4);
                index[chunkKey] = bucket;
            }
            bucket.Add(id);
        }

        private static void RemoveChunkIndex(Dictionary<long, List<int>> index,
            Dictionary<int, long> owner, int id)
        {
            if (!owner.TryGetValue(id, out long chunkKey)) return;
            owner.Remove(id);
            DetachChunk(index, chunkKey, id);
        }

        private static void DetachChunk(Dictionary<long, List<int>> index, long chunkKey, int id)
        {
            if (!index.TryGetValue(chunkKey, out List<int> bucket)) return;
            bucket.Remove(id);
            if (bucket.Count == 0) index.Remove(chunkKey);
        }

        // ------------------------------------------------------------ 待发队列（带收件人位图）

        private void EnqueuePickable(PickableStateData state, int action, ulong mask)
        {
            if (mask == 0UL) return;
            if (!m_pendingPickables.ContainsKey(state.Id) &&
                m_pendingPickables.Count >= MaxPendingPickables)
            {
                if (action == EntityActions.Remove)
                {
                    if (!TryEvictPendingPickable(false))
                    {
                        m_counters.PickableDropped++;
                        LogRateLimited("pending-pickables-cap-remove",
                            "待发拾取物队列已满且没有可丢弃的 UPDATE/ADD → 丢弃一条 REMOVE（id=" +
                            state.Id + "）");
                        return;
                    }
                }
                else
                {
                    if (!TryEvictPendingPickable(true))
                    {
                        m_counters.PickableDropped++;
                        LogRateLimited("pending-pickables-cap",
                            "待发拾取物队列已满 → 丢弃一条 " +
                            EntityActions.Name(action) + "（id=" + state.Id + "）");
                        return;
                    }
                }
            }
            m_pendingPickables.TryGetValue(state.Id, out MaskedPickable pending);
            pending.State = state;
            switch (action)
            {
                case EntityActions.Add:
                    pending.AddMask |= mask;
                    pending.RemoveMask &= ~mask;
                    break;
                case EntityActions.Update:
                    pending.UpdateMask |= mask;
                    pending.RemoveMask &= ~mask;
                    break;
                case EntityActions.Remove:
                    pending.RemoveMask |= mask;
                    pending.AddMask &= ~mask;
                    pending.UpdateMask &= ~mask;
                    break;
            }
            if (pending.AddMask == 0UL && pending.UpdateMask == 0UL && pending.RemoveMask == 0UL)
            {
                m_pendingPickables.Remove(state.Id);
                return;
            }
            m_pendingPickables[state.Id] = pending;
        }

        private void EnqueueProjectile(ProjectileStateData state, int action, ulong mask)
        {
            if (mask == 0UL) return;
            if (!m_pendingProjectiles.ContainsKey(state.Id) &&
                m_pendingProjectiles.Count >= MaxPendingProjectiles)
            {
                if (action == EntityActions.Remove)
                {
                    if (!TryEvictPendingProjectile(false))
                    {
                        m_counters.ProjectileDropped++;
                        LogRateLimited("pending-projectiles-cap-remove",
                            "待发投射物队列已满且没有可丢弃的 UPDATE/ADD → 丢弃一条 REMOVE（id=" +
                            state.Id + "）");
                        return;
                    }
                }
                else
                {
                    if (!TryEvictPendingProjectile(true))
                    {
                        m_counters.ProjectileDropped++;
                        LogRateLimited("pending-projectiles-cap",
                            "待发投射物队列已满 → 丢弃一条 " +
                            EntityActions.Name(action) + "（id=" + state.Id + "）");
                        return;
                    }
                }
            }
            m_pendingProjectiles.TryGetValue(state.Id, out MaskedProjectile pending);
            pending.State = state;
            switch (action)
            {
                case EntityActions.Add:
                    pending.AddMask |= mask;
                    pending.RemoveMask &= ~mask;
                    break;
                case EntityActions.Update:
                    pending.UpdateMask |= mask;
                    pending.RemoveMask &= ~mask;
                    break;
                case EntityActions.Remove:
                    pending.RemoveMask |= mask;
                    pending.AddMask &= ~mask;
                    pending.UpdateMask &= ~mask;
                    break;
            }
            if (pending.AddMask == 0UL && pending.UpdateMask == 0UL && pending.RemoveMask == 0UL)
            {
                m_pendingProjectiles.Remove(state.Id);
                return;
            }
            m_pendingProjectiles[state.Id] = pending;
        }

        /// <summary>true = 成功腾出一格；updateOnly=true 时只丢 UPDATE（不丢还没发出的 ADD / REMOVE）。</summary>
        private bool TryEvictPendingPickable(bool updateOnly)
        {
            int evict = 0;
            foreach (KeyValuePair<int, MaskedPickable> item in m_pendingPickables)
            {
                if (item.Value.RemoveMask != 0UL || item.Value.UpdateMask == 0UL) continue;
                evict = item.Key;
                break;
            }
            if (evict == 0 && !updateOnly)
            {
                foreach (KeyValuePair<int, MaskedPickable> item in m_pendingPickables)
                {
                    if (item.Value.RemoveMask != 0UL || item.Value.AddMask == 0UL) continue;
                    evict = item.Key;
                    break;
                }
            }
            if (evict == 0) return false;
            // 被丢弃的 ADD 位 = 对端不会收到它 → 同步清掉"已知"，避免之后把 UPDATE 发给没见过它的端。
            MaskedPickable dropped = m_pendingPickables[evict];
            if (dropped.AddMask != 0UL && m_pickableKnown.TryGetValue(evict, out ulong known))
                m_pickableKnown[evict] = known & ~dropped.AddMask;
            m_pendingPickables.Remove(evict);
            m_counters.PickableDropped++;
            return true;
        }

        private bool TryEvictPendingProjectile(bool updateOnly)
        {
            int evict = 0;
            foreach (KeyValuePair<int, MaskedProjectile> item in m_pendingProjectiles)
            {
                if (item.Value.RemoveMask != 0UL || item.Value.UpdateMask == 0UL) continue;
                evict = item.Key;
                break;
            }
            if (evict == 0 && !updateOnly)
            {
                foreach (KeyValuePair<int, MaskedProjectile> item in m_pendingProjectiles)
                {
                    if (item.Value.RemoveMask != 0UL || item.Value.AddMask == 0UL) continue;
                    evict = item.Key;
                    break;
                }
            }
            if (evict == 0) return false;
            MaskedProjectile dropped = m_pendingProjectiles[evict];
            if (dropped.AddMask != 0UL && m_projectileKnown.TryGetValue(evict, out ulong known))
                m_projectileKnown[evict] = known & ~dropped.AddMask;
            m_pendingProjectiles.Remove(evict);
            m_counters.ProjectileDropped++;
            return true;
        }

        /// <summary>
        /// 主机角色切换 / 断开时清空逐用户兴趣状态（槽位表、窗口、已知位、区块索引）。
        /// 槽位表**必须**在这里清：它原本"一次主机会话内不复用"，跨会话不清就会一直涨到 64 个后拒绝投递。
        /// </summary>
        private void ResetHostInterestState()
        {
            m_userSlots.Reset();
            m_userWindows.Clear();
            m_userRoster.Clear();
            m_userCapture.Clear();
            m_pickableKnown.Clear();
            m_projectileKnown.Clear();
            m_pickableChunks.Clear();
            m_projectileChunks.Clear();
            m_pickableChunkOf.Clear();
            m_projectileChunkOf.Clear();
        }

        private void DropPendingStates(string reason)
        {
            int pickables = m_pendingPickables.Count;
            int projectiles = m_pendingProjectiles.Count;
            m_pendingPickables.Clear();
            m_pendingProjectiles.Clear();
            m_pickableKnown.Clear();
            m_projectileKnown.Clear();
            m_pickableChunks.Clear();
            m_projectileChunks.Clear();
            m_pickableChunkOf.Clear();
            m_projectileChunkOf.Clear();
            for (int i = 0; i < m_userWindows.Count; i++) m_userWindows[i].Valid = false;
            if (pickables == 0 && projectiles == 0) return;
            m_counters.StateDropped += pickables + projectiles;
            LogRateLimited("drop-pending:" + reason,
                "没有在线玩家端 → 丢弃 " + pickables + " 条拾取物 / " + projectiles +
                " 条投射物待发变化（" + reason + "；下次加入用完整快照恢复，状态行计数可审计）");
        }

        private void CountSentPickables(List<PickableStateData> records)
        {
            for (int i = 0; i < records.Count; i++)
            {
                switch (records[i].Action)
                {
                    case EntityActions.Add: m_counters.PickableAdd++; break;
                    case EntityActions.Update: m_counters.PickableUpdate++; break;
                    case EntityActions.Remove: m_counters.PickableRemove++; break;
                }
            }
        }

        private void CountSentProjectiles(List<ProjectileStateData> records)
        {
            for (int i = 0; i < records.Count; i++)
            {
                switch (records[i].Action)
                {
                    case EntityActions.Add: m_counters.ProjectileAdd++; break;
                    case EntityActions.Update: m_counters.ProjectileUpdate++; break;
                    case EntityActions.Remove: m_counters.ProjectileRemove++; break;
                }
            }
        }

        // ================================================================ 客户端

        private void TickClient(in ModuleTickContext tick)
        {
            m_port.TickClient(tick.DeltaTime);
            double now = tick.Now;
            bool ready = m_port.IsReplicaReady;
            if (ready && !m_clientReady)
            {
                m_clientReady = true;
                RequestResync(now, "replica-ready");
            }
            else if (!ready)
            {
                m_clientReady = false;
            }
            PumpPendingPickups(now);
            PumpPendingDropReplicas(now);
            PumpPendingRequestTimeouts(now);
            PruneOwnPickupGrace(now);
            if (m_clientReady && now - m_lastEntityMessageMs >= StaleResyncSeconds &&
                (m_clientPickableIds.Count > 0 || m_clientProjectileIds.Count > 0))
            {
                RequestResync(now, "stale");
            }
        }

        /// <summary>清掉已过期的掉落者宽限条目（每拍一次；条目只与"自己刚造成的掉落"成正比）。</summary>
        private void PruneOwnPickupGrace(double now)
        {
            if (m_ownPickupGrace.Count == 0) return;
            m_removalScratch.Clear();
            foreach (KeyValuePair<int, double> item in m_ownPickupGrace)
                if (now >= item.Value) m_removalScratch.Add(item.Key);
            for (int i = 0; i < m_removalScratch.Count; i++)
                m_ownPickupGrace.Remove(m_removalScratch[i]);
        }

        private void RequestResync(double now, string reason)        {
            if (m_hosting || m_transport == null || !m_transport.IsConnected) return;
            if (m_lastResyncRequestMs != 0.0 &&
                now - m_lastResyncRequestMs < ResyncCooldownSeconds) return;
            m_lastResyncRequestMs = now;
            var request = new PE.EntityResyncRequest { Reason = reason ?? string.Empty };
            if (m_transport.TrySend(MessageType.EntityResyncRequest, request.ToByteArray()))
            {
                m_counters.ResyncRequestsSent++;
                Log("请求主机下发完整实体快照（" + reason + "）");
            }
            else
            {
                m_counters.PickupRequestFailures++;
                LogRateLimited("resync-send",
                    "实体快照请求发送失败（通道背压/未连接）→ 下一拍重试");
            }
        }

        private void PumpPendingPickups(double now)
        {
            if (m_pendingPickups.Count == 0) return;
            m_removalScratch.Clear();
            foreach (KeyValuePair<int, PendingPickup> item in m_pendingPickups)
            {
                if (now - item.Value.SentMs <= PickupTimeoutSeconds) continue;
                m_removalScratch.Add(item.Key);
            }
            for (int i = 0; i < m_removalScratch.Count; i++)
            {
                int id = m_removalScratch[i];
                m_pendingPickups.Remove(id);
                m_pickupCooldown[id] = now + PickupRetryCooldownSeconds;
                m_counters.PickupTimeouts++;
                LogRateLimited("pickup-timeout:" + id,
                    "拾取请求 id=" + id + " 超过 " + PickupTimeoutSeconds +
                    "s 没有回执 → 视为超时（稍后重试，不本地删除副本）");
            }
            if (m_pickupCooldown.Count > 256)
            {
                m_removalScratch.Clear();
                foreach (KeyValuePair<int, double> item in m_pickupCooldown)
                    if (item.Value <= now) m_removalScratch.Add(item.Key);
                for (int i = 0; i < m_removalScratch.Count; i++)
                    m_pickupCooldown.Remove(m_removalScratch[i]);
                if (m_pickupCooldown.Count > 512) m_pickupCooldown.Clear();
            }
        }

        private void OnClientPickupSubmitted(PickablePickupCandidate candidate)
        {
            if (!m_attached || m_hosting) return;
            if (m_transport == null || !m_transport.IsConnected || !m_clientReady)
            {
                m_counters.PickupSuppressed++;
                return;
            }
            if (m_pendingPickups.ContainsKey(candidate.PickableId))
            {
                m_counters.PickupSuppressed++;
                return;
            }
            double now = NowSeconds();
            if (m_pickupCooldown.TryGetValue(candidate.PickableId, out double until) && now < until)
            {
                m_counters.PickupSuppressed++;
                return;
            }
            // 掉落者宽限：自己刚丢出 / 刚造成的掉落，本机扫描在窗口内不发请求（主机也会拒绝）。
            if (m_ownPickupGrace.TryGetValue(candidate.PickableId, out double graceUntil))
            {
                if (now < graceUntil)
                {
                    m_counters.PickupGraceSuppressed++;
                    return;
                }
                m_ownPickupGrace.Remove(candidate.PickableId);
            }
            if (m_pendingPickups.Count >= MaxPendingPickups)
            {
                m_counters.PickupSuppressed++;
                LogRateLimited("pickup-pending-cap",
                    "同时待回执的拾取请求已达 " + MaxPendingPickups + " → 暂不请求 id=" +
                    candidate.PickableId);
                return;
            }
            long sequence = ++m_nextPickupSequence;
            if (sequence <= 0) sequence = ++m_nextPickupSequence;
            var request = new PickableAcquireRequestData(sequence, candidate.PickableId,
                candidate.Target, hasReach: true, reach: m_port.LocalReach);
            byte[] payload = EntityCodec.EncodeRequest(in request).ToByteArray();
            if (!m_transport.TrySend(MessageType.PickableAcquireRequest, payload))
            {
                m_counters.PickupRequestFailures++;
                LogRateLimited("pickup-send",
                    "拾取请求发送失败（通道背压/未连接）：" + request.Describe());
                return;
            }
            m_pendingPickups[candidate.PickableId] = new PendingPickup(sequence, now);
            m_counters.PickupRequestsSent++;
        }

        private void OnPickableAcquireResult(MessageContext context, PE.PickableAcquireResult message)
        {
            if (!m_attached) return;
            if (m_hosting)
            {
                m_counters.PickableRejected++;
                LogRateLimited("result-on-host",
                    "主机收到 PickableAcquireResult（主机不该是接收方）→ 丢弃");
                return;
            }
            if (!EntityCodec.TryDecodeResult(message, out PickableAcquireResultData result,
                    out string error))
            {
                m_counters.PickableRejected++;
                LogRateLimited("result-invalid:" + error,
                    "PickableAcquireResult 载荷非法（" + error + "）→ 整条丢弃");
                return;
            }
            if (!m_pendingPickups.TryGetValue(result.PickableId,
                    out PendingPickup pending) || pending.Sequence != result.Sequence)
            {
                m_counters.PickupResultUnknown++;
                LogRateLimited("result-unknown:" + result.PickableId,
                    "收到未知拾取回执（pickable=" + result.PickableId + " seq=" +
                    result.Sequence + "）→ 丢弃（可能已超时；副本仍由主机状态决定）");
                return;
            }
            m_pendingPickups.Remove(result.PickableId);
            if (!result.Accepted)
            {
                m_counters.PickupRejected++;
                m_pickupCooldown[result.PickableId] = NowSeconds() +
                    PickupRetryCooldownSeconds;
                if (result.Reason == "pickup-grace")
                {
                    // 主机明确：这个拾取物还在**掉落者本人**的免疫窗口内。
                    // 本地补登记同一窗口（正常路径是认领本机预测时登记，这里是竞态兜底）：
                    // 之后本机扫描不再请求它，窗口结束即可正常拾取。
                    m_ownPickupGrace[result.PickableId] = NowSeconds() +
                        DropPickupGraceSeconds;
                    m_counters.PickupGraceBlockedByHost++;
                }
                LogRateLimited("pickup-rejected:" + result.PickableId,
                    "主机拒绝拾取 id=" + result.PickableId + "： " + result.Reason);
                return;
            }
            m_counters.PickupAccepted++;
            if (result.HasInventory) ApplyPickupInventory(result);
            else m_counters.PickupNoInventory++;
            if (!result.Removed) return;
            var removal = new PickableStateData(result.PickableId, EntityActions.Remove, 0,
                result.RemainingCount, Vector3.Zero, Vector3.Zero, false, Vector3.Zero,
                Quaternion.Identity, false, Vector3.Zero, m_transport != null
                    ? m_transport.LocalClientId : 0);
            if (m_port.TryApplyPickable(in removal, out string removeError))
                m_clientPickableIds.Remove(result.PickableId);
            else
                LogRateLimited("pickup-remove:" + result.PickableId,
                    "回执成功后移除本地副本失败（" + removeError + "）→ 等待主机的 EntityState(REMOVE)");
        }

        private void ApplyPickupInventory(PickableAcquireResultData result)
        {
            if (m_transport == null)
            {
                m_counters.PickupInventoryFailures++;
                return;
            }
            int localClientId = m_transport.LocalClientId;
            if (localClientId < 0)
            {
                m_counters.PickupInventoryFailures++;
                LogRateLimited("pickup-inventory-local-id",
                    "拾取回执带背包增量，但本端还没有握手编号 → 无法把增量写进本机背包");
                return;
            }
            PlayerInventoryDelta delta = result.ToInventoryDelta(localClientId, 0);
            if (!m_actionPort.TryApplyInventory(localClientId, delta, out string reason))
            {
                m_counters.PickupInventoryFailures++;
                LogRateLimited("pickup-inventory-apply",
                    "把拾取回执的背包增量写进本机背包失败（" + reason + "）→ 物品仍由主机权威持有，" +
                    "下一次背包同步会对账");
                return;
            }
            m_counters.PickupInventoryApplied++;
        }

        private void OnEntityState(MessageContext context, PE.EntityState message)
        {
            if (!m_attached) return;
            m_counters.StateReceived++;
            if (m_hosting)
            {
                m_counters.ClientStateOnHostRejected++;
                m_counters.StateRejected++;
                LogRateLimited("state-on-host",
                    "主机收到客户端发来的 EntityState（客户端无权发布实体）→ 丢弃");
                return;
            }
            if (!m_clientReady)
            {
                m_counters.StateDropped++;
                return;
            }
            if (!EntityCodec.TryDecodeState(message, out EntityStateData state,
                    out string error))
            {
                m_counters.StateRejected++;
                m_counters.PickableRejected++;
                LogRateLimited("state-invalid:" + error,
                    "EntityState 载荷非法（" + error + "）→ 整条丢弃（不半条应用）");
                return;
            }
            if (!m_orderer.TryAcceptState(state.Version, out string orderReason))
            {
                m_counters.StateDropped++;
                if (orderReason == "stale" || orderReason == "duplicate")
                {
                    // 正常网络乱序：计数即可，不刷日志。
                    return;
                }
                LogRateLimited("state-order:" + orderReason,
                    "EntityState 被排序器拒绝：" + orderReason);
                return;
            }
            ApplyStateRecords(state);
        }

        private void OnEntitySnapshot(MessageContext context, PE.EntitySnapshot message)
        {
            if (!m_attached) return;
            m_counters.SnapshotsReceived++;
            if (m_hosting)
            {
                m_counters.ClientStateOnHostRejected++;
                m_counters.StateRejected++;
                LogRateLimited("snapshot-on-host",
                    "主机收到客户端发来的 EntitySnapshot（客户端无权发布实体）→ 丢弃");
                return;
            }
            if (!m_clientReady)
            {
                m_counters.SnapshotDropped++;
                LogRateLimited("snapshot-not-ready",
                    "收到实体快照时本端世界还没就绪 → 丢弃；世界就绪后会主动请求一次 resync");
                return;
            }
            if (!EntityCodec.TryDecodeSnapshot(message, out EntitySnapshotData snapshot,
                    out string error))
            {
                m_counters.StateRejected++;
                LogRateLimited("snapshot-invalid:" + error,
                    "EntitySnapshot 载荷非法（" + error + "）→ 整条丢弃");
                return;
            }
            if (!m_orderer.TryAcceptSnapshot(snapshot.SnapshotId, out string orderReason))
            {
                m_counters.StateDropped++;
                if (orderReason == "snapshot-duplicate") return;
                LogRateLimited("snapshot-order:" + orderReason,
                    "EntitySnapshot 被排序器拒绝：" + orderReason);
                return;
            }
            // 快照生成时的 state_version 比本端已应用的增量状态旧：它的 complete=true
            // 删除集合会把更新状态刚加的副本删掉（主机不会再补 ADD）。整条拒绝。
            if (snapshot.StateVersion != 0 && m_orderer.LastStateVersion != 0 &&
                snapshot.StateVersion < m_orderer.LastStateVersion)
            {
                m_counters.SnapshotStale++;
                LogRateLimited("snapshot-stale:" + snapshot.SnapshotId,
                    "实体快照 #" + snapshot.SnapshotId + " 的 state_version=" +
                    snapshot.StateVersion + " 旧于已应用的增量状态 " +
                    m_orderer.LastStateVersion + " → 整条拒绝（不删除 / 不回退副本）");
                return;
            }
            var seenPickables = new HashSet<int>();
            var seenProjectiles = new HashSet<int>();
            for (int i = 0; i < snapshot.Pickables.Count; i++)
            {
                PickableStateData record = snapshot.Pickables[i];
                seenPickables.Add(record.Id);
                ApplyPickableRecord(record);
            }
            for (int i = 0; i < snapshot.Projectiles.Count; i++)
            {
                ProjectileStateData record = snapshot.Projectiles[i];
                seenProjectiles.Add(record.Id);
                ApplyProjectileRecord(record);
            }
            m_counters.SnapshotRecords += snapshot.Pickables.Count + snapshot.Projectiles.Count;
            if (snapshot.Complete)
            {
                m_removalScratch.Clear();
                foreach (int id in m_clientPickableIds)
                    if (!seenPickables.Contains(id)) m_removalScratch.Add(id);
                for (int i = 0; i < m_removalScratch.Count; i++)
                {
                    int id = m_removalScratch[i];
                    var removal = new PickableStateData(id, EntityActions.Remove, 0, 0,
                        Vector3.Zero, Vector3.Zero, false, Vector3.Zero, Quaternion.Identity,
                        false, Vector3.Zero, -1);
                    if (m_port.TryApplyPickable(in removal, out _))
                    {
                        m_clientPickableIds.Remove(id);
                        m_counters.SnapshotStaleRemoved++;
                    }
                }
                m_removalScratch.Clear();
                foreach (int id in m_clientProjectileIds)
                    if (!seenProjectiles.Contains(id)) m_removalScratch.Add(id);
                for (int i = 0; i < m_removalScratch.Count; i++)
                {
                    int id = m_removalScratch[i];
                    var removal = new ProjectileStateData(id, EntityActions.Remove, 0,
                        Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, -1, false,
                        false, Vector3.Zero);
                    if (m_port.TryApplyProjectile(in removal, out _))
                    {
                        m_clientProjectileIds.Remove(id);
                        m_counters.SnapshotStaleRemoved++;
                    }
                }
            }
            m_lastEntityMessageMs = NowSeconds();
        }

        private void ApplyStateRecords(EntityStateData state)
        {
            for (int i = 0; i < state.Pickables.Count; i++)
                ApplyPickableRecord(state.Pickables[i]);
            for (int i = 0; i < state.Projectiles.Count; i++)
                ApplyProjectileRecord(state.Projectiles[i]);
            m_lastEntityMessageMs = NowSeconds();
        }

        private void ApplyPickableRecord(PickableStateData record)
        {
            m_counters.PickableReceived++;
            if (!m_port.TryApplyPickable(in record, out string reason))
            {
                m_counters.PickableRejected++;
                LogRateLimited("apply-pickable:" + record.Id + ":" + reason,
                    "应用拾取物副本 " + record.Describe() + " 失败：" + reason);
                return;
            }
            m_counters.PickableApplied++;
            if (record.Action == EntityActions.Remove)
            {
                m_clientPickableIds.Remove(record.Id);
                m_pendingDropReplicas.Remove(record.Id);
                m_ownPickupGrace.Remove(record.Id);
            }
            else
            {
                m_clientPickableIds.Add(record.Id);
                if (m_pendingDropReplicas.Remove(record.Id))
                    m_counters.DropReplicaConfirmed++;
                // 本机刚挖 / 放造成的掉落：认领后登记拾取宽限（窗口内本机不发请求）。
                if (record.Action == EntityActions.Add &&
                    m_port.TryClaimOwnDropGrace(in record))
                {
                    m_ownPickupGrace[record.Id] = NowSeconds() + DropPickupGraceSeconds;
                    m_counters.OwnDropGraceRegistered++;
                }
            }
        }

        private void ApplyProjectileRecord(ProjectileStateData record)
        {
            m_counters.ProjectileReceived++;
            if (!m_port.TryApplyProjectile(in record, out string reason))
            {
                m_counters.ProjectileRejected++;
                LogRateLimited("apply-projectile:" + record.Id + ":" + reason,
                    "应用投射物副本 " + record.Describe() + " 失败：" + reason);
                return;
            }
            m_counters.ProjectileApplied++;
            if (record.Action == EntityActions.Remove) m_clientProjectileIds.Remove(record.Id);
            else m_clientProjectileIds.Add(record.Id);
        }

        // ================================================================ 主机：拾取请求

        private void OnPickableAcquireRequest(MessageContext context, PE.PickableAcquireRequest message)
        {
            if (!m_attached) return;
            if (!m_hosting)
            {
                m_counters.PickableRejected++;
                LogRateLimited("request-on-client",
                    "客户端收到 PickableAcquireRequest（客户端不该是接收方）→ 丢弃");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0 || m_broadcast == null || !m_broadcast.IsPlayerClient(clientId))
            {
                m_counters.PickableRejected++;
                LogRateLimited("request-peer:" + clientId,
                    "拾取请求来源不是玩家端（clientId=" + clientId + "）→ 拒绝");
                if (clientId > 0) SendPickupRejection(clientId, message, "not-player-peer");
                return;
            }
            if (!EntityCodec.TryDecodeRequest(message, out PickableAcquireRequestData request,
                    out string error))
            {
                m_counters.PickableRejected++;
                LogRateLimited("request-invalid:" + error,
                    "PickableAcquireRequest 载荷非法（" + error + "）→ 整条丢弃");
                // 序号 / id 还能读出来就回一条明确拒绝（客户端不用干等超时）。
                if (message != null && message.Sequence > 0 && message.PickableId > 0)
                    SendPickupRejection(clientId, message, "invalid-payload-" + error);
                return;
            }
            m_counters.PickupRequestsReceived++;
            if (m_pickupHistory.TryGetValue(clientId, out PickupHistory history))
            {
                if (request.Sequence < history.LastSequence)
                {
                    m_counters.PickupDuplicate++;
                    return;
                }
                if (request.Sequence == history.LastSequence)
                {
                    m_counters.PickupDuplicate++;
                    if (history.HasResult &&
                        history.LastResult.PickableId == request.PickableId)
                        SendPickupResult(clientId, history.LastResult);
                    return;
                }
            }
            bool accepted = m_port.TryAcquire(clientId, in request,
                out PickableAcquireOutcome outcome, out string reason);
            PickableAcquireResultData result;
            if (accepted)
            {
                m_inventoryVersion++;
                result = new PickableAcquireResultData(request.Sequence, request.PickableId, true,
                    string.Empty, outcome.RemainingCount, outcome.Removed, outcome.Indices,
                    outcome.Values, outcome.Counts, m_inventoryVersion);
                m_counters.PickupAccepted++;
                PersistPickupInventory(clientId);
                EnqueueAcquireRemoval(request.PickableId, clientId, outcome);
                RelayPickupSound(clientId, request.PickableId, in outcome);
            }
            else
            {
                m_counters.PickupRejected++;
                result = new PickableAcquireResultData(request.Sequence, request.PickableId, false,
                    reason ?? "rejected", 0, false, Array.Empty<int>(), Array.Empty<int>(),
                    Array.Empty<int>(), 0);
                LogRateLimited("pickup-reject:" + clientId + ":" +
                    InteractionReachGate.ReasonCode(reason),
                    "拒绝 clientId=" + clientId + " 的拾取请求 " + request.Describe() + "：" + reason);
            }
            if (m_pickupHistory.TryGetValue(clientId, out PickupHistory updated))
            {
                updated.LastSequence = request.Sequence;
                updated.LastResult = result;
                updated.HasResult = true;
            }
            else
            {
                m_pickupHistory[clientId] = new PickupHistory
                {
                    LastSequence = request.Sequence,
                    LastResult = result,
                    HasResult = true
                };
            }
            SendPickupResult(clientId, result);
        }

        /// <summary>
        /// 主机拾取落地后立刻把该玩家端的权威背包写进角色记录（重连 / 加入关键帧的唯一来源）。
        /// 失败必须显式记录：绝不把“拾取成功”当成“已经持久化”。
        /// Source: Engine/AvatarInventoryStore.cs:StoreInventory
        ///         Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPickableProjectileHandlers.cs:376-395
        /// </summary>
        private void PersistPickupInventory(int clientId)
        {
            if (TryPersistAvatarInventory(clientId, "拾取", out _)) m_counters.PickupPersisted++;
            else m_counters.PickupPersistFailures++;
        }

        /// <summary>
        /// 主机：把该玩家端的权威背包（引擎里刚被主机改过）写进角色记录并落盘。
        /// 拾取 / 丢弃 / 开火共用；失败返回 false + 明确原因（日志按调用方上下文限流）。
        /// </summary>
        private bool TryPersistAvatarInventory(int clientId, string context, out string reason)
        {
            reason = null;
            if (!m_actionPort.TryCaptureInventory(clientId, out int[] values, out int[] counts,
                    out int activeSlot, out string captureReason))
            {
                reason = "capture:" + captureReason;
                LogRateLimited("avatar-persist-capture:" + clientId + ":" + context,
                    context + "落地后读不回 clientId=" + clientId + " 的化身背包（" +
                    captureReason + "）→ 本次不落盘（下一次背包上报仍会落盘）");
                return false;
            }
            if (!m_inventoryStore.StoreInventory(clientId, values, counts, activeSlot,
                    m_inventoryVersion, out string storeReason))
            {
                reason = "store:" + storeReason;
                LogRateLimited("avatar-persist-store:" + clientId + ":" + context,
                    context + "落地后写角色记录失败（clientId=" + clientId + "）：" + storeReason +
                    " → 本次不落盘（下一次背包上报仍会落盘）");
                return false;
            }
            return true;
        }

        private void SendPickupRejection(int clientId, PE.PickableAcquireRequest message,
            string reason)
        {
            if (message == null || message.Sequence <= 0) return;
            var result = new PickableAcquireResultData(message.Sequence, message.PickableId,
                false, reason, 0, false, Array.Empty<int>(), Array.Empty<int>(),
                Array.Empty<int>(), 0);
            SendPickupResult(clientId, result);
        }

        private void SendPickupResult(int clientId, in PickableAcquireResultData result)
        {
            byte[] payload = EntityCodec.EncodeResult(in result).ToByteArray();
            if (!m_transport.TrySendTo(clientId, MessageType.PickableAcquireResult, payload))
            {
                m_counters.StateSendFailures++;
                LogRateLimited("pickup-result-send:" + clientId,
                    "拾取回执发送失败（clientId=" + clientId + " seq=" + result.Sequence +
                    "）→ 客户端会超时重试（重复序号会重发缓存回执）");
            }
        }

        private void EnqueueAcquireRemoval(int pickableId, int collectorClientId,
            in PickableAcquireOutcome outcome)
        {
            if (!outcome.Removed)
            {
                if (m_lastPickables.TryGetValue(pickableId, out PickableStateData partial))
                {
                    PickableStateData updated = partial.WithCount(outcome.RemainingCount)
                        .WithAction(EntityActions.Update).WithCollector(-1);
                    m_lastPickables[pickableId] = updated;
                    m_pickableKnown.TryGetValue(pickableId, out ulong known);
                    if (known != 0UL)
                        EnqueuePickable(updated, EntityActions.Update, known);
                }
                return;
            }
            if (m_lastPickables.TryGetValue(pickableId, out PickableStateData last))
            {
                m_lastPickables.Remove(pickableId);
                m_pickableKnown.TryGetValue(pickableId, out ulong known);
                m_pickableKnown.Remove(pickableId);
                RemovePickableChunkIndex(pickableId);
                if (known != 0UL)
                {
                    EnqueuePickable(
                        last.WithAction(EntityActions.Remove).WithCollector(collectorClientId),
                        EntityActions.Remove, known);
                }
                return;
            }
            var removal = new PickableStateData(pickableId, EntityActions.Remove, 0,
                outcome.RemainingCount, Vector3.Zero, Vector3.Zero, false, Vector3.Zero,
                Quaternion.Identity, false, Vector3.Zero, collectorClientId);
            m_pickableKnown.TryGetValue(pickableId, out ulong remainingKnown);
            if (remainingKnown != 0UL)
                EnqueuePickable(removal, EntityActions.Remove, remainingKnown);
        }

        // ================================================================ 客户端：丢弃 / 投掷 / 开火请求

        /// <summary>
        /// 客户端丢弃（Q / 手柄 B / UI 拖出到世界）：**本地还没有任何扣除**，这里只发请求并保存在途表。
        /// 物品的扣除只发生在主机接受后的稀疏背包增量回执上。
        /// <see cref="PickableDropIntent.Source"/> 原样上线；UI 拖出单独计数
        /// （uiDropIntercepted / uiDropRequestSent / uiDropSuppressed）。
        /// </summary>
        public bool TrySubmitDrop(in PickableDropIntent intent, out string reason)
        {
            reason = null;
            bool ui = intent.Source == DropSources.Ui;
            if (!DropSources.IsKnown(intent.Source))
            {
                reason = "invalid-source";
                if (ui) m_counters.UiDropSuppressed++;
                LogRateLimited("drop-source:" + intent.Source,
                    "丢弃意图来源非法（source=" + intent.Source + "）→ 不发送请求、本地不扣物品");
                return false;
            }
            if (ui) m_counters.UiDropIntercepted++;
            if (!m_attached || m_hosting)
            {
                reason = "not-client";
                m_counters.DropSuppressed++;
                if (ui) m_counters.UiDropSuppressed++;
                return false;
            }
            if (m_transport == null || !m_transport.IsConnected || !m_clientReady)
            {
                m_counters.DropSuppressed++;
                if (ui) m_counters.UiDropSuppressed++;
                reason = "not-ready";
                LogRateLimited("drop-not-ready",
                    "客户端丢弃被抑制：本端副本/世界尚未就绪（" + (m_transport == null
                        ? "no-transport"
                        : !m_transport.IsConnected ? "disconnected" : "replica-not-ready") +
                    "）→ 不发送请求、本地不扣物品（" +
                    (ui ? "物品仍在背包里，UI 拖出已撤销" : "物品仍在背包里") + "）");
                return false;
            }
            if (m_pendingDrops.Count >= MaxPendingDropRequests)
            {
                m_counters.DropSuppressed++;
                if (ui) m_counters.UiDropSuppressed++;
                reason = "request-table-full";
                LogRateLimited("drop-table-full",
                    "同时待回执的丢弃请求已达 " + MaxPendingDropRequests + " → 本次不发送" +
                    "（本地不扣物品；等前面的回执/超时后再试）");
                return false;
            }
            long sequence = ++m_nextDropSequence;
            if (sequence <= 0) sequence = ++m_nextDropSequence;
            var request = new PickableDropRequestData(sequence, intent.ActiveSlot,
                intent.ItemValue, intent.Count, intent.Position, intent.Direction,
                intent.Source, hasReach: true, reach: m_port.LocalReach);
            if (!m_transport.TrySend(MessageType.PickableDropRequest,
                    EntityRequestCodec.EncodeDropBytes(in request)))
            {
                m_counters.DropRequestFailures++;
                if (ui)
                {
                    m_counters.UiDropSuppressed++;
                    reason = "send-failed";
                    LogRateLimited("ui-drop-send",
                        "UI 拖出丢弃请求发送失败（通道背压/未连接）：" + request.Describe() +
                        " → 已撤销本地拖出（物品仍在背包里）");
                    return false;
                }
                reason = "send-failed";
                LogRateLimited("drop-send",
                    "丢弃请求发送失败（通道背压/未连接）：" + request.Describe() +
                    " → 本地不扣物品（物品仍在背包里）");
                return false;
            }
            m_pendingDrops[sequence] = new PendingDropRequest(in request, NowSeconds());
            m_counters.DropRequestsSent++;
            if (ui) m_counters.UiDropRequestSent++;
            return true;
        }

        /// <summary>客户端投掷 / 弓 / 弩 / 火枪：只发请求，本地不生成投射物、不扣道具 / 耐久。</summary>
        public bool TrySubmitProjectileFire(in ProjectileFireIntent intent, out string reason)
        {
            reason = null;
            if (!m_attached || m_hosting)
            {
                reason = "not-client";
                m_counters.FireSuppressed++;
                return false;
            }
            if (m_transport == null || !m_transport.IsConnected || !m_clientReady)
            {
                m_counters.FireSuppressed++;
                reason = "not-ready";
                LogRateLimited("fire-not-ready",
                    "客户端开火被抑制：本端副本/世界尚未就绪（" + (m_transport == null
                        ? "no-transport"
                        : !m_transport.IsConnected ? "disconnected" : "replica-not-ready") +
                    "）→ 不发送请求、本地不生成投射物、不扣道具");
                return false;
            }
            if (m_pendingFires.Count >= MaxPendingFireRequests)
            {
                m_counters.FireSuppressed++;
                reason = "request-table-full";
                LogRateLimited("fire-table-full",
                    "同时待回执的开火请求已达 " + MaxPendingFireRequests + " → 本次不发送" +
                    "（本地不生成投射物、不扣道具）");
                return false;
            }
            long sequence = ++m_nextFireSequence;
            if (sequence <= 0) sequence = ++m_nextFireSequence;
            var request = new ProjectileFireRequestData(sequence, intent.Kind, intent.ItemValue,
                intent.ActiveSlot, intent.Origin, intent.Direction, intent.Charge,
                hasReach: true, reach: m_port.LocalReach);
            if (!m_transport.TrySend(MessageType.ProjectileFireRequest,
                    EntityRequestCodec.EncodeFireBytes(in request)))
            {
                m_counters.FireRequestFailures++;
                reason = "send-failed";
                LogRateLimited("fire-send",
                    "开火请求发送失败（通道背压/未连接）：" + request.Describe() +
                    " → 本地不生成投射物、不扣道具");
                return false;
            }
            m_pendingFires[sequence] = new PendingFireRequest(in request, NowSeconds());
            m_counters.FireRequestsSent++;
            return true;
        }

        private void PumpPendingRequestTimeouts(double now)
        {
            if (m_pendingDrops.Count > 0)
            {
                m_removalScratch64.Clear();
                foreach (KeyValuePair<long, PendingDropRequest> item in m_pendingDrops)
                    if (now - item.Value.SentMs > DropTimeoutSeconds)
                        m_removalScratch64.Add(item.Key);
                for (int i = 0; i < m_removalScratch64.Count; i++)
                {
                    long sequence = m_removalScratch64[i];
                    bool ui = m_pendingDrops.TryGetValue(sequence,
                        out PendingDropRequest pending) &&
                        pending.Request.Source == DropSources.Ui;
                    m_pendingDrops.Remove(sequence);
                    m_counters.DropTimeouts++;
                    if (ui) m_counters.UiDropTimeouts++;
                    LogRateLimited("drop-timeout:" + sequence,
                        "丢弃请求 seq=" + sequence + " 超过 " + DropTimeoutSeconds +
                        "s 没有回执 → 视为超时（客户端本地从未扣除，物品不丢；" +
                        "若主机其实执行了，以随后到达的权威背包同步为准）" +
                        (ui ? "（UI 拖出：本地从未撤销背包，物品仍在）" : string.Empty));
                }
            }
            if (m_pendingFires.Count > 0)
            {
                m_removalScratch64.Clear();
                foreach (KeyValuePair<long, PendingFireRequest> item in m_pendingFires)
                    if (now - item.Value.SentMs > FireTimeoutSeconds)
                        m_removalScratch64.Add(item.Key);
                for (int i = 0; i < m_removalScratch64.Count; i++)
                {
                    long sequence = m_removalScratch64[i];
                    m_pendingFires.Remove(sequence);
                    m_counters.FireTimeouts++;
                    LogRateLimited("fire-timeout:" + sequence,
                        "开火请求 seq=" + sequence + " 超过 " + FireTimeoutSeconds +
                        "s 没有回执 → 视为超时（客户端本地没有生成投射物、没有扣道具）");
                }
            }
        }

        private void OnPickableDropResult(MessageContext context, PE.PickableDropResult message)
        {
            if (!m_attached) return;
            if (m_hosting)
            {
                m_counters.DropRejected++;
                LogRateLimited("drop-result-on-host",
                    "主机收到 PickableDropResult（方向错误，clientId=" + context.ClientId +
                    "）→ 丢弃");
                return;
            }
            if (!EntityRequestCodec.TryDecodeDropResult(message,
                    out PickableDropResultData result, out string error))
            {
                m_counters.DropRejected++;
                LogRateLimited("drop-result-invalid:" + error,
                    "PickableDropResult 载荷非法（" + error + "）→ 整条丢弃（不半条应用）");
                return;
            }
            if (!m_pendingDrops.TryGetValue(result.Sequence, out PendingDropRequest pending))
            {
                m_counters.DropResultsUnknown++;
                LogRateLimited("drop-result-unknown:" + result.Sequence,
                    "收到未知丢弃回执（seq=" + result.Sequence + "，accepted=" + result.Accepted +
                    "）→ 不应用（可能已超时；物品状态仍以主机权威背包为准）");
                return;
            }
            m_pendingDrops.Remove(result.Sequence);
            bool ui = pending.Request.Source == DropSources.Ui;
            if (!result.Accepted)
            {
                m_counters.DropRejected++;
                m_counters.DropRejectReasons.Add(result.Reason);
                if (ui)
                {
                    m_counters.UiDropRejected++;
                    m_counters.UiDropRejectReasons.Add(result.Reason);
                    LogRateLimited("ui-drop-rejected:" + result.Reason,
                        "主机拒绝 UI 拖出丢弃请求 seq=" + result.Sequence + "（" +
                        pending.Request.Describe() + "）：" + result.Reason +
                        " → 本地拖出从未撤销背包（物品仍在背包里，不会消失）");
                }
                else
                {
                    LogRateLimited("drop-rejected:" + result.Reason,
                        "主机拒绝丢弃请求 seq=" + result.Sequence + "（" +
                        pending.Request.Describe() + "）：" + result.Reason +
                        " → 本地没有扣物品（物品仍在背包里）");
                }
                return;
            }
            m_counters.DropAccepted++;
            if (ui) m_counters.UiDropAccepted++;
            // 丢弃者免疫窗口：回执带 pickable_id → 本机 1.5s 内不请求拾取这个副本
            // （主机也用 pickup-grace 拒绝同一个人，两头同一窗口）。
            if (result.PickableId > 0)
            {
                m_ownPickupGrace[result.PickableId] = NowSeconds() + DropPickupGraceSeconds;
                m_counters.OwnDropGraceRegistered++;
            }
            // 主机回执带 pickable_id：立刻按**权威** position/velocity 物化一个副本。
            // 后续 EntityState(ADD/UPDATE) 会用同一个 id 更新它，不会重复。
            // 这一步保证"主机已生成、客户端却因为状态流竞态看不到"不会发生。
            MaterialiseDropReplica(in result);
            if (!result.HasInventory)
            {
                m_counters.DropInventoryFailures++;
                LogRateLimited("drop-no-delta",
                    "丢弃 seq=" + result.Sequence + " 被主机接受，但回执没有稀疏背包增量 → " +
                    "本机背包无法按回执对账（等下一次权威背包同步；物品已由主机移出）");
                return;
            }
            PlayerInventoryDelta delta = result.ToInventoryDelta(
                m_transport != null ? m_transport.LocalClientId : -1, 0);
            if (TryApplyRequestInventoryDelta(delta, "丢弃", out string applyReason))
            {
                m_counters.DropInventoryApplied++;
                Log("主机接受了丢弃请求 seq=" + result.Sequence + " → 权威掉落 " +
                    result.ItemValue + " x" + result.Count + "，背包增量 " +
                    result.Indices.Length + " 槽已应用（本地之前未扣除）");
            }
            else
            {
                m_counters.DropInventoryFailures++;
                m_counters.InventoryDeltaFailures++;
                LogRateLimited("drop-inventory-apply:" + applyReason,
                    "把丢弃回执的背包增量写进本机背包失败（" + applyReason +
                    "）→ 物品仍由主机权威持有，下一次背包同步会对账");
            }
        }

        /// <summary>
        /// 客户端：用主机回执里的权威 id / 位置 / 速度立即建一个渲染副本。
        /// 只调用端口 TryApplyPickable（客户端副本路径），绝不参与拾取结算；
        /// 不加入 m_clientPickableIds（等 EntityState 确认后再纳入快照替换集合，
        /// 这样迟到的完整快照不会把刚物化的副本误删）。
        /// </summary>
        private void MaterialiseDropReplica(in PickableDropResultData result)
        {
            if (result.PickableId <= 0)
            {
                LogRateLimited("drop-no-pickable-id:" + result.Sequence,
                    "丢弃回执没有 pickable_id（旧主机）→ 只能等 EntityState(ADD) 物化副本" +
                    "（seq=" + result.Sequence + " value=" + result.ItemValue + "）");
                return;
            }
            var state = new PickableStateData(result.PickableId, EntityActions.Add,
                result.ItemValue, result.Count, result.Position, result.Velocity, false,
                Vector3.Zero, Quaternion.Identity, false, Vector3.Zero, -1);
            bool applied;
            string reason;
            try
            {
                applied = m_port.TryApplyPickable(in state, out reason);
            }
            catch (Exception e)
            {
                applied = false;
                reason = "exception-" + e.GetType().Name + ": " + e.Message;
            }
            if (applied)
            {
                m_counters.DropReplicaMaterialised++;
                m_pendingDropReplicas[result.PickableId] = NowSeconds() + 3.0;
                LogRateLimited("drop-replica-materialised:" + result.PickableId,
                    "丢弃副本已按主机回执物化 id=" + result.PickableId + " value=" +
                    result.ItemValue + " count=" + result.Count + " pos=(" +
                    result.Position.X.ToString("F2") + "," +
                    result.Position.Y.ToString("F2") + "," +
                    result.Position.Z.ToString("F2") + ")");
            }
            else
            {
                m_counters.DropReplicaFailures++;
                LogRateLimited("drop-replica-materialise:" + result.PickableId,
                    "按主机回执物化丢弃副本失败 id=" + result.PickableId + "（" + reason +
                    "）→ 仍等待 EntityState(ADD)（主机是权威创建方）");
            }
        }

        private void PumpPendingDropReplicas(double now)
        {
            if (m_pendingDropReplicas.Count == 0) return;
            m_removalScratch.Clear();
            foreach (KeyValuePair<int, double> item in m_pendingDropReplicas)
                if (now >= item.Value) m_removalScratch.Add(item.Key);
            for (int i = 0; i < m_removalScratch.Count; i++)
            {
                int id = m_removalScratch[i];
                m_pendingDropReplicas.Remove(id);
                m_counters.DropReplicaTimeouts++;
                LogRateLimited("drop-replica-unconfirmed:" + id,
                    "丢弃副本 id=" + id + " 已按回执物化，但 3s 内没有收到 EntityState 确认" +
                    "（副本保留；若状态流持续缺失请检查 EntityState 广播）");
            }
        }

        private void OnWeaponFireEvent(MessageContext context, PE.WeaponFireEvent message)
        {
            if (!m_attached) return;
            if (m_hosting)
            {
                m_counters.FireSoundFailures++;
                LogRateLimited("weapon-fire-on-host",
                    "主机收到 WeaponFireEvent（主机不该是接收方）→ 丢弃");
                return;
            }
            if (!WeaponFireCodec.TryDecode(message, out WeaponFireEventData fire,
                    out string error))
            {
                m_counters.FireSoundFailures++;
                LogRateLimited("weapon-fire-invalid:" + error,
                    "WeaponFireEvent 载荷非法（" + error + "）→ 整条丢弃");
                return;
            }
            m_counters.FireSoundReceived++;
            int localClientId = m_transport != null ? m_transport.LocalClientId : -1;
            if (fire.ClientId == localClientId)
            {
                // 开火者本地已经播过；主机这条中继是给其他端的。
                m_counters.FireSoundOwnEchoSkipped++;
                return;
            }
            if (fire.EventId <= m_lastFireSoundEventId)
            {
                m_counters.FireSoundFailures++;
                return;
            }
            m_lastFireSoundEventId = fire.EventId;
            string reason;
            bool presented;
            try
            {
                presented = m_port.TryPresentWeaponFire(in fire, out reason);
            }
            catch (Exception e)
            {
                presented = false;
                reason = "exception-" + e.GetType().Name + ": " + e.Message;
            }
            if (presented)
            {
                m_counters.FireSoundPresented++;
            }
            else
            {
                m_counters.FireSoundFailures++;
                LogRateLimited("weapon-fire-present:" + reason,
                    "播放武器开火声音失败 " + fire.Describe() + "：" + reason);
            }
        }

        private void OnProjectileFireResult(MessageContext context,
            PE.ProjectileFireResult message)
        {
            if (!m_attached) return;
            if (m_hosting)
            {
                m_counters.FireRejected++;
                LogRateLimited("fire-result-on-host",
                    "主机收到 ProjectileFireResult（方向错误，clientId=" + context.ClientId +
                    "）→ 丢弃");
                return;
            }
            if (!EntityRequestCodec.TryDecodeFireResult(message,
                    out ProjectileFireResultData result, out string error))
            {
                m_counters.FireRejected++;
                LogRateLimited("fire-result-invalid:" + error,
                    "ProjectileFireResult 载荷非法（" + error + "）→ 整条丢弃（不半条应用）");
                return;
            }
            if (!m_pendingFires.TryGetValue(result.Sequence, out PendingFireRequest pending))
            {
                m_counters.FireResultsUnknown++;
                LogRateLimited("fire-result-unknown:" + result.Sequence,
                    "收到未知开火回执（seq=" + result.Sequence + "，accepted=" +
                    result.Accepted + "）→ 不应用（可能已超时）");
                return;
            }
            m_pendingFires.Remove(result.Sequence);
            if (!result.Accepted)
            {
                m_counters.FireRejected++;
                m_counters.FireRejectReasons.Add(result.Reason);
                LogRateLimited("fire-rejected:" + result.Reason,
                    "主机拒绝开火请求 seq=" + result.Sequence + "（" +
                    pending.Request.Describe() + "）：" + result.Reason +
                    " → 本地没有生成投射物、没有扣道具 / 耐久");
                return;
            }
            m_counters.FireAccepted++;
            if (result.HasInventory)
            {
                PlayerInventoryDelta delta = result.ToInventoryDelta(
                    m_transport != null ? m_transport.LocalClientId : -1, 0);
                if (TryApplyRequestInventoryDelta(delta, "开火", out string applyReason))
                {
                    m_counters.FireInventoryApplied++;
                    if (result.Kind == ProjectileFireKinds.Bow ||
                        result.Kind == ProjectileFireKinds.Crossbow ||
                        result.Kind == ProjectileFireKinds.Musket)
                    {
                        m_counters.FireResetApplied++;
                    }
                    Log("主机接受了开火请求 seq=" + result.Sequence + " kind=" +
                        ProjectileFireKinds.Name(result.Kind) + " spawned=" + result.Spawned +
                        " → 背包增量 " + result.Indices.Length + " 槽已应用（本地之前未扣除，" +
                        "弓/弩/火枪 draw 已复位）");
                }
                else
                {
                    m_counters.FireInventoryFailures++;
                    m_counters.InventoryDeltaFailures++;
                    LogRateLimited("fire-inventory-apply:" + applyReason,
                        "把开火回执的背包增量写进本机背包失败（" + applyReason +
                        "）→ 物品仍由主机权威持有，下一次背包同步会对账");
                }
            }
            else
            {
                m_counters.FireInventoryFailures++;
                LogRateLimited("fire-no-delta",
                    "开火 seq=" + result.Sequence + " 被主机接受，但回执没有稀疏背包增量 → " +
                    "本机背包无法按回执对账（等下一次权威背包同步）");
            }
        }

        private bool TryApplyRequestInventoryDelta(PlayerInventoryDelta delta, string kind,
            out string reason)
        {
            reason = null;
            if (m_transport == null || m_transport.LocalClientId < 0)
            {
                reason = "no-local-client-id";
                return false;
            }
            if (delta == null)
            {
                reason = "no-delta";
                return false;
            }
            if (!m_actionPort.TryApplyInventory(m_transport.LocalClientId, delta, out reason))
                return false;
            m_counters.InventoryDeltaApplied++;
            return true;
        }

        // ================================================================ 主机：丢弃 / 开火请求

        private void OnPickableDropRequest(MessageContext context, PE.PickableDropRequest message)
        {
            if (!m_attached) return;
            if (!m_hosting)
            {
                m_counters.DropRejected++;
                LogRateLimited("drop-request-on-client",
                    "客户端收到 PickableDropRequest（客户端不该是接收方）→ 丢弃");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0 || m_broadcast == null || !m_broadcast.IsPlayerClient(clientId))
            {
                m_counters.DropRejected++;
                LogRateLimited("drop-request-peer:" + clientId,
                    "丢弃请求来源不是玩家端（clientId=" + clientId + "）→ 拒绝");
                if (clientId > 0) SendDropRejection(clientId, message, "not-player-peer");
                return;
            }
            if (!EntityRequestCodec.TryDecodeDrop(message,
                    out PickableDropRequestData request, out string error))
            {
                m_counters.DropRejected++;
                m_counters.DropRejectReasons.Add("invalid-payload-" + error);
                LogRateLimited("drop-request-invalid:" + error,
                    "PickableDropRequest 载荷非法（" + error + "）→ 整条丢弃");
                if (message != null && message.Sequence > 0)
                    SendDropRejection(clientId, message, "invalid-payload-" + error);
                return;
            }
            m_counters.DropRequestsReceived++;
            bool requestUi = request.Source == DropSources.Ui;
            if (requestUi) m_counters.UiDropRequestsReceived++;
            if (m_dropHistory.TryGetValue(clientId, out DropHistory history))
            {
                if (request.Sequence < history.LastSequence)
                {
                    m_counters.DropDuplicate++;
                    return;
                }
                if (request.Sequence == history.LastSequence)
                {
                    m_counters.DropDuplicate++;
                    if (history.HasResult && history.LastResult != null &&
                        history.LastResult.Sequence == request.Sequence)
                        SendDropResult(clientId, history.LastResult);
                    return;
                }
            }
            bool accepted;
            PickableDropOutcome outcome;
            string reason;
            try
            {
                accepted = m_port.TryDrop(clientId, in request, out outcome, out reason);
            }
            catch (Exception e)
            {
                accepted = false;
                outcome = default;
                reason = "execution-exception-" + e.GetType().Name;
                LogRateLimited("drop-execute-exception:" + e.GetType().Name,
                    "主机执行丢弃请求 clientId=" + clientId + " " + request.Describe() +
                    " 异常：" + e.GetType().Name + " " + e.Message);
            }
            PickableDropResultData result;
            if (accepted)
            {
                m_inventoryVersion++;
                result = new PickableDropResultData(request.Sequence, true, string.Empty,
                    outcome.ItemValue, outcome.Count, outcome.Position, outcome.Velocity,
                    outcome.Indices, outcome.Values, outcome.Counts, m_inventoryVersion,
                    outcome.PickableId);
                m_counters.DropAccepted++;
                if (TryPersistAvatarInventory(clientId, "丢弃", out string persistReason))
                {
                    m_counters.DropPersisted++;
                }
                else
                {
                    m_counters.DropPersistFailures++;
                    LogRateLimited("drop-persist:" + clientId,
                        "丢弃落地后角色背包落盘失败（clientId=" + clientId + "）：" + persistReason +
                        " → 本次不落盘（下一次背包上报/周期抓取仍会落盘）");
                }
                Log("主机执行丢弃 clientId=" + clientId + " " + request.Describe() +
                    " → 生成拾取物 value=" + outcome.ItemValue + " count=" + outcome.Count +
                    " pos=(" + outcome.Position.X.ToString("F2") + "," +
                    outcome.Position.Y.ToString("F2") + "," +
                    outcome.Position.Z.ToString("F2") + ") 背包增量 " +
                    outcome.Indices.Length + " 槽" +
                    (requestUi ? "（UI 拖出：只移除请求数量）" : string.Empty));
            }
            else
            {
                m_counters.DropRejected++;
                m_counters.DropRejectReasons.Add(reason ?? "rejected");
                if (requestUi) m_counters.UiDropRejectReasons.Add(reason ?? "rejected");
                result = new PickableDropResultData(request.Sequence, false, reason ?? "rejected",
                    0, 0, Vector3.Zero, Vector3.Zero, Array.Empty<int>(), Array.Empty<int>(),
                    Array.Empty<int>(), 0);
                LogRateLimited("drop-reject:" + clientId + ":" +
                    InteractionReachGate.ReasonCode(reason),
                    "拒绝 clientId=" + clientId + " 的丢弃请求 " + request.Describe() + "：" +
                    (reason ?? "rejected") + (requestUi
                        ? "（UI 拖出被拒：客户端从未撤销背包，物品不会消失）"
                        : string.Empty));
            }
            RememberDropResult(clientId, request.Sequence, result);
            SendDropResult(clientId, result);
        }

        private void OnProjectileFireRequest(MessageContext context,
            PE.ProjectileFireRequest message)
        {
            if (!m_attached) return;
            if (!m_hosting)
            {
                m_counters.FireRejected++;
                LogRateLimited("fire-request-on-client",
                    "客户端收到 ProjectileFireRequest（客户端不该是接收方）→ 丢弃");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0 || m_broadcast == null || !m_broadcast.IsPlayerClient(clientId))
            {
                m_counters.FireRejected++;
                LogRateLimited("fire-request-peer:" + clientId,
                    "开火请求来源不是玩家端（clientId=" + clientId + "）→ 拒绝");
                if (clientId > 0) SendFireRejection(clientId, message, "not-player-peer");
                return;
            }
            if (!EntityRequestCodec.TryDecodeFire(message,
                    out ProjectileFireRequestData request, out string error))
            {
                m_counters.FireRejected++;
                m_counters.FireRejectReasons.Add("invalid-payload-" + error);
                LogRateLimited("fire-request-invalid:" + error,
                    "ProjectileFireRequest 载荷非法（" + error + "）→ 整条丢弃");
                if (message != null && message.Sequence > 0)
                    SendFireRejection(clientId, message, "invalid-payload-" + error);
                return;
            }
            m_counters.FireRequestsReceived++;
            if (m_fireHistory.TryGetValue(clientId, out FireHistory history))
            {
                if (request.Sequence < history.LastSequence)
                {
                    m_counters.FireDuplicate++;
                    return;
                }
                if (request.Sequence == history.LastSequence)
                {
                    m_counters.FireDuplicate++;
                    if (history.HasResult && history.LastResult != null &&
                        history.LastResult.Sequence == request.Sequence)
                        SendFireResult(clientId, history.LastResult);
                    return;
                }
            }
            bool accepted;
            ProjectileFireOutcome outcome;
            string reason;
            try
            {
                accepted = m_port.TryFireProjectile(clientId, in request, out outcome, out reason);
            }
            catch (Exception e)
            {
                accepted = false;
                outcome = default;
                reason = "execution-exception-" + e.GetType().Name;
                LogRateLimited("fire-execute-exception:" + e.GetType().Name,
                    "主机执行开火请求 clientId=" + clientId + " " + request.Describe() +
                    " 异常：" + e.GetType().Name + " " + e.Message);
            }
            ProjectileFireResultData result;
            if (accepted)
            {
                m_inventoryVersion++;
                result = new ProjectileFireResultData(request.Sequence, true, string.Empty,
                    request.Kind, outcome.ItemValue, outcome.Spawned, outcome.Position,
                    outcome.Velocity, outcome.Indices, outcome.Values, outcome.Counts,
                    m_inventoryVersion);
                m_counters.FireAccepted++;
                if (outcome.Spawned > 0)
                    m_counters.FireProjectilesSpawned += outcome.Spawned;
                else
                    m_counters.FireBlankShots++;
                if (TryPersistAvatarInventory(clientId, "开火", out string persistReason))
                {
                    m_counters.FirePersisted++;
                }
                else
                {
                    m_counters.FirePersistFailures++;
                    LogRateLimited("fire-persist:" + clientId,
                        "开火落地后角色背包落盘失败（clientId=" + clientId + "）：" + persistReason +
                        " → 本次不落盘（下一次背包上报/周期抓取仍会落盘）");
                }
                Log("主机执行开火 clientId=" + clientId + " " + request.Describe() +
                    " → 权威投射物 " + outcome.Spawned + " 个，出生点 (" +
                    outcome.Origin.X.ToString("F2") + "," + outcome.Origin.Y.ToString("F2") +
                    "," + outcome.Origin.Z.ToString("F2") + ")，背包增量 " +
                    outcome.Indices.Length + " 槽" + (outcome.Spawned == 0
                        ? "（引擎口径的合法空结果：空包弹 / 没装箭 / 弩没拉满 / 哑火）"
                        : string.Empty));
                // 释放后复位：主机已把权威背包里的弓/弩/火枪改成 draw=0，并扣了耐久；
                // 立即回读并广播一条权威手持物，开火者与其他客户端同时复位
                // （否则远端化身会一直停在满弓姿势）。
                BroadcastFireReset(clientId, request.Kind);
                // 开火声音：主机本机播一次 + 中继给其他客户端（开火者跳过自己的回声）。
                RelayWeaponFire(clientId, in request, in outcome);
            }
            else
            {
                m_counters.FireRejected++;
                m_counters.FireRejectReasons.Add(reason ?? "rejected");
                result = new ProjectileFireResultData(request.Sequence, false,
                    reason ?? "rejected", 0, 0, 0, Vector3.Zero, Vector3.Zero,
                    Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0);
                LogRateLimited("fire-reject:" + clientId + ":" +
                    InteractionReachGate.ReasonCode(reason),
                    "拒绝 clientId=" + clientId + " 的开火请求 " + request.Describe() + "：" +
                    (reason ?? "rejected") + "（不生成投射物、不消费道具）");
            }
            RememberFireResult(clientId, request.Sequence, result);
            SendFireResult(clientId, result);
        }

        private static HeldItemAimKind AimKindFor(int kind)
        {
            switch (kind)
            {
                case ProjectileFireKinds.Bow: return HeldItemAimKind.Bow;
                case ProjectileFireKinds.Crossbow: return HeldItemAimKind.Crossbow;
                case ProjectileFireKinds.Musket: return HeldItemAimKind.Musket;
                default: return HeldItemAimKind.None;
            }
        }

        private void BroadcastFireReset(int clientId, int kind)
        {
            if (m_actionPort == null)
            {
                m_counters.FireResetFailures++;
                LogRateLimited("fire-reset-no-port",
                    "开火后无法广播手持物复位：没有角色动作端口（clientId=" + clientId + "）");
                return;
            }
            string reason;
            bool sent;
            try
            {
                sent = m_actionPort.TryBroadcastHeldItem(clientId, AimKindFor(kind), out reason);
            }
            catch (Exception e)
            {
                sent = false;
                reason = "exception-" + e.GetType().Name + ": " + e.Message;
            }
            if (sent)
            {
                m_counters.FireResetSent++;
                return;
            }
            m_counters.FireResetFailures++;
            LogRateLimited("fire-reset-send:" + clientId,
                "开火后复位手持物广播失败（clientId=" + clientId + " kind=" +
                ProjectileFireKinds.Name(kind) + "）：" + reason +
                " → 远端化身可能暂时保持瞄准姿势（开火者的背包增量回执仍会复位本机）");
        }

        private uint NextFireSoundEventId()
        {
            uint id = ++m_fireSoundEventId;
            if (id == 0) id = ++m_fireSoundEventId;
            return id;
        }

        private void RelayWeaponFire(int clientId, in ProjectileFireRequestData request,
            in ProjectileFireOutcome outcome)
        {
            var fire = new WeaponFireEventData(clientId, m_serverTickMs,
                NextFireSoundEventId(), request.Kind, outcome.Origin, outcome.Direction,
                outcome.Misfire);
            string presentReason;
            bool presented;
            try
            {
                presented = m_port.TryPresentWeaponFire(in fire, out presentReason);
            }
            catch (Exception e)
            {
                presented = false;
                presentReason = "exception-" + e.GetType().Name + ": " + e.Message;
            }
            if (presented)
            {
                m_counters.FireSoundPresented++;
            }
            else
            {
                m_counters.FireSoundFailures++;
                LogRateLimited("fire-sound-host:" + presentReason,
                    "主机播放开火声音失败（clientId=" + clientId + " " + fire.Describe() +
                    "）：" + presentReason + " → 仍继续中继给其他客户端");
            }
            if (m_broadcast == null)
            {
                m_counters.FireSoundFailures++;
                LogRateLimited("fire-sound-no-broadcast",
                    "没有广播端口 → 开火声音无法中继给其他客户端（clientId=" + clientId + "）");
                return;
            }
            if (!WeaponFireCodec.TryEncode(in fire, out PE.WeaponFireEvent encoded,
                    out string encodeReason))
            {
                m_counters.FireSoundFailures++;
                LogRateLimited("fire-sound-encode:" + encodeReason,
                    "开火声音事件编码失败（" + encodeReason + "）：" + fire.Describe() +
                    " → 本机已播，其他客户端收不到");
                return;
            }
            byte[] payload = encoded.ToByteArray();
            // 收件人 = 兴趣窗口包含开火点的端（客户端自己按声音衰减决定播不播 / 播多响）：
            // 客户端只加载自己周围那片地形，窗口外的端既看不到也听不到，没有理由中继。
            if (m_window == null)
            {
                m_counters.FireSoundFailures++;
                LogRateLimited("fire-sound-no-window-port",
                    "没有窗口投递端口 → 开火声音无法按窗口中继（clientId=" + clientId +
                    "，已计入 FireSoundFailures；绝不退回全体广播）");
                return;
            }
            int sent = m_window.BroadcastToPlayersAt(MessageType.WeaponFireEvent, payload,
                outcome.Origin, 0, out int candidates);
            if (sent > 0)
            {
                m_counters.FireSoundRelayed += sent;
            }
            else if (candidates == 0)
            {
                m_counters.FireSoundFailures++;
                LogRateLimited("fire-sound-no-window",
                    "开火声音没有可投递的端（'没有端上报兴趣窗口'）：" + fire.Describe());
            }
            else
            {
                m_counters.FireSoundFailures++;
                LogRateLimited("fire-sound-out-of-window",
                    "开火声音的窗口内端全部背压 / 断开（候选 " + candidates +
                    "）：" + fire.Describe());
            }
        }

        // ================================================================ 拾取声音中继

        private uint NextPickupSoundEventId()
        {
            uint id = ++m_pickupSoundEventId;
            if (id == 0) id = ++m_pickupSoundEventId;
            return id;
        }

        /// <summary>
        /// 主机：接受一条客户端拾取请求后，补发那条只存在于主机引擎里的
        /// <c>Audio/PickableCollected</c>（纯表现）。
        ///
        /// 规则：
        ///   · 请求者恒定收到（收件人策略无条件包含它）；
        ///   · 其他玩家端只有读得到位置且距拾取点 ≤ <see cref="PickupSoundPolicy.MaxDistance"/>
        ///     才收到（读不到位置的端明确计数，不发）；
        ///   · 主机本机拾取（clientId &lt;= 0）**绝不**发事件：引擎原生 Update 已经播过，
        ///     再发就是回声 → 计 <c>pickupSoundEchoSkipped</c>；
        ///   · 主机本机也播一次同一参数的声音（附近主机玩家应当听到）；
        ///   · 发送 / 接收都有限速；所有失败都写限流日志 + 计数，绝不静默。
        /// </summary>
        private void RelayPickupSound(int clientId, int pickableId,
            in PickableAcquireOutcome outcome)
        {
            if (!outcome.PlayPickupSound) return;
            if (clientId <= 0)
            {
                m_counters.PickupSoundEchoSkipped++;
                LogRateLimited("pickup-sound-host-local",
                    "主机本机拾取（clientId=" + clientId + " pickable=" + pickableId +
                    "）→ 不中继拾取声音（引擎原生 Update 已经播放，避免回声）");
                return;
            }
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (!m_pickupSoundSendLimiter.TryAccept(nowMs))
            {
                m_counters.PickupSoundRateSkipped++;
                LogRateLimited("pickup-sound-send-rate",
                    "拾取声音发送超过限速 " + PickupSoundPolicy.MaxEventsPerSecond +
                    " 条/秒（clientId=" + clientId + " pickable=" + pickableId +
                    "）→ 本次不中继（拾取本身已落地，不受影响）");
                return;
            }
            var sound = new PickupSoundEventData(clientId, m_serverTickMs,
                NextPickupSoundEventId(), outcome.Position);
            // 主机本机播放：与引擎同一条声音参数（附近主机玩家应当听到这次拾取）。
            string presentReason;
            bool presented;
            try
            {
                presented = m_port.TryPresentPickupSound(in sound, out presentReason);
            }
            catch (Exception e)
            {
                presented = false;
                presentReason = "exception-" + e.GetType().Name + ": " + e.Message;
            }
            if (presented)
            {
                m_counters.PickupSoundPresented++;
            }
            else
            {
                m_counters.PickupSoundPresentFailures++;
                LogRateLimited("pickup-sound-host-present:" + presentReason,
                    "主机播放拾取声音失败（" + sound.Describe() + "）：" + presentReason +
                    " → 仍继续中继给客户端");
            }
            m_pickupSoundPeers.Clear();
            if (!m_port.TryCollectPlayerPeers(m_pickupSoundPeers, out string peerReason))
            {
                LogRateLimited("pickup-sound-peers",
                    "收集玩家端位置失败（" + peerReason + "）→ 本次只有请求者会收到拾取声音");
            }
            RemoteSoundRecipientPlan plan = RemoteSoundRelayPolicy.Plan(clientId,
                m_pickupSoundPeers, outcome.Position, PickupSoundPolicy.MaxDistance);
            m_counters.PickupSoundSkippedDistance += plan.SkippedDistance;
            m_counters.PickupSoundSkippedNoPosition += plan.SkippedNoPosition;
            if (plan.SkippedDistance > 0 || plan.SkippedNoPosition > 0)
            {
                LogRateLimited("pickup-sound-recipient-skip",
                    "拾取声音收件人裁剪（clientId=" + clientId + "）：超出 " +
                    PickupSoundPolicy.MaxDistance.ToString("F0") + " m 跳过 " +
                    plan.SkippedDistance + " 端；读不到位置跳过 " + plan.SkippedNoPosition +
                    " 端（请求者仍恒定收到）");
            }
            if (!PickupSoundCodec.TryEncode(in sound, out PE.PickupSoundEvent encoded,
                    out string encodeReason))
            {
                m_counters.PickupSoundSendFailures++;
                LogRateLimited("pickup-sound-encode:" + encodeReason,
                    "拾取声音事件编码失败（" + encodeReason + "）：" + sound.Describe() +
                    " → 本次没有中继（拾取本身已落地）");
                return;
            }
            byte[] payload = encoded.ToByteArray();
            for (int i = 0; i < plan.Recipients.Length; i++)
            {
                int recipient = plan.Recipients[i];
                if (!m_transport.TrySendTo(recipient, MessageType.PickupSoundEvent, payload))
                {
                    m_counters.PickupSoundSendFailures++;
                    LogRateLimited("pickup-sound-send:" + recipient,
                        "拾取声音发送失败（clientId=" + recipient + " " + sound.Describe() +
                        "）→ 该端听不到这次拾取声音");
                    continue;
                }
                m_counters.PickupSoundRelayed++;
            }
            if (plan.Recipients.Length == 0)
            {
                m_counters.PickupSoundSendFailures++;
                LogRateLimited("pickup-sound-no-recipients",
                    "拾取声音没有收件人（" + sound.Describe() + "；请求者 clientId=" +
                    clientId + "）→ 请求者可能听不到这次拾取");
            }
        }

        /// <summary>
        /// 客户端：主机接受的拾取声音。先严格解码 / 去重 / 限速，再按本机监听者距离裁剪，
        /// 最后只调 <c>SubsystemAudio.PlaySound</c>（不授予物品 / 不删拾取物 / 不改地形）。
        /// 请求者恒定播放（即使读不到监听者位置）；其他端按 16 m 半径裁剪。
        /// </summary>
        private void OnPickupSoundEvent(MessageContext context, PE.PickupSoundEvent message)
        {
            if (!m_attached) return;
            if (m_hosting)
            {
                // 主机不是接收方：事件回到主机只可能是接线错误 —— 明确跳过（回声）。
                m_counters.PickupSoundEchoSkipped++;
                LogRateLimited("pickup-sound-on-host",
                    "主机收到 PickupSoundEvent（方向错误，clientId=" + context.ClientId +
                    "）→ 丢弃（主机已经在本端播放过）");
                return;
            }
            m_counters.PickupSoundReceived++;
            if (!PickupSoundCodec.TryDecode(message, out PickupSoundEventData sound,
                    out string error))
            {
                m_counters.PickupSoundMalformed++;
                LogRateLimited("pickup-sound-invalid:" + error,
                    "PickupSoundEvent 载荷非法（" + error + "，clientId=" + context.ClientId +
                    "）→ 整条丢弃");
                return;
            }
            if (!m_pickupSoundOrderer.TryAccept(in sound, out string orderReason))
            {
                m_counters.PickupSoundReplay++;
                LogRateLimited("pickup-sound-order:" + orderReason,
                    "拾取声音 " + sound.Describe() + " 被拒绝（" + orderReason +
                    "，last id=" + m_pickupSoundOrderer.LastEventId + "）→ 不重复播放");
                return;
            }
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (!m_pickupSoundRecvLimiter.TryAccept(nowMs))
            {
                m_counters.PickupSoundRateSkipped++;
                LogRateLimited("pickup-sound-recv-rate",
                    "拾取声音到达过快（限速 " + PickupSoundPolicy.MaxReceiveEventsPerSecond +
                    " 条/秒）：" + sound.Describe() + " → 本事件不播放");
                return;
            }
            int localClientId = m_transport != null ? m_transport.LocalClientId : -1;
            bool hasListener = m_port.TryGetAudioListenerPosition(out Vector3 listener,
                out string listenerReason);
            PickupSoundPresentationPlan plan = PickupSoundPresentationPolicy.Plan(in sound,
                localClientId, listener, hasListener, PickupSoundPolicy.MaxDistance);
            m_counters.PickupSoundSkippedDistance += plan.SkippedDistance;
            if (!plan.Present)
            {
                if (plan.NeedsListener)
                {
                    m_counters.PickupSoundNoListener++;
                    LogRateLimited("pickup-sound-no-listener",
                        "读不到本机监听者位置（" + listenerReason + "）→ 拾取声音 " +
                        sound.Describe() + " 无法按距离判断（计数，不猜原点）");
                }
                else
                {
                    LogRateLimited("pickup-sound-distance",
                        "拾取声音 " + sound.Describe() + " 距本机超过 " +
                        PickupSoundPolicy.MaxDistance.ToString("F0") + " m → 不播放");
                }
                return;
            }
            string presentReason;
            bool presented;
            try
            {
                presented = m_port.TryPresentPickupSound(in sound, out presentReason);
            }
            catch (Exception e)
            {
                presented = false;
                presentReason = "exception-" + e.GetType().Name + ": " + e.Message;
            }
            if (presented)
            {
                m_counters.PickupSoundPresented++;
                return;
            }
            m_counters.PickupSoundPresentFailures++;
            LogRateLimited("pickup-sound-present:" + presentReason,
                "播放拾取声音失败 " + sound.Describe() + "：" + presentReason +
                " → 本次没有声音（明确计数）");
        }

        private void RememberDropResult(int clientId, long sequence,
            PickableDropResultData result)
        {
            if (!m_dropHistory.TryGetValue(clientId, out DropHistory history))
            {
                history = new DropHistory();
                m_dropHistory[clientId] = history;
            }
            history.LastSequence = sequence;
            history.LastResult = result;
            history.HasResult = true;
        }

        private void RememberFireResult(int clientId, long sequence,
            ProjectileFireResultData result)
        {
            if (!m_fireHistory.TryGetValue(clientId, out FireHistory history))
            {
                history = new FireHistory();
                m_fireHistory[clientId] = history;
            }
            history.LastSequence = sequence;
            history.LastResult = result;
            history.HasResult = true;
        }

        private void SendDropRejection(int clientId, PE.PickableDropRequest message,
            string reason)
        {
            if (message == null || message.Sequence <= 0) return;
            var result = new PickableDropResultData(message.Sequence, false, reason, 0, 0,
                Vector3.Zero, Vector3.Zero, Array.Empty<int>(), Array.Empty<int>(),
                Array.Empty<int>(), 0);
            SendDropResult(clientId, result);
        }

        private void SendFireRejection(int clientId, PE.ProjectileFireRequest message,
            string reason)
        {
            if (message == null || message.Sequence <= 0) return;
            var result = new ProjectileFireResultData(message.Sequence, false, reason, 0, 0, 0,
                Vector3.Zero, Vector3.Zero, Array.Empty<int>(), Array.Empty<int>(),
                Array.Empty<int>(), 0);
            SendFireResult(clientId, result);
        }

        private void SendDropResult(int clientId, PickableDropResultData result)
        {
            if (m_transport == null || result == null) return;
            byte[] payload = EntityRequestCodec.EncodeDropResult(in result).ToByteArray();
            if (!m_transport.TrySendTo(clientId, MessageType.PickableDropResult, payload))
            {
                m_counters.DropResultSendFailures++;
                LogRateLimited("drop-result-send:" + clientId,
                    "丢弃回执发送失败（clientId=" + clientId + " seq=" + result.Sequence +
                    "）→ 客户端会按超时处理（它本地从未扣除，物品不丢）");
            }
        }

        private void SendFireResult(int clientId, ProjectileFireResultData result)
        {
            if (m_transport == null || result == null) return;
            byte[] payload = EntityRequestCodec.EncodeFireResult(in result).ToByteArray();
            if (!m_transport.TrySendTo(clientId, MessageType.ProjectileFireResult, payload))
            {
                m_counters.FireResultSendFailures++;
                LogRateLimited("fire-result-send:" + clientId,
                    "开火回执发送失败（clientId=" + clientId + " seq=" + result.Sequence +
                    "）→ 客户端会按超时处理（它本地没有生成投射物、没有扣道具）");
            }
        }

        // ================================================================ resync 请求（主机）

        private void OnEntityResyncRequest(MessageContext context, PE.EntityResyncRequest message)
        {
            if (!m_attached) return;
            if (!m_hosting)
            {
                m_counters.StateRejected++;
                LogRateLimited("resync-on-client",
                    "客户端收到 EntityResyncRequest（只有主机处理）→ 丢弃");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0 || m_broadcast == null || !m_broadcast.IsPlayerClient(clientId))
            {
                m_counters.StateRejected++;
                LogRateLimited("resync-peer:" + clientId,
                    "实体快照请求来源不是玩家端（clientId=" + clientId + "）→ 拒绝");
                return;
            }
            m_counters.ResyncRequestsReceived++;
            m_pendingSnapshots[clientId] = NowSeconds();
            Log("客户端 " + clientId + " 请求完整实体快照（" +
                (message?.Reason ?? string.Empty) + "）→ 已排队");
        }

        // ================================================================ 工具

        private uint NextStateVersion()
        {
            uint version = ++m_stateVersion;
            if (version == 0) version = ++m_stateVersion;
            return version;
        }

        private uint NextSnapshotId()
        {
            uint id = ++m_snapshotId;
            if (id == 0) id = ++m_snapshotId;
            return id;
        }

        /// <summary>
        /// 模块自己的时间基准（= 最近一次 <see cref="ModuleTickContext.Now"/>）。
        /// 不读 <c>Time.RealTime</c>：自检没有引擎主循环，墙钟与 tick 时间不一致会让
        /// 拾取超时 / 冷却判据失真（生产里两者同源）。
        /// </summary>
        private double NowSeconds() => m_now;

        private void MaybeLogSummary(double now)
        {
            long ms = (long)(now * 1000.0);
            if (ms - m_lastSummaryMs < SummaryLogMs) return;
            m_lastSummaryMs = ms;
            Log(StatusLine());
        }

        private void Log(string message) => m_context?.Diagnostics?.Info(ModuleId, message);

        private void LogRateLimited(string key, string message)
        {
            long nowMs = (long)(m_now * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            if (m_lastProblemMs.Count > 512) m_lastProblemMs.Clear();
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics?.Warn(ModuleId, message);
        }
    }

    /// <summary>
    /// <see cref="PickableStateData"/> 的只读扩展（模块内部用；不改变端口契约）。
    /// </summary>
    internal static class PickableStateDataExtensions
    {
        public static PickableStateData WithCount(this PickableStateData data, int count) =>
            new PickableStateData(data.Id, data.Action, data.Value, count, data.Position,
                data.Velocity, data.HasStuckMatrix, data.StuckPosition, data.StuckRotation,
                data.HasFlyTo, data.FlyTo, data.CollectorClientId);
    }
}
