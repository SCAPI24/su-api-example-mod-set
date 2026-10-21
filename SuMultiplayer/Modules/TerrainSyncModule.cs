using System;
using System.Collections.Generic;
using Engine;
using GameEntitySystem;
using SuMultiplayer.Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using SuMultiplayer.Terrain;

using SuMultiplayer.Protocol;
using PP = SuMultiplayer.Protocol.Position;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 地形同步模块：把"引擎事实"和"网络服务"接到一起，自己不含引擎细节。
    ///
    /// 依赖方向（模块化要点）：
    ///   Engine（桥）→ 事件 → 本模块 → Terrain/Net 服务 → 网络
    ///   本模块只通过 <see cref="IModuleContext"/> 拿端口/日志，不 new 别人的内部；
    ///   传输、摘要、载荷都是独立部件，任何一个出问题只影响本模块（宿主熔断）。
    ///
    /// **通道何时开启由玩家操作决定**（见 <see cref="ITransportControlPort"/>）：
    ///   主机 = 游戏内 CR 按钮；客户端 = Play 屏点房间行。
    ///   本模块不读配置里的 host/client 模式，也不在 Attach 时自己联网。
    ///
    /// 演示页标记：MOD01。
    /// </summary>
    public sealed class TerrainSyncModule : IMultiplayerModule, IChunkReverifyPort, IPeerBroadcastPort,
        IWindowBroadcastPort, IKnownRecordHashPort
    {
        private readonly ModuleDescriptor m_descriptor;
        private readonly SyncConfig m_config;
        private readonly TerrainSyncOptions m_options;
        private readonly TerrainBridge m_bridge = new TerrainBridge();
        private readonly MessageChannel m_messages;
        private readonly Port m_port;
        private readonly ControlPortImpl m_control;
        /// <summary>
        /// 本机资料存储（组合根创建并注入；角色创建界面写它、Hello 读它）。
        /// null = 组合根没接资料链 → Hello 不带资料（主机据此要求建角色）。
        /// </summary>
        private readonly LocalPlayerProfileStore m_profileStore;
        /// <summary>主机 Hello 资料闸门（联机角色模块在组合根里注入）。</summary>
        private IHelloProfileGate m_helloProfileGate;

        private IModuleContext m_context;
        private TcpTerrainServer m_server;
        private TcpTerrainClient m_client;
        private TerrainSyncHost m_host;
        private TerrainSyncClient m_clientSync;
        private ChunkKey m_lastCenter;
        private bool m_hasCenter;
        /// <summary>上一次上报的兴趣半径（区块）；旧实现按半径去重（m_lastSentClientTerrainInterestRadius）。</summary>
        private int m_lastRadius;
        /// <summary>最近一次**实际发出**的兴趣半径 / 可见范围（区块 / 米）；状态行与日志用。</summary>
        private int m_interestRadiusSent;
        private float m_interestVisibilitySent;
        /// <summary>兴趣半径发生变化的次数（不含握手前 0.5 秒重发；累计、只增不减）。</summary>
        private long m_interestRadiusChanges;
        /// <summary>实际发出的 InterestUpdate 条数（含握手前重发）。</summary>
        private long m_interestSends;
        private long m_lastReportMs;
        /// <summary>上一次上报兴趣的时刻（秒，Time.RealTime 口径）。</summary>
        private double m_lastInterestSeconds;
        private long m_nextReconnectMs;
        private int m_reconnectAttempts;
        private bool m_attached;
        private Action<ReceivedFrame> m_frameArrived;
        private Action<int, string, string, bool> m_clientJoined;
        /// <summary>主机侧：某个玩家端的连接已经不可达（转发给联机角色模块清角色）。</summary>
        private Action<int, string> m_clientLeft;
        private SuSubsystemTerrain m_shell;
        /// <summary>当前注入的是哪个 Project 的 SubsystemTerrain（整图重下会换 Project，必须重注入）。</summary>
        private Project m_shellProject;
        private bool m_shellMissingLogged;
        private bool m_verbose;
        /// <summary>
        /// 联机角色视图（由联机角色模块在 Attach 时接到传输端口上）。
        /// 地形模块靠它按**旧实现的规则**挑"本机玩家"（PlayersData 里不是网络化身的那一条）
        /// 与算兴趣半径；为 null 时**明确失败**，绝不退回"第一个有 GameWidget 的角色"。
        /// </summary>
        private IAvatarTerrainLink m_avatarLink;
        /// <summary>客户端握手时上报的"已有角色记录 hash"复用容器（每次连接一次，不热）。</summary>
        private readonly List<RecordHashRef> m_knownRecordScratch = new List<RecordHashRef>(64);
        /// <summary>持续性问题只报一次（原因照实写，不静默）。</summary>
        private readonly HashSet<string> m_reportedFrameProblems = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>玩家点了"加入房间"：通道断了要按间隔重连（这是加入流程的一部分，不是兜底）。</summary>
        private bool m_wantClient;
        private string m_wantHost = string.Empty;
        private int m_wantPort;
        private int m_hostPort;
        /// <summary>本次房间 / 会话标识（建房时生成；绝不落盘；见 Terrain/SessionIdentity.cs）。</summary>
        private string m_sessionId = string.Empty;
        /// <summary>客户端：本端上次应用到的改动序号（跨连接保存；重连 Hello 发回主机）。</summary>
        private long m_clientChangeSequence;
        /// <summary>客户端：最近一次 HelloAck 收到的主机会话标识（重连判定的第二半）。</summary>
        private string m_lastHostSessionId = string.Empty;
        /// <summary>客户端：已经写过"追帧被拒绝 → 整图重下"的日志没有（同一原因只写一行）。</summary>
        private bool m_clientCatchUpRejectionLogged;

        // —— 玩家位置同步（旧实现的 32Hz 位置快照通道）——
        //
        // Source: Mod/ScMultiplayer/Core/ScMultiplayerRuntimeState.cs:809-810
        //         （SyncBaseRate = 32；SyncPulseDuration = 1f / SyncBaseRate）
        // Source: Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerUpdateLoop.cs:3023-3026
        //         （位置同步在每个网络脉冲上发一次：if (!sleepAcceleration || pulse1Hz || force) SendGamePlayerPositionMessage(...)）
        // 本模块的 Tick 是**每渲染帧**（Frame.Update），所以这里自己攒一个 32Hz 的相位，
        // 否则会把位置快照按 60+Hz 灌进通道（旧实现不存在这种帧率相关行为）。
        private const float PositionPulseDuration = 1f / 32f;
        /// <summary>一个渲染帧最多补发几个位置脉冲（旧实现 MaxSyncPulsesPerUpdate = 4）。</summary>
        private const int MaxPositionPulsesPerFrame = 4;
        private double m_lastPositionPulseTime;
        private float m_positionPulseAccumulator;
        /// <summary>发送方自己的位置脉冲序号（旧 <c>client.Step</c> 在位置快照里的角色）。</summary>
        private int m_positionPulseIndex;
        private long m_positionSnapshotsSent;
        private long m_positionSnapshotsApplied;
        private long m_positionSnapshotsDropped;
        /// <summary>位置批里因收件端窗口外被裁掉的化身条目数（含窗口未知时只发自己的）。</summary>
        private long m_positionEntriesOutOfWindow;
        /// <summary>还没上报兴趣窗口的端数（只发它自己的位置；明确计数，绝不改发全体）。</summary>
        private long m_positionWindowUnknownPeers;
        /// <summary>
        /// 主机：确实捕获到、但这一条**没有任何投递目标**的改动数（订阅表 ∪ 客户端内存集合都为空）。
        /// 与 hostCellMissed 区分：前者是"没抓到"，后者是"抓到了但这一格没人持有"。
        /// </summary>
        private long m_hostCellNoRecipient;
        /// <summary>
        /// 客户端角色下本地写入落到了**主机路径**、而本端根本没有主机（连接断开 / 正在重连）的格数。
        /// 这些格当下没有接收者（要等重连后的强制核对纠正）；必须计数并限流告警，禁止静默。
        /// </summary>
        private long m_hostCellNoHost;
        private long m_lastHostCellNoHostLogMs;

        public TerrainSyncModule(SyncConfig config, TerrainSyncOptions options = null,
            LocalPlayerProfileStore profileStore = null)
        {
            m_config = config ?? new SyncConfig();
            m_profileStore = profileStore;
            m_port = new Port(this);
            m_control = new ControlPortImpl(this);
            m_options = options ?? new TerrainSyncOptions();
            m_verbose = m_config.VerboseLog;
            m_messages = new MessageChannel(new MessageRegistry(), SendFromMessageLayer, "terrain-sync");
            RegisterModuleMessages();
            m_descriptor = new ModuleDescriptor("terrain-sync", "地形同步")
            {
                ProvidesCapabilities = new[] { "terrain.sync" },
                HotPluggable = true,
                Essential = false
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        /// <summary>传输端口的实现（在模块内部，不对外暴露实现细节）。</summary>
        private sealed class Port : ITransportPort, IAvatarTerrainBridge, IWorldSessionPort,
            IPlayerProfileHandshakePort
        {
            private readonly TerrainSyncModule m_owner;
            public Port(TerrainSyncModule owner) { m_owner = owner; }
            public bool TrySend(MessageType type, byte[] payload) => m_owner.SendThroughChannel(type, payload);
            /// <summary>
            /// 定向发送（世界传输多客户端时必须）：只发给这个 clientId 的玩家端。
            /// 用 ITransferChannel.TrySend 会发给“最后加入的那个”，两条世界传输会互相串流。
            /// </summary>
            public bool TrySendTo(int clientId, MessageType type, byte[] payload) =>
                m_owner.SendThroughChannelTo(clientId, type, payload);
            public bool IsClientSendable(int clientId) => m_owner.IsClientSendable(clientId);
            public bool IsConnected => m_owner.ChannelConnected;
            public string StateText => m_owner.ChannelStateText;
            public event Action<ReceivedFrame> FrameArrived
            {
                add { m_owner.m_frameArrived += value; }
                remove { m_owner.m_frameArrived -= value; }
            }
            public event Action<int, string, string, bool> ClientJoined
            {
                add { m_owner.m_clientJoined += value; }
                remove { m_owner.m_clientJoined -= value; }
            }
            public event Action<int, string> ClientLeft
            {
                add { m_owner.m_clientLeft += value; }
                remove { m_owner.m_clientLeft -= value; }
            }
            public string HostIdentity => m_owner.ClientHostIdentity;
            public string HostName => m_owner.ClientHostName;
            public int LocalClientId => m_owner.ClientLocalClientId;
            /// <summary>联机角色模块从传输端口上取到这个接口并接上自己（见 IAvatarTerrainBridge 的说明）。</summary>
            public void BindAvatarTerrainLink(IAvatarTerrainLink link) => m_owner.BindAvatarTerrainLink(link);

            // IWorldSessionPort：世界传输模块用它给世界清单打标 / 客户端导入完成后回调地形侧。
            public string SessionId => m_owner.CurrentSessionId;
            public long CurrentChangeSequence => m_owner.CurrentChangeSequence;
            public void OnClientWorldInstalled(string worldDirectory, string sessionId, long baseSequence) =>
                m_owner.OnClientWorldInstalled(worldDirectory, sessionId, baseSequence);

            // IPlayerProfileHandshakePort：联机角色模块读握手资料 / 要求重新握手。
            public PlayerProfileSnapshot HostProfile => m_owner.ClientHostProfile;
            public PlayerProfileSnapshot AssignedProfile => m_owner.ClientAssignedProfile;
            public bool ProfileRequired => m_owner.ClientProfileRequired;
            public string ProfileRequiredReason => m_owner.ClientProfileRequiredReason;
            public bool AssignedSkinAssetPresent => m_owner.ClientAssignedSkinAssetPresent;
            public bool ReconnectClient(string reason) => m_owner.ReconnectClient(reason);
        }

        /// <summary>通道控制端口的实现：会话模块（UI 操作）通过它起停通道。</summary>
        private sealed class ControlPortImpl : ITransportControlPort
        {
            private readonly TerrainSyncModule m_owner;
            public ControlPortImpl(TerrainSyncModule owner) { m_owner = owner; }
            public bool StartHostServer(int port, out string reason) => m_owner.StartHostServer(port, out reason);
            public bool ConnectTo(string host, int port, out string reason) => m_owner.ConnectTo(host, port, out reason);
            public void StopChannel() => m_owner.StopChannel();
            public bool IsChannelRunning => m_owner.m_server != null || m_owner.m_client != null;
            public bool IsHosting => m_owner.m_server != null;
            public int ConnectedPlayerCount => m_owner.CountPlayerPeers();
            public string ChannelStateText => m_owner.ChannelStateText;
        }

        public TerrainBridge Bridge => m_bridge;
        /// <summary>组合根用它注册端口（模块自己不注册，避免模块间互相注册的隐式耦合）。</summary>
        public ITransportPort TransportPort => m_port;
        /// <summary>组合根把它注册给世界传输模块：世界清单的会话标识 / 导出基线由这里提供。</summary>
        public IWorldSessionPort WorldSessionPort => m_port;
        /// <summary>组合根把它交给会话模块（UI 操作 → 起停通道）。</summary>
        public ITransportControlPort ControlPort => m_control;
        /// <summary>组合根把它注册给联机角色模块：握手资料（主机/分配/要求建角色）+ 重新握手。</summary>
        public IPlayerProfileHandshakePort ProfileHandshakePort => m_port;

        /// <summary>
        /// 组合根在装配时把联机角色模块（它拥有角色记录与本地玩家）接到主机 Hello 闸门上。
        /// 必须在任何连接之前调用；晚了会按"没有资料闸门"的旧语义处理（不静默：调用方会看日志）。
        /// </summary>
        public void SetHelloProfileGate(IHelloProfileGate gate)
        {
            m_helloProfileGate = gate;
            if (m_host != null) m_host.ProfileGate = gate;
        }

        /// <summary>本机资料存储（角色创建界面 / Hello 共用）。组合根创建后传入。</summary>
        public LocalPlayerProfileStore ProfileStore => m_profileStore;

        /// <summary>
        /// 消息层（类型 id ↔ 解析器/处理器 + 发送器）。第三方模块通过
        /// <see cref="IMessageRegistryPort"/> 拿到同一个实例；也可以直接读这个属性。
        /// </summary>
        public MessageRegistry MessageRegistry => m_messages.Registry;

        /// <summary>第三方模块的发送入口（与内置消息走同一条通道）。</summary>
        public IMessageSender MessageSender => m_messages.Sender;

        /// <summary>组合根注册给第三方模块的端口（Registry + Sender）。</summary>
        public IMessageRegistryPort MessagePort => m_messages;

        private const string ModuleMessageOwner = "terrain-sync-module";

        /// <summary>
        /// 模块这一层直接消费的消息（联机角色 / 实时地图）。其余类型由 TerrainSyncHost /
        /// TerrainSyncClient / 世界传输 / 编辑权威 / 生物模块各自注册。
        /// </summary>
        private void RegisterModuleMessages()
        {
            MessageRegistry registry = m_messages.Registry;
            registry.Register((int)MessageType.InterestUpdate, InterestUpdate.Parser,
                new DelegateMessageHandler<InterestUpdate>(OnInterestMessage), ModuleMessageOwner);
            // ⚠️ 线上类型：MessageType.PlayerPosition（"单条"）的**负载是 PlayerPositions{entries:[1]}**，
            // 不是裸 PlayerPosition —— `Protocol/position.proto` 的注释写明
            // "Client -> host: one snapshot. Host -> client: a batch for all avatars."，
            // 客户端编码走 `PlayerPositionCodec.EncodeSingle`（同一个 wrapper）。
            // 这里原先注册的是 PP.PlayerPosition.Parser：field 1 在 wrapper 里是 length-delimited
            // entries，在裸消息里却是 int32 client_id，protobuf 会把它当未知字段跳过 ⇒ 解出一个
            // 没有 position/rotation/velocity/look_angles 的空条目，**每一份客户端位置都被丢掉**
            // （实机证据：主机 posApplied=0 / posDropped 每 5 秒涨 ~300，日志只有一行
            //  "位置快照解码失败：条目缺少 position/rotation/velocity/look_angles"）。
            // 修复只改接收端解析器，线上字节完全不变（旧客户端不需要升级）。
            registry.Register((int)MessageType.PlayerPosition, PP.PlayerPositions.Parser,
                new DelegateMessageHandler<PP.PlayerPositions>(OnPlayerPosition), ModuleMessageOwner);
            registry.Register((int)MessageType.PlayerPositions, PP.PlayerPositions.Parser,
                new DelegateMessageHandler<PP.PlayerPositions>(OnPlayerPositions), ModuleMessageOwner);
            // live-map 请求是冻结的原始格式（网页 feed 不能变），走原始处理器。
            registry.RegisterRaw(MessageType.MapRequest, OnMapRequest, ModuleMessageOwner);
            m_messages.Problem += HandleMessageProblem;
        }

        private readonly Dictionary<string, long> m_lastMessageProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>消息层的问题（未知类型 / 解析失败 / 处理器异常）限流写一行。</summary>
        private void HandleMessageProblem(string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            string key = message.Length > 80 ? message.Substring(0, 80) : message;
            if (m_lastMessageProblemMs.TryGetValue(key, out long last) && nowMs - last < 5000) return;
            m_lastMessageProblemMs[key] = nowMs;
            Log("警告：" + message);
        }

        private bool SendFromMessageLayer(int clientId, MessageType type, byte[] payload)
        {
            if (m_server != null)
            {
                // 主机：目标必须显式（不猜"最后加入的那个"）。
                return clientId > 0 && m_server.TrySend(clientId, type, payload);
            }
            if (m_client != null)
            {
                // 客户端：0 = 发给主机；其它目标是协议方向错误。
                return clientId == 0 && m_client.IsAlive && m_client.TrySend(type, payload);
            }
            return false;
        }

        private void OnInterestMessage(MessageContext context, InterestUpdate message)
        {
            if (m_server == null || !IsPlayerPeer(context.ClientId)) return;
            int reportedRadius = message.Radius;
            if (m_avatarLink == null)
            {
                LogOnce("interest-no-avatar-link-host",
                    "收到 clientId=" + context.ClientId + " 的兴趣半径 " + reportedRadius +
                    "，但没有接上联机角色视图 → 无法交给主机的地形更新位置维护");
                return;
            }
            m_avatarLink.OnClientInterestRadiusReported(context.ClientId, reportedRadius);
        }

        private void OnPlayerPosition(MessageContext context, PP.PlayerPositions message)
        {
            if (m_server == null || !IsPlayerPeer(context.ClientId)) return;
            HandlePlayerPositionMessage(context, message);
        }

        private void OnPlayerPositions(MessageContext context, PP.PlayerPositions message)
        {
            // 客户端收主机批；主机现在也收客户端的多本机角色批（单角色仍走 PlayerPosition 单条）。
            if (m_clientSync == null && m_server == null) return;
            if (m_server != null && !IsPlayerPeer(context.ClientId)) return;
            HandlePlayerPositionsMessage(context, message);
        }

        private void OnMapRequest(MessageContext context, byte[] payload)
        {
            LiveMapCodec.DecodeRequest(payload, out int mapRadius, out bool mapAll,
                out bool hasRequestedCenter, out int requestedChunkX, out int requestedChunkZ);
            SendLiveMap(context.ClientId, mapRadius, mapAll, hasRequestedCenter, requestedChunkX,
                requestedChunkZ);
        }

        /// <summary>
        /// 联机角色模块接上 / 断开它的视图（<see cref="IAvatarTerrainBridge"/>）。
        /// 本机玩家判定（旧实现：PlayersData 里不是网络化身的那一条）与兴趣半径算法都住在
        /// 联机角色模块里，地形模块只是消费方；断开后本机玩家定位会明确失败并写清原因。
        /// </summary>
        private void BindAvatarTerrainLink(IAvatarTerrainLink link)
        {
            m_avatarLink = link;
            Log(link == null
                ? "联机角色视图已断开 → 本机玩家判定与兴趣半径上报会明确失败（不退回旧的 GameWidget 扫描）"
                : "已接上联机角色视图：本机玩家 = PlayersData 里不是网络化身的那一条（旧实现规则）");
        }

        public void Attach(IModuleContext context)
        {
            m_context = context;
            // v9 实时地图：主机端动物位置来源（只读）。缺失时明确写一行，animals[] 保持空，
            // 绝不用客户端复制体或别的角色顶替。
            if (context.TryGetPort(out IHostCreatureCellSource creatureCells) && creatureCells != null)
            {
                m_creatureCells = creatureCells;
            }
            else
            {
                m_creatureCells = null;
                LogOnce("livemap-animals-no-source",
                    "没有接上主机端动物位置来源（IHostCreatureCellSource）→ 实时地图的 animals[] " +
                    "会一直为空（请确认组合根注册了它；绝不拿客户端复制体顶替主机动物）");
            }
            m_options.DefaultRadius = m_config.Radius;
            m_bridge.ChunkLoaded += HandleChunkLoaded;
            m_bridge.ChunkUnloaded += HandleChunkUnloaded;
            m_bridge.ChunkAllocated += HandleChunkAllocated;
            m_bridge.ChunkFreed += HandleChunkFreed;
            m_bridge.ChunkWireExcluded += HandleChunkWireExcluded;
            m_bridge.ChunkWireIncluded += HandleChunkWireIncluded;
            m_bridge.FrameArrived += HandleFrame;
            m_bridge.HostCellChanged += HandleHostCellChanged;
            m_attached = true;
            // 刻意**不在这里开通道**：建房/加入是玩家的操作，不是启动配置。
            Log("地形同步模块就绪（通道等玩家操作：游戏内 CR 建房 / Play 屏点房间加入）");
        }

        // ------------------------------------------------------------ 通道起停

        private bool StartHostServer(int port, out string reason)
        {
            reason = null;
            if (m_server != null)
            {
                reason = "主机通道已经在监听端口 " + m_hostPort;
                return false;
            }
            try
            {
                m_server = new TcpTerrainServer(port);
                // 网络线程 → 主线程：只入队，不碰引擎（引擎读写都在 Pump 里）
                m_server.FrameReceived += m_bridge.PostFrame;
                m_server.Start();
                m_hostPort = m_server.Port;
                m_host = new TerrainSyncHost(m_bridge, m_server, m_options, attachToChannel: false,
                    messages: m_messages);
                // 资料闸门（联机角色模块）：Hello 时决定"有没有角色记录 / 要不要先建角色"。
                m_host.ProfileGate = m_helloProfileGate;
                // 会话标识：建房（房间会话开始）时生成一次，只存在内存里；主机重启 ⇒ 新标识 ⇒
                // 客户端手里的世界被判过期 ⇒ 整图重下。绝不落盘（见 Terrain/SessionIdentity.cs）。
                m_sessionId = SessionIdentity.NewSessionId();
                m_host.SessionId = m_sessionId;
                m_host.ClientJoined += HandleHostClientJoined;
                // 重连判定 / 追帧的明确日志（Terrain 层不写引擎日志，由这里照原样写一行）。
                m_host.RejoinTrace += HandleHostRejoinTrace;
                m_host.CatchUpTrace += HandleHostCatchUpTrace;
                m_host.CatchUpProblem += HandleHostCatchUpProblem;
                // 主机"欠着裁决的上报"（内容一直没加载出来 / 客户端断开时还有欠账）**已限流**，
                // 这里照写一行明确原因（绝不静默放弃，也绝不假装匹配）。
                m_host.ReportUnresolved += HandleHostReportUnresolved;
                // 欠更新计数器（实时地图格内数字）的生命周期决策：加标记 / 核对通过移除 / 客户端离开清掉它的
                // 全部标记 / 会话结束整体清空。Terrain 层不认识引擎日志，所以走事件由这里写一行明确日志。
                m_host.PendingTrace += HandleHostPendingTrace;
                // 按需单块加载（客户端上报的边缘块）：超时放弃 / 被拒绝 / 汇总，Terrain 层已限流。
                // 这是"客户端站着不动、边缘黄框也能变绿"的唯一证据链（旧实现的请求只到一行 verbose 日志）。
                m_host.OnDemandTrace += HandleHostOnDemandTrace;
                // 玩家端的连接**已经不可达**（对端关闭 / 链路失效）：必须写一行明确原因，
                // 并把它在主机世界里的联机角色清掉 —— 否则那个角色实体会停在最后一个已应用的位置上
                // （实机故障：主机侧角色卡死在 (-79.6,78.4,45.8)，重连后还会多出一个）。
                m_host.ClientLost += HandleHostClientLost;
                // 身份（UserId）会话记录的建立 / 重连复用 / 房间结束清空 —— Terrain 层不认识引擎日志，
                // 由这里照原样写一行（句子里只有身份前 8 位与稳定键，绝不出现 GUID 全值）。
                m_host.SessionTrace += HandleHostSessionTrace;
                // 身份缺失（Hello 没带 UserId）走的是**按 clientId 单独一条记录**的退化路径：
                // 必须当错误写一行，绝不静默合并（也绝不用 clientId 冒充身份）。
                m_host.SessionProblem += HandleHostSessionProblem;
                // 客户端"当前内存区块集合"（v8 实时地图客户端线框）：整集收齐 / 增量汇总 / 载荷被拒绝
                // 都在 Terrain 层限流，这里照原样写一行（拒绝写成 Warning，绝不静默丢载荷）。
                m_host.ClientChunkSetTrace += HandleHostClientChunkTrace;
                m_host.ClientChunkSetProblem += HandleHostClientChunkProblem;
                // HelloAck 要带回**主机身份**：客户端建"主机角色"必须按身份绑定（不用 index）。
                // 身份来自 UserId.dat；这里注入到 Terrain 层（Terrain 层不认识 Game.UserManager）。
                m_host.Identity = PlayerIdentity.LocalIdentity();
                m_host.Name = PlayerIdentity.LocalName();
                if (string.IsNullOrEmpty(m_host.Identity))
                {
                    Log("警告：本机身份不可用（UserId.dat / UserManager.ActiveUser 为空）—— " +
                        "HelloAck 会带回空身份，客户端将明确拒绝创建主机角色（不做 index 兜底）");
                }
                Log("主机通道已打开：TCP 端口 " + m_hostPort + " · 半径 " + m_config.Radius +
                    " · 会话 " + SessionIdentity.Short(m_sessionId) +
                    "（32 位十六进制，只在内存里；主机重启会换新会话）" +
                    " · 本机玩家走到哪，窗口就覆盖哪（客户端会来核对摘要）");
                return true;
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + " " + e.Message + "（端口 " + port + "）";
                Log("主机通道打开失败：" + reason);
                try { m_server?.Dispose(); } catch (Exception) { }
                m_server = null;
                m_host = null;
                m_sessionId = string.Empty;
                return false;
            }
        }

        private bool ConnectTo(string host, int port, out string reason)
        {
            reason = null;
            m_wantClient = true;
            m_wantHost = host;
            m_wantPort = port;
            m_reconnectAttempts = 0;
            if (m_client != null) return true;
            return StartClientOnce(out reason);
        }

        private bool StartClientOnce(out string reason)
        {
            reason = null;
            try
            {
                m_client = new TcpTerrainClient(m_wantHost, m_wantPort);
                if (!m_client.Connect(3000))
                {
                    reason = "连接 " + m_wantHost + ":" + m_wantPort + " 失败（状态 " + m_client.State + "）";
                    m_client.Dispose();
                    m_client = null;
                    return false;
                }
                m_client.FrameReceived += m_bridge.PostFrame;
                m_clientSync = new TerrainSyncClient(m_bridge, m_client, m_options, attachToChannel: false,
                    messages: m_messages);
                // 客户端同步的"明确原因行"（每帧预算队列饱和 / 回传载荷解码失败）：
                // Terrain 层不写引擎日志，由模块照原样写一行（事件本身已限流）。
                m_clientSync.Saturated += HandleClientQueueSaturated;
                m_clientSync.DecodeFailed += HandleClientDecodeFailed;
                // 客户端"当前内存区块集合"编码/上限的明确失败（正常不会触发）：必须让用户看见，写 Warning。
                m_clientSync.ClientChunkSetFailed += HandleClientChunkSetFailed;
                // 断线重连：重连判定拒绝 / 追帧拒绝 / 追帧过程日志。拒绝是显式失败（不是静默降级）。
                m_clientSync.RejoinRejected += HandleClientRejoinRejected;
                m_clientSync.CatchUpRejected += HandleClientCatchUpRejected;
                m_clientSync.CatchUpTrace += HandleClientCatchUpTrace;
                m_clientSync.CatchUpProblem += HandleClientCatchUpProblem;
                // (重)连后重新排队常驻区块 / 同一块反复不一致 / 回传落地后未发起核对：
                // Terrain 层只抛已限流的事件，这里照原样各写一行（不静默、不刷屏）。
                m_clientSync.ResidentRequeue += HandleClientResidentRequeue;
                m_clientSync.RepeatedMismatch += HandleClientRepeatedMismatch;
                m_clientSync.VerifyNotStarted += HandleClientVerifyNotStarted;
                // 同一块连续不一致重试超过上限（停止自动重报，避免无界 1:1 回传循环）：Warning 一行（已限流）。
                m_clientSync.MismatchRetryExhausted += HandleClientMismatchRetryExhausted;
                // Hello 末尾追加本机身份（UserId.dat）+ 名字：主机据此按身份建客户端角色。
                // 再追加"本端是否**此刻正在游玩**这个下载来的联机世界" + 下载世界的会话标识 + 上次应用序号：
                // 主机只接受会话一致、序号仍在保留历史内的重连；否则明确按首次加入处理（整图重下）。
                // 判定只有一条（RejoinFlagPolicy）：在游戏里 + Project + 下载登记 + 会话与主机一致。
                bool alreadyHasWorld = IsPlayingDownloadedWorld(out string rejoinReason,
                    out string storedSessionId, out long storedBaseSequence);
                long lastApplied = alreadyHasWorld
                    ? Math.Max(m_clientChangeSequence, storedBaseSequence)
                    : 0L;
                m_clientSync.SetAppliedChangeSequence(lastApplied);
                // 本机资料：角色创建界面写进 store 的优先，其次本机角色（PlayerData.Name），
                // **绝不**用引擎固定显示名 "Android User" 顶替真实资料（PlayerIdentity.LocalName 已修正）。
                PlayerProfileSnapshot localProfile = m_profileStore?.Profile;
                string helloName = localProfile != null && localProfile.HasName
                    ? localProfile.Name
                    : PlayerIdentity.LocalName();
                // 本机已有的角色记录 hash（来自下载世界 pak 里的 SuMultiplayerPlayers.xml）：
                // 主机据此只重发变化了的角色记录。读失败 / 首次加入 = 0 条（主机全量补发，不会漏）。
                m_knownRecordScratch.Clear();
                int knownRecords = m_avatarLink != null
                    ? m_avatarLink.CaptureKnownRecordHashes(m_knownRecordScratch)
                    : 0;
                m_clientSync.SendHello(helloName, PlayerIdentity.LocalIdentity(),
                    alreadyHasWorld,
                    alreadyHasWorld ? storedSessionId : string.Empty,
                    lastApplied, localProfile, m_knownRecordScratch);
                // 新连接 = 新窗口：兴趣中心/半径必须重新上报（否则主机的旧窗口还停在上一轮的位置）。
                m_hasCenter = false;
                m_lastRadius = 0;
                m_clientCatchUpRejectionLogged = false;
                m_nextReconnectMs = (long)(Time.RealTime * 1000.0) + 5000;
                // 每个握手恰好一行：角色 / 重连还是首次加入 / 依据 / 世界传输接下来会做什么。
                Log("握手（role=client）：已连接主机 " + m_wantHost + ":" + m_wantPort +
                    " · 半径 " + m_config.Radius +
                    " · " + (alreadyHasWorld ? "重连" : "首次加入（含 Play 屏加入）") +
                    " · alreadyHasWorld=" + (alreadyHasWorld ? "true" : "false") +
                    " · 下载世界会话=" + SessionIdentity.Short(storedSessionId) +
                    " · 最近主机会话=" + SessionIdentity.Short(m_lastHostSessionId) +
                    " · 上次应用序号=" + lastApplied +
                    " · 已知角色记录=" + knownRecords + " 条" +
                    " · 依据：" + rejoinReason +
                    " · 世界传输：" + (alreadyHasWorld
                        ? "主机不重发整图，只按序号追帧（同会话）"
                        : "等主机下发整图，收齐后导入并进入世界"));
                return true;
            }
            catch (Exception e)
            {
                reason = e.GetType().Name + " " + e.Message;
                try { m_client?.Dispose(); } catch (Exception) { }
                m_client = null;
                m_clientSync = null;
                return false;
            }
        }

        private void StopChannel()
        {
            m_wantClient = false;
            try { if (m_server != null) m_server.FrameReceived -= m_bridge.PostFrame; } catch (Exception) { }
            try { if (m_client != null) m_client.FrameReceived -= m_bridge.PostFrame; } catch (Exception) { }
            try { if (m_clientSync != null) m_clientSync.Saturated -= HandleClientQueueSaturated; } catch (Exception) { }
            try { if (m_clientSync != null) m_clientSync.DecodeFailed -= HandleClientDecodeFailed; } catch (Exception) { }
            try { if (m_clientSync != null) m_clientSync.ClientChunkSetFailed -= HandleClientChunkSetFailed; } catch (Exception) { }
            try { if (m_clientSync != null) m_clientSync.RejoinRejected -= HandleClientRejoinRejected; } catch (Exception) { }
            try { if (m_clientSync != null) m_clientSync.CatchUpRejected -= HandleClientCatchUpRejected; } catch (Exception) { }
            try { if (m_clientSync != null) m_clientSync.CatchUpTrace -= HandleClientCatchUpTrace; } catch (Exception) { }
            try { if (m_clientSync != null) m_clientSync.CatchUpProblem -= HandleClientCatchUpProblem; } catch (Exception) { }
            try { if (m_clientSync != null) m_clientSync.ResidentRequeue -= HandleClientResidentRequeue; } catch (Exception) { }
            try { if (m_clientSync != null) m_clientSync.RepeatedMismatch -= HandleClientRepeatedMismatch; } catch (Exception) { }
            try { if (m_clientSync != null) m_clientSync.VerifyNotStarted -= HandleClientVerifyNotStarted; } catch (Exception) { }
            try { if (m_host != null) m_host.ClientJoined -= HandleHostClientJoined; } catch (Exception) { }
            try { if (m_host != null) m_host.ReportUnresolved -= HandleHostReportUnresolved; } catch (Exception) { }
            try { if (m_host != null) m_host.RejoinTrace -= HandleHostRejoinTrace; } catch (Exception) { }
            try { if (m_host != null) m_host.CatchUpTrace -= HandleHostCatchUpTrace; } catch (Exception) { }
            try { if (m_host != null) m_host.CatchUpProblem -= HandleHostCatchUpProblem; } catch (Exception) { }
            // 会话结束：欠更新计数 + 全部身份会话记录**整体清空**（只在内存里，不落盘）。
            // 这一步必须还在订阅期内做，由 TerrainSyncHost 的 PendingTrace / SessionTrace
            // 抛出那两句明确日志（下面再退订）。
            try { if (m_host != null) m_host.ClearPendingMarks(); } catch (Exception) { }
            try { if (m_host != null) m_host.PendingTrace -= HandleHostPendingTrace; } catch (Exception) { }
            try { if (m_host != null) m_host.ClientLost -= HandleHostClientLost; } catch (Exception) { }
            try { if (m_host != null) m_host.SessionTrace -= HandleHostSessionTrace; } catch (Exception) { }
            try { if (m_host != null) m_host.SessionProblem -= HandleHostSessionProblem; } catch (Exception) { }
            try { if (m_host != null) m_host.ClientChunkSetTrace -= HandleHostClientChunkTrace; } catch (Exception) { }
            try { if (m_host != null) m_host.ClientChunkSetProblem -= HandleHostClientChunkProblem; } catch (Exception) { }
            try { m_server?.Dispose(); } catch (Exception) { }
            try { m_client?.Dispose(); } catch (Exception) { }
            m_server = null;
            m_client = null;
            m_host = null;
            m_clientSync = null;
            m_hostClientId = 0;
            m_hostPort = 0;
            m_hasCenter = false;
            m_lastRadius = 0;
            // 兴趣/动物计数属于"这一次通道"：断开后复位，下一次房间从 0 重新计数（日志里写明）。
            m_interestRadiusSent = 0;
            m_interestVisibilitySent = 0f;
            m_interestRadiusChanges = 0;
            m_interestSends = 0;
            m_animalsInFeed = 0;
            m_animalsTruncated = 0;
            m_lastRateLimitedLogMs.Clear();
            // 会话 / 序号状态属于"这一次房间"：离开房间后不复用（下次加入必须重新握手判定）。
            m_sessionId = string.Empty;
            m_clientChangeSequence = 0L;
            m_lastHostSessionId = string.Empty;
            m_clientCatchUpRejectionLogged = false;
            // 位置同步的相位属于"这一次连接"：断开后复位，重连时从 0 重新攒（不把旧相位补发出去）。
            m_lastPositionPulseTime = 0.0;
            m_positionPulseAccumulator = 0f;
            Log("通道已关闭（本模块自己的连接；引擎外壳保持原样）");
        }

        private int CountPlayerPeers()
        {
            if (m_server == null) return 0;
            int count = 0;
            PeerInfo[] peers = m_server.ListPeers();
            for (int i = 0; i < peers.Length; i++)
            {
                if (peers[i].Alive && peers[i].State == ChannelState.Connected && peers[i].Kind == ClientKind.Player)
                    count++;
            }
            return count;
        }

        // ------------------------------------------------------------ 端口：世界信息广播 / 编辑超时回滚

        /// <summary>主机：把一条消息发给**所有在线玩家端**（世界信息 2 Hz；调试端不参与）。</summary>
        public int BroadcastToPlayers(MessageType type, byte[] payload)
        {
            if (m_server == null) return 0;
            int sent = 0;
            PeerInfo[] peers = m_server.ListPeers();
            for (int i = 0; i < peers.Length; i++)
            {
                PeerInfo peer = peers[i];
                if (!peer.Alive || peer.Kind != ClientKind.Player) continue;
                if (peer.State != ChannelState.Connected && peer.State != ChannelState.Backpressured) continue;
                if (m_server.TrySend(peer.Id, type, payload)) sent++;
            }
            return sent;
        }

        /// <summary>
        /// 主机：把一条消息发给除 <paramref name="excludeClientId"/> 外的**所有在线玩家端**
        /// （皮肤资源转发专用：发起者只收回执，绝不再收到资源）。
        /// 过滤口径与 <see cref="BroadcastToPlayers"/> 完全一致，只多跳过目标 clientId；
        /// 返回成功入队的端数（0 = 没有可发目标或全部失败，调用方据此计数，不静默）。
        /// </summary>
        public int BroadcastToPlayersExcept(int excludeClientId, MessageType type, byte[] payload)
        {
            if (m_server == null) return 0;
            int sent = 0;
            PeerInfo[] peers = m_server.ListPeers();
            for (int i = 0; i < peers.Length; i++)
            {
                PeerInfo peer = peers[i];
                if (peer.Id == excludeClientId) continue;
                if (!peer.Alive || peer.Kind != ClientKind.Player) continue;
                if (peer.State != ChannelState.Connected && peer.State != ChannelState.Backpressured) continue;
                if (m_server.TrySend(peer.Id, type, payload)) sent++;
            }
            return sent;
        }

        public int PlayerPeerCount => CountPlayerPeers();

        /// <summary>
        /// 主机：按 clientId 升序抓在线玩家端 + 稳定用户键（逐用户投递的 mask 位序依据）。
        /// 身份还没就绪的连接**不出现**在结果里（调用方计数），绝不按 clientId 顶替用户键。
        /// </summary>
        public int CapturePlayerUsers(List<PlayerPeerUser> target)
        {
            if (target == null) return 0;
            target.Clear();
            if (m_server == null || m_host == null) return 0;
            PeerInfo[] peers = m_server.ListPeers();
            List<PeerInfo> players = new List<PeerInfo>(peers.Length);
            for (int i = 0; i < peers.Length; i++)
            {
                PeerInfo peer = peers[i];
                if (!peer.Alive || peer.Kind != ClientKind.Player) continue;
                if (peer.State != ChannelState.Connected && peer.State != ChannelState.Backpressured) continue;
                players.Add(peer);
            }
            players.Sort(ComparePeerById);
            for (int i = 0; i < players.Count; i++)
            {
                int clientId = players[i].Id;
                if (!m_host.TryGetClientUserKey(clientId, out string userKey)) continue;
                target.Add(new PlayerPeerUser(clientId, userKey));
            }
            return target.Count;
        }

        private static int ComparePeerById(PeerInfo left, PeerInfo right) => left.Id.CompareTo(right.Id);

        /// <summary>
        /// 主机：按 mask 发给 <paramref name="roster"/> 对应下标的玩家端 —— 同一份 payload、一次编码。
        /// 位对应的连接现在必须仍是"在线玩家端"才入队；<paramref name="deliveredMask"/> 回填实际入队位，
        /// 没入队的位由调用方保留重试（这里绝不假装成功）。
        /// </summary>
        public int BroadcastToPlayersMasked(MessageType type, byte[] payload,
            IReadOnlyList<PlayerPeerUser> roster, ulong mask, out ulong deliveredMask)
        {
            deliveredMask = 0UL;
            if (m_server == null || roster == null || mask == 0UL) return 0;
            PeerInfo[] peers = m_server.ListPeers();   // 一次取快照：一次编码 → N 个连接并发入队
            int sent = 0;
            int count = roster.Count < 64 ? roster.Count : 64;
            for (int i = 0; i < count; i++)
            {
                ulong bit = 1UL << i;
                if ((mask & bit) == 0UL) continue;
                int clientId = roster[i].ClientId;
                if (!IsConnectedPlayerPeer(peers, clientId)) continue;
                if (!m_server.TrySend(clientId, type, payload)) continue;
                deliveredMask |= bit;
                sent++;
            }
            return sent;
        }

        /// <summary>这一位对应的连接现在还是不是"在线的玩家端"（背压也算在线：消息会排队）。</summary>
        private static bool IsConnectedPlayerPeer(PeerInfo[] peers, int clientId)
        {
            if (peers == null) return false;
            for (int i = 0; i < peers.Length; i++)
            {
                PeerInfo peer = peers[i];
                if (peer.Id != clientId) continue;
                if (!peer.Alive || peer.Kind != ClientKind.Player) return false;
                return peer.State == ChannelState.Connected || peer.State == ChannelState.Backpressured;
            }
            return false;
        }

        public bool IsPlayerClient(int clientId) => IsPlayerPeer(clientId);

        /// <summary>
        /// 主机：窗口（外扩 <paramref name="chunkMargin"/> 环）包含该位置的在线玩家端 → 并发入队。
        /// 一次编码、同一份 payload；窗口口径与地形 / 编辑校验 / 实体同步完全相同。
        /// </summary>
        public int BroadcastToPlayersAt(MessageType type, byte[] payload, Vector3 position,
            int chunkMargin, out int candidates)
        {
            candidates = 0;
            if (m_server == null || payload == null) return 0;
            if (chunkMargin < 0) chunkMargin = 0;
            int chunkX = EntityInterestWindowDiff.ChunkOf(position.X);
            int chunkZ = EntityInterestWindowDiff.ChunkOf(position.Z);
            PeerInfo[] peers = m_server.ListPeers();
            int sent = 0;
            for (int i = 0; i < peers.Length; i++)
            {
                PeerInfo peer = peers[i];
                if (!peer.Alive || peer.Kind != ClientKind.Player) continue;
                if (peer.State != ChannelState.Connected &&
                    peer.State != ChannelState.Backpressured) continue;
                if (!TryGetClientRange(peer.Id, out int centerX, out int centerZ, out int radius))
                    continue;   // 还没上报兴趣窗口：明确不计入候选，绝不用"全体"替代
                candidates++;
                if (!EntityInterestWindowDiff.InWindow(chunkX, chunkZ, centerX, centerZ,
                        radius + chunkMargin)) continue;
                if (!m_server.TrySend(peer.Id, type, payload)) continue;
                sent++;
            }
            return sent;
        }

        /// <summary>
        /// 主机：枚举"此刻有可用兴趣窗口的在线玩家端"（逐端过滤内容时用）。
        /// 顺序 = 稳定用户槽位顺序（CapturePlayerUsers 同一顺序），不随连接重排变化。
        /// </summary>
        public int CaptureClientWindows(List<ClientWindowView> target)
        {
            if (target == null) return 0;
            target.Clear();
            if (m_server == null) return 0;
            PeerInfo[] peers = m_server.ListPeers();
            List<PeerInfo> players = new List<PeerInfo>(peers.Length);
            for (int i = 0; i < peers.Length; i++)
            {
                PeerInfo peer = peers[i];
                if (!peer.Alive || peer.Kind != ClientKind.Player) continue;
                if (peer.State != ChannelState.Connected &&
                    peer.State != ChannelState.Backpressured) continue;
                players.Add(peer);
            }
            players.Sort(ComparePeerById);
            for (int i = 0; i < players.Count; i++)
            {
                int clientId = players[i].Id;
                if (!TryGetClientRange(clientId, out int centerX, out int centerZ,
                        out int radius)) continue;   // 还没上报窗口：不算候选，绝不猜
                target.Add(new ClientWindowView(clientId, centerX, centerZ, radius));
            }
            return target.Count;
        }

        /// <summary>主机：给单个在线玩家端发一条已编码消息（逐端过滤后投递用）。</summary>
        public bool SendToClient(int clientId, MessageType type, byte[] payload)
        {
            if (m_server == null || payload == null) return false;
            PeerInfo[] peers = m_server.ListPeers();
            if (!IsConnectedPlayerPeer(peers, clientId)) return false;
            return m_server.TrySend(clientId, type, payload);
        }

        /// <summary>
        /// 主机：窗口包含该玩家端化身位置的端 + 化身自己的端（永远包含）。
        /// 位置来自主机侧角色实体的 <c>ComponentBody.Position</c>（<see cref="IAvatarTerrainLink"/>），
        /// 不按区块中心猜。化身还没实体 → 只发自己的端并回填 <c>avatarCellKnown=false</c>。
        ///
        /// <paramref name="avatarClientId"/> = 0 表示**主机本机玩家**（不是联机化身）：
        /// 位置取同一份"本机玩家位置"口径；它没有"自己的端"，只发窗口内的观察者端。
        /// </summary>
        public int BroadcastToPlayersAtAvatar(MessageType type, byte[] payload, int avatarClientId,
            int chunkMargin, out int candidates, out bool avatarCellKnown)
        {
            candidates = 0;
            avatarCellKnown = false;
            if (m_server == null || payload == null || avatarClientId < 0) return 0;
            if (chunkMargin < 0) chunkMargin = 0;
            Vector3 position = Vector3.Zero;
            bool hasCell;
            if (avatarClientId == 0)
            {
                // 主机本机玩家不是联机化身（m_avatars 里没有它的键）：用同一份"本机玩家位置"
                // 口径（ComponentBody.Position），不按区块中心猜。观察者端看得到它才需要它的背包镜像。
                PlayerPositionSnapshot local = default;
                hasCell = m_avatarLink != null &&
                    m_avatarLink.TryCaptureLocalPlayerPosition(0, out local, out _);
                if (hasCell) position = local.Position;
            }
            else
            {
                hasCell = m_avatarLink != null &&
                    m_avatarLink.TryGetAvatarCell(avatarClientId, out position, out _);
            }
            avatarCellKnown = hasCell;
            int chunkX = hasCell ? EntityInterestWindowDiff.ChunkOf(position.X) : 0;
            int chunkZ = hasCell ? EntityInterestWindowDiff.ChunkOf(position.Z) : 0;
            PeerInfo[] peers = m_server.ListPeers();
            int sent = 0;
            for (int i = 0; i < peers.Length; i++)
            {
                PeerInfo peer = peers[i];
                if (!peer.Alive || peer.Kind != ClientKind.Player) continue;
                if (peer.State != ChannelState.Connected &&
                    peer.State != ChannelState.Backpressured) continue;
                if (peer.Id == avatarClientId)
                {
                    // 自己的状态必须到：不依赖它是否已上报窗口。
                    if (m_server.TrySend(peer.Id, type, payload)) sent++;
                    continue;
                }
                if (!hasCell) continue;   // 位置未知：观察者一个都不发（绝不改发全体）
                if (!TryGetClientRange(peer.Id, out int centerX, out int centerZ, out int radius))
                    continue;
                candidates++;
                if (!EntityInterestWindowDiff.InWindow(chunkX, chunkZ, centerX, centerZ,
                        radius + chunkMargin)) continue;
                if (!m_server.TrySend(peer.Id, type, payload)) continue;
                sent++;
            }
            return sent;
        }

        public bool IsHosting => m_server != null;

        bool IPeerBroadcastPort.IsConnected => ChannelConnected;

        /// <summary>主机：某客户端当前兴趣窗口（区块中心 + 半径）；编辑请求距离校验的唯一依据。</summary>
        public bool TryGetClientRange(int clientId, out int centerChunkX, out int centerChunkZ,
            out int radiusChunks)
        {
            centerChunkX = 0;
            centerChunkZ = 0;
            radiusChunks = 0;
            if (m_host == null) return false;
            if (!m_host.TryGetClientRange(clientId, out ChunkKey center, out int radius)) return false;
            centerChunkX = center.X;
            centerChunkZ = center.Z;
            radiusChunks = radius;
            return true;
        }

        /// <summary>
        /// 主机：该连接在 Hello 里申报的"我已有角色记录 hash"清单。
        /// 数据源是这次连接的握手内容（<see cref="TerrainSyncHost.CaptureKnownRecordHashes"/>）。
        /// </summary>
        public int CaptureKnownRecordHashes(int clientId, List<RecordHashRef> target) =>
            m_host != null ? m_host.CaptureKnownRecordHashes(clientId, target) : 0;

        /// <summary>主机：该连接是否已持有该记录键的当前 hash（true = 这条记录不必重发）。</summary>
        public bool TryGetKnownRecordHash(int clientId, string recordKey, out ulong hash)
        {
            if (m_host != null) return m_host.TryGetKnownRecordHash(clientId, recordKey, out hash);
            hash = 0UL;
            return false;
        }

        /// <summary>客户端：编辑请求超时 → 把该块排进强制核对（主机比对不一致就回传整块）。</summary>
        public bool RequestReverification(int chunkX, int chunkZ)
        {
            if (m_clientSync == null) return false;
            return m_clientSync.RequestChunkReverification(new ChunkKey(chunkX, chunkZ));
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            WireShell();
            // 主线程泵：派发入站消息 + 产生区块加载/卸载事件。
            // 放在 tick（而不是 SuSubsystemTerrain.Update）是为了**主菜单也能跑**——
            // 加入房间、握手都在菜单阶段发生，那时没有 Project、没有子系统 Update。
            m_bridge.Pump(m_shell);
            // 架构决定：**主机唯一权威，客户端是副本**。角色一变就同步到桥与引擎外壳：
            //   · 客户端（m_client != null）：引擎侧停止所有会改地形的世界模拟，
            //     并把本地写入捕获成编辑请求（见 Engine/ClientReplicaSubsystems.cs）；
            //   · 主机 / 未联机：桥与外壳保持权威行为（下发 / 正常模拟）。
            bool replica = m_client != null;
            m_bridge.ClientReplica = replica;
            if (m_shell != null) m_shell.ClientReplica = replica;
            // 追帧 / 重连被拒绝时在回调里只**登记**断开请求（不在网络帧回调里 Dispose，
            // 避免在 TerrainSyncClient 自己的处理栈里拆掉它）：这里统一执行。
            if (m_clientDropRequested) DropClientConnection();
            UpdateInterest(tick.Now);
            if (m_host != null)
            {
                m_host.Tick();
            }
            // 客户端：这条连接**已经死了**（收/发线程失败 = Failed）就必须拆掉重连。
            // 关键：不能用"m_client 还在"当"连接还在"—— 实机故障里对端掉线后 m_client 一直非 null，
            // 于是重连分支（要求 m_client == null）永远不跑，位置快照一直往死连接里塞、
            // 主机侧的联机角色停在最后一个已应用的位置上一动不动（且两端都不写任何日志）。
            if (m_client != null && !m_client.IsAlive)
            {
                string deadReason = m_client.State + "（" + m_client.StateDetail +
                    "，已收 " + m_client.Stats.FramesReceived + " 帧）";
                LogWarning("与主机的通道已失效：" + deadReason + " → 拆掉这条连接，按 5s 间隔重连" +
                    "（重连成功后主机会重新建角色，位置快照立刻恢复；失败原因已如实记在这里，不再静默）");
                // 断线瞬间把"上次应用序号 / 最近主机会话"留在模块里：新 TerrainSyncClient 不会从 0 重来，
                // 重连 Hello 才能声明真实水位（否则主机会把整段历史都重放一遍）。
                m_clientChangeSequence = m_clientSync?.LastAppliedChangeSequence ?? m_clientChangeSequence;
                if (m_clientSync != null && !string.IsNullOrEmpty(m_clientSync.HostSessionId))
                    m_lastHostSessionId = m_clientSync.HostSessionId;
                try { m_client.FrameReceived -= m_bridge.PostFrame; } catch (Exception) { }
                try { m_clientSync?.Saturated -= HandleClientQueueSaturated; } catch (Exception) { }
                try { m_clientSync?.DecodeFailed -= HandleClientDecodeFailed; } catch (Exception) { }
                try { m_clientSync?.ClientChunkSetFailed -= HandleClientChunkSetFailed; } catch (Exception) { }
                try { m_clientSync?.RejoinRejected -= HandleClientRejoinRejected; } catch (Exception) { }
                try { m_clientSync?.CatchUpRejected -= HandleClientCatchUpRejected; } catch (Exception) { }
                try { m_clientSync?.CatchUpTrace -= HandleClientCatchUpTrace; } catch (Exception) { }
                try { m_clientSync?.CatchUpProblem -= HandleClientCatchUpProblem; } catch (Exception) { }
                try { m_clientSync?.ResidentRequeue -= HandleClientResidentRequeue; } catch (Exception) { }
                try { m_clientSync?.RepeatedMismatch -= HandleClientRepeatedMismatch; } catch (Exception) { }
                try { m_clientSync?.VerifyNotStarted -= HandleClientVerifyNotStarted; } catch (Exception) { }
                try { m_client.Dispose(); } catch (Exception) { }
                m_client = null;
                m_clientSync = null;
                m_reconnectAttempts = 0;
                // 立刻尝试一次重连（失败时下面的重连分支按 5s 间隔继续试）：
                // 恢复越快，主机侧那个角色"停住"的时间越短。
                m_nextReconnectMs = (long)(tick.Now * 1000.0);
            }
            if (m_wantClient && m_client == null && (long)(tick.Now * 1000.0) >= m_nextReconnectMs)
            {
                // 玩家已经在加入流程里：主机可能比客户端晚建好房间，按固定间隔重连，
                // 直到连上或玩家离开（这不是"配置兜底"，是加入流程本身的重试）。
                m_nextReconnectMs = (long)(tick.Now * 1000.0) + 5000;
                m_reconnectAttempts++;
                if (StartClientOnce(out string reason))
                    Log("重连成功（第 " + m_reconnectAttempts + " 次尝试）→ " + m_wantHost + ":" + m_wantPort);
                else if (m_reconnectAttempts == 1 || m_reconnectAttempts % 6 == 0)
                    Log("重连失败（第 " + m_reconnectAttempts + " 次）：" + reason);
            }
            if (m_clientSync != null)
            {
                m_clientSync.Tick();
                // 跨连接保存"上次应用序号 + 最近主机会话"：通道断掉时新客户端不会从 0 重来。
                m_clientChangeSequence = m_clientSync.LastAppliedChangeSequence;
                if (!string.IsNullOrEmpty(m_clientSync.HostSessionId))
                    m_lastHostSessionId = m_clientSync.HostSessionId;
                // 兴趣上报（含"握手前重发"）统一在 UpdateInterest 里做，这里不再另发一份。
            }
            UpdatePlayerPositionSync(tick);
            Report(tick.Now);
        }

        // ------------------------------------------------------------ 玩家位置同步（32Hz）

        /// <summary>
        /// 按旧实现的 32Hz 相位推进位置同步：
        ///   · 主机：把"本机玩家 + 每个联机角色"折成一批快照 → 广播给每个玩家端
        ///     —— Source: ScMultiplayerPlayerSync.cs:38-101（SendGamePlayerPositionMessage）
        ///   · 客户端：把自己本机玩家的一份快照 → 发给主机
        ///     —— Source: ScMultiplayerPlayerSync.cs:42-45（!IsHost → SendGamePlayerInputMessage）
        /// 位置同步是"可替换的最新状态"：定时器到点但拿不到快照时**不补发旧值**，
        /// 只记一条明确原因（拿不到本机玩家/化身没建好），下一拍再试。
        /// 一帧最多**发一份**（旧实现按拍各发一份，但同一渲染帧内角色状态不变，发两份是纯冗余流量）；
        /// 60fps 下相位每两帧跨一次 = 有效约 30Hz，与旧的 32Hz 同量级。
        /// </summary>
        private void UpdatePlayerPositionSync(in ModuleTickContext tick)
        {
            bool host = m_host != null;
            bool client = m_clientSync != null;
            if (!host && !client) return;             // 通道没开：没有可同步的对象
            if (m_avatarLink == null)
            {
                LogOnce("position-no-avatar-link",
                    "没有接上联机角色视图（IAvatarTerrainLink）→ 不发送玩家位置快照" +
                    "（联机角色模块没上线时，主机与客户端都不会看到对方移动）");
                return;
            }
            // 32Hz 相位：按真实时间攒，且一帧最多补 4 拍（旧实现 MaxSyncPulsesPerUpdate = 4）。
            double now = tick.Now;
            if (m_lastPositionPulseTime <= 0.0) m_lastPositionPulseTime = now;
            double elapsed = now - m_lastPositionPulseTime;
            m_lastPositionPulseTime = now;
            if (elapsed < 0.0) elapsed = 0.0;
            m_positionPulseAccumulator += (float)elapsed;
            int pulses = 0;
            while (m_positionPulseAccumulator >= PositionPulseDuration && pulses < MaxPositionPulsesPerFrame)
            {
                m_positionPulseAccumulator -= PositionPulseDuration;
                pulses++;
            }
            if (pulses == 0) return;
            // 落后过多时不要无限追赶（旧实现同样只跑有限拍）：把多余的时间丢掉并说明。
            if (m_positionPulseAccumulator > PositionPulseDuration * MaxPositionPulsesPerFrame)
            {
                m_positionPulseAccumulator = 0f;
                LogOnce("position-pulse-behind",
                    "位置同步相位落后超过 " + MaxPositionPulsesPerFrame + " 拍（单帧卡顿）→ 丢弃积压的相位，" +
                    "不补发陈旧位置（旧实现每帧同样只跑有限个脉冲）");
            }

            if (host) SendHostPlayerPositions();
            else SendClientPlayerPosition();
        }

        /// <summary>主机 → 客户端：一批（本机玩家 + 每个联机角色）位置快照。</summary>
        private void SendHostPlayerPositions()
        {
            m_positionPulseIndex++;
            var batch = new List<PlayerPositionEntry>(4);
            int count = m_avatarLink.CapturePlayerPositionBatch(m_positionPulseIndex, batch, out string reason);
            if (count <= 0)
            {
                LogOnce("position-host-empty",
                    "主机这一拍没有可发送的位置快照（" + (reason ?? "没有角色") + "）→ 不发空批" +
                    "（客户端会保持上一份权威位置，而不是被拉到某个猜测点）");
                return;
            }
            // 多本机角色：按**接收端**构建负载。旧构建（从没上报过本机槽位）只收槽位 0，
            // 单角色房间的负载与旧版逐字节一致；新构建收全部槽位。
            //
            // 逐端裁剪（用户口径）：客户端 A 只加载自己周围那片地形，所以只要 B（另一个客户端）
            // 在 A 的兴趣窗口内就该继续同步位置 —— 即使被墙 / 树完全遮挡、看不见人也一样：
            // 位置是"下一拍还有"的最新状态，客户端必须能在他进入视野的瞬间直接渲染，不能等他
            // 绕过遮挡物再开始同步（那会出现人从墙后"瞬移"出来）。
            // 窗口外扩 1 环（16 m）作为进入缓冲：贴着窗口边缘、刚跨进可视范围的化身也有位置。
            // 客户端自己的角色永远包含（它要用权威位置做对账，即使还没上报窗口）。
            int[] peers = m_host.GetPlayerClientIds();
            int sent = 0;
            int windowUnknownPeers = 0;
            int entriesFiltered = 0;
            for (int i = 0; i < peers.Length; i++)
            {
                int peer = peers[i];
                bool aware = m_avatarLink.PeerAnnouncedLocalSlots(peer);
                bool hasWindow = TryGetClientRange(peer, out int windowX, out int windowZ,
                    out int windowRadius);
                if (!hasWindow)
                {
                    windowUnknownPeers++;
                    LogOnce("position-window-unknown:" + peer,
                        "客户端 " + peer + " 还没上报兴趣窗口 → 这一拍只发它自己的角色位置" +
                        "（不发别的化身；不会退回「发给所有人」）");
                }
                int peerCount = 0;
                for (int entry = 0; entry < batch.Count; entry++)
                {
                    if (batch[entry].Slot != 0 && !aware) continue;
                    if (!hasWindow && batch[entry].Key.ClientId != peer) continue;
                    peerCount++;
                }
                if (peerCount == 0) continue;
                var peerBatch = new List<PlayerPositionEntry>(peerCount);
                for (int entry = 0; entry < batch.Count; entry++)
                {
                    if (batch[entry].Slot != 0 && !aware) continue;
                    if (batch[entry].Key.ClientId == peer)
                    {
                        peerBatch.Add(batch[entry]);   // 自己的位置：永远发
                        continue;
                    }
                    if (!hasWindow)
                    {
                        entriesFiltered++;             // 窗口未知：只发自己的，绝不猜
                        continue;
                    }
                    Vector3 position = batch[entry].Snapshot.Position;
                    if (!EntityInterestWindowDiff.InWindow(
                            EntityInterestWindowDiff.ChunkOf(position.X),
                            EntityInterestWindowDiff.ChunkOf(position.Z),
                            windowX, windowZ, windowRadius + 1))
                    {
                        entriesFiltered++;
                        continue;
                    }
                    peerBatch.Add(batch[entry]);
                }
                if (peerBatch.Count == 0) continue;
                byte[] payload = PlayerPositionCodec.EncodeBatch(peerBatch, out int dropped);
                if (dropped > 0)
                {
                    LogOnce("position-batch-overflow:" + peer,
                        "位置快照批超过 " + PlayerPositionCodec.MaxBatchEntries + " 条 → 已丢弃 " + dropped +
                        " 条（发往 clientId=" + peer + "；上限已按 4 本机角色 × 5 端放宽到 " +
                        PlayerPositionCodec.MaxBatchEntries + "）");
                }
                if (m_host.SendPlayerPositionsTo(peer, payload)) sent++;
            }
            m_positionSnapshotsSent += sent;
            if (entriesFiltered > 0) m_positionEntriesOutOfWindow += entriesFiltered;
            if (windowUnknownPeers > 0) m_positionWindowUnknownPeers += windowUnknownPeers;
            if (sent < peers.Length)
            {
                // 背压/未就绪 = 显式记录（位置是"下一拍还有"的最新状态，不回滚、不假装发成功）。
                LogOnce("position-host-partial",
                    "位置快照批只发到 " + sent + "/" + peers.Length + " 个客户端" +
                    "（通道背压或对端未就绪；不缓存旧位置，下一拍重发）");
            }
        }

        /// <summary>客户端 → 主机：**每个本机角色**一份位置快照（单角色时与旧版单条消息一致）。</summary>
        private void SendClientPlayerPosition()
        {
            m_positionPulseIndex++;
            var entries = new List<PlayerPositionEntry>(PlayerKey.SlotCount);
            int count = m_avatarLink.CaptureLocalPlayerPositions(m_positionPulseIndex, entries,
                out string reason);
            if (count <= 0)
            {
                LogOnce("position-client-none",
                    "本拍取不到本机玩家的位置快照（" + reason + "）→ 不发送" +
                    "（绝不猜一个位置：主机会因此把客户端周围的地形更新位置摆到别处）");
                return;
            }
            // 单角色（槽位 0）继续走旧的单条消息：旧主机也认；多角色用批（主机按来源重映射槽位）。
            byte[] payload = count == 1 && entries[0].Slot == 0
                ? PlayerPositionCodec.EncodeSingle(entries[0].Key, entries[0].Snapshot)
                : PlayerPositionCodec.EncodeBatch(entries, out _);
            if (!m_clientSync.SendPlayerPosition(payload))
            {
                // 每一拍都要**计数**（不是只写一行）：状态行的 posDropped 必须如实反映
                // "这份快照没能发给主机"。实机故障里通道已经死了而 posDropped 一直是 0、
                // 日志只有很早以前的一条，看起来像"一切正常但主机不动"。
                m_positionSnapshotsDropped++;
                LogOnce("position-client-send-failed",
                    "位置快照发送失败（通道未连接或发送队列已满 = 显式背压）→ 不重发陈旧位置，下一拍重试；" +
                    "每一拍都计入 posDropped（通道失效时由 tick 的看门狗拆链重连）");
                return;
            }
            m_positionSnapshotsSent++;
        }

        /// <summary>
        /// 主机收到某个玩家端的本机玩家位置；客户端收到主机的角色位置批。
        /// 解码失败**必须显式失败**（丢弃 + 写清原因），绝不拿默认值当位置用。
        ///
        /// 参数类型是 <see cref="PP.PlayerPositions"/>（wrapper）：<c>MessageType.PlayerPosition</c>
        /// 的线上负载就是 <c>PlayerPositions{entries:[1]}</c>（见 <see cref="RegisterModuleMessages"/>
        /// 的注释与 <c>PlayerPositionCodec.EncodeSingle</c>）。单条消息必须**恰好一条**条目：
        /// 0 条 / 多条都是协议违规，整条丢弃并计数（不半条应用）。
        /// </summary>
        private void HandlePlayerPositionMessage(MessageContext context, PP.PlayerPositions message)
        {
            if (m_avatarLink == null)
            {
                LogOnce("position-no-avatar-link-recv",
                    "收到 clientId=" + context.ClientId + " 的位置快照，但没有接上联机角色视图 → 丢弃" +
                    "（没有网络化身表就无法确定该把它应用到谁身上，不猜）");
                return;
            }
            if (message.Entries.Count != 1)
            {
                LogOnce("position-decode-single:" + context.ClientId,
                    "clientId=" + context.ClientId + " 的位置快照必须恰好 1 条条目（本次 " +
                    message.Entries.Count + " 条）→ 丢弃（单条消息不半条应用）");
                m_positionSnapshotsDropped++;
                return;
            }
            if (!PlayerPositionCodec.TryConvert(message.Entries[0], out PlayerKey key,
                out PlayerPositionSnapshot snapshot, out string error))
            {
                LogOnce("position-decode-single:" + context.ClientId,
                    "clientId=" + context.ClientId + " 的位置快照解码失败：" + error + " → 丢弃");
                m_positionSnapshotsDropped++;
                return;
            }
            if (!snapshot.IsFinite)
            {
                LogOnce("position-nonfinite:" + context.ClientId,
                    "clientId=" + context.ClientId + " 的位置快照含 NaN/Inf → 丢弃" +
                    "（旧实现同样在 !readOnlyState.IsFinite 时直接 return）");
                m_positionSnapshotsDropped++;
                return;
            }
            if (key.ClientId != 0 && key.ClientId != context.ClientId)
            {
                // 旧实现在主机侧校验 msg.PlayerIndex 必须等于来源 clientId（或 0）：
                // 载荷里的编号不能冒领另一个玩家端的身份。
                LogOnce("position-mismatch:" + context.ClientId,
                    "clientId=" + context.ClientId + " 的位置快照自称玩家键 " + key +
                    " → 丢弃（旧实现要求 clientId 一致或为 0）");
                m_positionSnapshotsDropped++;
                return;
            }
            // 主机侧：槽位属于来源连接（客户端填 0 是该端自己的槽位，不是主机）。
            PlayerKey sourceKey = new PlayerKey(context.ClientId, key.Slot);
            m_positionSnapshotsApplied++;
            m_avatarLink.OnRemotePlayerPositionReceived(sourceKey, in snapshot);
        }

        private void HandlePlayerPositionsMessage(MessageContext context, PP.PlayerPositions message)
        {
            if (m_avatarLink == null)
            {
                LogOnce("position-no-avatar-link-recv",
                    "收到位置快照批，但没有接上联机角色视图 → 整批丢弃（不猜）");
                return;
            }
            if (message.Entries.Count > PlayerPositionCodec.MaxBatchEntries)
            {
                LogOnce("position-decode-batch",
                    "位置快照批条数非法：" + message.Entries.Count + "（上限 " +
                    PlayerPositionCodec.MaxBatchEntries + "）→ 整批丢弃（不半批应用）");
                m_positionSnapshotsDropped++;
                return;
            }
            bool hosting = m_host != null;
            var decoded = new List<PlayerPositionEntry>(message.Entries.Count);
            for (int i = 0; i < message.Entries.Count; i++)
            {
                if (!PlayerPositionCodec.TryConvert(message.Entries[i], out PlayerKey key,
                        out PlayerPositionSnapshot snapshot, out string error))
                {
                    LogOnce("position-decode-batch-item:" + i,
                        "位置快照批第 " + (i + 1) + " 条解码失败：" + error + " → 整批丢弃");
                    m_positionSnapshotsDropped++;
                    return;
                }
                if (hosting)
                {
                    // 主机收到客户端的多本机角色批：载荷里的 clientId 只能是 0（= 来源端自己）或来源端编号。
                    if (key.ClientId != 0 && key.ClientId != context.ClientId)
                    {
                        LogOnce("position-batch-spoof:" + context.ClientId,
                            "clientId=" + context.ClientId + " 的位置快照批里第 " + (i + 1) +
                            " 条自称玩家键 " + key + " → 整批丢弃（不能冒领别的玩家端）");
                        m_positionSnapshotsDropped++;
                        return;
                    }
                    key = new PlayerKey(context.ClientId, key.Slot);
                }
                decoded.Add(new PlayerPositionEntry(key, snapshot));
            }
            for (int i = 0; i < decoded.Count; i++)
            {
                PlayerKey key = decoded[i].Key;
                PlayerPositionSnapshot snapshot = decoded[i].Snapshot;
                // 客户端：批里"本机玩家自己那一条"（编号 = 主机在 HelloAck 里给本端的 clientId）。
                // 旧实现走 ApplyAuthoritativeLocalPlayerState（受击击退 + 背包权威），尚未移植 →
                // 明确跳过并计数，绝不把它当远端角色应用（否则会去动本机玩家）。
                if (!hosting && key.ClientId == ClientLocalClientId)
                {
                    m_avatarLink.NotePositionSkippedOwn(key);
                    continue;
                }
                if (!snapshot.IsFinite)
                {
                    LogOnce("position-batch-nonfinite:" + key,
                        "位置快照批里玩家键 " + key + " 含 NaN/Inf → 跳过该条");
                    m_positionSnapshotsDropped++;
                    continue;
                }
                m_positionSnapshotsApplied++;
                m_avatarLink.OnRemotePlayerPositionReceived(key, in snapshot);
            }
        }

        /// <summary>
        /// 客户端的兴趣上报。
        ///
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:481-494
        ///   · 中心 = **本机玩家**所在区块（旧规则：PlayersData 里不是网络化身的那一条；
        ///     不是"第一个有 GameWidget 的角色"）；
        ///   · 半径 = <c>GetTerrainInterestRadius(SubsystemSky.VisibilityRange ?? 64f)</c>
        ///     （旧 :486-487 的 <c>clientRadius</c> → 旧 :530-543 <c>SendClientTerrainInterestUpdate</c>）。
        /// 旧实现按半径去重、中心由主机的权威位置决定；本重写版的通道把中心一起带上，
        /// 所以按 (中心, 半径) 去重 —— 客户端走到哪，主机记录的窗口中心就走到哪。
        /// </summary>
        private void UpdateInterest(double now)
        {
            if (m_clientSync == null) return;       // 主机侧不上报（本机窗口由 TerrainSyncHost 自己维护）
            var project = Game.GameManager.Project;
            if (project == null) return;            // 还没进世界：没有本机玩家可报（不猜中心）
            if (!TryGetLocalPlayerChunk(out ChunkKey center, out string reason))
            {
                LogOnce("interest-no-local-player",
                    "无法确定本机玩家：" + reason + " → 不上报兴趣中心（绝不拿别的角色或默认块顶替）");
                return;
            }
            if (m_avatarLink == null)
            {
                LogOnce("interest-no-avatar-link",
                    "没有接上联机角色视图（IAvatarTerrainLink）→ 算不出本机兴趣半径，不上报兴趣");
                return;
            }
            float visibility = project.FindSubsystem<Game.SubsystemSky>(false)?.VisibilityRange ?? 64f;
            // 半径 = max(1, ceil(visibility / 16))（夹到 1..32）：与客户端实际可见范围**同一个口径**，
            // 见 TerrainSyncRange.RadiusForVisibility（唯一实现；主机侧 ApplyTerrainUpdateLocations
            // 也按这个半径算可见范围 / 内容距离）。这样外层可见环不会长期停在"已请求但没内容"。
            int radius = m_avatarLink.GetTerrainInterestRadius(visibility);
            bool radiusChanged = !m_hasCenter || radius != m_lastRadius;
            if (!m_hasCenter || !center.Equals(m_lastCenter) || radiusChanged)
            {
                if (radiusChanged) m_interestRadiusChanges++;
                m_lastCenter = center;
                m_lastRadius = radius;
                m_hasCenter = true;
                m_lastInterestSeconds = now;
                SendInterest(center, radius, visibility);
                return;
            }
            // 握手前发出的帧可能落空：还没握手就按 0.5 秒重发（握手后只在上面的变化时发）。
            if (!m_clientSync.Handshaken && now - m_lastInterestSeconds >= 0.5)
            {
                m_lastInterestSeconds = now;
                SendInterest(center, radius, visibility);
            }
        }

        /// <summary>
        /// 发出 InterestUpdate 并记下**实际发出的值**（半径 / 可见范围）+ 限流日志。
        /// 计数器：<see cref="m_interestSends"/>（条数）/ <see cref="m_interestRadiusChanges"/>（半径变化次数）；
        /// 日志每 5 秒最多一行（半径不变时只可能在握手前 0.5 秒重发，不会刷屏）。
        /// </summary>
        private void SendInterest(ChunkKey center, int radius, float visibility)
        {
            m_clientSync.SendInterest(center, radius);
            m_interestSends++;
            m_interestRadiusSent = radius;
            m_interestVisibilitySent = visibility;
            LogRateLimited("interest-radius",
                "上报兴趣：半径 " + radius + " 区块（可见范围 " + visibility +
                "；半径 = max(1, ceil(可见范围 / 16))）· 中心 " + center +
                " · 半径变化次数 " + m_interestRadiusChanges + " · 累计上报 " + m_interestSends + " 条",
                5000);
        }

        private bool TryGetLocalPlayerChunk(out ChunkKey key, out string reason)
        {
            key = default;
            if (!TryGetLocalPlayerCell(out int x, out int _, out int z, out reason)) return false;
            key = new ChunkKey(x >> 4, z >> 4);
            return true;
        }

        /// <summary>
        /// 本机玩家所在的**绝对格子坐标**（网页地图要把角色放在正中间，所以需要它）。
        ///
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:481-484
        ///   旧规则：本机玩家 = <c>PlayersData</c> 里**不是网络化身**的那一条
        ///   （<c>!m_networkPlayerData.Values.Contains(item)</c>）。
        /// 拿不到就返回 false 并写清原因 —— **不**退回"第一个有 GameWidget 的角色"那种扫描
        /// （拿错人就会把兴趣中心 / 地图中心报到别人的位置上）。
        /// </summary>
        private bool TryGetLocalPlayerCell(out int cellX, out int cellY, out int cellZ, out string reason)
        {
            cellX = 0;
            cellY = 0;
            cellZ = 0;
            reason = null;
            if (m_avatarLink == null)
            {
                reason = "没有接上联机角色视图（IAvatarTerrainLink），无法区分\"网络化身\"与\"本机玩家\"";
                return false;
            }
            try
            {
                var players = Game.GameManager.Project?.FindSubsystem<Game.SubsystemPlayers>(false);
                if (players == null)
                {
                    reason = "还没有加载中的 Project / SubsystemPlayers（还没进世界）";
                    return false;
                }
                Game.PlayerData localPlayer = null;
                var list = players.PlayersData;
                for (int i = 0; i < list.Count; i++)
                {
                    if (m_avatarLink.IsNetworkAvatar(list[i])) continue;
                    localPlayer = list[i];
                    break;
                }
                if (localPlayer == null)
                {
                    reason = "PlayersData 里除网络化身以外没有任何角色";
                    return false;
                }
                var body = localPlayer.ComponentPlayer?.ComponentBody;
                if (body == null)
                {
                    reason = "本机玩家（PlayersData 里不是网络化身的那一条）还没有 ComponentBody";
                    return false;
                }
                Vector3 position = body.Position;
                cellX = (int)Math.Floor(position.X);
                cellY = (int)Math.Floor(position.Y);
                cellZ = (int)Math.Floor(position.Z);
                return true;
            }
            catch (Exception e)
            {
                reason = "读本机玩家位置时异常：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 把桥注入到引擎替换实例上（**不用静态**）。
        /// 引擎 new 出 SuSubsystemTerrain，模块在自己的 tick 里找它并注入依赖；
        /// 注入前外壳是纯原生行为，注入后才开始同步。
        /// </summary>
        private void WireShell()
        {
            Project project = Game.GameManager.Project;
            if (m_shell != null && ReferenceEquals(m_shellProject, project)) return;
            // Project 换了（例如主机重启后客户端**整图重下**、GameLoading 重新加载世界）：
            // 旧外壳已经随旧 Project 销毁，必须解除旧注入并重新找新外壳 —— 否则新世界的地形永远不同步。
            if (m_shell != null)
            {
                try { m_shell.Bridge = null; } catch (Exception) { }
                m_shell = null;
                m_shellMissingLogged = false;
            }
            m_shellProject = project;
            if (project == null) return;
            // 世界激活（首次进入 / 整图重下完成 / 主机重启后重新加载）：引擎地形整体换了一份，
            // 客户端旧世界的摘要状态与排队中的核对工作**全部作废**，必须清掉并重新排队全部常驻区块
            // —— 否则同一块在旧世界里的 Fresh 结论会被拿来给新世界背书（等于伪造匹配）。
            // 放在 Pump 之前置位、由同一个 Tick 末尾的 TryRequeueResidentChunks 在新常驻集合上执行。
            if (m_clientSync != null)
                m_clientSync.OnClientWorldReplaced("世界已激活 / Project 换新：旧世界的核对状态作废，重新排队常驻区块");
            try
            {
                var subsystem = project.FindSubsystem<Game.SubsystemTerrain>(false);
                m_shell = subsystem as SuSubsystemTerrain;
                if (m_shell == null)
                {
                    if (!m_shellMissingLogged)
                    {
                        m_shellMissingLogged = true;
                        Log("未找到注入后的 SubsystemTerrain 外壳（注入可能未生效），暂不同步地形");
                    }
                    return;
                }
                m_shell.Bridge = m_bridge;
                m_shell.ClientReplica = m_client != null;
                m_bridge.ClientReplica = m_client != null;
                Log("地形桥已注入引擎外壳（无静态引用）：SubsystemTerrain → " + m_shell.GetType().Name +
                    " · 角色=" + (m_client != null ? "client-replica" : "authority"));
            }
            catch (Exception e)
            {
                Log("注入地形桥失败：" + e.GetType().Name + " " + e.Message);
            }
        }

        private void HandleFrame(ReceivedFrame frame)
        {
            // 所有入站消息统一走注册表（强类型解析 + 各模块的处理器）。
            m_messages.Dispatch(in frame);
            // 与旧行为一致：实时地图请求 / 位置快照不再转发给原始订阅者；
            // 其余帧照旧转发（世界传输等自检 / 外部观察者用）。
            if (frame.Type == MessageType.MapRequest || frame.Type == MessageType.PlayerPosition ||
                frame.Type == MessageType.PlayerPositions)
            {
                return;
            }
            m_frameArrived?.Invoke(frame);
        }

        // ------------------------------------------------------------ 实时地图（只读）

        private List<byte[]> m_liveMapBatches;
        private string m_liveMapKey = string.Empty;
        private long m_liveMapBuiltMs;
        private int m_liveMapSnapshotId;
        /// <summary>
        /// 主机端动物位置来源（v9；组合根注册 <see cref="IHostCreatureCellSource"/>）。
        /// 缺失时 animals[] 为空并明确写一行原因 —— 绝不拿客户端复制体顶替。
        /// </summary>
        private IHostCreatureCellSource m_creatureCells;
        /// <summary>装配动物清单时复用的临时列表（每次快照清空；不制造持续垃圾）。</summary>
        private readonly List<LiveMapAnimal> m_liveMapAnimalScratch = new List<LiveMapAnimal>();
        /// <summary>装配逐角色槽位清单时复用的临时列表（v10；每次快照清空）。</summary>
        private readonly List<LiveMapPlayer> m_liveMapPlayerScratch = new List<LiveMapPlayer>();
        /// <summary>上一份快照的槽位键签名（槽位增减时写一行明确日志，不刷屏）。</summary>
        private string m_liveMapPlayerSignature = string.Empty;
        /// <summary>最近一次实时地图快照实际发出的动物数（= animalsInFeed）。</summary>
        private long m_animalsInFeed;
        /// <summary>累计被上限截掉的动物数（每次截断都计数；日志限流，绝不静默丢）。</summary>
        private long m_animalsTruncated;
        /// <summary>限流日志的 key → 上次写入毫秒（Time.RealTime 口径）。</summary>
        private readonly Dictionary<string, long> m_lastRateLimitedLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>
        /// 把**真实地形**回给调试端：
        ///   · <c>all=true</c> → 主机引擎当前加载的**整片区域**（用户要"整个地图区域都画进去"）；
        ///   · <c>all=false</c> → 角色（或指定块）周围的窗口。
        /// 只放真实存在的块 —— 没加载的格子网页就不会填色。
        /// 整片可能 200+ 块，一帧装不下（60 KiB），所以按批发（同一 snapshotId）。
        /// </summary>
        private void SendLiveMap(int clientId, int radius, bool all, bool hasRequestedCenter,
            int requestedChunkX, int requestedChunkZ)
        {
            if (m_server == null) return;
            radius = LiveMapCodec.ClampRadius(radius <= 0 ? 2 : radius);
            long nowMs = (long)(Time.RealTime * 1000.0);

            bool hasCell = TryGetLocalPlayerCell(out int cellX, out int cellY, out int cellZ,
                out string cellReason);
            int centerChunkX, centerChunkZ;
            if (hasRequestedCenter)
            {
                centerChunkX = requestedChunkX;
                centerChunkZ = requestedChunkZ;
            }
            else if (hasCell)
            {
                centerChunkX = cellX >> 4;
                centerChunkZ = cellZ >> 4;
            }
            else
            {
                // 明确写清原因：请求里没带中心、本机玩家也定位不到时窗口中心才落 (0,0)。
                LogOnce("livemap-no-local-player",
                    "实时地图：无法确定本机玩家（" + cellReason +
                    "）→ 未指定区块时窗口中心按 (0,0) 回（不拿别的角色顶替）");
                centerChunkX = 0;
                centerChunkZ = 0;
            }

            // 同样的请求 700 ms 内复用上一份（构建要读几万列，不能每帧都做）
            string key = (all ? "all" : centerChunkX + ":" + centerChunkZ + ":" + radius) + ":" +
                         m_bridge.ResidentCount;
            if (m_liveMapBatches == null || key != m_liveMapKey || nowMs - m_liveMapBuiltMs >= 700)
            {
                m_liveMapKey = key;
                m_liveMapBuiltMs = nowMs;
                m_liveMapSnapshotId++;
                if (m_liveMapSnapshotId <= 0) m_liveMapSnapshotId = 1;

                long started = nowMs;
                LiveMapSnapshot snapshot = BuildLiveMap(all, hasCell, cellX, cellY, cellZ,
                    centerChunkX, centerChunkZ, radius);
                List<byte[]> batches;
                try
                {
                    batches = LiveMapCodec.EncodeBatches(snapshot, m_liveMapSnapshotId);
                }
                catch (Exception e)
                {
                    // 明确失败：绝不下发被截断的"客户端已加载区域"（那会让网页画出错误的区域）。
                    m_liveMapBatches = null;
                    m_liveMapKey = string.Empty;
                    LogOnce("livemap-encode-failed:" + e.GetType().Name,
                        "实时地图快照编码失败（本次不发，等下次重试；绝不发送截断的数据）：" +
                        e.GetType().Name + " " + e.Message);
                    return;
                }
                for (int i = 0; i < batches.Count; i++)
                {
                    if (batches[i].Length <= TerrainFrame.MaxPayloadBytes) continue;
                    // 单帧超限：同样明确失败，不做任何裁剪。
                    m_liveMapBatches = null;
                    m_liveMapKey = string.Empty;
                    LogOnce("livemap-batch-oversize",
                        "实时地图第 " + (i + 1) + "/" + batches.Count + " 批 " + batches[i].Length +
                        " 字节超过单帧上限 " + TerrainFrame.MaxPayloadBytes + "（本次不发；" +
                        "在线玩家端过多或已加载区块过多）");
                    return;
                }
                m_liveMapBatches = batches;
                if (m_verbose)
                {
                    // 欠更新标块（chunks[].n > 0）：本快照里有多少块要标注、各块客户端数之和是多少
                    int pendingMarks = 0, pendingClients = 0;
                    for (int i = 0; i < snapshot.Chunks.Count; i++)
                    {
                        if (snapshot.Chunks[i].PendingClients <= 0) continue;
                        pendingMarks++;
                        pendingClients += snapshot.Chunks[i].PendingClients;
                    }
                    Log("实时地图：" + (all ? "整片已加载区域" : "窗口 " + centerChunkX + "," + centerChunkZ +
                            " 半径 " + radius) +
                        " · 角色 " + (hasCell ? "(" + cellX + "," + cellY + "," + cellZ + ")" : "未知") +
                        " · " + snapshot.Chunks.Count + " 块 / " + m_liveMapBatches.Count + " 批" +
                        " · 玩家端 " + snapshot.Clients.Count + " 个 / 其当前内存区块条目 " +
                        snapshot.ClientChunks.Count + " 条" +
                        " · 角色槽位 " + snapshot.Players.Count + " 个" +
                        " · 欠更新标块 " + pendingMarks + "（客户端 " + pendingClients + "）" +
                        " · 构建 " + ((long)(Time.RealTime * 1000.0) - started) + " ms");
                }
            }

            for (int i = 0; i < m_liveMapBatches.Count; i++)
            {
                if (m_server.TrySend(clientId, MessageType.MapState, m_liveMapBatches[i])) continue;
                Log("实时地图回包发送失败（目标客户端 " + clientId + "，第 " + (i + 1) + "/" +
                    m_liveMapBatches.Count + " 批）");
                return;
            }
        }

        /// <summary>
        /// v9：把**主机自己的**动物位置装进快照（只读；客户端不贡献）。
        ///
        /// 排序 / 截断只走 <see cref="LiveMapAnimalFeed.Fill"/>（唯一算法）：按网络键升序、
        /// 上限 <see cref="LiveMapCodec.MaxAnimals"/>。截断**不是静默行为**：累计
        /// <see cref="m_animalsTruncated"/> 并限流写一行日志；读不到来源也如实写原因。
        /// </summary>
        private void BuildLiveMapAnimals(LiveMapSnapshot snapshot)
        {
            snapshot.Animals.Clear();
            m_animalsInFeed = 0;
            if (m_creatureCells == null) return;
            m_liveMapAnimalScratch.Clear();
            int captured = m_creatureCells.CaptureHostCreatureCells(m_liveMapAnimalScratch,
                out string reason);
            if (captured < 0)
            {
                LogRateLimited("livemap-animals-unavailable",
                    "实时地图：读不到主机端动物位置（" + reason + "）→ 本快照 animals=0（不猜、不拿客户端复制体顶替）",
                    5000);
                return;
            }
            int included = LiveMapAnimalFeed.Fill(snapshot.Animals, m_liveMapAnimalScratch,
                LiveMapCodec.MaxAnimals, out int dropped);
            m_animalsInFeed = included;
            if (dropped <= 0) return;
            m_animalsTruncated += dropped;
            LogRateLimited("livemap-animals-truncated",
                "实时地图动物清单被上限截断：主机 " + captured + " 只 → 只发前 " + included +
                " 只（上限 " + LiveMapCodec.MaxAnimals + "）；本次丢弃 " + dropped +
                "，累计 animalsTruncated=" + m_animalsTruncated +
                "（重建房间 / 会话结束后清零）", 5000);
        }

        /// <summary>
        /// 同一条"限流日志"：key 在 intervalMs 内只写一次（计数不丢；日志不刷屏）。
        /// </summary>
        private void LogRateLimited(string key, string message, int intervalMs)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastRateLimitedLogMs.TryGetValue(key, out long last) && nowMs - last < intervalMs) return;
            m_lastRateLimitedLogMs[key] = nowMs;
            Log(message);
        }

        /// <summary>
        /// v10：把**每个玩家角色槽位**装进快照（主机本机槽位 0..N-1 + 每个联机化身的 (clientId, slot)）。
        ///
        /// 数据源只有一处：<see cref="IAvatarTerrainLink.CaptureLiveMapPlayers"/>（它内部走
        /// <c>NetworkAvatarSpawner.TryCollectLocalPlayers</c> / 化身表 <c>Keys</c> / <c>TryGet</c>）。
        /// 身份稳定键（页面选角 / clients[] 匹配）由**地形通道自己的身份会话表**按 clientId 补上：
        /// 同一身份的所有槽位共用它，重连后 clientId 变了、键不变。
        ///
        /// 槽位增减（多开一个本机角色 / 一个玩家端重连）会在键集合变化时写一行日志；
        /// 缺实体 / ComponentBody / 名字 / 身份键都照实列条目并写明原因，绝不丢槽位、也绝不顶替。
        /// </summary>
        private void BuildLiveMapPlayers(LiveMapSnapshot snapshot)
        {
            snapshot.Players.Clear();
            if (m_avatarLink == null)
            {
                LogRateLimited("livemap-players-no-link", "实时地图：没有接上联机角色视图（IAvatarTerrainLink）→ " +
                    "本次不给逐角色槽位清单（不拿身份清单顶替）", 5000);
                return;
            }
            int captured = m_avatarLink.CaptureLiveMapPlayers(m_liveMapPlayerScratch, out string reason);
            if (captured == 0)
            {
                if (!string.IsNullOrEmpty(reason))
                {
                    LogRateLimited("livemap-players-unavailable", "实时地图：读不到角色槽位（" + reason +
                        "）→ 本快照 players=0（不猜、不拿区块中心顶替）", 5000);
                }
                m_liveMapPlayerSignature = string.Empty;
                return;
            }

            // 身份稳定键：按**当前** clientId 从主机身份会话表取；主机自己的角色固定 "host"。
            // 重连瞬间新旧连接交替时可能取不到 → 该条 stableKey 留空，工具/页面显式回退到 id，
            // 但**绝不自造一个身份键**（那会让两个身份在页面上合并）。
            Dictionary<int, string> stableByClient = null;
            if (m_host != null)
            {
                TerrainHostSnapshot hostSnapshot = m_host.GetSnapshot();
                stableByClient = new Dictionary<int, string>(hostSnapshot.Clients.Count);
                for (int i = 0; i < hostSnapshot.Clients.Count; i++)
                {
                    ClientView view = hostSnapshot.Clients[i];
                    if (view.Kind != ClientKind.Player) continue;
                    stableByClient[view.Id] = view.StableKey ?? string.Empty;
                }
            }

            var signature = new System.Text.StringBuilder();
            for (int i = 0; i < captured; i++)
            {
                LiveMapPlayer source = m_liveMapPlayerScratch[i];
                string stableKey;
                if (source.IsHost)
                {
                    stableKey = LiveMapCodec.HostKey;
                }
                else if (stableByClient != null &&
                         stableByClient.TryGetValue(source.ClientId, out string resolved) &&
                         !string.IsNullOrEmpty(resolved))
                {
                    stableKey = resolved;
                }
                else
                {
                    stableKey = string.Empty;
                    LogRateLimited("livemap-players-nostable:" + source.Key,
                        "实时地图：角色槽位 " + source.Key + " 的身份稳定键暂时取不到（重连/会话记录未就绪）→ " +
                        "本条 stableKey 留空（工具按 id 显式回退，不伪造身份）", 5000);
                }
                // 复制进快照：scratch 下一次快照会被清空/复用，快照必须持有自己的对象。
                snapshot.Players.Add(new LiveMapPlayer
                {
                    ClientId = source.ClientId,
                    Slot = source.Slot,
                    Key = source.Key,
                    StableKey = stableKey,
                    Name = source.Name,
                    IsHost = source.IsHost,
                    HasCell = source.HasCell,
                    CellX = source.CellX,
                    CellY = source.CellY,
                    CellZ = source.CellZ,
                    CellReason = source.CellReason ?? string.Empty,
                    ChunkX = source.ChunkX,
                    ChunkZ = source.ChunkZ
                });
                if (i > 0) signature.Append(',');
                signature.Append(source.Key);
            }

            string current = signature.ToString();
            if (!string.Equals(current, m_liveMapPlayerSignature, StringComparison.Ordinal))
            {
                m_liveMapPlayerSignature = current;
                int hostSlots = 0, avatarSlots = 0, missingCells = 0;
                for (int i = 0; i < snapshot.Players.Count; i++)
                {
                    LiveMapPlayer player = snapshot.Players[i];
                    if (player.IsHost) hostSlots++; else avatarSlots++;
                    if (!player.HasCell) missingCells++;
                }
                Log("实时地图角色槽位：" + snapshot.Players.Count + " 个（主机 " + hostSlots +
                    " 个槽位 / 联机化身 " + avatarSlots + " 个）· 键 " + current +
                    (missingCells > 0 ? " · " + missingCells + " 个暂时读不到坐标（条目照列，原因随快照下发）" : ""));
            }
        }

        private LiveMapSnapshot BuildLiveMap(bool all, bool hasCell, int cellX, int cellY, int cellZ,
            int centerChunkX, int centerChunkZ, int radius)
        {
            var snapshot = new LiveMapSnapshot
            {
                HasViewerCell = hasCell,
                CellX = cellX,
                CellY = cellY,
                CellZ = cellZ,
                ChunkRadius = all ? 0 : radius,
                View = all ? LiveMapView.All : LiveMapView.Window,
                WatchedClientId = m_host?.WatchedClientId ?? -1,
                WatchedClientName = m_host?.WatchedClientName ?? string.Empty,
                // v6：被观察身份的稳定键（重连不变；页面据此说明"看的是哪个身份"）
                WatchedStableKey = m_host?.WatchedStableKey ?? string.Empty
            };
            // v9：主机端动物位置（仅位置；客户端不贡献）。截断 / 读不到都在这里计数并限流写日志。
            BuildLiveMapAnimals(snapshot);
            // v10：每个玩家角色槽位（主机本机槽位 0..N-1 + 每个联机化身的 (clientId, slot)）。
            BuildLiveMapPlayers(snapshot);
            if (hasCell)
            {
                snapshot.Centers.Add(new LiveMapCenter
                {
                    ClientId = -1,
                    StableKey = LiveMapCodec.HostKey,
                    Name = "Host",
                    IsHost = true,
                    ChunkX = cellX >> 4,
                    ChunkZ = cellZ >> 4,
                    HasCell = true,
                    CellX = cellX,
                    CellY = cellY,
                    CellZ = cellZ
                });
            }
            // 客户端角色：区块中心来自它上报的兴趣中心；**坐标**取主机侧那个联机角色实体的
            // ComponentBody.Position（真实身体位置，不按区块中心猜）。读不到就带原因发出去，
            // 由网页明说"该角色坐标未知"。
            if (m_host != null)
            {
                TerrainHostSnapshot hostSnapshot = m_host.GetSnapshot();
                // 顺序稳定：按稳定键排（主机端 m_clients 是字典，迭代顺序不保证）。
                // `GetSnapshot` 已经按**身份**去重（同一身份重连/新旧连接共存也只给一条），
                // 所以这里每个身份只有一个条目 —— 页面选角（稳定键）不会因为重连多出一个。
                var players = new List<ClientView>();
                for (int i = 0; i < hostSnapshot.Clients.Count; i++)
                {
                    if (hostSnapshot.Clients[i].Kind == ClientKind.Player) players.Add(hostSnapshot.Clients[i]);
                }
                players.Sort((a, b) => string.CompareOrdinal(a.StableKey, b.StableKey));
                for (int i = 0; i < players.Count; i++)
                {
                    ClientView view = players[i];
                    string name = string.IsNullOrEmpty(view.Name) ? ("Player" + view.Id) : view.Name;
                    var center = new LiveMapCenter
                    {
                        ClientId = view.Id,
                        StableKey = view.StableKey,
                        Name = name,
                        IsHost = false,
                        ChunkX = view.Center.X,
                        ChunkZ = view.Center.Z
                    };
                    if (m_avatarLink == null)
                    {
                        center.CellReason = "没有接上联机角色视图（IAvatarTerrainLink）→ 读不到该角色的身体位置";
                    }
                    else if (m_avatarLink.TryGetAvatarCell(view.Id, out Vector3 avatarPosition,
                        out string avatarReason))
                    {
                        center.HasCell = true;
                        center.CellX = (int)Math.Floor(avatarPosition.X);
                        center.CellY = (int)Math.Floor(avatarPosition.Y);
                        center.CellZ = (int)Math.Floor(avatarPosition.Z);
                        // 区块必须由**角色真实坐标**推导：view.Center 是客户端上报的兴趣中心，会滞后/错位
                        // （实机出现过 cell=[449,81,64] 而 chunk=[26,3] 的错配，用户："坐标和位置没对上"）。
                        // 只有读不到坐标时才保留兴趣中心区块并带原因（网页会显示"坐标未知"）。
                        center.ChunkX = center.CellX >> 4;
                        center.ChunkZ = center.CellZ >> 4;
                    }
                    else
                    {
                        center.CellReason = avatarReason;
                    }
                    snapshot.Centers.Add(center);

                    // 该身份**当前存在于客户端内存**的区块（客户端 ClientChunks 整集/增量维护）=
                    // 实时地图的"客户端线框"只画这一份：客户端卸载的块会从这里消失（用户口径：
                    // "已经脱离内存的格子就取消绘制"）。ReportedLoaded 是会话累计，仅随头部一起给参考值。
                    var client = new LiveMapClient
                    {
                        ClientId = view.Id,
                        StableKey = view.StableKey,
                        Name = name,
                        ReportedChunkCount = view.ReportedLoaded.Count,
                        CurrentLoadedChunkCount = view.MemoryChunks.Count
                    };
                    snapshot.Clients.Add(client);
                    for (int c = 0; c < view.MemoryChunks.Count; c++)
                    {
                        ChunkKey key = view.MemoryChunks[c];
                        snapshot.ClientChunks.Add(new LiveMapClientChunk
                        {
                            ClientId = view.Id,
                            StableKey = view.StableKey,
                            X = key.X,
                            Z = key.Z,
                            // v7：该块相对**这个身份自己**的校验状态（网页客户端线框按它着色）。
                            // 用稳定键查该身份的会话记录 —— 不能套"被观察客户端"的裁决，
                            // 否则第二个客户端会被套上第一个人的绿/红（伪造状态）。查不到 = None(0)，
                            // 网页对 0 明确报警、不画线框（绝不猜一个状态）。
                            // 注意：这块可能刚加载、摘要还没上报/裁决 → 0 是**正常**的（网页不画框，
                            // 等该身份上报摘要后下一次快照就带上真实状态）。
                            State = (byte)(m_host != null
                                ? m_host.VerifyStateOf(view.StableKey, key)
                                : ChunkVerifyState.None)
                        });
                    }
                }
            }

            if (all)
            {
                List<ChunkKey> loaded = m_bridge.EnumerateAllocatedChunks();
                // 顺序稳定（网页按 x,z 画；顺序稳定能避免同一批数据每次都换位置）
                loaded.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
                int empty = 0;
                for (int i = 0; i < loaded.Count; i++)
                {
                    LiveMapChunk chunk = m_bridge.BuildLiveMapChunk(loaded[i]);
                    if (chunk.IsEmpty()) { empty++; continue; }   // 还没生成内容 → 不发（发了也是空白）
                    // s 只对"该玩家端上报过摘要"的块非 0（VerifyStateOf 内部按 ReportedLoaded 过滤）：
                    // 客户端没上报的块 = 0 → 网页不画框（用户口径："只有纳入上报的区域才显示方块"）
                    chunk.State = (byte)(m_host != null ? m_host.VerifyStateOf(loaded[i]) : ChunkVerifyState.None);
                    // v5：该块还有几个在线玩家端没拿到最新（主机侧内存计数；主机卸载过也不清零）
                    chunk.PendingClients = m_host != null ? m_host.PendingClientCount(loaded[i]) : 0;
                    snapshot.Chunks.Add(chunk);
                }
                if (m_verbose)
                    Log("实时地图：引擎已分配 " + loaded.Count + " 块，其中 " + empty + " 块没有方块被跳过");
                return snapshot;
            }

            for (int x = centerChunkX - radius; x <= centerChunkX + radius; x++)
            {
                for (int z = centerChunkZ - radius; z <= centerChunkZ + radius; z++)
                {
                    var key = new ChunkKey(x, z);
                    if (!m_bridge.HasContents(key)) continue;      // 未加载 → 不进地图
                    LiveMapChunk chunk = m_bridge.BuildLiveMapChunk(key);
                    if (chunk.IsEmpty()) continue;
                    // 同上：没上报过的块 s=0（窗口视图也不画框）
                    chunk.State = (byte)(m_host != null ? m_host.VerifyStateOf(key) : ChunkVerifyState.None);
                    chunk.PendingClients = m_host != null ? m_host.PendingClientCount(key) : 0;   // v5
                    snapshot.Chunks.Add(chunk);
                }
            }
            return snapshot;
        }

        /// <summary>端口转发用：把消息发到本模块持有的连接上。</summary>
        internal bool SendThroughChannel(MessageType type, byte[] payload)
        {
            if (m_server != null)
            {
                // 目标必须是**当前连着的**玩家端：只用"最后加入的 id"会在这个连接失效后
                // 永久发不出去（世界传输实测卡在 0/1 就是这个原因）。
                if (!IsClientSendable(m_hostClientId))
                {
                    int picked = PickSendableClient();
                    if (picked <= 0) return false;
                    m_hostClientId = picked;
                }
                return m_server.TrySend(m_hostClientId, type, payload);
            }
            // 死连接**明确失败**：不能只看"客户端对象还在"（实机故障：对端掉线后 TrySend 仍返回 true，
            // 上层以为发出去了、posSent 继续涨，主机一个字节都收不到）。看门狗会在同一帧里拆掉它。
            if (m_client == null || !m_client.IsAlive) return false;
            return m_client.TrySend(type, payload);
        }

        /// <summary>
        /// 这个玩家端**还活着、可以尝试发**（Connected / Backpressured）。
        /// Backpressured 是"发送队列满、这一帧发不进去"的显式背压：连接仍然可用，调用方下一帧重试即可，
        /// **绝不能当成断线**（世界传输会把一个慢客户端误判成离开，把它的整图传输中途放弃）。
        /// </summary>
        private bool IsClientSendable(int clientId)
        {
            if (clientId <= 0 || m_server == null) return false;
            ChannelState state = m_server.StateOf(clientId);
            return state == ChannelState.Connected || state == ChannelState.Backpressured;
        }

        /// <summary>
        /// 端口转发用：把消息发给**指定**的玩家端（世界传输按 clientId 分开发送）。
        /// 不是主机 / 目标不可达 ⇒ false（明确失败，绝不改投别的客户端）。
        /// </summary>
        internal bool SendThroughChannelTo(int clientId, MessageType type, byte[] payload)
        {
            if (m_server == null) return false;
            if (!IsClientSendable(clientId)) return false;
            return m_server.TrySend(clientId, type, payload);
        }

        private int PickSendableClient()
        {
            if (m_server == null) return 0;
            PeerInfo[] peers = m_server.ListPeers();
            for (int i = 0; i < peers.Length; i++)
            {
                if (!peers[i].Alive) continue;
                if (peers[i].State != ChannelState.Connected) continue;
                if (peers[i].Kind != ClientKind.Player) continue;   // 调试端只观察，不下发
                return peers[i].Id;
            }
            return 0;
        }

        /// <summary>当前发送目标（主机侧 = 最近完成握手的客户端；多客户端各发一份留待后续）。</summary>
        private int m_hostClientId;
        internal bool ChannelConnected => m_server != null || (m_client != null && m_client.IsAlive);
        internal string ChannelStateText => m_server != null ? "host-listening" : (m_client?.State.ToString() ?? "none");

        private void HandleHostClientJoined(int clientId, string name, string identity)
        {
            m_hostClientId = clientId;
            // **只有玩家端**才算"加入房间"：调试端（实时地图工具）只是观察者。
            // 把它当玩家会让"只发一次的世界"发给观察者，真正的玩家一个字节都收不到（实测），
            // 而且调试端没有身份、不该建角色。
            if (!IsPlayerPeer(clientId)) return;
            // 是不是**重连**（对端 Hello 里说自己已经有过这个世界）：世界传输据此不再重发整图。
            // 握手刚写完 state，所以这里查得到；非玩家端上面已经 return。
            bool rejoin = m_host != null && m_host.WasRejoin(clientId);
            m_clientJoined?.Invoke(clientId, name, identity ?? string.Empty, rejoin);
        }

        /// <summary>
        /// 主机：一个玩家端的连接**已经不可达**（对端关闭 / 链路失效 / 协议违规）。
        /// 必须写一行明确原因（这条之前是完全静默的：实机里断了 4 小时，日志只有一句"从欠更新计数里移除"），
        /// 并让联机角色模块清掉它的角色 —— 否则角色实体会停在最后一个已应用的位置上，
        /// 看起来就是"主机的客户端角色卡住不动"，重连后还会多出一个。
        /// </summary>
        private void HandleHostClientLost(int clientId, string reason)
        {
            LogWarning("玩家端 clientId=" + clientId + " 的通道已失效：" + reason +
                " → 清理它的联机角色（角色不再更新位置）并停止把它算作在线客户端；" +
                "对端若重连，会作为新连接重新握手并重建角色");
            m_clientLeft?.Invoke(clientId, reason ?? string.Empty);
        }

        /// <summary>客户端侧：HelloAck 收到的主机身份 / 名字（未握手 = 空）。</summary>
        internal string ClientHostIdentity => m_clientSync?.HostIdentity ?? string.Empty;

        internal string ClientHostName => m_clientSync?.HostName ?? string.Empty;

        /// <summary>客户端侧：主机给本端的传输层编号（旧 <c>client.ClientID</c>）；未握手 = -1。</summary>
        internal int ClientLocalClientId => m_clientSync?.LocalClientId ?? -1;

        /// <summary>客户端侧：HelloAck 里的主机资料 / 本机分配资料 / 是否要求先建角色。</summary>
        internal PlayerProfileSnapshot ClientHostProfile => m_clientSync?.HostProfile;

        internal PlayerProfileSnapshot ClientAssignedProfile => m_clientSync?.AssignedProfile;

        internal bool ClientAssignedSkinAssetPresent =>
            m_clientSync?.AssignedSkinAssetPresent == true;

        internal bool ClientProfileRequired => m_clientSync?.ProfileRequired ?? false;

        internal string ClientProfileRequiredReason =>
            m_clientSync?.ProfileRequiredReason ?? string.Empty;

        /// <summary>
        /// 角色创建完成 / 资料变化后重新握手（旧实现 SubmitPendingJoin → 新 JoinGame）。
        /// 只有"玩家点了加入房间、通道要重连"时才允许：否则明确返回 false。
        /// 走 StopChannel + ConnectTo（与玩家重新点房间同一条路径），不是另造一条连接链。
        /// </summary>
        private bool ReconnectClient(string reason)
        {
            if (!m_wantClient || m_client == null)
            {
                LogWarning("要求重新握手（" + (reason ?? string.Empty) + 
                    "）时没有可重连的客户端通道（wantClient=" + m_wantClient +
                    " · client=" + (m_client == null ? "null" : "connected") +
                    "）→ 不假装重连成功；请从 Play 屏重新加入房间");
                return false;
            }
            string host = m_wantHost;
            int port = m_wantPort;
            Log("按主机要求重新握手（" + (reason ?? string.Empty) + "）→ 重新连接 " + host + ":" + port +
                "；本次 Hello 会带上角色创建界面刚确定的资料（姓名/性别/皮肤）");
            StopChannel();
            if (ConnectTo(host, port, out string connectReason)) return true;
            // StartClientOnce 失败时 m_wantClient 仍为 true —— 按 5s 间隔自动重试（加入流程本身）。
            LogWarning("重新握手的第一条连接没有连上（" + (connectReason ?? string.Empty) +
                "）→ 保持加入流程，按 5s 间隔继续重试");
            return true;
        }

        /// <summary>
        /// 客户端：本端**此刻是不是正在游玩**这个下载来的联机世界？ → Hello 里的 alreadyHasWorld。
        ///
        /// 只有一条判定入口（<see cref="RejoinFlagPolicy"/>）：在游戏里（GameScreen）· Project 已加载 ·
        /// 当前世界目录在下载登记表里。**不是**"磁盘上有一个下载来的世界 / 登记表里还有它"——
        /// 在 Play 屏加入（正在下载 / 刚导入还没进世界）必须为 false，主机会照常下发整图。
        /// 取不到事实（没有端口 / 没有 Project / 没有世界目录）一律**明确写原因**并返回 false
        /// （= 按首次加入处理：主机会下发整图，不会漏发；这不是静默兜底，原因进握手日志）。
        /// </summary>
        private bool IsPlayingDownloadedWorld(out string reason, out string storedSessionId,
            out long storedBaseSequence)
        {
            storedSessionId = string.Empty;
            storedBaseSequence = 0L;
            m_clientWorldDirectory = null;
            // Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.CurrentScreen
            // Source: Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerUpdateLoop.cs:114（gameScreenActive）
            bool inGame = Game.ScreensManager.CurrentScreen is Game.GameScreen;
            var project = Game.GameManager.Project;
            bool projectLoaded = project != null;
            string directory = null;
            if (projectLoaded)
            {
                var gameInfo = project.FindSubsystem<Game.SubsystemGameInfo>(false);
                directory = gameInfo?.DirectoryName;
            }
            bool directoryKnown = !string.IsNullOrEmpty(directory);
            if (directoryKnown) m_clientWorldDirectory = directory;
            bool downloaded = false;
            if (projectLoaded && directoryKnown)
            {
                if (m_context != null &&
                    m_context.TryGetPort(out IDownloadedWorldPort port) && port != null)
                {
                    try
                    {
                        downloaded = port.IsDownloadedWorld(directory);
                        if (downloaded)
                            port.TryGetWorldSession(directory, out storedSessionId, out storedBaseSequence);
                    }
                    catch (Exception e)
                    {
                        // 异常不能当"是"：否则主机跳过整图、客户端永远等不到世界。明确写原因。
                        LogWarning("判断当前世界是否下载来的联机世界时抛异常：" + e.GetType().Name + " " +
                            e.Message + " → 本次 Hello 按首次加入处理（主机会下发整图）");
                    }
                }
                else
                {
                    LogWarning("没有下载世界端口（世界传输模块未上线）→ 本次 Hello 按首次加入处理" +
                        "（主机会下发整图）");
                }
            }
            // 判定唯一入口（RejoinFlagPolicy）：在游戏内 + Project + 下载登记 + **会话一致**（+ 有会话标识）。
            // 会话标识来自下载世界的登记（WorldBegin 下发）与本端最近一次 HelloAck；任一缺失/不一致 ⇒ false。
            bool alreadyHasWorld = RejoinFlagPolicy.ShouldSendAlreadyHasWorld(
                inGame, projectLoaded, directoryKnown, downloaded,
                storedSessionId, m_lastHostSessionId, out reason);
            if (directoryKnown) reason += "（当前世界目录 " + directory + "）";
            return alreadyHasWorld;
        }

        /// <summary>当前正在游玩的世界目录（最近一次 Hello 判定时记录；追帧拒绝时用来作废会话）。</summary>
        private string m_clientWorldDirectory;

        /// <summary>读当前 Project 的世界目录（追帧拒绝 / 重连拒绝时作废会话用；拿不到返回空）。</summary>
        private string CurrentWorldDirectory()
        {
            try
            {
                var gameInfo = Game.GameManager.Project?.FindSubsystem<Game.SubsystemGameInfo>(false);
                return gameInfo?.DirectoryName ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private bool IsPlayerPeer(int clientId)
        {
            if (m_server == null) return false;
            PeerInfo[] peers = m_server.ListPeers();
            for (int i = 0; i < peers.Length; i++)
            {
                if (peers[i].Id == clientId) return peers[i].Kind == ClientKind.Player;
            }
            return false;
        }

        private void HandleChunkAllocated(ChunkKey key)
        {
            // 客户端：块进入引擎内存（内容可能还没定稿）→ 实时地图的客户端线框集合（v8）。
            // 与"块加载完"分开：海洋 / 水面块会先出现在这里，内容定稿后才走 HandleChunkLoaded。
            m_clientSync?.OnMemoryChunkAllocated(key);
            if (m_verbose) Log("区块分配（进入内存）" + key + "（内存区块 " + m_bridge.AllocatedCount +
                " · 常驻 " + m_bridge.ResidentCount + "）");
        }

        private void HandleChunkFreed(ChunkKey key)
        {
            // 客户端：块被引擎释放（离开内存）→ 从实时地图线框集合里移除（v8）。
            m_clientSync?.OnMemoryChunkFreed(key);
            if (m_verbose) Log("区块释放（离开内存）" + key + "（内存区块 " + m_bridge.AllocatedCount +
                " · 常驻 " + m_bridge.ResidentCount + "）");
        }

        /// <summary>
        /// 客户端：块在内存里超过宽限仍未定稿 → **明确不再作为"内存区块"上报**。
        /// 与"引擎释放"分开成一条日志：块还在内存里，只是没有可核对的内容（原因已写在 reason 里）。
        /// </summary>
        private void HandleChunkWireExcluded(ChunkKey key, string reason)
        {
            m_clientSync?.OnMemoryChunkFreed(key);
            LogWarning("[SuMP][terrain-sync] 内存区块不再上报 " + key + "：" + reason +
                " · 当前内存区块 " + m_bridge.AllocatedCount + " / 常驻 " + m_bridge.ResidentCount +
                "（memoryNotResident " + m_bridge.MemoryNotResident + "）");
        }

        /// <summary>客户端：之前被排除的块后来定稿了 → 重新纳回"内存区块"上报（地图上框会回来）。</summary>
        private void HandleChunkWireIncluded(ChunkKey key)
        {
            m_clientSync?.OnMemoryChunkAllocated(key);
            Log("[SuMP][terrain-sync] 内存区块重新上报 " + key + "（内容已定稿）· 内存区块 " +
                m_bridge.AllocatedCount + " / 常驻 " + m_bridge.ResidentCount);
        }

        private void HandleChunkLoaded(ChunkKey key)
        {
            // 客户端：块加载完 → 算一次摘要上报（这是唯一算摘要的时刻；与内存区块集合无关）
            if (m_clientSync != null) m_clientSync.OnChunkLoaded(key);
            if (m_verbose) Log("区块加载（内容可用）" + key + "（常驻 " + m_bridge.ResidentCount +
                " · 内存区块 " + m_bridge.AllocatedCount + "）");
        }

        private void HandleChunkUnloaded(ChunkKey key)
        {
            m_clientSync?.OnChunkUnloaded(key);
            if (m_verbose) Log("区块内容不再可用 " + key + "（常驻 " + m_bridge.ResidentCount +
                " · 内存区块 " + m_bridge.AllocatedCount + "；块仍在内存时方框保留）");
        }

        private void HandleHostCellChanged(ChunkKey key, CellEdit edit)
        {
            if (m_host == null)
            {
                // 本端不是主机：正常情况（单机 / 还没加入房间）不会有会话，直接忽略；
                // 但**玩家本来就在客户端角色**（连接断开 / 正在重连）时，这一格是真实的本地写入，
                // 当下没有接收者 —— 必须计数 + 限流告警，等重连后的强制核对纠正（不静默）。
                if (m_wantClient)
                {
                    m_hostCellNoHost++;
                    long now = Environment.TickCount64;
                    if (m_lastHostCellNoHostLogMs == 0 ||
                        now - m_lastHostCellNoHostLogMs >= 5000)
                    {
                        m_lastHostCellNoHostLogMs = now;
                        LogWarning("[SuMP][terrain-sync] 本端是客户端角色但连接不在了：一格本地写入" +
                            "（" + key + " " + edit.X + "," + edit.Y + "," + edit.Z +
                            "）走不到主机 → 当下没有接收者；累计 " + m_hostCellNoHost +
                            " 格（重连后由强制核对 / 整块回传纠正）");
                    }
                }
                return;
            }
            int sent = m_host.BroadcastCellChange(key, edit);
            if (sent == 0) m_hostCellNoRecipient++;
            if (m_verbose && sent > 0)
                Log("下发改动 " + key + " (" + edit.X + "," + edit.Y + "," + edit.Z + ") → " + sent +
                    " 个投递目标（内存集合包含该块的身份 ∪ 兴趣窗口订阅者）");
        }

        /// <summary>
        /// 主机：**按需单块加载**（客户端上报的边缘块）的限流行 —— 超时放弃 / 被拒绝 / 汇总。
        /// TerrainSyncHost 与 OnDemandChunkLoads 已经限流（同一块一行、拒绝与汇总按 10s），这里照写一行。
        /// 这条链路正是"客户端站着不动、边缘黄框也能变绿"的证据行（旧实现的请求只到一行 verbose 日志）。
        /// </summary>
        private void HandleHostOnDemandTrace(string reason)
        {
            Log(reason);
        }

        /// <summary>
        /// 主机欠着一条上报的裁决（内容一直没加载出来 / 客户端断开时还有欠账）。
        /// TerrainSyncHost 已经限流（同一块最多一行、断开时最多一行），这里如实写出来，
        /// 绝不静默放弃、绝不假装匹配。
        /// </summary>
        private void HandleHostReportUnresolved(string reason)
        {
            LogWarning(reason);
        }

        /// <summary>
        /// 欠更新计数器（实时地图格内数字）的生命周期决策 —— 由 TerrainSyncHost 在**状态真的变了**时抛出
        /// （0→≥1 的加入 / 真的移除了一个标记 / 客户端离开清掉它的全部标记 / 会话结束整体清空），
        /// 所以这里照原样写一行即可，不是逐格改动、逐帧的热路径日志。
        /// </summary>
        private void HandleHostPendingTrace(string reason)
        {
            Log(reason);
        }

        /// <summary>
        /// 身份（UserId）会话记录的生命周期（建立 / 重连复用 / 房间结束清空）—— Terrain 层按"状态真的变了"
        /// 才抛一条，这里照原样写一行。句子里只有身份前 8 位与稳定键（**GUID 全值禁止进日志**）。
        /// </summary>
        private void HandleHostSessionTrace(string reason)
        {
            Log(reason);
        }

        /// <summary>
        /// 身份缺失 / 身份异常（Hello 没带 UserId → 按 clientId 单独一条的**退化路径**）。
        /// 这是必须让用户看见的错误：写成 Warning，并保留 Terrain 层给的完整原因（不静默兜底）。
        /// </summary>
        private void HandleHostSessionProblem(string reason)
        {
            LogWarning(reason);
        }

        /// <summary>
        /// 客户端同步队列**持续饱和**（≥3 s）的限流原因行。饱和 = 块加载快于每帧预算，
        /// 是飞行/快速移动时的预期背压，不是丢包：照实写一行（客户端那边已经限流成 10s 一条），
        /// 让"为什么还在排队、排了多少、预算多少"可审计。
        /// </summary>
        private void HandleClientQueueSaturated(string reason)
        {
            Log(reason);
        }

        /// <summary>
        /// 客户端回传载荷解码失败（协议异常）：**必须让用户看见**，写成 Warning（客户端那边已限流）。
        /// 这一片不落地、不当成已覆盖，计入 ResyncDecodeFailures。
        /// </summary>
        private void HandleClientDecodeFailed(string reason)
        {
            LogWarning(reason);
        }

        /// <summary>
        /// 主机侧收到客户端"当前内存区块集合"的明确日志（整集收齐 / 增量汇总 —— Terrain 层已限流）。
        /// 这两类都是**正常状态迁移**，写成 Info，一行说明"哪个身份、多少块"。
        /// </summary>
        private void HandleHostClientChunkTrace(string reason)
        {
            Log(reason);
        }

        /// <summary>
        /// 主机侧拒绝了客户端"当前内存区块集合"载荷（超上限 / 格式错误 / 非当前句柄）：
        /// **必须让用户看见**，写成 Warning（Terrain 层已限流，行内带最近/累计条数）。
        /// </summary>
        private void HandleHostClientChunkProblem(string reason)
        {
            LogWarning(reason);
        }

        /// <summary>
        /// 客户端侧"当前内存区块集合"无法按协议发送（整集超过上限 / 编码失败）：明确失败写 Warning，
        /// 绝不静默降级成"只发一半"（那会让主机把没发到的块当成已卸载删掉）。
        /// </summary>
        private void HandleClientChunkSetFailed(string reason)
        {
            LogWarning(reason);
        }

        // ------------------------------------------------------------ 断线重连：追帧 / 会话

        /// <summary>主机当前会话标识（不是主机 = 空串）。世界传输模块通过 IWorldSessionPort 读它。</summary>
        internal string CurrentSessionId => m_server != null ? (m_sessionId ?? string.Empty) : string.Empty;

        /// <summary>主机当前改动水位（不是主机 = 0）。世界清单的导出基线取这里。</summary>
        internal long CurrentChangeSequence => m_host?.CurrentChangeSequence ?? 0L;

        /// <summary>
        /// 客户端：下载世界已导入完成（WorldTransferModule.Install → IWorldSessionPort）。
        /// 把"上次应用序号"重置为世界清单的导出基线，并把该世界的会话记为最近一次主机会话。
        /// </summary>
        internal void OnClientWorldInstalled(string worldDirectory, string sessionId, long baseSequence)
        {
            m_clientChangeSequence = Math.Max(0L, baseSequence);
            if (!string.IsNullOrEmpty(sessionId)) m_lastHostSessionId = sessionId;
            m_clientWorldDirectory = worldDirectory;
            m_clientSync?.SetAppliedChangeSequence(m_clientChangeSequence);
            Log("下载世界已导入：上次应用序号对齐到世界清单基线 " + m_clientChangeSequence +
                " · 会话 " + SessionIdentity.Short(sessionId) +
                "（之后的重连会带上它请求追帧；主机历史不覆盖时才整图重下）");
        }

        /// <summary>
        /// 主机拒绝重连（HelloAck RejoinAccepted=false）。客户端必须把下载世界的会话作废：
        ///   · 本版主机会在这条连接上直接下发整图（hostWillSendWorld=true）→ 保持连接等导入；
        ///   · 旧主机不带会话字段（hostWillSendWorld=false）→ 断开重连成首次加入，否则永远等不到世界。
        /// </summary>
        private void HandleClientRejoinRejected(string reason, bool hostWillSendWorld)
        {
            LogWarning("主机拒绝重连：" + reason + " → 作废下载世界的会话标识" +
                (hostWillSendWorld
                    ? "；主机将在本连接下发整图，客户端等导入后进入"
                    : "；断开本连接，以首次加入身份重连（主机才会下发整图）"));
            ForgetCurrentWorldSession();
            m_clientChangeSequence = 0L;
            if (!hostWillSendWorld) RequestClientDrop("主机是旧构建（HelloAck 没有会话字段），必须重连成首次加入");
        }

        /// <summary>
        /// 主机拒绝追帧（同会话握手通过后历史被裁剪）：**明确要求整图重下**。
        /// 作废会话 → 断开 → 下一次 tick 以首次加入身份重连；主机于是发送整图。
        /// </summary>
        private void HandleClientCatchUpRejected(string reason)
        {
            if (m_clientCatchUpRejectionLogged) return;
            m_clientCatchUpRejectionLogged = true;
            LogWarning("主机拒绝追帧（历史已裁剪 / 非重连）：" + reason +
                " → 作废下载世界的会话标识、断开连接并以首次加入身份重连（整图重下；绝不带着过期世界继续玩）");
            ForgetCurrentWorldSession();
            m_clientChangeSequence = 0L;
            RequestClientDrop("追帧被拒绝（需要整图重下）");
        }

        private void ForgetCurrentWorldSession()
        {
            string directory = CurrentWorldDirectory();
            if (string.IsNullOrEmpty(directory))
            {
                LogWarning("追帧/重连被拒绝，但读不到当前世界目录 → 无法作废会话标识；" +
                    "下次 Hello 仍会带旧会话，由主机再次判定（不会静默继续）");
                return;
            }
            if (m_context != null && m_context.TryGetPort(out IDownloadedWorldPort port) && port != null)
                port.ForgetWorldSession(directory);
        }

        /// <summary>请求在下一次 tick 拆掉客户端连接（不在网络帧回调里直接 Dispose）。</summary>
        private void RequestClientDrop(string why)
        {
            m_clientDropRequested = true;
            m_clientDropReason = why;
        }

        private bool m_clientDropRequested;
        private string m_clientDropReason;

        /// <summary>
        /// 拆掉客户端连接（已作废会话 / 旧主机拒绝重连）。**不 kill 任何进程**：只关本模块自己的 socket；
        /// 下一个 tick 的重连分支会以首次加入身份重新握手。
        /// </summary>
        private void DropClientConnection()
        {
            if (m_client == null) return;
            string why = m_clientDropReason ?? "客户端要求重连";
            m_clientDropRequested = false;
            m_clientDropReason = null;
            try { m_client.FrameReceived -= m_bridge.PostFrame; } catch (Exception) { }
            try { m_clientSync?.Saturated -= HandleClientQueueSaturated; } catch (Exception) { }
            try { m_clientSync?.DecodeFailed -= HandleClientDecodeFailed; } catch (Exception) { }
            try { m_clientSync?.ClientChunkSetFailed -= HandleClientChunkSetFailed; } catch (Exception) { }
            try { m_clientSync?.RejoinRejected -= HandleClientRejoinRejected; } catch (Exception) { }
            try { m_clientSync?.CatchUpRejected -= HandleClientCatchUpRejected; } catch (Exception) { }
            try { m_clientSync?.CatchUpTrace -= HandleClientCatchUpTrace; } catch (Exception) { }
            try { m_clientSync?.CatchUpProblem -= HandleClientCatchUpProblem; } catch (Exception) { }
            try { m_clientSync?.ResidentRequeue -= HandleClientResidentRequeue; } catch (Exception) { }
            try { m_clientSync?.RepeatedMismatch -= HandleClientRepeatedMismatch; } catch (Exception) { }
            try { m_clientSync?.VerifyNotStarted -= HandleClientVerifyNotStarted; } catch (Exception) { }
            try { m_client.Dispose(); } catch (Exception) { }
            m_client = null;
            m_clientSync = null;
            m_reconnectAttempts = 0;
            m_nextReconnectMs = 0L;      // 下一次 tick 立刻以首次加入身份重连
            LogWarning("已断开与主机的连接（" + why + "）→ 下一次 tick 以首次加入身份重连（主机将下发整图）");
        }

        /// <summary>主机：重连判定的一行明确日志（接受 / 拒绝 + 依据）。</summary>
        private void HandleHostRejoinTrace(string reason)
        {
            Log(reason);
        }

        /// <summary>主机：追帧过程（开始 / 完成 / Barrier）的明确日志。</summary>
        private void HandleHostCatchUpTrace(string reason)
        {
            Log(reason);
        }

        /// <summary>主机：追帧拒绝 / 历史裁剪 / 超时 —— 必须让用户看见，写 Warning。</summary>
        private void HandleHostCatchUpProblem(string reason)
        {
            LogWarning(reason);
        }

        /// <summary>客户端：追帧过程（请求 / Begin / Barrier / 完成）的明确日志。</summary>
        private void HandleClientCatchUpTrace(string reason)
        {
            Log(reason);
        }

        /// <summary>客户端：追帧协议异常 / 等不到主机回应 / 尾字段非法 —— 写 Warning。</summary>
        private void HandleClientCatchUpProblem(string reason)
        {
            LogWarning(reason);
        }

        /// <summary>
        /// (重)连后"重新排队 N 个常驻区块"的结果行（Terrain 层已限流：一次重连一行）。
        /// 这是"重连后本地核对状态为什么从 0 恢复"的唯一证据行。
        /// </summary>
        private void HandleClientResidentRequeue(string reason)
        {
            Log(reason);
        }

        /// <summary>
        /// 同一块反复不一致（回传风暴特征）：必须让用户看见（Warning），行内带区块键与每秒速率。
        /// </summary>
        private void HandleClientRepeatedMismatch(string reason)
        {
            LogWarning(reason);
        }

        /// <summary>
        /// 回传落地后本该重报、但状态机明确拒绝（已 Fresh / 在途 / 重试用尽）：Warning 一行（已限流）。
        /// </summary>
        private void HandleClientVerifyNotStarted(string reason)
        {
            LogWarning(reason);
        }

        /// <summary>
        /// 同一块连续不一致重试超过上限 → 停止自动重报（区块键 + 原因 + 次数；Terrain 层已按块 / 时间限流）。
        /// 这是"回传风暴不会无限 1:1 循环"的显式 Warning。
        /// </summary>
        private void HandleClientMismatchRetryExhausted(string reason)
        {
            LogWarning(reason);
        }

        private void Report(double now)
        {
            long ms = (long)(now * 1000.0);
            if (ms - m_lastReportMs < 5000) return;
            m_lastReportMs = ms;
            if (m_host != null)
            {
                TerrainHostSnapshot snapshot = m_host.GetSnapshot();
                Log("主机 状态：客户端 " + snapshot.Clients.Count + " · 兴趣并集 " + snapshot.Interest.Count +
                    " · 常驻（内容可用）" + snapshot.Loaded.Count + " · 摘要上报 " + snapshot.DigestReports +
                    " · 待裁决 " + snapshot.PendingReports +
                    " · 客户端内存区块（已分配）" + snapshot.MemoryChunkCount +
                    "（整集 " + snapshot.ClientChunkFullSets + " · 增量 " + snapshot.ClientChunkDeltaMessages +
                    " · 拒绝 " + snapshot.ClientChunkRejects + "）" +
                    " · 欠更新标块 " + snapshot.PendingMarks + "（客户端 " + snapshot.PendingClients + "）" +
                    " · 回传字节 " + snapshot.ResyncBytes + " · 广播改动 " + snapshot.CellChangesBroadcast +
                    "（内存命中 " + snapshot.CellChangesMemoryRecipients +
                    "/仅内存 " + snapshot.CellChangesMemoryOnly +
                    "/窗口外 " + snapshot.CellChangesOutsideWindow +
                    " · 推迟 " + snapshot.CellChangeDeferrals +
                    " · 补发 " + snapshot.CellChangeRepairs +
                    "/延后 " + snapshot.CellChangeRepairDeferrals +
                    "/不可补 " + snapshot.CellChangeRepairUnavailable +
                    " · 欠账补快照 " + snapshot.OwedSnapshotsQueued +
                    "/无内容跳过 " + snapshot.OwedSnapshotsSkippedNoContent +
                    " · 要求重新核对 " + snapshot.VerifyRequestsSent +
                    "/发送失败 " + snapshot.VerifyRequestSendFailures +
                    // 陈旧裁决自愈：内容补齐后仍停在红（Mismatch）的块 → 有界地要一份新鲜摘要重判
                    // （请求 / 重判成绿或蓝 / 到上限明确清红；在册 = 还没结论的条数）。
                    " · 陈旧裁决自愈 请求 " + snapshot.StaleVerdictRefreshRequested +
                    "/重判 " + snapshot.StaleVerdictRefreshed +
                    "/清除 " + snapshot.StaleVerdictCleared +
                    "（在册 " + snapshot.PendingStaleVerdicts +
                    " · 没发出去 " + snapshot.StaleVerdictRefreshDeferred + "）" +
                    " · 投递失败 " + snapshot.CellChangesDeliveryFailed + "）" +
                    // 捕获口径（与投递口径分开：这一行回答"主机到底有没有抓到那一格"）：
                    //   hostCellCaptured   = 真正交给广播管线的格数（每一格一次）；
                    //   hostCellRescued    = 在流体 ProcessModifiedCells 清表前接住的格数（>0 说明确实撞上过清表竞争）；
                    //   hostCellMissed     = 捕获时区块已不在内存而被明确跳过的格数（不猜值）；
                    //   broadcastDuplicates= 同一格既在引擎表又被登记 / 接住而去重的次数；
                    //   hostCellNoRecipient= 抓到了、但订阅表与所有客户端内存集合都没有这一块（没人可发）。
                    " · 捕获 hostCellCaptured " + m_bridge.HostCellsCaptured +
                    "（收集 " + (m_shell != null ? m_shell.CaptureState.Captured : 0L) +
                    " · hostCellRescued " + (m_shell != null ? m_shell.CaptureState.Rescued : 0L) +
                    " · hostCellMissed " + m_bridge.HostCellMisses +
                    " · broadcastDuplicates " + m_bridge.HostCellDuplicates +
                    " · hostCellNoRecipient " + m_hostCellNoRecipient + "）" +
                    // 引信捕获（火药桶 data-only 倒计时）：被流体清表抹掉、由引信 carry 补回的格数；
                    // skipped = 候选格前后相同（不是真实改动）；duplicates = 同格多路报告合并成一次。
                    " · 引信 fuseCellCaptured " +
                    (m_shell != null ? m_shell.CaptureState.FuseCellCaptured : 0L) +
                    "（skipped " + (m_shell != null ? m_shell.CaptureState.FuseCellSkipped : 0L) +
                    " · duplicates " + (m_shell != null ? m_shell.CaptureState.FuseCellDuplicates : 0L) +
                    " · 窗口 " + (m_shell != null ? m_shell.CaptureState.FuseCaptureWindows : 0L) +
                    " · 丢弃 " + (m_shell != null ? m_shell.CaptureState.FuseCellsDropped : 0L) + "）" +
                    // 客户端副本闸门 / 塌落闸门：按来源计数，一句日志可审计（没有静态状态）。
                    " · 副本闸门 " + (m_shell != null ? m_shell.Gate.Describe() : "gate=none") +
                    " · 塌落 " + (m_shell != null ? m_shell.CollapsingState.Describe() : "collapsing=none") +
                    // 活塞闸门：摘掉的 base 写格订阅 / 拦下的 base 写格路径 / 移除的残留表现集合。
                    " · 活塞 " + (m_shell != null ? m_shell.PistonState.Describe() : "piston=none") +
                    // 按需单块加载（客户端上报的边缘块）：**不改任何角色的兴趣半径 / 内容距离**，
                    // 只给"主机没有内容的那些上报块"挂一条只覆盖该块的临时更新位置。
                    //   容量限制的是**活动的引擎请求**（onDemandActive + onDemandHeld ≤ MaxActive），
                    //   不是排队台账：onDemandQueued 可以到 512，按身份轮转 FIFO 等待而不是被拒绝。
                    //   onDemandLoadRequests/Served/Timeouts/Rejected 与 pendingOnDemand 都来自 Terrain 层同一份台账；
                    //   onDemandLoadLocations = 引擎里当前真的挂着几条临时位置（TerrainBridge 计数）。
                    " · 按需单块加载 onDemandQueued " + snapshot.OnDemandQueued +
                    " · onDemandActive " + snapshot.OnDemandActive +
                    " · onDemandHeld " + snapshot.OnDemandHeld +
                    " · onDemandHeldReleasedUnderPressure " + snapshot.OnDemandHeldReleasedUnderPressure +
                    " · onDemandDeferred " + snapshot.OnDemandDeferred +
                    " · onDemandServed " + snapshot.OnDemandLoadServed +
                    "（onDemandLoadServed）· onDemandTimeouts " + snapshot.OnDemandLoadTimeouts +
                    "（onDemandLoadTimeouts）· onDemandRejected " + snapshot.OnDemandLoadRejected +
                    "（onDemandLoadRejected：cap " + snapshot.OnDemandLoadRejectedCap +
                    " · cooldown " + snapshot.OnDemandLoadRejectedCooldown +
                    " · engine " + snapshot.OnDemandLoadRejectedEngine +
                    "）· onDemandLoadRequests " + snapshot.OnDemandLoadRequests +
                    "（区块 " + snapshot.OnDemandLoadChunks +
                    "）· pendingOnDemand " + snapshot.PendingOnDemand +
                    " · 摘掉待裁决 " + snapshot.OnDemandReportsTimedOut +
                    " · 未挂待裁决 " + snapshot.OnDemandReportsDropped +
                    " · 临时位置 " + m_bridge.OnDemandLoadLocations +
                    "（累计创建 " + m_bridge.OnDemandLoadLocationsCreated +
                    "；不改 hostUpdateLocations 的任何半径 / 内容距离，那一行见玩家化身模块状态行））");
            }
            else if (m_clientSync != null)
            {
                TerrainClientSnapshot snapshot = m_clientSync.GetSnapshot();
                Log("客户端 状态：常驻 " + snapshot.Loaded.Count + " · Fresh " + snapshot.Fresh.Count +
                    " · 不一致 " + snapshot.Stale.Count + " · 未核对 " + snapshot.Unverified.Count +
                    " · 匹配 " + snapshot.Match + " · 回传 " + snapshot.ResyncApplied +
                    " · 重报 " + snapshot.ReReports + " · 放弃 " + snapshot.Abandoned +
                    " · 队列 " + (snapshot.QueuedDigests + snapshot.QueuedResyncParts +
                        snapshot.QueuedHashRefreshes) +
                    "（摘要 " + snapshot.QueuedDigests + "/每帧 " + snapshot.DigestBudgetPerTick +
                    " · 回传 " + snapshot.QueuedResyncParts + "/每帧 " + snapshot.ResyncBudgetPerTick +
                    " · 刷新 " + snapshot.QueuedHashRefreshes + "/每帧 " + snapshot.HashRefreshBudgetPerTick + "）" +
                    " · 本轮上报 " + snapshot.ReportsThisTick + "（预算 " + snapshot.ReportBudgetPerTick +
                    " · 本帧算摘要 " + snapshot.DigestsThisTick + "）" +
                    " · 内存区块（已分配）" + snapshot.MemoryChunkCount + "（整集 " + snapshot.ClientChunkFullSetsSent +
                    " · 增量 " + snapshot.ClientChunkDeltaMessagesSent + " 条 · 待发 " +
                    snapshot.QueuedClientChunkMessages + "/每帧 " + snapshot.ClientChunkBudgetPerTick +
                    " 条 · 本帧 " + snapshot.ClientChunkMessagesThisTick + "）" +
                    // 内存 / 常驻 / 未定稿（灰框）口径：memoryNotResident = 引擎已分配但内容还没定稿的块数。
                    // 修复前它是 201−93=108 且永不下降（常驻判据错误地叠加了 TerrainChunk.IsLoaded）；
                    // 现在应长期为 0，非 0 时同一个字段里给出 per-reason 的 contentGenStalled 账。
                    " · 常驻口径 " + m_bridge.DescribeResidency() +
                    " · 累计上报 " + snapshot.ReportEntriesSent + " 条/已算摘要 " + snapshot.DigestsComputed + " 块" +
                    " · 卸载作废 摘要 " + snapshot.DigestsDroppedUnloaded +
                    "/上报 " + snapshot.ReportsDroppedUnloaded +
                    "/回传 " + snapshot.ResyncDroppedUnloaded +
                    "/刷新 " + snapshot.HashRefreshesDroppedUnloaded +
                    " · 解码失败 " + snapshot.ResyncDecodeFailures +
                    " · 核对往返 " + snapshot.LastVerifyMs + "/" + snapshot.MaxVerifyMs + "ms" +
                    " · 有序应用 投递序号 " + snapshot.LastDeliveryIndex +
                    "/缺口 " + snapshot.DeliveryGaps + "/越序 " + snapshot.OutOfOrderChanges +
                    "（暂存实时改动 " + snapshot.HeldLiveChanges + "/丢弃 " + snapshot.HeldLiveChangesDropped + "）" +
                    " · 窗口外强制核对 发出 " + snapshot.MemoryVerificationsSent +
                    "/排队 " + snapshot.QueuedMemoryVerifications +
                    "/延后 " + snapshot.MemoryVerificationsDeferred +
                    "/作废 " + snapshot.MemoryVerificationsDroppedUnloaded +
                    " · 回传过期丢弃 " + snapshot.ResyncDroppedSuperseded +
                    "（丢弃后重核 " + snapshot.ResyncRefreshedAfterDiscard +
                    "/延后 " + snapshot.DiscardRefreshDeferred + "/跳过 " + snapshot.DiscardRefreshSkipped + "）" +
                    " · 不一致重试超限 " + snapshot.MismatchRetryExhausted +
                    "（块 " + snapshot.MismatchRetryChunks + "/最大 " + snapshot.MaxMismatchRetryCount +
                    " · 抑制重报 " + snapshot.MismatchRetrySuppressedVerifications +
                    " · 收敛 " + snapshot.ConvergedChunks + "）" +
                    " · 零格快照 " + snapshot.ResyncEmptySnapshots +
                    "/回传未定稿延后 " + snapshot.ResyncDeferredNotReady +
                    "/上报未定稿延后 " + snapshot.ReportsDeferredNotReady +
                    "/发送时刷新摘要 " + snapshot.ReportHashesRefreshed +
                    " · 重排队 " + snapshot.ResidentRequeues + " 次/最近 " +
                    snapshot.LastResidentRequeueCount + " 块（跳过 " + snapshot.ResidentRequeueSkipped +
                    " · 等追帧 " + snapshot.ResidentRequeueDeferred + " 帧 · 换世界清 " +
                    snapshot.WorldReplaceClearedWork + "）" +
                    " · 每秒 上报 " + snapshot.ReportsPerSecond + "（峰值 " + snapshot.ReportsPerSecondPeak +
                    "）/Match " + snapshot.MatchPerSecond + "/Mismatch " + snapshot.MismatchPerSecond +
                    "/回传收 " + snapshot.ResyncReceivedPerSecond + "/回传落地 " + snapshot.ResyncAppliedPerSecond +
                    " · 不一致区块 " + snapshot.MismatchChunkCount + "（反复 " +
                    snapshot.MismatchRepeatChunkCount + "）· 未发起核对 " + snapshot.VerifyNotStarted +
                    " · 主机要求核对 " + snapshot.VerifyRequestsReceived +
                    " 次/" + snapshot.VerifyRequestChunks + " 块（非法 " +
                    snapshot.VerifyRequestMalformed + "）" +
                    " · 未定稿改动 " + snapshot.CellChangesDroppedNotReady +
                    "/不持有跳过 " + snapshot.CellChangesSkippedNotHeld +
                    " · 收到 " + snapshot.BytesReceived + "B");
            }
        }

        public void Detach(ModuleDetachReason reason)
        {
            m_attached = false;
            try { m_bridge.ChunkLoaded -= HandleChunkLoaded; } catch (Exception) { }
            try { m_bridge.ChunkUnloaded -= HandleChunkUnloaded; } catch (Exception) { }
            try { m_bridge.ChunkAllocated -= HandleChunkAllocated; } catch (Exception) { }
            try { m_bridge.ChunkFreed -= HandleChunkFreed; } catch (Exception) { }
            try { m_bridge.ChunkWireExcluded -= HandleChunkWireExcluded; } catch (Exception) { }
            try { m_bridge.ChunkWireIncluded -= HandleChunkWireIncluded; } catch (Exception) { }
            try { m_bridge.FrameArrived -= HandleFrame; } catch (Exception) { }
            try { m_bridge.HostCellChanged -= HandleHostCellChanged; } catch (Exception) { }
            StopChannel();
            if (m_shell != null)
            {
                m_shell.Bridge = null;      // 解除注入：引擎外壳退回原生行为
                m_shell = null;
            }
            Log("模块下线（" + reason + "）：连接已关闭，只清理本模块自己的资源");
        }

        /// <summary>状态行（给诊断/UI 用），模块不直接写界面。</summary>
        public string StatusLine()
        {
            if (m_host != null)
            {
                TerrainHostSnapshot snapshot = m_host.GetSnapshot();
                return "terrain-sync: host :" + m_hostPort + " clients=" + snapshot.Clients.Count +
                       " reports=" + snapshot.DigestReports + " resident=" + m_bridge.ResidentCount +
                       " allocated=" + m_bridge.AllocatedCount +
                       " pendingMarks=" + snapshot.PendingMarks + " pendingClients=" + snapshot.PendingClients +
                       " clientMemChunks=" + snapshot.MemoryChunkCount + " fullSets=" +
                       snapshot.ClientChunkFullSets + " chunkDeltas=" + snapshot.ClientChunkDeltaMessages +
                       " chunkRejects=" + snapshot.ClientChunkRejects +
                       " memDeliver=" + snapshot.CellChangesMemoryRecipients +
                       " memOnly=" + snapshot.CellChangesMemoryOnly +
                       " outsideWindow=" + snapshot.CellChangesOutsideWindow +
                       " repairs=" + snapshot.CellChangeRepairs +
                       " repairUnavailable=" + snapshot.CellChangeRepairUnavailable +
                       " staleVerdicts=" + snapshot.StaleVerdictRefreshRequested +
                       "/" + snapshot.StaleVerdictRefreshed + "/" + snapshot.StaleVerdictCleared +
                       // v9：实时地图主机端动物（最近一次快照条数 / 累计截断条数）
                       " animalsInFeed=" + m_animalsInFeed +
                       " animalsTruncated=" + m_animalsTruncated +
                       " posSent=" + m_positionSnapshotsSent + " posApplied=" + m_positionSnapshotsApplied +
                       " posDropped=" + m_positionSnapshotsDropped +
                       // 逐端裁剪审计：窗口外被裁掉的化身条目 / 还没上报窗口的端
                       " posOutOfWindow=" + m_positionEntriesOutOfWindow +
                       " posWindowUnknown=" + m_positionWindowUnknownPeers;
            }
            if (m_clientSync != null)
            {
                TerrainClientSnapshot snapshot = m_clientSync.GetSnapshot();
                return "terrain-sync: client " + m_wantHost + ":" + m_wantPort +
                       " fresh=" + snapshot.Fresh.Count + " stale=" + snapshot.Stale.Count +
                       " unverified=" + snapshot.Unverified.Count +
                       " queued=" + (snapshot.QueuedDigests + snapshot.QueuedResyncParts +
                           snapshot.QueuedHashRefreshes) +
                       "(digest=" + snapshot.QueuedDigests + "/" + snapshot.DigestBudgetPerTick +
                       ",resync=" + snapshot.QueuedResyncParts + "/" + snapshot.ResyncBudgetPerTick +
                       ",refresh=" + snapshot.QueuedHashRefreshes + "/" + snapshot.HashRefreshBudgetPerTick + ")" +
                       " sentTick=" + snapshot.ReportsThisTick + "/" + snapshot.ReportBudgetPerTick +
                       " digests=" + snapshot.DigestsComputed +
                       " sendDeferred=" + snapshot.ReportSendDeferrals +
                       " memChunks=" + snapshot.MemoryChunkCount +
                       " resident=" + m_bridge.ResidentCount +
                       " memNotResident=" + m_bridge.MemoryNotResident +
                       " residentFinalized=" + m_bridge.ResidentFinalized +
                       " chunkMsgs=" + snapshot.ClientChunkMessagesSent + "/fullSets=" +
                       snapshot.ClientChunkFullSetsSent + "/queued=" + snapshot.QueuedClientChunkMessages +
                       "/budget=" + snapshot.ClientChunkBudgetPerTick +
                       " chunkDeferred=" + snapshot.ClientChunkSendDeferrals +
                       " deliveryIndex=" + snapshot.LastDeliveryIndex +
                       " gaps=" + snapshot.DeliveryGaps + " outOfOrder=" + snapshot.OutOfOrderChanges +
                       " heldLive=" + snapshot.HeldLiveChanges + " memVerify=" +
                       snapshot.MemoryVerificationsSent + "/" + snapshot.QueuedMemoryVerifications +
                       " droppedUnloaded=" + (snapshot.DigestsDroppedUnloaded + snapshot.ReportsDroppedUnloaded +
                           snapshot.ResyncDroppedUnloaded) +
                       // 兴趣半径（实际发出的值，与客户端可见范围对齐）：状态行可直接对数。
                       " interestRadius=" + m_interestRadiusSent +
                       " interestVisibility=" + m_interestVisibilitySent +
                       " interestRadiusChanges=" + m_interestRadiusChanges +
                       " interestSends=" + m_interestSends +
                       " posSent=" + m_positionSnapshotsSent + " posApplied=" + m_positionSnapshotsApplied +
                       " posDropped=" + m_positionSnapshotsDropped +
                       // 客户端角色但连接不在时，本地写入走不到主机（没有接收者）的格数。
                       " hostCellNoHost=" + m_hostCellNoHost +
                       // 客户端副本闸门 / 塌落闸门（按来源计数；collapsingGated=本地塌落被拒绝，
                       // collapsingSkipped=移动组落地/清表被跳过 —— 最终格只收主机 CellChange）。
                       " · 副本闸门 " + (m_shell != null ? m_shell.Gate.Describe() : "gate=none") +
                       " · 塌落 " + (m_shell != null ? m_shell.CollapsingState.Describe() : "collapsing=none") +
                       // 活塞闸门：摘掉的 base 写格订阅 / 拦下的 base 写格路径 / 移除的残留表现集合。
                       " · 活塞 " + (m_shell != null ? m_shell.PistonState.Describe() : "piston=none");
            }
            if (m_wantClient)
                return "terrain-sync: client 连接中 " + m_wantHost + ":" + m_wantPort +
                       "（第 " + m_reconnectAttempts + " 次重试）";
            return "terrain-sync: 未开通道（等游戏内 CR / Play 屏点房间）";
        }

        private void Log(string message)
        {
            if (m_context != null) m_context.Diagnostics.Info(m_descriptor.Id, message);
            else global::Engine.Log.Information("[SuMP] " + message);
        }

        /// <summary>需要"如实说明失败原因"的行走 Warning（不静默、也不当普通信息淹没）。</summary>
        private void LogWarning(string message)
        {
            if (m_context != null) m_context.Diagnostics.Warn(m_descriptor.Id, message);
            else global::Engine.Log.Warning("[SuMP] " + message);
        }

        /// <summary>同一条持续性问题只写一行（原因照实写，不静默也不刷屏）。</summary>
        private void LogOnce(string key, string message)
        {
            if (!m_reportedFrameProblems.Add(key)) return;
            Log(message);
        }
    }
}
