using Engine;
using Engine.Graphics;
using Engine.Input;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Threading;

namespace CmdBridgeMod
{
    /// <summary>
    /// 命令分发。obs.* 只读；act.* 走输入注入白名单。
    /// 所有触碰游戏对象的操作都在游戏线程（下一帧帧首）执行。
    /// </summary>
    internal sealed class CommandRouter
    {
        internal const string ModVersion = "1.0.0";

        /// <summary>
        /// 内建命令名（`cmd.list` 用）。加命令时**必须同步这里**：
        /// 它是 CLI 帮助与"这个实例支持哪些命令"的唯一清单（扩展命令从注册表读）。
        /// </summary>
        internal static readonly string[] BuiltInCommands =
        {
            "ping", "status", "cmd.list", "obs.selftest",
            "obs.snapshot", "obs.ui", "obs.player", "obs.input", "obs.aim", "obs.events",
            "obs.world.blocks", "obs.world.entities", "obs.world.time", "obs.messages", "obs.dialogs",
            "obs.waitfor", "ui.elements", "ui.reachability",
            "act.look", "act.lookdelta", "act.lookat", "act.key", "act.hold", "act.chord",
            "act.mouse", "act.wheel", "act.uiclick", "act.text", "act.releaseall",
            "ui.session.begin", "ui.session.end", "ui.session.status",
            "ui.cursor", "ui.press", "ui.release", "ui.move", "ui.click",
            "ui.rightclick", "ui.shiftclick", "ui.drag", "ui.split",
            "focus.status", "focus.auto", "focus.follow", "focus.attach", "focus.detach", "focus.recover",
            "focus.merge", "focus.lookhold", "look.owner", "hotkey.status"
        };

        // 条件等待：40ms 轮询一次（远快于人类的反应，也远慢于每帧，避免给游戏线程添压力）。
        private const int WaitPollIntervalMs = 40;
        private const int WaitMaxTimeoutMs = 120000;

        private readonly CmdBridgeConfig m_config;
        private readonly GameThreadInvoker m_invoker;
        private readonly InputInjector m_injector;
        private readonly EventRecorder m_recorder;

        public CommandRouter(
            CmdBridgeConfig config,
            GameThreadInvoker invoker,
            InputInjector injector,
            EventRecorder recorder)
        {
            m_config = config;
            m_invoker = invoker;
            m_injector = injector;
            m_recorder = recorder;
        }

        public object Execute(BridgeRequest request)
        {
            switch (request.Command)
            {
                // ---------------------------------------------------------- 只读
                case "ping":
                    // ping 完全在服务器线程回答：启动瞬间也能用，正好作为"游戏是否可接受命令"的探针。
                    return new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["pong"] = true,
                        ["instanceId"] = m_config.InstanceId,
                        ["modVersion"] = ModVersion,
                        ["dispatcherReady"] = GameThreadInvoker.IsDispatcherReady()
                    };
                case "status":
                    return OnGameThread(BuildStatus);
                case "obs.snapshot":
                    return OnGameThread(() => BuildSnapshot(request));
                case "obs.ui":
                case "ui.elements":
                    return OnGameThread(() => UiInspector.Describe(
                        m_config,
                        request.GetBoolean("all", false),
                        request.GetInteger("maxElements", m_config.MaxElements),
                        request.GetString("filter", null)));
                case "obs.player":
                    return OnGameThread(PlayerObserver.Describe);
                case "obs.input":
                    return OnGameThread(PlayerObserver.DescribeRawInput);
                case "obs.aim":
                    return OnGameThread(() => AimObserver.Describe(request.GetFloat("maxDistance", 8f)));
                case "obs.events":
                    return m_recorder.Read(
                        request.GetInteger("sinceSeq", 0),
                        request.GetInteger("max", 200));
                case "obs.world.blocks":
                    return OnGameThread(() => DescribeBlocks(request));
                case "obs.world.entities":
                    return OnGameThread(() => WorldObserver.DescribeEntities(
                        request.GetFloat("radius", 64f),
                        request.GetInteger("max", 64)));
                case "obs.world.time":
                    return OnGameThread(WorldObserver.DescribeTime);
                case "obs.messages":
                    return OnGameThread(MessageObserver.Describe);
                case "obs.dialogs":
                    return OnGameThread(DescribeDialogs);
                case "ui.reachability":
                    return OnGameThread(() => UiInspector.Reachability(m_config));
                case "obs.selftest":
                    return OnGameThread(SelfTest);
                case "obs.waitfor":
                    return WaitForConditions(request);

