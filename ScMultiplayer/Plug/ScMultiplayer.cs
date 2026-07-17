using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPI;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using TemplatesDatabase;

namespace ScMultiplayer
{
    // ================================================================
    // PlayerMappingManager / PlayerOperationSyncManager / NetworkMessageHandler
    // NetworkMessageSender 保持原样，不变
    // ================================================================
    #region Helpers

    public class PlayerMappingManager
    {
        private Dictionary<int, int> clientIdToPlayerIndex = new Dictionary<int, int>();
        private Dictionary<int, int> playerIndexToClientId = new Dictionary<int, int>();
        public int MaxPlayerIndices { get; set; } = 4;

        public int AssignPlayerIndex(int clientId)
        {
            if (clientIdToPlayerIndex.ContainsKey(clientId))
                return clientIdToPlayerIndex[clientId];
            for (int i = 0; i < MaxPlayerIndices; i++)
            {
                if (playerIndexToClientId.ContainsKey(i)) continue;
                clientIdToPlayerIndex[clientId] = i;
                playerIndexToClientId[i] = clientId;
                return i;
            }
            return -1;
        }

        public void ReleasePlayerIndex(int clientId)
        {
            if (clientIdToPlayerIndex.TryGetValue(clientId, out int pi))
            {
                clientIdToPlayerIndex.Remove(clientId);
                playerIndexToClientId.Remove(pi);
            }
        }

        public int GetPlayerIndex(int clientId) =>
            clientIdToPlayerIndex.TryGetValue(clientId, out int pi) ? pi : -1;

        public int GetClientId(int playerIndex) =>
            playerIndexToClientId.TryGetValue(playerIndex, out int cid) ? cid : -1;

        public List<int> GetAllPlayerIndices() => playerIndexToClientId.Keys.ToList();

        public void Reset()
        {
            clientIdToPlayerIndex.Clear();
            playerIndexToClientId.Clear();
        }
    }

    public class PlayerOperationSyncManager
    {
        public int ConvertPlayerIndexForClient(int sourcePlayerIndex, int targetClientId)
        {
            int sourceClientId = ScMultiplayer.playerMappingManager.GetClientId(sourcePlayerIndex);
            if (sourceClientId == -1) return -1;
            int targetPlayerIndex = ScMultiplayer.playerMappingManager.GetPlayerIndex(targetClientId);
            if (targetPlayerIndex == -1) return -1;
            return (sourcePlayerIndex - targetPlayerIndex + ScMultiplayer.playerMappingManager.MaxPlayerIndices)
                % ScMultiplayer.playerMappingManager.MaxPlayerIndices;
        }

        public int ConvertLocalPlayerIndexToNetwork(int localPlayerIndex, int localClientId)
        {
            int localClientPlayerIndex = ScMultiplayer.playerMappingManager.GetPlayerIndex(localClientId);
            if (localClientPlayerIndex == -1) return -1;
            return (localPlayerIndex - localClientPlayerIndex + ScMultiplayer.playerMappingManager.MaxPlayerIndices)
                % ScMultiplayer.playerMappingManager.MaxPlayerIndices;
        }
    }

    // ================================================================
    // NetworkPlayerState: 远程玩家状态快照
    // ================================================================
    public class NetworkPlayerState
    {
        public int ClientID;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector2 LookAngles;
        public Vector2? WalkOrder;
        public float JumpOrder;
        public float PokingPhase;
        public bool AttackOrder;
        public bool RowLeftOrder;
        public bool RowRightOrder;
        public bool IsCrouching;
        public bool IsFlying;
        public bool IsRiding;
        public bool IsGrounded;
        public int ActiveSlotIndex;
        public int HandItemValue;
        public int HandItemCount;
        public Vector3 ItemOffset;
        public Vector3 ItemRotation;
        public float AimHandAngle;
        public float Health;
        public float MaxHealth = 1f;
        public bool IsDead;
        public int ServerTick;
        public float EstimatedDelay;
        public bool PresentationInitialized;
        public double LastUpdateTime;
    }

    public class NetworkPlayerRecord
    {
        public string Name;
        public PlayerClass PlayerClass;
        public string SkinName;
        public Vector3 Position;
        public float Level = 1f;
        public float Health = 1f;
        public bool HasReceivedInitialItems = true;
        public int[] SlotValues;
        public int[] SlotCounts;
        public int[][] Clothes;
    }

    public class PendingJoinRequest
    {
        public IPEndPoint ServerAddress;
        public int GameId;
        public GameWorldInfoMessage WorldInfo;
    }

    public class NetworkPlayerInputState
    {
        public PlayerInput Input;
        public PlayerInput HeldInput;
        public Quaternion BodyRotation;
        public Vector2 LookAngles;
        public Vector3 BodyPosition;
        public Vector3 BodyVelocity;
        public int ClientTick;
        public bool InitialPositionApplied;
        public int Sequence = -1;
        public int ConsumedSequence = -1;
        public double LastReceivedTime;
    }

    public class RemotePickableRecord
    {
        public int Value;
        public int Count;
    }

    public class TerrainCellState
    {
        public bool IsModified;
        public int CellValue;
        public int Tick;
    }

    public class AnimalSyncMetadata
    {
        public double NextSendTime;
        public double HighPriorityUntil;
        public string BehaviorState = string.Empty;
        public int TargetEntityId;
        public string HerdName = string.Empty;
        public float LastHealth = 1f;
        public byte SyncTier;
        public bool HasSent;
    }

    public class AnimalSyncCandidate
    {
        public Entity Entity;
        public ComponentCreature Creature;
        public ComponentBody Body;
        public string BehaviorState;
        public int TargetEntityId;
        public string HerdName;
        public byte SyncTier;
        public bool StateChanged;
    }

    public class RemoteAnimalSyncState
    {
        public byte SyncTier;
        public string BehaviorState;
        public int TargetEntityId;
        public string HerdName;
    }

    public class NetworkMessageHandler
    {
        public static void HandleChatMessage(ChatMessage message, int clientID)
        {
            Log.Information($"[Chat] Client{clientID} {message.Sender}: {message.Text}");
            ScMultiplayer.currentInstance.DisplayChatMessage(message, clientID);
        }

        public static void HandleWorldInfoMessage(GameWorldInfoMessage1 message, int clientID)
        {
            ScMultiplayer.currentInstance.HandleGameWorldInfoMessage(message);
        }

        public static void HandleModifiedCellsMessage(GameModifiedCellsMessage message, int clientID)
        {
            ScMultiplayer.currentInstance.HandleGameModifiedCellsMessage(message, clientID);
        }

        public static void HandlePakWorldMessage(GamePakWorldMessage message, int clientID)
        {
            ScMultiplayer.currentInstance.HandleGamePakWorldMessage(message);
        }

        public static void HandlePlayerHealthMessage(GamePlayerHealthMessage message, int clientID)
        {
            ScMultiplayer.currentInstance.HandleGamePlayerHealthMessage(message, clientID);
        }
    }

