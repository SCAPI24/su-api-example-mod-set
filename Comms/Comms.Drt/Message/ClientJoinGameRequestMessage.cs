namespace Comms.Drt;

internal class ClientJoinGameRequestMessage : Message
{
    public int GameID;

    public string ClientName;

    public byte[] JoinRequestBytes;

    internal override void Read(Reader reader)
    {
        GameID = reader.ReadPackedInt32();
        ClientName = reader.ReadString();
        JoinRequestBytes = reader.ReadBytes();
    }

    internal override void Write(Writer writer)
    {
        writer.WritePackedInt32(GameID);
        writer.WriteString(ClientName);
        writer.WriteBytes(JoinRequestBytes);
    }
}
