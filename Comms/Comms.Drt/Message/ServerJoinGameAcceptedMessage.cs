namespace Comms.Drt;

internal class ServerJoinGameAcceptedMessage : Message
{
    public int GameID;

    public int ClientID;

    public float TickDuration;

    public int StepsPerTick;

    public DesyncDetectionMode DesyncDetectionMode;

    public int DesyncDetectionPeriod;

    public int Step;

    public byte[] StateBytes;

    public ServerTickMessage[] TickMessages;

    internal override void Read(Reader reader)
    {
        GameID = reader.ReadPackedInt32();
        ClientID = reader.ReadPackedInt32();
        TickDuration = reader.ReadSingle();
        StepsPerTick = reader.ReadPackedInt32();
        DesyncDetectionMode = (DesyncDetectionMode)reader.ReadByte();
        DesyncDetectionPeriod = reader.ReadPackedInt32();
        Step = reader.ReadInt32();
        StateBytes = reader.ReadBytes();
        int num = reader.ReadPackedInt32(0, 1048576);
        TickMessages = new ServerTickMessage[num];
        for (int i = 0; i < num; i++)
        {
            ServerTickMessage serverTickMessage = new();
            serverTickMessage.Read(reader);
            TickMessages[i] = serverTickMessage;
        }
    }

    internal override void Write(Writer writer)
    {
        writer.WritePackedInt32(GameID);
        writer.WritePackedInt32(ClientID);
        writer.WriteSingle(TickDuration);
        writer.WritePackedInt32(StepsPerTick);
        writer.WriteByte((byte)DesyncDetectionMode);
        writer.WritePackedInt32(DesyncDetectionPeriod);
        writer.WriteInt32(Step);
        writer.WriteBytes(StateBytes);
        writer.WritePackedInt32(TickMessages.Length);
        ServerTickMessage[] tickMessages = TickMessages;
        for (int i = 0; i < tickMessages.Length; i++)
        {
            tickMessages[i].Write(writer);
        }
    }
}
