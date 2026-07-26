using Comms.Drt;
using Engine;
using Engine.Input;
using Game;
using SuAPI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
        private readonly HashSet<WorldInfo> m_serviceWorlds = new HashSet<WorldInfo>();
        private readonly Dictionary<string, double> m_remoteLastSeen = new Dictionary<string, double>();
        private readonly Dictionary<string, double> m_remoteRouteLastSeen = new Dictionary<string, double>();
        private readonly Dictionary<string, WorldInfo> m_localWorlds =
            new Dictionary<string, WorldInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<ServerDescription> m_pendingRemoteServers =
            new ConcurrentQueue<ServerDescription>();
        private Explorer m_subscribedExplorer;
        private volatile bool m_remoteApplyRequested;
        private DateTime m_nextRemoteExpiryTime;
        private DateTime m_nextPingProbeTime;
        private int m_pingProbePending;
        private const double RemoteWorldRetentionSeconds = 15.0;
        private const double PreferredServiceRouteSeconds = 7.0;
        private const double RemotePingProbePeriodSeconds = 1.0;
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

            // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.PlayScreen
            // The base screen already combines the default LabelWidget factory with its saved-
            // world factory. Combining once more would construct three widgets and discard two.
            m_worldsListWidget.ItemWidgetFactory = CreateWorldItemWidget;

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
            DateTime now = DateTime.UtcNow;
            if (!m_worldScanPending && m_remoteApplyRequested)
            {
                m_remoteApplyRequested = false;
                ApplyPendingRemoteServers();
            }
            if (!m_worldScanPending && now >= m_nextRemoteExpiryTime)
            {
                m_nextRemoteExpiryTime = now.AddSeconds(1.0);
                RemoveStaleRemoteWorlds();
            }
            if (!m_worldScanPending && now >= m_nextPingProbeTime)
            {
                m_nextPingProbeTime = now.AddSeconds(RemotePingProbePeriodSeconds);
                BeginRemotePingProbes();
            }

            SelectedItem = GetSelectedWorldInfo();
            bool isRemote = SelectedItem != null && m_remoteGames.ContainsKey(SelectedItem);
            if (SelectedItem != null && !isRemote &&
                !WorldsManager.WorldInfos.Any(world =>
                    string.Equals(world.DirectoryName, SelectedItem.DirectoryName,
                        StringComparison.OrdinalIgnoreCase)))
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

        // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.PlayScreen
        private Widget CreateWorldItemWidget(object item)
        {
            WorldInfo worldInfo = (WorldInfo)item;
            XElement node = ContentManager.Get<XElement>("Widgets/SavedWorldItem");
            ContainerWidget container = (ContainerWidget)Widget.LoadWidget(this, node, null);
            container.Tag = worldInfo;
            UpdateWorldItemWidget(container, worldInfo);
            return container;
        }

        // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.PlayScreen
        private void UpdateWorldItemWidget(ContainerWidget container, WorldInfo worldInfo)
        {
            LabelWidget name = container.Children.Find<LabelWidget>("WorldItem.Name");
            LabelWidget details = container.Children.Find<LabelWidget>("WorldItem.Details");
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
                details.Text += " | " + (string.IsNullOrEmpty(worldInfo.SerializationVersion)
                    ? "(unknown)"
                    : "(" + worldInfo.SerializationVersion + ")");
            }
            if (m_remoteWorldPings.TryGetValue(worldInfo, out float ping))
            {
                string status = m_serviceWorlds.Contains(worldInfo) ? "Service" : "Online";
                string pingText = ping > 0f && ping < 3f
                    ? Math.Round(ping * 1000f).ToString() + "ms"
                    : "unknown";
                details.Text += " | " + status + " (Ping " + pingText + ")";
            }
        }

        private void RefreshVisibleWorldItems()
        {
            // Source: Survivalcraft/Game/ListPanelWidget.cs:ListPanelWidget.CreateListWidgets
            // Only visible rows are children, so dynamic RTT updates do not rebuild the list.
            foreach (Widget child in m_worldsListWidget.Children)
            {
                if (child is ContainerWidget container && container.Tag is WorldInfo worldInfo)
                    UpdateWorldItemWidget(container, worldInfo);
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

            // Source: Comms.Drt.Explorer.DiscoveredServers
            // Remote rows always carry m_remoteGames. Do not take Peer.Lock on the UI thread as
            // a fallback, and do not join a same-named remote room when a local world was clicked.
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

            ScMultiplayer.currentInstance.BeginLocalGameCreation(
                localServerAddress, ScMultiplayer.LastGameDescription);
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
            // Source: Mod/ScMultiplayer/Networking/RemoteServerDirectory.cs:RemoteServerDirectory.SetDiscoveryEnabled
            ScMultiplayer.currentInstance?.SetServerDiscoveryEnabled(true);
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
            while (m_pendingRemoteServers.TryDequeue(out _)) { }
            m_remoteApplyRequested = false;
            SubscribeToExplorer();
            QueueCurrentExplorerSnapshot(generation);
            m_nextRemoteExpiryTime = DateTime.MinValue;
            m_nextPingProbeTime = DateTime.MinValue;
            StartWorldScan(generation);
        }

        public override void Leave()
        {
            // Source: Mod/ScMultiplayer/Networking/RemoteServerDirectory.cs:RemoteServerDirectory.SetDiscoveryEnabled
            ScMultiplayer.currentInstance?.SetServerDiscoveryEnabled(false);
            Interlocked.Increment(ref m_enterGeneration);
            m_worldScanPending = false;
            HideScanningWorldsDialog();
            if (m_subscribedExplorer != null)
                m_subscribedExplorer.ServerDiscovered -= HandleServerDiscovered;
            m_subscribedExplorer = null;
            m_remoteApplyRequested = false;
            while (m_pendingRemoteServers.TryDequeue(out _)) { }
            base.Leave();
        }

        private void StartWorldScan(int generation)
        {
            HideScanningWorldsDialog();
            m_scanningWorldsDialog = new BusyDialog("Scanning Worlds", null);
            DialogsManager.ShowDialog(null, m_scanningWorldsDialog);
            WorldInfo selectedItem = GetSelectedWorldInfo();
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
                    ApplyLocalWorlds(worlds, selectedItem);
                    HideScanningWorldsDialog();
                    ApplyPendingRemoteServers();
                    RemoveStaleRemoteWorlds();
                });
            });
        }

        private void ApplyLocalWorlds(IReadOnlyList<WorldInfo> scannedWorlds,
            WorldInfo selectedItem)
        {
            // Source: Survivalcraft/Game/PlayScreen.cs:PlayScreen.Enter
            // Reuse unchanged local rows. ListPanelWidget.ClearItems invalidates every visible
            // widget, so rebuild only when the actual local/remote item sequence has changed.
            var orderedLocalWorlds = new List<WorldInfo>(scannedWorlds.Count);
            var activeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (WorldInfo scanned in scannedWorlds)
            {
                activeDirectories.Add(scanned.DirectoryName);
                if (!m_localWorlds.TryGetValue(scanned.DirectoryName, out WorldInfo current))
                {
                    current = scanned;
                    m_localWorlds.Add(scanned.DirectoryName, current);
                }
                else
                {
                    CopyWorldInfo(scanned, current);
                }
                orderedLocalWorlds.Add(current);
            }
            foreach (string directory in m_localWorlds.Keys
                .Where(directory => !activeDirectories.Contains(directory)).ToArray())
            {
                m_localWorlds.Remove(directory);
            }

            var desiredItems = new List<WorldInfo>(orderedLocalWorlds);
            desiredItems.AddRange(m_remoteWorlds.Values);
            bool sequenceChanged = desiredItems.Count != m_worldsListWidget.Items.Count;
            if (!sequenceChanged)
            {
                for (int i = 0; i < desiredItems.Count; i++)
                {
                    if (!ReferenceEquals(desiredItems[i], m_worldsListWidget.Items[i]))
                    {
                        sequenceChanged = true;
                        break;
                    }
                }
            }
            if (sequenceChanged)
            {
                m_worldsListWidget.ClearItems();
                foreach (WorldInfo world in desiredItems)
                    m_worldsListWidget.AddItem(world);
            }

            m_totalWorldsSize = scannedWorlds.Sum(world => world.Size);
            if (selectedItem != null)
            {
                WorldInfo selected = desiredItems.FirstOrDefault(world =>
                    ReferenceEquals(world, selectedItem) ||
                    (!m_remoteGames.ContainsKey(world) &&
                        world.DirectoryName == selectedItem.DirectoryName));
                m_worldsListWidget.SelectedItem = selected;
            }
            RefreshVisibleWorldItems();
        }

        // Source: Survivalcraft/Game/WorldInfo.cs:WorldInfo
        private static void CopyWorldInfo(WorldInfo source, WorldInfo target)
        {
            target.DirectoryName = source.DirectoryName;
            target.Size = source.Size;
            target.LastSaveTime = source.LastSaveTime;
            target.SerializationVersion = source.SerializationVersion;
            target.WorldSettings = source.WorldSettings;
            target.PlayerInfos = source.PlayerInfos;
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
            // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.ServerDiscovered
            // The callback runs while Explorer owns Peer.Lock. Queue the immutable response and
            // let Screen.Update apply it without reading DiscoveredServers on the UI thread.
            if (server == null) return;
            m_pendingRemoteServers.Enqueue(server);
            m_remoteApplyRequested = true;
        }

        // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.DiscoveredServers
        private void QueueCurrentExplorerSnapshot(int generation)
        {
            Explorer explorer = m_subscribedExplorer;
            if (explorer == null) return;
            Task.Run(delegate
            {
                ServerDescription[] servers;
                try { servers = explorer.DiscoveredServers.ToArray(); }
                catch { return; }
                if (generation != m_enterGeneration ||
                    !ReferenceEquals(explorer, m_subscribedExplorer)) return;
                foreach (ServerDescription server in servers)
                    m_pendingRemoteServers.Enqueue(server);
                if (servers.Length > 0)
                    m_remoteApplyRequested = true;
            });
        }

        // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.ServerDiscovered
        private void ApplyPendingRemoteServers()
        {
            bool changed = false;
            while (m_pendingRemoteServers.TryDequeue(out ServerDescription server))
            {
                foreach (GameDescription game in server.GameDescriptions)
                {
                    GameWorldInfoMessage info;
                    try { info = Message.Read(game.GameDescriptionBytes) as GameWorldInfoMessage; }
                    catch { continue; }
                    if (info == null || ScMultiplayer.IsLocalServerEndpoint(server.Address))
                        continue;

                    string key = GetRemoteRoomKey(info, game, server.Address);
                    double now = Time.RealTime;
                    m_remoteLastSeen[key] = now;
                    string serviceHost = ScMultiplayer.GetServiceDiscoveryHost(server.Address);
                    bool isService = !string.IsNullOrEmpty(serviceHost);
                    if (m_remoteWorlds.TryGetValue(key, out WorldInfo existingWorld) &&
                        m_remoteGames.TryGetValue(existingWorld, out GameDescription existingGame))
                    {
                        bool currentIsService = m_serviceWorlds.Contains(existingWorld);
                        bool sameEndpoint = Equals(existingGame.ServerDescription.Address,
                            server.Address);
                        if (currentIsService && !isService && !sameEndpoint &&
                            m_remoteRouteLastSeen.TryGetValue(key, out double routeLastSeen) &&
                            now - routeLastSeen <= PreferredServiceRouteSeconds)
                        {
                            continue;
                        }
                    }

                    string displayName = isService
                        ? info.Name + " (" + serviceHost + ")"
                        : info.Name;
                    if (!m_remoteWorlds.TryGetValue(key, out WorldInfo remoteWorld))
                    {
                        remoteWorld = CreateRemoteWorldInfo(info, game, displayName);
                        m_remoteWorlds.Add(key, remoteWorld);
                        m_worldsListWidget.AddItem(remoteWorld);
                    }
                    else
                    {
                        UpdateRemoteWorldInfo(remoteWorld, info, game, displayName);
                    }
                    if (isService) m_serviceWorlds.Add(remoteWorld);
                    else m_serviceWorlds.Remove(remoteWorld);

                    // Source: SCAPI24/RuthlessConquest:ServersManager.Handle
                    // Replace the sample on every discovery response. The echoed request time in
                    // Comms supplies the current endpoint RTT rather than a screen-entry constant.
                    m_remoteWorldPings[remoteWorld] = server.Ping;
                    m_remoteGames[remoteWorld] = game;
                    m_remoteRouteLastSeen[key] = now;
                    changed = true;
                }
            }
            if (changed)
                RefreshVisibleWorldItems();
        }

        private static string GetRemoteRoomKey(GameWorldInfoMessage info,
            GameDescription game, IPEndPoint discoveredAddress)
        {
            // Source: Mod/ScMultiplayer/Message/GameWorldInfoMessage.cs:PlayerIdentity
            // One host can be discovered through LAN, VPN, public DNS and explicit forwarding.
            // Merge those routes so a service response upgrades the same visible room.
            if (!string.IsNullOrEmpty(info.PlayerIdentity))
                return "player:" + info.PlayerIdentity + "/" + game.GameID;
            IPEndPoint advertised = info.HostAddress ?? discoveredAddress;
            return "room:" + advertised + "/" + game.GameID + "/" + info.Name;
        }

        // Source: Mod/ScMultiplayer/Func/Screen/SuPlayScreen.CreateRemoteWorldInfo
        private static void UpdateRemoteWorldInfo(WorldInfo worldInfo,
            GameWorldInfoMessage info, GameDescription game, string displayName)
        {
            worldInfo.Size = info.Size;
            worldInfo.LastSaveTime = info.LastSaveTime;
            worldInfo.SerializationVersion = info.SerializationVersion;
            worldInfo.WorldSettings.Name = displayName;
            worldInfo.WorldSettings.GameMode = info.GameMode;
            worldInfo.WorldSettings.EnvironmentBehaviorMode = info.EnvironmentBehaviorMode;
            while (worldInfo.PlayerInfos.Count < game.ClientsCount)
                worldInfo.PlayerInfos.Add(new PlayerInfo());
            while (worldInfo.PlayerInfos.Count > game.ClientsCount)
                worldInfo.PlayerInfos.RemoveAt(worldInfo.PlayerInfos.Count - 1);
        }

        // Source: Mod/Comms/Comms.Drt/Func/Explorer/Set/ExplorerSettings.cs
        private void RemoveStaleRemoteWorlds()
        {
            double now = Time.RealTime;
            string[] staleKeys = m_remoteWorlds.Keys.Where(key =>
                !m_remoteLastSeen.TryGetValue(key, out double lastSeen) ||
                now - lastSeen > RemoteWorldRetentionSeconds).ToArray();
            WorldInfo selectedWorld = GetSelectedWorldInfo();
            if (staleKeys.Length > 0)
                m_worldsListWidget.SelectedIndex = null;
            foreach (string key in staleKeys)
            {
                WorldInfo remoteWorld = m_remoteWorlds[key];
                m_worldsListWidget.RemoveItem(remoteWorld);
                m_remoteWorldPings.Remove(remoteWorld);
                m_remoteGames.Remove(remoteWorld);
                m_serviceWorlds.Remove(remoteWorld);
                m_remoteLastSeen.Remove(key);
                m_remoteRouteLastSeen.Remove(key);
                m_remoteWorlds.Remove(key);
            }
            if (selectedWorld != null &&
                m_worldsListWidget.Items.Any(item => ReferenceEquals(item, selectedWorld)))
            {
                m_worldsListWidget.SelectedItem = selectedWorld;
            }
            if (staleKeys.Length > 0)
                RefreshVisibleWorldItems();
        }

        // Source: SCAPI24/RuthlessConquest:ServersManager.SendInternetDiscoveryRequests
        private void BeginRemotePingProbes()
        {
            Explorer explorer = m_subscribedExplorer;
            if (explorer == null || Interlocked.Exchange(ref m_pingProbePending, 1) != 0)
                return;
            int generation = Volatile.Read(ref m_enterGeneration);
            var endpoints = m_remoteGames.Select(pair => new
                {
                    Address = pair.Value.ServerDescription.Address,
                    IsInternet = m_serviceWorlds.Contains(pair.Key) ||
                        !pair.Value.ServerDescription.IsLocal
                })
                .GroupBy(item => item.Address)
                .Select(group => new
                {
                    Address = group.Key,
                    IsInternet = group.Any(item => item.IsInternet)
                }).ToArray();
            Task.Run(delegate
            {
                try
                {
                    foreach (var endpoint in endpoints)
                    {
                        if (generation != Volatile.Read(ref m_enterGeneration) ||
                            !ReferenceEquals(explorer, m_subscribedExplorer)) break;
                        try { explorer.DiscoverServer(endpoint.Address, endpoint.IsInternet); }
                        catch { }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref m_pingProbePending, 0);
                }
            });
        }

        // Source: Survivalcraft/Game/ListPanelWidget.cs:ListPanelWidget.SelectedItem
        private WorldInfo GetSelectedWorldInfo()
        {
            int? selectedIndex = m_worldsListWidget?.SelectedIndex;
            if (!selectedIndex.HasValue) return null;
            if (selectedIndex.Value < 0 || selectedIndex.Value >= m_worldsListWidget.Items.Count)
            {
                m_worldsListWidget.SelectedIndex = null;
                return null;
            }
            return m_worldsListWidget.Items[selectedIndex.Value] as WorldInfo;
        }

        // Source: Survivalcraft/Game/WorldInfo.cs:WorldInfo
        private static WorldInfo CreateRemoteWorldInfo(GameWorldInfoMessage gameWorldInfo,
            GameDescription gd, string displayName)
        {
            var worldInfo = new WorldInfo
            {
                DirectoryName = "data:/Worlds/" + gameWorldInfo.Name,
                Size = gameWorldInfo.Size,
                LastSaveTime = gameWorldInfo.LastSaveTime,
                PlayerInfos = new List<PlayerInfo>(),
                SerializationVersion = gameWorldInfo.SerializationVersion,
                WorldSettings = new WorldSettings
                {
                    Name = displayName,
                    GameMode = gameWorldInfo.GameMode,
                    EnvironmentBehaviorMode = gameWorldInfo.EnvironmentBehaviorMode
                }
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
