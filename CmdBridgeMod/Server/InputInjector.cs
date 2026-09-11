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

        public InputInjector(CmdBridgeConfig config, GameThreadInvoker invoker)
        {
            m_config = config;
            m_invoker = invoker ?? new GameThreadInvoker(config.RequestTimeoutSeconds);
        }

        public bool Enabled => m_config.EnableInputInjection;

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
        public object UiClick(
            string selector, int holdMilliseconds, bool hasPoint, float pointX, float pointY)
        {
            EnsureEnabled();
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
                    {
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
            Widget target = UiInspector.Resolve(root, selector, hasPoint, requestedPoint);
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

        private void SetKeyHeld(Key key, bool down)
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

        private void SetMouseHeld(MouseButton button, bool down)
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
