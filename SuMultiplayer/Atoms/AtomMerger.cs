using System;
using System.Collections.Generic;

namespace SuMultiplayer.Atoms
{
    /// <summary>一条待发送的原子样本（合并器输入）。</summary>
    public struct AtomSample
    {
        public ushort AtomId;
        public byte Version;
        public long Seq;
        public int ActorId;
        public int ShardId;
        /// <summary>同一对象的多条更新用同一个 key（CoalesceByKey 用）。</summary>
        public int MergeKey;
        /// <summary>负载字节数（合并器只用它做包大小预算）。</summary>
        public int PayloadBytes;
    }

    /// <summary>合并输出的一条原子。</summary>
    public sealed class MergedAtom
    {
        public ushort AtomId;
        public byte Version;
        public long Seq;
        public int ActorId;
        public int ShardId;
        public int MergeKey;
        public int PayloadBytes;
        /// <summary>本次合并了多少条原始样本（Accumulate 用它表达"应用 N 次"）。</summary>
        public int Count;
    }

    /// <summary>合并输出的一个报文（同 tier / 同投递类 / 同目标）。</summary>
    public sealed class MergedPacket
    {
        public long PacketSeq;
        public AtomTier Tier;
        public AtomDelivery Delivery;
        public int ShardId;
        public int ActorId = -1;
        public bool Urgent;
        public readonly List<MergedAtom> Atoms = new List<MergedAtom>();
        public int PayloadBytes;
        public int Bytes => PayloadBytes + Atoms.Count * 8;
    }

    public readonly struct MergeStats
    {
        public MergeStats(long samplesIn, long atomsOut, long packetsOut, long bytesIn, long bytesOut,
            long rejectedUnknown, long droppedOverflow, long urgentPackets)
        {
            SamplesIn = samplesIn;
            AtomsOut = atomsOut;
            PacketsOut = packetsOut;
            BytesIn = bytesIn;
            BytesOut = bytesOut;
            RejectedUnknown = rejectedUnknown;
            DroppedOverflow = droppedOverflow;
            UrgentPackets = urgentPackets;
        }

        public long SamplesIn { get; }
        public long AtomsOut { get; }
        public long PacketsOut { get; }
        public long BytesIn { get; }
        public long BytesOut { get; }
        public long RejectedUnknown { get; }
        public long DroppedOverflow { get; }
        public long UrgentPackets { get; }
        /// <summary>合并带来的报文数/字节下降比例。</summary>
        public double AtomReduction => SamplesIn == 0 ? 0 : 1.0 - (double)AtomsOut / SamplesIn;
        public double ByteReduction => BytesIn == 0 ? 0 : 1.0 - (double)BytesOut / BytesIn;
    }

    public sealed class MergeOptions
    {
        /// <summary>网络 tick 频率（用于把 Hz 档位换算成 tick 桶）。</summary>
        public int TicksPerSecond { get; set; } = 100;

        /// <summary>单个报文最多几条原子。</summary>
        public int MaxAtomsPerPacket { get; set; } = 24;

        /// <summary>单个报文字节上限（默认贴合 Comms 的 1200 B 数据报上限）。</summary>
        public int MaxPacketBytes { get; set; } = 1200;

        /// <summary>是否允许把不同 actor 的原子合进同一个包（默认否：便于按来源审计与限流）。</summary>
        public bool MergeAcrossActors { get; set; }

        /// <summary>单个桶最多缓存多少样本；超出后：可丢弃类丢最旧，不可丢弃类拒新（背压）。</summary>
        public int MaxSamplesPerBucket { get; set; } = 512;

        /// <summary>每包固定的协议开销（信封头 + 每原子 id/版本/长度）。</summary>
        public int EnvelopeOverheadBytes { get; set; } = 24;
        public int PerAtomOverheadBytes { get; set; } = 8;
    }

    /// <summary>
    /// Hz 分层合并器。规则（对应演示页的六条）：
    /// <list type="number">
    /// <item>只合并**同投递类**（可丢弃 / 不可丢弃互不混装）；</item>
    /// <item>只合并**同 tier 桶**（低频数据不搭高频包的车）；</item>
    /// <item>每条原子保留自己的 Seq（包序号只给传输层用）；</item>
    /// <item>合并策略由原子契约声明（NoMerge / Replace / CoalesceByKey / Accumulate）；</item>
    /// <item>急件（Urgent）绕过窗口立即成包；</item>
    /// <item>成包后仍受"条数 + 字节"双上限约束。</item>
    /// </list>
    /// 演示页标记：MRG01。
    /// </summary>
    public sealed class AtomMerger
    {
        private sealed class Bucket
        {
            public AtomTier Tier;
            public AtomDelivery Delivery;
            public int ShardId;
            public int ActorId;
            public long DueTick;
            public readonly List<AtomSample> Samples = new List<AtomSample>();
        }