    public class NetworkMessageSender
    {
        public static void SendPlayerPositionMessage(int playerIndex, int serverTick,
            Vector3 position, Quaternion rotation,
            Vector3 velocity, Vector2 lookAngles, Vector2? walkOrder, float jumpOrder,
            float pokingPhase, bool attackOrder, bool rowLeftOrder, bool rowRightOrder,
            bool isCrouching, bool isFlying, bool isRiding, bool isGrounded,
            int activeSlotIndex, int handItemValue, int handItemCount,
            Vector3 itemOffset, Vector3 itemRotation, float aimHandAngle,
            int[] slotValues, int[] slotCounts)
        {
            var msg = new GamePlayerPositionMessage(playerIndex, serverTick, position, rotation, velocity,
                lookAngles, walkOrder, jumpOrder, pokingPhase,
                attackOrder, rowLeftOrder, rowRightOrder,
                isCrouching, isFlying, isRiding, isGrounded,
                activeSlotIndex, handItemValue, handItemCount,
                itemOffset, itemRotation, aimHandAngle, slotValues, slotCounts);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendPlayerInputMessage(int playerIndex, int sequence, int clientTick,
            Vector3 bodyPosition, Vector3 bodyVelocity, Quaternion bodyRotation,
            Vector2 lookAngles, PlayerInput playerInput)
        {
            var msg = new GamePlayerInputMessage(
                playerIndex, sequence, clientTick, bodyPosition, bodyVelocity,
                bodyRotation, lookAngles, playerInput);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendChatMessage(string sender, string senderIdentity, string text)
        {
            var msg = new ChatMessage(sender, senderIdentity, text);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendWorldInfoMessage(double timeOfDayOffset, double totalElapsedGameTime,
            TimeOfDayMode currentTimeMode, SubsystemWeather weather, SubsystemSky sky)
        {
            // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.m_lightningStrikePosition
            // Nullable<T> boxes a present value as T. Use SuAPI's non-generic getter so its
            // generic result check does not reject the boxed Vector3 value.
            object lightningValue = ScMultiplayer.ModManager.ModParentField.GetParentField(
                sky, "m_lightningStrikePosition", typeof(SubsystemSky));
            Vector3? lightningPosition = lightningValue is Vector3 position
                ? position
                : (Vector3?)null;
            var msg = new GameWorldInfoMessage1(timeOfDayOffset, totalElapsedGameTime, currentTimeMode,
                weather.IsPrecipitationStarted, weather.PrecipitationIntensity,
                weather.IsFogStarted, weather.FogProgress, weather.FogIntensity, weather.FogSeed,
                lightningPosition.HasValue, lightningPosition ?? Vector3.Zero);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendWorldControlRequest(WorldControlAction actions)
        {
            var msg = new WorldControlRequestMessage(actions);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendEntityMessage(EntityMessage message) =>
            ScMultiplayer.client.SendInput(Message.WriteWithSender(message, ScMultiplayer.client.Address));

        public static void SendBodyUpdateMessage(BodyUpdateMessage message) =>
            ScMultiplayer.client.SendInput(Message.WriteWithSender(message, ScMultiplayer.client.Address));

        public static void SendPickableMessage(PickableSyncMessage message) =>
            ScMultiplayer.client.SendInput(Message.WriteWithSender(message, ScMultiplayer.client.Address));

        public static void SendPakWorldMessage(string name, byte[] worldData, DateTime lastSaveTime,
            int targetClientId, int randomSeed, Dictionary<string, long> randomStates,
            NetworkPlayerRecord playerRecord)
        {
            var msg = new GamePakWorldMessage(
                name, worldData, lastSaveTime, targetClientId, randomSeed, randomStates, playerRecord);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendPlayerProfileMessage(int clientId, NetworkPlayerRecord record)
        {
            var msg = new PlayerProfileMessage(clientId, record);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendPlayerHealthMessage(int playerIndex, float health, float maxHealth,
            float healthChange, bool isDead, string cause = null)
        {
            var msg = new GamePlayerHealthMessage(playerIndex, health, maxHealth, healthChange, isDead, cause);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendKickPlayerMessage(int targetClientID, string reason = null)
        {
            var msg = new GameKickPlayerMessage(targetClientID, reason);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }
    }

    #endregion

    // ================================================================
    // ScMultiplayer 主类 (IMod + IUpdateable)
    // ================================================================
    public class ScMultiplayer : IMod, IUpdateable
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
        public string Version => "1.0.2";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;
        public UpdateOrder UpdateOrder => UpdateOrder.Body;

        // ---------- 内部状态 ----------
        private float m_networkTickAccumulator;
        private double m_lastNetworkUpdateTime;
        private float m_worldInfoSyncTime;
        private float m_inventorySyncTime;
        private Dictionary<int, float> m_playerHealthCache = new Dictionary<int, float>(); // clientID → last known health
        private readonly Dictionary<int, PlayerData> m_networkPlayerData = new Dictionary<int, PlayerData>();
        private readonly HashSet<int> m_creatingNetworkPlayers = new HashSet<int>();
        private readonly object m_updateRegistrationLock = new object();
        private readonly Dictionary<int, string> m_pendingNetworkPlayers = new Dictionary<int, string>();
        private readonly Dictionary<int, string> m_pendingNetworkPlayerIdentities = new Dictionary<int, string>();
        private readonly Dictionary<int, NetworkPlayerInputState> m_networkPlayerInputs =
            new Dictionary<int, NetworkPlayerInputState>();
        private PlayerInput m_localPlayerInput;
        private Vector3 m_localInputBodyPosition;
        private Vector3 m_localInputBodyVelocity;
        private Quaternion m_localInputBodyRotation = Quaternion.Identity;
        private Vector2 m_localInputLookAngles;
        private int m_localInputSequence;
        private int m_lastSentInputSequence = -1;
        private int m_localInputResendsRemaining;
        private float m_smoothedNetworkDelay;
        private readonly Dictionary<string, NetworkPlayerRecord> m_playerRecords = new Dictionary<string, NetworkPlayerRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string> m_clientRecordKeys = new Dictionary<int, string>();
        private string m_playerRecordsWorldDirectory;
        private bool m_playerRecordsDirty;
        private float m_playerRecordSaveTime;
        private float m_playerProfileSyncTime;
        private PendingJoinRequest m_pendingJoinRequest;
        private NetworkPlayerRecord m_pendingLocalPlayerRecord;
        private PlayerData m_localReplacementPlayerData;
        private bool m_localPlayerRecordQueued;
        private bool m_localPlayerRecordApplied;
        private bool m_replacingLocalPlayerData;
        private const float HealthSyncInterval = 1.0f; // 每秒同步一次生命
        private Project m_registeredProject;
        private string m_downloadedWorldDirectory;
        private bool m_hostDisconnectHandled;
        private bool m_localLeaveInProgress;
        private bool m_shouldCreateHostAvatar;
        private bool m_isLoadingDownloadedWorld;
        private ushort m_nextAnimalId = 1;
        private ushort m_nextPickableId = 1;
        private float m_worldObjectsSyncTime;
        private float m_fullWorldObjectsSyncTime;
        private Project m_clientWorldObjectsProject;
        private readonly ConcurrentQueue<Action> m_endOfFrameActions = new ConcurrentQueue<Action>();
        private readonly Dictionary<Entity, ushort> m_hostAnimalIds = new Dictionary<Entity, ushort>();
        private readonly List<Entity> m_hostAnimals = new List<Entity>();
        private readonly Dictionary<Entity, AnimalSyncMetadata> m_hostAnimalSync =
            new Dictionary<Entity, AnimalSyncMetadata>();
        private readonly Dictionary<ushort, Entity> m_remoteAnimals = new Dictionary<ushort, Entity>();
        private readonly Dictionary<ushort, string> m_remoteAnimalTemplates = new Dictionary<ushort, string>();
        private readonly Dictionary<ushort, RemoteAnimalSyncState> m_remoteAnimalSync =
            new Dictionary<ushort, RemoteAnimalSyncState>();
        private readonly Dictionary<Pickable, ushort> m_hostPickableIds = new Dictionary<Pickable, ushort>();
        private readonly Dictionary<ushort, Pickable> m_remotePickables = new Dictionary<ushort, Pickable>();
        private readonly Dictionary<ushort, RemotePickableRecord> m_remotePickableRecords = new Dictionary<ushort, RemotePickableRecord>();
        private readonly object m_terrainJournalLock = new object();
        private readonly Dictionary<Point3, TerrainCellState> m_terrainCheckpoint =
            new Dictionary<Point3, TerrainCellState>();
        private readonly Dictionary<Point3, TerrainCellState> m_pendingTerrainChanges =
            new Dictionary<Point3, TerrainCellState>();
        private readonly Dictionary<Point3, int> m_terrainRepairRepeats =
            new Dictionary<Point3, int>();
        private float m_terrainMergeTime;
        private float m_terrainRepairTime;
        private int m_sessionRandomSeed;
        private Dictionary<string, long> m_pendingRandomStates = new Dictionary<string, long>();
        private Project m_randomStateAppliedProject;
        private GameWorldInfoMessage1 m_remoteWeatherState;
        private bool m_remoteLightningActive;
        private WorldControlAction m_pendingWorldControlActions;
        private double m_worldControlRequestDeadline;
        private const float ServerTickDuration = 0.01f;
        private const float NetworkTickRate = 100f;
        private const float NetworkTickDuration = 1f / NetworkTickRate;
        private const int MaxNetworkTicksPerUpdate = 8;
        private const float WorldInfoSyncInterval = 1f / 20f;
        private const float InventorySyncInterval = 0.25f;
        private const float PlayerProfileSyncInterval = 1f;
        private const float PlayerRecordSaveInterval = 5f;
        private const float TerrainMergeInterval = 5f;
        private const float TerrainRepairInterval = 1f;
        private const int TerrainRepairRepeatCount = 3;
        private const int TerrainCatchUpBatchSize = 128;
        private const int AnimalSyncBatchSize = 12;
        private const string DownloadedWorldsRegistryPath = "data:/ScMultiplayerDownloadedWorlds.txt";
        private const string PlayerRecordsFileName = "ScMultiplayerPlayers.xml";
        private const string PlayerProfileRequiredReason = "SCMP_PROFILE_REQUIRED";
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

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            currentInstance = this;
            ModManager = Game.Program.ModManager;

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
            downloadSM.OnFailedEnter += (reason) => Log.Error($"[DL] Failed: {reason}");

            // EventBus
            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
                HandleGameDatabase((Database)args[0]), EventPriority.HIGHEST);
            eventBus.SubscribeEvent("Loading.Initialize", args =>
                HandleLoading(args), EventPriority.HIGHEST);
            eventBus.SubscribeEvent("Frame.Update", args =>
            {
                ProcessEndOfFrameActions();
                CleanupDownloadedWorldsIfIdle();
                return args;
            }, EventPriority.LOWEST);

            CleanupDownloadedWorldsIfIdle();
            GameManager.ProjectDisposed += HandleProjectDisposed;

            // 初始化网络
            // Source: Mod/Comms/Comms.Drt/Func/Server/Server.cs:Server.Server
            // 0.01s is the minimum tick duration accepted by Comms.
            float tickDuration = ServerTickDuration;
            int stepsPerTick = 1;
            int port = "SuSCMP".ToDynamicPort();
            Log.Information($"[ScMP] Starting on port {port}");

            // 探测物理 LAN IP（避免虚拟网卡如 ZeroTier/WSL/CFW 导致广播源不可达）
            var lanAddress = DetectLanAddress();
            Log.Information($"[ScMP] Detected LAN address: {lanAddress}");

            // UdpTransmitter(now) 只接受 localPort 参数，自动检测 LAN 地址
            var serverTransmitter = new UdpTransmitter(port);
            var explorerTransmitter = new UdpTransmitter(0);
            var clientTransmitter = new UdpTransmitter(0);

            try
            {
                server = new Server(0x53634d70, tickDuration, stepsPerTick, serverTransmitter);
                ConfigurePeerTimeout(server.Peer);
                server.Information += Server_Information;
                server.Start();
                Log.Information($"[ScMP] Server started OK, address={server.Address}");
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Server start FAILED: {ex.Message}");
            }

            explorer = new Explorer(0x53634d70, port, explorerTransmitter);
            explorer.Error += ex => Log.Error($"[Explorer] {ex.Message}");

            client = new Client(0x53634d70, clientTransmitter);
            ConfigurePeerTimeout(client.Peer);
            client.GameCreated += Client_GameCreated;
            client.GameJoined += Client_GameJoined;
            client.Error += Client_Error;
            client.GameDescriptionRequest += Client_GameDescriptionRequest;
            client.ConnectRefused += Client_ConnectRefused;
            client.GameStateRequest += Client_GameStateRequest;
            client.GameStep += Client_GameStep;
            client.Start();

            explorer.StartDiscovery();
            connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Discovering);
            Log.Information($"[ScMP] Explorer discovery started (address={explorerTransmitter.Address})");

            StartAsyncRegistration();
        }

        // Source: Mod/Comms/Comms/Peer.cs:Peer.ProcessPeers
        // Keep host/client failure detection responsive without coupling it to the game tick rate.
        private static void ConfigurePeerTimeout(Peer peer)
        {
            if (peer == null) return;
            peer.Settings.KeepAlivePeriod = 1f;
            peer.Settings.KeepAliveResendPeriod = 0.5f;
            peer.Settings.ConnectionLostPeriod = 5f;
        }

        private object[] HandleLoading(object[] args)
        {
            // Source: Survivalcraft/Game/Program.cs:Program.Initialize
            // Source: Survivalcraft/Game/LoadingManager.cs:LoadingManager.ReplaceItem
            if (!Game.LoadingManager.ReplaceItem("Initialize PlayScreen", delegate
            {
                ScreensManager.AddScreen("Play", new SuPlayScreen());
                // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.PlayerScreen
                ScreensManager.AddScreen("ScMultiplayerPlayer", new SuNetworkPlayerScreen());
            }))
            {
                throw new InvalidOperationException("Loading item 'Initialize PlayScreen' was not found.");
            }
            return args;
        }

        public object[] HandleGameDatabase(Database database)
        {
            var componentInput = database.FindDatabaseObject(
                new Guid("ec809766-ba61-434e-bfde-e677f506b887"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentInput.Value = "ScMultiplayer.SuComponentInput";

            var subsystemTerrain = database.FindDatabaseObject(
                new Guid("e2636c38-f179-4aa1-b087-ed6920d66e8e"),
                database.FindDatabaseObjectType("Parameter", true), true);
            subsystemTerrain.Value = "ScMultiplayer.SuSubsystemTerrain";

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
        // Update
        // ====================================================================
        public void Update(float dt)
        {
            EnsureNetworkComponentPlayers();
            EnsureLocalPlayerRecordApplied();
            connectionSM.Update();
            downloadSM.Update();
            if (IsHost) ApplyHostRemoteFollowVelocities();
            else UpdateRemotePlayerPresentations();

            // Source: Engine/Time.cs:Time.RealTime
            // Real time avoids duplicate network time when SubsystemUpdate runs multiple game
            // updates in one rendered frame. The accumulator preserves fractional 120Hz ticks.
            double now = Time.RealTime;
            if (m_lastNetworkUpdateTime <= 0.0)
                m_lastNetworkUpdateTime = now;
            float elapsed = (float)MathUtils.Clamp(
                now - m_lastNetworkUpdateTime, 0.0, NetworkTickDuration * MaxNetworkTicksPerUpdate);
            m_lastNetworkUpdateTime = now;
            m_networkTickAccumulator += elapsed;

            int ticks = 0;
            while (m_networkTickAccumulator >= NetworkTickDuration && ticks < MaxNetworkTicksPerUpdate)
            {
                m_networkTickAccumulator -= NetworkTickDuration;
                TriggerNetworkTick(NetworkTickDuration);
                ticks++;
            }
            if (ticks == MaxNetworkTicksPerUpdate && m_networkTickAccumulator >= NetworkTickDuration)
                m_networkTickAccumulator = 0f;

            // 渲染远程玩家
            RenderRemotePlayers();
        }

        // Source: ScMultiplayer.Update keyboard J flow
        // Source: ConsoleMod.ConsoleSubsystemGameWidgets.Update touch-button command pattern
        public void ShowCreateRoomDialog()
        {
            var sd = explorer?.DiscoveredServers?.FirstOrDefault();
            var gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            if (sd == null || gameInfo == null)
            {
                DialogsManager.ShowDialog(null, new MessageDialog("Network", "No local server or world is available.", "OK", null, null));
                return;
            }

            DialogsManager.ShowDialog(null,
                new MessageDialog("Create Room", gameInfo.WorldSettings.Name, "Create", "Cancel",
                    delegate (MessageDialogButton button)
                    {
                        if (button != MessageDialogButton.Button1) return;
                        try
                        {
                            CreateRoomFromCurrentWorld(sd, gameInfo);
                        }
                        catch (Exception ex)
                        {
                            DialogsManager.ShowDialog(null, new MessageDialog(
                                "Create Room", ex.Message, "OK", null, null));
                        }
                    }));
        }

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplaySmallMessage
        // Source: Mod/WeatherTips/Subsystem/SuSubsystemWeather.cs:SuSubsystemWeather.Update
        public void ShowTalkDialog()
        {
            if (client == null || !client.IsConnected)
            {
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Talk", "Join or create a room before sending messages.", "OK", null, null));
                return;
            }

            DialogsManager.ShowDialog(ScreensManager.RootWidget,
                new TextBoxDialog("Talk", "", 125, delegate (string text)
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        NetworkMessageSender.SendChatMessage(
                            GetLocalPlayerName(), GetLocalPlayerIdentity(), text.Trim());
                    }
                }));
        }

        public void DisplayChatMessage(ChatMessage message, int clientId)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Text)) return;
            string identity = string.IsNullOrWhiteSpace(message.SenderIdentity)
                ? clientId.ToString()
                : message.SenderIdentity;
            int hash = 17;
            foreach (char c in identity)
                hash = unchecked(hash * 31 + c);
            Color color = ChatColors[(hash & int.MaxValue) % ChatColors.Length];
            string sender = string.IsNullOrWhiteSpace(message.Sender) ? "Player" : message.Sender;

            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return;
            foreach (ComponentPlayer componentPlayer in players.ComponentPlayers)
            {
                if (m_networkPlayerData.Values.Contains(componentPlayer.PlayerData)) continue;
                componentPlayer.ComponentGui.DisplaySmallMessage(
                    sender + ": " + message.Text, color, blinking: true, playNotificationSound: true);
            }
        }

        // Source: Survivalcraft/Game/GameManager.cs:GameManager.SaveProject
        // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.ExportWorld
        private void CreateRoomFromCurrentWorld(ServerDescription serverDescription, SubsystemGameInfo gameInfo)
        {
            PrepareClientForGameCreation();
            string directoryName = gameInfo.DirectoryName;
            WorldInfo worldInfo = WorldsManager.WorldInfos.FirstOrDefault(world => world.DirectoryName == directoryName);
            bool snapshotMatches = worldInfo != null && SuPlayScreen.WorldData != null &&
                SuPlayScreen.WorldDataName == worldInfo.WorldSettings.Name &&
                SuPlayScreen.WorldDataLastSaveTime == worldInfo.LastSaveTime;

            if (!snapshotMatches)
            {
                // Region files are opened exclusively while a project is running. Save and unload once,
                // then export and immediately reload the same world.
                GameManager.SaveProject(waitForCompletion: true, showErrorDialog: true);
                GameManager.DisposeProject();
                WorldsManager.UpdateWorldsList();
                worldInfo = WorldsManager.WorldInfos.FirstOrDefault(world => world.DirectoryName == directoryName);
                if (worldInfo == null)
                    throw new InvalidOperationException("Saved world was not found after unloading the project.");

                using (var stream = new MemoryStream())
                {
                    WorldsManager.ExportWorld(worldInfo.DirectoryName, stream);
                    SuPlayScreen.WorldData = stream.ToArray();
                }
                SuPlayScreen.WorldDataName = worldInfo.WorldSettings.Name;
                SuPlayScreen.WorldDataLastSaveTime = worldInfo.LastSaveTime;
            }

            var worldMessage = new GameWorldInfoMessage(
                worldInfo.WorldSettings.Name, worldInfo.Size, worldInfo.LastSaveTime,
                worldInfo.WorldSettings.GameMode, worldInfo.WorldSettings.EnvironmentBehaviorMode,
                worldInfo.SerializationVersion, client.Address);
            IsHost = true;
            LastGameDescription = Message.WriteWithSender(worldMessage, client.Address);
            client.CreateGame(serverDescription.Address, LastGameDescription, client.ClientID.ToString());

            if (GameManager.Project == null)
                ScreensManager.SwitchScreen("GameLoading", worldInfo, null);
        }

        // Source: Comms/Drt/Client.cs:Client.CreateGame
        // A peer can own only one game membership. Close an existing hosted/joined session before
        // every create entry point so repeated CR clicks cannot reuse a joined Peer.
        public void PrepareClientForGameCreation()
        {
            if (client == null || !client.IsConnected) return;
            foreach (int clientId in m_networkPlayerData.Keys.ToArray())
                RemoveNetworkPlayer(clientId);
            client.LeaveGame();
            LastGameDescription = null;
            ResetTransientNetworkState();
        }

        // Source: ScMultiplayer.Update keyboard K flow
        // Source: Comms.Drt.Explorer.DiscoveredServers
        public void ShowJoinRoomDialog()
        {
            var games = explorer?.DiscoveredServers?
                .SelectMany(serverDescription => serverDescription.GameDescriptions)
                .ToList() ?? new List<GameDescription>();
            if (games.Count == 0)
            {
                DialogsManager.ShowDialog(null, new MessageDialog("Network", "No rooms were found.", "OK", null, null));
                return;
            }

            DialogsManager.ShowDialog(null,
                new ListSelectionDialog("Join Room", games, 60f,
                    item =>
                    {
                        var game = (GameDescription)item;
                        var info = Message.Read(game.GameDescriptionBytes) as GameWorldInfoMessage;
                        return info != null ? info.Name : game.ToString();
                    },
                    item =>
                    {
                        var game = (GameDescription)item;
                        var info = Message.Read(game.GameDescriptionBytes) as GameWorldInfoMessage;
                        if (info == null) return;
                        BeginJoinGame(game.ServerDescription.Address, game.GameID, info);
                    }));
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.JoinGame
        public void BeginJoinGame(IPEndPoint serverAddress, int gameId, GameWorldInfoMessage worldInfo)
        {
            if (serverAddress == null || worldInfo == null) return;
            m_pendingJoinRequest = new PendingJoinRequest
            {
                ServerAddress = serverAddress,
                GameId = gameId,
                WorldInfo = worldInfo
            };
            SubmitPendingJoin(null, PlayerClass.Male, null, hasPlayerProfile: false);
        }

        public void CancelPendingJoin()
        {
            m_pendingJoinRequest = null;
        }

        private void SubmitPendingJoin(string playerName, PlayerClass playerClass,
            string skinName, bool hasPlayerProfile)
        {
            PendingJoinRequest pending = m_pendingJoinRequest;
            if (pending?.WorldInfo == null) return;
            IsHost = false;
            if (client.IsConnected) client.LeaveGame();
            GameWorldInfoMessage info = pending.WorldInfo;
            var joinInfo = new GameWorldInfoMessage(
                info.Name, info.Size, info.LastSaveTime, info.GameMode,
                info.EnvironmentBehaviorMode, info.SerializationVersion, client.Address,
                hasPlayerProfile ? playerName : GetLocalPlayerName(), GetLocalPlayerIdentity(),
                hasPlayerProfile, playerClass, skinName);
            client.JoinGame(pending.ServerAddress, pending.GameId,
                Message.WriteWithSender(joinInfo, client.Address), client.Address.Port.ToString());
        }

        private void TryKickPlayer()
        {
            // 踢出最后一个加入的非房主玩家
            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null) return;
            var allPlayers = subsystemPlayers.ComponentPlayers;

            int hostPlayerIndex = playerMappingManager.GetPlayerIndex(0);
            ComponentPlayer target = null;
            foreach (var p in allPlayers)
            {
                if (p.PlayerData.PlayerIndex != hostPlayerIndex)
                {
                    target = p;
                    break;
                }
            }

            if (target == null) { Log.Information("[ScMP] No players to kick"); return; }

            int targetClientID = playerMappingManager.GetClientId(target.PlayerData.PlayerIndex);
            if (targetClientID <= 0) { Log.Information("[ScMP] Cannot kick player with invalid client ID"); return; }

            Log.Information($"[ScMP] Kicking player ClientID={targetClientID}");
            NetworkMessageSender.SendKickPlayerMessage(targetClientID, "Kicked by host");
        }

        // ====================================================================
        // 30fps 定时事件
        // ====================================================================
        private void TriggerNetworkTick(float tickDuration)
        {
            // Source: ScMultiplayer.ScMultiplayer.Update
            // Player transforms match the 100Hz server tick; slower state uses independent intervals.
            if (!client.IsConnected) return;

            m_inventorySyncTime += tickDuration;
            bool includeInventory = m_inventorySyncTime >= InventorySyncInterval;
            if (includeInventory) m_inventorySyncTime -= InventorySyncInterval;
            SendGamePlayerPositionMessage(includeInventory);

            m_worldInfoSyncTime += tickDuration;
            if (m_worldInfoSyncTime >= WorldInfoSyncInterval)
            {
                m_worldInfoSyncTime -= WorldInfoSyncInterval;
                SendGameWorldInfoMessage();
            }

            m_playerProfileSyncTime += tickDuration;
            if (m_playerProfileSyncTime >= PlayerProfileSyncInterval)
            {
                m_playerProfileSyncTime -= PlayerProfileSyncInterval;
                SynchronizePlayerProfiles();
            }

            SendGamePlayerHealthMessage();
            if (IsHost)
            {
                m_playerRecordSaveTime += tickDuration;
                if (m_playerRecordSaveTime >= PlayerRecordSaveInterval)
                {
                    m_playerRecordSaveTime -= PlayerRecordSaveInterval;
                    RefreshHostPlayerRecords();
                    SavePlayerRecords();
                }
                m_terrainMergeTime += tickDuration;
                if (m_terrainMergeTime >= TerrainMergeInterval)
                {
                    m_terrainMergeTime -= TerrainMergeInterval;
                    MergePendingTerrainChanges();
                }
                m_terrainRepairTime += tickDuration;
                if (m_terrainRepairTime >= TerrainRepairInterval)
                {
                    m_terrainRepairTime -= TerrainRepairInterval;
                    BroadcastTerrainRepairs();
                }
            }
            m_worldObjectsSyncTime += tickDuration;
            m_fullWorldObjectsSyncTime += tickDuration;
            if (m_worldObjectsSyncTime >= 0.1f)
            {
                bool fullSync = m_fullWorldObjectsSyncTime >= 1f;
                m_worldObjectsSyncTime -= 0.1f;
                if (fullSync) m_fullWorldObjectsSyncTime -= 1f;
                if (IsHost) SendWorldObjects(fullSync);
                else QueueEndOfFrameAction(MaintainClientWorldObjects);
            }
            if (IsHost) SendAdaptiveAnimalUpdates();
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Run
        // Frame.Update runs after ScreensManager.Update, outside SubsystemUpdate.Update enumeration.
        private void QueueEndOfFrameAction(Action action)
        {
            if (action != null) m_endOfFrameActions.Enqueue(action);
        }

        private void ProcessEndOfFrameActions()
        {
            while (m_endOfFrameActions.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ScMP] End-of-frame network action failed: {ex.Message}");
                }
            }
        }

        // ====================================================================
        // 发送: 玩家位置
        // ====================================================================
        private void SendGamePlayerPositionMessage(bool includeInventory)
        {
            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null) return;
            if (!IsHost)
            {
                SendGamePlayerInputMessage();
                return;
            }
            var players = subsystemPlayers.ComponentPlayers;

            // Source: SubsystemPlayers.ComponentPlayers
            // Network IDs and persisted PlayerData indices are different domains. Send the one
            // locally controlled player, identified by exclusion from the remote avatar table.
            ComponentPlayer item = players.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (item != null)
            {
                // 发送方直接使用 ClientID 作为网络标识，避免 PlayerIndex 映射冲突
                int senderClientId = client.ClientID;

                bool isCrouching = item.ComponentBody.TargetCrouchFactor > 0f;
                bool isFlying = item.ComponentLocomotion.IsCreativeFlyEnabled;
                bool isRiding = item.ComponentRider?.Mount != null;

                IInventory inventory = item.ComponentMiner?.Inventory;
                int activeSlot = inventory?.ActiveSlotIndex ?? -1;
                int handVal = inventory != null && activeSlot >= 0 ? inventory.GetSlotValue(activeSlot) : 0;
                int handCnt = inventory != null && activeSlot >= 0 ? inventory.GetSlotCount(activeSlot) : 0;
                int[] slotValues = inventory != null && includeInventory
                    ? new int[inventory.SlotsCount]
                    : Array.Empty<int>();
                int[] slotCounts = inventory != null && includeInventory
                    ? new int[inventory.SlotsCount]
                    : Array.Empty<int>();
                if (inventory != null && includeInventory)
                {
                    for (int i = 0; i < inventory.SlotsCount; i++)
                    {
                        slotValues[i] = inventory.GetSlotValue(i);
                        slotCounts[i] = inventory.GetSlotCount(i);
                    }
                }

                Vector3 itemOffset = item.ComponentCreatureModel.InHandItemOffsetOrder;
                Vector3 itemRotation = item.ComponentCreatureModel.InHandItemRotationOrder;
                float aimHandAngle = item.ComponentCreatureModel.AimHandAngleOrder;
                Vector2 lookAngles = item.ComponentLocomotion.LookAngles;
                Vector2? walkOrder = item.ComponentLocomotion.LastWalkOrder;
                float jumpOrder = item.ComponentLocomotion.LastJumpOrder;
                float pokingPhase = item.ComponentMiner?.PokingPhase ?? 0f;
                bool attackOrder = item.ComponentCreatureModel.AttackOrder;
                bool rowLeftOrder = item.ComponentCreatureModel.RowLeftOrder;
                bool rowRightOrder = item.ComponentCreatureModel.RowRightOrder;

                NetworkMessageSender.SendPlayerPositionMessage(
                    senderClientId, client.Step, item.ComponentBody.Position,
                    item.ComponentBody.Rotation, item.ComponentBody.Velocity, lookAngles,
                    walkOrder, jumpOrder, pokingPhase, attackOrder, rowLeftOrder, rowRightOrder,
                    isCrouching, isFlying, isRiding,
                    item.ComponentBody.StandingOnValue.HasValue,
                    activeSlot, handVal, handCnt,
                    itemOffset, itemRotation, aimHandAngle, slotValues, slotCounts);
            }
            foreach (KeyValuePair<int, PlayerData> remote in m_networkPlayerData.ToArray())
            {
                if (remote.Key > 0 && remote.Value?.ComponentPlayer != null)
                    SendAuthoritativePlayerState(remote.Key, remote.Value.ComponentPlayer, includeInventory);
            }
        }

        private void SendAuthoritativePlayerState(int networkClientId, ComponentPlayer item,
            bool includeInventory)
        {
            IInventory inventory = item.ComponentMiner?.Inventory;
            int activeSlot = inventory?.ActiveSlotIndex ?? -1;
            int handValue = inventory != null && activeSlot >= 0 ? inventory.GetSlotValue(activeSlot) : 0;
            int handCount = inventory != null && activeSlot >= 0 ? inventory.GetSlotCount(activeSlot) : 0;
            int[] slotValues = inventory != null && includeInventory
                ? new int[inventory.SlotsCount]
                : Array.Empty<int>();
            int[] slotCounts = inventory != null && includeInventory
                ? new int[inventory.SlotsCount]
                : Array.Empty<int>();
            if (inventory != null && includeInventory)
            {
                for (int i = 0; i < inventory.SlotsCount; i++)
                {
                    slotValues[i] = inventory.GetSlotValue(i);
                    slotCounts[i] = inventory.GetSlotCount(i);
                }
            }
            ComponentLocomotion locomotion = item.ComponentLocomotion;
            ComponentCreatureModel model = item.ComponentCreatureModel;
            NetworkMessageSender.SendPlayerPositionMessage(
                networkClientId, client.Step, item.ComponentBody.Position, item.ComponentBody.Rotation,
                item.ComponentBody.Velocity, locomotion.LookAngles,
                locomotion.LastWalkOrder, locomotion.LastJumpOrder,
                item.ComponentMiner?.PokingPhase ?? 0f, model.AttackOrder,
                model.RowLeftOrder, model.RowRightOrder,
                item.ComponentBody.TargetCrouchFactor > 0f,
                locomotion.IsCreativeFlyEnabled, item.ComponentRider?.Mount != null,
                item.ComponentBody.StandingOnValue.HasValue,
                activeSlot, handValue, handCount,
                model.InHandItemOffsetOrder, model.InHandItemRotationOrder,
                model.AimHandAngleOrder, slotValues, slotCounts);
        }

