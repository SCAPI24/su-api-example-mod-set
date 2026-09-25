# 会话问题清单（2026-09-24/25）：玩家领地 P1/P2

> 本文件只记**这一轮"玩家领地（区域认领 + 归属执法）"实施/实测**中踩到的问题与结论，
> 证据来自平板（主机）+ Windows（客户端）实测日志、GmMod 面板、`ScMultiplayerRegions.xml` 与 CmdBridge 观测。
> 设计口径见 `REGION-CLAIM-DESIGN.md`；通用联机纪律见 `DEV-RULES.md` 第十四节。

---

## 1. 跨程序集 mod 引用被混淆成员 → 运行期 `MissingFieldException`（P1 阶段，**真 bug**）

**现象**：平板 GM 面板点「玩家领地」的瞬间抛

```
ERROR: Field not found: ScMultiplayer.ScMultiplayer ScMultiplayer.ScMultiplayer.currentInstance
       Due to: Could not find field in class
   at GmMod.GmUiComponent.…(Object)
   at Game.ListSelectionDialog.Dismiss(Object result)
```

并把游戏**踢回主菜单**（对话框 `Dismiss` 里抛异常，界面栈被拆掉）。

**根因**：`Obfuscar.xml` 里 `<SkipType name="ScMultiplayer.ScMultiplayer" />` **只保类型名**，
成员照样被改名（`RenameFields/RenameMethods=true`）；GmMod 是**另一个程序集**，按名字引用该字段，
运行期自然找不到。

**试过但不可行**：给主类补 `skipMethods="true"` →
`Obfuscar.ObfuscarException: Inconsistent virtual method obfuscation state detected`，
指名 `Ports.IMultiplayerUiCommandPort::ShowCreateRoomDialog`（主类实现了内部接口，跳过一半就自相矛盾）。

**处置（已落地）**：跨程序集**只走静态门面类**，整类跳过混淆（与既有 `DataModificationTool` 同套路）：

- `ScMultiplayer.RegionSelectionApi`（P1：选区）
- `ScMultiplayer.RegionClaimApi`（P2：建立/放弃/列表）
- `Obfuscar.xml` 里均加 `skipMethods/skipFields/skipProperties/skipEvents="true"`

> 教训：给独立 mod 用的 API 一律**新建静态门面**，不要去跳过主类的成员。

---

## 2. 混淆后枚举 `ToString()` 变成空串（P2 阶段）

主机日志出现 `Region claim  : #1, sequence=1`（操作名空白）——代码写的是
`operation.ToString().ToLowerInvariant()`，而枚举成员同样是**字段**，会被 Obfuscar 改名
（`UseUnicodeNames=true` 时还会变成不可见字符）。

**处置**：审计/日志里的名字改成**显式分支**取字面量（`add`/`remove`/`replace`），不依赖 `ToString()`。

---

## 3. 子菜单没有返回入口 = 死路（P2 阶段）

GmMod 的 `ListSelectionDialog` **没有返回按钮**（引擎侧只有 CoverWidget 点击取消）。
P2 新增的「建立领地 / 领地列表 / 领地详情 / 放弃确认」如果不给返回项，用户（和自动化脚本）
进去就出不来——实测脚本因此卡在「领地列表」状态无法继续。

**处置**：每个子菜单都补一行 `← 返回…`（`Action` 重新打开父菜单）：
`ShowRegionPointMenu`/`ShowRegionCreateMenu`/`ShowRegionListMenu`/`ShowRegionClaimMenu`/`ConfirmDropRegionClaim`。

---

## 4. 增量同步要能"自愈"：可靠有序流也会跳包 / 断线（P2 阶段）

实测（2026-09-24 07:12–07:16）：

```
PC 07:12:46 ERROR: [ScMP] Client error: 远程主机强迫关闭了一个现有的连接。
PC 07:16:18 ERROR: [ScMP] Client error: Reliable sequenced stream from <tablet> stalled for 2.0s, skipped 7 message(s) to resume
```

PC 端掉线 6 分钟，期间主机广播的 `add #2` 没送达（客户端面板仍是 1 块）。
也就是说**"带序号 + 可靠通道"仍可能丢一条增量**。

**处置（已落地）**：

- 客户端按序号应用：`<=` 已应用序号 → 丢弃；`== +1` → 落地；**跳号 → 回 `RequestSync` 让主机补发整表**；
- 客户端在**加入流程结束**和**主机重连成功**（`Host reconnect succeeded`）后各主动补要一次整表；
- 报文读数前卡上限（512 块 / 每块 64 owner），坏包不会让对端按超大计数分配内存。

---

## 5. CmdBridge：Windows 上"点列表行"不能用 `--at`（工具链，非产品）

