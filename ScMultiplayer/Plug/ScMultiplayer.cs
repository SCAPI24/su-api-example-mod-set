using Comms;
using Comms.Drt;
using Engine;
using Engine.Input;
using Game;
using SuMod;
using SuMod.Tools;
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
using static System.Net.Mime.MediaTypeNames;

namespace ScMultiplayer
{
    /// <summary>
    /// 玩家映射管理器，用于管理客户端ID与PlayerIndex的映射关系
    /// </summary>
    public class PlayerMappingManager
    {
        /// <summary>
        /// 客户端ID到PlayerIndex的映射字典
        /// Key: 客户端ID
        /// Value: PlayerIndex
        /// </summary>
        private Dictionary<int, int> clientIdToPlayerIndex = new Dictionary<int, int>();

        /// <summary>
        /// PlayerIndex到客户端ID的映射字典
        /// Key: PlayerIndex
        /// Value: 客户端ID
        /// </summary>
        private Dictionary<int, int> playerIndexToClientId = new Dictionary<int, int>();

        /// <summary>
        /// 获取或设置最大PlayerIndex数量
        /// </summary>
        public int MaxPlayerIndices { get; set; } = 4;

        /// <summary>
        /// 为指定客户端分配PlayerIndex
        /// </summary>
        /// <param name="clientId">客户端ID</param>
        /// <returns>分配的PlayerIndex，如果无法分配则返回-1</returns>
        public int AssignPlayerIndex(int clientId)
        {
            // 如果客户端已经有分配的PlayerIndex，直接返回
            if (clientIdToPlayerIndex.ContainsKey(clientId))
            {
                return clientIdToPlayerIndex[clientId];
            }

            // 查找可用的PlayerIndex
            for (int i = 0; i < MaxPlayerIndices; i++)
            {
                // 跳过已经被占用的PlayerIndex
                if (playerIndexToClientId.ContainsKey(i))
                {
                    continue;
                }

                // 分配PlayerIndex
                clientIdToPlayerIndex[clientId] = i;
                playerIndexToClientId[i] = clientId;
                return i;
            }

            // 没有可用的PlayerIndex
            return -1;
        }

        /// <summary>
        /// 释放指定客户端的PlayerIndex
        /// </summary>
        /// <param name="clientId">客户端ID</param>
        public void ReleasePlayerIndex(int clientId)
        {
            if (clientIdToPlayerIndex.ContainsKey(clientId))
            {
                int playerIndex = clientIdToPlayerIndex[clientId];
                clientIdToPlayerIndex.Remove(clientId);
                playerIndexToClientId.Remove(playerIndex);
            }
        }

        /// <summary>
        /// 获取指定客户端的PlayerIndex
        /// </summary>
        /// <param name="clientId">客户端ID</param>
        /// <returns>PlayerIndex，如果未分配则返回-1</returns>
        public int GetPlayerIndex(int clientId)
        {
            if (clientIdToPlayerIndex.ContainsKey(clientId))
            {
                return clientIdToPlayerIndex[clientId];
            }
            return -1;
        }

        /// <summary>
        /// 获取指定PlayerIndex的客户端ID
        /// </summary>
        /// <param name="playerIndex">PlayerIndex</param>
        /// <returns>客户端ID，如果未分配则返回-1</returns>
        public int GetClientId(int playerIndex)
        {
            if (playerIndexToClientId.ContainsKey(playerIndex))
            {
                return playerIndexToClientId[playerIndex];
            }
            return -1;
        }

        /// <summary>
        /// 获取所有已分配的PlayerIndex列表
        /// </summary>
        /// <returns>PlayerIndex列表</returns>
        public List<int> GetAllPlayerIndices()
        {
            return playerIndexToClientId.Keys.ToList();
        }
    }

    /// <summary>
    /// 玩家操作同步管理器，用于处理玩家操作在不同客户端间的同步
    /// </summary>
    public class PlayerOperationSyncManager
    {
        /// <summary>
        /// 将源客户端的PlayerIndex转换为目标客户端对应的PlayerIndex
        /// </summary>
        /// <param name="sourcePlayerIndex">源PlayerIndex</param>
        /// <param name="targetClientId">目标客户端ID</param>
        /// <returns>在目标客户端上对应的PlayerIndex</returns>
        public int ConvertPlayerIndexForClient(int sourcePlayerIndex, int targetClientId)
        {
            // 获取源PlayerIndex对应的客户端ID
            int sourceClientId = ScMultiplayer.playerMappingManager.GetClientId(sourcePlayerIndex);
            
            // 如果源PlayerIndex没有对应的客户端，则返回-1
            if (sourceClientId == -1)
            {
                return -1;
            }
            
            // 获取目标客户端的PlayerIndex
            int targetPlayerIndex = ScMultiplayer.playerMappingManager.GetPlayerIndex(targetClientId);
            
            // 如果目标客户端没有分配PlayerIndex，则返回-1
            if (targetPlayerIndex == -1)
            {
                return -1;
            }
            
            // 计算相对索引偏移
            int relativeIndex = (sourcePlayerIndex - targetPlayerIndex + ScMultiplayer.playerMappingManager.MaxPlayerIndices) % ScMultiplayer.playerMappingManager.MaxPlayerIndices;
            
            return relativeIndex;
        }
        
