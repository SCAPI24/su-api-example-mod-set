namespace Comms.Drt;

internal abstract class Message
{
    internal abstract void Read(Reader reader);

    internal abstract void Write(Writer writer);
}
