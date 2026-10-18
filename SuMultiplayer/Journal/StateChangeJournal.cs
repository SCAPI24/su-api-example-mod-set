using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SuMultiplayer.Journal
{
    /// <summary>
    /// 状态变化队列（内存）。设计要点：
    /// <list type="bullet">
    /// <item><b>线性写入</b>：定长 48 字节记录写进 2 的幂环形数组，O(1)、零分配、无锁（单生产者 = 游戏线程）；</item>
    /// <item><b>单调序号</b>：每条记录一个永不重复的序号，筛选、增量消费、落盘续写都以它为准；</item>
    /// <item><b>快速按类别筛选</b>：位掩码 + 顺序扫描（顺序内存，无分支预测灾难），
    ///       另有一条按分类的稀疏索引用于「最近 N 条某分类」；</item>
    /// <item><b>快速存盘</b>：落盘端只顺序读序号区间，不遍历、不分配，见 <see cref="JournalSegmentWriter"/>。</item>
    /// </list>
    /// 演示页标记：Q01。
    /// </summary>
    public sealed class StateChangeJournal
    {
        private readonly JournalOptions m_options;
        private readonly JournalRecord[] m_records;
        private readonly int m_mask;
        private readonly string[] m_payloadText;
        private readonly long[] m_payloadSequence;
        private readonly int m_payloadMask;
        private readonly long[][] m_categorySequences;
        private readonly int m_categoryMask;

        private long m_nextSequence = 1L;
        private long m_totalAppended;
        private long m_publishedSequence;
        private int m_payloadHead;
        private long m_payloadCount;
        private readonly int[] m_categoryHead = new int[32];
        private readonly long[] m_categoryCount = new long[32];
        private long m_overwrittenUnflushed;
        private long m_filterScans;
        private long m_lastFilterCount;
        private long m_lastFilterMicros;

        public StateChangeJournal(JournalOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();
            m_options = options;
            m_records = new JournalRecord[options.Capacity];
            m_mask = options.Capacity - 1;
            m_payloadText = new string[options.PayloadPoolCapacity];
            m_payloadSequence = new long[options.PayloadPoolCapacity];
            m_payloadMask = options.PayloadPoolCapacity - 1;
            m_categorySequences = new long[32][];
            m_categoryMask = options.CategoryIndexCapacity - 1;
        }

        public JournalOptions Options => m_options;

        /// <summary>下一条将被写入的序号（读端用它判断"有没有新东西"）。</summary>
        public long NextSequence => Volatile.Read(ref m_nextSequence);

        /// <summary>已发布的最后一条序号（内容已可见）。</summary>
        public long PublishedSequence => Volatile.Read(ref m_publishedSequence);

        public long TotalAppended => Volatile.Read(ref m_totalAppended);

        /// <summary>仍在内存里最旧的序号；更旧的已被环形覆盖。</summary>
        public long OldestSequence
        {
            get
            {
                long published = Volatile.Read(ref m_publishedSequence);
                long capacity = m_options.Capacity;
                return published - capacity + 1 > 1L ? published - capacity + 1 : 1L;
            }
        }

        /// <summary>环形容量（条）。</summary>
        public int Capacity => m_options.Capacity;

        // ------------------------------------------------------------------
        // 写入
        // ------------------------------------------------------------------

        /// <summary>
        /// 追加一条状态变化（原始重载：分类与事件码已经是数值）。
        /// 只在游戏线程调用（单生产者）；不分配、不阻塞、不做 I/O。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long AppendRaw(uint category, ushort code, JournalRole role,
            JournalSeverity severity = JournalSeverity.Info, int actorId = -1,
            int valueA = 0, int valueB = 0, int valueC = 0, string payload = null)
        {
            long sequence = m_nextSequence;
            int index = (int)(sequence & m_mask);
            int payloadRef = payload == null ? -1 : StorePayload(sequence, payload);

            m_records[index] = new JournalRecord(sequence, m_options.ClockMs(), category, code,
                role, severity, actorId, valueA, valueB, valueC, payloadRef);

            m_totalAppended++;
            m_nextSequence = sequence + 1;
            // 发布：槽位内容先写好，再用 release 写序号，读端就不会看到半条记录
            Volatile.Write(ref m_publishedSequence, sequence);

            RecordCategoryIndex(sequence, category);
            return sequence;
        }

        public long Append(JournalCategory category, JournalEventCode code, JournalRole role,
            JournalSeverity severity = JournalSeverity.Info, int actorId = -1,
            int valueA = 0, int valueB = 0, int valueC = 0, string payload = null) =>
            AppendRaw((uint)category, (ushort)code, role, severity, actorId, valueA, valueB, valueC, payload);

        private int StorePayload(long sequence, string payload)
        {
            int slot = m_payloadHead & m_payloadMask;
            m_payloadHead++;
            m_payloadText[slot] = payload;
            Volatile.Write(ref m_payloadSequence[slot], sequence);
            m_payloadCount++;
            return slot;
        }

        /// <summary>取回字符串池里的文本；已被覆盖则返回 false（记录本身仍在）。</summary>
        public bool TryGetPayload(in JournalRecord record, out string payload)
        {
            payload = null;
            int slot = record.PayloadRef;
            if (slot < 0 || slot > m_payloadMask) return false;
            if (Volatile.Read(ref m_payloadSequence[slot]) != record.Sequence) return false;
            payload = m_payloadText[slot];
            return payload != null;
        }

        private void RecordCategoryIndex(long sequence, uint category)
        {
            if (category == 0u) return;
            for (int bit = 0; bit < 16; bit++)
            {
                if ((category & (1u << bit)) == 0u) continue;
                long[] ring = m_categorySequences[bit];
                if (ring == null)
                {
                    ring = new long[m_options.CategoryIndexCapacity];
                    m_categorySequences[bit] = ring;
                }
                ring[m_categoryHead[bit] & m_categoryMask] = sequence;
                m_categoryHead[bit]++;
                m_categoryCount[bit]++;
            }
        }

        /// <summary>已覆盖但未落盘的数量由落盘端统计（见 <see cref="ReportOverwrittenUnflushed"/>）。</summary>
        internal void ReportOverwrittenUnflushed(long count) =>
            Interlocked.Add(ref m_overwrittenUnflushed, count);

        // ------------------------------------------------------------------
        // 读取
        // ------------------------------------------------------------------

        /// <summary>按序号取一条记录；已被环形覆盖则返回 false。</summary>
        public bool TryGet(long sequence, out JournalRecord record)
        {
            record = default;
            // Volatile 读序号 = acquire 栅栏，之后再读槽位不会看到半条记录
            long published = Volatile.Read(ref m_publishedSequence);
            long oldest = published - m_options.Capacity + 1;
            if (oldest < 1L) oldest = 1L;
            if (sequence < oldest || sequence > published) return false;
            JournalRecord candidate = m_records[(int)(sequence & m_mask)];
            if (candidate.Sequence != sequence) return false;
            record = candidate;
            return true;
        }

        /// <summary>顺序复制一段序号区间到目标缓冲（落盘端用）。返回实际条数。</summary>
        public int CopyRange(long fromSequence, Span<JournalRecord> destination)
        {
            int written = 0;
            long published = Volatile.Read(ref m_publishedSequence);
            long oldest = published - m_options.Capacity + 1;
            if (oldest < 1L) oldest = 1L;
            if (fromSequence < oldest) fromSequence = oldest;
            for (long sequence = fromSequence; sequence <= published && written < destination.Length; sequence++)
            {
                JournalRecord candidate = m_records[(int)(sequence & m_mask)];
                if (candidate.Sequence != sequence) continue;
                destination[written++] = candidate;
            }
            return written;
        }

        /// <summary>
        /// 创建筛选器。筛选是「对内存窗口的一次顺序扫描」：定长记录、连续内存，
        /// 因此全量扫描 6.5 万条也在百微秒量级（见演示页实测表）。
        /// </summary>
        public JournalFilter CreateFilter(in JournalFilterSpec spec) => new JournalFilter(this, spec);

        /// <summary>「最近 N 条某分类」走稀疏索引：O(结果条数)，不扫描整个环形队列。</summary>
        public int CopyRecent(JournalCategory category, Span<JournalRecord> destination)
        {
            if (destination.Length == 0) return 0;
            int written = 0;
            long oldest = OldestSequence;
            for (int bit = 0; bit < 16 && written < destination.Length; bit++)
            {
                if (((uint)category & (1u << bit)) == 0u) continue;
                long[] ring = m_categorySequences[bit];
                if (ring == null) continue;
                int head = m_categoryHead[bit];
                long count = m_categoryCount[bit];
                int available = (int)Math.Min(count, m_options.CategoryIndexCapacity);
                for (int i = 1; i <= available && written < destination.Length; i++)
                {
                    int slot = (head - i) & m_categoryMask;
                    long sequence = ring[slot];
                    if (sequence < oldest) break;
                    if (TryGet(sequence, out JournalRecord record)) destination[written++] = record;
                }
            }
            return written;
        }

        internal void NoteFilter(int count, long elapsedMicros)
        {
            Interlocked.Increment(ref m_filterScans);
            Volatile.Write(ref m_lastFilterCount, count);
            Volatile.Write(ref m_lastFilterMicros, elapsedMicros);
        }

        public JournalStats GetStats()
        {
            long published = Volatile.Read(ref m_publishedSequence);
            long oldest = OldestSequence;
            long inMemory = published >= oldest ? published - oldest + 1 : 0;
            return new JournalStats(m_options.Capacity, inMemory, TotalAppended, oldest,
                Volatile.Read(ref m_nextSequence), Volatile.Read(ref m_overwrittenUnflushed),
                Volatile.Read(ref m_filterScans), Volatile.Read(ref m_lastFilterCount),
                Volatile.Read(ref m_lastFilterMicros) / 1000.0);
        }

        /// <summary>把内存队列清空（会话结束时调用）。序号不回退，保证外部引用不会错位。</summary>
        public void Reset()
        {
            for (int i = 0; i < m_records.Length; i++) m_records[i] = default;
            Array.Clear(m_payloadText, 0, m_payloadText.Length);
            Array.Clear(m_payloadSequence, 0, m_payloadSequence.Length);
            for (int bit = 0; bit < 32; bit++)
            {
                long[] ring = m_categorySequences[bit];
                if (ring != null) Array.Clear(ring, 0, ring.Length);
                m_categoryHead[bit] = 0;
                m_categoryCount[bit] = 0;
            }
            Volatile.Write(ref m_publishedSequence, m_nextSequence - 1);
        }
    }

    /// <summary>
    /// 一次筛选的句柄：构造时冻结窗口（序号上界），使 UI 分页与落盘消费都能看到稳定结果。
    /// 演示页标记：FLT01。
    /// </summary>
    public readonly struct JournalFilter
    {
        private readonly StateChangeJournal m_journal;
        private readonly JournalFilterSpec m_spec;
        private readonly long m_from;
        private readonly long m_to;

        internal JournalFilter(StateChangeJournal journal, in JournalFilterSpec spec)
        {
            m_journal = journal;
            m_spec = spec;
            long oldest = journal.OldestSequence;
            long from = spec.FromSequenceExclusive > oldest - 1 ? spec.FromSequenceExclusive : oldest - 1;
            long to = spec.ToSequenceInclusive < journal.PublishedSequence
                ? spec.ToSequenceInclusive : journal.PublishedSequence;
            m_from = from;
            m_to = to;
        }

        public long FromSequence => m_from;
        public long ToSequence => m_to;

        /// <summary>命中条数；<paramref name="maxScan"/> 用于限制最坏情况（UI 只要"还有多少"）。</summary>
        public int Count(int maxScan = int.MaxValue)
        {
            long start = JournalFilterClock.StartTimestamp();
            int hits = 0;
            long scanned = 0;
            for (long sequence = m_from + 1; sequence <= m_to; sequence++)
            {
                if (scanned++ >= maxScan) break;
                if (!m_journal.TryGet(sequence, out JournalRecord record)) continue;
                if (m_spec.Matches(in record)) hits++;
            }
            m_journal.NoteFilter(hits, JournalFilterClock.ElapsedMicros(start));
            return hits;
        }

        /// <summary>复制命中记录（应用 skip/take）。返回实际条数。</summary>
        public int CopyTo(Span<JournalRecord> destination)
        {
            long start = JournalFilterClock.StartTimestamp();
            int written = 0;
            int skipped = 0;
            for (long sequence = m_from + 1; sequence <= m_to && written < destination.Length; sequence++)
            {
                if (!m_journal.TryGet(sequence, out JournalRecord record)) continue;
                if (!m_spec.Matches(in record)) continue;
                if (skipped < m_spec.Skip) { skipped++; continue; }
                if (written >= m_spec.Take) break;
                destination[written++] = record;
            }
            m_journal.NoteFilter(written, JournalFilterClock.ElapsedMicros(start));
            return written;
        }

        /// <summary>零分配枚举（顺序、由旧到新）。</summary>
        public Enumerator GetEnumerator() => new Enumerator(m_journal, m_spec, m_from, m_to);

        public struct Enumerator
        {
            private readonly StateChangeJournal m_journal;
            private readonly JournalFilterSpec m_spec;
            private readonly long m_to;
            private long m_next;
            private JournalRecord m_current;

            internal Enumerator(StateChangeJournal journal, in JournalFilterSpec spec, long from, long to)
            {
                m_journal = journal;
                m_spec = spec;
                m_to = to;
                m_next = from + 1;
                m_current = default;
            }

            public JournalRecord Current => m_current;

            public bool MoveNext()
            {
                while (m_next <= m_to)
                {
                    long sequence = m_next++;
                    if (!m_journal.TryGet(sequence, out JournalRecord record)) continue;
                    if (!m_spec.Matches(in record)) continue;
                    m_current = record;
                    return true;
                }
                return false;
            }
        }
    }

    internal static class JournalFilterClock
    {
        public static long StartTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();

        public static long ElapsedMicros(long start) =>
            (long)((System.Diagnostics.Stopwatch.GetTimestamp() - start)
                * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
    }
}
