# ScMultiplayer 模块化重构计划

状态：2.0.8 控制边界、消息路由、端口和阶段调度迁移完成；稳定业务算法保留在兼容宿主中

目标版本：以当前 2.0.x 为兼容基线，形成可在后续版本逐步启用的模块化单体；不改变现有联机协议、游戏行为和可靠序列语义。

## 1. 重构目标

当前 `Plug/ScMultiplayer.cs` 约 20,299 行，同时承担以下职责：

- SuAPI Mod 入口、生命周期和事件注入；
- Comms Server/Client/Explorer 的创建、连接和断线恢复；
- 加入审批、世界传输、catch-up 和断线清理；
- 网络消息注册、编解码、路由和可靠/非可靠发送策略；
- 玩家输入、动作、位置、生命、装备、物品栏和档案同步；
- 地形预测、实时修改、Chunk checkpoint、恢复和流体结算；
- 动物、掉落物、投射物、家具和其他世界对象同步；
- 电路同步、时间天气、闪电和世界控制；
- 聊天、玩家列表、带宽设置、加入审批等 UI；
- 玩家记录、自定义皮肤和网络地图资源持久化。

重构后遵循两个层次：

1. **控制单元（CU，Control Unit）**：只负责网络生命周期、事件分派、模块调度、权限/角色判断和发送策略选择。
2. **外围物料模块（Peripheral Modules）**：每个模块只负责一个业务域的状态、预测、权威执行或表现应用，通过端口接口使用网络和游戏线程能力。

控制单元不直接操作方块、实体、玩家库存或 UI；业务模块不直接调用 Comms 的底层发送方法。这样可以让网络算法成为调度器，而不是和业务执行代码互相嵌套。

## 0. 复审结论：稳定性优先的 SMART 目标

上一版目录是能力地图，不是一次性施工清单。ScMultiplayer 是一个单程序集、单游戏线程、带历史兼容负担的模块化单体，不应为了形式上的多层和接口数量改写已经稳定的功能。

本次重构采用以下 SMART 目标：

- **S（具体）**：只解决三个问题：`Plug/ScMultiplayer.cs` 的职责混杂、网络控制与业务执行互相调用、已确认的地形/加入/可靠队列热点缺少隔离。每个迁移单元必须有唯一业务所有者和输入/输出边界。
- **M（可衡量）**：最终入口文件只保留生命周期、模块组装、消息路由和调度，目标不超过 1,500 行；新增或移动的方法原则上不超过 150 行；任何模块超过 1,200 行必须按真实职责继续拆分。所有现有消息都要有 ID、字段顺序、DeliveryMode 和所有者清单。
- **A（可实现）**：采用兼容外壳和绞杀式迁移，先包裹现有 Comms/游戏适配器，再逐个迁移业务域；不要求一次性引入完整领域框架，也不为每个类创建接口。
- **R（相关）**：优先覆盖历史上反复出现的 Joining Room 卡住、地形漏块/恢复洪泛、可靠 ACK 堵塞、电路延迟、物品事务回滚、客户端退出清理和流量持续上涨问题；没有实证风险的稳定模块不强行移动。
- **T（有时限）**：分为 6 个可独立验收的阶段；前一阶段未通过协议、行为和性能门禁，不进入下一阶段。每个阶段必须产出可回滚提交、Release 构建和回归记录。

### SMART 性能与稳定门禁

以 Phase 0 实测基线为准，重构候选包必须满足：

1. 空闲和单玩家 UDP 实际收发量相对基线不增加超过 5%；
2. 双客户端地形/电路压力场景的 Apply p95、主线程 CPU p95 和托管分配不增加超过 5%；
3. 5% 模拟丢包、200 ms 延迟下，可靠队列可回落，不出现新的 `retry=30` 或电路序列长期等待；
4. A -> Host -> B 连续 10,000 次地形修改后，B 的最终地形和碰撞与主机一致，无需重连；
5. 首次加入和断线重连各连续 20 次，不出现永久 `Joining Room`，客户端退出不得影响其他客户端；
6. 物品、投射物、动物受伤/声音和世界控制的既有回归用例全部通过；
7. 任一门禁失败时保留旧路径，不以“结构更漂亮”为理由接受行为或性能退化。

## 0.1 稳定功能冻结清单

以下内容在本次重构中只做包裹、测试和必要的依赖注入，不重写算法：

- `Message/*` 的字段顺序、消息名、现有显式 wire ID 注册和 DeliveryMode；
- `Func/Circuit/CircuitSynchronizer.cs` 的电路算法、epoch 和可靠序列；
- `Networking/NetworkStateMachine.cs` 的已验证状态转移；
- `Func/Subsystem/SuSubsystemTerrain.cs` 对原版地形更新和碰撞的适配时序；
- 已稳定的拾取保护、丢弃事务、RAKE 预测和主机权威安全锁；
- 已验证的玩家受伤/拾取/动物声音/物品拖拽协议。

只有当某项功能已有回归测试、边界明确且移动后行为等价，才允许从冻结清单中移出。

## 0.2 原版复用与 Mod-only 边界

本次重构仍然是 Mod 内部的工程优化，不以重写原版游戏为目标。所有实现必须先检查原版类型、继承关系和现有 SuAPI 注入入口，再决定是否新增代码。

### 复用优先级

1. **直接复用原版公开类型和方法**：`ComponentPlayer`、`SubsystemTerrain`、`SubsystemElectricity`、`Pickable`、`Projectile`、`PlayScreen` 等继续作为真实游戏对象；模块只保存网络增量、请求和同步状态。
2. **继承原版类并通过现有注入替换**：沿用 `SuSubsystemTerrain : SubsystemTerrain`、`SuPlayScreen : PlayScreen` 和现有 Component/Subsystem 替换机制，在覆盖点前后调用原版逻辑。
3. **组合适配器**：原版类型不能继承或需要跨域调度时，使用小型 Adapter/Facade 持有原版对象，不复制原版完整状态。
4. **新增 Mod 内部类**：只承载网络控制、序列、预测、恢复、策略和模块状态，不重新实现原版已有的实体、物理、地形、电路或 UI 类。

禁止事项：

- 不在 Mod 中复制一个与原版 `ComponentPlayer`、`SubsystemTerrain`、`Entity`、`Pickable`、`Projectile` 或电路 Subsystem 功能相同的平行类；
- 不把原版完整对象序列化为第二套“网络实体”，只传输必要的权威增量、请求和表现快照；
- 不因为模块化而绕开原版邻近更新、碰撞、物理、库存事务、声音或电路模拟；
- 不将原版类的继承替换改成独立模拟器，除非已经证明原版入口无法满足需求并获得单独批准。

### 游戏接口申请流程

如果某个模块确实缺少原版/SuAPI 入口，必须先提交接口申请，不直接修改游戏源码。申请至少包含：

1. 缺失的最小能力和当前受阻的业务场景；
2. 已检查过的公开方法、继承点、EventBus、Injector、ParentField/ParentMethod 入口；
3. 为什么不能在 `Mod/ScMultiplayer` 内通过继承、适配或排队解决；
4. 最小新增接口签名、线程归属、生命周期和兼容影响；
5. 不添加接口时的降级行为和回滚方式。

接口未获批准前，重构只能停留在 Mod 范围内；不得修改 `Survivalcraft/`、`Engine/` 或其他原版程序集来“顺手解决”。获批后也只添加最小接口，不把业务逻辑移入游戏本体。

## 0.3 复审补充项

审计现有实现后，以下边界必须加入后续阶段，避免只拆文件而遗漏真实风险：

1. **消息安全边界**：在 Transport/Router 入口统一限制包长度、集合数量、字符串长度、Chunk/资源大小、ClientID、来源端点和消息阶段；业务模块不能各自实现一套不一致的校验。
2. **消息分派分配**：当前 `Client_GameStep` 对多类消息使用 `QueueEndOfFrameAction(() => ...)` 闭包入队。需要在不改变 Apply 时序的前提下，评估按消息类型的结构化命令队列，避免高流量时每包产生闭包和重复捕获。
3. **静态状态隔离**：`RemotePlayers`、连接注册、世界传输、模块缓存和 UI 状态要标明会话所有者；客户端退出、世界切换和测试实例重启必须逐模块清理，禁止由静态字典形成跨房间残留。
4. **AOT/反射兼容**：`Message.Read` 的 `Activator.CreateInstance`、程序集类型扫描和 SuAPI 注入点要建立 Windows/Android Release 检查；不能为模块化引入运行时反射查找热点，必要时保留现有显式注册和链接保留配置。
5. **异常隔离与可观测性**：单个消息或模块失败只能生成带来源、ClientID、request/sequence 的有界诊断记录，不得中断整个 GameStep；诊断采样不能进入每包热路径。
6. **测试基础设施**：当前没有 ScMultiplayer 测试工程。Phase 0 先建立不依赖游戏窗口的消息黄金样本、状态机/策略纯函数测试和可重复的双端日志比对脚本，再考虑引入测试项目。
7. **迁移依赖检查**：每次移动前记录原版继承点、SuAPI 注入方式、私有字段/方法入口和所有调用方；迁移后用静态检查阻止 Domain/业务模块直接引用 Comms 或 `ScMultiplayer` 巨型实例。
8. **发布兼容检查**：每个阶段都要验证 `ModInfo.xml`、API/协议/BuildFingerprint、扁平 `Lib/`、Windows/Android 共用 DLL 以及旧包清理规则；重构不能只在 Debug 或单平台成立。

## 2. 当前代码问题基线

| 当前文件/区域 | 主要问题 | 重构方向 |
|---|---|---|
| `Plug/ScMultiplayer.cs` | 入口、网络、业务、UI、持久化全部混合；大量静态状态和跨域调用 | 保留为 Composition Root/CU 外壳，按业务域迁移实现 |
| `Func/WorldObjectSynchronizer.cs` | 家具、标牌、容器、可编辑数据和实体快照混在一起 | 拆为 WorldObject、Container、EditableData 三个模块 |
| `Func/Subsystem/SuSubsystemTerrain.cs` | 原生地形更新、网络延迟单元、checkpoint 应用混合 | 保留原生适配器，网络队列移至 TerrainSync 模块 |
| `Func/Screen/SuPlayScreen.cs` | 原生世界列表、发现、创建/加入流程和网络状态 UI 混合 | 原生屏幕适配、Room UI、Join UI 分开 |
| `Message/Message.cs` | 消息基类、注册、发送者信息、路由约定耦合 | 在 Mod 内包裹 DTO/路由，保留现有注册顺序，不复制原版消息体系 |
| `Message/CircuitSyncMessage.cs` | 电路快照、事件、调度电压和传输状态混合 | 电路领域模型与网络 DTO 分离 |
| `Func/Server/ScMultiplayerSettings.cs` | 设置读取、带宽策略、服务器 UI 语义混合 | 配置模型、策略计算、配置 UI 分离 |
| `Networking/NetworkStateMachine.cs` | 连接状态和世界下载状态共用状态定义 | Session 状态与 WorldTransfer 状态拆分 |

