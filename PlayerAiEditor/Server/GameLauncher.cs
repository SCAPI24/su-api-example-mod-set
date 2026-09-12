using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PlayerAiMod.Editor
{
    /// <summary>
    /// 启动 / 结束**游戏进程**——这是"人用的编辑器"在干的事，
    /// 等价于手动双击 <c>&lt;实例根&gt;\Survivalcraft.exe</c>：不传命令行参数、不改配置、
    /// 不碰存档。启动之后的通信仍然只走 CmdBridge 控制通道（见 <see cref="GameBridgeClient"/>），
    /// 所以"AI 只能通过玩家控制器行动、不许写游戏状态"这条边界一点没动。
    ///
    /// 为什么放在编辑器里：调试一条行为树要在"编辑器 ↔ 游戏"之间来回切，
    /// 每次手动去文件管理器双击一次游戏是纯摩擦；编辑器本来就知道实例根在哪。
    /// </summary>
    internal static class GameLauncher
    {
        /// <summary>游戏主程序文件名（单文件发布产物）。</summary>
        public const string ExecutableName = "Survivalcraft.exe";

        /// <summary>进程名（不带 .exe），用于"游戏在不在跑"的判断。</summary>
        public const string ProcessName = "Survivalcraft";

        /// <summary>控制通道的运行时文件（游戏启动后自己写）。</summary>
        public const string RuntimeFileName = "CmdBridge.runtime.json";

        public static string ExecutablePath(string instanceRoot)
        {
            if (string.IsNullOrEmpty(instanceRoot))
                return null;
            return Path.Combine(instanceRoot, ExecutableName);
        }

        public static string RuntimePath(string instanceRoot)
        {
            if (string.IsNullOrEmpty(instanceRoot))
                return null;
            return Path.Combine(instanceRoot, RuntimeFileName);
        }

        /// <summary>
        /// 在跑的游戏进程 —— **只看属于这个实例根的**。
        ///
        /// 为什么必须按 exe 路径过滤：机器上可能同时开着别的 Survivalcraft（另一个实例、另一份
        /// 发布目录）。按进程名一律当成"我们的游戏"会出两种事故：①状态显示成"游戏在跑"，
        /// 其实跑的是别人家的实例；②「结束游戏」/自检把**别人的**游戏关掉 —— 实测就发生过
        /// （自检里那条"没在跑时结束要如实回 not_running"把用户正在玩的那个实例关掉了）。
        /// 认不出 exe 路径的进程（权限不足）一律当作"不是我们的"：宁可不动，也不误杀。
        /// </summary>
        public static List<Process> Running(string instanceRoot)
        {
            var list = new List<Process>();
            Process[] found;
            try
            {
                found = Process.GetProcessesByName(ProcessName);
            }
            catch (Exception)
            {
                return list;   // 取不到进程列表就当"没在跑"：后面连通道时的报错更准确
            }

            for (int i = 0; i < found.Length; i++)
            {
                if (BelongsToInstance(found[i], instanceRoot))
                    list.Add(found[i]);
                else
                    found[i].Dispose();
            }
            return list;
        }

        /// <summary>这个进程的 exe 是不是就在 <paramref name="instanceRoot"/> 里。</summary>
        private static bool BelongsToInstance(Process process, string instanceRoot)
        {
            if (process == null || string.IsNullOrEmpty(instanceRoot))
                return false;
            try
            {
                ProcessModule module = process.MainModule;
                string file = module != null ? module.FileName : null;
                if (string.IsNullOrEmpty(file))
                    return false;
                string directory = new FileInfo(file).DirectoryName;
                if (string.IsNullOrEmpty(directory))
                    return false;
                return string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(instanceRoot)),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;   // 权限不足/进程已退出：不敢认领就不认领
            }
        }

        /// <summary>
        /// 游戏状态全貌（**只看，不动**）：进程在不在、exe 在不在、控制通道文件是不是旧的。
        /// 编辑器靠它把"没启动 / 启动了但通道还没开 / 已连上"三种状态区分开 ——
        /// 以前只有一句"连不上"，旧 runtime 文件还会报成裸露的 socket 错误。
        /// </summary>
        public static Dictionary<string, object> Describe(string instanceRoot)
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            string exe = ExecutablePath(instanceRoot);
            string runtime = RuntimePath(instanceRoot);

            info["instanceRoot"] = instanceRoot;
            info["exePath"] = exe;
            info["exeExists"] = !string.IsNullOrEmpty(exe) && File.Exists(exe);
            info["processName"] = ProcessName;
            info["runtimeFile"] = runtime;

            bool runtimeExists = !string.IsNullOrEmpty(runtime) && File.Exists(runtime);
            info["runtimeExists"] = runtimeExists;
            if (runtimeExists)
            {
                try
                {
                    info["runtimeModifiedUtc"] = File.GetLastWriteTimeUtc(runtime).ToString("o");
                }
                catch (Exception)
                {
                }
            }

            var processes = Running(instanceRoot);
            info["running"] = processes.Count > 0;
            info["processCount"] = processes.Count;
            if (processes.Count > 0)
            {
                Process first = processes[0];
                try
                {
                    info["pid"] = first.Id;
                }
                catch (Exception)
                {
                }
                try
                {
                    info["startedUtc"] = first.StartTime.ToUniversalTime().ToString("o");
                }
                catch (Exception)
                {
                }
            }

            // 旧 runtime：文件在、游戏进程不在 → 那是上次运行留下的，连它只会得到
            // "目标计算机积极拒绝"，很容易被误读成"游戏在跑但通道坏了"。
            info["runtimeStale"] = runtimeExists && processes.Count == 0;
            for (int i = 0; i < processes.Count; i++)
                processes[i].Dispose();
            return info;
        }

        /// <summary>
        /// 启动游戏（工作目录 = 实例根，这样 `<data:>` 与 Mod/存档路径都对上）。
        /// 只在 exe 不存在或已经在跑时拒绝 —— 其余交给游戏自己。
        /// </summary>
        public static Dictionary<string, object> Launch(string instanceRoot)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            string exe = ExecutablePath(instanceRoot);

            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                result["ok"] = false;
                result["code"] = "game_exe_missing";
                result["reason"] = "找不到游戏主程序：" + (exe ?? "<unknown>")
                    + "（编辑器只知道实例根，游戏得装在那儿）";
                return result;
            }

            var running = Running(instanceRoot);
            if (running.Count > 0)
            {
                int pid = 0;
                try
                {
                    pid = running[0].Id;
                }
                catch (Exception)
                {
                }
                for (int i = 0; i < running.Count; i++)
                    running[i].Dispose();
                result["ok"] = false;
                result["code"] = "already_running";
                result["pid"] = pid;
                result["reason"] = "游戏已经在跑了（pid " + pid + "）；要重启就先点「结束游戏」";
                return result;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = instanceRoot,
                    UseShellExecute = true
                };
                Process process = Process.Start(startInfo);
                int startedPid = 0;
                try
                {
                    startedPid = process != null ? process.Id : 0;
                }
                catch (Exception)
                {
                }
                if (process != null)
                    process.Dispose();

                result["ok"] = true;
                result["pid"] = startedPid;
                result["exe"] = exe;
                result["workingDirectory"] = instanceRoot;
                // 通道要等游戏里的 CmdBridgeMod 起来才会写 runtime 文件 —— 前端据此轮询
                result["channelFile"] = RuntimePath(instanceRoot);
                result["hint"] = "游戏正在启动；控制通道（" + RuntimeFileName
                    + "）要等它加载完 Mod 才会出现，编辑器会自动等它。";
                return result;
            }
            catch (Exception exception)
            {
                result["ok"] = false;
                result["code"] = "launch_failed";
                result["reason"] = exception.GetType().Name + ": " + exception.Message;
                return result;
            }
        }

        /// <summary>
        /// 结束游戏：先请它自己退出（`CloseMainWindow` —— 走正常关闭流程，输入会被释放、
        /// 日志会 flush），超时再强杀。
        /// </summary>
        public static Dictionary<string, object> Quit(string instanceRoot, int waitMilliseconds = 6000)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            var processes = Running(instanceRoot);
            if (processes.Count == 0)
            {
                result["ok"] = true;
                result["closed"] = 0;
                result["killed"] = 0;
                result["code"] = "not_running";
                result["reason"] = "游戏本来就没在跑";
                return result;
            }

            int closed = 0;
            int killed = 0;
            for (int i = 0; i < processes.Count; i++)
            {
                Process process = processes[i];
                try
                {
                    bool asked = process.CloseMainWindow();
                    if (asked && process.WaitForExit(waitMilliseconds))
                    {
                        closed++;
                        continue;
                    }
                }
                catch (Exception)
                {
                    // 关不掉就走强杀
                }
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        process.WaitForExit(waitMilliseconds);
                        killed++;
                    }
                    else
                    {
                        closed++;
                    }
                }
                catch (Exception exception)
                {
                    result["ok"] = false;
                    result["code"] = "quit_failed";
                    result["reason"] = exception.GetType().Name + ": " + exception.Message;
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (!result.ContainsKey("ok"))
                result["ok"] = true;
            result["closed"] = closed;
            result["killed"] = killed;
            return result;
        }
    }
}
