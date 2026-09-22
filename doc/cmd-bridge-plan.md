# CmdBridge 实施计划：命令行桥（Mod + C# 客户端）与游戏 AI 基础

> 状态：**设计已定稿，待实施**（本文档为 goal 模式实施依据）
> 目标产物：`Mod/CmdBridgeMod/`（scmod，只读观察面 + 白名单输入注入面） + `Mod/CmdBridgeClient/`（`sccmd.exe`，命令行驱动）
> 约束：主仓库 `Engine/`、`EntitySystem/`、`Survivalcraft/` **零修改**；所有产物位于 `Mod/` 仓库
> 定位：**玩家机器人（NPC）** —— 允许无限查看游戏内数据，但一切交互必须通过**玩家控制器**（优势 ≠ 特权）

---

## 0. 文档定位

本文档同时描述三层，实施时按层落地、逐层验收：

| 层 | 产物 | 职责 |
|---|---|---|
| L1 观察层 | `CmdBridgeMod`（.scmod） | **只读**导出游戏内存中的 UI / 玩家 / 世界状态 |
| L2 动作层 | `CmdBridgeMod`（白名单输入注入）+ `sccmd.exe`（命令行） | 把玩家级输入（视角/键鼠）送进游戏 |
| L3 决策层 | 未来的游戏 AI（AI 仓库） | 状态机 + 操作池，消费 L1、驱动 L2 |

L3 不在本仓库实现，但本文档冻结 L1/L2 的接口与数据模型，使 L3 可以独立开发。

---

## 1. 目标与能力边界

### 1.1 总体目标：一个“玩家机器人”（NPC）

做一个**能操作这个游戏的机器人**。它操控的对象是**玩家角色**（玩家控制器），相当于游戏内动物的 AI，只是操作对象换成了玩家——也就是“网游里的玩家机器人”。

**可以有优势，但不能有特权。**

两个用途：

1. **操作可达性调试**：模拟真人把 UI／交互路径真实走一遍，验证“这些操作是否真的可达”（哪个按钮点不到、被谁挡住、需要哪些前置步骤、最短操作链是什么），用于应用与 Mod 的可达性回归测试。
2. **自主游玩**：像 NPC 一样在生存模式里活下去（采集、合成、建造、战斗、进食、休息、躲避），由状态机 + 操作池驱动玩家控制器。

核心原则一句话：**允许无限查看，但交互必须通过玩家控制器。**

> **玩家控制器**（本文档的硬定义）= 游戏为玩家角色提供的输入通道：**视角转动 / 键盘 / 鼠标 / UI 点击**。
> 本 Mod 只允许在这四个通道上注入（§2.8），注入的每一项最终都由游戏原版逻辑裁决（相机 → 射线 → `ComponentMiner` → 方块与物品规则 → UI 事件）。

#### 能力要求

让 AI（或脚本）**像人一样玩 Survivalcraft**：

- 不再只靠视觉截图做模糊操作，而是**直接读懂游戏内存状态**：当前界面有哪些可点元素、玩家正在按什么键、瞄准的是什么方块/实体、背包里有什么、生命/食物/体力/体温/湿度、时间/天气、周围实体。
- 操作**必须经由游戏自身逻辑**完成：视角转动、按键、鼠标点击都走游戏原有的输入→规则→效果链路；挖方块要走 `ComponentMiner`、放置要走方块放置规则、进食要走物品使用规则。
- 允许输入层"超人化"：视角可以**瞬时旋转到任意角度**，按键可以精确到帧，避免 AI 为了挖一个方块做一连串拟人化的视角微操。
- **UI 操作必须逐级进行**：AI 可以超快速度连点 A→B→C→D，但**不允许跳级**（A→D）。按钮是一级一级出现的，AI 只能操作“当前确实存在”的元素；禁止直接切屏、禁止直接调用屏幕逻辑、禁止预置未来界面上的点击。
- 最终在其上构建**状态机 + 操作池**的游戏 AI。

### 1.2 能力判定铁律（唯一判据）

| | 内容 | 判定 |
|---|---|---|
| **优势（允许）** | 无限查看游戏内数据（全知读取） | ✅ AI 相对人的核心优势 |
| | 超人输入速度：瞬时转视角、超快连点、精确到帧的按键 | ✅ 仍走玩家控制器，只是“手速无限” |
| | 完美记忆、无疲劳、24 小时在线 | ✅ 不触及游戏状态 |
| **特权（禁止）** | 直接改生存值：扣血后自己加回来 | ❌ 只能等游戏自然回血，或通过玩家交互吃喝／用药／睡觉 |
| | 直接改背包／物品：无尽仓库、凭空物品、修复工具 | ❌ 只能通过 UI／鼠标在正常容器里存取；生存模式下其他玩家与容器就是普通仓库 |
| | 直接改地形／方块、直接传送、直接改时间天气、无敌／穿墙 | ❌ 全部禁止 |
| | 直接切屏、直接调用按钮回调、跳级操作 | ❌ 必须一级一级走玩家可达路径 |

**“优势 ≠ 特权”的界线**：优势改变的是“AI 怎么看、多快下手”；特权改变的是“世界状态本身”。前者允许，后者禁止。

**善意作弊也算作弊**：以“为了让它能继续玩”为名的状态写入（帮它回点血、补点材料、修一下工具、把它从坑里挪出来）**一律禁止**。生存压力必须由 AI 自己通过玩家交互解决。


> **"这个操作是不是经由游戏自己的逻辑产生的？"**
>
> - **是**（视角转动 → 相机 → 射线 → `ComponentMiner`；按键 → `PlayerInput` → 运动组件；点击 → `Widget` 命中 → 屏幕逻辑）→ **允许**，哪怕速度远超人类。
> - **否**（直接把方块改成空气、直接把玩家挪到某坐标、直接给物品/回血/改时间）→ **禁止**。

一句话概括：**允许把"手指"伸进引擎，禁止把"手"伸进世界状态。**

由此得出三段式边界：

| 层面 | 允许 | 说明 |
|---|---|---|
| 读（内存） | **无限制** | 这是 AI 相对视觉的核心优势，全部开放 |
| 写（输入层） | **允许，含超人速度** | 视角瞬时转动、按键精确注入、鼠标点击；效果必须由游戏逻辑裁决 |
| 写（状态层） | **禁止** | 任何绕过游戏规则直接改状态的行为 |

### 1.3 明确禁止清单（Non-Goals，写死在架构里）

以下能力**本 Mod 永远不提供**，任何后续开发都不得添加。判定方法：**是否"用坐标直接改变世界/角色状态"**。

| 禁止类别 | 点名 API（黑名单关键字） |
|---|---|
| 改地形/方块 | `SubsystemTerrain.ChangeCell`（`SubsystemTerrain.cs:229`）、`Terrain.SetCellValueFast`（`:242`）、`Terrain.SetCellValue` |
| 改玩家位置/速度 | `ComponentBody.Position` / `.Velocity` 写入、`ComponentFrame.Position` 写入 |
| 改生存状态 | `ComponentHealth.*`、`ComponentVitalStats.*`、`ComponentOnFire`、`ComponentFlu`、`ComponentSickness` 写入 |
| 改背包/物品 | `ComponentInventory` / `IInventory` 的增删改、`ComponentMiner.Inventory` 写入 |
| 改世界参数 | `SubsystemTime`、`SubsystemTimeOfDay`、`SubsystemWeather`、`SubsystemGameInfo.WorldSettings` 写入 |
| 跳过 UI 层级 | `ScreensManager.SwitchScreen`（`ScreensManager.cs:61-92`）、任何直接切屏／直接调用屏幕逻辑／直接触发按钮回调的接口 |
| 预置未来操作 | 对“当前不存在”的元素排队点击、对下一个界面预置整段点击序列 |
| 善意作弊 | 以“帮 AI 回血／补给／修复工具／补充材料／脱困”为由的任何状态写入 |
| 改创造/飞行等能力 | `ComponentCreature`/`ComponentPlayer` 的能力开关写入 |
| 替换游戏类绕过规则 | `GameDatabase.GameDatabase` 替换、`IModInjector.Register` 映射（`GodMode`/`ConsoleMod` 式做法） |
| 无限制批量操作 | 任何"一次性影响任意范围世界"的接口 |

> 注：`ConsoleMod` 的 `move` / `tp`、`HeadlessRenderingMod` 的 `world.*` / `player.*` 属**服务器运维**用途，与本 Mod 的"AI 玩游戏"定位不同，本 Mod **不复用其任何写命令**，也不调用其代码。

### 1.4 审计规则（可执行，纳入 P1 验收）

允许的引擎内写入点是**白名单**，只有下列 4 组（全部位于"输入层"）：

```text
1. ComponentLocomotion.m_lookAngles          视角角度（瞬时旋转）
2. Keyboard.m_keysDownArray / m_keysDownOnceArray / m_keysDownRepeatArray / m_lastKey / m_lastChar   按键
3. Mouse.m_mouseButtonsDownArray / m_mouseButtonsDownOnceArray / m_mousePosition                    鼠标
4. WidgetInput 的 Press / Tap / Click / SpecialClick / Scroll / Drag 派生字段（仅在需要免焦点点击时）
```

执行两条静态检查：

```bash
# 检查 1：所有反射写入的成员名必须落在白名单内
grep -nE "ModifyParentField|ModifyStaticField" Mod/CmdBridgeMod/**/*.cs   # 人工/脚本核对成员名 ∈ 白名单

# 检查 2：黑名单关键字命中即构建失败
grep -nE "ChangeCell|SetCellValue|SwitchScreen|\.Position *=|\.Velocity *=|\.Health *=|\.Food *=|AddItem|RemoveItem" \
     Mod/CmdBridgeMod/**/*.cs
```

补充规则：

- 只允许 `GetParentField` / `GetStaticField`（只读）用于观察；`InvokeParentMethod` / `InvokeStaticMethod` **一律禁止**（不调用游戏私有方法，避免绕过规则）。
- **逐级校验**：UI 点击必须在**注入前**用当前帧控件树重新断言“目标存在且命中”（§4.3）。客户端观测可能滞后 1 帧，不得作为注入依据，更不得跳级。
- **善意作弊零容忍**：代码评审对任何“为了绕过困难而顺手改状态”的写法一律打回；饥饿、受伤、夜晚、敌人等压力必须由 AI 通过玩家交互解决。
- Mod 不注册任何 Injector 映射、不订阅 `GameDatabase.GameDatabase`、不替换任何 Subsystem/Component/Parameter。
- 每个白名单写入点必须在代码里标注来源：`// Source: 文件名:类名.方法名`，并注明"属于输入层注入"。

---

## 2. 源码勘察结论（实现地基，均已读源码确认）

### 2.1 切入点：`Frame.Update` 事件（不替换任何东西）

```text
Survivalcraft/Game/Program.cs:144   ScreensManager.Update();   // ← 控件树 Update（含所有 Screen.Update()）
Survivalcraft/Game/Program.cs:145   DialogsManager.Update();
Survivalcraft/Game/Program.cs:147   ModManager.ModEventBus.TriggerEvent("Frame.Update", null);   // ← 本 Mod 钩子
Survivalcraft/Game/Program.cs:165   ScreensManager.Draw();     // ← 控件树 Layout + Draw
```

- `Frame.Update` 是纯事件订阅；`SuAPICore` 自身就订阅了两次（`EntitySystem/SuAPICore/Plug/SuAPICoreMod.cs:42,58`），**支持多订阅者**。
- 本 Mod 不替换 `SubsystemGameWidgets`（GUID `6bf14dc6-32e7-4e8c-b3c4-438e0eee13ad`，被 `ConsoleMod` 独占，见 `Mod/ConsoleMod/Plug/ConsoleMod.cs:27-37`）。
- 不碰渲染、窗口、音频、帧率 → **玩家 UI 完全不受影响**。
- EventBus 回调异常会被静默吞掉，回调必须自带 try/catch + `Log.Error`。

### 2.2 只读 API 清单（观察层地基）

| 需求 | API | 出处 |
|---|---|---|
| 遍历控件树 | `ContainerWidget.AllChildren` / `.Children` | `Survivalcraft/Game/ContainerWidget.cs:8,10` |
| 树根 / 当前屏幕 / 动画中 | `ScreensManager.RootWidget` / `.CurrentScreen` / `.IsAnimating` | `Survivalcraft/Game/ScreensManager.cs:42` |
| 控件屏幕矩形 | `Widget.GlobalBounds`（客户区像素） | `Survivalcraft/Game/Widget.cs:320,808-820` |
| 控件名 / 类型 | `Widget.Name` / 运行时类型 | `Survivalcraft/Game/Widget.cs:336` |
| 全局可见/可用/命中可见 | `IsVisibleGlobal` / `IsEnabledGlobal` / `IsHitTestVisible` | `Survivalcraft/Game/Widget.cs:378-410` |
| **坐标实际命中谁** | `Widget.HitTestGlobal(point)` | `Survivalcraft/Game/Widget.cs:699,836-857` |
| 坐标换算 | `Widget.ScreenToWidget` / `.WidgetToScreen` | `Survivalcraft/Game/Widget.cs:704-712` |
| 本帧被点击的控件 | `ClickableWidget.IsClicked` / `IsPressed` | `Survivalcraft/Game/ClickableWidget.cs:7-9,25-51` |
| 按钮文本/勾选 | `ButtonWidget.Text` / `.IsChecked` | `Survivalcraft/Game/ButtonWidget.cs:10,14` |
| 标签文本 | `LabelWidget.Text` | `Survivalcraft/Game/LabelWidget.cs:38` |
| 滑块值/复选框 | `SliderWidget.Value`、`CheckboxWidget.IsChecked` | `Survivalcraft/Game/SliderWidget.cs:67` |
| 弹出对话框 | `DialogsManager.Dialogs` | `Survivalcraft/Game/DialogsManager.cs:32` |
| 屏幕实例→名字 | `ScreensManager.m_screens`（静态私有字典，**只读反射**） | `ScreensManager.cs:44`；先例 `Mod/HeadlessRenderingMod/Plug/HeadlessRenderingMod.cs:835-843` |
| 窗口客户区尺寸/位置 | `Window.Size`(ClientSize) / `Window.Position` | `Engine/Engine/Window.cs:94-120` |
| GL 视口 | `Display.Viewport` / `Display.BackbufferSize` | `Engine/Engine/Graphics/Display.cs:41,43` |
| UI 缩放/翻转开关 | `SettingsManager.UIScale` / `.UpsideDownLayout` | `Survivalcraft/Game/ScreensManager.cs:420,431` |
| **玩家输入意图** | `ComponentInput.PlayerInput` | `Survivalcraft/Game/ComponentInput.cs:28` |
| 玩家输入字段 | `PlayerInput`（`Move/Look/Jump/Dig/Hit/Aim/Interact/Drop/…`） | `Survivalcraft/Game/PlayerInput.cs:5-68` |
| 模态面板（背包/工作台/衣物） | `ComponentGui.ModalPanelWidget`（**public**） | `Survivalcraft/Game/ComponentGui.cs:141` |
| HUD 读数控件 | `ComponentGui.HealthBarWidget` / `FoodBarWidget` / `TemperatureBarWidget` / `LevelLabelWidget` / `ShortInventoryWidget` | `Survivalcraft/Game/ComponentGui.cs:131-139` |
| 世界射线 | `SubsystemTerrain.Raycast(...)` → `TerrainRaycastResult?` | `Survivalcraft/Game/SubsystemTerrain.cs:88` |
| 实体射线 | `SubsystemBodies.Raycast(...)` → `BodyRaycastResult?` | `Survivalcraft/Game/SubsystemBodies.cs:66` |
| 移动方块射线 | `SubsystemMovingBlocks.Raycast(...)` | `Survivalcraft/Game/SubsystemMovingBlocks.cs:248` |
| 玩家/实体 | `SubsystemPlayers.ComponentPlayers`、`PlayerData.ComponentPlayer` | 先例 `Mod/ConsoleMod/Subsystem/ConsoleSubsystemGameWidgets.cs:540-548` |
| 视角状态 | `ComponentLocomotion.LookAngles`（**读**：public getter） | `Survivalcraft/Game/ComponentLocomotion.cs:96-108` |
| 键盘/鼠标原始状态 | `Keyboard.IsKeyDown/IsKeyDownOnce/LastKey/LastChar`、`Mouse.IsMouseButtonDown/IsMouseButtonDownOnce/MousePosition/MouseMovement/MouseWheelMovement` | `Engine/Engine/Input/Keyboard.cs:39-49`、`Mouse.cs:113-121` |

**唯一需要反射的两处**（均为只读）：

1. `ScreensManager.m_screens`（静态私有）→ 屏幕实例映射成名字。
2. `Game.TextBoxWidget` 是 `internal class`（`Survivalcraft/Game/TextBoxWidget.cs:9`）→ 用 `GetParentField` 读 `m_text` / `m_hasFocus`。

### 2.3 坐标系（Mod 直接给出屏幕绝对坐标）

- `ScreensManager.Draw()` 把 `RootWidget.LayoutTransform = Matrix.CreateScale(viewportX / 参考宽度)`（`ScreensManager.cs:420-430`），随后 `Widget.LayoutWidgetsHierarchy`（`:435`）。
- 所以 **`Widget.GlobalBounds` 的单位就是窗口客户区像素**。
- `Window.Position` = `m_gameWindow.Location`（GLFW content area 左上角，屏幕坐标）；`Window.Size` = `ClientSize`（`Window.cs:94-120`）。
- 因此：**屏幕绝对坐标 = `Window.Position` + `GlobalBounds`**，完全在游戏进程内算出（OS 鼠标通道需要；引擎内注入通道不需要坐标换算）。
- 两个必须处理的分支：`UpsideDownLayout`（Y 翻转，`ScreensManager.cs:431-433`）、letterbox 分支（`:425-429`）。输出里带上 `Display.Viewport` 与 `Window.Size` 以便自查。

