using Engine;
using Engine.Input;
using SuAPI;
using System;
using System.Collections.Generic;
using TkKeyboard = OpenTK.Input.Keyboard;
using TkKeyboardState = OpenTK.Input.KeyboardState;
using TkKey = OpenTK.Input.Key;
using TkMouse = OpenTK.Input.Mouse;
using TkMouseButton = OpenTK.Input.MouseButton;
using TkMouseState = OpenTK.Input.MouseState;

namespace CmdBridgeMod
{
    /// <summary>焦点策略：前台跟随引擎；失焦"脱离实际鼠标"。</summary>
    internal enum FocusMode
    {
        /// <summary>自动：跟随真实焦点（前台 Follow / 失焦 Detach）。</summary>
        Auto,
        /// <summary>永远按"前台"处理：不干预引擎焦点，真实输入与注入合并。</summary>
        Follow,
        /// <summary>永远按"脱离"处理：引擎以为窗口活跃，真实鼠标被切断。</summary>
        Detach
    }

    /// <summary>视角归属：谁在控制视角。</summary>
    internal enum LookOwnerMode
    {
        /// <summary>自动：最近有真实鼠标活动时归用户，停手一会儿后归 AI。</summary>
        Auto,
        User,
        Ai,
        /// <summary>共享：用户增量与 AI 绝对朝向都生效。</summary>
        Shared
    }

    /// <summary>
    /// 虚拟焦点与共控合并 —— 让"游戏放后台 / 多开 AI / 真人与 AI 同时操作"成立，
    /// 全部在 Mod 侧实现，**不改游戏源码**。
    ///
    /// 两套策略（按真实焦点自动切换，也可强制）：
    ///
    /// **前台（Follow）**
    ///   · 引擎焦点保持自然状态，游戏自己的鼠标逻辑（隐藏/捕获光标）照旧。
    ///   · 每帧把键盘/鼠标按键合并成 `真实 || AI 注入`，解决"两个输入源互相掐断"：
    ///     AI 松手不会打断你按住不放的键，你的松手也不会中断 AI 的动作。
    ///   · 视角按"最近真实鼠标活动"仲裁（`look.owner`）：你一动鼠标视角立刻归你。
    ///
    /// 真实焦点的判定（**关键**）：读 `Window.m_gameWindow.Focused`（OpenTK 的窗口焦点），
    /// 而不是 `Window.IsActive`，也不是 `Window.Activated` 事件 —— 因为本策略在失焦时会把
    /// `Window.m_state` 强制成 Active，而引擎的 `Activated` 只在 `Inactive→Active` 跳变时触发
    /// （`Window.cs:340-352`）。用事件判定会永久卡在"以为失焦"的状态里：
    /// 视角转不动（每帧清掉鼠标基准 → MouseMovement 恒 0）+ 光标不隐藏（每帧强制可见）。
    /// 读不到 `m_gameWindow` 时**不启用脱离策略**：宁可没有虚拟焦点，也绝不弄坏正常前台体验。
    ///
    /// **失焦（Detach）**
    ///   · 置 `Window.m_state = Active`，让 `ComponentInput` 不再丢弃输入
    ///     （否则 `ComponentInput.cs:91-94` 会把整个 PlayerInput 置空）。
    ///   · 帧末把 `Mouse.IsMouseVisible` 置真：`Widget.cs:768-770` 每帧会按"界面是否需要光标"重写它，
    ///     而 `Mouse.BeforeFrame()` 只在 `Window.IsActive` 时才 `CursorVisible = IsMouseVisible`
    ///     —— 不覆盖就会**全局隐藏系统光标**，坑到你在别的软件里用鼠标。
    ///   · 帧首切断真实鼠标：置空 `m_lastMousePosition` / `m_lastMouseWheelValue`，
    ///     于是 `Mouse.BeforeFrame()` 根本不去算增量（`Mouse.cs:48-70`），并把派生量清零
    ///     （否则"失焦前最后一帧的残留增量"会被当成每帧输入反复施加）。
    ///   · 此时**不合并**真实输入：Windows 上 `OpenTK.Input.Keyboard.GetState()` 是**全局**键盘状态，
    ///     失焦时合并会让"你在别的软件打字"变成角色走动。
    ///
    /// 帧首执行由 <see cref="FrameStartPump"/> 保证（`Dispatcher.Dispatch` 在主线程会立即执行）。
    /// </summary>
    internal sealed class FocusPolicy
    {
        private readonly InputInjector m_injector;

