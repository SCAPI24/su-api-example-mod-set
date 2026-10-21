# 断线重连：会话标识 + 追帧（SuMultiplayer）

本文记录 `SuMultiplayer` 的**重连 / 首次加入**语义、线上追加字段与客户端落点。
对应的旧实现是 `Mod/ScMultiplayer`（只读参照，未改动）。

## 1. 用户口径（本实现的唯一语义）

| 场景 | 判定 | 世界传输 | 地形同步 |
|---|---|---|---|
| 从 Play 屏点房间加入 | 不在游戏内 / 没有会话 | **整图下发一次** | 按新世界基线 |
| 游玩途中断网几秒，主机**没有重启** | 在游戏内 + 下载世界 + 会话一致 | **不发整图** | 从"上次应用序号"重放缺失改动（追帧） |
| 主机重启 / 换房间（会话变了） | 会话不一致 | **整图重下** | 新世界基线 |
| 上次应用序号已被历史裁掉 | 序号越界 | **整图重下**（明确拒绝追帧） | 新世界基线 |

主机分不清"同一个人的新连接"和"第一次加入"，所以这些事实全部由客户端在 Hello 里明说；
**主机是最终判定者**（会话 + 序号范围），客户端只是把已知事实带上。

## 2. 会话标识（Host session id）

- 生成：`Terrain/SessionIdentity.NewSessionId()` —— 128 位密码学随机、32 位小写十六进制。
- 时机：`TerrainSyncModule.StartHostServer`（玩家按 CR **建房 / 房间会话开始**）生成一次。
- 生命周期：**只在内存里**，不写文件、不写 `Project.xml`、不写世界目录；主机重启必然换新值。
- 下发：
  - `HelloAck` 末尾追加（主机 → 客户端，见 §3.2）；
  - `WorldBegin` 世界清单末尾追加（主机 → 客户端，见 §3.3）。
- 客户端落点：下载世界登记表
  `data:/SuMultiplayerDownloadedWorlds.txt`，一行
  `sessionId \t baseSequence \t directory`。
  旧格式的裸目录行仍然可读（= 没有会话标识 ⇒ 重连时按首次加入处理，不猜）。
  见 `Engine/EngineWorldTransfer.cs` 的 `RegisterSession / TryGetSession / ForgetSession`。

## 3. 线上协议（只追加，旧编号/旧字段顺序一个不动）

`Net/TerrainNet.cs` 的 `TerrainMessageType`：

| id | 名称 | 方向 | 说明 |
|---|---|---|---|
| 1 | Hello | C→H | 末尾追加会话标识 + 上次应用序号（§3.1） |
| 2 | HelloAck | H→C | 末尾追加主机会话 + 是否接受重连 + 水位 + 依据（§3.2） |
| 7 | CellChange | H→C | 末尾追加每条改动的序号（§3.4） |
| 11 | WorldBegin | H→C | 末尾追加主机会话 + 导出基线序号（§3.3） |
| **20** | **CatchUp** | C⇄H | **本版新增**（只追加 id；19 之前一个不动） |

实时地图 feed **未改动**（仍是 `payloadVersion = 8`，`doc/live-map.html` 未触碰）。

### 3.1 Hello（客户端 → 主机）

旧字段：`kind(1) name text 1 1 radius int32`，随后是既有追加：`identity text`、`alreadyHasWorld(1)`。
本版再追加：

```text
sessionId text        // 下载世界登记的主机会话；没有 = 空串
lastApplied int64     // 上次应用到的改动序号；没有 = 0
```

`alreadyHasWorld` 的唯一判定在 `Terrain/RejoinFlagPolicy.cs`：
在游戏内（GameScreen）+ Project 已加载 + 当前世界是下载世界 + **下载世界有会话标识**
+ **与最近一次 HelloAck 的主机会话一致**，缺一不可。Play 屏 / GameLoading / 缺失 / 不一致 ⇒ false。

### 3.2 HelloAck（主机 → 客户端）

旧字段：`1 clientId text 0 20`，随后是既有追加：`hostIdentity text hostName text`。
本版再追加：

