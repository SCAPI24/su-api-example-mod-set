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
            bool allowManualWake = false, bool forcedSleep = false, float level = 0f)
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
                AllowManualWake, ForcedSleep, Level);

        public PlayerVitalsSnapshot WithRevision(uint revision) =>
            new PlayerVitalsSnapshot(Key, revision, Health, Air, Food, Stamina, Sleep, Temperature,
                Wetness, TargetTemperature, FluDuration, FluOnset, SicknessDuration, FireDuration,
                Sleeping, CauseOfDeath, m_satiation, Position, SleepStartTime, SleepElapsed,
                AllowManualWake, ForcedSleep, Level);

        /// <summary>
        /// 只替换生命值（对账修正用：其余字段保持从化身读到的现值，绝不凭空造值）。
        /// <c>Dead</c> 由生命值推导，所以修正后的快照里两者依然一致。
        /// </summary>
        public PlayerVitalsSnapshot WithHealth(float health) =>
            new PlayerVitalsSnapshot(Key, Revision, health, Air, Food, Stamina, Sleep, Temperature,
                Wetness, TargetTemperature, FluDuration, FluOnset, SicknessDuration, FireDuration,
                Sleeping, CauseOfDeath, m_satiation, Position, SleepStartTime, SleepElapsed,
                AllowManualWake, ForcedSleep, Level);

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
            IsFiniteValue(Level) &&
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
            " wet=" + Wetness.ToString("0.##") + (Sleeping ? " sleeping" : "");
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
    /// 生命 / 体征上报与死亡 / 复活事件的编解码（proto：Protocol/vitals.proto）。
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
                values.SleepElapsed, values.AllowManualWake, values.ForcedSleep, values.Level);
            return snapshot.TryValidate(out error);
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
                Level = s.Level
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
}
