# Chunk 摘要分级校验与季节修改标记

> 状态：计划（待实施）
> 目标版本：SuMultiplayer 0.1.0
> 解决现象：客户端（红米）**每跨入一个新 chunk 就卡顿一次**，帧率从 80Hz 掉到 5~40Hz，单帧尖峰 250~634ms；同期电路暂停（`circuitSim=False`、`waiting-full-scope-snapshot`）。

---

## 1. 问题背景

### 1.1 实测现象

采集 300 秒（每 0.25s 采样一次，同时记录玩家坐标与帧率）：

| 指标 | 实测值 |
|---|---|
| 检测到的 chunk 切换 | **84 次** |
| 切换后 1 秒内掉帧（`hz<60`） | **53 次 = 63.1%** |
| 其中掉到 `hz<10` | **23 次 = 27.4%** |
| 切换前 `hz` 中位 | 60.46 |
| 切换后 `hz` 最低值中位 | **44.64** |
| 切换后 `frameTimeMax` 中位 | **251.06 ms** |
| 切换后 `frameTimeMax` 峰值 | **634.34 ms** |

**关键特征**：`frameTimeAvg` 只有 17~26ms，但 `frameTimeMax` 达 300~634ms。`avg/max` 比值 0.05~0.18 → **这是单帧长尖峰，不是整体变慢**。

### 1.2 已排除的原因

| 假说 | 排除依据 |
|---|---|
| GC 停顿 | logcat `art:V` 全程 **0 次 GC**；`Choreographer: Skipped 30~41 frames` 是 UI 线程记录，与游戏 Update 线程不同 |
| CPU 瓶颈 | 138~157% / 800%，8 核占用低 |
| 特定坐标触发 | 遍历 12 个区块（`(-3,-8)`~`(-7,-5)`）**每个方向跨 chunk 都卡** |
| 挖掘方块触发 | 仅移动、未挖掘同样卡顿 |

### 1.3 因果链（实测确认）

```
玩家跨入新 chunk
    ↓
TerrainSync.SendInterest(center, radius)        ← TerrainSync.cs:6019
    ↓
主机重建兴趣窗口（5×5 = 25 chunks）              ← TerrainSync.cs:3762-3780
    ↓
窗口内区块触发摘要核对
    ↓
摘要失配 → ChunkResync 整块回传
    ↓
① 客户端 chunk 落地（每帧 1 片，单片 250~634ms）
② 电路 scope 失效 → circuitSim=False → 电路暂停
    ↓
回传结束 → 恢复
```

**同期观测**（LiveMap + 客户端日志）：

```
circuitSim=False
clientBlock=waiting-full-scope-snapshot
pendingSnapshots=8/8    oldestMs=174
topologyBumps: client(1)
scopeRequests=341       scopeRequestChunksSent=67,780   ← 25 分钟内 341 次窗口请求
```

---

## 2. 数据模型事实（引擎侧，已核实）

### 2.1 chunk 格数据布局

`TerrainChunk.cs:67` — 格数据是**连续托管数组** `int[65536]`：

```csharp
private int[] Cells;    // Rent from ArrayCache, new int[65536]
```

索引公式（`TerrainChunk.cs:118`）：

```csharp
public static int CalculateCellIndex(int x, int y, int z)
    => y + x * 256 + z * 256 * 16;      // = y + x*256 + z*4096
```

### 2.2 单个 int 的位布局（`Terrain.cs:72-80`）

| 位 | 字段 | 宽度 | 权威性 |
|---|---|---|---|
| 0~9 | `Contents`（方块 ID，0=空气） | 10 bit | ✅ **权威** |
| 10~13 | `Light`（光照 0~15） | 4 bit | ❌ **本地自动计算，禁止进 hash** |
| 14~31 | `Data`（方块数据） | 18 bit | ⚠️ **混合**（见 2.3） |

**Light 必须剥离的决定性依据**：引擎自己判定格变化时就剥（`SubsystemTerrain.cs:235-237`）：

```csharp
value = Terrain.ReplaceLight(value, 0);
cellValueFast = Terrain.ReplaceLight(cellValueFast, 0);
if (value == cellValueFast) return;         // light 不参与"是否改变"
```

**剥离掩码**：`0xFFFFC3FF`（保留 contents + data，清除 bit 10~13）。

### 2.3 Data 字段的权威性分类

| 来源 | 代码位置 | 权威性 |
|---|---|---|
| 玩家放置区块的朝向 | `CellEditRequest` 权威下发 | ✅ 权威 |
| 容器/熔炉内容 | 容器同步权威下发 | ✅ 权威 |
| 电路元件状态 | 电路同步权威下发 | ✅ 权威 |
| **流体顶端标记** `isTop` | `SubsystemFluidBlockBehavior.cs:53` | ❌ **本地模拟** |
| 家具槽位（部分） | 家具系统 | ⚠️ 混合 |

**结论（用户确认）：Data 字段不需要剥离。**

理由：原本上面是水，同步后仍然是水——流体的 `isTop` 等标记在两端应该一致（都经过同一次 `CellChange` 下发）。如果真的不一致，那本身就是需要修复的同步 bug，不应该用 canonicalizer 掩盖。

**因此：hash 保留整个 data 位（`0xFFFFC3FF` 掩码只剥 light）。**

### 2.4 顶部高度已缓存（关键优化点）

`TerrainChunk.cs:69, 85` — 独立的 `Shafts` 数组：

```csharp
private int[] Shafts;                    // int[256]，16×16 列
Shafts = m_shaftsCache.Rent(256, clearArray: true);
```

`TerrainChunk.cs:169-171, 199-201`：

```csharp
public int GetShaftValueFast(int x, int z) => Shafts[x + z * 16];
public int GetTopHeightFast(int x, int z) => Terrain.ExtractTopHeight(GetShaftValueFast(x, z));
```

`TopHeightMask = 255`（bit 0~7），`TopHeightShift = 0`。

**这意味着每列的顶部高度是 O(1) 读取，无需扫描 256 格。**

---

## 3. 环境修改地形的完整清单（关键）

全引擎 `ChangeCell` 共 67 处。**环境类**分类如下：

