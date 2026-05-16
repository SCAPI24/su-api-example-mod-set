using System.Net;

namespace Comms.Drt;

public class ServerDescription
{
    public IPEndPoint Address;

    public bool IsLocal;

    public double DiscoveryTime;

    public float Ping;

    public string Name;

    public int Priority;

    public GameDescription[] GameDescriptions;
}
