using System;

namespace SuMultiplayer.Journal
{
    /// <summary>
    /// 状态变化分类。位掩码设计：一次筛选可以同时命中多个类别（例如「地形 + 恢复」）。
    /// 演示页标记：CAT01。
    /// </summary>
    [Flags]
    public enum JournalCategory : uint
    {
        None = 0u,
        Session = 1u << 0,            // 加入 / 离开 / 断线 / 宽限重连
        Approval = 1u << 1,           // 加入审批 / 踢人
        Transfer = 1u << 2,           // 世界传输 / 分片 / 追帧 / 屏障
        Terrain = 1u << 3,            // 方块修改 / 区块检查点 / 恢复
        PlayerState = 1u << 4,        // 位置 / 姿态 / 生命 / 属性
        PlayerAction = 1u << 5,       // 挖掘 / 放置 / 攻击 / 交互请求与回执
        Inventory = 1u << 6,          // 容器事务 / 物品
        Entity = 1u << 7,             // 动物 / 掉落物 / 投射物 / 爆炸
        Circuit = 1u << 8,            // 电路事件与快照
        WorldControl = 1u << 9,       // 时间 / 天气 / 世界设置
        DataModification = 1u << 10,  // DM 请求 / 审批 / 应用
        Region = 1u << 11,            // 领地建立 / 赋予 / 执法拒绝
        Capability = 1u << 12,        // 临时能力授予与撤销
        Chat = 1u << 13,              // 聊天
        Metrics = 1u << 14,           // 流量 / 重传 / 队列采样
        Diagnostics = 1u << 15,       // 异常 / 丢弃 / 协议拒绝
        All = 0xFFFFu
    }

    /// <summary>记录角色：主机端队列与客户端队列使用同一套格式，用这个字段区分来源。</summary>
    public enum JournalRole : byte
    {
        Host = 0,
        Client = 1
    }

    /// <summary>严重度：只用于筛选与着色，不改变队列行为。</summary>
    public enum JournalSeverity : byte
    {
        Trace = 0,
        Info = 1,
        Notice = 2,
        Warning = 3,
        Error = 4
    }

    /// <summary>
    /// 事件码（每个分类下的小类）。追加式分配：已发布的码不得改含义。
    /// </summary>
    public enum JournalEventCode : ushort
    {
        Unknown = 0,

        // Session
        SessionJoinRequested = 1,
        SessionJoined = 2,
        SessionLeft = 3,
        SessionDisconnected = 4,
        SessionRejoinGraceStarted = 5,
        SessionRejoinGraceExpired = 6,

        // Approval
        ApprovalRequested = 10,
        ApprovalAllowed = 11,
        ApprovalRejected = 12,
        ApprovalDeferred = 13,
        PlayerKicked = 14,

        // Transfer
        TransferStarted = 20,
        TransferChunkServed = 21,
        TransferChunkMissing = 22,
        TransferCompleted = 23,
        CatchUpBarrierPassed = 24,

        // Terrain
        TerrainCellsBroadcast = 25,
        TerrainCellsApplied = 26,
        TerrainCellsDropped = 27,
        TerrainChunkCheckpointRequested = 28,
        TerrainChunkCheckpointServed = 29,
        TerrainChunkCheckpointIncomplete = 30,
        TerrainGapRecovered = 31,

        // PlayerState / Action
        PlayerStateSent = 40,
        PlayerStateCorrected = 41,
        PlayerHealthChanged = 42,
        ActionRequested = 43,
        ActionAccepted = 44,
        ActionRejected = 45,

        // Inventory / Entity / Circuit
        ContainerCommitted = 50,
        ContainerRejected = 51,
        EntitySpawned = 52,
        EntityRemoved = 53,
        PickablePicked = 54,
        ProjectileHit = 55,
        ExplosionResolved = 56,
        CircuitSynced = 57,

        // World / DM / Region / Capability / Chat / Metrics / Diagnostics
        WorldControlApplied = 60,
        WorldSettingsChanged = 61,
        DataModificationRequested = 70,
        DataModificationApproved = 71,
        DataModificationRejected = 72,
        DataModificationApplied = 73,
        RegionEnforcementDenied = 80,
        RegionAssigned = 81,
        CapabilityGranted = 85,
        CapabilityRevoked = 86,
        ChatMessageRouted = 90,
        MetricsSample = 95,
        ProtocolRefused = 100,
        PacketMalformed = 101,
        ModuleException = 102,
        QueueOverflow = 103
    }

