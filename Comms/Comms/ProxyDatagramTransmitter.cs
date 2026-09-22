using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Comms;

// Source: Mod/Comms/Comms/Socks5Proxy.cs:Socks5ProxyState
// 数据报侧的双通道封装：代理可用时经 SOCKS5 UDP ASSOCIATE 中继，否则走本机直连 UDP。
//
// 为什么可以在游戏途中随时开/关代理：对方（主机）用我们已经实现的"按实际观测到的 UDP 源地址
// 修正/建立 peer"（token + MoveConnection）把连接重新绑定到新源地址上。所以切换通道不需要
// 任何协议变更 —— 只是短时间内会丢几个包，由上层重传补上。
//
// 身份（Address）始终报**本机直连 UDP 端点**：无论实际从哪条路出去，对端看到的都是观测地址，
// 由对端的修正逻辑落库；这样同一条连接不会出现两个"我们"。
public sealed class ProxyDatagramTransmitter : IWrapperTransmitter, IDisposable
{
    private readonly UdpTransmitter m_direct;
    private readonly Socks5ProxySettings m_settings;
    private readonly object m_sessionLock = new object();
    private Socks5UdpSession m_session;
    private bool m_disposed;
    private long m_relayPackets;
    private long m_directPackets;

    public ProxyDatagramTransmitter(UdpTransmitter direct, Socks5ProxySettings settings)
    {
        m_direct = direct ?? throw new ArgumentNullException(nameof(direct));
        m_settings = settings ?? Socks5ProxySettings.Default();
        m_direct.Error += InvokeError;
        m_direct.Debug += InvokeDebug;
        m_direct.PacketReceived += InvokePacketReceived;
        if (m_settings.IsEnabled)
            Socks5ProxyState.AvailabilityChanged += HandleAvailabilityChanged;
    }

    public ITransmitter BaseTransmitter => m_direct;

    public int MaxPacketSize => m_settings.IsEnabled
        ? Math.Min(m_direct.MaxPacketSize, Socks5.DesiredMaxPacketSize)
        : m_direct.MaxPacketSize;

    // 直连 UDP 依旧是"不可靠数据报"语义，即使中间隔着代理中继。
    public bool IsReliableStream => false;

    public IPEndPoint Address => m_direct.Address;

    public event Action<Exception> Error;

    public event Action<string> Debug;

    public event Action<Packet> PacketReceived;

    public long RelayPacketCount => Interlocked.Read(ref m_relayPackets);

    public long DirectPacketCount => Interlocked.Read(ref m_directPackets);

    public void SendPacket(Packet packet)
    {
        if (packet.Bytes == null || packet.Bytes.Length == 0)
            return;
        if (ShouldUseProxy(packet.Address))
        {
            Socks5UdpSession session = EnsureSession();
            if (session != null)
            {
                Socks5RouteTable.TryGetHost(packet.Address, out string host);
                if (session.TrySend(packet.Bytes, host, packet.Address, out string error))
                {
                    Interlocked.Increment(ref m_relayPackets);
                    return;
                }
                DropSession(session);
                InvokeDebug("SOCKS5 relay send failed (" + error + "), using direct datagram path");
                Socks5ProxyState.Report("[Comms] SOCKS5 relay send failed (" + error +
                    "), datagram falls back to direct");
            }
        }
        Interlocked.Increment(ref m_directPackets);
        m_direct.SendPacket(packet);
    }

    public int GetPendingSendCount(IPEndPoint address) => 0;

