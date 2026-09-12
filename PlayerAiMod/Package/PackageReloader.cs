using Engine;
using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>重载/切换的触发来源（日志与 `ai.status` 要能区分）。</summary>
    public enum ReloadTrigger
    {
        /// <summary>首次装载。</summary>
        Initial,

        /// <summary>编辑器推送通知 `ai.tree.notify`（计划 §5.1 主路径）。</summary>
        Notify,

        /// <summary>手动兜底 `ai.tree.reload`。</summary>
        Manual,

        /// <summary>可选低频目录扫描（默认关闭）。</summary>
        Watch,

        /// <summary>`ai.tree.switch` 切换活动树（P0-11 用同一个替换路径）。</summary>
        Switch
    }

    /// <summary>一次重载/切换的完整结果（成功、被拒、被忽略三种都要能说清楚）。</summary>
    public sealed class TreeReloadResult
    {
        public bool Replaced;

        public bool Rejected;

        /// <summary>被忽略（内容没变 / 通知哈希与文件不符）——不算失败，也不动旧树。</summary>
        public bool Ignored;

        public ReloadTrigger Trigger;

        public string Path;

        /// <summary>本次看到的文件哈希（读失败时为空）。</summary>
        public string Hash;

        /// <summary>替换前的哈希。</summary>
        public string PreviousHash;

        public string Reason;

        public double Milliseconds;

        public int PackageCount;

        public int NodeCount;

        public BtMigrationReport Migration;

        /// <summary>报告摘要（失败原因逐条列出，`ai.status` 直接显示）。</summary>
        public readonly List<string> Issues = new List<string>();

        public bool Succeeded
        {
            get { return Replaced; }
        }

        public string Describe()
        {
            string head = Trigger + " " + (Path ?? "<none>");
            if (Replaced)
            {
                return head + " -> replaced (" + NodeCount + " nodes, "
                    + Milliseconds.ToString("0.0") + "ms, "
                    + (Migration != null ? Migration.Describe() : "no migration") + ")";
            }
            if (Ignored)
                return head + " -> ignored (" + Reason + ")";
            return head + " -> rejected (" + Reason + (Issues.Count > 0 ? "; " + Issues[0] : string.Empty) + ")";
        }

        public override string ToString()
        {
            return "TreeReloadResult(" + Describe() + ")";
        }
    }

    /// <summary>待处理的请求（控制面/编辑器线程入队，游戏线程消费）。</summary>
    internal sealed class PackageReloadRequest
    {
        public string Value;

        public string Hash;

        public ReloadTrigger Trigger;

        public long Sequence;
    }

    /// <summary>
    /// 推送式重载（P0-5，计划 §5）。
    ///
    /// 分工：
    ///   · **入队**（<see cref="Notify"/> / <see cref="RequestReload"/>）可由任意线程调用（控制面在网络线程收包）；
    ///   · **应用**（<see cref="ApplyPending"/>）只在游戏线程、**tick 边界**调用 —— 绝不在节点执行中途换树。
    ///
    /// 铁律：
    ///   · 每次只读"被通知的包 + 它的引用闭包"，读完立即关句柄，**不锁文件、不保留句柄**；
    ///   · 任何失败（半截文件、校验不过、编译不过）都**保留旧树**并记录原因，绝不半加载；
    ///   · 通知带的哈希与文件实际哈希不符 → 忽略（对方还在写）。
    /// </summary>
    public sealed class PackageReloader
    {
        /// <summary>一帧最多处理多少个待处理请求（防止编辑器连发把一帧拖长）。</summary>
        public int MaxRequestsPerFrame = 4;

        public int HistoryCapacity = 32;

        private readonly object m_gate = new object();
        private readonly Queue<PackageReloadRequest> m_pending = new Queue<PackageReloadRequest>();
        private readonly List<TreeReloadResult> m_history = new List<TreeReloadResult>();
        private readonly Dictionary<string, DateTime> m_fileTimes =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private long m_sequence;
        private double m_watchAccumulator;

        /// <summary>事件日志（P0-10）：重载的成败与原因都留一份，便于事后排查。可为 null。</summary>
        public AiEventLog Log { get; set; }

        public PackageReloader(PackageRoots roots, PackageLoadOptions options = null)
        {
            Roots = roots;
            Options = options ?? new PackageLoadOptions();
            if (Options.Roots == null)
                Options.Roots = roots;
        }

        public PackageRoots Roots { get; }

        public PackageLoadOptions Options { get; }

        /// <summary>可选低频扫描间隔（秒）；0 = 关闭（默认，计划 §5.1 兜底项 3）。</summary>
        public double PackageWatchSeconds { get; set; }

        public string ActivePath { get; private set; }

        public string ActiveHash { get; private set; }

        public ScbtPackageSet ActiveSet { get; private set; }

        public CompiledTree ActiveTree { get; private set; }

        public int ReloadCount { get; private set; }

        public int RejectedCount { get; private set; }

        public int IgnoredCount { get; private set; }

        public TreeReloadResult LastResult { get; private set; }

        public bool IsLoaded
        {
            get { return ActiveTree != null && ActiveTree.Root != null && !ActiveTree.HasErrors; }
        }

        public int LoadedPackageCount
        {
            get { return ActiveSet != null ? ActiveSet.Packages.Count : 0; }
        }

        public IReadOnlyList<TreeReloadResult> History
        {
            get { return m_history; }
        }

        /// <summary>入队一个推送通知（计划 §5.1：编辑器保存后通知 path + hash）。</summary>
        public void Notify(string path, string hash)
        {
            Enqueue(new PackageReloadRequest
            {
                Value = path,
                Hash = hash,
                Trigger = ReloadTrigger.Notify
            });
        }

        /// <summary>入队一次手动重载（`ai.tree.reload` 兜底）。</summary>
        public void RequestReload(string pathOrName)
        {
            Enqueue(new PackageReloadRequest
            {
                Value = pathOrName,
                Trigger = ReloadTrigger.Manual
            });
        }

        private void Enqueue(PackageReloadRequest request)
        {
            if (request == null || string.IsNullOrEmpty(request.Value))
                return;
            lock (m_gate)
            {
                request.Sequence = ++m_sequence;
                m_pending.Enqueue(request);
            }
        }

        public int PendingCount
        {
            get
            {
                lock (m_gate)
                    return m_pending.Count;
            }
        }

        /// <summary>清空待处理请求（卸载/世界重载时用）。</summary>
        public void ClearPending()
        {
            lock (m_gate)
                m_pending.Clear();
        }

        // ---------------------------------------------------------------- 游戏线程：装载与应用

        /// <summary>
        /// 同步装载一棵树（首次装载 / `ai.tree.switch`）。
        /// <paramref name="migrate"/> 只在"同一个运行时已有旧树"时有意义：切换树通常应从头开始。
        /// </summary>
        public TreeReloadResult Load(string pathOrName, BtRuntime runtime, bool migrate = false,
            ReloadTrigger trigger = ReloadTrigger.Initial)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = new TreeReloadResult
            {
                Trigger = trigger,
                Path = pathOrName,
                PreviousHash = ActiveHash
            };

            string resolved = Roots != null
                ? Roots.Resolve(pathOrName, Options.Source.Exists)
                : PackageRoots.NormalizePath(pathOrName);
            if (resolved == null)
            {
                stopwatch.Stop();
                result.Rejected = true;
                result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
                result.Reason = "package not found";
                result.Issues.Add(PackageCodes.FileMissing + ": " + pathOrName
                    + " (roots: " + (Roots != null ? Roots.Describe() : "<none>") + ")");
                Record(result);
                return result;
            }

            resolved = PackageRoots.NormalizePath(resolved) ?? resolved;
            result.Path = resolved;

            ScbtPackageSet set = PackageLoader.Load(resolved, Options);
            string hash = set.Root != null ? set.Root.Hash : null;
            result.Hash = hash;

            if (set.Root == null || set.HasErrors)
            {
                stopwatch.Stop();
                result.Rejected = true;
                result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
                result.Reason = set.Root == null ? "package could not be read" : "package has errors";
                AddIssues(result, set);
                Record(result);
                return result;
            }

            CompiledTree compiled = TreeCompiler.Compile(set, set.Root);
            if (compiled.HasErrors || compiled.Root == null)
            {
                stopwatch.Stop();
                result.Rejected = true;
                result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
                result.Reason = "compiled tree has errors";
                result.Issues.AddRange(compiled.Report.Summarize(8));
                Record(result);
                return result;
            }

            if (runtime != null)
            {
                BtMigrationReport migration = runtime.ReplaceRoot(compiled.Root, migrate,
                    compiled.PackageId, compiled.SourcePath, compiled.SourceHash);
                result.Migration = migration;
                if (!runtime.IsRunning)
                    runtime.Start();
            }

            stopwatch.Stop();
            result.Replaced = true;
            result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
            result.PackageCount = set.Packages.Count;
            result.NodeCount = compiled.NodeCount;
            result.Reason = "loaded";
            result.Issues.AddRange(compiled.Report.Summarize(4));

            ActivePath = resolved;
            ActiveHash = hash;
            ActiveSet = set;
            ActiveTree = compiled;
            RememberFileTimes(set);
            ReloadCount++;
            Record(result);
            Engine.Log.Information("[PlayerAi][pkg] tree loaded: " + compiled.Describe()
                + " from " + resolved);
            WriteLog("load", result);
            return result;
        }

        /// <summary>
        /// 把"已经装载并编译好的树"认成活动树 —— 供 <see cref="TreeLibrary"/> 的切换路径使用
        /// （树库里常驻的副本是预编译好的，不需要再读盘）。
        ///
        /// 为什么必须调它：重载的**相关性判定**以"活动树是谁"为准；切换后如果不认，
        /// 编辑器针对新活动树发的 `ai.tree.notify` 会被判成"与本活动树无关"而拒掉。
        /// </summary>
        public void Adopt(ScbtPackageSet set, CompiledTree compiled, string path, string hash)
        {
            if (set == null || compiled == null || compiled.Root == null)
                return;

            ActivePath = PackageRoots.NormalizePath(path) ?? path;
            ActiveSet = set;
            ActiveTree = compiled;
            ActiveHash = hash;
            RememberFileTimes(set);
        }

        /// <summary>
        /// 处理所有排队请求（**tick 边界**调用）。返回 true 表示发生过替换。
        /// 每个请求独立成败：坏包只影响它自己，旧树继续工作。
        /// </summary>
        public bool ApplyPending(BtRuntime runtime)
        {
            bool replaced = false;
            int processed = 0;

            while (processed < MaxRequestsPerFrame)
            {
                PackageReloadRequest request;
                lock (m_gate)
                {
                    if (m_pending.Count == 0)
                        break;
                    request = m_pending.Dequeue();
                }

                processed++;
                if (Process(request, runtime))
                    replaced = true;
            }
            return replaced;
        }

        /// <summary>可选低频扫描：发现文件被外部直接改动（编辑器没通知）时补一次重载。</summary>
        public void Tick(double deltaTime)
        {
            if (PackageWatchSeconds <= 0.0 || ActiveSet == null)
                return;

            m_watchAccumulator += deltaTime;
            if (m_watchAccumulator < PackageWatchSeconds)
                return;
            m_watchAccumulator = 0.0;

            for (int i = 0; i < ActiveSet.Packages.Count; i++)
            {
                LoadedPackage package = ActiveSet.Packages[i];
                DateTime stamp;
                if (!Options.Source.TryGetLastWriteTimeUtc(package.Path, out stamp))
                    continue;

                DateTime previous;
                if (m_fileTimes.TryGetValue(package.Path, out previous) && previous == stamp)
                    continue;

                Enqueue(new PackageReloadRequest
                {
                    Value = package.Path,
                    Trigger = ReloadTrigger.Watch
                });
                break; // 一次只补一个，下一轮再看其它文件
            }
        }

        /// <summary>编辑器/控制面用的预检：只校验不装载（保存前就能告诉用户哪儿错了）。</summary>
        public PackageReport Validate(string pathOrName)
        {
            var report = new PackageReport();
            string resolved = Roots != null
                ? Roots.Resolve(pathOrName, Options.Source.Exists)
                : PackageRoots.NormalizePath(pathOrName);
            if (resolved == null)
            {
                report.Error(PackageCodes.FileMissing, pathOrName ?? "<none>",
                    "package not found (roots: " + (Roots != null ? Roots.Describe() : "<none>") + ")");
                return report;
            }

            ScbtPackageSet set = PackageLoader.Load(resolved, Options);
            report.AddRange(set.Report);
            for (int i = 0; i < set.Packages.Count; i++)
                report.AddRange(set.Packages[i].Report);
            if (set.Root == null)
                return report;

            CompiledTree compiled = TreeCompiler.Compile(set, set.Root, null, report);
            if (compiled.Root == null && !report.HasErrors)
                report.Error(PackageCodes.CompileFailed, resolved, "tree could not be compiled");
            return report;
        }

        // ---------------------------------------------------------------- 内部

        private bool Process(PackageReloadRequest request, BtRuntime runtime)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = new TreeReloadResult
            {
                Trigger = request.Trigger,
                Path = request.Value,
                PreviousHash = ActiveHash
            };

            string resolved = Roots != null
                ? Roots.Resolve(request.Value, Options.Source.Exists)
                : PackageRoots.NormalizePath(request.Value);
            if (resolved == null)
            {
                stopwatch.Stop();
                result.Rejected = true;
                result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
                result.Reason = "package not found";
                result.Issues.Add(PackageCodes.FileMissing + ": " + request.Value);
                Record(result);
                return false;
            }

            resolved = PackageRoots.NormalizePath(resolved) ?? resolved;
            result.Path = resolved;

            // 通知里的哈希：先把"被通知的那个文件"读出来核对（计划 §5.1）
            if (!string.IsNullOrEmpty(request.Hash))
            {
                byte[] bytes;
                string readError;
                if (!Options.Source.TryReadAllBytes(resolved, out bytes, out readError))
                {
                    stopwatch.Stop();
                    result.Rejected = true;
                    result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
                    result.Reason = "cannot read the notified file";
                    result.Issues.Add(PackageCodes.FileUnreadable + ": " + readError);
                    Record(result);
                    return false;
                }

                string actual = PackageLoader.ComputeHash(bytes);
                result.Hash = actual;
                if (!string.Equals(NormalizeHash(actual), NormalizeHash(request.Hash),
                    StringComparison.OrdinalIgnoreCase))
                {
                    stopwatch.Stop();
                    result.Ignored = true;
                    result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
                    result.Reason = "hash mismatch (writer may still be writing)";
                    IgnoredCount++;
                    Record(result);
                    WriteLog("ignore", result);
                    return false;
                }
            }

            // 相关性：只处理"活动树自己或它的某个引用包"
            if (ActivePath == null)
            {
                // 还没有活动树：这条通知就当首次装载
                TreeReloadResult load = Load(resolved, runtime, false, request.Trigger);
                return load.Replaced;
            }

            bool relevant = string.Equals(resolved, ActivePath, PackageRoots.PathComparison);
            if (!relevant && ActiveSet != null)
                relevant = ActiveSet.FindByPath(resolved) != null;

            if (!relevant)
            {
                stopwatch.Stop();
                result.Rejected = true;
                result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
                result.Reason = "not part of the active tree";
                result.Issues.Add("active tree is " + ActivePath);
                Record(result);
                WriteLog("reject", result);
                return false;
            }

            // 哈希没变 = 内容没变：什么都不做（也不算失败）
            ScbtPackageSet current = ActiveSet;
            LoadedPackage known = current != null ? current.FindByPath(resolved) : null;
            if (!string.IsNullOrEmpty(result.Hash) && known != null
                && string.Equals(known.Hash, result.Hash, StringComparison.OrdinalIgnoreCase))
            {
                stopwatch.Stop();
                result.Ignored = true;
                result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
                result.Reason = "content unchanged";
                IgnoredCount++;
                Record(result);
                WriteLog("ignore", result);
                return false;
            }

            // 重新装载整棵活动树（引用闭包一起刷新），失败就保留旧树
            ScbtPackageSet set = PackageLoader.Load(ActivePath, Options);
            string rootHash = set.Root != null ? set.Root.Hash : null;
            if (result.Hash == null)
                result.Hash = rootHash;

            if (set.Root == null || set.HasErrors)
            {
                stopwatch.Stop();
                result.Rejected = true;
                result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
                result.Reason = set.Root == null
                    ? "active package could not be read (old tree kept)"
                    : "active package has errors (old tree kept)";
                AddIssues(result, set);
                Record(result);
                Engine.Log.Warning("[PlayerAi][pkg] reload rejected: " + result.Describe());
                WriteLog("reject", result);
                return false;
            }

            CompiledTree compiled = TreeCompiler.Compile(set, set.Root);
            if (compiled.HasErrors || compiled.Root == null)
            {
                stopwatch.Stop();
                result.Rejected = true;
                result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
                result.Reason = "compiled tree has errors (old tree kept)";
                result.Issues.AddRange(compiled.Report.Summarize(8));
                Record(result);
                Engine.Log.Warning("[PlayerAi][pkg] reload rejected: " + result.Describe());
                WriteLog("reject", result);
                return false;
            }

            BtMigrationReport migration = runtime != null
                ? runtime.ReplaceRoot(compiled.Root, true, compiled.PackageId,
                    compiled.SourcePath, compiled.SourceHash)
                : null;

            stopwatch.Stop();
            result.Replaced = true;
            result.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
            result.PackageCount = set.Packages.Count;
            result.NodeCount = compiled.NodeCount;
            result.Migration = migration;
            result.Reason = "reloaded";
            result.Issues.AddRange(compiled.Report.Summarize(4));

            ActiveHash = rootHash;
            ActiveSet = set;
            ActiveTree = compiled;
            RememberFileTimes(set);
            ReloadCount++;
            Record(result);
            Engine.Log.Information("[PlayerAi][pkg] hot reload OK: " + compiled.Describe()
                + " (" + result.Milliseconds.ToString("0.0") + "ms)");
            WriteLog("reload", result);
            return true;
        }

        private void RememberFileTimes(ScbtPackageSet set)
        {
            m_fileTimes.Clear();
            if (set == null)
                return;
            for (int i = 0; i < set.Packages.Count; i++)
            {
                DateTime stamp;
                if (Options.Source.TryGetLastWriteTimeUtc(set.Packages[i].Path, out stamp))
                    m_fileTimes[set.Packages[i].Path] = stamp;
            }
        }

        /// <summary>把一次重载/切换结果写进事件日志（P0-10）。</summary>
        private void WriteLog(string category, TreeReloadResult result)
        {
            if (Log == null || result == null)
                return;

            string line = result.Describe();
            if (result.Issues.Count > 0)
                line += " | " + result.Issues[0];
            Log.Write(category, line);
        }

        private static string NormalizeHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
                return string.Empty;
            return hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? hash.Substring(7).Trim()
                : hash.Trim();
        }

        private static void AddIssues(TreeReloadResult result, ScbtPackageSet set)
        {
            var lines = set.Summarize(8);
            for (int i = 0; i < lines.Count; i++)
                result.Issues.Add(lines[i]);
        }

        private void Record(TreeReloadResult result)
        {
            LastResult = result;
            if (result.Rejected)
                RejectedCount++;

            m_history.Add(result);
            while (m_history.Count > HistoryCapacity)
                m_history.RemoveAt(0);
        }

        public string Describe()
        {
            return "PackageReloader(active=" + (ActivePath ?? "<none>")
                + " packages=" + LoadedPackageCount
                + " reloads=" + ReloadCount + " rejected=" + RejectedCount
                + " ignored=" + IgnoredCount + " pending=" + PendingCount + ")";
        }

        public override string ToString()
        {
            return Describe();
        }
    }
}
