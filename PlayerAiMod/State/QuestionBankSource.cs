using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 问题库的**查找来源** —— 构建期引用校验（plan G4）的输入。
    ///
    /// 为什么把它抽成一个接口：校验器（`PackageValidator`）必须保持**纯逻辑**
    /// （编辑器也编它，编辑器里不能有游戏胶水），而"库在哪"这件事只有调用方知道 ——
    /// 游戏侧与编辑器都按实例根找（编辑器由自己所在位置判定实例根）、自检按内存里造好的库找。
    /// 三者的**判定规则完全共用**，只有"从哪拿库"不同。
    /// </summary>
    public interface IQuestionBankSource
    {
        /// <summary>
        /// 按引用取库。`reference` 可以是 `world_goal`、`world_goal.qbank` 或带目录的路径
        /// （B 树里写的是**文件名**，但手写与旧包可能带扩展名）。
        /// 返回 false 时 `error` 必须说清**为什么**（找不到 / 读不了 / 解析失败）。
        /// </summary>
        bool TryGetBank(string reference, out QuestionBank bank, out string error);

        /// <summary>能给出来的库 id（用于"找不到时提示你有哪些"）。可以为空列表。</summary>
        List<string> AvailableIds();
    }

    /// <summary>
    /// 内存版来源：调用方已经把库读出来了（自检、以及"加载器顺手把库带上"的场合）。
    /// </summary>
    public sealed class QuestionBankMap : IQuestionBankSource
    {
        private readonly Dictionary<string, QuestionBank> m_byId =
            new Dictionary<string, QuestionBank>(StringComparer.OrdinalIgnoreCase);

        public void Add(QuestionBank bank)
        {
            if (bank == null || string.IsNullOrEmpty(bank.Id))
                return;
            m_byId[bank.Id] = bank;
        }

        public int Count
        {
            get { return m_byId.Count; }
        }

        public bool TryGetBank(string reference, out QuestionBank bank, out string error)
        {
            bank = null;
            error = null;
            string id = QuestionBankDirectorySource.NormalizeReference(reference);
            if (string.IsNullOrEmpty(id))
            {
                error = "empty question bank reference";
                return false;
            }
            if (m_byId.TryGetValue(id, out bank) && bank != null)
                return true;
            bank = null;
            error = "no question bank named '" + id + "' (available: " + Describe(AvailableIds()) + ")";
            return false;
        }

        public List<string> AvailableIds()
        {
            var ids = new List<string>(m_byId.Keys);
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return ids;
        }

        internal static string Describe(List<string> ids)
        {
            if (ids == null || ids.Count == 0)
                return "none";
            return string.Join(", ", ids.ToArray());
        }
    }

    /// <summary>
    /// 目录版来源：按 `&lt;dir&gt;/&lt;引用&gt;.qbank` 找。
    ///
    /// **为什么这里自己带一层缓存**（而不是复用 `LayaRuntimeService` 的库缓存）：
    /// 两者要回答的问题不同 —— 运行时那份缓存是"**我发出去的是哪一版**"（进请求指纹，
    /// 绝不能中途换掉），而校验要的是"**磁盘上现在是什么**"（编辑器改完库立刻校验）。
    /// 共用的部分（按 id 找文件、按 `QuestionBankParser` 解析）都在下面这一份实现里。
    /// 缓存按**文件戳**失效，所以"改了库再校验"一定看到新内容。
    /// </summary>
    public sealed class QuestionBankDirectorySource : IQuestionBankSource
    {
        private sealed class Entry
        {
            public long Stamp;
            public QuestionBank Bank;
            public string Error;
        }

        private readonly List<string> m_directories = new List<string>();
        private readonly Dictionary<string, Entry> m_cache =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public QuestionBankDirectorySource(IEnumerable<string> directories)
        {
            if (directories == null)
                return;
            foreach (string directory in directories)
            {
                if (!string.IsNullOrEmpty(directory) && !m_directories.Contains(directory))
                    m_directories.Add(directory);
            }
        }

        public IReadOnlyList<string> Directories
        {
            get { return m_directories; }
        }

        /// <summary>丢掉缓存（编辑器收到"库变了吗"的通知时可以主动调）。</summary>
        public void Invalidate()
        {
            m_cache.Clear();
        }

        public bool TryGetBank(string reference, out QuestionBank bank, out string error)
        {
            bank = null;
            error = null;
            string id = NormalizeReference(reference);
            if (string.IsNullOrEmpty(id))
            {
                error = "empty question bank reference";
                return false;
            }

            string path = Find(id);
            if (path == null)
            {
                error = "no question bank named '" + id + "' in "
                    + (m_directories.Count == 0 ? "(no bank directory configured)" : string.Join(", ", m_directories.ToArray()))
                    + " (available: " + QuestionBankMap.Describe(AvailableIds()) + ")";
                return false;
            }

            Entry entry;
            long stamp = StampOf(path);
            if (m_cache.TryGetValue(path, out entry) && entry.Stamp == stamp)
            {
                bank = entry.Bank;
                error = entry.Error;
                return bank != null;
            }

            var fresh = new Entry { Stamp = stamp };
            try
            {
                string text = File.ReadAllText(path);
                string parseError;
                QuestionBank parsed = QuestionBankParser.Parse(text, path, out parseError);
                if (parsed == null)
                {
                    fresh.Error = "question bank '" + id + "' does not parse: " + parseError;
                }
                else
                {
                    if (string.IsNullOrEmpty(parsed.Id))
                        parsed.Id = id;
                    parsed.SourcePath = path;
                    fresh.Bank = parsed;
                }
            }
            catch (Exception exception)
            {
                fresh.Error = "question bank '" + id + "' is unreadable: "
                    + exception.GetType().Name + ": " + exception.Message;
            }

            m_cache[path] = fresh;
            bank = fresh.Bank;
            error = fresh.Error;
            return bank != null;
        }

        public List<string> AvailableIds()
        {
            var ids = new List<string>();
            for (int d = 0; d < m_directories.Count; d++)
            {
                try
                {
                    if (!Directory.Exists(m_directories[d]))
                        continue;
                    string[] files = Directory.GetFiles(m_directories[d], "*.qbank");
                    for (int f = 0; f < files.Length; f++)
                    {
                        string id = NormalizeReference(files[f]);
                        // 已经解析过的库用**文件里写的 id**（那才是权威），否则退回文件名。
                        Entry entry;
                        if (m_cache.TryGetValue(files[f], out entry) && entry.Bank != null
                            && !string.IsNullOrEmpty(entry.Bank.Id))
                        {
                            id = entry.Bank.Id;
                        }
                        if (!string.IsNullOrEmpty(id) && !ids.Contains(id))
                            ids.Add(id);
                    }
                }
                catch (Exception)
                {
                    // 目录读不了就当它没有库；`TryGetBank` 那边的错误信息已经带了目录名。
                }
            }
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            return ids;
        }

        private string Find(string id)
        {
            for (int d = 0; d < m_directories.Count; d++)
            {
                string directory = m_directories[d];
                if (string.IsNullOrEmpty(directory))
                    continue;
                string candidate = Path.Combine(directory, id + ".qbank");
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
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

        /// <summary>
        /// 由实例根算出问题库目录。**这是全项目唯一一处**定义"库在哪"的地方
        /// （`LayaRuntimeService.BankDirectoriesFor` 直接转调它）——
        /// 两处各写一份的话，校验器说"库不在"而运行时明明读得到，就成了最费时间的假警报。
        /// </summary>
        public static List<string> DirectoriesFor(string instanceRoot)
        {
            var directories = new List<string>();
            if (!string.IsNullOrEmpty(instanceRoot))
                directories.Add(Path.Combine(instanceRoot, "PlayerAi", QuestionBank.FolderName));
            return directories;
        }

        /// <summary>
        /// `PlayerAi/Questions/world_goal.qbank` → `world_goal`。
        /// 目录与扩展名都剥掉：B 树里写的是**文件名**，但手写包可能带路径/扩展名，
        /// 两边都要认（认不出来就会把"其实存在"报成"找不到"，那是最烦人的假警报）。
        /// </summary>
        public static string NormalizeReference(string reference)
        {
            if (string.IsNullOrEmpty(reference))
                return null;
            string value = reference.Trim().Replace('\\', '/');
            int slash = value.LastIndexOf('/');
            if (slash >= 0)
                value = value.Substring(slash + 1);
            if (value.EndsWith(".qbank", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - ".qbank".Length);
            return value.Trim();
        }
    }
}
