using System;
using System.Net;

namespace Comms;

// Source: Comms/Comms/Comm.cs:Comm.ProcessConnections
// The event is optional and is only raised for an actual reliable resend.
public readonly struct ReliableRetransmitInfo
{
    public IPEndPoint Address { get; }

    public uint PacketId { get; }

    public int RetryNumber { get; }

    public int Bytes { get; }

    public string Source { get; }

    // This is the original application payload, not the UDP packet body.
    // It is retained only while the packet is unacknowledged or queued for audit.
    public byte[] Payload { get; }

    public ReliableRetransmitInfo(IPEndPoint address, uint packetId, int retryNumber,
        int bytes, string source, byte[] payload)
    {
        Address = address;
        PacketId = packetId;
        RetryNumber = retryNumber;
        Bytes = bytes;
        Source = source ?? string.Empty;
        Payload = payload;
    }
}

// Source: Comms/Comms/Comm.cs:Comm.ProcessConnections
public static class ReliableRetransmitDiagnostics
{
    public static event Action<ReliableRetransmitInfo> PacketRetransmitted;

    internal static void Report(ReliableRetransmitInfo info)
    {
        Action<ReliableRetransmitInfo> handlers = PacketRetransmitted;
        if (handlers == null)
            return;

        foreach (Action<ReliableRetransmitInfo> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(info);
            }
            catch
            {
                // Diagnostics must never interrupt the transport retry loop.
            }
        }
    }
}