| 系统 | 处数 | 修改深度 | 是否只影响顶层 | 需要季节标记 |
|---|---|---|---|---|
| **`SubsystemWeather`** | **3** | 用 `GetTopHeightFast` 取顶层 | ✅ **仅顶层** | ✅ **是** |
| `SubsystemGrassBlockBehavior` | 5 | `OnNeighborValueChanged` 任意 y | ❌ | ❌ |
| `SubsystemFluidBlockBehavior` | 1 | 流体流动，任意 y | ❌ | ❌ |
| `SubsystemFireBlockBehavior` | 3 | 火焰蔓延，任意 y | ❌ | ❌ |
| `SubsystemRotBlockBehavior` | 1 | 腐烂，任意 y | ❌ | ❌ |
| `SubsystemIvyBlockBehavior` | 1 | 藤蔓生长，任意 y | ❌ | ❌ |
| `SubsystemSaplingBlockBehavior` | 5 | 树苗长成树，跨 y | ❌ | ❌ |
| `SubsystemCollapsingBlockBehavior` | 1 | 塌方，任意 y | ❌ | ❌ |
| `SubsystemSoilBlockBehavior` | 3 | 土壤湿度，任意 y | ❌ | ❌ |
| `SubsystemMagmaBlockBehavior` | 1 | 岩浆，任意 y | ❌ | ❌ |
| `SubsystemFertilizerBlockBehavior` | 1 | 施肥，任意 y | ❌ | ❌ |
| `SubsystemRakeBlockBehavior` | 2 | 耙子，任意 y | ❌ | ❌ |

### 3.1 `SubsystemWeather` 的三处修改（唯一"仅顶层"的环境修改）

`SubsystemWeather.cs:520-578`，方法 `FreezeThawAndDepositSnow`：

```csharp
int num3 = chunk.GetTopHeightFast(num, num2);              // ← 取顶层高度
int cellValueFast = chunk.GetCellValueFast(num, num3, num2);

if (precipitationShaftInfo.Type == PrecipitationType.Snow)
{
    // ① :558  水面结冰 → block 62
    SubsystemTerrain.ChangeCell(num5, num6, num7, Terrain.MakeBlockValue(62));

    // ② :571  顶层上方积雪 → block 61，y = num6 + 1
    SubsystemTerrain.ChangeCell(num5, num6 + 1, num7, Terrain.MakeBlockValue(61));

    // ③ :576  替换顶层为雪 → block 61
    SubsystemTerrain.ChangeCell(num5, num6, num7, Terrain.MakeBlockValue(61));
}
```

**触发时机**（`SubsystemWeather.cs:293-308`）：

```csharp
m_subsystemBlocksScanner.ScanningChunkCompleted += delegate(TerrainChunk chunk)
{
    if (EnvironmentBehaviorMode == Living)
        FreezeThawAndDepositSnow(chunk, 0.66f, 0.66f, forceDepositSnow: false);
};
SubsystemTerrain.TerrainUpdater.ChunkInitialized += delegate(TerrainChunk chunk)
{
    if (EnvironmentBehaviorMode == Living)
    {
        FreezeThawAndDepositSnow(chunk, 1f, 1f, AreWeatherEffectsEnabled);
        FreezeThawAndDepositSnow(chunk, 0.66f, 0.66f, AreWeatherEffectsEnabled);
    }
};
```

**关键：修改发生在 `ChunkInitialized` —— 即 chunk 刚加载时。** 这正是"客户端加载边缘 chunk 时两端不一致"的机制。

### 3.2 季节结束雪化

`SubsystemWeather.cs:333-345` 按 `SubsystemSeasons.Season` 决定降水类型：

```csharp
switch (m_subsystemSeasons.Season)
{
    case Season.Summer:  num = 1f; num2 = 1f; probability = 0.5f; break;
    case Season.Autumn:  num = 1.5f; num2 = 1f; probability = 0.5f; break;
    case Season.Winter:  num = 1f; num2 = 0f; probability = 0f; break;
    case Season.Spring:  ...
}
```

**冬季 → 降水类型变为 Snow → 触发结冰/积雪；冬季结束 → `thawProbability` 生效 → 雪化。**

**结论：季节切换导致的雪化/结冰，与天气降水走同一入口 `FreezeThawAndDepositSnow`，应统一标记为「季节修改」。**

---

## 4. 方案设计

### 4.1 核心思想

**所有 chunk 加载时，统一用「顶层 hash」做校验。** 不区分有无标记。

顶层 hash = **16×16 = 256 个格子**（chunk 长宽各 16，只有顶部一层）。

**校验不匹配时的处理，由标记决定"补多少"**：

| 标记 | 含义 | 不匹配时 |
|---|---|---|
| **无标记** | 自上次同步后无任何修改 | **只从主机获取顶部一层内容覆盖**（≤256 格） |
| **`SeasonOnly`** | 只有天气/季节修改（`SubsystemWeather` 的 3 处 `ChangeCell`） | **只从主机获取顶部一层内容覆盖**（≤256 格） |
| **`HasContentChange`** | 存在其他修改（玩家挖掘/放置、草、树、流体、火…） | **整块回传**（现有 `ChunkResync`） |

**关键：无标记和 SeasonOnly 走同一条"只补顶层"的路径。** 两者的区别仅在于标记的语义（诊断用），**处理方式相同**。

### 4.1.1 标记只在 chunk 加载时生效

**这是本方案的关键前提，必须与现有模式一致。**

现有实现（`TerrainSync.cs`）的校验入口只有一个 —— `OnChunkLoaded`（`:6045`）：

```csharp
public bool OnChunkLoaded(ChunkKey key)
{
    m_resyncDiagnostics?.RecordChunkLoaded();
    if (!m_chunkChangeSequence.ContainsKey(key))
        m_chunkChangeSequence[key] = m_lastAppliedChangeSequence;
    if (!m_verifier.WouldBeginVerify(key)) return false;
    if (!m_digestQueued.Add(key)) return true;      // 同一块重复触发：只保留一条
    m_digestQueue.Enqueue(key);
    return true;
}
```

**已加载的 chunk 收到 `CellChange` 时，不触发校验**（`TerrainSync.cs:7647`）：

```csharp
if (m_dirtyQueued.Add(key)) m_dirtyQueue.Enqueue(key);   // 同一块改了 N 格只重算一次
```

它只是**重算本地 hash**（用于后续比对），**不发起新的校验流程**。因为内容已经通过 `CellChange` 权威下发了，两端必然一致，无需再校验。

**所以 `m_chunkMutations` 的使用方式也是同样的口径**：

