# ScMultiplayer 2.0.7-2.0.8 任务归档

归档日期：2026-08-08

归档说明：

- 本文件保存从 CURRENT-TASKS.md 移出的 2.0.7/2.0.8 复现记录、修复方案、构建产物和验证项。
- 这些章节已被后续 2.0.8 架构、提交或用户确认的稳定基线取代，不再作为当前实施入口。
- 归档不表示每个历史包都重新单独验证；需要回溯时以对应 Git 提交、日志和发布包为准。
- 禁止直接从本归档恢复旧方案覆盖当前睡眠、电路、Joining Room、ACK 或地形同步实现。

---

## 加入下载与 Joining Room 过慢（当前轮）

状态：已完成 Release 构建并部署 Windows/华为，待在线回归

本轮产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，SHA-256：
`896facf3f2094e08a3f543ce48b4254e552f2b423cfeafd130a4ad2bdd353791`

复现证据：

- 最新一次加入在 `12:31:14.959` 被接受，直到 `12:31:50.574` 才排完 650 个地图分片，地图阶段约 35.6 秒。
- `12:31:52.925` 已生成首批 catch-up，但直到 `12:32:39.620` 才排完 136 条 post-cutoff 消息，`World transfer ready` 延迟到 `12:32:39.820`。

原因：

1. 940 字节地图分片原本按设计可装入一个可靠 UDP 包，上一轮却按 2 包预留。
2. 本地 DRT 转发在两个采样点之间完成时，预留无法及时观察到，只能等待固定 1.5 秒过期，造成地图传输批次被人为压小。
3. 地图完成后，过期的分片预留和仍在 ACK 中的可靠包继续占用加入窗口，catch-up/post-cutoff 不能及时发送。

本轮修复：

1. 地图分片恢复按 1 个可靠包预留；catch-up 和电路 Snapshot 仍按实际序列化长度估算。
2. 转发预留寿命改为按 RTT 动态限制在 150ms 到 500ms，避免长时间空等，同时仍保留短暂本地 DRT 转发保护。
3. 客户端报告 `ProjectReady` 后清理已完成地图归档的本地转发预留；实际 Comms 未确认包仍由传输层计数保护 catch-up。
4. 不改 ACK、重传、可靠序列和在线玩家流量保留策略。

验证标准：

- 相同地图和带宽配置下，地图下载时间回到数秒级，并能使用可用加入带宽。
- 地图完成后 catch-up、post-cutoff 和 `World transfer ready` 不再因 1.5 秒预留周期长时间停滞。
- 雨雪、雾、已有在线玩家和高 RTT 情况下仍不突破可靠窗口，丢包只触发原有重传。

## 首次加入可靠队列计数滞后导致 Joining Room 卡住（当前轮）

状态：已完成 Release 构建并部署 Windows/华为，待首次加入在线回归

本轮产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，SHA-256：
`7a83ce1482369a97c961c5c71bb93e44b76fe6edb2a22a8629e358b0804eb1b2`

复现现象：

- 主机开启雨雪和雾时，客户端第一次加入偶发长期停在 `Joining Room`。
- HUD 的 `CKT` 长时间停在 `Snapshot`，`fence` 延迟从约 75ms 上升到 700ms 后又回落。
- 主机日志先出现 `Join catch-up batch queued`，随后可靠队列达到 `Rel=226`，却没有出现 `World transfer ready`；第二次加入通常可正常完成。

原因：

1. 主机发送给远程客户端的消息实际经过“主机本地 Client -> 本地 DRT Server -> 远程 Client”两段路径。
2. 地图分片还会先进入后台发送队列；`GetUnackedPacketsCount()` 只统计已经进入 Comms 的远程包，不包含本 tick 已批准但尚未由本地 DRT Server 转发的分片。
3. 世界分片、join catch-up 和电路 Snapshot 因此连续使用同一份过期未确认数，在动态窗口为 128 时仍可瞬间堆到 226，完成标记被可靠队列阻塞。

修复范围：

1. 为每个客户端增加 Mod 内的可靠转发预留计数，世界分片入后台队列、catch-up、Snapshot、Manifest 和 `ReadyToPlay` 等定向可靠消息在进入本地转发路径前先占用预留。
2. 队列检查统一使用“Comms 已确认未确认包 + 尚未反映到 Comms 的预留包”，严格受现有加入窗口和正常游戏关键包保留限制。
3. 后台分片取消、连接退出或发送异常时释放对应预留；真实远程队列增长时结算预留，超过有界时间的旧预留自动清理。
4. 不修改 Comms 的可靠序列、ACK、重传和消息类型；普通游戏可靠消息仍使用原有路径。

验证标准：

- 首次开启雨、雪、雾的加入不再因可靠队列从 128 越界而卡在 `Joining Room`；日志依次出现电路 bootstrap、catch-up complete 和 `World transfer ready`。
- 主机可靠队列不再出现“已批准包数未计入导致的瞬时 226”越界；加入客户端和已在线玩家的可靠消息均保留窗口。
- 世界分片取消/客户端退出后预留清零，不影响后续房间和其他客户端。

## 客户端切换时间时提示显示在主机端（本轮）

状态：已完成 Release 构建并部署 Windows/华为，待双端在线验证

本轮产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，Windows 与华为平板 SHA-256：
`2d57ceaf40defeec4ba1d340dc1fb41dc5380bbb33ec563cccbdce5abaf38d35`

复现现象：

- 客户端点击创造模式时间按钮后，世界时间由主机正确切换，但 `Dawn/Noon/Dusk/Midnight` 提示显示在主机端。

原因与修复：

1. 主机执行客户端 `WorldControlRequestMessage` 时直接调用了主机角色的 `ComponentGui.DisplaySmallMessage()`。
2. 客户端请求原本已有定向 `WorldControlResultMessage`，并按请求 ID 在发起客户端显示相同提示；主机端直接显示属于重复且归属错误的副作用。
3. 客户端请求路径现在只修改主机权威时间并返回结果，不再显示主机提示；主机自己点击时间按钮仍走原版 `ComponentGui`，行为不变。
4. 2026-08-08 在线验证中，主机能执行时间切换，但客户端仍无底部提示。检查发现请求和定向结果均为一次性非可靠 UDP；结果丢失后，世界已经改变，但客户端没有可用于显示提示的确认。
5. 本轮将请求与结果改为可靠有序小消息。主机继续按 `RequestId` 去重，客户端只在收到对应结果后显示提示，不增加乐观本地提示。
6. 2026-08-08 第二次在线验证：主机和客户端均不再显示时间提示。主机不显示属于预期；客户端仍不显示，证明单纯把结果改为可靠消息没有解决根因。
7. 一次完整追踪已证明请求、主机执行、定向结果、pending 命中和客户端 `ComponentGui` 调用均正常；最终文本却是不可见 Unicode 标识符。
8. Release 混淆会重命名 `WorldControlTimeResult` 的枚举成员，`TimeResult.ToString()` 因此不再稳定返回 `Dawn/Noon/Dusk/Midnight`。现改为显式枚举到固定 UI 文本的映射，并删除全部临时追踪。

验证标准：

- 客户端每次切换时间后，仅发起请求的客户端显示对应时间提示。
- 主机不显示客户端操作的提示；主机自己切换时间时仍正常显示。
- 时间权威同步、请求去重、结果超时和睡眠后的电路时间锚不受影响。

## 雨雪或雾开启时客户端卡在 Joining Room（本轮）

状态：已完成 Release 构建并部署 Windows/华为，待双端在线验证

本轮 Release 产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，Windows 与华为平板 SHA-256：
`2d57ceaf40defeec4ba1d340dc1fb41dc5380bbb33ec563cccbdce5abaf38d35`

复现与证据：

- 主机保持雨雪和雾时，客户端完成世界下载与 Project 加载后停留在 `Joining Room`，没有进入电路 bootstrap 完成阶段。
- 第一次失败的加入 catch-up 为 17 条消息、69 个地形单元，最终可靠队列达到 `Rel=223`；第二次为 13 条消息、206 个地形单元，最终达到 `Rel=225`。
- 主机关闭雨和雾后，同一客户端于 `10:44:36.084` 开始加入，`10:44:44.360` 完成电路 bootstrap，随后完成 catch-up。

原因与修复：

1. 加入期间的天气、冻结、融雪和积雪仍会产生主机权威地形变化，这些变化需要进入加入 catch-up。
2. `SendPendingJoinCatchUps()` 原本使用保留正常游戏关键包后的 bulk 窗口；加入客户端尚未进入正常游戏，却会被该保留量阻塞，形成加入完成屏障的优先级倒置。
3. catch-up 发送前只检查当前未确认包数，没有估算下一条可靠消息会拆成多少个 1024 字节 UDP 包，较大消息可能在一次调用中越过窗口。
4. 加入 catch-up 现使用完整加入传输窗口，并按 940 字节安全载荷估算整条消息的实际分片数后再入队；超大单条消息仅在未确认队列为空时启动，避免永久停滞。
5. 客户端仍处于加入屏障时，将本地雨雪强度、雾进度和闪电表现保持为零，减少 Project 加载阶段的渲染与天气处理压力；主机天气权威和地形模拟不变。
6. 收到 `ReadyToPlay` 并清除加入屏障后，客户端立即重新应用最后一个主机权威天气状态，不修改或保存临时世界天气配置。

验证标准：

