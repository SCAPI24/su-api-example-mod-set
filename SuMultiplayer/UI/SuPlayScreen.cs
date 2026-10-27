using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Xml.Linq;
using Engine;
using Game;
using SuMultiplayer.Engine;
using SuMultiplayer.Modules;
using SuMultiplayer.Session;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// Play 屏（世界列表）—— **加入房间的唯一入口**。
    ///
    /// 迁移自旧实现 <c>Mod/ScMultiplayer/Func/Screen/SuPlayScreen.cs</c>：
    ///   · 局域网房间和本地世界一样是**列表里的一行**（本地行在前，房间行在后）；
    ///   · 选中房间行点 Play / 双击 → 连主机 → 主机下发世界 → 导入 → 进入下载来的世界；
    ///   · 进入用的是引擎原本的入口：<c>ScreensManager.SwitchScreen("GameLoading", 导入的WorldInfo, null)</c>，
    ///     也就是旧实现 <c>SuPlayScreen.Play(item)</c> 那两行。**不是**从主菜单硬切 "Game"。
    ///
    /// 这个屏幕不碰网络实现：只读 <see cref="IRoomBrowsePort"/> 的快照、只发"加入"请求，
    /// 并在 <see cref="IWorldReadyPort"/> 上等下载结果。
    ///
    /// 演示页标记：PLAY01。
    /// </summary>
    public sealed class SuPlayScreen : PlayScreen
    {
        private readonly IRoomBrowsePort m_rooms;
        private readonly IWorldReadyPort m_ready;
        private readonly IDownloadedWorldPort m_downloads;
        private readonly ListPanelWidget m_list;
        private readonly List<WorldInfo> m_localWorlds = new List<WorldInfo>();
        private readonly List<RoomRow> m_roomOrder = new List<RoomRow>();
        private readonly Dictionary<string, RoomRow> m_roomRows = new Dictionary<string, RoomRow>(StringComparer.Ordinal);
        private readonly List<string> m_removedKeys = new List<string>();
        private readonly List<object> m_desired = new List<object>(64);
        private long m_totalWorldsSize;
        private int m_scanGeneration;
        private string m_selectedKey;
        private SuJoinBusyDialog m_joinDialog;

        /// <summary>房间行模型：房间不是本地世界，绝不伪造成 WorldInfo 去骗引擎的世界列表。</summary>
        private sealed class RoomRow
        {
            public RoomEntry Entry;
            public string Key => Entry?.Key ?? string.Empty;
        }

        public SuPlayScreen(IRoomBrowsePort rooms, IWorldReadyPort ready, IDownloadedWorldPort downloads)
            : base()
        {
            m_rooms = rooms;
            m_ready = ready;
            m_downloads = downloads;
            m_list = Children.Find<ListPanelWidget>("WorldsList");

            // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.PlayScreen
            // 基类已经把自己的工厂和"双击=打开本地世界"的回调装上了，两个都必须换掉。
            m_list.ItemWidgetFactory = CreateWorldItemWidget;
            // Source: Mod/ScMultiplayer/Func/Screen/SuPlayScreen.cs:SuPlayScreen.SuPlayScreen
            Game.Program.ModManager.ModParentField.ModifyParentField(
                m_list, "ItemClicked", null, typeof(ListPanelWidget));
            m_list.ItemClicked += delegate (object item)
            {
                if (item == null || m_list.SelectedItem != item) return;
                ActivateWorld(item);
            };
        }

        public override void Enter(object[] parameters)
        {
            // Source: Mod/ScMultiplayer/Func/Screen/SuPlayScreen.cs:SuPlayScreen.Enter
            // 回到世界列表 = 已经不在联机世界里：
            //   ① 退出上一个房间（旧实现 client.LeaveGame()）—— 否则再点房间行会被当成"还连着"，
            //      既不会重新下载世界，也不会重新进世界；
            //   ② 清掉上一次**下载来的**世界（旧实现 CleanupDownloadedWorldsBeforeWorldList）——
            //      下载世界是临时的，不清就会每加入一次多堆一个同名世界目录。
            // 从"角色创建屏"返回：这是同一次加入的继续，不能 LeaveRoom（否则连接被停掉，
            // 客户端拿不到世界）；其它情况下回到世界列表 = 离开上一个房间。
            if (m_rooms == null || !m_rooms.ConsumePreserveJoinOnNextPlayEnter())
            {
                m_rooms?.LeaveRoom();
            }
            // 只有这个屏幕可见时才做局域网房间探测；回主菜单/进世界后停止（不留后台发现流量）。
            m_rooms?.SetListVisible(true);
            if (m_downloads != null) m_downloads.TryCleanup(out string cleanupNote);
            StartLocalScan();
        }

        public override void Leave()
        {
            m_scanGeneration++;
            m_rooms?.SetListVisible(false);
            HideJoinDialog();
            m_selectedKey = null;
            base.Leave();
        }

        /// <summary>
        /// 本地世界扫描（和引擎一样放到后台线程，扫完回主线程填列表）。
        /// 带 generation 守卫：来回进出 Play 屏时，旧的扫描结果不许覆盖新的列表。
        /// </summary>
        private void StartLocalScan()
        {
            int generation = ++m_scanGeneration;
            Task.Run(delegate
            {
                List<WorldInfo> worlds = null;
                Exception error = null;
                try
                {
                    WorldsManager.UpdateWorldsList();
                    worlds = new List<WorldInfo>(WorldsManager.WorldInfos);
                    worlds.Sort((left, right) => DateTime.Compare(right.LastSaveTime, left.LastSaveTime));
                }
                catch (Exception e)
                {
                    error = e;
                }
                Dispatcher.Dispatch(delegate
                {
                    if (generation != m_scanGeneration) return;
                    if (error != null)
                    {
                        global::Engine.Log.Error("[SuMP][play] 扫描本地世界失败：" + error.GetType().Name + " " + error.Message);
                        return;
                    }
                    m_localWorlds.Clear();
                    // 硬约束（用户口径 2026-10-21）：Play 屏列表里**绝不能出现下载来的世界**。
                    // 正常路径是 Enter 时先 TryCleanup 再扫描（目录已删）；这里再按标记过滤一次，
                    // 防止任何时序/删除失败让它以"一个几 MB 的真实地图"身份混进列表。
                    int hiddenDownloads = 0;
                    for (int i = 0; i < worlds.Count; i++)
                    {
                        string directory = worlds[i]?.DirectoryName;
                        if (m_downloads != null && m_downloads.IsDownloadedWorld(directory))
                        {
                            hiddenDownloads++;
                            continue;
                        }
                        m_localWorlds.Add(worlds[i]);
                    }
                    if (hiddenDownloads > 0)
                    {
                        global::Engine.Log.Warning("[SuMP][play] 本地列表发现 " + hiddenDownloads +
                            " 个下载来的世界（应由清理删除；本帧先从列表隐藏，等下一轮清理）");
                    }
                    m_totalWorldsSize = 0L;
                    for (int i = 0; i < m_localWorlds.Count; i++) m_totalWorldsSize += m_localWorlds[i].Size;
                });
            });
        }

        public override void Update()
        {
            SyncRoomRows();
            RebuildListIfNeeded();
            UpdateTopBar();
            RefreshVisibleRows();

            object selected = m_list.SelectedItem;
            m_selectedKey = KeyOf(selected);
            bool isRoom = selected is RoomRow;
            Children.Find("Play").IsEnabled = selected != null;
            RoomRow personalRow = selected as RoomRow;
            bool isPersonalRoom = personalRow != null &&
                personalRow.Entry?.Source == RoomSource.Personal;
            Children.Find("Properties").IsEnabled = selected != null && (!isRoom || isPersonalRoom);
            // 右下角三点按钮的原文案是 "..."（Pak/Screens/PlayScreen.xml）。
            // 只有选中个人服务器时才临时变成 "Remove"，其它情况必须还原成三点。
            ButtonWidget propertiesButton = Children.Find<ButtonWidget>("Properties");
            if (propertiesButton != null)
                propertiesButton.Text = isPersonalRoom ? "Remove" : "...";
            if (Children.Find<ButtonWidget>("Play")?.IsClicked == true && selected != null)
                ActivateWorld(selected);
            if (Children.Find<ButtonWidget>("NewWorld").IsClicked)
            {
                if (WorldsManager.WorldInfos.Count >= 30)
                {
                    DialogsManager.ShowDialog(null, new MessageDialog("Too many worlds",
                        "A maximum of 30 worlds is allowed on a device. Delete some to make space for new ones.",
                        "OK", null, null));
                }
                else
                {
                    ScreensManager.SwitchScreen("NewWorld");
                    m_list.SelectedItem = null;
                }
            }
            if (Children.Find<ButtonWidget>("Properties").IsClicked)
            {
                if (selected is WorldInfo worldInfo)
                {
                    ScreensManager.SwitchScreen("ModifyWorld", worldInfo.DirectoryName,
                        worldInfo.WorldSettings);
                }
                else if (isPersonalRoom && personalRow?.Entry != null)
                {
                    RoomEntry removing = personalRow.Entry;
                    DialogsManager.ShowDialog(null, new MessageDialog("Remove Personal Server",
                        "Remove \"" + removing.Name + " (" + removing.DisplayAddress + ")\"?",
                        "Remove", "Cancel", delegate (MessageDialogButton button)
                        {
                            if (button != MessageDialogButton.Button1) return;
                            if (!m_rooms.RemovePersonalServer(removing))
                                ShowMessage("Remove Personal Server",
                                    "The personal server could not be removed.");
                            RebuildListIfNeeded();
                        }));
                }
            }
            if (base.Input.Back || base.Input.Cancel || Children.Find<ButtonWidget>("TopBar.Back").IsClicked)
            {
                ScreensManager.SwitchScreen("MainMenu");
                m_list.SelectedItem = null;
            }

            UpdateJoinDialog();
            PollReadyWorld();
        }

        // ---------------------------------------------------------------- 房间行

        private void SyncRoomRows()
        {
            IReadOnlyList<RoomEntry> rooms = m_rooms?.Rooms;
            m_roomOrder.Clear();
            if (rooms == null)
            {
                m_roomRows.Clear();
                return;
            }
            for (int i = 0; i < rooms.Count; i++)
            {
                RoomEntry entry = rooms[i];
                if (!m_roomRows.TryGetValue(entry.Key, out RoomRow row))
                {
                    row = new RoomRow { Entry = entry };
                    m_roomRows.Add(entry.Key, row);
                }
                else
                {
                    row.Entry = entry;
                }
                m_roomOrder.Add(row);
            }
            m_removedKeys.Clear();
            foreach (KeyValuePair<string, RoomRow> pair in m_roomRows)
            {
                bool stillThere = false;
                for (int i = 0; i < rooms.Count; i++)
                {
                    if (string.Equals(rooms[i].Key, pair.Key, StringComparison.Ordinal)) { stillThere = true; break; }
                }
                if (!stillThere) m_removedKeys.Add(pair.Key);
            }
            for (int i = 0; i < m_removedKeys.Count; i++) m_roomRows.Remove(m_removedKeys[i]);
        }

        private void RebuildListIfNeeded()
        {
            m_desired.Clear();
            for (int i = 0; i < m_localWorlds.Count; i++) m_desired.Add(m_localWorlds[i]);
            for (int i = 0; i < m_roomOrder.Count; i++) m_desired.Add(m_roomOrder[i]);

            bool changed = m_desired.Count != m_list.Items.Count;
            if (!changed)
            {
                for (int i = 0; i < m_desired.Count; i++)
                {
                    if (!ReferenceEquals(m_desired[i], m_list.Items[i])) { changed = true; break; }
                }
            }
            if (!changed) return;
            m_list.ClearItems();
            for (int i = 0; i < m_desired.Count; i++) m_list.AddItem(m_desired[i]);
            if (!string.IsNullOrEmpty(m_selectedKey))
                m_list.SelectedItem = FindByKey(m_desired, m_selectedKey);
        }

        private static object FindByKey(List<object> items, string key)
        {
            for (int i = 0; i < items.Count; i++)
            {
                string candidate = KeyOf(items[i]);
                if (candidate != null && string.Equals(candidate, key, StringComparison.Ordinal)) return items[i];
            }
            return null;
        }

        private static string KeyOf(object item)
        {
            if (item is RoomRow row) return row.Key;
            if (item is WorldInfo world) return world.DirectoryName;
            return null;
        }

        private void UpdateTopBar()
        {
            int count = m_localWorlds.Count + m_roomOrder.Count;
            Children.Find<LabelWidget>("TopBar.Label").Text = count > 0
                ? string.Format("{0} {1}, {2}", count, count == 1 ? "world" : "worlds",
                    DataSizeFormatter.Format(m_totalWorldsSize, 2))
                : "No worlds";
        }

        // ---------------------------------------------------------------- 行渲染

        // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.PlayScreen
        private Widget CreateWorldItemWidget(object item)
        {
            XElement node = ContentManager.Get<XElement>("Widgets/SavedWorldItem");
            ContainerWidget container = (ContainerWidget)Widget.LoadWidget(this, node, null);
            container.Tag = item;
            UpdateRowWidget(container, item);
            return container;
        }

        // Source: Survivalcraft/Game/ListPanelWidget.cs:ListPanelWidget.CreateListWidgets
        // 只有可见行是子控件，所以刷新 RTT/阶段文本不需要重建整个列表。
        private void RefreshVisibleRows()
        {
            for (int i = 0; i < m_list.Children.Count; i++)
            {
                if (m_list.Children[i] is ContainerWidget container && container.Tag != null)
                    UpdateRowWidget(container, container.Tag);
            }
        }

        private void UpdateRowWidget(ContainerWidget container, object item)
        {
            LabelWidget name = container.Children.Find<LabelWidget>("WorldItem.Name");
            LabelWidget details = container.Children.Find<LabelWidget>("WorldItem.Details");
            if (item is RoomRow row)
            {
                // 房间行：名字 + 房间详情。**这一行必须全英文** —— Play 屏用的是游戏界面字体，
                // 没有中文字形（中文会渲染成下划线），旧实现 SuPlayScreen 用的也是英文口径。
                name.Text = string.IsNullOrEmpty(row.Entry?.Name) ? "(unnamed room)" :
                    FormatRoomDisplayName(row.Entry);
                details.Text = DescribeRoom(row.Entry);
                return;
            }
            if (!(item is WorldInfo worldInfo)) return;
            name.Text = worldInfo.WorldSettings.Name;
            details.Text = string.Format("{0} | {1:dd MMM yyyy HH:mm} | {2} | {3} | {4}",
                DataSizeFormatter.Format(worldInfo.Size),
                worldInfo.LastSaveTime.ToLocalTime(),
                worldInfo.PlayerInfos.Count > 1
                    ? $"{worldInfo.PlayerInfos.Count} players"
                    : "1 player",
                worldInfo.WorldSettings.GameMode,
                worldInfo.WorldSettings.EnvironmentBehaviorMode);
            if (worldInfo.SerializationVersion != VersionsManager.SerializationVersion)
            {
                details.Text = details.Text + " | " + (string.IsNullOrEmpty(worldInfo.SerializationVersion)
                    ? "(unknown)"
                    : "(" + worldInfo.SerializationVersion + ")");
            }
        }

        // ---------------------------------------------------------------- 激活

        private void ActivateWorld(object item)
        {
            if (item is RoomRow row)
            {
                JoinRoom(row.Entry);
                return;
            }
            if (!(item is WorldInfo worldInfo)) return;
            // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.Play
            m_list.SelectedItem = null;
            ScreensManager.SwitchScreen("GameLoading", worldInfo, null);
        }

        private void JoinRoom(RoomEntry entry)
        {
            if (entry == null) return;
            if (m_rooms == null)
            {
                ShowMessage("Join Room", "Multiplayer module is not loaded; cannot join a room.");
                return;
            }
            if (!m_rooms.TryBeginJoin(entry, out string reason))
            {
                ShowMessage("Join Room", "Cannot join \"" + entry.Name + "\": " + reason);
                return;
            }
            HideJoinDialog();
            // 2026-10-26：加 Cancel 的加入窗口（SuJoinBusyDialog）—— 状态行由 UpdateJoinDialog
            // 按 JoinStatusText 每帧驱动（Connecting → Waiting+Ping → Downloading…）；
            // Cancel = 中止加入（关通道、回 Idle），重新点加入 = 全程从头。
            m_joinDialog = new SuJoinBusyDialog("Join Room",
                "Connecting to " + entry.HostAddress + ":" + entry.Port + " ...",
                delegate
                {
                    global::Engine.Log.Information("[SuMP][play] 加入窗口 Cancel → 中止加入流程");
                    m_rooms?.CancelJoin();
                    m_joinDialog = null;
                });
            DialogsManager.ShowDialog(null, m_joinDialog);
            m_list.SelectedItem = null;
        }

        /// <summary>
        /// 房间行详情。格式沿用旧实现 <c>SuPlayScreen.UpdateWorldItemWidget</c> 的英文口径：
        /// 世界名 | 人数 | 模式 | 在线/服务（延迟）。**只用 ASCII**，中文会变成下划线。
        /// </summary>
        // Source: Mod/ScMultiplayer/Func/Screen/SuPlayScreen.cs:SuPlayScreen.UpdateWorldItemWidget
        private static string FormatRoomDisplayName(RoomEntry entry)
        {
            if (entry == null) return string.Empty;
            // 旧联机 Mod 的房间行口径：ServerDns / 个人服务器在名称后补来源地址，便于区分同名房间。
            // 例：Kaikai (Suceru.site) / Kaitlin (waiwai.com:2984)；LAN 行只显示房间名。
            if (entry.Source == RoomSource.Lan || string.IsNullOrEmpty(entry.DisplayAddress))
                return entry.Name ?? string.Empty;
            return (entry.Name ?? string.Empty) + " (" + entry.DisplayAddress + ")";
        }

        // Source: Mod/ScMultiplayer/Func/Screen/SuPlayScreen.cs:UpdateWorldItemWidget
        // 旧格式：大小 | 日期 | 人数 | 游戏模式 | 环境模式 [| (版本)] [| Service/Online (Ping)]
        private static string DescribeRoom(RoomEntry entry)
        {
            if (entry == null) return string.Empty;
            if (entry.Source == RoomSource.Personal && !entry.Online)
                return "Net World | Offline (Ping -)";
            string size = entry.WorldSizeBytes > 0
                ? DataSizeFormatter.Format(entry.WorldSizeBytes, 2)
                : "-";
            string date = entry.LastSaveTimeUtc > DateTime.MinValue
                ? entry.LastSaveTimeUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm")
                : "-";
            string players = entry.PlayerCount > 1
                ? entry.PlayerCount + " players"
                : entry.PlayerCount == 1 ? "1 player" : "-";
            string mode = string.IsNullOrEmpty(entry.GameMode) ? "-" : entry.GameMode;
            string environment = string.IsNullOrEmpty(entry.EnvironmentMode) ? "-" : entry.EnvironmentMode;
            string text = size + " | " + date + " | " + players + " | " + mode + " | " + environment;
            if (!string.IsNullOrEmpty(entry.SerializationVersion) &&
                !string.Equals(entry.SerializationVersion, VersionsManager.SerializationVersion,
                    StringComparison.Ordinal))
                text += " | (" + entry.SerializationVersion + ")";
            string ping = entry.PingMs >= 0 ? entry.PingMs + "ms" : "unknown";
            return text + " | " + entry.SourceLabel + " (Ping " + ping + ")";
        }

        private void UpdateJoinDialog()
        {
            if (m_joinDialog == null) return;
            // 被主机拒绝加入 / 加入被外部取消时，组合根会 HideAllDialogs 再弹提示：
            // 这里发现 BusyDialog 已不在对话框栈里就丢弃引用，不再往已关闭的对话框写状态。
            if (!DialogsManager.Dialogs.Contains(m_joinDialog))
            {
                m_joinDialog = null;
                return;
            }
            m_joinDialog.SmallMessage = m_rooms?.JoinStatusText ?? string.Empty;
        }

        private void HideJoinDialog()
        {
            if (m_joinDialog == null) return;
            m_joinDialog.CloseQuietly();
            m_joinDialog = null;
        }

        /// <summary>
        /// 下载来的世界一就绪就进入它 —— 客户端必须活在**下载到的那个联机世界**里。
        /// 用的是引擎原本的入口：GameLoading + 导入后的 WorldInfo（旧实现 SuPlayScreen.Play）。
        /// 列表里找不到就明确报错，不做"随便打开一个世界"这种兜底。
        /// </summary>
        private void PollReadyWorld()
        {
            if (m_ready == null) return;
            if (!m_ready.TryTake(out string directory, out string worldName)) return;
            HideJoinDialog();
            WorldsManager.UpdateWorldsList();
            WorldInfo imported = EngineWorldTransfer.FindImported(directory);
            if (imported == null)
            {
                string reason = "The downloaded world was imported (" + directory + " / " + worldName +
                                ") but is not in the world list, so it cannot be opened.";
                global::Engine.Log.Error("[SuMP][play] 下载的世界已导入但世界列表里找不到：" +
                                         directory + " / " + worldName);
                ShowMessage("Join Room", reason);
                return;
            }
            global::Engine.Log.Information("[SuMP][play] 进入下载来的世界：" + imported.WorldSettings.Name +
                            "（" + imported.DirectoryName + "）");
            ScreensManager.SwitchScreen("GameLoading", imported, null);
        }

        private static void ShowMessage(string title, string text) =>
            DialogsManager.ShowDialog(null, new MessageDialog(title, text, "OK", null, null));
    }
}