```text
hostSessionId text    // 主机当前会话
rejoinAccepted(1)     // 1 = 接受重连（会话一致 + 序号仍在历史内）
headSequence int64    // 主机当前改动水位
reason text           // 接受 / 拒绝的明确依据（写日志；客户端据此作废旧会话）
```

`reason` 非空 + `rejoinAccepted=0` 表示：本版主机**会在同一条连接上直接下发整图**。
`reason` 为空 + `rejoinAccepted=0` 表示：旧构建（没有会话字段）——客户端必须断开重连成首次加入。

### 3.3 WorldBegin 世界清单（主机 → 客户端）

旧字段：`transferId worldName directoryName totalBytes chunkCount chunkBytes`。本版追加：

```text
sessionId text        // 本世界快照所属的主机会话
baseSequence int64    // 导出**开始前**主机已广播到的改动序号（客户端的初始"上次应用序号"）
```

`baseSequence` 必须在导出开始前取：导出期间产生的改动要么已进快照（重放幂等），要么会在追帧里补上；
取在导出之后则可能漏掉这段。

### 3.4 CellChange（主机 → 客户端，实时与追帧共用）

旧字段：`count int32`，然后每条 `chunkX chunkZ x y z packed`（各 int32）。本版在**条目之后**追加：

```text
seqCount int32        // 恒等于 count（新主机）
seq[seqCount] int64   // 每条改动的全局递增序号
```

旧读取器读完 `count` 条就停，尾部字段被忽略；新读取器用 `Remaining` 判断。
追帧时同一帧可带多条改动（默认 ≤64 条/批），实时仍是 1 条/帧。

### 3.5 CatchUp（id 20）

固定布局：

```text
stage(1) lastApplied int64 head int64 firstRetained int64 changeCount int32 reason text
```

| stage | 方向 | 含义 |
|---|---|---|
| 0 Request | C→H | 我上次应用到 `lastApplied`，请重放之后的改动 |
| 1 Begin | H→C | 本轮范围：请求序号 / 水位 / 最旧保留序号 / 将重放条数 |
| 2 Barrier | H→C | 重放数据已全部发出（客户端应已到水位） |
| 3 Rejected | H→C | **明确拒绝**（历史裁剪 / 不是被接受的重连）→ 客户端必须整图重下 |
| 4 Applied | C→H | 我已应用到 `lastApplied`（未到水位时主机会再发一轮） |

## 4. 主机侧有界改动历史

`Terrain/TerrainSync.cs` 的 `TerrainSyncHost`：

- 每条权威改动 `BroadcastCellChange` 先递增 `m_changeSequence` 并写入 `m_changeJournal`
  （**即使当时没有订阅者**：否则重连会留下无法重放的洞）。
- 裁剪（`TrimChangeJournalLocked`）：
  - 时间上限 `CatchUpRetentionMs = 60000`；
  - 条数上限 `CatchUpMaxJournalEntries = 8192`；
  - 裁掉的条数计入 `ChangeJournalEvictions`，限流写一行明确原因。
- 追帧发送（`SendCatchUpBatchesLocked`）：
  - 每批 ≤ `CatchUpBatchEntries = 64` 条；
  - 每 tick ≤ `CatchUpBatchesPerTick = 4` 批；
  - 发完发 `Barrier`；等 `Applied` 超过 `CatchUpAckTimeoutMs = 30000` 写一行明确原因并放弃这一轮。
- **范围判定**：`lastApplied < 0`、`> head`、或 `< 最旧保留序号 - 1` ⇒ 拒绝（`Rejected` / HelloAck 拒绝），
  绝不拿过期世界继续玩。
- 发送失败（显式背压 / 连接不可用）**保留任务**下一 tick 重试；拒绝帧 / Begin / Barrier 发送失败也明确记一行。

## 5. 客户端侧

- `TerrainSyncClient` 维护 `LastAppliedChangeSequence`：
  - 世界导入时 `SetAppliedChangeSequence(baseSequence)`（清单基线）；
  - 每条 `CellChange` 尾字段推进；
  - 跨连接由 `TerrainSyncModule` 保存/恢复（断线时保存，重连 Hello 带上）。
- 接受重连后：先 `InterestUpdate`（订阅先就绪），再发 `CatchUp Request`；
  `Begin` 设置目标水位，`Barrier` 后若已到水位就发 `Applied` 并完成。
