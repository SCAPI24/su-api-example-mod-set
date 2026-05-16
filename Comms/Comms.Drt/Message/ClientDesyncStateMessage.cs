namespace Comms.Drt;

internal class ClientDesyncStateMessage : Message
{
    public int Step;

    public byte[] StateBytes;

    public bool IsDeflated;

    internal override void Read(Reader reader)
    {
        int num = reader.ReadPackedInt32();
        IsDeflated = (num & 1) != 0;
        Step = num >> 1;
        StateBytes = reader.ReadBytes();
    }

    internal override void Write(Writer writer)
    {
        int value = (IsDeflated ? ((Step << 1) | 1) : (Step << 1));
        writer.WritePackedInt32(value);
        writer.WriteBytes(StateBytes);
    }
}
