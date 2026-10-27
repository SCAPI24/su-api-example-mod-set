using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Engine;
using SuMultiplayer.Modules;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// 游戏内 **CR 按钮**（建房入口）。
    ///
    /// 迁移自旧实现 <c>Mod/ScMultiplayer/Func/Component/MultiplayerUiComponent.cs</c>：
    ///   按钮挂在右上角三个点的菜单（<c>MoreContents</c>）里，和 GM/控制台按钮同一排；
    ///   · 未建房 → 文字 `CR`，点击 = **用当前正在玩的世界**建房；
    ///   · 已建房 → 文字 `IF`，点击 = 显示房间信息（谁在房里、通道状态）。
    ///
    /// 与旧实现不同的只有**挂载方式**：旧实现用 ComponentTemplate 往 Player 实体加组件；
    /// 这里由组合根每帧在 <c>SubsystemGameWidgets</c> 的每个 GameWidget 上挂同一个按钮 ——
    /// 不替换任何子系统（SubsystemGameWidgets 已被 ConsoleMod 占用），也不依赖任何静态单例。
    ///
    /// 演示页标记：UI01。
    /// </summary>
    public sealed class RoomUiAttacher
    {
        private const string ButtonName = "SuMultiplayerRoomButton";

        private readonly IRoomBrowsePort m_rooms;
        private readonly IWorldReadyPort m_ready;
        /// <summary>通用客户端事件请求队列（主机；null = 队列页不可用，IF 只显示房间信息）。</summary>
        private readonly Requests.HostRequestQueue m_requestQueue;
        /// <summary>"弹出最新请求"的动作（组合根注入 = JoinApprovalUi.ShowLatestJoinDialog；
        /// 用委托避免 UI 层依赖 Plug 内部类型）。</summary>
        private readonly Action m_showLatestRequest;
        private readonly Func<int> m_hostPort;
        private readonly Func<bool> m_autoHost;
        private readonly Func<IReadOnlyList<Modules.HostPeerInfo>> m_hostPeers;
        private readonly Func<IReadOnlyList<string>> m_bannedIds;
        private readonly Func<string, bool, string> m_banAndKick;
        private readonly Func<string, string> m_unban;
        private readonly Func<int, string> m_kick;
        private readonly Func<int, string> m_changePort;
        private readonly Func<bool> m_autoHostEnabled;
        private readonly Func<bool> m_autoApproveEnabled;
        private readonly Func<bool, string> m_setAutoHost;
        private readonly Func<bool, string> m_setAutoApprove;
        /// <summary>CR 建房成功后落盘世界联机设置（这是 SuMultiplayerWorld.xml 的创建时机）。</summary>
        private readonly Func<string> m_saveWorldSettings;
        private Project m_autoHostProject;
        private bool m_autoHostAttempted;
        /// <summary>已弹出的请求队列对话框（防重复；关一个才允许开下一个）。</summary>
        private MessageDialog m_queueDialog;
        /// <summary>主机设置容器（只允许主机打开；IF 再次点击要能把它关掉）。</summary>
        private HostSettingsWidget m_settingsWidget;
        private ComponentPlayer m_settingsOwner;
        private readonly List<BevelledButtonWidget> m_buttons = new List<BevelledButtonWidget>();
        /// <summary>
        /// 已经**消费过**当前这一次按下沿的按钮（见 <see cref="TryConsumeClick"/>）。
        ///
        /// 为什么需要它：`IsClicked` 是 `ClickableWidget.Update()` 每帧重算的**单帧边沿**
        /// （`Survivalcraft/Game/ClickableWidget.cs:28-52`），而本类是在 `Frame.Update` 里轮的。
        /// 一旦点击动作会切屏，`ScreensManager.SwitchScreen()` 会把
        /// `RootWidget.IsUpdateEnabled` 置 false（`ScreensManager.cs:79-83`），转屏期间整棵控件树
        /// **不再 Update** —— 那个 true 就冻结在那里；而 `Frame.Update` 不受它门控，于是每帧都会
        /// 再调一次 `SwitchScreen()`。`UpdateAnimation()` 每次都被新建的 AnimationData 从 Factor=0
        /// 重新开始（`ScreensManager.cs:61-92`），永远到不了 0.5 的中段插入点（`:297-311`）——
        /// 新屏永远不进 `RootWidget`：屏幕上还是旧 GameScreen、`status.screen` 却是目标屏、
        /// `animating` 永久为 true、新屏的 `Update()` 一次都不跑（Back/Escape 与 Cancel 全部失效）。
        /// 因此这里必须自己记边沿：一次 true 只动作一次，直到按钮真的被 Update 回 false
        /// （重新进游戏屏、控件树恢复更新）再复位。
        /// </summary>
        private readonly HashSet<BevelledButtonWidget> m_consumedClicks =
            new HashSet<BevelledButtonWidget>();
        private bool m_roomClosedOnLeave;
        /// <summary>
        /// 上一帧是否在游戏世界里。用于识别"**从世界退出**"（Project 变 null）：
        /// 引擎游戏内退出走 GameMenuDialog → DisposeProject → SwitchScreen("MainMenu")，
        /// **不进 Play 屏**，所以 SuPlayScreen.Enter 的 LeaveRoom 根本不会执行（实测客户端
        /// 退房后 TCP 一直不断、主机 4.5 分钟仍看到角色留在地图）。
        /// 只有"之前在世界里"才算退出，避免加入房间/加载世界期间（Project 还没建好）误退房。
        /// </summary>
        private bool m_wasInWorld;

        // 帧驱动计数：控件缺失 / 帧异常只计数 + 限流日志，绝不每帧抛到 Frame.Update。
        private long m_moreContentsMisses;
        private long m_tickFailures;
        private readonly Dictionary<string, long> m_lastLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public RoomUiAttacher(IRoomBrowsePort rooms, IWorldReadyPort ready = null,
            Requests.HostRequestQueue requestQueue = null, Action showLatestRequest = null,
            Func<int> hostPort = null, Func<bool> autoHost = null,
            Func<IReadOnlyList<Modules.HostPeerInfo>> hostPeers = null,
            Func<IReadOnlyList<string>> bannedIds = null,
            Func<string, bool, string> banAndKick = null,
            Func<string, string> unban = null,
            Func<int, string> kick = null,
            Func<int, string> changePort = null,
            Func<bool> autoHostEnabled = null,
            Func<bool> autoApproveEnabled = null,
            Func<bool, string> setAutoHost = null,
            Func<bool, string> setAutoApprove = null,
            Func<string> saveWorldSettings = null)
        {
            m_rooms = rooms;
            m_ready = ready;
            m_requestQueue = requestQueue;
            m_showLatestRequest = showLatestRequest;
            m_hostPort = hostPort;
            m_autoHost = autoHost;
            m_hostPeers = hostPeers;
            m_bannedIds = bannedIds;
            m_banAndKick = banAndKick;
            m_unban = unban;
            m_kick = kick;
            m_changePort = changePort;
            m_autoHostEnabled = autoHostEnabled;
            m_autoApproveEnabled = autoApproveEnabled;
            m_setAutoHost = setAutoHost;
            m_setAutoApprove = setAutoApprove;
            m_saveWorldSettings = saveWorldSettings;
        }

        public void Tick()
        {
            try
            {
                TickCore();
            }
            catch (Exception e)
            {
                m_tickFailures++;
                WarnRateLimited("room-ui-tick",
                    "[SuMP][ui] 房间 HUD 帧驱动异常（已隔离，下一帧继续）：" +
                    e.GetType().Name + " " + e.Message);
            }
        }

        private void TickCore()
        {
            Project project = GameManager.Project;
            if (project == null)
            {
                m_buttons.Clear();
                m_consumedClicks.Clear();
                m_settingsWidget = null;
                m_settingsOwner = null;
                // 离开世界 = 房间的世界没了，而不是留一个"房间还在但世界不在"的假状态。
                //   · 主机：房间自动关闭；
                //   · 客户端：**主动离开房间**（发 BYE + 关通道）。显式退房必须主动断连，
                //     否则主机永远等不到 ClientLeft，角色会停在地图上（2026-10-26 实机）。
                if (m_rooms != null && !m_roomClosedOnLeave && m_wasInWorld)
                {
                    m_roomClosedOnLeave = true;
                    if (m_rooms.IsHostingRoom)
                    {
                        m_rooms.CloseRoom();
                        SuMultiplayer.Engine.ModLog.Info("[SuMP][ui] left world -> room closed automatically");
                    }
                    else if (m_rooms.IsClientInRoom)
                    {
                        // LeaveRoom 内部走 StopChannelWithBye：先发 BYE（主机零宽限摘角色）再关通道。
                        m_rooms.LeaveRoom();
                        SuMultiplayer.Engine.ModLog.Info("[SuMP][ui] client left world -> leaving room" +
                            "（已发 BYE 并关闭通道，主机立即移除本机角色）");
                    }
                }
                m_wasInWorld = false;
                return;
            }
            m_roomClosedOnLeave = false;
            m_wasInWorld = true;

            // 游戏内"整图重下"入口：主机重启 / 重连被拒绝后，主机会把**当前世界**整图发来；
            // Play 屏不在前台时它自己不会消费 IWorldReadyPort，这里补上同一个落点
            // （引擎原本的 GameLoading 入口，会自然 dispose 掉手里的过期 Project）。
            TryEnterRefreshedWorld();

            TryAutoHost(project);
            SubsystemGameWidgets widgets = project.FindSubsystem<SubsystemGameWidgets>(false);
            if (widgets == null) return;
            ReadOnlyList<GameWidget> gameWidgets = widgets.GameWidgets;
            if (gameWidgets.Count == 0) return;

            bool hosting = m_rooms?.IsHostingRoom == true;
            // 产品口径（2026-10-21）：客户端加入房间后**只显示 IF**（房间信息），不再出现 CR。
            // 否则客户端能在别人的房间里再建一个房 → 双主机分裂（本地重建角色/位置错乱，
            // 连接又没断，所以既不踢人也不弹掉线框）。建房入口只留给"未连接、未主持"的本端。
            bool clientInRoom = !hosting && m_rooms?.IsClientInRoom == true;
            bool showInfo = hosting || clientInRoom;
            string text = showInfo ? "IF" : "CR";
            m_buttons.Clear();
            for (int i = 0; i < gameWidgets.Count; i++)
            {
                GameWidget gameWidget = gameWidgets[i];
                if (gameWidget == null) continue;
                // required:false：HUD 结构变化（或 XML 被别的 Mod 改过）不能让帧驱动每帧抛异常。
                StackPanelWidget moreContents =
                    gameWidget.Children.Find<StackPanelWidget>("MoreContents", false);
                if (moreContents == null)
                {
                    m_moreContentsMisses++;
                    WarnRateLimited("room-ui-morecontents",
                        "[SuMP][ui] GameWidget 里没有 MoreContents（引擎 HUD 结构变化）→ " +
                        "CR 按钮本帧不挂载（下一帧继续尝试）");
                    continue;
                }
                // 判重以**面板**为准（重进世界会重建 GameWidget，静态标志会让新 HUD 一个按钮都没有）。
                BevelledButtonWidget button =
                    moreContents.Children.Find<BevelledButtonWidget>(ButtonName, false);
                if (button == null) button = CreateButton(moreContents, ButtonName, "CR",
                    "CR create-room button attached to MoreContents");
                if (button.Text != text) button.Text = text;
                m_buttons.Add(button);
                if (TryConsumeClick(button))
                {
                    if (showInfo) ToggleRoomInfo();
                    else CreateRoom();
                }

                // 名字 / 皮肤统一走引擎自己的 Edit Players 屏（ESC → More → Edit Players）；
                // 独立的 SK 按钮与 SuSkinChangeScreen 已按用户要求删除，HUD 不再有第二个换肤入口。
                // 统计 / 成就没有独立的 HUD 入口：显示路径就是引擎自带的统计屏
                // （ESC → Game Menu → Statistics，读本机 ComponentPlayer.PlayerStats）。
                // 旧的 ST 按钮 / SyncedStatsDialog 已按用户要求删除。
            }
            // 只丢掉"肯定不会再被误读"的记录：控件已不在这一帧的面板里，**并且** IsClicked 也已经
            // 被 Update 回 false。冻结在 true 的记录必须留着 —— 否则会话掉线时按钮会暂时隐藏
            // （不在 m_buttons 里），重新出现时就会把同一次旧点击再触发一遍。
            m_consumedClicks.RemoveWhere(delegate (BevelledButtonWidget b)
            {
                return !b.IsClicked && !m_buttons.Contains(b);
            });
        }

        /// <summary>
        /// `IsClicked` 的**上升沿**只返回一次 true，并记录"这一次已经消费过"。
        ///
        /// 直接读 `IsClicked` 在"点击会切屏"的按钮上会变成每帧重复触发：切屏把
        /// `RootWidget.IsUpdateEnabled` 置 false，控件树停止 Update → `IsClicked` 冻结为 true，
        /// 而本 Tick 走 `Frame.Update` 仍在跑 → 每帧 `SwitchScreen()` → 转屏动画每帧重置 →
        /// 新屏永远不挂载。完整因果见 <see cref="m_consumedClicks"/> 的注释与
        /// `Survivalcraft/Game/ScreensManager.cs:61-92,282-323`。
        ///
        /// 复位条件 = 按钮真的被 Update 回 false（控件树恢复更新、这一帧没有 Click 边沿）。
        /// 因此从别的屏返回游戏屏、GameScreen 重新进入 RootWidget 后，旧边沿会被清掉，
        /// 下一次真人点击照常生效（不会永久吞点击）。
        /// </summary>
        private bool TryConsumeClick(BevelledButtonWidget button)
        {
            if (button == null) return false;
            if (!button.IsClicked)
            {
                m_consumedClicks.Remove(button);
                return false;
            }
            if (m_consumedClicks.Contains(button)) return false;
            m_consumedClicks.Add(button);
            return true;
        }

        /// <summary>
        /// 游戏内整图重下的落点：主机重启（会话变化）或追帧被拒绝时，客户端会收到**当前主机世界**；
        /// Play 屏不在前台就不会消费 <see cref="IWorldReadyPort"/> —— 这里用引擎原本的 GameLoading
        /// 入口进入新世界（会 dispose 掉手里那份过期 Project），与旧实现
        /// Mod/ScMultiplayer/Modules/Terrain/ScMultiplayerTerrainHandlers.cs:977-998
        /// （RestartClientWorldDownload → 重新加载主机快照）同一语义。
        /// 未在 GameScreen（Play 屏 / 主菜单）时不抢：Play 屏自己会消费 ready。
        /// </summary>
        private void TryEnterRefreshedWorld()
        {
            if (m_ready == null) return;
            if (!(ScreensManager.CurrentScreen is GameScreen)) return;
            if (!m_ready.TryTake(out string directory, out string worldName)) return;
            WorldsManager.UpdateWorldsList();
            WorldInfo imported = EngineWorldTransfer.FindImported(directory);
            if (imported == null)
            {
                // 明确失败：绝不"随便打开一个世界"，也不假装已经进去。
                global::Engine.Log.Error("[SuMP][ui] 下载的世界已导入但世界列表里找不到：" +
                    directory + " / " + worldName + " → 无法在游戏内切换到新世界");
                return;
            }
            SuMultiplayer.Engine.ModLog.Info("[SuMP][ui] 游戏内整图重下完成 → 进入主机的新世界：" +
                imported.WorldSettings.Name + "（" + imported.DirectoryName + "）");
            ScreensManager.SwitchScreen("GameLoading", imported, null);
        }

        private BevelledButtonWidget CreateButton(StackPanelWidget moreContents,
            string name, string text, string logMessage)
        {
            // Source: Survivalcraft/Game/BevelledButtonWidget.cs:BevelledButtonWidget
            var button = new BevelledButtonWidget
            {
                Name = name,
                Text = text,
                Size = new Vector2(76f, 64f),
                Margin = new Vector2(3f, 0f),
                Color = Color.White,
                CenterColor = new Color(45, 115, 75),
                BevelColor = new Color(120, 120, 120)
            };
            moreContents.Children.Add(button);
            InfoRateLimited("room-ui-button-" + name, "[SuMP][ui] " + logMessage);
            return button;
        }

        private void TryAutoHost(Project project)
        {
            if (m_autoHost == null || m_rooms == null || m_autoHost() != true) return;
            if (m_rooms.IsHostingRoom || m_rooms.IsClientInRoom) return;
            if (!ReferenceEquals(project, m_autoHostProject))
            {
                m_autoHostProject = project;
                m_autoHostAttempted = false;
            }
            if (m_autoHostAttempted) return;
            m_autoHostAttempted = true;
            CreateRoom();
        }

        private void CreateRoom()
        {
            if (m_rooms == null)
            {
                ShowDialog("Create Room", "Multiplayer module is not loaded; cannot create a room.");
                return;
            }
            if (!TryDescribeCurrentWorld(out string worldName, out string gameMode, out string reason))
            {
                ShowDialog("Create Room", reason);
                return;
            }
            int requestedPort = m_hostPort != null ? m_hostPort() : 0;
            if (!m_rooms.CreateRoom(worldName, gameMode, requestedPort, out string failReason))
            {
                global::Engine.Log.Error("[SuMP][ui] 建房失败：" + failReason);
                ShowDialog("Create Room", failReason);
                return;
            }
            SuMultiplayer.Engine.ModLog.Info("[SuMP][ui] CR: room created (world \"" + worldName + "\")");
            // 用户口径：SuMultiplayerWorld.xml 只在主机点 CR 建房时创建（或玩家修改联机设置时更新）。
            string saveNote = m_saveWorldSettings?.Invoke();
            if (!string.IsNullOrEmpty(saveNote)) ModLog.Info("[SuMP][ui] CR world settings: " + saveNote);
            ShowDialog("Create Room", m_rooms.RoomSummary);
        }

        /// <summary>
        /// IF 按钮是开关：房间信息框 / 主机设置容器已经打开时再按 IF = 关闭，不叠新界面。
        /// </summary>
        private void ToggleRoomInfo()
        {
            if (CloseHostSettingsIfOpen()) return;
            if (m_queueDialog != null && DialogsManager.Dialogs.Contains(m_queueDialog))
            {
                DialogsManager.HideDialog(m_queueDialog);
                m_queueDialog = null;
                return;
            }
            ShowRoomInfo();
        }

        private bool CloseHostSettingsIfOpen()
        {
            if (m_settingsOwner?.ComponentGui == null || m_settingsWidget == null)
            {
                m_settingsWidget = null;
                m_settingsOwner = null;
                return false;
            }
            if (!ReferenceEquals(m_settingsOwner.ComponentGui.ModalPanelWidget, m_settingsWidget))
            {
                m_settingsWidget = null;
                m_settingsOwner = null;
                return false;
            }
            m_settingsOwner.ComponentGui.ModalPanelWidget = null;
            m_settingsWidget = null;
            m_settingsOwner = null;
            return true;
        }

        private void CloseHostSettings()
        {
            if (m_settingsOwner?.ComponentGui != null && m_settingsWidget != null &&
                ReferenceEquals(m_settingsOwner.ComponentGui.ModalPanelWidget, m_settingsWidget))
            {
                m_settingsOwner.ComponentGui.ModalPanelWidget = null;
            }
            m_settingsWidget = null;
            m_settingsOwner = null;
        }

        private void ShowRoomInfo()
        {
            bool hosting = m_rooms?.IsHostingRoom == true;
            string summary = m_rooms?.RoomSummary ?? "Multiplayer module is not loaded";
            SuMultiplayer.Engine.ModLog.Info("[SuMP][ui] IF: " + summary);
            // 请求队列页（用户口径 2026-10-26）：房间信息下方按时间倒序列出待决请求；
            // 队列非空时给一个 Latest request 按钮，点击弹出最新一条的三按钮审批对话框。
            string queueText = string.Empty;
            string button1 = null;
            Action onButton1 = null;
            Requests.HostRequestQueue queue = m_requestQueue;
            if (queue != null)
            {
                System.Collections.Generic.List<Requests.HostRequestEntry> snapshot = queue.Snapshot();
                if (snapshot.Count > 0)
                {
                    var lines = new System.Text.StringBuilder();
                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        Requests.HostRequestEntry e = snapshot[i];
                        double age = Time.RealTime - e.CreatedSeconds;
                        lines.Append("- ").Append(Requests.HostRequestKind.Name(e.Kind)).Append(": ")
                             .Append(string.IsNullOrEmpty(e.Name) ? ("client#" + e.ClientId) : e.Name)
                             .Append(" (").Append(age < 60 ? ((int)age + "s ago") : ((int)(age / 60) + "m ago"))
                             .Append(")").AppendLine();
                    }
                    queueText = "Pending requests (newest first):" + Environment.NewLine + lines;
                    button1 = "Latest request";
                    onButton1 = ShowLatestRequest;
                }
            }
            // 设置容器只属于主机；客户端只读房间信息，设置入口后续由 GM mod 提供。
            string button1Text = hosting ? (button1 ?? "Settings") : (button1 ?? "OK");
            string button2Text = hosting ? (button1 == null ? null : "Settings") : null;
            var dialog = new MessageDialog("Room", summary + (queueText.Length > 0
                    ? Environment.NewLine + queueText
                    : string.Empty),
                button1Text, button2Text,
                delegate (MessageDialogButton button)
                {
                    if (button == MessageDialogButton.Button1)
                    {
                        if (button1 != null) onButton1?.Invoke();
                        else if (hosting) ShowHostSettingsMenu();
                    }
                    else if (button == MessageDialogButton.Button2 && hosting)
                    {
                        ShowHostSettingsMenu();
                    }
                });
            m_queueDialog = dialog;
            DialogsManager.ShowDialog(null, dialog);
        }

        private static void ShowResult(string title, string result) =>
            DialogsManager.ShowDialog(null, new MessageDialog(title,
                string.IsNullOrEmpty(result) ? "(done)" : result, "OK", null, null));

        private void ShowHostSettingsMenu()
        {
            // 设置界面只属于主机；客户端不允许从 IF 打开（后续由 GM mod 提供控制入口）。
            if (m_rooms?.IsHostingRoom != true) return;
            if (CloseHostSettingsIfOpen()) return;
            ComponentPlayer localPlayer = null;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players != null)
            {
                for (int i = 0; i < players.ComponentPlayers.Count; i++)
                {
                    if (players.ComponentPlayers[i]?.ComponentGui == null) continue;
                    localPlayer = players.ComponentPlayers[i];
                    break;
                }
            }
            if (localPlayer == null)
            {
                ShowResult("Multiplayer settings", "No local player (cannot open settings).");
                return;
            }
            var settings = new HostSettingsWidget(
                m_autoHostEnabled, m_autoApproveEnabled,
                delegate
                {
                    ShowResult("Auto host", m_setAutoHost?.Invoke(
                        m_autoHostEnabled?.Invoke() != true));
                },
                delegate
                {
                    ShowResult("Auto approve", m_setAutoApprove?.Invoke(
                        m_autoApproveEnabled?.Invoke() != true));
                },
                delegate { SelectPeer(true); },
                BanUserId,
                delegate { SelectPeer(false); },
                SelectBan,
                EditHostPort,
                CloseHostSettings);
            m_settingsOwner = localPlayer;
            m_settingsWidget = settings;
            localPlayer.ComponentGui.ModalPanelWidget = settings;
            SuMultiplayer.Engine.ModLog.Info("[SuMP][ui] IF settings container opened (button grid)");
        }

        private void SelectPeer(bool ban)
        {
            IReadOnlyList<Modules.HostPeerInfo> peers = m_hostPeers?.Invoke();
            if (peers == null || peers.Count == 0)
            {
                ShowResult("Players", "No connected players.");
                return;
            }
            var dialog = new ListSelectionDialog(ban ? "Ban + kick player" : "Kick player",
                peers, 48f, value =>
                {
                    var peer = (Modules.HostPeerInfo)value;
                    return peer.Name + " (" + peer.Identity + ") · client " + peer.ClientId;
                }, delegate (object selected)
                {
                    var peer = (Modules.HostPeerInfo)selected;
                    ShowResult(ban ? "Ban + kick" : "Kick",
                        ban ? m_banAndKick?.Invoke(peer.Identity, true) : m_kick?.Invoke(peer.ClientId));
                });
            DialogsManager.ShowDialog(null, dialog);
        }

        private void BanUserId()
        {
            DialogsManager.ShowDialog(null, new TextBoxDialog("Ban user ID", string.Empty, 64,
                text =>
                {
                    string id = (text ?? string.Empty).Trim();
                    if (id.Length == 0)
                    {
                        ShowResult("Ban user ID", "User ID is empty.");
                        return;
                    }
                    ShowResult("Ban user ID", m_banAndKick?.Invoke(id, false) ??
                        "Ban handler unavailable.");
                }));
        }

        private void SelectBan()
        {
            IReadOnlyList<string> bans = m_bannedIds?.Invoke();
            if (bans == null || bans.Count == 0)
            {
                ShowResult("Remove Ban", "Ban list is empty.");
                return;
            }
            var dialog = new ListSelectionDialog("Remove Ban", bans, 48f,
                value => value.ToString(), selected =>
                    ShowResult("Remove Ban", m_unban?.Invoke(selected as string ?? string.Empty)));
            DialogsManager.ShowDialog(null, dialog);
        }

        private void EditHostPort()
        {
            int current = m_hostPort != null ? m_hostPort() : 0;
            var dialog = new TextBoxDialog("Host port (0 = auto 51459..51468)",
                current.ToString(), 5, text =>
                {
                    if (!int.TryParse((text ?? string.Empty).Trim(), out int port) ||
                        port < 0 || port > 65535)
                    {
                        ShowResult("Host port", "Invalid port: " + text);
                        return;
                    }
                    ShowResult("Host port", m_changePort?.Invoke(port) ?? "Port handler unavailable.");
                });
            DialogsManager.ShowDialog(null, dialog);
        }

        /// <summary>弹出队列里最新一条请求的审批对话框；处理完（Allow/Reject）自动弹下一条。</summary>
        private void ShowLatestRequest()
        {
            if (m_requestQueue == null) return;
            if (m_requestQueue.Count == 0)
            {
                ShowDialog("Requests", "No pending requests.");
                return;
            }
            // 关掉队列页再弹审批对话框（同一时刻一个对话框，避免遮挡）。
            if (m_queueDialog != null && DialogsManager.Dialogs.Contains(m_queueDialog))
                DialogsManager.HideDialog(m_queueDialog);
            m_queueDialog = null;
            m_showLatestRequest?.Invoke();
        }

        /// <summary>帧驱动计数（5 秒状态行 / 限流日志用）。</summary>
        public string DescribeCounters() =>
            "room-ui: buttons=" + m_buttons.Count +
            " moreContentsMissing=" + m_moreContentsMisses +
            " tickFail=" + m_tickFailures;

        private void WarnRateLimited(string key, string message, int intervalMs = 10000)
        {
            if (!ShouldLog(key, intervalMs)) return;
            global::Engine.Log.Warning(message + " · " + DescribeCounters());
        }

        private void InfoRateLimited(string key, string message, int intervalMs = 5000)
        {
            if (!ShouldLog(key, intervalMs)) return;
            SuMultiplayer.Engine.ModLog.Info(message + " · " + DescribeCounters());
        }

        private bool ShouldLog(string key, int intervalMs)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastLogMs.TryGetValue(key, out long last) && nowMs - last < intervalMs)
                return false;
            m_lastLogMs[key] = nowMs;
            return true;
        }

        /// <summary>建房需要**当前正在玩的世界**；没有世界就明确拒绝（这正是旧实现 CR 的语义）。</summary>
        private static bool TryDescribeCurrentWorld(out string worldName, out string gameMode, out string reason)
        {
            worldName = null;
            gameMode = null;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "Enter a world first: CR creates a room for the world you are playing.";
                return false;
            }
            SubsystemGameInfo info = project.FindSubsystem<SubsystemGameInfo>(false);
            if (info?.WorldSettings == null)
            {
                reason = "Cannot read the current world info (SubsystemGameInfo missing); room not created.";
                return false;
            }
            worldName = info.WorldSettings.Name;
            gameMode = info.WorldSettings.GameMode.ToString();
            if (string.IsNullOrWhiteSpace(worldName))
            {
                reason = "The current world has no name, so it cannot identify a room.";
                return false;
            }
            return true;
        }

        private static void ShowDialog(string title, string text) =>
            DialogsManager.ShowDialog(null, new MessageDialog(title, text, "OK", null, null));
    }
}
