using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Engine;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// SuMultiplayer 的分层日志出口。**两种模式**（用户 2026-11-08 定）：
    ///
    ///   · <b>上线模式（默认，verbose=false）</b>：所有模块日志只进内存缓冲，
    ///     缓冲满（256 KB / 4096 行）就**直接丢弃**，**永不写盘**。
    ///     上线跑图不产生任何 SuMultiplayer-*.log 文件，也不占用存储。
    ///   · <b>Debug 模式（verbose=true）</b>：缓冲满时把这一轮内容写进角色日志
    ///     （`data:/Logs/{Server,Client}/SuMultiplayer-YYYYMMDD-*.log`，2 MiB 分卷），
    ///     供完整复盘。
    ///
    /// 模式**由主机调度**：主机按自己的 `SuMultiplayer.json` 的 `verbose` 决定自身模式，
    /// 并在握手（HelloAck.flags bit0）把同一个意图下发给客户端；客户端**不读本地 verbose**，
    /// 完全听主机的。客户端在收到 HelloAck 之前一律是上线模式（不落盘）。
    ///
    /// `Warn` / `Error` 始终同时写 Game.log（真异常必须留在核心日志），
    /// 是否再进角色日志由模式决定（同 Info）。
    /// </summary>
    public static class ModLog
    {
        private const int MaxBufferedBytes = 256 * 1024;
        private const int MaxBufferedLines = 4096;
        /// <summary>单个日志文件上限（2 MiB）：按日聚合后只在超过此大小时换分卷。</summary>
        private const int MaxFileBytes = 2 * 1024 * 1024;
        /// <summary>每个角色/每天最多保留 4 个分卷（约 8 MiB）。</summary>
        private const int MaxPartsPerDay = 4;
        /// <summary>日志保留天数（按文件名日期清理）。</summary>
        private const int RetentionDays = 7;
        private static readonly object Gate = new object();
        private static readonly List<string> Buffer = new List<string>(512);
        private static int s_bufferedBytes;
        private static bool s_verbose;
        /// <summary>
        /// 是否把内存缓冲写入磁盘。false = 上线模式（缓冲满直接丢弃，永不写盘）。
        /// 与 <see cref="s_verbose"/> 同步：只有 Debug 模式才落盘。
        /// </summary>
        private static bool s_persistToFile;
        private static string s_role = "Client";
        private static string s_lastCleanupDay;

        public static bool Verbose => s_verbose;

        public static void Configure(bool verbose)
        {
            lock (Gate)
            {
                s_verbose = verbose;
                s_persistToFile = verbose;
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

        /// <summary>联机会话边界标记（加入/主持开始和结束），只写角色日志。</summary>
        public static void SessionMarker(string message)
        {
            AppendLocked("SESSION", message);
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

        /// <summary>
        /// 选当天的分卷文件：<c>SuMultiplayer-YYYYMMDD-Role.log</c>，
        /// 超过 2 MiB 依次使用 <c>-2.log</c>、<c>-3.log</c>、<c>-4.log</c>；
        /// 4 卷都满后按环形日志处理：删除**最旧**的一卷并复用，只保留最新约 8 MiB。
        /// </summary>
        private static string PickLogFileLocked(string directory, int incomingBytes)
        {
            string date = DateTime.Now.ToString("yyyyMMdd");
            string basePath = Storage.CombinePaths(directory,
                "SuMultiplayer-" + date + "-" + s_role);
            string[] paths = new string[MaxPartsPerDay];
            for (int part = 1; part <= MaxPartsPerDay; part++)
            {
                paths[part - 1] = part == 1
                    ? basePath + ".log"
                    : basePath + "-" + part + ".log";
                string path = paths[part - 1];
                if (!Storage.FileExists(path)) return path;
                try
                {
                    if (Storage.GetFileSize(path) + incomingBytes <= MaxFileBytes) return path;
                }
                catch (Exception) { return path; }
            }
            // 4 卷全满：按最后写入时间找最旧的一卷，删掉后复用 → 始终保留最新数据。
            string oldest = paths[0];
            DateTime oldestTime = DateTime.MaxValue;
            for (int i = 0; i < paths.Length; i++)
            {
                try
                {
                    DateTime time = Storage.GetFileLastWriteTime(paths[i]);
                    if (time < oldestTime) { oldestTime = time; oldest = paths[i]; }
                }
                catch (Exception) { }
            }
            try { Storage.DeleteFile(oldest); } catch (Exception) { }
            return oldest;
        }

        /// <summary>
        /// 迁移旧版"每次 flush 一个新文件"的命名：
        /// <c>SuMultiplayer-YYYYMMDD-HHmmss-N.log</c> 只保留最近 10 个，其余删除。
        /// 新格式是 <c>SuMultiplayer-YYYYMMDD-Role.log</c> 及分卷，不在此列。
        /// </summary>
        private static void CleanupOldLayoutLocked(string directory)
        {
            try
            {
                var oldFiles = new List<string>();
                foreach (string name in Storage.ListFileNames(directory))
                {
                    string[] parts = name.Split('-');
                    if (parts.Length == 4 && parts[0] == "SuMultiplayer" &&
                        parts[1].Length == 8 && parts[2].Length == 6 &&
                        parts[3].EndsWith(".log", StringComparison.Ordinal))
                    {
                        oldFiles.Add(name);
                    }
                }
                if (oldFiles.Count <= 10) return;
                oldFiles.Sort(StringComparer.Ordinal);
                int removeCount = oldFiles.Count - 10;
                for (int i = 0; i < removeCount; i++)
                {
                    try { Storage.DeleteFile(Storage.CombinePaths(directory, oldFiles[i])); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        /// <summary>按文件名的 YYYYMMDD 清理超过保留天数的旧日志；每天最多扫描一次。</summary>
        private static void CleanupOldLogsLocked(string directory)
        {
            string today = DateTime.Now.ToString("yyyyMMdd");
            if (string.Equals(s_lastCleanupDay, today, StringComparison.Ordinal)) return;
            s_lastCleanupDay = today;
            DateTime cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
            try
            {
                foreach (string name in Storage.ListFileNames(directory))
                {
                    if (!name.StartsWith("SuMultiplayer-", StringComparison.Ordinal)) continue;
                    if (name.Length < 24) continue;
                    if (!DateTime.TryParseExact(name.Substring(16, 8), "yyyyMMdd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out DateTime day))
                        continue;
                    if (day >= cutoff) continue;
                    try { Storage.DeleteFile(Storage.CombinePaths(directory, name)); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
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
            if (!s_persistToFile)
            {
                // 上线模式：缓冲满即丢弃，绝不写盘（不累积、不轮转、不占存储）。
                Buffer.Clear();
                s_bufferedBytes = 0;
                return;
            }
            try
            {
                string directory = Storage.CombinePaths("data:/Logs", s_role);
                Storage.CreateDirectory(directory);
                CleanupOldLogsLocked(directory);
                CleanupOldLayoutLocked(directory);
                string file = PickLogFileLocked(directory, s_bufferedBytes);
                using (Stream stream = Storage.OpenFile(file, OpenFileMode.CreateOrOpen))
                {
                    stream.Seek(0L, SeekOrigin.End);
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        for (int i = 0; i < Buffer.Count; i++) writer.WriteLine(Buffer[i]);
                        writer.Flush();
                    }
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