- 主机分别开启雨、雪和雾后，Windows 与华为平板均能在正常时间内从 `Joining Room` 进入游戏。
- 加入期间可靠未确认队列不再越过动态加入窗口，能依次出现 `Client circuit bootstrap complete`、`Client catch-up complete` 和主机 `World transfer ready`。
- 进入游戏后雨雪、雾、雷电表现与主机一致，积雪、冻结和天气地形变化仍只以主机最终状态为准。
- 已在线玩家的普通可靠消息、电路同步和地形同步不因新客户端加入而被挤占。

## 初始电路边沿与睡眠会话时间统一（本轮）

状态：已完成 Release 构建并部署 Windows/华为，待双端在线验证

复现现象：

- 客户端首次进入地图，加载电压后偶发产生主机没有的额外电路边沿，计数器/加法器相差一个值。
- 主机和客户端同时睡眠时，双方进入加速和醒来的时刻不一致；醒来后的电路快照边界也随之不同。

原因与修复：

1. 首次权威电路快照应用时不再恢复普通元件的通用 `NextSimulationSteps`，避免把客户端原版加载队列叠加成一次本地输入；延迟门自己的明确历史队列仍保留。
2. 延迟快照记录沿用首次快照的抑制标记，避免远处区块稍后实例化时再次触发本地初始化边沿；后续严格修复快照仍可恢复必要的通用调度。
3. `GamePlayerHealthMessage` 增加主机权威 `SleepStartTime` 与 `SleepFactor`，客户端按主机睡眠会话起点重置私有状态，不再用本地请求到达时间计算自动醒来。
4. 客户端对每个已接受的权威 world-info 都刷新加速边沿；即使加速状态由 fence 速率推断，也不会漏掉主机的 `false` 唤醒边沿。

验证标准：

- 首次加入后持续高/低电平和振荡电路不出现客户端独有的一次边沿。
- 两端睡眠进入加速后使用同一个 `SleepStartTime`，主机醒来时客户端不提前或延后自动醒来；醒来后的首个权威快照内级联加法器/计数器一致。
- 非睡眠电路、延迟门历史、可靠序列和 Joining Room 流程不回归。

## 首次加入电路快照阻塞与范围延迟同步（current round）

状态：修复已在线验证通过，保留后续重连回归

本轮 Release 产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，Windows 与华为平板 SHA-256：
`7c99732d6bc541b34711bf4d3880eae90f639742d5c5fee7bedc303f681b6a19`

复现现象：

- 客户端地图下载完成后长期停留在 `Joining Room`。
- HUD 的 `CKT` 长期显示 `Snapshot`，fence age 在几十毫秒和数百毫秒之间跳变。
- 主机日志能看到 catch-up batch queued，但没有后续 `World transfer ready`。
- 2026-08-08 实测：世界下载用时 3.97 秒，客户端 Project 在 `10:09:22.585` 就绪；电路 bootstrap 到 `10:10:12.310` 才完成，单独耗时约 49.7 秒，随后 `10:10:12.560` 立即完成 catch-up。

原因：

- 主机快照包含当前已实例化的全部电路元件。
- 客户端对尚未加载区块中的元件找不到 `ElectricElement`，旧逻辑把它视为快照失败并重启完整快照。
- `CatchUpBatchApplied` 依赖电路快照完成，导致加入屏障和快照重试互相等待。
- 首次快照虽然是加入完成的必需条件，却和普通恢复共用 `GetReliableBulkUnackedPacketLimit()`。默认 32 包窗口再扣除 16 包关键保留后，快照长期等在世界/catch-up 可靠包之后，形成加入屏障的优先级倒置。

本轮修改：

1. 快照请求和响应增加有界区块范围；主机按客户端请求范围生成当前时刻快照。
2. 初次加入允许远处未实例化元件进入待同步表，当前已加载范围仍要求正常应用，不再阻塞 Joining Room。
3. 客户端区块范围变化后主动请求该范围的新快照，待同步元件在电路实例创建后应用。
4. 睡眠恢复、哈希修复仍使用严格快照，不把类型不匹配或已加载范围的应用失败当作可忽略状态。
5. checkpoint hash 按客户端确认的范围计算，避免主机和客户端加载范围不同造成永久误判。
6. 本轮只允许仍在 `TransfersAwaitingReady` 中的客户端，其 `Snapshot` 消息使用完整加入传输窗口；普通电路修复和已进入游戏后的快照继续保留关键包余量。
7. 2026-08-08 修复后实测：客户端 Project 于 `10:36:08.645` 就绪，电路 bootstrap 于 `10:36:10.322` 完成，耗时约 1.68 秒；随后 `10:36:10.366` 完成 catch-up。

验证标准：

- 首次加入时即使远处存在未加载电路，`CKT` 能从 `Snapshot` 进入 `Ready`，主机能完成 `World transfer ready`。
- 移动到远处电路后，该范围收到主机当前快照，计数器、延迟门历史、电压和锁存状态一致。
- 睡眠恢复仍在严格 fence 后完成，不出现旧 fence 回退、快照重试洪泛或 CPU 持续升高。
- Release Windows 与 Huawei 使用同一协议哈希和同一 `.scmod` 内容。

## Host-authoritative lightning and season boundaries (current)

Scope:

1. Automatic and manually requested lightning remain host-generated. The client only receives
   the lightning presentation and authoritative terrain/entity results.
2. A connected host defers an explosion while an already allocated chunk inside its conservative
   pressure envelope is not Valid or has not notified block behaviors. Missing chunks are not
   allocated and remain untouched.
3. Plant lifecycle and growth are host-only in a connected room. Clients keep the host terrain
   values and do not publish local seasonal growth as player terrain changes.
4. Deciduous leaf color/particle presentation remains local, while fallen-leaf terrain changes
   remain host-only and are gated by a ready host chunk.
5. Seasons remain a global world clock; no client-distance simulation or client authority is
   added. When a later host chunk load exposes a seasonal change, the existing terrain revision,
   checkpoint, and circuit synchronization paths must deliver the final host value.

Validation:

- Trigger automatic and manual lightning near a host loaded-range boundary. Confirm no cell in an
  unallocated or not-ready chunk is destroyed, while ready neighboring chunks preserve native
  explosion behavior.
- Change the season while clients have a larger local visibility range than the host. Confirm
  client-only outer chunks do not become authoritative changes, and later host loading replaces
  them with the host value.
- Verify plant growth, leaf fall, lightning explosion, terrain sequence, and reliable queue
  behavior in Release builds with no extra diagnostic logging.

本文件只保留尚未处理完成的任务，用于跨轮次继续工作。

- 新发现或未完成的问题立即加入本文件。
- 完成源码修改、Release 编译和要求的部署后，从本文件删除对应任务。
- 已完成任务不在本文件保留历史记录，历史以 Git 提交和发布包为准。
- 每轮修改 ScMultiplayer 前先核对本文件，避免上下文压缩后遗漏任务。

## 主机邻近算法派生的导线/LED 状态未及时同步（当前轮）

状态：已完成 Release 构建并部署 Windows/华为，待双端在线验证

本轮产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，Windows 与华为平板 SHA-256：
`4441e38bd7749377baf7927a53f9594cb952c3c127a2ab31add7680405bb4220`

复现现象：

- 主机挖掉导线附着方块后，主机邻近算法会正确移除导线，但客户端仍显示导线悬空连接。
- 主机直接移除导线后，客户端也可能继续显示原导线。
- 客户端挖掉挂墙 LED 后，主机先看到掉落物，但墙面保留一个不亮的 LED，数秒后才消失。
- 主机直接移除 LED 时，客户端能及时看到 LED 消失，说明普通单格地形删除链路本身可用。

源码结论：

- 原版 `SubsystemTerrain.ProcessModifiedCells()` 每轮先复制并清空 `m_modifiedCells`，邻近回调中的 `DestroyCell/ChangeCell` 会形成下一轮修改。
- `MountedElectricElement.OnNeighborBlockChanged()` 会在支撑面失效时销毁挂墙电器；`WireDomainElectricElement.OnNeighborBlockChanged()` 还会修改 wire face bitmask 或销毁整格导线，因此一次操作可能形成多轮派生变化。
- 当前 Mod 固定发布并处理两轮修改，而且每轮在邻近算法尚未收敛前就独立生成 terrain sequence。客户端可能先落地中间 wire bitmask，再等待后续序列补最终状态，形成悬空导线或不亮 LED 残影。

本轮修改：

1. 主机在 `SuSubsystemTerrain` 内对同一批修改执行有界邻近传播，最多 8 轮或 1024 个输入单元，避免大型连锁更新占满一帧。
2. 同一传播闭包内只收集坐标，邻近算法结束后由现有 `PublishTerrainChanges` 读取并发布最终值；继续复用 terrain sequence、journal、checkpoint 和恢复机制。
3. 超出上限的连锁修改保留在原生 `m_modifiedCells` 中供下一帧继续处理，同时先发布已经写入的当前值，不丢弃变化。
4. 客户端现有预测、修复及主机权威应用路径保持不变，不新建电路消息或另一套可靠序列。

验证范围：

- 主机移除导线附着方块，所有客户端的导线和碰撞/电路状态在同一轮更新后消失。
- 主机直接移除单面及多面导线，客户端不保留旧 face bitmask，不出现悬空连接。
- 客户端移除 LED、四 LED、多色 LED 等挂墙电器，主机和其他客户端不出现不亮残影，掉落物只生成一次。
- 大片导线、爆炸和流体更新时无明显单帧卡顿，terrain sequence 连续，电路可靠队列不增加新的阻塞。

