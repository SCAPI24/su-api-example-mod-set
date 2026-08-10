# ScMultiplayer Current Tasks

## 主持房间时成熟花草可贴墙放置

状态：待修复，已完成源码定位

现象：主持房间后，成熟花草可以放到墙面并持续存在；普通单机行为应将无合法支撑的花草清除。

源码结论：原版 `SubsystemPlantBlockBehavior.OnNeighborBlockChanged` 会检查植物下方支撑，不合法时调用
`DestroyCell`。`SuSubsystemPlantBlockBehavior` 当前要求
`HostTerrainAuthority.IsReadyForAuthoritativeMutation`，主持房间的区块在 `AreBehaviorsNotified`
之前可能返回未就绪，导致主机跳过这次原版支撑检查。

修复计划：主机权威端始终执行植物邻近支撑检查；客户端仍禁止本地产生持久植物变化。保留区块未准备好时
对生长和区块生成等非即时逻辑的保护，不修改 `Survivalcraft` 原版源码。

验收标准：普通单机、主持房间无客户端、主持房间有客户端三种路径中，墙面或侧面成熟花草都会被清除，
泥土、草方块和耕地表面的合法花草保留，种子放置规则不变。

## 冰块挖掘后的水与重新结冰同步

状态：待修复，已完成原版机制核对

现象：冰块在冰雪天气中被挖掉后先变成水，等待约 50 到 60 秒后才重新变回冰。联机时需要保证
主机和所有客户端遵循原版时序，不能由客户端提前冻结或覆盖主机结果。

原版机制：

- `SubsystemWaterBlockBehavior.Update` 负责水流恢复，周期约为 `0.25s`；挖掉冰块后，冰先被销毁，
  周围或下方水源随后可能恢复为水方块 `18`。
- `SubsystemBlocksScanner.ScanPeriod` 为 `60f`。扫描游标按已分配区块推进，单个区块再次被扫描的实际时间
  通常约为 50 到 60 秒，不是固定的 50 秒计时器。
- `SubsystemWeather.FreezeThawAndDepositSnow` 通过 `ScanningChunkCompleted` 和
  `TerrainUpdater.ChunkInitialized` 处理天气方块变化。局部柱体为降雪且顶层为水 `18` 时改成冰 `62`；
  非降雪柱体则清除冰块，水由流体系统恢复。
- 原版没有直接执行 `62 -> 18` 的天气替换；常见的“冰变水”是挖掘后清空方块，再由水流恢复。

联机实现计划：

- 保持客户端禁用持久冻结、融化和积雪处理，客户端只应用主机广播的地形结果。
- 主机继续运行原版 `SubsystemBlocksScanner` 和 `SubsystemWeather`，不新增每帧扫描或固定 50 秒计时器，
  避免改变原版冻结概率和扫描负载。
- 主机将挖掘后的 `62 -> 0`、流体恢复后的 `0 -> 18`、天气扫描后的 `18 -> 62` 按同一地形序列发布，
  同坐标的新值必须覆盖旧值，客户端不得用本地流体或天气结果反写。
- 对流体延迟确认只确认主机最终值，不提前把水强制改成冰；若水在扫描前已被沙土、泥土等方块覆盖，
  天气扫描应读取最终顶层方块，不得再次冻结被覆盖的水。
- 保留原版区块初始化时的即时天气处理；已加载区块的后续变化继续等待原版扫描游标，不人为缩短或延长周期。

验收标准：

- 冰块挖掉后，主机和客户端先看到水或流体恢复结果，客户端不会自行提前结冰。
- 在冰雪天气下，水在对应区块下一次原版扫描时重新变成冰，时间符合约 50 到 60 秒的原版范围。
- 水在扫描前被沙子或泥土覆盖后，最终保持覆盖方块，不会被天气结果恢复成冰。
- Windows、华为平板和主机使用同一 Mod 包时，同一坐标的地形变化顺序一致，重连后仍以主机存档为准。

## 乘船、骑马与乘骑状态同步

状态：架构级修复已实施，待 Windows 与华为平板双端回归验证

现象：客户端点击乘船或骑马后，本地看起来已经站到载具或马背上，但主机端与其他客户端看到的角色仍在上马前的位置，
角色模型可能浮空或与马分离。客户端下马后乘骑按钮很快恢复为“乘骑”或无法再次乘坐；其他客户端在碰撞后可能看到骑乘者站立在马旁边，
同一匹马的乘骑状态也会短暂不一致。客户端自己骑马时，马头和骑乘动作还会因主机离散快照出现卡顿。

本轮补充修复（2026-08-10）：

- 手持鞍具时屏蔽同一触摸帧产生的 `HitRequest` 和动物攻击请求，只保留原版
  `SubsystemSaddleBlockBehavior.OnUse` 的交互链路，避免出现 `-1` 伤害数字。

### 本轮回归问题