| 时机 | 行为 |
|---|---|
| chunk **加载**时 | 读 `m_chunkMutations[key]`，决定不匹配时补多少 |
| chunk **已加载**时收到 `CellChange` | **只更新内容 + 重算本地 hash**，**不读标记、不触发校验** |
| chunk 卸载时 | 保留标记（描述"自上次同步后的修改"，与加载状态无关） |

**这与现有模式完全一致，不引入新的校验时机。**

### 4.2 顶层 hash 覆盖的范围

```
顶层 hash = 16 × 16 = 256 格
           ├── 16×16 列，每列取 1 个格子（topHeight 处）
           └── 共 256 个 (contents, data) 对
```

**计算量对比**：

| 方式 | 操作数 | 相对 |
|---|---|---|
| **顶层 hash** | **512**（256 次读 Shafts + 256 次读 Cells） | **快 128 倍** |
| 全量 hash | 65536 | 1× |

**顶层 hash 包含两部分**（必须都比）：

```csharp
uint topHeightHash;   // 256 列的 topHeight（来自 Shafts 数组）
uint topBlockHash;    // 256 列顶格的 (contents, data)（来自 Cells 数组）
```

只比一个会漏判：
- 只比 `topBlockHash`：topHeight 不同但顶格方块相同（都是草）→ 漏判
- 只比 `topHeightHash`：topHeight 相同但顶格方块不同（挖掉后填回别的东西）→ 漏判

### 4.3 校验流程

```
chunk 加载
    ↓
计算顶层 hash（512 次操作，所有 chunk 都做）
    ↓
与主机比对 topHeightHash + topBlockHash
    ├── 一致 → 完成，无回传
    └── 不一致 → 查标记
            ├── 无标记 / SeasonOnly → TopLayerFix（只补 256 格内的差异）
            └── HasContentChange   → ChunkResync（整块回传）
```

**为什么无标记也要核对顶层 hash**：

即使没有修改标记，chunk 加载时仍可能因为以下原因与主机不同：
- 加载时序差异（主机 chunk 已初始化完成，客户端还在初始化中）
- 天气修改发生在标记建立之前
- 任何未被 `m_modifiedCells` 捕获的写入路径

**顶层 hash 校验是"每次加载都做"的廉价保险**（512 次操作 ≈ 0.01ms）。

### 4.4 圈层校验：2 圈全部校验（懒校验）

**用户定义**：以玩家所在 chunk 为中心，半径 32 格 = 2 个 chunk，**2 圈内全部校验**，采用**懒校验**（延迟到进入时才做）。

```
圈层 0（中心）: 1 chunk
圈层 1:        8 chunks
圈层 2:       16 chunks
─────────────────────
总计:         25 chunks   （等价于 5×5 窗口）
```

**与现有 `DefaultRadius = 2`（`TerrainSync.cs:526`）完全一致**，即 5×5 = 25 chunks。

**校验内容（2 圈内一律）**：

| 步骤 | 算法 | 何时做 |
|---|---|---|
| ① 顶层 hash | `ComputeTopLayer`（512 次） | **所有 chunk 加载时都做** |
| ② 全量 hash | `ComputeFromCells`（65536 格） | **仅在需要时**（见下） |

**② 全量 hash 的触发条件**：

| 情况 | 是否算全量 |
|---|---|
| 顶层 hash 一致 | ❌ 不算（省 65536 格） |
| 顶层不一致 + `HasContentChange` | ✅ 算（用于整块回传前的确认） |
| 顶层不一致 + 无标记/SeasonOnly | ❌ 不算（直接 TopLayerFix） |

**"懒校验"的准确含义**：

| 时机 | 行为 |
|---|---|
| chunk 刚加载 | 排入待校验队列，**不立即算** |
| 玩家在圈层 2 外 | 不校验 |
| 玩家进入圈层 2 | 排入队列，按每帧预算消费 |
| 玩家进入圈层 1 | 优先校验（预算提高） |
| 玩家进入圈层 0 | 立即校验（最高优先级） |

**为什么懒校验**：一次跨 chunk 会同时加载 25 个区块，如果同时算顶层 hash 也会瞬间堆积。懒校验让它们按玩家实际接近程度分摊到多帧。

**每帧预算**（沿用现有机制 + 新增分级）：

```csharp
// 现有（TerrainSyncOptions）
public int ClientDigestComputationsPerTick { get; set; } = 4;   // 每帧最多算 4 个摘要
public int PendingResolvesPerTick { get; set; } = 4;            // 每帧最多裁决 4 个
public int ClientResyncPartsPerTick { get; set; } = 1;          // 每帧最多落地 1 个重传分片

// 新增（按圈层分级）
public int TopLayerChecksPriority0PerTick { get; set; } = 8;   // 圈层 0：每帧 8 个
public int TopLayerChecksPriority1PerTick { get; set; } = 4;   // 圈层 1：每帧 4 个
public int TopLayerChecksPriority2PerTick { get; set; } = 2;   // 圈层 2：每帧 2 个
```

### 4.5 只下发不一致的方块（`TopLayerFix`）

当顶层 hash 不一致，且标记为「无标记」或「SeasonOnly」时：

```
客户端 → 主机：TopLayerMismatchReport {
    chunkKey,
    topHeightHash,      // 客户端算的
    topBlockHash        // 客户端算的
}

主机 → 客户端：TopLayerFix {
    chunkKey,
    cells: [ (x,y,z,contents,data) × N ],   // 只包含两端不同的格子
    hostTopHeightHash,
    hostTopBlockHash
}
```

**差异格数量预期**：

| 场景 | 差异格数 |
|---|---|
| 天气结冰（顶层几格） | 1~10 |
| 积雪（顶层 + 上方） | 1~30 |
| 大部分空气（无雪无冰） | **0** |

**无额外上限**：`TopLayerFix` 只下发"顶层 256 格中两端不同的那些"，**天然最多 256 格**（16×16）。

因此不需要额外的格数上限阈值——顶层一层本身就是上限。

**相比整块 `ChunkResync`（250~634ms）的收益**：只下发 ≤256 格，耗时 **< 1ms**。

### 4.6 标记的完整作用矩阵

| 标记 | 何时读取 | 加载时校验 | 不匹配时补多少 |
|---|---|---|---|
| **无标记** | chunk 加载时 | 顶层 hash | **顶部一层（≤256 格）** |
| **`SeasonOnly`** | chunk 加载时 | 顶层 hash | **顶部一层（≤256 格）** |
| **`HasContentChange`** | chunk 加载时 | 顶层 hash → 全量 hash | **整块 `ChunkResync`** |

