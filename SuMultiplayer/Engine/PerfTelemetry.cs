using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 一帧耗时窗口的只读快照（每次输出摘要时构建一次；热路径不分配）。
    /// 时间单位统一为毫秒。
    /// </summary>
    public readonly struct FrameTimeSnapshot
    {
        public FrameTimeSnapshot(int capacity, int windowCount, double averageMs, double p50Ms,
            double p95Ms, double maxMs, int windowOver33, int windowOver50, int windowOver100,
            long totalFrames, long totalOver33, long totalOver50, long totalOver100,
            long invalidSamples)
        {
            Capacity = capacity;
            WindowCount = windowCount;
            AverageMs = averageMs;
            P50Ms = p50Ms;
            P95Ms = p95Ms;
            MaxMs = maxMs;
            WindowOver33 = windowOver33;
            WindowOver50 = windowOver50;
            WindowOver100 = windowOver100;
            TotalFrames = totalFrames;
            TotalOver33 = totalOver33;
            TotalOver50 = totalOver50;
            TotalOver100 = totalOver100;
            InvalidSamples = invalidSamples;
        }

        /// <summary>窗口容量（固定 600）。</summary>
        public int Capacity { get; }

        /// <summary>窗口里实际有多少帧（&lt;= Capacity）。</summary>
        public int WindowCount { get; }

        public double AverageMs { get; }

        public double P50Ms { get; }

        public double P95Ms { get; }

        public double MaxMs { get; }

        /// <summary>窗口内耗时严格大于 33 ms 的帧数（"超过" = 严格大于，33.0 本身不算）。</summary>
        public int WindowOver33 { get; }

        public int WindowOver50 { get; }

        public int WindowOver100 { get; }

        /// <summary>本次会话累计计入窗口的帧数（非法样本不算）。</summary>
        public long TotalFrames { get; }

        /// <summary>本次会话累计超过 33 ms 的帧数（perfSlowFrames）。</summary>
        public long TotalOver33 { get; }

        public long TotalOver50 { get; }

        public long TotalOver100 { get; }

        /// <summary>非法样本数（NaN / Infinity / 负数）。显式计数，绝不静默丢弃。</summary>
        public long InvalidSamples { get; }
    }

    /// <summary>
    /// 最近 N 帧耗时的环形窗口。热路径只有一次数组写入 + 几次递增；
    /// 排序 / 百分位 / 字符串只在每 10 秒输出摘要时做。
    /// </summary>
    public sealed class FrameTimeWindow
    {
        public const int DefaultCapacity = 600;
        public const double SlowFrame33Ms = 33.0;
        public const double SlowFrame50Ms = 50.0;
        public const double SlowFrame100Ms = 100.0;

        private readonly double[] m_samples;
        private int m_next;
        private int m_count;
        private long m_totalFrames;
        private long m_totalOver33;
        private long m_totalOver50;
        private long m_totalOver100;
        private long m_invalidSamples;

        public FrameTimeWindow(int capacity = DefaultCapacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            m_samples = new double[capacity];
        }

        public int Capacity => m_samples.Length;

        public int WindowCount => m_count;

        public long TotalFrames => m_totalFrames;

        public long TotalSlow33 => m_totalOver33;

        public long TotalSlow50 => m_totalOver50;

        public long TotalSlow100 => m_totalOver100;

        public long InvalidSamples => m_invalidSamples;

        /// <summary>记录一帧耗时（毫秒）。非法值显式计数，不进入窗口。</summary>
        public void AddMilliseconds(double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0.0)
            {
                m_invalidSamples++;
                return;
            }
            m_samples[m_next] = milliseconds;
            m_next++;
            if (m_next >= m_samples.Length) m_next = 0;
            if (m_count < m_samples.Length) m_count++;
            m_totalFrames++;
            if (milliseconds > SlowFrame33Ms) m_totalOver33++;
            if (milliseconds > SlowFrame50Ms) m_totalOver50++;
            if (milliseconds > SlowFrame100Ms) m_totalOver100++;
        }

        /// <summary>构建窗口快照（排序一份窗口副本；只在摘要输出时调用）。</summary>
        public FrameTimeSnapshot Snapshot()
        {
            if (m_count == 0)
            {
                return new FrameTimeSnapshot(m_samples.Length, 0, 0.0, 0.0, 0.0, 0.0,
                    0, 0, 0, m_totalFrames, m_totalOver33, m_totalOver50, m_totalOver100,
                    m_invalidSamples);
            }

            var sorted = new double[m_count];
            double sum = 0.0;
            double max = 0.0;
            int over33 = 0;
            int over50 = 0;
            int over100 = 0;
            for (int i = 0; i < m_count; i++)
            {
                double value = m_samples[i];
                sorted[i] = value;
                sum += value;
                if (value > max) max = value;
                if (value > SlowFrame33Ms) over33++;
                if (value > SlowFrame50Ms) over50++;
                if (value > SlowFrame100Ms) over100++;
            }
            Array.Sort(sorted);
            return new FrameTimeSnapshot(m_samples.Length, m_count, sum / m_count,
                NearestRankPercentile(sorted, 50.0), NearestRankPercentile(sorted, 95.0), max,
                over33, over50, over100, m_totalFrames, m_totalOver33, m_totalOver50,
                m_totalOver100, m_invalidSamples);
        }

        /// <summary>
        /// nearest-rank 百分位：rank = ceil(p/100 * n)，取 sorted[rank-1]。
        /// 对 600 帧窗口 p50 = 第 300 个、p95 = 第 570 个；对 5 帧 p50 = 第 3 个。
        /// </summary>
        public static double NearestRankPercentile(double[] sorted, double percentile)
        {
            if (sorted == null) throw new ArgumentNullException(nameof(sorted));
            if (double.IsNaN(percentile) || percentile < 0.0 || percentile > 100.0)
                throw new ArgumentOutOfRangeException(nameof(percentile));
            if (sorted.Length == 0) return 0.0;
            if (percentile <= 0.0) return sorted[0];
            if (percentile >= 100.0) return sorted[sorted.Length - 1];
            int rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
            int index = rank - 1;
            if (index < 0) index = 0;
            if (index >= sorted.Length) index = sorted.Length - 1;
            return sorted[index];
        }
    }

    /// <summary>单个帧步骤的累计 / 最近 10 秒统计快照。</summary>
    public readonly struct StepTimingSnapshot
    {
        public StepTimingSnapshot(string name, long count, double totalMs, double maxMs,
            long slowCount, long periodCount, double periodTotalMs, double periodMaxMs,
            long periodSlowCount)
        {
            Name = name;
            Count = count;
            TotalMs = totalMs;
            MaxMs = maxMs;
            SlowCount = slowCount;
            PeriodCount = periodCount;
            PeriodTotalMs = periodTotalMs;
            PeriodMaxMs = periodMaxMs;
            PeriodSlowCount = periodSlowCount;
        }

        public string Name { get; }

        public long Count { get; }

        public double TotalMs { get; }

        public double AverageMs => Count > 0 ? TotalMs / Count : 0.0;

        public double MaxMs { get; }

        /// <summary>累计耗时严格大于 16 ms 的次数。</summary>
        public long SlowCount { get; }

        public long PeriodCount { get; }

        public double PeriodTotalMs { get; }

        public double PeriodAverageMs => PeriodCount > 0 ? PeriodTotalMs / PeriodCount : 0.0;

        public double PeriodMaxMs { get; }

        public long PeriodSlowCount { get; }
    }

    /// <summary>
    /// 帧步骤耗时表：累计总耗时 / 最大耗时 / 超阈值次数，同时维护"最近一个摘要周期"的副本。
    /// 步骤名按首次出现自动登记（模块 id 由 ModuleHost 直接传入，不在热路径拼字符串）。
    /// 只由主线程访问；不做锁。
    /// </summary>
    public sealed class StepTimingTable
    {
        /// <summary>单步超过 16 ms（60 Hz 下约一帧）计一次"慢步骤"。</summary>
        public const double SlowStepThresholdMs = 16.0;

        private sealed class Entry
        {
            public string Name;
            public long Count;
            public double TotalMs;
            public double MaxMs;
            public long SlowCount;
            public long PeriodCount;
            public double PeriodTotalMs;
            public double PeriodMaxMs;
            public long PeriodSlowCount;
        }

        private readonly List<Entry> m_entries = new List<Entry>();
        private readonly Dictionary<string, int> m_index = new Dictionary<string, int>(StringComparer.Ordinal);
        private long m_invalidSamples;

        public int Count => m_entries.Count;

        public long InvalidSamples => m_invalidSamples;

        public long TotalCalls
        {
            get
            {
                long total = 0;
                foreach (Entry entry in m_entries) total += entry.Count;
                return total;
            }
        }

        public long TotalSlow
        {
            get
            {
                long total = 0;
                foreach (Entry entry in m_entries) total += entry.SlowCount;
                return total;
            }
        }

        public long PeriodCalls
        {
            get
            {
                long total = 0;
                foreach (Entry entry in m_entries) total += entry.PeriodCount;
                return total;
            }
        }

        public long PeriodSlow
        {
            get
            {
                long total = 0;
                foreach (Entry entry in m_entries) total += entry.PeriodSlowCount;
                return total;
            }
        }

        /// <summary>显式登记步骤名；重复登记抛异常（防止两个步骤混进同一项）。</summary>
        public int Register(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("步骤名不能为空", nameof(name));
            if (m_index.ContainsKey(name))
                throw new InvalidOperationException("步骤名重复登记：" + name);
            var entry = new Entry { Name = name };
            int index = m_entries.Count;
            m_entries.Add(entry);
            m_index.Add(name, index);
            return index;
        }

        /// <summary>按步骤名累加一次耗时；首次出现的名字自动登记（模块 id 是稳定字符串）。</summary>
        public void Add(string name, double milliseconds)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("步骤名不能为空", nameof(name));
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0.0)
            {
                m_invalidSamples++;
                return;
            }
            if (!m_index.TryGetValue(name, out int index))
            {
                index = m_entries.Count;
                m_entries.Add(new Entry { Name = name });
                m_index.Add(name, index);
            }
            AddCore(m_entries[index], milliseconds);
        }

        /// <summary>按登记下标累加一次耗时（热路径可预先 Register）。</summary>
        public void Add(int stepIndex, double milliseconds)
        {
            if (stepIndex < 0 || stepIndex >= m_entries.Count)
                throw new ArgumentOutOfRangeException(nameof(stepIndex));
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0.0)
            {
                m_invalidSamples++;
                return;
            }
            AddCore(m_entries[stepIndex], milliseconds);
        }

        private static void AddCore(Entry entry, double milliseconds)
        {
            entry.Count++;
            entry.TotalMs += milliseconds;
            if (milliseconds > entry.MaxMs) entry.MaxMs = milliseconds;
            if (milliseconds > SlowStepThresholdMs) entry.SlowCount++;
            entry.PeriodCount++;
            entry.PeriodTotalMs += milliseconds;
            if (milliseconds > entry.PeriodMaxMs) entry.PeriodMaxMs = milliseconds;
            if (milliseconds > SlowStepThresholdMs) entry.PeriodSlowCount++;
        }

        /// <summary>清空最近周期统计（累计统计保留）。每次输出 10 秒摘要后调用。</summary>
        public void ResetPeriod()
        {
            foreach (Entry entry in m_entries)
            {
                entry.PeriodCount = 0;
                entry.PeriodTotalMs = 0.0;
                entry.PeriodMaxMs = 0.0;
                entry.PeriodSlowCount = 0;
            }
        }

        public IReadOnlyList<StepTimingSnapshot> SnapshotAll()
        {
            var list = new List<StepTimingSnapshot>(m_entries.Count);
            foreach (Entry entry in m_entries)
            {
                list.Add(new StepTimingSnapshot(entry.Name, entry.Count, entry.TotalMs,
                    entry.MaxMs, entry.SlowCount, entry.PeriodCount, entry.PeriodTotalMs,
                    entry.PeriodMaxMs, entry.PeriodSlowCount));
            }
            return list;
        }

        /// <summary>
        /// 最慢的 N 个步骤。recent=true 按最近周期总耗时排序（10 秒摘要用），
        /// false 按会话累计总耗时排序；总耗时相同再按最大耗时、最后按名称 ordinal 保证输出稳定。
        /// </summary>
        public IReadOnlyList<StepTimingSnapshot> SnapshotTop(int max, bool recent)
        {
            if (max <= 0) return Array.Empty<StepTimingSnapshot>();
            var list = new List<StepTimingSnapshot>(SnapshotAll());
            list.Sort((a, b) => CompareTop(a, b, recent));
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        private static int CompareTop(StepTimingSnapshot a, StepTimingSnapshot b, bool recent)
        {
            double aTotal = recent ? a.PeriodTotalMs : a.TotalMs;
            double bTotal = recent ? b.PeriodTotalMs : b.TotalMs;
            int compare = bTotal.CompareTo(aTotal);
            if (compare != 0) return compare;
            double aMax = recent ? a.PeriodMaxMs : a.MaxMs;
            double bMax = recent ? b.PeriodMaxMs : b.MaxMs;
            compare = bMax.CompareTo(aMax);
            if (compare != 0) return compare;
            return string.CompareOrdinal(a.Name, b.Name);
        }
    }

    /// <summary>性能遥测日志格式（纯字符串拼接，只在 10 秒摘要时调用）。</summary>
    public static class PerfLogFormatter
    {
        public const string Prefix = "[SuMP][perf]";

        public static string FormatMilliseconds(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "n/a";
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>拼出每 10 秒一行的可 grep 摘要（关键字固定为 [SuMP][perf]）。</summary>
        public static string BuildFrameLine(FrameTimeSnapshot frames, double engineFrameDurationMs,
            int engineFrameIndex, string stepTop5, long stepCalls, long stepSlow,
            string loadClues)
        {
            var text = new StringBuilder(384);
            text.Append(Prefix);
            text.Append(" scope=Frame.Update window=").Append(frames.WindowCount)
                .Append('/').Append(frames.Capacity);
            text.Append(" perfFrames=").Append(frames.TotalFrames);
            text.Append(" perfSlowFrames=").Append(frames.TotalOver33);
            text.Append(" frameMs avg=").Append(FormatMilliseconds(frames.AverageMs));
            text.Append(" p50=").Append(FormatMilliseconds(frames.P50Ms));
            text.Append(" p95=").Append(FormatMilliseconds(frames.P95Ms));
            text.Append(" max=").Append(FormatMilliseconds(frames.MaxMs));
            text.Append(" over33=").Append(frames.WindowOver33);
            text.Append(" over50=").Append(frames.WindowOver50);
            text.Append(" over100=").Append(frames.WindowOver100);
            text.Append(" invalid=").Append(frames.InvalidSamples);
            text.Append(" | engineFrameDurationMs=");
            if (IsUsableDuration(engineFrameDurationMs))
                text.Append(FormatMilliseconds(engineFrameDurationMs));
            else
                text.Append("n/a(<=0)");
            text.Append(" engineFrameIndex=").Append(engineFrameIndex);
            text.Append(" | perfStepCalls=").Append(stepCalls);
            text.Append(" perfStepSlow=").Append(stepSlow);
            text.Append(" perfStepTop5=")
                .Append(string.IsNullOrEmpty(stepTop5) ? "none" : stepTop5);
            text.Append(" | ")
                .Append(string.IsNullOrEmpty(loadClues) ? "load=n/a(no-clues)" : loadClues);
            return text.ToString();
        }

        private static bool IsUsableDuration(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;

        /// <summary>慢步骤列表：name:avg=..,max=..,n=..,over16=..</summary>
        public static string FormatSteps(IReadOnlyList<StepTimingSnapshot> steps, bool recent)
        {
            if (steps == null || steps.Count == 0) return "none";
            var text = new StringBuilder(192);
            for (int i = 0; i < steps.Count; i++)
            {
                if (i > 0) text.Append(';');
                StepTimingSnapshot step = steps[i];
                text.Append(step.Name)
                    .Append(":avg=")
                    .Append(FormatMilliseconds(recent ? step.PeriodAverageMs : step.AverageMs))
                    .Append(",max=")
                    .Append(FormatMilliseconds(recent ? step.PeriodMaxMs : step.MaxMs))
                    .Append(",n=")
                    .Append(recent ? step.PeriodCount : step.Count)
                    .Append(",over16=")
                    .Append(recent ? step.PeriodSlowCount : step.SlowCount);
            }
            return text.ToString();
        }

        /// <summary>只读计数的速率（/s）。计数回退或间隔非法时显式写 n/a，不伪造 0。</summary>
        public static string FormatRate(string name, long current, long previous,
            double elapsedSeconds)
        {
            string safeName = string.IsNullOrEmpty(name) ? "counter" : name;
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) ||
                elapsedSeconds <= 0.0)
                return safeName + "=" + current + "(n/a:elapsed<=0)";
            if (current < previous)
                return safeName + "=" + current + "(n/a:counter-reset)";
            double rate = (current - previous) / elapsedSeconds;
            return safeName + "=" + current + "(" +
                rate.ToString("0.0", CultureInfo.InvariantCulture) + "/s)";
        }
    }
}
