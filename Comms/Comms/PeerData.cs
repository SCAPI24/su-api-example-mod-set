using System.Net;

namespace Comms;

public class PeerData
{
    internal double LastKeepAliveReceiveTime;

    internal double NextKeepAliveSendTime;

    public Peer Owner { get; internal set; }

    public IPEndPoint Address { get; internal set; }

    public float Ping { get; internal set; }

    public object Tag { get; set; }

    internal PeerData(Peer owner, IPEndPoint address)
    {
        Owner = owner;
        Address = address;
        LastKeepAliveReceiveTime = Comm.GetTime();
        NextKeepAliveSendTime = LastKeepAliveReceiveTime + (double)owner.Settings.KeepAlivePeriod;
    }
}
