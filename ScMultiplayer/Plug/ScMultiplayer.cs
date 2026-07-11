using Comms;
using Comms.Drt;
using Engine;
using Engine.Graphics;
using Engine.Input;
using Engine.Media;
using Game;
using SuAPI;
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

    public class NetworkMessageHandler
    {
        public static void HandleChatMessage(ChatMessage message, int clientID)
        {
            Log.Information($"[Chat] Client{clientID} {message.Sender}: {message.Text}");
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
            Vector3 itemOffset, Vector3 itemRotation, float aimHandAngle)
        {
            var msg = new GamePlayerPositionMessage(playerIndex, position, rotation, velocity,
                lookAngles, isCrouching, isFlying, isRiding,
                activeSlotIndex, handItemValue, handItemCount,
                itemOffset, itemRotation, aimHandAngle);
            ScMultiplayer.client.SendInput(Message.WriteWithSender(msg, ScMultiplayer.client.Address));
        }

        public static void SendChatMessage(string sender, string text)
        {
            var msg = new ChatMessage(sender, text);
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
        public UpdateOrder UpdateOrder => UpdateOrder.Input;

        // ---------- 内部状态 ----------
        private float m_accumulatedTime = 0f;
        private Dictionary<int, float> m_playerHealthCache = new Dictionary<int, float>(); // clientID → last known health
        private const float HealthSyncInterval = 1.0f; // 每秒同步一次生命

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            currentInstance = this;

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
            // MAUI版: Loading.Initialize 事件触发时，加载项已在 LoadingManager 静态类中注册
            // 用 LoadingManager.ReplaceItem 替换 Play 屏幕加载步骤
            // 旧版: args[0] 是 List<Action>
            if (args[0] is List<Action> actions)
            {
                int playIndex = actions.Count - 13;
                actions[playIndex] = () => ScreensManager.AddScreen("Play", new SuPlayScreen());
                Log.Information("[ScMP] PlayScreen replaced via List<Action>");
            }
            else
            {
                // LoadingManager 是静态类，无法通过 args 传入，直接用 ReplaceItem
                bool replaced = Game.LoadingManager.ReplaceItem("Initialize PlayScreen", () => ScreensManager.AddScreen("Play", new SuPlayScreen()));
                if (!replaced)
                {
                    Log.Information("[ScMP] ReplaceItem failed, fallback to QueueItem");
                    // 如果 ReplaceItem 失败，说明还没到 PlayScreen 加载步骤
                    // 使用 QueueItem 添加但必须用不同的 name
                    Game.LoadingManager.QueueItem("Initialize SuPlayScreen", () => ScreensManager.AddScreen("Play", new SuPlayScreen()));
                }
                else
                {
                    Log.Information("[ScMP] PlayScreen replaced via LoadingManager.ReplaceItem");
                }
            }
            return new object[] { false, args[0] };
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

            Log.Information("[ScMP] Database hooks applied");
            return new object[] { true, database };
        }

        // ====================================================================
        // Update
        // ====================================================================
        public void Update(float dt)
        {
            connectionSM.Update();
            downloadSM.Update();

            m_accumulatedTime += dt;

            if (m_accumulatedTime >= 1f / 30f) // 30fps
            {
                m_accumulatedTime = 0f;
                Trigger30FrameEvent(dt);
            }

            // 聊天 (T键) - 仅Windows
#if WINDOWS
            if (Keyboard.IsKeyDownOnce(Key.T))
            {
                DialogsManager.ShowDialog(ScreensManager.RootWidget, new TextBoxDialog("Message", "", 125, delegate (string s)
                {
                    if (s != null)
                        NetworkMessageSender.SendChatMessage(client.Address.ToString(), s);
                }));
            }

            // 创建房间 (J键)
            if (Keyboard.IsKeyDownOnce(Key.J))
#else
            // Android: 触摸UI触发（待实现）
            if (false)
#endif
            {
                var sd = explorer?.DiscoveredServers?.FirstOrDefault();
                if (sd == null)
                {
                    Log.Information("[ScMP] No server discovered, cannot create game");
                    return;
                }

                // 从当前游戏状态构建世界信息
                var gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(true);
                if (gameInfo == null)
                {
                    Log.Error("[ScMP] Cannot get game info");
                    return;
                }
                WorldInfo wi = null;
                foreach (var w in WorldsManager.WorldInfos)
                {
                    if (w.DirectoryName == gameInfo.DirectoryName)
                    { wi = w; break; }
                }
                var worldMsg = new GameWorldInfoMessage(
                    gameInfo.WorldSettings.Name,
                    wi?.Size ?? 0,
                    wi?.LastSaveTime ?? DateTime.MinValue,
                    gameInfo.WorldSettings.GameMode,
                    gameInfo.WorldSettings.EnvironmentBehaviorMode,
                    VersionsManager.SerializationVersion,
                    client.Address);

                DialogsManager.ShowDialog(ScreensManager.RootWidget,
                    new MessageDialog("Create Game", $"Hosting '{gameInfo.WorldSettings.Name}'",
                        "CreateGame", "Cancel", delegate (MessageDialogButton b)
                        {
                            if (b == MessageDialogButton.Button1)
                            {
                                LastGameDescription = Message.WriteWithSender(worldMsg, client.Address);
                                client.CreateGame(sd.Address, LastGameDescription, client.ClientID.ToString());
                                Log.Information($"[ScMP] Creating game: {gameInfo.WorldSettings.Name}");
                            }
                        }));
            }

#if WINDOWS
            // 加入房间 (K键)
            if (Keyboard.IsKeyDownOnce(Key.K))
#else
            if (false)
#endif
            {
                var sd = explorer?.DiscoveredServers?.FirstOrDefault();
                if (sd == null || sd.GameDescriptions.Length == 0)
                {
                    Log.Information("[ScMP] No games available to join");
                    return;
                }
                DialogsManager.ShowDialog(null,
                    new ListSelectionDialog("Select Game", sd.GameDescriptions, 60f,
                        (object item) => ((GameDescription)item).ToString(),
                        delegate (object item)
                        {
                            var gd = (GameDescription)item;
                            client.JoinGame(sd.Address, gd.GameID,
                                Message.WriteWithSender(new ChatMessage("Joining...", ""), client.Address),
                                client.ClientID.ToString());
                            Log.Information($"[ScMP] Joining game {gd.GameID}");
                        }));
            }

#if WINDOWS
            // 踢出玩家 (U键, 仅Host)
            if (Keyboard.IsKeyDownOnce(Key.U) && client.ClientID == 0 && client.IsConnected)
#else
            if (false)
#endif
            {
                TryKickPlayer();
            }

            // 渲染远程玩家
            RenderRemotePlayers();
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

            int currentClientId = client.ClientID;
            int clientPlayerIndex = playerMappingManager.GetPlayerIndex(currentClientId);

            foreach (var item in players)
            {
                if (clientPlayerIndex == -1 || item.PlayerData.PlayerIndex != clientPlayerIndex)
                    continue;

                // 发送方直接使用 ClientID 作为网络标识，避免 PlayerIndex 映射冲突
                int senderClientId = client.ClientID;

                bool isCrouching = item.ComponentBody.TargetCrouchFactor > 0f;
                bool isFlying = item.ComponentLocomotion.IsCreativeFlyEnabled;
                bool isRiding = item.ComponentRider?.Mount != null;

                IInventory inventory = item.ComponentMiner?.Inventory;
                int activeSlot = inventory?.ActiveSlotIndex ?? -1;
                int handVal = inventory?.GetSlotValue(activeSlot) ?? 0;
                int handCnt = inventory?.GetSlotCount(activeSlot) ?? 0;

                Vector3 itemOffset = item.ComponentCreatureModel.InHandItemOffsetOrder;
                Vector3 itemRotation = item.ComponentCreatureModel.InHandItemRotationOrder;
                float aimHandAngle = item.ComponentCreatureModel.AimHandAngleOrder;
                Vector2 lookAngles = item.ComponentLocomotion.LookAngles;

                NetworkMessageSender.SendPlayerPositionMessage(
                    senderClientId, item.ComponentBody.Position,
                    item.ComponentBody.Rotation, item.ComponentBody.Velocity, lookAngles,
                    isCrouching, isFlying, isRiding, activeSlot, handVal, handCnt,
                    itemOffset, itemRotation, aimHandAngle);
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
            int clientPlayerIndex = playerMappingManager.GetPlayerIndex(currentClientId);

            foreach (var item in players)
            {
                if (clientPlayerIndex == -1 || item.PlayerData.PlayerIndex != clientPlayerIndex)
                    continue;

                var health = item.ComponentHealth;
                if (health == null) continue;

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
                playerMappingManager.ReleasePlayerIndex(item.ClientID);
            }

            // 加入
            foreach (var item in obj.Joins)
            {
                Log.Information($"[ScMP] Client joining: {item.ClientID}");
                int assignedPlayerIndex = playerMappingManager.AssignPlayerIndex(item.ClientID);

                if (assignedPlayerIndex != -1)
                {
                    Log.Information($"[ScMP] Assigned PlayerIndex {assignedPlayerIndex} to ClientID {item.ClientID}");
                    client.AcceptJoinGame(item.ClientID);

                    // 匹配世界并发送 WorldData
                    if (Message.Read(item.JoinRequestBytes) is GameWorldInfoMessage worldInfo)
                    {
                        foreach (var wi in WorldsManager.WorldInfos)
                        {
                            if (wi.LastSaveTime == worldInfo.LastSaveTime &&
                                wi.WorldSettings.Name == worldInfo.Name)
                            {
                                // 导出世界数据
                                byte[] worldData;
                                using (var ms = new MemoryStream())
                                {
                                    WorldsManager.ExportWorld(wi.DirectoryName, ms);
                                    worldData = ms.ToArray();
                                }
                                NetworkMessageSender.SendPakWorldMessage(
                                    wi.WorldSettings.Name, worldData, wi.LastSaveTime);
                                Log.Information($"[ScMP] Sent world data ({worldData.Length} bytes) to ClientID {item.ClientID}");
                                break;
                            }
                        }
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
                        if (connectionSM.CurrentState == NetworkConnectionStateMachine.ConnectionState.Playing)
                            HandleGamePlayerPositionMessage(pos, item.ClientID);
                        break;
                    case GameModifiedCellsMessage cells:
                        if (connectionSM.CurrentState == NetworkConnectionStateMachine.ConnectionState.Playing)
                            NetworkMessageHandler.HandleModifiedCellsMessage(cells, item.ClientID);
                        break;
                    case GameWorldInfoMessage1 worldInfo:
                        if (connectionSM.CurrentState == NetworkConnectionStateMachine.ConnectionState.Playing)
                            NetworkMessageHandler.HandleWorldInfoMessage(worldInfo, item.ClientID);
                        break;
                    case GamePakWorldMessage pakWorld:
                        NetworkMessageHandler.HandlePakWorldMessage(pakWorld, item.ClientID);
                        break;
                    case GamePlayerHealthMessage health:
                        if (connectionSM.CurrentState == NetworkConnectionStateMachine.ConnectionState.Playing)
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
            int remoteClientId = msg.PlayerIndex;
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
            var timeOfDay = GameManager.Project.FindSubsystem<SubsystemTimeOfDay>(true);
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
                WorldsManager.ImportWorld(new MemoryStream(msg.WorldData));
                WorldsManager.UpdateWorldsList();

                foreach (var wi in WorldsManager.WorldInfos)
                {
                    if (wi.WorldSettings.Name == msg.Name)
                    {
                        SuPlayScreen.Play(wi);
                        connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Playing);
                        Log.Information($"[ScMP] World imported, entering game: {msg.Name}");
                        return;
                    }
                }
                Log.Error($"[ScMP] World imported but not found in world list: {msg.Name}");
            }
            catch (Exception ex)
            {
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
            connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Playing);
        }

        private void Client_GameJoined(GameJoinedData obj)
        {
            Log.Information($"[ScMP] GameJoined, Step={obj.Step}, ClientID={client.ClientID}");
            IsHost = false;
            downloadSM.TransitionTo(WorldDownloadStateMachine.DownloadState.Requesting);
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
            connectionSM.TransitionTo(NetworkConnectionStateMachine.ConnectionState.Disconnected);
        }

        private void Client_GameStateRequest(GameStateRequestData obj)
        {
            client.SendState(client.Step,
                Message.WriteWithSender(new ChatMessage("StateSync", "OK"), client.Address));
        }

        private void Client_Error(Exception obj)
        {
            Log.Error($"[ScMP] Client error: {obj.Message}");
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
                GameManager.Project.FindSubsystem<SubsystemUpdate>(true).AddUpdateable(this);
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