**关键约束（与现有模式一致）：**

1. **标记只在 chunk 加载时读取和生效**
2. **已加载的 chunk 收到 `CellChange` 时：不读标记、不触发校验**，只更新内容 + 重算本地 hash（对应 `TerrainSync.cs:7647` 的 `m_dirtyQueued` 路径）
3. 无标记和 SeasonOnly 的处理方式**完全相同**，标记只用于诊断

**为什么已加载时不校验**：`CellChange` 是主机权威下发，客户端落地后两端必然一致。再校验是多余的。

## 5. 实施步骤

### 第 1 步：chunk 修改标记（基础）

**复用 LiveMap 现有的逐 chunk 记账模式，不新造数据结构。**

#### 1.1 现有可复用的结构（已核实）

LiveMap 已经实现"每个 chunk 记录每个客户端的校验状态"，正是需要的模式：

**① `ChunkVerifyState`（`Terrain/LiveMap.cs:211-228`）** — 5 态枚举：

```csharp
public enum ChunkVerifyState : byte
{
    None       = 0,   // 客户端没上报过这块：不画框
    Requested  = 1,   // 上报过摘要、主机还没裁决 —— 黄框
    Verified   = 2,   // 摘要核对通过 —— 绿框
    Failed     = 3,   // 不一致（主机已回传）—— 红框
    ReVerified = 4    // 回传后二次核对通过 —— 蓝框
}
```

**② `LiveMapClientChunk`（`LiveMap.cs:186-207`）** — 逐 chunk 逐身份记录：

```csharp
public sealed class LiveMapClientChunk
{
    public int ClientId;
    public string StableKey;    // 身份稳定键
    public int X, Z;
    public byte State;          // ChunkVerifyState
}
```

**③ `PlayerSessionRecord`（`TerrainSync.cs:1022`）** — 主机侧逐会话记账：

```csharp
public readonly Dictionary<ChunkKey, byte> Verdicts = new();           // 裁决结果
public readonly HashSet<ChunkKey> ReportedLoaded = new();              // 上报过的块（只增不减）
public readonly HashSet<ChunkKey> MemoryChunks = new();                // 当前内存集合（会删）
```

**④ `VerifyStateOf`（`TerrainSync.cs:2716`）** — 查询接口，`TerrainSyncModule.cs:2614` 调用它填充 LiveMap。

#### 1.2 新增：chunk 修改标记

**挂在 `PlayerSessionRecord` 旁，作为主机全局记账**（不是逐会话——修改是主机侧的客观事实）：

```csharp
[Flags]
public enum ChunkMutationKind : byte
{
    None    = 0,
    Season  = 1 << 0,   // SubsystemWeather 的 3 处 ChangeCell + 季节雪化
    Content = 1 << 1,   // 其他所有 ChangeCell（玩家/草/树/流体/火…）
}

// 主机侧全局（TerrainSyncHost）
private readonly Dictionary<ChunkKey, ChunkMutationKind> m_chunkMutations = new();
private readonly Dictionary<ChunkKey, long> m_chunkMutationSequence = new();   // 每次修改递增
```

**生命周期**（与 `OnChunkUnloaded` 对齐，`TerrainSync.cs:6068`）：

| 时机 | 操作 |
|---|---|
| chunk 首次加载 | 无标记（`None`） |
| 主机收到修改 | 按类型 OR 进 `m_chunkMutations[key]`，`m_chunkMutationSequence[key]++` |
| **客户端加载 chunk** | **读取标记**，决定不匹配时补多少 |
| **客户端已加载后收到 `CellChange`** | **不读标记、不触发校验**（与现有 `m_dirtyQueued` 路径一致，`TerrainSync.cs:7647`） |
| 客户端完成校验 | **不清除标记**（保留，用于下次加载判断） |
| chunk 卸载 | **保留标记**（描述"自上次同步后的修改"，与加载状态无关） |
| 会话结束 | 可清（下次会话是全新的） |

**注意：标记的"读"只发生在 chunk 加载时。** 这与现有 `OnChunkLoaded`（`TerrainSync.cs:6045`）作为唯一校验入口的约定一致。

#### 1.3 季节标记的埋点方式

**方案对比**：

| 方案 | 做法 | 优点 | 缺点 |
|---|---|---|---|
| **A. 引擎侧埋点** | 改 `SubsystemWeather.cs`，3 处 `ChangeCell` 包一层作用域 | 精确 | 需改引擎（SuAPI `//mod` 标记），维护成本高 |
| **B. mod 侧监听** | 监听 `TerrainUpdater.ChunkInitialized` + `BlocksScanner.ScanningChunkCompleted`，在这两个事件期间捕获的改动标为 Season | **不改引擎** | 需保证事件顺序正确 |

**推荐方案 B（不改引擎）。**

理由：`SubsystemWeather` 的 `FreezeThawAndDepositSnow` **只在两个事件里被调用**（`SubsystemWeather.cs:293-308`）：

```csharp
m_subsystemBlocksScanner.ScanningChunkCompleted += delegate(TerrainChunk chunk)
{
    if (EnvironmentBehaviorMode == Living)
        FreezeThawAndDepositSnow(chunk, 0.66f, 0.66f, forceDepositSnow: false);
};
SubsystemTerrain.TerrainUpdater.ChunkInitialized += delegate(TerrainChunk chunk)
{
    if (EnvironmentBehaviorMode == Living)
    {
        FreezeThawAndDepositSnow(chunk, 1f, 1f, AreWeatherEffectsEnabled);
        FreezeThawAndDepositSnow(chunk, 0.66f, 0.66f, AreWeatherEffectsEnabled);
    }
};
```

**实现方式**：

```csharp
// TerrainBridge 中
private int m_seasonMutationScopeDepth;   // 0 = 不在天气回调内

public IDisposable BeginSeasonMutationScope()
{
    m_seasonMutationScopeDepth++;
    return new SeasonScope(this);   // Dispose 时 --
}

public bool InSeasonMutationScope => m_seasonMutationScopeDepth > 0;

// SuSubsystemTerrain 捕获改动时分类
private ChunkMutationKind ClassifyMutation(Point3 cell)
{
    return m_bridge.InSeasonMutationScope
        ? ChunkMutationKind.Season
        : ChunkMutationKind.Content;
}
```

**订阅点**（mod 侧，不改引擎）：

