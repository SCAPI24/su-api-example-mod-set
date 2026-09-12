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
- 前端渲染层是可替换的：`Web/engine-adapter.js` 是 lowcode-engine 的挂载点（D15 spike 结论见
  `doc/player-ai-plan.md` §11：真实包名是 `@alilc/lowcode-engine`，6.17 MB、只有 CJS/ESM、
  ext 包要 React16 + @alifd/next，且与我们包格式不同构 → 本期自研）。

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
