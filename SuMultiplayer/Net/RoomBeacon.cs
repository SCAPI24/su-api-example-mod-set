using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SuMultiplayer.Engine;

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
        /// <summary>房间世界大小（旧 SuPlayScreen 行里的 DataSizeFormatter 字段；0 = 未知）。</summary>
        public long WorldSizeBytes;
        /// <summary>世界最后保存时间（UTC；DateTime.MinValue = 未知）。</summary>
        public DateTime LastSaveTimeUtc = DateTime.MinValue;
        /// <summary>环境模式（Living/…；旧列表行的 EnvironmentBehaviorMode）。</summary>
        public string EnvironmentMode = string.Empty;
        /// <summary>存档序列化版本（旧列表行版本不一致时显示）。</summary>
        public string SerializationVersion = string.Empty;
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
                info.GameMode, nonce.ToString(System.Globalization.CultureInfo.InvariantCulture),
                info.WorldSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (info.LastSaveTimeUtc == DateTime.MinValue ? "0" :
                    info.LastSaveTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                info.EnvironmentMode ?? string.Empty,
                info.SerializationVersion ?? string.Empty
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
            if (parts.Length > 9 && long.TryParse(parts[9], out long sizeBytes))
                info.WorldSizeBytes = Math.Max(0, sizeBytes);
            if (parts.Length > 10 && long.TryParse(parts[10], out long saveTicks) && saveTicks > 0)
                info.LastSaveTimeUtc = new DateTime(saveTicks, DateTimeKind.Utc);
            if (parts.Length > 11) info.EnvironmentMode = parts[11];
            if (parts.Length > 12) info.SerializationVersion = parts[12];
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
                // 刻意**不设** ReuseAddress：端口占用判据依赖"UDP 绑不上"。
                // 设了 ReuseAddress 的话，同一台机器上两个实例能同时绑同一条 UDP 端口
                // → 既判不出槽位占用，客户端还会收到"同端口两个房间"的混乱应答。
                // UDP 没有 TIME_WAIT，Dispose 后端口立刻可用，所以去掉它没有副作用。
                m_socket.Client.Bind(new IPEndPoint(IPAddress.Any, m_port));
                m_running = true;
                UdpClient socket = m_socket;
                m_thread = new Thread(() => Loop(socket)) { IsBackground = true, Name = "SuMP-Beacon" };
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

        private void Loop(UdpClient socket)
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (m_running)
            {
                try
                {
                    byte[] data = socket.Receive(ref remote);
                    if (!RoomBeaconCodec.IsProbe(data, data.Length)) continue;
                    RoomBeaconInfo info = m_infoProvider?.Invoke() ?? new RoomBeaconInfo();
                    info.HostName = remote.Address.ToString();
                    // 原样回显探测的 nonce：客户端用本地时间差算房间行上的 RTT（旧联机 mod 的 Ping 口径）。
                    byte[] reply = RoomBeaconCodec.EncodeReply(info,
                        RoomBeaconCodec.ParseProbeNonce(data, data.Length));
                    socket.Send(reply, reply.Length, remote);
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
        /// <summary>要探测的端口段（槽位从 start 起连续 count 个）。</summary>
        private readonly int[] m_ports;
        private UdpClient m_socket;
        private volatile bool m_running;
        private long m_lastProblemMs;
        private long m_probesSent;
        private long m_repliesReceived;
        private readonly HashSet<long> m_pendingProbes = new HashSet<long>();
        private const int MaxPendingProbes = 64;
        private const int InterfaceRefreshMs = 3000;
        private const int ReplyBudgetPerSocket = 16;

        /// <summary>探测间隔（旧联机 mod 的 Ping 探测是 1 s 一轮；房间行上的延迟靠它刷新）。</summary>
        public int ProbeIntervalMs { get; set; } = 1000;
        public long ProbesSent => Interlocked.Read(ref m_probesSent);
        public long RepliesReceived => Interlocked.Read(ref m_repliesReceived);
        /// <summary>是否至少收到过一个应答（用于判断广播是否被挡）。</summary>
        public bool HasAnyReply => RepliesReceived > 0;
        /// <summary>发现房间（info, 来源地址）。</summary>
        public event Action<RoomBeaconInfo, string> RoomFound;

        /// <param name="portStart">端口段起点。</param>
        /// <param name="portCount">端口段槽位数。</param>
        public RoomBeaconClient(int portStart, int portCount)
        {
            int count = Math.Max(1, Math.Min(256, portCount));
            m_ports = new int[count];
            for (int i = 0; i < count; i++) m_ports[i] = portStart + i;
        }

        /// <summary>本客户端要探测的端口（只读，诊断用）。</summary>
        public IReadOnlyList<int> Ports => m_ports;

        public bool Start()
        {
            if (m_socket != null) return false;
            try
            {
                UdpClient socket = BindProbeSocket(IPAddress.Any);
                m_socket = socket;
                m_running = true;
                var thread = new Thread(() => Loop(socket)) { IsBackground = true, Name = "SuMP-Probe" };
                thread.Start();
                return true;
            }
            catch (Exception e)
            {
                m_running = false;
                m_socket?.Dispose();
                m_socket = null;
                LogProblem("启动", e);
                return false;
            }
        }

        private static UdpClient BindProbeSocket(IPAddress localAddress)
        {
            var socket = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                socket.EnableBroadcast = true;
                socket.Client.Bind(new IPEndPoint(localAddress, 0));
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private void Loop(UdpClient socket)
        {
            bool useInterfaceBroadcasts = OperatingSystem.IsWindows();
            var interfaceSockets = new Dictionary<(IPAddress Source, IPAddress Broadcast), UdpClient>();
            long nextRefresh = 0;
            long nextProbe = 0;
            try
            {
                while (m_running)
                {
                    long now = Environment.TickCount64;
                    if (useInterfaceBroadcasts && now >= nextRefresh)
                    {
                        RefreshInterfaceSockets(interfaceSockets);
                        nextRefresh = now + InterfaceRefreshMs;
                    }
                    if (now >= nextProbe)
                    {
                        byte[] probe = CreateProbe(now);
                        if (useInterfaceBroadcasts)
                        {
                            foreach (var entry in interfaceSockets)
                                SendProbe(entry.Value, entry.Key.Broadcast, probe);
                        }
                        else
                        {
                            SendProbe(socket, IPAddress.Broadcast, probe);
                        }
                        nextProbe = now + Math.Max(1, ProbeIntervalMs);
                    }

                    ReadReplies(socket);
                    foreach (UdpClient interfaceSocket in interfaceSockets.Values)
                        ReadReplies(interfaceSocket);
                    Thread.Sleep(20);
                }
            }
            finally
            {
                foreach (UdpClient interfaceSocket in interfaceSockets.Values)
                    interfaceSocket.Dispose();
                socket.Dispose();
            }
        }

        // Source: Comms/UdpTransmitter.cs:UdpTransmitter.CreateIPv4NetworkSnapshot
        private void RefreshInterfaceSockets(Dictionary<(IPAddress Source, IPAddress Broadcast), UdpClient> sockets)
        {
            var available = new HashSet<(IPAddress Source, IPAddress Broadcast)>();
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    try
                    {
                        foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                        {
                            IPAddress address = unicast.Address;
                            if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) ||
                                address.Equals(IPAddress.Any) || unicast.PrefixLength < 1 || unicast.PrefixLength > 30)
                                continue;
                            byte[] broadcast = address.GetAddressBytes();
                            for (int i = 0; i < broadcast.Length; i++)
                            {
                                int networkBits = Math.Clamp(unicast.PrefixLength - i * 8, 0, 8);
                                broadcast[i] |= (byte)((1 << (8 - networkBits)) - 1);
                            }
                            available.Add((address, new IPAddress(broadcast)));
                        }
                    }
                    catch (NetworkInformationException e) { LogProblem("读取网卡", e); }
                }
            }
            catch (NetworkInformationException e)
            {
                LogProblem("枚举网卡", e);
                return;
            }

            foreach (var pair in new List<(IPAddress Source, IPAddress Broadcast)>(sockets.Keys))
            {
                if (available.Contains(pair)) continue;
                sockets[pair].Dispose();
                sockets.Remove(pair);
            }
            foreach (var pair in available)
            {
                if (!m_running || sockets.ContainsKey(pair)) continue;
                try { sockets.Add(pair, BindProbeSocket(pair.Source)); }
                catch (SocketException e) { LogProblem("绑定搜房网卡", e); }
            }
        }

        private byte[] CreateProbe(long now)
        {
            lock (m_pendingProbes)
            {
                m_pendingProbes.RemoveWhere(nonce => now - nonce > 5000L);
                if (m_pendingProbes.Count >= MaxPendingProbes) m_pendingProbes.Clear();
                m_pendingProbes.Add(now);
            }
            return RoomBeaconCodec.EncodeProbe(now);
        }

        private int SendProbe(UdpClient socket, IPAddress target, byte[] probe)
        {
            int sent = 0;
            foreach (int port in m_ports)
            {
                if (!m_running) break;
                try
                {
                    socket.Send(probe, probe.Length, new IPEndPoint(target, port));
                    Interlocked.Increment(ref m_probesSent);
                    sent++;
                }
                catch (SocketException e) { if (m_running) LogProblem("发送搜房探测", e); }
                catch (ObjectDisposedException) when (!m_running) { break; }
            }
            return sent;
        }

        private void ReadReplies(UdpClient socket)
        {
            try
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                for (int i = 0; i < ReplyBudgetPerSocket && m_running && socket.Available > 0; i++)
                {
                    byte[] data = socket.Receive(ref remote);
                    if (!RoomBeaconCodec.TryDecodeReply(data, data.Length, out RoomBeaconInfo info, out _))
                        continue;
                    Interlocked.Increment(ref m_repliesReceived);
                    long nonce = RoomBeaconCodec.ParseReplyNonce(data, data.Length);
                    info.PingMs = ComputePingMs(nonce, Environment.TickCount64);
                    RoomFound?.Invoke(info, remote.Address.ToString());
                }
            }
            catch (SocketException e) { if (m_running) LogProblem("接收搜房应答", e); }
            catch (ObjectDisposedException) when (!m_running) { }
        }

        // One broadcast round can have many hosts respond. Keep its nonce valid for all of them.
        private int ComputePingMs(long nonce, long nowMs)
        {
            if (nonce <= 0L || nowMs < nonce || nowMs - nonce > 5000L) return -1;
            lock (m_pendingProbes)
                return m_pendingProbes.Contains(nonce) ? (int)(nowMs - nonce) : -1;
        }

        /// <summary>向指定的 IPv4 地址发探测，按系统路由选择出口。</summary>
        public int ProbeTargets(IEnumerable<string> addresses)
        {
            UdpClient socket = m_socket;
            if (addresses == null || socket == null || !m_running) return 0;
            byte[] probe = CreateProbe(Environment.TickCount64);
            int sent = 0;
            foreach (string address in addresses)
            {
                if (IPAddress.TryParse(address, out IPAddress target) &&
                    target.AddressFamily == AddressFamily.InterNetwork)
                    sent += SendProbe(socket, target, probe);
            }
            return sent;
        }

        private void LogProblem(string operation, Exception error)
        {
            long now = Environment.TickCount64;
            long previous = Interlocked.Read(ref m_lastProblemMs);
            if (previous != 0 && now - previous < 10000L) return;
            if (Interlocked.CompareExchange(ref m_lastProblemMs, now, previous) == previous)
                ModLog.Warn("[SuMP][LAN] " + operation + "失败：" + error.Message);
        }

        public void Dispose()
        {
            m_running = false;
            // The worker owns the bound interface sockets and closes them in finally.
            m_socket?.Dispose();
        }
    }
}
