using System;
using System.Collections.Generic;
using System.Diagnostics;
using SuMultiplayer.Protocol.Telemetry;

namespace SuMultiplayer.Modules
{
    public sealed class CircuitProbeRecorder
    {
        private const double SlowCallThresholdMs = 20.0;

        private sealed class Entry
        {
            public string Id;
            public ulong Calls;
            public double TotalMs;
            public double MaxMs;
            public double LastMs;
            public ulong SlowCalls;
            public string MaxContext = string.Empty;
        }

        private readonly Dictionary<string, Entry> m_entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<Entry> m_orderedEntries = new List<Entry>(16);

        public long Start() => Stopwatch.GetTimestamp();

        public void Record(string id, long started, string trigger, int scopeChunks,
            int dirtyChunks, int pendingSnapshots, int states, int hashChunks)
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - started;
            double elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
            if (!m_entries.TryGetValue(id, out Entry entry))
            {
                entry = new Entry { Id = id };
                m_entries.Add(id, entry);
                m_orderedEntries.Add(entry);
            }

            entry.Calls++;
            entry.TotalMs += elapsedMs;
            entry.LastMs = elapsedMs;
            bool newMaximum = elapsedMs > entry.MaxMs;
            if (newMaximum)
            {
                entry.MaxMs = elapsedMs;
            }
            if (elapsedMs >= SlowCallThresholdMs)
            {
                entry.SlowCalls++;
                if (newMaximum)
                    entry.MaxContext = "trigger=" + trigger + ";scope=" + scopeChunks +
                        ";dirty=" + dirtyChunks + ";snapshots=" + pendingSnapshots +
                        ";states=" + states + ";hashChunks=" + hashChunks;
            }
        }

        public void AppendWindowAndReset(Google.Protobuf.Collections.RepeatedField<CircuitStageSample> target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            target.Clear();
            foreach (Entry entry in m_orderedEntries)
            {
                target.Add(new CircuitStageSample
                {
                    Id = entry.Id,
                    Calls = entry.Calls,
                    TotalMs = (float)entry.TotalMs,
                    MaxMs = (float)entry.MaxMs,
                    LastMs = (float)entry.LastMs,
                    SlowCalls = entry.SlowCalls,
                    MaxContext = entry.MaxContext
                });
                entry.Calls = 0;
                entry.TotalMs = 0;
                entry.MaxMs = 0;
                entry.LastMs = 0;
                entry.SlowCalls = 0;
                entry.MaxContext = string.Empty;
            }
        }
    }
}
