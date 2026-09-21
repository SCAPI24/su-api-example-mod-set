using Engine;
using Engine.Input;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Threading;

namespace CmdBridgeMod
{
    /// <summary>
    /// 玩家控制器注入：视角 / 键盘 / 鼠标。
    ///
    /// 设计要点（doc/cmd-bridge-plan.md §2.6/§2.7/§2.9）：
    ///   · 所有写入都必须发生在"下一帧帧首"，因为 Keyboard/Mouse 的 downOnce 数组会在帧末被清空；
    ///     GameThreadInvoker 通过 Dispatcher.Dispatch 把动作排到帧首执行。
    ///   · 视角 = 写 ComponentBody.Rotation（水平朝向，玩家转视角的同一状态）+ m_lookAngles.Y（俯仰）。
    ///   · 一次 UI 点击 = 两帧：帧 1 按下（派生 Press/Tap），帧 2 松开（派生 Click）。
    ///   · 只写 InputWhitelist 中的成员，不触碰任何游戏状态（生命/背包/方块/位置/时间）。
    /// </summary>
    internal sealed class InputInjector
    {
        private const float MaxPitchRadians = 82f * (MathUtils.PI / 180f);
        private const float MaxHeadYawRadians = 140f * (MathUtils.PI / 180f);

        private readonly CmdBridgeConfig m_config;
        private readonly GameThreadInvoker m_invoker;
        private readonly HashSet<int> m_heldKeys = new HashSet<int>();
        private readonly HashSet<int> m_heldButtons = new HashSet<int>();

        private IModParentField m_fields;
        private bool[] m_keysDown;
        private bool[] m_keysDownOnce;
        private double[] m_keysRepeat;
        private bool[] m_mouseDown;
        private bool[] m_mouseDownOnce;

        private WidgetInput m_clickInput;
        private MouseButton m_clickButton = MouseButton.Left;
        private Vector2 m_clickPoint;

        // Android 触摸点击：本帧按下、下一帧帧首抬起（见 AndroidTouch）。
        private bool m_androidTouchPressed;
        private Vector2 m_androidTouchPoint;

        /// <summary>
        /// 本帧做过的 UI 动作（`click:选择器` / `rightclick:…` / `drag:a→b`）。
        /// 菜单、背包这类操作在**原始输入层里没有痕迹**（走的是引擎软光标），所以另外记一份，
        /// 让动作包能把它录成语义事件、回放时按时间点重放同一套点击。
        /// 由 <see cref="DrainUiActions"/> 每帧取走一次（录制端读输入快照时）。
        /// </summary>
        private readonly List<string> m_uiActions = new List<string>();

        private static InputInjector s_current;

        /// <summary>当前注入器实例（进程里只有一个；工厂方法挂命令时用得上）。</summary>
        internal static InputInjector Current
        {
            get { return s_current; }
        }

        /// <summary>记一条 UI 动作（供本类与 <see cref="UiMouseSession"/> 用）。</summary>
        internal void NoteUiAction(string action)
        {
            if (string.IsNullOrEmpty(action))
                return;
            lock (m_uiActions)
            {
                if (m_uiActions.Count < 32)
                    m_uiActions.Add(action);
            }
        }

        /// <summary>取走本帧累积的 UI 动作（每帧读输入快照时调一次；读不到就返回空表）。</summary>
        internal static List<string> DrainUiActions()
        {
            InputInjector injector = s_current;
            if (injector == null)
                return new List<string>();

            lock (injector.m_uiActions)
            {
                if (injector.m_uiActions.Count == 0)
                    return new List<string>();
                var copy = new List<string>(injector.m_uiActions);
                injector.m_uiActions.Clear();
                return copy;
            }
        }

        public InputInjector(CmdBridgeConfig config, GameThreadInvoker invoker)
        {
            m_config = config;
            m_invoker = invoker ?? new GameThreadInvoker(config.RequestTimeoutSeconds);
            s_current = this;
            Pump = new FrameStartPump();
            Session = new UiMouseSession(this);
            Focus = new FocusPolicy(this);
            Hotkeys = new HotkeyRegistry();
            Commands = new CommandExtensionRegistry();
            Ui = new UiService(this);
        }

        /// <summary>UI 定位 / 点击服务（`ui.locate` / `ui.click`，编辑器与行为树共用）。</summary>
        internal UiService Ui { get; }

        /// <summary>单帧合成点击用的输入面（下一帧帧首收尾时把它交回去）。</summary>
        private WidgetInput m_directClickInput;

        /// <summary>在游戏线程执行并返回结果（UI 服务的同步入口）。</summary>
        internal object OnGameThreadForUi(Func<object> action)
        {
            return OnGameThread(action);
        }

        /// <summary>
        /// 排一次"单帧合成点击"（**只有坐标、不知道控件**时的入口）：把"按过一下 + 按下起点"
        /// 写进输入层，让引擎自己的 `UpdateInputFromMouse` 在同一帧派生 `Tap`+`Click`。
        ///
        /// 必须是**帧首**写：`WidgetInput.Update()` 一开头就 `ClearInput()`，
        /// 帧末写等于白写（早期"直注入没反应"就是这个原因）。
        /// 一帧之后再把软光标关掉，避免长驻改动影响后续真实鼠标。
        ///
        /// 优先用 <see cref="ApplyDirectClick"/>（带控件）：**输入面必须取自目标所在的层**
        /// （世界内 HUD 读的是 `GameWidget` 那层），只给坐标的话只能退回根输入面，HUD 点不动。
        /// </summary>
        internal void QueueDirectUiClick(Vector2 point)
        {
            EnsureEnabled();
            NoteUiAction(point.X.ToString("0.##") + "," + point.Y.ToString("0.##") + " (direct)");

            Pump.Enqueue(delegate
            {
                ContainerWidget root = ScreensManager.RootWidget;
                ApplyDirectUiClick(point, root != null ? root.HitTestGlobal(point) : null);
                // 下一帧收尾：关掉软光标（`Tap/Click` 与 `m_mouseDownPoint` 引擎自己会清）
                Pump.Enqueue(delegate { RestoreAfterDirectUiClick(); });
            });
        }

        /// <summary>在游戏线程上立即合成一次点击（调用方已知道要点哪个控件）。</summary>
        internal void ApplyDirectClick(Vector2 point, Widget target)
        {
            EnsureEnabled();
            ApplyDirectUiClick(point, target);
            // 下一帧收尾：关掉软光标（`Tap/Click` 与 `m_mouseDownPoint` 引擎自己会清）
            Pump.Enqueue(delegate { RestoreAfterDirectUiClick(); });
        }

