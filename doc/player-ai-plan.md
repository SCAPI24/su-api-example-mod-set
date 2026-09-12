# PlayerAiMod 行为树方案（scbtpak / scatpak / 低代码编辑器）

> 状态：**计划稿**（2026-09-12）。设计权威文档，实现进度以 §9 里程碑与 §10 当前清单为准。
> 关联：`Mod/PlayerAiMod/README.md`（现状与代码结构）、`Mod/doc/cmd-bridge-plan.md`（玩家控制器注入层）。

---

## 0. 结论摘要

1. **决策模型换成行为树**（对齐虚幻引擎）：现有手写状态机（`Fsm/AiStateMachine.cs`）作为"生命周期/模式"保留，
   但"怎么决策"改由行为树表达 —— 组合节点 + 装饰器 + 服务 + 任务，配 UE 的中断语义（Observer Aborts）。
2. **一棵树一个包**：`.scbtpak`（ZIP 改名），包可**嵌套**；进游戏后一次性解析成内存节点图，运行期**零解压**。
3. **热修改（推送式）**：编辑器保存后**主动通知 scmod** 重载，不做反复扫描；scmod 读取时**不持有文件句柄**，
   校验通过才在 tick 边界原子替换（手动 `ai.tree.reload` 作为兜底，低频安全扫描默认关闭）。
4. **动作包 `.scatpak`**：游戏内 PgUp 开始录制 / 再按暂停继续 / PgDn 结束 → 命名 → 落到与树包**同路径**；
   行为树的任务节点可挂**一条或多条**动作包。
5. **低代码编辑器**：本地单文件 exe（内嵌 web 前端，离线可用），三分栏（物料区 / 编辑区 / 属性区），
   物料区除节点外还包含**动作包**；校验逻辑与运行时共用同一份 C# 实现，避免两套规则漂移。
   行为树能被 web 打开、但**运行时不依赖 web**，编辑器可独立分发给别人用；
   内核走 `alibaba/lowcode-engine`（MIT）还是自研由 1~2 天 spike 定案（§15.2）。
6. **内存态独占**：AI 在运行时对行为树的重排/改写**只存在于内存**，绝不写回原始包；
   编辑器可从游戏**导出当前内存树**（`ai.tree.export`）再另存到本地（§4.5）。
7. **鼠标隔离**：AI 的 UI 操作（含拖拽）走引擎内的"虚拟 UI 鼠标会话"，**不动用户的物理鼠标**（§13）。
8. **快速切换**：所有树预编译常驻内存，切换 = 指针交换（tick 边界原子替换，零 IO），
   因为"AI 想得慢，但一旦决定就要立刻切"（§3.6）。
9. **后台也能控制（按焦点两套策略）**：前台完全跟随引擎（游戏自己隐藏/控制鼠标）；
   失焦则进入"**脱离实际鼠标**"模式（引擎以为窗口活跃 + 系统光标不受影响 + 真实鼠标移动/滚轮被切断 + UI 走软光标），
   于是既能后台控制，也能**多开 AI**，且全程不抢焦点、不改游戏源码（§14）。
10. **焦点回来是共控**：真实输入与 AI 注入每帧合并（`真实 || AI`，只在真正获得焦点时合并），
   视角按"最近真实鼠标活动"仲裁，UI 软光标与真实光标双路并存（§14.2.2）。
11. **`Home` 切执行/暂停**：焦点在窗口时按 `Home` 切换行为树执行/暂停（HUD 提示、暂停即释放输入、恢复不重置运行态）；
   热键注册表放在 CmdBridgeMod 供所有 Mod 复用（§14.6）。

---

## 1. 目标与范围

### 1.1 要做的

| # | 需求 | 落地物 |
|---|---|---|
| 1 | 能操作 AI | AI 控制面：启用/停用、切树、暂停/继续、读写黑板、录制开关（§8） |
| 2 | 决策用行为树（参考 UE），降低构建复杂度 | 行为树运行时 + 节点库（§3） |
| 3 | 行为树可动态热修改 | 热重载器：指纹轮询 + 校验 + 原子替换（§5） |
| 4 | 行为树有 web 显示，可做成 exe，离线可用 | `PlayerAiEditor.exe`（§7） |
| 5 | 一棵树打包一个 `.scbtpak`，可嵌套 | 包格式 v1 + 嵌套解析（§4） |
| 6 | 载入后按规则映射，不每次解压 | 解析成内存节点图 + 平坦索引（§4.2） |
| 7 | 物料区含动作包；动作包现场录制（PgUp/PgDn + 命名） | `.scatpak` 格式 + 录制/回放（§6） |
| 8 | 一个动作节点可挂一条或多条动作包 | 任务节点 `PlayActionPackage`（§3.1/§6.4） |

### 1.2 铁律（沿用 CmdBridgeMod）

- **优势可给**：无限读取、超人输入速度（瞬时转视角、每帧按键、精确点击）。
- **特权不给**：直接写生命/背包/方块/位置/时间；跳过界面层级；绕过玩家控制器调用游戏行为。
- 一切行动仍经**玩家控制器**（视角 / 键盘 / 鼠标 / UI 点击）；注入复用 CmdBridgeMod 的门面
  （`CmdBridgeMod.CmdBridgeInput`，见 `PlayerAiMod/README.md` §2）。

### 1.3 本阶段范围边界

- **只接管本端角色**；远端角色（远端执行 + 本端复现）是 P4 议题，不在本计划主线。
- 不引入自定义渲染、不改游戏源码、不替换 Subsystem（遵守仓库规则：主仓只读）。

---

## 2. 总体架构

```
┌────────────────────────── 工具链（离线，独立可执行） ──────────────────────────┐
│  PlayerAiEditor.exe   单文件 exe = 本地 HTTP 服务 + 内嵌 web 前端              │
│   ┌ 物料区 ┐ ┌── 编辑区 ──┐ ┌ 属性区 ┐   打开/编辑/保存 .scbtpak              │
│   │ 节点   │ │  节点图    │ │ 属性   │   校验走运行时同一套 C# 校验器(HTTP)    │
│   │ 动作包 │ │  拖拽/连线 │ │ 黑板   │   可选：连游戏看运行中的活动节点(实时)   │
│   └────────┘ └───────────┘ └────────┘                                        │
└───────────────┬───────────────────────────────────────────────────────────────┘
                │ 读写文件（原子写：临时文件 + 替换）
                ▼
       <实例根>/PlayerAi/BehaviorTrees/*.scbtpak     ← 一棵树 = 一个包（可嵌套）
       <实例根>/PlayerAi/BehaviorTrees/*.scatpak     ← 动作包（与树包同路径）
                │
                │ 只读读取 + 指纹轮询（不锁文件）
                ▼
┌────────────────────────── 游戏内（PlayerAiMod，C#） ──────────────────────────┐
│  PackageWatcher ──变更──▶ PackageLoader ──校验──▶ TreeCompiler ──原子替换──▶    │
│        (指纹/哈希)         (zip 只读)          (schema+语义+环)   (tick 边界)   │
│                                                                               │
│  BehaviorTreeRuntime（每帧首 tick）                                            │
│    Root → Composite(Selector/Sequence/SimpleParallel) → Decorator/Service/Task │
│    黑板(AiBlackboard) ── 传感器(IAiSensor 只读) ── 执行器(IAiActuator)          │
│                                                                               │
│  录制器 Recorder（PgUp/PgDn）──▶ .scatpak ──▶ 回放任务 PlayActionPackage        │
│                                                                               │
│  控制面（§8）：ai.status / ai.reload / ai.tree / ai.pause / ai.record ...      │
└───────────────────────────────────────────────────────────────────────────────┘
```

**关键设计取向**

- 运行时只做"读包 → 编译 → 执行"，不含任何编辑器逻辑；编辑器只做"编辑 → 写包 + 调校验"。
- 校验规则只有一份（C#）；编辑器通过本地 HTTP 调用，避免前后端两套规则漂移。
- 所有跨进程交互都是**文件 + 只读读取**，不使用文件锁、不要求同时运行编辑器与游戏。

---

## 3. 行为树模型（对齐虚幻引擎）

### 3.1 节点类型

| 类别 | 节点 | 说明 |
|---|---|---|
| 根 | `Root` | 唯一入口，持有唯一子节点；子节点 Running 时持续 tick |
| 组合 | `Selector` | 从左到右取第一个非 Failure 的子节点；Running 则记住下标继续 |
| 组合 | `Sequence` | 从左到右依次执行，任一 Failure 立即 Failure |
| 组合 | `SimpleParallel` | 主任务 + 后台子树（P3 完整实现，P0 先只支持主任务） |
| 装饰器 | `Blackboard` | 黑板键判定（Is Set / Is Not Set，可加比较运算符） |
| 装饰器 | `CompareBBEntries` | 两个黑板键比较 |
| 装饰器 | `Cooldown` | 冷却，未过期则该分支不可用 |
| 装饰器 | `TimeLimit` | 限时，超时判失败；**每次重新获得焦点时重置计时**（UE 行为） |
| 装饰器 | `Loop` | 循环 N 次 / 无限（附带超时兜底，避免死循环） |
| 装饰器 | `ForceSuccess` / `ForceFailure` / `Inverter` | 结果改写 / 取反（对应 UE 的 Inverse Condition） |
| 装饰器 | `Condition` | 自定义条件（表达式：黑板键 + 传感器读数 + 比较） |
| 服务 | `Service` | 挂在组合节点上，按 interval 周期执行（如"更新最近玩家""更新血量"） |
| 任务 | `Wait` / `SetBlackboard` / `Log` | 基础任务 |
| 任务 | `LookAt` / `MoveTo` / `FaceEntity` | 运动类任务（走执行器，不直接写状态） |
| 任务 | `Subtree` | **嵌套**：引用其它 `.scbtpak` 的某棵子树 |
| 任务 | `PlayActionPackage` | **挂载一条或多条 `.scatpak`**（§6.4） |
| 任务 | `Emit` | 触发控制面事件（如"任务完成"通知外部） |

> 装饰器/服务在 JSON 里是"挂在节点上的数组"，与 UE 一致（装饰器可挂 Composite 或 Task，服务只挂 Composite）。

### 3.2 执行语义

- 结果枚举对齐 UE：`Succeeded` / `Failed` / `Aborted` / `InProgress(Running)`。
- 每帧（帧首）从 `Root` 深度优先 tick 一次；Running 的节点每帧被再次 tick。
- 组合节点记住"当前活跃子节点下标"，不每帧从头重选（避免抖动）。
- 任务节点支持两种模式：`Instant`（一帧完成）与 `Latent`（跨帧，需 `TickTask`/`AbortTask`）。
- **帧预算**：单帧节点访问数有上限（默认 256），超限则本帧剩余部分推迟到下一帧，避免卡顿。
- **异常隔离**：任何节点抛异常 → 该子树 Abort + 记录 + 视作 Failed；连续失败超阈值则整树停用并报警。
- **确定性**：不依赖随机数（需要随机时使用可种子化的 `SeededRandom`，便于回放/复现）。

### 3.3 中断（Observer Aborts）

采用 UE 的 `Observer Aborts`：

| 值 | 含义 |
|---|---|
| `None` | 不中断任何东西 |
| `Self` | 中断自己及自己子树下的执行 |
| `LowerPriority` | 中断本节点右侧的节点（更"低优先级"的分支） |
| `Both` | 两者都中断 |

配合 UE 的 `Notify Observer`：`OnResultChange`（结果变化才重新评估）/ `OnValueChange`（被观察黑板键变化才评估）。
实现要点：装饰器的条件变化时，触发一次"从根重新搜索"（UE 的 search），被中断分支走 `AbortTask` 正常收尾
（**必须释放输入/撤销意图**，否则会出现"以为自己还在走路"）。

### 3.4 黑板

- 复用现有 `AiBlackboard`（类型化键、Revision 计数）。
- 包内声明黑板键（名字 + 类型 + 默认值 + 是否只读），加载时校验任务/装饰器引用的键都存在。
- 键类型：`bool` / `int` / `float` / `string` / `vector3` / `actor`（玩家或实体引用）/ `cell`（方块格）/ `enum`。
- 外部可读写（控制面 `ai.blackboard.get/set`），便于"操作 AI"与调试。

### 3.5 与现有状态机的关系

| 层 | 保留 | 用途 |
|---|---|---|
| `Fsm/AiStateMachine.cs`（现有） | **保留，已收口为模式层** | 只表达**模式/生命周期**（未接管 / 待机 / 运行行为树 / 录制中 / 故障），不表达具体决策；转移表在 `Core/AiModeGraph.cs`，模式由宿主/树的客观状态推导（P0-8）；FSM 只依赖 `IAiTreeHost` 接口，因此模式层能脱离游戏自检 |
| 行为树（新） | 新增 | 全部决策：交互、移动、采集、建造、战斗…… |
| `Actors/`（传感器/执行器） | 保留 | 行为树任务通过它们读世界、动玩家 |

现有「看向 basil → 走近」的示例状态（`DemoLookState`/`DemoApproachState`）**已改写成一棵示例行为树**
（`demo.greet.scbtpak`，P0-6），P0-8 把两个示例状态类与 `PlayerAiConfig` 里的示例决策参数**全部删除**：
同一行为用 BT 表达、行为等价，而代码里不再留任何"示例决策"。
**行为改包即可**：目标名 = 服务 `UpdateNearestPlayer.nameFilter`，距离/超时 = `Task.MoveTo.acceptableRadius/timeout`。

### 3.6 快速切换行为树（AI 决策慢，切换必须快）

需求原话：AI 的思维可能很慢，但一旦决定切换，就要能尽快切换。据此：

- **多树常驻**：`TreeLibrary` 把目录下所有包**预编译**成节点图驻留内存；切换 = 换一个指针。
- **零 IO 切换**：不读文件、不解析 JSON、不在切换路径上分配大对象；只在 **tick 边界**原子替换活动树。
- **代价**：`O(1)` 指针交换 + 被中断分支的 `AbortTask` 收尾（必须释放输入、撤销意图）。
- **带入口切换**：`ai.tree.switch <包> [入口节点id]`，可从某棵子树开始跑，方便 AI 把"半个计划"排进去。
- **切换预算**：同一帧最多一次切换；一帧内多次请求时后者覆盖前者（以最新意图为准）。
- **与热更新解耦**：包变更只重编译**受影响的树**；未被改动的运行中树完全不动。
- **预排**：AI 可先把候选树编译好放进"就绪队列"（`ai.tree.prepare`），切换时不再付编译成本。

---

## 4. 包格式

> 两类包都是 **ZIP 改名**；内部一律 `/` 分隔、UTF-8 JSON、无 BOM；scmod 只读读取，不写、不锁。

### 4.1 `.scbtpak`（行为树包）

```
demo.greet.scbtpak
├── manifest.json          # 包元数据 + 黑板定义 + 嵌套引用 + 入口
├── tree.json              # 节点图（嵌套 children；每个节点有唯一 id）
├── meta/
│   ├── description.md     # 可选：作者说明
│   └── editor.layout.json # 可选：编辑器画布坐标等（运行时忽略）
└── actions/               # 可选：随包内嵌的动作包（默认仍走同路径引用）
```

