namespace Comms.Drt;

// Source: Comms.Drt/Message/ServerTickMessage.cs:ServerTickMessage.ClientTickData
internal class ServerDirectInputMessage : Message
{
    public int SourceClientID;

    public byte[] InputBytes;

    internal override void Read(Reader reader)
    {
        SourceClientID = reader.ReadPackedInt32();
        InputBytes = reader.ReadBytes();
    }

    internal override void Write(Writer writer)
    {
        writer.WritePackedInt32(SourceClientID);
        writer.WriteBytes(InputBytes);
    }
}
