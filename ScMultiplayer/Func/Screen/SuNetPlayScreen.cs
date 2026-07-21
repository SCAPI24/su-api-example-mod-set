using Comms.Drt;
using Engine;
using Engine.Input;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ScMultiplayer
{
    public class SuNetPlayScreen : Screen
    {
        private ListPanelWidget m_serverListWidget;
        private List<ServerDescription> m_servers;
        private bool m_joinButtonHandled;

        public SuNetPlayScreen()
        {
            XElement node = ContentManager.Get<XElement>("Screens/PlayScreen");
            LoadContents(this, node);

            m_serverListWidget = Children.Find<ListPanelWidget>("WorldsList");

            // Repurpose item factory: display ServerDescription items
            m_serverListWidget.ItemWidgetFactory = (Func<object, Widget>)Delegate.Combine(
                m_serverListWidget.ItemWidgetFactory,
                (Func<object, Widget>)delegate (object item)
                {
                    ServerDescription sd = (ServerDescription)item;
                    XElement node2 = ContentManager.Get<XElement>("Widgets/SavedWorldItem");
                    ContainerWidget containerWidget = (ContainerWidget)Widget.LoadWidget(this, node2, null);
                    LabelWidget labelWidget = containerWidget.Children.Find<LabelWidget>("WorldItem.Name");
                    LabelWidget labelWidget2 = containerWidget.Children.Find<LabelWidget>("WorldItem.Details");
                    containerWidget.Tag = sd;

                    // Show server address as name
                    labelWidget.Text = sd.Address.ToString();

                    // Show ping, game count, local flag
                    string pingStr = $"Ping: {(int)(sd.Ping * 1000f)}ms";
                    string gameStr = sd.GameDescriptions.Length > 0
                        ? $"Games: {sd.GameDescriptions.Length}"
                        : "No games";
                    string localStr = sd.IsLocal ? " [Local]" : "";
                    labelWidget2.Text = $"{pingStr} | {gameStr}{localStr}";

                    return containerWidget;
                });

            m_serverListWidget.ScrollPosition = 0f;
            m_serverListWidget.ScrollSpeed = 0f;

            // Double-click on item -> join first game (skip if button click already handled)
            m_serverListWidget.ItemClicked += delegate (object item)
            {
                if (item == null || m_serverListWidget.SelectedItem != item) return;
                if (m_joinButtonHandled) { m_joinButtonHandled = false; return; }
                JoinSelectedServer();
            };

            // Change button texts
            Children.Find<ButtonWidget>("NewWorld").Text = "刷新";
            Children.Find<ButtonWidget>("Play").Text = "加入";
            Children.Find<ButtonWidget>("Properties").Text = "创建房间";
        }

        public override void Enter(object[] parameters)
        {
            // Disconnect previous connection
            ScMultiplayer.currentInstance.PrepareClientForGameCreation();

            RefreshServerList();

            base.Enter(parameters);
        }

        public override void Update()
        {
            ServerDescription selected = m_serverListWidget.SelectedItem as ServerDescription;

            // Update top bar label
            Children.Find<LabelWidget>("TopBar.Label").Text = m_servers != null && m_servers.Count > 0
                ? string.Format("{0} server{1}", m_servers.Count, m_servers.Count == 1 ? "" : "s")
                : "No servers";

            // Enable/disable buttons based on selection (cast to Widget for IsEnabled)
            Widget playBtn = Children.Find("Play");
            Widget propBtn = Children.Find("Properties");
            if (playBtn != null) playBtn.IsEnabled = selected != null;
            if (propBtn != null) propBtn.IsEnabled = selected != null;

            // 标记在按钮点击时，在 Update 中忽略 ItemClicked
            ButtonWidget playBtnW = Children.Find<ButtonWidget>("Play");
            if (playBtnW != null && playBtnW.IsClicked && selected != null)
            {
                m_joinButtonHandled = true;
                JoinSelectedServer();
            }

            // Create room button clicked
            ButtonWidget propBtnW = Children.Find<ButtonWidget>("Properties");
            if (propBtnW != null && propBtnW.IsClicked && selected != null)
            {
                CreateRoomOnServer(selected);
            }

            // Refresh button clicked
            ButtonWidget refreshBtn = Children.Find<ButtonWidget>("NewWorld");
            if (refreshBtn != null && refreshBtn.IsClicked)
            {
                RefreshServerList();
            }

            // Back button -> return to SuPlayScreen (Play screen)
            if (base.Input.Back || base.Input.Cancel || Children.Find<ButtonWidget>("TopBar.Back").IsClicked)
            {
                ScreensManager.SwitchScreen("Play");
                m_serverListWidget.SelectedItem = null;
            }

            base.Update();
        }

        /// <summary>
        /// Refresh the server list from Explorer.DiscoveredServers
        /// </summary>
        private void RefreshServerList()
        {
            var servers = ScMultiplayer.explorer?.DiscoveredServers;
            m_servers = servers != null ? new List<ServerDescription>(servers) : new List<ServerDescription>();

            m_serverListWidget.ClearItems();
            foreach (var sd in m_servers)
            {
                m_serverListWidget.AddItem(sd);
            }
        }

        /// <summary>
        /// Join the first available game on the selected server.
        /// If no games, try to create a new game connection.
        /// </summary>
        private void JoinSelectedServer()
        {
            ServerDescription sd = m_serverListWidget.SelectedItem as ServerDescription;
            if (sd == null) return;

            GameDescription[] games = sd.GameDescriptions;
            if (games != null && games.Length > 0)
            {
                // Join the first game on this server
                GameDescription gd = games[0];
                ConnectToGame(sd, gd);
            }
            else
            {
                // No games available; prompt user or just create a room
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "No Games",
                    "This server has no games. Create a room first?",
                    "Create", "Cancel", null
                ));
            }
        }

        /// <summary>
        /// Create a room on the selected server
        /// </summary>
        private void CreateRoomOnServer(ServerDescription sd)
        {
            ScMultiplayer.IsHost = true;

            if (ScMultiplayer.client.IsConnected)
            {
                try { ScMultiplayer.client.LeaveGame(); } catch { }
            }

            bool isLocalServer = ScMultiplayer.IsLocalServerEndpoint(sd.Address);
            IPEndPoint connectionAddress = isLocalServer
                ? ScMultiplayer.GetLocalServerConnectionAddress()
                : sd.Address;
            IPEndPoint advertisedAddress = isLocalServer
                ? ScMultiplayer.server.Address
                : sd.Address;

            // Generate a simple game description for the new room
            var worldMsg = new GameWorldInfoMessage(
                "NetRoom_" + Environment.TickCount,
                0L, DateTime.UtcNow,
                GameMode.Survival,
                EnvironmentBehaviorMode.Living,
                VersionsManager.SerializationVersion,
                advertisedAddress);

            ScMultiplayer.currentInstance.PrepareClientForGameCreation();
            byte[] descBytes = Message.WriteWithSender(worldMsg, ScMultiplayer.client.Address);
            ScMultiplayer.LastGameDescription = descBytes;

            ScMultiplayer.currentInstance.BeginLocalGameCreation(connectionAddress, descBytes);

            // Switch to game loading screen
            ScreensManager.SwitchScreen("GameLoading", null, null);
            m_serverListWidget.SelectedItem = null;
        }

        /// <summary>
        /// Connect to a specific game on a server
        /// </summary>
        private void ConnectToGame(ServerDescription sd, GameDescription gd)
        {
            ScMultiplayer.IsHost = false;

            if (ScMultiplayer.client.IsConnected)
            {
                try { ScMultiplayer.client.LeaveGame(); } catch { }
            }

            // Build a minimal join message
            var joinMsg = new GameWorldInfoMessage(
                "Joining",
                0L, DateTime.UtcNow,
                GameMode.Survival,
                EnvironmentBehaviorMode.Living,
                VersionsManager.SerializationVersion,
                ScMultiplayer.client.Address);

            ScMultiplayer.client.JoinGame(
                sd.Address,
                gd.GameID,
                Message.WriteWithSender(joinMsg, ScMultiplayer.client.Address),
                ScMultiplayer.client.Address.Port.ToString());

            ScreensManager.SwitchScreen("GameLoading", null, null);
            m_serverListWidget.SelectedItem = null;
        }
    }
}
