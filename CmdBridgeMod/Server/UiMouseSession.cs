using Engine;
using Engine.Input;
using Game;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CmdBridgeMod
{
    /// <summary>
    /// 虚拟 UI 鼠标会话 —— 用引擎内的**软光标**完成点击 / 拖拽，**完全不动用户的物理鼠标**。
    ///
    /// 为什么要它（早期实测的教训）：
    ///   · 走真实光标（`SetCursorPos` + 注入按键）时，必须先让物理鼠标进到游戏窗口；
    ///     而且真实光标一旦在窗口外，引擎会把"按下点 vs 当前点"算成拖拽，直接走错分支。
    ///   · 软光标下 `WidgetInput.MousePosition` 返回可编程的 `m_softMouseCursorPosition`，
    ///     UI 只看它，与真实光标位置无关（`Survivalcraft/Game/WidgetInput.cs:168-228`）。
    ///
    /// 关键约束（源码依据，实现时逐条满足）：
    ///   1. `WidgetInput.UpdateInputFromMouse` 只在 `IsMouseCursorVisible && MousePosition.HasValue` 时才派生
    ///      `Press/Tap/Click/Drag`（`Game/WidgetInput.cs:741`）。世界视图里 `ComponentInput.cs:155` 每帧会把
    ///      可见性置 false → 会话每步都要重新断言 `IsMouseCursorVisible = true`。
    ///   2. 拖拽判定：按住且移动距离超过 `MinimumDragDistance * GlobalScale` 时进入拖拽；
    ///      距离首次超阈值那一帧 `Drag = m_mouseDownPoint`（按下点），所以拖拽一定从源控件起跳，
    ///      而**松开必须发生在已经进入拖拽之后的帧**，否则引擎仍按点击处理（`Game/WidgetInput.cs:779-795`）。
    ///   3. "长按分割源 → 拖动"是分割物品的正路：`InventorySlotWidget.cs:319-325` 长按设置分割源，
    ///      随后拖动时 `dragMode` 变为 `SingleItem`（同文件 :335-338）→ 只搬 1 个而不是整叠。
    ///      所以 `ui.drag --hold 600` 就是"分离物品"。（长按后原地松开会被同一格的 Click 分支取消，必须拖走。）
    ///   4. 鼠标按键数组是真实鼠标与注入共用的 → `mask` 模式下每帧强制两个键的状态。
    ///
    /// 执行模型：所有动作排队成"每帧一步"，由 <see cref="FrameStartPump"/> 在帧首于游戏线程执行；
    /// 一步一帧是确定性的（不依赖 sleep 时序），因此多帧手势（按下→移动→松开）不会挤进同一帧。
    /// </summary>
    internal sealed class UiMouseSession
    {
        private enum StepKind
        {
            Assert,
            Move,
            ResolveMove,
            Press,
            Release,
            KeyDown,
            KeyUp,
            Finish
        }

        private sealed class Step
        {
            public StepKind Kind;
            public Vector2 Point;
            public bool HasPoint;
            public string Selector;
            public MouseButton Button;
            public Key Key;
            public string Note;
        }

        /// <summary>手势端点：要么是坐标，要么是元素选择器（运行时解析成中心点）。</summary>
        internal struct Endpoint
        {
            public string Selector;
            public Vector2 Point;
            public bool HasPoint;

            public static Endpoint At(Vector2 point)
            {
                return new Endpoint { Point = point, HasPoint = true };
            }

            public static Endpoint At(string selector)
            {
                return new Endpoint { Selector = selector };
            }

            public override string ToString()
            {
                return Selector != null ? Selector : ("(" + Point.X.ToString("0") + "," + Point.Y.ToString("0") + ")");
            }
        }

        private readonly InputInjector m_injector;
        private readonly object m_gate = new object();
        private readonly Queue<Step> m_steps = new Queue<Step>();

        private bool m_active;
        private bool m_mask;
        private bool m_hasPosition;
        private Vector2 m_position;

        // Android 触摸拖拽：会话里"是否已经按下"的触摸侧状态（见 AndroidTouch）。
        private bool m_touchDown;
        private bool m_leftDown;
        private bool m_rightDown;
        private readonly HashSet<int> m_heldKeys = new HashSet<int>();
        private WidgetInput m_input;
        private string m_lastError;
        private string m_lastAction;
        private int m_executedSteps;
        private bool m_executing;

        /// <summary>是否已经往帧首泵排了一个 tick（保证"一步一帧"，见 TickOnce 的注释）。</summary>
        private bool m_tickQueued;

        public UiMouseSession(InputInjector injector)
        {
            if (injector == null)
                throw new ArgumentNullException(nameof(injector));
            m_injector = injector;
        }

        // ---------------------------------------------------------------- 状态查询

        public bool Active
        {
            get { lock (m_gate) return m_active; }
        }

        public bool Mask
        {
            get { lock (m_gate) return m_mask; }
        }

        public int PendingSteps
        {
            get { lock (m_gate) return m_steps.Count; }
        }

        public bool IsIdle
        {
            get
            {
                lock (m_gate)
                {
                    return m_steps.Count == 0 && !m_executing;
                }
            }
        }

        public Dictionary<string, object> Describe()
        {
            lock (m_gate)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["active"] = m_active,
                    ["mask"] = m_mask,
                    ["position"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["x"] = m_position.X,
                        ["y"] = m_position.Y
                    },
                    ["hasPosition"] = m_hasPosition,
                    ["leftDown"] = m_leftDown,
                    ["rightDown"] = m_rightDown,
                    ["pendingSteps"] = m_steps.Count,
                    ["executedSteps"] = m_executedSteps,
                    ["executing"] = m_executing,
                    ["heldKeys"] = m_heldKeys.Count,
                    ["lastAction"] = m_lastAction,
                    ["lastError"] = m_lastError,
                    ["pump"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["running"] = m_injector.Pump.IsRunning,
                        ["pending"] = m_injector.Pump.PendingCount,
                        ["framesPumped"] = m_injector.Pump.FramesPumped,
                        ["executed"] = m_injector.Pump.ActionsExecuted,
                        ["dropped"] = m_injector.Pump.ActionsDropped
                    }
                };
                return result;
            }
        }

        // ---------------------------------------------------------------- 会话开关

        public Dictionary<string, object> Begin(bool mask)
        {
            lock (m_gate)
            {
                // **清掉上一轮的残留步骤**：`End()` 是"往队列尾塞一个 Finish"，如果那一轮
                // 还有步骤没跑完（或被异常中断），这个 Finish 会留在队列里，等**下一轮**
                // 会话开始后才被执行 —— 于是新会话刚起步就被自己的"结束"掐掉：
                // active 变 false、pendingSteps 永远回不到 0、手势只做了一半。
                // （实测症状：`ui.click` 报 completed=false、面板不关，CM-1 探针一片红。）
                m_steps.Clear();
                m_executedSteps = 0;
                m_active = true;
                m_mask = mask;
                m_lastError = null;
                m_lastAction = "session.begin";
                m_tickQueued = false;
                m_steps.Enqueue(new Step { Kind = StepKind.Assert, Note = "begin" });
            }

            // begin 自己也要排一次帧首执行：否则这一步会一直挂在队列里，
            // 直到后续某个 Enqueue 顺手把它带出去（曾经就是这样：pendingSteps 永远是 1，
            // 而且"哪一帧开始生效"取决于下一条命令什么时候来）。
            QueueTickIfNeeded();
            return Describe();
        }

        /// <summary>结束会话：释放按键、清掉残留的按下起点、交回用户鼠标。</summary>
        public Dictionary<string, object> End()
        {
            lock (m_gate)
            {
                m_lastAction = "session.end";
                m_steps.Enqueue(new Step { Kind = StepKind.Finish, Note = "end" });
            }

            // 和 Begin 同一个坑：不排帧首 tick 的话，这个 Finish 永远不执行
            // （症状：`ui.session.end` 之后 active 一直是 true）。
            QueueTickIfNeeded();
            return Describe();
        }

        /// <summary>立即结束（不等帧首队列）：Mod 卸载、异常兜底时用。</summary>
        public void EndImmediate()
        {
            lock (m_gate)
            {
                m_steps.Clear();
                m_active = false;
                m_tickQueued = false;
            }

            try
            {
                m_injector.SetMouseHeld(MouseButton.Left, false);
                m_injector.SetMouseHeld(MouseButton.Right, false);
                foreach (int key in m_heldKeys)
                    m_injector.SetKeyHeld((Key)key, false);
                m_heldKeys.Clear();
                WidgetInput input = m_input ?? m_injector.GetRootWidgetInput();
                if (input != null)
                {
                    m_injector.ClearWidgetMouseDownPoint(input);
                    input.UseSoftMouseCursor = false;
                }
            }
            catch (Exception exception)
            {
                Log.Warning("[CmdBridge] UiMouseSession.EndImmediate failed: " + exception.Message);
            }

            m_leftDown = false;
            m_rightDown = false;
        }

        // ---------------------------------------------------------------- 原子动作

        public Dictionary<string, object> MoveTo(Vector2 point, int steps)
        {
            EnqueueMove(point, steps, "move");
            return Describe();
        }

        public Dictionary<string, object> MoveToElement(string selector, int steps)
        {
            Enqueue(new Step { Kind = StepKind.ResolveMove, Selector = selector, Note = "resolve-move" });
            // 解析出的落点未知，插值步交给一个"解析后再走"的组合：这里退化为一次定位 + 可选细分。
            if (steps > 1)
                Enqueue(new Step { Kind = StepKind.Assert, Note = "settle" });
            return Describe();
        }

        public Dictionary<string, object> Press(MouseButton button)
        {
            Enqueue(new Step { Kind = StepKind.Press, Button = button, Note = "press:" + button });
            return Describe();
        }

        public Dictionary<string, object> Release(MouseButton button)
        {
            Enqueue(new Step { Kind = StepKind.Release, Button = button, Note = "release:" + button });
            return Describe();
        }

        public Dictionary<string, object> Click(string selector)
        {
            m_injector.NoteUiAction("click:" + selector);
            Enqueue(new Step { Kind = StepKind.ResolveMove, Selector = selector, Note = "click:move" });
            Enqueue(new Step { Kind = StepKind.Press, Button = MouseButton.Left, Note = "click:press" });
            Enqueue(new Step { Kind = StepKind.Release, Button = MouseButton.Left, Note = "click:release" });
            return Describe();
        }

        public Dictionary<string, object> RightClick(string selector)
        {
            Enqueue(new Step { Kind = StepKind.ResolveMove, Selector = selector, Note = "rclick:move" });
            Enqueue(new Step { Kind = StepKind.Press, Button = MouseButton.Right, Note = "rclick:press" });
            Enqueue(new Step { Kind = StepKind.Release, Button = MouseButton.Right, Note = "rclick:release" });
            return Describe();
        }

        /// <summary>Shift + 左键：引擎把它当 `SpecialClick`（`WidgetInput.cs:755-760`）。</summary>
        public Dictionary<string, object> ShiftClick(string selector)
        {
            Enqueue(new Step { Kind = StepKind.KeyDown, Key = Key.Shift, Note = "shift:down" });
            Enqueue(new Step { Kind = StepKind.ResolveMove, Selector = selector, Note = "shift:move" });
            Enqueue(new Step { Kind = StepKind.Press, Button = MouseButton.Left, Note = "shift:press" });
            Enqueue(new Step { Kind = StepKind.Release, Button = MouseButton.Left, Note = "shift:release" });
            Enqueue(new Step { Kind = StepKind.KeyUp, Key = Key.Shift, Note = "shift:up" });
            return Describe();
        }

        /// <summary>
        /// 拖拽：定位 → 按下 →（可选长按）→ 分步移动到目标 → 松开。
        /// `holdMs ≥ 500` 时即为"长按分割源后拖动"，引擎会以 `SingleItem` 模式只搬 1 个（分离物品）。
        /// </summary>
        public Dictionary<string, object> Drag(Endpoint from, Endpoint to, int steps, int holdMs)
        {
            Vector2 a = ResolveEndpoint(from, "drag:from");
            Vector2 b = ResolveEndpoint(to, "drag:to");
            if (steps < 2)
                steps = 2;

            Enqueue(new Step { Kind = StepKind.Move, Point = a, HasPoint = true, Note = "drag:move-from" });
            Enqueue(new Step { Kind = StepKind.Press, Button = MouseButton.Left, Note = "drag:press" });

            int holdFrames = holdMs > 0 ? (int)Math.Ceiling(holdMs / 16.7) : 0;
            for (int i = 0; i < holdFrames; i++)
                Enqueue(new Step { Kind = StepKind.Assert, Note = "drag:hold" + i });

            // 第一步必须跨过拖拽阈值（MinimumDragDistance * GlobalScale，默认约 10*scale），
            // 否则引擎会把它当"长按"而不是拖拽。
            Vector2 delta = b - a;
            float distance = delta.Length();
            float firstStepDistance = Math.Min(Math.Max(40f, distance * 0.25f), Math.Max(distance, 1f));
            Vector2 firstPoint = distance > 1f ? a + Vector2.Normalize(delta) * firstStepDistance : b;
            Enqueue(new Step { Kind = StepKind.Move, Point = firstPoint, HasPoint = true, Note = "drag:step1" });

            int remaining = steps - 1;
            for (int i = 1; i <= remaining; i++)
            {
                float t = i / (float)remaining;
                Vector2 point = Vector2.Lerp(firstPoint, b, t);
                Enqueue(new Step { Kind = StepKind.Move, Point = point, HasPoint = true, Note = "drag:step" + (i + 1) });
            }

            Enqueue(new Step { Kind = StepKind.Move, Point = b, HasPoint = true, Note = "drag:to-target" });
            Enqueue(new Step { Kind = StepKind.Release, Button = MouseButton.Left, Note = "drag:release" });
            return Describe();
        }

        // ---------------------------------------------------------------- 队列与执行

        private void EnqueueMove(Vector2 point, int steps, string note)
        {
            if (steps < 1)
                steps = 1;

            Vector2 origin;
            bool hasOrigin;
            lock (m_gate)
            {
                origin = m_position;
                hasOrigin = m_hasPosition;
            }

            if (steps == 1 || !hasOrigin)
            {
                Enqueue(new Step { Kind = StepKind.Move, Point = point, HasPoint = true, Note = note });
                return;
            }

            for (int i = 1; i <= steps; i++)
            {
                Vector2 p = Vector2.Lerp(origin, point, i / (float)steps);
                Enqueue(new Step { Kind = StepKind.Move, Point = p, HasPoint = true, Note = note + i });
            }
        }

        private void Enqueue(Step step)
        {
            lock (m_gate)
            {
                if (!m_active)
                    throw new BridgeCommandException(
                        "session_inactive", "No UI mouse session is active; call ui.session.begin first.");
                m_lastAction = step.Note;
                m_steps.Enqueue(step);
            }

            QueueTickIfNeeded();
        }

        /// <summary>确保队列里排着一个帧首 tick（已有就不重复排）。</summary>
        private void QueueTickIfNeeded()
        {
            bool needTick;
            lock (m_gate)
            {
                needTick = !m_tickQueued;
                if (needTick)
                    m_tickQueued = true;
            }
            if (needTick)
                m_injector.Pump.Enqueue(TickOnce);
        }

        /// <summary>
        /// 在游戏线程（帧首）执行**一步**。由 FrameStartPump 调用。
        ///
        /// 关键点：**一步一帧**。帧首泵会把"当前排队的回调"打包到同一帧里跑
        /// （FrameStartPump.Loop: `m_pending.ToArray()` → 一帧内全执行完），所以要是每个步骤
        /// 各排一个回调，`ui.click` 的"按下 + 抬起"就落在**同一帧**里 ——
        /// 引擎的 WidgetInput 靠"按下留起点、松开那一帧派生 Click"来判定点击，
        /// 同帧内按了又松什么也派生不出来（实测：面板不关、leftDown 卡住）。
        /// 所以这里每步跑完再补排一个回调：补排的那个会落进**下一帧**的批次。
        /// </summary>
        private void TickOnce()
        {
            Step step;
            lock (m_gate)
            {
                if (m_steps.Count == 0)
                {
                    m_tickQueued = false;
                    return;
                }
                step = m_steps.Dequeue();
                m_executing = true;
            }

            try
            {
                Apply(step);
                m_executedSteps++;
            }
            catch (Exception exception)
            {
                lock (m_gate)
                {
                    m_lastError = exception.GetType().Name + ": " + exception.Message;
                    m_steps.Clear();
                }
                Log.Warning("[CmdBridge] UiMouseSession step '" + step.Note + "' failed: " + exception.Message);
                EndImmediate();
                return;
            }
            finally
            {
                lock (m_gate)
                {
                    m_executing = false;
                }
            }

            // 还有步骤 → 补排一个回调，落到下一帧继续（一步一帧）
            bool again;
            lock (m_gate)
            {
                again = m_steps.Count > 0;
                if (!again)
                    m_tickQueued = false;
            }
            if (again)
                m_injector.Pump.Enqueue(TickOnce);
        }

        private void Apply(Step step)
        {
            switch (step.Kind)
            {
                case StepKind.Assert:
                    AssertState();
                    break;

                case StepKind.Move:
                    m_position = step.Point;
                    m_hasPosition = true;
                    AssertState();
                    break;

                case StepKind.ResolveMove:
                {
                    Vector2 center = ResolveElementCenter(step.Selector);
                    m_position = center;
                    m_hasPosition = true;
                    AssertState();
                    break;
                }

                case StepKind.Press:
                    AssertState();
                    if (step.Button == MouseButton.Left)
                        m_leftDown = true;
                    else if (step.Button == MouseButton.Right)
                        m_rightDown = true;
                    m_injector.SetMouseHeld(step.Button, true);
                    ApplyAndroidTouch();
                    break;

                case StepKind.Release:
                    AssertState();
                    if (step.Button == MouseButton.Left)
                        m_leftDown = false;
                    else if (step.Button == MouseButton.Right)
                        m_rightDown = false;
                    m_injector.SetMouseHeld(step.Button, false);
                    ApplyAndroidTouch();
                    break;

                case StepKind.KeyDown:
                    m_heldKeys.Add((int)step.Key);
                    m_injector.SetKeyHeld(step.Key, true);
                    break;

                case StepKind.KeyUp:
                    m_heldKeys.Remove((int)step.Key);
                    m_injector.SetKeyHeld(step.Key, false);
                    break;

                case StepKind.Finish:
                    FinishSession();
                    break;
            }
        }

        /// <summary>会话不变量：软光标 + 光标可见 + 当前位置 (+ mask 时的按键状态)。</summary>
        private void AssertState()
        {
            WidgetInput input = m_input ?? m_injector.GetRootWidgetInput();
            if (input == null)
                throw new BridgeCommandException("not_ready", "The UI input surface is not ready.");
            m_input = input;

            input.UseSoftMouseCursor = true;
            // 世界视图里 ComponentInput 每帧会把可见性置 false；不重申就派生不出 Click/Drag。
            input.IsMouseCursorVisible = true;
            if (m_hasPosition)
                input.MousePosition = m_position;

            if (m_mask)
            {
                m_injector.SetMouseHeld(MouseButton.Left, m_leftDown);
                m_injector.SetMouseHeld(MouseButton.Right, m_rightDown);
            }

            ApplyAndroidTouch();
        }

        /// <summary>
        /// Android：拖拽/滑动只能靠触摸 —— 摇杆、滑条、HUD 都从 `TouchLocations` 派生 Drag，
        /// 桌面那套"软光标 + 按住"在 Android 上派生不出任何东西。
        /// 会话的按下/移动/抬起在这里统一翻译成触摸的 Press/Move/Release（同一个合成指针 id）。
        /// Windows 上 <see cref="AndroidTouch.Available"/> 恒为 false，这条路径完全不参与。
        /// </summary>
        private void ApplyAndroidTouch()
        {
            if (!AndroidTouch.Available || !m_hasPosition)
                return;

            bool down = m_leftDown || m_rightDown;
            if (down && !m_touchDown)
            {
                m_touchDown = true;
                AndroidTouch.Press(m_position);
            }
            else if (down)
            {
                AndroidTouch.Move(m_position);
            }
            else if (m_touchDown)
            {
                m_touchDown = false;
                AndroidTouch.Release(m_position);
            }
        }

        private void FinishSession()
        {
            if (m_touchDown)
            {
                m_touchDown = false;
                AndroidTouch.Release(m_position);
            }
            m_injector.SetMouseHeld(MouseButton.Left, false);
            m_injector.SetMouseHeld(MouseButton.Right, false);
            foreach (int key in m_heldKeys)
                m_injector.SetKeyHeld((Key)key, false);
            m_heldKeys.Clear();

            WidgetInput input = m_input ?? m_injector.GetRootWidgetInput();
            if (input != null)
            {
                m_injector.ClearWidgetMouseDownPoint(input);
                input.UseSoftMouseCursor = false;
            }

            lock (m_gate)
            {
                // 会话结束时把还没跑完的步骤一并丢掉：留着的步骤会在下一轮会话里被
                // 当作"自己的步骤"执行（另一个方向的同一个坑，见 Begin 的注释）。
                m_steps.Clear();
                m_active = false;
                m_leftDown = false;
                m_rightDown = false;
                m_tickQueued = false;
            }
        }

        /// <summary>把端点解析成坐标（元素端点需要在游戏线程解析 → 走一次帧首调度）。</summary>
        private Vector2 ResolveEndpoint(Endpoint endpoint, string note)
        {
            if (endpoint.Selector == null)
            {
                if (!endpoint.HasPoint)
                    throw new BridgeCommandException("invalid_argument", "Endpoint has neither selector nor point.");
                return endpoint.Point;
            }

            Vector2 resolved = default(Vector2);
            bool ok = false;
            m_injector.RunOnFrame(delegate
            {
                resolved = ResolveElementCenter(endpoint.Selector);
                ok = true;
                return null;
            });

            if (!ok)
                throw new BridgeCommandException("not_ready", "Failed to resolve endpoint " + endpoint.Selector);
            return resolved;
        }

        private Vector2 ResolveElementCenter(string selector)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                throw new BridgeCommandException("not_ready", "The UI is not ready yet.");
            if (ScreensManager.IsAnimating)
                throw new BridgeCommandException("screen_busy", "A screen transition is in progress.");

            Widget target = UiInspector.Resolve(root, selector, false, Vector2.Zero);
            Vector2 center = UiInspector.CenterOf(target);
            Widget hit = root.HitTestGlobal(center);
            if (!IsSelfOrDescendant(hit, target))
            {
                throw new BridgeCommandException(
                    "element_occluded",
                    "The element is not clickable right now; blocked by " + UiInspector.ShortName(hit) + ".");
            }

            WidgetInput input = target.Input;
            if (input == null)
                throw new BridgeCommandException("element_missing", "The element has no input surface.");
            m_input = input;
            return center;
        }

        private static bool IsSelfOrDescendant(Widget candidate, Widget ancestor)
        {
            Widget current = candidate;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;
                current = current.ParentWidget;
            }
            return false;
        }

        // ---------------------------------------------------------------- 便捷工具

        public static string ParseButtonName(string name)
        {
            return string.IsNullOrEmpty(name) ? "left" : name.Trim().ToLowerInvariant();
        }

        public static MouseButton ToMouseButton(string name, MouseButton fallback)
        {
            switch (ParseButtonName(name))
            {
                case "left":
                    return MouseButton.Left;
                case "right":
                    return MouseButton.Right;
                case "middle":
                    return MouseButton.Middle;
                default:
                    return fallback;
            }
        }

        /// <summary>等待队列排空（网络侧命令用，保证返回时手势已完成）。</summary>
        public bool WaitUntilIdle(int timeoutMilliseconds)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
            {
                lock (m_gate)
                {
                    if (m_steps.Count == 0 && !m_executing)
                        return true;
                }
                System.Threading.Thread.Sleep(4);
            }
            return false;
        }
    }
}
