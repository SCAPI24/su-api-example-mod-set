using System;
using System.Collections.Generic;
using System.Linq;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Players;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 联机角色模块：把**两端的角色实体**建起来，并让它们**按身份**绑定到角色记录。
    ///
    /// 需求（用户口径，逐条对应）：
    ///   ① 主机世界里有客户端的角色、客户端世界里有主机的角色 —— 两端各自的角色实体都建起来；
    ///   ② 角色记录只写联机的角色 xml（<c>&lt;世界目录&gt;/SuMultiplayerPlayers.xml</c>），
    ///      **不污染 Project.xml**（引擎动作见 <see cref="NetworkAvatarSpawner"/> 里的 m_entities.Remove）；
    ///   ③ 绑定**不看角色 id / PlayerIndex / clientId**，只看身份（UserId.dat）；
    ///   ④ 身份 = <c>Game.UserManager.ActiveUser.UniqueId</c>（UserId.dat），见 <see cref="PlayerIdentity"/>。
    ///
    /// 两端各自的时机（照旧实现）：
    ///   · 主机：每个加入的**玩家端**（Debug 只读端不算，过滤在 TerrainSyncModule）建一个客户端角色；
    ///     主机没进世界时先把请求挂起，进世界后再建
    ///     —— Source: ScMultiplayerUpdateLoop.cs:1237-1241（m_pendingNetworkPlayers → CreateNetworkPlayer）；
    ///   · 客户端：进入**下载来的联机世界**后建"主机角色"
    ///     —— Source: ScMultiplayerWorldTransferHandlers.cs:558-562（m_shouldCreateHostAvatar / pending[0]）
    ///          Source: ScMultiplayerUpdateLoop.cs:1242-1243（CreateNetworkPlayer(0, "Host", ...)）。
    ///     旧实现在这里用 <c>GetNetworkRecordKey(0)</c>（"network:0"）当键 —— 那是按 index 兜底绑定，
    ///     本重写版改为用 HelloAck 里收到的主机身份（<see cref="ITransportPort.HostIdentity"/>）。
    ///
    /// 本模块**不含静态状态**，也不直接引用别的模块内部：只从 <see cref="IModuleContext"/> 取端口。
    /// </summary>
    public sealed class PlayerAvatarModule : IMultiplayerModule, IAvatarTerrainLink
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
            public int ClientId;
            public string Name;
            public string Identity;
            public string RecordKey;
            public long LastAttemptMs;
        }

        private readonly ModuleDescriptor m_descriptor;
        private readonly NetworkAvatarSpawner m_spawner;
        private readonly SuMultiplayerPlayersXml m_records;
        private readonly Dictionary<int, PendingPeer> m_pendingPeers = new Dictionary<int, PendingPeer>();
        /// <summary>记录键 → 拥有它的 clientId（身份冲突检测：同一个身份不许有两个角色）。</summary>
        private readonly Dictionary<string, int> m_recordKeyOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> m_reportedProblems = new HashSet<string>(StringComparer.Ordinal);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private IAvatarTerrainBridge m_terrainBridge;
        private IDownloadedWorldPort m_downloadedWorlds;
        private bool m_attached;
        private Project m_project;
        private bool m_hostAvatarCreated;
        private long m_lastRefreshMs;
        private long m_createdHostAvatars;
        private long m_createdClientAvatars;
        /// <summary>因为对端连接失效而摘掉的客户端角色数（状态行可见，实机排查用）。</summary>
        private long m_removedClientAvatars;
        private long m_rejectedPeers;

        public PlayerAvatarModule(NetworkAvatarSpawner spawner, SuMultiplayerPlayersXml records)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_records = records ?? throw new ArgumentNullException(nameof(records));
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
            Info("联机角色模块就绪：主机按对端身份建客户端角色；客户端进联机世界后按主机身份建主机角色");
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
                }
                m_project = project;
                m_hostAvatarCreated = false;
                m_reportedProblems.Clear();
                m_lastRefreshMs = 0;
            }
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
            m_pendingPeers[clientId] = new PendingPeer
            {
                ClientId = clientId,
                Name = name ?? string.Empty,
                Identity = trimmedIdentity,
                RecordKey = recordKey,
                LastAttemptMs = 0
            };
            Info("玩家端加入：clientId=" + clientId + " · 名字=" + (name ?? string.Empty) +
                " · 身份=" + (trimmedIdentity.Length == 0
                    ? "(不可用，按名字绑定)"
                    : PlayerIdentity.Shorten(trimmedIdentity)) +
                " · 记录键=" + PlayerIdentity.Describe(recordKey) +
                (rejoin ? " · 该端声明已有这个世界（重连；世界传输不再重发整图）" : string.Empty));
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
            if (removed) m_removedClientAvatars++;
            Warn("玩家端 clientId=" + clientId + " 的通道已失效（" + (reason ?? string.Empty) +
                "）→ " + (removed
                    ? "已把它的联机角色从世界里摘掉（否则那个角色会停在最后一个已应用的位置上，重连后还会多出一个）"
                    : "本机没有它的联机角色（本来就没建起来，无需摘除）") +
                (releasedKey != null
                    ? "；角色记录已保存并释放记录键 " + PlayerIdentity.Describe(releasedKey) + "（重连可重新建角色）"
                    : string.Empty));
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
                    record = CreateInitialPlayerRecord(peer.Name);
                    m_records.Put(peer.RecordKey, record);
                    Info("为身份 " + PlayerIdentity.Describe(peer.RecordKey) +
                        " 建了新的角色记录（名字 " + record.Name + "；" +
                        "客户端选的角色/皮肤需要 Hello 之外的信息，本次移植的 Hello 只带身份与名字，" +
                        "所以先用引擎默认值，首次出生走引擎自己的 InitialNoIntro 流程）");
                }
                if (!m_spawner.TryCreate(peer.ClientId, peer.Name, peer.RecordKey, record, isHost: true,
                    out string reason))
                {
                    // 明确失败：保留在挂起队列里按间隔重试，并写清原因（不假装成功）。
                    Error("创建客户端角色失败（保留在挂起队列按 " + CreateRetryIntervalMs +
                        " ms 重试）：clientId=" + peer.ClientId + " · 记录键=" +
                        PlayerIdentity.Describe(peer.RecordKey) + " · 原因=" + reason);
                    continue;
                }
                m_createdClientAvatars++;
                NetworkPlayerRecord captured = CaptureForStorage(peer.ClientId, record);
                if (captured != null) m_records.Put(peer.RecordKey, captured);
                m_records.SaveIfDirty();
                m_pendingPeers.Remove(peer.ClientId);
            }
        }

        /// <summary>
        /// 主机：每 5 秒把每个联机角色的当前位置/生命/背包抓进记录并落盘。
        /// Source: ScMultiplayerUpdateLoop.cs:2999-3003（RefreshHostPlayerRecords + SavePlayerRecords）
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
                m_records.Put(recordKey, captured);
                changed = true;
            }
            if (changed) m_records.SaveIfDirty();
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
            // 皮肤资源下发（自定义皮肤哈希）尚未移植：沿用记录里已有的哈希，不伪造新值。
            captured.SkinSha256 = previous?.SkinSha256 ?? Array.Empty<byte>();
            return captured;
        }

        // Source: ScMultiplayerProfileHandlers.cs:50-66（CreateInitialPlayerRecord）
        private static NetworkPlayerRecord CreateInitialPlayerRecord(string requestedName)
        {
            Project project = GameManager.Project;
            SubsystemPlayers players = project?.FindSubsystem<SubsystemPlayers>(false);
            Vector3 position = players?.ComponentPlayers.FirstOrDefault()?.ComponentBody.Position ??
                players?.GlobalSpawnPosition ?? Vector3.Zero;
            string name = (requestedName ?? string.Empty).Trim();
            if (name.Length > 14) name = name.Substring(0, 14);
            return new NetworkPlayerRecord
            {
                Name = string.IsNullOrEmpty(name) ? "Player" : name,
                PlayerClass = PlayerClass.Male,
                SkinName = string.Empty,
                Position = position,
                Level = PlayerRecordCodec.DefaultLevel,
                Health = PlayerRecordCodec.DefaultHealth,
                HasReceivedInitialItems = false
            };
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
            if (m_hostAvatarCreated) return;
            if (m_transport == null || !m_transport.IsConnected) return;
            if (m_downloadedWorlds == null) return;
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(false);
            string directory = gameInfo?.DirectoryName;
            if (string.IsNullOrEmpty(directory)) return;
            // 客户端必须活在**下载来的那个联机世界**里才建主机角色
            // —— Source: ScMultiplayerWorldTransferHandlers.cs:558-562（m_shouldCreateHostAvatar）。
            // 这里是精确判定（世界目录在"下载来的世界"登记表里），不是"只要连着通道就算"。
            if (!m_downloadedWorlds.IsDownloadedWorld(directory)) return;

            string hostIdentity = (m_transport.HostIdentity ?? string.Empty).Trim();
            string hostName = (m_transport.HostName ?? string.Empty).Trim();
            string localIdentity = PlayerIdentity.LocalIdentity();
            if (hostIdentity.Length == 0)
            {
                ErrorOnce("client-no-host-identity",
                    "握手（HelloAck）没有带回主机身份 → 拒绝创建主机角色：" +
                    "旧实现用 GetNetworkRecordKey(0) 那种 index 兜底键，本重写版明确不做；" +
                    "等主机身份到达后会自动重试");
                return;
            }
            if (string.Equals(hostIdentity, localIdentity, StringComparison.Ordinal))
            {
                m_hostAvatarCreated = true;
                m_rejectedPeers++;
                Error("拒绝创建主机角色：握手里的主机身份与本机身份相同（" +
                    PlayerIdentity.Shorten(hostIdentity) + "）—— 同一个 UserId 不可能是两台机器上的两个人");
                return;
            }
            string recordKey = PlayerIdentity.RecordKey(hostIdentity, hostName);
            // 客户端不拥有（也不读）主机写的角色记录文件：旧实现的客户端同样是 record == null，
            // 主机角色的位置/生命/背包由主机的同步消息给（那条链尚未移植，见报告）。
            if (!m_spawner.TryCreate(HostAvatarSlotKey, string.IsNullOrEmpty(hostName) ? "Host" : hostName,
                recordKey, null, isHost: false, out string reason))
            {
                ErrorOnce("client-host-avatar-failed",
                    "创建主机角色失败（每帧重试，不再刷屏）：记录键=" + PlayerIdentity.Describe(recordKey) +
                    " · 原因=" + reason);
                return;
            }
            m_hostAvatarCreated = true;
            m_createdHostAvatars++;
            Info("已创建主机角色：记录键=" + PlayerIdentity.Describe(recordKey) +
                "（身份来自 HelloAck，不是 index 兜底）");
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
            m_transport = null;
            m_downloadedWorlds = null;
            m_attached = false;
            Info("联机角色模块下线（" + reason + "）：只清理本模块自己的状态");
        }

        public string StatusLine() =>
            "player-avatar: host-avatars=" + m_createdClientAvatars +
            " client-host-avatars=" + m_createdHostAvatars +
            " removed=" + m_removedClientAvatars +
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

        private void Info(string message) => m_context?.Diagnostics.Info(m_descriptor.Id, message);

        private void Warn(string message) => m_context?.Diagnostics.Warn(m_descriptor.Id, message);

        private void Error(string message) => m_context?.Diagnostics.Error(m_descriptor.Id, message, null);

        /// <summary>同一条持续性的错误只报一次（避免每帧刷屏，但绝不静默）。</summary>
        private void ErrorOnce(string key, string message)
        {
            if (!m_reportedProblems.Add(key)) return;
            m_context?.Diagnostics.Error(m_descriptor.Id, message, null);
        }
    }
}
