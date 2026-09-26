using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// **资源取用失败的四种分流**（plan §4.12）。
    ///
    /// 为什么必须先分流：把"丢包"和"缺文件"混成一个"重取"，结果就是**校验错误也无限重试**
    /// （包永远编不过，重取一万次也编不过，只会把日志刷爆、把节点卡死）。
    /// 四种码的处置完全不同：
    ///
    /// | 码 | 含义 | 重取？ | 退避 |
    /// |---|---|---|---|
    /// | <see cref="WriteInProgress"/> | 文件正在被原子替换（读到半截 / 存在 `.tmp`） | ✅ 立刻 | **短**退避 |
    /// | <see cref="Missing"/> | 文件确实不存在 | ✅ 有上限 | **指数**退避 |
    /// | <see cref="Invalid"/> | 校验不过（JSON/结构/引用/类型/选项闭集） | ❌ | — |
    /// | <see cref="LayaUnavailable"/> | Laya 服务/密钥/端点问题 | ❌ | 走 `onUnavailable` 降级链 |
    ///
    /// 熔断（<see cref="Exhausted"/>）不是第五种"失败原因"，而是**处置结论**：
    /// 同一资源在窗口期内失败次数超上限 → 停手并提示人，**不进入自动重试循环**。
    /// </summary>
    public enum AssetFailureKind
    {
        /// <summary>没识别出来（不该出现在"可重取"的判定里；一律按不可重取处理）。</summary>
        None,

        /// <summary>文件正在被原子替换。</summary>
        WriteInProgress,

        /// <summary>文件不存在（或问题库文件缺失）。</summary>
        Missing,

        /// <summary>校验不过 —— **永不重取**。</summary>
        Invalid,

        /// <summary>Laya 服务/密钥/端点问题 —— 是基础设施问题，重取没有意义。</summary>
        LayaUnavailable,

        /// <summary>窗口内失败次数超上限：停手，等人处置。</summary>
        Exhausted
    }

    /// <summary>
    /// **重取的是哪一类资源**（决定"再取一次"具体做什么）。
    ///
    /// 与 <see cref="AssetFailureKind"/> 是两回事：那个说"**怎么坏的**"，这个说"**坏的什么**"。
    /// 分流只依赖前者；重取动作依赖后者（树包＝重装树、问题库＝重新解析 `.qbank`、
    /// 动作包/脚本＝重新读取并校验）。
    /// </summary>
    public enum AssetResourceKind
    {
        /// <summary>行为树包（`.scbtpak`）：重取＝让重载器再装一次。</summary>
        Tree,

        /// <summary>问题库（`.qbank`）：重取＝让 Laya 服务重新解析一次（编辑器可能正在写）。</summary>
        QuestionBank,

        /// <summary>动作包（`.scatpak`）：重取＝重新读取并校验。</summary>
        Action,

        /// <summary>动作脚本（`.aeact`）：重取＝重新读取并校验。</summary>
        Script
    }

    /// <summary>失败标记的**码**（写进黑板/日志的是这些稳定字符串，不是英文句子）。</summary>
    public static class AssetFailureCodes
    {
        public const string Ok = "ok";
        public const string WriteInProgress = "pkg.writeInProgress";
        public const string Missing = "pkg.missing";
        public const string Invalid = "pkg.invalid";
        public const string LayaUnavailable = "laya.unavailable";
        public const string Exhausted = "pkg.exhausted";
        public const string Unknown = "pkg.unknown";

        /// <summary>失败标记写进黑板的键（树里的 `Blackboard` 装饰器读它们走异常子树）。</summary>
        public const string FailKey = "pkg.fail";

        /// <summary>`2/3` 这种"第几次/上限"。</summary>
        public const string RetryKey = "pkg.retry";

        /// <summary>`retrying` / `exhausted` / `fatal`。</summary>
        public const string StateKey = "pkg.state";

        /// <summary>距下一次重取的毫秒数（0 表示立刻 / 不再重取）。</summary>
        public const string NextKey = "pkg.nextMs";

        public static readonly string[] AllKeys = { FailKey, RetryKey, StateKey, NextKey };
    }

    /// <summary>
    /// **把一段错误文本分流成四种码**。
    ///
    /// 判据是**我们自己的装载器/校验器已经写出来的稳定标记**（`PackageCodes` 的 `file.missing`
    /// 那一族、`LayaClient` 的 `laya_*` 码、`PackageWriter` 的 `.tmp` 原子写），
    /// **不是**去猜英文句子的意思 —— 猜句子会在改一句措辞之后就静默失准。
    ///
    /// 顺序很重要：先判"正在写"（它是可重取的短退避），再判 Laya（基础设施），
    /// 再判"确实不存在"，最后才落到"校验不过"。
    /// </summary>
    public static class AssetFailureClassifier
    {
        /// <summary>
        /// **从校验报告的错误码分流**（首选路径）。
        ///
        /// ⚠️ 为什么不能拿 `PackageReport.Summary()` 当输入：它返回的是
        /// **`"1 error(s), 0 warning(s)"`** —— 只有计数、**没有错误码**！
        /// 拿它去分流，"缺文件"会被判成"校验不过"，于是**该重取的永远不重取**（实机踩过）。
        /// 正确入口是 `HasCode(...)`（稳定码）或 `Summarize(...)`（逐条带码的文本）。
        /// </summary>
        public static AssetFailureKind ClassifyReport(PackageReport report)
        {
            if (report == null)
                return AssetFailureKind.None;

            if (report.HasCode(PackageCodes.FileMissing) || report.HasCode(PackageCodes.FileUnreadable))
                return AssetFailureKind.Missing;

            if (report.ErrorCount > 0)
                return AssetFailureKind.Invalid;

            return AssetFailureKind.None;
        }

        /// <summary>报告的**逐条文本**（带码），用来喂文本分流与 `pkg.fail` 的细节。</summary>
        public static string TextOf(PackageReport report, int maxLines = 4)
        {
            if (report == null || report.IsEmpty)
                return null;
            List<string> lines = report.Summarize(maxLines);
            return lines.Count > 0 ? string.Join(" | ", lines.ToArray()) : null;
        }

        /// <summary>报告第一条错误的"位置"（给 `pkg.fail` 当细节；没错误给 null）。</summary>
        public static string DetailOf(PackageReport report)
        {
            PackageIssue first = report != null ? report.FirstError : null;
            if (first == null)
                return null;
            return string.IsNullOrEmpty(first.Where) ? first.Code : (first.Code + " " + first.Where);
        }

        public static AssetFailureKind Classify(string error)
        {
            if (string.IsNullOrEmpty(error))
                return AssetFailureKind.None;

            string text = error;

            // 1) 正在被原子替换（读到半截 / .tmp 还在）—— 这类必须最先判：
            //    它的错误文本里往往同时含 "missing"（文件暂时不在），落到别的分支就永远不重取了。
            if (Contains(text, PackageWriter.TemporarySuffix)
                || Contains(text, "write_in_progress")
                || Contains(text, "writeInProgress")
                || Contains(text, "half-written")
                || Contains(text, "half written")
                || Contains(text, "being replaced")
                || Contains(text, "does not match its hash"))
            {
                return AssetFailureKind.WriteInProgress;
            }

            // 2) Laya 基础设施问题（密钥/端点/超时/401）
            if (StartsWith(text, "laya")
                || Contains(text, "laya_no_key")
                || Contains(text, "laya_unreachable")
                || Contains(text, "laya_timeout")
                || Contains(text, "laya_http"))
            {
                return AssetFailureKind.LayaUnavailable;
            }

            // 3) 确实不存在。注意 `manifest.missing` / `tree.missing` / `zip.entryMissing`
            //    是**结构错误**（文件在、里面缺东西）→ 属于"校验不过"，所以先排掉它们。
            if (Contains(text, PackageCodes.ManifestMissing)
                || Contains(text, PackageCodes.TreeMissing)
                || Contains(text, PackageCodes.ZipEntryMissing))
            {
                return AssetFailureKind.Invalid;
            }
            if (Contains(text, PackageCodes.FileMissing)
                || Contains(text, "not found")
                || Contains(text, "no such file")
                || Contains(text, "does not exist")
                || Contains(text, "cannot find"))
            {
                return AssetFailureKind.Missing;
            }

            // 4) 其余一律按"校验不过"（含 json.invalid / zip.invalid / compile.failed /
            //    type.* / property.* / reference.*）—— 宁可不重取，也不要把编译错误重试到天上。
            return AssetFailureKind.Invalid;
        }

        /// <summary>这一类失败该不该自动重取。</summary>
        public static bool IsRetryable(AssetFailureKind kind)
        {
            return kind == AssetFailureKind.WriteInProgress || kind == AssetFailureKind.Missing;
        }

        public static string CodeOf(AssetFailureKind kind)
        {
            switch (kind)
            {
                case AssetFailureKind.WriteInProgress: return AssetFailureCodes.WriteInProgress;
                case AssetFailureKind.Missing: return AssetFailureCodes.Missing;
                case AssetFailureKind.Invalid: return AssetFailureCodes.Invalid;
                case AssetFailureKind.LayaUnavailable: return AssetFailureCodes.LayaUnavailable;
                case AssetFailureKind.Exhausted: return AssetFailureCodes.Exhausted;
                case AssetFailureKind.None: return AssetFailureCodes.Unknown;
                default: return AssetFailureCodes.Unknown;
            }
        }

        /// <summary>给日志与人看的一句话（**必须说清归谁管**）。</summary>
        public static string Describe(AssetFailureKind kind)
        {
            switch (kind)
            {
                case AssetFailureKind.WriteInProgress:
                    return "the file is being replaced right now (atomic write in progress) - retry shortly";
                case AssetFailureKind.Missing:
                    return "the file does not exist - retry with backoff, up to the attempt limit";
                case AssetFailureKind.Invalid:
                    return "the file does not validate - NOT retried (retrying cannot fix a bad file)";
                case AssetFailureKind.LayaUnavailable:
                    return "the Laya service is unavailable - NOT retried (use the onUnavailable chain)";
                case AssetFailureKind.Exhausted:
                    return "too many failures in the window - stopped, waiting for a human";
                default:
                    return "unclassified failure";
            }
        }

        /// <summary>人话标签（编辑器/命令里显示）。</summary>
        public static string Label(AssetFailureKind kind)
        {
            switch (kind)
            {
                case AssetFailureKind.WriteInProgress: return "write-in-progress";
                case AssetFailureKind.Missing: return "missing";
                case AssetFailureKind.Invalid: return "invalid";
                case AssetFailureKind.LayaUnavailable: return "laya-unavailable";
                case AssetFailureKind.Exhausted: return "exhausted";
                default: return "unknown";
            }
        }

        private static bool Contains(string text, string needle)
        {
            return text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool StartsWith(string text, string needle)
        {
            return text.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>重取策略（D14/D16：一份配置管全部；编辑器里一个总开关）。</summary>
    public sealed class AssetRetryPolicy
    {
        /// <summary>总开关（编辑器里的"自动重取"勾选）。关掉时只分流、不重取。</summary>
        public bool Enabled = true;

        /// <summary>同一资源最多重取几次（**不含第一次**）。</summary>
        public int MaxAttempts = 3;

        /// <summary>指数退避的基数（秒）。</summary>
        public double BaseDelaySeconds = 0.25;

        public double Multiplier = 2.0;

        /// <summary>退避上限（秒）—— 再久也不等，免得"看起来像卡死"。</summary>
        public double MaxDelaySeconds = 4.0;

        /// <summary>"正在被原子替换"的短退避（秒）：这类失败通常几十毫秒后就自愈。</summary>
        public double ImmediateDelaySeconds = 0.1;

        /// <summary>熔断窗口（秒）。</summary>
        public double WindowSeconds = 60.0;

        /// <summary>窗口内同一资源失败到这个次数就熔断（停手、提示人）。</summary>
        public int WindowLimit = 6;

        /// <summary>是否允许走"自动问题判断"（§4.12 的 Laya 模板）；Laya 不可用时**不会**走它。</summary>
        public bool AskLaya = true;

        public void Validate()
        {
            if (MaxAttempts < 0)
                MaxAttempts = 0;
            if (BaseDelaySeconds < 0.0)
                BaseDelaySeconds = 0.0;
            if (Multiplier < 1.0)
                Multiplier = 1.0;
            if (MaxDelaySeconds < 0.0)
                MaxDelaySeconds = 0.0;
            if (MaxDelaySeconds > 0.0 && BaseDelaySeconds > MaxDelaySeconds)
                BaseDelaySeconds = MaxDelaySeconds;
            if (ImmediateDelaySeconds < 0.0)
                ImmediateDelaySeconds = 0.0;
            if (WindowSeconds <= 0.0)
                WindowSeconds = 60.0;
            if (WindowLimit < 1)
                WindowLimit = 1;
            if (WindowLimit < MaxAttempts)
                WindowLimit = MaxAttempts;
        }

        /// <summary>`attempt` 从 1 开始（第几次重取）。</summary>
        public double DelayFor(AssetFailureKind kind, int attempt)
        {
            if (kind == AssetFailureKind.WriteInProgress)
                return ImmediateDelaySeconds;

            int index = attempt > 1 ? attempt - 1 : 0;
            double delay = BaseDelaySeconds;
            for (int i = 0; i < index; i++)
            {
                delay *= Multiplier;
                if (MaxDelaySeconds > 0.0 && delay >= MaxDelaySeconds)
                    return MaxDelaySeconds;
            }
            if (MaxDelaySeconds > 0.0 && delay > MaxDelaySeconds)
                delay = MaxDelaySeconds;
            return delay;
        }

        public Dictionary<string, object> Describe()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["enabled"] = Enabled,
                ["maxAttempts"] = MaxAttempts,
                ["baseDelayMs"] = (int)Math.Round(BaseDelaySeconds * 1000.0),
                ["multiplier"] = Multiplier,
                ["maxDelayMs"] = (int)Math.Round(MaxDelaySeconds * 1000.0),
                ["immediateDelayMs"] = (int)Math.Round(ImmediateDelaySeconds * 1000.0),
                ["windowSeconds"] = WindowSeconds,
                ["windowLimit"] = WindowLimit,
                ["askLaya"] = AskLaya
            };
        }

        public AssetRetryPolicy Clone()
        {
            return new AssetRetryPolicy
            {
                Enabled = Enabled,
                MaxAttempts = MaxAttempts,
                BaseDelaySeconds = BaseDelaySeconds,
                Multiplier = Multiplier,
                MaxDelaySeconds = MaxDelaySeconds,
                ImmediateDelaySeconds = ImmediateDelaySeconds,
                WindowSeconds = WindowSeconds,
                WindowLimit = WindowLimit,
                AskLaya = AskLaya
            };
        }
    }

    /// <summary>一次失败的分流结论：还重不重试、等多久、失败标记写什么。</summary>
    public sealed class AssetFailureDecision
    {
        public string Resource;
        public AssetFailureKind Kind;

        /// <summary>哪一类资源（重取动作据此分派）。</summary>
        public AssetResourceKind ResourceKind;

        public string Code;
        public string Detail;
        public string Error;

        /// <summary>这次失败之后还有没有下一次（true = 已排入退避队列）。</summary>
        public bool Retry;

        /// <summary>这是第几次（含本次）。</summary>
        public int Attempt;

        public int MaxAttempts;

        public double DelaySeconds;

        /// <summary>熔断：窗口内失败次数超上限，停手等人处置。</summary>
        public bool Exhausted;

        /// <summary>为什么这么处置（给人看的一句话）。</summary>
        public string Reason;

        /// <summary>
        /// **失败标记**（feed the tree, not the model）。
        ///
        /// ⚠️ 这些键**绝不进摘要、绝不交给 Laya 判定**：它们是**基础设施状态**，不是游戏世界的状态。
        /// 让模型看"包加载失败"等于让它拿环境噪声做战术决策（§8.5 Non-Goals 的延伸）。
        /// 消费方式是**连线**：树里的 `Blackboard` 装饰器读 `pkg.fail` 走异常子树。
        /// </summary>
        public Dictionary<string, object> BlackboardMarks()
        {
            var marks = new Dictionary<string, object>(StringComparer.Ordinal);
            marks[AssetFailureCodes.FailKey] = string.IsNullOrEmpty(Detail)
                ? Code : (Code + ":" + Detail);
            marks[AssetFailureCodes.StateKey] = StateName();
            if (Retry)
            {
                marks[AssetFailureCodes.RetryKey] = Attempt + "/" + MaxAttempts;
                marks[AssetFailureCodes.NextKey] = (int)Math.Round(DelaySeconds * 1000.0);
            }
            else
            {
                marks[AssetFailureCodes.RetryKey] = string.Empty;
                marks[AssetFailureCodes.NextKey] = 0;
            }
            return marks;
        }

        public string StateName()
        {
            if (Exhausted)
                return "exhausted";
            if (Retry)
                return "retrying";
            return "fatal";
        }

        public string Describe()
        {
            return (Resource ?? "?") + " [" + ResourceKind + "] " + (Code ?? "?") + " -> " + StateName()
                + (Retry ? " attempt=" + Attempt + "/" + MaxAttempts
                    + " in " + (int)Math.Round(DelaySeconds * 1000.0) + "ms" : " (no retry)")
                + (string.IsNullOrEmpty(Reason) ? string.Empty : " : " + Reason);
        }

        public override string ToString()
        {
            return "AssetFailureDecision(" + Describe() + ")";
        }
    }
}
