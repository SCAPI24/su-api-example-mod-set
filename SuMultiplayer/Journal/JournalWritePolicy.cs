using System;
using System.Collections.Generic;

namespace SuMultiplayer.Journal
{
    /// <summary>
    /// 存盘频率预设。目标：按场景在"丢多少"和"碰多少次盘"之间选一个点。
    /// 演示页标记：POL01。
    /// </summary>
    public enum JournalWriteProfile : byte
    {
        /// <summary>最勤：崩溃最多丢 250 ms；适合调试与联调。</summary>
        RealTime = 0,

        /// <summary>默认折中：1 s 刷一次、8 MiB 段。</summary>
        Balanced = 1,

        /// <summary>低频：2 s 刷一次、1024 条/块、32 MiB 段、高频分类采样 —— 磁盘占用与写次数都降一个量级。</summary>
        LowFrequency = 2,

        /// <summary>不落盘：只保留内存窗口，需要时导出（游戏中零磁盘 I/O）。</summary>
        MemoryOnly = 3
    }

    /// <summary>单个分类的落盘策略。</summary>
    public readonly struct JournalCategorySettings
    {
        public JournalCategorySettings(bool persist, int sampleEvery, bool forceFsync)
        {
            Persist = persist;
            SampleEvery = sampleEvery < 1 ? 1 : sampleEvery;
            ForceFsync = forceFsync;
        }

        /// <summary>是否写进磁盘。</summary>
        public bool Persist { get; }
        /// <summary>采样：1 = 全量，N = 每 N 条取 1 条（按记录序号取模，确定性、可复现）。</summary>
        public int SampleEvery { get; }
        /// <summary>该分类出现时，本刷盘周期强制 fsync（用于账本类不可丢的记录）。</summary>
        public bool ForceFsync { get; }
    }

    /// <summary>
    /// 分类闸门：决定"哪些分类落盘 / 抽样多少 / 哪些必须 fsync"。
    /// 与原子契约的 <c>Journal</c> 字段配合：高频 State 类可以只采样或不落盘，账本类必须全量 + fsync。
    /// 演示页标记：POL02。
    /// </summary>
    public sealed class JournalCategoryPolicy
    {
        private const int CategoryBits = 16;
        private readonly JournalCategorySettings[] m_bits = new JournalCategorySettings[CategoryBits];

        public JournalCategoryPolicy(bool persist = true, int sampleEvery = 1, bool forceFsync = false)
        {
            for (int i = 0; i < CategoryBits; i++)
                m_bits[i] = new JournalCategorySettings(persist, sampleEvery, forceFsync);
        }

        public JournalCategoryPolicy Set(JournalCategory category, bool persist, int sampleEvery = 1,
            bool forceFsync = false)
        {
            uint mask = (uint)category;
            for (int bit = 0; bit < CategoryBits; bit++)
            {
                if ((mask & (1u << bit)) != 0)
                    m_bits[bit] = new JournalCategorySettings(persist, sampleEvery, forceFsync);
            }
            return this;
        }

        public JournalCategorySettings Get(JournalCategory category)
        {
            uint mask = (uint)category;
            for (int bit = 0; bit < CategoryBits; bit++)
            {
                if ((mask & (1u << bit)) != 0) return m_bits[bit];
            }
            return new JournalCategorySettings(true, 1, false);
        }

        /// <summary>任一位要求全量落盘就落盘；采样位按序号取模判定。</summary>
        public bool ShouldPersist(uint categoryMask, long sequence)
        {
            if (categoryMask == 0) return true;   // 未分类：按默认落盘
            for (int bit = 0; bit < CategoryBits; bit++)
            {
                if ((categoryMask & (1u << bit)) == 0) continue;
                JournalCategorySettings settings = m_bits[bit];
                if (!settings.Persist) continue;
                if (settings.SampleEvery <= 1) return true;
                if (sequence % settings.SampleEvery == 0) return true;
            }
            return false;
        }

