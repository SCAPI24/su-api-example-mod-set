# SuMultiplayer（联机重写工程）

`SuMultiplayer` 是联机功能的**重写工程**：交互逻辑、同步实现与模块划分仍在重写中，
目前只落地了骨架与第一个独立组件（状态变化队列）。

旧实现 `Mod/ScMultiplayer` 继续作为**行为参照与协议事实来源**，不再往里加新功能。

## 当前内容

```text
SuMultiplayer/
  ModInfo.xml                    # 扁平写法；IsMergeLib=true
  SuMultiplayer.csproj           # net8.0；引擎走 NuGet 制品源 cnb-nuget
  Plug/SuMultiplayerMod.cs       # IMod 入口：加载/卸载各一行日志
  Journal/                       # 已实现：状态变化队列（内存 + 落盘 + 分类筛选）
  Modules/                       # 已实现：模块宿主（能力依赖解析 + 异常隔离 + 热插拔下线）
  Atoms/                         # 已实现：原子契约 + Hz 分桶合并器
  Protocol/                      # protobuf 协议（.proto）+ 构建时生成的 C#（Protocol/Generated/）
  Session/                       # 已实现：房间发现列表 + 加入流程状态机
  Join/                          # 已实现：世界下载接收 + 存档条目名净化
  Terrain/                       # 已实现：chunk 摘要（只在加载时算）+ 加载时校验 + 同步服务（主机/客户端）
  Net/                           # TCP 地形通道（长度+序号+类型帧、有界队列、显式背压）+ MessageChannel（protobuf 注册表/分发）
  Tools/DebugBridge/             # 调试端：跑真实协议+合成世界，HTTP 给网页供数据（独立工程）
  Engine/                        # 引擎接线（薄）：配置 / 引擎桥（唯一线程边界）/ SubsystemTerrain 外壳
  Modules/SessionModule.cs       # 房间会话模块：创建房间（UDP 探测应答）/ 发现 / 加入
  Net/RoomBeacon.cs              # LAN 房间发现：文本协议探测与应答
  README.md                      # 本文件
  doc/
    multiplayer-architecture.html  # 旧联机模组解析（112 个 GUID）
    chunk-region-sync.html         # 区块 / 区域同步俯视图（21 个 GUID，含可播放动画）
    state-journal.html             # 状态变化队列设计与实测（25 个 GUID，含 4 个可播放动画）
    module-decoupling.md           # 模块解耦与「下线不影响其余」规范
    atom-merge.html                # 原子契约 / Hz 合并 / 兼容读取（13 个 GUID，含 3 个可播放动画）
    new-mod-status.html            # 新 mod 已完成功能总览（10 个 GUID，含 6 个可播放动画）
    join-room-plan.html            # 最小加入房间：现状 + 模块化方案 + 日志分流（16 个 GUID，含 3 个可播放动画）
    message-protocol.md            # **protobuf 消息层**：线格式 / 注册表 / 第三方 id / ext+Any / 加消息步骤
    rejoin-catchup.md              # 断线重连：会话标识 + 追帧（线上追加字段 / 有界历史 / 客户端落点）
    chunk-interest.html            # 多客户端 chunk 兴趣范围与同步判定（14 个 GUID，含 3 个可播放动画）
    chunk-bitmap-xor.html          # chunk 位图 + XOR 增量：能做什么、不能做什么（13 个 GUID，含 2 个可播放动画）
    chunk-digest.html              # 加载时校验 chunk 摘要 + TCP 策略（11 个 GUID，含 2 个可播放动画）
    terrain-sync-live.html         # 实时双端地图：主机 vs 客户端已加载内容（调试端供数据）
    ARCHITECTURE.md                # 骨架现状与待重写范围
```

## 已实现的第一个组件：状态变化队列（`Journal/`）

主机端与客户端各维护一条只追加的状态变化队列：**线性写入（实测 54 ns/条，零分配）**、
**分段快速落盘（38 MiB/s，块级 CRC，崩溃只截断尾块）**、
**按类别位掩码快速筛选（6.5 万条全量约 101 µs，最近 N 条某类约 8 µs）**。
纯 .NET、不依赖引擎，桌面与移动端可直接复用。

| 文件 | 作用 |
|---|---|
| `Journal/JournalTypes.cs` | 16 个分类、事件码、48 字节定长记录、筛选条件与统计 |
| `Journal/JournalOptions.cs` | 容量 / 分段 / 刷盘 / 保留 / 时钟注入等配置 |
| `Journal/StateChangeJournal.cs` | 环形内存队列、序号发布、掩码筛选、分类稀疏索引 |
| `Journal/JournalSegmentWriter.cs` | 落盘线程、文件格式、轮转与保留、崩溃恢复、CRC32 |
| `Journal/JournalExporter.cs` | 文本 / 二进制导出与回读、名称映射 |
| `Journal/JournalWritePolicy.cs` | 存盘频率预设（RealTime / Balanced / LowFrequency / MemoryOnly）+ 分类闸门（是否落盘 / 采样 / 强制 fsync） |

设计说明、可播放动画（环形写入 / 掩码扫描 / 背压 / 崩溃恢复）与全部实测数据见
[`doc/state-journal.html`](doc/state-journal.html)。

## 已实现的第二个组件：模块宿主（`Modules/`）

目标是**任何模块被关闭 / 缺失 / 初始化失败 / 运行中熔断，其余系统照常运行**（只掉功能，不掉会话）。
模块之间零直接引用，依赖写成"能力"而不是模块名；宿主负责依赖解析、异常隔离、熔断与热插拔下线。

| 文件 | 作用 |
|---|---|
| `Modules/ModuleContracts.cs` | 模块描述符（能力依赖）、状态、上下文、端口查询、状态快照 |
| `Modules/ModuleHost.cs` | 能力不动点解析、装配/卸载差值、逐模块异常隔离与熔断、热插拔、能力表 |

规范（五种"下线"含义、七条硬规则、跨机能力协商、验收判据）见
[`doc/module-decoupling.md`](doc/module-decoupling.md)。

## 旧联机模组解析（主要材料）

[`doc/multiplayer-architecture.html`](doc/multiplayer-architecture.html)
—— 面向非联机背景读者的功能 / 架构 / 模块 / 交互全过程解析，离线可开。
每个知识点带 **3 位域字母 + 2 位序号** 的 GUID 标记（如 `FLW04`），
便于在讨论移植时精确指代某一块；完整索引见文末 `IDX01`。

## 构建

```powershell
dotnet build Mod\SuMultiplayer\SuMultiplayer.csproj -c Debug
```

引擎依赖来自制品库源 `cnb-nuget`，无需克隆引擎工程。

## 遵循的硬约束（沿用 Mod 仓库规范）

- `ModInfo.xml` 扁平写法（`<Version>` / `<APIVersion>` 直接挂 `<ModInfo>` 下），`IsMergeLib=true`；
- 打包为 `.scmod`：ZIP 条目用 `/`、`ModInfo.xml` 在根目录、程序集放扁平 `Lib/`；
  禁止把 Engine / Survivalcraft / EntitySystem / SuAPICore 打进包；
- 只通过 SuAPI 注入（事件总线 / 注入器 / 父字段方法）扩展，不修改原版源码；
- 文本文件行尾 CRLF。
