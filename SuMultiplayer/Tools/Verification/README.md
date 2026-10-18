# SuMultiplayer 自检套件（Tools/Verification）

把原先散落在仓库外临时目录（磁盘/换机即丢）的自动化回归网收进仓库，作为 SuMultiplayer 的常规回归网。

设计原则：

- **单一事实来源**：所有 C# 套件都**直接编译 mod 的真实生产源码**（`Terrain/`、`Net/`、`Tools/LiveMap/Program.cs`），
  不复制、不重写被测逻辑；页面套件直接取 `doc/live-map.html` 里的真实 `<script>` 在 Node 里跑。
- **无设备**：不需要游戏运行、不需要真机/服务器；tsverify 用 `127.0.0.1` 随机端口的真实 TCP。
- **无绝对路径、无私有信息**：工程路径全部相对工程文件目录；运行时需要的 mod 根目录从程序输出目录向上定位，
  找不到就**明确报错退出**，不猜路径、不静默跳过。套件里不出现设备 IP / 序列号 / 完整 GUID / token。
- **失败必须响亮**：断言失败退出码 1；环境/路径前置不满足退出码 2；不把"跑不了"包装成"通过"。

## 前置条件

1. **.NET SDK**：8.0.402+（`global.json` 要求，允许向上滚动；PATH 上的 SDK 10 也能编译 `net8.0` 目标）。
   检查：`dotnet --version`。
2. **NuGet 源 `cnb-nuget`**：C# 套件的 `Engine 0.1.6` 包与 mod 同源（`PackageReference`，不依赖手工构建过 mod）。
   只需配置一次：
   ```bash
   dotnet nuget add source https://nuget.cnb.cool/suceru.cb/SuAPICore/-/packages/v3/index.json --name cnb-nuget
   ```
   已配置过会报 "source already exists"，可忽略。
3. **Node.js ≥ 18**：只给页面无头套件用。**仓库不自带 node**，用系统/环境里已安装的 `node`；
   本机 PATH 上没有 node 时，把命令里的 `node` 换成你本机 node 可执行文件的绝对路径即可（脚本内部不写死任何 node 路径）。
   检查：`node --version`。
4. 以下命令都在**仓库根目录**执行（`Mod/SuMultiplayer/...` 这些相对路径从仓库根算起）。

## 一览：每个套件一条命令

| 套件 | 用途 | 一条命令 | 当前基线（v8 实测） |
|---|---|---|---|
| tsverify | 地形摘要/裁决链/身份归档/欠更新/每帧预算/位置快照/客户端内存集合/世界传输闸门/会话追帧（真实 TCP） | `dotnet run -c Release --project Mod/SuMultiplayer/Tools/Verification/TsVerify/TsVerify.csproj` | **430/430 通过** |
| tsverify-before | 改前对照（修复前 Terrain 源码快照，阴性对照） | `dotnet run -c Release --project Mod/SuMultiplayer/Tools/Verification/TsVerifyBefore/TsVerifyBefore.csproj` | **116/121 通过，5 失败（预期）** |
| lmverify | 实时地图载荷编解码（v8）+ 工具 `/state` JSON | `dotnet run -c Release --project Mod/SuMultiplayer/Tools/Verification/LmVerify/LmVerify.csproj` | **79/79 通过** |
| lmsample | 打印一份真实 `/state` JSON 样本（非通过/失败型） | `dotnet run -c Release --project Mod/SuMultiplayer/Tools/Verification/LmSample/LmSample.csproj` | 打印样本，退出码 0 |
| page | `doc/live-map.html` 的无头断言（Node，无浏览器，合成 feed） | `node Mod/SuMultiplayer/Tools/Verification/PageCheck/livemap-page-check.mjs` | **237/237 通过** |
| realfeed | 页面 + **真实** `/state`（需要 `Tools/LiveMap` 工具在线） | `node Mod/SuMultiplayer/Tools/Verification/PageCheck/livemap-realfeed-check.mjs` | **17/17 通过**（工具在线时） |