        /// <summary>
        /// 语义点击（给行为树/门面用）：解析目标 + 单帧合成点击。
        ///
        /// 两条路，取决于**调用者在哪个线程**：
        ///   · 已经在游戏线程（行为树 tick、帧首批次）→ **就地**解析 + 就地写输入层，
        ///     当帧引擎就能读到，返回值是"真的点到了"；
        ///   · 其它线程（命令面）→ 整件事排到帧首，返回值是"已受理"。
        /// 坐标一律**点击那一刻**现算（用户要求"用 UI 的真实位置"）。
        /// </summary>
        internal bool UiClickTargetCore(string target, string mode, out string error)
        {
            error = null;
            EnsureEnabled();
            if (string.IsNullOrEmpty(target))
            {
                error = "a UI target is required";
                return false;
            }

            // 录进"本帧 UI 动作"：动作包靠它把菜单点击录成语义事件（`click:Play` / `click:list:…@…`）。
            NoteUiAction("click:" + target);

            bool viaSession = string.Equals(mode, "input", StringComparison.OrdinalIgnoreCase);
            if (GameThreadInvoker.IsGameThread())
            {
                return ApplyUiClickNow(target, viaSession, out error);
            }

            Pump.Enqueue(delegate
            {
                string ignored;
                ApplyUiClickNow(target, viaSession, out ignored);
            });
            return true;
        }

