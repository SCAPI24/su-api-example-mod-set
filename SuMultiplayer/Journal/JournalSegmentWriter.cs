using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace SuMultiplayer.Journal
{
    /// <summary>
    /// 分段落盘器。落盘线程只做「顺序读序号区间 → 组块 → 写文件」，因此：
    /// <list type="bullet">
    /// <item>游戏线程只做一次环形写入 + 一次信号，不等 I/O；</item>
    /// <item>磁盘慢的时候丢最旧的未落盘记录并计数，绝不阻塞联机会话；</item>
    /// <item>块级 CRC 让「撕裂的尾块」在下次启动时被识别并截断。</item>
    /// </list>
    /// 演示页标记：SEG01 / FILE01 / REC1。
    /// </summary>
    public sealed class JournalSegmentWriter : IDisposable
    {
        public const uint SegmentMagic = 0x314A4D53u;   // "SMJ1"
        public const uint BlockMagic = 0x424C4A53u;     // "SJLB"
        public const int SegmentHeaderBytes = 32;
        public const int BlockHeaderBytes = 24;
        /// <summary>块头 reserved 字段（偏移 6）的 bit0：本块含"必须 fsync"的分类。</summary>
        public const int BlockFlagNeedsFsync = 1;
        public const ushort FormatVersion = 1;
        public const string SegmentExtension = ".smj";
        public const string ManifestFileName = "journal.index";

        private readonly JournalOptions m_options;
        private readonly StateChangeJournal m_journal;
        private readonly string m_directory;
        private readonly int m_recordsPerBlock;
        private readonly JournalCategoryPolicy m_categoryPolicy;
        private readonly int m_blockPayloadBytes;
        private readonly JournalRecord[] m_blockBuffer;
        private readonly byte[] m_blockBytes;
        private readonly JournalSegmentInfo[] m_segments = new JournalSegmentInfo[64];
        private readonly ManualResetEventSlim m_wake = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim m_drained = new ManualResetEventSlim(false);
        private readonly object m_gate = new object();

        private Thread m_thread;
        private volatile bool m_running;
        private volatile bool m_disposed;
        private FileStream m_stream;
        private string m_currentPath;
        private long m_currentBytes;
        private long m_segmentFirstSequence;
        private long m_nextWriteSequence = 1L;
        private long m_persistedRecords;
        private long m_persistedBytes;
        private long m_blocksWritten;
        private long m_flushCount;
        private long m_lastFlushMs;
        private long m_droppedRecords;
        private long m_lostRecords;
        private long m_filteredRecords;
        private long m_fsyncCalls;
        private bool m_drainNeedsFsync;
        private int m_segmentIndex = 1;

        public JournalSegmentWriter(JournalOptions options, StateChangeJournal journal)
        {
            m_options = options ?? throw new ArgumentNullException(nameof(options));
            m_journal = journal ?? throw new ArgumentNullException(nameof(journal));
            options.Validate();
            if (string.IsNullOrWhiteSpace(options.Directory))
                throw new ArgumentException("落盘目录不能为空", nameof(options));
            m_directory = options.Directory;
            m_recordsPerBlock = options.RecordsPerBlock;
            m_categoryPolicy = options.CategoryPolicy ?? JournalCategoryPolicy.Default();
            m_blockPayloadBytes = m_recordsPerBlock * JournalRecord.SizeInBytes;
            m_blockBuffer = new JournalRecord[m_recordsPerBlock];
            m_blockBytes = new byte[BlockHeaderBytes + m_blockPayloadBytes];
        }

        public string Directory => m_directory;

        public string CurrentSegmentPath => m_currentPath;

        public IReadOnlyList<JournalSegmentInfo> Segments
        {
            get
            {
                lock (m_gate)
                {
                    var list = new List<JournalSegmentInfo>();
                    for (int i = 0; i < m_segments.Length; i++)
                    {
                        if (m_segments[i].FileName != null) list.Add(m_segments[i]);
                    }
                    return list;
                }
            }
        }

        /// <summary>启动：恢复旧分段 → 打开/新建当前分段 → 起落盘线程。</summary>
        public JournalRecoveryResult Start()
        {
            if (m_running) throw new InvalidOperationException("已经启动");
            System.IO.Directory.CreateDirectory(m_directory);
            JournalRecoveryResult recovery = Recover(m_directory, m_options);
            LoadExistingSegments();
            lock (m_gate)
            {
                // 续写序号 = 磁盘上最后一条 + 1；若这些记录已被环形覆盖，就从最旧可用处接上
                long resume = recovery.LastSequence > 0 ? recovery.LastSequence + 1 : 1L;
                long oldest = m_journal.OldestSequence;
                if (resume < oldest) resume = oldest;
                m_nextWriteSequence = resume;
                m_segmentIndex = NextSegmentIndex();
                m_currentPath = Path.Combine(m_directory, SegmentName(m_segmentIndex));
                m_segmentFirstSequence = m_nextWriteSequence;
                m_stream = new FileStream(m_currentPath, FileMode.Create, FileAccess.Write,
                    FileShare.Read, m_options.WriteBufferBytes, FileOptions.SequentialScan);
                WriteSegmentHeader(m_stream, m_segmentFirstSequence);
                m_currentBytes = SegmentHeaderBytes;
            }
            WriteManifest();
            m_running = true;
            m_thread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "SuMP-JournalWriter",
                Priority = ThreadPriority.BelowNormal
            };
            m_thread.Start();
            return recovery;
        }

        /// <summary>轻量信号：让落盘线程尽快跑一轮（不在调用线程做 I/O）。</summary>
        public void RequestFlush()
        {
            if (!m_running) return;
            m_wake.Set();
        }

        /// <summary>等待落盘线程追上（关闭、切场景、导出前调用）。超时返回 false。</summary>
        public bool FlushAndWait(int timeoutMs = 3000)
        {
            if (!m_running) return true;
            m_drained.Reset();
            m_wake.Set();
            bool ok = m_drained.Wait(timeoutMs);
            if (ok) WriteManifest();
            return ok;
        }

        public JournalWriterStats GetStats()
        {
            lock (m_gate)
            {
                int count = 0;
                for (int i = 0; i < m_segments.Length; i++) if (m_segments[i].FileName != null) count++;
                return new JournalWriterStats(m_persistedRecords, m_persistedBytes, m_blocksWritten,
                    m_flushCount, m_lastFlushMs / 1000.0, m_droppedRecords, m_lostRecords, count,
                    m_currentPath == null ? null : Path.GetFileName(m_currentPath),
                    m_filteredRecords, m_fsyncCalls);
            }
        }

        private void WriterLoop()
        {
            while (m_running)
            {
                m_wake.Wait(m_options.FlushIntervalMs);
                m_wake.Reset();
                try
                {
                    long started = JournalFilterClock.StartTimestamp();
                    Drain();
                    m_lastFlushMs = JournalFilterClock.ElapsedMicros(started);
                    m_flushCount++;
                    m_drained.Set();
                }
                catch (Exception)
                {
                    if (m_options.ThrowOnWriteFailure) throw;
                    // 诊断组件不允许拖垮会话：计数后继续
                    Interlocked.Increment(ref m_droppedRecords);
                }
            }
        }

        private void Drain()
        {
            long published = m_journal.PublishedSequence;
            while (m_running && m_nextWriteSequence <= published)
            {
                long oldest = m_journal.OldestSequence;
                if (m_nextWriteSequence < oldest)
                {
                    // 环形已经覆盖：这些记录再也拿不到，记缺口后跳到最旧可用处
                    long lost = oldest - m_nextWriteSequence;
                    m_lostRecords += lost;
                    m_droppedRecords += lost;
                    m_journal.ReportOverwrittenUnflushed(lost);
                    m_nextWriteSequence = oldest;
                }

                Span<JournalRecord> span = m_blockBuffer.AsSpan();
                int copied = m_journal.CopyRange(m_nextWriteSequence, span);
                if (copied <= 0)
                {
                    m_nextWriteSequence = oldest;
                    break;
                }

                // 分类闸门：不落盘的分类在这里被过滤掉，采样按序号取模（确定性）。
                // 就地压缩，不额外分配；被过滤的记录计入 FilteredRecords。
                int kept = 0;
                bool blockNeedsFsync = false;
                for (int i = 0; i < copied; i++)
                {
                    JournalRecord record = span[i];
                    if (!m_categoryPolicy.ShouldPersist(record.Category, record.Sequence))
                    {
                        m_filteredRecords++;
                        continue;
                    }
                    if (!blockNeedsFsync && m_categoryPolicy.ShouldFsync(record.Category))
                        blockNeedsFsync = true;
                    span[kept++] = record;
                }
                m_nextWriteSequence += copied;
                if (kept == 0) continue;
                WriteBlock(span.Slice(0, kept), blockNeedsFsync);
            }
            if (m_stream != null)
            {
                // 一个刷盘周期只 fsync 一次（块级 fsync 会把吞吐打掉一个量级）；
                // 全局 FlushToDisk 或本周期出现账本类记录时才真正落盘。
                bool fsync = m_options.FlushToDisk || m_drainNeedsFsync;
                m_stream.Flush(fsync);
                if (fsync) m_fsyncCalls++;
            }
            m_drainNeedsFsync = false;
        }

        /// <summary>把一批记录编码成一个块（含头部与 CRC），返回写入字节数。导出与落盘共用。</summary>
        internal static int WriteBlockTo(byte[] buffer, ReadOnlySpan<JournalRecord> records, int flags = 0)
        {
            int payload = records.Length * JournalRecord.SizeInBytes;
            BinaryPrimitivesWriteUInt32(buffer, 0, BlockMagic);
            BinaryPrimitivesWriteUInt16(buffer, 4, (ushort)records.Length);
            BinaryPrimitivesWriteUInt16(buffer, 6, (ushort)flags);
            BinaryPrimitivesWriteInt64(buffer, 8, records[0].Sequence);
            BinaryPrimitivesWriteInt32(buffer, 16, payload);
            for (int i = 0; i < records.Length; i++)
                WriteRecord(buffer, BlockHeaderBytes + i * JournalRecord.SizeInBytes, in records[i]);
            BinaryPrimitivesWriteUInt32(buffer, 20, Crc32.Compute(buffer, BlockHeaderBytes, payload));
            return BlockHeaderBytes + payload;
        }

        internal static void WriteSegmentHeaderTo(byte[] buffer, long firstSequence)
        {
            BinaryPrimitivesWriteUInt32(buffer, 0, SegmentMagic);
            BinaryPrimitivesWriteUInt16(buffer, 4, FormatVersion);
            BinaryPrimitivesWriteUInt16(buffer, 6, (ushort)SegmentHeaderBytes);
            BinaryPrimitivesWriteInt64(buffer, 8, firstSequence);
            BinaryPrimitivesWriteInt64(buffer, 16, DateTime.UtcNow.Ticks);
        }

        private void WriteBlock(ReadOnlySpan<JournalRecord> records, bool needsFsync = false)
        {
            int bytes = WriteBlockTo(m_blockBytes, records, needsFsync ? BlockFlagNeedsFsync : 0);
            m_stream.Write(m_blockBytes, 0, bytes);
            m_currentBytes += bytes;
            m_persistedRecords += records.Length;
            m_persistedBytes += bytes;
            m_blocksWritten++;
            if (needsFsync) m_drainNeedsFsync = true;

            if (m_currentBytes >= m_options.SegmentBytes) Rotate();
        }

        private void Rotate()
        {
            long lastSequence = m_nextWriteSequence - 1;
            m_stream.Flush();
            lock (m_gate)
            {
                AddSegmentInfo(Path.GetFileName(m_currentPath), m_segmentFirstSequence, lastSequence,
                    m_persistedRecords, m_currentBytes);
                m_stream.Dispose();
                m_segmentIndex++;
                m_currentPath = Path.Combine(m_directory, SegmentName(m_segmentIndex));
                m_segmentFirstSequence = m_nextWriteSequence;
                m_stream = new FileStream(m_currentPath, FileMode.Create, FileAccess.Write,
                    FileShare.Read, m_options.WriteBufferBytes, FileOptions.SequentialScan);
                WriteSegmentHeader(m_stream, m_segmentFirstSequence);
                m_currentBytes = SegmentHeaderBytes;
            }
            PruneSegments();
            WriteManifest();
        }

        private void AddSegmentInfo(string name, long first, long last, long records, long bytes)
        {
            for (int i = 0; i < m_segments.Length; i++)
            {
                if (m_segments[i].FileName != null) continue;
                m_segments[i] = new JournalSegmentInfo(name, first, last, records, bytes);
                return;
            }
            // 数组满：整体左移（保留最近 64 段）
            Array.Copy(m_segments, 1, m_segments, 0, m_segments.Length - 1);
            m_segments[m_segments.Length - 1] = new JournalSegmentInfo(name, first, last, records, bytes);
        }

        private void LoadExistingSegments()
        {
            try
            {
                string[] files = System.IO.Directory.GetFiles(m_directory, "*" + SegmentExtension);
                Array.Sort(files, StringComparer.Ordinal);
                for (int i = 0; i < files.Length; i++)
                {
                    DescribeSegment(files[i], out long first, out long last, out long records, out _, out long bytes);
                    if (bytes <= 0) continue;
                    AddSegmentInfo(Path.GetFileName(files[i]), first, last, records, bytes);
                }
            }
            catch (IOException)
            {
            }
        }

        private int NextSegmentIndex()
        {
            int max = 0;
            try
            {
                foreach (string file in System.IO.Directory.GetFiles(m_directory, "*" + SegmentExtension))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    int dash = name.LastIndexOf('-');
                    if (dash < 0) continue;
                    if (int.TryParse(name.Substring(dash + 1), out int value) && value > max) max = value;
                }
            }
            catch (IOException)
            {
            }
            return max + 1;
        }

        /// <summary>读一个分段的头与全部块头，给出首/末序号、记录数与字节数（不校验 CRC，用于统计）。</summary>
        internal static void DescribeSegment(string path, out long firstSequence, out long lastSequence,
            out long recordCount, out long validBytes, out long byteCount)
        {
            firstSequence = 0;
            lastSequence = 0;
            recordCount = 0;
            validBytes = SegmentHeaderBytes;
            byteCount = 0;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byteCount = fs.Length;
                    byte[] header = new byte[SegmentHeaderBytes];
                    if (ReadFull(fs, header, header.Length) < header.Length) return;
                    if (BinaryPrimitivesReadUInt32(header, 0) != SegmentMagic) return;
                    firstSequence = BinaryPrimitivesReadInt64(header, 8);
                    byte[] blockHeader = new byte[BlockHeaderBytes];
                    byte[] payload = new byte[64 * 1024];
                    while (true)
                    {
                        if (ReadFull(fs, blockHeader, blockHeader.Length) < blockHeader.Length) break;
                        if (BinaryPrimitivesReadUInt32(blockHeader, 0) != BlockMagic) break;
                        int count = BinaryPrimitivesReadUInt16(blockHeader, 4);
                        int payloadBytes = BinaryPrimitivesReadInt32(blockHeader, 16);
                        if (count <= 0 || payloadBytes != count * JournalRecord.SizeInBytes) break;
                        if (payload.Length < payloadBytes) payload = new byte[payloadBytes];
                        if (ReadFull(fs, payload, payloadBytes) < payloadBytes) break;
                        recordCount += count;
                        lastSequence = BinaryPrimitivesReadInt64(payload, (count - 1) * JournalRecord.SizeInBytes);
                        validBytes = fs.Position;
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        private void PruneSegments()
        {
            if (m_options.MaxSegments <= 0) return;
            // 当前正在写的分段不在 m_segments 里，所以磁盘上最多留 (MaxSegments - 1) 个已关闭分段
            int keepClosed = m_options.MaxSegments - 1;
            if (keepClosed < 1) keepClosed = 1;
            lock (m_gate)
            {
                int live = 0;
                for (int i = 0; i < m_segments.Length; i++) if (m_segments[i].FileName != null) live++;
                if (live <= keepClosed) return;
                int toDelete = live - keepClosed;
                for (int i = 0; i < m_segments.Length && toDelete > 0; i++)
                {
                    if (m_segments[i].FileName == null) continue;
                    string path = Path.Combine(m_directory, m_segments[i].FileName);
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (IOException) { /* 文件被占用：下一轮再删 */ }
                    m_segments[i] = default;
                    toDelete--;
                }
            }
        }

        private void WriteManifest()
        {
            lock (m_gate)
            {
                if (string.IsNullOrEmpty(m_directory)) return;
                var sb = new StringBuilder();
                sb.Append("#SMJI1\t").Append(FormatVersion).Append('\n');
                sb.Append("#segment\tfirst\tlast\trecords\tbytes\n");
                for (int i = 0; i < m_segments.Length; i++)
                {
                    JournalSegmentInfo info = m_segments[i];
                    if (info.FileName == null) continue;
                    sb.Append(info.FileName).Append('\t').Append(info.FirstSequence).Append('\t')
                        .Append(info.LastSequence).Append('\t').Append(info.RecordCount).Append('\t')
                        .Append(info.ByteCount).Append('\n');
                }
                string path = Path.Combine(m_directory, ManifestFileName);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        public void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            FlushAndWait(2000);
            m_running = false;
            m_wake.Set();
            Thread thread = m_thread;
            if (thread != null && thread.IsAlive) thread.Join(2000);
            lock (m_gate)
            {
                if (m_stream != null)
                {
                    try
                    {
                        m_stream.Flush(m_options.FlushToDisk);
                        AddSegmentInfo(Path.GetFileName(m_currentPath), m_segmentFirstSequence,
                            m_nextWriteSequence - 1, m_persistedRecords, m_currentBytes);
                    }
                    catch (IOException) { }
                    m_stream.Dispose();
                    m_stream = null;
                }
            }
            WriteManifest();
            m_wake.Dispose();
            m_drained.Dispose();
        }

        // ------------------------------------------------------------------
        // 文件格式
        // ------------------------------------------------------------------

        internal static string SegmentName(int index) => "journal-" + index.ToString("D6") + SegmentExtension;

        private static void WriteSegmentHeader(Stream stream, long firstSequence)
        {
            byte[] header = new byte[SegmentHeaderBytes];
            WriteSegmentHeaderTo(header, firstSequence);
            stream.Write(header, 0, header.Length);
        }

        private static void WriteRecord(byte[] buffer, int offset, in JournalRecord record)
        {
            BinaryPrimitivesWriteInt64(buffer, offset, record.Sequence);
            BinaryPrimitivesWriteInt64(buffer, offset + 8, record.TimestampMs);
            BinaryPrimitivesWriteUInt32(buffer, offset + 16, record.Category);
            BinaryPrimitivesWriteUInt16(buffer, offset + 20, record.Code);
            buffer[offset + 22] = (byte)record.Role;
            buffer[offset + 23] = (byte)record.Severity;
            BinaryPrimitivesWriteInt32(buffer, offset + 24, record.ActorId);
            BinaryPrimitivesWriteInt32(buffer, offset + 28, record.ValueA);
            BinaryPrimitivesWriteInt32(buffer, offset + 32, record.ValueB);
            BinaryPrimitivesWriteInt32(buffer, offset + 36, record.ValueC);
            BinaryPrimitivesWriteInt32(buffer, offset + 40, record.PayloadRef);
            BinaryPrimitivesWriteUInt32(buffer, offset + 44, 0u);
        }

        internal static JournalRecord ReadRecord(byte[] buffer, int offset) =>
            new JournalRecord(
                BinaryPrimitivesReadInt64(buffer, offset),
                BinaryPrimitivesReadInt64(buffer, offset + 8),
                BinaryPrimitivesReadUInt32(buffer, offset + 16),
                BinaryPrimitivesReadUInt16(buffer, offset + 20),
                (JournalRole)buffer[offset + 22],
                (JournalSeverity)buffer[offset + 23],
                BinaryPrimitivesReadInt32(buffer, offset + 24),
                BinaryPrimitivesReadInt32(buffer, offset + 28),
                BinaryPrimitivesReadInt32(buffer, offset + 32),
                BinaryPrimitivesReadInt32(buffer, offset + 36),
                BinaryPrimitivesReadInt32(buffer, offset + 40));

        /// <summary>
        /// 启动恢复：校验分段、截断撕裂尾块、按保留策略清理旧分段，并给出续写的序号。
        /// 只完整扫描最后一个分段（前面分段的头 + manifest 足够），因此启动代价与分段大小同阶。
        /// </summary>
        public static JournalRecoveryResult Recover(string directory, JournalOptions options)
        {
            if (!System.IO.Directory.Exists(directory))
                return new JournalRecoveryResult(0, 0, 0, 0, 0, 0);

            string[] files = System.IO.Directory.GetFiles(directory, "*" + SegmentExtension);
            Array.Sort(files, StringComparer.Ordinal);
            int scanned = 0, deleted = 0;
            long validRecords = 0, lastSequence = 0, truncated = 0, tornBlocks = 0;

            // 只对最新分段做完整 CRC 校验并截断撕裂尾块：崩溃只可能损坏正在写的那个
            if (files.Length > 0)
            {
                scanned = 1;
                truncated = TruncateTornTail(files[files.Length - 1], out tornBlocks);
            }

            for (int i = 0; i < files.Length; i++)
            {
                DescribeSegment(files[i], out _, out long last, out long records, out _, out _);
                validRecords += records;
                if (last > lastSequence) lastSequence = last;
            }

            if (options != null && options.MaxSegments > 0 && files.Length > options.MaxSegments)
            {
                int remove = files.Length - options.MaxSegments;
                for (int i = 0; i < remove; i++)
                {
                    try { File.Delete(files[i]); deleted++; } catch (IOException) { }
                }
            }

            return new JournalRecoveryResult(scanned, deleted, validRecords, lastSequence, truncated, tornBlocks);
        }

        /// <summary>从头逐块校验 CRC，遇到第一个不完整/校验失败的块就截断文件。返回被截断的字节数。</summary>
        private static long TruncateTornTail(string path, out long tornBlocks)
        {
            tornBlocks = 0;
            long truncatedBytes = 0;
            long validEnd = SegmentHeaderBytes;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] header = new byte[SegmentHeaderBytes];
                    if (ReadFull(fs, header, header.Length) < header.Length) return 0;
                    if (BinaryPrimitivesReadUInt32(header, 0) != SegmentMagic) return 0;
                    byte[] blockHeader = new byte[BlockHeaderBytes];
                    byte[] payload = new byte[64 * 1024];
                    while (true)
                    {
                        if (ReadFull(fs, blockHeader, blockHeader.Length) < blockHeader.Length) break;
                        if (BinaryPrimitivesReadUInt32(blockHeader, 0) != BlockMagic) break;
                        int records = BinaryPrimitivesReadUInt16(blockHeader, 4);
                        int payloadBytes = BinaryPrimitivesReadInt32(blockHeader, 16);
                        uint crc = BinaryPrimitivesReadUInt32(blockHeader, 20);
                        if (records <= 0 || payloadBytes != records * JournalRecord.SizeInBytes) break;
                        if (payload.Length < payloadBytes) payload = new byte[payloadBytes];
                        if (ReadFull(fs, payload, payloadBytes) < payloadBytes) break;
                        if (Crc32.Compute(payload, 0, payloadBytes) != crc) break;
                        validEnd = fs.Position;
                    }
                    truncatedBytes = fs.Length - validEnd;
                    if (truncatedBytes > 0) tornBlocks = 1;
                }
                if (truncatedBytes > 0)
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        fs.SetLength(validEnd);
                        fs.Flush(true);
                    }
                }
            }
            catch (IOException)
            {
                return 0;
            }
            return truncatedBytes;
        }

        private static int ReadFull(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        // ------------------------------------------------------------------
        // 小端读写（不依赖 System.Buffers.Binary，Android/AOT 下同样可用）
        // ------------------------------------------------------------------
        private static void BinaryPrimitivesWriteUInt16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
        }
        private static void BinaryPrimitivesWriteUInt32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
        private static void BinaryPrimitivesWriteInt32(byte[] b, int o, int v) =>
            BinaryPrimitivesWriteUInt32(b, o, unchecked((uint)v));
        private static void BinaryPrimitivesWriteInt64(byte[] b, int o, long v)
        {
            BinaryPrimitivesWriteUInt32(b, o, unchecked((uint)v));
            BinaryPrimitivesWriteUInt32(b, o + 4, unchecked((uint)(v >> 32)));
        }
        private static ushort BinaryPrimitivesReadUInt16(byte[] b, int o) =>
            (ushort)(b[o] | (b[o + 1] << 8));
        private static uint BinaryPrimitivesReadUInt32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        private static int BinaryPrimitivesReadInt32(byte[] b, int o) =>
            unchecked((int)BinaryPrimitivesReadUInt32(b, o));
        private static long BinaryPrimitivesReadInt64(byte[] b, int o) =>
            unchecked((long)(BinaryPrimitivesReadUInt32(b, o) | ((ulong)BinaryPrimitivesReadUInt32(b, o + 4) << 32)));
    }

    /// <summary>块级 CRC32（IEEE），用于识别撕裂写入。</summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(byte[] buffer, int offset, int count)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < count; i++) crc = Table[(crc ^ buffer[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
