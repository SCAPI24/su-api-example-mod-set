using System;
using System.Diagnostics;
using System.Threading;

namespace HeadlessRenderingMod
{
    internal sealed class FrameRateLimiter
    {
        private readonly long m_intervalTicks;
        private long m_nextFrameTick;

        public FrameRateLimiter(int targetFrameRate)
        {
            if (targetFrameRate < 1)
                throw new ArgumentOutOfRangeException(nameof(targetFrameRate));

            m_intervalTicks = Math.Max(1L, Stopwatch.Frequency / targetFrameRate);
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Run
        public void WaitForNextFrame()
        {
            long now = Stopwatch.GetTimestamp();
            if (m_nextFrameTick == 0L)
                m_nextFrameTick = now + m_intervalTicks;

            long remaining = m_nextFrameTick - now;
            if (remaining > 0L)
            {
                int milliseconds = (int)Math.Ceiling(
                    remaining * 1000.0 / Stopwatch.Frequency);
                if (milliseconds > 0)
                    Thread.Sleep(milliseconds);
            }

            now = Stopwatch.GetTimestamp();
            if (now > m_nextFrameTick + 4L * m_intervalTicks)
                m_nextFrameTick = now + m_intervalTicks;
            else
                m_nextFrameTick += m_intervalTicks;
        }
    }
}