> 也可以用 `--project` 指到对应 `.csproj` 后加 `--no-build` 复用上次构建；首次跑必须联网 restore。

## 1. tsverify — 地形同步 / 裁决链主回归网

### 用途

用真实 TCP 回环 + mod 的真实 `Terrain/`、`Net/` 源码，验证地形摘要与裁决链的完整行为：

- 上报时主机缺内容 → `NotReady(hostHash=0)` 并登记待裁决（摘要不丢），内容可用后 `Tick` 补齐真实摘要 → `Verified`；
- 客户端摘要与主机不一致 → `Mismatch`（带真实主机摘要）+ 回传 → 重报 → `ReVerified`；
- 主机一直拿不到内容时**绝不伪造匹配**，原因只写一行（限流）、按节奏重请加载；
- 客户端收到 `NotReady` 后退避重报；
- **欠账新语义（用户三区域模型）**：欠更新数字 = **当前内存集合里没有该块的在线身份数**
  （订阅 / 投递成功都不清欠账，只有"该身份当前内存里真有这块"才清；两身份不叠加）；
- **内存区块集合 = 引擎已分配**：未定稿 / 海洋块也进整集与增量；定稿不重复、释放才移除、重连如实反映内存；
- **会话标识 + 断线重连追帧**：同一会话只追帧、换会话整图重下、历史越界明确拒绝（真实 TCP）；
- 对端断开 / 死连接：欠着的上报明确作废，发送必须**如实失败**（不假装成功）；
- 欠更新计数（实时地图 `chunks[].n`）按投递结果结算；
- 按身份（`UserId`）归档：重连复用同一条记录、不同身份不合并、身份缺失走退化路径且明确报错；
- 位置快照：真实 TCP 上传 20 份逐一被解码/应用，断链后新连接重新握手、主机摘掉死连接并抛 `ClientLost`；
- 窗口不再刷黄框、同步范围跟随客户端声明的可见范围（`radius*16`）；
- **v8 客户端当前内存区块集合**：握手整集 / 加载·卸载增量 / 重连替换 / 每帧消息预算 / 非法载荷拒绝
  （`CurrentLoaded` 与 `ReportedLoaded` 是两条独立事实；含 `CurrentLoaded=19` 的整集用例）；
- **v8 世界传输闸门**：`alreadyHasWorld` 语义 → 首次加入恰好发一次整图、重连绝不导出或发送；
  源码守卫锁定 `RejoinFlagPolicy` + `GameScreen` 唯一来源与主机发送分支的 rejoin 拦截；
- 每帧预算（摘要计算/上报发送/回传落地/改动后刷新）、排队中块卸载的作废计数、队列饱和时的限流日志；
- 源码守卫：直接读仓库里的 `doc/live-map.html`、`Tools/LiveMap/Program.cs`、`Terrain/LiveMap.cs`、
  `Modules/TerrainSyncModule.cs`、`Modules/WorldTransferModule.cs`、`Engine/NetworkAvatarSpawner.cs`，
  断言关键字符串/版本号没有回退（改页面/工具/编解码时必须同步更新用例）。

### 前置

- 上面的 .NET SDK + `cnb-nuget` 源。
- **不需要**游戏、设备、端口预留（自己起回环随机端口）。

### 运行

```bash
dotnet run -c Release --project Mod/SuMultiplayer/Tools/Verification/TsVerify/TsVerify.csproj
```

### 预期输出

- 逐节打印 `== 节名 ==` 与每条的 `✓ 描述` / `✗ 描述`；
- 末尾：
  ```
  ---------------------------------------------
  结果：<实测>/<实测> 通过
  RESULT terrain digest/verdict chain cases passed
  ```
- 退出码：`0` = 全通过；`1` = 有失败（末尾会列出失败项）；`2` = 找不到 mod 根目录（路径前置不满足）。

### 说明

- 运行时从输出目录向上寻找同时包含 `ModInfo.xml` 与 `Terrain/` 的目录作为 mod 根目录；
  找不到直接报错退出（源码守卫需要的文件都在该目录下）。