## 3. 目标目录结构

以下是目标结构，按阶段迁移，不要求一次性创建所有文件：

```text
ScMultiplayer/
  Control/
    MultiplayerControlUnit.cs       # 唯一网络控制入口
    ModuleScheduler.cs               # 固定阶段和时间预算调度
    NetworkMessageRouter.cs          # 消息类型到模块的路由
    NetworkRolePolicy.cs             # Host/Client/Joining/Ready 权限判断
  Core/
    MultiplayerContext.cs            # 只读运行时依赖和共享句柄
    MultiplayerSessionState.cs       # 房间角色、连接、Transfer、退出状态
    ModuleTickContext.cs              # 当前帧、预算、网络时间、取消状态
    ModuleResult.cs                  # 模块处理结果和重试意图
  Ports/
    INetworkTransport.cs              # Comms 发送端口，不暴露业务语义
    IReliableChannel.cs               # 可靠窗口、序列和优先级端口
    IGameThreadDispatcher.cs          # 主线程/帧尾/优先动作队列
    IAuthoritativeWorld.cs             # 主机权威世界操作端口
    IWorldSnapshotStore.cs             # 世界快照和资源存取端口
    IPlayerStateStore.cs               # 玩家记录和会话状态端口
  Transport/
    CommsTransportAdapter.cs           # Comms.dll 的唯一适配层
    ReliableChannelCoordinator.cs      # 电路、操作、地形等可靠通道策略
    LatestStateChannel.cs              # 可替换的位置/状态快照
    NetworkMetricsCollector.cs         # 实际 UDP 收发、包数、重传统计
  Modules/
    Session/
      RoomSessionModule.cs             # 创建、加入、离开、断线恢复
      JoinApprovalModule.cs             # 主机审批、Later、踢出
      PlayerConnectionRegistry.cs      # ClientID、PlayerIndex、记录映射
    Join/
      WorldTransferModule.cs            # 地图分片、修复、校验和窗口
      JoinCatchUpModule.cs              # journal、checkpoint、完成屏障
      SessionAssetModule.cs             # 皮肤、材质和网络地图资源
    Player/
      PlayerInputSyncModule.cs         # 输入、视角、姿态和动作意图
      PlayerStateSyncModule.cs          # 位置、生命、体力、温度和装备
      PlayerActionAuthorityModule.cs    # 主机确认挖掘、交互、投掷和攻击
      InventoryAuthorityModule.cs       # inventory、快捷栏、crafting 事务
      PlayerProfileModule.cs            # 玩家档案、皮肤和下线保存
    Terrain/
      TerrainSyncModule.cs              # 实时方块修改广播和应用
      TerrainPredictionModule.cs        # 本地预测与权威结果回滚
      TerrainRecoveryModule.cs          # journal、缺失序列和定向恢复
      TerrainChunkCheckpointModule.cs   # Chunk revision、Data/Complete
      FluidSettlementModule.cs          # 水/岩浆/沙子等延迟结算
    Entity/
      AnimalSyncModule.cs               # 生物快照、行为、声音和受伤边沿
      PickableSyncModule.cs             # 掉落物、拾取确认和保护窗口
      ProjectileSyncModule.cs            # 投射物生成、命中、音效和冷却
      WorldObjectSyncModule.cs          # 家具、标牌和可编辑对象
    Circuit/
      CircuitSyncModule.cs              # 电路快照、事件和可靠序列隔离
      CircuitAuthorityAdapter.cs        # 主机电路执行和客户端表现
    World/
      WorldControlSyncModule.cs         # 时间、天气、雾、闪电
      WorldObjectDataModule.cs           # 容器、编辑数据和世界对象状态
    UI/
      MultiplayerUiModule.cs            # MP/IF/TA 面板
      RoomBrowserUiAdapter.cs           # 世界列表和发现结果
      JoinApprovalUiAdapter.cs          # 加入申请和状态窗口
      BandwidthSettingsUiAdapter.cs     # 简单/高级带宽配置
  Domain/
    Player/ ...                         # 与网络无关的玩家状态模型
    Terrain/ ...                        # 与传输无关的地形修改模型
    Entity/ ...                         # 动物、掉落物、投射物模型
    Circuit/ ...                        # 电路领域事件和快照模型
  Message/
    Dto/ ...                            # 只含字段和序列化顺序的网络 DTO
    Registry/StableMessageRegistry.cs   # 协议稳定 ID 映射
    Codec/MessageCodec.cs               # Reader/Writer 和包校验
  Adapters/
    Game/ ...                           # SuAPI/原版组件和 Subsystem 适配
    UI/ ...                             # Widget/Dialog 适配
  Diagnostics/
    NetworkAuditModule.cs
    SyncHealthSnapshot.cs
```

目录中的层次是逻辑边界，不是要求一次性建立的类清单。首轮只允许新增少量真实边界：

- `Control`：控制单元和调度器；
- `Core`：会话快照、帧预算和模块结果；
- `Ports`：只为 Comms、游戏线程、权威世界和存储建立端口；
- `Modules`：按业务域建立具体模块，内部优先使用具体类。

不采用以下做法：

- 不为每一个业务类创建 `IWhatever` 接口；
- 不在模块之间引入新的通用事件总线；
- 不把同一份状态复制到 CU、Domain 和模块三个地方；
- 不因为文件大小目标，拆开一个本来稳定且有完整测试的算法文件；
- 不让 Domain 层为了“纯净”复制大量游戏状态，导致每帧双份扫描和序列化；
- 不把原版已有的实体、Subsystem 或 UI 再建一套同名或等价实现。

模块化的最低标准是“边界清晰、状态有唯一所有者、调用方向稳定”，不是目录数量越多越好。

## 4. 控制单元职责

`MultiplayerControlUnit` 是唯一允许理解“网络控制算法”的组件，负责：

1. 组装 `MultiplayerContext` 和所有外围模块；
2. 订阅 Comms GameStep、Join、Leave、Error 和 SuAPI 生命周期事件；
3. 将输入消息、可靠消息和状态消息路由到对应模块；
4. 根据 Host/Client/Joining/Ready/Leaving 状态选择模块是否可执行；
5. 按固定阶段调用模块：Ingress -> Authority -> Replication -> Apply -> Egress -> Diagnostics；
6. 为每个模块提供帧时间预算、消息预算和取消/断线信号；
7. 统一处理可靠、可替换状态、加入传输和电路序列的通道策略；
8. 聚合模块结果，不直接执行业务操作。

控制单元禁止出现以下代码：

- `ChangeCell`、创建 Entity、修改 Inventory、播放游戏音效；
- 解析某个业务消息的字段并决定游戏结果；
- 直接访问 `SubsystemTerrain`、`ComponentPlayer`、`ComponentCircuit` 的私有字段；
- 为某个业务域维护自己的重传循环；
- 在热路径写详细诊断日志或创建临时 LINQ/闭包对象。

## 5. 外围模块职责

每个模块只实现一个业务域，并提供四类方法：

```text
Initialize(context)
Handle(message, source)
Tick(tickContext)
Reset(reason) / Dispose()
```

模块方法的具体含义：

- `Handle` 只解析该模块的 DTO，并生成领域意图或待应用事务；
- `Tick` 只推进本模块的有限队列，不主动驱动其他模块；
- `Reset` 只清理本模块拥有的状态，不能清空全局房间或其他客户端；
- 需要跨域协作时，通过端口或事件通知 CU，由 CU 排定顺序。

例如投掷物模块只负责“生成、同步、命中、表现、冷却”，不决定可靠队列；可靠通道由 `ReliableChannelCoordinator` 选择，主机权威执行由 `PlayerActionAuthorityModule` 调用。

## 6. 关键接口和依赖规则

### 6.1 依赖方向

```text
SuAPI/原版适配器 -> Domain/业务模块 -> Ports -> Transport/Comms
UI 适配器 --------> Control Unit -------> Modules
Message DTO ------> Codec/Registry ------> Transport
```

禁止反向依赖：

- Domain 不引用 Comms、Widget、SuAPI 反射对象；
- Message DTO 不引用 `ScMultiplayer`；
- 业务模块不引用另一个业务模块的具体实现类；
- UI 不直接写网络包；
- Transport 不判断地形、动物或玩家业务；
- 新模块不读取 `ScMultiplayer.currentInstance`，改用 `MultiplayerContext`。

### 6.2 状态所有权

每个可变集合必须只有一个所有者：

| 状态 | 所有者 |
|---|---|
| ClientID/PlayerIndex/连接生命周期 | `PlayerConnectionRegistry` |
| Join/Transfer/Catch-up | `Join` 模块 |
| 地形 revision、journal、预测 | `Terrain` 模块 |
| 电路 epoch、快照和电路事件 | `CircuitSyncModule` |
| 玩家档案、下线保存和皮肤 | `PlayerProfileModule` |
| 掉落物拾取请求 | `PickableSyncModule` |
| 可靠序列和窗口 | `ReliableChannelCoordinator` |
| HUD 的流量和重传采样 | `NetworkMetricsCollector` |

其他模块只能通过只读快照、命令或事件访问，不得保留第二份“权威副本”。

### 6.3 消息兼容

第一阶段不改变任何线上消息的字段顺序、消息名、显式 wire ID、DeliveryMode 或可靠序列语义。当前 `Message` 已使用显式 `Register<T>(id, name, revision)`，程序集扫描只用于发现遗漏类型；重构只增加只读快照和回归校验，不改注册表，不引入第二套消息体系。

## 7. 按业务域拆分的迁移表

