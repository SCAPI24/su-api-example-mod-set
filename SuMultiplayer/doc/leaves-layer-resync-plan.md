# 树叶专用回传与顶层分级修复

> 状态：方案（待实施）
> 目标：季节驱动的树叶 / 落叶变化不再触发整块回传（10~19 KB），改为只发该 chunk 的树叶段（约 0.2~3.4 KB）
> 前置文档：`doc/chunk-digest-tiered-verify-plan.md`

---

## 1. 问题背景

### 1.1 现状

chunk 加载时若整块摘要与主机不一致，走 `ChunkResync` 整块回传：

| 项 | 实测值 |
|---|---|
| 整块回传字节（RLE 压缩后） | 10~19 KB |
| 客户端单次落地耗时 | 250~295 ms |
| 采样窗口内触发次数 | 135 次 |

### 1.2 树叶季相差异是主要来源之一

实测 5 个「客户端未加载、主机已改动」的块，其中 3 个改动是树叶 `data`：

| chunk | 改动格（世界坐标） | 方块 | data |
|---|---|---|---|
| (-8,0) | (-120, 78, 13) | PoplarLeavesBlock（263） | 15 |
| (-6,1) | (-96, 74, 16) | OakLeavesBlock（12） | 14 |
| (-3,0) | (-48, 74, 4)、(-45, 75, 2) | OakLeavesBlock（12） | 14 |

另 2 个是水面结冰（IceBlock，contents=62），属 `Season` 类（保持顶层修复，本方案不调整）。

### 1.3 客户端不产生独立的季相变化

客户端被闸门禁止计算季相：

```csharp
public override void OnPoll(int value, int x, int y, int z, int pollPass)
{
    if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.DeciduousLeaves))
    {
        QueueLeafParticlesOnly(value, x, y, z);   // 客户端：只放粒子，不改格
        return;
    }
    base.OnPoll(value, x, y, z, pollPass);        // 主机：走这里 → 改格
}
```

**注意**：客户端手上的树叶值**不一定是春季值**。若它曾持有该块并收到过 `CellChange`，树叶就是当时的季节值。只有从未持有过的块才是生成器值（`PlantsManager.cs:20-28` 的 `Season.Spring`）。

**这正是标记必须按客户端区分的原因**（见 3.6）。

---

## 2. 数组布局与 RLE 格式（关键事实）

### 2.1 数组布局

`TerrainChunk.CalculateCellIndex`（`Survivalcraft/Game/TerrainChunk.cs:118`）：

```csharp
public static int CalculateCellIndex(int x, int y, int z)
    => y + x * 256 + z * 256 * 16;    // = y + x*256 + z*4096
```

**`y` 是最低位（系数 1），即最内层维度。** 每列 256 格连续，列之间跳 256（x+1）或 4096（z+1）。

### 2.2 不能按索引区间切片

树叶在 `y=70..75`、覆盖全部 256 列时：索引跨度 `70..65355` = **99.6%**。

**所以 `min~max` 索引区间方案无效**（RLE 的 run 数由材质变化决定，不由 y 范围决定）。

### 2.3 按列取「首个树叶到末个树叶」的范围

**按列（x, z）从 y=0 扫到 y=255**：

```
第一个「树叶类」格的 y  = 起点
最后一个「树叶类」格的 y = 终点
→ 该列记录 [起点, 终点] 整段（中间的空气也一起发）
```

**例**：某列 y=70..75 是树叶、y=80..81 也是树叶 → 记录 **70..81**（含 76~79 空隙）。

「树叶类」= 落叶树树叶（12 / 13 / 256 / 263）**∪ 落叶方块（261）**。

### 2.4 一个范围 = 一个或多个 run

范围内部按「相邻同值合并」拆成 run，而 `data` 的季相位随 y 连续变化，所以一列可能产生多个 run。

**实测（主机真实地形采样，2 个稀疏 chunk）**：

| chunk | 树叶列数 | run 数 | 字节 |
|---|---|---|---|
| (-8,0) | 19 | 83 | **664 B** |
| (-8,1) | 20 | 28 | **224 B** |

**平均 2.8 run/列 ≈ 22.8 字节/列。**

### 2.5 客户端解码不用改

`Decode`（`TerrainSync.cs:539`）每个 run 自带 `x/z/startY/len`，逐 run 展开，不依赖「整块范围」概念 → **只发树叶段时解码端零改动**。

---

## 3. 标记设计

### 3.1 标记定义

```csharp
[Flags]
public enum ChunkMutationKind : byte
{
    None   = 0,
    Season = 1 << 0,   // 结冰 / 积雪（保持现有顶层修复，本方案不调整）
    Leaves = 1 << 1,   // 季节驱动的树叶 data / 落叶方块变化
}
```

