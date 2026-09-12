using Engine;
using Engine.Input;
using System;
using System.Collections.Generic;

namespace CmdBridgeMod
{
    /// <summary>
    /// 供其它 Mod 复用的**玩家控制器注入门面**（PlayerAiMod 依赖本 Mod 时使用）。
    ///
    /// 为什么需要它：<see cref="InputInjector"/> 是 internal，只服务本 Mod 的命令路由；
    /// 而依赖本 Mod 的 Mod 需要复用同一套注入实现 —— 白名单字段、帧首时序、UI 点击两帧、
    /// 清 m_mouseDownPoint、释放残留按键这些坑只应该有一份代码。
    ///
    /// 契约：
    ///   · 只暴露**输入层**动作（视角 / 键盘 / 鼠标 / 滚轮 / UI 点击 / 文本），
    ///     不暴露观察接口、命令路由与 TCP 服务器。
    ///   · 所有方法**不抛异常**：返回 false 表示本次动作没有执行
    ///     （未启用 / 参数非法 / 游戏未就绪 / 帧首调度超时），调用方可直接用于状态机守卫。
    ///   · 写入依旧发生在"下一帧帧首"，语义与真人输入一致（见 GameThreadInvoker 注释）。
    ///   · 铁律不变：只写 InputWhitelist 里的输入层成员，绝不触碰生命/背包/方块/位置/时间等游戏状态。
    ///   · 角度单位是**度**（与 CmdBridge 命令一致），不是弧度。
    /// </summary>
    public sealed class CmdBridgeInput
    {
        private readonly InputInjector m_injector;
        private string m_lastFailureMessage;
        private double m_lastFailureTime = double.NegativeInfinity;

        internal CmdBridgeInput(InputInjector injector)
        {
            m_injector = injector;
        }

        /// <summary>注入是否可用（CmdBridge 已加载且 CmdBridge.json 里 EnableInputInjection 为真）。</summary>
        public bool IsAvailable
        {
            get { return m_injector != null && m_injector.Enabled; }
        }

        /// <summary>当前是否有按键/鼠标处于按住状态（用于确认"没有残留按键"）。</summary>
        public bool IsHoldingAnything
        {
            get { return m_injector != null && m_injector.IsHoldingAnything; }
        }

        /// <summary>瞬时转向（度）。人手做不到的角速度，属于"优势"而非特权。</summary>
        public bool Look(float yawDegrees, float pitchDegrees)
        {
            return Execute("Look", () => m_injector.Look(yawDegrees, pitchDegrees));
        }

        public bool LookDelta(float yawDeltaDegrees, float pitchDeltaDegrees)
        {
            return Execute("LookDelta", () => m_injector.LookDelta(yawDeltaDegrees, pitchDeltaDegrees));
        }

        /// <summary>看向世界坐标点（自动求 yaw/pitch）。</summary>
        public bool LookAt(float x, float y, float z)
        {
            return Execute("LookAt", () => m_injector.LookAt(x, y, z));
        }

        /// <summary>按一下某个键（holdMilliseconds = 按住时长，0 = 一帧）。</summary>
        public bool KeyPulse(string keyName, int holdMilliseconds = 60)
        {
            return Execute("KeyPulse", () => m_injector.KeyPulse(keyName, holdMilliseconds));
        }

        /// <summary>按住/松开某个键（跨帧有效，例如前进）。</summary>
        public bool KeyHold(string keyName, bool down)
        {
            return Execute("KeyHold", () => m_injector.KeyHold(keyName, down));
        }

        /// <summary>组合键（例如 Shift + 左键的"特殊点击"路径）。</summary>
        public bool KeyChord(string[] modifierNames, string keyName, int holdMilliseconds = 60)
        {
            return Execute("KeyChord", () => m_injector.KeyChord(modifierNames, keyName, holdMilliseconds));
        }

        /// <summary>输入文本（打字，需要文本框已获得焦点）。</summary>
        public bool TypeText(string text)
        {
            return Execute("TypeText", () => m_injector.TypeText(text));
        }

        /// <summary>鼠标按键：button = "left"/"right"/"middle"，action = "down"/"up"/"click"。</summary>
        public bool MouseAction(string buttonName, string action, int holdMilliseconds = 0)
        {
            return Execute("MouseAction", () => m_injector.MouseAction(buttonName, action, holdMilliseconds));
        }

