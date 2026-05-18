# ScMultiplayer 联机 Mod 架构文档

> 版本: 1.0.1
> 最后更新: 2026-05-17
> 依赖: Comms.dll, SuAPI (SuMod)

---

## 一、项目结构

ScMultiplayer/
├── Plug/
│   └── ScMultiplayer.cs          # IMod 入口 + IUpdateable + 全部主逻辑
├── Func/
│   ├── Component/
│   │   └── SuComponentInput.cs   # 替换 ComponentInput
│   ├── Screen/
│   │   └── SuPlayScreen.cs       # 替换 PlayScreen (世界列表)
│   └── Subsystem/
│       └── SuSubsystemTerrain.cs # 替换 SubsystemTerrain
├── Message/
│   ├── Message.cs                # 抽象消息基类 (Read/Write + IPEndPoint 发送者)
│   ├── SuReader.cs               # 扩展 Reader (Point3/Vector/Quaternion/Ray3)
│   ├── SuWriter.cs               # 扩展 Writer (同上)
│   ├── ChatMessage.cs            # 聊天消息
│   ├── GamePlayerPositionMessage.cs   # 玩家位置/旋转/速度/手持/状态
│   ├── GamePlayerInputMessage.cs      # 完整 PlayerInput (代码完成, 注释未启用)
│   ├── GameModifiedCellsMessage.cs    # 方块修改
│   ├── GameWorldInfoMessage.cs        # 世界信息 (房间列表用)
│   ├── GameWorldInfoMessage1.cs       # 世界时间同步
│   └── GamePakWorldMessage.cs         # 世界存档传输
├── String2int.cs                 # 字符串 -> 端口号映射
├── ScMultiplayer.csproj          # SDK 样式 .NET Framework 4.8 项目
└── doc/                          # 本文档目录

---

## 二、核心架构

### 入口 (OnLoad)

ScMultiplayer.OnLoad()
├── currentInstance = this
├── 订阅 GameDatabase.GameDatabase -> HandleGameDatabase
│     └── 替换 ComponentInput GUID -> ScMultiplayer.SuComponentInput
│     └── 替换 SubsystemTerrain GUID -> ScMultiplayer.SuSubsystemTerrain
├── 订阅 Loading.Initialize -> HandleLoading
│     └── 替换 Play Screen (actions[Count-13]) -> SuPlayScreen
├── port = "SuSCMP".ToDynamicPort()  (49152-65535)
├── new Explorer(gameTypeID: 0x53634d70, serverPort: port)
├── new Server(gameTypeID, 1/60f, 1, port) -> server.Start()
│     └── catch(Exception){} -- 静默失败!
├── new Client(gameTypeID) -> 订阅事件 -> client.Start()
├── explorer.StartDiscovery()
└── StartAsyncRegistration() -> AddUpdateable(this)

### 运行时数据流

[本机玩家操作]
  |
  v
SuComponentInput.Update()
  依赖 base.Update() 处理键盘/鼠标/手柄输入
  PlayerInput 被游戏原生逻辑消费
  UpdateNow() (已实现但未激活) 可 override 本机 PlayerInput
  |
  v
ScMultiplayer.Update() (每帧累计, 30fps 触发)
  |
  +--> Trigger30FrameEvent(dt)
  |    if (client.IsConnected) {
  |      SendGamePlayerPositionMessage()  // 发本机玩家状态
  |      SendGameWorldInfoMessage(dt)     // Host 才发
  |    }
  |
  +--> 按键检测
       T: 聊天对话框 -> ChatMessage
       J: 创建房间 (取第一个发现的 Server)
       K: 加入房间 (弹出游戏列表选择)

