using System;
using System.Diagnostics;
using SuMultiplayer.Protocol.Telemetry;

namespace SuMultiplayer.Engine
{
    public sealed class RuntimeTelemetrySampler
    {
        private long m_windowStartMs;
        private long m_frameCount;
        private double m_frameDurationTotal;
        private double m_frameDurationMax;
        private double m_lastCpuSeconds = double.NaN;

        public bool TrySample(long nowMs, float frameDuration, out RuntimeSample sample)
        {
            sample = null;
            double duration = frameDuration;
            if (duration > 0 && !double.IsNaN(duration) && !double.IsInfinity(duration))
            {
                m_frameCount++;
                m_frameDurationTotal += duration;
                if (duration > m_frameDurationMax) m_frameDurationMax = duration;
            }
            if (m_windowStartMs == 0)
            {
                m_windowStartMs = nowMs;
                m_lastCpuSeconds = ReadCpuSeconds();
                return false;
            }
            long elapsedMs = nowMs - m_windowStartMs;
            if (elapsedMs < 1000) return false;

            uint processorCount = (uint)Math.Max(1, Environment.ProcessorCount);
            var result = new RuntimeSample
            {
                SampledAtMs = (ulong)Math.Max(0, nowMs),
                ProcessorCount = processorCount,
                GameUpdateHz = (float)(m_frameCount * 1000.0 / elapsedMs),
                FrameTimeAvgMs = m_frameCount > 0
                    ? (float)(m_frameDurationTotal * 1000.0 / m_frameCount) : 0,
                FrameTimeMaxMs = (float)(m_frameDurationMax * 1000.0),
                ModuleSampleEvery = 4
            };
            double cpuSeconds = ReadCpuSeconds();
            if (!double.IsNaN(m_lastCpuSeconds) && !double.IsNaN(cpuSeconds))
            {
                float oneCorePercent = (float)(Math.Max(0, cpuSeconds - m_lastCpuSeconds) *
                    100000.0 / elapsedMs);
                result.CpuAvailable = true;
                result.CpuOneCorePercent = oneCorePercent;
                result.CpuTotalPercent = oneCorePercent / processorCount;
            }
            m_lastCpuSeconds = cpuSeconds;
            m_windowStartMs = nowMs;
            m_frameCount = 0;
            m_frameDurationTotal = 0;
            m_frameDurationMax = 0;
            sample = result;
            return true;
        }

        private static double ReadCpuSeconds()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                    return process.TotalProcessorTime.TotalSeconds;
            }
            catch (PlatformNotSupportedException) { return double.NaN; }
            catch (NotSupportedException) { return double.NaN; }
            catch (InvalidOperationException) { return double.NaN; }
        }
    }
}
