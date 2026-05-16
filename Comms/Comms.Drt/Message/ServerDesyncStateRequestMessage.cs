namespace Comms.Drt;

internal class ServerDesyncStateRequestMessage : Message
{
    public int Step;

    internal override void Read(Reader reader)
    {
        Step = reader.ReadPackedInt32();
    }

    internal override void Write(Writer writer)
    {
        writer.WritePackedInt32(Step);
    }
}