## 客户端加入时电路电压快照产生一次伪信号（当前轮）

状态：已完成 Release 构建并部署 Windows/华为，待双端在线验证

本轮产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，Windows 与华为平板 SHA-256：
`f10c85ee4782688a7f60175e7d1684619bb5ee700fda97d0f6454cb542b556df`

复现现象：

- 客户端每次进入游戏并加载主机电路电压时，会额外产生一次主机端不存在的信号。
- 边沿敏感电路因此在客户端多执行一次，最终显示值与主机相差一个单位。

源码结论：

- `CircuitSynchronizer.ApplyStateRecord()` 写入完整快照后，无条件调用 `QueueElectricElementConnectionsForSimulation(... + 1)`。
- 完整快照已经代表主机在 `SnapshotHostCircuitStep` 的稳定状态；再次唤醒每个下游，相当于把“恢复电压”错误解释为“电压刚发生变化”。
- SR 锁存器、存储器和随机信号发生器此前只同步了输出电压，没有同步其输入边沿允许位。即使不立即唤醒，下次输入变化前也可能使用客户端构造时的默认边沿状态。
- 仍存在第二个时序窗口：客户端 `Project` 已加载但 `CircuitSynchronizer` 尚未绑定时，`SuSubsystemElectricity.Update()` 可能因为 `client.IsConnected` 尚未更新而回落到原版 `base.Update(dt)`；原版加载队列先执行一次，产生主机不存在的初始边沿。

本轮修改：

1. 快照恢复只写入权威状态并恢复主机已有的 `NextSimulationSteps`，不再为每个快照元素额外唤醒全部下游。
2. 补齐 `SRLatchElectricElement` 的 set/reset/clock allowed、`MemoryBankElectricElement` 的 write/clock allowed，以及 `RandomGeneratorElectricElement` 的 clock allowed 状态。
3. 正常电路事件、按钮释放和真实输出变化仍使用原版连接排队模拟，不改变联机电路事件的可靠序列。
4. 联机 Project 处于加入/下载/切换阶段且同步器尚未绑定时，`SuSubsystemElectricity.Update()` 清空本地时间余量并跳过原版 bootstrap 模拟，避免客户端独有初始边沿。
5. 同步器继续只由既有帧末 `RunCircuitPhaseCore` 绑定；禁止在原生电路 `Update()` 内提前启动 checkpoint/snapshot，避免电路恢复可靠流量与加入 catch-up 批次争用窗口并卡在 `Joining Room / CKT Recovery`。

验证范围：

- 在持续高、持续低和脉冲输入下分别让客户端加入，计数器及显示器数值加入前后不变，并与主机一致。
- SR 锁存器、存储器和随机信号发生器在高电平保持期间加入时不产生第二次边沿；电平先回落再上升后，两端只执行一次真实边沿。
- 按钮、压力板及正常电路切换仍能立即传播，电路快照恢复和 Joining Room 完成条件不受影响。

## 本机睡眠结束后 Apply 持续增长（当前轮）

状态：源码已修复，待本机验证

现象：所有角色睡眠结束后，本机 HUD 显示 `Apply 3974 (48000ms)`，队列数量和最旧动作年龄仍继续升高，操作逐渐卡顿。

原因判断：客户端只有本地角色时，原版 `SubsystemTime.NextFrame` 会误判“所有人睡眠”，把客户端 `FixedTimeStep` 设为 `0.05` 并把 `SubsystemUpdate.UpdatesPerFrame` 设为 `20`。这会让客户端整套逻辑、输入和网络相关更新重复执行；低帧率下 `Frame.Update` 的 4ms Apply 消费预算赶不上持续进入的网络动作，形成积压正反馈。它不是可靠 ACK 或电路序列本身堵塞。

修复范围：客户端在每个原生逻辑步最前面、早于电路同步器的任何提前返回，始终强制 `FixedTimeStep=null`、`UpdatesPerFrame=1`，只接受主机的 `TotalElapsedGameTime + TimeOfDayOffset` 权威时间；主机在睡眠加速时把世界时间快照从 2Hz 临时提高到 8Hz，避免客户端运行 20 倍完整模拟。

待验证：睡眠结束后 Apply 数量停止增长并回落，最旧动作年龄回落到正常范围；两端仍能在主机睡眠结束后进入同一昼夜，正常挖掘、交互和电路同步不受影响。

## Terrain Recovery 回放导致 Apply 队列爆发（当前轮）

状态：修复中

现象：华为端收到大量历史 `GameModifiedCellsMessage` 后，`Apply` 达到千级、CPU 100%、帧率降到 1 FPS 左右；可靠重传和 ACK 正常，恢复完成后性能恢复。

处理范围：

1. 恢复回放仍保留连续 terrain sequence 和 barrier 语义，但同一坐标只保留恢复窗口内的最终值；中间状态只推进 sequence，不再重复调用 `ChangeCell`。
2. 恢复批次继续使用客户端跨帧地形预算，不能阻塞普通玩家操作、可靠队列或电路序列。
3. 历史过期仍使用完整主机世界快照兜底，不跳过 barrier、不修改 ACK/可靠序列定义。

## VR-05 交互与世界时间同步

状态：修复中

1. 电路按钮本地预测与主机确认只允许一次点击音效。
2. 发射器 `Dispense/Shoot` 与 `Accepts drops` 配置走现有主机权威 editable-data 链，客户端不能只改本地 UI。
3. 双端睡眠加速、时间按钮和电路时间锚点必须以主机 `TotalElapsedGameTime + TimeOfDayOffset` 为最终权威；客户端保持单步模拟，通过主机权威时间快照跟随睡眠加速，旧 fence 不得覆盖新世界时间。
4. 弓/弩保留本地即时发射，但主机权威箭矢必须使用经过校验的同一起点和初速度，避免两端分别计算晃动与随机散布后再持续修正落点。
5. 合成桌权威快照整表落地后只计算一次配方，提示发送给实际打开面板的本地角色，不能按逐格中间状态或主机上的最近远程角色显示。
6. 进入房间后的首个生命快照只建立历史受伤序号基线，不得播放一次历史受伤音效。

实施结果：

- `GameWorldInfoMessage1` 与电路 `Fence` 现在携带同一条主机世界时间修订号。主机每次发布权威世界状态时推进修订号；客户端收到更高修订后，较低修订的旧 fence 只能刷新电路窗口，不能再写回旧的昼夜锚点。
- 主机正常状态按 2Hz、睡眠加速期间按 8Hz 发布权威世界时间；客户端始终保持 `FixedTimeStep=null`、`UpdatesPerFrame=1`，避免整套客户端世界执行 20 倍更新并把 `Apply` 队列拖入正反馈积压。
- 电路按钮用请求携带的安装面确认释放，并复用本地预测音效凭证；发射器配置已接入主机权威 editable-data 链。
- 客户端释放弓/弩时先执行原版动作并捕获实际箭矢起点、速度和角速度；主机校验箭矢类型、距离、速度和方向后应用到权威箭矢，碰撞与伤害仍只由主机决定。
- 合成桌网络快照不再逐格调用 `Remove/Add`，而是一次写入最终输入格后调用一次原版配方计算；主机本地提示不再被最近的远程角色截走。
- 生命同步首包现在记录 `DamageSequence` 基线而不播放声音，之后真实递增仍正常播放受伤音效。
- 协议签名已同步提升，`2.0.8` Release 构建和重构契约检查通过；正式包已替换 Windows 发布目录并部署华为平板，SHA-256 为 `728bedadb7427b4f9998dfad277157d91962eca2e3bd0403ee9aaad0970bbf1a`。

待验证：

- 主机与客户端同时睡眠后，两端在同一时刻进入白天，客户端不再停留在夜间。
- 睡眠结束后本机 `Apply` 不再持续增长，队列年龄能够回落，角色操作和画面帧率保持正常。
- 客户端随后点击时间按钮，两端昼夜结果一致；后续 fence 不得把任一端恢复到按钮前或睡眠前的时间。
- 客户端分别用弓和弩射击近、远目标，预测箭矢被主机接管时不改变方向，最终落点与首次看到的轨迹一致。
- 主机和客户端分别向合成桌放入可熔炼物品，操作端各显示一次 `Use a furnace to smelt this item`，另一端不出现逐格中间提示。
- 客户端重进创造模式房间时不再凭空播放一次主机或其他角色的受伤声；后续真实受击仍逐次有声音。

## 爆炸/岩浆后地形恢复洪泛并阻塞玩家操作（2.0.7）

状态：Release 包已生成，待双端在线验证

日志证据与原因：

