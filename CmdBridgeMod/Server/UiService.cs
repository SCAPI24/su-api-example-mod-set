using Engine;
using Engine.Input;
using Game;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace CmdBridgeMod
{
    /// <summary>
    /// **UI 定位 / 点击服务**（用户要求："把相应的方法做成 CmdBridgeMod 能提供的服务，
    /// 在行为树编辑器中，要能够使用来获取坐标或点击对象"）。
    ///
    /// 对外就是两条命令（`ui.locate` / `ui.click`），编辑器的 `/api/game/ui/*` 直接转发它们，
    /// 行为树的 `Task.UiClick` 也走同一条路 —— 于是"编辑器里试一下"和"树里跑一下"用的是同一份实现，
    /// 不会出现"编辑器能点、回放点空"的错位。
    ///
    /// 三种点击方式（`mode`）：
    ///
    /// | mode | 怎么点 | 用在哪 | 代价 |
    /// |------|--------|--------|------|
    /// | `direct`（默认） | **单帧合成"按下→抬起"**：只往输入层的 `m_mouseDownOnce` 数组 + `m_mouseDownPoint/m_mouseDownButton` + 软光标位置写一次，引擎自己的 `UpdateInputFromMouse` 派生 `Tap`+`Click` | 编辑器试点击、回放、行为树 | 一帧；不依赖光标活过好几帧 |
    /// | `invoke` | **直接触发控件自己的按下事件**（`ListPanelWidget.ItemClicked`，必要时先摆好它处理器的前置状态） | 明确要"直接发事件"的场合 | **绕过输入层**，AI 默认不用 |
    /// | `input` | 多步软光标会话（移动 → 按下 → 抬起，一步一帧） | 对照 / 兼容旧行为 | 跨 5 帧，依赖会话不被打断 |
    ///
    /// 为什么 `direct` 是"单帧"而不是"同帧按下再抬起"：引擎的 `Click` 派生要求
    /// "这一帧没按着 + 上一帧留了按下点"（Source: Game/WidgetInput.cs:755-769）。
    /// 同帧把 down 数组按下又抬起，两件事在同一帧里看不到先后 —— 于是什么也派生不出来
    /// （早期"直注入点击没反应"就是这个原因）。把"按过一下"只写进 **downOnce** 数组、
    /// down 数组保持没按，`Tap` 与 `Click` 就会在这一帧同时成立，和真人快速点一下完全等价。
    /// </summary>
    internal sealed class UiService
    {
        // ==================================================================================
        // 规则 1（用户 2026-10-17 定）：点世界内 HUD 之前，本 Mod 必须自己把 HUD 强制显示出来。
        //
        // 实测事实：`GameWidget` 的 `Gui` 层里，`LeftControlsContainer` / `RightControlsContainer`
        // 由 `ComponentGui.UpdateSidePanelsAnimation()` 按 `m_sidePanelsFactor` **整条**平移到屏外
        // （Source: Survivalcraft/Game/ComponentGui.cs:300-316：非触屏且没有模态面板时 factor=1）。
        // 于是 `ui.locate MoreButton` 报 `clientPoint=(2139,55)`、`hittable=false ()`（客户区 2100）；
        // 真人之所以点得到，是因为他先按 `E` 打开仓库（模态面板）→ `flag=true` → factor→0 →
        // 侧栏滑回屏内（`ComponentGui.cs:303-315`）。
        //
        // 所以本 Mod 的点击路径必须自己走完同一条因果链，全程**虚拟输入**、后台完成：
        //   ① 用**玩家输入层**的按键脉冲按下 `E`（与 `act.hold` / `act.key` 同一条实现：
        //      `InputInjector.PulsePlayerInputKey` → `SetKeyHeld` → 直接写引擎的
        //      `Keyboard.m_keysDownArray` / `m_keysDownOnceArray`；`ComponentInput.cs:194` 的
        //      `input.IsKeyDownOnce(Key.E)` 读的就是它）→ 引擎自己打开仓库 → 侧栏滑入；
        //   ② 跨帧重新解析目标，能点到之后按原本的方式点下去（规则 1 只改"能不能点"，
        //      不改点击本身怎么合成）；
        //   ③ 仍然不可点 → **明确失败**，消息里写出目标、遮挡者、原因；绝不假装点到了、
        //      绝不"扩大窗口"、绝不抢真实鼠标/窗口、绝不要求人来点。
        //
        // 例外（必须写清楚，这不是兜底）：模态面板**已经**开着时绝不按 `E` —— `E` 是开关，
        // 会把已经展开的面板关掉；这种情况直接交给正常点击路径，由它自己明确报错。
        // ==================================================================================

        /// <summary>规则 1：强制展开 HUD 用的按键（`E` = 开关仓库，`ComponentInput.cs:194`）。</summary>
        internal const string HudRevealKeyName = "E";

        /// <summary>规则 1：等待侧栏滑入时的轮询间隔（一帧量级，与 `obs.waitfor` 同一套做法）。</summary>
        private const int HudRevealPollIntervalMs = 40;

        /// <summary>
        /// 规则 1：等待 HUD 布局完成的上限。侧栏滑入是 `ComponentGui.UpdateSidePanelsAnimation`
        /// 每帧约 0.2 的指数逼近（factor 1→0 约 6~10 帧），2500ms 在 15fps 的糟糕帧率下也够。
        /// </summary>
        private const int HudRevealTimeoutMs = 2500;

        // ==================================================================================
        // 规则 2（用户 2026-10-17 定）：`MoreButton` 是**二级开关**。
        //
        // 用户原话："你点开 HUD 强制显示的时候，如果一个界面是挂在 MoreButton 中的时候，
        // 你要再次点击 MoreButton，这是一个二级开关。"
        //
        // 引擎事实（不是推测）：
        //   · `Pak/Widgets/GameWidget.xml:65-77`：`RightControlsContainer` 里那个横排
        //     `StackPanelWidget` 下**并排**放着 `MoreContents`（一列按钮）与 `MoreButton`
        //     （`IsAutoCheckingEnabled="true"` = 点一下展开、再点一下收起）；
        //   · `Survivalcraft/Game/ComponentGui.cs:231,400`：每帧
        //     `m_moreContentsWidget.IsVisible = m_moreButtonWidget.IsChecked`，而二级收起时
        //     里面的按钮既没被布局也没被绘制（`StackPanelWidget.MeasureOverride/ArrangeOverride`
        //     跳过不可见子控件）→ 既点不到、尺寸也常常是零；
        //   · 外部 Mod 正是往这里挂按钮的（联机 Mod 的 `CR`：
        //     `Mod/ScMultiplayer/Func/Component/MultiplayerUiComponent.cs:85`）。
        //
        // 所以点击路径的第二段因果是：目标挂在 `MoreContents` 里且二级收着 → 先点 `MoreButton`
        // （**同一条虚拟输入路径**，不碰真实鼠标/窗口），跨帧等它展开 → 再让调用方点目标。
        // 已经展开时**绝不**点：它是开关，点了会把面板收起来。
        //
        // 与规则 1 一样，"做了什么"要如实交回调用方（见 `HudRevealReport`）：不拦截、不静默，
        // 两步都做完还不可点 → `hud_target_not_hittable` 明确失败并列出目标/遮挡者/步骤。
        // ==================================================================================

        /// <summary>
        /// 规则 1 / 规则 2 的**经过报告**（用户要求："要告诉我发生了什么"）。
        ///
        /// 除失败（抛 `hud_target_not_hittable` / pending）以外的每次调用都会带回一份，
        /// `UiService.Click` 把它写进点击回包；每次调用新建一份 —— 没有 static 运行时状态。
        /// </summary>
        internal sealed class HudRevealReport
        {
            /// <summary>是否用玩家输入层的 `E` 把世界内 HUD 侧栏强制显示出来了（按下了就是 true）。</summary>
            public bool HudRevealed;

            /// <summary>按 `E` 的次数（0 或 1 —— 模态面板已开时绝不按）。</summary>
            public int HudRevealKeyPresses;

            /// <summary>规则 1 这一步做过什么（人话，逐步）。</summary>
            public readonly List<string> HudRevealSteps = new List<string>();

            /// <summary>是否点了二级开关 `MoreButton`（点了就是 true）。</summary>
            public bool MoreToggled;

            /// <summary>点 `MoreButton` 的次数（0 或 1 —— 二级已展开时绝不点）。</summary>
            public int MoreToggleClicks;

            /// <summary>规则 2 这一步做过什么（点了哪个控件 / 落在哪个像素 / 开关前后的 `IsChecked`）。</summary>
            public readonly List<string> MoreToggleSteps = new List<string>();

            /// <summary>一句话总结（回包的 `revealNote`）。</summary>
            public string Note;
        }

        /// <summary>
        /// 规则 2 的一次判定 / 点击结果（游戏线程内产生，随 `HudRevealReport` 交回调用方）。
        /// </summary>
        private sealed class MoreToggleResult
        {
            /// <summary>目标在点之前已经不在 `MoreContents` 里了（状态在等待期间变了）→ 绝不点。</summary>
            public bool TargetLeftMoreContents;

            /// <summary>二级已经展开 → 绝不点（开关会把它收起来）。</summary>
            public bool Expanded;

            /// <summary>`MoreContents` 旁边找不到 `MoreButton`（HUD 结构变了）→ 无法展开。</summary>
            public bool ToggleMissing;

            /// <summary>`MoreButton` 自己现在能不能点。</summary>
            public bool ToggleHittable;

            /// <summary>是否真的点下去了（虚拟输入已写入）。</summary>
            public bool Clicked;

            /// <summary>点之前的 `MoreButton.IsChecked`（点完应为 true）。</summary>
            public bool WasChecked;

            public string TogglePath;
            public string ClickTarget;
            public string ToggleBlockedBy;
            public string ToggleReason;

            /// <summary>实际落点（`PickClickPoint` 挑的、会被消费的那个点）。</summary>
            public float ClickX;

            public float ClickY;
        }

        /// <summary>规则 1 的一次**只读**判定：目标在不在、属于哪一层、现在能不能点。</summary>
        private struct HudProbe
        {
            public bool Present;
            public bool InHud;
            public string HudContainer;
            public bool Hittable;
            /// <summary>目标中心落在根控件矩形之外 = 被 `m_sidePanelsFactor` 平移出屏（规则 1 的唯一因果）。</summary>
            public bool OffScreen;
            public string BlockedBy;
            public string Reason;
            public bool ModalPanelOpen;
            public string ModalPanel;
            public float ClientX;
            public float ClientY;
            /// <summary>规则 2：目标是不是**挂在 `MoreContents` 里**（`MoreButton` 二级开关后面）。</summary>
            public bool InMoreContents;
            /// <summary>规则 2：`MoreContents` 当前是否已展开（= `MoreButton.IsChecked`，`ComponentGui.cs:400`）。</summary>
            public bool MoreExpanded;
            /// <summary>规则 2：二级开关 `MoreButton` 找不找得到（找不到 = HUD 结构变了，如实报错）。</summary>
            public bool MoreToggleFound;
            /// <summary>规则 2：`MoreButton` 自己现在能不能点（侧栏没滑进来时它也点不到 —— 这是按 `E` 的第二个理由）。</summary>
            public bool MoreToggleHittable;
            /// <summary>规则 2：`MoreButton` 的中心是否落在根控件矩形之外。</summary>
            public bool MoreToggleOffScreen;
            public string MoreToggleBlockedBy;
            public string MoreToggleReason;
            public float MoreToggleX;
            public float MoreToggleY;
        }

        private readonly InputInjector m_injector;

        /// <summary>规则 1 的串行门：`E` 是开关，并发的 HUD 点击会把面板开了又关（见类头「规则 1」块）。</summary>
        private readonly object m_hudRevealGate = new object();

        public UiService(InputInjector injector)
        {
            m_injector = injector ?? throw new ArgumentNullException(nameof(injector));
        }

        // ---------------------------------------------------------------- 规则 1：强制展开 HUD

        /// <summary>
        /// 规则 1 / 规则 2 的入口：目标在**世界内 HUD** 且当前不可点时，本 Mod 自己走完
        /// "用玩家输入层的 `E` 把 HUD 侧栏强制显示 → 目标挂在 `MoreContents` 里就再点一下
        /// `MoreButton` 展开二级"这两段因果，然后才让调用方去点（见文件头「规则 1」「规则 2」两块）。
        ///
        /// 返回**这次替调用方做了什么**（`hudRevealed` / `hudRevealSteps` / `moreToggled` /
        /// `moreToggleSteps` / `note`）—— 用户要求"要告诉我发生了什么"，不拦截、不静默。
        ///
        /// 只允许从**命令线程**调用：跨帧等待靠"命令线程 sleep、游戏线程继续出帧"实现
        /// （与 `CommandRouter.WaitForConditions` 同一套做法）。在游戏线程上它**明确报错**
        /// （`hud_reveal_pending` / `more_toggle_pending`）而不是卡住整帧或假装点成功。
        /// </summary>
        internal HudRevealReport EnsureHudTargetRevealed(string target)
        {
            var report = new HudRevealReport();
            if (string.IsNullOrWhiteSpace(target))
                return report;   // 空目标不是 HUD 的问题：交回调用方报它自己的 invalid_argument

            // 并发命令（`ControlServer` 每个连接一个线程）可能同时点两个 HUD 目标，而 `E` 与
            // `MoreButton` 都是**开关**：两次并发按下会"开一次又关一次"。整段"判定 → 按 E →
            // 等布局 → 点 MoreButton → 等展开 → 复核"必须串行。
            lock (m_hudRevealGate)
            {
                EnsureHudTargetRevealedCore(target, report);
            }
            return report;
        }

        /// <summary>规则 1 + 规则 2 的串行实现（并发保护见 <see cref="EnsureHudTargetRevealed"/>）。</summary>
        private void EnsureHudTargetRevealedCore(string target, HudRevealReport report)
        {
            HudProbe probe = ProbeHudTarget(target);
            if (!probe.Present || !probe.InHud)
            {
                report.Note = "规则 1 / 规则 2 没有介入：目标不在世界内 HUD 里（present="
                    + probe.Present + "），什么都不改。";
                return;   // 不在 HUD：什么都不改
            }
            if (probe.Hittable)
            {
                report.Note = BuildRevealNote(report);
                return;   // 本来就能点：没有按 E、也没有点 MoreButton
            }

            bool targetOffScreen = probe.OffScreen;
            // 二级开关自己出屏，也是"侧栏没滑进来"这同一条因果（`ComponentGui.cs:314-315` 平移的正是
            // `RightControlsContainer`）。必须把它算进来：二级收起时目标常常**尺寸为零**
            // （`StackPanelWidget` 跳过不可见子控件），它的中心说明不了侧栏在不在屏内，只能看开关。
            bool toggleOffScreen = probe.InMoreContents && probe.MoreToggleFound
                && !probe.MoreToggleHittable && probe.MoreToggleOffScreen;
            // 规则 2 能帮上忙的唯一情形：目标挂在 `MoreContents` 里，而二级**收着**。
            bool secondLevelHelpful = probe.InMoreContents && !probe.MoreExpanded;

            if (!targetOffScreen && !toggleOffScreen && !secondLevelHelpful)
            {
                // 只有**被 `m_sidePanelsFactor` 平移出屏**的目标才按 E —— 这是引擎代码能证明的唯一因果：
                // `ComponentGui.cs:314-315` 只平移 `LeftControlsContainer` / `RightControlsContainer`，
                // 别的 HUD 控件（触屏摇杆等）不在这条因果链里：按 E 不会让它们变可点，只会凭空开关一次仓库。
                // 所以"在 HUD 但不可点、又没出屏、也不在收起的二级开关后面"的目标原样交回点击路径，
                // 由它报出真实原因（遮挡者 / 零尺寸 / 未布局）—— 既不是静默兜底，也不会凭空改游戏状态。
                Log.Information("[CmdBridge][hud] target '" + target + "' is in the in-world HUD ("
                    + probe.HudContainer + ") and not hittable, but it is NOT off-screen (reason="
                    + (probe.Reason ?? "<unknown>") + ") -> the side-panel slide-in cannot help it; "
                    + "NOT pressing " + HudRevealKeyName
                    + ", the normal click path will report the real error");
                report.Note = "规则 1 / 规则 2 没有介入：目标没有出屏，也不在收起的 "
                    + UiInspector.MoreContentsName + " 二级开关后面（reason="
                    + (probe.Reason ?? "<unknown>") + "）；交回正常点击路径报真实原因。";
                return;
            }

            if (!targetOffScreen && !toggleOffScreen)
            {
                // 目标在屏内（二级收起时尺寸为零、中心恰好在屏内），但二级收着 → 不用按 E，直接走规则 2。
                Log.Information("[CmdBridge][hud] target '" + target + "' is in the in-world HUD ("
                    + probe.HudContainer + ") and not hittable, but it is NOT off-screen (reason="
                    + (probe.Reason ?? "<unknown>") + "); it hangs inside the collapsed "
                    + UiInspector.MoreContentsName + " second level -> NOT pressing " + HudRevealKeyName
                    + ", the " + UiInspector.MoreToggleName + " step below opens that level");
            }
            else if (probe.ModalPanelOpen)
            {
                // 模态面板已经开着 = 侧栏本来就该在屏内；按 E 只会把它关掉（E 是开关）。
                // 二级开关仍可能是收起的，那种情况不按 E、直接点 MoreButton；别的不可点情形原样交回
                // 正常点击路径，由它自己明确报错 —— 不静默、也不假装成功。
                Log.Information("[CmdBridge][hud] step 0/4 target '" + target
                    + "' is in the in-world HUD (" + probe.HudContainer + ") and not hittable, but modal panel '"
                    + probe.ModalPanel + "' is already open -> NOT pressing " + HudRevealKeyName
                    + " (it would close that panel)"
                    + (secondLevelHelpful
                        ? "; the " + UiInspector.MoreToggleName + " step below opens the collapsed "
                          + UiInspector.MoreContentsName + " second level"
                        : "; the normal click path will report the real error"));
                if (!secondLevelHelpful)
                {
                    report.Note = "规则 1 没有介入：模态面板 '" + probe.ModalPanel + "' 已经开着，按 "
                        + HudRevealKeyName + " 只会把它关掉；交回正常点击路径报真实原因。";
                    return;
                }
            }
            else
            {
                Log.Information("[CmdBridge][hud] step 1/4 target '" + target + "' is in the in-world HUD ("
                    + probe.HudContainer + ") and not hittable: blocker=" + (probe.BlockedBy ?? "<none>")
                    + ", reason=" + (probe.Reason ?? "<unknown>") + ", clientPoint=("
                    + probe.ClientX.ToString("0.#") + "," + probe.ClientY.ToString("0.#")
                    + ")" + (targetOffScreen
                        ? ""
                        : "; the target itself is not off-screen but the " + UiInspector.MoreToggleName
                          + " second-level toggle is (toggleClientPoint=("
                          + probe.MoreToggleX.ToString("0.#") + ","
                          + probe.MoreToggleY.ToString("0.#") + "))")
                    + " -> pressing " + HudRevealKeyName
                    + " through the PlayerInput path to open the inventory and force the HUD to lay out");

                // ① 玩家输入层的按键脉冲（与 act.hold / act.key 同一条实现），虚拟输入、不碰真实键盘。
                m_injector.PulsePlayerInputKey(Key.E, InputInjector.DefaultPulseHoldMs);
                report.HudRevealKeyPresses++;
                report.HudRevealSteps.Add("press " + HudRevealKeyName
                    + " through the PlayerInput path (virtual input; the inventory panel opens and the HUD side panels slide back in-screen)");

                if (GameThreadInvoker.IsGameThread())
                {
                    // 游戏线程上不能跨帧等待：等待就是卡住整帧，而 HUD 恰恰需要帧才会布局完。
                    // 这里**明确拒绝**这一次点击（点也只会点空），不做任何兜底。
                    // E 已经按下去了，所以调用方在 HUD 布局完成后重发一次即可命中。
                    throw new BridgeCommandException("hud_reveal_pending",
                        "目标 '" + target + "' 在世界内 HUD（" + probe.HudContainer + "）里且当前不可点；"
                        + "已用玩家输入层的 " + HudRevealKeyName + " 强制展开 HUD，但本调用在游戏线程上，"
                        + "无法等待跨帧布局 -> 这一次不点（点了也只会点空）。"
                        + "请在 HUD 布局完成后重发这次点击。");
                }

                // ② 跨帧重解析：命令线程 sleep，游戏线程继续出帧（侧栏在这期间滑入）。
                int attempts;
                long elapsedMs;
                probe = WaitForHudReveal(target, probe, out attempts, out elapsedMs);
                if (probe.Hittable)
                {
                    Log.Information("[CmdBridge][hud] step 2/4 HUD revealed: '" + target
                        + "' is now hittable after " + attempts + " poll(s) / " + elapsedMs
                        + "ms (clientPoint=(" + probe.ClientX.ToString("0.#") + ","
                        + probe.ClientY.ToString("0.#") + ")) -> performing the click");
                }
                else
                {
                    Log.Warning("[CmdBridge][hud] step 2/4 target '" + target
                        + "' is still not hittable after forcing the HUD open (" + attempts
                        + " poll(s), " + elapsedMs + "ms)"
                        + (probe.InMoreContents && !probe.MoreExpanded
                            ? "; it hangs inside the collapsed " + UiInspector.MoreContentsName
                              + " second level -> the " + UiInspector.MoreToggleName + " step follows"
                            : ""));
                }
            }

            if (probe.Hittable)
            {
                report.HudRevealed = report.HudRevealKeyPresses > 0;
                report.Note = BuildRevealNote(report);
                return;
            }

            // ---------------------------------------------------------------- 规则 2：二级开关
            if (probe.InMoreContents)
            {
                if (probe.MoreExpanded)
                {
                    // 二级已经展开还不可点 → 点 MoreButton 只会把它收起来，**绝不点**。
                    // 交回正常点击路径报真实原因（与"模态面板已开时不按 E"同一条纪律）。
                    Log.Information("[CmdBridge][hud] step 3/4 target '" + target + "' hangs inside "
                        + UiInspector.MoreContentsName + " but that second level is ALREADY expanded (IsVisible=true)"
                        + " -> NOT clicking " + UiInspector.MoreToggleName
                        + " (it is a toggle: clicking would collapse the panel); blocker="
                        + (probe.BlockedBy ?? "<none>") + ", reason=" + (probe.Reason ?? "<unknown>")
                        + "; the normal click path will report the real error");
                    report.Note = "规则 2 没有介入：目标所在的 " + UiInspector.MoreContentsName
                        + " 二级开关已经展开，点 " + UiInspector.MoreToggleName
                        + " 只会把面板收起来（blocker=" + (probe.BlockedBy ?? "<none>")
                        + ", reason=" + (probe.Reason ?? "<unknown>") + "）；交回正常点击路径报真实原因。";
                    return;
                }

                Log.Information("[CmdBridge][hud] step 3/4 target '" + target + "' hangs inside "
                    + UiInspector.MoreContentsName + " (the " + UiInspector.MoreToggleName
                    + " second level, currently collapsed: " + UiInspector.MoreContentsName
                    + ".IsVisible=false) and is not hittable: blocker=" + (probe.BlockedBy ?? "<none>")
                    + ", reason=" + (probe.Reason ?? "<unknown>") + ", toggleClientPoint=("
                    + probe.MoreToggleX.ToString("0.#") + "," + probe.MoreToggleY.ToString("0.#")
                    + ") -> clicking " + UiInspector.MoreToggleName
                    + " once through the same virtual-input path to open that second level");

                var toggle = (MoreToggleResult)m_injector.OnGameThreadForUi(
                    delegate { return (object)ToggleMoreContentsCore(target); });

                if (toggle.TargetLeftMoreContents || toggle.Expanded)
                {
                    string why = toggle.TargetLeftMoreContents
                        ? "目标在点之前已经不在 " + UiInspector.MoreContentsName + " 里了（等待期间 UI 变了）"
                        : UiInspector.MoreContentsName + " 在点之前已经展开了";
                    Log.Information("[CmdBridge][hud] step 3/4 " + why + " -> NOT clicking "
                        + UiInspector.MoreToggleName
                        + " (it is a toggle: clicking would collapse the panel); re-probing once");
                    probe = ProbeHudTarget(target);
                    if (!probe.Hittable)
                    {
                        report.Note = "规则 2 没有介入：" + why + "，点 " + UiInspector.MoreToggleName
                            + " 只会把二级收起来；交回正常点击路径报真实原因（blocker="
                            + (probe.BlockedBy ?? "<none>") + ", reason="
                            + (probe.Reason ?? "<unknown>") + "）。";
                        return;
                    }
                }
                else if (toggle.ToggleMissing)
                {
                    Log.Warning("[CmdBridge][hud] step 4/4 no " + UiInspector.MoreToggleName
                        + " found next to " + UiInspector.MoreContentsName
                        + " -> cannot open the second level");
                    throw new BridgeCommandException("hud_target_not_hittable",
                        HudFailureMessage(target, probe, report)
                        + "（" + UiInspector.MoreContentsName + " 的同级里找不到 "
                        + UiInspector.MoreToggleName + "，二级开关打不开。）");
                }
                else if (!toggle.ToggleHittable)
                {
                    Log.Warning("[CmdBridge][hud] step 4/4 " + UiInspector.MoreToggleName
                        + " itself is not hittable (blocker=" + (toggle.ToggleBlockedBy ?? "<none>")
                        + ", reason=" + (toggle.ToggleReason ?? "<unknown>")
                        + ") -> cannot open the second level");
                    throw new BridgeCommandException("hud_target_not_hittable",
                        HudFailureMessage(target, probe, report)
                        + "（二级开关 " + UiInspector.MoreToggleName + " 自己不可点：blocker="
                        + (toggle.ToggleBlockedBy ?? "<none>") + ", reason="
                        + (toggle.ToggleReason ?? "<unknown>") + "。）");
                }
                else if (toggle.Clicked)
                {
                    report.MoreToggled = true;
                    report.MoreToggleClicks++;
                    report.MoreToggleSteps.Add("click " + UiInspector.MoreToggleName + " at ("
                        + toggle.ClickX.ToString("0.#") + "," + toggle.ClickY.ToString("0.#") + ")"
                        + (string.IsNullOrEmpty(toggle.ClickTarget) ? "" : " hit=" + toggle.ClickTarget)
                        + (string.IsNullOrEmpty(toggle.TogglePath) ? "" : " [" + toggle.TogglePath + "]")
                        + " through the same virtual-input path on its own input surface (IsChecked was "
                        + toggle.WasChecked.ToString().ToLowerInvariant() + " -> expected true = open "
                        + UiInspector.MoreContentsName + ")");

                    if (GameThreadInvoker.IsGameThread())
                    {
                        // 二级展开要下一帧才生效（`ComponentGui.UpdateWidgets` 每帧把
                        // `MoreContents.IsVisible` 绑到 `MoreButton.IsChecked` 上），游戏线程上无法跨帧等待。
                        // MoreButton 已经按下去了，所以调用方在展开完成后重发一次即可命中。
                        throw new BridgeCommandException("more_toggle_pending",
                            "目标 '" + target + "' 挂在 " + UiInspector.MoreContentsName
                            + " 里，已用虚拟输入点下 " + UiInspector.MoreToggleName + "（二级开关）展开它，"
                            + "但本调用在游戏线程上，无法等待跨帧展开 -> 这一次不点（点了也只会点空）。"
                            + "请在 " + UiInspector.MoreContentsName + " 展开完成后重发这次点击。");
                    }

                    long moreStartTicks = Environment.TickCount64;
                    int moreAttempts = 0;
                    while (Environment.TickCount64 - moreStartTicks < HudRevealTimeoutMs)
                    {
                        Thread.Sleep(HudRevealPollIntervalMs);
                        moreAttempts++;
                        probe = ProbeHudTarget(target);
                        if (probe.Hittable)
                            break;
                    }
                    if (probe.Hittable)
                    {
                        Log.Information("[CmdBridge][hud] step 4/4 " + UiInspector.MoreContentsName
                            + " second level opened: '" + target + "' is now hittable after "
                            + moreAttempts + " poll(s) / " + (Environment.TickCount64 - moreStartTicks)
                            + "ms (clientPoint=(" + probe.ClientX.ToString("0.#") + ","
                            + probe.ClientY.ToString("0.#") + ")) -> performing the click");
                    }
                }
            }

            if (probe.Hittable)
            {
                report.HudRevealed = report.HudRevealKeyPresses > 0;
                report.Note = BuildRevealNote(report);
                return;
            }

            // ③ 两步都做完还是不可点：明确失败，写出目标 / 所在层 / 遮挡者 / 原因 / 已经做过的步骤
            //    （规则 1 第 3 条、规则 2 第 4 条）。绝不假装点到了、绝不扩大窗口、绝不抢真实鼠标/窗口。
            Log.Warning("[CmdBridge][hud] step 4/4 target '" + target
                + "' is STILL not hittable after " + DescribeRevealSteps(report));
            throw new BridgeCommandException("hud_target_not_hittable",
                HudFailureMessage(target, probe, report));
        }

        /// <summary>
        /// 规则 1 的步骤 ②：按过 `E` 之后跨帧等侧栏滑进来（命令线程 sleep、游戏线程继续出帧；
        /// 侧栏是 `ComponentGui.UpdateSidePanelsAnimation` 每帧约 0.2 的指数逼近，factor 1→0 约 6~10 帧）。
        ///
        /// 提前结束的第二种情形：目标挂在 `MoreContents` 里、二级还收着，而 `MoreButton` 已经可点 ——
        /// 再等也不会可点（必须先开二级），把剩下的时间交给规则 2。
        /// </summary>
        private HudProbe WaitForHudReveal(string target, HudProbe probe, out int attempts,
            out long elapsedMs)
        {
            long startTicks = Environment.TickCount64;
            attempts = 0;
            while (Environment.TickCount64 - startTicks < HudRevealTimeoutMs)
            {
                Thread.Sleep(HudRevealPollIntervalMs);
                attempts++;
                probe = ProbeHudTarget(target);
                if (probe.Hittable)
                    break;
                if (probe.InMoreContents && !probe.MoreExpanded && probe.MoreToggleFound
                    && probe.MoreToggleHittable)
                {
                    Log.Information("[CmdBridge][hud] step 2/4 '" + target
                        + "': the HUD side panel is in (the " + UiInspector.MoreToggleName
                        + " toggle is hittable now) but the target hangs inside the collapsed "
                        + UiInspector.MoreContentsName + " second level -> stopping the "
                        + HudRevealKeyName + " wait after " + attempts + " poll(s) and going to the "
                        + UiInspector.MoreToggleName + " step");
                    break;
                }
            }
            elapsedMs = Environment.TickCount64 - startTicks;
            return probe;
        }

        /// <summary>
        /// 规则 2 的游戏线程实现：判定二级开关要不要点、能不能点，需要时用**同一条虚拟输入路径**
        /// （`InputInjector.ApplyDirectClick` → 写目标控件自己的输入面）点一下 `MoreButton`。
        ///
        /// 两件事**不做**（是纪律，不是遗漏）：
        ///   · 二级已经展开 → 绝不点（它是开关，点了会把面板收起来）；
        ///   · 目标已经不在 `MoreContents` 里 → 绝不点（状态在等待期间变了，按新状态重判）。
        /// </summary>
        private MoreToggleResult ToggleMoreContentsCore(string target)
        {
            var result = new MoreToggleResult();
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                throw new BridgeCommandException("not_ready", "The UI is not ready yet.");

            Widget widget = ResolveElementCore(target);
            Widget moreContents;
            if (widget == null || !UiInspector.TryGetMoreContentsAncestor(widget, out moreContents))
            {
                result.TargetLeftMoreContents = true;
                return result;
            }
            if (UiInspector.IsMoreContentsExpanded(moreContents))
            {
                result.Expanded = true;
                return result;
            }

            Widget moreButton;
            if (!UiInspector.TryGetMoreContentsToggle(moreContents, out moreButton))
            {
                result.ToggleMissing = true;
                return result;
            }
            result.TogglePath = UiInspector.BuildPath(moreButton);

            Vector2 toggleCenter = UiInspector.CenterOf(moreButton);
            Widget toggleHit = root.HitTestGlobal(toggleCenter);
            result.ToggleHittable = UiInspector.IsHittable(moreButton);
            result.ToggleBlockedBy = result.ToggleHittable ? null : UiInspector.ShortName(toggleHit);
            result.ToggleReason = result.ToggleHittable ? null
                : UiInspector.ExplainUnhittable(moreButton, toggleHit, moreButton.GlobalBounds,
                    toggleCenter, root);
            if (!result.ToggleHittable)
                return result;

            // 与 `ClickDirectCore` 同一套前置条件：引擎在"窗口不活跃"时整段输入都不算
            // （`WidgetInput.Update()` 里 `if (Window.IsActive)`），这时合成点击会被**无声无息地丢掉**。
            // 与其让用户以为"点了 MoreButton 没反应"，不如如实拒绝并说清怎么让它能点。
            if (!Window.IsActive)
            {
                throw new BridgeCommandException("window_not_active",
                    "游戏窗口现在不是活跃窗口（引擎认为它不在前台），输入会被整段忽略，所以没有点 "
                    + UiInspector.MoreToggleName + "（二级开关）：让游戏窗口拿到焦点，"
                    + "或用 focus.detach / focus.auto 让本 Mod 接管输入，再点一次。");
            }

            // 落点与 `ClickDirectCore` 同一套：复合按钮内部包着一个 `ClickableWidget`，
            // 直接点中心常盖着图标/文字子控件 → 在矩形里扫一个真会被消费的落点。
            Vector2 clickPoint = PickClickPoint(root, moreButton, toggleCenter);
            Widget hitAtClick = root.HitTestGlobal(clickPoint) ?? moreButton;
            var button = moreButton as ButtonWidget;
            result.WasChecked = button != null && button.IsChecked;
            m_injector.ApplyDirectClick(clickPoint, hitAtClick);
            result.Clicked = true;
            result.ClickTarget = UiInspector.ShortName(hitAtClick);
            result.ClickX = clickPoint.X;
            result.ClickY = clickPoint.Y;
            return result;
        }

        /// <summary>成功后的一句话总结（回包的 `revealNote`）。</summary>
        private static string BuildRevealNote(HudRevealReport report)
        {
            if (report.HudRevealKeyPresses == 0 && report.MoreToggleClicks == 0)
            {
                return "规则 1 / 规则 2 没有介入：目标本来就可点（没有按 " + HudRevealKeyName
                    + "、也没有点 " + UiInspector.MoreToggleName + "）。";
            }
            return "规则 1 / 规则 2 已生效：按 " + HudRevealKeyName + " ×" + report.HudRevealKeyPresses
                + "、点 " + UiInspector.MoreToggleName + " ×" + report.MoreToggleClicks
                + "，目标已可点。步骤：" + DescribeRevealSteps(report);
        }

        /// <summary>把报告里的步骤串成一行（回包的 `revealNote` 与失败消息共用）。</summary>
        private static string DescribeRevealSteps(HudRevealReport report)
        {
            if (report == null)
                return "没有做任何步骤";
            var parts = new List<string>();
            if (report.HudRevealSteps.Count > 0)
                parts.Add("hud: " + string.Join(" -> ", report.HudRevealSteps.ToArray()));
            if (report.MoreToggleSteps.Count > 0)
                parts.Add("more: " + string.Join(" -> ", report.MoreToggleSteps.ToArray()));
            return parts.Count > 0 ? string.Join(" | ", parts.ToArray()) : "没有做任何步骤";
        }

        /// <summary>
        /// 规则 1 / 规则 2 失败时的统一消息：目标 / 所在层 / 遮挡者 / 原因 / 已经做过什么。
        /// 用户要求"明确失败并写清原因"，这里就是那份原因。
        /// </summary>
        private static string HudFailureMessage(string target, HudProbe probe, HudRevealReport report)
        {
            return "世界内 HUD 目标 '" + target + "' 在强制展开 HUD（规则 1）与展开 "
                + UiInspector.MoreContentsName + " 二级开关（规则 2）之后仍不可点：present=" + probe.Present
                + "; layer=" + (probe.HudContainer ?? "<none>")
                + "; offScreen=" + probe.OffScreen
                + "; inMoreContents=" + probe.InMoreContents
                + "; moreContentsExpanded=" + probe.MoreExpanded
                + "; moreButtonFound=" + probe.MoreToggleFound
                + "; moreButtonHittable=" + probe.MoreToggleHittable
                + "; blocker=" + (probe.BlockedBy ?? "<none>")
                + "; reason=" + (probe.Reason ?? "<unknown>")
                + "; clientPoint=(" + probe.ClientX.ToString("0.#") + "," + probe.ClientY.ToString("0.#")
                + "); modalPanel=" + (probe.ModalPanel ?? "<none>")
                + "。已经做过的步骤：" + DescribeRevealSteps(report)
                + "。不做任何扩大窗口 / 抢占真实鼠标与窗口的兜底。";
        }

        /// <summary>在游戏线程上做一次只读判定（命令线程调用，等游戏线程那一帧）。</summary>
        private HudProbe ProbeHudTarget(string target)
        {
            return (HudProbe)m_injector.OnGameThreadForUi(
                delegate { return (object)ProbeHudTargetCore(target); });
        }

        /// <summary>
        /// 游戏线程上的只读判定：语义目标 → 控件 → HUD 归属 + 可点性（与 `obs.ui` 上报的
        /// `hittable` 用的是**同一份**判定，保证"探针说不可点"和"报给用户看的 hittable:false"
        /// 永远一致）。
        ///
        /// 解析失败（`element_missing` / `ambiguous_selector`）**不在这里报错**：那不是 HUD 的问题，
        /// 原样交回调用方原本的解析路径去报它自己的错误（保持错误码与消息不变）。
        /// </summary>
        private static HudProbe ProbeHudTargetCore(string target)
        {
            var probe = new HudProbe();
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                throw new BridgeCommandException("not_ready", "The UI is not ready yet.");

            UiTarget.Parsed parsed;
            if (!UiTarget.TryParse(target, out parsed))
                return probe;

            Widget widget;
            if (parsed.Kind == UiTarget.TargetKind.Point)
            {
                widget = root.HitTestGlobal(new Vector2(parsed.X, parsed.Y));
            }
            else
            {
                try
                {
                    widget = UiInspector.Resolve(root, parsed.Selector, false, default(Vector2));
                }
                catch (BridgeCommandException)
                {
                    return probe;
                }
            }
            if (widget == null)
                return probe;

            probe.Present = true;
            string container;
            probe.InHud = UiInspector.TryGetInWorldHudContainer(widget, out container);
            probe.HudContainer = container;

            Dictionary<string, object> described = UiInspector.DescribeElement(widget, 0);
            probe.Hittable = described["hittable"] is bool hittable && hittable;
            probe.BlockedBy = described["blockedBy"] as string;
            probe.Reason = described["clickReason"] as string;
            Vector2 center = UiInspector.CenterOf(widget);
            probe.ClientX = center.X;
            probe.ClientY = center.Y;
            // 出屏 = 中心落在根控件矩形之外。这里用的就是 `UiInspector.ExplainUnhittable` 的同一判据
            // （`root.GlobalBounds.Contains(center)`），所以"探针说是出屏"与"报给用户的
            // `clickReason` 写着 off-screen"永远一致。实测：`BackButton` x=-55、`MoreButton` x=2139
            // （客户区 2100），两者都判为出屏。
            probe.OffScreen = !root.GlobalBounds.Contains(center);

            // 规则 2：目标是不是挂在 `MoreContents`（`MoreButton` 二级开关）里，以及那个开关自己
            // 现在能不能点。跟可点性用的是同一份判定（`DescribeElement` / `IsHittable` / `GlobalBounds`），
            // 所以"探针说二级收着/开关点不到"与真正点击时看到的原因永远一致。
            Widget moreContents;
            probe.InMoreContents = UiInspector.TryGetMoreContentsAncestor(widget, out moreContents);
            if (probe.InMoreContents)
            {
                probe.MoreExpanded = UiInspector.IsMoreContentsExpanded(moreContents);
                Widget moreButton;
                probe.MoreToggleFound = UiInspector.TryGetMoreContentsToggle(moreContents, out moreButton);
                if (probe.MoreToggleFound)
                {
                    Vector2 toggleCenter = UiInspector.CenterOf(moreButton);
                    probe.MoreToggleX = toggleCenter.X;
                    probe.MoreToggleY = toggleCenter.Y;
                    probe.MoreToggleHittable = UiInspector.IsHittable(moreButton);
                    Widget toggleHit = root.HitTestGlobal(toggleCenter);
                    probe.MoreToggleBlockedBy = probe.MoreToggleHittable
                        ? null
                        : UiInspector.ShortName(toggleHit);
                    probe.MoreToggleReason = probe.MoreToggleHittable ? null
                        : UiInspector.ExplainUnhittable(moreButton, toggleHit, moreButton.GlobalBounds,
                            toggleCenter, root);
                    probe.MoreToggleOffScreen = !root.GlobalBounds.Contains(toggleCenter);
                }
            }

            ComponentPlayer player = FindPlayerOrNull();
            ComponentGui gui = player == null ? null : player.ComponentGui;
            if (gui != null && gui.ModalPanelWidget != null)
            {
                probe.ModalPanelOpen = true;
                probe.ModalPanel = gui.ModalPanelWidget.GetType().Name;
            }
            return probe;
        }

        /// <summary>当前玩家（没有就 null）。只读，供"模态面板是否已开"这类判定用。</summary>
        private static ComponentPlayer FindPlayerOrNull()
        {
            try
            {
                GameEntitySystem.Project project = GameManager.Project;
                if (project == null)
                    return null;
                SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(false);
                return players != null && players.ComponentPlayers.Count > 0
                    ? players.ComponentPlayers[0]
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- 定位

        /// <summary>
        /// 解析语义目标 → **当前真实的**坐标与可点性。
        /// 返回值就是命令的 `result`，字段命名与 `obs.ui` 对齐，编辑器前端可以直接复用。
        ///
        /// `mark` 打开时顺便在游戏里那个像素上亮一个红点（默认 2 秒 / 5 像素）——
        /// 编辑器点「定位」时用，人眼一眼就能看出"它认为目标在哪"。
        /// </summary>
        public Dictionary<string, object> Locate(string target, bool mark, float markSeconds,
            float markPixels)
        {
            return (Dictionary<string, object>)m_injector.OnGameThreadForUi(delegate
            {
                Dictionary<string, object> located = LocateCore(target);
                if (mark)
                    MarkLocation(located, markSeconds, markPixels);
                return located;
            });
        }

        /// <summary>在定位结果那个像素上亮一下标记（`mark` 开关用）。</summary>
        internal void MarkLocation(Dictionary<string, object> located, float markSeconds,
            float markPixels)
        {
            if (located == null)
                return;
            Vector2 point;
            if (!TryPoint(located, out point))
                return;

            // 屏幕外的点不画：画了也看不见，还会让人以为"标记功能坏了"。
            ContainerWidget root = ScreensManager.RootWidget;
            if (root != null && !root.GlobalBounds.Contains(point))
            {
                located["marked"] = false;
                located["markSkipped"] = "落点在屏幕外（"
                    + point.X.ToString("0.#") + "," + point.Y.ToString("0.#")
                    + "），没有画标记 —— 先看这个控件是不是被折叠/隐藏了";
                return;
            }

            bool shown = UiMarker.Show(m_injector, point,
                markSeconds > 0f ? markSeconds : UiMarker.DefaultSeconds,
                markPixels > 0f ? markPixels : UiMarker.DefaultDiameterPixels);
            located["marked"] = shown;
            if (shown)
            {
                located["markSeconds"] = markSeconds > 0f ? markSeconds : UiMarker.DefaultSeconds;
                located["markPixels"] = markPixels > 0f ? markPixels : UiMarker.DefaultDiameterPixels;
            }
        }

        private static bool TryPoint(Dictionary<string, object> located, out Vector2 point)
        {
            point = default(Vector2);
            try
            {
                point = PointOf(located);
                return true;
            }
            catch (BridgeCommandException)
            {
                return false;
            }
        }

        /// <summary>游戏线程内的定位实现（`Click` 与命令层共用）。</summary>
        internal Dictionary<string, object> LocateCore(string target)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["target"] = target;

            UiTarget.Parsed parsed;
            if (!UiTarget.TryParse(target, out parsed))
            {
                throw new BridgeCommandException("invalid_argument",
                    "Cannot parse the UI target '" + (target ?? "<null>") + "'.");
            }
            result["kind"] = parsed.Kind.ToString();

            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                throw new BridgeCommandException("not_ready", "The UI is not ready yet.");

            Vector2 point;
            Widget widget;

            if (parsed.Kind == UiTarget.TargetKind.Point)
            {
                // 坐标兜底：点在哪就点哪，能命中什么就报什么（不假装它是某个控件）。
                point = new Vector2(parsed.X, parsed.Y);
                widget = root.HitTestGlobal(point);
                result["pointSource"] = "given";
                var described = widget != null
                    ? UiInspector.DescribeElement(widget, 0)
                    : new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> pair in described)
                    result[pair.Key] = pair.Value;
                result["hittable"] = widget != null;
                result["clickable"] = widget != null && UiInspector.IsHittable(widget);
                DescribePoint(result, point);
                result["warning"] = "坐标目标会随窗口尺寸/UI 缩放失配，能用控件名或 list: 就别用坐标。";
                return result;
            }

            if (parsed.Kind == UiTarget.TargetKind.ListRow)
            {
                Widget list = UiInspector.Resolve(root, parsed.Selector, false, default(Vector2));
                if (list == null)
                {
                    throw new BridgeCommandException("element_missing",
                        "No list matches '" + parsed.Selector + "'.");
                }

                Vector2 rowPoint;
                int index;
                string text;
                if (!UiInspector.TryResolveListRow(list, parsed.RowIndex, parsed.RowText,
                    out rowPoint, out index, out text))
                {
                    var listInfo = UiInspector.DescribeElement(list, 0);
                    string hint = ListHint(listInfo);
                    throw new BridgeCommandException("element_missing",
                        "No such row in '" + parsed.Selector + "' (row=" + parsed.RowIndex
                        + " text=" + (parsed.RowText ?? "<none>") + ")." + hint);
                }

                point = rowPoint;
                widget = root.HitTestGlobal(point);
                result["list"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["selector"] = parsed.Selector,
                    ["index"] = index,
                    ["text"] = text,
                    ["count"] = ListCount(list)
                };
                var describedList = UiInspector.DescribeElement(list, 0);
                result["name"] = describedList["name"];
                result["path"] = describedList["path"];
                result["type"] = describedList["type"];
                result["text"] = text;
                result["pointSource"] = "listRow";
                result["hittable"] = widget != null;
                result["clickable"] = widget != null && UiInspector.IsHittable(widget);
                result["hitTargetType"] = widget != null ? widget.GetType().Name : null;
                DescribePoint(result, point);
                return result;
            }

            widget = UiInspector.Resolve(root, parsed.Selector, false, default(Vector2));
            if (widget == null)
            {
                throw new BridgeCommandException("element_missing",
                    "No element matches '" + parsed.Selector + "'.");
            }

            point = UiInspector.CenterOf(widget);
            var element = UiInspector.DescribeElement(widget, 0);
            foreach (KeyValuePair<string, object> pair in element)
                result[pair.Key] = pair.Value;
            result["pointSource"] = "element";
            DescribePoint(result, point);
            return result;
        }

        private static void DescribePoint(Dictionary<string, object> result, Vector2 point)
        {
            result["clientPoint"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = point.X,
                ["y"] = point.Y
            };
            Vector2? screen = UiInspector.ToScreenPoint(point);
            result["screenPoint"] = screen.HasValue
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = screen.Value.X,
                    ["y"] = screen.Value.Y
                }
                : null;
        }

        private static string ListHint(Dictionary<string, object> listInfo)
        {
            object list;
            if (!listInfo.TryGetValue("list", out list))
                return string.Empty;
            var info = list as Dictionary<string, object>;
            if (info == null)
                return string.Empty;
            object items;
            if (!info.TryGetValue("items", out items))
                return string.Empty;
            var rows = items as List<Dictionary<string, object>>;
            if (rows == null || rows.Count == 0)
                return " The list is empty right now.";

            var names = new List<string>();
            for (int i = 0; i < rows.Count && i < 12; i++)
            {
                object text;
                rows[i].TryGetValue("text", out text);
                names.Add(text as string ?? "<null>");
            }
            return " Current rows: " + string.Join(" | ", names.ToArray());
        }

        private static int ListCount(Widget widget)
        {
            var panel = widget as ListPanelWidget;
            return panel != null ? panel.Items.Count : 0;
        }

        // ---------------------------------------------------------------- 点击

        /// <summary>
        /// 按语义目标点击。`mode`：`direct`（默认）/ `invoke` / `input` / `auto`
        /// （`auto` = 能发事件就发事件，否则单帧合成）。
        /// `mark` 打开时在点的那个像素上亮一下标记（编辑器"点一下"用；AI 回放不要开，免得刷屏）。
        ///
        /// 任何 mode 都先过 <see cref="EnsureHudTargetRevealed"/>（规则 1 + 规则 2）：世界内 HUD 的控件
        /// 在非触屏平台上平时是**整条侧栏被平移到屏外**的，必须先用玩家输入层的 E 强制展开 HUD；
        /// 目标又挂在 `MoreContents` 里（`MoreButton` 是二级开关）时还要先点一下那个开关。
        /// 这两步属于"点击命令本身"，所以 CLI、编辑器、行为树、动作回放全部自动获得，
        /// 调用方不需要知道 HUD 是怎么被藏起来的；回包里的 `hudRevealed` / `moreToggled` / `revealNote`
        /// 会如实写出桥替它做了什么。
        /// </summary>
        public Dictionary<string, object> Click(string target, string mode, int holdMilliseconds,
            bool mark, float markSeconds, float markPixels)
        {
            // 规则 1 / 规则 2：世界内 HUD 且当前不可点 → 先按 E 强制展开 HUD，目标挂在 `MoreContents`
            // 里就再点 MoreButton 展开二级，然后才点。做了什么全部写进下面的回包（见 `DescribeReveal`）。
            HudRevealReport reveal = EnsureHudTargetRevealed(target);

            string wanted = string.IsNullOrEmpty(mode) ? "direct" : mode.Trim().ToLowerInvariant();
            if (wanted == "auto")
                wanted = "invoke-first";

            string used = wanted;
            object invoked = null;

            if (wanted == "invoke-first" || wanted == "invoke")
            {
                object attempt = m_injector.OnGameThreadForUi(delegate
                {
                    return TryInvokeCore(target);
                });
                var info = attempt as Dictionary<string, object>;
                if (info != null)
                {
                    invoked = info;
                    used = "invoke";
                }
                else if (wanted == "invoke")
                {
                    throw new BridgeCommandException("invoke_unavailable",
                        "The target has no click event to raise; use mode=direct instead.");
                }
                else
                {
                    used = "direct";
                }
            }
            else if (wanted != "direct" && wanted != "input")
            {
                throw new BridgeCommandException("invalid_argument",
                    "mode must be direct, invoke, auto or input (got '" + mode + "').");
            }

            if (used == "invoke")
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                var info = invoked as Dictionary<string, object>;
                foreach (KeyValuePair<string, object> pair in info)
                    result[pair.Key] = pair.Value;
                result["mode"] = "invoke";
                result["modeReason"] = "控件自己的按下事件被直接触发（绕过输入层）";
                if (mark)
                {
                    var located = (Dictionary<string, object>)m_injector.OnGameThreadForUi(delegate
                    {
                        Dictionary<string, object> found = LocateCore(target);
                        MarkLocation(found, markSeconds, markPixels);
                        return found;
                    });
                    object marked;
                    if (located.TryGetValue("marked", out marked))
                        result["marked"] = marked;
                }
                DescribeReveal(result, reveal);
                return result;
            }

            if (used == "input")
            {
                Dictionary<string, object> session = ClickViaSession(target, holdMilliseconds);
                DescribeReveal(session, reveal);
                return session;
            }

            var direct = (Dictionary<string, object>)m_injector.OnGameThreadForUi(delegate
            {
                Dictionary<string, object> clicked = ClickDirectCore(target);
                if (mark)
                    MarkLocation(clicked, markSeconds, markPixels);
                return clicked;
            });
            DescribeReveal(direct, reveal);
            return direct;
        }

        /// <summary>
        /// 把规则 1 / 规则 2 的经过写进点击回包（用户要求：要能看出桥替它做了什么 —— 不拦截、不静默）。
        ///
        /// 字段固定：`hudRevealed`（是否用 E 强制显示了 HUD 侧栏）/ `hudRevealKeyPresses`（按了几次）/
        /// `hudRevealSteps`（那一步做过什么）/ `moreToggled`（是否点了二级开关 MoreButton）/
        /// `moreToggleClicks`（点了几次）/ `moreToggleSteps`（点了哪个控件、落在哪个像素、开关前后 IsChecked）/
        /// `revealNote`（一句话总结）。两条步骤都没发生时它们如实写 false / 0 / 空 —— 不是缺字段。
        /// </summary>
        private static void DescribeReveal(Dictionary<string, object> result, HudRevealReport report)
        {
            if (result == null || report == null)
                return;
            result["hudRevealed"] = report.HudRevealed;
            result["hudRevealKeyPresses"] = report.HudRevealKeyPresses;
            result["hudRevealSteps"] = new List<string>(report.HudRevealSteps);
            result["moreToggled"] = report.MoreToggled;
            result["moreToggleClicks"] = report.MoreToggleClicks;
            result["moreToggleSteps"] = new List<string>(report.MoreToggleSteps);
            result["revealNote"] = string.IsNullOrEmpty(report.Note)
                ? ("hudRevealed=" + report.HudRevealed.ToString().ToLowerInvariant()
                    + "; moreToggled=" + report.MoreToggled.ToString().ToLowerInvariant()
                    + "; steps=" + DescribeRevealSteps(report))
                : report.Note;
        }

        /// <summary>
        /// 只求"点在哪"（行为树 / 门面的非阻塞路径用）：语义目标 → **当前**客户区坐标。
        /// 必须在游戏线程、且在点击那一刻调用（坐标随窗口尺寸与 UI 缩放变）。
        /// </summary>
        internal Vector2 ResolvePointCore(string target)
        {
            return PointOf(LocateCore(target));
        }

        /// <summary>
        /// 求"要点的那个控件"：**输入面必须取自它**。
        ///
        /// 引擎里每个 `WidgetsHierarchyInput` 是独立一份输入面：主菜单的屏用根输入面，
        /// 世界内 HUD 用 `GameWidget` 那层自己的输入面（`GameWidget.cs:104-106`）。
        /// 软光标位置 / `IsMouseCursorVisible` / `m_mouseDownPoint` 都是**每面一份**，
        /// 写错面 = 那一层什么都没变 = 点不动（实测：世界内 MoreButton）。
        /// 取不到（没命中任何控件）时返回 null，由调用方退回根输入面。
        /// </summary>
        internal Widget ResolveTargetCore(string target)
        {
            Dictionary<string, object> located = LocateCore(target);
            ContainerWidget root = ScreensManager.RootWidget;
            return root != null ? root.HitTestGlobal(PointOf(located)) : null;
        }

        /// <summary>
        /// 单帧合成点击：解析目标 → 把"按过一下"写进**目标那一层**的输入面
        /// → 引擎自己在同一帧派生 `Tap`+`Click`。
        ///
        /// HUD 的"强制展开"（规则 1）与"展开 MoreContents 二级开关"（规则 2）**都不在这里做**
        /// （唯一入口是 <see cref="EnsureHudTargetRevealed"/>，
        /// 由 <see cref="Click"/> 与 `InputInjector.UiClickTargetCore` 在进入本方法之前调用）。
        /// 这里曾经有过一份"同帧按 E 又同帧重解析"的重复实现，它必然失败：HUD 侧栏是
        /// `ComponentGui.UpdateSidePanelsAnimation()` 每帧约 0.2 指数逼近滑入的（factor 1→0
        /// 要 6~10 帧），同帧重解析拿到的还是旧坐标；而且它在模态面板已开时也会按 E，
        /// 等于把用户已展开的面板关掉（E 是开关）。已删除，入口只保留一个。
        /// </summary>
        internal Dictionary<string, object> ClickDirectCore(string target)
        {
            Dictionary<string, object> located = LocateCore(target);
            Vector2 point = PointOf(located);

            ContainerWidget root = ScreensManager.RootWidget;
            Widget widget = root != null ? root.HitTestGlobal(point) : null;
            if (widget == null)
            {
                // 点不到就是点不到：**绝不**改成"点屏幕角落"糊过去。
                // 用户实测踩到的正是这里：世界内 HUD 那条侧栏在非触屏平台上被整条平移到屏幕外，
                // 它的中心落在屏幕外，以前这里会照点，于是命中左上角某个控件 ——
                // 用户看到"点了一下，什么都没发生"，而且回包还写着命中了别的控件。
                // 规则 1 的强制展开已经在 `EnsureHudTargetRevealed` 里做过（跨帧等 HUD 布局完），
                // 走到这里说明它不在 HUD / 模态面板已开 / 展开后仍然落不到东西 —— 如实失败。
                throw new BridgeCommandException("element_off_screen",
                    "目标现在的落点 (" + point.X.ToString("0.#") + "," + point.Y.ToString("0.#")
                    + ") 上什么都没有：多半是屏幕外/被折叠的控件（例如非触屏平台上被平移出屏的 HUD 侧栏），"
                    + "或者它还没被布局。先用「定位」看它的真实坐标与不可点原因。");
            }





            // 落点要落在"目标自己或它内部的可点件"上：按钮中心常盖着图标子控件，直接点中心会点空。
            Widget wanted = ResolveElementCore(target) ?? widget;
            Vector2 clickPoint = PickClickPoint(root, wanted, point);
            bool adjusted = (clickPoint - point).Length() > 0.01f;
            Widget hitAtClick = root != null ? (root.HitTestGlobal(clickPoint) ?? widget) : widget;

            // 引擎在"窗口不活跃"时整段输入都不算（`WidgetInput.Update()` 里 `if (Window.IsActive)`）——
            // 这时合成点击会被**无声无息地丢掉**。共控模式（focus.attach）不强制活跃，
            // 所以真实焦点不在游戏里时必然是这个状态（实测：刚启动游戏、窗口还没拿到焦点时点 Play 点不动）。
            // 与其让用户以为"功能坏了"，不如如实拒绝并说清怎么让它能点。
            if (!Window.IsActive)
            {
                throw new BridgeCommandException("window_not_active",
                    "游戏窗口现在不是活跃窗口（引擎认为它不在前台），输入会被整段忽略："
                    + "让游戏窗口拿到焦点，或用 focus.detach / focus.auto 让本 Mod 接管输入，再点一次。");
            }
            m_injector.ApplyDirectClick(clickPoint, hitAtClick);
            located["mode"] = "direct";
            located["modeReason"] = "单帧合成 Tap+Click（引擎自己的控件逻辑照常跑）";
            located["clickPoint"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = clickPoint.X,
                ["y"] = clickPoint.Y
            };
            if (adjusted)
                located["clickPointAdjusted"] = true;
            located["clickTarget"] = hitAtClick.Name ?? hitAtClick.GetType().Name;
            located["clickTargetOnTarget"] = IsClickConsumer(hitAtClick, wanted);
            return located;
        }

        /// <summary>
        /// 这个命中结果"算不算点到了目标"：要么就是它自己，要么是它子孙里的 `ClickableWidget`
        /// （复合按钮的内部可点件）。
        /// </summary>
        internal static bool IsClickConsumer(Widget hit, Widget target)
        {
            if (hit == null || target == null)
                return false;
            for (Widget current = hit; current != null; current = current.ParentWidget)
            {
                if (ReferenceEquals(current, target))
                    return hit is ClickableWidget || ReferenceEquals(hit, target);
            }
            return false;
        }

        /// <summary>
        /// 解析"目标控件**本身**"（不是它中心盖着的那个子控件）。
        /// 点按钮时要的是按钮、或它内部那个真正的可点件，所以这个才是"想在哪儿点"的参照物。
        /// </summary>
        internal Widget ResolveElementCore(string target)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                return null;
            UiTarget.Parsed parsed;
            if (!UiTarget.TryParse(target, out parsed))
                return null;
            if (parsed.Kind == UiTarget.TargetKind.Selector)
                return UiInspector.Resolve(root, parsed.Selector, false, default(Vector2));
            Vector2 point = PointOf(LocateCore(target));
            return root.HitTestGlobal(point);
        }

        /// <summary>
        /// 在目标控件里挑一个"真的会被消费"的落点。
        ///
        /// 引擎里有两种形状，**别按同一种处理**：
        ///   · `ClickableWidget` 自己就是被点的那一个：`ClickableWidget.Update` 判 `HitTestGlobal(...) == this`
        ///     （`ClickableWidget.cs:39`），所以必须正好命中它自己；
        ///   · 复合按钮（`BitmapButtonWidget` 这类 `ButtonWidget`）内部**包着一个** `ClickableWidget`
        ///     （`BitmapButtonWidget.cs:12,18`：`IsClicked => m_clickableWidget.IsClicked`），
        ///     真正要被命中的是那个**子控件** —— 它没有名字，所以日志里看着像"点错了"。
        ///
        /// 所以规则是：落点命中"目标自己或其子孙里的 ClickableWidget"就算对；中心不行就在矩形里
        /// 5×5 网格扫一圈（内缩 15%）；都不行才退回中心（调用方会如实标注）。
        /// </summary>
        internal static Vector2 PickClickPoint(ContainerWidget root, Widget target, Vector2 fallback)
        {
            if (root == null || target == null)
                return fallback;
            if (IsClickConsumer(root.HitTestGlobal(fallback), target))
                return fallback;

            BoundingRectangle bounds = target.GlobalBounds;
            float width = bounds.Max.X - bounds.Min.X;
            float height = bounds.Max.Y - bounds.Min.Y;
            if (width <= 0.001f || height <= 0.001f)
                return fallback;

            for (int row = 0; row < 5; row++)
            {
                for (int column = 0; column < 5; column++)
                {
                    float fx = 0.15f + 0.175f * column;
                    float fy = 0.15f + 0.175f * row;
                    var candidate = new Vector2(bounds.Min.X + width * fx, bounds.Min.Y + height * fy);
                    if (ReferenceEquals(root.HitTestGlobal(candidate), target))
                        return candidate;
                }
            }
            return fallback;
        }

        /// <summary>老的软光标会话（移动 → 按下 → 抬起，一步一帧）。</summary>
        private Dictionary<string, object> ClickViaSession(string target, int holdMilliseconds)
        {
            UiTarget.Parsed parsed;
            if (!UiTarget.TryParse(target, out parsed))
            {
                throw new BridgeCommandException("invalid_argument",
                    "Cannot parse the UI target '" + (target ?? "<null>") + "'.");
            }

            // 复用 `act.uiclick` 那条已经验证过的路（会话 + 现算坐标）
            object clicked;
            switch (parsed.Kind)
            {
                case UiTarget.TargetKind.ListRow:
                    clicked = m_injector.UiClickSession(parsed.Selector, false, 0f, 0f,
                        parsed.RowIndex, parsed.RowText, holdMilliseconds);
                    break;
                case UiTarget.TargetKind.Point:
                    clicked = m_injector.UiClickSession(null, true, parsed.X, parsed.Y, -1, null,
                        holdMilliseconds);
                    break;
                default:
                    clicked = m_injector.UiClickSession(parsed.Selector, false, 0f, 0f, -1, null,
                        holdMilliseconds);
                    break;
            }

            var result = clicked as Dictionary<string, object>;
            if (result == null)
            {
                throw new BridgeCommandException("failed",
                    "The UI click session did not return a result.");
            }
            result["mode"] = "input";
            result["modeReason"] = "多帧软光标会话（移动→按下→抬起）";
            return result;
        }

        private static Vector2 PointOf(Dictionary<string, object> located)
        {
            object value;
            if (located.TryGetValue("clientPoint", out value))
            {
                var point = value as Dictionary<string, object>;
                if (point != null)
                {
                    float x;
                    float y;
                    if (TryNumber(point, "x", out x) && TryNumber(point, "y", out y))
                        return new Vector2(x, y);
                }
            }
            throw new BridgeCommandException("failed", "The located target carries no clientPoint.");
        }

        private static bool TryNumber(Dictionary<string, object> source, string key, out float value)
        {
            value = 0f;
            object raw;
            if (!source.TryGetValue(key, out raw) || raw == null)
                return false;
            if (raw is float)
            {
                value = (float)raw;
                return true;
            }
            if (raw is double)
            {
                value = (float)(double)raw;
                return true;
            }
            if (raw is int)
            {
                value = (int)raw;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 直接触发控件事件

        /// <summary>
        /// 能直接"发出按下"的就直接发：目前引擎里唯一带点击事件的是
        /// `ListPanelWidget.ItemClicked`（`Game/ListPanelWidget.cs:122`）。
        ///
        /// ⚠️ 两个坑（都踩过/查过源码）：
        ///   1. `PlayScreen` 的处理器写着 `if (selectedItem == item) Play(item)` ——
        ///      `SelectedItem` 是 `ListPanelWidget.Update` 在**发事件之后**才更新的
        ///      （`ListPanelWidget.cs:258-269`）。所以只发事件**不会**进世界：
        ///      必须先把 `SelectedIndex` 摆到这一行，再发事件，才等价于"用户点了这一行"。
        ///   2. 因此 `invoke` 实质上替界面做了"选中"这个决定 —— 它是**绕过输入层**的，
        ///      默认不给 AI 用（AI 用 `direct`：引擎自己跑完整逻辑）。
        /// </summary>
        internal Dictionary<string, object> TryInvokeCore(string target)
        {
            UiTarget.Parsed parsed;
            if (!UiTarget.TryParse(target, out parsed) || parsed.Kind != UiTarget.TargetKind.ListRow)
                return null;

            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                throw new BridgeCommandException("not_ready", "The UI is not ready yet.");

            Widget listWidget = UiInspector.Resolve(root, parsed.Selector, false, default(Vector2));
            var panel = listWidget as ListPanelWidget;
            if (panel == null)
                return null;

            ReadOnlyList<object> items = panel.Items;
            int index = -1;
            if (!string.IsNullOrEmpty(parsed.RowText))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    string text = UiInspector.DescribeListItemText(items[i]);
                    if (text != null && text.IndexOf(parsed.RowText, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        index = i;
                        break;
                    }
                }
            }
            else if (parsed.RowIndex >= 0 && parsed.RowIndex < items.Count)
            {
                index = parsed.RowIndex;
            }

            if (index < 0)
            {
                throw new BridgeCommandException("element_missing",
                    "No such row in '" + parsed.Selector + "' (row=" + parsed.RowIndex
                    + " text=" + (parsed.RowText ?? "<none>") + ")."
                    + ListHint(UiInspector.DescribeElement(panel, 0)));
            }

            EventInfo clicked = typeof(ListPanelWidget).GetEvent("ItemClicked");
            if (clicked == null)
                return null;

            panel.SelectedIndex = index;        // 先摆好处理器的前置状态（见上面的坑 1）
            Delegate handler = GetEventHandler(panel, "ItemClicked");
            if (handler == null)
            {
                throw new BridgeCommandException("invoke_unavailable",
                    "Nothing is listening to this list's ItemClicked event.");
            }

            object item = items[index];
            handler.DynamicInvoke(item);

            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["name"] = panel.Name;
            result["path"] = UiInspector.BuildPath(panel);
            result["type"] = panel.GetType().Name;
            result["text"] = UiInspector.DescribeListItemText(item);
            result["list"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["selector"] = parsed.Selector,
                ["index"] = index,
                ["text"] = UiInspector.DescribeListItemText(item),
                ["count"] = items.Count
            };
            return result;
        }

        /// <summary>取某个事件的委托（`ItemClicked` 这类字段式事件）。</summary>
        private static Delegate GetEventHandler(object target, string eventName)
        {
            Type type = target.GetType();
            FieldInfo field = type.GetField(eventName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
                return null;
            return field.GetValue(target) as Delegate;
        }
    }
}
