using System.Net;

namespace Comms;

public class KeepAliveTimeoutException : ProtocolViolationException
{
    public KeepAliveTimeoutException(string message)
        : base(message)
    {
    }
}
