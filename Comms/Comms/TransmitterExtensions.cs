namespace Comms;

public static class TransmitterExtensions
{
    public static ITransmitter RootTransmitter(this ITransmitter transmitter)
    {
        while (transmitter is IWrapperTransmitter wrapperTransmitter)
        {
            transmitter = wrapperTransmitter.BaseTransmitter;
        }
        return transmitter;
    }
}
