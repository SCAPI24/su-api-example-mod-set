# Laya × 动作原语层 × 玩家 AI：方案（提案稿）

> **状态：提案，未动任何现有代码。** 本文只回答"需要哪些技巧、怎么拼起来"。
> 用户明确批准后按 §9 的清单动手，一次一件事。
>
> 前置阅读：`doc/cmd-bridge-plan.md`（观察/注入/事件环/操作池）、`doc/player-ai-plan.md`
> （行为树 / 动作包录制回放 / `ai.*` 控制面）、`CmdBridgeMod/README.md`（铁律与配方）。

---

## 0. 结论摘要

一句话：**把"动作"从"录下来的输入回放"升级成"可参数化的原语脚本"，把"决策"从"逐帧 LLM/行为树"升级成"快状态摘要 + 一次 Laya 判定"**，
LLM 退到链外做编排/生成/复盘。

| # | 结论 | 为什么 |
|---|---|---|
| C1 | 现在只有**成品动作**（`.scatpak` 录制回放），没有**积木**。要补一层 **action verb（动作原语）+ 脚本序列**，它和录制回放**共用同一个执行器**（`IAiActuator`） | 录一条 = 一个文件 + 绑坐标 + 漂移判定；"挖一下 / 走 1.5 秒 / 点背包第 3 格"这种动态拼装不可能靠录制（`player-ai-plan.md` §6.3、§6.2.2 的漂移问题就是症状） |
| C2 | 动作原语**不落盘也能跑**：运行时把 verb 编译成逐帧输入流 → 复用 `ScatPlayer` 的节奏模型与 `IAiActuator` 注入路径 | 一份执行路径、一份白名单纪律；同时"想存就存成 `.scatpak`"（现有 `ScatTrack.ToBytes` 直接能用） |
| C3 | Laya 走 **HTTP 直连**，不需要 DSH 在链路里：`POST http://127.0.0.1:8770/v1/systemone`，Body `{model, state, questions}` | 实测本机 LayaApp 0.2.1 正在跑（8770 面板 + 8080 对外 API + 52120 llama-server），`/api/usage` 可读；DSH 插件只是它的另一个客户端，插件里的"会话开关"只管 `systemone_decide` 工具，**不影响 HTTP 端点** |
| C4 | 状态喂给 Laya 必须是**预消化的短摘要**，并**硬预算 ≤200 字符**；`noul` 要写成**两个选项的 `choice`** | 实测（§5.4）：同一事实换成长摘要后答案**直接翻转**（`mine` → `sleep`），延迟从 428 ms 涨到 1603 ms；而"是非题写成 2 选项 choice"实测完全稳定且正确（`noul` 版本答错） |
| C4b | **不要用 `confidence` 做判断** | 实测：3 个 gate 在 105 字符摘要下 `confidence`=0.71/0.67/0.96，摘要变长后全部变成 **1.0** —— 它随上下文长度饱和，不是"有多确定" |
| C4c | 摘要**只留驱动判定的原语**，不要顺手 dump 完整状态 | 实测：加 1000 字符无关字段后，`choice` 答案从 `mine` 翻成 `sleep`（0 示例外）；连问是 `noul` 还是 2 项 choice 都能改变对错 |
| C5 | Laya 只输出**离散目标 / 开关判定**（4~10 选一、是否题），不输出连续数值（距离、角度、时长） | 官方实测精度表：4 选项 9/10、8 选项 7/10、12 选项 5/10；`score` 最不稳（同一问题 1.315 vs 0.94） |
| C6 | 决策**异步、有节奏**：事件驱动 + 200~500 ms 心跳，最多 1 个在途请求，帧首应用结果；超时/失败**降级但不失控**（保持或释放输入） | 本机实测单次判定 `total_ms≈151`（3 问、199 input token），约等于 60 FPS 的 9 帧；游戏线程绝不能被 HTTP 阻塞 |
| C7 | 三层分工：**Laya = 快速决策（目标层），行为树/Skill = 执行（动作层），LLM = 慢思考（编排/生成/复盘，链外）** | 用户判断正确：Laya 比 LLM 快两个数量级；LLM 的价值在"写脚本 / 改知识 / 复盘日志"，不在闭环里 |
| C8 | **`Task.LayaAsk` 节点是 Laya 的主形态**：入树 = 用连线编排；答案写进**黑板**；分支交给现有 `Blackboard` 装饰器 | 节点类型/形状/属性表是单一事实来源（`BtNodeRegistry` → `/api/schema`），注册完**物料区自动出现**，前端几乎不用改；黑板是树内唯一的数据通道 |
| C9 | **"Laya 控制行为树"分三档，按侵入度递进**：①黑板写值（默认）②Laya 选子树/脚本（`Task.LayaSelect`）③整树切换（受控开关） | ①零风险且够用（选择器/装饰器立刻按值分流）；②是"Laya 参与调度"的主力；③等价于运行时换程序，只在明确需要时开 |
| C10 | Laya 参与决策的**判据只能是"离散、代码算得出档位"的东西**：地图也要先量化成档位（§4.5） | 实测该模型擅长"文中明确写出的信息"，对隐含推断接近随机（`D:\Laya\laya-run\doc\limitations.md`）。原始方块数组喂进去等于让它自己算，必错 |
| C11 | 游戏内 HTTP 与 DSH 工具**不冲突**：同一端点、同一模型，只是"谁在等答案"不同 | 游戏帧等 ~150 ms；LLM 回合等工具返回。用户在 DSH 里调 Laya 与 mod 调 Laya 是两条并行客户端 |

---

## 1. 现状盘点（只读核对过的事实）

| 层 | 已有的东西 | 位置 |
|---|---|---|
| 观察（只读，无限） | `obs.ui` / `obs.player` / `obs.input` / `obs.aim` / `obs.inventory` / `obs.world.*` / `obs.events`（增量事件环）/ `obs.waitFor`（条件等待） | `CmdBridgeMod/Server/Observers/*`、`CommandRouter.cs` |
| 动作（只经玩家控制器） | `act.look/lookdelta/lookat/key/hold/chord/mouse/wheel/uiclick/text/releaseAll`；CM-1 虚拟 UI 鼠标会话；CM-2 焦点/共控 | `CmdBridgeMod/Server/InputInjector.cs`、`CmdBridgeInput.cs`（对外门面） |
| 铁律 | 只写输入层白名单字段（`InputWhitelist.All`，16 个成员）；写入排在**下一帧帧首** | `CmdBridgeMod/Server/InputWhitelist.cs` |
| 决策（模式层） | `AiMode` / `AiModeGraph` / `AiStateMachine`：未接管 / 待机 / 运行树 / 录制中 / 暂停 / 故障 | `PlayerAiMod/Core/AiMode*.cs`、`Fsm/*` |
| 决策（行为树） | UE 风格 BT：`Sequence/Selector/SimpleParallel` + 装饰器/服务 + 黑板 + 树库毫秒级切换（实测 0.01~0.02 ms） | `PlayerAiMod/Bt/*` |
| 动作（成品） | `.scatpak` 录制（PgUp/PgDn 或 `ai.record.*`）→ `Task.PlayActionPackage`（`Sequence/Parallel/RandomOne/RaceFirstSuccess`）→ `ScatPlayer` 逐帧回放 + 关键帧漂移判定 | `PlayerAiMod/Record/*`、`Bt/BtActorTasks.cs:340+` |
| 控制面 | `ai.status/pause/enable/blackboard/tree.*/record.*/edit.*/action.*` 全部挂在 CmdBridge 通道上（`sccmd` 用） | `PlayerAiMod/Core/AiCommandSet.cs` |
| 状态读取粒度 | `PlayerObserver.Describe()` 一次给出位置/速度/视角/生命/体征/背包/输入意图/睡眠/HUD/游戏模式/等级；`UiInspector.ScreenName()` 给当前屏；事件环给增量变化 | `CmdBridgeMod/Server/Observers/PlayerObserver.cs:18` |

**缺口有四个，对应本方案的四条主线：**

1. **动作侧缺口**：verb（积木）+ 组合（脚本/序列）+ 参数化（坐标/时长/目标）+ 失败语义，
   以及"录制"与"生成"两条来源统一到同一个执行器。
2. **状态侧缺口**：没有"给判定模型的短摘要"（现在是完整 JSON，对 Laya 太大、字段名太长、
   `state_tokens` 会顶掉问题预算），也没有"问题库"（每次临时编题 → 不稳定、不可复现）。
3. **编排侧缺口**：Laya 还**不能出现在行为树里**（§4.6 给出落地路径：注册一个 Task 就够，
   但属性映射点 `TreeCompiler.ApplyProperties` 与"答案写哪些黑板键"必须一起补）。
4. **闭环侧缺口**：判定痕迹（摘要 / 题面 / 概率 / 执行结果）没有落到可复盘的地方，
   "Laya 判得对不对"无法度量 —— 这是能不能长期依赖它的前提（§8.4）。

---

## 2. 总体架构

```
┌── 慢思考（链外，秒级，可离线）──────────────────────────────────────┐
│  DSH + LLM：写动作脚本 / 生成技能 / 读日志复盘 / 改知识库（问题库+动作库）│
│  产物 = 文件（脚本 .json、技能 .aeactpak、问题库 .json）              │
└───────────────┬──────────────────────────────────────────────────────┘
                │ 推送式热重载（复用 ai.tree.notify 的思路：只认哈希）
                ▼
┌── 游戏内 PlayerAiMod（游戏线程，C#）─────────────────────────────────┐
│                                                                      │
│  ① 状态摘要  StateDigestCompiler ──► digest 字符串（≤200 字）         │
│     来源：PlayerObserver / UiInspector / WorldObserver / EventRecorder│
│                                                                      │
│  ② 问题库    QuestionBank ──► wire questions（≤12 问，选项 4~10）      │
│                                                                      │
│  ③ 判定器    LayaClient（异步 1 在途，帧首应用，超时降级）             │
│        POST /v1/systemone {model,state,questions} ──► answers         │
│                                                                      │
│  ④ 决策      两条并行入口，共用同一个 LayaClient：                    │
│      ④a 树内：Task.LayaAsk 节点（连线编排，答案 → 黑板键）            │
│      ④b 树外：AiStateLayaDecision 模式（事件驱动的自主快循环）          │
│                                                                      │
│  ⑤ 动作层    ActionScript（verb 序列）                                │
│       ActionClip（逐帧输入） ─► ActionQueuePlayer ─► IAiActuator      │
│       SkillRunner（守卫 + 效果核对 + 重试 + 看门狗）                   │
│                                                                      │
│  ⑥ 出口      CmdBridgeActuator ─► CmdBridgeInput ─► 输入层白名单      │
│      （与真人、与 .scatpak 回放**同一条**注入路径）                    │
└──────────────────────────────────────────────────────────────────────┘
                ▲ 只读观察                          │ 执行反馈（事件环）
                └───────────────────────────────────┘
```

**Laya 参与行为树调度的唯一通道是"黑板"**（不是偷偷改树）：节点把答案写进黑板 →
`Selector` + `Blackboard` 装饰器立刻按值分流 → 该跑哪个子树由既有调度逻辑决定。
好处是**树的形状与判定解耦**：改判定只需要改节点/问题库，改结构只需要连线，两者互不打架。

三种"动作来源"在 **⑤** 汇合，互不冲突：

| 来源 | 粒度 | 语义 | 用在哪 |
|---|---|---|---|
| verb 步（最小单元） | 单步 | 一次输入意图（走/转/挖一下/点一个控件） | Laya 临时调度底层动作、脚本内部 |
| verb 脚本（新） | 组合 | 参数化、可组合、带守卫与失败码 | 行为树节点、Laya 的"目标→动作"映射 |
| `.scatpak` 录制（已有） | 组合 | 与人手操作逐帧一致，可含 UI 点击 | 人教的"手感动作"（挖矿节奏、吃饭流程、进游戏全流程） |
| 单条 `act.*`（已有） | 单步 | 一步一命令，异步、慢 | 调试 / 兜底 / 外部脚本 |

---

## 3. 动作层：基础动作包（verb）与混搭

### 3.1 verb 词表（v1，全部落在 `IAiActuator` 已有能力内）

| verb | 参数 | 编译成的输入（逐帧） | 成功判据 |
|---|---|---|---|
| `wait` | `ms` | 无输入 | 计时到 |
| `move` | `dir`(f/b/l/r/fl/fr/bl/br) `ms` | `HoldKey(w/a/s/d 组合)`，帧末松开 | 计时到 |
| `turn` | `to`(yaw° , pitch°) 或 `by`(Δyaw, Δpitch) | `Look`/`LookDelta`（角度制） | 到达容差 |
| `lookAt` | `target`(坐标) | `LookAt(x,y,z)` | 准星命中该格/实体 |
| `hotbar` | `slot`(1~8) | `PulseKey(数字键)` | `obs.player.activeSlot == slot` |
| `jump` | — | `PulseKey(Space)` | 离地 |
| `sneak` | `on`(bool) | `PulseKey` 潜行键（开关语义） | 状态翻转 |
| `dig` | `ms` `target?` | `MouseAction(left, down)` → 保持 → `up` | 事件 `world.dig` / 方块变化 |
| `interact` | `target?` | `MouseAction(right, click)` | 事件 `world.interact` |
| `attack` | `target` | `lookAt` + 左键脉冲 | 事件 `player.damaged`（对方）/ 命中 |
| `aim` | `target` | `MouseAction(right, down/up)` 开关 | 状态翻转 |
| `drop` | — | `PulseKey(丢弃键)` | 背包变化 |
| `ui` | `click <selector>` / `text <s>` / `wheel <n>` | `UiClick` / `TypeText` / `Wheel` | `ui.click`/`ui.tap` 事件 |
| `sleep` | — | 复合：`ui c` → `waitFor SleepButton` → `ui SleepButton` | 事件 `player.sleepStarted` |
| `eat` | `slot?` | 复合：`hotbar` → 松手状态 → 右键 | 食物值上升 / 事件 |
| `flee` | `ms` | 复合：`turn` 背向威胁 → `move f` 持续 | 计时到 / 威胁距离拉开 |