    // 局域网/私有地址（以及广播、组播）保持直连：同网段走代理没有意义，还会丢掉广播发现。
    // Source: Comms/Comms/Socks5Proxy.cs:Socks5ProxyState.ShouldRouteThroughProxyFor
    // 剩下的公网地址再按"有没有 TUN / 是不是 fake-ip"决定（见 TunProxyDetector）。
    private static bool ShouldUseProxy(IPEndPoint target)
    {
        if (target == null || !Socks5ProxyState.ShouldRouteThroughProxyFor(target))
            return false;
        IPAddress address = target.Address;
        if (address == null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.Loopback) ||
            address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6None) ||
            address.IsIPv6Multicast || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            address.IsIPv6UniqueLocal || address.IsIPv6Teredo)
        {
            return false;
        }
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return true;
        byte[] bytes = address.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 || bytes[0] >= 224)
            return false;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            return false;
        if (bytes[0] == 192 && bytes[1] == 168)
            return false;
        if (bytes[0] == 169 && bytes[1] == 254)
            return false;
        return true;
    }

    private Socks5UdpSession EnsureSession()
    {
        lock (m_sessionLock)
        {
            if (m_disposed)
                return null;
            if (m_session != null && m_session.IsAlive)
                return m_session;
            if (m_session != null)
                m_session.Dispose();
            if (!Socks5ProxyState.IsAvailable)
            {
                m_session = null;
                return null;
            }
            Socks5UdpSession session = Socks5UdpSession.TryCreate(m_settings,
                InvokePacketReceived, InvokeError, InvokeDebug);
            m_session = session;
            return session;
        }
    }

    private void DropSession(Socks5UdpSession session)
    {
        lock (m_sessionLock)
        {
            if (ReferenceEquals(m_session, session))
                m_session = null;
        }
        session?.Dispose();
        // 让探测线程尽快重新判定代理状态（例如代理刚刚被关掉）。
        Socks5ProxyState.RequestProbe();
    }

    private void HandleAvailabilityChanged(bool available)
    {
        if (available)
            return;
        Socks5UdpSession session;
        lock (m_sessionLock)
        {
            session = m_session;
            m_session = null;
        }
        session?.Dispose();
    }

    public void Dispose()
    {
        if (m_disposed)
            return;
        m_disposed = true;
        if (m_settings.IsEnabled)
            Socks5ProxyState.AvailabilityChanged -= HandleAvailabilityChanged;
        Socks5UdpSession session;
        lock (m_sessionLock)
        {
            session = m_session;
            m_session = null;
        }
        session?.Dispose();
        m_direct.Dispose();
    }

    private void InvokeError(Exception error)
    {
        Action<Exception> handler = Error;
        if (handler == null)
            return;
        try
        {
            handler(error);
        }
        catch (Exception)
        {
        }
    }

    private void InvokeDebug(string message)
    {
        Action<string> handler = Debug;
        if (handler == null)
            return;
        try
        {
            handler(message);
        }
        catch (Exception)
        {
        }
    }

    private void InvokePacketReceived(Packet packet)
    {
        Action<Packet> handler = PacketReceived;
        if (handler == null)
            return;
        try
        {
            handler(packet);
        }
        catch (Exception)
        {
        }
    }
}

// Source: RFC 1928 §6/§7 - 一条 TCP 控制连接（UDP ASSOCIATE 的生命周期）+ 一个到中继的 UDP socket。
// 每条报文前面带 10 字节头（RSV·FRAG·ATYP·DST），回包同样带头，按头里的源地址还原对端地址。
internal sealed class Socks5UdpSession : IDisposable
{
    private readonly Socket m_control;
    private readonly Socket m_relay;
    private readonly IPEndPoint m_relayEndPoint;
    private readonly Action<Packet> m_onPacket;
    private readonly Action<Exception> m_onError;
    private readonly Action<string> m_onDebug;
    private readonly Thread m_thread;
    private readonly object m_sendLock = new object();
    private volatile bool m_disposed;
    private int m_failures;