本轮已针对以下现象完成架构修复，保留作为回归用例：

- 下马或下船完成后，迟到旧状态不能重新挂回马背或船上。
- 上船、上马和自动脱离由主机按原版状态机执行，客户端不再以连续位置快照反推动作。
- 客户端预测仅负责本地表现，主机结果通过独立状态序号确认；拒绝结果会恢复原版父子关系和动画状态。

### 原版状态机依据

重构必须以以下原版流程为边界，不修改 `Survivalcraft` 源码：

- `Survivalcraft/Game/ComponentGui.cs:577-602`：按钮或 `ToggleMount` 只产生一次动作；已乘骑调用
  `StartDismounting`，未乘骑调用 `FindNearestMount` 后 `StartMounting`。
- `Survivalcraft/Game/ComponentRider.cs:32-41`：`Mount` 由角色 `ComponentBody.ParentBody` 的实际父子关系决定，
  不是一个可由网络快照直接写入的布尔状态。
- `Survivalcraft/Game/ComponentRider.cs:68-153`：上、下马包含最长约 0.75 秒的动画阶段；动画完成后还会检查挂载偏移，
  偏移超过 0.4 格并持续约 0.1 秒会自动执行 `StartDismounting`。
- `Survivalcraft/Game/ComponentMount.cs:7-41`：载具只能通过 `ComponentMount.Rider` 维护实际占用关系。
- `Survivalcraft/Game/ComponentPlayer.cs:88-137`：只有 `ComponentRider.Mount != null` 时，移动输入才转交给
  `ComponentSteedBehavior` 或 `ComponentBoat`；否则输入属于角色自身。
- `Survivalcraft/Game/ComponentBoat.cs:55-77`：船体深度达到原版条件时会主动让骑手下船，主机和客户端不能各自模拟这一条件。

### 当前 Mod 的架构风险审核

- 用户操作本身是一次性按钮边沿，但 `SuComponentInput` 将它转换为 `PlayerInput.ToggleMount` 后，混入同时承载移动快照、位置、
  `IsRiding` 和 `MountEntityId` 的 `GamePlayerInputMessage`。`NetworkMessageSender.SendPlayerInputMessage` 又通过
  `SendDirectInput(... latest: true)` 发送，这个通道允许更新的输入覆盖较早的动作，导致主机可能根本收不到这次上马、下马或上船、下船。
- 主机 `TryGetNetworkPlayerInput` 依赖 `Sequence/ConsumedSequence` 从连续快照中取出动作，动作边沿与位置快照不是同一个可靠事务，可能出现动作重复、动作丢失或状态先后倒置。
- `ScMultiplayerPlayerSync` 每次发送 `IsRiding` 和 `MountEntityId`，但 `IsRiding` 在原版上、下马动画期间仍由 `ParentBody` 短暂保持为真，不能把它当作“动作已完成”的确认。
- 客户端 `MatchRemoteRidingState` 根据位置快照调用 `StartMounting/StartDismounting`，这会绕过原版按钮动作顺序；当旧快照晚到时，就可能覆盖刚完成的下船或下马。
- `ComponentRider.Update` 的偏移纠偏与 `ComponentBoat.Update` 的沉水脱离都属于主机权威逻辑；客户端若同时对父子关系或船体物理做本地修正，会出现站在船上、重复脱离或再次挂载。
- 当前没有主机侧的载具占用预留和动作结果序号，两个客户端同时预测同一匹马或同一条船时，无法用协议保证唯一骑手。

### 架构级修复计划

### 本轮实施结果（2026-08-11）

- 新增独立可靠的 `MountActionMessage`，乘骑动作不再混入 `latest:true` 的连续输入快照。
- 新增 `MountStateMessage.StateSequence`，将动作序号与主机状态序号分离，支持自动脱离和初始乘骑状态。
- 主机重新执行原版 `FindNearestMount`、`StartMounting`、`StartDismounting`，客户端目标载具 ID 仅作提示，不再硬拒绝合法动作。
- 主机本地角色和远程角色统一通过权威乘骑状态广播；重复动作只重放结果，不重复执行原版动作。
- 客户端收到确认、拒绝或自动脱离状态后，按 `ComponentBody.ParentBody` 和 `ComponentRider` 动画字段恢复或推进状态。
- 乘骑动作处理前先应用动作包携带的远程角色姿态，再由主机重新执行 `FindNearestMount`；避免客户端已靠近马而主机角色仍停在旧位置，导致主机找不到同一实体。
- 主机乘骑解析优先将客户端 `MountEntityId` 映射到 `m_hostAnimalIds` / `m_hostMountIds` 的实体，再按原版 `ScoreMount` 条件验证；客户端马和主机马因此保持同一网络实体，不再只依赖主机重新猜测最近目标。
- 乘骑动作消息同时携带客户端当帧 `LookAngles`；主机执行 `ScoreMount` 前恢复身体位置、身体旋转和视角，避免主机只看到转身而因旧视角判定未对准马。
- 乘骑消息已接入实际生效的 `NetworkMessageRouter`；此前仅接入旧分发分支会导致主机只应用普通输入旋转而不执行 `HandleMountActionMessage`。