**铁律承接**：每个 verb 只做"人能做的输入组合"，不写生命/背包/方块/位置/时间（
`cmd-bridge-plan.md` §1.2）。verb 表里**不允许出现**任何"结果断言式"的强制修正（例如瞬移、直接给物品）。

### 3.2 动作脚本（混搭的载体）

```json
{
  "format": "aea", "version": 1,
  "id": "mine_stone_once",
  "guards": ["alive", "world.loaded", "modal.none"],
  "steps": [
    { "verb": "hotbar", "slot": 1 },
    { "verb": "lookAt", "target": "aim" },
    { "verb": "dig",    "ms": 900 },
    { "verb": "wait",   "ms": 120 }
  ],
  "onFail": "abort"
}
```

- **控制流**（v1 只做够用的）：`step` 默认顺序执行；`repeat: n`；
  `branch: { cond, then, else }`，`cond` 用**已在 `obs.waitFor` 里验证过的条件词汇**
  （`player.alive` / `modal.is:X` / `element.clickable:Y` / `world.loaded` …）——不新造一套条件语言。
- **参数化**：`target` 支持 `aim`（当前准星目标）、`self`、`p:<slot>`、`list:<Name>@<text>`、
  `x,y,z`。坐标一律由**执行那一刻**的观察层现解析（沿用"语义目标优先、坐标兜底"的既有决定）。
- **两级来源**：Laya 只选"用哪条脚本 + 哪几个参数"（枚举值），不写坐标、不写时长。
- **失败语义**：每个 step 有 `timeoutMs`；失败上报
  `reason`（用稳定错误码：`no_target` / `blocked` / `ui_not_clickable` / `inventory_changed` / `timeout` / `injected_false`），
  交回上层的 `onFail`（`abort` / `retry:N` / `next`）。

### 3.3 执行内核（一个 player，三个来源共用）

```
ActionClip   : 逐帧输入 (keysHeld, keysPressed, mouse, wheel, lookDelta, dtMs)
   ├── ScriptClip : verb + 参数 → 编译成帧（运行时，不落盘）
   └── TrackClip  : .scatpak 的 tracks/input.bin → 直接映射
ActionQueuePlayer : 按 dt 节奏喂给 IAiActuator；支持 叠加 / 中断 / 抢占
SkillRunner       : 守卫 + 超时看门狗 + 效果核对 + 失败重试
```

要点（都是踩过的坑，别重新踩）：

1. **保持录制回放的节奏模型**：每帧带自己的 `dt`，帧率解耦（`ScatTrack` 已如此）。
2. **合成 vs 原始输入**：优先复用 `IAiActuator` 的**语义动作**（`LookAt`/`UiClick`/`Dig`），
   只在语义动作不存在时才退到原始按键。原因见 `player-ai-plan.md` §6.2.1：
   写 `PlayerInput` 会被同帧推导覆盖，只有原始输入通道/执行器门面才稳定。
3. **"一帧一次写"纪律**：按住类保持、按下类只按一次；结束路径**必须** `ReleaseAll()`。
4. **中断优先级**：`Laya 决策 > 技能内部重试 > 低优先级叠加`；打断要落在帧边界
   （与树切换/热重载同一套原子替换思路）。
5. **看门狗**：每个 step 有硬超时；超时 → 释放该 step 的输入 → 上报。
   （`JumpAssist`/`UiMouseSession` 这类"跨帧会话"必须有显式收尾，历史上漏收尾会残留输入。）

### 3.4 录制 → 积木（把"人教的"变成"可混搭的"）

- 新命令 `ai.action.export <录制名> <脚本名>`：把一条 `.scatpak` 的帧流**归纳**成 verb 序列草稿
  （连续 `KeysHeld` 段 → `move`；`LookDelta` 累计 → `turn`；左键保持 → `dig`；`ui.click` 事件 → `ui`）。
- 归纳结果**不保证等价**（人的操作里有噪声与地形耦合），所以：导出后必须能在编辑器里改参数，
  并可用 `--dry-run` 预览"这条脚本会发出哪些输入"。
- 反向也保留：`ai.action.record` 照旧；脚本可 `ai.action.bake` 成 `.scatpak` 归档。

### 3.5 编辑器（玩家 AI 编辑器扩展）

物料区加一类「动作脚本 / verb」；属性区用 schema 驱动参数表单（离线，无网）。
校验**不自己实现**：调 `ai.action.script.validate`（游戏侧同一份 C# 校验器），与 `player-ai-plan.md` §7.3 一致。

---

## 4. 状态层：界面状态 + 角色状态如何变成 Laya 的输入

### 4.1 两级门（先粗后细，这是精度要求，不是性能优化）

Laya 的 `choice` 精度随选项数快速衰减（4→9/10，12→5/10）。所以：

```
第一级：screen_kind（4~6 选一）
   world（世界内）/ modal.inventory（背包含创造/箱子/熔炉）/
   modal.character（衣物+体征+睡觉）/ dialog（对话框/命名框/提示）/
   menu（主菜单/选世界/加载/设置）/ dead（死亡/复活）

第二级：按第一级选一个**问题子集**（各 4~8 问），共 ≤12 问一次往返
```

- `screen_kind` 绝大部分由**代码直接得出**（`ScreenName()`、`hud.modalPanel`、`dialogs`），
  不必问 Laya —— 只在"模糊态"（模态类型不在白名单、屏名未知）时才让 Laya 判。
- 这天然满足"**界面就是界面状态**"：界面/模态是**状态机的一个维度**，不是"另一套系统"。

### 4.2 状态摘要（digest）格式

一行键值，全部用**离散枚举**，目标 ≤ 200 字符：

```
scr=world modal=- day=2333 hour=14.2 night=0 hp=72(med) food=31(low) stam=0.8
temp=cold wet=0 sleep=0 aim=stone@2.1 hotbar=1:stone_pickaxe,2:torch
near=zombie@6/2,sheep@14/0 ev=player.damaged,ui.message
act=mine_stone_once:dig@62% last=dig_lost_target
```

- 数值一律**带档位标签**（`hp=72(med)`）：实测表明该模型擅长"文中有明确信息"的判断，
  档位标签就是我们要它判的东西。
- `ev=` 只带**自上一条判定以来**的事件（事件环按 `sinceSeq` 增量取），避免状态膨胀。
- `act=` 是"当前动作 + 进度 + 上一次失败码"，让 Laya 能判"继续/换目标/放弃"。

### 4.3 问题库（关键产物，必须固化）

固定 12~16 个问题模板，**选项与描述固定**，随包分发（`PlayerAi/Questions/*.json`），
LLM/人改的是这个文件，不是每次现编。样例：

| id | 类型 | 说明 | 选项（带 description） |
|---|---|---|---|
| `screen_kind` | choice | 兜底判界面 | world / inventory / character / dialog / menu / dead |
| `goal` | choice | **下一步做什么** | mine / gather / craft / eat / sleep / fight / flee / explore / open_ui / wait |
| `threat` | choice(2) | 是否处于危险中 | yes=受伤或敌意实体近且朝向自己 / no=没有威胁在近处 |
| `need_heal` | choice(2) | 是否急需恢复 | yes / no |
| `can_reach` | choice(2) | 目标是否可达 | yes=瞄准目标在触及范围内 / no |
| `should_flee` | choice(3) | 逃 / 打 / 不动 | flee / fight / hold |
| `blocked_by` | choice(5) | 若上一步失败 | soft_block / wrong_tool / no_item / ui_gate / unknown |

> ⚠️ **是非题一律写成 2 选项 `choice`，不要用 `noul`**：实测同一组 gate，
> `noul` 形态答错（`must_eat` 在"food=8 饥饿"时判 0），改成 2 项 `choice` 后
> 两次重复完全一致且正确（§5.4）。`noul` 只在"真的要一个概率软信号"时才用，且不当分支条件。

**为什么固定**：Laya 的答案质量高度依赖 `instructions` 与选项描述措辞；
固定下来才能被 LLM 批量调参、被日志复盘、被 A/B 对比（同一状态同一问题，改描述看分离度）。

### 4.4 一次往返的请求形状（客户端只需拼 JSON）

```json
{ "model": "laya-multilingual-f16.gguf",
  "state": { "d": "scr=world modal=- hp=72(med) food=31(low) aim=stone@2.1 near=zombie@6/2 act=mine:dig@62%" },
  "questions": {
    "goal":   { "type": "choice", "instructions": "Next action for this character?", "criteria": { "mine": "break the targeted block", "eat": "consume food", "flee": "escape the nearby hostile" } },
    "threat": { "type": "noul",   "instructions": "Is the character in danger now?" }
  } }
```

响应：`{ model, answers: { <id>: { type, choice|noul|score, probabilities, confidence } }, usage, elapsed, cost }`。

> ⚠️ **实测约束**：`questions` 是**对象**（字典），必须有 `instructions`；
> `choice` 的 `criteria` 是对象且 ≥2 项，`score` 是数组且 ≥2 项，`noul` 的 `criteria` 可省。
> 不满足会被服务端以明确中文错误拒绝（`internal/api/api.go:485`），不要靠猜。

**预算真实模型（读 `app/laya_engine.py:134-164` 得到，比"state+题面共享 1024"更精确）**：

```
每问一条序列（问题之间互不共享）：
[CLS] + head( "choice question: " + instructions ) + [SEP] + 选项标记… + [SEP] + state + [SEP]

head 预算 = head_max_len = 256     ← instructions 与"全部选项文本"共同占用
  选项 = <mask> + 选项文本前 48 token；选项文本超了就按比例截断（最少 4 token/项）
  head 超了 → instructions 被截断
body 预算 = max_len − head 实际长度 − 2   ← state 占用剩下的
  实测：3 问合计 input_tokens=199，state 仅 33 token（样例 state 很短）

⇒ 每个问题都必须有自己的"选项文本预算"，两点是硬约束：
  ① 选项数 × 每项文本 ≈ ≤ 200 token；中文一项描述 8~12 字比较稳，别写句子；
  ② 批得越多，每个问题的 state 余量越少（12 问 ≈ state 只剩 ~700 token 上限），
     而且**同一份 state 会被重复计算 12 次**（每问一条序列）→ 直接决定耗时与账单。
```

**实践口径（比"一批 ≤12 问"更保守）**：

| 项 | 建议值 | 说明 |
|---|---|---|
| 每批问题数（树外循环） | **3~6 问** | 12 问是"能不能发出去"的上限，不是"该不该发" |
| 每问选项数 | **2~8 个** | 与精度表一致（4→9/10、8→7/10）；是非题写 2 项 `choice` |
| 每个选项描述 | **≤ 12 个汉字**（英文 ≤ 8 词） | 选项文本与 instructions 抢同一份 256 token |
| `instructions` | **≤ 60 汉字** | 超了会被截断，等于白写 |
| `state`（摘要） | **≤ 200 字符（硬预算）** | 实测延迟：200 字 837 ms / 400 字 1603 ms / 800 字 2840 ms（§5.4） |
| 每棵树的 Laya 节点数 | **1~3 个** | 同一轮访问里每个节点都是一次往返：4 个节点 ≈ 3 秒/轮。要更多判定就**合并进一批问题**，别堆节点 |

### 4.5 地图信息参与决策（先把地图**量化成档位**，再喂给 Laya）

原则：**Laya 不做几何计算，只做判断。** 原始方块数组 / 坐标差值不是它能判的东西
（`limitations.md`：它擅长"文中明确写出的信息"，隐含推断接近随机）。所以地图要先由代码
算成**离散档位 + 标签**，再进摘要（§4.2）。现有只读能力（`WorldObserver.DescribeBlocks` /
`DescribeEntities` / `Service.Probe`）已经够算这些，不需要新写世界扫描。

| 类别 | 由代码算出的量 | 进摘要的形态 | 支撑什么判断 |
|---|---|---|---|
| 地形 | 脚下方块、前方/下方是否空气/液体/固体 | `terrain=flat/rough/water/void/cliff` | 能不能走、会不会掉、能不能睡 |
| 可达性 | `Service.Probe`（`blocked/clear`）+ 到目标格距离 | `reach=yes/no@2.1` | `can_reach`（§4.3）——**这一项最好由代码给，不要让 Laya 猜** |
| 遮挡 | 准星射线是否命中、命中面朝向 | `los=clear/blocked` | 能不能挖到/看到目标 |
| 资源线索 | 附近同类方块数 / 上次挖到的方块 / 快捷栏是否有对应工具 | `res=stone:near3 tool=yes` | `goal=mine/gather` 值不值得 |
| 危险接近 | 半径内敌意实体数 + 最近距离 + 是否朝向自己 | `near=zombie@6/2 facing=1` | `threat` / `should_flee` |
| 地标 | 已知结构（洞穴口/水面/家）相对方位与距离档 | `lm=home@NE/near` | 探索/回家/补给 |
| 光照与时间 | 是否夜晚、光照档 | `night=1 light=dark` | 该不该点灯/睡觉/继续挖 |

**两条纪律**：

1. **能量化的不进问题库**：凡代码能算出的（可达性、遮挡、能否睡），直接进摘要做**证据**，
   只在"代码算不出取舍"的地方出题（例如"资源 near3 vs 危险 6 格，去不去"）。