    private Socks5UdpSession(Socket control, Socket relay, IPEndPoint relayEndPoint,
        Action<Packet> onPacket, Action<Exception> onError, Action<string> onDebug)
    {
        m_control = control;
        m_relay = relay;
        m_relayEndPoint = relayEndPoint;
        m_onPacket = onPacket;
        m_onError = onError;
        m_onDebug = onDebug;
        m_thread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "Socks5UdpSession"
        };
        m_thread.Start();
    }

    public bool IsAlive => !m_disposed && m_failures < 3;

    public static Socks5UdpSession TryCreate(Socks5ProxySettings settings,
        Action<Packet> onPacket, Action<Exception> onError, Action<string> onDebug)
    {
        IPEndPoint proxy = settings.ToEndPoint();
        Socket control = null;
        Socket relay = null;
        try
        {
            control = new Socket(proxy.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                ReceiveTimeout = Socks5.HandshakeTimeoutMilliseconds
            };
            if (!Socks5.ConnectSocket(control, proxy, Socks5.HandshakeTimeoutMilliseconds,
                out string error))
            {
                throw new InvalidOperationException("SOCKS5 control connect failed: " + error);
            }
            if (!Socks5.TryAssociateUdp(control, proxy, out IPEndPoint relayEndPoint,
                out error))
            {
                throw new InvalidOperationException("SOCKS5 UDP ASSOCIATE failed: " + error);
            }
            relay = new Socket(relayEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveTimeout = 1000
            };
            relay.Bind(new IPEndPoint(relayEndPoint.AddressFamily == AddressFamily.InterNetwork
                ? IPAddress.Any : IPAddress.IPv6Any, 0));
            onDebug?.Invoke("SOCKS5 UDP relay " + relayEndPoint + " via " + proxy);
            Socks5ProxyState.Report("[Comms] SOCKS5 UDP relay " + relayEndPoint + " via " + proxy);
            return new Socks5UdpSession(control, relay, relayEndPoint, onPacket, onError,
                onDebug);
        }
        catch (Exception ex)
        {
            try
            {
                control?.Dispose();
            }
            catch (Exception)
            {
            }
            try
            {
                relay?.Dispose();
            }
            catch (Exception)
            {
            }
            onDebug?.Invoke("SOCKS5 UDP session unavailable: " + ex.Message);
            Socks5ProxyState.Report("[Comms] SOCKS5 UDP relay unavailable: " + ex.Message);
            return null;
        }
    }

    public bool TrySend(byte[] payload, string host, IPEndPoint target, out string error)
    {
        error = null;
        if (m_disposed)
        {
            error = "session disposed";
            return false;
        }
        try
        {
            byte[] datagram = Socks5.EncodeDatagram(payload, host, target);
            lock (m_sendLock)
            {
                m_relay.SendTo(datagram, m_relayEndPoint);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Interlocked.Increment(ref m_failures);
            return false;
        }
    }

    private void ReceiveLoop()
    {
        byte[] buffer = new byte[65536];
        while (!m_disposed)
        {
            try
            {
                EndPoint source = m_relayEndPoint.AddressFamily == AddressFamily.InterNetwork
                    ? new IPEndPoint(IPAddress.Any, 0)
                    : new IPEndPoint(IPAddress.IPv6Any, 0);
                int received = m_relay.ReceiveFrom(buffer, ref source);
                if (received <= 0)
                    continue;
                if (!Socks5.TryDecodeDatagram(buffer, received, out IPEndPoint origin,
                    out byte[] payload) || payload.Length == 0)
                {
                    continue;
                }
                m_onPacket?.Invoke(new Packet(origin, payload));
            }
            catch (SocketException ex)
            {
                if (m_disposed)
                    break;
                if (ex.SocketErrorCode == SocketError.TimedOut ||
                    ex.SocketErrorCode == SocketError.Interrupted ||
                    ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                Interlocked.Increment(ref m_failures);
                m_onError?.Invoke(ex);
                if (m_failures >= 3)
                    break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (m_disposed)
                    break;
                Interlocked.Increment(ref m_failures);
                m_onError?.Invoke(ex);
                if (m_failures >= 3)
                    break;
            }
        }
        m_disposed = true;
    }

    public void Dispose()
    {
        if (m_disposed)
        {
            try
            {
                m_relay?.Dispose();
            }
            catch (Exception)
            {
            }
            try
            {
                m_control?.Dispose();
            }
            catch (Exception)
            {
            }
            return;
        }
        m_disposed = true;
        try
        {
            m_relay?.Dispose();
        }
        catch (Exception)
        {
        }
        try
        {
            m_control?.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