| 现有方法群 | 目标模块 |
|---|---|
| `OnLoad`、`OnUnload`、`UpdateFrame`、`TriggerNetworkTick` | `ControlUnit`、`ModuleScheduler` |
| `CreateStartedClient`、端口绑定、断线/重连、`Client_*` 生命周期回调 | `TransportAdapter`、`RoomSessionModule` |
| `HandleHostJoinRequest`、审批窗口、Later、踢人 | `JoinApprovalModule` |
| `BeginWorldTransfer`、Chunk 分片、修复请求、校验和 | `WorldTransferModule` |
| `JoinCatchUpJournal`、journal、checkpoint 完成屏障 | `JoinCatchUpModule` |
| `SendGamePlayerPosition/Input/Health`、远程玩家表现 | `PlayerStateSyncModule`、`PlayerInputSyncModule` |
| `CaptureLocalPlayerInput`、挖掘/交互/攻击/投掷动作 | `PlayerActionAuthorityModule` |
| inventory、快捷栏、crafting、容器拖拽和物品扣除 | `InventoryAuthorityModule` |
| 生命、温度、升级、下线保存、角色皮肤 | `PlayerProfileModule`、`PlayerStateSyncModule` |
| `PublishTerrainChanges`、预测、恢复、Chunk revision | `TerrainSync` 四个子模块 |
| `ConfirmPendingFluidSettlements`、沙子/水/岩浆状态 | `FluidSettlementModule` |
| `SendAdaptiveAnimalUpdates`、动物声音/行为/攻击 | `AnimalSyncModule` |
| 掉落物创建、拾取请求和 0.5 秒保护 | `PickableSyncModule` |
| 投射物、火枪/弓箭/投矛冷却和命中 | `ProjectileSyncModule` |
| `WorldObjectSynchronizer` 内家具、标牌、编辑数据 | `WorldObjectSyncModule`、`EditableDataModule` |
| `CircuitSynchronizer` 和 `CircuitSyncMessage` | `CircuitSyncModule`、`CircuitAuthorityAdapter` |
| 时间、天气、雾、闪电、世界模式 | `WorldControlSyncModule` |
| `Show*Dialog`、IF/TA/MP、带宽设置 | `UI` 适配模块 |
| 玩家记录、地图材质、皮肤分片 | `SessionAssetModule`、`PlayerProfileModule` |

## 7.1 热点路径隔离

当前需要特别保护的热点路径不是所有网络代码，而是以下几条实际高频链路：

```text
Comms 输入 -> Client_GameStep/Client_DirectInput
           -> 消息路由 -> 有界业务队列 -> 游戏线程 Apply

游戏帧   -> UpdateFrame/TriggerNetworkTick
           -> 状态采样 -> 发送策略 -> Comms

地形     -> GameModifiedCells/Chunk/Recovery
           -> revision/checkpoint -> ChangeCell -> ACK

可靠通道 -> 重传/ACK -> ReliableChannelCoordinator
           -> 电路、玩家操作、加入传输的独立预算
```

热点路径的规则：

1. Comms 接收回调只做解码、来源校验、去重和入队，不在回调中创建实体、修改地形或修改 Inventory；
2. 游戏线程只在规定阶段消费有界队列，所有地形、实体、电路和玩家状态修改保持原时序；
3. CU 不重复遍历业务队列，模块自行报告 `HasPendingWork` 和预算消耗；
4. 位置、姿态、动物普通快照继续使用可替换状态，不进入可靠重传队列；
5. 地形 checkpoint、加入完成屏障、电路事件和玩家操作的可靠策略保持独立，不能共享一个无上限尾队列；
6. 任何新增模块必须给出每帧最大消息数、最大单元数、最大耗时和队列高水位；
7. 热点代码禁止 LINQ、每包字符串格式化、反射查找、临时闭包和无界日志；
8. 业务方法拆分只改变调用边界，不把逐格循环拆成大量委托调用。

## 7.2 异步化边界

异步化的目标是隔离慢 I/O 和压缩，不是把游戏状态改成多线程。

### 允许异步/后台执行

- 世界快照复制、ZIP 压缩/解压、校验和计算；
- 皮肤/材质资源读取、分片组装和落盘；
- 玩家记录、服务器审计和重传日志的批量写入；
- 非实时的协议快照生成和发布前包校验。

### 禁止异步化

- `ChangeCell`、地形碰撞和原版 Subsystem 更新；
- Entity 创建/删除、Inventory、crafting、拾取和投射物命中；
- 电路模拟、可靠序列分配、ACK 状态和 Comms 非线程安全 API；
- 任意需要保持消息顺序的 `Handle` 和 `Apply` 操作；
- `Task.Run` 逐包、逐方块或逐实体创建任务。

### 异步任务约束

1. 游戏线程先复制不可变输入快照，后台任务只处理快照；
2. 结果通过有界完成队列回到游戏线程，不能从后台直接写游戏对象；
3. 每个传输/会话拥有取消令牌，客户端退出后丢弃其未完成结果；
4. 队列达到高水位时停止接收新后台任务，回退到现有同步路径或延迟处理；
5. 后台任务失败必须回到既有错误/重试路径，不能静默改变房间状态；
6. 异步化前后必须比较 CPU、内存、GC、加入速度和空闲流量，不能只看主线程时间。

## 8. 分阶段实施计划（复审后顺序）

每个阶段均采用“新增旁路模块 -> 双路径对照 -> 切换一个业务域 -> 删除旧路径”的绞杀式迁移。没有对照数据时不切换。

### Phase 0：基线冻结和依赖盘点

- 建立当前消息 ID、字段顺序、发送模式和可靠序列快照；
- 为 Host、Client、Joining、Ready、Leaving 建立行为矩阵；
- 记录现有关键路径的包数、重传数、CPU、Apply 时间和内存；
- 为巨型类按方法群标注边界，不移动实现；
- 将 `CURRENT-TASKS.md` 中的地形漏块/恢复洪泛、Joining Room、ACK 停滞/重传、流量上涨、电路延迟和客户端退出清理转成可重复测试；
- 交付：架构图、消息黄金样本、行为回归清单、热点基线报告。

### Phase 1：建立 CU、Context 和端口

- 新增 `MultiplayerContext`、`ModuleTickContext`、`ModuleResult`；
- 新增 `INetworkTransport`、`IReliableChannel`、`IGameThreadDispatcher`；
- 将现有 Comms 调用包进 `CommsTransportAdapter`；
- `ScMultiplayer` 暂时作为兼容外壳，只负责组装和转发；
- 不移动 `CircuitSynchronizer`、`NetworkStateMachine`、`SuSubsystemTerrain` 和已稳定消息实现；
- 交付：不改变行为的 Release 构建、消息回环测试、热路径前后对照。

### Phase 2：先拆低风险控制和观察模块

- 迁移 `NetworkMetricsCollector`、`NetworkAuditModule`、带宽策略；
- 迁移房间状态、连接注册和加入审批；
- 迁移 MP/IF/TA UI，使 UI 通过命令端口调用 CU；
- 交付：连接、审批、流量 HUD 与当前版本一致。

### Phase 3：拆加入和资源传输

- 迁移世界快照、分片窗口、修复和 checksum；
- 迁移 join journal、checkpoint、CatchUpBatchComplete 屏障；
- 迁移皮肤、材质和网络地图资源的分片传输；
- 只有压缩、文件读写和校验和可以进入后台队列；Transfer 状态和完成屏障仍由游戏线程按原顺序推进；
- 交付：首次加入、断线重连、客户端退出不影响其他玩家，且加入速度和带宽不低于基线门限。

### Phase 4：拆玩家和物品事务

- 先迁移只读状态快照和位置/姿态；
- 再迁移输入、动作意图、主机权威确认；
- 最后迁移 inventory、快捷栏、crafting、容器和丢弃事务；
- 所有物品事务保留 request ID 去重、主机权威快照和客户端预测回滚；
- 交付：拖拽、Split、合成、投掷、拾取、受伤和下线保存回归通过。

### Phase 5：拆地形、电路和世界对象

- 地形先拆为实时广播、预测、恢复、Chunk checkpoint、流体结算；
- 电路单独拥有可靠序列，但只能通过可靠通道端口发送；
- 拆动物、掉落物、投射物、家具、编辑数据和天气/时间；
- 地形先只抽取队列和状态所有权，不重写已经稳定的 `SuSubsystemTerrain` 应用时序；
- 电路算法先保持原文件，仅把发送策略和完成回调接到端口；
- 交付：A -> Host -> B 的地形、实体和电路一致性测试通过，可靠队列不会被地形恢复占满。

### Phase 6：清理兼容外壳和固化架构

- 将 `ScMultiplayer.cs` 缩减为入口、CU 组装、生命周期和兼容转发；
- 删除重复状态、重复判断、旧 handler 和已迁移的静态方法；
- 删除跨模块直接引用，补齐依赖方向检查；
- 更新 `SCMULTIPLAYER-ARCHITECTURE.md`、`INTERFACES.md` 和 README；
- 交付：Release 包、协议兼容报告和性能对比报告。

## 9. 判断和方法拆分规范

将不影响性能的多级判断改为命名方法和守卫式返回：

- `IsHostRole`、`IsClientReady`、`CanHandleLiveMessage`、`CanApplyCheckpoint`；
- `ShouldSendReliable`、`ShouldReplaceLatest`、`ShouldDeferUntilJoinReady`；
- `TryGetPlayerByClientId`、`TryResolveWorldObject`、`TryBuildAuthoritativeResult`；
- `HasPendingWork`、`CanSpendFrameBudget`、`ShouldRequestTerrainRepair`。

规则：

1. 纯判断方法优先 `static`、无分配、无日志、无网络副作用；
2. 判断方法不得隐藏昂贵 I/O、反射或游戏对象创建；
3. 热路径中的逐格地形循环、实体扫描和序列化保持批量实现，不拆成委托/闭包/LINQ；
4. 模块之间用明确命令和结果传递，不通过多个布尔字段互相修改状态；
5. 新增或迁移的方法只做一个可命名动作；已有稳定方法不因行数单独触发重写；
6. 300-800 行是模块的观察区间，超过约 1,200 行只有在存在真实职责边界时才继续拆分；
7. CU 只做调度，不能因为拆方法而增加每帧重复遍历或重复序列化。

