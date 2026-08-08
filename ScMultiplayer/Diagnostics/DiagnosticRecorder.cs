using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ScMultiplayer.Diagnostics
{
    // Source: Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerUpdateLoop.cs:
    // HandleReliableRetransmit
    // The recorder is deliberately bounded. A diagnostic burst can lose records, but it cannot
    // grow memory or block the Comms alarm thread.
    internal sealed class DiagnosticRecorder
    {
        private const int MaximumQueuedRecords = 1024;
        private readonly ConcurrentQueue<DiagnosticRecord> m_records =
            new ConcurrentQueue<DiagnosticRecord>();
        private readonly long[] m_droppedByKind =
            new long[Enum.GetValues<DiagnosticRecordKind>().Length];
        private int m_queuedRecords;
        private bool m_enabled = true;

        public bool Enabled
        {
            get => Volatile.Read(ref m_enabled);
            set => Volatile.Write(ref m_enabled, value);
        }

        public int Count => Math.Max(0, Volatile.Read(ref m_queuedRecords));

        public bool TryRecord(in DiagnosticRecord record)
        {
            if (!Enabled)
                return false;
            if (Interlocked.Increment(ref m_queuedRecords) > MaximumQueuedRecords)
            {
                Interlocked.Decrement(ref m_queuedRecords);
                Interlocked.Increment(ref m_droppedByKind[(int)record.Kind]);
                return false;
            }
            m_records.Enqueue(record);
            return true;
        }

        public int Drain(Action<DiagnosticRecord> consumer, int maximumRecords)
        {
            if (consumer == null || maximumRecords <= 0)
                return 0;
            int processed = 0;
            while (processed < maximumRecords && m_records.TryDequeue(
                out DiagnosticRecord record))
            {
                Interlocked.Decrement(ref m_queuedRecords);
                consumer(record);
                processed++;
            }
            return processed;
        }

        public long TakeDropped(DiagnosticRecordKind kind) =>
            Interlocked.Exchange(ref m_droppedByKind[(int)kind], 0L);

        public void Reset()
        {
            while (m_records.TryDequeue(out _)) { }
            Interlocked.Exchange(ref m_queuedRecords, 0);
            for (int i = 0; i < m_droppedByKind.Length; i++)
                Interlocked.Exchange(ref m_droppedByKind[i], 0L);
        }
    }
}