`manifest.json`：

```json
{
  "format": "scbt", "version": 1,
  "id": "demo.greet", "name": "示例·打招呼",
  "entry": "root",
  "blackboard": [
    { "name": "target", "type": "actor" },
    { "name": "targetDistance", "type": "float", "readonly": true }
  ],
  "references": [
    { "id": "common", "path": "common.scbtpak" }
  ]
}
```

`tree.json`（节选）：

```json
{
  "id": "root", "type": "Root",
  "children": [{
    "id": "sel", "type": "Selector",
    "services": [{ "id": "svc1", "type": "UpdateNearestPlayer", "interval": 0.25,
                   "properties": { "targetKey": "target", "nameFilter": "basil" } }],
    "children": [
      {
        "id": "seq", "type": "Sequence",
        "decorators": [{ "id": "d1", "type": "Blackboard",
                         "properties": { "key": "target", "query": "IsSet", "inverse": false,
                                         "notifyObserver": "OnResultChange",
                                         "observerAborts": "LowerPriority" } }],
        "children": [
          { "id": "t1", "type": "Task.LookAt",  "properties": { "targetKey": "target", "eyeHeight": 1.35 } },
          { "id": "t2", "type": "Task.MoveTo",  "properties": { "targetKey": "target", "acceptableRadius": 3.0, "timeout": 25.0 } },
          { "id": "t3", "type": "Task.PlayActionPackage",
            "properties": { "packages": ["greet_wave.scatpak"], "mode": "Sequence", "abortOnFail": true } }
        ]
      },
      { "id": "t4", "type": "Task.Wait", "properties": { "seconds": 1.0 } }
    ]
  }]
}
```

**约束**：节点 `id` 包内唯一；组合节点必须有 `children`；任务不能有 `children`；装饰器不能有 `children`；
`Subtree` 节点的 `properties.package` 必须命中 `references`。

### 4.2 嵌套与内存映射

- 嵌套两种引用方式：`Subtree` 节点引用 `"<referenceId>#<nodeId>"`，或引用整包入口（`"<referenceId>"`）。
- 加载流程（**只做一次**）：
  1. 打开根包 zip → 读 `manifest.json` + `tree.json` → 关闭（不保留句柄）。
  2. 递归解析 `references`（相对路径解析、限定在允许目录内）；**环检测** + 最大深度限制（默认 8）。
  3. 编译为**平坦节点数组**（`Node[]`）：每个节点预先解析好子节点下标、装饰器下标、服务下标、黑板键下标、
     动作包绝对路径 → 运行期只走内存数组，**不解压、不查表**。
  4. 校验通过后替换运行时树（§5）。
- 运行期零 IO：任务需要读动作包时，走"动作包缓存"（同样一次读入内存，见 §6.4）。

### 4.3 `.scatpak`（动作包）

```
greet_wave.scatpak
├── manifest.json
├── tracks/
│   ├── input.bin        # 逐帧输入流（紧凑二进制）
│   └── events.json      # 稀疏事件（UI 点击、快捷栏切换、挖掘/放置等语义事件）
├── keyframes.json       # 关键帧（每 0.5s 的位置/朝向），用于回放校验与漂移纠正
└── meta/
    └── ui-snapshots.json# 可选：录制时 UI 元素路径快照，供回放时重新解析坐标
```

`manifest.json`：

```json
{
  "format": "scat", "version": 1,
  "id": "greet_wave", "name": "挥手打招呼",
  "recordedUtc": "2026-09-12T10:00:00Z",
  "duration": 3.42, "sampleRate": 60,
  "startState": { "position": [0,0,0], "yaw": 0.0, "pitch": 0.0, "worldName": "Rebritish" },
  "tracks": { "input": "tracks/input.bin", "events": "tracks/events.json" },
  "checksum": "sha256:..."
}
```

`input.bin` 每帧定长记录（示例）：`dt(u16,ms) | flags(u16) | move(i8x3) | lookDelta(i16x2) | mouseButtons(u8) | slot(i8) | wheel(i8)`。
**坐标/朝向一律相对录制起点**，使动作包可跨地图/跨设备复用（§6.3）。

### 4.4 目录约定（已定：包目录跟随实例走，另有一份随 Mod 分发）

两个**包来源** + 一个**运行时内存态**：

| 优先级 | 位置 | 用途 | 可写 |
|---|---|---|---|
| 1（高） | `<实例根>/PlayerAi/BehaviorTrees/` | 用户 / AI / 编辑器日常改的包，**跟随游戏实例走** | ✅ 编辑器写 |
| 1（高） | `<实例根>/PlayerAi/BehaviorTrees/*.scatpak` | 动作包，与树包**同路径** | ✅ 录制 / 编辑器写 |
| 2（低） | `Mods/<scmod 安装目录>/PlayerAiMod/BehaviorTrees/` | 出厂包 / 示例包 / 发行作品，**随 Mod 分发** | ❌ 只读 |
| — | 运行时内存树 | AI 重排/改写后的活树 | ❌ 不落盘（§4.5） |

```
<实例根>/PlayerAi/                    # 与 CmdBridge.json 同级，便于用户找到
├── PlayerAi.json                     # 配置（可选）
├── BehaviorTrees/                    # 实例级：可改
│   ├── demo.greet.scbtpak
│   ├── common.scbtpak
│   ├── greet_wave.scatpak            # 动作包与树包同路径
│   └── wave_slow.scatpak
└── Logs/                             # 校验失败、重载记录（滚动，限量）

Mods/PlayerAiMod/PlayerAi/BehaviorTrees/   # Mod 级：只读，随 Mod 分发
```

- **同名优先**：实例目录的包覆盖 Mod 目录的同名包（用户改动优先，也便于"改出厂包而不动原文件"）。
- 两个目录并存是为了"**既能改、又能分发**"：Mod 目录承载发行包，实例目录承载本地改动。
- 允许的根目录白名单 = 上述两处；`references.path` 必须是相对路径且解析后仍落在白名单内（防路径穿越）。
- Android 下实例目录为游戏实例对应的可写目录、Mod 目录为 mod 安装目录，规则一致。

### 4.5 内存态：AI 可以改，但不落盘

- 加载 = 从包**读**到内存并编译成节点图；之后一切由内存树驱动。
- **AI 在运行时的重排/改写（增删节点、换分支、改参数、挂动作包）只作用于内存副本**：
  - 不写回任何原始 `.scbtpak`（原包在整局游戏里字节不变）；
  - 不产生隐式落盘（没有自动保存、没有备份文件）。
- 需要持久化时**只由人**通过编辑器完成：编辑器向游戏索取当前内存树（`ai.tree.export`，§8），
  在本地另存为**新包**（新文件名/新目录），由用户决定是否替换原包。
- 理由：AI 的思维是探索性的、会反复试错；允许它改内存能快速迭代，而写回原包会悄悄污染"原稿/发行包"，
  也让多设备、多人之间无法对账。
- 内存树记录 `sourcePackage` + `sourceHash` + `dirty`；`ai.status` 显示"来自哪个包、原哈希、是否被改过"。

---

### 4.6 实现要点（P0-3 / P0-4 落地记录）

**文件与职责**

| 文件 | 职责 |
|---|---|
| `Package/PackageValue.cs` | 自持 JSON 值模型（Null/Bool/Number/String/Array/Object）。**为什么不用 `JsonElement`**：它绑定 `JsonDocument` 的生命周期，而加载器读完 zip 必须立刻释放句柄（§5「不锁文件」）；这份值模型让"解析完就关文件"成立，也让内存态改写（§4.5）与导出有统一表示 |
| `Package/PackageIssue.cs` | 问题/严重级/报告 + **稳定错误码表 `PackageCodes`**（控制面与编辑器按码判断，不靠文案） |
| `Package/PackageJson.cs` | DOM 解析（严格：UTF-8、无 BOM、无注释/尾随逗号、体积 4 MB、深度 128）+ `PackageReader`（带上报的字段读取、枚举归一化、**未识别字段提示**） |
| `Package/ScbtManifest.cs` | manifest 读写 + 自校验（format/version/id/entry/blackboard/references） |
| `Package/ScbtTree.cs` | tree.json 文档模型（节点/装饰器/服务，保留原 JSON 以便导出不丢字段）+ 入口解析 |
| `Package/PackageValidator.cs` | 图校验（id 唯一、形状、必填/类型/枚举、未识别属性、Subtree 引用命中、节点数/深度/扇出上限） |
| `Package/PackageRoots.cs` | 双目录白名单（实例可写 / Mod 只读）、路径越界判定、名字解析、列表、按需建目录 |
| `Package/PackageLoader.cs` | 定位→读字节→哈希→解 zip→解析→校验→递归引用；`IPackageSource`（磁盘/内存）、`PackageLoadOptions`、`LoadedPackage`/`ScbtPackageSet` |
| `Package/TreeCompiler.cs` | 文档 → `BtNode` 对象图：造节点、**属性名→字段的唯一映射点**、挂装饰器/服务、解析嵌套引用 |
| `Package/PackageWriter.cs` | 打包成 `.scbtpak` 字节 + **原子写**（`.tmp` → 替换）。只用于出厂模板与导出，**AI 的内存改写永不走这里** |
| `Package/PackageTemplates.cs` | 出厂示例包（`demo.greet.scbtpak` + `common.scbtpak`）与安装（缺什么补什么，绝不覆盖） |
| `Package/PackageReloader.cs` | 推送式重载（P0-5）：入队（任意线程）/ 应用（tick 边界）/ 预检 / 可选扫描 |
| `Core/AiModeSelfTest.cs` | 33 项模式层自检（模式推导、故障回退、外部塞树、录制模式、模式映射表） |
| `Bt/TreeLibrary.cs` | 树库（P0-11）：预编译常驻 + 毫秒级切换 + 过期重编译 + `Adopt` 让热重载认新活动树 |
| `Package/TreeMutation.cs` | 内存态改写（P0-12）：改参数/插节点/删节点/搬移，只改内存 |
| `Package/TreeWriter.cs` | 反序列化（P0-12/13）：节点→属性 JSON + 导出成新包 |
| `Package/TreeEditSelfTest.cs` | 43 项改写/导出自检（含「原包字节不变」与「reload 丢弃改动」） |
| `Core/AiEventLog.cs` | 观测日志（P0-10）：滚动 + 限量 + 写失败不影响游戏 + 内存最近记录 |
| `Record/AiRecordingSession.cs` | 录制会话（P0-9）：状态机 + 名字消毒 + 元数据落盘；`Record/AiRecordingSelfTest.cs` 31 项 |
| `Package/PackageSelfTest.cs` | 100 项自检（合法包、属性/装饰器/服务落地、嵌套与菱形引用、每处引用独立实例、运行时冒烟、15 条错误路径、环/穿越/深度、白名单、往返导出、装载闸门、**出厂模板落盘装载 + 不锁文件 + 六种热重载路径**） |

**与计划的差异（有意为之）**

- §4.2 说"编译为平坦节点数组"。实现改为**对象图**：C# 里对象引用跳转与数组下标等价，少一层间接层反而更快，
  也避免"节点表与下标不同步"这类错误；"零解压、零查表"的目标不变（`CompiledTree` 里只有对象引用）。
- 校验职责分两处且不重叠：**类型/形状/必填/类型不符/枚举**在校验器（读注册表）；
  **取值语义**（radius 必须 ≥ 0、`limitSeconds > 0`、`numLoops ≥ 1`）在编译器的属性读取点
  —— 只有读取点知道每个属性的含义。
- `Task.Subtree` 的 `properties.package` 支持 `"<引用id>"` 与 `"<引用id>#<节点id>"` 两种写法（§4.2）；
  引用的归属是**当前正在编译的那个包**（嵌套包的引用是它自己的 `references`，不是根包的）。
- 未知字段（`property.unknown`）默认是 **warning 而非 error**：手写包最常见的错误就是属性名拼错，
  提示即可，不必拒绝装载；但**任何 error 一律拒绝装载**（`LoadAndCompile` 在装载有错时直接返回失败，不产出树）。

**自检**

七套自检合计 **378/378 PASS**（内核 63 + 包/热重载/树库 119 + 命令层 65 + 模式层 33 + 录制 31 + 改写/导出 43 + 观测日志 24）（在本机不依赖游戏运行时的临时工程里跑，
`%TEMP%\pa_selftest`：编译 `Bt/**` + `Package/**` + 黑板 + 传感器/执行器接口，只引用 `Engine.dll`/`EntitySystem.dll`）。
控制面 `bt.selftest`（`sccmd aiselftest`）一次跑五套（P0-7 已接）。

---

## 5. 热修改（推送式重载）

### 5.1 机制：由编辑器通知，不反复扫描

- **主路径（推送）**：编辑器保存包后，通过复用的 CmdBridgeMod 控制通道通知游戏：
  `ai.tree.notify {"path": "...", "hash": "sha256:..."}`。
  scmod 只读**这一个文件**、重算哈希：与通知一致 → 校验 → tick 边界原子替换；不一致 → 忽略（对方还在写）。
  —— 没有"每帧/每秒扫目录"的开销。
- **兜底**（按需，默认不常开）：
  1. `ai.tree.reload`：手动/脚本强制重载（带哈希校验）；
  2. `ai.tree.switch` 时顺带比一次来源文件哈希，发现"文件被外部改过"就重编译；
  3. 可选低频安全扫描（默认关闭，`packageWatchSeconds=0`；需要兜住"直接手改文件"时设 5~10 秒）。
- **不锁文件（铁律）**：读取用"打开→读→立即关闭"，不保留任何 FileStream/内存映射句柄；
  读失败或读到半截包 → 保留旧树 + 记录 + 等下一次通知。
- 编辑器一律**原子写**：写临时文件 → 替换；scmod 仍按"可能读到半截"设计。

### 5.2 校验（校验器只有一份，C#）

| 层级 | 检查 |
|---|---|
| 结构 | JSON 合法、必需字段、节点类型已注册、id 唯一、children 合法性 |
| 语义 | 黑板键存在且类型匹配、装饰器参数范围、动作包文件存在、`references` 可解析 |
| 图 | 无环、深度/节点数上限、`Subtree` 引用命中、不可达节点告警 |
| 运行期兼容 | 包 `version` 支持；不支持的节点类型 → 拒绝整包（不半加载） |

### 5.3 原子替换与运行态迁移

- 替换只发生在 **tick 边界**（帧首 tick 之前/之后，绝不在节点执行中途）；重载与 `ai.tree.switch` 共用同一套替换路径。
- 迁移策略（按优先级）：
  1. **按节点 id 尽量保留**：新旧树中 id 相同的 Running 任务保持运行（适合"只改参数"）；
  2. 无法保留的 Running 任务走 `AbortTask` 正常收尾（释放输入）；
  3. 新增/删除分支从根重新搜索。
- 迁移策略写进日志与控制面（`ai.status` 可见"上次重载：保留 N 个任务 / 中断 M 个"）。