```csharp
// 在 TerrainSyncModule 或 TerrainBridge 初始化时
TerrainUpdater.ChunkInitialized += chunk =>
{
    using (bridge.BeginSeasonMutationScope())
    {
        // 天气回调此时会在同一事件链内执行 ChangeCell
        // 我们的 scope 覆盖整个 ChunkInitialized 委托链
    }
};
```

**⚠️ 关键风险**：`ChunkInitialized` 事件是**多播**，天气的委托和我们的委托**执行顺序不确定**。如果我们的 scope 前后包住整个事件，需要**确保天气委托在我们的 scope 内执行**。

**更稳妥的实现**：不用 scope，而是在**捕获改动后按"当前帧是否发生过天气事件"判断**：

```csharp
// 主机帧内记录
private bool m_weatherMutatedThisTick;

// 天气事件订阅（在 mod 侧，通过反射拿到 SubsystemWeather 的委托后追加）
// 或者在 ChunkInitialized 时把我们自己的委托追加到天气委托之后

// 捕获时
ChunkMutationKind kind = m_weatherMutatedThisTick
    ? ChunkMutationKind.Season
    : ChunkMutationKind.Content;
```

**如果方案 B 因事件顺序不可靠而失败，退回到方案 A（引擎侧埋点）**，改动量很小（3 处 `ChangeCell` 外层加一行）。

#### 1.4 季节结束雪化

`SubsystemWeather.cs:333-345` 按 `SubsystemSeasons.Season` 决定降水类型。**季节切换导致雪化时，`thawProbability` 生效，同样走 `FreezeThawAndDepositSnow`。**

**所以不需要单独识别"季节切换"** —— 只要 `FreezeThawAndDepositSnow` 内的修改都标记为 `Season`，结冰和雪化都覆盖到了。

### 第 2 步：顶层 hash（快速校验）

**新增 `ChunkDigest.ComputeTopLayer`**：

```csharp
/// <summary>
/// 顶层摘要：16×16 列的 topHeight + 每列顶格方块。
/// 读 Shafts（256）+ Cells（256）= 512 次，比全量快 128 倍。
/// </summary>
public void ComputeTopLayer(int[] cells, int[] shafts)
{
    if (cells == null) throw new ArgumentNullException(nameof(cells));
    if (shafts == null) throw new ArgumentNullException(nameof(shafts));

    uint heightHash = 2166136261u;   // FNV-1a offset basis
    uint blockHash  = 2166136261u;

    for (int col = 0; col < 256; col++)
    {
        int shaft = shafts[col];
        int top = shaft & 0xFF;                     // TopHeightMask

        heightHash ^= (uint)top;
        heightHash *= 16777619u;

        // top 之上是空气；top 本身是可见方块
        int x = col & 0xF;
        int z = col >> 4;
        int index = top + x * 256 + z * 4096;       // CalculateCellIndex
        uint value = unchecked((uint)cells[index]) & 0xFFFFC3FFu;   // 剥 light

        blockHash ^= value;
        blockHash *= 16777619u;
    }

    m_topHeightHash = heightHash;
    m_topBlockHash = blockHash;
    m_toplevelComputed = true;
}
```

**必须同时比较 `heightHash` 和 `blockHash`**：
- 只比 `blockHash` 会漏判：topHeight 不同但顶格方块相同（比如都是草）
- 只比 `heightHash` 会漏判：topHeight 相同但顶格方块不同（挖掘后填回别的东西）

**切片粒度（用户确认）：只有顶部一层，不做 16 个 y 切片。**

顶层 hash 已经是"每列取 1 格"的聚合，再细分成 16 个 y 切片没有意义（每个切片最多只有 16 个格子贡献）。保持**两个 32 位 hash**（`topHeightHash` + `topBlockHash`）即可。

### 第 3 步：全量 hash 重写（零风险，快 2 倍）

**删除 `Compute()`**（当前是死代码，`IPackedTerrainWorld` 只有 `TerrainBridge` 一处实现）。

**`ComputePacked` 重写为连续扫描 + 循环展开**：

```csharp
public void ComputeFromCells(int[] cells)
{
    if (cells == null) throw new ArgumentNullException(nameof(cells));
    if (cells.Length < SizeXZ * SizeXZ * Height)
        throw new ArgumentException("Cell array is smaller than one chunk.", nameof(cells));

    for (int i = 0; i < SliceCount; i++) m_slices[i] = 2166136261u;
    m_shaftHash = 0u;

    // 数组布局 i = y + x*256 + z*4096
    // 外层 column (256 列)，内层 slice (16 段 × 16 格)
    for (int column = 0; column < 256; column++)
    {
        int baseIdx = column * 256;
        for (int slice = 0; slice < 16; slice++)
        {
            uint hash = m_slices[slice];
            int start = baseIdx + (slice << 4);
            int end = start + 16;
            for (int i = start; i < end; i++)
            {
                hash ^= unchecked((uint)cells[i]) & 0xFFFFC3FFu;   // 剥 light
                hash *= 16777619u;
            }
            m_slices[slice] = hash;
        }
    }
    m_computed = true;
}
```

**优化点**：
| 项 | 改前 | 改后 |
|---|---|---|
| 委托调用 | 65536 次 | **0** |
| 每格操作 | ~8 次 | **4 次** |
| contents/data 分别散列 | 2 次混入 | **1 次** |

**注意**：遍历顺序从 `x→z→y` 改为 `column→slice→y`，**必须确认两端完全一致**，否则永久失配。

### 第 4 步：读取数组的通道

引擎 `Cells` / `Shafts` 都是 `private`。项目已有成熟模式 `BindFieldRef`（`IModParentField.cs:33`）：

```csharp
ModFieldRef<TTarget, TValue> BindFieldRef<TTarget, TValue>(string fieldName);
```

文档明确：
> Binds a private instance field once and returns a ref delegate to its actual storage.
> **This is the preferred API for repeated per-frame access.**
> The returned delegate performs no lookup or boxing.

**用法（严格遵守）**：

```csharp
// 构造时绑定一次
m_cellsRef  = m_parentField.BindFieldRef<TerrainChunk, int[]>("Cells");
m_shaftsRef = m_parentField.BindFieldRef<TerrainChunk, int[]>("Shafts");

// 使用时：每次传当前 chunk，取到后立即用完
public bool TryGetChunkArrays(ChunkKey key, out int[] cells, out int[] shafts)
{
    cells = null; shafts = null;
    TerrainChunk chunk = ...;
    if (chunk == null) return false;
    cells  = m_cellsRef(chunk);
    shafts = m_shaftsRef(chunk);
    return cells != null && shafts != null;
}
```

