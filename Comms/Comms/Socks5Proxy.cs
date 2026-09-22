using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Comms;

// Source: RFC 1928 (SOCKS Protocol Version 5), RFC 1929 (username/password - not used)
// 客户端侧的 SOCKS5 支持。目标场景：玩家本机跑着 Clash / v2ray 一类代理（默认 127.0.0.1:7890），
// 游戏不想（也不能）装 TUN 驱动时，让 Comms 自己把两条通道都送进代理：
//   · TCP 可靠流  → SOCKS5 CONNECT（ATYP=3 域名，交给代理解析 DNS，绕开本机 fake-ip 抢答）
//   · UDP 数据报  → SOCKS5 UDP ASSOCIATE（每条报文前面加 10 字节头）
// 认证只做"无认证"（0x00）：本机代理不需要账号密码，需要认证的代理由用户自己在代理侧配置。
public static class Socks5
{
    public const int DesiredMaxPacketSize = 1190;   // 1200 - 10 字节 SOCKS UDP 头，仍不超过 IPv6 最小 MTU
    public const int HandshakeTimeoutMilliseconds = 5000;
    public const int ProbeTimeoutMilliseconds = 800;

    public const byte ReplySucceeded = 0x00;

    private static readonly string[] ReplyText =
    {
        "succeeded",
        "general SOCKS server failure",
        "connection not allowed by ruleset",
        "network unreachable",
        "host unreachable",
        "connection refused",
        "TTL expired",
        "command not supported",
        "address type not supported"
    };

    public static string DescribeReply(byte reply) =>
        reply < ReplyText.Length ? ReplyText[reply] : "unknown SOCKS5 reply " + reply;

