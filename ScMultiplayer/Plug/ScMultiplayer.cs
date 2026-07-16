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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
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
        public bool IsCrouching;
        public bool IsFlying;
        public bool IsRiding;
        public int ActiveSlotIndex;
        public int HandItemValue;
        public int HandItemCount;
        public Vector3 ItemOffset;
        public Vector3 ItemRotation;
        public float AimHandAngle;
        public float Health;
        public float MaxHealth = 1f;
        public bool IsDead;
        public double LastUpdateTime;
    }

    public class NetworkPlayerRecord
    {
        public string Name;
        public PlayerClass PlayerClass;
        public string SkinName;
        public Vector3 Position;
        public int[] SlotValues;
        public int[] SlotCounts;
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
            ScMultiplayer.currentInstance.HandleGameModifiedCellsMessage(message);
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
        public static void SendPlayerPositionMessage(int playerIndex, Vector3 position, Quaternion rotation,
            Vector3 velocity, Vector2 lookAngles, bool isCrouching, bool isFlying, bool isRiding,
            int activeSlotIndex, int handItemValue, int handItemCount,
            Vector3 itemOffset, Vector3 itemRotation, float aimHandAngle,
            int[] slotValues, int[] slotCounts)
        {
            var msg = new GamePlayerPositionMessage(playerIndex, position, rotation, velocity,
                lookAngles, isCrouching, isFlying, isRiding,
                activeSlotIndex, handItemValue, handItemCount,
                itemOffset, itemRotation, aimHandAngle, slotValues, slotCounts);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendChatMessage(string sender, string senderIdentity, string text)
        {
            var msg = new ChatMessage(sender, senderIdentity, text);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendWorldInfoMessage(double timeOfDayOffset, double totalElapsedGameTime,
            TimeOfDayMode currentTimeMode)
        {
            var msg = new GameWorldInfoMessage1(timeOfDayOffset, totalElapsedGameTime, currentTimeMode);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendModifiedCellsMessage(Dictionary<Point3, bool> modifiedCells)
        {
            var msg = new GameModifiedCellsMessage(modifiedCells);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendPakWorldMessage(string name, byte[] worldData, DateTime lastSaveTime)
        {
            var msg = new GamePakWorldMessage(name, worldData, lastSaveTime);
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
        public UpdateOrder UpdateOrder => UpdateOrder.Input;

        // ---------- 内部状态 ----------
        private float m_accumulatedTime = 0f;
        private Dictionary<int, float> m_playerHealthCache = new Dictionary<int, float>(); // clientID → last known health
        private readonly Dictionary<int, PlayerData> m_networkPlayerData = new Dictionary<int, PlayerData>();
        private readonly HashSet<int> m_creatingNetworkPlayers = new HashSet<int>();
        private readonly object m_updateRegistrationLock = new object();
        private readonly Dictionary<int, string> m_pendingNetworkPlayers = new Dictionary<int, string>();
        private readonly Dictionary<int, string> m_pendingNetworkPlayerIdentities = new Dictionary<int, string>();
        private readonly Dictionary<string, NetworkPlayerRecord> m_playerRecords = new Dictionary<string, NetworkPlayerRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string> m_clientRecordKeys = new Dictionary<int, string>();
        private const float HealthSyncInterval = 1.0f; // 每秒同步一次生命
        private Project m_registeredProject;
        private string m_downloadedWorldDirectory;
        private bool m_hostDisconnectHandled;
        private bool m_localLeaveInProgress;
        private bool m_shouldCreateHostAvatar;
        private bool m_isLoadingDownloadedWorld;
        private const string DownloadedWorldsRegistryPath = "data:/ScMultiplayerDownloadedWorlds.txt";
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
                CleanupDownloadedWorldsIfIdle();
                return args;
            }, EventPriority.LOWEST);

            CleanupDownloadedWorldsIfIdle();
            GameManager.ProjectDisposed += HandleProjectDisposed;

            // 初始化网络
            float tickDuration = 1f / 60f;
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

        private object[] HandleLoading(object[] args)
        {
            // Source: Survivalcraft/Game/Program.cs:Program.Initialize
            // Source: Survivalcraft/Game/LoadingManager.cs:LoadingManager.ReplaceItem
            if (!Game.LoadingManager.ReplaceItem("Initialize PlayScreen",
                () => ScreensManager.AddScreen("Play", new SuPlayScreen())))
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
            connectionSM.Update();
            downloadSM.Update();

            m_accumulatedTime += dt;

            if (m_accumulatedTime >= 1f / 30f) // 30fps
            {
                m_accumulatedTime = 0f;
                Trigger30FrameEvent(dt);
            }

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
                        IsHost = false;
                        if (client.IsConnected) client.LeaveGame();
                        var joinInfo = new GameWorldInfoMessage(
                            info.Name, info.Size, info.LastSaveTime, info.GameMode,
                            info.EnvironmentBehaviorMode, info.SerializationVersion, client.Address,
                            GetLocalPlayerName(), GetLocalPlayerIdentity());
                        client.JoinGame(game.ServerDescription.Address, game.GameID,
                            Message.WriteWithSender(joinInfo, client.Address),
                            client.ClientID.ToString());
                    }));
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
        private void Trigger30FrameEvent(float dt)
        {
            if (!client.IsConnected) return;

            SendGamePlayerPositionMessage();
            SendGameWorldInfoMessage(dt);
            SendGamePlayerHealthMessage(dt);
        }

        // ====================================================================
        // 发送: 玩家位置
        // ====================================================================
        private void SendGamePlayerPositionMessage()
        {
            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null) return;
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
                int[] slotValues = inventory != null ? new int[inventory.SlotsCount] : Array.Empty<int>();
                int[] slotCounts = inventory != null ? new int[inventory.SlotsCount] : Array.Empty<int>();
                if (inventory != null)
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

                NetworkMessageSender.SendPlayerPositionMessage(
                    senderClientId, item.ComponentBody.Position,
                    item.ComponentBody.Rotation, item.ComponentBody.Velocity, lookAngles,
                    isCrouching, isFlying, isRiding, activeSlot, handVal, handCnt,
                    itemOffset, itemRotation, aimHandAngle, slotValues, slotCounts);
            }
        }

        // ====================================================================
        // 发送: 世界信息 (仅Host)
        // ====================================================================
        private void SendGameWorldInfoMessage(float dt)
        {
            if (client.ClientID != 0) return;
            var gameInfo = GameManager.Project.FindSubsystem<SubsystemGameInfo>(true);
            var timeOfDay = GameManager.Project.FindSubsystem<SubsystemTimeOfDay>(true);
            NetworkMessageSender.SendWorldInfoMessage(
                timeOfDay.TimeOfDayOffset,
                gameInfo.TotalElapsedGameTime,
                gameInfo.WorldSettings.TimeOfDayMode);
        }

        // ====================================================================
        // 发送: 生命值 (周期性)
        // ====================================================================
        private void SendGamePlayerHealthMessage(float dt)
        {
            var subsystemPlayers = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (subsystemPlayers == null) return;
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
                        if (IsHost) CreateNetworkPlayer(item.ClientID, worldInfo.PlayerName, worldInfo.PlayerIdentity);
                        client.AcceptJoinGame(item.ClientID);
                        if (IsHost)
                        {
                            string playerName = string.IsNullOrWhiteSpace(worldInfo.PlayerName)
                                ? "Player " + item.ClientID
                                : worldInfo.PlayerName;
                            DialogsManager.ShowDialog(null, new MessageDialog(
                                "Player Joined", playerName + " joined the room.", "OK", "Cancel", null));
                        }
                        NetworkMessageSender.SendPakWorldMessage(
                            SuPlayScreen.WorldDataName, SuPlayScreen.WorldData, SuPlayScreen.WorldDataLastSaveTime);
                        Log.Information($"[ScMP] Sent cached world data ({SuPlayScreen.WorldData.Length} bytes) to ClientID {item.ClientID}");
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
                        HandleGamePlayerPositionMessage(pos, item.ClientID);
                        break;
                    case GameModifiedCellsMessage cells:
                        NetworkMessageHandler.HandleModifiedCellsMessage(cells, item.ClientID);
                        break;
                    case GameWorldInfoMessage1 worldInfo:
                        NetworkMessageHandler.HandleWorldInfoMessage(worldInfo, item.ClientID);
                        break;
                    case GamePakWorldMessage pakWorld:
                        NetworkMessageHandler.HandlePakWorldMessage(pakWorld, item.ClientID);
                        break;
                    case GamePlayerHealthMessage health:
                        NetworkMessageHandler.HandlePlayerHealthMessage(health, item.ClientID);
                        break;
                    case GameKickPlayerMessage kick:
                        HandleGameKickPlayerMessage(kick, item.ClientID);
                        break;
                    default:
                        Log.Error($"[ScMP] Unknown message type: {message.GetType().Name}");
                        break;
                }
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
            int remoteClientId = clientID;
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
            state.LookAngles = msg.LookAngles;
            state.IsCrouching = msg.IsCrouching;
            state.IsFlying = msg.IsFlying;
            state.IsRiding = msg.IsRiding;
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
                body.Position = msg.Position;
                body.Rotation = msg.Rotation;
                body.Velocity = msg.Velocity;
                IInventory inventory = playerData.ComponentPlayer.ComponentMiner?.Inventory;
                if (inventory != null && msg.SlotValues != null)
                {
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
                entity = DatabaseManager.CreateEntity(project, playerData.GetEntityTemplateName(), overrides, true);
                ComponentBody body = entity.FindComponent<ComponentBody>(true);
                body.Position = playerData.SpawnPosition + new Vector3(1f, 0f, 0f);
                project.AddEntity(entity);

                // Source: GameEntitySystem/Project.cs:Project.SaveEntities
                // Subsystems already received EntityAdded. Remove only from the persistence set;
                // runtime subsystem references remain active until RemoveNetworkPlayer fires removal events.
                Dictionary<Entity, bool> projectEntities = ModManager.ModParentField.GetParentField<Dictionary<Entity, bool>>(
                    project, "m_entities", typeof(Project));
                projectEntities.Remove(entity);

                IInventory inventory = playerData.ComponentPlayer?.ComponentMiner?.Inventory;
                if (record?.SlotValues != null && inventory != null)
                {
                    int slotsCount = Math.Min(inventory.SlotsCount, record.SlotValues.Length);
                    for (int i = 0; i < slotsCount; i++)
                    {
                        inventory.RemoveSlotItems(i, int.MaxValue);
                        if (record.SlotCounts[i] > 0)
                            inventory.AddSlotItems(i, record.SlotValues[i], record.SlotCounts[i]);
                    }
                }

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
            var record = new NetworkPlayerRecord
            {
                Name = playerData.Name,
                PlayerClass = playerData.PlayerClass,
                SkinName = playerData.CharacterSkinName,
                Position = playerData.ComponentPlayer?.ComponentBody.Position ?? playerData.SpawnPosition
            };
            IInventory inventory = playerData.ComponentPlayer?.ComponentMiner?.Inventory;
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
            m_playerRecords[recordKey] = record;
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
            m_pendingNetworkPlayers.Remove(clientId);
            m_pendingNetworkPlayerIdentities.Remove(clientId);
            m_clientRecordKeys.Remove(clientId);
        }

        public void HandleGamePlayerHealthMessage(GamePlayerHealthMessage msg, int clientID)
        {
            // msg.PlayerIndex = 发送方 ClientID, 写入 RemotePlayers
            int remoteClientId = msg.PlayerIndex;
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
            if (project == null) return;
            var timeOfDay = project.FindSubsystem<SubsystemTimeOfDay>(true);
            if (Math.Abs(timeOfDay.TimeOfDayOffset - msg.TimeOfDayOffset) > 0.02)
                timeOfDay.TimeOfDayOffset = msg.TimeOfDayOffset;
        }

        public void HandleGameModifiedCellsMessage(GameModifiedCellsMessage msg)
        {
            // Source: SuSubsystemTerrain.cs - 接收远程方块修改
            lock (SuSubsystemTerrain.ReModifiedCells)
            {
                SuSubsystemTerrain.CellValues = msg.CellValues;
                foreach (var kvp in msg.ModifiedCells)
                    SuSubsystemTerrain.ReModifiedCells[kvp.Key] = kvp.Value;
            }
        }

        public void HandleGamePakWorldMessage(GamePakWorldMessage msg)
        {
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
                    m_pendingNetworkPlayerIdentities[0] = "host";
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
        }

        private void Client_GameStateRequest(GameStateRequestData obj)
        {
            client.SendState(client.Step,
                Message.WriteWithSender(new ChatMessage("StateSync", string.Empty, "OK"), client.Address));
        }

        private void Client_Error(Exception obj)
        {
            Log.Error($"[ScMP] Client error: {obj.Message}");
            if (!IsHost && client != null && !client.IsConnected)
                HandleHostDisconnected();
        }

        private void HandleHostDisconnected()
        {
            if (IsHost || m_hostDisconnectHandled) return;
            m_hostDisconnectHandled = true;
            m_isLoadingDownloadedWorld = false;
            Dispatcher.Dispatch(delegate
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
            if (playerData == null || IsHost || m_hostDisconnectHandled || m_localLeaveInProgress) return;
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
            m_clientRecordKeys.Clear();
            m_playerHealthCache.Clear();
            RemotePlayers.Clear();
            playerMappingManager.Reset();
            m_registeredProject = null;
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

        private void HandleGamePlayerInputMessage(GamePlayerInputMessage msg)
        {
            var players = GameManager.Project.FindSubsystem<SubsystemPlayers>(true).ComponentPlayers;
            foreach (var item in players)
            {
                if (item.PlayerData.PlayerIndex == msg.PlayerIndex + 1)
                {
                    ModManager.ModParentField.ModifyParentField(
                        item.ComponentInput, "m_playerInput",
                        msg.PlayerInput, item.ComponentInput.GetType().BaseType);
                }
            }
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

            foreach (var pending in m_pendingNetworkPlayers.ToArray())
            {
                m_pendingNetworkPlayerIdentities.TryGetValue(pending.Key, out string identity);
                CreateNetworkPlayer(pending.Key, pending.Value, identity);
            }
            if (!IsHost && m_shouldCreateHostAvatar && !m_networkPlayerData.ContainsKey(0))
                CreateNetworkPlayer(0, "Host", "host");
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