`Leaves` 的语义是「**这块有季相改动，该客户端需要补**」，不是「这块是树叶方块」。

### 3.2 判定约束：必须**真实写入 chunk** 才产生标记

**不能**在「排进队列」时标记 —— 队列项可能最终不应用（`RequiredValue` 校验失败）。必须在**格值真的变了**之后才标记。

`ChangeCell` 在值未变时提前 return（`SubsystemTerrain.cs:233`），不会产生真实写入：

```csharp
int cellValueFast = Terrain.GetCellValueFast(x, y, z);
value = Terrain.ReplaceLight(value, 0);
cellValueFast = Terrain.ReplaceLight(cellValueFast, 0);
if (value == cellValueFast) return;          // ← 没变就不写
Terrain.SetCellValueFast(x, y, z, value);
...
m_modifiedCells[new Point3(x, y, z)] = true;  // ← 真实写入的唯一登记点（:252）
```

### 3.3 两条写入路径与对应埋点

季相写入有两条路径，`applyImmediately` 不同：

| 路径 | 触发 | `applyImmediately` | 写入时机 |
|---|---|---|---|
| `OnBlockGenerated` → `UpdateTimeOfYear` | chunk **加载时一次** | `true` | **立即** `ChangeCell` |
| `OnPoll` → `UpdateTimeOfYear` | 加载后**周期性**（约 60 秒一轮） | `false` | 进 `m_toChange`，**≤20 秒后**批量 `ChangeCell` |

#### OnBlockGenerated 是什么时候

| 项 | 内容 |
|---|---|
| 调用点 | `TerrainUpdater.cs:1422`，chunk 初始化流程里（紧跟 `OnChunkInitialized`） |
| 频率 | **该 chunk 内容第一次进内存时，只一次** |
| 遍历 | 16×16 列 × y=0..254 全部，对每个非空气格调用 |
| `isLoaded` | `true` = 从存档读入；`false` = 刚生成 |

它在**加载那一次**把树叶从存档/生成器值修正到当前季节。

#### OnPoll 是什么时候

| 项 | 内容 |
|---|---|
| 调用点 | `SubsystemBlocksScanner.Update`（`:77`） |
| 频率 | **周期性，永久重复**，每个 chunk 约 **60 秒**轮一遍 |
| 速率 | `m_pollShaftsCount += AllocatedChunks * 256 * dt / 60f` |
| 遍历 | 16×16 列，每列只到 `topHeightFast`（地表以上不扫） |
| `Content` 未定稿的块 | 直接跳过（`SubsystemBlocksScanner.cs:50`） |

**季节持续推进靠的是 `OnPoll`**（`TimeOfYear` 一直在走），不是 `OnBlockGenerated`。

#### 埋点一：OnBlockGenerated（立即写 → 前后比对）

```csharp
public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
{
    if (!ReplicaGates.Allow(SubsystemTerrain, ReplicaMutationSource.DeciduousLeaves))
    {
        QueueLeafParticlesOnly(value, x, y, z);
        return;
    }
    var cell = new Point3(x, y, z);
    int before = ReadCellValue(SubsystemTerrain, cell);
    base.OnBlockGenerated(value, x, y, z, isLoaded);   // 立即 ChangeCell
    int after = ReadCellValue(SubsystemTerrain, cell);
    if (IsLeavesSeasonWrite(before, after)) NoteLeavesMutation(x, y, z);   // ← 真实写入才标记
}
```

#### 埋点二：OnPoll（延迟写 → 在队列外壳里确认）

`OnPoll` 的写入发生在 `SubsystemCellChangeQueue.Update` → `ApplyCellChanges`。mod **已有**外壳 `SuSubsystemCellChangeQueue`（`ClientReplicaSubsystems.cs:3045`，已在 `SuMultiplayerMod.cs:1018` 完成替换），它本来就在读 `m_toChange`。

在 `Update` 里**围绕 `base` 做前后比对**：

```csharp
void IUpdateable.Update(float dt)
{
    if (!ReplicaGates.Allow(m_terrain, ReplicaMutationSource.CellChangeQueue))
    {
        ReplicaGates.ClearTable(m_terrain, this, "m_toChange", typeof(SubsystemCellChangeQueue));
        return;
    }

    // ① base 之前：把待应用格与它们的旧值记下来
    m_beforeValue.Clear();
    if (ReplicaGates.GetField(this, "m_toChange", typeof(SubsystemCellChangeQueue))
        is IDictionary table && table.Count > 0)
    {
        foreach (DictionaryEntry entry in table)
        {
            var cell = (Point3)entry.Key;
            m_beforeValue[cell] = ReadCellValue(m_terrain, cell);
        }
    }

    if (m_baseUpdate == null) return;
    try { m_baseUpdate.Invoke(this, new object[] { dt }); }
    catch (Exception e) { /* 现有错误处理：明确失败不静默 */ return; }

    // ② base 之后：只有值真的变了才算「真实写入 chunk」
    foreach (var pair in m_beforeValue)
    {
        int after = ReadCellValue(m_terrain, pair.Key);
        if (after == pair.Value) continue;                       // RequiredValue 校验失败 → 没写
        if (!IsLeavesSeasonWrite(pair.Value, after)) continue;   // 不是季相写入
        m_terrain.NoteLeavesMutation(pair.Key);                  // ← 标记
    }
    m_beforeValue.Clear();

    ReplicaGates.RescueEvolutionCells(m_terrain);
}
```

