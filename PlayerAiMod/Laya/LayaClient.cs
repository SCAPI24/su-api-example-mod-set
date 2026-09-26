using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace PlayerAiMod
{
    /// <summary>一次判定的结果。</summary>
    public sealed class LayaAnswer
    {
        public bool Ok;

        /// <summary>稳定错误码（`laya_no_key` / `laya_unreachable` / `laya_timeout` / `laya_http_401` …）。</summary>
        public string ErrorCode;

        public string Error;

        /// <summary>`问题id → 答案行`（`choice` / `noul` / `score` + 概率）。</summary>
        public readonly Dictionary<string, object> Answers = new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>请求指纹（摘要哈希 + 问题库哈希 + 模型）：结果回来时用它判断"还作不作数"。</summary>
        public string Fingerprint;

        public long ElapsedMs;
        public int InputTokens;

        /// <summary>是否来自去重缓存（没有真的发请求）。</summary>
        public bool FromCache;

        public string Summarize()
        {
            if (!Ok)
                return "laya failed: " + (ErrorCode ?? "?") + " " + (Error ?? string.Empty);
            var parts = new List<string>();
            foreach (KeyValuePair<string, object> pair in Answers)
            {
                var row = pair.Value as Dictionary<string, object>;
                string value = null;
                if (row != null)
                {
                    if (row.ContainsKey("choice"))
                        value = Convert.ToString(row["choice"]);
                    else if (row.ContainsKey("noul"))
                        value = Convert.ToString(row["noul"]);
                    else if (row.ContainsKey("score"))
                        value = Convert.ToString(row["score"]);
                }
                parts.Add(pair.Key + "=" + (value ?? "?"));
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(" ", parts.ToArray())
                + (FromCache ? " (cached)" : " (" + ElapsedMs + "ms/" + InputTokens + "tok)");
        }

        public override string ToString()
        {
            return Summarize();
        }
    }

    /// <summary>
    /// Laya（System One）客户端（plan §5.2）。
    ///
    /// 契约（都是实机/实测换来的）：
    ///   · **不阻塞游戏线程**：请求在**后台线程**发出，结果进队列，由 **帧首** 取用；
    ///   · **在途上限 1**：新请求发出前把旧请求标记为过期（防堆叠、防"答案与状态错位"）；
    ///   · **指纹校验**：结果回来时比对指纹（摘要 + 问题库 + 模型），
    ///     世界切换/模态变了/死了 → 指纹不符 → **丢弃**，不执行；
    ///   · **去重窗口**：同一指纹在 `refreshMs` 内只发一次（选择器每帧重访节点是常态）；
    ///   · **降级链**：超时/不可达/401 各自稳定错误码，交回上层决定兜底（绝不静默当成功）；
    ///   · **密钥永不进日志**（只出现掩码形式）。
    ///
    /// `POST {baseURL}/v1/systemone`，body `{model, state, questions}`，Bearer 鉴权。
    /// **实测注意**：`questions` 必须是**对象**（写成数组会 400）；`state` 超长会被**静默截断**，
    /// 所以发送前必须先跑 <see cref="QuestionBankParser.CheckBudget"/>。
    /// </summary>
    public sealed class LayaClient
    {
        public const string ErrNoKey = "laya_no_key";
        public const string ErrUnreachable = "laya_unreachable";
        public const string ErrTimeout = "laya_timeout";
        public const string ErrHttp = "laya_http_error";
        public const string ErrBadResponse = "laya_bad_response";
        public const string ErrBudget = "laya_over_budget";
        public const string ErrBusy = "laya_busy";

        private sealed class InFlight
        {
            public string Fingerprint;
            public Thread Worker;
            public volatile bool Cancelled;

            /// <summary>发出时刻（`Environment.TickCount64`）—— HUD 的"思考中(0.4s)"要用它。</summary>
            public long StartedMs;
        }

        private sealed class CacheEntry
        {
            public LayaAnswer Answer;
            public DateTime StampUtc;
        }

        private readonly object m_gate = new object();
        private readonly Dictionary<string, CacheEntry> m_cache =
            new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private readonly Queue<LayaAnswer> m_ready = new Queue<LayaAnswer>();

        private LayaConfig m_config;
        private InFlight m_inFlight;
        private long m_generation;

        private int m_requests;
        private int m_cacheHits;
        private int m_droppedStale;
        private int m_failures;

        public LayaClient(LayaConfig config)
        {
            m_config = config ?? new LayaConfig();
            Decisions = new DecisionLog(PlayerAiConfig.DecisionLogCapacity);
        }

        public LayaConfig Config
        {
            get { return m_config; }
        }

        /// <summary>
        /// 判定记录（P4 复盘）：**每一次**"问 Laya"的结果都在这里留一条 ——
        /// 真发请求、命中去重缓存、请求前被拒（没 key / 超预算）三种都记。
        /// 三个记录点分别是 <see cref="Run"/>（后台线程，真实往返）、<see cref="Ask"/> 的缓存命中
        /// 与 <see cref="Ask"/> 的即时失败。
        /// </summary>
        public DecisionLog Decisions { get; }

        /// <summary>换配置（编辑器改完热生效）。会清缓存与在途标记。</summary>
        public void Reload(LayaConfig config)
        {
            lock (m_gate)
            {
                m_config = config ?? new LayaConfig();
                m_cache.Clear();
                m_inFlight = null;
                m_ready.Clear();
                m_generation++;
            }
        }

        /// <summary>
        /// 中断在途请求并清缓存（**人类夺回 / 暂停 / 切换 world** 时调）。
        /// 代次 +1 → 之后回来的答案因代次不匹配被丢弃（plan G8：1 秒后不能又被 AI 接管）。
        /// </summary>
        public void Reset(string reason)
        {
            lock (m_gate)
            {
                m_generation++;
                if (m_inFlight != null)
                    m_inFlight.Cancelled = true;
                m_inFlight = null;
                m_cache.Clear();
                m_ready.Clear();
            }
        }

        /// <summary>是否有在途请求。</summary>
        public bool Busy
        {
            get
            {
                lock (m_gate)
                    return m_inFlight != null;
            }
        }

        /// <summary>
        /// 发起一次判定。返回 null 表示**这次没发**（在途/去重命中/预算不过/未启用），
        /// 原因在 <paramref name="note"/>。真正的结果通过 <see cref="Poll"/> 取。
        /// </summary>
        public LayaAnswer Ask(string fingerprint, string digest, QuestionBank bank, out string note)
        {
            note = null;
            LayaConfig config;
            lock (m_gate)
                config = m_config;

            if (config == null || !config.Enabled)
            {
                note = "laya is disabled in the configuration";
                return null;
            }
            if (!config.HasKey)
            {
                note = ErrNoKey + ": no API key (set " + LayaConfig.EnvApiKey + " or "
                    + "PlayerAi/" + LayaConfig.ConfigFileName + ")";
                LayaAnswer noKey = Fail(ErrNoKey, note, fingerprint);
                Decisions.Record(DecisionRecord.KindRefused, bank != null ? bank.Id : null,
                    QuestionIds(bank), digest, fingerprint, noKey,
                    StateDigestCompiler.Version, bank != null ? bank.Version : 0,
                    bank != null ? bank.SourceHash : null, config.Model);
                return noKey;
            }
            if (bank == null || bank.Questions.Count == 0)
            {
                note = "no questions to ask";
                return null;
            }

            List<string> issues = QuestionBankParser.CheckBudget(bank, digest,
                config.HeadMaxLen, config.MaxLen);
            if (issues.Count > 0)
            {
                note = ErrBudget + ": " + QuestionBankParser.DescribeIssues(issues);
                LayaAnswer overBudget = Fail(ErrBudget, note, fingerprint);
                Decisions.Record(DecisionRecord.KindRefused, bank.Id, QuestionIds(bank), digest,
                    fingerprint, overBudget, StateDigestCompiler.Version, bank.Version,
                    bank.SourceHash, config.Model);
                return overBudget;
            }

            CacheEntry cached = null;
            lock (m_gate)
            {
                // 去重：同一指纹在窗口内只发一次
                CacheEntry found;
                if (m_cache.TryGetValue(fingerprint, out found)
                    && (DateTime.UtcNow - found.StampUtc).TotalMilliseconds < config.RefreshMs)
                {
                    m_cacheHits++;
                    note = "deduplicated (same question within " + config.RefreshMs + " ms)";
                    cached = found;
                }
            }

            if (cached != null)
            {
                // 命中去重缓存也是一次判定（只是没花时间/没花 token）——复盘要看得见"省了多少次"
                Decisions.Record(DecisionRecord.KindCached, bank != null ? bank.Id : null,
                    QuestionIds(bank), digest, fingerprint, cached.Answer,
                    StateDigestCompiler.Version, bank != null ? bank.Version : 0,
                    bank != null ? bank.SourceHash : null, config.Model);
                return cached.Answer;
            }

            lock (m_gate)
            {
                // 在途上限 1：旧的标记过期（它的结果回来会被丢掉）
                if (m_inFlight != null)
                {
                    if (string.Equals(m_inFlight.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        note = ErrBusy + ": the same question is already in flight";
                        return null;
                    }

                    m_inFlight.Cancelled = true;
                    m_inFlight = null;
                    m_droppedStale++;
                }

                long generation = m_generation;
                var flight = new InFlight
                {
                    Fingerprint = fingerprint,
                    StartedMs = Environment.TickCount64
                };
                m_inFlight = flight;
                m_requests++;

                var worker = new Thread(() => Run(flight, generation, config, digest, bank))
                {
                    IsBackground = true,
                    Name = "laya-ask"
                };
                flight.Worker = worker;
                worker.Start();
            }

            note = "request sent";
            return null;
        }

        /// <summary>
        /// 帧首取结果（非阻塞）。返回 null = 还没有结果。
        /// **同时做指纹校验**：已经过期的结果直接丢弃并计数。
        /// </summary>
        public LayaAnswer Poll(string currentFingerprint)
        {
            lock (m_gate)
            {
                if (m_ready.Count == 0)
                    return null;

                LayaAnswer answer = m_ready.Dequeue();
                if (!string.IsNullOrEmpty(currentFingerprint)
                    && !string.Equals(answer.Fingerprint, currentFingerprint, StringComparison.Ordinal))
                {
                    // 世界切换/模态变了/死了 → 这个答案已经不作数
                    m_droppedStale++;
                    return null;
                }
                return answer;
            }
        }

        /// <summary>现状（给 `ai.laya.status`；**不含密钥本体**）。</summary>
        public Dictionary<string, object> Describe()
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            lock (m_gate)
            {
                info["config"] = m_config != null ? m_config.Describe() : null;
                info["busy"] = m_inFlight != null;
                info["inFlight"] = m_inFlight != null ? m_inFlight.Fingerprint : null;
                // 这一趟已经等了多久（HUD 状态行要用；0 = 没有在途请求）。
                info["inFlightMs"] = m_inFlight != null
                    ? Math.Max(0, Environment.TickCount64 - m_inFlight.StartedMs)
                    : 0L;
                info["cachedAnswers"] = m_cache.Count;
                info["readyAnswers"] = m_ready.Count;
                info["requests"] = m_requests;
                info["cacheHits"] = m_cacheHits;
                info["droppedStale"] = m_droppedStale;
                info["failures"] = m_failures;
                info["generation"] = m_generation;
            }
            return info;
        }

        // ---------------------------------------------------------------- 后台线程

        private void Run(InFlight flight, long generation, LayaConfig config, string digest, QuestionBank bank)
        {
            LayaAnswer answer;
            try
            {
                answer = Send(config, digest, bank, flight.Fingerprint);
            }
            catch (Exception exception)
            {
                answer = Fail(ErrUnreachable, exception.GetType().Name + ": " + exception.Message,
                    flight.Fingerprint);
            }

            lock (m_gate)
            {
                // 过期（被更新的请求顶掉 / 被 Reset 打断 / 代次变了）→ 丢弃
                bool stale = flight.Cancelled || generation != m_generation;
                if (stale)
                {
                    m_droppedStale++;
                    if (ReferenceEquals(m_inFlight, flight))
                        m_inFlight = null;
                    return;
                }

                if (ReferenceEquals(m_inFlight, flight))
                    m_inFlight = null;

                if (answer.Ok)
                {
                    m_cache[answer.Fingerprint] = new CacheEntry
                    {
                        Answer = answer,
                        StampUtc = DateTime.UtcNow
                    };
                }
                else
                {
                    m_failures++;
                }
                m_ready.Enqueue(answer);

                // 记一条判定（P4 复盘）。放在锁内是因为"过期的那些"在上面已经 return 掉了 ——
                // 过期答案没被任何树消费，记进去只会污染延迟与成功率。
                // `DecisionLog` 自带走自己的锁且**从不回调本类**，不存在锁序问题。
                Decisions.Record(DecisionRecord.KindSent, bank != null ? bank.Id : null,
                    QuestionIds(bank), digest, flight.Fingerprint, answer,
                    StateDigestCompiler.Version, bank != null ? bank.Version : 0,
                    bank != null ? bank.SourceHash : null, config != null ? config.Model : null);
            }
        }

        /// <summary>本批问了哪些问题（复盘表里一眼能看出"这一问覆盖了什么"）。</summary>
        private static string QuestionIds(QuestionBank bank)
        {
            if (bank == null || bank.Questions.Count == 0)
                return null;
            var ids = new List<string>();
            for (int i = 0; i < bank.Questions.Count; i++)
                ids.Add(bank.Questions[i].Id);
            return string.Join(",", ids.ToArray());
        }

        /// <summary>真正发 HTTP（只有这一处碰网络）。</summary>
        private static LayaAnswer Send(LayaConfig config, string digest, QuestionBank bank, string fingerprint)
        {
            var answer = new LayaAnswer { Fingerprint = fingerprint };
            long started = Environment.TickCount64;

            var body = new StringBuilder();
            body.Append("{\"model\":").Append(Json(config.Model));
            body.Append(",\"state\":").Append(Json(digest));
            body.Append(",\"questions\":").Append(QuestionsJson(bank));
            body.Append('}');

            var request = (HttpWebRequest)WebRequest.Create(config.SystemOneUrl);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Headers["Authorization"] = "Bearer " + (config.ApiKey ?? string.Empty);
            request.Timeout = config.TimeoutMs;
            request.ReadWriteTimeout = config.TimeoutMs;
            request.Proxy = null; // 本机回环，别走系统代理（否则可能被代理拦成不可达）

            byte[] payload = new UTF8Encoding(false).GetBytes(body.ToString());
            request.ContentLength = payload.Length;
            using (Stream stream = request.GetRequestStream())
                stream.Write(payload, 0, payload.Length);

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string text = reader.ReadToEnd();
                    answer.ElapsedMs = Environment.TickCount64 - started;
                    ParseInto(answer, text);
                    if (!answer.Ok)
                    {
                        answer.ErrorCode = ErrBadResponse;
                        answer.Error = "response had no usable answers object";
                    }
                    return answer;
                }
            }
            catch (WebException exception)
            {
                answer.ElapsedMs = Environment.TickCount64 - started;
                var http = exception.Response as HttpWebResponse;
                if (http != null)
                {
                    int status = (int)http.StatusCode;
                    answer.ErrorCode = ErrHttp + "_" + status;
                    string detail = null;
                    try
                    {
                        using (var reader = new StreamReader(http.GetResponseStream(), Encoding.UTF8))
                            detail = reader.ReadToEnd();
                    }
                    catch (Exception)
                    {
                    }
                    answer.Error = "HTTP " + status
                        + (status == 401 || status == 403 ? " (check the API key)" : string.Empty)
                        + (string.IsNullOrEmpty(detail) ? string.Empty : " - " + Truncate(detail, 200));
                    return answer;
                }
                if (exception.Status == WebExceptionStatus.Timeout)
                {
                    answer.ErrorCode = ErrTimeout;
                    answer.Error = "no response within " + config.TimeoutMs + " ms from " + config.BaseUrl;
                    return answer;
                }
                answer.ErrorCode = ErrUnreachable;
                answer.Error = "cannot reach " + config.SystemOneUrl + " - " + exception.Message;
                return answer;
            }
        }

        private static void ParseInto(LayaAnswer answer, string text)
        {
            try
            {
                var report = new PackageReport();
                PackageValue root;
                if (!PackageJson.TryParse(text, "laya", report, out root) || root == null || !root.IsObject)
                    return;

                PackageValue answers = root.Get("answers");
                if (!answers.IsObject)
                    return;

                foreach (string id in answers.MemberNames)
                {
                    PackageValue row = answers.Get(id);
                    if (row == null || !row.IsObject)
                        continue;

                    var parsed = new Dictionary<string, object>(StringComparer.Ordinal);
                    string type = row.Get("type").AsString(null);
                    if (!string.IsNullOrEmpty(type))
                        parsed["type"] = type;

                    // 只取"能用"的字段：confidence 明确不取（实测随上下文长度饱和，不是可信度）
                    if (row.Get("choice").IsString)
                        parsed["choice"] = row.Get("choice").AsString(null);
                    if (row.Get("noul").IsNumber)
                        parsed["noul"] = (float)row.Get("noul").AsNumber();
                    if (row.Get("score").IsNumber)
                        parsed["score"] = (float)row.Get("score").AsNumber();

                    PackageValue probabilities = row.Get("probabilities");
                    if (probabilities.IsObject)
                    {
                        var distribution = new Dictionary<string, object>(StringComparer.Ordinal);
                        foreach (string key in probabilities.MemberNames)
                            distribution[key] = (float)probabilities.Get(key).AsNumber();
                        parsed["probabilities"] = distribution;
                    }

                    answer.Answers[id] = parsed;
                }

                PackageValue usage = root.Get("usage");
                if (usage.IsObject)
                    answer.InputTokens = usage.Get("input_tokens").AsInt(0);
                answer.Ok = answer.Answers.Count > 0;
            }
            catch (Exception exception)
            {
                answer.Ok = false;
                answer.Error = exception.GetType().Name + ": " + exception.Message;
            }
        }

        private LayaAnswer Fail(string code, string error, string fingerprint)
        {
            lock (m_gate)
                m_failures++;
            return new LayaAnswer
            {
                Ok = false,
                ErrorCode = code,
                Error = error,
                Fingerprint = fingerprint
            };
        }

        // ---------------------------------------------------------------- JSON 拼装

        private static string Json(string value)
        {
            if (value == null)
                return "null";
            var text = new StringBuilder(value.Length + 8);
            text.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            text.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            text.Append(c);
                        break;
                }
            }
            text.Append('"');
            return text.ToString();
        }

        private static string QuestionsJson(QuestionBank bank)
        {
            var text = new StringBuilder();
            text.Append('{');
            for (int i = 0; i < bank.Questions.Count; i++)
            {
                QuestionTemplate question = bank.Questions[i];
                if (i > 0)
                    text.Append(',');

                string type = string.IsNullOrEmpty(question.Type) ? "choice" : question.Type.ToLowerInvariant();
                text.Append(Json(question.Id)).Append(":{");
                text.Append("\"type\":").Append(Json(type)).Append(',');
                text.Append("\"instructions\":").Append(Json(question.Instructions ?? question.Id));

                if (question.IsChoice)
                {
                    text.Append(",\"criteria\":{");
                    for (int o = 0; o < question.Options.Count; o++)
                    {
                        if (o > 0)
                            text.Append(',');
                        text.Append(Json(question.Options[o].Key)).Append(':')
                            .Append(Json(question.Options[o].Description));
                    }
                    text.Append('}');
                }
                else if (question.IsScore)
                {
                    text.Append(",\"criteria\":[");
                    for (int o = 0; o < question.Options.Count; o++)
                    {
                        if (o > 0)
                            text.Append(',');
                        text.Append(Json(question.Options[o].Description ?? question.Options[o].Key));
                    }
                    text.Append(']');
                }
                else if (!string.IsNullOrEmpty(question.TrueCriteria) || !string.IsNullOrEmpty(question.FalseCriteria))
                {
                    text.Append(",\"criteria\":{\"true\":").Append(Json(question.TrueCriteria))
                        .Append(",\"false\":").Append(Json(question.FalseCriteria)).Append('}');
                }

                text.Append('}');
            }
            text.Append('}');
            return text.ToString();
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
                return text;
            return text.Substring(0, max) + "…";
        }

        /// <summary>
        /// 指纹：**模型 + 摘要（含布局版本）+ 问题库身份与内容**（同一指纹 = 同一题面）。
        ///
        /// 为什么把"库内容哈希"也算进来（plan G18）：选项**描述**改了，判准就变了 ——
        /// 只按 id 做指纹的话，改完描述之后 `RefreshMs` 窗口内的旧答案会被当成"同一题"复用，
        /// 复盘时也分不清某个样本是改前还是改后产生的。`SourceHash` 是文件内容哈希（有则用），
        /// 没有（手工构造的库）就退化成 id + 选项 key 串。
        /// </summary>
        public static string Fingerprint(string digest, QuestionBank bank, string model)
        {
            var text = new StringBuilder();
            text.Append(model ?? "?").Append('|').Append(StateDigestCompiler.Version).Append('|')
                .Append(digest ?? string.Empty).Append('|');
            if (bank != null)
            {
                text.Append(bank.Id ?? "?").Append('#').Append(bank.Version).Append('#');
                if (!string.IsNullOrEmpty(bank.SourceHash))
                {
                    text.Append(bank.SourceHash);
                }
                // **问的是哪几问也要进指纹**：`bank.SourceHash` 是**整个文件**的哈希，
                // 而 `only=` 取的是它的子集 —— 两个节点问同一个库的不同子集时，
                // 只按文件哈希算出来的指纹**一模一样** → 缓存里互相顶掉、或拿到对方的答案
                // （对方没问的那几问在答案里不存在，节点只会看到"拿不到"）。
                // 实测背景：A54 修好之前 `only` 只生效第一次，所以这个碰撞一直没显形；
                // 修好之后"按子集取库"成了常态，就必须把子集也算进去。
                // 顺序也一并写进去：同一批问题换个顺序 = 另一种请求形态，不该复用答案。
                for (int i = 0; i < bank.Questions.Count; i++)
                {
                    text.Append(bank.Questions[i].Id).Append(':');
                    for (int o = 0; o < bank.Questions[i].Options.Count; o++)
                        text.Append(bank.Questions[i].Options[o].Key).Append(',');
                    text.Append(';');
                }
            }
            return PackageLoader.ComputeHash(new UTF8Encoding(false).GetBytes(text.ToString()));
        }

        /// <summary>
        /// 同步问一次（**阻塞**；只给测试/离线工具用，游戏里必须用 <see cref="Ask"/>）。
        /// </summary>
        public LayaAnswer AskSync(string digest, QuestionBank bank)
        {
            string fingerprint = Fingerprint(digest, bank, m_config != null ? m_config.Model : null);
            if (m_config == null || !m_config.HasKey)
                return Fail(ErrNoKey, ErrNoKey + ": no API key", fingerprint);
            List<string> issues = QuestionBankParser.CheckBudget(bank, digest,
                m_config.HeadMaxLen, m_config.MaxLen);
            if (issues.Count > 0)
                return Fail(ErrBudget, QuestionBankParser.DescribeIssues(issues), fingerprint);
            return Send(m_config, digest, bank, fingerprint);
        }
    }
}
