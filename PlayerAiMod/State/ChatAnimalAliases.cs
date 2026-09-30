using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 一个物种：**一组能对上的名字**（口语词 + 游戏里的显示名 + 英文名）。
    ///
    /// 为什么不能只靠引擎的 `CreatureCategory`：那个枚举只有 5 个值
    /// （`LandPredator=1 / LandOther=2 / WaterPredator=4 / WaterOther=8 / Bird=0x10`），
    /// 牛和鹿、狼和熊虎全挤在同一类里 —— "打牛" 靠类别根本分不出来。
    /// 唯一能区分物种的是生物名（`ComponentName.Name` → 退回 `ComponentCreature.DisplayName`），
    /// 所以这里维护的是"口语词 ↔ 生物名"的对应表。
    /// </summary>
    public sealed class ChatAnimalSpec
    {
        /// <summary>物种短名（`cow`/`wolf`），只用于日志与自检，不参与匹配。</summary>
        public string Key;

        /// <summary>能匹配上这个物种的所有名字（小写）。</summary>
        public List<string> Names = new List<string>();

        /// <summary>
        /// 这个物种属于哪一类（`Game.CreatureCategory` 位掩码；0 = 不限）。
        ///
        /// 为什么名字之外还要类别：名字是**子串匹配**，`牛` 扩展出的 `bull` 会命中
        /// "**Bull** Shark" —— 2026-09-26 实测"打牛"跑去追鲨鱼。类别不给它机会：
        /// 牛是陆地动物（1|2=3），鲨鱼是水生（4|8=12），一筛就分开了。
        /// </summary>
        public int CategoryMask;

        public string Description;

        public ChatAnimalSpec Clone()
        {
            return new ChatAnimalSpec
            {
                Key = Key, Description = Description, CategoryMask = CategoryMask,
                Names = new List<string>(Names)
            };
        }
    }

    /// <summary>
    /// 物种别名表（`PlayerAi/Chat/animals.json`）：**口语词 → 生物名**。
    ///
    /// 与短语表同一套纪律（见 <see cref="ChatPhrases"/>）：格式带版本、内容带哈希、
    /// 缺文件用内置默认、解析失败保留上一份 —— 于是"加一种动物"是改文件，不是重编译 Mod。
    ///
    /// 它只负责**把候选词扩展成一组可比的名字**；谁最近由 `PlayerSensor` 决定。
    /// </summary>
    public sealed class ChatAnimalAliases
    {
        public const string FormatName = "chatanimals";
        public const int FormatVersion = 1;
        public const string DefaultId = "world";

        /// <summary>与 `phrases.json` 同目录；两个格式共用一个 `Chat/` 文件夹。</summary>
        public const string FileName = "animals.json";

        /// <summary>类别掩码（对齐 `Game.CreatureCategory`）：不限 / 陆地 / 水生 / 鸟。</summary>
        public const int MaskAny = 0;
        public const int MaskLand = 3;      // LandPredator(1) | LandOther(2)
        public const int MaskWater = 12;    // WaterPredator(4) | WaterOther(8)
        public const int MaskBird = 16;

        /// <summary>`"land"` / `"water"` / `"bird"` / `"any"` → 掩码（认不出来按 any）。</summary>
        public static int MaskOf(string name)
        {
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "land": return MaskLand;
                case "water": return MaskWater;
                case "bird":
                case "air": return MaskBird;
                default: return MaskAny;
            }
        }

        /// <summary>掩码 → 文件里写的那个词（写回用）。</summary>
        public static string MaskName(int mask)
        {
            switch (mask)
            {
                case MaskLand: return "land";
                case MaskWater: return "water";
                case MaskBird: return "bird";
                default: return "any";
            }
        }

        public string Format = FormatName;
        public int Version = FormatVersion;
        public string Id = DefaultId;
        public string Name;
        public string Description;

        public List<ChatAnimalSpec> Species = new List<ChatAnimalSpec>();

        public string SourcePath;
        public string SourceHash;

        public bool IsBuiltin
        {
            get { return string.IsNullOrEmpty(SourcePath); }
        }

        public ChatAnimalSpec Find(string key)
        {
            for (int i = 0; i < Species.Count; i++)
                if (string.Equals(Species[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return Species[i];
            return null;
        }

        public ChatAnimalAliases Clone()
        {
            var copy = new ChatAnimalAliases
            {
                Format = Format, Version = Version, Id = Id, Name = Name,
                Description = Description, SourcePath = SourcePath, SourceHash = SourceHash
            };
            for (int i = 0; i < Species.Count; i++)
                copy.Species.Add(Species[i].Clone());
            return copy;
        }

        // ------------------------------------------------------------------ 匹配

        /// <summary>
        /// 把聊天里抠出来的候选词**扩展**成一组可比的名字。
        ///
        /// 例（内置表）：`牛` → `牛, 公牛, 奶牛, ox, cow, bull`；`牛狼` → 上面那组 + 狼那一组
        /// （"牛狼" 里同时含两种动物的名字，于是"最近的那只"说了算）。
        /// 表里没有的词原样保留 —— 交给"和生物显示名直接比"的兜底路径。
        /// </summary>
        public List<string> Expand(string candidates)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(candidates))
                return result;

            string[] parts = candidates.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int p = 0; p < parts.Length; p++)
            {
                string word = parts[p].Trim().ToLowerInvariant();
                if (word.Length == 0)
                    continue;
                AddUnique(result, word);

                // 命中了某个物种就把它的一整组名字都算上（口语词 → 游戏里的显示名）
                for (int s = 0; s < Species.Count; s++)
                {
                    ChatAnimalSpec spec = Species[s];
                    bool hit = false;
                    for (int n = 0; n < spec.Names.Count && !hit; n++)
                        hit = MatchOne(word, spec.Names[n]);
                    if (!hit)
                        continue;
                    for (int n = 0; n < spec.Names.Count; n++)
                        AddUnique(result, spec.Names[n]);
                }
            }
            return result;
        }

        /// <summary>
        /// 候选词命中的物种**属于哪一类**（多个则按位或；一个都没命中 → 0 = 不限）。
        ///
        /// 用途：名字是子串匹配，`牛` 会命中 "**Bull** Shark"（实测"打牛"跑去追鲨鱼）；
        /// 拿这个掩码再筛一遍类别，牛（陆地 3）就不会去追鲨鱼（水生 12）。
        /// 与 <see cref="Expand"/> 共用"命中"判定，保证"名字能对上"和"类别能筛"始终一致。
        /// </summary>
        public int CategoryMaskOf(string candidates)
        {
            int mask = 0;
            if (string.IsNullOrEmpty(candidates))
                return 0;

            string[] parts = candidates.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int p = 0; p < parts.Length; p++)
            {
                string word = parts[p].Trim().ToLowerInvariant();
                if (word.Length == 0)
                    continue;
                for (int s = 0; s < Species.Count; s++)
                {
                    ChatAnimalSpec spec = Species[s];
                    bool hit = false;
                    for (int n = 0; n < spec.Names.Count && !hit; n++)
                        hit = MatchOne(word, spec.Names[n]);
                    if (hit)
                        mask |= spec.CategoryMask;
                }
            }
            return mask;
        }

        /// <summary>候选词里有没有一个能对这个生物名（任一命中即算）。</summary>
        public static bool MatchWords(string creatureName, IReadOnlyList<string> candidates)
        {
            if (string.IsNullOrEmpty(creatureName) || candidates == null)
                return false;
            string name = creatureName.Trim().ToLowerInvariant();
            if (name.Length == 0)
                return false;
            for (int i = 0; i < candidates.Count; i++)
                if (MatchOne(name, candidates[i]))
                    return true;
            return false;
        }

        /// <summary>
        /// 单词匹配规则：
        ///   · 相等；
        ///   · 一方含另一方（`牛` ↔ `公牛`；`牛` ↔ `牛狼` —— 一句点多种动物时靠这条）；
        ///   · 纯 ASCII 的名字要求 **≥3 个字符**才允许"含"匹配，
        ///     否则 `cow` 里的 `ow`、`ox` 这种碎片会把不相干的动物也拉进来。
        /// </summary>
        internal static bool MatchOne(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                return false;
            string a = left.Trim().ToLowerInvariant();
            string b = right.Trim().ToLowerInvariant();
            if (a.Length == 0 || b.Length == 0)
                return false;
            if (string.Equals(a, b, StringComparison.Ordinal))
                return true;

            bool aAscii = IsAscii(a);
            bool bAscii = IsAscii(b);
            if (aAscii && bAscii)
            {
                if (a.Length < 3 || b.Length < 3)
                    return false;
                return a.Contains(b) || b.Contains(a);
            }

            // 中文（或中英混合）：子串即命中
            return a.Contains(b) || b.Contains(a);
        }

        private static void AddUnique(List<string> list, string word)
        {
            string value = (word ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0)
                return;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], value, StringComparison.Ordinal))
                    return;
            list.Add(value);
        }

        private static bool IsAscii(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] > 127)
                    return false;
            return true;
        }

        // ------------------------------------------------------------------ 内置默认

        private static ChatAnimalAliases s_default;

        /// <summary>
        /// **内置默认别名表**（实例里没有 `animals.json` 时的兜底）。
        ///
        /// 只收常见家畜/猎物；两个名字体系都写（中文显示名 + 英文显示名），
        /// 因为游戏语言库切换后 `DisplayName` 会变，而用户说的还是中文。
        /// </summary>
        public static ChatAnimalAliases Default
        {
            get
            {
                if (s_default == null)
                    s_default = BuildDefault();
                return s_default;
            }
        }

        private static ChatAnimalAliases BuildDefault()
        {
            var table = new ChatAnimalAliases
            {
                Id = DefaultId,
                Name = "world",
                Description = "出厂默认：常见猎物/家畜的口语词 → 生物显示名；加动物改这个文件即可。"
            };

            Add(table, "cow", "牛 / 家牛", MaskLand, "牛", "公牛", "奶牛", "小牛", "cow", "bull", "cattle");
            Add(table, "wolf", "狼", MaskLand, "狼", "灰狼", "白狼", "wolf");
            Add(table, "bear", "熊", MaskLand, "熊", "棕熊", "黑熊", "bear");
            Add(table, "tiger", "虎", MaskLand, "虎", "老虎", "tiger");
            Add(table, "lion", "狮", MaskLand, "狮", "狮子", "lion");
            Add(table, "hyena", "鬣狗", MaskLand, "鬣狗", "hyena");
            Add(table, "boar", "野猪", MaskLand, "野猪", "猪", "boar", "pig");
            Add(table, "deer", "鹿", MaskLand, "鹿", "deer");
            Add(table, "giraffe", "长颈鹿", MaskLand, "长颈鹿", "giraffe");
            Add(table, "rhino", "犀牛", MaskLand, "犀牛", "rhino");
            Add(table, "zebra", "斑马", MaskLand, "斑马", "zebra");
            Add(table, "camel", "骆驼", MaskLand, "骆驼", "camel");
            Add(table, "ostrich", "鸵鸟", MaskLand, "鸵鸟", "ostrich");
            Add(table, "bird", "鸟", MaskBird, "鸟", "小鸟", "bird", "raven", "seagull", "duck");
            Add(table, "bass", "鲈鱼", MaskWater, "鲈鱼", "鱼", "bass", "fish");
            Add(table, "ray", "鳐鱼", MaskWater, "鳐鱼", "ray");
            Add(table, "piranha", "食人鱼", MaskWater, "食人鱼", "piranha");
            Add(table, "barracuda", "梭鱼", MaskWater, "梭鱼", "barracuda");
            Add(table, "shark", "鲨鱼", MaskWater, "鲨鱼", "shark");
            Add(table, "orca", "虎鲸", MaskWater, "虎鲸", "orca", "killer whale");
            Add(table, "beluga", "白鲸", MaskWater, "白鲸", "beluga");
            Add(table, "whale", "鲸", MaskWater, "鲸", "鲸鱼", "whale");

            table.SourceHash = ChatAnimalAliasesJson.ComputeHash(table);
            return table;
        }

        private static void Add(ChatAnimalAliases table, string key, string description, int categoryMask,
            params string[] names)
        {
            var spec = new ChatAnimalSpec { Key = key, Description = description, CategoryMask = categoryMask };
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                if (!string.IsNullOrEmpty(name))
                    spec.Names.Add(name);
            }
            table.Species.Add(spec);
        }
    }

    /// <summary>`animals.json` 的解析/写回（与 <see cref="ChatPhrasesJson"/> 同一套路）。</summary>
    public static class ChatAnimalAliasesJson
    {
        public static ChatAnimalAliases Parse(string json, string sourcePath, out string error)
        {
            error = null;
            var report = new PackageReport();
            PackageValue root;
            if (!PackageJson.TryParse(json ?? string.Empty, sourcePath ?? "animals", report, out root))
            {
                PackageIssue first = report.FirstError;
                error = "invalid JSON: " + (first != null ? first.Describe() : "parse failed");
                return null;
            }

            if (root == null || !root.IsObject)
            {
                error = "chat animals must be a JSON object";
                return null;
            }

            string format = root.Get("format").AsString(null);
            if (!string.IsNullOrEmpty(format)
                && !string.Equals(format, ChatAnimalAliases.FormatName, StringComparison.OrdinalIgnoreCase))
            {
                error = "unsupported format '" + format + "' (expected '" + ChatAnimalAliases.FormatName + "')";
                return null;
            }

            int version = root.Get("version").AsInt(ChatAnimalAliases.FormatVersion);
            if (version != ChatAnimalAliases.FormatVersion)
            {
                error = "unsupported chat animals format version " + version
                    + " (only " + ChatAnimalAliases.FormatVersion + " is accepted)";
                return null;
            }

            var table = new ChatAnimalAliases
            {
                Format = ChatAnimalAliases.FormatName,
                Version = version,
                Id = root.Get("id").AsString(null),
                Name = root.Get("name").AsString(null),
                Description = root.Get("description").AsString(null),
                SourcePath = sourcePath
            };
            if (string.IsNullOrEmpty(table.Id))
            {
                error = "chat animals need an 'id'";
                return null;
            }

            PackageValue list = root.Get("species");
            if (!list.IsArray)
            {
                error = "chat animals need a 'species' array";
                return null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++)
            {
                PackageValue item = list.Item(i);
                string key = item.Get("key").AsString(null);
                if (string.IsNullOrWhiteSpace(key))
                {
                    error = "species[" + i + "] needs a 'key'";
                    return null;
                }
                if (!seen.Add(key.Trim()))
                {
                    error = "duplicate animal key '" + key + "'";
                    return null;
                }

                var spec = new ChatAnimalSpec
                {
                    Key = key.Trim().ToLowerInvariant(),
                    Description = item.Get("description").AsString(null),
                    CategoryMask = ChatAnimalAliases.MaskOf(item.Get("category").AsString(null))
                };
                PackageValue names = item.Get("names");
                if (names.IsArray)
                {
                    for (int n = 0; n < names.Count; n++)
                    {
                        string name = names.Item(n).AsString(null);
                        if (!string.IsNullOrWhiteSpace(name))
                            spec.Names.Add(name.Trim());
                    }
                }
                if (spec.Names.Count == 0)
                {
                    error = "animal '" + key + "' needs a non-empty 'names' array";
                    return null;
                }

                // 一个名字只能归一个物种：否则"打牛"到底打哪只就取决于表的顺序了
                for (int n = 0; n < spec.Names.Count; n++)
                {
                    if (!seenNames.Add(spec.Names[n]))
                    {
                        error = "animal name '" + spec.Names[n] + "' is already used by another species";
                        return null;
                    }
                }
                table.Species.Add(spec);
            }

            if (table.Species.Count == 0)
            {
                error = "chat animals needs at least one species";
                return null;
            }

            table.SourceHash = ComputeHash(table);
            return table;
        }

        public static string Write(ChatAnimalAliases table)
        {
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"format\": \"").Append(table.Format).Append("\",\r\n");
            sb.Append("  \"version\": ").Append(table.Version).Append(",\r\n");
            sb.Append("  \"id\": \"").Append(Escape(table.Id)).Append("\",\r\n");
            if (!string.IsNullOrEmpty(table.Name))
                sb.Append("  \"name\": \"").Append(Escape(table.Name)).Append("\",\r\n");
            if (!string.IsNullOrEmpty(table.Description))
                sb.Append("  \"description\": \"").Append(Escape(table.Description)).Append("\",\r\n");
            sb.Append("  \"species\": [\r\n");
            for (int i = 0; i < table.Species.Count; i++)
            {
                ChatAnimalSpec spec = table.Species[i];
                sb.Append("    { \"key\": \"").Append(Escape(spec.Key)).Append('"');
                if (!string.IsNullOrEmpty(spec.Description))
                    sb.Append(", \"description\": \"").Append(Escape(spec.Description)).Append('"');
                if (spec.CategoryMask != 0)
                    sb.Append(", \"category\": \"").Append(ChatAnimalAliases.MaskName(spec.CategoryMask)).Append('"');
                sb.Append(", \"names\": [");
                for (int n = 0; n < spec.Names.Count; n++)
                {
                    if (n > 0) sb.Append(", ");
                    sb.Append('"').Append(Escape(spec.Names[n])).Append('"');
                }
                sb.Append("] }");
                if (i < table.Species.Count - 1)
                    sb.Append(',');
                sb.Append("\r\n");
            }
            sb.Append("  ]\r\n");
            sb.Append("}\r\n");
            return sb.ToString();
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            var sb = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>内容哈希（行尾归一再算，保证 CRLF/LF 检出算出同一个值）。</summary>
        public static string ComputeHash(ChatAnimalAliases table)
        {
            string text = Write(table).Replace("\r\n", "\n").Replace('\r', '\n');
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// 别名表的**目录来源 + 文件戳热重载**（与 <see cref="ChatPhraseCatalog"/> 同一套规矩）。
    ///
    /// 刻意只认 `animals.json` 这一个文件名：`Chat/` 文件夹里同时住着 `phrases.json`，
    /// "挑目录里唯一的 json" 那种兜底会把短语表当别名表读进来。
    /// </summary>
    public static class ChatAnimalCatalog
    {
        private static readonly List<string> s_directories = new List<string>();
        private static readonly HashSet<string> s_reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static ChatAnimalAliases s_current;
        private static string s_currentPath;
        private static long s_stamp;
        private static bool s_force;
        private static string s_lastError;

        /// <summary>日志出口（运行期接到 `Engine.Log`，自检里可以留空）。</summary>
        public static Action<string> LogSink;

        /// <summary>表内容变化时回调（用来记一条"热重载了"的事件）。</summary>
        public static Action Changed;

        public static ChatAnimalAliases Current
        {
            get
            {
                EnsureFresh();
                return s_current ?? ChatAnimalAliases.Default;
            }
        }

        public static string CurrentPath
        {
            get { return s_currentPath; }
        }

        public static string LastError
        {
            get { return s_lastError; }
        }

        /// <summary>与上次读盘相比，文件戳或路径变了（UI 用来显示"磁盘上改了"）。</summary>
        public static bool ChangedOnDisk
        {
            get
            {
                string path = FindFile();
                if (path == null)
                    return s_currentPath != null;
                return !string.Equals(path, s_currentPath, StringComparison.OrdinalIgnoreCase)
                    || StampOf(path) != s_stamp;
            }
        }

        public static IReadOnlyList<string> Directories
        {
            get { return s_directories; }
        }

        /// <summary>由实例根算出目录：`&lt;实例根&gt;/PlayerAi/Chat`（与 `phrases.json` 同处）。</summary>
        public static List<string> DirectoriesFor(string instanceRoot)
        {
            var directories = new List<string>();
            if (!string.IsNullOrEmpty(instanceRoot))
                directories.Add(Path.Combine(instanceRoot, DigestCatalog.PlayerAiFolderName, ChatPhraseCatalog.FolderName));
            return directories;
        }

        public static void Configure(IEnumerable<string> directories)
        {
            s_directories.Clear();
            if (directories != null)
            {
                foreach (string directory in directories)
                    if (!string.IsNullOrEmpty(directory) && !s_directories.Contains(directory))
                        s_directories.Add(directory);
            }
        }

        public static void EnsureFresh()
        {
            string path = FindFile();
            if (path == null)
            {
                if (s_currentPath != null || s_current == null)
                {
                    s_current = ChatAnimalAliases.Default;
                    s_currentPath = null;
                    s_stamp = 0;
                    s_force = false;
                    s_lastError = null;
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

            ChatAnimalAliases parsed = Load(path, out string error);
            if (parsed == null)
            {
                s_lastError = error;
                if (s_current == null)
                    s_current = ChatAnimalAliases.Default;
                if (s_reported.Add(path))
                    Log("[PlayerAi][chat] rejected " + path + " (keeping the previous animal table): " + error);
                return;
            }

            bool changed = s_current == null
                || !string.Equals(s_current.SourceHash, parsed.SourceHash, StringComparison.Ordinal);
            s_current = parsed;
            s_currentPath = path;
            s_stamp = stamp;
            s_lastError = null;
            s_reported.Remove(path);
            if (changed)
            {
                Log("[PlayerAi][chat] animals loaded from " + path
                    + " (hash=" + parsed.SourceHash + " species=" + parsed.Species.Count + ")");
                if (Changed != null)
                    Changed();
            }
        }

        /// <summary>强制重读（不看文件戳）。</summary>
        public static bool Reload(out ChatAnimalAliases table, out string error)
        {
            s_force = true;
            s_reported.Clear();                 // 手动重载：同一个错也要能再报一次
            EnsureFresh();
            table = s_current;
            error = s_lastError;
            return error == null;
        }

        public static ChatAnimalAliases Load(string path, out string error)
        {
            error = null;
            try
            {
                string json = File.ReadAllText(path);
                return ChatAnimalAliasesJson.Parse(json, path, out error);
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return null;
            }
        }

        /// <summary>自检用：清掉缓存与目录。</summary>
        public static void ResetForTests()
        {
            s_directories.Clear();
            s_reported.Clear();
            s_current = null;
            s_currentPath = null;
            s_stamp = 0;
            s_force = false;
            s_lastError = null;
        }

        private static string FindFile()
        {
            for (int d = 0; d < s_directories.Count; d++)
            {
                string directory = s_directories[d];
                if (string.IsNullOrEmpty(directory))
                    continue;
                string path = Path.Combine(directory, ChatAnimalAliases.FileName);
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        private static long StampOf(string path)
        {
            try
            {
                // 与 `ChatPhraseCatalog` 同一套：时间戳 + **文件长度**都算进戳里。
                // 理由是时间戳的分辨率只有系统时钟一拍（约 15 ms），同一拍内的两次写入
                // 会拿到相同的 `LastWriteTime` —— 那时热重载会看不见"文件改过了"。
                var info = new FileInfo(path);
                return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0L;
            }
            catch (Exception)
            {
                return 0L;
            }
        }

        private static void Log(string message)
        {
            Action<string> sink = LogSink;
            if (sink != null)
                sink(message);
        }
    }
}
