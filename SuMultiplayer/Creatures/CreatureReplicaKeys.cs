using System;
using System.Collections.Generic;

namespace SuMultiplayer.Creatures
{
    /// <summary>
    /// 复制体键对账（**纯函数**，无引擎、无网络，可直接自检）。
    ///
    /// 为什么需要它：客户端的"清理未分配生物实体"是唯一会**删掉世界里已存在实体**的动作。
    /// 旧实现对账只看"本地 key 是否在保留集合里"，两种键不一致的情况会被当成"未分配"
    /// 静默删掉，表现就是"计数器说有 N 个复制体，世界里一个都没有"：
    ///   · 保留集合里有、本地没有（模块/适配器表不一致、创建失败、Project 换新后被清过表）；
    ///   · 保留集合与本地键**一个都对不上**（典型：Project 换新后适配器的表被清空，而模块
    ///     还拿着旧键；此时按"都不在集合里"清理会把世界里所有生物实体删光）。
    ///
    /// 这里把三种情况分开（绝不静默）：
    ///   · <see cref="Ledger.StaleKeys"/>      本地有、保留集合没有 → 确实不该再保留；
    ///   · <see cref="Ledger.MissingKeys"/>    保留集合有、本地没有 → **键不一致**：只记录 + 写日志，
    ///                                        不能因为"本地没有"就把这些键当成"未分配"去删别的东西；
    ///   · <see cref="Ledger.KeepSetUnusable"/> 保留集合非空、但一个都对不上本地 → 保留集合不可信，
    ///                                        此时**禁止**按它清世界实体（先请求全量重新对齐）。
    ///
    /// 键统一是主机分配的**正整数网络 id**（<see cref="CreatureSyncCodec"/> 解码时拒绝 0 / 负数），
    /// 任何符号/类型/编号不一致都会在这里被看见，而不是被当成"不在集合里"裁掉。
    /// </summary>
    public static class CreatureReplicaKeys
    {
        /// <summary>对账结果。键列表都按升序排列（日志/自检可复现）。</summary>
        public readonly struct Ledger
        {
            private readonly List<int> m_staleKeys;
            private readonly List<int> m_missingKeys;

            public Ledger(List<int> staleKeys, List<int> missingKeys, bool keepSetUsable)
            {
                m_staleKeys = staleKeys ?? new List<int>();
                m_missingKeys = missingKeys ?? new List<int>();
                KeepSetUsable = keepSetUsable;
            }

            /// <summary>本地有、保留集合里没有的键（可以删：不再分配给本端）。</summary>
            public IReadOnlyList<int> StaleKeys => m_staleKeys;

            /// <summary>保留集合里有、本地没有的键（键不一致，只记录，不当成"未分配"）。</summary>
            public IReadOnlyList<int> MissingKeys => m_missingKeys;

            /// <summary>保留集合是否可信（非空却一个键都对不上本地 ⇒ false）。</summary>
            public bool KeepSetUsable { get; }

            /// <summary>保留集合非空但本地一个都对不上 ⇒ 不可信（禁止按它清理）。</summary>
            public bool KeepSetUnusable => !KeepSetUsable;

            public bool HasMismatch => m_missingKeys.Count > 0;
        }

        /// <summary>
        /// 对账：<paramref name="keepKeys"/> = 本拍要保留的键（主机分配集合 / 当前快照键），
        /// <paramref name="replicaKeys"/> = 本地实际持有的复制体键。
        /// 传 null/空集合表示"什么都不保留"（合法的清空操作）。
        /// </summary>
        public static Ledger Reconcile(ICollection<int> keepKeys, IEnumerable<int> replicaKeys)
        {
            var keep = new HashSet<int>();
            if (keepKeys != null)
            {
                foreach (int key in keepKeys) keep.Add(key);
            }
            var local = new HashSet<int>();
            if (replicaKeys != null)
            {
                foreach (int key in replicaKeys) local.Add(key);
            }

            var stale = new List<int>();
            foreach (int key in local)
            {
                if (!keep.Contains(key)) stale.Add(key);
            }
            var missing = new List<int>();
            foreach (int key in keep)
            {
                if (!local.Contains(key)) missing.Add(key);
            }
            stale.Sort();
            missing.Sort();

            // 保留集合非空、但一个键都对不上本地 ⇒ 不可信（不是"全部未分配"）。
            // 正常的整体重分配不会落到这里：模块先把快照里的 Add 落进本地表，再裁剪，
            // 所以裁剪时本地集合与新保留集合一定至少有一个交集。
            bool keepSetUsable = true;
            if (keep.Count > 0)
            {
                bool anyMatched = false;
                foreach (int key in keep)
                {
                    if (local.Contains(key)) { anyMatched = true; break; }
                }
                keepSetUsable = anyMatched;
            }
            return new Ledger(stale, missing, keepSetUsable);
        }

        /// <summary>把不一致写成人能直接定位的一行（限流日志用；最多列 <paramref name="sampleLimit"/> 个键）。</summary>
        public static string Describe(Ledger ledger, int sampleLimit = 5)
        {
            int limit = sampleLimit > 0 ? sampleLimit : 5;
            string description = "stale=" + ledger.StaleKeys.Count + Sample(ledger.StaleKeys, limit) +
                " missing=" + ledger.MissingKeys.Count + Sample(ledger.MissingKeys, limit);
            if (ledger.KeepSetUnusable)
            {
                description += " keepSetUnusable=yes（保留集合与本地复制体键一个都对不上：不按它清理世界实体）";
            }
            return description;
        }

        private static string Sample(IReadOnlyList<int> keys, int limit)
        {
            if (keys == null || keys.Count == 0) return string.Empty;
            var builder = new System.Text.StringBuilder("[");
            int count = Math.Min(keys.Count, limit);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(keys[i]);
            }
            if (keys.Count > count) builder.Append(",…");
            builder.Append(']');
            return builder.ToString();
        }
    }
}
