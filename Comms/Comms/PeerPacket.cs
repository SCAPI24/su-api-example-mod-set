namespace Comms;

public struct PeerPacket
{
    public PeerData PeerData;

    public byte[] Bytes;

    public PeerPacket(PeerData peerData, byte[] bytes)
    {
        PeerData = peerData;
        Bytes = bytes;
    }
}