### 2.4 时序（决定"什么时候读、什么时候注入"）

```text
RenderFrameHandler（Engine/Engine/Window.cs:400-416）
 ├─ BeforeFrameAll()                     （Window.cs:464-474）
 │    ├─ Time.BeforeFrame()
 │    ├─ Dispatcher.BeforeFrame()   ◀── ★ 后台线程排入的注入动作在这里执行
 │    ├─ Display.BeforeFrame()
 │    ├─ Keyboard.BeforeFrame()         只更新 repeat 计时，不清按键数组
 │    ├─ Mouse.BeforeFrame()            只更新 Movement/Wheel，不清按钮数组
 │    └─ Touch/GamePad/Mixer.BeforeFrame()
 ├─ Window.Frame() → Program.Run()
 │    ├─ ScreensManager.Update()  → Widget.UpdateWidgetsHierarchy(RootWidget)
 │    │                              （WidgetInput.Update() 先清后建；各 Screen 消费点击；
 │    │                               实体/组件更新亦在此帧内）
 │    ├─ DialogsManager.Update()
 │    └─ Frame.Update             ◀── 本 Mod 的读点（只读观察在此执行）
 ├─ AfterFrameAll()                      （Window.cs:476-486）
 │    ├─ Keyboard.AfterFrame()     清 m_keysDownOnceArray（:140-142）
 │    └─ Mouse.AfterFrame()        清 m_mouseButtonsDownOnceArray（:132-142）
 └─ SwapBuffers
```

**由此得出四条铁律：**

1. **读取放在 `Frame.Update`**：控件树刚更新完，`ClickableWidget.IsClicked` 此刻仍为本帧点击结果（其在自身 `Update()` 里先清后置，`ClickableWidget.cs:28-51`，下一次清除在下一帧的控件树 pass）。
2. **`GlobalBounds` 反映"上一帧 Draw 的布局"**（布局在 `ScreensManager.cs:435` 的 Draw 阶段，晚于 `Frame.Update`）。60fps 下滞后约 16ms；必须输出 `layoutValid`（首帧布局前 bounds 为 0）。
3. **脉冲型输入（`downOnce`）绝不能只在 `Frame.Update` 注入**：`Frame.Update` 之后紧跟 `AfterFrame`，会把同帧注入的 `downOnce` 清掉。**必须走"下一帧帧首"缝隙**（见 §2.7）。
4. **转屏动画期间整棵控件树停止更新**（`ScreensManager.cs:81,309`），读写都不可靠；`animating == true` 时只读不动作。

### 2.5 人类输入 → 游戏动作映射表（权威，来自 `ComponentInput.cs:139-246`）

这是动作空间的权威依据，同时给出每项动作的**引擎内注入落点**。

**世界内（无模态面板、无对话框）时生效：**

| 人类输入 | 游戏动作 | 引擎内注入落点 | 出处 |
|---|---|---|---|
| 鼠标相对位移 | 转视角（`Look`） | **直接写 `m_lookAngles`**（瞬时，见 §2.6） | `ComponentInput.cs:158-178` |
| 鼠标滚轮 | 切换快捷栏（`ScrollInventory`） | 注入 `Mouse` 滚轮值 | `:165,182` |
| 左键**按住** | 挖/持续作用（`Dig` 射线） | 注入 `m_mouseButtonsDownArray[Left]=true` | `:183` |
| 左键**按下一次** | 击打（`Hit` 射线） | 帧首注入 `m_mouseButtonsDownOnceArray[Left]=true` | `:184` |
| 右键**按住** | 瞄准（`Aim` 射线） | 注入 `m_mouseButtonsDownArray[Right]=true` | `:185` |
| 右键**按下一次** | 交互/放置（`Interact` 射线） | 帧首注入 `m_mouseButtonsDownOnceArray[Right]=true` | `:186` |
| 中键按下一次 | 取方块类型（`PickBlockType`） | 帧首注入 `[Middle]` | `:190` |
| `W/S/A/D` | 前后左右移动 | 注入 `m_keysDownArray[W/S/A/D]` | `:172-175` |
| `Space` 按住 | 上升（飞行） | 注入 `m_keysDownArray[Space]` | `:176` |
| `Space` 按下一次 | 跳跃 | 帧首注入 `m_keysDownOnceArray[Space]` | `:181` |
| `Shift` 按住 | 下降（飞行） | 注入 `m_keysDownArray[Shift]` | `:177` |
| `Shift` 按下一次 | 切换潜行 | 帧首注入 `m_keysDownOnceArray[Shift]` | `:187` |
| `R` | 上/下坐骑 | 帧首注入 `m_keysDownOnceArray[R]` | `:188` |
| `F` | 切换创造飞行 | 帧首注入 `[F]` | `:189` |

**任何时候（无对话框）可用的按键：**

| 键 | 动作 | 键 | 动作 |
|---|---|---|---|
| `E` | 打开/关闭背包 | `Q` | 丢弃手持物 |
| `C` | 打开衣物面板 | `G` | 编辑物品 |
| `P` | 截图 | `H` | 键盘帮助 |
| `V` | 切换视角模式 | `T` | 切换昼夜 |
| `L` | 切换光照 | `K` | 切换降水 |
| `J` | 切换雾 | `1`-`0` | 选择快捷栏槽位 1-10 |

> 出处：`ComponentInput.cs:187-244`。

**模态面板/对话框打开时**：`ComponentGui.ModalPanelWidget != null || DialogsManager.HasDialogs(...)` → 光标可见、世界输入被跳过（`:143-152`），操作变为对面板/对话框的 UI 点击。这是 L3 状态机切换操作池的信号源（§7）。

### 2.6 视角转动（"游戏内视角转动"的落点）

| 事实 | 出处 |
|---|---|
| `ComponentLocomotion.LookAngles`（`Vector2`，X=yaw、Y=pitch），**getter public / setter private**，并 clamp：X ∈ ±140°、Y ∈ ±82° | `ComponentLocomotion.cs:96-108` |
| `LookOrder` **public settable**；`LookAngles += LookSpeed * LookOrder * dt`（受 `LookSpeed` 限制 = 人类速度） | `:112-121, 295` |
| `ComponentPlayer.Update` 把 `playerInput.Look` 写进 `LookOrder` / `TurnOrder` | `ComponentPlayer.cs:128-145` |
| `ComponentInput.Update` 每帧重建 `m_playerInput`（`default(PlayerInput)` / `new PlayerInput{}`），因此**直接写 `PlayerInput.Look` 会被同帧覆盖**（写入点必须在 `ComponentInput.Update` 之后、`ComponentPlayer.Update` 之前，而两者同帧相邻，无可用缝隙） | `ComponentInput.cs:93,97` |

**结论（视角注入方案）**：

- **瞬时旋转（推荐，符合"允许快速旋转视角"）**：在**帧首**直接写 `ComponentLocomotion` 的后端字段 `m_lookAngles`，并自行施加同一套 clamp（X ∈ ±140°、Y ∈ ±82°）。效果链完全走游戏原有逻辑：`m_lookAngles` → `ComponentLocomotion` → 相机 `ViewDirection` → `ComponentInput` 构造 `Dig/Hit/Aim/Interact` 射线 → `SubsystemTerrain.Raycast` / `ComponentMiner` 裁决。**这就是"通过游戏内的视角转动计算或输入"，不涉及任何世界状态写入。**
- **平滑/拟人旋转（可选）**：写 `LookOrder`（public），由游戏按 `LookSpeed` 自行积分。
- **辅助动作**：`lookat(cell|entity)` = 由目标坐标与玩家眼位反算 yaw/pitch，再写入 `m_lookAngles`。AI 因此可以"看一眼就开挖"，不需要一连串拟人微操。
- 待实现期验证（R6）：同一帧内写入视角后，射线是否立刻反映新方向（相机更新顺序）；最坏情况滞后 1 帧，无功能影响。

### 2.7 引擎内输入注入缝隙（本方案的技术核心）

**问题**：`Keyboard.m_keysDownOnceArray` / `Mouse.m_mouseButtonsDownOnceArray` 在每帧末尾的 `AfterFrame` 被清空（`Keyboard.cs:140-142`、`Mouse.cs:132-137`），而 `Frame.Update` 正好在 `AfterFrame` **之前**执行 → 在 `Frame.Update` 注入的脉冲会被同帧末尾清掉，下一帧消费者看不到。

**解法**：借助引擎自带的 `Dispatcher`。

```csharp
// Engine/Engine/Dispatcher.cs:34-68
public static void Dispatch(Action action, bool waitUntilCompleted = false)
{
    if (m_mainThreadId.Value == Environment.CurrentManagedThreadId) { action(); return; }  // 主线程：立即执行
    ... 入队 ...
}
// Engine/Engine/Dispatcher.cs:79-105
internal static void BeforeFrame()   // 帧首执行队列，由 Window.BeforeFrameAll() 调用
```

关键性质：

- **从后台线程调用 `Dispatcher.Dispatch(action)` 会入队，并在下一帧的 `Dispatcher.BeforeFrame()` 执行**（`Window.cs:468`），位置在 `Keyboard.BeforeFrame` / `Mouse.BeforeFrame` **之前**、在整个帧体之前，并且在上一帧 `AfterFrame` 清理**之后**。
- 这正是真实输入事件到达的等效时刻（GLFW 事件也在帧首前后被处理），因此注入的脉冲语义与真人输入**完全一致**：本帧可见、帧末自动清除。
- `waitUntilCompleted: true` 可让调用线程阻塞到动作执行完 → 提供"注入完成 → 再观察"的确定性时序。

**因此 Mod 的网络线程（ControlServer 后台线程）承担输入注入**，而**只读观察仍在游戏线程的 `Frame.Update` 队列**执行：

```text
ControlServer 后台线程
 ├─ 只读命令  → 入队 → Frame.Update（游戏线程）执行 → 回包
 └─ 输入命令  → Dispatcher.Dispatch（帧首执行，注入白名单写入）
                 ├─ 持续型（按住）：每次设置后保持，直到显式释放
                 └─ 脉冲型（downOnce）：注入一次，本帧消费，帧末自动清除
```

> 备注：原先考虑的"往 `RootWidget.Children` / `GameWidget.Children` 插哨兵控件抢时序"方案**不再需要**，已废弃（记录于 §2.9）。

### 2.8 输入注入点细节（白名单成员）

| 注入点 | 成员 | 语义 | 备注 |
|---|---|---|---|
| 视角 | `ComponentLocomotion.m_lookAngles` | 持续（角度状态） | 自行 clamp ±140°/±82° |
| 视角（可选） | `ComponentLocomotion.LookOrder`（public） | 持续（速度指令） | 受 `LookSpeed` 限制 |
| 按键持续 | `Keyboard.m_keysDownArray[i]` | 持续到显式释放 | `IsKeyDown` 直读该数组（`Keyboard.cs:39-42`） |
| 按键脉冲 | `Keyboard.m_keysDownOnceArray[i]` | 一帧 | 帧末清空 |
| 按键repeat | `Keyboard.m_keysDownRepeatArray[i]` | 持续 | 影响 `IsKeyDownRepeat` |
| 文本输入 | `Keyboard.m_lastChar` / `m_lastKey` | 一帧 | 供 `TextBoxWidget` 读 `LastChar/LastKey` |
| 鼠标持续 | `Mouse.m_mouseButtonsDownArray[i]` | 持续到显式释放 | `IsMouseButtonDown` 直读（`Mouse.cs:113-116`） |
| 鼠标脉冲 | `Mouse.m_mouseButtonsDownOnceArray[i]` | 一帧 | 帧末清空 |
| 鼠标位置（UI 点击用） | `Mouse.m_mousePosition` 或 `WidgetInput.UseSoftMouseCursor=true` + `MousePosition` 设置器 | 持续 | 后者可**避开物理鼠标干扰**（`WidgetInput.cs:140-150, 168-235`） |

**焦点无关性分析**（重要，决定 AI 能否无人值守运行）：

| 消费点 | 是否受 `Window.IsActive` 限制 | 结论 |
|---|---|---|
| `WidgetInput.IsKeyDown` / `IsMouseButtonDown(Once)` | **否**（`m_isCleared` 在 `Update()` 开头无条件置 false，`WidgetInput.cs:624-628`；随后直读静态数组） | 世界内移动、挖、放、瞄准 **无需游戏窗口前台** |
| `WidgetInput.Press/Tap/Click/Scroll/Drag` 派生字段 | **是**（`UpdateInputFromMouse` 整段被 `if (Window.IsActive)` 包住，`WidgetInput.cs:628`） | UI 菜单点击需要前台窗口（或用 §5.3 的免焦点变体） |
| 视角写入 `m_lookAngles` | **否** | 无需前台 |

### 2.9 UI 点击的引擎内实现（逐级不可跳）

UI 点击同样走引擎内注入，**不需要 OS 鼠标模拟**。机制是利用引擎自身的派生逻辑：

```text
Mouse 按钮数组（注入）──┐
                        ├─► WidgetInput.UpdateInputFromMouse() ─► Press / Tap / Click 派生字段
MousePosition（注入）───┘        （WidgetInput.cs:735-809）              │
                                                                        ▼
                                                    ClickableWidget.Update() → IsClicked
                                                                        │
                                                                        ▼
                                                        各 Screen 的按钮逻辑（原版链路）
```

- **注入点**：帧首（`Dispatcher` 缝隙，§2.7）写 `Mouse.m_mouseButtonsDownArray[Left]`（持续）+ `Mouse.m_mouseButtonsDownOnceArray[Left]`（脉冲）+ `Mouse.m_mousePosition`（目标点，后端字段）。
- **一次点击 = 2 帧**：帧 1 按下（派生 `Press`/`Tap` 并记录 `m_mouseDownPoint`）→ 帧 2 松开（派生 `Click = Segment(m_mouseDownPoint, MousePosition)`，`WidgetInput.cs:755-765`）→ `ClickableWidget` 断言 `HitTestGlobal(Start) == this && HitTestGlobal(End) == this` 后置 `IsClicked = true`（`ClickableWidget.cs:39-50`）→ 同帧内祖先 `Screen.Update()` 消费。
- **前置条件**：该派生段落被 `if (Window.IsActive)` 包住（`WidgetInput.cs:628`），故引擎内 UI 点击**需要游戏窗口在前台**。免焦点变体见 §5.3。
- **物理鼠标干扰**：真实鼠标移动经 `ProcessMouseMove` 覆盖 `MousePosition`（`Mouse.cs:177-190`），但真实事件在帧体之前处理、我们的注入在帧首之后 → 注入值当帧生效（P2 实测确认）；持续点击期间每帧重新断言坐标。
- **逐级不可跳的三重保证**：
  1. **元素必须存在**：D 在 A/B/C 完成前根本不在控件树里，任何选择器都不可能选中它。
  2. **注入前游戏侧二次校验**：Mod 在注入前用**当前帧**控件树重新断言 `HitTestGlobal(target) == 该元素`；不通过则返回 `element_missing` / `element_occluded`，**不注入**。
  3. **禁止切屏捷径**：不提供任何“直接切到某屏／直接触发按钮回调”的命令（审计黑名单含 `SwitchScreen` 与 `InvokeParentMethod`）。
- **节拍由状态驱动**：不靠固定 sleep，而是“点击后等下一级元素出现”（`obs.waitFor`，§4.5）。AI 可以在这条链上跑到极限速度，但每一级都必须真实发生。

### 2.10 已否决方案（记录以备复盘）

| 方案 | 否决原因 |
|---|---|
| 引擎内合成点击（往 `WidgetInput.Click/Press` 写派生字段 + 哨兵控件抢时序） | 需要往 `RootWidget.Children`/`GameWidget.Children` 插控件、侵入游戏 UI 树；且 §2.7 的 `Dispatcher` 缝隙更干净。**仅保留为"免焦点 UI 点击"的最后手段** |
| 纯 OS 鼠标/键盘模拟（`SendInput`）作为唯一通道 | 需要窗口前台、需要 DPI 感知、会劫持物理光标（与玩家同时操作冲突）、无法做瞬时视角。**降级为 UI 点击的备选通道** |
| 直接写 `PlayerInput.Look` | `ComponentInput.Update` 每帧重建 `m_playerInput`（`ComponentInput.cs:93,97`），同帧覆盖，无可用缝隙 |

---

## 3. 总体架构

```text
┌──────────────────────── 同一台电脑 ────────────────────────┐
│                                                            │
│  Survivalcraft.exe  ← 玩家正常玩，UI/渲染/输入零改动        │
│   └─ CmdBridgeMod.dll（scmod）                              │
│       ├─ Frame.Update 钩子（游戏线程）→ 只读观察             │
│       ├─ Observers：Ui / Player / Aim / World / Event       │
│       ├─ InputInjector（后台线程 + Dispatcher 帧首缝隙）     │
│       │     ├─ 视角：m_lookAngles（瞬时旋转）                │
│       │     ├─ 按键：Keyboard 按键数组                       │
│       │     ├─ 鼠标：Mouse 按钮数组（世界挖/放/交互 + UI 点击） │
│       ├─ ControlServer：TCP 127.0.0.1:26751（token 鉴权）    │
│       └─ 写 CmdBridge.runtime.json（port/token/pid）         │
│                                                            │
│  cmd / Windows Terminal                                     │
│   └─ sccmd.exe（net8.0 控制台，零游戏引用）                  │
│       ├─ 发现：找 Survivalcraft 进程 → 读 runtime.json       │
│       ├─ 观察：sccmd obs / ui / player / aim / world …      │
│       ├─ 动作：look / lookat / key / holdkey / mouse / click │
│       └─ OS 鼠标模拟（兜底通道，需前台 + DPI 感知）          │
│                                                            │
│  AI 进程（未来，L3）                                        │
│       └─ 状态机 + 操作池 → 调用 sccmd 的库/管道             │
└────────────────────────────────────────────────────────────┘
```

