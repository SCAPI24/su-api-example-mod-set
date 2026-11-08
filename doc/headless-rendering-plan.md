# HeadlessRenderingMod 待办与改进计划

> 状态：**部分已实施，其余待做**
> 产物：`Mod/HeadlessRenderingMod/`（scmod）
> 本文档记录**已确认要做、但尚未实施**的改进项，实施后在此标注完成并简述改法。

---

## 0. 文档定位

Linux 无 GPU 运行能力（6 个空壳库）与控制台 TUI 的跨平台改造**已完成并发布**（1.4.1）。
本文档只跟踪**后续打磨项**，避免"口头记下的待办"丢失。

已完成能力简述：

| 能力 | 说明 |
|---|---|
| Linux 无 GPU 启动 | 6 个空壳库（gbm/drm/EGL/GLESv2/libinput/libc），全部嵌入 mod、独立文件名落盘，不覆盖系统库 |
| 纯文本 TUI | `Console.ReadKey` + 清屏重绘；Windows 抢/建控制台，Linux 直接用调用方终端 |
| 底部状态栏 | 原 `Console.Title`（Windows 专有）改为屏幕内状态栏，实时刷新 |
| 卸载可恢复 | 空壳用 `*-headless.so`，系统库零污染；Engine 仅加 try/catch |

---

## 1. 世界编辑器：二维方向键导航

**现状**：`Console/WindowsConsoleController.cs` → `EditWorldPageCore` 只处理 `↑↓`，
光标按一维序号在网格项之间顺序移动；`←→` 完全无响应。

例：4 列布局下

```
[World name: x]  [Seed: <>]#  [Game mode: Survival]  [Starting position: Easy]
[World options...]
```

按 `↓` 从 `Seed` 直接跳到 `Game mode`（同行右边），不是下一行。

**目标**：真正的二维导航

| 键 | 行为 |
|---|---|
| `←` / `→` | 在同一行内左右移动；行首/行尾按需环绕或停住 |
| `↑` / `↓` | 按**同一列**跳到相邻行 |

**依据**：用户 2026-11-08 明确"后续改成二维的"。

**实现要点**：

- `BuildWorldEditorGrid` 已产出列布局（每行列数、`ColumnGap`、`MinimumColumns=2`、`MaximumColumns=4`），可复用于行列换算；
- 末行项目数不足时，目标列不存在则**落到该行最后一列**（不要跳到空白）；
- 现有 `Home` / `End` 语义保留（首项 / 末项）。

---

## 2. 世界编辑器：Seed 留空显示 `<>`

**现状**：`BuildNewWorldPage` 第 1119 行

```csharp
"Seed: " + (draft.Seed.Length == 0 ? "<random>" : draft.Seed)
```

`PromptText("Seed", draft.Seed, "random")` 的 `"random"` 只是显示用（`defaultDisplay`），
**不会被当成种子值** —— 直接回车返回的仍是空串，游戏侧按 `Time.RealTime * 1000` 取随机种子
（`Survivalcraft/Game/WorldsManager.cs:263`），所以**当前确实是随机的**。

**目标**：显示简化，避免用户误以为要输入 `random` 这三个字

```
[Seed: <>]          ← 留空（每次创建都是新地图）
[Seed: abc]         ← 已指定（按字符哈希，可复现）
```

**依据**：用户 2026-11-08 原话"你这里就显示留空就行了，有一个 <> 来指示就够了"。

**同时**：把 `PromptText` 的 `"random"` 显示参数去掉，提示里不再出现 `random`。

---

## 3. 皮肤列表：`Walter | $Male1` → `Walter |$Walter`

**现状**：`SelectSkin` 第 1742 行

```csharp
labels.Add(skin["displayName"] + " | " + skin["name"]);
```

内置皮肤显示成 `Walter | $Male1`，右侧暴露的是内部资源名（`$Male1` 实际对应纹理
`Textures/Creatures/HumanMale1`），对用户无意义。

