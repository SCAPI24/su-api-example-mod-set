using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 摘要规格的**目录来源 + 文件戳热重载**（计划 §4.2 / G18 的同一条思路）。
    ///
    /// 规矩（都是既有教训的复用）：
    ///   · **缺文件不算错**：用内置默认规格兜底（空实例照样跑），只在状态里记一句"用的是内置";
    ///   · **解析失败绝不半生效**：拒用新文件、**继续用上一份好规格**，并给出明确错误码
    ///     （`digest_invalid`），与"问题库拒包"同一个口径；
    ///   · **只在真要发问时读盘**（`EnsureFresh` 由编译摘要那几处调用）——
    ///     "改了文件但没人问"不白读盘，只会让 `changedOnDisk` 亮着。
    /// </summary>
    public static class DigestCatalog
    {
        public const string FolderName = "Digest";
        public const string Extension = ".digest.json";

        /// <summary>
        /// `PlayerAi` 这一层目录名。**刻意不复用 `PackageRoots.PlayerAiFolder`**：
        /// 本文件是纯逻辑层（编辑器也编它），而 `PackageRoots` 会拖进 SuAPI/ModLoader 依赖。
        /// </summary>
        public const string PlayerAiFolderName = "PlayerAi";

        private static readonly List<string> s_directories = new List<string>();
        private static readonly HashSet<string> s_reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static DigestSpec s_current;
        private static string s_currentPath;
        private static long s_stamp;
        private static bool s_force;
        private static string s_lastError;
        private static string s_lastErrorPath;

        /// <summary>
        /// "规格换了"的回调（**单个**，不是多播事件：运行时是单例，多播只会随重建泄漏）。
        /// 游戏侧把它接到事件日志，于是 `ai.logs` 里能看到 `digest-reload`。
        /// </summary>
        public static Action Changed;

        /// <summary>
        /// 日志接缝。**刻意不直接调 `Engine.Log`**：本文件也被编辑器编译
        /// （编辑器共用纯逻辑层、不带游戏胶水），那里没有引擎日志。
        /// 游戏侧在 `PlayerAiRuntime` 里把它接到 `Engine.Log`。
        /// </summary>
        public static Action<string> LogSink;

        private static void Log(string message)
        {
            Action<string> sink = LogSink;
            if (sink == null || message == null)
                return;
            try
            {
                sink(message);
            }
            catch (Exception)
            {
                // 日志失败不影响判准
            }
        }

        /// <summary>当前生效的规格（恒不为 null：没文件就是内置默认）。</summary>
        public static DigestSpec Current
        {
            get
            {
                EnsureFresh();
                return s_current ?? DigestSpec.Default;
            }
        }

        public static string CurrentPath
        {
            get
            {
                EnsureFresh();
                return s_currentPath;
            }
        }

        /// <summary>磁盘上的文件与**已生效的那一份**是否已经不一致（"改了还没被读"）。</summary>
        public static bool ChangedOnDisk
        {
            get
            {
                string path = FindFile();
                if (path == null)
                    return s_currentPath != null;   // 文件被删了 → 与生效的那份不一致
                return !string.Equals(path, s_currentPath, StringComparison.OrdinalIgnoreCase)
                    || StampOf(path) != s_stamp;
            }
        }

        public static string LastError
        {
            get { return s_lastError; }
        }

        public static string LastErrorPath
        {
            get { return s_lastErrorPath; }
        }

        public static IReadOnlyList<string> Directories
        {
            get { return s_directories; }
        }

        /// <summary>由实例根算出摘要目录：`&lt;实例根&gt;/PlayerAi/Digest`（与 `Questions` 并列）。</summary>
        public static List<string> DirectoriesFor(string instanceRoot)
        {
            var directories = new List<string>();
            if (!string.IsNullOrEmpty(instanceRoot))
                directories.Add(Path.Combine(instanceRoot, PlayerAiFolderName, FolderName));
            return directories;
        }

        public static void Configure(IEnumerable<string> directories)
        {
            s_directories.Clear();
            if (directories != null)
            {
                foreach (string directory in directories)
                {
                    if (!string.IsNullOrEmpty(directory) && !s_directories.Contains(directory))
                        s_directories.Add(directory);
                }
            }
        }

        /// <summary>文件戳变了就重读；解析失败**保留旧的那份**并记错误。线程/帧安全（幂等、无锁竞争写）。</summary>
        public static void EnsureFresh()
        {
            string path = FindFile();
            if (path == null)
            {
                // 没有文件：用内置默认（若上一份是文件版，这就是一次"退回默认"）
                if (s_currentPath != null || s_current == null)
                {
                    s_current = DigestSpec.Default;
                    s_currentPath = null;
                    s_stamp = 0;
                    s_force = false;
                    s_lastError = null;
                    s_lastErrorPath = null;
                    StateDigestCompiler.SetActive(s_current);
                    NotifyChanged();
                }
                return;
            }

            long stamp = StampOf(path);
            if (!s_force
                && string.Equals(path, s_currentPath, StringComparison.OrdinalIgnoreCase)
                && stamp == s_stamp)
            {
                return;
            }
            s_force = false;

            DigestSpec parsed = Load(path, out string error);
            if (parsed == null)
            {
                // 拒用新文件：旧的那份继续跑（第一次就没有旧的 → 退到内置默认）。
                // ⚠️ **失败时不动 `s_currentPath`/`s_stamp`** —— 它们记的是"正在跑的那份从哪来"，
                // 抹掉它们会让 `ai.digest.status` 报 `path=null`（明明有一份好规格在跑），
                // 也会让 `changedOnDisk` 从此恒为 true。诊断信息失真比"没写这条"更贵。
                s_lastError = error;
                s_lastErrorPath = path;
                if (s_current == null)
                {
                    s_current = DigestSpec.Default;
                    s_currentPath = null;
                    s_stamp = 0;
                    StateDigestCompiler.SetActive(s_current);
                }
                if (s_reported.Add(path))
                {
                    // 拒用新文件这件事**必须吵一声**：否则表现就是"改了文件但判定没变"，最难查。
                    Log("[PlayerAi][digest] rejected " + path + " (keeping the previous spec): " + error);
                }
                return;
            }

            bool changed = s_current == null
                || !string.Equals(s_current.SourceHash, parsed.SourceHash, StringComparison.Ordinal);
            s_current = parsed;
            s_currentPath = path;
            s_stamp = stamp;
            s_lastError = null;
            s_lastErrorPath = null;
            s_reported.Remove(path);
            StateDigestCompiler.SetActive(s_current);
            if (changed)
            {
                Log("[PlayerAi][digest] spec reloaded: " + Path.GetFileName(path)
                    + " hash=" + parsed.SourceHash + " fields=" + parsed.Fields.Count);
                NotifyChanged();
            }
        }

        /// <summary>
        /// 显式重载（命令用）：忽略文件戳，强制重读一次。
        ///
        /// 用**一个 force 标志**而不是"把戳清零/把路径置空"来强制：
        /// 后两者会顺手毁掉"正在跑的那份从哪来"（`CurrentPath` 变 null、`ChangedOnDisk` 恒 true），
        /// 于是"重载失败"之后状态面板开始撒谎 —— 那正是排查时最不需要的东西。
        /// </summary>
        public static bool Reload(out DigestSpec spec, out string error)
        {
            // 显式重载 = 用户的明确动作：把"已经报过拒用"的去重标记清掉，
            // 于是文件仍然是坏的时**一定**会再报一次（A26 的同一条教训：
            // 去重是为了不刷屏，不是为了永远沉默）。
            ForgetReported();
            s_force = true;
            EnsureFresh();
            spec = s_current ?? DigestSpec.Default;
            error = s_lastError;
            return s_lastError == null;
        }

        public static DigestSpec Load(string path, out string error)
        {
            error = null;
            try
            {
                string text = File.ReadAllText(path);
                DigestSpec spec = DigestSpecJson.Parse(text, path, out error);
                if (spec == null)
                    return null;
                if (string.IsNullOrEmpty(spec.Name))
                    spec.Name = Path.GetFileNameWithoutExtension(path);
                return spec;
            }
            catch (Exception exception)
            {
                error = "digest spec is unreadable: " + exception.GetType().Name + ": " + exception.Message;
                return null;
            }
        }

        /// <summary>清掉"已报过"的去重标记（重新尝试时报错要能再出现一次）。</summary>
        public static void ForgetReported()
        {
            s_reported.Clear();
        }

        /// <summary>自检用：把目录/缓存恢复成"什么都没配"的干净状态。</summary>
        public static void ResetForTests()
        {
            s_directories.Clear();
            s_reported.Clear();
            s_current = null;
            s_currentPath = null;
            s_stamp = 0;
            s_force = false;
            s_lastError = null;
            s_lastErrorPath = null;
            StateDigestCompiler.SetActive(null);
        }

        private static string FindFile()
        {
            for (int d = 0; d < s_directories.Count; d++)
            {
                string directory = s_directories[d];
                if (string.IsNullOrEmpty(directory))
                    continue;
                string preferred = Path.Combine(directory, DigestSpec.DefaultId + Extension);
                if (File.Exists(preferred))
                    return preferred;
            }
            // 没找到约定名：**恰好一份**时才认（改名不会静默失效，多份也不会瞎猜）
            string only = null;
            for (int d = 0; d < s_directories.Count; d++)
            {
                string directory = s_directories[d];
                try
                {
                    if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                        continue;
                    string[] files = Directory.GetFiles(directory, "*" + Extension, SearchOption.TopDirectoryOnly);
                    for (int f = 0; f < files.Length; f++)
                    {
                        if (only != null)
                            return null;
                        only = files[f];
                    }
                }
                catch (Exception)
                {
                    // 目录读不了就当它没有
                }
            }
            return only;
        }

        private static long StampOf(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0L;
            }
            catch (Exception)
            {
                return 0L;
            }
        }

        private static void NotifyChanged()
        {
            Action handler = Changed;
            if (handler == null)
                return;
            try
            {
                handler();
            }
            catch (Exception)
            {
                // 订阅方的异常不影响判准
            }
        }
    }
}