        /// <summary>桶键 = 档位 + 投递类 + 目标 + **窗口**（窗口是键的一部分，否则新窗口的样本会被吸进旧桶）。</summary>
        private readonly struct BucketKey : IEquatable<BucketKey>
        {
            private readonly byte m_tier;
            private readonly byte m_delivery;
            private readonly int m_shard;
            private readonly int m_actor;
            private readonly long m_due;

            public BucketKey(AtomTier tier, AtomDelivery delivery, int shard, int actor, long due)
            {
                m_tier = (byte)tier;
                m_delivery = (byte)delivery;
                m_shard = shard;
                m_actor = actor;
                m_due = due;
            }

            public bool Equals(BucketKey other) =>
                m_tier == other.m_tier && m_delivery == other.m_delivery && m_shard == other.m_shard &&
                m_actor == other.m_actor && m_due == other.m_due;

            public override bool Equals(object obj) => obj is BucketKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = m_tier * 397 ^ m_delivery;
                    hash = hash * 397 ^ m_shard;
                    hash = hash * 397 ^ m_actor;
                    hash = hash * 397 ^ m_due.GetHashCode();
                    return hash;
                }
            }
        }

        private readonly AtomRegistry m_registry;
        private readonly MergeOptions m_options;
        private readonly Dictionary<BucketKey, Bucket> m_buckets = new Dictionary<BucketKey, Bucket>();
        private readonly List<MergedPacket> m_urgent = new List<MergedPacket>();
        private long m_packetSeq;
        private long m_samplesIn, m_atomsOut, m_packetsOut, m_bytesIn, m_bytesOut;
        private long m_rejectedUnknown, m_droppedOverflow, m_urgentPackets;

        public AtomMerger(AtomRegistry registry, MergeOptions options = null)
        {
            m_registry = registry ?? throw new ArgumentNullException(nameof(registry));
            m_options = options ?? new MergeOptions();
        }

        public MergeStats Stats => new MergeStats(m_samplesIn, m_atomsOut, m_packetsOut,
            m_bytesIn, m_bytesOut, m_rejectedUnknown, m_droppedOverflow, m_urgentPackets);

        public int PendingBucketCount => m_buckets.Count;

        private long BucketTicks(AtomTier tier)
        {
            int hz = (int)tier;
            if (hz <= 0) hz = 1;
            int ticks = m_options.TicksPerSecond / hz;
            return ticks < 1 ? 1 : (m_options.TicksPerSecond % hz == 0 ? ticks : ticks + 1);
        }

        /// <summary>下一个对齐窗口的发送 tick（同一窗口内到达的样本合并成一个包）。</summary>
        internal static long NextDueTick(long arrivalTick, long windowTicks)
        {
            if (windowTicks <= 0) windowTicks = 1;
            if (arrivalTick < 0) arrivalTick = 0;
            return (arrivalTick / windowTicks + 1) * windowTicks;
        }

        /// <summary>入桶。返回 false 表示被拒（未注册的原子 = 契约违规）。</summary>
        public bool Enqueue(in AtomSample sample, long arrivalTick)
        {
            if (!m_registry.TryResolve(sample.AtomId, sample.Version, out AtomDescriptor descriptor))
            {
                m_rejectedUnknown++;
                return false;
            }

            if (descriptor.Urgent)
            {
                var urgentPacket = new MergedPacket
                {
                    PacketSeq = ++m_packetSeq,
                    Tier = descriptor.Tier,
                    Delivery = descriptor.Delivery,
                    ShardId = sample.ShardId,
                    ActorId = sample.ActorId,
                    Urgent = true
                };
                urgentPacket.Atoms.Add(ToMerged(sample, 1));
                urgentPacket.PayloadBytes = sample.PayloadBytes;
                m_urgent.Add(urgentPacket);
                m_urgentPackets++;
                m_samplesIn++;
                m_atomsOut++;
                m_packetsOut++;
                m_bytesIn += sample.PayloadBytes;
                m_bytesOut += urgentPacket.Bytes;
                return true;
            }

            long window = BucketTicks(descriptor.Tier);
            long due = NextDueTick(arrivalTick, window);
            var key = new BucketKey(descriptor.Tier, descriptor.Delivery, sample.ShardId, sample.ActorId, due);
            if (!m_buckets.TryGetValue(key, out Bucket bucket))
            {
                bucket = new Bucket
                {
                    Tier = descriptor.Tier,
                    Delivery = descriptor.Delivery,
                    ShardId = sample.ShardId,
                    ActorId = sample.ActorId,
                    DueTick = due
                };
                m_buckets.Add(key, bucket);
            }

            if (bucket.Samples.Count >= m_options.MaxSamplesPerBucket)
            {
                // 可丢弃：丢最旧保新；不可丢弃：拒新（背压），绝不静默丢关键数据
                if (descriptor.Delivery == AtomDelivery.Replaceable) bucket.Samples.RemoveAt(0);
                else { m_droppedOverflow++; return false; }
                m_droppedOverflow++;
            }

            bucket.Samples.Add(sample);
            m_samplesIn++;
            m_bytesIn += sample.PayloadBytes;
            return true;
        }

        /// <summary>到达 tick：把到期的桶打成报文（含急件）。返回本次输出的报文数。</summary>
        public int Flush(long tickIndex, List<MergedPacket> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            int packets = 0;
            for (int i = 0; i < m_urgent.Count; i++) { output.Add(m_urgent[i]); packets++; }
            m_urgent.Clear();
            if (m_buckets.Count == 0) return packets;

            var due = new List<KeyValuePair<BucketKey, Bucket>>();
            foreach (KeyValuePair<BucketKey, Bucket> pair in m_buckets)
            {
                if (pair.Value.DueTick <= tickIndex) due.Add(pair);
            }
            due.Sort((a, b) =>
            {
                int byTick = a.Value.DueTick.CompareTo(b.Value.DueTick);
                if (byTick != 0) return byTick;
                int byTier = ((byte)a.Value.Tier).CompareTo((byte)b.Value.Tier);
                if (byTier != 0) return byTier;
                int byShard = a.Value.ShardId.CompareTo(b.Value.ShardId);
                if (byShard != 0) return byShard;
                return a.Value.ActorId.CompareTo(b.Value.ActorId);
            });

            foreach (KeyValuePair<BucketKey, Bucket> pair in due)
            {
                packets += EmitBucket(pair.Value, output);
                m_buckets.Remove(pair.Key);
            }
            return packets;
        }

        /// <summary>无视时间桶，全部输出（关闭 / 强制同步用）。</summary>
        public int FlushAll(List<MergedPacket> output)
        {
            int packets = Flush(long.MaxValue, output);
            return packets;
        }

        private int EmitBucket(Bucket bucket, List<MergedPacket> output)
        {
            var merged = new List<MergedAtom>();
            var byAtom = new Dictionary<ushort, List<AtomSample>>();
            var order = new List<ushort>();
            foreach (AtomSample sample in bucket.Samples)
            {
                if (!byAtom.TryGetValue(sample.AtomId, out List<AtomSample> list))
                {
                    list = new List<AtomSample>();
                    byAtom.Add(sample.AtomId, list);
                    order.Add(sample.AtomId);
                }
                list.Add(sample);
            }

            foreach (ushort atomId in order)
            {
                List<AtomSample> samples = byAtom[atomId];
                AtomDescriptor descriptor = null;
                m_registry.TryResolve(samples[0].AtomId, samples[0].Version, out descriptor);
                switch (descriptor.Merge)
                {
                    case AtomMergePolicy.NoMerge:
                        foreach (AtomSample sample in samples) merged.Add(ToMerged(sample, 1));
                        break;
                    case AtomMergePolicy.Replace:
                        merged.Add(ToMerged(samples[samples.Count - 1], samples.Count));
                        break;
                    case AtomMergePolicy.CoalesceByKey:
                    {
                        var byKey = new Dictionary<int, AtomSample>();
                        var keyOrder = new List<int>();
                        foreach (AtomSample sample in samples)
                        {
                            if (!byKey.ContainsKey(sample.MergeKey)) keyOrder.Add(sample.MergeKey);
                            byKey[sample.MergeKey] = sample;   // 后值覆盖前值
                        }
                        foreach (int key in keyOrder)
                        {
                            int count = 0;
                            foreach (AtomSample sample in samples) if (sample.MergeKey == key) count++;
                            merged.Add(ToMerged(byKey[key], count));
                        }
                        break;
                    }
                    case AtomMergePolicy.Accumulate:
                    {
                        AtomSample last = samples[samples.Count - 1];
                        MergedAtom atom = ToMerged(last, samples.Count);
                        atom.Count = samples.Count;   // 接收端按 Count 应用
                        merged.Add(atom);
                        break;
                    }
                }
            }

            int packets = 0;
            int index = 0;
            while (index < merged.Count)
            {
                var packet = new MergedPacket
                {
                    PacketSeq = ++m_packetSeq,
                    Tier = bucket.Tier,
                    Delivery = bucket.Delivery,
                    ShardId = bucket.ShardId,
                    ActorId = bucket.ActorId
                };
                int bytes = m_options.EnvelopeOverheadBytes;
                while (index < merged.Count && packet.Atoms.Count < m_options.MaxAtomsPerPacket)
                {
                    int atomBytes = merged[index].PayloadBytes + m_options.PerAtomOverheadBytes;
                    if (packet.Atoms.Count > 0 && bytes + atomBytes > m_options.MaxPacketBytes) break;
                    packet.Atoms.Add(merged[index]);
                    packet.PayloadBytes += merged[index].PayloadBytes;
                    bytes += atomBytes;
                    index++;
                }
                output.Add(packet);
                m_atomsOut += packet.Atoms.Count;
                m_packetsOut++;
                m_bytesOut += packet.Bytes;
                packets++;
            }
            return packets;
        }

        private static MergedAtom ToMerged(in AtomSample sample, int count) => new MergedAtom
        {
            AtomId = sample.AtomId,
            Version = sample.Version,
            Seq = sample.Seq,
            ActorId = sample.ActorId,
            ShardId = sample.ShardId,
            MergeKey = sample.MergeKey,
            PayloadBytes = sample.PayloadBytes,
            Count = count
        };
    }
}
