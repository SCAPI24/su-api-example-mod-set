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
        private readonly List<BevelledButtonWidget> m_buttons = new List<BevelledButtonWidget>();
        private bool m_roomClosedOnLeave;

        public RoomUiAttacher(IRoomBrowsePort rooms, IWorldReadyPort ready = null)
        {
            m_rooms = rooms;
            m_ready = ready;
        }

        public void Tick()
        {
            Project project = GameManager.Project;
            if (project == null)
            {
                m_buttons.Clear();
                // 离开世界 = 房间的世界没了：把房间收掉，而不是留一个"房间还在但世界不在"的假状态。
                if (m_rooms != null && m_rooms.IsHostingRoom && !m_roomClosedOnLeave)
                {
                    m_roomClosedOnLeave = true;
                    m_rooms.CloseRoom();
                    global::Engine.Log.Information("[SuMP][ui] 已离开世界 → 房间自动关闭");
                }
                return;
            }
            m_roomClosedOnLeave = false;

            // 游戏内"整图重下"入口：主机重启 / 重连被拒绝后，主机会把**当前世界**整图发来；
            // Play 屏不在前台时它自己不会消费 IWorldReadyPort，这里补上同一个落点
            // （引擎原本的 GameLoading 入口，会自然 dispose 掉手里的过期 Project）。
            TryEnterRefreshedWorld();

            SubsystemGameWidgets widgets = project.FindSubsystem<SubsystemGameWidgets>(false);
            if (widgets == null) return;
            ReadOnlyList<GameWidget> gameWidgets = widgets.GameWidgets;
            if (gameWidgets.Count == 0) return;

            bool hosting = m_rooms?.IsHostingRoom == true;
            string text = hosting ? "IF" : "CR";
            m_buttons.Clear();
            for (int i = 0; i < gameWidgets.Count; i++)
            {
                GameWidget gameWidget = gameWidgets[i];
                if (gameWidget == null) continue;
                StackPanelWidget moreContents = gameWidget.Children.Find<StackPanelWidget>("MoreContents", true);
                if (moreContents == null) continue;
                // 判重以**面板**为准（重进世界会重建 GameWidget，静态标志会让新 HUD 一个按钮都没有）。
                BevelledButtonWidget button =
                    moreContents.Children.Find<BevelledButtonWidget>(ButtonName, false);
                if (button == null) button = CreateButton(moreContents);
                if (button.Text != text) button.Text = text;
                m_buttons.Add(button);
                if (!button.IsClicked) continue;
                if (hosting) ShowRoomInfo();
                else CreateRoom();
            }
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
            global::Engine.Log.Information("[SuMP][ui] 游戏内整图重下完成 → 进入主机的新世界：" +
                imported.WorldSettings.Name + "（" + imported.DirectoryName + "）");
            ScreensManager.SwitchScreen("GameLoading", imported, null);
        }

        private static BevelledButtonWidget CreateButton(StackPanelWidget moreContents)
        {
            // Source: Survivalcraft/Game/BevelledButtonWidget.cs:BevelledButtonWidget
            var button = new BevelledButtonWidget
            {
                Name = ButtonName,
                Text = "CR",
                Size = new Vector2(76f, 64f),
                Margin = new Vector2(3f, 0f),
                Color = Color.White,
                CenterColor = new Color(45, 115, 75),
                BevelColor = new Color(120, 120, 120)
            };
            moreContents.Children.Add(button);
            global::Engine.Log.Information("[SuMP][ui] CR 建房按钮已挂到 MoreContents（游戏内建房入口）");
            return button;
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
            if (!m_rooms.CreateRoom(worldName, gameMode, out string failReason))
            {
                global::Engine.Log.Error("[SuMP][ui] 建房失败：" + failReason);
                ShowDialog("Create Room", failReason);
                return;
            }
            global::Engine.Log.Information("[SuMP][ui] CR：房间已创建（世界「" + worldName + "」）");
            ShowDialog("Create Room", m_rooms.RoomSummary);
        }

        private void ShowRoomInfo()
        {
            string summary = m_rooms?.RoomSummary ?? "Multiplayer module is not loaded";
            global::Engine.Log.Information("[SuMP][ui] IF：" + summary);
            ShowDialog("Room", summary);
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
