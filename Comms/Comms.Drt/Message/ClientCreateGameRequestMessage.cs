namespace Comms.Drt;

internal class ClientCreateGameRequestMessage : Message
{
    public string ClientName;

    public byte[] GameDescriptionBytes;

    internal override void Read(Reader reader)
    {
        ClientName = reader.ReadString();
        GameDescriptionBytes = reader.ReadBytes();
    }

    internal override void Write(Writer writer)
    {
        writer.WriteString(ClientName);
        writer.WriteBytes(GameDescriptionBytes);
    }
}
