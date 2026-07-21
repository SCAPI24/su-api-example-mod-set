namespace Comms.Drt;

public class ExplorerSettings
{
    public float LocalDiscoveryPeriod = 0.5f;

    // Source: Comms.Drt/Func/Explorer/Explorer.cs:Explorer.DiscoverLocalServers
    public int LocalDiscoveryPortBatchSize = 4;

    public float InternetDiscoveryPeriod = 3f;

    public float LocalRemoveTime = 12f;

    public float InternetRemoveTime = 7f;
}