**安全规则**：

| 做法 | 安全性 |
|---|---|
| `cellsRef(chunk)` 每次传当前 chunk，用完即弃 | ✅ 安全 |
| 缓存 `int[] cells` 跨帧使用 | ❌ **危险**（chunk 可能 `Dispose` → 数组归还缓存 → 被别的 chunk 租用） |
| 缓存 ref delegate 本身 | ✅ 安全（每次调用重新从 target 读字段） |

**风险说明**：`TerrainChunk.Dispose()`（`:88-97`）会 `m_cellsCache.Return(Cells)`。但 ref delegate 每次调用都重新读字段，所以在 chunk 有效期内取值是安全的。

### 第 5 步：稳定状态（`StableForDigest`）

只有满足以下**全部**条件才允许计算摘要：

```
① chunk.State >= Valid
② chunk.ThreadState >= Valid
③ 内容连续 2 个采样帧无变化
④ ModificationCounter 无变化
⑤ 无待处理的本地 CellChange
⑥ 【必须】无进行中的 ChunkResync 落地    ← 当前缺失
⑦ 【必须】无待下发的季节修改标记
```

**第 ⑥ 条理由**：`ApplyQueuedResyncParts`（`TerrainSync.cs:6543`）每帧落地 1 片，**落地期间内容在变**。不排除会必然失配 → 死循环。

**第 ⑦ 条理由**：如果主机还有未下发的季节修改，客户端算出的 hash 必然不同。

**不满足时发给主机 `NotReady`，不计入 Mismatch。**

### 第 6 步：NotReady 与 Mismatch 分离（核心修复）

**当前问题**：`ChunkDigestVerdict` 已有 `NOT_READY = 0`（`terrain.proto:28`），但实际被当成 `MISMATCH` 处理，导致每次都要整块回传。

**新判定规则**：

```
以下全部 → NOT_READY（指数退避，不回传，不计 mismatch）
    · 客户端 chunk 未 StableForDigest
    · 主机 chunk 未稳定
    · clientTerrainSequence != hostTerrainSequence
    · clientRevision != hostRevision
    · 客户端刚加载，尚未收到该区块的权威版本
    · 区块正在切换内存状态（加载/卸载中）
    · 有进行中的 ChunkResync

只有全部满足 → MISMATCH（整块回传）
    hostRevision == clientRevision
    hostTerrainSequence == clientTerrainSequence
    双方都 StableForDigest
    clientHash != hostHash
```

**协议改动**（允许不兼容，无需保留旧字段顺序）：

```protobuf
// terrain.proto
message ChunkDigestReport {
  message Entry {
    ChunkKey key = 1;
    uint32 hash = 2;
    uint64 chunk_revision = 3;        // 新增：客户端记录的该 chunk 修改序号
    int64 terrain_sequence = 4;       // 新增：客户端地形同步序号
    uint32 ready_state = 5;           // 新增：0=NotStable, 1=Stable
  }
  repeated Entry entries = 1;
}

message ChunkVerdict {
  message Entry {
    ChunkKey key = 1;
    ChunkDigestVerdict verdict = 2;
    uint32 host_hash = 3;
    uint64 host_revision = 4;         // 新增：主机该 chunk 修改序号
    int64 host_terrain_sequence = 5;  // 新增：主机地形同步序号
  }
  repeated Entry entries = 1;
}
```

### 第 7 步：只下发差异格（`TopLayerFix`）

**新增消息**：

```protobuf
// Client -> Host: 顶层 hash 不一致，请求差异格
message TopLayerMismatchReport {
  ChunkKey key = 1;
  uint32 top_height_hash = 2;    // 客户端算的 256 列 topHeight hash
  uint32 top_block_hash = 3;     // 客户端算的 256 格顶格方块 hash
}

// Host -> Client: 只下发不一致的格子
message TopLayerFix {
  ChunkKey key = 1;
  repeated CellEdit cells = 2;   // 复用现有 CellEdit（chunk-local 坐标）
  uint32 host_top_height_hash = 3;
  uint32 host_top_block_hash = 4;
}
```

**完整流程**：

```
chunk 加载
    ↓
计算顶层 hash（所有 chunk 都做，512 次操作）
    ↓
与主机比对 topHeightHash + topBlockHash
    ├── 一致 → 完成，无回传
    └── 不一致 → 查标记 m_chunkMutations[key]
            ├── None / SeasonOnly → 发 TopLayerMismatchReport
            │                        主机只回 TopLayerFix（≤256 格，顶层全部）
            │                        → 客户端覆盖这 256 格范围内的差异
            └── HasContentChange → 现有 ChunkResync（整块回传）
```

**预期收益**：

| 场景 | 当前 | 修后 |
|---|---|---|
| 无标记 / SeasonOnly 且顶层不一致 | 整块回传 250~634ms | **≤256 格，< 1ms** |
| 顶层一致 | 也要整块比对 65536 格 | **512 次操作，跳过整块** |
| HasContentChange | 整块回传 | 整块回传（不变） |

**无额外上限**：顶层一层固定 16×16 = 256 格，这就是天然上限，不需要阈值判断。

### 第 8 步：诊断输出到 LiveMap

**扩展 `TerrainResyncDiagnosticWindow`**（`TerrainSync.cs:124`，已有 20 个指标）：

**现有指标**（无需新增）：

```
HashChecks / HashMatches / HashMismatches / HashMs / HashMaxMs
ResyncBytes / ResyncChangedCells / ResyncUnchangedCells
CircuitCallbackMs / DecodeMs / ApplyMs / FinalDigestMs
NoPendingSixFaceRollbackMs
ChunkLoadEvents / ChunkUnloadEvents / AppliedParts / ShardBytes
```

**新增指标**：

```csharp
public ulong TopLayerChecks;        // 顶层校验次数
public ulong TopLayerMatches;
public ulong TopLayerMismatches;
public ulong TopLayerFixCells;      // 通过 TopLayerFix 修复的格数
public ulong SeasonOnlyChunks;      // 标记为 SeasonOnly 的 chunk 数
public ulong ContentChangedChunks;  // 标记为 HasContentChange 的 chunk 数
public ulong NotReadyVerdicts;      // NotReady 次数（与 Mismatch 分开）
```

