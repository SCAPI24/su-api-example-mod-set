using System;
using Engine;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 第三分屏（H3 的 <c>GameWidget</c>）**客户端受伤 / 体征 / 死亡反馈的纯映射**：
    /// 不引用 <c>Game</c>，自检（P:\tmp\tsverify）直接编译本文件。
    ///
    /// 为什么需要它（引擎事实，逐条已核对源码）：
    ///   · 掉血反馈只在 <c>ComponentHealth.Update</c> 里产生，并且写的是**该 ComponentHealth 所属玩家自己**的
    ///     <c>ComponentGui</c> / <c>ComponentScreenOverlays</c>：
    ///       <c>HealthChange = Health - m_lastHealth</c>（<c>ComponentHealth.cs:251</c>）；
    ///       <c>m_redScreenFactor += -4f * HealthChange</c>（<c>:256</c>）；
    ///       <c>HealthBarWidget.Flash(MathUtils.Clamp((int)((0f - HealthChange) * 30f), 0, 10))</c>（<c>:257</c>）；
    ///       <c>RedoutFactor = MathUtils.Max(RedoutFactor, m_redScreenFactor)</c>（<c>:261</c>）；
    ///       <c>HealthBarWidget.Value = Health</c>（<c>:265</c>）。
    ///     replace 模式把第三分屏的相机绑到客户端的 <c>ComponentPlayer</c>，但 HUD 仍属于视图所有者 H3，
    ///     而 H3 的 <c>ComponentHealth.Update</c> 在绑定期间被摘掉（<c>MirrorCharacterEngine.TryBind</c>）——
    ///     所以客户端掉血不会让第三分屏闪红、血条也不会动。本类把这条链在视图所有者身上**重放一遍**，
    ///     只产生"要写哪些显示值"的决定，实际写控件的活由 <see cref="MirrorViewFeedback"/> 做。
    ///   · <c>m_redScreenFactor</c> 每拍先按 <c>MathUtils.Pow(0.2f, dt)</c> 衰减、≤0.01 归零（<c>ComponentHealth.cs:141-147</c>）；
    ///     红屏 <c>RedoutFactor</c> / 黑屏 <c>BlackoutFactor</c> / 冰冻 <c>IceFactor</c> 都被
    ///     <c>ComponentScreenOverlays.Update</c> 每拍清 0（<c>:60-77</c>，UpdateOrder.Reset），所以只要还有值就必须每拍重写。
    ///   · 食物条 = <c>ComponentVitalStats.UpdateFood</c> 每拍写 <c>FoodBarWidget.Value = Food</c>（<c>:345</c>）；
    ///     温度条**不是进度条**：XML 里 <c>Value=1</c>，变的只有 <c>BarSubtexture</c>（<c>:589-616</c>，
    ///     档位阈值 22/18/14/10/6/2 全部严格大于）；冰冻覆盖 <c>IceFactor = Saturate(1 - Temperature/6)</c>（<c>:579</c>）；
    ///     等级文字 = <c>ComponentLevel.Update</c> 的 <c>"Level " + MathUtils.Floor(PlayerData.Level)</c>（<c>:335</c>，边沿触发）。
    ///   · 死亡表现 = 黑屏 + <c>ComponentGui.DisplayLargeMessage</c>（<c>PlayerData.cs:275-300</c>：
    ///     大标题 "YOU HAVE DIED"、小字 "Cause of death: …"、30 秒 / 1.5 秒延迟）。
    ///
    /// 明确的"不做"：不写 <c>ComponentHealth</c> / <c>ComponentVitalStats</c> / <c>PlayerData</c> 的任何字段
    /// （没有 Injure / 没有 setter / 没有背包复制），不切相机（第三分屏的 ActiveCamera 必须留在客户端第一人称，
    /// 否则 <c>MirrorCharacterEngine.ValidateBound</c> 会把相机重新对回客户端）。
    ///
    /// Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update:139-265
    /// Source: Survivalcraft/Game/ComponentScreenOverlays.cs:ComponentScreenOverlays.Update:60-77
    /// Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.UpdateFood:345 / UpdateTemperature:579,589-616
    /// Source: Survivalcraft/Game/ComponentLevel.cs:ComponentLevel.Update:335
    /// Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerDead:275-300
    /// Source: Pak/Widgets/GameWidget.xml:51-54（HealthBar / TemperatureBar / LevelLabel / FoodBar）
    /// </summary>
    public static class MirrorViewFeedbackMath
    {
        /// <summary>红屏因子衰减到该值以下即归零（引擎 <c>ComponentHealth.Update:141-147</c>）。</summary>
        public const float RedoutCutoff = 0.01f;

        /// <summary>每 1.0 点掉血增加的红屏因子（引擎 <c>-4f * HealthChange</c>，而 <c>HealthChange = -drop</c>）。</summary>
        public const float RedFactorPerHealth = 4f;

        /// <summary>血条闪烁强度系数（引擎 <c>(0f - HealthChange) * 30f</c>）。</summary>
        public const float FlashStrengthPerHealth = 30f;

        /// <summary>血条闪烁上限（引擎 <c>MathUtils.Clamp(..., 0, 10)</c>）。</summary>
        public const int MaxHealthBarFlash = 10;

        /// <summary>HUD 显示值去重阈值：同值不重复写控件（"diff-based"）。</summary>
        public const float HudEpsilon = 0.0001f;

        /// <summary>非有限值（NaN / ±Inf）—— 一律明确失败，绝不写进引擎控件。</summary>
        public static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>
        /// 一次掉血要加的红屏因子：引擎 <c>m_redScreenFactor += -4f * HealthChange</c>，
        /// 其中 <c>HealthChange = Health - m_lastHealth = -drop</c>，所以等价于 <c>4f * drop</c>。
        /// 掉血 &lt;= 0 / 非有限值一律 0（加血不加红屏）。
        /// </summary>
        public static float RedFactorIncrement(float healthDrop)
        {
            if (!(healthDrop > 0f) || !IsFinite(healthDrop)) return 0f;
            return RedFactorPerHealth * healthDrop;
        }

        /// <summary>
        /// 血条闪烁次数：引擎 <c>MathUtils.Clamp((int)((0f - HealthChange) * 30f), 0, 10)</c>
        /// = <c>Clamp((int)(drop * 30f), 0, 10)</c>（float→int 截断，然后夹到 0..10）。
        /// </summary>
        public static int HealthBarFlashCount(float healthDrop)
        {
            if (!(healthDrop > 0f) || !IsFinite(healthDrop)) return 0;
            if (healthDrop * FlashStrengthPerHealth >= MaxHealthBarFlash) return MaxHealthBarFlash;
            int count = (int)(healthDrop * FlashStrengthPerHealth);
            if (count < 0) return 0;
            return count > MaxHealthBarFlash ? MaxHealthBarFlash : count;
        }

        /// <summary>
        /// 红屏因子每拍衰减：引擎 <c>if (m_redScreenFactor &gt; 0.01f) m_redScreenFactor *= Pow(0.2f, dt); else 0f;</c>
        /// （<c>ComponentHealth.cs:141-147</c>）。非有限值按 0 处理（调用方已保证输入有限）。
        /// </summary>
        public static float DecayRedFactor(float factor, float dt)
        {
            if (!IsFinite(factor) || !IsFinite(dt)) return 0f;
            if (!(factor > RedoutCutoff)) return 0f;
            return factor * MathUtils.Pow(0.2f, dt);
        }

        /// <summary>两个 HUD 显示值是否视为同一个（不重复写控件）。</summary>
        public static bool Near(float left, float right) =>
            Math.Abs(left - right) <= HudEpsilon;

        /// <summary>等级文字口径：引擎 <c>"Level " + MathUtils.Floor(PlayerData.Level)</c>（<c>ComponentLevel.cs:335</c>）。</summary>
        public static int LevelOf(float level) => (int)MathUtils.Floor(level);

        /// <summary>等级文字（与引擎完全同一个字符串）。</summary>
        public static string LevelText(int level) => "Level " + level;
    }

    /// <summary>
    /// 第三分屏的死亡文字（与引擎同一套大消息 / 时长 / 延迟；小字只写死因与本端状态，
    /// **不**写 "Tap to respawn" —— 主机点屏幕不能替客户端复活，那句话会是假的）。
    ///
    /// Source: Survivalcraft/Game/PlayerData.cs:PlayerData.PlayerDead:275-300
    /// Source: SuMultiplayer/Net/PlayerRespawnNet.cs:PlayerRespawnRules.MessageDurationSeconds/MessageDelaySeconds
    /// </summary>
    public static class MirrorViewDeathText
    {
        /// <summary>引擎死亡大标题（<c>PlayerData.cs:275-300</c>，与 <c>PlayerRespawnRules.DeathLargeText</c> 同一个串）。</summary>
        public const string LargeText = "YOU HAVE DIED";

        /// <summary>大消息持续时间（引擎 30 秒）。</summary>
        public const float DurationSeconds = 30f;

        /// <summary>大消息延迟（引擎 1.5 秒）。</summary>
        public const float DelaySeconds = 1.5f;

        /// <summary>
        /// 小字：<c>"Cause of death: &lt;cause&gt;\n\n&lt;client&gt; is dead (waiting for respawn)"</c>。
        /// 死因为空 = "Unknown"（引擎 <c>PlayerData.cs:279-283</c> 同口径）。
        /// </summary>
        public static string BuildSmallText(string cause, string clientName)
        {
            string causeText = string.IsNullOrEmpty(cause) ? "Unknown" : cause;
            string who = string.IsNullOrEmpty(clientName) ? "The client" : clientName;
            return "Cause of death: " + causeText + "\n\n" + who +
                " is dead (waiting for respawn)";
        }
    }

    /// <summary>
    /// 第三分屏客户端反馈镜像的计数器（实例持有，无静态状态）。字段名与需求逐项对应：
    /// <c>viewDamageFlash</c> / <c>viewHealthBarFlash</c> / <c>viewHudNumbers</c> /
    /// <c>viewDeathOverlay</c> / <c>viewFeedbackFailures</c> / <c>viewFeedbackSkipped</c>（+ 原因）。
    /// 额外两个只在 Describe 里出现、便于实机排查的细项：<c>viewIceWrites</c>、<c>viewRespawnClears</c>。
    /// 计数**每拍如实累加**，日志按 10 秒限流（见 <see cref="MirrorViewFeedback"/>）。
    /// </summary>
    public sealed class MirrorViewFeedbackCounters
    {
        /// <summary>掉血 → 第三分屏红屏的次数（红屏因子真的超过截断、会被画出来的那些拍）。</summary>
        public long ViewDamageFlash { get; private set; }

        /// <summary>掉血 → <c>HealthBarWidget.Flash(...)</c> 的次数（闪烁次数 &gt; 0 的那些拍）。</summary>
        public long ViewHealthBarFlash { get; private set; }

        /// <summary>HUD 数值 / 档位 / 等级文字的写入次数（diff-based：只在值变了才写）。</summary>
        public long ViewHudNumbers { get; private set; }

        /// <summary>死亡黑屏 + 死亡大消息的展示次数（每次死亡边界 1 次）。</summary>
        public long ViewDeathOverlay { get; private set; }

        /// <summary>写入失败次数（控件缺失 / 引擎字段读不到 / 写控件抛异常）。</summary>
        public long ViewFeedbackFailures { get; private set; }

        /// <summary>本拍跳过镜像的次数（不可观测 / 未接入 / 来源组件缺失）。</summary>
        public long ViewFeedbackSkipped { get; private set; }

        /// <summary>最近一次跳过原因（稳定键，日志 / 状态行直接用）。</summary>
        public string LastSkipReason { get; private set; }

        /// <summary>最近一次失败原因（稳定键 + 明细）。</summary>
        public string LastFailureReason { get; private set; }

        /// <summary>冰冻覆盖 <c>IceFactor</c> 的写入次数（引擎每拍清 0，所以有值就必须每拍写）。</summary>
        public long ViewIceWrites { get; private set; }

        /// <summary>复活清屏次数（清黑屏 / 清死亡大消息 / 红屏因子归零）。</summary>
        public long ViewRespawnClears { get; private set; }

        /// <summary>被驱动的帧数（分母；配置关闭 / 未绑定时不涨）。</summary>
        public long ViewFrames { get; private set; }

        public void NoteFrame()
        {
            ViewFrames++;
        }

        public void NoteDamageFlash()
        {
            ViewDamageFlash++;
        }

        public void NoteHealthBarFlash()
        {
            ViewHealthBarFlash++;
        }

        public void NoteHudNumber()
        {
            ViewHudNumbers++;
        }

        public void NoteDeathOverlay()
        {
            ViewDeathOverlay++;
        }

        public void NoteIceWrite()
        {
            ViewIceWrites++;
        }

        public void NoteRespawnClear()
        {
            ViewRespawnClears++;
        }

        public void NoteFailure(string reason)
        {
            ViewFeedbackFailures++;
            LastFailureReason = reason;
        }

        public void NoteSkipped(string reason)
        {
            ViewFeedbackSkipped++;
            LastSkipReason = reason;
        }

        /// <summary>可审计的一行（10 秒日志 / 状态行）。</summary>
        public string Describe() =>
            "viewDamageFlash=" + ViewDamageFlash +
            " viewHealthBarFlash=" + ViewHealthBarFlash +
            " viewHudNumbers=" + ViewHudNumbers +
            " viewDeathOverlay=" + ViewDeathOverlay +
            " viewFeedbackFailures=" + ViewFeedbackFailures +
            " viewFeedbackSkipped=" + ViewFeedbackSkipped +
            " viewSkippedReason=" + (LastSkipReason ?? "none") +
            " viewFailureReason=" + (LastFailureReason ?? "none") +
            " viewIceWrites=" + ViewIceWrites +
            " viewRespawnClears=" + ViewRespawnClears +
            " viewFrames=" + ViewFrames;
    }

    /// <summary>
    /// 一帧"客户端化身"的可显示状态（只读值类型）。<see cref="HasHealth"/> / <see cref="HasVitals"/>
    /// 为 false 表示该组件这一拍不可观测 —— 调用方必须跳过并写清原因，绝不用默认值顶替。
    /// </summary>
    public readonly struct MirrorViewSourceSample
    {
        public MirrorViewSourceSample(bool hasHealth, float health, string causeOfDeath,
            bool hasVitals, float food, float temperature, float targetTemperature, float level)
        {
            HasHealth = hasHealth;
            Health = health;
            CauseOfDeath = causeOfDeath;
            HasVitals = hasVitals;
            Food = food;
            Temperature = temperature;
            TargetTemperature = targetTemperature;
            Level = level;
        }

        /// <summary>客户端化身的 <c>ComponentHealth</c> 这一拍是否存在。</summary>
        public bool HasHealth { get; }

        /// <summary><c>ComponentHealth.Health</c>（0..1）。</summary>
        public float Health { get; }

        /// <summary><c>ComponentHealth.CauseOfDeath</c>（死亡屏小字用；可空）。</summary>
        public string CauseOfDeath { get; }

        /// <summary>客户端化身的 <c>ComponentVitalStats</c> 这一拍是否存在。</summary>
        public bool HasVitals { get; }

        /// <summary><c>ComponentVitalStats.Food</c>（0..1）。</summary>
        public float Food { get; }

        /// <summary><c>ComponentVitalStats.Temperature</c>（0..24）。</summary>
        public float Temperature { get; }

        /// <summary><c>ComponentVitalStats.m_targetTemperature</c>（温度条档位；0..24）。</summary>
        public float TargetTemperature { get; }

        /// <summary><c>PlayerData.Level</c>（等级文字；只读）。</summary>
        public float Level { get; }

        public string Describe() =>
            "health=" + Health.ToString("F4") + " cause=" + (CauseOfDeath ?? "null") +
            " food=" + Food.ToString("F4") + " temperature=" + Temperature.ToString("F4") +
            " targetTemperature=" + TargetTemperature.ToString("F4") +
            " level=" + Level.ToString("F2");
    }

    /// <summary>一帧的掉血反馈决定（红屏因子 / 血条闪烁）。</summary>
    public readonly struct MirrorViewDamageStep
    {
        public MirrorViewDamageStep(bool damageApplied, bool writeRedout, float redoutFactor,
            int flashCount)
        {
            DamageApplied = damageApplied;
            WriteRedout = writeRedout;
            RedoutFactor = redoutFactor;
            FlashCount = flashCount;
        }

        /// <summary>本拍观测到掉血（= <c>HealthChange &lt; 0</c>）。</summary>
        public bool DamageApplied { get; }

        /// <summary>本拍要写 <c>RedoutFactor</c>（红屏因子仍高于引擎截断）。</summary>
        public bool WriteRedout { get; }

        /// <summary>要写的红屏因子（= 衰减 + 本拍增量的结果）。</summary>
        public float RedoutFactor { get; }

        /// <summary>要传给 <c>HealthBarWidget.Flash</c> 的次数（0 = 不闪）。</summary>
        public int FlashCount { get; }
    }

    /// <summary>一帧的 HUD 显示值决定（只有 <c>Write*</c> 为 true 的项才写控件）。</summary>
    public readonly struct MirrorViewHudWrites
    {
        public MirrorViewHudWrites(bool writeHealthBar, float healthBarValue, bool writeFoodBar,
            float foodBarValue, bool writeTemperatureTexture, int temperatureTextureIndex,
            string temperatureTextureName, bool writeIce, float iceFactor, bool writeLevelLabel,
            int level)
        {
            WriteHealthBar = writeHealthBar;
            HealthBarValue = healthBarValue;
            WriteFoodBar = writeFoodBar;
            FoodBarValue = foodBarValue;
            WriteTemperatureTexture = writeTemperatureTexture;
            TemperatureTextureIndex = temperatureTextureIndex;
            TemperatureTextureName = temperatureTextureName;
            WriteIce = writeIce;
            IceFactor = iceFactor;
            WriteLevelLabel = writeLevelLabel;
            Level = level;
        }

        public bool WriteHealthBar { get; }
        public float HealthBarValue { get; }
        public bool WriteFoodBar { get; }
        public float FoodBarValue { get; }
        public bool WriteTemperatureTexture { get; }
        public int TemperatureTextureIndex { get; }
        public string TemperatureTextureName { get; }
        /// <summary>引擎每拍把 <c>IceFactor</c> 清 0，所以只要 &gt; 0 就每拍写。</summary>
        public bool WriteIce { get; }
        public float IceFactor { get; }
        public bool WriteLevelLabel { get; }
        public int Level { get; }
    }

    /// <summary>一帧的死亡 / 复活边沿决定。</summary>
    public readonly struct MirrorViewDeathStep
    {
        public MirrorViewDeathStep(bool showOverlay, bool keepBlackout, bool clearOverlay,
            string cause, string smallText)
        {
            ShowOverlay = showOverlay;
            KeepBlackout = keepBlackout;
            ClearOverlay = clearOverlay;
            Cause = cause;
            SmallText = smallText;
        }

        /// <summary>本拍刚进入死亡（显示一次死亡大消息）。</summary>
        public bool ShowOverlay { get; }

        /// <summary>死亡期间每拍写 <c>BlackoutFactor = 1</c>（引擎每拍清 0）。</summary>
        public bool KeepBlackout { get; }

        /// <summary>本拍刚复活（清死亡大消息 / 红屏因子归零 / 停止黑屏）。</summary>
        public bool ClearOverlay { get; }

        /// <summary>死因（<c>ComponentHealth.CauseOfDeath</c>；仅 <see cref="ShowOverlay"/> 时有值）。</summary>
        public string Cause { get; }

        /// <summary>死亡大消息小字（仅 <see cref="ShowOverlay"/> 时有值）。</summary>
        public string SmallText { get; }
    }

    /// <summary>
    /// 一帧的完整镜像计划。<see cref="SkipReason"/> / <see cref="FailureReason"/> 非空时其余字段无意义
    /// （调用方只计数 + 限流日志，不写任何控件）。
    /// </summary>
    public readonly struct MirrorViewFramePlan
    {
        public MirrorViewFramePlan(string skipReason, string failureReason, string hudSkipReason,
            MirrorViewDamageStep damage, MirrorViewHudWrites hud, MirrorViewDeathStep death)
        {
            SkipReason = skipReason;
            FailureReason = failureReason;
            HudSkipReason = hudSkipReason;
            Damage = damage;
            Hud = hud;
            Death = death;
        }

        /// <summary>整帧跳过原因（不可观测 / 未接入）。</summary>
        public string SkipReason { get; }

        /// <summary>整帧失败原因（输入非法 —— 保留上一次有效显示）。</summary>
        public string FailureReason { get; }

        /// <summary>只跳过 HUD 的原因（体征不可观测；红屏 / 死亡反馈照常）。</summary>
        public string HudSkipReason { get; }

        public MirrorViewDamageStep Damage { get; }
        public MirrorViewHudWrites Hud { get; }
        public MirrorViewDeathStep Death { get; }

        public bool IsSkipped => SkipReason != null;

        public bool IsFailed => FailureReason != null;

        public static MirrorViewFramePlan Skipped(string reason) =>
            new MirrorViewFramePlan(reason, null, null, default, default, default);

        public static MirrorViewFramePlan Failed(string reason) =>
            new MirrorViewFramePlan(null, reason, null, default, default, default);

        public string Describe() =>
            "skip=" + (SkipReason ?? "none") + " failure=" + (FailureReason ?? "none") +
            " hudSkip=" + (HudSkipReason ?? "none") +
            " damage{applied=" + (Damage.DamageApplied ? "1" : "0") +
            " redout=" + (Damage.WriteRedout ? "1" : "0") + ":" +
            Damage.RedoutFactor.ToString("F4") +
            " flash=" + Damage.FlashCount + "}" +
            " hud{health=" + (Hud.WriteHealthBar ? Hud.HealthBarValue.ToString("F4") : "-") +
            " food=" + (Hud.WriteFoodBar ? Hud.FoodBarValue.ToString("F4") : "-") +
            " tempTex=" + (Hud.WriteTemperatureTexture
                ? Hud.TemperatureTextureIndex.ToString()
                : "-") +
            " ice=" + (Hud.WriteIce ? Hud.IceFactor.ToString("F4") : "-") +
            " level=" + (Hud.WriteLevelLabel ? Hud.Level.ToString() : "-") + "}" +
            " death{show=" + (Death.ShowOverlay ? "1" : "0") +
            " keep=" + (Death.KeepBlackout ? "1" : "0") +
            " clear=" + (Death.ClearOverlay ? "1" : "0") + "}";
    }

    /// <summary>
    /// 第三分屏反馈的**逐帧差分状态机**（纯逻辑，无引擎引用）：
    /// 输入一帧的客户端化身可显示值，输出"这一帧要写哪些显示值与红屏 / 黑屏 / 死亡大消息"。
    ///
    /// 差分口径：
    ///   · 掉血 = 本拍观测到的 <c>Health</c> 相对上一拍下降（与引擎 <c>HealthChange = Health - m_lastHealth</c>
    ///     同一个量，只是跨帧保存）；
    ///   · 红屏因子 = 上一拍因子按 <c>Pow(0.2, dt)</c> 衰减 + 本拍 <c>4f * drop</c>，与引擎同序；
    ///   · 血条 / 食物条 / 温度档位 / 等级文字：值没变就不写（引擎里它们每拍被写，是因为引擎自己的
    ///     Update 在跑；绑定期 Update 被摘掉，值本来就不变，diff-based 等价且不会每拍刷控件）；
    ///   · 死亡边沿：首次观测到 <c>Health &lt;= 0</c> 才显示一次大消息；复活边沿清消息 + 因子归零；
    ///   · 首次观测（绑定后的第一拍）只写当前值，**不**因为"从 0 变到 X"而闪红 / 报死亡（除非本来就已经死了）。
    ///
    /// 本类不做 IO、不持引擎对象、无静态可变状态；一个绑定实例一个 <see cref="Reset"/> 生命周期。
    /// </summary>
    public sealed class MirrorViewFeedbackDiff
    {
        private bool m_seeded;
        private bool m_hudSeeded;
        private float m_lastHealth;
        private bool m_dead;
        private bool m_deathOverlayShown;
        private float m_redFactor;
        private float m_lastHealthBarValue;
        private float m_lastFoodBarValue;
        private int m_lastTemperatureTextureIndex = int.MinValue;
        private float m_lastIceFactor;
        private int m_lastLevel = int.MinValue;

        /// <summary>是否已观测过第一帧（未观测时不产生任何掉血 / 死亡边沿）。</summary>
        public bool Seeded => m_seeded;

        /// <summary>HUD 显示值是否已播种（播种那拍写全部当前值）。</summary>
        public bool HudSeeded => m_hudSeeded;

        /// <summary>上一拍是否处于死亡状态。</summary>
        public bool Dead => m_dead;

        /// <summary>死亡大消息是否已经显示过（复活时清）。</summary>
        public bool DeathOverlayShown => m_deathOverlayShown;

        /// <summary>当前红屏因子（引擎 <c>m_redScreenFactor</c> 的镜像）。</summary>
        public float RedFactor => m_redFactor;

        /// <summary>最后写入的血条值（诊断用）。</summary>
        public float LastHealthBarValue => m_lastHealthBarValue;

        /// <summary>最后写入的食物条值（诊断用）。</summary>
        public float LastFoodBarValue => m_lastFoodBarValue;

        /// <summary>最后写入的温度贴图档位（诊断用；<c>int.MinValue</c> = 还没写过）。</summary>
        public int LastTemperatureTextureIndex => m_lastTemperatureTextureIndex;

        /// <summary>最后观测到的冰冻因子（诊断用）。</summary>
        public float LastIceFactor => m_lastIceFactor;

        /// <summary>最后写入的等级（诊断用；<c>int.MinValue</c> = 还没写过）。</summary>
        public int LastLevel => m_lastLevel;

        /// <summary>解绑 / 重新绑定：清空全部差分状态（下一次 <see cref="Advance"/> 重新播种）。</summary>
        public void Reset()
        {
            m_seeded = false;
            m_hudSeeded = false;
            m_lastHealth = 0f;
            m_dead = false;
            m_deathOverlayShown = false;
            m_redFactor = 0f;
            m_lastHealthBarValue = 0f;
            m_lastFoodBarValue = 0f;
            m_lastTemperatureTextureIndex = int.MinValue;
            m_lastIceFactor = 0f;
            m_lastLevel = int.MinValue;
        }

        /// <summary>
        /// 推进一帧并给出计划。<paramref name="dt"/> 是这一拍的时长（秒，引擎 <c>Time.FrameDuration</c>）；
        /// <paramref name="clientName"/> 只用于死亡大消息的小字。
        /// </summary>
        public MirrorViewFramePlan Advance(in MirrorViewSourceSample sample, float dt,
            string clientName)
        {
            if (!sample.HasHealth) return MirrorViewFramePlan.Skipped("source-health-missing");
            if (!MirrorViewFeedbackMath.IsFinite(sample.Health))
                return MirrorViewFramePlan.Failed("source-health-not-finite");
            if (!MirrorViewFeedbackMath.IsFinite(dt) || dt < 0f) dt = 0f;

            float drop = 0f;
            if (m_seeded && sample.Health < m_lastHealth) drop = m_lastHealth - sample.Health;
            m_seeded = true;
            m_lastHealth = sample.Health;
            bool damageApplied = drop > 0f;

            bool dead = sample.Health <= 0f;
            bool showDeath = dead && !m_deathOverlayShown;
            bool keepBlackout = dead;
            bool clearDeath = !dead && m_dead;
            if (showDeath) m_deathOverlayShown = true;
            if (clearDeath)
            {
                // 复活：死亡表现立刻消失（黑屏因子每拍由引擎清 0，这里只负责不再写），红屏因子归零。
                m_deathOverlayShown = false;
                m_redFactor = 0f;
            }
            m_dead = dead;

            m_redFactor = MirrorViewFeedbackMath.DecayRedFactor(m_redFactor, dt);
            if (damageApplied) m_redFactor += MirrorViewFeedbackMath.RedFactorIncrement(drop);
            bool writeRedout = m_redFactor > MirrorViewFeedbackMath.RedoutCutoff;
            int flashCount = damageApplied
                ? MirrorViewFeedbackMath.HealthBarFlashCount(drop)
                : 0;

            var damage = new MirrorViewDamageStep(damageApplied, writeRedout, m_redFactor,
                flashCount);
            var death = new MirrorViewDeathStep(showDeath, keepBlackout, clearDeath,
                showDeath ? sample.CauseOfDeath : null,
                showDeath
                    ? MirrorViewDeathText.BuildSmallText(sample.CauseOfDeath, clientName)
                    : null);

            string hudSkipReason = null;
            bool writeHealth = false, writeFood = false, writeTemperature = false,
                writeIce = false, writeLevel = false;
            float healthBarValue = 0f, foodBarValue = 0f, iceFactor = 0f;
            int temperatureIndex = 0, level = 0;
            string temperatureName = null;
            if (!sample.HasVitals)
            {
                // 体征不可观测：红屏 / 死亡反馈照常，只跳过 HUD 数值（明确计数，不拿默认值顶替）。
                hudSkipReason = "source-vitals-missing";
            }
            else
            {
                if (!PlayerVitalsPresentation.TryCreate(dead ? 0f : sample.Health, sample.Food,
                        sample.Temperature, sample.TargetTemperature, dead,
                        out PlayerVitalsHudValues values))
                    return MirrorViewFramePlan.Failed("source-vitals-not-finite");
                if (!MirrorViewFeedbackMath.IsFinite(sample.Level))
                    return MirrorViewFramePlan.Failed("source-level-not-finite");
                bool seed = !m_hudSeeded;
                int levelNow = MirrorViewFeedbackMath.LevelOf(sample.Level);
                if (seed ||
                    !MirrorViewFeedbackMath.Near(values.HealthBarValue, m_lastHealthBarValue))
                {
                    writeHealth = true;
                    healthBarValue = values.HealthBarValue;
                    m_lastHealthBarValue = values.HealthBarValue;
                }
                if (seed ||
                    !MirrorViewFeedbackMath.Near(values.FoodBarValue, m_lastFoodBarValue))
                {
                    writeFood = true;
                    foodBarValue = values.FoodBarValue;
                    m_lastFoodBarValue = values.FoodBarValue;
                }
                if (seed || values.TemperatureTextureIndex != m_lastTemperatureTextureIndex)
                {
                    writeTemperature = true;
                    temperatureIndex = values.TemperatureTextureIndex;
                    temperatureName = values.TemperatureTextureName;
                    m_lastTemperatureTextureIndex = temperatureIndex;
                }
                // 引擎每拍清 0：只要还有冰冻覆盖就必须每拍写；为 0 时不写（引擎已经是 0）。
                if (values.IceFactor > MirrorViewFeedbackMath.HudEpsilon)
                {
                    writeIce = true;
                    iceFactor = values.IceFactor;
                }
                m_lastIceFactor = values.IceFactor;
                if (seed || levelNow != m_lastLevel)
                {
                    writeLevel = true;
                    level = levelNow;
                    m_lastLevel = levelNow;
                }
                m_hudSeeded = true;
            }

            var hud = new MirrorViewHudWrites(writeHealth, healthBarValue, writeFood,
                foodBarValue, writeTemperature, temperatureIndex, temperatureName, writeIce,
                iceFactor, writeLevel, level);
            return new MirrorViewFramePlan(null, null, hudSkipReason, damage, hud, death);
        }

        /// <summary>可审计的一行（10 秒日志 / 状态行）。</summary>
        public string Describe() =>
            "seeded=" + (m_seeded ? "1" : "0") + " hud=" + (m_hudSeeded ? "1" : "0") +
            " dead=" + (m_dead ? "1" : "0") +
            " deathOverlay=" + (m_deathOverlayShown ? "1" : "0") +
            " red=" + m_redFactor.ToString("F4") +
            " healthBar=" + m_lastHealthBarValue.ToString("F4") +
            " foodBar=" + m_lastFoodBarValue.ToString("F4") +
            " tempTex=" + (m_lastTemperatureTextureIndex == int.MinValue
                ? "-"
                : m_lastTemperatureTextureIndex.ToString()) +
            " ice=" + m_lastIceFactor.ToString("F4") +
            " level=" + (m_lastLevel == int.MinValue ? "-" : m_lastLevel.ToString());
    }
}