    // Source: Comms/Comms/Socks5Proxy.cs:Socks5.ConnectSocket
    // BeginConnect + 等待，避免代理端口被防火墙丢包时卡死拨号线程。
    public static bool ConnectSocket(Socket socket, IPEndPoint target, int timeoutMilliseconds,
        out string error)
    {
        error = null;
        try
        {
            IAsyncResult result = socket.BeginConnect(target, null, null);
            if (!result.AsyncWaitHandle.WaitOne(timeoutMilliseconds))
            {
                error = "connect to " + target + " timed out";
                return false;
            }
            socket.EndConnect(result);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // Source: RFC 1928 §3 - 方法协商。只声明"无认证"。
    public static bool TryNegotiate(Socket socket, out string error)
    {
        error = null;
        try
        {
            socket.Send(new byte[] { 0x05, 0x01, 0x00 });
            byte[] reply = ReadExactly(socket, 2);
            if (reply == null || reply[0] != 0x05)
            {
                error = "not a SOCKS5 server";
                return false;
            }
            if (reply[1] != 0x00)
            {
                error = reply[1] == 0xff
                    ? "SOCKS5 server requires authentication (only no-auth is supported)"
                    : "SOCKS5 server selected unsupported method 0x" +
                      reply[1].ToString("x2", CultureInfo.InvariantCulture);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // Source: RFC 1928 §4 - CONNECT。host 非空时用 ATYP=3 交给代理解析（避免本机 DNS 被抢答）。
    public static bool TryConnect(Socket socket, string host, IPEndPoint target, out string error)
    {
        error = null;
        if (!TryNegotiate(socket, out error))
            return false;
        if (!TryWriteRequest(socket, 0x01, host, target, out error))
            return false;
        return TryReadReply(socket, out _, out _, out error);
    }

    // Source: RFC 1928 §6 - UDP ASSOCIATE。返回中继端点；之后每条报文都要带 10 字节头。
    public static bool TryAssociateUdp(Socket socket, IPEndPoint proxy, out IPEndPoint relay,
        out string error)
    {
        relay = null;
        if (!TryNegotiate(socket, out error))
            return false;
        if (!TryWriteRequest(socket, 0x03, null, new IPEndPoint(IPAddress.Any, 0), out error))
            return false;
        if (!TryReadReply(socket, out IPAddress boundAddress, out int boundPort, out error))
            return false;
        if (boundPort == 0)
        {
            error = "SOCKS5 server returned no UDP relay port";
            return false;
        }
        if (boundAddress == null || boundAddress.Equals(IPAddress.Any) ||
            boundAddress.Equals(IPAddress.IPv6Any))
        {
            // 0.0.0.0 表示"用你连我的那个地址"，即代理自身的地址。
            boundAddress = proxy.Address;
        }
        relay = new IPEndPoint(boundAddress, boundPort);
        return true;
    }

    private static bool TryWriteRequest(Socket socket, byte command, string host,
        IPEndPoint target, out string error)
    {
        error = null;
        try
        {
            var request = new List<byte>(32) { 0x05, command, 0x00 };
            if (!string.IsNullOrEmpty(host))
            {
                byte[] name = Encoding.ASCII.GetBytes(host);
                if (name.Length == 0 || name.Length > 255)
                {
                    error = "invalid host name '" + host + "'";
                    return false;
                }
                request.Add(0x03);
                request.Add((byte)name.Length);
                request.AddRange(name);
            }
            else if (target.AddressFamily == AddressFamily.InterNetworkV6)
            {
                request.Add(0x04);
                request.AddRange(target.Address.GetAddressBytes());
            }
            else
            {
                request.Add(0x01);
                request.AddRange(target.Address.GetAddressBytes());
            }
            request.Add((byte)(target.Port >> 8));
            request.Add((byte)(target.Port & 0xff));
            socket.Send(request.ToArray());
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryReadReply(Socket socket, out IPAddress boundAddress, out int boundPort,
        out string error)
    {
        boundAddress = null;
        boundPort = 0;
        error = null;
        try
        {
            byte[] head = ReadExactly(socket, 4);
            if (head == null || head[0] != 0x05)
            {
                error = "malformed SOCKS5 reply";
                return false;
            }
            if (head[1] != ReplySucceeded)
            {
                error = DescribeReply(head[1]);
                return false;
            }
            byte[] address;
            switch (head[3])
            {
                case 0x01:
                    address = ReadExactly(socket, 4);
                    boundAddress = address == null ? null : new IPAddress(address);
                    break;
                case 0x04:
                    address = ReadExactly(socket, 16);
                    boundAddress = address == null ? null : new IPAddress(address);
                    break;
                case 0x03:
                    byte[] length = ReadExactly(socket, 1);
                    if (length == null)
                    {
                        error = "malformed SOCKS5 reply address";
                        return false;
                    }
                    address = ReadExactly(socket, length[0]);
                    if (address == null)
                    {
                        error = "malformed SOCKS5 reply address";
                        return false;
                    }
                    if (!IPAddress.TryParse(Encoding.ASCII.GetString(address), out boundAddress))
                        boundAddress = null;
                    break;
                default:
                    error = "unsupported SOCKS5 address type " + head[3];
                    return false;
            }
            byte[] port = ReadExactly(socket, 2);
            if (port == null)
            {
                error = "malformed SOCKS5 reply port";
                return false;
            }
            boundPort = (port[0] << 8) | port[1];
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static byte[] ReadExactly(Socket socket, int count)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int received = socket.Receive(buffer, read, count - read, SocketFlags.None);
            if (received <= 0)
                return null;
            read += received;
        }
        return buffer;
    }

    // Source: RFC 1928 §7 - 报文头：RSV(2) FRAG(1) ATYP(1) DST.ADDR DST.PORT
    public static byte[] EncodeDatagram(byte[] payload, string host, IPEndPoint target)
    {
        var buffer = new List<byte>(payload.Length + 22) { 0x00, 0x00, 0x00 };
        byte[] name = string.IsNullOrEmpty(host) ? null : Encoding.ASCII.GetBytes(host);
        if (name != null && name.Length > 0 && name.Length <= 255)
        {
            buffer.Add(0x03);
            buffer.Add((byte)name.Length);
            buffer.AddRange(name);
        }
        else if (target.AddressFamily == AddressFamily.InterNetworkV6)
        {
            buffer.Add(0x04);
            buffer.AddRange(target.Address.GetAddressBytes());
        }
        else
        {
            buffer.Add(0x01);
            buffer.AddRange(target.Address.GetAddressBytes());
        }
        buffer.Add((byte)(target.Port >> 8));
        buffer.Add((byte)(target.Port & 0xff));
        buffer.AddRange(payload);
        return buffer.ToArray();
    }

    public static bool TryDecodeDatagram(byte[] buffer, int length, out IPEndPoint source,
        out byte[] payload)
    {
        source = null;
        payload = null;
        if (buffer == null || length < 10 || buffer[0] != 0x00 || buffer[1] != 0x00 ||
            buffer[2] != 0x00)
        {
            return false;   // 分片（FRAG != 0）与非法头一律丢弃
        }
        int offset = 3;
        switch (buffer[offset++])
        {
            case 0x01:
                if (length < offset + 4 + 2)
                    return false;
                source = new IPEndPoint(new IPAddress(new[]
                {
                    buffer[offset], buffer[offset + 1], buffer[offset + 2], buffer[offset + 3]
                }), 0);
                offset += 4;
                break;
            case 0x04:
                if (length < offset + 16 + 2)
                    return false;
                byte[] v6 = new byte[16];
                Array.Copy(buffer, offset, v6, 0, 16);
                source = new IPEndPoint(new IPAddress(v6), 0);
                offset += 16;
                break;
            case 0x03:
                if (length < offset + 1)
                    return false;
                int nameLength = buffer[offset++];
                if (length < offset + nameLength + 2)
                    return false;
                string name = Encoding.ASCII.GetString(buffer, offset, nameLength);
                offset += nameLength;
                source = IPAddress.TryParse(name, out IPAddress parsed)
                    ? new IPEndPoint(parsed, 0)
                    : new IPEndPoint(IPAddress.None, 0);
                break;
            default:
                return false;
        }
        int port = (buffer[offset] << 8) | buffer[offset + 1];
        offset += 2;
        source.Port = port;
        payload = new byte[length - offset];
        Array.Copy(buffer, offset, payload, 0, payload.Length);
        return true;
    }
}

public enum Socks5ProxyMode
{
    Off,
    On,
    Auto
}

// 客户端进程级的代理设置。配置面很小：模式 + 地址 + 端口（无认证）。
public sealed class Socks5ProxySettings
{
    public Socks5ProxyMode Mode = Socks5ProxyMode.Auto;
    public string Host = "127.0.0.1";
    public int Port = 7890;

    public bool IsEnabled => Mode != Socks5ProxyMode.Off;

    public static Socks5ProxySettings Default() => new Socks5ProxySettings();

    // Source: Mod/ScMultiplayer/ScMultiplayer.proxy.txt
    // 纯 key=value，`#`/`;`/`//` 起注释；未知键忽略，坏值退回默认，保证配置文件手改不会导致连不上。
    public static Socks5ProxySettings Parse(string text)
    {
        var settings = Default();
        if (string.IsNullOrWhiteSpace(text))
            return settings;
        foreach (string sourceLine in text.Split(new[] { "\r\n", "\n", "\r" },
            StringSplitOptions.None))
        {
            string line = sourceLine.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") ||
                line.StartsWith("//"))
            {
                continue;
            }
            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            string key = line.Substring(0, separator).Trim().ToLowerInvariant();
            string value = line.Substring(separator + 1).Trim().Trim('"');
            switch (key)
            {
                case "enabled":
                case "mode":
                    switch (value.ToLowerInvariant())
                    {
                        case "off":
                        case "false":
                        case "0":
                        case "direct":
                            settings.Mode = Socks5ProxyMode.Off;
                            break;
                        case "on":
                        case "true":
                        case "1":
                        case "always":
                            settings.Mode = Socks5ProxyMode.On;
                            break;
                        default:
                            settings.Mode = Socks5ProxyMode.Auto;
                            break;
                    }
                    break;
                case "host":
                case "server":
                    if (value.Length > 0)
                        settings.Host = value;
                    break;
                case "port":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int port) && port > 0 && port <= 65535)
                    {
                        settings.Port = port;
                    }
                    break;
            }
        }
        return settings;
    }

    public string Format() =>
        "# ScMultiplayer 客户端 SOCKS5 代理设置（只影响本机客户端，主机端不需要改）\r\n" +
        "# enabled: auto = 检测到代理就用（默认，代理开关随时切换都行）；on = 必须走代理；" +
        "off = 只走直连\r\n" +
        "# 认证只支持「无认证」（0x00）：需要账号密码的代理请在代理侧放开本机回环地址。\r\n" +
        "enabled=" + (Mode == Socks5ProxyMode.Off ? "off" :
            Mode == Socks5ProxyMode.On ? "on" : "auto") + "\r\n" +
        "host=" + Host + "\r\n" +
        "port=" + Port.ToString(CultureInfo.InvariantCulture) + "\r\n";

    public IPEndPoint ToEndPoint() =>
        new IPEndPoint(ResolveProxyAddress(Host), Port);

    private static IPAddress ResolveProxyAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return IPAddress.Loopback;
        if (IPAddress.TryParse(host, out IPAddress parsed))
            return parsed;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        try
        {
            IPAddress[] addresses = Dns.GetHostAddresses(host);
            foreach (IPAddress address in addresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                    return address;
            }
            if (addresses.Length > 0)
                return addresses[0];
        }
        catch (Exception)
        {
        }
        return IPAddress.Loopback;
    }
}

// Source: Mod/ScMultiplayer/Networking/RemoteServerDirectory.cs:ResolveDirectoryEntries
// 本机 DNS 可能被代理的 fake-ip 抢答（域名解析到 198.18.x）。把"域名 → 解析出来的地址"
// 记在这里，SOCKS 请求就能改用 ATYP=3 把**域名**交给代理自己解析，fake-ip 完全绕开。
public static class Socks5RouteTable
{
    private const int MaximumEntries = 512;

    private static readonly object Lock = new object();
    private static readonly Dictionary<IPAddress, string> Hosts = new();

    public static void Register(IPAddress address, string host)
    {
        if (address == null || string.IsNullOrWhiteSpace(host))
            return;
        lock (Lock)
        {
            if (Hosts.Count >= MaximumEntries && !Hosts.ContainsKey(address))
                Hosts.Clear();
            Hosts[address] = host.Trim();
        }
    }

    public static bool TryGetHost(IPEndPoint target, out string host)
    {
        host = null;
        if (target == null)
            return false;
        lock (Lock)
        {
            return Hosts.TryGetValue(target.Address, out host) && !string.IsNullOrEmpty(host);
        }
    }

    public static void Clear()
    {
        lock (Lock)
            Hosts.Clear();
    }
}

// 代理可用性：Auto 模式下每 3 秒做一次真正的 SOCKS5 握手探测。
// 探到就切到代理、探不到就退回直连 —— 这就是"游戏途中开代理 / 关代理"都能连上的关键。
public static class Socks5ProxyState
{
    private static readonly object Lock = new object();
    private static Socks5ProxySettings s_settings = Socks5ProxySettings.Default();
    private static bool s_available;
    private static bool s_probed;
    private static Thread s_watcher;
    private static volatile bool s_running;
    private static int s_probeGeneration;

    public static Action<string> Log;

    public static event Action<bool> AvailabilityChanged;

    public static Socks5ProxySettings Settings
    {
        get
        {
            lock (Lock)
                return s_settings;
        }
    }

    // 当前是否应该把流量送进代理（On 模式不再探测，探测失败也只记日志）。
    public static bool IsAvailable
    {
        get
        {
            lock (Lock)
                return s_settings.IsEnabled && s_available;
        }
    }

    public static bool IsProbed
    {
        get
        {
            lock (Lock)
                return s_probed;
        }
    }

    // 实际选路依据：off 永不代理；on 一律尝试代理（失败由调用方报错，不偷偷直连）；
    // auto（默认）只在探测到代理时才走代理 —— 这就是"游戏途中开/关代理"都能连上的依据。
    public static bool ShouldRouteThroughProxy
    {
        get
        {
            Socks5ProxySettings settings = Settings;
            if (!settings.IsEnabled)
                return false;
            return settings.Mode == Socks5ProxyMode.On || IsAvailable;
        }
    }

    // Source: Comms/Comms/TunProxyDetector.cs
    // 按**目的地**决定选路（"混合代理 + 虚拟网卡"和"只开代理不开网卡"两种用法都要对）：
    //   · on   → 一律代理（私有地址由调用方另行排除）；
    //   · auto → 有 TUN 代理网卡时，普通公网地址交给 TUN 自己带（只过一次代理）；
    //            fake-ip 地址必须走 SOCKS+域名兜底；没有 TUN 时公网地址只能靠 SOCKS。
    public static bool ShouldRouteThroughProxyFor(IPEndPoint target)
    {
        Socks5ProxySettings settings = Settings;
        if (!settings.IsEnabled || target == null)
            return false;
        if (settings.Mode == Socks5ProxyMode.On)
            return true;
        if (!IsAvailable)
            return false;
        if (TunProxyDetector.IsActive && !IsFakeIpAddress(target.Address))
            return false;
        return true;
    }

    // fake-ip 段（Clash/mihomo 默认 198.18.0.0/15）：本机 DNS 被抢答的标志。
    // 这种地址直连必然失败，只有"交给代理用域名解析"或"交给 TUN"才有意义。
    public static bool IsFakeIpAddress(IPAddress address)
    {
        if (address == null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
    }

    public static void Configure(Socks5ProxySettings settings)
    {
        if (settings == null || !settings.IsEnabled)
        {
            bool disabled;
            lock (Lock)
            {
                s_settings = settings ?? Socks5ProxySettings.Default();
                disabled = SetAvailableLocked(false);
                s_probed = true;
            }
            if (disabled)
                AnnounceAvailable(false);
            LogMessage("[ScMP] SOCKS5 proxy disabled, using direct connections");
            return;
        }
        bool changed;
        lock (Lock)
        {
            changed = s_settings.Mode != settings.Mode ||
                !string.Equals(s_settings.Host, settings.Host, StringComparison.OrdinalIgnoreCase) ||
                s_settings.Port != settings.Port;
            s_settings = settings;
            if (changed)
            {
                s_available = false;
                s_probed = false;
            }
        }
        LogMessage("[ScMP] SOCKS5 proxy configured: " + settings.Host + ":" + settings.Port +
            " (" + settings.Mode.ToString().ToLowerInvariant() + ")");
        LogMessage("[ScMP] TUN proxy adapter: " + (TunProxyDetector.IsActive
            ? TunProxyDetector.ActiveAdapterName
            : "none - SOCKS carries the public addresses"));
        Start();
        RequestProbe();
    }

    public static void Start()
    {
        if (s_running)
            return;
        s_running = true;
        s_watcher = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "Socks5ProxyState"
        };
        s_watcher.Start();
    }

    public static void Stop()
    {
        s_running = false;
        s_watcher = null;
    }

    public static void RequestProbe() => Interlocked.Increment(ref s_probeGeneration);

    private static void WatchLoop()
    {
        while (s_running)
        {
            int generation = Volatile.Read(ref s_probeGeneration);
            Socks5ProxySettings settings = Settings;
            if (settings.IsEnabled)
            {
                bool available = Probe(settings, out string error);
                bool changed;
                lock (Lock)
                {
                    s_probed = true;
                    changed = SetAvailableLocked(available);
                }
                if (changed)
                    AnnounceAvailable(available);
                if (!available && settings.Mode == Socks5ProxyMode.On &&
                    Volatile.Read(ref s_probeGeneration) == generation)
                {
                    LogMessage("[ScMP] SOCKS5 proxy " + settings.Host + ":" + settings.Port +
                        " is not answering: " + (error ?? "unknown error"));
                }
            }
            for (int i = 0; i < 30 && s_running; i++)
            {
                if (Volatile.Read(ref s_probeGeneration) != generation)
                    break;
                Thread.Sleep(100);
            }
        }
    }

    // 真正的 SOCKS5 握手，而不是"端口开着"：7890 上放别的服务时不会误判。
    private static bool Probe(Socks5ProxySettings settings, out string error)
    {
        error = null;
        IPEndPoint proxy = settings.ToEndPoint();
        Socket socket = null;
        try
        {
            socket = new Socket(proxy.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = Socks5.ProbeTimeoutMilliseconds
            };
            if (!Socks5.ConnectSocket(socket, proxy, Socks5.ProbeTimeoutMilliseconds,
                out error) || !Socks5.TryNegotiate(socket, out error))
            {
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                socket?.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    private static bool SetAvailableLocked(bool value)
    {
        if (s_available == value)
            return false;
        s_available = value;
        return true;
    }

    // 事件与日志一律在锁外触发，避免观察者回调里再读 Socks5ProxyState。
    private static void AnnounceAvailable(bool value)
    {
        LogMessage(value
            ? "[ScMP] SOCKS5 proxy is available, datagram + stream go through it"
            : "[ScMP] SOCKS5 proxy is gone, falling back to direct connections");
        Action<bool> handler = AvailabilityChanged;
        if (handler == null)
            return;
        try
        {
            handler(value);
        }
        catch (Exception)
        {
        }
    }

    private static void LogMessage(string message)
    {
        Report(message);
    }

    // 供 Comms 内部（TcpTransmitter / ProxyDatagramTransmitter）上报"这次走了代理"这类信息：
    // Comms 的 Debug 事件在 Release 下被 [Conditional("DEBUG")] 编译掉，诊断必须走这条路，
    // 由宿主（ScMP）接到 Engine.Log 上。
    public static void Report(string message)
    {
        try
        {
            Log?.Invoke(message);
        }
        catch (Exception)
        {
        }
    }
}
