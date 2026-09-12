using Engine;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace PlayerAiMod
{
    /// <summary>包字节的来源（磁盘 / 内存）。抽出来是为了让自检不落盘、也让推送重载能直接喂字节。</summary>
    public interface IPackageSource
    {
        bool Exists(string path);

        bool TryReadAllBytes(string path, out byte[] bytes, out string error);

        /// <summary>最后写入时间（低频扫描兜底用；内存数据源返回 false）。</summary>
        bool TryGetLastWriteTimeUtc(string path, out DateTime utc);
    }

    /// <summary>磁盘来源：一次性读完就关句柄（计划 §4.2"不保留句柄"）。</summary>
    public sealed class FilePackageSource : IPackageSource
    {
        public static readonly FilePackageSource Instance = new FilePackageSource();

        public bool Exists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool TryReadAllBytes(string path, out byte[] bytes, out string error)
        {
            bytes = null;
            error = null;
            try
            {
                // File.ReadAllBytes 内部是 FileShare.Read：读的这一刻别的进程仍可读，
                // 读完立即释放 —— 编辑器随后写同名文件不会被我们锁住。
                bytes = File.ReadAllBytes(path);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        public bool TryGetLastWriteTimeUtc(string path, out DateTime utc)
        {
            utc = default(DateTime);
            try
            {
                if (!File.Exists(path))
                    return false;
                utc = File.GetLastWriteTimeUtc(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>内存来源（自检用）：路径 → 字节。</summary>
    public sealed class MemoryPackageSource : IPackageSource
    {
        private readonly Dictionary<string, byte[]> m_files =
            new Dictionary<string, byte[]>(PackageRoots.PathComparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        public void Add(string path, byte[] bytes)
        {
            string key = PackageRoots.NormalizePath(path) ?? path;
            m_files[key] = bytes;
        }

        public bool Exists(string path)
        {
            return m_files.ContainsKey(PackageRoots.NormalizePath(path) ?? path);
        }

        public bool TryReadAllBytes(string path, out byte[] bytes, out string error)
        {
            error = null;
            if (m_files.TryGetValue(PackageRoots.NormalizePath(path) ?? path, out bytes))
                return true;
            error = "not in memory source";
            return false;
        }

        /// <summary>内存数据源没有"写入时间"概念，低频扫描对它天然失效（自检用不到扫描）。</summary>
        public bool TryGetLastWriteTimeUtc(string path, out DateTime utc)
        {
            utc = default(DateTime);
            return false;
        }

        public int Count
        {
            get { return m_files.Count; }
        }
    }

    /// <summary>装载选项（默认值就是生产路径用的那套）。</summary>
    public sealed class PackageLoadOptions
    {
        public IPackageSource Source = FilePackageSource.Instance;

        /// <summary>包目录白名单。为 null 表示"调用方已校验过路径"，跳过越界检查（自检与绝对路径入口）。</summary>
        public PackageRoots Roots;

        public int MaxDepth = 8;

        public int MaxPackages = 64;

        public long MaxPackageBytes = 16L * 1024L * 1024L;

        /// <summary>是否在装载时就做结构校验（生产总是 true；关掉只用于"先看看能不能读"）。</summary>
        public bool Validate = true;
    }

    /// <summary>一个已经读进内存的包（zip 句柄已关闭，只剩数据与校验结果）。</summary>
    public sealed class LoadedPackage
    {
        public string Path;

        public string Directory;

        public string FileName;

        public string Hash;

        public long ByteCount;

        public double LoadMilliseconds;

        public ScbtManifest Manifest;

        public ScbtTree Tree;

        /// <summary>与 <see cref="ScbtManifest.References"/> 下标一一对应（加载失败的为 null）。</summary>
        public readonly List<LoadedPackage> ReferenceTargets = new List<LoadedPackage>();

        public readonly PackageReport Report = new PackageReport();

        public bool HasErrors
        {
            get { return Report.HasErrors; }
        }

        public string PackageId
        {
            get { return Manifest != null ? Manifest.Id : null; }
        }

        public LoadedPackage FindReference(string referenceId)
        {
            if (Manifest == null || string.IsNullOrEmpty(referenceId))
                return null;
            for (int i = 0; i < Manifest.References.Count; i++)
            {
                if (!string.Equals(Manifest.References[i].Id, referenceId, StringComparison.OrdinalIgnoreCase))
                    continue;
                return i < ReferenceTargets.Count ? ReferenceTargets[i] : null;
            }
            return null;
        }

        public string Describe()
        {
            return (FileName ?? "?") + " id=" + (PackageId ?? "?")
                + " hash=" + (Hash != null && Hash.Length >= 8 ? Hash.Substring(0, 8) : "?")
                + " nodes=" + (Tree != null ? Tree.NodeCount : 0)
                + " refs=" + ReferenceTargets.Count
                + " load=" + LoadMilliseconds.ToString("0.0") + "ms"
                + (HasErrors ? " ERRORS=" + Report.ErrorCount : string.Empty);
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>一次装载的结果集：根包 + 它递归引用到的所有包 + 汇总报告。</summary>
    public sealed class ScbtPackageSet
    {
        public string RootPath;

        public LoadedPackage Root;

        /// <summary>加载顺序（被引用的包在前）。</summary>
        public readonly List<LoadedPackage> Packages = new List<LoadedPackage>();

        /// <summary>汇总报告（含各包的条目，位置前缀带包名）。</summary>
        public readonly PackageReport Report = new PackageReport();

        public double LoadMilliseconds;

        public bool HasErrors
        {
            get
            {
                if (Report.HasErrors)
                    return true;
                for (int i = 0; i < Packages.Count; i++)
                {
                    if (Packages[i].HasErrors)
                        return true;
                }
                return false;
            }
        }

        public int ErrorCount
        {
            get
            {
                int count = Report.ErrorCount;
                for (int i = 0; i < Packages.Count; i++)
                    count += Packages[i].Report.ErrorCount;
                return count;
            }
        }

        public int WarningCount
        {
            get
            {
                int count = Report.WarningCount;
                for (int i = 0; i < Packages.Count; i++)
                    count += Packages[i].Report.WarningCount;
                return count;
            }
        }

        public LoadedPackage FindByPath(string path)
        {
            string normalized = PackageRoots.NormalizePath(path);
            if (normalized == null)
                return null;
            for (int i = 0; i < Packages.Count; i++)
            {
                if (string.Equals(Packages[i].Path, normalized, PackageRoots.PathComparison))
                    return Packages[i];
            }
            return null;
        }

        public LoadedPackage FindById(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            for (int i = 0; i < Packages.Count; i++)
            {
                if (string.Equals(Packages[i].PackageId, id, StringComparison.OrdinalIgnoreCase))
                    return Packages[i];
            }
            return null;
        }

        public List<string> Summarize(int maxLines = 32)
        {
            var lines = new List<string>();
            for (int i = 0; i < Packages.Count; i++)
            {
                lines.Add(Packages[i].Describe());
                var issues = Packages[i].Report.Summarize(8);
                for (int j = 0; j < issues.Count; j++)
                    lines.Add("  " + issues[j]);
            }
            var rootIssues = Report.Summarize(8);
            for (int i = 0; i < rootIssues.Count; i++)
                lines.Add("  " + rootIssues[i]);

            if (lines.Count > maxLines)
            {
                lines.RemoveRange(maxLines, lines.Count - maxLines);
                lines.Add("... (truncated)");
            }
            return lines;
        }

        public string Describe()
        {
            return "PackageSet(packages=" + Packages.Count
                + " errors=" + ErrorCount + " warnings=" + WarningCount
                + " load=" + LoadMilliseconds.ToString("0.0") + "ms)";
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// 包加载器（P0-4）：**读一次、进内存、零运行期 IO**。
    ///
    /// 流程（计划 §4.2）：
    ///   1. 定位包文件（白名单目录内）；
    ///   2. 一次读完字节 → 算 SHA-256 → 打开 zip 读出 `manifest.json` 与 `tree.json` → **立刻关闭**；
    ///   3. 递归加载 `references`（相对路径、白名单内、环检测 + 深度/数量上限）；
    ///   4. 校验（<see cref="PackageValidator"/>）；
    ///   5. 需要可执行树时再调 <see cref="TreeCompiler.Compile"/>。
    ///
    /// 任何一步失败都记进报告而不是抛异常 —— 控制面要能把"为什么这个包装不上"完整回给调用者。
    /// </summary>
    public static class PackageLoader
    {
        public static ScbtPackageSet Load(string nameOrPath, PackageLoadOptions options = null)
        {
            options = options ?? new PackageLoadOptions();
            var set = new ScbtPackageSet();
            var stopwatch = Stopwatch.StartNew();

            string path = ResolvePath(nameOrPath, options, set.Report);
            set.RootPath = path;
            if (path == null)
            {
                stopwatch.Stop();
                set.LoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                return set;
            }

            var stack = new List<string>();
            set.Root = LoadPackage(path, set, options, 0, stack);
            stopwatch.Stop();
            set.LoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            if (set.Root != null && set.HasErrors)
                Engine.Log.Warning("[PlayerAi][pkg] " + System.IO.Path.GetFileName(path)
                    + " loaded with " + set.ErrorCount + " error(s), " + set.WarningCount + " warning(s)");
            return set;
        }

        /// <summary>直接喂字节（推送重载：编辑器写完文件后把内容送来，避免二次读盘竞态）。</summary>
        public static ScbtPackageSet LoadFromBytes(byte[] bytes, string sourcePath,
            PackageLoadOptions options = null)
        {
            options = options ?? new PackageLoadOptions();
            var set = new ScbtPackageSet();
            string path = PackageRoots.NormalizePath(sourcePath) ?? sourcePath;
            set.RootPath = path;

            var stopwatch = Stopwatch.StartNew();
            var memory = new MemoryPackageSource();
            memory.Add(path, bytes);
            var effective = new PackageLoadOptions
            {
                // 根包的字节来自参数；被引用的包仍走调用方给的数据源（磁盘或内存）
                Source = new OverlayPackageSource(memory, options.Source ?? FilePackageSource.Instance),
                Roots = options.Roots,
                MaxDepth = options.MaxDepth,
                MaxPackages = options.MaxPackages,
                MaxPackageBytes = options.MaxPackageBytes,
                Validate = options.Validate
            };

            var stack = new List<string>();
            set.Root = LoadPackage(path, set, effective, 0, stack);
            stopwatch.Stop();
            set.LoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return set;
        }

        /// <summary>先查内存、再落回磁盘的来源（推送重载：根包用送来的字节，引用仍从目录读）。</summary>
        private sealed class OverlayPackageSource : IPackageSource
        {
            private readonly MemoryPackageSource m_primary;
            private readonly IPackageSource m_fallback;

            public OverlayPackageSource(MemoryPackageSource primary, IPackageSource fallback)
            {
                m_primary = primary;
                m_fallback = fallback;
            }

            public bool Exists(string path)
            {
                return m_primary.Exists(path) || (m_fallback != null && m_fallback.Exists(path));
            }

            public bool TryReadAllBytes(string path, out byte[] bytes, out string error)
            {
                if (m_primary.TryReadAllBytes(path, out bytes, out error))
                    return true;
                if (m_fallback != null)
                    return m_fallback.TryReadAllBytes(path, out bytes, out error);
                return false;
            }

            public bool TryGetLastWriteTimeUtc(string path, out DateTime utc)
            {
                if (m_primary.TryGetLastWriteTimeUtc(path, out utc))
                    return true;
                if (m_fallback != null)
                    return m_fallback.TryGetLastWriteTimeUtc(path, out utc);
                utc = default(DateTime);
                return false;
            }
        }

        private static string ResolvePath(string nameOrPath, PackageLoadOptions options,
            PackageReport report)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath))
            {
                report.Error(PackageCodes.FileMissing, "<none>", "no package name or path was given");
                return null;
            }

            if (options.Roots != null)
            {
                // "存在"由数据源回答，而不是直接问磁盘 —— 推送重载与自检都不走磁盘
                string resolved = options.Roots.Resolve(nameOrPath, options.Source.Exists);
                if (resolved == null)
                {
                    report.Error(PackageCodes.FileMissing, nameOrPath,
                        "package not found in " + options.Roots.Describe());
                    return null;
                }
                return resolved;
            }

            string normalized = PackageRoots.NormalizePath(nameOrPath);
            if (normalized == null)
            {
                report.Error(PackageCodes.FileMissing, nameOrPath, "invalid path");
                return null;
            }
            return normalized;
        }

        private static LoadedPackage LoadPackage(string path, ScbtPackageSet set,
            PackageLoadOptions options, int depth, List<string> stack)
        {
            string normalized = PackageRoots.NormalizePath(path);
            if (normalized == null)
            {
                set.Report.Error(PackageCodes.FileMissing, path ?? "<null>", "invalid package path");
                return null;
            }

            if (options.Roots != null && !options.Roots.IsAllowed(normalized))
            {
                set.Report.Error(PackageCodes.ReferenceEscape, normalized,
                    "package is outside the allowed folders (" + options.Roots.Describe() + ")");
                return null;
            }

            if (depth > options.MaxDepth)
            {
                set.Report.Error(PackageCodes.ReferenceDepth, normalized,
                    "reference nesting deeper than " + options.MaxDepth + " levels");
                return null;
            }

            for (int i = 0; i < stack.Count; i++)
            {
                if (string.Equals(stack[i], normalized, PackageRoots.PathComparison))
                {
                    set.Report.Error(PackageCodes.ReferenceCycle, normalized,
                        "reference cycle: " + System.IO.Path.GetFileName(normalized)
                        + " is already being loaded");
                    return null;
                }
            }

            // 菱形引用（两个包引用同一个包）合法：读过的直接复用
            LoadedPackage existing = set.FindByPath(normalized);
            if (existing != null)
                return existing;

            if (set.Packages.Count >= options.MaxPackages)
            {
                set.Report.Error(PackageCodes.ReferenceCount, normalized,
                    "more than " + options.MaxPackages + " packages in one set");
                return null;
            }

            var package = new LoadedPackage
            {
                Path = normalized,
                Directory = System.IO.Path.GetDirectoryName(normalized),
                FileName = System.IO.Path.GetFileName(normalized)
            };

            var stopwatch = Stopwatch.StartNew();
            byte[] bytes;
            string error;
            if (!options.Source.Exists(normalized))
            {
                set.Report.Error(PackageCodes.FileMissing, normalized, "package file does not exist");
                return null;
            }
            if (!options.Source.TryReadAllBytes(normalized, out bytes, out error))
            {
                set.Report.Error(PackageCodes.FileUnreadable, normalized, "cannot read package: " + error);
                return null;
            }
            if (bytes == null || bytes.Length == 0)
            {
                set.Report.Error(PackageCodes.FileUnreadable, normalized, "package is empty");
                return null;
            }
            if (bytes.LongLength > options.MaxPackageBytes)
            {
                set.Report.Error(PackageCodes.FileTooLarge, normalized,
                    "package is " + bytes.LongLength + " bytes (limit " + options.MaxPackageBytes + ")");
                return null;
            }

            package.ByteCount = bytes.LongLength;
            package.Hash = ComputeHash(bytes);

            ScbtManifest manifest;
            ScbtTree tree;
            if (!ReadPackageEntries(package, bytes, out manifest, out tree))
            {
                // 读出失败：包本身入库（这样报告能被汇总到），但不参与后续引用解析
                stopwatch.Stop();
                package.LoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                set.Packages.Add(package);
                return null;
            }

            package.Manifest = manifest;
            package.Tree = tree;

            if (options.Validate)
            {
                manifest.Validate(package.FileName, package.Report);
                PackageValidator.ValidateTree(tree, manifest, package.FileName, package.Report);
            }

            set.Packages.Add(package);
            stopwatch.Stop();
            package.LoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            LoadedPackage duplicateId = set.FindById(manifest.Id);
            if (duplicateId != null && !ReferenceEquals(duplicateId, package))
            {
                package.Report.Error(PackageCodes.ManifestIdDuplicate, package.FileName,
                    "another loaded package already uses id '" + manifest.Id + "' ("
                    + duplicateId.FileName + ")");
            }

            // ---- 递归加载引用
            stack.Add(normalized);
            for (int i = 0; i < manifest.References.Count; i++)
            {
                ScbtReference reference = manifest.References[i];
                if (string.IsNullOrEmpty(reference.Path))
                {
                    package.ReferenceTargets.Add(null);
                    continue;
                }

                string referencePath = ResolveReferencePath(package, reference, options, set.Report);
                if (referencePath == null)
                {
                    package.ReferenceTargets.Add(null);
                    continue;
                }

                LoadedPackage target = LoadPackage(referencePath, set, options, depth + 1, stack);
                package.ReferenceTargets.Add(target);
                if (target == null)
                {
                    package.Report.Error(PackageCodes.ReferenceUnresolved,
                        package.FileName + ".references[" + i + "]",
                        "reference '" + reference.Id + "' -> '" + reference.Path + "' could not be loaded");
                }
            }
            stack.RemoveAt(stack.Count - 1);

            return package;
        }

        /// <summary>
        /// 解析 `references.path`：
        ///   1. 先按**本包所在目录**找；
        ///   2. 找不到就依次在各**包根目录**里找（实例目录 → Mod 分发目录）。
        ///
        /// 第 2 条是必须的：用户把出厂包从 Mod 目录复制到实例目录来改（计划 §4.4 的推荐流程），
        /// 它引用的 `common.scbtpak` 仍留在 Mod 目录 —— 只按本包目录找就会"复制过去就坏掉"；
        /// 导出到实例目录的新包同理。白名单校验在装载时仍然生效，安全性不变。
        /// </summary>
        private static string ResolveReferencePath(LoadedPackage package, ScbtReference reference,
            PackageLoadOptions options, PackageReport report)
        {
            string relative = reference.Path.Replace('\\', '/');
            if (relative.Length > 1 && relative[1] == ':')
            {
                report.Error(PackageCodes.ManifestReferencePath, package.FileName + ".references",
                    "reference '" + reference.Id + "' must use a relative path");
                return null;
            }

            string combined = PackageRoots.NormalizePath(System.IO.Path.Combine(
                package.Directory ?? string.Empty, relative));
            if (combined == null)
            {
                report.Error(PackageCodes.ManifestReferencePath, package.FileName + ".references",
                    "reference '" + reference.Id + "' has an invalid path '" + reference.Path + "'");
                return null;
            }

            if (options.Source.Exists(combined))
                return combined;

            if (options.Roots != null)
            {
                for (int i = 0; i < options.Roots.Roots.Count; i++)
                {
                    string candidate = PackageRoots.NormalizePath(System.IO.Path.Combine(
                        options.Roots.Roots[i].Path, relative));
                    if (candidate == null || !options.Roots.IsAllowed(candidate))
                        continue;
                    if (options.Source.Exists(candidate))
                        return candidate;
                }
            }

            // 都没找到：返回"本包目录下那个"路径，让装载阶段给出文件不存在的明确错误
            return combined;
        }

        /// <summary>打开 zip、读出 manifest 与 tree、立刻关闭（不保留任何句柄）。</summary>
        private static bool ReadPackageEntries(LoadedPackage package, byte[] bytes,
            out ScbtManifest manifest, out ScbtTree tree)
        {
            manifest = null;
            tree = null;

            byte[] manifestBytes = null;
            byte[] treeBytes = null;

            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var archive = ZipArchive.Open(stream, keepStreamOpen: true);
                List<ZipArchiveEntry> entries = archive.ReadCentralDir();

                for (int i = 0; i < entries.Count; i++)
                {
                    string name = (entries[i].FilenameInZip ?? string.Empty).Replace('\\', '/').Trim('/');
                    if (string.Equals(name, ScbtManifest.FileName, StringComparison.OrdinalIgnoreCase))
                        manifestBytes = Extract(archive, entries[i], package);
                    else if (string.Equals(name, ScbtTree.FileName, StringComparison.OrdinalIgnoreCase))
                        treeBytes = Extract(archive, entries[i], package);
                }
            }
            catch (Exception exception)
            {
                package.Report.Error(PackageCodes.ZipInvalid, package.FileName,
                    "cannot read package as zip: " + exception.GetType().Name + ": " + exception.Message);
                return false;
            }

            if (manifestBytes == null)
            {
                package.Report.Error(PackageCodes.ZipEntryMissing, package.FileName,
                    "package has no " + ScbtManifest.FileName + " at its root");
                return false;
            }
            if (treeBytes == null)
            {
                package.Report.Error(PackageCodes.ZipEntryMissing, package.FileName,
                    "package has no " + ScbtTree.FileName + " at its root");
                return false;
            }

            PackageValue manifestValue;
            if (!PackageJson.TryParseUtf8(manifestBytes, package.FileName + ":" + ScbtManifest.FileName,
                package.Report, out manifestValue))
            {
                return false;
            }
            manifest = ScbtManifest.Parse(manifestValue,
                package.FileName + ":" + ScbtManifest.FileName, package.Report);
            if (manifest == null)
                return false;

            PackageValue treeValue;
            if (!PackageJson.TryParseUtf8(treeBytes, package.FileName + ":" + ScbtTree.FileName,
                package.Report, out treeValue))
            {
                return false;
            }
            tree = ScbtTree.Parse(treeValue, package.FileName + ":" + ScbtTree.FileName, package.Report);
            return tree != null;
        }

        private static byte[] Extract(ZipArchive archive, ZipArchiveEntry entry, LoadedPackage package)
        {
            if (entry.FileSize > PackageJson.MaxBytes)
            {
                package.Report.Error(PackageCodes.FileTooLarge, package.FileName + ":" + entry.FilenameInZip,
                    "entry is " + entry.FileSize + " bytes (limit " + PackageJson.MaxBytes + ")");
                return null;
            }

            using var memory = new MemoryStream();
            archive.ExtractFile(entry, memory);
            return memory.ToArray();
        }

        /// <summary>包哈希（SHA-256，小写十六进制）：推送重载用它判断"内容变了没有"。</summary>
        public static string ComputeHash(byte[] bytes)
        {
            if (bytes == null)
                return null;
            byte[] hash = SHA256.HashData(bytes);
            var builder = new System.Text.StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
                builder.Append(hash[i].ToString("x2"));
            return builder.ToString();
        }

        /// <summary>
        /// 便捷入口：装载 + 编译一次拿到可执行树（控制面 `ai.tree.switch` 用）。
        /// 结果里 <see cref="CompiledTree.Report"/> 同时含装载与编译的问题。
        /// </summary>
        public static CompiledTree LoadAndCompile(string nameOrPath, out ScbtPackageSet set,
            PackageLoadOptions options = null)
        {
            set = Load(nameOrPath, options);

            var report = new PackageReport();
            report.AddRange(set.Report);
            for (int i = 0; i < set.Packages.Count; i++)
                report.AddRange(set.Packages[i].Report);

            if (set.Root == null || report.HasErrors)
            {
                // 有错误就不装：与其跑出不可预期的行为，不如当场说清楚（计划 §5.2）
                var failed = new CompiledTree { SourcePath = set.RootPath };
                failed.Report.AddRange(report);
                if (set.Root == null && !report.HasErrors)
                    failed.Report.Error(PackageCodes.CompileFailed, nameOrPath ?? "<none>", "package was not loaded");
                return failed;
            }

            return TreeCompiler.Compile(set, set.Root, null, report);
        }
    }
}
