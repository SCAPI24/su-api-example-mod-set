namespace Comms;

public class DiagnosticStats
{
    public long PacketsReceived;

    public long PacketsSent;

    public long BytesSent;

    public long BytesReceived;

    public override string ToString()
    {
        return $"Sent {BytesSent:N0} bytes ({PacketsSent:N0} packets), received {BytesReceived:N0} bytes ({PacketsReceived:N0} packets)";
    }
}