**新增失配明细**（环形缓冲 200 条，**不设上限**——反复加载都要记录）：

```protobuf
message DigestMismatch {
  ChunkKey key = 1;
  uint32 client_hash = 2;
  uint32 host_hash = 3;
  int64 client_terrain_sequence = 4;
  int64 host_terrain_sequence = 5;
  uint32 client_chunk_state = 6;
  uint32 client_thread_state = 7;
  uint32 client_modification_counter = 8;
  uint32 mutation_kind = 9;                          // Season / Content / None
  bool client_unsettled = 10;
  repeated uint32 client_slice_hashes = 11;          // 16 个
  repeated uint32 host_slice_hashes = 12;            // 16 个
  int32 first_differing_slice = 13;
  repeated CellDiff cell_diffs = 14;                 // 最多 8 个
}
message CellDiff {
  int32 x = 1; int32 y = 2; int32 z = 3;
  uint32 client_contents = 4; uint32 client_data = 5;
  uint32 host_contents = 6;   uint32 host_data = 7;
}
```

**日志策略**：ModLog（`Engine/ModLog.cs`）已有完善机制，**不改动**：

```
data:/Logs/Server/SuMultiplayer-*.log      ← 主机
data:/Logs/Client/SuMultiplayer-*.log      ← 客户端
缓冲 256KB / 4096 行才落盘；单文件 2 MiB；每天 4 卷；保留 7 天
```

诊断明细**只走 LiveMap**，日志仅放摘要（避免日志膨胀）。

---

## 6. 验收标准

红米移动跨越多个 chunk 时：

| 指标 | 目标 | 测量方式 |
|---|---|---|
| 主机 `gameUpdateHz` | 保持 ~180Hz | LiveMap |
| 红米 `ChunkResync` 洪峰 | **不再出现** | `resyncBytes` 增量 / 秒 |
| 未修改的远端区块 | 只出现 Match 或 NotReady | `hashMatches` / `notReadyVerdicts` |
| Mismatch 触发条件 | **只在真实方块值差异时** | `hashMismatches` + 明细 |
| 红米 `terrain-sync` 单次峰值 | **< 50ms**（当前 250~634ms） | LiveMap `maxMs` |
| `circuitCallbackMs` | 接近 0 | 诊断窗口 |
| `noPendingSixFaceRollbackMs` | 不随普通地形加载增长 | 诊断窗口 |
| 跨 chunk 后掉帧率 | **< 10%**（当前 63.1%） | 采集器统计 |
| 顶层校验命中率 | `SeasonOnly` 场景 > 90% | `topLayerMatches` / `topLayerChecks` |

**LiveMap 需显示**：

```
NotReady / hashChecks / hashMatches / hashMismatches
resyncBytes / resyncApplied
chunkLoadEvents / chunkUnloadEvents
hostRevision / clientRevision mismatch
topLayerChecks / topLayerMatches / topLayerMismatches
seasonOnlyChunks / contentChangedChunks
```

---

## 7. 实施顺序

```
第 1 步：chunk 修改标记（第 5 章第 1 步）
    ├── 引擎侧或 mod 侧：天气修改打 Season 标记
    └── m_chunkMutations 记账（复用 LiveMap 模式）
    → 无行为变化，纯记录

第 2 步：诊断 → LiveMap（第 5 章第 8 步）
    ├── 扩 TerrainResyncDiagnosticWindow
    ├── 扩 terrain.proto
    └── 扩 live-map.html
    → 部署，红米移动，观察失配明细
    → 【决策点】看失配是否集中在顶层

第 3 步：删除 Compute() + ComputePacked 重写（第 5 章第 3 步）
    → 快 2 倍，零风险

第 4 步：顶层 hash（第 5 章第 2 步）
    → 新增 ComputeTopLayer（512 次操作）
    → 【核心】所有 chunk 加载时都用它校验

第 5 步：TopLayerFix 协议 + 差异格下发（第 5 章第 7 步）
    → 无标记 / SeasonOnly 走这条路
    → 【核心修复】从 250~634ms 降到 < 1ms

第 6 步：StableForDigest（第 5 章第 5 步）
    → 含"无进行中 resync"和"无待下发季节标记"

第 7 步：协议加 revision/sequence（第 5 章第 6 步）

第 8 步：NotReady / Mismatch 分离（第 5 章第 6 步）

第 9 步：红米移动验证（第 6 章）
```

**关键：第 4+5 步是核心修复**，其余是配套。建议第 1~2 步先做完验证，第 3~5 步一起做（因为顶层 hash 和 TopLayerFix 是配套的）。

## 8. 风险与注意事项

### 8.1 顶层 hash 不能单独确认一致

**顶层 hash 只能"快速否证"，不能"确认相同"**。

漏判场景：

| 场景 | topHeight | 顶层方块 | 中间层 | 顶层 hash 能检出？ |
|---|---|---|---|---|
| 草扩散到相邻格 | 不变 | 不变 | **变** | ❌ 漏判 |
| 树苗长成树 | **变** | **变** | 变 | ✅ |
| 流体流动 | 不变 | 不变 | **变** | ❌ 漏判 |
| 玩家垂直挖掘 | **变** | **变** | 变 | ✅ |
| 天气结冰/积雪 | **变** | **变** | 不变 | ✅ |

**结论**：`SeasonOnly` 标记的 chunk 用顶层 hash 是**安全的**（因为该标记保证只有天气改过，且天气只改顶层）。**但标记必须准确**——一旦漏标（比如草扩散被误标为 SeasonOnly），就会漏判。

**所以第 1 步的标记准确性是整个方案的前提。**

### 8.2 遍历顺序必须两端一致

`ComputeFromCells` 从 `x→z→y` 改为 `column→slice→y`。**两端必须用完全相同的实现**，否则 hash 永久不同。

**建议**：把 `ChunkDigest` 移到共享位置，或两端共用同一份代码（当前已共用 `Terrain/ChunkDigest.cs`）。

### 8.3 `BindFieldRef` 的生命周期

| 规则 | 说明 |
|---|---|
| ✅ 每次传当前 chunk | ref delegate 每次重新读字段 |
| ❌ 不跨帧缓存数组 | chunk 可能 `Dispose`，数组归还缓存池后被别的 chunk 租用 |
| ✅ 可缓存 delegate | delegate 本身无状态 |

### 8.4 Data 字段的处理（已定：不剥离）

