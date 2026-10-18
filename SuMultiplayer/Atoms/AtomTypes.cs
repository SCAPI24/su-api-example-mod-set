using System;
using System.Collections.Generic;
using SuMultiplayer.Journal;

namespace SuMultiplayer.Atoms
{
    /// <summary>原子种类。决定谁能执行、能不能预测。演示页标记：ATM01。</summary>
    public enum AtomKind : byte
    {
        /// <summary>只读查询（可缓存、可丢弃）。</summary>
        Query = 0,
        /// <summary>客户端意图（请求 + 回执）。</summary>
        Intent = 1,
        /// <summary>主机结果（可靠）。</summary>
        Effect = 2,
        /// <summary>可替换状态快照。</summary>
        State = 3,
        /// <summary>一次性事件（可靠、不可合并）。</summary>
        Event = 4,
        /// <summary>账本事务（幂等、可审计）。</summary>
        Ledger = 5
    }

    public enum AtomAuthority : byte
    {
        HostOnly = 0,
        ClientPredictable = 1,
        Either = 2
    }

    /// <summary>投递语义：可丢弃 / 不可丢弃（见上一轮序号方案）。</summary>
    public enum AtomDelivery : byte
    {
        Replaceable = 0,
        Reliable = 1
    }

    /// <summary>合并策略：合并器只认这一条。演示页标记：ATM02。</summary>
    public enum AtomMergePolicy : byte
    {
        /// <summary>永不合并（事件、账本）。</summary>
        NoMerge = 0,
        /// <summary>同 key 只留最后一个（可替换状态）。</summary>
        Replace = 1,
        /// <summary>按 MergeKey 分桶，每桶留最后一个（同一对象的多条更新）。</summary>
        CoalesceByKey = 2,
        /// <summary>保留条数并用 RepeatCount 表达（累加类事件）。</summary>
        Accumulate = 3
    }

    public enum AtomOrderRequirement : byte
    {
        None = 0,
        PerActor = 1,
        PerShard = 2
    }

    /// <summary>丢包之后靠谁收敛。</summary>
    public enum AtomReconcile : byte
    {
        None = 0,
        NextSnapshot = 1,
        PeriodicFloor = 2,
        OnDemandRequest = 3,
        SnapshotRebuild = 4
    }

    /// <summary>采样率分层（合并分桶按它对齐）。</summary>
    public enum AtomTier : byte
    {
        Hz100 = 100,
        Hz16 = 16,
        Hz8 = 8,
        Hz4 = 4,
        Hz2 = 2,
        Hz1 = 1
    }

    /// <summary>
    /// 原子契约。**这张表是唯一的策略来源**：合并器、校验入口、审计、能力协商都从这里读。
    /// 演示页标记：ATM03。
    /// </summary>
    public sealed class AtomDescriptor
    {
        public AtomDescriptor(ushort id, byte version, string name, AtomKind kind)
        {
            Id = id;
            Version = version;
            Name = name ?? ("atom" + id);
            Kind = kind;
        }

        public ushort Id { get; }
        public byte Version { get; }
        public string Name { get; }
        public AtomKind Kind { get; }

        public AtomAuthority Authority { get; set; } = AtomAuthority.HostOnly;
        public AtomDelivery Delivery { get; set; } = AtomDelivery.Reliable;
        public AtomMergePolicy Merge { get; set; } = AtomMergePolicy.NoMerge;
        public AtomOrderRequirement Order { get; set; } = AtomOrderRequirement.PerActor;
        public AtomReconcile Reconcile { get; set; } = AtomReconcile.None;
        public AtomTier Tier { get; set; } = AtomTier.Hz8;
        public JournalCategory Journal { get; set; } = JournalCategory.Diagnostics;

        /// <summary>所需能力（空 = 基础能力）。用于能力协商与模块下线。</summary>
        public string Capability { get; set; } = string.Empty;

        /// <summary>单个原子的负载上限（防畸形包）。</summary>
        public int MaxPayloadBytes { get; set; } = 1024;

        /// <summary>急件：绕过合并窗口立即发送（Intent / Effect / Ledger 通常为 true）。</summary>
        public bool Urgent { get; set; }

