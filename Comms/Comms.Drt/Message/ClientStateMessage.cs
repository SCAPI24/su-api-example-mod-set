namespace Comms.Drt;

internal class ClientStateMessage : Message
{
    public int Step;

    public byte[] StateBytes;

    internal override void Read(Reader reader)
    {
        Step = reader.ReadPackedInt32();
        StateBytes = reader.ReadBytes();
    }

    internal override void Write(Writer writer)
    {
        writer.WritePackedInt32(Step);
        writer.WriteBytes(StateBytes);
    }
}