- 红魔 ClientID=2 在 `15:58:10` 单独退出后，ClientID=4 仍留在房间，直到 `16:05:59` 才断开；因此不是一个客户端退出就结束房间或清空其他客户端。
- ClientID=4 异常期间仍能移动但挖掘失效。重传记录共 14,185 条、6,218 个不同包、约 5.52 MB，其中 `TerrainChunkSyncMessage` 3,356 条约 3.24 MB，另有 `GameModifiedCellsMessage` 952 条、`PickableSyncMessage` 583 条、`ExplosionSyncMessage` 212 条。
- 重传从 `16:01` 的 2,125 条/分钟升至 `16:05` 的 5,277 条/分钟；地形恢复日志中的 `Replay` 每轮只有 2-6 条，主要洪泛来自每个 Chunk 的权威校验响应。
- 客户端每 0.1 秒最多请求 4 个 Chunk（理论 40 Chunk/s），而当前每帧只分派 4 条 Chunk 消息；20 FPS 时处理能力约 80 条/s，遇到一个 Chunk 多个 Data 分片即没有吸收突发的余量。Chunk Data/Complete 还会在 checkpoint 尚未落地时形成重复请求压力。
- 岩浆待确认单元当前逐格创建 `GameModifiedCellsMessage` 和可靠序列；爆炸虽已按 48 格拆包，但仍会在短时间内制造多个可靠地形序列。地形分片和玩家操作共用可靠有序传输，缺口或积压会阻塞后续挖掘、交互等操作，而移动消息仍可替换发送，所以表现为“能走、不能挖”。

修复范围：

1. 客户端退出只清理该客户端的玩家实体、可靠目标、恢复状态和临时资源；不结束房间、不重置其他客户端。现有玩家操作继续使用可靠安全锁、请求 ID 去重和主机权威确认。
2. 恢复 ACK 继续携带客户端当前已缓存的 `BufferedRanges`，主机后续恢复轮按这些范围排除已收到但尚未连续应用的序列；不得以 `bufferedRanges=null` 重发已缓存片段。
3. 待确认流体单元按现有 48 格批量合并后再分配可靠序列，保留最终权威值、日志和 Chunk revision 更新语义。
4. Chunk 消息使用有界动态分派预算：基础能力高于当前接收速率，队列增长时短时提高上限，但仍受每帧毫秒预算和总动作上限约束；Data 必须先于 Complete。
5. Chunk checkpoint 的单元落地预算同步提高，revision 仍只能在所有单元实际写入后确认；队列达到高水位时先消化已有分片，不继续制造重复校验请求。
6. 不改变电路可靠序列、玩家动作可靠性、主机权威地形和全局恢复的连续序列语义。

实施结果：

- 恢复 `Acknowledge` 已携带客户端当前 `BufferedRanges`，后续恢复轮不再以 `null` 丢失缺失范围信息。
- Chunk 分派基础预算由 4 条/帧提高到 8 条/帧，积压时有界增长到 16 条/帧，仍受 4 ms 每帧总时间预算限制；checkpoint 落地预算由 64 提高到 128 个单元/帧。消息队列达到 32 条或 checkpoint 队列达到 16 批时暂停发送新校验请求，先消化现有工作。
- Chunk 的 `Complete` 不再提前结束 in-flight；只有 revision 全部落地后才移除 pending，失败或旧 checkpoint 会先释放 pending 再定向重试。
- 流体确认已按 48 格批量分配序列并广播，不再为每个单元单独生成可靠包。
- `ScMultiplayer` Release 构建通过，0 警告、0 错误；正式包已生成到 Windows 发布目录，SHA-256 为 `97e561589f0cebc8d9a3a205fecbfe999c881d916dcdb5bdb02d90d19e8e6738`，尚未部署设备或测试服务器。

验证范围：

- 连续扔雷、倒岩浆并移动离开加载区，Chunk 恢复只补缺失范围，已应用分片不重复广播，`Apply` 和可靠队列能回落。
- 一个客户端退出后，剩余客户端仍能即时挖掘、放置和交互，且其地形、角色和电路同步不被清理。
- 主机、本机客户端和华为平板同时在线时，地形分片应用速率高于接收速率；连续操作不再出现“只能移动、不能挖掘”。
- 检查重传日志中 `TerrainChunkSyncMessage`、`GameModifiedCellsMessage` 是否在爆炸/岩浆后成倍增长，确认电路可靠消息没有被地形恢复占满。

## 首次选角时 Joining Room 与角色界面反复切换（2.0.7）

状态：Release 包已部署测试服务端与红魔，待首次选角实测验证

日志证据：

- 红魔客户端首次加入新地图 `ceshi207` 时，主机正常返回 `SCMP_PROFILE_REQUIRED`。
- 客户端在角色资料选择完成前将该探测连接关闭误判为主机异常断线，随后每约 3 秒重复一次 `Host reconnect attempt 1/5`。
- 主机为每次重试分配新的 ClientID，但这些连接均未进入玩家索引保留、接受加入或地图传输阶段。

实施范围：

1. `SCMP_PROFILE_REQUIRED` 清除已请求、待执行和握手超时三项恢复重连状态。
2. `m_pendingJoinRequest` 仍在等待首次角色资料时，连接关闭事件与帧级失联检测均不得启动恢复重连。
3. 玩家选择完成后继续复用 `SubmitPendingJoin(..., hasPlayerProfile: true)`，不增加公开接口或新状态类。
4. 已完成加入后的真实断线恢复路径保持不变。

实施结果：

- `ScMultiplayer` Release 构建通过，0 警告、0 错误，混淆完成。
- 正式包已替换到 Windows 发布目录并部署红魔 `192.168.191.143:5555 / NX729J`，设备端与本机 SHA-256 均为 `15f8184b1d9dc2ce2108000f5f61861aca6de934fcc71fc81fda94315f6b4848`。
- 同一包已部署到 `139.155.99.152:22 / C:\SurvivalcraftServer`；测试实例 PID 932 已重新进入 `ceshi207`，构建指纹为 `25b03ba60006`，与红魔一致。正式实例 PID 2900 未停止。

验证范围：

- 新角色首次进入房间时只出现一次角色选择界面，`Joining Room` 不再周期闪烁。
- 选定角色后能够正常接受、下载并进入世界。
- 已加入玩家临时中断 UDP 时仍按既有有界恢复流程重连。

## 加入地图后连续回放大量地形中间状态（2.0.7）

状态：Release 包已部署测试服务端与红魔，待在线加入验证

日志证据：

- `201ser` 的地图包在约 3.62 秒内完成下载，但客户端直到约 24 秒后才完成 catch-up。
- 主机在该轮加入中记录 967 条 / 282,339 B journal 消息，并另外发送 18,345 个地形单元的最终 checkpoint。
- 普通 `GameModifiedCellsMessage` 的逐步变化已经进入 journal，末尾 `SendTerrainCatchUp()` 又按坐标发送最终权威值，导致同一地形先回放中间状态再落到最终状态。

实施范围：

1. joining client 不记录普通实时 `GameModifiedCellsMessage`；这些消息仍照常发送给已经完成加入的客户端。
2. joining client 只接收现有 `SendTerrainCatchUp()` 合并后的每格最终权威状态，其他容器、电路和世界事件 journal 保持不变。
3. 地形 catch-up 起点改为实际导出地图快照的 tick，排除已经包含在地图包中的变化。
4. 保留现有可靠传输、最终地形值、Chunk checkpoint 完整确认和客户端跨帧应用上限。

实施结果：

- joining client 的普通地形消息只触发已完成加入客户端的实时广播，不再复制进 join journal；非地形 journal 语义保持不变。
- `JoinCatchUpJournal.StartTick` 在地图导出后更新为 `HostedWorldSnapshot.Tick`，`SendTerrainCatchUp()` 继续发送该时点之后的最终权威状态。
- Release 包及测试部署信息与上一任务相同；远端测试实例当前 `serverError=null`、`frameError=null`、`connectedClients=0`，待客户端重新加入比较 journal 数量、完成时间与地形表现。

验证范围：

- 加入大量历史修改或刚加载区域时，不再看到同一批方块反复变化。
- 日志中的 join journal 消息数和 catch-up 时间显著下降，最终 checkpoint 单元仍全部应用。
- 同时在线的其他客户端继续实时看到挖掘、放置、流体、燃烧和坍塌结果。
- 加入完成后地形与碰撞和主机一致，不因压缩中间状态产生漏块。

## 进入已修改区域时 Apply 暴涨并卡顿（2.0.7）

状态：已完成 Release 构建并部署测试服务器，待客户端在线复现验证

日志证据：

- 客户端走到约 `109.25,84.23,-40.75` 后，主机集中返回 4 个已修改 Chunk 的权威 checkpoint。
- 同一轮约有 58 个 `TerrainChunkSyncMessage`、1631 个方块状态进入客户端；这些包仅发生一次可靠重传，不是丢包风暴。
- 原实现把每个 Chunk 消息都排入通用帧尾 `Apply` 队列，32 格 checkpoint 又因超过 8 格被降级到普通地形恢复队列，造成消息解析与大量 `ChangeCell` 集中执行。

实施范围：

1. `TerrainChunkSyncMessage` 使用独立 FIFO，每帧最多分派 4 条，保持 `Data -> Complete` 顺序。
2. Chunk checkpoint 使用独立地形队列和跨帧活动批次，每帧最多应用 64 个方块。
3. checkpoint revision 仍只在该批所有方块实际落地后确认；未就绪单元继续走原有延迟重试。
4. HUD `Apply` 同时统计通用动作和 Chunk 同步队列，网络状态重置时清空新增队列。
5. 不修改可靠序列、电路同步、主机权威或最终地形内容。

实施结果：

- `ScMultiplayer` Release 构建通过，0 警告、0 错误，混淆完成。
- 正式包已由本轮综合修复包替换，Windows 发布目录 SHA-256 为 `15f8184b1d9dc2ce2108000f5f61861aca6de934fcc71fc81fda94315f6b4848`。
- 同一包已部署到 `139.155.99.152:22 / C:\SurvivalcraftServer`，测试实例重新进入 `201ser`，控制状态无 server/frame error；正式运营实例未被停止。