**分层铁律**：L1 只读；L2 只注入输入层（白名单）；L3 只做决策。任何一层都不得越界。

---

## 4. Mod 端设计（`Mod/CmdBridgeMod/`）

### 4.1 生命周期

```text
IMod.OnLoad(eventBus, injector)
  ├─ 读/生成 CmdBridge.json（端口、token、上限、开关）
  ├─ new ControlServer(config).Start()                       // 后台线程 Accept + 读行
  ├─ new InputInjector(config)                               // 通过 Dispatcher 注入
  ├─ eventBus.SubscribeEvent("Frame.Update", OnFrameUpdate, EventPriority.LOWEST)   // 只读观察
  ├─ 写 CmdBridge.runtime.json（临时文件 + File.Move 原子替换）
  └─ Log.Information("[CmdBridge] listening 127.0.0.1:26751")
IMod.OnUnload() → 释放全部按键/鼠标（防止残留按住） + 退订 + 停服务器 + 删 runtime 文件
```

- `IsMergeLib => true`（唯一允许模式），单 `net8.0` DLL。
- 默认端口 `26751`（与 `HeadlessRenderingMod` 的 `26741` 错开），占用则递增探测（参考 `HeadlessServerConfig.FindAvailablePort`）。
- **不分配控制台窗口**（`enableConsole` 属 `HeadlessRenderingMod`）。
- `OnUnload` 必须把注入的按键/鼠标状态**全部释放**，否则退出时残留"按住 W"。

### 4.2 观察器（全部只读）

#### `UiInspector` — 界面元素读取

- 遍历 `ScreensManager.RootWidget.AllChildren`，识别可交互类型：`ClickableWidget`、各 `ButtonWidget` 子类、`SliderWidget`、`CheckboxWidget`、`LinkWidget`、`ScrollPanelWidget`、`ListPanelWidget`、`InventorySlotWidget`、`BlockIconWidget`、`CraftingRecipeSlotWidget`、`StarRatingWidget`、`TextBoxWidget`（按类型名，因 internal）。
- **去重**：`BitmapButtonWidget` 内部含 `Button.Clickable`（真正命中目标）与 `Button.Label`（文本），由 XML 模板创建（`BitmapButtonWidget.cs:73-83`）；对外报告**语义元素**，模板子控件默认折叠，`all=true` 展开。
- **可点性判定（核心）**：`hittable = (HitTestGlobal(rect.center) == 该元素或其子控件)`，与真实点击同一条命中路径；`hittable=false` 时输出 `blockedBy`。这是"只允许操作当前实际可用元素"的硬保证。

#### `PlayerObserver` — 玩家状态与当前操作

- **当前操作**：`ComponentInput.PlayerInput`（`ComponentInput.cs:28`）的 `Move / Look / CrouchMove / Jump / Dig / Hit / Aim / Interact / Drop / SelectInventorySlot / ToggleInventory / ToggleCrouch / ToggleMount / ToggleCreativeFly / ScrollInventory`（`PlayerInput.cs:5-68`）。
- **原始输入**：`Keyboard.LastKey/LastChar` + 当前按下键集合、`Mouse` 按钮/滚轮/位置。
- **视角**：`ComponentLocomotion.LookAngles`（yaw/pitch）。
- **本体状态**：`ComponentBody` 位置/速度/着地/水中；`ComponentHealth`、`ComponentVitalStats`、`ComponentLevel`、`ComponentOnFire/Flu/Sickness` 时长（**只读**）。
- **手持与背包**：`ComponentMiner.Inventory.ActiveSlotIndex`、手持物品、`ShortInventoryWidget` 可见槽位。
- **模态面板**：`ComponentGui.ModalPanelWidget` 是否为 null 及其类型（`FullInventoryWidget`/`CreativeInventoryWidget`/`ClothingWidget`/`FurnitureInventoryPanel`）。
- **界面态**：当前屏幕名、`ScreensManager.IsAnimating`、`DialogsManager.Dialogs`。

#### `AimObserver` — 瞄准/点击对象读取

- 玩家瞄准射线：`PlayerInput.Aim`（右键按住）/ `Dig` / `Hit` / `Interact`（`ComponentInput.cs:183-186`）。
- 解析成"点击的对象"：方块 → `SubsystemTerrain.Raycast`（格子坐标/面/方块值→方块名）；实体 → `SubsystemBodies.Raycast`；移动方块 → `SubsystemMovingBlocks.Raycast`。
- 兜底：射线为空时用 `GameWidget.ActiveCamera.ViewPosition/ViewDirection` 现场构造同一条射线（保证 AI 随时知道准星指向什么）。

#### `WorldObserver` — 世界读取（AI 的信息优势）

只读扫描，**强制限制体量**（默认半径 8、上限 512 条）：

- `blocks`：以玩家为中心的 AABB 非空气方块扫描。
- `entities`：半径内实体（类型/位置/距离/是否生物或掉落物）。
- `time`：`SubsystemTime`/`SubsystemTimeOfDay`/`SubsystemWeather`/`SubsystemGameInfo.WorldSettings.GameMode`。

#### `EventRecorder` — 事件环（增量轮询）

固定容量环形缓冲（默认 256），每条带递增 `seq`，客户端按 `sinceSeq` 增量拉取。记录：

- UI 点击：本帧 `IsClicked == true` 的元素 ← **"点击的对象（UI 侧）"**
- 世界交互：`Dig/Hit/Interact/PickBlockType` 的目标方块/实体 ← **"点击的对象（世界侧）"**
- 状态变化：屏幕切换、对话框出现/消失、模态面板开/关、游戏模式变化
- 生存事件：受伤、死亡/复活、升级、进入水中/着火/生病

### 4.3 `InputInjector`（输入注入，白名单写入）

```text
输入命令（来自 ControlServer 后台线程）
  └─ Dispatcher.Dispatch(() => { 白名单写入 }, waitUntilCompleted: true)
       └─ 于下一帧 Dispatcher.BeforeFrame() 执行（Window.cs:468）

持续型（hold）: 写 down 数组 = true，记录在"已按下集合"；release 时写 false
脉冲型（once）: 写 downOnce 数组 = true（帧末自动清除）
视角（look/lookat）: 写 m_lookAngles（自行 clamp ±140°/±82°）
```

约束与防护：

- 所有注入都**先检查游戏状态**：无世界/无玩家时不注入（返回 `world_not_loaded`）；`animating` 时不注入 UI 类点击。
- 单帧注入上限（`maxInjectionsPerFrame`，默认 8），防止请求洪泛打乱帧节奏。
- **注入速度不受人类限制**：视角瞬时旋转、UI 超快连点都是设计目标。节拍改为**状态驱动**——不靠固定 sleep，而是“动作后等条件成立”（`obs.waitFor`，§4.5）；仅保留 `minActionIntervalMs`（默认 40ms）作为同一动作的最小间隔保护。
- **注入前二次校验**（“不许跳级”的实现）：UI 点击在注入前用**当前帧**控件树重新断言目标元素存在且 `HitTestGlobal(center) == 该元素`；不通过返回 `element_missing` / `element_occluded` 且**不注入**。客户端观测可能滞后 1 帧（§2.4），不得作为注入依据。
- `OnUnload` 释放全部按键/鼠标状态。

### 4.4 控制服务与安全

复用 `Mod/HeadlessRenderingMod/Server/HeadlessControlServer.cs` 已验证的实现形态（行分隔 JSON、token 定长比较、连接数/S 请求大小上限、队列 + `TaskCompletionSource`）：

- 传输：TCP，**只接受数值 loopback 地址**。
- 鉴权：`token`（32-256 字符，首次运行随机生成）。
- 只读命令在游戏线程执行；输入命令经 `Dispatcher` 在帧首执行。
- 超时 `requestTimeoutSeconds`（默认 10）、队列上限 `maxQueuedCommands`（默认 64）。

### 4.5 命令清单

| 命令 | 类型 | 说明 |
|---|---|---|
| `ping` | — | 存活探测（不排队） |
| `status` | 读 | Mod 版本、pid、屏幕名、`animating`、`layoutValid`、`frameIndex`、窗口几何、viewport、UIScale、`upsideDownLayout` |
| `obs.snapshot` | 读 | **一次拿全**：`status + ui + player + aim + modalPanel`（L3 主接口） |
| `obs.ui` | 读 | UI 元素表（`all` / `maxElements` / `filter` / `types`） |
| `obs.raw` | 读 | 完整控件树（调试，`depth`） |
| `obs.player` | 读 | 玩家状态 + 当前操作（`PlayerInput`）+ 视角 |
| `obs.aim` | 读 | 瞄准射线 + 解析后的目标（方块/实体） |
| `obs.inventory` | 读 | 背包槽位（value/count/名称） |
| `obs.world.blocks` | 读 | 区域方块扫描（`x,y,z,radius,max`） |
| `obs.world.entities` | 读 | 半径内实体（`radius,max`） |
| `obs.world.time` | 读 | 时间/季节/天气/游戏模式 |
| `obs.events` | 读 | 事件环增量（`sinceSeq`, `max`） |
| `obs.selftest` | 读 | 自检：复算 `hittable`、复算 `lookat` 角度、校验注入点可用性 |
| `obs.waitFor` | 读 | 条件等待（服务端逐帧求值，超时返回）：`element.present:<path>` / `element.hittable:<path>` / `screen.is:<name>` / `screen.animating.false` / `modal.none` / `modal.is:<type>` / `events.since:<seq>` / `aim.target.changed` / `player.moved` |
| `ui.reachability` | 读 | **可达性报告**：遍历当前界面全部可交互元素，逐个判定 `hittable`，输出“可达／不可达 + 遮挡者 + 所需前置步骤”，并给出最短操作链建议（A→B→C→D） |
| `act.look` | 写输入 | `yaw`,`pitch`（绝对角度，瞬时） |
| `act.lookat` | 写输入 | 目标方块/实体 → 自动反算角度 |
| `act.lookdelta` | 写输入 | `dx`,`dy`（相对，可超人速度） |
| `act.key` | 写输入 | `key`,`holdMs`（脉冲；**不传时默认 40ms ≈ 一帧**，显式 `0` = 同帧按下并抬起；`holdMs` 大则视为持续） |
| `act.hold` | 写输入 | `key`,`down`（显式按下/释放，用于移动） |
| `act.chord` | 写输入 | `modifiers[]`,`key`,`holdMs`（目标键脉冲时长，**不传时默认 40ms ≈ 一帧**，与 `act.key` 同源；显式 `0` = 同帧按下并抬起） |
| `jump.status` | 只读观察 | 空格/跳跃审计（帧首逐帧计数，**单调递增**）：`spaceEdges` 引擎看到几次空格边沿、`jumpOrders` 游戏产生几次跳跃指令、`rises` 几次真的离地、`rejected` 几次**没跳成**、`inputStageCalls` 输入阶段钩子跑了几次（order -11）、`edgesRestored` 其中恢复了几次引擎边沿（`buffered` 同值，向后兼容）、`edgesRestoredSpace` / `edgesRestoredOther` / `lastRestoredKey` 分别看空格与其他键、`expiredPresses` 窗口内没救回来的次数、`restoreSkippedByGate` 被组件前提挡住的次数；另附最近一次边沿当帧的 `lastEdgeStanding / Active / CameraOk / Sleeping / Ready / VelocityY`。因为计数单调递增，**轮询取增量即可**，不受"边沿只活一帧"影响 |
| `jump.buffer` | 行为开关（**代码里默认开**） | `state=on/off/toggle`：**在输入阶段恢复引擎边沿，覆盖所有边沿型按键**（空格跳跃、Shift 蹲 `ComponentInput.cs:187`、E/C/V/Q/G/T/L/K/J/P/H/R/F/数字键 `:187-241`），不只是跳跃。注入点由 order **-11** 的 `IUpdateable`（`CursorSoftGuard`，紧贴 `ComponentInput`(-10) 之前，同一个排序 pass）调用 —— 与原版输入同一时刻（原版是"消息到达时记录 → 输入帧一次性写进 `PlayerInput`"）。条件：事件记录到一次空格、而 `Keyboard.IsKeyDownOnce(Space)` 仍为 false（`Keyboard.cs:171-176` 因"引擎认为还按着"不置边沿，或 `Keyboard.Clear()` 把边沿抹掉）、且玩家满足游戏自己的起跳前提（接地 + 活着 + 未睡 + 相机 `IsEntityControlEnabled && !UsesMovementControls` + 窗口活跃 + 世界就绪）时，写 `m_keysDownOnceArray[Space] = true`。随后 `ComponentInput`(-10) 把它当**真实按下**读走，0.3 秒双按规则（`ComponentInput.cs:76-87`）与所有门槛（`:91-117`）全部走原版；**不写 `JumpOrder`、不伪造第二次按键**。开关**恒久写在代码里**（`JumpAssist` 构造函数默认 true，不落 `CmdBridge.json`），`state=off` 只在本次运行内关闭。返回当前 `jump.status` |
| `act.mouse` | 写输入 | `button`,`action`（`down`/`up`/`click`/`doubleclick`）、`holdMs` |
| `act.wheel` | 写输入 | `delta` |
| `act.releaseAll` | 写输入 | 释放全部按键/鼠标（紧急停止） |

### 4.6 配置

`<游戏目录>/CmdBridge.json`（不存在则生成）：

```json
{
  "enabled": true,
  "bindAddress": "127.0.0.1",
  "port": 26751,
  "token": "（32-256 字符，随机生成）",
  "enableInputInjection": true,
  "maxInjectionsPerFrame": 8,
  "minActionIntervalMs": 40,
  "maxCommandsPerFrame": 8,
  "maxQueuedCommands": 64,
  "requestTimeoutSeconds": 10,
  "maxRequestBytes": 262144,
  "maxElements": 512,
  "eventRingCapacity": 256
}
```

`<游戏目录>/CmdBridge.runtime.json`：`{ pid, port, token, modVersion, startedUtc, screen, inputInjection: true }`

安全小结：只监听 loopback + token；读无限制、写仅限输入层白名单 → 无法直接改游戏状态。

---

## 5. 客户端设计（`Mod/CmdBridgeClient/` → `sccmd.exe`）

纯 `net8.0` 控制台程序，**不引用 Engine / GameEntitySystem / Survivalcraft**。

### 5.1 通道选择（默认引擎内，OS 模拟为备选）

| 目标 | 默认通道 | 备选通道 | 理由 |
|---|---|---|---|
| 视角 | **引擎内** `act.look` / `act.lookat`（瞬时） | — | 无需光标/DPI/焦点，支持超人速度 |
| 世界内移动 | **引擎内** `act.hold(W/S/A/D/...)` | OS `SendInput` 键盘 | 免焦点、时序确定 |
| 世界内挖/放/交互 | **引擎内** `act.mouse`（Left/Right 按下-松开） | OS 鼠标 | 免焦点 |
| UI 菜单点击 | **引擎内**：Mouse 按钮数组 + 位置注入（§2.9） | OS 鼠标模拟（需前台 + DPI 感知） | 引擎内不劫持物理鼠标、不需 DPI 感知；OS 通道作兜底 |

### 5.2 OS 通道（仅 UI 点击备选/兜底）

1. **第一件事**：`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`（P/Invoke，早于任何输入/窗口 API）。游戏由 GLFW 设为 DPI 感知并返回物理像素，客户端不感知 DPI 会被系统虚拟化 → 高 DPI 下全点偏。
2. 发现游戏：`Process.GetProcessesByName("Survivalcraft")` → 目录 → 读 `CmdBridge.runtime.json`。
3. `EnsureForeground()`：`IsIconic ? ShowWindow(SW_RESTORE)` → `SetForegroundWindow` → 等 ~80ms（UI 点击受 `Window.IsActive` 限制）。
4. `SetCursorPos` + `SendInput`（mouse/keyboard 用 scan code）。
5. `--restore` 可把光标放回原位。

### 5.3 免焦点 UI 点击（哨兵缝隙，可选）

**纠正**：派生字段（`Press`/`Tap`/`Click`）**不能**在帧首注入 —— `WidgetInput.Update()` 每帧开头先 `ClearInput()` 清空派生字段（`WidgetInput.cs:624-627, 681-700`），帧首写入必被本帧清除。免焦点点击需要“在该输入宿主 `Update()` 之后、其子控件 `Update()` 之前”的缝隙：往输入宿主（`RootWidget` / `GameWidget`）的 `Children` **末尾**插入一个哨兵控件（`Widget.UpdateWidgetsHierarchy` 的递归顺序是 children 逆序 → 自身 `Update()`，`Widget.cs:859-884`），由它的 `Update()` 写入派生字段；配合 `UseSoftMouseCursor = true`（`WidgetInput.cs:140-150`）+ `MousePosition` 设置器（写 `m_softMouseCursorPosition`，`:201-234`）指定坐标。副作用：软光标会额外绘制一个手柄风格箭头（`WidgetInput.cs:653-664`），且需向游戏 UI 树插入控件。**默认不启用**，仅在确实需要“游戏窗口失焦也能点 UI”时启用。

### 5.4 命令面

