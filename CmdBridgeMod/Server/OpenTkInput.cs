using System;
using System.Collections.Generic;
using System.Reflection;
using Engine.Input;

namespace CmdBridgeMod
{
    /// <summary>
    /// OpenTK 的**反射门面**。
    ///
    /// 背景（实测）：Android 端不部署 OpenTK，只要 Mod 里存在**以 OpenTK 类型声明的字段或参数**，
    /// 加载时就会抛
    /// `Could not load type of field 'A.F:A' (13) … Could not load file or assembly 'OpenTK'`，
    /// 整个 Mod 直接加载失败。所以这里把 OpenTK 访问全部收进反射：
    /// 类型不存在时 <see cref="Available"/> 为 false，调用方跳过"真实输入合并"等桌面专属能力，
    /// 而不是让 Mod 崩掉。
    ///
    /// 桌面（Windows）行为不变：OpenTK 在，反射解析成功，语义与原先的直接调用一致。
    /// Source: Engine/Engine/OpenTK.dll（桌面自带）；Engine/Engine/Input/Keyboard.cs（同名枚举映射）
    /// </summary>
    internal static class OpenTkInput
    {
        private static readonly object Sync = new object();
        private static bool m_resolved;
        private static bool m_available;

        private static Type m_keyboardType;
        private static Type m_keyboardStateType;
        private static Type m_mouseType;
        private static Type m_mouseStateType;
        private static Type m_keyType;
        private static Type m_mouseButtonType;
        private static Type m_gameWindowType;

        private static MethodInfo m_keyboardGetState;
        private static MethodInfo m_mouseGetState;
        private static PropertyInfo m_keyboardIndexer;
        private static PropertyInfo m_mouseIndexer;
        private static PropertyInfo m_mouseXProperty;
        private static PropertyInfo m_mouseYProperty;
        private static FieldInfo m_mouseXField;
        private static FieldInfo m_mouseYField;
        private static PropertyInfo m_focusedProperty;
        private static PropertyInfo m_windowInfoProperty;
        private static PropertyInfo m_windowHandleProperty;
        private static FieldInfo m_windowHandleField;

        private static readonly Dictionary<string, object> KeyCache = new Dictionary<string, object>();
        private static readonly Dictionary<string, object> ButtonCache = new Dictionary<string, object>();

        /// <summary>OpenTK 是否可用（Android 上为 false）。</summary>
        public static bool Available
        {
            get
            {
                EnsureResolved();
                return m_available;
            }
        }

        /// <summary>判断引擎 `Window.m_gameWindow` 的值是不是 OpenTK 的 GameWindow。</summary>
        public static bool IsGameWindow(object value)
        {
            EnsureResolved();
            return value != null && m_gameWindowType != null &&
                m_gameWindowType.IsInstanceOfType(value);
        }