**目标**

```
Walter |$Walter        ← 内置皮肤：$ + 显示名
MySkin |MySkin         ← 自定义 .scskin：原样
```

**要点**：

- 是否内置按 `name` 是否以 `$` 开头判断（也可用返回里的 `builtIn` 字段）；
- **提交给游戏的仍是原始 `skin["name"]`（`$Male1`）**，只改显示层，不影响功能；
- 保留 `$` 前缀的作用：区分游戏自带皮肤与用户导入的 `.scskin`。

**依据**：用户 2026-11-08 原话"这里的表述自带的，就改为 Walter |$Walter"。

---

## 4. Server Status 行内拼接网络流量

**现状**：`BuildMainMenuItems` 里 `Server Status` 是一项固定文字：

```csharp
private string[] BuildMainMenuItems() => new[]
{
    GetWorldMenuLabel(), "Manage Players", "Multiplayer Settings",
    "Server Status", "Command Line", "Shutdown"
};
```

联机遥测已存在（`m_multiplayerTelemetry`，由 `UpdateMultiplayerTelemetry()` 每 5 秒刷新），
但只显示在底部状态栏。

**目标**：**不新增菜单项**，把流量直接拼到 `Server Status` 这一项的标题里。

未创建房间：

```
> World-[]
  Manage Players
  Multiplayer Settings
  Server Status-[Offline]
  Command Line
  Shutdown
```

已创建房间：

```
  Server Status-[2 clients | 1.2 KB/s ↓ 0.4 KB/s ↑]
```

**格式规则**：

| 状态 | 标签 |
|---|---|
| 未创建房间 | `Server Status-[Offline]` |
| 已创建房间 | `Server Status-[N clients \| X KB/s ↓ Y KB/s ↑]` |

（`Offline` 首字母大写；已开房时 `clients` 用复数形式，单位保留 `KB/s`。）

**要点**：

- 改动点在 `BuildMainMenuItems`：把 `"Server Status"` 换成 `GetServerStatusLabel()`，
  与 `GetWorldMenuLabel()` 同样的"动态标签"做法（主菜单每帧重新求值，流量自动跟着刷新）；
- 流量数字取现有遥测里的上下行速率。当前 `m_multiplayerTelemetry` 是给状态栏用的整串文字，
  形如 `Clients 2 | UDP OUT 1.2 KB/s (18 pkt/s) | UDP IN 0.4 KB/s (9 pkt/s) | Join idle`；
  **推荐改成保存结构化数值**、由两处各自格式化，而不是去解析字符串；
- **"未创建房间"的判定需先查清**：从联机 mod 查询房间状态（`roomCreated` / `hosting` 一类字段），
  **不得凭猜**；查不到明确字段就先停下来确认；
- 底部状态栏保持现状（仍是完整遥测串），这一项只是同一份数据的另一种呈现。

**依据**：用户 2026-11-08 给出的两个样例
`Server Status-[Offline]` 与 `Server Status-[2 clients | 1.2 KB/s ↓ 0.4 KB/s ↑]`。

---

## 5. Multiplayer Settings 新增 Host Room / ChatRoom

**现状**：`RunMultiplayerSettingsMenu` 只有三项：

```
Multiplayer Hosting
Multiplayer Bandwidth
Multiplayer Log Mode: <on|off>
Back
```

**目标**：在里面加上两个功能

```
Multiplayer Settings
> Host Room
  BBS
  Multiplayer Hosting
  Multiplayer Bandwidth
  Multiplayer Log Mode: <on|off>
  Back
```

### 5.1 Host Room（创建房间）

**语义**：把**当前正在玩的世界**开成房间。这与联机 Mod 的既定入口一致 ——
`Mod/SuMultiplayer/Modules/SessionModule.cs` 开头写明：

