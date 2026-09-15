# PlayerAiMod —— 玩家 AI

给 Survivalcraft 里的**玩家角色**装一套 AI：一个可分层、可打断、可回退的状态机，
加上"只读观察 + 只经玩家控制器行动"的传感器/执行器两层。

- 标识：`PlayerAiMod`（中文名「玩家 AI」/ 英文名 `Player AI`）
- 版本：`0.2.0`
- 平台：`net8.0`，Windows / Android 共用同一份平台无关 DLL
- **mod 依赖：`CmdBridgeMod`** —— 复用它的玩家控制器注入门面 `CmdBridgeMod.CmdBridgeInput`

## 1. 定位与铁律

| 允许（优势） | 禁止（特权） |
|---|---|
| 无限读取游戏内数据：世界 / 实体 / 背包 / 界面 / 事件 | 直接写游戏状态：生命、背包、方块、位置、时间 |
| 超人输入速度：瞬时转视角、每帧按键、精确点击 | 跳过界面层级、跳过交互前置条件 |
| — | 绕过玩家控制器直接调用游戏行为 |

一切行动都落到**玩家控制器**（视角 / 键盘 / 鼠标 / UI 点击），也就是"人能做但做得慢"的那条路径。

## 2. 与 CmdBridgeMod 的关系（mod 依赖，不做库合并）

- 注入是细活：写入必须在**下一帧帧首**、只能写输入层白名单字段、UI 点击是两帧动作、
  还要清 `WidgetInput.m_mouseDownPoint`、窗口失焦时游戏会丢弃全部输入。
  这些坑只应该有一份实现 —— 因此本 Mod **不自己写注入**，而是复用 CmdBridgeMod。
- CmdBridgeMod 对外新增了公开门面 `CmdBridgeMod.CmdBridgeInput`（`Mod/CmdBridgeMod/Server/CmdBridgeInput.cs`）：
  只暴露输入层动作（`Look`/`LookAt`/`KeyHold`/`KeyPulse`/`MouseAction`/`Wheel`/`UiClick`/`TypeText`/`ReleaseAll`），
  全部方法**不抛异常**，返回 `false` 表示本次没执行，可以直接用作状态机守卫。
  角度单位是**度**；本 Mod 内部用弧度，换算在 `CmdBridgeActuator` 里。
- 打包与加载：**两个 Mod 各自打包成独立 scmod 放进 `Mods/`**，由加载器各自加载（不做库合并）。
  `ModInfo.xml` 只声明依赖标识；`ModLoader` 会按依赖做拓扑排序，保证 CmdBridgeMod 先加载。
  csproj 里的 ProjectReference 用 `<Private>false</Private>`：本 Mod 的输出与包里**不带** CmdBridgeMod.dll。
- 依赖不可用（CmdBridgeMod 未安装 / `EnableInputInjection=false`）时，执行器 `IsReady=false`，
  状态机守卫不会产生动作，日志里会明确写出 `injector=UNAVAILABLE`。

## 3. 目录结构

```
PlayerAiMod/
├── Plug/PlayerAiMod.cs                IMod 入口：注册组件模板 + 帧末把 tick 排到下一帧帧首
├── Component/PlayerAiComponent.cs     挂在 Player 实体上的组件（装配/生命周期/接管条件）
├── Core/
│   ├── PlayerAiConfig.cs              参数：状态机上限、接管范围、启动行为树、包扫描间隔
│   ├── PlayerAiRuntime.cs             全局运行时：登记角色、帧首统一 tick、异常隔离、本端玩家判定
│   ├── PlayerAiBehaviour.cs           行为图（根状态机）与示例目标解析
│   ├── AiBlackboard.cs                状态间共享数据的类型化黑板
│   ├── AiMode.cs                      模式层枚举：未接管/待机/运行树/录制中/暂停/故障
│   ├── AiModeGraph.cs                 模式层转移表 + 状态→模式映射（可被自检直接核对）
│   ├── AiModeSelfTest.cs              模式层自检 33 项
│   ├── AiEventLog.cs                  观测日志：滚动 + 限量 + 写失败不影响游戏（P0-10）
│   ├── AiEventLogSelfTest.cs          观测日志自检 24 项
│   ├── PlayerAiBehaviour.cs           组装模式层状态机（只管模式，不管决策）
│   ├── IAiTreeHost.cs                 行为树宿主接口（真实实现是 AiActor；自检用假件）
│   ├── AiCommandSet.cs              ★ ai.* 命令集（纯语义，不依赖游戏）
│   ├── AiCommandRequest.cs            命令请求与 AiCommandException（带错误码）
│   ├── IAiCommandContext.cs           命令层需要的运行时门面
│   ├── AiCommandBridge.cs             把 ai.* 绑到 CmdBridgeMod 的扩展命令注册表
│   └── AiCommandSelfTest.cs           命令层自检 35 项
├── Fsm/
│   ├── AiStateMachine.cs              ★ 状态机内核（转移裁决、历史栈、冷却、驻留时间）
│   ├── AiState.cs / AiCompositeState.cs   状态基类 / 组合状态（内嵌子状态机，分层）
│   ├── AiTransition.cs                转移定义（触发 + 守卫 + 优先级 + 冷却 + 最短驻留）
│   ├── AiStateContext.cs              执行上下文（黑板 / 传感器 / 执行器 / Raise）
│   ├── AiStateMachineSnapshot.cs      快照（当前/上一个状态、历史栈、最近转移）
│   └── AiStateIds.cs                  状态与触发词汇表
├── Actor/
│   ├── AiActor.cs                     角色：绑定 ComponentPlayer，持有状态机与两层接口
│   ├── IAiSensor.cs / IAiActuator.cs  只读观察 / 玩家控制器动作（含 AiActorView）
│   ├── PlayerSensor.cs                默认传感器：位置/朝向/生命/姓名/按名找玩家
│   ├── CmdBridgeActuator.cs          ★ 真实执行器：转交 CmdBridgeMod 的注入门面
│   └── PlaceholderActuator.cs         占位执行器：依赖不可用时只记意图、不动玩家
├── States/                             模式层的状态（只管模式，不做决策）
│   ├── AiInactiveState.cs                    未接管（绝对安全态：进入即释放全部输入）
│   ├── AiIdleState.cs                        待机（已接管但没有活动行为树）
│   └── AiTreeState.cs                        运行行为树（模式层与决策层的接缝）
├── Bt/                                 ★ 行为树内核（对齐 UE，见 doc/player-ai-plan.md §3/§4）
│   ├── BtResult.cs / BtContext.cs             结果枚举 / 执行上下文（黑板+传感器+执行器+预算）
│   ├── BtNode.cs / BtComposites.cs            节点基类（装饰器套用/收尾）+ Root/Selector/Sequence/SimpleParallel
│   ├── BtTask.cs 见 BtTasks.cs                任务基类 + Wait/SetBlackboard/Log/Lambda
│   ├── BtActorTasks.cs                        角色任务：LookAt / MoveTo / WaitForTarget / Subtree / PlayActionPackage
│   ├── BtDecorator.cs / BtDecorators.cs       装饰器与服务基类 + Blackboard/Cooldown/TimeLimit/Loop/ForceSuccess/Inverter
│   ├── BtNodeSchema.cs                        节点形状与属性表（包格式/校验器/编辑器共用的元数据）
│   ├── BtNodeRegistry.cs                    ★ 类型注册表：工厂 + 形状 + 属性表（单一事实来源）
│   ├── BtRuntime.cs                           运行时：观察者中断、帧预算、异常隔离、自动重跑、快照、ReplaceRoot 迁移
│   ├── BtNodeState.cs                         运行态快照/恢复 + 迁移统计（热重载按「来源包/id」保留正在跑的任务）
│   ├── TreeLibrary.cs                       ★ 树库：预编译常驻 + 毫秒级切换（P0-11）
│   ├── BtSelfTest.cs / BtTestDoubles.cs       内核自检 63 项 + 共用的假传感器/假执行器
│   └── (TreeMutation 内存改写属 P0-12)
├── Record/                             ★ 动作包录制（P0-9 入口链路，帧流 P1）
│   ├── AiRecordingSession.cs          录制会话：状态机 + 名字消毒 + 元数据落盘（可自检）
│   ├── ScatPackage.cs                 .scatpak 打包：manifest / 起点关键帧 / 空事件轨
│   ├── AiRecordingUi.cs               游戏内命名框与覆盖询问（TextBoxDialog / MessageDialog）
│   └── AiRecordingSelfTest.cs         录制自检 31 项
└── Package/                            ★ 包格式 v1：读、校验、编译（见 §11）
    ├── PackageValue.cs / PackageJson.cs       自持 JSON 值模型 + 严格解析与字段读取器
    ├── PackageIssue.cs                        问题/严重级/报告 + 稳定错误码表
    ├── ScbtManifest.cs / ScbtTree.cs          manifest.json / tree.json 文档模型
    ├── PackageValidator.cs                    结构/语义校验（规则只有一份，读 BtNodeRegistry）
    ├── PackageRoots.cs                        包目录白名单：<实例根>/PlayerAi/BehaviorTrees（唯一目录）
    ├── PackageLoader.cs                       装载与嵌套引用解析（IPackageSource：磁盘/内存）
    ├── TreeCompiler.cs                        文档 → 可执行节点对象图（属性名→字段的唯一映射点）
    ├── TreeMutation.cs                        内存态改写：改参数/插删搬节点（只改内存，P0-12）
    ├── TreeWriter.cs                          反序列化 + 导出成新包（P0-13）
    ├── TreeEditSelfTest.cs                    改写/导出自检 43 项
    ├── PackageWriter.cs                       打包成 .scbtpak + 原子写（只用于出厂模板与导出）
    ├── PackageTemplates.cs                    出厂示例包 demo.greet.scbtpak + common.scbtpak 与安装
    ├── PackageReloader.cs                     推送式热重载：入队（任意线程）/ 应用（tick 边界）/ 预检 / 可选扫描
    └── PackageSelfTest.cs                     包、加载器、模板与热重载自检 100 项
```

## 3.1 包目录约定（行为树包放哪）

**只有一个目录**（2026-09-12 按用户要求把"随 Mod 分发"的第二目录并掉了）：

| 位置 | 用途 | 谁可以写 |
|---|---|---|
| `<实例根>/PlayerAi/BehaviorTrees/` | 树包 `.scbtpak` + 动作包 `.scatpak` 都在这里（**实例根 = 游戏 exe 所在目录**） | **编辑器**随便改；游戏侧只允许**新建**（重名要显式 `overwrite=true`），出厂模板补齐只创建不覆盖 |

- 导出 / 新建 / 另存为 / 修改**默认都落在这个目录**；以前那个
  `<实例根>/Mods/PlayerAiMod/PlayerAi/BehaviorTrees/` 不再被读取，也不再被写入。
- `references` 里的路径必须是相对路径，且解析后仍落在这个目录内（防路径穿越）。
- 目录里只扫**顶层**：子目录（例如 `Logs/`）不参与索引。
- 边界：**AI 不允许覆盖磁盘上的包**（游戏内状态铁律）；`ai.tree.export` / `ai.record.save`
  默认拒绝重名，只有显式传 `overwrite=true` 才允许覆盖。

## 4. 状态机能力（"复杂状态切换"）

- **分层**：`AiCompositeState` 内部挂子状态机（例如 Root → Combat → {Approach, Attack, Retreat}），
  子机 `Raise` 只作用于自己，需要交回父机时用 `context.Machine.Raise(...)` 或 `OnChildTick` 返回触发名。
- **打断与回退**：`PushState` / `PopState` 维护状态栈；转移目标支持 `"<previous>"`（回到上一个状态）
  与 `"<pop>"`（弹出返回地址）。
- **多条竞争转移**：同一触发可有多条候选，按 `Priority` 排序、用 `Guard` 逐条判定；
  `MinDwellSeconds`（最短驻留：>0 指定 / 0 用全局下限 / <0 显式关闭）与 `CooldownSeconds`（本条冷却）防抖。
- **全局面转移**：`From = AiTransition.AnyState`，例如"任何状态下失败就回未接管"。
- **条件式转移**：`Trigger = null` 表示只看守卫，每帧评估一次（至多一条）。
- **确定性**：转移只在 tick 边界裁决（`Raise` 只入队），单帧转移数有上限；
  触发若因守卫/驻留暂时不可用会被丢弃 —— 需要重试就每帧 Raise，或写成条件式转移。
- **健壮性**：状态抛异常时记录并回退到安全状态，或把状态机置为 faulted 停机（`Reset()` 恢复）；
  `Validate()` 启动自检未知/不可达状态。
- **可观测**：`Snapshot()` 给出一致视图，`DescribeGraph()` 打印状态图。

## 5. 帧时序（为什么在帧首 tick）

```
Window: BeforeFrameAll() → Dispatcher.BeforeFrame() → Keyboard/Mouse.BeforeFrame() → 帧体 → AfterFrameAll()
Keyboard/Mouse 的 downOnce 数组在帧末被清空
```

插件在帧末的 `Frame.Update` 里 `Dispatcher.Dispatch`，把 AI 的 tick 排到**下一帧帧首**执行，
位置早于输入设备读取与整个帧体 —— 与真人输入事件语义一致。

`// Source: Engine/Engine/Window.cs:464 BeforeFrameAll / Engine/Engine/Dispatcher.cs`
`// Source: Survivalcraft/Game/Program.cs:147 TriggerEvent("Frame.Update")`

## 6. 接管范围：本阶段只接管本端角色

- `PlayerAiConfig.OnlyLocalPlayer = true`（默认）：只接管**本端玩家**（其 `GameWidget` 注册在
  `SubsystemGameWidgets` 里的那个）。
- **远端角色不在主机端直接操作**：远端操作要远端执行、本端只做复现，在主机端直接改会两边不同步。
  远端支持是后续独立议题（远端执行 + 本端复现，或走联机 Mod 的权威通道）。
- 装配阶段就不给"不打算接管的玩家"建 `AiActor` —— 执行器共享同一个注入器，
  多一个不干活的 Actor 去 `ReleaseAll` 会把真正干活那个 Actor 按住的键放掉。

## 7. 模式层与「示例行为在包里」（P0-8）

**代码只表达模式，行为全在包里。** 状态机现在只回答「现在该不该动、谁来动」：

```
Inactive --start--> Idle --(有树)--> Tree --(树卸下)--> Idle
        \--stop/failed/release（全局兜底，任何模式）--------> Inactive
                  Tree --(树 tick 出错)--------------------> Inactive
        Inactive --(已装树且无故障)------------------------> Tree
```

- 模式由**事实推导**：`HasTree`（运行时的树标识）、`Tree.LastError`。
  所以控制面直接往运行时塞一棵树，下一帧就会进入「运行树」模式 —— 不需要谁记得发事件。
- 故障 → 未接管：进入未接管会释放全部输入，并**停掉那棵树**（否则没人 tick 的树会一直按着键）。
- 接管态却停在未接管（首帧顺序、世界重载、故障恢复）会**自愈**：补一次接管请求。
- 模式转移**不做最短驻留**（`MinDwellSeconds = -1`）：防抖是给决策用的，
  给事实加 0.15 秒延迟只会让 `ai.status` 撒谎、让「保存后立刻生效」变成「过一会儿才变」。
- FSM 只依赖 `IAiTreeHost` 接口（不认识 `AiActor`/游戏类型），所以模式层能在无游戏环境下自检。

**示例行为**（原来写在 `DemoLookState`/`DemoApproachState` 里）现在等于出厂包
`demo.greet.scbtpak`：服务 `UpdateNearestPlayer(nameFilter=basil)` + 装饰器抢占 +
`Task.Subtree(common#greet.look)` + `Task.MoveTo(acceptableRadius=3, timeout=25)`。
改目标名、改距离、改超时**都在包里改**；代码里不再有任何示例决策参数。

- 关闭方式：`PlayerAiConfig.AutoEnableLocalPlayer=false`（不接管）或
  `PlayerAiConfig.AutoLoadTreeOnStart=false`（接管但不装树 → 停在待机）。
- 临时想停 AI：`Home` 热键或 `sccmd ai pause`；想彻底放手用 `sccmd ai disable`。

## 8. 装配方式

`Plug/PlayerAiMod.cs` 在 `GameDatabase.GameDatabase` 事件里注册组件模板
（`ComponentTemplate` + `Class = PlayerAiMod.PlayerAiComponent` + 挂在 `Player` 实体模板下的
`MemberComponentTemplate`），写法与 `Mod/WatchMod/Plug/WatchMod.cs` 一致。

| 用途 | GUID |
|---|---|
| ComponentTemplate | `98EC6C61-A8A4-4365-A02C-2C4DB2580DF7` |
| Parameter `Class` | `E23D1309-174F-485E-AEE9-798F8397CC52` |
| MemberComponentTemplate（Player 下） | `6BA88EDF-CE95-4F46-9081-87D9C588B827` |

## 9. 构建与打包

```powershell
# 构建（两个 Mod 都要重新构建；会各自跑 Obfuscar）
dotnet build Mod/CmdBridgeMod/CmdBridgeMod.csproj -c Debug
dotnet build Mod/PlayerAiMod/PlayerAiMod.csproj -c Debug
```

- 打包遵循仓库 `.scmod` 规范：`IsMergeLib=true`、ZIP 根目录放 `ModInfo.xml`、程序集放扁平 `Lib/`、
  文件名加 `[SuAPI]` 前缀，用 Python `zipfile`（不能用 `Compress-Archive`，反斜杠路径会导致加载失败）。
- 两个 scmod 各自独立放进 `Mods/`，加载器按依赖排序后各自加载。
- `PlayerAiMod` 的包里**不要**放 `CmdBridgeMod.dll`（依赖声明已经表达了这个关系）。

## 9.1 控制面：怎么操作 AI（`sccmd ai ...`）

命令通过 **CmdBridgeMod 的扩展命令注册表**分发（一套通道、一个 token、一个 CLI）：
PlayerAiMod 在 `OnLoad` 注册 13 条 `ai.*`/`bt.selftest`，卸载时按 owner 整批摘除。
命令默认在**游戏线程**执行（触碰游戏对象必须如此），错误以稳定错误码返回。

```bash
sccmd ai status          # 模式/暂停/树来源与哈希/活动节点路径/黑板/重载统计
sccmd ai trees           # 包目录里的树（标出 active）
sccmd ai load demo.greet # 装载/切换活动树（立即生效）
sccmd ai pause / resume  # 暂停/继续（保留运行态；暂停即释放 AI 输入）
sccmd ai bb              # 列全部黑板值；ai bb target / ai bb mood happy string
sccmd ai validate demo.greet
sccmd ai prepare demo.greet       # 预编译进常驻树库（把编译成本挪到切换之前）
sccmd ai switch demo.greet        # 毫秒级切换（报告 switchMs）
sccmd ai notify <包路径> [hash]   # 模拟编辑器推送（哈希不符会被忽略）
sccmd ai reload          # 手动兜底重载（排队，tick 边界生效）
sccmd ai record start|pause|stop|save|discard   # 录制（= PgUp/PgDn）
sccmd ai set move acceptableRadius 6    # 改活树参数（只改内存）
sccmd ai insert greet '{"id":"w9","type":"Task.Wait"}'
sccmd ai remove w9 / ai move w9 sel     # 摘掉 / 搬移
sccmd ai export my_variant              # 把含改动的活树导出成新包（原包不动）
sccmd ai snapshot        # 活动节点快照（编辑器实时监视复用）
sccmd ai logs 30         # 事件日志最近 30 条（文件：PlayerAi/Logs/PlayerAi.log）
sccmd aiselftest         # 七套自检一次跑完
sccmd commands           # 内建命令 + 各 Mod 注册的扩展命令
```

- **热重载的生效时机**：`notify`/`reload` 只是**排队**，真正的替换发生在帧首 tick 边界
  （`PlayerAiRuntime.ApplyPendingReloadsNow`），绝不在节点执行中途换树；`ai.tree.load` 是显式切换，立即生效。
- **内存态改写（P0-12）**：`ai set/insert/remove/move` 只改**内存里那棵活树** ——
  `.scbtpak` 的字节整局游戏不变（自检里拿哈希比对），也不产生任何隐式落盘。
  想固化只有一条路：`ai export <新包名>` 导出成**新包**，由人决定要不要替换原包。
  改错了随时 `ai reload` 回退（原包就是那条永远可回退的基线）。
  未知参数名、插到非组合节点下、把节点挪进自己子树、删空组合（需 `force`）都会被拒绝并说明原因。
- **毫秒级切换（P0-11）**：`ai prepare <包>` 先把「读包 + 校验 + 编译」付掉，之后 `ai switch <包>`
  只做「比对哈希 → 换根指针 → 迁移运行态」——本机实测 **0.01~0.02 ms**，而同一棵包现场编译约 5~6 ms
  （差两个数量级）。切换后热重载仍认得这棵新树为活动树（`PackageReloader.Adopt`），编辑器照常
  `ai notify` 继续改；常驻副本所在文件被改过 → 自动重编译（`staleRecompiles` 计数）。
- **失败不动旧树**：半截文件、校验失败、编译失败都只记录原因（`ai.status.reloads.lastIssues`），旧树继续跑。
- `Home` 与 `ai.pause` 等价（都走同一个暂停开关）。

## 9.2 录制：`PgUp` / `PgDn`（P0-9）

| 按键 / 命令 | 行为 |
|---|---|
| `PgUp`（`ai record start`） | 没在录 → **开始录制**；录制中 → 暂停/继续切换；已停止待命名 → 再弹一次命名框 |
| `PgDn`（`ai record stop`） | **结束录制** → 弹命名框 → 保存成 `<名字>.scatpak` |
| `ai record save <名字> [--overwrite]` | 把"已停止但还没命名"的录制落盘（**取消命名不会丢**） |
| `ai record discard` | 丢弃待保存的录制 |

- **录制中 AI 停手**：进入录制模式会释放 AI 按住的全部输入，并且**不 tick 行为树** ——
  但树的运行态保留，录完回到运行树模式时从原处继续（`AiRecordingState`）。
  录的永远是"人的操作"，不会混进 AI 的动作。
- **产物位置**：`<实例根>/PlayerAi/BehaviorTrees/<名字>.scatpak`（与树包同路径，计划 §4.4），
  **原子写**（`.tmp` → 替换），不会留下半截文件。
- **同名不静默覆盖**：撞名会弹「覆盖 / 取消」；取消后录制仍留在待命名状态，可换个名字另存
  （对应决策 D3：询问覆盖 + 另存为入口）。
- **名字消毒**：只留 `[A-Za-z0-9_.-]`，最长 64 字符，点号开头/结尾会被去掉 —— 录制名字会变成文件名。
- **P1 起：包里有真帧流**。录制时帧末采一帧"原始输入"（键盘 held/downOnce、鼠标键、滚轮、视角增量），
  写进 `tracks/input.bin`（自描述：文件头带键名表）；同时每 0.5 s 记一条关键帧（位置/朝向），
  快捷栏切换/鼠标按下/开关切换/滚轮记成 `tracks/events.json` 里的稀疏事件。
- **能不能回放一眼可查**：`sccmd ai action validate <名字>` 会明确给出 `replayable`；
  用 P0 老版本录的包只有元数据（`tracks/input.bin` 不存在），校验器会如实说"不可回放"，而不是回放时才发现。
- Android / 无 `PgUp`/`PgDn` 的设备：用上面的等价命令（`sccmd ai record ...`）。

## 9.3 实机验收（一条命令）

```bash
py -3 Mod/Packages/pack_player_ai.py --deploy   # 打包 [SuAPI]CmdBridgeMod + [SuAPI]PlayerAiMod 并部署到 Mods/
# 启动游戏 → 进入一个世界（AI 会自动装载 demo.greet 并接管本端角色）
py -3 Mod/Packages/enter_world.py Rebritish     # 也可以让脚本自己从主菜单点进某个世界（走玩家控制器）
py -3 Mod/Packages/verify_player_ai.py          # 一键跑完可自动判断的验收项
py -3 Mod/Packages/verify_player_ai.py --skip-probes   # 不跑 CM-1/CM-2 探针（它们会动真实鼠标/焦点）
py -3 Mod/Packages/smoke_bridge_commands.py     # 27 条只读命令扫一遍 + 确认游戏没被带走
py -3 Mod/Packages/check_record_replay_live.py  # 录制→解码轨道→回放，证明"录得下来也放得出来"
py -3 Mod/Packages/check_action_replay_live.py  # 动作包直接回放 + 行为树里回放，都要真的走动角色
py -3 Mod/Packages/scat_dump.py <包.scatpak> --summary   # 把逐帧轨道解码开看（held/pressed/时长）
py -3 Mod/Packages/smoke_bridge_commands.py     # 只读命令冒烟（曾经有一条命令能把游戏进程干掉）
```

验收脚本会依次检查并在末尾汇总：连通性与 `ai.*` 命令是否齐全、七套自检（`bt.selftest`）、
模式/树/快照、**毫秒级切换**（断言 `switchMs < 一帧`）、**改内存后原 `.scbtpak` 字节不变 + `ai reload` 回退**、
导出成新包、录制落盘、事件日志留痕、暂停/恢复；随后调用 CM-1/CM-2 探针，
最后打印需要人眼确认的清单（`Home` / `PgUp` / `PgDn` 与命名框）。

- 想临时让 AI 停手：`sccmd ai pause`（保留运行态）或 `sccmd ai disable`（释放全部输入）；
  彻底不自动跑树：把 `PlayerAiConfig.AutoLoadTreeOnStart` 改成 `false`（需重新编译）。

## 9.4 回放：动作包 → 行为树（P1）

```bash
sccmd ai action list                    # 列出动作包（时长/帧数/能否回放）
sccmd ai action validate sample_walk    # 校验一个包（结构 + 能不能回放 + 用了哪些键）
sccmd ai action play sample_walk        # 直接回放（不经行为树，给人测试用）
sccmd ai action stop                    # 停止并释放输入
sccmd ai action status                  # 回放进度/漂移
```

行为树里用 `Task.PlayActionPackage` 挂动作包（出厂示例 `test.action.scbtpak` 就是它）：

```json
{ "id": "play", "type": "Task.PlayActionPackage",
  "properties": { "packages": ["sample_walk"], "mode": "Sequence",
                  "repeat": 1, "abortOnFail": true } }
```

- **包名按"与树包同路径"解析**（计划 §4.4）：把 `.scatpak` 和 `.scbtpak` 放**同一个目录**
  （`<实例根>/PlayerAi/BehaviorTrees/`）即可，出厂示例也装在这里。
- **回放 = 同一条原始输入通道**：按住类保持、按下类只按一次、鼠标/滚轮/视角增量按录制还原；
  录 60 FPS 放 144 FPS 也不会快放（每帧带自己的时长）。
- **漂移即失败**：位置偏离关键帧超过 `ReplayDriftToleranceMeters`（默认 3 m）或朝向超过 45°
  → 节点判失败，交回行为树（可重试/换分支），**不做瞬移类修正**。
  注意关键帧存的是**绝对世界坐标**（"在哪录的就在哪放"），所以示例包只带起点一条关键帧。
- **出厂示例**（随 Mod 安装）：
  - `sample_walk.scatpak`：走 1 s → 边走边左转 0.4 s → 走+跳 0.2 s → 侧移 0.2 s → 停 0.2 s（2.000 s / 120 帧，数据完全确定）；
  - `test.action.scbtpak`：`Sequence[ 日志 → 播放 sample_walk → 等 0.5 s ]`，是"行为树 + 动作包"的最小可跑样例
    （`sccmd ai switch test.action` 即可切过去看）。
- **自己造一个包**（不用真机录制）：编辑器里点「自造」，或
  `POST /api/action/create {"name":"my_action"}` —— 写进实例目录的 `my_action.scatpak`，
  内容与 `sample_walk` 同构（确定数据，可直接当基准）。

### 9.4.1 菜单操作也能录进动作包（「进入游戏」）

菜单点击走的是引擎**软光标**（CM-1），**原始输入层里没有痕迹**，所以它单独走一条
**语义事件通道**：注入器每做一次 UI 动作就记一条，录制端写成 `kind=ui.click` 的事件
（`tracks/events.json`，与逐帧轨道同一个时间轴）；回放时 `ScatPlayer` 按时间点重放同一套点击，
注入走 CM-1 会话（一步一帧、不动物理鼠标）。主菜单里没有角色，
`ai.action.play` 会退化成"只做 UI 点击"的兜底执行器，所以这种包在**没有世界**时也能放。

