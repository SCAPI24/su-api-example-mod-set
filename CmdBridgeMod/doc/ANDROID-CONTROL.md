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
