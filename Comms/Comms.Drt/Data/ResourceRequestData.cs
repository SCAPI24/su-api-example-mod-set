using System.Net;

namespace Comms.Drt;

public struct ResourceRequestData
{
    public IPEndPoint Address;

    public string Name;

    public int MinimumVersion;
}