## 10. 线程、可靠性和性能不变量

以下规则在整个重构期间不可改变：

- 所有游戏对象修改仍在游戏线程/规定的帧阶段执行；
- Comms 的线程安全和锁边界不外泄；
- 电路可靠序列保持独立冗余，不被地形、动物或加入传输占满；
- 地形 checkpoint 只有在所有 Data 实际落地后才提升 revision；
- 可替换状态不进入可靠重传队列；
- joining client 不接收普通实时可靠广播；
- 客户端退出只清理自身状态；
- 主机权威事务仍使用 request ID 去重和安全锁；
- 热路径不增加反射查找、字符串格式化、频繁日志或大对象分配。

## 10.1 已知网络问题的模块归属

重构不是重新猜测业务逻辑，而是将已有证据放到唯一责任边界中：

| 已知问题 | 第一责任模块 | 不允许的修复方式 |
|---|---|---|
| A -> Host -> B 漏块、Chunk 恢复洪泛 | `TerrainRecoveryModule`、`TerrainChunkCheckpointModule`、`ReliableChannelCoordinator` | 不能用全图重发、无界可靠队列或降低电路优先级掩盖问题 |
| 首次加入卡 `Joining Room`、角色界面反复切换 | `RoomSessionModule`、`WorldTransferModule`、`JoinCatchUpModule` | 不能在 UI 层强制关闭窗口或绕过 CatchUp 屏障 |
| ACK 停滞、`retry=30`、可靠尾队列阻塞 | `ReliableChannelCoordinator`、`NetworkMetricsCollector` | 不能把所有消息改成不可靠，也不能让电路失去可靠序列 |
| 空闲流量从约 30 Kbps 长到 160 Kbps | `LatestStateChannel`、`TerrainSyncModule`、`AnimalSyncModule` | 不能只隐藏 HUD 数值，必须按实际 UDP 包/字节和重传来源定位 |
| 电路视觉先变、声音迟滞或同步被占用 | `CircuitSyncModule`、`ReliableChannelCoordinator` | 不能把电路塞进普通地形恢复队列 |
| 客户端退出后其他玩家受影响 | `PlayerConnectionRegistry`、各模块的 `Reset(clientId)` | 不能调用全局 Reset 或清空全房间状态 |
| 沙子/水/岩浆/火焰最终状态不一致 | `FluidSettlementModule`、`TerrainAuthorityAdapter` | 不能由客户端单独执行邻近更新并期待主机自动追上 |

每个问题必须先有现象、包/序列证据和回归用例，再决定是否需要移动代码。模块化本身不是修复结果。

## 11. 测试和验收

### 协议/模块契约测试

- 所有现有消息的读写回环和黄金字节样本保持一致；
- 同一消息在旧包与新包之间字段顺序和 DeliveryMode 一致；
- 控制单元只路由一次，重复消息由所属模块去重；
- 模块 Reset 不影响其他模块的状态。

### 联机集成测试

- Host、Client A、Client B：挖掘、放置、沙子/水/岩浆、火焰和爆炸；
- 玩家 A 操作后 B 最终地形和碰撞一致，不重连也能定向恢复；
- 加入地图、断线重连、客户端退出、主机退出和审批 Later；
- inventory、Split、crafting、容器、丢弃、拾取和投掷物；
- 动物攻击/受伤/声音、投射物命中、家具、电路和世界控制；
- 外部皮肤和网络材质只随会话同步，不污染本地全局资源。

### 性能和网络测试

- 空闲、单玩家、双客户端、四客户端分别记录 UDP 实际包数/字节、重传、Apply 时间和帧率；
- 爆炸/岩浆/大面积地形恢复期间，确认可靠队列、电路序列和玩家操作有冗余；
- 高延迟、短暂丢包、重复包和连续重传下，模块队列有界并能回落；
- 对允许后台的世界压缩、资源读取、日志批量写入分别比较主线程时间、GC、队列长度和失败回退；
- 验证后台任务取消后不会写入已退出客户端或已切换世界；
- 统计 CU 到业务模块的调用次数，不能因分层出现每帧重复扫描或重复序列化；
- Release 构建禁止引入 Debug DLL、诊断热路径和额外平台目录。

### 发布验收

- `dotnet build -c Release` 0 警告、0 错误；
- `.scmod` 仍为 `IsMergeLib=true`，根目录 `ModInfo.xml`，扁平 `Lib/`；
- 不打包 Engine、EntitySystem、Survivalcraft 游戏程序集；
- Windows、Android 使用同一份 Mod DLL；
- 记录包 SHA-256、协议版本和回归结果后再发布。

## 12. 风险和回滚策略

- 每个 Phase 单独提交，禁止将多个业务域的大迁移合并为一个不可回滚提交；
- 每次迁移保留旧入口转发层，确认新模块结果后再删除旧实现；
- 任何协议黄金样本、地形一致性或电路可靠序列回归，立即回退当前 Phase，不回退已经验证通过的前序 Phase；
- 不在重构同时升级 Comms、改变协议 ID 或修改原版游戏程序集；
- 临时诊断只允许带 `[SuAPI]` 前缀，验证完成立即移除。

## 13. 首轮评审需要确认的事项

1. 是否接受 `ScMultiplayer.cs` 作为兼容外壳，并将网络控制集中到 `Control/`；
2. 是否接受按 Session -> Join -> Player -> Terrain -> Entity/Circuit -> UI 的迁移顺序；
3. 是否接受“模块化单体、真实边界才抽象、不新增通用事件总线”的原则；
4. 是否接受只对世界传输、资源、日志和校验和进行异步化，游戏状态和可靠序列保持单线程；
5. 是否接受 SMART 门禁：流量/CPU/Apply/分配不超过基线 5%，20 次加入成功，10,000 次地形修改无最终不一致；
6. 是否需要在第一阶段加入自动化架构依赖检查，阻止业务模块直接引用 Comms 或 `ScMultiplayer`。

## 14. 执行记录

### 2026-08-07：Phase 0 审计与 Phase 1 最小骨架

- 已补充消息安全边界、热点分派分配、静态状态、Android/AOT、异常隔离、测试基础设施和发布兼容检查；
- 已新增 Mod-only 的 `Core/`、`Control/`、`Ports/` 最小骨架；
- `ScMultiplayer` 只在 `OnLoad`、`UpdateFrame`、`OnUnload` 接入控制单元，当前没有注册业务模块，不改变网络包和游戏行为；
- 已保留原版继承、SuAPI 注入、现有消息显式 wire ID 和稳定算法；
- Release 构建通过，0 警告、0 错误；已完成最小 CU/Context/端口骨架。
- 已将 `NetworkMessageHandler` 和 `NetworkMessageSender` 从巨型入口迁移到 `Networking/`；发送算法、批处理阈值、可靠/可替换参数和协议字段未改变。
- 已新增 `NetworkMessageIngress`，将输入解码和本机回环过滤从 `Client_GameStep` 分离；业务 Apply 仍在原有帧尾队列中执行。
- 已新增 `INetworkTransport` 与 `CommsTransportAdapter`，发送器通过 Mod-only 端口转发到现有 Comms Client，不引入第二套重传或可靠序列。
- 已将 `SessionStateModule` 注册到 `ModuleScheduler`，会话快照由首个实际外围模块推进；其状态转换与原先 `MultiplayerControlUnit.Tick` 等价。
- 已将 HUD 的 UDP 字节差分采样迁移到 `Diagnostics/NetworkMetricsCollector`；RTT、可靠队列、重传率和 UI 更新时序保持在原有 `ReadNetworkStats` 路径。
- Release 构建通过，0 警告、0 错误；正式包已更新为 `publish/Windows/Mods/[SuAPI]ScMultiplayer-2.0.8.scmod`，包内含根 `ModInfo.xml`、扁平 `Lib/ScMultiplayer.dll` 和 `Lib/Comms.dll`，SHA-256 为 `3213801a581eb4bf5d88ce6a13ca6c2a55b5531a79e0b566b518f8f22bee25ca`。
- 首次执行 `publish/Windows/Survivalcraft.exe` 短时启动验证时，发布包未包含 `Comms.dll`，日志报告过缺失依赖；该问题不是 ScMultiplayer 编译错误，随后按历史包结构补入同包 `Lib/Comms.dll`。
- 重新打包 `Comms.dll` 后启动验证通过：发布目录不再产生新的 `Comms.dll` 缺失错误，日志加载的协议为 Mod `2.0.8`、Protocol `v1`；旧日志中的历史 2.0.7/缺失错误未清理。
- 本轮未移动地形、电路、加入、玩家和物品事务实现；下一阶段继续迁移低风险观察/连接状态模块，并以现有包作为可回滚基线。

### 2026-08-07：Phase 2 连接生命周期注册表

- 诊断基础已通过 Release 构建后，满足进入 Phase 2 的门禁：协议字段、wire ID、DeliveryMode、可靠序列和原版游戏程序集均未改变；当前诊断队列仍为有界、帧尾刷新。
- `DiagnosticRecord`、有界 `DiagnosticRecorder`、`IDiagnosticSink` 和 `DiagnosticsModule` 已作为 Phase 2 的观察基础接入；UDP/Comms 回调只入队固定记录，格式化与既有 Headless EventBus 写入仅在帧尾有界 drain 中执行，Router 错误按秒限流。
- 新增 `Core/PlayerConnectionRegistry`，集中保存 ClientID、PlayerIndex 快照、端点文本、主机标记、Reserved/Joining/Ready/Disconnected 阶段和最近转换时间；`Snapshot()` 只供低频观察/UI 使用，不进入 Comms 回调或逐帧热路径。
- 新增 `Modules/Session/PlayerConnectionRegistryModule`，由 CU 管理生命周期 Reset；现有 Client 创建/加入、主机审批、世界传输完成、GameStep 离开和断线路径已写入注册表。
- 兼容边界保持明确：`PlayerMappingManager` 仍是玩家索引分配的唯一现有实现，注册表只记录快照，尚未替换索引分配或改变 Join 审批；下一步必须先完成调用点盘点和双路径对照，再迁移索引所有权。
- 本阶段不新增周期扫描、网络包或日志输出；连接注册为 O(1) 字典更新，ActiveCount/Snapshot 仅在观察路径使用。
- Release 包已按 Python `zipfile` 重建并校验：`publish/Windows/Mods/[SuAPI]ScMultiplayer-2.0.8.scmod`，`testzip()` 通过，SHA-256 为 `23d16364381deb1e30f257b6a22d4c906a749d7c2e6dee48a0209cb66a3f83f0`。
- 启动初始化验证通过：最新记录加载 Mod `2.0.8`、Protocol `v1`、协议哈希 `a547411b4219`，并成功启动联机 Server；该记录之后没有新的 Mod 加载错误或 `Comms.dll` 缺失错误。测试进程随后在 `Engine.Window.get_ScreenSize` 的原版窗口初始化处退出，属于当前无窗口运行环境的 Engine 异常，不是本轮 Mod 注册表代码路径。