- 套件内部会把一份 `feed-e2.json` 写到系统临时目录并打印路径，仅供人工核对，不影响结果。

## 2. tsverify-before — 改前对照（阴性对照）

### 用途

用**修复前的 Terrain 源码快照**（本目录 `Terrain/*.cs`，从 Mod 仓库索引取出）跑同一套断言，证明用例确实能抓住旧 bug。
它**预期不是全绿**：K 节 5 条"窗口刷黄框"断言在修复前代码上必然失败——这正是要对照出来的。

### 前置

同 tsverify（.NET SDK + `cnb-nuget`）。传输层用当前 `Net/*.cs`，只有 `Terrain/` 是改前快照。

### 运行

```bash
dotnet run -c Release --project Mod/SuMultiplayer/Tools/Verification/TsVerifyBefore/TsVerifyBefore.csproj
```

### 预期输出

- 末尾 `结果：<实测>/<实测> 通过`，随后列出失败项；
- 与本目录 `Terrain/` 快照对应的实测是 **116/121 通过、5 条 K 节失败**，其余通过；
- 退出码 `1` 属预期（阴性对照）；如果这里**全绿**，说明对照没在测旧代码，要当作对照失效处理。

## 3. lmverify — 实时地图载荷 / 工具 JSON

### 用途

验证实时地图链路上的数据形状（无设备，共 9 节）：

- `LiveMapCodec` 载荷版本（**v8**）/ 编解码往返（地形块、中心清单、逐块 `n`、客户端当前内存区块集合），
  每批都在单帧上限内；
- **v7 逐条身份状态**：每条客户端条目带 `State`（0..4）逐条往返（0 也照实带回，不丢字段、不猜状态）；
- **v8 `currentLoaded`**：玩家端头部记录末尾追加“当前内存区块数”；`/state` 的 `complete` 按它判定
  （`reported` 仍是会话累计参考值）；`clients[].chunks` 就是当前内存集合（卸载的块不再出现）；
- **两个身份的状态各归各的**（按身份稳定键）：同一块 `(-13,13)` 在 A/B 两个身份下状态不同，
  `/state` 里各带自己的 `s`（A=2 / B=1），不互相套用；
- 坐标读不到时必须带 `cellReason`，绝不留空；
- 旧版本 / 截断 / 尾部多余字节的载荷必须**明确失败**：v7/v6/v5/v4/v3 版本号一律拒绝，
  未改动的 v8 载荷正常解码（闸门只拦旧版本，不误伤自己）；
- 客户端区域随批分发：大集合不截断、超上限明确抛错；线上条目大小实测 **27 B/条**
  （10 字符合成稳定键；公式 = 4 id + 4 x + 4 z + (4 + 稳定键字节) + 1 状态。
  真实身份稳定键 17 字符时为 **34 B/条**；v8 另在每条玩家端头部记录 +4 B），满载单批仍 < 帧上限；
- 工具 `BuildStateJson` 的 `/state` 字段：`players[]` / `clients[]` 的 `reported` + `currentLoaded` +
  `[cx,cz,s]` / `bounds` 并集 / `counts`（含 `currentLoadedChunks`）/ `complete` 与原因 /
  没有快照时的字段形状。

### 前置

同 tsverify（.NET SDK + `cnb-nuget`；本套件不引引擎程序集）。

### 运行

```bash
dotnet run -c Release --project Mod/SuMultiplayer/Tools/Verification/LmVerify/LmVerify.csproj
```

### 预期输出

```
---------------------------------------------
结果：<实测>/<实测> 通过
RESULT livemap payload+tool cases passed
```

退出码：`0` 全通过；`1` 有失败（列出失败项）；其他非零 = 套件自身异常
（例如断言与当前源码版本不匹配时会抛异常——那必须当作用例需要同步，不是"通过"）。

## 4. lmsample — 打印真实 `/state` 样本

### 用途

用工具自己的 `BuildStateJson` + 合成快照打印一份真实的 `/state` JSON（地形 `d` 字段缩略），
供报告引用 / 人工核对字段形状。**不是通过/失败型套件**。

### 运行