        /// <summary>滚轮（正 = 向上；用于切快捷栏/列表）。</summary>
        public bool Wheel(int notches)
        {
            return Execute("Wheel", () => m_injector.Wheel(notches));
        }

        /// <summary>引擎内 UI 点击（按控件路径；注入前会二次校验元素是否可点，不可跳级）。</summary>
        public bool UiClick(string selector, int holdMilliseconds = 0)
        {
            return Execute("UiClick", () => m_injector.UiClick(selector, holdMilliseconds, false, 0f, 0f));
        }

        /// <summary>引擎内 UI 点击（按坐标点）。</summary>
        public bool UiClickAt(float x, float y, int holdMilliseconds = 0)
        {
            return Execute("UiClickAt", () => m_injector.UiClick(null, holdMilliseconds, true, x, y));
        }

        // ---------------------------------------------------------------- 焦点策略与共控（CM-2）

        /// <summary>设置焦点策略：auto（跟随真实焦点）/ follow（永远按前台）/ detach（永远脱离）。</summary>
        public bool SetFocusMode(string mode)
        {
            if (!IsAvailable)
                return false;

            FocusMode parsed;
            if (!FocusPolicy.TryParseMode(mode, out parsed))
                return false;

            m_injector.Focus.Mode = parsed;
            return true;
        }

        public string FocusMode
        {
            get { return IsAvailable ? m_injector.Focus.ModeName : null; }
        }

        /// <summary>当前是否"脱离实际鼠标"（游戏在后台时自动进入）。</summary>
        public bool IsDetached
        {
            get { return IsAvailable && m_injector.Focus.IsDetached; }
        }

        /// <summary>当前是否在做"真实输入 || AI 注入"的合并（仅前台）。</summary>
        public bool IsMergingInput
        {
            get
            {
                return IsAvailable && m_injector.Focus.MergeEnabled && !m_injector.Focus.IsDetached;
            }
        }

        /// <summary>视角当前是否归用户（自动模式下：最近有真实鼠标活动）。</summary>
        public bool LookOwnedByUser
        {
            get { return IsAvailable && m_injector.Focus.LookOwnedByUser; }
        }

        /// <summary>固定视角归属：auto / user / ai / shared。</summary>
        public bool SetLookOwner(string owner)
        {
            if (!IsAvailable)
                return false;

            LookOwnerMode parsed;
            if (!FocusPolicy.TryParseLookOwner(owner, out parsed))
                return false;

            m_injector.Focus.LookOwner = parsed;
            return true;
        }

        /// <summary>开关"真实输入与 AI 注入合并"。</summary>
        public bool SetMergeEnabled(bool enabled)
        {
            if (!IsAvailable)
                return false;
            m_injector.Focus.MergeEnabled = enabled;
            return true;
        }

        /// <summary>因视角归用户而被跳过的命令数（用于诊断）。</summary>
        public int SkippedCommands { get; private set; }

        /// <summary>焦点策略状态。</summary>
        public object FocusStatus()
        {
            if (!IsAvailable)
                return null;
            try
            {
                return m_injector.Focus.Describe();
            }
            catch (Exception exception)
            {
                LogFailure("FocusStatus", exception);
                return null;
            }
        }

        // ---------------------------------------------------------------- 热键（CM-3，可复用）

        /// <summary>
        /// 注册一个"帧首判定"的热键：按下沿触发一次，回调在游戏线程执行。
        /// 名字用于注销与状态查询；`keyName` 支持 "Home"/"PageUp"/"PgUp"/"Shift"/"Escape" 等写法。
        /// </summary>
        // ---------------------------------------------------------------- 只读输入快照（动作包录制用）

        /// <summary>
        /// 读一帧「玩家控制器看到的输入」（只读，不改游戏）。动作包录制每帧调一次即可。
        /// 写原始输入字段的活儿仍然只在本 Mod 里做（白名单纪律），别的 Mod 只拿到快照。
        /// 返回 null 表示现在没有可读的玩家（世界没加载/角色不在了）。
        /// </summary>
        public CmdBridgeInputFrame ReadInputFrame()
        {
            try
            {
                return InputSnapshot.ReadCurrent();
            }
            catch (Exception exception)
            {
                return new CmdBridgeInputFrame
                {
                    Error = exception.GetType().Name + ": " + exception.Message
                };
            }
        }