        /// <summary>
        /// 将本地PlayerIndex转换为网络传输用的PlayerIndex
        /// </summary>
        /// <param name="localPlayerIndex">本地PlayerIndex</param>
        /// <param name="localClientId">本地客户端ID</param>
        /// <returns>网络传输用的PlayerIndex</returns>
        public int ConvertLocalPlayerIndexToNetwork(int localPlayerIndex, int localClientId)
        {
            // 获取本地客户端的PlayerIndex
            int localClientPlayerIndex = ScMultiplayer.playerMappingManager.GetPlayerIndex(localClientId);
            
            // 如果本地客户端没有分配PlayerIndex，则返回-1
            if (localClientPlayerIndex == -1)
            {
                return -1;
            }
            
            // 计算相对于本地客户端PlayerIndex的偏移
            int relativeIndex = (localPlayerIndex - localClientPlayerIndex + ScMultiplayer.playerMappingManager.MaxPlayerIndices) % ScMultiplayer.playerMappingManager.MaxPlayerIndices;
            
            return relativeIndex;
        }
    }
    
    /// <summary>
    /// 网络消息处理器，用于处理不同类型的消息
    /// </summary>
    public class NetworkMessageHandler
    {
        /// <summary>
        /// 处理玩家位置消息
        /// </summary>
        /// <param name="message">玩家位置消息</param>
        /// <param name="clientID">发送消息的客户端ID</param>
        public static void HandlePlayerPositionMessage(GamePlayerPositionMessage message, int clientID)
        {
            // 记录日志
            Log.Information($"Handling player position message from client {clientID}, player index {message.PlayerIndex}");
            
            // 调用现有的处理方法
            ScMultiplayer.currentInstance.HandleGamePlayerPositionMessage(message, clientID);
        }
        
        /// <summary>
        /// 处理聊天消息
        /// </summary>
        /// <param name="message">聊天消息</param>
        /// <param name="clientID">发送消息的客户端ID</param>
        public static void HandleChatMessage(ChatMessage message, int clientID)
        {
            // 记录日志
            Log.Information($"Handling chat message from client {clientID}: {message.Sender} - {message.Text}");
            
            // 显示聊天消息
            // 这里可以添加显示聊天消息的逻辑
        }
        
        /// <summary>
        /// 处理世界信息消息
        /// </summary>
        /// <param name="message">世界信息消息</param>
        /// <param name="clientID">发送消息的客户端ID</param>
        public static void HandleWorldInfoMessage(GameWorldInfoMessage1 message, int clientID)
        {
            // 记录日志
            Log.Information($"Handling world info message from client {clientID}");
            
            // 调用现有的处理方法
            ScMultiplayer.currentInstance.HandleGameWorldInfoMessage(message);
        }
        
        /// <summary>
        /// 处理修改方块消息
        /// </summary>
        /// <param name="message">修改方块消息</param>
        /// <param name="clientID">发送消息的客户端ID</param>
        public static void HandleModifiedCellsMessage(GameModifiedCellsMessage message, int clientID)
        {
            // 记录日志
            Log.Information($"Handling modified cells message from client {clientID}");
            
            // 调用现有的处理方法
            ScMultiplayer.currentInstance.HandleGameModifiedCellsMessage(message);
        }
        
        /// <summary>
        /// 处理世界包消息
        /// </summary>
        /// <param name="message">世界包消息</param>
        /// <param name="clientID">发送消息的客户端ID</param>
        public static void HandlePakWorldMessage(GamePakWorldMessage message, int clientID)
        {
            // 记录日志
            Log.Information($"Handling pak world message from client {clientID}");
            
            // 调用现有的处理方法
            ScMultiplayer.currentInstance.HandleGamePakWorldMessage(message);
        }
    }
    
