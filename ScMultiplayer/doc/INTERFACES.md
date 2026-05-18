# ScMultiplayer 接口文档

> 当前使用的接口 + 未使用但可满足功能需求的接口
> 最后更新: 2026-05-17

---

## 一、SuAPI 接口使用清单

### IModEventBus

| 事件 | 用途 | 优先级 | 返回值 | 文件 |
|------|------|--------|--------|------|
| GameDatabase.GameDatabase | 替换 ComponentInput + SubsystemTerrain | HIGHEST | {true, database} | ScMultiplayer.cs |
| Loading.Initialize | 替换 PlayScreen | HIGHEST | {false, actions} | ScMultiplayer.cs |

### IModParentField (反射私有字段)

| 调用 | 目标 | 用途 |
|------|------|------|
| GetParentField< List<WorldInfo> > | WorldsManager.m_worldInfos | 获取世界列表 |
| GetParentField< Dictionary<Point3,bool> > | SubsystemTerrain.m_modifiedCells | 获取修改方块 |
| GetParentField< ComponentPlayer > | ComponentInput.m_componentPlayer | 获取玩家组件 |
| GetParentField< double > | ComponentInput.m_lastJumpTime | 获取跳计时 |
| ModifyParentField | ComponentInput.m_playerInput | 写入输入 |
| ModifyParentField | ComponentLocomotion.LookAngles | 写入视角 |
| GetStaticField | WorldsManager.m_worldInfos | 静态字段 |

### IModParentMethod (反射私有方法)

| 调用 | 目标 | 用途 |
|------|------|------|
| InvokeParentMethod | UpdateInputFromMouseAndKeyboard | 输入处理 |
| InvokeParentMethod | UpdateInputFromGamepad | 输入处理 |
| InvokeParentMethod | UpdateInputFromVrControllers | 输入处理 |
| InvokeParentMethod | UpdateInputFromWidgets | 输入处理 |

---

## 二、Comms.Drt 接口使用清单

### Client

| 接口 | 用途 | 调用位置 |
|------|------|----------|
| Client(gameTypeID) | 构造 | OnLoad |
| Start() | 启动 | OnLoad |
| CreateGame(addr, desc, name) | 创建房间 | SuPlayScreen.GameCreate |
| JoinGame(addr, gameID, join, name) | 加入房间 | SuPlayScreen.GameJoin |
| LeaveGame() | 离开 | SuPlayScreen.Enter |
| SendInput(byte[]) | 发送输入 | 多处 |
| SendState(step, byte[]) | 发送状态 | Client_GameStateRequest |
| AcceptJoinGame(clientID) | 接受加入 | GameStep joins |
| RefuseJoinGame(clientID, reason) | 拒绝加入 | GameStep (满员) |
| IsConnected | 是否连接 | Update 等多处 |
| ClientID | 本地ID | 多处 |
| Step | 当前步数 | 多处 |
| Address | 本地地址 | 发送者标识 |
| GameStep 事件 | 每 Tick | GameStep 处理 |
| GameCreated 事件 | 创建成功 | 日志 |
| GameJoined 事件 | 加入成功 | 日志 |
| ConnectRefused 事件 | 连接拒绝 | 日志 |
| GameDescriptionRequest 事件 | 描述请求 | 未处理 |
| GameStateRequest 事件 | 状态请求 | SendState |
| Error 事件 | 错误 | 日志 |

### Server

| 接口 | 用途 |
|------|------|
| Server(typeID, tickDur, steps, port) | 构造 |
| Start() | 启动 |
| Games | 游戏列表 |
| Address | 地址 |
| Information 事件 | 日志 |

### Explorer

| 接口 | 用途 |
|------|------|
| Explorer(typeID, serverPort) | 构造 |
| StartDiscovery() | 开始发现 |
| StopDiscovery() | 停止发现 |
| DiscoveredServers | 发现的服务器 |
| Error 事件 | 错误 |

---

## 三、Peer/Comm 接口使用清单