```bash
dotnet run -c Release --project Mod/SuMultiplayer/Tools/Verification/LmSample/LmSample.csproj
```

### 预期输出

- 标准输出打印一份格式化 JSON（含 `payloadVersion`、`players`、`clients`、`chunks`、`bounds`、`counts` 等）；
- 退出码 `0` 即成功。

## 5. page — 页面无头自检（Node）

### 用途

在 Node 里用"录屏式" canvas 2D 上下文 + DOM 桩跑 `doc/live-map.html` 自己的 `<script>`，
对录到的绘制指令与 DOM 文本做断言（不需要浏览器/无头 Chrome）。当前 **237 版**配套
`doc/live-map.html`（130140 B / 1828 行 / 15:44 版）。覆盖：

- 两个角色 / 两块区域的默认绘制（主机地形只填充 `chunks[]`、**任何情况下都不画主机框**）；
- 没有任何虚线框 / 同色填充 / 验证框图例 / 验证勾选框（旧的"主机验证框"已整体删除）；
- 图例两组标题 + 中性灰说明 + 统计新行（`主机地形覆盖 N 块（无任何方框）· 客户端线框 N 圈`）；
- `?verify=0` 与不传参渲染完全一致（该参数不再是开关）；
- 旧版 feed / 客户端区域不完整（`complete=false`）/ 坐标缺失（`cell=null` + `cellReason`）的降级说明；
- 载荷版本不匹配必须报警；
- 视图作用域（主机并集 / 客户端只看该客户端范围内）、区域边界跟随、格内数字 `n>0` 才画；
- **v8：`clients[].chunks` = 客户端当前内存集合** —— 已卸载的块立刻消失（不再保留历史框）；
- **v8：`s=0`（刚加载、尚未上报/裁决）按中性灰处理**：不画彩色框、也不报警成非法状态；
- **v8：主机地形配色按游戏源码校正**（雪 / 水域 / 未知方块 id 各有断言）；
- **v8 `currentLoaded=0`**：页面加粗红字提示 + 统计行文案，明确写出"不是画错位置"及原因（S 节）；
- 缺状态 / 非法状态的条目逐块不画并明确计数；多客户端重叠内缩仍保持可见；
- 客户端线框全部实线 + 深色底衬；数据变化才"整片区域"拟合；等等。

### 前置

- Node.js ≥ 18（仓库不自带 node；用系统 node，PATH 没有就换成 node 可执行文件的绝对路径）。
- **不需要** `npm install`（脚本只用 `node:fs` / `node:vm` / `node:path` / `node:url`）。

### 运行

```bash
node Mod/SuMultiplayer/Tools/Verification/PageCheck/livemap-page-check.mjs
```

默认读取脚本相对路径 `../../../doc/live-map.html`（即 `Mod/SuMultiplayer/doc/live-map.html`），
也可以显式传路径：`node .../livemap-page-check.mjs <live-map.html 路径>`。

### 预期输出

```
---------------------------------------------
页面文件：<路径>（<字节数> 字节）
结果：<实测>/<实测> 通过
RESULT livemap page cases passed
```

退出码：`0` 全通过；`1` 有失败（列出失败项）；`2` 脚本自身前置失败（找不到页面 / 页面结构变了导致插桩失败）。

## 6. realfeed — 真实 feed 现场自检（Node，**需要工具在线**）

### 用途

用**真实 GET /state**（`Tools/LiveMap` 工具在线时的现场 feed）跑页面自己的 `<script>`，断言：

- 每个完整客户端的线框圈数 == `clients[].currentLoaded`（逐身份相等）；
- 四色 + `s=0` 中性灰的**逐块**颜色/位置正确（未定稿块 `s=0` 也有线框）；
- 主机侧 **0 个方框**（没有任何虚线框 / 同色填充）；
- 图例两组标题 + 中性灰说明 + 统计新行文案正确；UI 上没有验证框图例/勾选框。

### 前置