- `act.uiclick --at x y` 走的是**鼠标会话**（MoveTo→Press→Release 各一帧）。Android 触摸路径下坐标有效；
  **Windows 上落点是错的**：实测传 `(960,802)`（第 8 行）实际选中第 4 行——命中点被按
  `设计坐标×GlobalScale`（本机 ≈0.65）换算，或用了引擎当前鼠标位置，总之**不可预测**。
  移动真实光标（`Cursor.Position`）也无效（GameScreen 捕获鼠标）。
- **可行做法**：`sccmd click list:<列表选择器>#<行号>`（`RowIndex` 语义目标）——
  服务端**在点击这一刻现算坐标**，走 `direct` 模式（单帧合成 Tap+Click，绕过输入层），Windows/Android 都稳。
  实测：`click list:ListSelectionDialog.List#7` 在 PC 上一次命中「玩家领地」。
- 两个相关坑：`CoverWidget` **没有 name**（`ui --all` 名字列为空）→ 选择器点不到它，只能用坐标；
  GmMod 的 `MenuEntry` **没有 `Name` 字段** → CmdBridge 的 `text=<行文字>` 匹配会退化成
  `ToString()`（全是 `GmMod.GmUiComponent+MenuEntry`），所以 GM 菜单只能按**行号**寻址。
  （如需按文字寻址，给 `MenuEntry` 加一个 `Name` 字段即可。）

---

## 6. P4 阶段新踩到的两个坑

### 6.1 `ContentManager.Get<BitmapFont>` 在 `Component.Load()` 里拿不到字体（P4）

`SuComponentRegionOverlay` 最初在 `Load()` 里取 `Fonts/Pericles18` 并缓存，结果**编号一个字都不画**
（填充/线框都正常，就是没有数字）。改成**在绘制期懒加载**（与
`CircuitAutoRouter.SubsystemCircuitRouter.Draw` 一致的取法）后编号立刻出现。

> 教训：3D 字体这类资源按引擎既有实现的时机取（绘制期），不要想当然挪到 `Load()`；
> 而且"没画出来"要用可视证据确认，不能靠读代码猜。

### 6.2 Windows 上"GM 按钮"点不到：右侧 HUD 条被排到屏幕外（工具链/平台）

`ComponentGui` 把 `MoreContents`（GmMod 的 GM 按钮就挂在里面）的可见性绑在 `MoreButton.IsChecked` 上，
而 Windows 上整条右侧控制栏被排到视口**右侧之外**：

```
MoreButton: rect=1920,5 101x101 hittable=False
  reason=off-screen: center (1970.6,55.3) is outside the 1920x1080 screen area
```

- 窗口最大化后同样如此（2560 宽 → `rect=2560,6`）：**贴右边缘外侧**布局，与窗口尺寸无关；
- 引擎里也没有对应快捷键（`ComponentInput` 只映射 P/T/L/K/J/V/F/C/E/G/H/R 等，没有"更多"面板）；
- 结论：**PC（Windows）端目前无法用点击打开 GM 面板**；平板（主机+GM）不受影响。
  早期 PC 能驱动面板，是当时布局处于"陈旧布局"状态（面板已展开且未复位）。

可选修法（待定）：
1. GmMod 加**桌面快捷键**打开 GM 菜单（引擎里 M/N/O/U/I/Y/Z/X/B 等未占用）——方便真人，
   但 CmdBridge 的 `key` 注入在玩家控制层，自动化不一定能触发；
2. GmMod 通过 CmdBridge 的**扩展命令注册表**注册 `gm.menu`（参考 PlayerAiMod 的 `ai.*`）——
   自动化与远端都能开面板，最通用。

---

## 7. 本轮未覆盖

- 上限拒绝（单体 >64×64、第 129 块、越界坐标）**没实测**：需要手填坐标，而平板 IME 不适合自动化。
- 执法（挖/放/拾取/点燃/爆炸/流体）是 P5–P7：目前只做了数据、同步与区域展示（P5 代码见 §8）。
- **编号可见性已定案**：设计稿写"5 个外表面中央 + 字号约 0.04"，而领地全高（0–255）⇒ 面中心在
  y≈128、站地上几乎看不见（面侧对镜头）。**用户决定"只保留贴地那一份"** ⇒ 改为画在 4 个侧面、
  按该面中心列的地表高度 +1.2 定位（地表高度缓存 1 秒），字号仍 0.04；顶面与面中心不再画。
  实测：站在面外约 10 格、水平视角，平板与 PC 截图都能看到编号。

---

## 8. P5 阶段的三个坑（2026-09-24 下午）

### 8.1 Windows 端游戏进程被**外部反复结束**（挡住 P5 实测）

约 1 小时内 4 次：08:52 加入后不久、18:1x、18:2x 各一次消失，`sccmd status` 报
`Survivalcraft is not running`、`SocketException: 目标计算机积极拒绝`。