**取值方式**：`ReplicaGates.GetField`（`ClientReplicaSubsystems.cs:186`）已有现成封装，`SuSubsystemCellChangeQueue` 里已经在用同一个调用读 `m_toChange` → **不需要新的反射代码**。

**为什么这样能同时覆盖 `OnPoll` 和 `CreateFallenLeaves`**：两者都走同一个 `QueueCellChange`，都在同一个 `ApplyCellChanges` 里落地，外壳的前后比对把两者一起看见。

### 3.4 判据

```csharp
/// 季相写入 = 「树叶 data 只变低 6 位」 或 「新生成了落叶方块 261」
static bool IsLeavesSeasonWrite(int before, int after)
{
    if (before == after) return false;                       // 没有真实变化

    int oldContents = before & 0x3FF, newContents = after & 0x3FF;
    if (newContents == 0) return false;                      // 变成空气 = 不是季相

    // ① 落叶方块：CreateFallenLeaves 把空气/可替换格变成 261
    if (newContents == FallenLeavesBlockContents) return true;   // 261

    // ② 树叶季相：contents 不变，只有 data 低 6 位变
    if (oldContents != newContents) return false;
    if (!IsDeciduousLeavesContents(newContents)) return false;
    int oldData = before >>> 14, newData = after >>> 14;
    return ((oldData ^ newData) & ~0x3F) == 0;
}
```

**落叶树树叶 data 位布局**（`DeciduousLeavesBlock.cs:268-296`）：

| 位 | 掩码 | 字段 | 修改函数清除的位 |
|---|---|---|---|
| 0~2 | `0x07` | `TimeOfSeason` | `data & -8` |
| 3~4 | `0x18` | `Season` | `data & -25` |
| 5 | `0x20` | `IsShaken` | `data & -33` |

### 3.5 玩家操作天然不经过

`SubsystemDeciduousLeavesBlockBehavior` **只 override 了 `OnBlockGenerated`（`:60`）和 `OnPoll`（`:68`）**，没有 `OnBlockAdded` / `OnBlockRemoved`（该文件全文确认）。

| 操作 | 引擎回调 | 本方案埋点 |
|---|---|---|
| 主机季节推进 | `OnPoll` → `UpdateTimeOfYear` → 队列 | ✅ 命中 → `Leaves` |
| 主机加载块季节初始化 | `OnBlockGenerated` → `UpdateTimeOfYear` | ✅ 命中 → `Leaves` |
| **玩家放置 / 破坏树叶** | `ChangeCell` → `OnBlockAdded/Removed` | ❌ 不经过本行为，不进季节队列 |
| 玩家放其他方块 | `ChangeCell` → `OnBlockAdded` | ❌ 不经过 |

#### 树叶类内容集合（启动时构建 `bool[1024]` 缓存）

| contents | 类 | 计入 |
|---|---|---|
| 12 | `OakLeavesBlock` | ✅ |
| 13 | `BirchLeavesBlock` | ✅ |
| 256 | `MimosaLeavesBlock` | ✅ |
| 263 | `PoplarLeavesBlock` | ✅ |
| **261** | **`FallenLeavesBlock`** | ✅（`CreateFallenLeaves` 产生） |

常绿树（14 / 225）不是 `DeciduousLeavesBlock`，不参与季节变化。

### 3.6 标记按客户端区分（必须）

**理由**：主机改动时 A、B 都不持有该块 → 两方各记一条；A 先加载、消耗掉自己的记录，**B 的记录必须还在**。若用全局标记，B 就会永远丢失这次变更通知。

结构对照现有 `m_pendingClients`（`TerrainSync.cs:1807`）：

```csharp
private readonly Dictionary<ChunkKey, Dictionary<string, long>> m_pendingClients;   // 现有（欠更新序号）
private readonly Dictionary<ChunkKey, Dictionary<string, ChunkMutationKind>> m_pendingKinds;  // 新增（本次）
```

**不能直接复用 `m_pendingClients`**：它在客户端「加载块」时就清（`ClearPendingOnChunkLoaded`），而我们需要在**裁决时**（加载之后）还知道标记。

#### 生命周期

