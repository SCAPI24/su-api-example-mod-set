using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CmdBridgeMod
{
    /// <summary>
    /// OS 前台窗口探测（**只读**）：谁在前台、标题是什么、游戏窗口句柄是哪个。
    ///
    /// 为什么要它：OpenTK 的 `GameWindow.Focused` 可能滞后 —— 用户实测"游戏被别的应用盖住、
    /// 但还没失焦"，于是键鼠仍然进游戏；另一个方向也要能查清"是不是我们把游戏拉到前台了"
    /// （我们从来只读、不写窗口状态，有了这个才有证据说清楚）。
    ///
    /// 铁律：**只读**。这里没有、也不要加 `SetForegroundWindow` / `ShowWindow` / `Activate`。
    /// 非 Windows 平台由调用方兜底（本类只在 Windows 上实例化）。
    /// </summary>
    internal sealed class ForegroundWindowProbe
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr handle, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLengthW(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);

        private IntPtr m_gameHandle;
        private bool m_gameHandleResolved;
        private int m_ourProcessId;

        /// <summary>
        /// 判断"前台窗口是不是本进程的窗口"。
        ///
        /// 为什么按**进程**判而不是按句柄：`WindowInfo.Handle` 在窗口刚创建时可能和
        /// 真正在用的那个顶层句柄不一致（实测：前台标题明明是 `Survivalcraft 2`，
        /// 句柄却对不上，于是"失焦"判断整个反过来）。按进程判既稳又准 ——
        /// 前台只要是本进程的窗口，就说明用户此刻在看着游戏。
        /// </summary>
        public bool IsOurProcess(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return false;

            if (m_ourProcessId == 0)
            {
                try
                {
                    m_ourProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
                }
                catch (Exception)
                {
                    m_ourProcessId = -1;
                }
            }
            if (m_ourProcessId <= 0)
                return false;

            int pid;
            GetWindowThreadProcessId(handle, out pid);
            return pid == m_ourProcessId;
        }

        /// <summary>窗口是否还活着（用于区分"真被抢了"和"那个窗口自己关掉了"）。</summary>
        public bool IsAlive(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return false;
            return IsWindow(handle);
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr handle);

        /// <summary>当前前台窗口句柄（可能为 Zero）。</summary>
        public IntPtr Foreground()
        {
            return GetForegroundWindow();
        }

        /// <summary>游戏窗口的 OS 句柄（OpenTK 的 `WindowInfo.Handle`，经反射）；取不到返回 Zero。</summary>
        public IntPtr HandleOf(object window)
        {
            if (m_gameHandleResolved)
                return m_gameHandle;

            m_gameHandleResolved = true;
            try
            {
                m_gameHandle = OpenTkInput.GetWindowHandle(window);
            }
            catch (Exception)
            {
                m_gameHandle = IntPtr.Zero;
            }
            return m_gameHandle;
        }

        /// <summary>前台窗口标题（取不到就返回空串）。</summary>
        public string Title(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return string.Empty;

            int length = GetWindowTextLengthW(handle);
            if (length <= 0)
                return string.Empty;

            var builder = new StringBuilder(length + 2);
            GetWindowTextW(handle, builder, builder.Capacity);
            return builder.ToString();
        }
    }
}