    /// <summary>
    /// 网络消息发送器，用于发送不同类型的消息
    /// </summary>
    public class NetworkMessageSender
    {
        /// <summary>
        /// 发送玩家位置消息
        /// </summary>
        /// <param name="playerIndex">玩家索引</param>
        /// <param name="position">位置</param>
        /// <param name="rotation">旋转</param>
        /// <param name="velocity">速度</param>
        /// <param name="lookAngles">视角角度</param>
        /// <param name="isCrouching">是否蹲下</param>
        /// <param name="isFlying">是否飞行</param>
        /// <param name="isRiding">是否骑乘</param>
        /// <param name="activeSlotIndex">活动槽位索引</param>
        /// <param name="handItemValue">手中物品值</param>
        /// <param name="handItemCount">手中物品数量</param>
        /// <param name="itemOffset">物品偏移</param>
        /// <param name="itemRotation">物品旋转</param>
        /// <param name="aimHandAngle">瞄准手角度</param>
        public static void SendPlayerPositionMessage(int playerIndex, Vector3 position, Quaternion rotation, Vector3 velocity, 
            Vector2 lookAngles, bool isCrouching, bool isFlying, bool isRiding, int activeSlotIndex, 
            int handItemValue, int handItemCount, Vector3 itemOffset, Vector3 itemRotation, float aimHandAngle)
        {
            // 创建消息
            GamePlayerPositionMessage message = new GamePlayerPositionMessage(
                playerIndex, position, rotation, velocity, lookAngles,
                isCrouching, isFlying, isRiding, activeSlotIndex, handItemValue, handItemCount, 
                itemOffset, itemRotation, aimHandAngle);
                
            // 发送消息
            ScMultiplayer.client.SendInput(Message.Write(message, ScMultiplayer.client.Address));
            
            // 记录日志
            Log.Information($"Sent player position message for player {playerIndex}");
        }
        
        /// <summary>
        /// 发送聊天消息
        /// </summary>
        /// <param name="sender">发送者</param>
        /// <param name="text">文本</param>
        public static void SendChatMessage(string sender, string text)
        {
            // 创建消息
            ChatMessage message = new ChatMessage(sender, text);
            
            // 发送消息
            ScMultiplayer.client.SendInput(Message.Write(message, ScMultiplayer.client.Address));
            
            // 记录日志
            Log.Information($"Sent chat message from {sender}: {text}");
        }
        
        /// <summary>
        /// 发送世界信息消息
        /// </summary>
        /// <param name="timeOfDay">时间</param>
        /// <param name="totalElapsedGameTime">总游戏时间</param>
        /// <param name="timeOfDayOffset">时间偏移</param>
        public static void SendWorldInfoMessage(double timeOfDayOffset, double totalElapsedGameTime, TimeOfDayMode currentTimeMode)
        {
            // 创建消息
            GameWorldInfoMessage1 message = new GameWorldInfoMessage1(timeOfDayOffset, totalElapsedGameTime, currentTimeMode);
            
            // 发送消息
            ScMultiplayer.client.SendInput(Message.Write(message, ScMultiplayer.client.Address));
            
            // 记录日志
            Log.Information($"Sent world info message");
        }
        
        /// <summary>
        /// 发送修改方块消息
        /// </summary>
        /// <param name="modifiedCells">修改的方块</param>
        /// <param name="cellValues">方块值</param>
        public static void SendModifiedCellsMessage(Dictionary<Point3, bool> modifiedCells)
        {
            // 创建消息
            GameModifiedCellsMessage message = new GameModifiedCellsMessage(modifiedCells);
            
            // 发送消息
            ScMultiplayer.client.SendInput(Message.Write(message, ScMultiplayer.client.Address));
            
            // 记录日志
            Log.Information($"Sent modified cells message");
        }
        
        /// <summary>
        /// 发送世界包消息
        /// </summary>
        /// <param name="name">名称</param>
        /// <param name="worldData">世界数据</param>
        /// <param name="lastSaveTime">最后保存时间</param>
        public static void SendPakWorldMessage(string name, byte[] worldData, DateTime lastSaveTime)
        {
            // 创建消息
            GamePakWorldMessage message = new GamePakWorldMessage(name, worldData, lastSaveTime);
            
            // 发送消息
            ScMultiplayer.client.SendInput(Message.Write(message, ScMultiplayer.client.Address));
            
            // 记录日志
            Log.Information($"Sent pak world message for {name}");
        }
    }
    
    public class ScMultiplayer : IMod, IUpdateable
    {
        public static ModManager ModManager= (ModManager)AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes()).FirstOrDefault(t => t.FullName == "Game.Program")?.GetField("ModManager", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        public static Server server;
        public static Client client;
        public static Explorer explorer;
        
        /// <summary>
        /// 当前ScMultiplayer实例
        /// </summary>
        public static ScMultiplayer currentInstance;
        
        /// <summary>
        /// 玩家映射管理器实例
        /// </summary>
        public static PlayerMappingManager playerMappingManager = new PlayerMappingManager();
        
        /// <summary>
        /// 玩家操作同步管理器实例
        /// </summary>
        public static PlayerOperationSyncManager playerOperationSyncManager = new PlayerOperationSyncManager();
        
        public string Name => "SC联机";
        public static bool IsHost=false;
        public string Version => "1.0.1";

        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;

        public UpdateOrder UpdateOrder => UpdateOrder.Input;

        public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
        {
            // 设置当前实例
            currentInstance = this;
            
            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                ; return HandleGameDatabase((Database)args[0]);
            }, EventPriority.HIGHEST);
            eventBus.SubscribeEvent("Loading.Initialize", args =>
            {
                return HandleLoading((List<Action>)args[0]);
            }, EventPriority.HIGHEST);



