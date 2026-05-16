namespace Comms.Drt;

internal class ServerDiscoveryResponseMessage : Message
{
    public string Name;

    public int Priority;

    public GameDescription[] GamesDescriptions;

    internal override void Read(Reader reader)
    {
        Name = reader.ReadString();
        Priority = reader.ReadPackedInt32();
        GamesDescriptions = new GameDescription[reader.ReadPackedInt32(0, 1048576)];
        for (int i = 0; i < GamesDescriptions.Length; i++)
        {
            GamesDescriptions[i] = new GameDescription();
            GamesDescriptions[i].Read(reader);
        }
    }

    internal override void Write(Writer writer)
    {
        writer.WriteString(Name);
        writer.WritePackedInt32(Priority);
        writer.WritePackedInt32(GamesDescriptions.Length);
        GameDescription[] gamesDescriptions = GamesDescriptions;
        for (int i = 0; i < gamesDescriptions.Length; i++)
        {
            gamesDescriptions[i].Write(writer);
        }
    }
}