| 时机 | 动作 |
|---|---|
| `NoteLeavesMutation`（真实写入后） | 对每个「**在线**且 `MemoryChunks` 不含这块」的身份 `\|= Leaves` |
| 客户端不在线 | **不建记录**（它加入房间时拿到的是同步存档） |
| 该客户端收到补丁并落地 | **只清该身份**的条目 |
| 身份离开联机 | 全表去掉该身份（对照 `RemovePendingClientEverywhere`） |
| 会话结束 | 整体清空（对照 `ClearPendingMarks`） |

判定时只查**当前这个客户端**：

```csharp
ChunkMutationKind kind = m_pendingKinds.TryGetValue(key, out var byClient)
    && byClient.TryGetValue(state.StableKey, out var k) ? k : ChunkMutationKind.None;
```

---

## 4. 回传判定

`ApplyVerdict` 的 Mismatch 分支，插在最前：

```
该客户端有 Leaves 标记？
  ├─ 有 → 必须给修复（客户端已请求）
  │      ├─ 客户端没带顶层 hash → 回 NotReady（树叶也不发），让它重算再报
  │      ├─ 顶层不同             → 树叶补丁 + 顶层修复（树叶发完再发顶层）
  │      └─ 顶层相同 / 不可用    → 只发树叶补丁
  └─ 无任何标记 → 现有逻辑（TrySendTopLayerFix / 整块回传）
```

### 4.1 决策表

| Leaves 标记 | 顶层 hash | 树叶补丁 | 顶层修复 |
|---|---|---|---|
| 有 | 不同 | ✅ | ✅（树叶之后） |
| 有 | 相同 | ✅ | ❌ |
| 有 | **缺失** | ❌ | ❌ → NotReady |
| 无（含仅 `Season`） | 任意 | ❌ | 现有逻辑（顶层修复 / 整块回传） |

**无任何标记 = 这块没被改过 = 不需要回传，走现有逻辑。**

### 4.2 缺失 hash 的闭环

`SendQueuedReports` 发送前会补算顶层 hash（已在提交 `32e8fbf` 实现）→ 客户端重报时必然带 hash。

### 4.3 `CompareTopLayer` 纯函数

现有 `TrySendTopLayerFix` 把「比对」和「发送」混在一起，抽成无副作用的比对：

```csharp
private enum TopLayerOutcome { Same, Different, ClientMustRecalc, Unavailable }

private TopLayerOutcome CompareTopLayer(ChunkKey key, uint clientTopHeightHash,
    uint clientTopBlockHash, out uint hostH, out uint hostB)
{
    hostH = hostB = 0;
    if (!m_world.HasContents(key)) return TopLayerOutcome.Unavailable;
    if (!(m_world is ITopLayerTerrainWorld topLayer)) return TopLayerOutcome.Unavailable;
    if (!topLayer.TryGetChunkArrays(key, out int[] cells, out int[] shafts))
        return TopLayerOutcome.Unavailable;

    var digest = new ChunkDigest();
    digest.ComputeTopLayer(cells, shafts);
    if (!digest.HasTopLayer) return TopLayerOutcome.Unavailable;
    hostH = digest.TopHeightHash;
    hostB = digest.TopBlockHash;

    if (clientTopHeightHash == 0u && clientTopBlockHash == 0u)
        return TopLayerOutcome.ClientMustRecalc;
    return (clientTopHeightHash == hostH && clientTopBlockHash == hostB)
        ? TopLayerOutcome.Same : TopLayerOutcome.Different;
}
```

`TrySendTopLayerFix` 改为调它 + 发消息，现有调用点不变。

### 4.4 收敛：**不重算整块 hash**

与结冰的顶层修复一致：**补丁落地即视为该部分已修复**。

| 时机 | 动作 |
|---|---|
| 补丁全部分片发完 | 清该客户端该块的 `Leaves` 标记 |
| 该块裁决状态 | 按「已修复」处置（与 `TopLayerFix` 后同样的收尾） |
| **不做** | 不发 `RequestChunkReverification`、不重算整块 hash |

**安全网仍在**：若这块还有真实深层差异，它下次以**无标记**状态加载时会走现有整块 hash 比对 → 整块回传。补丁只负责它自己那部分。

---

## 5. 协议与编码

### 5.1 协议改动（1 个字段）

```protobuf
message ChunkResync {
  ChunkKey key = 1;
  int32 index = 2;
  int32 count = 3;
  int32 entries = 4;
  bytes records = 5;
  optional int64 watermark = 6;
  // 新增：这份快照只含树叶段（客户端走轻量落地路径）
  bool leaves_only = 7;
}
```

复用现有消息、现有队列、现有分帧机制，**不新增消息类型**。

### 5.2 编码：每列取首个~末个树叶类格的范围

