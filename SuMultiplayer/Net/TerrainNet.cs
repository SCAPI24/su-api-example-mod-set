using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SuMultiplayer.Protocol;

namespace SuMultiplayer.Net
{
    // 消息类型 id 在 Protocol/protocol.proto 的 MessageType 枚举里（1..999 内置 /
    // 1000..1999 第三方）。帧头的 type 是 uint16，所以第三方 id 也能原样进帧。

    /// <summary>客户端种类：普通玩家端 / 调试端（只观察，不参与权威）。</summary>
    public enum ClientKind : byte
    {
        Player = 0,
        Debug = 1
    }

    public static class TerrainFrame
    {
        public const int HeaderBytes = 16;
        public const int MaxPayloadBytes = 60 * 1024;

        public static byte[] Encode(long sequence, MessageType type, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            if (payload.Length > MaxPayloadBytes) throw new ArgumentException("payload 过大");
            int typeId = (int)type;
            if (!MessageRegistry.IsValidId(typeId))
                throw new ArgumentOutOfRangeException(nameof(type), "消息类型 id " + typeId +
                    " 不在 1.." + MessageRegistry.ThirdPartyMax + " 内");
            var buffer = new byte[HeaderBytes + payload.Length];
            WriteInt32(buffer, 0, payload.Length);
            WriteInt64(buffer, 4, sequence);
            WriteUInt16(buffer, 12, (ushort)typeId);  // 紧凑 id（uint16：内置 + 第三方）
            WriteUInt16(buffer, 14, 0);              // flags
            Buffer.BlockCopy(payload, 0, buffer, HeaderBytes, payload.Length);
            return buffer;
        }

        /// <summary>解析帧头。返回 false = 协议违规，调用方必须关闭连接（不能当丢包忽略）。</summary>
        public static bool TryReadHeader(byte[] buffer, int offset, out int payloadLength, out long sequence,
            out MessageType type)
        {
            payloadLength = 0; sequence = 0; type = MessageType.Unspecified;
            if (buffer == null || offset + HeaderBytes > buffer.Length) return false;
            payloadLength = ReadInt32(buffer, offset);
            sequence = ReadInt64(buffer, offset + 4);
            int typeId = ReadUInt16(buffer, offset + 12);
            if (payloadLength < 0 || payloadLength > MaxPayloadBytes) return false;
            // 结构校验：只接受 1..1999（内置 + 第三方）。具体类型是否注册由 MessageRegistry 判定。
            if (!MessageRegistry.IsValidId(typeId)) return false;
            type = (MessageType)typeId;
            return true;
        }

        public static void WriteInt32(byte[] b, int o, int v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        public static void WriteInt64(byte[] b, int o, long v)
        {
            for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i));
        }

