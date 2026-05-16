namespace Comms.Drt;

internal class ClientJoinGameAcceptedMessage : Message
{
    public int ClientID;

    internal override void Read(Reader reader)
    {
        ClientID = reader.ReadPackedInt32();
    }

    internal override void Write(Writer writer)
    {
        writer.WritePackedInt32(ClientID);
    }
}