                // ---------------------------------------------------------- 动作（输入层）
                case "act.look":
                    return m_injector.Look(
                        request.GetFloat("yaw", 0f), request.GetFloat("pitch", 0f));
                case "act.lookdelta":
                    return m_injector.LookDelta(
                        request.GetFloat("dx", 0f), request.GetFloat("dy", 0f));
                case "act.lookat":
                    return m_injector.LookAt(
                        request.GetFloat("x", 0f),
                        request.GetFloat("y", 0f),
                        request.GetFloat("z", 0f));
                case "act.key":
                    return m_injector.KeyPulse(
                        request.GetString("key", null), request.GetInteger("holdMs", 0));
                case "act.hold":
                    return m_injector.KeyHold(
                        request.GetString("key", null), request.GetBoolean("down", true));
                case "act.chord":
                    return m_injector.KeyChord(
                        request.GetStringArray("modifiers").ToArray(),
                        request.GetString("key", null),
                        request.GetInteger("holdMs", 0));
                case "act.mouse":
                    return m_injector.MouseAction(
                        request.GetString("button", "left"),
                        request.GetString("action", "click"),
                        request.GetInteger("holdMs", 0));
                case "act.wheel":
                    return m_injector.Wheel(request.GetInteger("delta", 1));
                case "act.uiclick":
                {
                    float clickX = 0f;
                    float clickY = 0f;
                    bool hasPoint = request.TryGetFloat("x", out clickX) &&
                        request.TryGetFloat("y", out clickY);
                    return m_injector.UiClick(
                        request.GetString("selector", null),
                        request.GetInteger("holdMs", 0),
                        hasPoint, clickX, clickY);
                }
                case "act.text":
                    return m_injector.TypeText(request.GetString("text", string.Empty));
                case "act.releaseall":
                case "act.releaseAll":
                    return m_injector.ReleaseAll();

                // ------------------------------------------------------ 虚拟 UI 鼠标会话（CM-1）
                // 用引擎内的软光标做点击/拖拽：物理鼠标完全不动，也不需要窗口在前台。
                case "ui.session.begin":
                    return UiSessionCommand(request,
                        () => m_injector.Session.Begin(request.GetBoolean("mask", false)));
                case "ui.session.end":
                    return UiSessionCommand(request, () => m_injector.Session.End());
                case "ui.session.status":
                    return m_injector.Session.Describe();
                case "ui.cursor":
                {
                    float cx = 0f;
                    float cy = 0f;
                    bool hasPoint = request.TryGetFloat("x", out cx) && request.TryGetFloat("y", out cy);
                    string selector = request.GetString("selector", null);
                    int steps = request.GetInteger("steps", 1);
                    if (!hasPoint && string.IsNullOrEmpty(selector))
                        throw new BridgeCommandException("invalid_argument", "ui.cursor needs x/y or selector.");
                    return UiSessionCommand(request, () => hasPoint
                        ? m_injector.Session.MoveTo(new Vector2(cx, cy), steps)
                        : m_injector.Session.MoveToElement(selector, steps));
                }
                case "ui.press":
                    return UiSessionCommand(request, () => m_injector.Session.Press(
                        UiMouseSession.ToMouseButton(request.GetString("button", "left"), MouseButton.Left)));
                case "ui.release":
                    return UiSessionCommand(request, () => m_injector.Session.Release(
                        UiMouseSession.ToMouseButton(request.GetString("button", "left"), MouseButton.Left)));
                case "ui.move":
                {
                    float mx = 0f;
                    float my = 0f;
                    if (!request.TryGetFloat("x", out mx) || !request.TryGetFloat("y", out my))
                        throw new BridgeCommandException("invalid_argument", "ui.move needs x and y.");
                    int steps = request.GetInteger("steps", 8);
                    return UiSessionCommand(request, () => m_injector.Session.MoveTo(new Vector2(mx, my), steps));
                }
                case "ui.click":
                {
                    string selector = request.GetString("selector", null);
                    if (string.IsNullOrEmpty(selector))
                        throw new BridgeCommandException("invalid_argument", "ui.click needs a selector.");
                    return UiSessionCommand(request, () => m_injector.Session.Click(selector));
                }
                case "ui.rightclick":
                {
                    string selector = request.GetString("selector", null);
                    if (string.IsNullOrEmpty(selector))
                        throw new BridgeCommandException("invalid_argument", "ui.rightclick needs a selector.");
                    return UiSessionCommand(request, () => m_injector.Session.RightClick(selector));
                }
                case "ui.shiftclick":
                {
                    string selector = request.GetString("selector", null);
                    if (string.IsNullOrEmpty(selector))
                        throw new BridgeCommandException("invalid_argument", "ui.shiftclick needs a selector.");
                    return UiSessionCommand(request, () => m_injector.Session.ShiftClick(selector));
                }
                case "ui.drag":
                case "ui.split":
                {
                    int steps = request.GetInteger("steps", 8);
                    int holdMs = request.GetInteger("holdMs", request.Command == "ui.split" ? 600 : 0);
                    float x1 = 0f;
                    float y1 = 0f;
                    float x2 = 0f;
                    float y2 = 0f;
                    bool hasPoints = request.TryGetFloat("x1", out x1) && request.TryGetFloat("y1", out y1)
                        && request.TryGetFloat("x2", out x2) && request.TryGetFloat("y2", out y2);
                    string from = request.GetString("from", null);
                    string to = request.GetString("to", null);
                    if (!hasPoints && (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)))
                    {
                        throw new BridgeCommandException(
                            "invalid_argument", "ui.drag needs x1/y1/x2/y2 or from/to selectors.");
                    }