验证范围：

- 客户端进入包含大量历史修改的区域时，`Apply` 有界下降，输入、挖掘和交互不再整段卡住。
- 4 个 Chunk 的最终方块和碰撞与主机一致，`Complete` 不得早于 Data 应用完成而提升 revision。
- 正常实时地形、电路和容器消息不被 checkpoint 队列阻塞，空闲流量不增加。

## 客户端 ACK 中断后被主机提前断开（2.0.7）

状态：修复中，待双端在线验证

日志证据：

- 客户端 `09:59:25` 后停止确认主机可靠包；105 个独立包进入重传，其中 104 个是 22 B 普通数据包，7 个达到 `retry=30`。
- 主机在 `09:59:33` 以 `Rel=100, Limit=6` 主动断开 ClientID 2；客户端随后报告主机 26.5 秒无响应。
- 单个 ACK 数据报丢失不足以解释持续 30 次重传，因为重复可靠包会再次加入 ACK 队列。
- `Comm.ProcessReceivedPacket` 原本在持有通信锁时同步调用上层消息处理，ACK 定时器也需要同一把锁；上层处理停顿会阻止 ACK 生成。
- 当前连接按完整 `IP:端口` 绑定，不支持未经重新握手的 NAT 端口迁移；此情况继续由有界重连恢复，不直接迁移旧会话。

修复范围：

1. 上层消息回调移出 `Comm.Lock`；包解析、可靠序列和 ACK 状态仍在锁内更新，保持消息顺序。
2. 达到可靠重传上限后增加 6 秒恢复窗口；恢复则清除停滞状态，持续异常才重建连接。
3. 已安排或正在执行重连时暂停旧 `Join barrier` 超时，避免它覆盖重连并切换为 `Disconnected`。
4. 保留 `MaxResends=30` 后停止继续重传的机制，不恢复无限重传，不修改电路可靠序列。

验证范围：

- 正常加入和运行时 ACK、可靠消息、地形、电路和容器同步没有顺序回归。
- 人为制造 5-10 秒 UDP/线程停顿时不会被主机立即踢出；恢复后未确认队列能够清空。
- 持续断网时仍会进入有界重连，不永久保留玩家连接或可靠队列。
- 加入屏障期间断线后自动重连，不再被旧 Transfer 的 30 秒超时取消。

## 在线运行后流量由约 30 Kbps 增长到约 160 Kbps（2.0.6）

状态：修复中，待双端在线验证

日志证据：

- `Retransmit-2026-08-06.log` 的最新会话中，`TerrainChunkSync` 请求已携带加入基线 `known=41`，2.0.6 的无条件 `known=0` 修复生效。
- 客户端仍会对每个已正常收到的实时地形 sequence 无条件登记 Chunk 二次校验；08:50-08:53 仅回环端点就产生 894 个独立 `TerrainChunkSyncMessage` 数据包和 355,928 B 重传。
- 08:54 外网端点 `123.147.236.213:25266` 的可靠 ACK 停滞：184 个独立可靠包产生 2,454 条重传、530,701 B 重传负载，最高 `retry=30`。
- 其中 `BodyUpdateMessage` 只有 36 个独立分片、原始负载约 32,899 B，却被重传 482 次、放大为 444,792 B，占该端点异常重传约 83.8%。
- 当前主机每秒把全部动物完整状态组成一个可靠 `BodyUpdateMessage`；多轮已经过期的完整快照可同时滞留在可靠队列，网络抖动会被放大为持续流量并挤占电路等可靠消息。

修复计划：

1. 取消 `HandleGameModifiedCellsMessage` 对所有正常实时 sequence 的无条件 Chunk 校验；只在网络单元因 Chunk 未就绪被延迟、`ChangeCell` 后最终值不一致，或 checkpoint 批次应用失败时登记定向校验。
2. 保留 2.0.6 的 join terrain head 基线、checkpoint 强制权威写入和 revision 完整确认规则，不能以降低流量为由重新引入漏块。
3. 动物普通位置、旋转和动作继续走可替换展示更新；动物创建、移除、变形和受伤边沿继续可靠发送。
4. 动物完整集合只作为恢复快照，改为 5 秒一次的可替换消息；新快照必须替代旧快照，不能进入可靠重传队列。
5. 不修改电路可靠序列；修复后确认 terrain checkpoint 和动物快照不会占满可靠窗口。

实施状态：

- 2.0.7 已移除正常实时 sequence 的无条件 Chunk 校验；仅在权威写入后的最终值不一致，或延迟单元被容量淘汰而无法保证落地时登记定向校验。
- 动物完整恢复快照已改为每 5 秒一次的 `latest` 可替换消息；可靠的创建、移除、变形和受伤边沿保持原路径。新客户端完成加入或地形恢复后会立即安排一份恢复快照。
- 2.0.7 Release 构建为 0 警告、0 错误；正式包已生成到 Windows 发布目录，SHA-256 为 `6eb0b21f97d07fe48b6f39441d5f7a6eb6a07c6aac991a28f613d51b5b30ee3e`。旧 2.0.6 发布包已移除，尚未部署设备或进行在线验证。

验证范围：

- 两个客户端同时在线并持续移动、挖掘、放置，地形和碰撞保持一致，运行数分钟后不会出现漏块。
- 正常落地的地形 sequence 不产生额外 Chunk 请求；只在故意制造未就绪/失败应用时出现定向 checkpoint。
- 动物逐步加载后流量不会从约 30 Kbps 持续增长到约 160 Kbps；重传日志中不再出现多轮 `BodyUpdateMessage` 达到 `retry=20-30`。
- 动物创建、移除、攻击、受伤、变形和远端展示仍正确；丢失一个恢复快照后，下一份快照可在 5 秒内恢复。
- 电路、容器、聊天和玩家操作的可靠消息不会被动物或地形恢复流量阻塞。

## 在线客户端仍遗漏主机已确认的实时地形增删（2.0.4）

状态：已部署在线复现，当前修复仍未覆盖问题

复现现象：

- 本机客户端连续挖除一批方块后，本机看到所有目标方块均已消失。
- 同时在线的华为平板只收到部分删除，中间仍保留几个方块。
- 当前再次复现：本机挖掘方块后，平板端仍有部分方块看起来没有被挖除；本机或主机放置方块后，平板端也会遗漏部分放置。
- 主机端已经记录对应的挖掘或放置结果，平板重连后可恢复为主机权威地形，说明不是主机拒绝操作，而是在线客户端的实时接收、应用或恢复链路仍会漏掉最终单元格。
- 华为关闭飞行后能够站在这些残留方块上，说明客户端保留了实际地形和碰撞，不是单纯的几何渲染缓存。
- 本机看到华为角色悬浮在已经删除的空气位置，并在位置同步过程中偶尔下落后再次被修正上去；这是两端碰撞地形不一致造成的位置反复校正。
- 华为退出地图并重新加入后，残留方块消失，说明主机权威地形和存档正确；遗漏只发生在华为在线接收或应用实时地形删除的过程中。
- 该问题在加入“主机地形头持续领先且客户端无推进时触发 terrain journal 恢复”的 2.0.3 包中仍能复现，上一轮修复没有覆盖全部遗漏路径。
- 2026-08-06 已在 `ScMultiplayer.cs` 增加已加载 Chunk 的定向权威校验：实时序列影响的 Chunk 在 0.35 秒合并后复用既有的限量请求队列；主机按客户端已确认 revision 仅返回新增最终单元格。Release 包已部署到本机和华为平板，尚未进行游戏内复现验证。
- 2026-08-06 继续修复 B 端“收到序列但未实际落地”路径：未 `Valid` 的 Chunk 不再直接写入；所有收到的实时序列都会保留定向校验需求；checkpoint revision 仅在所有 Data 批次实际完成 `ChangeCell` 后确认；延迟条目被覆盖或淘汰时取消该 revision 并重新请求该 Chunk 的最终权威状态。
- 2026-08-06 本机 Windows 与华为平板已部署此包后在线复现：Windows 挖掘或放置时，华为仍会遗漏部分方块；华为挖掘或放置时，Windows 也会遗漏部分方块。遗漏不再是单一 Android 或单一操作者路径，而是任一接收端都可能没有最终应用主机权威地形。
- 2026-08-06 诊断日志已确认客户端会收到主机 sequence，但未 `Valid` Chunk 的逐帧重试会耗尽诊断预算并挤占正常应用。2.0.4 已移除临时 `TerrainTrace`，改为每帧最多重试 128 个延迟单元、每个未就绪单元至少间隔 100 ms；同帧实际 `ChangeCell` 的几何失效改在该帧全部网络单元写入后执行，避免第一个单元使同 Chunk 的后续单元自我延迟。Release 构建通过，尚待双端在线复现。
- 2026-08-06 2.0.5 修复 checkpoint 旧响应确认漏洞：Chunk checkpoint 不再被本地 tick/sequence 去重跳过，且只有 checkpoint revision 已覆盖该 Chunk 所需的最高实时 sequence、全部单元实际写入后才提升客户端 revision；迟到的旧 checkpoint 会保留校验需求并重发定向请求。待本机与华为平板在线复现验证。
- 2026-08-06 已完成 `2.0.5` Release 构建与打包；Windows 发布目录和华为平板均为同一 SHA-256 包，旧 `2.0.4` 已从这两个部署位置移除。尚未进行游戏内验证。
- 2026-08-06 重传日志确认 2.0.5 的定向 Chunk 校验产生流量回路：新初始化 Chunk 无条件以 `known=0` 请求历史，主机按 32 cells 分批返回；2 秒超时在 `Complete` 到达前又请求同一历史。2.0.6 改为使用加入 catch-up 的 terrain head 作为 Chunk revision 基线，只对收到更高实时 sequence 的 Chunk 校验；Data 到达会刷新进度，应用层重试间隔提高为 5 秒。待验证同步正确且空闲流量恢复。
- 2026-08-06 `2.0.6` Release 构建为 0 警告、0 错误；包已替换 Windows 发布目录并部署华为平板，SHA-256 为 `ff888a496cf55132ee3c9f77b14cecdfe376f11fee8540df82a17bdd17de95de`。旧 `2.0.5` 已从两个部署位置移除，尚未进行游戏内验证。

