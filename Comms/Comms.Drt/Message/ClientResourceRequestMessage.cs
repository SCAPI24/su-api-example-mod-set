namespace Comms.Drt;

internal class ClientResourceRequestMessage : Message
{
    public string Name;

    public int MinimumVersion;

    internal override void Read(Reader reader)
    {
        Name = reader.ReadString();
        MinimumVersion = reader.ReadInt32();
    }

    internal override void Write(Writer writer)
    {
        writer.WriteString(Name);
        writer.WriteInt32(MinimumVersion);
    }
}