        /// <summary>在游戏线程上执行一次语义点击（解析失败如实返回 false + 原因）。</summary>
        private bool ApplyUiClickNow(string target, bool viaSession, out string error)
        {
            error = null;
            UiTarget.Parsed parsed;
            if (!UiTarget.TryParse(target, out parsed))
            {
                error = "cannot parse the UI target '" + target + "'";
                return false;
            }

            try
            {
                if (viaSession)
                {
                    switch (parsed.Kind)
                    {
                        case UiTarget.TargetKind.ListRow:
                            UiClickSession(parsed.Selector, false, 0f, 0f, parsed.RowIndex,
                                parsed.RowText, 0);
                            break;
                        case UiTarget.TargetKind.Point:
                            UiClickSession(null, true, parsed.X, parsed.Y, -1, null, 0);
                            break;
                        default:
                            UiClickSession(parsed.Selector, false, 0f, 0f, -1, null, 0);
                            break;
                    }
                    return true;
                }

                ApplyDirectUiClick(Ui.ResolvePointCore(target), Ui.ResolveTargetCore(target));
                // 下一帧收尾：关掉软光标（`Tap/Click` 与 `m_mouseDownPoint` 引擎自己会清）
                Pump.Enqueue(delegate { RestoreAfterDirectUiClick(); });
                return true;
            }
            catch (BridgeCommandException exception)
            {
                // 目标现在还不在（切场动画中间、列表还没填）—— 如实回报，让调用方决定重试。
                error = exception.Code + ": " + exception.Message;
                return false;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        /// <summary>帧首：写输入层的"按过一下"。</summary>
        private void ApplyDirectUiClick(Vector2 point, Widget target)
        {
            // Source: Mod/CmdBridgeMod/Server/AndroidTouch.cs
            // Android：界面只认触摸（WidgetInput 的 Tap/Click 来自 TouchLocations），
            // 桌面那套"软光标 + downOnce"在 Android 上派生不出 Click。
            // 因此这里注入真实触摸事件：本帧按下，下一帧帧首抬起（RestoreAfterDirectUiClick），
            // 与真人"快速点一下"在引擎眼里等价。
            if (AndroidTouch.Available)
            {
                m_androidTouchPressed = true;
                m_androidTouchPoint = point;
                AndroidTouch.Press(point);
                return;
            }

            // ⚠️ **必须写在"目标控件自己的输入面"上，不能写在根控件的输入面上**。
            //
            // 引擎里每个 `WidgetsHierarchyInput` 是一个独立的 `WidgetInput`：
            //   · 主菜单那些屏挂在 `ScreensManager.RootWidget` 上 → 用根输入面；
            //   · **世界内 HUD** 挂在 `GameWidget` 下，而 `GameWidget` 自己在
            //     `GameWidget.cs:104-106` 设了 `WidgetsHierarchyInput` → HUD 按钮读的是**它那一层**的输入面。
            // 软光标位置、`IsMouseCursorVisible`、`m_mouseDownPoint` 这三样都是**每个输入面各自一份**，
            // 写到根输入面上，HUD 按钮那一层什么都没变 → 派生不出 Click。
            // （实测症状：Editor 里点「点一下」，世界内 MoreButton 毫无反应；`obs.ui` 还如实写着
            //   `clickable=false, clickReason="mouse cursor is captured"`。）
            WidgetInput input = target != null ? target.Input : GetRootWidgetInput();
            if (input == null)
                throw new BridgeCommandException("not_ready", "The UI input surface is not ready.");

            // 先开软光标，再写位置：MousePosition 的 setter 在软光标关闭时会去挪**真实光标**。
            input.UseSoftMouseCursor = true;
            // 世界内 `ComponentInput.Update` 每帧把这一层的可见性置 false（`ComponentInput.cs:155`），
            // 不重申的话 `UpdateInputFromMouse` 整个被门控掉（`WidgetInput.cs:741`）→ 派生不出 Click。
            input.IsMouseCursorVisible = true;
            m_directClickInput = input;
            input.MousePosition = point;

            // down 数组保持"没按"、只把 downOnce 置位：
            // 于是这一帧 `Tap`（来自 downOnce）与 `Click`（来自"没按 + 有按下起点"）同时成立 ——
            // 与真人"快速点一下"在引擎眼里完全等价（Source: Game/WidgetInput.cs:735-769）。
            SetMouseHeld(MouseButton.Left, false);
            SetMouseDownOnce(MouseButton.Left, true);

            // 按下起点与"按下的是左键"：Click 派生的两个必要条件
            Fields.ModifyParentField(input, InputWhitelist.WidgetInputMouseDownPoint,
                (Vector2?)point, typeof(WidgetInput));
            Fields.ModifyParentField(input, InputWhitelist.WidgetInputMouseDownButton,
                MouseButton.Left, typeof(WidgetInput));
        }

        /// <summary>下一帧帧首：把软光标交回去（一次性动作不该留下长驻状态）。</summary>
        private void RestoreAfterDirectUiClick()
        {
            if (m_androidTouchPressed)
            {
                m_androidTouchPressed = false;
                AndroidTouch.Release(m_androidTouchPoint);
                return;
            }

            WidgetInput input = m_directClickInput ?? GetRootWidgetInput();
            m_directClickInput = null;
            if (input == null)
                return;

            try
            {
                SetMouseDownOnce(MouseButton.Left, false);
                ClearWidgetMouseDownPoint(input);
                input.UseSoftMouseCursor = false;
            }
            catch (Exception exception)
            {
                Log.Warning("[CmdBridge] direct ui click cleanup failed: " + exception.Message);
            }
        }

        public bool Enabled => m_config.EnableInputInjection;

        /// <summary>当前是否有按键/鼠标处于"按住"状态（供依赖 Mod 检查是否还有残留输入）。</summary>
        public bool IsHoldingAnything => m_heldKeys.Count > 0 || m_heldButtons.Count > 0;

        /// <summary>
        /// 帧首泵：任何 Mod 都可以把一个动作排到"下一帧帧首"执行。
        /// 需要它是因为 Dispatcher.Dispatch 在主线程调用会立即执行，只有后台线程调用才会入队到帧首。
        /// </summary>
        internal FrameStartPump Pump { get; }

        /// <summary>虚拟 UI 鼠标会话（软光标点击/拖拽，不动用户物理鼠标）。</summary>
        internal UiMouseSession Session { get; }

        /// <summary>焦点策略与共控合并（前台合并真实输入、失焦脱离真实鼠标）。</summary>
        internal FocusPolicy Focus { get; }

        /// <summary>热键注册表（帧首判定，可复用：PlayerAiMod 用它绑 Home / PgUp / PgDn）。</summary>
        internal HotkeyRegistry Hotkeys { get; }

        /// <summary>
        /// 扩展命令注册表：其它 Mod 往同一个控制通道里挂自己的命令前缀（例如 PlayerAiMod 的 `ai.*`），
        /// 不必改本 Mod 的路由代码。
        /// </summary>
        internal CommandExtensionRegistry Commands { get; }

        /// <summary>某个键当前是否由注入按住（供共控合并判断）。</summary>
        internal bool IsKeyHeldByInjection(int index)
        {
            return m_heldKeys.Contains(index);
        }

        /// <summary>某个鼠标键当前是否由注入按住。</summary>
        internal bool IsMouseButtonHeldByInjection(int index)
        {
            return m_heldButtons.Contains(index);
        }

        private IModParentField Fields
        {
            get
            {
                if (m_fields == null)
                    m_fields = ModManager.Instance.ModParentField;
                return m_fields;
            }
        }

        // ---------------------------------------------------------------- 视角

        public object Look(float yawDegrees, float pitchDegrees)
        {
            EnsureEnabled();
            object skipped = SkipLookIfUserOwnsIt();
            if (skipped != null)
                return skipped;
            return OnGameThread(() =>
            {
                ComponentPlayer player = RequirePlayer();
                ApplyYaw(player, DegreesToRadians(yawDegrees));
                ApplyPitch(player, DegreesToRadians(pitchDegrees));
                return DescribeLook(player);
            });
        }

        public object LookDelta(float yawDeltaDegrees, float pitchDeltaDegrees)
        {
            EnsureEnabled();
            object skipped = SkipLookIfUserOwnsIt();
            if (skipped != null)
                return skipped;
            return OnGameThread(() =>
            {
                ComponentPlayer player = RequirePlayer();
                ApplyYaw(player, GetYaw(player) + DegreesToRadians(yawDeltaDegrees));
                ApplyPitch(player, GetPitch(player) + DegreesToRadians(pitchDeltaDegrees));
                return DescribeLook(player);
            });
        }

        /// <summary>看向世界坐标（方块用格子中心，实体用其位置）。瞬时旋转，不受人手速度限制。</summary>
        public object LookAt(float x, float y, float z)
        {
            EnsureEnabled();
            object skipped = SkipLookIfUserOwnsIt();
            if (skipped != null)
                return skipped;
            return OnGameThread(() =>
            {
                ComponentPlayer player = RequirePlayer();
                Vector3 eye = player.ComponentCreatureModel.EyePosition;
                Vector3 direction = new Vector3(x, y, z) - eye;
                if (direction.LengthSquared() < 1e-6f)
                {
                    throw new BridgeCommandException(
                        "invalid_argument", "The target is at the player's eye position.");
                }

                direction = Vector3.Normalize(direction);
                float yaw = SolveYaw(direction);
                ApplyYaw(player, yaw);
                ApplyPitch(player, (float)Math.Asin(MathUtils.Clamp(direction.Y, -1f, 1f)));

                Dictionary<string, object> result = DescribeLook(player);
                result["eye"] = Vector3ToDictionary(eye);
                result["desiredDirection"] = Vector3ToDictionary(direction);
                return result;
            });
        }

        // ---------------------------------------------------------------- 键盘

        /// <summary>脉冲式按键：按下 1 帧（或 holdMs 毫秒）后松开，等价于真人按一下。</summary>
        public object KeyPulse(string keyName, int holdMilliseconds)
        {
            EnsureEnabled();
            Key key = ParseKey(keyName);
            OnGameThread(() =>
            {
                SetKeyHeld(key, true);
                return null;
            });

            if (holdMilliseconds > 0)
                Thread.Sleep(Math.Min(holdMilliseconds, 10000));

            OnGameThread(() =>
            {
                SetKeyHeld(key, false);
                return null;
            });

            return DescribeHeldKeys();
        }

        /// <summary>持续按下 / 释放（移动类必须用这个）。</summary>
        public object KeyHold(string keyName, bool down)
        {
            EnsureEnabled();
            Key key = ParseKey(keyName);
            return OnGameThread(() =>
            {
                SetKeyHeld(key, down);
                return DescribeHeldKeys();
            });
        }

        /// <summary>组合键：先按住修饰键，再脉冲目标键，最后松开修饰键。</summary>
        public object KeyChord(string[] modifierNames, string keyName, int holdMilliseconds)
        {
            EnsureEnabled();
            Key key = ParseKey(keyName);
            var modifiers = new List<Key>();
            for (int i = 0; i < modifierNames.Length; i++)
                modifiers.Add(ParseKey(modifierNames[i]));

            OnGameThread(() =>
            {
                for (int i = 0; i < modifiers.Count; i++)
                    SetKeyHeld(modifiers[i], true);
                return null;
            });

            KeyPulseCore(key, holdMilliseconds);

            OnGameThread(() =>
            {
                for (int i = 0; i < modifiers.Count; i++)
                    SetKeyHeld(modifiers[i], false);
                return DescribeHeldKeys();
            });

            return DescribeHeldKeys();
        }

        /// <summary>逐字符输入（每帧一个字符），供文本框使用。</summary>
        public object TypeText(string text)
        {
            EnsureEnabled();
            if (string.IsNullOrEmpty(text))
                return new Dictionary<string, object>(StringComparer.Ordinal) { ["typed"] = 0 };

            string sanitized = text.Replace("\r", string.Empty).Replace("\n", string.Empty);
            int typed = 0;
            for (int i = 0; i < sanitized.Length; i++)
            {
                char character = sanitized[i];
                OnGameThread(() =>
                {
                    Fields.ModifyStaticField(
                        typeof(Keyboard), InputWhitelist.KeyboardLastChar, (char?)character);
                    return null;
                });
                typed++;
            }

            OnGameThread(() =>
            {
                Fields.ModifyStaticField(typeof(Keyboard), InputWhitelist.KeyboardLastChar, null);
                return null;
            });

            return new Dictionary<string, object>(StringComparer.Ordinal) { ["typed"] = typed };
        }

        // ---------------------------------------------------------------- 鼠标

        /// <summary>世界内鼠标动作：down / up / click / doubleclick。click 为两帧（按下→松开）。</summary>
        public object MouseAction(string buttonName, string action, int holdMilliseconds)
        {
            EnsureEnabled();
            MouseButton button = ParseMouseButton(buttonName);
            string normalized = (action ?? string.Empty).Trim().ToLowerInvariant();

            switch (normalized)
            {
                case "down":
                    return OnGameThread(() =>
                    {
                        SetMouseHeld(button, true);
                        return DescribeHeldButtons();
                    });
                case "up":
                    return OnGameThread(() =>
                    {
                        SetMouseHeld(button, false);
                        return DescribeHeldButtons();
                    });
                case "click":
                    MouseClickCore(button, holdMilliseconds);
                    break;
                case "doubleclick":
                    MouseClickCore(button, holdMilliseconds);
                    Thread.Sleep(60);
                    MouseClickCore(button, holdMilliseconds);
                    break;
                default:
                    throw new BridgeCommandException(
                        "invalid_argument", "action must be down, up, click or doubleclick.");
            }

            return DescribeHeldButtons();
        }

        /// <summary>滚轮：正数向上（快捷栏/列表前进）。</summary>
        public object Wheel(int notches)
        {
            EnsureEnabled();
            return OnGameThread(() =>
            {
                // Mouse.BeforeFrame 会用真实滚轮重算 MouseWheelMovement；
                // 把 m_lastMouseWheelValue 置空可让它跳过本次重算，从而保留我们写入的值。
                // Source: Engine/Engine/Input/Mouse.cs:Mouse.BeforeFrame
                Fields.ModifyStaticField(
                    typeof(Mouse), InputWhitelist.MouseLastWheelValue, null);
                Fields.ModifyStaticField(
                    typeof(Mouse), InputWhitelist.MouseWheelMovement, notches * 120);

                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["notches"] = notches,
                    ["raw"] = notches * 120
                };
            });
        }

        // ---------------------------------------------------------------- UI 点击

        /// <summary>
        /// 引擎内 UI 点击（不需要 OS 鼠标）。
        /// 逐级不可跳的三重保证：目标必须存在（否则 element_missing）、必须当前可命中
        /// （否则 element_occluded 并回报遮挡者）、不提供任何切屏捷径。
        /// </summary>
        /// <summary>
        /// 列表行的**当前**客户区坐标（按序号或文字找行）。给"记行不记像素"的回放用：
        /// 窗口改过大小、UI 缩放过之后，坐标现算才不会点到别的地方。
        /// </summary>
        public Vector2? ResolveListRowPoint(string selector, int rowIndex, string rowText)
        {
            object result = OnGameThread(() =>
            {
                ContainerWidget root = ScreensManager.RootWidget;
                if (root == null || string.IsNullOrEmpty(selector))
                    return null;
                Widget list = UiInspector.Resolve(root, selector, false, default(Vector2));
                if (list == null)
                    return null;
                Vector2 clientPoint;
                int index;
                string text;
                if (!UiInspector.TryResolveListRow(list, rowIndex, rowText, out clientPoint,
                    out index, out text))
                {
                    return null;
                }
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["selector"] = selector,
                    ["index"] = index,
                    ["text"] = text,
                    ["clientPoint"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["x"] = clientPoint.X,
                        ["y"] = clientPoint.Y
                    },
                    ["value"] = clientPoint
                };
            });
            var info = result as Dictionary<string, object>;
            if (info == null)
                return null;
            object value;
            return info.TryGetValue("value", out value) ? (Vector2?)value : null;
        }

