using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace SuMultiplayer.Players
{
    /// <summary>一次成就解锁：稳定 id + 主机解锁时间（Unix UTC 秒）。</summary>
    public readonly struct AchievementUnlock
    {
        public AchievementUnlock(string id, long unlockedUtcSeconds)
        {
            Id = id ?? string.Empty;
            UnlockedUtcSeconds = unlockedUtcSeconds;
        }

        public string Id { get; }

        public long UnlockedUtcSeconds { get; }

        public string Describe() => Id + "@" + UnlockedUtcSeconds;
    }

    /// <summary>
    /// 一个身份的**成就账本**（id → 解锁时间）。
    ///
    /// 引擎事实：本引擎版本**没有成就子系统**（Survivalcraft/Game 下没有任何 Achievement 类型），
    /// 所以成就是 SuMultiplayer 自己的目录（<see cref="AchievementCatalog"/>）：
    ///   · 主机按权威统计判定解锁、盖 UTC 时间戳、写进世界作用域角色记录；
    ///   · 客户端只接收 / 展示，绝不自己解锁。
    ///
    /// 前向兼容：不认识的 id **保留**（记录 / 展示原样），由调用方计数（achvUnknown）；
    /// 非法 id / 负时间戳明确拒绝并计数（achvMalformed）。
    /// 每条 id 只保留**最早**的解锁时间（合并幂等，重发 / 重复分片不会改写历史）。
    ///
    /// 无静态状态；XML 读写是纯值操作（角色记录用）。
    /// </summary>
    public sealed class AchievementLedger
    {
        /// <summary>每条身份的成就上限（当前目录 11 条；预留跨版本增长）。</summary>
        public const int MaxAchievements = 200;

        /// <summary>成就 id 上限（当前最长 "sump.altitude.150" = 17）。</summary>
        public const int MaxIdLength = 80;

        private const string RootElementName = "Achievements";
        private const string EntryElementName = "Achievement";

        private readonly Dictionary<string, long> m_unlocks =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public int Count => m_unlocks.Count;

        public bool IsEmpty => m_unlocks.Count == 0;

        /// <summary>按 id 序（Ordinal）枚举，保证线上 / 记录顺序可复现。</summary>
        public IEnumerable<AchievementUnlock> Entries
        {
            get
            {
                var ids = new List<string>(m_unlocks.Keys);
                ids.Sort(string.CompareOrdinal);
                var list = new List<AchievementUnlock>(ids.Count);
                foreach (string id in ids)
                    list.Add(new AchievementUnlock(id, m_unlocks[id]));
                return list;
            }
        }

        public bool Contains(string id) => m_unlocks.ContainsKey(id ?? string.Empty);

        public bool TryGet(string id, out long unlockedUtcSeconds) =>
            m_unlocks.TryGetValue(id ?? string.Empty, out unlockedUtcSeconds);

        /// <summary>
        /// 合并一次解锁。已存在 → 保留更早的时间戳、<paramref name="added"/> = false（幂等）。
        /// 非法 id / 负时间戳 / 超过上限 → false + 明确原因。
        /// </summary>
        public bool TryUnlock(string id, long unlockedUtcSeconds, out bool added, out string reason)
        {
            added = false;
            reason = null;
            if (!IsValidId(id, out reason)) return false;
            if (unlockedUtcSeconds < 0)
            {
                reason = "成就解锁时间戳为负（" + unlockedUtcSeconds + "）";
                return false;
            }
            if (m_unlocks.TryGetValue(id, out long existing))
            {
                if (unlockedUtcSeconds < existing) m_unlocks[id] = unlockedUtcSeconds;
                return true;
            }
            if (m_unlocks.Count >= MaxAchievements)
            {
                reason = "成就条目超过上限 " + MaxAchievements;
                return false;
            }
            m_unlocks[id] = unlockedUtcSeconds;
            added = true;
            return true;
        }

        public bool TryMerge(AchievementUnlock unlock, out bool added, out string reason) =>
            TryUnlock(unlock.Id, unlock.UnlockedUtcSeconds, out added, out reason);

        /// <summary>
        /// 合并另一份账本（幂等；同一 id 保留更早时间戳）。非法条目计入返回值，
        /// 由调用方决定计数 / 日志（绝不静默）。
        /// </summary>
        public int TryMergeAll(AchievementLedger other)
        {
            if (other == null) return 0;
            return TryMergeAll(other.Entries);
        }

        /// <summary>合并一组解锁（分片消息 / 记录里的列表）；返回被拒绝的条数。</summary>
        public int TryMergeAll(IEnumerable<AchievementUnlock> unlocks)
        {
            if (unlocks == null) return 0;
            int rejected = 0;
            foreach (AchievementUnlock unlock in unlocks)
            {
                if (TryMerge(unlock, out _, out _)) continue;
                rejected++;
            }
            return rejected;
        }

        public AchievementLedger Clone()
        {
            var clone = new AchievementLedger();
            foreach (KeyValuePair<string, long> item in m_unlocks)
                clone.m_unlocks[item.Key] = item.Value;
            return clone;
        }

        /// <summary>成就 id：1..80 字符，ASCII 字母 / 数字 / '_' / '.' / '-'。</summary>
        public static bool IsValidId(string id, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(id))
            {
                reason = "成就 id 为空";
                return false;
            }
            if (id.Length > MaxIdLength)
            {
                reason = "成就 id 过长（" + id.Length + " > " + MaxIdLength + "）";
                return false;
            }
            foreach (char c in id)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                          (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';
                if (ok) continue;
                reason = "成就 id 含非法字符";
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- 角色记录 XML

        public XElement ToXml()
        {
            var element = new XElement(RootElementName);
            foreach (AchievementUnlock unlock in Entries)
            {
                element.Add(new XElement(EntryElementName,
                    new XAttribute("Id", unlock.Id),
                    new XAttribute("UnlockedUtc", unlock.UnlockedUtcSeconds)));
            }
            return element;
        }

        /// <summary>
        /// 从角色记录读回成就。非法 / 超上限的条目计入 <paramref name="rejected"/>，
        /// 由调用方写明确日志（不静默丢弃）。
        /// </summary>
        public static AchievementLedger FromXml(XElement element, out int rejected)
        {
            rejected = 0;
            var ledger = new AchievementLedger();
            if (element == null) return ledger;
            foreach (XElement entry in element.Elements(EntryElementName))
            {
                string id = (string)entry.Attribute("Id");
                long? unlocked = (long?)entry.Attribute("UnlockedUtc");
                bool ok = unlocked.HasValue &&
                          ledger.TryUnlock(id, unlocked.Value, out _, out _);
                if (ok) continue;
                rejected++;
            }
            return ledger;
        }
    }
}
