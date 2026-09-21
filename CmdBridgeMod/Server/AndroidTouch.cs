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
            // 先清掉可能残留的合成点：引擎的 `ProcessTouchPressed → ProcessTouchMoved` 命中旧点后
            // **不会新建点**，旧点位置就会被这一次按下沿用（实测：落点错位到上一次拖动结束的位置）。
            // 只在 Press 前清：Release 之后要让引擎在本帧看到 `Released`，否则 UI 的 Tap/Click 派生会坏。
            RemoveSynthetic();
            Invoke(m_pressed, point);
        }

        /// <summary>移动（拖拽/滑条）。</summary>
        public static void Move(Vector2 point)
        {
            // 引擎的 `ProcessTouchMoved` 只在"该点状态已是 Moved"时才更新位置，而 `Pressed` 点会被
            // 丢弃位置（`Touch.Android.cs:122-130`）。真机流程是引擎自己在 AfterFrame 里把 Pressed
            // 提升为 Moved，摇杆则在 Pressed 阶段捕获触摸并持续算偏移 —— 所以这里**只强写位置、
            // 不改状态**（等价于铁律"ProcessTouchMoved 必须无条件更新位置"的 mod 侧实现）；
            // 之前把状态改成 Moved 会让摇杆不再认这次触摸（实测只走一步就不动了）。
            ForcePosition(point);
            Invoke(m_moved, point);
        }

        /// <summary>只把合成点的 Position 写成新值，其余字段与状态一律不动。</summary>
        private static void ForcePosition(Vector2 point)
        {
            try
            {
                if (m_locationsField == null && !ResolveLocations())
                    return;
                var list = m_locationsField.GetValue(null) as System.Collections.IList;
                if (list == null)
                    return;
                for (int i = 0; i < list.Count; i++)
                {
                    object location = list[i];
                    if (location == null ||
                        !Equals(m_idField.GetValue(location), SyntheticPointerId))
                        continue;
                    m_positionField.SetValue(location, point);
                    list[i] = location; // 结构体：装箱副本必须写回列表
                    return;
                }
            }
            catch (Exception error)
            {
                Log.Warning("[CmdBridge] android touch position write failed: " + error.Message);
            }
        }

        private static FieldInfo m_locationsField;
        private static Type m_locationType;
        private static FieldInfo m_idField;
        private static FieldInfo m_positionField;
        private static FieldInfo m_stateField;
        private static object m_stateMoved;
        private static bool m_promoteUnavailable;

        /// <summary>
        /// 把合成触摸点从 `Pressed` 提升为 `Moved`。
        ///
        /// 引擎的 `Touch.ProcessTouchMoved`（`Engine/Engine/Input/Touch.Android.cs:113-135`）**只在
        /// 已存在点的状态是 `Moved` 时才更新位置**，而 `ProcessTouchPressed` 新建的点状态是
        /// `Pressed`（`:138-143`）——于是后续每一次 Move 的位置都被丢弃，触摸点永远停在按下处：
        /// 移动摇杆/滑条因此纹丝不动，而"按下与抬起同一点"的 UI 点击不受影响（它不需要位置更新）。
        /// 这与项目铁律"`ProcessTouchMoved` 必须无条件更新位置"是同一个坑。
        ///
        /// 只改我们自己那个合成点（id=1001）；纯点击不经过这里，因此 Tap/Click 依赖的
        /// `Pressed` + `ReleaseQueued` 派生路径完全不受影响。释放时该点为 `Moved` →
        /// `ProcessTouchReleased` 走 `Released` 分支（`:170-178`），不会留下卡住的触摸。
        /// </summary>
        public static void PromoteToMoved(Vector2 point)
        {
            EnsureResolved();
            if (!m_available || m_promoteUnavailable)
                return;
            try
            {
                if (m_locationsField == null && !ResolveLocations())
                    return;
                var list = m_locationsField.GetValue(null) as System.Collections.IList;
                if (list == null)
                    return;
                for (int i = 0; i < list.Count; i++)
                {
                    object location = list[i];
                    if (location == null ||
                        !Equals(m_idField.GetValue(location), SyntheticPointerId))
                        continue;
                    // 原地改字段：只动 Position/State，其余字段（触摸起点等）必须原样保留。
                    // 之前用 Activator 新建实例替换，会把没写的字段清零 —— 移动摇杆据此算出的
                    // 偏移量就是错的，实测表现为"触摸被当成视角/挖掘，角色不动"。
                    m_positionField.SetValue(location, point);
                    m_stateField.SetValue(location, m_stateMoved);
                    list[i] = location; // 结构体：装箱副本必须写回列表
                    return;
                }
            }
            catch (Exception error)
            {
                m_promoteUnavailable = true;
                Log.Warning("[CmdBridge] android touch promote failed: " + error.Message);
            }
        }

        private static void RemoveSynthetic()
        {
            try
            {
                if (m_locationsField == null && !ResolveLocations())
                    return;
                var list = m_locationsField.GetValue(null) as System.Collections.IList;
                if (list == null)
                    return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    object location = list[i];
                    if (location != null &&
                        Equals(m_idField.GetValue(location), SyntheticPointerId))
                    {
                        list.RemoveAt(i);
                    }
                }
            }
            catch (Exception error)
            {
                Log.Warning("[CmdBridge] android touch cleanup failed: " + error.Message);
            }
        }

        private static bool ResolveLocations()
        {
            m_locationsField = typeof(Touch).GetField("m_touchLocations",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (m_locationsField == null)
            {
                m_promoteUnavailable = true;
                return false;
            }
            Type listType = m_locationsField.FieldType;
            Type[] arguments = listType.IsGenericType
                ? listType.GetGenericArguments()
                : Type.EmptyTypes;
            if (arguments.Length != 1)
            {
                m_promoteUnavailable = true;
                return false;
            }
            m_locationType = arguments[0];
            m_idField = m_locationType.GetField("Id");
            m_positionField = m_locationType.GetField("Position");
            m_stateField = m_locationType.GetField("State");
            if (m_idField == null || m_positionField == null || m_stateField == null)
            {
                m_promoteUnavailable = true;
                return false;
            }
            m_stateMoved = Enum.Parse(m_stateField.FieldType, "Moved");
            return true;
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
                    // Source: Mod/CmdBridgeMod/Server/PlatformInfo.cs
                    // 平台判定跟 SuAPI 一致（SuAPI.ModLoader.GetCurrentPlatform() == "Android"）；
                    // 另外还要**探测能力**：引擎里真的存在这三个触摸入口才启用
                    // （桌面 Touch.cs 同名但语义不同，且桌面本来就走鼠标路径）。
                    if (!PlatformInfo.IsAndroid)
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
