using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Comms;

public class LimiterTransmitter : IWrapperTransmitter, ITransmitter, IDisposable
{
    private Alarm Alarm;

    private volatile bool IsDisposed;

    private int BytesPerSecondLimit;

    private float Period;

    private double BytesRemaining;

    private double BytesRemainingTime;

    private Queue<Packet> QueuedPackets = new();

    private long QueuedBytes;

    private object Lock = new();



    public ITransmitter BaseTransmitter { get; }

    public int MaxPacketSize => BaseTransmitter.MaxPacketSize;

    public IPEndPoint Address => BaseTransmitter.Address;

    public event Action<Exception> Error;

    public event Action<string> Debug;

    public event Action<Packet> PacketReceived;

    public LimiterTransmitter(ITransmitter baseTransmitter, int bytesPerSecondLimit, float period = 0.01f)
    {
        BaseTransmitter = baseTransmitter ?? throw new ArgumentNullException(nameof(baseTransmitter));
        if (period < 0.005f || period > 0.1f)
        {
            throw new ArgumentException("period too small or too large.");
        }
        BytesPerSecondLimit = bytesPerSecondLimit;
        Period = period;
        BaseTransmitter.Error += InvokeError;
        BaseTransmitter.Debug += delegate (string s)
        {
            this.Debug?.Invoke(s);
        };
        BaseTransmitter.PacketReceived += delegate (Packet packet)
        {
            this.PacketReceived?.Invoke(packet);
        };
        Alarm = new Alarm(AlarmFunction);
        Alarm.Error += InvokeError;
        BytesRemaining = (float)bytesPerSecondLimit * Period;
        BytesRemainingTime = Comm.GetTime();
    }

    public void Dispose()
    {
        if (!IsDisposed)
        {
            IsDisposed = true;
            BaseTransmitter.Dispose();
            Alarm.Dispose();
        }
    }

    public void SendPacket(Packet packet)
    {
        CheckNotDisposed();
        lock (Lock)
        {
            if (QueuedPackets.Count > 0)
            {
                QueuedPackets.Enqueue(packet);
            }
            else if (!TrySend(packet))
            {
                QueuedPackets.Enqueue(packet);
                Alarm.Set(Period);
            }
        }
    }

    private void AlarmFunction()
    {
        lock (Lock)
        {
            if (IsDisposed)
            {
                return;
            }
            while (QueuedPackets.Count > 0)
            {
                Packet packet = QueuedPackets.Peek();
                if (TrySend(packet))
                {
                    QueuedPackets.Dequeue();
                    QueuedBytes -= packet.Bytes.Length;
                    continue;
                }
                Alarm.Set(Period);
                break;
            }
        }
    }

    private void Enqueue(Packet packet)
    {
        QueuedPackets.Enqueue(packet);
        QueuedBytes += packet.Bytes.Length;
        _ = QueuedBytes;
        _ = BytesPerSecondLimit * 10;
    }

    private bool TrySend(Packet packet)
    {
        double time = Comm.GetTime();
        double num = Math.Max((float)(2 * BytesPerSecondLimit) * Period, 2 * MaxPacketSize);
        BytesRemaining = Math.Min(BytesRemaining + (time - BytesRemainingTime) * (double)BytesPerSecondLimit, num);
        BytesRemainingTime = time;
        if ((double)packet.Bytes.Length <= BytesRemaining)
        {
            BytesRemaining -= packet.Bytes.Length;
            BaseTransmitter.SendPacket(packet);
            return true;
        }
        return false;
    }

    private void CheckNotDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException("LimiterTransmitter");
        }
    }

    private void InvokeError(Exception error)
    {
        this.Error?.Invoke(error);
    }

    [Conditional("DEBUG")]
    private void InvokeDebug(string message)
    {
        this.Debug?.Invoke(message);
    }

    [Conditional("DEBUG")]
    private void InvokeDebug(string format, params object[] args)
    {
        if (this.Debug != null)
        {
            this.Debug?.Invoke(string.Format(format, args));
        }
    }
}
