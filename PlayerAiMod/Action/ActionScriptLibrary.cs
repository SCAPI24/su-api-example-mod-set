using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>脚本库里的一条（带来源与解析状态，供列表/校验/热重载判断）。</summary>
    public sealed class ActionScriptEntry
    {
        public string Name;
        public string Path;
        public string Directory;
        public string Hash;
        public long SizeBytes;
        public DateTime ModifiedUtc;

        /// <summary>解析出来的脚本（解析失败为 null）。</summary>
        public ActionScript Script;

        /// <summary>解析失败原因（成功为 null）。</summary>
        public string Error;

        public bool Ok
        {
            get { return Script != null && string.IsNullOrEmpty(Error); }
        }

        public int StepCount
        {
            get { return Script != null ? Script.Steps.Count : 0; }
        }

        public string Describe()
        {
            return Name + (Ok ? " steps=" + StepCount : " INVALID: " + Error);
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// 动作脚本库（P1）：从一批目录里**按名字**解析 `.aeact` 脚本，带内存缓存 + 哈希失效。
    ///
    /// 三条纪律：
    ///   1. **不锁文件**：读一次就关（沿用包加载器的铁律），编辑器保存中的半截文件不会被长期占用；
    ///   2. **哈希失效**：缓存按 mtime + size + hash 判断，文件被改过就重新解析
    ///      （于是 AI 改了脚本、人改了脚本都能立刻生效，不需要重启游戏）；
    ///   3. **搜索顺序 = 目录顺序**：前面的优先（实例目录优先于发行目录）。
    ///
    /// 目录编排（与 `PackageRoots` 的实例/发行两级一致）：
    ///   · 1 级（可写）：`<实例根>/PlayerAi/Scripts/`  —— 人/AI/编辑器日常改的
    ///   · 2 级（只读）：`<实例根>/PlayerAi/BehaviorTrees/`（兼容：脚本与树包放一起也行）
    /// </summary>
    public sealed class ActionScriptLibrary
    {
        private readonly List<string> m_directories = new List<string>();
        private readonly Dictionary<string, ActionScriptEntry> m_cache =
            new Dictionary<string, ActionScriptEntry>(StringComparer.OrdinalIgnoreCase);

        private int m_hits;
        private int m_misses;
        private int m_parseFailures;

        public ActionScriptLibrary(IEnumerable<string> directories)
        {
            if (directories == null)
                return;
            foreach (string directory in directories)
            {
                if (!string.IsNullOrEmpty(directory))
                    m_directories.Add(directory);
            }
        }

        public IReadOnlyList<string> Directories
        {
            get { return m_directories; }
        }

        public int CacheCount
        {
            get { return m_cache.Count; }
        }

        public int CacheHits
        {
            get { return m_hits; }
        }

        public int CacheMisses
        {
            get { return m_misses; }
        }

        public int ParseFailures
        {
            get { return m_parseFailures; }
        }

        /// <summary>清掉缓存（目录变化、卸载、或人要求强制重读时用）。</summary>
        public void Clear()
        {
            m_cache.Clear();
            m_hits = 0;
            m_misses = 0;
            m_parseFailures = 0;
        }

        /// <summary>
        /// 按名字解析脚本。`nameOrPath` 可以是 `mine_stone_once`、`sub/mine.aeact` 或绝对/相对路径。
        /// 返回 null 时 <paramref name="error"/> 给原因（调用方据此判 Failed 并写失败标记）。
        /// </summary>
        public ActionScript Resolve(string nameOrPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(nameOrPath))
            {
                error = "script name is empty";
                return null;
            }

            ActionScriptEntry entry = ResolveEntry(nameOrPath, out error);
            if (entry == null)
                return null;
            if (!entry.Ok)
            {
                error = entry.Error;
                return null;
            }
            return entry.Script;
        }

        /// <summary>同 <see cref="Resolve"/>，但把整条（含路径/哈希/错误）给出来。</summary>
        public ActionScriptEntry ResolveEntry(string nameOrPath, out string error)
        {
            error = null;

            string path = FindFile(nameOrPath);
            if (path == null)
            {
                error = "script file not found: '" + nameOrPath + "' (searched: "
                    + string.Join(", ", m_directories.ToArray()) + ")";
                return null;
            }

            string key = PackageRoots.NormalizePath(path) ?? path;
            ActionScriptEntry cached;
            if (m_cache.TryGetValue(key, out cached) && IsFresh(cached))
            {
                m_hits++;
                return cached;
            }

            m_misses++;
            var entry = new ActionScriptEntry { Name = StripExtension(Path.GetFileName(path)), Path = path };
            try
            {
                var info = new FileInfo(path);
                entry.SizeBytes = info.Length;
                entry.ModifiedUtc = info.LastWriteTimeUtc;
                entry.Directory = info.DirectoryName;

                byte[] bytes = File.ReadAllBytes(path); // 读一次就关，不保留句柄
                entry.Hash = PackageLoader.ComputeHash(bytes);
                ActionScriptParseResult parsed = ActionScriptParser.ParseJson(
                    new UTF8Encoding(false).GetString(bytes), path);
                if (parsed.Ok)
                {
                    entry.Script = parsed.Script;
                }
                else
                {
                    entry.Error = parsed.Error;
                    m_parseFailures++;
                }
            }
            catch (Exception exception)
            {
                entry.Error = exception.GetType().Name + ": " + exception.Message;
                m_parseFailures++;
            }

            m_cache[key] = entry;
            return entry;
        }

        /// <summary>列出所有目录里的脚本（前面的目录里同名者遮蔽后面的，标记 shadowed）。</summary>
        public List<ActionScriptEntry> List()
        {
            var results = new List<ActionScriptEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int d = 0; d < m_directories.Count; d++)
            {
                string directory = m_directories[d];
                string[] files;
                try
                {
                    if (!Directory.Exists(directory))
                        continue;
                    files = Directory.GetFiles(directory, "*" + ActionScript.Extension, SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    continue;
                }

                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length; i++)
                {
                    string name = StripExtension(Path.GetFileName(files[i]));
                    if (!seen.Add(name))
                        continue; // 前面的目录已经提供了同名脚本

                    string error;
                    ActionScriptEntry entry = ResolveEntry(files[i], out error);
                    if (entry != null)
                        results.Add(entry);
                }
            }
            return results;
        }

        /// <summary>只校验不执行（编辑器保存前预检 / `ai.action.script.validate`）。</summary>
        public Dictionary<string, object> Validate(string nameOrPath)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            string error;
            ActionScriptEntry entry = ResolveEntry(nameOrPath, out error);
            result["name"] = nameOrPath;
            if (entry == null)
            {
                result["ok"] = false;
                result["error"] = error;
                return result;
            }

            result["ok"] = entry.Ok;
            result["path"] = entry.Path;
            result["hash"] = entry.Hash;
            result["steps"] = entry.StepCount;
            result["error"] = entry.Error;
            if (entry.Script != null)
            {
                result["id"] = entry.Script.Id;
                result["guards"] = entry.Script.Guards.Count;
                result["onFail"] = entry.Script.OnFail;
                var verbs = new List<string>();
                for (int i = 0; i < entry.Script.Steps.Count; i++)
                    verbs.Add(entry.Script.Steps[i].Verb);
                result["verbs"] = verbs;
            }
            return result;
        }

        // ---------------------------------------------------------------- 内部

        private string FindFile(string nameOrPath)
        {
            string trimmed = nameOrPath.Trim();

            // 1) 明确给了路径（绝对/带目录/带后缀）
            if (trimmed.IndexOf('/') >= 0 || trimmed.IndexOf('\\') >= 0
                || trimmed.EndsWith(ActionScript.Extension, StringComparison.OrdinalIgnoreCase)
                || Path.IsPathRooted(trimmed))
            {
                var candidates = new List<string> { trimmed };
                if (!trimmed.EndsWith(ActionScript.Extension, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(trimmed + ActionScript.Extension);

                for (int i = 0; i < candidates.Count; i++)
                {
                    string candidate = candidates[i];
                    if (File.Exists(candidate))
                        return candidate;

                    // 相对路径：依次在每个搜索目录下找
                    for (int d = 0; d < m_directories.Count; d++)
                    {
                        string combined = Path.Combine(m_directories[d], candidate);
                        if (File.Exists(combined))
                            return combined;
                    }
                }
                return null;
            }

            // 2) 只有名字：依次在每个目录里找 `<name>.aeact`
            for (int d = 0; d < m_directories.Count; d++)
            {
                string combined = Path.Combine(m_directories[d], trimmed + ActionScript.Extension);
                if (File.Exists(combined))
                    return combined;
            }
            return null;
        }

        private static bool IsFresh(ActionScriptEntry entry)
        {
            try
            {
                var info = new FileInfo(entry.Path);
                if (!info.Exists)
                    return false;
                return info.Length == entry.SizeBytes
                    && info.LastWriteTimeUtc == entry.ModifiedUtc;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string StripExtension(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            return name.EndsWith(ActionScript.Extension, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - ActionScript.Extension.Length)
                : name;
        }

        public Dictionary<string, object> Describe()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["directories"] = new List<string>(m_directories);
            result["cached"] = m_cache.Count;
            result["hits"] = m_hits;
            result["misses"] = m_misses;
            result["parseFailures"] = m_parseFailures;
            return result;
        }
    }
}
