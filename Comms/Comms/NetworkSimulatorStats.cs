using System;
using System.Threading;

namespace Comms;

public class NetworkSimulatorStats : DiagnosticStats
{
    internal int LastActivityTicks = -1;

    public long PacketsDropped;

    public override string ToString()
    {
        return $"Sent: {BytesSent:N0} bytes ({PacketsSent:N0} packets), received {BytesReceived:N0} bytes ({PacketsReceived:N0} packets), dropped {PacketsDropped:N0} packets";
    }

    public float GetIdleTime()
    {
        if (LastActivityTicks < 0)
        {
            return 0f;
        }
        return (float)((Environment.TickCount & 0x7FFFFFFF) - LastActivityTicks) / 1000f;
    }

    public void WaitUntilIdle(float idleTime)
    {
        while (GetIdleTime() <= idleTime)
        {
            Thread.Sleep(10);
        }
    }
}
