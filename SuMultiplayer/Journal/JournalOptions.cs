using System;

namespace SuMultiplayer.Journal
{
    /// <summary>
    /// 状态变化队列的配置。主机端与客户端使用同一份配置类型，只换目录与角色。
    /// 演示页标记：OPT01。
    /// </summary>
    public sealed class JournalOptions
    {
        /// <summary>内存队列容量（条）。必须是 2 的幂。默认 65536 条 = 3 MB。</summary>
        public int Capacity { get; set; } = 1 << 16;

        /// <summary>字符串池容量（条）。必须是 2 的幂；只影响带文本的事件。</summary>
        public int PayloadPoolCapacity { get; set; } = 1 << 12;

        /// <summary>每个分类的稀疏索引容量（条），用于"最近 N 条某分类"快速查询。</summary>
        public int CategoryIndexCapacity { get; set; } = 1 << 10;

        /// <summary>落盘目录；为空表示只保留在内存（纯诊断模式）。</summary>
        public string Directory { get; set; }

        /// <summary>单个分段文件的目标大小（字节），超过就轮转。</summary>
        public long SegmentBytes { get; set; } = 4L * 1024 * 1024;

        /// <summary>保留的分段数上限（按时间从旧到新删除）；0 = 不删除。</summary>
        public int MaxSegments { get; set; } = 8;

        /// <summary>每个落盘块的记录数（块 = 独立 CRC 单元）。必须是 2 的幂。</summary>
        public int RecordsPerBlock { get; set; } = 256;

        /// <summary>最长刷盘间隔（毫秒）：即使没写满也把缓冲刷下去。</summary>
        public int FlushIntervalMs { get; set; } = 250;

        /// <summary>落盘缓冲大小（字节）。</summary>
        public int WriteBufferBytes { get; set; } = 256 * 1024;

        /// <summary>刷盘后是否 fsync（强持久化）。默认关闭：靠操作系统的页缓存，吞吐高一个量级。</summary>
        public bool FlushToDisk { get; set; }

        /// <summary>
        /// 落盘端落后超过这个记录数时，允许丢弃最旧的未落盘记录（只计数，不阻塞游戏线程）。
        /// 0 = 永不主动丢弃（此时环形覆盖仍会丢，但会有 <see cref="JournalStats.OverwrittenUnflushed"/> 计数）。
        /// </summary>
        public long MaxPendingRecords { get; set; } = 1 << 18;

        /// <summary>写入失败时抛出还是静默计数。默认静默：诊断组件不允许拖垮联机会话。</summary>
        public bool ThrowOnWriteFailure { get; set; }

        /// <summary>允许调用方注入时钟（测试用）；默认使用 <see cref="Environment.TickCount64"/>。</summary>
        public Func<long> ClockMs { get; set; } = () => Environment.TickCount64;

        /// <summary>
        /// 分类闸门：哪些分类落盘、抽样多少、哪些必须 fsync。
        /// 默认全量落盘且不强制 fsync（与旧行为一致）。见 <see cref="JournalWritePolicy"/>。
        /// </summary>
        public JournalCategoryPolicy CategoryPolicy { get; set; } = JournalCategoryPolicy.Default();

        /// <summary>按预设套用存盘频率策略（覆盖块/段/间隔/fsync/容量/保留/分类闸门）。</summary>
        public JournalOptions UseProfile(JournalWriteProfile profile)
        {
            JournalWritePolicy.For(profile).ApplyTo(this);
            return this;
        }

        internal void Validate()
        {
            if (Capacity <= 0 || (Capacity & (Capacity - 1)) != 0)
                throw new ArgumentException("Capacity 必须是 2 的幂", nameof(Capacity));
            if (PayloadPoolCapacity <= 0 || (PayloadPoolCapacity & (PayloadPoolCapacity - 1)) != 0)
                throw new ArgumentException("PayloadPoolCapacity 必须是 2 的幂", nameof(PayloadPoolCapacity));
            if (RecordsPerBlock <= 0 || (RecordsPerBlock & (RecordsPerBlock - 1)) != 0)
                throw new ArgumentException("RecordsPerBlock 必须是 2 的幂", nameof(RecordsPerBlock));
            if (SegmentBytes < RecordsPerBlock * JournalRecord.SizeInBytes + 64)
                throw new ArgumentException("SegmentBytes 至少要能装下一个块", nameof(SegmentBytes));
        }
    }
}