        /// <summary>OpenTK 全局键盘状态（boxed `KeyboardState`）；不可用时返回 null。</summary>
        public static object GetKeyboardState()
        {
            EnsureResolved();
            if (!m_available || m_keyboardGetState == null)
                return null;
            try
            {
                return m_keyboardGetState.Invoke(null, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>OpenTK 全局鼠标状态（boxed `MouseState`）；不可用时返回 null。</summary>
        public static object GetMouseState()
        {
            EnsureResolved();
            if (!m_available || m_mouseGetState == null)
                return null;
            try
            {
                return m_mouseGetState.Invoke(null, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>`KeyboardState[Engine.Key]`；名字对不上或不可用一律当作未按下（安全）。</summary>
        public static bool IsKeyDown(object keyboardState, Key key)
        {
            if (keyboardState == null || m_keyboardIndexer == null || m_keyType == null)
                return false;
            object parsed = ParseEnum(KeyCache, m_keyType, key.ToString());
            if (parsed == null)
                return false;
            try
            {
                object value = m_keyboardIndexer.GetValue(keyboardState, new[] { parsed });
                return value is bool down && down;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>`MouseState[Engine.MouseButton]`。</summary>
        public static bool IsMouseButtonDown(object mouseState, MouseButton button)
        {
            if (mouseState == null || m_mouseIndexer == null || m_mouseButtonType == null)
                return false;
            object parsed = ParseEnum(ButtonCache, m_mouseButtonType, button.ToString());
            if (parsed == null)
                return false;
            try
            {
                object value = m_mouseIndexer.GetValue(mouseState, new[] { parsed });
                return value is bool down && down;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>OpenTK 全局光标的屏幕坐标。</summary>
        public static bool TryGetMousePosition(object mouseState, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (mouseState == null)
                return false;
            try
            {
                if (m_mouseXField != null && m_mouseYField != null)
                {
                    x = Convert.ToInt32(m_mouseXField.GetValue(mouseState));
                    y = Convert.ToInt32(m_mouseYField.GetValue(mouseState));
                    return true;
                }
                if (m_mouseXProperty != null && m_mouseYProperty != null)
                {
                    x = Convert.ToInt32(m_mouseXProperty.GetValue(mouseState));
                    y = Convert.ToInt32(m_mouseYProperty.GetValue(mouseState));
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>`GameWindow.Focused`（OpenTK 的窗口焦点）。</summary>
        public static bool TryGetWindowFocused(object gameWindow, out bool focused)
        {
            focused = false;
            if (gameWindow == null || m_focusedProperty == null)
                return false;
            try
            {
                object value = m_focusedProperty.GetValue(gameWindow);
                if (value is bool flag)
                {
                    focused = flag;
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>游戏窗口的 OS 句柄（`GameWindow.WindowInfo.Handle`）；取不到返回 Zero。</summary>
        public static IntPtr GetWindowHandle(object gameWindow)
        {
            EnsureResolved();
            if (gameWindow == null)
                return IntPtr.Zero;
            try
            {
                object windowInfo = m_windowInfoProperty?.GetValue(gameWindow);
                if (windowInfo == null)
                    return IntPtr.Zero;
                if (m_windowHandleProperty != null)
                {
                    object handle = m_windowHandleProperty.GetValue(windowInfo);
                    if (handle is IntPtr pointer)
                        return pointer;
                }
                if (m_windowHandleField != null)
                {
                    object handle = m_windowHandleField.GetValue(windowInfo);
                    if (handle is IntPtr pointer)
                        return pointer;
                }
            }
            catch (Exception)
            {
            }
            return IntPtr.Zero;
        }

        private static object ParseEnum(Dictionary<string, object> cache, Type enumType, string name)
        {
            lock (cache)
            {
                if (cache.TryGetValue(name, out object cached))
                    return cached;
            }
            object parsed = null;
            try
            {
                if (Enum.IsDefined(enumType, name))
                    parsed = Enum.Parse(enumType, name);
            }
            catch (Exception)
            {
            }
            lock (cache)
            {
                cache[name] = parsed;
            }
            return parsed;
        }

        private static void EnsureResolved()
        {
            lock (Sync)
            {
                if (m_resolved)
                    return;
                m_resolved = true;
                try
                {
                    Assembly assembly = Assembly.Load("OpenTK");
                    if (assembly == null)
                        return;

                    m_keyboardType = assembly.GetType("OpenTK.Input.Keyboard");
                    m_keyboardStateType = assembly.GetType("OpenTK.Input.KeyboardState");
                    m_mouseType = assembly.GetType("OpenTK.Input.Mouse");
                    m_mouseStateType = assembly.GetType("OpenTK.Input.MouseState");
                    m_keyType = assembly.GetType("OpenTK.Input.Key");
                    m_mouseButtonType = assembly.GetType("OpenTK.Input.MouseButton");
                    m_gameWindowType = assembly.GetType("OpenTK.GameWindow");

                    m_keyboardGetState = m_keyboardType?.GetMethod("GetState", Type.EmptyTypes);
                    m_mouseGetState = m_mouseType?.GetMethod("GetState", Type.EmptyTypes);

                    m_keyboardIndexer = m_keyType == null
                        ? null
                        : m_keyboardStateType?.GetProperty("Item", new[] { m_keyType });
                    m_mouseIndexer = m_mouseButtonType == null
                        ? null
                        : m_mouseStateType?.GetProperty("Item", new[] { m_mouseButtonType });

                    m_mouseXField = m_mouseStateType?.GetField("X");
                    m_mouseYField = m_mouseStateType?.GetField("Y");
                    m_mouseXProperty = m_mouseStateType?.GetProperty("X");
                    m_mouseYProperty = m_mouseStateType?.GetProperty("Y");

                    m_focusedProperty = m_gameWindowType?.GetProperty("Focused");
                    m_windowInfoProperty = m_gameWindowType?.GetProperty("WindowInfo");
                    Type windowInfoType = m_windowInfoProperty?.PropertyType;
                    if (windowInfoType != null)
                    {
                        m_windowHandleProperty = windowInfoType.GetProperty("Handle");
                        m_windowHandleField = windowInfoType.GetField("Handle");
                    }

                    m_available = m_keyboardType != null && m_mouseType != null;
                }
                catch (Exception)
                {
                    m_available = false;
                }
            }
        }
    }
}