- Node.js ≥ 18；`Tools/LiveMap` 工具**在线**（默认地址 `http://127.0.0.1:8738/state`，回环；不写私有地址）。
- 页面路径相对本脚本解析（`../../../doc/live-map.html`）；可用第一个参数覆盖 `/state` 地址。

### 运行

```bash
node Mod/SuMultiplayer/Tools/Verification/PageCheck/livemap-realfeed-check.mjs
# 指定别的 /state： node .../livemap-realfeed-check.mjs http://127.0.0.1:<端口>/state
```

### 预期输出

```
---------------------------------------------
真实 feed 结果：17/17 通过
RESULT livemap real-feed cases passed
```

退出码：`0` 全通过；`1` 有失败；工具未在线时脚本会明确报错退出（不会把"跑不了"当成"通过"）。

## 本基线快照（v8，2026-10-18 实测）

- **取数时间**：2026-10-18 16:00–16:05（Asia/Shanghai，本机时钟；tsverify/lmverify/before/lmsample/page/realfeed 同轮实测）。
- **被测源码/页面 mtime**：`Terrain/TerrainSync.cs` 15:55:49、`Modules/TerrainSyncModule.cs` 15:55:18、
  `Terrain/LiveMap.cs` 14:53:08、`Tools/LiveMap/Program.cs` 14:53:18；
  `doc/live-map.html` 15:44:13（130140 B / 1828 行）、页面用例 `livemap-page-check.mjs` 15:45:38、
  真实 feed 用例 `livemap-realfeed-check.mjs` 16:01:30；套件 `TsVerify/Check.cs` 15:53:28
  （上游交付版，已按相对路径改造）；被测 `LiveMapCodec.Version = 8`。
- 测试文件行数：`TsVerify/Check.cs` 3633、`TsVerifyBefore/Check.cs` 969、`LmVerify/Verify.cs` 504、
  `LmSample/Sample.cs` 80、`PageCheck/livemap-page-check.mjs` 1270、`PageCheck/livemap-realfeed-check.mjs` 226。
- 各套件实测（按本轮固定写入，后续回归用同口径对照）：
  - tsverify **430/430** 通过（16:00 实测，退出码 0）
  - tsverify-before **116/121**（5 条 K 节失败为**阴性对照**，退出码 1 属预期）
  - lmverify **79/79** 通过（16:01 复跑，退出码 0）
  - lmsample 退出码 0（打印 `/state` 样本）
  - page **237/237** 通过（16:04 实测，退出码 0；配套 15:44 页面）
  - realfeed **17/17** 通过（16:04 实测，工具在线 `127.0.0.1:8738`，退出码 0）
- **TsVerify 上一轮的接口阻塞已解除**：已整体同步上游 15:53:28 版（含 `MemoryChunks` /
  `MemoryChunkCount` 新接口与追帧用例），实跑 430/430；旧 341 数字与 `CS0535` 说明作废。
- 条数变化（v7 → 最新）：

  | 套件 | v7 | 最新 | 变化 |
  |---|---|---|---|
  | tsverify | 281 | **430** | +149（v8 第一批 +60：X 当前内存集合 29 / Y 世界传输闸门 29 / M+2；本轮 +89：N 欠账三区域模型 19、X2 内存=引擎已分配 16、Z 会话标识+断线追帧 47、I 欠账语义 4、W 1、Y 2） |
  | lmverify | 70 | **79** | +9（Version/currentLoaded 语义、v7 版本闸门、新 v8 currentLoaded 节 5 条、section 5 拆分） |
  | page | 205 | **237** | +32（v7→255 两批 +50；255→237 净 **-18**：删除主机框相关约 20 条〔细虚线内框/验证框图例/`?verify` 开关/四态色框〕，新增"任何情况下不画主机框 / 图例两组 / 统计新行 / `?verify` 参数无效化 / 客户端线框全实线+单枚底衬"等） |
  | page-realfeed | —（新增） | **17/17** | 新套件：真实 `/state` 现场 feed（需 `Tools/LiveMap` 在线；默认回环 8738） |
  | tsverify-before | 116/121 | **116/121** | 不变（阴性对照 5 条 K 节失败） |

