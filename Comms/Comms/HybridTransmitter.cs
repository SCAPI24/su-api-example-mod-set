using System;
using System.Net;

namespace Comms;

// Source: Comms/Comms/ITransmitter.cs:ITransmitter.IsReliableStream
// The gameplay channel keeps its datagram socket (peer identity, broadcast discovery answers,
// unreliable state updates) and adds a TCP stream for reliable traffic. Comm sees a single
// transmitter; the packet type in the first byte of each Comm packet decides the path.
public class HybridTransmitter : IWrapperTransmitter, IDisposable
{
    public ITransmitter DatagramTransmitter { get; }

    public TcpTransmitter StreamTransmitter { get; }

    // The datagram side owns the identity: every message that carries a sender port keeps
    // reporting the UDP endpoint the peers already know.
    public ITransmitter BaseTransmitter => DatagramTransmitter;

    // Comm fragments by the smaller of the two so a datagram packet always fits its socket.
    public int MaxPacketSize =>
        Math.Min(DatagramTransmitter.MaxPacketSize, StreamTransmitter.MaxPacketSize);

    // Reliable traffic rides a stream, so Comm must not add its own ACK/resend layer.
    public bool IsReliableStream => true;

    public IPEndPoint Address => DatagramTransmitter.Address;

    public event Action<Exception>? Error;

    public event Action<string>? Debug;

    public event Action<Packet>? PacketReceived;

    public HybridTransmitter(ITransmitter datagramTransmitter, TcpTransmitter streamTransmitter)
    {
        DatagramTransmitter = datagramTransmitter ??
            throw new ArgumentNullException(nameof(datagramTransmitter));
        StreamTransmitter = streamTransmitter ??
            throw new ArgumentNullException(nameof(streamTransmitter));
        DatagramTransmitter.Error += InvokeError;
        StreamTransmitter.Error += InvokeError;
        DatagramTransmitter.Debug += InvokeDebug;
        StreamTransmitter.Debug += InvokeDebug;
        DatagramTransmitter.PacketReceived += InvokePacketReceived;
        StreamTransmitter.PacketReceived += InvokePacketReceived;
    }

    public void SendPacket(Packet packet)
    {
        byte[]? bytes = packet.Bytes;
        if (bytes != null && bytes.Length > 0 && packet.Address != null &&
            (bytes[0] & TcpTransmitter.PacketTypeMask) == TcpTransmitter.PacketTypeReliableData &&
            CanUseStream(packet.Address))
        {
            StreamTransmitter.SendPacket(packet);
            return;
        }
        DatagramTransmitter.SendPacket(packet);
    }

    // A stream is used when the peer already opened one, or when this side may dial one (the
    // client). Everything else, including broadcast answers, stays on the datagram path.
    private bool CanUseStream(IPEndPoint address)
    {
        return StreamTransmitter.HasConnection(address) || StreamTransmitter.CanDialOut;
    }

    public int GetPendingSendCount(IPEndPoint address)
    {
        return DatagramTransmitter.GetPendingSendCount(address) +
            StreamTransmitter.GetPendingSendCount(address);
    }

    public void Dispose()
    {
        DatagramTransmitter.Dispose();
        StreamTransmitter.Dispose();
    }

    private void InvokeError(Exception error)
    {
        Action<Exception>? handler = Error;
        if (handler != null)
        {
            try
            {
                handler(error);
            }
            catch (Exception)
            {
            }
        }
    }

    private void InvokeDebug(string message)
    {
        Action<string>? handler = Debug;
        if (handler != null)
        {
            try
            {
                handler(message);
            }
            catch (Exception)
            {
            }
        }
    }

    private void InvokePacketReceived(Packet packet)
    {
        Action<Packet>? handler = PacketReceived;
        if (handler != null)
        {
            try
            {
                handler(packet);
            }
            catch (Exception)
            {
            }
        }
    }
}
