using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Comms;

public class UdpTransmitter : ITransmitter, IDisposable
{
    private volatile bool IsDisposed;

    private Task Task;

    private Socket Socket4;

    private Socket Socket6;


    public static IPAddress IPV4BroadcastAddress { get; } = IPAddress.Broadcast;

    public static IPAddress IPV6BroadcastAddress { get; } = IPAddress.Parse("ff08::1");

    public int MaxPacketSize { get; set; } = 1024;

    public IPEndPoint Address { get; private set; }

    public event Action<Exception> Error;

    public event Action<string> Debug;

    public event Action<Packet> PacketReceived;

    public UdpTransmitter(int localPort = 0)
    {
        try
        {
            Socket4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            Socket4.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            Socket4.Bind(new IPEndPoint(IPAddress.Any, localPort));
            Socket4.ReceiveTimeout = 1000;
            if (Address == null)
            {
                try
                {
                    using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    socket.Connect("8.8.8.8", 12345);
                    Address = new IPEndPoint(((IPEndPoint)socket.LocalEndPoint).Address, ((IPEndPoint)Socket4.LocalEndPoint).Port);
                }
                catch (Exception)
                {
                    Address = new IPEndPoint(IPAddress.None, 0);
                }
            }
        }
        catch (Exception ex2)
        {
            Socket4?.Dispose();
            Socket4 = null;
            if (ex2 is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } ex3)
            {
                throw ex3;
            }
        }
        try
        {
            Socket6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            Socket6.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            Socket6.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
            Socket6.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(IPV6BroadcastAddress));
            Socket6.Bind(new IPEndPoint(IPAddress.IPv6Any, localPort));
            Socket6.ReceiveTimeout = 1000;
            if (Address == null)
            {
                try
                {
                    using Socket socket2 = new(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                    socket2.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
                    socket2.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                    socket2.Connect("2001:4860:4860::8888", 12345);
                    Address = new IPEndPoint(((IPEndPoint)socket2.LocalEndPoint).Address, ((IPEndPoint)Socket6.LocalEndPoint).Port);
                }
                catch (Exception)
                {
                    Address = new IPEndPoint(IPAddress.IPv6None, 0);
                }
            }
        }
        catch (Exception ex5)
        {
            Socket6?.Dispose();
            Socket6 = null;
            if (ex5 is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } ex6)
            {
                throw ex6;
            }
        }
        if (Socket4 == null && Socket6 == null)
        {
            throw new InvalidOperationException("No network connectivity.");
        }
        Task = new Task(TaskFunction, TaskCreationOptions.LongRunning);
        Task.Start();
    }

    public void Dispose()
    {
        if (!IsDisposed)
        {
            IsDisposed = true;
            Task.Wait();
            Socket4?.Dispose();
            Socket6?.Dispose();
        }
    }

    public void SendPacket(Packet packet)
    {
        CheckNotDisposed();
        if (packet.Address.AddressFamily == AddressFamily.InterNetwork && Socket4 != null)
        {
            Socket4.SendTo(packet.Bytes, packet.Address);
        }
        else if (packet.Address.AddressFamily == AddressFamily.InterNetworkV6 && Socket6 != null)
        {
            Socket6.SendTo(packet.Bytes, packet.Address);
        }
    }

    private void TaskFunction()
    {
        Thread.CurrentThread.Name = "UdpTransmitter";
        List<Socket> list = new();
        byte[] array = new byte[65536];
        while (!IsDisposed)
        {
            try
            {
                list.Clear();
                if (Socket4 != null)
                {
                    list.Add(Socket4);
                }
                if (Socket6 != null)
                {
                    list.Add(Socket6);
                }
                Socket.Select(list, null, null, 1000000);
                foreach (Socket item in list)
                {
                    EndPoint endPoint = ((item.AddressFamily != AddressFamily.InterNetwork) ? new IPEndPoint(IPAddress.IPv6Any, 0) : new IPEndPoint(IPAddress.Any, 0));
                    int num = item.ReceiveFrom(array, ref endPoint);
                    byte[] array2 = new byte[num];
                    Array.Copy(array, 0, array2, 0, num);
                    InvokePacketReceived((IPEndPoint)endPoint, array2);
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode != SocketError.TimedOut && ex.SocketErrorCode != SocketError.Interrupted && ex.SocketErrorCode != SocketError.ConnectionReset)
                {
                    InvokeError(ex);
                }
            }
        }
    }

    private void CheckNotDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException("UdpTransmitter");
        }
    }

    private void InvokePacketReceived(IPEndPoint address, byte[] bytes)
    {
        this.PacketReceived?.Invoke(new Packet(address, bytes));
    }

    private void InvokeError(Exception error)
    {
        this.Error?.Invoke(error);
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