- `lmverify` 为 v8 断言版本（9 节：逐条 `State`、v8 `currentLoaded` 语义、两身份不串、v7/v6/v5/v4/v3 闸门）；
  本目录只有这一套，版本与行数以本目录文件为准。

### 已知事项（防重复/误拷）

- **PageCheck 已定稿 237 版**（配套 15:44 的 130140 B 页面）；旧的 255 版对应 15:32 页面、**已失效**
  （对当前页面会插桩失败、退出码 2），不要再用。真实 feed 用例 `livemap-realfeed-check.mjs`
  需要工具在线（未在线时脚本明确失败，不当作通过）。
- **`lmverify` 只有本目录一套**：入库时上游临时工程的旧 v5 版、过渡手写 v7 版、v8 初版都已先后被最终版覆盖
  （本目录 504 行、9 节、79 条）。**禁止再把旧版本或另拷一份进来**；若上游再有更新，
  以最新最终版覆盖本目录这一套并全量重跑，不保留第二套。

### 已知待清理项（不在本套件写域，未顺手改）

- `Mod/SuMultiplayer/Terrain/LiveMap.cs` 里 `MaxClientChunksPerBatch` 的注释写"256 × 13 B ≈ 3.3 KiB"，
  与 v7/v8 实现不一致：客户端条目实际 = 4 id + 4 x + 4 z + (4 + 稳定键字节) + 1 状态。
  真实身份稳定键 17 字符 → **34 B/条**（满批 256 条 = 8704 B）；`LmVerify` 用 10 字符合成键实测 **27 B/条**；
  v8 另在每条玩家端头部记录 +4 B（`currentLoaded`）。注释属 `Terrain/**` 维护范围，本套件只按实测断言，未改源码。

## 目录结构

```
Mod/SuMultiplayer/Tools/Verification/
├── README.md                     # 本文件
├── .gitignore                    # 排除嵌套工程的 bin/ obj/ .vs/
├── TsVerify/                     # 套件 1：主回归网（Check.cs + csproj）
├── TsVerifyBefore/               # 套件 2：改前对照（Check.cs + 修复前 Terrain/ 快照）
├── LmVerify/                     # 套件 3：载荷 / 工具 JSON（Verify.cs + csproj）
├── LmSample/                     # 套件 4：/state 样本（Sample.cs + csproj）
└── PageCheck/                    # 套件 5/6：页面无头用例（合成 feed）+ 真实 feed 现场自检（需工具在线）
```

各工程 `EnableDefaultCompileItems=false`，编译项显式列出；生产源码用 `..\..\..\` 相对路径引用，
`Mod/SuMultiplayer` 的 `SuMultiplayer.csproj` 已用 `<Compile Remove="Tools/**" />` 排除本目录，互不影响。

## 常见问题

- **`dotnet run` 报找不到 `Engine` 包 / NU1101**：`cnb-nuget` 源没配，按上面"前置条件 2"配置一次。
- **tsverify 退出码 2、提示找不到 mod 根目录**：不要从别的目录拷贝编译产物执行；用上面的 `--project` 命令
  （输出目录在 `Mod/SuMultiplayer/Tools/Verification/<套件>/bin/...` 下，能向上定位到 mod 根）。
- **页面套件报 `node: command not found`**：说明 PATH 没有 node，用本机 node 的绝对路径替换命令里的 `node`。
- **页面套件退出码 2**：页面 `<script>` 结构变了（插桩失败）或路径写错；先确认 `doc/live-map.html` 存在。
- **`livemap-realfeed-check.mjs` 报连接失败 / 非 17/17**：它需要 `Tools/LiveMap` 工具在线
  （默认 `http://127.0.0.1:8738/state`）；工具没起就明确失败，属前置不满足，不是页面回归。
- **改了 `doc/live-map.html` / `Tools/LiveMap/Program.cs` / `Terrain/LiveMap.cs` 后 tsverify 失败**：
  可能是源码守卫的预期值需要随版本更新（例如 `LiveMapCodec.Version`），这属于用例需要同步，不是产品回归。
