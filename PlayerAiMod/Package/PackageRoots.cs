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

        /// <summary>是否可写：只有实例目录可写（录制、导出、编辑器保存都写这里）。</summary>
        public bool Writable { get; }

        /// <summary>"instance" | "mod"。</summary>
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
    /// 包目录约定（计划 §4.4）：
    ///   1（高）<c>&lt;实例根&gt;/PlayerAi/BehaviorTrees/</c> —— 跟随游戏实例走，**可写**
    ///   2（低）<c>&lt;实例根&gt;/Mods/PlayerAiMod/BehaviorTrees/</c> —— 随 Mod 分发，**只读**
    ///
    /// 同名优先：实例目录覆盖 Mod 目录。两个目录并存是为了"既能改、又能分发"。
    /// 白名单同时是**路径穿越防线**：任何解析结果落在白名单之外一律拒绝。
    /// </summary>
    public sealed class PackageRoots
    {
        public const string BehaviorTreesFolder = "BehaviorTrees";
        public const string PlayerAiFolder = "PlayerAi";
        public const string ModFolder = "PlayerAiMod";
        public const string Extension = ".scbtpak";
        public const string ActionExtension = ".scatpak";

        private readonly List<PackageRoot> m_roots = new List<PackageRoot>();

        public PackageRoots(string instanceDirectory, string modDirectory)
        {
            if (!string.IsNullOrEmpty(instanceDirectory))
                m_roots.Add(new PackageRoot(Normalize(instanceDirectory), true, "instance"));
            if (!string.IsNullOrEmpty(modDirectory))
                m_roots.Add(new PackageRoot(Normalize(modDirectory), false, "mod"));
        }

        public IReadOnlyList<PackageRoot> Roots
        {
            get { return m_roots; }
        }

        public PackageRoot InstanceRoot
        {
            get { return Find("instance"); }
        }

        public PackageRoot ModRoot
        {
            get { return Find("mod"); }
        }

        private PackageRoot Find(string kind)
        {
            for (int i = 0; i < m_roots.Count; i++)
            {
                if (string.Equals(m_roots[i].Kind, kind, StringComparison.Ordinal))
                    return m_roots[i];
            }
            return null;
        }

        /// <summary>
        /// 按约定发现两个目录。可以在测试里显式传目录（不依赖游戏运行时）。
        /// 游戏内默认：<c>&lt;data:&gt;</c> 即 exe 所在目录。
        /// </summary>
        public static PackageRoots Discover(string instanceDirectory = null, string modDirectory = null)
        {
            if (string.IsNullOrEmpty(instanceDirectory))
            {
                string root = GetGameRootDirectory();
                instanceDirectory = root != null
                    ? System.IO.Path.Combine(root, PlayerAiFolder, BehaviorTreesFolder)
                    : null;
            }
            if (string.IsNullOrEmpty(modDirectory))
            {
                string root = GetGameRootDirectory();
                modDirectory = root != null
                    ? System.IO.Path.Combine(root, "Mods", ModFolder, PlayerAiFolder, BehaviorTreesFolder)
                    : null;
            }
            return new PackageRoots(instanceDirectory, modDirectory);
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
        /// 实例目录优先于 Mod 目录（同名优先）。找不到或越界返回 null。
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

            // 绝对路径：只接受白名单内的（控制面/编辑器会传绝对路径来通知重载）
            if (value.Length > 1 && value[1] == ':')
            {
                string absolute = NormalizePath(value);
                return absolute != null && IsAllowed(absolute) ? absolute : null;
            }
            if (value.StartsWith("/", StringComparison.Ordinal))
                return null;

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

        /// <summary>列出两个目录里的包（实例优先；同名只出现一次）。</summary>
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

        /// <summary>确保实例目录存在（录制、导出、编辑器保存前调用）。</summary>
        public bool EnsureInstanceDirectory(out string error)
        {
            error = null;
            PackageRoot instance = InstanceRoot;
            if (instance == null)
            {
                error = "没有可写的实例包目录（PackageRoots 未配置 instance 目录）";
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