### 5.4 可观测

- 每次重载记录：时间、包路径、哈希、结果（成功/失败原因）、迁移统计。
- 失败时游戏内小提示（`DisplaySmallMessage`）+ 日志 + `PlayerAi/Logs/`；**旧树继续工作**。

---

### 5.5 实现要点（P0-5 落地记录）

| 关注点 | 做法 |
|---|---|
| 线程模型 | 控制面在网络线程收包 → `Notify(path, hash)` 只入队；`ApplyPending(runtime)` 只在**游戏线程的 tick 边界**调用（绝不在节点执行中途换树）。一帧最多处理 4 个请求，避免编辑器连发拖长一帧 |
| 通知校验 | 先把**被通知的那个文件**读出来重算哈希：与通知不符 → **忽略**（对方还在写），不算失败也不动旧树；内容与已加载版本一致 → 忽略 |
| 相关性 | 通知只对"活动树自己或它的某个引用包"生效；其它包一律拒绝（不能把无关的包塞进当前运行）。改**被引用**的包（如 `common.scbtpak`）会重新装载整棵活动树（引用闭包一起刷新） |
| 失败处理 | 读失败 / 校验不过 / 编译不过 → **保留旧树** + 记录逐条原因（`TreeReloadResult.Issues`），游戏内小提示与日志同步；绝不半加载 |
| 运行态迁移 | `BtRuntime.ReplaceRoot(newRoot, migrate: true, ...)`：按「**来源包 id / 节点 id**」匹配（id 只在单包内唯一，嵌套后必须带包名）；只保留**父链也一路匹配**的节点，避免"父节点没跑、子节点却 active"的幽灵任务（那种任务会永远按着键）；旧树里没被保留的活动分支**先正常中断收尾**（`OnExit` 释放输入）；统计写进日志与 `ai.status` |
| 已知取舍 | 装饰器/服务内部的计时（冷却锁定、循环计数、服务下次触发时间）**不迁移**，重载后从头算 —— 要迁移得给它们也配一套状态结构，留到 P3 |
| 不锁文件 | 读取走"打开→读→立即关闭"（`File.ReadAllBytes`），不保留任何句柄；自检里在装载后用 `FileShare.None` **独占打开**同一个包文件来验证这条铁律 |
| 兜底通道 | `ai.tree.reload`（手动/脚本，带哈希校验）、`ai.tree.switch` 时比一次来源哈希、可选低频扫描（`PlayerAiConfig.PackageWatchSeconds`，默认 0 = 关闭） |

---

## 6. 动作包：录制与回放

### 6.1 录制入口（用户指定）

| 按键 | 行为 |
|---|---|
| `PgUp`（`Key.PageUp`） | 未录制 → **开始录制**；正在录制 → 在"暂停录制 / 继续录制"之间切换开关 |
| `PgDn`（`Key.PageDown`） | **结束录制** → 弹出输入框命名 → 写入与树包同路径的 `<名字>.scatpak` |

- 开始录制时：**行为树动作暂停**（暂停行为树 tick + 释放 AI 持有的输入；人类操作照常被记录）。
- 命名对话框复用游戏现成组件 `TextBoxDialog(title, text, maximumLength, Action<string> handler)`
  （`Survivalcraft/Game/TextBoxDialog.cs`，带 `AutoHide`、自动聚焦、回车确认）。
- 名字冲突 → 自动加序号或提示覆盖（待确认项 D3）；非法字符过滤（文件系统安全字符集）。
- **Android/无 PgUp 设备**：需要替代入口（控制面命令 `ai.record.start/pause/stop` 或 HUD 按钮），P1 一并做。
- 录制状态在 HUD 上有明确提示（红色 REC + 时长），避免"以为没在录"。

### 6.2 录制内容

- 输入流（逐帧）：移动向量、视角增量、跳跃/潜行/飞行切换、鼠标按键、滚轮/快捷栏切换、挖掘/放置/交互的按下与松开。
- 语义事件（稀疏）：UI 点击（元素路径 + 归一化坐标 + 当时 UI 快照）、容器搬运、方块放置/挖掘目标格。
- 关键帧（0.5 s）：位置、朝向、手持物品、生命/饥饿等（用于回放校验与失败判定）。
- 录制采样点选在**帧末**（`Frame.Update`）：此时 `ComponentInput.PlayerInput` 已是"人在这一帧的意图"，
  读它比读原始设备状态更干净；同时读坐标/朝向做关键帧。

### 6.2.1 实现要点（P1 落地记录）

| 关注点 | 做法 |
|---|---|
| 录哪一层 | **录制原始输入层**（键盘 held/downOnce、鼠标键位图、滚轮、视角增量），不录推导出来的 `PlayerInput`。理由：`PlayerInput` 是游戏从原始输入推导的（`ComponentInput.cs:160-250`），回放时你写它会被同一帧的推导覆盖，写不进去；而走原始输入通道就能让游戏推导出同样的意图，且复用 CM-1/CM-2 已验证的注入路径 |
| 采样点 | **帧末**（插件 `Frame.Update`）：此时 `PlayerInput` 已是这一帧的完整意图（计划 §6.2 指定的位置） |
| 谁读字段 | 原始字段读取只在 CmdBridgeMod 里做一次（`InputSnapshot` + `InputWhitelist`），PlayerAiMod 只拿只读快照 `CmdBridgeInputFrame` —— 白名单纪律与审计脚本才守得住 |
| 轨道格式 | `SCATIN01` 头 + 版本 + **键名表** + 定长帧；帧里存键索引而不是枚举值 → 换版本/换设备不会认错键，人工看十六进制也能对上名字；单帧约 30 字节（2 s ≈ 2 KB） |
| 回放节奏 | 每帧带自己的 `dt`；回放时一个录制帧的输入**保持它那段时间**，于是"录 60 FPS、放 144 FPS"不会快放（帧率解耦） |
| 漂移 | 越过关键帧时用**绝对世界坐标**比对位置/朝向，超阈值（默认 3 m / 45°，`PlayerAiConfig.ReplayDriftTolerance*`）→ **Failed**，交回行为树；执行器拒绝的键会记进 `MissingKeys`，绝不静默丢输入 |
| 出厂示例 | `sample_walk.scatpak`（走→边走边转→走+跳→侧移→停，2.000 s / 120 帧，数据完全确定；**只带起点一条关键帧**，见 §6.2.2）+ `test.action.scbtpak`（`Sequence[Log, PlayActionPackage(sample_walk), Wait 0.5]`）随包分发 |

### 6.2.2 本轮实测发现（都已修）

> **后续（2026-09-12）**：这一段描述的"实例目录 → Mod 分发目录"两级查找链已经被取消 ——
> 用户要求只保留 `<实例根>/PlayerAi/BehaviorTrees` 这一个包目录（见 README §9.5.20）。
> 下面保留的是当时发现与修复的过程。

**① 动作包查找链漏了 Mod 只读目录**（`ai.action.list/validate/play` 只认实例目录）。
症状：出厂示例 `sample_walk.scatpak` 装在 `Mods/PlayerAiMod/PlayerAi/BehaviorTrees/`，
`ai.action.play sample_walk` 一定报 `file_missing` —— "开箱可跑"是假的。
树包早就有"实例目录优先、分发目录兜底"的两级解析（`PackageLoadOptions.Roots`），动作包却漏了。

修法（与树包同源）：
- `ScatLibrary.ResolveIn(directories, name, out folder)` —— 一串目录依次找，前面的优先；
- `PlayerAiRuntime.ResolveActionDirectories()` —— **实例目录 → Mod 分发目录**；`PlayAction` 用它解析；
- 命令层 `IAiActionPlayer.ActionSearchDirectories`（新成员）→ `ai.action.list` 两个目录都列，
  每条带 `source=instance|mod`、`writable`，同名被遮住的标 `shadowed`（不进 replayable 统计）；
- 录制仍然只写实例目录（`ResolveActionDirectory()` 语义不变：那是"可写的主目录"）。

**② 出厂示例的关键帧是"虚构坐标"，真实世界里第一帧就会判漂移失败**。
`CheckDrift` 比的是**绝对世界坐标**（容差 3 m），而样例原先每隔 0.5 s 写一条 `(0,64,0)`
—— 用户在任意世界、任意位置播放它，立刻就是"漂移 200 米 → Failed"。
修法：样例**只保留起点这一条关键帧**（`IsStart`，`CheckDrift` 会跳过它），
让样例不含任何位置断言 → 在哪都能放；真实录制照旧每 0.5 s 一条（"在哪个世界录的就在哪放"）。
自检用 `the shipped sample asserts no mid-run positions (playable anywhere)` 钉住这条不变量。

> **待定（P1 遗留设计问题）**：关键帧到底该按**绝对坐标**比，还是按**相对起点的位移**比？
> §6.3 当初写的是"相对化 + 可跨地图复用"，代码实现的是绝对坐标。绝对坐标更严（能抓到"被墙挡住走不动"），
> 但要求同一世界同一地点回放；相对位移更通用（录一次到处用），但会放过"地形变了"这类问题。
> 现阶段按**绝对坐标 + 漂移即失败**执行（D6：不做瞬移纠偏），是否引入相对模式留待实测后再定。

### 6.3 回放

- 任务节点 `PlayActionPackage` 按记录节奏把输入喂给**同一个执行器**（CmdBridgeMod 门面），
  与真人操作走完全相同的路径（不写状态）。
- **坐标**：关键帧存的是**绝对世界坐标**（与实现一致，见 §6.2.2 的待定项）；
  "相对起点复用"这条理想还没做，现阶段语义是"在哪个世界录的，就在哪个世界那个位置放"。
- **漂移判定**：每越过一条关键帧比对位置/朝向，超阈值（默认位移 **3 m** / 朝向 **45°**，
  `PlayerAiConfig`）→ 任务判 Failed（由行为树决定重试/换分支），不做"瞬移修正"（那属于特权）。
- 回放失败原因（被挡住、物品不在手、UI 不同）写入事件与日志，便于在编辑器里复盘。

### 6.4 挂载一条或多条动作包

```json
{ "type": "Task.PlayActionPackage",
  "properties": {
    "packages": ["wave.scatpak", "greet_voice.scatpak"],
    "mode": "Sequence",            // Sequence | Parallel | RandomOne | RaceFirstSuccess
    "repeat": 1,
    "interruptible": true,
    "abortOnFail": true
  } }
```

- `Sequence`：依次播放（多动作串联成一个"动作包组"）。
- `Parallel`：同时播放（后续 P3；先支持"主包 + 叠加包"如视角抖动）。
- `RandomOne` / `RaceFirstSuccess`：从多条里随机取一 / 谁先成功用谁。
- 物料区（编辑器）中，动作包与节点并列展示，可直接拖到编辑区成为 `PlayActionPackage` 节点；
  拖到已有 `PlayActionPackage` 节点上则**追加一条**（对应"任意的一条，或多条"）。

---

## 7. Web 低代码编辑器（`PlayerAiEditor.exe`）

### 7.1 形态与离线要求

- **单文件自包含 exe**（.NET 8 `PublishSingleFile` + `SelfContained`），双击即用、无需安装、**无需网络**。
- 内置本地 HTTP 服务（ASP.NET Core Minimal API + 静态资源内嵌），随机端口或固定端口，仅监听 `127.0.0.1`；
  启动后自动打开系统默认浏览器（也支持 `--no-browser`）。
- 可选 `--game <实例根>` 直连游戏目录读写包；也可独立打开任意路径的 `.scbtpak`。
- 备选形态（待确认项 D2）：内置 WebView2 的桌面窗口（更"像应用"，但依赖 WebView2 运行时，离线新设备风险高）。

### 7.2 三大区域（参考掘金文章的三分栏 + 组件树 + config 注册表思路）

| 区域 | 内容 |
|---|---|
| **物料区** | 分类折叠：组合节点 / 装饰器 / 服务 / 任务 / **动作包**（扫描目录动态生成，带缩略信息：时长、帧数）；支持搜索；拖拽到编辑区 |
| **编辑区** | 节点图：树形自动布局（默认）+ 自由拖拽；拖入物料即新建节点；连线/插入/替换/删除；嵌套包以"折叠子树"呈现，可双击进入；支持多选、撤销重做 |
| **属性区** | 选中节点的属性表单（schema 驱动：类型/枚举/范围/黑板键下拉）；装饰器与服务以卡片形式挂在节点上；动作包列表可增删排序 |

### 7.3 关键实现要点（借鉴参考文章）

- 编辑器状态 = 一棵**组件树**（`id / type / props / children / parentId`），与包格式同构 → 序列化几乎零转换。
- **节点类型注册表**（等价文章的 `componentConfig`）：每类节点的默认属性、图标、分类、可挂载位置、校验规则；
  前端的物料区与属性区都由注册表驱动，运行时的节点工厂也读同一份 JSON（**单一事实来源**：`nodes.schema.json`）。
- 拖拽用成熟的 DnD 库；注意文章提到的 **`drop` 重复触发**问题（嵌套容器里父容器会重复接收）——
  BT 里对应"把装饰器/服务拖到已挂了同类节点的节点上"，同样要用"是否已被子节点处理过"的判断。
- 树操作工具函数：`findById` / `insert` / `moveTo` / `removeSubtree` / `walk`（递归，配单测）。
- 校验：前端**不自己实现规则**，把整棵树 POST 给 exe 的 `/api/validate` → 复用运行时同一份 C# 校验器 → 返回问题列表
  并在编辑区高亮（点问题跳转到节点）。
- 保存：`/api/save` 写包（原子写）；`/api/open` 读包；`/api/validate` 校验；`/api/scat` 列出同路径动作包。
- 实时监视（P3）：exe 连游戏的本地端点，拉取"当前活动节点路径 + 黑板值"，在图上高亮活动分支；
  支持"暂停/单步/强制切分支"（通过控制面 §8）。

### 7.4 构建与产物

> **已实现（P2 MVP，本轮）**：`Mod/PlayerAiEditor/` —— 单文件 exe（4.93 MB，含全部内嵌页面），
> 零运行期依赖；`--selftest` 无头自检 **41/41 PASS**（Debug exe 与单文件发布 exe 各跑一遍）。
>
> **动作包当物料（本轮补全）**：物料区多出一组「动作包（录好的输入轨道）」——
> 它**不是节点类型**，而是数据材料：点一下 = 在选中节点下挂一个 `Task.PlayActionPackage`，
> 或把该包加入已选中播放节点的 `packages` 列表；属性区里 `packages` 也从"逗号分隔文本框"
> 换成**勾选列表**（带"可回放 / 仅结构"、时长帧数、是否为只读分发目录、是否被同名包遮住）。
> 顶栏另有一组「动作包」：校验 / 试跑（直接让游戏回放）/ 停止 / 自造。

