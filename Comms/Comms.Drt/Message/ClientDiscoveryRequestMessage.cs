namespace Comms.Drt;

internal class ClientDiscoveryRequestMessage : Message
{
    public double ProbeSendTime;

    internal override void Read(Reader reader)
    {
        if (reader.Length - reader.Position >= 8)
            ProbeSendTime = reader.ReadDouble();
    }

    internal override void Write(Writer writer)
    {
        if (ProbeSendTime > 0.0)
            writer.WriteDouble(ProbeSendTime);
    }
}