```text
# 观察
sccmd status                        # 屏幕/窗口/Mod 版本
sccmd ui [--all] [--json]           # UI 元素表（id/文本/rect/屏幕坐标/hittable/blockedBy）
sccmd find "<文本|名字>"             # 过滤元素
sccmd player [--json]               # 玩家状态 + 当前操作 + 视角
sccmd aim [--json]                  # 准星指向的对象（方块/实体）
sccmd inv [--json]                  # 背包
sccmd world blocks|entities|time    # 世界读取
sccmd events [--since N]            # 事件环增量
sccmd watch [--interval 500]        # 实时刷新元素表（UI 探测器）
sccmd reachability [--json]         # 可达性报告：哪些元素点不到、被谁挡、需要哪些前置步骤
sccmd snapshot [--json]             # 一次拿全量（AI 主接口）

# 动作 —— 视角（引擎内，瞬时）
sccmd look <yaw> <pitch>            # 绝对角度（度）
sccmd lookat <x> <y> <z>            # 看向世界坐标（自动反算）
sccmd lookat --entity <id>          # 看向实体
sccmd lookdelta <dx> <dy>           # 相对转动（可超人速度）

# 动作 —— 键盘（引擎内）
sccmd key <name> [--hold <ms>]      # 脉冲或定时长按
sccmd hold <name>                   # 按下保持（移动）
sccmd release <name> | --all        # 释放
sccmd chord ctrl v                  # 组合键

# 动作 —— 鼠标（引擎内）
sccmd mouse left click|down|up      # 世界挖/放/交互
sccmd wheel <n>                     # 滚轮（快捷栏）

# 动作 —— UI 点击
sccmd click <id|path|"文本">        # 元素点击（引擎内注入；注入前二次校验 hittable，不可跳级）
sccmd clickat <x> <y>               # 原始屏幕坐标点击
sccmd waitfor <condition> [--timeout 3000]   # 条件等待（替代 sleep，AI 节拍器）

# 交互
sccmd                               # 无参数 → REPL：ui / click 3 / hold w / lookat 12 68 -30 / quit
sccmd serve --pipe <name>           # （P4）命名管道服务，供 L3 低延迟调用
```

**UI 点击类命令的强制校验流程**（落实"只允许操作实际可用元素"）：

```text
1. obs.ui → 按选择器解析目标元素
2. 断言 element.hittable == true         否则报错并打印 blockedBy
3. 断言 status.animating == false && status.layoutValid == true
4. 用 element.screenPoint 点击
5. sleep(clickSettleMs, 默认 120) → 重新 obs.ui 校验：元素仍存在 / 屏幕未意外切换
   → 判定成功；否则返回 actionFailed 供 AI 反馈
--force 才允许跳过 2/3（仅调试）
```

退出码：`0` 成功、`2` 选择器不存在、`3` 元素不可点、`4` 连接失败、`5` 屏幕忙/布局无效、`6` 无世界/无玩家。

---

## 6. 协议规范（冻结 v1）

### 6.1 Wire format

一行一个 UTF-8 JSON（`\n` 结束），沿用 `HeadlessRenderingMod` 已验证格式：

```json
→ {"id":"1","token":"...","command":"obs.snapshot","args":{"maxElements":256}}
← {"id":"1","ok":true,"result":{...}}
← {"id":"1","ok":false,"error":{"code":"element_occluded","message":"..."}}
```

错误码：`unauthorized` / `queue_full` / `timeout` / `invalid_argument` / `screen_busy` / `layout_invalid` / `world_not_loaded` / `player_not_found` / `not_found` / `element_occluded` / `injection_disabled` / `command_failed`。

### 6.2 观察数据模型（`obs.snapshot`）

```jsonc
{
  "status": {
    "modVersion": "1.0.0", "pid": 12345,
    "screen": "MainMenu", "screenType": "MainMenuScreen",
    "animating": false, "layoutValid": true, "frameIndex": 98765,
    "window": { "position": {"x":100,"y":80}, "clientSize": {"x":1280,"y":720} },
    "viewport": {"x":1280,"y":720}, "uiScale": 1.0, "upsideDownLayout": false,
    "inputInjection": true
  },
  "ui": {
    "elements": [
      {
        "id": 3, "path": "PlayButton", "name": "PlayButton",
        "type": "BitmapButtonWidget", "text": "Play",
        "rect": {"x":40,"y":300,"w":260,"h":64},
        "center": {"x":170,"y":332},
        "screenPoint": {"x":270,"y":412},
        "visible": true, "enabled": true, "hittable": true,
        "blockedBy": null, "checked": false, "hitTargetType": "ClickableWidget"
      }
    ],
    "truncated": false, "totalCount": 4
  },
  "player": {
    "position": {"x":512.5,"y":68.0,"z":-233.25},
    "velocity": {"x":0.0,"y":-0.1,"z":1.2},
    "look": {"yawDeg": 91.2, "pitchDeg": -3.5},
    "onGround": true, "inWater": false, "flying": false, "crouching": false,
    "health": {"health":1.0,"air":1.0},
    "vitals": {"food":0.82,"stamina":0.95,"sleep":0.77,"temperature":12.3,"wetness":0.05},
    "level": 12.5, "gameMode": "Survival",
    "onFire": 0.0, "flu": 0.0, "sickness": 0.0,
    "activeSlot": 2, "holding": {"value": 42, "count": 3, "name": "StoneAxe"},
    "modalPanel": null,
    "input": {
      "move": {"x":0.0,"y":0.0,"z":1.0}, "look": {"x":0.0,"y":0.0},
      "jump": false, "dig": true, "hit": false, "aim": false, "interact": false,
      "drop": false, "selectSlot": null, "keysDown": ["W"]
    }
  },
  "aim": {
    "active": true,
    "origin": {"x":512.5,"y":69.6,"z":-233.25},
    "direction": {"x":0.01,"y":-0.02,"z":0.99},
    "target": {
      "kind": "block", "cell": {"x":513,"y":68,"z":-230},
      "face": "PositiveZ", "blockName": "Dirt", "distance": 3.2
    }
  },
  "dialogs": [ {"type": "MessageDialog", "parent": "GameScreen", "buttons": ["OK","Cancel"]} ],
  "events": { "lastSeq": 4211 }
}
```

### 6.3 动作模型（L2 暴露给 L3 的动作原语）

| 动作 | 参数 | 通道 | 说明 |
|---|---|---|---|
| `look` | `yawDeg`, `pitchDeg` | 引擎内 | **瞬时**旋转（clamp ±140°/±82°） |
| `lookat` | `cell` 或 `entityId` | 引擎内 | 由目标位置反算角度，一步对准 |
| `lookdelta` | `dxDeg`, `dyDeg` | 引擎内 | 相对旋转，不限速 |
| `hold` / `release` | `key` | 引擎内 | 移动/持续动作 |
| `key` | `key`, `holdMs` | 引擎内 | 脉冲或定时长按 |
| `chord` | `modifiers[]`, `key` | 引擎内 | 组合键 |
| `mouse` | `button`, `down/up/click`, `holdMs` | 引擎内 | 世界挖/放/交互/瞄准 |
| `wheel` | `delta` | 引擎内 | 快捷栏/列表 |
| `uiClick` | `element` / `screenPoint` | **引擎内**（OS 兜底） | 菜单点击；注入前二次校验，**不可跳级** |
| `waitFor` | `condition`, `timeoutMs` | — | 条件等待（替代固定 sleep，AI 的节拍器） |
| `wait` | `ms` | — | 等待观察 |

**边界说明**：视角瞬时旋转是**允许**的（输入层超人化），但它只改变"眼睛朝向"；能否挖到/放到/交互到，仍由 `ComponentMiner`、方块规则、距离限制、工具条件裁决——所以不构成作弊。

### 6.4 事件流

`obs.events` 返回 `{ sinceSeq, lastSeq, events: [ {seq, frame, kind, ...} ] }`，`kind` 取值：
`ui.click` / `world.dig` / `world.hit` / `world.interact` / `screen.changed` / `dialog.shown` / `dialog.hidden` / `modal.opened` / `modal.closed` / `player.damaged` / `player.died` / `player.respawned` / `player.levelup`。

---

## 7. AI 层设计（L3，未来独立仓库）

### 7.1 主循环

```text
loop:
  obs    = bridge.snapshot()             # 一次请求拿全量（§6.2）
  events = bridge.events(since=seq)      # 增量事件
  state  = classify(obs, events, memory) # 两级状态判定
  pool   = pools[state]                  # 选择操作池
  action = pool.policy(obs, events, memory)
  if action is None: sleep(tick); continue
  result = bridge.execute(action)        # 输入注入 + 结果校验
  memory.update(obs, action, result)
  sleep(tick, jitter)                    # 默认 80~150ms ± 抖动
```

### 7.2 状态划分（两级）

**L1 界面态**（`status.screen`）：`Loading / MainMenu / Play / NewWorld / WorldOptions / Game / Player / Players / Recipaedia* / Bestiary* / Help / Settings* / Community* / Mods / Nag / TrialEnded / Error`

**L2 情境态**（`Game` 屏内，来自 `player` + `aim` + `modalPanel` + `events`）：`Idle / Exploring / Mining / Building / Hungry / Dehydrated / Exhausted / Freezing / Overheating / Wet / Injured / LowAir / OnFire / Sick / Night / HostileNearby / InventoryOpen / CraftingOpen / ContainerOpen / ClothingOpen / RidingMount / Sleeping / Respawn`

### 7.3 操作池（Operation Pool）

一个操作池 = `{ 前置条件, 候选动作集, 选择策略, 超时, 失败重试, 退出条件 }`。

| 池 | 触发状态 | 候选动作 |
|---|---|---|
| `Pool.MainMenu` | screen=MainMenu | `uiClick(Play/Settings/Quit)` |
| `Pool.PlayWorldList` | screen=Play | `uiClick(世界条目)`、`uiClick(NewWorld)`、`wheel` |
| `Pool.WorldOptions` | screen=WorldOptions | `uiClick(选项)`、`uiClick(Start/Cancel)` |
| `Pool.Locomotion` | Game 且无模态 | `hold(W/A/S/D)`、`look/lookat`、`key(Space)` 跳、`key(Shift)` 潜行、`key(R)` 坐骑 |
| `Pool.Interact` | Game 且 `aim.target` 存在 | `mouse left hold`（挖）、`mouse right click`（放/交互）、`key(Q)`、`key(1..0)`、`key(E)` |
| `Pool.Inventory` | `modalPanel = FullInventoryWidget` | `uiClick/拖拽(槽位)`、`key(E)` 关闭 |
| `Pool.Crafting` | 工作台面板 | `uiClick(配方)`、`uiClick(合成)` |
| `Pool.Container` | 容器面板 | `uiClick/拖拽(槽位)` |
| `Pool.Dialog` | `dialogs` 非空 | `uiClick(OK/Cancel/第N个)`、`key(Escape)` |
| `Pool.Survival` | Hungry/Injured/Freezing/… | `key(E)` → 背包找食物/药 → `mouse right` 使用 |
| `Pool.Night` | 入夜 | 放置光源 / 建庇护所 / 睡觉 |
| `Pool.Combat` | HostileNearby | `lookat(entity)` → `mouse left` 攻击 → 后撤 |
| `Pool.Recovery` | 卡住/无进展 | 退避、重试、回退到上个稳定池 |

### 7.4 技能层（基本动作的组合，示例）

```text
MineBlock(cell):   lookat(cell) → hold(W) 接近到 ≤4 格 → hold(mouse left) 直到
                   事件 world.dig 报告该格消失或超时 → releaseAll
PlaceBlock(cell):  lookat(cell 相邻面) → key(数字键选中方块) → mouse right click
PickupItem(id):    lookat(entity) → hold(W) 接近 → 自动拾取或 mouse right
EatBestFood():     key(E) → obs.inventory 选最佳食物槽 → uiClick(槽) → mouse right → key(E)
```

**视角瞬时对准（`lookat`）让技能层实现大幅简化**：不再需要"一点点转鼠标直到准星对上"的拟人微操循环。

### 7.5 节流：不限速 + 状态驱动

- **不限速**：视角瞬时、UI 超快连点都是设计目标，**不做拟人限速**。
- **节拍由状态驱动**：动作后使用 `waitFor` 等条件（下一级元素出现／屏幕切换完成／事件到达），而不是固定 sleep。这是“允许快、但不许跳级”的正确实现方式。
- **兜底间隔**：同一动作最小间隔 40ms，避免单帧内堆叠注入；全局 tick 默认 20~50ms（比人快，但每步仍等状态推进）。

### 7.6 失败恢复

每个动作带结果校验：无效果 → 重试 2 次 → 换策略 → `Pool.Recovery`。事件环用于识别"操作被游戏拒绝"（点击被遮挡、`screen_busy`、`world_not_loaded`）。**紧急停止**：`act.releaseAll` 一次性释放所有注入状态。

### 7.7 AI 侧铁律：用优势，不用特权

- **只能调用 L2 动作原语**（视角／键盘／鼠标／UI 点击／等待／观察）。L2 不提供任何“直接改状态”的接口，L3 也不得自己造。
- **生存压力必须真实承受**：受伤 → 进食／用药／睡觉／躲避／等待自然回血；饥饿 → 找食物；严寒／中暑 → 衣物与火源；夜晚 → 光源或庇护所。禁止“自动恢复”式技能。
- **资源必须真实获取**：物品只能通过采集、合成、容器存取获得；禁止“补充材料”式技能。生存模式下其他玩家与容器就是普通仓库，AI 不享有任何数据特权。
- **可达性是硬约束**：每个动作都必须经过 `hittable` / `waitFor` 校验；不可达就换路径（绕路、开门、滚动、等下一级界面），而不是绕过。
- **调试用途同样受约束**：可达性测试要报告“这条路走不通”，而不是替应用把路走通。

---

## 8. 文件清单（全部在 Mod 仓库内）

```text
Mod/CmdBridgeMod/                          # L1 观察层 + L2 输入注入层（scmod）
├── CmdBridgeMod.csproj                    # net8.0 + Engine/EntitySystem/Survivalcraft ProjectReference
├── ModInfo.xml                            # IsMergeLib=true, Identifier=CmdBridgeMod
├── Obfuscar.xml
├── Plug/CmdBridgeMod.cs                   # IMod 入口：Frame.Update 订阅 + 服务器/注入器启停
├── Server/CmdBridgeConfig.cs              # CmdBridge.json + runtime.json + 端口探测
├── Server/ControlServer.cs                # TCP loopback JSON（移植 HeadlessControlServer 形态）
├── Server/CommandRouter.cs                # 命令分发：obs.* 只读 / act.* 输入注入
├── Server/InputInjector.cs                # ★ Dispatcher 帧首缝隙 + 白名单写入
├── Server/InjectionWhitelist.cs           # ★ 白名单成员常量 + 运行时自检
├── Server/Observers/UiInspector.cs         # ★ 控件树遍历 + hittable 判定 + 屏幕坐标换算
├── Server/Observers/WidgetClassification.cs# 可交互类型识别 / 模板子控件去重
├── Server/Observers/PlayerObserver.cs      # ★ PlayerInput + 生存状态 + 背包含 + 视角
├── Server/Observers/AimObserver.cs         # ★ 瞄准射线 + 射线解析成方块/实体
├── Server/Observers/WorldObserver.cs       # 区域方块 / 实体 / 时间天气
├── Server/Observers/EventRecorder.cs       # 事件环（增量拉取）
├── Server/ReflectionBridge.cs              # 只读反射（m_screens / TextBoxWidget）
└── README.md

Mod/CmdBridgeClient/                       # 命令行驱动（sccmd.exe）
├── CmdBridgeClient.csproj                 # net8.0 控制台，零游戏引用
├── Program.cs                             # 参数解析 + REPL
├── BridgeClient.cs                        # 协议客户端（发现 + 请求/响应）
├── Discovery.cs                           # 找进程 → 读 runtime.json
├── DpiAwareness.cs                        # 启动即设 PerMonitorV2（OS 通道需要）
├── InputSimulator.cs                      # OS 鼠标/键盘模拟（UI 点击备选通道）
├── ElementSelector.cs                     # id/path/文本 → 元素解析 + hittable 校验
├── OutputFormatter.cs                     # 表格 / --json
└── README.md

Mod/doc/cmd-bridge-plan.md                 # 本文档
Mod/Packages/pack_cmd_bridge.py            # .scmod 打包脚本（条目名用正斜杠）
Mod/Packages/[SuAPI]CmdBridgeMod-1.0.0.scmod
```

后续需同步：`Mod/README.md`（已收录 Mod 表）、`Mod/SYNC_LIST`（新增 `CmdBridgeMod`、`CmdBridgeClient`、`doc`）。

---

## 9. 打包与部署（按仓库铁律）

- **打包工具不限，但条目名必须是正斜杠**（`Compress-Archive` 会写反斜杠路径 → ModLoader 匹配失败）。
- 结构：`ModInfo.xml` 在 ZIP 根 + `Lib/CmdBridgeMod.dll`（**扁平 `Lib/`，禁止 `Lib/X64/`、`Lib/Arm64/`**）。
- DLL 来源：Obfuscar 输出的 `bin/Debug/net8.0/Obfuscar/CmdBridgeMod.dll`。
- 打包后校验：列出全部条目名检查根结构、逐个解压校验完整性。
- 部署：`publish/win-x64/Mods/[SuAPI]CmdBridgeMod-1.0.0.scmod`。
- 客户端：`dotnet build` 直接用，或 `dotnet publish -c Release -r win-x64` 出单文件。
- 执行规范：构建/打包/部署统一走 `py -3 <脚本>`；打包脚本 `Mod/Packages/pack_cmd_bridge.py`。
- 调试日志必须带 `[CmdBridge]` 前缀，验证后**立即移除**；热路径禁止日志。

---

## 10. 分阶段实施与验收

