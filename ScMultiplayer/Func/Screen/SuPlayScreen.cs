using Comms.Drt;
using Engine;
using Engine.Input;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ScMultiplayer
{
    public class SuPlayScreen : PlayScreen
    {
        public static ListPanelWidget m_worldsListWidget;
        public List<WorldInfo> m_worldInfos;
        public Dictionary<string, (DateTime, float)> ServerWorldName = new Dictionary<string, (DateTime, float)>();
        public static bool IsGameJoined = false;
        public static byte[] WorldData;
        public WorldInfo SelectedItem;

        public SuPlayScreen() : base()
        {
            m_worldsListWidget = Children.Find<ListPanelWidget>("WorldsList");

            if (m_worldInfos == null)
            {
                m_worldInfos = Game.Program.ModManager.ModParentField
                    .GetStaticField<List<WorldInfo>>(typeof(Game.WorldsManager), "m_worldInfos");
            }

            // Enhanced world list display: add online info
            m_worldsListWidget.ItemWidgetFactory = (Func<object, Widget>)Delegate.Combine(
                m_worldsListWidget.ItemWidgetFactory,
                (Func<object, Widget>)delegate (object item)
                {
                    WorldInfo worldInfo = (WorldInfo)item;
                    XElement node2 = ContentManager.Get<XElement>("Widgets/SavedWorldItem");
                    ContainerWidget containerWidget = (ContainerWidget)Widget.LoadWidget(this, node2, null);
                    LabelWidget labelWidget = containerWidget.Children.Find<LabelWidget>("WorldItem.Name");
                    LabelWidget labelWidget2 = containerWidget.Children.Find<LabelWidget>("WorldItem.Details");
                    containerWidget.Tag = worldInfo;
                    labelWidget.Text = worldInfo.WorldSettings.Name;
                    labelWidget2.Text = string.Format("{0} | {1:dd MMM yyyy HH:mm} | {2} | {3} | {4}",
                        DataSizeFormatter.Format(worldInfo.Size),
                        worldInfo.LastSaveTime.ToLocalTime(),
                        (worldInfo.PlayerInfos.Count > 1) ? $"{worldInfo.PlayerInfos.Count} players" : "1 player",
                        worldInfo.WorldSettings.GameMode.ToString(),
                        worldInfo.WorldSettings.EnvironmentBehaviorMode.ToString());

                    if (worldInfo.SerializationVersion != VersionsManager.SerializationVersion)
                        labelWidget2.Text += " | " + (string.IsNullOrEmpty(worldInfo.SerializationVersion)
                            ? "(unknown)" : ("(" + worldInfo.SerializationVersion + ")"));

                    // Show online Ping
                    foreach (var name in ServerWorldName)
                    {
                        if (labelWidget.Text == name.Key && worldInfo.LastSaveTime == name.Value.Item1)
                            labelWidget2.Text += " | Net (Ping " + (int)(name.Value.Item2 * 1000f) + "ms)";
                    }
                    return containerWidget;
                });

            // Double-click: auto-detect create/join (search ALL servers for matching game)
            m_worldsListWidget.ItemClicked += delegate (object item)
            {
                if (item == null || m_worldsListWidget.SelectedItem != item) return;
                WorldInfo worldInfo = (WorldInfo)item;

                var servers = ScMultiplayer.explorer?.DiscoveredServers;
                if (servers == null || servers.Count == 0)
                {
                    // No servers discovered, create game locally
                    GameCreate(item);
                    Play(item);
                    return;
                }

                // Search ALL servers for matching remote game
                foreach (var sd in servers)
                {
                    foreach (var gd in sd.GameDescriptions)
                    {
                        if (Message.Read(gd.GameDescriptionBytes) is GameWorldInfoMessage remoteInfo)
                        {
                            // Skip games created by self
                            if (remoteInfo.HostAddress != null &&
                                remoteInfo.HostAddress.Address.Equals(ScMultiplayer.client.Address.Address))
                                continue;

                            // Match by world name
                            if (remoteInfo.Name == worldInfo.WorldSettings.Name)
                            {
                                GameJoin(item, gd);
                                return;
                            }
                        }
                    }
                }
                // No matching remote game -> create locally
                GameCreate(item);
                Play(item);
            };
        }

        public override void Update()
        {
            SelectedItem = m_worldsListWidget.SelectedItem as WorldInfo;

            if (Children.Find<ButtonWidget>("Play")?.IsClicked == true && SelectedItem != null)
            {
                WorldInfo worldInfo = SelectedItem;
                // Find matching GameDescription across ALL servers
                GameDescription matchGd = null;
                var servers = ScMultiplayer.explorer?.DiscoveredServers;
                if (servers != null)
                {
                    foreach (var sd in servers)
                    {
                        foreach (var gd in sd.GameDescriptions)
                        {
                            if (Message.Read(gd.GameDescriptionBytes) is GameWorldInfoMessage gameWorldInfo
                                && gameWorldInfo.Name == worldInfo.WorldSettings.Name)
                            {
                                matchGd = gd;
                                break;
                            }
                        }
                        if (matchGd != null) break;
                    }
                }

                if (matchGd != null)
                {
                    var msg = Message.Read(matchGd.GameDescriptionBytes) as GameWorldInfoMessage;
                    if (msg?.HostAddress != null &&
                        msg.HostAddress.Address.Equals(ScMultiplayer.client.Address.Address))
                        GameCreate(worldInfo); // Local game -> create
                    else
                        GameJoin(worldInfo, matchGd); // Remote game -> join
                }
                else
                {
                    GameCreate(worldInfo); // No match -> create locally
                }
            }
            base.Update();
        }

        /// <summary>
        /// Create game: export world data, then CreateGame
        /// </summary>
        private void GameCreate(object item)
        {
            ScMultiplayer.IsHost = true;
            WorldInfo worldInfo = (WorldInfo)item;
            var servers = ScMultiplayer.explorer?.DiscoveredServers;
            ServerDescription localSd = null;
            if (servers != null)
            {
                foreach (var s in servers)
                {
                    if (s.Address.Address.Equals(ScMultiplayer.client.Address.Address))
                    { localSd = s; break; }
                }
            }
            if (localSd == null)
            {
                Log.Error("[SuPlay] Cannot create game: no local server discovered");
                return;
            }

            // Export world data first (before CreateGame)
            using (var ms = new MemoryStream())
            {
                WorldsManager.ExportWorld(worldInfo.DirectoryName, ms);
                WorldData = ms.ToArray();
            }
            Log.Information($"[SuPlay] Exported world: {worldInfo.WorldSettings.Name} ({WorldData.Length} bytes)");

            var worldMsg = new GameWorldInfoMessage(
                worldInfo.WorldSettings.Name, worldInfo.Size, worldInfo.LastSaveTime,
                worldInfo.WorldSettings.GameMode, worldInfo.WorldSettings.EnvironmentBehaviorMode,
                worldInfo.SerializationVersion, ScMultiplayer.client.Address);

            // Cache game description bytes for LAN discovery response
            ScMultiplayer.LastGameDescription = Message.WriteWithSender(worldMsg, ScMultiplayer.client.Address);

            ScMultiplayer.client.CreateGame(localSd.Address,
                ScMultiplayer.LastGameDescription,
                ScMultiplayer.client.Address.Port.ToString());
            Log.Information($"[SuPlay] CreateGame sent: {worldInfo.WorldSettings.Name}");
        }

        /// <summary>
        /// Join game
        /// </summary>
        private void GameJoin(object item, GameDescription gd)
        {
            ScMultiplayer.IsHost = false;
            WorldInfo worldInfo = (WorldInfo)item;

            // Source: Comms.Peer.Connect throws "Peer is already connected"
            // if Peer.ConnectedTo != null. Client may still be connected
            // to local Server from previous session or discovery.
            if (ScMultiplayer.client.IsConnected)
            {
                ScMultiplayer.client.LeaveGame();
                Log.Information($"[SuPlay] Disconnected from previous peer before join");
            }

            var worldMsg = new GameWorldInfoMessage(
                worldInfo.WorldSettings.Name, worldInfo.Size, worldInfo.LastSaveTime,
                worldInfo.WorldSettings.GameMode, worldInfo.WorldSettings.EnvironmentBehaviorMode,
                worldInfo.SerializationVersion, ScMultiplayer.client.Address);

            ScMultiplayer.client.JoinGame(gd.ServerDescription.Address, gd.GameID,
                Message.WriteWithSender(worldMsg, ScMultiplayer.client.Address),
                ScMultiplayer.client.Address.Port.ToString());
            Log.Information($"[SuPlay] JoinGame sent: {worldInfo.WorldSettings.Name} -> {gd.GameID}");
        }

        public override void Enter(object[] parameters)
        {
            // Disconnect previous connection
            if (ScMultiplayer.client.IsConnected)
            {
                try { ScMultiplayer.client.LeaveGame(); } catch { }
            }

            // Async load remote world list
            Task.Run(delegate
            {
                Dispatcher.Dispatch(delegate
                {
                    ServerWorldName.Clear();
                    var servers = ScMultiplayer.explorer?.DiscoveredServers;
                    if (servers == null || servers.Count == 0) { Log.Information("[SuPlay] No servers discovered"); return; }

                    int remoteCount = 0;
                    foreach (var sd in servers)
                    {
                        Log.Information($"[SuPlay] Server {sd.Address}, Games={sd.GameDescriptions.Length}");
                        foreach (var gd in sd.GameDescriptions)
                        {
                            var msg = Message.Read(gd.GameDescriptionBytes);
                            if (msg is GameWorldInfoMessage worldInfo)
                            {
                                Log.Information($"[SuPlay]   Game '{worldInfo.Name}' Host={worldInfo.HostAddress}, MyAddr={ScMultiplayer.client.Address}, Skip={ScMultiplayer.client.Address.Equals(worldInfo.HostAddress)}");
                                if (ScMultiplayer.client.Address.Equals(worldInfo.HostAddress))
                                    continue; // Skip self

                                HandleGameWorldInfoMessage(worldInfo, gd);
                                remoteCount++;
                            }
                            else
                            {
                                Log.Information($"[SuPlay]   Game desc not GameWorldInfoMessage: " + (msg?.GetType().Name ?? "null"));
                            }
                        }
                    }
                    Log.Information($"[SuPlay] Loaded {remoteCount} remote games from {servers.Count} servers");
                });
            });

            base.Enter(parameters);
        }

        private void HandleGameWorldInfoMessage(GameWorldInfoMessage gameWorldInfo, GameDescription gd)
        {
            var worldInfo = new WorldInfo
            {
                DirectoryName = "data:/Worlds/" + gameWorldInfo.Name,
                Size = gameWorldInfo.Size,
                LastSaveTime = gameWorldInfo.LastSaveTime,
                PlayerInfos = new List<PlayerInfo>(),
                SerializationVersion = gameWorldInfo.SerializationVersion,
                WorldSettings = new WorldSettings { Name = gameWorldInfo.Name }
            };

            for (int i = 0; i < gd.ClientsCount; i++)
                worldInfo.PlayerInfos.Add(new PlayerInfo());

            ServerWorldName[gameWorldInfo.Name] = (gameWorldInfo.LastSaveTime, gd.ServerDescription.Ping);
            m_worldInfos.Add(worldInfo);
            m_worldsListWidget.AddItem(worldInfo);
        }

        public static void Play(object item)
        {
            ScreensManager.SwitchScreen("GameLoading", item, null);
            m_worldsListWidget.SelectedItem = null;
        }
    }
}