            float tickDuration = 1f / 60f; // 约 0.01667 秒
            int stepsPerTick = 1;
            int port = "SuSCMP".ToDynamicPort();


            explorer = new Explorer(gameTypeID: 0x53634d70, serverPort: port);
            explorer.Error += ex => Log.Information($"Error: {ex.Message}");
            try
            {
                server = new Server(0x53634d70, tickDuration, stepsPerTick, port);
                server.Information += Server_Information;
                server.Start();
            }
            catch (Exception)
            {


            }
            /*foreach (var item in server.Games)
            {
                item.Clients.
            }*/


            client = new Client(0x53634d70);
            client.GameCreated += Client_GameCreated;
            client.GameJoined += Client_GameJoined;
            client.Error += Client_Error;
            client.GameDescriptionRequest += Client_GameDescriptionRequest;
            client.ConnectRefused += Client_ConnectRefused;
            client.GameStateRequest += Client_GameStateRequest;
            client.GameStep += Client_GameStep;
            StartAsyncRegistration();
            // 发送局域网广播发现请求
            explorer.StartDiscovery();
            client.Start();

        }

        private object[] HandleLoading(List<Action> actions)
        {
            // “Play”是倒数第13个action（屏幕注册有固定数量，Play之后还有12个）
            // 用动态索引而非硬编码803，避免ContentManager.List变动导致偏移
            int playIndex = actions.Count - 13;
            actions[playIndex] = () =>
            {
                ScreensManager.AddScreen("Play", new SuPlayScreen());
            };
            Log.Information($"HandleLoading");
            return new object[] { false, actions };
        }

        public void Update(float dt)
        {
            m_accumulatedTime += dt;
            m_accumulatedTime1 += dt;
            if (m_accumulatedTime >= 1f / 60f)
            {
                m_accumulatedTime = 0f;
                Trigger30FrameEvent(dt);
            }

            // 聊天
            if (Keyboard.IsKeyDownOnce(Key.T))
            {
                DialogsManager.ShowDialog(ScreensManager.RootWidget, new TextBoxDialog("Message", "", 125, delegate (string s)
                {
                    if (s != null)
                    {
                        client.SendInput(Message.Write(new ChatMessage(client.Peer.Address.ToString(), s), client.Address));
                    }
                }));
            }

            // 创建游戏
            if (Keyboard.IsKeyDownOnce(Key.J))
            {
                Log.Information($"IsKeyDown(Key.J)");
                ServerDescription a = explorer.DiscoveredServers.FirstOrDefault();
                if (a != null)
                {
                    DialogsManager.ShowDialog(ScreensManager.RootWidget, new MessageDialog("Create Game", a.GameDescriptions.Count().ToString(), "CreateGame", "Suppress", delegate (MessageDialogButton b)
                    {
                        switch (b)
                        {
                            case MessageDialogButton.Button1:
                                client.CreateGame(a.Address, Message.Write(new ChatMessage("ss", "ss")), client.ClientID.ToString());
                                break;
                            case MessageDialogButton.Button2:
                                break;
                        }
                    }));
                }
            }

            // 加入游戏
            if (Keyboard.IsKeyDownOnce(Key.K))
            {
                Log.Information($"IsKeyDown(Key.K)");
                ServerDescription a = explorer.DiscoveredServers.FirstOrDefault();
                if (a != null)
                {
                    DialogsManager.ShowDialog(null, new ListSelectionDialog("Select Sort Order", a.GameDescriptions, 60f, (object item) => { return item.ToString(); }, delegate (object item)
                    {
                        client.JoinGame(a.Address, ((GameDescription)item).GameID, Message.Write(new ChatMessage("ss", "ss")), client.ClientID.ToString());
                        Log.Information("JoinGame:{0}", a.Address);
                    }));
                }
            }
        }

        private void Trigger30FrameEvent(float dt)
        {
            if (ScMultiplayer.client.IsConnected)
            {
                SendGamePlayerPositionMessage();
                SendGameWorldInfoMessage(dt);
            }
        }

        public object[] HandleGameDatabase(Database database)
        {
            var componentInput = database.FindDatabaseObject(new Guid("ec809766-ba61-434e-bfde-e677f506b887"), database.FindDatabaseObjectType("Parameter", true), true);
            componentInput.Value = "ScMultiplayer.SuComponentInput";
            var subsystemTerrain = database.FindDatabaseObject(new Guid("e2636c38-f179-4aa1-b087-ed6920d66e8e"), database.FindDatabaseObjectType("Parameter", true), true);
            subsystemTerrain.Value = "ScMultiplayer.SuSubsystemTerrain";

            Log.Information($"HandleDatabase");
            return new object[] { true, database };
        }

        private void Client_GameStep(GameStepData obj)
        {
            foreach (var item in obj.Leaves)
            {
                // 处理客户端离开事件
                Log.Information($"Client left: {item.ClientID}");
                // 释放该客户端的PlayerIndex
                playerMappingManager.ReleasePlayerIndex(item.ClientID);
            }
            
            foreach (var item in obj.Joins)
            {
                Log.Information($"Client joining: {item.ClientID}");
                
                // 为新客户端分配PlayerIndex
                int assignedPlayerIndex = playerMappingManager.AssignPlayerIndex(item.ClientID);
                
                if (assignedPlayerIndex != -1)
                {
                    Log.Information($"Assigned PlayerIndex {assignedPlayerIndex} to ClientID {item.ClientID}");
                    
                    // 接受客户端加入游戏
                    client.AcceptJoinGame(item.ClientID);
                    
                    switch (Message.Read(item.JoinRequestBytes))
                    {
                        case GameWorldInfoMessage gameWorldInfoMessage:
                            foreach (var worldInfos in WorldsManager.WorldInfos)
                            {
                                if (worldInfos.LastSaveTime == gameWorldInfoMessage.LastSaveTime)
                                {
                                    if (worldInfos.WorldSettings.Name == gameWorldInfoMessage.Name)
                                    {
                                        client.SendInput(Message.Write(new GamePakWorldMessage(worldInfos.WorldSettings.Name, SuPlayScreen.WorldData, worldInfos.LastSaveTime), client.Address));
                                    }
                                }
                            }
                            break;
                        default:
                            break;
                    }
                }
                else
                {
                    Log.Information($"Failed to assign PlayerIndex to ClientID {item.ClientID} - game full");
                    // 拒绝客户端加入（游戏已满）
                    client.RefuseJoinGame(item.ClientID, "Game is full");
                }
                
                Log.Information($"Client_GameStep joins: {obj.Joins.Length}");
            }
            
            foreach (var item in obj.Inputs)
            {
                var message = Message.Read(item.InputBytes);
                if (message.GetSenderPort() == client.Address.Port) 
                    continue;
                    
                // 使用NetworkMessageHandler处理不同类型的消息
                switch (message)
                {
                    case ChatMessage chatMessage:
                        NetworkMessageHandler.HandleChatMessage(chatMessage, item.ClientID);
                        break;
                    case GamePlayerInputMessage playerInputMessage:
                        //HandleGamePlayerInputMessage(playerInputMessage);
                        break;
                    case GamePlayerPositionMessage gamePlayerPositionMessage:
                        if (SuPlayScreen.IsGameJoined)
                            NetworkMessageHandler.HandlePlayerPositionMessage(gamePlayerPositionMessage, item.ClientID);
                        break;
                    case GameModifiedCellsMessage gameModifiedCellsMessage:
                        if (SuPlayScreen.IsGameJoined)
                            NetworkMessageHandler.HandleModifiedCellsMessage(gameModifiedCellsMessage, item.ClientID);
                        break;
                    case GameWorldInfoMessage1 gameWorldInfoMessage:
                        if (SuPlayScreen.IsGameJoined)
                            NetworkMessageHandler.HandleWorldInfoMessage(gameWorldInfoMessage, item.ClientID);
                        break;
                    case GamePakWorldMessage gamePakWorldMessage:
                        NetworkMessageHandler.HandlePakWorldMessage(gamePakWorldMessage, item.ClientID);
                        break;
                    default:
                        Log.Error("Unknown message type received");
                        break;
                }
            }
        }

        public void HandleGamePakWorldMessage(GamePakWorldMessage gamePakWorldMessage)
        {
            WorldsManager.ImportWorld(new MemoryStream(gamePakWorldMessage.WorldData));
            
            WorldsManager.UpdateWorldsList();
            foreach (var item in WorldsManager.WorldInfos) {

                if (item.WorldSettings.Name == gamePakWorldMessage.Name)
                {
                    SuPlayScreen.Play(item);
                }
            }


        }

        public void HandleGameWorldInfoMessage(GameWorldInfoMessage1 gameWorldInfoMessage)
        {
            var subsystemTimeOfDay = GameManager.Project.FindSubsystem<SubsystemTimeOfDay>(throwOnError: true);
            if (MathUtils.Abs(subsystemTimeOfDay.TimeOfDayOffset - gameWorldInfoMessage.TimeOfDayOffset) > 0.02)
                subsystemTimeOfDay.TimeOfDayOffset = gameWorldInfoMessage.TimeOfDayOffset;

        }

        public void HandleGameModifiedCellsMessage(GameModifiedCellsMessage gameModifiedCellsMessage)
        {
            // var subsystemTerrain = GameManager.Project.FindSubsystem<SuSubsystemTerrain>(throwOnError: true);
            lock (SuSubsystemTerrain.ReModifiedCells) // 添加线程锁
            {
                //int index = 0;
                SuSubsystemTerrain.CellValues = gameModifiedCellsMessage.CellValues;
                foreach (var kvp in gameModifiedCellsMessage.ModifiedCells)
                {
                    //subsystemTerrain.ChangeCell(kvp.Key.X, kvp.Key.Y, kvp.Key.Z, gameModifiedCellsMessage.CellValues[index],false);
                    SuSubsystemTerrain.ReModifiedCells[kvp.Key] = kvp.Value;
                    //index++; // 递增索引
                }
            }


        }

        public void HandleGamePlayerPositionMessage(GamePlayerPositionMessage gamePlayerPositionMessage, int clientID)
        {
            // 将网络PlayerIndex转换为本地PlayerIndex
            int localPlayerIndex = playerOperationSyncManager.ConvertPlayerIndexForClient(gamePlayerPositionMessage.PlayerIndex, ScMultiplayer.client.ClientID);
            
            // 如果转换失败，回退到原始值
            if (localPlayerIndex == -1)
            {
                localPlayerIndex = gamePlayerPositionMessage.PlayerIndex;
            }

            foreach (var item in GameManager.Project.FindSubsystem<SubsystemPlayers>(throwOnError: true).ComponentPlayers)
            {
                if (item.PlayerData.PlayerIndex == localPlayerIndex)
                {
                    item.ComponentBody.Position = gamePlayerPositionMessage.Position;
                    item.ComponentBody.Rotation = gamePlayerPositionMessage.Rotation;
                    item.ComponentBody.Velocity = gamePlayerPositionMessage.Velocity;
                    // 使用 ModParentField 修改私有字段
                    Game.Program.ModManager.ModParentField.ModifyParentField(item.ComponentLocomotion, "LookAngles", gamePlayerPositionMessage.LookAngles, typeof(ComponentLocomotion));

                    item.ComponentBody.TargetCrouchFactor = gamePlayerPositionMessage.IsCrouching ? 1f : 0f;
                    item.ComponentLocomotion.IsCreativeFlyEnabled = gamePlayerPositionMessage.IsFlying;
                    //item.ComponentRider?.Mount
                    IInventory inventory = item.ComponentMiner.Inventory;
                    inventory.ActiveSlotIndex = gamePlayerPositionMessage.ActiveSlotIndex;
                    // 移除当前槽位的所有物品（根据ComponentInventoryBase.cs）
                    // 获取新的槽位索引
                    int newActiveSlotIndex = inventory.ActiveSlotIndex;
                    int removedCount = inventory.RemoveSlotItems(newActiveSlotIndex, inventory.GetSlotCount(newActiveSlotIndex));

                    // 添加新的物品类型和数量（根据ComponentInventoryBase.cs）
                    if (gamePlayerPositionMessage.HandItemValue != 0 && gamePlayerPositionMessage.HandItemCount > 0)
                    {
                        inventory.AddSlotItems(newActiveSlotIndex, gamePlayerPositionMessage.HandItemValue, gamePlayerPositionMessage.HandItemCount);
                    }
                    var creatureModel = item.ComponentCreatureModel;
                    creatureModel.InHandItemOffsetOrder = gamePlayerPositionMessage.ItemOffset;
                    creatureModel.InHandItemRotationOrder = gamePlayerPositionMessage.ItemRotation;
                    creatureModel.AimHandAngleOrder = gamePlayerPositionMessage.AimHandAngle;
                }
            }
        }


        private void SendGamePlayerPositionMessage()
        {
            foreach (var item in GameManager.Project.FindSubsystem<SubsystemPlayers>(throwOnError: true).ComponentPlayers)
            {
                // 检查当前玩家是否属于当前客户端
                int currentPlayerIndex = item.PlayerData.PlayerIndex;
                int currentClientId = ScMultiplayer.client.ClientID;
                int clientPlayerIndex = playerMappingManager.GetPlayerIndex(currentClientId);
                
                // 只发送当前客户端控制的玩家数据
                if (clientPlayerIndex != -1 && currentPlayerIndex == clientPlayerIndex)
                {
                    // 获取当前状态（根据文档内容）
                    bool isCrouching = item.ComponentBody.TargetCrouchFactor > 0f;
                    bool isFlying = item.ComponentLocomotion.IsCreativeFlyEnabled;
                    bool isRiding = item.ComponentRider?.Mount != null;
                    //int mountEntityId = isRiding ? item.ComponentRider.Mount.Entity : 0;
                    // 获取当前手持物品状态（根据InventorySlotWidget.cs和ComponentHumanModel.cs）
                    IInventory inventory = item.ComponentMiner.Inventory;
                    int activeSlotIndex = inventory?.ActiveSlotIndex ?? -1;
                    int handItemValue = inventory?.GetSlotValue(activeSlotIndex) ?? 0;
                    int handItemCount = inventory?.GetSlotCount(activeSlotIndex) ?? 0;

                    // 获取物品动画状态（根据ComponentHumanModel.cs）
                    Vector3 itemOffset = item.ComponentCreatureModel.InHandItemOffsetOrder;
                    Vector3 itemRotation = item.ComponentCreatureModel.InHandItemRotationOrder;
                    float aimHandAngle = item.ComponentCreatureModel.AimHandAngleOrder;
                    // 假设 componentCreatureModel 是玩家的 ComponentCreatureModel 实例
                    Vector2 lookAngles = item.ComponentLocomotion.LookAngles;

                    // 使用网络PlayerIndex而不是本地PlayerIndex
                    int networkPlayerIndex = playerOperationSyncManager.ConvertLocalPlayerIndexToNetwork(currentPlayerIndex, currentClientId);
                    if (networkPlayerIndex == -1)
                    {
                        networkPlayerIndex = currentPlayerIndex; // 回退到原始值
                    }

                    // 使用NetworkMessageSender发送消息
                    NetworkMessageSender.SendPlayerPositionMessage(
                        networkPlayerIndex, item.ComponentBody.Position,
                        item.ComponentBody.Rotation, item.ComponentBody.Velocity, lookAngles,
                        isCrouching, isFlying, isRiding, activeSlotIndex, handItemValue, handItemCount, 
                        itemOffset, itemRotation, aimHandAngle);
                }
            }
        }
        private void HandleGamePlayerInputMessage(GamePlayerInputMessage playerInputMessage)
        {
            foreach (var item in GameManager.Project.FindSubsystem<SubsystemPlayers>(throwOnError: true).ComponentPlayers)
            {
                if (item.PlayerData.PlayerIndex == playerInputMessage.PlayerIndex + 1)
                {
                    Log.Information("S{0}", playerInputMessage.PlayerInput.Move);
                    Game.Program.ModManager.ModParentField.ModifyParentField(item.ComponentInput, "m_playerInput", playerInputMessage.PlayerInput, item.ComponentInput.GetType());
                    // Game.Program.ModManager.ModParentField.ModifyParentField(item, "ComponentInput", default(ComponentInput), this.GetType());
                    Log.Information("D{0}", item.ComponentInput.PlayerInput.Move);
                }
            }
            Log.Information("{0}", playerInputMessage.PlayerInput);
        }


        private void Client_GameStateRequest(GameStateRequestData obj)
        {
            client.SendState(client.Step, Message.Write(new ChatMessage("Client_GameStateRequest", "Ok"), client.Address));
            Log.Information($"Client_GameStateRequest:{0}", obj.ToString());
        }

        private void Client_ConnectRefused(ConnectRefusedData obj)
        {
            Log.Information($"Client_ConnectRefused:{0}", obj.Reason);
        }

        private void Client_GameJoined(GameJoinedData obj)
        {
            Log.Information($"Client_GameJoined:{0},ClientID is {1}", obj.Step, client.ClientID);
        }

        private void Client_GameCreated(GameCreatedData obj)
        {

            Log.Information($"Client_GameCreated:{0},ClientID is {1}", obj.CreatorAddress, client.ClientID);
        }

        private void Client_GameDescriptionRequest(GameDescriptionRequestData obj)
        {
        }
        private float m_accumulatedTime = 0f;
        private float m_accumulatedTime1 = 0f;

        private void SendGameWorldInfoMessage(float dt)
        {
            var subsystemGameInfo = GameManager.Project.FindSubsystem<SubsystemGameInfo>(throwOnError: true);
            var subsystemTimeOfDay = GameManager.Project.FindSubsystem<SubsystemTimeOfDay>(throwOnError: true);
            if (ScMultiplayer.client.ClientID == 0)
            {
                // 使用NetworkMessageSender发送消息
                NetworkMessageSender.SendWorldInfoMessage(
                    subsystemTimeOfDay.TimeOfDayOffset, 
                    subsystemGameInfo.TotalElapsedGameTime, 
                    subsystemGameInfo.WorldSettings.TimeOfDayMode);
            }
        }

        private async void StartAsyncRegistration()
        {
            try
            {
                // 等待项目加载
                await WaitForProjectLoaded();
                GameManager.Project.FindSubsystem<SubsystemUpdate>(throwOnError: true).AddUpdateable(this);
            }
            catch (Exception ex)
            {
                Log.Error($"异步注册失败: {ex.Message}");
            }
        }
        private async Task WaitForProjectLoaded()
        {
            while (GameManager.Project == null)
            {
                await Task.Delay(1000);
            }
        }


        private void Client_Error(Exception obj)
        {
            Log.Error("Client_Error{0}", obj);
        }


        private void Server_Information(string obj)
        {
            Log.Information(obj);
        }

        public void OnUnload()
        {
            throw new NotImplementedException();
        }


    }
}