[网络接收]
Client.GameStep 事件 (每 Tick)
  |
  +--> Inputs[]: Message.Read() 分派 ->
  |    ChatMessage -> NetworkMessageHandler.HandleChatMessage
  |    GamePlayerPosition -> HandleGamePlayerPositionMessage (设置远程玩家位置)
  |    GameModifiedCells -> HandleGameModifiedCellsMessage (执行方块修改)
  |    GameWorldInfo1 -> HandleGameWorldInfoMessage (同步时间)
  |    GamePakWorld -> HandleGamePakWorldMessage (导入世界存档)
  |
  +--> Joins[]: 分配 PlayerIndex -> AcceptJoinGame / RefuseJoinGame
  +--> Leaves[]: 释放 PlayerIndex

---

## 三、替换的游戏类

### SuComponentInput : ComponentInput
- **替换 GUID**: ec809766-ba61-434e-bfde-e677f506b887
- **更新**: override Update() -> base.Update() 保留原生输入处理
- **保留未用**: UpdateNow() 可强制覆盖 PlayerInput (ModParentField)

### SuSubsystemTerrain : SubsystemTerrain
- **替换 GUID**: e2636c38-f179-4aa1-b087-ed6920d66e8e
- **更新**: override Update()
  - 全局 ModifiedCells 引用 (ModParentField 获取 m_modifiedCells)
  - 连接时: ModifiedCells 有变更 -> 发送 GameModifiedCellsMessage
  - 接收远程变更: ReModifiedCells 有数据 -> ChangeCell() 逐个执行
  - 调用 TerrainUpdater.Update() 和 ProcessModifiedCells()

### SuPlayScreen : PlayScreen
- **替换方式**: Loading.Initialize actions 列表替换
- **扩展**:
  - 世界列表 ItemWidgetFactory 增强: 显示 "Net (Ping Xms)"
  - 收到远程世界信息 -> 动态添加到世界列表
  - 双击/Play 按钮 -> 自动判断: 有游戏就 JoinGame, 没有就 CreateGame
  - Enter 时扫描局域网服务器
  - GameCreate: 导出世界 -> WorldData 缓存 -> CreateGame
  - GameJoin: JoinGame 发送世界匹配信息

---

## 四、消息体系

### 自定义 Message 基类

ScMultiplayer.Message (不同于 Comms Peer 内部 Message):
- 自动类型注册: 反射扫描 Message 子类, 按名称排序分配 ID
- 发送者标识: 序列化/反序列化 IPEndPoint
- 便捷方法: SetSender(), GetSenderPort() (过滤自己)

### 已定义消息 (按名称排序, ID 自动分配)

| 消息类 | 序列化内容 | 方向 |
|--------|------------|------|
| ChatMessage | Sender(string), Text(string), Timestamp, MessageId | 双向 |
| GameModifiedCellsMessage | Dictionary<Point3,bool> + List<int>(CellValues) | P -> S -> All |
| GamePakWorldMessage | Name, WorldData(byte[]), LastSaveTime | Host -> Client |
| GamePlayerInputMessage | PlayerIndex, 完整 PlayerInput(所有字段) | P -> S -> All |
| GamePlayerPositionMessage | PlayerIndex, Position, Rotation, Velocity, LookAngles, 手持信息, 动画参数 | P -> S -> All |
| GameWorldInfoMessage | Name, Size, LastSaveTime, GameMode, EnvMode, SerializationVersion, HostAddress | 广播 (房间列表) |
| GameWorldInfoMessage1 | TimeOfDayOffset, TotalElapsedGameTime, TimeOfDayMode | Host -> S -> All |

### SuReader / SuWriter 扩展方法

在 Comms Reader/Writer 基础上:
- Point3: WritePackedInt32 三坐标 (压缩)
- Vector2: 2x WriteSingle
- Vector3: 3x WriteSingle
- Quaternion: 4x WriteSingle
- Ray3: Vector3 position + Vector3 direction

---

## 五、玩家管理