- `Game.log` 最后一次输出是正常的 `PerformanceManager Measurement` / `Client catch-up complete`，
  **没有任何异常或崩溃栈**；Windows 应用日志里也**没有** `Application Error` / `.NET Runtime` / `Application Hang`。
- ⇒ 不是崩溃，是**进程被外部终止**（关窗口/被别的进程杀掉）；同一时段 PC 上有人在使用（CmdBridge 日志里
  出现过 `搜索` 抢占前台、focus policy attached/detached 反复切换）。
- 影响：自动化脚本每次跑到一半就没客户端了。**复盘时先确认 PC 端游戏还活着**。

### 8.2 平板端只要开着对话框，`hold W` 就不会移动

实测：脚本上一步把手上的 GM 面板留着没关，下一步"走到目标点"的 `hold W` 连按 5 次坐标**一模一样**
（(-193.5,65.0,116.6)）。对话框吃掉了移动输入。
⇒ 任何"先走位再操作"的脚本，**必须先关面板**（用"无动作行"或「← 返回领地菜单」把对话框收掉）。

### 8.3 CmdBridge 的 `key` 注入确实进到了引擎 `Keyboard`

用户提示"按 E 打开 HUD"后实测：`sccmd key E` 让 PC 打开了背包界面 ⇒ 注入路径**能**触发引擎级
`IsKeyDownOnce`（与早期"只能到玩家控制层"的结论不同，可复用于桌面快捷键类验证）。
但 `MoreButton` 仍然 `rect.x == 视口宽度`（屏幕外），所以 E 并不能让 GM 按钮变成可点；
若要在 PC 上开 GM 面板，仍需另加入口（GmMod 桌面快捷键或 CmdBridge 扩展命令 `gm.menu`）——待定。

### 8.4 P5 实测终于跑通：过程中的可用/坑（复盘清单）

**跑通的路径**（脚本：`%TEMP%\scfetch\verify-p5q.ps1` + `verify-p5r.ps1` 的组合）：
1. 平板（主机）先就地建一块领地（`点1=点2=自己` 得 1×1；或 `点1 → 走 3s → 点2` 得 4×2，能容忍两人站位的 1 格漂移）；
2. 主机 GM 面板 `玩家操作 → 选 PC 玩家 → 复活点设为"我的当前位置" → 送回睡觉点`，把 PC **传送**到平板身边
   （无需长距离走位）；
3. PC 用 `raw act.dig`（**不带 cell**，挖准星处）反复挖 → 主机先 `dig.req` 收到请求，再否决并回滚；
4. 证据在 **PC 端 `Game.log`**：`[Chat] Client0 ScMP: 这块区域属于 Android User（领地 #12），修改已否决`（1 秒节流）。

**这条链路上的坑（按踩的顺序）**：
1. **`sccmd` 的 `--port` 发现依赖进程名**：Windows 端进程被杀/重启后 `--port 26751` 会报
   `discovery_failed: Survivalcraft is not running`；改用 `--root <游戏根目录>`（直接读
   `CmdBridge.runtime.json`）更稳。
2. **被杀掉的脚本会留下"卡住的按键"**：`hold W` 之后脚本被中断 ⇒ 平板上 `keysDown=["W"]` 一直按着，
   之后再走位就"贴着墙原地不动"。**任何走位前先 `release --all`**（`sccmd release --all`）。
3. **对话框会吃掉移动输入**：GM 面板开着时 `hold W` 完全不动（实测坐标连续 5 次一模一样）。
   走位前必须把面板收掉（点"无动作行"或「← 返回」）。
4. **列表行文字在 `ui --all` 里会被截断**（如 `Basil Lv 1.74 [c…`）⇒ 别用 `[client N]` 这类**行尾**片段
   去找行；用"包含 Basil 且不含（主机）"这种**行首/中段**特征（或直接按行号）。
5. **主机自己的 GM 操作也会进 DM 审批队列**：`玩家操作 → 复活点` 提交后，平板上弹出
   `Allow / Reject / Always allow this player`，**必须点 Allow 才落地**（实测：不点 Allow，PC 不会被传送）。
   自动化脚本要先把这类挂起审批点掉。
6. **`act.dig cell=x,y,z` 需要"准星真的打到该格的面"**，否则回 `aimMiss: true`；
   PC 在水下时准星先命中水（`WaterBlock`），带 cell 的挖经常 aimMiss ⇒ 用**不带 cell 的 `act.dig`**
   （挖准星处）最省事。
7. **`PublishServerAudit` 在"平板当主机"时没有落到 `Logs/Server/ScMP-op-<日期>.log`**
   （该文件只有 `dig.req` 这类 `ScMultiplayerOperationLog.Write`）——验证时以**客户端聊天通知**为准。

