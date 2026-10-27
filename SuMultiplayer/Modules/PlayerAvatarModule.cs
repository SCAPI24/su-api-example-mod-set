using System;
using System.Collections.Generic;
using System.Linq;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using SuMultiplayer.Terrain;
using Google.Protobuf;
using Proto = SuMultiplayer.Protocol;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 联机角色模块：把**两端的角色实体**建起来，并让它们**按身份**绑定到角色记录。
    ///
    /// 需求（用户口径，逐条对应）：
    ///   ① 主机世界里有客户端的角色、**每个客户端世界里都有主机和其他客户端**的角色
    ///      —— 主机把"房间里有哪些人、各自长什么样"广播给每个客户端（PlayerProfile / AvatarRemove）；
    ///   ② 角色记录只写联机的角色 xml（<c>&lt;世界目录&gt;/SuMultiplayerPlayers.xml</c>），
    ///      **不污染 Project.xml**（引擎动作见 <see cref="NetworkAvatarSpawner"/> 里的 m_entities.Remove）；
    ///   ③ 绑定**不看角色 id / PlayerIndex / clientId**，只看身份（UserId.dat）；
    ///   ④ 身份 = <c>Game.UserManager.ActiveUser.UniqueId</c>（UserId.dat），见 <see cref="PlayerIdentity"/>；
    ///   ⑤ 名字 / 性别 / 皮肤 / 衣服都来自真实玩家（PlayerData / ComponentClothing），不再用
    ///      引擎固定显示名 "Android User"、也不再让化身随机长成别的样子。
    ///
    /// 旧实现对应处（照旧实现移植，不自己发明方案）：
    ///   · 主机建客户端角色：ScMultiplayerWorldObjectHandlers.cs:1237-1241 / CreateNetworkPlayer；
    ///   · 客户端建主机角色：ScMultiplayerWorldTransferHandlers.cs:558-562 / UpdateLoop:1242-1243；
    ///   · 资料（名字/职业/皮肤/衣服）广播与本地应用：
    ///     ScMultiplayerProfileHandlers.cs:728-890（SynchronizePlayerProfiles / HandlePlayerProfileMessage）
    ///     与 :1248-1300（SynchronizeLocalProfileIfChanged / ApplyProfileToLocalPlayer）；
    ///   · 首次加入没有角色 → SCMP_PROFILE_REQUIRED：
    ///     ScMultiplayerPlayerHealthAndIngress.cs:941-961 + ScMultiplayerClientEvents.cs:254-278
    ///     （客户端打开 SuNetworkPlayerScreen，完成后 SubmitPendingJoin(hasPlayerProfile: true)）。
    ///
    /// 本模块**不含静态状态**，也不直接引用别的模块内部：只从 <see cref="IModuleContext"/> 取端口。
    /// </summary>
    public sealed class PlayerAvatarModule : IMultiplayerModule, IAvatarTerrainLink, IHelloProfileGate,
        IPlayerSkinRecordPort, IPlayerProfileAuthorityPort, IProfileSkinCatalog, ISpawnPointProvider
    {
        /// <summary>主机角色在**本机**通道里的槽位键（旧实现同样用 0）；它只是本机索引，不是身份。
        /// 位置快照批里"主机自己那一条"用的也是这个键（<c>NetworkAvatarSpawner.CapturePlayerPositionBatch</c>）。</summary>
        internal const int HostAvatarSlotKey = 0;

        /// <summary>主机侧角色记录刷新/落盘周期（旧实现 PlayerRecordSaveInterval = 5 秒）。</summary>
        private const long RecordRefreshIntervalMs = 5000;

        /// <summary>创建失败后的重试最小间隔（旧实现每帧重试；这里限制日志频率，不改变失败语义）。</summary>
        private const long CreateRetryIntervalMs = 2000;

        private sealed class PendingPeer
        {
            public PlayerKey Key;
            public string Name;
            public string Identity;
            public string RecordKey;
            public PlayerProfileSnapshot Profile;
            public long LastAttemptMs;
        }

        /// <summary>主机侧一个**本机角色槽位**的已登记状态（一个 (clientId, slot) 一条）。</summary>
        private sealed class PeerSlot
        {
            public PlayerKey Key;
            public string Identity = string.Empty;
            public string Name = string.Empty;
            public string RecordKey = string.Empty;
            public PlayerProfileSnapshot Profile;
        }

        /// <summary>限流日志间隔（用户口径 10 秒；计数每次照常累加）。</summary>
        private const long RateLimitedLogIntervalMs = 10000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly NetworkAvatarSpawner m_spawner;
        private readonly SuMultiplayerPlayersXml m_records;
        private readonly LocalPlayerProfileStore m_profileStore;
        /// <summary>
        /// 本机资料缓存当前属于哪个世界目录。**客户端角色按世界隔离**（用户口径 2026-10-21）：
        /// 换世界就清空本机资料缓存，下一张图不再把上一张图的角色外观当"我的资料"报给主机
        /// （否则新世界不会弹创建角色，而是沿用旧名字/旧皮肤）。
        /// </summary>
        private string m_profileWorldDirectory = string.Empty;
        /// <summary>引擎皮肤资源端口（组合根注入；没有时自定义皮肤明确失败，不换成内置）。</summary>
        private readonly IPlayerSkinPort m_skinPort;
        /// <summary>玩家键 → 待建/重试中的本机角色（一个槽位一条）。</summary>
        private readonly Dictionary<PlayerKey, PendingPeer> m_pendingPeers =
            new Dictionary<PlayerKey, PendingPeer>();
        /// <summary>记录键 → 拥有它的玩家键（身份冲突检测：同一个 (身份, 槽位) 只能有一个角色）。</summary>
        private readonly Dictionary<string, PlayerKey> m_recordKeyOwner =
            new Dictionary<string, PlayerKey>(StringComparer.Ordinal);
        /// <summary>clientId → 身份（广播 AvatarRemove / 资料消息用；只在本机内存）。</summary>
        private readonly Dictionary<int, string> m_peerIdentities = new Dictionary<int, string>();
        /// <summary>主机侧已登记的本机角色槽位（clientId, slot → 资料/记录键）。</summary>
        private readonly Dictionary<PlayerKey, PeerSlot> m_peerSlots =
            new Dictionary<PlayerKey, PeerSlot>();
        /// <summary>已经上报过本机槽位的玩家端（旧构建永远不在里面 → 只收槽位 0）。</summary>
        private readonly HashSet<int> m_slotAwareClients = new HashSet<int>();
        /// <summary>主机：每个玩家端当前登记的槽位数（0 = 还没上报）。</summary>
        private readonly Dictionary<int, int> m_peerSlotCounts = new Dictionary<int, int>();
        /// <summary>限流日志：key → 上次写入的毫秒（Time.RealTime 口径）。</summary>
        private readonly Dictionary<string, long> m_lastRateLimitedLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>客户端：已经应用到引擎的角色资料签名（按玩家键，相同就不重复写引擎）。</summary>
        private readonly Dictionary<PlayerKey, string> m_appliedAvatarSignatures =
            new Dictionary<PlayerKey, string>();
        /// <summary>客户端远端化身名册（主机广播的玩家键集合；按 (身份,槽位) 去重）。</summary>
        private readonly AvatarRoster m_roster = new AvatarRoster();
        /// <summary>同一条持续性问题只报一次（原因照实写，不静默）。</summary>
        private readonly HashSet<string> m_reportedProblems = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>主机自己的本机角色资料签名（槽位 → 签名；变了才广播）。</summary>
        private readonly Dictionary<int, string> m_hostSlotSignatures = new Dictionary<int, string>();
        /// <summary>
        /// 编辑玩家屏同步的计数器（实例字段，无静态状态）。组合根把它同时交给
        /// <see cref="PlayerEditWatcher"/>，状态行 / 10 秒日志里读同一份数字。
        /// </summary>
        private readonly PlayerEditCounters m_editCounters = new PlayerEditCounters();
        /// <summary>
        /// 编辑玩家屏退出后，本机资料**每个槽位**最近一次发给主机的签名
        /// （槽位 0 同时写 <see cref="m_localProfileSignature"/> 保持旧口径）。
        /// </summary>
        private readonly Dictionary<int, string> m_localSlotProfileSignatures =
            new Dictionary<int, string>();
        /// <summary>自定义皮肤资源同步端口（组合根注入 PlayerSkinModule；没有时明确失败）。</summary>
        private IEditPlayerSkinSink m_editSkinSink;
        /// <summary>客户端：本机资料最近一次发给主机的签名（槽位 0；变了才发）。</summary>
        private string m_localProfileSignature = string.Empty;
        /// <summary>客户端：最近一次应用到本机玩家的资料签名。</summary>
        private string m_appliedSelfSignature = string.Empty;
        /// <summary>
        /// 客户端：主机在 HelloAck 里分配的资料快照已经应用过的签名。
        /// 加入时的快照**只按新快照应用一次**；不能每帧拿"本机资料存储 != 分配快照"当条件 ——
        /// 玩家在编辑屏改名/换肤后，下一帧就会被旧快照覆盖回去（实机 2026-10-21：
        /// nameChanged/skinChanged 恒为 0、108 的 profileSent 不涨）。
        /// 本机编辑由 108 / 编辑屏退出通道上报，主机更新记录后以 108 广播回来。
        /// </summary>
        private string m_appliedAssignedProfileSignature = string.Empty;
        /// <summary>客户端：真正应用了几次主机分配资料（重连 / 换世界各一次，不再每帧重放）。</summary>
        private long m_assignedProfileApplied;
        /// <summary>客户端：分配快照已应用过、但本机资料存储已有更新的编辑 → 保留本机值（计数）。</summary>
        private long m_assignedProfileKeptLocal;
        /// <summary>上一次因"保留本机编辑"而计数的资料签名（避免每帧 +1；同一份编辑只计一次）。</summary>
        private string m_keptLocalStoreSignature = string.Empty;
        /// <summary>客户端：最近一次上报的本机槽位集合签名（变了才发 LOCAL_PLAYER_SLOTS）。</summary>
        private string m_localSlotsSignature = string.Empty;
        /// <summary>
        /// 客户端：是否还需要把本机资料上报一次（首次应用主机分配的资料 / 编辑屏退出 / 重连后）。
        /// 不是"每帧轮询玩家资料"：只有这个标志为 true 时才抓一次资料，发成功即清掉。
        /// </summary>
        private bool m_profileSendPending;
        /// <summary>客户端：最近一次上报的本机槽位数（第一帧 / 槽位增删时用）。</summary>
        private int m_localSlotsCount;
        /// <summary>主机：当前本机角色槽位数（状态行 hostLocalSlots）。</summary>
        private int m_hostSlotsCount;
        /// <summary>主机：最近一次整表广播的签名（避免重复刷）。</summary>
        private string m_lastRosterSignature = string.Empty;
        /// <summary>
        /// 客户端：HelloAck 的"本机角色重新进入位置"已经应用到的 Project（每个下载世界只做一次；
        /// 重进地图 / 整图重下换 Project 会再应用一次）。null = 还没应用。
        /// </summary>
        private Project m_placementAppliedProject;
        private long m_placementApplied;
        private long m_placementPendingTicks;

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private IAvatarTerrainBridge m_terrainBridge;
        private IDownloadedWorldPort m_downloadedWorlds;
        private IPlayerProfileHandshakePort m_handshake;
        private IPeerBroadcastPort m_broadcast;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private Project m_project;
        private bool m_hostAvatarCreated;
        private bool m_profilePromptShown;
        private long m_lastRefreshMs;
        private long m_createdHostAvatars;
        private long m_createdClientAvatars;
        /// <summary>因为对端连接失效而摘掉的客户端角色数（状态行可见，实机排查用）。</summary>
        private long m_removedClientAvatars;
        /// <summary>同身份顶替次数（新连接取代旧连接）。</summary>
        private long m_identityTakeovers;
        /// <summary>因为睡醒/睡眠而立刻落盘角色记录（含复活点）的次数。</summary>
        private long m_recordSpawnPointSaved;
        /// <summary>
        /// 断线宽涨期（秒）：普通断线（网络掉线 / 闪退但主机还没判死）时角色**留在地图**一段时间，
        /// 期间同身份重连会顶替；显式退房（BYE）零宽限立即摘除（2026-10-26 实机口径）。
        /// 必须大于客户端 5s 自动重连间隔，留足重连时间。
        /// </summary>
        private const double DisconnectGraceSeconds = 20.0;

        private sealed class DeferredLeaveEntry
        {
            public int ClientId;
            public string Reason;
            public double DueTime;
        }

        /// <summary>已判失效但还在宽限期里的客户端 → 到期才摘角色（重连/顶替时提前摘）。</summary>
        private readonly Dictionary<int, DeferredLeaveEntry> m_deferredLeaves =
            new Dictionary<int, DeferredLeaveEntry>();
        private double m_lastTickNow;
        private long m_rejectedPeers;
        /// <summary>本端累计创建 / 更新 / 摘除的远端化身（主机与客户端都计入）。</summary>
        private long m_avatarsCreated;
        private long m_avatarsUpdated;
        private long m_avatarsRemoved;
        /// <summary>外观（名字/性别/皮肤/衣服）真正写进引擎的次数。</summary>
        private long m_avatarAppearanceApplied;
        /// <summary>成功解析出真实名字的身份数（每个身份一行日志）。</summary>
        private long m_avatarNamesResolved;
        /// <summary>没有可用名字的身份数（每次都有明确原因；绝不拿默认名字兜底）。</summary>
        private long m_avatarNameUnresolved;
        /// <summary>资料缺失 / 外观不可用（名字、皮肤、本地角色）的明确失败次数。</summary>
        private long m_avatarAppearanceUnavailable;
        /// <summary>客户端累计上报的本机槽位条目数（slotsAnnounced）。</summary>
        private long m_slotsAnnounced;
        /// <summary>主机当前登记的远端本机角色槽位数（slotsRegistered）。</summary>
        private int m_slotsRegistered;
        /// <summary>名册/槽位公告自相矛盾或无法映射的次数（rosterMismatch）。</summary>
        private long m_rosterMismatch;
        /// <summary>本机槽位超过 4 被截断 / 公告条数非法的次数（tooManySlots）。</summary>
        private long m_tooManySlots;
        /// <summary>按槽位 0 兼容旧构建发送的次数（legacyDefaultSlot）。</summary>
        private long m_legacyDefaultSlot;
        /// <summary>客户端：本地角色记录 hash 读取失败次数（Hello 将不申报任何已知记录 → 主机全量补发）。</summary>
        private long m_knownRecordHashReadFailures;
        /// <summary>客户端：最近一次 Hello 申报的已知角色记录条数（诊断；每次握手一行日志）。</summary>
        private long m_knownRecordHashesReported;
        /// <summary>主机：名册条目里成功带上 record_hash 的条数（0 = 记录缺失 / 旧数据）。</summary>
        private long m_rosterRecordHashes;
        /// <summary>
        /// 客户端：本次进程内已知的"角色记录键 → 不常变 hash"（来自主机名册 / 资料广播）。
        /// 重连时和本地 pak 副本里的 hash 一起上报（缓存优先：它比磁盘副本更新）。
        /// 不随名册清空而清（连接断了不代表这些记录内容变了）。
        /// </summary>
        private readonly Dictionary<string, ulong> m_knownRecordHashCache =
            new Dictionary<string, ulong>(StringComparer.Ordinal);

        // ---------------------------------------------------------------- Phase 2：客户端本机角色归一化
        // 下载世界里的 PlayersData 是**主机自己的角色**；客户端必须把它们当远端化身，而不是本机槽位。
        // 状态行（normalizedLocalPlayers / normalizedRemoved / normalizedKept / normalizeFailures /
        // normalizeSkippedNoWorld / normalizeSkippedNotClient ...）就是这一步是否真的执行过的唯一证据。

        /// <summary>正在归一化的 Project（换世界 / 整图重下 = 换 Project → 重新归一化）。</summary>
        private Project m_normalizeProject;
        /// <summary>这个 Project 已经归一化完成（幂等：每个世界安装只做一次）。</summary>
        private bool m_normalizeApplied;
        /// <summary>幂等跳过已经记过一次（状态行只计一次，不随 tick 增长）。</summary>
        private bool m_normalizeAlreadyLogged;
        /// <summary>上一次归一化失败原因（同一原因只计一次，便于状态行读数）。</summary>
        private string m_normalizeLastFailure = string.Empty;
        private bool m_normalizeNoPortLogged;
        private bool m_normalizeNoWorldLogged;
        private bool m_normalizeNotClientLogged;
        /// <summary>归一化时看到的本机角色总数（含保留与移除）。</summary>
        private long m_normalizeLocalPlayers;
        /// <summary>归一化从引擎里摘掉的下载来的主机角色副本数。</summary>
        private long m_normalizeRemoved;
        /// <summary>归一化保留的本机角色数（至少 1：本机自己那条）。</summary>
        private long m_normalizeKept;
        /// <summary>归一化失败（读不到角色 / 引擎移除失败 / 没有本机角色可判定）的次数。</summary>
        private long m_normalizeFailures;
        private long m_normalizeSkippedNoWorld;
        private long m_normalizeSkippedNotClient;
        private long m_normalizeSkippedAlready;
        /// <summary>因为超过引擎上限而明确拒绝的远端角色数（limited roster）。</summary>
        private long m_normalizeCapacityRefusals;
        /// <summary>名字/外观同时匹配主机槽位与本机资料的边界个数（显式处理，不静默二选一）。</summary>
        private long m_normalizeAmbiguous;
        /// <summary>创建远端化身时因为玩家槽位已满而明确拒绝的次数（每个玩家键只计一次）。</summary>
        private long m_avatarCapacityRefusals;
        /// <summary>已经因为容量被拒绝的玩家键（避免每帧重复计数 / 刷日志；容量恢复后移除）。</summary>
        private readonly HashSet<PlayerKey> m_capacityRefusedKeys = new HashSet<PlayerKey>();

        public PlayerAvatarModule(NetworkAvatarSpawner spawner, SuMultiplayerPlayersXml records,
            LocalPlayerProfileStore profileStore = null, IPlayerSkinPort skinPort = null)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_records = records ?? throw new ArgumentNullException(nameof(records));
            m_profileStore = profileStore;
            m_skinPort = skinPort;
            m_descriptor = new ModuleDescriptor("player-avatar", "联机角色")
            {
                // 建角色需要通道（拿对端身份/名字）与世界传输（判断"当前世界是不是下载来的联机世界"）。
                // 整条加入链缺一不可，所以都是硬依赖 —— 依赖不满足就直接 Unavailable，不假装能建。
                RequiresCapabilities = new[] { "transport", "world.transfer" },
                ProvidesCapabilities = new[] { "player.avatars" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        /// <summary>角色记录表（主机侧拥有；客户端只从握手拿身份，不读也不写这份文件）。</summary>
        public SuMultiplayerPlayersXml Records => m_records;

        /// <summary>编辑玩家屏计数器（组合根 / 状态行 / 自检读同一份）。</summary>
        public PlayerEditCounters EditCounters => m_editCounters;

        /// <summary>
        /// 自定义皮肤资源同步端口（由组合根在 PlayerSkinModule 建好后注入）。
        /// 不注入 = 自定义皮肤编辑会明确失败并计数，绝不静默换成内置皮肤。
        /// </summary>
        public IEditPlayerSkinSink EditSkinSink
        {
            get => m_editSkinSink;
            set => m_editSkinSink = value;
        }

        /// <summary>本端是否处在联机会话里（主机已建房 / 客户端已连上）。</summary>
        public bool IsInSession =>
            m_attached && (m_broadcast?.IsHosting == true || m_transport?.IsConnected == true);

        public void Attach(IModuleContext context)
        {
            m_context = context;
            m_attached = true;
            if (!context.TryGetPort(out m_transport))
            {
                Error("没有拿到传输端口（transport 能力缺失）→ 无法拿对端身份，联机角色不会创建");
            }
            else
            {
                m_transport.ClientJoined += HandleClientJoined;
                // 与 ClientJoined 成对：连接**已经不可达**时清掉这个客户端的联机角色。
                // 不接这条，角色实体会停在最后一个已应用的位置上（实机现象：主机侧客户端角色卡住不动），
                // 而且重连回来会因为"身份已被旧 clientId 占用"被拒绝建角色。
                m_transport.ClientLeft += HandleClientLeft;
                // 传输端口上同时挂着"联机角色 ↔ 地形通道"的桥：地形模块靠它判断哪些 PlayerData 是
                // 网络化身（旧实现用 m_networkPlayerData.Values 排除它们来找本机玩家），并把客户端
                // 上报的兴趣半径交回来。取不到就显式报错，绝不静默降级。
                m_terrainBridge = m_transport as IAvatarTerrainBridge;
                if (m_terrainBridge == null)
                {
                    Error("传输端口没有实现 IAvatarTerrainBridge（地形通道版本过旧？）→ " +
                        "主机不会按客户端上报的兴趣半径给联机角色挂引擎地形更新位置（客户端周围不会生成地形），" +
                        "地形模块也无法把网络化身从\"本机玩家\"判定里排除");
                }
                else
                {
                    m_terrainBridge.BindAvatarTerrainLink(this);
                }
            }
            if (!context.TryGetPort(out m_downloadedWorlds))
            {
                Error("没有拿到下载世界端口（world.transfer 缺失）→ 客户端无法判断当前世界是不是联机世界，" +
                    "不会创建主机角色（不做 index 兜底）");
            }
            // 握手资料视图（地形模块提供）：客户端据此建主机/其他客户端化身、判断是否要求先建角色。
            if (!context.TryGetPort(out m_handshake))
            {
                Error("没有拿到握手资料端口（IPlayerProfileHandshakePort）→ 客户端不会建主机化身、" +
                    "也无法响应主机的\"先创建角色\"要求（主机侧建角色不受影响）");
            }
            // 主机 → 每个客户端广播资料；拿不到就明确失败（客户端只能看到主机，看不到其他客户端）。
            if (!context.TryGetPort(out m_broadcast))
            {
                Error("没有拿到广播端口（IPeerBroadcastPort）→ 主机无法把\"房间里有哪些人、各自长什么样\"" +
                    "发给每个客户端（客户端之间互相看不到化身）");
            }
            if (context.TryGetPort(out m_messages))
            {
                RegisterMessages();
            }
            else
            {
                Error("没有拿到消息注册表（IMessageRegistryPort）→ 资料广播与离开通知无法收发" +
                    "（客户端之间互相看不到化身，离开也不会摘除）");
            }
            Info("联机角色模块就绪：主机按对端身份建客户端角色并把资料广播给所有客户端；" +
                "客户端按主机广播的身份建主机与其他客户端的化身");
        }

        private void RegisterMessages()
        {
            MessageRegistry registry = m_messages.Registry;
            // 重挂 / 模块热插拔：先摘掉本模块上一次的处理器（否则重复注册会抛）。
            registry.UnregisterOwner(m_descriptor.Id);
            registry.Register((int)Proto.MessageType.PlayerProfile, Proto.PlayerProfile.Parser,
                new DelegateMessageHandler<Proto.PlayerProfile>(OnPlayerProfileMessage),
                m_descriptor.Id);
            registry.Register((int)Proto.MessageType.AvatarRemove, Proto.AvatarRemove.Parser,
                new DelegateMessageHandler<Proto.AvatarRemove>(OnAvatarRemoveMessage),
                m_descriptor.Id);
            registry.Register((int)Proto.MessageType.AvatarRoster, Proto.AvatarRoster.Parser,
                new DelegateMessageHandler<Proto.AvatarRoster>(OnAvatarRosterMessage),
                m_descriptor.Id);
            registry.Register((int)Proto.MessageType.LocalPlayerSlots, Proto.LocalPlayerSlots.Parser,
                new DelegateMessageHandler<Proto.LocalPlayerSlots>(OnLocalPlayerSlotsMessage),
                m_descriptor.Id);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_lastTickNow = tick.Now;
            ProcessDeferredLeaves();
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                if (m_project != null)
                {
                    // 换了世界：旧世界的 PlayerData 已随 Project 销毁，必须丢掉引用。
                    Info("检测到 Project 更换：丢掉旧世界的联机角色引用（玩家端 " + m_spawner.Count +
                        " 个）；仍在挂起队列里的加入请求会照常在**新世界**里创建");
                    // 角色记录表按"世界实例"重建（不能只按目录名判断）：世界被删后在同一目录
                    // 名上重建时，旧记录绝不能漏进新世界（否则客户端会被当成已有角色、不弹创建界面）。
                    m_records.OnProjectChanged();
                    m_spawner.ForgetAll();
                    m_roster.Clear();
                    m_appliedAvatarSignatures.Clear();
                    m_appliedSelfSignature = string.Empty;
                    m_localSlotProfileSignatures.Clear();
                    m_lastRosterSignature = string.Empty;
                    m_localProfileSignature = string.Empty;
                    m_localSlotsSignature = string.Empty;
                    m_localSlotsCount = 0;
                    m_hostSlotSignatures.Clear();
                }
                m_project = project;
                // 世界切换：客户端资料缓存按世界隔离（不同世界 = 不同文件夹，信息不迁移）。
                // 同一世界内的重连不触发（Project 引用没变），主机分配资料会照常重新应用。
                string newWorldDirectory = project?.FindSubsystem<SubsystemGameInfo>(false)
                    ?.DirectoryName ?? string.Empty;
                if (!string.Equals(newWorldDirectory, m_profileWorldDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    string previousWorldDirectory = m_profileWorldDirectory;
                    m_profileWorldDirectory = newWorldDirectory;
                    if (m_profileStore.HasProfile)
                    {
                        Info("世界切换（\"" + (previousWorldDirectory.Length == 0
                                ? "(无世界)" : previousWorldDirectory) + "\" → \"" +
                            (newWorldDirectory.Length == 0 ? "(无世界)" : newWorldDirectory) +
                            "\"）→ 清空本机资料缓存：客户端角色按世界隔离，" +
                            "换世界不再沿用上一张图的名字/外观（要角色由新世界主机分配或重新创建）");
                        m_profileStore.Clear();
                    }
                }
                m_hostAvatarCreated = false;
                m_profilePromptShown = false;
                m_reportedProblems.Clear();
                m_lastRefreshMs = 0;
                m_profileSendPending = false;
                // Phase 2：换 Project = 换世界安装（重连整图重下 / 重新加入）→ 归一化必须重新做一次。
                m_normalizeApplied = false;
                m_normalizeAlreadyLogged = false;
                m_normalizeLastFailure = string.Empty;
                m_normalizeNoPortLogged = false;
                m_normalizeNoWorldLogged = false;
                m_normalizeNotClientLogged = false;
                m_capacityRefusedKeys.Clear();
            }
            // 客户端在**还没有 Project**（Play 屏等世界下载）时也可能收到 profile_required：
            // 必须在世界检查之前处理，否则会一直等一个永远不会下发的世界。
            if (m_transport != null)
            {
                int localClientId = m_transport.LocalClientId;
                if (localClientId < 0)
                {
                    // 新连接已经建立但 HelloAck 还没到（LocalClientId = -1）：上一轮的名册属于旧连接，
                    // 必须先清空 —— 否则旧 clientId 的化身会留在名册里，重连后多出一个。
                    // （收到新 HelloAck 后再按新连接的名册重建；主机在 ClientJoined 时会重发整表。）
                    if (m_roster.LocalClientId >= 0)
                    {
                        Info("重新握手（本机通道编号 " + m_roster.LocalClientId +
                            " → 未分配）：清空上一轮名册，等主机按新连接重新广播身份");
                        m_roster.Clear();
                        m_appliedAvatarSignatures.Clear();
                        m_localSlotsSignature = string.Empty;
                        m_localSlotsCount = 0;
                    }
                    m_roster.LocalClientId = -1;
                }
                else if (m_roster.LocalClientId != localClientId)
                {
                    m_roster.LocalClientId = localClientId;
                    SeedSelfFlagFromRoster();
                    // 新连接 = 主机还不知道我是谁：下一次 TickClient 发一次本机资料（事件驱动）。
                    m_profileSendPending = true;
                }
            }
            if (!tick.IsHost) HandleProfileRequired();
            if (tick.IsHost) TickHost(project, tick.Now);
            else TickClient(project);
            // 远端角色的位置插值/平滑：两端都每帧推进（旧实现主机 :1285 / 客户端 :1299 都每帧做）。
            // 必须放在这里（帧末尾）——引擎物理本帧已经把身体推过一遍，这一步再把身体拉回权威轨迹。
            if (project != null) m_spawner.UpdateRemotePresentations(tick.DeltaTime);
        }

        // ---------------------------------------------------------------- 主机侧

        private void TickHost(Project project, double now)
        {
            if (project == null) return;    // 主机还没进世界：请求留在挂起队列里（旧实现同样挂起）
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(false);
            m_records.EnsureLoaded(gameInfo?.DirectoryName, isHost: true);
            CreatePendingHostAvatars(now);
            // 旧实现 MaintainRemoteTerrainLocations 的主机分支（旧实现每帧调用，位置在加入处理之后：
            // Modules/Runtime/ScMultiplayerUpdateLoop.cs:1303）：给每个联机角色的 PlayerIndex 挂引擎
            // 地形更新位置，半径用玩家端上报的兴趣半径（没上报过 → 主机可见范围算出的默认半径）。
            m_spawner.ApplyTerrainUpdateLocations(isHost: true);
            RefreshHostRecords(now);
            // 主机本机角色的名字/皮肤变化不再每秒轮询：由 Engine/PlayerEditWatcher 在
            // PlayerScreen 退出时调用 OnEditScreenExited（编辑器之外不会改名字/皮肤）。
        }

        /// <summary>
        /// 主机：一个玩家端加入（已由传输模块过滤掉 Debug 只读端）。
        /// 这里做**身份冲突检测**：绝不用 index 兜底，也绝不静默把两个人合并到一条记录。
        /// </summary>
        private void HandleClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!m_attached) return;
            string trimmedIdentity = (identity ?? string.Empty).Trim();
            string localIdentity = PlayerIdentity.LocalIdentity();
            if (trimmedIdentity.Length == 0)
            {
                // 身份不可用：允许退到按名字绑定，但必须写明原因（不许静默）。
                Error("对端 clientId=" + clientId + "（名字 " + (name ?? string.Empty) +
                    "）的身份不可用：Hello 没有携带身份（UserId.dat 为空或对端是旧构建）→ " +
                    "本角色按名字绑定（身份不可用，按名字绑定；不能保证是同一个人）");
            }
            else if (string.Equals(trimmedIdentity, localIdentity, StringComparison.Ordinal))
            {
                m_rejectedPeers++;
                Error("拒绝创建角色：clientId=" + clientId + " 的身份与本机身份相同（" +
                    PlayerIdentity.Shorten(trimmedIdentity) +
                    "）—— 同一个 UserId 不能同时既是主机又是客户端；不建第二个角色（绝不静默合并）");
                return;
            }

            PlayerKey key = new PlayerKey(clientId, 0);
            string recordKey = PlayerIdentity.RecordKey(trimmedIdentity, name, key.Slot);
            if (m_recordKeyOwner.TryGetValue(recordKey, out PlayerKey owner) && owner != key)
            {
                // 2026-10-26 同身份顶替：同一个 UserId 只可能是同一个人。新连接出现就意味着
                // 旧连接已经失效（闪退 / 掉线但主机还没判死）。旧实现直接拒绝新角色，实机结果是
                // 旧角色无人摘、新角色建不出来（幽灵角色 + 进不了房）。这里先摘旧角色、释放记录键，
                // 再让新连接正常建角色（绝不静默合并两个角色）。
                Warn("同身份顶替：clientId=" + clientId + "（名字 " + (name ?? string.Empty) +
                    "）的身份 " + PlayerIdentity.Describe(recordKey) + " 仍归旧连接 clientId=" +
                    owner.ClientId + "（旧连接未被判死 / 可能闪退）→ 立刻摘掉旧角色、释放记录键，" +
                    "再为新连接建角色");
                m_deferredLeaves.Remove(owner.ClientId);
                m_identityTakeovers++;
                RemoveClientAvatarsNow(owner.ClientId,
                    "同身份顶替（新连接 clientId=" + clientId + "）", "同身份顶替");
            }
            m_recordKeyOwner[recordKey] = key;
            m_peerIdentities[clientId] = trimmedIdentity;
            // 新连接 = 还没上报本机槽位；旧构建永远不会上报，按单角色（槽位 0）兼容。
            m_slotAwareClients.Remove(clientId);
            m_peerSlotCounts[clientId] = 0;
            PlayerProfileSnapshot profile = GetPeerProfile(key);
            string resolvedName = profile != null && profile.HasName ? profile.Name : (name ?? string.Empty);
            // 同一个身份只写一行名字解析（rate-limited by identity；不含 GUID 全值）。
            ResolveName(recordKey, resolvedName, "Hello/主机角色记录");
            m_pendingPeers[key] = new PendingPeer
            {
                Key = key,
                Name = resolvedName,
                Identity = trimmedIdentity,
                RecordKey = recordKey,
                Profile = profile,
                LastAttemptMs = 0
            };
            PeerSlot slot = EnsurePeerSlot(key);
            slot.Identity = trimmedIdentity;
            slot.Name = resolvedName;
            slot.RecordKey = recordKey;
            Info("玩家端加入：clientId=" + clientId + " · 名字=" + resolvedName +
                " · 身份=" + (trimmedIdentity.Length == 0
                    ? "(不可用，按名字绑定)"
                    : PlayerIdentity.Shorten(trimmedIdentity)) +
                " · 记录键=" + PlayerIdentity.Describe(recordKey) +
                (rejoin ? " · 该端声明已有这个世界（重连；世界传输不再重发整图）" : string.Empty));
            // 新客户端进来：把主机 + 现有所有人的资料广播一遍（它才能在加载完世界后建出所有化身）。
            // 此时它自己的 Avatar 还没建好；建好后 CreatePendingHostAvatars 会再广播它自己的资料。
            BroadcastRoster("新客户端加入", force: true);
        }

        /// <summary>
        /// 主机：一个玩家端的连接**已经不可达**（对端关闭 / 链路失效 / 协议违规）——
        /// 把它的联机角色从世界里摘掉，并把记录键的所有权释放（否则它重连回来会被
        /// "身份已被占用"拒绝建角色）。角色记录先抓一份落盘，进度不丢。
        /// 旧实现对应 <c>RemoveNetworkPlayer</c>（Source: ScMultiplayerWorldObjectHandlers.cs:348-427）；
        /// 差异：旧实现的"断线挂起、重连复用同一身份"那条链仍未移植，这里按"断开即移除"处理。
        /// </summary>
        private void HandleClientLeft(int clientId, string reason)
        {
            if (!m_attached) return;
            // 显式退房（客户端发 BYE）= 零宽限立即摘；普通断线 = 保留 DisconnectGraceSeconds
            // 让"只是断连 / 闪退重进"有机会同身份重连，避免角色被太早摘掉（用户口径 2026-10-26）。
            bool explicitLeave = !string.IsNullOrEmpty(reason) &&
                reason.Contains("Bye", StringComparison.Ordinal);
            if (!explicitLeave)
            {
                // 2026-10-27：宽限期间角色实体还活着，但**离线位置必须马上落盘**：
                // 否则主机崩溃 / 宽限期内重进会读到上一次 5 秒刷新前的旧位置，
                // 客户端再次进入就不会出现在"最后离线位置"。
                CaptureClientRecordsNow(clientId, "断线宽限：先保存最后离线位置");
                m_deferredLeaves[clientId] = new DeferredLeaveEntry
                {
                    ClientId = clientId,
                    Reason = reason,
                    DueTime = m_lastTickNow + DisconnectGraceSeconds
                };
                Warn("玩家端 clientId=" + clientId + " 的通道已失效（" + (reason ?? string.Empty) +
                    "）→ 角色保留 " + (int)DisconnectGraceSeconds + " 秒宽限（同身份重连会顶替；" +
                    "显式退房发 BYE 才会立即摘除）");
                return;
            }
            RemoveClientAvatarsNow(clientId, reason, "显式退房（BYE）");
        }

        /// <summary>
        /// 只把该连接的**角色记录抓一份落盘**，不摘化身、不释放记录键（宽限期间仍需保留）。
        /// 断线第一时间调用：保证 SuMultiplayerPlayers.xml 里是该角色最后离线位置/复活点。
        /// </summary>
        private void CaptureClientRecordsNow(int clientId, string why)
        {
            int captured = 0;
            foreach (KeyValuePair<string, PlayerKey> pair in m_recordKeyOwner.ToArray())
            {
                if (pair.Value.ClientId != clientId) continue;
                m_records.TryGet(pair.Key, out NetworkPlayerRecord previous);
                NetworkPlayerRecord record = CaptureForStorage(pair.Value, previous);
                if (record == null) continue;
                m_records.Put(pair.Key, record);
                captured++;
            }
            if (captured > 0)
            {
                m_records.SaveIfDirty();
                Warn("clientId=" + clientId + " 断线：已立刻保存 " + captured +
                    " 份角色记录（最后离线位置 / 最后睡觉复活点已写 SuMultiplayerPlayers.xml）· " +
                    (why ?? string.Empty));
            }
        }

        /// <summary>宽限期到期 → 真正摘掉角色（普通断线路径的唯一摘除点）。</summary>
        private void ProcessDeferredLeaves()
        {
            if (m_deferredLeaves.Count == 0) return;
            List<int> expired = null;
            foreach (KeyValuePair<int, DeferredLeaveEntry> pair in m_deferredLeaves)
            {
                if (m_lastTickNow < pair.Value.DueTime) continue;
                if (expired == null) expired = new List<int>();
                expired.Add(pair.Key);
            }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++)
            {
                int clientId = expired[i];
                if (!m_deferredLeaves.TryGetValue(clientId, out DeferredLeaveEntry entry)) continue;
                m_deferredLeaves.Remove(clientId);
                RemoveClientAvatarsNow(clientId, entry.Reason,
                    "断线宽限期到（" + (int)DisconnectGraceSeconds + " 秒未重连）");
            }
        }

        private void RemoveClientAvatarsNow(int clientId, string reason, string why)
        {
            if (!m_attached) return;
            m_deferredLeaves.Remove(clientId);
            m_peerIdentities.TryGetValue(clientId, out string identity);
            // 这个连接上的**所有槽位**：先抓记录落盘，再释放记录键，最后摘化身。
            var removedKeys = new List<PlayerKey>(PlayerKey.SlotCount);
            foreach (KeyValuePair<PlayerKey, PendingPeer> peer in m_pendingPeers.ToArray())
                if (peer.Key.ClientId == clientId) m_pendingPeers.Remove(peer.Key);
            foreach (KeyValuePair<string, PlayerKey> pair in m_recordKeyOwner.ToArray())
            {
                if (pair.Value.ClientId != clientId) continue;
                // 先抓一份还活着的角色记录再摘实体（摘掉之后就读不到了）。
                m_records.TryGet(pair.Key, out NetworkPlayerRecord previous);
                NetworkPlayerRecord captured = CaptureForStorage(pair.Value, previous);
                if (captured != null)
                {
                    m_records.Put(pair.Key, captured);
                    m_records.SaveIfDirty();
                }
                m_recordKeyOwner.Remove(pair.Key);
                removedKeys.Add(pair.Value);
            }
            foreach (KeyValuePair<PlayerKey, PeerSlot> pair in m_peerSlots.ToArray())
            {
                if (pair.Key.ClientId != clientId) continue;
                if (!removedKeys.Contains(pair.Key)) removedKeys.Add(pair.Key);
                m_peerSlots.Remove(pair.Key);
            }
            bool removed = false;
            for (int slot = 0; slot < removedKeys.Count; slot++)
            {
                PlayerKey key = removedKeys[slot];
                if (!m_spawner.Remove(key)) continue;
                removed = true;
                m_avatarsRemoved++;
                m_appliedAvatarSignatures.Remove(key);
            }
            // 防御：登记表与化身表不一致时也把残留槽位摘干净（绝不留下幽灵化身）。
            foreach (PlayerKey key in m_spawner.Keys.ToArray())
            {
                if (key.ClientId != clientId) continue;
                if (!m_spawner.Remove(key)) continue;
                removed = true;
                m_avatarsRemoved++;
                m_appliedAvatarSignatures.Remove(key);
            }
            if (removed) m_removedClientAvatars++;
            m_peerIdentities.Remove(clientId);
            m_slotAwareClients.Remove(clientId);
            m_peerSlotCounts.Remove(clientId);
            m_slotsRegistered = CountRegisteredSlots();
            Warn("玩家端 clientId=" + clientId + " 的通道已失效（" + (reason ?? string.Empty) +
                "）· " + (why ?? string.Empty) + " → " + (removed
                    ? "已把它的 " + Math.Max(1, removedKeys.Count) + " 个本机角色从世界里摘掉（否则它们会停在最后一个已应用的位置上，重连后还会多出来）"
                    : "本机没有它的联机角色（本来就没建起来，无需摘除）") +
                (removedKeys.Count > 0
                    ? "；角色记录已保存并释放 " + removedKeys.Count + " 个记录键（重连可重新建角色）"
                    : string.Empty));
            // 通知**所有剩下的客户端**：把它从名册里摘掉（否则别人世界里会留下一个不动的化身）。
            for (int i = 0; i < removedKeys.Count; i++)
                BroadcastAvatarRemove(removedKeys[i], identity, reason);
            BroadcastRoster("客户端离开");
        }

        private void CreatePendingHostAvatars(double now)
        {
            if (m_pendingPeers.Count == 0) return;
            long nowMs = (long)(now * 1000.0);
            foreach (PendingPeer peer in m_pendingPeers.Values.ToArray())
            {
                if (nowMs - peer.LastAttemptMs < CreateRetryIntervalMs) continue;
                peer.LastAttemptMs = nowMs;
                if (!m_records.TryGet(peer.RecordKey, out NetworkPlayerRecord record))
                {
                    if (peer.Profile == null || !IsCompleteProfile(peer.Profile))
                    {
                        // 资料闸门已经要求先建角色；这里绝不拿默认值伪造一个角色记录。
                        ErrorOnce("record-missing:" + peer.Key,
                            "玩家键 " + peer.Key + " 没有角色记录且资料不完整（" +
                            PlayerIdentity.Describe(peer.RecordKey) + "）→ 不建角色；" +
                            "客户端会收到 profile_required 并先创建角色");
                        continue;
                    }
                    record = CreateInitialPlayerRecord(peer.Profile);
                    m_records.Put(peer.RecordKey, record);
                    m_records.SaveIfDirty();
                    Info("为身份 " + PlayerIdentity.Describe(peer.RecordKey) +
                        " 建了新的角色记录（名字 " + record.Name + " · 性别 " + record.PlayerClass +
                        " · 皮肤 " + record.SkinName + "；资料来自客户端 Hello.profile）");
                }
                if (!m_spawner.TryCreate(peer.Key, record.Name, peer.RecordKey, record, isHost: true,
                    out string reason))
                {
                    // 明确失败：保留在挂起队列里按间隔重试，并写清原因（不假装成功）。
                    // 10 秒限流一条（引擎玩家槽位满时会每 2 秒重试一次，不能刷屏）。
                    ErrorRateLimited("avatar-pending:" + peer.Key,
                        "创建客户端角色失败（保留在挂起队列按 " + CreateRetryIntervalMs +
                        " ms 重试）：玩家键 " + peer.Key + " · 记录键=" +
                        PlayerIdentity.Describe(peer.RecordKey) + " · 原因=" + reason);
                    continue;
                }
                m_createdClientAvatars++;
                m_avatarsCreated++;
                if (!string.IsNullOrEmpty(record.SkinName)) m_avatarAppearanceApplied++;
                NetworkPlayerRecord captured = CaptureForStorage(peer.Key, record);
                if (captured != null) m_records.Put(peer.RecordKey, captured);
                m_records.SaveIfDirty();
                m_pendingPeers.Remove(peer.Key);
                PeerSlot peerSlot = EnsurePeerSlot(peer.Key);
                peerSlot.Profile = RecordToProfile(captured ?? record);
                if (string.IsNullOrEmpty(peerSlot.RecordKey)) peerSlot.RecordKey = peer.RecordKey;
                Info("已创建客户端角色：玩家键 " + peer.Key + " · 名字=" + record.Name +
                    " · 性别=" + record.PlayerClass + " · 皮肤=" + record.SkinName +
                    " · 记录键=" + PlayerIdentity.Describe(peer.RecordKey));
                // 新角色建好 → 让所有客户端（含刚加入的那个）都拿到他的真实资料。
                BroadcastProfile(peer.Key, peerSlot.Profile, isHost: false);
                BroadcastRoster("客户端角色已创建");
            }
        }

        /// <summary>
        /// 主机：每 5 秒把每个联机角色的当前位置/生命/背包抓进记录并落盘。
        /// Source: ScMultiplayerUpdateLoop.cs:2999-3003（RefreshHostPlayerRecords + SavePlayerRecords）
        /// 资料（名字/性别/皮肤/衣服）变化时立即广播 —— 客户端据此更新化身外观。
        /// </summary>
        private void RefreshHostRecords(double now)
        {
            long nowMs = (long)(now * 1000.0);
            if (nowMs - m_lastRefreshMs < RecordRefreshIntervalMs) return;
            m_lastRefreshMs = nowMs;
            bool changed = false;
            foreach (PlayerKey key in m_spawner.Keys.ToArray())
            {
                if (!m_spawner.TryGetRecordKey(key, out string recordKey)) continue;
                m_records.TryGet(recordKey, out NetworkPlayerRecord previous);
                NetworkPlayerRecord captured = CaptureForStorage(key, previous);
                if (captured == null) continue;
                string previousSignature = previous != null
                    ? RecordToProfile(previous).Signature
                    : string.Empty;
                string capturedSignature = RecordToProfile(captured).Signature;
                m_records.Put(recordKey, captured);
                changed = true;
                PeerSlot peer = EnsurePeerSlot(key);
                peer.RecordKey = recordKey;
                peer.Profile = RecordToProfile(captured);
                if (!string.Equals(previousSignature, capturedSignature, StringComparison.Ordinal))
                {
                    m_avatarsUpdated++;
                    Info("联机角色资料变化：玩家键 " + key + " · 名字=" + captured.Name +
                        " · 性别=" + captured.PlayerClass + " · 皮肤=" + captured.SkinName +
                        " → 广播给所有客户端");
                    BroadcastProfile(key, peer.Profile, isHost: false);
                    BroadcastRoster("客户端资料变化");
                }
            }
            if (changed) m_records.SaveIfDirty();
        }

        /// <summary>
        /// 主机：**每个本机角色**（分屏槽位 0..N-1）的资料变化 → 广播给所有客户端，并维护整表名册。
        /// 新增一个本机角色 = 名册里多一条 (0, slot)；移除一个 = 名册里少一条并显式发 AvatarRemove。
        /// **不再由每帧/每秒轮询触发**：只在编辑玩家屏退出（PlayerEditWatcher 检测到屏幕切换）时调用。
        /// </summary>
        private void SyncHostLocalSlots(string why)
        {
            var localPlayers = new List<PlayerData>(PlayerKey.SlotCount);
            if (!m_spawner.TryCollectLocalPlayers(GameManager.Project, localPlayers,
                    out bool tooMany, out string reason))
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("host-profile-unavailable",
                    "主机自己的资料取不到（" + (reason ?? "没有本机角色") + "）→ 客户端会明确记录\"主机外观未知\"，" +
                    "不会伪造性别/皮肤（检查主机是否已经进入世界并有本机角色）");
                return;
            }
            m_hostSlotsCount = localPlayers.Count;
            if (localPlayers.Count == 0)
            {
                // 一个本机角色都不剩（死亡/退到角色选择）：下面按 alive 为空把已公告的槽位全部收回。
                m_avatarAppearanceUnavailable++;
                ErrorOnce("host-profile-empty",
                    "主机当前没有本机角色（" + (reason ?? "PlayersData 为空") +
                    "）→ 收回已公告的主机槽位（客户端会摘掉对应化身）");
            }
            if (tooMany)
            {
                m_tooManySlots++;
                LogRateLimited("host-slots-too-many", reason);
            }
            bool changed = false;
            var alive = new HashSet<int>();
            for (int slot = 0; slot < localPlayers.Count; slot++)
            {
                PlayerProfileSnapshot profile = m_spawner.CaptureProfile(localPlayers[slot],
                    out string captureReason);
                if (profile == null)
                {
                    m_avatarAppearanceUnavailable++;
                    ErrorOnce("host-slot-profile:" + slot,
                        "主机本机槽位 " + slot + " 的资料取不到（" + captureReason +
                        "）→ 这个槽位暂时不广播（客户端保留上一份外观，绝不伪造）");
                    continue;
                }
                alive.Add(slot);
                string signature = profile.Signature;
                if (m_hostSlotSignatures.TryGetValue(slot, out string previous) &&
                    string.Equals(previous, signature, StringComparison.Ordinal))
                    continue;
                m_hostSlotSignatures[slot] = signature;
                changed = true;
                PlayerKey key = new PlayerKey(0, slot);
                ResolveName(PlayerIdentity.RecordKey(PlayerIdentity.LocalIdentity(), profile.Name, slot),
                    profile.Name, "主机本机角色 PlayerData.Name（槽位 " + slot + "）");
                Info("主机本机角色资料（槽位 " + slot + "：名字=" + profile.Name + " · 性别=" +
                    profile.Appearance.Sex + " · 皮肤=" + profile.Appearance.SkinName +
                    "）已变化 → 广播给所有客户端");
                BroadcastProfile(key, profile, isHost: true,
                    identity: PlayerIdentity.LocalIdentity());
            }
            // 本机角色被移除（槽位消失）→ 立即发离开通知（名册随后整表对齐）。
            foreach (int slot in m_hostSlotSignatures.Keys.ToArray())
            {
                if (alive.Contains(slot)) continue;
                m_hostSlotSignatures.Remove(slot);
                changed = true;
                PlayerKey key = new PlayerKey(0, slot);
                Info("主机本机槽位 " + slot + " 消失 → 广播离开通知（客户端会摘掉这个化身）");
                BroadcastAvatarRemove(key, PlayerIdentity.LocalIdentity(),
                    "主机移除了本机角色槽位 " + slot);
            }
            if (changed) BroadcastRoster("主机本机角色变化：" + (why ?? string.Empty));
        }

        // ---------------------------------------------------------------- 资料闸门（IHelloProfileGate）

        /// <summary>
        /// 主机：一次 Hello 的资料裁决。**这是首次加入"没有角色 → 先创建角色"的唯一入口。**
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerHealthAndIngress.cs:941-961
        ///         （m_playerRecords.TryGetValue(recordKey) → 有记录直接用；
        ///          没有记录且 !IsValidRequestedProfile → RefuseJoinGame(SCMP_PROFILE_REQUIRED)）
        /// </summary>
        public HelloProfileDecision AcceptHello(int clientId, string identity, string name,
            PlayerProfileSnapshot claimedProfile, bool rejoin)
        {
            var decision = new HelloProfileDecision();
            Project project = GameManager.Project;
            if (project == null)
            {
                decision.ProfileRequired = true;
                decision.Reason = "主机还没有加载世界（Project 为空）→ 无法确认/创建角色";
                return decision;
            }
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(false);
            m_records.EnsureLoaded(gameInfo?.DirectoryName, isHost: true);
            string trimmedIdentity = (identity ?? string.Empty).Trim();
            string recordKey = PlayerIdentity.RecordKey(trimmedIdentity, name);
            decision.RecordKey = recordKey;
            if (m_records.TryGet(recordKey, out NetworkPlayerRecord record))
            {
                // 老玩家：资料以主机记录为准（客户端不能借重连改名/改性别）。
                // 例外（显式迁移）：旧构建把引擎默认名 "Android User" 当角色名写进了记录，
                // 这样的记录**不代表真实资料** —— 要求客户端走一次角色创建，再用它提交的资料覆盖。
                PlayerProfileSnapshot recordProfile = RecordToProfile(record);
                bool unconfirmedName = !record.NameConfirmed;
                bool missingAppearance = !IsUsableAppearance(recordProfile);
                if (unconfirmedName || missingAppearance)
                {
                    if (claimedProfile != null && IsCompleteProfile(claimedProfile))
                    {
                        ApplyProfileToRecord(record, claimedProfile);
                        record.NameConfirmed = true;
                        m_records.Put(recordKey, record);
                        m_records.SaveIfDirty();
                        recordProfile = RecordToProfile(record);
                        Info("身份 " + PlayerIdentity.Describe(recordKey) +
                            (unconfirmedName
                                ? " 的角色记录是旧构建的默认名字（引擎显示名）→ 已用客户端 Hello.profile 迁移为真实资料"
                                : " 的旧角色记录缺少皮肤/性别资料 → 已用客户端 Hello.profile 补齐") +
                            "（名字 " + record.Name + " · 性别 " + record.PlayerClass +
                            " · 皮肤 " + record.SkinName + "）");
                        unconfirmedName = false;
                        missingAppearance = false;
                    }
                    else if (unconfirmedName)
                    {
                        // 旧构建的默认名字：必须重新创建一次角色（不拿 "Android User" 当真实名字）。
                        decision.ProfileRequired = true;
                        decision.HasCharacter = false;
                        decision.Reason = "角色记录没有确认过真实资料（旧构建把引擎默认显示名当名字写入）：" +
                            "需要重新创建一次角色以取得真实姓名/性别/皮肤";
                        m_avatarAppearanceUnavailable++;
                        Warn("身份 " + PlayerIdentity.Describe(recordKey) +
                            " 的角色记录未确认过真实资料（名字 " + record.Name +
                            "）→ 要求客户端重新创建角色（一次显式迁移，不把默认名当真实资料）");
                        return decision;
                    }
                    else
                    {
                        m_avatarAppearanceUnavailable++;
                        ErrorOnce("record-appearance:" + recordKey,
                            "身份 " + PlayerIdentity.Describe(recordKey) +
                            " 的角色记录没有皮肤（SkinName 为空），客户端也没带完整资料 → " +
                            "客户端会明确记录\"外观未知\"（不伪造皮肤/性别）");
                    }
                }
                PeerSlot helloSlot = EnsurePeerSlot(new PlayerKey(clientId, 0));
                helloSlot.Profile = recordProfile;
                helloSlot.Identity = trimmedIdentity;
                helloSlot.RecordKey = recordKey;
                m_peerIdentities[clientId] = trimmedIdentity;
                decision.HasCharacter = true;
                decision.Name = record.Name ?? string.Empty;
                decision.Profile = recordProfile;
                decision.AssignedSkinAssetPresent = HasStoredSkinAsset(record);
                // 2026-10-27：把该客户端角色记录里的"最后离线位置 + 最后睡觉复活点"带给客户端。
                // 客户端本地角色是下载世界里的新实体（Players 段是主机存档角色），不带上这份
                // 记录位置就会出生在主机角色旁边（实机 bug）。
                Vector3 recordPosition = record.Position;
                Vector3 recordSpawn = record.SpawnPosition;
                if (IsFiniteVector(recordPosition))
                {
                    if (!IsFiniteVector(recordSpawn) || recordSpawn == Vector3.Zero)
                        recordSpawn = recordPosition;
                    decision.HasPlacement = true;
                    decision.Position = new PlayerPlacement(recordPosition.X, recordPosition.Y,
                        recordPosition.Z);
                    decision.SpawnPosition = new PlayerPlacement(recordSpawn.X, recordSpawn.Y,
                        recordSpawn.Z);
                }
                decision.Reason = "已有角色记录（重连 / 老玩家）";
                return decision;
            }

            // 没有记录：必须有客户端提交的完整资料，否则要求先创建角色（旧 SCMP_PROFILE_REQUIRED）。
            if (claimedProfile == null || !IsCompleteProfile(claimedProfile))
            {
                decision.ProfileRequired = true;
                decision.HasCharacter = false;
                decision.Reason = claimedProfile == null
                    ? "主机没有这个身份的角色记录，Hello 也没有携带资料（首次加入）"
                    : "主机没有这个身份的角色记录，Hello 携带的资料不完整/非法";
                return decision;
            }
            record = CreateInitialPlayerRecord(claimedProfile);
            m_records.Put(recordKey, record);
            m_records.SaveIfDirty();
            PlayerProfileSnapshot profile = RecordToProfile(record);
            PeerSlot createdSlot = EnsurePeerSlot(new PlayerKey(clientId, 0));
            createdSlot.Profile = profile;
            createdSlot.Identity = trimmedIdentity;
            createdSlot.RecordKey = recordKey;
            m_peerIdentities[clientId] = trimmedIdentity;
            decision.HasCharacter = false;
            decision.Name = record.Name;
            decision.Profile = profile;
            decision.Reason = "按客户端 Hello.profile 新建角色记录（首次加入）";
            Info("身份 " + PlayerIdentity.Describe(recordKey) + " 首次加入 → 用 Hello.profile 建角色记录" +
                "（名字 " + record.Name + " · 性别 " + record.PlayerClass + " · 皮肤 " + record.SkinName + "）");
            return decision;
        }

        /// <summary>位置是否可用于出生（有限值；Vector3.Zero 视为没有记录，不当作有效坐标）。</summary>
        private static bool IsFiniteVector(Vector3 value) =>
            !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
            !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
            !float.IsNaN(value.Z) && !float.IsInfinity(value.Z) &&
            value != Vector3.Zero;

        /// <summary>
        /// 主机：某角色的睡眠复活点变了（引擎 ComponentSleep.WakeUp/Sleep 已写入
        /// PlayerData.SpawnPosition）→ 立刻把该角色记录抓一份写进 SuMultiplayerPlayers.xml。
        /// 记录里带 Position 与 SpawnPosition；**绝不写 Project.xml**。
        /// </summary>
        public void NoteAuthoritySpawnPointChanged(PlayerKey key)
        {
            if (!m_attached) return;
            if (!m_spawner.TryGetRecordKey(key, out string recordKey)) return;
            if (!m_records.TryGet(recordKey, out NetworkPlayerRecord previous)) return;
            NetworkPlayerRecord captured = CaptureForStorage(key, previous);
            if (captured == null) return;
            m_records.Put(recordKey, captured);
            m_records.SaveIfDirty();
            m_recordSpawnPointSaved++;
            Info("角色记录已保存最后睡觉复活点：玩家键 " + key + " · 记录键 " +
                PlayerIdentity.Describe(recordKey) + " · 复活点 " +
                DescribePlacement(new PlayerPlacement(captured.SpawnPosition.X,
                    captured.SpawnPosition.Y, captured.SpawnPosition.Z)) +
                "（写 SuMultiplayerPlayers.xml，不写 Project.xml）");
        }

        // ---------------------------------------------------------------- 复活点表端口（ISpawnPointProvider）

        /// <summary>
        /// 主机：抓“主机本地角色 + 全部在线联机角色”的复活点表（PLAYER_SPAWN 的权威来源）。
        /// 主机本地角色的 SpawnPosition 继续以 Project.xml 为权威，本方法只读运行期值。
        /// 返回 -1 = 世界/角色暂不可用（调用方不能把 0 当成“全部离线”发删除）。
        /// </summary>
        public int CaptureOnlineSpawnPoints(List<OnlineSpawnPoint> target, out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "调用方没有给复活点表容器";
                return -1;
            }
            target.Clear();
            if (m_broadcast == null || !m_broadcast.IsHosting)
            {
                reason = "本端不是主机（复活点表只能由主机生成）";
                return -1;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（主机还没进入世界）";
                return -1;
            }
            var localPlayers = new List<PlayerData>(PlayerKey.SlotCount);
            if (!m_spawner.TryCollectLocalPlayers(project, localPlayers, out bool tooMany,
                    out string localReason))
            {
                reason = "抓主机本地角色失败：" + (localReason ?? "未知");
                return -1;
            }
            if (tooMany)
                LogRateLimited("spawn-entries-local-too-many", localReason);
            string localIdentity = PlayerIdentity.LocalIdentity();
            for (int slot = 0; slot < localPlayers.Count; slot++)
            {
                PlayerData local = localPlayers[slot];
                if (local == null || !IsFiniteVector(local.SpawnPosition)) continue;
                target.Add(new OnlineSpawnPoint(new PlayerKey(0, slot), local.SpawnPosition,
                    localIdentity));
            }
            foreach (PlayerKey key in m_spawner.Keys.ToArray())
            {
                if (!m_spawner.TryGet(key, out PlayerData playerData) || playerData == null)
                    continue;
                if (!IsFiniteVector(playerData.SpawnPosition)) continue;
                m_peerIdentities.TryGetValue(key.ClientId, out string identity);
                target.Add(new OnlineSpawnPoint(key, playerData.SpawnPosition, identity));
            }
            return target.Count;
        }

        /// <summary>
        /// 客户端：把主机下发的复活点写入本机角色（clientId=本机通道编号）或远端化身。
        /// 只改运行期 PlayerData.SpawnPosition；不写 Project.xml，也不写角色记录文件。
        /// </summary>
        public bool TryApplySpawnPoint(PlayerKey key, Vector3 spawnPosition, out string reason)
        {
            reason = null;
            if (!key.IsValid)
            {
                reason = "复活点表的玩家键非法：" + key;
                return false;
            }
            if (!IsFiniteVector(spawnPosition))
            {
                reason = "复活点为零或非有限值：" + spawnPosition;
                return false;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（世界还没就绪）";
                return false;
            }
            int localClientId = m_transport?.LocalClientId ?? -1;
            if (key.ClientId == localClientId)
            {
                var localPlayers = new List<PlayerData>(PlayerKey.SlotCount);
                if (!m_spawner.TryCollectLocalPlayers(project, localPlayers, out _,
                        out string collectReason))
                {
                    reason = "抓本机角色失败：" + (collectReason ?? "未知");
                    return false;
                }
                if (key.Slot < 0 || key.Slot >= localPlayers.Count)
                {
                    reason = "本机只有 " + localPlayers.Count + " 个角色，没有槽位 " + key.Slot;
                    return false;
                }
                localPlayers[key.Slot].SpawnPosition = spawnPosition;
                return true;
            }
            if (!m_spawner.TryGet(key, out PlayerData remote) || remote == null)
            {
                reason = "本机还没有 " + key + " 的远端化身";
                return false;
            }
            remote.SpawnPosition = spawnPosition;
            return true;
        }

        /// <summary>主机自己的资料（HelloAck.host_profile）。</summary>
        public bool TryGetHostProfile(out PlayerProfileSnapshot profile, out string reason) =>
            m_spawner.TryCaptureLocalProfile(out profile, out reason);

        // ---------------------------------------------------------------- 资料消息（收发）

        /// <summary>记下"这条角色记录的当前版本"（主机在资料 / 名册里下发的 record_hash）。</summary>
        private void NoteRecordHash(string identity, string name, int slot, ulong hash)
        {
            if (hash == 0UL) return;   // 旧主机 / 未知：不缓存，重连时按"变了"处理
            m_knownRecordHashCache[PlayerIdentity.RecordKey(identity, name, slot)] = hash;
        }

        /// <summary>
        /// 资料消息：主机收到客户端自己的资料 → 更新记录/化身并广播；
        /// 客户端收到主机/其他客户端资料 → 写进名册（主机的 client_id=0）。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:763-849
        /// </summary>
        private void OnPlayerProfileMessage(MessageContext context, Proto.PlayerProfile message)
        {
            PlayerProfileSnapshot profile = PlayerProfileCodec.FromMessage(message, out int clientId,
                out int slot, out string identity, out bool isHost, out bool hasCharacter,
                out ulong recordHash);
            if (profile == null)
            {
                ErrorOnce("profile-null", "收到空资料消息 → 丢弃（不猜任何字段）");
                return;
            }
            if (!PlayerKey.TryCreate(clientId, slot, out PlayerKey key, out string keyError))
            {
                m_rosterMismatch++;
                ErrorOnce("profile-bad-key:" + clientId + ":" + slot,
                    "收到玩家键 clientId=" + clientId + " slot=" + slot + " 的资料消息：" +
                    keyError + " → 丢弃（不夹紧、不猜槽位）");
                return;
            }
            bool hosting = m_broadcast?.IsHosting == true;
            if (hosting)
            {
                HandleClientProfileMessage(context.ClientId, key, identity, profile,
                    broadcastLegacy: true, out _);
                return;
            }
            // 客户端：主机广播的 host（clientId=0）/ 其他客户端 / 本机自己的记录。
            NoteRecordHash(identity, profile.Name, slot, recordHash);
            ProfileSyncApplyOutcome outcome = ApplyProfileBroadcastClient(key, identity, profile,
                isHost, out string applyReason);
            if (outcome == ProfileSyncApplyOutcome.Rejected)
            {
                ErrorOnce("profile-client-apply:" + key,
                    "应用主机广播的资料失败（玩家键 " + key + "）：" + applyReason);
            }
        }

        /// <summary>
        /// 客户端侧的唯一应用入口（27 与 108 两条消息共用，避免两套结果不一致）：
        ///   · 本机槽位 0 → 写资料存储 + 应用到本机 PlayerData（本机 UI 已改成同一值时是 Unchanged）；
        ///   · 本机槽位 &gt; 0 → 只登记资料存储（Phase 1 不回写本机 PlayerData，显式计数/日志）；
        ///   · 主机 / 其他客户端 → 更新名册并执行建/更新/重建化身。
        /// </summary>
        private ProfileSyncApplyOutcome ApplyProfileBroadcastClient(PlayerKey key, string identity,
            PlayerProfileSnapshot profile, bool isHost, out string reason)
        {
            reason = null;
            if (profile == null)
            {
                reason = "资料为空";
                return ProfileSyncApplyOutcome.Rejected;
            }
            if (m_transport != null && key.ClientId == m_transport.LocalClientId)
            {
                if (key.Slot == 0)
                {
                    if (m_profileStore == null)
                    {
                        reason = "没有本机资料存储（LocalPlayerProfileStore 未注入）";
                        return ProfileSyncApplyOutcome.Rejected;
                    }
                    string before = m_profileStore.Profile?.Signature ?? string.Empty;
                    m_profileStore.Set(profile, "host-record");
                    // 世界还没加载时只存资料；等 Project 就绪后由 TickClient 应用到本机角色
                    // （这里不报"应用失败"，那只是正常时序）。
                    if (GameManager.Project != null) ApplySelfProfileFromStore();
                    if (string.Equals(before, profile.Signature, StringComparison.Ordinal))
                    {
                        reason = "本机资料没有变化";
                        return ProfileSyncApplyOutcome.Unchanged;
                    }
                    reason = "已应用到本机角色";
                    return ProfileSyncApplyOutcome.Applied;
                }
                LogRateLimited("profile-self-slot:" + key,
                    "收到主机广播的本机槽位 " + key.Slot + " 资料（名字 " + profile.Name +
                    "）→ Phase 1 只把槽位 0 应用到本机角色；其余本机角色保留本地 PlayerData 资料" +
                    "（per-slot 资料回写属于后续阶段）");
                reason = "本机槽位 " + key.Slot + " 只登记资料存储，不回写本机 PlayerData";
                return ProfileSyncApplyOutcome.Unchanged;
            }
            IReadOnlyList<AvatarRosterChange> changes = m_roster.Apply(key, identity,
                profile.Name, profile.Appearance, isHost, "profile-sync");
            ApplyRosterChanges(changes, "资料广播");
            if (changes.Count == 0)
            {
                reason = "名册里的资料没有变化";
                return ProfileSyncApplyOutcome.Unchanged;
            }
            reason = "已更新 " + changes.Count + " 个名册条目";
            return ProfileSyncApplyOutcome.Applied;
        }

        /// <summary>
        /// 主机：应用一条**已由资料同步模块校验过**的客户端资料（108）。
        /// 不广播 27（由调用方广播 108）；返回 Applied 时调用方必须广播权威资料。
        /// </summary>
        public ProfileSyncApplyOutcome ApplyProfileSyncOnHost(int sourceClientId, PlayerKey key,
            string identity, PlayerProfileSnapshot profile, out string reason)
        {
            if (!m_attached)
            {
                reason = "联机角色模块未装配";
                return ProfileSyncApplyOutcome.Rejected;
            }
            return HandleClientProfileMessage(sourceClientId, key, identity, profile,
                broadcastLegacy: false, out reason);
        }

        /// <summary>客户端：应用主机广播的权威资料（108）。与 27 广播共用同一个应用入口。</summary>
        public ProfileSyncApplyOutcome ApplyProfileSyncBroadcast(PlayerKey key, string identity,
            PlayerProfileSnapshot profile, bool isHost, out string reason)
        {
            if (!m_attached)
            {
                reason = "联机角色模块未装配";
                return ProfileSyncApplyOutcome.Rejected;
            }
            return ApplyProfileBroadcastClient(key, identity, profile, isHost, out reason);
        }

        // ---------------------------------------------------------------- 资料同步端口（IPlayerProfileAuthorityPort / IProfileSkinCatalog）

        /// <summary>客户端：本机资料已经应用成自己的资料（下载世界的主机角色不能当自己上报）。</summary>
        public bool IsSelfProfileReady => m_appliedSelfSignature.Length > 0;

        /// <summary>本机身份（UserId.dat）；为空 = 身份不可用（资料同步会明确记录）。</summary>
        public string LocalIdentity => PlayerIdentity.LocalIdentity();

        /// <summary>本机角色槽位数（没有世界 / 没有本机玩家 = 0；调用方必须按 0 处理并写原因）。</summary>
        public int LocalPlayerSlotCount
        {
            get
            {
                Project project = GameManager.Project;
                if (project == null) return 0;
                var players = new List<PlayerData>(PlayerKey.SlotCount);
                return m_spawner.TryCollectLocalPlayers(project, players, out _, out _)
                    ? players.Count
                    : 0;
            }
        }

        /// <summary>本机槽位是否已经通过 LOCAL_PLAYER_SLOTS 公告给主机（槽位 &gt; 0 的先决条件）。</summary>
        public bool WasLocalSlotAnnounced(int slot) =>
            slot >= 0 && slot < m_localSlotsCount;

        /// <summary>
        /// 主机最终校验：名字 = 引擎 <see cref="PlayerData.VerifyName"/>（经 IsCompleteProfile）；
        /// 内置皮肤 = CharacterSkinsNames；自定义皮肤哈希 / 职业；衣服边界。
        /// </summary>
        public bool TryValidateProfileForSync(PlayerProfileSnapshot profile, int slot,
            out string reason)
        {
            if (!PlayerEditSync.TryValidateProfile(profile, slot, out reason)) return false;
            if (!IsCompleteProfile(profile))
            {
                reason = "资料不完整/皮肤与职业不兼容（名字 \"" + (profile?.Name ?? string.Empty) +
                    "\" · 皮肤 \"" + (profile?.Appearance?.SkinName ?? string.Empty) + "\"）";
                return false;
            }
            return true;
        }

        /// <summary>已知的权威资料：主机端已登记的对端资料 / 客户端名册 / 本机引擎角色。</summary>
        public bool TryGetKnownProfile(PlayerKey key, out PlayerProfileSnapshot profile)
        {
            if (m_peerSlots.TryGetValue(key, out PeerSlot peer) && peer.Profile != null)
            {
                profile = peer.Profile;
                return true;
            }
            if (m_roster.TryGet(key, out AvatarRosterEntry entry) && entry != null)
            {
                profile = new PlayerProfileSnapshot
                {
                    Name = entry.Name ?? string.Empty,
                    Appearance = entry.Appearance?.Clone() ?? new PlayerAppearance()
                };
                return profile.HasName;
            }
            if (m_spawner.TryGet(key, out PlayerData playerData))
            {
                profile = m_spawner.CaptureProfile(playerData, out _);
                return profile != null;
            }
            // 注意：不能把本机 PlayerData 当成主机（clientId=0）的已知资料 ——
            // 客户端上的本机角色是"自己"，把它复制到主机资料上会把本机衣服/皮肤写给别人。
            // 拿不到已知资料时由调用方明确拒绝"空衣服"（不猜、不覆盖）。
            profile = null;
            return false;
        }

        /// <summary>内置皮肤目录（CharacterSkinsNames）；自定义 .scskin 不走这里。</summary>
        public bool TryGetBuiltInClass(string skinName, out PlayerSex? playerClass)
        {
            playerClass = null;
            string skin = (skinName ?? string.Empty).Trim();
            if (skin.Length == 0) return false;
            // 与既有 IsKnownBuiltInSkin 同一口径：先刷新列表（外部皮肤可能刚导入）。
            CharacterSkinsManager.UpdateCharacterSkinsList();
            if (!CharacterSkinsManager.CharacterSkinsNames.Contains(skin)) return false;
            PlayerClass? engineClass = CharacterSkinsManager.GetPlayerClass(skin);
            if (engineClass.HasValue)
            {
                playerClass = engineClass.Value == PlayerClass.Female
                    ? PlayerSex.Female
                    : PlayerSex.Male;
            }
            return true;
        }

        private ProfileSyncApplyOutcome HandleClientProfileMessage(int sourceClientId,
            PlayerKey claimedKey, string identity, PlayerProfileSnapshot profile,
            bool broadcastLegacy, out string reason)
        {
            reason = null;
            if (sourceClientId <= 0)
            {
                reason = "来源连接编号 " + sourceClientId + " 非法";
                return ProfileSyncApplyOutcome.Rejected;
            }
            // owner 严格校验（照旧实现）：载荷里的 client_id 必须等于来源连接编号；
            // 0 是主机广播方向，不是客户端可以申报的值。
            if (claimedKey.ClientId != sourceClientId)
            {
                m_rejectedPeers++;
                reason = "载荷 client_id=" + claimedKey.ClientId + " != 来源 " + sourceClientId;
                ErrorOnce("profile-spoof:" + sourceClientId,
                    "clientId=" + sourceClientId + " 的资料自称玩家键 " + claimedKey +
                    " → 拒绝（不允许冒领另一个身份的资料）");
                return ProfileSyncApplyOutcome.Rejected;
            }
            string localIdentity = PlayerIdentity.LocalIdentity();
            if (!string.IsNullOrEmpty(identity) &&
                string.Equals(identity, localIdentity, StringComparison.Ordinal))
            {
                m_rejectedPeers++;
                reason = "资料用了主机自己的身份";
                ErrorOnce("profile-host-identity:" + sourceClientId,
                    "clientId=" + sourceClientId + " 的资料用了主机自己的身份 → 拒绝");
                return ProfileSyncApplyOutcome.Rejected;
            }
            // 最小校验：名字长度/字符集（与引擎 PlayerData.VerifyName 同规则）、皮肤名、自定义
            // 皮肤哈希、衣服数组边界。**不检查名字唯一性** —— 产品明确允许重名，重名照常应用并广播。
            if (!PlayerEditSync.TryValidateProfile(profile, claimedKey.Slot, out string editReason))
            {
                m_rejectedPeers++;
                m_editCounters.OnProfileUpdateRejected(editReason);
                reason = editReason;
                ErrorOnce("profile-edit-invalid:" + sourceClientId + ":" + claimedKey.Slot,
                    "玩家键 " + claimedKey + " 上报的资料未通过最小校验：" + editReason +
                    " → 保留旧资料（不夹紧、不换默认皮肤、不因重名拒绝）");
                return ProfileSyncApplyOutcome.Rejected;
            }
            if (!IsCompleteProfile(profile))
            {
                m_avatarAppearanceUnavailable++;
                reason = "资料不完整/皮肤与职业不兼容";
                ErrorOnce("profile-invalid:" + sourceClientId + ":" + claimedKey.Slot,
                    "玩家键 " + claimedKey + " 上报的资料不完整/非法（名字 " +
                    (profile.Name ?? string.Empty) + " · 皮肤 " + (profile.Appearance?.SkinName ?? string.Empty) +
                    "）→ 保留旧资料，不拿默认值覆盖");
                return ProfileSyncApplyOutcome.Rejected;
            }
            // 载荷里的 clientId 只用于校验；权威来源是连接编号 + 申报槽位。
            PlayerKey key = new PlayerKey(sourceClientId, claimedKey.Slot);
            // 槽位 > 0 必须已经被 LOCAL_PLAYER_SLOTS 公告过（否则就是凭空多出来的键）。
            if (key.Slot > 0 &&
                (!m_slotAwareClients.Contains(sourceClientId) ||
                 !m_peerSlotCounts.TryGetValue(sourceClientId, out int announcedSlotCount) ||
                 key.Slot >= announcedSlotCount))
            {
                m_rosterMismatch++;
                reason = "槽位 " + key.Slot + " 没有被 LOCAL_PLAYER_SLOTS 公告过";
                ErrorOnce("profile-unannounced-slot:" + key,
                    "玩家键 " + key + " 的资料来自一个没有公告过的槽位（该端已公告 " +
                    (m_peerSlotCounts.TryGetValue(sourceClientId, out int known) ? known : 0) +
                    " 个槽位）→ 拒绝（先发 LOCAL_PLAYER_SLOTS 再发该槽位资料）");
                return ProfileSyncApplyOutcome.Rejected;
            }
            string recordKey = m_spawner.TryGetRecordKey(key, out string existingKey)
                ? existingKey
                : PlayerIdentity.RecordKey(identity, profile.Name, key.Slot);
            if (!m_records.TryGet(recordKey, out NetworkPlayerRecord record))
            {
                record = CreateInitialPlayerRecord(profile);
                m_records.Put(recordKey, record);
                m_records.SaveIfDirty();
                Info("玩家键 " + key + " 的本机槽位没有角色记录 → 按上报资料建记录（名字 " +
                    record.Name + " · 记录键=" + PlayerIdentity.Describe(recordKey) + "）");
            }
            else
            {
                string before = RecordToProfile(record).Signature;
                ApplyProfileToRecord(record, profile);
                m_records.Put(recordKey, record);
                m_records.SaveIfDirty();
                if (string.Equals(before, RecordToProfile(record).Signature, StringComparison.Ordinal) &&
                    m_slotAwareClients.Contains(sourceClientId))
                {
                    reason = "资料与记录一致";
                    return ProfileSyncApplyOutcome.Unchanged;
                }
            }
            if (m_recordKeyOwner.TryGetValue(recordKey, out PlayerKey recordOwner) &&
                recordOwner.ClientId != sourceClientId)
            {
                m_rosterMismatch++;
                reason = "记录键已属于玩家键 " + recordOwner;
                ErrorOnce("profile-record-owner:" + key,
                    "玩家键 " + key + " 的记录键 " + PlayerIdentity.Describe(recordKey) +
                    " 已属于玩家键 " + recordOwner + " → 拒绝这次资料（同一身份+槽位只能有一个角色）");
                return ProfileSyncApplyOutcome.Rejected;
            }
            m_recordKeyOwner[recordKey] = key;
            PeerSlot peer = EnsurePeerSlot(key);
            peer.Identity = identity ?? string.Empty;
            peer.Name = profile.Name;
            peer.RecordKey = recordKey;
            peer.Profile = profile;
            m_peerIdentities[sourceClientId] = identity ?? string.Empty;
            ResolveName(recordKey, profile.Name, "客户端自行上报（PlayerProfile）");
            // 化身已存在 → 就地更新；性别变了 / 还没有化身 → 摘掉重建或进挂起队列。
            if (m_spawner.TryGet(key, out _))
            {
                if (m_spawner.TryApplyRemoteProfile(key, profile,
                        out bool mustRecreate, out string reason2))
                {
                    m_avatarsUpdated++;
                    m_avatarAppearanceApplied++;
                    Info("已更新客户端角色外观：玩家键 " + key + " · 名字=" + profile.Name +
                        " · 性别=" + profile.Appearance.Sex + " · 皮肤=" + profile.Appearance.SkinName);
                }
                else if (mustRecreate)
                {
                    Info("客户端角色性别变化（玩家键 " + key + "）：摘掉旧化身并按新资料重建 —— " + reason2);
                    if (m_spawner.Remove(key)) m_avatarsRemoved++;
                    m_appliedAvatarSignatures.Remove(key);
                    QueuePendingPeer(key, identity, profile, recordKey);
                }
                else
                {
                    ErrorOnce("profile-apply:" + key,
                        "更新客户端角色外观失败（玩家键 " + key + "）：" + reason2);
                }
            }
            else
            {
                QueuePendingPeer(key, identity, profile, recordKey);
            }
            if (broadcastLegacy)
            {
                BroadcastProfile(key, profile, isHost: false, identity: identity);
                BroadcastRoster("客户端资料上报");
                // 客户端自己的角色记录也回给它（名字/性别/皮肤/衣服以主机记录为准）。
                // Phase 1 只回槽位 0（HelloAck.assigned_profile 的口径）；其余槽位后续阶段再回写。
                if (key.Slot == 0 && recordKey.Length > 0 &&
                    m_records.TryGet(recordKey, out NetworkPlayerRecord assigned))
                    SendAssignedProfile(sourceClientId, assigned);
            }
            reason = "已应用并更新记录";
            return ProfileSyncApplyOutcome.Applied;
        }

        /// <summary>客户端：主机广播"某人离开" → 摘掉对应化身（按身份去重，重连也不会留下旧的）。</summary>
        private void OnAvatarRemoveMessage(MessageContext context, Proto.AvatarRemove message)
        {
            if (m_broadcast?.IsHosting == true) return;    // 主机不应收到这个方向的消息
            string reason = message.Reason ?? string.Empty;
            // **只按玩家键摘除**：同一身份重连后 identity 已经指向新键，
            // 旧连接迟到的"离开"如果按身份摘除，会把刚建好的新化身也删掉。
            // 真的漏了也不要紧：主机在离开事件后还会广播整表，客户端会对齐。
            if (!PlayerKey.TryCreate(message.ClientId, (int)message.Slot, out PlayerKey key,
                    out string keyError))
            {
                m_rosterMismatch++;
                ErrorOnce("remove-bad-key:" + message.ClientId + ":" + message.Slot,
                    "离开通知的玩家键 clientId=" + message.ClientId + " slot=" + message.Slot +
                    " 非法：" + keyError + " → 丢弃（不猜槽位；名册随后整表对齐）");
                return;
            }
            IReadOnlyList<AvatarRosterChange> changes = m_roster.Remove(key, reason);
            ApplyRosterChanges(changes, "离开通知");
        }

        /// <summary>
        /// 客户端：主机广播的**完整名册**。这是客户端之间互相可见的权威来源：
        /// 名册里每个身份都要有化身；名册里没有的（离开 / 重连换连接）立即摘掉。
        /// </summary>
        private void OnAvatarRosterMessage(MessageContext context, Proto.AvatarRoster message)
        {
            if (m_broadcast?.IsHosting == true) return;    // 主机不应收到这个方向的消息
            if (message == null) return;
            var incoming = new HashSet<PlayerKey>();
            var changes = new List<AvatarRosterChange>();
            for (int i = 0; i < message.Avatars.Count; i++)
            {
                Proto.PlayerProfile item = message.Avatars[i];
                if (item == null) continue;
                PlayerProfileSnapshot profile = PlayerProfileCodec.FromMessage(item,
                    out int clientId, out int slot, out string identity, out bool isHost,
                    out bool hasCharacter, out ulong recordHash);
                bool keyValid = PlayerKey.TryCreate(clientId, slot, out PlayerKey key,
                    out string keyError);
                if (profile == null || !profile.HasName || !keyValid)
                {
                    m_avatarAppearanceUnavailable++;
                    m_rosterMismatch++;
                    ErrorOnce("roster-item:" + clientId + ":" + slot,
                        "名册里玩家键 clientId=" + clientId + " slot=" + slot + " 的资料不完整/非法（" +
                        (keyError ?? "名字为空") + "；身份 " +
                        PlayerIdentity.Describe(PlayerIdentity.RecordKey(identity, profile?.Name, slot)) +
                        "）→ 不建这个化身（绝不拿默认名字/皮肤兜底）");
                    continue;
                }
                if (!incoming.Add(key))
                {
                    m_rosterMismatch++;
                    ErrorOnce("roster-duplicate:" + key,
                        "完整名册里玩家键 " + key + " 出现了多次 → 只应用第一条（协议违规；计数 rosterMismatch）");
                    continue;
                }
                NoteRecordHash(identity, profile.Name, slot, recordHash);
                if (key.ClientId == m_transport?.LocalClientId)
                {
                    if (key.Slot == 0)
                    {
                        m_profileStore?.Set(profile, "host-roster");
                        if (GameManager.Project != null) ApplySelfProfileFromStore();
                    }
                    continue;
                }
                IReadOnlyList<AvatarRosterChange> applied = m_roster.Apply(key, identity,
                    profile.Name, profile.Appearance, isHost, "host-roster");
                for (int j = 0; j < applied.Count; j++) changes.Add(applied[j]);
            }
            // 名册是**完整集合**：不在里面的旧条目一律摘掉（离开消息丢失 / 重连换连接都不会留下幽灵化身）。
            IReadOnlyList<AvatarRosterEntry> existing = m_roster.Entries;
            for (int i = 0; i < existing.Count; i++)
            {
                AvatarRosterEntry entry = existing[i];
                if (incoming.Contains(entry.Key)) continue;
                IReadOnlyList<AvatarRosterChange> removed = m_roster.Remove(entry.Key,
                    "不在主机完整名册里（离开 / 重连换连接）");
                for (int j = 0; j < removed.Count; j++) changes.Add(removed[j]);
            }
            ApplyRosterChanges(changes, "完整名册");
        }

        /// <summary>
        /// 主机：一个客户端上报的**完整本机角色列表**（<c>LOCAL_PLAYER_SLOTS</c>）。
        /// 这是"主机给该客户端的每个本机角色建一个化身"的唯一入口；条数/槽位非法一律整条拒绝，
        /// 绝不夹紧或猜槽位。旧构建永远不发这条消息 → 保持单角色（槽位 0）行为。
        /// </summary>
        private void OnLocalPlayerSlotsMessage(MessageContext context, Proto.LocalPlayerSlots message)
        {
            if (m_broadcast?.IsHosting != true)
            {
                ErrorOnce("local-slots-direction",
                    "客户端收到了 LOCAL_PLAYER_SLOTS（主机才处理这个方向）→ 丢弃");
                return;
            }
            int clientId = context.ClientId;
            if (clientId <= 0 || message == null) return;
            int count = (int)message.Count;
            if (count < 1 || count > PlayerKey.SlotCount)
            {
                m_tooManySlots++;
                ErrorOnce("local-slots-count:" + clientId,
                    "clientId=" + clientId + " 上报本机槽位数 " + count + " 非法（合法 1.." +
                    PlayerKey.SlotCount + "）→ 整条丢弃（不夹紧）");
                return;
            }
            if (message.Slots.Count != count)
            {
                m_rosterMismatch++;
                ErrorOnce("local-slots-size:" + clientId,
                    "clientId=" + clientId + " 上报本机槽位数 " + count + " 与条目数 " +
                    message.Slots.Count + " 不一致 → 整条丢弃（不半条应用）");
                return;
            }
            m_peerIdentities.TryGetValue(clientId, out string connectionIdentity);
            var entries = new List<Proto.LocalPlayerSlot>(count);
            var seenSlots = new HashSet<int>();
            for (int i = 0; i < message.Slots.Count; i++)
            {
                Proto.LocalPlayerSlot entry = message.Slots[i];
                if (entry == null || entry.Profile == null) continue;
                int slot = (int)entry.Slot;
                if (slot != i || !seenSlots.Add(slot))
                {
                    m_rosterMismatch++;
                    ErrorOnce("local-slots-entry:" + clientId + ":" + slot,
                        "clientId=" + clientId + " 的本机槽位条目 " + slot + " 非法/重复/不连续" +
                        "（第 " + i + " 条必须是槽位 " + i + "；合法 0.." + PlayerKey.MaxSlot +
                        "）→ 整条丢弃（不夹紧）");
                    return;
                }
                string claimedIdentity = (entry.Profile.Identity ?? string.Empty).Trim();
                if (claimedIdentity.Length > 0 && connectionIdentity.Length > 0 &&
                    !string.Equals(claimedIdentity, connectionIdentity, StringComparison.Ordinal))
                {
                    m_rosterMismatch++;
                    ErrorOnce("local-slots-identity:" + clientId,
                        "clientId=" + clientId + " 的本机槽位 " + slot + " 声称身份 " +
                        PlayerIdentity.Shorten(claimedIdentity) + "，与该连接身份 " +
                        PlayerIdentity.Shorten(connectionIdentity) + " 不一致 → 整条丢弃（不冒领身份）");
                    return;
                }
                PlayerProfileSnapshot profile = PlayerProfileCodec.FromMessage(entry.Profile,
                    out _, out _, out _, out _, out _);
                if (profile == null || !profile.HasName)
                {
                    m_rosterMismatch++;
                    ErrorOnce("local-slots-profile:" + clientId + ":" + slot,
                        "clientId=" + clientId + " 的本机槽位 " + slot + " 没有可用名字/资料 → 整条丢弃" +
                        "（绝不拿默认名字兜底）");
                    return;
                }
                string claimedRecordKey = PlayerIdentity.RecordKey(connectionIdentity,
                    profile.Name, slot);
                if (m_recordKeyOwner.TryGetValue(claimedRecordKey, out PlayerKey keyOwner) &&
                    keyOwner.ClientId != clientId)
                {
                    m_rosterMismatch++;
                    ErrorOnce("local-slots-owner:" + clientId + ":" + slot,
                        "clientId=" + clientId + " 的本机槽位 " + slot + " 记录键 " +
                        PlayerIdentity.Describe(claimedRecordKey) + " 已属于玩家键 " + keyOwner +
                        " → 整条丢弃（同一身份+槽位只能有一个角色）");
                    return;
                }
                entries.Add(entry);
            }
            if (entries.Count != count)
            {
                m_rosterMismatch++;
                ErrorOnce("local-slots-entries:" + clientId,
                    "clientId=" + clientId + " 上报本机槽位数 " + count + "，可用条目 " +
                    entries.Count + " → 整条丢弃（不半条应用）");
                return;
            }
            // 合法：登记为"按槽位感知"的端，之后位置批/名册都发全部槽位。
            m_slotAwareClients.Add(clientId);
            m_peerSlotCounts[clientId] = count;
            m_slotsAnnounced += count;
            m_slotsRegistered = CountRegisteredSlots();
            bool changed = false;
            for (int i = 0; i < entries.Count; i++)
            {
                Proto.LocalPlayerSlot entry = entries[i];
                int slot = (int)entry.Slot;
                PlayerKey key = new PlayerKey(clientId, slot);
                PlayerProfileSnapshot profile = PlayerProfileCodec.FromMessage(entry.Profile,
                    out _, out _, out _, out _, out _);
                string identity = connectionIdentity;
                string recordKey = PlayerIdentity.RecordKey(identity, profile.Name, slot);
                if (!m_records.TryGet(recordKey, out NetworkPlayerRecord record))
                {
                    if (!IsCompleteProfile(profile))
                    {
                        m_avatarAppearanceUnavailable++;
                        ErrorOnce("local-slots-record:" + key,
                            "玩家键 " + key + " 没有角色记录，上报资料也不完整 → 该槽位暂不建化身" +
                            "（不拿默认值伪造记录）");
                        continue;
                    }
                    record = CreateInitialPlayerRecord(profile);
                    m_records.Put(recordKey, record);
                    m_records.SaveIfDirty();
                    Info("为玩家键 " + key + " 建了本机角色记录（名字 " + record.Name +
                        " · 记录键=" + PlayerIdentity.Describe(recordKey) +
                        "；资料来自 LOCAL_PLAYER_SLOTS）");
                }
                PeerSlot peer = EnsurePeerSlot(key);
                peer.Identity = identity;
                peer.Name = profile.Name;
                peer.RecordKey = recordKey;
                peer.Profile = profile;
                m_recordKeyOwner[recordKey] = key;
                changed = true;
                if (m_spawner.TryGet(key, out _))
                {
                    if (!m_spawner.TryApplyRemoteProfile(key, profile, out bool mustRecreate,
                            out string applyReason))
                    {
                        if (mustRecreate)
                        {
                            Info("本机槽位性别变化（玩家键 " + key + "）：摘掉旧化身并按新资料重建 —— " +
                                applyReason);
                            if (m_spawner.Remove(key)) m_avatarsRemoved++;
                            m_appliedAvatarSignatures.Remove(key);
                            QueuePendingPeer(key, identity, profile, recordKey);
                        }
                        else
                        {
                            ErrorOnce("local-slots-apply:" + key,
                                "更新本机槽位化身外观失败（玩家键 " + key + "）：" + applyReason);
                        }
                    }
                }
                else
                {
                    QueuePendingPeer(key, identity, profile, recordKey);
                }
            }
            // 槽位消失（分屏第二个本机角色被移除）：摘化身 + 显式离开通知。
            foreach (KeyValuePair<PlayerKey, PeerSlot> pair in m_peerSlots.ToArray())
            {
                if (pair.Key.ClientId != clientId || pair.Key.Slot < count) continue;
                PlayerKey key = pair.Key;
                if (m_spawner.Remove(key))
                {
                    m_avatarsRemoved++;
                    m_appliedAvatarSignatures.Remove(key);
                }
                m_peerSlots.Remove(key);
                m_pendingPeers.Remove(key);
                if (m_recordKeyOwner.TryGetValue(pair.Value.RecordKey, out PlayerKey owner) &&
                    owner == key)
                    m_recordKeyOwner.Remove(pair.Value.RecordKey);
                changed = true;
                Info("本机槽位消失：玩家键 " + key + "（clientId=" + clientId + " 现在上报 " +
                    count + " 个槽位）→ 已摘掉化身并发离开通知");
                BroadcastAvatarRemove(key, connectionIdentity,
                    "该客户端移除了本机角色槽位 " + key.Slot);
            }
            m_slotsRegistered = CountRegisteredSlots();
            // 该端第一次上报后从"旧构建口径"变成"按槽位口径"：名册/位置负载变了但签名可能没变，
            // 必须强制整表重发一次，否则它收不到主机槽位 1..N。
            if (changed) BroadcastRoster("客户端本机槽位变化", force: true);
            LogRateLimited("local-slots:" + clientId,
                "clientId=" + clientId + " 上报 " + count + " 个本机角色槽位（0.." + (count - 1) +
                "）→ 主机为每个槽位维护一个化身；旧构建不上报 = 只有槽位 0");
        }

        private int CountRegisteredSlots()
        {
            int count = 0;
            foreach (KeyValuePair<PlayerKey, PeerSlot> pair in m_peerSlots)
                if (pair.Key.ClientId > 0) count++;
            return count;
        }

        /// <summary>
        /// 客户端：把本机角色集合上报给主机（<c>LOCAL_PLAYER_SLOTS</c>）。
        /// 握完手就发一次（还没进世界时用本机资料存储里的槽位 0 资料）；世界加载后按
        /// <c>PlayersData</c>（非网络化身）的真实本机角色重新枚举，槽位增删/资料变化时再发。
        /// </summary>
        private void UpdateLocalSlotsAnnouncement()
        {
            if (m_transport == null || !m_transport.IsConnected) return;
            if (m_transport.LocalClientId < 0) return;
            if (m_messages == null) return;
            string identity = PlayerIdentity.LocalIdentity();
            if (identity.Length == 0)
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("local-slots-identity",
                    "本机身份（UserId.dat）为空 → 无法上报本机槽位（主机只能按单角色处理）");
                return;
            }
            var profiles = new List<PlayerProfileSnapshot>(PlayerKey.SlotCount);
            var indexes = new List<int>(PlayerKey.SlotCount);
            Project project = GameManager.Project;
            if (project != null)
            {
                var localPlayers = new List<PlayerData>(PlayerKey.SlotCount);
                if (m_spawner.TryCollectLocalPlayers(project, localPlayers, out bool tooMany,
                        out string collectReason))
                {
                    if (tooMany)
                    {
                        m_tooManySlots++;
                        LogRateLimited("local-slots-too-many", collectReason);
                    }
                    for (int slot = 0; slot < localPlayers.Count; slot++)
                    {
                        PlayerProfileSnapshot profile = m_spawner.CaptureProfile(localPlayers[slot],
                            out string captureReason);
                        if (profile == null)
                        {
                            m_avatarAppearanceUnavailable++;
                            ErrorOnce("local-slots-profile:" + slot,
                                "本机槽位 " + slot + " 的资料取不到（" + captureReason +
                                "）→ 本帧不上报槽位集合（绝不发半套槽位让主机建错化身）");
                            return;
                        }
                        profiles.Add(profile);
                        indexes.Add(localPlayers[slot].PlayerIndex);
                    }
                }
            }
            if (profiles.Count == 0)
            {
                PlayerProfileSnapshot stored = m_profileStore?.Profile;
                if (stored == null || !stored.HasName) return;
                profiles.Add(stored);
                indexes.Add(-1);
            }
            if (profiles.Count > PlayerKey.SlotCount)
            {
                m_tooManySlots++;
                ErrorOnce("local-slots-overflow",
                    "本机角色数 " + profiles.Count + " 超过 " + PlayerKey.SlotCount +
                    " → 只上报前 " + PlayerKey.SlotCount + " 个（引擎上限）");
                profiles.RemoveRange(PlayerKey.SlotCount, profiles.Count - PlayerKey.SlotCount);
                indexes.RemoveRange(PlayerKey.SlotCount, indexes.Count - PlayerKey.SlotCount);
            }
            // 容量口径：本机角色 + 名册里的远端角色共用 0..3。超过时**不删本机角色**（用户的分屏角色
            // 一个都不退让，照常公告），而是明确说明超出的远端化身会被容量闸门拒绝。
            int remoteCharacters = CountRemoteCharacters();
            if (profiles.Count + remoteCharacters > PlayerKey.SlotCount)
            {
                LogRateLimited("local-slots-capacity",
                    "本机槽位公告：本机 " + profiles.Count + " 个角色 + 名册里的远端角色 " +
                    remoteCharacters + " 个 = " + (profiles.Count + remoteCharacters) +
                    " 超过引擎上限 " + PlayerKey.SlotCount + "（SubsystemPlayers.MaxPlayers / " +
                    "SubsystemGameWidgets.MaxGameWidgets）→ 仍然公告本机全部槽位（绝不替用户删角色）；" +
                    "超出的远端化身由容量闸门显式拒绝并计数（avatarCapacityRefusals）");
            }
            var signature = new System.Text.StringBuilder();
            for (int slot = 0; slot < profiles.Count; slot++)
                signature.Append(slot).Append(':').Append(profiles[slot].Signature).Append('|');
            string text = signature.ToString();
            if (string.Equals(text, m_localSlotsSignature, StringComparison.Ordinal)) return;
            var message = new Proto.LocalPlayerSlots { Count = (uint)profiles.Count };
            for (int slot = 0; slot < profiles.Count; slot++)
            {
                message.Slots.Add(new Proto.LocalPlayerSlot
                {
                    Slot = (uint)slot,
                    LocalPlayerIndex = indexes[slot],
                    Profile = PlayerProfileCodec.ToMessage(new PlayerKey(0, slot), identity,
                        profiles[slot], isHost: false, hasCharacter: true)
                });
            }
            if (!m_messages.Sender.Send(0, message))
            {
                ErrorOnce("local-slots-send",
                    "本机槽位集合发送失败（通道未连接 / 队列满）→ 下次变化或重试时再发（不假装成功）");
                m_localSlotsSignature = string.Empty;
                return;
            }
            m_localSlotsSignature = text;
            m_localSlotsCount = profiles.Count;
            m_slotsAnnounced += profiles.Count;
            Info("已上报本机角色槽位：" + profiles.Count + " 个（" +
                string.Join("、", profiles.ConvertAll(item => item.Name)) +
                "）→ 主机为每个槽位建一个化身" +
                (profiles.Count > 1 ? "；多本机角色（分屏）已启用" : string.Empty));
        }

        // ---------------------------------------------------------------- 客户端名册 → 引擎动作

        private void ApplyRosterChanges(IReadOnlyList<AvatarRosterChange> changes, string why)
        {
            if (changes == null) return;
            for (int i = 0; i < changes.Count; i++)
            {
                AvatarRosterChange change = changes[i];
                if (change.Action != AvatarRosterAction.Remove) continue;
                if (m_spawner.Remove(change.Key))
                {
                    m_avatarsRemoved++;
                    m_appliedAvatarSignatures.Remove(change.Key);
                    Info("已摘除远端化身：玩家键 " + change.Key +
                        (string.IsNullOrEmpty(change.Name) ? string.Empty : "（" + change.Name + "）") +
                        " · 原因=" + (change.Reason ?? why) + "（离开 / 重连换连接）");
                }
            }
        }

        /// <summary>
        /// 客户端：把主机广播的名册与引擎里的远端化身对齐。
        /// **每个身份（主机 + 其他每个客户端）都必须有一个化身**；资料变了就地更新，
        /// 性别变了摘掉重建；名册里没有的化身摘掉（离开）。
        /// </summary>
        private void ReconcileClientAvatars()
        {
            IReadOnlyList<AvatarRosterEntry> entries = m_roster.Entries;
            var seen = new HashSet<PlayerKey>();
            // 容量闸门的本机角色数：只算一次（本帧内创建化身不会改变本机角色）。
            int localCount = m_spawner.CountLocalPlayers(GameManager.Project);
            for (int i = 0; i < entries.Count; i++)
            {
                AvatarRosterEntry entry = entries[i];
                seen.Add(entry.Key);
                if (entry.IsSelf)
                {
                    // 本机自己的资料绝不建远端化身（旧实现里也是应用到本机玩家）。
                    if (m_spawner.Remove(entry.Key))
                    {
                        m_avatarsRemoved++;
                        m_appliedAvatarSignatures.Remove(entry.Key);
                        Info("名册把玩家键 " + entry.Key + " 标为本机自己 → 摘掉误建的自身化身");
                    }
                    continue;
                }
                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    m_avatarAppearanceUnavailable++;
                    ErrorOnce("avatar-name-missing:" + entry.Key,
                        "名册里玩家键 " + entry.Key + "（身份 " +
                        PlayerIdentity.Describe(PlayerIdentity.RecordKey(entry.Identity, entry.Name, entry.Slot)) +
                        "）没有名字 → 不建/不更新这个化身（绝不拿 \"Player\"/\"Android User\" 兜底）");
                    continue;
                }
                bool exists = m_spawner.TryGet(entry.Key, out _);
                string signature = entry.Signature;
                if (exists && m_appliedAvatarSignatures.TryGetValue(entry.Key, out string applied) &&
                    string.Equals(applied, signature, StringComparison.Ordinal))
                    continue;
                if (!exists)
                {
                    if (!TryReserveAvatarCapacity(entry.Key, localCount)) continue;
                    TryCreateClientAvatar(entry);
                    continue;
                }
                if (m_spawner.TryApplyRemoteProfile(entry.Key, ToSnapshot(entry),
                        out bool mustRecreate, out string reason))
                {
                    m_appliedAvatarSignatures[entry.Key] = signature;
                    m_avatarsUpdated++;
                    m_avatarAppearanceApplied++;
                    Info("已应用远端化身外观：玩家键 " + entry.Key + " · 名字=" + entry.Name +
                        " · 性别=" + (entry.Appearance?.Sex ?? PlayerSex.Male) +
                        " · 皮肤=" + (entry.Appearance?.SkinName ?? string.Empty));
                }
                else if (mustRecreate)
                {
                    Info("远端化身性别变化（玩家键 " + entry.Key + "）：摘掉旧化身并按新资料重建 —— " + reason);
                    if (m_spawner.Remove(entry.Key))
                    {
                        m_avatarsRemoved++;
                        m_appliedAvatarSignatures.Remove(entry.Key);
                    }
                    if (!TryReserveAvatarCapacity(entry.Key, localCount)) continue;
                    TryCreateClientAvatar(entry);
                }
                else
                {
                    ErrorOnce("avatar-apply:" + entry.Key,
                        "应用远端化身外观失败（玩家键 " + entry.Key + "）：" + reason);
                }
            }
            // 名册里已经不存在、但引擎里还留着的化身（离开消息丢失 / 主机没发）→ 摘掉。
            foreach (PlayerKey key in m_spawner.Keys.ToArray())
            {
                if (seen.Contains(key)) continue;
                if (m_spawner.Remove(key))
                {
                    m_avatarsRemoved++;
                    m_appliedAvatarSignatures.Remove(key);
                    Info("远端化身不在主机名册里 → 已摘除：玩家键 " + key);
                }
            }
        }

        private bool TryCreateClientAvatar(AvatarRosterEntry entry)
        {
            PlayerProfileSnapshot profile = ToSnapshot(entry);
            string recordKey = PlayerIdentity.RecordKey(entry.Identity, entry.Name, entry.Slot);
            var record = new NetworkPlayerRecord
            {
                Name = profile.Name,
                PlayerClass = NetworkAvatarSpawner.ToPlayerClass(profile.Appearance?.Sex ?? PlayerSex.Male),
                SkinName = profile.Appearance?.SkinName ?? string.Empty,
                SkinSha256 = profile.Appearance?.SkinSha256 != null
                    ? (byte[])profile.Appearance.SkinSha256.Clone()
                    : Array.Empty<byte>(),
                Clothes = PlayerAppearance.CloneClothes(profile.Appearance?.Clothes),
                HasReceivedInitialItems = true
            };
            if (!m_spawner.TryCreate(entry.Key, entry.Name, recordKey, record, isHost: false,
                    out string reason))
            {
                ErrorOnce("avatar-create:" + entry.Key,
                    "创建远端化身失败（每帧重试，不刷屏）：玩家键 " + entry.Key +
                    " · 记录键=" + PlayerIdentity.Describe(recordKey) + " · 原因=" + reason);
                return false;
            }
            m_avatarsCreated++;
            if (!string.IsNullOrEmpty(record.SkinName)) m_avatarAppearanceApplied++;
            else
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("avatar-skin-missing:" + entry.Key,
                    "玩家键 " + entry.Key + " 的资料没有皮肤名 → 引擎会按默认外观创建" +
                    "（明确记录：这不是该玩家的真实皮肤；主机应尽快广播完整资料）");
            }
            m_appliedAvatarSignatures[entry.Key] = entry.Signature;
            ResolveName(recordKey, entry.Name, entry.IsHost ? "HelloAck.host_profile" : "主机广播资料");
            Info("已创建远端化身：玩家键 " + entry.Key + " · 名字=" + entry.Name +
                " · 性别=" + record.PlayerClass + " · 皮肤=" + record.SkinName +
                " · 记录键=" + PlayerIdentity.Describe(recordKey));
            return true;
        }

        private static PlayerProfileSnapshot ToSnapshot(AvatarRosterEntry entry) => new PlayerProfileSnapshot
        {
            Name = entry.Name ?? string.Empty,
            Appearance = entry.Appearance?.Clone() ?? new PlayerAppearance()
        };

        private void ShowProfileRequiredScreen(string reason)
        {
            if (m_profilePromptShown) return;
            m_profilePromptShown = true;
            Warn("主机要求先创建角色（" + (reason ?? string.Empty) + "）→ 打开角色创建界面" +
                "（旧实现的 SuNetworkPlayerScreen 流程）；创建完成后带 Hello.profile 重新握手");
            try
            {
                ScreensManager.SwitchScreen("SuMultiplayerPlayerProfile",
                    (Action<PlayerSkinChangeRequest>)OnCharacterCreated,
                    KnownSessionNamesForDefaultName());
            }
            catch (Exception e)
            {
                Error("打开角色创建界面失败（" + e.GetType().Name + " " + e.Message + "）→ " +
                    "本次加入无法继续；请确认组合根注册了 \"SuMultiplayerPlayerProfile\" 屏（Load 'Initialize PlayScreen'）");
            }
        }

        /// <summary>
        /// 角色创建屏摇默认名字时要避开的**当前会话已知名字**（主机 + 名册里已经出现的人）。
        /// 只取会话里真实出现过的名字；本机身份自己的旧名字排除（重连时不能被自己的旧名挤走），
        /// 引擎固定显示名 "Android User" 也不是玩家名字（PlayerIdentity.IsEngineDefaultName）。
        /// 客户端在 profile_required 时还没有世界 / 名册时，这里可能只有主机名字 —— 这是如实结果
        /// （重名重试由创建屏按这份列表做，达到上限后接受重名并记录 nameRollCollisions）。
        /// </summary>
        private string[] KnownSessionNamesForDefaultName()
        {
            var names = new List<string>();
            void Add(string value)
            {
                string trimmed = (value ?? string.Empty).Trim();
                if (trimmed.Length == 0) return;
                if (names.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return;
                names.Add(trimmed);
            }

            PlayerProfileSnapshot hostProfile = m_handshake?.HostProfile;
            if (hostProfile != null && hostProfile.HasName) Add(hostProfile.Name);
            Add(m_transport?.HostName);
            string selfIdentity = PlayerIdentity.LocalIdentity();
            var entries = m_roster.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                AvatarRosterEntry entry = entries[i];
                if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                if (selfIdentity.Length > 0 &&
                    string.Equals(entry.Identity, selfIdentity, StringComparison.Ordinal))
                {
                    continue;
                }
                Add(entry.Name);
            }
            names.RemoveAll(PlayerIdentity.IsEngineDefaultName);
            return names.ToArray();
        }

        /// <summary>
        /// 角色创建屏完成（首次加入的唯一入口）。自定义皮肤把**屏幕读到的原始资源**一起交上来：
        ///   · 校验名称 / 大小 / PNG / 引擎解码；
        ///   · 写进本机会话缓存（Owned）并把哈希放进资料；
        ///   · 重新握手；主机据此建世界作用域角色记录，客户端随后上传资源（HelloAck 未声明已有）。
        /// 任何一步失败都明确记录并中止，不拿默认皮肤伪造资料。
        /// </summary>
        public void OnCharacterCreated(PlayerSkinChangeRequest request)
        {
            if (request == null)
            {
                Error("角色创建返回空请求 → 不提交");
                m_profilePromptShown = false;
                return;
            }
            // 名字按引擎原样校验（不 Trim）：合法性只由 PlayerData.VerifyName 决定。
            string name = request.Name ?? string.Empty;
            if (!PlayerData.VerifyName(name))
            {
                Error("角色创建返回的名字非法（\"" + name + "\"）→ 不提交；请重新创建");
                m_profilePromptShown = false;
                return;
            }
            string skinName = (request.SkinName ?? string.Empty).Trim();
            if (!SkinAssetPolicy.TryValidateSkinName(skinName, allowBuiltIn: true, out string skinReason))
            {
                Error("角色创建返回的皮肤名非法（\"" + skinName + "\"）：" + skinReason + " → 不提交");
                m_profilePromptShown = false;
                return;
            }
            bool custom = SkinAssetPolicy.IsCustomSkinName(skinName);
            byte[] skinHash = Array.Empty<byte>();
            PlayerSkinAssetRecord skinRecord = null;
            if (custom)
            {
                if (!request.HasCustomAsset)
                {
                    Error("自定义皮肤 \"" + skinName + "\" 没有资源字节 → 不提交（请重新选择/导入）");
                    m_profilePromptShown = false;
                    return;
                }
                if (!SkinAssetPolicy.TryValidateAssetData(request.SkinAsset, out string assetReason))
                {
                    Error("自定义皮肤 \"" + skinName + "\" 资源非法：" + assetReason + " → 不提交");
                    m_profilePromptShown = false;
                    return;
                }
                if (m_skinPort == null)
                {
                    Error("没有引擎皮肤端口（组合根没有注入 PlayerSkinStore）→ 无法缓存自定义皮肤；" +
                        "不提交（不换成内置皮肤）");
                    m_profilePromptShown = false;
                    return;
                }
                if (!m_skinPort.TryDecodeAssetData(request.SkinAsset, out _, out _, out string decodeReason))
                {
                    Error("自定义皮肤 \"" + skinName + "\" 无法解码：" + decodeReason + " → 不提交");
                    m_profilePromptShown = false;
                    return;
                }
                skinHash = SkinAssetPolicy.Sha256(request.SkinAsset);
                skinRecord = new PlayerSkinAssetRecord
                {
                    SkinName = skinName,
                    Sex = request.Sex,
                    Sha256 = skinHash,
                    Data = (byte[])request.SkinAsset.Clone()
                };
                if (!m_skinPort.StoreAsset(skinRecord, SkinAssetOrigin.Owned, out _, out string storeReason))
                {
                    Error("缓存自定义皮肤失败：" + storeReason + " → 不提交");
                    m_profilePromptShown = false;
                    return;
                }
            }
            else if (!IsKnownBuiltInSkin(skinName, out skinReason))
            {
                Error("角色创建返回的内置皮肤 \"" + skinName + "\" 不可用：" + skinReason + " → 不提交");
                m_profilePromptShown = false;
                return;
            }
            var profile = new PlayerProfileSnapshot
            {
                Name = name,
                Appearance = new PlayerAppearance
                {
                    Sex = request.Sex,
                    SkinName = skinName,
                    SkinSha256 = skinHash,
                    Clothes = PlayerAppearance.CreateEmptyClothes()
                }
            };
            if (m_profileStore == null)
            {
                Error("没有本机资料存储（组合根没有注入 LocalPlayerProfileStore）→ 无法提交角色资料；" +
                    "请从 Play 屏重新加入房间（不假装已提交）");
                return;
            }
            m_profileStore.Set(profile, "character-screen");
            m_localProfileSignature = string.Empty;
            m_appliedSelfSignature = string.Empty;
            Info("角色创建完成：名字=" + profile.Name + " · 性别=" + request.Sex +
                " · 皮肤=" + skinName +
                (custom ? "（自定义 · " + skinHash.Length + " 字节哈希 · " + request.SkinAsset.Length +
                    "B 资源已缓存）" : "（内置）") +
                " → 重新握手；主机建记录后广播，客户端随首次加入上传资源");
            if (m_handshake == null || !m_handshake.ReconnectClient("角色创建完成"))
                Error("角色创建完成后无法重新握手（没有可重连的客户端通道）→ 请从 Play 屏重新加入房间；" +
                    "资料已保存在本机资料存储里，不会丢");
        }

        // ---------------------------------------------------------------- 编辑玩家屏（PlayerScreen）退出同步

        /// <summary>
        /// 引擎 Edit Players 屏**退出**时调用（唯一入口；由 Engine/PlayerEditWatcher 在
        /// <c>ScreensManager.CurrentScreen</c> 引用变化时触发）。
        ///
        /// 语义（用户口径 2026-10-20）：
        ///   · 允许重名：这里没有任何名字唯一性判断；
        ///   · 主机：每个变化的本机槽位（clientId=0, slot=0..3）写进
        ///     <c>SuMultiplayerPlayers.xml</c> 的身份记录（按 (身份,槽位) 键），并立即
        ///     <c>PlayerProfile</c> + 整表名册广播 —— 客户端因此看到主机改名后的名字；
        ///   · 客户端：每个变化的本机槽位按 <c>(本机 clientId, slot)</c> 发 <c>PlayerProfile</c>
        ///     给主机；自定义皮肤先走皮肤模块的资源上传 / 校验 / 广播链，内置皮肤只走资料消息；
        ///   · 资料最小校验（长度/字符集/皮肤/衣服边界）失败 → 计数 + 限流日志，绝不半应用。
        ///
        /// 不轮询：本方法只在屏幕退出时被调用一次。
        /// </summary>
        public void OnEditScreenExited(PlayerEditSnapshot enter, PlayerEditSnapshot exit)
        {
            if (!m_attached) return;
            if (enter == null || exit == null)
            {
                LogRateLimited("edit-null-snapshot",
                    "编辑玩家屏退出：进入/退出快照为空 → 不产生资料更新（不猜测槽位）");
                return;
            }
            PlayerEditDiff diff = PlayerEditSync.Compare(enter, exit);
            if (!diff.AnyChanged) return;
            bool hosting = m_broadcast?.IsHosting == true;
            bool connected = m_transport?.IsConnected == true;
            if (!hosting && !connected)
            {
                LogRateLimited("edit-no-session",
                    "编辑玩家屏退出：本端不在联机会话里（未建房 / 未连接）→ 名字/皮肤只留在本机，" +
                    "不发任何网络消息（没有对端可同步）");
                return;
            }
            if (!exit.HasWorld)
            {
                LogRateLimited("edit-no-world-exit",
                    "编辑玩家屏退出：没有加载中的世界 → 不产生资料更新（原因：" +
                    (exit.Reason ?? "未知") + "）");
                return;
            }
            string identity = PlayerIdentity.LocalIdentity();
            if (!hosting)
            {
                // 本机槽位集合先强制重发一次：新增/删除槽位 / 槽位>0 的资料必须让主机先登记
                // （否则主机按"未公告槽位"拒绝 profile）。
                ForceLocalSlotsAnnouncement("编辑玩家屏退出");
            }
            int nameChanges = 0;
            int skinChanges = 0;
            int resubmits = 0;
            int broadcasts = 0;
            bool rosterDirty = false;
            bool removedSlots = false;
            for (int i = 0; i < diff.Changes.Count; i++)
            {
                PlayerEditSlotChange change = diff.Changes[i];
                if (change == null) continue;
                if (change.Removed)
                {
                    removedSlots = true;
                    if (hosting)
                    {
                        Info("编辑玩家屏：主机移除了本机角色槽位 " + change.Slot +
                            " → 广播离开通知（客户端摘掉这个化身）");
                        BroadcastAvatarRemove(new PlayerKey(0, change.Slot), identity,
                            "主机在编辑玩家屏移除了本机角色槽位 " + change.Slot);
                        rosterDirty = true;
                    }
                    continue;
                }
                if (change.After == null) continue;
                // 先把槽位范围钉死；名字 / 皮肤 / 衣服 / 自定义哈希的完整校验放到**补哈希之后**的
                // TryValidateProfile（否则"只改名字但本会话还没有自定义皮肤哈希"会被误拒）。
                if (!PlayerEditSync.TryValidateSlot(change.Slot, out string validateReason))
                {
                    RejectEditedProfile(change.Slot, validateReason);
                    continue;
                }
                PlayerData player = TryGetLocalPlayerAtSlot(change.Slot, out string lookupReason);
                if (player == null)
                {
                    RejectEditedProfile(change.Slot,
                        "本机槽位 " + change.Slot + " 在退出时不存在（" + lookupReason + "）");
                    continue;
                }
                PlayerProfileSnapshot profile = m_spawner.CaptureProfile(player,
                    out string captureReason);
                if (profile == null)
                {
                    RejectEditedProfile(change.Slot,
                        "退出时抓不到槽位 " + change.Slot + " 的资料（" + captureReason + "）");
                    continue;
                }
                // 只改名字 / 衣服时，自定义皮肤的哈希可能还没进本会话缓存（重连后资源还没到）：
                // 从角色记录 / 本机资料存储补上同一个皮肤的哈希，避免"改名因为皮肤哈希为空被拒"。
                FillCustomSkinHashFromKnownSources(change.Slot, profile);
                // 自定义皮肤：先走皮肤模块的既有资源链（读取 → 校验 → 分片上传 / 主机写记录 + 广播）。
                // 失败时仍继续尝试资料消息：只要资料里有合法哈希，接收端会显示确定性占位并计数，
                // 不会静默换成内置皮肤。没有哈希时下面的 TryValidateProfile 会明确拒绝。
                if (change.SkinChanged && change.IsCustomSkin && hosting)
                {
                    // 主机：皮肤模块的 StoreRecordSkin 要求 (身份,槽位) 记录已存在；
                    // 只有记录**还不存在**时才先建（已有记录不动，避免在皮肤资源暂时读不到时
                    // 把记录里已保存的 SkinAsset 清空 —— 那是重连补发的唯一来源）。
                    string preRecordKey = PlayerIdentity.RecordKey(PlayerIdentity.LocalIdentity(),
                        profile.Name, change.Slot);
                    if (!m_records.TryGet(preRecordKey, out _) &&
                        !TryStoreEditedLocalRecord(change.Slot, profile, out string preReason))
                    {
                        RejectEditedProfile(change.Slot,
                            "写角色记录失败（自定义皮肤前置）：" + preReason);
                        continue;
                    }
                }
                if (change.SkinChanged && change.IsCustomSkin)
                {
                    if (m_editSkinSink == null)
                    {
                        m_editCounters.OnMissingAsset();
                        LogRateLimited("edit-skin-sink-missing:" + change.Slot,
                            "编辑玩家屏：自定义皮肤资源同步端口未注入（组合根没有接 PlayerSkinModule）→ " +
                            "已计 missingAsset；仍按资料里的哈希继续（接收端会显示确定性占位）");
                    }
                    else if (!m_editSkinSink.TrySyncEditedSkin(profile, change.Slot, hosting,
                            out string skinReason))
                    {
                        m_editCounters.OnMissingAsset();
                        LogRateLimited("edit-skin-missing:" + change.Slot + ":" + skinReason,
                            "编辑玩家屏：自定义皮肤资源同步失败（槽位 " + change.Slot + "）：" +
                            skinReason + " → 已计 missingAsset；仍按资料里的哈希继续" +
                            "（接收端显示确定性占位，不换成内置皮肤）");
                    }
                    else
                    {
                        // 资源已进缓存：重新抓一次，让资料带上真实哈希。
                        PlayerProfileSnapshot refreshed = m_spawner.CaptureProfile(player, out _);
                        if (refreshed != null) profile = refreshed;
                    }
                }
                if (!PlayerEditSync.TryValidateProfile(profile, change.Slot, out string profileReason))
                {
                    RejectEditedProfile(change.Slot, profileReason);
                    continue;
                }
                if (hosting)
                {
                    if (!TryStoreEditedLocalRecord(change.Slot, profile, out string storeReason))
                    {
                        RejectEditedProfile(change.Slot, "写角色记录失败：" + storeReason);
                        continue;
                    }
                    PlayerKey key = new PlayerKey(0, change.Slot);
                    m_hostSlotSignatures[change.Slot] = profile.Signature;
                    // 主机自己的资料必须立刻广播（客户端旧实现只靠 1 秒轮询，改名会滞后/丢失）。
                    BroadcastProfile(key, profile, isHost: true, identity: identity);
                    broadcasts++;
                    rosterDirty = true;
                    Info("编辑玩家屏：主机本机角色已更新并广播（玩家键 " + key + " · 名字=" +
                        profile.Name + " · 性别=" + profile.Appearance.Sex + " · 皮肤=" +
                        profile.Appearance.SkinName +
                        (change.SkinChanged && change.IsCustomSkin ? "（自定义皮肤资源已同步）" : string.Empty) +
                        "）");
                }
                else
                {
                    if (!TrySubmitEditedProfile(change.Slot, profile, out string sendReason))
                    {
                        RejectEditedProfile(change.Slot, sendReason);
                        continue;
                    }
                    resubmits++;
                }
                if (change.NameChanged) nameChanges++;
                if (change.SkinChanged) skinChanges++;
            }
            if (nameChanges > 0) m_editCounters.OnNameChanged(nameChanges);
            if (skinChanges > 0) m_editCounters.OnSkinChanged(skinChanges);
            if (resubmits > 0) m_editCounters.OnProfileResubmitted(resubmits);
            if (broadcasts > 0) m_editCounters.OnHostProfileBroadcast(broadcasts);
            if (hosting && (rosterDirty || removedSlots))
                BroadcastRoster("编辑玩家屏退出：主机本机角色资料变化", force: true);
            if (hosting)
            {
                // 事件驱动的整表对齐（不是轮询）：补齐编辑屏之外可能发生的槽位增删 / 签名变化。
                SyncHostLocalSlots("编辑玩家屏退出");
            }
        }

        /// <summary>
        /// 客户端：把编辑后的某个本机槽位资料作为 <c>PlayerProfile</c> 提交给主机。
        /// 玩家键 = <c>(本机 clientId, slot)</c>；主机按连接编号 + 申报槽位校验，载荷里的 clientId 不作权威。
        /// </summary>
        private bool TrySubmitEditedProfile(int slot, PlayerProfileSnapshot profile, out string reason)
        {
            reason = null;
            if (m_messages == null)
            {
                reason = "没有消息注册表，无法提交资料更新";
                return false;
            }
            if (m_transport == null || !m_transport.IsConnected)
            {
                reason = "本机客户通道未连接";
                return false;
            }
            int localClientId = m_transport.LocalClientId;
            if (localClientId < 0)
            {
                reason = "本机还没有握手编号（HelloAck 未到）";
                return false;
            }
            string identity = PlayerIdentity.LocalIdentity();
            if (identity.Length == 0)
            {
                reason = "本机身份（UserId.dat）为空";
                return false;
            }
            var key = new PlayerKey(localClientId, slot);
            Proto.PlayerProfile message = PlayerProfileCodec.ToMessage(key, identity, profile,
                isHost: false, hasCharacter: true);
            if (!m_messages.Sender.Send(0, message))
            {
                reason = "资料更新发送失败（通道不可用 / 队列满）";
                return false;
            }
            m_localSlotProfileSignatures[slot] = profile.Signature;
            if (slot == 0)
            {
                m_localProfileSignature = profile.Signature;
                m_profileSendPending = false;
                // 本机资料存储是重连 Hello 的唯一来源：编辑后同步更新，重连不会带回旧名字。
                m_profileStore?.Set(profile, "edit-player");
            }
            Info("编辑玩家屏：已提交本机槽位资料给主机（玩家键 " + key + " · 名字=" + profile.Name +
                " · 性别=" + profile.Appearance.Sex + " · 皮肤=" + profile.Appearance.SkinName + "）");
            return true;
        }

        /// <summary>
        /// 名字 / 衣服变化时，自定义皮肤的哈希可能还没进本会话缓存（重连后资源还没到）。
        /// 用**已有的角色记录**（主机）或**本机资料存储**（客户端槽位 0）补上同一皮肤名的哈希；
        /// 只补哈希，不动资源，也绝不换成别的皮肤。找不到就保持空（上层会明确拒绝）。
        /// </summary>
        private void FillCustomSkinHashFromKnownSources(int slot, PlayerProfileSnapshot profile)
        {
            if (profile?.Appearance == null) return;
            string skinName = (profile.Appearance.SkinName ?? string.Empty).Trim();
            if (!SkinAssetPolicy.IsCustomSkinName(skinName)) return;
            if (SkinAssetPolicy.IsValidHash(profile.Appearance.SkinSha256)) return;
            string identity = PlayerIdentity.LocalIdentity();
            if (TryGetRecordSkin(identity, profile.Name, slot,
                    out PlayerSkinAssetRecord record, out _) &&
                record != null &&
                string.Equals(record.SkinName ?? string.Empty, skinName,
                    StringComparison.Ordinal) &&
                SkinAssetPolicy.IsValidHash(record.Sha256))
            {
                profile.Appearance.SkinSha256 = (byte[])record.Sha256.Clone();
                Info("编辑玩家屏：槽位 " + slot + " 的自定义皮肤哈希从角色记录补齐（\"" +
                    skinName + "\" · " + SkinAssetPolicy.HashHex(record.Sha256) + "）");
                return;
            }
            if (slot != 0) return;
            PlayerProfileSnapshot stored = m_profileStore?.Profile;
            if (stored?.Appearance == null ||
                !string.Equals((stored.Appearance.SkinName ?? string.Empty).Trim(), skinName,
                    StringComparison.Ordinal) ||
                !SkinAssetPolicy.IsValidHash(stored.Appearance.SkinSha256)) return;
            profile.Appearance.SkinSha256 = (byte[])stored.Appearance.SkinSha256.Clone();
            Info("编辑玩家屏：槽位 0 的自定义皮肤哈希从本机资料存储补齐（\"" + skinName +
                "\" · " + SkinAssetPolicy.HashHex(profile.Appearance.SkinSha256) + "）");
        }

        /// <summary>
        /// 主机：把编辑后的本机槽位资料写进**身份记录**（<c>SuMultiplayerPlayers.xml</c>，按 (身份,槽位) 键）。
        /// 重连 / 其他客户端建化身都从这份记录读名字与外观；重复名字不做任何去重。
        /// </summary>
        private bool TryStoreEditedLocalRecord(int slot, PlayerProfileSnapshot profile,
            out string reason)
        {
            reason = null;
            string identity = PlayerIdentity.LocalIdentity();
            if (identity.Length == 0)
            {
                LogRateLimited("edit-record-no-identity",
                    "主机本机身份（UserId.dat）为空 → 角色记录按名字绑定；改名后旧记录不会自动删除" +
                    "（身份不可用，无法稳定定位同一条记录）");
            }
            string recordKey = PlayerIdentity.RecordKey(identity, profile.Name, slot);
            m_records.TryGet(recordKey, out NetworkPlayerRecord record);
            if (record == null)
            {
                record = CreateInitialPlayerRecord(profile);
                Info("编辑玩家屏：为主机本机槽位 " + slot + " 新建角色记录（记录键=" +
                    PlayerIdentity.Describe(recordKey) + "）");
            }
            else
            {
                ApplyProfileToRecord(record, profile);
            }
            record.NameConfirmed = true;
            // 自定义皮肤资源由皮肤模块写进同一条记录；这里只同步名字/性别/皮肤名/哈希/衣服，
            // ApplyProfileToRecord 在哈希相同时保留记录里的资源字节。
            m_records.Put(recordKey, record);
            if (!m_records.SaveIfDirty())
            {
                reason = "写角色记录文件失败（SuMultiplayerPlayers.xml）";
                return false;
            }
            ResolveName(recordKey, profile.Name, "主机本机角色编辑（PlayerScreen）");
            return true;
        }

        /// <summary>
        /// 客户端：本机槽位集合强制重发一次（编辑后新增/删除槽位、槽位>0 的资料更新都要求先登记）。
        /// </summary>
        private void ForceLocalSlotsAnnouncement(string why)
        {
            if (m_transport == null || !m_transport.IsConnected) return;
            if (m_transport.LocalClientId < 0) return;
            m_localSlotsSignature = string.Empty;
            UpdateLocalSlotsAnnouncement();
            Info("编辑玩家屏：已强制重发本机槽位集合（" + why + "）");
        }

        /// <summary>本机槽位 → 引擎 PlayerData（下标 = TryCollectLocalPlayers 的顺序）。</summary>
        private PlayerData TryGetLocalPlayerAtSlot(int slot, out string reason)
        {
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project";
                return null;
            }
            var players = new List<PlayerData>(PlayerKey.SlotCount);
            if (!m_spawner.TryCollectLocalPlayers(project, players, out _, out reason)) return null;
            if (slot < 0 || slot >= players.Count)
            {
                reason = "本机只有 " + players.Count + " 个槽位（0.." + (players.Count - 1) + "）";
                return null;
            }
            return players[slot];
        }

        private void RejectEditedProfile(int slot, string reason)
        {
            m_editCounters.OnProfileUpdateRejected(reason);
            LogRateLimited("edit-rejected:" + slot + ":" + reason,
                "编辑玩家屏：槽位 " + slot + " 的资料更新被拒：" + reason +
                "（保留原资料，不发半套更新）");
        }

        // ---------------------------------------------------------------- 皮肤记录端口（IPlayerSkinRecordPort）

        public bool IsHosting => m_broadcast?.IsHosting == true;

        public bool TryGetLocalIdentity(out string identity, out string reason)
        {
            identity = PlayerIdentity.LocalIdentity();
            if (identity.Length == 0)
            {
                reason = "本机身份（UserId.dat）为空";
                return false;
            }
            reason = null;
            return true;
        }

        public bool TryCaptureLocalProfile(out PlayerProfileSnapshot profile, out string reason) =>
            TryCaptureLocalProfile(0, out profile, out reason);

        /// <summary>
        /// 抓本机**指定槽位**的资料（槽位 0..3；索引口径与 TryCollectLocalPlayers 完全一致）。
        /// 槽位不存在时返回 false + 明确原因（不回退到槽位 0，避免把别的角色资料当成这个槽位）。
        /// </summary>
        public bool TryCaptureLocalProfile(int slot, out PlayerProfileSnapshot profile,
            out string reason)
        {
            profile = null;
            PlayerData player = TryGetLocalPlayerAtSlot(slot, out reason);
            if (player == null) return false;
            profile = m_spawner.CaptureProfile(player, out reason);
            return profile != null;
        }

        public bool IsKnownBuiltInSkin(string skinName, out string reason)
        {
            reason = null;
            if (!SkinAssetPolicy.IsBuiltInSkinName(skinName))
            {
                reason = "\"" + (skinName ?? string.Empty) + "\" 不是内置皮肤名";
                return false;
            }
            CharacterSkinsManager.UpdateCharacterSkinsList();
            if (!CharacterSkinsManager.CharacterSkinsNames.Contains(skinName))
            {
                reason = "内置皮肤 \"" + skinName + "\" 不在本机皮肤列表里";
                return false;
            }
            return true;
        }

        public bool TryGetRecordSkin(string identity, string name, out PlayerSkinAssetRecord skin,
            out string reason) =>
            TryGetRecordSkin(identity, name, 0, out skin, out reason);

        /// <summary>按 (身份, 槽位) 取记录里的皮肤（槽位 0 与旧口径完全一致）。</summary>
        public bool TryGetRecordSkin(string identity, string name, int slot,
            out PlayerSkinAssetRecord skin, out string reason)
        {
            skin = null;
            reason = null;
            string recordKey = PlayerIdentity.RecordKey(identity, name, slot);
            if (!m_records.TryGet(recordKey, out NetworkPlayerRecord record) || record == null)
            {
                reason = "身份 " + PlayerIdentity.Describe(recordKey) + " 没有角色记录";
                return false;
            }
            skin = new PlayerSkinAssetRecord
            {
                // 角色记录的不常变内容 hash：皮肤补发前用它和该连接申报的已知 hash 比对。
                RecordHash = record.RecordHash,
                SkinName = record.SkinName ?? string.Empty,
                Sex = NetworkAvatarSpawner.ToPlayerSex(record.PlayerClass),
                Sha256 = record.SkinSha256 != null ? (byte[])record.SkinSha256.Clone()
                    : Array.Empty<byte>(),
                Data = record.SkinAsset != null ? (byte[])record.SkinAsset.Clone()
                    : Array.Empty<byte>()
            };
            return true;
        }

        public bool StoreRecordSkin(string identity, string name, int clientId,
            PlayerSkinAssetRecord skin, out string reason) =>
            StoreRecordSkin(identity, name, clientId, skin, 0, out reason);

        /// <summary>按 (身份, 槽位) 写角色记录里的皮肤；clientId &gt; 0 时同步该槽位的名册资料。</summary>
        public bool StoreRecordSkin(string identity, string name, int clientId,
            PlayerSkinAssetRecord skin, int slot, out string reason)
        {
            reason = null;
            if (skin == null)
            {
                reason = "皮肤记录为空";
                return false;
            }
            if (!PlayerEditSync.TryValidateSlot(slot, out reason)) return false;
            if (m_records.LoadFailed)
            {
                reason = "角色记录文件先前解析失败，本会话拒绝写盘";
                return false;
            }
            string recordKey = PlayerIdentity.RecordKey(identity, name, slot);
            if (!m_records.TryGet(recordKey, out NetworkPlayerRecord record) || record == null)
            {
                reason = "身份 " + PlayerIdentity.Describe(recordKey) + " 没有角色记录（尚未创建）";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(skin.SkinName)) record.SkinName = skin.SkinName.Trim();
            record.PlayerClass = NetworkAvatarSpawner.ToPlayerClass(skin.Sex);
            record.SkinSha256 = skin.Sha256 != null ? (byte[])skin.Sha256.Clone() : Array.Empty<byte>();
            record.SkinAsset = skin.Data != null ? (byte[])skin.Data.Clone() : Array.Empty<byte>();
            record.NameConfirmed = true;
            m_records.Put(recordKey, record);
            if (!m_records.SaveIfDirty())
            {
                reason = "写角色记录文件失败（SuMultiplayerPlayers.xml）";
                return false;
            }
            if (clientId > 0)
            {
                PlayerKey key = new PlayerKey(clientId, slot);
                PeerSlot peer = EnsurePeerSlot(key);
                peer.Identity = identity ?? string.Empty;
                peer.RecordKey = recordKey;
                peer.Profile = RecordToProfile(record);
                m_peerIdentities[clientId] = identity ?? string.Empty;
                UpdateSkinAppearance(clientId, identity, name, skin, slot);
            }
            return true;
        }

        public void UpdateSkinAppearance(int clientId, string identity, string name,
            PlayerSkinAssetRecord skin) =>
            UpdateSkinAppearance(clientId, identity, name, skin, 0);

        /// <summary>按 (clientId, 槽位) 把皮肤哈希/名称同步进名册并广播。</summary>
        public void UpdateSkinAppearance(int clientId, string identity, string name,
            PlayerSkinAssetRecord skin, int slot)
        {
            if (clientId <= 0) return;    // 主机自己的资料由编辑屏退出 / 记录刷新广播
            if (!PlayerEditSync.TryValidateSlot(slot, out string slotReason))
            {
                ErrorOnce("skin-appearance-slot:" + clientId + ":" + slot,
                    "更新皮肤名册时槽位非法（" + slotReason + "）→ 丢弃这次名册更新（不夹紧）");
                return;
            }
            PlayerKey key = new PlayerKey(clientId, slot);
            PlayerProfileSnapshot profile = GetPeerProfile(key);
            if (profile?.Appearance == null) return;
            if (skin != null && !string.IsNullOrWhiteSpace(skin.SkinName))
                profile.Appearance.SkinName = skin.SkinName.Trim();
            if (skin != null)
            {
                profile.Appearance.SkinSha256 = skin.Sha256 != null
                    ? (byte[])skin.Sha256.Clone()
                    : Array.Empty<byte>();
            }
            BroadcastProfile(key, profile, isHost: false, identity: identity);
            BroadcastRoster("皮肤资源 / 哈希更新（玩家键 " + key + "）");
        }

        // ---------------------------------------------------------------- 客户端侧

        private void TickClient(Project project)
        {
            if (project == null) return;
            // ★ Phase 2 钩子：Project 由 GameLoading 加载完成后的**第一个客户端 tick**。
            // 此刻世界还在、PlayersData 已经是从主机整包下载来的角色集合；必须先把不属于本机的
            // 主机角色副本摘掉，后面的阶段（地形更新位置 / 本机资料 / Phase 1 槽位公告 / 远端化身）
            // 才会只看到本机自己的角色。
            NormalizeDownloadedLocalPlayers(project);
            // 旧实现 MaintainRemoteTerrainLocations 的客户端分支：远端化身的更新位置摘掉，
            // 只给**本机玩家**挂（可见范围 = 本机可见距离）。兴趣半径由地形模块按本机可见距离
            // 上报给主机（旧 :486-494 的 SendClientTerrainInterestUpdate）。
            // 这一步必须在"主机角色已创建"之前也跑：本机地形窗口与角色建没建无关。
            m_spawner.ApplyTerrainUpdateLocations(isHost: false);
            if (m_transport == null || !m_transport.IsConnected) return;
            if (m_downloadedWorlds == null) return;
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(false);
            string directory = gameInfo?.DirectoryName;
            if (string.IsNullOrEmpty(directory)) return;
            // 客户端必须活在**下载来的那个联机世界**里才建化身
            // —— Source: ScMultiplayerWorldTransferHandlers.cs:558-562（m_shouldCreateHostAvatar）。
            // 这里是精确判定（世界目录在"下载来的世界"登记表里），不是"只要连着通道就算"。
            if (!m_downloadedWorlds.IsDownloadedWorld(directory)) return;

            if (m_roster.LocalClientId != m_transport.LocalClientId)
            {
                m_roster.LocalClientId = m_transport.LocalClientId;
                // 新连接 / 重新握手：允许主机分配资料按新快照再应用一次（旧快照的应用标记作废）。
                m_appliedAssignedProfileSignature = string.Empty;
                m_keptLocalStoreSignature = string.Empty;
                // 自己那一条已经登记过的话，重新标记 IsSelf（名册按身份去重，不会多出化身）。
                SeedSelfFlagFromRoster();
            }

            // 主机要求先创建角色：本连接不会有世界（旧 SCMP_PROFILE_REQUIRED）。
            if (m_handshake != null && m_handshake.ProfileRequired)
            {
                HandleProfileRequired();
                return;
            }

            // 主机给本端的角色记录（HelloAck.assigned_profile）：写进本机资料存储，客户端自己的
            // 名字/外观以主机记录为准（重连时不会用下载世界里的主机角色冒充自己）。
            PlayerProfileSnapshot assigned = m_handshake?.AssignedProfile;
            if (assigned != null && assigned.HasName)
            {
                // **只按"分配快照换了一份"应用一次**：旧实现比较"本机资料存储 != 分配快照"，
                // 每帧都会把分配快照重新写回存储 —— 玩家在编辑屏改名/换肤后，下一帧就被旧快照
                // 覆盖（nameChanged/skinChanged 恒为 0、profileSent 不涨的根因）。
                // 本机编辑走 108 / 编辑屏退出上报，主机记录更新后由 108 广播回来。
                if (!string.Equals(m_appliedAssignedProfileSignature, assigned.Signature,
                        StringComparison.Ordinal))
                {
                    m_appliedAssignedProfileSignature = assigned.Signature;
                    m_keptLocalStoreSignature = string.Empty;
                    m_profileStore?.Set(assigned, "host-record");
                    m_assignedProfileApplied++;
                    Info("主机分配的本机角色资料：名字=" + assigned.Name + " · 性别=" +
                        assigned.Appearance.Sex + " · 皮肤=" + assigned.Appearance.SkinName +
                        " → 应用到本机角色（下载世界自带的角色不会冒充自己）");
                }
                else if (m_profileStore != null &&
                         !string.Equals(m_profileStore.Profile?.Signature ?? string.Empty,
                             assigned.Signature, StringComparison.Ordinal))
                {
                    // 分配快照已经应用过，但本机资料存储被一次更新的编辑改过 → 保留本机编辑，
                    // 等 108 / 编辑屏退出通道把它送到主机（不再用旧快照回滚）。
                    string storeSignature = m_profileStore.Profile?.Signature ?? string.Empty;
                    if (!string.Equals(m_keptLocalStoreSignature, storeSignature,
                            StringComparison.Ordinal))
                    {
                        m_keptLocalStoreSignature = storeSignature;
                        m_assignedProfileKeptLocal++;
                    }
                    LogRateLimited("assigned-kept-local",
                        "主机分配资料是加入时的快照，本机资料存储已有更新（编辑玩家屏改名/换肤）" +
                        "→ 保留本机值，等 108 / 编辑屏退出上报主机（不再每帧用旧快照覆盖；" +
                        "assignedProfileKeptLocal=" + m_assignedProfileKeptLocal + "）");
                }
                else
                {
                    m_keptLocalStoreSignature = string.Empty;
                }
            }

            // 主机化身：来自 HelloAck.host_profile（名字/性别/皮肤/衣服都是主机的真实资料）。
            SeedHostAvatarFromHandshake();

            // 本机玩家 = 自己的角色（名字/性别/皮肤/衣服）；性别不同会按旧实现替换 PlayerData。
            ApplySelfProfileFromStore();

            // 主机 + 其他每个客户端：每个身份都要有一个化身（这是客户端互相可见的唯一入口）。
            ReconcileClientAvatars();
            if (!m_hostAvatarCreated && m_roster.TryGet(PlayerKey.Host, out _) &&
                m_spawner.TryGet(PlayerKey.Host, out _))
            {
                m_hostAvatarCreated = true;
                m_createdHostAvatars++;
            }

            // 本机资料是**事件驱动**上报的：首次应用主机分配的资料 / 编辑玩家屏退出 / 重连后
            // 置 m_profileSendPending，这里只在标志为 true 时抓一次资料并发送（不逐帧轮询玩家资料）。
            if (m_profileSendPending) SendLocalProfileIfChanged();
            // 本机槽位（分屏本机角色）集合：握手后 / 增删时上报，主机据此给每个槽位建一个化身。
            UpdateLocalSlotsAnnouncement();
            // 最后应用"主机角色记录里的最后离线位置 + 复活点"：必须在归一化之后、本机角色存在时做。
            // 客户端本地角色是下载世界里的新实体（Players 段是主机存档角色），不搬过去就会出生在
            // 主机角色旁边（实机 bug 2026-10-27）。
            ApplyAssignedPlacementOnce(project);
        }

        /// <summary>
        /// 客户端：按 HelloAck 携带的主机角色记录位置把**本机角色**放到最后离线位置，并把
        /// <c>PlayerData.SpawnPosition</c> 设为该角色最后睡觉的复活点。
        /// 解析/落点全部来自主机权威记录（SuMultiplayerPlayers.xml），不改 Project.xml。
        /// </summary>
        private void ApplyAssignedPlacementOnce(Project project)
        {
            if (project == null || ReferenceEquals(project, m_placementAppliedProject)) return;
            if (m_handshake == null || !m_handshake.HasAssignedPlacement) return;
            PlayerPlacement placement = m_handshake.AssignedPosition;
            PlayerPlacement spawnPlacement = m_handshake.AssignedSpawnPosition;
            if (!placement.IsFinite)
            {
                Info("主机下发的角色重新进入位置非法（非有限值）→ 保持引擎出生行为（不猜坐标）");
                m_placementAppliedProject = project;
                return;
            }
            Vector3 position = new Vector3(placement.X, placement.Y, placement.Z);
            Vector3 spawnPosition = spawnPlacement.IsFinite
                ? new Vector3(spawnPlacement.X, spawnPlacement.Y, spawnPlacement.Z)
                : position;
            var localPlayers = new List<PlayerData>(1);
            if (!m_spawner.TryCollectLocalPlayers(project, localPlayers, out _, out string collectReason) ||
                localPlayers.Count == 0)
            {
                m_placementPendingTicks++;
                if (m_placementPendingTicks == 1 || m_placementPendingTicks % 50 == 0)
                {
                    Info("本机角色重新进入位置暂不能应用（" + (collectReason ?? "本机角色还没创建") +
                        "）→ 继续等待本机角色 " + m_placementPendingTicks + " tick");
                }
                return;
            }
            PlayerData playerData = localPlayers[0];
            ComponentPlayer player = playerData.ComponentPlayer;
            ComponentBody body = player?.ComponentBody;
            if (body == null)
            {
                // 引擎原生 PrepareSpawn → WaitForTerrain 可能等几秒；这期间 GameScreen 会用默认
                // 相机画在下载世界的 GlobalSpawnPosition（主机进入地图的位置），玩家先看到主机
                // 位置再瞬移（实机反馈 2026-10-27）。这里按旧实现立刻以 Respawn 模式出生本机角色，
                // 让相机在**第一帧就挂到记录位置**，不等地形等待。
                playerData.SpawnPosition = position;
                if (!m_spawner.TrySpawnClientLocalPlayerNow(playerData, position, out string earlyReason))
                {
                    ErrorOnce("assigned-placement-early-spawn",
                        "提前出生本机角色失败（" + (earlyReason ?? "未知") +
                        "）；先保留 SpawnPosition，等引擎原生出生后再贴合");
                    m_placementPendingTicks++;
                    return;
                }
                player = playerData.ComponentPlayer;
                body = player?.ComponentBody;
                if (body == null)
                {
                    m_placementPendingTicks++;
                    return;
                }
                Info("客户端本机角色已提前出生（不等原生 WaitForTerrain），出生位置=" +
                    DescribePlacement(placement));
            }
            if (player.ComponentHealth != null && player.ComponentHealth.Health <= 0f)
            {
                // 刚进世界就是死亡态：等复活链路自己落点，不在这里和死亡流程抢位置。
                m_placementPendingTicks++;
                return;
            }
            try
            {
                body.Position = position;
                body.Velocity = Vector3.Zero;
                playerData.SpawnPosition = spawnPosition;
                m_placementAppliedProject = project;
                m_placementApplied++;
                Info("本机角色已放到主机记录的最后离线位置 " + DescribePlacement(placement) +
                    "（复活点 " + DescribePlacement(spawnPlacement.IsFinite ? spawnPlacement : placement) +
                    "，来自主机的 SuMultiplayerPlayers.xml，不写 Project.xml）");
            }
            catch (Exception e)
            {
                ErrorOnce("assigned-placement:" + project.GetHashCode(),
                    "应用主机记录的本机角色位置失败（" + e.GetType().Name + " " + e.Message +
                    "）→ 本机角色停在引擎出生点（明确失败，不伪造位置）");
                m_placementAppliedProject = project;
            }
        }

        private static string DescribePlacement(PlayerPlacement placement) =>
            "(" + placement.X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
            "," + placement.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
            "," + placement.Z.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ")";

        /// <summary>
        /// Phase 2：客户端把**下载世界带来的主机角色副本**从引擎里摘掉，只留下本设备真正操作的
        /// 本机角色；摘完强制 Phase 1 重新枚举槽位（清空 <c>m_localSlotsSignature</c>，下一次
        /// <see cref="UpdateLocalSlotsAnnouncement"/> 只公告本机自己的槽位）。
        ///
        /// 钩子（为什么是这里）：
        ///   · <see cref="IWorldReadyPort"/> 只表示"世界文件已导入完成"，那时 GameLoading 还没加载
        ///     Project，PlayerData 根本不在内存里，无法归一化；
        ///   · 本方法在 <see cref="TickClient"/> 第一行执行 —— Project 已由 GameLoading 加载完成，
        ///     世界目录也已经在下载世界登记表里（<see cref="IDownloadedWorldPort.IsDownloadedWorld"/>）。
        ///     这与 TerrainSyncModule 把 <c>SuSubsystemTerrain.ClientReplica</c> 置为 true 是同一个
        ///     "世界装好了"时刻，只是这里不依赖地形通道：**客户端断线后仍能进入已下载的世界并归一化**。
        ///
        /// 幂等：每个 Project 只做一次；重连（alreadyHasWorld，不换 Project）不重复做；
        /// 整图重下 / 重新加入（换 Project）会重新做。
        /// </summary>
        private void NormalizeDownloadedLocalPlayers(Project project)
        {
            if (!ReferenceEquals(project, m_normalizeProject))
            {
                m_normalizeProject = project;
                m_normalizeApplied = false;
                m_normalizeAlreadyLogged = false;
                m_normalizeLastFailure = string.Empty;
                m_normalizeNoPortLogged = false;
                m_normalizeNoWorldLogged = false;
                m_normalizeNotClientLogged = false;
                m_capacityRefusedKeys.Clear();
                // 新世界（整图重下 / 重新加入）：下载来的本机角色是主机副本，分配快照要重新应用一次。
                m_appliedAssignedProfileSignature = string.Empty;
                m_keptLocalStoreSignature = string.Empty;
            }
            if (m_normalizeApplied)
            {
                // 幂等：每个世界安装只做一次。状态行只计一次 skippedAlready（不随 tick 增长）。
                if (!m_normalizeAlreadyLogged)
                {
                    m_normalizeAlreadyLogged = true;
                    m_normalizeSkippedAlready++;
                }
                return;
            }

            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(false);
            string directory = gameInfo?.DirectoryName;
            if (string.IsNullOrEmpty(directory))
            {
                if (!m_normalizeNoWorldLogged)
                {
                    m_normalizeNoWorldLogged = true;
                    m_normalizeSkippedNoWorld++;
                    Info("本机角色归一化跳过（no-world）：Project 还没有世界目录（世界仍在加载）→ " +
                        "等世界就绪后的第一个客户端 tick 再做");
                }
                return;
            }
            if (m_downloadedWorlds == null)
            {
                if (!m_normalizeNoPortLogged)
                {
                    m_normalizeNoPortLogged = true;
                    m_normalizeFailures++;
                    ErrorOnce("normalize-no-download-port",
                        "本机角色归一化失败：没有下载世界端口（IDownloadedWorldPort）→ 无法判断当前世界" +
                        "是不是下载来的联机世界；不执行归一化（客户端可能把主机角色当成自己的槽位公告）");
                }
                return;
            }
            if (!m_downloadedWorlds.IsDownloadedWorld(directory))
            {
                if (!m_normalizeNotClientLogged)
                {
                    m_normalizeNotClientLogged = true;
                    m_normalizeSkippedNotClient++;
                    Info("本机角色归一化跳过（not-client）：当前世界「" + directory +
                        "」不是下载来的联机世界（本端不是客户端 / 单机世界）→ 本机角色全部属于本机");
                }
                return;
            }

            var localPlayers = new List<PlayerData>(PlayerKey.SlotCount);
            if (!m_spawner.TryCollectLocalPlayers(project, localPlayers, out bool tooMany,
                    out string collectReason))
            {
                NoteNormalizeFailure(collectReason ?? "读本机角色失败");
                return;
            }
            if (tooMany)
            {
                // TryCollectLocalPlayers 已经计数 tooManySlots；这里只把事实写进日志（不是静默截断）。
                Info("本机角色归一化：世界里的本机角色超过引擎上限 " + PlayerKey.SlotCount +
                    "（" + collectReason + "）→ 按枚举结果处理前 " + localPlayers.Count + " 个");
            }
            List<LocalPlayerCandidate> candidates = BuildLocalPlayerCandidates(localPlayers);
            List<LocalPlayerSlotRef> hostSlots = BuildHostSlotRefs();
            List<LocalPlayerSlotRef> ownSlots = BuildOwnSlotRefs();
            int remoteCharacters = CountRemoteCharacters();
            if (hostSlots.Count == 0)
            {
                // 没有主机槽位名册（旧主机 / 名册还没到）：每个本地角色都对应主机档案里的一个角色
                //（包括被本机接管的那条）→ 容量至少按这个数估算，避免少报。
                remoteCharacters = Math.Max(remoteCharacters, candidates.Count);
            }
            LocalPlayerNormalizePlan plan = LocalPlayerNormalization.Decide(
                isClient: true,
                worldReady: true,
                downloadedWorld: true,
                alreadyNormalized: false,
                candidates,
                hostSlots,
                ownSlots,
                ownLocalCountHint: m_localSlotsCount,
                remoteCharacterCount: remoteCharacters);
            if (plan.Failed)
            {
                // 没有任何本机角色：引擎会先走"创建角色"流程。等 PlayerData 出现后重试（限流日志，不静默）。
                NoteNormalizeFailure(plan.FailureReason);
                return;
            }
            if (!plan.Apply)
            {
                // 纯逻辑在这里不应该给出 Skip（上面已经逐项排除），真出现就明确记录而不是假装成功。
                m_normalizeFailures++;
                ErrorOnce("normalize-unexpected-skip",
                    "本机角色归一化意外跳过（" + plan.Skip + "）：" + plan.Reason +
                    " → 本机角色保持原样（这是逻辑错误，不是正常路径）");
                return;
            }
            m_normalizeLastFailure = string.Empty;

            // 执行引擎移除（逐条；失败保留 PlayerData 并让下个 tick 重试，绝不假装摘掉）。
            int removed = 0;
            int failed = 0;
            for (int i = 0; i < plan.Remove.Count; i++)
            {
                LocalPlayerCandidate candidate = plan.Remove[i];
                PlayerData playerData = FindLocalPlayerData(localPlayers, candidate.PlayerIndex);
                if (playerData == null)
                {
                    failed++;
                    m_normalizeFailures++;
                    ErrorOnce("normalize-missing:" + candidate.PlayerIndex,
                        "本机角色归一化：要移除的 " + candidate.Describe() +
                        " 在 PlayersData 里已经找不到 → 跳过（下个 tick 重新判定）");
                    continue;
                }
                if (m_spawner.RemoveLocalPlayer(project, playerData, out string removeReason))
                {
                    // 从本帧的快照列表里也去掉（防御重复 PlayerIndex 的旧世界数据：
                    // 下一次按索引查找会拿到另一条真实存在的 PlayerData，而不是已删掉的那条）。
                    localPlayers.Remove(playerData);
                    removed++;
                    continue;
                }
                failed++;
                m_normalizeFailures++;
                ErrorOnce("normalize-remove:" + candidate.PlayerIndex,
                    "本机角色归一化：移除 " + candidate.Describe() + " 失败：" + removeReason +
                    " → 保留原样，下个 tick 重试（不假装已移除）");
            }
            if (failed > 0)
            {
                Warn("本机角色归一化未完成：" + plan.LocalPlayerCount + " 个本机角色里已移除 " +
                    removed + " 个、失败 " + failed + " 个 → 下个 tick 重试（Phase 1 槽位公告暂不刷新）");
                return;
            }

            m_normalizeApplied = true;
            m_normalizeLocalPlayers += plan.LocalPlayerCount;
            m_normalizeRemoved += removed;
            m_normalizeKept += plan.KeptCount;
            m_normalizeAmbiguous += plan.AmbiguousMatches;
            if (plan.CapacityLimited)
            {
                m_normalizeCapacityRefusals += plan.CapacityRefusedRemotes;
                Warn("本机角色归一化（limited roster）：" + plan.CapacityReason +
                    "；被拒绝的远端角色不会建化身（每个玩家键一条日志 + avatarCapacityRefusals 计数）");
            }
            if (plan.AmbiguousMatches > 0)
            {
                Warn("本机角色归一化：发现 " + plan.AmbiguousMatches + " 个本地角色的名字/性别/皮肤同时匹配" +
                    "主机槽位与本机资料 → 按\"下载世界里除本机自己那条外的角色都来自主机档案\"判为主机副本移除；" +
                    "本机自己那条按引擎顺序保留（不会误删自己）");
            }
            // Phase 1 **重新枚举**：清掉上一次公告签名，下一次 UpdateLocalSlotsAnnouncement 必然重发；
            // 此时 PlayersData 里只剩本机自己的角色，公告不会再包含主机角色副本。
            m_localSlotsSignature = string.Empty;
            m_localSlotsCount = 0;

            Info("客户端本机角色已归一化（Phase 2）：世界「" + directory + "」本地角色 " +
                plan.LocalPlayerCount + " 个 → 保留 " + plan.KeptCount + " 个（" +
                DescribeCandidates(plan.Keep) + "）、移除 " + removed + " 个（" +
                DescribeRemovals(plan) + "）" +
                (plan.HostRosterMissing
                    ? "；没有主机槽位名册：除保留项外的本地角色按下载来的主机副本移除"
                    : string.Empty));
            Info("Phase 1 重新枚举：本机槽位公告已重置（localSlots=" + plan.KeptCount +
                "）→ 之后只上报本机自己的槽位，不再包含主机角色副本；ESC 统计读的是本机玩家 PlayerIndex=" +
                (plan.Keep.Count > 0 ? plan.Keep[0].PlayerIndex.ToString() : "-1") +
                "（EnsureLocalStatsOwnership 仍以它为本机槽位）");
        }

        /// <summary>同一失败原因只计一次（下个 tick 重试时不会把计数刷成"每秒一次"），日志仍然明确。</summary>
        private void NoteNormalizeFailure(string reason)
        {
            string text = string.IsNullOrEmpty(reason) ? "未知原因" : reason;
            if (!string.Equals(text, m_normalizeLastFailure, StringComparison.Ordinal))
            {
                m_normalizeLastFailure = text;
                m_normalizeFailures++;
            }
            ErrorOnce("normalize-failed:" + text,
                "本机角色归一化失败：" + text + " → 本机角色保持原样；下个 tick 重试（不静默跳过）");
        }

        private static List<LocalPlayerCandidate> BuildLocalPlayerCandidates(List<PlayerData> localPlayers)
        {
            var candidates = new List<LocalPlayerCandidate>(localPlayers.Count);
            for (int i = 0; i < localPlayers.Count; i++)
            {
                PlayerData player = localPlayers[i];
                if (player == null) continue;
                candidates.Add(new LocalPlayerCandidate
                {
                    PlayerIndex = player.PlayerIndex,
                    Name = (player.Name ?? string.Empty).Trim(),
                    Sex = NetworkAvatarSpawner.ToPlayerSex(player.PlayerClass),
                    SkinName = (player.CharacterSkinName ?? string.Empty).Trim(),
                    // TryCollectLocalPlayers 按 PlayerIndex 升序；第一条 = TryGetLocalPlayer 口径的
                    // "本机玩家"，本机资料应用（ApplySelfProfileFromStore）也落在它上面。
                    IsAdoptedSelf = i == 0
                });
            }
            return candidates;
        }

        /// <summary>主机名册里的主机本机槽位 (0, slot)（+ 握手里的 host_profile 作为槽位 0 的兜底证据）。</summary>
        private List<LocalPlayerSlotRef> BuildHostSlotRefs()
        {
            var slots = new List<LocalPlayerSlotRef>(PlayerKey.SlotCount);
            IReadOnlyList<AvatarRosterEntry> entries = m_roster.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                AvatarRosterEntry entry = entries[i];
                if (entry.Key.ClientId != 0) continue;
                slots.Add(ToSlotRef(entry, "roster:host"));
            }
            PlayerProfileSnapshot hostProfile = m_handshake?.HostProfile;
            if (hostProfile != null && hostProfile.HasName && !HasSlot(slots, 0))
            {
                slots.Add(new LocalPlayerSlotRef
                {
                    Key = PlayerKey.Host,
                    Name = (hostProfile.Name ?? string.Empty).Trim(),
                    Sex = hostProfile.Appearance?.Sex ?? PlayerSex.Male,
                    SkinName = (hostProfile.Appearance?.SkinName ?? string.Empty).Trim(),
                    Source = "handshake:host_profile"
                });
            }
            return slots;
        }

        /// <summary>本机自己的槽位视图（名册里 clientId = 本机通道编号的条目 + 主机分配资料）。</summary>
        private List<LocalPlayerSlotRef> BuildOwnSlotRefs()
        {
            var slots = new List<LocalPlayerSlotRef>(PlayerKey.SlotCount);
            int localClientId = m_transport?.LocalClientId ?? m_roster.LocalClientId;
            IReadOnlyList<AvatarRosterEntry> entries = m_roster.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                AvatarRosterEntry entry = entries[i];
                if (localClientId < 0 || entry.Key.ClientId != localClientId) continue;
                slots.Add(ToSlotRef(entry, "roster:self"));
            }
            PlayerProfileSnapshot assigned = m_handshake?.AssignedProfile ?? m_profileStore?.Profile;
            if (assigned != null && assigned.HasName && !HasSlot(slots, 0))
            {
                slots.Add(new LocalPlayerSlotRef
                {
                    Key = new PlayerKey(localClientId >= 0 ? localClientId : 0, 0),
                    Name = (assigned.Name ?? string.Empty).Trim(),
                    Sex = assigned.Appearance?.Sex ?? PlayerSex.Male,
                    SkinName = (assigned.Appearance?.SkinName ?? string.Empty).Trim(),
                    Source = "assigned-profile"
                });
            }
            return slots;
        }

        private static LocalPlayerSlotRef ToSlotRef(AvatarRosterEntry entry, string source) =>
            new LocalPlayerSlotRef
            {
                Key = entry.Key,
                Name = (entry.Name ?? string.Empty).Trim(),
                Sex = entry.Appearance?.Sex ?? PlayerSex.Male,
                SkinName = (entry.Appearance?.SkinName ?? string.Empty).Trim(),
                Source = source
            };

        private static bool HasSlot(List<LocalPlayerSlotRef> slots, int slot)
        {
            for (int i = 0; i < slots.Count; i++)
                if (slots[i] != null && slots[i].Key.Slot == slot) return true;
            return false;
        }

        /// <summary>名册里属于**其他设备**的角色数（容量计算用；本机自己的槽位不算远端）。</summary>
        private int CountRemoteCharacters()
        {
            int localClientId = m_transport?.LocalClientId ?? m_roster.LocalClientId;
            int count = 0;
            IReadOnlyList<AvatarRosterEntry> entries = m_roster.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (localClientId >= 0 && entries[i].Key.ClientId == localClientId) continue;
                count++;
            }
            return count;
        }

        private static PlayerData FindLocalPlayerData(List<PlayerData> players, int playerIndex)
        {
            for (int i = 0; i < players.Count; i++)
                if (players[i] != null && players[i].PlayerIndex == playerIndex) return players[i];
            return null;
        }

        private static string DescribeCandidates(List<LocalPlayerCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return "(none)";
            var parts = new List<string>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i] != null) parts.Add(candidates[i].Describe());
            return parts.Count == 0 ? "(none)" : string.Join("、", parts);
        }

        private static string DescribeRemovals(LocalPlayerNormalizePlan plan)
        {
            if (plan == null || plan.Remove.Count == 0) return "无需移除";
            var parts = new List<string>(plan.Remove.Count);
            for (int i = 0; i < plan.Remove.Count; i++)
            {
                string reason = i < plan.RemoveReasons.Count ? plan.RemoveReasons[i] : "原因未记录";
                parts.Add(plan.Remove[i].Describe() + " ← " + reason);
            }
            return string.Join("；", parts);
        }

        /// <summary>
        /// 创建远端化身前的容量闸门：引擎最多 4 个玩家槽位（SubsystemPlayers.MaxPlayers /
        /// SubsystemGameWidgets.MaxGameWidgets），**本机角色 + 远端化身共用 0..3**。
        /// 满了就明确拒绝这个玩家键（每键一次计数 + 一条日志；名册超出容量 = limited roster）。
        /// </summary>
        private bool TryReserveAvatarCapacity(PlayerKey key, int localCount)
        {
            int total = localCount + m_spawner.Count;
            if (total < PlayerKey.SlotCount)
            {
                m_capacityRefusedKeys.Remove(key);
                return true;
            }
            if (m_capacityRefusedKeys.Add(key))
            {
                m_avatarCapacityRefusals++;
                ErrorOnce("avatar-capacity:" + key,
                    "拒绝为玩家键 " + key + " 创建远端化身：引擎玩家槽位 0.." + PlayerKey.MaxSlot +
                    " 已占满（SubsystemPlayers.MaxPlayers / SubsystemGameWidgets.MaxGameWidgets = " +
                    PlayerKey.SlotCount + "）—— 本机角色 " + localCount + " 个 + 已有远端化身 " +
                    m_spawner.Count + " 个 = " + total + "；名册超出容量（limited roster：显式拒绝，" +
                    "不静默超限，也不会动本机角色）");
            }
            return false;
        }

        /// <summary>
        /// 主机要求先创建角色（旧 SCMP_PROFILE_REQUIRED）：打开角色创建屏，完成后重新握手。
        /// 不需要 Project —— 这条判定发生在 Play 屏（世界还没下载）。
        /// </summary>
        private void HandleProfileRequired()
        {
            bool required = m_handshake != null && m_handshake.ProfileRequired;
            if (!required)
            {
                m_profilePromptShown = false;
                return;
            }
            ShowProfileRequiredScreen(m_handshake.ProfileRequiredReason);
        }

        private void SeedSelfFlagFromRoster()
        {
            var entries = m_roster.Entries;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Key.ClientId == m_roster.LocalClientId) entries[i].IsSelf = true;
        }

        private void SeedHostAvatarFromHandshake()
        {
            string hostIdentity = (m_transport.HostIdentity ?? string.Empty).Trim();
            string hostName = (m_transport.HostName ?? string.Empty).Trim();
            if (hostIdentity.Length == 0)
            {
                ErrorOnce("client-no-host-identity",
                    "握手（HelloAck）没有带回主机身份 → 拒绝创建主机角色：" +
                    "旧实现用 GetNetworkRecordKey(0) 那种 index 兜底键，本重写版明确不做；" +
                    "等主机身份到达后会自动重试");
                return;
            }
            if (string.Equals(hostIdentity, PlayerIdentity.LocalIdentity(), StringComparison.Ordinal))
            {
                m_rejectedPeers++;
                ErrorOnce("client-host-is-self",
                    "拒绝创建主机角色：握手里的主机身份与本机身份相同（" +
                    PlayerIdentity.Shorten(hostIdentity) + "）—— 同一个 UserId 不可能是两台机器上的两个人");
                return;
            }
            PlayerProfileSnapshot hostProfile = m_handshake?.HostProfile;
            if (hostProfile != null && !hostProfile.HasName)
            {
                hostProfile = null;
            }
            string name = hostProfile?.HasName == true
                ? hostProfile.Name
                : (hostName.Length > 0 ? hostName : string.Empty);
            if (name.Length == 0)
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("client-host-no-name",
                    "HelloAck 没有主机名字（host_profile 与 host_name 都为空）→ 不建主机化身" +
                    "（绝不拿 \"Host\"/\"Android User\" 兜底）");
                return;
            }
            if (hostProfile == null)
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("client-host-no-appearance",
                    "HelloAck 没有主机资料（旧主机 / 主机没有本机角色）→ 主机化身按引擎默认外观创建；" +
                    "这是唯一没有真实皮肤来源的情况，已明确记录（不伪造皮肤/性别）");
                hostProfile = new PlayerProfileSnapshot
                {
                    Name = name,
                    Appearance = new PlayerAppearance
                    {
                        Sex = PlayerSex.Male,
                        SkinName = string.Empty,
                        Clothes = PlayerAppearance.CreateEmptyClothes()
                    }
                };
            }
            IReadOnlyList<AvatarRosterChange> changes = m_roster.Apply(PlayerKey.Host, hostIdentity,
                name, hostProfile.Appearance, isHost: true, "HelloAck.host_profile");
            ApplyRosterChanges(changes, "主机握手资料");
        }

        private void ApplySelfProfileFromStore()
        {
            PlayerProfileSnapshot profile = m_profileStore?.Profile;
            if (profile == null || !profile.HasName)
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("client-self-no-profile",
                    "本机没有可用的角色资料（主机也没分配）→ 本机名字/外观保持下载世界里的角色；" +
                    "首次加入会先收到 profile_required 并要求创建角色（不伪造身份）");
                return;
            }
            string signature = profile.Signature;
            if (string.Equals(signature, m_appliedSelfSignature, StringComparison.Ordinal)) return;
            if (!m_spawner.TryApplyLocalProfile(profile, out bool replaced, out string reason))
            {
                // 世界刚加载、本机角色还没建好是正常时序：不刷屏（每帧重试），但必须能定位。
                ErrorOnce("client-self-apply-fail",
                    "把本机资料应用到本机角色失败（会每帧重试）：" + reason);
                return;
            }
            m_appliedSelfSignature = signature;
            m_avatarAppearanceApplied++;
            // 资料刚被应用（首次加入 / 主机分配 / 重连）→ 允许 TickClient 发一次 PlayerProfile。
            m_profileSendPending = true;
            ResolveName(PlayerIdentity.RecordKey(PlayerIdentity.LocalIdentity(), profile.Name),
                profile.Name, "主机分配的记录 / 本机资料存储");
            Info("已应用本机角色资料：名字=" + profile.Name + " · 性别=" + profile.Appearance.Sex +
                " · 皮肤=" + profile.Appearance.SkinName +
                (replaced ? " · 已按旧实现替换本机 PlayerData（性别不同）" : string.Empty));
        }

        /// <summary>
        /// 客户端：把本机玩家的真实资料发给主机（旧 SynchronizeLocalProfileIfChanged）。
        /// 只有本机玩家已经应用成自己的资料之后才发 —— 否则会把下载世界里的主机角色
        /// 当成"自己"上报（实机缺陷：客户端用主机的名字/皮肤覆盖自己的记录）。
        /// Source: ScMultiplayerProfileHandlers.cs:1248-1269
        /// </summary>
        private void SendLocalProfileIfChanged()
        {
            if (m_transport == null || !m_transport.IsConnected) return;
            if (m_transport.LocalClientId < 0) return;     // 还没握手，主机不知道是谁
            // 本机玩家还没换成自己的资料时**绝不能上报**：下载世界里的本机角色是主机角色，
            // 上报它会把主机的名字/皮肤盖到客户端自己的身份记录上（实机缺陷）。
            if (m_appliedSelfSignature.Length == 0) return;
            string localIdentity = PlayerIdentity.LocalIdentity();
            if (localIdentity.Length == 0)
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("local-identity-missing",
                    "本机身份（UserId.dat）为空 → 无法把本机资料上报给主机（主机无法按身份绑定）");
                m_profileSendPending = false;
                return;
            }
            if (!m_spawner.TryCaptureLocalProfile(out PlayerProfileSnapshot profile, out string reason))
            {
                ErrorOnce("local-profile-missing", "本机玩家资料取不到（" + reason + "）→ 本帧不上报");
                return;
            }
            string signature = profile.Signature;
            if (string.Equals(signature, m_localProfileSignature, StringComparison.Ordinal))
            {
                m_profileSendPending = false;
                return;
            }
            m_localProfileSignature = signature;
            if (m_messages == null)
            {
                ErrorOnce("local-profile-no-messages",
                    "没有消息注册表 → 本机资料无法上报给主机（其他客户端会看不到你的真实外观）");
                m_profileSendPending = false;
                return;
            }
            Proto.PlayerProfile message = PlayerProfileCodec.ToMessage(
                new PlayerKey(m_transport.LocalClientId, 0), localIdentity,
                profile, isHost: false, hasCharacter: true);
            if (m_messages.Sender.Send(0, message))
            {
                m_profileSendPending = false;
                ResolveName(PlayerIdentity.RecordKey(localIdentity, profile.Name, 0), profile.Name,
                    "本机角色 PlayerData.Name（上报给主机）");
                Info("已把本机资料上报给主机：名字=" + profile.Name + " · 性别=" +
                    profile.Appearance.Sex + " · 皮肤=" + profile.Appearance.SkinName +
                    "（主机随后广播给其他客户端）");
            }
            else
            {
                m_localProfileSignature = string.Empty;    // 下次重试（没有假装发成功）
                ErrorOnce("local-profile-send-failed",
                    "本机资料发送失败（通道不可用 / 队列满）→ 下次变化或重试时再发（不假装成功）");
            }
        }

        // ------------------------------------------------- 交给地形通道的联机角色视图（IAvatarTerrainLink）
        /// <summary>
        /// 客户端：本机**已有的角色记录 hash**清单 —— 直接读本机世界目录里的
        /// `SuMultiplayerPlayers.xml`（它来自主机导出的 world pak；pak 里带着这份文件）。
        /// Hello 里上报给主机，主机据此只重发变化了的角色记录。
        /// 读失败 / 没进世界 / 本地没有文件 → 0 条（主机按全量补发，绝不漏）。
        /// </summary>
        public int CaptureKnownRecordHashes(List<RecordHashRef> target)
        {
            if (target == null) return 0;
            target.Clear();
            Project project = GameManager.Project;
            SubsystemGameInfo gameInfo = project?.FindSubsystem<SubsystemGameInfo>(false);
            string directory = gameInfo?.DirectoryName;
            if (string.IsNullOrEmpty(directory)) return 0;   // 没进世界：没有本地副本可申报
            if (!SuMultiplayerPlayersXml.TryReadRecordHashes(directory, target, out string reason))
            {
                m_knownRecordHashReadFailures++;
                ErrorOnce("known-record-hashes",
                    "读取本地角色记录 hash 失败（" + reason + "）→ 本次 Hello 不申报任何已知记录，" +
                    "主机会全量补发（不会漏发；明确计数）");
                return 0;
            }
            // 进程内缓存（主机名册 / 资料广播刚下发的 hash）优先于磁盘副本：
            // 它比 pak 里的那份新（重连前收到的最新版本）。同键覆盖，不产生重复条目。
            if (m_knownRecordHashCache.Count > 0)
            {
                for (int i = 0; i < target.Count; i++)
                {
                    if (!m_knownRecordHashCache.TryGetValue(target[i].RecordKey,
                            out ulong cached)) continue;
                    if (cached == target[i].Hash) continue;
                    target[i] = new RecordHashRef(target[i].RecordKey, cached);
                }
                foreach (KeyValuePair<string, ulong> item in m_knownRecordHashCache)
                {
                    bool exists = false;
                    for (int i = 0; i < target.Count; i++)
                    {
                        if (!string.Equals(target[i].RecordKey, item.Key,
                                StringComparison.Ordinal)) continue;
                        exists = true;
                        break;
                    }
                    if (exists) continue;
                    target.Add(new RecordHashRef(item.Key, item.Value));
                }
            }
            m_knownRecordHashesReported = target.Count;
            return target.Count;
        }


        /// <summary>
        /// Source: ScMultiplayerWorldObjectHandlers.cs:481-484
        ///         （旧实现用 m_networkPlayerData.Values 排除网络化身来找"本机玩家"）
        /// </summary>
        /// <inheritdoc />
        public bool TryGetHelloProfileClaim(out PlayerProfileSnapshot profile)
        {
            profile = null;
            PlayerProfileSnapshot stored = m_profileStore?.Profile;
            if (stored == null || !stored.HasName) return false;
            // 只有"这个资料刚在创建角色界面确认过"才允许在 Hello 里申报（旧联机 mod 的
            // m_activeJoinHasPlayerProfile 口径）。host-record / host-roster / edit-player 都是
            // 主机分配或房间内编辑的结果，不是"我要在新世界创建这个角色"，绝不能拿去让主机建记录。
            if (!string.Equals(m_profileStore.Source, "character-screen", StringComparison.Ordinal))
                return false;
            profile = stored;
            return true;
        }

        public bool IsNetworkAvatar(PlayerData playerData) => m_spawner.Contains(playerData);

        /// <summary>
        /// 主机：某个玩家端在**加入握手**里声明的稳定身份（UserId.dat）。
        /// 统计 / 成就模块按它把记录键与内容对应到同一身份（它自己只收到 ClientJoined 的名字 /
        /// 身份，这里给的是联机角色模块已经验收过的那份，避免两处判断不一致）。
        /// 返回 false = 该端没有可用身份（按名字绑定的退化路径，调用方必须明确记录）。
        /// </summary>
        public bool TryGetPeerIdentity(int clientId, out string identity)
        {
            if (m_peerIdentities.TryGetValue(clientId, out identity) &&
                !string.IsNullOrEmpty(identity))
                return true;
            identity = string.Empty;
            return false;
        }

        /// <summary>
        /// 主机：某个玩家端的联机角色在世界里的真实位置（实时地图显示客户端坐标用）。
        /// 由持有网络化身表的 <see cref="NetworkAvatarSpawner"/> 提供；读不到就写明原因，
        /// 调用方（实时地图）据此明说"坐标未知"，不按区块中心猜。
        /// </summary>
        public bool TryGetAvatarCell(int clientId, out Vector3 position, out string reason) =>
            m_spawner.TryGetAvatarCell(clientId, out position, out reason);

        /// <summary>
        /// 主机：实时地图的**逐角色槽位**清单（v10）—— 每个本机槽位 + 每个联机化身都一条。
        /// 唯一枚举口径在 <see cref="NetworkAvatarSpawner.TryCollectLocalPlayers"/> / 化身表的
        /// <c>Keys</c> / <c>TryGet</c>（本类不再自己扫 PlayersData，避免与位置同步链路漂移）。
        /// </summary>
        public int CaptureLiveMapPlayers(List<LiveMapPlayer> target, out string reason) =>
            m_spawner.CaptureLiveMapPlayers(target, out reason);

        /// <summary>
        /// Source: ScMultiplayerWorldObjectHandlers.cs:521-525（GetTerrainInterestRadius）
        /// </summary>
        public int GetTerrainInterestRadius(float visibility) =>
            m_spawner.GetTerrainInterestRadius(visibility);

        /// <summary>
        /// Source: ScMultiplayerTerrainHandlers.cs:441-449
        ///         （Interest 分支 → m_hostTerrainReportedInterestRadii）
        /// </summary>
        public void OnClientInterestRadiusReported(int clientId, int radius) =>
            m_spawner.SetReportedInterestRadius(clientId, radius);

        // ------------------------------------------------- 玩家位置同步（位置快照的收发与应用）

        /// <summary>
        /// 客户端：本机玩家当前的位置/姿态快照（每 32Hz 由地形通道取一份发给主机）。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:53-92
        ///         （本机玩家 = PlayersData 里不是网络化身的那一条；位置/姿态与速度同一份快照）
        /// </summary>
        public bool TryCaptureLocalPlayerPosition(int serverTick,
            out PlayerPositionSnapshot snapshot, out string reason) =>
            m_spawner.TryCaptureLocalPlayerPosition(serverTick, out snapshot, out reason);

        /// <summary>
        /// 客户端：**所有本机角色**的位置快照（槽位 0..N-1）；单角色时与旧实现一条、键 (0,0)。
        /// </summary>
        public int CaptureLocalPlayerPositions(int serverTick, List<PlayerPositionEntry> target,
            out string reason) =>
            m_spawner.CaptureLocalPlayerPositions(serverTick, target, out reason);

        /// <summary>
        /// 主机：本机玩家 + 每个联机角色，一批位置快照。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:47-100（positionBatch）
        /// </summary>
        public int CapturePlayerPositionBatch(int serverTick, List<PlayerPositionEntry> target,
            out string reason) =>
            m_spawner.CapturePlayerPositionBatch(serverTick, target, out reason);

        /// <summary>
        /// 收到远端角色的位置快照（主机 = 某玩家端的本机玩家；客户端 = 主机角色）。
        /// Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:38-97
        ///         （HandleGamePlayerPositionMessage：校验有限值 → 丢弃陈旧 → 建状态 → 写只读字段；
        ///           本重写版把"应用到身体"放在下面的每帧表现推进里，与旧实现的时序一致）
        /// </summary>
        public void OnRemotePlayerPositionReceived(int clientId, in PlayerPositionSnapshot snapshot) =>
            m_spawner.ApplyRemotePlayerPosition(new PlayerKey(clientId, 0), in snapshot);

        public void OnRemotePlayerPositionReceived(PlayerKey key, in PlayerPositionSnapshot snapshot) =>
            m_spawner.ApplyRemotePlayerPosition(key, in snapshot);

        /// <summary>
        /// 客户端：批里本机自己那一条 → 计数 positionSkippedOwn，绝不写回本机角色。
        /// </summary>
        public void NotePositionSkippedOwn(PlayerKey key) => m_spawner.NotePositionSkippedOwn(key);

        /// <summary>
        /// 主机：这个玩家端是否已经上报过本机槽位（false = 旧构建，只发/只收槽位 0）。
        /// </summary>
        public bool PeerAnnouncedLocalSlots(int clientId) => m_slotAwareClients.Contains(clientId);

        /// <summary>
        /// 每帧推进远端角色的插值/平滑并写回引擎。
        /// Source: Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerUpdateLoop.cs:1299
        ///         （客户端分支每帧 UpdateRemotePlayerPresentations(dt)；
        ///           主机分支 :1285 的 ApplyHostRemoteFollowVelocities 依赖尚未移植的输入链，见报告）
        /// </summary>
        public void UpdateRemotePlayerPresentations(float deltaTime) =>
            m_spawner.UpdateRemotePresentations(deltaTime);

        // ---------------------------------------------------------------- 资料 / 记录工具

        private static PlayerProfileSnapshot RecordToProfile(NetworkPlayerRecord record)
        {
            if (record == null) return null;
            return new PlayerProfileSnapshot
            {
                Name = (record.Name ?? string.Empty).Trim(),
                Appearance = new PlayerAppearance
                {
                    Sex = NetworkAvatarSpawner.ToPlayerSex(record.PlayerClass),
                    SkinName = record.SkinName ?? string.Empty,
                    SkinSha256 = record.SkinSha256 != null
                        ? (byte[])record.SkinSha256.Clone()
                        : Array.Empty<byte>(),
                    Clothes = PlayerAppearance.CloneClothes(record.Clothes)
                }
            };
        }

        // Source: ScMultiplayerProfileHandlers.cs:488-500（ApplyProfileToRecord）
        private static void ApplyProfileToRecord(NetworkPlayerRecord record, PlayerProfileSnapshot profile)
        {
            if (record == null || profile == null) return;
            record.NameConfirmed = true;
            // 名字原样写入（不 Trim）：记录里的名字 = 引擎校验通过的那个字符串。
            if (profile.HasName) record.Name = profile.Name;
            if (profile.Appearance != null)
            {
                record.PlayerClass = NetworkAvatarSpawner.ToPlayerClass(profile.Appearance.Sex);
                if (profile.Appearance.HasSkinName) record.SkinName = profile.Appearance.SkinName;
                byte[] newHash = profile.Appearance.SkinSha256 != null
                    ? (byte[])profile.Appearance.SkinSha256.Clone()
                    : Array.Empty<byte>();
                // 资源只对"同一个自定义皮肤哈希"有效：换成内置皮肤或换了哈希都明确作废旧资源，
                // 不能把上一份自定义皮肤的资源挂在新资料下（也避免记录里留下无主的资源）。
                if (!SkinAssetPolicy.IsCustomSkinName(record.SkinName) ||
                    !SkinAssetPolicy.BytesEqual(record.SkinSha256, newHash))
                    record.SkinAsset = Array.Empty<byte>();
                record.SkinSha256 = newHash;
                record.Clothes = PlayerAppearance.CloneClothes(profile.Appearance.Clothes);
            }
        }

        private static bool IsUsableAppearance(PlayerProfileSnapshot profile) =>
            profile != null && profile.HasName && profile.Appearance != null &&
            profile.Appearance.HasUsableSkin;

        /// <summary>
        /// 资料是否完整：名字合法 + 皮肤与性别兼容。
        ///   · 内置皮肤：必须在本机皮肤列表里；
        ///   · 自定义皮肤：名字合法 + 有 32 字节 SHA-256（资源可以稍后由客户端上传；
        ///     缺资源时接收端显示确定性占位并计数，绝不换成内置皮肤）。
        /// Source: ScMultiplayerProfileHandlers.cs:34-48（IsValidRequestedProfile）
        /// </summary>
        private bool IsCompleteProfile(PlayerProfileSnapshot profile)
        {
            if (profile == null || !profile.HasName) return false;
            // 最终校验只调用引擎 PlayerData.VerifyName，输入不 Trim（合法性以原字符串为准）。
            if (!PlayerData.VerifyName(profile.Name)) return false;
            if (profile.Appearance == null || !profile.Appearance.HasSkinName) return false;
            string skin = profile.Appearance.SkinName.Trim();
            if (SkinAssetPolicy.IsCustomSkinName(skin))
            {
                if (!SkinAssetPolicy.IsValidHash(profile.Appearance.SkinSha256))
                {
                    m_avatarAppearanceUnavailable++;
                    ErrorOnce("custom-skin-hash:" + skin,
                        "资料里的自定义皮肤 \"" + skin + "\" 没有合法的 32 字节哈希 → 拒绝这次资料" +
                        "（没有哈希就无法向其他客户端说明要哪份资源）");
                    return false;
                }
                if (SkinAssetPolicy.TryGetClassHint(skin, out PlayerSex hint) &&
                    hint != profile.Appearance.Sex)
                {
                    m_avatarAppearanceUnavailable++;
                    ErrorOnce("custom-skin-class:" + skin,
                        "自定义皮肤 \"" + skin + "\" 的名称暗示性别 " + hint +
                        "，资料却是 " + profile.Appearance.Sex + " → 拒绝（不静默改性别）");
                    return false;
                }
                return true;
            }
            CharacterSkinsManager.UpdateCharacterSkinsList();
            if (!CharacterSkinsManager.IsBuiltIn(skin))
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("skin-not-custom:" + skin,
                    "资料里的皮肤 \"" + skin + "\" 不是内置皮肤也不是合法的 .scskin 自定义皮肤名 → 拒绝");
                return false;
            }
            if (!CharacterSkinsManager.CharacterSkinsNames.Contains(skin))
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("unknown-skin:" + skin, "资料里的皮肤 \"" + skin + "\" 不在本机皮肤列表里");
                return false;
            }
            PlayerClass? skinClass = CharacterSkinsManager.GetPlayerClass(skin);
            return !skinClass.HasValue ||
                skinClass.Value == NetworkAvatarSpawner.ToPlayerClass(profile.Appearance.Sex);
        }

        /// <summary>记录里是否存着一份与哈希自洽的自定义皮肤资源（HelloAck 的皮肤资源标志）。</summary>
        private static bool HasStoredSkinAsset(NetworkPlayerRecord record)
        {
            if (record == null || record.SkinAsset == null || record.SkinAsset.Length == 0) return false;
            if (!PlayerRecordCodec.IsSkinHashValid(record.SkinSha256)) return false;
            byte[] actual = SkinAssetPolicy.Sha256(record.SkinAsset);
            return SkinAssetPolicy.BytesEqual(actual, record.SkinSha256);
        }

        // Source: ScMultiplayerProfileHandlers.cs:50-66（CreateInitialPlayerRecord）
        private static NetworkPlayerRecord CreateInitialPlayerRecord(PlayerProfileSnapshot profile)
        {
            Project project = GameManager.Project;
            SubsystemPlayers players = project?.FindSubsystem<SubsystemPlayers>(false);
            Vector3 position = players?.ComponentPlayers.FirstOrDefault()?.ComponentBody.Position ??
                players?.GlobalSpawnPosition ?? Vector3.Zero;
            string name = (profile?.Name ?? string.Empty).Trim();
            if (name.Length > 14) name = name.Substring(0, 14);
            return new NetworkPlayerRecord
            {
                Name = string.IsNullOrEmpty(name) ? "Player" : name,
                PlayerClass = NetworkAvatarSpawner.ToPlayerClass(
                    profile?.Appearance?.Sex ?? PlayerSex.Male),
                SkinName = profile?.Appearance?.SkinName ?? string.Empty,
                SkinSha256 = profile?.Appearance?.SkinSha256 != null
                    ? (byte[])profile.Appearance.SkinSha256.Clone()
                    : Array.Empty<byte>(),
                Clothes = PlayerAppearance.CloneClothes(profile?.Appearance?.Clothes),
                Position = position,
                SpawnPosition = Vector3.Zero,
                Level = PlayerRecordCodec.DefaultLevel,
                Health = PlayerRecordCodec.DefaultHealth,
                HasReceivedInitialItems = false,
                NameConfirmed = true
            };
        }

        /// <summary>
        /// 抓一份可落盘的角色记录，并保留**只有客户端算得准 / 本模块还没接的功能**写进去的字段。
        /// Source: ScMultiplayerProfileHandlers.cs:469-493（PreserveRecordedPlayerStats）
        /// </summary>
        private NetworkPlayerRecord CaptureForStorage(int clientId, NetworkPlayerRecord previous) =>
            CaptureForStorage(new PlayerKey(clientId, 0), previous);

        private NetworkPlayerRecord CaptureForStorage(PlayerKey key, NetworkPlayerRecord previous)
        {
            NetworkPlayerRecord captured = m_spawner.Capture(key);
            if (captured == null) return null;
            // 统计 / 成就是**主机权威**（由 player-stats 模块从引擎 PlayerStats 抓取并写入记录），
            // 这里只是每 5 秒抓一次角色状态，不能把记录里已有的统计 / 成就覆盖掉。
            captured.Stats = previous?.Stats;
            captured.Achievements = previous?.Achievements;
            captured.RejectedAchievements = previous?.RejectedAchievements ?? 0;
            // 名字是否已确认真实资料是记录级事实：Capture 只抓引擎状态，不能把未确认记录标成已确认。
            captured.NameConfirmed = previous?.NameConfirmed ?? captured.NameConfirmed;
            // 皮肤哈希 / 资源是记录级事实（由皮肤模块校验后写入）：沿用记录里已有的值，
            // 不因为一次抓取把"待补发 / 已保存"的状态抹掉。
            if (previous?.SkinSha256 != null && previous.SkinSha256.Length > 0)
                captured.SkinSha256 = (byte[])previous.SkinSha256.Clone();
            captured.SkinAsset = previous?.SkinAsset != null && previous.SkinAsset.Length > 0
                ? (byte[])previous.SkinAsset.Clone()
                : Array.Empty<byte>();
            return captured;
        }

        // ---------------------------------------------------------------- 广播

        /// <summary>旧版单角色入口：槽位 0 的资料广播。</summary>
        private void BroadcastProfile(int clientId, PlayerProfileSnapshot profile, bool isHost,
            string identity = null) =>
            BroadcastProfile(new PlayerKey(clientId, 0), profile, isHost, identity);

        /// <summary>
        /// 主机 → 客户端：一个玩家键的资料。
        /// 旧构建（从没上报本机槽位）只收槽位 0；给它们发第二个槽位会让它们把主机
        /// 第二个角色当成主机角色来回覆盖（显式计 legacyDefaultSlot，10 秒限流一行）。
        /// </summary>
        private void BroadcastProfile(PlayerKey key, PlayerProfileSnapshot profile, bool isHost,
            string identity = null)
        {
            if (m_broadcast == null || profile == null) return;
            string peerIdentity = identity;
            if (peerIdentity == null && key.ClientId > 0)
                m_peerIdentities.TryGetValue(key.ClientId, out peerIdentity);
            string lookupIdentity = isHost ? PlayerIdentity.LocalIdentity()
                : (peerIdentity ?? string.Empty);
            ulong recordHash = RecordHashOf(lookupIdentity, profile.Name, key.Slot);
            Proto.PlayerProfile message = PlayerProfileCodec.ToMessage(key,
                peerIdentity ?? string.Empty, profile, isHost, hasCharacter: true, recordHash);
            byte[] payload = message.ToByteArray();
            int sent = 0;
            foreach (int target in m_spawner.ClientIds.ToArray())
            {
                if (key.Slot != 0 && !m_slotAwareClients.Contains(target))
                {
                    m_legacyDefaultSlot++;
                    LogRateLimited("legacy-profile:" + target,
                        "clientId=" + target + " 从未上报本机槽位（旧构建）→ 只给它发槽位 0 的资料；" +
                        "玩家键 " + key + " 的资料对它不可见（显式兼容，不是静默丢弃）");
                    continue;
                }
                if (m_transport.TrySendTo(target, Proto.MessageType.PlayerProfile, payload)) sent++;
            }
            if (sent == 0)
            {
                // 没有在线玩家端（只有主机 / Debug 端）不是错误；新客户端加入时会重新广播。
                Info("资料广播暂时没有接收方（玩家键 " + key + " · 名字=" + profile.Name +
                    "）；下一个客户端加入时会随整表广播重发");
            }
        }

        private void SendAssignedProfile(int clientId, NetworkPlayerRecord record)
        {
            if (m_broadcast == null || record == null) return;
            string identity = m_peerIdentities.TryGetValue(clientId, out string value) ? value : string.Empty;
            PlayerProfileSnapshot profile = RecordToProfile(record);
            Proto.PlayerProfile message = PlayerProfileCodec.ToMessage(
                new PlayerKey(clientId, 0), identity, profile, isHost: false, hasCharacter: true);
            if (!m_transport.TrySendTo(clientId, Proto.MessageType.PlayerProfile, message.ToByteArray()))
            {
                ErrorOnce("assigned-profile-send:" + clientId,
                    "把主机记录里的本机资料回给 clientId=" + clientId + " 失败（通道不可用）→ " +
                    "该客户端会暂时用 Hello 里提交的资料（不会假装已同步）");
            }
        }

        /// <summary>
        /// 主机：某条角色记录的当前"不常变内容 hash"（<see cref="PlayerRecordHash"/>；0 = 没有记录）。
        /// 名册 / 资料广播带上它，客户端据此缓存"我已有这条记录的哪个版本"，重连 Hello 上报做 diff。
        /// </summary>
        private ulong RecordHashOf(string identity, string name, int slot) =>
            m_records.TryGetRecordHash(identity, name, slot, out ulong hash) ? hash : 0UL;

        /// <summary>
        /// 主机：把当前房间里**每个本机角色槽位**（主机 0:0..0:N-1 + 每个客户端自己的槽位）
        /// 作为一份完整名册广播给每个客户端。客户端用整表替换而不是增量叠加：
        /// 重连换连接、离开消息丢失、槽位增删都不会留下幽灵化身。
        /// 旧构建只收槽位 0 的条目（单角色兼容）。名册按**接收端**分别构建。
        /// </summary>
        private void BroadcastRoster(string why, bool force = false)
        {
            if (m_broadcast == null || m_broadcast.IsHosting != true) return;
            var full = new Proto.AvatarRoster();
            var legacy = new Proto.AvatarRoster();
            var signature = new System.Text.StringBuilder();
            int hostSlots = 0;
            // 主机自己的每个本机角色：slot 0..N-1，键 (0, slot)。
            var hostPlayers = new List<PlayerData>(PlayerKey.SlotCount);
            if (m_spawner.TryCollectLocalPlayers(GameManager.Project, hostPlayers, out _, out _) &&
                hostPlayers.Count > 0)
            {
                for (int slot = 0; slot < hostPlayers.Count; slot++)
                {
                    PlayerProfileSnapshot hostProfile = m_spawner.CaptureProfile(hostPlayers[slot],
                        out string hostReason);
                    if (hostProfile == null)
                    {
                        m_avatarAppearanceUnavailable++;
                        ErrorOnce("roster-host-profile:" + slot,
                            "整表广播时主机槽位 " + slot + " 的资料不可用（" + hostReason +
                            "）→ 名册里没有该条目，客户端会明确记录\"主机外观未知\"（不伪造性别/皮肤）");
                        continue;
                    }
                    PlayerKey key = new PlayerKey(0, slot);
                    ulong hostRecordHash = RecordHashOf(PlayerIdentity.LocalIdentity(),
                        hostProfile.Name, slot);
                    if (hostRecordHash != 0UL) m_rosterRecordHashes++;
                    Proto.PlayerProfile item = PlayerProfileCodec.ToMessage(key,
                        PlayerIdentity.LocalIdentity(), hostProfile, isHost: true,
                        hasCharacter: true, hostRecordHash);
                    full.Avatars.Add(item);
                    if (slot == 0) legacy.Avatars.Add(item);
                    signature.Append(key).Append(':').Append(hostProfile.Signature).Append(';');
                    hostSlots++;
                }
            }
            m_hostSlotsCount = hostPlayers.Count;
            if (hostSlots == 0)
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("roster-host-profile",
                    "整表广播时主机资料不可用（主机还没进世界 / 没有本机角色）→ " +
                    "名册里没有主机条目，客户端会明确记录\"主机外观未知\"（不伪造性别/皮肤）");
            }
            int peers = 0;
            foreach (PlayerKey key in m_spawner.Keys.ToArray())
            {
                if (key.ClientId == 0) continue;    // 主机自己的本机角色上面已经列过
                if (!m_peerSlots.TryGetValue(key, out PeerSlot peer) || peer.Profile == null ||
                    !peer.Profile.HasName) continue;
                m_peerIdentities.TryGetValue(key.ClientId, out string identity);
                ulong peerRecordHash = RecordHashOf(identity ?? string.Empty, peer.Profile.Name,
                    key.Slot);
                if (peerRecordHash != 0UL) m_rosterRecordHashes++;
                Proto.PlayerProfile item = PlayerProfileCodec.ToMessage(key,
                    identity ?? string.Empty, peer.Profile, isHost: false, hasCharacter: true,
                    peerRecordHash);
                full.Avatars.Add(item);
                if (key.Slot == 0) legacy.Avatars.Add(item);
                signature.Append(key).Append(':').Append(peer.Profile.Signature).Append(';');
                peers++;
            }
            string text = signature.ToString();
            if (!force && string.Equals(text, m_lastRosterSignature, StringComparison.Ordinal)) return;
            m_lastRosterSignature = text;
            byte[] fullPayload = full.ToByteArray();
            byte[] legacyPayload = legacy.ToByteArray();
            int sent = 0;
            foreach (int target in m_spawner.ClientIds.ToArray())
            {
                bool aware = m_slotAwareClients.Contains(target);
                byte[] payload = aware ? fullPayload : legacyPayload;
                if (!aware && (full.Avatars.Count != legacy.Avatars.Count))
                {
                    m_legacyDefaultSlot++;
                    LogRateLimited("legacy-roster:" + target,
                        "clientId=" + target + " 从未上报本机槽位（旧构建）→ 只发槽位 0 的名册（" +
                        legacy.Avatars.Count + " 条；完整名册 " + full.Avatars.Count +
                        " 条）；多本机角色对旧客户端不可见（显式兼容）");
                }
                if (m_transport.TrySendTo(target, Proto.MessageType.AvatarRoster, payload)) sent++;
            }
            Info("已向 " + sent + " 个客户端广播完整房间名册（" + why + "）：主机 " + hostSlots +
                " 个本机槽位 + " + peers + " 个远端槽位；旧构建只收槽位 0");
        }

        /// <summary>旧版单角色入口：槽位 0 的离开通知。</summary>
        private void BroadcastAvatarRemove(int clientId, string identity, string reason) =>
            BroadcastAvatarRemove(new PlayerKey(clientId, 0), identity, reason);

        /// <summary>
        /// 主机 → 客户端：一个玩家键离开。槽位 &gt; 0 的通知**绝不发给旧构建**：
        /// 旧客户端的 AvatarRemove 只认 client_id，会把该 clientId 的槽位 0 化身也删掉。
        /// </summary>
        private void BroadcastAvatarRemove(PlayerKey key, string identity, string reason)
        {
            if (m_broadcast == null) return;
            var message = new Proto.AvatarRemove
            {
                ClientId = key.ClientId,
                Slot = (uint)key.Slot,
                Identity = identity ?? string.Empty,
                Reason = reason ?? string.Empty
            };
            byte[] payload = message.ToByteArray();
            int sent = 0;
            foreach (int target in m_spawner.ClientIds.ToArray())
            {
                if (key.Slot != 0 && !m_slotAwareClients.Contains(target))
                {
                    m_legacyDefaultSlot++;
                    LogRateLimited("legacy-remove:" + target,
                        "clientId=" + target + " 从未上报本机槽位（旧构建）→ 不给它发槽位 " +
                        key.Slot + " 的离开通知（旧客户端会误删该连接槽位 0 的化身）");
                    continue;
                }
                if (m_transport.TrySendTo(target, Proto.MessageType.AvatarRemove, payload)) sent++;
            }
            if (sent == 0)
            {
                Info("离开通知暂时没有接收方（玩家键 " + key + "）；剩下的客户端在下次整表广播时会对齐");
            }
        }

        // ---------------------------------------------------------------- 槽位状态工具

        private PeerSlot EnsurePeerSlot(PlayerKey key)
        {
            if (m_peerSlots.TryGetValue(key, out PeerSlot peer)) return peer;
            peer = new PeerSlot { Key = key };
            m_peerSlots[key] = peer;
            return peer;
        }

        private PlayerProfileSnapshot GetPeerProfile(PlayerKey key) =>
            m_peerSlots.TryGetValue(key, out PeerSlot peer) ? peer.Profile : null;

        /// <summary>把"这个槽位还没建出化身"放进挂起队列（按间隔重试；明确记原因）。</summary>
        private void QueuePendingPeer(PlayerKey key, string identity, PlayerProfileSnapshot profile,
            string recordKey)
        {
            m_pendingPeers[key] = new PendingPeer
            {
                Key = key,
                Name = profile?.Name ?? string.Empty,
                Identity = identity ?? string.Empty,
                RecordKey = recordKey ?? string.Empty,
                Profile = profile,
                LastAttemptMs = 0
            };
        }

        /// <summary>限流日志（同一个 key 至少间隔 10 秒；计数照常累加，绝不静默）。</summary>
        private void LogRateLimited(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastRateLimitedLogMs.TryGetValue(key, out long last) &&
                nowMs - last < RateLimitedLogIntervalMs)
                return;
            m_lastRateLimitedLogMs[key] = nowMs;
            Info(message);
        }

        /// <summary>限流错误（同一条持续性失败 10 秒最多一条；绝不静默）。</summary>
        private void ErrorRateLimited(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastRateLimitedLogMs.TryGetValue(key, out long last) &&
                nowMs - last < RateLimitedLogIntervalMs)
                return;
            m_lastRateLimitedLogMs[key] = nowMs;
            Error(message);
        }

        // ---------------------------------------------------------------- 日志

        /// <summary>同一个身份只写一行名字解析（rate-limited；身份只出前 8 位）。</summary>
        private void ResolveName(string recordKey, string name, string source)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                m_avatarNameUnresolved++;
                m_avatarAppearanceUnavailable++;
                ErrorOnce("name-missing:" + recordKey,
                    "身份 " + PlayerIdentity.Describe(recordKey) + " 没有可用名字（来源：" + source +
                    "）→ 不拿 \"Player\"/\"Android User\" 兜底；等真实名字到达后再建/更新化身");
                return;
            }
            // 同一身份 + 同一名字只写一行；名字真的变了会再写一行（不是每帧刷）。
            if (!m_reportedProblems.Add("name:" + recordKey + ":" + name)) return;
            m_avatarNamesResolved++;
            Info("名字解析：身份 " + PlayerIdentity.Describe(recordKey) + " → 「" + name +
                "」（来源：" + source + "）");
        }

        private void Info(string message) => m_context?.Diagnostics.Info(m_descriptor.Id, message);

        private void Warn(string message) => m_context?.Diagnostics.Warn(m_descriptor.Id, message);

        private void Error(string message) => m_context?.Diagnostics.Error(m_descriptor.Id, message, null);

        /// <summary>同一条持续性的错误只报一次（避免每帧刷屏，但绝不静默）。</summary>
        private void ErrorOnce(string key, string message)
        {
            if (!m_reportedProblems.Add(key)) return;
            m_context?.Diagnostics.Error(m_descriptor.Id, message, null);
        }

        // ---------------------------------------------------------------- 生命周期

        public void Detach(ModuleDetachReason reason)
        {
            if (m_transport != null)
            {
                try { m_transport.ClientJoined -= HandleClientJoined; } catch (Exception) { }
                try { m_transport.ClientLeft -= HandleClientLeft; } catch (Exception) { }
            }
            // 断开地形通道上的联机角色视图：本模块下线后，地形模块必须显式失败（找不到本机玩家 /
            // 算不出兴趣半径），而不是拿着已经失效的引用或退回"第一个有 GameWidget 的角色"。
            if (m_terrainBridge != null)
            {
                try { m_terrainBridge.BindAvatarTerrainLink(null); } catch (Exception) { }
                m_terrainBridge = null;
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(m_descriptor.Id); } catch (Exception) { }
            }
            // 主机下线前把当前角色状态抓进记录并落盘（照旧实现的退出保存）。
            // 只有主机（= 记录表已经按世界目录加载过）才写盘；客户端没有这份文件的所有权。
            if (m_records.WorldDirectory.Length > 0 && m_project != null)
            {
                foreach (PlayerKey key in m_spawner.Keys.ToArray())
                {
                    if (!m_spawner.TryGetRecordKey(key, out string recordKey)) continue;
                    m_records.TryGet(recordKey, out NetworkPlayerRecord previous);
                    NetworkPlayerRecord captured = CaptureForStorage(key, previous);
                    if (captured != null) m_records.Put(recordKey, captured);
                }
                m_records.SaveIfDirty();
            }
            m_spawner.ForgetAll();
            m_pendingPeers.Clear();
            m_recordKeyOwner.Clear();
            m_peerIdentities.Clear();
            m_peerSlots.Clear();
            m_slotAwareClients.Clear();
            m_peerSlotCounts.Clear();
            m_hostSlotSignatures.Clear();
            m_roster.Clear();
            m_appliedAvatarSignatures.Clear();
            // Phase 2 归一化状态随模块下线一起丢掉（重新上线后按新 Project 重新判定）。
            m_normalizeProject = null;
            m_normalizeApplied = false;
            m_normalizeAlreadyLogged = false;
            m_normalizeLastFailure = string.Empty;
            m_normalizeNoPortLogged = false;
            m_normalizeNoWorldLogged = false;
            m_normalizeNotClientLogged = false;
            m_capacityRefusedKeys.Clear();
            // 分配快照的应用标记随模块下线一起丢掉（重新上线后按当前 Project / 握手重新判定）。
            m_appliedAssignedProfileSignature = string.Empty;
            m_keptLocalStoreSignature = string.Empty;
            m_handshake = null;
            m_broadcast = null;
            m_messages = null;
            m_transport = null;
            m_downloadedWorlds = null;
            m_attached = false;
            Info("联机角色模块下线（" + reason + "）：只清理本模块自己的状态");
        }

        public string StatusLine() =>
            "player-avatar: host-avatars=" + m_createdClientAvatars +
            " client-host-avatars=" + m_createdHostAvatars +
            " removed=" + m_removedClientAvatars +
            " boatStandingSkips=" + (m_spawner?.BoatStandingSkips ?? 0) +
            " standingMountFlag=" + (m_spawner?.StandingMountFlagSnaps ?? 0) +
            " standingBodySnapshots=" + (m_spawner?.StandingBodySnapshots ?? 0) +
            " deferred=" + m_deferredLeaves.Count +
            " takeovers=" + m_identityTakeovers +
            " avatarsCreated=" + m_avatarsCreated +
            " avatarsUpdated=" + m_avatarsUpdated +
            " avatarsRemoved=" + m_avatarsRemoved +
            " avatarAppearanceApplied=" + m_avatarAppearanceApplied +
            " avatarNameResolved=" + m_avatarNamesResolved +
            " avatarNameUnresolved=" + m_avatarNameUnresolved +
            " avatarAppearanceUnavailable=" + m_avatarAppearanceUnavailable +
            " roster=" + m_roster.Count +
            " pending=" + m_pendingPeers.Count +
            " records=" + m_records.Count +
            " remote-positions=" + m_spawner.RemotePositionCount +
            " posApply=" + m_spawner.RemotePositionsApplied +
            " posSkippedOwn=" + m_spawner.RemotePositionsSkippedOwn +
            " posSkipped=" + m_spawner.RemotePositionsSkipped +
            // 2026-10-26 客户端推动物：客户端发出的"移动速度（碰撞前）"条数 / 主机写进化身 body 的次数。
            " moveVelSent=" + m_spawner.MoveVelocitySent +
            " moveVelApplied=" + m_spawner.MoveVelocityApplied +
            " slotsAnnounced=" + m_slotsAnnounced +
            " slotsRegistered=" + m_slotsRegistered +
            // Phase 2 客户端本机角色归一化：下载世界里的主机角色副本被摘掉了多少、
            // 本机真实槽位保留了多少、跳过/失败/容量拒绝各多少（实机排查一眼可见）。
            " normalizedLocalPlayers=" + m_normalizeLocalPlayers +
            " normalizedRemoved=" + m_normalizeRemoved +
            " normalizedKept=" + m_normalizeKept +
            " normalizeFailures=" + m_normalizeFailures +
            " normalizeSkippedNoWorld=" + m_normalizeSkippedNoWorld +
            " normalizeSkippedNotClient=" + m_normalizeSkippedNotClient +
            " normalizeSkippedAlready=" + m_normalizeSkippedAlready +
            " normalizeCapacityRefusals=" + m_normalizeCapacityRefusals +
            " normalizeAmbiguous=" + m_normalizeAmbiguous +
            " avatarCapacityRefusals=" + m_avatarCapacityRefusals +
            " localSlots=" + (m_broadcast?.IsHosting == true ? m_hostSlotsCount : m_localSlotsCount) +
            " tooManySlots=" + (m_tooManySlots + m_spawner.TooManyLocalSlots) +
            " legacyDefaultSlot=" + m_legacyDefaultSlot +
            " rosterMismatch=" + m_rosterMismatch +
            // 主机侧地形更新位置：每个联机角色一条（区块 + 半径 + 内容距离 + 已移动次数）。
            // 实机排查"主机有没有跟着客户端移动"直接读这一行；空表 = none。
            " hostUpdateLocations=" + m_spawner.DescribeHostUpdateLocations() +
            " updateLocationMoves=" + m_spawner.UpdateLocationMoves +
            (m_records.LoadFailed ? " 记录文件损坏(本会话不写盘)" : string.Empty) +
            " rejected=" + m_rejectedPeers +
            // 主机分配资料：真正应用次数 / 因本机已有更新编辑而保留本机值的次数
            // （旧实现每帧重放分配快照，会把编辑屏的改名/换肤覆盖回去）。
            " assignedPlacementApplied=" + m_placementApplied +
            " assignedPlacementPendingTicks=" + m_placementPendingTicks +
            " recordSpawnPointSaved=" + m_recordSpawnPointSaved +
            " assignedProfileApplied=" + m_assignedProfileApplied +
            " assignedProfileKeptLocal=" + m_assignedProfileKeptLocal +
            // 编辑玩家屏同步：进入/退出、重提资料、改名/换肤、主机广播、缺失资源、拒绝原因。
            " · " + m_editCounters.Describe();

    }
}
