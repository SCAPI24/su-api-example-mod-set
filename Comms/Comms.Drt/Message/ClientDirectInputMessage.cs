namespace Comms.Drt;

// Source: Comms.Drt/Message/ClientInputMessage.cs:ClientInputMessage
// Direct inputs bypass the shared game-tick queue. Sequenced messages are used for ordered,
// target-only transfers such as a world snapshot followed by terrain catch-up batches.
internal class ClientDirectInputMessage : Message
{
    public int TargetClientID;

    public bool IsSequenced;

    public bool IsLatest;

    public byte[] InputBytes;

    internal override void Read(Reader reader)
    {
        TargetClientID = reader.ReadInt32();
        IsSequenced = reader.ReadBoolean();
        IsLatest = reader.ReadBoolean();
        InputBytes = reader.ReadBytes();
    }

    internal override void Write(Writer writer)
    {
        writer.WriteInt32(TargetClientID);
        writer.WriteBoolean(IsSequenced);
        writer.WriteBoolean(IsLatest);
        writer.WriteBytes(InputBytes);
    }
}