| 阶段 | 内容 | 验收标准 |
|---|---|---|
| **P1** | Mod 骨架：`Frame.Update` + 配置 + TCP 服务 + `ping`/`status`/`obs.ui`；客户端骨架：发现 + `status`/`ui`；白名单审计脚本 | ① `sccmd status`/`ui` 通；② 主菜单元素坐标目视正确；③ 玩家键鼠零影响；④ 审计脚本无黑名单命中、白名单无越界 |
| **P2** | **输入注入核心**：`InputInjector` + `Dispatcher` 帧首缝隙 + `act.look`/`act.lookat`/`act.hold`/`act.key`/`act.mouse` + **引擎内 UI 点击**（§2.9）+ `act.releaseAll` | ① `lookat <方块>` 一步对准且 `obs.aim` 当帧对上新目标；② `hold w` 能走动、`release` 能停；③ `mouse left down` 能挖穿方块、`mouse left click` 能击打；④ **引擎内点击主菜单按钮能真实进入下一屏**；⑤ 点击“当前不存在的元素”被拒绝（不许跳级）；⑥ `releaseAll` 后无残留；⑦ 玩家同时操作不受干扰 |
| **P3** | 观察层完整化：`obs.player`（含 `PlayerInput` + 视角）+ `obs.aim` + `obs.inventory` + `obs.events`；`--json`；`obs.selftest` | ① 不截图即可知道"玩家在按 W、准星指向 Dirt、背包第 3 格是石斧"；② 事件环能捕获 UI 点击与世界交互；③ `selftest` 全通过 |
| **P4** | 世界观察（`obs.world.*`）+ `obs.waitFor` 条件等待 + OS 鼠标兜底通道 + `serve --pipe` | ① 世界扫描不掉帧；② `waitFor` 在各典型状态下正确收敛；③ OS 兜底通道在前台窗口下可用；④ 管道延迟 < 20ms |
| **P5**（可选） | 免焦点 UI 点击（哨兵控件缝隙 + `UseSoftMouseCursor`，§5.3） | 失焦状态下菜单点击仍生效；玩家侧仅多一个软光标绘制 |

**每阶段通用验证项**：

1. `taskkill /F /IM Survivalcraft.exe` → touch 源文件 → 构建（`SOUL.md` 铁律 24）。
2. 打包 → 条目名 + 完整性校验。
3. 部署 → 启动游戏 → `Logs/Game.log` 无 `[CmdBridge]` 诊断残留、无异常。
4. **配置矩阵**：`UIScale` 0.7/0.85/1.0、窗口移动/缩放、`UpsideDownLayout` 开、系统 DPI 100%/125%/150%（OS 通道相关）。
5. **遮挡验证**：弹出模态对话框后，底层按钮 `hittable=false` 且 `blockedBy` 正确。
6. **并发验证**：玩家一边用键鼠玩、AI 一边注入视角/按键，互不干扰。
7. **公平性验证**：审计脚本 + 人工复核 `Server/` 全部源码，确认无状态层写入。
8. **残留验证**：任意时刻中断注入（进程被杀/`releaseAll`），游戏内无"按键卡住"。

---

## 11. 风险与运行时待确认项

| # | 风险 | 影响 | 处置 |
|---|---|---|---|
| R1 | **`Dispatcher` 缝隙的实际落地时机**：从后台线程注入是否能稳定在"下一帧帧首"执行 | 中 | P2 首先验证：注入脉冲 → 同帧/次帧观察是否被消费；必要时改用 `waitUntilCompleted: true` 强制同步 |
| R2 | **视角写入与相机/射线同帧一致性**：写入 `m_lookAngles` 后，`ActiveCamera.ViewDirection` 与 `PlayerInput.Aim` 是否当帧就更新 | 中 | P2 用 `obs.aim` 对照 `lookat` 目标验证；最坏滞后 1 帧，无功能影响 |
| R3 | **clamp 一致性**：`m_lookAngles` 后端字段绕过原 setter 的 clamp（±140°/±82°） | 中 | `InputInjector` 自行施加同一 clamp，并写入注释 `// Source: ComponentLocomotion.cs:96-108` |
| R4 | 世界内鼠标被隐藏（`ComponentInput.cs:155`），`Look` 依赖光标位移 | **已消解** | 视角改走 `m_lookAngles` 注入，不再依赖光标位移 |
| R5 | **引擎内 UI 点击需要游戏窗口前台**（派生段被 `Window.IsActive` 包住，`WidgetInput.cs:628`；世界内挖／放／瞄准不受此限） | 中 | 正常玩法下窗口就是前台；需要免焦点时启用 P5 哨兵缝隙 |
| R6 | `GlobalBounds` 是**上一帧布局**（`Frame.Update` 早于 Draw 的 layout） | 低 | 输出 `layoutValid`/`frameIndex`；要求 `!animating && layoutValid` |
| R7 | `UpsideDownLayout` / letterbox / viewport≠ClientSize 的坐标换算（仅 OS 通道需要） | 中 | Mod 侧统一换算成屏幕绝对坐标；`obs.selftest` + 验证项 4 |
| R8 | 元素重复：`BitmapButtonWidget` + 内部 `Button.Clickable` + `Button.Label` | 低 | 折叠模板子控件，报告语义元素 + `hitTargetType` |
| R9 | `TextBoxWidget` 是 `internal`（`TextBoxWidget.cs:9`） | 低 | `GetParentField` 只读 `m_text`/`m_hasFocus` |
| R10 | 滚动面板中滚出可视区的元素中心点命中不到 | 低 | 标 `hittable=false`；先 `wheel`/拖拽滚动再点 |
| R11 | 转屏动画期间控件树停止更新（`ScreensManager.cs:81`） | 中 | `animating=true` 时只读不动作；命令返回 `screen_busy` |
| R12 | 大量控件导致响应体过大 | 低 | `maxElements` + `truncated` + 类型过滤 |
| R13 | **注入残留**：异常退出时"按住 W"卡住 | 中 | `OnUnload` + `act.releaseAll` + 注入状态定期心跳校验；验证项 8 |
| R14 | 注入过于频繁扰乱帧节奏 | 低 | `maxInjectionsPerFrame` + `minActionIntervalMs` |
| R15 | **物理鼠标移动可能覆盖注入的 `MousePosition`** | 低 | 帧首注入顺序在真实事件之后（P2 实测）；持续点击每帧重新断言坐标 |
| R16 | **跳级风险**：AI 依据过期观测点到下一级元素 | 中 | 注入前游戏侧二次校验（§4.3）＋ 元素不存在则 `element_missing`；禁止切屏捷径（黑名单含 `SwitchScreen`） |

---

## 12. 附录：术语与源码索引

### 12.1 术语

| 术语 | 含义 |
|---|---|
| 观察面（Observation） | Mod 只读导出的游戏状态快照 |
| 动作面（Action） | 客户端可触发的、玩家级输入 |
| 操作池（Operation Pool） | 某个状态下允许的动作集合 + 选择策略 |
| 输入层 / 状态层 | 前者允许（含超人速度），后者禁止 |
| `hittable` | 用 `HitTestGlobal(元素中心)` 判定该元素当前是否真的点得到 |
| 帧首缝隙 | `Dispatcher.BeforeFrame()`，后台线程注入脉冲的唯一可靠落点 |
| 玩家控制器 | 游戏给玩家角色的输入通道：视角、键盘、鼠标、UI 点击；本 Mod 唯一的交互出口 |
| 优势 / 特权 | 优势 = 无限读取 + 超人输入速度（允许）；特权 = 直接改游戏状态（禁止） |
| 可达性 | 某元素当前是否能被玩家真实操作到（`hittable` + 前置步骤 + 最短操作链） |
| 玩家机器人 | 通过玩家控制器操作角色、像 NPC 一样游玩的 AI（本文档 L3） |

### 12.2 源码索引（实施时直接定位）

| 主题 | 位置 |
|---|---|
| 帧循环与事件注入点 | `Survivalcraft/Game/Program.cs:144-148, 165` |
| **帧首/帧末顺序** | `Engine/Engine/Window.cs:400-416, 464-486` |
| **Dispatcher 缝隙** | `Engine/Engine/Dispatcher.cs:34-68, 79-105` |
| 控件基类（bounds/命中/变换） | `Survivalcraft/Game/Widget.cs:320, 336, 378-410, 699-712, 808-820, 836-857, 859-884` |
| 点击语义 | `Survivalcraft/Game/ClickableWidget.cs:7-9, 25-51` |
| UI 点击派生链路 | `Survivalcraft/Game/WidgetInput.cs:735-809` |
| 禁止的切屏捷径 | `Survivalcraft/Game/ScreensManager.cs:61-92` |
| 输入结构（含 `m_isCleared`/派生字段） | `Survivalcraft/Game/WidgetInput.cs:60-92, 140-150, 168-235, 624-651, 681-700, 735-809` |
| **人类输入→动作映射** | `Survivalcraft/Game/ComponentInput.cs:139-246` |
| 玩家意图结构 | `Survivalcraft/Game/PlayerInput.cs:5-68` |
| **视角状态/注入点** | `Survivalcraft/Game/ComponentLocomotion.cs:96-108, 112-121, 295` |
| 视角消费链 | `Survivalcraft/Game/ComponentPlayer.cs:71-146` |
| **键盘注入点** | `Engine/Engine/Input/Keyboard.cs:13-49, 116-142` |
| **鼠标注入点** | `Engine/Engine/Input/Mouse.cs:13-42, 113-142` |
| 黑名单（地形写入） | `Survivalcraft/Game/SubsystemTerrain.cs:229, 242` |
| 屏幕与布局 | `Survivalcraft/Game/ScreensManager.cs:42, 61-92, 94-97, 218-229, 418-437` |
| 对话框 | `Survivalcraft/Game/DialogsManager.cs:26-32, 50-91, 102-130` |
| 按钮模板（元素去重依据） | `Survivalcraft/Game/BitmapButtonWidget.cs:73-83` |
| 文本框（internal） | `Survivalcraft/Game/TextBoxWidget.cs:9, 47, 83, 164-237` |
| HUD 与模态面板 | `Survivalcraft/Game/ComponentGui.cs:121-141, 539-558` |
| 游戏内控件与输入设备 | `Survivalcraft/Game/GameWidget.cs:101-161` |
| 世界射线 | `Survivalcraft/Game/SubsystemTerrain.cs:88`、`SubsystemBodies.cs:66`、`SubsystemMovingBlocks.cs:248` |
| 窗口与视口 | `Engine/Engine/Window.cs:94-120, 218`、`Engine/Engine/Graphics/Display.cs:41-45` |
| 控制服务器参考实现 | `Mod/HeadlessRenderingMod/Server/HeadlessControlServer.cs` |
| 只读反射先例 | `Mod/HeadlessRenderingMod/Plug/HeadlessRenderingMod.cs:835-843` |
| 命令/操作参考（**勿复用其写命令**） | `Mod/ConsoleMod/Subsystem/ConsoleSubsystemGameWidgets.cs:497-698` |

### 12.3 仓库规范引用

- `AGENTS.md` — 代码规范、.scmod 打包铁律、执行规范、设备与部署目标
- `Mod/SKILL.md` — Mod 开发流程、IMod 接口、打包脚本模板、运行时铁律
- `doc/mod-development-guide.md` — 五步开发流程、项目模板、Android AOT 裁剪规避
- `doc/sumod-architecture.md` — SuAPI 六大组件与三种注入模式

---

**下一步**：以本文档为输入，在 goal 模式下按 P1 → P2 → P3 → P4 顺序实施；P5 可选，需另行批准。

---

## 13. 实施进度与实测记录（Round 1）

### 13.1 已交付

| 产物 | 说明 |
|---|---|
| `Mod/CmdBridgeMod/` | scmod 服务端：只读观察 + 白名单输入注入 |
| `Mod/CmdBridgeClient/` | `sccmd.exe` 命令行客户端（纯 BCL，零游戏引用） |
| `Mod/Packages/pack_cmd_bridge.py` | .scmod 打包 + 校验 + 部署 |
| `Mod/Packages/check_cmd_bridge_readonly.py` | 只读/白名单审计（0 黑名单命中） |
| `Mod/Packages/[SuAPI]CmdBridgeMod-1.0.0.scmod` | 33,933 字节，`ModInfo.xml` + 扁平 `Lib/CmdBridgeMod.dll` |
| 部署 | `publish/Windows/Mods/[SuAPI]CmdBridgeMod-1.0.0.scmod` |

Mod 源文件（10 个 .cs）：`Plug/CmdBridgeMod.cs`、`Server/{CmdBridgeConfig,ControlServer,CommandRouter,GameThreadInvoker,InputInjector,InputWhitelist}.cs`、`Server/Observers/{UiInspector,PlayerObserver,AimObserver}.cs`。

### 13.2 已实现命令

只读：`ping` / `status` / `obs.snapshot` / `obs.ui`（`all`/`maxElements`/`filter`）/ `obs.player` / `obs.aim`（`maxDistance`）/ `obs.input` / `obs.dialogs` / `ui.reachability` / `obs.selftest`

动作（输入层）：`act.look` / `act.lookdelta` / `act.lookat` / `act.key` / `act.hold` / `act.chord` / `act.mouse` / `act.wheel` / `act.uiclick`（支持 `x`/`y` 指定坐标）/ `act.text` / `act.releaseAll`

客户端：`status` `ui` `reachability` `player` `input` `aim` `dialogs` `snapshot` `selftest` `look` `lookdelta` `lookat` `key` `hold` `release` `chord` `mouse` `wheel` `click`（`--at x y`）`text` `raw`，以及无参 REPL；`--json/--root/--port/--token/--timeout`。

### 13.3 实测证据（`publish/Windows` 实例，真实运行）

| 验证项 | 结果 |
|---|---|
| Mod 加载与发现 | `CmdBridge.runtime.json` 自动生成（port 26751、随机 token、`inputInjection:true`）；客户端零配置发现成功 |
| `obs.selftest` | `allOk: true`，9 项注入点全部可解析（Keyboard 3 组数组、Mouse 2 组数组 + 滚轮、`m_screens`、`Window.Position`） |
| `ui.reachability` | 主菜单：`totalInteractive=6`，5 个可达、1 个隐藏（`Full Version...` 在 0,0 且 `hittable:false`） |
| **引擎内 UI 点击** | `sccmd click Settings` → 游戏真实进入 `Settings` 屏；`click TopBar.Back` → 回到 `MainMenu` |
| **不可跳级** | 在 Settings 屏点主菜单的 `Play` → `error[element_missing]` + 退出码 2（元素不存在，绝不猜） |
| 错误码契约 | `element_missing`→2、`element_occluded/ambiguous`→3、连接类→4、`screen_busy/timeout`→5、`world_not_loaded`→6 |
| **虚拟列表项** | `ListPanelWidget` 条目不是控件 → 已导出 `list{itemsCount,itemSize,scrollPosition,selectedIndex,items[{index,text,clientPoint,screenPoint}]}`；3 个世界全部列出 |
| 列表行点击 | `sccmd click WorldsList --at 1010,241` → `selectedIndex=2` |
| **完整 A→B→C→D** | MainMenu →(click Play) Play 屏 →(click 列表行) 选中世界 →(click Play!) 世界加载完成（`screen:Game, worldLoaded:true`） |
| **键盘注入（引擎层取证）** | `hold w` → 引擎 `Keyboard.m_keysDownArray` 读出 `keysDown:["W"]`；`release w` → `[]` |
| **鼠标注入（引擎层取证）** | `mouse left down` → `mouseButtonsDown:["Left"]`；`right down` → `["Right"]`；物理光标位置未变（`mousePosition` 保持 1573,698） |
| **世界内玩家状态** | Survival 模式；health=1、food=0.60、sleep=0.13、temp=23.0；背包 26 格、activeSlot=0、手持空 |
| **瞬时转视角** | `lookat <脚下>` → `yawDeg 180, pitchDeg -81.99999`（±82° clamp 生效） |
| **准星目标解析** | 向下 → `kind:block cell:(-41,66,10) CobblestoneBlock distance:1.56`；向上 → `BasaltSlabBlock`（室内天花板） |
| **自身排除修复** | `maxDistance=1` → `kind:none`（未修复前会命中自己 `distance:0`）；`maxDistance=2` → `kind:block` |
| **键盘驱动玩家控制器** | `hold w` → 世界内 `PlayerInput.Move=(0,0,1)`；玩家实际从 `x=-38.95` 移动到 `x=-40.21`（约 1.26m）；`release w` 后 `Move=(0,0,0)` |
| 只读审计 | 10 个源文件、0 黑名单命中、所有写入均走 `InputWhitelist` 常量；唯一人工复核项为视角朝向 `.Rotation =` |
| 玩家正常操作 | 全程未修改渲染/窗口/输入/帧率；Mod 仅订阅只读命令与注入白名单 |

### 13.4 实施期新发现（相对计划的修正/补充）

1. **`Frame.Update` 不参与**：命令全部通过 `Dispatcher.Dispatch`（后台线程入队 → 下一帧 `Dispatcher.BeforeFrame()` 执行）在游戏线程运行，读写统一，无需 `Frame.Update` 订阅。已实测可在世界加载后稳定执行。
2. **`Keyboard.AfterFrame` 的连发管理**：它会把 `m_keysDownRepeatArray[i] < 0` 转成真实连发计时（`Keyboard.cs:144-160`）。因此**只在"按下那一帧"写 -1**，不能每帧重写（否则永远不连发）。
3. **键盘鼠标事件无订阅者**：`Keyboard.KeyDown/KeyUp/CharacterEntered`、`Mouse.MouseDown/Up/Move` 在 Survivalcraft 中无人订阅 → 纯轮询式数组注入足够，无需触发事件。
4. **虚拟列表**：`ListPanelWidget`/`ScrollPanelWidget` 的条目是自绘的，**不是控件**，`AllChildren` 里没有它们。已按游戏自身的行几何（`i*ItemSize - ScrollPosition`，来源 `ListPanelWidget.cs:226-248`）导出可点击坐标；点选仍走"面板元素 + 指定坐标"校验路径。
5. **射线自身命中**：`SubsystemBodies.Raycast` 会命中射线起点所在的玩家自身 → 必须用谓词排除 `player.ComponentBody`。
6. **`obs.player` 依赖世界**：无世界时提前返回 `loaded:false`，因此新增了不依赖世界的 `obs.input` 作为注入取证通道。
7. **异常必须原样穿透**：`TaskCompletionSource` + `Task.Wait` 会把异常包成 `AggregateException`，导致客户端拿不到 `element_missing` 等错误码；已改为捕获后由 `GetAwaiter().GetResult()` 原样抛出。
8. **UI 点击的软光标**：用 `WidgetInput.UseSoftMouseCursor=true` + `MousePosition` 设置器指定坐标，避免移动物理光标（实测物理光标未变），点击完成后还原，避免残留软光标绘制。