下一轮核查范围：

1. 主机、本机客户端和华为平板在线复现 A 挖掘/放置、主机权威落地、B 接收的完整链路，确认 B 不重连即可请求并应用该 Chunk 的最终状态。
2. 覆盖目标 Chunk 已加载、已分配但未 `Valid`、尚未分配三条路径；延迟条目不得因容量限制、同坐标后续序列、网络状态重置或 Chunk 初始化顺序而被静默确认。
3. 确认 Chunk revision 只在 checkpoint 全部实际落地后提升；不能由 Data 的接收、`Complete` 报文或全局 terrain sequence 单独提升。
4. 确认定向校验只返回客户端已确认 revision 后的最终单元格，不重新广播全图，也不造成电路同步排队或空闲流量持续增长。
5. 若仍复现，按一次操作的 request ID、主机 sequence/journal、B checkpoint revision 与最终 `ChangeCell` 增加短期定向追踪；不得恢复逐帧单元日志。

验证范围：

- 主机、本机客户端和华为平板同时在线，连续挖除一条或一片普通方块、树木方块后，所有端的实际地形和碰撞完全一致。
- 华为站到刚删除区域并关闭飞行，不会站在本机所见的空气中，也不会因地形差异反复进行位置修正。
- 人为遗漏中间序列和最后一个序列时都能在线自动恢复，无需退出地图重新加入。
- 客户端序列已追上但单个方块未落地时，能够通过 Chunk 权威校验修复。
- 恢复期间电路、角色操作和正常地形同步没有明显延迟，空闲流量不会持续增长。

## 睡眠加速期间的网络降频与受击唤醒（2.0.8）

状态：已完成 Release 构建与部署，待在线验证

本轮产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，Windows 与华为平板 SHA-256：
`ad071e691169b2e86eb8a7dda915b18955bfdec9f2784958c7c35fea34f40880`

实施内容：

- 主机依据原版 `SubsystemTime.FixedTimeStep` 判断全员睡眠加速；客户端只依据主机
  `GameWorldInfo1Message.IsTimeAccelerated` 判断网络档位，不在客户端重复运行 20 倍世界模拟。
- 睡眠加速期间，玩家位置同步降为 1Hz，世界对象同步降为 2Hz，投射物状态同步降为 4Hz。
  位置/对象/投射物均为可替换状态；投射物创建、命中、移除等已有关键边沿仍通过原有即时或可靠路径发送。
- 睡眠加速期间普通玩家状态的重复生命/体力/温度广播最多 2Hz；生命下降、睡眠状态边沿和强制权威状态立即发送。
- 动物同步档位调整为：普通动物 1Hz、目标或附近动物 4Hz、高优先级/攻击动物 16Hz；非睡眠状态保持原有频率。
- 主机为每个玩家注册 `ComponentHealth.Attacked`。非玩家动物攻击正在睡眠的角色时立即调用 `WakeUp()`，发送权威生命/睡眠状态，使 `SleepFactor` 低于 1 并退出世界加速。玩家对玩家攻击不走该无条件唤醒分支。
- 不降低 ACK、可靠序列、电路 fence、地形 journal/recovery、伤害边沿、关键音效和容器/装备边沿同步；睡眠期间只降低可替换的连续状态。
- 在线复现发现：主机睡眠时原版每帧执行 20 次电路更新，而客户端仅执行一次。醒来后客户端需要追赶数万步 circuit step，`ShouldSuppressClientInput` 因此持续禁止移动和交互。修复后客户端在主机睡眠加速期间保持当前电路边界；收到 `IsTimeAccelerated=false` 后请求当前权威电路快照，按快照 host step 重设本地 offset，再恢复正常增量同步。
- 第二轮在线复现中客户端醒来后约为 CPU 97%、4 FPS、长时间不能移动，但最终自行恢复到约 57 FPS；状态栏期间仍显示 `Ckt Ready`。这确认客户端在慢速追赶大 circuit step 差，而不是断线或快照永久阻塞。原因是 `IsTimeAccelerated=true` 属于可替换世界状态，全部丢失时电路没有进入冻结档位。现由连续 fence 的 `HostCircuitStep/ServerStep` 增长比例直接识别 20 倍睡眠；连续 4 个 fence 恢复正常速率后触发一次快照重锚，不再依赖世界状态边沿必达。

待验证：

- 全员睡眠时客户端世界时间连续跟随主机，Apply 不持续堆积，普通 UDP 流量下降且关键电路/地形仍正常。
- 动物攻击任一睡眠角色后，主机立即退出睡眠加速，客户端收到生命/睡眠状态，`SleepFactor` 不再保持为 1。
- 睡眠结束、角色死亡、断线、重连和切换 Project 后，频率恢复正常且没有重复事件处理器或旧时间 fence 把昼夜拉回去。

## 首次加入后卡在 Joining Room 的电路引导竞态（2.0.4）

状态：待修复

复现现象：

- 本机连接已建立、世界下载完成且 Project 已加载后，界面持续显示 `Joining Room`、`Applying host changes`、`Circuit: Synchronizing`。
- 本机日志显示 `World download complete`、`Client project ready`，但缺少 `Client circuit bootstrap complete` 与 `Client catch-up complete`。
- 同一客户端重新启动并再次加入可正常完成，说明不是协议版本、世界包或持续网络故障。
- 2026-08-06 复现细节：Windows 在主机开服后第一个加入时卡在 `Joining Room`；华为平板随后可正常加入；Windows 关闭并重新打开游戏后再次加入也可正常完成。
- 2026-08-06 已修改为保留待执行 bootstrap 请求，在新的电路子系统绑定及 epoch 就绪后自动启动快照；待首次加入复现验证。
- 2026-08-06 诊断包验证：Windows 先加入时已不再卡住；随后华为平板首次加入时卡在 `Joining Room`。说明待执行 bootstrap 修复覆盖了 Windows 时序，但 Android 的 Project/electricity/epoch 绑定顺序仍有独立的遗漏触发点。

已确认与待确认原因：

- 主机的 `CatchUpBatchComplete` 可能先于新 Project 的 `SubsystemElectricity` / circuit epoch 绑定到达。
- `CircuitSynchronizer.BeginJoinBootstrap()` 在 `m_subsystem == null` 或 `m_epoch <= 0` 时直接返回；当前没有保存待执行的 bootstrap 请求，也没有在 `EnsureBound()` 后补发。
- 只有后续偶然收到 fence/clock 时才可能自愈，因此首次加入会间歇性永久停留在加入屏障。
- 上述早到竞态已由待执行 bootstrap 覆盖了 Windows 首次加入：Windows 本轮完整出现 bootstrap 与 catch-up complete。
- 华为本轮 Transfer=2 在 `World download complete`、`Client project ready` 后直接停住，同时已收到 terrain sequence 48/49，但缺少 `Client circuit bootstrap complete` 和 `Client catch-up complete`。这说明该次失败发生在更早的阶段：客户端没有收到或没有处理 `CatchUpBatchComplete`，因此没有执行 `BeginJoinBootstrap()`。
- 2026-08-06 主机诊断日志已确认该次 ClientID=2 / Transfer=2 的 completion callback 未触发：主机已排入 41 条 / 30,398 B catch-up 和 22 个 terrain 单元，但之后没有 `Join post-cutoff batch queued` 或 `World transfer ready`。在 callback 有机会发送 `CatchUpBatchComplete` 前，主机的该客户端可靠未确认队列增长到 `Rel=97`，而 bulk 窗口限制为 `4`，最终触发 `Host reliable transport stalled` 并断开。因此不是单个 marker 偶发丢失，而是 completion marker 被依赖的加入可靠队列阻塞在尾部。
- 2026-08-06 流量核查：游戏内 UDP 统计在 `DiagnosticTransmitter.SendPacket` 的实际底层发送前累加，可靠重传会按每次实际发送计入，不是 UI 重复计数。历史重传日志中同一秒出现大量约 560-620 B 的 `GameModifiedCellsMessage retry=1`，且均为 `catchUp=False target=-1` 的普通实时地形广播。约 97 个未确认分片的一轮重传即可额外产生约 55-60 KB；叠加正常包、多次重传和 ACK，足以使稳定约 30 KB/s 升至约 150 KB/s。加入未完成客户端必须不接收普通实时可靠广播，避免其队列既阻塞完成 marker 又持续制造重传流量。
- 2026-08-06 2.0.4 已将 scheduled、circuit、world-object、动物音效与击退等 `target=-1` 广播在存在 joining client 时分发给已完成加入的客户端；joining client 仅接收其 journal/checkpoint，避免普通实时可靠消息进入其窗口。客户端既有的 `ProjectReady` / `CatchUpBatchApplied` 定时重发仍保留，主机会对重复 `ProjectReady` 重发 `CatchUpBatchComplete`。Release 构建通过，尚待首次双端加入与流量复现。
- 2026-08-06 `2.0.5` 首次加入可靠队列隔离已经部署验证；后续流量回路修复升为 `2.0.6`，保留游戏内首次双端加入验证。