`ChunkPayloadCodec.BuildColumnRuns`（`TerrainSync.cs:368`）加 `leavesOnly` 分支：

```csharp
if (leavesOnly)
{
    for (int x = 0; x < 16; x++)
    for (int z = 0; z < 16; z++)
    {
        // ① 先扫全列，找首个/末个「树叶类」格
        int first = -1, last = -1;
        for (int y = 0; y < 256; y++)
        {
            source(x, y, z, out int c, out int d);
            if (!IsLeavesPatchContents(c)) continue;   // 12/13/256/263/261
            if (first < 0) first = y;
            last = y;
        }
        if (first < 0) continue;                       // 该列没有树叶类格

        // ② 只对 [first, last] 这段做相邻同值合并（范围内的空气也发）
        int runStart = first, prev = ValueAt(x, first, z);
        for (int y = first + 1; y <= last + 1; y++)
        {
            int cur = y <= last ? ValueAt(x, y, z) : int.MinValue;
            if (cur == prev) continue;
            EmitRun(x, z, runStart, y - runStart - 1, prev);
            runStart = y;
            prev = cur;
        }
    }
}
```

**要点**：
- 范围端点由「树叶类格」决定，**不是**由「值变化」决定
- 范围内部仍按相邻同值合并 → 一列可能多个 run
- `261` 计入「树叶类」，所以落叶方块会把范围端点往外扩（秋季产生的落叶也能被覆盖）

### 5.3 传输量（实测 + 推算）

| 场景 | 树叶列数 | run | 字节 | vs 整块 10~19 KB |
|---|---|---|---|---|
| 稀疏（实测 1~2 棵树） | 19~20 | 28~83 | **224~664 B** | 省 15~85× |
| 中等（约 60 列） | 60 | ~170 | **~1.4 KB** | 省 7~14× |
| 茂密森林（约 150 列） | 150 | ~420 | **~3.4 KB** | 省 3~6× |

run 数按实测 **2.8 run/列 × 8 字节** 推算。

### 5.4 零 run 处置

标记还在但该 chunk 已无树叶类格（树叶被砍光）→ 编码结果 `entries == 0` → **不发树叶补丁、改走整块回传**，计 `LeavesZeroRunFallback` 并写清原因。

否则会命中 `IsEmptySnapshotPart`（`TerrainSync.cs:528`）造成「空补丁 → NotReady → 重报」循环。

### 5.5 发送顺序：先树叶后顶层

`PendingResync` 发送循环（`TerrainSync.cs:2632` 附近，`job.Index >= job.Parts.Count` 处）：

```csharp
state.PendingResync.Dequeue();
if (job.TopLayerFixAfter != null)
    m_server.TrySend(state.Id, MessageType.TopLayerFix, job.TopLayerFixAfter);
ClearPendingKind(state, job.Key, ChunkMutationKind.Leaves);   // ← 清该客户端的标记
```

TCP 保序 → 客户端必然先收树叶补丁再收顶层修复。

---

## 6. 客户端落地（两段式）

```csharp
if (part.LeavesOnly)
    m_world.SetCellsRaw(part.Key, part.Edits);        // 轻量：只写数组
else
    diagnosticWorld.ApplyResyncCells(part.Key, part.Edits, ...);   // 现有重路径
```

`TerrainBridge.SetCellsRaw`：只做 `GetCellValueFast` / `SetCellValueFast` + 一次邻域降级。

### 6.1 为什么不会产生本地编辑回传（已核实）

| 环节 | 证据 |
|---|---|
| `m_modifiedCells` 只有**一个**写入点 | `SubsystemTerrain.cs:252`，只在 `ChangeCell` 里写 |
| `SetCellValueFast` 不经 `ChangeCell` | 直接数组赋值 |
| 客户端上报的数据源就是 `m_modifiedCells.Keys` | `SuSubsystemTerrain.cs:784` |
| 现有整块回传落地路径同样用 `SetCellValueFast` | `ApplyCellsCore:441`，从未产生虚假回传 |

```
SetCellsRaw → SetCellValueFast → 不进 m_modifiedCells → CaptureClientEdits 收不到 → 不回传 ✅
```

**回主机的只有玩家真实操作**：`ChangeCell` → `m_modifiedCells` → `CaptureClientEdits` → `CellEditRequest` → 主机权威执行。这条链完全不变。

### 6.2 为什么不用 `ApplyResyncCells`

它每格派发 3 类事件（电路拓扑 / 方块行为 / 副本事件）—— 客户端 250 ms 单帧卡顿的来源。树叶只改 `data`，不影响电路。

---

## 7. 完整数据流