        /// <summary>
        /// `act.uiclick` 的实现：**走 CM-1 会话**（一步一帧），按下与抬起天然落在不同帧 ——
        /// 引擎的 `WidgetInput` 才派生得出 Click。
        ///
        /// 为什么不再用"按下 → 释放"的三段式直注入：那三段在同一帧里跑完（命令若在游戏线程
        /// 派发更是立即执行），实测就是"命令返回成功、界面纹丝不动"。会话路径是动作包回放
        /// 一直在用的那条，稳定。
        ///
        /// 目标定位优先级（用户要求：能用 UI 的真实位置就别用录下来的像素）：
        /// 列表行（`row`/`text`）→ 控件选择器/路径 → 坐标兜底。
        /// </summary>
        public object UiClickSession(string selector, bool hasPoint, float pointX, float pointY,
            int rowIndex, string rowText, int holdMilliseconds)
        {
            EnsureEnabled();

            // 记进"本帧 UI 动作"，供动作包录制：列表行记语义目标，其余记选择器/坐标。
            if (rowIndex >= 0 || !string.IsNullOrEmpty(rowText))
            {
                NoteUiAction("click:list:" + selector
                    + (string.IsNullOrEmpty(rowText) ? "#" + rowIndex : "@" + rowText));
            }
            else
            {
                NoteUiAction(hasPoint
                    ? "click:" + pointX.ToString("0.##") + "," + pointY.ToString("0.##")
                    : "click:" + selector);
            }

            // 目标信息（给调用方看"点到了什么"）+ 现算的落点
            object resolved = OnGameThread(() =>
            {
                ContainerWidget root = ScreensManager.RootWidget;
                if (root == null)
                    throw new BridgeCommandException("not_ready", "The UI is not ready yet.");

                Widget target;
                Vector2 point;
                if (rowIndex >= 0 || !string.IsNullOrEmpty(rowText))
                {
                    Widget list = UiInspector.Resolve(root, selector, false, default(Vector2));
                    if (list == null)
                        throw new BridgeCommandException("element_missing",
                            "no list matches '" + (selector ?? "?") + "'.");
                    Vector2 rowPoint;
                    int index;
                    string text;
                    if (!UiInspector.TryResolveListRow(list, rowIndex, rowText, out rowPoint,
                        out index, out text))
                    {
                        throw new BridgeCommandException("element_missing",
                            "no such row in '" + selector + "' (row=" + rowIndex
                            + " text=" + (rowText ?? "<none>") + ").");
                    }
                    target = list;
                    point = rowPoint;
                }
                else if (hasPoint)
                {
                    target = root.HitTestGlobal(new Vector2(pointX, pointY));
                    point = new Vector2(pointX, pointY);
                    if (target == null)
                        throw new BridgeCommandException("element_missing",
                            "Nothing is under (" + pointX.ToString("0.##") + ","
                            + pointY.ToString("0.##") + ").");
                }
                else
                {
                    target = UiInspector.Resolve(root, selector, false, default(Vector2));
                    if (target == null)
                        throw new BridgeCommandException("element_missing",
                            "no element matches '" + (selector ?? "?") + "'.");
                    point = UiInspector.CenterOf(target);
                }

                Dictionary<string, object> info = UiInspector.DescribeElement(target, 0);
                info["clickPoint"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = point.X,
                    ["y"] = point.Y
                };
                info["value"] = point;
                return info;
            });

            var described = resolved as Dictionary<string, object>;
            if (described == null)
                throw new BridgeCommandException("failed", "could not resolve the click target.");
            var clickPoint = (Vector2)described["value"];
            described.Remove("value");

            Session.Begin(false);
            Session.MoveTo(clickPoint, 1);
            Session.Press(MouseButton.Left);
            Session.Release(MouseButton.Left);
            Session.End();
            Session.WaitUntilIdle(Math.Max(4000, holdMilliseconds + 4000));
            return described;
        }

