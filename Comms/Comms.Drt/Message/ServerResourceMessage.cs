namespace Comms.Drt;

internal class ServerResourceMessage : Message
{
    public string Name;

    public int Version;

    public byte[] Bytes;

    internal override void Read(Reader reader)
    {
        Name = reader.ReadString();
        Version = reader.ReadInt32();
        Bytes = reader.ReadBytes();
    }

    internal override void Write(Writer writer)
    {
        writer.WriteString(Name);
        writer.WriteInt32(Version);
        writer.WriteBytes(Bytes);
    }
}