        // ---------------------------------------------------------------- 扩展命令（别的 Mod 挂自己的命令前缀）

        /// <summary>
        /// 注册一条扩展命令（例如 `ai.status`）。**一套通道、一个 token、一个 CLI**：
        /// 上层 Mod 不必改 CmdBridgeMod 的路由代码，也不会各自开一个端口。
        ///
        /// <paramref name="runOnGameThread"/> 默认 true —— 触碰游戏对象/输入的命令必须走游戏线程；
        /// 只有纯计算类命令才应该关掉它（省一次线程切换）。
        /// 同名命令只能有一个注册者；卸载时用 <see cref="UnregisterAllCommands"/> 整批摘掉。
        /// </summary>
        public bool RegisterCommand(string name, CmdBridgeCommandHandler handler,
            string owner = null, bool runOnGameThread = true, string description = null)
        {
            InputInjector injector = m_injector;
            if (injector == null)
                return false;

            string error;
            bool ok = injector.Commands.Register(name, handler, owner, description, runOnGameThread,
                out error);
            if (!ok)
                Log.Warning("[CmdBridge] register command '" + name + "' failed: " + error);
            return ok;
        }

        /// <summary>摘掉一条命令（只有注册者本人能摘；<paramref name="owner"/> 为空表示强制摘）。</summary>
        public bool UnregisterCommand(string name, string owner = null)
        {
            InputInjector injector = m_injector;
            if (injector == null)
                return false;

            string error;
            bool ok = injector.Commands.Unregister(name, owner, out error);
            if (!ok)
                Log.Warning("[CmdBridge] unregister command '" + name + "' failed: " + error);
            return ok;
        }

        /// <summary>按注册者整批摘掉（Mod 卸载时调用）。返回摘掉的条数。</summary>
        public int UnregisterAllCommands(string owner)
        {
            InputInjector injector = m_injector;
            return injector != null ? injector.Commands.UnregisterAll(owner) : 0;
        }

        /// <summary>列出扩展命令名（可按注册者过滤）。</summary>
        public System.Collections.Generic.List<string> ListCommands(string owner = null)
        {
            InputInjector injector = m_injector;
            return injector != null
                ? injector.Commands.Names(owner)
                : new System.Collections.Generic.List<string>();
        }

        /// <summary>扩展命令条数（`cmd.list` 与诊断用）。</summary>
        public int CommandCount
        {
            get
            {
                InputInjector injector = m_injector;
                return injector != null ? injector.Commands.Count : 0;
            }
        }

        public bool RegisterHotkey(string keyName, System.Action handler, string name = null)
        {
            if (!IsAvailable || handler == null)
                return false;

            Key key;
            if (!HotkeyRegistry.TryParseKey(keyName, out key))
            {
                Log.Warning("[CmdBridge] RegisterHotkey: unknown key '" + keyName + "'.");
                return false;
            }

            string error = m_injector.Hotkeys.Register(name ?? keyName, key, handler);
            if (error != null)
            {
                Log.Warning("[CmdBridge] RegisterHotkey failed: " + error);
                return false;
            }
            return true;
        }

        public bool UnregisterHotkey(string name)
        {
            return IsAvailable && m_injector.Hotkeys.Unregister(name);
        }

        /// <summary>热键状态（注册表内容、触发次数）。</summary>
        public object HotkeyStatus()
        {
            return IsAvailable ? m_injector.Hotkeys.Describe() : null;
        }

        // ---------------------------------------------------------------- 帧首调度（可复用）

        /// <summary>
        /// 把一个动作排到"下一帧帧首"执行（游戏线程）。
        ///
        /// 为什么需要：`Dispatcher.Dispatch` 在**主线程调用会立即执行**，只有后台线程调用才入队到
        /// 下一帧 `Dispatcher.BeforeFrame()`；而 `Keyboard/Mouse.AfterFrame()` 会在帧末清空 downOnce 数组，
        /// 所以"按下脉冲"只能写在帧首 —— 写在帧尾（Frame.Update 里）会被同帧清掉。
        /// 本方法内部用一个后台线程派发，因此调用方在哪个线程都能拿到"帧首执行"的语义。
        /// </summary>
        public bool PostToFrameStart(System.Action action)
        {
            if (!IsAvailable || action == null)
                return false;

            try
            {
                m_injector.Pump.Enqueue(action);
                return true;
            }
            catch (Exception exception)
            {
                LogFailure("PostToFrameStart", exception);
                return false;
            }
        }

