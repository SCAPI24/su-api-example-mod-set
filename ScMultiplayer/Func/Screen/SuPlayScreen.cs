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
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ScMultiplayer
{
    public class SuPlayScreen : PlayScreen
    {
        public static ListPanelWidget m_worldsListWidget;
        private readonly Dictionary<string, WorldInfo> m_remoteWorlds = new Dictionary<string, WorldInfo>();
        private readonly Dictionary<WorldInfo, float> m_remoteWorldPings = new Dictionary<WorldInfo, float>();
        private readonly Dictionary<WorldInfo, GameDescription> m_remoteGames = new Dictionary<WorldInfo, GameDescription>();
        private readonly Dictionary<string, double> m_remoteLastSeen = new Dictionary<string, double>();
        private DateTime m_nextRemoteRefreshTime;
        private Explorer m_subscribedExplorer;
        private volatile bool m_remoteRefreshRequested;
        private int m_remoteRefreshDispatchPending;
        private static readonly SemaphoreSlim s_worldScanLock = new SemaphoreSlim(1, 1);
        private int m_enterGeneration;
        private bool m_worldScanPending;
        private long m_totalWorldsSize;
        private BusyDialog m_scanningWorldsDialog;
        public static bool IsGameJoined = false;
        public static byte[] WorldData;
        public static string WorldDataName;
        public static DateTime WorldDataLastSaveTime;
        public WorldInfo SelectedItem;

        public SuPlayScreen() : base()
        {
            m_worldsListWidget = Children.Find<ListPanelWidget>("WorldsList");

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
            // Source: Comms.Drt.Explorer.ServerDiscovered
            // Keep the list live when a host creates a room while this screen is already open.
            DateTime now = DateTime.UtcNow;
            if (!m_worldScanPending &&
                (m_remoteRefreshRequested || now >= m_nextRemoteRefreshTime))
            {
                m_remoteRefreshRequested = false;
                m_nextRemoteRefreshTime = now.AddSeconds(2.0);
                RefreshRemoteWorlds();
            }

            SelectedItem = m_worldsListWidget.SelectedItem as WorldInfo;
            bool isRemote = SelectedItem != null && m_remoteGames.ContainsKey(SelectedItem);
            if (SelectedItem != null && !isRemote &&
                WorldsManager.WorldInfos.IndexOf(SelectedItem) < 0)
            {
                m_worldsListWidget.SelectedItem = null;
                SelectedItem = null;
            }
            Children.Find<LabelWidget>("TopBar.Label").Text =
                m_worldsListWidget.Items.Count > 0
                    ? string.Format("{0} {1}, {2}", m_worldsListWidget.Items.Count,
                        m_worldsListWidget.Items.Count == 1 ? "world" : "worlds",
                        DataSizeFormatter.Format(m_totalWorldsSize, 2))
                    : "No worlds";
            Children.Find("Play").IsEnabled = SelectedItem != null;
            Children.Find("Properties").IsEnabled = SelectedItem != null && !isRemote;
            if (Children.Find<ButtonWidget>("Play")?.IsClicked == true && SelectedItem != null)
            {
                ActivateWorld(SelectedItem);
            }
            if (Children.Find<ButtonWidget>("NewWorld").IsClicked)
            {
                if (WorldsManager.WorldInfos.Count >= 30)
                {
                    DialogsManager.ShowDialog(null, new MessageDialog(
                        "Too many worlds", "A maximum of 30 worlds is allowed on a device. " +
                        "Delete some to make space for new ones.", "OK", null, null));
                }
                else
                {
                    ScreensManager.SwitchScreen("NewWorld");
                    m_worldsListWidget.SelectedItem = null;
                }
            }
            if (Children.Find<ButtonWidget>("Properties").IsClicked &&
                SelectedItem != null && !isRemote)
            {
                ScreensManager.SwitchScreen("ModifyWorld", SelectedItem.DirectoryName,
                    SelectedItem.WorldSettings);
            }
            if (Input.Back || Input.Cancel ||
                Children.Find<ButtonWidget>("TopBar.Back").IsClicked)
            {
                ScreensManager.SwitchScreen("MainMenu");
                m_worldsListWidget.SelectedItem = null;
            }
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
                !ScMultiplayer.IsLocalServerEndpoint(matchingGame.ServerDescription.Address))
            {
                GameJoin(worldInfo, matchingGame);
                return;
            }

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
            IPEndPoint localServerAddress = ScMultiplayer.GetLocalServerConnectionAddress();
            if (localServerAddress == null)
            {
                Log.Error("[SuPlay] Cannot create game: local server is unavailable");
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
                worldInfo.SerializationVersion, ScMultiplayer.server.Address, ScMultiplayer.GetLocalPlayerName(),
                ScMultiplayer.GetLocalPlayerIdentity());

            ScMultiplayer.currentInstance.PrepareClientForGameCreation();
            // Cache game description bytes for LAN discovery response
            ScMultiplayer.LastGameDescription = Message.WriteWithSender(worldMsg, ScMultiplayer.client.Address);

            ScMultiplayer.client.CreateGame(localServerAddress,
                ScMultiplayer.LastGameDescription,
                ScMultiplayer.client.Address.Port.ToString());
            Log.Information($"[SuPlay] CreateGame sent: {worldInfo.WorldSettings.Name}, local={localServerAddress}, advertised={ScMultiplayer.server.Address}");
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
            Log.Information($"[SuPlay] JoinGame sent: {worldInfo.WorldSettings.Name} -> {gd.GameID}, server={gd.ServerDescription.Address}, advertisedHost={worldMsg.HostAddress}");
        }

        public override void Enter(object[] parameters)
        {
            // Disconnect previous connection
            if (ScMultiplayer.client.IsConnected)
            {
                try { ScMultiplayer.client.LeaveGame(); } catch { }
            }

            // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.Enter
            // The native task has no screen-generation guard. Repeated Enter/Leave can let an old
            // scan rewrite this list after remote rooms were added, leaving null/incomplete items.
            int generation = Interlocked.Increment(ref m_enterGeneration);
            m_worldScanPending = true;
            m_remoteWorlds.Clear();
            m_remoteWorldPings.Clear();
            m_remoteGames.Clear();
            m_remoteLastSeen.Clear();
            SubscribeToExplorer();
            m_nextRemoteRefreshTime = DateTime.MinValue;
            m_remoteRefreshRequested = true;
            StartWorldScan(generation);
        }

        public override void Leave()
        {
            Interlocked.Increment(ref m_enterGeneration);
            m_worldScanPending = false;
            HideScanningWorldsDialog();
            if (m_subscribedExplorer != null)
                m_subscribedExplorer.ServerDiscovered -= HandleServerDiscovered;
            m_subscribedExplorer = null;
            m_remoteRefreshRequested = false;
            Interlocked.Exchange(ref m_remoteRefreshDispatchPending, 0);
            base.Leave();
        }

        private void StartWorldScan(int generation)
        {
            HideScanningWorldsDialog();
            m_scanningWorldsDialog = new BusyDialog("Scanning Worlds", null);
            DialogsManager.ShowDialog(null, m_scanningWorldsDialog);
            WorldInfo selectedItem = m_worldsListWidget.SelectedItem as WorldInfo;
            Task.Run(async () =>
            {
                await s_worldScanLock.WaitAsync();
                List<WorldInfo> worlds = null;
                Exception scanError = null;
                try
                {
                    WorldsManager.UpdateWorldsList();
                    worlds = WorldsManager.WorldInfos.Where(world =>
                        world != null && world.WorldSettings != null &&
                        world.PlayerInfos != null).ToList();
                    worlds.Sort((left, right) =>
                        DateTime.Compare(right.LastSaveTime, left.LastSaveTime));
                }
                catch (Exception ex)
                {
                    scanError = ex;
                }
                finally
                {
                    s_worldScanLock.Release();
                }
                Dispatcher.Dispatch(() =>
                {
                    if (generation != m_enterGeneration)
                        return;
                    m_worldScanPending = false;
                    if (scanError != null)
                    {
                        HideScanningWorldsDialog();
                        Log.Error($"[ScMP] Failed to scan worlds: {scanError.Message}");
                        return;
                    }
                    m_worldsListWidget.ClearItems();
                    foreach (WorldInfo world in worlds)
                        m_worldsListWidget.AddItem(world);
                    m_totalWorldsSize = worlds.Sum(world => world.Size);
                    if (selectedItem != null)
                    {
                        m_worldsListWidget.SelectedItem = worlds.FirstOrDefault(world =>
                            world.DirectoryName == selectedItem.DirectoryName);
                    }
                    HideScanningWorldsDialog();
                    RefreshRemoteWorlds();
                });
            });
        }

        private void HideScanningWorldsDialog()
        {
            if (m_scanningWorldsDialog == null) return;
            DialogsManager.HideDialog(m_scanningWorldsDialog);
            m_scanningWorldsDialog = null;
        }

        // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.cs:Explorer.ServerDiscovered
        private void SubscribeToExplorer()
        {
            Explorer explorer = ScMultiplayer.explorer;
            if (ReferenceEquals(m_subscribedExplorer, explorer)) return;
            if (m_subscribedExplorer != null)
                m_subscribedExplorer.ServerDiscovered -= HandleServerDiscovered;
            m_subscribedExplorer = explorer;
            if (m_subscribedExplorer != null)
                m_subscribedExplorer.ServerDiscovered += HandleServerDiscovered;
        }

        private void HandleServerDiscovered(ServerDescription server)
        {
            m_remoteRefreshRequested = true;
            int generation = m_enterGeneration;
            // Source: Engine/Dispatcher.cs:Dispatcher.Dispatch
            // Explorer raises this on its network thread. Coalesce repeated discovery responses
            // and mutate ListPanelWidget only after returning to the game UI thread.
            if (Interlocked.Exchange(ref m_remoteRefreshDispatchPending, 1) != 0) return;
            Dispatcher.Dispatch(delegate
            {
                Interlocked.Exchange(ref m_remoteRefreshDispatchPending, 0);
                if (generation != m_enterGeneration) return;
                m_remoteRefreshRequested = false;
                m_nextRemoteRefreshTime = DateTime.UtcNow.AddSeconds(2.0);
                if (!m_worldScanPending)
                    RefreshRemoteWorlds();
            });
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
                        if (info == null || ScMultiplayer.IsLocalServerEndpoint(server.Address)) continue;
                        // Keep the displayed remote world tied to the discovery endpoint.
                        // HostAddress is metadata and may be stale from an older virtual NIC;
                        // JoinGame must use server.Address, which is the endpoint that replied.

                        string key = server.Address + "/" + game.GameID;
                        seen.Add(key);
                        m_remoteLastSeen[key] = Time.RealTime;
                        if (!m_remoteWorlds.TryGetValue(key, out WorldInfo remoteWorld))
                        {
                            remoteWorld = CreateRemoteWorldInfo(info, game);
                            m_remoteWorlds.Add(key, remoteWorld);
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