当前仅剩运行时回归：Windows 主机、Windows 客户端、华为平板客户端的上下马、上下船、同一载具竞争、船沉水自动脱离、断线重连。

1. **拆分乘骑动作与连续输入传输通道**
   - 新增独立、可靠、可去重的 `MountActionMessage`，包含动作序号、请求类型（Mount/Dismount）、目标载具网络 ID、
     客户端位置和客户端步号。
   - `GamePlayerInputMessage` 不再承载需要可靠送达的 `ToggleMount`；连续输入仍可保留移动、视角和载具控制量，
     但乘骑边沿必须走独立可靠通道，不能使用 `latest:true` 覆盖式发送。
   - 可靠动作发送沿用现有 `SendScheduledMessage`/定向可靠请求的传输设施，不新建第二套 UDP 确认协议。
   - 主机按客户端 ID 保存最后处理的动作序号，重复包只回放结果，不重复调用原版动作。

2. **主机单一权威执行原版动作**
   - 主机收到 `MountActionMessage` 后，使用主机实体和 `ComponentRider` 当前状态重新验证距离、朝向、遮挡、载具存活状态和占用关系。
   - 通过原版 `FindNearestMount`、`StartMounting`、`StartDismounting` 执行一次，不直接写 `ParentBody`，不从客户端 `IsRiding` 反推动作。
   - 主机为马、驴、船统一维护载具占用；已被其他角色占用时返回拒绝结果。
   - 船的沉水脱离、马匹死亡或其他原版自动脱离只由主机执行，并作为带序号的结果发送。

3. **引入带序号的乘骑结果状态**
   - 新增 `MountStateMessage`，包含动作序号、结果（Rejected/Mounting/Mounted/Dismounting/Dismounted）、
     载具网络 ID、主机步号和挂载变换。
   - 客户端位置快照中的 `IsRiding` 只作为显示校验，不再直接调用 `StartMounting/StartDismounting`。
   - 客户端只接受不早于当前动作序号的结果；旧结果不能覆盖已确认的下船或下马。

4. **保留预测但隔离网络纠偏**
   - 本地仍立即使用原版 `ComponentGui` 预测视觉和动画，等待主机结果确认。
   - 确认期间不把角色直接位置写回，也不让远程载具的快照修改本地 `ParentBody`；载具移动和碰撞继续由主机权威驱动。
   - 主机结果拒绝时，客户端按原版安全脱离流程回退；确认成功后只校正目标载具、挂载偏移和按钮状态。
   - 远程客户端只维护可见载具的插值显示，不启用 `ComponentBoat` 的本地物理模拟，避免沉水判断和速度差异造成重复脱离。

5. **分阶段验证与回归**
   - 单人主机：反复上、下马和上、下船，覆盖站立、跳跃、边缘、移动船体和载具沉水场景。
   - 双端：A/B 同时靠近同一匹马或船，验证只能有一个骑手，另一端收到明确拒绝，不出现模型浮空或重复挂载。
   - 网络扰动：延迟、乱序、重复和丢失动作包，验证旧 `MountStateMessage` 永远不能恢复已完成的下船/下马。
   - 验证主机、Windows 客户端、华为平板的 `MountEntityId`、动作序号、角色父子关系和按钮状态一致。

### 架构验收标准

- 不再通过普通位置快照触发乘骑动作；一次用户操作最多执行一次原版 `StartMounting/StartDismounting`。
- 下船或下马完成后，即使迟到旧包到达，角色也不会重新挂载。
- 上船、上马失败时明确回退为角色状态，不出现站在载具上但 `ComponentRider.Mount == null` 的永久状态。
- 主机是唯一的载具物理、碰撞、自动脱离和骑手占用判定者；客户端只做有界预测和表现插值。

验收标准：

- Windows 主机、Windows 客户端、华为平板客户端均可正常上船、下船、上马和下马；主机及所有客户端的角色位置、挂载对象和按钮状态一致。
- 客户端选择的马与主机执行的马必须通过同一网络实体 ID 对应；主机不得因延迟快照使用旧角色位置选错或找不到载具。
- 客户端上马或上船后不会出现角色浮空、模型与载具分离、碰撞后被错误拆除，也不会在同一匹马被占用时同时维持两名骑乘者。
- 客户端乘骑状态下马头和骑乘动作连续可见；网络纠偏只修正实体状态，不产生明显的重复上马、重复下马或视觉回弹。
- 服务器仍保持主机权威，客户端预测只限视觉表现；断线、重连和远端快照延迟不能造成错误的永久乘骑状态。
