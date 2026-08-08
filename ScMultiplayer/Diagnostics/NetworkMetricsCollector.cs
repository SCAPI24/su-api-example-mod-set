using Comms;
using System;
using System.Threading;

namespace ScMultiplayer.Diagnostics
{
    internal readonly struct NetworkThroughputSample
    {
        public NetworkThroughputSample(float bytesPerSecond)
        {
            BytesPerSecond = Math.Max(0f, bytesPerSecond);
        }

        public float BytesPerSecond { get; }
    }

    // Source: Comms/Comms/DiagnosticTransmitter.cs:DiagnosticStats.BytesSent/BytesReceived
    // Owns only the byte-delta sample window. It does not read peers, allocate packets or decide
    // bandwidth policy, so it is safe to call from the existing HUD sampling point.
    internal sealed class NetworkMetricsCollector
    {
        private long m_lastByteSample;
        private double m_lastSampleTime;

        public NetworkThroughputSample Sample(DiagnosticStats stats, double now)
        {
            if (stats == null)
                return new NetworkThroughputSample(0f);

            long totalBytes = Math.Max(0L,
                Volatile.Read(ref stats.BytesSent) + Volatile.Read(ref stats.BytesReceived));
            float throughput = 0f;
            if (m_lastSampleTime > 0.0 && now > m_lastSampleTime)
            {
                long deltaBytes = totalBytes - m_lastByteSample;
                if (deltaBytes >= 0)
                    throughput = (float)(deltaBytes / (now - m_lastSampleTime));
            }

            m_lastByteSample = totalBytes;
            m_lastSampleTime = now;
            return new NetworkThroughputSample(throughput);
        }

        public void Reset()
        {
            m_lastByteSample = 0L;
            m_lastSampleTime = 0.0;
        }
    }
}
