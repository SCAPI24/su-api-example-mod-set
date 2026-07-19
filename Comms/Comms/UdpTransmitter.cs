using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
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

    // Source: System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces
    // Broadcast discovery on every IPv4 subnet. This includes virtual LAN adapters such as
    // ZeroTier instead of relying only on the physical network's 255.255.255.255 route.
    public static IReadOnlyList<IPAddress> GetIPv4DiscoveryAddresses()
    {
        var result = new List<IPAddress> { IPV4BroadcastAddress };
        try
        {
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up) continue;
                foreach (UnicastIPAddressInformation address in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] ip = address.Address.GetAddressBytes();
                    byte[] mask = GetIPv4MaskBytes(address);
                    if (mask == null) continue;
                    byte[] broadcast = new byte[4];
                    for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | (byte)~mask[i]);
                    IPAddress endpoint = new IPAddress(broadcast);
                    if (!result.Contains(endpoint)) result.Add(endpoint);
                }
            }
        }
        catch
        {
        }
        return result;
    }

    // Source: System.Net.NetworkInformation.UnicastIPAddressInformation.PrefixLength
    // Android VPN adapters can omit IPv4Mask while still exposing PrefixLength.
    private static byte[] GetIPv4MaskBytes(UnicastIPAddressInformation address)
    {
        if (address.IPv4Mask != null) return address.IPv4Mask.GetAddressBytes();
        int prefixLength;
        try
        {
            prefixLength = address.PrefixLength;
        }
        catch
        {
            return null;
        }
        if (prefixLength <= 0 || prefixLength > 32) return null;
        byte[] mask = new byte[4];
        for (int i = 0; i < mask.Length; i++)
        {
            int bits = Math.Min(Math.Max(prefixLength - i * 8, 0), 8);
            mask[i] = bits == 0 ? (byte)0 : (byte)(0xff << (8 - bits));
        }
        return mask;
    }

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
                    Address = new IPEndPoint(GetPreferredIPv4Address(), ((IPEndPoint)Socket4.LocalEndPoint).Port);
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

    // Source: System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces
    // Android exposes a VpnService adapter as tun/ppp and does not always include the provider
    // name in Description. Prefer an explicit ZeroTier adapter, then a private tunnel adapter,
    // before falling back to the default route used for ordinary LAN traffic.
    public static IPAddress GetPreferredIPv4Address()
    {
        IPAddress defaultAddress = GetDefaultIPv4Address();
        IPAddress tunnelFallback = null;
        IPAddress fallback = null;
        try
        {
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                string name = (networkInterface.Name ?? string.Empty).ToLowerInvariant();
                string description = (networkInterface.Description ?? string.Empty).ToLowerInvariant();
                string adapter = name + " " + description;
                bool isZeroTier = adapter.Contains("zerotier") ||
                    name.StartsWith("zt", StringComparison.Ordinal);
                bool isTunnel = networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                    name.StartsWith("tun", StringComparison.Ordinal) ||
                    name.StartsWith("tap", StringComparison.Ordinal);
                foreach (UnicastIPAddressInformation address in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
                        !IsPrivateIPv4(address.Address))
                        continue;
                    if (isZeroTier)
                        return address.Address;
                    if (isTunnel && tunnelFallback == null)
                        tunnelFallback = address.Address;
                    if (fallback == null && !adapter.Contains("hyper-v") &&
                        !adapter.Contains("wsl") && !adapter.Contains("virtualbox") &&
                        !adapter.Contains("vmware"))
                        fallback = address.Address;
                }
            }
        }
        catch
        {
        }

        if (tunnelFallback != null) return tunnelFallback;
        if (defaultAddress != null && IsPrivateIPv4(defaultAddress)) return defaultAddress;
        if (fallback != null) return fallback;
        return defaultAddress ?? IPAddress.Any;
    }

    private static IPAddress GetDefaultIPv4Address()
    {
        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.Connect("8.8.8.8", 12345);
            return ((IPEndPoint)socket.LocalEndPoint).Address;
        }
        catch
        {
            return null;
        }
    }

    // Source: System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces
    public static bool IsLocalIPv4Address(IPAddress address)
    {
        if (address == null || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        if (IPAddress.IsLoopback(address)) return true;
        try
        {
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation localAddress in
                    networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (address.Equals(localAddress.Address)) return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool IsPrivateIPv4(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
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
