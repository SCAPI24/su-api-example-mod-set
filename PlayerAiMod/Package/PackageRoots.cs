using SuAPI;
using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>一个允许的包来源目录（计划 §4.4）。</summary>
    public sealed class PackageRoot
    {
        public PackageRoot(string path, bool writable, string kind)
        {
            Path = path;
            Writable = writable;
            Kind = kind;
        }

        /// <summary>绝对路径（无尾分隔符）。</summary>
        public string Path { get; }

        /// <summary>
        /// 是否可写。包目录对**编辑器**是可写的（人要能改游戏正在用的那份包）；
        /// 游戏侧的写盘有另外的约束：只允许新建，覆盖必须显式给 `overwrite=true`
        /// （见 `ai.tree.export` / `ai.record.save`）。
        /// </summary>
        public bool Writable { get; }

        /// <summary>来源标记。只有一个包目录了，所以恒为 "instance"。</summary>
        public string Kind { get; }

        public bool Exists
        {
            get
            {
                try
                {
                    return Directory.Exists(Path);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public override string ToString()
        {
            return Kind + (Writable ? "(rw)" : "(ro)") + " " + Path;
        }
    }

    /// <summary>
    /// 包目录约定（计划 §4.4，2026-09-12 简化为**单一目录**）：
    /// <c>&lt;实例根&gt;/PlayerAi/BehaviorTrees/</c> —— 树包 `.scbtpak` 与动作包 `.scatpak` 同路径。
    ///
    /// 以前还有一个"随 Mod 分发"的第二目录 <c>Mods/PlayerAiMod/PlayerAi/BehaviorTrees/</c>
    /// （出厂示例装在那里、实例目录优先）。用户明确不要了：**只认这一个目录**，理由很实际 ——
    /// 两个目录带来的"同名谁赢/改哪一份/Mod 更新会不会覆盖"全是不必要的复杂度。
    /// 现在：导出、新建、另存为、修改**默认都落在这个目录**；游戏侧只允许新建
    /// （覆盖要显式 `overwrite=true`）；编辑器怎么改都行。
    ///
    /// 这个白名单同时是**路径穿越防线**：任何解析结果落在目录之外一律拒绝。
    /// </summary>
    public sealed class PackageRoots
    {
        public const string BehaviorTreesFolder = "BehaviorTrees";
        public const string PlayerAiFolder = "PlayerAi";
        public const string Extension = ".scbtpak";
        public const string ActionExtension = ".scatpak";

        /// <summary>
        /// **手动存档**目录（`&lt;实例根&gt;/PlayerAi/Saves/`）—— 计划 §4.11 的第 3 层。
        /// 与包目录的区别只有一个：包目录是"正在跑的"，这里是"人存档的"（可对账、可分发）。
        /// **它不是包来源**（不参与发现与自动装载），只有显式 load 才进内存库。
        /// </summary>
        public const string SavesFolder = "Saves";

        private readonly List<PackageRoot> m_roots = new List<PackageRoot>();

        public PackageRoots(string packageDirectory)
        {
            if (!string.IsNullOrEmpty(packageDirectory))
                m_roots.Add(new PackageRoot(Normalize(packageDirectory), true, "instance"));
        }

        public IReadOnlyList<PackageRoot> Roots
        {
            get { return m_roots; }
        }

        /// <summary>唯一的包目录（没配就是 null）。</summary>
        public PackageRoot InstanceRoot
        {
            get { return m_roots.Count > 0 ? m_roots[0] : null; }
        }

        /// <summary>由实例根拼出包目录：<c>&lt;实例根&gt;/PlayerAi/BehaviorTrees</c>。</summary>
        public static string DirectoryFor(string instanceRoot)
        {
            if (string.IsNullOrEmpty(instanceRoot))
                return null;
            return System.IO.Path.Combine(instanceRoot, PlayerAiFolder, BehaviorTreesFolder);
        }

        /// <summary>
        /// 由实例根拼出**手动存档**目录：<c>&lt;实例根&gt;/PlayerAi/Saves</c>（P6，§4.11 第 3 层）。
        /// 这是 `ai.asset.save` 的默认落点；它**不在**包白名单里，所以不会被当成"正在跑的包"扫出来。
        /// </summary>
        public static string SavesDirectoryFor(string instanceRoot)
        {
            if (string.IsNullOrEmpty(instanceRoot))
                return null;
            return System.IO.Path.Combine(instanceRoot, PlayerAiFolder, SavesFolder);
        }

        /// <summary>
        /// 按约定发现包目录。可以在测试里显式传目录（不依赖游戏运行时）。
        /// 游戏内默认：<c>&lt;data:&gt;</c> 即 exe 所在目录。
        /// </summary>
        public static PackageRoots Discover(string packageDirectory = null)
        {
            if (string.IsNullOrEmpty(packageDirectory))
                packageDirectory = DirectoryFor(GetGameRootDirectory());
            return new PackageRoots(packageDirectory);
        }

        private static string GetGameRootDirectory()
        {
            try
            {
                string root = ModLoader.GetPlatformRootDirectory();
                if (!string.IsNullOrEmpty(root))
                    return root;
            }
            catch (Exception)
            {
                // 脱离游戏运行时（测试）走这里，退回进程目录。
            }
            return AppContext.BaseDirectory;
        }

        /// <summary>路径比较：Windows 不区分大小写，其它平台区分。</summary>
        public static StringComparison PathComparison
        {
            get
            {
                return Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
            }
        }

        private static string Normalize(string path)
        {
            string full = System.IO.Path.GetFullPath(path);
            return System.IO.Path.TrimEndingDirectorySeparator(full);
        }

        /// <summary>把绝对路径归一化为标准全路径（失败返回 null）。</summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>某个绝对路径是否落在白名单目录内（含目录自身）。</summary>
        public bool IsAllowed(string fullPath)
        {
            return OwnerOf(fullPath) != null;
        }

        /// <summary>某个绝对路径属于哪个白名单目录（不属于返回 null）。</summary>
        public PackageRoot OwnerOf(string fullPath)
        {
            string normalized = NormalizePath(fullPath);
            if (normalized == null)
                return null;

            for (int i = 0; i < m_roots.Count; i++)
            {
                string root = m_roots[i].Path;
                if (string.Equals(normalized, root, PathComparison))
                    return m_roots[i];
                if (normalized.Length > root.Length
                    && normalized.StartsWith(root, PathComparison)
                    && (normalized[root.Length] == System.IO.Path.DirectorySeparatorChar
                        || normalized[root.Length] == System.IO.Path.AltDirectorySeparatorChar))
                {
                    return m_roots[i];
                }
            }
            return null;
        }

        /// <summary>
        /// 解析用户给的"包名或相对路径"：支持
        ///   · 文件名（`demo.greet.scbtpak`）
        ///   · 不带扩展名的包名（`demo.greet`）
        ///   · 相对路径（`sub/x.scbtpak`）
        ///   · 绝对路径，但必须落在包目录里
        /// 找不到或越界返回 null。
        ///
        /// <paramref name="exists"/> 允许调用方换一套"存在"的判断（内存数据源的自检就靠它）。
        /// </summary>
        public string Resolve(string nameOrRelativePath, string extension = Extension)
        {
            return Resolve(nameOrRelativePath, null, extension);
        }

        public string Resolve(string nameOrRelativePath, Func<string, bool> exists,
            string extension = Extension)
        {
            if (string.IsNullOrWhiteSpace(nameOrRelativePath))
                return null;

            Func<string, bool> fileExists = exists ?? File.Exists;
            string value = nameOrRelativePath.Trim().Replace('\\', '/');

            // 绝对路径：只接受白名单内的（控制面/编辑器会传绝对路径来通知重载）。
            //
            // ⚠️ **两种根都要认**：Windows 是 `C:/…`，Unix/Android 是 `/…`。
            // 第一版只认 `C:/` 并把开头的 `/` 一律当**路径穿越**拒掉 —— 于是 Android 上
            // "把已经解析好的绝对路径再传回来"（`PackageLoader.Load` / `Validate` / 树库切换
            // 都会这么做）全部报 `file.missing`，等于**整台平板的包装载功能全废**（实机实测）。
            // 安全边界没有放松：真正的判据是 `IsAllowed`（必须落在白名单目录里），
            // 所以 `/etc/passwd` 照样被拒。
            bool windowsRooted = value.Length > 1 && value[1] == ':';
            bool unixRooted = value.StartsWith("/", StringComparison.Ordinal);
            if (windowsRooted || unixRooted)
            {
                string absolute = NormalizePath(value);
                return absolute != null && IsAllowed(absolute) ? absolute : null;
            }

            var candidates = new List<string> { value };
            if (!value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                candidates.Add(value + extension);

            for (int c = 0; c < candidates.Count; c++)
            {
                for (int i = 0; i < m_roots.Count; i++)
                {
                    string combined = NormalizePath(System.IO.Path.Combine(m_roots[i].Path, candidates[c]));
                    if (combined == null || !IsAllowed(combined))
                        continue;
                    if (fileExists(combined))
                        return combined;
                }
            }
            return null;
        }

        /// <summary>列出包目录里的包（按文件名排序；只扫顶层，不递归）。</summary>
        public List<string> ListFiles(string extension = Extension)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < m_roots.Count; i++)
            {
                string[] files;
                try
                {
                    if (!Directory.Exists(m_roots[i].Path))
                        continue;
                    files = Directory.GetFiles(m_roots[i].Path, "*" + extension,
                        SearchOption.TopDirectoryOnly);
                }
                catch (Exception)
                {
                    continue;
                }

                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int f = 0; f < files.Length; f++)
                {
                    string name = System.IO.Path.GetFileName(files[f]);
                    if (seen.Add(name))
                        result.Add(files[f]);
                }
            }
            return result;
        }

        /// <summary>确保包目录存在（录制、导出、编辑器保存前调用）。</summary>
        public bool EnsureInstanceDirectory(out string error)
        {
            error = null;
            PackageRoot instance = InstanceRoot;
            if (instance == null)
            {
                error = "没有包目录（PackageRoots 未配置）";
                return false;
            }
            try
            {
                Directory.CreateDirectory(instance.Path);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        public string Describe()
        {
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < m_roots.Count; i++)
            {
                if (i > 0)
                    builder.Append("; ");
                builder.Append(m_roots[i].Kind).Append('=')
                    .Append(m_roots[i].Path)
                    .Append(m_roots[i].Exists ? string.Empty : " (missing)");
            }
            return builder.Length > 0 ? builder.ToString() : "<none>";
        }

        public override string ToString()
        {
            return "PackageRoots(" + Describe() + ")";
        }
    }
}