### 2026-08-07：Phase 2 房间状态快照

- `MultiplayerSessionState` 现在集中保存 ClientID、GameID、服务端端点、世界名和 `IsWorldReady`；`EnterRoom`、`MarkWorldReady`、`Reset` 是唯一状态转换入口。
- `ScMultiplayer.IsInRoom` 已从直接读取 Comms Client 改为读取 CU 会话快照；Client 创建/加入、世界 Catch-up 完成和断线清理均更新同一份状态。
- `SessionStateModule.Update` 保留原有 Host/Client/Detached 角色判断，并在客户端 Ready 后保持 Ready，不会被每帧状态采样退回 Joining；没有新增网络包、可靠序列或游戏对象操作。
- Release 构建通过，0 警告、0 错误；正式包 `publish/Windows/Mods/[SuAPI]ScMultiplayer-2.0.8.scmod` 已重建，根 `ModInfo.xml`、扁平 `Lib/ScMultiplayer.dll` 和 `Lib/Comms.dll`，`testzip()` 通过，SHA-256 为 `bf09b885898be7aa6ef8cccf88228d3238a9f9e1c59a2868d338e613dda6f823`。
- 启动日志确认协议仍为 `2.0.8 / v1`、协议哈希 `a547411b4219`，Server 成功启动；随后退出仍发生在无窗口环境的原版 `Engine.Window` 初始化处，未出现新的 Mod 加载或 Comms 依赖错误。

### 2026-08-07：Phase 2 UI 命令端口

- 新增 `Ports/IMultiplayerUiCommandPort`，将 CR/TA/MP/IF 的房间查询、创建房间、聊天、多人管理和玩家列表命令定义为只读/命令端口。
- `MultiplayerUiComponent` 通过端口调用现有方法，按钮文本、Enter 聊天热键、Dialog 判断和调用顺序保持不变；UI 不接触 Comms、消息 DTO 或可靠通道。
- 现有 `ScMultiplayer` 方法保留为兼容实现，端口只是边界适配，不新增事件总线或第二套 UI 状态。
- Release 构建通过，0 警告、0 错误；正式包已重建，`testzip()` 通过，SHA-256 为 `a11aa7b7685a4b98999bad038013219e72d2443fbb90e28487d46de0bc22f5c3`。
- 启动日志确认 `2.0.8 / v1`、协议哈希 `a547411b4219`，Server 成功启动且没有新的 Mod 加载或 Comms 依赖错误。
- `WorldTransferRegistry` 进一步接管 Join barrier 标量：客户端地形 Join 基线、chunk 基线、待完成 TransferID、Circuit Ready TransferID 和 Ready 阶段；旧字段改为兼容访问器，原有清零和比较顺序保持不变。
- Release 构建通过，0 警告、0 错误；正式包 SHA-256 更新为 `be62bdab817999fac0b499f75d3efe7e97add7ee17ecde527bea4113a65f4fa0`，ZIP 结构和 `testzip()` 通过。
- 启动日志仍确认 `2.0.8 / v1`、协议哈希 `a547411b4219` 和 Server 成功启动；无新的 Mod 加载或 Comms 依赖错误。
- 新增 `Core/JoinCatchUpRegistry`，集中持有按 ClientID 索引的 Catch-up journal 和待发送队列；原有 Catch-up replay、完成回调、发送预算和清理时序保持不变。
- Release 构建通过，0 警告、0 错误；正式包 SHA-256 更新为 `a4c8e8397c51f4dd501cbf724a419ca3350da9cefeba17ee39e5a0b7ba1eda7c`，ZIP 结构和 `testzip()` 通过。
- 启动验证通过：进程保持运行至测试结束，日志确认 `2.0.8 / v1`、协议哈希 `a547411b4219`、Server 成功启动，无新的 Mod 加载或 Comms 依赖错误。
- Join 注册表进一步接管三组按 ClientID 索引的 Ready barrier：等待 Ready、Host Project Ready、已完成 Ready；客户端和主机的重试/补发判断仍使用原字段访问器，未改变完成屏障。
- Release 构建通过，0 警告、0 错误；正式包 SHA-256 更新为 `c64744d5185f39befac1f48615215daae879be505206722a435a6f0a0c9f5aaa`，ZIP 结构和 `testzip()` 通过。
- 启动日志确认 `2.0.8 / v1`、协议哈希 `a547411b4219`、Server 成功启动，无新的 Mod 加载或 Comms 依赖错误。
- 新增 `Core/WorldSnapshotFileCopier`，将临时世界目录复制和文件流复制从玩家/Join 业务处理器中移出；调用者仍在游戏线程完成 SaveProject、ExportWorld 和临时目录清理，尚未异步化。
- 合约检查器已增加快照复制器和禁止依赖检查，执行结果为 `refactor contracts: OK`。
- Release 构建通过，0 警告、0 错误；正式包 SHA-256 更新为 `687833f4a4323f4498d7870be15c04bee4f141de4ffe006116607e3df35c110d`，ZIP 结构和 `testzip()` 通过。
- 启动验证通过：进程保持运行至测试结束，日志确认 `2.0.8 / v1`、协议哈希 `a547411b4219`、Server 成功启动，无新的 Mod 加载或 Comms 依赖错误。
- 新增 `Tools/verify_refactor_contracts.py`，在没有现成测试工程的前提下检查 Transfer/Catch-up/Asset 容器、兼容访问器、Core 依赖边界和正式包结构；本轮执行结果为 `refactor contracts: OK`。该脚本不进入 `.scmod`，不修改运行时状态。
- `SessionAssetRegistry` 进一步接管网络地图会话资源引用；`Texture2D` 创建、替换、Dispose 和项目绑定仍在原处理器中执行，避免改变渲染资源生命周期。
- Release 构建通过，0 警告、0 错误；正式包 SHA-256 更新为 `a3ab63c86ee1848869b2db54a14c590c4519491220ab36848a78655a8a82451f`，ZIP 结构和 `testzip()` 通过。
- 启动日志确认 `2.0.8 / v1`、协议哈希 `a547411b4219`、Server 成功启动，无新的 Mod 加载或 Comms 依赖错误。
- 新增 `Core/SessionAssetRegistry`，接管会话皮肤哈希、请求去重、入站皮肤分片和已发送哈希集合；网络地图材质对象及其释放仍由原适配器负责。
- Release 构建通过，0 警告、0 错误；正式包 SHA-256 更新为 `0b2c205582dba3a01adfb8dd9615d8a3ed9e2cee9ae87c3663550faeb4a94446`，ZIP 结构和 `testzip()` 通过。
- 启动日志确认 `2.0.8 / v1`、协议哈希 `a547411b4219`、Server 成功启动，无新的 Mod 加载或 Comms 依赖错误。

### 2026-08-07：Phase 3 世界传输状态容器起步

- 新增 `Core/WorldTransferRegistry`，集中持有出站和入站世界分片集合；旧处理器通过兼容访问器继续使用原有字典语义，未改变分片顺序、窗口预算、修复请求、checksum 或 ACK/重传策略。
- 暂不迁移 Join barrier、Catch-up journal、压缩和文件 I/O；这些仍由现有游戏线程处理器控制，下一步先为 TransferID、Ready 阶段和 journal 建立契约测试再移动。
- Core 容器不引用 Comms、Engine 或 UI；入站集合按 TransferID 管理，没有新增按 ClientID 的错误清理路径。
- Release 构建通过，0 警告、0 错误；正式包已重建，`testzip()` 通过，SHA-256 为 `9b04bb1f5104658ac3cd58d4391932d2b38b89f0e1273531a6c8df58e8b3ab32`。
- 启动日志确认 `2.0.8 / v1`、协议哈希 `a547411b4219`，Server 成功启动且没有新的 Mod 加载或 Comms 依赖错误。

### 2026-08-07：剩余迁移的控制边界与发布重建

- `MultiplayerControlUnit` 已从只调度会话快照扩展为固定顺序的八阶段调度：Session、JoinTransfer、WorldControl、Circuit、World、Player、Entity、UI；每阶段通过 `IMultiplayerRuntimeHost` 调用兼容宿主，不直接访问游戏对象或 Comms。
- `UpdateFrame` 已收敛为 CU 入口。原有调用顺序保持不变：会话/重连 -> 加入传输/修复 -> 世界控制 -> 电路绑定 -> 世界模拟 -> 玩家暂停状态 -> 实体表现 -> 远程玩家渲染；帧尾队列仍由原有 `ProcessEndOfFrameActions` 在同一阶段消费。
- `Client_GameStep` 的正常输入已切换到 `Control/NetworkMessageRouter.cs`。Router 统一执行来源端口过滤、消息解码、SyncBatch 展开和嵌套批次拒绝，并按原有优先输入、世界传输、Chunk、帧尾队列分派；旧 switch 仅作为初始化失败时的兼容回退。
- `Core/ModuleResult.cs`、`Core/RuntimeStateOwnerRegistry.cs` 和 `Ports/ModulePorts.cs` 已建立模块结果、会话/加入/玩家/地形/实体/电路/世界/UI 状态域和 `IReliableChannel`、`IGameThreadDispatcher`、`IAuthoritativeWorld`、`IWorldSnapshotStore`、`IPlayerStateStore` 端口。
- `Transport/ReliableChannelCoordinator.cs` 和 `Transport/LatestStateChannel.cs` 已成为可靠策略与可替换状态的唯一 Mod 端端口；现有 `NetworkMessageSender` 仍是实际发送算法所有者，未引入第二套 ACK、重传或可靠序列。
- 业务实现仍保留在已验证的 `Modules/*` partial 兼容外壳和原版 SuAPI 适配器中，避免为模块化复制 `SubsystemTerrain`、实体、电路或库存实现。下一步只在有回归证据时继续把单一职责方法移动到独立类。
- 重新构建前执行了 `taskkill /F /IM Survivalcraft.exe /T` 并触碰所有修改的 C# 文件；ScMultiplayer Release 构建通过，0 警告、0 错误。正式包 `publish/Windows/Mods/[SuAPI]ScMultiplayer-2.0.8.scmod` 已按 Python `zipfile` 重建，包含根 `ModInfo.xml`、扁平 `Lib/ScMultiplayer.dll` 和 `Lib/Comms.dll`，`testzip()` 通过，SHA-256 为 `4676180f8c61a16f643c94459b811dd4368a0605a5026a10c50fec3bf66f37a3`。

