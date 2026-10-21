using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Players;
using V = SuMultiplayer.Protocol.Vitals;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 线路 / 领域边界上的硬上限（编码与解码都按同一组常量校验，绝不"发出去再说"）。
    /// </summary>
    public static class PlayerVitalsLimits
    {
        /// <summary>
        /// 一份上报最多几条条目：主机最多 4 个本机角色 + 4 个客户端 × 4 个化身 = 20；
        /// 留 4 条余量给以后的调试端（与位置批的 24 同一口径）。
        /// </summary>
        public const int MaxReportEntries = 24;

        /// <summary>一个角色的 <c>m_satiation</c> 最多几条（引擎里按"吃过多少种食物"累积）。</summary>
        public const int MaxSatiationEntries = 32;

        /// <summary>死因文本上限（<c>ComponentHealth.CauseOfDeath</c> 是引擎内部短文本）。</summary>
        public const int MaxCauseLength = 64;

        /// <summary>病程计时上限（秒）：引擎的流感 / 食物中毒都是 900 秒量级，3600 秒是硬上界。</summary>
        public const float MaxConditionSeconds = 3600f;

        /// <summary>单条上报 / 事件消息的字节上限（有界：最坏 24 条 × 32 条 satiation 也远小于它）。</summary>
        public const int MaxPayloadBytes = 8192;

        /// <summary>两条状态被认定为"没有变化"的浮点容差（生命 / 体征）。</summary>
        public const float ValueEpsilon = 0.001f;

        /// <summary>病程计时的"没有变化"容差（秒）。</summary>
        public const float ConditionEpsilon = 0.01f;

        /// <summary>
        /// 角色等级上限（<c>PlayerData.Level</c>，vitals.proto 字段 21）：0 = 对端没有携带
        /// （老版本主机），1..<see cref="MaxLevel"/> = 主机权威值，其余一律拒绝。
        /// </summary>
        public const float MaxLevel = 1000f;

        /// <summary>两条等级被认定为"没有变化"的浮点容差。</summary>
        public const float LevelEpsilon = 0.001f;

        /// <summary>
        /// 一份表现事件消息最多几条（vitals.proto <c>PLAYER_EFFECT_EVENTS</c> = 109）。
        /// 主机每帧最多为一个 4 槽客户端产出几条（咳嗽 / 喷嚏 / 恶心 / 呕吐 / 着火 / 熄灭），
        /// 16 条足够覆盖 4 个槽位的一帧；超出的条目显式计 <c>dropped</c>，不静默截断。
        /// </summary>
        public const int MaxEffectEntries = 16;

        /// <summary>表现事件里的引擎资源名上限（<c>Audio/Creatures/HumanCough</c> 量级）。</summary>
        public const int MaxEffectSoundLength = 64;

        /// <summary>
        /// 冻结阶段上限（vitals.proto 字段 23）：0 none / 1 chilly / 2 cold / 3 hypothermia /
        /// 4 frozen。越界一律拒绝（不夹紧：夹紧会把"未知的新阶段"悄悄当成已知的阶段）。
        /// </summary>
        public const uint MaxFreezeStage = 4;
    }

    /// <summary>
    /// 主机权威的**冻结进度 / 阶段**纯策略（不引用 Game，自检可直接编译）。
    ///
    /// 引擎事实（为什么客户端不能自己算）：
    ///   · 冰冻屏幕覆盖 <c>ComponentScreenOverlays.IceFactor</c> 由
    ///     <c>ComponentVitalStats.UpdateTemperature</c> 每帧写成
    ///     <c>Saturate(1 - Temperature / 6)</c>（<c>ComponentVitalStats.cs:579</c>）；
    ///   · 冷阶段阈值（同方法 <c>:532-557</c>）：&lt;= 0 → <c>Injure(1, "Froze to death")</c>；
    ///     &lt; 3 → 每 10 秒 <c>Injure(0.05, "Hypothermia")</c>；&lt; 6 / &lt; 8 → 提示 + 温度条闪。
    ///   · 客户端的 <c>ComponentVitalStats.Update</c> 被摘掉，Temperature 只由 95 上报写入
    ///     （5 Hz）：如果客户端继续自己从温度推导覆盖强度，就会在两次上报之间**跳变**，
    ///     并且与主机第三分屏（读主机连续温度）不在同一时刻冻上（实机问题 1/2）。
    /// 所以：主机用自己模拟的 Temperature 算 progress/stage 随 95 下发（字段 22/23），
    /// 客户端**只跟随**（插值），不再本地推导。老版本主机不带字段时（proto3 默认 0），
    /// 只要权威 temperature &lt; 6 就能判定"字段缺失"，此时用主机权威温度推导，
    /// 显式计 <c>freezeLegacy</c> —— 仍然是主机值，不是本地模拟。
    /// </summary>
    public static class PlayerFreezePolicy
    {
        /// <summary>引擎冰覆盖的温度跨度（<c>Saturate(1 - Temperature / 6)</c>）。</summary>
        public const float IceTemperatureSpan = 6f;

        /// <summary>0 = 不冷（Temperature &gt;= 8）。</summary>
        public const uint StageNone = 0;

        /// <summary>1 = 微冷（Temperature &lt; 8，引擎提示 "You feel a bit chilly"）。</summary>
        public const uint StageChilly = 1;

        /// <summary>2 = 冷（Temperature &lt; 6，引擎提示 "getting cold" + 冰覆盖开始出现）。</summary>
        public const uint StageCold = 2;

        /// <summary>3 = 低体温（Temperature &lt; 3，每 10 秒掉 0.05 血）。</summary>
        public const uint StageHypothermia = 3;

        /// <summary>4 = 冻死（Temperature &lt;= 0，引擎直接 <c>Injure(1, "Froze to death")</c>）。</summary>
        public const uint StageFrozen = 4;

        public static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>引擎同款冰冻覆盖强度（0..1）；非有限值返回 false（调用方明确失败）。</summary>
        public static bool TryProgress(float temperature, out float progress)
        {
            progress = 0f;
            if (!IsFinite(temperature)) return false;
            progress = MathUtils.Saturate(1f - temperature / IceTemperatureSpan);
            return true;
        }

        /// <summary>引擎同款冷阶段（严格小于阈值，与 <c>UpdateTemperature</c> 的分支一致）。</summary>
        public static bool TryStage(float temperature, out uint stage)
        {
            stage = StageNone;
            if (!IsFinite(temperature)) return false;
            if (temperature <= 0f) stage = StageFrozen;
            else if (temperature < 3f) stage = StageHypothermia;
            else if (temperature < 6f) stage = StageCold;
            else if (temperature < 8f) stage = StageChilly;
            else stage = StageNone;
            return true;
        }

        /// <summary>主机抓取口径：进度与阶段来自同一份 Temperature。</summary>
        public static bool TryCapture(float temperature, out float progress, out uint stage)
        {
            progress = 0f;
            stage = StageNone;
            if (!TryProgress(temperature, out progress)) return false;
            return TryStage(temperature, out stage);
        }

        /// <summary>阶段 → 稳定短名（日志 / 状态行用）；未知阶段返回 false。</summary>
        public static bool TryDescribeStage(uint stage, out string name)
        {
            switch (stage)
            {
                case StageNone: name = "none"; return true;
                case StageChilly: name = "chilly"; return true;
                case StageCold: name = "cold"; return true;
                case StageHypothermia: name = "hypothermia"; return true;
                case StageFrozen: name = "frozen"; return true;
                default: name = null; return false;
            }
        }

        /// <summary>
        /// 接收口径：主机携带的字段优先；两者都是 0 且权威 temperature &lt; 6 = 老版本主机
        /// 没有携带（<paramref name="legacy"/> = true），用权威温度推导。
        /// 任何非法输入返回 false + 原因（绝不夹紧 / 绝不猜）。
        /// </summary>
        public static bool TryResolve(float freezeProgress, uint freezeStage, float temperature,
            out float progress, out uint stage, out bool legacy, out string error)
        {
            progress = 0f;
            stage = StageNone;
            legacy = false;
            error = null;
            if (!IsFinite(freezeProgress))
            {
                error = "freeze_progress 不是有限数（" + freezeProgress + "）";
                return false;
            }
            if (freezeProgress < 0f || freezeProgress > 1f)
            {
                error = "freeze_progress = " + freezeProgress.ToString("0.###") + " 越界（0..1）";
                return false;
            }
            if (freezeStage > PlayerVitalsLimits.MaxFreezeStage)
            {
                error = "freeze_stage = " + freezeStage + " 越界（0.." +
                    PlayerVitalsLimits.MaxFreezeStage + "）";
                return false;
            }
            bool carried = freezeStage != StageNone || freezeProgress > 0f;
            if (carried)
            {
                progress = freezeProgress;
                stage = freezeStage;
                if (freezeProgress > 0f && stage < StageCold)
                {
                    error = "freeze_progress = " + freezeProgress.ToString("0.###") +
                        " 但 freeze_stage = " + stage + "（进度 > 0 必须处于 cold 及以下阶段）";
                    return false;
                }
                return true;
            }
            if (!IsFinite(temperature))
            {
                error = "主机没有携带冻结字段，且 temperature 不是有限数（无法判定是否不冷）";
                return false;
            }
            if (temperature >= IceTemperatureSpan) return true;   // 不冷：0/0 正确
            legacy = true;
            return TryCapture(temperature, out progress, out stage);
        }
    }

    /// <summary>一条 <c>ComponentVitalStats.m_satiation</c> 记录（方块 contents → 累积值）。</summary>
    public readonly struct PlayerSatiation : IEquatable<PlayerSatiation>
    {
        public PlayerSatiation(int contents, float value)
        {
            Contents = contents;
            Value = value;
        }

        /// <summary>方块 contents（引擎 <c>Terrain.ExtractContents</c> 的口径；必须 &gt; 0）。</summary>
        public int Contents { get; }

        public float Value { get; }

        public bool Equals(PlayerSatiation other) =>
            Contents == other.Contents && Value.Equals(other.Value);

        public override bool Equals(object obj) => obj is PlayerSatiation other && Equals(other);

        public override int GetHashCode() => (Contents * 397) ^ Value.GetHashCode();

        public override string ToString() => Contents + "=" + Value.ToString("0.###");
    }

    /// <summary>
    /// 一个角色在**某一端**的完整生命 / 体征 / 死亡状态。两侧都用同一个结构：
    ///   · 主机（唯一权威）抓取它模拟的**每个**角色并下发；
    ///   · 客户端把它写进本机玩家 / 远端化身的引擎组件（绝不在本端重新模拟伤害 / 饥饿 / 死亡，
    ///     本机玩家的生命 / 体征 / 睡眠 / 病程 / 火焰 Update 会被摘掉）。
    ///
    /// 字段口径（全部来自引擎事实）：
    ///   Health / Air            = <c>ComponentHealth.Health</c> / <c>Air</c>
    ///   Food / Stamina / Sleep  = <c>ComponentVitalStats</c> 的同名属性（0..1）
    ///   Temperature             = <c>ComponentVitalStats.Temperature</c>（引擎夹紧 0..24）
    ///   Wetness                 = <c>ComponentVitalStats.Wetness</c>（0..1）
    ///   TargetTemperature       = <c>ComponentVitalStats.m_targetTemperature</c>（环境目标温度）
    ///   FluDuration / FluOnset  = <c>ComponentFlu.m_fluDuration</c> / <c>m_fluOnset</c>
    ///   SicknessDuration        = <c>ComponentSickness.m_sicknessDuration</c>
    ///   FireDuration            = <c>ComponentOnFire.m_fireDuration</c>
    ///   Sleeping                = <c>ComponentSleep.IsSleeping</c>（决定活着时的躺下姿势）
    ///   SleepStartTime / SleepElapsed / AllowManualWake / ForcedSleep
    ///                           = 睡眠表现权威（见 vitals.proto 17..20）：客户端
    ///                             <c>ComponentSleep.Update</c> 被摘掉后，黑屏淡入 / Zzz /
    ///                             "Tap to wake up early" / "You fell unconscious from lack of
    ///                             sleep" / 控件隐藏全部由这四个字段驱动；强制睡眠没有唤醒路径。
    ///   Dead                    = <c>Health &lt;= 0</c>（与健康值必须一致，编解码会校验）
    ///   CauseOfDeath            = <c>ComponentHealth.CauseOfDeath</c>（死亡时才有意义）
    ///   Satiation               = <c>ComponentVitalStats.m_satiation</c> 的有界副本
    ///   Position                = 主机权威角色的 <c>ComponentBody.Position</c>（**只**用于复活事件的落点，
    ///                             普通上报不序列化它 —— 连续运动仍由 position.proto 的流负责）
    ///
    /// Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update/Load
    /// Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Update/Load
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerHealthAndIngress.cs:94-105
    ///         （CaptureAuthoritativePlayerState 的字段集）
    /// </summary>
    public readonly struct PlayerVitalsSnapshot
    {
        private readonly PlayerSatiation[] m_satiation;
        private readonly string m_causeOfDeath;

        public PlayerVitalsSnapshot(PlayerKey key, uint revision, float health, float air, float food,
            float stamina, float sleep, float temperature, float wetness, float targetTemperature,
            float fluDuration, float fluOnset, float sicknessDuration, float fireDuration,
            bool sleeping, string causeOfDeath, IReadOnlyList<PlayerSatiation> satiation,
            Vector3 position, double sleepStartTime = 0.0, float sleepElapsed = 0f,
            bool allowManualWake = false, bool forcedSleep = false, float level = 0f,
            float freezeProgress = 0f, uint freezeStage = 0)
        {
            Key = key;
            Revision = revision;
            Health = health;
            Air = air;
            Food = food;
            Stamina = stamina;
            Sleep = sleep;
            Temperature = temperature;
            Wetness = wetness;
            TargetTemperature = targetTemperature;
            FluDuration = fluDuration;
            FluOnset = fluOnset;
            SicknessDuration = sicknessDuration;
            FireDuration = fireDuration;
            Sleeping = sleeping;
            m_causeOfDeath = causeOfDeath ?? string.Empty;
            m_satiation = CopySatiation(satiation);
            Position = position;
            SleepStartTime = sleepStartTime;
            SleepElapsed = sleepElapsed;
            AllowManualWake = allowManualWake;
            ForcedSleep = forcedSleep;
            Level = level;
            FreezeProgress = freezeProgress;
            FreezeStage = freezeStage;
        }

        public PlayerKey Key { get; }

        /// <summary>主机的每键状态版本（&gt; 0；只有变化 / 死亡 / 复活时递增）。</summary>
        public uint Revision { get; }

        public float Health { get; }
        public float Air { get; }
        public float Food { get; }
        public float Stamina { get; }
        public float Sleep { get; }
        public float Temperature { get; }
        public float Wetness { get; }
        public float TargetTemperature { get; }
        public float FluDuration { get; }
        public float FluOnset { get; }
        public float SicknessDuration { get; }
        public float FireDuration { get; }
        public bool Sleeping { get; }

        /// <summary>
        /// <c>ComponentSleep.m_sleepStartTime</c>（主机 <c>TotalElapsedGameTime</c>；0 = 不在睡 /
        /// 老版本主机没有睡眠权威字段）。客户端用它识别"本次睡眠期"，主机用它复检唤醒门槛。
        /// </summary>
        public double SleepStartTime { get; }

        /// <summary>
        /// 抓取那一刻的睡眠时长（秒）= 主机 <c>TotalElapsedGameTime - SleepStartTime</c>。
        /// **绝对起点**语义：客户端可以用同步过的世界时钟继续外推，不依赖跨端时钟对齐。
        /// 它每拍都变，因此**不**作为 <see cref="HasMeaningfulChangeFrom"/> 的变化判据。
        /// </summary>
        public float SleepElapsed { get; }

        /// <summary><c>ComponentSleep.m_allowManualWakeUp</c>（强制睡眠 = false）。</summary>
        public bool AllowManualWake { get; }

        /// <summary>
        /// <c>Sleeping &amp;&amp; !AllowManualWake</c>：引擎 <c>Sleep(allowManualWakeup: false)</c>
        /// 的强制睡眠 / 昏迷，客户端不提供点击唤醒路径。
        /// </summary>
        public bool ForcedSleep { get; }

        /// <summary>
        /// 主机权威的 <c>PlayerData.Level</c>（vitals.proto 字段 21）。0 = 对端没有携带
        /// （老版本主机）；1..<see cref="PlayerVitalsLimits.MaxLevel"/> = 权威值。客户端
        /// **不得**把 0 写进本机等级，是否 / 何时写入由 <see cref="PlayerLevelPolicy.Decide"/>
        /// 判定（死亡快照一律推迟到复活后）。
        /// </summary>
        public float Level { get; }

        /// <summary>
        /// 主机权威的**冻结进度**（vitals.proto 字段 22，0..1）：主机用自己模拟的
        /// <c>ComponentVitalStats.Temperature</c> 算出的引擎同款冰冻覆盖强度
        /// （<c>Saturate(1 - Temperature / 6)</c>，<c>ComponentVitalStats.cs:579</c>）。
        /// 客户端只按这个值做表现（连续插值），**不**再自己从温度推导。
        /// 0 + <c>temperature &lt; 6</c> = 老版本主机没有携带该字段（客户端计
        /// <c>freezeLegacy</c>，仍用主机权威 temperature 推导，绝不本地模拟）。
        /// </summary>
        public float FreezeProgress { get; }

        /// <summary>
        /// 主机权威的冻结阶段（vitals.proto 字段 23）：0 none / 1 chilly(&lt;8) /
        /// 2 cold(&lt;6) / 3 hypothermia(&lt;3) / 4 frozen(&lt;=0)。阶段变化只做诊断 /
        /// 计数（<c>freezeStageChanges</c>），视觉强度由 <see cref="FreezeProgress"/> 决定。
        /// </summary>
        public uint FreezeStage { get; }

        /// <summary>睡眠状态的领域形态（校验 / 表现推进 / 唤醒门槛共用）。</summary>
        public PlayerSleepState SleepState =>
            new PlayerSleepState(Sleeping, Dead, AllowManualWake, ForcedSleep, SleepStartTime,
                SleepElapsed);

        /// <summary>死亡 = <c>health &lt;= 0</c>；与 <see cref="Health"/> 必须一致（校验过的快照）。</summary>
        public bool Dead => Health <= 0f;

        public string CauseOfDeath => m_causeOfDeath ?? string.Empty;

        public IReadOnlyList<PlayerSatiation> Satiation =>
            m_satiation ?? Array.Empty<PlayerSatiation>();

        /// <summary>
        /// 主机权威角色的身体坐标。**不参与普通上报的线格式**（只由复活事件携带）；
        /// 解码出来的快照这里是 <c>Vector3.Zero</c>。
        /// </summary>
        public Vector3 Position { get; }

        public PlayerVitalsSnapshot WithKey(PlayerKey key) =>
            new PlayerVitalsSnapshot(key, Revision, Health, Air, Food, Stamina, Sleep, Temperature,
                Wetness, TargetTemperature, FluDuration, FluOnset, SicknessDuration, FireDuration,
                Sleeping, CauseOfDeath, m_satiation, Position, SleepStartTime, SleepElapsed,
                AllowManualWake, ForcedSleep, Level, FreezeProgress, FreezeStage);

        public PlayerVitalsSnapshot WithRevision(uint revision) =>
            new PlayerVitalsSnapshot(Key, revision, Health, Air, Food, Stamina, Sleep, Temperature,
                Wetness, TargetTemperature, FluDuration, FluOnset, SicknessDuration, FireDuration,
                Sleeping, CauseOfDeath, m_satiation, Position, SleepStartTime, SleepElapsed,
                AllowManualWake, ForcedSleep, Level, FreezeProgress, FreezeStage);

        /// <summary>
        /// 只替换生命值（对账修正用：其余字段保持从化身读到的现值，绝不凭空造值）。
        /// <c>Dead</c> 由生命值推导，所以修正后的快照里两者依然一致。
        /// </summary>
        public PlayerVitalsSnapshot WithHealth(float health) =>
            new PlayerVitalsSnapshot(Key, Revision, health, Air, Food, Stamina, Sleep, Temperature,
                Wetness, TargetTemperature, FluDuration, FluOnset, SicknessDuration, FireDuration,
                Sleeping, CauseOfDeath, m_satiation, Position, SleepStartTime, SleepElapsed,
                AllowManualWake, ForcedSleep, Level, FreezeProgress, FreezeStage);

        private static PlayerSatiation[] CopySatiation(IReadOnlyList<PlayerSatiation> source)
        {
            if (source == null || source.Count == 0) return Array.Empty<PlayerSatiation>();
            var copy = new PlayerSatiation[source.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
            return copy;
        }

        /// <summary>浮点全部有限（NaN 从协议进引擎会让物理 / 体征全部失效）。</summary>
        public bool IsFinite =>
            IsFiniteValue(Health) && IsFiniteValue(Air) && IsFiniteValue(Food) &&
            IsFiniteValue(Stamina) && IsFiniteValue(Sleep) && IsFiniteValue(Temperature) &&
            IsFiniteValue(Wetness) && IsFiniteValue(TargetTemperature) &&
            IsFiniteValue(FluDuration) && IsFiniteValue(FluOnset) &&
            IsFiniteValue(SicknessDuration) && IsFiniteValue(FireDuration) &&
            IsFiniteDouble(SleepStartTime) && IsFiniteValue(SleepElapsed) &&
            IsFiniteValue(Level) && IsFiniteValue(FreezeProgress) &&
            IsFiniteValue(Position.X) && IsFiniteValue(Position.Y) && IsFiniteValue(Position.Z);

        public static bool IsFiniteValue(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static bool IsFiniteDouble(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>
        /// 值域 + 语义校验。任何一条不满足都必须**明确失败**（调用方计数 + 限流日志），
        /// 绝不夹紧后照用（夹紧会把"对方死亡"悄悄变成"1 点血"）。
        /// </summary>
        public bool TryValidate(out string reason)
        {
            reason = null;
            if (!Key.IsValid)
            {
                reason = "玩家键 " + Key + " 非法（clientId >= 0、槽位 0.." + PlayerKey.MaxSlot + "）";
                return false;
            }
            if (Revision == 0)
            {
                reason = "玩家键 " + Key + " 的 revision = 0（必须 > 0）";
                return false;
            }
            if (!IsFinite)
            {
                reason = "玩家键 " + Key + " 的状态含 NaN/Inf";
                return false;
            }
            if (!InRange(Health, 0f, 1f)) return Fail("health", Health, "0..1", out reason);
            if (!InRange(Air, 0f, 1f)) return Fail("air", Air, "0..1", out reason);
            if (!InRange(Food, 0f, 1f)) return Fail("food", Food, "0..1", out reason);
            if (!InRange(Stamina, 0f, 1f)) return Fail("stamina", Stamina, "0..1", out reason);
            if (!InRange(Sleep, 0f, 1f)) return Fail("sleep", Sleep, "0..1", out reason);
            if (!InRange(Temperature, 0f, 24f))
                return Fail("temperature", Temperature, "0..24", out reason);
            if (!InRange(Wetness, 0f, 1f)) return Fail("wetness", Wetness, "0..1", out reason);
            if (TargetTemperature < -100f || TargetTemperature > 100f)
                return Fail("target_temperature", TargetTemperature, "-100..100", out reason);
            if (!InRange(FluDuration, 0f, PlayerVitalsLimits.MaxConditionSeconds))
                return Fail("flu_duration", FluDuration, "0.." + PlayerVitalsLimits.MaxConditionSeconds,
                    out reason);
            if (!InRange(FluOnset, 0f, PlayerVitalsLimits.MaxConditionSeconds))
                return Fail("flu_onset", FluOnset, "0.." + PlayerVitalsLimits.MaxConditionSeconds,
                    out reason);
            if (!InRange(SicknessDuration, 0f, PlayerVitalsLimits.MaxConditionSeconds))
                return Fail("sickness_duration", SicknessDuration,
                    "0.." + PlayerVitalsLimits.MaxConditionSeconds, out reason);
            if (!InRange(FireDuration, 0f, PlayerVitalsLimits.MaxConditionSeconds))
                return Fail("fire_duration", FireDuration,
                    "0.." + PlayerVitalsLimits.MaxConditionSeconds, out reason);
            // 等级（append-only 21）：0 = 对端没有携带；1..1000 = 权威值。负数 / 超上限一律拒绝，
            // 不夹紧（否则"对手 9999 级"会悄悄变成 1000 级）。NaN / Inf 已由 IsFinite 拦下。
            if (!InRange(Level, 0f, PlayerVitalsLimits.MaxLevel))
                return Fail("level", Level, "0.." + PlayerVitalsLimits.MaxLevel, out reason);
            // 冻结表现（append-only 22..23）：进度 0..1、阶段 0..4；进度 > 0 只可能出现在
            // 温度 < 6 的"cold"及以下阶段（引擎 Saturate(1-T/6)），所以 stage 不能是 0。
            if (!InRange(FreezeProgress, 0f, 1f))
                return Fail("freeze_progress", FreezeProgress, "0..1", out reason);
            if (FreezeStage > PlayerVitalsLimits.MaxFreezeStage)
            {
                reason = "玩家键 " + Key + " 的 freeze_stage = " + FreezeStage + " 越界（合法 0.." +
                    PlayerVitalsLimits.MaxFreezeStage + "）";
                return false;
            }
            if (FreezeProgress > PlayerVitalsLimits.ValueEpsilon &&
                FreezeStage < PlayerFreezePolicy.StageCold)
            {
                reason = "玩家键 " + Key + " 的 freeze_progress = " +
                    FreezeProgress.ToString("0.###") + " 但 freeze_stage = " + FreezeStage +
                    "（进度 > 0 必须处于 cold 及以下阶段）";
                return false;
            }
            // 冻结进度必须与主机权威温度自洽（同一份快照里两个字段来自同一次抓取）：
            // 温度 >= 6 时引擎覆盖必为 0；温度 < 6 时进度必须等于引擎口径（容差 1e-3）。
            if (InRange(Temperature, 0f, 24f) &&
                Temperature >= PlayerFreezePolicy.IceTemperatureSpan)
            {
                if (FreezeProgress > PlayerVitalsLimits.ValueEpsilon)
                {
                    reason = "玩家键 " + Key + " 的 temperature = " +
                        Temperature.ToString("0.###") + " >= 6 但 freeze_progress = " +
                        FreezeProgress.ToString("0.###") + "（引擎 IceFactor 此时必为 0）";
                    return false;
                }
            }
            else if (InRange(Temperature, 0f, 24f) && FreezeProgress > 0f)
            {
                float expected = MathUtils.Saturate(1f -
                    Temperature / PlayerFreezePolicy.IceTemperatureSpan);
                if (Math.Abs(expected - FreezeProgress) > PlayerVitalsLimits.ValueEpsilon)
                {
                    reason = "玩家键 " + Key + " 的 freeze_progress = " +
                        FreezeProgress.ToString("0.###") + " 与 temperature = " +
                        Temperature.ToString("0.###") + " 的引擎口径 " +
                        expected.ToString("0.###") + " 不一致";
                    return false;
                }
            }
            // 睡眠表现权威字段（append-only 17..20）：睡眠中必须自洽（forced == !allow、
            // 起始时 > 0 才有表现权威）；醒着时不得带 forced / elapsed / start。
            // 老版本主机不发 17..20（start=0、elapsed=0、forced=false）仍然是合法状态。
            if (!SleepState.TryValidate(out string sleepError))
            {
                reason = "玩家键 " + Key + " 的睡眠状态非法：" + sleepError;
                return false;
            }
            if (CauseOfDeath.Length > PlayerVitalsLimits.MaxCauseLength)
            {
                reason = "玩家键 " + Key + " 的死因长度 " + CauseOfDeath.Length + " 超过上限 " +
                    PlayerVitalsLimits.MaxCauseLength;
                return false;
            }
            if (m_satiation != null && m_satiation.Length > PlayerVitalsLimits.MaxSatiationEntries)
            {
                reason = "玩家键 " + Key + " 的 satiation 条数 " + m_satiation.Length + " 超过上限 " +
                    PlayerVitalsLimits.MaxSatiationEntries;
                return false;
            }
            if (m_satiation != null)
            {
                for (int i = 0; i < m_satiation.Length; i++)
                {
                    PlayerSatiation item = m_satiation[i];
                    if (item.Contents <= 0)
                    {
                        reason = "玩家键 " + Key + " 的 satiation[" + i + "] contents = " +
                            item.Contents + "（必须 > 0）";
                        return false;
                    }
                    if (!InRange(item.Value, 0.0001f, 1000f))
                    {
                        reason = "玩家键 " + Key + " 的 satiation[" + i + "] value = " +
                            item.Value.ToString("0.###") + "（合法 0..1000）";
                        return false;
                    }
                    for (int j = i + 1; j < m_satiation.Length; j++)
                    {
                        if (m_satiation[j].Contents != item.Contents) continue;
                        reason = "玩家键 " + Key + " 的 satiation 有重复 contents=" + item.Contents;
                        return false;
                    }
                }
            }
            return true;
        }

        private bool Fail(string field, float value, string range, out string reason)
        {
            reason = "玩家键 " + Key + " 的 " + field + " = " + value.ToString("0.####") +
                " 越界（合法 " + range + "）";
            return false;
        }

        private static bool InRange(float value, float min, float max) => value >= min && value <= max;

        /// <summary>
        /// 与上一份**已发送 / 已应用**的状态相比是否有实质变化。用于把上报做成"变化驱动"：
        /// 没有变化就只按心跳发（避免 5 Hz 空包），有变化立刻发。
        /// </summary>
        public bool HasMeaningfulChangeFrom(in PlayerVitalsSnapshot previous) =>
            HasMeaningfulChangeFrom(previous, out _);

        public bool HasMeaningfulChangeFrom(in PlayerVitalsSnapshot previous, out string what)
        {
            what = null;
            if (Dead != previous.Dead)
            {
                what = "dead";
                return true;
            }
            if (Sleeping != previous.Sleeping)
            {
                what = "sleeping";
                return true;
            }
            if (Sleeping)
            {
                // 强制 / 手动唤醒权限变化与新的睡眠期必须立刻发出；
                // SleepElapsed 每拍都在涨（客户端用世界时钟外推），**不算**变化 —— 否则
                // 睡觉期间会永远按 5 Hz 发上报。
                if (ForcedSleep != previous.ForcedSleep)
                {
                    what = "sleep_forced";
                    return true;
                }
                if (SleepStartTime != previous.SleepStartTime)
                {
                    what = "sleep_episode";
                    return true;
                }
            }
            if (Changed(Health, previous.Health, PlayerVitalsLimits.ValueEpsilon)) { what = "health"; return true; }
            if (Changed(Air, previous.Air, PlayerVitalsLimits.ValueEpsilon)) { what = "air"; return true; }
            if (Changed(Food, previous.Food, PlayerVitalsLimits.ValueEpsilon)) { what = "food"; return true; }
            if (Changed(Stamina, previous.Stamina, PlayerVitalsLimits.ValueEpsilon)) { what = "stamina"; return true; }
            if (Changed(Sleep, previous.Sleep, PlayerVitalsLimits.ValueEpsilon)) { what = "sleep"; return true; }
            if (Changed(Temperature, previous.Temperature, PlayerVitalsLimits.ValueEpsilon)) { what = "temperature"; return true; }
            if (Changed(Wetness, previous.Wetness, PlayerVitalsLimits.ValueEpsilon)) { what = "wetness"; return true; }
            if (Changed(TargetTemperature, previous.TargetTemperature, PlayerVitalsLimits.ValueEpsilon))
            {
                what = "target_temperature";
                return true;
            }
            if (Changed(FluDuration, previous.FluDuration, PlayerVitalsLimits.ConditionEpsilon)) { what = "flu_duration"; return true; }
            if (Changed(FluOnset, previous.FluOnset, PlayerVitalsLimits.ConditionEpsilon)) { what = "flu_onset"; return true; }
            if (Changed(SicknessDuration, previous.SicknessDuration, PlayerVitalsLimits.ConditionEpsilon))
            {
                what = "sickness_duration";
                return true;
            }
            if (Changed(FireDuration, previous.FireDuration, PlayerVitalsLimits.ConditionEpsilon)) { what = "fire_duration"; return true; }
            if (Changed(Level, previous.Level, PlayerVitalsLimits.LevelEpsilon)) { what = "level"; return true; }
            if (Changed(FreezeProgress, previous.FreezeProgress,
                    PlayerVitalsLimits.ValueEpsilon)) { what = "freeze_progress"; return true; }
            if (FreezeStage != previous.FreezeStage) { what = "freeze_stage"; return true; }
            if (!string.Equals(CauseOfDeath, previous.CauseOfDeath, StringComparison.Ordinal))
            {
                what = "cause_of_death";
                return true;
            }
            if (!SatiationEquals(m_satiation, previous.m_satiation))
            {
                what = "satiation";
                return true;
            }
            return false;
        }

        private static bool Changed(float left, float right, float epsilon) =>
            Math.Abs(left - right) > epsilon;

        private static bool SatiationEquals(PlayerSatiation[] left, PlayerSatiation[] right)
        {
            int leftCount = left?.Length ?? 0;
            int rightCount = right?.Length ?? 0;
            if (leftCount != rightCount) return false;
            if (leftCount == 0) return true;
            for (int i = 0; i < leftCount; i++)
            {
                if (left[i].Contents != right[i].Contents) return false;
                if (Math.Abs(left[i].Value - right[i].Value) > PlayerVitalsLimits.ValueEpsilon)
                    return false;
            }
            return true;
        }

        public override string ToString() =>
            Key + " rev=" + Revision + " hp=" + Health.ToString("0.###") +
            (Dead ? " DEAD" : "") + " air=" + Air.ToString("0.###") +
            " food=" + Food.ToString("0.##") + " stam=" + Stamina.ToString("0.##") +
            " sleep=" + Sleep.ToString("0.##") + " temp=" + Temperature.ToString("0.#") +
            " wet=" + Wetness.ToString("0.##") +
            " freeze=" + FreezeProgress.ToString("0.###") + "/" + FreezeStage +
            (Sleeping ? " sleeping" : "");
    }

    /// <summary>死亡 / 复活边沿的种类（线格式 = vitals.proto 的 PlayerVitalEventKind）。</summary>
    public enum PlayerVitalEventKind
    {
        /// <summary>非法 / 未指定（解码拒绝）。</summary>
        Unspecified = 0,
        Death = 1,
        Respawn = 2
    }

    /// <summary>
    /// 一条死亡 / 复活边沿。复活带主机选定的落点（<see cref="HasPosition"/>），
    /// 死亡带死因（<see cref="CauseOfDeath"/>）。
    /// Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:3123-3260
    ///         （旧实现的 RespawnRequest：主机按这份落点重置化身位置 / 生命 / 体征）
    /// </summary>
    public readonly struct PlayerVitalEventSnapshot
    {
        private readonly string m_causeOfDeath;

        public PlayerVitalEventSnapshot(PlayerKey key, uint revision, PlayerVitalEventKind kind,
            string causeOfDeath, bool hasPosition, Vector3 position)
        {
            Key = key;
            Revision = revision;
            Kind = kind;
            m_causeOfDeath = causeOfDeath ?? string.Empty;
            HasPosition = hasPosition;
            Position = position;
        }

        public PlayerKey Key { get; }
        public uint Revision { get; }
        public PlayerVitalEventKind Kind { get; }
        public string CauseOfDeath => m_causeOfDeath ?? string.Empty;
        public bool HasPosition { get; }
        public Vector3 Position { get; }
        public bool IsDeath => Kind == PlayerVitalEventKind.Death;
        public bool IsRespawn => Kind == PlayerVitalEventKind.Respawn;

        public bool IsFinite =>
            PlayerVitalsSnapshot.IsFiniteValue(Position.X) &&
            PlayerVitalsSnapshot.IsFiniteValue(Position.Y) &&
            PlayerVitalsSnapshot.IsFiniteValue(Position.Z);

        public bool TryValidate(out string reason)
        {
            reason = null;
            if (!Key.IsValid)
            {
                reason = "玩家键 " + Key + " 非法";
                return false;
            }
            if (Revision == 0)
            {
                reason = "玩家键 " + Key + " 的事件 revision = 0（必须 > 0）";
                return false;
            }
            if (Kind != PlayerVitalEventKind.Death && Kind != PlayerVitalEventKind.Respawn)
            {
                reason = "玩家键 " + Key + " 的事件种类非法：" + (int)Kind;
                return false;
            }
            if (!IsFinite)
            {
                reason = "玩家键 " + Key + " 的事件坐标含 NaN/Inf";
                return false;
            }
            if (CauseOfDeath.Length > PlayerVitalsLimits.MaxCauseLength)
            {
                reason = "玩家键 " + Key + " 的死因长度 " + CauseOfDeath.Length + " 超过上限 " +
                    PlayerVitalsLimits.MaxCauseLength;
                return false;
            }
            // 复活必须带落点：主机用它把客户端本机玩家 / 化身摆到主机选定的出生点（旧实现同样由
            // RespawnRequest.Position 决定，见 ScMultiplayerClientEvents.cs:3143-3155）。
            if (Kind == PlayerVitalEventKind.Respawn && !HasPosition)
            {
                reason = "玩家键 " + Key + " 的复活事件缺少落点坐标";
                return false;
            }
            return true;
        }

        public PlayerVitalEventSnapshot WithKey(PlayerKey key) =>
            new PlayerVitalEventSnapshot(key, Revision, Kind, CauseOfDeath, HasPosition, Position);

        public override string ToString() =>
            Key + " rev=" + Revision + " " + Kind + (HasPosition ? " @ " + Position : "");
    }

    /// <summary>
    /// 一条主机广播的**表现事件**（vitals.proto <c>PlayerEffectEventEntry</c> / 消息 109）。
    ///
    /// 只描述"该播什么表现"：类型 / 坐标 / 主机资源名 / 强度 / 主机游戏时刻 / 每键单调序号。
    /// **绝不携带 / 修改生命值**（扣血仍由 PLAYER_VITALS_REPORT 95 下发，见实机问题 4）。
    /// </summary>
    public readonly struct PlayerEffectEventSnapshot
    {
        private readonly string m_sound;

        public PlayerEffectEventSnapshot(PlayerKey key, uint sequence, PlayerEffectKind kind,
            Vector3 position, double hostTime, float strength, string sound)
        {
            Key = key;
            Sequence = sequence;
            Kind = kind;
            Position = position;
            HostTime = hostTime;
            Strength = strength;
            m_sound = sound ?? string.Empty;
        }

        public PlayerKey Key { get; }

        /// <summary>每 (client_id, slot) 单调的事件序号（&gt; 0；重复 = 同序号）。</summary>
        public uint Sequence { get; }

        public PlayerEffectKind Kind { get; }

        /// <summary>事件发生时的身体坐标（必须有限；用于诊断 / 空间音频）。</summary>
        public Vector3 Position { get; }

        /// <summary>主机 <c>SubsystemGameInfo.TotalElapsedGameTime</c>（秒）。</summary>
        public double HostTime { get; }

        /// <summary>
        /// 表现强度：黑屏 / 绿屏时长（秒）、着火时的权威剩余火时长、熄灭 = 1 表示被水浇灭。
        /// 0 = 这次表现没有覆盖层（引擎此次没有设置黑屏 / 绿屏时长，例如单纯受冷打喷嚏）。
        /// </summary>
        public float Strength { get; }

        /// <summary>主机端该角色实际使用的引擎资源名（可空；核对 / 日志用）。</summary>
        public string Sound => m_sound ?? string.Empty;

        public bool IsKnownKind => PlayerEffectPolicy.IsKnown(Kind);

        public bool IsFinite =>
            PlayerVitalsSnapshot.IsFiniteValue(Position.X) &&
            PlayerVitalsSnapshot.IsFiniteValue(Position.Y) &&
            PlayerVitalsSnapshot.IsFiniteValue(Position.Z) &&
            PlayerVitalsSnapshot.IsFiniteDouble(HostTime) &&
            PlayerVitalsSnapshot.IsFiniteValue(Strength);

        public bool TryValidate(out string reason)
        {
            reason = null;
            if (!Key.IsValid)
            {
                reason = "玩家键 " + Key + " 非法";
                return false;
            }
            if (Sequence == 0)
            {
                reason = "玩家键 " + Key + " 的表现事件 sequence = 0（必须 > 0）";
                return false;
            }
            if (!IsKnownKind)
            {
                reason = "玩家键 " + Key + " 的表现事件种类非法：" + (int)Kind;
                return false;
            }
            if (!IsFinite)
            {
                reason = "玩家键 " + Key + " 的表现事件含 NaN/Inf（坐标 / 主机时刻 / 强度）";
                return false;
            }
            if (HostTime < 0.0)
            {
                reason = "玩家键 " + Key + " 的表现事件 host_time = " + HostTime + "（必须 >= 0）";
                return false;
            }
            if (Strength < 0f || Strength > PlayerVitalsLimits.MaxConditionSeconds)
            {
                reason = "玩家键 " + Key + " 的表现事件 strength = " + Strength.ToString("0.###") +
                    " 越界（0.." + PlayerVitalsLimits.MaxConditionSeconds + "）";
                return false;
            }
            if (Sound.Length > PlayerVitalsLimits.MaxEffectSoundLength)
            {
                reason = "玩家键 " + Key + " 的表现事件音效名长度 " + Sound.Length + " 超过上限 " +
                    PlayerVitalsLimits.MaxEffectSoundLength;
                return false;
            }
            return true;
        }

        public PlayerEffectEventSnapshot WithKey(PlayerKey key) =>
            new PlayerEffectEventSnapshot(key, Sequence, Kind, Position, HostTime, Strength, Sound);

        public string Describe() =>
            Key + " #" + Sequence + " " + PlayerEffectPolicy.Describe(Kind) +
            " t=" + HostTime.ToString("0.###") + " s=" + Strength.ToString("0.###") +
            " sound=" + (Sound.Length == 0 ? "-" : Sound);
    }

    /// <summary>表现事件的领域种类（线格式 = vitals.proto 的 PlayerEffectKind）。</summary>
    public enum PlayerEffectKind
    {
        /// <summary>非法 / 未指定（解码拒绝）。</summary>
        Unspecified = 0,

        /// <summary>流感咳嗽（引擎 <c>ComponentFlu.Cough</c> → <c>PlayCoughSound</c> + 4 秒咳嗽动作）。</summary>
        FluCough = 1,

        /// <summary>流感到期喷嚏（<c>ComponentFlu.Sneeze</c> → <c>PlaySneezeSound</c>）。</summary>
        FluSneeze = 2,

        /// <summary>恶心（<c>ComponentSickness.NauseaEffect</c> → <c>PlayMoanSound</c> + 绿屏渐入）。</summary>
        SicknessNausea = 3,

        /// <summary>呕吐（<c>ComponentSickness.NauseaEffect</c> → <c>PukeParticleSystem</c> + <c>PlayPukeSound</c>）。</summary>
        SicknessPuke = 4,

        /// <summary>着火（<c>ComponentOnFire.Update</c> → <c>OnFireParticleSystem</c>，火声由环境音负责）。</summary>
        FireIgnite = 5,

        /// <summary>火灭（<c>ComponentOnFire.Update</c> 水中熄灭 → "Audio/SizzleLong"）。</summary>
        FireExtinguish = 6
    }

    /// <summary>
    /// 表现事件的**纯策略**：种类合法性 / 过期 TTL / 引擎默认强度 / 该播什么。
    /// TTL 用于"过期事件去重计数"：客户端的游戏时钟由世界信息持续对齐主机，
    /// 所以可以用事件里的 <c>host_time</c> 判过期（引擎这些表现本身都只有 1~4 秒）。
    /// </summary>
    public static class PlayerEffectPolicy
    {
        /// <summary>引擎呕吐时绿屏的固定时长（<c>m_greenoutDuration = 0.8f</c>）。</summary>
        public const float PukeGreenoutSeconds = 0.8f;

        public static bool IsKnown(PlayerEffectKind kind) =>
            kind >= PlayerEffectKind.FluCough && kind <= PlayerEffectKind.FireExtinguish;

        /// <summary>
        /// 事件的过期上限（秒）：超过它到达的事件不再播放（计数为 stale），因为对应的引擎表现
        /// 早已结束。取值与引擎各表现的持续时间同量级（咳嗽动作 4 秒 / 喷嚏 1 秒 / 恶心 3 秒周期）。
        /// </summary>
        public static float TtlSeconds(PlayerEffectKind kind)
        {
            switch (kind)
            {
                case PlayerEffectKind.FluCough: return 2.0f;
                case PlayerEffectKind.FluSneeze: return 1.5f;
                case PlayerEffectKind.SicknessNausea: return 3.0f;
                case PlayerEffectKind.SicknessPuke: return 2.0f;
                case PlayerEffectKind.FireIgnite: return 3.0f;
                case PlayerEffectKind.FireExtinguish: return 2.0f;
                default: return 0f;
            }
        }

        /// <summary>该种类是否由 <c>ComponentCreatureSounds</c> 播放人声（引擎同款方法）。</summary>
        public static bool UsesCreatureSound(PlayerEffectKind kind) =>
            kind == PlayerEffectKind.FluCough || kind == PlayerEffectKind.FluSneeze ||
            kind == PlayerEffectKind.SicknessNausea || kind == PlayerEffectKind.SicknessPuke;

        /// <summary>该种类是否由引擎覆盖层（黑屏 / 绿屏）表现。</summary>
        public static bool UsesOverlay(PlayerEffectKind kind) =>
            kind == PlayerEffectKind.FluCough || kind == PlayerEffectKind.FluSneeze ||
            kind == PlayerEffectKind.SicknessNausea || kind == PlayerEffectKind.SicknessPuke;

        /// <summary>该种类是否产生粒子（呕吐 / 火焰）。</summary>
        public static bool UsesParticles(PlayerEffectKind kind) =>
            kind == PlayerEffectKind.SicknessPuke || kind == PlayerEffectKind.FireIgnite ||
            kind == PlayerEffectKind.FireExtinguish;

        public static string Describe(PlayerEffectKind kind)
        {
            switch (kind)
            {
                case PlayerEffectKind.FluCough: return "flu-cough";
                case PlayerEffectKind.FluSneeze: return "flu-sneeze";
                case PlayerEffectKind.SicknessNausea: return "sickness-nausea";
                case PlayerEffectKind.SicknessPuke: return "sickness-puke";
                case PlayerEffectKind.FireIgnite: return "fire-ignite";
                case PlayerEffectKind.FireExtinguish: return "fire-extinguish";
                default: return "unspecified";
            }
        }
    }

    /// <summary>
    /// 生命 / 体征上报、死亡 / 复活事件与表现事件的编解码（proto：Protocol/vitals.proto）。
    ///
    ///   · 单条上报 = <c>PlayerVitalsReport{ sequence, entries[N] }</c>，N ≤ 24；
    ///   · 单条事件 = <c>PlayerVitalsEvents{ sequence, entries[N] }</c>，N ≤ 24；
    ///   · 解码失败（缺 values、revision=0、值域越界、dead 与 health 不一致、条数 / 字节超限、
    ///     protobuf 非法）**必须显式失败**：返回 false + 可直接定位的原因，调用方丢弃并计数。
    ///     绝不夹紧 / 绝不补默认值（把"死亡"夹成 1 点血正是这次要修的缺陷形态）。
    /// </summary>
    public static class PlayerVitalsCodec
    {
        public static byte[] EncodeReport(uint sequence,
            IReadOnlyList<PlayerVitalsSnapshot> entries, out int dropped, out string error)
        {
            dropped = 0;
            error = null;
            if (sequence == 0)
            {
                error = "上报 sequence = 0（必须 > 0）";
                return null;
            }
            int count = entries?.Count ?? 0;
            if (count > PlayerVitalsLimits.MaxReportEntries)
            {
                // 显式：超限的条目**不静默截断后假装全发**，而是报出被丢弃的数量（调用方写日志）。
                dropped = count - PlayerVitalsLimits.MaxReportEntries;
                count = PlayerVitalsLimits.MaxReportEntries;
            }
            var message = new V.PlayerVitalsReport { Sequence = sequence };
            for (int i = 0; i < count; i++)
            {
                PlayerVitalsSnapshot snapshot = entries[i];
                if (!snapshot.TryValidate(out string itemError))
                {
                    error = "第 " + (i + 1) + " 条：" + itemError;
                    return null;
                }
                message.Entries.Add(ToMessage(snapshot));
            }
            byte[] payload = message.ToByteArray();
            if (payload.Length > PlayerVitalsLimits.MaxPayloadBytes)
            {
                error = "上报编码后 " + payload.Length + " 字节超过上限 " +
                    PlayerVitalsLimits.MaxPayloadBytes;
                return null;
            }
            return payload;
        }

        public static bool TryDecodeReport(byte[] payload, out uint sequence,
            out List<PlayerVitalsSnapshot> entries, out string error)
        {
            sequence = 0;
            entries = null;
            error = null;
            if (!TryParse(payload, V.PlayerVitalsReport.Parser, out V.PlayerVitalsReport message,
                    out error))
                return false;
            return TryConvertReport(message, out sequence, out entries, out error);
        }

        /// <summary>
        /// 已解析消息 → 领域快照（消息处理器用这条，避免把收到的消息再序列化一遍）。
        /// 校验口径与 <see cref="TryDecodeReport"/> 完全一致（同一个实现）。
        /// </summary>
        public static bool TryConvertReport(V.PlayerVitalsReport message, out uint sequence,
            out List<PlayerVitalsSnapshot> entries, out string error)
        {
            sequence = 0;
            entries = null;
            error = null;
            if (message == null)
            {
                error = "消息为空";
                return false;
            }
            if (message.Sequence == 0)
            {
                error = "上报 sequence = 0（必须 > 0）";
                return false;
            }
            if (message.Entries.Count > PlayerVitalsLimits.MaxReportEntries)
            {
                error = "上报条数 " + message.Entries.Count + " 超过上限 " +
                    PlayerVitalsLimits.MaxReportEntries;
                return false;
            }
            var result = new List<PlayerVitalsSnapshot>(message.Entries.Count);
            for (int i = 0; i < message.Entries.Count; i++)
            {
                if (!TryReadEntry(message.Entries[i], out PlayerVitalsSnapshot snapshot,
                        out string itemError))
                {
                    error = "第 " + (i + 1) + " 条：" + itemError;
                    return false;
                }
                result.Add(snapshot);
            }
            sequence = message.Sequence;
            entries = result;
            return true;
        }

        public static byte[] EncodeEvents(uint sequence,
            IReadOnlyList<PlayerVitalEventSnapshot> entries, out int dropped, out string error)
        {
            dropped = 0;
            error = null;
            if (sequence == 0)
            {
                error = "事件 sequence = 0（必须 > 0）";
                return null;
            }
            int count = entries?.Count ?? 0;
            if (count > PlayerVitalsLimits.MaxReportEntries)
            {
                dropped = count - PlayerVitalsLimits.MaxReportEntries;
                count = PlayerVitalsLimits.MaxReportEntries;
            }
            var message = new V.PlayerVitalsEvents { Sequence = sequence };
            for (int i = 0; i < count; i++)
            {
                PlayerVitalEventSnapshot evt = entries[i];
                if (!evt.TryValidate(out string itemError))
                {
                    error = "第 " + (i + 1) + " 条：" + itemError;
                    return null;
                }
                message.Entries.Add(ToMessage(evt));
            }
            byte[] payload = message.ToByteArray();
            if (payload.Length > PlayerVitalsLimits.MaxPayloadBytes)
            {
                error = "事件编码后 " + payload.Length + " 字节超过上限 " +
                    PlayerVitalsLimits.MaxPayloadBytes;
                return null;
            }
            return payload;
        }

        public static byte[] EncodeEffects(uint sequence,
            IReadOnlyList<PlayerEffectEventSnapshot> entries, out int dropped, out string error)
        {
            dropped = 0;
            error = null;
            if (sequence == 0)
            {
                error = "表现事件 sequence = 0（必须 > 0）";
                return null;
            }
            int count = entries?.Count ?? 0;
            if (count > PlayerVitalsLimits.MaxEffectEntries)
            {
                dropped = count - PlayerVitalsLimits.MaxEffectEntries;
                count = PlayerVitalsLimits.MaxEffectEntries;
            }
            var message = new V.PlayerEffectEvents { Sequence = sequence };
            for (int i = 0; i < count; i++)
            {
                PlayerEffectEventSnapshot evt = entries[i];
                if (!evt.TryValidate(out string itemError))
                {
                    error = "第 " + (i + 1) + " 条：" + itemError;
                    return null;
                }
                if (!TryToProtocolEffectKind(evt.Kind, out V.PlayerEffectKind protocolKind))
                {
                    error = "第 " + (i + 1) + " 条：表现种类 " + (int)evt.Kind + " 无法编码";
                    return null;
                }
                message.Entries.Add(ToMessage(evt, protocolKind));
            }
            byte[] payload = message.ToByteArray();
            if (payload.Length > PlayerVitalsLimits.MaxPayloadBytes)
            {
                error = "表现事件编码后 " + payload.Length + " 字节超过上限 " +
                    PlayerVitalsLimits.MaxPayloadBytes;
                return null;
            }
            return payload;
        }

        public static bool TryDecodeEffects(byte[] payload, out uint sequence,
            out List<PlayerEffectEventSnapshot> entries, out string error)
        {
            sequence = 0;
            entries = null;
            error = null;
            if (!TryParse(payload, V.PlayerEffectEvents.Parser, out V.PlayerEffectEvents message,
                    out error))
                return false;
            return TryConvertEffects(message, out sequence, out entries, out error);
        }

        /// <summary>已解析消息 → 领域事件（校验口径与 <see cref="TryDecodeEffects"/> 相同）。</summary>
        public static bool TryConvertEffects(V.PlayerEffectEvents message, out uint sequence,
            out List<PlayerEffectEventSnapshot> entries, out string error)
        {
            sequence = 0;
            entries = null;
            error = null;
            if (message == null)
            {
                error = "消息为空";
                return false;
            }
            if (message.Sequence == 0)
            {
                error = "表现事件 sequence = 0（必须 > 0）";
                return false;
            }
            if (message.Entries.Count > PlayerVitalsLimits.MaxEffectEntries)
            {
                error = "表现事件条数 " + message.Entries.Count + " 超过上限 " +
                    PlayerVitalsLimits.MaxEffectEntries;
                return false;
            }
            var result = new List<PlayerEffectEventSnapshot>(message.Entries.Count);
            for (int i = 0; i < message.Entries.Count; i++)
            {
                if (!TryReadEffect(message.Entries[i], out PlayerEffectEventSnapshot evt,
                        out string itemError))
                {
                    error = "第 " + (i + 1) + " 条：" + itemError;
                    return false;
                }
                result.Add(evt);
            }
            sequence = message.Sequence;
            entries = result;
            return true;
        }

        public static bool TryDecodeEvents(byte[] payload, out uint sequence,
            out List<PlayerVitalEventSnapshot> entries, out string error)
        {
            sequence = 0;
            entries = null;
            error = null;
            if (!TryParse(payload, V.PlayerVitalsEvents.Parser, out V.PlayerVitalsEvents message,
                    out error))
                return false;
            return TryConvertEvents(message, out sequence, out entries, out error);
        }

        /// <summary>已解析消息 → 领域事件（校验口径与 <see cref="TryDecodeEvents"/> 相同）。</summary>
        public static bool TryConvertEvents(V.PlayerVitalsEvents message, out uint sequence,
            out List<PlayerVitalEventSnapshot> entries, out string error)
        {
            sequence = 0;
            entries = null;
            error = null;
            if (message == null)
            {
                error = "消息为空";
                return false;
            }
            if (message.Sequence == 0)
            {
                error = "事件 sequence = 0（必须 > 0）";
                return false;
            }
            if (message.Entries.Count > PlayerVitalsLimits.MaxReportEntries)
            {
                error = "事件条数 " + message.Entries.Count + " 超过上限 " +
                    PlayerVitalsLimits.MaxReportEntries;
                return false;
            }
            var result = new List<PlayerVitalEventSnapshot>(message.Entries.Count);
            for (int i = 0; i < message.Entries.Count; i++)
            {
                if (!TryReadEvent(message.Entries[i], out PlayerVitalEventSnapshot evt,
                        out string itemError))
                {
                    error = "第 " + (i + 1) + " 条：" + itemError;
                    return false;
                }
                result.Add(evt);
            }
            sequence = message.Sequence;
            entries = result;
            return true;
        }

        private static bool TryParse<T>(byte[] payload, MessageParser<T> parser, out T message,
            out string error) where T : class, Google.Protobuf.IMessage<T>
        {
            message = null;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            if (payload.Length > PlayerVitalsLimits.MaxPayloadBytes)
            {
                error = "载荷 " + payload.Length + " 字节超过上限 " +
                    PlayerVitalsLimits.MaxPayloadBytes;
                return false;
            }
            try
            {
                message = parser.ParseFrom(payload);
                return true;
            }
            catch (Exception e)
            {
                error = "解析失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        private static bool TryReadEntry(V.PlayerVitalsEntry entry, out PlayerVitalsSnapshot snapshot,
            out string error)
        {
            snapshot = default;
            error = null;
            if (entry == null)
            {
                error = "条目为空";
                return false;
            }
            if (!PlayerKey.TryCreate((int)entry.ClientId, (int)entry.Slot, out PlayerKey key,
                    out error))
                return false;
            if (entry.Values == null)
            {
                error = "玩家键 " + key + " 的条目缺少 values（协议要求整块状态都在）";
                return false;
            }
            V.PlayerVitalsValues values = entry.Values;
            // 语义校验：线路上的 dead 必须与 health 一致。不一致的条目一律拒绝 ——
            // 静默按 health 推导会把"对端说死了但血量 0.9"这种矛盾悄悄改成一种解释。
            if (values.Dead != (values.Health <= 0f))
            {
                error = "玩家键 " + key + " 的 dead=" + values.Dead + " 与 health=" +
                    values.Health.ToString("0.###") + " 不一致";
                return false;
            }
            var satiation = new PlayerSatiation[values.Satiation.Count];
            for (int i = 0; i < satiation.Length; i++)
            {
                V.PlayerSatiationEntry item = values.Satiation[i];
                satiation[i] = new PlayerSatiation((int)item.Contents, item.Value);
            }
            snapshot = new PlayerVitalsSnapshot(key, entry.Revision, values.Health, values.Air,
                values.Food, values.Stamina, values.Sleep, values.Temperature, values.Wetness,
                values.TargetTemperature, values.FluDuration, values.FluOnset,
                values.SicknessDuration, values.FireDuration, values.Sleeping,
                values.CauseOfDeath, satiation, Vector3.Zero, values.SleepStartTime,
                values.SleepElapsed, values.AllowManualWake, values.ForcedSleep, values.Level,
                values.FreezeProgress, values.FreezeStage);
            return snapshot.TryValidate(out error);
        }

        private static bool TryReadEffect(V.PlayerEffectEventEntry entry,
            out PlayerEffectEventSnapshot evt, out string error)
        {
            evt = default;
            error = null;
            if (entry == null)
            {
                error = "条目为空";
                return false;
            }
            if (!PlayerKey.TryCreate((int)entry.ClientId, (int)entry.Slot, out PlayerKey key,
                    out error))
                return false;
            if (!TryToDomainEffectKind(entry.Kind, out PlayerEffectKind kind))
            {
                error = "玩家键 " + key + " 的表现种类非法：" + (int)entry.Kind;
                return false;
            }
            if (entry.Position == null)
            {
                error = "玩家键 " + key + " 的表现事件缺少坐标";
                return false;
            }
            var position = new Vector3(entry.Position.X, entry.Position.Y, entry.Position.Z);
            evt = new PlayerEffectEventSnapshot(key, entry.Sequence, kind, position,
                entry.HostTime, entry.Strength, entry.Sound);
            return evt.TryValidate(out error);
        }

        private static bool TryReadEvent(V.PlayerVitalEventEntry entry,
            out PlayerVitalEventSnapshot evt, out string error)
        {
            evt = default;
            error = null;
            if (entry == null)
            {
                error = "条目为空";
                return false;
            }
            if (!PlayerKey.TryCreate((int)entry.ClientId, (int)entry.Slot, out PlayerKey key,
                    out error))
                return false;
            var kind = (PlayerVitalEventKind)(int)entry.Kind;
            bool hasPosition = entry.Position != null;
            Vector3 position = hasPosition
                ? new Vector3(entry.Position.X, entry.Position.Y, entry.Position.Z)
                : Vector3.Zero;
            evt = new PlayerVitalEventSnapshot(key, entry.Revision, kind, entry.CauseOfDeath,
                hasPosition, position);
            return evt.TryValidate(out error);
        }

        private static V.PlayerVitalsEntry ToMessage(in PlayerVitalsSnapshot s)
        {
            var values = new V.PlayerVitalsValues
            {
                Health = s.Health,
                Air = s.Air,
                Food = s.Food,
                Stamina = s.Stamina,
                Sleep = s.Sleep,
                Temperature = s.Temperature,
                Wetness = s.Wetness,
                TargetTemperature = s.TargetTemperature,
                FluDuration = s.FluDuration,
                FluOnset = s.FluOnset,
                SicknessDuration = s.SicknessDuration,
                FireDuration = s.FireDuration,
                Sleeping = s.Sleeping,
                Dead = s.Dead,
                CauseOfDeath = s.CauseOfDeath,
                SleepStartTime = s.SleepStartTime,
                SleepElapsed = s.SleepElapsed,
                AllowManualWake = s.AllowManualWake,
                ForcedSleep = s.ForcedSleep,
                Level = s.Level,
                FreezeProgress = s.FreezeProgress,
                FreezeStage = s.FreezeStage
            };
            IReadOnlyList<PlayerSatiation> satiation = s.Satiation;
            for (int i = 0; i < satiation.Count; i++)
            {
                values.Satiation.Add(new V.PlayerSatiationEntry
                {
                    Contents = (uint)satiation[i].Contents,
                    Value = satiation[i].Value
                });
            }
            return new V.PlayerVitalsEntry
            {
                ClientId = (uint)s.Key.ClientId,
                Slot = (uint)s.Key.Slot,
                Revision = s.Revision,
                Values = values
            };
        }

        private static V.PlayerEffectEventEntry ToMessage(in PlayerEffectEventSnapshot e,
            V.PlayerEffectKind kind)
        {
            return new V.PlayerEffectEventEntry
            {
                ClientId = (uint)e.Key.ClientId,
                Slot = (uint)e.Key.Slot,
                Sequence = e.Sequence,
                Kind = kind,
                Position = new V.VitalsVec3
                {
                    X = e.Position.X,
                    Y = e.Position.Y,
                    Z = e.Position.Z
                },
                HostTime = e.HostTime,
                Strength = e.Strength,
                Sound = e.Sound
            };
        }

        private static bool TryToDomainEffectKind(V.PlayerEffectKind kind,
            out PlayerEffectKind result)
        {
            switch (kind)
            {
                case V.PlayerEffectKind.FluCough: result = PlayerEffectKind.FluCough; return true;
                case V.PlayerEffectKind.FluSneeze: result = PlayerEffectKind.FluSneeze; return true;
                case V.PlayerEffectKind.SicknessNausea:
                    result = PlayerEffectKind.SicknessNausea;
                    return true;
                case V.PlayerEffectKind.SicknessPuke:
                    result = PlayerEffectKind.SicknessPuke;
                    return true;
                case V.PlayerEffectKind.FireIgnite: result = PlayerEffectKind.FireIgnite; return true;
                case V.PlayerEffectKind.FireExtinguish:
                    result = PlayerEffectKind.FireExtinguish;
                    return true;
                default:
                    result = PlayerEffectKind.Unspecified;
                    return false;
            }
        }

        private static bool TryToProtocolEffectKind(PlayerEffectKind kind,
            out V.PlayerEffectKind result)
        {
            switch (kind)
            {
                case PlayerEffectKind.FluCough: result = V.PlayerEffectKind.FluCough; return true;
                case PlayerEffectKind.FluSneeze: result = V.PlayerEffectKind.FluSneeze; return true;
                case PlayerEffectKind.SicknessNausea:
                    result = V.PlayerEffectKind.SicknessNausea;
                    return true;
                case PlayerEffectKind.SicknessPuke:
                    result = V.PlayerEffectKind.SicknessPuke;
                    return true;
                case PlayerEffectKind.FireIgnite: result = V.PlayerEffectKind.FireIgnite; return true;
                case PlayerEffectKind.FireExtinguish:
                    result = V.PlayerEffectKind.FireExtinguish;
                    return true;
                default:
                    result = V.PlayerEffectKind.Unspecified;
                    return false;
            }
        }

        private static V.PlayerVitalEventEntry ToMessage(in PlayerVitalEventSnapshot e)
        {
            var message = new V.PlayerVitalEventEntry
            {
                ClientId = (uint)e.Key.ClientId,
                Slot = (uint)e.Key.Slot,
                Revision = e.Revision,
                Kind = (V.PlayerVitalEventKind)(int)e.Kind,
                CauseOfDeath = e.CauseOfDeath
            };
            if (e.HasPosition)
            {
                message.Position = new V.VitalsVec3
                {
                    X = e.Position.X,
                    Y = e.Position.Y,
                    Z = e.Position.Z
                };
            }
            return message;
        }
    }

    /// <summary>
    /// 一次"主机权威等级 → 本机 PlayerData.Level"的判定结果（纯逻辑，可离线自检）。
    /// </summary>
    public enum PlayerLevelDecision
    {
        /// <summary>写进本机 <c>PlayerData.Level</c>（值与本地不同）。</summary>
        Apply = 0,

        /// <summary>与本地一致（容差内）：不用写（不算覆盖）。</summary>
        Unchanged = 1,

        /// <summary>对端没有携带等级（<c>level &lt;= 0</c>，老版本主机）：不写，计数。</summary>
        Absent = 2,

        /// <summary>非法值（NaN / Inf / 负数 / 超上限）：拒绝，计数。</summary>
        Invalid = 3,

        /// <summary>快照是死亡状态：本地引擎会按引擎规则减半，等复活后主机再报。</summary>
        DeadDeferred = 4,

        /// <summary>比已应用的 revision 旧：陈旧，忽略。</summary>
        Stale = 5
    }

    /// <summary>
    /// 等级权威策略（纯逻辑，无引擎依赖）。
    ///
    /// 权威口径（产品决定 2026-10）：**主机裁决等级**。
    ///   · 主机的每个角色（本机槽位 + 客户端化身）都是真实的 <c>PlayerData</c>，死亡时引擎
    ///     <c>PlayerDead</c> 入场执行 <c>Level = max(floor(Level / 2), 1)</c>
    ///     （<c>Survivalcraft/Game/PlayerData.cs:299</c>）—— 减半**只由主机的引擎做一次**；
    ///   · 主机把这个已减半的值写进业务记录（5 秒周期 <c>CapturePlayerRecord</c> 已包含
    ///     <c>Level</c>），并随 <c>PLAYER_VITALS_REPORT</c> 下发（vitals.proto 字段 21）；
    ///   · 客户端本机引擎在自己的 <c>PlayerDead</c> 入场也会减半一次（无法拦，也不该拦），
    ///     但**死亡状态的快照一律不写等级**（<see cref="PlayerLevelDecision.DeadDeferred"/>）：
    ///     这样主机的权威值不会在本地减半之前被写进去（那就是双端各减半一次）；
    ///     等主机报"活着"（复活后）时再把主机值写进新实体，计数 <c>levelRespawnOverrides</c>。
    ///
    /// 因此客户端本地的等级只会在"活着"的主机快照上被改写，且同一个 revision 之内的
    /// 陈旧条目由模块的 revision 门控丢弃；<see cref="Decide"/> 里还带一道显式的
    /// revision 比较（<see cref="PlayerLevelDecision.Stale"/>），用于本机化的复核。
    /// </summary>
    public static class PlayerLevelPolicy
    {
        /// <summary>
        /// 引擎死亡减半规则（<c>PlayerData.cs:299</c>）：
        /// <c>Level = MathUtils.Max(MathUtils.Floor(Level / 2f), 1f)</c>。
        /// 主机侧用它**校验**引擎确实只减半了一次（不一致只记日志，不改值）。
        /// </summary>
        public static float HalveOnDeath(float level)
        {
            if (!IsFinite(level)) return 1f;
            return Math.Max((float)Math.Floor(level / 2f), 1f);
        }

        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>0 = 对端没有携带（老版本），1..1000 = 权威值。</summary>
        public static bool IsValid(float level) =>
            IsFinite(level) && level >= 0f && level <= PlayerVitalsLimits.MaxLevel;

        /// <summary>
        /// 非法等级的分类标签（<c>levelRejectedBy</c> 用）。**有界**：不把每个越界数值
        /// 变成一个新的计数字典键（否则一个乱发等级的对端能把状态行撑爆）。
        /// </summary>
        public static string RejectionTag(float level)
        {
            if (!IsFinite(level)) return "nonfinite";
            if (level < 0f) return "negative";
            if (level > PlayerVitalsLimits.MaxLevel) return "over-max";
            return "invalid";
        }

        /// <summary>
        /// 判定是否/如何把主机权威等级写进本机角色。
        /// <paramref name="revision"/> = 本条快照的 revision，<paramref name="lastAppliedRevision"/> = 本槽位
        /// 已写过的等级 revision（0 = 还没写过）。严格更旧 = 陈旧；相等 = 允许（复活重放会重发同一条快照）。
        /// </summary>
        public static PlayerLevelDecision Decide(float hostLevel, float localLevel, bool dead,
            uint revision, uint lastAppliedRevision, out string reason)
        {
            reason = null;
            if (revision != 0 && lastAppliedRevision != 0 && revision < lastAppliedRevision)
            {
                reason = "revision " + revision + " 比已应用的 " + lastAppliedRevision + " 旧";
                return PlayerLevelDecision.Stale;
            }
            if (!IsValid(hostLevel))
            {
                reason = "等级 " + hostLevel.ToString("0.####") + " 非法（合法 0.." +
                    PlayerVitalsLimits.MaxLevel + " 的有限值）";
                return PlayerLevelDecision.Invalid;
            }
            if (dead)
            {
                reason = "快照是死亡状态：等级按主机的引擎减半一次，等复活后的权威值";
                return PlayerLevelDecision.DeadDeferred;
            }
            if (hostLevel <= 0f)
            {
                reason = "对端没有携带等级（0 = 老版本主机）";
                return PlayerLevelDecision.Absent;
            }
            if (Math.Abs(hostLevel - localLevel) <= PlayerVitalsLimits.LevelEpsilon)
                return PlayerLevelDecision.Unchanged;
            reason = "本地 " + localLevel.ToString("0.####") + " → 主机 " +
                hostLevel.ToString("0.####");
            return PlayerLevelDecision.Apply;
        }
    }

    /// <summary>
    /// 主机抓取 <c>PlayerData.Level</c> 的唯一入口（纯逻辑，编解码 / 自检共用）。
    ///
    /// 口径（vitals.proto 字段 21）：
    ///   · 0 = 对端没有携带（读不到 / 老版本），快照里就是 0；
    ///   · 1..<see cref="PlayerVitalsLimits.MaxLevel"/> = 主机权威值；
    ///   · NaN / Inf / 负数 / 超上限 = **抓取失败**：返回 false，调用方计数 + 限流日志，
    ///     快照按 0 上报（**绝不夹紧** —— 夹紧会把"对手 9999 级"悄悄变成 1000 级）。
    /// </summary>
    public static class PlayerLevelCapture
    {
        public static bool TryCapture(float rawLevel, out float level, out string reason)
        {
            level = 0f;
            reason = null;
            if (!PlayerLevelPolicy.IsFinite(rawLevel))
            {
                reason = "PlayerData.Level 不是有限数（NaN / Inf）";
                return false;
            }
            if (rawLevel < 0f || rawLevel > PlayerVitalsLimits.MaxLevel)
            {
                reason = "PlayerData.Level = " + rawLevel.ToString("0.####") + " 越界（合法 0.." +
                    PlayerVitalsLimits.MaxLevel + "）";
                return false;
            }
            level = rawLevel;
            return true;
        }
    }

    /// <summary>
    /// 主机侧**客户端化身**的死亡减半账本（纯逻辑，无引擎依赖）：
    /// 同一个玩家键在**当前这次死亡**里只允许减半一次。
    ///
    /// 为什么需要它：
    ///   · 主机本机槽位（<c>clientId == 0</c>）的 <c>PlayerData.Update</c> 会跑引擎
    ///     <c>PlayerDead</c> 入场，减半由引擎做（<c>PlayerData.cs:299</c>）——这里永远返回 false；
    ///   · 主机侧客户端化身的 <c>PlayerData</c> 被 <c>NetworkAvatarSpawner</c> 从
    ///     <c>SubsystemPlayers.m_playersData</c> 摘掉（模块化角色不走引擎玩家更新），
    ///     <c>PlayerDead</c> 永远不会跑 → **引擎不会替它减半**，必须由主机在这里减一次；
    ///   · 抓取是 5 Hz 的重复调用：没有"本次死亡已减"的账本就会每拍减半一次（4 → 2 → 1）。
    ///
    /// 复活（<see cref="NoteAlive"/>）清掉账本 → 下一次死亡可以再减一次。
    /// 写回 <c>PlayerData.Level</c> 失败时**不要** <see cref="CommitHalved"/>，下一拍会重试。
    /// </summary>
    public sealed class HostAvatarLevelAuthority
    {
        private readonly Dictionary<PlayerKey, int> m_halvedTokens =
            new Dictionary<PlayerKey, int>();
        private long m_halvedEpisodes;

        /// <summary>累计减半的死亡期数（状态行 <c>levelHostHalved</c>）。</summary>
        public long HalvedEpisodes => m_halvedEpisodes;

        /// <summary>当前仍处于"已减半、等复活"的客户端化身数。</summary>
        public int ActiveDeaths => m_halvedTokens.Count;

        /// <summary>
        /// 这次抓取是否应该减半。<paramref name="halvedLevel"/> = 减半后的值
        /// （未减半时 = <paramref name="currentLevel"/>）。真正的"已减"由
        /// <see cref="CommitHalved"/> 在写回成功后登记。
        /// </summary>
        public bool ShouldHalve(PlayerKey key, int entityToken, bool dead, float currentLevel,
            out float halvedLevel, out string reason)
        {
            halvedLevel = currentLevel;
            reason = null;
            if (!key.IsValid || key.ClientId <= 0)
            {
                // 主机本机槽位：引擎 PlayerDead 已经减半，mod 不再减（否则双端 / 双次减半）。
                return false;
            }
            if (!dead) return false;
            if (!PlayerLevelPolicy.IsFinite(currentLevel) || currentLevel <= 0f)
            {
                reason = "玩家键 " + key + " 的等级是 " +
                    (PlayerLevelPolicy.IsFinite(currentLevel)
                        ? "0（未携带）"
                        : "非有限值（NaN / Inf）") +
                    " → 不减半（绝不凭空造出 1 级）";
                return false;
            }
            if (m_halvedTokens.TryGetValue(key, out int token) && token == entityToken)
                return false;
            halvedLevel = PlayerLevelPolicy.HalveOnDeath(currentLevel);
            reason = "主机侧客户端化身 " + key + " 死亡：等级 " +
                currentLevel.ToString("0.####") + " → " + halvedLevel.ToString("0.####") +
                "（本次死亡只减一次；主机记录里的等级是唯一权威源）";
            return true;
        }

        /// <summary>写回 <c>PlayerData.Level</c> 成功之后登记（同一死亡期不再减）。</summary>
        public void CommitHalved(PlayerKey key, int entityToken)
        {
            m_halvedTokens[key] = entityToken;
            m_halvedEpisodes++;
        }

        /// <summary>复活：清掉本键的"本次死亡已减半"标记，下一次死亡可以再减一次。</summary>
        public void NoteAlive(PlayerKey key)
        {
            if (key.IsValid) m_halvedTokens.Remove(key);
        }

        public void Reset() => m_halvedTokens.Clear();
    }

    /// <summary>
    /// 等级同步的计数（10 秒状态行；由引擎适配层 <c>Describe()</c> 输出）。
    /// 字段名就是排查时用的名字：<c>levelApplied / levelRejectedBy / levelDeadDeferred /
    /// levelStale / levelAbsent / levelRespawnOverrides / levelCaptureFailures</c>。
    /// </summary>
    public sealed class PlayerLevelCounters
    {
        private readonly Dictionary<string, long> m_rejectedBy =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_captureFailuresBy =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>主机权威等级写进本机 <c>PlayerData.Level</c> 的次数（不含复活覆盖）。</summary>
        public long Applied { get; private set; }

        /// <summary>与本地一致（容差内）而没写的次数。</summary>
        public long Unchanged { get; private set; }

        /// <summary>越界 / NaN 被拒绝的次数（分类见 <see cref="RejectedByText"/>）。</summary>
        public long Invalid { get; private set; }

        /// <summary>死亡状态快照被推迟的次数（等复活后的权威值）。</summary>
        public long DeadDeferred { get; private set; }

        /// <summary>revision 比已应用的旧而被忽略的次数。</summary>
        public long Stale { get; private set; }

        /// <summary>对端没有携带等级（0 = 老版本主机）的次数。</summary>
        public long Absent { get; private set; }

        /// <summary>复活后覆盖新实体等级的次数。</summary>
        public long RespawnOverrides { get; private set; }

        /// <summary>主机抓取 <c>PlayerData.Level</c> 失败的次数（非法值 / 读不到）。</summary>
        public long CaptureFailures { get; private set; }

        /// <summary>把等级写回 <c>PlayerData.Level</c> 抛异常的次数。</summary>
        public long WriteFailures { get; private set; }

        /// <summary>主机侧客户端化身死亡减半的次数（每次死亡只应 +1）。</summary>
        public long HostHalved { get; private set; }

        public string RejectedByText => Format(m_rejectedBy);
        public string CaptureFailuresByText => Format(m_captureFailuresBy);

        public void NoteApplied(bool respawnOverride)
        {
            if (respawnOverride) RespawnOverrides++;
            else Applied++;
        }

        public void NoteUnchanged() => Unchanged++;
        public void NoteDeadDeferred() => DeadDeferred++;
        public void NoteStale() => Stale++;
        public void NoteAbsent() => Absent++;
        public void NoteHostHalved() => HostHalved++;
        public void NoteWriteFailure() => WriteFailures++;

        public void NoteInvalid(string tag)
        {
            Invalid++;
            Increment(m_rejectedBy, string.IsNullOrEmpty(tag) ? "invalid" : tag);
        }

        public void NoteCaptureFailure(string tag)
        {
            CaptureFailures++;
            Increment(m_captureFailuresBy, string.IsNullOrEmpty(tag) ? "unknown" : tag);
        }

        public string Describe() =>
            "levelApplied=" + Applied +
            " levelUnchanged=" + Unchanged +
            " levelRejected=" + Invalid +
            " levelRejectedBy=" + RejectedByText +
            " levelDeadDeferred=" + DeadDeferred +
            " levelStale=" + Stale +
            " levelAbsent=" + Absent +
            " levelRespawnOverrides=" + RespawnOverrides +
            " levelCaptureFailures=" + CaptureFailures +
            " levelCaptureFailuresBy=" + CaptureFailuresByText +
            " levelWriteFailures=" + WriteFailures +
            " levelHostHalved=" + HostHalved;

        public void Reset()
        {
            Applied = 0;
            Unchanged = 0;
            Invalid = 0;
            DeadDeferred = 0;
            Stale = 0;
            Absent = 0;
            RespawnOverrides = 0;
            CaptureFailures = 0;
            WriteFailures = 0;
            HostHalved = 0;
            m_rejectedBy.Clear();
            m_captureFailuresBy.Clear();
        }

        private static void Increment(Dictionary<string, long> values, string key)
        {
            values.TryGetValue(key, out long count);
            values[key] = count + 1;
        }

        private static string Format(Dictionary<string, long> values)
        {
            if (values == null || values.Count == 0) return "{}";
            var keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.Ordinal);
            var builder = new System.Text.StringBuilder("{");
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(keys[i]).Append(':').Append(values[keys[i]]);
            }
            builder.Append('}');
            return builder.ToString();
        }
    }
}