                    UiMouseSession.Endpoint start = hasPoints
                        ? UiMouseSession.Endpoint.At(new Vector2(x1, y1))
                        : UiMouseSession.Endpoint.At(from);
                    UiMouseSession.Endpoint end = hasPoints
                        ? UiMouseSession.Endpoint.At(new Vector2(x2, y2))
                        : UiMouseSession.Endpoint.At(to);
                    return UiSessionCommand(request,
                        () => m_injector.Session.Drag(start, end, steps, holdMs));
                }

                // ------------------------------------------------------ 焦点策略与共控（CM-2）
                case "focus.status":
                    return m_injector.Focus.Describe();
                case "focus.auto":
                case "focus.follow":
                case "focus.attach":
                case "focus.detach":
                {
                    string requested = request.Command.Substring("focus.".Length);
                    FocusMode mode;
                    if (!FocusPolicy.TryParseMode(requested, out mode))
                        throw new BridgeCommandException("invalid_argument", "Unknown focus mode: " + requested);
                    m_injector.Focus.Mode = mode;
                    Log.Information("[CmdBridge] focus mode -> " + m_injector.Focus.ModeName);
                    return m_injector.Focus.Describe();
                }
                case "focus.recover":
                {
                    // 一键自救：万一焦点策略把鼠标卡住了（视角转不动 / 光标跑到程序外），
                    // 这条命令立刻回到"跟随引擎"并把窗口状态恢复成真实值。
                    m_injector.Focus.Mode = FocusMode.Follow;
                    m_injector.Focus.RestoreNaturalFocus();
                    Log.Information("[CmdBridge] focus recovered to follow mode");
                    return m_injector.Focus.Describe();
                }
                case "focus.merge":
                    m_injector.Focus.MergeEnabled = request.GetBoolean("enabled", true);
                    return m_injector.Focus.Describe();
                case "focus.lookhold":
                    m_injector.Focus.LookHoldSeconds = request.GetFloat("seconds", 0.4f);
                    return m_injector.Focus.Describe();
                case "hotkey.status":
                    return m_injector.Hotkeys.Describe();
                case "cmd.list":
                    return ListCommands();
                case "look.owner":
                {
                    LookOwnerMode owner;
                    if (!FocusPolicy.TryParseLookOwner(request.GetString("owner", null), out owner))
                        throw new BridgeCommandException(
                            "invalid_argument", "look.owner must be auto|user|ai|shared.");
                    m_injector.Focus.LookOwner = owner;
                    return m_injector.Focus.Describe();
                }