        /// <summary>任一位要求强制 fsync 就必须 fsync。</summary>
        public bool ShouldFsync(uint categoryMask)
        {
            if (categoryMask == 0) return false;
            for (int bit = 0; bit < CategoryBits; bit++)
            {
                if ((categoryMask & (1u << bit)) != 0 && m_bits[bit].ForceFsync) return true;
            }
            return false;
        }

        public int PersistedCategoryCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < CategoryBits; i++) if (m_bits[i].Persist) n++;
                return n;
            }
        }

        /// <summary>全量落盘、不强制 fsync（默认）。</summary>
        public static JournalCategoryPolicy Default() => new JournalCategoryPolicy(true, 1, false);

        /// <summary>低频：高频状态类采样，账本类强制 fsync。</summary>
        public static JournalCategoryPolicy LowFrequency() =>
            new JournalCategoryPolicy(true, 1, false)
                .Set(JournalCategory.Metrics, true, 16)
                .Set(JournalCategory.PlayerState, true, 4)
                .Set(JournalCategory.Terrain, true, 4)
                .Set(JournalCategory.Entity, true, 4)
                .Set(JournalCategory.Circuit, true, 2)
                .Set(JournalCategory.Inventory, true, 1, forceFsync: true)
                .Set(JournalCategory.DataModification, true, 1, forceFsync: true)
                .Set(JournalCategory.Diagnostics, true, 1, forceFsync: true);

        /// <summary>只写诊断与账本（排障模式，磁盘占用最小）。</summary>
        public static JournalCategoryPolicy DiagnosticsOnly() =>
            new JournalCategoryPolicy(false, 1, false)
                .Set(JournalCategory.Diagnostics, true, 1, forceFsync: true)
                .Set(JournalCategory.Inventory, true, 1, forceFsync: true)
                .Set(JournalCategory.Session, true, 1)
                .Set(JournalCategory.Approval, true, 1);
    }

    /// <summary>
    /// 存盘频率策略：把"想多久碰一次盘"翻译成具体选项，并给出可读的自检。
    /// 参数依据：写块次数 = 记录数 / RecordsPerBlock；轮转次数 = 磁盘字节 / SegmentBytes；
    /// 崩溃风险窗口 = FlushIntervalMs（最多丢一个周期的记录），因此 Capacity ≥ 记录速率 × 间隔。
    /// 演示页标记：POL03。
    /// </summary>
    public sealed class JournalWritePolicy
    {
        private JournalWritePolicy(JournalWriteProfile profile, int recordsPerBlock, long segmentBytes,
            int flushIntervalMs, bool flushToDisk, int capacity, int maxSegments,
            JournalCategoryPolicy categories)
        {
            Profile = profile;
            RecordsPerBlock = recordsPerBlock;
            SegmentBytes = segmentBytes;
            FlushIntervalMs = flushIntervalMs;
            FlushToDisk = flushToDisk;
            Capacity = capacity;
            MaxSegments = maxSegments;
            Categories = categories;
        }

        public JournalWriteProfile Profile { get; }
        public int RecordsPerBlock { get; }
        public long SegmentBytes { get; }
        public int FlushIntervalMs { get; }
        public bool FlushToDisk { get; }
        public int Capacity { get; }
        public int MaxSegments { get; }
        public JournalCategoryPolicy Categories { get; }

        public static JournalWritePolicy For(JournalWriteProfile profile)
        {
            switch (profile)
            {
                case JournalWriteProfile.RealTime:
                    return new JournalWritePolicy(profile, 256, 4L * 1024 * 1024, 250, false,
                        1 << 16, 8, JournalCategoryPolicy.Default());
                case JournalWriteProfile.MemoryOnly:
                    return new JournalWritePolicy(profile, 256, 4L * 1024 * 1024, 1000, false,
                        1 << 18, 0, JournalCategoryPolicy.Default());
                case JournalWriteProfile.LowFrequency:
                    return new JournalWritePolicy(profile, 1024, 32L * 1024 * 1024, 2000, false,
                        1 << 18, 4, JournalCategoryPolicy.LowFrequency());
                default:
                    return new JournalWritePolicy(JournalWriteProfile.Balanced, 512, 8L * 1024 * 1024, 1000,
                        false, 1 << 17, 8, JournalCategoryPolicy.Default());
            }
        }

        /// <summary>套用到选项上。<see cref="JournalWriteProfile.MemoryOnly"/> 会把目录清空。</summary>
        public JournalOptions ApplyTo(JournalOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.RecordsPerBlock = RecordsPerBlock;
            options.SegmentBytes = SegmentBytes;
            options.FlushIntervalMs = FlushIntervalMs;
            options.FlushToDisk = FlushToDisk;
            options.Capacity = Capacity;
            options.MaxSegments = MaxSegments;
            options.CategoryPolicy = Categories;
            if (Profile == JournalWriteProfile.MemoryOnly) options.Directory = null;
            return options;
        }

        public static JournalOptions Apply(JournalWriteProfile profile, JournalOptions options = null) =>
            For(profile).ApplyTo(options ?? new JournalOptions());

        /// <summary>可读自检：返回问题列表（空 = 通过）。不做静默修正。</summary>
        public IReadOnlyList<string> Validate(JournalOptions options)
        {
            var problems = new List<string>();
            JournalOptions target = options ?? ApplyTo(new JournalOptions());
            if ((target.Capacity & (target.Capacity - 1)) != 0 || target.Capacity <= 0)
                problems.Add("Capacity 必须是 2 的幂（当前 " + target.Capacity + "）");
            if (target.RecordsPerBlock <= 0 || (target.RecordsPerBlock & (target.RecordsPerBlock - 1)) != 0)
                problems.Add("RecordsPerBlock 必须是 2 的幂（当前 " + target.RecordsPerBlock + "）");
            if (target.SegmentBytes < target.RecordsPerBlock * JournalRecord.SizeInBytes + 64)
                problems.Add("SegmentBytes 装不下一个块（需要 ≥ " +
                    (target.RecordsPerBlock * JournalRecord.SizeInBytes + 64) + " 字节）");
            if (target.FlushIntervalMs <= 0)
                problems.Add("FlushIntervalMs 必须为正");
            if (Profile == JournalWriteProfile.MemoryOnly && !string.IsNullOrEmpty(target.Directory))
                problems.Add("MemoryOnly 预设不应设置 Directory");
            return problems;
        }

        /// <summary>给定记录速率，环形容量能撑多久（秒）。小于刷盘间隔就会开始覆盖未落盘记录。</summary>
        public double SafeIntervalSeconds(double recordsPerSecond) =>
            recordsPerSecond <= 0 ? double.PositiveInfinity : Capacity / recordsPerSecond;

        /// <summary>给定记录速率，每秒会写多少块（= 磁盘操作次数）。</summary>
        public double BlocksPerSecond(double recordsPerSecond) => recordsPerSecond / RecordsPerBlock;

        /// <summary>给定记录速率，每秒写多少字节（按 48 B/条估算，未计采样）。</summary>
        public double BytesPerSecond(double recordsPerSecond) => recordsPerSecond * JournalRecord.SizeInBytes;

        public string Describe() =>
            Profile + "：块 " + RecordsPerBlock + " 条 · 段 " + (SegmentBytes / 1024 / 1024) + " MiB · 刷盘 " +
            FlushIntervalMs + " ms · fsync " + (FlushToDisk ? "开" : "关") + " · 容量 " + Capacity +
            " 条（" + (Capacity * JournalRecord.SizeInBytes / 1024 / 1024) + " MiB）· 保留 " + MaxSegments + " 段";
    }
}