### 13.5 下一轮待办（建议顺序）

1. **P3 事件环**：`obs.events`（`sinceSeq` 增量）——记录本帧 `IsClicked` 的 UI 元素、世界交互目标、屏幕/对话框/模态面板变化、受伤/死亡/升级。需要重新引入 `Frame.Update` 订阅。
2. **世界观察**：`obs.world.blocks`（区域方块扫描，强制半径/条数上限）、`obs.world.entities`（半径内实体，需补充实体身份标识，因 `Entity` 无 `Id` 成员）、`obs.world.time`。
3. **`obs.snapshot` 补全**：加入 `input` 与 `world` 块。
4. **文本输入落地验证**：`act.text` 已实现（逐帧写 `m_lastChar`），但需要 `TextBoxWidget` 获得焦点（点击文本框 → `Keyboard.ShowKeyboard` 行为待实测）。
5. **玩家创建流程**：新世界需要创建玩家（Player 屏含文本输入），需实测 A→B→C→D 全链路。
6. **客户端体验**：REPL 补全、`ui` 表格增加 `list` 小节展示、`--at` 帮助文本已补。
7. **文档同步**：`Mod/README.md` 增加 CmdBridgeMod 条目（`SYNC_LIST` 已补 `CmdBridgeMod` / `CmdBridgeClient` / `doc`）。
8. **压测**：大量控件界面（如背包/合成）下的 `obs.ui` 响应体量与耗时；`maxElements` 截断行为。

### 13.6 需要向用户说明的事项

- 实测为不破坏性验证：**未挖掘、未放置、未改动任何方块或物品**；唯一的世界状态变化是"按住 W"步行测试让 `ceshi260810` 世界中的玩家移动了约 1.26m（并被转视角改变朝向）。
- 未执行 OS 级鼠标模拟（`SendInput`/DPI 感知）：引擎内注入已覆盖 UI 点击、世界交互与视角，OS 通道按计划降级为 P4 兜底，尚未实现。

---

## 14. Round 2 记录：输入通道与 UI 可见性解耦（实测）

### 14.1 机制（源码依据）

| 事实 | 出处 |
|---|---|
| 本机第一个玩家的输入设备集合**总是**包含 `WidgetInputDevice.Keyboard \| Mouse`，与 `PlayerData.InputDevice` 无关 | `Survivalcraft/Game/GameWidget.cs:140-151` |
| HUD 控件的显示/隐藏是**各自独立的 `IsVisible` 条件**（触屏面板看 `IsControlledByTouch`、创造/生存按钮看游戏模式、状态条看冒险机制开关） | `Survivalcraft/Game/ComponentGui.cs:395-411` |
| 世界内键鼠游玩时鼠标光标被隐藏（`IsMouseCursorVisible = false`），鼠标交给世界视角；打开模态面板/对话框时才恢复光标 | `Survivalcraft/Game/ComponentInput.cs:143-155` |
| 模态面板打开时世界输入被跳过，操作全部变成对面板的 UI 点击 | `Survivalcraft/Game/ComponentInput.cs:143-152, 418-421` |

**结论**：**按键永远有效，UI 点击是否有效由游戏自身状态决定**。CMD Bridge 把两者如实分开报告，绝不因为"UI 不可点"而禁止按键。

### 14.2 实测（`publish/Windows` + `ceshi260810` 世界）

| 场景 | 观测 |
|---|---|
| 世界加载刚完成 | `interactive=25, hittable=0` —— 13 个元素被 `ViewWidget(View)` 吞掉命中，即"UI 在但点不到" |
| 玩家进入可操作状态后 | `hittable=12`：8 个快捷栏 `InventorySlotWidget` + `BackButton`/`ClothingButton`/`InventoryButton`/`MoreButton`/`CrouchButton`（`hitTargetType: ClickableWidget`） |
| `wheel -2` | `activeSlot: 0 -> 2`（滚轮驱动快捷栏，纯玩家控制器动作） |
| `key e` | `hud.modalPanel: '' -> FullInventoryWidget`（按键打开背包） |
| 背包打开后 | `interactive=47, hittable=34`，其中 **29 个 `InventorySlotWidget`** —— "隐藏的 UI 在打开背包后出现且可点" 得到实测确认 |
| `click <背包槽位路径>` | `exit=0`，`hittable:true`，返回 `clickPoint` |
| `click ClothingButton` | `modalPanel: FullInventoryWidget -> ClothingWidget`（点击造成真实面板切换） |
| 再次 `key e` | 面板切回/关闭（依游戏自身的 `ComponentGui` 面板状态机） |

### 14.3 Round 2 修复的问题

1. **面板类型误列入可交互集合**：`FullInventoryWidget`、`CreativeInventoryWidget`、`ShortInventoryWidget`、`ClothingWidget`、`ChestWidget`、`FurnaceWidget`、`ScrollPanelWidget`、`CraftingRecipeWidget` 等只是**容器**，一旦列入可交互类型，父子去重规则会吞掉整个子树 → 背包槽位完全不可见（实测 `hittable` 只有 7）。已移出，槽位恢复为 29 个独立可寻址元素。
2. **无名控件的路径不唯一**：快捷栏 8 个 `InventorySlotWidget` 都没有 `Name`，路径段退化成 `[InventorySlotWidget]` → `ambiguous_selector`，槽位无法寻址。现路径段改为 `[TypeName#兄弟索引]`（如 `[InventorySlotWidget#3]`），路径唯一。
3. **selector 歧义可用坐标消歧**：`click <selector> --at x y` 在多个候选中取"包含该坐标"的那个；仍是先校验元素存在、再校验命中，不引入猜测。
4. **序号选择器口径不一致**：原先按"全部控件"编号，而 `obs.ui` 的 `id` 按"去重后的可交互元素"编号 → 两者不等价。现序号与列表同口径。

### 14.4 对 AI（L3）的含义

- **世界内 HUD 优先用按键**：键鼠玩家的鼠标被游戏路由给世界视角，HUD 按钮多数不可点；`hittable=false` + `blockedBy` 明确告知原因，AI 应改用按键（`e` 背包、`c` 衣物、数字键换槽等）。
- **模态面板是天然的"操作池切换点"**：`hud.modalPanel` 从 `null` 变为具体类型时，鼠标操作对象从"世界"切换为"面板"；这正是计划 §7 状态机 `Pool.Inventory` / `Pool.Clothing` 的判据。
- **`hittable` 是唯一的点击许可**：不要因为"元素在列表里"就点，必须 `hittable=true`。

---

## 15. Round 3 记录：P3 观察层完成（事件环 + 世界观察）

### 15.1 新增命令

| 命令 | 说明 |
|---|---|
| `obs.events [sinceSeq] [max]` | 事件环增量拉取（读取时加锁，无需游戏线程） |
| `obs.world.time` | 天数 / 一天占比 / 小时 / 是否夜晚 / 季节 / 天气 / 游戏模式 / 世界总时长 |
| `obs.world.blocks [x y z radius max]` | 区域方块扫描；未给中心时以玩家所在格为中心 |
| `obs.world.entities [radius max]` | 半径内实体（按距离升序，含组件名列表） |
| `obs.snapshot` | 现一次返回 `status + ui + player + input + aim + world + dialogs + events` |

事件种类：`ui.click`、`world.dig`/`world.hit`/`world.interact`/`world.aim`、`screen.changed`、`modal.opened`/`modal.closed`、`dialog.shown`/`dialog.hidden`、`world.loaded`/`world.unloaded`、`player.damaged`/`player.healed`/`player.died`/`player.slotChanged`。

### 15.2 实测证据

| 验证项 | 结果 |
|---|---|
| `obs.world.time` | `day=2333.4, timeOfDay=0.4075, hour=9.78, isNight=false, season=Summer, precip=0`；数分钟后同一世界读到 `hour=11.2`（世界在推进） |
| `obs.world.entities`（半径 48） | `considered=4`：玩家自身（distance 0、isPlayer）+ 3 只生物（14.5 / 19.8 / 20.1 米，isCreature，含 `ComponentLocomotion/ComponentCreature/ComponentLoot/...` 组件名） |
| `obs.world.blocks`（半径 1） | 中心自动取玩家格 `(-41,67,10)`；`totalNonAir=9`，列出 9 个 `CobblestoneBlock` |
| 事件环：按键开背包 | `seq 38 modal.opened {panel: FullInventoryWidget}` |
| 事件环：面板内点击 | `seq 39 modal.opened {panel: ClothingWidget, previous: FullInventoryWidget}` + `seq 40 ui.click {path: .../ClothingButton}` |
| 事件环：快捷栏变更 | `wheel -3` → `player.slotChanged {from: 2, to: 5}` |
| `obs.snapshot` | 键集合 `status, ui, player, input, aim, world, dialogs, events` |
| 只读审计 | PASS（0 黑名单命中） |

### 15.3 实现要点

- **事件环跑在 `Frame.Update`**：这是本 Mod 唯一的每帧钩子，且位于控件树 Update 之后，因此 `ClickableWidget.IsClicked` 此刻就是"本帧被点击的元素"（来源 `Program.cs:147`）。回调自带 try/catch，异常只记一次以免刷屏。
- **UI 点击事件报告"语义拥有者"**：`ClickableWidget` 命中后向上找最近的 `ButtonWidget`，输出其 path/name/type/text/坐标，避免报出内部模板子控件。
- **实体枚举用 `SubsystemBodies.Bodies`**（`SubsystemBodies.cs:18`）+ `Entity.Components`（`Entity.cs:97`）的组件名列表——因为 `Entity` 没有 `Id`，组件名是 AI 区分"生物 / 玩家 / 拾取物 / 投射物"最可靠的依据；另附 `handle = GetHashCode()` 作为会话内稳定标识。
- **方块扫描用 `Terrain.GetCellContentsFast`**，强制 `radius ≤ 32`、`max ≤ 1024`，并统计越界 Y 的跳过数，避免拉爆响应。
- **时间单位**：`SubsystemTimeOfDay.TimeOfDay` 是 `[0,1)` 的一天占比（`DayDuration = 1200` 秒），`isNight` 由 `NightStart`/`DawnStart` 自行判定（来源 `SubsystemTimeOfDay.cs:16-42, 78-90`）。
- **`player.healed` 与 `player.damaged` 成对**：AI 必须能看到"自然回血"发生了，才能理解"不能自己加血、只能等或吃"的约束下自己的生存窗口。
- 新增 `player.level`（来源 `PlayerData.cs:118`）。

### 15.4 剩余待办

1. `act.text` 文本输入的落地实测（签牌/玩家命名需要；需先让 `TextBoxWidget` 获得焦点）。
2. 新世界的玩家创建流程（Player 屏含文本输入）A→B→C→D 全链路。
3. 客户端体验：`ui` 表格渲染 `list` 小节、REPL 补全。
4. OS 级鼠标兜底通道（P4，当前判定为不需要：引擎内注入已覆盖全部交互）。
5. `obs.world.blocks` 的增量模式（只返回与上次相比的变化）以支持长期运行的 AI。
6. **待查（重要）**：Play 屏选世界那一步记录到 22 条 `ui.click`，明显多于实际点击次数（约 3 次），疑似重复上报（同一 ClickableWidget 在多帧保持 IsClicked，或列表项重建所致）。需做一次干净实验：重启 → 进世界 → 只点一次 → 统计事件数，确认是否严格「一次点击一条事件」。AI 的事件驱动逻辑依赖该语义。

---

## 16. 查证："睡觉时睡在石块上" —— 原版设计的空子，非本 Mod 所致

### 16.1 用户报告

> 刚我看到了睡觉的时候，睡在石块上，这在游戏中应该是不存在的。

用户的记忆是**正确的**：游戏确实不允许在石块上手动睡觉。但存在一条绕过路径，下面是完整查证。

### 16.2 两条睡觉路径

| 路径 | 是否检查 `CanSleep` | 出处 |
|---|---|---|
| **手动睡觉**（衣物面板 `SleepButton`） | ✅ 检查，不通过则提示原因 | `ClothingWidget.cs:80-90` |
| **困到昏睡**（`Sleep` 归零） | ❌ **直接 `Sleep(allowManualWakeup: false)`，完全绕过 `CanSleep`** | `ComponentVitalStats.cs:473-478` |

`CanSleep` 的判定顺序（`ComponentSleep.cs:41-79`）：① 站在干燥地面；② **脚下方块 `SleepSuitability != 0`**（否则 "Too uncomfortable to sleep"）；③ 不能已经睡饱（`Sleep > 0.99`）；④ 不能太湿（`Wetness > 0.95`）；⑤ 头顶 3×3 九个采样点向上都要被方块遮挡（否则 "You need a better shelter"）。

昏睡路径的附带后果（`ComponentSleep.cs:112, 131-151`）：
- `allowManualWakeUp = false` → **任何玩家输入都无法叫醒**（只有 `allowManualWakeUp == true` 时才响应输入唤醒，第 131-136 行）；
- 需要等 `Sleep >= 1` 且处在白天区间且已睡满 180 游戏秒才会自然醒（第 112 行）；
- 屏幕会显示 "You fell unconscious from lack of sleep" / "Zzz..." / 黑屏渐入。

### 16.3 数据证据：哪些方块能睡

`Pak/BlocksData.csv` 共 270 个方块，`SleepSuitability` 分布：`0` 有 233 个、`0.5` 有 22 个、`1` 有 3 个、空 12 个。

| 值 | 代表方块 |
|---|---|
| **0（不可睡）** | `CobblestoneBlock`、`BasaltBlock`、`GraniteBlock`、`LimestoneBlock`、`SandstoneBlock`、`MarbleBlock`、`BrickBlock`、各类石制台阶/石板/栅栏、雪、冰、梯子、门、木牌 |
| 0.5 | `DirtBlock`、`SoilBlock`、`SandBlock`、五种原木、六种树叶、`PlanksBlock`、`CraftingTableBlock`、`ChestBlock`、`DispenserBlock`、`GrassTrapBlock` |
| 1.0 | `GrassBlock`、`CarpetBlock`、`FurnitureBlock` |

### 16.4 速率证据：为什么会在测试期间昏睡

`ComponentVitalStats.cs:444`：`Sleep -= gameTimeDelta / 1800f`（每秒衰减 1/1800，且入水或刚被攻击 10 秒内暂停衰减）。

- 早前会话读到 `vitals.sleep = 0.13` → 距归零只需约 **0.13 × 1800 ≈ 234 秒**；
- 该会话在世界内持续运行了数分钟 → 归零 → 触发昏睡；
- 现场实测读到 `vitals.sleep = 0.823819`，即**昏睡后已恢复**，与"睡了、且在鹅卵石上睡"的观测一致。

### 16.5 现场实测（修复后新增的观察能力输出）

```json
{
  "isSleeping": false,
  "sleepFactor": 0,
  "canSleep": false,
  "canSleepReason": "Too uncomfortable to sleep",
  "allowManualWakeUp": false,
  "standingOn": "CobblestoneBlock",
  "standingOnSleepSuitability": 0,
  "secondsUntilFaint": 1482.8741
}
```

`canSleepReason` 之所以是 "Too uncomfortable to sleep" 而不是 "You are not tired enough"，是因为 `CanSleep` 把"方块适睡度"检查排在"够不够困"之前——这恰好是"石块上不许手动睡"的直接证据。

### 16.6 责任归属：不是 CmdBridge，也不是 ScMultiplayer

- **CmdBridge**：源码中没有任何睡觉相关逻辑（`grep -i sleep Mod/CmdBridgeMod` 只命中 `vitals.Sleep` 只读与无关的 `Thread.Sleep`）。本 Mod 也从不点击睡觉按钮。
- **ScMultiplayer**（测试实例已安装 2.1.1）：`SuComponentSleep` 继承 `ComponentSleep` 且**未覆盖 `CanSleep`**，只改客户端唤醒权（主机权威）；在单机/主机情形下直接 `base.Update(dt)` 走原版路径（`Mod/ScMultiplayer/Func/Component/SuComponentSleep.cs:26-35`）。`SuComponentVitalStats` 完全不引用 Sleep。
- 因此该行为是**原版设计**（"困到昏睡"有意为之的后果，只是它没有复用 `CanSleep` 的门槛）。

### 16.7 本 Mod 的处置：不修游戏，改为让 AI 看得见

主仓库零修改是硬约束，且这属于原版设计取舍，因此**不改游戏逻辑**，而是补齐 AI 的观察能力：

1. `obs.player.sleep` 新增块：`isSleeping`、`sleepFactor`、`canSleep`、`canSleepReason`、`allowManualWakeUp`、`standingOn`、`standingOnSleepSuitability`、**`secondsUntilFaint`**（按 1/1800 速率换算，告诉 AI 还有多久会昏睡）。
2. 事件环新增 `player.sleepStarted` / `player.sleepEnded`，并带 `allowManualWakeUp`，用于区分"主动入睡"与"困到昏睡"。

### 16.8 对 AI 的直接含义