> 建房 = 游戏内 CR 按钮 → `CreateRoom`（世界是玩家正在玩的那个）；
> 本模块**不在启动时按配置自己建房或自己连** —— 那会让"联机"在没有玩家的操作时凭空发生。

所以控制台这一项**等价于替玩家按下 CR 按钮**，不是"启动时自动开房"。

**前置条件**（按 `CreateRoom` 实际校验处理，不自己猜）：

| 条件 | `CreateRoom` 返回的 reason |
|---|---|
| 已经在开房 | `Already hosting room "<name>"` |
| 地形/传输模块未激活 | `Transport module is unavailable ...` |
| 世界名为空（不在世界里） | `Cannot read the current world name; create a room from inside a world.` |
| 端口段全部占用 | `Automatic port allocation failed (<range> are all unavailable): ...` |

**实现方向**：

- 名字与 gameMode 取自当前世界设置，**不要弹输入框让用户手填**（那是自创入口）；
- 需要一条到联机 Mod 的通道。headless Mod **不引用** SuMultiplayer 程序集（两边可独立部署），
  现有桥接方式是 `m_eventBus.TriggerEvent("ScMultiplayer.ServerSettings", ...)`；
  Host Room 需要同类通道，**具体事件名必须先查清**（见 5.3）；
- 开房成功后显示端口与房间名；失败时把 `reason` 原样显示（不吞错误）。

### 5.2 ChatRoom（聊天室）

**原需求里的 "BBS" 已改为 "ChatRoom"**（用户 2026-11-08 后续更正）。

**语义**：显示**游戏内的聊天消息**，每行一条；顶部两个操作项。

**界面形态**：

```
Multiplayer Settings > ChatRoom
Up/Down select  Enter send  Esc back

> Send message
  Back

[12:34] Steve: hello
[12:35] Alex: hi there
[12:36] <system> joined
```

**要求**（用户原话拆解）：

| 要求 | 说明 |
|---|---|
| 每行显示一条 | 一条聊天消息占一行，按时间顺序 |
| 只显示游戏内的聊天 | 不显示外部来源（不是 HTTP 公告板） |
| 顶部两个选项 | ① `Send message` ② `Back`（英文标签） |
| Enter 后可输入 | 选 `Send message` 回车 → 进入文本输入模式 |

**待查清（已派子代理调查）**：

| # | 问题 |
|---|---|
| 1 | 游戏本体聊天数据结构（`ChatLine`? 存在哪个 Subsystem/Manager？） |
| 2 | 能否订阅/拦截聊天消息（有没有事件可挂） |
| 3 | 能否程序化发送一条聊天消息（无头服务器没有真实玩家，如何伪造身份或走系统消息） |
| 4 | SuMultiplayer 是否同步聊天，消息格式如何 |

**已知约束**：headless Mod 不引用 SuMultiplayer 程序集，跨 mod 通信只能走
`IModEventBus` 事件或反射。

**依据**：用户 2026-11-08 原话"Multiplayer Settings中加上Host Room，BBS这两个功能"，
随后更正为 **ChatRoom**（"BBS换成ChatRoom进入后，按每行显示一条，只显示游戏内的聊天"）。

---

## 6. Multiplayer Settings 新增 RoomSetting（二维表，含 LogMode）

**语义**：把**游戏内 IF 界面的房间设置**搬到控制台，用二维表展示与编辑，
与游戏内行为一致（**逐项即时保存**，没有 Apply 步骤）。

**数据来源**（已查清，`Mod/SuMultiplayer/Engine/WorldJoinSettings.cs`）：

| 项 | 字段 | 类型 |
|---|---|---|
| Auto host | `AutoHostCurrentWorld` | bool |
| Auto approve | `AutoApproveJoins` | bool |
| Host port | `HostPort`（0 = 自动，51459 起 10 个槽位） | int |
| LogMode | `LogMode-[Runtime]` / `LogMode-[Save]` | 二选一（`HeadlessSuMultiplayerBridge`） |
| Client furniture delete | `AllowClientFurnitureDelete` | bool |
| Client furniture import | `AllowFurnitureImport` | bool |
| Client furniture export | `AllowFurnitureExport` | bool |