2. **地图信息要有预算**：摘要 ≤200 字符是硬预算，所以地图只带**与当前目标相关**的那几项
   （按 `act=` / `aim=` 选择性地展开），不做全量快照 —— 全量快照既是预算问题，也是判准问题。

### 4.6 Laya 进行为树：`Task.LayaAsk`（用户问的核心）

**结论：可以，而且这是最省事的一条路**，因为节点类型是单一事实来源，注册完编辑器物料区自动出现。

| 步骤 | 改动 | 依据 |
|---|---|---|
| ① 注册节点类型 | 在 `BtNodeRegistry.RegisterBuiltIns()` 里加 `s_nodes["Task.LayaAsk"] = new BtNodeInfo(...)`，形状 `BtNodeShape.Task`（无 children、无 services），`packageSerializable: true` | `Bt/BtNodeRegistry.cs:283` 起：节点 = 工厂 + 形状 + 属性表 |
| ② 节点类 | 新建 `Bt/BtLayaAskTask.cs`，继承 `BtTaskNode`：`NodeType => "Task.LayaAsk"`，`IsLatent => true`（跨帧等待 HTTP），`OnEnter` 发请求、`OnTick` 取结果 | 与 `Task.PlayActionPackage` / `Task.MoveTo` 同构（`Bt/BtActorTasks.cs`） |
| ③ 属性映射 | 在 `TreeCompiler.ApplyProperties` 的 `switch (node.NodeType)` 里加 `case "Task.LayaAsk":`，逐字段读 | `Package/TreeCompiler.cs:417`，这是**属性名→字段的唯一映射点** |
| ④ 输出声明 | 节点写进黑板的键必须在包 `manifest.blackboard` 里声明（`name` 1~64 字符 `[A-Za-z0-9._-]`，类型 bool/int/float/string/actor） | `Package/ScbtManifest.cs:180` 起会校验 |
| ⑤ 编辑器 | `GET /api/schema` 自动带上它 → `engine-adapter.js` 按 `shape` 分组 → 物料区出现 | `PlayerAiEditor/Server/EditorApi.cs:100`、`Web/engine-adapter.js:31`，**前端分类不用改** |

**节点属性（建议 v1）**：

```json
{ "type": "Task.LayaAsk",
  "properties": {
    "questions": "danger.json",        // 问题库文件（相对 PlayerAi/Questions/）
    "answerKeys": "goal,danger",        // 答案写到哪些黑板键（顺序与问题 id 对应）
    "template": "mine_or_run",          // 可选：问题库里的模板名（复用同一批选项描述）
    "timeoutMs": 1200,
    "refreshMs": 500,                   // 树每次重新访问时，多久内的答案可直接复用
    "onUnavailable": "fail"             // fail | default | keep  —— 见下
  } }
```

**执行语义（异步节点，必须写死这四条）**：

0. **照抄既有异步任务的做法**：`BtPlayActionPackageTask` / `BtMoveToTargetTask` 已经是
   "跨帧返回 `Running`、帧首被 tick 驱动"的实测可用模式，`IsLatent` 语义直接复用，不新造调度。
1. `OnEnter`：编译摘要 → 发 HTTP（去重见 §5.2.7）→ 返回 `Running`；**不发第二个请求**。
2. `OnTick`：结果到了 → 写黑板 → `Success`；超时 → 按 `onUnavailable` 决定 `fail/default/keep`。
3. `refreshMs` 内的同一题面（摘要哈希 + 问题库哈希相同）直接复用上次答案，**不重复请求**：
   选择器每帧重访本节点是常态，不去重会把 Laya 打死。
4. **中断**：`OnExit` 必须取消在途请求并清理，绝不让"上一个问题的答案"落到下一棵子树。

**编辑器联动（注册完就有的效果 + 必须补的两处）**：

| 项 | 现状 | 要做的事 |
|---|---|---|
| 物料区出现 | `/api/schema` 由 `BtNodeRegistry` 生成 → `engine-adapter.js` 按 `shape` 分组 | **零改动**（放在「任务」组） |
| 属性表单 | 按 `BtPropertySpec` 自动生成控件 | 大部分零改动；`questions` 想要"问题库下拉"、`answerKeys` 想要"与 manifest 黑板联动" → 各加一个控件分支（照抄 `Web/app.js:4610` 的 `packages` 勾选做法） |
| 校验 | 前端不自己实现，POST 给游戏侧同一份校验器 | 复用；新增的语义检查（§9 P3）自动生效 |
| 实时判定面板（增强） | 现有 `ai.tree.snapshot` 高亮活动分支 | 加一栏显示"本节点发出的题面 + 概率分布 + 实际走了哪条连线"（数据来自 §8.4 的留痕） |

**能做什么 / 不能做什么（边界要写清）**：

| Laya 在树里可以做 | 不能做 |
|---|---|
| 判 `danger` / `can_reach` / `has_tool` / `screen_kind`，让装饰器与选择器分流（连线决定结构） | 不能自己改树结构、不能跳级、不能写非声明过的黑板键 |
| 选"用哪条脚本 / 哪个子树入口"（`Task.LayaSelect`，§6.3） | 不能输出坐标/时长/角度（那是动作层的参数） |
| 通过黑板把结论交给后续节点（例如 `goal` → `Task.RunActionScript`） | 不能把"等待中的 Running"当成"成功"（未答 ≠ 可以继续） |

### 4.7 答案 → 黑板：类型兼容与越界语义（对应 G3）

**先看服务端真实返回形状**（用户给的 curl 形态，本机实测同形）：

```bash
curl -sS http://127.0.0.1:8770/v1/systemone \
  -H "Authorization: Bearer <key>" -H "Content-Type: application/json" \
  -d '{"state":{"body":"要判定的内容"},"model":"laya-multilingual-f16.gguf",
       "questions":{"dept":{"type":"choice","instructions":"该由哪个团队负责？",
                            "criteria":{"billing":"发票、扣费、退款","technical":"软件故障和缺陷"}}}}'
```

```jsonc
{ "model": "...", "usage": {...}, "elapsed": 151, "cost": 0.000008,
  "answers": {
    "dept":   { "type": "choice", "choice": "billing",
                "probabilities": { "billing": 0.9943, "technical": 0.0038 },
                "confidence": 0.9645, "action": { "action_probability": 0.61 } } } }
```

⇒ **每个答案自带 `type`（实测：`choice` / `noul` / `score`）**，与问题声明的类型一致；
另有 `probabilities`（按选项 key）、`confidence`（**不可用，见 §5.4 ③**）、`action.action_probability`。

**兼容矩阵（问题库类型 ↔ 黑板类型 ↔ 可用性）**：

| 问题类型 | 可用的答案字段 | 建议黑板类型 | 兼容性结论 |
|---|---|---|---|
| `choice` | `choice`（就是选项 **key**）、`probabilities` | `string` | ✅ 完全兼容；**key 是稳定 id**，描述可改、key 不可改 |
| `score` | `score`（期望等级，浮点） | `float` | ⚠️ 兼容但只作**软信号**；**不要**用 `bool`/`int` 接（模型侧最不稳） |
| `noul` | `noul`（0~1） | `float`（或 `bool`，阈值自己定） | ⚠️ 能做"概率"用，但**实测做 2 项 `choice` 更准**（§5.4 ④）→ 分支条件一律用 `choice` |
| —（服务端附赠） | `confidence` | 不写黑板 | ❌ 明确禁止：随上下文长度饱和，不能当"可信度" |
| —（服务端附赠） | `action.action_probability` | 不写黑板 | ❌ 语义未定义，不接 |
| 模型侧无法产出 | —— | `actor` | ❌ **模型永远给不出 `actor`**：实体引用必须由 C# 的确定性查找补（§4.9 / G2） |

**绑定语法（节点属性 `answerKeys`）** —— 用四元组把"声明"写死，编译期就能报错：

```
answerKeys = [
  "goal:goal:str",        // 黑板键 : 问题 id : 期望类型
  "danger:in_danger:bool",
  "urgency:urgent:float"
]
```

**越界与兼容规则（写进 `TreeCompiler` + `PackageValidator`，都在编译期）**：

| 情形 | 处理 |
|---|---|
| `answers` 缺该问题 id，或 `type` 与问题声明不符 | **本节点失败**（`fail`），绝不"猜一个值"写进黑板 |
| 答案值无法转成声明的黑板类型（如 `choice` key 不在 `criteria` 里） | 同上失败，并记一条明确错误码（`laya_answer_type_mismatch`） |
| 选项 **key 改名/删除** 但树里 `answerKeys` 仍引用 | **编译期报错**（可选 key 清单来自问题库，做闭集校验）——这正是"别等到运行时才炸" |
| 问题库文件不存在 / 问题 id 不在库里 | 编译期报错（G4） |
| `answerKeys` 里的黑板键没在 `manifest.blackboard` 声明 | 沿用现有黑板校验 → 报错 |
| 一个节点写多个键（多问一批） | 允许；**任一问失败即整节点失败**（不做"写一半"） |

### 4.8 行为树池（pool）与执行调度：用原行为树包补足"策略时间尺度"

**用户的原则**：Laya 只管策略级判断 → **策略之间的"无缝流程"由原行为树包承担**；
Laya 的决定不直接产生动作，而是**调整池的执行调度顺序**。这一节就是把它落成机制。

**池是什么（现有能力，不新造）**：`TreeLibrary` 就是池 —— **预编译常驻**（默认容量 8，LRU 淘汰）、
哈希比对后**指针交换**（`Switch` 实测 `switchMs` 0.01~0.02 ms）、切换只在 **tick 边界**。
所以"池"不是新容器，而是"把 `PlayerAi/BehaviorTrees/` 里那批包**提前编译常驻**"。

```
池（≤8 个常驻包 .scbtpak）
   │  调度器：定序 / 选下一个 / 失败回退（Laya 可改的是"顺序"，不是"内部"）
   ▼
活动树（同一时刻一棵）＝ 无缝流程的载体（Sequence + 装饰器 + 服务 + 黑板）
   │
   ▼
叶子：Task.RunActionScript / Task.PlayActionPackage / Task.LayaAsk / 传感器守卫
```

**无缝性靠三条不变量，不靠"迁移运行中的任务"**（这一条我核过源码，必须写清）：

| # | 不变量 | 源码依据 / 理由 |
|---|---|---|
| S1 | **切换只发生在"步骤边界"**：活动树 Success、或主动让出（进入 `Wait` 类叶子 / 树内 `Yield`） | `TreeLibrary.Switch` 调 `host.Tree.ReplaceRoot(prepared.Compiled.Root, **migrate:false**, …)`（`TreeLibrary.cs:354-355`）→ **不迁移**，因此绝不能在挖矿/移动中途换树，否则那一步被丢弃 |
| S2 | **切换前先 `ResetSubtreeState()`**，常驻树不带上次的运行态 | `TreeLibrary.cs:354`；且库文档明确"第二次切回同一棵树会继承上次跑到哪"是一个必须避免的坑（`TreeLibrary.cs:117-121`） |
| S3 | **`Task.PoolCall` 切换后保证"释放再接管"**：切换瞬间释放旧树持有的输入，新树第一帧重新按自己的意愿按 | `PlayerAiRuntime.ReleaseAll()` + 帧首注入语义；不释放就会出现"上一棵树的 W 还按着" |

**一个必须补的前置条件（否则热点重载会咬人）**：`BtMigration` 按**节点 id** 保留运行态
（`BtRuntime.ReplaceRoot` → `BtMigration.Key(node)`），所以同一棵树**热重载/换版本**时，
"希望你正在跑的那一步别被中断"的那些叶子**必须保持节点 id 稳定**。这是一条**包作者纪律**，
要写进校验器提示与编辑器的"改名会中断运行态"警告里。
（注意与 S1 区分：跨树切换不迁移 —— 这是有意的，见下。）

**为什么跨树不迁移是对的**：一次切换 = "换一整套流程"。若强行迁移运行中的任务，
最容易出现"半条腿在旧流程、半条腿在新流程"的状态（挖到一半的方块、开了一半的面板）。
所以设计成：**要切换 → 走到步骤边界 → 干净的树从头跑**；而**大多数技能都是幂等的**
（靠近、吃、走、看），重跑开头几步的代价远小于状态错位。

**调度语义（Laya 调整"顺序"的确切含义）**：

| 术语 | 定义 | 谁改 |
|---|---|---|
| 池顺序 `pool.sequence` | 有序的包名列表：**"没人喊停时，接下来按这个顺序跑"** | Laya（枚举）/ 人 / 默认文件 |
| 立刻插队 `pool.next` | 只影响"下一个"，不打断当前 | Laya（枚举） |
| 抢占 | **默认关闭**：只有活动树到步骤边界才让位（S1） | 树自己决定（`Yield`），不是 Laya 说抢就抢 |
| 失败回退 `pool.fallback` | 活动树 Failed → 按顺序取下一个；全失败 → 安全态（释放输入 + `wait`） | 确定性规则 |
| 周期重入 | 活动树 Success 后是否重跑同一棵 / 取下一个 | 池顺序 |

**接口（v1）**：

| 形态 | 名字 | 语义 |
|---|---|---|
| 树内任务 | `Task.PoolCall packages="a,b" mode=Sequence\|RandomOne\|RaceFirstSuccess` | 依次/随机/竞速跑池里的包（与 `Task.PlayActionPackage` 的 `mode` 词汇一致，不新造枚举） |
| 树内任务 | `Task.PoolSetOrder keys="b,a"` | 改池顺序（写黑板键 `pool.sequence` 更常见，见下） |
| 黑板约定 | `pool.sequence` / `pool.next` / `pool.fallback` | Laya 的 `Task.LayaAsk` **只写这三个键**就能"调整调度顺序"，不需要新节点、不需要执行树操作 |
| 树外（Laya 模式） | `ai.pool.list/status/prepare/use` | 人/LLM 用；`prepare` 把池一次性常驻（把编译成本提前付掉） |

