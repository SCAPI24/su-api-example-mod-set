using System.Net;

namespace Comms;

public struct Packet
{
    public IPEndPoint Address;

    public byte[] Bytes;

    // Source: Comms/Comms/TcpTransmitter.cs:TcpTransmitter.ReadLoop
    // True when this packet was delivered by the reliable stream. The stream is the only channel
    // whose sender identity cannot be forged or rewritten by a NAT, so session identities (the
    // datagram token) are learned from stream packets only.
    public bool FromStream;

    public Packet(IPEndPoint address, byte[] bytes)
    {
        Address = address;
        Bytes = bytes;
        FromStream = false;
    }
}