        private void SendGamePlayerInputMessage()
        {
            if (m_localInputResendsRemaining <= 0 || m_localInputSequence <= 0) return;
            NetworkMessageSender.SendPlayerInputMessage(
                client.ClientID, m_localInputSequence, client.Step,
                m_localInputBodyPosition, m_localInputBodyVelocity, m_localInputBodyRotation,
                m_localInputLookAngles, m_localPlayerInput);
            m_lastSentInputSequence = m_localInputSequence;
            m_localInputResendsRemaining--;
        }

        // ====================================================================
        // 发送: 世界信息 (仅Host)
        // ====================================================================
        private void SendGameWorldInfoMessage()
        {
            if (client.ClientID != 0) return;
            var gameInfo = GameManager.Project.FindSubsystem<SubsystemGameInfo>(true);
            var timeOfDay = GameManager.Project.FindSubsystem<SubsystemTimeOfDay>(true);
            var weather = GameManager.Project.FindSubsystem<SubsystemWeather>(true);
            var sky = GameManager.Project.FindSubsystem<SubsystemSky>(true);
            NetworkMessageSender.SendWorldInfoMessage(
                timeOfDay.TimeOfDayOffset,
                gameInfo.TotalElapsedGameTime,
                gameInfo.WorldSettings.TimeOfDayMode,
                weather,
                sky);
        }

        // Source: Survivalcraft/Game/SubsystemBodies.cs:SubsystemBodies.Bodies
        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
        private void SendWorldObjects(bool fullSync)
        {
            Project project = GameManager.Project;
            if (project == null) return;

            SubsystemBodies subsystemBodies = project.FindSubsystem<SubsystemBodies>(true);
            Entity[] animals = subsystemBodies.Bodies
                .Select(body => body?.Entity)
                .Where(entity => entity?.FindComponent<ComponentCreature>() != null &&
                    entity.FindComponent<ComponentPlayer>() == null)
                .Distinct()
                .ToArray();
            var currentAnimals = new HashSet<Entity>(animals);
            foreach (Entity removed in m_hostAnimalIds.Keys.Where(entity =>
                entity == null || !currentAnimals.Contains(entity) || !entity.IsAddedToProject).ToArray())
            {
                ushort id = m_hostAnimalIds[removed];
                NetworkMessageSender.SendEntityMessage(new EntityMessage(id, EntityMessage.EntityAction.Remove));
                m_hostAnimalIds.Remove(removed);
                m_hostAnimalSync.Remove(removed);
            }
            m_hostAnimals.Clear();
            foreach (Entity entity in animals)
            {
                if (!m_hostAnimalIds.TryGetValue(entity, out ushort id))
                {
                    id = m_nextAnimalId++;
                    m_hostAnimalIds.Add(entity, id);
                }
                if (!m_hostAnimalSync.ContainsKey(entity))
                    m_hostAnimalSync.Add(entity, new AnimalSyncMetadata());
                m_hostAnimals.Add(entity);
            }

            SubsystemPickables subsystemPickables = project.FindSubsystem<SubsystemPickables>(false);
            if (subsystemPickables == null) return;
            Pickable[] pickables = subsystemPickables.Pickables.Where(pickable => pickable != null && !pickable.ToRemove).ToArray();
            var currentPickables = new HashSet<Pickable>(pickables);
            foreach (Pickable removed in m_hostPickableIds.Keys.Where(pickable =>
                pickable == null || !currentPickables.Contains(pickable) || pickable.ToRemove).ToArray())
            {
                ushort id = m_hostPickableIds[removed];
                NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                    PickableSyncMessage.PickAction.Delete, id, 0, 0, Vector3.Zero, Vector3.Zero));
                m_hostPickableIds.Remove(removed);
            }

