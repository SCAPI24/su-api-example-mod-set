namespace Comms.Drt;

internal class ClientJoinGameRefusedMessage : Message
{
    public int ClientID;

    public string Reason;

    internal override void Read(Reader reader)
    {
        ClientID = reader.ReadPackedInt32();
        Reason = reader.ReadString();
    }

    internal override void Write(Writer writer)
    {
        writer.WritePackedInt32(ClientID);
        writer.WriteString(Reason);
    }
}