### PlayerMappingManager
- ClientID <-> PlayerIndex 双向映射
- MaxPlayerIndices = 4
- AssignPlayerIndex(clientId) -> 分配可用槽位, 满则返回 -1
- ReleasePlayerIndex(clientId) -> 释放槽位
- GetPlayerIndex(clientId) / GetClientId(playerIndex)

### PlayerOperationSyncManager
- ConvertPlayerIndexForClient: 将远程 PlayerIndex 转为本地 PlayerIndex
- ConvertLocalPlayerIndexToNetwork: 将本地 PlayerIndex 转为网络传输用

---

## 六、当前同步状态

### 已同步
| 数据 | 消息 | 频率 | 发送条件 |
|------|------|------|----------|
| 位置/旋转/速度 | GamePlayerPosition | 30fps | client.IsConnected |
| 视角角度 | GamePlayerPosition | 30fps | 同上 |
| 蹲/飞/骑 | GamePlayerPosition | 30fps | 同上 |
| 手持物品 | GamePlayerPosition | 30fps | 同上 |
| 手部动画 | GamePlayerPosition | 30fps | 同上 |
| 世界时间 | GameWorldInfo1 | 30fps | ClientID==0 |
| 方块修改 | GameModifiedCells | 按需 | 连接且有变更 |
| 世界下载 | GamePakWorld | 加入时 | Host 匹配世界 |
| 聊天 | ChatMessage | 按需 | T 键 |
| 加入/离开 | GameStep | 事件 | Drt 框架 |

### 代码完成但未启用
| 数据 | 位置 | 激活方式 |
|------|------|----------|
| 完整 PlayerInput | GamePlayerInputMessage + UpdateNow | 取消注释 |

### 需要新增同步
| 数据 | 优先级 | 复杂度 |
|------|--------|--------|
| 玩家生命值 | 高 | 低 |
| 物品栏变更 | 高 | 中 |
| 实体生成/消失 | 高 | 高 |
| 掉落物品 | 中 | 中 |
| 家具/方块交互 | 中 | 高 |
| 天气变化 | 中 | 低 |
| 门/电路状态 | 中 | 高 |
| 爆炸/投射物 | 低 | 高 |
| 玩家皮肤 | 低 | 低 |

---

## 七、关键规则

1. **Mod 开发者模式**: 不修改原始游戏代码, 通过 EventBus 替换
2. **禁止打包引擎 DLL**: Engine/EntitySystem/Survivalcraft.dll 不得打入 .scmod
3. **Comms 依赖**: .scmod Lib/X64/ 含 Comms.dll + ModInfo.xml Dependencies
4. **端口唯一性**: 同一主机仅一个实例 (哈希确定性端口)
5. **Play Screen 索引动态**: actions.Count-13, 不硬编码
6. **MSBuild 编译**: 不用 dotnet build
7. **打包扁平化**: Push-Location 后 Compress-Archive -Path *
8. **异常处理**: ModEventBus 异常静默, handler 外围 try-catch

---

## 八、连接流程

### 创建房间
Host: 选择世界 -> SuPlayScreen.GameCreate()
  1. 导出世界数据到 WorldData (MemoryStream)
  2. explorer.DiscoveredServers.First() -> CreateGame(addr, GameWorldInfoMessage, port)
  3. Server 转发创建请求 -> Client.GameCreated 事件
  4. ClientID = 0  (房主)
  5. SuPlayScreen.Play(item) 进入游戏
  6. 其他玩家 Join 时 -> Send PakWorldMessage

### 加入房间
Client: 选世界 -> SuPlayScreen.GameJoin()
  1. JoinGame(addr, gameID, GameWorldInfoMessage(本地世界信息), clientName)
  2. Server 转发 -> Host GameStep.Joins
  3. Host 匹配世界 -> AcceptJoinGame -> 发送 GamePakWorldMessage
  4. Server 收集 Host 状态 -> 发送给新 Client
  5. Client.GameJoined 事件
  6. 收到 GamePakWorld -> ImportWorld -> Play