- `Rejected`（追帧）或 HelloAck 拒绝（且主机不会发整图）：
  作废下载世界的会话（`ForgetSession`）→ 断开 → 以**首次加入**身份重连 → 主机发整图。
  本版主机在同连接直接发整图时只作废会话、保持连接，等导入完成。
- 游戏内整图重下的落点：`UI/RoomUiAttacher.TryEnterRefreshedWorld()`（`GameScreen` 下消费
  `IWorldReadyPort`，走引擎原本的 `GameLoading`）；Play 屏仍在自己的 `PollReadyWorld` 里消费。

## 6. 旧实现对照（ported pieces）

| 本工程 | 旧实现 |
|---|---|
| 会话 / 世界基线 / 追帧协议 | `Mod/ScMultiplayer/Message/TerrainRecoveryMessage.cs`；`Modules/Terrain/ScMultiplayerTerrainHandlers.cs:774-998`（`SendHostTerrainRecoveryRound` / ReplayBatch / Barrier / `ResyncRequired`） |
| 有界改动历史 + 裁剪 | `Modules/Terrain/ScMultiplayerTerrainHandlers.cs:311-358`（`RecordHostTerrainJournal` / `TrimHostTerrainJournalLocked`） |
| 60 秒保留常量 | `Core/ScMultiplayerRuntimeState.cs:894`（`TerrainRecoveryRetention = 60.0`） |
| 请求 / 确认循环 | `Modules/Runtime/ScMultiplayerUpdateLoop.cs:173-273`（Request / Acknowledge / Ready） |
| 历史过期 → 重下世界 | `Modules/Terrain/ScMultiplayerTerrainHandlers.cs:977-998`（`RestartClientWorldDownload`） |
| 加入时追帧（StartTick/HeadSequence） | `Modules/Join/ScMultiplayerWorldTransferHandlers.cs:58-110`（`SendTerrainCatchUp`） |
| 客户端应用追帧（HeadSequence 水位） | `Func/Subsystem/SuSubsystemTerrain.cs:159-193, 566-581, 969-975` |

## 7. 重连时的角色记录 diff（每角色独立 hash）

角色记录（名字 / 皮肤 / 衣服 / 背包 / 等级 …）不在 `Project.xml` 里，而在世界目录的
`SuMultiplayerPlayers.xml`；世界 pak 导出时整个世界目录被复制，所以加入端本地也有一份。
重连不重发 pak，因此用**每角色一个 hash** 做 diff：

- 记录文件里每条 `<Player RecordHash="…">`（v9，FNV-1a 64，只覆盖**不常变**字段）；
- 客户端 Hello 里 `known_records` 上报"我本地已有这些记录的 hash"（读本地 pak 副本 + 本次进程缓存）；
- 主机只对 hash 不同的角色重发不常变大块数据（目前是自定义皮肤资源），相同的跳过并计数
  `BackfillSkippedUnchanged`；
- 客户端在名册 / 资料广播里也收到 `record_hash`，用来维护缓存。

字段口径、常量与跳过规则见 `doc/message-protocol.md` 第 12 节。

## 8. 明确的失败路径（没有静默兜底）

- 主机没有会话标识：`WorldTransferModule` **不发世界**并写错误（没有会话标签的世界会让每次加入都整图重下）。
- 客户端缺失 / 会话不一致 / 序号越界：Hello 或追帧请求被**明确拒绝**，写清原因；客户端作废旧会话并重下。
- 主机历史已裁剪：`Rejected` + Warning；绝不继续用过期的客户端世界。
- 追帧请求重发上限 `CatchUpRequestMaxRetries = 3`：用尽写一行"主机没回应"，保留上次应用序号，不假装完成。
- 追帧 Begin / Barrier / 请求 / Applied 发送失败：显式计数 + 限流 Warning；任务保留、下一 tick 重试。
- 旧构建（HelloAck 没有会话字段）：客户端按"主机不会发整图"处理，断开重连成首次加入。
- 旧构建（CellChange 没有序号）：格子照常落地，但**不推进**水位；写一行说明，重连时退回世界清单基线。
