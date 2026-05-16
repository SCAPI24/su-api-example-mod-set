namespace Comms.Drt;

internal class ClientInputMessage : Message
{
    public byte[] InputBytes;

    internal override void Read(Reader reader)
    {
        InputBytes = reader.ReadBytes();
    }

    internal override void Write(Writer writer)
    {
        writer.WriteBytes(InputBytes);
    }
}
