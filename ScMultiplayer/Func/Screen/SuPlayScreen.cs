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
        private readonly Dictionary<string, WorldInfo> m_remoteWorlds = new Dictionary<string, WorldInfo>();
        private readonly Dictionary<WorldInfo, float> m_remoteWorldPings = new Dictionary<WorldInfo, float>();
        private readonly Dictionary<WorldInfo, GameDescription> m_remoteGames = new Dictionary<WorldInfo, GameDescription>();
        private readonly Dictionary<string, double> m_remoteLastSeen = new Dictionary<string, double>();
        private double m_nextRemoteRefreshTime;
        public static bool IsGameJoined = false;
        public static byte[] WorldData;
        public static string WorldDataName;
        public static DateTime WorldDataLastSaveTime;
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
                    if (m_remoteWorldPings.TryGetValue(worldInfo, out float ping))
                        labelWidget2.Text += " | Online (Ping " + (int)(ping * 1000f) + "ms)";
                    return containerWidget;
                });

            // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.PlayScreen
            // Replace the base double-click delegate, which always treats an item as a local world.
            Game.Program.ModManager.ModParentField.ModifyParentField(
                m_worldsListWidget, "ItemClicked", null, typeof(ListPanelWidget));
            m_worldsListWidget.ItemClicked += delegate (object item)
            {
                if (item == null || m_worldsListWidget.SelectedItem != item) return;
                ActivateWorld((WorldInfo)item);
            };
        }

        public override void Update()
        {
            // Source: Comms.Drt.Explorer.DiscoveredServers
            // Keep the list live when a host creates a room while this screen is already open.
            if (Time.RealTime >= m_nextRemoteRefreshTime)
            {
                m_nextRemoteRefreshTime = Time.RealTime + 1.0;
                RefreshRemoteWorlds();
            }

            SelectedItem = m_worldsListWidget.SelectedItem as WorldInfo;

            if (Children.Find<ButtonWidget>("Play")?.IsClicked == true && SelectedItem != null)
            {
                ActivateWorld(SelectedItem);
            }
            base.Update();
        }

        private void ActivateWorld(WorldInfo worldInfo)
        {
            if (m_remoteGames.TryGetValue(worldInfo, out GameDescription remoteGame))
            {
                GameJoin(worldInfo, remoteGame);
                return;
            }

            if (m_remoteWorlds.Values.Contains(worldInfo))
            {
                m_worldsListWidget.SelectedItem = null;
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Join Room", "This room is no longer available.", "OK", null, null));
                return;
            }

            GameDescription matchingGame = null;
            GameWorldInfoMessage matchingInfo = null;
            var servers = ScMultiplayer.explorer?.DiscoveredServers;
            if (servers != null)
            {
                foreach (var server in servers)
                {
                    foreach (var game in server.GameDescriptions)
                    {
                        try { matchingInfo = Message.Read(game.GameDescriptionBytes) as GameWorldInfoMessage; }
                        catch { matchingInfo = null; }
                        if (matchingInfo?.Name == worldInfo.WorldSettings.Name)
                        {
                            matchingGame = game;
                            break;
                        }
                    }
                    if (matchingGame != null) break;
                }
            }

            if (matchingGame != null &&
                (matchingInfo?.HostAddress == null ||
                 !matchingInfo.HostAddress.Address.Equals(ScMultiplayer.client.Address.Address)))
            {
                GameJoin(worldInfo, matchingGame);
                return;
            }

            GameCreate(worldInfo);
            Play(worldInfo);
        }

        /// <summary>
        /// Create game: export world data, then CreateGame
        /// </summary>
        private void GameCreate(object item)
        {
            ScMultiplayer.IsHost = true;
            WorldInfo worldInfo = (WorldInfo)item;
            RemoveOrphanPlayerEntities(worldInfo.DirectoryName);
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
            WorldDataName = worldInfo.WorldSettings.Name;
            WorldDataLastSaveTime = worldInfo.LastSaveTime;
            Log.Information($"[SuPlay] Exported world: {worldInfo.WorldSettings.Name} ({WorldData.Length} bytes)");

            var worldMsg = new GameWorldInfoMessage(
                worldInfo.WorldSettings.Name, worldInfo.Size, worldInfo.LastSaveTime,
                worldInfo.WorldSettings.GameMode, worldInfo.WorldSettings.EnvironmentBehaviorMode,
                worldInfo.SerializationVersion, ScMultiplayer.client.Address, ScMultiplayer.GetLocalPlayerName(),
                ScMultiplayer.GetLocalPlayerIdentity());

            ScMultiplayer.currentInstance.PrepareClientForGameCreation();
            // Cache game description bytes for LAN discovery response
            ScMultiplayer.LastGameDescription = Message.WriteWithSender(worldMsg, ScMultiplayer.client.Address);

            ScMultiplayer.client.CreateGame(localSd.Address,
                ScMultiplayer.LastGameDescription,
                ScMultiplayer.client.Address.Port.ToString());
            Log.Information($"[SuPlay] CreateGame sent: {worldInfo.WorldSettings.Name}");
        }

        // Source: GameEntitySystem/Project.cs:Project.SaveEntities
        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.Load
        private static void RemoveOrphanPlayerEntities(string directoryName)
        {
            string projectPath = Storage.CombinePaths(directoryName, "Project.xml");
            if (!Storage.FileExists(projectPath)) return;

            XDocument document;
            using (Stream stream = Storage.OpenFile(projectPath, OpenFileMode.Read))
                document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);

            XElement playersSubsystem = document.Root?.Element("Subsystems")?.Elements("Values")
                .FirstOrDefault(element => (string)element.Attribute("Name") == "Players");
            XElement playersValues = playersSubsystem?.Elements("Values")
                .FirstOrDefault(element => (string)element.Attribute("Name") == "Players");
            if (playersValues == null) return;

            var validPlayerIndices = new HashSet<string>(playersValues.Elements("Values")
                .Select(element => (string)element.Attribute("Name"))
                .Where(value => !string.IsNullOrEmpty(value)));
            XElement entities = document.Root?.Element("Entities");
            if (entities == null) return;

            XElement[] orphanEntities = entities.Elements("Entity").Where(entity =>
            {
                XElement player = entity.Elements("Values")
                    .FirstOrDefault(element => (string)element.Attribute("Name") == "Player");
                string playerIndex = (string)player?.Elements("Value")
                    .FirstOrDefault(element => (string)element.Attribute("Name") == "PlayerIndex")?
                    .Attribute("Value");
                return playerIndex != null && !validPlayerIndices.Contains(playerIndex);
            }).ToArray();
            if (orphanEntities.Length == 0) return;

            foreach (XElement entity in orphanEntities) entity.Remove();
            using (Stream stream = Storage.OpenFile(projectPath, OpenFileMode.Create))
                document.Save(stream, SaveOptions.DisableFormatting);
            Log.Information($"[ScMP] Removed {orphanEntities.Length} orphan network player entities from {directoryName}");
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

            GameWorldInfoMessage worldMsg;
            try { worldMsg = Message.Read(gd.GameDescriptionBytes) as GameWorldInfoMessage; }
            catch { worldMsg = null; }
            if (worldMsg == null) return;

            ScMultiplayer.currentInstance.BeginJoinGame(
                gd.ServerDescription.Address, gd.GameID, worldMsg);
            // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.Update
            // Prevent the base screen from loading this virtual remote WorldInfo as a local directory.
            m_worldsListWidget.SelectedItem = null;
            Log.Information($"[SuPlay] JoinGame sent: {worldInfo.WorldSettings.Name} -> {gd.GameID}");
        }

        public override void Enter(object[] parameters)
        {
            // Disconnect previous connection
            if (ScMultiplayer.client.IsConnected)
            {
                try { ScMultiplayer.client.LeaveGame(); } catch { }
            }

            base.Enter(parameters);
            m_worldInfos = Game.Program.ModManager.ModParentField
                .GetStaticField<List<WorldInfo>>(typeof(Game.WorldsManager), "m_worldInfos");
            m_remoteWorlds.Clear();
            m_remoteWorldPings.Clear();
            m_remoteGames.Clear();
            m_remoteLastSeen.Clear();
            m_nextRemoteRefreshTime = 0.0;
            RefreshRemoteWorlds();
        }

        private void RefreshRemoteWorlds()
        {
            var seen = new HashSet<string>();
            var servers = ScMultiplayer.explorer?.DiscoveredServers;
            if (servers != null)
            {
                foreach (var server in servers.ToArray())
                {
                    foreach (var game in server.GameDescriptions)
                    {
                        GameWorldInfoMessage info;
                        try { info = Message.Read(game.GameDescriptionBytes) as GameWorldInfoMessage; }
                        catch { continue; }
                        if (info == null || ScMultiplayer.client.Address.Equals(info.HostAddress)) continue;

                        string key = server.Address + "/" + game.GameID;
                        seen.Add(key);
                        m_remoteLastSeen[key] = Time.RealTime;
                        if (!m_remoteWorlds.TryGetValue(key, out WorldInfo remoteWorld))
                        {
                            remoteWorld = CreateRemoteWorldInfo(info, game);
                            m_remoteWorlds.Add(key, remoteWorld);
                            m_worldInfos.Add(remoteWorld);
                            m_worldsListWidget.AddItem(remoteWorld);
                        }
                        m_remoteWorldPings[remoteWorld] = server.Ping;
                        m_remoteGames[remoteWorld] = game;
                    }
                }
            }

            foreach (string key in m_remoteWorlds.Keys.Where(key =>
                !seen.Contains(key) &&
                (!m_remoteLastSeen.TryGetValue(key, out double lastSeen) || Time.RealTime - lastSeen > 5.0)).ToArray())
            {
                WorldInfo remoteWorld = m_remoteWorlds[key];
                m_worldsListWidget.RemoveItem(remoteWorld);
                m_worldInfos.Remove(remoteWorld);
                m_remoteWorldPings.Remove(remoteWorld);
                m_remoteGames.Remove(remoteWorld);
                m_remoteLastSeen.Remove(key);
                m_remoteWorlds.Remove(key);
            }
        }

        // Source: Survivalcraft/Game/WorldInfo.cs:WorldInfo
        private static WorldInfo CreateRemoteWorldInfo(GameWorldInfoMessage gameWorldInfo, GameDescription gd)
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
            return worldInfo;
        }

        public static void Play(object item)
        {
            ScreensManager.SwitchScreen("GameLoading", item, null);
            m_worldsListWidget.SelectedItem = null;
        }
    }
}
