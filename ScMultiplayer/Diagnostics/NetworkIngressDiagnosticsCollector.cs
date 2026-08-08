using System;
using System.Diagnostics;
using System.Threading;

namespace ScMultiplayer.Diagnostics
{
    internal readonly struct NetworkIngressMetricsSnapshot
    {
        public NetworkIngressMetricsSnapshot(long windowMilliseconds, long received,
            long payloadBytes, long enqueued, long applied, long succeeded, long failed,
            double receiveToEnqueueP95Milliseconds, double queueP95Milliseconds,
            double applyP95Milliseconds, NetworkIngressCommandKind topKind, long topCount)
        {
            WindowMilliseconds = windowMilliseconds;
            Received = received;
            PayloadBytes = payloadBytes;
            Enqueued = enqueued;
            Applied = applied;
            Succeeded = succeeded;
            Failed = failed;
            ReceiveToEnqueueP95Milliseconds = receiveToEnqueueP95Milliseconds;
            QueueP95Milliseconds = queueP95Milliseconds;
            ApplyP95Milliseconds = applyP95Milliseconds;
            TopKind = topKind;
            TopCount = topCount;
        }

        public long WindowMilliseconds { get; }
        public long Received { get; }
        public long PayloadBytes { get; }
        public long Enqueued { get; }
        public long Applied { get; }
        public long Succeeded { get; }
        public long Failed { get; }
        public double ReceiveToEnqueueP95Milliseconds { get; }
        public double QueueP95Milliseconds { get; }
        public double ApplyP95Milliseconds { get; }
        public NetworkIngressCommandKind TopKind { get; }
        public long TopCount { get; }
    }

    // Source: Mod/ScMultiplayer/Control/NetworkMessageRouter.cs:NetworkMessageRouter.Route
    // Fixed counters and histograms avoid per-message records and formatting. One aggregate
    // snapshot is produced after Apply at most once per five-second active window.
    internal sealed class NetworkIngressDiagnosticsCollector
    {
        private const long SampleWindowMilliseconds = 5000L;
        private static readonly int[] s_latencyBucketMilliseconds =
        {
            0, 1, 2, 4, 8, 16, 33, 67, 125, 250, 500, 1000, 2000, 5000,
            int.MaxValue
        };

        private readonly object m_snapshotLock = new object();
        private readonly long[] m_receivedByKind =
            new long[Enum.GetValues<NetworkIngressCommandKind>().Length];
        private readonly long[] m_receiveToEnqueueLatency =
            new long[s_latencyBucketMilliseconds.Length];
        private readonly long[] m_queueLatency =
            new long[s_latencyBucketMilliseconds.Length];
        private readonly long[] m_applyLatency =
            new long[s_latencyBucketMilliseconds.Length];
        private long m_windowStartTimestamp;
        private long m_received;
        private long m_payloadBytes;
        private long m_enqueued;
        private long m_applied;
        private long m_succeeded;
        private long m_failed;

        public void RecordReceive(in NetworkIngressCommand command)
        {
            if (!command.IsValid)
                return;
            Interlocked.CompareExchange(ref m_windowStartTimestamp,
                command.ReceivedTimestamp, 0L);
            Interlocked.Increment(ref m_received);
            Interlocked.Add(ref m_payloadBytes, command.PayloadBytes);
            Interlocked.Increment(ref m_receivedByKind[(int)command.Kind]);
        }

        public void RecordEnqueue(in NetworkIngressCommand command)
        {
            if (!command.IsValid || command.EnqueuedTimestamp <= 0L)
                return;
            Interlocked.Increment(ref m_enqueued);
            RecordLatency(m_receiveToEnqueueLatency,
                command.EnqueuedTimestamp - command.ReceivedTimestamp);
        }

        public void RecordApply(in NetworkIngressCommand command, long applyTimestamp)
        {
            if (!command.IsValid || applyTimestamp <= 0L)
                return;
            Interlocked.Increment(ref m_applied);
            if (command.EnqueuedTimestamp > 0L)
                RecordLatency(m_queueLatency, applyTimestamp - command.EnqueuedTimestamp);
        }

        public void RecordResult(in NetworkIngressCommand command, long applyTimestamp,
            long resultTimestamp, bool succeeded)
        {
            if (!command.IsValid || resultTimestamp <= 0L)
                return;
            if (succeeded)
                Interlocked.Increment(ref m_succeeded);
            else
                Interlocked.Increment(ref m_failed);
            if (applyTimestamp > 0L)
                RecordLatency(m_applyLatency, resultTimestamp - applyTimestamp);
        }