        /// <summary>帧首泵是否在运行（游戏是否已在出帧）。</summary>
        public bool FrameStartPumpRunning
        {
            get { return IsAvailable && m_injector.Pump.IsRunning; }
        }

        /// <summary>还没执行的帧首动作数 / 已执行总数 / 因队列满被丢弃数。</summary>
        public int FrameStartPending
        {
            get { return IsAvailable ? m_injector.Pump.PendingCount : 0; }
        }

        public int FrameStartExecuted
        {
            get { return IsAvailable ? m_injector.Pump.ActionsExecuted : 0; }
        }

        public int FrameStartDropped
        {
            get { return IsAvailable ? m_injector.Pump.ActionsDropped : 0; }
        }

        // ---------------------------------------------------------------- 虚拟 UI 鼠标会话（CM-1）

        /// <summary>
        /// 开始"AI 占用鼠标"会话：之后所有 UI 操作走引擎内软光标，**物理鼠标完全不动**。
        /// mask=true 时每帧强制鼠标按键状态，屏蔽用户真实点击（避免与 AI 拖拽互相污染）。
        /// </summary>
        public bool UiSessionBegin(bool mask = false)
        {
            return RunSession(() => m_injector.Session.Begin(mask), 2000);
        }

        /// <summary>结束会话：释放按键、清掉残留按下起点、交回用户鼠标。</summary>
        public bool UiSessionEnd()
        {
            return RunSession(() => m_injector.Session.End(), 2000);
        }

        /// <summary>会话状态（是否激活、软光标位置、队列长度、错误等）。</summary>
        public object UiSessionStatus()
        {
            if (!IsAvailable)
                return null;
            try
            {
                return m_injector.Session.Describe();
            }
            catch (Exception exception)
            {
                LogFailure("UiSessionStatus", exception);
                return null;
            }
        }

        /// <summary>软光标移动到元素中心。</summary>
        public bool UiCursorAt(string selector)
        {
            return RunSession(() => m_injector.Session.MoveToElement(selector, 1), 2000);
        }

        /// <summary>软光标移动到客户区坐标。</summary>
        public bool UiMoveTo(float x, float y, int steps = 1)
        {
            return RunSession(() => m_injector.Session.MoveTo(new Vector2(x, y), steps), 3000);
        }

        public bool UiPress(string button = "left")
        {
            return RunSession(() => m_injector.Session.Press(
                UiMouseSession.ToMouseButton(button, MouseButton.Left)), 2000);
        }

        public bool UiRelease(string button = "left")
        {
            return RunSession(() => m_injector.Session.Release(
                UiMouseSession.ToMouseButton(button, MouseButton.Left)), 2000);
        }

        /// <summary>软光标点击（不需要窗口在前台）。</summary>
        public bool UiClickElement(string selector)
        {
            return RunSession(() => m_injector.Session.Click(selector), 3000);
        }

        /// <summary>
        /// **非阻塞**的软光标点击（按控件路径）：排完就返回，不等待手势完成。
        ///
        /// 为什么要它：动作包回放在**游戏线程的帧首 tick** 里跑，那里等不了帧
        /// （等＝把游戏线程自己卡住）。而且直接注入"按下 + 抬起"会让两者落在同一帧，
        /// 引擎的 `WidgetInput` 派生不出 Click（实测：命令返回成功、界面纹丝不动）。
        /// 这里改走 CM-1 会话：会话把步骤按**一步一帧**推进，按下与抬起天然分开，
        /// 点击做完后会话自己收尾（释放按键 + 清残留 + 交回光标）。
        /// </summary>
        public bool UiQueueClick(string selector)
        {
            if (!IsAvailable || string.IsNullOrEmpty(selector))
                return false;
            try
            {
                m_injector.Session.Begin(false);
                m_injector.Session.Click(selector);
                m_injector.Session.End();
                return true;
            }
            catch (Exception exception)
            {
                LogFailure("UiQueueClick", exception);
                return false;
            }
        }