```
主机季节推进
    ├─ chunk 加载那一次：OnBlockGenerated → UpdateTimeOfYear(applyImmediately:true) → 立即 ChangeCell
    └─ 之后每约 60 秒：  OnPoll → UpdateTimeOfYear(applyImmediately:false) → m_toChange → ≤20s 后 ApplyCellChanges
                ↓
    mod 在真实写入后比对（before/after）：
        ├─ OnBlockGenerated 外壳：前后读值
        └─ SuSubsystemCellChangeQueue.Update 外壳：base 前后读值
                ↓
    IsLeavesSeasonWrite：树叶 data 只变低 6 位 ／ 新生成了 261
                ↓
    ✅ NoteLeavesMutation → 对每个「在线且不含该块」的身份 |= Leaves
                ↓
客户端加载该块 → 整块 hash 上报 → 主机判 Mismatch
                ↓
    该客户端有 Leaves 标记？
        ├─ 有 → CompareTopLayer
        │        ├─ 缺失 → 回 NotReady（树叶也不发）→ 客户端重算再报
        │        ├─ 不同 → PendingResync{LeavesOnly, TopLayerFixAfter}
        │        │           ↓
        │        │      编码：每列取首个~末个树叶类格的范围，内部同值合并
        │        │           ↓
        │        │      ChunkResync{leaves_only=true}（约 0.2~3.4 KB）
        │        │           ↓
        │        │      客户端 SetCellsRaw（只写数组，< 5 ms）
        │        │           ↓
        │        │      job 发完 → TopLayerFix（256 格）→ 客户端 ApplyCells
        │        └─ 相同 → 只发树叶补丁
        └─ 无任何标记 → 现有逻辑（顶层修复 / 整块回传）
                ↓
    补丁发完 → 清该客户端该块的 Leaves 标记（不重算整块 hash）
```

---

## 8. 改动清单

| # | 位置 | 改动 | 行数 |
|---|---|---|---|
| 1 | `Protocol/terrain.proto` | `ChunkResync` 加 `bool leaves_only = 7` | +2 |
| 2 | `Terrain/ChunkDigest.cs` | `ChunkMutationKind` 枚举 | +8 |
| 3 | `Engine/ClientReplicaSubsystems.cs` | `IsLeavesSeasonWrite` + 树叶类缓存（含 261） | +26 |
| 4 | `Engine/ClientReplicaSubsystems.cs` | `SuSubsystemCellChangeQueue.Update` 前后比对 | +24 |
| 5 | `Engine/ClientReplicaSubsystems.cs` | `SuSubsystemDeciduousLeavesBlockBehavior.OnBlockGenerated` 前后比对 | +10 |
| 6 | `Terrain/TerrainSync.cs` | `m_pendingKinds`（按客户端）+ `NoteLeavesMutation` + `ClearPendingKind` | +38 |
| 7 | `Terrain/TerrainSync.cs` | `TopLayerOutcome` + `CompareTopLayer`（从 `TrySendTopLayerFix` 抽出） | +28 |
| 8 | `Terrain/TerrainSync.cs` | `PendingResyncJob` 加 `LeavesOnly` / `TopLayerFixAfter` | +2 |
| 9 | `Terrain/TerrainSync.cs` | `ApplyVerdict` 树叶分支（决策表） | +22 |
| 10 | `Terrain/TerrainSync.cs` | `Encode` / `BuildColumnRuns` 加 `leavesOnly`（首末点范围 + 同值合并） | +34 |
| 11 | `Terrain/TerrainSync.cs` | 发送循环：job 完成后发 `TopLayerFixAfter` + 清标记 | +5 |
| 12 | `Engine/TerrainBridge.cs` | `SetCellsRaw` 轻量落地 | +18 |
| 13 | `Terrain/TerrainSync.cs` | `ApplyQueuedResyncParts` 按 `leaves_only` 分流 | +12 |
| 14 | 身份离开 / 会话结束 | `m_pendingKinds` 清理（对照现有两处） | +8 |
| 15 | `Protocol/telemetry.proto` + `Modules/TerrainSyncModule.cs` + `doc/live-map.html` | 诊断计数 | +26 |
| | **合计** | | **~283 行** |

**无新消息类型、无新协议消息、无新队列。**

---

## 9. 风险与对策

### 9.1 `m_toChange` 读取失败

`ReplicaGates.GetField` 内部已 try/catch 返回 null。null 时计 `LeavesQueueUnreadable` + 限流告警，**不静默当作「没有季相改动」**；退化为整块回传（现有行为）。

### 9.2 前后比对与同帧其他写入

`SuSubsystemCellChangeQueue.Update` 内 base 只应用一次。同帧其他系统若改了同一格，`after` 会包含它们的改动；但判据要求「contents 不变 + 只有 data 低 6 位变」，误判概率极低。

### 9.3 树叶 run 数为 0

见 5.4：改走整块回传（计 `LeavesZeroRunFallback`）。

### 9.4 打标记开销

