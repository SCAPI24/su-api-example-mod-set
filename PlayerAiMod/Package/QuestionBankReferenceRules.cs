using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>一个 `Task.LayaAsk` 节点引用问题库的**事实**（校验器的输入）。</summary>
    public sealed class LayaAskReference
    {
        /// <summary>节点位置（`package.scbtpak#node`）。</summary>
        public string Where;

        /// <summary>`questions` 字面量（文件名）。`questionsKey` 那种动态取名的留 null。</summary>
        public string BankName;

        /// <summary>`questionsKey`（动态库名）—— 有值时**校验不了**，报告要如实说。</summary>
        public string QuestionsKey;

        /// <summary>`only=` 里声明的问题 id（空 = 全问）。</summary>
        public readonly List<string> Only = new List<string>();

        public readonly List<LayaAnswerBindingReference> Bindings = new List<LayaAnswerBindingReference>();
    }

    /// <summary>一条 `黑板键:问题id:类型` 绑定。</summary>
    public sealed class LayaAnswerBindingReference
    {
        public string BlackboardKey;
        public string QuestionId;

        /// <summary>已归一成 `str` / `bool` / `int` / `float`。</summary>
        public string Kind;
    }

    /// <summary>
    /// 一处"拿答案做分支"的比较：`Blackboard` 装饰器的 `query=Compare` + `valueKind=string`。
    /// </summary>
    public sealed class AnswerCompareReference
    {
        public string Where;
        public string BlackboardKey;
        public string Operator;
        public string Value;
    }

    /// <summary>
    /// **构建期引用校验**（plan G4）：把"问题库/问题 id/答案类型/选项 key"这四类引用
    /// 在**装载/保存之前**核对掉，而不是等运行时。
    ///
    /// 为什么值得单独一层：这四类错误的运行时表现**全都是"静默降质"而不是报错** ——
    ///   · 题目改名 → 那次询问照样发得出去，只是绑定的问题 id 拿不到 → 分支永远走不中；
    ///   · 选项 key 改名 → 比较字面量对不上 → **那一条分支静默失效**（树看着是完整的）；
    ///   · 答案类型写错 → `WriteBinding` 返回 false，一次**已经付过钱的**请求白花，节点才失败；
    ///   · 整库缺文件 → 装载期只看"字段有没有"，到第一次真正要问的时候才炸。
    /// 这些错误人眼在 JSON 里都很难看出来，所以只能靠校验器。
    ///
    /// 纯逻辑：不认识游戏、不认识文件系统 —— 只吃上面这几个事实结构 + 一个
    /// <see cref="IQuestionBankSource"/>。于是规则能在没有游戏的临时工程里逐条自检。
    /// </summary>
    public static class QuestionBankReferenceRules
    {
        /// <summary>
        /// 答案类型（`answerKeys` 里声明的）与问题类型（库里的）**必须相容**。
        ///
        /// 这张表**照抄 <c>BtLayaAskTask.WriteBinding</c> 的实际分支**，不是"理论上应该"：
        ///   · `choice` → `str`（写 key）；`bool` 只在"选项就是 yes/no 这种可折算取值"时才有意义
        ///     （它把 key 与 `yes`/`true` 比），选项是 `mine`/`gather` 时**永远写 false** ——
        ///     这条最危险，因为 `WriteBinding` 对它返回 true，**不会失败**；
        ///   · `noul`（0~1 概率）→ `bool`（>0.5）或 `float`；
        ///   · `score`（有序档位）→ `float` 或 `int`。
        /// 其余组合运行时一律 `return false`（节点失败），所以这里报 **error** 而不是 warning。
        /// </summary>
        public static bool TypeAllowed(string questionType, string kind, IReadOnlyList<string> optionKeys)
        {
            string type = (questionType ?? "choice").Trim().ToLowerInvariant();
            if (type == "choice")
            {
                if (kind == "str")
                    return true;
                if (kind == "bool")
                    return IsBooleanChoice(optionKeys);
                return false;
            }
            if (type == "noul")
                return kind == "bool" || kind == "float";
            if (type == "score")
                return kind == "float" || kind == "int";
            return false;
        }

        /// <summary>选项集是不是"能折算成真假"的那种（`yes/no`、`true/false`）。</summary>
        public static bool IsBooleanChoice(IReadOnlyList<string> optionKeys)
        {
            if (optionKeys == null || optionKeys.Count == 0)
                return false;
            for (int i = 0; i < optionKeys.Count; i++)
            {
                string key = (optionKeys[i] ?? string.Empty).Trim();
                if (!string.Equals(key, "yes", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(key, "no", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(key, "true", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(key, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 跑完一个树包的全部引用检查。`banks == null` 表示**调用方没给库来源** ——
        /// 这时只报"哪些引用**没被校验**"（`bank.unverified` 警告），不假装通过：
        /// 判不了要说清，否则"校验通过"会被误读成"引用都对"。
        /// </summary>
        public static void Check(IReadOnlyList<LayaAskReference> asks,
            IReadOnlyList<AnswerCompareReference> compares,
            IQuestionBankSource banks, string where, PackageReport report)
        {
            if (report == null)
                return;
            if (asks == null || asks.Count == 0)
                return;

            // 黑板键 → 问题 id（供选项 key 闭集校验用）。同一个键被两个问题写 = 静默后写覆盖前写。
            var blackboardToQuestion = new Dictionary<string, QuestionTemplate>(StringComparer.OrdinalIgnoreCase);
            var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < asks.Count; i++)
            {
                LayaAskReference ask = asks[i];
                if (ask == null)
                    continue;

                if (string.IsNullOrEmpty(ask.BankName))
                {
                    report.Warn(PackageCodes.BankUnverified, ask.Where ?? where,
                        "this node picks its question bank at runtime (questionsKey='" + (ask.QuestionsKey ?? "?")
                        + "'), so its question ids, answer types and option keys could not be checked");
                    continue;
                }

                if (banks == null)
                {
                    report.Warn(PackageCodes.BankUnverified, ask.Where ?? where,
                        "question bank '" + ask.BankName + "' was not checked (no question bank source was supplied)");
                    continue;
                }

                QuestionBank bank;
                string error;
                if (!banks.TryGetBank(ask.BankName, out bank, out error))
                {
                    report.Error(PackageCodes.BankMissing, ask.Where ?? where,
                        "Task.LayaAsk references " + error);
                    continue;
                }

                CheckBindings(ask, bank, blackboardToQuestion, owner, report);

                for (int q = 0; q < bank.Questions.Count; q++)
                {
                    QuestionTemplate question = bank.Questions[q];
                    if (question == null || string.IsNullOrEmpty(question.Id))
                        continue;
                    if (IsAsked(ask, question.Id) && !HasBinding(ask, question.Id))
                    {
                        report.Warn(PackageCodes.BankAnswerUnused, ask.Where ?? where,
                            "question '" + question.Id + "' is asked but nothing is bound to it in answerKeys "
                            + "(you pay for the round trip and cannot branch on the answer)");
                    }
                }
            }

            if (compares == null)
                return;
            for (int i = 0; i < compares.Count; i++)
            {
                AnswerCompareReference compare = compares[i];
                if (compare == null || string.IsNullOrEmpty(compare.BlackboardKey))
                    continue;
                // 空字面量是"还没决定"的哨兵（出厂树用 `work.script != ""`），不是选项 key。
                if (string.IsNullOrEmpty(compare.Value))
                    continue;

                QuestionTemplate question;
                if (!blackboardToQuestion.TryGetValue(compare.BlackboardKey, out question) || question == null)
                    continue;
                if (!question.IsChoice)
                    continue;
                if (ContainsOption(question, compare.Value))
                    continue;

                report.Error(PackageCodes.BankOptionUnknown, compare.Where ?? where,
                    "'" + compare.Value + "' is not an option key of question '" + question.Id
                    + "' (options: " + QuestionBankMap.Describe(question.OptionKeys()) + ")"
                    + " - renaming an option key silently kills this branch");
            }
        }

        private static void CheckBindings(LayaAskReference ask, QuestionBank bank,
            Dictionary<string, QuestionTemplate> blackboardToQuestion,
            Dictionary<string, string> owner, PackageReport report)
        {
            string where = ask.Where;
            var seenBlackboard = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < ask.Bindings.Count; i++)
            {
                LayaAnswerBindingReference binding = ask.Bindings[i];
                if (binding == null || string.IsNullOrEmpty(binding.QuestionId))
                    continue;

                QuestionTemplate question = bank.Find(binding.QuestionId);
                if (question == null)
                {
                    var ids = new List<string>();
                    for (int q = 0; q < bank.Questions.Count; q++)
                        ids.Add(bank.Questions[q].Id);
                    report.Error(PackageCodes.BankQuestionMissing, where,
                        "answerKeys binds question '" + binding.QuestionId + "' which does not exist in question bank '"
                        + bank.Id + "' (questions: " + QuestionBankMap.Describe(ids) + ")");
                    continue;
                }

                if (!IsAsked(ask, question.Id))
                {
                    report.Error(PackageCodes.BankBindingNotAsked, where,
                        "answerKeys binds question '" + question.Id + "' but only='" + string.Join(",", ask.Only.ToArray())
                        + "' means this node never asks it");
                }

                if (!TypeAllowed(question.Type, binding.Kind, question.OptionKeys()))
                {
                    report.Error(PackageCodes.BankAnswerType, where,
                        "question '" + question.Id + "' is type '" + question.Type + "' but blackboard key '"
                        + binding.BlackboardKey + "' is declared '" + binding.Kind + "'"
                        + (question.IsChoice && binding.Kind == "bool"
                            ? " (a choice answer only folds into bool when its options are yes/no)"
                            : " (the runtime refuses this pairing: the answer is paid for and then thrown away)"));
                }

                string previous;
                if (seenBlackboard.TryGetValue(binding.BlackboardKey, out previous)
                    && !string.Equals(previous, binding.QuestionId, StringComparison.OrdinalIgnoreCase))
                {
                    report.Error(PackageCodes.BankAnswerType, where,
                        "blackboard key '" + binding.BlackboardKey + "' is bound to two different questions ('"
                        + previous + "' and '" + binding.QuestionId + "') - the later write wins silently");
                }
                seenBlackboard[binding.BlackboardKey] = binding.QuestionId;
                blackboardToQuestion[binding.BlackboardKey] = question;
                owner[binding.BlackboardKey] = where;
            }

            for (int i = 0; i < ask.Only.Count; i++)
            {
                string id = ask.Only[i];
                if (string.IsNullOrEmpty(id))
                    continue;
                if (bank.Find(id) == null)
                {
                    var ids = new List<string>();
                    for (int q = 0; q < bank.Questions.Count; q++)
                        ids.Add(bank.Questions[q].Id);
                    report.Error(PackageCodes.BankQuestionMissing, where,
                        "only='" + id + "' names a question that does not exist in question bank '" + bank.Id
                        + "' (questions: " + QuestionBankMap.Describe(ids) + ")");
                }
            }
        }

        private static bool IsAsked(LayaAskReference ask, string questionId)
        {
            if (ask.Only.Count == 0)
                return true;
            for (int i = 0; i < ask.Only.Count; i++)
            {
                if (string.Equals(ask.Only[i], questionId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool HasBinding(LayaAskReference ask, string questionId)
        {
            for (int i = 0; i < ask.Bindings.Count; i++)
            {
                LayaAnswerBindingReference binding = ask.Bindings[i];
                if (binding != null && string.Equals(binding.QuestionId, questionId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool ContainsOption(QuestionTemplate question, string value)
        {
            for (int i = 0; i < question.Options.Count; i++)
            {
                if (string.Equals(question.Options[i].Key, value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