```
PlayerAiEditor/
├── Program.cs                     入口（--root / --port / --no-browser / --selftest）
├── Server/HttpServer.cs           极简 HTTP/1.1（TcpListener，**不需要 URL ACL/管理员**）
├── Server/EditorRouter.cs         路由：静态资源 + JSON API（保存与校验共用同一套参数解析）
├── Server/EditorApi.cs            API 实现：**调用游戏内同一份**校验器/编译器/加载器
├── Server/GameBridgeClient.cs     与运行中的游戏通信（CmdBridge 通道：notify/status/validate/action）
├── Server/JsonWriter.cs           手写 JSON 输出（不反射，AOT 友好）
├── Server/EditorSelfTest.cs       41 项无头自检（真起 HTTP 服务，用 HttpClient 全跑一遍）
└── Web/                           index.html / app.css / app.js / engine-adapter.js（内嵌进 exe）
```

| API | 作用 |
|---|---|
| `GET /api/meta` | 实例根、两个包目录、游戏是否在跑 |
| `GET /api/schema` | **节点/装饰器/服务的类型与属性表**（直接来自 `BtNodeRegistry`，编辑器物料区读它） |
| `GET /api/packages` | 列出两个目录里的 `.scbtpak`（id/入口/节点数/错误数/是否可写） |
| `GET /api/package?path=` | 读一个包（原文 manifest+tree + 校验报告） |
| `POST /api/package?path=` | **校验通过才写**，原子写；只允许实例目录；`overwrite=true` 才覆盖；写后重新装载验证 |
| `POST /api/validate` | 只校验不写盘（正文 `{manifest, tree}`） |
| `POST /api/notify?path=` | 让**正在运行的游戏**热重载（`ai.tree.notify`，带哈希） |
| `GET /api/game/status` | 直接拿游戏的 `ai.status`（模式/树/活动路径/动作包播放态） |
| `GET /api/subtree?path=&node=` | 把 `Task.Subtree` 引用的包**按游戏内同一套规则**解析出来（复用包加载器的引用闭包 + `LoadedPackage.FindReference`），供画布就地只读展开、双击进入那个包 |
| `GET /api/game/live` | **实时监视**：一次往返拿 `ai.status` + `ai.tree.snapshot`（活动节点路径/tick/上次结果）+ `ai.blackboard`（同一时刻的三份数据） |
| `POST /api/game/pause` / `resume` | 从编辑器遥控游戏侧暂停/继续行为树 |
| `GET /api/actions` | 列出两个目录里的 `.scatpak`（时长/帧数/**能否回放**/来源/只读/被遮住） |
| `POST /api/action/validate?name=` | 校验动作包（结构 + 能不能回放；含人类可读 summary） |
| `POST /api/action/play` | 让游戏**直接回放**（`ai.action.play`，传绝对路径）；`{path, repeat}` |
| `POST /api/action/stop` | 停止回放并释放输入（`ai.action.stop`） |
| `POST /api/action/create` | **自造**一个确定内容的示例动作包到实例目录（不依赖真机录制；不覆盖除非 `overwrite=true`） |

几条工程决定：
- **不用 `HttpListener`**：它在非管理员账号下要 `netsh http add urlacl`；自己写 HTTP/1.1 子集
  反而更简单，任何端口都能绑，和 CmdBridge 的控制通道同一套路（`TcpListener`）。
- **校验器只有一份**：编辑器工程 `Compile Include` 游戏内的 `Package/**`、`Bt/**`（排除自检与游戏胶水），
  编译成同一个 dll 的一部分 —— 于是"编辑器说能存 = 游戏说能跑"。
- **保存后立刻重新装载验证**（写出来的字节自己读一遍），避免"写了个游戏读不了的文件"。
- 前端是**零依赖的三分栏**（物料/画布/属性），全部内嵌；`engine-adapter.js` 是 lowcode-engine 的挂载点
  （见 D15 结论）。


- 前端（推荐 Vite + React + TS，或原生 ES 模块 + SVG 画布两选一，见 D2）构建产物作为
  `EmbeddedResource` 打进 exe；构建期需要 node，**运行期不需要任何外部依赖**。
- 产物：`PlayerAiEditor.exe`（Windows）；Android 端不做编辑器（只做运行时）。
- **内核选型**由 D15 的 spike 决定：基于 `alibaba/lowcode-engine`（MIT，vendored UMD，白送骨架/拖拽/属性面板/撤销重做）
  还是自研（Vite + React + 图库，贴合行为树语义、体积更小）。两者都不改变"离线 + 单文件 exe"的形态，见 §15.2
  与 `doc/player-ai-references.md` §2.2。

---

## 8. AI 控制面（"能操作 AI"）

### 8.1 通道选择（待确认项 D4）

推荐：**给 CmdBridgeMod 增加"命令扩展注册" API**，PlayerAiMod 注册 `ai.*` 命令 —— 复用它已有的
TCP + token 控制面与 C# 客户端 `sccmd`，不必再造一套（也能立刻被脚本/AI 使用）。

### 8.2 命令集（P0 已实现部分）

> 通道：**CmdBridgeMod 的扩展命令注册表**（`RegisterCommand`）。内建命令优先，扩展命令不能覆盖内建；
> 同名只能一个注册者，卸载时按 owner 整批摘除。客户端：`sccmd ai <子命令>`、`sccmd aiselftest`、`sccmd commands`。

| 命令 | 作用 | 状态 |
|---|---|---|
| `ai.status` | 模式、暂停、行为树来源/哈希/dirty/活动节点路径、黑板摘要、包与重载统计、模式层状态机摘要 | ✅ |
| `ai.pause` / `ai.resume` | 暂停/继续行为树（保留运行态；暂停即释放输入） | ✅ |
| `ai.enable` / `ai.disable` | 接管 / 放弃接管（disable 释放全部 AI 输入） | ✅ |
| `ai.input.release` | 只释放 AI 注入的输入，不动接管状态 | ✅ |
| `ai.blackboard` | 读/写黑板（`key` / `value` / `type=bool|int|float|string`；`all=true` 列全部） | ✅ |
| `ai.tree.list` | 列出两个包目录里的包（实例目录优先、标出 active） | ✅ |
| `ai.tree.load` | 装载/切换活动树（立即生效，`ReloadTrigger.Switch`） | ✅ |
| `ai.tree.reload` | 强制重载（**排队，tick 边界生效**） | ✅ |
| `ai.tree.notify` | 编辑器推送重载通知（`path` + `hash`；哈希不符即忽略） | ✅ |
| `ai.tree.validate` | 只校验不装载（编辑器保存前预检） | ✅ |
| `bt.selftest` | 跑内核 + 包/热重载 + 命令层三套自检，返回逐条失败原因 | ✅ |
| `ai.tree.prepare` / `ai.tree.switch` | 预编译常驻 + 毫秒级切换（§3.6）：实测 `switchMs` = 0.01~0.02 ms | ✅ P0-11 |
| `ai.tree.export` | 导出含运行时改动的活树成**新包**（来源包不动） | ✅ P0-13 |
| `ai.edit.set/insert/remove/move` | 内存态改写（只改内存，不落盘） | ✅ P0-12 |
| `ai.record.start/pause/stop/save/discard` | 录制控制（PgUp/PgDn 的等价入口，Android 无这两个键时用它） | ✅ P0-9 |
| `ai.logs` / `ai.tree.snapshot` | 事件日志 / 活动节点快照（编辑器实时监视复用） | ✅ P0-10 |
| `ai.node.find` | 定位节点（供编辑器高亮） | P3 |

### 8.2.1 （草案留存）命令集原始设计

PlayerAiMod **建立在 CmdBridgeMod 之上**：不自己造控制通道，而是复用它的 TCP + token 控制面与 C# 客户端
`sccmd`（需给 CmdBridgeMod 加一个"命令扩展注册"API，见 P0-7）。

| 命令 | 作用 |
|---|---|
| `ai.status` | 当前树、来源包+原哈希+dirty、活动节点路径、黑板摘要、暂停/录制状态、上次重载结果 |
| `ai.enable` / `ai.disable` | 接管 / 放弃本端角色（disable 会释放全部输入） |
| `ai.tree.list` | 列出树库：包名、来源目录（实例/Mod）、编译状态、是否脏 |
| `ai.tree.prepare <包>` | 预编译进"就绪队列"（切换前先把成本付掉） |
| `ai.tree.switch <包> [入口]` | **毫秒级切换**活动树（tick 边界原子替换，§3.6） |
| `ai.tree.reload [包]` | 强制重载（推送失败时的手动兜底） |
| `ai.tree.notify <path> <hash>` | **编辑器→游戏**的推送重载通知（§5.1） |
| `ai.tree.export [包]` | **导出当前内存树**（含 AI 的运行时改动），供编辑器另存到本地（§4.5） |
| `ai.pause` / `ai.resume` | 暂停/继续行为树（录制开始时自动暂停） |
| `ai.blackboard.get/set <键> [值]` | 读写黑板（调试与外部驱动） |
| `ai.record.start/pause/stop` | 录制控制（键盘 PgUp/PgDn 的等价入口） |
| `ai.action.list` | 列出动作包（名称、时长、校验状态） |
| `ai.node.find <节点id>` | 定位节点（供编辑器高亮） |
| `ai.edit.*` | 运行时内存改写：加/删/移节点、换分支、改参数（**只改内存，不落盘**，§4.5） |

---

## 9. 里程碑

| 阶段 | 内容 | 依赖 | 出口标准 |
|---|---|---|---|
| **P0（已完成）** | 行为树数据结构 ✅ + 运行时内核 ✅ + 包格式 v1 ✅ + 加载/嵌套/编译 ✅ + 热重载 ✅ + 控制面 ✅ + 示例树 ✅ + 模式整合 ✅ + 录制入口骨架 ✅ + 树库快速切换 ✅ + 内存改写 ✅ + 导出 ✅ + 观测日志 ✅ | 现有传感器/执行器、CmdBridgeMod 门面 | §10 验收 1–6 全过（需进游戏实测的部分待用户安排） |
| **P1（进行中）** | ✅ 帧流录制（`tracks/input.bin`，逐帧原始输入 + 键名表）、✅ 关键帧（每 0.5 s 位置/朝向）、✅ 语义事件（槽位/鼠标/开关/滚轮）、✅ 回放（`Task.PlayActionPackage` + `ai.action.play`，按录制节奏喂同一个执行器）、✅ 漂移检测（超阈值判失败，不瞬移修正）、✅ 校验器与 `ai.action.*`、✅ 出厂示例（`sample_walk.scatpak` + `test.action.scbtpak`）；**待做**：UI 快照轨（`meta/ui-snapshots.json`）、手柄/触屏输入的完整覆盖 | P0 | 录一段"看+走"→存档→回放，误差在阈值内 |
| **P2（MVP 已完成）** | ✅ `PlayerAiEditor.exe`：单文件 exe + 内嵌三分栏（物料/画布/属性）+ 打开/编辑/校验/保存/另存为 + **推送热重载**（已对运行中的游戏实测成功）+ spike 结论（D15：自研）；**待做**：动作包作为物料（`.scatpak` 列表/校验已在 API 里，UI 面板未做）、可视化拖拽连线 | P0（格式冻结）| 编辑器改树 → 保存 → 游戏内自动推送重载生效 |
| **P3** | 实时监视/调试（活动节点高亮、黑板观察、暂停/单步/强制切分支）+ SimpleParallel 完整 + Observer 的 OnValueChange 优化 + 节点库扩充 | P1/P2 | 编辑器里能"看着 AI 跑"并干预 |
| **P4** | 远端角色（远端执行 + 本端复现）、多 AI 实例调度、动作包市场/导入导出 | P0–P3 | 远端角色由远端 AI 驱动，本端仅复现 |

---

## 10. 当前需要实现（P0 清单）

> **状态：P0-1 ~ P0-13 与 CM-1 ~ CM-3 全部完成**（自动化自检 378/378）；剩下的是**进游戏实测**（重启后跑 `sccmd aiselftest` / `ai status` 与 CM-1/CM-2 探针，由用户安排）。
>
> 目标：**把主干打通**——"写一棵树 → 放进目录 → 游戏里跑 → 改文件 → 立刻生效 → 外部可操作"。
> 不含：完整动作包录制/回放（P1）、编辑器（P2）、实时监视（P3）。

