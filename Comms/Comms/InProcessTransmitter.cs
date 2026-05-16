using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Comms;

public class InProcessTransmitter : ITransmitter, IDisposable
{
    private static InProcessTransmitter[] Transmitters = new InProcessTransmitter[256];

    private volatile bool IsDisposed;

    private Task Task;

    private CancellationTokenSource CancellationTokenSource = new();

    private BlockingCollection<Packet> SendQueue = new();


    public int MaxPacketSize { get; set; } = 1024;

    public IPEndPoint Address { get; private set; }

    public event Action<Exception> Error
    {
        add
        {
        }
        remove
        {
        }
    }

    public event Action<string> Debug;

    public event Action<Packet> PacketReceived;

    public InProcessTransmitter()
    {
        lock (Transmitters)
        {
            for (int i = 0; i < Transmitters.Length; i++)
            {
                if (Transmitters[i] == null)
                {
                    Initialize(i);
                    return;
                }
            }
            throw new InvalidOperationException("Too many transmitters.");
        }
    }

    public InProcessTransmitter(int port)
    {
        lock (Transmitters)
        {
            Initialize(port);
        }
    }

    public void Dispose()
    {
        if (!IsDisposed)
        {
            IsDisposed = true;
            CancellationTokenSource.Cancel();
            Task.Wait();
            lock (Transmitters)
            {
                Transmitters[Address.Port] = null;
            }
            SendQueue?.Dispose();
        }
    }

    public void SendPacket(Packet packet)
    {
        CheckNotDisposed();
        SendQueue.Add(packet);
    }

    public static IPEndPoint GetAddress(int port)
    {
        return new IPEndPoint(0L, port);
    }

    private void Initialize(int port)
    {
        if (port < 0 || port >= Transmitters.Length || Transmitters[port] != null)
        {
            throw new InvalidOperationException($"Port {port} unavailable.");
        }
        Address = GetAddress(port);
        Transmitters[port] = this;
        Task = new Task(TaskFunction, TaskCreationOptions.LongRunning);
        Task.Start();
    }

    private void TaskFunction()
    {
        Thread.CurrentThread.Name = "InProcessTransmitter";
        CancellationToken token = CancellationTokenSource.Token;
        while (!IsDisposed)
        {
            try
            {
                if (!SendQueue.TryTake(out var packet, -1, token))
                {
                    continue;
                }
                if (object.Equals(packet.Address.Address, UdpTransmitter.IPV4BroadcastAddress))
                {
                    InProcessTransmitter[] transmitters = Transmitters;
                    foreach (InProcessTransmitter inProcessTransmitter in transmitters)
                    {
                        if (inProcessTransmitter != null && inProcessTransmitter != this)
                        {
                            inProcessTransmitter.InvokePacketReceived(Address, packet.Bytes);
                        }
                    }
                }
                else
                {
                    Transmitters[packet.Address.Port]?.InvokePacketReceived(Address, packet.Bytes);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void CheckNotDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException("InProcessTransmitter");
        }
    }

    private void InvokePacketReceived(IPEndPoint address, byte[] bytes)
    {
        this.PacketReceived?.Invoke(new Packet(address, bytes));
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
