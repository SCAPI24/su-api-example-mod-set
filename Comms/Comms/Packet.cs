using System.Net;

namespace Comms;

public struct Packet
{
    public IPEndPoint Address;

    public byte[] Bytes;

    public Packet(IPEndPoint address, byte[] bytes)
    {
        Address = address;
        Bytes = bytes;
    }
}
