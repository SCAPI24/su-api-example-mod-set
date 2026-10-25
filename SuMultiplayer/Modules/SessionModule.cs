using System;
using System.Collections.Generic;
using SuMultiplayer.Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Session;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 房间会话模块：**创建房间 / 发现房间 / 加入房间**。
    ///
    /// 入口（旧实现迁移，唯一正确的那两个）：
    ///   · 建房 = 游戏内 CR 按钮 → <see cref="CreateRoom"/>（世界是玩家正在玩的那个）；
    ///   · 加入 = Play 屏点房间行 → <see cref="TryBeginJoin"/>（连上才返回 true）。
    /// 本模块**不在启动时按配置自己建房或自己连** —— 那会让"联机"在没有玩家的操作时凭空发生。
    ///
    /// 分工（模块化）：
    ///   · 本模块只做"在人前露面"和"找到别人"——UDP 探测/应答 + 房间列表 + 加入流程状态机；
    ///   · 真正的连接与地图同步归地形模块（一个 TCP 连接一个主人），本模块通过
    ///     <see cref="ITransportControlPort"/> 让它在正确时机开通道；
    ///   · 发现到的房间行由 Play 屏渲染，本模块不碰界面。
    ///
    /// 重要：**房间流程发生在主菜单（没有 Project）**，所以本模块的 tick 不能被
    /// "Project 已加载" 限制 —— 这是旧实现把一切挂在游戏世界里的老毛病。
    ///
    /// 演示页标记：ROOM04。
    /// </summary>
    public sealed class SessionModule : IMultiplayerModule, IRoomBrowsePort
    {
        private readonly ModuleDescriptor m_descriptor;
        private readonly SyncConfig m_config;
        /// <summary>本机资料存储（新加入 / 离开房间时清空：新世界必须重新走角色创建流程）。</summary>
        private readonly SuMultiplayer.Players.LocalPlayerProfileStore m_profileStore;
        private readonly RoomDirectory m_directory = new RoomDirectory();
        private readonly JoinFlow m_join = new JoinFlow();

        private IModuleContext m_context;
        private ITransportControlPort m_transportControl;
        private RoomBeaconHost m_beaconHost;
        private RoomBeaconClient m_beaconClient;
        private long m_lastListLogMs;
        private int m_lastLoggedCount = -1;
        private bool m_joinAnnounced;
        /// <summary>
        /// 从"角色创建屏"返回 Play 屏时置位：这次进入 Play 是**同一次加入的继续**（旧实现
        /// SubmitPendingJoin 重新 JoinGame），不是离开房间 —— Play 屏的 Enter 不能调 LeaveRoom，
        /// 否则会把刚建立的连接停掉，客户端永远收不到世界。只生效一次，且只在真正返回时置位。
        /// </summary>
        private bool m_preserveJoinOnNextPlayEnter;
        /// <summary>Play 屏（地图列表）是否可见：只有可见时才持续做局域网房间探测。</summary>
        private bool m_listVisible;
        private string m_roomWorldName = string.Empty;
        private string m_roomGameMode = string.Empty;

        public SessionModule(SyncConfig config,
            SuMultiplayer.Players.LocalPlayerProfileStore profileStore = null)
        {
            m_config = config ?? new SyncConfig();
            m_profileStore = profileStore;
            m_descriptor = new ModuleDescriptor("session", "房间会话")
            {
                ProvidesCapabilities = new[] { "session.rooms" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;
        public RoomDirectory Directory => m_directory;
        public JoinFlow Join => m_join;
        /// <summary>发现/选中的房间地址（加入流程用）。</summary>
        public string SelectedAddress { get; private set; }
        public int SelectedPort { get; private set; }

        public void Attach(IModuleContext context)
        {
            m_context = context;
            // 通道控制器由组合根注册（地形模块提供）；拿不到就明确说清楚，不假装能联机。
            if (!context.TryGetPort(out m_transportControl))
                Log("没有拿到传输控制端口（地形模块缺失）→ 建房/加入都无法进行");

            // 发现按需开启：只有 Play 屏（地图列表）可见时才探测，
            // 回主菜单/进世界后不再持续发探测包（用户口径：不在列表时不要刷新）。
            m_join.StageChanged += (from, to, detail) => Log("加入流程 " + from + " → " + to + "：" + detail);
        }

        /// <summary>
        /// 开发现（找别人的房间）。
        /// **主持房间时不开**：主机自己也会收到自己应答的探测包，于是自己的房间会出现在自己的
        /// Play 屏里（点它等于连自己）。旧实现是按"本地服务端点"过滤的，这里更直接 —— 主机不浏览房间。
        /// </summary>
        private void StartDiscovery(string why)
        {
            if (m_beaconClient != null || IsHostingRoom) return;
            if (!m_listVisible)
            {
                Log("暂不寻找房间（Play 屏不可见；进入地图列表时才开始探测）");
                return;
            }
            m_beaconClient = new RoomBeaconClient(m_config.DiscoveryPort);
            m_beaconClient.RoomFound += HandleRoomFound;
            if (m_beaconClient.Start())
            {
                Log("开始寻找房间（" + why + "）：发现端口 " + m_config.DiscoveryPort + " · 每 " +
                    m_beaconClient.ProbeIntervalMs + " ms 广播一次（发现的房间会进入 Play 屏世界列表）");
                m_join.Discover(Environment.TickCount64);
            }
            else
            {
                Log("房间发现启动失败（可能被系统禁止广播）→ Play 屏不会出现局域网房间");
            }
        }

        private void StopDiscovery(string why)
        {
            if (m_beaconClient == null) return;
            m_beaconClient.RoomFound -= HandleRoomFound;
            try { m_beaconClient.Dispose(); } catch (Exception) { }
            m_beaconClient = null;
            m_directory.Clear();
            m_lastLoggedCount = -1;
            Log("停止寻找房间（" + why + "）");
        }

        // ------------------------------------------------------------ 建房（CR）

        /// <summary>
        /// 建房：**只能在正在游玩的世界里调用**（worldName 来自当前世界）。
        /// 顺序：先开传输通道（失败就明确失败）→ 再开始应答发现探测。
        /// </summary>
        public bool CreateRoom(string worldName, string gameMode, out string reason)
        {
            reason = null;
            if (IsHostingRoom)
            {
                reason = "Already hosting room \"" + m_config.RoomName + "\"";
                return false;
            }
            if (m_transportControl == null)
            {
                reason = "Transport module is unavailable (terrain module is not active).";
                return false;
            }
            if (string.IsNullOrWhiteSpace(worldName))
            {
                reason = "Cannot read the current world name; create a room from inside a world.";
                return false;
            }
            if (!m_transportControl.StartHostServer(m_config.Port, out reason)) return false;

            m_roomWorldName = worldName;
            m_roomGameMode = gameMode ?? string.Empty;
            m_beaconHost = new RoomBeaconHost(m_config.DiscoveryPort, BuildRoomInfo);
            if (!m_beaconHost.Start())
            {
                // 发现端口绑不上：房间本身（TCP）已经开了，但局域网里看不到。
                // 明确失败并回滚，避免出现"有房间但没人能发现"的半成品状态。
                string bindReason = "Discovery port " + m_config.DiscoveryPort +
                    " could not be bound (the room would not be visible on the LAN).";
                m_beaconHost.Dispose();
                m_beaconHost = null;
                m_transportControl.StopChannel();
                reason = bindReason;
                return false;
            }
            Log("房间已创建：名称「" + m_config.RoomName + "」· 世界「" + worldName + "」· 地图端口 " +
                m_config.Port + " · 发现端口 " + m_config.DiscoveryPort + "（有人探测就应答，不周期广播）");
            // 主持期间不浏览房间：否则自己的房间会出现在自己的 Play 屏里。
            StopDiscovery("本机已在主持房间");
            return true;
        }

        public void CloseRoom()
        {
            if (m_beaconHost != null)
            {
                m_beaconHost.Dispose();
                m_beaconHost = null;
            }
            m_roomWorldName = string.Empty;
            m_roomGameMode = string.Empty;
            m_transportControl?.StopChannel();
            Log("房间已关闭（发现应答与地图通道都已停）");
            if (m_listVisible) StartDiscovery("房间已关闭，重新寻找房间");
        }

        /// <summary>Play 屏可见性（UI 在 Enter/Leave 调用）：不可见时停止一切房间探测。</summary>
        public void SetListVisible(bool visible)
        {
            if (m_listVisible == visible) return;
            m_listVisible = visible;
            if (visible)
            {
                StartDiscovery("进入地图列表");
                m_join.Discover(Environment.TickCount64);
            }
            else
            {
                StopDiscovery("离开地图列表（停止刷新）");
            }
        }

        private RoomBeaconInfo BuildRoomInfo() => new RoomBeaconInfo
        {
            RoomName = m_config.RoomName,
            Port = m_config.Port,
            Players = 1 + (m_transportControl?.ConnectedPlayerCount ?? 0),
            MaxPlayers = m_config.MaxPlayers,
            WorldName = m_roomWorldName,
            GameMode = m_roomGameMode,
            Protocol = 1
        };

        // ------------------------------------------------------------ 加入

        public bool TryBeginJoin(RoomEntry entry, out string reason)
        {
            reason = null;
            if (entry == null)
            {
                reason = "No room selected.";
                return false;
            }
            if (IsHostingRoom)
            {
                reason = "This device is hosting a room; close it before joining another.";
                return false;
            }
            if (m_transportControl == null)
            {
                reason = "Transport module is unavailable (terrain module is not active).";
                return false;
            }
            long now = Environment.TickCount64;
            if (m_join.IsActive && SelectedAddress == entry.HostAddress && SelectedPort == entry.Port)
                return true;
            m_preserveJoinOnNextPlayEnter = false;
            // 新的一次加入 = 新的一次角色申请（旧实现 m_activeJoinHasPlayerProfile = false）：
            // 上一次房间留下的资料不能静默套到新世界；主机没有记录时会回 profile_required 重新创建。
            m_profileStore?.Clear();
            if (!m_transportControl.ConnectTo(entry.HostAddress, entry.Port, out reason))
                return false;

            SelectedAddress = entry.HostAddress;
            SelectedPort = entry.Port;
            m_joinAnnounced = true;
            m_join.BeginConnect(entry.Key, now);
            m_join.Connected(now);
            Log("加入房间「" + entry.Name + "」→ 已连接 " + entry.HostAddress + ":" + entry.Port +
                "（世界随后由主机下发）");
            return true;
        }

        /// <summary>
        /// 离开房间：回到世界列表时调用（旧实现 SuPlayScreen.Enter → client.LeaveGame()）。
        /// 主持中就先关房间，然后断通道、把加入流程打回 Idle，并重新开始找房间。
        /// </summary>
        public void LeaveRoom()
        {
            if (IsHostingRoom) CloseRoom();
            bool wasActive = m_transportControl?.IsChannelRunning == true || m_joinAnnounced;
            m_transportControl?.StopChannel();
            m_joinAnnounced = false;
            m_preserveJoinOnNextPlayEnter = false;
            SelectedAddress = null;
            SelectedPort = 0;
            m_profileStore?.Clear();
            m_join.Reset(Environment.TickCount64);
            if (wasActive) Log("已离开上一个房间（通道已关闭，加入流程回到 Idle）");
            StartDiscovery("回到世界列表");
        }

        /// <summary>
        /// 客户端被主机拒绝加入（HelloAck.rejected / 主机审批超时）：关闭通道、把加入流程打回 Idle。
        /// 这不是正常"离开房间"：不写欢迎类日志，由组合根弹提示并显示拒绝原因；绝不自动重连。
        /// </summary>
        public void NotifyJoinRejected(string reason)
        {
            bool wasActive = m_transportControl?.IsChannelRunning == true || m_joinAnnounced;
            m_transportControl?.StopChannel();
            m_joinAnnounced = false;
            m_preserveJoinOnNextPlayEnter = false;
            SelectedAddress = null;
            SelectedPort = 0;
            m_profileStore?.Clear();
            m_join.Reset(Environment.TickCount64);
            Log("主机拒绝加入 → 通道已关闭，加入流程回到 Idle：" +
                (string.IsNullOrEmpty(reason) ? "(主机未说明原因)" : reason) +
                (wasActive ? string.Empty : "（当时通道未在运行）"));
        }

        private void HandleRoomFound(RoomBeaconInfo info, string address)
        {
            long now = Environment.TickCount64;
            var entry = new RoomEntry
            {
                Key = "lan:" + address + ":" + info.Port,
                Name = string.IsNullOrEmpty(info.RoomName) ? "(unnamed room)" : info.RoomName,
                HostAddress = address,
                Port = info.Port,
                Source = RoomSource.Lan,
                PlayerCount = info.Players,
                MaxPlayers = info.MaxPlayers,
                WorldName = info.WorldName,
                GameMode = info.GameMode,
                // 房间行的延迟：用探测→应答的 RTT（旧联机 mod 房间行同样显示 Ping；-1 = unknown）。
                PingMs = info.PingMs,
                LastSeenMs = now
            };
            bool isNew = FindByKey(entry.Key) == null;
            m_directory.Upsert(entry, now);
            m_join.FoundRooms(m_directory.Count, now);
            if (isNew) Log("发现房间：" + entry.Describe() + "（来源 " + address + "）→ 已加入 Play 屏列表，点它加入");
        }

        private RoomEntry FindByKey(string key)
        {
            IReadOnlyList<RoomEntry> view = m_directory.View;
            for (int i = 0; i < view.Count; i++)
            {
                if (view[i].Key == key) return view[i];
            }
            return null;
        }

        private string DescribeAll()
        {
            IReadOnlyList<RoomEntry> view = m_directory.View;
            var parts = new List<string>(view.Count);
            for (int i = 0; i < view.Count; i++) parts.Add("「" + view[i].Name + "」" + view[i].PlayerCount +
                "/" + view[i].MaxPlayers + "@" + view[i].HostAddress + ":" + view[i].Port);
            return string.Join("、", parts);
        }

        public void Tick(in ModuleTickContext tick)
        {
            long nowMs = (long)(tick.Now * 1000.0);
            if (m_beaconClient == null) return;
            int removed = m_directory.RemoveStale(nowMs);
            if (removed > 0) Log("房间列表移除 " + removed + " 个超时条目（现在 " + m_directory.Count + " 个）");
            if (m_directory.Count != m_lastLoggedCount && nowMs - m_lastListLogMs > 3000)
            {
                m_lastLoggedCount = m_directory.Count;
                m_lastListLogMs = nowMs;
                Log("当前可加入房间 " + m_directory.Count + " 个" + (m_directory.Count == 0
                    ? "（继续每 2 秒探测；若一直为 0，检查主机是否已在游戏内按了 CR、是否同一网段）"
                    : "：" + DescribeAll()));
            }
        }

        public void Detach(ModuleDetachReason reason)
        {
            try { m_beaconHost?.Dispose(); } catch (Exception) { }
            m_beaconHost = null;
            StopDiscovery("模块下线");
            m_transportControl?.StopChannel();
            if (m_join.IsActive) m_join.Fail(JoinAbortReason.UserCancelled,
                Environment.TickCount64, "模块下线");
            Log("房间会话下线（" + reason + "）");
        }

        // ------------------------------------------------------------ IRoomBrowsePort

        public IReadOnlyList<RoomEntry> Rooms => m_directory.View;

        public bool IsHostingRoom => m_beaconHost != null;

        /// <summary>
        /// 本端是否作为客户端加入了房间（与主持互斥）。判据：不是房主，且加入流程已发起过
        /// （<see cref="m_joinAnnounced"/>，直到 <see cref="LeaveRoom"/> 才复位）或传输通道正在跑。
        /// 产品口径：客户端加入房间后 HUD 只显示 IF，绝不出现 CR（见 <see cref="IRoomBrowsePort"/>）。
        /// </summary>
        public bool IsClientInRoom =>
            !IsHostingRoom &&
            (m_joinAnnounced || m_transportControl?.IsChannelRunning == true);

        /// <summary>
        /// 角色创建完成、即将返回 Play 屏：这一次 Enter 保留连接（不要 LeaveRoom）。
        /// 由角色创建屏在 SwitchScreen("Play") 之前调用；只生效一次。
        /// </summary>
        public void PreserveJoinOnNextPlayEnter()
        {
            m_preserveJoinOnNextPlayEnter = true;
            Log("角色创建屏返回 Play：保留本次加入的连接（不执行 LeaveRoom），完成后会带资料重新握手");
        }

        /// <summary>Play 屏 Enter 调用：返回 true = 本次不要 LeaveRoom（同一次加入的继续）。</summary>
        public bool ConsumePreserveJoinOnNextPlayEnter()
        {
            if (!m_preserveJoinOnNextPlayEnter) return false;
            m_preserveJoinOnNextPlayEnter = false;
            return true;
        }

        public string RoomSummary
        {
            get
            {
                // 用户可见（对话框）→ 只用 ASCII：界面字体没有中文字形，中文会变下划线。
                if (!IsHostingRoom) return "This device is not hosting a room.";
                return "Room \"" + m_config.RoomName + "\" | world \"" + m_roomWorldName +
                       "\" | map port " + m_config.Port + " | players " +
                       (m_transportControl?.ConnectedPlayerCount ?? 0) + "/" + m_config.MaxPlayers +
                       " | channel " + (m_transportControl?.ChannelStateText ?? "none");
            }
        }

        public string JoinStageText => m_join.Stage.ToString();

        public string StatusLine()
        {
            if (IsHostingRoom)
                return "session: host room=\"" + m_config.RoomName + "\" world=\"" + m_roomWorldName +
                       "\" beacon=True answered=" + (m_beaconHost?.ProbesAnswered ?? 0) +
                       " players=" + (m_transportControl?.ConnectedPlayerCount ?? 0);
            return "session: rooms=" + m_directory.Count + " probes=" +
                   (m_beaconClient?.ProbesSent ?? 0) + " replies=" + (m_beaconClient?.RepliesReceived ?? 0) +
                   " stage=" + m_join.Stage +
                   (m_joinAnnounced ? " → " + SelectedAddress + ":" + SelectedPort : string.Empty);
        }

        private void Log(string message)
        {
            if (m_context != null) m_context.Diagnostics.Info(m_descriptor.Id, message);
            else global::Engine.Log.Information("[SuMP] " + message);
        }
    }
}