- `OnBlockGenerated`：每格 2 次 `GetCellValueFast`（加载时一次，可忽略）
- 队列外壳：只在 `m_toChange` 非空时工作（约每 20 秒一次），每格 2 次读值

### 9.5 `BlocksManager` 反射开销

启动时构建 `bool[1024]` 缓存，热路径纯数组查。

### 9.6 补丁与 `CellChange` 竞态

复用 `PendingResync` 现有的 `watermark` + `ChunkGeneration` 代检查。

### 9.7 按客户端标记表的增长

条目数 = 块数 × 欠更新客户端数。会话结束整体清空，不落盘。

---

## 10. 标记生命周期

| 时机 | 动作 |
|---|---|
| 真实写入后（`NoteLeavesMutation`） | 对每个「在线且不含该块」的身份 `\|= Leaves` |
| 客户端不在线 | 不建记录（加入时即同步存档） |
| 该客户端补丁落地 | 只清该身份 |
| 身份离开联机 | 全表去掉该身份 |
| 会话结束 | 整体清空 |

---

## 11. 诊断

### 11.1 主机

| 计数器 | 含义 | 预期 |
|---|---|---|
| `LeavesMarked` | 打过 Leaves 标记的次数 | > 0 |
| `LeavesQueueUnreadable` | `m_toChange` 读不到 | 0 |
| `LeavesOnlyRuns` / `LeavesOnlyBytes` | 树叶补丁累计 run / 字节 | ÷ 块数 < 3.5 KB |
| `LeavesZeroRunFallback` | run 为 0 而回退整块 | 稳态 ≈ 0 |
| `LeavesTopLayerSkipped` | 顶层相同而未发顶层修复 | — |
| `LeavesRecalcDeferred` | 缺失顶层 hash 而回 NotReady | 启动后很快归 0 |
| `LeavesPendingClients` | 当前标记表条目总数 | 有界 |

### 11.2 客户端

| 计数器 | 含义 |
|---|---|
| `LeavesFixApplied` | 树叶补丁落地次数 |
| `LeavesFixCells` | 累计落地格数 |

接入 `telemetry.proto` 的 `TerrainSyncSample` + `doc/live-map.html` 的 Terrain 面板。

---

## 12. 实施顺序

| 步 | 内容 | 验证 | 风险 |
|---|---|---|---|
| **1** | `ChunkMutationKind` + 两处真实写入比对 + 按客户端标记表 + 诊断 | `LeavesMarked > 0`，且与日志里的树叶/落叶格吻合 | **零行为变化** |
| **2** | `CompareTopLayer` 重构（纯重命名） | 现有顶层补丁逻辑照常工作 | 低 |
| **3** | 协议 + 编码（首末点范围 + 261）+ 判定表 + 发送顺序 | `LeavesOnlyBytes` < 3.5 KB/块 | 中 |
| **4** | 客户端 `SetCellsRaw` + 清标记 + 不重算 hash | 树叶颜色正确、整块回传统计下降、无虚假回传 | 中 |

---

## 13. 验收标准

| 指标 | 目标 |
|---|---|
| 树叶补丁平均字节数 | < 3.5 KB（稀疏场景 224~664 B） |
| 客户端落地耗时 | < 5 ms（对比整块 250~295 ms） |
| 整块回传触发次数 | 显著下降（预计降至原来 1/3 以下） |
| 树叶颜色正确性 | 与主机一致（视觉确认） |
| `LeavesZeroRunFallback` | 稳态 ≈ 0 |
| `LeavesRecalcDeferred` | 启动后很快归 0 |
| 客户端虚假 `CellEditRequest` | **0**（`SetCellsRaw` 不经 `ChangeCell`） |

---

## 14. 被否定的方案

### 14.1 `min~max` 索引区间切片

**否**。`y` 是最低位，同一 y 范围跨全部列时索引跨度 99.6%。

### 14.2 按 Y 层连续传输

**否**。Y 层不连续（列才是外层）。

### 14.3 「排队即标记」

**否**。`ApplyCellChanges` 会因 `RequiredValue` 校验失败而丢弃队列项 → 排了队不等于真写了。必须在**真实写入后**比对（见 3.2、3.3）。

### 14.4 客户端镜像延迟队列（早期设计）

**否 —— 过度设计。** 在 `SuSubsystemCellChangeQueue.Update` 里围绕 `base` 做前后比对即可覆盖同一批写入，不需要自建 20 秒延迟队列。

### 14.5 全局标记（不分客户端）

**否**。A 消耗掉记录后 B 会丢失该块的变更通知（见 3.6）。

### 14.6 按「方块类型是落叶树叶」判定

**否**。会把玩家放置的树叶也判成季相改动。本方案判据是「真实写入 + contents 不变且只有 data 低 6 位变」+ 观察点仅在季节路径（见 3.4、3.5）。