    /// <summary>
    /// 定长记录（48 字节，顺序布局）。定长 + 顺序布局是"线性写入 + 顺序回读 + 快速筛选"的前提：
    /// 队列、筛选、落盘都按字节块处理，没有逐条分配与序列化开销。
    /// 演示页标记：REC01。
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 8)]
    public readonly struct JournalRecord
    {
        /// <summary>单调递增序号，进程内唯一；落盘与筛选都按它定位。</summary>
        public readonly long Sequence;

        /// <summary>单调时钟毫秒（由注入的时钟提供，避免系统时间回拨造成乱序）。</summary>
        public readonly long TimestampMs;

        /// <summary>分类位掩码（<see cref="JournalCategory"/>）。</summary>
        public readonly uint Category;

        /// <summary>事件码（<see cref="JournalEventCode"/>）。</summary>
        public readonly ushort Code;

        public readonly JournalRole Role;

        public readonly JournalSeverity Severity;

        /// <summary>主体标识：clientId / entityId / -1 表示无。</summary>
        public readonly int ActorId;

        /// <summary>三个上下文整数，含义由事件码决定（坐标、格子数、批号、版本号…）。</summary>
        public readonly int ValueA;
        public readonly int ValueB;
        public readonly int ValueC;

        /// <summary>字符串池引用（聊天文本、错误摘要…）；-1 表示无。</summary>
        public readonly int PayloadRef;

        public const int SizeInBytes = 48;

        public JournalRecord(long sequence, long timestampMs, uint category, ushort code,
            JournalRole role, JournalSeverity severity, int actorId,
            int valueA, int valueB, int valueC, int payloadRef)
        {
            Sequence = sequence;
            TimestampMs = timestampMs;
            Category = category;
            Code = code;
            Role = role;
            Severity = severity;
            ActorId = actorId;
            ValueA = valueA;
            ValueB = valueB;
            ValueC = valueC;
            PayloadRef = payloadRef;
        }

        public JournalRecord WithSequence(long sequence, long timestampMs) =>
            new JournalRecord(sequence, timestampMs, Category, Code, Role, Severity,
                ActorId, ValueA, ValueB, ValueC, PayloadRef);

        public bool HasCategory(JournalCategory category) =>
            (Category & (uint)category) != 0u;

        public bool HasSeverity(JournalSeverity minimum) => Severity >= minimum;
    }

    /// <summary>
    /// 筛选条件。全部为「与」关系：类别、序号区间、时间区间、主体、最低严重度。
    /// 演示页标记：FLT01。
    /// </summary>
    public readonly struct JournalFilterSpec
    {
        public JournalFilterSpec(JournalCategory categories = JournalCategory.All,
            long fromSequenceExclusive = 0, long toSequenceInclusive = long.MaxValue,
            long fromTimestampMs = long.MinValue, long toTimestampMs = long.MaxValue,
            int actorId = int.MinValue, JournalSeverity minimumSeverity = JournalSeverity.Trace,
            int skip = 0, int take = int.MaxValue)
        {
            Categories = categories;
            FromSequenceExclusive = fromSequenceExclusive;
            ToSequenceInclusive = toSequenceInclusive;
            FromTimestampMs = fromTimestampMs;
            ToTimestampMs = toTimestampMs;
            ActorId = actorId;
            MinimumSeverity = minimumSeverity;
            Skip = skip;
            Take = take;
        }

        public JournalCategory Categories { get; }
        public long FromSequenceExclusive { get; }
        public long ToSequenceInclusive { get; }
        public long FromTimestampMs { get; }
        public long ToTimestampMs { get; }
        public int ActorId { get; }
        public JournalSeverity MinimumSeverity { get; }
        public int Skip { get; }
        public int Take { get; }

        public static JournalFilterSpec All => new JournalFilterSpec();

        public bool Matches(in JournalRecord record)
        {
            if ((record.Category & (uint)Categories) == 0u) return false;
            if (record.Sequence <= FromSequenceExclusive) return false;
            if (record.Sequence > ToSequenceInclusive) return false;
            if (record.TimestampMs < FromTimestampMs) return false;
            if (record.TimestampMs > ToTimestampMs) return false;
            if (ActorId != int.MinValue && record.ActorId != ActorId) return false;
            if (record.Severity < MinimumSeverity) return false;
            return true;
        }
    }

    /// <summary>队列运行统计（供 UI / 诊断面板读取，无锁快照）。</summary>
    public readonly struct JournalStats
    {
        public JournalStats(long capacity, long inMemoryCount, long totalAppended,
            long oldestSequence, long nextSequence, long overwrittenUnflushed,
            long filterScans, long lastFilterCount, double lastFilterMicroseconds)
        {
            Capacity = capacity;
            InMemoryCount = inMemoryCount;
            TotalAppended = totalAppended;
            OldestSequence = oldestSequence;
            NextSequence = nextSequence;
            OverwrittenUnflushed = overwrittenUnflushed;
            FilterScans = filterScans;
            LastFilterCount = lastFilterCount;
            LastFilterMicroseconds = lastFilterMicroseconds;
        }

        public long Capacity { get; }
        public long InMemoryCount { get; }
        public long TotalAppended { get; }
        public long OldestSequence { get; }
        public long NextSequence { get; }
        /// <summary>尚未落盘就被环形覆盖的记录数（说明落盘速度跟不上写入速度）。</summary>
        public long OverwrittenUnflushed { get; }
        public long FilterScans { get; }
        public long LastFilterCount { get; }
        public double LastFilterMicroseconds { get; }
    }

    /// <summary>落盘统计。</summary>
    public readonly struct JournalWriterStats
    {
        public JournalWriterStats(long persistedRecords, long persistedBytes, long blocksWritten,
            long flushCount, double lastFlushMilliseconds, long droppedRecords, long lostRecords,
            int segmentCount, string currentSegment, long filteredRecords = 0, long fsyncCalls = 0)
        {
            PersistedRecords = persistedRecords;
            PersistedBytes = persistedBytes;
            BlocksWritten = blocksWritten;
            FlushCount = flushCount;
            LastFlushMilliseconds = lastFlushMilliseconds;
            DroppedRecords = droppedRecords;
            LostRecords = lostRecords;
            SegmentCount = segmentCount;
            CurrentSegment = currentSegment;
            FilteredRecords = filteredRecords;
            FsyncCalls = fsyncCalls;
        }

        public long PersistedRecords { get; }
        public long PersistedBytes { get; }
        public long BlocksWritten { get; }
        public long FlushCount { get; }
        public double LastFlushMilliseconds { get; }
        /// <summary>队列已环形覆盖、落盘端再也拿不到的记录数。</summary>
        public long DroppedRecords { get; }
        /// <summary>落盘端主动跳过的缺口（覆盖造成）记录数。</summary>
        public long LostRecords { get; }
        public int SegmentCount { get; }
        public string CurrentSegment { get; }
        /// <summary>被分类闸门过滤掉、没有写进磁盘的记录数（主动丢弃，不是丢失）。</summary>
        public long FilteredRecords { get; }
        /// <summary>实际执行的 fsync 次数（一个刷盘周期最多一次）。</summary>
        public long FsyncCalls { get; }
    }

    public readonly struct JournalSegmentInfo
    {
        public JournalSegmentInfo(string fileName, long firstSequence, long lastSequence,
            long recordCount, long byteCount)
        {
            FileName = fileName;
            FirstSequence = firstSequence;
            LastSequence = lastSequence;
            RecordCount = recordCount;
            ByteCount = byteCount;
        }

        public string FileName { get; }
        public long FirstSequence { get; }
        public long LastSequence { get; }
        public long RecordCount { get; }
        public long ByteCount { get; }
    }

    /// <summary>启动恢复结果。</summary>
    public readonly struct JournalRecoveryResult
    {
        public JournalRecoveryResult(int segmentsScanned, int segmentsDeleted, long validRecords,
            long lastSequence, long truncatedBytes, long tornBlocksDropped)
        {
            SegmentsScanned = segmentsScanned;
            SegmentsDeleted = segmentsDeleted;
            ValidRecords = validRecords;
            LastSequence = lastSequence;
            TruncatedBytes = truncatedBytes;
            TornBlocksDropped = tornBlocksDropped;
        }

        public int SegmentsScanned { get; }
        public int SegmentsDeleted { get; }
        public long ValidRecords { get; }
        public long LastSequence { get; }
        /// <summary>被截断的字节数（撕裂的尾部块）。</summary>
        public long TruncatedBytes { get; }
        public long TornBlocksDropped { get; }
    }
}
