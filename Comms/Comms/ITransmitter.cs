using System;
using System.Net;

namespace Comms;

public interface ITransmitter : IDisposable
{
    int MaxPacketSize { get; }

    IPEndPoint Address { get; }

    event Action<Exception> Error;

    event Action<string> Debug;

    event Action<Packet> PacketReceived;

    void SendPacket(Packet packet);
}
