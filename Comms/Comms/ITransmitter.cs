using System;
using System.Net;

namespace Comms;

public interface ITransmitter : IDisposable
{
    int MaxPacketSize { get; }

    // True when the transport itself guarantees delivery, ordering and no duplication
    // (a byte stream such as TCP). Comm then skips its own ACK/resend bookkeeping for
    // Reliable/ReliableSequenced traffic, because the stream already provides it.
    // Both peers must agree on this capability; the connection handshake negotiates it.
    bool IsReliableStream { get; }

    IPEndPoint Address { get; }

    event Action<Exception> Error;

    event Action<string> Debug;

    event Action<Packet> PacketReceived;

    void SendPacket(Packet packet);

    // Records still queued inside the transport for one peer. A datagram transport writes
    // straight to the socket and returns zero; a stream transport returns its send backlog so
    // Comm.GetUnackedPacketsCount stays meaningful as a flow-control signal for its callers.
    int GetPendingSendCount(IPEndPoint address);
}