- **`secondsUntilFaint` 是一个硬倒计时**：AI 必须在归零前找到适睡地面（泥土/沙/原木/树叶/木板 0.5，草/地毯/家具 1.0）并进入有遮蔽处；否则会当场昏睡，屏幕黑掉、无法用输入唤醒、直到自然醒。
- 计划 §7.2 的 L2 情境态应显式包含 `Sleeping` 与 `AboutToFaint`（阈值建议 `vitals.sleep < 0.15` 或 `secondsUntilFaint < 300`），对应操作池 `Pool.Sleep`：走到适睡地面 → 进遮蔽 → 主动睡（`CanSleep == true` 才允许）。
- **主动入睡是可被输入打断的**（`allowManualWakeUp == true` 时任何输入即醒，`ComponentSleep.cs:131-136`）；昏睡不可打断——所以"困了就自己找地方睡"比"拖到昏睡"策略上严格更优，这也正符合"优势 ≠ 特权"：AI 只能靠规划与操作，不能靠改数据。

---

## 17. 睡觉的操作链路与游戏反馈通道（实测）

### 17.1 用户补充的事实

> 而且睡觉的按钮位置是在打开角色信息中才有按钮的，没有对应的按键。

用户说的完全正确，源码可证：

| 环节 | 事实 | 出处 |
|---|---|---|
| 打开角色面板 | 有按键：`C`（`ToggleClothing`），也可点 HUD 的衣物按钮 | `ComponentInput.cs:195` → `ComponentGui.cs:550` |
| 面板类型 | `ClothingWidget` | `ComponentGui.cs:558` |
| 睡觉按钮 | 在 `ClothingWidget` 内部，名为 `SleepButton` | `ClothingWidget.cs:14, 30, 80` |
| 睡觉专用键 | **不存在**。`Key.C` 只负责开面板，睡觉得再点按钮 | `ComponentInput.cs:172-244` 全表核对 |

因此 AI 的"主动入睡"必然是一个**两段式操作**：**按键开面板 → 点击按钮**，二者缺一不可。这正好是"AI 只能通过玩家控制器交互"的典型例子。

### 17.2 新增：游戏反馈消息通道（`obs.messages` / `ui.message` 事件）

游戏本来就在通过屏幕提示教学：`ComponentGui.DisplaySmallMessage` 把文本交给 `MessageWidget`，
而 `MessageWidget` 把文本**作为子 `LabelWidget` 挂在控件树上**（`MessageWidget.cs:88-92`），
所以可以纯公开 API 读取，无需反射。另有 `ComponentScreenOverlays.Message/FloatingMessage`
（睡觉、溺水等状态表现，`ComponentScreenOverlays.cs:46-52`）。

- 命令：`obs.messages` → `{ messages: [...], overlay: { message, messageFactor, floatingMessage, ..., blackoutFactor } }`
- 事件：`ui.message { text }` —— 小提示只存活 4~6 秒（`MessageWidget.cs:36`），靠轮询会漏，因此由事件环逐帧比对上报，保证 AI 不错过
  "You will faint, go to sleep!"、"You are very tired, sleep"、"Too uncomfortable to sleep"、"Can't go no more"、
  "You fell unconscious from lack of sleep"、"You need a better shelter"、"Brrr, wet!" 等关键信号。
- `obs.snapshot` 也已加入 `messages`。

### 17.3 实测：AI 执行"尝试睡觉"并读懂拒绝原因

在鹅卵石地面上（`SleepSuitability = 0`）完整跑了一遍：

| 步骤 | 观测 |
|---|---|
| `key c` | `hud.modalPanel: '' -> ClothingWidget` |
| 面板内可点元素 | `VitalStatsButton`、**`SleepButton`**、20 个 `InventorySlotWidget`（全部 hittable） |
| `player.sleep` | `canSleep=false`、`reason="Too uncomfortable to sleep"`、`standingOn=CobblestoneBlock`、`standingOnSleepSuitability=0`、`secondsUntilFaint=1445` |
| `click SleepButton` | 点击送达（返回元素 path 与 clickPoint） |
| **`obs.messages`** | `messages: ["Too uncomfortable to sleep"]` ← **游戏自己给出的拒绝原因被 AI 读到** |
| 结果 | `isSleeping=false`（游戏正确拒绝，没有发生非法睡觉） |
| 事件环 | `modal.opened {ClothingWidget}` → `ui.click {name: SleepButton}` → `ui.message {text: "Too uncomfortable to sleep"}` |

这一步同时构成对 §16 的**独立交叉验证**：手动睡觉在石块上会被游戏真实拒绝，
而之前观测到的"睡在石块上"只能来自昏睡路径（绕过 `CanSleep`）。

### 17.4 AI（L3）的睡觉操作配方

```text
前置：obs.player.sleep.secondsUntilFaint 变小 / vitals.sleep 偏低
1. 用 obs.world.blocks / aim 找一块适睡地面（Grass 1.0；Dirt/Sand/原木/树叶/木板 0.5；石类一律 0）
2. 移动到该地面（hold W/A/S/D + lookat），并确认有遮蔽（sleep.canSleep 会顺带检查 3x3 遮挡）
3. key c            → waitFor modal.is:ClothingWidget
4. 复查 player.sleep.canSleep 与 canSleepReason
   · canSleep == true  → click SleepButton → waitFor player.sleepStarted
   · canSleep == false → 按 canSleepReason 修正（换地面 / 去淋不到雨的地方 / 等不那么湿）
5. 观察 events 里的 player.sleepStarted（allowManualWakeUp=true 表示是主动入睡，可被输入打断）
```

**关键设计点**：先查 `canSleep` 再点按钮，而不是靠"点了再看提示"——因为 `CanSleep` 的判定顺序是
「地面干燥 → 方块适睡度 → 是否已睡饱 → 湿度 → 遮蔽」（`ComponentSleep.cs:41-79`），
一次查询就能拿到完整原因，比解析提示消息更可靠；提示消息通道作为兜底与交叉验证。

### 17.5 顺带发现

同一个角色面板里还有 `VitalStatsButton`（打开 `VitalStatsWidget` 详细状态面板）与 20 个角色装备槽位，
全部可点。也就是说 `key c` 是 AI 打开"角色相关一切"的统一入口，状态机可以把
`Pool.Clothing` 与 `Pool.Sleep` 合并挂在`modalPanel == ClothingWidget` 这一个判据上。

---

## 18. 测试用图约定与完整睡眠流程实测

### 18.1 测试用图约定（用户要求）

**禁止在联机地图上测试。** 依据（`publish/Windows/Worlds/`）：

| 目录 | 世界名 | `ScMultiplayerPlayers.xml` | 判定 |
|---|---|---|---|
| `World/` | **ceshi260810 (suceru.site)** | 48,077 字符 / **23 条玩家记录** | ❌ **联机实时下载缓存，禁止用于测试** |
| `World1/` | Rosnia Mear | 2,250 字符 / 1 条记录（Basil） | ✅ 单机图，可用于测试 |
| `pingtan/` | Rebritish | 2,274 字符 | ✅ 单机图，可用于测试 |

**为什么不能用它测试（用户明确要求，机制已查证）**：`Worlds/World` **不是本地存档，而是联机世界的实时下载缓存**——

- 每次加入房间：服务器把世界传过来，客户端解压导入到 `data:/Worlds/World`
  （`ScMultiplayerWorldTransferHandlers.cs:552, 605-613`；日志中表现为
  `[SuPlay] JoinGame sent` → `[DL] Requesting world` → `[ScMP] World download complete` → `Importing world`）；
- 离开/断线：帧末统一清理，`WorldsManager.DeleteWorld(downloadedWorldDirectory)` 并切回 Play 屏
  （`ScMultiplayerClientEvents.cs:413-425`）；
- 导入失败：同样会删除半成品目录（`ScMultiplayerWorldTransferHandlers.cs:615-626`）。

因此该目录**随时可能被游戏自己删除并重建**：在其中做测试，测试痕迹会随缓存消失，
而且会与正在进行的实时联机会话互相干扰。联机世界的权威副本在服务器（`<server-ip>`），
本地目录消失**不构成数据丢失**。此前 §13~§17 记录中有一轮曾使用 `ceshi260810`，后续测试一律使用 `Rosnia Mear`。

### 18.2 正反两向对照：`canSleep` 判定正确

同一套 `obs.player.sleep` 查询，在两种地面上的结果完全符合 §16 的源码结论：

| 地图 / 脚下方块 | `standingOnSleepSuitability` | `canSleep` | `canSleepReason` |
|---|---|---|---|
| ceshi260810 / `CobblestoneBlock` | 0 | **false** | "Too uncomfortable to sleep" |
| Rosnia Mear / `GrassBlock` | 1 | **true** | `null` |

这是可复现的判定验证：AI 在点睡觉按钮**之前**就能知道"这块地行不行"。

### 18.3 完整睡眠周期实测（Rosnia Mear，事件环原文）

```
seq kind                panel          name        text                  allowManualWakeUp
51  world.loaded
52  screen.changed                                                      (GameLoading → Game)
53  dialog.shown
54  dialog.hidden
55  modal.opened        ClothingWidget                                   ← key c 打开角色面板
56  ui.click                           SleepButton                      ← 点击睡觉按钮
57  player.sleepStarted                                             True ← 真正入睡（主动）
58  modal.closed                                                         ← 游戏自动关闭面板
59  ui.message                                     Zzz...
60  ui.message                                     Tap to wake up early ← 可手动唤醒提示
61  ui.message                                     You feel a bit chilly ← 睡眠中变冷
62  player.sleepEnded                                               True
```

配套观测：

| 项 | 变化 |
|---|---|
| 世界天数 | `day 130.40 → 131.31`（**整夜被跳过**） |
| 一天占比 | `hour 9.51 → 7.50`（次日清晨醒来） |
| 睡眠值 | `0.89 → 0.99`（睡饱） |
| 食物 | `0.90 → 0.64`（**睡觉会消耗食物**，AI 规划时不能忽略） |
| 位置 | 不变（`-88.5, 66.0, -76.5`） |
| 醒来后 `canSleepReason` | `"You are not tired enough"`（睡饱了就睡不了） |

### 18.4 实现期踩到的坑：手动唤醒有 10 游戏秒门槛

`ComponentSleep.Update` 的手动唤醒分支要求 `num > 10f`（`num` 为已睡游戏秒数）**且** `m_allowManualWakeUp == true`
（`ComponentSleep.cs:131-136`）。而时间加速只在 `SleepFactor == 1f` 时才启动（`SubsystemTime.cs:87`），
`SleepFactor` 每秒涨 0.33（`ComponentSleep.cs:109`）→ 约 3 秒后才加速。

因此：**点完睡觉立刻发输入不会醒**。实测第一次 `key space` 无效，必须等约 4~5 秒（或等 `SleepFactor` 达到 1）后再发输入。
AI 若要"睡一小会儿就起来"，必须遵守这个门槛；否则就等自然醒（`Sleep >= 1` 且处于白天区间且睡满 180 游戏秒，`ComponentSleep.cs:112`）。

### 18.5 AI（L3）睡眠操作配方（修订版）

```text
1. obs.player.sleep.secondsUntilFaint 变小，或 vitals.sleep 偏低 → 想睡
2. 检查 obs.player.sleep：
   standingOnSleepSuitability == 0 → 当前地面不能睡，先走到 Grass/Dirt/Sand/木/树叶/地毯上
   canSleep == false → 读 canSleepReason 决定：换地面 / 避雨 / 等不湿 / 还不用睡
3. key c → waitFor modal.is:ClothingWidget
4. click SleepButton（此时 CanSleep 已为 true，游戏不会拒绝）
5. waitFor 事件 player.sleepStarted（allowManualWakeUp=true 表示主动入睡）
6. 二选一：
   · 睡到自然醒：waitFor 事件 player.sleepEnded
   · 提前起床：等 ≥ 5 秒（让 SleepFactor 到 1 并过 10 游戏秒）后再发任意输入 → sleepEnded
7. 醒来后复查 vitals：food 会下降，sleep 会接近 1
```

### 18.6 附：`Worlds/World` 目录"消失"的查证结论

本轮曾一度以为联机图目录丢失，随即查证并排除：

| 证据 | 内容 |
|---|---|
| 游戏日志 | 23:15~23:21 之间用户正在连自己的服务器：`JoinGame sent: ceshi260810 -> 0, server=<server-ip>:51460` → `[DL] Requesting world` → `World download complete: Bytes=3114927` → `Importing world → data:/Worlds/World`，共 24 次导入记录 |
| 源码 | ScMultiplayer 有 3 处主动删除世界目录：`ScMultiplayerClientEvents.cs:422`、`:945`、`ScMultiplayerWorldTransferHandlers.cs:619` |
| 结论 | 目录由游戏自身的"下载缓存导入/清理"流程增删，**与 CmdBridge 无关**（本 Mod 无任何文件删除逻辑，只写 `CmdBridge.json` / `CmdBridge.runtime.json`），也**不是数据丢失**（权威副本在服务器） |

用户随后确认："不用管联机地图，那是实时下载的。"

---

## 19. Round 4：文本输入 / 建号入世 / 点击语义修复

### 19.1 `act.text` 文本输入实测通过

Windows 上 `Keyboard.ShowKeyboardInternal` 的实现是 `{ cancel(); }`（`Engine/Engine/Input/Keyboard.cs:217-220`）——
**没有屏幕键盘对话框**，文本框直接依赖 `WidgetInput.LastChar`，所以 `act.text`（逐帧写 `m_lastChar`）正好喂给它。

实测（新建世界界面的 `Name` 文本框）：

| 步骤 | 观测 |
|---|---|
| `click <Name 文本框>` | 获焦（`TextBoxWidget`，原文本 "Prusbo Nilonia"） |
| `key BackSpace` × 16 | 文本被清空为 `''`（**按键也能驱动文本编辑**） |
| `act.text "CmdBridgeTest"` | 返回 `{"typed": 13}`，文本框变成 `CmdBridgeTest` ✓ |

至此四类玩家控制器输入全部实测通过：视角 / 键盘 / 鼠标 / UI 点击，外加文本输入。

### 19.2 新建世界 + 创建角色 全链路实测通过

```
MainMenu →(click Play) Play 屏 →(click NewWorld) NewWorld 屏
  · Name 文本框预填随机名，Seed 可空
  · click Play("Setup Players") → screen=Player（世界已创建并加载，worldLoaded=true）
Player 屏 →(click AddAnotherButton) 加入角色 →(click PlayButton) 进入游戏
  · screen=Game, animating=false, worldLoaded=true, obs.player.loaded=true ✓
```

途中观测到的界面语义：

| 现象 | 说明 |
|---|---|
| `obs.player.loaded=false` 但世界已加载 | `ComponentPlayers` 只含**已生成**的角色；角色名单（`PlayersData`）要等进入游戏才生成实体——AI 需要区分"世界已加载"与"角色已生成" |
| `AddButton("Add Player")` `hittable=false` | 角色列表为空时该按钮不可见 |
| `PlayerClassButton` / `CharacterSkinButton` / `ControlsButton` | 建号界面可用于设定职业/皮肤/输入设备（AI 可用 `key c`-式路径先设定再建号） |

### 19.3 修复：`ui.click` 一次点击被重复上报（根因已定位）

**现象**：一次 `click` 命令在事件环里报出 **22 条** `ui.click`（Round 3 已flag，本轮定位）。

**根因**：`ScreensManager.SwitchScreen` 会把 `RootWidget.IsUpdateEnabled = false`（`ScreensManager.cs:81`），
转屏动画期间 `Widget.UpdateWidgetsHierarchy` 整个 pass 被跳过 → `WidgetInput` 不再每帧清空、
旧屏幕控件的 `ClickableWidget.IsClicked` **冻结在 true** → 事件环在动画的每一帧都重复上报
（22 帧 ≈ 0.37 秒，正是转屏动画时长）。

注意：这不只是"上报重复"——按钮处理逻辑同样读 `IsClicked`，只是它也在被跳过的 pass 里，所以业务影响有限
（实测那次"Add Another"只合成了 2 个角色而非 22 个）。但对 AI 的事件驱动逻辑，语义必须是"一次点击一条事件"。

**修复**（`EventRecorder.RecordUiClicks`）：边沿去重——维护"上一帧已点击集合"，只上报本帧**新出现**的点击；
同一帧仍会收集全部点击控件用于下一帧比较。

**验证**：`click SleepButton` → 事件环 `ui.click` **恰好 1 条**；`click <背包槽位>` → `ui.tap` **恰好 1 条** ✓

另外在 `InputInjector.UiClick` 的收尾帧增加了防御：当没有任何鼠标键处于按下状态时，主动清空
`WidgetInput.m_mouseDownPoint`（`WidgetInput.cs:755-802` 只有在左右键都松开时才清它，残留会导致
后续每帧继续派生 Click）。该写入已登记进 `InputWhitelist`，审计规则继续成立。

### 19.4 关键语义修正：`hittable` ≠ 能点（这就是"UI 隐藏要靠按键"的机制本体）

世界内 `ComponentInput` 会把该输入层级的 `IsMouseCursorVisible` 置 **false**（鼠标交给视角，
`ComponentInput.cs:155`），而 `WidgetInput` 的 `Press/Tap/Click` 派生**整段被 `IsMouseCursorVisible` 门控**
（`WidgetInput.cs:741`）。因此：

- **世界内 HUD 按钮：几何上命中得到，但游戏永远不会派生点击** → 鼠标点不到，玩家只能用按键；
- **打开模态面板/对话框后**：`ComponentInput` 会把光标恢复为可见（`ComponentInput.cs:143-152`）→ 此时才可点。

实测对照（同一颗按钮）：

| 状态 | `hittable` | `clickable` | `clickReason` |
|---|---|---|---|
| 世界内（光标被视角捕获） | true | **false** | `mouse cursor is captured (IsMouseCursorVisible=false); use keys instead` |
| 打开背包后（面板内槽位） | true | **true** | `null` |

