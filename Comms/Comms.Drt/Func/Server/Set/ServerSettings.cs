namespace Comms.Drt;

public class ServerSettings
{
    public string Name = "Server";

    public int Priority = 100;

    public int MaxGames = 1000;

    public int MaxGamesToList = 50;

    public float GameListCacheTime = 1f;

    public float JoinRequestTimeout = 15f;

    public float StateRequestPeriod = 2f;

    public float GameDescriptionRequestPeriod = 2f;

    public float TurnBasedTickWaitTime = 0.05f;

    public DesyncDetectionMode DesyncDetectionMode = DesyncDetectionMode.Detect;

    public int DesyncDetectionPeriod = 20;

    public float DesyncDetectionStatesTimeout = 15f;
}
