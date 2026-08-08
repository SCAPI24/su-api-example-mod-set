using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using SuAPICore;
using ScMultiplayer.Core;
using ScMultiplayer.Control;
using ScMultiplayer.Diagnostics;
using ScMultiplayer.Ports;
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
    // Shared state is kept in one partial file while ownership moves module by module.
    public partial class ScMultiplayer
    {
        public static ModManager ModManager = (ModManager)AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .FirstOrDefault(t => t.FullName == "Game.Program")?
            .GetField("ModManager", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        public static Server server;
        public static Client client;
        public static Explorer explorer;
        public static ScMultiplayer currentInstance;
        public static PlayerMappingManager playerMappingManager = new PlayerMappingManager();
        public static PlayerOperationSyncManager playerOperationSyncManager = new PlayerOperationSyncManager();
        public static bool IsHost = false;
        private MultiplayerControlUnit m_controlUnit;
        private NetworkMessageRouter m_messageRouter;

        // ---------- 游戏描述缓存 (LanDiscovery 响应用) ----------
        public static byte[] LastGameDescription;

        // ---------- 远程玩家 ----------
        public static Dictionary<int, NetworkPlayerState> RemotePlayers = new Dictionary<int, NetworkPlayerState>();
        private PrimitivesRenderer3D m_primitivesRenderer3D;

        // ---------- 状态机 ----------
        public static NetworkConnectionStateMachine connectionSM;
        public static WorldDownloadStateMachine downloadSM;

        // ---------- IMod ----------
        public string Name => "SC联机";
        public string Version => Message.ModVersion;
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;

        public bool IsInRoom => m_controlUnit?.Context.Session.IsConnected == true &&
            m_controlUnit.Context.Session.GameId >= 0;

        internal IMultiplayerUiCommandPort UiCommands => this;

        // ---------- 内部状态 ----------
        private float m_syncPulseAccumulator;
        private IModEventBus m_eventBus;
        private EventSubscriptionToken m_fromLinkToken;
        private double m_lastSyncUpdateTime;
        private uint m_syncPulseIndex;
        private Project m_frameProject;
        private Project m_projectReadySentProject;
        private int m_projectReadySentTransferId;
        private int m_lastWorldUpdateFrameIndex = -1;
        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Update
        // Store only values actually sent by the host. This keeps small natural changes quiet
        // while allowing meaningful heat, stamina, wetness and sleep changes through promptly.
        private readonly Dictionary<int, AuthoritativePlayerStateSnapshot>
            m_lastSentAuthoritativePlayerStates =
                new Dictionary<int, AuthoritativePlayerStateSnapshot>();
        private readonly Dictionary<int, int> m_authoritativePlayerStateSequences =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> m_lastReceivedAuthoritativePlayerStateSequences =
            new Dictionary<int, int>();
        private int m_lastAuthoritativeLocalWholeLevel = -1;
        private readonly Dictionary<int, float> m_hostKnockbackHealthCache =
            new Dictionary<int, float>();
        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
        private readonly Dictionary<int, int> m_damageSequences = new Dictionary<int, int>();
        private readonly Dictionary<int, int> m_receivedDamageSequences = new Dictionary<int, int>();
        private readonly Dictionary<int, double> m_hostRemoteKnockbackUntil =
            new Dictionary<int, double>();
        private readonly Dictionary<int, int> m_hostKnockbackSequences =
            new Dictionary<int, int>();
        private bool m_hasObservedClientHealth;
        private float m_observedClientHealth;
        private float m_observedClientFood;
        private bool m_observedClientSleeping;
        private bool m_hasAuthoritativeLocalInventory;
        private int[] m_authoritativeLocalSlotValues = Array.Empty<int>();
        private int[] m_authoritativeLocalSlotCounts = Array.Empty<int>();
        private readonly Dictionary<int, PlayerData> m_networkPlayerData = new Dictionary<int, PlayerData>();
        private readonly Dictionary<int, int> m_reservedNetworkPlayerIndices =
            new Dictionary<int, int>();
        private readonly HashSet<int> m_creatingNetworkPlayers = new HashSet<int>();
        private readonly Dictionary<int, string> m_pendingNetworkPlayers = new Dictionary<int, string>();
        private readonly Dictionary<int, string> m_pendingNetworkPlayerIdentities = new Dictionary<int, string>();
        private readonly Dictionary<int, NetworkPlayerInputState> m_networkPlayerInputs =
            new Dictionary<int, NetworkPlayerInputState>();
        // Source: Mod/Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.Handle
        // Client IDs increase for the lifetime of a room, so a leave tombstone safely rejects
        // delayed profile/entity messages until the next room resets transient state.
        private readonly HashSet<int> m_departedRemoteClientIds = new HashSet<int>();
        private PlayerInput m_localPlayerInput;
        private Vector3 m_localInputBodyPosition;
        private Vector3 m_localInputBodyVelocity;
        private Quaternion m_localInputBodyRotation = Quaternion.Identity;
        private Vector2 m_localInputLookAngles;
        private int m_localInputSequence;
        private int m_lastSentInputSequence = -1;
        private int m_localInputResendsRemaining;
        private bool m_localAimActive;
        private int m_localAimSequence;
        private int m_localAimSlot = -1;
        private int m_localAimItemValue;
        private int m_localAimItemCount;
        private Ray3 m_localAimRay;
        private double m_lastAimUpdateSentTime;
        private float m_smoothedNetworkDelay;
        private readonly Dictionary<string, NetworkPlayerRecord> m_playerRecords = new Dictionary<string, NetworkPlayerRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string> m_clientRecordKeys = new Dictionary<int, string>();
        private readonly Queue<ChatMessage> m_recentChatMessages = new Queue<ChatMessage>();
        private readonly HashSet<Guid> m_recentChatMessageIds = new HashSet<Guid>();
        private IModInjector m_modInjector;
        private EventSubscriptionToken m_serverSettingsToken;
        private LabelWidget m_networkStatsLabel;
        // Source: Comms/Comms/DiagnosticTransmitter.cs:DiagnosticStats
        private DiagnosticStats m_serverNetworkStats;
        private DiagnosticStats m_clientNetworkStats;
        private readonly NetworkMetricsCollector m_networkMetricsCollector =
            new NetworkMetricsCollector();
        private double m_nextNetworkStatsUpdateTime;
        // Source: Comms/Comms/DiagnosticTransmitter.cs:DiagnosticStats.BytesSent
        // The sender records join bytes separately. Any retransmission not attributable to the
        // first send is intentionally treated as gameplay pressure, which conservatively pauses
        // new joins on a congested link instead of stealing capacity from connected players.
        private long m_joinTransferBytesSentSinceSample;
        private long m_lastJoinTransferBytesSample;
        private long m_lastJoinTransferNetworkBytesSample;
        private long m_lastJoinTransferReceiveBytesSample;
        private double m_lastJoinTransferSampleTime;
        private readonly Queue<JoinTransferTrafficSample> m_joinTransferTrafficSamples =
            new Queue<JoinTransferTrafficSample>();
        private double m_joinTransferTokens;
        private double m_joinTransferLastTokenTime;
        private double m_joinTransferAvailableBytesPerSecond;
        private double m_joinTransferGameplayBytesPerSecond;
        private double m_joinTransferBytesPerSecond;
        private double m_joinTransferReceiveBytesPerSecond;
        private bool m_joinTransferPausedByGameplay;
        // Source: Comms/Comms/Peer.cs:GetPacketLossRate
        // Automatic mode uses additive growth with an immediate multiplicative backoff. Saved
        // configured limits stay untouched and are never consulted while Automatic is selected.
        private double m_automaticJoinTransferKbps = AutomaticJoinTransferStartKbps;
        private double m_nextAutomaticJoinTransferAdjustmentTime;
        private double m_automaticJoinTransferCooldownUntil;
        private double m_automaticJoinRttBaseline;
        // Source: Comms/Comms/DiagnosticTransmitter.cs:DiagnosticStats.BytesSent
        // The headless title samples only during its final one-second window. This is separate
        // from the join scheduler, which must continue responding immediately to game traffic.
        private double m_nextServerTrafficSampleStartTime;
        private double m_serverTrafficSampleStartTime;
        private long m_serverTrafficSampleStartBytesSent;
        private long m_serverTrafficSampleStartBytesReceived;
        private long m_serverTrafficSampleStartPacketsSent;
        private long m_serverTrafficSampleStartPacketsReceived;
        private long m_lastServerTrafficSampleBytesSent = -1L;
        private long m_lastServerTrafficSampleBytesReceived = -1L;
        private long m_lastServerTrafficSamplePacketsSent = -1L;
        private long m_lastServerTrafficSamplePacketsReceived = -1L;
        private bool m_serverTrafficSampleActive;
        private readonly Dictionary<IPAddress, double> m_reverseDiscoveryProbeTimes =
            new Dictionary<IPAddress, double>();
        private RemoteServerDirectory m_remoteServerDirectory;
        private string m_playerRecordsWorldDirectory;
        private bool m_playerRecordsDirty;
        private float m_playerRecordSaveTime;
        private float m_inventoryKeyframeTime;
        private readonly Dictionary<int, int[]> m_lastSentInventoryValues =
            new Dictionary<int, int[]>();
        private readonly Dictionary<int, int[]> m_lastSentInventoryCounts =
            new Dictionary<int, int[]>();
        private readonly Dictionary<int, int> m_equipmentAuthorityRevisions =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> m_lastClientEquipmentRevisions =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> m_lastReceivedEquipmentRevisions =
            new Dictionary<int, int>();
        private readonly Dictionary<int, EquipmentSnapshot> m_lastEquipmentSnapshots =
            new Dictionary<int, EquipmentSnapshot>();
        private readonly Queue<EquipmentSnapshot> m_recentLocalEquipmentSnapshots =
            new Queue<EquipmentSnapshot>();
        private readonly HashSet<int> m_equipmentSynchronizedClients = new HashSet<int>();
        private readonly Dictionary<int, PlayerEquipmentMessage> m_pendingPlayerEquipmentMessages =
            new Dictionary<int, PlayerEquipmentMessage>();
        private int m_localEquipmentRevision;
        // Source: Survivalcraft/Game/SubsystemEditableItemBehavior.cs:SubsystemEditableItemBehavior<T>
        private readonly Dictionary<int, int> m_lastEditableDataRequestIds =
            new Dictionary<int, int>();
        private readonly Dictionary<string, int> m_lastEditableDataRevisions =
            new Dictionary<string, int>();
        private readonly Dictionary<string, string> m_lastEditableDataPayloads =
            new Dictionary<string, string>();
        private int m_localEditableDataRequestId;
        private int m_editableDataRevision;
        private CircuitSynchronizer m_circuitSynchronizer;
        private WorldObjectSynchronizer m_worldObjectSynchronizer;

        internal CircuitSynchronizer CircuitSynchronizer => m_circuitSynchronizer;
        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.UpdateReliableTransportHealth
        internal bool ShouldHoldCircuitForReconnect => !IsHost &&
            (m_reconnectRequested || m_reconnectPending);

        // Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:
        // Client_GameJoined; the downloaded Project can enter the native update loop before
        // the network client state and CircuitSynchronizer binding are visible to every phase.
        internal bool ShouldHoldClientCircuitBeforeBinding => !IsHost &&
            (m_activeJoinRequest != null || m_pendingJoinRequest != null ||
            m_isLoadingDownloadedWorld || m_joinAwaitingWorldProgress ||
            m_worldTransferRegistry.PendingWorldReadyTransferId > 0 ||
            m_clientWorldRefreshProject != null);

        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Update
        internal bool ShouldHoldClientSleepTimeline(ComponentPlayer player)
        {
            ComponentSleep sleep = player?.ComponentSleep;
            if (IsHost || client?.IsConnected != true || sleep?.IsSleeping != true)
                return false;
            return m_pendingClientSleepWakeups.Contains(sleep) ||
                ShouldDeferClientSleepWakeup();
        }

        private PendingJoinRequest m_pendingJoinRequest;
        private PendingJoinRequest m_activeJoinRequest;
        private string m_activeJoinPlayerName;
        private PlayerClass m_activeJoinPlayerClass = PlayerClass.Male;
        private string m_activeJoinSkinName;
        private bool m_activeJoinHasPlayerProfile;
        private volatile bool m_reconnectRequested;
        private bool m_reconnectPending;
        private int m_reconnectAttempts;
        private double m_nextReconnectAttemptTime;
        private double m_reconnectAttemptDeadline;
        private long m_reliableRetryLimitBaseline;
        private bool m_reliableStallReconnectIssued;
        private double m_reliableStallSince;
        private readonly Dictionary<int, long> m_hostReliableRetryLimitBaselines =
            new Dictionary<int, long>();
        private readonly Dictionary<int, double> m_hostReliableStallSince =
            new Dictionary<int, double>();
        private readonly HashSet<int> m_hostReliableStallDisconnectIssued =
            new HashSet<int>();
        private double m_nextHostReliableHealthTime;
        private bool m_joinAwaitingWorldProgress;
        private double m_lastJoinWorldProgressTime;
        private NetworkPlayerRecord m_pendingLocalPlayerRecord;
        private PlayerData m_localReplacementPlayerData;
        private bool m_localPlayerRecordQueued;
        private bool m_localPlayerRecordApplied;
        private bool m_replacingLocalPlayerData;
        private const float HealthSyncInterval = 1.0f; // 每秒同步一次生命
        private string m_downloadedWorldDirectory;
        private Project m_clientWorldRefreshProject;
        private bool m_hostDisconnectHandled;
        private bool m_localLeaveInProgress;
        private bool m_shouldCreateHostAvatar;
        private bool m_isLoadingDownloadedWorld;
        private BusyDialog m_joinRoomBusyDialog;
        private bool m_createRoomPending;
        private Project m_autoHostProject;
        private bool m_autoHostAttempted;
        private double m_nextAutoHostAttemptTime;
        private double m_localKnockbackPositionCorrectionUntil;
        private int m_localKnockbackCorrectionStartTick = -1;
        private int m_lastLocalKnockbackSequence = -1;
        private int m_lastAuthoritativeLocalPositionTick = -1;
        private ushort m_nextAnimalId = 1;
        private ushort m_nextPickableId = 1;
        private float m_fullWorldObjectsSyncTime;
        private float m_fullAnimalSyncTime;
        private Project m_runawayCreatureCleanupProject;
        private readonly Queue<Entity> m_runawayCreatureCleanup = new Queue<Entity>();
        private double m_nextRunawayCreatureCheckTime;
        private double m_nextRemoteCreatureSpawnTime;
        private int m_remoteCreatureSpawnCursor;
        private Project m_clientWorldObjectsProject;
        private readonly ConcurrentQueue<QueuedFrameAction> m_endOfFrameActions =
            new ConcurrentQueue<QueuedFrameAction>();
        // Source: ScMultiplayer.cs:ScMultiplayer.Client_GameStep
        // Chunk checkpoints are terrain work, not generic frame actions. Keep their FIFO separate
        // so a burst of authoritative cells cannot inflate the normal Apply queue.
        private readonly ConcurrentQueue<QueuedTerrainChunkSync> m_terrainChunkSyncActions =
            new ConcurrentQueue<QueuedTerrainChunkSync>();
        // Source: ScMultiplayer.cs:ProcessEndOfFrameActions
        // Short player edges must not wait behind circuit snapshot or repair work.
        private readonly ConcurrentQueue<QueuedFrameAction> m_priorityInputActions =
            new ConcurrentQueue<QueuedFrameAction>();
        // Source: ScMultiplayer.cs:ProcessEndOfFrameActions
        // World-transfer control must not wait behind high-rate gameplay presentation work.
        private readonly ConcurrentQueue<QueuedFrameAction> m_worldTransferActions =
            new ConcurrentQueue<QueuedFrameAction>();
        private readonly Dictionary<Entity, ushort> m_hostAnimalIds = new Dictionary<Entity, ushort>();
        private readonly List<Entity> m_hostAnimals = new List<Entity>();
        private readonly Dictionary<Entity, AnimalSyncMetadata> m_hostAnimalSync =
            new Dictionary<Entity, AnimalSyncMetadata>();
        private readonly Dictionary<ushort, Entity> m_remoteAnimals = new Dictionary<ushort, Entity>();
        private readonly Dictionary<ushort, string> m_remoteAnimalTemplates = new Dictionary<ushort, string>();
        private readonly Dictionary<ushort, RemoteAnimalSyncState> m_remoteAnimalSync =
            new Dictionary<ushort, RemoteAnimalSyncState>();
        private readonly HashSet<ushort> m_loggedRemoteAnimalFailures = new HashSet<ushort>();
        private int m_lastFullAnimalSnapshotTick;
        private readonly Dictionary<Pickable, ushort> m_hostPickableIds = new Dictionary<Pickable, ushort>();
        private SubsystemPickables m_hostPickablesSubsystem;
        private GameWidget m_clientDropDragHostGameWidget;
        private bool m_forceHostInventorySync;
        private readonly Dictionary<ushort, Pickable> m_remotePickables = new Dictionary<ushort, Pickable>();
        private bool m_applyingNetworkPickable;
        private int m_lastAuthoritativeLocalInventoryTick;
        private int[] m_lastLocalInventoryValues = Array.Empty<int>();
        private int[] m_lastLocalInventoryCounts = Array.Empty<int>();
        private int m_pendingLocalDropValue;
        private int m_pendingLocalDropCount;
        private Vector3 m_pendingLocalDropPosition;
        private double m_pendingLocalDropPredictionUntil;
        private readonly Dictionary<ushort, RemotePickableNetworkState> m_remotePickableStates =
            new Dictionary<ushort, RemotePickableNetworkState>();
        private readonly Dictionary<ushort, PendingPickablePickupPresentation>
            m_pendingPickablePickups =
                new Dictionary<ushort, PendingPickablePickupPresentation>();
        private readonly Dictionary<ushort, PendingPickableAcquireRequest>
            m_pendingPickableAcquireRequests =
                new Dictionary<ushort, PendingPickableAcquireRequest>();
        private readonly Dictionary<long, ProcessedPickableAcquireRequest>
            m_processedPickableAcquireRequests =
                new Dictionary<long, ProcessedPickableAcquireRequest>();
        private readonly HashSet<ushort> m_authoritativePickableAcquireIds =
            new HashSet<ushort>();
        private int m_nextPickableAcquireRequestId;
        private double m_nextPickableAcquireScanTime;
        private readonly Dictionary<Projectile, ushort> m_hostProjectileIds = new Dictionary<Projectile, ushort>();
        private readonly Dictionary<Projectile, int> m_hostProjectileReleaseCompensationSteps =
            new Dictionary<Projectile, int>();
        private readonly Dictionary<ushort, Projectile> m_remoteProjectiles = new Dictionary<ushort, Projectile>();
        private readonly Dictionary<Projectile, double> m_clientPredictedProjectiles =
            new Dictionary<Projectile, double>();
        private readonly HashSet<long> m_displayedProjectileHits = new HashSet<long>();
        private ushort m_nextProjectileId = 1;
        private readonly Dictionary<string, ContainerNetworkState> m_containerStates =
            new Dictionary<string, ContainerNetworkState>();
        private readonly Dictionary<string, PendingContainerTransaction>
            m_pendingContainerTransactions =
                new Dictionary<string, PendingContainerTransaction>();
        private readonly Dictionary<string, ProcessedContainerTransaction>
            m_processedContainerTransactions =
                new Dictionary<string, ProcessedContainerTransaction>();
        private int m_nextContainerRequestId;
        private bool m_wasNetworkContainerOpen;
        private bool m_forceContainerFullSync;
        private Widget m_openContainerPanel;
        private NetworkContainerReference m_openContainer;
        private string m_baselineRequestedContainerKey;
        private readonly HashSet<IUpdateable> m_disabledClientContainerUpdates = new HashSet<IUpdateable>();
        private readonly Dictionary<ushort, RemotePickableRecord> m_remotePickableRecords = new Dictionary<ushort, RemotePickableRecord>();
        private readonly object m_terrainJournalLock = new object();
        private readonly Queue<TerrainJournalEntry> m_hostTerrainJournal =
            new Queue<TerrainJournalEntry>();
        private readonly Dictionary<int, long> m_hostTerrainRecoveryTargets =
            new Dictionary<int, long>();
        private readonly Dictionary<Point2, long> m_hostTerrainChunkRevisions =
            new Dictionary<Point2, long>();
        private long m_hostTerrainSequence;
        private long m_pendingTerrainSequenceBaseline;
        private volatile bool m_clientTerrainRecoveryActive;
        private volatile bool m_clientTerrainRecoveryPending;
        private bool m_clientTerrainRecoveryRequestInFlight;
        private volatile bool m_clientSuspensionRequested;
        private long m_clientTerrainRecoveryTarget = -1;
        private long m_clientTerrainRecoveryAcknowledged = -1;
        private long m_clientTerrainRecoveryReady = -1;
        private long m_remoteTerrainHeadSequence;
        private long m_lastObservedClientTerrainSequence = -1;
        private double m_clientTerrainGapDetectedTime;
        private bool m_clientGameplayScreenObserved;
        private bool m_wasClientGameScreenActive;
        private volatile bool m_clientWindowDeactivated;
        private int m_lastProjectSimulationFrameIndex = -1;
        private readonly Dictionary<Point3, TerrainCellState> m_terrainCheckpoint =
            new Dictionary<Point3, TerrainCellState>();
        private readonly Dictionary<Point2, Dictionary<Point3, TerrainCellState>>
            m_terrainCheckpointByChunk =
                new Dictionary<Point2, Dictionary<Point3, TerrainCellState>>();
        private readonly Dictionary<Point3, TerrainCellState> m_pendingTerrainChanges =
            new Dictionary<Point3, TerrainCellState>();
        private TerrainUpdater m_clientTerrainChunkSyncUpdater;
        private readonly Queue<Point2> m_clientTerrainChunkSyncQueue =
            new Queue<Point2>();
        private readonly HashSet<Point2> m_clientTerrainChunkSyncQueued =
            new HashSet<Point2>();
        private readonly Dictionary<Point2, double> m_clientTerrainChunkSyncPending =
            new Dictionary<Point2, double>();
        private readonly Dictionary<Point2, long> m_clientTerrainChunkRevisions =
            new Dictionary<Point2, long>();
        private readonly Dictionary<Point2, PendingTerrainChunkVerification>
            m_clientTerrainChunkVerifications =
                new Dictionary<Point2, PendingTerrainChunkVerification>();
        private readonly Dictionary<Point2, PendingTerrainChunkCheckpoint>
            m_clientTerrainChunkCheckpoints =
                new Dictionary<Point2, PendingTerrainChunkCheckpoint>();
        private readonly Dictionary<Point2, long> m_clientTerrainChunkFailedRevisions =
            new Dictionary<Point2, long>();
        private double m_nextTerrainChunkSyncRequestTime;
        private readonly Dictionary<Point3, PendingHostTerrainPlaceFallback>
            m_hostTerrainPlaceFallbacks =
                new Dictionary<Point3, PendingHostTerrainPlaceFallback>();
        private readonly Dictionary<Point3, PendingFluidSettlement>
            m_pendingFluidSettlements =
                new Dictionary<Point3, PendingFluidSettlement>();
        private readonly Dictionary<int, PendingTerrainPrediction> m_pendingTerrainPredictions =
            new Dictionary<int, PendingTerrainPrediction>();
        private readonly Dictionary<Point3, int> m_pendingTerrainPredictionCells =
            new Dictionary<Point3, int>();
        private readonly Dictionary<long, TerrainDigResultMessage> m_processedTerrainDigRequests =
            new Dictionary<long, TerrainDigResultMessage>();
        private readonly Dictionary<Point3, LocalTerrainDigIntent> m_localTerrainDigIntents =
            new Dictionary<Point3, LocalTerrainDigIntent>();
        private readonly Dictionary<Point3, LocalTerrainUsePrediction> m_localTerrainUsePredictions =
            new Dictionary<Point3, LocalTerrainUsePrediction>();
        private readonly Dictionary<int, PendingTerrainPlacePrediction>
            m_pendingTerrainPlacePredictions =
                new Dictionary<int, PendingTerrainPlacePrediction>();
        private readonly Dictionary<Point3, int> m_pendingTerrainPlacePredictionCells =
            new Dictionary<Point3, int>();
        private readonly Dictionary<Point3, double> m_localCollapsingPlacePredictions =
            new Dictionary<Point3, double>();
        private readonly List<HostTerrainPlaceExecution> m_hostTerrainPlaceExecutions =
            new List<HostTerrainPlaceExecution>();
        private readonly List<HostMeleeHitExecution> m_hostMeleeHitExecutions =
            new List<HostMeleeHitExecution>();
        private readonly Dictionary<long, PlayerActionMessage> m_processedTerrainPlaceRequests =
            new Dictionary<long, PlayerActionMessage>();
        private readonly Dictionary<int, float> m_hostPlayerPokingPhases =
            new Dictionary<int, float>();
        private readonly Dictionary<int, int> m_hostPlayerPokeSequences =
            new Dictionary<int, int>();
        private readonly Dictionary<int, int> m_playerWhistleSequences =
            new Dictionary<int, int>();
        private readonly Engine.Random m_audioEventRandom = new Engine.Random();
        private readonly WorldTransferRegistry m_worldTransferRegistry =
            new WorldTransferRegistry();
        private readonly JoinCatchUpRegistry m_joinCatchUpRegistry =
            new JoinCatchUpRegistry();
        private readonly Dictionary<int, string> m_pendingAcceptedJoinKeys =
            new Dictionary<int, string>();
        private readonly Dictionary<int, HostJoinRequest> m_hostJoinRequests =
            new Dictionary<int, HostJoinRequest>();
        private Dialog m_activeJoinDecisionDialog;
        private int m_activeJoinDecisionClientId = -1;
        private readonly ConcurrentQueue<WorldTransferChunkSendWork> m_worldTransferSendQueue =
            new ConcurrentQueue<WorldTransferChunkSendWork>();
        private readonly SemaphoreSlim m_worldTransferSendSignal = new SemaphoreSlim(0);
        private CancellationTokenSource m_worldTransferSendCancellation;
        private Task m_worldTransferSendTask;
        private int m_worldTransferQueuedWorkCount;
        private int m_worldTransferGeneration;
        // Source: Mod/Comms/Comms/Comm.cs:Comm.GetUnackedPacketsCount
        // The host sends targeted payloads through its local DRT server.  Comm therefore observes
        // the relay only after the local server processes the input, while the sender may already
        // have approved more work in the same network tick.  These reservations account for that
        // short relay gap without changing the reliable sequence or Comms itself.
        private sealed class ReliableRelayReservation
        {
            public int Packets;
            public double CreatedTime;
        }

        private const double ReliableRelayReservationMinimumLifetime = 0.15;
        private const double ReliableRelayReservationMaximumLifetime = 0.5;
        private readonly object m_reliableRelayReservationLock = new object();
        private readonly Dictionary<int, Queue<ReliableRelayReservation>>
            m_reliableRelayReservations =
                new Dictionary<int, Queue<ReliableRelayReservation>>();
        private readonly Dictionary<int, int> m_reliableRelayObservedUnacked =
            new Dictionary<int, int>();
        private Point3? m_localDigTarget;
        private int m_localDigPresentationSequence;
        private double m_nextLocalDigPresentationTime;
        private bool m_localDigPresentationActive;
        private CellFace? m_localDigPresentationFace;
        // Source: Survivalcraft/Game/ComponentDiggingCracks.cs:ComponentDiggingCracks.Draw
        private readonly Dictionary<int, RemoteDigPresentation> m_remoteDigPresentations =
            new Dictionary<int, RemoteDigPresentation>();
        private int m_nextTerrainDigRequestId;
        private int m_localHitSequence;
        private double m_nextLocalHitRequestTime;
        private readonly Dictionary<int, LocalMeleePrediction> m_localMeleePredictions =
            new Dictionary<int, LocalMeleePrediction>();
        private int m_localInteractSequence;
        private int m_localDropSequence;
        private int m_localJumpSequence;
        private Entity m_observedLocalPlayerEntity;
        private bool m_observedLocalPlayerWasDead;
        private int m_localRespawnSequence;
        private double m_localRespawnPendingUntil;
        private int m_nextWorldTransferId;
        private int m_worldTransferCursor;
        private double m_nextWorldTransferManifestRequestTime;
        private double m_nextWorldTransferUiUpdateTime;
        private GamePakWorldReadyStage ClientJoinReadyStage
        {
            get => (GamePakWorldReadyStage)m_worldTransferRegistry.ClientJoinReadyStageValue;
            set => m_worldTransferRegistry.ClientJoinReadyStageValue = (int)value;
        }
        private double m_nextClientJoinReadyRetryTime;
        private double m_lastClientJoinBarrierProgressTime;
        private float m_terrainMergeTime;
        private int m_sessionRandomSeed;
        private Dictionary<string, long> m_pendingRandomStates = new Dictionary<string, long>();
        private Project m_randomStateAppliedProject;
        private GameWorldInfoMessage1 m_remoteWeatherState;
        private int m_lastRemoteWorldInfoTick = -1;
        private int m_hostWorldTimeRevision;
        private bool m_remoteTimeAccelerated;
        private bool m_hostSleepAccelerationSessionActive;
        private double m_hostSleepAccelerationStartTime;
        // Source: CircuitSynchronizer.NotifyRemoteTimeAccelerationChanged
        // Keep the host wake boundary until the post-sleep circuit rebase is ready.
        private bool m_clientSleepWakeBoundaryPending;
        private readonly HashSet<ComponentSleep> m_pendingClientSleepWakeups =
            new HashSet<ComponentSleep>();
        private readonly Dictionary<int, double> m_nextSleepHealthSendTimes =
            new Dictionary<int, double>();
        private readonly Dictionary<ComponentHealth, Action<ComponentCreature>>
            m_hostSleepWakeHandlers =
            new Dictionary<ComponentHealth, Action<ComponentCreature>>();
        private bool m_remoteFogPresentationInitialized;
        private bool m_remoteLightningActive;
        private bool m_hostLightningActive;
        private readonly SessionAssetRegistry m_sessionAssetRegistry =
            new SessionAssetRegistry();
        private string m_lastLocalProfileSignature;
        private const double HostLightningStaleDuration = 1.0;
        private readonly Dictionary<int, PendingWorldControlRequest>
            m_pendingWorldControlRequests = new Dictionary<int, PendingWorldControlRequest>();
        private readonly Queue<QueuedWorldControlRequest> m_queuedWorldControlRequests =
            new Queue<QueuedWorldControlRequest>();
        private readonly Dictionary<int, WorldControlResultMessage>
            m_bufferedWorldControlResults = new Dictionary<int, WorldControlResultMessage>();
        private readonly Dictionary<int, HostWorldControlRequestState>
            m_hostWorldControlRequestStates = new Dictionary<int, HostWorldControlRequestState>();
        private int m_nextWorldControlRequestId;
        private int m_nextWorldControlFeedbackRequestId = 1;
        private bool m_worldControlQueueNoticeShown;
        private const double WorldControlResultTimeout = 5.0;
        private const int MaximumCachedWorldControlResults = 64;
        private const int MaximumPendingWorldControlRequests = 64;
        private byte[] m_pendingLocalCreateDescription;
        private IPEndPoint m_pendingLocalCreateAddress;
        private int m_localCreateAttempts;
        private double m_nextLocalCreateAttemptTime;
        private const float ServerTickDuration = 0.01f;
        private const float TransportTickDuration = 0.05f;
        private const int LogicStepsPerTransportTick = 5;
        private const int SyncBaseRate = 32;
        private const float SyncPulseDuration = 1f / SyncBaseRate;
        private const int MaxSyncPulsesPerUpdate = 4;
        private const int MaxReconnectAttempts = 5;
        private const int MaximumLocalCreateAttempts = 5;
        private const double LocalCreateRetryInterval = 1.5;
        private const float ReconnectInitialDelay = 1f;
        private const float ReconnectMaxDelay = 5f;
        private const double ReconnectHandshakeTimeout = 12.0;
        private const double JoinWorldNoProgressTimeout = 30.0;
        private const double JoinBarrierRetryInterval = 1.5;
        private const double JoinBarrierNoProgressTimeout = 30.0;
        private const float RemoteConnectionLostPeriod = 15f;
        private const double ReliableStallGracePeriod = 6.0;
        private const float LocalHostConnectionLostPeriod = float.MaxValue;
        private const float LocalKnockbackPositionCorrectionDuration = 0.75f;
        private const float RemoteInputHoldDuration = 0.75f;
        private const float MaximumHostActionPoseCorrection = 2f;
        private const float RemoteDelaySampleLimit = 0.6f;
        private const float RemoteExtrapolationLimit = 0.35f;
        private const float RemotePresentationStaleTime = 2f;
        private const float RemoteAnimalPredictionLimit = 1.15f;
        private const float RemoteAnimalSnapDistance = 3f;
        private const float ClientProjectilePredictionGrace = 3f;
        private const float ClientProjectileDuplicateDistance = 1.25f;
        private const int MaximumProjectileReleaseCompensationSteps = 25;
        private const float MaximumProjectileReleaseVelocity = 64f;
        private const float PlayerHitRequestInterval = 0.36f;
        private const int PlayerJumpRequestMaxInputLagSteps = 50;
        private const double PlayerJumpRequestReceiptLifetime = 1.0;
        private const int MaximumPendingJumpRequests = 4;
        private const int MaximumPriorityInputActionsPerFrame = 16;
        // Source: Comms/Comms/UdpTransmitter.cs:UdpTransmitter.MaxPacketSize
        // 940 bytes plus the nested ScMultiplayer, DRT, Peer and Comm headers fits in one
        // 1024-byte UDP packet for both IPv4 and IPv6 connection-init packets.
        private const int WorldTransferChunkSize = 940;
        // Source: Mod/Comms/Comms/UdpTransmitter.cs:UdpTransmitter.MaxPacketSize
        // The 940-byte world chunk was selected with all nested headers included, so it occupies
        // one reliable UDP packet. Catch-up and circuit payloads still use their serialized length.
        private const int WorldTransferRelayPackets = 1;
        private const int MaximumWorldTransferChunksPerNetworkTick = 8;
        private const int MaximumWorldTransferChunksPerGameplayTick = 4;
        private const int MaximumWorldTransferUnackedPackets = 32;
        private const int MaximumDynamicWorldTransferChunksPerNetworkTick = 64;
        private const int MaximumDynamicWorldTransferUnackedPackets = 128;
        private const int ReliableCriticalReservePackets = 16;
        // Source: Comms/Comms/Comm.cs:Comm.GetUnackedPacketsCount
        // The transport count includes gameplay packets and world-transfer packets together.
        // Configured join transfers therefore need a separate in-flight allowance, otherwise
        // normal gameplay can consume the 32-packet legacy ceiling before the map is sent.
        private const int ConfiguredWorldTransferGameplayQueueAllowance = 32;
        private const double AutomaticJoinTransferStartKbps = 1200.0;
        private const double AutomaticJoinTransferMaximumKbps = 9600.0;
        private const double AutomaticJoinTransferGrowthFactor = 1.25;
        private const double AutomaticJoinTransferBackoffFactor = 0.5;
        private const double AutomaticJoinTransferAdjustmentInterval = 1.0;
        private const double AutomaticJoinTransferCooldown = 3.0;
        private const double AutomaticJoinTransferMaximumLossRate = 0.02;
        private const int AutomaticJoinTransferPressureUnackedPackets = 96;
        private const int AutomaticJoinTransferGameplayReserveKbps = 96;
        private const int MaximumWorldTransferRepairChunks = 24;
        private const int MaximumQueuedWorldTransferChunks = 128;
        private const int WorldTransferWindowChunks = 24;
        private const int ReverseDiscoveryPortProbeCount = 4;
        // Source: Comms/Comms/Comm.cs:Comm.ProcessConnections
        // Progress acknowledgements only run while a client is downloading a world. At 50ms, the
        // 24-chunk application window can use the configured join bandwidth without changing
        // loss repair or normal gameplay traffic.
        private const double WorldTransferProgressStatusInterval = 0.05;
        private const double WorldTransferRepairInterval = 0.75;
        private const double WorldTransferRepairRequestInterval = 1.5;
        private const int MaximumWorldTransferSize = 64 * 1024 * 1024;
        private const int MaximumJoinCatchUpBytes = 4 * 1024 * 1024;
        private const double TerrainRecoveryRetention = 15.0;
        private const int MaximumTerrainRecoveryRanges = 64;
        private const int MaximumTerrainRecoveryBatchBytes = 1024;
        private const double TerrainGapRecoveryDelay = 0.75;
        // Source: ScMultiplayer.cs:ScMultiplayer.ProcessEndOfFrameActions
        private const int MaximumEndOfFrameActionsPerFrame = 256;
        private const int TerrainChunkSyncBaseMessagesPerFrame = 8;
        private const int TerrainChunkSyncBurstMessagesPerFrame = 16;
        private const int TerrainChunkSyncQueueGrowthStep = 8;
        private const int TerrainChunkSyncMessageQueueHighWaterMark = 32;
        private const int TerrainChunkCheckpointQueueHighWaterMark = 16;
        private const long EndOfFrameActionBudgetMilliseconds = 4;
        private const int RunawayCreatureThreshold = 256;
        private const int RunawayCreatureKeepCount = 52;
        private const int RunawayCreatureCleanupBatchSize = 256;
        private const float RemoteCreatureSpawnInterval = 1f;
        private const int RemoteSpawnRecordsPerInterval = 2;
        private const int RemoteCreatureTargetCount = 26;
        private const float RemoteCreaturePopulationRadius = 68f;
        private const float WorldObjectFullSyncInterval = 5f;
        private const float PlayerRecordSaveInterval = 5f;
        private const float TerrainMergeInterval = 5f;
        private const int TerrainCatchUpBatchSize = 48;
        private const int TerrainReliableBatchSize = 48;
        private const int TerrainChunkSyncBatchSize = 32;
        private const int TerrainChunkSyncRequestsPerInterval = 4;
        private const double TerrainChunkSyncRequestInterval = 0.1;
        private const double TerrainChunkSyncRetryInterval = 5.0;
        private const double TerrainChunkVerificationDelay = 0.35;
        private const double HostTerrainPlaceFallbackLifetime = 0.5;
        private const double LocalCollapsingPredictionLifetime = 8.0;
        private const double WaterSettlementDelay = 0.35;
        private const double SlowFluidSettlementDelay = 2.1;
        private const int AnimalSyncBatchSize = 12;
        private const int MaximumSkinAssetBytes = 512 * 1024;
        private const int MaximumBlocksTextureAssetBytes = 4 * 1024 * 1024;
        private const int MaximumRecentChatMessages = 50;
        private const string ServerAuditEventName = "ScMultiplayer.ServerAudit";
        private const string ServerRetransmitAuditEventName = "ScMultiplayer.ServerRetransmitAudit";
        private const string DownloadedWorldsRegistryPath = "data:/ScMultiplayerDownloadedWorlds.txt";
        private const string PlayerRecordsFileName = "ScMultiplayerPlayers.xml";
        private const string PlayerProfileRequiredReason = "SCMP_PROFILE_REQUIRED";
        private const string ProtocolMismatchReasonPrefix = "SCMP_PROTOCOL_MISMATCH";
        // Source: Mod/CircuitAutoRouter/SubsystemCircuitRouter.cs:CircuitColors
        private static readonly Color[] ChatColors =
        {
            Color.White,
            Color.Cyan,
            Color.Red,
            Color.Blue,
            Color.Yellow,
            Color.Green,
            new Color(255, 165, 0),
            new Color(160, 32, 240)
        };

    }
}
