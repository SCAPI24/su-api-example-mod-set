# PlayerAiMod 参考资料与借鉴笔记

> **用途**：把"读外部资料/源码得到的结论"沉淀在这里，供实现与复查复用；方案文档（`doc/player-ai-plan.md`）只保留最终结论。
>
> **规则**：
> 1. 只借鉴**语义、数据流与结构**，**不逐行抄代码**（UE 是专有许可源码，尤其注意）。
> 2. 每条结论必须写清：来源、落点（影响方案的哪一节）、日期、可信度。
> 3. 不以记忆为准：不确定的写法标 `待源码确认`（仓库铁律）。

---

## 1. 虚幻引擎行为树（UE）

### 1.1 获取方式

需要 Epic 账号已关联 GitHub（仓库为专有许可）。**只取 AIModule 一个目录**（几十 MB 级），够覆盖下表全部主题：

```bash
git clone --filter=blob:none --sparse https://github.com/EpicGames/UnrealEngine.git ue-ref
cd ue-ref
git sparse-checkout set Engine/Source/Runtime/AIModule
```

### 1.2 文件索引（按语义主题）

根路径：`Engine/Source/Runtime/AIModule/`

| 主题 | 位置 | 读它回答什么 |
|---|---|---|
| 节点基类与结果语义 | `Classes/BehaviorTree/BTNode.h/.cpp`、`BehaviorTreeTypes.h` | `EBTNodeResult`（Succeeded / Failed / Aborted / InProgress）；`ExecuteTask` / `TickTask` / `AbortTask` 的调用时机 |
| 组合节点 | `BTCompositeNode.h/.cpp`、`BTComposite_Selector.h`、`BTComposite_Sequence.h`、`BTComposite_SimpleParallel.h` | 活跃子节点记忆、`FBTCompositeChild`（子节点 + 装饰器 + 装饰器操作）、成为/失去焦点回调 |
| 装饰器与中断 | `BTDecorator.h/.cpp`、`BehaviorTreeTypes.h`（`EBTFlowAbortMode`） | `ObserverAborts`（None / Self / LowerPriority / Both）的真实实现、"重新搜索"如何触发、如何中断已运行分支 |
| 只条件装饰器范例 | `BTDecorator_Blackboard.h`、`_CompareBBEntries`、`_Cooldown`、`_TimeLimit`、`_Loop`、`_ConeCheck` | 每个装饰器的公开参数与判定细节（与官方 Decorators 文档页一一对应） |
| 服务 | `BTService.h/.cpp` | `Interval` / `RandomDeviation` 的调度方式；"只随有焦点的组合节点 tick"的语义 |
| 任务范例（含子树） | `BTTask_Wait.h`、`BTTask_MoveTo.h`、`BTTask_RunBehavior.h` | 跨帧任务怎么写；`BTTask_RunBehavior` 如何"引用另一棵行为树"（对应我们的 `Subtree` + 嵌套包） |
| 黑板 | `BlackboardData.h`、`BlackboardKeyType*.h`、`BehaviorTreeComponent` 中的 `UBlackboardComponent` | 键类型体系、键选择器（`FBlackboardKeySelector`）、观察者通知 |
| 执行器 | `BehaviorTreeComponent.h/.cpp`、`BehaviorTreeManager.h` | tick 驱动、搜索与中断（`FBehaviorTreeSearchData`）、树结束与重启、单帧预算 |
| 编辑器图表示 | `Editor/.../BehaviorTreeEditor`、`BehaviorTreeGraphNode*` | 节点/装饰器在编辑器里的表示方式（与我们的 web 低代码编辑器对照） |

### 1.3 官方文档（已核对）