> **为什么优先用黑板而不是"Laya 直接执行树操作"**：黑板写值仍是"声明"，
> 调度器读值决定下一步；这样 Laya 永远不直接改树（可审计、可回放、出错只会走错分支而不是崩树）。
> 只有 `Task.PoolCall` 这种"显式入树"的操作才由包作者连线决定。

**Laya 决定 → 无缝流程 的完整时序**（这是用户要的"补足"）：

```
活动树 A 正在跑：Sequence[ 走到矿点 → Task.RunActionScript(mine_stone) → Task.LayaAsk(goal) ]
  ├─ 前两步是**原行为树包的无缝流程**（不需要 Laya，毫秒级，帧级响应）
  ├─ 到 Task.LayaAsk：写 pool.next = "combat.scbtpak"（枚举），节点 Success（μs 级）
  └─ 树 A 继续跑它的收尾步骤 → 到步骤边界（Yield/Wait）→ 调度器按 pool.next 切到树 B
                        切换耗时 0.01~0.02 ms，且 S3 保证输入不残留
```

### 4.9 非枚举参数：Laya 给枚举，C# 给具体值（对应 G2）

Laya 的输出永远是**枚举/分类**；"具体是哪一个"由 C# 用**确定性规则**在游戏里查：

| Laya 给的枚举 | C# 的确定性取值规则（全部只读，不需要新能力） | 落成动作参数 |
|---|---|---|
| `goal=mine` / `target=block` | 取**当前准星命中的方块格**（`obs.aim` → cell）；没有就取视线前方第一格（`Service.Probe`） | `Task.RunActionScript(target=aim)` |
| `slot=1..6`（枚举槽位） | 槽位序号 → **实际快捷栏索引**（空格子跳过；工具类优先规则）| `verb hotbar(slot)` |
| `eat=first_food` | 快捷栏/背包里**第一个可食用物品**（食物值表，从 `obs.inventory` 查） | `verb eat(slot=…)` |
| `target=entity`（`near=zombie@6/2`） | **最近**且朝向自己的那一个（事件环已有 `near` 列表，按距离排序） | `verb attack(target=…)` / `lookAt` |
| `count=n`（`few`/`some`/`all`） | 映射成固定数量档（1 / 3 / 全部可拿），**上限由包作者写死** | `verb dig(repeat)` / `gather` |
| `ui=gather_screen` | 语义选择器（`list:XxxList@名字` / `element:Play`），由 CmdBridge 的 UI 服务**点击那刻现解析** | `verb ui(click …)` |

三条纪律：

1. **模型永远看不到也不需要坐标**；它给的是"哪一类/第几档"，C# 给"具体那一个"。
2. 确定性规则必须**可复现**（同一状态同一输入 → 同一目标），否则"回放/复盘"对不上账。
3. 规则本身用**现有只读观察**实现（`obs.aim` / `obs.inventory` / `obs.world.*` / `Service.Probe`），
   **不新增任何写入口**，也不新增游戏侧 API 面。

### 4.10 Laya 能"控制行为树"到什么程度（三档，按侵入度递进）

| 档 | 手段 | 侵入度 | 何时用 |
|---|---|---|---|
| ① 写黑板 | `Task.LayaAsk` → 黑板键 → `Blackboard` 装饰器 / `Selector` | 零：树结构不变 | **默认**。绝大多数"让 Laya 参与调度"用这一档就够 |
| ② 选题 | `Task.LayaSelect`：答案是一个**枚举**（脚本名 / 子树入口 / 目标类），由节点去 `Library` 取对应项并运行 | 低：只是"运行哪一个"由 Laya 定 | Laya 要当"小调度器"时（例如同一棵树下按局势选 `attack`/`retreat` 脚本） |
| ③ 换树 | 复用既有 `TreeLibrary` 的毫秒级切换能力，答案 → 目标包名 → 帧边界原子替换 | 高：等于运行时换程序 | 只在明确需要"整套策略切换"时开，且**默认关闭**（要单独的开关与日志） |

③ 的既有依据：`TreeLibrary` 预编译常驻 + `ai.tree.switch` 实测 `switchMs≈0.01~0.02 ms`，
替换只发生在 **tick 边界**（`player-ai-plan.md` §3.6/§5.3）。**本方案不新造替换机制**，
只是把"谁来决定切哪棵"从人/命令换成 Laya 的一个枚举答案。

> **与 §4.8 的关系**：③ 是"Laya 直接点包名"，§4.8 的 `pool.next/sequence` 是"Laya 只调顺序、由调度器在步骤边界切"。
> **优先用 §4.8**：它是 ③ 的安全包装（同样是 `TreeLibrary.Switch`，但把"何时切"交给步骤边界），
> ③ 只在需要"立刻、无条件切"时由包作者显式使用。

### 4.11 资源库的存储模型：存盘库 / 内存库 / 临时缓存（"读档-运行-存档"）

**用户的设计（2026-09-26）**：资源（树包 / 动作包 / **问题库**）分**两套库** —— 存盘库与内存库；
先从存盘读进内存，**读进来才算加载**；内存里随便改；**默认不回写磁盘**；
中间过程用 **tmp 临时文件夹**做自动缓存；内存库可**手动保存回磁盘**，也可**丢弃**。
断电重启时：先看 tmp 缓存能不能恢复（自动存档），不能就读新的进内存、再读手动存档。

这个"像游戏读档"的模型是对的，因为它正好把 §4.5/§4.7 已经定下的几条铁律（"AI 内存可改、落盘由人"、
"缺文件拒包"）收进一个统一机制。既有地基已经够用：

| 需要的能力 | 已有实现 |
|---|---|
| 原子写（绝不半截文件） | `PackageWriter.TryWriteFile`：先写 `<path>.tmp` 再替换（`.tmp` 失败即清理） |
| 目录白名单（防穿越） | `PackageRoots`（实例根可写；已收敛为**一个**包目录 `PlayerAi/BehaviorTrees/`） |
| 内存态改写 | `TreeMutation`（改参数/插删搬），`TreeWriter`（反序列化 → 导出新包） |
| 导出成新包 | `ai.tree.export`（**只产出新包，绝不覆盖来源包**） |

**四层存储与优先级（读入顺序）**：

| 序 | 层 | 位置 | 可写 | 作用 |
|---|---|---|---|---|
| 1 | **内存库**（运行态，权威） | RAM | — | 实际跑的东西；`dirty` 标记 + 内存哈希 |
| 2 | **tmp 缓存**（自动存档） | `<实例根>/PlayerAi/.autosave/` | 游戏自己写 | 断电恢复用；**不是正式包**，不参与"可分发" |
| 3 | **手动存档**（人存档） | 用户指定路径 / `<实例根>/PlayerAi/Saves/` | 编辑器显式保存 | 可分发、可对账、可版本化 |
| 4 | **出厂发行**（只读） | `<Mods>/PlayerAiMod/PlayerAi/BehaviorTrees/` | ❌ | 默认内容，永远不被写 |

**生命周期（状态机）**：

```
        ┌─────────────── 载入 ───────────────┐
        ▼                                    │
 [磁盘/发行] ──读字节→校验→编译──► [内存库(clean)] ──编辑/LLM改──► [内存库(dirty)]
        ▲                                    │                        │
        │                          ┌─────────┴──────────┐             │
        │                          ▼                    ▼             │
        │                  [手动保存→Saves/]      [自动缓存→.autosave/]│
        │                  （显式、可对账）        （周期/退出前，静默） │
        └──────────── 丢弃（drop，回磁盘版）◄──────────────────────────┘

 启动恢复顺序：  读发行/存档 → 有 .autosave？→ 比时间戳 → 决定用缓存还是存档
```

**四条必须写死的规则（不写就会出事）**：

| # | 规则 | 理由 |
|---|---|---|
| R1 | **自动缓存永不覆盖手动存档**：只写 `.autosave/`；恢复时**比时间戳 + 内容哈希**决定用哪个，并在编辑器里显示"将用缓存（比存档新 X 分钟）/ 将用存档" | 否则"自动存档"会静默吃掉用户的手动存档（等于游戏存档被自动覆盖） |
| R2 | **缓存文件格式带版本头**：`schemaVersion + 包名 + 来源路径 + 来源哈希 + 内存哈希 + 生成时刻 + 进程 id`；版本不认 → **当成不可恢复**（宁可读存档，不猜） | 断电恢复是"猜文件"的高危场景；宁可少恢复，不可错恢复 |
| R3 | **缓存必须原子写**：直接复用 `PackageWriter.TryWriteFile` 的 `.tmp` → replace；写入失败只记日志不影响游戏 | 断电正好发生在写缓存时，不能让缓存本身变成半截 |
| R4 | **`.autosave/` 与 `*.local.*` 一样不进任何分发**：不列进包清单、不参与"可回放/可分发"统计、不进校验器的"包存在"判断 | 否则缓存会被当成正式包分发出去（跨设备会带出脏状态） |

**手动保存 / 导出 / 丢弃的语义（三者不能混）**：

| 操作 | 语义 | 落点 | 覆盖来源包？ |
|---|---|---|---|
| **手动保存**（save） | 把当前内存库写成正式包，**带备份代次**（`name.v2.scbtpak` 或保留上一代） | 用户指定 / `Saves/` | ❌ 默认不覆盖；显式 `overwrite=true` 才覆盖，且先备份 |
| **导出**（export，已有 `ai.tree.export`） | 产出**新包**，来源包字节不变 | 用户指定 | ❌ 永远不覆盖 |
| **丢弃**（drop） | 内存库回到"最近一次载入的磁盘版"，`dirty` 清掉 | — | — |
| **自动缓存**（autosave） | 静默影子副本（R1~R4） | `.autosave/` | ❌ |

**内存库的四类操作**（沿用 `TreeMutation` 的能力，不新造）：改参数 / 插节点 / 删节点 / 搬移，
外加"挂/摘动作包"与"改问题库选项"。每次改动：内存哈希重算 + `dirty=true` + 事件留痕（谁改的：人 / LLM / Laya）。

**与热重载的关系（必须给冲突规则，否则编辑器和内存库会互相踩）**：

现状是推送式热重载：编辑器保存后发 `ai.tree.notify {path, hash}`，游戏只认哈希一致的通知。
现在内存库可写，于是出现**三份哈希**：通知里的 `hashN`、磁盘当前 `hashD`、内存 `hashM`。

| 情形 | 处理 |
|---|---|
| `hashN != hashD`（对方还在写） | 忽略（现状行为不变） |
| `hashN == hashM` | 已在运行这个版本，忽略 |
| `hashN == hashD`，且内存 `clean` | 正常重载（现状行为） |
| `hashN == hashD`，但内存 `dirty` | **不静默覆盖**：记一条冲突事件 + 进入"待选择"（编辑器里选"用磁盘覆盖内存"或"把内存另存为新包"）。默认保留**内存**（运行态不被打断） |

**范围建议（v1 先做小）**：先只把**树包**做进内存库；动作包与问题库**沿用同一套抽象**但要晚一步接
（`ScatLibrary` 与问题库各自有自己的读路径，一起改会拉长 P1/P2 的验证面）。抽象层统一叫
`IAssetStore`（`Load / Memory / AutoSave / Save / Drop / Dirty / Hash`），三种资源各自实现。

### 4.12 拒包后的异常处理：失败标记 + 默认重取

**用户的要求**：问题库缺失 → 拒包；**节点引出一个失败原因标记**用于提前规划异常路径；
异常处理可以**默认连入一个 Laya 自动问题判断**；丢包**默认重取**。

**先把三种"取不到"分开（这是"重取"能不能用的前提）**：

| 失败码 | 含义 | 重取？ | 归谁管 |
|---|---|---|---|
| `pkg_write_in_progress` | 文件正在被原子替换（读到半截 / 哈希与通知不符 / 存在 `.tmp`） | ✅ **立刻重取**（短退避） | 装载器自带 |
| `pkg_missing` | 文件确实不存在（或问题库文件缺失） | ✅ **重取**（指数退避，上限 N 次，默认 3） | 异常处理节点 |
| `pkg_invalid` | 校验不过（JSON/结构/引用/类型/选项闭集） | ❌ **不重取**，直接拒包 | 拒包 + 失败标记 |
| `laya_unavailable` | Laya 服务/密钥/端点问题 | ❌ 不重取（是基础设施问题） | 走 `onUnavailable` 降级链 |

> **"丢包"与"缺文件"不是一回事**：前者往往是"正好在写"（可重取），后者可能要等到下一次
> 编辑器保存才会出现（重取有意义但要有上限）。把两者混成一个"重取"会导致**校验错误也无限重试**。

**失败标记（feed the tree, not the model）—— 这一条是纪律**：

- 拒包/重取失败时，节点除了 `Failed`，还**输出一个失败原因键**（例如 `pkg.fail = "missing:questions/combat.json"`、
  `pkg.retry = 2/3`），供树里的异常分支**提前规划**（走备用问题库 / 切到不用 Laya 的子树 / 回退到录制包）。
- **失败标记绝不进摘要、绝不交给 Laya 判定**。理由：那是**基础设施状态**，不是游戏世界的状态；
  让模型看"包加载失败"这类信息，等于让它拿环境噪声做战术决策（§8.5 Non-Goals 的延伸）。
- 失败标记的**消费方式**是连线：`Blackboard` 装饰器读 `pkg.fail` 走异常子树，或 `Selector` 兜底分支。

**默认接入的 Laya 自动问题判断（可选，但默认给一个模板）**：

