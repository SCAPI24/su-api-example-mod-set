using System.Net;

namespace Comms.Drt;

internal class MalformedMessageException : ProtocolViolationException
{
    public IPEndPoint SenderAddress;

    public MalformedMessageException(string message, IPEndPoint senderAddress)
        : base(message)
    {
        SenderAddress = senderAddress;
    }
}