        public static void WriteUInt16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
        }

        public static int ReadInt32(byte[] b, int o) =>
            b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

        public static long ReadInt64(byte[] b, int o)
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v |= (long)b[o + i] << (8 * i);
            return v;
        }

        public static ushort ReadUInt16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    }

    /// <summary>
    /// 简单的 LE 负载读写器。**只给冻结的 live-map 载荷用**
    /// （<see cref="SuMultiplayer.Terrain.LiveMapCodec"/>）；新消息一律用 protobuf。
    /// </summary>
    public sealed class PayloadWriter
    {
        private readonly List<byte> m_bytes = new List<byte>(256);

        public int Length => m_bytes.Count;
        public byte[] ToArray() => m_bytes.ToArray();
        public void Clear() => m_bytes.Clear();

        public PayloadWriter Byte(byte v) { m_bytes.Add(v); return this; }
        /// <summary>小端 16 位（地图每列的方块 contents 用它，比 Int32 省一半体积）。</summary>
        public PayloadWriter UInt16(ushort v) { m_bytes.Add((byte)v); m_bytes.Add((byte)(v >> 8)); return this; }
        public PayloadWriter Int32(int v) { var t = new byte[4]; TerrainFrame.WriteInt32(t, 0, v); m_bytes.AddRange(t); return this; }
        /// <summary>布尔：1 字节 0/1（旧 SuWriter.WriteBoolean）。</summary>
        public PayloadWriter Boolean(bool v) { m_bytes.Add(v ? (byte)1 : (byte)0); return this; }
        /// <summary>单精度浮点：小端 4 字节（旧 SuWriter.WriteSingle；位置快照的向量逐分量用它）。</summary>
        public PayloadWriter Single(float v) => Int32(BitConverter.SingleToInt32Bits(v));
        public PayloadWriter UInt32(uint v) { return Int32((int)v); }
        public PayloadWriter Int64(long v) { var t = new byte[8]; TerrainFrame.WriteInt64(t, 0, v); m_bytes.AddRange(t); return this; }
        public PayloadWriter Text(string s)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s ?? string.Empty);
            Int32(bytes.Length);
            m_bytes.AddRange(bytes);
            return this;
        }
        public PayloadWriter Bytes(byte[] data, int offset, int count)
        {
            Int32(count);
            for (int i = 0; i < count; i++) m_bytes.Add(data[offset + i]);
            return this;
        }
    }

    public sealed class PayloadReader
    {
        private readonly byte[] m_bytes;
        private int m_position;

        public PayloadReader(byte[] bytes) { m_bytes = bytes ?? Array.Empty<byte>(); }
        public int Position => m_position;
        public int Remaining => m_bytes.Length - m_position;
        public bool AtEnd => m_position >= m_bytes.Length;

        public byte Byte() => m_bytes[m_position++];
        /// <summary>小端 16 位。</summary>
        public ushort UInt16()
        {
            int v = m_bytes[m_position] | (m_bytes[m_position + 1] << 8);
            m_position += 2;
            return (ushort)v;
        }
        public int Int32() { int v = TerrainFrame.ReadInt32(m_bytes, m_position); m_position += 4; return v; }
        /// <summary>布尔：1 字节 0/1（旧 SuReader.ReadBoolean）。</summary>
        public bool Boolean() => m_bytes[m_position++] != 0;
        /// <summary>单精度浮点：小端 4 字节（旧 SuReader.ReadSingle）。</summary>
        public float Single() => BitConverter.Int32BitsToSingle(Int32());
        public uint UInt32() => (uint)Int32();
        public long Int64() { long v = TerrainFrame.ReadInt64(m_bytes, m_position); m_position += 8; return v; }
        public string Text()
        {
            int len = Int32();
            if (len < 0 || m_position + len > m_bytes.Length) throw new InvalidOperationException("payload 截断");
            string s = System.Text.Encoding.UTF8.GetString(m_bytes, m_position, len);
            m_position += len;
            return s;
        }
        /// <summary>读指定长度的原始字节（**没有**长度前缀；世界分片用）。</summary>
        public byte[] RawBytes(int length)
        {
            if (length < 0 || m_position + length > m_bytes.Length) throw new InvalidOperationException("payload 截断");
            var result = new byte[length];
            Buffer.BlockCopy(m_bytes, m_position, result, 0, length);
            m_position += length;
            return result;
        }

        public byte[] Bytes()
        {
            int len = Int32();
            if (len < 0 || m_position + len > m_bytes.Length) throw new InvalidOperationException("payload 截断");
            var result = new byte[len];
            Buffer.BlockCopy(m_bytes, m_position, result, 0, len);
            m_position += len;
            return result;
        }
    }

    /// <summary>通道健康状态：只有这三态，**没有「丢了一些但还能用」**。</summary>
    public enum ChannelState : byte
    {
        Disconnected = 0,
        Connected = 1,
        /// <summary>发送队列积压到上限：调用方必须显式处理（暂停加载 / 降级 / 断开）。</summary>
        Backpressured = 2,
        Failed = 3
    }

    /// <summary>通道统计（用于页面与断言：发送 = 接收 + 在途，丢弃恒为 0）。</summary>
    public sealed class ChannelStats
    {
        public long FramesSent;
        public long FramesReceived;
        public long BytesSent;
        public long BytesReceived;
        public long BackpressureEvents;
        public long ProtocolViolations;
        public long SendQueueHigh;
        public long Dropped;              // 设计上恒为 0

        public override string ToString() =>
            "sent=" + FramesSent + " recv=" + FramesReceived + " bytes=" + BytesSent + "/" + BytesReceived +
            " backpressure=" + BackpressureEvents + " violations=" + ProtocolViolations + " dropped=" + Dropped;
    }

    public readonly struct ReceivedFrame
    {
        public ReceivedFrame(int clientId, long sequence, MessageType type, byte[] payload)
        {
            ClientId = clientId; Sequence = sequence; Type = type; Payload = payload;
        }
        public int ClientId { get; }
        public long Sequence { get; }
        public MessageType Type { get; }
        public byte[] Payload { get; }
    }

    /// <summary>
    /// TCP 通道（服务端）：每客户端一条连接、一个接收线程；发送走有界队列 + 发送线程。
    /// 要点：<c>NoDelay</c>（否则最多攒 200 ms）；队列满 = 显式 Backpressured，**绝不静默丢**。
    /// 演示页标记：NET02。
    /// </summary>
    public sealed class TcpTerrainServer : IDisposable
    {
        private sealed class Peer
        {
            public int Id;
            public TcpClient Tcp;
            public NetworkStream Stream;
            public readonly Queue<byte[]> SendQueue = new Queue<byte[]>();
            public readonly ManualResetEventSlim SendWake = new ManualResetEventSlim(false);
            public Thread ReceiveThread;
            public Thread SendThread;
            public volatile bool Alive = true;
            public ChannelState State = ChannelState.Connected;
            public ClientKind Kind = ClientKind.Player;
            public string Name = string.Empty;
            public readonly ChannelStats Stats = new ChannelStats();
            public long LastReceivedSequence = -1;
        }

        private readonly TcpListener m_listener;
        private readonly List<Peer> m_peers = new List<Peer>();
        private readonly object m_gate = new object();
        private readonly int m_queueCapacity;
        private Thread m_acceptThread;
        private int m_nextClientId = 1;
        private volatile bool m_running;
        private long m_sendSequence;

        /// <param name="bindAddress">
        /// 默认 <see cref="IPAddress.Any"/> —— **局域网内其它设备必须能连进来**。
        /// （只在本机自测时才可以传 Loopback；调试端那种只服务本机网页的场景另说。）
        /// </param>
        public TcpTerrainServer(int port, int queueCapacity = 256, IPAddress bindAddress = null)
        {
            m_queueCapacity = Math.Max(8, queueCapacity);
            m_listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
        }

        public int Port => ((IPEndPoint)m_listener.LocalEndpoint).Port;
        public int QueueCapacity => m_queueCapacity;

        /// <summary>收到一帧（在接收线程上回调）。</summary>
        public event Action<ReceivedFrame> FrameReceived;
        public event Action<int, ChannelState, string> PeerStateChanged;

        public void Start()
        {
            m_listener.Start();
            m_running = true;
            m_acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "SuMP-Accept" };
            m_acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (m_running)
            {
                try
                {
                    TcpClient tcp = m_listener.AcceptTcpClient();
                    tcp.NoDelay = true;
                    var peer = new Peer
                    {
                        Id = Interlocked.Increment(ref m_nextClientId) - 1,
                        Tcp = tcp,
                        Stream = tcp.GetStream()
                    };
                    lock (m_gate) m_peers.Add(peer);
                    peer.ReceiveThread = new Thread(() => ReceiveLoop(peer)) { IsBackground = true, Name = "SuMP-Recv" + peer.Id };
                    peer.SendThread = new Thread(() => SendLoop(peer)) { IsBackground = true, Name = "SuMP-Send" + peer.Id };
                    peer.ReceiveThread.Start();
                    peer.SendThread.Start();
                }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        private void ReceiveLoop(Peer peer)
        {
            var header = new byte[TerrainFrame.HeaderBytes];
            try
            {
                while (m_running && peer.Alive)
                {
                    if (!ReadExact(peer.Stream, header, TerrainFrame.HeaderBytes))
                    {
                        Fail(peer, "对端关闭"); return;
                    }
                    if (!TerrainFrame.TryReadHeader(header, 0, out int len, out long seq, out MessageType type))
                    {
                        peer.Stats.ProtocolViolations++;
                        Fail(peer, "协议违规（帧头非法）"); return;
                    }
                    var payload = new byte[len];
                    if (len > 0 && !ReadExact(peer.Stream, payload, len))
                    {
                        Fail(peer, "负载截断"); return;
                    }
                    peer.Stats.FramesReceived++;
                    peer.Stats.BytesReceived += TerrainFrame.HeaderBytes + len;
                    peer.LastReceivedSequence = seq;
                    FrameReceived?.Invoke(new ReceivedFrame(peer.Id, seq, type, payload));
                }
            }
            catch (Exception e) { Fail(peer, e.GetType().Name); }
        }

        private void SendLoop(Peer peer)
        {
            try
            {
                while (m_running && peer.Alive)
                {
                    byte[] frame = null;
                    lock (peer.SendQueue)
                    {
                        if (peer.SendQueue.Count > 0) frame = peer.SendQueue.Dequeue();
                    }
                    if (frame == null)
                    {
                        peer.SendWake.Wait(5);
                        peer.SendWake.Reset();
                        continue;
                    }
                    peer.Stream.Write(frame, 0, frame.Length);
                    peer.Stats.FramesSent++;
                    peer.Stats.BytesSent += frame.Length;
                    lock (peer.SendQueue)
                    {
                        if (peer.SendQueue.Count < m_queueCapacity && peer.State == ChannelState.Backpressured)
                            SetState(peer, ChannelState.Connected, "队列回落");
                    }
                }
            }
            catch (Exception e) { Fail(peer, e.GetType().Name); }
        }

        /// <summary>
        /// 入队一帧。返回 false = 队列已满（**显式背压**，调用方必须处理；不丢包）。
        /// </summary>
        public bool TrySend(int clientId, MessageType type, byte[] payload)
        {
            Peer peer = Find(clientId);
            if (peer == null || !peer.Alive) return false;
            byte[] frame = TerrainFrame.Encode(Interlocked.Increment(ref m_sendSequence), type, payload);
            lock (peer.SendQueue)
            {
                if (peer.SendQueue.Count >= m_queueCapacity)
                {
                    peer.Stats.BackpressureEvents++;
                    lock (peer.SendQueue) peer.Stats.SendQueueHigh = Math.Max(peer.Stats.SendQueueHigh, peer.SendQueue.Count);
                    SetState(peer, ChannelState.Backpressured, "发送队列已满（显式背压）");
                    return false;
                }
                peer.SendQueue.Enqueue(frame);
                peer.Stats.SendQueueHigh = Math.Max(peer.Stats.SendQueueHigh, peer.SendQueue.Count);
            }
            peer.SendWake.Set();
            return true;
        }

        public PeerInfo[] ListPeers()
        {
            lock (m_gate)
            {
                var list = new PeerInfo[m_peers.Count];
                for (int i = 0; i < m_peers.Count; i++)
                {
                    Peer p = m_peers[i];
                    list[i] = new PeerInfo(p.Id, p.Kind, p.Name, p.State, p.Alive, p.Stats);
                }
                return list;
            }
        }

        public ChannelStats StatsOf(int clientId) => Find(clientId)?.Stats;

        public ChannelState StateOf(int clientId) => Find(clientId)?.State ?? ChannelState.Disconnected;

        public void SetPeerIdentity(int clientId, ClientKind kind, string name)
        {
            Peer peer = Find(clientId);
            if (peer == null) return;
            peer.Kind = kind;
            peer.Name = name ?? string.Empty;
        }

        private Peer Find(int clientId)
        {
            lock (m_gate)
            {
                for (int i = 0; i < m_peers.Count; i++) if (m_peers[i].Id == clientId) return m_peers[i];
            }
            return null;
        }

        private void SetState(Peer peer, ChannelState state, string detail)
        {
            if (peer.State == state) return;
            peer.State = state;
            PeerStateChanged?.Invoke(peer.Id, state, detail);
        }

        private void Fail(Peer peer, string detail)
        {
            if (!peer.Alive) return;
            peer.Alive = false;
            SetState(peer, ChannelState.Failed, detail);
            try { peer.Tcp.Close(); } catch (Exception) { }
            peer.SendWake.Set();
        }

        internal static bool ReadExact(NetworkStream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        public void Dispose()
        {
            m_running = false;
            try { m_listener.Stop(); } catch (Exception) { }
            lock (m_gate)
            {
                foreach (Peer peer in m_peers)
                {
                    peer.Alive = false;
                    try { peer.Tcp.Close(); } catch (Exception) { }
                    peer.SendWake.Set();
                }
                m_peers.Clear();
            }
        }
    }

    public readonly struct PeerInfo
    {
        public PeerInfo(int id, ClientKind kind, string name, ChannelState state, bool alive, ChannelStats stats)
        {
            Id = id; Kind = kind; Name = name; State = state; Alive = alive; Stats = stats;
        }
        public int Id { get; }
        public ClientKind Kind { get; }
        public string Name { get; }
        public ChannelState State { get; }
        public bool Alive { get; }
        public ChannelStats Stats { get; }
    }

    /// <summary>TCP 通道（客户端侧）。</summary>
    public sealed class TcpTerrainClient : IDisposable
    {
        private readonly string m_host;
        private readonly int m_port;
        private readonly int m_queueCapacity;
        private TcpClient m_tcp;
        private NetworkStream m_stream;
        private Thread m_receiveThread;
        private Thread m_sendThread;
        private readonly Queue<byte[]> m_sendQueue = new Queue<byte[]>();
        private readonly ManualResetEventSlim m_sendWake = new ManualResetEventSlim(false);
        private long m_sendSequence;
        private volatile bool m_running;

        public TcpTerrainClient(string host, int port, int queueCapacity = 256)
        {
            m_host = host; m_port = port; m_queueCapacity = Math.Max(8, queueCapacity);
        }

        public ChannelStats Stats { get; } = new ChannelStats();
        public ChannelState State { get; private set; } = ChannelState.Disconnected;
        public event Action<ReceivedFrame> FrameReceived;
        public event Action<ChannelState, string> StateChanged;

        /// <summary>
        /// 最近一次状态变化的原因（Failed 时就是"对端关闭 / 协议违规 / IOException"这类实因）。
        /// 上层拆链重连时把它写进日志 —— 断链原因**必须**有据可查
        /// （实机故障：对端掉线后两端状态行只是"不再变化"，日志一个字都没有，排查只能靠猜）。
        /// </summary>
        public string StateDetail { get; private set; } = string.Empty;

        /// <summary>
        /// 这条连接现在还能不能收发：只有 Connected / Backpressured 算活着。
        /// 收线程或发线程一旦失败（<see cref="SetState"/> → Failed）它就是 false。
        /// 上层**绝不能**拿"客户端对象还在"当"连接还在"（实机故障：对端掉线后位置快照一直往
        /// 死连接里塞、posSent 继续涨，主机一个字节都收不到）。
        /// </summary>
        public bool IsAlive => m_running &&
            (State == ChannelState.Connected || State == ChannelState.Backpressured);

        public bool Connect(int timeoutMs = 3000)
        {
            try
            {
                m_tcp = new TcpClient { NoDelay = true };
                if (!m_tcp.ConnectAsync(m_host, m_port).Wait(timeoutMs))
                {
                    SetState(ChannelState.Failed, "连接超时");
                    return false;
                }
                m_stream = m_tcp.GetStream();
                m_running = true;
                SetState(ChannelState.Connected, "已连接");
                m_receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "SuMP-CRecv" };
                m_sendThread = new Thread(SendLoop) { IsBackground = true, Name = "SuMP-CSend" };
                m_receiveThread.Start();
                m_sendThread.Start();
                return true;
            }
            catch (Exception e)
            {
                SetState(ChannelState.Failed, e.GetType().Name);
                return false;
            }
        }

        private void ReceiveLoop()
        {
            var header = new byte[TerrainFrame.HeaderBytes];
            try
            {
                while (m_running)
                {
                    if (!TcpTerrainServer.ReadExact(m_stream, header, TerrainFrame.HeaderBytes))
                    {
                        SetState(ChannelState.Failed, "对端关闭"); return;
                    }
                    if (!TerrainFrame.TryReadHeader(header, 0, out int len, out long seq, out MessageType type))
                    {
                        Stats.ProtocolViolations++;
                        SetState(ChannelState.Failed, "协议违规"); return;
                    }
                    var payload = new byte[len];
                    if (len > 0 && !TcpTerrainServer.ReadExact(m_stream, payload, len))
                    {
                        SetState(ChannelState.Failed, "负载截断"); return;
                    }
                    Stats.FramesReceived++;
                    Stats.BytesReceived += TerrainFrame.HeaderBytes + len;
                    FrameReceived?.Invoke(new ReceivedFrame(0, seq, type, payload));
                }
            }
            catch (Exception e) { SetState(ChannelState.Failed, e.GetType().Name); }
        }

        private void SendLoop()
        {
            try
            {
                while (m_running)
                {
                    byte[] frame = null;
                    lock (m_sendQueue) { if (m_sendQueue.Count > 0) frame = m_sendQueue.Dequeue(); }
                    if (frame == null) { m_sendWake.Wait(5); m_sendWake.Reset(); continue; }
                    m_stream.Write(frame, 0, frame.Length);
                    Stats.FramesSent++;
                    Stats.BytesSent += frame.Length;
                    lock (m_sendQueue)
                    {
                        if (m_sendQueue.Count < m_queueCapacity && State == ChannelState.Backpressured)
                            SetState(ChannelState.Connected, "队列回落");
                    }
                }
            }
            catch (Exception e) { SetState(ChannelState.Failed, e.GetType().Name); }
        }

        public bool TrySend(MessageType type, byte[] payload)
        {
            // 死连接不假装"发成功"：Failed/Disconnected 之后入队只会越堆越多，
            // 调用方的 posSent 会继续涨而主机永远收不到（实机故障就是这样静默的）。
            if (!IsAlive) return false;
            byte[] frame = TerrainFrame.Encode(Interlocked.Increment(ref m_sendSequence), type, payload);
            lock (m_sendQueue)
            {
                if (m_sendQueue.Count >= m_queueCapacity)
                {
                    Stats.BackpressureEvents++;
                    SetState(ChannelState.Backpressured, "发送队列已满（显式背压）");
                    return false;
                }
                m_sendQueue.Enqueue(frame);
            }
            m_sendWake.Set();
            return true;
        }

        private void SetState(ChannelState state, string detail)
        {
            if (State == state) return;
            State = state;
            if (!string.IsNullOrEmpty(detail)) StateDetail = detail;
            StateChanged?.Invoke(state, detail);
        }

        /// <summary>等待发送队列清空（测试/关闭前用）。</summary>
        public bool WaitDrained(int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                lock (m_sendQueue) { if (m_sendQueue.Count == 0) return true; }
                Thread.Sleep(2);
                waited += 2;
            }
            return false;
        }

        public void Dispose()
        {
            m_running = false;
            try { m_tcp?.Close(); } catch (Exception) { }
            m_sendWake.Set();
            SetState(ChannelState.Disconnected, "已关闭");
        }
    }
}
