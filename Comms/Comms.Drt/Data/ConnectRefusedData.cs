using System.Net;

namespace Comms.Drt;

public struct ConnectRefusedData
{
    public IPEndPoint Address;

    public string Reason;
}