/*           if (Keyboard.IsKeyDownOnce(Key.T))
           {
               DialogsManager.ShowDialog(ScreensManager.RootWidget, new TextBoxDialog("Message", "", 125, delegate (string s)
               {
                   if (s != null)
                   {

                       client.SendInput(Message.Write(new ChatMessage(client.Peer.Address.ToString(), s),client.Address));
                   }
               }));

           }*/
/* DatabaseObject ComponentTemplatemap = new DatabaseObject(database.FindDatabaseObjectType("ComponentTemplate", true), new Guid("387007A5-9269-1362-A0E7-DFEA4AC68E02"), "Map", null);
             ComponentTemplatemap.Description = "";
             ComponentTemplatemap.ExplicitInheritanceParent = database.FindDatabaseObject(new Guid("b05700ed-7e4e-4679-98f5-b597f421496b"), database.FindDatabaseObjectType("ComponentTemplate", true), true);
             ComponentTemplatemap.NestingParent = database.FindDatabaseObject("Gameplay", database.FindDatabaseObjectType("Folder", true), true);

             DatabaseObject Parameterclass = new DatabaseObject(database.FindDatabaseObjectType("Parameter", true), new Guid("B13D2D65-46A7-D038-8111-DE8FCBA58FBC"), "Class", "SurvivalcraftMiniMap.SuComponentMap");
             Parameterclass.NestingParent = ComponentTemplatemap;

             DatabaseObject databaseObject1 = new DatabaseObject(database.FindDatabaseObjectType("MemberComponentTemplate", true), new Guid("736FC2A9-9B0A-2E00-F7C8-95A4A6811FEE"), "Map", null);
             databaseObject1.Description = "";
             databaseObject1.ExplicitInheritanceParent = database.FindDatabaseObject(new Guid("387007A5-9269-1362-A0E7-DFEA4AC68E02"), database.FindDatabaseObjectType("ComponentTemplate", true), true);

             databaseObject1.NestingParent = database.FindDatabaseObject("Player", database.FindDatabaseObjectType("EntityTemplate", true), true);*///挂载


