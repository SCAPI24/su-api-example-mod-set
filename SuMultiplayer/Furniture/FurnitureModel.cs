using System;
using System.Collections.Generic;
using System.Globalization;

namespace SuMultiplayer.Furniture
{
    /// <summary>
    /// 一条家具设计记录的**纯值形态**（不引用 Game 程序集）：
    /// 引擎侧 <c>FurnitureDesign</c> ↔ 协议 <c>FurnitureDesignRecord</c> 都通过它转换，
    /// 这样自检可以直接编译并断言"主机权威 / 客户端副本"的全部逻辑。
    ///
    /// Source: Survivalcraft/Game/FurnitureDesign.cs:FurnitureDesign
    ///         （Resolution / Name / InteractionMode / LinkedDesign / FurnitureSet / GetValue）
    /// Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:AddDesign
    ///         （Index 是主机设计表里的槽位 0..1023）
    /// </summary>
    public sealed class FurnitureDesignRecord
    {
        private int[] m_values;
        private long? m_contentHash;

        public FurnitureDesignRecord(int index, int resolution, string name, int interactionMode,
            int linkedIndex, string furnitureSet, int[] values)
        {
            Index = index;
            Resolution = resolution;
            Name = name ?? string.Empty;
            InteractionMode = interactionMode;
            LinkedIndex = linkedIndex;
            FurnitureSet = furnitureSet ?? string.Empty;
            m_values = values == null ? Array.Empty<int>() : (int[])values.Clone();
            for (int i = 0; i < m_values.Length; i++)
                m_values[i] = FurnitureDesignHash.StripLight(m_values[i]);
        }

        /// <summary>主机设计表槽位（0..1023）。客户端**绝不自行分配**这个值。</summary>
        public int Index { get; }

        /// <summary>2..16（FurnitureDesign.MinResolution / MaxResolution）。</summary>
        public int Resolution { get; }

        /// <summary>0..20 字符；空 = 使用引擎默认名。</summary>
        public string Name { get; }

        /// <summary>FurnitureInteractionMode 的整数值（0..4）。</summary>
        public int InteractionMode { get; }

        /// <summary>多状态链的下一条设计索引；-1 = 无。</summary>
        public int LinkedIndex { get; }

        /// <summary>FurnitureSet 名；空 = Uncategorized。</summary>
        public string FurnitureSet { get; }

        /// <summary>灯值已清零的设计格值（与引擎 <c>FurnitureDesign.Save</c> 同口径）。</summary>
        public IReadOnlyList<int> Values => m_values;

        public int ValueCount => m_values.Length;

        /// <summary>
        /// 规范化内容哈希：resolution + interaction_mode + linked_index + name + set + 去灯值的 values。
        /// 用于主机去重 / 客户端幂等应用 / 内容表比对（不是密码学用途）。
        /// </summary>
        public long ContentHash
        {
            get
            {
                if (!m_contentHash.HasValue)
                    m_contentHash = FurnitureDesignHash.Compute(this);
                return m_contentHash.Value;
            }
        }

        public int GetValue(int index) => m_values[index];

        public FurnitureDesignRecord Clone() =>
            new FurnitureDesignRecord(Index, Resolution, Name, InteractionMode, LinkedIndex,
                FurnitureSet, m_values);

        /// <summary>同槽位、同内容（含名字 / 集合 / 链）→ 幂等，不重复安装。</summary>
        public bool SameContent(FurnitureDesignRecord other) =>
            other != null && other.Index == Index && other.ContentHash == ContentHash;

        /// <summary>只比较内容，不看槽位（主机去重判断用）。</summary>
        public bool SameDesign(FurnitureDesignRecord other) =>
            other != null && other.ContentHash == ContentHash;

        public string Describe() =>
            "#" + Index.ToString(CultureInfo.InvariantCulture) + " " +
            (string.IsNullOrEmpty(Name) ? "<default>" : Name) + " res=" +
            Resolution.ToString(CultureInfo.InvariantCulture) + " mode=" +
            InteractionMode.ToString(CultureInfo.InvariantCulture) + " linked=" +
            LinkedIndex.ToString(CultureInfo.InvariantCulture) +
            (string.IsNullOrEmpty(FurnitureSet) ? string.Empty : " set=" + FurnitureSet);
    }

    /// <summary>
    /// 规范化设计哈希 + 灯值剥离。**与引擎设计表无关**：只按记录内容计算。
    /// 来源：值布局见 Survivalcraft/Game/Terrain.cs:MakeBlockValue（contents 10 bit / light 4 bit / data）。
    /// </summary>
    public static class FurnitureDesignHash
    {
        /// <summary>灯值位（Terrain.MakeBlockValue 的 light 段 10..13）。</summary>
        public const int LightMask = 0x3C00;

        public static int StripLight(int value) => value & ~LightMask;