```jsonc
// PlayerAi/Questions/pkg_recover.json —— 异常处理专用的固定问题库
{ "id": "pkg_recover", "questions": {
    "recover": { "type": "choice", "instructions": "A required file for the current plan is unavailable. What now?",
                 "criteria": { "retry":        "wait and try the same file again",
                               "use_backup":   "switch to the backup question bank / action set",
                               "skip_step":    "skip this step and continue the plan",
                               "abort_plan":   "stop the plan and stand by" } } } }
```

- 只在**确实有可选路径**时问 Laya；`retry` 与 `use_backup` 的**具体动作仍由 C# 确定性执行**（§4.9）。
- 自动重取的参数（次数、退避、上限）**写在一份 Laya/资源配置里**（D14 的那份），
  在行为树编辑器里有一个总开关（用户要求：**一份配置 + 编辑器里一个功能来控制**）。

**熔断（否则会转圈）**：同一资源在窗口期内重取超过上限 → 标记 `exhausted`，
停手并提示人（HUD/`ai.logs`），**不进入自动重试循环**；恢复条件明确（编辑器保存了新版本、或人手动 `ai.tree.reload`）。
这也防住"Laya 自己不可用时，自动问题判断也问不出来"的死循环：**自动问题只在 Laya 可用时走，否则直接走 C# 的确定性兜底**。

---

### 4.13 双状态机：世界内 / 世界外（+ 过渡态与交接协议）

**用户指出的关键事实**：世界外**没有角色、没有地图**，但**仍然要操作界面**（选世界、翻页、建角色、命名、确认）。
所以这不是"一个状态机加个 if"，而是**两套宿主、两套摘要、两套问题库、两套树包**。

| 维度 | 世界内（**Phase.World**） | 世界外（**Phase.Front**） |
|---|---|---|
| 宿主 | `ComponentPlayer` 上的 `PlayerAiComponent` | **runtime 级**（不依赖 ComponentPlayer）——现有实现已支持：帧首 tick 与暂停都是全局的（`PlayerAiRuntime.TickFrameStart` / `Paused`，源码注释写明"对世界外同样有效"） |
| 摘要字段 | 角色（生命/食物/体温/睡眠）+ 地图档位（§4.5）+ 瞄准/背包 + 近邻实体 + 事件环 | **界面为主**：`screen` / `modal` / `dialog` / **可见列表行（索引 + 文本 + 选中态）** / 可点元素与 `clickable` 标记 / 上一次点击结果 |
| 问题库 | `goal / threat / can_reach / …`（§4.3） | `front_goal`（new_world / play / delete / back / page / confirm / cancel）、`which_row`（枚举序号）、`dialog_action`、`typing_needed` |
| 动作 | verb（走/挖/吃/睡/打）+ 录制包 | verb 的**界面子集**（`ui.click` / `text` / `wheel`）+ `obs.waitFor` 条件等待 |
| 典型流程 | 生存技能 | **固定链条**：选世界 → 翻页 → 创建/进入 → 命名 → 确认 → 等加载（→ G20） |

**过渡态（Phase.Transition）必须是第三种**：`SwitchScreen` 期间控件树冻结、世界加载中途既不能点也不能走
（`cmd-bridge-plan.md` §22 的 `screen_busy` 与"点击前先等动画"就是这个坑）。

```
Front ──(点了 Play)──► Transition ──(world.loaded 且 GameScreen 就绪)──► World
  ▲                        │                                              │
  └──(world.unloaded)──────┴──────────(退出/死亡回主菜单)────────────────┘
```

- 过渡态**只做条件等待**（`obs.waitFor screen.animating.false / element.clickable:X / world.loaded`），
  **不做任何 Laya 判定**（问题此刻无意义，且答案回来时 phase 已经变了）。
- **交接协议**（进/出世界的瞬间，一次性执行，顺序不能乱）：
  1. `ReleaseAll()`（释放旧 phase 的所有按键/鼠标）
  2. `Pause("phase")`（暂停旧状态机；保留其状态以便返回时不重来）
  3. 清 Laya 在途请求与去重缓存（G8 的代次 +1）
  4. 切宿主与摘要编译器、加载对应 phase 的树包与问题库
  5. 新 phase 的树从根开始（`ResetSubtreeState`），`Resume`
- **phase 判定的去抖**（G15）：单一定义 + 连续 N 帧一致才算切换；判定结果进 `ai.status` 与摘要
  （否则 `world.loaded` 抖动会让两套状态机来回切，每次切换都释放输入 → 角色抽搐）。

### 4.14 共控：人、AI 与"完全暂停"的三态（对应 G8 / G13）

**用户的要求**：设一个按键**完全暂停** AI 接管；**不按的时候，允许 AI 和人抢操作**（即共控）。

现有基础（`CmdBridgeMod/Server/FocusPolicy.cs`）：**共控已经有一半** ——
`FocusMode.Follow` 每帧把键盘/鼠标合并成 `真实 ∥ AI 注入`（"AI 松手不会打断你按住的键"），
视角由 `LookOwnerMode.Auto` 按"最近真实鼠标活动"仲裁（你一动鼠标视角立刻归你）。

所以要做的是**把它显式化成三态**，而不是新造共控：

| 态 | 含义 | 输入 | Laya/树 | 触发 |
|---|---|---|---|---|
| **Paused（完全暂停）** | 停手，人全权 | 不注入；释放所有 AI 输入 | 不决策、不发请求；**保留**树/黑板状态（回来能续） | 暂停键（建议沿用已有的 `Home`，见 `PlayerAiRuntime.TogglePause`）、`ai.pause` |
| **CoControl（共控，默认）** | AI 干活，人可随时插手抢 | `真实 ∥ 注入` 合并；视角"最近真实鼠标"仲裁 | 照常决策执行 | 默认态 |
| **Standby（放弃接管）** | AI 不碰输入端，只观察 | 释放并停止注入 | 树卸载/待机；仍可读状态、仍可被叫回 | `ai.disable`、`Home` 长按（或另一个键） |

**暂停键的语义（要写死，否则又是"以为按了没按"）**：

- **完全暂停 = 不决策 + 不注入 + 取消在途请求 + 清去重缓存 + 接管代次 +1**；
  之后回来的答案**因代次不匹配一律丢弃**（这就是 G8 的落地）。
- **恢复时**：不重放暂停期间攒下的事件（事件环的 `sinceSeq` 从"当前"重新开始），
  避免"恢复后 AI 突然执行 10 秒前的决定"。
- 暂停期间**人类输入照常被记录**（录制/复盘不受影响），但 AI 不消费。
- 键位建议：`Home` = 暂停 ⇄ 继续（已有）；`ai.disable` = 放弃接管（明确回待机）；
  两者在 `ai.status` 里必须是**可区分**的字段（`paused` vs `enabled`）。

**共控时三个具体冲突的处理（G13）**：

| 冲突 | 处理 |
|---|---|
| 按键 | 沿用"真实 ∥ 注入"合并（已实现）：人的按下不会被 AI 的松开清掉，反之亦然。**同键冲突是"共赢"而不是"谁赢"** —— 谁松开谁的那部分 |
| 视角 | 沿用 `LookOwnerMode.Auto`：最近有真实鼠标活动 → 归人；人停手一段时间 → 归 AI。**AI 在人拥有视角期间不得 `LookAt`/`Look`**（否则人转一点就被每帧抢回，手感极差） |
| 动作 | 新增：**人的输入边沿事件触发"当前动作让位"** —— 例如人按下左键时 AI 正在 `dig`，AI 立即让位（释放该动作 + 短冷却），不与人抢同一个动作对象。让位规则进事件日志（便于复盘"为什么 AI 停了"） |

---

## 5. Laya 接入层（游戏侧 HTTP 客户端）

### 5.1 端点与配置（本机实测）

| 项 | 值 | 备注 |
|---|---|---|
| 对外 API | `http://127.0.0.1:8770` + `/v1/systemone`、`/v1/models` | `config.json: server.port=8770`，实测 `/api/usage` 返回 200 |
| 局域网 API | `api_host=0.0.0.0`、`api_port=8080` | 供平板/其它机器用；地址属于**私有信息**，只进 `AGENTS.local.md` 或本地配置 |
| 模型 | 已加载 `laya-multilingual-f16.gguf`（别名 `laya-latest`/`multilingual`/`jev-latest`… 一律解析到当前模型） | `model` 字段是建议值，写错不会报错 |
| 鉴权 | `Authorization: Bearer sk-…` | **任何被跟踪文件禁止出现密钥**；走 §5.5 的预填机制 |
| 预算 | `max_len=1024`、`head_max_len=256`、`parallel=8`、`inference.ctx_size=8192` | 见 `config.json`；每问一条独立序列（§4.4 的真实模型） |
| 缓存 | 响应带 `cached` 字段；服务端按 `input_hash` 记录 | 实测同输入同答案（§5.4 ②），去重是安全的 |
| 用量 | `GET /api/usage`（含 `input_tokens`/`infer_ms`/`total_ms`/`answers`） | 本地可读，用于校准预算与复盘（§8.4） |
| 并发 | `parallel=8`、`workers=0`（跟随并发槽位） | 上下文按槽位均分：`max(ctx,batch) ÷ parallel = 1024`；**多客户端共用时只报告、不抢占**（G17） |
### 5.2 客户端设计（`Laya/LayaClient.cs`）

1. **异步、不阻塞游戏线程**：`ThreadPool`/`Task` 发请求，结果放进队列，
   在**帧首 tick**（`PlayerAiRuntime` 已有的帧首统一 tick）应用；HTTP 超时默认 **1500 ms**
   （实测 200 字摘要 3~6 问 = 0.4~1.6 s，超时要按预算给，不能照抄"服务端很快"）。
2. **在途上限 1**：新请求发出前若有旧请求未回，直接标记旧的为过期（防堆叠、防"答案与状态错位"）。
3. **指纹校验**：请求带上摘要指纹（digest 的哈希 + 事件 `seq` + 世界/位置粗量）。
   回来的答案若指纹已过期（世界切换、模态变了、死了）→ **丢弃**，不执行。
4. **降级链**：`Laya 可用 → 用答案` / `超时或不可达 → 行为树兜底动作（hold/wait + 释放输入）` /
   `连续失败 N 次 → 退出 Laya 模式并写事件日志`。
5. **错误处理**：`401/403`（密钥）、`400`（**`questions` 必须是对象**，写成数组会 400，实测踩过）、
   `422 options_exceed_head`（选项超预算）都要有**独立错误码**，进 `ai.logs` 与事件环。
6. **隐私**：日志里对 `baseURL`/密钥做掩码；`state` 只含游戏内状态，不含玩家自己的聊天/文件内容。
7. **去重与合并（树内节点必需）**：键 = `摘要哈希 + 问题库哈希 + 模型`。
   同一键在 `refreshMs`（默认 **1000 ms**，按 §5.4 的延迟量级调）内**只发一次**请求，
   其余消费者共享答案 —— 选择器每帧重访 `Task.LayaAsk` 是常态，没有这层会把 Laya 打死。
8. **多消费者等待**：同一键有多个节点在等时，一个答案喂全部（各自投影到自己要的黑板键）。
9. **不读 `confidence`**：只读 `choice` / `probabilities`（要看分布也只用"top1−top2 的差距"这类相对量，
   绝不用 `confidence` 绝对值 —— 实测它随上下文长度饱和到 1.0）。

### 5.3 预算守卫（发送前自检，别把烂请求发出去）

- 逐问检查 `head`：`len("choice question: " + instructions) + Σ(1 + min(48, 选项文本 token)) ≤ 240`
  （留 16 token 余量），超了 → **本地报错并拒绝发送**，别等服务端截断或报 `options_exceed_head`；
- 选项数与选项描述长度上限见 §4.4 的实践口径表；
- `state` ≤ 200 字符（可配）；`Σ(每问 head) + state` 的粗估守卫（按中文字数 ≈ token 数估）；
- 一批 ≤ 6 问（上限 20 根本不该用，见 §4.4）；
- 估算取 `/api/usage` 里的 `state_tokens`/`input_tokens` 做回归校准（本地可读，便于调参）。

### 5.4 实测（本机，2026-09-26，`laya-multilingual-f16.gguf`，`/v1/systemone`）

**① 预算与延迟（探针：同一份摘要截断/补噪声到指定长度，单批问题固定）**

| 用例 | state 字符 | input_tokens | elapsed |
|---|---|---|---|
| 1 问 8 选项 | 100 / 200 / 401 | 113 / 150 / 242 | 199 / 352 / 434 ms |
| 1 问（4 / 8 / 12 选项） | 200 | 124 / 150 / 176 | 242 / 384 / 417 ms |
| 3 / 6 / 12 问 | 200 | 389 / 740 / 1445 | 808 / 1553 / 2763 ms |
| 3 问 gate | 105 / 201 / 401 / 801 | 238 / 349 / 589 / 1051 | 428 / 837 / 1603 / 2840 ms |

⇒ **耗时与 input token 都随"状态长度 + 问题数"近似增长**（每问一条独立序列，state 被重复编码）。
但**单问并不复制 state**：3 问 × 200 字摘要实测只有 349 input token，
而**把整份状态直接 dump（中文 1146 字）单问就是 518 token / 1493~1865 ms** ——
这就是"预消化"省下来的钱（**约 4 倍**）。

**①b 有效上限（再测一轮，纠正了上一版的误判）**

| 用例 | state 字符 | input_tokens | elapsed |
|---|---|---|---|
| 中文摘要 1 问 | 200 / 400 / 600 | 92 / 149 / 204 | 111 / 373 / 455 ms |
| ASCII 摘要 1 问 | 400 / 600 / 800 | 184 / 262 / 336 | 389 / 663 / 976 ms |
| 摘要 120 字 | 3 / 8 / 10 问 | 234 / 624 / 780 | 379 / 976 / 1250 ms |