/*            if (Keyboard.IsKeyDownOnce(Key.J))
            {
                Log.Information($"IsKeyDown(Key.J)");
                ServerDescription a = explorer.DiscoveredServers.FirstOrDefault();
                DialogsManager.ShowDialog(ScreensManager.RootWidget, new MessageDialog("Loading Error", a.GameDescriptions.Count().ToString(), "CreateGame", "Suppress", delegate (MessageDialogButton b)
                {
                    switch (b)
                    {
                        case MessageDialogButton.Button1:
                            client.CreateGame(a.Address, Message.Write(new ChatMessage("ss", "ss")), client.ClientID.ToString());
                            break;
                        case MessageDialogButton.Button2:

                            break;
                    }
                }));
            }*/
/*if (Keyboard.IsKeyDownOnce(Key.K))
{
    Log.Information($"IsKeyDown(Key.K)");
    ServerDescription a = explorer.DiscoveredServers.FirstOrDefault();
    DialogsManager.ShowDialog(null, new ListSelectionDialog("Select Sort Order", a.GameDescriptions, 60f, (object item) => { return item.ToString(); }, delegate (object item)
    {

        client.JoinGame(a.Address, ((GameDescription)item).GameID, Message.Write(new ChatMessage("ss", "ss")), client.ClientID.ToString());
        Log.Information("JoinGame:{0}", a.Address);
    }));
}*/


/* using (MemoryStream memoryStream = new MemoryStream(SuPlayScreen.WorldData))
 {
     foreach (var worldInfos in WorldsManager.WorldInfos)
     {
         if (worldInfos.WorldSettings.Name == gameWorldInfoMessage.Name)
         {

             client.SendInput(Message.Write(new GamePakWorldMessage(worldInfos.WorldSettings.Name, memoryStream.ToArray())));
         }

     }

 }*/

/*
        SuPlayScreen.IsGameJoined = true;
        ScreensManager.SwitchScreen("GameLoading", item, null);
        SuPlayScreen.m_worldsListWidget.SelectedItem = null;*/
/*if (item.LastSaveTime.Equals(gamePakWorldMessage.LastSaveTime) )
    {

    }
    */
