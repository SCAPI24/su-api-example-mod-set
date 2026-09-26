using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>一个"退避时间到了、该再取一次"的资源。</summary>
    public sealed class AssetPendingRetry
    {
        public string Resource;

        /// <summary>最近一次失败的分类。</summary>
        public AssetFailureKind Kind;

        /// <summary>这是哪一类资源（决定重取动作）。</summary>
        public AssetResourceKind ResourceKind;

        /// <summary>第几次重取（从 1 开始）。</summary>
        public int Attempt;

        public string Error;

        /// <summary>这次重取已经等了多久（秒）。</summary>
        public double WaitedSeconds;

        public string Describe()
        {
            return Resource + " (attempt " + Attempt + ", " + AssetFailureClassifier.Label(Kind)
                + ", waited " + WaitedSeconds.ToString("0.00") + "s)";
        }

        public override string ToString()
        {
            return "AssetPendingRetry(" + Describe() + ")";
        }
    }

    /// <summary>
    /// **拒包 / 重取的异常处理**（plan §4.12 的 `AssetRecoveryTask`）：
    /// 四码分流 + 指数退避 + 熔断 + **失败标记**。
    ///
    /// 三条纪律都落在这一层，而不是散在各个调用点：
    ///
    /// 1. **"丢包"与"缺文件"必须分开**（见 <see cref="AssetFailureClassifier"/>）——
    ///    混成一个"重取"会让校验错误也无限重试。
    /// 2. **熔断**：同一资源在窗口期内失败超上限 → `<see cref="AssetFailureKind.Exhausted"/>`，
    ///    停手并提示人；恢复条件是**明确的**（人处置：编辑器保存了新版本、或 `ai.tree.reload`），
    ///    不是"等一会儿再试"。这一条同时防住"Laya 自己都不可用时，自动问题判断也问不出来"的死循环。
    /// 3. **失败标记进黑板、不进摘要**：见 <see cref="AssetFailureDecision.BlackboardMarks"/> 的注释。
    ///
    /// 计时用**内部时钟**（<see cref="Tick"/> 推进），不碰真实时间 —— 于是自检可以在微秒内
    /// 走完"退避 0.25s → 0.5s → 1s → 熔断"的整条时间线。
    /// </summary>
    public sealed class AssetRecoveryTask
    {
        private sealed class ResourceState
        {
            public string Resource;
            public AssetFailureKind Kind;
            public AssetResourceKind ResourceKind;
            public string Error;
            public string Detail;
            public int Attempts;
            public bool Pending;
            public bool Exhausted;
            public double NextAttemptAt;
            public double ScheduledAt;
            public double AttemptInterval;
            public double LastFailureAt;
            public readonly List<double> Window = new List<double>();

            public double SecondsToNext(double clock)
            {
                return NextAttemptAt > clock ? NextAttemptAt - clock : 0.0;
            }
        }

        private readonly Dictionary<string, ResourceState> m_states =
            new Dictionary<string, ResourceState>(StringComparer.OrdinalIgnoreCase);

        private double m_clock;

        public AssetRecoveryTask(AssetRetryPolicy policy = null)
        {
            Policy = policy ?? new AssetRetryPolicy();
            Policy.Validate();
        }

        public AssetRetryPolicy Policy { get; private set; }

        /// <summary>内部时钟（秒）。自检用它把整条退避时间线压缩到一次调用里。</summary>
        public double Clock
        {
            get { return m_clock; }
        }

        /// <summary>当前有没有"在等退避"的资源。</summary>
        public int PendingCount
        {
            get
            {
                int count = 0;
                foreach (KeyValuePair<string, ResourceState> pair in m_states)
                {
                    if (pair.Value.Pending)
                        count++;
                }
                return count;
            }
        }

        /// <summary>熔断了的资源数（`ai.asset.retry` 用它给人看）。</summary>
        public int ExhaustedCount
        {
            get
            {
                int count = 0;
                foreach (KeyValuePair<string, ResourceState> pair in m_states)
                {
                    if (pair.Value.Exhausted)
                        count++;
                }
                return count;
            }
        }

        public void ApplyPolicy(AssetRetryPolicy policy)
        {
            if (policy == null)
                return;
            policy.Validate();
            Policy = policy;
        }

        // ---------------------------------------------------------------- 失败 / 成功

        /// <summary>
        /// **记一次失败**：分流 → 决定还重不重取 → 排入退避队列或熔断。
        /// 调用方拿到 <see cref="AssetFailureDecision"/> 之后负责：写黑板标记、记事件日志。
        ///
        /// <paramref name="kind"/> 给 <see cref="AssetFailureKind.None"/> 时按 <paramref name="error"/>
        /// 的文本分流；调用方**手里有校验报告时应当用
        /// <see cref="AssetFailureClassifier.ClassifyReport"/> 先判好再传进来** ——
        /// 报告的错误码才是权威判据（文本只是兜底）。
        /// </summary>
        public AssetFailureDecision Fail(string resource, string error, string detail = null,
            AssetFailureKind kind = AssetFailureKind.None,
            AssetResourceKind resourceKind = AssetResourceKind.Tree)
        {
            string key = string.IsNullOrEmpty(resource) ? "<unknown>" : resource;
            ResourceState state = Fetch(key);

            if (kind == AssetFailureKind.None)
                kind = AssetFailureClassifier.Classify(error);
            state.Kind = kind;
            state.ResourceKind = resourceKind;
            state.Error = error;
            state.Detail = detail;

            // 熔断窗口：**先**按窗口过滤（这一步顺带处理"安静了一段时间 → 计数清零"），
            // **再**把本次失败记进去 —— 顺序反了的话窗口永远不会变空，
            // "隔了很久再失败一次"会被当成连击。
            PruneWindow(state, key);
            state.Window.Add(m_clock);

            var decision = new AssetFailureDecision
            {
                Resource = key,
                Kind = kind,
                ResourceKind = resourceKind,
                Code = AssetFailureClassifier.CodeOf(kind),
                Detail = detail,
                Error = error,
                MaxAttempts = Policy.MaxAttempts
            };

            if (!AssetFailureClassifier.IsRetryable(kind))
            {
                state.Pending = false;
                decision.Attempt = state.Attempts + 1;
                decision.Reason = AssetFailureClassifier.Describe(kind);
                if (kind == AssetFailureKind.None)
                    decision.Reason = "unclassified failure - refusing to retry blindly";
                return decision;
            }

            if (!Policy.Enabled)
            {
                state.Pending = false;
                decision.Attempt = state.Attempts + 1;
                decision.Reason = "automatic retry is turned off (policy)";
                return decision;
            }

            // 熔断：窗口内失败次数已经到上限 → 停手（重试上限与窗口上限哪个先到都算）
            if (state.Window.Count >= Policy.WindowLimit)
            {
                state.Pending = false;
                state.Exhausted = true;
                decision.Attempt = state.Attempts + 1;
                decision.Exhausted = true;
                decision.Reason = "failed " + state.Window.Count + " times within "
                    + Policy.WindowSeconds.ToString("0") + "s - stopped; a human must act"
                    + " (save a new version, or ai.tree.reload)";
                return decision;
            }

            state.Attempts++;
            decision.Attempt = state.Attempts;

            if (state.Attempts > Policy.MaxAttempts)
            {
                state.Pending = false;
                state.Exhausted = true;
                decision.Exhausted = true;
                decision.Reason = "reached the attempt limit (" + Policy.MaxAttempts + ") - stopped";
                return decision;
            }

            decision.Retry = true;
            decision.DelaySeconds = Policy.DelayFor(kind, state.Attempts);
            decision.Reason = AssetFailureClassifier.Describe(kind);
            state.Pending = true;
            state.ScheduledAt = m_clock;
            state.NextAttemptAt = m_clock + decision.DelaySeconds;
            return decision;
        }

        /// <summary>取用成功 → 这个资源的历史一笔勾销（含熔断）。</summary>
        public void Succeed(string resource)
        {
            if (string.IsNullOrEmpty(resource))
                return;
            m_states.Remove(resource);
        }

        /// <summary>
        /// **人处置过了**（编辑器保存了新版本 / 手动 `ai.tree.reload` / 手动重取）→ 解除熔断、
        /// 清零次数，但**保留资源名**（下一次失败会重新开始计数）。
        /// 返回 true 表示之前确实处在熔断状态。
        /// </summary>
        public bool Reset(string resource)
        {
            ResourceState state;
            if (string.IsNullOrEmpty(resource) || !m_states.TryGetValue(resource, out state))
                return false;

            bool was = state.Exhausted;
            state.Attempts = 0;
            state.Pending = false;
            state.Exhausted = false;
            state.Window.Clear();
            state.NextAttemptAt = 0.0;
            return was;
        }

        public void ResetAll()
        {
            m_states.Clear();
        }

        public bool IsExhausted(string resource)
        {
            ResourceState state;
            return !string.IsNullOrEmpty(resource) && m_states.TryGetValue(resource, out state)
                && state.Exhausted;
        }

        public bool IsPending(string resource)
        {
            ResourceState state;
            return !string.IsNullOrEmpty(resource) && m_states.TryGetValue(resource, out state)
                && state.Pending;
        }

        // ---------------------------------------------------------------- 推进

        /// <summary>
        /// 推进内部时钟，返回**这一拍退避到期、该再取一次**的资源（可能不止一个）。
        /// 返回即出队（<c>Pending</c> 清掉）：调用方必须接着调 <see cref="Fail"/> 或 <see cref="Succeed"/>，
        /// 否则这个资源就停在"取过一次但没结论"的状态 —— 那正是"看起来卡住"的来源。
        /// </summary>
        public List<AssetPendingRetry> Tick(double deltaTime)
        {
            if (deltaTime > 0.0)
                m_clock += deltaTime;

            var due = new List<AssetPendingRetry>();
            var keys = new List<string>(m_states.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                ResourceState state = m_states[keys[i]];
                if (!state.Pending || state.NextAttemptAt > m_clock)
                    continue;

                state.Pending = false;
                due.Add(new AssetPendingRetry
                {
                    Resource = state.Resource,
                    Kind = state.Kind,
                    ResourceKind = state.ResourceKind,
                    Attempt = state.Attempts,
                    Error = state.Error,
                    WaitedSeconds = m_clock - state.ScheduledAt
                });
            }
            return due;
        }

        /// <summary>还剩多久该重取（秒；没有待重取的资源给 -1）。</summary>
        public double SecondsToNextRetry()
        {
            double best = -1.0;
            foreach (KeyValuePair<string, ResourceState> pair in m_states)
            {
                if (!pair.Value.Pending)
                    continue;
                double remaining = pair.Value.SecondsToNext(m_clock);
                if (best < 0.0 || remaining < best)
                    best = remaining;
            }
            return best;
        }

        // ---------------------------------------------------------------- 观测

        public List<Dictionary<string, object>> DescribeResources()
        {
            var list = new List<Dictionary<string, object>>();
            foreach (KeyValuePair<string, ResourceState> pair in m_states)
            {
                ResourceState state = pair.Value;
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["resource"] = state.Resource,
                    ["assetKind"] = state.ResourceKind.ToString(),
                    ["kind"] = AssetFailureClassifier.Label(state.Kind),
                    ["code"] = AssetFailureClassifier.CodeOf(state.Kind),
                    ["attempts"] = state.Attempts,
                    ["maxAttempts"] = Policy.MaxAttempts,
                    ["pending"] = state.Pending,
                    ["exhausted"] = state.Exhausted,
                    ["nextRetryMs"] = state.Pending
                        ? (int)Math.Round(state.SecondsToNext(m_clock) * 1000.0) : 0,
                    ["failuresInWindow"] = state.Window.Count,
                    ["error"] = state.Error,
                    ["detail"] = state.Detail
                });
            }
            list.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
            {
                return string.CompareOrdinal(Convert.ToString(a["resource"]),
                    Convert.ToString(b["resource"]));
            });
            return list;
        }

        public string Describe()
        {
            return "AssetRecoveryTask(resources=" + m_states.Count + " pending=" + PendingCount
                + " exhausted=" + ExhaustedCount + " retry=" + (Policy.Enabled ? "on" : "off")
                + " max=" + Policy.MaxAttempts + ")";
        }

        public override string ToString()
        {
            return Describe();
        }

        // ---------------------------------------------------------------- 内部

        private ResourceState Fetch(string resource)
        {
            ResourceState state;
            if (!m_states.TryGetValue(resource, out state))
            {
                state = new ResourceState { Resource = resource };
                m_states[resource] = state;
            }
            return state;
        }

        private void PruneWindow(ResourceState state, string resource)
        {
            double cutoff = m_clock - Policy.WindowSeconds;
            if (state.Window.Count == 0)
                return;

            int keep = 0;
            for (int i = 0; i < state.Window.Count; i++)
            {
                if (state.Window[i] >= cutoff)
                    state.Window[keep++] = state.Window[i];
            }
            if (keep < state.Window.Count)
                state.Window.RemoveRange(keep, state.Window.Count - keep);

            // 窗口清空 = 一段时间没再出错 → 允许重新计数（但不解除熔断，那要人处置）
            if (state.Window.Count == 0 && !state.Exhausted)
                state.Attempts = 0;
        }
    }
}