        public object UiClick(
            string selector, int holdMilliseconds, bool hasPoint, float pointX, float pointY,
            int rowIndex = -1, string rowText = null)
        {
            EnsureEnabled();

            // 列表行：先把"哪一行"的**当前**坐标算出来，再当成坐标点击走同一条路。
            // 记进录制事件时仍然记语义目标（`list:列表@文字`），回放时才与窗口尺寸无关。
            if (rowIndex >= 0 || !string.IsNullOrEmpty(rowText))
            {
                Vector2? rowPoint = ResolveListRowPoint(selector, rowIndex, rowText);
                if (!rowPoint.HasValue)
                {
                    throw new BridgeCommandException("element_missing",
                        "no such row in list '" + (selector ?? "?") + "' (row=" + rowIndex
                        + " text=" + (rowText ?? "<none>") + ")");
                }
                hasPoint = true;
                pointX = rowPoint.Value.X;
                pointY = rowPoint.Value.Y;
                // 事件语法：`list:<选择器>#<行号>` 或 `list:<选择器>@<文字>`。
                // 回放端（PlayerAiMod 的 `UiClickTarget`）按同一套语法解析再现算坐标 ——
                // 两边是同一份约定，改一处要改另一处。
                NoteUiAction("click:list:" + selector
                    + (string.IsNullOrEmpty(rowText) ? "#" + rowIndex : "@" + rowText));
            }
            else
            {
                // 记进"本帧 UI 动作"：动作包录制靠它把菜单点击录成语义事件。
                // 带坐标的点击（列表行这类"不是控件"的目标）记坐标，否则记选择器 ——
                // 回放时按同样的语义还原（选择器点击 / 坐标点击）。
                NoteUiAction(hasPoint
                    ? "click:" + pointX.ToString("0.##") + "," + pointY.ToString("0.##")
                    : "click:" + selector);
            }

            object info = OnGameThread(() => ResolveAndPress(selector, hasPoint, pointX, pointY));

            if (holdMilliseconds > 0)
                Thread.Sleep(Math.Min(holdMilliseconds, 10000));

            OnGameThread(() =>
            {
                WidgetInput input = m_clickInput;
                if (input != null)
                {
                    input.UseSoftMouseCursor = true;
                    input.MousePosition = m_clickPoint;
                }
                SetMouseHeld(m_clickButton, false);
                return null;
            });

            // 第三帧：还原软光标，并清掉 WidgetInput 里残留的"按下起点"。
            //
            // 为什么必须清：WidgetInput.UpdateInputFromMouse 只有在"左右键都没按下"时才会
            // 把 m_mouseDownPoint 置空；一旦它残留，且左键为松开态，就会在后续每一帧继续派生
            // Click（Click = Segment(m_mouseDownPoint, MousePosition)），使一次合成点击被消费多次。
            // Source: Survivalcraft/Game/WidgetInput.cs:755-802
            //
            // 只在"当前没有任何鼠标键处于按下状态"时清理，避免破坏玩家自己正在按住的动作（如挖方块）。
            OnGameThread(() =>
            {
                WidgetInput input = m_clickInput;
                if (input != null)
                {
                    input.UseSoftMouseCursor = false;
                    if (m_heldButtons.Count == 0)
                        ClearWidgetMouseDownPoint(input);
                    m_clickInput = null;
                }
                return null;
            });

            return info;
        }

        // ---------------------------------------------------------------- 释放

        public object ReleaseAll()
        {
            OnGameThread(() =>
            {
                ReleaseAllCore();
                return null;
            });
            return DescribeHeldKeys();
        }

