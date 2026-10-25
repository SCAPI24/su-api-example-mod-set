using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SuMultiplayer.Net
{
    /// <summary>房间广播信息（LAN 发现用，纯数据）。</summary>
    public sealed class RoomBeaconInfo
    {
        public string RoomName = "SuMP Room";
        public string HostName = string.Empty;
        public int Port;
        public int Players;
        public int MaxPlayers = 8;
        public string WorldName = string.Empty;
        public string GameMode = string.Empty;
        public byte Protocol = 1;
        /// <summary>
        /// 客户端测得的往返延迟（毫秒；-1 = 还没测到 / 对端不回显探测序号）。
        /// 照旧联机 mod 的房间行口径：探测包带时间戳、应答原样回显，客户端用本地时间差算 RTT。
        /// </summary>
        public int PingMs = -1;

        public override string ToString() =>
            "room=" + RoomName + " host=" + HostName + ":" + Port + " world=" + WorldName +
            " players=" + Players + "/" + MaxPlayers + " mode=" + GameMode + " proto=" + Protocol;
    }

    /// <summary>
    /// LAN 房间发现的报文编解码：**文本格式**（便于用抓包/日志直接看，不需要额外工具）。
    ///   探测：<c>SUMP?\t1</c>
    ///   应答：<c>SUMP1\t房间名\t主机名\t端口\t人数\t上限\t世界名\t模式</c>
    /// 演示页标记：ROOM01。
    /// </summary>
    public static class RoomBeaconCodec
    {
        public const string ProbeToken = "SUMP?";
        public const string ReplyToken = "SUMP1";
        private const char Sep = '\t';

        /// <summary>探测包：<c>SUMP?	1	&lt;nonce&gt;</c>（nonce = 客户端本地毫秒时刻，用于算 RTT）。</summary>
        public static byte[] EncodeProbe() => EncodeProbe(Environment.TickCount64);

        public static byte[] EncodeProbe(long nonce) =>
            Encoding.UTF8.GetBytes(ProbeToken + Sep + "1" + Sep +
                nonce.ToString(System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>从探测包里读 nonce（旧探测 / 坏包 = 0，主机照样应答但不回显）。</summary>
        public static long ParseProbeNonce(byte[] data, int length)
        {
            if (data == null || length <= 0) return 0L;
            string text = Encoding.UTF8.GetString(data, 0, length);
            string[] parts = text.Split(Sep);
            if (parts.Length < 3) return 0L;
            return long.TryParse(parts[2], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long nonce) ? nonce : 0L;
        }

        public static bool IsProbe(byte[] data, int length)
        {
            if (data == null || length < ProbeToken.Length) return false;
            return Encoding.UTF8.GetString(data, 0, ProbeToken.Length) == ProbeToken;
        }

        /// <summary>从应答里读回显的 nonce（没有 = 0：旧主机 / 坏包 → 延迟显示 unknown）。</summary>
        public static long ParseReplyNonce(byte[] data, int length)
        {
            if (data == null || length <= 0) return 0L;
            string[] parts = Encoding.UTF8.GetString(data, 0, length).Split(Sep);
            if (parts.Length < 9) return 0L;
            return long.TryParse(parts[8], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long nonce) ? nonce : 0L;
        }

        public static byte[] EncodeReply(RoomBeaconInfo info) => EncodeReply(info, 0L);

        /// <summary>应答：末尾原样回显探测的 nonce（0 = 没有 nonce，客户端只能显示 unknown 延迟）。</summary>
        public static byte[] EncodeReply(RoomBeaconInfo info, long nonce)
        {
            info = info ?? new RoomBeaconInfo();
            string text = string.Join(Sep.ToString(), new[]
            {
                ReplyToken, info.RoomName, info.HostName, info.Port.ToString(),
                info.Players.ToString(), info.MaxPlayers.ToString(), info.WorldName,
                info.GameMode, nonce.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            return Encoding.UTF8.GetBytes(text);
        }

        public static bool TryDecodeReply(byte[] data, int length, out RoomBeaconInfo info, out string address)
        {
            info = null;
            address = null;
            if (data == null || length <= 0) return false;
            string text = Encoding.UTF8.GetString(data, 0, length);
            string[] parts = text.Split(Sep);
            if (parts.Length < 6 || parts[0] != ReplyToken) return false;
            if (!int.TryParse(parts[3], out int port)) return false;
            info = new RoomBeaconInfo
            {
                RoomName = parts[1],
                HostName = parts[2],
                Port = port,
                Players = int.TryParse(parts[4], out int players) ? players : 0,
                MaxPlayers = int.TryParse(parts[5], out int max) ? max : 8,
                WorldName = parts.Length > 6 ? parts[6] : string.Empty,
                GameMode = parts.Length > 7 ? parts[7] : string.Empty
            };
            // nonce 只用于算 RTT，由上层（RoomBeaconClient）在同一轮答复里折算成 PingMs。
            info.PingMs = -1;
            return true;
        }
    }

    /// <summary>
    /// 主机侧：只要有人探测就应答（**不周期广播**，省电省带宽，也不会在别人网络里刷屏）。
    /// 演示页标记：ROOM02。
    /// </summary>
    public sealed class RoomBeaconHost : IDisposable
    {
        private readonly int m_port;
        private readonly Func<RoomBeaconInfo> m_infoProvider;
        private UdpClient m_socket;
        private Thread m_thread;
        private volatile bool m_running;
        public long ProbesAnswered { get; private set; }
        public int BoundPort => m_port;

        public RoomBeaconHost(int discoveryPort, Func<RoomBeaconInfo> infoProvider)
        {
            m_port = discoveryPort;
            m_infoProvider = infoProvider;
        }

        public bool Start()
        {
            try
            {
                m_socket = new UdpClient();
                m_socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                m_socket.Client.Bind(new IPEndPoint(IPAddress.Any, m_port));
                m_running = true;
                m_thread = new Thread(Loop) { IsBackground = true, Name = "SuMP-Beacon" };
                m_thread.Start();
                return true;
            }
            catch (Exception)
            {
                m_socket?.Close();
                m_socket = null;
                return false;
            }
        }

        private void Loop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (m_running)
            {
                try
                {
                    byte[] data = m_socket.Receive(ref remote);
                    if (!RoomBeaconCodec.IsProbe(data, data.Length)) continue;
                    RoomBeaconInfo info = m_infoProvider?.Invoke() ?? new RoomBeaconInfo();
                    info.HostName = remote.Address.ToString();
                    // 原样回显探测的 nonce：客户端用本地时间差算房间行上的 RTT（旧联机 mod 的 Ping 口径）。
                    byte[] reply = RoomBeaconCodec.EncodeReply(info,
                        RoomBeaconCodec.ParseProbeNonce(data, data.Length));
                    m_socket.Send(reply, reply.Length, remote);
                    ProbesAnswered++;
                }
                catch (SocketException)
                {
                    if (!m_running) return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            m_running = false;
            try { m_socket?.Close(); } catch (Exception) { }
            m_socket = null;
        }
    }

    /// <summary>
    /// 客户端侧：每 <see cref="ProbeIntervalMs"/> 广播一次探测，收集应答。
    /// 收不到任何回应也算正常（可能只有直连地址可用），不阻塞、不重试风暴。
    /// 演示页标记：ROOM03。
    /// </summary>
    public sealed class RoomBeaconClient : IDisposable
    {
        private readonly int m_port;
        private UdpClient m_socket;
        private Thread m_thread;
        private volatile bool m_running;
        private long m_lastProbeMs;
        /// <summary>已发出但还没收到应答的探测：nonce → 发送时刻（毫秒）。有界，防坏包堆积。</summary>
        private readonly Dictionary<long, long> m_pendingProbes = new Dictionary<long, long>();
        private const int MaxPendingProbes = 64;

        /// <summary>探测间隔（旧联机 mod 的 Ping 探测是 1 s 一轮；房间行上的延迟靠它刷新）。</summary>
        public int ProbeIntervalMs { get; set; } = 1000;
        public long ProbesSent { get; private set; }
        public long RepliesReceived { get; private set; }
        /// <summary>是否至少收到过一个应答（用于判断广播是否被挡）。</summary>
        public bool HasAnyReply => RepliesReceived > 0;
        /// <summary>发现房间（info, 来源地址）。</summary>
        public event Action<RoomBeaconInfo, string> RoomFound;

        public RoomBeaconClient(int discoveryPort) { m_port = discoveryPort; }

        public bool Start()
        {
            try
            {
                m_socket = new UdpClient();
                m_socket.EnableBroadcast = true;
                m_socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                m_running = true;
                m_thread = new Thread(Loop) { IsBackground = true, Name = "SuMP-Probe" };
                m_thread.Start();
                return true;
            }
            catch (Exception)
            {
                m_socket?.Close();
                m_socket = null;
                return false;
            }
        }

        private void Loop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            var broadcast = new IPEndPoint(IPAddress.Broadcast, m_port);
            while (m_running)
            {
                long now = Environment.TickCount64;
                if (now - m_lastProbeMs >= ProbeIntervalMs)
                {
                    m_lastProbeMs = now;
                    try
                    {
                        byte[] probe = RoomBeaconCodec.EncodeProbe(now);
                        m_socket.Send(probe, probe.Length, broadcast);
                        ProbesSent++;
                        if (m_pendingProbes.Count >= MaxPendingProbes) m_pendingProbes.Clear();
                        m_pendingProbes[now] = now;
                    }
                    catch (Exception)
                    {
                        // 广播被系统拒绝（某些网络禁广播）：不致命，等下一轮
                    }
                }
                try
                {
                    if (m_socket.Available > 0)
                    {
                        byte[] data = m_socket.Receive(ref remote);
                        if (RoomBeaconCodec.TryDecodeReply(data, data.Length, out RoomBeaconInfo info, out _))
                        {
                            RepliesReceived++;
                            long nonce = RoomBeaconCodec.ParseReplyNonce(data, data.Length);
                            info.PingMs = ComputePingMs(nonce, Environment.TickCount64);
                            RoomFound?.Invoke(info, remote.Address.ToString());
                        }
                        continue;
                    }
                }
                catch (SocketException)
                {
                    if (!m_running) return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                Thread.Sleep(50);
            }
        }

        /// <summary>
        /// 用探测→应答的本地时间差算 RTT。
        /// nonce 不在待答复表里（旧主机不回显 / 坏包 / 超时太久）→ -1（房间行显示 unknown），
        /// **绝不猜**一个延迟。
        /// </summary>
        private int ComputePingMs(long nonce, long nowMs)
        {
            if (nonce <= 0L) return -1;
            if (!m_pendingProbes.Remove(nonce)) return -1;
            long rtt = nowMs - nonce;
            if (rtt < 0L) return -1;
            if (rtt > 5000L) return -1;
            return (int)rtt;
        }

        /// <summary>单机自测：不需要广播也能用（-1 = 广播全地址）。</summary>
        public int ProbeTargets(IEnumerable<string> addresses)
        {
            int sent = 0;
            if (addresses == null) return 0;
            foreach (string address in addresses)
            {
                try
                {
                    byte[] probe = RoomBeaconCodec.EncodeProbe();
                    m_socket.Send(probe, probe.Length, new IPEndPoint(IPAddress.Parse(address), m_port));
                    sent++;
                }
                catch (Exception)
                {
                }
            }
            return sent;
        }

        public void Dispose()
        {
            m_running = false;
            try { m_socket?.Close(); } catch (Exception) { }
            m_socket = null;
        }
    }
}