出厂示例：**「进入游戏」** —— 从主菜单点 `Play` → 选世界行 → 点 `Play!`，把游戏带进地图：

```bash
# 录（要求当前在主菜单）：点 Play → 选世界 → Play! → 世界加载 → 存成 进入游戏.scatpak
py -3 Mod/Packages/record_enter_game.py record 进入游戏 --world Rebritish
# 放（同样从主菜单开始）：整个流程会自己重演一遍
py -3 Mod/Packages/record_enter_game.py replay 进入游戏
```

命名规则：文件名与 `manifest.name` 可以是中文（用户要的「进入游戏」），
`manifest.id` 仍是机器键（只允许 `[A-Za-z0-9._-]`，全中文名时退回 `action_<时间戳>`）。


## 9.5 编辑器：`PlayerAiEditor.exe`（P2 MVP）

行为树的低代码编辑器（物料区 / 画布 / 属性区），**零运行期依赖**：页面全部内嵌在 exe 里，
不需要 node、不需要联网、不需要管理员权限。

```bash
# 开发期直接跑
dotnet run --project Mod/PlayerAiEditor -c Release -- --root publish/Windows
# 或者发布成单文件 exe（5.3 MB）——**装进游戏实例根里面**：<实例根>/PlayerAi/editor/
dotnet publish Mod/PlayerAiEditor/PlayerAiEditor.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true -o publish/Windows/PlayerAi/editor
publish/Windows/PlayerAi/editor/PlayerAiEditor.exe            # 不用 --root，自己就在游戏目录里
publish/Windows/PlayerAi/editor/PlayerAiEditor.exe --print-root        # 只看它判定的实例根
publish/Windows/PlayerAi/editor/PlayerAiEditor.exe --selftest          # 无头自检 86 项
```

- **物料区**读游戏的节点注册表（`GET /api/schema`）：类型、形状（组合/任务）、属性表（类型/默认值/枚举）
  全部来自 `BtNodeRegistry`，所以**编辑器里能选的，游戏里一定能跑**；代码专用节点（lambda）不在物料里。