**用户确认：不剥流体 `isTop`。**

理由：原本上面是水，同步后仍然是水。如果 `isTop` 在两端不同，那是**真实的同步 bug**，应该修复而不是用 canonicalizer 掩盖。

**因此保留 `DataCanonicalizer` 不启用**（属性保留但设为 `null`）。hash 掩码仍是 `0xFFFFC3FF`（只剥 light）。

### 8.5 不能绕过的红线

以下做法**禁止**（只会隐藏问题或加重卡顿）：

- ❌ 关闭 hash 校验
- ❌ 跳过 Mismatch 判定
- ❌ 单纯提高每帧预算
- ❌ 对未修改区块也强制整块回传

---

## 9. 引用索引

### 引擎侧文件

| 文件 | 关键位置 |
|---|---|
| `Survivalcraft/Game/TerrainChunk.cs` | `:67` Cells 数组；`:69` Shafts 数组；`:118` CalculateCellIndex；`:139,144` GetCellValueFast；`:169` GetShaftValueFast；`:199` GetTopHeightFast；`:43,45` State/ThreadState；`:55` ModificationCounter |
| `Survivalcraft/Game/Terrain.cs` | `:72-80` 位掩码常量；`:401` MakeBlockValue；`:408` ExtractContents；`:418` ExtractData；`:430` ExtractLight；`:434` ExtractTopHeight |
| `Survivalcraft/Game/SubsystemTerrain.cs` | `:229` ChangeCell；`:235-237` 剥 light 后比较 |
| `Survivalcraft/Game/SubsystemWeather.cs` | `:293-308` ChunkInitialized 触发；`:520-578` FreezeThawAndDepositSnow；`:558,571,576` 三处 ChangeCell；`:333-345` 季节切换 |
| `Survivalcraft/Game/ArrayCache.cs` | `:55-78` Rent 返回 `new T[capacity]`（连续托管数组） |
| `EntitySystem/SuAPI/IModParentField.cs` | `:13` ModFieldRef；`:33` BindFieldRef |

### Mod 侧文件

| 文件 | 关键位置 |
|---|---|
| `Mod/SuMultiplayer/Terrain/ChunkDigest.cs` | `:41` ChunkDigest 类；`:62` DataCanonicalizer；`:65` IncludeShafts；`:88` CellHash；`:117` Compute()（待删）；`:142` ComputePacked |
| `Mod/SuMultiplayer/Terrain/TerrainSync.cs` | `:124` TerrainResyncDiagnosticWindow；`:526` DefaultRadius=2；`:1709,5446` m_chunkChangeSequence；`:6019` SendInterest；`:6045` OnChunkLoaded；`:6068` OnChunkUnloaded；`:6543` ApplyQueuedResyncParts；`:6460` ComputeHash |
| `Mod/SuMultiplayer/Engine/TerrainBridge.cs` | `:28` TerrainBridge 类；`:288` GetPackedCellSource；`:165-167` HostChanged 事件；`:1358` PublishHostCells；`:302` ApplyCells |
| `Mod/SuMultiplayer/Engine/SuSubsystemTerrain.cs` | `:789-795` 唯一捕获点（m_modifiedCells → PublishHostCells） |
| `Mod/SuMultiplayer/Engine/ModLog.cs` | `:20` ModLog 类；`:28-37` 缓冲/轮转常量（不改动） |
| `Mod/SuMultiplayer/Protocol/terrain.proto` | `:26-29` ChunkDigestVerdict；`:31-43` ChunkDigestReport；`:45-56` ChunkVerdict |

### 相关文档

| 文档 | 内容 |
|---|---|
| `doc/chunk-digest.html` | chunk 摘要现有设计（42KB） |
| `doc/chunk-interest.html` | 兴趣窗口机制 |
| `doc/chunk-region-sync.html` | 区块区域同步 |
| `doc/known-sync-gaps.md` | 已知同步缺口 |
| `doc/message-protocol.md` | 消息协议规范 |

---

## 10. 已确认决策

| # | 问题 | 决策 |
|---|---|---|
| 1 | **校验时机** | **所有 chunk 加载时都用顶层 hash 校验**，不区分有无标记 |
| 2 | **顶层 hash 粒度** | **只有顶部一层，16×16 = 256 格**（两个 32 位 hash：topHeight + topBlock） |
| 3 | **不匹配时补多少** | 无标记 / `SeasonOnly` → **只补顶部一层（≤256 格）**；`HasContentChange` → 整块回传 |
| 4 | **圈层校验范围** | **2 圈（25 chunks）全部校验**，采用懒校验分级预算 |
| 5 | **季节标记埋点** | **优先 mod 侧监听（不改引擎）**；若事件顺序不可靠则退回引擎侧埋点 |
| 6 | **`Data` 是否剥流体 `isTop`** | **不剥**。原本是水同步后仍是水；不一致就是真 bug |
| 7 | **季节结束雪化** | **不需要单独识别**。走同一入口 `FreezeThawAndDepositSnow`，统一标 `Season` |
| 8 | **`TopLayerFix` 差异格上限** | **无额外上限**。顶层 16×16 = 256 格即天然上限 |

## 11. 参考实现索引（LiveMap 现有模式）

本方案的"逐 chunk 记账"直接复用 LiveMap 已有实现，不新造轮子：

| 结构 | 位置 | 用途 |
|---|---|---|
| `ChunkVerifyState`（5 态枚举） | `Terrain/LiveMap.cs:211-228` | 逐 chunk 校验状态着色 |
| `LiveMapClientChunk` | `Terrain/LiveMap.cs:186-207` | 逐 chunk 逐身份记录 |
| `PlayerSessionRecord.Verdicts` | `Terrain/TerrainSync.cs:1022` | 主机侧逐会话裁决记账 |
| `PlayerSessionRecord.ReportedLoaded` | `Terrain/TerrainSync.cs:985` | 上报过的块（只增不减） |
| `PlayerSessionRecord.MemoryChunks` | `Terrain/TerrainSync.cs:996` | 当前内存集合（会删） |
| `VerifyStateOf` | `Terrain/TerrainSync.cs:2716` | 查询接口 |
| LiveMap 填充点 | `Modules/TerrainSyncModule.cs:2614,2633` | 每帧把状态写进快照 |

**新增 `m_chunkMutations` 应该挂在与这些相同的位置**（`TerrainSyncHost` 内部），共享同一套生命周期管理。