        /// <summary>卸载/关停时直接释放（不经过帧首队列，避免游戏已停止推进时残留按键）。</summary>
        public void ReleaseAllDirect()
        {
            try
            {
                Session?.EndImmediate();
                ReleaseAllCore();
            }
            catch (Exception exception)
            {
                Log.Warning("[CmdBridge] ReleaseAllDirect failed: " + exception.Message);
            }
        }

        // ---------------------------------------------------------------- 内部实现

        private object OnGameThread(Func<object> action)
        {
            return m_invoker.Invoke(action);
        }

        /// <summary>在游戏线程执行并等待结果（供会话解析元素等同步用途）。</summary>
        internal object RunOnFrame(Func<object> action)
        {
            return OnGameThread(action);
        }

        /// <summary>根控件树的 WidgetInput：软光标、光标可见性等会话级状态写在它上面。</summary>
        internal WidgetInput GetRootWidgetInput()
        {
            try
            {
                ContainerWidget root = ScreensManager.RootWidget;
                return root != null ? root.WidgetsHierarchyInput : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 清掉 WidgetInput 里残留的"按下起点"。
        /// 不清的后果：Click = Segment(m_mouseDownPoint, MousePosition) 会在后续每一帧继续派生，
        /// 一次合成就被消费多次（Source: Game/WidgetInput.cs:755-802）。
        /// </summary>
        internal void ClearWidgetMouseDownPoint(WidgetInput input)
        {
            if (input == null)
                return;
            try
            {
                Fields.ModifyParentField(
                    input,
                    InputWhitelist.WidgetInputMouseDownPoint,
                    null,
                    typeof(WidgetInput));
            }
            catch (Exception exception)
            {
                Log.Warning("[CmdBridge] Failed to clear WidgetInput.m_mouseDownPoint: "
                    + exception.Message);
            }
        }

        /// <summary>由 Mod 的 Frame.Update 钩子每帧调用一次，驱动帧首泵。</summary>
        internal void SignalFrameEnd()
        {
            Pump.SignalFrameEnd();
        }

        /// <summary>
        /// 视角仲裁（CM-2）：真实焦点在游戏、且最近有真实鼠标活动时，视角归用户。
        /// 放在注入器里是为了让命令面与门面（其他 Mod）都自动遵守，绕不过去。
        /// 返回非 null 表示"本次跳过"（契约：`skipped` 字段）。
        /// </summary>
        private object SkipLookIfUserOwnsIt()
        {
            FocusPolicy focus = Focus;
            if (focus == null || !focus.LookOwnedByUser)
                return null;

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["skipped"] = "look_owned_by_user",
                ["lookOwner"] = focus.LookOwnerName,
                ["hint"] = "The player is moving a real mouse, so look ownership is with the user. "
                    + "Use look.owner=ai to force or look.owner=shared to combine."
            };
        }

        private void EnsureEnabled()
        {
            if (!m_config.EnableInputInjection)
            {
                throw new BridgeCommandException(
                    "injection_disabled",
                    "Input injection is disabled by CmdBridge.json (enableInputInjection=false).");
            }
        }

        private object ResolveAndPress(
            string selector, bool hasPoint, float pointX, float pointY)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                throw new BridgeCommandException("not_ready", "The UI is not ready yet.");
            if (ScreensManager.IsAnimating)
            {
                throw new BridgeCommandException(
                    "screen_busy", "A screen transition is in progress.");
            }

            Vector2 requestedPoint = new Vector2(pointX, pointY);
            // 指定坐标有两个用途：虚拟列表项定位，以及同名兄弟元素消歧。
            // 只给坐标不给选择器（`ui.click x=… y=…` / 动作包里的坐标点击）时，
            // 目标就是"这一点下面的控件" —— 等价于真人用软光标点这个像素，不跳级、不猜。
            Widget target = string.IsNullOrEmpty(selector) && hasPoint
                ? root.HitTestGlobal(requestedPoint)
                : UiInspector.Resolve(root, selector, hasPoint, requestedPoint);
            if (target == null)
            {
                throw new BridgeCommandException(
                    "element_missing", "Nothing is under (" + pointX.ToString("0.##") + ","
                    + pointY.ToString("0.##") + ").");
            }
            BoundingRectangle bounds = target.GlobalBounds;
            Vector2 point = hasPoint
                ? requestedPoint
                : new Vector2(
                    (bounds.Min.X + bounds.Max.X) * 0.5f,
                    (bounds.Min.Y + bounds.Max.Y) * 0.5f);

            Widget hit = root.HitTestGlobal(point);
            if (!IsSelfOrDescendant(hit, target))
            {
                throw new BridgeCommandException(
                    "element_occluded",
                    "The element is not clickable right now; blocked by " + UiInspector.ShortName(hit) + ".");
            }

            WidgetInput input = target.Input;
            if (input == null)
                throw new BridgeCommandException("element_missing", "The element has no input surface.");

            input.UseSoftMouseCursor = true;
            input.MousePosition = point;

            m_clickInput = input;
            m_clickButton = MouseButton.Left;
            m_clickPoint = point;
            SetMouseHeld(MouseButton.Left, true);

            Dictionary<string, object> info = UiInspector.DescribeElement(target, 0);
            info["clickPoint"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = point.X,
                ["y"] = point.Y
            };
            return info;
        }

        private void MouseClickCore(MouseButton button, int holdMilliseconds)
        {
            OnGameThread(() =>
            {
                SetMouseHeld(button, true);
                return null;
            });

            if (holdMilliseconds > 0)
                Thread.Sleep(Math.Min(holdMilliseconds, 10000));

            OnGameThread(() =>
            {
                SetMouseHeld(button, false);
                return null;
            });
        }

        private void KeyPulseCore(Key key, int holdMilliseconds)
        {
            OnGameThread(() =>
            {
                SetKeyHeld(key, true);
                return null;
            });

            if (holdMilliseconds > 0)
                Thread.Sleep(Math.Min(holdMilliseconds, 10000));

            OnGameThread(() =>
            {
                SetKeyHeld(key, false);
                return null;
            });
        }

