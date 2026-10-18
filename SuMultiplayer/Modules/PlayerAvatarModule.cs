using System;
using System.Collections.Generic;
using System.Linq;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
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
    public sealed class PlayerAvatarModule : IMultiplayerModule, IAvatarTerrainLink, IHelloProfileGate
    {
        /// <summary>主机角色在**本机**通道里的槽位键（旧实现同样用 0）；它只是本机索引，不是身份。
        /// 位置快照批里"主机自己那一条"用的也是这个键（<c>NetworkAvatarSpawner.CapturePlayerPositionBatch</c>）。</summary>
        internal const int HostAvatarSlotKey = 0;

        /// <summary>主机侧角色记录刷新/落盘周期（旧实现 PlayerRecordSaveInterval = 5 秒）。</summary>
        private const long RecordRefreshIntervalMs = 5000;

        /// <summary>创建失败后的重试最小间隔（旧实现每帧重试；这里限制日志频率，不改变失败语义）。</summary>
        private const long CreateRetryIntervalMs = 2000;

        /// <summary>主机自己的资料轮询间隔（衣服会变；签名不变就不广播）。</summary>
        private const long HostProfilePollIntervalMs = 1000;

        private sealed class PendingPeer
        {
            public int ClientId;
            public string Name;
            public string Identity;
            public string RecordKey;
            public PlayerProfileSnapshot Profile;
            public long LastAttemptMs;
        }

        private readonly ModuleDescriptor m_descriptor;
        private readonly NetworkAvatarSpawner m_spawner;
        private readonly SuMultiplayerPlayersXml m_records;
        private readonly LocalPlayerProfileStore m_profileStore;
        private readonly Dictionary<int, PendingPeer> m_pendingPeers =
            new Dictionary<int, PendingPeer>();
        /// <summary>记录键 → 拥有它的 clientId（身份冲突检测：同一个身份不许有两个角色）。</summary>
        private readonly Dictionary<string, int> m_recordKeyOwner =
            new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>clientId → 身份（广播 AvatarRemove / 资料消息用；只在本机内存）。</summary>
        private readonly Dictionary<int, string> m_peerIdentities = new Dictionary<int, string>();
        /// <summary>clientId → 主机采用/最近一次收到的资料（建角色与广播用）。</summary>
        private readonly Dictionary<int, PlayerProfileSnapshot> m_peerProfiles =
            new Dictionary<int, PlayerProfileSnapshot>();
        /// <summary>客户端：已经应用到引擎的角色资料签名（相同就不重复写引擎）。</summary>
        private readonly Dictionary<int, string> m_appliedAvatarSignatures = new Dictionary<int, string>();
        /// <summary>客户端远端化身名册（主机广播的身份集合；按身份去重，重连不重复）。</summary>
        private readonly AvatarRoster m_roster = new AvatarRoster();
        /// <summary>同一条持续性问题只报一次（原因照实写，不静默）。</summary>
        private readonly HashSet<string> m_reportedProblems = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>主机自己的资料签名（变了才广播）。</summary>
        private string m_hostProfileSignature = string.Empty;
        /// <summary>客户端：本机资料最近一次发给主机的签名（变了才发）。</summary>
        private string m_localProfileSignature = string.Empty;
        /// <summary>客户端：最近一次应用到本机玩家的资料签名。</summary>
        private string m_appliedSelfSignature = string.Empty;
        /// <summary>主机：最近一次整表广播的签名（避免重复刷）。</summary>
        private string m_lastRosterSignature = string.Empty;

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
        private long m_lastHostProfilePollMs;
        private long m_createdHostAvatars;
        private long m_createdClientAvatars;
        /// <summary>因为对端连接失效而摘掉的客户端角色数（状态行可见，实机排查用）。</summary>
        private long m_removedClientAvatars;
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

        public PlayerAvatarModule(NetworkAvatarSpawner spawner, SuMultiplayerPlayersXml records,
            LocalPlayerProfileStore profileStore = null)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_records = records ?? throw new ArgumentNullException(nameof(records));
            m_profileStore = profileStore;
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
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                if (m_project != null)
                {
                    // 换了世界：旧世界的 PlayerData 已随 Project 销毁，必须丢掉引用。
                    Info("检测到 Project 更换：丢掉旧世界的联机角色引用（玩家端 " + m_spawner.Count +
                        " 个）；仍在挂起队列里的加入请求会照常在**新世界**里创建");
                    m_spawner.ForgetAll();
                    m_roster.Clear();
                    m_appliedAvatarSignatures.Clear();
                    m_appliedSelfSignature = string.Empty;
                    m_lastRosterSignature = string.Empty;
                    m_localProfileSignature = string.Empty;
                }
                m_project = project;
                m_hostAvatarCreated = false;
                m_profilePromptShown = false;
                m_reportedProblems.Clear();
                m_lastRefreshMs = 0;
                m_lastHostProfilePollMs = 0;
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
                    }
                    m_roster.LocalClientId = -1;
                }
                else if (m_roster.LocalClientId != localClientId)
                {
                    m_roster.LocalClientId = localClientId;
                    SeedSelfFlagFromRoster();
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
            BroadcastHostProfileIfChanged(now);
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

            string recordKey = PlayerIdentity.RecordKey(trimmedIdentity, name);
            if (m_recordKeyOwner.TryGetValue(recordKey, out int owner) && owner != clientId)
            {
                m_rejectedPeers++;
                Error("拒绝创建角色：clientId=" + clientId + " 的身份 " +
                    PlayerIdentity.Describe(recordKey) + " 已经被 clientId=" + owner +
                    " 占用（同一个身份只能有一个联机角色）；不建第二个角色（绝不静默合并）" +
                    " —— 若对端其实是断线重连，说明它的旧连接还没被判失效（链路失效清理只在" +
                    " TerrainSyncHost 每 tick 的可达性检查里做）");
                return;
            }
            m_recordKeyOwner[recordKey] = clientId;
            m_peerIdentities[clientId] = trimmedIdentity;
            m_peerProfiles.TryGetValue(clientId, out PlayerProfileSnapshot profile);
            string resolvedName = profile != null && profile.HasName ? profile.Name : (name ?? string.Empty);
            // 同一个身份只写一行名字解析（rate-limited by identity；不含 GUID 全值）。
            ResolveName(recordKey, resolvedName, "Hello/主机角色记录");
            m_pendingPeers[clientId] = new PendingPeer
            {
                ClientId = clientId,
                Name = resolvedName,
                Identity = trimmedIdentity,
                RecordKey = recordKey,
                Profile = profile,
                LastAttemptMs = 0
            };
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
            m_pendingPeers.Remove(clientId);
            m_peerIdentities.TryGetValue(clientId, out string identity);
            string releasedKey = null;
            foreach (KeyValuePair<string, int> pair in m_recordKeyOwner)
            {
                if (pair.Value != clientId) continue;
                releasedKey = pair.Key;
                break;
            }
            if (releasedKey != null)
            {
                // 先抓一份还活着的角色记录再摘实体（摘掉之后就读不到了）。
                m_records.TryGet(releasedKey, out NetworkPlayerRecord previous);
                NetworkPlayerRecord captured = CaptureForStorage(clientId, previous);
                if (captured != null)
                {
                    m_records.Put(releasedKey, captured);
                    m_records.SaveIfDirty();
                }
                m_recordKeyOwner.Remove(releasedKey);
            }
            bool removed = m_spawner.Remove(clientId);
            if (removed)
            {
                m_removedClientAvatars++;
                m_avatarsRemoved++;
                m_appliedAvatarSignatures.Remove(clientId);
            }
            m_peerProfiles.Remove(clientId);
            m_peerIdentities.Remove(clientId);
            Warn("玩家端 clientId=" + clientId + " 的通道已失效（" + (reason ?? string.Empty) +
                "）→ " + (removed
                    ? "已把它的联机角色从世界里摘掉（否则那个角色会停在最后一个已应用的位置上，重连后还会多出一个）"
                    : "本机没有它的联机角色（本来就没建起来，无需摘除）") +
                (releasedKey != null
                    ? "；角色记录已保存并释放记录键 " + PlayerIdentity.Describe(releasedKey) + "（重连可重新建角色）"
                    : string.Empty));
            // 通知**所有剩下的客户端**：把它从名册里摘掉（否则别人世界里会留下一个不动的化身）。
            BroadcastAvatarRemove(clientId, identity, reason);
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
                        ErrorOnce("record-missing:" + peer.ClientId,
                            "clientId=" + peer.ClientId + " 没有角色记录且资料不完整（" +
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
                if (!m_spawner.TryCreate(peer.ClientId, record.Name, peer.RecordKey, record, isHost: true,
                    out string reason))
                {
                    // 明确失败：保留在挂起队列里按间隔重试，并写清原因（不假装成功）。
                    Error("创建客户端角色失败（保留在挂起队列按 " + CreateRetryIntervalMs +
                        " ms 重试）：clientId=" + peer.ClientId + " · 记录键=" +
                        PlayerIdentity.Describe(peer.RecordKey) + " · 原因=" + reason);
                    continue;
                }
                m_createdClientAvatars++;
                m_avatarsCreated++;
                if (!string.IsNullOrEmpty(record.SkinName)) m_avatarAppearanceApplied++;
                NetworkPlayerRecord captured = CaptureForStorage(peer.ClientId, record);
                if (captured != null) m_records.Put(peer.RecordKey, captured);
                m_records.SaveIfDirty();
                m_pendingPeers.Remove(peer.ClientId);
                m_peerProfiles[peer.ClientId] = RecordToProfile(captured ?? record);
                Info("已创建客户端角色：clientId=" + peer.ClientId + " · 名字=" + record.Name +
                    " · 性别=" + record.PlayerClass + " · 皮肤=" + record.SkinName +
                    " · 记录键=" + PlayerIdentity.Describe(peer.RecordKey));
                // 新角色建好 → 让所有客户端（含刚加入的那个）都拿到他的真实资料。
                BroadcastProfile(peer.ClientId, m_peerProfiles[peer.ClientId], isHost: false);
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
            foreach (int clientId in m_spawner.ClientIds.ToArray())
            {
                if (!m_spawner.TryGetRecordKey(clientId, out string recordKey)) continue;
                m_records.TryGet(recordKey, out NetworkPlayerRecord previous);
                NetworkPlayerRecord captured = CaptureForStorage(clientId, previous);
                if (captured == null) continue;
                string previousSignature = previous != null
                    ? RecordToProfile(previous).Signature
                    : string.Empty;
                string capturedSignature = RecordToProfile(captured).Signature;
                m_records.Put(recordKey, captured);
                changed = true;
                m_peerProfiles[clientId] = RecordToProfile(captured);
                if (!string.Equals(previousSignature, capturedSignature, StringComparison.Ordinal))
                {
                    m_avatarsUpdated++;
                    Info("联机角色资料变化：clientId=" + clientId + " · 名字=" + captured.Name +
                        " · 性别=" + captured.PlayerClass + " · 皮肤=" + captured.SkinName +
                        " → 广播给所有客户端");
                    BroadcastProfile(clientId, m_peerProfiles[clientId], isHost: false);
                    BroadcastRoster("客户端资料变化");
                }
            }
            if (changed) m_records.SaveIfDirty();
        }

        /// <summary>主机：本机玩家资料变化（名字/皮肤/衣服）→ 广播给所有客户端。</summary>
        private void BroadcastHostProfileIfChanged(double now)
        {
            long nowMs = (long)(now * 1000.0);
            if (nowMs - m_lastHostProfilePollMs < HostProfilePollIntervalMs) return;
            m_lastHostProfilePollMs = nowMs;
            if (!m_spawner.TryCaptureLocalProfile(out PlayerProfileSnapshot profile, out string reason))
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("host-profile-unavailable",
                    "主机自己的资料取不到（" + reason + "）→ 客户端会明确记录\"主机外观未知\"，" +
                    "不会伪造性别/皮肤（检查主机是否已经进入世界并有本机角色）");
                return;
            }
            string signature = profile.Signature;
            if (string.Equals(signature, m_hostProfileSignature, StringComparison.Ordinal)) return;
            m_hostProfileSignature = signature;
            ResolveName(PlayerIdentity.RecordKey(PlayerIdentity.LocalIdentity(), profile.Name),
                profile.Name, "主机本机角色 PlayerData.Name");
            Info("主机资料（名字=" + profile.Name + " · 性别=" + profile.Appearance.Sex +
                " · 皮肤=" + profile.Appearance.SkinName + "）已变化 → 广播给所有客户端");
            BroadcastProfile(HostAvatarSlotKey, profile, isHost: true, identity: PlayerIdentity.LocalIdentity());
            BroadcastRoster("主机资料变化");
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
                m_peerProfiles[clientId] = recordProfile;
                m_peerIdentities[clientId] = trimmedIdentity;
                decision.HasCharacter = true;
                decision.Name = record.Name ?? string.Empty;
                decision.Profile = recordProfile;
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
            m_peerProfiles[clientId] = profile;
            m_peerIdentities[clientId] = trimmedIdentity;
            decision.HasCharacter = false;
            decision.Name = record.Name;
            decision.Profile = profile;
            decision.Reason = "按客户端 Hello.profile 新建角色记录（首次加入）";
            Info("身份 " + PlayerIdentity.Describe(recordKey) + " 首次加入 → 用 Hello.profile 建角色记录" +
                "（名字 " + record.Name + " · 性别 " + record.PlayerClass + " · 皮肤 " + record.SkinName + "）");
            return decision;
        }

        /// <summary>主机自己的资料（HelloAck.host_profile）。</summary>
        public bool TryGetHostProfile(out PlayerProfileSnapshot profile, out string reason) =>
            m_spawner.TryCaptureLocalProfile(out profile, out reason);

        // ---------------------------------------------------------------- 资料消息（收发）

        /// <summary>
        /// 资料消息：主机收到客户端自己的资料 → 更新记录/化身并广播；
        /// 客户端收到主机/其他客户端资料 → 写进名册（主机的 client_id=0）。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:763-849
        /// </summary>
        private void OnPlayerProfileMessage(MessageContext context, Proto.PlayerProfile message)
        {
            PlayerProfileSnapshot profile = PlayerProfileCodec.FromMessage(message, out int clientId,
                out string identity, out bool isHost, out bool hasCharacter);
            if (profile == null)
            {
                ErrorOnce("profile-null", "收到空资料消息 → 丢弃（不猜任何字段）");
                return;
            }
            bool hosting = m_broadcast?.IsHosting == true;
            if (hosting)
            {
                HandleClientProfileMessage(context.ClientId, clientId, identity, profile);
                return;
            }
            // 客户端：主机广播的 host（clientId=0）/ 其他客户端 / 本机自己的记录。
            if (clientId < 0)
            {
                ErrorOnce("profile-bad-client-id", "收到 clientId=" + clientId + " 的资料消息 → 丢弃");
                return;
            }
            if (clientId == m_transport?.LocalClientId)
            {
                m_profileStore?.Set(profile, "host-record");
                // 世界还没加载时只存资料；等 Project 就绪后由 TickClient 应用到本机角色
                // （这里不报"应用失败"，那只是正常时序）。
                if (GameManager.Project != null) ApplySelfProfileFromStore();
                return;
            }
            IReadOnlyList<AvatarRosterChange> changes = m_roster.Apply(clientId, identity,
                profile.Name, profile.Appearance, isHost, "host-broadcast");
            ApplyRosterChanges(changes, "资料广播");
        }

        private void HandleClientProfileMessage(int sourceClientId, int claimedClientId,
            string identity, PlayerProfileSnapshot profile)
        {
            if (sourceClientId <= 0) return;
            if (claimedClientId != 0 && claimedClientId != sourceClientId)
            {
                m_rejectedPeers++;
                ErrorOnce("profile-spoof:" + sourceClientId,
                    "clientId=" + sourceClientId + " 的资料自称 clientId=" + claimedClientId +
                    " → 拒绝（不允许冒领另一个身份的资料）");
                return;
            }
            string localIdentity = PlayerIdentity.LocalIdentity();
            if (!string.IsNullOrEmpty(identity) &&
                string.Equals(identity, localIdentity, StringComparison.Ordinal))
            {
                m_rejectedPeers++;
                ErrorOnce("profile-host-identity:" + sourceClientId,
                    "clientId=" + sourceClientId + " 的资料用了主机自己的身份 → 拒绝");
                return;
            }
            if (!IsCompleteProfile(profile))
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("profile-invalid:" + sourceClientId,
                    "clientId=" + sourceClientId + " 上报的资料不完整/非法（名字 " +
                    (profile.Name ?? string.Empty) + " · 皮肤 " + (profile.Appearance?.SkinName ?? string.Empty) +
                    "）→ 保留旧资料，不拿默认值覆盖");
                return;
            }
            string recordKey = m_spawner.TryGetRecordKey(sourceClientId, out string existingKey)
                ? existingKey
                : PlayerIdentity.RecordKey(identity, profile.Name);
            if (m_records.TryGet(recordKey, out NetworkPlayerRecord record))
            {
                string before = RecordToProfile(record).Signature;
                ApplyProfileToRecord(record, profile);
                m_records.Put(recordKey, record);
                m_records.SaveIfDirty();
                string after = RecordToProfile(record).Signature;
                if (string.Equals(before, after, StringComparison.Ordinal)) return;
            }
            m_peerProfiles[sourceClientId] = profile;
            m_peerIdentities[sourceClientId] = identity ?? string.Empty;
            ResolveName(recordKey, profile.Name, "客户端自行上报（PlayerProfile）");
            // 化身已存在 → 就地更新；性别变了 → 摘掉重建（旧实现同样重建）。
            if (m_spawner.TryGet(sourceClientId, out _))
            {
                if (m_spawner.TryApplyRemoteProfile(sourceClientId, profile,
                        out bool mustRecreate, out string reason))
                {
                    m_avatarsUpdated++;
                    m_avatarAppearanceApplied++;
                    Info("已更新客户端角色外观：clientId=" + sourceClientId + " · 名字=" + profile.Name +
                        " · 性别=" + profile.Appearance.Sex + " · 皮肤=" + profile.Appearance.SkinName);
                }
                else if (mustRecreate)
                {
                    Info("客户端角色性别变化（clientId=" + sourceClientId + "）：摘掉旧化身并按新资料重建 —— " + reason);
                    m_spawner.Remove(sourceClientId);
                    m_avatarsRemoved++;
                    m_appliedAvatarSignatures.Remove(sourceClientId);
                    if (m_records.TryGet(recordKey, out NetworkPlayerRecord freshRecord))
                    {
                        if (m_spawner.TryCreate(sourceClientId, freshRecord.Name, recordKey, freshRecord,
                            isHost: true, out string createReason))
                        {
                            m_avatarsCreated++;
                            m_avatarAppearanceApplied++;
                        }
                        else
                        {
                            // 明确失败：留在挂起队列按间隔重试（不假装重建成功、也不丢这个玩家）。
                            ErrorOnce("profile-recreate:" + sourceClientId,
                                "按新资料重建客户端角色失败（clientId=" + sourceClientId + "）：" + createReason +
                                " → 保留在挂起队列按 " + CreateRetryIntervalMs + " ms 重试");
                            m_pendingPeers[sourceClientId] = new PendingPeer
                            {
                                ClientId = sourceClientId,
                                Name = profile.Name,
                                Identity = identity ?? string.Empty,
                                RecordKey = recordKey,
                                Profile = profile,
                                LastAttemptMs = 0
                            };
                        }
                    }
                }
                else
                {
                    ErrorOnce("profile-apply:" + sourceClientId,
                        "更新客户端角色外观失败（clientId=" + sourceClientId + "）：" + reason);
                }
            }
            BroadcastProfile(sourceClientId, profile, isHost: false, identity: identity);
            BroadcastRoster("客户端资料上报");
            // 客户端自己的角色记录也回给它（名字/性别/皮肤/衣服以主机记录为准）。
            if (recordKey.Length > 0 && m_records.TryGet(recordKey, out NetworkPlayerRecord assigned))
                SendAssignedProfile(sourceClientId, assigned);
        }

        /// <summary>客户端：主机广播"某人离开" → 摘掉对应化身（按身份去重，重连也不会留下旧的）。</summary>
        private void OnAvatarRemoveMessage(MessageContext context, Proto.AvatarRemove message)
        {
            if (m_broadcast?.IsHosting == true) return;    // 主机不应收到这个方向的消息
            string reason = message.Reason ?? string.Empty;
            // **只按连接编号摘除**：同一身份重连后 identity 已经指向新槽位，
            // 旧连接迟到的"离开"如果按身份摘除，会把刚建好的新化身也删掉。
            // 真的漏了也不要紧：主机在离开事件后还会广播整表，客户端会对齐。
            IReadOnlyList<AvatarRosterChange> changes = m_roster.Remove(message.ClientId, reason);
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
            var incoming = new HashSet<int>();
            var changes = new List<AvatarRosterChange>();
            for (int i = 0; i < message.Avatars.Count; i++)
            {
                Proto.PlayerProfile item = message.Avatars[i];
                if (item == null) continue;
                PlayerProfileSnapshot profile = PlayerProfileCodec.FromMessage(item,
                    out int clientId, out string identity, out bool isHost, out bool hasCharacter);
                if (profile == null || !profile.HasName || clientId < 0)
                {
                    m_avatarAppearanceUnavailable++;
                    ErrorOnce("roster-item:" + clientId,
                        "名册里 clientId=" + clientId + " 的资料不完整（身份 " +
                        PlayerIdentity.Describe(PlayerIdentity.RecordKey(identity, profile?.Name)) +
                        "）→ 不建这个化身（绝不拿默认名字/皮肤兜底）");
                    continue;
                }
                if (clientId == m_transport?.LocalClientId)
                {
                    m_profileStore?.Set(profile, "host-roster");
                    if (GameManager.Project != null) ApplySelfProfileFromStore();
                    continue;
                }
                incoming.Add(clientId);
                IReadOnlyList<AvatarRosterChange> applied = m_roster.Apply(clientId, identity,
                    profile.Name, profile.Appearance, isHost, "host-roster");
                for (int j = 0; j < applied.Count; j++) changes.Add(applied[j]);
            }
            // 名册是**完整集合**：不在里面的旧条目一律摘掉（离开消息丢失 / 重连换连接都不会留下幽灵化身）。
            IReadOnlyList<AvatarRosterEntry> existing = m_roster.Entries;
            for (int i = 0; i < existing.Count; i++)
            {
                AvatarRosterEntry entry = existing[i];
                if (incoming.Contains(entry.ClientId)) continue;
                IReadOnlyList<AvatarRosterChange> removed = m_roster.Remove(entry.ClientId,
                    "不在主机完整名册里（离开 / 重连换连接）");
                for (int j = 0; j < removed.Count; j++) changes.Add(removed[j]);
            }
            ApplyRosterChanges(changes, "完整名册");
        }

        // ---------------------------------------------------------------- 客户端名册 → 引擎动作

        private void ApplyRosterChanges(IReadOnlyList<AvatarRosterChange> changes, string why)
        {
            if (changes == null) return;
            for (int i = 0; i < changes.Count; i++)
            {
                AvatarRosterChange change = changes[i];
                if (change.Action != AvatarRosterAction.Remove) continue;
                if (m_spawner.Remove(change.ClientId))
                {
                    m_avatarsRemoved++;
                    m_appliedAvatarSignatures.Remove(change.ClientId);
                    Info("已摘除远端化身：clientId=" + change.ClientId +
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
            var seen = new HashSet<int>();
            for (int i = 0; i < entries.Count; i++)
            {
                AvatarRosterEntry entry = entries[i];
                seen.Add(entry.ClientId);
                if (entry.IsSelf)
                {
                    // 本机自己的资料绝不建远端化身（旧实现里也是应用到本机玩家）。
                    if (m_spawner.Remove(entry.ClientId))
                    {
                        m_avatarsRemoved++;
                        m_appliedAvatarSignatures.Remove(entry.ClientId);
                        Info("名册把 clientId=" + entry.ClientId + " 标为本机自己 → 摘掉误建的自身化身");
                    }
                    continue;
                }
                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    m_avatarAppearanceUnavailable++;
                    ErrorOnce("avatar-name-missing:" + entry.ClientId,
                        "名册里 clientId=" + entry.ClientId + "（身份 " +
                        PlayerIdentity.Describe(PlayerIdentity.RecordKey(entry.Identity, entry.Name)) +
                        "）没有名字 → 不建/不更新这个化身（绝不拿 \"Player\"/\"Android User\" 兜底）");
                    continue;
                }
                bool exists = m_spawner.TryGet(entry.ClientId, out _);
                string signature = entry.Signature;
                if (exists && m_appliedAvatarSignatures.TryGetValue(entry.ClientId, out string applied) &&
                    string.Equals(applied, signature, StringComparison.Ordinal))
                    continue;
                if (!exists)
                {
                    TryCreateClientAvatar(entry);
                    continue;
                }
                if (m_spawner.TryApplyRemoteProfile(entry.ClientId, ToSnapshot(entry),
                        out bool mustRecreate, out string reason))
                {
                    m_appliedAvatarSignatures[entry.ClientId] = signature;
                    m_avatarsUpdated++;
                    m_avatarAppearanceApplied++;
                    Info("已应用远端化身外观：clientId=" + entry.ClientId + " · 名字=" + entry.Name +
                        " · 性别=" + (entry.Appearance?.Sex ?? PlayerSex.Male) +
                        " · 皮肤=" + (entry.Appearance?.SkinName ?? string.Empty));
                }
                else if (mustRecreate)
                {
                    Info("远端化身性别变化（clientId=" + entry.ClientId + "）：摘掉旧化身并按新资料重建 —— " + reason);
                    if (m_spawner.Remove(entry.ClientId))
                    {
                        m_avatarsRemoved++;
                        m_appliedAvatarSignatures.Remove(entry.ClientId);
                    }
                    TryCreateClientAvatar(entry);
                }
                else
                {
                    ErrorOnce("avatar-apply:" + entry.ClientId,
                        "应用远端化身外观失败（clientId=" + entry.ClientId + "）：" + reason);
                }
            }
            // 名册里已经不存在、但引擎里还留着的化身（离开消息丢失 / 主机没发）→ 摘掉。
            foreach (int clientId in m_spawner.ClientIds.ToArray())
            {
                if (seen.Contains(clientId)) continue;
                if (m_spawner.Remove(clientId))
                {
                    m_avatarsRemoved++;
                    m_appliedAvatarSignatures.Remove(clientId);
                    Info("远端化身不在主机名册里 → 已摘除：clientId=" + clientId);
                }
            }
        }

        private bool TryCreateClientAvatar(AvatarRosterEntry entry)
        {
            PlayerProfileSnapshot profile = ToSnapshot(entry);
            string recordKey = PlayerIdentity.RecordKey(entry.Identity, entry.Name);
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
            if (!m_spawner.TryCreate(entry.ClientId, entry.Name, recordKey, record, isHost: false,
                    out string reason))
            {
                ErrorOnce("avatar-create:" + entry.ClientId,
                    "创建远端化身失败（每帧重试，不刷屏）：clientId=" + entry.ClientId +
                    " · 记录键=" + PlayerIdentity.Describe(recordKey) + " · 原因=" + reason);
                return false;
            }
            m_avatarsCreated++;
            if (!string.IsNullOrEmpty(record.SkinName)) m_avatarAppearanceApplied++;
            else
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("avatar-skin-missing:" + entry.ClientId,
                    "clientId=" + entry.ClientId + " 的资料没有皮肤名 → 引擎会按默认外观创建" +
                    "（明确记录：这不是该玩家的真实皮肤；主机应尽快广播完整资料）");
            }
            m_appliedAvatarSignatures[entry.ClientId] = entry.Signature;
            ResolveName(recordKey, entry.Name, entry.IsHost ? "HelloAck.host_profile" : "主机广播资料");
            Info("已创建远端化身：clientId=" + entry.ClientId + " · 名字=" + entry.Name +
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
                    (Action<string, PlayerClass, string>)OnCharacterCreated,
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

        private void OnCharacterCreated(string name, PlayerClass playerClass, string skinName)
        {
            string trimmed = (name ?? string.Empty).Trim();
            if (!PlayerData.VerifyName(trimmed))
            {
                Error("角色创建返回的名字非法（\"" + trimmed + "\"）→ 不提交；请重新创建");
                m_profilePromptShown = false;
                return;
            }
            if (string.IsNullOrWhiteSpace(skinName))
            {
                Error("角色创建没有返回皮肤 → 不提交（绝不拿默认皮肤伪造资料）");
                m_profilePromptShown = false;
                return;
            }
            var profile = new PlayerProfileSnapshot
            {
                Name = trimmed,
                Appearance = new PlayerAppearance
                {
                    Sex = NetworkAvatarSpawner.ToPlayerSex(playerClass),
                    SkinName = skinName,
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
            Info("角色创建完成：名字=" + profile.Name + " · 性别=" + playerClass + " · 皮肤=" + skinName +
                " → 重新握手，主机据此建角色记录并广播给其他客户端");
            if (m_handshake == null || !m_handshake.ReconnectClient("角色创建完成"))
                Error("角色创建完成后无法重新握手（没有可重连的客户端通道）→ 请从 Play 屏重新加入房间；" +
                    "资料已保存在本机资料存储里，不会丢");
        }

        // ---------------------------------------------------------------- 客户端侧

        private void TickClient(Project project)
        {
            if (project == null) return;
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
                string stored = m_profileStore?.Profile?.Signature ?? string.Empty;
                if (!string.Equals(stored, assigned.Signature, StringComparison.Ordinal))
                {
                    m_profileStore?.Set(assigned, "host-record");
                    Info("主机分配的本机角色资料：名字=" + assigned.Name + " · 性别=" +
                        assigned.Appearance.Sex + " · 皮肤=" + assigned.Appearance.SkinName +
                        " → 应用到本机角色（下载世界自带的角色不会冒充自己）");
                }
            }

            // 主机化身：来自 HelloAck.host_profile（名字/性别/皮肤/衣服都是主机的真实资料）。
            SeedHostAvatarFromHandshake();

            // 本机玩家 = 自己的角色（名字/性别/皮肤/衣服）；性别不同会按旧实现替换 PlayerData。
            ApplySelfProfileFromStore();

            // 主机 + 其他每个客户端：每个身份都要有一个化身（这是客户端互相可见的唯一入口）。
            ReconcileClientAvatars();
            if (!m_hostAvatarCreated && m_roster.TryGet(HostAvatarSlotKey, out _) &&
                m_spawner.TryGet(HostAvatarSlotKey, out _))
            {
                m_hostAvatarCreated = true;
                m_createdHostAvatars++;
            }

            // 把本机玩家的真实资料上报给主机（名字/皮肤/衣服变了才发；主机再广播给其他客户端）。
            SendLocalProfileIfChanged();
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
                if (entries[i].ClientId == m_roster.LocalClientId) entries[i].IsSelf = true;
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
            IReadOnlyList<AvatarRosterChange> changes = m_roster.Apply(HostAvatarSlotKey, hostIdentity,
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
                return;
            }
            if (!m_spawner.TryCaptureLocalProfile(out PlayerProfileSnapshot profile, out string reason))
            {
                ErrorOnce("local-profile-missing", "本机玩家资料取不到（" + reason + "）→ 本帧不上报");
                return;
            }
            string signature = profile.Signature;
            if (string.Equals(signature, m_localProfileSignature, StringComparison.Ordinal)) return;
            m_localProfileSignature = signature;
            if (m_messages == null)
            {
                ErrorOnce("local-profile-no-messages",
                    "没有消息注册表 → 本机资料无法上报给主机（其他客户端会看不到你的真实外观）");
                return;
            }
            Proto.PlayerProfile message = PlayerProfileCodec.ToMessage(0, localIdentity, profile,
                isHost: false, hasCharacter: true);
            if (m_messages.Sender.Send(0, message))
            {
                ResolveName(PlayerIdentity.RecordKey(localIdentity, profile.Name), profile.Name,
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
        /// Source: ScMultiplayerWorldObjectHandlers.cs:481-484
        ///         （旧实现用 m_networkPlayerData.Values 排除网络化身来找"本机玩家"）
        /// </summary>
        public bool IsNetworkAvatar(PlayerData playerData) => m_spawner.Contains(playerData);

        /// <summary>
        /// 主机：某个玩家端的联机角色在世界里的真实位置（实时地图显示客户端坐标用）。
        /// 由持有网络化身表的 <see cref="NetworkAvatarSpawner"/> 提供；读不到就写明原因，
        /// 调用方（实时地图）据此明说"坐标未知"，不按区块中心猜。
        /// </summary>
        public bool TryGetAvatarCell(int clientId, out Vector3 position, out string reason) =>
            m_spawner.TryGetAvatarCell(clientId, out position, out reason);

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
        public void OnRemotePlayerPositionReceived(int clientId, in PlayerPositionSnapshot snapshot)
        {
            m_spawner.ApplyRemotePlayerPosition(clientId, in snapshot);
        }

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
            if (profile.HasName) record.Name = profile.Name.Trim();
            if (profile.Appearance != null)
            {
                record.PlayerClass = NetworkAvatarSpawner.ToPlayerClass(profile.Appearance.Sex);
                if (profile.Appearance.HasSkinName) record.SkinName = profile.Appearance.SkinName;
                record.SkinSha256 = profile.Appearance.SkinSha256 != null
                    ? (byte[])profile.Appearance.SkinSha256.Clone()
                    : Array.Empty<byte>();
                record.Clothes = PlayerAppearance.CloneClothes(profile.Appearance.Clothes);
            }
        }

        private static bool IsUsableAppearance(PlayerProfileSnapshot profile) =>
            profile != null && profile.HasName && profile.Appearance != null &&
            profile.Appearance.HasSkinName;

        /// <summary>
        /// 资料是否完整：名字合法 + 皮肤存在且与性别兼容。**自定义皮肤资源下发尚未移植**，
        /// 所以这里只接受内置皮肤；遇到自定义皮肤明确记录原因（不静默接受再显示错皮肤）。
        /// Source: ScMultiplayerProfileHandlers.cs:34-48（IsValidRequestedProfile）
        /// </summary>
        private bool IsCompleteProfile(PlayerProfileSnapshot profile)
        {
            if (profile == null || !profile.HasName) return false;
            if (!PlayerData.VerifyName(profile.Name.Trim())) return false;
            if (profile.Appearance == null || !profile.Appearance.HasSkinName) return false;
            string skin = profile.Appearance.SkinName.Trim();
            CharacterSkinsManager.UpdateCharacterSkinsList();
            if (!CharacterSkinsManager.IsBuiltIn(skin))
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("custom-skin:" + skin,
                    "资料里的皮肤 \"" + skin + "\" 是自定义皮肤；本重写版还没有皮肤资源下发链路" +
                    "（旧实现 PlayerSkinAssetMessage）→ 明确拒绝这次资料，请选择内置皮肤");
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
        private NetworkPlayerRecord CaptureForStorage(int clientId, NetworkPlayerRecord previous)
        {
            NetworkPlayerRecord captured = m_spawner.Capture(clientId);
            if (captured == null) return null;
            // 统计只有客户端算得准：主机侧的远端化身不跑客户端的挖掘/移动逻辑，
            // 所以这里一律沿用记录里的旧值，等客户端上报（客户端上报那条链尚未移植）。
            captured.Stats = previous?.Stats;
            // 名字是否已确认真实资料是记录级事实：Capture 只抓引擎状态，不能把未确认记录标成已确认。
            captured.NameConfirmed = previous?.NameConfirmed ?? captured.NameConfirmed;
            // 皮肤资源下发（自定义皮肤哈希）尚未移植：沿用记录里已有的哈希，不伪造新值。
            captured.SkinSha256 = previous?.SkinSha256 ?? Array.Empty<byte>();
            return captured;
        }

        // ---------------------------------------------------------------- 广播

        private void BroadcastProfile(int clientId, PlayerProfileSnapshot profile, bool isHost,
            string identity = null)
        {
            if (m_broadcast == null || profile == null) return;
            string peerIdentity = identity;
            if (peerIdentity == null)
                m_peerIdentities.TryGetValue(clientId, out peerIdentity);
            Proto.PlayerProfile message = PlayerProfileCodec.ToMessage(clientId,
                peerIdentity ?? string.Empty, profile, isHost, hasCharacter: true);
            int sent = m_broadcast.BroadcastToPlayers(Proto.MessageType.PlayerProfile, message.ToByteArray());
            if (sent == 0)
            {
                // 没有在线玩家端（只有主机 / Debug 端）不是错误；新客户端加入时会重新广播。
                Info("资料广播暂时没有接收方（clientId=" + clientId + " · 名字=" + profile.Name +
                    "）；下一个客户端加入时会随整表广播重发");
            }
        }

        private void SendAssignedProfile(int clientId, NetworkPlayerRecord record)
        {
            if (m_broadcast == null || record == null) return;
            string identity = m_peerIdentities.TryGetValue(clientId, out string value) ? value : string.Empty;
            PlayerProfileSnapshot profile = RecordToProfile(record);
            Proto.PlayerProfile message = PlayerProfileCodec.ToMessage(clientId, identity, profile,
                isHost: false, hasCharacter: true);
            if (!m_transport.TrySendTo(clientId, Proto.MessageType.PlayerProfile, message.ToByteArray()))
            {
                ErrorOnce("assigned-profile-send:" + clientId,
                    "把主机记录里的本机资料回给 clientId=" + clientId + " 失败（通道不可用）→ " +
                    "该客户端会暂时用 Hello 里提交的资料（不会假装已同步）");
            }
        }

        /// <summary>
        /// 主机：把当前房间里所有身份（主机 + 每个客户端）作为**一份完整名册**广播给所有客户端。
        /// 客户端用整表替换而不是增量叠加：重连换连接、离开消息丢失都不会留下幽灵化身。
        /// </summary>
        private void BroadcastRoster(string why, bool force = false)
        {
            if (m_broadcast == null || m_broadcast.IsHosting != true) return;
            var message = new Proto.AvatarRoster();
            var signature = new System.Text.StringBuilder();
            if (m_spawner.TryCaptureLocalProfile(out PlayerProfileSnapshot hostProfile, out string hostReason))
            {
                message.Avatars.Add(PlayerProfileCodec.ToMessage(HostAvatarSlotKey,
                    PlayerIdentity.LocalIdentity(), hostProfile, isHost: true, hasCharacter: true));
                signature.Append(hostProfile.Signature).Append('|');
            }
            else
            {
                m_avatarAppearanceUnavailable++;
                ErrorOnce("roster-host-profile",
                    "整表广播时主机资料不可用（" + hostReason +
                    "）→ 名册里没有主机条目，客户端会明确记录\"主机外观未知\"（不伪造性别/皮肤）");
            }
            int peers = 0;
            foreach (int clientId in m_spawner.ClientIds.ToArray())
            {
                if (clientId == HostAvatarSlotKey) continue;
                if (!m_peerProfiles.TryGetValue(clientId, out PlayerProfileSnapshot profile) ||
                    profile == null || !profile.HasName) continue;
                m_peerIdentities.TryGetValue(clientId, out string identity);
                message.Avatars.Add(PlayerProfileCodec.ToMessage(clientId, identity ?? string.Empty,
                    profile, isHost: false, hasCharacter: true));
                signature.Append(clientId).Append(':').Append(profile.Signature).Append(';');
                peers++;
            }
            string text = signature.ToString();
            if (!force && string.Equals(text, m_lastRosterSignature, StringComparison.Ordinal)) return;
            m_lastRosterSignature = text;
            int sent = m_broadcast.BroadcastToPlayers(Proto.MessageType.AvatarRoster,
                message.ToByteArray());
            Info("已向 " + sent + " 个客户端广播完整房间名册（" + why + "）：主机" +
                (hostProfile != null ? " + " : "（资料不可用，未列入）") + peers + " 个客户端");
        }

        private void BroadcastAvatarRemove(int clientId, string identity, string reason)
        {
            if (m_broadcast == null) return;
            var message = new Proto.AvatarRemove
            {
                ClientId = clientId,
                Identity = identity ?? string.Empty,
                Reason = reason ?? string.Empty
            };
            int sent = m_broadcast.BroadcastToPlayers(Proto.MessageType.AvatarRemove, message.ToByteArray());
            if (sent == 0)
            {
                Info("离开通知暂时没有接收方（clientId=" + clientId + "）；剩下的客户端在下次整表广播时会对齐");
            }
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
                foreach (int clientId in m_spawner.ClientIds.ToArray())
                {
                    if (!m_spawner.TryGetRecordKey(clientId, out string recordKey)) continue;
                    m_records.TryGet(recordKey, out NetworkPlayerRecord previous);
                    NetworkPlayerRecord captured = CaptureForStorage(clientId, previous);
                    if (captured != null) m_records.Put(recordKey, captured);
                }
                m_records.SaveIfDirty();
            }
            m_spawner.ForgetAll();
            m_pendingPeers.Clear();
            m_recordKeyOwner.Clear();
            m_peerIdentities.Clear();
            m_peerProfiles.Clear();
            m_roster.Clear();
            m_appliedAvatarSignatures.Clear();
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
            " posApplied=" + m_spawner.RemotePositionsApplied +
            " posSkipped=" + m_spawner.RemotePositionsSkipped +
            // 主机侧地形更新位置：每个联机角色一条（区块 + 半径 + 内容距离 + 已移动次数）。
            // 实机排查"主机有没有跟着客户端移动"直接读这一行；空表 = none。
            " hostUpdateLocations=" + m_spawner.DescribeHostUpdateLocations() +
            " updateLocationMoves=" + m_spawner.UpdateLocationMoves +
            (m_records.LoadFailed ? " 记录文件损坏(本会话不写盘)" : string.Empty) +
            " rejected=" + m_rejectedPeers;

    }
}
