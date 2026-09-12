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
    ├── PackageRoots.cs                        双目录白名单：<实例根>/PlayerAi/BehaviorTrees（可写）+ Mods/PlayerAiMod/...（只读）
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

| 优先级 | 位置 | 用途 | 可写 |
|---|---|---|---|
| 1 | `<实例根>/PlayerAi/BehaviorTrees/` | 用户 / AI / 编辑器日常改的包（**实例根 = 游戏 exe 所在目录**） | ✅ |
| 2 | `<实例根>/Mods/PlayerAiMod/PlayerAi/BehaviorTrees/` | 出厂示例包，**随 Mod 分发**（Mod 加载时自动补齐，缺什么补什么、绝不覆盖） | ❌ 只读 |

同名以实例目录优先：想改出厂示例就把它复制到实例目录再改，原文件不会被冲掉。
`references` 里的路径必须是相对路径且解析后仍落在上面两个目录内（防路径穿越）。

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
sccmd ai trees           # 两个包目录里的树（实例目录优先，标出 active）
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

- **包名按"与树包同路径"解析**（计划 §4.4）：把 `.scatpak` 和 `.scbtpak` 放同一个目录即可。
  查找链是**实例目录 → Mod 只读分发目录**（同名时实例优先），所以出厂示例装在 Mod 目录里也能直接播放。
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
# 或者发布成单文件 exe（4.93 MB）
dotnet publish Mod/PlayerAiEditor/PlayerAiEditor.csproj -c Release -r win-x64 \
  --self-contained false -p:PublishSingleFile=true -o publish/editor
publish/editor/PlayerAiEditor.exe --root publish/Windows      # 浏览器会自动打开 127.0.0.1:8760
publish/editor/PlayerAiEditor.exe --selftest --root publish/Windows   # 无头自检 41 项
```

- **物料区**读游戏的节点注册表（`GET /api/schema`）：类型、形状（组合/任务）、属性表（类型/默认值/枚举）
  全部来自 `BtNodeRegistry`，所以**编辑器里能选的，游戏里一定能跑**；代码专用节点（lambda）不在物料里。
- **动作包也是物料**（`GET /api/actions`）：物料区最后一组列出两个目录里的 `.scatpak`
  （时长/帧数/**能不能回放**/来源/只读/被同名包遮住）；点一下就挂一个 `Task.PlayActionPackage`，
  或加进已选中播放节点的 `packages`（属性区里是**勾选列表**，不是让人手打文件名）。
- **画布**改的就是包格式本身（`tree.json` 的 children/decorators/services/properties）。
- **保存**先跑**游戏内同一份校验器**，通过才原子写；只允许写实例目录（Mod 分发目录只读），
  覆盖需要显式确认，写后还会自己重新装载一遍确认游戏读得动。
- **推送热重载**：保存后点一下，编辑器直接通过 CmdBridge 通道给**正在运行的游戏**发
  `ai.tree.notify`（带哈希）—— 不重启、不重载世界。
- **动作包那一组按钮**：`校验`（结构 + 能否回放）/ `试跑`（`ai.action.play`，游戏里真放一遍）/
  `停止`（`ai.action.stop`，释放输入）/ `自造`（写一个新的示例包进实例目录）。
  控制通道的报错分两类：`game_unreachable`（没连上）与 `game_refused`（连上了但游戏拒绝了，
  例如游戏里那份 Mod 还没有这个命令 → 重新部署并重启）。

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
| 前端资源没进新构建 | 改的是磁盘上的 `app.js`，`publish/editor/PlayerAiEditor.exe` 里嵌的还是旧前端（`dotnet` 增量编译不跟踪 `Web/**` 这类内嵌资源） | 新增 `check_player_ai_build.py assets`：把内嵌资源与 `Web/` 源码**逐字节**比对，并确认 `index.html` 的构建时间戳已替换；改了前端必须先 `build` 再 `assets` |
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
4. 新增 `--print-root`：只打印判定出来的实例根与两个包目录就退出，专门用来排查这类问题。

验证：`assets` 门新增两条 —— ①从 `publish/editor` 当工作目录、不带 `--root` 跑 `--print-root`，断言它自己找到 `publish/Windows`；②起服务后查 `/api/packages`，断言 `count >= 1`（空列表就是用户看到的那个现象，必须能被测出来）。

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
   现在合计 **425/425 PASS**（内核 63 + 包/热重载/树库 119 + 命令层 70 + 模式层 33 + 录制 31 + 改写/导出 43 + 观测日志 24 + **动作包 42**）。
1. 真实行为状态：跟随 / 寻路（示例是直走，撞墙会卡）/ 采集 / 建造 / 战斗 / 交互 UI。
2. 远端角色：远端执行 + 本端复现的通道设计。
3. 激活与调试入口：命令、配置热调、观察接口（可考虑复用 CmdBridgeMod 的命令路由）。
4. 配置外置：按 CmdBridgeConfig 的模式改成 JSON。