修复范围与验证：

1. 在客户端记录与 Transfer 绑定的待执行 bootstrap 状态；仅在 Project、电路子系统和 epoch 全部就绪后启动一次快照请求。
2. 保持现有 catch-up 语义：只在权威电路快照实际应用后发送 `CatchUpBatchApplied`，不能提前放行角色操作或电路输入。
3. 验证首次加入和重连都能收到 bootstrap/catch-up 完成日志；连接中断或真正失败时按现有失败路径关闭 Joining Room，不能停在 30 秒提示状态。
4. 将加入 catch-up 与普通实时可靠流量隔离，并为每个 joining client 保留严格有界的可靠窗口：未完成 catch-up 的客户端只接收其 journal/checkpoint，不让新的 live 广播持续堆入同一未确认队列。
5. 为 `CatchUpBatchComplete` 增加与 Transfer 绑定的确认/重发屏障：主机在 join journal 实际发送完后按有限间隔重发 completion marker，直至收到 `CatchUpBatchApplied`；客户端按 Transfer 去重但每次均可触发待执行 bootstrap。该 marker 不走电路可靠序列，也不依赖 bulk catch-up 队列清空后的一次性 callback。

## 睡眠加速结束后级联电路状态滞后（2.0.8 后续修复）

状态：已完成 Release 构建与 Windows/华为部署，待双端在线验证

本轮产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，Windows 与华为平板 SHA-256：
`121da1abca562197c038bfb64dc0d08680a0d24ec4429dd57513ed337dd3eaef`

复现现象：

- 主机和客户端同时睡眠，世界进入加速时间流动；醒来后，已被新信号修改的加法器/计数器能够逐步一致，但没有立即收到新边沿的后级电路仍保留旧值。
- NOT 门振荡器连接多级加法器或计数器时，主机可显示 `65`，客户端仍显示 `23`；后续边沿到来后客户端逐级追赶，主机到 `70` 时客户端才整体接近 `70`。
- 级联层数越深，客户端自我修正所需时间越长，不能依赖等待振荡信号自然传播。

原因判断：

- 当前客户端虽然在唤醒边沿请求完整快照，但快照没有绑定一个由主机确认的唤醒最终电路边界；主机抓取快照和继续推进电路之间存在时间窗口。
- 客户端收到全部分片后，主要按可靠序列和分片数量放行，没有要求收到不早于快照主机步的新的正常 fence。
- 快照应用过程中，部分原版待模拟队列和边界前的 Mod 调度可能残留；同时 `ApplyStateRecord()` 对缺失或类型不匹配的元件直接跳过，仍会把快照标记为完成。
- `NotifyRemoteTimeAccelerationChanged(false)` 之前会在远端标记已经是 `false` 时直接返回；当加速状态是由 fence 速率推断出来时，`m_inferredTimeAccelerated` 仍会保持为 `true`，客户端继续冻结电路，直到后续 fence 样本触发自愈。
- 客户端 Project 加载后、`CircuitSynchronizer` 绑定前，原版 `SubsystemElectricity.Update()` 仍可能执行一次加载队列，产生主机不存在的本地边沿。

修复范围：

1. 唤醒快照建立明确的主机边界和代次，客户端在边界确认前保持电路恢复锁。
2. 快照应用前清理旧的原版电路调度和边界前的 Mod 调度，只恢复快照记录中的电压、计数器、边沿状态和 `NextSimulationSteps`。
3. 快照应用必须统计并验证所有元件；存在缺失或类型不匹配时不能放行，按现有分片请求机制重试。
4. 保留边界之后的有效事件，丢弃边界之前的旧事件；不重放整段睡眠期间的 circuit step。
5. 恢复后第一个 checkpoint 必须重新校验，不能改变电路可靠序列、地形序列或普通玩家操作的可靠性。

本轮实施：

- 联机客户端在同步器尚未绑定时跳过原版电路更新，并清零本地时间余量，避免加载阶段提前模拟。
- 主机权威的“不再加速”状态会清除 fence 速率推断标志，并在加速到正常的边沿触发一次重锚快照。
- 重锚保存快照的 `HostCircuitStep`、`LastSequence` 和时间线代次；客户端必须收到覆盖这些边界的 fence 才解除恢复锁。
- 快照应用前同时清理 `m_nextStepSimulateList` 和 future simulation 中的旧条目，保留现有快照字段与延迟历史恢复逻辑。

验收标准：

- 睡眠结束后的第一个有效 fence 内，振荡器及所有级联加法器/计数器与主机一致，不再逐级追赶。
- 快照期间不会出现 CPU 97% 以上、Apply 队列持续增长或输入长时间被禁止。
- 丢失 world-info、fence、快照分片或客户端重连时仍能恢复；正常非睡眠电路同步行为不变。

构建约束：

- 发布和验证必须使用 Release 配置，禁止用 Debug DLL 或 Debug `.scmod` 作为最终产物。
- 由于根项目的历史循环引用，Release 依赖按 `Engine → EntitySystem → Survivalcraft(CoreCompile) → SuAPICore → Survivalcraft(完整 Build) → Comms → ScMultiplayer` 顺序构建；不修改项目引用来绕过该顺序。

## 客户端睡眠加速导致 1 FPS 与 Apply 持续积压（2.0.8 后续修复）

状态：已完成 Release 构建与 Windows/华为部署，待双端在线验证

复现现象：

- 客户端先睡、主机随后睡下并进入加速后，客户端帧率可降到约 1 FPS。
- 醒来后右上角 `Apply` 每次刷新增加 100 以上，最老等待时间持续按秒增长。
- 客户端先于主机醒来后，客户端电路恢复正常速度，主机电路仍在 20 倍睡眠速度运行。
- 客户端帧率正常时可消费约 2000 条 Apply/秒；因此瓶颈不是 Apply 上限，而是睡眠期间本地逻辑占满帧时间，导致既有 4ms Apply 窗口失去调度机会。

原因判断：

- 客户端仍运行原版 `ComponentSleep.Update`。复制到客户端的角色全部达到 `SleepFactor == 1` 后，原版 `SubsystemTime.NextFrame` 会把客户端误判为权威的全员睡眠世界。
- 每个角色原本保存独立的 `SleepStartTime`；先睡角色可以先满足自动唤醒条件，使客户端在主机仍加速时提前恢复本地电路。
- 客户端 Project 已加载但 `CircuitSynchronizer` 尚未绑定的窗口仍可能执行一次原版电路加载队列，产生主机不存在的初始电压边沿。

本轮实施：

- 使用 Mod 数据库替换新增 `SuComponentSleep`。主机保持原版睡眠更新；客户端由主机权威的加速状态约束自动唤醒，并将复制睡眠因子限制在小于原版全员睡眠判定值，避免客户端进入 20 倍世界循环。
- 手动唤醒输入仍走现有客户端请求，不能被权威睡眠保护吞掉。
- 主机首次进入睡眠加速时，将所有已睡角色的 `SleepStartTime` 统一为同一个主机世界时间，并立即发布角色状态。
- 客户端已连接而电路同步器尚未绑定时，`SuSubsystemElectricity` 始终跳过原版 bootstrap 模拟。
- 切图、断线和重连时清除共享睡眠会话状态。

验收标准：

- 两端全部睡下后，客户端保持正常帧率，不出现约 1 FPS。
- 睡眠和醒来期间 Apply 队列可及时清空，数量与最老等待时间不持续增长。
- 主机与客户端在同一权威睡眠边界醒来；客户端不得在主机仍加速时提前恢复电路。
- 客户端首次进入地图时不再产生主机不存在的初始电压信号。

## 首次加入电路快照提前启动并卡在 Recovery（2.0.8 后续修复）

状态：已完成 Release 构建与 Windows/华为部署，待双端在线验证

本轮产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，Windows 与华为平板 SHA-256：
`121da1abca562197c038bfb64dc0d08680a0d24ec4429dd57513ed337dd3eaef`

复现与证据：

- 客户端世界已加载但持续显示 `Joining Room`，右上角 `Ckt Recovery`；`Fence` 从约
  `56ms` 增长到 `700ms` 后回落并反复循环。
- Fence 年龄周期性回落说明主机 fence 仍在到达，不是双向 UDP 中断。
- 2026-08-08 主机日志中，31,555 B 的首轮 join catch-up 从 `13:34:25` 排队到
  `13:37:59` 才完成；期间又累积 480 条、49,574 B post-cutoff 消息。
- `HandleFence()` / `HandleClock()` 在收到 `CatchUpBatchComplete` 之前提前请求首次
  电路快照，使电路 bulk 与地图 catch-up 争用同一可靠加入窗口。
- 首次快照完成后又错误复用睡眠重锚的“等待快照之后新 fence”屏障，恢复锁无法按
  普通首次加入条件及时解除。

本轮修复：