        /// <summary>**非阻塞**的软光标点击（按客户区坐标：列表行这类没有控件的目标）。</summary>
        public bool UiQueueClickAt(float x, float y)
        {
            if (!IsAvailable)
                return false;
            try
            {
                m_injector.Session.Begin(false);
                m_injector.Session.MoveTo(new Vector2(x, y), 1);
                m_injector.Session.Press(MouseButton.Left);
                m_injector.Session.Release(MouseButton.Left);
                m_injector.Session.End();
                return true;
            }
            catch (Exception exception)
            {
                LogFailure("UiQueueClickAt", exception);
                return false;
            }
        }

        /// <summary>
        /// **非阻塞**的语义点击（UI 服务版，行为树/回放用）：
        /// 目标是"控件名 / 路径 / `list:列表@文字` / `list:列表#行号`"，坐标在点击那一刻现算。
        ///
        /// `mode`：`direct`（默认，单帧合成 Tap+Click，引擎自己的控件逻辑照常跑）/
        /// `input`（老的软光标多帧会话）。返回 true = 已受理（不是"已经点到了"）。
        /// </summary>
        public bool UiClickTarget(string target, string mode = "direct")
        {
            string ignored;
            return UiClickTarget(target, mode, out ignored);
        }

        /// <summary>
        /// 同上，但**如实回报"这次到底点到没有"**：在游戏线程上调用（行为树 tick）时，
        /// 解析就地进行 —— 目标不在（切场动画中间、列表还没填）时返回 false 并把原因写进
        /// <paramref name="error"/>，调用方可以据此重试（这正是回放里"没点成就重试"的依据）。
        /// </summary>
        public bool UiClickTarget(string target, string mode, out string error)
        {
            error = null;
            if (!IsAvailable || string.IsNullOrEmpty(target))
            {
                error = IsAvailable ? "a UI target is required" : "CmdBridgeMod is not available";
                return false;
            }
            try
            {
                return m_injector.UiClickTargetCore(target, mode, out error);
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                LogFailure("UiClickTarget", exception);
                return false;
            }
        }

        /// <summary>同步定位一个语义目标（编辑器/命令面用）：现算坐标 + 可点性。</summary>
        public Dictionary<string, object> UiLocate(string target, bool mark = false)
        {
            if (!IsAvailable || string.IsNullOrEmpty(target))
                return null;
            try
            {
                return m_injector.Ui.Locate(target, mark, UiMarker.DefaultSeconds,
                    UiMarker.DefaultDiameterPixels);
            }
            catch (Exception exception)
            {
                LogFailure("UiLocate", exception);
                return null;
            }
        }

        /// <summary>
        /// **非阻塞**的软光标点击：点列表里的某一行（按序号 `row` 或文字 `text` 找行）。
        /// 保留给"必须走多帧会话"的场合；新代码优先用 <see cref="UiClickTarget"/>。
        ///
        /// 为什么要它（用户明确要求）：列表行在录制时只能记坐标，而**坐标会随窗口尺寸/UI 缩放失效**
        /// —— 改过窗口再回放就点不动。这里记的是"哪个列表的第几行 / 哪一行文字"，
        /// 坐标在**点击这一刻**由 `UiInspector.TryResolveListRow` 现算，与窗口尺寸无关。
        /// </summary>
        public bool UiQueueClickListRow(string selector, int rowIndex, string rowText)
        {
            if (!IsAvailable || string.IsNullOrEmpty(selector))
                return false;
            try
            {
                Vector2? point = m_injector.ResolveListRowPoint(selector, rowIndex, rowText);
                if (!point.HasValue)
                    return false;

                m_injector.Session.Begin(false);
                m_injector.Session.MoveTo(point.Value, 1);
                m_injector.Session.Press(MouseButton.Left);
                m_injector.Session.Release(MouseButton.Left);
                m_injector.Session.End();
                return true;
            }
            catch (Exception exception)
            {
                LogFailure("UiQueueClickListRow", exception);
                return false;
            }
        }

