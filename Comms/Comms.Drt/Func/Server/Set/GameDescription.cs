namespace Comms.Drt;

public class GameDescription
{
    public ServerDescription ServerDescription;

    public int GameID;

    public int ClientsCount;

    public int Step;

    public byte[] GameDescriptionBytes;

    internal void Read(Reader reader)
    {
        GameID = reader.ReadPackedInt32();
        ClientsCount = reader.ReadPackedInt32();
        Step = reader.ReadPackedInt32();
        GameDescriptionBytes = reader.ReadBytes();
    }

    internal void Write(Writer writer)
    {
        writer.WritePackedInt32(GameID);
        writer.WritePackedInt32(ClientsCount);
        writer.WritePackedInt32(Step);
        writer.WriteBytes(GameDescriptionBytes);
    }
}