### 14.7 补丁后重算整块 hash

**否**。与结冰的顶层修复一致：补丁落地即视为该部分已修复。深层差异由「无标记时加载」的现有整块比对兜住（见 4.4）。

### 14.8 复现 `TimeOfYear` 哈希反推季节值

**否**。依赖浮点精度与 `SubsystemGameInfo` 可访问性，春季边界有歧义。前后比对零依赖。

### 14.9 客户端本地计算树叶季相

**暂不采用**。技术上可行（`TimeOfYear` 已同步、`SetTimeOfYear` 是纯函数），但违反「客户端 = 纯副本」约定，且客户端写格需额外拦截 `m_modifiedCells`。

---

## 15. 引用索引

### 引擎侧

| 文件 | 位置 | 内容 |
|---|---|---|
| `Survivalcraft/Game/TerrainChunk.cs` | `:118` | `CalculateCellIndex`（数组布局） |
| | `:139,144` | `GetCellValueFast` |
| | `:149,154` | `SetCellValueFast` |
| `Survivalcraft/Game/SubsystemDeciduousLeavesBlockBehavior.cs` | `:36` | `CreateFallenLeaves`（queue 261） |
| | `:60` | `OnBlockGenerated`（`applyImmediately: true`） |
| | `:68` | `OnPoll`（`applyImmediately: false`） |
| | `:115` | `UpdateTimeOfYear`（**private**） |
| `Survivalcraft/Game/DeciduousLeavesBlock.cs` | `:225` | `SetTimeOfYear`（无子类 override） |
| | `:268,273` | `GetSeason` / `SetSeason`（bit3~4，掩码 `-25`） |
| | `:278,283` | `GetTimeOfSeason` / `SetTimeOfSeason`（bit0~2，掩码 `-8`） |
| | `:291,296` | `GetIsShaken` / `SetIsShaken`（bit5，掩码 `-33`） |
| `Survivalcraft/Game/SubsystemCellChangeQueue.cs` | `:12` | `CellChange` 结构（`RequiredValue` / `Value`） |
| | `:21` | `m_toChange`（**private**） |
| | `:31` | `QueueCellChange`（`applyImmediately` 分支） |
| | `:53` | `PeriodicGameTimeEvent(20.0, 0.0)` |
| | `:59` | `ApplyCellChanges`（**private**，含 `RequiredValue` 校验） |
| `Survivalcraft/Game/SubsystemTerrain.cs` | `:233` | `ChangeCell`（值未变提前 return） |
| | `:252` | `m_modifiedCells[...] = true`（真实写入唯一登记点） |
| `Survivalcraft/Game/SubsystemBlocksScanner.cs` | `:11` | `ScanPeriod = 60f` |
| | `:50` | `State <= InvalidContents4` 跳过 |
| | `:77` | `OnPoll` 传世界坐标 |
| `Survivalcraft/Game/TerrainUpdater.cs` | `:1422` | `OnBlockGenerated` 唯一调用点 |
| `Survivalcraft/Game/PlantsManager.cs` | `:20-28` | 树叶生成固定值（`Season.Spring`） |

### Mod 侧

| 文件 | 位置 | 内容 |
|---|---|---|
| `Mod/SuMultiplayer/Terrain/TerrainSync.cs` | `:310` | `RecordBytes = 8` / `RecordsPerFrame = 5000` |
| | `:368` | `BuildColumnRuns`（RLE 编码） |
| | `:528` | `IsEmptySnapshotPart` |
| | `:539` | `Decode`（run 自描述） |
| | `:1741` | `PendingResyncJob` |
| | `:1807` | `m_pendingClients`（**按客户端**台账范例） |
| | `:2632` | `PendingResync` 发送循环 |
| | `:4445` | `ApplyVerdict` Mismatch 分支 |
| | `:4733` | `BroadcastCellChange` |
| `Mod/SuMultiplayer/Engine/ClientReplicaSubsystems.cs` | `:186` | `ReplicaGates.GetField`（读 private 字段封装） |
| | `:1381` | `SuSubsystemDeciduousLeavesBlockBehavior` |
| | `:3045` | `SuSubsystemCellChangeQueue`（已在读 `m_toChange`） |
| `Mod/SuMultiplayer/Engine/SuSubsystemTerrain.cs` | `:784` | 客户端改动上报数据源（`m_modifiedCells.Keys`） |
| | `:844` | `EnsureModifiedCellsBound` |
| `Mod/SuMultiplayer/Engine/TerrainBridge.cs` | `:441` | `ApplyCellsCore` 用 `SetCellValueFast` |
| `Mod/SuMultiplayer/Plug/SuMultiplayerMod.cs` | `:1018` | `SubsystemCellChangeQueue` 已替换 |
