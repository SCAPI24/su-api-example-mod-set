using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PlayerAiMod
{
    /// <summary>一棵**预编译常驻**的树（P0-11）。</summary>
    public sealed class PreparedTree
    {
        /// <summary>用户请求时用的名字（包名或路径）。</summary>
        public string Key;

        /// <summary>归一化后的绝对路径。</summary>
        public string Path;

        /// <summary>编译时的文件哈希：切换前比对，变了就重编译。</summary>
        public string Hash;

        public string EntryId;

        public ScbtPackageSet Set;

        public CompiledTree Compiled;

        public double LoadMilliseconds;

        public double CompileMilliseconds;

        /// <summary>被切换过几次（观测用）。</summary>
        public long SwitchCount;

        public string PreparedUtc;

        public bool Ready
        {
            get { return Compiled != null && Compiled.Root != null && !Compiled.HasErrors; }
        }

        public int NodeCount
        {
            get { return Compiled != null ? Compiled.NodeCount : 0; }
        }

        public string Describe()
        {
            return (Key ?? "?") + " nodes=" + NodeCount
                + " hash=" + (Hash != null && Hash.Length >= 8 ? Hash.Substring(0, 8) : "?")
                + " compiled=" + CompileMilliseconds.ToString("0.0") + "ms"
                + " switches=" + SwitchCount
                + (Ready ? string.Empty : " NOT-READY");
        }

        public override string ToString()
        {
            return "PreparedTree(" + Describe() + ")";
        }
    }

    /// <summary>一次"切换活动树"的结果（含**切换耗时**：验收标准 6 要看这个）。</summary>
    public sealed class TreeSwitchResult
    {
        public bool Switched;

        /// <summary>是从常驻库里直接换的（没有重新编译）——这才是"毫秒级切换"。</summary>
        public bool UsedPrepared;

        /// <summary>因为库里的副本过期（文件变了）而重新编译。</summary>
        public bool Recompiled;

        public string Reason;

        public string Path;

        public string Hash;

        /// <summary>交换指针 + 迁移运行态的耗时（不含编译/读盘）。</summary>
        public double SwitchMilliseconds;

        /// <summary>本次调用的总耗时（含可能的重编译）。</summary>
        public double TotalMilliseconds;

        public double CompileMilliseconds;

        public int NodeCount;

        public BtMigrationReport Migration;

        public readonly List<string> Issues = new List<string>();

        public string Describe()
        {
            if (!Switched)
                return "switch failed: " + (Reason ?? "?")
                    + (Issues.Count > 0 ? " (" + Issues[0] + ")" : string.Empty);

            return "switched to " + System.IO.Path.GetFileName(Path ?? "?")
                + " nodes=" + NodeCount
                + " prepared=" + UsedPrepared + (Recompiled ? " (recompiled)" : string.Empty)
                + " switch=" + SwitchMilliseconds.ToString("0.00") + "ms"
                + " total=" + TotalMilliseconds.ToString("0.00") + "ms";
        }

        public override string ToString()
        {
            return "TreeSwitchResult(" + Describe() + ")";
        }
    }

    /// <summary>
    /// 树库（P0-11，计划 §3.6 / D10）：**把编译成本提前付掉**。
    ///
    /// 需求原话是"AI 的思维可能很慢，但一旦决定切换，就要能尽快切换"：
    ///   · `Prepare` 把"读包 + 校验 + 编译"全做完，对象图常驻内存；
    ///   · `Switch` 只做三件事 —— 比对哈希（文件没变就跳过）、把根指针换掉、迁移运行态。
    ///     于是切换耗时与树的大小无关，是**几十微秒级**的指针交换（自检里给出实测数字）。
    ///
    /// 两条必须注意的语义：
    ///   1. 常驻的对象图**带运行态**：每次切换前必须 `ResetSubtreeState`（干净起点），
    ///      否则第二次切回同一棵树会继承上一次的"跑到哪了"。
    ///   2. 切换后要让**热重载**也认这棵新树为活动树（`PackageReloader.Adopt`），
    ///      否则编辑器再发 `ai.tree.notify` 会被判成"与活动树无关"。
    /// </summary>
    public sealed class TreeLibrary
    {
        /// <summary>常驻上限（超过就按最久未用淘汰），避免"prepare all"把内存吃光。</summary>
        public const int DefaultCapacity = 8;

        private readonly List<PreparedTree> m_prepared = new List<PreparedTree>();
        private readonly PackageReloader m_reloader;

        public TreeLibrary(PackageReloader reloader, int capacity = DefaultCapacity)
            : this(reloader != null ? reloader.Roots : null,
                   reloader != null ? reloader.Options : null, capacity)
        {
            m_reloader = reloader;
        }

        public TreeLibrary(PackageRoots roots, PackageLoadOptions options = null,
            int capacity = DefaultCapacity)
        {
            Roots = roots;
            Options = options ?? new PackageLoadOptions { Roots = roots };
            if (Options.Roots == null)
                Options.Roots = roots;
            Capacity = capacity > 0 ? capacity : DefaultCapacity;
        }

        /// <summary>事件日志（P0-10）：预编译与切换结果。可为 null。</summary>
        public AiEventLog Log { get; set; }

        public PackageRoots Roots { get; }

        public PackageLoadOptions Options { get; }

        public int Capacity { get; set; }

        public int PrepareCount { get; private set; }

        public int SwitchCount { get; private set; }

        /// <summary>因为"库里的副本过期"而不得不重编译的次数（理想情况是 0）。</summary>
        public int StaleRecompiles { get; private set; }

        /// <summary>最近一次切换结果。</summary>
        public TreeSwitchResult LastSwitch { get; private set; }

        /// <summary>当前活动的预编译树（没经库切换时为 null）。</summary>
        public PreparedTree Active { get; private set; }

        public IReadOnlyList<PreparedTree> Prepared
        {
            get { return m_prepared; }
        }

        // ---------------------------------------------------------------- 预编译

        /// <summary>
        /// 预编译一棵树并常驻。同一个包重复 prepare 会**替换**旧副本（文件已变的常见情形）。
        /// 失败时返回的 <see cref="PreparedTree.Ready"/> 为 false，原因在报告里。
        /// </summary>
        public PreparedTree Prepare(string nameOrPath, string entryId = null,
            PackageReport report = null)
        {
            PackageReport target = report ?? new PackageReport();
            var stopwatch = Stopwatch.StartNew();

            string path = Resolve(nameOrPath);
            if (path == null)
            {
                target.Error(PackageCodes.FileMissing, nameOrPath ?? "<none>",
                    "package not found (roots: " + (Roots != null ? Roots.Describe() : "<none>") + ")");
                stopwatch.Stop();
                return new PreparedTree
                {
                    Key = nameOrPath,
                    CompileMilliseconds = stopwatch.Elapsed.TotalMilliseconds
                };
            }

            var prepared = new PreparedTree
            {
                Key = nameOrPath,
                Path = path,
                EntryId = entryId,
                PreparedUtc = DateTime.UtcNow.ToString("o")
            };

            ScbtPackageSet set = PackageLoader.Load(path, Options);
            prepared.Set = set;
            prepared.LoadMilliseconds = set.LoadMilliseconds;
            prepared.Hash = set.Root != null ? set.Root.Hash : null;

            if (set.Root == null || set.HasErrors)
            {
                AddIssues(target, set);
                stopwatch.Stop();
                prepared.CompileMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                Store(prepared);
                return prepared;
            }

            CompiledTree compiled = TreeCompiler.Compile(set, set.Root, entryId, target);
            prepared.Compiled = compiled;
            prepared.CompileMilliseconds = compiled.CompileMilliseconds;

            if (compiled.HasErrors || compiled.Root == null)
                AddIssues(target, compiled);

            stopwatch.Stop();
            prepared.CompileMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            PrepareCount++;
            Store(prepared);

            if (PlayerAiConfig.VerboseLogging)
                Engine.Log.Information("[PlayerAi][lib] prepared " + prepared.Describe());

            if (Log != null)
                Log.Write("prepare", prepared.Describe());
            return prepared;
        }

        /// <summary>
        /// 预编译目录里能找到的全部包（`ai.tree.prepare all=true`）。
        /// 受 <see cref="Capacity"/> 限制：装不下就只留前 N 个（按文件名序）。
        /// </summary>
        public List<PreparedTree> PrepareAll(int maximum = 0, PackageReport report = null)
        {
            var result = new List<PreparedTree>();
            if (Roots == null)
                return result;

            int limit = maximum > 0 ? maximum : Capacity;
            List<string> files = Roots.ListFiles();
            for (int i = 0; i < files.Count && result.Count < limit; i++)
            {
                PreparedTree prepared = Prepare(files[i], null, report);
                result.Add(prepared);
            }
            return result;
        }

        /// <summary>查已常驻的副本（按路径或请求名匹配）。</summary>
        public bool TryGetPrepared(string nameOrPath, out PreparedTree prepared)
        {
            prepared = null;
            string path = Resolve(nameOrPath);
            for (int i = 0; i < m_prepared.Count; i++)
            {
                PreparedTree candidate = m_prepared[i];
                bool samePath = path != null && candidate.Path != null
                    && string.Equals(candidate.Path, path, PackageRoots.PathComparison);
                bool sameKey = !string.IsNullOrEmpty(nameOrPath) && candidate.Key != null
                    && string.Equals(candidate.Key, nameOrPath, StringComparison.OrdinalIgnoreCase);
                if (samePath || sameKey)
                {
                    prepared = candidate;
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- 切换

        /// <summary>
        /// 切换到指定包：优先用常驻副本（**毫秒级**），副本过期才重编译。
        /// 切换必须由调用方在**游戏线程的 tick 边界**调用（控制面命令天然满足）。
        /// </summary>
        public TreeSwitchResult Switch(string nameOrPath, IAiTreeHost host, string entryId = null,
            bool start = true)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new TreeSwitchResult { Path = nameOrPath };

            if (host == null || host.Tree == null)
            {
                stopwatch.Stop();
                result.TotalMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                result.Reason = "no behaviour tree host (load a world first)";
                LastSwitch = result;
                return result;
            }

            string path = Resolve(nameOrPath);
            if (path == null)
            {
                stopwatch.Stop();
                result.TotalMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                result.Reason = "package not found";
                result.Issues.Add(PackageCodes.FileMissing + ": " + nameOrPath);
                LastSwitch = result;
                return result;
            }
            result.Path = path;

            string currentHash = HashOf(path);
            result.Hash = currentHash;

            PreparedTree prepared;
            bool havePrepared = TryGetPrepared(path, out prepared) && prepared.Ready;
            bool stale = havePrepared
                && !string.Equals(prepared.Hash, currentHash, StringComparison.OrdinalIgnoreCase);

            if (!havePrepared || stale)
            {
                var report = new PackageReport();
                prepared = Prepare(path, entryId, report);
                result.Recompiled = havePrepared;
                if (prepared.Ready)
                    result.Issues.AddRange(report.Summarize(4));
                if (!prepared.Ready)
                {
                    stopwatch.Stop();
                    AddIssues(result, report);
                    result.TotalMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                    result.Reason = "package could not be prepared";
                    LastSwitch = result;
                    if (havePrepared && stale)
                        StaleRecompiles++;
                    return result;
                }
                if (havePrepared && stale)
                    StaleRecompiles++;
            }
            else
            {
                result.UsedPrepared = true;
            }

            result.CompileMilliseconds = prepared.CompileMilliseconds;

            // ---- 真正的"切换"：清运行态 + 换根指针 + 迁移运行态（都与树的大小无关的固定开销）
            var switchWatch = Stopwatch.StartNew();
            prepared.Compiled.Root.ResetSubtreeState();
            BtMigrationReport migration = host.Tree.ReplaceRoot(prepared.Compiled.Root, false,
                prepared.Compiled.PackageId, prepared.Compiled.SourcePath, currentHash);
            if (start && !host.Tree.IsRunning)
                host.Tree.Start();
            host.Enabled = true;
            switchWatch.Stop();

            result.Switched = true;
            result.NodeCount = prepared.Compiled.NodeCount;
            result.SwitchMilliseconds = switchWatch.Elapsed.TotalMilliseconds;
            result.Migration = migration;
            result.Reason = result.UsedPrepared ? "prepared (no compile)" : "prepared on demand";

            prepared.SwitchCount++;
            Active = prepared;
            SwitchCount++;

            // 让热重载也认这棵新树为"活动树"（否则编辑器再 notify 会被判成无关）
            if (m_reloader != null && prepared.Set != null)
                m_reloader.Adopt(prepared.Set, prepared.Compiled, path, currentHash);

            stopwatch.Stop();
            result.TotalMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            LastSwitch = result;

            Engine.Log.Information("[PlayerAi][lib] " + result.Describe());
            if (Log != null)
                Log.Write(result.Switched ? "switch" : "switch-reject", result.Describe());
            return result;
        }

        /// <summary>只校验不编译（编辑器保存前预检用；与重载器的 Validate 同源）。</summary>
        public PackageReport Validate(string nameOrPath)
        {
            if (m_reloader != null)
                return m_reloader.Validate(nameOrPath);

            var report = new PackageReport();
            string path = Resolve(nameOrPath);
            if (path == null)
            {
                report.Error(PackageCodes.FileMissing, nameOrPath ?? "<none>", "package not found");
                return report;
            }

            ScbtPackageSet set = PackageLoader.Load(path, Options);
            report.AddRange(set.Report);
            for (int i = 0; i < set.Packages.Count; i++)
                report.AddRange(set.Packages[i].Report);
            if (set.Root != null)
                TreeCompiler.Compile(set, set.Root, null, report);
            return report;
        }

        /// <summary>丢掉全部常驻副本（包目录变了、卸载时用）。</summary>
        public void Clear()
        {
            m_prepared.Clear();
            Active = null;
        }

        public string Describe()
        {
            return "TreeLibrary(prepared=" + m_prepared.Count + "/" + Capacity
                + " prepares=" + PrepareCount + " switches=" + SwitchCount
                + " stale=" + StaleRecompiles
                + (Active != null ? " active=" + Active.Key : string.Empty) + ")";
        }

        public List<Dictionary<string, object>> DescribePrepared()
        {
            var list = new List<Dictionary<string, object>>();
            for (int i = 0; i < m_prepared.Count; i++)
            {
                PreparedTree prepared = m_prepared[i];
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["name"] = prepared.Key,
                    ["file"] = prepared.Path != null
                        ? System.IO.Path.GetFileName(prepared.Path) : null,
                    ["ready"] = prepared.Ready,
                    ["nodes"] = prepared.NodeCount,
                    ["hash"] = prepared.Hash != null && prepared.Hash.Length >= 12
                        ? prepared.Hash.Substring(0, 12) : prepared.Hash,
                    ["compileMs"] = Math.Round(prepared.CompileMilliseconds, 2),
                    ["switches"] = prepared.SwitchCount,
                    ["active"] = ReferenceEquals(prepared, Active)
                });
            }
            return list;
        }

        public override string ToString()
        {
            return Describe();
        }

        // ---------------------------------------------------------------- 内部

        private string Resolve(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath))
                return null;
            return Roots != null
                ? Roots.Resolve(nameOrPath, Options.Source.Exists)
                : PackageRoots.NormalizePath(nameOrPath);
        }

        private string HashOf(string path)
        {
            byte[] bytes;
            string error;
            if (Options.Source == null || !Options.Source.TryReadAllBytes(path, out bytes, out error))
                return null;
            return PackageLoader.ComputeHash(bytes);
        }

        private void Store(PreparedTree prepared)
        {
            // 同一个包只留一份副本（按路径替换）
            for (int i = 0; i < m_prepared.Count; i++)
            {
                PreparedTree existing = m_prepared[i];
                bool samePath = prepared.Path != null && existing.Path != null
                    && string.Equals(existing.Path, prepared.Path, PackageRoots.PathComparison);
                if (samePath || (prepared.Path == null && string.Equals(existing.Key, prepared.Key,
                    StringComparison.OrdinalIgnoreCase)))
                {
                    m_prepared[i] = prepared;
                    if (ReferenceEquals(Active, existing))
                        Active = prepared;
                    return;
                }
            }

            m_prepared.Add(prepared);

            // 超容量：淘汰最久未用的（保留 Active）
            while (m_prepared.Count > Capacity)
            {
                int victim = -1;
                for (int i = 0; i < m_prepared.Count; i++)
                {
                    if (ReferenceEquals(m_prepared[i], Active))
                        continue;
                    if (victim < 0 || m_prepared[i].SwitchCount < m_prepared[victim].SwitchCount)
                        victim = i;
                }
                if (victim < 0)
                    break;
                m_prepared.RemoveAt(victim);
            }
        }

        private static void AddIssues(PackageReport report, ScbtPackageSet set)
        {
            List<string> lines = set.Summarize(8);
            for (int i = 0; i < lines.Count; i++)
                report.Error(PackageCodes.CompileFailed, "prepare", lines[i]);
        }

        private static void AddIssues(PackageReport target, CompiledTree compiled)
        {
            List<string> lines = compiled.Report.Summarize(8);
            for (int i = 0; i < lines.Count; i++)
                target.Error(PackageCodes.CompileFailed, "prepare", lines[i]);
        }

        private static void AddIssues(TreeSwitchResult result, PackageReport report)
        {
            List<string> lines = report.Summarize(8);
            for (int i = 0; i < lines.Count; i++)
                result.Issues.Add(lines[i]);
        }
    }
}
