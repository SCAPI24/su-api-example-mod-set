# Android 触摸控制（CmdBridgeMod）

> 本文件随 `.scmod` 一起分发（打包在 `Content/doc/ANDROID-CONTROL.md`），
> 因此拿到 mod 的 AI/使用者不需要访问仓库也能看到这套语义。
> 运行时也可以直接执行 `guide.android` 让控制端打印要点。

## 一、引擎侧语义（实测，Survivalcraft 2.4.10 fork）
| 操作 | 手指语义 | 说明 |
|---|---|---|
| **移动** | 手指落在**左下角移动区**内，按住不放并朝某方向拖动 | 控件为 `TouchInputWidget name=Move`；实测矩形 `x=0 y=880 w=320 h=320`，中心 `(160,1040)`（分辨率相关，请用 `ui` 实时取中心，不要写死） |
| **视角** | 手指落在**没有按钮的区域**，按住拖动 | `Settings.xml` 的 `LookControlMode=EntireScreen` 时整屏可转视角 |
| **挖掘** | 手指落在无按钮区域**按住不动**约 0.2~0.5s | 这就是"只是按住也会挖地面"的原因 |
| **跳跃** | 在 `Move`（或 `Look`）上**轻点一下**（按下后立即松开） | 也可用 `act.jump pad=Look` |
| **UI 按钮** | 点按按钮区域 | 与触摸同一条注入路径 |

前置条件：**游戏在前台**且**软键盘收起**（`Touch.Android.cs:115` 的前置判断）。
Android 上引擎**不绘制光标**，所以看不到指针，只能靠坐标与状态判定。

## 二、推荐用法：语义动作（无需知道任何坐标）
```
act.move dir=forward holdMs=1200     # 前进；dir=back|left|right 同理
act.jump                             # 跳跃（默认在 Move 上轻点；pad=Look 可换）
act.look yawDeg=<角度> pitchDeg=<角度>   # 转视角（引擎级、精确；也可 lookdelta <dYaw> <dPitch>）
guide.android                        # 打印本页要点
```
**想完全走 Android 手指语义转视角**（无按钮区按住拖动）：
`ui.session.begin` → `ui.move x=1000 y=600` → `ui.press` → `ui.move x=1120 y=600`（多帧重申）→ `ui.release` → `ui.session.end`。

## 三、底层会话（需要精确控制时）
```
ui.session.begin
ui.move x=<起点x> y=<起点y>     # ⚠ 必须先定位：Press 本身不带坐标
ui.press                        # 也可 ui.press x=.. y=..（会自动先定位）
ui.move x=<目标x> y=<目标y>      # 每帧重申一次位置 = 保持按住（拖动/长按）
ui.release                      # 也可 ui.release x=.. y=..（先定位再抬起）
ui.session.end
```
要点：
- **顺序**：先 `ui.move` 再 `ui.press`。颠倒会把"按下"落在**上一次操作结束的位置**，
  实测表现就是"移动"和"视角"互换（诊断日志里 `press before … at <上一次的坐标>`）。
- 坐标点击 `act.uiclick x= y=` / 列表行 `click <list> --at x y` 走的都是这条会话。
- Android 上会话**不写软光标**，所以整屏不会出现"光标飞过去"的假象。

## 四、常见问题
- **只想走却挖了地**：按住不动就是挖掘（见上表）；移动必须落在 `Move` 区内拖动。
- **拖动完全没反应**：① 起点不在 `Move` 区；② `ui.press` 没有先定位；③ 游戏不在前台或软键盘弹出。
- **跳跃没生效**：轻点必须是"按下+松开"；`holdMs` 太大会变成移动（默认 140ms 可用）。
- **短距离位移看不出效果**：先看 `player` 的 `position`；角色可能被地形挡住。

## act.dig：只挖一格 + 成败判定（实测）

- 用法：`act.dig [x= y=] [holdMs=200] [maxDistance=8]`
- **创造模式挖掘时间 = 0**（`ComponentMiner.CalculateDigTime`：Creative 且可挖 → `0f`），所以按住越久越会**顺着射线连挖一串**（实测 0.36s 挖掉 4~7 格）。本接口用最小帧数按下即松。
- 返回字段：`cell`、`beforeValue`、`afterValue`、`removed`（该处是否真的变化）、`nowAir`（整格是否清空）、`afterBlockType`、`beforeAim`/`afterAim`。
- **导线按面存储**：`WireBlock.GetDigValue` 只清 `raycastResult.CollisionBoxIndex` 那一面的导线 → 只掉一面时 `removed=true` 而 `nowAir=false`、`afterBlockType` 仍是 `WireBlock`；要把整格清空必须**对着每一面各挖一次**。
- 安卓用**触摸点**决定目标（全屏无按钮区都可挖），Windows 用**准星**；`x/y` 默认屏幕正中。
- `world blocks [半径]`：半径是**位置参数**，且结果**截断在 256 条**（半径调大反而可能看不到导线，用默认 4 或小半径）。
- 主机侧修复要点（联机时"挖掉又被还原"的根因）：挖掘判定只比 contents（掩掉 data 位）；请求里必须带客户端真实命中的 `CollisionBoxIndex`（否则主机按错面重算＝什么都没挖，再把权威值广播回来就是还原）；内容匹配即接受，以主机结果为准。

## 面挖掘的标准流程（实测）

1. 用 `world blocks` 找到目标方块/导线格与它的依附面；
2. `lookat <该面中心>`（= 格中心 + 0.5 × 面法线），把该面转到准星/屏幕中心；
3. `act.dig`（默认 holdMs=200ms；创造模式挖时=0，但按住时长必须够引擎进入挖掘状态——实测 34ms 挖不到任何东西，200ms 可稳定挖掉一格）；
4. 读返回的 `removed`/`nowAir` 判断是否挖掉（`removed=true` 即该处确实变化；导线可能被邻近算法一次带走整格，无需按面区分计数）。