                default:
                {
                    // 扩展命令（别的 Mod 注册的，例如 PlayerAiMod 的 ai.*）：
                    // 内建命令优先，扩展命令不能覆盖内建行为。
                    CommandExtension extension;
                    if (m_injector.Commands.TryGet(request.Command, out extension))
                        return ExecuteExtension(extension, request);

                    throw new BridgeCommandException(
                        "unknown_command", "Unknown command '" + request.Command + "'.");
                }
            }
        }

        /// <summary>
        /// 执行扩展命令。默认在**游戏线程**执行（触碰游戏对象/输入的命令必须如此）；
        /// 扩展命令抛出的 <see cref="CmdBridgeCommandException"/> 直接映射成错误码返回给客户端。
        /// </summary>
        private object ExecuteExtension(CommandExtension extension, BridgeRequest request)
        {
            var view = new CmdBridgeCommandRequest(request);
            Func<object> action = () =>
            {
                try
                {
                    object result = extension.Handler(view);
                    return result ?? new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["ok"] = true,
                        ["command"] = extension.Name
                    };
                }
                catch (CmdBridgeCommandException exception)
                {
                    throw new BridgeCommandException(exception.Code, exception.Message);
                }
            };

            return extension.RunOnGameThread ? OnGameThread(action) : action();
        }

        /// <summary>列出内建命令与扩展命令（谁注册的一目了然，便于排查"命令没生效"）。</summary>
        private object ListCommands()
        {
            List<CommandExtension> extensions = m_injector.Commands.List();
            var builtIn = new List<string>(BuiltInCommands);
            var external = new List<Dictionary<string, object>>();
            for (int i = 0; i < extensions.Count; i++)
            {
                external.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = extensions[i].Name,
                    ["owner"] = extensions[i].Owner,
                    ["description"] = extensions[i].Description,
                    ["gameThread"] = extensions[i].RunOnGameThread
                });
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["builtIn"] = builtIn,
                ["extensions"] = external,
                ["counts"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["builtIn"] = builtIn.Count,
                    ["extensions"] = external.Count
                }
            };
        }

        /// <summary>
        /// 执行一条"虚拟 UI 鼠标会话"命令：默认等手势在帧首逐帧跑完再返回，
        /// 于是脚本/AI 拿到返回值时动作已经落地（等价于真人松手那一刻）。
        /// </summary>
        private object UiSessionCommand(BridgeRequest request, Func<Dictionary<string, object>> action)
        {
            action();

            bool wait = request.GetBoolean("wait", true);
            bool completed = true;
            if (wait)
            {
                int timeout = request.GetInteger("timeoutMs", 5000);
                completed = m_injector.Session.WaitUntilIdle(Math.Max(200, timeout));
            }

            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["completed"] = completed,
                ["session"] = m_injector.Session.Describe()
            };
            if (!completed)
                result["hint"] = "The gesture is still running; poll ui.session.status or retry with a larger timeoutMs.";
            return result;
        }

        private object OnGameThread(Func<object> action)
        {
            return m_invoker.Invoke(action);
        }

        // ---------------------------------------------------------------- 只读实现

        /// <summary>
        /// 条件等待：在游戏线程上周期性求值，直到全部条件成立或超时。
        ///
        /// 为什么需要它：屏幕切换、世界加载、面板弹出都是跨帧完成的——SwitchScreen 会立即更新
        /// CurrentScreen，但新屏幕的控件要等转屏动画中段才进入 RootWidget.Children
        /// （ScreensManager.cs:85, 302-309）；对话框也是异步关闭的。客户端靠固定 sleep 必然要么太慢、
        /// 要么在竞态里读到空界面。有了它，AI 可以写"按键开背包 → 等槽位可点"，语义明确、不靠猜。
        ///
        /// 支持的条件（大小写不敏感）：
        ///   screen.animating.false / screen.animating.true / screen.is:&lt;name&gt;
        ///   element.present:&lt;selector&gt; / element.hittable:&lt;selector&gt; / element.clickable:&lt;selector&gt;
        ///   modal.none / modal.is:&lt;TypeName&gt; / dialog.none / dialog.present
        ///   world.loaded / world.unloaded / player.alive / player.dead
        ///   player.sleeping / player.awake / events.since:&lt;seq&gt;
        /// </summary>
        private object WaitForConditions(BridgeRequest request)
        {
            string single = request.GetString("condition", null);
            List<string> conditions = request.GetStringArray("conditions");
            if (!string.IsNullOrWhiteSpace(single))
                conditions.Insert(0, single);
            if (conditions.Count == 0)
            {
                throw new BridgeCommandException(
                    "invalid_argument", "condition or conditions is required.");
            }

            int timeoutMs = MathUtils.Clamp(
                request.GetInteger("timeoutMs", 10000), 0, WaitMaxTimeoutMs);
            long startTicks = Environment.TickCount64;
            int polls = 0;

            while (true)
            {
                string pending = (string)m_invoker.Invoke(() =>
                {
                    // 条件求值必须整批在同一帧完成，否则多个条件可能落在不同帧上互相矛盾。
                    for (int i = 0; i < conditions.Count; i++)
                    {
                        string failed = EvaluateCondition(conditions[i]);
                        if (failed != null)
                            return failed;
                    }
                    return null;
                });
                polls++;

                if (pending == null)
                {
                    return new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["satisfied"] = true,
                        ["conditions"] = conditions,
                        ["elapsedMs"] = Environment.TickCount64 - startTicks,
                        ["polls"] = polls
                    };
                }

                if (Environment.TickCount64 - startTicks >= timeoutMs)
                {
                    return new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["satisfied"] = false,
                        ["pendingCondition"] = pending,
                        ["conditions"] = conditions,
                        ["elapsedMs"] = Environment.TickCount64 - startTicks,
                        ["polls"] = polls,
                        ["timeoutMs"] = timeoutMs
                    };
                }

                Thread.Sleep(WaitPollIntervalMs);
            }
        }

        /// <summary>返回 null 表示条件成立；否则返回该条件（供超时报告）。</summary>
        private string EvaluateCondition(string condition)
        {
            if (string.IsNullOrWhiteSpace(condition))
                throw new BridgeCommandException("invalid_argument", "condition cannot be empty.");
            string c = condition.Trim();

            if (string.Equals(c, "screen.animating.false", StringComparison.OrdinalIgnoreCase))
                return ScreensManager.IsAnimating ? c : null;
            if (string.Equals(c, "screen.animating.true", StringComparison.OrdinalIgnoreCase))
                return ScreensManager.IsAnimating ? null : c;

            const string screenPrefix = "screen.is:";
            if (c.StartsWith(screenPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string expected = c.Substring(screenPrefix.Length).Trim();
                string actual = UiInspector.ScreenName();
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ? null : c;
            }

            if (c.StartsWith("element.", StringComparison.OrdinalIgnoreCase))
                return EvaluateElementCondition(c);

            if (string.Equals(c, "modal.none", StringComparison.OrdinalIgnoreCase))
                return CurrentModalPanel() == null ? null : c;
            const string modalPrefix = "modal.is:";
            if (c.StartsWith(modalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string expected = c.Substring(modalPrefix.Length).Trim();
                string actual = CurrentModalPanel();
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ? null : c;
            }

            if (string.Equals(c, "dialog.none", StringComparison.OrdinalIgnoreCase))
                return DialogsManager.Dialogs.Count == 0 ? null : c;
            if (string.Equals(c, "dialog.present", StringComparison.OrdinalIgnoreCase))
                return DialogsManager.Dialogs.Count > 0 ? null : c;

            if (string.Equals(c, "world.loaded", StringComparison.OrdinalIgnoreCase))
                return GameManager.Project != null ? null : c;
            if (string.Equals(c, "world.unloaded", StringComparison.OrdinalIgnoreCase))
                return GameManager.Project == null ? null : c;

            if (string.Equals(c, "player.alive", StringComparison.OrdinalIgnoreCase))
            {
                ComponentPlayer player = GetPlayerOrNull();
                return player != null && player.ComponentHealth != null &&
                    player.ComponentHealth.Health > 0f ? null : c;
            }
            if (string.Equals(c, "player.dead", StringComparison.OrdinalIgnoreCase))
            {
                ComponentPlayer player = GetPlayerOrNull();
                return player != null && player.ComponentHealth != null &&
                    player.ComponentHealth.Health <= 0f ? null : c;
            }
            if (string.Equals(c, "player.sleeping", StringComparison.OrdinalIgnoreCase))
                return IsPlayerSleeping() ? null : c;
            if (string.Equals(c, "player.awake", StringComparison.OrdinalIgnoreCase))
                return IsPlayerSleeping() ? c : null;

            const string eventsPrefix = "events.since:";
            if (c.StartsWith(eventsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string raw = c.Substring(eventsPrefix.Length).Trim();
                if (!long.TryParse(raw, out long seq))
                {
                    throw new BridgeCommandException(
                        "invalid_wait_condition", "events.since requires a numeric sequence.");
                }
                return m_recorder.LastSeq > seq ? null : c;
            }

            throw new BridgeCommandException(
                "invalid_wait_condition", "Unknown wait condition '" + c + "'.");
        }

        private string EvaluateElementCondition(string condition)
        {
            int colon = condition.IndexOf(':');
            if (colon < 0)
            {
                throw new BridgeCommandException(
                    "invalid_wait_condition",
                    "element conditions look like element.clickable:<selector>.");
            }
            string kind = condition.Substring(0, colon).Trim().ToLowerInvariant();
            string selector = condition.Substring(colon + 1).Trim();
            if (selector.Length == 0)
            {
                throw new BridgeCommandException(
                    "invalid_wait_condition", "element condition needs a selector.");
            }

            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                return condition;   // 界面尚未就绪，继续等待

            Widget widget;
            try
            {
                widget = UiInspector.Resolve(root, selector, false, Vector2.Zero);
            }
            catch (BridgeCommandException exception) when (exception.Code == "element_missing")
            {
                return condition;   // 还没出现，继续等待
            }

            switch (kind)
            {
                case "element.present":
                    return null;
                case "element.hittable":
                    return UiInspector.IsHittable(widget) ? null : condition;
                case "element.clickable":
                    return UiInspector.IsClickable(widget) ? null : condition;
                default:
                    throw new BridgeCommandException(
                        "invalid_wait_condition", "Unknown element condition '" + kind + "'.");
            }
        }

        private static string CurrentModalPanel()
        {
            ComponentPlayer player = GetPlayerOrNull();
            if (player == null)
                return null;
            try
            {
                ComponentGui gui = player.ComponentGui;
                return gui != null && gui.ModalPanelWidget != null
                    ? gui.ModalPanelWidget.GetType().Name
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPlayerSleeping()
        {
            ComponentPlayer player = GetPlayerOrNull();
            if (player == null)
                return false;
            try
            {
                ComponentSleep sleep = player.Entity.FindComponent<ComponentSleep>(false);
                return sleep != null && sleep.IsSleeping;
            }
            catch
            {
                return false;
            }
        }

        private Dictionary<string, object> BuildStatus()
        {
            var status = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["modVersion"] = ModVersion,
                ["pid"] = Environment.ProcessId,
                ["instanceId"] = m_config.InstanceId,
                ["screen"] = UiInspector.ScreenName(),
                ["screenType"] = ScreensManager.CurrentScreen != null
                    ? ScreensManager.CurrentScreen.GetType().Name
                    : null,
                ["animating"] = ScreensManager.IsAnimating,
                ["frameIndex"] = Time.FrameIndex,
                ["worldLoaded"] = GameManager.Project != null,
                ["inputInjection"] = m_config.EnableInputInjection
            };

            ContainerWidget root = ScreensManager.RootWidget;
            bool layoutValid = false;
            if (root != null)
            {
                BoundingRectangle bounds = root.GlobalBounds;
                layoutValid = bounds.Max.X - bounds.Min.X > 1f && bounds.Max.Y - bounds.Min.Y > 1f;
            }
            status["layoutValid"] = layoutValid;

            try
            {
                Point2 position = Window.Position;
                Point2 size = Window.Size;
                status["window"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["position"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["x"] = position.X,
                        ["y"] = position.Y
                    },
                    ["clientSize"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["x"] = size.X,
                        ["y"] = size.Y
                    }
                };
            }
            catch
            {
                status["window"] = null;
            }

            try
            {
                status["viewport"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = Display.Viewport.Width,
                    ["y"] = Display.Viewport.Height
                };
            }
            catch
            {
                status["viewport"] = null;
            }

            try
            {
                status["uiScale"] = SettingsManager.UIScale;
                status["upsideDownLayout"] = SettingsManager.UpsideDownLayout;
            }
            catch
            {
            }

            try
            {
                status["serverError"] = null;
            }
            catch
            {
            }

            return status;
        }

        private Dictionary<string, object> BuildSnapshot(BridgeRequest request)
        {
            var snapshot = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["status"] = BuildStatus(),
                ["ui"] = UiInspector.Describe(
                    m_config,
                    request.GetBoolean("all", false),
                    request.GetInteger("maxElements", m_config.MaxElements),
                    request.GetString("filter", null)),
                ["player"] = PlayerObserver.Describe(),
                ["input"] = PlayerObserver.DescribeRawInput(),
                ["aim"] = AimObserver.Describe(request.GetFloat("maxDistance", 8f)),
                ["world"] = GameManager.Project != null ? WorldObserver.DescribeTime() : null,
                ["messages"] = MessageObserver.Describe(),
                ["dialogs"] = DescribeDialogs(),
                ["events"] = m_recorder.Read(
                    request.GetInteger("sinceSeq", 0),
                    request.GetInteger("maxEvents", 64))
            };
            return snapshot;
        }

        /// <summary>
        /// 区域方块扫描。未给中心时以玩家所在格为中心，避免 AI 必须自己换算格子坐标。
        /// </summary>
        private Dictionary<string, object> DescribeBlocks(BridgeRequest request)
        {
            int x = 0;
            int y = 0;
            int z = 0;
            bool hasCenter = request.TryGetInteger("x", out x) &&
                request.TryGetInteger("y", out y) &&
                request.TryGetInteger("z", out z);
            if (!hasCenter)
            {
                ComponentPlayer player = GetPlayerOrNull();
                if (player == null)
                    throw new BridgeCommandException("world_not_loaded", "No player is available.");
                Vector3 position = player.ComponentBody.Position;
                x = Terrain.ToCell(position.X);
                y = Terrain.ToCell(position.Y);
                z = Terrain.ToCell(position.Z);
            }
            return WorldObserver.DescribeBlocks(
                x, y, z,
                request.GetInteger("radius", 4),
                request.GetInteger("max", 256));
        }

        private static ComponentPlayer GetPlayerOrNull()
        {
            if (GameManager.Project == null)
                return null;
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null || players.ComponentPlayers.Count == 0)
                return null;
            return players.ComponentPlayers[0];
        }

        private List<Dictionary<string, object>> DescribeDialogs()
        {
            var dialogs = new List<Dictionary<string, object>>();
            try
            {
                ReadOnlyList<Dialog> all = DialogsManager.Dialogs;
                for (int i = 0; i < all.Count; i++)
                {
                    Dialog dialog = all[i];
                    dialogs.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["index"] = i,
                        ["type"] = dialog.GetType().Name,
                        ["parent"] = dialog.ParentWidget != null
                            ? dialog.ParentWidget.GetType().Name
                            : null
                    });
                }
            }
            catch
            {
            }
            return dialogs;
        }

        /// <summary>
        /// 自检：确认白名单注入点全部可用（不写入任何值），并复查可达性统计。
        /// 用于 P2 阶段验证"帧首缝隙 + 注入点"是否成立。
        /// </summary>
        private Dictionary<string, object> SelfTest()
        {
            var checks = new List<Dictionary<string, object>>();
            IModParentField fields = ModManager.Instance.ModParentField;

            AddCheck(checks, "Keyboard." + InputWhitelist.KeyboardDownArray, () =>
            {
                bool[] array = fields.GetStaticField<bool[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardDownArray);
                return array != null && array.Length > 0;
            });
            AddCheck(checks, "Keyboard." + InputWhitelist.KeyboardDownOnceArray, () =>
            {
                bool[] array = fields.GetStaticField<bool[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardDownOnceArray);
                return array != null && array.Length > 0;
            });
            AddCheck(checks, "Keyboard." + InputWhitelist.KeyboardRepeatArray, () =>
            {
                double[] array = fields.GetStaticField<double[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardRepeatArray);
                return array != null && array.Length > 0;
            });
            AddCheck(checks, "Mouse." + InputWhitelist.MouseDownArray, () =>
            {
                bool[] array = fields.GetStaticField<bool[]>(
                    typeof(Mouse), InputWhitelist.MouseDownArray);
                return array != null && array.Length > 0;
            });
            AddCheck(checks, "Mouse." + InputWhitelist.MouseDownOnceArray, () =>
            {
                bool[] array = fields.GetStaticField<bool[]>(
                    typeof(Mouse), InputWhitelist.MouseDownOnceArray);
                return array != null && array.Length > 0;
            });
            AddCheck(checks, "Mouse." + InputWhitelist.MouseWheelMovement, () =>
            {
                object value = fields.GetStaticField(
                    typeof(Mouse), InputWhitelist.MouseWheelMovement);
                return value is int;
            });
            AddCheck(checks, "Mouse." + InputWhitelist.MouseLastWheelValue, () =>
            {
                object value = fields.GetStaticField(
                    typeof(Mouse), InputWhitelist.MouseLastWheelValue);
                return value == null || value is int;
            });
            AddCheck(checks, "ScreensManager.m_screens", () =>
            {
                Dictionary<string, Screen> screens = fields.GetStaticField<Dictionary<string, Screen>>(
                    typeof(ScreensManager), "m_screens");
                return screens != null;
            });
            AddCheck(checks, "window.position", () =>
            {
                Point2 ignored = Window.Position;
                return true;
            });

            // 扩展命令注册表：上层 Mod（如 PlayerAiMod 的 ai.*）靠它挂命令，不该出现"注册不上/摘不掉"。
            AddCheck(checks, "commands.register", () =>
            {
                string error;
                bool ok = m_injector.Commands.Register("selftest.probe", delegate { return null; },
                    "CmdBridgeMod", "selftest probe", false, out error);
                CommandExtension probe;
                return ok && m_injector.Commands.TryGet("selftest.probe", out probe);
            });
            AddCheck(checks, "commands.duplicateRejected", () =>
            {
                string error;
                bool again = m_injector.Commands.Register("selftest.probe", delegate { return null; },
                    "someone.else", null, false, out error);
                return !again && !string.IsNullOrEmpty(error);
            });
            AddCheck(checks, "commands.ownerGuard", () =>
            {
                string error;
                CommandExtension probe;
                bool stolen = m_injector.Commands.Unregister("selftest.probe", "someone.else", out error);
                return !stolen && !string.IsNullOrEmpty(error)
                    && m_injector.Commands.TryGet("selftest.probe", out probe);
            });
            AddCheck(checks, "commands.unregisterAll", () =>
            {
                int removed = m_injector.Commands.UnregisterAll("CmdBridgeMod");
                CommandExtension probe;
                return removed >= 1 && !m_injector.Commands.TryGet("selftest.probe", out probe);
            });
            AddCheck(checks, "commands.builtInListed", () =>
            {
                for (int i = 0; i < BuiltInCommands.Length; i++)
                {
                    if (string.Equals(BuiltInCommands[i], "cmd.list", StringComparison.Ordinal))
                        return true;
                }
                return false;
            });

            bool injectionPointOk = true;
            if (GameManager.Project != null)
            {                try
                {
                    SubsystemPlayers players =
                        GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
                    if (players != null && players.ComponentPlayers.Count > 0)
                    {
                        ComponentLocomotion locomotion = players.ComponentPlayers[0].ComponentLocomotion;
                        AddCheck(checks, "ComponentLocomotion." + InputWhitelist.LocomotionLookAngles, () =>
                        {
                            Vector2 angles = fields.GetParentField<Vector2>(
                                locomotion,
                                InputWhitelist.LocomotionLookAngles,
                                typeof(ComponentLocomotion));
                            return true;
                        });
                    }
                }
                catch
                {
                    injectionPointOk = false;
                }
            }

            bool allOk = injectionPointOk;
            for (int i = 0; i < checks.Count; i++)
            {
                if (!(bool)checks[i]["ok"])
                    allOk = false;
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["allOk"] = allOk,
                ["checks"] = checks,
                ["whitelist"] = InputWhitelist.All,
                ["reachability"] = UiInspector.Reachability(m_config)
            };
        }

        private static void AddCheck(
            List<Dictionary<string, object>> checks, string name, Func<bool> probe)
        {
            var entry = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = name
            };
            try
            {
                entry["ok"] = probe();
            }
            catch (Exception exception)
            {
                entry["ok"] = false;
                entry["error"] = exception.GetType().Name + ": " + exception.Message;
            }
            checks.Add(entry);
        }
    }
}
