namespace Comms;

public class PeerSettings
{
    public bool SendPeerConnectDisconnectNotifications = true;

    public float ConnectTimeOut = 8f;

    public float KeepAlivePeriod = 10f;

    public float KeepAliveResendPeriod = 1f;

    public float ConnectionLostPeriod = 30f;
}
