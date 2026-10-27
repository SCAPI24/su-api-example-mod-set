using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Engine;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// SuMultiplayer 的分层日志出口。
    ///
    /// Game.log 只留给游戏核心 / 真正异常；本 Mod 的常规高频日志（模块状态、网络统计、
    /// 房间发现、区块生命周期……）先进入内存缓冲，按角色写入：
    ///   data:/Logs/Server/SuMultiplayer-*.log
    ///   data:/Logs/Client/SuMultiplayer-*.log
    ///
    /// 默认不逐行落盘：缓冲区达到阈值（约 256KB）或角色切换 / 卸载时才写盘并轮转文件。
    /// verbose 只是“把额外诊断细节也收进角色日志”，普通 Info 始终不会写 Game.log。
    /// </summary>
    public static class ModLog
    {
        private const int MaxBufferedBytes = 256 * 1024;
        private const int MaxBufferedLines = 4096;
        private static readonly object Gate = new object();
        private static readonly List<string> Buffer = new List<string>(512);
        private static int s_bufferedBytes;
        private static bool s_verbose;
        private static string s_role = "Client";
        private static long s_fileSequence;

        public static bool Verbose => s_verbose;

        public static void Configure(bool verbose)
        {
            lock (Gate)
            {
                s_verbose = verbose;
                Buffer.Clear();
                s_bufferedBytes = 0;
            }
        }

        public static void SetRole(string role)
        {
            string normalized = string.Equals(role, "Server", StringComparison.OrdinalIgnoreCase)
                ? "Server" : "Client";
            lock (Gate)
            {
                if (string.Equals(s_role, normalized, StringComparison.Ordinal)) return;
                WriteBufferLocked();
                s_role = normalized;
            }
        }

        /// <summary>常规模块日志：只进角色日志内存缓冲，不写 Game.log。</summary>
        public static void Info(string message)
        {
            AppendLocked("INFO", message);
        }

        /// <summary>调试细节：verbose=false 时完全丢弃；verbose=true 时进角色日志缓冲。</summary>
        public static void VerboseInfo(string message)
        {
            if (!s_verbose) return;
            AppendLocked("DEBUG", message);
        }

        /// <summary>警告仍然进 Game.log（核心日志），同时进入角色日志。</summary>
        public static void Warn(string message)
        {
            try { Log.Warning("[SuMP] " + message); } catch (Exception) { }
            AppendLocked("WARN", message);
        }

        public static void Error(string message, Exception exception = null)
        {
            try
            {
                Log.Error("[SuMP] " + message +
                    (exception == null ? string.Empty : " :: " + exception));
            }
            catch (Exception) { }
            AppendLocked("ERROR", message + (exception == null ? string.Empty : " :: " + exception));
        }

        /// <summary>强制把剩余缓冲写入角色日志（卸载 / 角色切换 / 退出前）。</summary>
        public static void FlushAll()
        {
            lock (Gate) WriteBufferLocked();
        }

        private static void AppendLocked(string level, string message)
        {
            if (message == null) return;
            lock (Gate)
            {
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + message;
                Buffer.Add(line);
                s_bufferedBytes += line.Length + 2;
                if (s_bufferedBytes >= MaxBufferedBytes || Buffer.Count >= MaxBufferedLines)
                    WriteBufferLocked();
            }
        }

        private static void WriteBufferLocked()
        {
            if (Buffer.Count == 0) return;
            try
            {
                string directory = Storage.CombinePaths("data:/Logs", s_role);
                Storage.CreateDirectory(directory);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string file = Storage.CombinePaths(directory,
                    "SuMultiplayer-" + stamp + "-" + (++s_fileSequence) + ".log");
                using (Stream stream = Storage.OpenFile(file, OpenFileMode.Create))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    for (int i = 0; i < Buffer.Count; i++) writer.WriteLine(Buffer[i]);
                    writer.Flush();
                }
            }
            catch (Exception error)
            {
                // 角色日志写失败不能反过来把 Game.log 打爆；只记录一次到核心日志。
                try
                {
                    Log.Warning("[SuMP] 角色日志写盘失败（" + s_role + "）：" +
                        error.GetType().Name + " " + error.Message);
                }
                catch (Exception) { }
            }
            finally
            {
                Buffer.Clear();
                s_bufferedBytes = 0;
            }
        }
    }
}