        private FocusMode m_mode = FocusMode.Auto;
        private LookOwnerMode m_lookOwner = LookOwnerMode.Auto;
        private bool m_mergeEnabled = true;
        private float m_lookHoldSeconds = 0.4f;

        private bool m_realFocus;
        private int? m_lastRealMouseX;
        private int? m_lastRealMouseY;
        private double m_userLookUntil = double.NegativeInfinity;

        private object m_activeWindowState;
        private object m_inactiveWindowState;
        private bool m_windowStateUnavailable;
        private string m_windowStateError;

        private OpenTK.GameWindow m_gameWindow;
        private bool m_realFocusSourceUnavailable;
        private string m_realFocusError;
        private bool m_detachedLastFrame;

        // OS 前台窗口探测（只读；Windows 之外退回 OpenTK 的 Focused）
        private bool m_foregroundProbeTried;
        private ForegroundWindowProbe m_foregroundProbe;
        private string m_foregroundError;
        private bool m_foregroundSourceAvailable;
        private bool m_foregroundIsGame;
        private string m_foregroundTitle;
        private bool m_foregroundWasForeign;
        private int m_stealCount;
        private string m_lastStealNote;
        private string m_lastForeignForeground;
        private IntPtr m_lastForeignHandle;
        private int m_mergeFixups;
        private int m_framesApplied;
        private string m_lastNote;
        private string m_lastError;

        public FocusPolicy(InputInjector injector)
        {
            if (injector == null)
                throw new ArgumentNullException(nameof(injector));
            m_injector = injector;
            m_realFocus = RefreshRealFocusFromWindow();
        }

        // ---------------------------------------------------------------- 配置与查询

        public FocusMode Mode
        {
            get { return m_mode; }
            set { m_mode = value; }
        }

        public LookOwnerMode LookOwner
        {
            get { return m_lookOwner; }
            set { m_lookOwner = value; }
        }

        public bool MergeEnabled
        {
            get { return m_mergeEnabled; }
            set { m_mergeEnabled = value; }
        }

        public float LookHoldSeconds
        {
            get { return m_lookHoldSeconds; }
            set { m_lookHoldSeconds = Math.Max(0f, value); }
        }

        /// <summary>真实焦点（由 Window.Activated/Deactivated 事件跟踪，不受我们强制值影响）。</summary>
        public bool RealFocus
        {
            get { return m_realFocus; }
        }

        /// <summary>当前是否处于"脱离实际鼠标"模式。</summary>
        public bool IsDetached
        {
            get
            {
                if (m_windowStateUnavailable)
                    return false; // 无法操纵引擎焦点时如实报告"没脱离"，而不是假装成功
                if (m_realFocusSourceUnavailable)
                    return false; // 不知道真实焦点时绝不脱离（否则会卡住前台体验）
                return m_mode == FocusMode.Detach
                    || (m_mode == FocusMode.Auto && !m_realFocus);
            }
        }

        /// <summary>视角是否归用户（自动模式下：最近有真实鼠标活动）。</summary>
        public bool LookOwnedByUser
        {
            get
            {
                if (m_lookOwner == LookOwnerMode.User)
                    return true;
                if (m_lookOwner == LookOwnerMode.Ai || m_lookOwner == LookOwnerMode.Shared)
                    return false;
                return Time.RealTime < m_userLookUntil;
            }
        }

        public string ModeName
        {
            get
            {
                switch (m_mode)
                {
                    case FocusMode.Detach: return "detach";
                    case FocusMode.Follow: return "follow";
                    default: return "auto";
                }
            }
        }

        public string LookOwnerName
        {
            get
            {
                switch (m_lookOwner)
                {
                    case LookOwnerMode.User: return "user";
                    case LookOwnerMode.Ai: return "ai";
                    case LookOwnerMode.Shared: return "shared";
                    default: return "auto";
                }
            }
        }

        public static bool TryParseMode(string name, out FocusMode mode)
        {
            mode = FocusMode.Auto;
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "auto":
                    mode = FocusMode.Auto;
                    return true;
                case "follow":
                case "attach":
                case "attached":
                    mode = FocusMode.Follow;
                    return true;
                case "detach":
                case "detached":
                case "background":
                    mode = FocusMode.Detach;
                    return true;
                default:
                    return false;
            }
        }