### 2026-08-07: Phase 3 world archive sanitization boundary

- Added `Modules/Join/WorldArchiveSanitizer`, which owns the ZIP/XML filtering needed when exporting a hosted world. It removes network-player entries from `Project.xml` while leaving archive traversal and byte ownership outside the player/Join compatibility shell.
- `ScMultiplayerPlayerHealthAndIngress` now delegates this operation through `WorldArchiveSanitizer.RemoveNetworkPlayers(...)`; the former inline archive/XML helpers were removed. Save, export, cleanup timing, and game-thread ownership are unchanged.
- The refactor contract checker now verifies the sanitizer boundary and its `Project.xml` filtering contract. No protocol fields, wire IDs, delivery modes, reliable sequence behavior, or original game assemblies were changed.
- Release build passed with 0 warnings and 0 errors. The package was rebuilt from the Release obfuscated DLL and validated with `zipfile.testzip()`; SHA-256 is `f8108855fd6d46d1f63960c540f99cb2e0c7ade4988cf285d047cb92cedcd571`.
- `verify_refactor_contracts.py` returned `refactor contracts: OK`. The publish-directory startup check loaded Mod `2.0.8`, Protocol `v1`, hash `a547411b4219`, and started the local Server without new Mod loading or `Comms.dll` dependency errors. The process then exited with the known headless/no-window Engine initialization code `0xC0000005`; this is outside the sanitizer path.

### 2026-08-07: Phase 3 player record key boundary

- Added `Modules/Player/PlayerRecordKeyResolver`, a pure formatter for persistent player identity keys and network-player keys. Profile, player-health, world-transfer, and runtime callers now use this boundary instead of private helpers hidden in `ScMultiplayerProfileHandlers`.
- The resolver does not read or mutate player entities, inventories, session state, messages, or transport state; generated key strings and fallback rules are unchanged.
- The contract checker now verifies the resolver and both key methods. Release build passed with 0 warnings and 0 errors; the package remains `2.0.8`, `IsMergeLib=true`, and contains only the root `ModInfo.xml` plus flat `Lib/ScMultiplayer.dll` and `Lib/Comms.dll`.
- `verify_refactor_contracts.py` returned `refactor contracts: OK`; package `testzip()` passed. Current package SHA-256 is `e46fe67e6ff9a33fd72a2d222091aa1474391a55aadf7574d1aa568cf43bff5b`.
- A fresh publish-directory startup check again logged Mod `2.0.8`, Protocol `v1`, hash `a547411b4219`, and `Server started OK`; the only termination was the known no-window `Engine.Window.get_ScreenSize` null-reference in the original game executable.
- After CRLF normalization and the final package rebuild, the same startup check logged `2.0.8 / v1`, `Server started OK`, and no new Mod or Comms errors; the known original Engine no-window exception remained the only termination.

### 2026-08-07: Phase 3 player profile value codec boundary

- Added `Modules/Player/PlayerProfileValueCodec`, which owns the pure XML value conversions used by player records: invariant float formatting/parsing, `PlayerClass` parsing, boolean parsing, semicolon-separated integer arrays, and the four-slot empty-clothing shape.
- `ScMultiplayerProfileHandlers` and the world-object diagnostic label now call the codec explicitly. The XML attribute names, fallback values, separators, culture, clothing slot count, and save/load order are unchanged; entity capture, restore, and game-thread ownership remain in the existing handlers.
- Removed the now-unused profile-local `System.Globalization` dependency. No protocol field, wire ID, delivery mode, reliable sequence, inventory rule, or original game assembly was changed.
- The contract checker now verifies the codec boundary. Release build passed with 0 warnings and 0 errors; `verify_refactor_contracts.py` returned `refactor contracts: OK`; package `testzip()` passed.
- Current formal package SHA-256: `ba6b5e2029957383c812256187ed48dc47c5a94ae0e31d573167eb9042d39a81`.
- Final publish-directory startup check kept the process alive through initialization, logged Mod `2.0.8`, Protocol `v1`, `Server started OK`, database hooks, and both service-DNS sources; it was then stopped by the verification harness without a new Mod or Comms error.

### 2026-08-07: Phase 3 skin hash representation boundary

- Added `Modules/Player/SkinHashCodec`, which owns byte cloning, 32-byte non-zero SHA-256 validation, lowercase hexadecimal formatting, and hexadecimal parsing.
- Profile and world-transfer handlers now use the codec for skin hashes and asset chunk copies. Local file access, image validation, `CharacterSkinsManager`, session asset ownership, and network transfer timing remain in the existing handlers.
- The contract checker now verifies the skin hash boundary. No skin message fields, chunk ordering, reliable sequence, resource lifetime, or gameplay behavior changed.
- Release build passed with 0 warnings and 0 errors; `verify_refactor_contracts.py` returned `refactor contracts: OK`; package `testzip()` passed.
- Current formal package SHA-256: `b51551266ee5b03b73207de0ee669f17082a16b8645834436503c1a69ac12ebd`.
- Final publish-directory startup check remained alive through initialization, logged `2.0.8 / v1`, `Server started OK`, database hooks, and both service-DNS sources; no new skin or Comms loading error was emitted.

### 2026-08-07: Phase 3 bounded skin asset reader boundary

- Added `Modules/Player/BoundedStreamReader`, which reads a stream into memory under the caller-provided byte limit and preserves the existing null, seekable-length, and over-limit exception behavior.
- `TryReadLocalCharacterSkinAsset` now uses the reader; stream ownership remains with the existing `using` scope, while image validation, hashing, file lookup, and network asset handling remain unchanged.
- The contract checker now verifies the bounded reader and its limit contract. No asset size limit, message field, chunk sequence, reliable queue behavior, or game-thread timing changed.
- Release build passed with 0 warnings and 0 errors; `verify_refactor_contracts.py` returned `refactor contracts: OK`; package `testzip()` passed.
- Current formal package SHA-256: `ced8fe0371120a228475c7d8c1384ee54a56da35007ec6388a28693ee298b29e`.
- Publish-directory startup logged Mod `2.0.8`, Protocol `v1`, `Server started OK`, and discovery initialization with no new asset or Comms error; the process then hit the known original no-window `Engine.Window.get_ScreenSize` exception.

### 2026-08-07: Phase 3 skin image validation boundary

- Added `Modules/Player/SkinImageValidator`, which owns character-skin payload size, image dimension, and power-of-two checks. Character class compatibility remains in `ScMultiplayerProfileHandlers`, where `CharacterSkinsManager` is already owned.
- `ValidateSkinAssetData` now delegates only the image-data portion; error text, 256x256 limit, power-of-two rule, maximum payload, and validation order are unchanged.
- The contract checker was corrected to apply Engine/Comms dependency restrictions only to `Core/*`; module-level adapters such as this validator may use their required game APIs.
- Release build passed with 0 warnings and 0 errors; `verify_refactor_contracts.py` returned `refactor contracts: OK`; package `testzip()` passed.
- Current formal package SHA-256: `bddc5527f455b0f6d6d08b3198dc23795ca58741f253e973974ba2b950bca1ef`.
- Final publish-directory startup logged `2.0.8 / v1`, `Server started OK`, database hooks, and both service-DNS sources with no new skin or Comms error; the known no-window Engine exception remained outside the Mod path.

### 2026-08-07：Phase 3 注册表 Reset 生命周期集中化

- `WorldTransferRegistry` 新增 `ResetClientTerrainBaselines()` 和 `RemoveClient(int)`；`JoinCatchUpRegistry` 新增 `RemoveClient(int)`，单客户端离开不再由业务处理器逐项清理五组状态。
- 全局退出/换图路径改为统一调用 `WorldTransferRegistry.Reset()` 与 `JoinCatchUpRegistry.Reset()`；Ready TransferID、Circuit TransferID、地形基线和 Ready 阶段的清零顺序保持不变。
- `SessionAssetRegistry` 新增 `DetachWorldSessionAssets()`；`ClearSessionAssets()` 先脱离并释放非内容纹理，再调用注册表 `Reset()`，没有改变纹理所有权或项目绑定时序。
- 本轮没有改变协议字段、wire ID、DeliveryMode、可靠序列、ACK/重传、Chunk 顺序、压缩方式或游戏线程 I/O；压缩、文件复制、checksum 和纹理应用仍保持原有同步边界。
- `verify_refactor_contracts.py` 返回 `refactor contracts: OK`；ScMultiplayer Release 构建通过，0 警告、0 错误。
- 正式包 `publish/Windows/Mods/[SuAPI]ScMultiplayer-2.0.8.scmod` 已按 Python `zipfile` 重建，条目为根 `ModInfo.xml`、扁平 `Lib/ScMultiplayer.dll`、`Lib/Comms.dll`，`testzip()` 通过；SHA-256 为 `058b555fb3914535f5211079f01eba585baeed31043ee5b5279a0cff6c7cab2f`。
- 发布目录启动级检查通过：日志加载 `2.0.8 / v1`、协议哈希 `a547411b4219`、`Server started OK`、数据库钩子和两个服务 DNS；检查结束后已停止测试进程。
- Phase 3 尚未宣告完成：仍需真实双端验收首次加入、断线重连、A→Host→B 地形同步、Catch-up/Ready barrier 和客户端退出不影响其他玩家；这些运行时门禁不能由静态构建替代。

### 2026-08-07：Phase 3 原样传输端口收敛