⇒ **能玩的量级比"200 字 / 3~6 问"宽得多**（10 问也才 780 token / 1.25 s），
"≤200 字符"是**延迟预算**（不是硬墙）：200 字中文 ≈ 92 token ≈ **0.1~0.4 s**，600 字仍 ≈ 0.46 s。
真正该守住的是：**摘要别 dump、问题别堆、延迟别超过动作层能兜住的时间**。
一轮"塞满"的实测上限仍在 `config.json` 的 `max_len=1024` 附近（见 ③ 的 1024 token / 4472 ms）。

**② 稳定性（同一请求重复 5 次）**

| 用例 | 结果 |
|---|---|
| 长摘要 + 单问 `choice` | 5/5 完全一致（同答案、同 `confidence`、同 token） |
| 短摘要 + 单问 `choice` | 5/5 完全一致 |
| 3 问 gate + 短摘要 | 重复两次逐字段一致（`0.7131 / 0.6691 / 0.9555`） |

⇒ **同输入同输出，确定性很好**：`refreshMs` 去重缓存不会引入"答案抖动"，也意味着
"同一摘要答案反复变"只可能来自**输入变了**，可直接当退化告警。

**③ 两个必须记住的负面结论**

| 结论 | 证据 |
|---|---|
| **摘要长度会改变决策，不是"慢一点"而是"换了个答案"** | 同一事实：104 字符 → `mine`；补噪声到 1146 字符 → `sleep`（各 5/5 一致，都是"稳定地不同"） |
| **`confidence` 不可用于判断** | 105 字符时 0.71/0.67/0.96；摘要变长后三个 gate 全部 = **1.0**（随上下文长度饱和） |
| **超长 state 是"静默截断"，不是报错** | 8600 字符 state（纯英文填充）→ 请求成功、`input_tokens = 1024`（正好等于 `max_len`）、4472 ms。**没有任何"被截断"的信号** → 只能靠自己的摘要预算守 |
| **单个问题的选项超预算也不会拖垮整批** | 3 问里中间那问带 **16 个长选项**：整批仍 `OK`，另两问照常返回（引擎按 §4.4 的规则截断选项文本） |
| **`answers[id]` 自带 `type`** | 实测返回 `gate1:choice, gate2:choice, huge:choice` / `danger:noul, gate:choice, urgent:score` → 客户端可据此校验"问题类型 ↔ 黑板类型"（§4.7） |

**④ 问法决定对错（最重要的可操作发现）**

| 问法 | 结果 |
|---|---|
| `noul`："Is this character currently starving and in need of food?"（state: `food=8(starving)`） | ❌ 判 **0**（错） |
| 同一问题改成 **2 选项 `choice`**（`yes` / `no`，各带 description） | ✅ `yes`，两次重复一致 |
| `noul`："Can this character hit the thing it is aiming at?" | ❌ 判 0（错） |
| 同问题的 2 选项 `choice` | ✅ `yes`（`confidence` 0.9555） |

⇒ 问题库**一律用 `choice`**（是非题给 2 项），`noul` 只当"要一个概率软信号"的旁路。
### 5.5 密钥与配置：**预填 + 快速取用**（照 DSH 的做法）

DSH 侧的做法就是`dsh-laya-system1` 的两层：**默认值 + 用户可覆盖字段 + 密钥优先取环境变量**
（`apiKey` 优先，其次 `apiKeyEnv`，默认 `TYPESAFE_API_KEY`）。游戏侧照抄这个形状：

```
优先级（高 → 低）                         落点                              谁写
1. 环境变量 LAYA_API_KEY                  进程环境                          人/启动脚本
2. <实例根>/PlayerAi/Laya.local.json      mod 配置（不进仓库）               编辑器「设密钥…」按钮 / 手写
3. 空                                      ——                                报明确错误：laya_no_key
```

- 文件名带 `.local.` **是为了让忽略规则一致**：`Mod/.gitignore` 与根 `.gitignore` 都会挡住它
  （实测 `git check-ignore` 命中 `.gitignore:5:/*` 与 `*.local.md`），`AGENTS.md` 的"私有信息"铁律不靠自觉。
- **编辑器预填**（用户要的"快速用节点调用"）：
  1. 属性区新增「🔑 Laya 密钥…」按钮：粘贴一次 → 写入 `PlayerAi/Laya.local.json`；
  2. 之后所有 Laya 节点的密钥字段**显示为掩码 + 来源标签**（`env` / `local` / `none`），不再让人手输；
  3. 密钥文件缺失时，面板直接给"去 DSH 设置里那张卡 / LayaApp 面板复制 key"的指引，
     而不是抛一个泛化的 401。
- 节点本身**不存密钥**（属性里没有这个字段）；密钥只在客户端配置里，节点只引用问题库与模型名。
- 日志/截图/事件环里一律掩码成 `sk-xxxxxx…xxxx`（服务端 `/api/usage` 自己就是这么显示的）。


---

## 6. 决策闭环（快循环）

```
帧首 tick（游戏线程）
 ├─ 观察：摘要编译（只读）
 ├─ 若 Laya 结果队列非空 → 校验指纹 → 应用：
 │     目标 → 动作脚本 → ActionScript.Start()
 ├─ 若无在途请求 且 触发条件成立 → 组装问题 → LayaClient.SendAsync()
 ├─ 若请求在途 → 继续执行当前脚本（不等待、不空转）
 └─ 看门狗：脚本超时/失败 → 走 onFail → 更新事件日志 → 可能立刻再决策
```

**两个入口，一套底座**（不要做成两套实现）：

| 入口 | 谁在等答案 | 节奏 | 用途 |
|---|---|---|---|
| 树内 `Task.LayaAsk` | 行为树的那个节点（返回 `Running`） | 由树的访问频率 + `refreshMs` 决定 | 用连线编排决策点；**默认形态** |
| 树外 `AiStateLayaDecision` | 模式层（自主快循环） | 事件驱动 + 500 ms 心跳 | 没有树 / 树不好表达 / 想让 Laya 直接主导时 |

两者共用：同一份摘要编译器、同一个 `LayaClient`（含去重缓存）、同一套动作脚本与看门狗。
区别只是"谁发起、谁消费答案"。

**触发条件**（树外循环用；树内由节点访问天然触发）：

| 触发 | 说明 |
|---|---|
| 心跳 | 每 500 ms（可配），低频兜底 |
| 事件 | `player.damaged/died`、`ui.message`（游戏自己的提示）、`modal.opened/closed`、`screen.changed` |
| 动作结果 | 脚本结束 / 失败（带稳定错误码） |
| 状态翻转 | 生命/食物/温度跨档、威胁集合变化、`aim` 目标变化 |
| 外部 | 命令行/LLM 显式触发（`ai.laya.ask`） |

**模式层集成**（不新造架构，接进已有的 `AiMode`/`AiModeGraph`）：

| 模式 | 含义 | 输入纪律 |
|---|---|---|
| `Tree`（含树内 Laya 节点） | 树在跑；`Task.LayaAsk` 在树里正常发问 | **默认**。Laya 参与调度但不夺权 |
| `Laya` | 树外快循环接管目标选择，脚本执行动作 | 与 `Tree` **互斥**；进入即暂停树 |
| 保留 `Recording` / `Paused` / `Fault` | 原有语义 | 切出 `Laya` 必 `ReleaseAll()` |

> 注意区分：**树内 Laya 节点 ≠ `Laya` 模式**。前者是树的一部分（可随时被人暂停/接管），
> 后者是"树让位给 Laya"。这个区分要在 `ai.status` 里显式显示，否则排查时会分不清"谁在动"。

**硬规则（不因"模型想这么做"而放宽）**：

1. Laya 是**建议源**，不是特权：所有输出必须映射到动作库/黑板里的枚举项；映射不到 → `wait` / `fail`。
2. 任何脚本、任何来源，最终都写同一份白名单输入字段。
3. 看门狗与超时优先于模型输出：超时即释放/持续，不允许无限保持。
4. 人类随时可夺回（`Home` 热键、`ai.disable`、真人输入优先的共控策略沿用 CM-2）。
5. **未答 ≠ 可以继续**：节点没拿到答案时按 `onUnavailable` 明确处理，绝不"猜一个继续跑"。

---

## 7. 三层分工（"LLM 也要在链外有用"）

| 角色 | 时间尺度 | 干什么 | 不干什么 |
|---|---|---|---|
| **Laya** | 10⁻¹ s | 目标选择、危险判定、阻塞归因、界面分类 | 不输出坐标/时长/角度；不做多步规划；不做细粒度多选题 |
| **动作脚本 / 行为树** | 10⁻² s | 守卫、执行、核对、重试、看门狗 | 不做高层取舍 |
| **LLM（链外）** | 10⁰~10¹ s | ① 把自然语言需求编译成脚本（`aeact`）② 生成/调参问题库 ③ 复盘日志找失败模式 ④ 写新技能并提交人审 | 不进实时闭环；不直接下发输入；不直接落盘（沿用 `player-ai-plan.md` §4.5：内存态可改、落盘由人） |

这条分工正好把用户的判断落成机制：**Laya 快 → 让它在环里；LLM 慢 → 让它在环外产出可复用的东西**。

---

## 8. 安全、健壮性与验收口径

### 8.1 关切 → 措施 → 验收证据

| 关切 | 措施 | 验收证据 |
|---|---|---|
| 不越权 | verb 白名单 + 静态审计脚本扩展（`check_cmd_bridge_readonly.py` 同源思路） | 审计通过；`git grep` 无非法写入 |
| 不残留输入 | 所有结束路径 `ReleaseAll()`；看门狗超时也释放 | 事件日志 + `obs.input` 显示无 held 键 |
| 不失控 | 1 在途请求 / 结果指纹校验 / 连续失败降级 / 人类可夺回 | 掉电式断网测试：角色停下且日志有明确错误码 |
| 可解释 | 每次判定与每次脚本执行都写事件日志（摘要 + 问题 + 答案 + 概率 + 执行结果） | `ai.logs` 可复盘到"为什么做了这个动作" |
| 可复现 | 问题库/动作脚本进包（版本化）；判定输入可回放（同一摘要→同一题面） | 同摘要重发得到稳定答案（观察概率分布） |
| 精度不吹牛 | 只用 4~10 选项的 `choice` + `noul`；`score` 仅作软信号 | 精度回归表（选项数 vs 准确率）随问题库维护 |
| 服务不可用也确定 | 每个节点/循环声明 `onUnavailable`（`fail` / `default` / `keep`），**默认 `fail`** | 服务未启动时整棵树行为与有服务时"同形"：只是走到兜底分支，不卡死、不静默继续 |
| 判得对才敢依赖 | 事件日志按 §8.4 留痕 + 定期抽样人评 | 抽样表：状态指纹 / 题面 / 答案 / 概率 / 实际执行 / 人评对错 |

### 8.4 怎么知道 Laya"判得对"（长期依赖的前提）

- **每次判定留痕**（进 `AiEventLog` 与 `PlayerAi/Logs/`）：摘要指纹、问题库哈希、问题、答案、
  完整概率分布、`confidence`、执行结果（脚本成功/失败码）。
- **顺手做可验证的题**：在同一批问题里加一条 `action_now`（"这一帧实际在做什么"），
  用它的 `choice` 与代码已知的事实对照 —— 这是一个**零成本准确率探针**，
  与 `/api/usage` 记录里的 `answers`（服务端也存了一份答案）互相印证。
- **人评抽样**：导出最近 N 条（含摘要与上下文），人工标"该选哪个"，得到可比较的正确率；
  问题库/选项描述改动后重跑同一批样本，作为回归。
- **退化告警**：`confidence` 长期偏低、或同一摘要答案抖动（概率分布来回摆）→ 记事件并提示，
  这是"模型换了/描述写歪了/摘要字段变了"的第一手信号。

---

### 8.5 明确不做（Non-Goals，写进架构免得越界）

| 不做 | 为什么 |
|---|---|
| Laya 输出坐标/角度/时长等连续量 | 实测该模型做不了回归；也对不齐"优势≠特权"的铁律（很多连续量只能靠特权实现） |
| Laya 直接写输入层字段 | 绕过 verb 白名单就失去了唯一可审计的出口 |
| 60 Hz 逐帧判定 | 150 ms 一次是它的物理速度；连续控制归动作层，Laya 只在事件与心跳上出结论 |
| Laya 改包格式/树结构（除 §4.7 ③ 的受控换树） | 声明式包是校验器/编辑器/分发的基础；让模型直接写树 = 无法审计 |
| Laya/LLM 直接落盘 | 沿用 `player-ai-plan.md` §4.5：内存可改、落盘由人 |
| 把密钥/局域网地址写进被跟踪文件 | 仓库硬规则（`AGENTS.md`）；只进 `AGENTS.local.md` 或本地配置 |
| 让 Laya 判"代码能算出的东西"（可达性/遮挡/能否睡） | 浪费预算且更不准；这些直接进摘要当证据（§4.5） |

---

## 9. 实施分期与文件清单（**待批准，尚未动手**）

### P1 动作原语层（不接 Laya，先能用脚本跑）

