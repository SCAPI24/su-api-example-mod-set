using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PlayerAiMod
{
    /// <summary>
    /// 一条**判定记录**（P4 复盘的原子单位）：一次"问 Laya"的完整事实。
    ///
    /// 为什么要记这么细（而不是只记"答案是什么"）：§5.4 的实测结论是**摘要本身就是最主要的调参旋钮**
    /// （同一事实换成长摘要，答案会直接翻转；延迟从 428 ms 涨到 1603 ms）。所以复盘必须能回答
    /// "**当时喂进去的那串字到底是什么**、花了多久、多少 token、答案是哪个选项、有没有失败" ——
    /// 少任何一样，事后都无法解释"为什么它当时这么答"。
    /// </summary>
    public sealed class DecisionRecord
    {
        /// <summary>这次判定的**性质**。</summary>
        public const string KindSent = "sent";        // 真的发了 HTTP
        public const string KindCached = "cached";    // 命中同一摘要指纹的去重缓存（没发请求）
        public const string KindRefused = "refused";  // 请求前就被拒（没 key / 超预算 / 服务未启用）

        /// <summary>自增序号（从 1 开始；重启归零）。</summary>
        public long Seq;

        public DateTime Utc;

        public string Kind = KindSent;

        /// <summary>问题库（文件名或 id）。</summary>
        public string Bank;

        /// <summary>本批问了哪些问题（逗号分隔的 id）。</summary>
        public string Questions;

        /// <summary>**当时发出去的原文摘要**（复盘/调参的核心证据）。</summary>
        public string Digest;

        /// <summary>请求指纹（摘要 + 问题库 + 模型）。</summary>
        public string Fingerprint;

        /// <summary>这一问用的**摘要布局版本**（plan G18：改过字段表之后靠它分清新旧样本）。</summary>
        public int DigestVersion;

        /// <summary>这一问用的**问题库内容版本**（库 JSON 里的 `version`）。</summary>
        public int BankVersion;

        /// <summary>问题库**内容哈希**（文件字节哈希；手工构造的库为空）。</summary>
        public string BankHash;

        /// <summary>用的哪个模型（同一台机器上换了模型也要看得出来）。</summary>
        public string Model;

        /// <summary>答案摘要（`goal=craft threat=yes`），失败时是错误码。</summary>
        public string Answer;

        public string ErrorCode;

        public long ElapsedMs;

        public int InputTokens;

        public bool Ok;

        /// <summary>摘要字符数（调参第一眼要看的数）。</summary>
        public int DigestChars
        {
            get { return Digest != null ? Digest.Length : 0; }
        }

        public string Describe()
        {
            return (Kind ?? "?") + " " + (Bank ?? "?") + " digest=" + DigestChars + "ch"
                + (Ok ? " -> " + (Answer ?? "?") : " -> FAILED " + (ErrorCode ?? "?"))
                + " (" + ElapsedMs + "ms/" + InputTokens + "tok)";
        }

        /// <summary>给人/表格看的行（`ai.laya.review` 与编辑器面板共用）。</summary>
        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["seq"] = Seq,
                ["time"] = Utc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                ["kind"] = Kind,
                ["bank"] = Bank,
                ["questions"] = Questions,
                ["digestChars"] = DigestChars,
                ["digest"] = Digest,
                ["digestVersion"] = DigestVersion,
                ["bankVersion"] = BankVersion,
                ["bankHash"] = BankHash,
                ["model"] = Model,
                ["fingerprint"] = Fingerprint,
                ["ok"] = Ok,
                ["answer"] = Answer,
                ["errorCode"] = ErrorCode,
                ["elapsedMs"] = ElapsedMs,
                ["inputTokens"] = InputTokens
            };
        }
    }

    /// <summary>
    /// 判定记录的**内存环 + 聚合**（P4 复盘；plan §5.4 / G9）。
    ///
    /// 三条纪律：
    ///   · **只在内存**（容量可配，默认 64）：它服务于"刚才那几轮判得怎么样"，不是审计日志；
    ///     要长期留痕走 `AiEventLog`（那里的 `[tree]` 行有同一次判定的一行摘要）；
    ///   · **线程安全**：记录发生在后台请求线程（`LayaClient.Run`）与游戏线程（请求前被拒）两处；
    ///   · **聚合只算"真的发了请求"的那些**：缓存命中与请求前被拒不进延迟/ token 均值
    ///     （否则"服务没起来"会污染平均延迟，看着像模型变慢了）。
    /// </summary>
    public sealed class DecisionLog
    {
        private readonly object m_gate = new object();
        private readonly Queue<DecisionRecord> m_records = new Queue<DecisionRecord>();
        private readonly List<long> m_latencies = new List<long>();

        private long m_seq;

        public DecisionLog(int capacity = 64)
        {
            Capacity = capacity > 8 ? capacity : 8;
        }

        public int Capacity { get; }

        /// <summary>累计记录条数（含已被环挤掉的）。</summary>
        public long WriteCount { get; private set; }

        public long SentCount { get; private set; }
        public long CachedCount { get; private set; }
        public long RefusedCount { get; private set; }
        public long FailedCount { get; private set; }
        public long TotalTokens { get; private set; }

        public DecisionRecord Record(string kind, string bank, string questions, string digest,
            string fingerprint, LayaAnswer answer, int digestVersion = 0, int bankVersion = 0,
            string bankHash = null, string model = null)
        {
            var record = new DecisionRecord
            {
                Utc = DateTime.UtcNow,
                Kind = string.IsNullOrEmpty(kind) ? DecisionRecord.KindSent : kind,
                Bank = bank,
                Questions = questions,
                Digest = digest,
                Fingerprint = fingerprint,
                // 版本默认取"当前值"：绝大多数调用点（游戏侧）就是想记"这一问用的版本"，
                // 显式传参只是给自检/离线工具留的口子。
                DigestVersion = digestVersion > 0 ? digestVersion : StateDigestCompiler.Version,
                BankVersion = bankVersion,
                BankHash = bankHash,
                Model = model
            };

            lock (m_gate)
            {
                record.Seq = ++m_seq;
                WriteCount++;

                if (answer != null)
                {
                    record.Ok = answer.Ok;
                    record.ErrorCode = answer.ErrorCode;
                    record.ElapsedMs = answer.ElapsedMs;
                    record.InputTokens = answer.InputTokens;
                    record.Answer = DescribeAnswer(answer);
                }

                switch (record.Kind)
                {
                    case DecisionRecord.KindCached:
                        CachedCount++;
                        break;
                    case DecisionRecord.KindRefused:
                        RefusedCount++;
                        break;
                    default:
                        SentCount++;
                        if (record.ElapsedMs > 0)
                            m_latencies.Add(record.ElapsedMs);
                        if (record.InputTokens > 0)
                            TotalTokens += record.InputTokens;
                        break;
                }

                if (answer != null && !answer.Ok)
                    FailedCount++;

                m_records.Enqueue(record);
                while (m_records.Count > Capacity)
                    m_records.Dequeue();
            }
            return record;
        }

        /// <summary>最近 N 条（新的在后）。`count &lt;= 0` = 全部。</summary>
        public List<DecisionRecord> Recent(int count = 0)
        {
            lock (m_gate)
            {
                var list = new List<DecisionRecord>(m_records);
                if (count <= 0 || count >= list.Count)
                    return list;
                return list.GetRange(list.Count - count, count);
            }
        }

        public void Clear()
        {
            lock (m_gate)
            {
                m_records.Clear();
                m_latencies.Clear();
                WriteCount = 0;
                SentCount = 0;
                CachedCount = 0;
                RefusedCount = 0;
                FailedCount = 0;
                TotalTokens = 0;
                m_seq = 0;
            }
        }

        /// <summary>聚合（`ai.laya.review` 的第一段；控制面/编辑器都读它）。</summary>
        public Dictionary<string, object> Summarize()
        {
            lock (m_gate)
            {
                var sorted = new List<long>(m_latencies);
                sorted.Sort();

                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["records"] = m_records.Count,
                    ["capacity"] = Capacity,
                    ["total"] = WriteCount,
                    ["sent"] = SentCount,
                    ["cached"] = CachedCount,
                    ["refused"] = RefusedCount,
                    ["failed"] = FailedCount,
                    ["inputTokens"] = TotalTokens,
                    ["avgMs"] = sorted.Count > 0 ? (long)Math.Round(Average(sorted)) : 0L,
                    ["p95Ms"] = sorted.Count > 0 ? Percentile(sorted, 0.95) : 0L,
                    ["maxMs"] = sorted.Count > 0 ? sorted[sorted.Count - 1] : 0L,
                    ["minMs"] = sorted.Count > 0 ? sorted[0] : 0L
                };
            }
        }

        /// <summary>
        /// **可复算的抽样表**（markdown）：P4 的验收物 —— 人拿着它就能核对"当时喂了什么、答了什么"。
        /// 带 `v` 列（摘要布局版本 + 库内容版本/哈希短号）：**改完摘要或问题库之后**，
        /// 一眼能看出表里哪几行是旧版本产生的（plan G18）。
        /// </summary>
        public string ToMarkdown(int count = 10)
        {
            List<DecisionRecord> recent = Recent(count);
            var text = new StringBuilder();
            text.Append("| # | time | kind | bank | v | digest(ch) | answer | ms | tokens |").Append('\n');
            text.Append("|---|---|---|---|---|---|---|---|---|").Append('\n');
            for (int i = 0; i < recent.Count; i++)
            {
                DecisionRecord r = recent[i];
                text.Append("| ").Append(r.Seq)
                    .Append(" | ").Append(r.Utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(r.Kind)
                    .Append(" | ").Append(Escape(r.Bank))
                    .Append(" | ").Append(DescribeVersion(r))
                    .Append(" | ").Append(r.DigestChars)
                    .Append(" | ").Append(Escape(r.Ok ? r.Answer : "FAILED:" + r.ErrorCode))
                    .Append(" | ").Append(r.ElapsedMs)
                    .Append(" | ").Append(r.InputTokens)
                    .Append(" |").Append('\n');
            }
            return text.ToString();
        }

        /// <summary>`d1/q1@ab12cd` 这样的版本短号（摘要版本 / 库版本@内容哈希前 6 位）。</summary>
        public static string DescribeVersion(DecisionRecord record)
        {
            if (record == null)
                return "-";
            string hash = record.BankHash;
            if (!string.IsNullOrEmpty(hash) && hash.Length > 6)
                hash = hash.Substring(0, 6);
            return "d" + record.DigestVersion + "/q" + record.BankVersion
                + (string.IsNullOrEmpty(hash) ? string.Empty : "@" + hash);
        }

        /// <summary>
        /// **最近的样本是不是当前版本产生的**（plan G18 的"建议重跑抽样表"）：
        /// 摘要布局版本或库内容哈希与现在不一致 → false。没有记录时返回 true（无从判断）。
        /// </summary>
        public bool SamplesMatchCurrent(int digestVersion, string bankHash)
        {
            List<DecisionRecord> recent = Recent(1);
            if (recent.Count == 0)
                return true;
            DecisionRecord last = recent[0];
            if (last.DigestVersion != digestVersion)
                return false;
            return string.IsNullOrEmpty(bankHash) || string.IsNullOrEmpty(last.BankHash)
                || string.Equals(last.BankHash, bankHash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把一条记录的**原文摘要**单独列出来（调摘要长度时最有用的一屏）。</summary>
        public List<string> RecentDigests(int count = 10)
        {
            List<DecisionRecord> recent = Recent(count);
            var lines = new List<string>();
            for (int i = 0; i < recent.Count; i++)
            {
                DecisionRecord r = recent[i];
                lines.Add("#" + r.Seq + " " + (r.Ok ? "OK" : "FAILED") + " " + DigestVersionOf(r)
                    + " " + r.DigestChars + "ch: " + (r.Digest ?? string.Empty));
            }
            return lines;
        }

        // ---------------------------------------------------------------- 内部

        private static string DigestVersionOf(DecisionRecord record)
        {
            return "v" + DescribeVersion(record);
        }

        private static string DescribeAnswer(LayaAnswer answer)
        {
            if (!answer.Ok)
                return answer.ErrorCode ?? "failed";
            var parts = new List<string>();
            foreach (KeyValuePair<string, object> pair in answer.Answers)
            {
                var row = pair.Value as Dictionary<string, object>;
                string value = null;
                if (row != null)
                {
                    if (row.ContainsKey("choice"))
                        value = Convert.ToString(row["choice"], CultureInfo.InvariantCulture);
                    else if (row.ContainsKey("noul"))
                        value = Convert.ToString(row["noul"], CultureInfo.InvariantCulture);
                    else if (row.ContainsKey("score"))
                        value = Convert.ToString(row["score"], CultureInfo.InvariantCulture);
                }
                parts.Add(pair.Key + "=" + (value ?? "?"));
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(" ", parts.ToArray());
        }

        private static double Average(List<long> sorted)
        {
            if (sorted.Count == 0)
                return 0.0;
            long total = 0;
            for (int i = 0; i < sorted.Count; i++)
                total += sorted[i];
            return (double)total / sorted.Count;
        }

        private static long Percentile(List<long> sorted, double fraction)
        {
            if (sorted.Count == 0)
                return 0;
            int index = (int)Math.Ceiling(fraction * sorted.Count) - 1;
            if (index < 0)
                index = 0;
            if (index >= sorted.Count)
                index = sorted.Count - 1;
            return sorted[index];
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return text.Replace("|", "\\|").Replace("\n", " ").Replace("\r", string.Empty);
        }
    }
}