设置保存在**世界目录**的 `SuMultiplayerWorld.xml`（不是全局配置）。

**界面形态**（2 列，每列左对齐，标签按游戏内原样）：

```
Multiplayer Settings > RoomSetting
Arrows move  Enter edit  Esc back

> [Auto host: OFF]              [Auto approve: OFF]
  [Host port: Auto]             [LogMode-[Runtime]]
  [Client furniture delete: OFF] [Client furniture import: OFF]
  [Client furniture export: OFF]
```

**要求**（用户 2026-11-08 确认）：

| 要求 | 说明 |
|---|---|
| 二维方向键 | `←→` 行内移动，`↑↓` 按同列跨行；末行项目不足时落到该行最后一列 |
| 每列左对齐 | 与游戏内一致；列宽按该列最宽标签算，不用固定宽度 |
| 标签原样 | 不做缩写，保持 `Auto host` / `Client furniture delete` 等英文原样 |
| 即时保存 | `Enter` 改动后立刻生效（与游戏内行为一致），**没有 Apply** |
| 选中标记 | `#` 标在当前项之后（沿用世界编辑器既有做法） |

**编辑方式**：

| 项类型 | Enter 后 |
|---|---|
| 布尔 | 直接切换 ON/OFF 并保存 |
| Host port | 输入框（留空 = Auto），保存 |
| LogMode | 切 `[Runtime]`/`[Save]` 并保存（复用 `TrySetModuleLogMode`，失败原样显示原因） |

**实现要点**：

- 复用世界编辑器已有的网格能力：`BuildWorldEditorGrid` / `DisplayWidth`（CJK 宽度）/ `SelectionMarker`，
  但 RoomSetting 是独立的可视化，不要把世界编辑器的 `WorldCreationDraft` 逻辑耦合进来；
- 列宽按每列最长标签的显示宽度（`DisplayWidth`，中文/全角算 2）计算，`ColumnGap = 2`；
- 重绘用"帧文本对比 + 变了才刷"（沿用世界编辑器的 `BuildWorldEditorFrame` 思路），避免闪烁；
- 保存走联机 Mod 通道：与现有 `UpdateMultiplayerSettings`（`ScMultiplayer.ServerSettings` 事件）**不同** ——
  该事件在 SuMultiplayer 里不存在（见 5.3），所以 RoomSetting 的读写通道**必须先查清**，
  必要时给 SuMultiplayer 加事件接口（与 Host Room 同一个问题）。

**待查清**：

| # | 问题 |
|---|---|
| 1 | 控制台如何读写 `WorldJoinSettings`（SuMultiplayer 未暴露该设置的读写事件） |
| 2 | 读写是否需要"主机"身份校验（非主机时联机 Mod 会拒绝，需原样显示原因） |

**依据**：用户 2026-11-08 原话"RoomSetting，就是游戏中的多人房间的设置功能，你按照二维表的方式放里面"、
"光标移动没问题，就按原样显示就行了，然后你还是每列左对齐，就和在游戏中操作一样，即时保存"、
"LogMode-[Runtime]/[Save]也挪动到RoomSetting中"。

---

## 7. 实施顺序建议

1. **Seed 显示**（最小，1 行）
2. **皮肤显示**（1 处拼字符串）
3. **二维导航**（RoomSetting 的前置能力，世界编辑器与 RoomSetting 共用）
4. **RoomSetting 二维表（含 LogMode）**（依赖设置读写通道查清）
5. **Server Status 行内流量**（需先查房间状态字段）
6. **Multiplayer Settings：Host Room / ChatRoom**（依赖待查清三项；ChatRoom 等接口调查）