        public bool UiRightClickElement(string selector)
        {
            return RunSession(() => m_injector.Session.RightClick(selector), 3000);
        }

        /// <summary>Shift+左键（引擎侧等价于 SpecialClick）。</summary>
        public bool UiShiftClickElement(string selector)
        {
            return RunSession(() => m_injector.Session.ShiftClick(selector), 3000);
        }

        /// <summary>
        /// 拖拽（槽位搬运、滑条、列表）。holdMs ≥ 500 时是"长按后拖动"，
        /// 引擎会按 SingleItem 模式只搬 1 个 —— 也就是"分离物品"。
        /// </summary>
        public bool UiDrag(string fromSelector, string toSelector, int steps = 8, int holdMs = 0)
        {
            return RunSession(() => m_injector.Session.Drag(
                UiMouseSession.Endpoint.At(fromSelector),
                UiMouseSession.Endpoint.At(toSelector),
                steps, holdMs), 6000);
        }

        /// <summary>按坐标拖拽（不知道元素选择器时用）。</summary>
        public bool UiDragAt(float x1, float y1, float x2, float y2, int steps = 8, int holdMs = 0)
        {
            return RunSession(() => m_injector.Session.Drag(
                UiMouseSession.Endpoint.At(new Vector2(x1, y1)),
                UiMouseSession.Endpoint.At(new Vector2(x2, y2)),
                steps, holdMs), 6000);
        }

        /// <summary>分离物品：长按源槽位后拖到目标槽位（只搬 1 个）。</summary>
        public bool UiSplitItem(string fromSelector, string toSelector, int holdMs = 600)
        {
            return UiDrag(fromSelector, toSelector, 8, Math.Max(holdMs, 500));
        }

        /// <summary>
        /// 例行释放：**只放掉本 Mod 注入并按住**的键/鼠标。
        /// 与 <see cref="ReleaseAll"/> 的区别见注入器里的注释 —— 后者会把真实鼠标的按下沿一起清掉，
        /// 用在"每帧都会走到的例行释放"上会让玩家点不动按钮（实测踩过）。
        /// </summary>
        public bool ReleaseInjectedInput()
        {
            return Execute("ReleaseInjectedInput", () => m_injector.ReleaseInjectedOnly());
        }

        /// <summary>释放全部按键与鼠标（走帧首队列）。</summary>
        public bool ReleaseAll()
        {
            return Execute("ReleaseAll", () => m_injector.ReleaseAll());
        }

        /// <summary>立即释放（不等帧首；用于卸载/停用，避免游戏停止推进时残留按住状态）。</summary>
        public void ReleaseAllImmediate()
        {
            if (m_injector != null)
                m_injector.ReleaseAllDirect();
        }

        /// <summary>执行一条会话动作并等待其在帧首逐帧完成。</summary>
        private bool RunSession(Func<Dictionary<string, object>> action, int timeoutMilliseconds)
        {
            if (!IsAvailable)
                return false;

            try
            {
                action();
                return m_injector.Session.WaitUntilIdle(timeoutMilliseconds);
            }
            catch (Exception exception)
            {
                LogFailure("UiSession", exception);
                return false;
            }
        }

        /// <summary>失败限流日志：新原因或距上次超过 2 秒才记一条。</summary>
        private void LogFailure(string name, Exception exception)
        {
            string message = exception.GetType().Name + ": " + exception.Message;
            double now = Time.RealTime;
            if (!string.Equals(message, m_lastFailureMessage, StringComparison.Ordinal)
                || now - m_lastFailureTime > 2.0)
            {
                m_lastFailureMessage = message;
                m_lastFailureTime = now;
                Log.Warning("[CmdBridge] input facade '" + name + "' failed: " + message);
            }
        }

        private bool Execute(string name, Func<object> action)
        {
            if (!IsAvailable)
                return false;

            try
            {
                object result = action();

                // 契约：返回 false 表示"这次没执行"。注入器以 `skipped` 字段表达"被仲裁跳过"
                // （例如视角归用户），这里如实转成 false，调用方不会误以为动作已生效。
                var dictionary = result as Dictionary<string, object>;
                if (dictionary != null && dictionary.ContainsKey("skipped"))
                {
                    SkippedCommands++;
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                LogFailure(name, exception);
                return false;
            }
        }
    }
}
