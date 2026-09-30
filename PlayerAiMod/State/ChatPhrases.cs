using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 聊天短语表里的一条意图：**一组说法 → 一个枚举**。
    ///
    /// 为什么是"关键词表"而不是让模型读自由文本：§12.1 已经量穿了这个模型的边界 ——
    /// 它做的是**表层 token 匹配**，不做多因素推断。指令识别恰恰是"表里有没有这个词"，
    /// 于是 C# 用一张可编辑的表把住它、模型只兜底（见 <see cref="ChatIntents"/>）。
    /// </summary>
    public sealed class ChatIntentSpec
    {
        public string Key;
        public List<string> Phrases = new List<string>();
        public string Description;

        /// <summary>
        /// **这个意图要带参数**：命中的短语只是"触发词"，句子里剩下的部分才是参数
        /// （例：「帮我打一下<b>牛</b>」→ 触发词 `帮我打一下`、参数 `牛`）。
        ///
        /// 为什么要这个开关：不像 `come`/`stop` 那样"命中即完整语义"，`hunt` 少了参数就无从下手。
        /// 标了它，`MatchEx` 才会去抠句子剩下的部分并写进黑板；没标就维持"命中即完毕"的老行为。
        /// </summary>
        public bool TakesArgument;

        public ChatIntentSpec Clone()
        {
            return new ChatIntentSpec
            {
                Key = Key, Description = Description, TakesArgument = TakesArgument,
                Phrases = new List<string>(Phrases)
            };
        }
    }

    /// <summary>
    /// 一次匹配的结果：意图 + 命中的短语 + 参数（只有带参意图才有）。
    /// 参数是**待解析的候选词**（`牛` 或 `牛,狼`），解析成具体生物是传感器的事
    /// （见 `ChatAnimalAliases` / `PlayerSensor.TryFindNearestCreatureByName`）。
    /// </summary>
    public sealed class ChatMatch
    {
        public string Intent;
        public string Phrase;

        /// <summary>带参意图的参数；不带参的意图为 null，抠不出东西时为空串。</summary>
        public string Argument;

        public bool HasArgument
        {
            get { return !string.IsNullOrEmpty(Argument); }
        }
    }

    /// <summary>
    /// 意图枚举（**闭集**，树里的分支与兜底问题库的选项都用它）。
    ///
    /// 加一个意图要同时改三处（选项 ↔ 分支 ↔ 行为），所以这里写死：
    /// 数据文件只能**给这些意图配说法**，不能自己发明意图 —— 否则模型/用户答出一个
    /// 树里没有分支的枚举，表现是"答了却什么都不发生"（§4.7 说的那类静默降质）。
    /// </summary>
    public static class ChatIntents
    {
        public const string Come = "come";
        public const string Follow = "follow";
        public const string Stop = "stop";
        public const string Hunt = "hunt";
        public const string Ignore = "ignore";

        private static readonly string[] s_all = { Come, Follow, Stop, Hunt, Ignore };

        public static List<string> All()
        {
            return new List<string>(s_all);
        }

        public static bool IsKnown(string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            for (int i = 0; i < s_all.Length; i++)
                if (string.Equals(s_all[i], key, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// 这个意图**由树解析参数**吗？目前只有 <see cref="Hunt"/>（打谁）。
        /// 其余意图命中即完整语义，给它们配 `takesArgument` 只会写出没人消费的黑板键。
        /// </summary>
        public static bool AcceptsArgument(string key)
        {
            return string.Equals(key, Hunt, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 聊天短语表（`PlayerAi/Chat/phrases.json`）：**说哪些话 = 哪个意图**。
    ///
    /// 与摘要规格同一套纪律（`DigestSpec`）：格式带版本、内容带哈希、缺文件用内置默认兜底、
    /// 解析失败拒绝并保留上一份 —— 于是"加一句话"是改文件，不是重编译 Mod。
    /// </summary>
    public sealed class ChatPhrases
    {
        public const string FormatName = "chatphrases";
        public const int FormatVersion = 1;
        public const string DefaultId = "world";

        public string Format = FormatName;
        public int Version = FormatVersion;
        public string Id = DefaultId;
        public string Name;
        public string Description;

        /// <summary>这些发送者的消息不当指令（ScMP 的系统通知等）。</summary>
        public List<string> IgnoreSenders = new List<string>();

        public List<ChatIntentSpec> Intents = new List<ChatIntentSpec>();

        public string SourcePath;
        public string SourceHash;

        public bool IsBuiltin
        {
            get { return string.IsNullOrEmpty(SourcePath); }
        }

        public ChatIntentSpec Intent(string key)
        {
            for (int i = 0; i < Intents.Count; i++)
                if (string.Equals(Intents[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return Intents[i];
            return null;
        }

        public bool IsIgnoredSender(string sender)
        {
            if (string.IsNullOrEmpty(sender))
                return false;
            for (int i = 0; i < IgnoreSenders.Count; i++)
                if (string.Equals(IgnoreSenders[i], sender, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public ChatPhrases Clone()
        {
            var copy = new ChatPhrases
            {
                Format = Format, Version = Version, Id = Id, Name = Name,
                Description = Description, SourcePath = SourcePath, SourceHash = SourceHash
            };
            copy.IgnoreSenders = new List<string>(IgnoreSenders);
            for (int i = 0; i < Intents.Count; i++)
                copy.Intents.Add(Intents[i].Clone());
            return copy;
        }

        // ------------------------------------------------------------------ 匹配

        /// <summary>
        /// 一句话 → 意图（命中不了返回 null）。规则：
        ///   · 归一化：小写 + 去首尾空白（中文不受影响）；
        ///   · **纯 ASCII 短语按词边界**匹配 —— 否则 `come` 会命中 `become`
        ///     （这类误伤在少数字符集短语上很常见，而误命中的后果是"AI 突然跑过来"）；
        ///   · 含非 ASCII（中文）的短语按**子串**匹配（中文没有词边界这回事）。
        /// 顺序即优先级：表里靠前的意图先命中（"别过来" 里同时含 "过来" 和 "别过来"，
        /// 所以 `stop` 一定要写在 `come` 前面）。
        /// </summary>
        public static string Match(ChatPhrases phrases, string text)
        {
            ChatMatch match = MatchEx(phrases, text);
            return match != null ? match.Intent : null;
        }

        /// <summary>
        /// 带参数版本的匹配（`Match` 的完整形态）：除了意图，还给出**命中的短语**与**参数**。
        ///
        /// 参数只在 <see cref="ChatIntentSpec.TakesArgument"/> 为真时抽取；抠的是**最长**的那个命中短语
        /// （"帮我打死牛" 同时含 `帮我打` 与 `打死`，抠短的会剩个"死牛"）。
        /// 句子先被转成小写，所以参数也是小写 —— 匹配本来就不区分大小写，中文不受影响。
        /// </summary>
        public static ChatMatch MatchEx(ChatPhrases phrases, string text)
        {
            if (phrases == null || string.IsNullOrEmpty(text))
                return null;
            string haystack = text.Trim().ToLowerInvariant();
            if (haystack.Length == 0)
                return null;

            for (int i = 0; i < phrases.Intents.Count; i++)
            {
                ChatIntentSpec intent = phrases.Intents[i];
                string hit = null;
                for (int p = 0; p < intent.Phrases.Count; p++)
                {
                    string needle = (intent.Phrases[p] ?? string.Empty).Trim().ToLowerInvariant();
                    if (needle.Length == 0)
                        continue;
                    if (!ContainsPhrase(haystack, needle))
                        continue;
                    if (hit == null || needle.Length > hit.Length)
                        hit = needle;
                }
                if (hit == null)
                    continue;

                return new ChatMatch
                {
                    Intent = intent.Key,
                    Phrase = hit,
                    Argument = intent.TakesArgument ? ExtractArgument(haystack, hit) : null
                };
            }
            return null;
        }

        // ---------------------------------------------------------------- 参数抽取

        /// <summary>句子里出现这些标记就把参数当成并列的多个词（`牛和狼` → `牛,狼`）。</summary>
        private static readonly char[] s_separators = { '和', '跟', '与', '、', '，', ',', '+', '＆', '&', ' ' };

        /// <summary>两端要剥掉的标点。</summary>
        private static readonly char[] s_trimmers =
            { ' ', '\t', '。', '！', '!', '？', '?', '，', ',', '、', '；', ';', '：', ':', '.', '~', '～' };

        /// <summary>中文语气词/量词/客套话：只在**两端**反复剥（`狼牙棒` 里的字不会被吃掉）。</summary>
        private static readonly string[] s_fillersCjk =
        {
            "帮我", "帮忙", "给我", "一下", "一只", "一头", "一条", "那只", "这只", "那个", "这个",
            "它们", "他们", "它", "吧", "啊", "呀", "呗", "嘛", "呢", "了", "的", "请", "快", "去", "打"
        };

        /// <summary>英文语气词：按**整词**剥（否则 `buffalo` 里的 `a` 会被吃掉）。</summary>
        private static readonly string[] s_fillersAscii =
        {
            "please", "the", "that", "this", "those", "these", "a", "an", "some", "for", "me", "to"
        };

        /// <summary>
        /// 从句子里抠出参数：去掉命中的触发词 → 剥两端语气词 → 把并列词拆成逗号分隔。
        /// 例：`帮我打一下牛` → `牛`；`打死那只牛` → `牛`；`打我一下 牛和狼` → `牛,狼`。
        /// 抠不出东西时返回空串（调用方据此回一句"没说打什么"，而不是瞎猜）。
        /// </summary>
        internal static string ExtractArgument(string haystack, string phrase)
        {
            int index = haystack.IndexOf(phrase, StringComparison.Ordinal);
            string rest = index < 0 ? haystack : haystack.Remove(index, phrase.Length);
            rest = StripFillers(rest);
            return SplitCandidates(rest);
        }

        private static string StripFillers(string text)
        {
            string rest = text.Trim().Trim(s_trimmers);

            // ① 英文语气词：整词丢弃
            string[] tokens = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>();
            for (int i = 0; i < tokens.Length; i++)
            {
                bool drop = false;
                for (int f = 0; f < s_fillersAscii.Length; f++)
                {
                    if (string.Equals(tokens[i], s_fillersAscii[f], StringComparison.Ordinal))
                    {
                        drop = true;
                        break;
                    }
                }
                if (!drop)
                    kept.Add(tokens[i]);
            }
            rest = string.Join(" ", kept.ToArray()).Trim().Trim(s_trimmers);

            // ② 中文语气词：两端反复剥（剥完继续剥，`那只小牛吧` 这类能一路剥干净）
            bool changed = true;
            while (changed && rest.Length > 0)
            {
                changed = false;
                for (int i = 0; i < s_fillersCjk.Length; i++)
                {
                    string filler = s_fillersCjk[i];
                    if (rest.Length > filler.Length && rest.StartsWith(filler, StringComparison.Ordinal))
                    {
                        rest = rest.Substring(filler.Length);
                        changed = true;
                    }
                    else if (rest.Length > filler.Length && rest.EndsWith(filler, StringComparison.Ordinal))
                    {
                        rest = rest.Substring(0, rest.Length - filler.Length);
                        changed = true;
                    }
                }
                rest = rest.Trim().Trim(s_trimmers);
            }
            return rest;
        }

        private static string SplitCandidates(string text)
        {
            string[] parts = text.Split(s_separators, StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim().Trim(s_trimmers);
                if (part.Length > 0)
                    kept.Add(part);
            }
            return string.Join(",", kept.ToArray());
        }

        internal static bool ContainsPhrase(string haystack, string needle)
        {
            bool asciiOnly = IsAscii(needle);
            int index = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (index >= 0)
            {
                if (!asciiOnly)
                    return true;
                bool leftOk = index == 0 || !IsWordChar(haystack[index - 1]);
                int end = index + needle.Length;
                bool rightOk = end >= haystack.Length || !IsWordChar(haystack[end]);
                if (leftOk && rightOk)
                    return true;
                index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal);
            }
            return false;
        }

        private static bool IsAscii(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] > 127)
                    return false;
            return true;
        }

        private static bool IsWordChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9') || c == '_';
        }

        // ------------------------------------------------------------------ 内置默认

        private static ChatPhrases s_default;

        /// <summary>
        /// **内置默认短语表**（实例里没有 `phrases.json` 时的兜底）。
        ///
        /// 顺序有讲究：`stop` 在 `come` **前面** —— "别过来""不要过来" 同时含 `过来`，
        /// 先判 stop 才不会被当成"过来"。加词只加说法，不要调顺序。
        /// </summary>
        public static ChatPhrases Default
        {
            get
            {
                if (s_default == null)
                    s_default = BuildDefault();
                return s_default;
            }
        }

        private static ChatPhrases BuildDefault()
        {
            var phrases = new ChatPhrases
            {
                Id = DefaultId,
                Name = "world",
                Description = "出厂默认：中文为主的常用指令；顺序即优先级（stop 必须在 come 前面）。"
            };
            phrases.IgnoreSenders.Add("ScMP");
            phrases.IgnoreSenders.Add("Host");
            phrases.IgnoreSenders.Add("Server");

            phrases.Intents.Add(new ChatIntentSpec
            {
                Key = ChatIntents.Stop,
                Description = "站住 / 别动 / 别过来 / 别打（否定优先于打猎）",
                Phrases = new List<string>
                {
                    "停", "站住", "别动", "不要动", "别过来", "不要过来", "别跟了", "不用跟",
                    // 打猎的否定说法：不加这些，"别打牛" 会命中 hunt 的 `打`，变成真的去打牛
                    "别打", "不要打", "别打了", "别杀", "不要杀", "别攻击", "停手", "住手",
                    "stop", "stay", "wait", "hold", "don't move", "dont move", "stand still",
                    "stop attacking", "cease"
                }
            });
            phrases.Intents.Add(new ChatIntentSpec
            {
                Key = ChatIntents.Hunt,
                // 触发词只是"起手式"，真正打谁由句子里剩下的词决定（`TakesArgument`）
                TakesArgument = true,
                Description = "帮我打某个生物：触发词 + 物种词（「帮我打一下牛」）",
                Phrases = new List<string>
                {
                    "帮我打一下", "帮我打死", "帮我弄死", "帮我打", "帮我杀", "帮我猎",
                    "去打", "去杀", "打死", "杀掉", "杀死", "猎杀", "打一下", "攻击",
                    "kill", "attack", "hunt", "shoot"
                }
            });
            phrases.Intents.Add(new ChatIntentSpec
            {
                Key = ChatIntents.Follow,
                Description = "跟着我走",
                Phrases = new List<string>
                {
                    "跟着我", "跟紧", "跟我走", "跟上来", "follow me", "follow", "come with me"
                }
            });
            phrases.Intents.Add(new ChatIntentSpec
            {
                Key = ChatIntents.Come,
                Description = "过来（一次性到达）",
                Phrases = new List<string>
                {
                    "过来", "来我这", "来我这里", "来我这儿", "到我这里", "来一下", "过来一下",
                    "come", "come here", "come to me", "over here"
                }
            });
            phrases.Intents.Add(new ChatIntentSpec
            {
                Key = ChatIntents.Ignore,
                Description = "明确说别理我时什么都不做",
                Phrases = new List<string> { "别理我", "不用管我", "ignore me", "never mind" }
            });

            phrases.SourceHash = ChatPhrasesJson.ComputeHash(phrases);
            return phrases;
        }
    }

    /// <summary>`phrases.json` 的解析/写回（与 <see cref="DigestSpecJson"/> 同一套路）。</summary>
    public static class ChatPhrasesJson
    {
        public static ChatPhrases Parse(string json, string sourcePath, out string error)
        {
            error = null;
            var report = new PackageReport();
            PackageValue root;
            if (!PackageJson.TryParse(json ?? string.Empty, sourcePath ?? "phrases", report, out root))
            {
                PackageIssue first = report.FirstError;
                error = "invalid JSON: " + (first != null ? first.Describe() : "parse failed");
                return null;
            }

            if (root == null || !root.IsObject)
            {
                error = "chat phrases must be a JSON object";
                return null;
            }

            string format = root.Get("format").AsString(null);
            if (!string.IsNullOrEmpty(format)
                && !string.Equals(format, ChatPhrases.FormatName, StringComparison.OrdinalIgnoreCase))
            {
                error = "unsupported format '" + format + "' (expected '" + ChatPhrases.FormatName + "')";
                return null;
            }

            int version = root.Get("version").AsInt(ChatPhrases.FormatVersion);
            if (version != ChatPhrases.FormatVersion)
            {
                error = "unsupported chat phrases format version " + version
                    + " (only " + ChatPhrases.FormatVersion + " is accepted)";
                return null;
            }

            var phrases = new ChatPhrases
            {
                Format = ChatPhrases.FormatName,
                Version = version,
                Id = root.Get("id").AsString(null),
                Name = root.Get("name").AsString(null),
                Description = root.Get("description").AsString(null),
                SourcePath = sourcePath
            };
            if (string.IsNullOrEmpty(phrases.Id))
            {
                error = "chat phrases need an 'id'";
                return null;
            }

            PackageValue senders = root.Get("ignoreSenders");
            if (senders.IsArray)
            {
                for (int i = 0; i < senders.Count; i++)
                {
                    string sender = senders.Item(i).AsString(null);
                    if (!string.IsNullOrEmpty(sender))
                        phrases.IgnoreSenders.Add(sender);
                }
            }

            PackageValue intents = root.Get("intents");
            if (!intents.IsArray || intents.Count == 0)
            {
                error = "chat phrases need a non-empty 'intents' array";
                return null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < intents.Count; i++)
            {
                PackageValue item = intents.Item(i);
                if (!item.IsObject)
                {
                    error = "intents[" + i + "] must be an object";
                    return null;
                }
                string key = item.Get("key").AsString(null);
                if (string.IsNullOrEmpty(key))
                {
                    error = "intents[" + i + "] needs a 'key'";
                    return null;
                }
                if (!ChatIntents.IsKnown(key))
                {
                    // 数据只能给**已知意图**配说法：自己发明意图 = 树里没有分支接它
                    error = "intents[" + i + "] uses unknown intent '" + key
                        + "' (known: " + string.Join(", ", ChatIntents.All().ToArray()) + ")";
                    return null;
                }
                if (!seen.Add(key))
                {
                    error = "duplicate chat intent '" + key + "'";
                    return null;
                }

                var intent = new ChatIntentSpec
                {
                    Key = key.ToLowerInvariant(),
                    Description = item.Get("description").AsString(null),
                    TakesArgument = item.Get("takesArgument").AsBool(false)
                };
                if (intent.TakesArgument && !ChatIntents.AcceptsArgument(intent.Key))
                {
                    // 只有树里真去解析参数的意图才能标：标错了会写出一个没人消费的参数键
                    error = "intents[" + i + "] ('" + key + "') cannot take an argument";
                    return null;
                }
                PackageValue list = item.Get("phrases");
                if (list.IsArray)
                {
                    for (int p = 0; p < list.Count; p++)
                    {
                        string phrase = list.Item(p).AsString(null);
                        if (!string.IsNullOrWhiteSpace(phrase))
                            intent.Phrases.Add(phrase.Trim());
                    }
                }
                if (intent.Phrases.Count == 0)
                {
                    error = "chat intent '" + key + "' needs a non-empty 'phrases' array";
                    return null;
                }
                phrases.Intents.Add(intent);
            }

            phrases.SourceHash = ComputeHash(phrases);
            return phrases;
        }

        public static string Write(ChatPhrases phrases)
        {
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"format\": \"").Append(phrases.Format).Append("\",\r\n");
            sb.Append("  \"version\": ").Append(phrases.Version).Append(",\r\n");
            sb.Append("  \"id\": \"").Append(Escape(phrases.Id)).Append("\",\r\n");
            if (!string.IsNullOrEmpty(phrases.Name))
                sb.Append("  \"name\": \"").Append(Escape(phrases.Name)).Append("\",\r\n");
            if (!string.IsNullOrEmpty(phrases.Description))
                sb.Append("  \"description\": \"").Append(Escape(phrases.Description)).Append("\",\r\n");
            sb.Append("  \"ignoreSenders\": [");
            for (int i = 0; i < phrases.IgnoreSenders.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(Escape(phrases.IgnoreSenders[i])).Append('"');
            }
            sb.Append("],\r\n");
            sb.Append("  \"intents\": [\r\n");
            for (int i = 0; i < phrases.Intents.Count; i++)
            {
                ChatIntentSpec intent = phrases.Intents[i];
                sb.Append("    { \"key\": \"").Append(Escape(intent.Key)).Append('"');
                if (!string.IsNullOrEmpty(intent.Description))
                    sb.Append(", \"description\": \"").Append(Escape(intent.Description)).Append('"');
                if (intent.TakesArgument)
                    sb.Append(", \"takesArgument\": true");
                sb.Append(", \"phrases\": [");
                for (int p = 0; p < intent.Phrases.Count; p++)
                {
                    if (p > 0) sb.Append(", ");
                    sb.Append('"').Append(Escape(intent.Phrases[p])).Append('"');
                }
                sb.Append("] }");
                if (i < phrases.Intents.Count - 1)
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
        public static string ComputeHash(ChatPhrases phrases)
        {
            string text = Write(phrases).Replace("\r\n", "\n").Replace('\r', '\n');
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
    /// 短语表的**目录来源 + 文件戳热重载**（与 <see cref="DigestCatalog"/> 同一套规矩：
    /// 缺文件用内置默认、解析失败保留上一份、只在真要用时读盘）。
    /// </summary>
    public static class ChatPhraseCatalog
    {
        public const string FolderName = "Chat";
        public const string Extension = ".json";
        public const string DefaultFileName = "phrases" + Extension;

        private static readonly List<string> s_directories = new List<string>();
        private static readonly HashSet<string> s_reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static ChatPhrases s_current;
        private static string s_currentPath;
        private static long s_stamp;
        private static bool s_force;
        private static string s_lastError;

        public static Action<string> LogSink;
        public static Action Changed;

        public static ChatPhrases Current
        {
            get
            {
                EnsureFresh();
                return s_current ?? ChatPhrases.Default;
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

        public static string LastError
        {
            get { return s_lastError; }
        }

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

        /// <summary>由实例根算出目录：`&lt;实例根&gt;/PlayerAi/Chat`（与 `Questions`/`Digest` 并列）。</summary>
        public static List<string> DirectoriesFor(string instanceRoot)
        {
            var directories = new List<string>();
            if (!string.IsNullOrEmpty(instanceRoot))
                directories.Add(Path.Combine(instanceRoot, DigestCatalog.PlayerAiFolderName, FolderName));
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
                    s_current = ChatPhrases.Default;
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

            ChatPhrases parsed = Load(path, out string error);
            if (parsed == null)
            {
                s_lastError = error;
                if (s_current == null)
                    s_current = ChatPhrases.Default;
                if (s_reported.Add(path))
                    Log("[PlayerAi][chat] rejected " + path + " (keeping the previous table): " + error);
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
                Log("[PlayerAi][chat] phrases reloaded: " + Path.GetFileName(path)
                    + " hash=" + parsed.SourceHash + " intents=" + parsed.Intents.Count);
                Action handler = Changed;
                if (handler != null)
                {
                    try { handler(); } catch (Exception) { }
                }
            }
        }

        public static bool Reload(out ChatPhrases phrases, out string error)
        {
            s_reported.Clear();
            s_force = true;
            EnsureFresh();
            phrases = s_current ?? ChatPhrases.Default;
            error = s_lastError;
            return s_lastError == null;
        }

        public static ChatPhrases Load(string path, out string error)
        {
            error = null;
            try
            {
                string text = File.ReadAllText(path);
                ChatPhrases parsed = ChatPhrasesJson.Parse(text, path, out error);
                if (parsed == null)
                    return null;
                if (string.IsNullOrEmpty(parsed.Name))
                    parsed.Name = Path.GetFileNameWithoutExtension(path);
                return parsed;
            }
            catch (Exception exception)
            {
                error = "chat phrases are unreadable: " + exception.GetType().Name + ": " + exception.Message;
                return null;
            }
        }

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
                string preferred = Path.Combine(directory, DefaultFileName);
                if (File.Exists(preferred))
                    return preferred;
            }
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
                        // `Chat/` 里还住着物种别名表（animals.json）：它属于另一个格式，
                        // 不能被当成"唯一的短语表"捡回来，否则每次读盘都要报一次格式不符
                        if (string.Equals(Path.GetFileName(files[f]), ChatAnimalAliases.FileName,
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (only != null)
                            return null;   // 多份候选：不瞎猜
                        only = files[f];
                    }
                }
                catch (Exception)
                {
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

        private static void Log(string message)
        {
            Action<string> sink = LogSink;
            if (sink == null || message == null)
                return;
            try { sink(message); } catch (Exception) { }
        }
    }
}