- `NetworkMessageSender` 新增 `SendRawPayload` 和 `SendRawMessage`，只封装现有 `INetworkTransport.SendDirectInput`，不新增队列、重传、批处理或 DeliveryMode。
- Join Catch-up、Terrain Chunk、Terrain Recovery、Chunk 修复和动物交互模块改用发送器端口；Modules 中不再直接依赖 `client/server.SendDirectInput`。
- `SendLiveBroadcastToReadyClients` 的加入状态判断和 Ready 客户端筛选仍保留在原兼容宿主中，本轮没有移动稳定的加入策略。
- 契约检查新增 Modules 直连 Comms 发送的禁止规则；`verify_refactor_contracts.py` 返回 `refactor contracts: OK`。
- Release 构建通过，0 警告、0 错误；正式包 `testzip()` 通过，SHA-256 更新为 `7428d77a1e9e738b647b1867b36f2fbe35cc266a34f2580bdfaad93e3db1a686`。

#### Phase 3 剩余审计

- `ScMultiplayerUpdateLoop` 仍拥有加入带宽采样、自动调速、token bucket、unacked 窗口和共享带宽余量计算；这些逻辑会读取实时 Comms RTT/丢包/可靠队列，不能在没有等价指标端口和回归数据前机械搬移。
- `ScMultiplayerPlayerHealthAndIngress` 仍负责 Catch-up journal 的游戏线程排队、发送预算扣减、完成统计和客户端退出清理；注册表已接管状态所有权，但发送窗口推进仍需保持现有帧序。
- `ScMultiplayerWorldTransferHandlers` 仍负责 ZIP 导入、纹理应用、checksum 失败恢复和 Ready barrier 的游戏线程执行；纯文件/资源辅助边界已拆出，剩余部分需要双端回归后再拆。
- `SessionAssetRegistry` 已接管皮肤哈希和分片缓存，但 `Texture2D` 创建、项目绑定和释放仍由原适配器拥有，这是有意保留的资源生命周期边界。
- 当前尚未进入 Phase 4 的玩家/库存事务重构，也没有改变 `NetworkMessageSender` 的可靠策略或任何游戏程序集。

### 2026-08-07：Phase 4 只读玩家状态快照边界

- 新增 `Core/PlayerReadOnlyStateSnapshot`，只承载位置、旋转、速度、视角和姿态标志；它不包含输入、动作、库存或主机权威结果，也不改变任何网络字段。
- 新增 `Modules/Player/PlayerReadOnlyStateCapture`，统一从 `ComponentPlayer` 捕获上述只读状态，并在发送前执行有限值检查；原有 `GamePlayerPositionMessage` 字段顺序、wire ID、DeliveryMode 和发送批次保持不变。
- 玩家位置发送端的本地玩家和主机权威远程玩家均使用同一采集边界；接收端使用同一快照校验并更新 `NetworkPlayerState`，本地预测仍只在原有受击修正窗口内调整速度，不改写位置、旋转或视角。
- 没有迁移输入、动作确认、库存/快捷栏、Split、crafting、容器或丢弃事务；没有新增可靠队列、ACK/重传、预测回滚或游戏线程 Apply 阶段。
- `verify_refactor_contracts.py` 增加 Phase 4 快照边界检查。Release 构建通过后再重建正式包；首次加入、断线重连和双端运行时门禁仍需真实设备回归，不能由静态检查替代。

### 2026-08-07：Phase 4 纯策略与权威状态边界

- 新增 `Core/AuthoritativePlayerStateSnapshot`，将生命、空气、食物、体力、睡眠、温度、湿度、等级和睡眠标志的比较逻辑从运行时状态文件移出；快照仍只由主机生命同步模块捕获和发送。
- 新增 `Modules/Player/PlayerInputStatePolicy`，集中网络输入清洗和一次性动作消费后的保持输入规则；`ScMultiplayerClientEvents` 仍负责接收顺序、动作队列、主机应用和安全锁。
- 新增 `Transport/JoinTransferBudgetPolicy`，集中加入传输的可用带宽、token refill、包大小估算和退款纯计算；Comms 采样、发送窗口、重传和游戏线程时序仍由原运行时模块拥有。
- 本轮没有改变消息字段、wire ID、DeliveryMode、可靠序列、ACK/重传、库存事务、预测回滚或主机权威动作顺序；策略类不引用 Comms、游戏实体或 UI。
- Phase 4 仍未迁移输入接收、动作确认和 inventory/crafting/container/drop 事务；这些必须在双端回归证据后分小步迁移。

### 2026-08-07：Phase 4 本轮验证

- ScMultiplayer Release 构建通过，0 警告、0 错误；构建前已停止 `Survivalcraft.exe` 并触碰本轮修改的 C# 文件。
- `verify_refactor_contracts.py` 返回 `refactor contracts: OK`；所有修改的 C# 文件行尾为 CRLF，`git diff --check` 通过。
- 正式包 `publish/Windows/Mods/[SuAPI]ScMultiplayer-2.0.8.scmod` 已按 Python `zipfile` 重建，条目仍为根 `ModInfo.xml`、扁平 `Lib/ScMultiplayer.dll`、`Lib/Comms.dll`，`testzip()` 通过；SHA-256 为 `0934e7a4c6c9905ca6e47b43913ad5387d607b35c6bdb714a6c1d8c8643223f2`。
- 发布目录启动检查确认 Mod 加载和数据库钩子成功，进程保持运行；因未进入开服流程没有产生 `Server started OK`，未发现新的加载错误。

### 2026-08-07：后续验证门禁与剩余重构审计

本节把“必须先验证”和“可以继续拆分”的工作分开。没有满足门禁时，只保留兼容外壳，不移动游戏对象 Apply 时序。

#### A. 必须先验证的运行时场景

**VR-03 Phase 3 加入与地形同步：**

1. 单客户端首次加入，连续 20 次；不得永久停留 `Joining Room`，WorldTransfer、Catch-up、Circuit Ready 必须按顺序完成。
2. 两个客户端同时在线时，客户端 A 放置/挖掘，主机和客户端 B 各重复 1000 次；B 最终地形、碰撞和方块 revision 必须与主机一致，不能只靠重连恢复。
3. 主机放置/挖掘，A 和 B 反向重复 1000 次；记录漏块、重复分片、repair 请求和 `retry=30`。
4. 客户端 A 在 Catch-up 期间退出，客户端 B 继续移动、挖掘、放置和使用电路；A 的清理不得影响 B 的可靠队列、地形 journal 或 Ready barrier。
5. 5% 丢包、200ms 延迟下重复上述加入和地形场景；电路可靠序列必须持续推进，地形恢复不能长期占满可靠窗口。

**VR-04 Phase 4 只读玩家快照：**

1. 两客户端同时移动、旋转、蹲伏、飞行、骑乘和落地，持续 10 分钟；位置/姿态包只能单调应用，不能出现视角回弹或实体重复。
2. 客户端受击和击退时，预测位置、主机速度修正和远程表现分别记录 p95；不能增加额外位置跳变。
3. 非有限位置、旋转、速度和视角输入必须被丢弃；正常消息字段黄金样本读写结果必须与基线字节一致。

**VR-05 输入、动作和物品事务：**

1. 输入序列重复、乱序、延迟和断线重连；动作 request ID 去重，安全锁不会重复执行。
2. 挖掘、交互、投掷物、弓/弩/火枪和受击反馈各重复 500 次；主机结果、声音、粒子和客户端预测回滚一致。
3. 快捷栏拖动、Split、handcrafting、合成桌、箱子、发射器和熔炉各执行放入、取出、分割、丢弃、重连恢复；源格、目标格和主机快照最终一致。
4. 客户端异常退出、睡眠后恢复、切换世界和再次加入；角色物品、位置、视角、生命、温度、体力和容器状态只保存一次且不回滚旧快照。

#### B. 当前仍可继续重构的低风险边界

以下项目不改变协议、可靠序列或游戏对象时序，可在上述门禁之外先做纯边界迁移：

1. **JoinReadyPolicy**：将 WorldTransfer/Catch-up/Circuit Ready 的阶段比较、重试条件和超时判定提取为无状态策略；发送 Ready 包和游戏线程 Apply 仍留在原模块。
2. **ActionRequestValidationPolicy**：集中 ClientID、来源端点、序列号、有限 Ray/位置/速度、阶段状态和请求长度检查；策略只返回验证结果，不执行挖掘、交互、攻击或库存改变。
3. **PlayerActionSequencePolicy**：提取“新序列、重复序列、过期序列、最大缓存清理”的纯判断和边界计算；现有各动作队列仍由兼容宿主维护。
4. **NetworkStatusFormatter**：提取 WAN IN/OUT、Join 状态、包数/字节数、带宽设置摘要和玩家列表方向文字的纯格式化；不改变采样周期和统计来源。
5. **WorldTransferPathPolicy**：提取 ZIP 条目路径规范化、允许扩展名、文件大小上限和资源类型判断；Texture2D 创建、Project 绑定和释放不移动。
6. **PlayerRecordValuePolicy**：在已有 Profile codec 基础上继续拆分默认值、版本兼容和字段缺省策略；实体读取/写回和存档时机不移动。
7. **NetworkIngressCommand（已完成兼容边界）**：已定义结构化命令值类型并随现有队列项携带；没有替换闭包队列。后续是否切换执行模型仍由性能基线决定。

#### C. 暂缓，不得仅凭静态重构进入的部分

- `ScMultiplayerPlayerHealthAndIngress` 的 Catch-up 发送预算、完成统计和退出清理；需要 VR-03 的双端数据。
- `ScMultiplayerWorldTransferHandlers` 的 ZIP 导入、纹理应用、checksum 恢复和 Ready barrier Apply；需要 VR-03 的加入/重连数据。
- `ScMultiplayerClientEvents` 的输入接收、动作确认、主机安全锁和预测回滚；需要 VR-05 的重复/乱序事务数据。
- Inventory、快捷栏、Split、crafting、容器、熔炉、发射器和丢弃事务；需要 VR-05 的主机快照与源格/目标格一致性数据。
- `Client_GameStep` 的闭包队列替换和统一消息入口校验；必须先比较每包分配、Apply p95、主线程 CPU p95 和 UDP 流量，避免重构后再次出现流量持续上涨。
- `Texture2D`、原版实体、SubsystemTerrain、电路算法及其私有字段访问；继续保留原版/SuAPI 适配边界，不复制平行实现。

