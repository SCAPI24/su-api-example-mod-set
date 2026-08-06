# ScMultiplayer 当前修改清单

本文件只保留尚未处理完成的任务，用于跨轮次继续工作。

- 新发现或未完成的问题立即加入本文件。
- 完成源码修改、Release 编译和要求的部署后，从本文件删除对应任务。
- 已完成任务不在本文件保留历史记录，历史以 Git 提交和发布包为准。
- 每轮修改 ScMultiplayer 前先核对本文件，避免上下文压缩后遗漏任务。

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

\n