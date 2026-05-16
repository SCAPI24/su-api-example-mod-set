namespace Comms.Drt;

internal class ClientGameDescriptionMessage : Message
{
    public int Step;

    public byte[] GameDescriptionBytes;

    internal override void Read(Reader reader)
    {
        Step = reader.ReadInt32();
        GameDescriptionBytes = reader.ReadBytes();
    }

    internal override void Write(Writer writer)
    {
        writer.WriteInt32(Step);
        writer.WriteBytes(GameDescriptionBytes);
    }
}