| 接口 | 层 | 用途 |
|------|-----|------|
| Peer.Start() | Peer | 启动 (内部) |
| Peer.ConnectedTo | Peer | 连接状态 |
| Peer.Address | Peer | 本地地址 |
| Peer.Disconnect() | Peer | 断开 |
| Peer.Lock | Peer | 同步锁 |
| Peer.DataMessageReceived | Peer | 数据接收 |
| Comm.GetTime() | Comm | 统一时间 |
| Comm.Lock | Comm | 同步锁 |

---

## 四、未使用但可用的接口

### 房间管理类

| 接口 | 功能 | 场景 | 优先级 |
|------|------|------|--------|
| Peer.DisconnectPeer(PeerData) | 踢出对端 | 房主踢人 | **高** |
| Peer.DisconnectAllPeers() | 全部断开 | 关闭房间 | 高 |
| server.DisconnectAllClients() | 服务器踢人 | 管理 | 中 |
| ServerGame.Clients | 客户端列表 (只读) | 玩家列表 | 中 |

### 连接管理类

| 接口 | 功能 | 优先级 |
|------|------|--------|
| Peer.DiscoverPeer(IPEndPoint) | 单播发现 | 中 |
| Peer.RespondToDiscovery() | 自定义发现响应 | 低 |
| client.ConnectTimedOut 事件 | 超时回调 | 中 |
| client.Disconnected 事件 | 断连回调 | 高 |
| PeerSettings.SendPeerConnectDisconnectNotifications | 对端通知 | 中 |

### 数据传输优化类

| 接口 | 功能 | 优先级 |
|------|------|--------|
| DeliveryMode.Unreliable | 不保证送达 | **高** |
| DeliveryMode.UnreliableSequenced | 有序无重发 | **高** |
| Peer.SendDataMessages() | 批量发送 | 中 |
| client.SendGameDescription() | 动态描述更新 | 中 |

### 状态校验类

| 接口 | 功能 | 优先级 |
|------|------|--------|
| client.SendDesyncState() | 不同步上报 | 低 |
| client.SendStateHashes() | 哈希校验 | 低 |
| GameDesyncStateRequest 事件 | 状态请求 | 低 |
| DesyncData | 不同步数据 | 低 |

### 资源分分类

| 接口 | 功能 | 优先级 |
|------|------|--------|
| server.SendResource() | 资源发送 | 中 |
| explorer.RequestResource() | 资源请求 | 中 |
| ResourceReceived 事件 | 资源接收 | 中 |

---

## 五、游戏 Subsystem 接口

### 当前使用

| Subsystem | 获取 | 用途 |
|-----------|------|------|
| SubsystemPlayers | FindSubsystem<> | 玩家列表 |
| SubsystemTimeOfDay | FindSubsystem<> | 时间同步 |
| SubsystemGameInfo | FindSubsystem<> | 游戏信息 |
| SubsystemUpdate | FindSubsystem<> | IUpdateable |
| SubsystemTerrain | 替换为 SuSubsystemTerrain | 方块 |

### 可能需要 (用于未来同步)

| Subsystem | 用途 | 关联功能 |
|-----------|------|----------|
| SubsystemWeather | 天气 | 天气同步 |
| SubsystemExplosions | 爆炸 | 爆炸同步 |
| SubsystemPickables | 掉落物 | 掉落同步 |
| SubsystemCreatureSpawn | 生物生成 | 实体同步 |
| SubsystemBodies | 物理体 | 物理同步 |
| SubsystemProjectiles | 投射物 | 投射物同步 |
| SubsystemFurnitureBlockBehavior | 家具 | 家具同步 |
| SubsystemElectricity | 电路 | 电路同步 |

### 未使用的 SuAPI 接口

| 接口 | 功能 | 潜在用途 |
|------|------|----------|
| ModResource.LoadModResources() | Content 加载 | 自定义纹理/音效 |
| ModResource KV Store | 键值存储 | Mod 配置持久化 |
| ModInjector.Register() | 类名映射 | Block 类型替换 |