| 动作 | 文件 | 说明 |
|---|---|---|
| 新增 | `PlayerAiMod/Action/ActionVerb.cs` | verb 词表 + 参数 schema + 校验 |
| 新增 | `PlayerAiMod/Action/ActionClip.cs` | 逐帧输入片段（三种来源共用） |
| 新增 | `PlayerAiMod/Action/ActionScript.cs` | 脚本模型 + 解析（复用 `Package/PackageJson` 自持 JSON） |
| 新增 | `PlayerAiMod/Action/ScriptCompiler.cs` | verb + 参数 → `ActionClip` |
| 新增 | `PlayerAiMod/Action/ActionQueuePlayer.cs` | 节奏播放 / 叠加 / 中断 / 看门狗 |
| 新增 | `PlayerAiMod/Action/SkillRunner.cs` | 守卫 + 效果核对 + 重试 + 失败码 |
| 新增 | `PlayerAiMod/Action/ActionSelfTest.cs` | 自检（沿用项目"七套自检"风格） |
| 新增 | `PlayerAiMod/Bt/BtRunActionScriptTask.cs` | **行为树节点 `Task.RunActionScript`**（与 `PlayActionPackage` 并列） |
| 改 | `PlayerAiMod/Bt/BtNodeRegistry.cs` | 注册 `Task.RunActionScript`（工厂 + `Task` 形状 + 属性表） |
| 改 | `PlayerAiMod/Package/TreeCompiler.cs` | `ApplyProperties` 加一个 `case`（属性名→字段的唯一映射点） |
| 改 | `PlayerAiMod/Core/AiCommandSet.cs` | `ai.action.script.list/validate/run/stop/export/bake` |
| 改 | `PlayerAiMod/Core/PlayerAiRuntime.cs` | 脚本目录解析（与动作包同路径 `<实例根>/PlayerAi/BehaviorTrees/`，后缀区分） |
| 改 | `PlayerAiEditor` | 物料区「动作脚本」组 + `packages` 式勾选（复用 `Web/app.js:4610` 的现成做法） |

### P2 状态摘要 + 问题库

| 动作 | 文件 | 说明 |
|---|---|---|
| 新增 | `PlayerAiMod/State/StateDigest.cs` | 摘要模型 + 序列化（键值紧凑格式） |
| 新增 | `PlayerAiMod/State/StateDigestCompiler.cs` | 从观察层编译摘要（档位标签、事件增量、**按目标裁剪的地图档位 §4.5**） |
| 新增 | `PlayerAiMod/State/QuestionBank.cs` | 问题库加载 + 模板 + 预算守卫 |
| 新增 | `PlayerAiMod/Questions/*.json`（随包） | 出厂问题库（含选项 description） |
| 改 | `CmdBridgeMod` | 只读命令 `state.digest`（编辑器/AI/复盘共用）；不新增任何写入口 |

### P3 Laya 接入 + 树内节点 + 决策闭环

| 动作 | 文件 | 说明 |
|---|---|---|
| 新增 | `PlayerAiMod/Laya/LayaClient.cs` | HTTP + 超时 + 1 在途 + **去重缓存（§5.2.7）** + 指纹校验 + 错误码 |
| 新增 | `PlayerAiMod/Laya/LayaConfig.cs` | **预填链（§5.5）**：`env LAYA_API_KEY` > `<实例根>/PlayerAi/Laya.local.json`（`baseURL/model/apiKey/timeoutMs/预算`）；密钥不进被跟踪文件 |
| 新增 | `PlayerAiMod/Laya/LayaAnswerCache.cs` | 去重缓存（键 = 摘要哈希 + 问题库哈希 + 模型；TTL = `refreshMs`）与多消费者等待 |
| 新增 | `PlayerAiMod/Bt/BtLayaAskTask.cs` | **`Task.LayaAsk`**：`IsLatent`，`OnEnter` 发问、`OnTick` 写黑板；模型照抄 `BtPlayActionPackageTask`/`BtMoveToTargetTask`（已实测的 Running 驱动模式） |
| 新增 | `PlayerAiMod/Bt/BtLayaSelectTask.cs` | **`Task.LayaSelect`**（§4.7 ②）：答案→选脚本/子树入口并运行 |
| 改 | `PlayerAiMod/Bt/BtNodeRegistry.cs` | 注册 `Task.LayaAsk` / `Task.LayaSelect`（形状 `Task`，属性表见 §4.6） |
| 改 | `PlayerAiMod/Package/TreeCompiler.cs` | 两个新 `case`；`answerKeys` 与 manifest 黑板声明的**一致性校验**（不一致 → 编译报错，不半加载） |
| 改 | `PlayerAiMod/Package/PackageValidator.cs` | 语义检查：`questions` 指向的问题库文件存在、`answerKeys` 数量/类型与问题匹配 |
| 新增 | `PlayerAiMod/Laya/LayaDecisionLoop.cs` | 树外快循环（触发条件 + 批次组装 + 结果映射） |
| 新增 | `PlayerAiMod/States/AiLayaState.cs` | 模式层新状态（与 `Tree` 互斥、进入/退出纪律） |
| 改 | `PlayerAiMod/Core/AiMode.cs` / `AiModeGraph.cs` | 模式枚举 + 转移表 + 自检（33 项那套跟着加） |
| 改 | `PlayerAiMod/Core/AiCommandSet.cs` | `ai.laya.status/ask/on/off/limits`（`ask` 支持 `dry-run`） |
| 改 | `PlayerAiEditor` | 属性区：问题库下拉 + `answerKeys` 与 manifest 黑板的**联动校验提示**；**「🔑 Laya 密钥…」一键设置 + 掩码显示来源（§5.5）**；新增「实时判定」面板（看摘要、题面、概率分布、实际走的连线） |

### P4 复盘与调参工具

- 复盘表：`摘要指纹 / 题面 / 答案 / 概率 / confidence / 实际执行 / 人评`（§8.4）。
- 导出 → LLM 改问题库描述与动作脚本 → 人审 → 落盘（沿用推送式热重载）。
- 编辑器：脚本 `dry-run` 预览（这条脚本会发出哪些输入）；问题库 A/B 对比同一批摘要的答案分布。

### P5 行为树池调度（§4.8：Laya 调顺序，原行为树包补足无缝流程）

| 动作 | 文件 | 说明 |
|---|---|---|
| 新增 | `PlayerAiMod/Bt/BtPoolCallTask.cs` | **`Task.PoolCall`**：按 `mode`（沿用 `Sequence/RandomOne/RaceFirstSuccess` 词汇）跑池里的包；切换走既有 `TreeLibrary.Switch` |
| 新增 | `PlayerAiMod/Bt/BtPoolSetOrderTask.cs` | **`Task.PoolSetOrder`**：改 `pool.sequence` |
| 新增 | `PlayerAiMod/Core/PoolScheduler.cs` | 读 `pool.next/sequence/fallback` 决定"活动树成功后跑哪棵"；**只在步骤边界切换**（S1）；切换前释放输入（S3） |
| 改 | `PlayerAiMod/Bt/BtRuntime.cs` | 暴露"步骤边界/让出"信号（Success 或显式 `Yield`）给调度器；**不改迁移语义** |
| 改 | `PlayerAiMod/Bt/BtNodeRegistry.cs` + `TreeCompiler.cs` | 注册与属性映射（同上） |
| 改 | `PlayerAiMod/Core/AiCommandSet.cs` | `ai.pool.list/status/prepare/use`（`prepare` 复用 `TreeLibrary.PrepareAll`） |
| 改 | `PlayerAiMod/Package/PackageValidator.cs` | 提示"节点 id 变更会中断运行态迁移"（G11）；池引用包存在性校验 |

### P6 资源库（存盘 / 内存 / 临时缓存）与拒包异常处理（§4.11 / §4.12）

| 动作 | 文件 | 说明 |
|---|---|---|
| 新增 | `PlayerAiMod/Assets/IAssetStore.cs` | 统一抽象：`Load / Memory / AutoSave / Save / Drop / Dirty / Hash`（树包先接，动作包与问题库随后） |
| 新增 | `PlayerAiMod/Assets/MemoryAssetStore.cs` | 内存库：载入即算哈希、改动置 `dirty`、世代号（与"A 暂停代次"同源思路） |
| 新增 | `PlayerAiMod/Assets/AutoSaveCache.cs` | `.autosave/` 影子副本：**原子写（复用 `PackageWriter.TryWriteFile`）**、版本头（R2）、恢复时的"时间戳 + 哈希"比较（R1） |
| 新增 | `PlayerAiMod/Assets/AssetRecovery.cs` | 启动恢复流程：发行/存档 → 缓存 → 决定用哪个 + 在编辑器/日志里说明原因 |
| 新增 | `PlayerAiMod/Assets/AssetRecoveryTask.cs` | **拒包/重取的异常处理**：四码分流、指数退避、熔断（`exhausted`）、失败标记键（`pkg.fail`/`pkg.retry`） |
| 新增 | `PlayerAiMod/Questions/pkg_recover.json`（随包） | 异常处理专用问题库（§4.12 的四选项）；**只在 Laya 可用时**问 |
| 改 | `PlayerAiMod/Package/PackageRoots.cs` | 加入 `.autosave/` 与 `Saves/` 的落点；**`.autosave/` 不进包清单、不参与"包存在"校验**（R4） |
| 改 | `PlayerAiMod/Core/AiCommandSet.cs` | `ai.asset.status/save/drop/autosave/restore`（人/LLM 用；Laya 不用这些） |
| 改 | `PlayerAiEditor` | 物料区显示"磁盘版 / 内存版（dirty）/ 缓存版（更新 X 分钟）"三态；保存/丢弃/恢复三个按钮；**Laya 总配置入口（D16）**；冲突时给"用磁盘覆盖 / 另存为新包"选择（G25） |
| 改 | `PlayerAiMod/Package/PackageReloader.cs` | 三份哈希的冲突规则（G25）：内存 `dirty` 时不静默覆盖，记冲突事件 |

### 每期验收口径

- **P1**：脚本 `mine_stone_once` 在实测世界里完整跑通（按键/朝向/挖掘/事件闭环），看门狗与中断实测；
  录制包能 `export` 成脚本草稿并手动改参数后跑通；`Task.RunActionScript` 在树里跑通且能在编辑器里连线。
- **P2**：摘要 ≤200 字符且覆盖"界面 + 角色 + 环境（含地图档位）+ 最近事件 + 当前动作"；预算守卫拦得住超长请求。
- **P3**：`Task.LayaAsk` 在树里连通（物料区出现 → 连线 → 跑 → 黑板看到答案 → 装饰器分流）；
  服务未启动时整树走 `onUnavailable` 分支且不卡死；去重生效（选择器每帧重访只发一次请求）；
  断网/超时/401/答非所问四种异常下**行为可预测**（停、释放输入、写明确错误码、可夺回）；
  **人类夺回后，1 秒后才回来的在途答案不得重新接管**（G8）；
  实测延迟与 `/api/usage` 的 `total_ms` 对齐。
- **P4**：LLM 产出的脚本/问题库改动**必须经人审**后落盘；抽样正确率表可复算。
- **P5（池调度）**：`Task.PoolCall` + `pool.next/sequence/fallback` 跑通"树 A 判定 → 池切到树 B"的完整时序；
  实测三条无缝性不变量（S1 只在步骤边界切、S2 常驻树无残留运行态、S3 切换瞬间无残留按键）；
  Laya 不可用时池按默认顺序继续（不改变"行为可预测"）。
- **P6（资源库）**：内存改动默认**不落盘**（断点验证：改完直接杀进程 → 磁盘包字节不变）；
  **杀进程后重启能恢复自动缓存**（且编辑器明确显示"用的是缓存"）；手动存档优先级高于缓存（R1）；
  缺问题库**拒包**且节点上能看到 `pkg.fail`/`pkg.retry`；`pkg_write_in_progress` 自动重取成功、
  `pkg_invalid` **不重取**；连续失败达上限后**熔断停手**而不是转圈；`.autosave/` 不出现在包清单里。

---

## 10. 决策记录

### 已定（用户 2026-09-26 答复）

| # | 问题 | 决定 |
|---|---|---|
| D1 | Laya 输出粒度 | **目标 + 少量参数**；且**地图信息要参与决策** → 先量化成档位进摘要（§4.5），再输出后续目标 |
| D2 | 第一版行为树能不能跑脚本 | **能**：`Task.RunActionScript` 与 `Task.PlayActionPackage` 并列（P1 就做，不依赖 Laya） |
| D3 | 脚本格式 | **JSON**（复用 `PackageJson` / `PackageValidator` / 编辑器 schema） |
| D5 | 平板端 Laya 地址 | **走局域网地址**；地址只在本地配置/`AGENTS.local.md`，不进被跟踪文件 |
| D7 | 定位 | **Laya = 控制单元**：可参与行为树调度、也可临时调度底层最小动作；原有树调度**保留** |
| D8 | Laya 能否进行为树编辑器连线 | **能**：`Task.LayaAsk` / `Task.LayaSelect` 注册即可自动进物料区（§4.6） |
| D4' | 三层关系 | 游戏内 HTTP 直调 与 DSH 里的 LLM 调 Laya **不冲突**（同一服务、两条客户端） |
| D9 | 是否开放 §4.7 ③ **受控换树** | **先不开**，等 ①② 跑稳再评估（本轮不实现，只在文档里留位） |
| D10 | 节点拿不到答案时 | **默认 `fail`**，可配 `default` / `keep` |
| D12 | 密钥怎么放 | **预填 + 快速取用**（§5.5）：环境变量 > `PlayerAi/Laya.local.json`，编辑器一键设置并掩码显示；密钥不进任何被跟踪文件、不进节点属性 |
| D13 | 问法口径 | **一律 `choice`**（是非题给 2 项）；不用 `noul` 做分支条件，不用 `confidence` 做判断（实测依据 §5.4 ③④） |
| D11 | 问题库缺失时 | **拒包**（校验器报 error，不半加载）；同时节点引出**失败原因键**供异常分支提前规划；丢包/缺文件**默认重取**（有上限与熔断），校验不过**不重取**（§4.12） |
| D15 | 资源库的存储模型 | **两套库 + 临时缓存**（§4.11）：存盘库 → 读进内存才算加载；内存任意改；**默认不回写磁盘**；中间用 `.autosave/` 自动缓存；可**手动保存**回磁盘、可**丢弃**；断电按"先缓存、后存档"恢复 |
| D16 | Laya 的参数配置在哪 | **一份配置**（`PlayerAi/Laya.local.json`）管全部（端点/key/超时/预算/重取次数），在**行为树编辑器里有一个统一的功能入口**控制，不散落在节点属性里 |

