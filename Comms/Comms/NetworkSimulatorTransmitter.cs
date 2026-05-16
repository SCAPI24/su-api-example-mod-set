using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Comms;

public class NetworkSimulatorTransmitter : IWrapperTransmitter, ITransmitter, IDisposable
{
    private volatile bool IsDisposed;

    private int SimulatedMaxPacketSize = 2147483647;

    private SortedDictionary<double, List<Action>> PendingActions = new();

    private Task Task;

    private object Lock = new();

    private int RndSeed;


    public ITransmitter BaseTransmitter { get; private set; }

    public NetworkSimulatorStats Stats { get; }

    public float MinimumDelay { get; set; }

    public float MaximumDelay { get; set; }

    public float DropRatio { get; set; }

    public float DuplicateRatio { get; set; }

    public float ByteCorruptRatio { get; set; }

    public float TruncateRatio { get; set; }

    public int MaxPacketSize
    {
        get
        {
            return Math.Max(Math.Min(SimulatedMaxPacketSize, BaseTransmitter.MaxPacketSize), 0);
        }
        set
        {
            SimulatedMaxPacketSize = value;
        }
    }

    public IPEndPoint Address => BaseTransmitter.Address;

    public event Action<Exception> Error;

    public event Action<string> Debug
    {
        add
        {
            BaseTransmitter.Debug += value;
        }
        remove
        {
            BaseTransmitter.Debug -= value;
        }
    }

    public event Action<Packet> PacketReceived;

    public NetworkSimulatorTransmitter(ITransmitter baseTransmitter)
        : this(baseTransmitter, new NetworkSimulatorStats())
    {
    }

    public NetworkSimulatorTransmitter(ITransmitter baseTransmitter, NetworkSimulatorStats stats)
    {
        RndSeed = GetHashCode();
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
                Stats.LastActivityTicks = Environment.TickCount & 0x7FFFFFFF;
            }
            if (DropRatio <= 0f || !RndBool(DropRatio))
            {
                if (Stats != null)
                {
                    Interlocked.Increment(ref Stats.PacketsReceived);
                    Interlocked.Add(ref Stats.BytesReceived, packet.Bytes.Length);
                }
                this.PacketReceived?.Invoke(packet);
            }
        };
        Task = new Task(delegate
        {
            Thread.CurrentThread.Name = "NetworkSimulatorTransmitter";
            while (!IsDisposed)
            {
                try
                {
                    ExecutePendingActions();
                    Thread.Sleep(10);
                }
                catch (Exception obj)
                {
                    this.Error?.Invoke(obj);
                }
            }
        }, TaskCreationOptions.LongRunning);
        Task.Start();
    }

    public void Dispose()
    {
        if (!IsDisposed)
        {
            IsDisposed = true;
            Task.Wait();
            BaseTransmitter.Dispose();
        }
    }

    public void SendPacket(Packet packet)
    {
        lock (Lock)
        {
            if (Stats != null)
            {
                Stats.LastActivityTicks = Environment.TickCount & 0x7FFFFFFF;
                Interlocked.Increment(ref Stats.PacketsSent);
                Interlocked.Add(ref Stats.BytesSent, packet.Bytes.Length);
            }
            if (DropRatio <= 0f || !RndBool(DropRatio))
            {
                if (TruncateRatio > 0f && packet.Bytes.Length != 0 && RndBool(TruncateRatio))
                {
                    packet.Bytes = packet.Bytes.Take(RndInt(packet.Bytes.Length)).ToArray();
                }
                if (ByteCorruptRatio > 0f)
                {
                    packet.Bytes = packet.Bytes.ToArray();
                    for (int i = 0; i < packet.Bytes.Length; i++)
                    {
                        if (RndBool(ByteCorruptRatio))
                        {
                            packet.Bytes[i] = (byte)RndInt();
                        }
                    }
                }
                if (DuplicateRatio > 0f && RndBool(DuplicateRatio))
                {
                    QueueAction(RandomizeDelay(), delegate
                    {
                        SendPacket(packet);
                    });
                }
                if (MinimumDelay > 0f || MaximumDelay > 0f)
                {
                    QueueAction(RandomizeDelay(), delegate
                    {
                        BaseTransmitter.SendPacket(packet);
                    });
                }
                else
                {
                    BaseTransmitter.SendPacket(packet);
                }
            }
            else if (Stats != null)
            {
                Interlocked.Increment(ref Stats.PacketsDropped);
            }
        }
    }

    private void QueueAction(double delay, Action action)
    {
        double time = Comm.GetTime();
        double num = Math.Round((time + delay) * 100.0) / 100.0;
        if (num <= time)
        {
            action();
            return;
        }
        lock (Lock)
        {
            if (!PendingActions.TryGetValue(num, out var list))
            {
                list = new List<Action>();
                PendingActions.Add(num, list);
            }
            list.Add(action);
        }
    }

    private void ExecutePendingActions()
    {
        lock (Lock)
        {
            double time = Comm.GetTime();
            while (PendingActions.Count > 0)
            {
                KeyValuePair<double, List<Action>> keyValuePair = PendingActions.First();
                if (keyValuePair.Key > time)
                {
                    break;
                }
                PendingActions.Remove(keyValuePair.Key);
                foreach (Action item in keyValuePair.Value)
                {
                    item();
                }
            }
        }
    }

    private double RandomizeDelay()
    {
        float minimumDelay = MinimumDelay;
        float num = Math.Max(MaximumDelay, MinimumDelay);
        return (double)RndInt() / 2147483648.0 * (double)(num - minimumDelay) + (double)minimumDelay;
    }

    private int RndInt()
    {
        Interlocked.Increment(ref RndSeed);
        return (int)(Hash((uint)RndSeed) & 0x7FFFFFFF);
    }

    private int RndInt(int bound)
    {
        return RndInt() % bound;
    }

    private bool RndBool(double probability)
    {
        return (double)RndInt() < probability * 2147483648.0;
    }

    private static uint Hash(uint key)
    {
        key ^= key >> 16;
        key *= 2146121005;
        key ^= key >> 15;
        key *= 2221713035u;
        key ^= key >> 16;
        return key;
    }
}
