using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SuMultiplayer.Engine;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 最小 SOCKS5 客户端（RFC 1928，无认证）：TCP CONNECT 与 UDP ASSOCIATE。
    /// 只依赖 BCL；从旧 Comms/Socks5Proxy.cs 的协议层精简移植。
    /// </summary>
    public static class Socks5Proxy
    {
        public const int DesiredMaxPacketSize = 1190;
        private const int HandshakeTimeoutMs = 5000;
        private const int UdpProbeTimeoutMs = 1200;

        public static bool IsUsableHost(string host) => !string.IsNullOrWhiteSpace(host);

        public static Socket Connect(string targetHost, int targetPort, int timeoutMs,
            out string error)
        {
            error = null;
            if (!TryResolveProxy(out IPEndPoint proxy, out error)) return null;
            Socket socket = null;
            try
            {
                socket = new Socket(proxy.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                    SendTimeout = timeoutMs,
                    ReceiveTimeout = timeoutMs
                };
                if (!ConnectSocket(socket, proxy, timeoutMs, out error)) return Fail(socket, out error);
                if (!TryNegotiate(socket, out error)) return Fail(socket, out error);
                if (!TryWriteRequest(socket, 0x01, targetHost, targetPort, out error))
                    return Fail(socket, out error);
                if (!TryReadReply(socket, out _, out _, out error))
                    return Fail(socket, out error);
                return socket;
            }
            catch (Exception ex)
            {
                error = "SOCKS5 连接异常：" + ex.GetType().Name + " " + ex.Message;
                try { socket?.Close(); } catch (Exception) { }
                return null;
            }
        }

        /// <summary>
        /// 通过 SOCKS5 UDP ASSOCIATE 向目标发送一个数据报并等待一个回包（用于公共 Beacon 探测）。
        /// 每次探测建一条短命控制连接；目录扫描周期低频，代价可接受。
        /// </summary>
        public static bool TryUdpProbe(string targetHost, int targetPort, byte[] payload,
            int timeoutMs, out byte[] replyPayload, out string error)
        {
            replyPayload = null;
            error = null;
            if (!TryResolveProxy(out IPEndPoint proxy, out error)) return false;
            Socket control = null;
            Socket relay = null;
            try
            {
                control = new Socket(proxy.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                    SendTimeout = timeoutMs,
                    ReceiveTimeout = timeoutMs
                };
                if (!ConnectSocket(control, proxy, timeoutMs, out error)) return false;
                if (!TryNegotiate(control, out error)) return false;
                if (!TryWriteRequest(control, 0x03, null, 0, out error)) return false;
                if (!TryReadReply(control, out IPAddress boundAddress, out int boundPort,
                        out error)) return false;
                if (boundPort <= 0)
                {
                    error = "SOCKS5 UDP ASSOCIATE 未返回中继端口";
                    return false;
                }
                if (boundAddress == null || boundAddress.Equals(IPAddress.Any) ||
                    boundAddress.Equals(IPAddress.IPv6Any))
                    boundAddress = proxy.Address;
                var relayEndPoint = new IPEndPoint(boundAddress, boundPort);
                relay = new Socket(relayEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
                {
                    ReceiveTimeout = Math.Max(200, timeoutMs),
                    SendTimeout = Math.Max(200, timeoutMs)
                };
                relay.Bind(new IPEndPoint(
                    relayEndPoint.AddressFamily == AddressFamily.InterNetwork
                        ? IPAddress.Any : IPAddress.IPv6Any, 0));
                byte[] datagram = EncodeDatagram(payload, targetHost, targetPort);
                relay.SendTo(datagram, relayEndPoint);
                byte[] buffer = new byte[8192];
                EndPoint remote = new IPEndPoint(
                    relayEndPoint.AddressFamily == AddressFamily.InterNetwork
                        ? IPAddress.Any : IPAddress.IPv6Any, 0);
                int received = relay.ReceiveFrom(buffer, ref remote);
                if (!TryDecodeDatagram(buffer, received, out _, out replyPayload))
                {
                    error = "SOCKS5 UDP 回包格式非法";
                    replyPayload = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "SOCKS5 UDP 探测异常：" + ex.GetType().Name + " " + ex.Message;
                return false;
            }
            finally
            {
                try { relay?.Close(); } catch (Exception) { }
                try { control?.Close(); } catch (Exception) { }
            }
        }

        private static Socket Fail(Socket socket, out string error)
        {
            try { socket?.Close(); } catch (Exception) { }
            error = "SOCKS5 握手/连接失败";
            return null;
        }

        private static bool TryResolveProxy(out IPEndPoint endpoint, out string error)
        {
            endpoint = null;
            error = null;
            string host = ProxySettings.Host;
            int port = ProxySettings.Port;
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || port > 65535)
            {
                error = "代理地址非法：" + host + ":" + port;
                return false;
            }
            try
            {
                if (IPAddress.TryParse(host, out IPAddress address))
                {
                    endpoint = new IPEndPoint(address, port);
                    return true;
                }
                IPAddress[] addresses = Dns.GetHostAddresses(host);
                if (addresses.Length == 0)
                {
                    error = "代理主机解析失败：" + host;
                    return false;
                }
                endpoint = new IPEndPoint(addresses[0], port);
                return true;
            }
            catch (Exception ex)
            {
                error = "代理主机解析失败：" + ex.Message;
                return false;
            }
        }

        private static bool ConnectSocket(Socket socket, IPEndPoint target, int timeoutMs,
            out string error)
        {
            error = null;
            try
            {
                IAsyncResult result = socket.BeginConnect(target, null, null);
                if (!result.AsyncWaitHandle.WaitOne(Math.Max(100, timeoutMs)))
                {
                    error = "连接代理超时：" + target;
                    return false;
                }
                socket.EndConnect(result);
                return true;
            }
            catch (Exception ex)
            {
                error = "连接代理失败：" + ex.Message;
                return false;
            }
        }

        private static bool TryNegotiate(Socket socket, out string error)
        {
            error = null;
            try
            {
                socket.Send(new byte[] { 0x05, 0x01, 0x00 });
                byte[] reply = ReadExactly(socket, 2);
                if (reply == null || reply[0] != 0x05)
                {
                    error = "不是 SOCKS5 服务";
                    return false;
                }
                if (reply[1] != 0x00)
                {
                    error = reply[1] == 0xff
                        ? "SOCKS5 需要认证（当前只支持无认证）"
                        : "SOCKS5 协商方法不支持：0x" + reply[1].ToString("x2");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "SOCKS5 协商异常：" + ex.Message;
                return false;
            }
        }

        private static bool TryWriteRequest(Socket socket, byte command, string host, int port,
            out string error)
        {
            error = null;
            try
            {
                var request = new List<byte>(32) { 0x05, command, 0x00 };
                if (!string.IsNullOrEmpty(host) && !IPAddress.TryParse(host, out _))
                {
                    byte[] name = Encoding.ASCII.GetBytes(host);
                    if (name.Length == 0 || name.Length > 255)
                    {
                        error = "目标域名长度非法：" + host;
                        return false;
                    }
                    request.Add(0x03);
                    request.Add((byte)name.Length);
                    request.AddRange(name);
                }
                else if (IPAddress.TryParse(host, out IPAddress targetAddress) &&
                    targetAddress.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    request.Add(0x04);
                    request.AddRange(targetAddress.GetAddressBytes());
                }
                else if (IPAddress.TryParse(host, out targetAddress))
                {
                    request.Add(0x01);
                    request.AddRange(targetAddress.GetAddressBytes());
                }
                else
                {
                    // UDP ASSOCIATE：DST.ADDR/DST.PORT 全 0 表示“由代理决定”。
                    request.Add(0x01);
                    request.AddRange(new byte[] { 0, 0, 0, 0 });
                }
                request.Add((byte)(port >> 8));
                request.Add((byte)(port & 0xff));
                socket.Send(request.ToArray());
                return true;
            }
            catch (Exception ex)
            {
                error = "SOCKS5 请求发送异常：" + ex.Message;
                return false;
            }
        }

        private static bool TryReadReply(Socket socket, out IPAddress boundAddress,
            out int boundPort, out string error)
        {
            boundAddress = null;
            boundPort = 0;
            error = null;
            try
            {
                byte[] head = ReadExactly(socket, 4);
                if (head == null || head[0] != 0x05)
                {
                    error = "SOCKS5 回复格式非法";
                    return false;
                }
                if (head[1] != 0)
                {
                    error = "SOCKS5 回复失败代码：" + head[1];
                    return false;
                }
                switch (head[3])
                {
                    case 0x01:
                        byte[] v4 = ReadExactly(socket, 4);
                        if (v4 != null) boundAddress = new IPAddress(v4);
                        break;
                    case 0x04:
                        byte[] v6 = ReadExactly(socket, 16);
                        if (v6 != null) boundAddress = new IPAddress(v6);
                        break;
                    case 0x03:
                        byte[] length = ReadExactly(socket, 1);
                        if (length == null) { error = "SOCKS5 域名回复非法"; return false; }
                        byte[] name = ReadExactly(socket, length[0]);
                        if (name == null) { error = "SOCKS5 域名回复非法"; return false; }
                        IPAddress.TryParse(Encoding.ASCII.GetString(name), out boundAddress);
                        break;
                    default:
                        error = "SOCKS5 地址类型不支持：" + head[3];
                        return false;
                }
                byte[] port = ReadExactly(socket, 2);
                if (port == null) { error = "SOCKS5 回复端口缺失"; return false; }
                boundPort = (port[0] << 8) | port[1];
                return true;
            }
            catch (Exception ex)
            {
                error = "SOCKS5 回复读取异常：" + ex.Message;
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
                if (received <= 0) return null;
                read += received;
            }
            return buffer;
        }

        private static byte[] EncodeDatagram(byte[] payload, string host, int port)
        {
            payload ??= Array.Empty<byte>();
            var buffer = new List<byte>(payload.Length + 32) { 0, 0, 0 };
            if (!string.IsNullOrEmpty(host) && !IPAddress.TryParse(host, out _))
            {
                byte[] name = Encoding.ASCII.GetBytes(host);
                buffer.Add(0x03);
                buffer.Add((byte)name.Length);
                buffer.AddRange(name);
            }
            else if (IPAddress.TryParse(host, out IPAddress address) &&
                address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                buffer.Add(0x04);
                buffer.AddRange(address.GetAddressBytes());
            }
            else if (IPAddress.TryParse(host, out address))
            {
                buffer.Add(0x01);
                buffer.AddRange(address.GetAddressBytes());
            }
            else
            {
                buffer.Add(0x01);
                buffer.AddRange(new byte[] { 0, 0, 0, 0 });
            }
            buffer.Add((byte)(port >> 8));
            buffer.Add((byte)(port & 0xff));
            buffer.AddRange(payload);
            return buffer.ToArray();
        }

        private static bool TryDecodeDatagram(byte[] buffer, int length, out IPEndPoint source,
            out byte[] payload)
        {
            source = null;
            payload = null;
            if (buffer == null || length < 10 || buffer[0] != 0 || buffer[1] != 0 || buffer[2] != 0)
                return false;
            int offset = 3;
            switch (buffer[offset++])
            {
                case 0x01:
                    if (length < offset + 6) return false;
                    source = new IPEndPoint(new IPAddress(new[]
                    {
                        buffer[offset], buffer[offset + 1], buffer[offset + 2], buffer[offset + 3]
                    }), 0);
                    offset += 4;
                    break;
                case 0x04:
                    if (length < offset + 18) return false;
                    byte[] v6 = new byte[16];
                    Array.Copy(buffer, offset, v6, 0, 16);
                    source = new IPEndPoint(new IPAddress(v6), 0);
                    offset += 16;
                    break;
                case 0x03:
                    if (length < offset + 1) return false;
                    int nameLength = buffer[offset++];
                    if (length < offset + nameLength + 2) return false;
                    string name = Encoding.ASCII.GetString(buffer, offset, nameLength);
                    offset += nameLength;
                    source = new IPEndPoint(IPAddress.TryParse(name, out IPAddress parsed)
                        ? parsed : IPAddress.None, 0);
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
}
