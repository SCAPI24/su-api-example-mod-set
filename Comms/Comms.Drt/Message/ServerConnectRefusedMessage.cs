namespace Comms.Drt;

internal class ServerConnectRefusedMessage : Message
{
    public string Reason;

    internal override void Read(Reader reader)
    {
        Reason = reader.ReadString();
    }

    internal override void Write(Writer writer)
    {
        writer.WriteString(Reason);
    }
}