- **动作包也是物料**（`GET /api/actions`）：物料区最后一组列出包目录里的 `.scatpak`
  （时长/帧数/**能不能回放**）；点一下就挂一个 `Task.PlayActionPackage`，
  或加进已选中播放节点的 `packages`（属性区里是**勾选列表**，不是让人手打文件名）。
- **画布**改的就是包格式本身（`tree.json` 的 children/decorators/services/properties）。
- **保存**先跑**游戏内同一份校验器**，通过才原子写；包目录可写，覆盖需要显式确认，
  写后还会自己重新装载一遍确认游戏读得动。
- **推送热重载**：保存后点一下，编辑器直接通过 CmdBridge 通道给**正在运行的游戏**发
  `ai.tree.notify`（带哈希）—— 不重启、不重载世界。
- **启动 / 结束游戏**（§9.5.21）：人不用去文件管理器双击游戏了 —— 编辑器知道实例根在哪，
  点「启动游戏」就行；起来之后它会自动等控制通道，连上再干活。
- **播放 / 暂停 / 停止一棵树**（§9.5.22 / §9.5.24）：工具条上一个按钮 + 一个「树：…」状态徽标 ——
  点「▶ 播放这棵树」= 保存改动 → 让游戏切到这棵树并开始跑；正在跑这棵树时它变成
  「⏸ 暂停」/「▶ 继续」/「⟳ 推送改动」；旁边的「⏹ 停止」= 卸下树（**重置**，再播放就从根重跑）。
  再也不用猜"游戏里到底跑没跑、跑的是哪棵"；画布上**只有正在执行的那一个节点**亮绿框。
- **动作包那一组按钮**：`校验`（结构 + 能否回放）/ `试跑`（`ai.action.play`，**只回放下拉框里选中
  的那一个包**；试跑前会自动暂停行为树，免得两边抢输入）/ `停止`（`ai.action.stop`，释放输入）/
  `自造`（写一个新的示例包进包目录）。
  控制通道的报错分三类：`game_unreachable`（没连上）、`game_not_ready`（连上了但还没进世界 /
  AI 没接管 —— 这是**过渡状态**，好了会自动恢复）、`game_refused`（游戏明确拒绝，例如这份 Mod
  里还没有这个命令）。

### 9.5.1 编辑器这一轮的体验补齐（撤销/拖拽/跳转/新建）

| 能力 | 说明 |
|---|---|
| **撤销 / 重做** | 快照整份 `{manifest, tree}` 压栈（比"记录逆操作"可靠），连续同类操作 800ms 内合并成一步；按钮 + `Ctrl+Z` / `Ctrl+Y`（`Ctrl+Shift+Z` 也行），换包时清栈 |
| **拖拽** | 物料区的节点/装饰器/服务/动作包都能拖到画布：落点**中间 1/2 = 挂成子节点**，**上/下 1/4 = 插成兄弟**；节点本身也能拖着重排；拖进自己的子树会被拒绝（否则树会断开） |
| **问题跳转** | 校验信息里的落点（`@tree.json#root.children[0]#seq…#wait`）会被解析回节点 id，点一下选中并滚到可见；画布上同时给该节点打红/黄标记 |
| **子树复制/剪切/粘贴/重复/删除** | `Ctrl+C/X/V/D` 与属性区按钮；粘贴**重新分配所有 id**（节点/装饰器/服务）避免同包重名；剪贴板只在内存里（刷新即丢，编辑器不往磁盘偷记东西）；粘贴进来的 `Task.Subtree` 如果引用了**当前包没声明**的包会当场提示（`manifest.references` 里要先写它，否则游戏解析不到） |
| **节点搜索** | 画布上方搜索框按 id/类型/显示名过滤高亮，回车跳到第一个命中（`Esc` 清空） |
| **快捷键** | `Ctrl+Z/Y` 撤销重做、`Ctrl+C/X/V` 复制剪切粘贴、`Ctrl+D` 重复、`Delete` 删除、`F2` 改 id（焦点丢进属性区输入框）、`F3` 跳到搜索框 |
| **嵌套包引用选择器** | `Task.Subtree` 的 `package` 属性给的是**本包 `manifest.references` 已声明引用**的下拉（而不是让人手打一个不存在的 id）；当前值不在清单里时会标成「未声明！游戏解析不到」 |
| **两种画布视图** | `视图：缩进树 / 节点图` 一键切换，同一份布局数据两种画法：节点图是 SVG 连线 + 绝对定位方框，`－/＋` 缩放（0.6~1.6）。几何是**纯函数** `graphGeometry(layout, scale)`：x 随深度、叶子按槽位堆叠、**父节点摆在子节点中点**（不是斜楼梯），线从父底边中点到子顶边中点画贝塞尔。拖拽/点选/双击在两种视图里语义一致 |
| **导出选中子树为独立包** | 属性区 `导出为包`：`manifest.entry` = **这棵子树自己的根**（不是原包入口），只带上它**真的用到**的引用，黑板一并继承；落盘走与保存完全相同的校验+原子写路径，写完直接打开新包 |
| **入口节点选择器** | `Task.Subtree` 的 `#节点id` 那段不用手打了：属性区会（懒加载、按引用缓存）列出**被引用包里的节点**（id + 类型）供选，读不到时仍可手填 |
| **多选与批量操作** | `Ctrl+点击` 加/减选、`Shift+点击` 从锚点按**画布顺序**整段选；选中多个时属性区出现 `批量删除` / `包进 Sequence` / `包进 Selector` / `清空选择`。批量删除是**一步撤销**，且"父子同时选中"只删父（不会删完父再删子而扑空）；包进组合要求它们**同属一个父节点**，否则如实拒绝 |
| **新建空白树** | 一键生成 `Root → Sequence → Task.Wait`（校验器要求组合节点至少有一个子节点，所以不能"光一个 Root"），走的是与"保存"完全相同的校验+原子写路径 |
| **前端无头自检** | `node Mod/Packages/editor_web_selftest.js` —— 用 DOM 桩把 `app.js` 真跑起来，验证撤销/重做语义、落点判定与效果、问题定位、新建树的 payload、活动路径→高亮，以及**接线级**的"按下→移动→松手"与"窗口变小→重新适应"（**198/198**）；C# 侧 `--selftest` 验服务端（**86/86**） |

### 9.5.2 实时监视（P3）：在编辑器里看游戏正在跑什么

属性区下面多了一块「实时监视」：点 `▶ 实时监视` 后每 700ms 拉一次
`GET /api/game/live`（一次往返拿到 `ai.status` + `ai.tree.snapshot` + `ai.blackboard` 三份**同一时刻**的数据），
把游戏里正在执行的**活动节点路径**在画布上点亮 —— 路径上的节点标「路径上」，
最末端那个标「▶ 正在执行」；黑板键值列在下面；`暂停树` / `继续树` 直接遥控游戏侧
（`POST /api/game/pause|resume`）。地址对不上时（编辑器打开的不是游戏在跑的那棵树）
面板会直接说明，而不是默默不高亮。

```bash
py -3 Mod/Packages/check_live_monitor.py   # 实机验证：数据能拉到、id 能对上包、遥控暂停/继续生效
```

### 9.5.3 嵌套包：`Task.Subtree` 就地展开 / 双击进入

`Task.Subtree` 的 `properties.package` 写的是**归属包 `manifest.references` 里的引用 id**
（可带 `#节点id`）。编辑器的解析**复用游戏内同一套规则**（`PackageLoader` 的引用闭包 +
`LoadedPackage.FindReference`，与 `TreeCompiler.ResolveSubtree` 完全一致），所以"编辑器里展开出来的"
和"游戏里真的会跑"的是同一个东西：

- 画布上该节点显示 `↳ common#greet.look`，旁边一个 `▸ 展开引用`；
- 点展开 → `GET /api/subtree?path=&node=` → 把**被引用包的那棵子树就地、只读地**展开在下面，
  并标出"引用 common#greet.look（2 节点，只读）"；
- **双击**该节点 → 直接打开那个包来改（不在这里改：改的会写进当前包，等于改错文件）；
- 引用解析不出来时（`manifest.references` 里没写、或被引用的包里没有那个节点），
  面板会说清是哪一步断的，并列出该包的引用表与可用节点 id。

```bash
# 实机：demo.greet 的 sub_look 引用 common#greet.look
curl "http://127.0.0.1:8760/api/subtree?path=<demo.greet.scbtpak>&node=sub_look"
# → {"ok":true,"resolvedId":"common","entry":"greet.look","nodes":2,"references":["common → common.scbtpak"]}
```


- 前端渲染层是可替换的：`Web/engine-adapter.js` 是 lowcode-engine 的挂载点（D15 spike 结论见
  `doc/player-ai-plan.md` §11：真实包名是 `@alilc/lowcode-engine`，6.17 MB、只有 CJS/ESM、
  ext 包要 React16 + @alifd/next，且与我们包格式不同构 → 本期自研）。

### 9.5.4 真浏览器自检：/selftest.html + 无头 Chrome

前面两条反馈（拖不动、缩小窗口不适应）反复修不干净的根因之一是**验证层次不够**：
node + DOM 桩能证明逻辑，证明不了真实浏览器的布局、命中测试和 CSS 媒体查询。现在补上这一层：

```bash
py -3 Mod/Packages/check_editor_browser.py           # 跑一遍真浏览器自检（38 项）
py -3 Mod/Packages/check_editor_browser.py --shots   # 顺带在 1600×1000 / 1180×820 / 880×640 截图
py -3 Mod/Packages/check_player_ai_build.py browser   # 等价入口（all 里也会跑）
```

- 编辑器多了一个路由 `/selftest.html`：用**同源 iframe** 加载真正的 `/`，所以测的就是用户看到的那个页面；
- 用真实 `MouseEvent` 走 `mousedown → mousemove → mouseup`，落点判定交给真实的 `document.elementFromPoint`，
  再用真实布局数字（缩进像素、状态栏文案、诊断行）断言"节点真的被搬走了"；
- 把 iframe 改成 1400×900 / 1000×700 / 820×560，断言"画布宽度跟着收窄、页面没有被切掉、
  ≤940px 属性栏挪到下面、缩放被重算、放大回去数值复原"；
- 这一步不需要浏览器以外的依赖：本机有 Chrome 或 Edge 就跑，没有就整步跳过（不算失败）。

这一层上来就抓到三个只在真浏览器里才暴露的问题，全都不是"看源码"能发现的：

| 问题 | 实测现象 | 处理 |
|------|----------|------|
| 节点图的滚动区**按内容撑高**自己 | "适应窗口"读到的"可用高度"其实是它自己上次算出来的高度（自反馈），缩放数在 104%/108% 之间乱跳；图比画布小时下面那片区域连滚动条都没有 | `.graph-scroller { height: 100% }`：铺满画布栏，可用尺寸从此稳定可复现（120% / 100% / 50% 三档各就各位） |
| 重画 → 观察者回调 → 再重画的自反馈环 | 每 150ms 自动"适应"一次，状态栏一直被刷成"已适应窗口"，把"已移动节点 …"顶掉；拖拽途中还会把鼠标下面的元素换掉 | 只在缩放**真的变了**（或用户点了"适应窗口"）时才重画并报状态；切视图用 `fitGraphToWindow(true)` 强制画一次 |
| 画布区高度被文字换行吃掉 | 900px 高的窗口里画布只剩 260px、640px 高的窗口里只剩 102px —— 这就是"只有全屏才完整"的真身 | 表头提示行与状态栏统一压成**一行 + 省略号**（完整内容进 `title`）；窄而矮的窗口把画布/属性栏比例改成 3:1；画布栏高度从 102 → 324 |

### 9.5.5 拖拽不生效 / 缩小窗口不适应：前三条原因（走弯路的那一段）

用户两次反馈"编辑区域拖不动节点图"和"窗口缩小后没有重新自适应、只有全屏才正确"。结论是**三个**
独立原因叠在一起，所以修了三次都像没修（真浏览器那一层见上一条 §9.5.4）：

| 原因 | 现象 | 处理 |
|------|------|------|
| 前端资源没进新构建 | 改的是磁盘上的 `app.js`，`<实例根>/PlayerAi/editor/PlayerAiEditor.exe` 里嵌的还是旧前端（`dotnet` 增量编译不跟踪 `Web/**` 这类内嵌资源） | 新增 `check_player_ai_build.py assets`：把内嵌资源与 `Web/` 源码**逐字节**比对，并确认 `index.html` 的构建时间戳已替换；改了前端必须先 `build` 再 `assets` |
| 网格行高按内容撑 | `main` 是 grid 而行是 `auto`，窗口一矮/树一长，行就顶破容器底部，侧栏与画布都不滚动，多出来的部分被视口切掉 | `grid-template-rows: minmax(0, 1fr)` 把行钉在可用高度上；≤1200px / ≤940px 两档断点重排三栏 |
| 拖拽依赖 HTML5 DnD | 在嵌入式 webview 里 `dragstart` 常常根本不派发 | 改成 `mousedown/mousemove/mouseup` + `elementFromPoint` + `[data-node-id]`，落点效果只有一处 `applyDrop` |

同时补上"不该只依赖一种输入方式"的三条等价路径：**鼠标拖方框 / 属性区「移动」两步走 / Alt+方向键**
（`Alt+↑↓` 同层上下挪，`Alt+→` 降级到前一个组合兄弟下，`Alt+←` 升级）。拖动中还加了可见反馈
（拖动源半透明、目标高亮、状态栏"松手后落到 X（挂进去当子节点 / 插到它后面当兄弟）"），
以及 `Esc` 取消拖动、在窗口外松手（收不到 `mouseup`）时按 `buttons === 0` 结算。

这两个 bug 都不是靠"看源码"能发现的，所以自检也补了两层：

- **接线自检（`editor_web_selftest.js` §21）**：DOM 桩会**记录监听器**，并且支持选择器匹配、
  `className`↔`classList` 同步、变参 `classList.add/remove`、`innerHTML` 清空、可控定时器与
  `ResizeObserver`。于是自检可以顺着记下的监听器真走一遍 `mousedown → mousemove → mouseup`，
  证明**接线本身**是通的（不是"函数存在"），并断言"窗口变小后缩放真的被重算"。
  历史上本项目出过"函数写好了但没接到主循环"的事故，这类断言就是为它准备的。
- 这条更高的保真度当场又抓出两个真 bug：拖动时的高亮与"松手后真正的落点"规则不一致
  （落在组合节点上时高亮说"插到后面"、实际却挂成子节点），以及
  `clearDropHighlight` 只清缩进树的行、**节点图里被经过的方框会一直亮着**（同时亮好几个，
  看着就像拖拽坏了）。

### 9.5.6 节点图：做成 UE / Shader Graph 那种"可连线"的节点编辑器

原来的"节点图"其实只是把缩进树横过来摆：方框之间是折线，没有引脚、也不能连。现在的画法参照
UE 材质编辑器 / Unity Shader Graph / Blender Node Editor 这一类（跨软件通用叫法：**Node Graph /
可视化脚本**）：

- **深色网格背景**（两层 CSS 渐变叠出细格 + 粗格，比画几百条 SVG 线便宜得多）；
- **方框 = 彩色标题栏 + 引脚行**：标题栏按类别配色（根=棕、组合=蓝、任务=绿），
  方框上方挂一个**节点 id 标签**，方框里第一行是 id、下面每个子节点一行（`1 · wait_1`）；
- **引脚**：左边一个**输入脚**（接父节点），右边每个子节点一个**输出脚**；
  引脚坐标由几何层统一算出，连线和圆圈共用同一份数字（差几个像素就会"看着没接上"）；
- **连线是 S 形贝塞尔**（水平出入，就是 UE 那种手感），线下面压一条透明的粗线专职接鼠标
  （否则细线根本点不中）；
- **引脚可拖动连线**：从输出脚拖到某个节点上 = 那个节点成为本节点的子节点；
  从**输入脚**拖到某个节点上 = 本节点改挂到它下面。拖动中画一条跟随鼠标的虚线，
  目标方框**能接的亮绿、接不了的亮红并说明原因**；
- **连线可选中可断开**：点中连线（`data-wire="父>子"`）后按 `Delete`，
  子节点**提升为父节点的兄弟** —— 不删节点，因为行为树里每个节点都必须有归宿，
  提升是可逆的（Ctrl+Z）。父节点是根时无处可提，会直接说清楚并提示改用 `Delete` 删节点。

连线合法性（`connectBlocker`，纯函数）逐条给出原因，拒绝时状态栏写明白：
不能连自己、根不能当子节点、任务节点挂不下子节点、不能挂到自己的后代下面（成环）、已经是它的子节点。

缩进树视图**保留并存**（长树还是缩进树好读），两个视图共用同一份数据与全部交互语义。

验证：`editor_web_selftest.js` §11（引脚/连线几何：方框高度按输出脚数量增长、连线两端正好落在引脚上、
贝塞尔水平出入、缩放后引脚跟着走）、§22（连接/断线/拒绝原因/撤销步数/桩里走一遍拉线），
以及真浏览器自检里的 ⑤⑥ 两节（真鼠标从引脚拖到节点上真的改变了缩进、点中连线按 Delete 真的断开）。

### 9.5.7 节点图 = 自由画布（UE 式拖动 / 框选 / 预览区 / 小地图）

用户反馈"节点左键拖动没法任意移动"。原因：上一版把"拖动"定义成**改父子关系**
（拖到谁身上就挂给谁），于是节点永远跳回自动布局算出来的位置，看起来就是"拖不动"。
现在按 UE 的做法把两件事拆开：

| 操作 | 行为 |
|------|------|
| **左键拖动节点** | **任意摆放**（自由布局）。位移按"按下点到当前点"整段算，不逐帧累加，松手不会差一截 |
| 拖的是多选里的一员 | 整个选区一起挪（UE / Figma 的手感） |
| **Shift + 拖到另一个节点上** | 改**父子关系**（这时节点回到按下前的位置、只高亮落点 —— 否则它会盖住落点，命中测试永远只认它自己） |
| **空白处左键拖** | **框选**（橡皮筋）；Ctrl+框选是叠加选择 |
| **中键拖 / Shift+左键拖** | 平移画布（左键平移让位给框选了；滚轮平移仍然可用） |
| 引脚拖出连线 / 点线 + Delete / Alt+方向键 / 属性区「移动」 / 缩进树拖放 | 改父子关系的既有几条路都不变 |
| **「自动布局」按钮** | 丢掉手动位置与节点组，回到 tidy tree 自动排布 |

配套的三件体验：

- **节点内预览区**：行为树没有纹理可缩略，缩略的是"这是什么、关键参数、现在什么状态"——
  组合节点显示子节点数、任务显示前两个参数（`seconds=2.5`）、`Task.Subtree` 显示引用的包，
  右端一个状态小标（执行中 / 路径上 / 有错 / 有警告）。
- **小地图**：画布右下角，所有节点按类别配色的小方块 + 当前视口框；点/拖它可以把视口带过去。
- **默认视图就是节点图**（以前默认缩进树，要手点一次才切）。

位置与节点组存在**浏览器本地**（`localStorage`，按包路径分键），**不写进包** ——
包格式是游戏要读的，往里塞编辑器专用坐标会污染行为树数据、还可能被游戏校验拒绝；
代价是换浏览器/换机器不跟着走，点「自动布局」即可复位。

验证：`editor_web_selftest.js` §16（空白处手势语义）、§21（节点图里普通拖动不挂父、Shift 才挂父）、
§23（手动位置优先于自动布局、拖动位移是绝对值而非累加、多选一起挪、位置/节点组持久化、
「自动布局」复位、框选矩形归一化与命中判定、预览区三种摘要、小地图方块数与等比缩放）；
真浏览器自检 ③ 一节用真鼠标验证"拖动中节点跟着鼠标走、松手报告已摆放、结构没变、
位置重画后还在、框选出现选框并选中、小地图点击能带视口、Shift+松手真的挂成父子"。



### 9.5.8 浅色 / 深色主题

顶栏右侧一个「主题：深色 / 浅色」按钮，点一下切换，选择记在浏览器里（`localStorage`）。

- 主题实现是**一份 token、两套色值**：`app.css` 顶部的 `:root` 是深色（默认），
  `:root.light` 覆盖同一组 token；所有组件只引用 token，**没有第二份组件样式**。
  这两块由一张表生成（`Mod/Packages/_theme_tokens.py`），不会出现"改了深色忘了浅色"。
- 切换 = 给 `<html>` 加/去 `light` 类；`index.html` 里有一小段内联脚本在**第一帧之前**
  就把类定下来，浅色用户不会先看到一闪的深色。
- 需要核对时可以用 `?theme=light` / `?theme=dark` 直接指定（截图脚本就是这么用的，
  见 `Mod/Packages/out/light.png`）。

验证：`editor_web_selftest.js` §24（默认深色、切换改 `<html>` 类与按钮文案、选择持久化、
存了非法值时回落深色、切主题不动数据）；真浏览器自检 ⑦ 一节比较的是浏览器**算出来的颜色**
（页面底色 / 画布底色 / 节点底色 / 文字色都必须变，浅色确实是"深字浅底"、切换后布局不破）。

### 9.5.9 节点组 / 注释框

画布上框选若干节点后点「建组」，得到一个带标题的注释框（UE 的 Comment Box / Blender 的 Frame）：

- **拖组头 = 整体移动**组内节点（组里还没手动摆过的节点会先按当前位置落一个基准，
  否则一移动它们就被自动布局拽回去）；**右下角可缩放**；
- **双击标题改名**；属性区里能改标题、换颜色（蓝/紫/绿/橙/灰，UE 里也是这么给注释配色的）、
  「贴回内容」（框重新贴合组内节点）、「解散」；
- 选中注释框时 `Delete` = 解散（不是删节点）；`Esc` 取消选中；
- 组只影响**摆放**、不影响行为树结构：解散之后节点原地不动；位置与组一起存在浏览器本地。

顺带修掉一个真实缺陷：**多选在节点图里以前看不见** —— 只有"最后点中的那个"有高亮，
`.multi-selected` 只有缩进树里有。现在节点图里的多选成员也会亮一圈，选了几个一眼能数出来。

验证：`editor_web_selftest.js` §25（包围盒含留白与标题栏高度、空/未知成员不产生坏框、
建组取当前选中、改名拒绝空名、整体移动让成员同步位移且**不改树**、拖组头与缩放把手的位移换算、
贴回内容、选中互斥、解散后位置保留、组随位置一起持久化、自动布局连组一起清）；
真浏览器自检 ⑧ 一节用真鼠标走"框选 → 建组 → 拖组头（框与成员位移一致）→ 双击改名 → Delete 解散"。

### 9.5.10 画布手感：默认浅色 / 网格铺满 / 中键平移 / 滚轮缩放 / 物料拖入 / Shift 点线断开

按使用反馈调整了六处：

| 项 | 行为 |
|----|------|
| **默认主题** | 改成**浅色**（只有明确选过深色才是深色；`?theme=dark` 可强制） |
| **网格背景** | 画布被撑到**至少和可视区一样大**，所以格子铺满整个绘图区（节点少的时候也不留纯色空地）；只改 DOM 尺寸，几何里的"内容尺寸"不动，"适应窗口"和诊断行仍按内容算 |
| **平移** | 按住**滚轮（中键）**拖动平移（原来的左键平移早让位给框选了）。改走 **document 级**鼠标通路：以前监听挂在滚动容器自己身上，拖出容器就收不到 mousemove，像"拖到一半断了"；中键还要 `preventDefault`，否则 Windows 的自动滚动会吃掉后续事件 |
| **缩放** | **滚轮上下 = 缩放**（用户要求；Shift+滚轮仍是左右滚），并且**锚在光标位置**：先记住光标下的内容点，缩放重画后把滚动位置挪回去 |
| **物料拖入** | 从左侧物料区把物料拖到**空白画布**上＝在落点**新建节点**（挂到当前选中的节点下；选中的是任务节点就退到它的父节点）；拖到某个节点上仍是"挂成它的子节点" |
| **断开连线** | **Shift + 点连线**＝直接断开（等价于点中后按 Delete）；断线仍是"子节点提升为父节点的兄弟"，不删节点 |

顺手修掉两个过程中暴露的真问题：

- **注释框会吃掉指针事件**：注释框铺得很大，压在它下面的连线点不中、它盖住的空白也没法框选。
  现在注释框本体 `pointer-events: none`，只有标题栏和右下角把手接鼠标（UE 的注释框也是这个行为）。
- **重画会丢掉平移位置**：滚动区每次渲染都是新元素（`scrollLeft` 从 0 开始），于是"平移到图的另一头
  → 点个节点改参数"会让视野跳回原点。现在重画前记住滚动位置、重画后接回去。

验证：`editor_web_selftest.js` §16（空白手势语义）、§24（默认浅色、存了非法值回落浅色）、
§26（中键平移的位移与自动收尾、滚轮=缩放与空事件不缩放、画布铺满可视区、物料落空白处新建节点并
钉在落点、装饰器/任务节点选中的回退、Shift 点线断开 vs 普通点击只是选中）；
真浏览器自检 ⑦（默认浅色 + 切深色后浏览器算出来的颜色）与 ⑨（真中键拖动平移、真滚轮缩放并锚在光标、
画布高度≥可视区、物料真拖进画布后在落点新建、Shift+点线真的断开）。

### 9.5.11 画布手感第二轮：平移空间、游离节点、拖动影像、物料拖动、换包

按使用反馈又调整了一轮，其中"中键拖动平移看不出来"和"小地图拖不动可视窗"是**同一个根因**：

| 反馈 | 原因 | 处理 |
|------|------|------|
| 滚轮按下拖动平移**看不出效果**；小地图没法按住拖可视窗 | 画布只有"内容那么大"，内容一屏装得下就**没得滚** —— 平移和拖可视窗自然都没反应 | 画布 = `max(内容, 可视区) + 2×留白`，永远留出可滚余量；留白用**内容层**（`.graph-content` 的 left/top）实现 —— 绝对定位子元素的包含块是 padding box，用父元素 padding 是推不走它们的（踩过一次，内容直接被推到视口外） |
| 物料拖到画布**自动挂到当前选中节点**上 | 上一版就是这么设计的 | 落到**空白画布**＝新建**游离节点**（不挂载）；落到已有节点上才连线。游离节点画成虚线方框、缩进树里**单独列一条**（和根同级），一挂上就从游离列表消失；它们存在浏览器本地，**保存时不会写进包**（界面上写明） |
| 拖物料**看不到**跟着鼠标的东西 | 没有拖动影像 | 加 `.drag-ghost`：拖动中跟着鼠标的小标签（节点自身拖动不需要，方框本来就跟着走） |
| 物料区按住拖动**变成了文字框选**、有的拖不动 | 界面元素没禁止文字选择 | 可拖动元素统 `user-select: none`，拖动源 mousedown `preventDefault`；自检逐项确认每个物料都有 mousedown 监听 |
| 顶部**切换包**之后画布没跟着换 | 下拉只有"点「打开」"才加载 | 加 change 监听：下拉里选中即 `openPackage` |
| 缩放到光标 / 适应窗口 | 上一轮加了缩放到光标，但留白接入后坐标换算要跟着改 | 三套坐标系（未缩放内容坐标 / 含缩放的几何坐标 / 屏幕坐标）在注释里写清，换算只在一处做 |

顺带修的：诊断行的"横向需滚动"不再把画布留白算成需要滚动；`refreshGraphPositions` 与 `syncCanvasViewportSize` 的职责分清（一个管内容位置、一个管画布尺寸与留白）。

验证：`editor_web_selftest.js` §24/§26（默认浅色、中键平移位移与自动收尾、滚轮=缩放且空事件不缩放、
画布铺满与留白、**游离节点**的创建/查找/布局/持久化/收编/删除/摘下来、拖动影像跟随与销毁、
物料项都是拖动源、装饰器仍挂到选中节点）；
真浏览器自检 ⑨（真中键拖动、真滚轮缩放并锚在光标、真拖物料进画布）与 ⑩（拖动影像跟随鼠标、
松手后消失、空白处落成游离节点、缩进树里单独一条且缩进与根同级、物料项 `user-select: none`、
顶部下拉换包后画布立刻换）。

### 9.5.12 无限画布：拖到左上角外会扩边、拖动中连线实时跟随

两条反馈，第二条的根因很隐蔽：

| 反馈 | 原因 | 处理 |
|------|------|------|
| 画布的节点被"钉"在左上角，**没法在左上方再加东西**扩大区域 | 节点坐标被夹在 `≥ 0`（后来是 `≥ -400`），而且画布尺寸只按"原点右下方"的内容算 —— 左上角之外没有空间 | 内容坐标**允许为负**，画布按**内容包围盒**算尺寸；往左上长出去的部分通过**内容层偏移**（`layerLeft = 留白 − min(0, 最小 x)`）来容纳，几何/连线/命中测试仍在同一套内容坐标里。扩边时把偏移的变化量补进滚动位置，**视野不跳** |
| 拖动**有连线**的节点，线要等松手才画出来 | 拖动中只更新了 `path` 的 `d`，没有同步 SVG 的 `width/height` 与画布尺寸 —— 超出旧画布范围的部分被 SVG 裁掉了（画布边缘之外那段线是"没画"而不是"没更新"） | 拖动路径（`refreshGraphPositions`）里同步画布尺寸 + SVG 尺寸 + 补偿滚动；整屏重画（`renderGraph`）里也补偿一次 |

配套：`contentBounds()` 统一算内容包围盒（小地图取景也用它）；落点不再夹取（落在哪儿就是哪儿）；
`syncCanvasViewportSize` 返回值里带上 `dx/dy`（内容层偏移变化），调用方负责补偿滚动 —— 这是"扩边不跳视野"的关键。

验证：`editor_web_selftest.js` §27（包围盒含负坐标、内容层按负坐标右移、画布尺寸覆盖负区、
层偏移变化量用于补偿、落点允许负坐标、拖动可写到负坐标、扩边后画布变大）；
真浏览器自检 ⑪（拖动**有连线**的节点到画布右下角：拖动中连线终点已经在新位置、
画布/SVG 同步变大；把最左上的节点往左上拖 200×160：节点跟着鼠标走、
内容层偏移 152 → 335（左上方腾出新空间）、视野不跳）。

### 9.5.13 负坐标的连线不再被裁；包可以直接改并保存

| 反馈 | 原因 | 处理 |
|------|------|------|
| 拖到左上角（坐标变负）之后，**松手连线也被截断**（往右下拖是实时的） | 拖动路径会同步 SVG 的 `width/height`，所以往右下长出来时看得见；但**负坐标**在 SVG 自己的坐标系里是"视口之外"，而最外层 SVG 默认 `overflow: hidden` —— 于是负半轴那一段被裁掉 | `.graph-wires { overflow: visible }`：负坐标的连线照样画出来（命中测试也一起恢复，自检里"线上有一段真的能被点中"就是验这个） |
| 编辑器里**改不了包**，只能"另存为" | 白名单里的 Mod 分发目录被标成只读（当初是为了防"改完被 Mod 更新覆盖"），保存按钮直接禁用 | 两个目录都允许写：编辑器要能改**游戏正在用的那份包**。保存回 Mod 目录时给出**提醒**（`warnsModFolder`），包列表标「Mod 分发」、打开时说明"可以直接改并保存，但 Mod 更新会覆盖"。边界不变：**AI 侧**仍然不许写磁盘包（那是游戏内状态铁律），可写只服务于人用的编辑器 |

顺带修的：保存成功后 `loadPackages()` 会把"已保存…"的状态顶掉 → 改成安静刷新（`loadPackages(quiet)`）。

验证：`PlayerAiEditor --selftest` 两条断言改成"Mod 目录里的包可写 / 能保存回去（带提醒）"；
真浏览器自检 ⑫（把有连线的节点拖到左上角外之后：连线还在、终点跟着节点、线上有一段能被点中；
包列表标出「Mod 分发」、打开时说明可改、"保存"按钮可用、直接保存回 Mod 目录成功并提醒覆盖）。

> 这一节的"两个目录 + Mod 分发提醒"后来被 §9.5.20 推翻了：第二目录整个删掉，只剩
> `<实例根>/PlayerAi/BehaviorTrees`，`warnsModFolder`/「Mod 分发」标记也随之删除。

### 9.5.14 网格背景与节点锁在同一坐标系

反馈：往左上角拖到坐标变负时，**背景在动、其余节点不动**，看着像"其它节点都在移动"。

原因：网格原来画在 `.graph-canvas` 上，而画布在扩边那一下会被**滚动补偿**推一把
（`layerLeft` 变大、`scrollLeft` 跟着加同样的量，这样内容在屏幕上不动）——
节点在内容层里因此稳如泰山，贴在画布上的网格却被带着走了，两者的相对位置就漂了 Δ。

处理：**网格改画在内容层 `.graph-content` 上**（和节点同一个坐标系、同一个偏移），
画布只留底色。顺带让格子大小跟着缩放走（`background-size = 20 × 缩放`），
格子的"世界尺寸"恒定，放大缩小时看着更像同一张网格。

验证：真浏览器自检 ⑬ —— 先确认网格的 `background-image` 在内容层、画布上没有；
再把一个有连线的节点拖到左上角之外，断言**旁观节点在屏幕上位移为 0**、
**网格层与旁观节点的相对位置恒定**、松手整屏重画之后依然为 0。

### 9.5.15 格子看不见了：把网格挪到内容层引发的两个连锁问题

把网格从画布挪到内容层（§9.5.14）之后用户反馈"看不到格子了"。查下来是**我这次改动引入的两个 bug**：

| 现象 | 原因 | 处理 |
|------|------|------|
| **格子整片消失** | 内容层设尺寸时，`width/height` 那一对变量在代码里**还没算出来**（声明在后面），于是 `style.width = "undefinedpx"` → 层是 0×0 → 背景没有可画的框 | 把"定层尺寸"这段挪到算出画布尺寸之后；自检补上"层必须有真实尺寸且盖住可视区"（以前只断言了 `background-image` 在层上，没断言层有大小 —— 所以这个 bug 溜过去了） |
| 拖动节点**垂直方向对不上**（y 少 ~47px） | 层的尺寸给成了整个画布，而层本身有偏移 → 层伸出画布之外 → **滚动区被撑大**，于是 ①出现滚动条让容器尺寸变化 → ResizeObserver 触发"自动适应"②更要命的是浏览器**滚动锚定**（scroll anchoring）在拖动过程中自作主张调 `scrollTop`：实测 `scrollTop 135 → 182`、`canvasTop 119 → 72` | 层的尺寸改成"画布减去偏移"（右/下边缘正好贴住画布）；画布 `overflow: hidden` 保证"滚动区 == 画布"；画布与内容层 `overflow-anchor: none` 关掉滚动锚定（画布的滚动只归我们自己的平移/缩放逻辑管） |

排查靠的是**量数字**而不是猜：在自检里打印拖动前后的 `scrollTop / layerTop / canvasTop`，
一眼就看出"画布自己在垂直方向平移了 47px"，而节点与网格都没动。

教训（已写进自检）：**"背景画在哪个元素上"和"那个元素有没有尺寸"必须一起断言** ——
只断言 CSS 属性存在，会漏掉"元素被撑成 0×0"这类问题。

### 9.5.16 网格层与内容层分家：留白区（0,0 左上角）也要有格子

反馈：往左上角拖过 `0,0` 之后，**内容原点左上角那圈留白是空的**，没有背景格子。

原因（§9.5.14 那个"把网格挪到内容层"的决定留下的）：内容层是**从内容原点开始**的
（`left/top = layerLeft/layerTop`，尺寸"画布减去偏移"），所以它天然盖不到原点左上的留白 ——
网格画在它上面就跟着只从原点往右下画。可留白也是画布，也该有背景。

处理：**网格与内容分成两层**。

| 层 | 位置 / 尺寸 | 背景 |
|----|-------------|------|
| `.graph-grid`（新） | `left/top = 0`，尺寸 = **整块画布**（`pointer-events: none`，最底层） | 四道 `linear-gradient` 画格子，`background-position = layerLeft/layerTop` |
| `.graph-content` | `left/top = layerLeft/layerTop`，尺寸 = 画布减偏移（右/下贴住画布） | 无（只剩节点方框 / 连线 SVG / 注释框） |

关键点：格子既要**铺满整块画布**，又要**锚在内容坐标系**上 —— 两者由
`background-position = 内容层偏移` 同时满足：层固定不动（覆盖整块画布），
相位跟着内容原点走（拖节点/扩边时格线与节点锁在一起，不会回到 §9.5.14 的漂移）。

验证：无头自检 + 真浏览器自检各补了断言 —— 网格层与画布 `getBoundingClientRect()`
四条边都在 1.5px 内重合、`background-position` 等于内容层偏移、网格层是画布的第一个子元素
（在节点下面）、`.graph-content` 上不再有 `linear-gradient`（没有双层网格）、
并且拖出界后 `layerLeft/layerTop > 0` 时依然满足以上全部。真浏览器自检 125 → **131/131**，
前端无头自检 327 → **331/331**，`light.png` / `nodegraph.png` 肉眼复核：整块画布（含原点左上的留白）都铺满格子。

### 9.5.17 "一个包都读不到"：实例根自动判定指错了目录

反馈：编辑器打开后**包里一个都没有**。

原因不在包，在**编辑器自己的实例根**：`PlayerAiEditor.exe` 本轮是直接双击/从 `publish/editor` 启动的，
而 `DefaultInstanceRoot()` 当时只看三处 —— 当前目录、`AppContext.BaseDirectory`、它的父目录 ——
三处都没有 `Mods/`，于是退回"当前目录"，实例根就成了 `publish/editor`，
包目录被拼成 `publish/editor\PlayerAi\BehaviorTrees (missing)` 与 `publish/editor\Mods\... (missing)`。
真正的游戏实例在**兄弟目录** `publish/Windows`。

顺带踩到的一个坑：单文件发布下 `AppContext.BaseDirectory` **可能是解包临时目录**
（`%TEMP%\.net\PlayerAiEditor\...`），光信它会一路找错 —— 真正的 exe 路径要用 `Environment.ProcessPath`。

处理（`Program.cs`）：

1. 起点改成三个：`Environment.ProcessPath` 所在目录、`AppContext.BaseDirectory`、当前目录；
2. 判定顺序：① 起点自己带 `Mods/` → ② 起点的父目录带 `Mods/` → ③ **兄弟目录**里恰好只有一个带 `Mods/` 的（`publish/editor` + `publish/Windows` 就是这种；有歧义就不猜）；
3. 实例根下既没有 `Mods/` 也没有 `PlayerAi/` 时**在控制台吼一声警告**并提示用 `--root` 指定 —— 以前是静默退回，界面只显示空列表，看不出是路径问题；
4. 新增 `--print-root`：只打印判定出来的实例根与包目录就退出，专门用来排查这类问题。

验证：`assets` 门新增两条 —— ①从编辑器自己所在的目录当工作目录、不带 `--root` 跑 `--print-root`，断言它自己找到游戏实例根；②起服务后查 `/api/packages`，断言 `count >= 1`（空列表就是用户看到的那个现象，必须能被测出来）。

> 布局后来改过（见 §9.5.19）：编辑器从 `publish/editor` 搬到了**实例根里面**的
> `<实例根>/PlayerAi/editor/`，判定顺序也随之加了"沿父链往上找"。上面这一节保留的是当时的过程。

### 9.5.18 编辑器到底"索引了哪些文件"（一张表说清）

编辑器（现在是 `<实例根>/PlayerAi/editor/`，见 §9.5.19）**自己不含任何数据文件**，只有单文件 exe（+ pdb）：

```
publish/Windows/PlayerAi/editor/
  PlayerAiEditor.exe      5.3 MB   ← 纯逻辑层 + 前端资源全都嵌在里面
  PlayerAiEditor.pdb / Engine.pdb / EntitySystem.pdb / EntitySystem.xml
```

所以"编辑器索引文件"其实是两步：**先在实例根定位包目录，再在目录里按扩展名扫顶层**。

| 找什么 | 从哪里找 | 规则 |
|--------|----------|------|
| 实例根（一切的起点） | `Environment.ProcessPath` 目录 → `AppContext.BaseDirectory` → 当前目录，再沿父链往上（最多 6 级）找带 `Mods/` 的 | 同时有 `Mods/` + `PlayerAi/` 的优先；`--root` 显式指定时最高优先。见 §9.5.17 |
| 树包 `.scbtpak` | `<实例根>/PlayerAi/BehaviorTrees/` | `Directory.GetFiles(dir, "*.scbtpak", TopDirectoryOnly)`：**不递归**、按文件名排序（只有这一个目录，见 §9.5.20） |
| 动作包 `.scatpak` | 同一个目录 | 同样只扫顶层 |
| 前端页面 | **exe 内嵌资源**（`PlayerAiMod.Editor.Web.*`），不看磁盘 | `/`、`/app.js`、`/app.css`、`/engine-adapter.js`、`/selftest.html` 直接读嵌入资源 |
| 游戏控制通道 | `<实例根>/CmdBridge.runtime.json`（游戏启动时写：port + token） | 读不到就如实报"游戏没在跑"，编辑器照常可用 |
| 某个包的解析 | `PackageRoots.Resolve(name)` | 接受 `demo.greet` / `demo.greet.scbtpak` / `sub/x.scbtpak` / **白名单内的绝对路径**；`..` 穿越、白名单外路径、前导 `/` 一律拒绝（白名单同时是路径穿越防线） |

实测（`http://127.0.0.1:8760`，实例根 `publish\Windows`；这是**单一目录**之后的结果）：

```
/api/packages  count=3   common.scbtpak / demo.greet.scbtpak / test.action.scbtpak（source=instance）
/api/actions   count=3   action_20260911_185631 / sample_walk / 生存游戏（都在同一个包目录里）
demo.greet                        → ok=true   root=instance
绝对路径 …\common.scbtpak          → ok=true   root=instance
绝对路径 …\sample_walk.scatpak     → 能定位，但按树包读会失败（树包/动作包不混用）
…\Scworld\no_such.scbtpak          → 拒绝（同实例根但不在包目录里）
C:\Windows\win.ini                 → 拒绝
..\..\MachineCache.scbtpak         → 拒绝
/etc/passwd                        → 拒绝
```

前端拿到 `/api/packages` + `/api/actions` 后建下拉框与左侧物料区；节点位置/注释框/游离节点另存在浏览器 `localStorage`（键按包路径分），**不写进包**。

### 9.5.19 编辑器搬进实例根：`<实例根>/PlayerAi/editor/`

原来的布局是 `publish/editor/PlayerAiEditor.exe`（**和游戏实例并排**）。这有两个毛病：
① 编辑器不在游戏目录里，得记着 `--root`；② 它的上级目录也不像游戏实例，
一旦自动判定出错就退化成"自己的目录 = 实例根"→ 一个包都读不到（§9.5.17）。

现在改成**装在实例根里面**：

```
publish/Windows/                       ← 实例根（游戏 exe、Mods/、PlayerAi/ 都在这）
├── PlayerAi/BehaviorTrees/                    ← **唯一的包目录**（树包 + 动作包都在这）
└── PlayerAi/editor/PlayerAiEditor.exe         ← 编辑器（就是这里）
```

改动：

| 位置 | 改动 |
|------|------|
| `check_player_ai_build.py` | `publish` 的 `-o` 指到 `publish/Windows/PlayerAi/editor`；新增 `INSTANCE/EDITOR_DIR/EDITOR_EXE` 三个常量；`editor`/`assets` 门都用它们 |
| `check_editor_browser.py` | `EDITOR_EXE` 跟着搬 |
| `Program.cs` | 实例根判定新增"**沿父链往上找**"（最多 6 级，优先同时有 `Mods/` 与 `PlayerAi/` 的目录，其次只要 `Mods/`）—— 因为现在 exe 目录和它的父目录都没有 `Mods/`，要走到祖父 `<实例根>` 才对 |
| `assets` 门 | 新增布局不变量检查：编辑器必须在实例根**里面**（`commonpath` 判定 + 从编辑器目录跑 `--print-root` 必须得到实例根） |

好处：双击 exe 就在游戏目录里，自己就能找到实例根；`publish/editor` 那个"看着像实例根"的目录不再存在。

### 9.5.20 砍掉第二包目录：只认 `<实例根>/PlayerAi/BehaviorTrees`

用户要求：`Mods\PlayerAiMod\PlayerAi` 不需要了，只要 `PlayerAi\BehaviorTrees`；
**导出与修改默认都落在这个目录**；**游戏内 AI 不允许覆盖**（只能新建，重名要显式 `overwrite=true`）；
这个目录由**编辑器**负责改。

改动（单一目录，端到端）：

| 位置 | 改动 |
|------|------|
| `PackageRoots.cs` | 构造只收一个目录；`ModRoot`/`ModFolder` 删除；新增 `DirectoryFor(instanceRoot)`；`Discover()` 只发现这一个 |
| `PackageTemplates.Install` | 出厂模板改为装进**包目录**（仍然"缺什么补什么、绝不覆盖"） |
| `PlayerAiRuntime` | 动作包查找链从"实例 → Mod"简化成单目录；注释同步 |
| `EditorApi` | 单根；`/api/meta` 去掉 `modFolder`；保存结果去掉 `warnsModFolder`；动作包列表去掉 `shadowed`/`source: mod` |
| `app.js` / `index.html` / `engine-adapter.js` | 去掉「Mod 分发（只读）」标记、`shadowed` 徽标与相关提示；按钮/提示文案统一成"包目录" |
| `AiCommandSet` | `ai.tree.list` / `ai.action.list` 文案与回包字段（去掉 `shadowed`）同步 |
| 自检 | 双目录相关断言按"单目录"重写：跨根引用 → 包目录内引用、`shadowed` → 工厂示例就在包目录里；`editor 86/86`、前端无头自检、真浏览器自检全部保持绿 |

判定边界（按用户确认的口径）：

| 谁 | 能读 | 能新建 | 能覆盖已有包 |
|----|------|--------|--------------|
| 编辑器 | ✅ | ✅ | ✅（显式确认后） |
| 游戏侧（AI / 插件） | ✅ | ✅（`ai.record.save`、`ai.tree.export`、出厂模板补齐） | ⚠️ 只有显式传 `overwrite=true` 才允许，默认拒绝重名 |

顺带做的数据迁移（部署实例）：把 `Mods\PlayerAiMod\PlayerAi\BehaviorTrees\` 里的
`common.scbtpak`、`demo.greet.scbtpak`、`test.action.scbtpak` **缺什么补什么**地复制进
`PlayerAi\BehaviorTrees\`（`sample_walk.scatpak` 那边已有，保留实例那份），然后删掉整个
`Mods\PlayerAiMod\PlayerAi\` 目录（连空的 `Mods\PlayerAiMod\` 也删了 —— 它只是老约定的残留）。

### 9.5.21 编辑器可以直接"启动游戏 / 结束游戏"了

之前的编辑器只是**控制通道客户端**：连得上就干活，连不上就报 `game_unreachable`。
调一条行为树要在"编辑器 ↔ 游戏"之间来回，每次还得去文件管理器双击游戏 —— 纯摩擦。

现在工具条上有 **「启动游戏」/「结束游戏」**，以及一个**三态**徽标：

| 状态 | 徽标 | 按钮 |
|------|------|------|
| 没启动 | `游戏：未启动` | 「启动游戏」可点，「结束游戏」灰掉 |
| 进程起来了、控制通道还没开（游戏正在加载世界，这中间几十秒本来就连不上） | `游戏：启动中（等控制通道）#pid` | 两个都灰掉（别乱点） |
| 通道能连上 | `游戏：已连上#pid` | 「结束游戏」可点 |
| 实例根里没有 `Survivalcraft.exe` | `游戏：找不到 Survivalcraft.exe` | 「启动游戏」灰掉 |

- `POST /api/game/launch`：`Process.Start(<实例根>/Survivalcraft.exe)`，工作目录 = 实例根
  （这样 `<data:>`、Mods、`PlayerAi/` 全都对得上），**不传任何命令行参数** ——
  就是"手动双击"的等价物。已经在跑就拒绝（`already_running`），没 exe 就拒绝（`game_exe_missing`）。
- `POST /api/game/quit`：先 `CloseMainWindow()` 请它**正常退出**（走正常关闭流程：释放 AI 输入、
  flush 日志），等 6 s 不退再 `Kill(entireProcessTree)`，回包里报"正常退出几个 / 强杀几个"。
- `GET /api/game/process`：只读地把上面四态说清楚（`exeExists/running/pid/runtimeStale/channelConnected`）。
- 启动后前端**每秒轮询**这个端点等控制通道，最长 3 分钟；连上就自动刷新游戏状态并提示
  "现在可以用游戏状态 / 实时监视 / 试跑 / 推送热重载"。

顺手修的一个老毛病：**旧 `CmdBridge.runtime.json` 会撒谎**。游戏上次退出时留下的 runtime 文件
还在，编辑器连它只会得到裸露的 `由于目标计算机积极拒绝…` —— 看着像"游戏在跑但通道坏了"。
现在这种情况下 `/api/game/process` 报 `runtimeStale=true`，命令层也会补一句
"如果游戏确实没开，点「启动游戏」；`CmdBridge.runtime.json` 可能是上次运行留下的旧文件"。

边界没动：这是**人用的编辑器在起进程**，不是 AI 在改游戏状态；启动之后所有命令仍然只走
CmdBridge 控制通道，AI 那条"只能通过玩家控制器行动"的铁律原样成立。

验证：编辑器无头自检 86 → **94 项**（新 exe 时如实拒绝启动、没在跑时结束命令如实回 `not_running`、
写一个死端口的 runtime 文件 → `runtimeStale` 且报错点明"旧文件"、编辑器从不改写那个文件）；
前端无头自检 331 → **343**（三态徽标的文案与 `disabled`、`launchGame` 真发 POST、
启动后确实开起轮询、通道连上后停轮询、结束游戏也停轮询）；真浏览器自检 132 → **141**
（真按钮 + 真 `disabled` + 三态文案，用临时替换 iframe 的 `fetch` 驱动，**绝不真的启动游戏**）。

### 9.5.22 播放 / 暂停一棵树：一个按钮 + 一个「树：…」徽标；顺便把"not_ready"的谎话堵掉

用户反馈三件事：①点实时监视报"游戏拒绝了…多半是 Mod 没有这个命令"，过一会儿自己又好了；
②想要一个播放按钮（播放 = 推送热重载到游戏里），并且能一眼看出树在不在跑；③试跑应当只跑
选中的那一个动作包。

**① 报错分类错了（真 bug）**：游戏拒绝时给的是稳定错误码，但编辑器把**所有**拒绝都当成
"多半是 Mod 太旧"。实测那次是 `not_ready`（世界还没加载完 / AI 没接管）—— 这是**过渡状态**，
世界起来 + `ai enable` 之后自己就好了（用户看到"过一会儿又能监视了"就是这个）。改法：

| 情况 | 现在怎么报 |
|------|-----------|
| 没连上（游戏没跑 / 通道没开） | `game_unreachable` + 旧 runtime 文件的提示 |
| 连上了但**还没准备好**（`not_ready`） | `game_not_ready`：附上游戏原话 + "需要①进世界②`ai enable`；准备好之前这里会一直等，好了会自动开始刷新" |
| 连上了但 Mod 里根本没这条命令（`unknown_command`） | `game_refused` + "多半是这份 Mod 还没有这个命令" |
| 其它拒绝 | `game_refused` + 原错误码 + 游戏原话（不再猜原因） |

实现上给 `GameBridgeClient` 加了一个 `GameCommandException`（带 `code`/`gameMessage`），
`EditorApi.GameError` 按码分流；`/api/game/status` 也改成走同一套（以前它在路由里自己 catch，
报错文案跟别的端点不一致）。实时监视面板里"还没准备好"用**中性样式**显示，并写明会自动恢复 ——
以前是个红框，第一眼像坏了。

**② 播放按钮 + 运行状态徽标**：工具条上一个按钮，语义由"游戏里现在跑的是什么"决定：

| 游戏状态 | 按钮 | 点了做什么 |
|----------|------|-----------|
| 没接管 / 跑的是别的包 | `▶ 播放这棵树` | 有改动先保存（校验+原子写），再 `ai.tree.switch` 切过去开始跑 |
| 跑的就是这棵树、无改动 | `⏸ 暂停` | `ai.pause`（保留运行态、立刻释放 AI 注入的输入） |
| 跑的就是这棵树、已暂停 | `▶ 继续` | `ai.resume` |
| 跑的就是这棵树、有改动 | `⟳ 推送改动` | 保存 + `ai.tree.notify` 热重载（不重启世界、保留运行态） |

旁边的 `树：…` 徽标常显：`树：运行中 demo.greet.scbtpak（tick 25967）` / `树：已暂停 …` /
`树：跑的是 common.scbtpak（编辑器打开的是 demo.greet.scbtpak）` / `树：未接管（进世界 + ai enable
后可播）` / `树：游戏没在跑`。原来的「推送热重载」独立按钮**删掉了** —— 它的语义已经被
"⟳ 推送改动"这个状态覆盖，留两个入口只会让人不知道该点哪个。

⚠️ 一个前提必须说清：`ai.tree.switch` 需要**可接管的宿主**（进世界 + AI 接管）。在主菜单里没有
角色，所以"播放"会如实回 `game_not_ready`；而「进入游戏」这种整段在菜单里的动作包，走的是
**试跑**那条路（游戏侧为 UI 类动作包留了 `UiOnlyActuator` 兜底，不需要角色）。

**③ 试跑只跑选中的那一个包**：本来就只发一条 `ai.action.play`（body 里就是下拉框那个文件名，
自检用"请求体里不能出现别的包名"钉住）；这次补的是**先把行为树暂停**再试跑 —— 树和动作包会往
同一套输入通道写（帧首先推进动作包，没暂停才 tick 树），两边同时写就是互相打架。试跑时顺手
暂停会记下来，停止回放时提示"树还是暂停状态，点继续恢复"。

**④ 顺带的数据**：`Mod/Packages/out/make_enter_game_tree.py` 用编辑器 API 生成了
`enter.game.scbtpak`（`Root → Sequence [Log, PlayActionPackage(packages=["进入游戏"]), Wait 2]`，
`/api/package` 回包 `reloaded=true / nodes=5 / 0 errors`），就是"用节点连线调用进入游戏动作包"。

**⑤ 自检踩到的一课（必须记住）**：新加的自检里有"没在跑时结束命令要回 `not_running`"这一条，
而它跑在临时实例根上 —— 当时机器上开着**用户正在玩的那个游戏**，`/api/game/quit` 按进程**名字**
把它关掉了。改法（两层）：
- `GameLauncher.Running(instanceRoot)` **按 exe 路径认领**进程（`MainModule.FileName` 的目录
  必须等于实例根），认不出的（权限不足）一律不动 —— 宁可不动，也不误杀别人的游戏；
- 判据写进注释与自检：进程状态只在**属于这个实例根**时才算数。

**验证**：编辑器无头自检 94 → **100/100**（新增一个假游戏通道：监听回环端口、按剧本回
`{"ok":false,"error":{code:"not_ready"…}}`，断言 `/api/game/live`、`/api/game/status`、
`/api/game/tree/switch` 三处都把它报成 `game_not_ready` 且保留游戏原话；另一个剧本回成功切换，
断言 `switchMs/nodes` 原样带回、请求里发的是绝对路径）；前端无头自检 343 → **355**
（按钮四种语义 + 徽标四种文案 + "点播放先保存再 switch" / "跑的就是它且有改动发 notify" /
"试跑只发选中的那一个包、且先 pause"）；真浏览器自检 141 → **149**（真 DOM 驱动上述状态）；
窄窗口顶栏顺手压紧了一档（新增 ≤980px 断点 + 徽标省略号）：820×560 下画布 139 → **145px**。

### 9.5.23 无角色宿主：**主菜单里也能跑树、也能一开始就监视**

用户的原话："不是有进入游戏这个动作包吗，这个就是游戏启动后、不进入世界就要跑的，
实时监视也需要一开始就能监听。" —— 这两件事以前都做不到：

- `ResolveTreeHost()` 只认"已接管且就绪的**角色**"。主菜单里没有角色 → 返回 null；
- 于是 `ai.tree.switch` / `ai.tree.snapshot` / `ai.blackboard` 全部 `not_ready`
  （编辑器上就是"点实时监视被拒绝"，而且报的是误导人的"Mod 太旧"）；
- 帧首 tick 在 `GameManager.Project == null` 时直接 `ReleaseAll(); return;` —— 树根本没机会推进。

改法：新增 **`UiTreeHost`（无角色宿主）**，实现同一套 `IAiTreeHost`：

| 项 | 值 | 为什么 |
|----|----|--------|
| `HostKind` / `HostName` | `"menu"` | 编辑器据此显示"（主菜单，无角色）"，而不是让人以为角色已被接管 |
| 执行器 | `UiOnlyActuator` | 引擎软光标那套**只点 UI**；世界外的"按住 W / 转视角"一律空实现，不假装成功 |
| 传感器 | `null` | 没有角色就没有位置/朝向可比，动作包的漂移检查自动跳过（`ScatPlayer` 本来就这么判） |
| `IsReady` | 有注入通道且 `PlayerAiConfig.Enabled` | 通道不在就如实报 not ready |
| `Paused` | 由运行时每帧同步 | 不直接读运行时单例 → 这个类能进"纯逻辑自检"那份编译清单 |

接线（`PlayerAiRuntime`）：

1. `ResolveTreeHost()` 三级：绑定的宿主 → 首个就绪角色 → **无角色宿主**。
   注意无角色宿主**不写进 `TreeHost`**：一旦缓存下来，等真角色出现时就永远轮不到它接管；
2. 没有世界时的帧首：`ReleaseAll()` 之后**照样 tick 无角色宿主**，并在它前面补一次
   `ApplyPendingReloadsNow()` —— 主菜单里"推送改动"也该生效；
3. 角色就绪的那一刻：`StopUiTree("player ready: …")` 并写一条事件日志。
   菜单树不"搬"到角色上（运行态属于旧运行时，搬过去只会让状态机各说各话）——
   进世界之后要跑什么，用「播放」重新切一次，语义清楚。

`ai.status` 的 `host` 里多了 `kind`（`player` / `menu`），前端徽标据此写
`树：运行中（主菜单，无角色）enter.game.scbtpak（tick 12）`；另外顺手修了前端一个真 bug：
游戏侧的 `tree.source` 是**绝对路径**，而编辑器手上是文件名 —— 直接比会永远不相等
（"暂停"永远变不成，"切过去"反而每次都发）。现在按文件名比。

**实测（真游戏、主菜单、没进世界）**：

```
/api/game/status → ok=true mode=idle
                   host={"name":"menu","kind":"menu","enabled":true,"ready":true,"hasTree":false}
POST /api/game/tree/switch?path=…\test.action.scbtpak → {"ok":true,"switched":true,"reason":"prepared on demand"}
/api/game/live   → ok=true 活动树=test.action running=true ticks=219 → 1.5s 后 489（树真的在主菜单里跑）
```

**验证**：游戏侧纯逻辑自检 436 → **443/443**（新增 8 条：菜单宿主自报 kind/name、没有世界也能
`switch` 进树、`Tick` 真的推进、树里的 `PlayActionPackage` 在**没有任何角色**的情况下把输入
交给注入层、`ai.tree.snapshot` 在菜单里答得出、`ai.stop` 后释放并清空、`ai.status` 报 kind=menu）；
编辑器自检 100/100、前端无头自检 355 → **358**（新增"主菜单跑树"徽标 + 绝对路径也能认出是同一棵树 +
徽标只写文件名）、真浏览器自检保持全绿。

**顺手做的一件工程清理**：纯逻辑自检的 harness 生成脚本原来只存在于
`%TEMP%\pa_build_test.py` —— TEMP 一清自检就跑不了，而且改源码时很容易忘了同步它的编译清单
（新增的 `UiTreeHost.cs` 就是这么被漏掉的）。现在它是仓库里的
`Mod/Packages/pa_build_test.py`，`check_player_ai_build.py selftest` 直接跑它。

### 9.5.24 停止按钮（= 重置，从头跑）+ 画布只亮"正在执行"的那一个节点

用户反馈两点：①"可以暂停、暂停后还保持到执行的位置，但需要一个停止按钮，这样我才能重置行为树
从头开始跑"；②"执行到对应位置时画布中对应节点亮起（边框加高亮、亮绿色），**只亮正在执行的**，
不需要亮已执行的"。

**① 停止**：新增命令 `ai.tree.stop`（`TreeStop`）——`host.StopTree()`：卸下树、释放输入、
写事件日志。与"暂停"的分工就是用户要的那个：

| 动作 | 运行态 | 再播放时 |
|------|--------|----------|
| 暂停 | **保留**（继续 = 从原处接着跑） | —— |
| 停止 | **丢掉**（回到"什么都没跑"） | 从根开始 |

编辑器侧加了 `POST /api/game/tree/stop` 与工具条上的「⏹ 停止」按钮（有树在跑/暂停时才可点），
点完提示"再点「▶ 播放这棵树」就是从头开始跑"。

顺手修了一个真 bug：`BtRuntime.ReplaceRoot(..., migrate:false)` **只重置节点状态、不重置运行计数**
（tick / 时间 / 循环数）—— 于是"停止后重播"看起来像是没重置（实测：停止再 switch，`ticks` 还是 6）。
现在没有迁移（= 从头开始）时也 `ResetRunState()`；热重载走 `migrate:true` 那条路，进度照旧保留。

**② 只亮正在执行的节点**：`ai.tree.snapshot` 的 `path` 数组本来就是"当前 `IsActive` 的节点"
（前序遍历），于是**最后一个**就是最深、真正在跑的那个。前端改成：

- 只给这一个节点加 `.live-active`：亮绿色（`--live-accent`，深色主题 #35d07f / 浅色 #14a35c）
  2px 边框 + 外发光 + 轻微呼吸动画（`prefers-reduced-motion` 下不动画）；
- **删掉 `live-path` 那一层**（以前路径上的祖先会挂一层暗色高亮，看着像"好几个都在跑"）；
  预览区/缩进树里"路径上"的标记也一并去掉，"活动节点路径"仍留在实时监视面板的文字里；
- 拿不到 `path` 明细（老版本 Mod）时退回路径字符串的末位 —— 只亮一个，不亮一片。

**实测（真游戏、主菜单）**：

```
① switch  test.action → switched=true，ticks=220 running=true
② stop               → {"stopped":true,"host":"menu","mode":"idle","hasTree":false}，ticks=0 running=false
③ 再 switch          → ticks 立刻 = 2（**归零**），1 秒后 180   ← "重置、从头开始"就是这条
④ 再 stop            → mode=idle paused=false（干净收工）
```

**验证**：游戏侧纯逻辑自检 443 → **447/447**（新增：`ai.tree.stop` 卸下树 + 释放输入 + 报 idle、
重复停止如实说"没有树可停"、停止后再切 tick 归零）；编辑器自检 100 → **102/102**（`/api/game/tree/stop`
把游戏的 `stopped/mode/hasTree` 原样带回、请求发的就是 `ai.tree.stop`）；前端无头自检 358 → **365**
（停止按钮的可点状态与请求、`liveActiveChain` 三条：只取最深 active / 已执行的不亮 / 无明细时退回末位）；
真浏览器自检 149 → **154**（按钮文案与真实可点状态、画布上 `.live-active` ≤ 1 且 `.live-path` = 0）。

### 9.5.25 "第二次播放不执行"：三个真原因（都是状态没清干净）

用户报了两条：①播放→暂停→停止→再播放，界面显示"已暂停"；②进世界后停止、回主菜单再播放，
**没有被执行**。查下来是三个独立的原因，都能用日志与实测钉住：

**① 暂停是全局的，停止没有清它**（就是①的直接原因）。`PlayerAiRuntime.Paused` 在帧首最先判断，
暂停中 `TickFrameStart` 直接 return —— 于是"停止后再播放"只是把树**装**回去，永远不会被 tick，
`ai.status` 的 `paused` 还是 true，界面自然写"已暂停"。改法：
- `ai.tree.stop` 顺手 `context.Resume(...)`（回包里多一个 `resumed` 字段）——**停止 = 完全归零**；
- 编辑器的「▶ 播放」在切换成功后，若发现游戏仍处于暂停就自动 `ai.resume` 并说明原因；
- 「试跑」为了不跟树抢输入会自动暂停，现在**回放一结束就自动恢复**（原先只在状态栏提醒一句，
  用户没点恢复的话，之后所有"播放"都不执行 —— 这正是②的常见触发路径之一）。

**② `TreeHost` 绑在角色上，回到主菜单后那个宿主永远不会被 tick**（②的另一个原因）。
`ResolveTreeHost()` 原来第一句就是"绑定且 Enabled 的宿主直接返回"。进过世界之后它绑在 `AiActor` 上；
回主菜单时角色随世界消失、不会再被 tick，但函数还是把它返回出去 → `ai.tree.switch` 把树装进一个
**死宿主**：事件日志里 `[switch] … prepared=True` 一条条成功，游戏里什么都不发生（实测就是这么报的）。
改法：宿主解析**跟着"现在有没有世界"走** ——
- 有世界：优先就绪角色；没有就绪角色时保留绑定（命令层要能如实报 not_ready）；都没有 → 无角色宿主；
- 没有世界：只能用无角色宿主，并**清掉指向角色的陈旧绑定**，同时写一条
  `[host-unbind] world unloaded: dropping the stale player host`（排查时一眼能看到）。

**③ 启动包会顶掉用户正在跑的树**：`AutoLoadTreeOnStart` 在世界就绪时无条件装载
`PlayerAiConfig.StartupTreePackage`，日志里就是进入世界后紧跟一行
`[load] Initial demo.greet.scbtpak -> replaced` —— 用户"播放 enter.game 进游戏"，一进世界
那棵树就被 demo.greet 换掉了。现在**宿主已经在跑树就跳过**（写 `[startup-tree-skip]`），
启动包只负责"什么都没跑时给个默认的"。

**实测（真游戏、主菜单，用户报的那串操作）**：

```
① 播放 test.action → switched=true          ticks 7 → 195（在跑）
② 暂停             → paused=true            ticks 冻在 197（运行态保留）
③ 停止             → {stopped:true, **resumed:true**, paused:false, hasTree:false, mode:idle}
④ 再播放           → switched=true          ticks 从 7 开始 → 1.2s 后 226   ← 真的从头跑起来了
```

**验证**：游戏侧纯逻辑自检 447 → **450/450**（新增：暂停后 `ai.tree.stop` 把暂停一起清掉、
`resumed=true`、暂停标志真的没了）；前端无头自检 365 → **368**（播放时发现暂停会发 `ai.resume`
并说明、徽标写"（AI 已暂停，播放时会自动取消）"、试跑回放结束自动恢复）。
②③ 属于"要看世界切换"的行为，自检覆盖不到运行时（那份工程不含 `PlayerAiRuntime`），
所以靠**事件日志 + 实测**兜：回主菜单时日志里应出现 `[host-unbind] world unloaded…`，
进世界时应出现 `[startup-tree-skip] host already runs enter.game.scbtpak`。

### 9.5.26 行为树绑"控制器"，不绑角色（架构调整）

用户的原话："行为树不应该绑定到角色上，而是角色的控制器 —— 例如还没进入游戏的时候，也是要操作
按钮的，退出世界了，也是要执行其他操作的。"

这一条把之前两轮"打补丁"的做法（菜单宿主 / 角色宿主来回切）彻底换掉了：

| | 以前 | 现在 |
|---|---|---|
| 树 / 黑板 / 模式层在哪 | `AiActor`（世界里才存在），没世界时临时给个 `UiTreeHost` | **`ControllerTreeHost`**（一直存在） |
| 世界切换时 | 换宿主：菜单树被停掉、进世界重装、回菜单再摘 | **只换输入路由**（`Bind`/`Unbind`），树不卸载、运行态保留 |
| 输入从哪出去 | 角色执行器，或 UI 兜底（两套宿主各一份） | `ControllerActuator`：有世界转发玩家执行器，没世界只点 UI（同一个对象） |
| 传感器 | 角色的 | `ControllerSensor`：有世界转发，没世界 `IsReady=false`（漂移检查自动跳过） |
| `ai.enable/disable` | 翻角色的开关 | 翻**控制器**的开关（世界外也有意义：允许点 UI） |
| `ai.status.host` | `kind=player` / `kind=menu` | `kind=controller` + **`situation`**（`world`/`menu`）+ `player` |

代码落点：新增 `Actor/ControllerTreeHost.cs`（宿主 + 模式层 + 帧首 tick）、
`Actor/ControllerInput.cs`（转发式执行器/传感器）、`Actor/IPlayerInputProvider.cs`（"世界输入来源"接口）；
`Actor/AiActor.cs` 缩成**世界输入来源**（玩家绑定 + 传感器/执行器 + `ReleaseInput`，不再持有树/黑板/状态机）；
`PlayerAiRuntime.TickFrameStart` 现在**每帧只 tick 控制器一次**（世界内外都一样），并在帧首
`SyncWorldInput()` 按"有没有世界 + 有没有就绪角色"接上/摘下输入来源；`PlayerAiComponent` 的自动接管
改成 `runtime.EnableController(...)`。

**顺带发现并修掉的一件事**：行为树默认是**循环**的（UE 语义，`BtRuntime.Tick` 里"跑完 → 重开"），
所以"进游戏"这种菜单宏会**每隔几秒再点一次 Play**（实测：树在菜单里循环点了十几轮）。
新增包级属性 **`Root.loop`**（bool，默认 `true`）：
`loop:false` = 一次性，跑完由运行时把树停掉（`IsRunning=false`）。`enter.game.scbtpak` 已按
`loop:false` 重新生成 —— 实测 `t=5s` 时 `running=false / lastResult=Succeeded / ticks 冻住`，不再循环。

**验证**：
- 纯逻辑自检 458 → **461/461**：新增"根 loop=false 一次性停 / 默认循环树照旧 / 停了的树不再跑第二轮"，
  以及上一节的绑定断言（绑上/摘下玩家时**同一棵树继续跑、tick 继续涨**、世界里输入走玩家执行器、
  世界外按键注入是空操作、`situation` 与 `player` 如实上报）共 7 条；
- 真游戏（主菜单实测）：`host={"name":"controller","kind":"controller","situation":"menu","ready":true}`，
  播放 `enter.game` → 树在菜单里跑起来（ticks 180→725）→ 到 `Wait(2)` 结束**自己停下**（`running=false`）；
- `check_player_ai_build.py all` 全绿：461/461、editor 102/102×2、ASSETS 0、BROWSER 154/154、
  web 368/368、WIRING 0。

**没能当场验证的一环（如实记下）**：菜单 → 世界里那一步我这边跑不通 ——
`act.uiclick` 点 `WorldsList` 之后 `selectedIndex=None`（连既有的 `Mod/Packages/enter_world.py`
也卡在同一处），所以录制的 `进入游戏.scatpak` 第二步（选世界）点不动。这属于 **CmdBridge 的 UI 自动化**
问题，跟这次架构调整无关；人工进世界之后，编辑器徽标应从"（控制器·主菜单）"变成"（控制器·<玩家名>）"
而**树继续跑**（ticks 不归零）—— 那正是这次改动的验收点。

### 9.5.27 "光标乱跳、菜单没反应"：控制器重构时引入的一处回归（已修）

用户反馈："点击播放进入世界的树，看不到操作菜单，只看到原本的控制器的手指样式的光标乱跳了几下。"

**原因（我这次重构引入的）**：`PlayerAiRuntime.ReleaseAll()` 里顺手加了
`m_controller.ReleaseInput()`，而这个函数在**世界外每一帧**都会被调用（"没有世界 → 释放角色输入"那条分支）。
控制器的释放又会一路传到 `UiOnlyActuator.ReleaseAll()` → `CmdBridgeInput.ReleaseAll()` ——
UI 那套"释放"的语义是**取消整个软光标注入会话**，而一次点击要走"移动 → 按下 → 抬起"好几个帧：
**每帧被清一次 ⇒ 永远派生不出 Click**。表现就是光标移过去了（位置确实被设过）、界面纹丝不动。

**改法**（把两种"释放"分开，语义写进注释）：

| 调用方 | 现在释放什么 |
|--------|--------------|
| `IAiActuator.ReleaseAll()`（任务收尾、动作包回放结束、运行时世界外每帧） | **只放世界输入**（玩家按住的键/鼠标） |
| `ControllerTreeHost.ReleaseInput()`（停止树 / 未就绪 / `ai.input.release` / `StopEverything`） | 世界输入 **+ 显式取消 UI 注入** |

即：`ControllerActuator.ReleaseAll()` 不再动 UI，新增 `CancelUiInjection()` 只给上面那几条显式路径用；
`PlayerAiRuntime.ReleaseAll()` 回到"只释放角色"，全停用新增的 `StopEverything()`。

**验证（真游戏，端到端）**：

```
播放 enter.game（主菜单）→ t=0.6s screen=Play → t=1.2s screen=Game worldLoaded=True   ← 真的进世界了
ai.status：host={"kind":"controller","situation":"world","player":"host","ready":true}   ← 同一次运行里
           tree=enter.game running=False ticks=275                                    ← 还是同一棵树
日志：      [startup-tree-skip] host already runs enter.game.scbtpak   ← 启动包不再顶掉用户的树
            actor attached: AiActor(host idx=1 local=True)                ← 进世界后控制器接上玩家
```
自检补了回归：`a task-level ReleaseAll does not cancel the UI injection` +
`an explicit ReleaseInput does cancel it`（纯逻辑层就能钉住，不必靠真机）。

**顺带记下两件事**（都不是这次改动的锅，但要用户知道）：
1. 进世界之后这棵树最后是 `lastResult=Failed`，日志写明
   `replay drift: position drifted 37.24m from the keyframe (tolerance 3.0m)` ——
   `进入游戏` 这类**录下来的**动作包带的是**绝对世界坐标**关键帧，换个位置回放必然判漂移失败
   （设计如此："漂移即失败，不做瞬移修正"）。菜单宏要么录成"只带起点一条关键帧"（像
   `sample_walk` 那样），要么把节点的 `abortOnFail` 关掉。
2. `sccmd act.uiclick`（那条"一步到位"的注入路径）在当前环境里**点了不生效**（光标到位置、界面无反应），
   而动作包/行为树走的是 `UiQueueClick` 会话路径（一步一帧）——**后者是好的**，前者待查。

**顺手的一个实验事故**：我用 `act.key Escape` 探测键盘注入时，主菜单里的 Escape 直接把游戏退出了
（日志 `Saved settings`）——记一笔，别在菜单里随手发 Escape。

### 9.5.28 "改过窗口大小就播放失败"：三处一起治（UI 真实位置 + 时钟 + 点击重试）

用户原话：「之前不是让获取按钮所在位置的坐标吗，例如 `Button("Play").getLocation()` 这样来获取。
能获取 ui 的时候，尽可能使用 UI 的真实位置。避免我把窗口长宽调整了导致播放失败。」

这条做完了，但过程中挖出**三个叠在一起的真原因**，缺一个都还是"光标动了一下、界面纹丝不动"：

**① 控制器未接管时每帧 `ReleaseInput()`（真凶）**
`ControllerTreeHost.Tick` 的 `!Enabled` 分支每帧都跑（启动后还没进世界、`ai.disable` 之后都是这个常态），
而 `ReleaseInput()` 会 `CancelUiInjection()` —— UI 那套"释放"的语义是**取消整个软光标注入会话**。
一次软光标点击要跨好几帧（`UiMouseSession` 一步一帧：移动 → 按下 → 抬起），每帧被清一次，
引擎永远派生不出 `Click`。现在只在"刚变成未接管"的那一帧释放一次（`m_releasedForDisabled`）。
判据很好认：`ai.status.host.enabled=false` + `situation=menu` 时，注入的按下状态活不过一帧。

**② 回放/录制的时间轴不是真实时间（`frames=723 / duration=2.231s` 露的馅）**
帧首泵靠 `AutoResetEvent` 逐帧举手、后台线程收信号再派发，**信号会合并**：泵慢一拍就少跑一帧 tick，
可每次 tick 拿到的 `Time.FrameDuration` 仍然只有"它自己那一帧"的时长。于是 AI 的时间轴比真实时间慢
——上一版进游戏包 `frames=723, duration=2.231s`，而 723 帧在当时的 ~180 FPS 下是 4.0s，**只走了 56%**。
回放时泵不漏帧、时间轴≈真实时间，包里"隔 0.45s 点世界列表"就撞进 Play 屏的切场动画里。
现在 `ScheduleFrameStartTick` 用单调时钟量"距上次 tick 的真实时间"（`ConsumeRealDelta`，clamp 0.25s）。

**③ 被拒绝的 UI 点击直接丢掉（顺序还会乱）**
`ScatPlayer.FireUiEvents` 原来"到点打一枪"，引擎如实回 `rejected (missing/occluded)` 就记一句警告算了。
而语义目标（`Play` / `list:WorldsList@世界名`）本来就能随时现解析 —— 现在改成**重试**：
没点成的点击挂在 `m_pendingUiClicks` 里逐帧重试（默认 5s），并且**只要还有没完成的点击就不放行后面的点击**
（顺序铁律：`选世界` 必须发生在 `Play!` 之前）。成功/放弃都会各记一条日志。

**顺手补上的**：`list:WorldsList@<文字>` 的行文字**从游戏里查**再写进包，不手打 ——
第一版把 `Rebritish` 打成了 `Rebristish`，回放时引擎如实回一句 `rejected (missing/occluded)`，
看起来像"点击机制坏了"，其实是名字对不上（`Mod/Packages/out/convert_enter_game_events.py` 现在会先
`obs.ui` 拉一遍列表、逐字核对，改名/改列表都能立刻发现）。

验收（`Mod/Packages/out/verify_enter_game_tree_resized.py`，把窗口缩到 1084x661 之后再跑整棵树）：

```
screen        : MainMenu worldLoaded = False
resize        : client = (1084, 661)
switch        : {"mode": "tree"}
   t=  1.0s screen=GameLoading worldLoaded=False ticks=181 active=… Task.PlayActionPackage#enter
   t=  2.0s screen=Game      worldLoaded=True  ticks=273 active=<none>
host          : {"name":"controller","kind":"controller","enabled":true,"ready":true,
                 "hasTree":true,"situation":"world","player":"host"}
```

配套的两条：
· 录制的世界行不再记像素（`record_enter_game.py` / `enter_world.py` 都改成
  `act.uiclick selector=WorldsList text=<世界名>`），录出来的事件是 `click:list:WorldsList@<世界名>`；
· 已经录好的 `进入游戏.scatpak` 用 `convert_enter_game_events.py` 原地换掉那条死像素
  （原包另存 `.bak-像素`），其它轨道一个字节没动。

回放能点中的几何前提（也顺便回答了用户那句"用 UI 的真实位置"）：`obs.ui`/点击解析用的是
`Widget.GlobalBounds`，它是**已经乘过 `ScreensManager` 布局缩放**的客户区像素
（`ScreensManager.cs:420-430`：`num = 850/Clamp(UIScale,0.5,1)`，`RootWidget.LayoutTransform = Scale(num2)`），
所以窗口一变大小，同一个控件解析出来的点自然跟着变 —— 实测同一行：旧 1920 宽窗口 `570.6`
vs 现在 1084 宽 `351.8`。

### 9.5.29 UI 定位 / 点击**服务**（`ui.locate` / `ui.clickelement`）+ 编辑器拾取面板 + `Task.UiClick`

用户原话：「把相应的方法做成 CmdBridgeMod 能提供的服务，在行为树编辑器中，要能够使用来获取坐标
或点击对象。避免硬编码由于分辨率变化或窗口尺寸变化导致无法使用，最好可以通过参考按钮按下的事件，
在可行的情况下，直接调用发出按钮已按下。」

**服务端（CmdBridgeMod，谁都能用）**

| 命令 | 作用 |
|------|------|
| `ui.locate target=<语义目标>` | 解析目标 → **当前**客户区坐标 + 路径/类型/文字 + `hittable`/`clickable`/`blockedBy`；列表行另给 `list:{index,text,count}` |
| `ui.clickelement target=<语义目标> mode=direct\|input\|invoke` | 真的点它（`auto` = 能发事件就发事件，否则 `direct`） |
| `obs.ui` / `ui.elements` | 当前屏幕上可交互元素的清单（编辑器拾取面板的数据源） |

目标写法（**语义优先，坐标只是兜底**）——解析实现在 `CmdBridgeMod/Server/UiTarget.cs`，
它是**唯一**一份（PlayerAiMod 里那份重复实现已删除），并且被编进离线自检：

| 写法 | 含义 |
|------|------|
| `Play` | 控件名或文本（当前屏幕里必须唯一，歧义如实报错，不猜） |
| `[MainMenuScreen#0]/…/Play` | 完整路径（最精确；编辑器拾取出来写进树的就是这个） |
| `list:WorldsList@Rebritish` | 列表里文字含 `Rebritish` 的那一行（跟着列表内容走） |
| `list:WorldsList#0` | 列表第 0 行（跟着滚动位置走） |
| `1010.6,64.83` | 客户区坐标 —— **不推荐**，窗口一变就点到别处 |

命令名全小写是**协议要求**：控制通道在解析请求时会把命令名 `ToLowerInvariant()`
（`ControlServer.cs:341`），驼峰写法只会在 `cmd.list` 里好看，调用一律 `unknown_command`（实测踩过）。

**三种点法（`mode`）**

| mode | 怎么点 | 说明 |
|------|--------|------|
| `direct`（默认） | **单帧合成"按下→抬起"** | 只往输入层写一次：`m_mouseDownOnce[Left]=true`、down 数组保持**没按**、`m_mouseDownPoint/m_mouseDownButton` 摆好、软光标定位到目标 → 引擎自己的 `UpdateInputFromMouse` 在同一帧派生 `Tap`+`Click`。控件自己的逻辑（`IsClicked`、列表选中+`ItemClicked`、点击音）照常跑 |
| `invoke` | **直接触发控件自己的按下事件** | 目前引擎里唯一带点击事件的是 `ListPanelWidget.ItemClicked`。⚠️ 两个坑：① `PlayScreen` 的处理器写着 `if (selectedItem == item) Play(item)`，而 `SelectedItem` 是 `ListPanelWidget.Update` 在**发事件之后**才更新的 —— 所以必须先摆 `SelectedIndex` 再发事件，否则点不动；② 这实质替界面做了"选中"这个决定（等价于点已选中的那一行 = 直接进世界），**绕过输入层**，不给 AI 当默认 |
| `input` | 多帧软光标会话（移动→按下→抬起，一步一帧） | 保留对照/兼容 |

**为什么 `direct` 是"单帧"而不是"同帧按下再抬起"**：引擎的 `Click` 派生条件是
"这一帧**没按着** + 上一帧留了按下起点"（`WidgetInput.cs:755-769`）。同帧把 down 数组按了又松，
两件事在同一帧里看不到先后，于是什么也派生不出来（早期"直注入点击没反应"就是这个原因）。
把"按过一下"只写进 **downOnce** 数组，`Tap` 与 `Click` 就在同一帧同时成立，和真人快速点一下等价。

**踩到的坑（值得记）**：这次按下**绝不能**登记进 `m_heldButtons`。共控合并
（`focus.attach`，或 `auto` 且真实焦点恰好在游戏里）每帧会把 down 数组重算成
`IsMouseButtonHeldByInjection(i) || 真实按下`（`FocusPolicy.MergeInjectedWithReal`）——
登记成 held 就等于告诉它"这个键还按着"，down 数组被抬成 true → `Click` 分支不成立 → 界面纹丝不动。
症状很有迷惑性：`auto` 模式下**第一次点击时好时坏**（取决于真实焦点在谁那儿），`focus.detach` 之后
同样的调用立刻就好。现在验收脚本**两种模式都跑**（见下）。

**编辑器侧**

- `GET /api/game/ui/elements` —— 元素清单；每个元素带上 `target`（语义目标，写进树用）
  与 `rowTarget`（列表行）。注意游戏返回的 `elements` 是**一段 JSON 文本**
  （`GameBridgeClient.Collect` 把数组按 `ToJson` 收成字符串），编辑器必须先解析回数组，
  否则前端 `forEach` 直接炸（实测元素个数变成 9593 = 字符串长度）。
- `GET /api/game/ui/locate?target=…`、`POST /api/game/ui/click {target, mode}`。
- 检查器里多了「**UI 拾取**」面板：`⟳ 拾取界面元素` 列出当前屏幕可点的东西（**真实坐标** + 可点性），
  点一行 → 目标框填**语义目标**；`定位` 看它现在在哪；`点一下` 在游戏里真的点它（可选点法）；
  `填进树` 把它写进选中的 `Task.UiClick`（没有就新建一个挂上去）。**写进树的永远是语义目标，不是像素。**

**行为树节点 `Task.UiClick`**（属性：`target` 必填、`mode`（默认 `direct`）、
`waitSeconds`（默认 3，目标还没出现就等它就绪再点，等不到才 Failed）、`repeat`）：

```json
{ "id": "clickWorld", "type": "Task.UiClick",
  "properties": { "target": "list:WorldsList@Rebritish", "mode": "direct", "waitSeconds": 3 } }
```

`PlayerAiMod` 里两条执行器（世界内 / 世界外）现在都把目标**原样转发**给这个服务，
自己不再解析目标类型 —— 两边各解析一次曾经就是"编辑器能点、回放点空"的来源。

**验收**（`Mod/Packages/out/verify_ui_service.py`，先把窗口缩到 1084x661）：

```
ui.locate Play 给出真实坐标                 PASS {'x': 394.7, 'y': 459.7}
ui.locate 说得出它能不能点                   PASS clickable=True blockedBy=None
focus : attach（共控合并模式，最容易被合并逻辑吃掉）
ui.clickElement Play (direct) 在共控合并模式下也能点动  PASS MainMenu -> Play
focus : detach（脱离模式，真实鼠标切断）
世界列表出现                               PASS ['Rebritish', 'CmdBridgeTest', …]
ui.locate 列表行给出真实坐标                  PASS list:WorldsList@Rebritish -> {'x': 351.75, 'y': 36.6}
ui.clickElement 列表行 (direct) 选中了那一行     PASS selectedIndex=0
ui.clickElement Play! (direct) 真的进了世界    PASS screen=Game
编辑器 /api/game/ui/elements 列出元素（真数组）  PASS 18 个元素
拾取到的元素带语义目标（能直接写进树，不是像素）      PASS [GameScreen#0]/…/Back
编辑器 /api/game/ui/locate 能解析这个目标        PASS {'x': 0, 'y': 0}
FAILURES: 0
```

自检也跟着长：纯逻辑 **474/474**（`UiTarget` 的解析现在钉在 CmdBridge 那份实现上）、
编辑器 **106/106**（新增"编辑器把 `ui.locate` / `ui.clickelement` 连目标带点法发出去"两组）、
真浏览器 **159/159**（新增 5 条：面板存在 / 游戏没在跑时如实报错 / 拾取结果带真实坐标 /
点一行填的是路径不是像素 / 「填进树」新建 `Task.UiClick` 并写对属性）。

### 9.5.30 "游戏已经启动了，但树无法点击播放，提示游戏没在跑"

用户实测报的现象。查下来是**编辑器前端的状态缓存没人刷新**（编辑器与游戏本身都是好的：
`/api/game/process` 说 `running=true channelConnected=true`，`/api/game/status` 也能拿到 `ai.status`）。

根因链条：
1. 树徽标与「▶ 播放」的判断全看 `state.gameStatus`（`treeRunState()`），而它**只在**
   页面加载、换包、点播放/暂停/停止之后才刷新；
2. 游戏是**在页面之外**起来的（手动双击 exe、或先开编辑器再开游戏）时，那份缓存永远停在
   `game_unreachable` → 徽标一直写「树：游戏没在跑」；
3. 而 `updateTreeRunUi()` 里 `button.disabled = … || !st.connected` 把播放按钮**灰掉** ——
   于是用户的感受就是"点不动"，而且没有任何入口能自己恢复（只有手动点一次「游戏状态」才会刷新）；
4. `waitForGameChannel()`（启动游戏后的轮询）连上通道时只调了 `loadMeta()`，
   而 `loadMeta()` 只更新游戏徽标/实例根，**不碰** `state.gameStatus` —— 走「启动游戏」这条路也会中招。

四处一起改（`PlayerAiEditor/Web/app.js`）：
- **播放按钮不再因为"缓存说没在跑"而灰掉**：只有"正在通信"或"没有打开的包"才禁用；
- `treeRunPrimary()` 在 `!connected` 时**先重新确认一次**再决定（`treeRunPrimaryAfterRefresh()`），
  确认后仍没在跑才如实报"游戏没在跑 + 该怎么办"；
- 新增**低频游戏状态哨兵** `pollStatusQuiet()`（3s，进程 + 游戏状态两个端点一起刷），
  页面加载后自动开跑；打开「实时监视」时停掉（那条 700ms 已经在刷），关掉监视再接手；
  标签页隐藏时不打，`visibilitychange` / `window.focus` 时立刻补一次
  —— 正好对应"在游戏那边点完、切回编辑器点播放"这个动作；
- `waitForGameChannel()` 连上通道时补一句 `refreshGameStatusQuiet()`。

真浏览器自检 +3 条（**163/163**）：缓存写着"游戏没在跑"时播放按钮仍可点、
点它会先打 `/api/game/status` 再决定、哨兵一次会同时刷进程与游戏状态。
另外把原来那条"游戏没在跑 → 播放按钮禁用"的用例**改成"仍可点、标题说明会重新确认"**
（旧断言正是这次要修的行为）。

### 9.5.31 落点标记（2s / 5px 红点）+ 三个"点一下没反应"的真原因

用户原话：「我在UI拾取的里面拾取界面元素后，点击选择morebutton后，点定位，在游戏中看不到明显的标记，
可以在点击定位处渲染2s直接5像素的红色圆点，方便定位。现在点击点一下，并没有点击效果。」

**① 落点标记**（`ui.locate mark=true` / `ui.clickelement mark=true`，编辑器默认开，可关）

`UiMarker`（新文件）往 `ScreensManager.RootWidget` **末尾**塞一个自己的小控件
（子控件顺序＝绘制顺序，最后一个画在最上面），只在 `Draw` 里往 2D 批次塞一个红方块 + 1px 深红外框：
· `IsHitTestVisible = false` —— 覆盖层绝不吃点击；
· `DesiredSize` 必须非零（`Widget.CollateDrawItems` 会用 `GlobalBounds` 与屏幕求交，空尺寸会被直接跳过）；
· 坐标换算：RootWidget 的子控件在设计坐标里，`GlobalScale` 才是"设计 → 客户区像素"的比例；
· 2 秒后自己在帧首摘掉（`ui.marker` 命令可查 `shown/drawFrames/expiresIn` —— `drawFrames>0`
  才算"真的画出来了"，而不是"命令返回了、屏幕上什么都没有"）。
屏幕外的点**不画**，并如实回 `marked=false, markSkipped=…`。

**② 世界内按钮的输入面写错了层**（真凶之一）
`WidgetsHierarchyInput` 是**一层一份**：主菜单的屏用根输入面，世界内 HUD 用
`GameWidget` 自己的那层（`GameWidget.cs:104-106`）。软光标位置 / `IsMouseCursorVisible` /
`m_mouseDownPoint` 都是每层一份 —— 以前 `direct` 写的是**根**输入面，所以 HUD 按钮那一层
什么都没变，派生不出 `Click`。现在按"命中控件所在的层"写（`target.Input`）。

**③ 落点落在按钮中心盖着的图标上**（真凶之二）
`ClickableWidget.Update` 判点击的条件是 `HitTestGlobal(Click.Start) == this`；
而复合按钮（`BitmapButtonWidget`）内部**包着一个** `ClickableWidget`
（`BitmapButtonWidget.cs:12,18`：`IsClicked => m_clickableWidget.IsClicked`）。
所以规则是"落点要命中**目标自己或它子孙里的 ClickableWidget**"：中心不行就在矩形里按
5×5 网格扫一圈（内缩 15%）；回包给出 `clickTarget` / `clickTargetOnTarget` / `clickPointAdjusted`，
一眼能看出"到底点在谁身上"。

**④ 点不到就说点不到**（真凶之三，也是"看不到标记"的原因）
`MoreButton` 这类世界内 HUD 控件在 **Windows 上是触屏专用**的：
`ComponentGui.UpdateSidePanelsAnimation` 在非触屏且无模态面板时把 `m_sidePanelsFactor` 拉到 1，
整条控制栏被 `RenderTransform` **平移到屏幕外**（实测坐标 x=2852、屏幕只有 1920 宽）。
以前"中心点"会照点，于是点到左上角某个控件 —— 用户看到"点了没反应"，回包却写着命中了别的控件。
现在：
· `obs.ui` 的 `clickReason` 说人话（`off-screen: center (2852.9,55.3) is outside the 1920x1080 screen area - …`
  / `zero size (not laid out yet…)` / `blocked by X` / `mouse cursor is captured…`）；
· 编辑器拾取列表把点不到的行**压暗 + 行尾写明原因**，鼠标悬停有完整原因；
· `ui.clickelement` 在"落点上什么都没有"时**拒绝**（`element_off_screen`）并告诉你去按「定位」看原因，
  绝不再点屏幕角落糊过去。

验收（`Mod/Packages/out/verify_ui_marker_and_hud_click.py`，世界里跑）：

```
世界内 HUD 里有 MoreButton                       PASS [GameScreen#0]/…/RightControlsContainer/…
ui.locate 命中 MoreButton 并给出屏幕坐标            PASS {'x': 1869.4, 'y': 55.3}
ui.locate 报告已经亮起标记                        PASS marked=True skipped=None
标记控件真的被引擎画出来了（drawFrames>0）              PASS drawFrames=73 shown=True expiresIn=1.60s
标记就是 3 项要求：5 像素 / 落点一致                  PASS diameterPx=5 point={'x': 1869.4, 'y': 55.3}
2 秒后标记自己消失（不留常驻控件）                     PASS shown=False
-- 世界内模态面板（Esc 菜单）--
Esc 菜单里有可点的 Resume 按钮                     PASS [None, 'Resume', 'Quit', 'More']
ui.clickelement 点的是这个按钮（命中它自己或内部可点件）   PASS clickTarget=BevelledButton.Clickable onTarget=True
点下去真的生效了：菜单关了（Resume 消失）               PASS Resume 还在 = False
FAILURES: 0
```

`MoreButton` 本身也验过是"点得动"的：它是自动勾选按钮，点一下 `checked` 从 `False` 翻到 `True`
（`obs.ui` 直接读得到）—— 它在 Windows 上"看着没反应"是因为那条控制栏本来就是触屏 UI。

自检：纯逻辑 **474/474**、编辑器 **108/108**（+2：定位/点击都要把 `mark` 发出去）、
真浏览器 **164/164**（+1：点不到的元素在列表里写明原因）。

### 9.5.32 物料区翻译 + 中文/English 切换 + 列表行拾取 + 动作包右键（编辑/删除/重命名）

用户这一轮提了四件事，逐条对应：

**① 物料区加一层"只影响显示"的翻译**（`PlayerAiEditor/Web/i18n.js` 的 `NODE_LABELS`）

`Task.Log` 在中文界面里显示成「任务·日志」，英文界面是「Task · Log」；
**写进包的永远是 `node.type` 原文**。物料按钮、画布节点标题栏、缩进树的行标签、属性区的"类型"行
四处统一用 `setNodeLabel()`：显示名一个 `<span class="node-label">`、原始类型一个小字
`<span class="node-raw">`，一眼能分清"哪个是显示名、哪个是包里真正写的东西"。
自检里加了一条"schema 里每个节点类型都有中英显示名"，漏一个就会红。

**② 整个界面在主题旁边切中文 / English**（`#btnLang`）

`i18n.js` 里 `STRINGS.zh` / `STRINGS.en` 两张表 + `t(key)`（缺 key → 中文兜底 → key 本身）。
静态文案在 `index.html` 上挂 `data-i18n` / `data-i18n-html` / `data-i18n-placeholder`，
`applyLanguage()` 一次性回填并重画物料/画布/属性/拾取列表；语言记在 localStorage，也能用 `?lang=en`。

**状态提示全部翻了**（这一轮补完）：
- 80 条"整句" → `L('st.NNN', '中文原文')`（`extract_status_keys.py` + `apply_status_i18n.py`）；
- 109 条"拼接句" → **整句模板 + 占位符**：`setStatus('已保存 ' + f + '（' + n + ' 节点）')`
  → `setStatus(I18n.format('st.c001', '已保存 {0}（{1} 节点）', f, n))`
  （`extract_status_all.py` + `apply_status_all.py`）。**中文原文一字不改**，只是把"拼字符串"
  换成"模板 + 值"——因为碎片单独翻译是错的（`' 个节点'` 离开原句没有意义），
  而且中英文语序不同；
- 40 条"三元分支"（`ok ? '校验通过' : ('校验失败：' + n + ' 个错误')`）→ 两个分支各一个模板；
  其中 6 处是"拼接里嵌三元"（`(code === 'not_ready' ? '还不能播放：' : '播放失败：') + reason`），
  由 `apply_status_nested_ternary.py` 改写成"三元选模板"；
- 收尾 `finish_status_i18n*.py` 处理两处分支里的裸中文，并**严格复查**：
  把 `L(...)` / `I18n.format(...)` 的实参先抠掉再看还有没有裸中文 → 现在 **0 处**。

覆盖度有独立检查：`check_i18n_coverage.py` 用 node 把 `i18n.js` 装进沙箱、导出两张表，
再把 `app.js` + `index.html` 里用到的**每个 key** 对一遍（`st.*` 只要求英文，中文原文是代码里的兜底）。
现在 **305 个 key，缺中文 0、缺英文 0 → RESULT: OK**。缺一个 key 不会报错，只会安静地退回中文，
所以这条检查是"英文界面里夹中文"的唯一保险。

> 踩过的坑（都写进了脚本注释）：① 用非贪婪正则找 `setStatus(...)` 会在嵌套调用处截断，
> 把半截表达式当整句改写 → 直接语法错误（已用 `status_i18n.py` 的**括号深度感知**扫描替换掉）；
> ② 补 key 的脚本把行尾写死成 CRLF，而 Web 资源是 LF，`replace` 一次没命中却还打印"新增 N 条"
> → 静默失败（现在插完立刻回读断言，并且按文件真实行尾插）；
> ③ 翻译是"按中文原文"做的，旧版 key 与新抽取对不上 → 按**文本**重新对齐（`realign_status_translations.py`）。

### 9.5.33 进过世界、Quit 回主菜单后「真实鼠标点不动按钮」（每帧释放把按下沿清掉了）

用户报的现象：播放行为树进入地图 → Esc → Quit 回主菜单 → 之后鼠标点那四个按钮
**只有"按下的一瞬间"效果、不会真的触发**，多次点击也没用。

**诊断链**（已实测复现：主菜单里连发 3 次真实鼠标点击，`screen` 一直是 `MainMenu`）：

1. `PlayerAiRuntime.TickFrameStart` 在 `GameManager.Project == null`（主菜单）时**每帧**调 `ReleaseAll()`；
2. `ReleaseAll()` → 角色 `ReleaseInput()` → `CmdBridgeActuator.ReleaseAll()` → `facade.ReleaseAll()`
   → `InputInjector.ReleaseAll()` → `ReleaseAllCore()`，它会把引擎的
   `m_keysDownArray/m_keysDownOnceArray/m_mouseButtonsDownArray/m_mouseButtonsDownOnceArray`
   **整体清零**；
3. 而**真实鼠标的"按下沿"就写在同一个 `downOnce` 数组里**（OS 的 mouse-down 事件写进去）。
   每帧被清一次之后，`WidgetInput.UpdateInputFromMouse` 永远留不下 `m_mouseDownPoint`
   （`WidgetInput.cs:748-765`：`downOnce` 那一帧才写起点），松手那一帧
   `!IsMouseButtonDown(Left) && m_mouseDownPoint.HasValue` 不成立 → **派生不出 `Click`**；
4. 只剩 `Press` 成立（`Press` 只看 down 数组，那个由真实事件在按住期间保持为真）——
   于是表现就是"按钮有按下视觉、但点不动"。**完全对得上用户的描述。**

为什么"进过世界才有"：`m_actors` 只在世界加载时被填（`PlayerAiComponent`），
退出世界时 `Disable()` 只调 `Actor.ReleaseInput()`、**不把 actor 从列表里摘掉**，
所以回到主菜单后那个"每帧释放"才开始真的有东西可放（空列表时释放什么也不做）。

**两处一起改，缺一个都还会犯**：

- **① 窄释放**（根因）：`InputInjector.ReleaseInjectedOnly()` —— 例行释放**只放掉"本 Mod 注入并按住"
  的键与鼠标**（遍历 `m_heldKeys`/`m_heldButtons` 逐个清），不再整体清空设备数组；
  `CmdBridgeInput.ReleaseInjectedInput()` 把它做成门面；
  `CmdBridgeActuator.ReleaseAll()` 改走窄释放。**宽释放 `ReleaseAll()` 保留**，只给
  "彻底停手"（`StopEverything()` / `ReleaseAllImmediate()` / Mod 卸载）用。
- **② 边沿触发**（防御）：`TickFrameStart` 的世界外分支改成**只在刚回到世界外的那一帧释放一次**
  （`m_releasedForNoProject`），和之前 `ControllerTreeHost.Tick` 的 `!Enabled` 分支同一个道理 ——
  那个分支在主菜单是"每帧都会走到的常态"，任何"每次都要清一遍"的动作都会踩到真实输入。

**验收**（`Mod/Packages/out/` 里两个脚本，一前一后）：

```
py Mod/Packages/out/reach_menu_after_world.py     # 进世界 → Esc → Quit 回主菜单（复现用户状态）
py Mod/Packages/out/verify_menu_real_click.py     # 用**真的 OS 鼠标事件**（SendInput）点 Play
```

修复前：连点 3 次 → `screen = MainMenu`（FAIL，复现成功）；
修复后：第 1 次 → `MainMenu -> Play`（PASS）。脚本还会检查窗口是否真在前台
（否则真实鼠标进不了游戏，那是测试环境问题而不是产品问题）。

**③ 物料区多一个"用于解析 list 或多种结构"的物料 + 能点容器里的具体地图**

- 新物料组「UI 操作（界面/列表）」，里面是「🎯 拾取界面目标…」：点它会把**当前屏幕上**的
  可交互元素列出来 —— 关键在于**列表容器会展开到每一行**：
  `WorldsList` 只是一个容器，真正要选的是里面那张地图，所以每一行都给
  `list:WorldsList@世界名`（按文字）与 `list:WorldsList#0`（按序号）两个语义目标。
- 选中的目标 → 直接生成/更新一个 `Task.UiClick` 节点（`target`/`mode`/`waitSeconds`）；
  属性区里选中 `Task.UiClick` 也有同一个「🎯 拾取界面目标…」按钮。
- UI 拾取面板里的列表元素同样展开了每一行（点一行就填进目标框并顺手定位）。
- 选择器每一行都有「用作节点目标」与「在游戏里点一下」，后者直接走 `ui.clickelement`，
  点完自动刷新列表 —— "这个目标到底对应界面上哪一行"当场就能确认。

**④ 动作包右键：编辑 / 删除 / 重命名**（顺序就是用户要的顺序）

动作包下拉与物料区的动作包项都支持右键，弹出菜单：**编辑 / 删除 / 重命名**。

- **编辑**：`openActionEditor(file)` —— 打开动作包编辑器，编辑**语义事件轨**
  （时间 / 类型 / 目标），可加一条、上下移、删除；每一行还有「🎯」按钮可以直接把某个点击
  改成从游戏里现取的语义目标。保存走 `POST /api/action/events`，**逐帧输入轨与关键帧一字不动**，
  写完立刻重新校验并把报告显示出来。这正是"把 `click:1010.6,64.83` 改成
  `click:list:WorldsList@世界名`"的入口 —— 改完再也不会因为窗口尺寸失效。
- **重命名**：`POST /api/action/rename` —— 改文件名，**manifest 里的 name 跟着改**；
  还会回报"哪些树还在引用旧名字"。
- **删除**：`POST /api/action/delete` —— 先确认再删，同样回报引用它的树；
  只允许删可写包目录里的文件（只读目录/游戏安装目录一律拒绝）。

服务端新增四个端点：`GET/POST /api/action/events`、`POST /api/action/rename`、
`POST /api/action/delete`；编辑器自检 **116/116**（+8：读事件、写事件且仍可回放、
缺 detail 拒收、重命名（含同名 no-op）、删除、删不存在的如实报 not_found）。

**顺手补的一个诚实性修复**：窗口不活跃时引擎整段忽略输入
（`WidgetInput.Update()` 里 `if (Window.IsActive)`），以前合成点击会**无声无息地丢掉**。
现在 `ui.clickelement` 直接拒绝并说明怎么办（`window_not_active`）——
实测：刚启动游戏、窗口还没拿到焦点时点 Play，以前"看着像没反应"，现在一句话说清。

验收：真浏览器 **179/179**（+15：语言切换后物料显示名变英文而 `data-type` 不变、
每个类型都有翻译、切回中文、`data-type` 仍在、UI 操作物料存在、选择器把列表展开到行、
「用作节点目标」回调正确、模态框能关、右键菜单三项且顺序正确、动作包编辑器列出事件、
保存把像素目标改成 `list:` 语义目标）；UI 服务链 `verify_ui_service.py` **11/11**
（共控模式要么真的点动、要么如实报 `window_not_active`；脱离模式下整条链进世界）。

### 9.5.34 「切换到英文后很多按钮还是中文」：三类漏翻 + 一个被语言切换掩盖的真 bug

用户报的现象（附截图，界面已经是英文模式）：**工具栏、徽标、状态行、悬停提示里还有一大堆中文**。

先做了一把"照妖镜"再动手 —— 两个脚本把"切英文后仍然是中文"的地方全部列出来：

```
py Mod/Packages/out/check_ui_english.py    # 静态（index.html 没挂 data-i18n）+ 动态（app.js 直接写中文）
py Mod/Packages/out/check_i18n_keys.py     # 中英两段键集合是否一致、英文值里是否还有中文、占位符是否对齐
```

首轮跑出来 **127 处**。逐类处理之后是 0，两个脚本现在都进了总闸门（`check_player_ai_build.py i18n`）。

**① 动态文案（88 处，主因）**：`badge.textContent = '树：未运行…'`、`parts.push('视图=' + …)`、
`rows.push(['模式', …])` 这类**每次刷新界面都会重写一遍**的赋值 —— `applyLanguage()` 明明已经
把静态文案换成英文了，下一次刷新又被这些中文字面量覆盖回去。处理方式是**按整条语句**包成
`I18n.format("ui.NNN", '模板 {0}', 值)` / `L("ui.NNN", '原文')`（`apply_ui_texts.py`，74 处）。

**② 三元与拼接里的碎片（179 条去重、209 处）**：上一轮的正则只能认"整条赋值"，
`'已连接：' + childId + ' 的第 ' + n + ' 个子节点'` 这种套不进去。改成**按字面量**包
（`apply_remaining_i18n.py`），拼接结构原样保留；只有四处**语序会坏**的（连接/断开/升降级/
子节点计数）整体改成模板，例如 `'已连接：{0} → {1} 的第 {2} 个子节点'` → `Connected: {0} → child #{2} of {1}`。

**③ 藏在 `L()/I18n.format()` 参数里的中文（46 条去重、64 处）** —— 这是最隐蔽的一类：
扫描器把整个"已翻译调用"当白名单跳过了，但**只有第 2 个参数**（调用点的中文兜底）该跳过，
第 3 个参数起是真正要显示的值：

```js
I18n.format("ui.085", '树：{0}{1} {2}（tick {3}）',
  (st.paused ? '已暂停' : '运行中'),              // ← 漏的就是这两句
  (st.menu ? '（控制器·主菜单）' : '（控制器）'), …)
```

症状正是用户看到的"半中半英"：`Tree: 运行中（控制器） enter.game.scbtpak (tick 7)`。
`wrap_arg_i18n.py` 按"第 3 个参数起"重扫重包，并且**优先复用已有键**（同一句中文不会出现两种英文）。
顺带修了语序：`setPaused(paused, what)` 原来从调用点把中文词传进来，
英文下会变成 `Now Pause the tree…`，现在按 `paused` 取词（`ui.w01` pausing / `ui.w02` resuming）。

**④ 静态漏挂（8 处）**：撤销/重做/视图/构建时间的悬停提示、搜索框占位文字、"标记落点"那行的提示、
`#diagLine` 的首屏占位。第一轮是按 id 清单批量挂的，清单外的就漏了 —— 根源是 `title="…"`
里带 `>`（`<实例根>` 那种）会把"标签到哪结束"的正则带偏；这次逐个精确锚点补
（`patch_static_i18n2.py`），并让 `applyLanguage()` 支持 `data-i18n-title`。

**⑤ 顺手抓到的真 bug：`saveAs` 从界面文字里抠实例根**。
`$('rootInfo').textContent.match(/实例根：([^\s　]+)/)` —— 切英文之后这行文字变成
`Instance root: …`，正则抠不到 → `folder` 是 `undefined` → **另存为会写到相对目录去**。
现在实例根同时放在 `data-instance-root` 上，正则只当兜底。这类"拿界面文字当数据源"的写法，
是这次翻译工作最大的附加收获。

**⑥ 语言切换后徽标不跟着变**：`applyLanguage()` 只回填 `data-i18n` 的静态文案，
而徽标是 `markDirty()` / `updateTreeRunUi()` 动态写的 —— 切回中文后改动徽标还停在 `Modified`。
现在这两处也一起重画。

**⑦ 工具自身的两个坑（都已修，并写进注释）**：

- 表格写入时把"源码转义写法"当逻辑文本又转义了一遍 → `\n` 变成 `\\n`，界面上真的显示一个反斜杠。
  现在**存进表里的一律是逻辑文本**，写进 `.js` 时统一转义（`i18n_text.py`）；
- 英文表里有 10 个键顶着**上一条**的译文（中文从调用点补回来后，占位符个数一对就露馅，
  如 `st.c053` 需要 `{0}{1}` 却只有 `{0}`）→ `fix_misaligned_en.py` 逐条修正，
  `check_i18n_keys.py` 会把这类问题当成硬失败。

**顺带把中英两段补成自洽**：原先 319 个键只有英文（中文靠 `t()` 的兜底链回退到调用点原文），
能跑但很脆 —— 任何一处忘了传兜底就会把键名显示出来。`fill_zh_from_callsites.py` 从
调用点与 `index.html` 里把中文原文捞回来写进中文段，现在 **673 / 673 完全对称**。

**验收**：

```
py -3 Mod/Packages/check_player_ai_build.py all
# RESULT: all -> [0, 0, 0, 0, 0, 0, 0, 0]
#   编辑器自检 116/116；真浏览器 187/187（+7 英文模式断言）；web 自检 376/376（+7）
#   i18n 闸门 0 problem(s)
```

新增的断言正是用户关心的那条线：切到 English 后**工具栏与徽标整排**（含悬停提示、占位文字、
画布提示）都不含中文，**并且刷新一次真实游戏状态之后仍然是英文**（动态写入不许覆盖回去）；
`#btnLang` 自己显示"切换到中文"属于故意（按钮写的是切过去之后的语言），已在闸门里白名单说明。

### 9.5.35 「地图选择界面里 UI 拾取器拾不到东西」：嵌套数组被压成字符串（+ 前端加兜底）

用户报的现象（附截图）：进到**选择世界**那个界面，点「拾取界面元素」，
面板红框写着

```
Request failed: listInfo.items.forEach is not a function
```

**根因链**（一路查下来，只有最后一段是新的）：

1. 游戏侧 `ui.elements` 的 `result.elements` 是**一段 JSON 文本**，不是数组 ——
   这是 `GameBridgeClient.Collect` 的既定行为（`member.IsArray → member.ToJson(false)`），当初是为了
   让整份清单能塞进一行控制通道消息。编辑器早就知道这件事，`SuggestTargets` 会先
   `ParseElementArray` 把它解析回数组（注释里就写着"否则前端拿到的是字符串，`elements.forEach` 直接炸"）。
2. 但**嵌套的那一层没管**：解析每个元素时，`PlainValue` 只处理标量，
   遇到数组/对象又 `ToJson(false)` 回去了 ——
   于是列表控件 `list.items`（**每一行世界**）到前端变成了字符串：
   `items` 的类型是 `string`，内容是 `[{"index":0,"text":"Rebritish",...}]`。
3. 前端那句守卫 `if (listInfo && listInfo.items && listInfo.items.length)` 拦不住：
   **非空字符串也有 `.length`**。接着 `.forEach` 不是函数 → 整个面板走 `catch`，
   显示成"请求失败"，一个元素都拾不到。
4. 只在有列表控件的界面暴露（主菜单没有列表；选择世界/物品栏这类界面才有），
   所以前几轮自检没碰到 —— 而且自检里的拾取测试用的是**桩数据**（`items` 是数组），
   正好绕开了这条真实链路的形态差异。

**四处一起改**：

- **① 递归转换**（根因）：`EditorApi.PlainValue` 现在把数组转成 `List<object>`、对象转成
  `Dictionary<string,object>`，不再 `ToJson`；`ParseElementArray` 每层都走它。
  实测：`/api/game/ui/elements` 里 `list.items` 从 string 变回数组（4 个世界，每行带坐标）。
- **② 前端兜底**：新增 `asArray(value)` —— 真数组直接用；长得像 JSON 文本就解析回来
  （兼容旧版 Mod/旧版编辑器）；其余一律空数组。`elements`、`list.items`、状态行计数
  全部改走它。**列表行少展开几行可以忍，整个面板崩掉不能忍。**
- **③ 顺手修掉 `screen=?`**：游戏侧的 `ui.elements` 根本没带 `screen` 字段
  （实测字段只有 `elements/ready/shown/totalCount/truncated`），面板标题写着 `screen=?` 等于没说。
  现在用元素的路径根当界面名（`[SuPlayScreen#0]/…` → `SuPlayScreen`），
  并且 `truncated` 时补一句"这里只列了前 N 个，界面上共 M 个"。
- **④ 补测试，把这条真实形态钉死**：
  - 编辑器自检 **121/121**（+5）：用**和游戏侧一模一样**的回包（`elements` 是 JSON 文本、
    里面嵌着数组）断言 `elements` 是真数组、`list.items` 是**真数组**、
    行里有坐标、`selectedIndex → rowTarget`、每个元素都有语义目标；
  - web 自检 **384/384**（+8）：`items` 是真数组 / 是 JSON 文本 / `elements` 是 JSON 文本 /
    彻底无效的值，四种形态都不能崩；外加 `screen` 推导与截断提示；
  - 真浏览器 **190/190**（+3）：真 DOM 下容器行展开成子行、行带 `list:列表@文字` 与坐标、
    `items` 是 JSON 文本时照样展开。

**验收**（真机，游戏停在"选择世界"界面）：

```
py -3 Mod/Packages/out/goto_world_select.py      # 主菜单点一次 Play，只进选择界面
py -3 Mod/Packages/out/verify_ui_picker_list.py  # 复核 list.items 与逐行定位/点击
```

实测输出：`list.items` 是真数组（4 行：Rebritish / CmdBridgeTest / Rosnia Mear / ceshi260810）、
每行有坐标、`list:WorldsList@Rebritish` 定位得到 `(619.5, 64.8)` 且点击被游戏接受
（`ok=true`，不点世界名以外的东西，不会真的进世界）。

**教训（写给下次改这条链路的人）**：`GameBridgeClient.Collect` 会把**顶层**数组收成 JSON 文本，
这是通道的既定约定；凡是**嵌套**的数组/对象，转成 CLR 结构时必须逐层递归，
否则前端拿到的是"看着像数组、其实是字符串"的东西 —— 而 `typeof` 与 `.length` 都骗得过守卫。

### 9.5.36 「切英文后还剩几处中文」：文案由"只在启动时跑一次"的函数写出来，没人重画

用户第三次反馈（附截图）：切到英文之后，**底部**「实例根：…　包目录：…」、
**顶部**打开包下拉里的「common.scbtpak [common] 5节点」、
**动作包下拉**里的「（仅结构：不能回放）…1043帧」还是中文。

**这一次没有再去逐个猜** —— 前面已经为同类问题（徽标、状态行）列过两轮清单，列清单必定漏。
改成让真浏览器**把整个页面扫一遍**：切到英文之后，文档里任何元素的文字节点与
`title` / `placeholder` 属性里都不许再有中文，只放行两件事 ——

- 语言按钮自己（它写的是"切换到中文"，那是设计）；
- **数据**里的中文（动作包/行为树文件名可以是中文，例如用户自己的「进入游戏.scatpak」）。
  这里有个坑：不能"看到文件名就跳过整段"，得**把文件名整段剥掉再判中文** ——
  下拉里是 `进入游戏.scatpak（仅结构：不能回放）2.231s/723帧` 这种混合文本，
  跳过整段就把模板里的中文一起放过了（用户报的正是这一段）。

第一次跑就抓出 5 类残留，全是**同一种病**：文案写它的那个函数只在启动/加载时跑一次，
`applyLanguage()` 没有重画它。

| 残留 | 谁写的 | 什么时候写 | 修法 |
|------|--------|-----------|------|
| 底部「实例根/包目录」+ 游戏徽标 | `loadMeta()` | 启动时拉一次 `/api/meta` | 缓存 `state.meta`，拆出 `renderMeta()`，语言切换时重画 |
| 打开包下拉「5节点」 | `loadPackages()` | 启动/保存后 | 拆出 `renderPackageOptions()`（纯渲染） |
| 动作包下拉「（仅结构…）/帧」+ 动作包徽标 | `loadActions()` | 启动时拉一次 `/api/actions` | 拆出 `renderActionSelect()`（纯渲染） |
| 小地图悬停提示 | `ensureMinimap()` | 元素创建时 | 已存在时也按当前语言刷一次 title |
| 「落点 x,y」那一行 | `showLocatedLine(text)` 的调用点 | 每次定位/点击 | 改成缓存**原始回包**（`uiPick.located`）+ `renderLocatedLine()` —— 只缓存拼好的字符串是翻不了的 |
| 组头「注释 1」 | `createGroupFromSelection()` | 建组时算好的默认名，还会**存进 localStorage** | 默认名不再落盘：`title` 存 null，显示时用 `groupTitle(group)` 按当前语言现算；用户改过名才写 title |

**顺带**：`ui.040` 的英文模板原来是 `{2}nodes`（"5nodes"），补成 `{2} nodes`。

**验收**：

```
py -3 Mod/Packages/check_player_ai_build.py all
# RESULT: all -> [0, 0, 0, 0, 0, 0, 0, 0, 0]
#   真浏览器 191/191（新增"整页扫描"这条，跑第一次时是 FAIL，把上面 5 类全抓了出来）
#   web 自检 389/389（+5：底部实例根、包下拉的节点数、动作包下拉的"帧"、游戏徽标、
#                       以及切回中文之后这三处又能跟着回来）
```

**教训**：i18n 的完整性不能靠"人工列清单 + 静态扫源码"。文字是怎么写上去的（静态属性、
启动时写一次、每次刷新重写）决定了它会不会跟着语言走，而**只有真浏览器里"切换一次语言再整页扫一遍"
能一次抓全**。这条断言现在是常驻的，以后新增任何一处文案，只要它不跟语言走就会被抓住。

### 9.5.37 编辑器改成**自包含**单文件发布（对方机器不用装 .NET）

起因：上一轮查明 `PlayerAiEditor.exe` 是**框架依赖**发布（5.5MB 单文件，内嵌 runtimeconfig 写着
`"framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" }`，且没有 `includedFrameworks`）。
也就是说：**没装 .NET 8 运行时的机器上，双击编辑器没反应**（连窗口都出不来）。
游戏本体不受影响 —— 实例根是自包含发布，自带 `coreclr.dll` / `hostfxr.dll` / `System.Private.CoreLib.dll`。

**改动**（发布形态写进 `PlayerAiEditor.csproj`，随源码走）：

```xml
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<SelfContained>true</SelfContained>
<PublishSingleFile>true</PublishSingleFile>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
<DebugType>embedded</DebugType>
<AllowedReferenceRelatedFileExtensions>none</AllowedReferenceRelatedFileExtensions>
```

为什么必须写进 csproj 而不是只写在构建脚本里：`Mod/Packages/` 被 `.gitignore` 排除，
只改脚本等于"只有本机能构建对"，克隆仓库的人还是发框架依赖版。

顺带把发布目录清成"只有一个 exe"：`DebugType=embedded` 把调试符号塞进程序集内部
（**行号不丢**，但不再生成 `.pdb`），`AllowedReferenceRelatedFileExtensions=none` 不再把
`Engine.pdb` / `EntitySystem.pdb` / `EntitySystem.xml` 这些引用程序集的附属文件拷过来。

**结果对照**：

| | 框架依赖（旧） | 自包含（新） |
|---|---|---|
| 产物 | `PlayerAiEditor.exe` 5.5MB + 4 个符号文件 | **`PlayerAiEditor.exe` 35MB（仅此一个文件）** |
| 内嵌运行时 | 无（`framework`） | 有（`includedFrameworks`） |
| 目标机器要求 | **必须装 .NET 8 运行时** | **无** |
| 启动开销 | 立即 | 首次启动在内存里解压（几十毫秒，一次性程序可忽略） |

**验证**（把 `DOTNET_ROOT` 指到空目录 = 等价于"这台机器没装 .NET"，两个 exe 都跑 `--print-root`）：

```
A. 旧的框架依赖 exe：You must install .NET to run this application.  exit=-2147450749（框架未找到）
B. 新的自包含 exe  ：正常打印实例根检测结果                        exit=0
```

外加自包含 exe 上的 C# 自检 **121/121 ALL PASS**（同一套 `--selftest`）。

**顺带确认的一件事（回答"用户不装 Python 能不能用"）**：整个运行链路与 Python 无关 ——

- 实例根 `publish/Windows` 里递归找不到任何 `.py`，连 `.js` / `.html` 也没有
  （编辑器前端是**内嵌**在 exe 里的，运行期不读磁盘上的 `Web/`）；
- 三个运行时工程（`CmdBridgeMod` / `PlayerAiMod` / `PlayerAiEditor`）的 C# 里没有任何
  Python 调用（C 里只出现注释与 README 文档；`Process.Start` 只用于启动游戏、打开浏览器）；
- `.scmod` 里只有 `ModInfo.xml + Lib/*.dll`；编辑器与游戏之间是 socket 协议，不是脚本桥。

所以 `Mod/Packages/**` 那一百多个 `.py` 全是**开发/验证工具**（含总闸门 `check_player_ai_build.py`
与打包部署脚本 `pack_player_ai.py`），删掉不影响用户玩、也不影响 AI 操作游戏；只影响"从源码改 mod
再重新打包/跑验收"。它们本来也不在版本库里。

### 9.5.38 补上缺的装饰器与黑板工具链（含"物料区搜索搜不到中文"等 6 项）

用户要求（原话）："实现缺失的装饰器，服务，还有中英文搜索问题和建议的修改"，并明确
"寻路器可以参考游戏目前已有的寻路器，那是 A* 制作的；其余的任务族也需要能够处理"。
这一节记录**第一轮**落地的部分（装饰器 + 黑板工具链 + 物料区交互）；服务族、寻路器与世界交互任务族见后续小节。

**① 两个缺失的装饰器**（对照 `doc/player-ai-plan.md §3.1` 的清单，此前确实没有）：

| 装饰器 | 语义 |
|---|---|
| `ForceFailure` | 把 `Succeeded` 改成 `Failed`（`ForceSuccess` 的反面；`InProgress` / `Aborted` 不动）。以前只能拿 `Inverter + ForceSuccess` 拼，还得注意顺序 |
| `CompareBBEntries` | **两个黑板键比较**（UE 的 `BTDecorator_CompareBBEntries`）：两边都得有值、类型能比才成立；数值优先（`int` 与 `float` 混用也能比），其次 `bool` / 字符串 / actor（按名字比）——"目标比上次更近了吗""两个键是不是同一个目标"这类**相对判断**终于能表达 |

两处都补了注册表项、编译器（`TreeCompiler.ApplyDecoratorProperties`）、导出器
（`TreeWriter.CaptureDecoratorProperties`）与中英显示名。

**② 属性表新增 `BlackboardKey` 类型**（`BtPropertyKind.BlackboardKey`）：
`key` / `keyA` / `keyB` / `targetKey` 不再靠"名字恰好像黑板键"的约定去猜，而是**显式声明类型**。
由此换来三件事：属性区给**下拉**（候选 = `manifest.blackboard` 声明的键 ∪ 本树里用过的键 ∪
实时监视里游戏报过值的键，并留"（自定义…）"手填 —— "第一次写入的新键"是正常用法）；
校验器知道该检查哪些属性；类型检查照旧（本质是字符串）。

**③ 校验器：引用的黑板键必须声明过（否则警告）**。
`PackageValidator.ValidateDeclaredBlackboardKeys` 走一遍节点 + 装饰器 + 服务，
把 `BlackboardKey` 属性的值与 `manifest.blackboard` 对照。**用警告不用错误**：
键完全可能来自被引用的父包（`common.scbtpak` 的 `target` 就是 `demo.greet` 的服务写进去的），
硬判错会把本来能跑的包拦下来；但警告必须给 —— 键名打错（`targt`）在运行期只表现为
"条件永远不成立"，极难查。

**④ 「包信息…」面板**（工具条新增按钮）：`manifest` 的**黑板键声明**与**引用表**终于能在界面里改
（以前只能手改 JSON，界面上还写着"要加引用得改 manifest.references"）。面板改的就是 `state.manifest`，
点「保存」照常走既有链路（校验 → 原子写回）；引用路径给"从包目录里选"的下拉，黑板键给类型下拉与只读勾选。

**⑤ 物料区搜索支持中英文显示名**（用户点名的问题）。
以前 haystack 只有 `type + file + id` —— 中文界面下搜「冷却」「循环」「等待」**全是空结果**，
用户不可能记得住 `Cooldown`。现在 `I18n.nodeLabelSearch(type)` 把**类型标识 + 中文名 + 英文名**
一起塞进 haystack（英文界面搜中文名也能命中），并且**分组名命中就列出整组**（搜「装饰」列出全部装饰器）。

**⑥ 点击添加装饰器/服务时套用 schema 默认值**。
以前**拖拽**会填默认值、**点击添加却给空 `properties`** —— 同一个物料两种结果，
而 `Blackboard` 的必填 `key` 为空会立刻进校验错误（用户看到的是"刚加进来就报错"）。
现在两处共用 `materialDefaults(kind, type)`。

**顺手修的表格维护问题**：`i18n.js` 的 en 段有 **22 个键被重复定义**（早前几个补丁脚本留下的），
JS 里后者生效所以显示一直是对的，但表已经糊了 —— `dedupe_i18n_keys.py` 清掉 28 行重复
（保留生效的那一份；实测核对过 `st.c046/c049/c056/c066/c078/c107` 等只有最后一份是修好的译文）。
`check_i18n_keys.py` 同时补了两条：**同段内重复定义**要报错、跨段同名键**不算**重复
（第一版把 `ui.087` 这种正常的中英各一条误报了，顺手修掉）。

**验收**（这一轮用**临时构建 + 8761 端口**跑真浏览器自检，完全没打扰正在使用的编辑器）：

```
dotnet build  ×2                              → 0 警告 0 错误
PlayerAiEditor --selftest                     → 127/127 ALL PASS（+6）
node Mod/Packages/editor_web_selftest.js      → 397/397 ALL PASS（+8）
check_editor_browser.py --port 8761           → 198/198 ALL PASS（+7）
check_i18n_keys.py / check_ui_english.py      → OK（678/678 对称、无重复、英文里没中文）
```

真浏览器那 7 条里有一条特别值得留："点击添加装饰器套用默认值"是在**真 DOM、真 schema** 下验证的
（`{"id":"d1","type":"Cooldown","properties":{"cooldownSeconds":5}}`），
顺带证明了补上的两个装饰器在游戏侧真注册表里确实存在。

### 9.5.39 传感器服务族 + `Task.FaceEntity` / `Task.Emit`（P2）

接 §9.5.38。用户要求"**服务**缺失也要补"。以前整个服务族只有一个
`Service.UpdateNearestPlayer` —— 行为树能判断的东西少得可怜：除了"最近的玩家在哪"，
其余（我快饿死了吗 / 附近有狼吗 / 前面是不是墙 / 看得见目标吗）都问不出来。

**① 新增扩展观察层 `IAiWorldSensor`**（`Actor/IAiSensor.cs`）。
为什么**不**直接往 `IAiSensor` 上加方法：纯逻辑自检里的传感器桩（`BtTestSensor`、
动作包自检的 `DriftingSensor`）只要基础那几项就有意义；逼它们实现"方块射线"只会让桩
变成"假装懂世界"的假货。所以扩展能力单开一个接口：真实游戏里的 `PlayerSensor` 实现它，
**`ControllerSensor` 必须转发它**（运行时真正拿到的是控制器传感器 —— 少了这段转发，
服务会以为"没有扩展能力"而静默跳过，那种"功能都在、就是不生效"最难查）；
服务用 `as` 取，取不到就跳过并说明原因，于是同一棵包在自检环境里照样能装、能跑。

新增能力（全部只读）：`TryGetSelfState`（生命/氧气/饥饿/耐力/睡眠/体温/湿度 + 坐标朝向是否落地）、
`TryFindNearestCreature(categoryMask, maxDistance)`、`TryFindNearestPickable`、
`TryRaycastBlock(direction, maxDistance)`、`HasLineOfSight(target)`。
`AiActorView` 也加了 `Kind` / `CategoryMask` / `Value` / `Count` 四个字段（掉落物也算"可感知实体"）。
源码依据：`ComponentVitalStats.cs:72-126`、`ComponentHealth.cs:40,46`、`ComponentBody.cs:99,107`、
`SubsystemBodies.FindBodiesAroundPoint`、`SubsystemPickables.Pickables`、
`SubsystemTerrain.Raycast(...)`（四参数版，`action` 可为 null）、`CreatureCategory` 位掩码、`ComponentName.Name`。

**② 五个新服务**（`Bt/BtSensorServices.cs`，都已注册且可入包）：

| 服务 | 写进黑板的东西 |
|---|---|
| `Service.UpdateSelf` | `self.Health/Air/Food/Stamina/Sleep/Temperature/Wetness/OnGround` + `self.X/Y/Z/Yaw`（坐标拆成三个 float —— 黑板只有 bool/int/float/string/actor，拆开才能比较、也能喂给"走到某坐标"） |
| `Service.UpdateNearestCreature` | `targetKey`(actor) + `Distance`/`Name`/`IsSet`；`categoryMask` 按 `CreatureCategory` 过滤（1=陆地掠食者 …） |
| `Service.UpdateNearestPickable` | `targetKey`(actor) + `Distance`/`Name`/`Count`/`IsSet` |
| `Service.UpdateBlockAhead` | `key`(bool 前方有方块) + `Distance`/`Contents`/`X/Y/Z` + 方块名（`nameKey`）；`pitchOffset` 默认 -0.35 弧度 = 看脚前那格 |
| `Service.UpdateLineOfSight` | `key`(bool 看得见) + `Distance`；目标取黑板里的 actor 键 |

共同约定：**找不到就清掉上次的值**（`clearWhenMissing` 默认 true）—— 否则"狼已经走了"
而黑板里还留着旧坐标，树会一直朝空地跑（这个坑在 `UpdateNearestPlayer` 上踩过）。

**③ 两个计划里列了但一直没实现的任务**（`Bt/BtActorControlTasks.cs`）：

- `Task.FaceEntity`：把视角转到黑板里那个 actor 的方向，**转到位就成功**、超时判失败。
  与 `Task.LookAt` 的分工很清楚：`LookAt` 的 KPI 是"看了一会儿"，`FaceEntity` 的 KPI 是
  "朝向正确了" —— 攻击/交互/上马这类动作要的正是后者（转到位才动手，而不是瞎等固定时长）。
  角度误差做了归一化（`差 359° 其实是差 1°` 这种判定错误）；`maxTurnPerSecond=0` 时用 AI 的瞬时转向。
- `Task.Emit`：往 **AI 事件日志**写一条（`ai.logs` / `<实例根>/PlayerAi/Logs/PlayerAi.log`
  都能看到），并可顺手置一个布尔键。为此给 `BtRuntime` 加了一个**可写**的 `EventLog` 属性
  （不改进构造函数：纯逻辑自检里大量 `new BtRuntime(黑板, 桩, 桩)`，为可选日志去改所有调用点不值得），
  `ControllerTreeHost` 多了一个可选参数把日志接进去，`PlayerAiRuntime` 传生产上的那份。

**验收**（同样是"临时构建 + 8761 端口"跑真浏览器，没打扰正在使用的编辑器）：

```
dotnet build ×2                              → 0 警告 0 错误
PlayerAiEditor --selftest                    → 143/143 ALL PASS（+8）
node Mod/Packages/editor_web_selftest.js     → 397/397 ALL PASS
check_editor_browser.py --port 8761          → 201/201 ALL PASS（+3）
i18n 闸门 ×2 / 行尾检查                       → OK
```

新增的 8 条自检里，有 6 条是**内核行为**（编辑器链接了 `PlayerAiMod/Bt/**` 的同一份源码，
所以 `--selftest` 就能直接造树、直接 tick）：

- `ForceFailure` 把成功改成失败（`Failed`）；
- `CompareBBEntries` 拿 float 键比 int 键（`3 < 10 = True, 3 > 10 = False`）、缺一边判不成立；
- 没有扩展观察能力时服务**跳过而不抛**（写 0 个键）；
- 有桩传感器时 `Service.UpdateSelf` 真的写进 `self.Food=0.42`；
- `Task.FaceEntity` 没转到位继续转（并且真的调了执行器的 `Look`，`lookCalls=1`）、转到位成功、
  目标没了失败；
- 新服务/新任务的属性名→字段映射**往返不丢**（`prefix` / `categoryMask` / `toleranceDegrees` / `message`）。

> 写这些断言时踩到一个值得记下来的点：树一旦**完成**，`BtRuntime.Tick` 会
> `ResetSubtreeState()`，把每个节点的 `LastResult` 复位成 `Failed`、`ActiveTime` 归零 ——
> 所以"这棵树跑成什么样了"要看 `runtime.LastResult`（运行时在复位**之前**记的那份），
> 看节点自己只会看到一片 Failed。这条也单独立了一条断言，免得下次又有人踩。

### 9.5.40 寻路移动 + 世界交互任务族（P3）

接 §9.5.39。用户要求："**寻路器可以参考游戏目前已有的寻路器，那是 A* 制作的。
其余的任务族也需要能够处理**"。

**① `Task.NavigateTo`：借游戏的 A* 算路，用注入输入跟着走**。

先说清为什么值得单独一个任务、而不是给 `Task.MoveTo` 加个开关：`MoveTo` 是"朝目标按 W"，
撞墙就顶着；`NavigateTo` 会绕。两者不是"改进关系"而是两种语义 —— 直线走法在短距离、
看得见目标时反而更合适，硬合并只会让简单场景也背上寻路队列的延迟：

- 寻路是**异步**的（`SubsystemPathfinding.QueuePathSearch` 只入队，**后台线程每 250ms 处理一条**，
  队列上限 10），要轮询状态、要在等待期间继续朝目标走、要在走不动时重算；
- 跟随自己要维护"当前第几个航点""卡住没卡住""该不该重算"，这些和"按住 W"完全不是一回事。

读源码读出来的两个关键点（不然必踩）：

| 发现 | 后果 |
|---|---|
| `AStar.BuildPathFromEndNode`（`AStar.cs:39-47`）是**从终点往起点**填数组的：`Path[0]` 是**终点**，`Path[Count-1]` 才是离自己最近的那格。游戏自己的 `ComponentPathfinding` 也从数组**末尾**取航点 | 直接顺着数组走 = 先往终点跑（穿墙/绕远）；所以 `CopyPath` **翻过来**拷，index 0 = 最近航点 |
| 队列满（≥10 条）时游戏返回的是"**空且已完成**"的结果，不是失败也不是异常 | 判"路径可用"必须判 `Count > 0`，否则会把"没路"当"走到了" |

铁律不变：**路径计算是只读查询**（借游戏的寻路器，不改世界、不动角色），
**路径跟随只用输入层**（`LookAt` 转向前方 → 按住前进键 → 走到航点半径内换下一个 →
航点明显更高且已靠近时脉冲一下跳跃键 → 一段时间位移不足就判定卡住并重算）。
寻路不可用时（没有子系统/队列满）**退化成直线走**，而不是原地失败。

**② 世界交互任务族**（`Bt/BtInteractionTasks.cs`）：挖 / 放 / 攻击 / 交互 / 选快捷栏。
全部只用"转视角 + 鼠标左右键 + 数字键/滚轮"，不做任何游戏状态直写。
判据都取自**只读观察**，不去猜：

| 任务 | 成功判据 |
|---|---|
| `Task.Mine` | 再看一次那个格子，**变成空气**才算挖掉（按了多久不算数：工具不对/超距/失焦都会被游戏忽略） |
| `Task.Attack` | 黑板里的目标键消失（生物服务找不到目标会清键）= 打死或跑了；SC 攻击是"按一下挥一次"，所以默认**连点**（`clickInterval`），`0` 才是一直按住 |
| `Task.Interact` | 对黑板坐标的格子或黑板里的 actor 点右键（可 `repeat`/`interval`，上马这类要双击） |
| `Task.PlaceBlock` | 看向目标格子中心点右键（游戏放在你正看的那个面上）；手上得有方块 → 先 `Task.SelectSlot` |
| `Task.SelectSlot` | 按数字键切快捷栏（或滚轮）；纯输入动作，点完即成功 |

配套：`IAiWorldSensor` 新增 `TryPeekBlock(x,y,z)`（挖方块的"挖掉了没"）与寻路四件套
（`TryRequestPath` / `GetPathStatus` / `CopyPath` / `ClearPath`）；`AiPathStatus` 枚举
（Idle/Searching/Ready/Failed）放在 `IAiSensor.cs` 里，与扩展接口同处一个文件。

**验收**（临时构建 + 8761 端口跑真浏览器；编辑器 8760 全程没被碰）：

```
dotnet build ×2                              → 0 警告 0 错误
PlayerAiEditor --selftest                    → 157/157 ALL PASS（+14）
node Mod/Packages/editor_web_selftest.js     → 397/397 ALL PASS
check_editor_browser.py --port 8761          → 204/204 ALL PASS（+3）
i18n 闸门 ×2 / 行尾检查                       → OK
```

新增的内核断言里有 10 条是 P3 的行为（编辑器链接同一份 `Bt/**` 源码，`--selftest` 直接 tick）：

- `NavigateTo` **只请求一次**路径（`requests=1`）且真的按住前进键、真的在看向航点；
- 走到中途**不会重复请求**（`requests` 仍是 1，树仍 InProgress）——防"每帧重算把队列占满"；
- 走到目标半径内 → `Succeeded`；
- 队列满（无路径）→ **不失败**，退化成直线走（`held=True`）；
- `Mine` 在方块还在时按住左键、格子变空气即成功；
- `Attack` 目标在时连点并持续看向目标、目标消失即成功；
- `Interact` 点的是**右键**（这条一开始是失败：基类把 `Button` 默认写成 `left`，
  子类的"空则取右键"永远不成立 —— 自检报的 `button=left` 直接把它抓出来了）；
- `SelectSlot` 脉冲的是数字键 `3`。

另外补了寻路/交互两族的**包往返**断言（`maxPositionsToCheck` / `repathSeconds` / `slot` /
`interval` / `repeat` / `source` 保存再读回来都不丢）。

> 真机验收**已经做了**：见 §9.5.43（新节点在真游戏里跑通，含三条实测结论）。
> 那轮之后只剩"别的任务族"（合成/搬运/睡觉）没做。

### 9.5.41 坐标模式寻路 + 键名对齐 + 「跑的是旧 exe」的闸门修补

接 §9.5.40。三件事，第三件比前两件更重要。

**① `Task.NavigateTo` 增加 `source=cell`（走到固定坐标，不只是跟 actor）**。

`targetKey` 只能表达"跟着某个角色走"，而"去 (x,y,z)"才是采集 / 建造 / 回基地的基本动作。
有意思的是**坐标模式反而更贴游戏底层**：`SubsystemPathfinding.QueuePathSearch` 吃的就是一个
目标点，游戏自己也不知道"目标是个生物"这回事 —— 生物位置是每一步**重新取一次**再请求路径。

约定与挖/放保持一致：坐标是**格子**，目标点取格子中心 `(x + 0.5, y, z + 0.5)`；
`xKey/yKey/zKey` 默认 `goalX/goalY/goalZ`（int 键），三个键**缺一即失败**（不是"傻站着走直线"：
没有目标点时"朝目标按 W"这句话本身就没有意义）。

**② 键盘名规则统一：前缀 + `X/Y/Z`**。

`Service.UpdateBlockAhead` 写的键和 `Task.Mine` 读的键，必须是**同一套拼接规则**：
`mine` + `X/Y/Z`（服务默认 `key=mine`，任务默认 `xKey=mineX`）。这条不统一时最典型的症状是
"服务明明探测到了方块、任务却说黑板里没有目标格" —— 两个默认值各自看着都合理，
拼起来却不成立。同理 `interact*` / `place*`。规则写死成"前缀 + X/Y/Z"之后，
"探测 → 挖" 这种最常见的组合**不填任何属性**就能跑通。

**③ 闸门一直在跑「加 RID 之前留下的旧 exe」**（`check_player_ai_build.py`）。

编辑器 csproj 加了 `RuntimeIdentifier=win-x64` 之后，Debug 产物落在
`bin/Debug/net8.0/win-x64/`，而闸门里的路径还停在 `bin/Debug/net8.0/` —— 那是一个**旧文件**，
于是"自检全绿"报的是旧 exe 的数字（121 项），而源码里已经有 160 个 `Check()`。
发现它靠的是"数字对不上"这种很弱的信号，所以补了两道：

- 路径改成 RID 那一层；
- 新增 `newest_source_mtime()` + **STALE 判失败**：产物比源码（`.cs` 与 Web 资源）旧就直接红，
  不再给一个好看的假数字。"忘了编译就跑测试"从此是硬错误。

**验收**（临时构建 + 8761 端口跑真浏览器；编辑器 8760 全程没被碰）：

```
dotnet build ×2 + publish                      → 0 警告 0 错误
pa_build_test.py（mod 侧内核）                  → 474/474 ALL PASS
PlayerAiEditor --selftest（Debug + 发布 两遍）   → 168/168 ALL PASS（+11）
node Mod/Packages/editor_web_selftest.js       → 397/397 ALL PASS
check_editor_browser.py --port 8761            → 207/207 ALL PASS（+3）
i18n 闸门 ×2（700/700 键）/ 行尾检查             → OK
```

新增断言里与坐标模式有关的四条：

- `(source=cell)` 请求的路径目标点**正好是格子中心**（`40.5, 64, 20.5`）；
- 少一个坐标键 → `Failed` 且**一次路径都不请求**；
- `source=cell` / `goalX…Z` 存盘再读回来**不丢**（漏写 registry/编译器/写回器任何一处，
  界面上能选、存盘却会静默退回 actor 模式 —— 这类"静默降级"只能靠往返断言抓）；
- schema 里确实把坐标模式当成枚举属性暴露（`actor|cell`）。

### 9.5.42 「用手上的物品」与「跳」：两个**没有目标格**的输入动作

补完交互族之后还剩两种玩家动作无处安放，都是"目标"这件事本身不成立：

| 动作 | 为什么 `Task.Interact` / `Task.PlaceBlock` 表达不了 |
|---|---|
| 吃 / 喝 / 用手上的物品 | `Interact` 必须有目标（格子或 actor）；`PlaceBlock` 必须有"看得见的面"。而 SC 的 `ComponentMiner.Use` 在**没命中方块**时用的就是手上物品 —— 吃东西恰恰是"对着空气右键" |
| 跳 | `NavigateTo` 里那一下跳是**跟着航点自动补**的；"原地跳上面前的台阶""跳过脚下的沟"这种看着地形做的动作，树里得能手动表达 |

于是：

- **`Task.UseItem`**：按住右键 `holdSeconds` 秒再松开，可 `repeat` 次（吃东西要"按住一小会儿"，
  不是点一下）。`button` 可改。
- **`Task.Jump`**：脉冲跳跃键 `times` 次；`alsoForward` = 边按住前进键边跳（跳台阶/跳沟）。
  两次之间默认 0.45 s —— SC 的跳跃有落地判定，按太快等于没按。

**判据都是"这个输入动作做完了"**（和 `Task.SelectSlot` 一个道理），本节点**不猜"吃了有没有用"**：
"食物真涨了没"属于只读观察，交给它后面的条件去看 `self.Food`（`Service.UpdateSelf` 写进去的）——
"输入动作负责做、只读观察负责判断"是这套节点的分工。反过来，
**失焦（`IsInputAccepted=false`）时如实失败**，绝不能"按住了一段就报成功"，那等于把
"什么都没发生"写成"我吃过了"。

**验收**：

```
PlayerAiEditor --selftest（Debug + 发布 两遍）   → 168/168 ALL PASS（+5 条内核断言）
check_editor_browser.py --port 8761            → 207/207 ALL PASS（+3：真 schema / 中文搜索 / 英文搜索）
schema 里 23 个节点类型都有中英显示名
```

内核断言（`--selftest` 直接 tick，不碰游戏）：

- `UseItem` **跨帧按住**右键（`MouseButtonCount>0` 且 `MouseClickCount==0` —— "按住"不是"点一下"）；
- 按满 `holdSeconds` 后**松开并成功**；
- 失焦时**失败**（不是"假装成功"）；
- `Jump` 脉冲的是 `space`；`alsoForward` 时**按住 w、做完再松开**（`pulses=2`、`held=w/False`）。

### 9.5.43 真机验收：新节点在**真游戏**里跑通（这轮的三条实测结论）

接 §9.5.40 / §9.5.41 / §9.5.42。前面所有验证都是"没有游戏"的（内核 tick + 无头浏览器），
这一轮把新构建**部署进游戏**跑了一遍真机：编辑器 + 两个 `.scmod` 全部换新，游戏重启进世界。

**第一道**：游戏里跑 `bt.selftest`（命令 `bt.selftest`）→ **399/399 ALL PASS**
（内核 66 + 包 119 + 命令层 103 + 模式层 33 + 录制 35 + 树编辑 43）——
新构建在真游戏运行时里是健康的。

**第二道**：造一棵真树，全流程**只读取证**（`obs.player` / `obs.world.blocks` /
`ai.blackboard` / `ai.logs`）。为了有"障碍"可绕，墙是 **AI 自己用 `Task.PlaceBlock` 砌的**
（超平坦世界没有天然障碍），拆也是自己用 `Task.Mine` 拆的，最后世界回到原样。

| 步骤 | 只读证据 |
|---|---|
| `Task.PlaceBlock` ×3 在身前砌墙（`(-139,65,191..193)`） | `obs.world.blocks` 复核 0/3 → **3/3 实心（contents=97）** |
| `Task.NavigateTo`（`source=cell`）从墙西侧走到 `(-133,65,192)` | 轨迹：先向西退到 x≈-146.4，再折返向东并**跳过那堵 1 格高的墙**（采样到 y=65.77 / 65.55），停在 `(-134.10, 192.40)`：**距目标格中心 1.60 m ≤ 到达半径 2.0** |
| 同一趟的时间线 | `11:25:25.659 start` → `25.706 heading` → `32.670 arrived`（**7.0 s**）；**走了 22.88 m，直线只有 6.87 m** |
| 回程（`(-141,65,192)`） | `34.783 heading` → `36.703 arrived`（1.9 s），`距离 1.22 m` |
| 第三段（`(-127,65,192)`） | `11:26:13.053 heading` → `14.793 arrived`（1.74 s），`距离 1.59 m` |
| `Service.UpdateSelf` / `Service.UpdateBlockAhead` | `ai.blackboard` 实时读到 `self.Food=0.9`、`self.X/Y/Z/Yaw`、`mine=false` |
| `Task.SelectSlot` | 每段开头都切了快捷栏（slot 3 / 1） |
| `Task.Mine` 拆墙还原 | `obs.world.blocks` 复核 **3/3 → 0/3 空气**，世界回到原样 |

"走了 22.88 m 换 6.87 m 直线"这句就是**寻路**和"朝目标按 W"的区别：它真的在跟着
游戏 A* 给出的一串路径点走（中途还自己补了个跳），而不是顶着墙往前推。

**真机跑出来的三条结论**（都是"不看真游戏就发现不了"的那类）：

1. **游戏的动作冷却是 0.33 s**（`ComponentPlayer.cs:151/230/243`：交互 / 攻击 / 挖掘共用
   同一个 `m_lastActionTime`）。第一次砌墙**只立起来 1/3**：三个 `PlaceBlock` 节点在同一帧里
   各点了一次右键，第 2、3 下被游戏**直接丢掉**。节点之间插 `Task.Wait(0.5)` 之后 3/3 成功 ——
   据此把 `Task.PlaceBlock` 的默认 `interval` 从 `0.3` 调到 **`0.4`**（并写进源码注释：
   连续点击不会被照单全收，要留出冷却）。
2. **够不着就等于按空气**。第一次拆墙失败：站在 11 格开外按左键，`Task.Mine` 老老实实按满
   10 秒然后超时失败（它的判据是"那一格变成空气"，不会因为"我按了"就报成功 —— 这正是要的）。
   在它前面补一个 `NavigateTo` 走到 2 格外，**2.5 s 拆完 3 块**。
3. **`Root.loop` 默认是 `true`**（`TreeCompiler.cs:418` 的 `reader.Bool("loop", true)`）：
   第一版验收树的日志被刷成 **22 次/秒**（"到达 → 重跑 → 又到达"，每轮 3 条 milestone），
   因为到达之后再跑一遍就是"瞬间又到达一次"。验收树显式写 `loop=false`，跑完立刻 `ai.tree.stop`；
   这条默认值本身没错（示例树就是要一直跑），但**做一次性任务时必须显式关掉**。

**证据怎么复现**（脚本没入库，`Mod/Packages/out/` 下）：

```
py -3 Mod/Packages/out/acceptance.py plan        # 读玩家位置算格子
py -3 Mod/Packages/out/acceptance.py place       # 树 1：AI 自己砌墙
py -3 Mod/Packages/out/acceptance.py wall        # 只读复核墙立起来没
py -3 Mod/Packages/out/acceptance.py walk a      # 树 2：寻路绕过墙到 A
py -3 Mod/Packages/out/acceptance.py trace a     # 位置轨迹 + 结论 + 事件日志
py -3 Mod/Packages/out/acceptance.py cleanup     # 树 4：走回去把墙拆了（还原世界）
```

验收用的 5 个包已经删掉（`<实例根>/PlayerAi/BehaviorTrees` 只剩出厂示例 + `enter.game`），
树已 `ai.tree.stop`、输入已 `ai.input.release`，角色留在原地没被 AI 接管。

> 还没做的：**别的任务族**（合成/配方、容器与背包搬运、睡觉/坐骑）见 §11 第 5 条。

### 9.5.44 出厂示例 `su.watch.scbtpak`：盯住最近的玩家（反应式树的标准写法）

用户要的示例：**让角色一直用眼睛跟着最近的玩家转身；看不见他的时候视线就不动；
他重新出现在可观察位置就继续面向**。难点不在"看向某人"，而在**那个玩家一直在动**。

结论：**不需要任何新节点**，出厂示例 `su.watch.scbtpak` 一共 6 个节点：

```
Root(loop=true)
└─ Sequence                          ← 服务挂这里，interval=0（每帧刷新）
   │  UpdateNearestPlayer  targetKey=player  clearWhenMissing=true
   │  UpdateLineOfSight    targetKey=player  key=canSee  maxDistance=48
   └─ Selector
      ├─ Sequence [Blackboard(canSee == true) + observerAborts=LowerPriority]
      │  └─ Task.LookAt  targetKey=player  eyeHeight=1.55  seconds=0.1
      └─ Task.Wait 0.1s             ← 看不见：什么都不做（视线保持不动）
```

**为什么这样成立**（每条都有源码锚点，不是猜的）：

| 事实 | 源码 | 设计后果 |
|---|---|---|
| 玩家每帧都在动 | `BtService.Interval`（`BtDecorator.cs:108`）：`0` = 每帧执行；默认 **0.25s** | 服务必须写 `interval=0`。默认间隔下眼睛追的是 0.25 秒前的位置（4.5 m/s 跑动 ≈ 1 米滞后），看起来一顿一顿 |
| 网络玩家没有控制器 | `PlayerSensor.TryFindNearestPlayer`（`PlayerSensor.cs:195-208`）：遍历 `SubsystemPlayers.ComponentPlayers`，**跳过自己**；`Describe` 只读 `PlayerData` + `ComponentBody` | 联机时"最近的其它玩家"就是那个网络角色 —— 他有没有控制器不影响读位置。真机实测：`playerCount=2`，另一条实体 `isPlayer=true/isSelf=false`，距离 3.02 m |
| 对准要跟手 | `Task.LookAt` 是潜在任务，`OnExecute` 与 `OnTick` **都**调 `Look`（`BtActorTasks.cs:34/39`） | 一挂上就是"持续盯着"：在 `seconds` 期间每个 tick 重新对准黑板里的新位置，不需要每帧重进树 |
| 注入是瞬时旋转 | `InputInjector.LookAt`（`InputInjector.cs:369-396`）注释："瞬时旋转，不受人手速度限制" | 跟手程度 = 调用频率；要更顺滑就调小 `seconds`，要更像真人就换 `Task.FaceEntity`（带 `maxTurnPerSecond`） |
| "看不见就不动" | 写视角的入口只有 `LookAt/Look/LookDelta`；`ReleaseAll`（`ControllerInput.cs:142-147`）**只放按键与鼠标、不碰视角** | **不需要任何节点**：这一帧没人写视角，视线就停在原处（不会归位、不会漂）。所以空转分支只要"什么都不做" |
| "可观察" = 地形不挡 | `PlayerSensor.HasLineOfSight`（`PlayerSensor.cs:443-462`）：从眼睛向目标射线，命中方块比目标更远即通视；**实体不遮挡** | 躲到墙/山后 = `canSee=false`；自己的身体、别的生物不会造成误判 |
| 条件变化要能抢占 | 装饰器基类的 `observerAborts`（`BtAbortMode`，`BtDecorator.cs:9-40`） | 用 `LowerPriority`：看不见的瞬间中断正在跑的 `LookAt`，同一帧就落到 `Wait` 分支 |

**三种状态**（正是用户要的语义）：

| 情况 | 谁在跑 | 结果 |
|---|---|---|
| 看得见（他在跑动） | `Task.LookAt` | 身体与视线一直跟着他转（每 tick 对准一次） |
| 看不见（墙/山挡住、或超过 48 m） | `Task.Wait` | **一次视角写入都没有 → 视线停在原地不动** |
| 重新回到可观察位置 | `canSee` 变 true | 下一个 tick 立刻重新锁定（≤1 帧） |
| 他断开连接 | 服务清 `player` 键 + `canSee=false` | 同上：只是"不动"，不会去盯旧坐标 |

**验收**（`--selftest` 里直接 tick，桩传感器给一个"会动的目标 + 可翻转的可见性"）：

```
watch: aims at the player's eye height while he is visible   aim=10.00,65.55,0.00
watch: re-aims every tick as the player moves               aim=4.00,6.00
watch: issues no look command at all while the player is hidden   before=2 after=2
watch: resumes tracking as soon as he is visible again      lookAt=3 aim=-8.00,2.00
watch: a vanished player clears the key and the gaze stays put    hasPlayer=False canSee=False
```

外加包级断言：出厂内容里真的装了 `su.watch.scbtpak`、两个服务都挂在循环序列上
（`interval:0`）、`LookAt` 那条分支挂着 `Blackboard(canSee == true)`、另一条是 `Task.Wait`、
整包 `validate` **0 error 0 warning**。真浏览器自检还会断言包列表里能列出这个示例。

> 跟 `demo.greet.scbtpak`（示例·打招呼）的分工：那个演示**嵌套包**与"看到人就动"的抢占；
> 这个演示**反应式**（目标会动 + 可见性会翻转），是写"跟随/盯人"类行为的标准骨架。

### 9.5.45 「客户端只露出头的时候，本地看不到」：视线射线瞄错了地方

用户真机实测（联机、一台 host 被 AI 接管 + 一个网络玩家当目标）报的问题：
**瞄准是对的**（实测 host 的 yaw 与"host 眼睛 → 他"的方位角差 **0.02°**），
但**只露出头**的时候 `canSee` 变成 false，盯人树就把视线冻住了 —— 人明明就在眼前露着头。

**根因**：`Service.UpdateLineOfSight` 第一版拿 `target.Position` 打射线，那是角色的**脚底位置**。

```
        host 眼睛 ─────────────► 目标脚底(被 1 格高的墙挡住)  → 判"看不见"
        host 眼睛 ─────────────► 目标头  (在墙上方)            → 其实看得见
```

躲在 1 格高的墙 / 蹲在坑里只露头时，"到脚"的射线必然被挡住。真人判断"看不看得见"看的是
**能看到他哪一块**，不是脚。

**修法**：射线改瞄**头**，并顺带瞄一条**胸口**，**任一通畅即算看得见**。

| 属性 | 默认 | 说明 |
|---|---|---|
| `targetEyeHeight` | `1.55` | 主射线高度（米，相对目标脚底）= **眼睛高度**，与 `Task.LookAt` 的 `eyeHeight` 对齐 |
| `alsoCheckBody` | `true` | 头被挡时再加打一条胸口射线（例如头顶有屋檐） |
| `bodyHeight` | `0.9` | 胸口射线的高度（米） |

`<key>Distance` 写的仍然是**到脚底**的距离（旧语义不变，拿它做"走近点"判断的树不受影响）。
代价是每 tick 最多两次体素射线 —— 相对游戏自己每帧做的射线可以忽略。

**验收**（`--selftest` 直接 tick；桩传感器给出"哪些高度看得见"的窗口）：

```
line of sight: a target showing only his head still counts as visible   canSee=True
line of sight: the rays are aimed at the head and then the chest (never the feet)
                                                                       canSee=True points=2 head=True body=True
line of sight: fully blocked still reads as not visible                 canSee=False
line of sight: alsoCheckBody=false falls back to the single head ray    canSee=False
```

四条分别钉住：①**只露头 = 看得见**（用户报的那条）；②两条射线的**高度**真的是 1.55 / 0.9，
永远不打脚；③整个被挡住仍然是"看不见"（不能修过头）；④关掉备用射线后同样的场景变"看不见"
—— 证明救回来的是那条备用射线，不是别的原因。另外出厂示例 `su.watch.scbtpak` 显式写上
这三个属性，包往返断言也覆盖了它们。

### 9.5.46 通用射线探测 + 模型节点（骨骼）跟踪：把"新问法"变成"改属性"

用户要求（原话）："**射线检测是否能加入，或者增加模型节点跟踪**，这样就可以根据射线检测来查看
类似这种能看到头的定位，或者模型的其他节点的跟踪。"

背景就是 §9.5.45 那个"只露头看不见"：修好之后仍然只是"脚底 + 1.55 m"这个**写死的高度**在近似
"头"。要真正按"头"来判断，得能**取到模型节点的世界坐标**，再**从眼睛朝它打一条射线**。

**源码先读清楚**（这几条决定了实现方式，也决定了哪个坑不能踩）：

| 事实 | 出处 | 结论 |
|---|---|---|
| 骨骼公开可读：`Model.FindBone(name)`、`ModelBone.Name/ParentBone/Transform/Index`、`Model.Bones` | `Model.cs:16-22`、`ModelBone.cs:5-31` | 任意节点都能按名字取 |
| 模型组件公开可读：`ComponentModel.Model`、`GetBoneTransform(i)`；`ComponentCreature.ComponentCreatureModel` / `.ComponentBody` | `ComponentModel.cs:27,53`、`ComponentCreature.cs:15,21` | 玩家和生物都能拿 |
| **Body 骨骼携带世界位置**（`CreateRotationY(yaw) * CreateTranslation(position)`），头/手/腿只带相对旋转 | `ComponentHumanModel.cs:411-416` | 节点世界坐标 = **静止姿态偏移链 × `ComponentBody.Matrix`** |
| 人形模型骨骼名：`Body / Head / Leg1 / Leg2 / Hand1 / Hand2` | `ComponentHumanModel.cs:282-287` | 默认直接可用 |
| 渲染**只对视野内**的模型调 `Animate()` | `SubsystemModelsRenderer.cs:77-92` | **不能**读"动画后的骨骼"：目标在背后时读到的是过期值 → 用静止姿态 |

**三个新件**（都不是"专用件"，而是把一类问法收成属性）：

| 节点 | 干什么 | 关键属性 |
|---|---|---|
| **`Service.Probe`** | **通用射线**：从"我身上的点 / 黑板里的点"朝"目标 / 黑板里的点 / 朝向 / 正下方"打一条（只读） | `from=eye\|body\|point`、`to=target\|point\|ahead\|down`、`mode=blocked\|clear`、`key`、`maxDistance`、三浮点键 `fromXKey…/toXKey…` |
| **`Service.UpdateModelNode`** | **模型节点跟踪**：把某个 actor 的骨骼世界坐标写进黑板 | `targetKey`、`nodeName=Head`、`prefix=node.` → 写出 `node.X/Y/Z`、`node.Exists`、`node.Distance` |
| **`Task.LookAtPoint`** | 看向**黑板里的一个点**（三 float 键），在 `seconds` 期间每 tick 重新对准 | `xKey/yKey/zKey`、`seconds` |

**写出来什么 / 语义细节**：

- `Probe` **两种读数都写**（省得每棵树在脑子里取反）：`<key>Blocked` = 打到了方块、
  `<key>Reached` = 通畅到达终点；`mode` 决定主键 `<key>` 是哪一个 —— `blocked`（默认，和
  `UpdateBlockAhead` 同口径）或 `clear`（"看得见吗"用这个，`seeHead == true` 就是看得见）。
  另外还有 `<key>Distance`、`<key>X/Y/Z`、`<key>Contents`、`<key>Block`。
- **端点取不到时不撒谎**：主键与 `Reached` 都写 false，并把格子/名字那几个键**清掉** ——
  留着上一 tick 的 `seeHead=true`，树会继续以为"还看得见"，比"没有读数"更危险。
- `UpdateModelNode` 的骨骼名写错时：`node.Exists=false`、坐标键被清，并把该模型**可用的骨骼名
  写进 AI 事件日志**（在游戏里就能查到正确名字，不用翻源码）。

**出厂示例 `su.watch.scbtpak` 因此升级成"真正的盯头"**：

```
Root(loop=true)
└─ Sequence.seq   services:
   │   UpdateNearestPlayer   targetKey=player  clearWhenMissing=true
   │   UpdateModelNode       targetKey=player  nodeName=Head  prefix=node.
   │   Probe                 from=eye  to=point(toXKey=node.X…)  mode=clear  key=seeHead
   │   UpdateLineOfSight     targetKey=player  key=canSee  （旧口径，留着做对照）
   └─ Selector.sel
      ├─ Sequence.watch [Blackboard(seeHead == true)]
      │   └─ Task.LookAtPoint(xKey=node.X, yKey=node.Y, zKey=node.Z)
      └─ Task.Wait 0.1s
```

一句话读法：**"从我的眼睛打向他的头骨骼，通得过就一直把头锁在视线中心"**。
比 §9.5.45 的固定 1.55 m 更准：他蹲下、跳起、头随动画摆动时，`node.*` 跟着动。

**验收**（`--selftest` 直接 tick，六条新断言的实际输出）：

```
model node: the head position is the body position plus the bone offset   node=(8.00,66.55,0.00)
probe: eye → head bone reads as visible while nothing blocks it           seeHead=True aim=66.55
probe: a wall closer than the head turns 'seeHead' false and freezes the gaze   seeHead=False lookAt=1/1
probe: a wall behind the head keeps it visible (reached, not blocked)     seeHead=True
model node + LookAtPoint follow the target as he moves                    aim=(5.00,66.55,3.00)
model node: a wrong bone name clears the point and reports Exists=False   seeHead=False
```

外加：出厂示例的包级断言（含 `UpdateModelNode` / `Probe` / `"mode":"clear"` / `LookAtPoint` /
`"nodeName":"Head"`）、三个新属性的**包往返**断言、以及"23 → 26 个节点类型都有中英显示名"。

### 9.5.47 「主持房间」做成了动作包 `su.host.scatpak`

用户要求："**主持房间可以做成一个动作包**"。对 —— 联机开房（右上角「…」→ `CR` → 确认框
`Create`）是一串**纯界面点击**，本来就该是数据：放进包里就能在编辑器的动作包编辑器里改、
任意分辨率回放、不用部署、也不用每次让 Python 脚本现场点。

**关键前提：先按 `E` 打开一个面板**（这条是用户点出来的，也解释了那段代码注释）。

非触屏设备上、**没有模态面板**时，`ComponentGui.UpdateSidePanelsAnimation` 会把整条 HUD
控制栏用 `RenderTransform` **平移到屏幕外**（`m_sidePanelsFactor = 1`）——所以「…」按钮
在桌面鼠标键盘下本来就是"看不见/点不到"的；**打开任意模态面板（E = 仓库）它才滑回屏幕内**。
真机实测（同一秒内前后对比）：

```
按 E 之前： MoreButton center=(1970.6, 55.3)   hittable=false  clickable=false
按 E 之后： modalPanel=CreativeInventoryWidget
            HUD 的 MoreButton center=(1869.4, 55.3)  hittable=true  clickable=true
```

两个连带结论：

- **`MoreButton` 会匹配到 2 个**（HUD 里一个、`CreativeInventoryWidget` 面板里还有一个同名的），
  所以包里必须用**完整路径**，不能用名字选择器：
  `[GameScreen#0]/GamesWidget/[GameWidget#0]/Gui/ControlsContainer/RightControlsContainer/[StackPanelWidget#0]/MoreButton`
- **那个 `E` 必须在帧轨里**：包里其它事件种类（`toggle inventory` 等）回放时**不生效**
  （源码注释写明"回放不依赖它们，只是给人看"），只有 `tracks/input.bin` 的逐帧按键会被重放。
  所以轨要**真录**（录的时候按 E），语义点击才注入。

**包长什么样**（`<实例根>/PlayerAi/BehaviorTrees/su.host.scatpak`，2.964 s / 538 帧，`keys=[E]`）：

```
tracks/input.bin   帧轨：t≈0 按下 E（开仓库）… t≈2.2 再按 E（关仓库）—— 真的按键
tracks/events.json
  t=0.50  ui.click  click:[GameScreen#0]/…/RightControlsContainer/[StackPanelWidget#0]/MoreButton
  t=1.00  ui.click  click:[GameScreen#0]/…/MoreContents/[BevelledButtonWidget#7]     # CR（无名）
  t=1.50  ui.click  click:MessageDialog.Button1                                      # Create
```

`ai.action.validate name=su.host` → `ok / 0 error / 0 warning / replayable=true / keys=[E]`。

**怎么做的**（`Mod/Packages/out/make_host_room_action.py`）：

1. `ai.record.start` → **按 E 开面板 → 等 → 按 E 关面板** → `ai.record.stop` → `ai.record.save`
   —— 先拿到结构合法、可回放、**带真按键**的轨。
2. 用脚本把三条 `ui.click` 语义事件写进 `tracks/events.json`（原包自动备份成 `.bak`），
   时间点落在"面板开着"的那段窗口里。

**只注入 `ui.click`，绝不注入 `mouseDown/mouseUp`**：后者回放时是**真的世界鼠标左键**
（出拳/放方块），而 `ui.click` 走软光标那条 UI 通道。录制出来的包两者都有，是录制器的
习惯；手写这种"纯界面导航包"时必须只留前者。

**真机踩到的坑**（都如实报出来了）：

| 现象 | 原因 | 怎么办 |
|---|---|---|
| `direct` 点击被拒 `element_off_screen`（落点 1970.6 落在 1920×1080 之外）| 上面那条：桌面无模态面板时控制栏被平移到屏幕外 | **先按 E 开面板**（包自己会做），不要试图"修窗口" |
| `ambiguous_selector: 'MoreButton' matches 2 elements` | 仓库面板里也有一个同名按钮 | 用**完整路径** |
| 回放日志 `[action-play-done] … err=ui click(s) never became clickable` | 第一下没点开 `MoreContents`，后面 `CR`/确认框自然不存在 | 链路逐步依赖；先看第一下的原因 |

**怎么用**：

- 命令：`ai.action.play name=su.host`（`Mod/Packages/out/redeploy.py` 已经**首选**它：
  一条命令重装完以后自动进世界、然后回放这个包开房，失败才退回 Python 现场点）。
- 行为树：`Task.PlayActionPackage(packages=[su.host])`（挂在进图后的第一条即可）。
- 编辑器：动作包下拉里能直接打开、改时间点（`t`）和目标（`detail`），保存后热重载。

**真机验收**（真游戏、真联机会话；`redeploy.py --no-host` 重启进图，此时**没有主机**）：

```
20:40:24.449 [action-play]      su.host.scatpak id=su.host 2.96s/538 帧 可回放 keys=[E] repeat=1 uiEvents=3
20:40:26.211 [Server] Client "58960" at 127.0.0.1:58960 created game 0.
20:40:27.688 [action-play-done] replay(su.host.scatpak finished frame=537/538 t=3.23s loops=1)
```

三条一起看才是完整的证据链：

- `keys=[E]` + `uiEvents=3`：回放真的按了 E、真的重放了三次语义点击；
- `[action-play-done]` **没有 `err=` 字段**（失败时会写 `err=ui click(s) never became clickable: …`）
  —— 三次点击全部命中；
- 游戏自己的日志里 **`[Server] … created game 0`** 与回放同一时刻出现 = **房间真的建起来了**。

回放后复核：`obs.dialogs` 已空（Create 确认框弹过又关掉）、`obs.player.hud.modalPanel = null`
（第二次 E 把仓库关掉、回到正常游玩）、`playerCount=1`（此时还没人连进来）。

### 9.5.48 `Task.FollowEntity`：跟随另一个角色（踩脚印 / 不行就绕 / 保持两格）

用户要求："跟随另一个角色移动，**先靠近**；然后跟随分两种：**先尽可能走另一个角色所走的方块**，
**不行的时候可以绕着过去**（A\*）；跟随的时候**保持两格的水平间隔**。"

做成**一个**任务（`Bt/BtFollowTask.cs`）而不是三个，因为这三件事是同一个闭环在不同条件下的分支，
拆开反而更难保证"保持 2 格"这种连续约束：

| 情况 | `Task.FollowEntity` 怎么做 |
|---|---|
| 目标还没有足迹（刚出现 / 在远处） | 借游戏 A\* 走过去 —— 这就是「先靠近」 |
| 目标前面走过一串格子 | 追**下一个还没踩过的脚印**（足迹链：每 tick 采样目标的格子、按 `trailMaxAge` 过期、走到半径内就消费掉）|
| 脚印太旧 / 追着追着走不动（卡住） | **丢掉脚印改走 A\*** —— 这就是「不行就绕过去」 |
| 水平距离已 ≤ `keepDistance`（默认 2） | **站住**（照样盯着他），不往人身上挤 |

**镜头永远锁在他身上**（§9.5.49）：走路方向由 `w/a/s/d` 在**身体基**上的投影决定，
相机不参与 —— 所以"踩脚印 / 绕过去 / 站住"这三种分支切换**都不会带动镜头**。

`mode` 就是"跟随分两种"那个开关：`auto`（默认，脚印优先、走不通自动切 A\*）/ `trail`（只走脚印）/
`path`（只用 A\* 绕）。其余可调：`trailLength` / `trailMaxAge` / `trailCellRadius` /
`trailStuckSeconds` / `repathSeconds` / `repathMoveThreshold` / `forwardKey` / `backKey` /
`leftKey` / `rightKey` / `jumpKey` / `standHysteresis` / `eyeHeight` / `timeout`（0 = 一直跟）。

**顺带修掉一个真隐患**：寻路返回**空路径**时（队列满 / 不可达）航点数一直是 0，
"没航点就重算"那条会**每帧**成立 —— 而游戏的寻路是"队列上限 10、后台每 250ms 处理一条"，
每帧重算会把队列打满、把别的生物和玩家自己的寻路挤掉。所以加了 `MinRequestInterval = 0.25s`
的硬下限，并写成断言（见下）。

**验收**（`--selftest` 直接 tick；桩要**真的朝瞄准点挪动**，否则卡住判定会正确地丢脚印改走 A\*，
就测不到脚印那条腿；桩的身体朝向每帧对准目标 —— 那正是任务每次 `LookAt(目标)` 的副作用）：

```
follow: walks the target's footsteps (aims at the oldest un-walked cell, not at him)  aim=(2.50,2.50) pathRequests=0
follow: the camera stays locked on the target while the feet walk his footsteps       gaze=(2.00,65.35,4.00)
follow: consumes footsteps as it reaches them (aim moves along the trail)             aimZ=4.50 before=2.50
follow: keeps the 2 block horizontal gap (stops instead of pushing into him)          held=- tree=InProgress
follow: falls back to A* once the trail is stale (auto mode)                          requests=3
follow: being stuck keeps using A* (trail dropped, position frozen)                   before=3 after=9
follow: a vanished target fails and releases every movement key                       tree=Failed held=-
follow: mode=path walks the game's A* waypoints while the camera stays on him         requests=1 aim=(8.00,4.00) gaze=(30.00,10.00)
follow: an empty path does not turn into a per-frame request storm                    requests in 2s=8 held=d+s
follow: the gaze stays on the target while walking toward his footsteps               gaze=(6.00,0.00) walkAim=(6.50,0.50)
follow: walking/standing does not flip-flop while the gap hovers at keepDistance      flips=1 held=-
follow: the camera is locked on him even while standing                               gaze=(2.40,0.00)
follow: with the camera locked on him, the feet still walk the side footstep (strafe) gaze=(6.00,0.00) walkAim=(0.50,6.50) held=d
follow: re-reads the target's live position (a stale blackboard snapshot cannot step the camera) gaze=(10.00,0.00) 黑板快照还在 6.00
follow: w/a/s/d pressed in the body frame reproduce the wanted walk direction         wrong=0/8 worst=7.1deg aim=(-3.5,4.5) held=d+s
```

第一条是最要紧的：目标走的是"先 +Z 再 +X"的折线，而跟随者瞄准的是**第一个还没踩过的脚印
(2.5,2.5)**、不是目标的当前位置，而且**没发过任何 A\* 请求** —— 即"真的在走他走过的格子"。
第二条是同一 tick 的**视线**：落在目标本体 `(2.00,65.35,4.00)`（脚底 +1.35 眼睛高度）——
"看的是人、走的是格心"，两条断言合起来就是解耦的证据。
倒数第二条是最能说明"侧向走位"的一条：脚印在 **+Z**（`walkAim=(0.50,6.50)`），
目标在 **+X**（`gaze=(6.00,0.00)`），脚下按的键是 **`d`（右平移）** —— 镜头一动不动，
人却朝他走过的路走。最后一条把"键位映射"本身钉住：8 个方向各走一次，
把按住的键反解成世界方向与想走的方向比对，最大误差 7.1°（8 方向量化上限 22.5°）。

出厂示例 `su.follow.scbtpak`（"示例·跟随最近的玩家"）就是最小用法：
`UpdateNearestPlayer(interval=0)` → 门 `Blackboard(player IsSet)` → `Task.FollowEntity(mode=auto, keepDistance=2)`，
没人时落 `Wait` 待机（免得每帧报"目标没了"）。**`interval` 必须是 0** —— 理由见 §9.5.49 第三轮。

> 与 `Task.NavigateTo` 的关系：两者共用同一套传感器 API（`TryRequestPath/GetPathStatus/CopyPath/
> ClearPath`），但**没有**共用实现 —— 跟随的终点每帧都在动，到达判据是"水平距离"，
> 还多一层"脚印优先"，硬套会让 `NavigateTo`（已真机验收）变复杂。这条重复是**有意**的，
> 等跟随的语义稳定后再考虑抽公共件。

**真机验收**（真联机：host 被 AI 接管、客户端是那个网络玩家；host 的树切到 `su.follow.scbtpak`）：

```
采样 205 次 / 44.8 秒
水平间隔：平均 4.10 / 最小 0.41 / 最大 14.69    在 3.2 格内占 64%
目标移动 170.1 米，跟随者移动 193.9 米
踩在「他最近 8 秒走过的格子」上的比例：53%
起始：gap 14.69 → 2.2 秒内收到 7.28（"先靠近"那条腿）
```

跟随者在动的目标后面**一直在跟**（走的里程比目标还多 —— 多出来的是绕路和追脚印），
间隔大多数时间贴在 `keepDistance` 附近。

**真机抓到的三个问题**：

1. **镜头反复切 → 相机与走路方向彻底解耦**（用户两次实测："跟随另一个角色移动的时候，
   镜头出现反复切的情况，而不是一直盯着角色" → 修完仍报"现在仍然在抖"）。

   第一次的修法（**不够**）：`keepDistance` 是硬阈值，距离抖一下每 tick 就在"站住（盯目标）/ 走（盯脚印）"
   之间翻转；于是加了 `standHysteresis` + "视线锥 `gazeConeDegrees`"。**但锥本身又是新的抖动源**：
   走路瞄准点（格心）在锥边界附近来回进出，视线就在"目标 ↔ 航点"之间反复切 —— 用户看到的"抖"。

   最终修法（§9.5.49）：**视线永远只盯目标**（删掉 `gazeConeDegrees`），
   走路方向改由 `w/a/s/d` 在**身体基**上投影得到。`standHysteresis` 保留，但它现在只防
   "步伐动画/视点起伏反复重启"，与镜头无关。
2. **告警风暴**（已修）：`stuck while walking the trail -> drop it and use A*` 在 `Game.log` 里刷了
   **2692 行** —— 因为丢掉脚印之后，下一 tick 又从目标位置把脚印记了回来，于是每帧再丢一次、再报一次。
   现在：卡住判定挪到"记脚印"之前、卡住时不记、并且**每次卡住只报一次**
   （走起来才复位）。这条也提醒：热路径上的 `Warn` 必须自带"只报一次"的闸。
3. **目标自己走过来时，间隔会小于 2 格**（实测最小 0.41 m）：`keepDistance` 只约束**跟随者不往前挤**；
   如果是**目标**走向跟随者，跟随者不后退就挡不住。要不要"他贴太近就退开"（加一个 `minDistance`
   + 按后退键）等用户确认 —— 这属于新行为，要用户点头才做。
   （`backKey` 现在只用于"走路方向落在身体后方"这一种情形，SC 里按 S 速度打 6 折。）

**真机验收（端到端）**：真联机会话里，`su.host.scatpak` 一条命令把房间开起来，**真客户端进来了**：

```
20:59:37 [action-play]      su.host.scatpak id=su.host ... uiEvents=3
20:59:40 [Server] Client "60877" at 127.0.0.1:60877 created game 0.
20:59:40 [ScMP] GameCreated, ClientID=0, Creator=127.0.0.1:60877
20:59:52 [ScMP] Reserved PlayerIndex 0 for ClientID 1 (Basil)
20:59:52 [Server] Client "46454" at <平板地址见 AGENTS.local.md>:46454 joined game 0 at step 1006
20:59:53 [ScMP] Created transient network player for ClientID 1, PlayerIndex=0
```

`[action-play-done]` 没有 `err=`（三次点击全中）、`created game 0`（房间建立）、
另一个真机玩家 `joined game 0`（外面的人**真的能连进来**）。

**判据：`CR` 按钮的文字变成 `IF` = 房间已建好**（用户实测）。

这是最省事的"开房成功"读法 —— 不用翻日志、不用打开面板（`ui.elements` 连隐藏控件都带 `text`）：

```
未开房： [GameScreen#0]/…/MoreContents/[BevelledButtonWidget#7]  text = "CR"
已开房： 同一个控件                                              text = "IF"
```

`redeploy.py` 就用它：① 回放前先看状态，已经是 `IF` 就跳过（避免重复开房）；
② 回放后再看一次，只有变成 `IF` 才算这一步成功（并打印出来）。
这条也解释了之前那次"回放到最后报 Button1 没就绪"的另一种情形：房间其实已经开着，
`CR` 点了不会再弹确认框。

**回放前必须把「…」面板归零**（用户实测 + 踩过两次）：

- `MoreButton` 是**开关**：按一次展开、再按一次关闭；
- 如果上一轮回放/手动操作把它留在"已展开"，包里的第一次点击就会把它**关掉**，
  后面 `CR` 永远点不到 —— 表现是回放到最后才报
  `ui click(s) never became clickable: MessageDialog.Button1`（**链路逐步依赖，报错总在最后一步**）；
- 所以 `redeploy.py` 在回放前会先看 `CR/TA/MP` 是否可见（= 面板是否展开），展开就点一次收起，
  再回放包。判据就是这三个按钮的 `visible/hittable`。

> 另一个实测教训：**UI 包不要带漂移基准**。录制时会记起点位置/朝向，回放时逐条比对
> （容差 45°），换个朝向就被如实拒放：`replay drift: view drifted 213.0° from the keyframe`。
> `make_host_room_action.py` 现在会把 `keyframes.json` 的 `frames` 清空 —— 纯界面导航与角色
> 位置/朝向无关，本来就不该拿它当回放前提。

### 9.5.49 相机与走路方向解耦：镜头锁在目标身上（`Task.FollowEntity`）

用户要求（原话）："跟随角色走，**只需要镜头一直锁在身上就行了**，现在仍然在抖。"

#### 先把引擎事实钉死（读源码，不猜）

| 事实 | 来源 |
|---|---|
| 相机朝向 = **身体旋转 × 头部偏航** `LookAngles.X`（玩家恒为 0）× 俯仰 `LookAngles.Y` | `ComponentCreatureModel.cs:206-209`、`ComponentHumanModel.cs:442-449`、`FppCamera.cs:21-31` |
| 玩家的**水平朝向就是身体 yaw**（鼠标 X 驱动 `TurnOrder` → `body.Rotation`） | `ComponentLocomotion.cs:302-309`、`ComponentPlayer.cs:142` |
| 走路方向 = **身体基**上的 `WalkOrder.X * 身体右 + WalkOrder.Y * 身体前`（水平分量） | `ComponentLocomotion.cs:400-402,452` |
| 键盘 WASD 只是往 `WalkOrder` 里塞 ±1/±1（`D=+X, S=-Z, W=+Z, A=-X` 世界轴写法在这里**只是中间量**） | `ComponentInput.cs:171-177` → `ComponentPlayer.cs:140` |
| **`LookAutoLevelX` 玩家模板没关**（默认 true，每帧按 10/s 往 0 拉头部偏航） | `ComponentLocomotion.cs:352` + `Pak/Database.xml:389-398`（只覆盖了 `LookAutoLevelY=False`） |

结论：**"身体朝走路方向、只把视线偏过去"这条路走不通** —— 写 `LookAngles.X` 等于每帧跟引擎的
自动回正拔河（`LookAngles += LookSpeed * ( -10*LookAngles.X/LookSpeed ) * dt`，60fps 下每帧衰减 ~17%），
画面只会更抖。所以反过来做：**相机（=身体）锁死目标，走路方向用按键在身体基上投影**。

#### 实现（`Bt/BtFollowTask.cs`）

```text
每 tick：
  ① LookAt(目标位置 + eyeHeight)          ← 镜头锁死（唯一一次转向调用；删掉了视线锥）
  ② direction = normalize(水平(aim - self))
  ③ (f, r) = (direction · 身体前, direction · 身体右)      ← 引擎自己的 Matrix.CreateFromAxisAngle(UnitY, yaw)
  ④ 8 方向量化（sin22.5° = 0.38268）：f>t 按 W、f<-t 按 S、r>t 按 D、r<-t 按 A
  ⑤ 只在**变化**时下发按键（Keyboard 的按下状态是持久的，Source: Engine/Engine/Input/Keyboard.cs:128-130
     `BeforeFrame` 是空的），另外每 1s 强制重发一次自愈
```

- 站住时（`keepDistance` 内）同样 `LookAt(目标)`，只是四个移动键全松开；
- 走路方向的量化误差 ≤ 22.5°（自检实测最好/最差：8 个方向里最大 7.1°）；
- 方向落在身体**后方**时只能按 S（SC 里 0.6× 速度），这是"镜头不动"的代价，只影响走位不影响镜头。

#### 自检（编辑器 `--selftest`，206/206）

新增/改写的 6 条断言（见 §9.5.48 的验收块）：**视线锁目标**（走脚印时 `gaze=(2.00,65.35,4.00)`）、
**站住也锁**（`gaze=(2.40,0.00)`）、**侧向走位**（脚印在 +Z、目标在 +X 时按 `d`）、
**旧快照也要锁得住**（黑板里停在 6.00、真位置 10.00 → 视线落在 10.00）、
**键位映射反解**（8 方向最大误差 7.1°）、**站走不翻转**（`flips=1`）。
桩的执行器改成记**按住集合**（`HeldKeys`）—— "最后一次调用的键"在 4 键组合下没有意义。

#### 真机第三轮："一段一段地在跳" = 黑板里存的是**快照**，而服务 0.2s 才写一次

镜头不切了、也不抖了，但用户接着报："现在是走动的时候，视角跟踪有些不连贯的感觉，
**一段一段地在跳**。"

根因在本项目自己的架构上（**不是**网络同步的问题 —— 用户也确认了"网络玩家在本地的副本是连续的"）：

- 黑板存的值是 `AiActorView` —— **值类型快照**（`Ai/Core/AiBlackboard.cs:47-53` 里 `object` 装箱一份拷贝）；
- 写它的是树上的 `Service.UpdateNearestPlayer`，而服务由 `BtService.Interval` 限流
  （`Bt/BtDecorator.cs:108`：`0` = 每帧，默认 **0.25s**）；
- 出厂跟随示例 `su.follow.scbtpak` 当时写的是 **`interval: 0.2`** ——
  于是 `Task.FollowEntity` 每 0.2 秒才拿到一个新位置，`LookAt` 追的就是**阶梯**。

这条坑 README §9.5.36 已经写过一次（"眼睛追的是 0.25 秒前的位置（4.5 m/s 跑动 ≈ 1 米滞后），
看起来一顿一顿"），注视示例当时改成了 `interval=0`，**但跟随示例又踩了一次**。

修法两条（一条立即生效、一条防复发）：

1. **包**（热重载，不用重启）：`svc_player.interval` `0.2 → 0`。
   改的是实例里的 `publish/Windows/PlayerAi/BehaviorTrees/su.follow.scbtpak`
   （工具：`Mod/Packages/out/set_service_interval.py su.follow.scbtpak 0 --id svc_player`），
   源码同步改成 `PackageTemplates.BuildFollow` 里的 `Num(0)` + 文档，**下次装模板就是对的**。
2. **任务自己刷新目标**（`BtFollowTask.RefreshTarget`，**需要重启才生效**）：
   每 tick 用 `IAiSensor.TryFindPlayer(快照里的名字)` 重取一次真位置；生物则按
   "快照位置附近 + 同名"再取一次。**树里 `interval` 写成多少都不会再让镜头跟着阶梯走**。
   断言：黑板里停在 6.00、传感器里的真位置已经 10.00 → 视线必须落在 **10.00**。

> 一句话教训：**"值类型快照 + 限流服务"是这类"一顿一顿"的通用配方**。
> 凡是"每帧都在动的东西"（玩家位置、模型骨骼），要么服务写 `interval=0`，
> 要么消费方自己重取一次 —— 两者至少有一个，否则一定会看到阶梯。

#### 真机验收（这一轮）

| 项 | 数据 | 怎么测的 |
|---|---|---|
| 镜头锁定（站住时，1521 个采样 / 120 秒 / 60ms 间隔） | 视线角误差 **平均 0.02° / 最大 0.02°**；按键一直为空（间隔 1.67 格 ≤ `keepDistance` 2 → 站住盯着他） | `Mod/Packages/out/follow_camera_trace.py start 120 --gap 0.06` |
| 面板不再卡住角色 | `obs.player.hud.modalPanel = null`、`input.keysDown = []`（开房动作包结束后面板已关、无残留按键） | 同上 + `pa_cmd.py obs.player` |
| 开房动作包（压短后） | `playing su.host.scatpak id=su.host **3.79s/658 帧**` → `CR 按钮现在是 'IF'` | `redeploy.py` 第 ⑥b 步 |
| 游戏内自检 | `bt.selftest` **399/399**（新 DLL，270214 字节 @ 22:03） | `pa_cmd.py bt.selftest` |
| 用户实测 | "视角跳转的情况减轻了" → "一段一段地在跳" → 修完 **"没问题了"** | 用户口述（画面问题最终以用户观感为准） |

> 走路时的逐帧数字没能留下来：两次 60ms 采样窗口里客户端都是**站着不动**的
> （间隔恒定 1.67/1.62 格、`keysDown` 一直为空）。要复现"边走边量"，
> 让客户端在采样窗口里连续走 30s 即可 —— 工具已经ready（`follow_camera_trace.py`
> 会同时打印 `err`=锁定误差 与 `moveVsHe`=移动方向与"朝向他的方向"的夹角，
> 后者 >20° 就是"镜头锁人、脚下走路"的直接证据）。


### 9.5.50 动作包：把"开仓库面板"的窗口压到最短（`su.host.scatpak`）

用户实测："按 E 打开仓库的时候，角色是**不能移动**的，必须关闭才能移动。"

源码确认这是引擎行为、不是 Mod 的锅：`ComponentInput.cs:143-153` ——
`ModalPanelWidget != null || DialogsManager.HasDialogs(...)` 时，**整段键盘移动输入被跳过**
（只保留"可见鼠标光标"那条分支），所以面板开着 = 角色动不了。

而 `su.host.scatpak` 原来的帧轨是"录一条长的空轨 + 注入 3 个语义点击"：

```
帧轨 2510 帧 / 13.83s，键表只有 E： t=0.09 按 E（开面板）   t=13.06 按 E（关面板）
语义点击：t=1.0  MoreButton「…」  t=2.0  CR  t=3.0  确认框 Create
```

也就是说**干完活只用了 3 秒，后面 10 秒纯白等**，而白等期间角色被面板钉住。
`Mod/Packages/out/shorten_host_action.py` 把轨裁到 **658 帧 / 3.79s**：
保留 t≤3.4s 的原始帧（一个字节不改）+ **把原来那次"关面板的 E 帧"原样搬过来**（只改与前帧的间隔
为 200ms）+ 40 帧静止尾。面板打开窗口 **~13s → ~3.4s**。

配套的只读工具：`Mod/Packages/out/inspect_action_track.py <包名>` 按时间线打印
manifest / 语义事件 / 帧轨按键 / 视角累计漂移（纯 UI 包必须是 0）。

## 10. 从 CmdBridgeMod 迁移的试错结论（改本 Mod 前先看这里）
1. **窗口失焦 = 全部输入被丢弃**：`ComponentInput.cs:91-94` 在 `!Window.IsActive || !PlayerData.IsReadyForPlaying`
   时把 `m_playerInput` 置空。行动前用 `IAiSensor.IsInputAccepted` 自查。
2. **交互射线包含身体**：`ComponentMiner.cs:384` 的身体射线带 0.35 m 膨胀，最近命中是身体时
   `Raycast<TerrainRaycastResult>` 返回 null，`Place`/`Interact` 会被静默跳过。靠近其他玩家时放置/交互要绕开其碰撞体。
3. **`ComponentMiner.Place` 的前置**：`IsBlockPlacingAllowed`（需站立/浸水/梯子等）与 0.33 s 动作冷却。
4. **创造模式目录顺序**：`ComponentCreativeInventory.cs:80` 按 `Block.DisplayOrder` 排序，不是方块索引。
5. **槽位搬运**：`InventorySlotWidget` 左键只切当前槽；长按设"分割源"后松手会被同一格 `Click` 取消；
   跨容器 `SpecialClick` 需要"另一个 inventory"；创造模式下目录与快捷栏是同一个 `ComponentCreativeInventory`。
6. **右键 = 交互/放置**（`IsMouseButtonDownOnce(Right)`），左键 = 挖掘；`downOnce` 只在帧首写入才生效。
7. **UI 点击是两帧动作**（按下帧派生 `Press/Tap`，松开帧派生 `Click`），并要清 `WidgetInput.m_mouseDownPoint`。
8. **朝向约定**：yaw 从 `ComponentBody.Rotation` 提取（公式见 `ComponentLocomotion.cs:303`）；
   俯仰在 `ComponentLocomotion.m_lookAngles.Y`（±82°，setter 为 private）。
9. **方块值布局**：`contents = value & 0x3FF`，`light = (value >> 10) & 0xF`，`data = value >>> 14`。
10. **`obs.world.blocks` 的参数名是 x/y/z**（不是 centerX/…）—— 传错会被忽略并默认扫描玩家所在格。

## 11. 后续（等方案）

0. **自检入口**：`sccmd aiselftest`（= 命令 `bt.selftest`）一次跑八套 —— 行为树内核、包格式/热重载/树库、命令层、模式层、录制、内存改写/导出、观测日志、动作包（轨道格式/校验/回放/漂移/行为树集成/出厂示例）。
   日志文件：`<实例根>/PlayerAi/Logs/PlayerAi.log`（滚动 256 KB × 3；写不进去只记录原因，绝不影响游戏）。
   当前验证方式：临时工程 `%TEMP%\pa_selftest` 编译 `Bt/**` + `Package/**` + 命令层（只引用
   `Engine.dll`/`EntitySystem.dll`，不依赖游戏运行时），执行 `dotnet run` 即得逐条结果 ——
   现在合计 **474/474 PASS**（内核 66 + 包/热重载/树库 119 + 命令层 103 + 模式层 33 +
   录制 35 + 改写/导出 43 + 观测日志 24 + 动作包 51）—— 相对 §9.5.40 的 425 涨了 49 条，
   分布在内核（+3）、命令层（+33）、录制（+4）、动作包（+9）。
1. ~~真实行为状态：寻路（示例是直走，撞墙会卡）~~ → **已做**：§9.5.40 借游戏 A* 的
   `Task.NavigateTo`（含 §9.5.41 的坐标模式）、§9.5.40 的世界交互族（挖/放/攻击/交互/选快捷栏）、
   §9.5.42 的 `Task.UseItem` / `Task.Jump`、§9.5.39 的传感器服务族与 `Task.FaceEntity` / `Task.Emit`。
   还剩：真机验收（见 §9.5.40 末尾的说明）。
2. 远端角色：远端执行 + 本端复现的通道设计。
3. 激活与调试入口：命令、配置热调、观察接口（可考虑复用 CmdBridgeMod 的命令路由）。
4. 配置外置：按 CmdBridgeConfig 的模式改成 JSON。
5. 其余任务族（还没做的）：合成/配方（可先用 `Task.UiClick` + UI 拾取走界面）、
   容器与背包搬运（拖拽是 `InventorySlotWidget` 的两步操作）、睡觉/坐骑这类长动作。