        public bool TryTakeSnapshot(long nowTimestamp,
            out NetworkIngressMetricsSnapshot snapshot)
        {
            snapshot = default;
            long start = Volatile.Read(ref m_windowStartTimestamp);
            if (start <= 0L || nowTimestamp <= start ||
                ToMilliseconds(nowTimestamp - start) < SampleWindowMilliseconds)
                return false;

            lock (m_snapshotLock)
            {
                start = Volatile.Read(ref m_windowStartTimestamp);
                if (start <= 0L || nowTimestamp <= start ||
                    ToMilliseconds(nowTimestamp - start) < SampleWindowMilliseconds)
                    return false;

                Interlocked.Exchange(ref m_windowStartTimestamp, 0L);
                long received = Interlocked.Exchange(ref m_received, 0L);
                long payloadBytes = Interlocked.Exchange(ref m_payloadBytes, 0L);
                long enqueued = Interlocked.Exchange(ref m_enqueued, 0L);
                long applied = Interlocked.Exchange(ref m_applied, 0L);
                long succeeded = Interlocked.Exchange(ref m_succeeded, 0L);
                long failed = Interlocked.Exchange(ref m_failed, 0L);
                NetworkIngressCommandKind topKind = NetworkIngressCommandKind.None;
                long topCount = 0L;
                for (int i = 1; i < m_receivedByKind.Length; i++)
                {
                    long count = Interlocked.Exchange(ref m_receivedByKind[i], 0L);
                    if (count > topCount)
                    {
                        topCount = count;
                        topKind = (NetworkIngressCommandKind)i;
                    }
                }

                snapshot = new NetworkIngressMetricsSnapshot(
                    ToMilliseconds(nowTimestamp - start), received, payloadBytes,
                    enqueued, applied, succeeded, failed,
                    TakeP95Milliseconds(m_receiveToEnqueueLatency),
                    TakeP95Milliseconds(m_queueLatency),
                    TakeP95Milliseconds(m_applyLatency), topKind, topCount);
                return received > 0L || enqueued > 0L || applied > 0L;
            }
        }

        public void Reset()
        {
            Interlocked.Exchange(ref m_windowStartTimestamp, 0L);
            Interlocked.Exchange(ref m_received, 0L);
            Interlocked.Exchange(ref m_payloadBytes, 0L);
            Interlocked.Exchange(ref m_enqueued, 0L);
            Interlocked.Exchange(ref m_applied, 0L);
            Interlocked.Exchange(ref m_succeeded, 0L);
            Interlocked.Exchange(ref m_failed, 0L);
            ResetArray(m_receivedByKind);
            ResetArray(m_receiveToEnqueueLatency);
            ResetArray(m_queueLatency);
            ResetArray(m_applyLatency);
        }

        private static void RecordLatency(long[] histogram, long elapsedTicks)
        {
            long elapsedMilliseconds = ToMilliseconds(Math.Max(0L, elapsedTicks));
            int bucket = s_latencyBucketMilliseconds.Length - 1;
            for (int i = 0; i < s_latencyBucketMilliseconds.Length - 1; i++)
            {
                if (elapsedMilliseconds <= s_latencyBucketMilliseconds[i])
                {
                    bucket = i;
                    break;
                }
            }
            Interlocked.Increment(ref histogram[bucket]);
        }

        private static double TakeP95Milliseconds(long[] histogram)
        {
            long total = 0L;
            var counts = new long[histogram.Length];
            for (int i = 0; i < histogram.Length; i++)
            {
                counts[i] = Interlocked.Exchange(ref histogram[i], 0L);
                total += counts[i];
            }
            if (total <= 0L)
                return 0.0;
            long target = Math.Max(1L, (long)Math.Ceiling(total * 0.95));
            long cumulative = 0L;
            for (int i = 0; i < counts.Length; i++)
            {
                cumulative += counts[i];
                if (cumulative >= target)
                    return s_latencyBucketMilliseconds[i] == int.MaxValue
                        ? 5000.0
                        : s_latencyBucketMilliseconds[i];
            }
            return 0.0;
        }

        private static long ToMilliseconds(long ticks)
        {
            return ticks <= 0L ? 0L : ticks * 1000L / Stopwatch.Frequency;
        }

        private static void ResetArray(long[] values)
        {
            for (int i = 0; i < values.Length; i++)
                Interlocked.Exchange(ref values[i], 0L);
        }
    }
}
