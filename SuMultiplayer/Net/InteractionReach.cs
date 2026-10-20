using System;
using System.Globalization;
using System.Threading;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 请求者声明的**有效交互距离**（引擎 <c>ComponentMiner.Raycast</c> 的 reach）。
    ///
    /// Source: Survivalcraft/Game/ComponentMiner.cs:378
    ///   <c>reach = (WorldSettings.GameMode == GameMode.Creative) ? SettingsManager.CreativeReach : 5f</c>
    /// <c>SettingsManager.CreativeReach</c> 是**客户端本地可调**设置（默认 7.5）。
    ///
    /// <see cref="HasValue"/> = false 表示旧客户端 / 旧载荷没有这个字段：主机必须按
    /// 该世界模式的默认值（生存 5.0 / 创造 7.5）处理，不能把"缺字段"当成 0。
    /// </summary>
    public readonly struct InteractionReachClaim
    {
        public InteractionReachClaim(bool hasValue, float meters)
        {
            HasValue = hasValue;
            Meters = meters;
        }

        public bool HasValue { get; }
        public float Meters { get; }

        public static InteractionReachClaim Missing => default;

        public string Describe() => HasValue
            ? Meters.ToString("0.##", CultureInfo.InvariantCulture) + " m"
            : "missing";
    }

    /// <summary>
    /// 主机对一次请求的 reach 裁决。
    ///   · <see cref="Valid"/> = false → 声明值畸形（<c>reach-invalid</c>），请求必须拒绝；
    ///   · <see cref="AllowedDistanceMeters"/> = <c>min(effectiveReach + tolerance, serverMax)</c>；
    ///   · <see cref="Clamped"/> = 声明值超过主机上限（已夹到 serverMax，计数后按上限放行）。
    /// </summary>
    public readonly struct InteractionReachDecision
    {
        internal InteractionReachDecision(bool valid, bool usedDefault, bool clamped,
            float effectiveReachMeters, float allowedDistanceMeters, string invalidReason)
        {
            Valid = valid;
            UsedDefault = usedDefault;
            Clamped = clamped;
            EffectiveReachMeters = effectiveReachMeters;
            AllowedDistanceMeters = allowedDistanceMeters;
            InvalidReason = invalidReason;
        }

        public bool Valid { get; }
        public bool UsedDefault { get; }
        public bool Clamped { get; }
        public float EffectiveReachMeters { get; }
        public float AllowedDistanceMeters { get; }
        /// <summary>非空 = <c>reach-invalid: ...</c>（畸形声明，请求必须拒绝）。</summary>
        public string InvalidReason { get; }
    }

    /// <summary>
    /// reach 校验计数（实例状态，随组合根存活；模块 / 引擎适配器共用同一个实例）。
    /// 字段名与状态行一致：reachAwareChecks / reachClampedToServerMax / reachInvalid。
    /// </summary>
    public sealed class InteractionReachCounters
    {
        private long m_checks;
        private long m_clamped;
        private long m_invalid;
        private long m_defaults;

        public long Checks => Interlocked.Read(ref m_checks);
        public long ClampedToServerMax => Interlocked.Read(ref m_clamped);
        public long Invalid => Interlocked.Read(ref m_invalid);
        public long Defaults => Interlocked.Read(ref m_defaults);

        internal void NoteCheck() => Interlocked.Increment(ref m_checks);
        internal void NoteClamped() => Interlocked.Increment(ref m_clamped);
        internal void NoteInvalid() => Interlocked.Increment(ref m_invalid);
        internal void NoteDefault() => Interlocked.Increment(ref m_defaults);

        public string Describe() => "reachAwareChecks=" + Checks +
            " reachClampedToServerMax=" + ClampedToServerMax +
            " reachInvalid=" + Invalid +
            " reachDefault=" + Defaults;
    }

    /// <summary>
    /// 主机侧交互距离校验的**唯一规则**（所有交互共用，不许各写一套常量）。
    ///
    /// 规则（explicit semantics）：
    ///   allowed = min(requesterReach + ToleranceMeters, ServerMaxMeters)
    ///   · requesterReach 取请求带上来的有效 reach；旧客户端缺字段 → 按世界模式默认
    ///     （生存 5.0 / 创造 7.5，和 <c>ComponentMiner.Raycast</c> 同源）；
    ///   · tolerance = 0.5 m：覆盖一个 RTT 的位移 + 身体中心与眼睛位置的差异；
    ///   · serverMax = 主机配置上限（默认 16 m，<c>SyncConfig.MaxInteractionReach</c>）：
    ///     即使客户端声明 500 m，也只按 16 m 放行（计数 reachClampedToServerMax）；
    ///   · 声明值必须是有限数且在 [0.5, 1024] m 内，否则整条请求按 <c>reach-invalid</c>
    ///     拒绝（计数 reachInvalid）——不做"悄悄换默认值"的兜底。
    ///
    /// 非玩家相对的距离检查（区块加载 / 世界边界 / 家具源格跨度）不套用本规则。
    /// </summary>
    public sealed class InteractionReachGate
    {
        /// <summary>生存模式 reach（引擎硬编码 5f）。</summary>
        public const float SurvivalReachMeters = 5f;

        /// <summary>创造模式默认 reach（SettingsManager 默认 7.5f）。</summary>
        public const float DefaultCreativeReachMeters = 7.5f;

        /// <summary>主机上限默认值（m）；可被配置覆盖。</summary>
        public const float DefaultServerMaxMeters = 16f;

        /// <summary>主机上限的允许范围：低于 5 m 会让默认生存交互失效，高于 64 m 失去防作弊意义。</summary>
        public const float MinServerMaxMeters = 5f;
        public const float MaxServerMaxMeters = 64f;

        /// <summary>容差（m）：一个 RTT 的位移 + 身体中心/眼睛差异。</summary>
        public const float ToleranceMeters = 0.5f;

        /// <summary>合法声明下限 / 上限（m）；超过上限视为 malformed（reach-invalid）。</summary>
        public const float MinClaimMeters = 0.5f;
        public const float MaxClaimMeters = 1024f;

        /// <summary>客户端上报前的夹紧上限（m）：只做"发一个有限值"，真正的防作弊上限在主机。</summary>
        public const float ClientSendMaxMeters = 128f;

        private readonly float m_serverMaxMeters;

        public InteractionReachGate(float serverMaxMeters)
        {
            if (!float.IsFinite(serverMaxMeters) || serverMaxMeters < MinServerMaxMeters ||
                serverMaxMeters > MaxServerMaxMeters)
            {
                throw new ArgumentOutOfRangeException(nameof(serverMaxMeters), serverMaxMeters,
                    "serverMax 必须在 " + MinServerMaxMeters + ".." + MaxServerMaxMeters + " m 之间");
            }
            m_serverMaxMeters = serverMaxMeters;
        }

        public float ServerMaxMeters => m_serverMaxMeters;

        /// <summary>共享计数（组合根创建一次，所有引擎适配器 / 状态行共用）。</summary>
        public InteractionReachCounters Counters { get; } = new InteractionReachCounters();

        /// <summary>引擎实际 reach（客户端侧）：创造 = CreativeReach，生存 = 5；夹到可发送的合理范围。</summary>
        public static float LocalReach(bool creative, float creativeReach)
        {
            float raw = creative ? creativeReach : SurvivalReachMeters;
            if (!float.IsFinite(raw))
                raw = creative ? DefaultCreativeReachMeters : SurvivalReachMeters;
            if (raw < MinClaimMeters) raw = MinClaimMeters;
            if (raw > ClientSendMaxMeters) raw = ClientSendMaxMeters;
            return raw;
        }

        /// <summary>客户端侧：不带上限的声明（主机负责夹到 serverMax）。</summary>
        public static InteractionReachClaim LocalClaim(bool creative, float creativeReach) =>
            new InteractionReachClaim(true, LocalReach(creative, creativeReach));

        /// <summary>主机侧默认 reach（旧客户端缺字段时使用）。</summary>
        public static float DefaultReach(bool creative) =>
            creative ? DefaultCreativeReachMeters : SurvivalReachMeters;

        /// <summary>对一次请求的声明做裁决；计数在调用时推进。</summary>
        public InteractionReachDecision Evaluate(bool hasReach, float reach, bool hostCreative)
        {
            Counters.NoteCheck();
            if (!hasReach)
            {
                Counters.NoteDefault();
                float fallback = DefaultReach(hostCreative);
                return new InteractionReachDecision(true, true, false, fallback,
                    Math.Min(fallback + ToleranceMeters, m_serverMaxMeters), null);
            }
            if (!float.IsFinite(reach) || reach < MinClaimMeters || reach > MaxClaimMeters)
            {
                Counters.NoteInvalid();
                return new InteractionReachDecision(false, false, false, 0f, 0f,
                    "reach-invalid: " + Format(reach) + " m（合法范围 " +
                    Format(MinClaimMeters) + ".." + Format(MaxClaimMeters) + " m）");
            }
            bool clamped = reach > m_serverMaxMeters;
            if (clamped) Counters.NoteClamped();
            float effective = clamped ? m_serverMaxMeters : reach;
            return new InteractionReachDecision(true, false, clamped, effective,
                Math.Min(effective + ToleranceMeters, m_serverMaxMeters), null);
        }

        /// <summary>
        /// 一次完整的"声明 + 实测距离"裁决。
        /// 返回 true = 放行；false 时 <paramref name="reason"/> 是明确的
        /// <c>reach-invalid: ...</c> 或 <c>too-far: 实测 &gt; 允许 (...)</c>。
        /// </summary>
        public bool TryCheckDistance(bool hasReach, float reach, bool hostCreative,
            float measuredMeters, out string reason)
        {
            InteractionReachDecision decision = Evaluate(hasReach, reach, hostCreative);
            if (!decision.Valid)
            {
                reason = decision.InvalidReason;
                return false;
            }
            if (!(measuredMeters <= decision.AllowedDistanceMeters))
            {
                reason = TooFarReason(measuredMeters, in decision);
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// 稳定原因码（第一个 ':' 之前）：限流日志的 key 必须用它，不能用带实测距离的整句 ——
        /// 否则每个不同的距离会各占一个 key，限流形同虚设（too-far: 5.1 / 5.2 / ...）。
        /// </summary>
        public static string ReasonCode(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "unknown";
            int colon = reason.IndexOf(':');
            string code = (colon > 0 ? reason.Substring(0, colon) : reason).Trim();
            return code.Length == 0 ? "unknown" : code;
        }

        /// <summary>显式拒绝原因（含实测 / 允许 / 生效 reach / 上限 / 容差，供限流日志直接定位）。</summary>
        public string TooFarReason(float measuredMeters, in InteractionReachDecision decision)
        {
            return "too-far: " + Format(measuredMeters) + " m > " +
                Format(decision.AllowedDistanceMeters) + " m（reach=" +
                Format(decision.EffectiveReachMeters) + " m" +
                (decision.Clamped
                    ? ", clamped serverMax=" + Format(m_serverMaxMeters) + " m"
                    : string.Empty) +
                (decision.UsedDefault ? ", old-client default" : string.Empty) +
                ", tolerance=" + Format(ToleranceMeters) + " m）";
        }

        private static string Format(float value) =>
            float.IsFinite(value)
                ? value.ToString("0.##", CultureInfo.InvariantCulture)
                : value.ToString(CultureInfo.InvariantCulture);
    }
}