#### D. 下一步执行顺序

1. 先建立 VR-03/VR-04/VR-05 的日志字段和黄金样本比对脚本，不改运行时行为。
2. 执行 B-1 至 B-5 的纯策略边界迁移，每项单独构建和契约检查。
3. 通过 VR-03 后再处理 Catch-up/Ready 和消息入口的结构化命令边界。
4. 通过 VR-04 后再处理输入/动作意图边界。
5. 通过 VR-05 后才允许迁移物品事务和主机权威结果适配。
6. 每一项都必须保留 Release 包、协议哈希、包结构、运行日志和可回滚暂存状态；任何性能或行为门禁失败时恢复兼容路径。

### 2026-08-07：Phase 4 低风险策略边界迁移（本轮）

- 已新增并接入 `JoinReadyPolicy`：加入 TransferID 比较、重试到期、无进展超时和倒计时均使用纯策略；Ready 包发送、Catch-up 和游戏线程 Apply 时序保持在原模块。
- 已新增并接入 `ActionRequestValidationPolicy`：主机动作信封、地形预测请求、Drop/Jump/Interact/Hit 的序列和有限值检查集中处理；动作执行、安全锁、库存和队列所有权未移动。
- 已新增并接入 `PlayerActionSequencePolicy`：统一新序列判断、环回递增和已处理请求缓存上限判断；没有改变序列字段、消息顺序或可靠通道。
- 已新增并接入 `NetworkStatusFormatter`：网络速率、聊天文本、Join 带宽摘要、玩家相对方向的纯格式化集中处理；采样频率和统计来源不变。
- 已新增并接入 `WorldTransferPathPolicy`：世界 ZIP 路径规范化和 `.scskin`/`.scbtex` 资源大小上限集中处理；解压、纹理创建、Project 绑定和释放仍由原适配器负责。
- 已新增并接入 `PlayerRecordValuePolicy`：玩家存档默认生命/空气/食物/体力/睡眠/温度和持久物品判定集中处理；XML 字段、存档版本、读取写回顺序不变。
- 未迁移 `NetworkIngressCommand`、闭包队列、Catch-up 预算、Ready barrier Apply、输入预测、物品事务；这些仍需 VR-03/VR-04/VR-05 的运行时证据后再处理。

#### 本轮验证要求

1. `verify_refactor_contracts.py` 必须通过，且新增策略文件不能引入 Comms、Engine 或 UI 依赖到 Core。
2. ScMultiplayer Release 构建必须为 0 警告、0 错误；C# 文件统一 CRLF，`git diff --check` 通过。
3. 正式 `.scmod` 仍只包含根 `ModInfo.xml` 和扁平 `Lib/ScMultiplayer.dll`、`Lib/Comms.dll`，并通过 ZIP CRC 检查。
4. 发布目录启动检查只验证 Mod/Comms 加载，不宣称替代 VR-03/VR-04/VR-05 的双端运行时验收。

#### 本轮结果

- `verify_refactor_contracts.py`：通过。
- ScMultiplayer Release：0 警告、0 错误；Obfuscar 正常完成。
- 正式包：`publish/Windows/Mods/[SuAPI]ScMultiplayer-2.0.8.scmod`；ZIP 条目和 CRC 校验通过；SHA-256：`338835d764a63973d22ea53cb1d566fe5f39385f9a80f6d5addd09143e7e93dd`。
- 发布目录启动检查保持进程运行超过 8 秒，日志确认 `Wire protocol mod 2.0.8`、`Server started OK`、数据库钩子和两个服务 DNS 均正常；未进入双端世界流程，因此 VR-03/VR-04/VR-05 仍待真实运行时验收。

### 2026-08-07：Phase 4 结构化入口与低开销诊断边界

- 新增 `Networking/NetworkIngressCommand`，只携带消息种类、来源 ClientID、序列、载荷字节数、接收/入队时间和队列类型；业务载荷和执行仍由原有 `Action` 闭包持有。
- `NetworkMessageRouter` 在解码后建立命令值，并将其随普通、优先输入、WorldTransfer、TerrainChunk 和 Dispatcher 兼容队列传递；原消息 switch、队列优先级、FIFO 和游戏线程 Apply 顺序不变。
- 新增 `NetworkIngressDiagnosticsCollector`，在 `Receive -> Enqueue -> Apply -> Result` 四个检查点只更新固定大小的原子计数器和延迟直方图，不逐包创建诊断记录或格式化字符串。
- 活跃网络每 5 秒最多产生一条聚合摘要，包含包数、载荷字节、入队/Apply/成功/失败数量、三个 p95 延迟和最高频消息类型；摘要继续通过既有 Headless 事件端口异步写出。
- WorldTransfer 队列仍执行原闭包，仅把队列元素从裸 `Action` 换成包含同一 `Action` 和命令元数据的兼容外壳；Catch-up、Ready barrier、地形和物品事务未迁移。
- 本轮没有改变消息字段、wire ID、协议哈希、DeliveryMode、ACK/重传、请求去重、安全锁或主机权威执行逻辑。

#### 稳定性停止点

- 当前必要的结构拆分已经完成，不再继续按文件长度或方法数量机械拆分。
- 下一阶段先执行 VR-03/VR-04/VR-05；只有运行数据证明某个兼容外壳阻碍性能、诊断或扩展时，才迁移其执行逻辑。
- 闭包队列、Catch-up/Ready Apply、输入预测和物品事务继续保留当前实现，避免为了架构整洁打破已验证行为。

#### 本轮验证结果

- ScMultiplayer Release 构建通过，0 警告、0 错误；Obfuscar 正常完成。
- `verify_refactor_contracts.py` 返回 `refactor contracts: OK`，`git diff --check` 通过。
- 正式包仍为 `2.0.8`，只包含根 `ModInfo.xml`、扁平 `Lib/ScMultiplayer.dll` 和 `Lib/Comms.dll`；ZIP CRC 校验通过，SHA-256 为 `e9694e7c7874b1594a24d9cd81bb2fd2dcd7b07adc275ae9eb299f05efaac9ed`。
- 发布目录启动检查保持进程运行超过 8 秒，日志确认 `Wire protocol mod 2.0.8`、`Server started OK`、数据库钩子和两个服务 DNS 正常，未出现新的 Mod/Comms 加载错误。
- 同一正式包已部署到本机 `publish/Windows/Mods` 和华为平板 `192.168.31.212:5555`；两端 SHA-256 均为 `e9694e7c7874b1594a24d9cd81bb2fd2dcd7b07adc275ae9eb299f05efaac9ed`。

### 2026-08-07：VR 运行时冒烟基线

- 新增 `Tools/analyze_runtime_verification.py`，按最新一次 wire-protocol 会话分析 Windows 文件日志和 ADB 设备日志，输出版本/协议/构建、Join Ready 证据、下载修复、Terrain Recovery、重传、入口摘要和错误分类。
- 工具明确将单次实际运行标记为 `OBSERVED`，完整 VR-03/04/05 固定显示 `NOT_RUN`；不会把一次无异常游戏误报为 20 次加入、1000 次地形操作或故障注入门禁通过。
- Windows 主机与华为客户端均加载 Mod `2.0.8`、协议 `v1/a547411b4219`、构建 `8598b5bd6d67`；两端冒烟结果均为 `PASS`。
- Windows 观察到一次 `World transfer ready`；华为观察到一次 `GameJoined`、一次 Circuit Ready、一次 Catch-up Complete 和一次世界下载，`RepairRounds=0`。
- 两端均未出现 Join 超时、checksum 失败、`retry=30`、网络 Apply 异常、Terrain Recovery 或重传；华为的一条 GitHub DNS 连接失败归类为外部网络告警，不属于联机协议错误。
- 当前证据允许保留 Phase 4 兼容边界并继续测试，但尚不足以迁移 Catch-up/Ready Apply、输入预测或物品事务。

### 2026-08-07：VR-03 双端手工回归补充

- 用户完成 Windows 与华为平板的双向放置/挖掘检查，并让华为客户端退出后重新加入；本轮未观察到漏块、碰撞差异、永久停留 `Joining Room` 或重连后才恢复地形的情况。
- 最新协议会话中，Windows 观察到 2 次 `World transfer ready`，华为观察到 2 次 `GameJoined`、2 次 Circuit Ready 和 2 次 Catch-up Complete；两次下载均为 `RepairRounds=0`。
- 两端继续保持 0 条重传、0 次 Terrain Recovery、0 个 Join/checksum/Apply 错误；华为 HUD 采样为 `Retx 0`、`Apply 0ms`、Circuit Ready。
- 本轮记为 VR-03 的双端手工冒烟通过，不等同于 20 次加入、1000 次地形操作和故障注入全部完成；因此继续保留 Catch-up/Ready Apply、闭包队列和可靠通道的现有执行时序。
- 对 `ScMultiplayerPlayerHealthAndIngress` 与 `ScMultiplayerWorldTransferHandlers` 的复核确认：剩余代码同时拥有可靠窗口、带宽令牌、Terrain catch-up、Ready barrier 和连接阶段切换，不存在不改变时序即可独立迁移的必要边界。下一步转入 VR-04/VR-05 行为验收，只有出现明确故障或量化瓶颈才继续迁移对应执行逻辑。

### 2026-08-07：VR-04 双端玩家状态手工回归

- 用户完成双端移动、转向、蹲伏、飞行、落地、受击和击退行为检查，本轮未报告位置回弹、重复角色、姿态错误或玩家状态不同步。
- 最新协议会话中，Windows 与华为继续加载相同的 Mod `2.0.8`、协议 `v1/a547411b4219` 和构建 `8598b5bd6d67`；累计观察到 3 次完整 Ready/Join/Circuit Ready 流程。
- 两端仍为 0 条重传、0 次 Terrain Recovery、0 次下载 repair、0 个 Join/checksum/Apply 关键错误；外部 GitHub DNS 失败继续与联机协议分开统计。
- 本轮记为 VR-04 手工行为冒烟通过，不替代 10 分钟长时状态采样、p95 位置/击退量化和非有限输入故障注入；因此继续保留玩家输入接收、动作确认、主机安全锁和预测回滚的现有实现。
- 下一门禁为 VR-05 输入、动作与物品事务；只有出现可复现故障或完成对应压力证据后，才评估迁移这些主机权威执行边界。