| 编号 | 任务 | 交付物 | 备注 |
|---|---|---|---|
| P0-1 | ✅ **行为树数据结构与节点库（已完成并自检通过）** | `Bt/BtNode.cs`、`Bt/BtResult.cs`、`Bt/BtContext.cs`、`Bt/BtComposites.cs`、`Bt/BtDecorator.cs`、`Bt/BtDecorators.cs`、`Bt/BtTasks.cs`、`Bt/BtActorTasks.cs`、`Bt/BtNodeRegistry.cs` | 结果枚举（Succeeded/Failed/Aborted/InProgress）、Instant/Latent 任务、装饰器（Blackboard/Cooldown/TimeLimit/Loop/ForceSuccess/Inverter/Condition）、服务（Lambda/UpdateNearestPlayer）、节点注册表（包格式与编辑器共用的类型表） |
| P0-2 | ✅ **运行时执行器（已完成并自检通过）** | `Bt/BtRuntime.cs`（含 `BtSnapshot`/`BtNodeSnapshot`）、`Bt/BtNodeState.cs`（运行态迁移）、`Bt/BtSelfTest.cs` | tick 驱动、活跃下标记忆、**Observer Aborts**（Self/LowerPriority/Both）、帧预算、异常隔离、跑完自动重跑、活动路径快照、**`ReplaceRoot` 按「来源包/id」迁移运行态**（热重载的地基）；内核自检 **63/63 PASS**（`BtSelfTest.Run()`，临时驱动工程在 `%TEMP%\pa_selftest`，编译 `Bt/` + `Package/` + 黑板 + 接口，不依赖游戏运行时） |
| P0-3 | ✅ **包格式 v1 + 校验器（已完成并自检通过）** | `Package/PackageValue.cs`、`Package/PackageIssue.cs`、`Package/PackageJson.cs`、`Package/ScbtManifest.cs`、`Package/ScbtTree.cs`、`Package/PackageValidator.cs`、`Package/PackageSelfTest.cs` | 自持 JSON 值模型（读完 zip 即可释放句柄）、严格解析（UTF-8/无 BOM/无注释/体积与深度上限）、manifest+tree 文档模型、**规则只有一份**的校验器（形状/属性/唯一性/引用/深度全部读 `BtNodeRegistry` 的注册信息）；错误码稳定（`PackageCodes`），编辑器与控制面按码判断 |
| P0-4 | ✅ **包加载与编译（已完成并自检通过）** | `Package/PackageRoots.cs`、`Package/PackageLoader.cs`、`Package/TreeCompiler.cs`、`Bt/BtNodeSchema.cs`、`Bt/BtTestDoubles.cs`（+ `Bt/BtNodeRegistry.cs` 升级为「工厂+形状+属性表」、`Bt/BtComposites.cs` 补 `Root`） | zip 只读一次读完即关句柄、SHA-256 溯源、递归解析 `references`（相对路径 + 白名单 + 环检测 + 深度/数量上限）、`Task.Subtree` 每个引用点**独立编译实例**、编译成对象图（零运行期查表）；**双目录来源**（实例优先 / Mod 只读，§4.4）；`IPackageSource` 抽出磁盘/内存两态（推送重载与自检都不碰磁盘） |
| P0-5 | ✅ **推送式重载（已完成并自检通过）** | `Package/PackageReloader.cs` + `Bt/BtNodeState.cs` + `Bt/BtRuntime.ReplaceRoot` | 任意线程入队（`Notify`/`RequestReload`）、**tick 边界应用**（`ApplyPending`）；通知哈希与文件实际哈希不符 → 忽略（对方还在写）；内容没变 → 忽略；与活动树无关 → 拒绝；装载/校验/编译任一失败 → **保留旧树**并记原因；替换时按「来源包/id」迁移运行态（改参数不掐断正在跑的任务），旧树被丢弃的活动分支**正常中断收尾**（释放输入）；`ai.tree.reload` 兜底 + 可选低频扫描（默认 0 = 关闭）；**不锁文件**（自检里用 `FileShare.None` 独占打开验证） |
| P0-6 | ✅ **目录约定 + 示例树（已完成并自检通过）** | `Package/PackageTemplates.cs`、`Package/PackageWriter.cs`、`Core/PlayerAiRuntime.EnsurePackages()`、`PlayerAiConfig.PackageWatchSeconds` | 出厂模板 = `demo.greet.scbtpak`（Root→Selector[Sequence[Subtree(common#greet.look), MoveTo] \| Wait]，服务 `UpdateNearestPlayer(nameFilter=basil)`，装饰器 `Blackboard(target,IsSet)+LowerPriority` 抢占）+ `common.scbtpak`（被引用的最小子树，演示嵌套）；装到 `<实例根>/Mods/PlayerAiMod/PlayerAi/BehaviorTrees/`（**缺什么补什么，绝不覆盖**），用户复制到 `<实例根>/PlayerAi/BehaviorTrees/` 即可改；`PackageWriter` 负责 zip 打包与**原子写**（`.tmp` → 替换） |
| P0-7 | ✅ **控制面（已完成并自检通过）** | CmdBridgeMod 侧：`Server/CommandExtensions.cs`（`CmdBridgeCommandRequest`/`CmdBridgeCommandHandler`/`CmdBridgeCommandException` + 注册表）+ 门面 `RegisterCommand/UnregisterCommand/UnregisterAllCommands/ListCommands` + 路由宿主与 `cmd.list`；PlayerAiMod 侧：`Core/AiCommandSet.cs`（13 条命令，**纯语义、不依赖游戏**）、`Core/AiCommandBridge.cs`（通道适配 + 命令上下文）、`Core/AiCommandRequest.cs`、`Core/IAiCommandContext.cs`、`Core/AiCommandSelfTest.cs` | **一套通道、一个 token、一个 CLI**：别的 Mod 不必改 CmdBridgeMod 路由就能挂自己的命令前缀；同名只能一个注册者、按 owner 整批摘除、默认在游戏线程执行、`CmdBridgeCommandException` 映射成稳定错误码。命令：`ai.status/pause/resume/enable/disable/blackboard/tree.list/tree.load/tree.reload/tree.notify/tree.validate/input.release` + `bt.selftest`（一次跑三套自检）；客户端加 `sccmd ai <子命令>` 与 `sccmd commands`。命令层自检 **35/35 PASS** |
| P0-8 | ✅ **模式整合（已完成并自检通过）** | `Core/AiModeGraph.cs`（转移表 + `AiModeMap`）、`States/AiTreeState.cs`、`Core/PlayerAiBehaviour.cs`（只组装模式层）、`Core/AiModeSelfTest.cs`；**删除** `States/DemoLookState.cs`、`States/DemoApproachState.cs` 与 `PlayerAiConfig` 里全部示例决策参数 | FSM **退居模式层**（未接管/待机/运行树/录制中/故障）；模式由**客观事实推导**（有没有树、树有没有故障），不靠谁记得 Raise ——「控制面直接往运行时塞树」也能下一帧自动进入运行树模式；故障 → 未接管（释放输入）且**停树**（避免没人 tick 的树继续按着键）；接管态却停在未接管会**自愈**补一次接管请求；模式转移**显式关掉最短驻留**（防抖是给决策用的，给事实加延迟只会让 `ai.status` 撒谎）。自检 **25/25 PASS** |
| P0-9 | ✅ **录制入口骨架（已完成并自检通过）** | `Record/AiRecordingSession.cs`（会话状态机 + 名字消毒 + 落盘）、`Record/ScatPackage.cs`（`.scatpak` manifest/关键帧/空事件轨）、`Record/AiRecordingUi.cs`（`TextBoxDialog` 命名 + 覆盖询问）、`Record/AiRecordingSelfTest.cs`、`States/AiRecordingState.cs`（录制模式）、热键 `PgUp`/`PgDn`、命令 `ai.record.start/pause/stop/save/discard` | 会话：Idle→Recording⇄Paused→PendingSave→(命名)Idle；**录制中不 tick 行为树、进入即释放 AI 输入**（运行态保留，录完接着跑），时长/帧数只在录制中累计；`PgDn` 停止后弹命名框，**取消也不丢**（仍可 `ai.record.save name=` 另存）；同名**不静默覆盖**（`already_exists` → 覆盖/取消询问）；产物写到 `<实例根>/PlayerAi/BehaviorTrees/<名字>.scatpak`（原子写），内容为 manifest + 起点关键帧 + 空事件轨，**逐帧输入流留 P1**。自检 **31/31 PASS** |
| P0-10 | ✅ **观测与日志（已完成并自检通过）** | `Core/AiEventLog.cs`（按大小滚动、限量、写失败不影响游戏）、`Core/AiEventLogSelfTest.cs`、命令 `ai.logs` / `ai.tree.snapshot`、`PlayerAiConfig` 日志参数 | 重载/切换/改写/导出/录制/自检都留痕到 `<实例根>/PlayerAi/Logs/PlayerAi.log`（UTC 时间戳 + 分类 + 结果 + 首个原因）；按 `MaxBytes × MaxFiles` 滚动（默认 256 KB × 3）；**任何 IO 失败只记 `LastError` 并停止重试**（内存最近记录仍可用，`ai.status`/`ai.logs` 照常看）；`ai.tree.snapshot` 给出活动路径上每个节点的 id/类型/结果/驻留时间/激活次数（P3 编辑器实时监视直接复用）。自检 **24/24 PASS** |
| P0-11 | ✅ **树库与快速切换（已完成并自检通过）** | `Bt/TreeLibrary.cs`（`PreparedTree`/`TreeSwitchResult`）、`PackageReloader.Adopt`、命令 `ai.tree.prepare`（含 `all=true`）、`ai.tree.switch` | 预编译常驻（`Prepare`/`PrepareAll` + 容量淘汰）；`Switch` 只做「比对哈希 → 换根指针 → 迁移运行态」，**实测 switch = 0.01~0.02 ms**（同一棵包重新编译要 5.3 ms，即切换比编译快约两个数量级）；文件被改过 → 副本过期自动重编译并计数；切换前 `ResetSubtreeState` 保证干净起点（常驻对象图带运行态）；切换后 `Adopt` 让**热重载仍然认这棵新树为活动树**；坏包/找不到包 → 失败但**不动当前运行**。自检 18 项（包层）+ 6 项（命令层） |
| P0-12 | ✅ **内存态改写（已完成并自检通过）** | `Package/TreeMutation.cs`、`Bt/BtComposites.cs`（插入/删除/移动子节点 + 活跃下标校正）、`Package/TreeWriter.cs`（反序列化属性）、`TreeCompiler.TryApplyProperties`/`CompileNodeDocument`、命令 `ai.edit.set/insert/remove/move` | 改参数以「节点当前全部属性」为底再改一个（单改一个不会把别的打回默认）；**未知属性名按错误处理**（拼错必须当场报错，不能静默无效）；插入节点走与包装载**同一条**校验/映射路径；摘掉正在运行的子树会**先正常收尾**（释放输入）；拒绝把节点挪进自己的子树；删空组合需要 `force=true`；每次改写置 `IsDirty`；**`ai.tree.reload` 即丢弃全部内存改动**（原包永远是可回退的基线） |
| P0-13 | ✅ **内存树导出（已完成并自检通过）** | `Package/TreeWriter.cs`（`CaptureProperties`/`NodeToValue`/`TreeToValue`/`TryExportPackage`）+ 命令 `ai.tree.export name=… [overwrite=]` | 把**活树**（含 `ai.edit.*` 的改动）写成**新包**到实例包目录：manifest 沿用来源包的 blackboard/references（只改身份与入口），`Task.Subtree` **保留引用结构不摊平**；导出**绝不碰来源包**（自检比对哈希）；同名默认拒绝（`already_exists`） |
| **CM-1** | ✅ **鼠标隔离会话（已完成，待实机验证）** | `Server/FrameStartPump.cs`（帧首泵）+ `Server/UiMouseSession.cs` + `ui.*` 命令/门面 | 软光标 + 一步一帧的确定性手势（按下→移动→松开）+ mask 屏蔽真实鼠标；AI 拖拽/点击不再占用物理鼠标（§13）。实机探针：`Mod/Packages/cm1_ui_mouse_probe.py`（含"物理鼠标不动"断言） |
| **CM-2** | ✅ **焦点策略 + 共控合并（已完成，待实机验证）** | `Server/FocusPolicy.cs` + 白名单新增 3 项（`Window.m_state`、`Mouse.m_lastMousePosition`、`Mouse.MouseMovement`；滚轮两项沿用既有）+ `focus.*` / `look.owner` 命令 + OpenTK 引用 | 前台：每帧把键盘/鼠标按键合并成 `真实 \|\| AI` + 视角按"最近真实鼠标活动"仲裁（`look.owner`）；失焦：置 `Window.m_state=Active` + 帧末保 `Mouse.IsMouseVisible=true` + 帧首切断真实鼠标；`focus.status` 可观测。实机探针：`Mod/Packages/cm2_focus_probe.py`（含"切后台仍能操作"与"系统光标未被隐藏"断言） |
| **CM-3** | ✅ **热键注册表（已完成）** | `Server/HotkeyRegistry.cs` + 门面 `RegisterHotkey/UnregisterHotkey/HotkeyStatus` + `hotkey.status` 命令 + `PlayerAiMod` 绑定 | `Home` → 行为树执行/暂停（暂停即释放输入、恢复不重置运行态、HUD 提示）；`PgUp`/`PgDn` 后续走同一机制；帧首判定、线程安全、可复用（§14.6） |

**P0 验收标准**

> **自动验收**：启动游戏并进入世界后，跑一条命令即可跑完全部"可自动判断"的验收项：
> ```bash
> py -3 Mod/Packages/pack_player_ai.py --deploy     # 打包两个 scmod 并部署到 publish/Windows/Mods
> py -3 Mod/Packages/verify_player_ai.py            # 连通性 / 七套自检 / 模式与树 / 毫秒级切换 /
>                                                   # 原包字节不变 + reload 回退 / 导出 / 录制 / 事件日志 / 暂停恢复
> ```
> 脚本只做只读探测与"改内存→回退"，不会动世界；结果末尾给出通过/失败/跳过清单，
> 并提示需要人眼确认的项（`Home`、`PgUp`/`PgDn` 与命名框）。CM-1/CM-2 的实机探针会被它一并调用
> （`--skip-probes` 可跳过）。


1. 放入 `demo.greet.scbtpak` 后进游戏，AI 自动接管本端角色并跑该树；行为与手写 FSM 示例等价（看向 basil → 走近至 3 m → 待机）。
2. **推送式重载**：改包内一个参数（如 `acceptableRadius` 3.0 → 6.0）保存后，用 `sccmd` 发 `ai.tree.notify`（编辑器未就绪时也能验证该通道）→ **不重启游戏、不重载世界**，行为立刻按新值变化；`ai.status` 与日志可见"来自谁的哈希、重载结果、运行态保留/中断统计"。
3. **不锁文件**：包正在被外部写入（半截文件）或暂时不可读时，scmod 不报错、不锁文件，保留旧树并在下一次通知/重载时成功；用 `handle.exe`/资源监视器确认游戏进程没有句柄残留。
4. **嵌套生效**：树 A 通过 `Subtree` 引用包 B 的子树；改 B → A 的运行实例同步更新。
5. **双目录来源**：同名包在实例目录时优先于 Mod 目录；删掉实例目录的覆盖版后回落到 Mod 版本（`ai.tree.list` 可见来源）。
6. **快速切换**：`ai.tree.switch` 在 1 帧内完成（日志给出耗时）；切换前用 `ai.tree.prepare` 预编译可做到零卡顿。
7. **内存态不落盘**：用 `ai.edit.*` 改过内存树后，原 `.scbtpak` 字节不变（比对哈希）；`ai.tree.export` 能导出改后的活树并另存为新包。
8. **鼠标隔离（CM-1）**：把物理鼠标移出游戏窗口后，用 `ui.drag` 完成一次背包槽位"分离物品"拖拽成功；会话期间用户点击被屏蔽并有 HUD 提示；`ui.session.end` 后用户鼠标立即恢复。
8b. **后台输入（CM-2）**：前台时鼠标行为与改动前完全一致（游戏照常隐藏/捕获光标）；切到别的软件（非最小化）后，AI 仍能行走/转向/开面板/点击，**系统光标保持可见且不被移动**，在别的软件里移动鼠标/滚轮**不会**带偏视角或误切快捷栏；回到游戏窗口立即恢复前台策略。
8c. **多开（CM-2）**：两个实例同时运行，各自 `CmdBridge.json` 端口独立、`sccmd --root` 分别控制，互不抢焦点、互不干扰鼠标；每个实例的 `PlayerAi/BehaviorTrees` 各自独立。
8d. **共控（CM-2）**：焦点在游戏时，你按 `W` 的同时 AI 也在按键 → 两者都生效；你松手而 AI 仍要求按住 → 角色继续走（不被你的松开掐断）；反之 AI 释放也不会打断你按住不放的键；你移动鼠标 → 视角立刻归你，停手 `lookHoldMs` 后 AI 若在跑视角任务才接管；AI 用软光标拖物品时，你仍能用真实光标操作界面，互不干扰。
8e. **Home 切换（CM-3）**：焦点在游戏窗口时按 `Home` → 行为树在"执行/暂停"间切换，HUD 有提示、`ai.status` 同步；暂停瞬间 AI 按住的键被释放；再按 `Home` 从当前位置继续（不重置运行态）；录制中按 `Home` 只切行为树，不影响录制状态；失焦时 `sccmd ai.pause/resume` 等价可用。
9. `ai.*` 命令可用：查状态、暂停/继续、切换树、读黑板、强制重载、导出（用 CmdBridge 的 `sccmd` 验证）。
10. **校验拒绝坏包**：故意写一个引用不存在黑板键/存在环的包 → 游戏内提示 + 旧树继续运行，坏包不影响当前行为；包被拒后 `ai.status` 给出具体原因与位置。

---

## 11. 决策记录（全部已定）

| 编号 | 问题 | 决定 |
|---|---|---|
| D1 | 包存放目录 | **跟随实例走**：`<实例根>/PlayerAi/BehaviorTrees/` 为主；另有 `Mods/<scmod 目录>/PlayerAiMod/PlayerAi/BehaviorTrees/` 作为**随 Mod 分发**的只读来源；同名以实例目录优先（§4.4） |
| D2 | 编辑器形态与前端栈 | 浏览器 + 内嵌静态资源（Vite + React；构建期需要 node，**运行期零依赖**）；不做 WebView2 |
| D3 | 动作包重名 | **询问覆盖 + 另存为入口**，不静默加序号 |
| D4 | 控制面通道 | **复用 CmdBridgeMod**（已实现命令扩展注册 API，见 P0-7）：一套通道、一套 token、现成 `sccmd`；同名命令单注册者、按 owner 整批摘除、默认游戏线程执行；PlayerAiMod 就是建立在它之上的 |
| D5 | 现有 FSM 去留 | **保留为模式层**（未接管/待机/运行树/录制中/故障），不再表达决策 —— P0-8 已落地：转移表在 `AiModeGraph`、模式由事实推导、只依赖 `IAiTreeHost` 接口、示例决策状态已删除 |
| D6 | 回放漂移策略 | **失败即交回行为树决策**（可重试/换分支），**禁止**瞬移类特权修正 |
| D7 | UI 事件回放粒度 | **selector + 归一化坐标 + UI 快照都记**，优先按键路径 |
| D8 | 热重载触发方式 | **推送式**：编辑器保存后通知 scmod（`ai.tree.notify`）；不做反复扫描；保留手动 `ai.tree.reload` 与可选低频扫描兜底（§5.1） |
| D9 | 运行时改写与落盘 | AI 可改**内存树**，**绝不写回原包**；持久化只由人经编辑器 `ai.tree.export` → 另存新包（§4.5） |
| D10 | 行为树切换速度 | 全树预编译常驻 + tick 边界原子切换 + 可预排（`ai.tree.prepare/switch`），满足"AI 决定后尽快切换"（§3.6） |
| D11 | 鼠标隔离 | 新增"**虚拟 UI 鼠标会话**"：AI 的拖拽走软光标 + 每帧强制按键状态，**不占用物理鼠标**（§13） |
| D13 | 焦点回来后的控制权 | **共控**：真实输入与 AI 注入**共同参与**（每帧合并 `真实 \|\| AI`，只在窗口真正获得焦点时合并）；视角按"最近真实鼠标活动"仲裁（`look.owner auto/user/ai/shared`）；UI 由软光标与真实光标**双路并存**（§14.2.2） |
| D15 | 编辑器内核选型 | **spike 已做，结论：本期自研**（零依赖三分栏 + 内嵌静态资源），lowcode-engine 留挂载点。依据（实测）：真实包名是 **`@alilc/lowcode-engine`**（不是 `@alibaba/…`，后者在 npm 上 404）；1.3.4 解包 **6.17 MB**，只发布 CJS/ESM（`lib/engine-core.js`、`es/engine-core.js`）**没有现成 UMD**；要真用起来还需 `@alilc/lowcode-engine-ext`，其 peer 依赖是 **react ^16.3.0 + @alifd/next 1.x**（还要打包链），且我们的 `tree.json` 与低代码搭建协议**并不同构**（要额外映射层）。自研路线 0 依赖、4.93 MB 单文件、离线可用（§15.2） |
| D16 | 参考资料本地化与合规 | 建 `doc/player-ai-references.md` 存借鉴结论；UE 只稀疏检出 `AIModule`、**只借鉴语义不逐行抄**（专有许可）；lowcode-engine 若 vendored 需保留 MIT 许可声明；不确定处标 `待源码确认`（§15） |
| D14 | 行为树执行/暂停热键 | `Home` 切换（焦点在窗口时）；实现在 **CmdBridgeMod 热键注册表**（帧首判定、可复用），PlayerAiMod 绑定；`PgUp/PgDn` 走同一套机制（§14.6） |
| D18 | 热重载的粒度与迁移 | 通知 → **重新装载整棵活动树**（引用闭包一起刷新，包都很小、毫秒级），不做「只换一个文件」的增量更新；换树时按「来源包/id」迁移运行态，父链必须一路匹配；装饰器/服务计时不迁移（P3） |
| D17 | 编译产物形态与错误码 | 编译成**对象图**而非平坦下标数组（等价更快、少一层不同步风险，§4.6）；问题码走稳定表 `PackageCodes`，控制面/编辑器按码判断；**任何 error 拒绝装载**，warning 只提示 |
| D12 | 后台/失焦控制 | **按焦点分两套策略**：前台 = 完全跟随引擎（游戏自己隐藏/控制鼠标）；失焦 = **脱离实际鼠标**（虚拟焦点 + 保持系统光标可见 + 切断真实鼠标增量/滚轮 + 软光标做 UI），自动切换也可强制。不抢焦点、不碰游戏状态，实现在 CmdBridgeMod 供复用（§14）；由此支持"游戏放后台 / 多开 AI" |

---

## 12. 风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| 回放在物理/联机环境下不确定 | 动作走偏、失败 | 相对化记录 + 关键帧漂移检测 + 失败交由行为树处理；录制时记录环境快照（地图名、手持物、游戏模式） |
| UI 点击回放脆弱 | 界面层级/分辨率变化即失效 | 记录 selector + 归一化坐标 + UI 快照；尽量用按键替代（CmdBridge 已有结论） |
| 热重载与运行中任务冲突 | 状态错乱、输入残留 | 只在 tick 边界替换；按 id 尽量保留运行态；被中断任务强制 `AbortTask` 释放输入 |
| 校验逻辑两份漂移 | 编辑器能存、运行时跑不了 | 校验器只实现一份（C#），编辑器远程调用 |
| 包格式演进 | 老包跑不起来 | `version` + 迁移器 + 拒绝时明确报错（不半加载） |
| 帧预算被大树吃满 | 掉帧 | 节点数上限、单帧访问上限、服务 interval 下限、热路径零分配 |
| 记录的动作包与地图强耦合 | 换图即失效 | 相对化 + 起始状态校验 + 编辑器里标注适用场景 |
| Android 无 PgUp/PgDn | 无法录制 | 控制面命令 + HUD 按钮作为等价入口（P1） |

---

## 13. 鼠标隔离：虚拟 UI 鼠标会话（CmdBridgeMod 侧）

> **起因**：早期做"分离物品"（背包槽位拖拽）时走的是**真实光标**方案（`SetCursorPos` + 注入按键），
> 于是必须先把物理鼠标挪进游戏窗口；而且真实光标一旦在窗口外，引擎会把"按下点 vs 当前点"算成拖拽而走错分支。
> **结论**：拖拽类 UI 操作应当走**引擎内的虚拟（软）光标**，物理鼠标完全不参与。

### 13.1 原理（源码依据）

- `WidgetInput.MousePosition`：`UseSoftMouseCursor=true` 时返回可编程的 `m_softMouseCursorPosition`，
  否则返回真实 `Mouse.MousePosition` → **开了软光标，UI 就只看软光标，物理鼠标位置无关**。
- `UpdateInputFromMouse` 只在 `IsMouseCursorVisible && MousePosition.HasValue` 时派生 `Press/Tap/Click/Drag`；
  模态面板打开时 `IsMouseCursorVisible=true`（`ComponentInput.cs:143-152`），软光标同样满足。
- 拖拽启动条件：距离超过 `MinimumDragDistance * Widget.Scale`，且**第一帧超阈值时仍命中源控件**
  （`InventorySlotWidget.Update` 要求 `HitTestGlobal(Drag) == this`）→ 软光标必须**小步移动**（每帧 10~30 px）。
- 按键状态是引擎静态数组，真实鼠标与注入**共用** → AI 占用期间必须**每帧强制写回**期望状态，
  否则用户的真实点击会混进同一条拖拽。
- 面板打开时游戏把鼠标交给 UI、不用鼠标做视角（`ComponentInput.cs:143`），所以用户此时用键盘照常玩不受影响。

### 13.2 新增 API（CmdBridgeMod 门面 + 控制面）

| 能力 | 说明 |
|---|---|
| `ui.session.begin [--mask]` | 开始"AI 占用鼠标"会话：开软光标；`--mask` 时每帧强制按键状态（屏蔽用户真实鼠标） |
| `ui.cursor <x> <y>` / `ui.cursor at <路径>` | 移动软光标（客户区坐标 / 元素中心） |
| `ui.press [left\|right]` / `ui.release` | 软光标处按下 / 松开（跨帧保持） |
| `ui.move <x> <y> [--steps N]` | 小步移动（保证拖拽在源控件内启动） |
| `ui.drag <路径A> <路径B> [--steps N] [--holdMs M]` | 一步到位：定位 A → 按下 → 小步到 B → 松开（背包/容器搬运、滑条、列表滚动） |
| `ui.specialclick` | 长按分割源 + 目标点击、`Shift+左键`、右键等特殊点击路径（实测各自分支不同） |
| `ui.session.end` | 结束会话：释放按键、关软光标、交回用户鼠标 |

### 13.3 体验与边界

- AI 操作期间**物理鼠标完全自由**（可移出窗口去干别的），不再需要"专程挪进游戏"。
- `--mask` 会话内用户点击不生效（避免与 AI 拖拽互相污染），HUD 提示"AI 正在操作鼠标"；
  会话默认超时（如 5 秒无动作自动结束），避免"鼠标被锁住"的观感。
- 用户主动点击可选择**抢占**：结束 AI 会话 → 该任务判失败 → 交回行为树决策（与 D6 一致）。
- **录制/回放受益**：UI 事件按 D7 记 selector + 归一化坐标 + UI 快照，回放走软光标，
  不再依赖分辨率与物理光标位置，跨设备稳定得多。
- 该能力属于 **CmdBridgeMod（输入层）**；PlayerAiMod 只是调用方（与 D4 一致）。

### 13.4 工作量与定位

`InputInjector` 会话状态机 + 软光标小步拖拽约 250 行；公开门面约 12 个方法；命令路由约 8 条；
自检脚本补几条断言。**不触碰任何游戏状态写入**（仍全部落在输入层白名单内）。
建议作为**独立小步先做**：立刻可用（分离物品不再需要物理鼠标），同时是 P1 回放的前置。

---

## 14. 后台输入：虚拟焦点（游戏不在前台也能控制）

> **起因**：切到别的软件后 AI 就控制不了游戏。实测根因不是"注入失效"，而是游戏侧三处 `Window.IsActive` 判断。

### 14.1 根因（源码）

| 位置 | 行为 |
|---|---|
| `ComponentInput.cs:91-94` | `!Window.IsActive` → **把整个 `m_playerInput` 置空**（移动/挖掘/放置/交互/切面板全部丢弃） |
| `ComponentInput.cs:158` | 失焦时不算"鼠标增量带来的视角"（AI 用绝对朝向，本不需要它） |
| `Game/WidgetInput.cs:628` | `WidgetInput.Update()` 只在 `Window.IsActive` 时才派生 `Press/Tap/Click/Drag`（UI 点击随之失效） |

同时确认：**游戏失焦时并不暂停模拟**（帧循环与实体系统照常推进），所以"只要输入被采纳，就能继续控制"。

### 14.2 方案：让引擎"以为"窗口是活跃的（只在需要驱动时）

| # | 做法 | 为什么 |
|---|---|---|
| 1 | 帧末（`Frame.Update` 钩子）把 `Engine.Window.m_state` 置为 `Active` | 一个开关同时解掉上表三处判断；下一帧 `Dispatcher.BeforeFrame → Keyboard/Mouse.BeforeFrame → 帧体` 全按"活跃"路径走 |
| 2 | 同一钩子里把 `Mouse.IsMouseVisible = true` | `Widget.cs:768-770` 每帧按"界面是否需要光标"重写它（世界视图里是 `false`），而 `Mouse.BeforeFrame()` 只在 `Window.IsActive` 时才执行 `CursorVisible = IsMouseVisible` → 不覆盖会**全局隐藏系统光标**，坑到用户在别的软件里用鼠标 |
| 3 | **切断真实鼠标**：注入点（`Dispatcher.BeforeFrame`，正好是 AI tick）把 `Mouse.m_lastMousePosition`/`m_lastMouseWheelValue` 置空，并把 `Mouse.MouseMovement`/`MouseWheelMovement` 清零 | `Mouse.BeforeFrame()` 只在 `m_lastMousePosition.HasValue` 时才用 `state - 上帧位置` 算增量 —— 置空后它**根本不会计算**，真实鼠标在别的软件的移动/滚轮进不来。⚠️ 必须同时清零派生的两个量，否则会把"失焦前最后一帧的残留增量"当成每帧输入重复施加 |
| 4 | UI 操作走"虚拟 UI 鼠标会话"（§13） | 软光标下 `WidgetInput.MousePosition` 不读真实光标，游戏也不会用 `SetMousePosition` 去动用户的鼠标 |
| 5 | 会话结束即停止干预，让 `m_state` 回到引擎自己的焦点事件驱动 | 用户正常使用完全不受影响，只影响"命令式强制"的那一段 |
| 6 | `IAiSensor.IsInputAccepted` 改为"自然前台 **或** 虚拟焦点会话中" | 让 AI 的自检与"输入是否真被采纳"一致 |

### 14.2.1 两套策略（按焦点自动切换，也可强制）

| 焦点状态 | 策略 | 具体行为 |
|---|---|---|
| **游戏在前台** | `FollowEngine`（默认，什么都不做） | 游戏自己的鼠标逻辑照旧：该隐藏就隐藏、该捕获就捕获；真人操作与 AI 注入共用一条通道（现有行为） |
| **游戏失焦** | `Detached`（脱离实际鼠标） | ① `Window.m_state=Active` 让输入被采纳；② `Mouse.IsMouseVisible=true` 不让游戏去动系统光标；③ 置空/清零上表第 3 步的四个量，真实鼠标彻底不影响游戏；④ UI 走软光标会话（§13） |

- 切换是**自动**的（跟随 `Window` 的真实焦点状态），也支持命令强制：`focus.auto` / `focus.detach` / `focus.attach` / `focus.status`。

##### 14.2.1a 帧首执行的前提（已实测，CM-1 落地）

`Dispatcher.Dispatch` **在主线程调用会立即执行**（`Engine/Engine/Dispatcher.cs:40-44`），
只有**后台线程**调用才会入队并在下一帧 `BeforeFrame` 执行（同文件 `:79-105`）。
而 `Mouse.AfterFrame()` / `Keyboard.AfterFrame()` 会在帧末清空 downOnce 数组
（`Engine/Engine/Input/Mouse.cs:132-138`）—— 所以"按下脉冲只能写在帧首"，
写在 `Frame.Update`（帧末）会被同帧清掉。

因此本 Mod 提供 **`FrameStartPump`（帧首泵）** 作为可复用基建：
`Frame.Update`（帧末）发信号 → 后台线程把队列整体入队 → 下一帧帧首在游戏线程依次执行。
CM-1 / CM-2 / CM-3 与 PlayerAiMod 的 AI tick 都走同一套机制
（门面已公开 `PostToFrameStart` / `FrameStartPumpRunning` / `FrameStartPending`）。

### 14.2.2 焦点回来时：真人与 AI **共控**

焦点回到游戏后，不是"AI 让位"也不是"只能其中一个"，而是**两者共同参与**（这也符合最初的设计：鼠标与键盘照常可用，AI 只是另一个输入源）：

| 输入 | 前台（共控） | 失焦（脱离） |
|---|---|---|
| 键盘（按住/脉冲） | 每帧在 `Dispatcher.BeforeFrame` 把引擎数组写成 **`真实按下 \|\| AI 要求`** | 只保留 AI 状态 |
| 鼠标按键 | 同上（真实点击事件本来就只在窗口获得焦点时才到达） | 只保留 AI 状态 |
| 滚轮 | 真实滚轮 + AI 注入**叠加** | 只用 AI（真实被切断） |
| 视角 | **仲裁**：最近 `lookHoldMs`（默认 400ms）内真实鼠标动过 → 视角归用户；否则归 AI（AI 用绝对 yaw/pitch，用户增量会叠加在其上） | AI 独占 |
| UI 点击 / 拖拽 | AI 走软光标会话（§13）；用户用真实光标照常点，**两条路同时可用** | AI 走软光标；真实不参与 |
| 热键（Home / PgUp / PgDn） | 真实按键与 AI 注入都能触发（同一个热键注册表，§14.6） | 只有 AI 注入能触发（用户按键到不了游戏） |

- **为什么"合并"只在窗口真正获得焦点时做**：Windows 上 `OpenTK.Input.Keyboard.GetState()` 取得的是**全局**键盘状态，
  失焦时若也合并，"你在别的软件打字"会变成角色走动。所以合并以真实焦点为界 —— 与两套策略天然一致。
- **合并的实现**：真实状态用 OpenTK 的 `Keyboard.GetState()` / `Mouse.GetState()` 采集（给 CmdBridgeMod 加 OpenTK 引用；
  Windows 下亦可用 `GetAsyncKeyState`/`GetCursorPos` P/Invoke 兜底），在 `Dispatcher.BeforeFrame` 写入合并结果 ——
  此刻 OS 事件处理器已把真实状态写进引擎数组、而消费者（`ComponentInput.Update`）还没读，位置正好。
- **视角仲裁**可用命令固定：`look.owner auto|user|ai|shared`（`shared` = 用户增量 + AI 绝对目标都生效）。
- **不需要再抢焦点**：虚拟焦点生效后，AI 不再依赖 `SetForegroundWindow`，多开时也不会互相抢窗口（旧实现按窗口标题找窗口，多开必然找错）。
- 复用方式：这套策略放在 **CmdBridgeMod（输入层）**，通过公开门面暴露 `SetFocusPolicy(FocusPolicy)` / `IsDetached`，
  PlayerAiMod 与将来的任何 Mod 都只是调用方（符合"尽可能用 CmdBridgeMod/PlayerAiMod 实现、要能复用"）。

**新增的输入层白名单成员**（必须进 `InputWhitelist` 并由只读审计脚本覆盖）：
`Engine.Window.m_state`、`Engine.Mouse.m_lastMousePosition`、`Engine.Mouse.m_lastMouseWheelValue`、
`Engine.Mouse.MouseMovement`、`Engine.Mouse.MouseWheelMovement`。
全部是输入层状态，仍然不碰任何游戏状态（生命/背包/方块/位置/时间），符合"优势 ≠ 特权"。
**不需要读全局鼠标状态**（不用加 OpenTK 引用、不用 P/Invoke）。

### 14.2.3 实测踩到的坑：`Activated` 事件被自己的强制状态吃掉（已修）

**症状**（用户实测）：窗口焦点在游戏里（键盘、点击都正常），但**鼠标没被吸进 3D 视角**，
指针跑到程序外面，视角完全转不动。

**根因**（读源码 + 实测 `focus.status` 确认）：
`Window.Activated` / `Deactivated` **不是** OS 焦点事件，而是 `m_state` 状态位的跳变回调：

```
Engine/Engine/Window.cs:340-352  FocusedChangedHandler:
    if (m_gameWindow.Focused) { if (m_state == Inactive) { m_state = Active; Activated(); } return; }
    if (m_state == Active)    { m_state = Inactive; Deactivated(); }
```

而"失焦虚拟焦点"做的正是 `m_state = Active`（`FocusPolicy.ForceEngineActive`）。
于是用户回到游戏时 `m_state` 已经是 Active → **那个 `Inactive→Active` 跳变永远不会发生**
→ `Activated` 不触发 → `realFocus` 永远停在 false → 策略一直以为"失焦"：

| 每帧动作 | 后果 |
|---|---|
| `CutRealMouse()`：把 `m_lastMousePosition` 置空 | `Mouse.BeforeFrame()` 的 `if (m_lastMousePosition.HasValue)` 不成立 → `MouseMovement` 恒为 0 → **视角转不动** |
| 帧末 `Mouse.IsMouseVisible = true` | `Mouse.BeforeFrame()` 里 `CursorVisible = IsMouseVisible` → **系统光标显示、不被捕获** → 指针跑到窗口外 |

**修法**：
1. 真实焦点改读 **OS 真相** `Window.m_gameWindow.Focused`（`InputWhitelist.WindowGameWindow`），
   每帧刷一次；事件降级为辅助信号（`NotifyFocus` 也走同一个读取）；
2. 读不到 `m_gameWindow` 时**整体停用脱离策略**（`realFocusSource=unavailable`）——
   宁可没有虚拟焦点，也绝不弄坏"正常前台玩"这条主路径；
3. 新增 `focus.recover` 一键自救（回 Follow + 恢复窗口状态），并在 `focus.status` 里暴露
   `realFocusSource`，下次同类问题一眼可见。

**教训（写进来免得再犯）**：凡是"我们自己会写的状态位"，就不能再用它派生的回调来判断真实世界
—— 必须去找一个**我们没碰过的**权威来源（这里是 OpenTK 的 `GameWindow.Focused`）。

### 14.3 已知边界与取舍

- **最小化**：帧循环由窗口渲染事件驱动，最小化后是否继续出帧需要实测；保守建议"放到其他窗口后面"，而不是最小化。
- **画面仍在渲染**：后台时照常出帧，CPU/GPU 占用与前台相同（想省资源见 §14.4）。
- **键盘不受影响**：OS 只把键盘事件发给前台窗口，所以你在别的软件打字不会传进游戏；被"吃掉"的只有真实鼠标的移动/滚轮。
- **共控的"松开"语义**：合并是每帧重算的（`真实 || AI`），所以"你松手但 AI 仍要求按住"时角色会继续走；
  反之"AI 松手但你仍按着"也不会被 AI 的释放动作打断 —— 不会出现两个输入源互相掐断。
- **用户抢占**：点回游戏窗口即恢复真实焦点；AI 会话可继续或让位（让位则该任务失败、交回行为树决策，与 D6 一致）。
- **不是万能**：若游戏被系统挂起（最小化/省电/独占全屏切换），帧循环本身可能停，任何注入都无效。

### 14.4 多开：同时跑多个实例 / 多个 AI

脱离模式让"后台不抢焦点"成为默认能力，于是多开只剩三件配置事（都在 Mod 侧，不动游戏源码）：

| 事项 | 做法 |
|---|---|
| **端口不撞** | `CmdBridge.json` / `CmdBridge.runtime.json` 在**实例根目录**，天然每实例一份：首启随机端口、写入 runtime 文件；客户端用 `--root <实例根>` 指定目标（`sccmd` 已支持） |
| **不再抢焦点** | 取消/禁用"按窗口标题找窗口再置前"的逻辑（多开时标题相同会找错）；虚拟焦点不依赖前台 |
| **世界与资源配置** | 每个实例各自独立的 `data:` 世界目录；**禁止两个实例写同一个世界**（会互相覆盖）。要"多个 AI 进同一个世界"，正确做法是 host 实例 + 联机客户端（P4 远端路径），而不是多开同一个世界目录 |
| **资源占用** | 每个实例都照常渲染，多开会线性吃 CPU/GPU；可选：每实例降分辨率/关特效，或参考仓库既有 `HeadlessRenderingMod`（隐藏窗口、不绘制）的思路做"后台实例" |

### 14.5 更彻底的替代方案

| 方案 | 适用 | 代价 |
|---|---|---|
| 本地虚拟焦点（本节） | 想立刻见效、继续用当前实例 | 需要 3 个白名单成员；后台仍渲染；最小化可能不推进 |
| **后台/无窗口实例**（仓库已有 `HeadlessRenderingMod`：隐藏窗口、不绘制） | AI 独自长时间跑 | 需要另一个实例（与世界副本） |
| **远端玩家路径（P4）** | 联机语义、本端只复现 | 依赖联机 Mod 的权威通道 |

### 14.6 热键：Home 切换行为树执行/暂停（以及 PgUp/PgDn 录制）

需求：AI 运行期间，**焦点在游戏窗口时按 `Home`** 在"执行 / 暂停行为树"之间切换。

- 实现在 **CmdBridgeMod 的热键注册表**（输入层，可复用）：`Register(Key, Action)` / `Unregister`，
  在 `Dispatcher.BeforeFrame`（帧首）判定触发，天然与注入同语义、不受失焦影响。
- **PlayerAiMod** 注册 `Home` → 切换行为树暂停（等价 `ai.pause` / `ai.resume`）；同时注册
  `PgUp`/`PgDn` 的录制控制（§6.1）→ 三个键走同一套机制，将来任何 Mod 都能注册自己的热键。
  Source: `Engine/Engine/Input/Key.cs:32`（`Key.PageUp` / `Key.PageDown`；`Key.Home` 同属该枚举）
- 切换时：HUD 显示状态（小提示 + 可选常驻标签）、`ai.status.paused` 同步、**暂停瞬间释放 AI 按住的全部输入**
  （避免"暂停了还在走"），恢复时从当前位置继续执行（不重置运行态）。
- 三种状态互不干扰：`Home` 只管行为树；`PgUp/PgDn` 只管录制（录制中行为树本来就被暂停，见 §6.1）；
  失焦时用户按不到这些键，用控制面命令等价操作（`sccmd ai.pause` / `ai.record.*`）。

---

## 15. 参考资料与代码库

> 逐条借鉴笔记（UE 文件索引、结论条目、本仓库源码结论索引）单独维护：
> **`doc/player-ai-references.md`**。本节只记"用什么、怎么用、边界在哪"。

### 15.1 三条参考线

| 参考 | 用途 | 怎么用 | 边界 |
|---|---|---|---|
| **虚幻引擎行为树**（专有许可；需 Epic 账号已关联 GitHub） | 行为树语义权威：节点 / 装饰器 / 服务 / 中断 / 黑板 / 执行器 / 编辑器图表示 | `git clone --filter=blob:none --sparse` + `git sparse-checkout set Engine/Source/Runtime/AIModule`，**只取该目录**（几十 MB）；按笔记里的文件索引逐个主题读 | **只借鉴语义与结构，不逐行抄代码**；不确定处标 `待源码确认`（仓库铁律：不以记忆为准） |
| **低代码编辑器参考文章**（掘金） | 三分栏布局、组件树状态、节点注册表、拖拽重复触发、撤销重做 | 作为我们编辑器的实现蓝本 | 仅思路借鉴 |
| **alibaba/lowcode-engine**（[MIT](https://raw.githubusercontent.com/alibaba/lowcode-engine/main/LICENSE)，Copyright (c) 2021 Alibaba） | 现成的低代码内核：骨架 / 物料 / 设置器 / 插件 / 渲染器 | 见 §15.2；若采用则把 UMD 产物 vendored 进 exe 内嵌资源 | MIT 可商用，**需保留版权与许可声明** |

### 15.2 编辑器内核选型（D15）

- **能不能基于 lowcode-engine 打包成一个 exe 低代码平台：能。** 它是一套 web 引擎（UMD + React 生态），
  与 §7 的"单文件 exe + 内嵌静态资源 + 本地 HTTP 服务"完全兼容；唯一限制是**官方只发布 CDN 形式**
  （`npm install` 只为 typings），所以必须把 `engine-core.js`、模拟器渲染器与 React 等**本地化 vendored**，
  以满足离线要求（代价：exe 增大到数十 MB 级）。
- **关键差距**：它面向"页面搭建"，渲染器负责把 schema 渲染成页面组件；我们只需要"编辑行为树 + 属性面板 + 校验"。
  两条路线对比（**当 schema 编辑器外壳** vs **自研 Vite + React + 图库**）见 `doc/player-ai-references.md` §2.2。
- **决定方式**：先做 **1~2 天技术验证（spike）**：用它的 UMD 做一个最小 material
  （`Selector` / `Sequence` / `Task.PlayActionPackage`）+ 一个 setter，验证"能否直接编辑我们的 `tree.json` 并回写"。
  成立 → 路线 1；不成立 → 路线 2。**两条路线都满足"离线、单文件 exe、浏览器访问"**。
- 无论哪条路线：**校验逻辑只有一份**（C#，§7.3），编辑器通过本地 HTTP 调用；素材区照样包含**动作包**（§7.2）。

### 15.3 借鉴工作流（避免重复研究）

1. 动手前先查 `doc/player-ai-references.md` 是否已有结论条目。
2. 一次只读一个主题（按该文件的"文件索引"定位），读完立刻补一条结论（来源 + 落点 + 日期）。
3. 结论必须写明"影响方案的哪一节"；方案文档只保留最终结论，细节留在笔记里。
4. 每轮实现后把发现的差异回填笔记；不确定的写法标 `待源码确认`。

### 15.4 本仓库内参考

| 位置 | 用途 |
|---|---|
| `Mod/CmdBridgeMod/Server/CmdBridgeInput.cs` | 玩家控制器注入门面（PlayerAiMod 的执行器就是转交给它） |
| `Survivalcraft/Game/TextBoxDialog.cs:20` | 通用文本输入对话框：录制结束后的命名复用（标题/初值/最大长度/回调，自动聚焦与回车确认） |
| `Engine/Engine/Input/Key.cs:32` | 按键枚举（`PageUp` / `PageDown` / `Home` 等；Android 侧映射见 `Keyboard.Android.cs`） |
| `Mod/doc/player-ai-references.md` §3 | 读本项目源码得到的关键结论索引（失焦输入丢弃、光标控制、软光标、交互射线、创造目录顺序等） |

---

## 16. 实机验收记录：2026-09-12（关游戏→部署→重启→进世界→跑验收）

这一节按**时间顺序**记"实机跑出来什么、挖出什么坑、怎么修的"，因为本轮的几个坑都只在真机上才现形
（无头自检一律全绿）。

### 16.1 部署与进世界（全部走玩家控制器）

| 步骤 | 做法 | 结果 |
|---|---|---|
| 关游戏 | `Stop-Process`（用户已授权任意重启） | world `pingtan`（显示名 Rebritish）最后一次自动存档在关闭前 1 分钟 |
| 部署 | `py -3 Mod/Packages/pack_player_ai.py --deploy` | 两个 `.scmod` 覆盖进 `publish/Windows/Mods/`（ZIP 结构 + sha256 已校验） |
| 重启 | `Survivalcraft.exe`（cwd=实例根） | 控制通道约 1 s 后写出新的 `CmdBridge.runtime.json` |
| 进世界 | `py -3 Mod/Packages/enter_world.py Rebritish` | 全程 `act.uiclick`（引擎软光标）点 `Play` → 世界列表第 1 行 → `Play!`，3 s 进世界 |

`enter_world.py` 是这轮新增的开发脚本：**不写状态**，只点玩家自己会点的按钮；
`ui_scan.py` / `ui_list.py` / `scat_dump.py` / `cmd_bridge.py` 同理（观测与解码，Python 侧）。

### 16.2 挖出的三个真坑（都已修 + 已实测）

**① `SampleRecordingFrame()` 从来没被调用 → 录出来的包永远不可回放。**
`PlayerAiRuntime.SampleRecordingFrame()`、`PlayerInputSampler`、`AiRecordingSession.CaptureFrame`
三件套都在，**但插件那侧只调了 `ScheduleFrameStartTick`**，帧末采样一次都没发生。
症状极具欺骗性：录出来的包 manifest/keyframes/events 齐全、校验 0 error（只有一条 WARN
"no per-frame input track"），`replayable=false` —— 不真的去回放根本发现不了。
修：`Plug/PlayerAiMod.cs` 的 `Frame.Update` 回调里补上采样调用；
并把"插件必须调用这两个钩子"做成 `check_player_ai_build.py wiring` 的源码级断言（防止再漏）。

**② `ai.record.stop/save` 的 `path` 字段里塞的是整句人话（`"saved D:\…"`）。**
脚本照着 `path` 去开文件必然炸（`WinError 123`）。修：`AiRecordingSession.Save()` 返回**真实路径**，
人话留给事件日志与 `recording.message`。

**③ `ui.reachability` 会把游戏进程干掉（最严重）。**
`UiInspector.Reachability` 把 `ToScreenPoint(...)` 的 `Vector2?` **裸着**放进回包；
而 `Engine.Vector2` 有个 `YX` 属性（返回新的 Vector2）→ `System.Text.Json` 顺着 `$.YX.YX.YX…`
一路递归 → 抛 "possible object cycle" → 这是**后台网络线程上的未处理异常** → 游戏进程直接退出
（Game.log 里只有一句 `Unhandled exception.`）。两处修：
- payload 侧：坐标一律过 `UiInspector.PointToValue()`（`{x,y}` 或 null）；
- 框架侧：`ControlServer.WriteResponse` 把序列化包进 try/catch，失败就回
  `response_not_serializable` 错误回包（**任何**回包都不该能带走游戏）。
回归网：`py -3 Mod/Packages/smoke_bridge_commands.py` —— 27 条只读命令全跑一遍，
末了再 ping 一次确认进程还活着。

### 16.3 实测出来的两个"看着像坑、其实是设计"的行为

- **录制时别让 AI 接管**：接管中的树会重新占有/释放它自己的输入（`Task.PlayActionPackage`
  起手就 `ReleaseInput()`），于是从外面注入的"按住 W"可能只活一帧，录出来就是一帧脉冲。
  真人按键不受影响（那不在注入器的 held 集合里）。自检脚本因此先 `ai.disable` 再录，
  并直接把 `tracks/input.bin` 解码开验"一段连续 held + 只有一次按下沿"。
- **`ui.cursor/ui.click` 的目标必须真的可点**：面板打开时 HUD 的 `BackButton` 被面板挡住，
  会话会（正确地）报 `element_occluded` 并中止手势。CM-1 探针原来点了它，看起来像 Mod 坏了，
  其实是探针挑错了目标（改成点 `InventoryButton`，用"面板被打开"当可观测效果）。

### 16.4 CM-1 虚拟 UI 鼠标会话：三处执行模型缺陷（已修，探针 11/11）

| 缺陷 | 症状 | 修法 |
|---|---|---|
| 步骤队列**跨会话残留** | 上一轮 `End()` 排的 Finish 留到下一轮才执行，新会话刚起步就被"结束"掐断：`ui.click` 报 `completed=false`、面板不关、`pendingSteps` 永远回不到 0 | `Begin()` 清队列 + 重置计数；`FinishSession()` 丢弃剩余步骤 |
| 帧首泵**把整批回调塞进同一帧** | `ui.click` 的"按下 + 抬起"落在同一帧 → 引擎派生不出 Click（`WidgetInput` 靠"按下留起点、松开那帧派生 Click"） | 会话改**一步一帧**：`TickOnce` 执行完一步再补排下一个回调（`m_tickQueued` 保证不重复排） |
| `Begin()/End()` 自己不排帧首 tick | `begin` 的 Assert / `end` 的 Finish 一直挂在队列里，`pendingSteps` 卡在 1、`active` 结束不掉 | 两处都调 `QueueTickIfNeeded()` |

实测：软光标精确落在元素中心（0.0 px 误差）、`ui.click` 真的把背包点开了（`completed=true`）、
物理鼠标位移 **0 px**、结束后无残留按键。

### 16.5 最终验收口径

| 验收 | 结果 |
|---|---|
| 无头自检 | **430/430 ALL PASS**（8 套）；编辑器自检 **41/41**（Debug exe + 单文件发布 exe） |
| 只读命令扫一遍 | `smoke_bridge_commands.py`：**27/27**，游戏存活 |
| 录制→回放（实时） | `check_record_replay_live.py`：**ALL PASS**；轨道解码 `W held=270/330, runs=1, pressed=1`；回放真的把角色走动了 |
| 动作包回放（实时） | `check_action_replay_live.py`：**ALL PASS**（直接播放 + `test.action` 树里 `Task.PlayActionPackage` 都真的按住 W/A 走路） |
| 出厂内容 | 新构建首启即安装 `sample_walk.scatpak` + `test.action.scbtpak`；`ai.action.list` 报 3 条（实例 2 + Mod 1 只读且 `shadowed`） |
| 焦点修复 | `focus.status.realFocusSource = "gameWindow.Focused"`，`realFocus=true`，`virtualFocusAvailable=true` |
| CM-1 探针 | **11/11 PASS**（干净起手；`Mask` 会话、软光标、点击效果、物理鼠标不动、无残留） |
| CM-2 探针 | 12~14/16：失败项都是"**让游戏真的失焦**"这一步（B1）及其级联 —— 本环境里没有任何东西抢游戏窗口的焦点，探针造不出前置条件；焦点代码本身由 `focus.status` 与 `focus.recover/auto` 实测覆盖 |
| `verify_player_ai.py` | **22 PASS / 2 FAIL / 0 SKIP**，2 个 FAIL 就是上面两个探针的包装项 |

### 16.6 「进入游戏」动作包：把"从主菜单走进地图"整个过程录下来

用户要的是"把进入地图这整个过程做成动作包，叫进入游戏"。做法与踩到的坑：

**为什么不能只靠逐帧输入通道**：菜单点击走的是引擎**软光标**（CM-1）——
原始输入层里根本没有痕迹（没有按键、没有视角增量、鼠标位图也不是那个意思），
所以"在菜单里点了哪个控件"这件事**录不到原始轨道里**。因此新增一条**语义事件通道**
（复用包里已有的 `tracks/events.json`，不发明新格式）：

| 环节 | 做法 |
|---|---|
| 录制 | 注入器每做一次 UI 动作就记一条（`click:<选择器>` 或 `click:<x>,<y>`）；录制端帧末读输入快照时**取走**这批动作，写成 `kind=ui.click` 的事件（时间点 = 当时录制时长） |
| 存储 | `ui.click` 事件与逐帧轨道**并存**：轨道负责"世界里的动作"，事件负责"菜单里的动作"，同一个时间轴 |
| 回放 | `ScatPlayer` 按事件时间点重放点击；**注入走 CM-1 会话**（一步一帧），不动物理鼠标 |
| 主菜单没角色 | `ai.action.play` 在没有玩家时退化为 `UiOnlyActuator`（只做 UI 点击；世界外的按键本来也没意义），于是这个包在**没有世界**时也能放 |

**踩到的两个坑（都已修，且都只在实机上现形）**：

1. **事件里的 `click:` 前缀被当成选择器**：录制时为了可读性写的是 `click:Play`，
   回放端却直接把整串当选择器 → `element_missing`，而且当时注入失败是**静默**的
   （外观是"命令成功、界面不动"）。修：回放端剥掉 `<动词>:` 前缀；
   `IAiActuator.UiClick` 改成返回 `bool`，拒绝就写进 `lastError` 与日志，不再假装成功。
2. **按下与抬起落在同一帧 → 引擎派生不出 Click**：回放在**游戏线程的帧首 tick** 里跑，
   `InputInjector.UiClick` 的三段式注入在那里会**就地执行**，于是"软光标 + 按下 + 抬起 + 收尾"
   全在同一帧完成，`WidgetInput` 什么也没派生出来（实测：返回成功、界面纹丝不动；
   而同样的命令从控制通道发就正常，因为那是在后台线程排队、跨帧执行）。
   修：给门面加**非阻塞**的 `UiQueueClick` / `UiQueueClickAt` —— 它们把点击排进 CM-1 会话
   （一步一帧、按下与抬起天然分开、做完自己收尾），回放路径改走它们。

**取名规则（顺手修的）**：包名要能是中文（用户要的「进入游戏」），但 `manifest.id` 是机器键
（引用/去重/迁移靠它）只能 `[A-Za-z0-9._-]`。现在 `SanitizeName` 允许 Unicode 字母数字
（仍然挡 `/ \ : * ? " < > |` 与控制字符），`AsciiId` 负责把中文名压成合法 id
（全中文时退回 `action_<时间戳>`）。实测产物：文件名 `进入游戏.scatpak`、
`name=进入游戏`、`id=action_20260912_000415`、717 帧 / 2.23 s、3 条 `ui.click`、0 error。

**实机验证**（`py -3 Mod/Packages/record_enter_game.py replay 进入游戏`，从主菜单开始）：

```
play: playing 进入游戏.scatpak … uiEvents=3
   t=  1.0s screen=GameLoading worldLoaded=False replay=281/723 playing
   t=  2.0s screen=Game        worldLoaded=True  replay=547/723 playing
== 回放把游戏带进了世界 ==
```

顺带把"这条包里有几条 UI 事件"写进了事件日志（`[action-play] … uiEvents=3`）：
以后要区分"包里没有菜单操作"和"点击被拒绝了"，看一眼就知道。

### 16.7 焦点：后台/失焦的游戏不该被调到前台（用户实测反馈）

**用户反馈**：①游戏被别的应用盖住、"好像还没失焦"时键鼠仍然进游戏，要按 Win 才真的失焦；
②执行动作包回放时，**有时**会把失焦的游戏拉到前台。要求：后台/失焦的游戏不要被自动调到前台。

**先测量，再改**（`py -3 Mod/Packages/check_focus_stealing.py` / `check_focus_guard.py`）：
脚本自己开一个真窗口并 `SetForegroundWindow` 把它放到前台，然后在前台不属于游戏的情况下
触发注入与回放，盯四件事：前台窗口句柄、物理光标坐标、游戏是否仍被打动、`focus.status` 怎么自述。
结论（本环境）：

| 场景 | 前台 | 物理光标 | 游戏被打动 | 结论 |
|---|---|---|---|---|
| 后台进世界（点 Play→选世界→Play!） | 不变 | 不动 | 是 | 没抢焦点 |
| 后台回放按键/视角动作包（`sample_walk`） | 不变 | 不动 | 是（keys=[A,W]，位置变化） | 没抢焦点 |
| 后台软光标 UI 点击 | 不变 | 不动 | 是 | 没抢焦点 |

也就是说：**我们的注入路径没有调用任何"激活/置顶窗口"的 API**（代码里也确实没有
`Activate/SetForegroundWindow/ShowWindow`），复现不出用户说的"被拉到前台"。
但用户遇到的是真事，所以这一轮做了三件能落地的事：

1. **焦点判据换成 OS 前台窗口**（`realFocusSource = "foregroundWindow"`）：
   之前用 OpenTK 的 `GameWindow.Focused`，实测会滞后/不准 —— 用户说的"被盖住但还没失焦"
   就是这么来的。读 `GetForegroundWindow()` 再**按进程 id**判断（不按句柄：实测
   `WindowInfo.Handle` 与真正在用的顶层句柄对不上，判据会整个反过来），
   非 Windows 自动退回 `GameWindow.Focused`。`focus.status` 现在报
   `foregroundIsGame / foregroundTitle / lastForeignForeground`。
2. **光标守卫**：失焦（含被盖住）时**绝不隐藏/抓取系统光标**。
   以前只有"脱离模式"才覆盖 `Mouse.IsMouseVisible`；现在只要 `IsDetached || !realFocus`
   都在帧末把它压回 `true`。用户"游戏在后台却还能控制我的键鼠"里最难受的那种
   "鼠标被游戏吃掉"，就是这条挡掉的。`focus.status.cursorGuarded` 报出当前是否在守。
3. **抢焦点看门狗 + 明确的不作为**：记录"用户本来在别的应用、前台却被换成游戏"这件事
   （`foregroundStealCount` / `foregroundStealNote`，同时写 Game.log），并注明当时有没有注入在飞 ——
   下次真出现时，日志能直接说清"是不是我们干的"。看门狗**只记录、不动窗口**：
   我们绝不自己去抢焦点，也不替游戏抢回来。
   实测踩到两次假警报都已修：探针窗口自己关掉（前台自然轮到游戏）、以及游戏刚启动时
   还没见过任何别的窗口 —— 现在要求"确实见过一个**仍然活着**的别的窗口"才算。

**验证**：`check_focus_guard.py` 全绿 —— 后台进世界/后台回放前台不变、光标 0 位移、
游戏照旧被打动、`realFocus=false + foregroundIsGame=false + cursorGuarded=true`，
切回前台后判据反过来（`realFocus=true`），并且 `foregroundStealCount=0`。
其余回归：无头自检 438/438、编辑器 41/41×2、只读命令 27/27、动作包回放与录制→回放全绿、CM-1 探针 11/11。

> **状态：这一条暂时搁置（用户 2026-09-12 决定）。** 用户实测仍会偶发"游戏自己把前台抢回来"
> （看门狗 08:21:06 抓到一条：前台是资源管理器时游戏抢回前台，当时无按键按住），
> 复现条件与成因未定，用户的处理方式是**重启游戏**找回焦点，因此不再继续追。
> 代码里保留三样东西（都不带副作用）：OS 前台判据、失焦时的光标守卫、以及只记录不动窗口的
> 抢焦点看门狗 —— 下次真出现时，`focus.status.foregroundStealNote` 与 `Logs/Game.log` 里那条
> `[CmdBridge]` 足以继续定位。



