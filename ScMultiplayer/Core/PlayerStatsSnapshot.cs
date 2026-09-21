using Comms;
using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace ScMultiplayer
{
    /// <summary>
    /// 联机角色的**游戏统计**快照（`Game.PlayerStats` 的可序列化副本）。
    ///
    /// 为什么需要它：客户端的统计是**客户端自己**算出来的（它才有 ComponentMiner /
    /// ComponentLocomotion 的真实行为），但权威副本必须跟角色其它信息一样放在
    /// `ScMultiplayerPlayers.xml` 的角色记录里 —— 既不能写进 Project.xml（网络角色不属于世界本身），
    /// 又要在客户端退出重进后保留。所以走"客户端周期性上报 → 主机写进角色记录 → 加入时回填客户端"。
    ///
    /// 字段用反射枚举 `PlayerStats.Stats`（引擎自己也是这么 Save/Load 的：只认带 [Stat] 的字段），
    /// 因此引擎后续新增统计字段时**不需要改这里**。取值按目标字段类型解析，
    /// 数值一律走 InvariantCulture（不受客户端语言/区域影响）。
    /// </summary>
    public sealed class PlayerStatsSnapshot
    {
        /// <summary>[Stat] 字段名 → 不变文化字符串值。</summary>
        public readonly Dictionary<string, string> Values =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>死亡记录，格式与 `PlayerStats.DeathRecord.Save()` 相同（逗号分隔）。</summary>
        public readonly List<string> DeathRecords = new List<string>();

        public bool IsEmpty => Values.Count == 0 && DeathRecords.Count == 0;

        public static PlayerStatsSnapshot Capture(PlayerStats stats)
        {
            var snapshot = new PlayerStatsSnapshot();
            if (stats == null)
                return snapshot;
            // `PlayerStats.Stats` 就是引擎自己 Save/Load 用的那份 [Stat] 字段表（属性是 public，
            // 而 StatAttribute 本身是 private 嵌套类型，所以只能借这个属性拿字段）。
            foreach (FieldInfo field in stats.Stats)
            {
                object value = field.GetValue(stats);
                snapshot.Values[field.Name] = FormatValue(value, field.FieldType);
            }
            foreach (PlayerStats.DeathRecord record in stats.DeathRecords)
                snapshot.DeathRecords.Add(record.Save());
            return snapshot;
        }

        /// <summary>把快照写回一个 `PlayerStats` 实例（缺失/解析失败的字段保持原值）。</summary>
        public void ApplyTo(PlayerStats stats)
        {
            if (stats == null)
                return;
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (FieldInfo field in stats.Stats)
            {
                known.Add(field.Name);
                if (!Values.TryGetValue(field.Name, out string text) || text == null)
                    continue;
                if (!TryParseValue(text, field.FieldType, out object value))
                    continue;
                try
                {
                    field.SetValue(stats, value);
                }
                catch (Exception ex)
                {
                    Log.Warning("[ScMP] Failed to apply player stat '" + field.Name + "': " + ex.Message);
                }
            }
            // 快照里有、当前引擎没有的字段：忽略（跨版本兼容，不让它把整个回填搞失败）。
            foreach (string name in Values.Keys)
            {
                if (!known.Contains(name))
                    Log.Warning("[ScMP] Player stat '" + name + "' is not present in this build; skipped.");
            }
            foreach (string text in DeathRecords)
            {
                if (string.IsNullOrEmpty(text))
                    continue;
                try
                {
                    var record = default(PlayerStats.DeathRecord);
                    record.Load(text);
                    stats.AddDeathRecord(record);
                }
                catch (Exception ex)
                {
                    Log.Warning("[ScMP] Failed to apply player death record: " + ex.Message);
                }
            }
        }

        public bool HasSameContent(PlayerStatsSnapshot other)
        {
            if (other == null || other.Values.Count != Values.Count ||
                other.DeathRecords.Count != DeathRecords.Count)
                return false;
            foreach (KeyValuePair<string, string> item in Values)
            {
                if (!other.Values.TryGetValue(item.Key, out string text) ||
                    !string.Equals(text, item.Value, StringComparison.Ordinal))
                    return false;
            }
            for (int i = 0; i < DeathRecords.Count; i++)
            {
                if (!string.Equals(DeathRecords[i], other.DeathRecords[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        public PlayerStatsSnapshot Clone()
        {
            var clone = new PlayerStatsSnapshot();
            foreach (KeyValuePair<string, string> item in Values)
                clone.Values[item.Key] = item.Value;
            clone.DeathRecords.AddRange(DeathRecords);
            return clone;
        }

        // ---------------------------------------------------------------- 角色记录（ScMultiplayerPlayers.xml）

        private const string StatsElementName = "Stats";
        private const string ValueElementName = "Value";
        private const string DeathRecordElementName = "DeathRecord";

        public XElement ToXml()
        {
            var element = new XElement(StatsElementName);
            foreach (KeyValuePair<string, string> item in Values)
            {
                element.Add(new XElement(ValueElementName,
                    new XAttribute("Name", item.Key),
                    new XAttribute("Value", item.Value ?? string.Empty)));
            }
            foreach (string record in DeathRecords)
                element.Add(new XElement(DeathRecordElementName,
                    new XAttribute("Value", record ?? string.Empty)));
            return element;
        }

        public static PlayerStatsSnapshot FromXml(XElement element)
        {
            if (element == null)
                return null;
            var snapshot = new PlayerStatsSnapshot();
            foreach (XElement value in element.Elements(ValueElementName))
            {
                string name = (string)value.Attribute("Name");
                if (string.IsNullOrEmpty(name))
                    continue;
                snapshot.Values[name] = (string)value.Attribute("Value") ?? string.Empty;
            }
            foreach (XElement record in element.Elements(DeathRecordElementName))
                snapshot.DeathRecords.Add((string)record.Attribute("Value") ?? string.Empty);
            return snapshot;
        }

        // ---------------------------------------------------------------- 网络（PlayerStatsMessage / GamePakWorldMessage）

        public static void Write(SuWriter writer, PlayerStatsSnapshot snapshot)
        {
            PlayerStatsSnapshot value = snapshot ?? new PlayerStatsSnapshot();
            writer.WritePackedInt32(value.Values.Count);
            foreach (KeyValuePair<string, string> item in value.Values)
            {
                writer.WriteString(item.Key);
                writer.WriteString(item.Value ?? string.Empty);
            }
            writer.WritePackedInt32(value.DeathRecords.Count);
            foreach (string record in value.DeathRecords)
                writer.WriteString(record ?? string.Empty);
        }

        public static PlayerStatsSnapshot Read(SuReader reader)
        {
            var snapshot = new PlayerStatsSnapshot();
            int count = reader.ReadPackedInt32();
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                string value = reader.ReadString();
                if (!string.IsNullOrEmpty(name))
                    snapshot.Values[name] = value ?? string.Empty;
            }
            int records = reader.ReadPackedInt32();
            for (int i = 0; i < records; i++)
                snapshot.DeathRecords.Add(reader.ReadString() ?? string.Empty);
            return snapshot;
        }

        // ---------------------------------------------------------------- 值转换

        private static string FormatValue(object value, Type type)
        {
            if (value == null)
                return string.Empty;
            if (type == typeof(double))
                return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            if (type == typeof(float))
                return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (type == typeof(long))
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(int))
                return ((int)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(bool))
                return (bool)value ? "1" : "0";
            if (type.IsEnum)
                return Convert.ToInt32(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static bool TryParseValue(string text, Type type, out object value)
        {
            value = null;
            if (type == typeof(double))
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    return false;
                value = d;
                return true;
            }
            if (type == typeof(float))
            {
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    return false;
                value = f;
                return true;
            }
            if (type == typeof(long))
            {
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                    return false;
                value = l;
                return true;
            }
            if (type == typeof(int))
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                    return false;
                value = i;
                return true;
            }
            if (type == typeof(bool))
            {
                value = text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
                return true;
            }
            if (type.IsEnum)
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw))
                    return false;
                value = Enum.ToObject(type, raw);
                return true;
            }
            if (type == typeof(string))
            {
                value = text;
                return true;
            }
            return false;
        }
    }
}