### 仍需拍板

| # | 问题 | 我的建议 | 理由 |
|---|---|---|---|
| D4 | Laya 是否允许改写动作库/问题库？ | **只改内存，落盘由人** | 与 `player-ai-plan.md` §4.5 既有决策一致（AI 探索性改动不污染发行包） |
| D6 | LLM 生成的技能是否直接变成包分发？ | 先只做人审导出（P4） | 出厂包一旦被污染，多设备对账就崩了 |
| D11 | 问题库文件缺失时是拒包还是告警 | **拒包**（`PackageValidator` 报 error，不半加载） | 沿用现有装载闸门：任何 error 一律拒绝装载 |
| D14 | `PlayerAi/Laya.local.json` 是否也用于存"模型名/超时/预算"（不只密钥） | **是**：一份本地配置管全部 Laya 参数 | 与 `CmdBridge.json` 同构，用户只学一个位置；`baseURL` 在 Android 上就指局域网 |

**实测已完成（§5.4，2026-09-26）**：预算/延迟曲线、同输入稳定性、摘要长度对答案的影响、
`noul` vs 2 项 `choice` 的对错差异、`confidence` 不可用 —— 结论已写进 C4/C4b/C4c 与 §4.3/§4.4。

**仍需实测的三件事**（写进 P2/P3/P4 验收）：
1. 中文摘要（不是英文样例）在 100/200 字下对**真实游戏状态题**的分离度，用游戏内实际摘要重跑一遍；
2. 游戏帧率波动下"1 在途 + 1 s 去重 + 心跳"的实际决策频率与失控率；
3. 树内节点版（`Task.LayaAsk`）与树外循环版在同一场景下的行为差异与延迟开销。

---

## 11. 补漏清单（2026-09-26 复查新增；按"不做会出事"排序）

| # | 缺的东西 | 不补的后果 | 做法 |
|---|---|---|---|
| G1 | **延迟对控制结构有要求：Laya 只能管"策略时间尺度"，策略之间的流程由原行为树包补足（§4.8）** | 0.4~1.6 s 的判定若写进"躲岩浆/被围殴"的实时分支 → 角色站着挨打 | 规矩写进文档与校验：Laya 节点**不得**放在"每帧都会重访"的实时子树上；实时反应由**动作层/传感器/原行为树包**负责（亚帧级）。**Laya 决定的是"接下来跑池里的哪棵包"（`pool.next` / `pool.sequence`），不是逐帧控制** |
| G2 | **非枚举参数怎么来** | Laya 输出 `goal=mine` 之后，没人决定"挖哪一格" | 两级：**枚举**由 Laya 给（≤6 槽位/≤8 目标）；**具体值**由 C# 按确定性规则挑（§4.9：准星格 / 最近敌对 / 第一个可吃 / 语义选择器）。规则必须可复现，且只用现有只读观察 |
| G3 | **答案 → 黑板的类型与越界语义** | 答案里有脏值；选项改名/删项后旧树静默走错分支 | 完整矩阵与四元组绑定语法见 §4.7：`answers[id].type` 自带类型可校验；`choice.key` 是稳定 id；`confidence`/`action_probability` 禁止写黑板；`actor` 模型给不出；**任何失配都在编译期或本节点 `fail`，绝不猜值** |
| G4 | **构建期引用校验 + CI** | 题目改名/删文件后，包到运行时才炸 | `PackageValidator` 加"问题库存在 / 每个追问的 id 在库里 / `answerKeys` 数量与类型匹配 / 选项 key 闭集"；**已定（用户同意）** |
| G5 | **双状态机：世界内 / 世界外各一套（§4.13），含"过渡态"与交接协议** | 世界外没有角色与地图：摘要编译不出来、树的节点不可用 → 白等超时或动作对着空气发；世界进出的过渡帧（切场动画/加载）两类动作都不安全，谁都不该动 | 摘要与问题库**按 phase 分支**；两套树包 + 两套问题库；过渡态**只做 `obs.waitFor` 条件等待，不做决策**；交接协议：进入/退出世界的瞬间由 phase 检测器触发"释放输入 + 暂停 + 清缓存 + 换宿主" |
| G6 | **服务/密钥/端点的三种分叉必须在**发请求前**就判出来** | 用户只看到"没反应"；或所有失败都糊成一个泛化错误 | 顺序探测：① 端点可达性（`/v1/models` 或 TCP 连接）② 密钥来源是否存在（`env`/`local`/`none`）③ 探测到是 401 还是超时 → 三种提示各自可执行（开 App / 设 key / 改地址）；`ai.laya.status` 必须回答"端点、key 来源、上次耗时、上次失败码、上次成功时间" |
| G7 | **同一棵树里多个 Laya 节点的一致性** | 两个节点可能各自发问、拿到互相矛盾的答案（都基于不同时刻的摘要） | 去重缓存按**摘要指纹**共享（§5.2.7）；同一帧内的多个节点用**同一份摘要**，因此共享同一批答案。要"多问"就合并成一批问题，不要多个节点 |
| G8 | **暂停/夺回的三态语义（§4.14）** | 一个键把"停手""放弃接管""让开但不退出"混成一种；在途答案 1 秒后又把角色接管；共控时人/AI 互相掐 | 三态分开：**完全暂停**（不决策、不注入、可保留树状态）/ **放弃接管**（回待机、释放输入）/ **共控**（沿用 `FocusPolicy` 的输入合并 + 视角"最近真实鼠标"仲裁）。暂停要**取消在途请求 + 清缓存 + 按接管代次丢弃已到答案** |
| G9 | **判定日志的体积控制** | 每次判定写摘要+概率+分布 → 事件环与日志膨胀 | 复用 `AiEventLog` 的滚动+限量机制；概率分布只留 top2 + 全量可选（默认关）；采样率可配（例如每 N 次留一条全量） |
| G10 | **池切换的无缝性不变量（S1~S3）** | 在挖矿中途切树 → 那一步被丢；切回常驻树继承上次运行态；切换瞬间旧树的按键还按着 | §4.8：切换只在**步骤边界**；切换前 `ResetSubtreeState()`；切换后先释放再接管。**依赖源码事实**：`TreeLibrary.Switch` 明确用 `ReplaceRoot(migrate:false)`，跨树**不迁移**运行态 |
| G11 | **热点重载的 id 稳定性纪律** | 想"正在跑的那一步别被中断"，但改了节点 id → `BtMigration` 认不出，运行态被中断 | §4.8：跨包/热重载保留运行态**按节点 id**（`BtMigration.Key`）；校验器与编辑器要给"改名会中断运行态"的警告 |
| G12 | **确定性取值规则必须可复现** | 同一状态两次挑中不同目标 → 复盘/回放对不上账，问题无法定位 | §4.9 纪律 2：规则写死（距离升序 + 序数兜底），并进事件日志（"Laya 说 mine → C# 选了哪一格"） |
| G13 | **共控的三个具体冲突没人规定：按键、视角、动作** | 人和 AI 同时按 W；人转视角被 AI 的 `lookAt` 每帧抢回；人手动拿了镐子而 AI 正在执行"吃东西" | ① 按键：沿用 `FocusPolicy` 的"真实 ∥ 注入"合并（已实现，P1 明确不做"谁赢"的仲裁，但**要写清"同键冲突时人的按下不会被 AI 的松开清掉"**）；② 视角：沿用 `LookOwnerMode.Auto`"最近真实鼠标活动归人"；③ 动作：新增规则 —— **人的输入事件（`obs.events` 里的按键/鼠标边沿）触发"当前动作让位"**，AI 不与人抢同一个动作对象 |
| G14 | **双状态机的代码宿主（世界外没有 Player 实体）** | 世界外的树/状态机没有挂载点 → 界面阶段直接不工作 | 现有实现已经支持：`PlayerAiComponent` 挂在 Player 上，但**帧首 tick 与暂停是全局的**（`PlayerAiRuntime.TickFrameStart` / `Paused` **对世界外同样有效**，源码注释已写明）；世界外宿主用 runtime 级宿主 + `ScreensManager.CurrentScreen` 驱动，不依赖 ComponentPlayer |
| G15 | **"当前 phase"的权威判定与稳定化** | 抖动：`world.loaded` 每帧跳变 → 两套状态机来回切 | 单一定义（`GameManager.Project != null && 有 ComponentPlayer && ScreensManager.CurrentScreen is GameScreen`…），**加去抖**（连续 N 帧一致 + 过渡期不算）；判定结果进 `ai.status` 与摘要 |
| G16 | **Laya 的"视觉盲区"** | 进世界选地图、翻页、看对话框时，Laya 只能看到我们摘要里给的字；列表很长时信息不全，选错世界/点错按钮 | 世界外的摘要要**结构化列候选**（`list:WorldsList` 的可见行 + 索引 + 选中态），枚举成"第 1/2/3 行 / 下一页 / 返回"；Laya 只选序号，**翻页/滚动由 C# 做**（确定性） |
| G17 | **共享 Laya 服务的资源竞争** | 游戏内 AI + 面板分析 + DSH 里的 LLM + 动作包编辑器试跑同时打同一个服务；`parallel=8` 但每槽只有 `max_len` 的预算 | 客户端侧：单在途 + 去重 + 队列上限；`ai.laya.status` 报告"本端在途/排队"；面板分析等重活建议人工避让（**不抢，只报告**） |
| G18 | **版本/摘要/问题库对账** | 升级 Laya 或改摘要字段后，旧包静默降质（答案还出得来，只是不对） | 摘要带 `digestVer`、问题库带 `qbankVer`；每次判定记录 `model` + `version`（服务端本来就在 usage 里记）；版本变化 → 事件 + `ai.laya.status` 提示"建议重跑抽样表" |
| G19 | **玩家侧可见性：AI 现在到底在干嘛** | 用户在共控/接管时看不出"AI 在等 Laya / 卡住 / 已放弃"，只会觉得"AI 发呆" | HUD 一行状态（`AI: 思考中(0.4s) / 执行 mine_stone / 已暂停 / Laya 不可用`），失败原因用已有 `ai.logs` 与事件环；世界外也显示（界面阶段同样需要） |
| G20 | **世界外操作链的"配方与录制"没有沉淀** | 每次进游戏都靠实时判定，慢且不稳；而这条链其实是**固定流程**（选世界→翻页→创建角色→命名→确认→等加载） | 按 `cmd-bridge-plan.md` 既有配方落成**录制动作包 + 世界外树包**（`cmd-bridge-plan` §16.6 已有"进入游戏动作包"的先例）；Laya 只在这条链的**分叉点**（选哪个世界/是否覆盖/是否删除）出枚举 |
| G21 | **多设备共用同一 key 的用量归属** | PC 与平板的调用混在一个 `key_id` 里，`/api/usage` 分不清是谁的延迟/失败 | 每端一个 key（LayaApp 面板可建多个）；`ai.laya.status` 显示本端用的 key 前缀（掩码） |
| G22 | **失败标记绝不能喂给模型**（§4.12 的核心纪律） | 把"包加载失败/文件缺失"写进摘要 → 模型拿**基础设施噪声**做战术决策；日志与摘要混在一起后无法复盘"是决策错还是环境错" | 失败标记只走**黑板键**（`pkg.fail` / `pkg.retry`）供树内异常分支消费；**永不进摘要、永不作为 Laya 问题**；摘要里只允许出现"游戏世界的状态" |
| G23 | **"丢包"与"缺文件"必须分开处理** | 混成一个"重取"→ 校验错误也无限重试；或在"文件正在被原子替换"时立刻判失败 | 四码分开（§4.12）：`pkg_write_in_progress`（立刻短退避重取）/ `pkg_missing`（指数退避，默认 3 次）/ `pkg_invalid`（**不重取**，拒包）/ `laya_unavailable`（走降级链） |
| G24 | **重取要有熔断 + 自动问题判断不能自成死循环** | 服务或资源长期不可用时无限重试；"自动问题判断"自己也要问 Laya，Laya 挂了就转圈 | 窗口期内超上限 → `exhausted` + 停手提示人；**自动问题只在 Laya 可用时走**，否则直接走 C# 确定性兜底 |
| G25 | **内存库 vs 热重载的冲突规则**（三份哈希：通知 `hashN` / 磁盘 `hashD` / 内存 `hashM`） | 编辑器保存的通知**静默覆盖**内存里未保存的改动（或反之），两边都以为自己是权威 | §4.11 的冲突表：`hashN==hashD 且内存 dirty` → **不静默覆盖**，记冲突事件 + 编辑器里选"用磁盘覆盖 / 把内存另存为新包"，默认**保留内存**（不打断运行态） |
| G26 | **自动缓存不得吃掉手动存档** | "自动存档"静默覆盖用户手动存档 = 游戏存档被自动覆盖，且不可逆 | R1：autosave 只写 `.autosave/`；恢复时比**时间戳 + 内容哈希**，并在编辑器显示"将用缓存（新 X 分钟）/ 将用存档"。R2 缓存带版本头，版本不认就当**不可恢复** |
| G27 | **`.autosave/` 不能当正式包** | 缓存被列进包清单/可分发统计/校验器的"包存在"判断 → 跨设备带出脏状态，甚至把缓存当发行包 | R4：不列清单、不进"可回放/可分发"、**不满足"问题库存在"的校验**（缺正式文件仍然拒包） |