| 文档 | 已确认要点 |
|---|---|
| [Decorators](https://dev.epicgames.com/documentation/unreal-engine/unreal-engine-behavior-tree-node-reference-decorators) | `Observer Aborts` = **None / Self / Lower Priority / Both**；`Notify Observer` = **On Result Change / On Value Change**；装饰器类型（Blackboard、Compare BBEntries、Composite Decorator(AND/OR/NOT)、Conditional Loop、Cone Check、Cooldown、Force Success、Is At Location、Is BBEntry Of Class、Keep in Cone、Loop、Tag Cooldown、Time Limit）；**Time Limit 在节点每次重新获得焦点时重置计时** |
| [Tasks](https://dev.epicgames.com/documentation/unreal-engine/unreal-engine-behavior-tree-node-reference-tasks) | 任务节点清单（Wait / MoveTo / RunBehavior 等） |
| [EBTNodeResult::Type](https://dev.epicgames.com/documentation/unreal-engine/API/Runtime/AIModule/EBTNodeResult__Type) | 结果枚举定义 |

### 1.4 结论条目

| # | 结论 | 来源 | 落点 | 日期 |
|---|---|---|---|---|
| U1 | 中断语义要按"Self / LowerPriority / Both"三档实现，且中断必须走 `AbortTask` 正常收尾（释放输入、撤销意图） | 文档 + 待 `BTDecorator.h/.cpp` 确认实现细节 | `player-ai-plan.md` §3.3、§3.2 | 2026-09-12 |
| U2 | `TimeLimit` 的计时在"重新获得焦点"时重置，不是首次进入 | Decorators 文档 | §3.1 装饰器表 | 2026-09-12 |
| U3 | "引用另一棵行为树"在 UE 里是任务节点（RunBehavior），与我们的 `Subtree` + 嵌套 `.scbtpak` 同构 | 文档 + 待 `BTTask_RunBehavior.h` 确认 | §3.1、§4.2 | 2026-09-12 |

---

## 2. 低代码编辑器

### 2.1 参考文章

[从零实现一个低代码编辑器：揭秘可视化搭建的核心原理（掘金）](https://juejin.cn/post/7571344319702433832)

| 已确认要点 | 我们的用法 |
|---|---|
| 三大区域：**物料区 / 编辑区 / 属性设置区** | 直接对应我们的物料区（节点 + **动作包**）/ 编辑区（行为树画布）/ 属性区 |
| 编辑器状态就是一棵**组件树**（`id / name / props / children / parentId`），用 zustand 管理 | 与我们的 `tree.json`（嵌套 children + 唯一 id）同构，序列化几乎零转换 |
| **组件配置注册表**（`componentConfig`：默认属性 + React 组件）驱动物料区与属性区 | 我们的"节点类型注册表"（`nodes.schema.json`）—— 前端与运行时共用同一份 |
| 拖拽用 react-dnd；**嵌套容器里 `drop` 会重复触发**，要用 `monitor.didDrop()` 判断是否已被子节点处理 | 我们的"装饰器/服务拖到已挂同类节点的节点上"有同类问题 |
| 递归树工具函数（`getComponentById` 等） | 我们的 `findById / insert / moveTo / removeSubtree / walk` |
| 扩展方向：撤销重做（past/present/future）、数据绑定、条件/循环渲染、事件系统 | 撤销重做是编辑器必备；数据绑定 → 我们的黑板键选择器 |

### 2.2 alibaba/lowcode-engine

仓库：[alibaba/lowcode-engine](https://github.com/alibaba/lowcode-engine)（许可：[MIT](https://raw.githubusercontent.com/alibaba/lowcode-engine/main/LICENSE)，Copyright (c) 2021 Alibaba）

| 事实（已核对） | 对我们的含义 |
|---|---|
| 企业级低代码**内核引擎**，面向扩展设计（"最小内核 + 最强生态"） | 骨架、物料、设置器、插件、渲染器都是外挂件，可按需取用 |
| **只支持 CDN 方式引入**（`npm install` 只为 typings；运行时用 UMD：`engine-core.js` + `react-simulator-renderer.js`） | 必须把 UMD 与依赖**vendored 成本地资源**（离线要求），由我们的 exe 本地 HTTP 服务提供 |
| 引擎生态是 React 技术栈（物料/设置器 = React 组件） | 与 D2 决定（Vite + React）一致 |
| 其自身开发在 Windows 下**要求 WSL** | 我们**只消费构建产物**，不需要 WSL |
| 协议：LowCodeEngine 基础搭建协议 + 物料协议 | 我们的 BT schema 要么贴合协议，要么在边界做映射 |
| 相关生态：lowcode-demo / lowcode-materials / lowcode-engine-ext / lowcode-plugins | 可直接参考其物料与设置器实现 |

**可行性能否"基于它打包一个 exe 低代码平台"**：**能**。做法与 §7 一致 —— 单文件 exe 内嵌静态资源 + 本地 HTTP 服务；
把 `engine-core.js`、模拟器渲染器、React 等 vendored 进去（保留 MIT 许可与版权声明）。此时 exe 会明显变大（数十 MB 级），但离线、零安装的要求满足。

**关键差距**：它是为**页面搭建**设计的，其"渲染器"负责把 schema 渲染成页面组件；我们的域是**行为树** —— 不需要页面渲染，
只需要"编辑树 + 属性面板 + 校验"。因此有两条路线：

| 路线 | 做法 | 优点 | 缺点 |
|---|---|---|---|
| 1. 当 schema 编辑器外壳 | 每类 BT 节点写一个 **material**（React 组件画节点）；装饰器/服务用 props 或专用容器；属性面板用 **setter**；校验调用我们的 C# 校验器 | 白送骨架/拖拽嵌套/属性面板/撤销重做/插件体系 | 要顺着它的协议与物料模型；页面模型与树模型可能有摩擦 |
| 2. 自研（Vite + React + 图库 React Flow / AntV X6） | 节点注册表 + 画布 + 属性面板 + 校验全部自写（借鉴文章与路线 1 的做法） | 贴合 BT 语义、体积小、完全可控 | 三分栏/拖拽/撤销重做要自己实现 |

**建议**：先做 1~2 天技术验证（spike，见方案 §11 D15）：用 UMD 做一个最小 material（`Selector`/`Sequence`/`Task.PlayActionPackage`）+ 一个 setter，
验证"直接编辑我们的 `tree.json` 并回写"。成立走路线 1，否则走路线 2 —— 两者都满足"单文件 exe + 离线"。

### 2.3 结论条目

| # | 结论 | 来源 | 落点 | 日期 |
|---|---|---|---|---|
| L1 | lowcode-engine 为 MIT，可 vendored 进我们的 exe（需保留许可声明） | LICENSE 原文 | §7.4、§15.2 | 2026-09-12 |
| L2 | 它只发布 UMD（CDN 引入），离线方案必须本地化产物，不能依赖 CDN | packages/engine/README.md | §7.4 | 2026-09-12 |
| L3 | 编辑器状态与包格式同构可省掉转换层；节点注册表驱动物料区/属性区 | 掘金文章 | §7.2、§7.3 | 2026-09-12 |
| L4 | **更正**：npm 上的真实包名是 `@alilc/lowcode-engine`（1.3.4）；`@alibaba/lowcode-engine` **404**（旧记录有误） | `npm view @alilc/lowcode-engine version` | §15.2、D15 | 2026-09-12 |
| L5 | 引擎 1.3.4 解包 6.17 MB，产物是 `lib/`（CJS）+ `es/`（ESM），**没有 UMD**；消费它必须上打包器 | `npm view @alilc/lowcode-engine dist.unpackedSize main module` | §7.4、D15 | 2026-09-12 |
| L6 | `@alilc/lowcode-engine-ext` peer 依赖 `react ^16.3.0` + `@alifd/next 1.x` → 离线内嵌要背 React16 + Fusion | `npm view @alilc/lowcode-engine-ext peerDependencies` | D15 | 2026-09-12 |
| L7 | 结论：本期**自研零依赖编辑器**（单文件 4.93 MB + 内嵌页面），`engine-adapter.js` 留作 lowcode-engine 挂载点 | 本仓库 `Mod/PlayerAiEditor`（自检 26/26） | D15 | 2026-09-12 |

---

## 3. 本仓库/本游戏源码的相关结论（索引）

> 这些是"读本项目源码"得到的、影响设计的关键结论，详细出处见方案文档正文。

| # | 结论 | 出处 | 落点 |
|---|---|---|---|
| G1 | 失焦时输入被丢弃的三处判断 | `ComponentInput.cs:91-94`、`:158`、`Game/WidgetInput.cs:628` | 方案 §14.1 |
| G2 | 游戏失焦不暂停模拟（帧循环与实体系统照常推进） | `Window.cs` 帧事件与 Program 帧体 | §14.1 |
| G3 | 真实光标只在 `Window.IsActive` 时才被引擎改写（`CursorVisible = IsMouseVisible`） | `Mouse.cs:48-70`、`Widget.cs:768-770` | §14.2 步骤 2 |
| G4 | 软光标可完全替代真实光标做 UI 输入 | `Game/WidgetInput.cs:168-228`、`:741-795` | §13 |
| G5 | 拖拽启动要求"首帧超阈值时仍命中源控件" → 软光标必须小步移动 | `InventorySlotWidget.cs:319-357` | §13.1 |
| G6 | 交互射线包含身体（+0.35m 膨胀），会静默吞掉放置/交互 | `ComponentMiner.cs:376-440` | README §7、§12 |
| G7 | 创造目录顺序按 `Block.DisplayOrder`，不是方块索引 | `ComponentCreativeInventory.cs:80` | README §7 |
| G8 | 方块值布局：`contents = value & 0x3FF`，`light = (value>>10)&0xF`，`data = value>>>14` | `Terrain.cs:394-417` | README §7 |
| G9 | 游戏内有通用文本输入对话框可复用（录制命名） | `TextBoxDialog.cs:20` | §6.1 |
| G10 | `PgUp/PgDn/Home` 都在引擎按键枚举里（含 Android 映射） | `Key.cs:32`、`Keyboard.cs:283` | §6.1、§14.6 |
| G11 | **`Dispatcher.Dispatch` 在主线程调用会立即执行**，只有后台线程调用才入队到下一帧 `BeforeFrame`；`Mouse/Keyboard.AfterFrame` 在帧末清空 downOnce → **脉冲只能写在帧首** | `Dispatcher.cs:34-68, 79-105`、`Mouse.cs:132-138` | §14.2.1a、CM-1/CM-2/CM-3 |
| G12 | 帧首执行的可复用闭环：`Frame.Update` 发信号 → 后台线程入队 → 下一帧帧首执行（`FrameStartPump`） | 本项目实现 + G11 | CM-1/CM-2/CM-3、PlayerAiMod AI tick |
| G13 | "长按分割源 → 拖动"才是分离物品的正路：长按设置分割源后拖动会变成 `SingleItem` 模式只搬 1 个；长按原地松开反而被同格 Click 取消 | `InventorySlotWidget.cs:319-325, 335-338`、`WidgetInput.cs:755-795` | §6.4、§13.1、CM-1 `ui.split` |
| G14 | 真实焦点可用 `Window.Activated/Deactivated`（public 事件）独立跟踪，因此"强制引擎活跃"（直接写 `Window.m_state`）不会破坏焦点判定；`Window.State` 值名为 `Uncreated/Inactive/Active` | `Window.cs:16, 218, 325-379` | §14.2、CM-2 `FocusPolicy` |
| G15 | 共控合并的正确规则是 `引擎数组 = 真实 \|\| AI 注入`，且**只在窗口真正获得焦点时合并** —— Windows 上 `OpenTK.Input.Keyboard.GetState()` 是全局键盘状态，失焦合并会让"在别的软件打字"变成角色走动 | `Engine/Engine/Input/Keyboard.cs:150-197`（事件受 IsActive 限制）、OpenTK 全局快照 | §14.2.2、CM-2 |
| G16 | `ComponentGui.DisplaySmallMessage` 是 4 参（无默认值）：`(string, Color, bool blinking, bool playNotificationSound)` | `ComponentGui.cs` 调用点 | CM-3 HUD 提示 |
| G17 | 行为树"跑完一轮"会**自动重跑**（UE 行为）——写断言/校验时必须按 `CompletedLoops` 或"恰好跑到完成那一帧"来写，否则会误判 | 本项目 `BtRuntime` + 自检实测 | P0-2 自检用例 |
| G18 | 装饰器的"持久效果"（如 Cooldown 锁定期）**不能被运行态重置清掉**：`ResetState` 是给"失活/树重启"用的，清掉冷却会让冷却形同虚设（自检抓出的真 bug） | 本项目 `BtDecorators` + 自检实测 | P0-1/P0-2 |