        public static bool TryParseLookOwner(string name, out LookOwnerMode owner)
        {
            owner = LookOwnerMode.Auto;
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "auto":
                    owner = LookOwnerMode.Auto;
                    return true;
                case "user":
                case "player":
                    owner = LookOwnerMode.User;
                    return true;
                case "ai":
                case "bot":
                    owner = LookOwnerMode.Ai;
                    return true;
                case "shared":
                case "both":
                    owner = LookOwnerMode.Shared;
                    return true;
                default:
                    return false;
            }
        }

        public Dictionary<string, object> Describe()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["mode"] = ModeName,
                ["realFocus"] = m_realFocus,
                ["engineThinksActive"] = SafeWindowActive(),
                ["detached"] = IsDetached,
                ["mergeRealAndInjected"] = m_mergeEnabled && !IsDetached,
                ["lookOwner"] = LookOwnerName,
                ["lookOwnedByUser"] = LookOwnedByUser,
                ["lookHoldSeconds"] = m_lookHoldSeconds,
                ["framesApplied"] = m_framesApplied,
                ["mergeFixups"] = m_mergeFixups,
                ["virtualFocusAvailable"] = !m_windowStateUnavailable,
                ["realFocusSource"] = RealFocusSource,
                ["foregroundIsGame"] = m_foregroundSourceAvailable ? (object)m_foregroundIsGame : null,
                ["foregroundTitle"] = m_foregroundTitle,
                ["lastForeignForeground"] = m_lastForeignForeground,
                ["foregroundStealCount"] = m_stealCount,
                ["foregroundStealNote"] = m_lastStealNote,
                ["foregroundError"] = m_foregroundError,
                ["cursorGuarded"] = IsDetached || !m_realFocus,
                ["windowStateError"] = m_windowStateError,
                ["lastNote"] = m_lastNote,
                ["lastError"] = m_lastError
            };
        }

        // ---------------------------------------------------------------- 焦点事件

        /// <summary>
        /// 由 Window.Activated / Window.Deactivated 调用。
        /// 只是**辅助信号**：失焦虚拟焦点期间我们会把 `m_state` 强制成 Active，
        /// 于是引擎的 Activated 不会随真实焦点恢复而触发（`Window.cs:340-352`），
        /// 所以权威来源是 <see cref="RefreshRealFocusFromWindow"/>。
        /// </summary>
        public void NotifyFocus(bool active)
        {
            // 两种情况都以 OS 真相为准；事件只是"提前一帧"的提示（帧首本来也会刷一次）。
            m_realFocus = RefreshRealFocusFromWindow();
        }

        // ---------------------------------------------------------------- 每帧

        /// <summary>帧首（游戏线程，早于 Keyboard/Mouse.BeforeFrame 与整个帧体）。</summary>
        public void ApplyFrameStart()
        {
            try
            {
                m_framesApplied++;

                // 真实焦点以 OS 为准（我们写 m_state 不会影响它）
                m_realFocus = RefreshRealFocusFromWindow();

                UpdateRealMouseActivity();

                bool detached = IsDetached;
                if (detached)
                {
                    ForceEngineActive();
                    CutRealMouse();
                }
                else
                {
                    RestoreEngineFocus();
                    if (m_mergeEnabled)
                        MergeInjectedWithReal();
                }

                if (detached != m_detachedLastFrame)
                {
                    m_detachedLastFrame = detached;
                    m_lastNote = detached
                        ? "detached: engine treats the window as active, real mouse is cut off"
                        : "attached: engine focus is natural, real input merged with injected";
                    Log.Information("[CmdBridge] focus policy -> " + m_lastNote);
                }
            }
            catch (Exception exception)
            {
                m_lastError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        /// <summary>
        /// 帧末（游戏线程，在控件树 Update 之后）：此时 `Widget.cs` 刚按"界面是否需要光标"写过
        /// `Mouse.IsMouseVisible`，脱离模式下必须覆盖它，否则下一帧 `Mouse.BeforeFrame()` 会隐藏系统光标。
        /// </summary>
        public void ApplyFrameEnd()
        {
            // 失焦（含"被别的应用盖住"）时**绝不隐藏/抓取系统光标**：
            // 否则用户在别的应用里干活，鼠标会被游戏吃掉 —— 表现就是
            // "游戏在后台却还能控制我的键鼠"，而他只能按 Win 才能把焦点抢回来。
            // 脱离模式本来就要这么做；这里把"真实失焦"也纳入同一条规则。
            bool guardCursor = IsDetached || !m_realFocus;
            if (!guardCursor)
                return;
            try
            {
                Mouse.IsMouseVisible = true;
            }
            catch (Exception exception)
            {
                m_lastError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        /// <summary>把引擎焦点恢复成真实值（卸载/停用时用）。</summary>
        public void RestoreNaturalFocus()
        {
            try
            {
                SetWindowState(m_realFocus ? m_activeWindowState : m_inactiveWindowState);
            }
            catch (Exception exception)
            {
                m_lastError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        // ---------------------------------------------------------------- 内部实现

        /// <summary>
        /// 读 OS 层的真实焦点（`Window.m_gameWindow.Focused`）。
        /// 读不到就标记为不可用，并让脱离策略整体停用 —— 绝不猜。
        /// </summary>
        private bool RefreshRealFocusFromWindow()
        {
            try
            {
                if (m_gameWindow == null)
                {
                    object value = Fields.GetStaticField(typeof(Window), InputWhitelist.WindowGameWindow);
                    m_gameWindow = value as OpenTK.GameWindow;
                    if (m_gameWindow == null)
                    {
                        m_realFocusSourceUnavailable = true;
                        m_realFocusError = "Window.m_gameWindow is not an OpenTK.GameWindow"
                            + (value == null ? " (null)" : " (" + value.GetType().Name + ")");
                        Log.Warning("[CmdBridge] virtual focus disabled: " + m_realFocusError);
                        return SafeWindowActive();
                    }
                }

                m_realFocusSourceUnavailable = false;
                m_realFocusError = null;

                // **OS 前台窗口**才是权威：OpenTK 的 `Focused` 可能滞后/过时
                // （用户实测：游戏被别的应用盖住后，"还没失焦"，键鼠仍然进游戏）。
                // 有了前台句柄我们才能说清"现在到底谁在前台"，也才能发现"游戏被拉到前台"。
                bool? foregroundIsGame = RefreshForegroundWindow();
                if (foregroundIsGame.HasValue)
                {
                    WatchForFocusSteal(foregroundIsGame.Value);
                    return foregroundIsGame.Value;
                }

                return m_gameWindow.Focused;
            }
            catch (Exception exception)
            {
                m_realFocusSourceUnavailable = true;
                m_realFocusError = exception.GetType().Name + ": " + exception.Message;
                Log.Warning("[CmdBridge] virtual focus disabled: " + m_realFocusError);
                return SafeWindowActive();
            }
        }

        /// <summary>真实焦点的来源（写进 focus.status，出问题时一眼能看出判据换没换）。</summary>
        public string RealFocusSource
        {
            get
            {
                if (m_realFocusSourceUnavailable)
                    return "unavailable: " + (m_realFocusError ?? "?");
                return m_foregroundSourceAvailable ? ForegroundSourceName : "gameWindow.Focused";
            }
        }

        private const string ForegroundSourceName = "foregroundWindow";

        /// <summary>
        /// 读 OS 前台窗口并与游戏窗口比较。**只读**，不碰任何窗口状态。
        /// 非 Windows（或 API 不可用）时返回 null，调用方退回 OpenTK 的 `Focused`。
        /// </summary>
        private bool? RefreshForegroundWindow()
        {
            if (!m_foregroundProbeTried)
            {
                m_foregroundProbeTried = true;
                try
                {
                    if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                            System.Runtime.InteropServices.OSPlatform.Windows))
                    {
                        m_foregroundProbe = new ForegroundWindowProbe();
                    }
                }
                catch (Exception exception)
                {
                    m_foregroundProbe = null;
                    m_foregroundError = exception.GetType().Name + ": " + exception.Message;
                }
            }

            if (m_foregroundProbe == null)
                return null;

            try
            {
                IntPtr foreground = m_foregroundProbe.Foreground();
                if (foreground == IntPtr.Zero)
                    return null;

                m_foregroundTitle = m_foregroundProbe.Title(foreground) ?? string.Empty;
                bool isGame = m_foregroundProbe.IsOurProcess(foreground);
                m_foregroundSourceAvailable = true;
                m_foregroundIsGame = isGame;

                if (!isGame && !string.IsNullOrEmpty(m_foregroundTitle))
                {
                    m_lastForeignForeground = m_foregroundTitle;
                    m_lastForeignHandle = foreground;
                }
                return isGame;
            }
            catch (Exception exception)
            {
                m_foregroundSourceAvailable = false;
                m_foregroundError = exception.GetType().Name + ": " + exception.Message;
                return null;
            }
        }

        /// <summary>
        /// 看门狗：**用户本来在别的应用里**，前台却被换成了游戏 —— 记一条，供事后追责/排查。
        /// 只记录、不改窗口：我们绝不自己去抢焦点，也不替游戏"抢回来"。
        /// </summary>
        private void WatchForFocusSteal(bool foregroundIsGame)
        {
            if (!foregroundIsGame)
            {
                m_foregroundWasForeign = true;
                return;
            }

            if (!m_foregroundWasForeign || m_lastForeignHandle == IntPtr.Zero)
                return;

            m_foregroundWasForeign = false;

            // 那个"别的应用"已经关掉了 → 前台自然轮到别人（常常就是游戏），这不是抢焦点。
            // 实测踩过两次假警报：探针窗口一关；以及游戏刚启动时我们还没见过任何别的窗口。
            bool alive = m_foregroundProbe != null && m_foregroundProbe.IsAlive(m_lastForeignHandle);
            m_lastForeignHandle = IntPtr.Zero;
            if (!alive)
                return;

            m_stealCount++;
            m_lastStealNote = "the game window took the foreground while '"
                + (m_lastForeignForeground ?? "another app") + "' had it"
                + (m_injector != null && m_injector.IsHoldingAnything
                    ? " (an injection was in flight)" : " (no injection in flight)")
                + " — we never call Activate/SetForegroundWindow; check the game/OS side.";
            Log.Warning("[CmdBridge] " + m_lastStealNote);
        }

        private static bool SafeWindowActive()
        {
            try
            {
                return Window.IsActive;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private IModParentField Fields
        {
            get { return ModManager.Instance.ModParentField; }
        }

        private bool EnsureWindowStates()
        {
            if (m_windowStateUnavailable)
                return false;
            if (m_activeWindowState != null && m_inactiveWindowState != null)
                return true;

            try
            {
                object current = Fields.GetStaticField(typeof(Window), InputWhitelist.WindowState);
                if (current == null)
                {
                    m_windowStateUnavailable = true;
                    m_windowStateError = "Window.m_state is not readable.";
                    return false;
                }

                Type stateType = current.GetType();
                m_activeWindowState = Enum.Parse(stateType, "Active");
                m_inactiveWindowState = Enum.Parse(stateType, "Inactive");
                return true;
            }
            catch (Exception exception)
            {
                m_windowStateUnavailable = true;
                m_windowStateError = exception.GetType().Name + ": " + exception.Message;
                Log.Warning("[CmdBridge] virtual focus unavailable: " + m_windowStateError);
                return false;
            }
        }

        private void ForceEngineActive()
        {
            if (!EnsureWindowStates())
                return;
            SetWindowState(m_activeWindowState);
        }

        private void RestoreEngineFocus()
        {
            if (!EnsureWindowStates())
                return;
            SetWindowState(m_realFocus ? m_activeWindowState : m_inactiveWindowState);
        }

        private void SetWindowState(object state)
        {
            if (state == null)
                return;

            try
            {
                object current = Fields.GetStaticField(typeof(Window), InputWhitelist.WindowState);
                if (Equals(current, state))
                    return; // 已是目标值：不重复写
                Fields.ModifyStaticField(typeof(Window), InputWhitelist.WindowState, state);
            }
            catch (Exception exception)
            {
                m_lastError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        /// <summary>切断真实鼠标：置空上帧位置/滚轮 + 清零派生量。</summary>
        private void CutRealMouse()
        {
            try
            {
                Fields.ModifyStaticField(typeof(Mouse), InputWhitelist.MouseLastPosition, null);
                Fields.ModifyStaticField(typeof(Mouse), InputWhitelist.MouseLastWheelValue, null);
                Fields.ModifyStaticField(typeof(Mouse), InputWhitelist.MouseMovement, default(Point2));
                Fields.ModifyStaticField(typeof(Mouse), InputWhitelist.MouseWheelMovement, 0);
            }
            catch (Exception exception)
            {
                m_lastError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        /// <summary>前台共控：把键盘/鼠标按键合并成"真实 || AI 注入"。</summary>
        private void MergeInjectedWithReal()
        {
            try
            {
                bool[] keysDown = Fields.GetStaticField<bool[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardDownArray);
                if (keysDown != null)
                {
                    TkKeyboardState keyboard = TkKeyboard.GetState();
                    EnsureKeyMap(keysDown.Length);
                    for (int i = 0; i < keysDown.Length; i++)
                    {
                        bool expected = m_injector.IsKeyHeldByInjection(i) || IsRealKeyDown(keyboard, i);
                        if (keysDown[i] != expected)
                        {
                            keysDown[i] = expected;
                            m_mergeFixups++;
                        }
                    }
                }

                bool[] buttonsDown = Fields.GetStaticField<bool[]>(
                    typeof(Mouse), InputWhitelist.MouseDownArray);
                if (buttonsDown != null)
                {
                    TkMouseState mouse = TkMouse.GetState();
                    EnsureButtonMap(buttonsDown.Length);
                    for (int i = 0; i < buttonsDown.Length; i++)
                    {
                        bool expected = m_injector.IsMouseButtonHeldByInjection(i) || IsRealButtonDown(mouse, i);
                        if (buttonsDown[i] != expected)
                        {
                            buttonsDown[i] = expected;
                            m_mergeFixups++;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                m_lastError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        /// <summary>记录真实鼠标活动（用于视角仲裁）：OS 光标移动量超过阈值即认为"用户在动鼠标"。</summary>
        private void UpdateRealMouseActivity()
        {
            try
            {
                TkMouseState mouse = TkMouse.GetState();
                if (m_lastRealMouseX.HasValue && m_lastRealMouseY.HasValue)
                {
                    int dx = Math.Abs(mouse.X - m_lastRealMouseX.Value);
                    int dy = Math.Abs(mouse.Y - m_lastRealMouseY.Value);
                    if (dx + dy >= 2)
                        m_userLookUntil = Time.RealTime + m_lookHoldSeconds;
                }
                m_lastRealMouseX = mouse.X;
                m_lastRealMouseY = mouse.Y;
            }
            catch (Exception exception)
            {
                m_lastError = exception.GetType().Name + ": " + exception.Message;
            }
        }

        // ---------------------------------------------------------------- Engine.Key ↔ OpenTK.Input.Key 名称映射

        private TkKey[] m_tkKeyMap;
        private bool[] m_tkKeyValid;
        private TkMouseButton[] m_tkButtonMap;
        private bool[] m_tkButtonValid;

        /// <summary>
        /// 不维护手写映射表：`Engine/Engine/Input/Keyboard.cs` 本身就是按**同名**把
        /// `OpenTK.Input.Key` 转成 `Engine.Key` 的（如 `OpenTK.Input.Key.W => Key.W`），
        /// 所以按枚举名反查即可；对不上名字的键一律当作"真实未按下"（安全）。
        /// </summary>
        private void EnsureKeyMap(int count)
        {
            if (m_tkKeyMap != null && m_tkKeyMap.Length >= count)
                return;

            m_tkKeyMap = new TkKey[count];
            m_tkKeyValid = new bool[count];
            for (int i = 0; i < count; i++)
            {
                TkKey parsed;
                if (Enum.TryParse(((Key)i).ToString(), out parsed))
                {
                    m_tkKeyMap[i] = parsed;
                    m_tkKeyValid[i] = true;
                }
            }
        }

        private void EnsureButtonMap(int count)
        {
            if (m_tkButtonMap != null && m_tkButtonMap.Length >= count)
                return;

            m_tkButtonMap = new TkMouseButton[count];
            m_tkButtonValid = new bool[count];
            for (int i = 0; i < count; i++)
            {
                TkMouseButton parsed;
                if (Enum.TryParse(((MouseButton)i).ToString(), out parsed))
                {
                    m_tkButtonMap[i] = parsed;
                    m_tkButtonValid[i] = true;
                }
            }
        }

        private bool IsRealKeyDown(TkKeyboardState state, int index)
        {
            if (m_tkKeyValid == null || index >= m_tkKeyValid.Length || !m_tkKeyValid[index])
                return false;
            return state[m_tkKeyMap[index]];
        }

        private bool IsRealButtonDown(TkMouseState state, int index)
        {
            if (m_tkButtonValid == null || index >= m_tkButtonValid.Length || !m_tkButtonValid[index])
                return false;
            return state[m_tkButtonMap[index]];
        }
    }
}
