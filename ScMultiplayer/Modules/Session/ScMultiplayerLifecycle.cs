using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using SuAPICore;
using ScMultiplayer.Control;
using ScMultiplayer.Diagnostics;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public partial class ScMultiplayer : IMod, Ports.IMultiplayerUiCommandPort
    {
        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            currentInstance = this;
            m_eventBus = eventBus;
            m_controlUnit ??= new MultiplayerControlUnit(this, this);
            m_messageRouter ??= new NetworkMessageRouter(this);
            m_controlUnit.Initialize();
            ReliableRetransmitDiagnostics.PacketRetransmitted += HandleReliableRetransmit;
            // Source: Mod/ScMultiplayer/Message/Message.cs:Message.ProtocolHash
            string protocolLabel = Message.GetProtocolLabel(
                Message.ModVersion, Message.ProtocolVersion,
                Message.ProtocolHash, Message.BuildFingerprint);
            Log.Information($"[ScMP] Wire protocol {protocolLabel}, " +
                $"hash={Message.ProtocolHash}, build={Message.BuildFingerprint}");
            m_circuitSynchronizer ??= new CircuitSynchronizer(this);
            m_worldObjectSynchronizer ??= new WorldObjectSynchronizer(this);
            ModManager = Game.Program.ModManager;
            m_modInjector = modInjector;
            modInjector.Register("Game.ComponentHumanModel",
                "ScMultiplayer.SuComponentHumanModel");
            modInjector.Register("Game.SubsystemElectricity",
                "ScMultiplayer.SuSubsystemElectricity");
            modInjector.RegisterBlock(Game.ButtonBlock.Index,
                typeof(global::ScMultiplayer.ButtonBlock), Name);
            modInjector.RegisterBlock(Game.PressurePlateBlock.Index,
                typeof(global::ScMultiplayer.PressurePlateBlock), Name);
            modInjector.RegisterBlock(Game.RandomGeneratorBlock.Index,
                typeof(global::ScMultiplayer.RandomGeneratorBlock), Name);
            modInjector.RegisterBlock(Game.DetonatorBlock.Index,
                typeof(global::ScMultiplayer.DetonatorBlock), Name);
            modInjector.RegisterBlock(Game.DispenserBlock.Index,
                typeof(global::ScMultiplayer.DispenserBlock), Name);
            modInjector.RegisterBlock(Game.PistonBlock.Index,
                typeof(global::ScMultiplayer.PistonBlock), Name);
            ScMultiplayerSettings.Load();
            m_serverSettingsToken = eventBus.SubscribeEvent(
                "ScMultiplayer.ServerSettings",
                HandleServerSettingsEvent,
                EventPriority.HIGHEST);
            PersonalServerDirectory.Load();
            m_fromLinkToken = SuFromLinkProviders.Subscribe(eventBus,
                new NetWorldFromLinkProvider());
            playerMappingManager.MaxPlayerIndices = ScMultiplayerSettings.MaxPlayers;
            StartWorldTransferSender();

            // 初始化状态机
            connectionSM = new NetworkConnectionStateMachine(msg => Log.Information(msg));
            downloadSM = new WorldDownloadStateMachine(msg => Log.Information(msg));

            // 注册状态机回调
            connectionSM.OnDisconnectedEnter += () =>
            {
                if (client.IsConnected) { try { client.LeaveGame(); } catch { } }
            };
            connectionSM.OnPlayingEnter += () => IsHost = (client.ClientID == 0);

            downloadSM.OnCompleteEnter += () => connectionSM.TransitionTo(
                NetworkConnectionStateMachine.ConnectionState.Playing);
            downloadSM.OnFailedEnter += (reason) =>
            {
                Log.Error($"[DL] Failed: {reason}");
                Dispatcher.Dispatch(() =>
                {
                    HideJoinRoomBusyDialog();
                    DialogsManager.ShowDialog(null, new MessageDialog(
                        "Join Room", reason ?? "World download failed.",
                        "OK", null, null));
                });
            };

            // EventBus
            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
                HandleGameDatabase((Database)args[0]), EventPriority.HIGHEST);
            eventBus.SubscribeEvent("BlocksManager.Initialize",
                HandleBlocksManagerInitialize, EventPriority.HIGHEST);
            eventBus.SubscribeEvent("Loading.Initialize", args =>
                HandleLoading(args), EventPriority.HIGHEST);
            eventBus.SubscribeEvent("Frame.Update", args =>
            {
                // Source: Survivalcraft/Game/Program.cs:Program.Run
                // Menu/loading state and the post-game-update network scheduler share this
                // once-per-rendered-frame entry point.
                UpdateFrame(Time.FrameDuration);
                ProcessEndOfFrameActions();
                m_controlUnit?.FlushDiagnostics();
                // Source: ScMultiplayer.cs:ScMultiplayer.ProcessEndOfFrameActions
                // Sample Apply only after this frame consumed its network work.
                UpdateNetworkStatsOverlay();
                UpdateClientTerrainRecoveryAfterNetworkActions();
                CleanupDownloadedWorldsIfIdle();
                return args;
            }, EventPriority.LOWEST);

            CleanupDownloadedWorldsIfIdle();
            GameManager.ProjectDisposed += HandleProjectDisposed;
            Window.Deactivated += HandleWindowDeactivated;
            Window.Activated += HandleWindowActivated;

            // 初始化网络
            // Source: Mod/Comms/Comms.Drt/Func/Server/Server.cs:Server.Server
            // Source: Comms.Drt/Func/Server/Server.cs:Server.Server
            // Five 10ms logic steps remain batched into one 50ms transport tick. The independent
            // 32Hz power-of-two message scheduler must not alter Client.Step's 100Hz logic clock.
            float tickDuration = TransportTickDuration;
            int stepsPerTick = LogicStepsPerTransportTick;
            IReadOnlyList<int> serverPorts = ScMultiplayerSettings.ServerPorts;
            Log.Information($"[ScMP] Scanning server ports {serverPorts[0]}-" +
                $"{serverPorts[serverPorts.Count - 1]}");

            // 探测物理 LAN IP（避免虚拟网卡如 ZeroTier/WSL/CFW 导致广播源不可达）
            var lanAddress = DetectLanAddress();
            Log.Information($"[ScMP] Detected LAN address: {lanAddress}");

            // UdpTransmitter(now) 只接受 localPort 参数，自动检测 LAN 地址
            var serverTransmitter = BindFirstAvailableServerPort(
                ScMultiplayerSettings.ServerBindPorts,
                out int port);
            var explorerTransmitter = new UdpTransmitter(0);
            var serverDiagnosticTransmitter = new DiagnosticTransmitter(serverTransmitter);
            m_serverNetworkStats = serverDiagnosticTransmitter.Stats;
            m_networkMetricsCollector.Reset();

            try
            {
                server = new Server(0x53634d70, tickDuration, stepsPerTick,
                    serverDiagnosticTransmitter);
                ConfigurePeerTimeout(server.Peer, RemoteConnectionLostPeriod);
                // Source: Mod/Comms/Comms.Drt/Func/Server/Set/ServerSettings.cs:ServerSettings.JoinRequestTimeout
                // Manual approval can remain pending while the host finishes another action.
                server.Settings.JoinRequestTimeout = 300f;
                server.Information += Server_Information;
                server.Start();
                Log.Information($"[ScMP] Server started OK, address={server.Address}");
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Server start FAILED: {ex.Message}");
            }

            explorer = new Explorer(0x53634d70, serverPorts, explorerTransmitter);
            double nextExplorerErrorLogTime = 0.0;
            int suppressedExplorerErrors = 0;
            explorer.Error += ex =>
            {
                double now = Time.RealTime;
                if (now < nextExplorerErrorLogTime)
                {
                    suppressedExplorerErrors++;
                    return;
                }
                string suffix = suppressedExplorerErrors > 0
                    ? $" (suppressed {suppressedExplorerErrors} repeats)"
                    : string.Empty;
                Log.Error($"[Explorer] {ex.Message}{suffix}");
                suppressedExplorerErrors = 0;
                nextExplorerErrorLogTime = now + 5.0;
            };
            // Source: Comms/Comms/Peer.cs:Peer.DiscoverLocalPeers
            // On Android, tun0 may receive a ZeroTier broadcast but reject sending one back.
            // Let the local Explorer unicast-probe the request source, with per-address throttling.
            server.Peer.PeerDiscoveryRequest += HandleReverseDiscoveryRequest;

            client = CreateStartedClient(RemoteConnectionLostPeriod);

            // Source: Mod/Comms/Comms.Drt/Func/Explorer/Explorer.cs:Explorer.StartDiscovery
            m_remoteServerDirectory = new RemoteServerDirectory(explorer);
            m_remoteServerDirectory.Start();
            connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Discovering);
            Log.Information($"[ScMP] Explorer discovery started (address={explorerTransmitter.Address})");

            // World synchronization is registered as a database subsystem and is recreated
            // together with every Project, including downloaded-world reloads.
        }

        // Source: Mod/Comms/Comms/UdpTransmitter.cs:UdpTransmitter.UdpTransmitter
        private static UdpTransmitter BindFirstAvailableServerPort(
            IReadOnlyList<int> serverPorts,
            out int selectedPort)
        {
            SocketException lastError = null;
            foreach (int port in serverPorts)
            {
                try
                {
                    UdpTransmitter transmitter = new UdpTransmitter(port);
                    selectedPort = port;
                    Log.Information($"[ScMP] Selected local server port {port}");
                    return transmitter;
                }
                catch (SocketException error) when (
                    error.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    lastError = error;
                }
            }
            throw new InvalidOperationException(
                $"No free UDP server port exists in {serverPorts[0]}-" +
                $"{serverPorts[serverPorts.Count - 1]}.",
                lastError);
        }

        // Source: Comms.Drt/Func/Server/Server.cs:Server.PeerDiscoveryRequest
        private void HandleReverseDiscoveryRequest(Packet packet)
        {
            if (packet.Address == null || packet.Address.AddressFamily != AddressFamily.InterNetwork ||
                server == null || explorer == null ||
                UdpTransmitter.IsLocalIPv4Address(packet.Address.Address))
                return;

            double now = Comm.GetTime();
            lock (m_reverseDiscoveryProbeTimes)
            {
                if (m_reverseDiscoveryProbeTimes.TryGetValue(packet.Address.Address, out double lastTime) &&
                    now - lastTime < 2.0)
                    return;
                m_reverseDiscoveryProbeTimes[packet.Address.Address] = now;
            }

            try
            {
                // Source: Comms.Drt/Func/Explorer/Explorer.cs:Explorer.DiscoverServer
                // Android clients normally bind at the first free base port. Probing the full
                // server range for every request amplifies VPN discovery traffic unnecessarily.
                IReadOnlyList<int> ports = ScMultiplayerSettings.ServerPorts;
                int count = Math.Min(ReverseDiscoveryPortProbeCount, ports.Count);
                for (int i = 0; i < count; i++)
                    explorer.DiscoverServer(new IPEndPoint(packet.Address.Address, ports[i]));
            }
            catch (Exception error)
            {
                Log.Error($"[SuAPI] Reverse discovery probe failed for {packet.Address.Address}: {error.Message}");
            }
        }

        // Source: Mod/Comms/Comms/Peer.cs:Peer.ProcessPeers
        // Keep host/client failure detection responsive without coupling it to the game tick rate.
        private static void ConfigurePeerTimeout(Peer peer, float connectionLostPeriod)
        {
            if (peer == null) return;
            peer.Settings.KeepAlivePeriod = 2f;
            peer.Settings.KeepAliveResendPeriod = 1f;
            peer.Settings.ConnectTimeOut = 300f;
            peer.Settings.ConnectionLostPeriod = connectionLostPeriod;
        }

        // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.Client
        private Client CreateStartedClient(float connectionLostPeriod)
        {
            var clientDiagnosticTransmitter = new DiagnosticTransmitter(new UdpTransmitter(0));
            m_clientNetworkStats = clientDiagnosticTransmitter.Stats;
            m_networkMetricsCollector.Reset();
            m_reliableRetryLimitBaseline = 0L;
            m_reliableStallReconnectIssued = false;
            var newClient = new Client(0x53634d70, clientDiagnosticTransmitter);
            ConfigurePeerTimeout(newClient.Peer, connectionLostPeriod);
            newClient.GameCreated += Client_GameCreated;
            newClient.GameJoined += Client_GameJoined;
            // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.Disconnected
            // A peer can close cleanly without raising Client.Error, and a disposed endpoint can
            // raise its final event after a replacement has started.
            // Capture the owner so that event cannot tear down the new session.
            newClient.Disconnected += data => Client_Disconnected(newClient, data);
            newClient.Error += error => Client_Error(newClient, error);
            newClient.GameDescriptionRequest += Client_GameDescriptionRequest;
            newClient.ConnectRefused += Client_ConnectRefused;
            newClient.ConnectTimedOut += Client_ConnectTimedOut;
            newClient.GameStateRequest += Client_GameStateRequest;
            newClient.GameStep += Client_GameStep;
            newClient.DirectInput += Client_DirectInput;
            newClient.Start();
            return newClient;
        }

        // Source: Mod/Comms/Comms/Peer.cs:Peer.ProcessPeers
        // A transient UDP timeout must not destroy the downloaded world immediately. Rejoin the
        // same room with bounded exponential backoff, then use the normal host-disconnect cleanup.
        private void UpdateHostReconnect()
        {
            if (m_reconnectRequested)
            {
                m_reconnectRequested = false;
                if (!IsHost && !m_localLeaveInProgress && !m_hostDisconnectHandled &&
                    m_activeJoinRequest?.WorldInfo != null)
                {
                    m_reconnectPending = true;
                    m_reconnectAttempts = 0;
                    m_reconnectAttemptDeadline = 0.0;
                    m_nextReconnectAttemptTime = Time.RealTime + ReconnectInitialDelay;
                    Log.Information("[ScMP] Host connection interrupted; reconnect scheduled");
                }
            }

            if (!m_reconnectPending) return;
            if (IsHost || m_localLeaveInProgress || m_hostDisconnectHandled)
            {
                m_reconnectPending = false;
                return;
            }
            double now = Time.RealTime;
            if (m_reconnectAttemptDeadline > 0.0 && now >= m_reconnectAttemptDeadline)
            {
                // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.Dispose
                // A route change can leave Peer.ConnectingTo set indefinitely. Do not wait for
                // its broad transport timeout; a reconnect attempt owns a short handshake window
                // and must use a fresh UDP endpoint after that window expires.
                Log.Warning($"[ScMP] Reconnect attempt {m_reconnectAttempts}/" +
                    $"{MaxReconnectAttempts} exceeded its handshake deadline");
                Client previousClient = client;
                client = null;
                try { previousClient?.Dispose(); }
                catch (Exception ex) { Log.Warning($"[ScMP] Failed to dispose stalled reconnect client: {ex.Message}"); }
                client = CreateStartedClient(RemoteConnectionLostPeriod);
                m_reconnectAttemptDeadline = 0.0;
                m_nextReconnectAttemptTime = now;
            }
            if (client == null || m_activeJoinRequest?.WorldInfo == null)
            {
                m_reconnectPending = false;
                HandleHostDisconnected();
                return;
            }
            if (client.IsConnected || client.IsConnecting || now < m_nextReconnectAttemptTime)
                return;
            if (m_reconnectAttempts >= MaxReconnectAttempts)
            {
                Log.Error($"[ScMP] Host reconnect failed after {m_reconnectAttempts} attempts");
                m_reconnectPending = false;
                HandleHostDisconnected();
                return;
            }

            m_reconnectAttempts++;
            double retryDelay = Math.Min(ReconnectMaxDelay,
                ReconnectInitialDelay * Math.Pow(2.0, m_reconnectAttempts - 1));
            m_nextReconnectAttemptTime = now + retryDelay;
            m_pendingJoinRequest = m_activeJoinRequest;
            m_isLoadingDownloadedWorld = true;
            try
            {
                Log.Information($"[ScMP] Host reconnect attempt {m_reconnectAttempts}/" +
                    $"{MaxReconnectAttempts} to {m_activeJoinRequest.ServerAddress}");
                SubmitPendingJoin(m_activeJoinPlayerName, m_activeJoinPlayerClass,
                    m_activeJoinSkinName, m_activeJoinHasPlayerProfile);
                m_reconnectAttemptDeadline = now + ReconnectHandshakeTimeout;
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Host reconnect attempt {m_reconnectAttempts} failed: {ex.Message}");
                m_reconnectAttemptDeadline = 0.0;
                m_nextReconnectAttemptTime = now + ReconnectInitialDelay;
            }
        }

        // Source: Mod/Comms/Comms/Comm.cs:Comm.ProcessConnections
        // A reliable packet remains in the resend set after MaxResends and otherwise retries
        // forever. Once a fully joined client observes that limit while packets are still
        // outstanding, rebuild the transport through the existing authoritative rejoin flow.
        private void UpdateReliableTransportHealth()
        {
            if (IsHost)
            {
                UpdateHostReliableTransportHealth();
                return;
            }
            if (m_localLeaveInProgress || m_hostDisconnectHandled ||
                m_reconnectRequested || m_reconnectPending || client?.IsConnected != true ||
                m_activeJoinRequest?.WorldInfo == null || GameManager.Project == null ||
                m_isLoadingDownloadedWorld || m_worldTransferRegistry.PendingWorldReadyTransferId > 0 ||
                m_worldTransferRegistry.IncomingTransfers.Count > 0)
                return;

            try
            {
                PeerData connected = client.Peer?.ConnectedTo;
                if (connected == null) return;
                int reliableQueue = client.Peer.Comm.GetUnackedPacketsCount(
                    connected.Address);
                long retryLimit = client.Peer.Comm.GetReliableRetryLimitCount(
                    connected.Address);
                if (reliableQueue <= 0)
                {
                    m_reliableRetryLimitBaseline = retryLimit;
                    m_reliableStallReconnectIssued = false;
                    m_reliableStallSince = 0.0;
                    return;
                }
                if (m_reliableStallReconnectIssued ||
                    retryLimit <= m_reliableRetryLimitBaseline)
                    return;

                // Source: Mod/Comms/Comms/Comm.cs:Comm.ProcessConnections
                // MaxResends already covers the initial outage. Allow the remaining part of the
                // normal 15-second peer timeout for delayed ACKs before rebuilding the transport.
                if (m_reliableStallSince <= 0.0)
                {
                    m_reliableStallSince = Time.RealTime;
                    return;
                }
                if (Time.RealTime - m_reliableStallSince < ReliableStallGracePeriod)
                    return;

                m_reliableStallReconnectIssued = true;
                m_reconnectRequested = true;
                Log.Warning($"[ScMP] Reliable transport stalled: Rel={reliableQueue}, " +
                    $"Limit={retryLimit}; reconnecting to refresh authoritative state");
                try { client.LeaveGame(); }
                catch (Exception ex)
                {
                    Log.Warning($"[ScMP] Failed to close stalled transport: {ex.Message}");
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // Source: Mod/Comms/Comms/Comm.cs:Comm.ProcessConnections
        // The host owns one reliable connection per remote client. A dead peer can otherwise
        // retain every unacknowledged fragment forever and create a continuous resend storm.
        private void UpdateHostReliableTransportHealth()
        {
            if (m_localLeaveInProgress || server?.Peer == null ||
                client?.IsConnected != true || Time.RealTime < m_nextHostReliableHealthTime)
                return;
            m_nextHostReliableHealthTime = Time.RealTime + 0.5;

            HashSet<int> connectedIds = new HashSet<int>();
            foreach (ServerClient remote in GetConnectedRemoteClients())
            {
                connectedIds.Add(remote.ClientID);
                PeerData peer = server.Peer.FindPeer(remote.Address);
                if (peer == null) continue;

                int reliableQueue = server.Peer.Comm.GetUnackedPacketsCount(peer.Address);
                long retryLimit = server.Peer.Comm.GetReliableRetryLimitCount(peer.Address);
                if (reliableQueue <= 0)
                {
                    m_hostReliableRetryLimitBaselines[remote.ClientID] = retryLimit;
                    m_hostReliableStallSince.Remove(remote.ClientID);
                    m_hostReliableStallDisconnectIssued.Remove(remote.ClientID);
                    continue;
                }

                if (!m_hostReliableRetryLimitBaselines.TryGetValue(
                    remote.ClientID, out long baseline))
                {
                    m_hostReliableRetryLimitBaselines[remote.ClientID] = retryLimit;
                    continue;
                }
                if (retryLimit <= baseline)
                    continue;

                // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
                // ScMultiplayer.UpdateReliableTransportHealth
                // A transient ACK blackout must not disconnect a remote player immediately.
                if (!m_hostReliableStallSince.TryGetValue(remote.ClientID,
                        out double stallSince))
                {
                    m_hostReliableStallSince[remote.ClientID] = Time.RealTime;
                    continue;
                }
                if (Time.RealTime - stallSince < ReliableStallGracePeriod ||
                    !m_hostReliableStallDisconnectIssued.Add(remote.ClientID))
                    continue;

                Log.Warning($"[ScMP] Host reliable transport stalled: ClientID={remote.ClientID}, " +
                    $"Rel={reliableQueue}, Limit={retryLimit}; disconnecting stale peer");
                DisconnectNetworkClient(remote);
            }

            foreach (int clientId in m_hostReliableRetryLimitBaselines.Keys
                .Where(id => !connectedIds.Contains(id)).ToArray())
            {
                m_hostReliableRetryLimitBaselines.Remove(clientId);
                m_hostReliableStallSince.Remove(clientId);
                m_hostReliableStallDisconnectIssued.Remove(clientId);
            }
        }

        private object[] HandleLoading(object[] args)
        {
            // Source: EntitySystem/SuAPICore/Plug/SuAPICoreMod.cs:SuAPICoreMod.HandleScreen
            // Loading.Initialize can be published by compatibility layers. Register screens only
            // for the authoritative LoadingManager initialization event.
            if (args == null || args.Length == 0 || args[0] is not Type type ||
                type.Name != "LoadingManager")
                return args;
            // Source: Survivalcraft/Game/Program.cs:Program.Initialize
            // Source: Survivalcraft/Game/LoadingManager.cs:LoadingManager.ReplaceItem
            Game.LoadingManager.QueueItem("Load ScMultiplayer Chinese Font",
                MultiplayerChineseFont.Load);
            if (!Game.LoadingManager.ReplaceItem("Initialize PlayScreen", delegate
            {
                ScreensManager.AddScreen("Play", new SuPlayScreen());
                // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.PlayerScreen
                ScreensManager.AddScreen("ScMultiplayerPlayer", new SuNetworkPlayerScreen());
                // Source: Survivalcraft/Game/ScreensManager.cs:ScreensManager.Initialize
                ScreensManager.AddScreen("ScMultiplayerModifyNetWorld",
                    new SuModifyNetWorldScreen());
            }))
            {
                throw new InvalidOperationException("Loading item 'Initialize PlayScreen' was not found.");
            }
            return args;
        }

        private object[] HandleBlocksManagerInitialize(object[] args)
        {
            // Source: Survivalcraft/Game/BlocksManager.cs:BlocksManager.Initialize
            if (args?.Length != 1 || args[0] is not Block[] blocks)
                return new object[] { false, args != null && args.Length > 0 ? args[0] : null };

            m_modInjector?.ApplyBlocks(blocks, Name);
            return new object[] { true, blocks };
        }

        public object[] HandleGameDatabase(Database database)
        {
            var componentInput = database.FindDatabaseObject(
                new Guid("ec809766-ba61-434e-bfde-e677f506b887"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentInput.Value = "ScMultiplayer.SuComponentInput";

            m_modInjector?.Apply(database, "ScMultiplayer");

            // Source: Pak/Database.xml:ComponentVitalStats.Class
            var componentVitalStats = database.FindDatabaseObject(
                new Guid("aa7f845d-165e-4fff-95f0-453cd4e14cea"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentVitalStats.Value = "ScMultiplayer.SuComponentVitalStats";

            // Source: Pak/Database.xml:ComponentSleep.Class
            var componentSleep = database.FindDatabaseObject(
                new Guid("46dd5d8f-8a84-4bb6-bb7c-bc42afe926d4"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentSleep.Value = "ScMultiplayer.SuComponentSleep";

            // Source: Pak/Database.xml:ComponentFlu.Class
            var componentFlu = database.FindDatabaseObject(
                new Guid("88c778ff-b238-4303-b1c5-468cb0f6c73a"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentFlu.Value = "ScMultiplayer.SuComponentFlu";

            // Source: Pak/Database.xml:ComponentFurnace.Class
            var componentFurnace = database.FindDatabaseObject(
                new Guid("f04c23fe-1d3c-467e-81bc-1796f686be51"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentFurnace.Value = "ScMultiplayer.SuComponentFurnace";

            var subsystemTerrain = database.FindDatabaseObject(
                new Guid("e2636c38-f179-4aa1-b087-ed6920d66e8e"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemTerrain.Value = "ScMultiplayer.SuSubsystemTerrain";

            // Source: Pak/Database.xml:SubsystemWeather.Class
            var subsystemWeather = database.FindDatabaseObject(
                new Guid("b4f7e5b0-df22-47ea-a1eb-df191df54f2e"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemWeather.Value = "ScMultiplayer.SuSubsystemWeather";

            // Source: Pak/Database.xml:SubsystemSpawn.Class
            var subsystemSpawn = database.FindDatabaseObject(
                new Guid("09091863-1852-4c05-ade0-d57fe04289e3"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemSpawn.Value = "ScMultiplayer.SuSubsystemSpawn";

            // Source: Pak/Database.xml:SubsystemCreatureSpawn.Class
            var subsystemCreatureSpawn = database.FindDatabaseObject(
                new Guid("d3764c71-e1e7-48b1-b12a-17428daad169"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemCreatureSpawn.Value = "ScMultiplayer.SuSubsystemCreatureSpawn";

            // Source: Pak/Database.xml:SubsystemGrassBlockBehavior.Class
            var subsystemGrass = database.FindDatabaseObject(
                new Guid("e167fcdc-6960-4487-ace1-6a56eecae003"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemGrass.Value = "ScMultiplayer.SuSubsystemGrassBlockBehavior";

            // Source: Pak/Database.xml:DeciduousLeavesBlockBehavior.Class
            var subsystemDeciduousLeaves = database.FindDatabaseObject(
                new Guid("ea363abf-49f7-4106-a487-1726304f8214"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemDeciduousLeaves.Value =
                "ScMultiplayer.SuSubsystemDeciduousLeavesBlockBehavior";

            // Source: Pak/Database.xml:PlantBlockBehavior.Class
            var subsystemPlant = database.FindDatabaseObject(
                new Guid("1c95cd40-26be-44cf-938a-157b318ff086"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemPlant.Value = "ScMultiplayer.SuSubsystemPlantBlockBehavior";

            // Source: Pak/Database.xml:SubsystemExplosions.Class
            var subsystemExplosions = database.FindDatabaseObject(
                new Guid("96e79f99-a082-4190-9ab6-835dc49ebbdd"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemExplosions.Value = "ScMultiplayer.SuSubsystemExplosions";

            // Source: Pak/Database.xml:FireBlockBehavior.Class
            var subsystemFire = database.FindDatabaseObject(
                new Guid("049e50e8-f0f4-4ae9-990f-965fe77b625c"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemFire.Value = "ScMultiplayer.SuSubsystemFireBlockBehavior";

            // Source: Pak/Database.xml:Projectiles.Class
            var subsystemProjectiles = database.FindDatabaseObject(
                new Guid("dafb8e14-11b9-44b7-a208-424b770aeaa9"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemProjectiles.Value = "ScMultiplayer.SuSubsystemProjectiles";

            // Source: Pak/Database.xml:SubsystemPickables.Class
            var subsystemPickables = database.FindDatabaseObject(
                new Guid("32d392de-69c1-4d04-9e0b-5c7463201892"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemPickables.Value = "ScMultiplayer.SuSubsystemPickables";

            // Source: Pak/Database.xml:WhistleBlockBehavior.Class
            var subsystemWhistle = database.FindDatabaseObject(
                new Guid("87c04d2e-b460-4934-a59d-3b63261e16e4"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemWhistle.Value = "ScMultiplayer.SuSubsystemWhistleBlockBehavior";

            // Source: Pak/Database.xml:SubsystemMemoryBankBlockBehavior.Class
            var subsystemMemoryBank = database.FindDatabaseObject(
                new Guid("32a2d9ef-b01a-4f80-a6f8-5d2d5e9e9275"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemMemoryBank.Value =
                "ScMultiplayer.SuSubsystemMemoryBankBlockBehavior";

            // Source: Pak/Database.xml:SubsystemTruthTableCircuitBlockBehavior.Class
            var subsystemTruthTable = database.FindDatabaseObject(
                new Guid("a66d56a3-a8e1-4407-9a77-05ecdcb9766b"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemTruthTable.Value =
                "ScMultiplayer.SuSubsystemTruthTableCircuitBlockBehavior";

            // Source: Pak/Database.xml:AdjustableDelayGateBlockBehavior.Class
            var subsystemAdjustableDelay = database.FindDatabaseObject(
                new Guid("64552991-c904-476c-a0bb-3b2710e54433"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemAdjustableDelay.Value =
                "ScMultiplayer.SuSubsystemAdjustableDelayGateBlockBehavior";

            // Source: Pak/Database.xml:SwitchBlockBehavior.Class
            var subsystemSwitch = database.FindDatabaseObject(
                new Guid("4e3b2bf3-e8b9-4317-b52a-26ce3070d2e3"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemSwitch.Value = "ScMultiplayer.SuSubsystemSwitchBlockBehavior";

            // Source: Pak/Database.xml:ButtonBlockBehavior.Class
            var subsystemButton = database.FindDatabaseObject(
                new Guid("4407e30b-89ee-40c7-b1d2-eb100c2b8ac4"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemButton.Value = "ScMultiplayer.SuSubsystemButtonBlockBehavior";

            // Source: Pak/Database.xml:PistonBlockBehavior.Class
            var subsystemPiston = database.FindDatabaseObject(
                new Guid("937999c9-9570-4cbd-8390-23f1e4609cdd"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemPiston.Value = "ScMultiplayer.SuSubsystemPistonBlockBehavior";

            // Source: Pak/Database.xml:DispenserBlockBehavior.Class
            var subsystemDispenser = database.FindDatabaseObject(
                new Guid("4c917896-d6dc-4e4d-b27f-f02deea0f241"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemDispenser.Value = "ScMultiplayer.SuSubsystemDispenserBlockBehavior";

            // Source: Pak/Database.xml:HammerBlockBehavior.Class
            var subsystemHammer = database.FindDatabaseObject(
                new Guid("d447f324-eda9-47d4-8671-af13a1a858cd"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemHammer.Value = "ScMultiplayer.SuSubsystemHammerBlockBehavior";

            // Source: Mod/WatchMod/Plug/WatchMod.cs:WatchMod.HandleGameDatabase
            // Register an independent player component instead of replacing SubsystemGameWidgets.
            var uiTemplate = new DatabaseObject(
                database.FindDatabaseObjectType("ComponentTemplate", true),
                new Guid("61f1848d-baa7-49b1-9652-66410aef1901"),
                "ScMultiplayerUI", null);
            uiTemplate.ExplicitInheritanceParent = database.FindDatabaseObject(
                new Guid("b05700ed-7e4e-4679-98f5-b597f421496b"),
                database.FindDatabaseObjectType("ComponentTemplate", true), true);
            uiTemplate.NestingParent = database.FindDatabaseObject(
                "Gameplay", database.FindDatabaseObjectType("Folder", true), true);

            var uiClass = new DatabaseObject(
                database.FindDatabaseObjectType("Parameter", true),
                new Guid("a49522cb-eaf2-47de-acf5-43d20a035f25"),
                "Class", "ScMultiplayer.MultiplayerUiComponent");
            uiClass.NestingParent = uiTemplate;

            var uiMember = new DatabaseObject(
                database.FindDatabaseObjectType("MemberComponentTemplate", true),
                new Guid("e9d71741-c8ef-4b38-b423-e49b01b3ae5d"),
                "ScMultiplayerUI", null);
            uiMember.ExplicitInheritanceParent = uiTemplate;
            uiMember.NestingParent = database.FindDatabaseObject(
                "Player", database.FindDatabaseObjectType("EntityTemplate", true), true);

            Log.Information("[ScMP] Database hooks applied");
            return new object[] { true, database };
        }

        // ====================================================================
        // 异步注册 IUpdateable
        // ====================================================================
        /// <summary>
        /// 探测物理 LAN IP：优先选择非虚拟网卡的私网 IPv4 地址
        /// 逻辑：连 8.8.8.8 确定默认出口 IP，再验证是否为私网地址且非虚拟网卡
        /// </summary>
        private static System.Net.IPAddress DetectLanAddress()
        {
            try
            {
                // 方法1：通过 UDP 连 8.8.8.8 确定默认路由出口 IP
                using (var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram,
                    System.Net.Sockets.ProtocolType.Udp))
                {
                    socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
                    socket.Connect("8.8.8.8", 12345);
                    var defaultIp = ((System.Net.IPEndPoint)socket.LocalEndPoint).Address;

                    // 检查是否为私网地址 (10.x / 172.16-31.x / 192.168.x)
                    if (IsPrivateAddress(defaultIp))
                    {
                        return defaultIp;
                    }
                }

                // 非私网，继续搜索
            }
            catch { }

            try
            {
                // 方法2：遍历网卡，找第一个非虚拟的私网 IPv4
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    // 跳过虚拟/隧道/回环
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;
                    // 跳过常见的虚拟网卡描述关键词
                    var desc = ni.Description.ToLowerInvariant();
                    if (desc.Contains("wireguard") ||
                        desc.Contains("vmware") || desc.Contains("virtualbox") ||
                        desc.Contains("hyper-v") || desc.Contains("wsl") ||
                        desc.Contains("docker") || desc.Contains("tunnel") ||
                        desc.Contains("cfw") || desc.Contains("clash"))
                        continue;

                    var ipProps = ni.GetIPProperties();
                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            IsPrivateAddress(ua.Address))
                        {
                            if (desc.Contains("zerotier")) return ua.Address;
                            if (ua.Address.ToString().StartsWith("10.160.", StringComparison.Ordinal))
                                return ua.Address;
                            return ua.Address;
                        }
                    }
                }
            }
            catch { }

            // 兜底：返回 Any（让系统自动选择）
            return System.Net.IPAddress.Any;
        }

        private static bool IsPrivateAddress(System.Net.IPAddress addr)
        {
            if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return false;
            var bytes = addr.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            return false;
        }

        public void OnUnload()
        {
            m_controlUnit?.Dispose();
            m_controlUnit = null;
            m_messageRouter = null;
            ReliableRetransmitDiagnostics.PacketRetransmitted -= HandleReliableRetransmit;
            if (m_eventBus != null && m_serverSettingsToken != null)
                m_eventBus.UnsubscribeEvent(m_serverSettingsToken);
            m_serverSettingsToken = null;
            if (m_eventBus != null && m_fromLinkToken != null)
                m_eventBus.UnsubscribeEvent(m_fromLinkToken);
            m_fromLinkToken = null;
            m_eventBus = null;
            Window.Deactivated -= HandleWindowDeactivated;
            Window.Activated -= HandleWindowActivated;
            GameManager.ProjectDisposed -= HandleProjectDisposed;
            if (m_networkStatsLabel?.ParentWidget != null)
                m_networkStatsLabel.ParentWidget.Children.Remove(m_networkStatsLabel);
            m_networkStatsLabel = null;
            try
            {
                m_worldTransferSendCancellation?.Cancel();
                m_worldTransferSendSignal.Release();
                m_worldTransferSendTask?.Wait(1000);
            }
            catch { }
            try { client?.LeaveGame(); } catch { }
            try { server?.Dispose(); } catch { }
            try { explorer?.StopDiscovery(); } catch { }
            m_remoteServerDirectory?.Stop();
            m_remoteServerDirectory = null;
        }
    }
}
