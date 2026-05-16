using System.Collections.Generic;

namespace Comms.Drt;

public class DesyncData
{
    public int GameID;

    public int Step;

    public int ClientsCount;

    public Dictionary<int, byte[]> PriorStates = new();

    public Dictionary<int, byte[]> States = new();
}