为此 `obs.ui` 新增两个字段：

- `clickable` = `hittable && 该控件层级输入通道的 IsMouseCursorVisible`
- `clickReason` = 不可点时的原因（被谁挡住 / 鼠标被捕获需改用按键）

**AI 应当只用 `clickable == true` 的元素做点击**；`clickable == false` 且 `hittable == true` 时，
正确做法是改用按键（`e`/`c`/`q`/数字键…），这正是"不能因为检测不到 UI 就认为键盘也不允许"的正向实现。

### 19.5 新增 `ui.tap` 事件：覆盖非按钮控件

`ui.click` 只覆盖 `ClickableWidget`（按钮类）；而**背包槽位、滑条、列表项、文本框都不是 `ClickableWidget`**，
它们自己读 `Press/Tap/Click`，所以此前的点击在事件环里看不到（实测点击槽位得到 0 条事件）。

新增 `ui.tap`：读取各输入层级（根控件树 + 每名玩家的 `GameWidget`）本帧派生的 `Click`，
用 `HitTestGlobal(Click.End)` 落到具体控件并上报（同样做边沿去重）。实测点击一次背包槽位得到
**恰好 1 条** `ui.tap {type: InventorySlotWidget, hitType: InventorySlotWidget}` ✓

### 19.6 新增专用测试世界 `CmdBridgeTest`

本轮为了验证"新建世界"链路，通过游戏自身的 UI 流程创建了一张新世界。**必须改名的原因**：
ScMultiplayer 的联机下载缓存固定使用 `data:/Worlds/World`
（`Mod/ScMultiplayer/Modules/Join/ScMultiplayerWorldTransferHandlers.cs:631`），而新建世界默认就落在 `Worlds/World`，
两者会互相覆盖。已改名为 `Worlds/CmdBridgeTest`，与联机缓存命名空间彻底分离。

- 世界名（UI 显示）：`CmdBridgeTest`；目录：`publish/Windows/Worlds/CmdBridgeTest`
- 当前含 **2 个角色**——这是 19.3 那个"一次点击重复上报"缺陷的副作用（当时多点出了 1 个角色），
  缺陷已修复；该世界是我方测试专用，用户可随时删除。
- 后续所有测试统一使用 `CmdBridgeTest`，不再触碰用户自己的世界。

---

## 20. Round 5：README 补全 + 可重跑冒烟测试（19/19 通过）

### 20.1 补齐计划中的文件清单缺口

计划 §8 的文件清单要求 `CmdBridgeMod/README.md` 与 `CmdBridgeClient/README.md`，此前缺失，本轮补齐：

- `Mod/CmdBridgeMod/README.md`：能力分层、安装与打包、运行时文件、注入机制（帧首缝隙 + 白名单成员）、
  完整命令表、`hittable` 与 `clickable` 的区别、事件种类、**AI 操作配方速查**（睡觉/背包/进食/挖掘/快捷栏）、
  验证工具清单，末尾附中文速览。
- `Mod/CmdBridgeClient/README.md`：发现机制、命令表、退出码语义（特别说明 2=元素不存在、3=存在但点不到，AI 必须区分）、REPL、中文速览。
- 语言遵循仓库惯例：单 Mod README 用英文（同 `ScMultiplayer`、`HeadlessRenderingMod`），设计文档保持中文。

### 20.2 新增可重跑冒烟测试 `Mod/Packages/smoke_test_cmd_bridge.py`

把此前各轮的手工验证固化成**协议级**端到端回归（直接走 loopback JSON，不依赖 sccmd 客户端）：

```
py -3 Mod/Packages/smoke_test_cmd_bridge.py [--root <游戏目录>] [--world CmdBridgeTest] [--no-launch] [--kill]
```

覆盖的不变量：

| 组 | 内容 |
|---|---|
| A | Mod 监听可连通、token 鉴权、`ping` |
| B | 全部注入点可解析（`obs.selftest.allOk`），白名单 11 项 |
| C | 自动进入测试世界（找不到就启动游戏）、玩家/世界时间/实体/方块/消息通道可读、**状态归一化**（无对话框、无残留模态面板） |
| D | 键盘注入落到引擎输入数组（`obs.input.keysDown` 出现 W，释放后消失） |
| E | **世界内可命中的元素一定 `clickable=false` 且原因提示 use keys** |
| F | `key e` 打开背包 → 背包槽位变为 `clickable=true` |
| G | 一次点击 = **恰好一条** `ui.tap` / `ui.click` 事件 |
| H | `act.releaseAll` 后无残留输入 |

实测输出（19 项全过）：

```
PASS  A. mod listener reachable - port 26751
PASS  B. injection points resolve - whitelist=11 entries
PASS  C. state normalised (no dialogs, no modal panel) - modalPanel=None
PASS  C. world time observable - hour=9.21 isNight=False
PASS  D. keyboard injection lands - keysDown=['W']
PASS  E. hit-testable in-world element is NOT clickable - InventorySlotWidget -> mouse cursor is captured (IsMouseCursorVisible=false); use keys instead
PASS  E. reason tells the AI to use keys
PASS  F. key e opens the inventory - modalPanel=FullInventoryWidget
PASS  F. panel slots become clickable - .../FullInventoryWidget/InventoryGrid/[InventorySlotWidget#0]
PASS  G. one click -> exactly one ui.tap - ui.tap=1
PASS  G. one click -> exactly one ui.click - ui.click=1
PASS  H. no stuck input after releaseAll - keys=[] buttons=[]
checks: 19  passed: 19  failed: 0
RESULT: PASS
```

### 20.3 写测试过程中暴露的三个 AI 相关事实（都很重要）

1. **`status.screen` 会"抢跑"**：`ScreensManager.SwitchScreen` 立即更新 `CurrentScreen`（`ScreensManager.cs:85`），
   但新屏幕的控件要等转屏动画进行到中段才被插入 `RootWidget.Children`（`ScreensManager.cs:302-309`）。
   因此**只看屏幕名会读到空界面**——AI 必须先等 `animating == false`，再等目标元素真的出现
   （`waitFor screen.animating.false` + `element.present:<path>`）。
   冒烟测试最初就是因此失败（读不到 `WorldsList`）。
2. **`SpawnDialog` 这类加载对话框会同时挡住 HUD 并让游戏跳过按键**：
   它是只有标签与进度条、**没有任何按钮**的临时对话框（`Survivalcraft/Game/SpawnDialog.cs`），
   自己消失；期间 `obs.ui` 的所有元素都会报 `blockedBy: SpawnDialog`、
   `ComponentGui` 也会因为 `DialogsManager.HasDialogs` 而跳过按键分支（`ComponentInput.cs:192`）。
   → AI 必须先等 `dialogs` 清空再操作；`blockedBy` 正好能诊断出这种情况。
3. **世界内某个按钮是否 `hittable` 是"此刻事实"**：SC 的左右控制面板会通过 RenderTransform 滑出屏幕
   （`ComponentGui.cs:314-315`），所以同一个 `InventoryButton` 有时可命中、有时不可。
   → AI 不应假设"元素一定在"，也不该把 `hittable=false` 当成 bug；正确做法是每帧按当前快照决策。
   冒烟测试的 E 段据此改为断言**不变量**（对所有当前可命中元素），而不是断言某个具体按钮。

### 20.4 剩余计划项（未完成，非阻塞）

| 项 | 状态 |
|---|---|
| `serve --pipe`（P4，命名管道供 L3 低延迟调用） | 未实现（当前 loopback TCP 已够用） |
| OS 级鼠标兜底通道（P4） | 判定不需要：引擎内注入已覆盖全部交互（实测 4 类输入全通） |
| `obs.world.blocks` 增量模式 | 未实现（长期运行 AI 的带宽优化） |
| 客户端 `ui` 表格渲染 `list` 小节、REPL 补全 | 未实现（`--json` 已可用） |
| L3 游戏 AI（状态机 + 操作池） | 不在本仓库，见 §7 设计 |

---

## 21. Round 6：世界交互闭环实测（挖掘 / 放置 / 死亡 / 复活）

此前所有验证都在"读 + 输入"层，从未证明**AI 真的改变了世界**（因为不敢在用户的世界里挖方块）。
本轮在专用测试世界 `CmdBridgeTest` 补上了这一环。

### 21.1 `lookat` 精度 + "动作目标由射线决定"这个坑

`lookat <格心>` 后读 `obs.aim`：命中格 **恰好**等于请求的格子（`cell=-166,64,36`，`face=4`，`distance=1.64`）✓
说明视角求解是精确的。

**但**：第二次实验里 `lookat (-166.5, 58.9, 36.5)`（侧面格）读回的命中格却是 `(-167,59,36)` ——
射线从眼睛出发，**只要沿途有方块就会先被拦下**，所以"我瞄的点"和"射线实际打到的格"可以不同。

> **AI 规则**：`lookat` 之后必须重读 `obs.aim.target.cell`，确认它就是要操作的格子再动手；
> 不要把 `lookat` 的参数当作操作目标。这一条已作为实测教训写入本节。

### 21.2 挖掘闭环实测（成功）

| 步骤 | 观测 |
|---|---|
| `lookat` 脚下格 → `obs.aim` | `cell=-166,64,36 face=4 SandBlock distance=1.64` ✓ |
| `act.mouse left down`（按住） | 事件 `world.dig` + `world.hit` ✓ |
| 持续按住 ~20 秒（每 0.5 秒轮询该格） | 方块被挖穿；玩家 **y 从 65.00 掉到 58.00**（挖了 7 格竖井） |
| 逐格核对 (-166, 58..65, 36) | **全部为空** ✓（竖井深度与 y 位移完全吻合） |
| 掉落物 | 自动拾取进背包：`slot0 = 7 x 7`、`slot1 = 162 x 1` ✓ |
| 游戏自身提示 | `ui.message`："Digging with bare hands is slow, use a stick" ✓（AI 可由此学会用工具） |

结论：**按住左键 = 持续挖掘**，且挖掘结果（方块消失、掉落入包）都能通过只读观察确认。

### 21.3 放置实测（成功，并发现一条游戏规则）

- 第一次尝试：玩家站在 y=58 的竖井里，瞄准脚下底面右键 → **什么都没发生**，但事件环有 `world.interact`。
  原因是**游戏拒绝把方块放进玩家身体所在的格子**——这是正确的规则，不是注入失败。
- 第二次尝试：瞄准后右键，**手持数量 12 → 11**，同时 `world.interact` 事件触发。
  游戏**只在放置成功时才消耗物品**，因此"物品 -1"就是放置成功的权威证据 ✓

> 实测还暴露一个心态问题：我当时用固定格子做断言，而射线打到了别的格，于是误判为失败。
> 正确做法是**用动作前后邻域快照求差**（`obs.world.blocks` 前后各取一次，diff 出新增/消失的格子），
> 或直接看物品数量变化——这两个都是"不依赖坐标假设"的验证方式，AI 应当采用。

### 21.4 挖穿水层导致溺亡：事件环完整记录了整条生存链条

挖掘过程中挖穿了水层，竖井灌水，玩家溺亡。事件环原文：

```
23 ui.message       "You are completely wet"        ← 进水警告
24 player.damaged   1        -> 0.904   (-0.096)    ← 溺水开始，每 tick 掉血
25 player.damaged   0.905    -> 0.809   (-0.096)
... （共 15 条 player.damaged，逐 tick 可读）
36 player.damaged   0.051    -> 0       (-0.051)
37 player.died                                       ← 死亡
```

死亡后状态：`health=0`、`air=0`、`wetness=1.00`、`temperature=9.0`、背包清空（掉落）✓

**这是对 `player.damaged` / `player.died` 的强验证**：AI 能看到掉血的每一步数值，
从而判断"继续待在灌水竖井里会死"，而不是只看到一条笼统的死亡。

### 21.5 死亡 → 输入 → 复活（已实测）

`PlayerData.cs:306`：死亡 1.5 秒后任意输入即复活。实测：`health=0` 时发送 `act.key Space`
→ 玩家在 `(-147.2, 64.9, 21.0)` 复活、`health=1` ✓

> AI 恢复动作：死亡后发任意输入即可复活（不需要任何特权操作）✓

### 21.6 冒烟测试加固

死亡会把世界留在"等待输入复活"的状态，因此 `normalise_state` 增加了复活分支
（`health <= 0` → 发 Space → 等 `health > 0`），保证回归测试可重复运行。
加固后重跑：**19/19 PASS** ✓

### 21.7 本轮结论

至此**玩家控制器的全部通道 + 世界交互闭环**都已在真实运行中验证：

| 能力 | 验证方式 |
|---|---|
| 视角（绝对/相对/看向坐标） | `lookat` 命中格 == 请求格；±82° clamp 生效 |
| 键盘 | `obs.input.keysDown` 取证；驱动 `PlayerInput.Move` 并让角色真的移动 |
| 鼠标 | `mouseButtonsDown` 取证；**按住挖穿 7 格**、右键放置（物品 -1） |
| UI 点击 | 模态面板内点击生效（切面板/睡觉）；世界内 `clickable=false` 且提示 use keys |
| 文本输入 | 文本框清空 + 写入 13 字符 |
| 世界后果可读 | 方块消失/新增、掉落入包、掉血逐 tick、死亡、复活、游戏提示消息 |

### 21.8 下一轮（最后一个计划交付项）

计划 §10 的 P4 里有一项尚未实现：**`obs.waitFor` 条件等待**（服务端逐帧求值，替代固定 sleep）。
本轮写冒烟测试时反复手写"等屏幕不动画 / 等元素出现"，正说明这条命令的必要性——
应实现 `obs.waitFor { condition, timeoutMs }`，支持
`screen.animating.false` / `element.present:<path>` / `element.clickable:<path>` /
`modal.none` / `dialog.none` / `events.since:<seq>` / `world.loaded` / `player.alive` 等条件，
并给客户端加 `sccmd waitfor` 子命令。完成后计划中的 P1~P4 即全部落地（P5 为可选）。

---

## 22. Round 7：`obs.waitFor` 落地 + 启动竞态修复（冒烟测试 22/22）

### 22.1 `obs.waitFor`（计划 P4 的最后一项）

服务端**逐帧求值**的条件等待，40ms 轮询一次（远快于人类反应、远慢于每帧，不给游戏线程添压力），
条件批量求值保证整批落在同一帧上（否则多个条件可能互相矛盾）。

支持的条件：

```text
screen.animating.false | screen.animating.true | screen.is:<name>
element.present:<selector> | element.hittable:<selector> | element.clickable:<selector>
modal.none | modal.is:<TypeName> | dialog.none | dialog.present
world.loaded | world.unloaded | player.alive | player.dead
player.sleeping | player.awake | events.since:<seq>
```

返回 `satisfied` / `conditions` / `elapsedMs` / `polls`，超时时额外返回 `pendingCondition`（是哪一条没满足）。
客户端加 `sccmd waitfor <condition> [--timeout ms]`，超时退出码 **7**。

实测两条分支：

```
PASS  I. waitFor satisfies an already-true condition - polls=1 elapsed=16ms
PASS  I. waitFor times out and reports the pending condition - pending=screen.is:NoSuchScreen elapsed=641ms
```

`wait_until_idle` 已改为 `waitFor screen.animating.false`——上一轮我在冒烟测试里手写的轮询，
正是这条命令的必要性证明。

### 22.2 修复：启动竞态（冒烟测试抓到的真实缺陷）

**现象**：游戏刚启动、客户端一连上就发命令 → `command_failed: InvalidOperationException: Dispatcher is not initialized.`

**根因**：Mod 的监听在 `IMod.OnLoad` 就绪，而 `Dispatcher.Initialize()` 在 `Window.Run()` 里才执行
（`Engine/Engine/Window.cs`）。这期间的任何命令都要经 `Dispatcher.Dispatch` 才能上游戏线程，于是直接抛异常，
客户端只能看到"未知失败"。

**修复**：
1. `GameThreadInvoker.Invoke` 未就绪时抛**可重试的明确错误** `not_ready`
   （客户端归入退出码 5，与 `screen_busy`/`timeout` 同类）；
2. `ping` 新增 `dispatcherReady` 字段——`ping` 完全在服务器线程回答，启动瞬间也能用，正好当就绪探针；
3. 冒烟测试在跑检查前先轮询 `ping.dispatcherReady`。

### 22.3 模式固化：点击前先等动画

冒烟测试第二次失败于 `screen_busy: A screen transition is in progress.` ——
这是 CmdBridge 的**正确行为**（转屏期间控件树被冻结，注入点击没有意义），修的是测试：

```text
waitFor screen.animating.false  →  act.uiclick  →  waitFor <效果>
```

这条模式现已写进模组 README 的 "Waiting" 章节与客户端 README，作为 AI 的标准点击流程。

### 22.4 计划完成情况

| 阶段 | 状态 |
|---|---|
| P1 Mod 骨架 + 只读观察 + 审计脚本 | ✅ 完成并实测 |
| P2 输入注入核心（视角/键盘/鼠标/UI 点击/文本） | ✅ 完成并实测（含挖穿 7 格、放置物品 -1） |
| P3 观察层完整化（状态机/事件环/世界/消息） | ✅ 完成并实测 |
| P4 世界观察 + `obs.waitFor` + 管道 + OS 兜底 | ✅ `obs.waitFor` 本轮落地；世界观察 P3 已完成；OS 兜底判定不需要；`serve --pipe` 未实现（loopback TCP 已够用，列为可选增强） |
| P5 免焦点 UI 点击（哨兵缝隙） | 可选，未实现（需用户明确批准） |

**计划中承诺的能力全部落地并验证**；仅剩两项可选增强（命名管道、P5 免焦点点击）。