        public AtomDescriptor Clone(byte version) => new AtomDescriptor(Id, version, Name, Kind)
        {
            Authority = Authority, Delivery = Delivery, Merge = Merge, Order = Order,
            Reconcile = Reconcile, Tier = Tier, Journal = Journal, Capability = Capability,
            MaxPayloadBytes = MaxPayloadBytes, Urgent = Urgent
        };
    }

    /// <summary>注册结果（用于启动自检给出可读原因）。</summary>
    public readonly struct AtomRegistration
    {
        public AtomRegistration(bool accepted, string reason)
        {
            Accepted = accepted;
            Reason = reason ?? string.Empty;
        }

        public bool Accepted { get; }
        public string Reason { get; }
    }

    /// <summary>
    /// 原子注册表：**同一个 atomId 允许多个版本共存**（前向兼容的基础）。
    /// 解析规则：精确版本优先；否则取"不高于请求版本的最高已知版本"（降级解析）。
    /// 演示页标记：ATM04。
    /// </summary>
    public sealed class AtomRegistry
    {
        private readonly Dictionary<ushort, List<AtomDescriptor>> m_byId = new Dictionary<ushort, List<AtomDescriptor>>();
        private readonly Dictionary<int, AtomDescriptor> m_exact = new Dictionary<int, AtomDescriptor>();
        private readonly List<AtomDescriptor> m_all = new List<AtomDescriptor>();

        public IReadOnlyList<AtomDescriptor> All => m_all;

        private static int Key(ushort id, byte version) => (id << 8) | version;

        public AtomRegistration Register(AtomDescriptor descriptor)
        {
            if (descriptor == null) return new AtomRegistration(false, "descriptor 为 null");
            if (descriptor.Id == 0) return new AtomRegistration(false, "atomId 0 保留");
            if (descriptor.MaxPayloadBytes <= 0) return new AtomRegistration(false, "MaxPayloadBytes 必须为正");
            if (descriptor.Merge == AtomMergePolicy.CoalesceByKey && descriptor.Delivery == AtomDelivery.Reliable
                && descriptor.Kind == AtomKind.Event)
                return new AtomRegistration(false, "事件类不允许 CoalesceByKey（会静默吞事件）");
            if (m_exact.ContainsKey(Key(descriptor.Id, descriptor.Version)))
                return new AtomRegistration(false, "重复注册：" + descriptor.Name + " v" + descriptor.Version);

            if (!m_byId.TryGetValue(descriptor.Id, out List<AtomDescriptor> versions))
            {
                versions = new List<AtomDescriptor>();
                m_byId.Add(descriptor.Id, versions);
            }
            versions.Add(descriptor);
            versions.Sort((a, b) => a.Version.CompareTo(b.Version));
            m_exact.Add(Key(descriptor.Id, descriptor.Version), descriptor);
            m_all.Add(descriptor);
            return new AtomRegistration(true, string.Empty);
        }

        public bool TryGetExact(ushort id, byte version, out AtomDescriptor descriptor) =>
            m_exact.TryGetValue(Key(id, version), out descriptor);

        /// <summary>解析：精确版本 → 降级到不高于请求版本的最高版本。</summary>
        public bool TryResolve(ushort id, byte version, out AtomDescriptor descriptor)
        {
            descriptor = null;
            if (!m_byId.TryGetValue(id, out List<AtomDescriptor> versions)) return false;
            for (int i = versions.Count - 1; i >= 0; i--)
            {
                if (versions[i].Version <= version)
                {
                    descriptor = versions[i];
                    return true;
                }
            }
            return false;
        }

        public bool IsKnown(ushort id) => m_byId.ContainsKey(id);

        /// <summary>启动自检：返回所有不合规条目（空 = 通过）。</summary>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();
            foreach (KeyValuePair<ushort, List<AtomDescriptor>> pair in m_byId)
            {
                List<AtomDescriptor> versions = pair.Value;
                for (int i = 1; i < versions.Count; i++)
                {
                    AtomDescriptor older = versions[i - 1], newer = versions[i];
                    if (newer.Kind != older.Kind)
                        problems.Add(newer.Name + " v" + newer.Version + " 改变了 Kind（破坏兼容）");
                    if (newer.Delivery != older.Delivery)
                        problems.Add(newer.Name + " v" + newer.Version + " 改变了 Delivery（破坏合并语义）");
                    if (newer.Tier != older.Tier)
                        problems.Add(newer.Name + " v" + newer.Version + " 改变了 Tier");
                }
            }
            return problems;
        }
    }
}
