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

        public static byte[] EncodeProbe() => Encoding.UTF8.GetBytes(ProbeToken + Sep + "1");

        public static bool IsProbe(byte[] data, int length)
        {
            if (data == null || length < ProbeToken.Length) return false;
            return Encoding.UTF8.GetString(data, 0, ProbeToken.Length) == ProbeToken;
        }

        public static byte[] EncodeReply(RoomBeaconInfo info)
        {
            info = info ?? new RoomBeaconInfo();
            string text = string.Join(Sep.ToString(), new[]
            {
                ReplyToken, info.RoomName, info.HostName, info.Port.ToString(),
                info.Players.ToString(), info.MaxPlayers.ToString(), info.WorldName, info.GameMode
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
                    byte[] reply = RoomBeaconCodec.EncodeReply(info);
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

        public int ProbeIntervalMs { get; set; } = 2000;
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
                        byte[] probe = RoomBeaconCodec.EncodeProbe();
                        m_socket.Send(probe, probe.Length, broadcast);
                        ProbesSent++;
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
