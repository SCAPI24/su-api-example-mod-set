using System.Net;

namespace Comms.Drt;

public struct GameStepData
{
    public struct JoinData
    {
        public int ClientID;

        public IPEndPoint Address;

        public byte[] JoinRequestBytes;
    }

    public struct LeaveData
    {
        public int ClientID;
    }

    public struct InputData
    {
        public int ClientID;

        public byte[] InputBytes;
    }

    public int Step;

    public JoinData[] Joins;

    public LeaveData[] Leaves;

    public InputData[] Inputs;
}