- 首次权威电路快照只由 `CatchUpBatchComplete -> BeginJoinBootstrap()` 启动；普通
  fence/clock 在此前仅建立 epoch 和时钟，不提前请求快照。
- 首次快照仍清理客户端加载阶段的原版电路调度、应用权威运行时状态，并延迟保存尚未
  实例化的远处电路状态。
- “快照应用后必须收到覆盖边界的新 fence”仅用于睡眠结束重锚；首次加入不再增加该
  屏障，避免 `Recovery` 循环。
- Release 验证必须确认 catch-up 完成后才开始电路 bootstrap，且依次出现
  `World transfer ready`、`Client circuit bootstrap complete` 和
  `Client catch-up complete`。

## 睡眠加速结束时客户端提前醒来（2.0.8 后续修复）

状态：已完成 Release 构建与 Windows/华为部署，待双端在线验证

本轮产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，Windows 与华为平板 SHA-256：
`67724cf6992887f143114460805dde3e12a1cf438de0040950ff51c9b46cfe63`

复现现象：

- 主机与客户端均睡眠并进入加速后，客户端会比主机提前醒来一瞬间。
- 客户端醒来时仍显示睡前电路值，主机计数器已经在 20 倍模拟中前进很多；稍后才开始
  电路恢复。

原因：

- 主机睡眠状态下降沿作为玩家生命状态立即发送，而 `IsTimeAccelerated=false` 原本只随
  普通 2Hz 可替换世界状态发送。
- 客户端可能先执行玩家 `WakeUp()`，之后才得知主机电路已经退出加速并开始权威快照
  重锚，因此出现醒来画面和电路状态边界错位。

本轮修复：

- 主机检测到 `FixedTimeStep` 下降沿后立即可靠发布加速结束的世界状态，并发布所有玩家
  的最终睡眠状态，不等待普通世界同步周期。
- 客户端先收到 `IsSleeping=false` 时，若主机仍加速或电路重锚未完成，则暂存该醒来状态。
- 客户端继续使用当前权威快照直接重锚到主机最终电路边界，不重放睡眠期间的数万步；
  快照和后续 fence 完成后才执行暂存的 `WakeUp()`。
- 断线、切图和重连时清理暂存醒来状态。

验收标准：

- 客户端不会早于主机醒来；客户端醒来时计数器、加法器及后级电路已与主机一致。
- 睡眠退出不产生高 CPU、Apply 持续增长或逐步追赶数万 circuit step。

## 主机醒来后客户端保持睡眠（2.0.8 后续修复）

状态：代码修复完成，已完成 Release 构建与 Windows/华为部署，待双端在线验证

本轮产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，Windows 与华为平板 SHA-256：
`c79e6d17d4d3506b00e1afe93d16b8c84230e42ad9aec093ace06b94e67a052e`

复现现象：

- 主机与客户端完成睡眠加速后，主机已经醒来，客户端仍保持睡眠状态。
- 客户端状态栏已经显示 `Ckt Ready`、`Apply 0`，说明电路重锚已完成，不是快照或
  Fence 恢复仍在阻塞。

原因：

- 加速状态分别保存在 `ScMultiplayer.m_remoteTimeAccelerated` 和
  `CircuitSynchronizer.m_remoteTimeAccelerated`。
- 世界状态下降沿缺失或较晚时，`CircuitSynchronizer` 能通过连续正常 Fence 判断加速
  已结束，并完成重锚；但该判断此前只清除了同步器内部状态。
- 即使加速状态已清除，客户端仍依赖单次 `IsSleeping=false` 健康包先把角色加入待唤醒
  集合；该包丢失或在恢复窗口外到达时，集合为空，`Ckt Ready` 后没有再次唤醒动作。

本轮修复：

- 连续正常 Fence 确认加速结束时，同时回写并清除外层加速状态，使睡眠门控和网络降频
  使用同一恢复结论。
- 世界状态下降沿和 Fence 推断下降沿都会设置独立的“睡眠会话结束待处理”标记；它不
  依赖单个健康包，并在电路恢复屏障完成后扫描当前已加载的本地角色，将仍睡眠的角色
  加入现有待唤醒集合，再统一执行原版 `WakeUp()`。
- 切图、断线和重连时清除该标记；没有本地角色或电路仍在恢复时保留标记，避免过早丢失
  唤醒边界。
- 同步器存在时，睡眠门控以同步器的加速状态和 `IsClientBootstrapReady` 为准；外层状态
  仅在同步器尚不可用时作为加载阶段兜底。
- 保留原有规则：加速仍在进行或睡眠后电路重锚尚未完成时继续延迟醒来；只有重锚完成
  并达到 `Ckt Ready` 后才释放待唤醒角色。

验收标准：

- 主机结束加速后，客户端在电路重锚完成时自动醒来，不需要触摸屏幕或重新进入房间。
- 客户端醒来时计数器和级联电路已与主机一致，不能提前显示睡前状态。
- 睡眠结束后网络同步恢复正常档位，`Ckt Ready`、`Apply` 和 Fence 不持续异常。

## MP 手动电路同步（2.0.8）

状态：视觉刷新已修复，Release 构建及 Windows/华为部署完成，待双端运行验证

本轮产物：`[SuAPI]ScMultiplayer-2.0.8.scmod`，Windows 与华为平板 SHA-256：
`862e725d79ed159f2a1fa221d9bc61a9dfb49a039dbe4784e7ceda5d5f90b4f8`

功能：

- MP 列表增加 `Circuit Synchronization`，仅在已经进入联机房间时显示。
- 客户端点击后请求主机当前全部已实例化电路状态，进入独立的手动重锚屏障；快照应用前
  暂停客户端电路并清除旧调度、边界前事件和旧的原生待模拟项。
- 主机点击后，仅通知已完成加入的客户端各自发起完整快照请求，不向 Joining 客户端的
  可靠窗口追加任务。
- 快照完整应用电压、计数器、延迟历史和运行时状态；客户端尚未实例化的元件进入现有
  延迟状态表，待对应 Chunk 和电路元件加载后再应用。
- 重锚前清除的旧模拟队列不会继续运行；当前已加载元件会按主机快照中的
  `NextSimulationSteps` 恢复后续调度，避免 `Ckt Ready` 后电路数值正确但逻辑停住。
- 恢复调度只排入主机捕获的元件自身计划任务，不重新排队整张电路连接，避免制造
  客户端独有的输入边沿。远处未加载元件继续等待对应范围的当前快照。
- 快照应用后必须收到覆盖该权威边界的新 Fence 才恢复客户端电路，避免旧的本地模拟
  在同一帧重新覆盖刚拉取的主机值。
- 快照直接写入七段数码管和 LED 的 `m_voltage` 后，会使其视觉缓存失效并仅排入这些
  显示元件的原版 `Simulate()`；不重新模拟业务电路连接，也不制造额外输入边沿。
- 已有快照、Recovery、Fence 失效或电路尚未完成首次引导时不重复启动任务，界面显示
  当前同步状态。
- 选择 MP 中的 `Circuit Synchronization` 后立即关闭列表并返回游戏，不再显示结果弹窗。

验收标准：

- 客户端点击一次后状态栏进入 Snapshot/Recovery，完整快照和后续 Fence 完成后恢复
  `Ckt Ready`，全部已知电路数值与主机当前权威状态一致，并从该状态继续运行。
- 主机点击后所有 Ready 客户端分别完成一次同步，Joining 客户端的地图下载和加入屏障
  不受影响。
- 强制同步不改变地形序列，不阻塞实时电路边沿的可靠保留容量，也不会形成重复快照循环。

## 客户端睡眠不执行主机 20 倍加速（2.0.8）

状态：代码调整、Release 构建及 Windows/华为部署完成，待双端运行验证

问题：

- 主机进入全员睡眠后应独自执行原版 20 倍世界更新；客户端曾在收到权威加速边沿前让
  `SleepFactor` 达到 `1`，从而短暂进入本地 `UpdatesPerFrame=20`。
- 加速世界时间使原版“睡眠十秒后允许手动醒来”很快满足，开始睡眠的残留输入可能让
  客户端先本地醒来，随后又被主机权威睡眠状态拉回，形成醒来/睡下抖动和重复电路重锚。

修复：

- 连接中的客户端从进入睡眠第一帧起就使用主机权威睡眠分支，并把显示用
  `SleepFactor` 限制为 `0.999`；客户端始终保持 `FixedTimeStep=null`、
  `UpdatesPerFrame=1`。
- 主机进入和退出睡眠加速都可靠发布状态边沿。加速期间客户端不逐次应用主机的 20 倍
  世界时间，也不追赶睡眠期间的电路步，只保留正常单步客户端循环。
- 退出加速时客户端一次性应用最终世界时间，拉取主机最终电路快照并等待后续 Fence；
  完成后恢复快照捕获的后续调度，再退出睡眠画面。
- 手动醒来必须先释放开始睡眠的输入，再进行一次新输入；客户端只向主机请求醒来，
  不提前执行本地 `WakeUp()`。

验收：

- 主机和客户端均睡下后，客户端帧率、CPU 与 Apply 队列保持正常，不出现本地 20 倍更新。
- 睡眠画面不发生醒来后再次睡下的抖动；新输入仍能请求主机提前醒来。
- 主机结束加速后，客户端只进行一次世界时间与电路重锚，随后两端昼夜、计数器、
  振荡器和级联电路一致并继续运行。
