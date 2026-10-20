using System;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Players;
using V = SuMultiplayer.Protocol.Vitals;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// Sleep presentation / tap-to-wake 的硬上限与引擎常量（**纯逻辑**，不引用 Game）。
    /// <c>P:\tmp\tsverify</c> 直接编译这份源码，引擎侧（<c>Engine/PlayerVitalsReplica.cs</c>）
    /// 与模块层（<c>Modules/PlayerVitalsModule.cs</c>）用的是同一组值。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/ComponentSleep.cs:104-164）：
    ///   · 睡觉 → <c>m_sleepFactor += 0.33 * dt</c>（上限 1）；醒着（或死亡）→
    ///     <c>-= 1 * dt</c>（下限 0）；
    ///   · <c>BlackoutFactor = max(BlackoutFactor, m_sleepFactor)</c>；
    ///   · <c>m_sleepFactor &gt; 0.01</c> → <c>FloatingMessage = "Zzz..."</c>、
    ///     <c>FloatingMessageFactor = saturate(10 * (m_sleepFactor - 0.9))</c>；
    ///   · 手动唤醒允许且 <c>elapsed &gt; 10 s</c> → <c>Message = "Tap to wake up early"</c>、
    ///     <c>m_messageFactor += 0.5 * dt</c>；
    ///   · 不允许手动唤醒（强制 / 昏迷）且 <c>elapsed &gt; 5 s</c> →
    ///     <c>Message = "You fell unconscious from lack of sleep"</c>、<c>m_messageFactor += 1 * dt</c>；
    ///   · <c>ComponentGui.cs:395</c>：<c>SleepFactor &gt; 0</c> 时隐藏操作控件；
    ///   · <c>ComponentInput.cs:95</c>：<c>SleepFactor &gt; 0</c> 时清掉移动 / 跳跃输入。
    /// </summary>
    public static class PlayerSleepLimits
    {
        /// <summary>引擎手动唤醒门槛：<c>num &gt; 10f</c>（ComponentSleep.cs:131）。</summary>
        public const float TapGuardSeconds = 10f;

        /// <summary>强制睡眠提示门槛：<c>num &gt; 5f</c>（ComponentSleep.cs:146）。</summary>
        public const float UnconsciousMessageSeconds = 5f;

        /// <summary>入睡淡入速度（每秒，ComponentSleep.cs:109）。</summary>
        public const float FadeInPerSecond = 0.33f;

        /// <summary>醒来淡出速度（每秒，ComponentSleep.cs:156）。</summary>
        public const float FadeOutPerSecond = 1f;

        /// <summary>手动唤醒提示的 <c>m_messageFactor</c> 上升速度（ComponentSleep.cs:142）。</summary>
        public const float ManualMessageRisePerSecond = 0.5f;

        /// <summary>强制睡眠提示的 <c>m_messageFactor</c> 上升速度（ComponentSleep.cs:148）。</summary>
        public const float ForcedMessageRisePerSecond = 1f;

        /// <summary><c>m_sleepFactor &gt; 0.01</c> 才写 "Zzz..."（ComponentSleep.cs:159）。</summary>
        public const float ZzzVisibleThreshold = 0.01f;

        /// <summary>"Zzz..." 的淡入起点（<c>10 * (factor - 0.9)</c>）。</summary>
        public const float ZzzRampStart = 0.9f;

        /// <summary>"Zzz..." 的淡入斜率。</summary>
        public const float ZzzRampScale = 10f;

        /// <summary>
        /// 客户端唤醒请求的时钟安全余量（秒）：客户端的 <c>TotalElapsedGameTime</c> 由
        /// 世界信息以 2 Hz 对齐，漂移阈值 0.35 秒（<c>Engine/ClientReplicaEngine.cs</c> 的
        /// <c>WorldClockPolicy.NeedsSnap(..., 0.35)</c>）。客户端按
        /// <c>elapsed - 0.35</c> 过 10 秒门槛，保证主机用自己的权威时钟复检时**不会**
        /// 把玩家刚看到的提示点击拒成 <c>too-early</c>；主机的门槛始终是精确的 10 秒。
        /// </summary>
        public const float ClockSkewAllowanceSeconds = 0.35f;

        /// <summary>睡眠时长硬上界（秒）：引擎自动唤醒在 180 s 内，24 小时只是防脏值的上界。</summary>
        public const float MaxSleepElapsedSeconds = 86400f;

        /// <summary>睡眠起始游戏时（秒）的硬上界（防 NaN / 天文数字脏值）。</summary>
        public const double MaxSleepStartGameTime = 1e12;

        /// <summary>一条唤醒请求的字节上限。</summary>
        public const int MaxWakeRequestBytes = 512;

        /// <summary>稳定原因码长度上限（短码，不是句子）。</summary>
        public const int MaxReasonLength = 64;

        /// <summary>请求序号上限（防御性；一局不可能到达）。</summary>
        public const long MaxRequestSequence = long.MaxValue - 1;

        /// <summary><c>ext</c> 必须为空（内置消息；第三方扩展走 <c>custom</c>）。</summary>
        public const int MaxExtBytes = 0;
    }

    /// <summary>
    /// 引擎里写死的三句睡眠提示（**逐字一致**，客户端不能另写一句）。
    /// Source: Survivalcraft/Game/ComponentSleep.cs:143/149/161
    /// </summary>
    public static class PlayerSleepTexts
    {
        /// <summary><c>ComponentSleep.cs:161</c> 的漂浮提示。</summary>
        public const string Zzz = "Zzz...";

        /// <summary><c>ComponentSleep.cs:143</c> 的手动唤醒提示。</summary>
        public const string TapToWakeUpEarly = "Tap to wake up early";

        /// <summary><c>ComponentSleep.cs:149</c> 的强制睡眠（昏迷）提示。</summary>
        public const string FellUnconscious = "You fell unconscious from lack of sleep";
    }

    /// <summary>睡眠提示种类（与引擎 <c>ComponentScreenOverlays.Message</c> 的三种状态一一对应）。</summary>
    public enum PlayerSleepMessageKind
    {
        /// <summary>没有提示（未到门槛 / 醒着 / 老版本主机没有睡眠权威字段）。</summary>
        None = 0,

        /// <summary>"Tap to wake up early"（手动唤醒允许且 elapsed &gt; 10 s）。</summary>
        TapToWakeUpEarly = 1,

        /// <summary>"You fell unconscious from lack of sleep"（强制睡眠且 elapsed &gt; 5 s）。</summary>
        FellUnconscious = 2
    }

    /// <summary>
    /// 主机拒绝唤醒请求的**稳定原因码**（线路 / 状态行 / 日志同一个口径）。
    /// 产品要求的六个原因：<c>not-sleeping / forced-sleep / too-early / wrong-owner /
    /// unknown-player / cooldown</c>；<c>wake-failed</c> 只表示"结构与身份都合法，但主机引擎
    /// <c>WakeUp()</c> 失败"，<c>malformed</c> 表示载荷本身不合法。
    /// </summary>
    public static class PlayerSleepReasons
    {
        public const string Accepted = "accepted";
        public const string NotSleeping = "not-sleeping";
        public const string ForcedSleep = "forced-sleep";
        public const string TooEarly = "too-early";
        public const string WrongOwner = "wrong-owner";
        public const string UnknownPlayer = "unknown-player";
        public const string Cooldown = "cooldown";
        public const string WakeFailed = "wake-failed";
        public const string Malformed = "malformed";

        /// <summary>状态行计数的稳定键：第一个 ':' 之前的部分（空 = accepted）。</summary>
        public static string Key(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return Accepted;
            int colon = reason.IndexOf(':');
            string code = (colon > 0 ? reason.Substring(0, colon) : reason).Trim();
            return code.Length == 0 ? Accepted : code;
        }

        public static bool IsKnownRejection(string reason)
        {
            switch (Key(reason))
            {
                case NotSleeping:
                case ForcedSleep:
                case TooEarly:
                case WrongOwner:
                case UnknownPlayer:
                case Cooldown:
                case WakeFailed:
                case Malformed:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// 主机权威的睡眠状态（线上字段的领域形态）。<see cref="SleepStartTime"/> 是主机的
    /// <c>SubsystemGameInfo.TotalElapsedGameTime</c>（0 = 不在睡 / 没有睡眠权威字段 →
    /// 老版本主机）。<see cref="SleepElapsed"/> 是抓取那一刻距睡眠开始的秒数。
    /// </summary>
    public readonly struct PlayerSleepState
    {
        public PlayerSleepState(bool sleeping, bool dead, bool allowManualWake, bool forcedSleep,
            double sleepStartTime, float sleepElapsed)
        {
            Sleeping = sleeping;
            Dead = dead;
            AllowManualWake = allowManualWake;
            ForcedSleep = forcedSleep;
            SleepStartTime = sleepStartTime;
            SleepElapsed = sleepElapsed;
        }

        public bool Sleeping { get; }
        public bool Dead { get; }
        public bool AllowManualWake { get; }
        public bool ForcedSleep { get; }
        public double SleepStartTime { get; }
        public float SleepElapsed { get; }

        /// <summary>引擎淡入条件：<c>IsSleeping &amp;&amp; Health &gt; 0</c>。</summary>
        public bool Active => Sleeping && !Dead;

        /// <summary>
        /// 主机是否下发了睡眠表现权威字段（睡眠中且起始时 &gt; 0）。false = 老版本主机的
        /// "只有 sleeping 布尔"格式：仍然淡入 + Zzz / 黑屏，但没有提示文本 / 唤醒路径。
        /// </summary>
        public bool HasPresentation => Sleeping && SleepStartTime > 0.0;

        /// <summary>允许玩家点击唤醒（强制睡眠 / 昏迷不行）。</summary>
        public bool ManualWakeAllowed => Active && AllowManualWake && !ForcedSleep;

        /// <summary>
        /// 值域 + 语义校验。任何一条不满足都明确失败（调用方丢弃并计数），绝不夹紧后照用。
        /// </summary>
        public bool TryValidate(out string reason)
        {
            reason = null;
            if (!IsFinite(SleepStartTime))
            {
                reason = "sleep_start_time 不是有限数：" + SleepStartTime;
                return false;
            }
            if (SleepStartTime < 0.0 || SleepStartTime > PlayerSleepLimits.MaxSleepStartGameTime)
            {
                reason = "sleep_start_time = " + SleepStartTime + " 越界（0.." +
                    PlayerSleepLimits.MaxSleepStartGameTime + "）";
                return false;
            }
            if (!PlayerVitalsSnapshot.IsFiniteValue(SleepElapsed))
            {
                reason = "sleep_elapsed 不是有限数：" + SleepElapsed;
                return false;
            }
            if (SleepElapsed < 0f || SleepElapsed > PlayerSleepLimits.MaxSleepElapsedSeconds)
            {
                reason = "sleep_elapsed = " + SleepElapsed + " 越界（0.." +
                    PlayerSleepLimits.MaxSleepElapsedSeconds + "）";
                return false;
            }
            if (ForcedSleep && AllowManualWake)
            {
                reason = "forced_sleep=true 与 allow_manual_wake=true 矛盾";
                return false;
            }
            if (!Sleeping)
            {
                if (ForcedSleep)
                {
                    reason = "不在睡眠却带 forced_sleep=true";
                    return false;
                }
                if (SleepElapsed != 0f)
                {
                    reason = "不在睡眠却带 sleep_elapsed=" + SleepElapsed;
                    return false;
                }
                if (SleepStartTime != 0.0)
                {
                    reason = "不在睡眠却带 sleep_start_time=" + SleepStartTime;
                    return false;
                }
                return true;
            }
            if (SleepStartTime <= 0.0)
            {
                // 老版本主机 / 引擎 Load() 把持久化的 0.0 还原成 null：没有表现权威字段。
                if (ForcedSleep)
                {
                    reason = "没有 sleep_start_time 却带 forced_sleep=true";
                    return false;
                }
                if (SleepElapsed != 0f)
                {
                    reason = "没有 sleep_start_time 却带 sleep_elapsed=" + SleepElapsed;
                    return false;
                }
                return true;
            }
            // 新版本主机：睡眠中 forced 必须恰好等于 !allow。
            if (ForcedSleep == AllowManualWake)
            {
                reason = "睡眠中 forced_sleep 必须等于 !allow_manual_wake（当前 forced=" +
                    ForcedSleep + " allow=" + AllowManualWake + "）";
                return false;
            }
            return true;
        }

        public override string ToString() =>
            "sleep{sleeping=" + Sleeping + " dead=" + Dead + " allow=" + AllowManualWake +
            " forced=" + ForcedSleep + " start=" + SleepStartTime.ToString("0.##") +
            " elapsed=" + SleepElapsed.ToString("0.##") + "}";

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>
    /// 客户端**本地睡眠表现**的运行状态（端口 → 模块的只读视图）。<see cref="ElapsedSeconds"/>
    /// 是客户端用**同步过的世界时钟**从 <c>sleep_elapsed</c> 外推出来的当前值（见
    /// <see cref="PlayerSleepLimits.ClockSkewAllowanceSeconds"/>）。
    /// </summary>
    public readonly struct PlayerSleepViewState
    {
        public PlayerSleepViewState(bool sleeping, bool dead, bool allowManualWake,
            bool forcedSleep, bool hasPresentation, double sleepStartTime, float elapsedSeconds)
        {
            Sleeping = sleeping;
            Dead = dead;
            AllowManualWake = allowManualWake;
            ForcedSleep = forcedSleep;
            HasPresentation = hasPresentation;
            SleepStartTime = sleepStartTime;
            ElapsedSeconds = elapsedSeconds;
        }

        public bool Sleeping { get; }
        public bool Dead { get; }
        public bool AllowManualWake { get; }
        public bool ForcedSleep { get; }
        public bool HasPresentation { get; }
        public double SleepStartTime { get; }
        public float ElapsedSeconds { get; }

        public bool ManualWakeAllowed =>
            Sleeping && !Dead && AllowManualWake && !ForcedSleep;

        public override string ToString() =>
            "sleepView{sleeping=" + Sleeping + " dead=" + Dead + " allow=" + AllowManualWake +
            " forced=" + ForcedSleep + " hasPresentation=" + HasPresentation +
            " start=" + SleepStartTime.ToString("0.##") +
            " elapsed=" + ElapsedSeconds.ToString("0.##") + "}";
    }

    /// <summary>
    /// 主机侧一个角色的**实时**睡眠权威状态（模块用它复检唤醒请求；门槛 / 强制判定都
    /// 来自主机自己的引擎组件，绝不信任客户端上报的 elapsed）。
    /// </summary>
    public readonly struct PlayerSleepAuthorityState
    {
        public PlayerSleepAuthorityState(bool sleeping, bool dead, bool allowManualWake,
            double sleepStartTime, float elapsedSeconds)
        {
            Sleeping = sleeping;
            Dead = dead;
            AllowManualWake = allowManualWake;
            SleepStartTime = sleepStartTime;
            ElapsedSeconds = elapsedSeconds;
        }

        public bool Sleeping { get; }
        public bool Dead { get; }
        public bool AllowManualWake { get; }
        public double SleepStartTime { get; }
        public float ElapsedSeconds { get; }

        /// <summary>强制睡眠 = 睡着但引擎 <c>m_allowManualWakeUp == false</c>。</summary>
        public bool Forced => Sleeping && !AllowManualWake;

        public override string ToString() =>
            "sleepAuthority{sleeping=" + Sleeping + " dead=" + Dead + " allow=" +
            AllowManualWake + " start=" + SleepStartTime.ToString("0.##") +
            " elapsed=" + ElapsedSeconds.ToString("0.##") + "}";
    }

    /// <summary>
    /// 一帧的**睡眠表现值**（纯结果）：引擎侧只负责把这些值写进
    /// <c>ComponentSleep.m_sleepFactor</c> / <c>ComponentScreenOverlays</c> / 控件可见性。
    /// 与引擎 <c>ComponentSleep.Update:104-164</c> 逐条对应。
    /// </summary>
    public readonly struct PlayerSleepFrame
    {
        public PlayerSleepFrame(float factor, float messageFactor, float elapsedSeconds,
            bool zzzVisible, float zzzFactor, PlayerSleepMessageKind message,
            string messageText, bool controlsHidden, float blackoutFactor, bool active)
        {
            Factor = factor;
            MessageFactor = messageFactor;
            ElapsedSeconds = elapsedSeconds;
            ZzzVisible = zzzVisible;
            ZzzFactor = zzzFactor;
            Message = message;
            MessageText = messageText;
            ControlsHidden = controlsHidden;
            BlackoutFactor = blackoutFactor;
            Active = active;
        }

        /// <summary>引擎 <c>m_sleepFactor</c>（0..1）。</summary>
        public float Factor { get; }

        /// <summary>引擎 <c>m_messageFactor</c>（0..1）。</summary>
        public float MessageFactor { get; }

        /// <summary>本帧用于提示 / 唤醒判断的 elapsed（秒）。</summary>
        public float ElapsedSeconds { get; }

        /// <summary><c>m_sleepFactor &gt; 0.01</c> → 写 "Zzz..."。</summary>
        public bool ZzzVisible { get; }

        /// <summary><c>FloatingMessageFactor</c>。</summary>
        public float ZzzFactor { get; }

        /// <summary>提示种类。</summary>
        public PlayerSleepMessageKind Message { get; }

        /// <summary>提示文本（None = null，逐字等于引擎文本）。</summary>
        public string MessageText { get; }

        /// <summary><c>SleepFactor &gt; 0</c> → 隐藏操作控件。</summary>
        public bool ControlsHidden { get; }

        /// <summary>写进 <c>ComponentScreenOverlays.BlackoutFactor</c> 的下限值。</summary>
        public float BlackoutFactor { get; }

        /// <summary>引擎淡入条件（睡眠中且活着）。</summary>
        public bool Active { get; }

        /// <summary>这一帧是否需要写表现（否则保持引擎每帧 Reset 后的干净状态）。</summary>
        public bool PresentationActive => Active || Factor > 0f || Message != PlayerSleepMessageKind.None;

        public override string ToString() =>
            "sleepFrame{factor=" + Factor.ToString("0.###") + " msgFactor=" +
            MessageFactor.ToString("0.###") + " elapsed=" + ElapsedSeconds.ToString("0.##") +
            " zzz=" + ZzzVisible + "/" + ZzzFactor.ToString("0.###") + " message=" + Message +
            " controlsHidden=" + ControlsHidden + "}";
    }

    /// <summary>
    /// 睡眠表现 / 唤醒门槛的**纯规则**（无 Game 依赖，自检直接覆盖生产代码）。
    /// 客户端每帧推进一帧表现；主机用 <see cref="ValidateWakeAuthority"/> 复检唤醒请求。
    /// </summary>
    public static class PlayerSleepRules
    {
        /// <summary>引擎门槛：<c>num &gt; 10f</c>（严格大于）。</summary>
        public static bool IsPastTapGuard(float elapsedSeconds) =>
            elapsedSeconds > PlayerSleepLimits.TapGuardSeconds;

        /// <summary>引擎强制睡眠提示门槛：<c>num &gt; 5f</c>（严格大于）。</summary>
        public static bool IsPastUnconsciousMessage(float elapsedSeconds) =>
            elapsedSeconds > PlayerSleepLimits.UnconsciousMessageSeconds;

        /// <summary>
        /// 本帧应该显示哪一种提示（与 <c>ComponentSleep.Update:128-151</c> 的分支一致）。
        /// 没有睡眠权威字段（老版本主机）时**不显示提示**（也就没有唤醒路径）。
        /// </summary>
        public static PlayerSleepMessageKind MessageKind(in PlayerSleepState state,
            float elapsedSeconds)
        {
            if (!state.Active || !state.HasPresentation) return PlayerSleepMessageKind.None;
            if (state.AllowManualWake && !state.ForcedSleep)
            {
                return IsPastTapGuard(elapsedSeconds)
                    ? PlayerSleepMessageKind.TapToWakeUpEarly
                    : PlayerSleepMessageKind.None;
            }
            return IsPastUnconsciousMessage(elapsedSeconds)
                ? PlayerSleepMessageKind.FellUnconscious
                : PlayerSleepMessageKind.None;
        }

        /// <summary>提示文本；None → null。</summary>
        public static string MessageText(PlayerSleepMessageKind kind)
        {
            switch (kind)
            {
                case PlayerSleepMessageKind.TapToWakeUpEarly:
                    return PlayerSleepTexts.TapToWakeUpEarly;
                case PlayerSleepMessageKind.FellUnconscious:
                    return PlayerSleepTexts.FellUnconscious;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 推进一帧睡眠表现。逐条对应引擎 <c>ComponentSleep.Update</c>：
        /// 淡入 0.33/s（仅 <c>IsSleeping &amp;&amp; Health &gt; 0</c>）/ 淡出 1/s；
        /// Zzz 阈值 0.01、强度 <c>saturate(10*(factor-0.9))</c>；
        /// 手动提示 0.5/s（&gt;10 s）、强制提示 1/s（&gt;5 s）；
        /// <c>SleepFactor &gt; 0</c> 隐藏控件；黑屏 = max(现有, factor)。
        /// </summary>
        public static PlayerSleepFrame Advance(in PlayerSleepState state, float elapsedSeconds,
            float factor, float messageFactor, float dt)
        {
            float step = PlayerVitalsSnapshot.IsFiniteValue(dt) && dt > 0f ? dt : 0f;
            float current = PlayerVitalsSnapshot.IsFiniteValue(factor)
                ? MathUtils.Clamp(factor, 0f, 1f)
                : 0f;
            float currentMessage = PlayerVitalsSnapshot.IsFiniteValue(messageFactor)
                ? MathUtils.Clamp(messageFactor, 0f, 1f)
                : 0f;
            bool active = state.Active;
            float next = active
                ? MathUtils.Min(current + PlayerSleepLimits.FadeInPerSecond * step, 1f)
                : MathUtils.Max(current - PlayerSleepLimits.FadeOutPerSecond * step, 0f);
            float elapsed = state.Sleeping && PlayerVitalsSnapshot.IsFiniteValue(elapsedSeconds)
                ? MathUtils.Max(elapsedSeconds, 0f)
                : 0f;
            PlayerSleepMessageKind kind = MessageKind(in state, elapsed);
            float nextMessage;
            switch (kind)
            {
                case PlayerSleepMessageKind.TapToWakeUpEarly:
                    nextMessage = MathUtils.Min(currentMessage +
                        PlayerSleepLimits.ManualMessageRisePerSecond * step, 1f);
                    break;
                case PlayerSleepMessageKind.FellUnconscious:
                    nextMessage = MathUtils.Min(currentMessage +
                        PlayerSleepLimits.ForcedMessageRisePerSecond * step, 1f);
                    break;
                default:
                    // 引擎在睡眠开始时把 m_messageFactor 归 0（Sleep()），提示消失时
                    // ComponentScreenOverlays.Update 每帧清掉 Message；这里把 factor 归 0
                    // 得到同样的显示结果，并保证醒来后不会残留提示。
                    nextMessage = 0f;
                    break;
            }
            bool zzz = next > PlayerSleepLimits.ZzzVisibleThreshold;
            float zzzFactor = zzz
                ? MathUtils.Saturate(PlayerSleepLimits.ZzzRampScale * (next -
                    PlayerSleepLimits.ZzzRampStart))
                : 0f;
            return new PlayerSleepFrame(next, nextMessage, elapsed, zzz, zzzFactor, kind,
                MessageText(kind), next > 0f, next, active);
        }

        /// <summary>
        /// 主机权威复检（**只用主机自己的引擎组件**）：必须睡着且活着 → 允许手动唤醒 →
        /// elapsed &gt; 10 s。顺序与产品要求一致；失败给出稳定原因码。
        /// </summary>
        public static bool ValidateWakeAuthority(in PlayerSleepAuthorityState state,
            out string reason)
        {
            if (!state.Sleeping || state.Dead)
            {
                reason = PlayerSleepReasons.NotSleeping;
                return false;
            }
            if (!state.AllowManualWake)
            {
                reason = PlayerSleepReasons.ForcedSleep;
                return false;
            }
            if (!IsPastTapGuard(state.ElapsedSeconds))
            {
                reason = PlayerSleepReasons.TooEarly;
                return false;
            }
            reason = null;
            return true;
        }
    }

    /// <summary>一次睡眠点击的判定结果（客户端门控）。</summary>
    public enum PlayerSleepGateAction
    {
        None = 0,

        /// <summary>发送一条 <c>PLAYER_WAKE_REQUEST</c>。</summary>
        SendRequest = 1,

        /// <summary>已经不在睡眠（报告 / 状态已醒）。</summary>
        SuppressedNotSleeping = 2,

        /// <summary>强制睡眠 / 昏迷（引擎不允许手动唤醒）。</summary>
        SuppressedForced = 3,

        /// <summary>还没到 10 秒手动唤醒门槛。</summary>
        SuppressedEarly = 4,

        /// <summary>本次睡眠已经发过请求（每次睡眠最多一条）。</summary>
        SuppressedAlreadyRequested = 5,

        /// <summary>角色已经死亡（不能唤醒）。</summary>
        SuppressedDead = 6,

        /// <summary>老版本主机没有睡眠权威字段（没有唤醒路径）。</summary>
        SuppressedNoPresentation = 7,

        /// <summary>没有登记的睡眠期（门控没有 "arm" 过）。</summary>
        SuppressedNoEpisode = 8,

        /// <summary>请求序号非法。</summary>
        SuppressedInvalidSequence = 9
    }

    /// <summary>
    /// 客户端**每次睡眠最多一条**唤醒请求的门控（与 <c>PlayerRespawnGate</c> 同一套纪律）。
    /// 纯逻辑：输入是主机权威状态 + 本地点击；输出是"发 / 不发"与稳定原因。
    /// 只在睡眠期第一次 <see cref="OnSleep"/> 时登记；醒来由 <see cref="OnWake"/> 清表。
    /// 发送失败由模块调用 <see cref="OnSendFailed"/> 释放本次请求（下一次点击可重试）。
    /// </summary>
    public sealed class PlayerSleepGate
    {
        private sealed class Entry
        {
            public double SleepStartTime;
            public bool RequestSent;
            public long RequestSequence;
            public float RequestElapsed;
        }

        private readonly System.Collections.Generic.Dictionary<PlayerKey, Entry> m_entries =
            new System.Collections.Generic.Dictionary<PlayerKey, Entry>();

        public int Count => m_entries.Count;

        public bool Contains(PlayerKey key) => m_entries.ContainsKey(key);

        public void CopyKeys(System.Collections.Generic.List<PlayerKey> target)
        {
            if (target == null) return;
            target.Clear();
            foreach (PlayerKey key in m_entries.Keys) target.Add(key);
        }

        public void Clear() => m_entries.Clear();

        /// <summary>
        /// 登记一个睡眠期（按主机 <c>sleep_start_time</c> 识别）。返回 true = 新睡眠期
        /// （调用方可以写一行日志 / 计数）；同一睡眠期的重复心跳返回 false。
        /// </summary>
        public bool OnSleep(PlayerKey key, double sleepStartTime)
        {
            if (m_entries.TryGetValue(key, out Entry entry))
            {
                if (entry.SleepStartTime.Equals(sleepStartTime)) return false;
                entry.SleepStartTime = sleepStartTime;
                entry.RequestSent = false;
                entry.RequestSequence = 0;
                entry.RequestElapsed = 0f;
                return true;
            }
            m_entries[key] = new Entry { SleepStartTime = sleepStartTime };
            return true;
        }

        /// <summary>
        /// 醒来（主机报告 sleeping=false / 死亡 / 复活）。返回 true = 这个睡眠期确实发过
        /// 唤醒请求（模块据此计 <c>sleepWakeConfirmed</c>：主机接受了请求并让角色醒来）。
        /// </summary>
        public bool OnWake(PlayerKey key)
        {
            if (!m_entries.TryGetValue(key, out Entry entry)) return false;
            m_entries.Remove(key);
            return entry.RequestSent;
        }

        /// <summary>
        /// 一次点击。顺序与引擎一致：不在睡 → 死亡 → 没有睡眠权威字段 → 强制睡眠 →
        /// 10 秒门槛 → 每次睡眠最多一条。客户端用 <paramref name="skewAllowanceSeconds"/>
        /// 抵消世界时钟对齐误差（主机始终按精确 10 秒复检）。
        /// </summary>
        public PlayerSleepGateAction OnTap(PlayerKey key, in PlayerSleepViewState state,
            long requestSequence, float skewAllowanceSeconds, out string reason)
        {
            reason = null;
            if (!state.Sleeping)
            {
                reason = PlayerSleepReasons.NotSleeping;
                return PlayerSleepGateAction.SuppressedNotSleeping;
            }
            if (state.Dead)
            {
                reason = "dead";
                return PlayerSleepGateAction.SuppressedDead;
            }
            if (!state.HasPresentation)
            {
                reason = "no-presentation";
                return PlayerSleepGateAction.SuppressedNoPresentation;
            }
            if (state.ForcedSleep || !state.AllowManualWake)
            {
                reason = PlayerSleepReasons.ForcedSleep;
                return PlayerSleepGateAction.SuppressedForced;
            }
            float allowance = PlayerVitalsSnapshot.IsFiniteValue(skewAllowanceSeconds)
                ? MathUtils.Max(skewAllowanceSeconds, 0f)
                : 0f;
            float effective = state.ElapsedSeconds - allowance;
            if (!PlayerSleepRules.IsPastTapGuard(effective))
            {
                reason = PlayerSleepReasons.TooEarly;
                return PlayerSleepGateAction.SuppressedEarly;
            }
            if (!m_entries.TryGetValue(key, out Entry entry) ||
                !entry.SleepStartTime.Equals(state.SleepStartTime))
            {
                // 没有登记的睡眠期（例如模块刚刚装配、还没收到睡眠报告）：不凭点击造请求。
                reason = "no-episode";
                return PlayerSleepGateAction.SuppressedNoEpisode;
            }
            if (entry.RequestSent)
            {
                reason = "already-sent";
                return PlayerSleepGateAction.SuppressedAlreadyRequested;
            }
            if (requestSequence <= 0 || requestSequence > PlayerSleepLimits.MaxRequestSequence)
            {
                reason = "invalid-sequence";
                return PlayerSleepGateAction.SuppressedInvalidSequence;
            }
            entry.RequestSent = true;
            entry.RequestSequence = requestSequence;
            entry.RequestElapsed = effective;
            return PlayerSleepGateAction.SendRequest;
        }

        /// <summary>发送失败：释放本次请求（本次睡眠的下一次点击可以重试）。</summary>
        public void OnSendFailed(PlayerKey key)
        {
            if (!m_entries.TryGetValue(key, out Entry entry)) return;
            entry.RequestSent = false;
            entry.RequestSequence = 0;
            entry.RequestElapsed = 0f;
        }

        /// <summary>诊断：某个键是否已经发过请求（<c>false</c> = 没登记）。</summary>
        public bool TryGetRequest(PlayerKey key, out bool sent, out long sequence, out float elapsed)
        {
            sent = false;
            sequence = 0;
            elapsed = 0f;
            if (!m_entries.TryGetValue(key, out Entry entry)) return false;
            sent = entry.RequestSent;
            sequence = entry.RequestSequence;
            elapsed = entry.RequestElapsed;
            return true;
        }
    }

    /// <summary>一条已验证的 <c>PLAYER_WAKE_REQUEST</c>（client -> host）。</summary>
    public readonly struct PlayerWakeRequestData
    {
        public PlayerWakeRequestData(int clientId, int slot, long requestSequence,
            float sleepElapsed, double sleepStartTime)
        {
            ClientId = clientId;
            Slot = slot;
            RequestSequence = requestSequence;
            SleepElapsed = sleepElapsed;
            SleepStartTime = sleepStartTime;
        }

        public int ClientId { get; }
        public int Slot { get; }
        public long RequestSequence { get; }

        /// <summary>客户端估计的睡眠时长（诊断 / 日志；主机不拿它做门槛）。</summary>
        public float SleepElapsed { get; }

        /// <summary>客户端请求结束的睡眠期（主机用于日志比对）。</summary>
        public double SleepStartTime { get; }

        public PlayerKey Key => new PlayerKey(ClientId, Slot);

        public string Describe() => "wake-request{key=" + ClientId + ":" + Slot +
            " seq=" + RequestSequence + " elapsed=" + SleepElapsed.ToString("0.##") +
            " start=" + SleepStartTime.ToString("0.##") + "}";
    }

    /// <summary>
    /// <c>PLAYER_WAKE_REQUEST</c>（id 103）的有界编解码。编码对非法数据抛异常；解码返回
    /// false + 可直接定位的原因（绝不静默丢弃 / 绝不夹紧）。
    /// </summary>
    public static class PlayerWakeRequestCodec
    {
        public static V.PlayerWakeRequest Encode(in PlayerWakeRequestData data)
        {
            if (!TryEncode(in data, out V.PlayerWakeRequest message, out string reason))
                throw new InvalidOperationException("唤醒请求编码失败：" + reason);
            return message;
        }

        public static byte[] EncodeBytes(in PlayerWakeRequestData data)
        {
            V.PlayerWakeRequest message = Encode(in data);
            byte[] payload = message.ToByteArray();
            if (payload.Length > PlayerSleepLimits.MaxWakeRequestBytes)
                throw new InvalidOperationException("唤醒请求 " + payload.Length +
                    " 字节超过上限 " + PlayerSleepLimits.MaxWakeRequestBytes);
            return payload;
        }

        public static bool TryDecode(V.PlayerWakeRequest message,
            out PlayerWakeRequestData data, out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "wake-request-null";
                return false;
            }
            if (!CheckExt(message.Ext, out reason)) return false;
            if (message.ClientId > int.MaxValue)
            {
                reason = "client_id=" + message.ClientId + " 超出 int 范围";
                return false;
            }
            int clientId = (int)message.ClientId;
            if (clientId <= 0)
            {
                reason = "client_id=" + clientId + " 非法（请求只能来自玩家端，> 0）";
                return false;
            }
            if (message.Slot > PlayerKey.MaxSlot)
            {
                reason = "slot=" + message.Slot + " 非法（0.." + PlayerKey.MaxSlot + "）";
                return false;
            }
            if (message.RequestSequence == 0 ||
                message.RequestSequence > (ulong)PlayerSleepLimits.MaxRequestSequence)
            {
                reason = "request_sequence=" + message.RequestSequence + " 非法（1.." +
                    PlayerSleepLimits.MaxRequestSequence + "）";
                return false;
            }
            if (!PlayerVitalsSnapshot.IsFiniteValue(message.SleepElapsed) ||
                message.SleepElapsed < 0f ||
                message.SleepElapsed > PlayerSleepLimits.MaxSleepElapsedSeconds)
            {
                reason = "sleep_elapsed = " + message.SleepElapsed + " 非法（0.." +
                    PlayerSleepLimits.MaxSleepElapsedSeconds + " 的有限数）";
                return false;
            }
            if (!IsFinite(message.SleepStartTime) || message.SleepStartTime < 0.0 ||
                message.SleepStartTime > PlayerSleepLimits.MaxSleepStartGameTime)
            {
                reason = "sleep_start_time = " + message.SleepStartTime + " 非法（0.." +
                    PlayerSleepLimits.MaxSleepStartGameTime + " 的有限数）";
                return false;
            }
            int size = message.CalculateSize();
            if (size > PlayerSleepLimits.MaxWakeRequestBytes)
            {
                reason = "请求 " + size + " 字节超过上限 " +
                    PlayerSleepLimits.MaxWakeRequestBytes;
                return false;
            }
            data = new PlayerWakeRequestData(clientId, (int)message.Slot,
                (long)message.RequestSequence, message.SleepElapsed, message.SleepStartTime);
            return true;
        }

        private static bool TryEncode(in PlayerWakeRequestData data,
            out V.PlayerWakeRequest message, out string reason)
        {
            message = null;
            reason = null;
            if (data.ClientId <= 0)
            {
                reason = "client_id=" + data.ClientId + " 非法（必须 > 0）";
                return false;
            }
            if (data.Slot < 0 || data.Slot > PlayerKey.MaxSlot)
            {
                reason = "slot=" + data.Slot + " 非法（0.." + PlayerKey.MaxSlot + "）";
                return false;
            }
            if (data.RequestSequence <= 0 ||
                data.RequestSequence > PlayerSleepLimits.MaxRequestSequence)
            {
                reason = "request_sequence=" + data.RequestSequence + " 非法（1.." +
                    PlayerSleepLimits.MaxRequestSequence + "）";
                return false;
            }
            if (!PlayerVitalsSnapshot.IsFiniteValue(data.SleepElapsed) ||
                data.SleepElapsed < 0f ||
                data.SleepElapsed > PlayerSleepLimits.MaxSleepElapsedSeconds)
            {
                reason = "sleep_elapsed = " + data.SleepElapsed + " 非法";
                return false;
            }
            if (!IsFinite(data.SleepStartTime) || data.SleepStartTime < 0.0 ||
                data.SleepStartTime > PlayerSleepLimits.MaxSleepStartGameTime)
            {
                reason = "sleep_start_time = " + data.SleepStartTime + " 非法";
                return false;
            }
            message = new V.PlayerWakeRequest
            {
                ClientId = (uint)data.ClientId,
                Slot = (uint)data.Slot,
                RequestSequence = (ulong)data.RequestSequence,
                SleepElapsed = data.SleepElapsed,
                SleepStartTime = data.SleepStartTime
            };
            if (message.CalculateSize() > PlayerSleepLimits.MaxWakeRequestBytes)
            {
                reason = "请求超过 " + PlayerSleepLimits.MaxWakeRequestBytes + " 字节";
                message = null;
                return false;
            }
            return true;
        }

        private static bool CheckExt(Google.Protobuf.ByteString ext, out string reason)
        {
            reason = null;
            int length = ext?.Length ?? 0;
            if (length <= PlayerSleepLimits.MaxExtBytes) return true;
            reason = "ext=" + length + " 字节（内置消息必须为空；第三方扩展走 custom）";
            return false;
        }

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