        internal void SetKeyHeld(Key key, bool down)
        {
            EnsureKeyboardArrays();
            int index = (int)key;
            if (index < 0 || index >= m_keysDown.Length)
                throw new BridgeCommandException("invalid_argument", "Unknown key: " + key + ".");

            bool wasHeld = m_heldKeys.Contains(index);
            m_keysDown[index] = down;

            if (down)
            {
                if (!wasHeld)
                {
                    // 只在下按的那一帧写 -1：Keyboard.AfterFrame 会把它转换成真实的连发计时，
                    // 每帧重写会不断重置计时导致永远不连发。
                    // Source: Engine/Engine/Input/Keyboard.cs:154-160
                    m_keysRepeat[index] = -1.0;
                    m_keysDownOnce[index] = true;
                    m_heldKeys.Add(index);
                }
                Fields.ModifyStaticField(typeof(Keyboard), InputWhitelist.KeyboardLastKey, (Key?)key);
            }
            else
            {
                m_heldKeys.Remove(index);
                m_keysRepeat[index] = 0.0;
            }
        }

        internal void SetMouseHeld(MouseButton button, bool down)
        {
            EnsureMouseArrays();
            int index = (int)button;
            if (index < 0 || index >= m_mouseDown.Length)
                throw new BridgeCommandException("invalid_argument", "Unknown mouse button.");

            m_mouseDown[index] = down;
            if (down)
            {
                m_mouseDownOnce[index] = true;
                m_heldButtons.Add(index);
            }
            else
            {
                m_heldButtons.Remove(index);
            }
        }

        /// <summary>
        /// 只写 `downOnce` 数组（**不动** down 数组，也**不登记**为"注入按住"）。
        ///
        /// 给"单帧合成点击"用：引擎的 `Click` 派生要求"这一帧没按着 + 上一帧留了按下起点"，
        /// 而同帧按下再抬起是看不到先后的 —— 所以只在 downOnce 上留"按过一下"，
        /// `Tap` 与 `Click` 才会在同一帧成立（Source: Game/WidgetInput.cs:735-769）。
        ///
        /// ⚠️ **绝不能**把这次按下登记进 `m_heldButtons`：共控合并（`focus.attach` / `auto` 且真实焦点在游戏时）
        /// 每帧都会把 down 数组重算成 `IsMouseButtonHeldByInjection(i) || 真实按下`
        /// （`FocusPolicy.MergeInjectedWithReal`），登记成 held 就等于告诉它"这个键还按着"，
        /// down 数组被抬成 true → `Click` 分支的"没按着"不成立 → 界面纹丝不动。
        /// （实测：`auto` 模式下真实焦点恰好在游戏里时第一次点击无效，`focus.detach` 之后同样的调用就好了。）
        /// </summary>
        internal void SetMouseDownOnce(MouseButton button, bool once)
        {
            EnsureMouseArrays();
            int index = (int)button;
            if (index < 0 || index >= m_mouseDownOnce.Length)
                throw new BridgeCommandException("invalid_argument", "Unknown mouse button.");

            m_mouseDownOnce[index] = once;
            if (!once)
                m_heldButtons.Remove(index);
        }

        /// <summary>
        /// **只释放"本 Mod 注入并按住"的键与鼠标**（不动设备数组里的其它内容）。
        ///
        /// 为什么要单独一个窄版本：`ReleaseAll()` 会把引擎的按键/鼠标数组**整体清零**，
        /// 而真实鼠标的"按下沿"（`m_mouseButtonsDownOnceArray`）就写在同一个数组里 ——
        /// 被清一次，`WidgetInput.UpdateInputFromMouse` 就留不下 `m_mouseDownPoint`，
        /// 松手时派生不出 `Click`，玩家看到的是"按钮有按下效果但点不动"。
        ///
        /// 例行释放（任务收尾、世界卸载、回放结束、主菜单兜底）要的只是"别再按着 W 了"，
        /// 放掉我们自己按住的那些就够；`ReleaseAll()` 留给"彻底停手"（Mod 卸载等）。
        /// </summary>
        internal object ReleaseInjectedOnly()
        {
            EnsureKeyboardArrays();
            EnsureMouseArrays();

            int keys = 0;
            if (m_keysDown != null)
            {
                foreach (int index in m_heldKeys)
                {
                    if (index < 0 || index >= m_keysDown.Length)
                        continue;
                    m_keysDown[index] = false;
                    m_keysDownOnce[index] = false;
                    m_keysRepeat[index] = 0.0;
                    keys++;
                }
            }

            int buttons = 0;
            if (m_mouseDown != null)
            {
                foreach (int index in m_heldButtons)
                {
                    if (index < 0 || index >= m_mouseDown.Length)
                        continue;
                    m_mouseDown[index] = false;
                    m_mouseDownOnce[index] = false;
                    buttons++;
                }
            }

            m_heldKeys.Clear();
            m_heldButtons.Clear();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["releasedKeys"] = keys,
                ["releasedButtons"] = buttons,
                ["scope"] = "injected-only"
            };
        }

        private void ReleaseAllCore()
        {
            if (m_keysDown != null)
            {
                for (int i = 0; i < m_keysDown.Length; i++)
                {
                    m_keysDown[i] = false;
                    m_keysDownOnce[i] = false;
                    m_keysRepeat[i] = 0.0;
                }
            }
            if (m_mouseDown != null)
            {
                for (int i = 0; i < m_mouseDown.Length; i++)
                {
                    m_mouseDown[i] = false;
                    m_mouseDownOnce[i] = false;
                }
            }
            m_heldKeys.Clear();
            m_heldButtons.Clear();

            try
            {
                Fields.ModifyStaticField(typeof(Keyboard), InputWhitelist.KeyboardLastKey, null);
                Fields.ModifyStaticField(typeof(Keyboard), InputWhitelist.KeyboardLastChar, null);
            }
            catch
            {
            }

            if (m_clickInput != null)
            {
                m_clickInput.UseSoftMouseCursor = false;
                m_clickInput = null;
            }
        }

        private void EnsureKeyboardArrays()
        {
            if (m_keysDown != null)
                return;
            m_keysDown = Fields.GetStaticField<bool[]>(typeof(Keyboard), InputWhitelist.KeyboardDownArray);
            m_keysDownOnce = Fields.GetStaticField<bool[]>(typeof(Keyboard), InputWhitelist.KeyboardDownOnceArray);
            m_keysRepeat = Fields.GetStaticField<double[]>(typeof(Keyboard), InputWhitelist.KeyboardRepeatArray);
        }

        private void EnsureMouseArrays()
        {
            if (m_mouseDown != null)
                return;
            m_mouseDown = Fields.GetStaticField<bool[]>(typeof(Mouse), InputWhitelist.MouseDownArray);
            m_mouseDownOnce = Fields.GetStaticField<bool[]>(typeof(Mouse), InputWhitelist.MouseDownOnceArray);
        }

