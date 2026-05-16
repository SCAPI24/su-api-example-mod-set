using System.Net;

namespace Comms.Drt;

internal class ServerCreateGameAcceptedMessage : Message
{
    public int GameID;

    public IPEndPoint CreatorAddress;

    public float TickDuration;

    public int StepsPerTick;

    public DesyncDetectionMode DesyncDetectionMode;

    public int DesyncDetectionPeriod;

    internal override void Read(Reader reader)
    {
        GameID = reader.ReadPackedInt32();
        CreatorAddress = reader.ReadIPEndPoint();
        TickDuration = reader.ReadSingle();
        StepsPerTick = reader.ReadPackedInt32();
        DesyncDetectionMode = (DesyncDetectionMode)reader.ReadByte();
        DesyncDetectionPeriod = reader.ReadPackedInt32();
    }

    internal override void Write(Writer writer)
    {
        writer.WritePackedInt32(GameID);
        writer.WriteIPEndPoint(CreatorAddress);
        writer.WriteSingle(TickDuration);
        writer.WritePackedInt32(StepsPerTick);
        writer.WriteByte((byte)DesyncDetectionMode);
        writer.WritePackedInt32(DesyncDetectionPeriod);
    }
}
