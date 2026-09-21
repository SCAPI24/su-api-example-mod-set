using System;
using System.Reflection;
using Engine;
using Engine.Input;

namespace CmdBridgeMod
{
    /// <summary>
    /// Android 触摸注入（反射 `Engine.Input.Touch` 的私有入口）。
    ///
    /// 为什么必须有它：Android 的界面与世界内 HUD 由**触摸**驱动 —— `WidgetInput` 从
    /// `TouchLocations` 派生 Tap/Click/Drag，桌面那条"软鼠标 + downOnce"路径在 Android 上
    /// 什么也派生不出来（软光标在 Android 上也不显示）。
    ///
    /// 入口在引擎里是 private static（`Engine/Engine/Input/Touch.Android.cs:108-151`、
    /// 桌面 `Touch.cs` 同名），所以用反射调用。Android 上这两个方法自带前置条件
    /// `Window.IsActive && !Keyboard.IsKeyboardVisible`（`Touch.Android.cs:115`）——
    /// 即游戏在前台且软键盘收起时触摸才生效（Android 上游戏始终前台、软键盘收起是常态）。
    ///
    /// 注入时机必须落在**游戏帧首**（调用方 `FrameStartPump`），与项目输入铁律一致：
    /// 不能在 UI 线程之外直接改触摸表。
    /// </summary>
    internal static class AndroidTouch
    {
        // 与真实手指 id 区分：引擎按 id 查/增/删触摸点（Touch.FindTouchLocationIndex）。
        private const int SyntheticPointerId = 1001;

        private static readonly object Sync = new object();
        private static bool m_resolved;
        private static bool m_available;
        private static MethodInfo m_pressed;
        private static MethodInfo m_moved;
        private static MethodInfo m_released;

        /// <summary>Android 且引擎触摸入口可用时为 true；其他平台一律 false（走鼠标路径）。</summary>
        public static bool Available
        {
            get
            {
                EnsureResolved();
                return m_available;
            }
        }

        /// <summary>按下（等价于手指落点）。</summary>
        public static void Press(Vector2 point)
        {
            Invoke(m_pressed, point);
        }

        /// <summary>移动（拖拽/滑条）。</summary>
        public static void Move(Vector2 point)
        {
            Invoke(m_moved, point);
        }

        /// <summary>抬起（等价于手指离屏，引擎据此派生 Tap/Click）。</summary>
        public static void Release(Vector2 point)
        {
            Invoke(m_released, point);
        }

        private static void Invoke(MethodInfo method, Vector2 point)
        {
            if (method == null)
                return;
            try
            {
                method.Invoke(null, new object[] { SyntheticPointerId, point });
            }
            catch (Exception error)
            {
                Log.Warning("[CmdBridge] android touch injection failed: " + error.Message);
            }
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
                    if (!OperatingSystem.IsAndroid())
                        return;

                    Type touch = typeof(Touch);
                    const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
                    Type[] signature = { typeof(int), typeof(Vector2) };
                    m_pressed = touch.GetMethod("ProcessTouchPressed", Flags, null, signature, null);
                    m_moved = touch.GetMethod("ProcessTouchMoved", Flags, null, signature, null);
                    m_released = touch.GetMethod("ProcessTouchReleased", Flags, null, signature, null);
                    m_available = m_pressed != null && m_moved != null && m_released != null;
                    if (!m_available)
                    {
                        Log.Warning("[CmdBridge] android touch entry points not found; " +
                            "touch injection disabled");
                    }
                }
                catch (Exception error)
                {
                    m_available = false;
                    Log.Warning("[CmdBridge] android touch lookup failed: " + error.Message);
                }
            }
        }
    }
}