        // ---------------------------------------------------------------- 视角工具

        private void ApplyYaw(ComponentPlayer player, float yawRadians)
        {
            // Source: Survivalcraft/Game/ComponentLocomotion.cs:302-309
            // 玩家水平朝向就是身体旋转（游戏自身也是这么写的）。
            player.ComponentBody.Rotation =
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRadians);
        }

        private void ApplyPitch(ComponentPlayer player, float pitchRadians)
        {
            // Source: Survivalcraft/Game/ComponentLocomotion.cs:96-108
            // LookAngles 的 setter 是 private，且带 clamp（X∈±140°, Y∈±82°）；这里写后端字段并自行 clamp。
            float pitch = MathUtils.Clamp(pitchRadians, -MaxPitchRadians, MaxPitchRadians);
            Vector2 angles = new Vector2(
                MathUtils.Clamp(GetHeadYaw(player), -MaxHeadYawRadians, MaxHeadYawRadians),
                pitch);
            Fields.ModifyParentField(
                player.ComponentLocomotion,
                InputWhitelist.LocomotionLookAngles,
                angles,
                typeof(ComponentLocomotion));
        }

        private float GetHeadYaw(ComponentPlayer player)
        {
            Vector2 angles = Fields.GetParentField<Vector2>(
                player.ComponentLocomotion,
                InputWhitelist.LocomotionLookAngles,
                typeof(ComponentLocomotion));
            return angles.X;
        }

        private float GetPitch(ComponentPlayer player)
        {
            Vector2 angles = Fields.GetParentField<Vector2>(
                player.ComponentLocomotion,
                InputWhitelist.LocomotionLookAngles,
                typeof(ComponentLocomotion));
            return angles.Y;
        }

        private static float GetYaw(ComponentPlayer player)
        {
            Quaternion rotation = player.ComponentBody.Rotation;
            // Source: Survivalcraft/Game/ComponentLocomotion.cs:303（游戏自身的 yaw 提取公式）
            return MathUtils.Atan2(
                2f * rotation.Y * rotation.W - 2f * rotation.X * rotation.Z,
                1f - 2f * rotation.Y * rotation.Y - 2f * rotation.Z * rotation.Z);
        }

        /// <summary>
        /// 求"让视线朝向 direction（水平分量）"所需的 yaw。
        /// 不猜引擎的朝向约定，而是用引擎自己的 Matrix.Forward 做全局搜索，因此永远与游戏一致。
        /// </summary>
        private static float SolveYaw(Vector3 direction)
        {
            Vector3 flat = new Vector3(direction.X, 0f, direction.Z);
            if (flat.LengthSquared() < 1e-8f)
                flat = new Vector3(0f, 0f, 1f);
            flat = Vector3.Normalize(flat);

            float best = 0f;
            float bestDot = -2f;
            for (int pass = 0; pass < 3; pass++)
            {
                float span = pass == 0 ? MathUtils.PI : (pass == 1 ? 0.2f : 0.01f);
                int steps = pass == 0 ? 36 : 20;
                for (int i = -steps; i <= steps; i++)
                {
                    float candidate = best + span * i / steps;
                    Vector3 forward = Matrix.CreateFromQuaternion(
                        Quaternion.CreateFromAxisAngle(Vector3.UnitY, candidate)).Forward;
                    float dot = forward.X * flat.X + forward.Z * flat.Z;
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        best = candidate;
                    }
                }
            }
            return best;
        }

        private Dictionary<string, object> DescribeLook(ComponentPlayer player)
        {
            float radiansToDegrees = 180f / MathUtils.PI;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["yawDeg"] = GetYaw(player) * radiansToDegrees,
                ["pitchDeg"] = GetPitch(player) * radiansToDegrees,
                ["position"] = Vector3ToDictionary(player.ComponentBody.Position)
            };
        }

        private Dictionary<string, object> DescribeHeldKeys()
        {
            var keys = new List<string>();
            foreach (int index in m_heldKeys)
                keys.Add(((Key)index).ToString());
            keys.Sort(StringComparer.Ordinal);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["heldKeys"] = keys
            };
        }

        private Dictionary<string, object> DescribeHeldButtons()
        {
            var buttons = new List<string>();
            foreach (int index in m_heldButtons)
                buttons.Add(((MouseButton)index).ToString());
            buttons.Sort(StringComparer.Ordinal);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["heldButtons"] = buttons
            };
        }

        internal static Dictionary<string, object> Vector3ToDictionary(Vector3 value)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = value.X,
                ["y"] = value.Y,
                ["z"] = value.Z
            };
        }

        private static float DegreesToRadians(float degrees)
        {
            return degrees * (MathUtils.PI / 180f);
        }

        /// <summary>取第一个玩家；没有世界/没有玩家时返回 null（不抛异常，供只读观察用）。</summary>
        internal static ComponentPlayer TryGetPlayer()
        {
            try
            {
                if (GameManager.Project == null)
                    return null;
                SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
                if (players == null || players.ComponentPlayers.Count == 0)
                    return null;
                return players.ComponentPlayers[0];
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static ComponentPlayer RequirePlayer()
        {
            if (GameManager.Project == null)
                throw new BridgeCommandException("world_not_loaded", "No world is loaded.");
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null || players.ComponentPlayers.Count == 0)
                throw new BridgeCommandException("player_not_found", "No player is available.");
            return players.ComponentPlayers[0];
        }

        private static bool IsSelfOrDescendant(Widget candidate, Widget ancestor)
        {
            for (Widget widget = candidate; widget != null; widget = widget.ParentWidget)
            {
                if (ReferenceEquals(widget, ancestor))
                    return true;
            }
            return false;
        }

        private static Key ParseKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new BridgeCommandException("invalid_argument", "A key name is required.");
            if (Enum.TryParse(name.Trim(), true, out Key key) && Enum.IsDefined(typeof(Key), key))
                return key;
            throw new BridgeCommandException("invalid_argument", "Unknown key: " + name + ".");
        }

        private static MouseButton ParseMouseButton(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return MouseButton.Left;
            if (Enum.TryParse(name.Trim(), true, out MouseButton button) &&
                Enum.IsDefined(typeof(MouseButton), button))
            {
                return button;
            }
            throw new BridgeCommandException("invalid_argument", "Unknown mouse button: " + name + ".");
        }
    }
}
