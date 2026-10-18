using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Google.Protobuf;
using SuMultiplayer.Net;
using PTransfer = SuMultiplayer.Protocol.Transfer;
using PWorldInfo = SuMultiplayer.Protocol.WorldInfo;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 一条世界设置（字段名 → 值文本）。值一律用不变文化序列化，跨端不依赖区域设置。
    /// 对应旧实现 <c>GameWorldInfoMessage1.WorldSettings</c>（Dictionary&lt;string,string&gt;）。
    /// </summary>
    public readonly struct WorldSettingEntry
    {
        public WorldSettingEntry(string key, string value)
        {
            Key = key ?? string.Empty;
            Value = value ?? string.Empty;
        }

        public string Key { get; }
        public string Value { get; }
    }

    /// <summary>
    /// 主机权威的世界信息快照（约 2 Hz 下发）。
    /// 字段对应旧实现 <c>Mod/ScMultiplayer/Message/GameWorldInfo1Message.cs</c>：
    /// TimeOfDayOffset / TotalElapsedGameTime / TimeOfDayMode / 降水（started+intensity）/
    /// 雾（started+progress+intensity+**FogSeed**）/ 闪电（bool + 位置）/ WorldSettings /
    /// 时间加速；本版追加 <see cref="InitialGameTime"/> 与 <see cref="WorldTimeRevision"/>。
    /// </summary>
    public sealed class WorldInfoSnapshot
    {
        public int ServerTick;
        public double InitialGameTime;
        public double TimeOfDayOffset;
        public double TotalElapsedGameTime;
        public int TimeOfDayMode;
        public bool PrecipitationStarted;
        public float PrecipitationIntensity;
        public bool FogStarted;
        public float FogProgress;
        public float FogIntensity;
        public int FogSeed;
        public bool HasLightning;
        public int LightningX;
        public int LightningY;
        public int LightningZ;
        /// <summary>闪电事件序号（位置/发生时刻变化时主机 +1；客户端据此只闪一次，不会每 2 Hz 重置亮度）。</summary>
        public int LightningRevision;
        /// <summary>主机每次下发 +1（客户端据此丢弃乱序 / 重复的旧快照）。</summary>
        public long WorldTimeRevision;
        public bool TimeAccelerated;
        /// <summary>主机 SubsystemTime.GameTimeFactor（客户端照抄，保证本地推进速度与主机一致）。</summary>
        public float TimeFactor = 1f;
        public readonly List<WorldSettingEntry> WorldSettings = new List<WorldSettingEntry>(24);

        public string Setting(string key)
        {
            for (int i = 0; i < WorldSettings.Count; i++)
                if (string.Equals(WorldSettings[i].Key, key, StringComparison.Ordinal))
                    return WorldSettings[i].Value;
            return null;
        }

        public void SetSetting(string key, string value)
        {
            for (int i = 0; i < WorldSettings.Count; i++)
            {
                if (!string.Equals(WorldSettings[i].Key, key, StringComparison.Ordinal)) continue;
                WorldSettings[i] = new WorldSettingEntry(key, value);
                return;
            }
            WorldSettings.Add(new WorldSettingEntry(key, value));
        }

        /// <summary>可审计的一行（时间 / 天气 / 设置条数）。</summary>
        public string Describe()
        {
            return "rev=" + WorldTimeRevision.ToString(CultureInfo.InvariantCulture) +
                " tod=" + TotalElapsedGameTime.ToString("F2", CultureInfo.InvariantCulture) +
                " offset=" + TimeOfDayOffset.ToString("F4", CultureInfo.InvariantCulture) +
                " mode=" + TimeOfDayMode.ToString(CultureInfo.InvariantCulture) +
                " precip=" + (PrecipitationStarted ? "on" : "off") + "/" +
                PrecipitationIntensity.ToString("F3", CultureInfo.InvariantCulture) +
                " fog=" + (FogStarted ? "on" : "off") + "/" +
                FogProgress.ToString("F3", CultureInfo.InvariantCulture) + "/" +
                FogIntensity.ToString("F3", CultureInfo.InvariantCulture) +
                " seed=" + FogSeed.ToString(CultureInfo.InvariantCulture) +
                " lightning=" + (HasLightning
                    ? LightningX.ToString(CultureInfo.InvariantCulture) + "," +
                      LightningY.ToString(CultureInfo.InvariantCulture) + "," +
                      LightningZ.ToString(CultureInfo.InvariantCulture)
                    : "none") +
                " accel=" + (TimeAccelerated ? "on" : "off") +
                " settings=" + WorldSettings.Count.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// WorldInfo（主机 → 客户端）编解码（proto：Protocol/worldinfo.proto）。
    /// 字段与旧 <c>GameWorldInfo1Message</c> 一一对应；WorldSettings 用 repeated key/value。
    /// </summary>
    public static class WorldInfoCodec
    {
        /// <summary>WorldSettings 最多几条（超出的载荷整条拒绝，不部分应用）。</summary>
        public const int MaxSettings = 512;

        public static byte[] Encode(WorldInfoSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var message = new PWorldInfo.WorldInfo
            {
                ServerTick = snapshot.ServerTick,
                InitialGameTime = snapshot.InitialGameTime,
                TimeOfDayOffset = snapshot.TimeOfDayOffset,
                TotalElapsedGameTime = snapshot.TotalElapsedGameTime,
                TimeOfDayMode = snapshot.TimeOfDayMode,
                PrecipitationStarted = snapshot.PrecipitationStarted,
                PrecipitationIntensity = snapshot.PrecipitationIntensity,
                FogStarted = snapshot.FogStarted,
                FogProgress = snapshot.FogProgress,
                FogIntensity = snapshot.FogIntensity,
                FogSeed = snapshot.FogSeed,
                HasLightning = snapshot.HasLightning,
                LightningX = snapshot.LightningX,
                LightningY = snapshot.LightningY,
                LightningZ = snapshot.LightningZ,
                LightningRevision = snapshot.LightningRevision,
                WorldTimeRevision = snapshot.WorldTimeRevision,
                TimeAccelerated = snapshot.TimeAccelerated,
                TimeFactor = snapshot.TimeFactor
            };
            for (int i = 0; i < snapshot.WorldSettings.Count; i++)
            {
                WorldSettingEntry setting = snapshot.WorldSettings[i];
                message.Settings.Add(new PWorldInfo.WorldSetting
                {
                    Key = setting.Key,
                    Value = setting.Value
                });
            }
            return message.ToByteArray();
        }

        public static bool TryDecode(byte[] payload, out WorldInfoSnapshot snapshot, out string reason)
        {
            snapshot = null;
            reason = null;
            if (payload == null || payload.Length == 0)
            {
                reason = "载荷为空";
                return false;
            }
            PWorldInfo.WorldInfo message;
            try
            {
                message = PWorldInfo.WorldInfo.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                reason = "载荷不是合法 protobuf：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            return TryConvert(message, out snapshot, out reason);
        }

        /// <summary>把已解析的 protobuf 消息转成领域快照（强类型处理器用）。</summary>
        public static bool TryConvert(PWorldInfo.WorldInfo message, out WorldInfoSnapshot snapshot,
            out string reason)
        {
            snapshot = null;
            reason = null;
            if (message == null)
            {
                reason = "消息为 null";
                return false;
            }
            if (message.Settings.Count > MaxSettings)
            {
                reason = "WorldSettings 条数 " + message.Settings.Count + " 超出上限 " + MaxSettings;
                return false;
            }
            var result = new WorldInfoSnapshot
            {
                ServerTick = message.ServerTick,
                InitialGameTime = message.InitialGameTime,
                TimeOfDayOffset = message.TimeOfDayOffset,
                TotalElapsedGameTime = message.TotalElapsedGameTime,
                TimeOfDayMode = message.TimeOfDayMode,
                PrecipitationStarted = message.PrecipitationStarted,
                PrecipitationIntensity = message.PrecipitationIntensity,
                FogStarted = message.FogStarted,
                FogProgress = message.FogProgress,
                FogIntensity = message.FogIntensity,
                FogSeed = message.FogSeed,
                HasLightning = message.HasLightning,
                LightningX = message.LightningX,
                LightningY = message.LightningY,
                LightningZ = message.LightningZ,
                LightningRevision = message.LightningRevision,
                WorldTimeRevision = message.WorldTimeRevision,
                TimeAccelerated = message.TimeAccelerated,
                TimeFactor = message.TimeFactor
            };
            for (int i = 0; i < message.Settings.Count; i++)
            {
                PWorldInfo.WorldSetting setting = message.Settings[i];
                result.SetSetting(setting.Key, setting.Value);
            }
            if (result.WorldTimeRevision <= 0)
            {
                reason = "WorldTimeRevision 非法：" + result.WorldTimeRevision;
                return false;
            }
            snapshot = result;
            return true;
        }
    }

    /// <summary>
    /// 客户端的世界信息副本：收到主机快照就整体覆盖（权威）；**绝不自己推进天气 / 时间**。
    /// <see cref="Tick"/> 只用于证明"客户端没有独立演化"（<see cref="IndependentTicks"/> 恒 0），
    /// 引擎侧对应 <c>SuSubsystemWeather</c> 在客户端不调用 <c>base.Update</c>。
    /// </summary>
    public sealed class ClientWorldInfoReplica
    {
        private WorldInfoSnapshot m_current;

        public bool HasAuthoritative => m_current != null;
        public WorldInfoSnapshot Current => m_current;
        public long Applied { get; private set; }
        public long StaleIgnored { get; private set; }
        public long Malformed { get; private set; }
        public long ClockSnaps { get; private set; }
        /// <summary>客户端尝试独立演化天气的次数（按架构必须恒为 0；非 0 = 实现回退）。</summary>
        public long IndependentTicks { get; private set; }
        /// <summary>天气/雾状态被主机覆盖的次数（= <see cref="Applied"/>；可审计）。</summary>
        public long WeatherOverwrites { get; private set; }
        /// <summary>闪电表现被应用了几次。</summary>
        public long LightningApplied { get; private set; }

        /// <summary>世界时钟允许的漂移（秒）：超过就硬对齐到主机值。</summary>
        public double ClockSnapThresholdSeconds { get; set; } = 0.35;

        /// <summary>应用一条主机快照；返回 false = 乱序 / 重复的旧快照（已计数，不覆盖新值）。</summary>
        public bool Apply(WorldInfoSnapshot snapshot)
        {
            if (snapshot == null || snapshot.WorldTimeRevision <= 0)
            {
                Malformed++;
                return false;
            }
            if (m_current != null && snapshot.WorldTimeRevision <= m_current.WorldTimeRevision)
            {
                StaleIgnored++;
                return false;
            }
            bool clock = m_current == null ||
                Math.Abs(m_current.TotalElapsedGameTime - snapshot.TotalElapsedGameTime) >
                    ClockSnapThresholdSeconds;
            m_current = snapshot;
            Applied++;
            WeatherOverwrites++;
            if (clock) ClockSnaps++;
            if (snapshot.HasLightning) LightningApplied++;
            return true;
        }

        /// <summary>
        /// 客户端**不允许**独立推进天气。这个方法只统计"有人试图让副本独立演化"的次数；
        /// 正常情况下调用方（SuSubsystemWeather）在客户端根本不会调用 base.Update。
        /// </summary>
        public void Tick(float dt)
        {
            _ = dt;
            if (!HasAuthoritative) return;
            IndependentTicks++;
        }

        /// <summary>连接换了 / 主机重启：作废旧会话的修订号（新主机的 revision 可能从 1 重新开始）。</summary>
        public void Reset()
        {
            m_current = null;
        }

        public string Describe()
        {
            return "applied=" + Applied.ToString(CultureInfo.InvariantCulture) +
                " stale=" + StaleIgnored.ToString(CultureInfo.InvariantCulture) +
                " malformed=" + Malformed.ToString(CultureInfo.InvariantCulture) +
                " clockSnaps=" + ClockSnaps.ToString(CultureInfo.InvariantCulture) +
                " independentTicks=" + IndependentTicks.ToString(CultureInfo.InvariantCulture) +
                " lightning=" + LightningApplied.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 客户端 / 主机时钟对齐的纯判定（引擎侧把结果写成 <c>SubsystemGameInfo.TotalElapsedGameTime</c>）。
    /// </summary>
    public static class WorldClockPolicy
    {
        /// <summary>漂移超过阈值才硬对齐（避免 2 Hz 覆盖把本地时钟打成抖动）。</summary>
        public static bool NeedsSnap(double hostTotalElapsed, double clientTotalElapsed, double thresholdSeconds)
        {
            double threshold = thresholdSeconds <= 0.0 ? 0.35 : thresholdSeconds;
            return Math.Abs(hostTotalElapsed - clientTotalElapsed) > threshold;
        }
    }

    /// <summary>
    /// 随机数状态同步（旧实现 <c>ScMultiplayerClientEvents.cs:878-930</c>，逐字迁移）：
    ///   · 主机把**所有 Subsystems 上的所有 <see cref="global::Engine.Random"/> 字段**抓成
    ///     <c>Dictionary&lt;string,long&gt;</c>，键 = <c>DeclaringType.FullName + "|" + fieldName</c>；
    ///   · 随世界 pak 下发会话种子 + 这份表；
    ///   · 客户端**每个 Project 只应用一次**：对每个字段先
    ///     <c>new global::Engine.Random(DeriveRandomSeed(seed,key))</c> 再 <c>State = hostState</c>。
    /// 为什么连"新建 + 播种"这一步也要做：主机的 Random 对象是它自己 new 出来的，
    /// 客户端如果只写 State 而不替换对象，字段类型/构造差异（或将来新增的 Random 子类）会留下分歧；
    /// 旧实现就是这么做的，逐字保留。
    /// </summary>
    public static class RandomStateTable
    {
        /// <summary>抓一台主机上所有 Subsystems 的 Random 字段（只读；不分配新的 Random）。</summary>
        public static Dictionary<string, long> Capture(IEnumerable<object> subsystems)
        {
            var states = new Dictionary<string, long>(StringComparer.Ordinal);
            if (subsystems == null) return states;
            foreach (object subsystem in subsystems)
            {
                if (subsystem == null) continue;
                foreach (FieldInfo field in RandomFieldsOf(subsystem.GetType()))
                {
                    if (field.GetValue(subsystem) is not global::Engine.Random random) continue;
                    states[KeyOf(field)] = (long)random.State;
                }
            }
            return states;
        }

        /// <summary>
        /// 客户端应用：每个字段都换成"按 key 派生种子的新 Random"，再写入主机 State。
        /// 返回实际应用的字段数；<paramref name="missing"/> = 主机表里有、本端没有对应字段的键数。
        /// </summary>
        public static int Apply(IEnumerable<object> subsystems, int seed,
            IReadOnlyDictionary<string, long> states, out int missing)
        {
            missing = 0;
            if (subsystems == null) return 0;
            var used = new HashSet<string>(StringComparer.Ordinal);
            int applied = 0;
            foreach (object subsystem in subsystems)
            {
                if (subsystem == null) continue;
                foreach (FieldInfo field in RandomFieldsOf(subsystem.GetType()))
                {
                    string key = KeyOf(field);
                    var random = new global::Engine.Random(DeriveRandomSeed(seed, key));
                    if (states != null && states.TryGetValue(key, out long state))
                    {
                        random.State = (ulong)state;
                        used.Add(key);
                    }
                    if (field.IsInitOnly)
                    {
                        // 只读字段无法安全替换：明确计数（调用方写日志），不假装应用成功。
                        missing++;
                        continue;
                    }
                    field.SetValue(subsystem, random);
                    applied++;
                }
            }
            if (states != null)
            {
                foreach (KeyValuePair<string, long> pair in states)
                    if (!used.Contains(pair.Key)) missing++;
            }
            return applied;
        }

        /// <summary>旧实现逐字：<c>hash = hash * 31 + c</c>（int 溢出即截断，必须保持一致）。</summary>
        public static int DeriveRandomSeed(int seed, string key)
        {
            int hash = seed;
            if (key != null)
            {
                for (int i = 0; i < key.Length; i++) hash = hash * 31 + key[i];
            }
            return hash;
        }

        public static string KeyOf(FieldInfo field) =>
            (field.DeclaringType == null ? "?" : field.DeclaringType.FullName) + "|" + field.Name;

        /// <summary>
        /// 枚举类型继承链上所有 <see cref="global::Engine.Random"/> 实例字段（DeclaredOnly，避免同一字段重复）。
        /// 旧实现用 ModParentField 读父类私有字段；这里直接用反射（同一份字段集合）。
        /// </summary>
        public static List<FieldInfo> RandomFieldsOf(Type type)
        {
            var fields = new List<FieldInfo>();
            Type current = type;
            while (current != null && current != typeof(object))
            {
                FieldInfo[] declared = current.GetFields(BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < declared.Length; i++)
                {
                    if (declared[i].FieldType == typeof(global::Engine.Random)) fields.Add(declared[i]);
                }
                current = current.BaseType;
            }
            return fields;
        }
    }

    /// <summary>随机数状态表的线格式（随 WorldBegin 末尾追加；只许追加）。</summary>
    public static class RandomStateCodec
    {
        /// <summary>把随机数状态表转成 protobuf 子消息（顺序保持稳定）。</summary>
        public static PTransfer.RandomStateTable ToMessage(int seed,
            IReadOnlyDictionary<string, long> states)
        {
            var table = new PTransfer.RandomStateTable { Seed = seed };
            if (states == null) return table;
            foreach (KeyValuePair<string, long> pair in states)
            {
                table.Entries.Add(new PTransfer.RandomStateEntry { Key = pair.Key, State = pair.Value });
            }
            return table;
        }

        /// <summary>读回随机数状态表；重复 key 以最后一次为准（与旧 Dictionary 语义一致）。</summary>
        public static Dictionary<string, long> FromMessage(PTransfer.RandomStateTable table,
            out int seed)
        {
            seed = table?.Seed ?? 0;
            var states = new Dictionary<string, long>(StringComparer.Ordinal);
            if (table == null) return states;
            for (int i = 0; i < table.Entries.Count; i++)
            {
                PTransfer.RandomStateEntry entry = table.Entries[i];
                if (entry == null) continue;
                states[entry.Key ?? string.Empty] = entry.State;
            }
            return states;
        }

        public static string Describe(int seed, IReadOnlyDictionary<string, long> states)
        {
            var builder = new StringBuilder(64);
            builder.Append("seed=").Append(seed.ToString(CultureInfo.InvariantCulture));
            builder.Append(" states=").Append((states?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }
}
