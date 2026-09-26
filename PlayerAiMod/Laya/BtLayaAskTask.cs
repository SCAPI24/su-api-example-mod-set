using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 行为树任务：**问一次 Laya，把答案写进黑板**（plan §4.6 —— 用户问的核心）。
    ///
    /// 语义（异步节点，逐条都来自"不做会出事"）：
    ///   · `OnExecute` 只**发一次**请求（去重由 <see cref="LayaClient"/> 负责），返回 `Running`；
    ///   · `OnTick` 取结果 → 写黑板 → `Success`；超时/失败按 <see cref="OnUnavailable"/> 处理：
    ///     `fail`（默认，走兜底分支）/ `default`（写默认值继续）/ `keep`（保留旧值继续）；
    ///   · **未答 ≠ 可以继续**：没有答案时绝不猜一个值写进黑板；
    ///   · `OnExit` 取消在途请求，**绝不让上一个问题的答案落到下一棵子树**；
    ///   · **不读 `confidence`**（实测随上下文长度饱和，不是可信度）。
    ///
    /// 两种给脚本名/问题库的方式：
    ///   · `questions` 直接给 `.qbank` 文件名；
    ///   · `questionsKey` 给黑板 string 键，运行时取（Laya 或其它节点决定问哪套）。
    ///
    /// **失败标记不进摘要**（plan G22）：本节点写的是黑板键（给树的异常分支用），
    /// 不是给模型的状态输入。
    /// </summary>
    public sealed class BtLayaAskTask : BtTaskNode
    {
        /// <summary>问题库文件名（`<实例根>/PlayerAi/Questions/*.qbank`）。与 QuestionsKey 二选一。</summary>
        public string Questions { get; set; }

        /// <summary>可选：黑板 string 键，运行时从它取问题库名。</summary>
        public string QuestionsKey { get; set; }

        /// <summary>
        /// 答案写到哪些黑板键。语法 `黑板键:问题id:类型`（类型 `str|bool|int|float`），
        /// 多个用逗号分隔，例如 `goal:goal:str,danger:threat:bool`。
        /// **类型不符 = 本节点失败**（对应 §4.7 的兼容矩阵）。
        /// </summary>
        public string AnswerKeys { get; set; }

        /// <summary>可选：只要这一批里的这些问题（逗号分隔的问题 id）；为空 = 全问。</summary>
        public string Only { get; set; }

        /// <summary>节点级超时（毫秒）；0 = 用配置里的 timeoutMs。</summary>
        public int TimeoutMs { get; set; }

        /// <summary>拿不到答案时的行为：`fail`（默认）/ `default` / `keep`。</summary>
        public string OnUnavailable { get; set; } = "fail";

        /// <summary>`default` 模式下写什么（按答不上来的那个键的声明类型解释）。</summary>
        public string DefaultValue { get; set; }

        /// <summary>失败时是否写失败标记（错误码 / 原因）到黑板。</summary>
        public bool WriteFailKey { get; set; } = true;
        public string FailKey { get; set; } = "laya.fail";
        public string ReasonKey { get; set; } = "laya.reason";

        /// <summary>最近一次的错误码 / 原因（`ai.status`、日志）。</summary>
        public string LastErrorCode { get; private set; }
        public string LastError { get; private set; }

        /// <summary>最近一次判定的摘要（人类可读，便于复盘"当时问了什么"）。</summary>
        public string LastSummary { get; private set; }

        public override string NodeType
        {
            get { return "Task.LayaAsk"; }
        }

        public override bool IsLatent
        {
            get { return true; }
        }

        /// <summary>
        /// 取 Laya 服务：先看静态发布点，**再问"按需取"的回调**。
        ///
        /// 为什么必须兜这一下：`LayaRuntimeHost.Current` 只在"运行时的 `Laya` 属性第一次被访问"时才被填上。
        /// 于是"开机后第一棵用到 `Task.LayaAsk` 的树"会读到 null，报 `Laya service is not initialized` ——
        /// 而它其实完全可用（实机踩过：异常子树第一次跑就撞在这上面）。
        ///
        /// ⚠️ 这里**不能直接引用 `PlayerAiRuntime`**：本节点属于纯逻辑层，编辑器也编它，
        /// 而编辑器不收运行时那个文件 —— 直接引用会把编辑器编译打断（实测踩过）。
        /// 所以只认 `LayaRuntimeHost.Provider` 这个委托（与 `PoolRuntimeHost` 同一套办法）。
        /// </summary>
        private static ILayaRuntime ResolveService()
        {
            ILayaRuntime service = LayaRuntimeHost.Current;
            if (service != null)
                return service;

            Func<ILayaRuntime> provider = LayaRuntimeHost.Provider;
            return provider != null ? provider() : null;
        }

        protected override BtResult OnExecute(BtContext context)
        {
            LastErrorCode = null;
            LastError = null;
            LastSummary = null;

            ILayaRuntime service = ResolveService();
            if (service == null)
            {
                // ⚠️ 这条路**必须**走 Unavailable，不能直接 Fail：服务没起来也是"拿不到答案"的一种，
                //    要按节点自己的 `onUnavailable` 处置（D10：默认 fail，可配 default/keep）。
                //    直接 Fail 会让"配了 default 的节点"在服务未初始化时依然整节点失败（实机踩过）。
                return Unavailable(context, "laya_unavailable", "Laya service is not initialized");
            }

            string bankName = ResolveBankName(context, out string nameError);
            if (bankName == null)
                return Fail(context, "laya_no_bank", nameError);

            QuestionBank bank;
            if (!service.TryResolveBank(bankName, Only, out bank, out string bankError))
                return Fail(context, "laya_no_bank", bankError);

            string digest = service.CompileDigest(context, out string digestError);
            if (digest == null)
                return Fail(context, "laya_no_state", digestError);

            // **上线状态是空的就别问**（A56）。
            //
            // 实测（2026-09-26）：把"什么都没有"当输入喂给它时，模型会答 `danger` / `mine` / `fight`
            // ——**无中生有的警报**。而"完全良性"的角色编译出来的上线状态恰恰可能是空的
            // （生命/饥饿/困倦/体温/潮湿/体力全在正常档，且没瞄任何东西 —— 见 `WireInclude`）。
            // 于是最常见的"没事干"场景会得到最危险的动作。
            // 处理方式**不新造分支**：走既有的降级链（`onUnavailable`），
            // 出厂 demo 用的是 `keep` → 保留上次目标；首次就没有目标时落到 `no_goal` → 待机。
            if (digest.Trim().Length == 0)
            {
                context.Log("LayaAsk: state is empty (everything normal, nothing aimed at) -> not asking");
                return Unavailable(context, "laya_quiet_state",
                    "the state has nothing worth asking about (all vitals normal, nothing aimed at)");
            }

            string fingerprint = LayaClient.Fingerprint(digest, bank, service.Config.Model);
            string note;
            LayaAnswer immediate = service.Client.Ask(fingerprint, digest, bank, out note);
            m_fingerprint = fingerprint;
            m_pending = immediate == null;   // null = 发出去了（或去重命中）；非 null = 立刻有结果/立刻失败

            if (immediate != null)
            {
                m_result = immediate;
                m_pending = false;
            }
            else if (!service.Client.Busy)
            {
                // Ask 返回 null 且没有在途请求 = 这次没发出去（禁用/去重命中/同题在途）
                m_result = new LayaAnswer { Ok = false, ErrorCode = "laya_not_sent", Error = note, Fingerprint = fingerprint };
                m_pending = false;
            }

            context.Log("LayaAsk: fingerprint=" + (fingerprint ?? "?").Substring(0, Math.Min(8, (fingerprint ?? "?").Length))
                + " bank=" + bankName + " digest=" + digest.Length + "ch - " + note);

            return OnTick(context);
        }

        protected override BtResult OnTick(BtContext context)
        {
            ILayaRuntime service = ResolveService();
            if (service == null)
                return Unavailable(context, "laya_unavailable", "Laya service is not initialized");

            if (m_pending && m_result == null)
            {
                LayaAnswer polled = service.Client.Poll(m_fingerprint);
                if (polled != null)
                    m_result = polled;

                int budget = TimeoutMs > 0 ? TimeoutMs : service.Config.TimeoutMs;
                if (m_result == null && ActiveTime * 1000f > budget + 250f)
                {
                    return Unavailable(context, "laya_timeout",
                        "no answer within " + budget + " ms");
                }
                if (m_result == null)
                    return BtResult.InProgress;
            }

            LayaAnswer answer = m_result;
            if (answer == null)
                return Unavailable(context, "laya_no_answer", "no answer and no failure recorded");

            if (!answer.Ok)
                return Unavailable(context, answer.ErrorCode, answer.Error);

            if (answer.Answers.Count == 0)
                return Unavailable(context, "laya_no_answers", "the service returned no usable answers");

            LastSummary = answer.Summarize();
            List<AnswerBinding> bindings = ParseBindings(AnswerKeys, out string bindingError);
            if (bindings.Count == 0)
                return Fail(context, "laya_no_answer_keys",
                    bindingError ?? "node has no 'answerKeys' configured");

            for (int i = 0; i < bindings.Count; i++)
            {
                AnswerBinding binding = bindings[i];
                object row;
                if (!answer.Answers.TryGetValue(binding.QuestionId, out row) || row == null)
                {
                    // 缺一个答案就算本节点失败：**绝不猜值写黑板**（§4.7）
                    return Fail(context, "laya_missing_answer",
                        "no answer for question '" + binding.QuestionId + "'");
                }
                string writeError;
                if (!WriteBinding(context, binding, row, out writeError))
                    return Fail(context, "laya_answer_type_mismatch", writeError);
            }

            context.Log("LayaAsk: " + (ResolvedBank ?? "?") + " -> " + LastSummary);
            return BtResult.Succeeded;
        }

        protected override void OnExit(BtContext context, BtResult result)
        {
            // 铁律：结束/中断都不留悬挂的请求（否则上一个问题的答案会落到下一棵子树）
            m_pending = false;
            m_result = null;
        }

        public override void ResetState()
        {
            base.ResetState();
            m_pending = false;
            m_result = null;
            LastErrorCode = null;
            LastError = null;
            LastSummary = null;
        }

        public override string ToString()
        {
            return base.ToString() + " questions=" + (ResolvedBank ?? Questions ?? ("{" + QuestionsKey + "}"))
                + " answers=" + (AnswerKeys ?? "-");
        }

        // ---------------------------------------------------------------- 内部

        /// <summary>当前问题库名（读一次就记住，便于 `ai.status` 显示）。</summary>
        public string ResolvedBank { get; private set; }

        private LayaAnswer m_result;
        private bool m_pending;
        private string m_fingerprint;

        internal sealed class AnswerBinding
        {
            public string BlackboardKey;
            public string QuestionId;
            public string Kind;   // str / bool / int / float
        }

        private string ResolveBankName(BtContext context, out string error)
        {
            error = null;
            if (!string.IsNullOrEmpty(QuestionsKey) && context.Blackboard != null)
            {
                string fromBoard;
                if (context.Blackboard.TryGet<string>(QuestionsKey, out fromBoard)
                    && !string.IsNullOrEmpty(fromBoard))
                {
                    ResolvedBank = fromBoard;
                    return fromBoard;
                }
                if (string.IsNullOrEmpty(Questions))
                {
                    error = "blackboard key '" + QuestionsKey + "' has no question bank name yet";
                    return null;
                }
            }
            if (!string.IsNullOrEmpty(Questions))
            {
                ResolvedBank = Questions;
                return Questions;
            }
            error = "node has neither 'questions' nor 'questionsKey' configured";
            return null;
        }

        /// <summary>`goal:goal:str,danger:threat:bool` → 绑定表。</summary>
        internal static List<AnswerBinding> ParseBindings(string spec, out string error)
        {
            error = null;
            var bindings = new List<AnswerBinding>();
            if (string.IsNullOrEmpty(spec))
                return bindings;

            string[] parts = spec.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0)
                    continue;

                string[] fields = part.Split(':');
                if (fields.Length < 3)
                {
                    error = "answerKeys entry '" + part + "' must be blackboardKey:questionId:type";
                    return new List<AnswerBinding>();
                }

                string kind = fields[2].Trim().ToLowerInvariant();
                if (kind != "str" && kind != "string" && kind != "bool" && kind != "int" && kind != "float")
                {
                    error = "answerKeys entry '" + part + "' has unknown type '" + fields[2]
                        + "' (str / bool / int / float)";
                    return new List<AnswerBinding>();
                }

                bindings.Add(new AnswerBinding
                {
                    BlackboardKey = fields[0].Trim(),
                    QuestionId = fields[1].Trim(),
                    Kind = kind == "string" ? "str" : kind
                });
            }
            return bindings;
        }

        /// <summary>
        /// 把答案写进黑板（**类型按声明解释**，失配就失败 —— 不猜）。
        /// `choice` 写它的 key；`noul` 写 0~1 的浮点（声明 bool 时按 &gt; 0.5 折算）；
        /// `score` 写浮点。**`confidence` 永不写**（§5.4 实测不可用）。
        /// </summary>
        private static bool WriteBinding(BtContext context, AnswerBinding binding, object row, out string error)
        {
            error = null;
            var values = row as Dictionary<string, object>;
            if (values == null || context.Blackboard == null)
            {
                error = "answer row for '" + binding.QuestionId + "' is not an object";
                return false;
            }

            if (values.ContainsKey("choice"))
            {
                string choice = Convert.ToString(values["choice"]);
                if (binding.Kind == "str")
                {
                    context.Blackboard.Set(new AiBlackboardKey<string>(binding.BlackboardKey), choice ?? string.Empty);
                    return true;
                }
                // 非字符串声明：只能接受 yes/no 这类可折算的取值
                if (binding.Kind == "bool")
                {
                    bool value = string.Equals(choice, "yes", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(choice, "true", StringComparison.OrdinalIgnoreCase);
                    context.Blackboard.Set(new AiBlackboardKey<bool>(binding.BlackboardKey), value);
                    return true;
                }
                error = "'" + binding.QuestionId + "' is a choice answer but "
                    + binding.BlackboardKey + " is declared " + binding.Kind;
                return false;
            }

            if (values.ContainsKey("noul"))
            {
                float probability = Convert.ToSingle(values["noul"]);
                if (binding.Kind == "bool")
                {
                    context.Blackboard.Set(new AiBlackboardKey<bool>(binding.BlackboardKey), probability > 0.5f);
                    return true;
                }
                if (binding.Kind == "float")
                {
                    context.Blackboard.Set(new AiBlackboardKey<float>(binding.BlackboardKey), probability);
                    return true;
                }
                error = "'" + binding.QuestionId + "' is a noul answer but "
                    + binding.BlackboardKey + " is declared " + binding.Kind;
                return false;
            }

            if (values.ContainsKey("score"))
            {
                float score = Convert.ToSingle(values["score"]);
                if (binding.Kind == "float")
                {
                    context.Blackboard.Set(new AiBlackboardKey<float>(binding.BlackboardKey), score);
                    return true;
                }
                if (binding.Kind == "int")
                {
                    context.Blackboard.Set(new AiBlackboardKey<int>(binding.BlackboardKey), (int)Math.Round(score));
                    return true;
                }
                error = "'" + binding.QuestionId + "' is a score answer but "
                    + binding.BlackboardKey + " is declared " + binding.Kind;
                return false;
            }

            error = "answer for '" + binding.QuestionId + "' has no choice/noul/score field";
            return false;
        }

        /// <summary>拿不到答案时的处理（默认 `fail`；`keep` = 保留旧值继续，`default` = 写默认值继续）。</summary>
        private BtResult Unavailable(BtContext context, string code, string reason)
        {
            string mode = string.IsNullOrEmpty(OnUnavailable) ? "fail" : OnUnavailable.Trim().ToLowerInvariant();
            LastErrorCode = code;
            LastError = reason;

            if (mode == "keep")
            {
                if (context != null)
                    context.Warn("LayaAsk: " + code + " - " + reason + " -> keeping previous values");
                return BtResult.Succeeded;
            }

            if (mode == "default")
            {
                List<AnswerBinding> bindings = ParseBindings(AnswerKeys, out string _);
                for (int i = 0; i < bindings.Count; i++)
                {
                    AnswerBinding binding = bindings[i];
                    string text = DefaultValue ?? string.Empty;
                    switch (binding.Kind)
                    {
                        case "bool":
                            context.Blackboard.Set(new AiBlackboardKey<bool>(binding.BlackboardKey),
                                string.Equals(text, "true", StringComparison.OrdinalIgnoreCase));
                            break;
                        case "int":
                            int asInt;
                            context.Blackboard.Set(new AiBlackboardKey<int>(binding.BlackboardKey),
                                int.TryParse(text, out asInt) ? asInt : 0);
                            break;
                        case "float":
                            float asFloat;
                            context.Blackboard.Set(new AiBlackboardKey<float>(binding.BlackboardKey),
                                float.TryParse(text, out asFloat) ? asFloat : 0f);
                            break;
                        default:
                            context.Blackboard.Set(new AiBlackboardKey<string>(binding.BlackboardKey), text);
                            break;
                    }
                }
                if (context != null)
                    context.Warn("LayaAsk: " + code + " - " + reason + " -> wrote defaults");
                return BtResult.Succeeded;
            }

            return Fail(context, code, reason);
        }

        private BtResult Fail(BtContext context, string code, string reason)
        {
            LastErrorCode = string.IsNullOrEmpty(code) ? "laya_failed" : code;
            LastError = reason ?? LastErrorCode;
            if (context != null)
                context.Warn("LayaAsk: " + LastErrorCode + " - " + LastError + " -> Failed");

            // 失败标记写给**树**（异常分支提前规划）；不是给模型的输入（plan G22）
            if (WriteFailKey && context != null && context.Blackboard != null)
            {
                if (!string.IsNullOrEmpty(FailKey))
                    context.Blackboard.Set(new AiBlackboardKey<string>(FailKey), LastErrorCode);
                if (!string.IsNullOrEmpty(ReasonKey))
                    context.Blackboard.Set(new AiBlackboardKey<string>(ReasonKey), LastError);
            }
            return BtResult.Failed;
        }
    }
}
