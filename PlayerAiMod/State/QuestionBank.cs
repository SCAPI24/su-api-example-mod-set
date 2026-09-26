using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlayerAiMod
{
    /// <summary>一条问题模板里的选项（`choice`）或等级（`score`）。</summary>
    public sealed class QuestionOption
    {
        public string Key;
        public string Description;

        public override string ToString()
        {
            return Key + (string.IsNullOrEmpty(Description) ? string.Empty : "=" + Description);
        }
    }

    /// <summary>
    /// 一条固定的问题模板（plan §4.3：**问题库必须固化**）。
    ///
    /// 为什么固化而不是"每次现编"：Laya 的答案质量高度依赖 `instructions` 与选项描述的措辞，
    /// 固定下来才能被 LLM 批量调参、被日志复盘、被 A/B 对比（同一状态同一问题，改描述看分离度）。
    /// </summary>
    public sealed class QuestionTemplate
    {
        public string Id;

        /// <summary>`choice` / `noul` / `score`。**是非题一律写成两项 choice**（§4.3 实测）。</summary>
        public string Type = "choice";

        public string Instructions;

        public readonly List<QuestionOption> Options = new List<QuestionOption>();

        /// <summary>`noul` 可选：true / false 两种判据说明。</summary>
        public string TrueCriteria;
        public string FalseCriteria;

        public string Description;

        public bool IsChoice
        {
            get { return string.Equals(Type, "choice", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsNoul
        {
            get { return string.Equals(Type, "noul", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsScore
        {
            get { return string.Equals(Type, "score", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>选项 key 的闭集（校验"答案必须落在候选里"）。</summary>
        public List<string> OptionKeys()
        {
            var keys = new List<string>();
            for (int i = 0; i < Options.Count; i++)
                keys.Add(Options[i].Key);
            return keys;
        }

        public string Describe()
        {
            return Id + ":" + Type + "(" + Options.Count + " opts)";
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>问题库（一个文件里的一组问题，可带模板名）。</summary>
    public sealed class QuestionBank
    {
        public const string FormatName = "qbank";
        public const int FormatVersion = 1;
        public const string Extension = ".qbank";

        /// <summary>
        /// 问题库目录名（`&lt;实例根&gt;/PlayerAi/Questions/`）。
        /// 放在这里而不是 `QuestionBankTemplates`：模板那份依赖 `Engine.Log`（游戏侧胶水），
        /// **编辑器不编它** —— 于是编辑器要用目录名时只能抄一个字面量，两处一旦不一致就是"编辑器列不出库"。
        /// </summary>
        public const string FolderName = "Questions";

        /// <summary>
        /// 异常处理专用问题库的文件名（plan §4.12）。
        /// 与 <see cref="FolderName"/> 同一个理由放在这里：`PackageTemplates` 与编辑器都会引用它，
        /// 而模板那份（`QuestionBankTemplates`）依赖 `Engine.Log`、**编辑器不编**。
        /// </summary>
        public const string RecoverBankFileName = "pkg_recover.qbank";

        /// <summary>
        /// 世界内主问题库的文件名（`goal` / `threat` / `can_reach`）。
        /// 同样放在这里：出厂树包（`PackageTemplates` 的 Laya 主循环示例）要在**编辑器也编得到**的
        /// 文件里引用它，而 `QuestionBankTemplates` 编辑器不编（A29 的老坑）。
        /// </summary>
        public const string WorldGoalFileName = "world_goal.qbank";

        /// <summary>世界外界面问题库的文件名（`front_goal` / `dialog_action`）。理由同 <see cref="WorldGoalFileName"/>。</summary>
        public const string FrontGoalFileName = "front_goal.qbank";

        public string Id;
        public string Name;
        public string Description;

        /// <summary>
        /// 库文件里声明的版本（`"version": 1`）—— **它就是"格式版本"，不是内容版本**。
        ///
        /// ⚠️ **只认 <see cref="FormatVersion"/>（=1）**：写成 2 会被解析器直接拒掉
        /// （`unsupported question bank version 2`）。实测踩过：以为它是"人改过几轮判准"的
        /// 内容版本，把它从 1 改成 2 想记一笔，结果**整库解析不了** ——
        /// 出厂库解析不了会连锁报"包引用的问题库不存在"（plan A59）。
        ///
        /// "内容改过几轮"这件事**由 `SourceHash` 承担**：它进请求指纹与复盘明细，
        /// 于是"改完选项描述之后再看复盘表"照样能分清新旧样本（plan G18）。
        /// </summary>
        public int Version = FormatVersion;

        /// <summary>可选模板名：同一个库可以放多套（按名取用）。</summary>
        public string Template;

        public readonly List<QuestionTemplate> Questions = new List<QuestionTemplate>();

        public string SourcePath;
        public string SourceHash;

        public QuestionTemplate Find(string id)
        {
            for (int i = 0; i < Questions.Count; i++)
            {
                if (string.Equals(Questions[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    return Questions[i];
            }
            return null;
        }

        public QuestionBank Select(IEnumerable<string> ids)
        {
            var subset = new QuestionBank
            {
                Id = Id,
                Name = Name,
                Description = Description,
                Template = Template,
                SourcePath = SourcePath,
                SourceHash = SourceHash
            };
            if (ids == null)
                return subset;
            foreach (string id in ids)
            {
                QuestionTemplate question = Find(id);
                if (question != null)
                    subset.Questions.Add(question);
            }
            return subset;
        }

        public string Describe()
        {
            return "qbank " + (Id ?? "?") + " questions=" + Questions.Count;
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// 问题库的解析与**预算守卫**（plan §4.4/§5.3）。
    ///
    /// 预算模型（读 `app/laya_engine.py` 得到，比"共享 1024"更精确）：
    ///   每问一条独立序列 = `[CLS] + head("type question: " + instructions) + [SEP] + 选项… + [SEP] + state + [SEP]`
    ///   · **head 预算 = head_max_len（默认 256）**：instructions 与全部选项文本**共同**占用；
    ///   · **body 预算 = max_len（默认 1024）− head 实际长度 − 2**：state 占剩下的。
    ///   超了会**静默截断**（实测：8600 字符 state → input_tokens 正好 1024，没有任何报错信号），
    ///   所以必须**发之前自己拦**。
    ///
    /// 估算法：中文按 1 token/字、英文按 0.3 token/字符（实测英文 400 字符 ≈ 184 token；
    /// 中文 600 字 ≈ 204 token）。宁可高估。
    /// </summary>
    public static class QuestionBankParser
    {
        public const int DefaultHeadMaxLen = 256;
        public const int DefaultMaxLen = 1024;

        /// <summary>每问选项数上限（精度衰减：4→9/10，8→7/10，12→5/10）。</summary>
        public const int MaxOptionsPerQuestion = 12;

        /// <summary>单批问题数上限（引擎允许 20，但"一批少问"通常更快也更准）。</summary>
        public const int MaxQuestionsPerBatch = 12;

        public static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            int cjk = 0;
            int other = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 0x2E80 && c <= 0x9FFF)
                    cjk++;
                else
                    other++;
            }
            // 中文 ≈1 token/字；英文 ≈0.3 token/字符（实测比例）；取偏保守的向上取整
            return cjk + (int)Math.Ceiling(other * 0.34);
        }

        /// <summary>一"问"在 head 里占的 token（instructions + 选项文本）。</summary>
        public static int EstimateHeadTokens(QuestionTemplate question)
        {
            if (question == null)
                return 0;
            int tokens = EstimateTokens(QuestionWire(question)[1]);
            for (int i = 0; i < question.Options.Count; i++)
                tokens += EstimateTokens(question.Options[i].Description);
            return tokens;
        }

        /// <summary>
        /// 发送前的预算守卫。返回空列表 = 可以发；否则每条是人类可读的拒绝理由。
        /// **宁可本地拒绝，也不要发出去被静默截断。**
        /// </summary>
        public static List<string> CheckBudget(QuestionBank bank, string digest,
            int headMaxLen = DefaultHeadMaxLen, int maxLen = DefaultMaxLen)
        {
            var issues = new List<string>();
            if (bank == null || bank.Questions.Count == 0)
            {
                issues.Add("no questions to ask");
                return issues;
            }
            if (bank.Questions.Count > MaxQuestionsPerBatch)
            {
                issues.Add("too many questions in one batch: " + bank.Questions.Count
                    + " (limit " + MaxQuestionsPerBatch + ")");
            }

            int totalHead = 0;
            for (int i = 0; i < bank.Questions.Count; i++)
            {
                QuestionTemplate question = bank.Questions[i];
                if (question.IsChoice && question.Options.Count < 2)
                {
                    issues.Add(question.Id + ": choice needs at least 2 options");
                }
                if (question.IsScore && question.Options.Count < 2)
                {
                    issues.Add(question.Id + ": score needs at least 2 levels");
                }
                if (question.Options.Count > MaxOptionsPerQuestion)
                {
                    issues.Add(question.Id + ": " + question.Options.Count
                        + " options exceeds the accuracy limit (" + MaxOptionsPerQuestion + ")");
                }
                for (int o = 0; o < question.Options.Count; o++)
                {
                    if (string.IsNullOrEmpty(question.Options[o].Description))
                    {
                        issues.Add(question.Id + ": option '" + question.Options[o].Key
                            + "' has no description (descriptions are what the model discriminates on)");
                    }
                }

                int head = EstimateHeadTokens(question);
                totalHead += head;
                if (head > headMaxLen)
                {
                    issues.Add(question.Id + ": head budget exceeded (~" + head + " > " + headMaxLen
                        + " tokens; shorten instructions or option descriptions)");
                }
            }

            int digestTokens = EstimateTokens(digest);
            int bodyBudget = maxLen - totalHead - 4 * bank.Questions.Count;
            if (digestTokens > bodyBudget)
            {
                issues.Add("digest too long for this batch: ~" + digestTokens + " > " + bodyBudget
                    + " tokens (shrink the digest or ask fewer questions)");
            }
            return issues;
        }

        /// <summary>
        /// 把问题库编译成"线格式"：`Dictionary&lt;问题id, (类型, instructions, criteria)&gt;`。
        /// criteria 的形状与引擎一致：`choice` 是 `key → 描述`、`score` 是数组、`noul` 可选。
        /// </summary>
        public static Dictionary<string, object> ToWireQuestions(QuestionBank bank)
        {
            var wire = new Dictionary<string, object>(StringComparer.Ordinal);
            if (bank == null)
                return wire;

            for (int i = 0; i < bank.Questions.Count; i++)
            {
                QuestionTemplate question = bank.Questions[i];
                string[] parts = QuestionWire(question);
                var entry = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = parts[0],
                    ["instructions"] = parts[1]
                };

                if (question.IsChoice)
                {
                    var criteria = new Dictionary<string, object>(StringComparer.Ordinal);
                    for (int o = 0; o < question.Options.Count; o++)
                        criteria[question.Options[o].Key] = question.Options[o].Description;
                    entry["criteria"] = criteria;
                }
                else if (question.IsScore)
                {
                    var levels = new List<object>();
                    for (int o = 0; o < question.Options.Count; o++)
                        levels.Add(question.Options[o].Description ?? question.Options[o].Key);
                    entry["criteria"] = levels;
                }
                else if (!string.IsNullOrEmpty(question.TrueCriteria) || !string.IsNullOrEmpty(question.FalseCriteria))
                {
                    var criteria = new Dictionary<string, object>(StringComparer.Ordinal);
                    criteria["true"] = question.TrueCriteria;
                    criteria["false"] = question.FalseCriteria;
                    entry["criteria"] = criteria;
                }

                wire[question.Id] = entry;
            }
            return wire;
        }

        /// <summary>[类型, instructions]（两者一起用于 token 估算与线格式）。</summary>
        private static string[] QuestionWire(QuestionTemplate question)
        {
            string type = string.IsNullOrEmpty(question.Type) ? "choice" : question.Type.Trim().ToLowerInvariant();
            string instructions = question.Instructions;
            if (string.IsNullOrEmpty(instructions))
                instructions = question.Id;
            return new[] { type, instructions };
        }

        // ---------------------------------------------------------------- 解析

        /// <summary>解析一个 `.qbank` JSON。返回 null 时 <paramref name="error"/> 给原因。</summary>
        public static QuestionBank Parse(string json, string sourcePath, out string error)
        {
            error = null;
            var report = new PackageReport();
            PackageValue root;
            if (!PackageJson.TryParse(json ?? string.Empty, sourcePath ?? "qbank", report, out root))
            {
                PackageIssue first = report.FirstError;
                error = "invalid JSON: " + (first != null ? first.Describe() : "parse failed");
                return null;
            }
            return Parse(root, sourcePath, out error);
        }

        public static QuestionBank Parse(PackageValue root, string sourcePath, out string error)
        {
            error = null;
            if (root == null || !root.IsObject)
            {
                error = "question bank must be a JSON object";
                return null;
            }

            string format = root.Get("format").AsString(null);
            if (!string.IsNullOrEmpty(format)
                && !string.Equals(format, QuestionBank.FormatName, StringComparison.OrdinalIgnoreCase))
            {
                error = "unsupported format '" + format + "' (expected '" + QuestionBank.FormatName + "')";
                return null;
            }

            int version = root.Get("version").AsInt(QuestionBank.FormatVersion);
            if (version != QuestionBank.FormatVersion)
            {
                // 报错里**给出唯一认的值**：实测有人（我）把它当"内容版本"改成 2，
            // 只看到 `unsupported question bank version 2` 还以为是内容版本没登记上（A59）。
            error = "unsupported question bank version " + version
                + " (this field is the FORMAT version and only " + QuestionBank.FormatVersion
                + " is accepted; use SourceHash to distinguish content revisions)";
                return null;
            }

            var bank = new QuestionBank
            {
                Id = root.Get("id").AsString(null),
                Name = root.Get("name").AsString(null),
                Description = root.Get("description").AsString(null),
                Template = root.Get("template").AsString(null),
                Version = version,
                SourcePath = sourcePath
            };
            if (string.IsNullOrEmpty(bank.Id))
            {
                error = "question bank needs an 'id'";
                return null;
            }

            PackageValue questions = root.Get("questions");
            if (!questions.IsArray || questions.Count == 0)
            {
                error = "question bank needs a non-empty 'questions' array";
                return null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < questions.Count; i++)
            {
                PackageValue item = questions.Item(i);
                if (!item.IsObject)
                {
                    error = "questions[" + i + "] must be an object";
                    return null;
                }

                string id = item.Get("id").AsString(null);
                if (string.IsNullOrEmpty(id))
                {
                    error = "questions[" + i + "] needs an 'id'";
                    return null;
                }
                if (!seen.Add(id))
                {
                    error = "duplicate question id '" + id + "'";
                    return null;
                }

                string type = (item.Get("type").AsString("choice") ?? "choice").Trim().ToLowerInvariant();
                if (type != "choice" && type != "noul" && type != "score")
                {
                    error = "question '" + id + "' has unsupported type '" + type
                        + "' (choice / noul / score)";
                    return null;
                }

                var question = new QuestionTemplate
                {
                    Id = id,
                    Type = type,
                    Instructions = item.Get("instructions").AsString(null),
                    Description = item.Get("description").AsString(null),
                    TrueCriteria = item.Get("trueCriteria").AsString(null),
                    FalseCriteria = item.Get("falseCriteria").AsString(null)
                };
                if (string.IsNullOrEmpty(question.Instructions))
                {
                    error = "question '" + id + "' needs 'instructions'";
                    return null;
                }

                PackageValue options = item.Get("options");
                if (options.IsArray)
                {
                    for (int o = 0; o < options.Count; o++)
                    {
                        PackageValue option = options.Item(o);
                        if (option.IsObject)
                        {
                            question.Options.Add(new QuestionOption
                            {
                                Key = option.Get("key").AsString(null),
                                Description = option.Get("description").AsString(null)
                            });
                        }
                        else
                        {
                            question.Options.Add(new QuestionOption { Key = option.AsString(null) });
                        }
                    }
                }

                if (question.IsChoice || question.IsScore)
                {
                    if (question.Options.Count == 0)
                    {
                        error = "question '" + id + "' is " + type + " and needs 'options'";
                        return null;
                    }
                    for (int o = 0; o < question.Options.Count; o++)
                    {
                        if (string.IsNullOrEmpty(question.Options[o].Key))
                        {
                            error = "question '" + id + "' option " + o + " needs a 'key'";
                            return null;
                        }
                    }
                }

                bank.Questions.Add(question);
            }

            return bank;
        }

        /// <summary>把预算问题转成人类可读的一行（日志/控制面用）。</summary>
        public static string DescribeIssues(List<string> issues)
        {
            if (issues == null || issues.Count == 0)
                return "ok";
            return string.Join("; ", issues.ToArray());
        }

        /// <summary>`wire` 的紧凑描述（日志/复盘用，不参与协议）。</summary>
        public static string DescribeWire(Dictionary<string, object> wire)
        {
            if (wire == null)
                return "<null>";
            var parts = new List<string>();
            foreach (KeyValuePair<string, object> pair in wire)
                parts.Add(pair.Key);
            parts.Sort(StringComparer.Ordinal);
            return string.Join(",", parts.ToArray());
        }

        internal static string FormatNumber(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
