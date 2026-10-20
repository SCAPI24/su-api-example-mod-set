using System;
using System.Collections.Generic;
using System.Globalization;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 一组引擎统计的**名字 → 值**（<c>Game.PlayerStats</c> 里带 <c>[Stat]</c> 的字段）。
    ///
    /// 纯逻辑（不依赖 Game）：线上编解码、差分、角色记录 XML 与自检都用它。
    /// 值一律是不变文化文本（double 用 "R"，long/int 用十进制，enum 用整数，string 原样），
    /// 与引擎自己的 <c>PlayerStats.Save/Load</c> 口径一致：
    ///   Source: Survivalcraft/Game/PlayerStats.cs:PlayerStats.Save（:208-222）
    ///   Source: Survivalcraft/Game/PlayerStats.cs:PlayerStats.Load（:186-206）
    ///
    /// 有界：名字 ≤ 64 字符 / 值 ≤ 64 字符 / 条目 ≤ 96。超限**明确拒绝**
    /// （<see cref="TrySet"/> 返回 false 并给出原因），不截断、不静默丢弃。
    /// </summary>
    public sealed class PlayerStatSet
    {
        /// <summary>统计字段名上限（引擎最长名字 "LandCreatureKills" = 17）。</summary>
        public const int MaxNameLength = 64;

        /// <summary>单个值上限（double/ long 的文本远小于它）。</summary>
        public const int MaxValueLength = 64;

        /// <summary>总条目上限（当前引擎 35 个 [Stat] 字段；预留跨版本增长）。</summary>
        public const int MaxEntries = 96;

        private readonly Dictionary<string, string> m_values =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public int Count => m_values.Count;

        public bool IsEmpty => m_values.Count == 0;

        /// <summary>按名字序（Ordinal）枚举，保证线上顺序与日志可复现。</summary>
        public IEnumerable<KeyValuePair<string, string>> Entries
        {
            get
            {
                var list = new List<KeyValuePair<string, string>>(m_values);
                list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                return list;
            }
        }

        /// <summary>写入 / 覆盖一项。非法名字或过长的值 → false + 原因（绝不截断）。</summary>
        public bool TrySet(string name, string value, out string reason)
        {
            reason = null;
            if (!IsValidName(name, out reason)) return false;
            string text = value ?? string.Empty;
            if (!IsValidValue(text, out reason)) return false;
            if (!m_values.ContainsKey(name) && m_values.Count >= MaxEntries)
            {
                reason = "统计条目超过上限 " + MaxEntries;
                return false;
            }
            m_values[name] = text;
            return true;
        }

        public bool TryGet(string name, out string value) =>
            m_values.TryGetValue(name ?? string.Empty, out value);

        public bool Contains(string name) => m_values.ContainsKey(name ?? string.Empty);

        public bool Remove(string name) => m_values.Remove(name ?? string.Empty);

        public PlayerStatSet Clone()
        {
            var clone = new PlayerStatSet();
            foreach (KeyValuePair<string, string> item in m_values)
                clone.m_values[item.Key] = item.Value;
            return clone;
        }

        public bool ContentEquals(PlayerStatSet other)
        {
            if (other == null || other.m_values.Count != m_values.Count) return false;
            foreach (KeyValuePair<string, string> item in m_values)
            {
                if (!other.m_values.TryGetValue(item.Key, out string text) ||
                    !string.Equals(text, item.Value, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 相对上一份权威值**真正变化**的条目（新增或值不同），按名字序。
        /// <paramref name="previous"/> 为 null = 全部条目都算变化。
        /// </summary>
        public List<KeyValuePair<string, string>> ChangedFrom(PlayerStatSet previous)
        {
            var changed = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> item in Entries)
            {
                if (previous != null && previous.m_values.TryGetValue(item.Key, out string old) &&
                    string.Equals(old, item.Value, StringComparison.Ordinal))
                    continue;
                changed.Add(item);
            }
            return changed;
        }

        /// <summary>
        /// 统计字段名合法性：1..64 字符，只允许 ASCII 字母 / 数字 / '_' / '.'。
        /// 引擎字段都是标识符；拒绝空格 / 控制字符 / 换行（日志注入与协议歧义）。
        /// </summary>
        public static bool IsValidName(string name, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(name))
            {
                reason = "统计名字为空";
                return false;
            }
            if (name.Length > MaxNameLength)
            {
                reason = "统计名字过长（" + name.Length + " > " + MaxNameLength + "）";
                return false;
            }
            foreach (char c in name)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                          (c >= '0' && c <= '9') || c == '_' || c == '.';
                if (ok) continue;
                reason = "统计名字含非法字符 U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
                return false;
            }
            return true;
        }

        /// <summary>值合法性：≤ 64 字符，不含 CR / LF / NUL（不截断；超限整条拒绝）。</summary>
        public static bool IsValidValue(string value, out string reason)
        {
            reason = null;
            string text = value ?? string.Empty;
            if (text.Length > MaxValueLength)
            {
                reason = "统计值过长（" + text.Length + " > " + MaxValueLength + "）";
                return false;
            }
            foreach (char c in text)
            {
                if (c == '\r' || c == '\n' || c == '\0')
                {
                    reason = "统计值含控制字符（换行 / NUL）";
                    return false;
                }
            }
            return true;
        }

        /// <summary>日志用：前 <paramref name="max"/> 个名字，超出用 "…(+N)" 标注。</summary>
        public string DescribeNames(int max)
        {
            var names = new List<string>();
            int total = 0;
            foreach (KeyValuePair<string, string> item in Entries)
            {
                total++;
                if (names.Count < max) names.Add(item.Key);
            }
            if (total == 0) return "(none)";
            return string.Join(",", names) +
                (total > names.Count ? ",…(+" + (total - names.Count) + ")" : string.Empty);
        }
    }
}