        public static long Compute(FurnitureDesignRecord record)
        {
            if (record == null) return 0L;
            unchecked
            {
                long hash = 1469598103934665603L;
                hash = Mix(hash, record.Resolution);
                hash = Mix(hash, record.InteractionMode);
                hash = Mix(hash, record.LinkedIndex);
                hash = MixString(hash, record.Name);
                hash = MixString(hash, record.FurnitureSet);
                hash = Mix(hash, record.ValueCount);
                IReadOnlyList<int> values = record.Values;
                for (int i = 0; i < values.Count; i++)
                    hash = Mix(hash, StripLight(values[i]));
                return hash == 0L ? 1L : hash;
            }
        }

        public static long Mix(long hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= 1099511628211L;
                return hash;
            }
        }

        public static long MixString(long hash, string text)
        {
            unchecked
            {
                if (text == null) return Mix(hash, -1);
                hash = Mix(hash, text.Length);
                for (int i = 0; i < text.Length; i++)
                    hash = Mix(hash, text[i]);
                return hash;
            }
        }
    }

    /// <summary>一次设计状态应用的结果（计数供状态行 / 日志）。</summary>
    public struct FurnitureDesignApplyResult
    {
        public int Added;
        public int Updated;
        public int Duplicate;
        public int Conflict;
        public int Removed;
        public int RemoveMissed;
        public bool Stale;
        public int Revision;
    }

    /// <summary>一次内容表对账的结果。</summary>
    public struct FurnitureContentReconcileResult
    {
        public int Matched;
        public int Missing;
        public int Extra;
        public bool Stale;
        public bool Malformed;
        public int Revision;
        /// <summary>因"主机内容表里没有"而必须从客户端副本移除的设计索引。</summary>
        public IReadOnlyList<int> RolledBack;
    }

    /// <summary>
    /// **客户端副本专用**的家具设计存储（会话级、内存、永不落盘）。
    ///
    /// 它只保存主机下发的记录；引擎侧 <c>SubsystemFurnitureBlockBehavior</c> 的
    /// <c>m_furnitureDesigns</c> 槽位只是这份记录的**渲染查找表**（引擎的 GetDesign 不是
    /// 虚方法，客户端要渲染就必须让槽位里有对象）。客户端本地任何引擎路径新增的设计
    /// 都不在这里 → 由 leak sweep 明确移除并计数（clientLibraryRejected）。
    ///
    /// 没有静态状态：一个客户端会话一个实例，组合根持有。
    /// </summary>
    public sealed class FurnitureDesignStore
    {
        public const int MaxDesigns = 1024;

        private readonly Dictionary<int, FurnitureDesignRecord> m_designs =
            new Dictionary<int, FurnitureDesignRecord>();

        /// <summary>已应用的权威 revision（主机设计表版本）。</summary>
        public int Revision { get; private set; }

        public int Count => m_designs.Count;

        public IReadOnlyCollection<int> Indices => m_designs.Keys;

        public bool Contains(int index) => m_designs.ContainsKey(index);

        public bool TryGet(int index, out FurnitureDesignRecord record) =>
            m_designs.TryGetValue(index, out record);

        public void Reset()
        {
            m_designs.Clear();
            Revision = 0;
        }

        /// <summary>
        /// 重置 revision 但保留副本设计。重连 / 主机重启后主机的 revision
        /// 域会从 1 重新开始，客户端若保留旧 revision 会把合法的全量快照当成 stale。
        /// </summary>
        public void ResetRevision() => Revision = 0;

        /// <summary>按索引排序的快照（日志 / 内容表构造用）。</summary>
        public List<FurnitureDesignRecord> SnapshotSorted()
        {
            var list = new List<FurnitureDesignRecord>(m_designs.Values);
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            return list;
        }

        /// <summary>
        /// 接管客户端世界里**已经加载**的设计（首次识别为联机客户端时调用一次）。
        /// 这些设计来自下载世界的存档（第一次加入）或上一次副本（重连）；接管后仍只是副本，
        /// 客户端 Save 不会写它们。返回接管条数。
        /// </summary>
        public int Adopt(IReadOnlyList<FurnitureDesignRecord> records, int revision,
            out int conflicts)
        {
            conflicts = 0;
            if (records != null)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    FurnitureDesignRecord record = records[i];
                    if (record == null || !IsValidIndex(record.Index)) continue;
                    if (m_designs.TryGetValue(record.Index, out FurnitureDesignRecord existing) &&
                        !existing.SameContent(record))
                    {
                        conflicts++;
                    }
                    m_designs[record.Index] = record.Clone();
                }
            }
            if (revision > Revision) Revision = revision;
            return m_designs.Count;
        }

        /// <summary>
        /// 应用一批主机权威记录 + 显式移除。revision 只允许前进（相等 = 同一次快照的
        /// 多个批次，幂等）；更旧的整批按 stale 丢弃（计数，不静默）。
        /// </summary>
        public FurnitureDesignApplyResult Apply(IReadOnlyList<FurnitureDesignRecord> upserts,
            IReadOnlyList<int> removed, int revision)
        {
            var result = new FurnitureDesignApplyResult { Revision = Revision };
            if (revision < Revision)
            {
                result.Stale = true;
                return result;
            }
            if (upserts != null)
            {
                for (int i = 0; i < upserts.Count; i++)
                {
                    FurnitureDesignRecord record = upserts[i];
                    if (record == null || !IsValidIndex(record.Index))
                    {
                        result.Conflict++;
                        continue;
                    }
                    if (m_designs.TryGetValue(record.Index, out FurnitureDesignRecord existing))
                    {
                        if (existing.SameContent(record))
                        {
                            result.Duplicate++;
                            continue;
                        }
                        result.Conflict++;
                        result.Updated++;
                        m_designs[record.Index] = record.Clone();
                    }
                    else
                    {
                        result.Added++;
                        m_designs.Add(record.Index, record.Clone());
                    }
                }
            }
            if (removed != null)
            {
                for (int i = 0; i < removed.Count; i++)
                {
                    int index = removed[i];
                    if (m_designs.Remove(index)) result.Removed++;
                    else result.RemoveMissed++;
                }
            }
            if (revision > Revision) Revision = revision;
            result.Revision = Revision;
            return result;
        }

        /// <summary>
        /// 用主机的家具内容表（设计索引 + 内容哈希）对账客户端副本：
        ///   · 表里没有的本地设计 → 回滚移除（Extra / RolledBack）；
        ///   · 表里有但本地缺失 / 哈希不同 → Missing（由调用方请求全量同步）。
        /// </summary>
        public FurnitureContentReconcileResult Reconcile(IReadOnlyList<int> indices,
            IReadOnlyList<long> hashes, int revision)
        {
            var result = new FurnitureContentReconcileResult { Revision = Revision };
            if (indices == null || hashes == null || indices.Count != hashes.Count)
            {
                result.Malformed = true;
                return result;
            }
            if (revision < Revision)
            {
                result.Stale = true;
                return result;
            }
            var expected = new HashSet<int>();
            for (int i = 0; i < indices.Count; i++)
            {
                int index = indices[i];
                if (!IsValidIndex(index) || !expected.Add(index))
                {
                    result.Malformed = true;   // 非法 / 重复索引：整条内容表不可信
                    return result;
                }
                if (m_designs.TryGetValue(index, out FurnitureDesignRecord record) &&
                    record.ContentHash == hashes[i])
                {
                    result.Matched++;
                }
                else
                {
                    result.Missing++;
                }
            }
            var rolledBack = new List<int>();
            foreach (KeyValuePair<int, FurnitureDesignRecord> item in m_designs)
            {
                if (expected.Contains(item.Key)) continue;
                rolledBack.Add(item.Key);
            }
            for (int i = 0; i < rolledBack.Count; i++)
                m_designs.Remove(rolledBack[i]);
            result.Extra = rolledBack.Count;
            result.RolledBack = rolledBack;
            if (revision > Revision) Revision = revision;
            result.Revision = Revision;
            return result;
        }

        public static bool IsValidIndex(int index) => index >= 0 && index < MaxDesigns;
    }

    /// <summary>
    /// 客户端设计库的**持久化策略**（唯一事实来源，Game 外壳与自检都调用它）：
    /// 联机客户端上的家具设计只是当前世界的会话副本，**绝不写进客户端自己的
    /// FurnitureDesigns / FurnitureSets 存档**；主机 / 单机保持引擎原生保存。
    ///
    /// 为什么必须由客户端 Save 显式写空：下载世界里的家具格 data 指向主机设计槽位，
    /// 若客户端把设计写进自己的存档，下次用该存档开局（或被别的世界复用）就会把
    /// 联机设计带进客户端家具库 —— 正是本功能要禁止的污染。
    /// </summary>
    public static class FurnitureSavePolicy
    {
        public static bool WritesDesigns(bool isMultiplayerClient) => !isMultiplayerClient;
    }

    /// <summary>家具设计表的稳定 revision / 指纹（主机轮询"变化才抓取"用，纯逻辑）。</summary>
    public static class FurnitureDesignFingerprint
    {
        public static long Of(FurnitureDesignRecord record)
        {
            if (record == null) return 0L;
            unchecked
            {
                long hash = record.Index;
                hash = FurnitureDesignHash.Mix(hash, record.ContentHash.GetHashCode());
                hash ^= record.ContentHash;
                hash *= 1099511628211L;
                return hash;
            }
        }

        public static long OfAll(IReadOnlyList<FurnitureDesignRecord> records)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                if (records != null)
                {
                    for (int i = 0; i < records.Count; i++)
                    {
                        FurnitureDesignRecord record = records[i];
                        if (record == null) continue;
                        hash = FurnitureDesignHash.Mix(hash, record.Index);
                        hash = FurnitureDesignHash.MixString(hash, record.Name);
                        hash = FurnitureDesignHash.Mix(hash, record.InteractionMode);
                        hash = FurnitureDesignHash.Mix(hash, record.LinkedIndex);
                        hash = FurnitureDesignHash.MixString(hash, record.FurnitureSet);
                        hash ^= record.ContentHash;
                        hash *= 1099511628211L;
                    }
                }
                return hash == 0L ? 1L : hash;
            }
        }
    }
}
