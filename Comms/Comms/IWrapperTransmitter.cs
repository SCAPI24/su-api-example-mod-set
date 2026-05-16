using System;

namespace Comms;

public interface IWrapperTransmitter : ITransmitter, IDisposable
{
    ITransmitter BaseTransmitter { get; }
}
