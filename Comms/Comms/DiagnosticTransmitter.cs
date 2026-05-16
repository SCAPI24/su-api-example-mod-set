using System;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Comms;

public class DiagnosticTransmitter : IWrapperTransmitter, ITransmitter, IDisposable
{

    public ITransmitter BaseTransmitter { get; }

    public DiagnosticStats Stats { get; }

    public int MaxPacketSize => BaseTransmitter.MaxPacketSize;

    public IPEndPoint Address => BaseTransmitter.Address;

    public event Action<Exception> Error;

    public event Action<string> Debug;

    public event Action<Packet> PacketSent;

    public event Action<Packet> PacketReceived;

    public DiagnosticTransmitter(ITransmitter baseTransmitter)
        : this(baseTransmitter, new DiagnosticStats())
    {
    }

    public DiagnosticTransmitter(ITransmitter baseTransmitter, DiagnosticStats stats)
    {
        Stats = stats;
        BaseTransmitter = baseTransmitter ?? throw new ArgumentNullException(nameof(baseTransmitter));
        BaseTransmitter.Error += delegate (Exception e)
        {
            this.Error?.Invoke(e);
        };
        BaseTransmitter.PacketReceived += delegate (Packet packet)
        {
            if (Stats != null)
            {
                Interlocked.Increment(ref Stats.PacketsReceived);
                Interlocked.Add(ref Stats.BytesReceived, packet.Bytes.Length);
            }
            this.PacketReceived?.Invoke(packet);
        };
    }

    public void Dispose()
    {
        BaseTransmitter.Dispose();
    }

    public void SendPacket(Packet packet)
    {
        if (Stats != null)
        {
            Interlocked.Increment(ref Stats.PacketsSent);
            Interlocked.Add(ref Stats.BytesSent, packet.Bytes.Length);
        }
        BaseTransmitter.SendPacket(packet);
        this.PacketSent?.Invoke(packet);
    }
}