            var pickableUpdate = new PickableSyncMessage { Action = PickableSyncMessage.PickAction.UpdatePosition };
            foreach (Pickable pickable in pickables)
            {
                if (!m_hostPickableIds.TryGetValue(pickable, out ushort id))
                {
                    id = m_nextPickableId++;
                    m_hostPickableIds.Add(pickable, id);
                    fullSync = true;
                }
                if (fullSync)
                {
                    NetworkMessageSender.SendPickableMessage(new PickableSyncMessage(
                        PickableSyncMessage.PickAction.Create, id, pickable.Value, pickable.Count,
                        pickable.Position, pickable.Velocity, pickable.FlyToPosition));
                }
                pickableUpdate.Positions.Add(new PickableSyncMessage.PickablePos
                {
                    Id = id,
                    Position = pickable.Position,
                    Velocity = pickable.Velocity,
                    FlyToPosition = pickable.FlyToPosition
                });
            }
            if (pickableUpdate.Positions.Count > 0)
                NetworkMessageSender.SendPickableMessage(pickableUpdate);
        }

        // Source: Survivalcraft/Game/ComponentBehavior.cs:ComponentBehavior.IsActive
        // Source: Survivalcraft/Game/ComponentHerdBehavior.cs:ComponentHerdBehavior.CallNearbyCreaturesHelp
        private void SendAdaptiveAnimalUpdates()
        {
            Project project = GameManager.Project;
            if (project == null || m_hostAnimals.Count == 0) return;

            Vector3[] playerPositions = project.FindSubsystem<SubsystemPlayers>(false)?
                .ComponentPlayers
                .Where(player => player?.ComponentBody != null)
                .Select(player => player.ComponentBody.Position)
                .ToArray() ?? Array.Empty<Vector3>();
            var candidates = new List<AnimalSyncCandidate>(m_hostAnimals.Count);
            foreach (Entity entity in m_hostAnimals.ToArray())
            {
                if (entity?.IsAddedToProject != true) continue;
                ComponentCreature creature = entity.FindComponent<ComponentCreature>();
                ComponentBody body = creature?.ComponentBody;
                if (creature == null || body == null) continue;

                ComponentBehavior activeBehavior = entity.FindComponents<ComponentBehavior>()
                    .Where(behavior => behavior != null && behavior.IsActive)
                    .OrderByDescending(behavior => behavior.ImportanceLevel)
                    .FirstOrDefault();
                ComponentChaseBehavior chase = entity.FindComponent<ComponentChaseBehavior>();
                ComponentCreature target = chase?.Target;
                ComponentHerdBehavior herd = entity.FindComponent<ComponentHerdBehavior>();
                ComponentCreatureModel model = creature.ComponentCreatureModel;
                AnimalSyncMetadata metadata = m_hostAnimalSync[entity];
                float health = creature.ComponentHealth?.Health ?? 0f;
                bool wasAttacked = metadata.HasSent && health < metadata.LastHealth - 0.001f;
                bool isAttacking = model?.AttackOrder == true || model?.IsAttackHitMoment == true;

                byte tier = 0;
                float nearestPlayerDistanceSquared = playerPositions.Length > 0
                    ? playerPositions.Min(position => Vector3.DistanceSquared(position, body.Position))
                    : float.MaxValue;
                if (nearestPlayerDistanceSquared <= 64f * 64f) tier = 1;
                if (nearestPlayerDistanceSquared <= 24f * 24f) tier = 2;
                if (target != null || wasAttacked) tier = 3;
                if (isAttacking) tier = 4;

                candidates.Add(new AnimalSyncCandidate
                {
                    Entity = entity,
                    Creature = creature,
                    Body = body,
                    BehaviorState = GetActiveBehaviorState(activeBehavior),
                    TargetEntityId = GetCreatureTargetNetworkId(target),
                    HerdName = herd?.HerdName ?? string.Empty,
                    SyncTier = tier
                });
            }

            foreach (AnimalSyncCandidate source in candidates.Where(candidate =>
                candidate.SyncTier >= 3 && !string.IsNullOrEmpty(candidate.HerdName)).ToArray())
            {
                foreach (AnimalSyncCandidate member in candidates)
                {
                    if (member.HerdName == source.HerdName &&
                        Vector3.DistanceSquared(member.Body.Position, source.Body.Position) < 256f)
                        member.SyncTier = Math.Max(member.SyncTier, (byte)3);
                }
            }

            double now = Time.RealTime;
            var bodyMessage = new BodyUpdateMessage();
            foreach (AnimalSyncCandidate candidate in candidates)
            {
                AnimalSyncMetadata metadata = m_hostAnimalSync[candidate.Entity];
                if (candidate.SyncTier >= 3)
                    metadata.HighPriorityUntil = now + 3.0;
                else if (now < metadata.HighPriorityUntil)
                    candidate.SyncTier = 3;

                candidate.StateChanged = !metadata.HasSent ||
                    metadata.BehaviorState != candidate.BehaviorState ||
                    metadata.TargetEntityId != candidate.TargetEntityId ||
                    metadata.HerdName != candidate.HerdName ||
                    metadata.SyncTier != candidate.SyncTier;
                if (!candidate.StateChanged && now < metadata.NextSendTime) continue;

                ComponentLocomotion locomotion = candidate.Creature.ComponentLocomotion;
                ComponentCreatureModel model = candidate.Creature.ComponentCreatureModel;
                BodyUpdateMessage.ChangeFlag flags = BodyUpdateMessage.ChangeFlag.Position |
                    BodyUpdateMessage.ChangeFlag.Rotation |
                    BodyUpdateMessage.ChangeFlag.Velocity |
                    BodyUpdateMessage.ChangeFlag.LookAngles |
                    BodyUpdateMessage.ChangeFlag.Movement |
                    BodyUpdateMessage.ChangeFlag.Template |
                    BodyUpdateMessage.ChangeFlag.Health;
                if (candidate.StateChanged) flags |= BodyUpdateMessage.ChangeFlag.BehaviorState;
                bodyMessage.Bodies.Add(new BodyUpdateMessage.BodyItem
                {
                    EntityId = m_hostAnimalIds[candidate.Entity],
                    Flags = flags,
                    Position = candidate.Body.Position,
                    Rotation = candidate.Body.Rotation,
                    Velocity = candidate.Body.Velocity,
                    LookAngles = locomotion?.LookAngles ?? Vector2.Zero,
                    WalkOrder = locomotion?.LastWalkOrder,
                    FlyOrder = locomotion?.LastFlyOrder,
                    SwimOrder = locomotion?.LastSwimOrder,
                    TurnOrder = locomotion?.LastTurnOrder ?? Vector2.Zero,
                    JumpOrder = locomotion?.LastJumpOrder ?? 0f,
                    AttackOrder = model?.AttackOrder ?? false,
                    FeedOrder = model?.FeedOrder ?? false,
                    TemplateName = candidate.Entity.ValuesDictionary?.DatabaseObject?.Name,
                    SyncTier = candidate.SyncTier,
                    ActiveBehaviorState = candidate.BehaviorState,
                    TargetEntityId = candidate.TargetEntityId,
                    HerdName = candidate.HerdName,
                    Health = candidate.Creature.ComponentHealth?.Health ?? 0f
                });

                metadata.HasSent = true;
                metadata.BehaviorState = candidate.BehaviorState;
                metadata.TargetEntityId = candidate.TargetEntityId;
                metadata.HerdName = candidate.HerdName;
                metadata.SyncTier = candidate.SyncTier;
                metadata.LastHealth = candidate.Creature.ComponentHealth?.Health ?? 0f;
                metadata.NextSendTime = now + GetAnimalSyncInterval(candidate.SyncTier);

                if (bodyMessage.Bodies.Count >= AnimalSyncBatchSize)
                {
                    NetworkMessageSender.SendBodyUpdateMessage(bodyMessage);
                    bodyMessage = new BodyUpdateMessage();
                }
            }
            if (bodyMessage.Bodies.Count > 0)
                NetworkMessageSender.SendBodyUpdateMessage(bodyMessage);
        }

        private string GetActiveBehaviorState(ComponentBehavior behavior)
        {
            if (behavior == null) return string.Empty;
            for (Type type = behavior.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                FieldInfo field = type.GetField("m_stateMachine",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null || field.FieldType != typeof(StateMachine)) continue;
                StateMachine stateMachine = ModManager.ModParentField.GetParentField<StateMachine>(
                    behavior, field.Name, field.DeclaringType);
                return behavior.GetType().Name + ":" + (stateMachine?.CurrentState ?? string.Empty);
            }
            return behavior.GetType().Name;
        }

        private int GetCreatureTargetNetworkId(ComponentCreature target)
        {
            Entity targetEntity = target?.Entity;
            if (targetEntity == null || targetEntity.IsAddedToProject != true) return 0;
            if (m_hostAnimalIds.TryGetValue(targetEntity, out ushort animalId)) return animalId;
            ComponentPlayer targetPlayer = targetEntity.FindComponent<ComponentPlayer>();
            if (targetPlayer == null) return 0;
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
            {
                if (item.Value?.ComponentPlayer == targetPlayer) return -(item.Key + 1);
            }
            return -(client.ClientID + 1);
        }

        private static double GetAnimalSyncInterval(byte tier)
        {
            switch (tier)
            {
                case 4: return 1.0 / 30.0;
                case 3: return 1.0 / 20.0;
                case 2: return 0.1;
                case 1: return 0.2;
                default: return 1.0;
            }
        }

        // ====================================================================
        // 发送: 生命值 (周期性)
        // ====================================================================
        private void SendGamePlayerHealthMessage()
        {
            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null || !IsHost) return;
            var players = subsystemPlayers.ComponentPlayers;

            int currentClientId = client.ClientID;
            ComponentPlayer item = players.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (item != null)
            {
                var health = item.ComponentHealth;
                if (health == null) return;

                float lastHealth;
                if (!m_playerHealthCache.TryGetValue(currentClientId, out lastHealth))
                    lastHealth = health.Health;

                float change = health.Health - lastHealth;
                bool isDead = health.Health <= 0f;

                // 仅在变化时发送
                if (Math.Abs(change) > 0.01f || isDead)
                {
                    NetworkMessageSender.SendPlayerHealthMessage(
                        client.ClientID, health.Health, 1f, change, isDead);
                    m_playerHealthCache[currentClientId] = health.Health;
                }
            }
            foreach (KeyValuePair<int, PlayerData> remote in m_networkPlayerData.ToArray())
            {
                if (remote.Key > 0 && remote.Value?.ComponentPlayer != null)
                    SendAuthoritativePlayerHealth(remote.Key, remote.Value.ComponentPlayer);
            }
        }

        private void SendAuthoritativePlayerHealth(int networkClientId, ComponentPlayer player)
        {
            ComponentHealth health = player.ComponentHealth;
            if (health == null) return;
            if (!m_playerHealthCache.TryGetValue(networkClientId, out float lastHealth))
                lastHealth = health.Health;
            float change = health.Health - lastHealth;
            bool isDead = health.Health <= 0f;
            if (Math.Abs(change) <= 0.01f && !isDead) return;
            NetworkMessageSender.SendPlayerHealthMessage(
                networkClientId, health.Health, 1f, change, isDead);
            m_playerHealthCache[networkClientId] = health.Health;
        }

        // ====================================================================
        // 渲染远程玩家
        // ====================================================================
        private void RenderRemotePlayers()
        {
            if (!client.IsConnected || RemotePlayers.Count == 0) return;

            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null) return;
            var players = subsystemPlayers.ComponentPlayers;
            if (players.Count == 0) return;

            // 获取本地玩家相机
            var localPlayer = players[0];
            var camera = localPlayer.GameWidget?.ActiveCamera;
            if (camera == null) return;

            // 延迟初始化 PrimitivesRenderer3D
            if (m_primitivesRenderer3D == null)
                m_primitivesRenderer3D = new PrimitivesRenderer3D();

            float cubeSize = 0.4f;
            var color = Color.White;
            double now = Time.RealTime;

            foreach (var kvp in RemotePlayers)
            {
                var state = kvp.Value;
                // 超过 5 秒没有更新, 跳过
                if (now - state.LastUpdateTime > 5.0) continue;

                Vector3 pos = state.Position;
                Vector3 offset = new Vector3(-cubeSize, 0, -cubeSize);
                Vector3 p1 = pos + new Vector3(-cubeSize, 0, -cubeSize);
                Vector3 p2 = pos + new Vector3(cubeSize, 0, -cubeSize);
                Vector3 p3 = pos + new Vector3(cubeSize, 2 * cubeSize, cubeSize);
                Vector3 p4 = pos + new Vector3(-cubeSize, 2 * cubeSize, cubeSize);

                var flatBatch = m_primitivesRenderer3D.FlatBatch();
                flatBatch.QueueQuad(p1, p2, p3, p4, color);
            }

            m_primitivesRenderer3D.Flush(camera.ViewProjectionMatrix);
        }

        // ====================================================================
        // Client_GameStep: 处理每 Tick 的网络事件
        // ====================================================================
        private void Client_GameStep(GameStepData obj)
        {
            // 离开
            foreach (var item in obj.Leaves)
            {
                Log.Information($"[ScMP] Client left: {item.ClientID}");
                if (!IsHost && item.ClientID == 0)
                {
                    HandleHostDisconnected();
                    continue;
                }
                RemoveNetworkPlayer(item.ClientID);
                playerMappingManager.ReleasePlayerIndex(item.ClientID);
            }

            // 加入
            foreach (var item in obj.Joins)
            {
                Log.Information($"[ScMP] Client joining: {item.ClientID}");
                // Source: Comms/Comms.Drt/Func/Server/Set/ServerGame.cs:ServerGame.Handle
                // A single existing peer accepts or refuses a join. Only the room owner is allowed
                // to decide, otherwise another client can accept before the host requests a profile.
                if (!IsHost) continue;
                int assignedPlayerIndex = playerMappingManager.AssignPlayerIndex(item.ClientID);

                if (assignedPlayerIndex != -1)
                {
                    // Source: SuPlayScreen.GameCreate caches a snapshot before the world files are opened.
                    // Exporting a running world here fails because region files are locked by SubsystemTerrain.
                    if (Message.Read(item.JoinRequestBytes) is GameWorldInfoMessage worldInfo &&
                        SuPlayScreen.WorldData != null &&
                        SuPlayScreen.WorldDataName == worldInfo.Name &&
                        SuPlayScreen.WorldDataLastSaveTime == worldInfo.LastSaveTime)
                    {
                        Log.Information($"[ScMP] Assigned PlayerIndex {assignedPlayerIndex} to ClientID {item.ClientID}");
                        EnsurePlayerRecordsLoaded();
                        string recordKey = GetPlayerRecordKey(worldInfo.PlayerIdentity, worldInfo.PlayerName);
                        if (!m_playerRecords.TryGetValue(recordKey, out NetworkPlayerRecord joiningRecord))
                        {
                            if (!IsValidRequestedProfile(worldInfo))
                            {
                                playerMappingManager.ReleasePlayerIndex(item.ClientID);
                                client.RefuseJoinGame(item.ClientID, PlayerProfileRequiredReason);
                                continue;
                            }
                            joiningRecord = CreateInitialPlayerRecord(worldInfo);
                            NetworkPlayerRecord approvedRecord = joiningRecord;
                            int joiningClientId = item.ClientID;
                            DialogsManager.ShowDialog(null, new MessageDialog(
                                "Player Join Request", approvedRecord.Name + " wants to join the room.",
                                "OK", "Cancel", delegate (MessageDialogButton button)
                                {
                                    if (button == MessageDialogButton.Button1)
                                    {
                                        m_playerRecords[recordKey] = approvedRecord;
                                        m_playerRecordsDirty = true;
                                        AcceptNetworkPlayerJoin(
                                            joiningClientId, recordKey, approvedRecord, isNewApproval: true);
                                    }
                                    else
                                    {
                                        playerMappingManager.ReleasePlayerIndex(joiningClientId);
                                        client.RefuseJoinGame(joiningClientId, "Host declined the join request.");
                                    }
                                }));
                            continue;
                        }
                        // A persisted identity has already been approved for this world.
                        AcceptNetworkPlayerJoin(item.ClientID, recordKey, joiningRecord);
                    }
                    else
                    {
                        playerMappingManager.ReleasePlayerIndex(item.ClientID);
                        client.RefuseJoinGame(item.ClientID, "Host world snapshot is unavailable");
                    }
                }
                else
                {
                    Log.Information($"[ScMP] Game full, refusing ClientID {item.ClientID}");
                    client.RefuseJoinGame(item.ClientID, "Game is full");
                }
            }

            // 输入消息
            foreach (var item in obj.Inputs)
            {
                if (item.InputBytes == null || item.InputBytes.Length == 0) continue;

                Message message;
                try
                {
                    message = Message.Read(item.InputBytes);
                }
                catch (Exception ex)
                {
                    Log.Error($"[ScMP] Failed to parse message: {ex.Message}");
                    continue;
                }

                // 跳过自己发出的消息 (回环消息)
                if (message.GetSenderPort() == client.Address.Port)
                    continue;

                switch (message)
                {
                    case ChatMessage chat:
                        NetworkMessageHandler.HandleChatMessage(chat, item.ClientID);
                        break;
                    case GamePlayerPositionMessage pos:
                        QueueEndOfFrameAction(() => HandleGamePlayerPositionMessage(pos, item.ClientID));
                        break;
                    case GamePlayerInputMessage playerInput:
                        QueueEndOfFrameAction(() => HandleGamePlayerInputMessage(
                            playerInput, item.ClientID));
                        break;
                    case GameModifiedCellsMessage cells:
                        QueueEndOfFrameAction(() =>
                            NetworkMessageHandler.HandleModifiedCellsMessage(cells, item.ClientID));
                        break;
                    case GameWorldInfoMessage1 worldInfo:
                        Dispatcher.Dispatch(() => NetworkMessageHandler.HandleWorldInfoMessage(worldInfo, item.ClientID));
                        break;
                    case WorldControlRequestMessage worldControl:
                        QueueEndOfFrameAction(() => HandleWorldControlRequest(worldControl, item.ClientID));
                        break;
                    case PlayerProfileMessage playerProfile:
                        QueueEndOfFrameAction(() => HandlePlayerProfileMessage(playerProfile, item.ClientID));
                        break;
                    case GamePakWorldMessage pakWorld:
                        NetworkMessageHandler.HandlePakWorldMessage(pakWorld, item.ClientID);
                        break;
                    case GamePlayerHealthMessage health:
                        QueueEndOfFrameAction(() =>
                            NetworkMessageHandler.HandlePlayerHealthMessage(health, item.ClientID));
                        break;
                    case GameKickPlayerMessage kick:
                        HandleGameKickPlayerMessage(kick, item.ClientID);
                        break;
                    case EntityMessage entityMessage:
                        QueueEndOfFrameAction(() => HandleAnimalEntityMessage(entityMessage, item.ClientID));
                        break;
                    case BodyUpdateMessage bodyUpdate:
                        QueueEndOfFrameAction(() => HandleAnimalBodyUpdate(bodyUpdate, item.ClientID));
                        break;
                    case AnimalInteractionMessage animalInteraction:
                        QueueEndOfFrameAction(() => HandleAnimalInteractionMessage(
                            animalInteraction, item.ClientID));
                        break;
                    case PickableSyncMessage pickableSync:
                        QueueEndOfFrameAction(() => HandlePickableSyncMessage(pickableSync, item.ClientID));
                        break;
                    default:
                        Log.Error($"[ScMP] Unknown message type: {message.GetType().Name}");
                        break;
                }
            }
        }

        // Source: Comms/Comms.Drt/Func/Client/Client.cs:Client.AcceptJoinGame
        private void AcceptNetworkPlayerJoin(int joiningClientId, string recordKey,
            NetworkPlayerRecord joiningRecord, bool isNewApproval = false)
        {
            try
            {
                CreateNetworkPlayer(joiningClientId, joiningRecord.Name, recordKey);
                if (!m_networkPlayerData.TryGetValue(joiningClientId, out PlayerData joinedPlayer) ||
                    joinedPlayer?.ComponentPlayer == null)
                    throw new InvalidOperationException("Network player could not be created.");
                joiningRecord = CapturePlayerRecord(joinedPlayer);
                m_playerRecords[recordKey] = joiningRecord;
                m_playerRecordsDirty = true;
                SavePlayerRecords();
                client.AcceptJoinGame(joiningClientId);
                NetworkMessageSender.SendPakWorldMessage(
                    SuPlayScreen.WorldDataName, SuPlayScreen.WorldData,
                    SuPlayScreen.WorldDataLastSaveTime, joiningClientId,
                    m_sessionRandomSeed, CaptureSubsystemRandomStates(), joiningRecord);
                SendTerrainCatchUp(joiningClientId);
                Log.Information($"[ScMP] Accepted ClientID {joiningClientId} and sent cached world data " +
                    $"({SuPlayScreen.WorldData.Length} bytes)");
            }
            catch (Exception ex)
            {
                RemoveNetworkPlayer(joiningClientId);
                if (isNewApproval)
                {
                    m_playerRecords.Remove(recordKey);
                    m_playerRecordsDirty = true;
                    SavePlayerRecords();
                }
                playerMappingManager.ReleasePlayerIndex(joiningClientId);
                client.RefuseJoinGame(joiningClientId, "Failed to prepare player: " + ex.Message);
                Log.Error($"[ScMP] Failed to accept ClientID {joiningClientId}: {ex.Message}");
            }
        }

        // ====================================================================
        // 消息处理
        // ====================================================================
        public void HandleGamePlayerPositionMessage(GamePlayerPositionMessage msg, int clientID)
        {
            // Source: msg.PlayerIndex = 发送方的 ClientID
            // 写入 RemotePlayers 而非本地 ComponentPlayers, 避免覆盖本地玩家
            // Source: Comms/GameStepData.Inputs
            // The transport ClientID is authoritative. The ID serialized in a packet can belong
            // to an earlier connection after a client leaves and rejoins.
            if (IsHost || clientID != 0) return;
            int remoteClientId = msg.PlayerIndex;
            if (remoteClientId == client.ClientID)
                ApplyAuthoritativeLocalPlayerState(msg);
            if (remoteClientId == client.ClientID)
                return; // 忽略自己发回的消息

            NetworkPlayerState state;
            if (!RemotePlayers.TryGetValue(remoteClientId, out state))
            {
                state = new NetworkPlayerState { ClientID = remoteClientId };
                RemotePlayers[remoteClientId] = state;
            }

            state.Position = msg.Position;
            state.Rotation = msg.Rotation;
            state.Velocity = msg.Velocity;
            state.ServerTick = msg.ServerTick;
            state.LookAngles = msg.LookAngles;
            state.WalkOrder = msg.WalkOrder;
            state.JumpOrder = msg.JumpOrder;
            state.PokingPhase = msg.PokingPhase;
            state.AttackOrder = msg.AttackOrder;
            state.RowLeftOrder = msg.RowLeftOrder;
            state.RowRightOrder = msg.RowRightOrder;
            state.IsCrouching = msg.IsCrouching;
            state.IsFlying = msg.IsFlying;
            state.IsRiding = msg.IsRiding;
            state.IsGrounded = msg.IsGrounded;
            state.ActiveSlotIndex = msg.ActiveSlotIndex;
            state.HandItemValue = msg.HandItemValue;
            state.HandItemCount = msg.HandItemCount;
            state.ItemOffset = msg.ItemOffset;
            state.ItemRotation = msg.ItemRotation;
            state.AimHandAngle = msg.AimHandAngle;
            state.LastUpdateTime = Time.RealTime;

            if (m_networkPlayerData.TryGetValue(remoteClientId, out PlayerData playerData) &&
                playerData.ComponentPlayer != null)
            {
                ComponentBody body = playerData.ComponentPlayer.ComponentBody;
                body.TargetCrouchFactor = msg.IsCrouching ? 1f : 0f;
                // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.LookAngles
                // Body rotation carries yaw; pitch is stored separately in m_lookAngles.
                ComponentLocomotion locomotion = playerData.ComponentPlayer.ComponentLocomotion;
                if (locomotion != null)
                {
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "m_lookAngles", msg.LookAngles, typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "<LastWalkOrder>k__BackingField", msg.WalkOrder, typeof(ComponentLocomotion));
                    ModManager.ModParentField.ModifyParentField(
                        locomotion, "<LastJumpOrder>k__BackingField", msg.JumpOrder, typeof(ComponentLocomotion));
                    locomotion.IsCreativeFlyEnabled = msg.IsFlying;
                }
                ComponentMiner remoteMiner = playerData.ComponentPlayer.ComponentMiner;
                if (remoteMiner != null)
                    ModManager.ModParentField.ModifyParentField(
                        remoteMiner, "<PokingPhase>k__BackingField", msg.PokingPhase, typeof(ComponentMiner));
                ComponentCreatureModel remoteModel = playerData.ComponentPlayer.ComponentCreatureModel;
                if (remoteModel != null)
                {
                    remoteModel.AttackOrder = msg.AttackOrder;
                    remoteModel.RowLeftOrder = msg.RowLeftOrder;
                    remoteModel.RowRightOrder = msg.RowRightOrder;
                    remoteModel.InHandItemOffsetOrder = msg.ItemOffset;
                    remoteModel.InHandItemRotationOrder = msg.ItemRotation;
                    remoteModel.AimHandAngleOrder = msg.AimHandAngle;
                }
                IInventory inventory = playerData.ComponentPlayer.ComponentMiner?.Inventory;
                if (inventory != null && msg.SlotValues != null)
                {
                    if (msg.ActiveSlotIndex >= 0 && msg.ActiveSlotIndex < inventory.SlotsCount)
                        inventory.ActiveSlotIndex = msg.ActiveSlotIndex;
                    int slotsCount = Math.Min(inventory.SlotsCount, msg.SlotValues.Length);
                    for (int i = 0; i < slotsCount; i++)
                    {
                        if (inventory.GetSlotValue(i) == msg.SlotValues[i] &&
                            inventory.GetSlotCount(i) == msg.SlotCounts[i]) continue;
                        inventory.RemoveSlotItems(i, int.MaxValue);
                        if (msg.SlotCounts[i] > 0)
                            inventory.AddSlotItems(i, msg.SlotValues[i], msg.SlotCounts[i]);
                    }
                }
            }
        }

        private void ApplyAuthoritativeLocalPlayerState(GamePlayerPositionMessage msg)
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer == null) return;

            float delaySample = MathUtils.Clamp(
                (client.Step - msg.ServerTick) * ServerTickDuration, 0f, 0.5f);
            m_smoothedNetworkDelay = m_smoothedNetworkDelay <= 0f
                ? delaySample
                : MathUtils.Lerp(m_smoothedNetworkDelay, delaySample, 0.1f);
            // Client movement is predicted and never rewound. The host-side split-screen avatar
            // follows this trajectory with a bounded catch-up velocity instead.

            IInventory inventory = localPlayer.ComponentMiner?.Inventory;
            if (inventory == null || msg.SlotValues == null || msg.SlotValues.Length == 0) return;
            if (msg.ActiveSlotIndex >= 0 && msg.ActiveSlotIndex < inventory.SlotsCount)
                inventory.ActiveSlotIndex = msg.ActiveSlotIndex;
            int slotsCount = Math.Min(inventory.SlotsCount,
                Math.Min(msg.SlotValues.Length, msg.SlotCounts?.Length ?? 0));
            for (int i = 0; i < slotsCount; i++)
            {
                if (inventory.GetSlotValue(i) == msg.SlotValues[i] &&
                    inventory.GetSlotCount(i) == msg.SlotCounts[i]) continue;
                inventory.RemoveSlotItems(i, int.MaxValue);
                if (msg.SlotCounts[i] > 0)
                    inventory.AddSlotItems(i, msg.SlotValues[i], msg.SlotCounts[i]);
            }
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        public bool TrySendAnimalAttackRequest(ComponentPlayer player, Ray3 hitRay)
        {
            if (IsHost || client?.IsConnected != true || player?.ComponentMiner == null ||
                GameManager.Project == null)
                return false;
            BodyRaycastResult? result = player.ComponentMiner.Raycast<BodyRaycastResult>(
                hitRay, RaycastMode.Interaction);
            if (!result.HasValue) return false;

            Entity targetEntity = result.Value.ComponentBody?.Entity;
            if (targetEntity == null) return false;
            ushort targetId = 0;
            foreach (KeyValuePair<ushort, Entity> item in m_remoteAnimals)
            {
                if (ReferenceEquals(item.Value, targetEntity))
                {
                    targetId = item.Key;
                    break;
                }
            }
            if (targetId == 0) return false;

            Vector3 hitPoint = result.Value.HitPoint();
            if (Vector3.DistanceSquared(hitPoint, player.ComponentCreatureModel.EyePosition) > 2.25f * 2.25f)
                return false;
            Vector3 direction = hitRay.Direction.LengthSquared() > 0.0001f
                ? Vector3.Normalize(hitRay.Direction)
                : Vector3.UnitZ;
            var message = new AnimalInteractionMessage(targetId, client.Step, hitPoint, direction);
            client.SendInput(Message.WriteWithSender(message, client.Address));
            return true;
        }

        private void HandleAnimalInteractionMessage(AnimalInteractionMessage message, int sourceClientId)
        {
            if (!IsHost || message == null || sourceClientId <= 0 || GameManager.Project == null)
                return;
            if (!m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData))
                return;
            ComponentPlayer attacker = playerData?.ComponentPlayer;
            ComponentMiner miner = attacker?.ComponentMiner;
            if (attacker == null || miner == null || attacker.ComponentHealth?.Health <= 0f)
                return;

            Entity targetEntity = m_hostAnimalIds
                .FirstOrDefault(item => item.Value == message.TargetAnimalId).Key;
            ComponentCreature target = targetEntity?.FindComponent<ComponentCreature>();
            ComponentBody targetBody = target?.ComponentBody;
            if (targetEntity?.IsAddedToProject != true || targetBody == null ||
                target.ComponentHealth?.Health <= 0f)
                return;

            Vector3 eyePosition = attacker.ComponentCreatureModel.EyePosition;
            Vector3 targetPoint = targetBody.BoundingBox.Center();
            Vector3 toTarget = targetPoint - eyePosition;
            if (toTarget.LengthSquared() > 4f * 4f) return;
            Vector3 direction = message.HitDirection.LengthSquared() > 0.0001f
                ? Vector3.Normalize(message.HitDirection)
                : Vector3.Normalize(toTarget);
            if (toTarget.LengthSquared() > 0.0001f &&
                Vector3.Dot(direction, Vector3.Normalize(toTarget)) < 0.2f)
                return;

            // Source: Survivalcraft/Game/ComponentChaseBehavior.cs:ComponentChaseBehavior.Attack
            // A network attack is already spatially validated above. Establish predator aggro
            // immediately instead of waiting for a second request when host-side hit RNG misses.
            CreatureCategory predatorMask = CreatureCategory.LandPredator | CreatureCategory.WaterPredator;
            if ((target.Category & predatorMask) != 0)
            {
                ComponentChaseBehavior chase = targetEntity.FindComponent<ComponentChaseBehavior>();
                if (chase != null && chase.Target == null)
                    chase.Attack(attacker, 30f, 60f, true);
                ComponentHerdBehavior herd = targetEntity.FindComponent<ComponentHerdBehavior>();
                herd?.CallNearbyCreaturesHelp(attacker, 20f, 30f, false);
            }

            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Hit
            // The host recomputes hit probability, tool power, damage and Attacked events.
            miner.Hit(targetBody, targetPoint, direction);
            if (m_hostAnimalSync.TryGetValue(targetEntity, out AnimalSyncMetadata metadata))
            {
                metadata.NextSendTime = 0.0;
                metadata.HighPriorityUntil = Time.RealTime + 3.0;
            }
        }

        private void HandleAnimalEntityMessage(EntityMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null) return;
            if (message.Action == EntityMessage.EntityAction.Add)
            {
                if (!string.IsNullOrWhiteSpace(message.TemplateName))
                    m_remoteAnimalTemplates[message.EntityId] = message.TemplateName;
                return;
            }

            if (m_remoteAnimals.TryGetValue(message.EntityId, out Entity entity))
            {
                if (entity?.IsAddedToProject == true && entity.Project == GameManager.Project)
                    entity.Project.RemoveEntity(entity, true);
                m_remoteAnimals.Remove(message.EntityId);
            }
            m_remoteAnimalTemplates.Remove(message.EntityId);
            m_remoteAnimalSync.Remove(message.EntityId);
        }

        private Entity EnsureRemoteAnimal(ushort id, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            if (m_remoteAnimals.TryGetValue(id, out Entity existing) && existing?.IsAddedToProject == true)
                return existing;
            if (!m_remoteAnimalTemplates.TryGetValue(id, out string templateName) ||
                string.IsNullOrWhiteSpace(templateName) || GameManager.Project == null)
                return null;

            try
            {
                Entity entity = DatabaseManager.CreateEntity(
                    GameManager.Project, templateName, new ValuesDictionary(), true);
                ComponentBody body = entity?.FindComponent<ComponentBody>();
                if (entity == null || body == null) return null;
                body.Position = position;
                body.Rotation = rotation;
                body.Velocity = velocity;
                GameManager.Project.AddEntity(entity);
                // Source: Survivalcraft/Game/SubsystemUpdate.cs:SubsystemUpdate.RemoveUpdateable
                // Remote animals are presentation replicas. Their local AI and spawn state machine
                // must not compete with authoritative movement or despawn them at chunk edges.
                SubsystemUpdate subsystemUpdate = GameManager.Project.FindSubsystem<SubsystemUpdate>(true);
                foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                {
                    if (updateable is ComponentBehavior || updateable is ComponentLocomotion ||
                        ReferenceEquals(updateable, entity.FindComponent<ComponentSpawn>()))
                        subsystemUpdate.RemoveUpdateable(updateable);
                }
                m_remoteAnimals[id] = entity;
                return entity;
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Failed to recreate animal {id} ({templateName}): {ex.Message}");
                m_remoteAnimals.Remove(id);
                return null;
            }
        }

        private void HandleAnimalBodyUpdate(BodyUpdateMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message?.Bodies == null || GameManager.Project == null) return;
            MaintainClientWorldObjects();
            foreach (BodyUpdateMessage.BodyItem item in message.Bodies)
            {
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Template) &&
                    !string.IsNullOrWhiteSpace(item.TemplateName))
                    m_remoteAnimalTemplates[item.EntityId] = item.TemplateName;
                Entity entity = EnsureRemoteAnimal(item.EntityId, item.Position, item.Rotation, item.Velocity);
                ComponentCreature creature = entity?.FindComponent<ComponentCreature>();
                ComponentBody body = creature?.ComponentBody;
                if (creature == null || body == null) continue;

                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.BehaviorState))
                    ApplyRemoteAnimalBehaviorState(item.EntityId, entity, item);
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Health) &&
                    creature.ComponentHealth != null)
                {
                    ModManager.ModParentField.ModifyParentField(
                        creature.ComponentHealth, "<Health>k__BackingField",
                        MathUtils.Saturate(item.Health), typeof(ComponentHealth));
                }

                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Position))
                {
                    body.Position = Vector3.DistanceSquared(body.Position, item.Position) > 16f
                        ? item.Position
                        : Vector3.Lerp(body.Position, item.Position, 0.5f);
                }
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Rotation))
                    body.Rotation = Quaternion.Slerp(body.Rotation, item.Rotation, 0.5f);
                if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Velocity)) body.Velocity = item.Velocity;
                ComponentLocomotion locomotion = creature.ComponentLocomotion;
                if (locomotion != null)
                {
                    if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.LookAngles))
                        ModManager.ModParentField.ModifyParentField(
                            locomotion, "m_lookAngles", item.LookAngles, typeof(ComponentLocomotion));
                    if (item.Flags.HasFlag(BodyUpdateMessage.ChangeFlag.Movement))
                    {
                        locomotion.WalkOrder = item.WalkOrder;
                        locomotion.FlyOrder = item.FlyOrder;
                        locomotion.SwimOrder = item.SwimOrder;
                        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.Update
                        // Body.Rotation is already authoritative. Replaying TurnOrder integrates
                        // yaw a second time and makes the head snap back on every network packet.
                        locomotion.TurnOrder = Vector2.Zero;
                        locomotion.JumpOrder = item.JumpOrder;
                    }
                }
                ComponentCreatureModel model = creature.ComponentCreatureModel;
                if (model != null)
                {
                    model.AttackOrder = item.AttackOrder;
                    model.FeedOrder = item.FeedOrder;
                }
            }
        }

        private void ApplyRemoteAnimalBehaviorState(ushort id, Entity entity, BodyUpdateMessage.BodyItem item)
        {
            m_remoteAnimalSync[id] = new RemoteAnimalSyncState
            {
                SyncTier = item.SyncTier,
                BehaviorState = item.ActiveBehaviorState ?? string.Empty,
                TargetEntityId = item.TargetEntityId,
                HerdName = item.HerdName ?? string.Empty
            };
            string activeBehaviorName = (item.ActiveBehaviorState ?? string.Empty).Split(':')[0];
            foreach (ComponentBehavior behavior in entity.FindComponents<ComponentBehavior>())
            {
                if (behavior != null)
                    behavior.IsActive = behavior.GetType().Name == activeBehaviorName;
            }
        }

        private Pickable EnsureRemotePickable(ushort id, Vector3 position, Vector3 velocity)
        {
            if (m_remotePickables.TryGetValue(id, out Pickable existing) && existing != null && !existing.ToRemove)
                return existing;
            if (!m_remotePickableRecords.TryGetValue(id, out RemotePickableRecord record)) return null;
            SubsystemPickables subsystem = GameManager.Project?.FindSubsystem<SubsystemPickables>(false);
            if (subsystem == null) return null;
            Pickable pickable = subsystem.AddPickable(record.Value, record.Count, position, velocity, null);
            if (pickable != null) m_remotePickables[id] = pickable;
            return pickable;
        }

        private void HandlePickableSyncMessage(PickableSyncMessage message, int sourceClientId)
        {
            if (IsHost || sourceClientId != 0 || message == null || GameManager.Project == null) return;
            MaintainClientWorldObjects();
            switch (message.Action)
            {
                case PickableSyncMessage.PickAction.Create:
                    m_remotePickableRecords[message.Id] = new RemotePickableRecord
                    {
                        Value = message.Value,
                        Count = message.Count
                    };
                    Pickable created = EnsureRemotePickable(message.Id, message.Position, message.Velocity);
                    if (created != null) created.FlyToPosition = message.FlyToPosition;
                    break;
                case PickableSyncMessage.PickAction.UpdatePosition:
                    foreach (PickableSyncMessage.PickablePos state in message.Positions)
                    {
                        Pickable pickable = EnsureRemotePickable(state.Id, state.Position, state.Velocity);
                        if (pickable == null) continue;
                        pickable.Position = state.Position;
                        pickable.Velocity = state.Velocity;
                        pickable.FlyToPosition = state.FlyToPosition;
                    }
                    break;
                case PickableSyncMessage.PickAction.Delete:
                    if (m_remotePickables.TryGetValue(message.Id, out Pickable removed) && removed != null)
                        removed.ToRemove = true;
                    m_remotePickables.Remove(message.Id);
                    m_remotePickableRecords.Remove(message.Id);
                    break;
                case PickableSyncMessage.PickAction.SetFlyTo:
                    if (m_remotePickables.TryGetValue(message.Id, out Pickable target) && target != null)
                        target.FlyToPosition = message.FlyToPosition;
                    break;
            }
        }

        private void MaintainClientWorldObjects()
        {
            Project project = GameManager.Project;
            if (project == null || IsHost) return;
            if (!ReferenceEquals(m_clientWorldObjectsProject, project))
            {
                m_clientWorldObjectsProject = project;
                m_remoteAnimals.Clear();
                m_remoteAnimalSync.Clear();
                m_remotePickables.Clear();
            }

            var remoteAnimalSet = new HashSet<Entity>(m_remoteAnimals.Values.Where(entity => entity != null));
            foreach (Entity entity in project.Entities.Where(entity =>
                entity?.FindComponent<ComponentCreature>() != null &&
                entity.FindComponent<ComponentPlayer>() == null &&
                !remoteAnimalSet.Contains(entity)).ToArray())
            {
                if (entity?.IsAddedToProject == true) project.RemoveEntity(entity, true);
            }

            SubsystemPickables subsystem = project.FindSubsystem<SubsystemPickables>(false);
            if (subsystem == null) return;
            var remotePickableSet = new HashSet<Pickable>(m_remotePickables.Values.Where(pickable => pickable != null));
            foreach (Pickable pickable in subsystem.Pickables)
            {
                if (pickable != null && !remotePickableSet.Contains(pickable)) pickable.ToRemove = true;
            }
        }

        // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.Update
        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPlayer
        private void CreateNetworkPlayer(int clientId, string requestedName, string playerIdentity = null)
        {
            if (GameManager.Project == null)
            {
                m_pendingNetworkPlayers[clientId] = requestedName;
                m_pendingNetworkPlayerIdentities[clientId] = playerIdentity ?? string.Empty;
                return;
            }

            lock (m_creatingNetworkPlayers)
            {
                if (m_networkPlayerData.ContainsKey(clientId) || !m_creatingNetworkPlayers.Add(clientId)) return;
            }

            Project project = GameManager.Project;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(true);
            PlayerData playerData = null;
            Entity entity = null;
            try
            {
                PlayerData hostPlayer = players.PlayersData.FirstOrDefault();
                string playerName = string.IsNullOrWhiteSpace(requestedName) ? "NetPlayer" + clientId : requestedName.Trim();
                if (playerName.Length > 14) playerName = playerName.Substring(0, 14);
                string recordKey = string.IsNullOrWhiteSpace(playerIdentity) ? playerName : playerIdentity;
                m_playerRecords.TryGetValue(recordKey, out NetworkPlayerRecord record);
                playerData = new PlayerData(project)
                {
                    Name = record?.Name ?? playerName,
                    PlayerClass = record?.PlayerClass ?? PlayerClass.Male,
                    Level = record?.Level ?? 1f,
                    InputDevice = WidgetInputDevice.None,
                    SpawnPosition = record?.Position ?? hostPlayer?.ComponentPlayer?.ComponentBody.Position ?? players.GlobalSpawnPosition
                };
                if (!string.IsNullOrEmpty(record?.SkinName)) playerData.CharacterSkinName = record.SkinName;

                // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.AddPlayerData
                // PlayerIndex 0 belongs to the locally controlled player. Include detached network
                // avatars when selecting an index because they are intentionally absent from PlayersData.
                int freePlayerIndex = Enumerable.Range(1, Math.Max(0, SubsystemPlayers.MaxPlayers - 1))
                    .FirstOrDefault(index =>
                        !players.PlayersData.Any(player => player.PlayerIndex == index) &&
                        !m_networkPlayerData.Values.Any(player => player.PlayerIndex == index));
                if (freePlayerIndex == 0)
                    throw new InvalidOperationException("No free remote player index.");

                ModManager.ModParentField.ModifyParentField(
                    players, "m_nextPlayerIndex", freePlayerIndex, typeof(SubsystemPlayers));
                players.AddPlayerData(playerData);

                var overrides = new ValuesDictionary
                {
                    { "Player", new ValuesDictionary { { "PlayerIndex", playerData.PlayerIndex } } },
                    { "Intro", new ValuesDictionary { { "PlayIntro", false } } }
                };
                if (record != null && !record.HasReceivedInitialItems)
                {
                    InvokeInitialPlayerSpawn(playerData, record.Position);
                    entity = playerData.ComponentPlayer?.Entity ??
                        throw new InvalidOperationException("Initial network player spawn failed.");
                    record.HasReceivedInitialItems = true;
                }
                else
                {
                    entity = DatabaseManager.CreateEntity(
                        project, playerData.GetEntityTemplateName(), overrides, true);
                    ComponentBody body = entity.FindComponent<ComponentBody>(true);
                    body.Position = record != null
                        ? record.Position
                        : playerData.SpawnPosition + new Vector3(1f, 0f, 0f);
                    project.AddEntity(entity);
                }

                // Source: Survivalcraft/Game/SubsystemUpdate.cs:SubsystemUpdate.RemoveUpdateable
                // Remote avatars are driven by network state. Local player/input/locomotion/miner
                // updates otherwise consume the local controls and clear animation orders.
                SubsystemUpdate subsystemUpdate = project.FindSubsystem<SubsystemUpdate>(true);
                foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                {
                    if (!IsHost && (updateable is ComponentPlayer || updateable is ComponentInput ||
                        updateable is ComponentLocomotion || updateable is ComponentMiner)
                    )
                        subsystemUpdate.RemoveUpdateable(updateable);
                }

                // Source: GameEntitySystem/Project.cs:Project.SaveEntities
                // Subsystems already received EntityAdded. Remove only from the persistence set;
                // runtime subsystem references remain active until RemoveNetworkPlayer fires removal events.
                Dictionary<Entity, bool> projectEntities = ModManager.ModParentField.GetParentField<Dictionary<Entity, bool>>(
                    project, "m_entities", typeof(Project));
                projectEntities.Remove(entity);

                IInventory inventory = playerData.ComponentPlayer?.ComponentMiner?.Inventory;
                if (record?.SlotValues != null && record.SlotCounts != null && inventory != null)
                {
                    int slotsCount = Math.Min(inventory.SlotsCount,
                        Math.Min(record.SlotValues.Length, record.SlotCounts.Length));
                    for (int i = 0; i < slotsCount; i++)
                    {
                        inventory.RemoveSlotItems(i, int.MaxValue);
                        if (record.SlotCounts[i] > 0)
                            inventory.AddSlotItems(i, record.SlotValues[i], record.SlotCounts[i]);
                    }
                }
                ApplyClothes(playerData.ComponentPlayer, record?.Clothes);
                if (record != null && playerData.ComponentPlayer?.ComponentHealth != null)
                    ModManager.ModParentField.ModifyParentField(
                        playerData.ComponentPlayer.ComponentHealth, "<Health>k__BackingField",
                        MathUtils.Saturate(record.Health), typeof(ComponentHealth));

                // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerData
                StateMachine stateMachine = ModManager.ModParentField.GetParentField<StateMachine>(
                    playerData, "m_stateMachine", typeof(PlayerData));
                stateMachine.TransitionTo("Playing");

                // Source: Survivalcraft/Game/SubsystemGameWidgets.cs:SubsystemGameWidgets.RemoveGameWidget
                SubsystemGameWidgets gameWidgets = project.FindSubsystem<SubsystemGameWidgets>(true);
                GameWidget networkGameWidget = playerData.GameWidget;
                MethodInfo removeGameWidget = ModManager.ModParentMethod.GetInstanceMethodInfo(
                    typeof(SubsystemGameWidgets), "RemoveGameWidget", new[] { typeof(GameWidget) });
                removeGameWidget.Invoke(gameWidgets, new object[] { networkGameWidget });

                // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.Save
                // Keep network avatars in the runtime component list but out of PlayersData so
                // autosave and map exit never persist them as local split-screen players.
                List<PlayerData> playerList = ModManager.ModParentField.GetParentField<List<PlayerData>>(
                    players, "m_playersData", typeof(SubsystemPlayers));
                playerList.Remove(playerData);
                List<ComponentPlayer> componentPlayers = ModManager.ModParentField.GetParentField<List<ComponentPlayer>>(
                    players, "m_componentPlayers", typeof(SubsystemPlayers));
                if (playerData.ComponentPlayer != null && !componentPlayers.Contains(playerData.ComponentPlayer))
                    componentPlayers.Add(playerData.ComponentPlayer);

                m_networkPlayerData.Add(clientId, playerData);
                m_clientRecordKeys[clientId] = recordKey;
                m_pendingNetworkPlayers.Remove(clientId);
                m_pendingNetworkPlayerIdentities.Remove(clientId);
                if (clientId == 0) m_shouldCreateHostAvatar = false;
                Log.Information($"[ScMP] Created transient network player for ClientID {clientId}, PlayerIndex={playerData.PlayerIndex}");
            }
            catch (Exception ex)
            {
                List<PlayerData> playerList = ModManager.ModParentField.GetParentField<List<PlayerData>>(
                    players, "m_playersData", typeof(SubsystemPlayers));
                if (playerData != null) playerList.Remove(playerData);
                if (entity?.IsAddedToProject == true) project.RemoveEntity(entity, true);
                playerData?.Dispose();
                m_pendingNetworkPlayers[clientId] = requestedName;
                m_pendingNetworkPlayerIdentities[clientId] = playerIdentity ?? string.Empty;
                Log.Error($"[ScMP] Failed to create network player for ClientID {clientId}: {ex.Message}");
            }
            finally
            {
                lock (m_creatingNetworkPlayers) m_creatingNetworkPlayers.Remove(clientId);
            }
        }

        private void RemoveNetworkPlayer(int clientId)
        {
            if (!m_networkPlayerData.TryGetValue(clientId, out PlayerData playerData)) return;
            string recordKey = m_clientRecordKeys.TryGetValue(clientId, out string key) ? key : playerData.Name;
            NetworkPlayerRecord record = CapturePlayerRecord(playerData);
            m_playerRecords[recordKey] = record;
            if (IsHost)
            {
                m_playerRecordsDirty = true;
                SavePlayerRecords();
            }
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players != null)
            {
                // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.RemovePlayerData
                // The network GameWidget was already detached, so bypass PlayerRemoved to avoid
                // SubsystemGameWidgets trying to remove the same child twice.
                List<PlayerData> playerList = ModManager.ModParentField.GetParentField<List<PlayerData>>(
                    players, "m_playersData", typeof(SubsystemPlayers));
                playerList.Remove(playerData);
                List<ComponentPlayer> componentPlayers = ModManager.ModParentField.GetParentField<List<ComponentPlayer>>(
                    players, "m_componentPlayers", typeof(SubsystemPlayers));
                if (playerData.ComponentPlayer != null)
                {
                    componentPlayers.Remove(playerData.ComponentPlayer);
                    GameManager.Project.RemoveEntity(playerData.ComponentPlayer.Entity, true);
                }
                playerData.Dispose();
            }
            m_networkPlayerData.Remove(clientId);
            m_networkPlayerInputs.Remove(clientId);
            m_pendingNetworkPlayers.Remove(clientId);
            m_pendingNetworkPlayerIdentities.Remove(clientId);
            m_clientRecordKeys.Remove(clientId);
        }

        // Source: Survivalcraft/Game/UserManager.cs:UserManager.UserManager
        private static string GetPlayerRecordKey(string identity, string fallbackName)
        {
            return !string.IsNullOrWhiteSpace(identity)
                ? identity.Trim()
                : "name:" + (fallbackName ?? string.Empty).Trim();
        }

        private static string GetNetworkRecordKey(int clientId) => "network:" + clientId;

        private static bool IsValidRequestedProfile(GameWorldInfoMessage message)
        {
            if (message == null || !message.HasPlayerProfile ||
                !PlayerData.VerifyName((message.PlayerName ?? string.Empty).Trim()) ||
                string.IsNullOrWhiteSpace(message.PlayerIdentity) ||
                string.IsNullOrWhiteSpace(message.CharacterSkinName))
                return false;
            CharacterSkinsManager.UpdateCharacterSkinsList();
            if (!CharacterSkinsManager.CharacterSkinsNames.Contains(message.CharacterSkinName)) return false;
            PlayerClass? skinClass = CharacterSkinsManager.GetPlayerClass(message.CharacterSkinName);
            return !skinClass.HasValue || skinClass.Value == message.PlayerClass;
        }

        private static NetworkPlayerRecord CreateInitialPlayerRecord(GameWorldInfoMessage message)
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            Vector3 position = players?.ComponentPlayers.FirstOrDefault()?.ComponentBody.Position ??
                players?.GlobalSpawnPosition ?? Vector3.Zero;
            return new NetworkPlayerRecord
            {
                Name = message.PlayerName.Trim(),
                PlayerClass = message.PlayerClass,
                SkinName = message.CharacterSkinName,
                Position = position,
                Level = 1f,
                Health = 1f,
                HasReceivedInitialItems = false
            };
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPlayer
        private static void InvokeInitialPlayerSpawn(PlayerData playerData, Vector3 position)
        {
            Type spawnModeType = typeof(PlayerData).GetNestedType(
                "SpawnMode", BindingFlags.NonPublic);
            if (spawnModeType == null)
                throw new MissingMemberException(typeof(PlayerData).FullName, "SpawnMode");
            object initialNoIntro = Enum.Parse(spawnModeType, "InitialNoIntro");
            ModManager.ModParentMethod.InvokeParentMethod(
                playerData, "SpawnPlayer", new[] { typeof(Vector3), spawnModeType },
                position, initialNoIntro);
        }

        // Source: Survivalcraft/Game/GameManager.cs:GameManager.SaveProject
        // The multiplayer file is a sibling of Project.xml and is ignored by the base game.
        private void EnsurePlayerRecordsLoaded()
        {
            if (!IsHost) return;
            string directory = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false)?.DirectoryName;
            if (string.IsNullOrEmpty(directory) ||
                string.Equals(directory, m_playerRecordsWorldDirectory, StringComparison.OrdinalIgnoreCase))
                return;

            m_playerRecords.Clear();
            m_playerRecordsWorldDirectory = directory;
            m_playerRecordsDirty = false;
            string path = Storage.CombinePaths(directory, PlayerRecordsFileName);
            if (!Storage.FileExists(path)) return;
            try
            {
                XDocument document;
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                    document = XDocument.Load(stream);
                foreach (XElement element in document.Root?.Elements("Player") ?? Enumerable.Empty<XElement>())
                {
                    string identity = (string)element.Attribute("Identity");
                    if (string.IsNullOrWhiteSpace(identity)) continue;
                    var record = new NetworkPlayerRecord
                    {
                        Name = (string)element.Attribute("Name") ?? "Player",
                        PlayerClass = ParsePlayerClass((string)element.Attribute("Class")),
                        SkinName = (string)element.Attribute("Skin") ?? string.Empty,
                        Position = new Vector3(
                            ParseFloat((string)element.Attribute("X")),
                            ParseFloat((string)element.Attribute("Y")),
                            ParseFloat((string)element.Attribute("Z"))),
                        Level = ParseFloat((string)element.Attribute("Level"), 1f),
                        Health = ParseFloat((string)element.Attribute("Health"), 1f),
                        HasReceivedInitialItems = ParseBool(
                            (string)element.Attribute("InitialItems"), true)
                    };
                    XElement inventory = element.Element("Inventory");
                    XElement[] slots = inventory?.Elements("Slot").OrderBy(slot =>
                        (int?)slot.Attribute("Index") ?? 0).ToArray() ?? Array.Empty<XElement>();
                    int slotsCount = slots.Length == 0 ? 0 : slots.Max(slot =>
                        (int?)slot.Attribute("Index") ?? 0) + 1;
                    record.SlotValues = new int[slotsCount];
                    record.SlotCounts = new int[slotsCount];
                    foreach (XElement slot in slots)
                    {
                        int index = (int?)slot.Attribute("Index") ?? -1;
                        if (index < 0 || index >= slotsCount) continue;
                        record.SlotValues[index] = (int?)slot.Attribute("Value") ?? 0;
                        record.SlotCounts[index] = (int?)slot.Attribute("Count") ?? 0;
                    }
                    record.Clothes = CreateEmptyClothes();
                    foreach (XElement slot in element.Element("Clothes")?.Elements("Slot") ??
                        Enumerable.Empty<XElement>())
                    {
                        int index = (int?)slot.Attribute("Index") ?? -1;
                        if (index >= 0 && index < record.Clothes.Length)
                            record.Clothes[index] = ParseIntArray((string)slot.Attribute("Values"));
                    }
                    if (element.Attribute("InitialItems") == null)
                    {
                        bool hasClothes = record.Clothes.Any(slot => slot != null && slot.Length > 0);
                        record.HasReceivedInitialItems = hasClothes;
                    }
                    m_playerRecords[identity] = record;
                }
                Log.Information($"[ScMP] Loaded {m_playerRecords.Count} network player records");
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Failed to load network player records: {ex.Message}");
            }
        }

        private void SavePlayerRecords()
        {
            if (!IsHost || !m_playerRecordsDirty || string.IsNullOrEmpty(m_playerRecordsWorldDirectory)) return;
            try
            {
                var root = new XElement("ScMultiplayerPlayers", new XAttribute("Version", 1));
                foreach (KeyValuePair<string, NetworkPlayerRecord> item in m_playerRecords.OrderBy(pair => pair.Key))
                {
                    NetworkPlayerRecord record = item.Value;
                    if (record == null) continue;
                    var player = new XElement("Player",
                        new XAttribute("Identity", item.Key),
                        new XAttribute("Name", record.Name ?? "Player"),
                        new XAttribute("Class", record.PlayerClass),
                        new XAttribute("Skin", record.SkinName ?? string.Empty),
                        new XAttribute("X", FormatFloat(record.Position.X)),
                        new XAttribute("Y", FormatFloat(record.Position.Y)),
                        new XAttribute("Z", FormatFloat(record.Position.Z)),
                        new XAttribute("Level", FormatFloat(record.Level)),
                        new XAttribute("Health", FormatFloat(record.Health)),
                        new XAttribute("InitialItems", record.HasReceivedInitialItems));
                    var inventory = new XElement("Inventory");
                    int slotsCount = Math.Min(record.SlotValues?.Length ?? 0, record.SlotCounts?.Length ?? 0);
                    for (int i = 0; i < slotsCount; i++)
                        inventory.Add(new XElement("Slot", new XAttribute("Index", i),
                            new XAttribute("Value", record.SlotValues[i]),
                            new XAttribute("Count", record.SlotCounts[i])));
                    player.Add(inventory);
                    var clothes = new XElement("Clothes");
                    int[][] clothesValues = record.Clothes ?? CreateEmptyClothes();
                    for (int i = 0; i < 4; i++)
                        clothes.Add(new XElement("Slot", new XAttribute("Index", i),
                            new XAttribute("Values", FormatIntArray(
                                i < clothesValues.Length ? clothesValues[i] : null))));
                    player.Add(clothes);
                    root.Add(player);
                }
                string path = Storage.CombinePaths(m_playerRecordsWorldDirectory, PlayerRecordsFileName);
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Create))
                    new XDocument(root).Save(stream);
                m_playerRecordsDirty = false;
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Failed to save network player records: {ex.Message}");
            }
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.Save
        // Source: Survivalcraft/Game/ComponentClothing.cs:ComponentClothing.Save
        private static NetworkPlayerRecord CapturePlayerRecord(PlayerData playerData)
        {
            ComponentPlayer player = playerData?.ComponentPlayer;
            var record = new NetworkPlayerRecord
            {
                Name = playerData?.Name ?? "Player",
                PlayerClass = playerData?.PlayerClass ?? PlayerClass.Male,
                SkinName = playerData?.CharacterSkinName ?? string.Empty,
                Position = player?.ComponentBody.Position ?? playerData?.SpawnPosition ?? Vector3.Zero,
                Level = playerData?.Level ?? 1f,
                Health = player?.ComponentHealth?.Health ?? 1f,
                HasReceivedInitialItems = true,
                Clothes = CaptureClothes(player)
            };
            IInventory inventory = player?.ComponentMiner?.Inventory;
            if (inventory != null)
            {
                record.SlotValues = new int[inventory.SlotsCount];
                record.SlotCounts = new int[inventory.SlotsCount];
                for (int i = 0; i < inventory.SlotsCount; i++)
                {
                    record.SlotValues[i] = inventory.GetSlotValue(i);
                    record.SlotCounts[i] = inventory.GetSlotCount(i);
                }
            }
            return record;
        }

        private static int[][] CaptureClothes(ComponentPlayer player)
        {
            int[][] result = CreateEmptyClothes();
            ComponentClothing clothing = player?.Entity.FindComponent<ComponentClothing>();
            if (clothing == null) return result;
            for (int i = 0; i < result.Length; i++)
                result[i] = clothing.GetClothes((ClothingSlot)i).ToArray();
            return result;
        }

        private static void ApplyClothes(ComponentPlayer player, int[][] clothes)
        {
            if (player == null || clothes == null) return;
            ComponentClothing clothing = player.Entity.FindComponent<ComponentClothing>();
            if (clothing == null) return;
            for (int i = 0; i < Math.Min(4, clothes.Length); i++)
                clothing.SetClothes((ClothingSlot)i, clothes[i] ?? Array.Empty<int>());
        }

        private static int[][] CreateEmptyClothes() =>
            new[] { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };

        private static string FormatFloat(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static float ParseFloat(string value, float fallback = 0f) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
                ? result : fallback;

        private static PlayerClass ParsePlayerClass(string value) =>
            Enum.TryParse(value, true, out PlayerClass result) ? result : PlayerClass.Male;

        private static bool ParseBool(string value, bool fallback) =>
            bool.TryParse(value, out bool result) ? result : fallback;

        private static string FormatIntArray(int[] values) =>
            values == null || values.Length == 0 ? string.Empty : string.Join(";", values);

        private static int[] ParseIntArray(string values)
        {
            if (string.IsNullOrWhiteSpace(values)) return Array.Empty<int>();
            return values.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int result) ? result : 0).ToArray();
        }

        private void RefreshHostPlayerRecords()
        {
            if (!IsHost) return;
            EnsurePlayerRecordsLoaded();
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData.ToArray())
            {
                if (!m_clientRecordKeys.TryGetValue(item.Key, out string recordKey) ||
                    item.Value?.ComponentPlayer == null) continue;
                m_playerRecords[recordKey] = CapturePlayerRecord(item.Value);
                m_playerRecordsDirty = true;
            }
        }

        // Source: Survivalcraft/Game/ComponentClothing.cs:ComponentClothing.GetClothes
        private void SynchronizePlayerProfiles()
        {
            Project project = GameManager.Project;
            if (client?.IsConnected != true || project == null) return;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null) return;

            if (IsHost)
            {
                ComponentPlayer hostPlayer = players.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
                if (hostPlayer != null)
                    NetworkMessageSender.SendPlayerProfileMessage(
                        client.ClientID, CapturePlayerRecord(hostPlayer.PlayerData));
                foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData.ToArray())
                {
                    if (item.Value?.ComponentPlayer != null)
                        NetworkMessageSender.SendPlayerProfileMessage(
                            item.Key, CapturePlayerRecord(item.Value));
                }
            }
            else
            {
                ComponentPlayer localPlayer = players.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData));
                if (localPlayer != null)
                    NetworkMessageSender.SendPlayerProfileMessage(
                        client.ClientID, CapturePlayerRecord(localPlayer.PlayerData));
            }
        }

        private void HandlePlayerProfileMessage(PlayerProfileMessage message, int sourceClientId)
        {
            if (message == null) return;
            if (IsHost)
            {
                if (sourceClientId <= 0 || message.ClientId != sourceClientId ||
                    !m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData) ||
                    playerData?.ComponentPlayer == null || playerData.PlayerClass != message.PlayerClass)
                    return;
                if (PlayerData.VerifyName((message.Name ?? string.Empty).Trim()))
                    playerData.Name = message.Name.Trim();
                if (IsSkinValidForClass(message.SkinName, playerData.PlayerClass))
                    playerData.CharacterSkinName = message.SkinName;
                ApplyClothes(playerData.ComponentPlayer, message.Clothes);
                if (m_clientRecordKeys.TryGetValue(sourceClientId, out string recordKey))
                {
                    m_playerRecords[recordKey] = CapturePlayerRecord(playerData);
                    m_playerRecordsDirty = true;
                }
                return;
            }

            if (sourceClientId != 0) return;
            if (message.ClientId == client.ClientID)
            {
                ApplyProfileToLocalPlayer(message);
                return;
            }

            string networkKey = GetNetworkRecordKey(message.ClientId);
            NetworkPlayerRecord record = m_playerRecords.TryGetValue(networkKey, out NetworkPlayerRecord existing)
                ? existing : new NetworkPlayerRecord();
            record.Name = message.Name;
            record.PlayerClass = message.PlayerClass;
            record.SkinName = message.SkinName;
            record.Clothes = message.Clothes;

            if (m_networkPlayerData.TryGetValue(message.ClientId, out PlayerData remotePlayer) &&
                remotePlayer.PlayerClass != record.PlayerClass)
            {
                RemoveNetworkPlayer(message.ClientId);
                m_playerRecords[networkKey] = record;
                CreateNetworkPlayer(message.ClientId, record.Name, networkKey);
                return;
            }

            m_playerRecords[networkKey] = record;
            if (remotePlayer?.ComponentPlayer != null)
            {
                remotePlayer.Name = record.Name;
                remotePlayer.CharacterSkinName = record.SkinName;
                ApplyClothes(remotePlayer.ComponentPlayer, record.Clothes);
            }
            else
            {
                CreateNetworkPlayer(message.ClientId, record.Name, networkKey);
            }
        }

        private static bool IsSkinValidForClass(string skinName, PlayerClass playerClass)
        {
            if (string.IsNullOrWhiteSpace(skinName)) return false;
            CharacterSkinsManager.UpdateCharacterSkinsList();
            if (!CharacterSkinsManager.CharacterSkinsNames.Contains(skinName)) return false;
            PlayerClass? skinClass = CharacterSkinsManager.GetPlayerClass(skinName);
            return !skinClass.HasValue || skinClass.Value == playerClass;
        }

        private void ApplyProfileToLocalPlayer(PlayerProfileMessage message)
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            if (localPlayer == null || localPlayer.PlayerData.PlayerClass != message.PlayerClass) return;
            if (PlayerData.VerifyName((message.Name ?? string.Empty).Trim()))
                localPlayer.PlayerData.Name = message.Name.Trim();
            if (IsSkinValidForClass(message.SkinName, message.PlayerClass))
                localPlayer.PlayerData.CharacterSkinName = message.SkinName;
            ApplyClothes(localPlayer, message.Clothes);
        }

        private void EnsureLocalPlayerRecordApplied()
        {
            if (IsHost || m_pendingLocalPlayerRecord == null || GameManager.Project == null) return;
            if (m_localReplacementPlayerData == null)
            {
                if (m_localPlayerRecordQueued) return;
                m_localPlayerRecordQueued = true;
                QueueEndOfFrameAction(ReplaceLocalPlayerData);
                return;
            }
            if (m_localPlayerRecordApplied || m_localReplacementPlayerData.ComponentPlayer == null) return;

            ComponentPlayer player = m_localReplacementPlayerData.ComponentPlayer;
            NetworkPlayerRecord record = m_pendingLocalPlayerRecord;
            player.ComponentBody.Position = record.Position;
            player.ComponentBody.Velocity = Vector3.Zero;
            ApplyInventory(player.ComponentMiner?.Inventory, record.SlotValues, record.SlotCounts);
            ApplyClothes(player, record.Clothes);
            if (player.ComponentHealth != null)
                ModManager.ModParentField.ModifyParentField(
                    player.ComponentHealth, "<Health>k__BackingField",
                    MathUtils.Saturate(record.Health), typeof(ComponentHealth));
            m_localPlayerRecordApplied = true;
        }

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.RemovePlayerData
        // Source: Survivalcraft/Game/PlayerScreen.cs:PlayerScreen.Update
        private void ReplaceLocalPlayerData()
        {
            m_localPlayerRecordQueued = false;
            Project project = GameManager.Project;
            SubsystemPlayers players = project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null || m_pendingLocalPlayerRecord == null) return;
            PlayerData current = players.PlayersData.FirstOrDefault(player =>
                !m_networkPlayerData.Values.Contains(player));
            if (current == null) return;

            int playerIndex = current.PlayerIndex;
            WidgetInputDevice inputDevice = current.InputDevice;
            NetworkPlayerRecord record = m_pendingLocalPlayerRecord;
            PlayerData replacement;
            m_replacingLocalPlayerData = true;
            try
            {
                players.RemovePlayerData(current);
                replacement = new PlayerData(project)
                {
                    Name = record.Name,
                    PlayerClass = record.PlayerClass,
                    CharacterSkinName = record.SkinName,
                    Level = record.Level,
                    InputDevice = inputDevice,
                    SpawnPosition = record.Position
                };
                ModManager.ModParentField.ModifyParentField(
                    players, "m_nextPlayerIndex", playerIndex, typeof(SubsystemPlayers));
                players.AddPlayerData(replacement);
            }
            finally
            {
                m_replacingLocalPlayerData = false;
            }
            m_localReplacementPlayerData = replacement;
            m_localPlayerRecordApplied = false;
        }

        private static void ApplyInventory(IInventory inventory, int[] values, int[] counts)
        {
            if (inventory == null || values == null || counts == null) return;
            int slotsCount = Math.Min(inventory.SlotsCount, Math.Min(values.Length, counts.Length));
            for (int i = 0; i < slotsCount; i++)
            {
                inventory.RemoveSlotItems(i, int.MaxValue);
                if (counts[i] > 0) inventory.AddSlotItems(i, values[i], counts[i]);
            }
        }

        public void HandleGamePlayerHealthMessage(GamePlayerHealthMessage msg, int clientID)
        {
            if (IsHost || clientID != 0 || msg == null) return;
            // msg.PlayerIndex = 发送方 ClientID, 写入 RemotePlayers
            int remoteClientId = msg.PlayerIndex;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer targetPlayer = remoteClientId == client.ClientID
                ? players?.ComponentPlayers.FirstOrDefault(player =>
                    !m_networkPlayerData.Values.Contains(player.PlayerData))
                : (m_networkPlayerData.TryGetValue(remoteClientId, out PlayerData remoteData)
                    ? remoteData.ComponentPlayer
                    : null);
            if (targetPlayer?.ComponentHealth != null)
                ModManager.ModParentField.ModifyParentField(
                    targetPlayer.ComponentHealth, "<Health>k__BackingField",
                    MathUtils.Saturate(msg.Health), typeof(ComponentHealth));
            if (remoteClientId == client.ClientID) return;

            NetworkPlayerState state;
            if (!RemotePlayers.TryGetValue(remoteClientId, out state))
            {
                state = new NetworkPlayerState { ClientID = remoteClientId };
                RemotePlayers[remoteClientId] = state;
            }

            state.Health = msg.Health;
            state.MaxHealth = msg.MaxHealth;
            state.IsDead = msg.IsDead;
            Log.Information($"[ScMP] Remote player {remoteClientId} health: {msg.Health}/{msg.MaxHealth} (dead={msg.IsDead})");
        }

        public void HandleGameKickPlayerMessage(GameKickPlayerMessage msg, int sourceClientID)
        {
            // 仅 Host 可以处理踢人
            if (client.ClientID != 0) return;

            int targetID = msg.TargetClientID;
            Log.Information($"[ScMP] Kick request: ClientID {targetID}, reason: {msg.Reason}");

            // 释放玩家映射
            playerMappingManager.ReleasePlayerIndex(targetID);

            // 通过 Drt 框架断开玩家
            // Comms.Drt 内部管理连接，我们通过 RefuseJoinGame 已经可以阻止加入
            // Peer 层的 DisconnectPeer 需要 PeerData 引用
            Log.Information($"[ScMP] Player {targetID} kicked");
        }

        public void HandleGameWorldInfoMessage(GameWorldInfoMessage1 msg)
        {
            Project project = GameManager.Project;
            if (project == null || IsHost) return;
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            var timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(true);
            // Source: Survivalcraft/Game/SubsystemTimeOfDay.cs:SubsystemTimeOfDay.TimeOfDay
            // TimeOfDay depends on both values. Synchronizing only the offset allows the imported
            // client clock to remain minutes away from the host clock.
            if (Math.Abs(gameInfo.TotalElapsedGameTime - msg.TotalElapsedGameTime) > 0.25)
            {
                ModManager.ModParentField.ModifyParentField(
                    gameInfo, "<TotalElapsedGameTime>k__BackingField",
                    msg.TotalElapsedGameTime, typeof(SubsystemGameInfo));
                ModManager.ModParentField.ModifyParentField(
                    gameInfo, "m_lastTotalElapsedGameTime",
                    (double?)msg.TotalElapsedGameTime, typeof(SubsystemGameInfo));
            }
            gameInfo.WorldSettings.TimeOfDayMode = msg.CurrentTimeMode;
            if (Math.Abs(timeOfDay.TimeOfDayOffset - msg.TimeOfDayOffset) > 0.0001)
                timeOfDay.TimeOfDayOffset = msg.TimeOfDayOffset;
            m_pendingWorldControlActions = WorldControlAction.None;
            m_remoteWeatherState = msg;
            ApplyRemoteWeatherState();
        }

        public void TrySendWorldControlRequest(ComponentPlayer componentPlayer, WorldControlAction actions)
        {
            if (actions == WorldControlAction.None || IsHost || client?.IsConnected != true ||
                componentPlayer == null || m_networkPlayerData.Values.Contains(componentPlayer.PlayerData))
                return;
            double now = Time.RealTime;
            WorldControlAction newActions = now < m_worldControlRequestDeadline
                ? actions & ~m_pendingWorldControlActions
                : actions;
            if (newActions == WorldControlAction.None) return;
            NetworkMessageSender.SendWorldControlRequest(newActions);
            m_pendingWorldControlActions |= newActions;
            m_worldControlRequestDeadline = now + 0.5;
        }

        private void HandleWorldControlRequest(WorldControlRequestMessage message, int sourceClientId)
        {
            // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.Update
            Project project = GameManager.Project;
            if (!IsHost || sourceClientId <= 0 || message == null || project == null ||
                !m_networkPlayerData.ContainsKey(sourceClientId))
                return;
            SubsystemGameInfo gameInfo = project.FindSubsystem<SubsystemGameInfo>(true);
            if (gameInfo.WorldSettings.GameMode != GameMode.Creative) return;

            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(true);
            SubsystemTimeOfDay timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(true);
            ComponentGui hostGui = project.FindSubsystem<SubsystemPlayers>(true).ComponentPlayers
                .FirstOrDefault(player => !m_networkPlayerData.Values.Contains(player.PlayerData))?.ComponentGui;

            if (message.Actions.HasFlag(WorldControlAction.Precipitation))
            {
                if (weather.IsPrecipitationStarted)
                {
                    weather.ManualPrecipitationEnd();
                    hostGui?.DisplaySmallMessage("Precipitation Off", Color.White, false, false);
                }
                else
                {
                    weather.ManualPrecipitationStart();
                    hostGui?.DisplaySmallMessage("Precipitation On", Color.White, false, false);
                }
            }
            if (message.Actions.HasFlag(WorldControlAction.Fog))
            {
                if (weather.IsFogStarted)
                {
                    weather.ManualFogEnd();
                    hostGui?.DisplaySmallMessage("Fog Off", Color.White, false, false);
                }
                else
                {
                    weather.ManualFogStart();
                    hostGui?.DisplaySmallMessage("Fog On", Color.White, false, false);
                }
            }
            if (message.Actions.HasFlag(WorldControlAction.TimeOfDay))
            {
                float dawn = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Middawn);
                float noon = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Midday);
                float dusk = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Middusk);
                float midnight = IntervalUtils.Interval(timeOfDay.TimeOfDay, timeOfDay.Midnight);
                float nearest = MathUtils.Min(dawn, noon, dusk, midnight);
                if (dawn == nearest)
                {
                    timeOfDay.TimeOfDayOffset += dawn;
                    hostGui?.DisplaySmallMessage("Dawn", Color.White, false, false);
                }
                else if (noon == nearest)
                {
                    timeOfDay.TimeOfDayOffset += noon;
                    hostGui?.DisplaySmallMessage("Noon", Color.White, false, false);
                }
                else if (dusk == nearest)
                {
                    timeOfDay.TimeOfDayOffset += dusk;
                    hostGui?.DisplaySmallMessage("Dusk", Color.White, false, false);
                }
                else
                {
                    timeOfDay.TimeOfDayOffset += midnight;
                    hostGui?.DisplaySmallMessage("Midnight", Color.White, false, false);
                }
            }
            SendGameWorldInfoMessage();
        }

        public void ApplyRemoteWeatherState()
        {
            GameWorldInfoMessage1 msg = m_remoteWeatherState;
            Project project = GameManager.Project;
            if (msg == null || project == null || IsHost) return;
            // Source: Survivalcraft/Game/SubsystemWeather.cs:SubsystemWeather.UpdatePrecipitation
            SubsystemWeather weather = project.FindSubsystem<SubsystemWeather>(true);
            if (weather.IsPrecipitationStarted != msg.IsPrecipitationStarted)
            {
                if (msg.IsPrecipitationStarted) weather.ManualPrecipitationStart();
                else weather.ManualPrecipitationEnd();
            }
            if (weather.IsFogStarted != msg.IsFogStarted)
            {
                if (msg.IsFogStarted) weather.ManualFogStart();
                else weather.ManualFogEnd();
            }
            ModManager.ModParentField.ModifyParentField(
                weather, "<PrecipitationIntensity>k__BackingField", msg.PrecipitationIntensity, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogProgress>k__BackingField", msg.FogProgress, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogIntensity>k__BackingField", msg.FogIntensity, typeof(SubsystemWeather));
            ModManager.ModParentField.ModifyParentField(
                weather, "<FogSeed>k__BackingField", msg.FogSeed, typeof(SubsystemWeather));

            SubsystemSky sky = project.FindSubsystem<SubsystemSky>(true);
            if (msg.HasLightningStrike && !m_remoteLightningActive)
            {
                sky.MakeLightningStrike(msg.LightningStrikePosition, manual: true);
                m_remoteLightningActive = true;
            }
            else if (!msg.HasLightningStrike)
            {
                m_remoteLightningActive = false;
            }
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        public void PublishTerrainChanges(Dictionary<Point3, bool> modifiedCells)
        {
            if (client?.IsConnected != true || modifiedCells == null || modifiedCells.Count == 0)
                return;
            // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
            // Client terrain is prediction. The host executes the same remote input through the
            // original ComponentMiner and is the only source of authoritative terrain changes.
            if (!IsHost) return;
            var message = new GameModifiedCellsMessage(modifiedCells, client.Step);
            if (IsHost) RecordHostTerrainChanges(message, client.Step);
            client.SendInput(Message.WriteWithSender(message, client.Address));
        }

        public void HandleGameModifiedCellsMessage(GameModifiedCellsMessage msg, int sourceClientId)
        {
            // Source: SuSubsystemTerrain.cs - 接收远程方块修改
            if (msg == null || (msg.TargetClientId >= 0 && msg.TargetClientId != client.ClientID))
                return;
            if (IsHost && sourceClientId != 0)
            {
                ApplyClientTerrainDestruction(msg, sourceClientId);
                RecordHostTerrainChanges(msg, client.Step);
            }
            SuSubsystemTerrain.EnqueueNetworkBatch(msg);
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.DestroyCell
        // A client sends final terrain values. Replaying them only through ChangeCell omits the
        // host-side drop generation that normally happens in DestroyCell.
        private void ApplyClientTerrainDestruction(GameModifiedCellsMessage message, int sourceClientId)
        {
            Project project = GameManager.Project;
            if (message?.ModifiedCells == null || message.CellValues == null || project == null ||
                !m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData))
                return;
            ComponentPlayer player = playerData?.ComponentPlayer;
            ComponentMiner miner = player?.ComponentMiner;
            ComponentBody playerBody = player?.ComponentBody;
            if (miner == null || playerBody == null) return;

            int toolContents = Terrain.ExtractContents(miner.ActiveBlockValue);
            int toolLevel = BlocksManager.Blocks[toolContents].ToolLevel;
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(true);
            int index = 0;
            foreach (KeyValuePair<Point3, bool> item in message.ModifiedCells)
            {
                if (index >= message.CellValues.Count) break;
                int newValue = message.CellValues[index++];
                int oldValue = terrain.Terrain.GetCellValue(item.Key.X, item.Key.Y, item.Key.Z);
                int oldContents = Terrain.ExtractContents(oldValue);
                int newContents = Terrain.ExtractContents(newValue);
                Vector3 cellCenter = new Vector3(item.Key.X + 0.5f, item.Key.Y + 0.5f, item.Key.Z + 0.5f);
                if (oldContents != 0 && oldContents != newContents &&
                    Vector3.DistanceSquared(playerBody.Position, cellCenter) <= 36f)
                {
                    terrain.DestroyCell(toolLevel, item.Key.X, item.Key.Y, item.Key.Z,
                        newValue, false, false);
                }
            }
        }

        private void RecordHostTerrainChanges(GameModifiedCellsMessage message, int authoritativeTick)
        {
            if (message?.ModifiedCells == null) return;
            lock (m_terrainJournalLock)
            {
                int index = 0;
                foreach (KeyValuePair<Point3, bool> item in message.ModifiedCells)
                {
                    if (message.CellValues != null && index < message.CellValues.Count)
                    {
                        m_pendingTerrainChanges[item.Key] = new TerrainCellState
                        {
                            IsModified = item.Value,
                            CellValue = message.CellValues[index],
                            Tick = authoritativeTick
                        };
                        m_terrainRepairRepeats[item.Key] = TerrainRepairRepeatCount;
                    }
                    index++;
                }
            }
        }

        private void MergePendingTerrainChanges()
        {
            lock (m_terrainJournalLock)
            {
                foreach (KeyValuePair<Point3, TerrainCellState> item in m_pendingTerrainChanges)
                    m_terrainCheckpoint[item.Key] = item.Value;
                m_pendingTerrainChanges.Clear();
            }
        }

        // Source: Comms/Drt/GameStepData.Inputs
        // Live terrain deltas are sent once. Repeat each authoritative final value a bounded
        // number of times so a lost UDP input cannot leave one cell permanently divergent.
        private void BroadcastTerrainRepairs()
        {
            List<KeyValuePair<Point3, TerrainCellState>> repairs;
            lock (m_terrainJournalLock)
            {
                repairs = new List<KeyValuePair<Point3, TerrainCellState>>(m_terrainRepairRepeats.Count);
                foreach (Point3 point in m_terrainRepairRepeats.Keys.ToArray())
                {
                    TerrainCellState state;
                    if (!m_pendingTerrainChanges.TryGetValue(point, out state) &&
                        !m_terrainCheckpoint.TryGetValue(point, out state))
                    {
                        m_terrainRepairRepeats.Remove(point);
                        continue;
                    }
                    repairs.Add(new KeyValuePair<Point3, TerrainCellState>(point, state));
                    int repeats = m_terrainRepairRepeats[point] - 1;
                    if (repeats > 0) m_terrainRepairRepeats[point] = repeats;
                    else m_terrainRepairRepeats.Remove(point);
                }
            }
            if (repairs.Count == 0) return;

            int authoritativeTick = client.Step;
            for (int offset = 0; offset < repairs.Count; offset += TerrainCatchUpBatchSize)
            {
                var cells = new Dictionary<Point3, bool>();
                var values = new List<int>();
                int count = Math.Min(TerrainCatchUpBatchSize, repairs.Count - offset);
                for (int i = 0; i < count; i++)
                {
                    KeyValuePair<Point3, TerrainCellState> item = repairs[offset + i];
                    cells[item.Key] = item.Value.IsModified;
                    values.Add(item.Value.CellValue);
                }
                client.SendInput(Message.WriteWithSender(new GameModifiedCellsMessage(
                    cells, values, authoritativeTick, true, -1), client.Address));
            }
        }

        // Source: Comms/Drt/Client.cs:Client.GameJoined
        // The base world is the room-creation snapshot. This targeted checkpoint advances a
        // rejoining client from that snapshot to the host's current authoritative terrain tick.
        private void SendTerrainCatchUp(int targetClientId)
        {
            List<KeyValuePair<Point3, TerrainCellState>> snapshot;
            int targetTick = client.Step;
            lock (m_terrainJournalLock)
            {
                foreach (KeyValuePair<Point3, TerrainCellState> item in m_pendingTerrainChanges)
                    m_terrainCheckpoint[item.Key] = item.Value;
                m_pendingTerrainChanges.Clear();
                snapshot = m_terrainCheckpoint
                    .OrderBy(item => item.Value.Tick)
                    .ThenBy(item => item.Key.X)
                    .ThenBy(item => item.Key.Y)
                    .ThenBy(item => item.Key.Z)
                    .ToList();
            }

            if (snapshot.Count == 0)
            {
                var marker = new GameModifiedCellsMessage(
                    new Dictionary<Point3, bool>(), new List<int>(), targetTick, true, targetClientId);
                client.SendInput(Message.WriteWithSender(marker, client.Address));
                return;
            }

            for (int offset = 0; offset < snapshot.Count; offset += TerrainCatchUpBatchSize)
            {
                var cells = new Dictionary<Point3, bool>();
                var values = new List<int>();
                int count = Math.Min(TerrainCatchUpBatchSize, snapshot.Count - offset);
                for (int i = 0; i < count; i++)
                {
                    KeyValuePair<Point3, TerrainCellState> item = snapshot[offset + i];
                    cells[item.Key] = item.Value.IsModified;
                    values.Add(item.Value.CellValue);
                }
                var message = new GameModifiedCellsMessage(
                    cells, values, targetTick, true, targetClientId);
                client.SendInput(Message.WriteWithSender(message, client.Address));
            }
            Log.Information($"[ScMP] Sent terrain catch-up: ClientID={targetClientId}, Tick={targetTick}, Cells={snapshot.Count}");
        }

        public void HandleGamePakWorldMessage(GamePakWorldMessage msg)
        {
            if (msg == null || (msg.TargetClientId >= 0 && msg.TargetClientId != client.ClientID))
                return;
            m_sessionRandomSeed = msg.RandomSeed;
            m_pendingRandomStates = msg.RandomStates ?? new Dictionary<string, long>();
            m_randomStateAppliedProject = null;
            m_pendingLocalPlayerRecord = new NetworkPlayerRecord
            {
                Name = msg.PlayerName,
                PlayerClass = msg.PlayerClass,
                SkinName = msg.SkinName,
                Position = msg.PlayerPosition,
                Level = msg.PlayerLevel,
                Health = msg.PlayerHealth,
                SlotValues = msg.SlotValues,
                SlotCounts = msg.SlotCounts,
                Clothes = msg.Clothes
            };
            m_localReplacementPlayerData = null;
            m_localPlayerRecordQueued = false;
            m_localPlayerRecordApplied = false;
            try
            {
                Log.Information($"[ScMP] Importing world: {msg.Name} ({msg.WorldData.Length} bytes)");
                // Source: Survivalcraft/Game/WorldsManager.cs:WorldsManager.ImportWorld
                var existingDirectories = new HashSet<string>(WorldsManager.WorldInfos.Select(world => world.DirectoryName));
                string importedDirectory = WorldsManager.ImportWorld(new MemoryStream(msg.WorldData));
                m_downloadedWorldDirectory = importedDirectory;
                RegisterDownloadedWorld(importedDirectory);
                WorldsManager.UpdateWorldsList();

                WorldInfo importedWorld = WorldsManager.WorldInfos.FirstOrDefault(world =>
                    world.DirectoryName == importedDirectory);
                if (importedWorld == null)
                    importedWorld = WorldsManager.WorldInfos.FirstOrDefault(world =>
                        !existingDirectories.Contains(world.DirectoryName) &&
                        world.WorldSettings.Name == msg.Name);
                if (importedWorld == null)
                    importedWorld = WorldsManager.WorldInfos.FirstOrDefault(world =>
                        world.WorldSettings.Name == msg.Name && world.LastSaveTime == msg.LastSaveTime);
                if (importedWorld != null)
                {
                    SuPlayScreen.Play(importedWorld);
                    connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Playing);
                    m_shouldCreateHostAvatar = true;
                    m_pendingNetworkPlayers[0] = "Host";
                    m_pendingNetworkPlayerIdentities[0] = GetNetworkRecordKey(0);
                    Log.Information($"[ScMP] World imported, entering game: {importedWorld.DirectoryName}");
                    return;
                }
                Log.Error($"[ScMP] World imported but not found in world list: {msg.Name}");
                m_isLoadingDownloadedWorld = false;
            }
            catch (Exception ex)
            {
                m_isLoadingDownloadedWorld = false;
                Log.Error($"[ScMP] Failed to import world: {ex.Message}");
            }
        }

        // ====================================================================
        // Client 事件回调
        // ====================================================================
        private void Client_GameCreated(GameCreatedData obj)
        {
            Log.Information($"[ScMP] GameCreated, ClientID={client.ClientID}, Creator={obj.CreatorAddress}");
            IsHost = true;
            m_shouldCreateHostAvatar = false;
            Project registeredProject = m_registeredProject;
            ResetTransientNetworkState();
            m_sessionRandomSeed = Guid.NewGuid().GetHashCode();
            if (m_sessionRandomSeed == 0) m_sessionRandomSeed = 1;
            if (ReferenceEquals(registeredProject, GameManager.Project))
                m_registeredProject = registeredProject;
            playerMappingManager.AssignPlayerIndex(client.ClientID);
            connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Playing);
        }

        private void Client_GameJoined(GameJoinedData obj)
        {
            Log.Information($"[ScMP] GameJoined, Step={obj.Step}, ClientID={client.ClientID}");
            IsHost = false;
            m_hostDisconnectHandled = false;
            m_localLeaveInProgress = false;
            m_isLoadingDownloadedWorld = true;
            ResetTransientNetworkState();
            m_pendingJoinRequest = null;
            playerMappingManager.AssignPlayerIndex(client.ClientID);
            downloadSM.TransitionTo(WorldDownloadStateMachine.DownloadState.Requesting);
        }

        public static string GetLocalPlayerName()
        {
            string name = UserManager.ActiveUser?.DisplayName;
            if (!string.IsNullOrWhiteSpace(name)) return name;
            PlayerData player = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false)?.PlayersData.FirstOrDefault();
            return !string.IsNullOrWhiteSpace(player?.Name) ? player.Name : "Player";
        }

        // Source: Survivalcraft/Game/UserManager.cs:UserManager.UserManager
        // UniqueId is generated once and persisted in data:/UserId.dat on each installation.
        public static string GetLocalPlayerIdentity()
        {
            return UserManager.ActiveUser?.UniqueId ?? string.Empty;
        }

        private void Client_GameDescriptionRequest(GameDescriptionRequestData obj)
        {
            // Source: Comms.Drt Explorer queries server → server fires this on client
            // Client must respond via SendGameDescription for game to appear in DiscoveredServers
            if (LastGameDescription != null && LastGameDescription.Length > 0)
            {
                try
                {
                    client.SendGameDescription(LastGameDescription);
                    Log.Information($"[ScMP] GameDescription sent ({LastGameDescription.Length} bytes)");
                }
                catch (Exception ex)
                {
                    Log.Error($"[ScMP] SendGameDescription failed: {ex.Message}");
                }
            }
        }

        private void Client_ConnectRefused(ConnectRefusedData obj)
        {
            Log.Information($"[ScMP] Connect refused: {obj.Reason}");
            m_isLoadingDownloadedWorld = false;
            connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Disconnected);
            if (obj.Reason == PlayerProfileRequiredReason && m_pendingJoinRequest != null)
            {
                Dispatcher.Dispatch(() => ScreensManager.SwitchScreen(
                    "ScMultiplayerPlayer",
                    new Action<string, PlayerClass, string>((name, playerClass, skinName) =>
                        SubmitPendingJoin(name, playerClass, skinName, hasPlayerProfile: true))));
                return;
            }
            m_pendingJoinRequest = null;
        }

        private void Client_GameStateRequest(GameStateRequestData obj)
        {
            client.SendState(client.Step,
                Message.WriteWithSender(new ChatMessage("StateSync", string.Empty, "OK"), client.Address));
        }

        private void Client_Error(Exception obj)
        {
            Log.Error($"[ScMP] Client error: {obj.Message}");
            // Source: Comms/Peer.cs:Peer.CheckKeepAlives
            // Error is raised before all public connection state is guaranteed to be updated.
            if (!IsHost && (m_isLoadingDownloadedWorld ||
                !string.IsNullOrEmpty(m_downloadedWorldDirectory) || m_shouldCreateHostAvatar))
                HandleHostDisconnected();
        }

        private void HandleHostDisconnected()
        {
            if (IsHost || m_hostDisconnectHandled) return;
            m_hostDisconnectHandled = true;
            m_isLoadingDownloadedWorld = false;
            QueueEndOfFrameAction(delegate
            {
                string downloadedDirectory = m_downloadedWorldDirectory;
                if (GameManager.Project != null)
                    GameManager.DisposeProject();
                if (!string.IsNullOrEmpty(downloadedDirectory))
                {
                    WorldsManager.DeleteWorld(downloadedDirectory);
                    WorldsManager.UpdateWorldsList();
                    m_downloadedWorldDirectory = null;
                }
                m_networkPlayerData.Clear();
                m_pendingNetworkPlayers.Clear();
                RemotePlayers.Clear();
                ScreensManager.SwitchScreen("Play");
                DialogsManager.ShowDialog(null, new MessageDialog(
                    "Host Disconnected", "The host left the room. The downloaded world was removed.",
                    "OK", "Cancel", null));
            });
        }

        public void NotifyPlayerComponentDisposing(PlayerData playerData)
        {
            if (playerData == null || IsHost || m_hostDisconnectHandled || m_localLeaveInProgress ||
                m_replacingLocalPlayerData) return;
            if (m_networkPlayerData.Values.Contains(playerData)) return;

            m_localLeaveInProgress = true;
            m_shouldCreateHostAvatar = false;
            m_isLoadingDownloadedWorld = false;
            if (client != null && client.IsConnected)
            {
                try
                {
                    client.LeaveGame();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ScMP] Failed to leave game: {ex.Message}");
                }
            }

            ResetTransientNetworkState();
        }

        private void ResetTransientNetworkState()
        {
            m_networkPlayerData.Clear();
            lock (m_creatingNetworkPlayers) m_creatingNetworkPlayers.Clear();
            m_pendingNetworkPlayers.Clear();
            m_pendingNetworkPlayerIdentities.Clear();
            m_networkPlayerInputs.Clear();
            m_clientRecordKeys.Clear();
            m_playerHealthCache.Clear();
            RemotePlayers.Clear();
            m_hostAnimalIds.Clear();
            m_hostAnimals.Clear();
            m_hostAnimalSync.Clear();
            m_remoteAnimals.Clear();
            m_remoteAnimalTemplates.Clear();
            m_remoteAnimalSync.Clear();
            m_hostPickableIds.Clear();
            m_remotePickables.Clear();
            m_remotePickableRecords.Clear();
            lock (m_terrainJournalLock)
            {
                m_terrainCheckpoint.Clear();
                m_pendingTerrainChanges.Clear();
                m_terrainRepairRepeats.Clear();
            }
            m_terrainMergeTime = 0f;
            m_terrainRepairTime = 0f;
            SuSubsystemTerrain.ResetNetworkState();
            m_sessionRandomSeed = 0;
            m_pendingRandomStates.Clear();
            m_randomStateAppliedProject = null;
            m_nextAnimalId = 1;
            m_nextPickableId = 1;
            m_worldObjectsSyncTime = 0f;
            m_fullWorldObjectsSyncTime = 0f;
            m_networkTickAccumulator = 0f;
            m_lastNetworkUpdateTime = 0.0;
            m_worldInfoSyncTime = 0f;
            m_inventorySyncTime = 0f;
            m_playerProfileSyncTime = 0f;
            m_playerRecordSaveTime = 0f;
            m_localPlayerInput = default;
            m_localInputBodyPosition = Vector3.Zero;
            m_localInputBodyVelocity = Vector3.Zero;
            m_localInputBodyRotation = Quaternion.Identity;
            m_localInputLookAngles = Vector2.Zero;
            m_localInputSequence = 0;
            m_lastSentInputSequence = -1;
            m_localInputResendsRemaining = 0;
            m_smoothedNetworkDelay = 0f;
            m_clientWorldObjectsProject = null;
            m_remoteWeatherState = null;
            m_remoteLightningActive = false;
            m_pendingLocalPlayerRecord = null;
            m_localReplacementPlayerData = null;
            m_localPlayerRecordQueued = false;
            m_localPlayerRecordApplied = false;
            if (!IsHost)
            {
                m_playerRecords.Clear();
                m_playerRecordsWorldDirectory = null;
                m_playerRecordsDirty = false;
            }
            playerMappingManager.Reset();
            m_registeredProject = null;
        }

        // Source: Engine/Random.cs:Random.State
        // Subsystem random state is captured at join time. Component-level AI is still governed
        // by host-authoritative animal synchronization and is intentionally not reseeded here.
        private Dictionary<string, long> CaptureSubsystemRandomStates()
        {
            var states = new Dictionary<string, long>(StringComparer.Ordinal);
            Project project = GameManager.Project;
            if (project == null) return states;
            foreach (Subsystem subsystem in project.Subsystems)
            {
                foreach (FieldInfo field in GetSubsystemRandomFields(subsystem.GetType()))
                {
                    Engine.Random random = ModManager.ModParentField.GetParentField<Engine.Random>(
                        subsystem, field.Name, field.DeclaringType);
                    if (random != null)
                        states[GetRandomFieldKey(field)] = unchecked((long)random.State);
                }
            }
            return states;
        }

        private void ApplyHostRandomStates(Project project)
        {
            if (project == null || IsHost || m_sessionRandomSeed == 0 ||
                ReferenceEquals(m_randomStateAppliedProject, project))
                return;
            foreach (Subsystem subsystem in project.Subsystems)
            {
                foreach (FieldInfo field in GetSubsystemRandomFields(subsystem.GetType()))
                {
                    var random = new Engine.Random(DeriveRandomSeed(m_sessionRandomSeed, GetRandomFieldKey(field)));
                    if (m_pendingRandomStates.TryGetValue(GetRandomFieldKey(field), out long state))
                        random.State = unchecked((ulong)state);
                    ModManager.ModParentField.ModifyParentField(
                        subsystem, field.Name, random, field.DeclaringType);
                }
            }
            m_randomStateAppliedProject = project;
        }

        private static IEnumerable<FieldInfo> GetSubsystemRandomFields(Type type)
        {
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!field.IsInitOnly && field.FieldType == typeof(Engine.Random))
                        yield return field;
                }
            }
        }

        private static string GetRandomFieldKey(FieldInfo field) =>
            field.DeclaringType.FullName + "|" + field.Name;

        private static int DeriveRandomSeed(int seed, string key)
        {
            int hash = seed;
            foreach (char c in key)
                hash = unchecked(hash * 31 + c);
            return hash;
        }

        private void EnsureNetworkComponentPlayers()
        {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null || m_networkPlayerData.Count == 0) return;
            List<ComponentPlayer> componentPlayers = ModManager.ModParentField.GetParentField<List<ComponentPlayer>>(
                players, "m_componentPlayers", typeof(SubsystemPlayers));
            foreach (PlayerData playerData in m_networkPlayerData.Values)
            {
                if (playerData.ComponentPlayer != null && !componentPlayers.Contains(playerData.ComponentPlayer))
                    componentPlayers.Add(playerData.ComponentPlayer);
            }
        }

        private void HandleProjectDisposed(Project project)
        {
            foreach (int clientId in m_networkPlayerData
                .Where(pair => pair.Value.ComponentPlayer?.Entity.Project == project)
                .Select(pair => pair.Key).ToArray())
            {
                RemoveNetworkPlayer(clientId);
            }
            m_registeredProject = null;
        }

        private static HashSet<string> ReadDownloadedWorldRegistry()
        {
            if (!Storage.FileExists(DownloadedWorldsRegistryPath))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(Storage.ReadAllText(DownloadedWorldsRegistryPath)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
        }

        private static void WriteDownloadedWorldRegistry(HashSet<string> directories)
        {
            if (directories.Count == 0)
            {
                if (Storage.FileExists(DownloadedWorldsRegistryPath))
                    Storage.DeleteFile(DownloadedWorldsRegistryPath);
                return;
            }
            Storage.WriteAllText(DownloadedWorldsRegistryPath, string.Join("\n", directories));
        }

        private static void RegisterDownloadedWorld(string directoryName)
        {
            HashSet<string> directories = ReadDownloadedWorldRegistry();
            directories.Add(directoryName);
            WriteDownloadedWorldRegistry(directories);
        }

        private void CleanupDownloadedWorldsIfIdle()
        {
            if (GameManager.Project != null || m_isLoadingDownloadedWorld) return;
            HashSet<string> directories = ReadDownloadedWorldRegistry();
            if (directories.Count == 0) return;
            var failedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directoryName in directories)
            {
                try
                {
                    WorldsManager.DeleteWorld(directoryName);
                    if (string.Equals(m_downloadedWorldDirectory, directoryName, StringComparison.OrdinalIgnoreCase))
                        m_downloadedWorldDirectory = null;
                }
                catch (Exception ex)
                {
                    Log.Error($"[ScMP] Failed to delete downloaded world {directoryName}: {ex.Message}");
                    failedDirectories.Add(directoryName);
                    continue;
                }
            }
            WriteDownloadedWorldRegistry(failedDirectories);
            WorldsManager.UpdateWorldsList();
        }

        private void Server_Information(string obj)
        {
            Log.Information($"[Server] {obj}");
        }

        // Source: Comms/Drt/GameStepData.Inputs
        // Remote host/peer avatars are presentation replicas. Extrapolate the last authoritative
        // snapshot to the current server step, then interpolate the visible body toward it.
        private void UpdateRemotePlayerPresentations()
        {
            foreach (KeyValuePair<int, NetworkPlayerState> item in RemotePlayers.ToArray())
            {
                NetworkPlayerState state = item.Value;
                if (state == null || Time.RealTime - state.LastUpdateTime > 1.0 ||
                    !m_networkPlayerData.TryGetValue(item.Key, out PlayerData playerData) ||
                    playerData.ComponentPlayer?.ComponentBody == null)
                    continue;
                ComponentBody body = playerData.ComponentPlayer.ComponentBody;
                float delaySample = MathUtils.Clamp(
                    (client.Step - state.ServerTick) * ServerTickDuration, 0f, 0.35f);
                state.EstimatedDelay = state.EstimatedDelay <= 0f
                    ? delaySample
                    : MathUtils.Lerp(state.EstimatedDelay, delaySample, 0.12f);
                float extrapolationTime = MathUtils.Min(state.EstimatedDelay, 0.2f);
                Vector3 targetPosition = state.Position;
                targetPosition.X += state.Velocity.X * extrapolationTime;
                targetPosition.Z += state.Velocity.Z * extrapolationTime;
                if (!state.IsGrounded)
                {
                    targetPosition.Y += state.Velocity.Y * extrapolationTime -
                        4.9f * extrapolationTime * extrapolationTime;
                }
                float errorSquared = Vector3.DistanceSquared(body.Position, targetPosition);
                if (!state.PresentationInitialized || errorSquared > 64f)
                {
                    // Snap only to an authoritative sampled position. An extrapolated descending
                    // point can already be below terrain before the next grounded packet arrives.
                    body.Position = state.Position;
                    body.Rotation = state.Rotation;
                    body.Velocity = state.Velocity;
                    state.PresentationInitialized = true;
                }
                else
                {
                    float delayFactor = MathUtils.Saturate(state.EstimatedDelay / 0.2f);
                    Vector3 error = targetPosition - body.Position;
                    float deadZone = state.IsGrounded ? 0.4f : 0.65f;
                    Vector3 targetVelocity = state.Velocity;
                    if (state.IsGrounded) targetVelocity.Y = 0f;
                    Vector3 desiredVelocity;
                    float blend;
                    if (error.LengthSquared() <= deadZone * deadZone)
                    {
                        desiredVelocity = targetVelocity;
                        blend = 0.45f;
                    }
                    else
                    {
                        float horizon = MathUtils.Lerp(0.35f, 0.2f, delayFactor);
                        Vector3 catchUpVelocity = error / horizon;
                        float maxExtraSpeed = MathUtils.Lerp(3f, 8f, delayFactor);
                        float extraSpeed = catchUpVelocity.Length();
                        if (extraSpeed > maxExtraSpeed)
                            catchUpVelocity *= maxExtraSpeed / extraSpeed;
                        desiredVelocity = targetVelocity + catchUpVelocity;
                        blend = MathUtils.Lerp(0.2f, 0.35f, delayFactor);
                    }
                    body.Velocity = Vector3.Lerp(body.Velocity, desiredVelocity, blend);
                    body.Rotation = Quaternion.Slerp(body.Rotation, state.Rotation, 0.4f);
                }
            }
        }

        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.NormalMovement
        // Remote host avatars keep original locomotion, then receive a bounded velocity addition
        // that makes them converge on the client's continuous A-B-C trajectory without teleporting.
        private void ApplyHostRemoteFollowVelocities()
        {
            foreach (KeyValuePair<int, PlayerData> remote in m_networkPlayerData.ToArray())
            {
                if (remote.Key <= 0 || remote.Value?.ComponentPlayer == null ||
                    !m_networkPlayerInputs.TryGetValue(remote.Key, out NetworkPlayerInputState state) ||
                    Time.RealTime - state.LastReceivedTime > 0.25)
                    continue;
                ComponentPlayer player = remote.Value.ComponentPlayer;
                ComponentBody body = player.ComponentBody;
                ComponentLocomotion locomotion = player.ComponentLocomotion;
                float delay = MathUtils.Clamp(
                    (client.Step - state.ClientTick) * ServerTickDuration, 0f, 0.25f);
                Vector3 targetPosition = state.BodyPosition + state.BodyVelocity * delay;
                Vector3 error = targetPosition - body.Position;
                float trackingRadius = locomotion.IsCreativeFlyEnabled ? 32f : 16f;
                float errorLength = error.Length();
                if (errorLength > trackingRadius)
                    error *= trackingRadius / errorLength;

                bool isInteracting = state.Input.Dig.HasValue || state.Input.Hit.HasValue ||
                    state.Input.Interact.HasValue || state.Input.Aim.HasValue;
                float deadZone = locomotion.IsCreativeFlyEnabled ? 2f : 1.25f;
                Vector3 desiredVelocity;
                float blend;
                if (error.LengthSquared() <= deadZone * deadZone)
                {
                    // Once close enough, stop chasing position and match velocity. This brakes
                    // the extra catch-up speed before it crosses the target and reverses direction.
                    desiredVelocity = state.BodyVelocity;
                    blend = 0.45f;
                }
                else
                {
                    float delayFactor = MathUtils.Saturate(delay / 0.2f);
                    float horizon = MathUtils.Lerp(0.45f, 0.22f, delayFactor);
                    Vector3 catchUpVelocity = error / horizon;
                    float maxExtraSpeed = locomotion.IsCreativeFlyEnabled
                        ? MathUtils.Lerp(4f, 10f, delayFactor)
                        : MathUtils.Lerp(2f, 6f, delayFactor);
                    float extraSpeed = catchUpVelocity.Length();
                    if (extraSpeed > maxExtraSpeed)
                        catchUpVelocity *= maxExtraSpeed / extraSpeed;
                    desiredVelocity = state.BodyVelocity + catchUpVelocity;
                    blend = MathUtils.Lerp(0.14f, 0.28f, delayFactor);
                    if (isInteracting) blend = MathUtils.Max(blend, 0.35f);
                }
                body.Velocity = Vector3.Lerp(body.Velocity, desiredVelocity, blend);
            }
        }

        public void CaptureLocalPlayerInput(ComponentPlayer player, PlayerInput playerInput)
        {
            if (IsHost || client?.IsConnected != true || player == null ||
                m_networkPlayerData.Values.Contains(player.PlayerData))
                return;
            m_localPlayerInput = SanitizeNetworkPlayerInput(playerInput);
            m_localInputBodyPosition = player.ComponentBody.Position;
            m_localInputBodyVelocity = player.ComponentBody.Velocity;
            m_localInputBodyRotation = player.ComponentBody.Rotation;
            m_localInputLookAngles = player.ComponentLocomotion.LookAngles;
            m_localInputSequence = m_localInputSequence == int.MaxValue
                ? 1
                : m_localInputSequence + 1;
            m_localInputResendsRemaining = 3;
        }

        public bool TryGetNetworkPlayerInput(ComponentPlayer player, out PlayerInput playerInput)
        {
            playerInput = default;
            if (!IsHost || player == null) return false;
            int sourceClientId = m_networkPlayerData.FirstOrDefault(pair =>
                pair.Key > 0 && ReferenceEquals(pair.Value, player.PlayerData)).Key;
            if (sourceClientId <= 0) return false;
            if (!m_networkPlayerInputs.TryGetValue(sourceClientId, out NetworkPlayerInputState state) ||
                Time.RealTime - state.LastReceivedTime > 0.25)
                return true;

            if (state.ConsumedSequence != state.Sequence)
            {
                player.ComponentBody.Rotation = state.BodyRotation;
                ModManager.ModParentField.ModifyParentField(
                    player.ComponentLocomotion, "m_lookAngles",
                    state.LookAngles, typeof(ComponentLocomotion));
                playerInput = state.Input;
                state.ConsumedSequence = state.Sequence;
                state.HeldInput = CreateHeldNetworkInput(state.Input);
            }
            else
            {
                playerInput = state.HeldInput;
            }
            return true;
        }

        private void HandleGamePlayerInputMessage(GamePlayerInputMessage msg, int sourceClientId)
        {
            if (!IsHost || msg == null || sourceClientId <= 0 ||
                !m_networkPlayerData.ContainsKey(sourceClientId) ||
                (msg.PlayerIndex != 0 && msg.PlayerIndex != sourceClientId))
                return;
            if (!m_networkPlayerInputs.TryGetValue(sourceClientId, out NetworkPlayerInputState state))
            {
                state = new NetworkPlayerInputState();
                m_networkPlayerInputs[sourceClientId] = state;
            }
            if (!state.InitialPositionApplied &&
                m_networkPlayerData.TryGetValue(sourceClientId, out PlayerData playerData) &&
                playerData.ComponentPlayer?.ComponentBody != null)
            {
                ComponentBody body = playerData.ComponentPlayer.ComponentBody;
                if (Vector3.DistanceSquared(body.Position, msg.BodyPosition) <= 64f)
                    body.Position = msg.BodyPosition;
                state.InitialPositionApplied = true;
            }
            if (msg.Sequence <= state.Sequence) return;
            state.Input = SanitizeNetworkPlayerInput(msg.PlayerInput);
            state.BodyPosition = msg.BodyPosition;
            state.BodyVelocity = msg.BodyVelocity;
            state.ClientTick = msg.ClientTick;
            state.BodyRotation = msg.BodyRotation;
            state.LookAngles = msg.LookAngles;
            state.Sequence = msg.Sequence;
            state.LastReceivedTime = Time.RealTime;
        }

        private static PlayerInput SanitizeNetworkPlayerInput(PlayerInput input)
        {
            input.ToggleInventory = false;
            input.ToggleClothing = false;
            input.TakeScreenshot = false;
            input.SwitchCameraMode = false;
            input.TimeOfDay = false;
            input.Lighting = false;
            input.Precipitation = false;
            input.Fog = false;
            input.KeyboardHelp = false;
            input.GamepadHelp = false;
            return input;
        }

        private static PlayerInput CreateHeldNetworkInput(PlayerInput input)
        {
            input.Look = Vector2.Zero;
            input.CameraLook = Vector2.Zero;
            input.VrLook = null;
            input.ToggleCreativeFly = false;
            input.ToggleCrouch = false;
            input.ToggleMount = false;
            input.EditItem = false;
            input.Jump = false;
            input.ScrollInventory = 0;
            input.ToggleInventory = false;
            input.ToggleClothing = false;
            input.TakeScreenshot = false;
            input.SwitchCameraMode = false;
            input.TimeOfDay = false;
            input.Lighting = false;
            input.Precipitation = false;
            input.Fog = false;
            input.KeyboardHelp = false;
            input.GamepadHelp = false;
            input.Interact = null;
            input.PickBlockType = null;
            input.Drop = false;
            input.SelectInventorySlot = null;
            return input;
        }

        // ====================================================================
        // 异步注册 IUpdateable
        // ====================================================================
        private async void StartAsyncRegistration()
        {
            try
            {
                await WaitForProjectLoaded();
                EnsureUpdateRegistration();
            }
            catch (Exception ex)
            {
                Log.Error($"[ScMP] Async registration failed: {ex.Message}");
            }
        }

        private async Task WaitForProjectLoaded()
        {
            while (GameManager.Project == null)
                await Task.Delay(1000);
        }

        public void EnsureUpdateRegistration()
        {
            Project project = GameManager.Project;
            if (project == null) return;
            lock (m_updateRegistrationLock)
            {
                if (!ReferenceEquals(m_registeredProject, project))
                {
                    project.FindSubsystem<SubsystemUpdate>(true).AddUpdateable(this);
                    m_registeredProject = project;
                    if (!IsHost) m_isLoadingDownloadedWorld = false;
                    Log.Information("[ScMP] Registered multiplayer update for current project");
                }
            }

            ApplyHostRandomStates(project);
            foreach (var pending in m_pendingNetworkPlayers.ToArray())
            {
                m_pendingNetworkPlayerIdentities.TryGetValue(pending.Key, out string identity);
                CreateNetworkPlayer(pending.Key, pending.Value, identity);
            }
            if (!IsHost && m_shouldCreateHostAvatar && !m_networkPlayerData.ContainsKey(0))
                CreateNetworkPlayer(0, "Host", GetNetworkRecordKey(0));
        }

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
                    if (desc.Contains("zerotier") || desc.Contains("wireguard") ||
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
            try { client?.LeaveGame(); } catch { }
            try { server?.Dispose(); } catch { }
            try { explorer?.StopDiscovery(); } catch { }
        }
    }
}
