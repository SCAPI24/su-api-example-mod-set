using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using SuMultiplayer.Protocol;
using SuMultiplayer.Protocol.Terrain;
using SuMultiplayer.Engine;

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

    /// <summary>主连接之外的可靠 TCP 通道种类。</summary>
    public enum SecondaryChannelKind : uint
    {
        Circuit = 0,
        Terrain = 1
    }

    public static class TerrainFrame
    {
        public const int HeaderBytes = 16;
        public const int MaxPayloadBytes = 60 * 1024;

        public static byte[] Encode(long sequence, MessageType type, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            Validate(type, payload);
            var buffer = new byte[HeaderBytes + payload.Length];
            EncodeTo(buffer, sequence, type, payload);
            return buffer;
        }

        /// <summary>
        /// 把一帧编码进**调用方提供的**缓冲（池化发送路径用，避免每帧 new byte[]）。
        /// 返回帧总长度；缓冲不足抛 <see cref="ArgumentException"/>。
        /// 校验 / 异常口径与 <see cref="Encode"/> 完全一致（payload 过大 / 类型 id 非法）。
        /// </summary>
        public static int EncodeTo(byte[] buffer, long sequence, MessageType type, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            Validate(type, payload);
            int typeId = (int)type;
            int length = HeaderBytes + payload.Length;
            if (buffer == null || buffer.Length < length)
                throw new ArgumentException("帧缓冲不足（需要 " + length + " 字节）", nameof(buffer));
            WriteInt32(buffer, 0, payload.Length);
            WriteInt64(buffer, 4, sequence);
            WriteUInt16(buffer, 12, (ushort)typeId);  // 紧凑 id（uint16：内置 + 第三方）
            WriteUInt16(buffer, 14, 0);              // flags
            Buffer.BlockCopy(payload, 0, buffer, HeaderBytes, payload.Length);
            return length;
        }

        /// <summary>帧头校验（payload 上限 / 消息类型 id）；发送前先校验可以避免为非法负载借大缓冲。</summary>
        public static void Validate(MessageType type, byte[] payload)
        {
            if (payload.Length > MaxPayloadBytes) throw new ArgumentException("payload 过大");
            int typeId = (int)type;
            if (!MessageRegistry.IsValidId(typeId))
                throw new ArgumentOutOfRangeException(nameof(type), "消息类型 id " + typeId +
                    " 不在 1.." + MessageRegistry.ThirdPartyMax + " 内");
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
        private sealed class TypeCounters
        {
            public long FramesSent;
            public long BytesSent;
            public long FramesReceived;
            public long BytesReceived;
        }

        private readonly TypeCounters[] m_byType = new TypeCounters[2000];

        public long FramesSent;
        public long FramesReceived;
        public long BytesSent;
        public long BytesReceived;
        public long BackpressureEvents;
        public long ProtocolViolations;
        public long SendQueueHigh;
        public long Dropped;              // 设计上恒为 0

        public void RecordSent(MessageType type, int bytes)
        {
            Interlocked.Increment(ref FramesSent);
            Interlocked.Add(ref BytesSent, bytes);
            TypeCounters counters = GetTypeCounters(type);
            if (counters == null) return;
            Interlocked.Increment(ref counters.FramesSent);
            Interlocked.Add(ref counters.BytesSent, bytes);
        }

        public void RecordReceived(MessageType type, int bytes)
        {
            Interlocked.Increment(ref FramesReceived);
            Interlocked.Add(ref BytesReceived, bytes);
            TypeCounters counters = GetTypeCounters(type);
            if (counters == null) return;
            Interlocked.Increment(ref counters.FramesReceived);
            Interlocked.Add(ref counters.BytesReceived, bytes);
        }

        private TypeCounters GetTypeCounters(MessageType type)
        {
            int id = (ushort)type;
            if (id <= 0 || id >= m_byType.Length) return null;
            TypeCounters counters = Volatile.Read(ref m_byType[id]);
            if (counters != null) return counters;
            var created = new TypeCounters();
            return Interlocked.CompareExchange(ref m_byType[id], created, null) ?? created;
        }

        public void RecordBackpressure() => Interlocked.Increment(ref BackpressureEvents);

        public void RecordProtocolViolation() => Interlocked.Increment(ref ProtocolViolations);

        public void ObserveSendQueue(long count)
        {
            long current = Interlocked.Read(ref SendQueueHigh);
            while (count > current)
            {
                long observed = Interlocked.CompareExchange(ref SendQueueHigh, count, current);
                if (observed == current) return;
                current = observed;
            }
        }

        public long ReadFramesSent() => Interlocked.Read(ref FramesSent);
        public long ReadFramesReceived() => Interlocked.Read(ref FramesReceived);
        public long ReadBytesSent() => Interlocked.Read(ref BytesSent);
        public long ReadBytesReceived() => Interlocked.Read(ref BytesReceived);
        public long ReadBackpressureEvents() => Interlocked.Read(ref BackpressureEvents);
        public long ReadProtocolViolations() => Interlocked.Read(ref ProtocolViolations);
        public long ReadSendQueueHigh() => Interlocked.Read(ref SendQueueHigh);

        public MessageTypeTrafficSnapshot[] SnapshotByMessageType(int maximum = 32)
        {
            var snapshot = new List<MessageTypeTrafficSnapshot>(32);
            for (ushort id = 1; id < m_byType.Length; id++)
            {
                TypeCounters counters = Volatile.Read(ref m_byType[id]);
                if (counters == null) continue;
                snapshot.Add(new MessageTypeTrafficSnapshot(id,
                    Interlocked.Read(ref counters.FramesSent),
                    Interlocked.Read(ref counters.BytesSent),
                    Interlocked.Read(ref counters.FramesReceived),
                    Interlocked.Read(ref counters.BytesReceived)));
            }
            snapshot.Sort((a, b) =>
            {
                long aBytes = a.BytesSent + a.BytesReceived;
                long bBytes = b.BytesSent + b.BytesReceived;
                return bBytes.CompareTo(aBytes);
            });
            int count = Math.Max(0, Math.Min(maximum, snapshot.Count));
            if (count != snapshot.Count) snapshot.RemoveRange(count, snapshot.Count - count);
            return snapshot.ToArray();
        }

        public override string ToString() =>
            "sent=" + ReadFramesSent() + " recv=" + ReadFramesReceived() +
            " bytes=" + ReadBytesSent() + "/" + ReadBytesReceived() +
            " backpressure=" + ReadBackpressureEvents() + " violations=" +
            ReadProtocolViolations() + " dropped=" + Dropped;
    }

    public readonly struct MessageTypeTrafficSnapshot
    {
        public MessageTypeTrafficSnapshot(ushort typeId, long framesSent, long bytesSent,
            long framesReceived, long bytesReceived)
        {
            TypeId = typeId;
            FramesSent = framesSent;
            BytesSent = bytesSent;
            FramesReceived = framesReceived;
            BytesReceived = bytesReceived;
        }

        public ushort TypeId { get; }
        public long FramesSent { get; }
        public long BytesSent { get; }
        public long FramesReceived { get; }
        public long BytesReceived { get; }
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
    /// TCP 通道（服务端）：每客户端一条连接；接收 / 发送都是 **async 循环**（网络 IO 用
    /// <c>ReadAsync</c>/<c>WriteAsync</c>，不再每个连接开两条专属线程）。
    /// 发送走有界队列；队列满 = 显式 Backpressured，**绝不静默丢**。
    /// 帧缓冲从 <see cref="ArrayPool{T}.Shared"/> 借、写完立即归还（借还计数见
    /// <see cref="TransportDiagnostics"/>，<c>transportPoolLive</c> 必须归零）。
    /// 要点：<c>NoDelay</c>（否则最多攒 200 ms）；16 字节帧头 / 序号 / 队列上限 / 背压语义与旧实现一致。
    /// 演示页标记：NET02。
    /// </summary>
    public class TcpTerrainServer : IDisposable
    {
        private sealed class CircuitTicket
        {
            public int PrimaryClientId;
            public SecondaryChannelKind Kind;
            public long ExpiresAtMs;
        }

        private readonly struct SendFrame
        {
            public SendFrame(byte[] buffer, int length, MessageType type, TaskCompletionSource<bool> completion = null)
            {
                Buffer = buffer;
                Length = length;
                Type = type;
                Completion = completion;
            }
            public byte[] Buffer { get; }
            public int Length { get; }
            public MessageType Type { get; }
            public TaskCompletionSource<bool> Completion { get; }
        }

        private sealed class Peer
        {
            public int Id;
            public TcpClient Tcp;
            public NetworkStream Stream;
            public readonly Queue<SendFrame> SendQueue = new Queue<SendFrame>();
            public readonly SemaphoreSlim SendSignal = new SemaphoreSlim(0, int.MaxValue);
            public volatile bool Alive = true;
            public ChannelState State = ChannelState.Connected;
            public ClientKind Kind = ClientKind.Player;
            public string Name = string.Empty;
            public readonly ChannelStats Stats = new ChannelStats();
            public long LastReceivedSequence = -1;
            public bool ConnectionClosed;
            public bool IsCircuit;
            public bool IsTerrain;
            public int PrimaryClientId;
        }

        private readonly TcpListener m_listener;
        private readonly List<Peer> m_peers = new List<Peer>();
        private readonly object m_gate = new object();
        private readonly Dictionary<string, CircuitTicket> m_circuitTickets =
            new Dictionary<string, CircuitTicket>(StringComparer.Ordinal);
        private readonly Dictionary<int, Peer> m_circuitPeers = new Dictionary<int, Peer>();
        private readonly Dictionary<int, Peer> m_terrainPeers = new Dictionary<int, Peer>();
        private readonly ConcurrentQueue<Action> m_circuitCallbacks =
            new ConcurrentQueue<Action>();
        private readonly ConcurrentQueue<Action> m_terrainCallbacks =
            new ConcurrentQueue<Action>();
        private readonly int m_queueCapacity;
        private int m_nextClientId = 1;
        private volatile bool m_running;
        private long m_sendSequence;
        private bool m_listenerClosed;

        /// <summary>给派生的内存传输（自检用）留的无 socket 构造：不绑定监听端口，也不起任何线程。</summary>
        protected TcpTerrainServer(int queueCapacity)
        {
            m_queueCapacity = Math.Max(8, queueCapacity);
        }

        /// <param name="bindAddress">
        /// 默认 <see cref="IPAddress.Any"/> —— **局域网内其它设备必须能连进来**。
        /// （只在本机自测时才可以传 Loopback；调试端那种只服务本机网页的场景另说。）
        /// </param>
        public TcpTerrainServer(int port, int queueCapacity = 256, IPAddress bindAddress = null)
            : this(queueCapacity)
        {
            m_listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
            TransportDiagnostics.OnListenerOpened();
            TransportDiagnostics.OnSocketOpened();
        }

        public virtual int Port => ((IPEndPoint)m_listener.LocalEndpoint).Port;
        public virtual int QueueCapacity => m_queueCapacity;

        /// <summary>收到一帧（在 async 接收循环的续体上回调；上层只应入队，不在这里碰引擎）。</summary>
        public event Action<ReceivedFrame> FrameReceived;
        public event Action<int, ChannelState, string> PeerStateChanged;
        public event Action<int, ReceivedFrame> CircuitFrameReceived;
        public event Action<int> CircuitPeerAttached;
        public event Action<int, string> CircuitPeerDetached;
        public event Action<int, ChannelState, string> CircuitPeerStateChanged;
        public event Action<int, ReceivedFrame> TerrainFrameReceived;
        public event Action<int> TerrainPeerAttached;
        public event Action<int, string> TerrainPeerDetached;
        public event Action<int, ChannelState, string> TerrainPeerStateChanged;

        /// <summary>派生传输（自检的内存实现）用它把帧交给订阅者，走的是同一个事件契约。</summary>
        protected void RaiseFrameReceived(ReceivedFrame frame) => FrameReceived?.Invoke(frame);

        /// <summary>派生传输用它抛状态变化（与 socket 实现同一套语义）。</summary>
        protected void RaisePeerStateChanged(int clientId, ChannelState state, string detail) =>
            PeerStateChanged?.Invoke(clientId, state, detail);

        public virtual void Start()
        {
            m_listener.Start();
            m_running = true;
            // fire-and-forget：循环内部处理所有异常；不在 Start 里阻塞、也不占专属线程。
            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (m_running)
            {
                TcpClient tcp;
                try
                {
                    tcp = await m_listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    ReportProblem("accept 失败", e);
                    break;
                }
                if (!m_running)
                {
                    try { tcp.Close(); } catch (Exception) { }
                    break;
                }
                try
                {
                    tcp.NoDelay = true;
                    // 空闲探测：客户端 WiFi 抖动 / 省电休眠后，没有 KeepAlive 的连接会一直挂在
                    // "半开"状态（读阻塞、写成功），上层看不出对端已经不可达。见 EnableTcpKeepAlive。
                    EnableTcpKeepAlive(tcp.Client, "server-accept");
                    var peer = new Peer
                    {
                        Id = Interlocked.Increment(ref m_nextClientId) - 1,
                        Tcp = tcp,
                        Stream = tcp.GetStream()
                    };
                    lock (m_gate) m_peers.Add(peer);
                    TransportDiagnostics.OnConnectionCreated();
                    // 两个 async 循环：没有专属线程，续体跑在线程池上。
                    _ = ReceiveLoopAsync(peer);
                    _ = SendLoopAsync(peer);
                }
                catch (Exception e)
                {
                    ReportProblem("登记新连接失败", e);
                    try { tcp.Close(); } catch (Exception) { }
                }
            }
        }

        private async Task ReceiveLoopAsync(Peer peer)
        {
            // 帧头缓冲按连接借一次、循环复用；负载是消息所有权，交给上层（不池化）。
            byte[] header = TransportDiagnostics.RentBuffer(TerrainFrame.HeaderBytes);
            try
            {
                while (m_running && peer.Alive)
                {
                    if (!await ReadExactAsync(peer.Stream, header, TerrainFrame.HeaderBytes).ConfigureAwait(false))
                    {
                        Fail(peer, "对端关闭"); return;
                    }
                    if (!TerrainFrame.TryReadHeader(header, 0, out int len, out long seq, out MessageType type))
                    {
                        peer.Stats.RecordProtocolViolation();
                        Fail(peer, "协议违规（帧头非法）"); return;
                    }
                    byte[] payload = len == 0 ? Array.Empty<byte>() : new byte[len];
                    if (len > 0 && !await ReadExactAsync(peer.Stream, payload, len).ConfigureAwait(false))
                    {
                        Fail(peer, "负载截断"); return;
                    }
                    peer.Stats.RecordReceived(type, TerrainFrame.HeaderBytes + len);
                    peer.LastReceivedSequence = seq;
                    if (peer.IsCircuit || peer.IsTerrain)
                    {
                        if (type == MessageType.CircuitAttach || type == MessageType.CircuitAttached)
                        {
                            peer.Stats.RecordProtocolViolation();
                            Fail(peer, "附属电路通道握手帧重复");
                            return;
                        }
                        int primaryClientId = peer.PrimaryClientId;
                        ReceivedFrame received = new ReceivedFrame(primaryClientId,
                            seq, type, payload);
                        if (peer.IsTerrain)
                            m_terrainCallbacks.Enqueue(() =>
                                TerrainFrameReceived?.Invoke(primaryClientId, received));
                        else
                            m_circuitCallbacks.Enqueue(() =>
                                CircuitFrameReceived?.Invoke(primaryClientId, received));
                    }
                    else if (peer.LastReceivedSequence == seq &&
                        peer.Stats.ReadFramesReceived() == 1 && type == MessageType.CircuitAttach)
                    {
                        if (!await TryAttachCircuitAsync(peer, seq, payload).ConfigureAwait(false))
                            return;
                    }
                    else
                    {
                        if (type == MessageType.CircuitAttach || type == MessageType.CircuitAttached)
                        {
                            peer.Stats.RecordProtocolViolation();
                            Fail(peer, "附属电路握手帧只能作为连接首帧");
                            return;
                        }
                        RaiseFrameReceived(new ReceivedFrame(peer.Id, seq, type, payload));
                    }
                }
            }
            catch (Exception e) { Fail(peer, e.GetType().Name); }
            finally { TransportDiagnostics.ReturnBuffer(header); }
        }

        private async Task<bool> TryAttachCircuitAsync(Peer peer, long sequence, byte[] payload)
        {
            int primaryClientId = 0;
            bool accepted = false;
            string reason = string.Empty;
            SecondaryChannelKind kind = SecondaryChannelKind.Circuit;
            try
            {
                CircuitAttach request = CircuitAttach.Parser.ParseFrom(payload ?? Array.Empty<byte>());
                primaryClientId = request.PrimaryClientId;
                kind = request.ChannelKind == (uint)SecondaryChannelKind.Terrain
                    ? SecondaryChannelKind.Terrain : SecondaryChannelKind.Circuit;
                if (primaryClientId <= 0 || request.Ticket == null || request.Ticket.IsEmpty)
                {
                    reason = "invalid-attach";
                }
                else
                {
                    string ticketKey = Convert.ToBase64String(request.Ticket.ToByteArray());
                    lock (m_gate)
                    {
                        RemoveExpiredCircuitTicketsLocked(Environment.TickCount64);
                        if (!m_circuitTickets.TryGetValue(ticketKey, out CircuitTicket ticket))
                        {
                            reason = "ticket-invalid-or-expired";
                        }
                        else
                        {
                            m_circuitTickets.Remove(ticketKey);
                            Peer primary = FindLocked(primaryClientId);
                            if (ticket.PrimaryClientId != primaryClientId || ticket.Kind != kind)
                                reason = "ticket-client-mismatch";
                            else if (primary == null || !primary.Alive || primary.IsCircuit || primary.IsTerrain ||
                                (primary.Kind != ClientKind.Player &&
                                 !(kind == SecondaryChannelKind.Terrain && primary.Kind == ClientKind.Debug)))
                                reason = "primary-player-unavailable";
                            else if (kind == SecondaryChannelKind.Circuit && m_circuitPeers.ContainsKey(primaryClientId))
                                reason = "circuit-channel-already-attached";
                            else if (kind == SecondaryChannelKind.Terrain && m_terrainPeers.ContainsKey(primaryClientId))
                                reason = "terrain-channel-already-attached";
                            else
                            {
                                accepted = true;
                                peer.IsCircuit = kind == SecondaryChannelKind.Circuit;
                                peer.IsTerrain = kind == SecondaryChannelKind.Terrain;
                                peer.PrimaryClientId = primaryClientId;
                                if (kind == SecondaryChannelKind.Circuit)
                                    m_circuitPeers.Add(primaryClientId, peer);
                                else
                                    m_terrainPeers.Add(primaryClientId, peer);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                reason = "attach-malformed:" + e.GetType().Name;
            }

            var response = new CircuitAttached
            {
                PrimaryClientId = primaryClientId,
                Accepted = accepted,
                Reason = reason,
                ChannelKind = (uint)kind
            };
            byte[] responsePayload = response.ToByteArray();
            byte[] frame = TerrainFrame.Encode(sequence, MessageType.CircuitAttached, responsePayload);
            try
            {
                await peer.Stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
                peer.Stats.RecordSent(MessageType.CircuitAttached, frame.Length);
            }
            catch (Exception e)
            {
                Fail(peer, "附属 TCP 握手回执发送失败:" + e.GetType().Name);
                return false;
            }

            if (!accepted)
            {
                Fail(peer, "附属 TCP 握手拒绝:" + reason);
                return false;
            }
            if (kind == SecondaryChannelKind.Circuit)
                m_circuitCallbacks.Enqueue(() => CircuitPeerAttached?.Invoke(primaryClientId));
            else
                m_terrainCallbacks.Enqueue(() => TerrainPeerAttached?.Invoke(primaryClientId));
            return true;
        }

        private async Task SendLoopAsync(Peer peer)
        {
            try
            {
                while (m_running && peer.Alive)
                {
                    SendFrame frame = default;
                    bool hasFrame = false;
                    lock (peer.SendQueue)
                    {
                        if (peer.SendQueue.Count > 0)
                        {
                            frame = peer.SendQueue.Dequeue();
                            hasFrame = true;
                            // 与入队时的 Release 配对消费一个信号量计数。
                            peer.SendSignal.Wait(0);
                        }
                    }
                    if (!hasFrame)
                    {
                        // 旧实现是 5 ms 轮询唤醒；这里等同样的 5 ms，语义不变。
                        try { await peer.SendSignal.WaitAsync(5).ConfigureAwait(false); }
                        catch (ObjectDisposedException) { return; }
                        continue;
                    }
                    try
                    {
                        await peer.Stream.WriteAsync(frame.Buffer, 0, frame.Length).ConfigureAwait(false);
                        peer.Stats.RecordSent(frame.Type, frame.Length);
                        frame.Completion?.TrySetResult(true);
                        lock (peer.SendQueue)
                        {
                            if (peer.SendQueue.Count < m_queueCapacity && peer.State == ChannelState.Backpressured)
                                SetPeerState(peer, ChannelState.Connected, "队列回落");
                        }
                    }
                    finally
                    {
                        frame.Completion?.TrySetResult(false);
                        // 无论写完 / 抛异常，池化帧都必须归还（唯一归还点）。
                        TransportDiagnostics.ReturnBuffer(frame.Buffer);
                    }
                }
            }
            catch (Exception e) { Fail(peer, e.GetType().Name); }
        }

        /// <summary>
        /// 入队一帧。返回 false = 队列已满（**显式背压**，调用方必须处理；不丢包）。
        /// 帧缓冲来自池，所有权在入队成功时转交发送循环；失败 / 异常路径当场归还。
        /// </summary>
        public virtual bool TrySend(int clientId, MessageType type, byte[] payload)
        {
            Peer peer = Find(clientId);
            if (peer == null || !peer.Alive) return false;
            payload = payload ?? Array.Empty<byte>();
            long sequence = Interlocked.Increment(ref m_sendSequence);
            TerrainFrame.Validate(type, payload);          // 先校验再借缓冲（非法负载不占池）
            int length = TerrainFrame.HeaderBytes + payload.Length;
            byte[] frame = TransportDiagnostics.RentBuffer(length);
            bool enqueued = false;
            try
            {
                TerrainFrame.EncodeTo(frame, sequence, type, payload);
                bool dead, full;
                lock (peer.SendQueue)
                {
                    dead = !peer.Alive;
                    full = !dead && peer.SendQueue.Count >= m_queueCapacity;
                    if (!dead && !full)
                    {
                        peer.SendQueue.Enqueue(new SendFrame(frame, length, type));
                        peer.Stats.ObserveSendQueue(peer.SendQueue.Count);
                        peer.SendSignal.Release();
                        enqueued = true;
                    }
                }
                if (dead) return false;
                if (full)
                {
                    lock (peer.SendQueue)
                    {
                        peer.Stats.RecordBackpressure();
                        peer.Stats.ObserveSendQueue(peer.SendQueue.Count);
                    }
                    SetPeerState(peer, ChannelState.Backpressured, "发送队列已满（显式背压）");
                    return false;
                }
                return true;
            }
            finally
            {
                // 成功入队时所有权转交发送循环；其余路径（背压 / 断开 / 编码异常）当场归还。
                if (!enqueued) TransportDiagnostics.ReturnBuffer(frame);
            }
        }

        public virtual byte[] CreateCircuitTicket(int primaryClientId)
        {
            return CreateSecondaryTicket(primaryClientId, SecondaryChannelKind.Circuit);
        }

        public virtual byte[] CreateTerrainTicket(int primaryClientId)
        {
            return CreateSecondaryTicket(primaryClientId, SecondaryChannelKind.Terrain);
        }

        private byte[] CreateSecondaryTicket(int primaryClientId, SecondaryChannelKind kind)
        {
            lock (m_gate)
            {
                Peer primary = FindLocked(primaryClientId);
                if (primary == null || !primary.Alive || primary.IsCircuit || primary.IsTerrain ||
                    (primary.Kind != ClientKind.Player &&
                     !(kind == SecondaryChannelKind.Terrain && primary.Kind == ClientKind.Debug)))
                    throw new InvalidOperationException("无法为不存在或非玩家主连接签发附属 ticket");
                RemoveExpiredCircuitTicketsLocked(Environment.TickCount64);
                byte[] bytes = new byte[32];
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                    rng.GetBytes(bytes);
                string ticket = Convert.ToBase64String(bytes);
                m_circuitTickets.Add(ticket, new CircuitTicket
                {
                    PrimaryClientId = primaryClientId,
                    Kind = kind,
                    ExpiresAtMs = Environment.TickCount64 + 30000
                });
                return bytes;
            }
        }

        public void PumpCircuitEvents()
        {
            while (m_circuitCallbacks.TryDequeue(out Action callback)) callback();
        }

        public void PumpTerrainEvents()
        {
            while (m_terrainCallbacks.TryDequeue(out Action callback)) callback();
        }

        public virtual bool TrySendCircuit(int primaryClientId, MessageType type, byte[] payload)
        {
            Peer peer;
            lock (m_gate)
                m_circuitPeers.TryGetValue(primaryClientId, out peer);
            if (peer == null || !peer.Alive || !peer.IsCircuit) return false;
            return Enqueue(peer, type, payload);
        }

        public virtual bool TrySendTerrain(int primaryClientId, MessageType type, byte[] payload)
        {
            Peer peer;
            lock (m_gate) m_terrainPeers.TryGetValue(primaryClientId, out peer);
            if (peer == null || !peer.Alive || !peer.IsTerrain) return false;
            return Enqueue(peer, type, payload);
        }

        public virtual bool IsCircuitAttached(int primaryClientId)
        {
            lock (m_gate)
                return m_circuitPeers.TryGetValue(primaryClientId, out Peer peer) &&
                    peer.Alive && peer.IsCircuit;
        }

        public virtual bool IsTerrainAttached(int primaryClientId)
        {
            lock (m_gate)
                return m_terrainPeers.TryGetValue(primaryClientId, out Peer peer) &&
                    peer.Alive && peer.IsTerrain;
        }

        public virtual bool DisconnectCircuitClient(int primaryClientId, string reason)
        {
            Peer peer;
            lock (m_gate)
            {
                if (!m_circuitPeers.TryGetValue(primaryClientId, out peer)) return false;
                m_circuitPeers.Remove(primaryClientId);
            }
            m_circuitCallbacks.Enqueue(() => CircuitPeerDetached?.Invoke(primaryClientId,
                reason ?? "host-disconnected"));
            Fail(peer, reason ?? "host-disconnected");
            return true;
        }

        public virtual bool DisconnectTerrainClient(int primaryClientId, string reason)
        {
            Peer peer;
            lock (m_gate)
            {
                if (!m_terrainPeers.TryGetValue(primaryClientId, out peer)) return false;
                m_terrainPeers.Remove(primaryClientId);
            }
            m_terrainCallbacks.Enqueue(() => TerrainPeerDetached?.Invoke(primaryClientId,
                reason ?? "host-disconnected"));
            Fail(peer, reason ?? "host-disconnected");
            return true;
        }

        /// <summary>
        /// 主机：把一条**主连接**连同它的两条附属连接一起踢掉（G6 / G7）。
        /// 顺序很重要：先尽力把 <c>Kicked</c> 发出去（让对端知道"别再重连"），
        /// 再断两条附属通道，最后关主连接。
        ///
        /// 为什么不能只关 socket：对端会把"连接被他端关闭"当成网络故障，进入断线重连窗口
        /// 一遍遍重试，直到窗口耗尽（30 秒白等）。<c>Kicked</c> 是明确的语义信号。
        /// 发送失败不阻断踢出流程（socket 可能已经死了）—— 那条路径下对端本来也收不到。
        /// </summary>
        public virtual void KickPrimaryClient(int clientId, string reason, bool superseded)
        {
            if (clientId <= 0) return;
            Peer peer = null;
            lock (m_gate)
            {
                for (int i = 0; i < m_peers.Count; i++)
                {
                    Peer candidate = m_peers[i];
                    if (candidate.Id != clientId || candidate.IsCircuit || candidate.IsTerrain)
                        continue;
                    peer = candidate;
                    break;
                }
            }
            if (peer == null || !peer.Alive) return;
            var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = false;
            if (peer.Alive)
            {
                try
                {
                    var message = new SuMultiplayer.Protocol.Kicked
                    {
                        Reason = reason ?? string.Empty,
                        Superseded = superseded
                    };
                    byte[] payload = message.ToByteArray();
                    int length = TerrainFrame.HeaderBytes + payload.Length;
                    byte[] frame = TransportDiagnostics.RentBuffer(length);
                    TerrainFrame.EncodeTo(frame, Interlocked.Increment(ref m_sendSequence),
                        MessageType.Kicked, payload);
                    lock (peer.SendQueue)
                    {
                        if (peer.Alive && peer.SendQueue.Count < m_queueCapacity)
                        {
                            peer.SendQueue.Enqueue(new SendFrame(frame, length, MessageType.Kicked, delivered));
                            peer.SendSignal.Release();
                            queued = true;
                        }
                    }
                    if (!queued) TransportDiagnostics.ReturnBuffer(frame);
                }
                catch (Exception e)
                {
                    ModLog.Info("[SuMP][net] Failed to send the kick signal (clientId=" +
                        clientId + "）：" + e.GetType().Name + " " + e.Message +
                        " → 仍然继续踢出（对端可能已不可达）");
                }
            }
            // Let the serialized send loop finish writing the kick before closing the socket.
            // Bound the wait: a stalled peer must never block the game thread indefinitely.
            _ = Task.Run(async () =>
            {
                if (queued) await Task.WhenAny(delivered.Task, Task.Delay(500)).ConfigureAwait(false);
                DisconnectTerrainClient(clientId, reason ?? "host-kicked");
                DisconnectCircuitClient(clientId, reason ?? "host-kicked");
                Fail(peer, reason ?? "host-kicked");
            });
        }

        private bool Enqueue(Peer peer, MessageType type, byte[] payload)
        {
            if (peer == null || !peer.Alive) return false;
            payload = payload ?? Array.Empty<byte>();
            long sequence = Interlocked.Increment(ref m_sendSequence);
            TerrainFrame.Validate(type, payload);
            int length = TerrainFrame.HeaderBytes + payload.Length;
            byte[] frame = TransportDiagnostics.RentBuffer(length);
            bool enqueued = false;
            try
            {
                TerrainFrame.EncodeTo(frame, sequence, type, payload);
                bool dead, full;
                lock (peer.SendQueue)
                {
                    dead = !peer.Alive;
                    full = !dead && peer.SendQueue.Count >= m_queueCapacity;
                    if (!dead && !full)
                    {
                        peer.SendQueue.Enqueue(new SendFrame(frame, length, type));
                        peer.Stats.ObserveSendQueue(peer.SendQueue.Count);
                        peer.SendSignal.Release();
                        enqueued = true;
                    }
                }
                if (dead) return false;
                if (full)
                {
                    peer.Stats.RecordBackpressure();
                    SetPeerState(peer, ChannelState.Backpressured, "发送队列已满（显式背压）");
                    return false;
                }
                return true;
            }
            finally
            {
                if (!enqueued) TransportDiagnostics.ReturnBuffer(frame);
            }
        }

        public virtual PeerInfo[] ListPeers()
        {
            lock (m_gate)
            {
                var list = new List<PeerInfo>(m_peers.Count);
                for (int i = 0; i < m_peers.Count; i++)
                {
                    Peer p = m_peers[i];
                    if (!p.IsCircuit)
                    {
                        if (!p.IsTerrain)
                        list.Add(new PeerInfo(p.Id, p.Kind, p.Name, p.State, p.Alive, p.Stats));
                    }
                }
                return list.ToArray();
            }
        }

        public virtual ChannelStats StatsOf(int clientId) => Find(clientId)?.Stats;

        public virtual ChannelStats CircuitStatsOf(int primaryClientId)
        {
            lock (m_gate)
                return m_circuitPeers.TryGetValue(primaryClientId, out Peer peer) &&
                    peer.Alive && peer.IsCircuit ? peer.Stats : null;
        }

        public virtual ChannelStats TerrainStatsOf(int primaryClientId)
        {
            lock (m_gate)
                return m_terrainPeers.TryGetValue(primaryClientId, out Peer peer) &&
                    peer.Alive && peer.IsTerrain ? peer.Stats : null;
        }

        public virtual ChannelState CircuitStateOf(int primaryClientId)
        {
            lock (m_gate)
                return m_circuitPeers.TryGetValue(primaryClientId, out Peer peer) &&
                    peer.Alive && peer.IsCircuit ? peer.State : ChannelState.Disconnected;
        }

        public virtual ChannelState TerrainStateOf(int primaryClientId)
        {
            lock (m_gate)
                return m_terrainPeers.TryGetValue(primaryClientId, out Peer peer) &&
                    peer.Alive && peer.IsTerrain ? peer.State : ChannelState.Disconnected;
        }

        public virtual ClientKind? KindOf(int clientId) => Find(clientId)?.Kind;

        public virtual ChannelState StateOf(int clientId) => Find(clientId)?.State ?? ChannelState.Disconnected;

        /// <summary>
        /// 主机主动断开一个玩家端。关掉 TCP 后由接收循环抛 ClientLeft，上层清理角色/记录。
        /// 找不到该 clientId = 明确失败（已经被断开的连接不重复算成功）。
        /// </summary>
        public virtual bool DisconnectClient(int clientId, out string reason)
        {
            Peer peer = Find(clientId);
            if (peer == null)
            {
                reason = "没有 clientId=" + clientId + " 的连接（可能已经断开）";
                return false;
            }
            try
            {
                if (!peer.Alive || peer.IsCircuit || peer.IsTerrain)
                {
                    reason = "Player connection is no longer active.";
                    return false;
                }
                KickPrimaryClient(clientId, "You were removed by the host.", false);
                reason = null;
                return true;
            }
            catch (Exception e)
            {
                reason = "关闭 clientId=" + clientId + " 失败（" + e.GetType().Name + " " +
                    e.Message + "）";
                return false;
            }
        }

        public virtual void SetPeerIdentity(int clientId, ClientKind kind, string name)
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

        private Peer FindLocked(int clientId)
        {
            for (int i = 0; i < m_peers.Count; i++)
                if (m_peers[i].Id == clientId) return m_peers[i];
            return null;
        }

        private void RemoveExpiredCircuitTicketsLocked(long nowMs)
        {
            List<string> expired = null;
            foreach (KeyValuePair<string, CircuitTicket> pair in m_circuitTickets)
            {
                if (pair.Value.ExpiresAtMs > nowMs) continue;
                if (expired == null) expired = new List<string>();
                expired.Add(pair.Key);
            }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++) m_circuitTickets.Remove(expired[i]);
        }

        private void CloseCircuitForPrimary(int primaryClientId, string reason)
        {
            Peer circuit = null;
            Peer terrain = null;
            lock (m_gate)
            {
                m_circuitPeers.TryGetValue(primaryClientId, out circuit);
                if (circuit != null) m_circuitPeers.Remove(primaryClientId);
                m_terrainPeers.TryGetValue(primaryClientId, out terrain);
                if (terrain != null) m_terrainPeers.Remove(primaryClientId);
                RemoveTicketsForPrimaryLocked(primaryClientId);
            }
            if (circuit != null)
            {
                m_circuitCallbacks.Enqueue(() => CircuitPeerDetached?.Invoke(primaryClientId, reason));
                Fail(circuit, reason);
            }
            if (terrain != null)
            {
                m_terrainCallbacks.Enqueue(() => TerrainPeerDetached?.Invoke(primaryClientId, reason));
                Fail(terrain, reason);
            }
        }

        private void RemoveTicketsForPrimaryLocked(int primaryClientId)
        {
            List<string> remove = null;
            foreach (KeyValuePair<string, CircuitTicket> pair in m_circuitTickets)
            {
                if (pair.Value.PrimaryClientId != primaryClientId) continue;
                if (remove == null) remove = new List<string>();
                remove.Add(pair.Key);
            }
            if (remove == null) return;
            for (int i = 0; i < remove.Count; i++) m_circuitTickets.Remove(remove[i]);
        }

        /// <summary>状态变化的唯一入口（派生传输不改这一处，直接复用）。</summary>
        private void SetPeerState(Peer peer, ChannelState state, string detail)
        {
            if (peer.State == state) return;
            peer.State = state;
            if (peer.IsCircuit || peer.IsTerrain)
            {
                int primaryClientId = peer.PrimaryClientId;
                if (peer.IsTerrain)
                    m_terrainCallbacks.Enqueue(() => TerrainPeerStateChanged?.Invoke(
                        primaryClientId, state, detail));
                else
                    m_circuitCallbacks.Enqueue(() => CircuitPeerStateChanged?.Invoke(
                        primaryClientId, state, detail));
            }
            else
                RaisePeerStateChanged(peer.Id, state, detail);
        }

        private void Fail(Peer peer, string detail)
        {
            if (!peer.Alive) return;
            peer.Alive = false;
            SetPeerState(peer, ChannelState.Failed, detail);
            try { peer.Tcp.Close(); } catch (Exception) { }
            peer.SendSignal.Release();
            DrainSendQueue(peer);
            if (!peer.ConnectionClosed) { peer.ConnectionClosed = true; TransportDiagnostics.OnConnectionClosed(); }
            if (peer.IsCircuit || peer.IsTerrain)
            {
                bool removed;
                lock (m_gate)
                {
                    if (peer.IsTerrain)
                    {
                        removed = m_terrainPeers.TryGetValue(peer.PrimaryClientId, out Peer current) &&
                            ReferenceEquals(current, peer);
                        if (removed) m_terrainPeers.Remove(peer.PrimaryClientId);
                    }
                    else
                    {
                        removed = m_circuitPeers.TryGetValue(peer.PrimaryClientId, out Peer current) &&
                            ReferenceEquals(current, peer);
                        if (removed) m_circuitPeers.Remove(peer.PrimaryClientId);
                    }
                }
                if (removed)
                {
                    int primaryClientId = peer.PrimaryClientId;
                    if (peer.IsTerrain)
                        m_terrainCallbacks.Enqueue(() =>
                            TerrainPeerDetached?.Invoke(primaryClientId, detail));
                    else
                        m_circuitCallbacks.Enqueue(() =>
                            CircuitPeerDetached?.Invoke(primaryClientId, detail));
                }
            }
            else
            {
                CloseCircuitForPrimary(peer.Id, "primary-disconnected:" + detail);
            }
        }

        /// <summary>把队列里还没发出去的池化帧全部归还（断开 / 关闭时调用，避免池泄漏）。</summary>
        private void DrainSendQueue(Peer peer)
        {
            while (true)
            {
                SendFrame frame;
                lock (peer.SendQueue)
                {
                    if (peer.SendQueue.Count == 0) return;
                    frame = peer.SendQueue.Dequeue();
                }
                TransportDiagnostics.ReturnBuffer(frame.Buffer);
                frame.Completion?.TrySetResult(false);
            }
        }

        // ---- TCP KeepAlive ----
        //
        // 为什么必须开（2026-11-06 实机问题）：
        //   · 客户端/服务端都**没有**配置过 KeepAlive，连接空闲时操作系统不发任何探测包；
        //   · WiFi 抖动 / 省电休眠 / AP 空闲回收 NAT 映射之后，连接会进入"半开"状态：
        //     读循环一直阻塞、发送仍然"成功"（写进内核缓冲），上层完全看不出对端已经
        //     不在了 —— 用户看到的就是"没操作时突然 Connection to the host was lost"；
        //   · 有流量时不会出问题（上行包本身起保活作用），所以症状是"不操作时才断"。
        // 开启后：空闲超过 KeepAliveTime 就发探测包（保活 + 让 NAT 映射不过期），
        // 连续 KeepAliveRetryCount 次无响应即由内核判死 → 读循环立刻失败 → 上层按既有
        // 路径重连 / 退出，而不是无限期挂在半开连接上。
        private const int KeepAliveTimeSeconds = 30;      // 空闲多久开始探测
        private const int KeepAliveIntervalSeconds = 5;   // 探测间隔
        private const int KeepAliveRetryCount = 3;        // 连续几次无响应判死（30+5×3=45s 内）

        /// <summary>
        /// 给已建立（或刚 accept）的 socket 打开 TCP KeepAlive。
        ///
        /// 平台差异（这是本方法必须容错的原因，不是"静默兜底"）：
        ///   · Windows / Linux 支持 TcpKeepAliveTime / Interval / RetryCount 三个 socket
        ///     选项，能精确控制空闲时间 / 间隔 / 次数；
        ///   · Android（Bionic）对这些选项的支持不完整：部分值会被忽略或抛异常，但
        ///     SocketOptionName.KeepAlive 本身一定可用（退回系统默认：2 小时空闲 +
        ///     9×75s 探测，只保证"不会永远挂住"，不保证及时）。
        /// 所以这里：先无条件开 KeepAlive，再尽力设置精细参数；精细参数失败时
        /// **只记一行**（不刷屏、不当失败），因为开着的 KeepAlive 已达成主要目标。
        /// </summary>
        private static void EnableTcpKeepAlive(Socket socket, string role)
        {
            if (socket == null) return;
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            catch (Exception e)
            {
                // 连 KeepAlive 都开不了才是真问题：明确记下来（不静默）。
                ModLog.Warn("[SuMP][net] Failed to enable TCP KeepAlive (" + role + "）：" +
                    e.GetType().Name + " " + e.Message + " → 空闲连接可能被中间设备回收");
                return;
            }
            bool fine = true;
            fine &= TrySetKeepAliveOption(socket, SocketOptionName.TcpKeepAliveTime,
                KeepAliveTimeSeconds);
            fine &= TrySetKeepAliveOption(socket, SocketOptionName.TcpKeepAliveInterval,
                KeepAliveIntervalSeconds);
            fine &= TrySetKeepAliveOption(socket, SocketOptionName.TcpKeepAliveRetryCount,
                KeepAliveRetryCount);
            if (!fine)
            {
                ModLog.Info("[SuMP][net] TCP KeepAlive enabled, but the platform does not support fine-grained parameters" +
                    "（" + role + "）→ 使用系统默认探测节奏；连接不会被无限期挂住");
            }
        }

        /// <summary>设置单个 KeepAlive 精细选项；不支持时返回 false（不抛给调用方）。</summary>
        private static bool TrySetKeepAliveOption(Socket socket, SocketOptionName option, int value)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Tcp, option, value);
                return true;
            }
            catch (Exception)
            {
                // Android / 旧平台不支持该选项：由 EnableTcpKeepAlive 汇总成一行说明。
                return false;
            }
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset).ConfigureAwait(false);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        /// <summary>旧的同步读实现保留（自检 / 诊断仍可复用），正式收发走 async。</summary>
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

        private static void ReportProblem(string what, Exception e)
        {
            try
            {
                ModLog.Warn("[SuMP] transport " + what + "：" + e.GetType().Name + " " + e.Message);
            }
            catch (Exception)
            {
            }
        }

        public virtual void Dispose()
        {
            m_running = false;
            try { m_listener?.Stop(); } catch (Exception) { }
            if (!m_listenerClosed)
            {
                m_listenerClosed = true;
                // 派生内存传输没有 listener（protected 构造），不能记一次 socket 关闭。
                if (m_listener != null) TransportDiagnostics.OnSocketClosed();
            }
            List<Peer> peers;
            lock (m_gate)
            {
                peers = new List<Peer>(m_peers);
                m_peers.Clear();
            }
            for (int i = 0; i < peers.Count; i++)
            {
                Peer peer = peers[i];
                peer.Alive = false;
                try { peer.Tcp.Close(); } catch (Exception) { }
                peer.SendSignal.Release();
                DrainSendQueue(peer);
                if (!peer.ConnectionClosed) { peer.ConnectionClosed = true; TransportDiagnostics.OnConnectionClosed(); }
                if (peer.IsCircuit || peer.IsTerrain)
                {
                    int primaryClientId = peer.PrimaryClientId;
                    if (peer.IsTerrain)
                        m_terrainCallbacks.Enqueue(() => TerrainPeerDetached?.Invoke(
                            primaryClientId, "server-disposed"));
                    else
                        m_circuitCallbacks.Enqueue(() => CircuitPeerDetached?.Invoke(
                            primaryClientId, "server-disposed"));
                }
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

    /// <summary>
    /// TCP 通道（客户端侧）。连接建立仍是同步 API（调用方按原样拿 bool），
    /// 但收发改成 async 循环：不占专属线程，帧缓冲池化。
    /// </summary>
    public class TcpTerrainClient : IDisposable
    {
        private readonly string m_host;
        private readonly int m_port;
        private readonly int m_queueCapacity;
        private TcpClient m_tcp;
        private NetworkStream m_stream;
        private readonly Queue<SendFrame> m_sendQueue = new Queue<SendFrame>();
        private readonly SemaphoreSlim m_sendWake = new SemaphoreSlim(0, int.MaxValue);
        private long m_sendSequence;
        private volatile bool m_running;
        private bool m_connectionCounted;
        private bool m_socketCounted;
        private volatile bool m_circuitMode;
        private volatile SecondaryChannelKind m_secondaryKind;
        private volatile bool m_circuitAttached;
        private int m_circuitPrimaryClientId;
        private string m_circuitAttachReason = string.Empty;
        private readonly ConcurrentQueue<Action> m_circuitCallbacks =
            new ConcurrentQueue<Action>();

        private readonly struct SendFrame
        {
            public SendFrame(byte[] buffer, int length, MessageType type)
            {
                Buffer = buffer;
                Length = length;
                Type = type;
            }
            public byte[] Buffer { get; }
            public int Length { get; }
            public MessageType Type { get; }
        }

        /// <summary>给派生的内存传输（自检用）留的无 socket 构造。</summary>
        protected TcpTerrainClient(int queueCapacity)
        {
            m_queueCapacity = Math.Max(8, queueCapacity);
            m_host = string.Empty;
            m_port = 0;
        }

        public TcpTerrainClient(string host, int port, int queueCapacity = 256) : this(queueCapacity)
        {
            m_host = host; m_port = port;
            TransportDiagnostics.OnSocketOpened();
            m_socketCounted = true;
        }

        public ChannelStats Stats { get; } = new ChannelStats();
        public virtual ChannelState State { get; protected set; } = ChannelState.Disconnected;
        public event Action<ReceivedFrame> FrameReceived;
        public event Action<ReceivedFrame> CircuitFrameReceived;
        public event Action<ReceivedFrame> TerrainFrameReceived;
        public event Action<ChannelState, string> StateChanged;
        public event Action<bool, string> CircuitAttachCompleted;
        public event Action<bool, string> TerrainAttachCompleted;
        public bool IsCircuitAttached => m_circuitAttached && IsAlive;
        public bool IsTerrainAttached => m_circuitAttached && m_secondaryKind == SecondaryChannelKind.Terrain && IsAlive;
        public string CircuitAttachReason => m_circuitAttachReason;

        /// <summary>派生传输（自检的内存实现）用它把帧交给订阅者。</summary>
        protected void RaiseFrameReceived(ReceivedFrame frame) => FrameReceived?.Invoke(frame);

        /// <summary>派生传输用它抛状态变化。</summary>
        protected void RaiseStateChanged(ChannelState state, string detail) => StateChanged?.Invoke(state, detail);

        /// <summary>
        /// 最近一次状态变化的原因（Failed 时就是"对端关闭 / 协议违规 / IOException"这类实因）。
        /// 上层拆链重连时把它写进日志 —— 断链原因**必须**有据可查
        /// （实机故障：对端掉线后两端状态行只是"不再变化"，日志一个字都没有，排查只能靠猜）。
        /// </summary>
        public virtual string StateDetail { get; protected set; } = string.Empty;

        /// <summary>
        /// 上一次 Connect 失败的**可读分类**（"连接被拒绝（主机端口没有监听…）" / "网络不可达(HostUnreachable)" /
        /// "连接超时" …）。**只用于日志与诊断**，不参与任何"要不要退出世界"的决策。
        ///
        /// 为什么不做决策依据（2026-11-06 结论）：实机强杀主机时客户端拿到的是「连接超时」
        /// 而不是 ConnectionRefused —— PC 整体网络不可达（ping 100% 丢包、nc 超时）时 OS
        /// 根本收不到 RST。而"网络不可达"与"我自己的 WiFi 断了"在网络层完全同形，
        /// 后者正是要"保持世界并重连"的场景。基于连接错误的快速退出一定会误伤它，
        /// 所以一切交给 30 秒重连窗口统一收尾。
        /// </summary>
        public string LastConnectFailureKind { get; private set; } = string.Empty;

        /// <summary>
        /// 这条连接现在还能不能收发：只有 Connected / Backpressured 算活着。
        /// 收循环或发循环一旦失败（<see cref="SetState"/> → Failed）它就是 false。
        /// 上层**绝不能**拿"客户端对象还在"当"连接还在"（实机故障：对端掉线后位置快照一直往
        /// 死连接里塞、posSent 继续涨，主机一个字节都收不到）。
        /// </summary>
        public virtual bool IsAlive => m_running &&
            (State == ChannelState.Connected || State == ChannelState.Backpressured);

        public virtual bool Connect(int timeoutMs = 3000)
        {
            return ConnectInternal(timeoutMs, false, 0, SecondaryChannelKind.Circuit);
        }

        private bool ConnectInternal(int timeoutMs, bool circuitMode, int primaryClientId,
            SecondaryChannelKind secondaryKind)
        {
            LastConnectFailureKind = string.Empty;
            m_circuitMode = false;
            m_secondaryKind = secondaryKind;
            m_circuitAttached = false;
            m_circuitPrimaryClientId = circuitMode ? primaryClientId : 0;
            m_circuitAttachReason = string.Empty;
            try
            {
                // 同一个对象重复 Connect 时先关掉旧 socket（旧实现会把它丢掉 → socket 泄漏）。
                CloseSocket();
                if (m_connectionCounted)
                {
                    m_connectionCounted = false;
                    TransportDiagnostics.OnConnectionClosed();
                }
                bool useProxy = false;
                string routeReason = null;
                if (ProxyRouter.TryChooseTcp(m_host, m_port, out useProxy, out routeReason))
                    ModLog.Info("[SuMP][proxy] Join route: " + m_host + ":" + m_port +
                        " → " + (useProxy ? "代理" : "直连") + "（" + routeReason + "）");
                if (useProxy)
                {
                    Socket proxied = Socks5Proxy.Connect(m_host, m_port, timeoutMs,
                        out string proxyError);
                    if (proxied == null)
                    {
                        CloseSocket();
                        SetState(ChannelState.Failed, "代理连接失败：" + (proxyError ?? "未知"));
                        return false;
                    }
                    m_tcp = new TcpClient { NoDelay = true };
                    m_tcp.Client = proxied;
                    TransportDiagnostics.OnSocketOpened();
                    m_socketCounted = true;
                }
                else
                {
                    m_tcp = new TcpClient { NoDelay = true };
                    TransportDiagnostics.OnSocketOpened();
                    m_socketCounted = true;
                    // Wait(timeout) 抛 AggregateException 时内层才是真实的 SocketException：
                    // 把它交给 ClassifyConnectFailure 判定"主机确实不在"还是"本端网络问题"。
                    Task connectTask = m_tcp.ConnectAsync(m_host, m_port);
                    bool completed;
                    try
                    {
                        completed = connectTask.Wait(timeoutMs);
                    }
                    catch (AggregateException ae)
                    {
                        CloseSocket();
                        string kind = DescribeConnectFailure(FindSocketException(ae));
                        LastConnectFailureKind = kind;
                        SetState(ChannelState.Failed, kind);
                        return false;
                    }
                    if (!completed)
                    {
                        CloseSocket();
                        LastConnectFailureKind = "连接超时（" + timeoutMs + " ms 内未建立）";
                        SetState(ChannelState.Failed, LastConnectFailureKind);
                        return false;
                    }
                }
                m_stream = m_tcp.GetStream();
                // 空闲探测：不操作时 WiFi 进入省电 / AP 回收 NAT 映射后连接会静默半开，
                // 上层看不到对端已经不在，直到某个时机才突然报错并退出房间。见 EnableTcpKeepAlive。
                EnableTcpKeepAlive(m_tcp.Client, "client-connect");
                // Set the transport mode before starting ReceiveLoopAsync. The server can
                // reply with CircuitAttached immediately after the attach frame; if the
                // receive loop starts in normal mode it rejects that frame and loses the
                // circuit channel before ConnectCircuit can switch the flag.
                m_circuitMode = circuitMode;
                m_running = true;
                TransportDiagnostics.OnConnectionCreated();
                m_connectionCounted = true;
                SetState(ChannelState.Connected, "已连接");
                _ = ReceiveLoopAsync();
                _ = SendLoopAsync();
                return true;
            }
            catch (Exception e)
            {
                CloseSocket();
                SetState(ChannelState.Failed, e.GetType().Name);
                return false;
            }
        }

        /// <summary>从 AggregateException 里取出第一个 SocketException（可能嵌套多层）。</summary>
        private static SocketException FindSocketException(AggregateException aggregate)
        {
            if (aggregate == null) return null;
            foreach (Exception inner in aggregate.Flatten().InnerExceptions)
            {
                if (inner is SocketException socket) return socket;
            }
            return aggregate.InnerException as SocketException;
        }

        /// <summary>
        /// 把连接失败归类成可读原因，并指出它是否**说明主机进程真的不在了**（G10）。
        ///
        /// <paramref name="refused"/> = true 仅当 <c>SocketError.ConnectionRefused</c>：
        /// TCP 握手被 RST ⇒ 主机在线但端口无人监听 ⇒ 主机游戏已退出，重连没有意义。
        /// 其它（HostUnreachable / NetworkUnreachable / TimedOut / 代理失败 …）一律算
        /// "可能是本端网络问题"，值得留在重连窗口里继续等。
        /// </summary>
        /// <summary>
        /// 把连接失败翻译成**可读原因**（只用于日志 / StateDetail，不参与任何退出决策）。
        ///
        /// 保留完整的 <see cref="SocketError"/> 名字：实机排障时"连接超时"和
        /// "ConnectionRefused"是完全不同的两件事，日志里必须能分辨。
        /// </summary>
        private static string DescribeConnectFailure(SocketException socketError)
        {
            if (socketError == null) return "连接失败（无底层错误码）";
            switch (socketError.SocketErrorCode)
            {
                case SocketError.ConnectionRefused:
                    return "连接被拒绝 ConnectionRefused（目标端口无人监听）";
                case SocketError.HostUnreachable:
                case SocketError.NetworkUnreachable:
                case SocketError.HostDown:
                case SocketError.NetworkDown:
                case SocketError.TimedOut:
                case SocketError.TryAgain:
                    return "网络不可达 " + socketError.SocketErrorCode;
                default:
                    return "连接失败 " + socketError.SocketErrorCode;
            }
        }

        public virtual bool ConnectCircuit(int primaryClientId, byte[] ticket,
            int timeoutMs = 3000)
        {
            if (primaryClientId <= 0 || ticket == null || ticket.Length == 0)
            {
                m_circuitAttachReason = "invalid-primary-or-ticket";
                return false;
            }
            if (!ConnectInternal(timeoutMs, true, primaryClientId, SecondaryChannelKind.Circuit)) return false;
            if (!AttachCircuit(primaryClientId, ticket))
            {
                m_circuitAttachReason = "attach-send-failed";
                Dispose();
                return false;
            }
            return true;
        }

        public virtual bool ConnectTerrain(int primaryClientId, byte[] ticket,
            int timeoutMs = 3000)
        {
            if (primaryClientId <= 0 || ticket == null || ticket.Length == 0)
            {
                m_circuitAttachReason = "invalid-primary-or-ticket";
                return false;
            }
            if (!ConnectInternal(timeoutMs, true, primaryClientId, SecondaryChannelKind.Terrain)) return false;
            if (!AttachTerrain(primaryClientId, ticket))
            {
                m_circuitAttachReason = "attach-send-failed";
                Dispose();
                return false;
            }
            return true;
        }

        public virtual bool AttachCircuit(int primaryClientId, byte[] ticket)
        {
            if (!IsAlive || primaryClientId <= 0 || ticket == null || ticket.Length == 0)
            {
                m_circuitAttachReason = "not-connected-or-invalid-attach";
                return false;
            }
            m_circuitMode = true;
            m_circuitPrimaryClientId = primaryClientId;
            m_circuitAttached = false;
            m_circuitAttachReason = string.Empty;
            var request = new CircuitAttach
            {
                PrimaryClientId = primaryClientId,
                Ticket = ByteString.CopyFrom(ticket),
                ChannelKind = (uint)SecondaryChannelKind.Circuit
            };
            return TrySend(MessageType.CircuitAttach, request.ToByteArray());
        }

        public virtual bool AttachTerrain(int primaryClientId, byte[] ticket)
        {
            if (!IsAlive || primaryClientId <= 0 || ticket == null || ticket.Length == 0)
            {
                m_circuitAttachReason = "not-connected-or-invalid-attach";
                return false;
            }
            m_circuitMode = true;
            m_secondaryKind = SecondaryChannelKind.Terrain;
            m_circuitPrimaryClientId = primaryClientId;
            m_circuitAttached = false;
            m_circuitAttachReason = string.Empty;
            var request = new CircuitAttach
            {
                PrimaryClientId = primaryClientId,
                Ticket = ByteString.CopyFrom(ticket),
                ChannelKind = (uint)SecondaryChannelKind.Terrain
            };
            return TrySend(MessageType.CircuitAttach, request.ToByteArray());
        }

        public void PumpCircuitEvents()
        {
            while (m_circuitCallbacks.TryDequeue(out Action callback)) callback();
        }

        private void CloseSocket()
        {
            try { m_tcp?.Close(); } catch (Exception) { }
            m_tcp = null;
            m_stream = null;
            if (m_socketCounted)
            {
                m_socketCounted = false;
                TransportDiagnostics.OnSocketClosed();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            byte[] header = TransportDiagnostics.RentBuffer(TerrainFrame.HeaderBytes);
            try
            {
                while (m_running)
                {
                    if (!await ReadExactAsync(m_stream, header, TerrainFrame.HeaderBytes).ConfigureAwait(false))
                    {
                        SetState(ChannelState.Failed, "对端关闭"); return;
                    }
                    if (!TerrainFrame.TryReadHeader(header, 0, out int len, out long seq, out MessageType type))
                    {
                        Stats.RecordProtocolViolation();
                        SetState(ChannelState.Failed, "协议违规"); return;
                    }
                    byte[] payload = len == 0 ? Array.Empty<byte>() : new byte[len];
                    if (len > 0 && !await ReadExactAsync(m_stream, payload, len).ConfigureAwait(false))
                    {
                        SetState(ChannelState.Failed, "负载截断"); return;
                    }
                    Stats.RecordReceived(type, TerrainFrame.HeaderBytes + len);
                    if (m_circuitMode)
                    {
                        if (type == MessageType.CircuitAttached)
                        {
                            CircuitAttached response = CircuitAttached.Parser.ParseFrom(payload);
                            if (response.PrimaryClientId != m_circuitPrimaryClientId)
                            {
                                Stats.RecordProtocolViolation();
                                m_circuitAttachReason = "attached-primary-mismatch";
                                SetState(ChannelState.Failed, m_circuitAttachReason);
                                return;
                            }
                            SecondaryChannelKind responseKind = response.ChannelKind ==
                                (uint)SecondaryChannelKind.Terrain
                                ? SecondaryChannelKind.Terrain : SecondaryChannelKind.Circuit;
                            if (responseKind != m_secondaryKind)
                            {
                                Stats.RecordProtocolViolation();
                                m_circuitAttachReason = "attached-channel-kind-mismatch";
                                SetState(ChannelState.Failed, m_circuitAttachReason);
                                return;
                            }
                            m_circuitAttached = response.Accepted;
                            m_circuitAttachReason = response.Reason ?? string.Empty;
                            bool accepted = response.Accepted;
                            string attachReason = m_circuitAttachReason;
                            if (m_secondaryKind == SecondaryChannelKind.Terrain)
                                m_circuitCallbacks.Enqueue(() =>
                                    TerrainAttachCompleted?.Invoke(accepted, attachReason));
                            else
                                m_circuitCallbacks.Enqueue(() =>
                                    CircuitAttachCompleted?.Invoke(accepted, attachReason));
                            if (!response.Accepted)
                            {
                                SetState(ChannelState.Failed,
                                    "circuit-attach-rejected:" + m_circuitAttachReason);
                                return;
                            }
                            continue;
                        }
                        if (!m_circuitAttached || type == MessageType.CircuitAttach)
                        {
                            Stats.RecordProtocolViolation();
                            m_circuitAttachReason = "circuit-frame-before-accepted";
                            SetState(ChannelState.Failed, m_circuitAttachReason);
                            return;
                        }
                        ReceivedFrame received = new ReceivedFrame(0, seq, type, payload);
                        if (m_secondaryKind == SecondaryChannelKind.Terrain)
                            m_circuitCallbacks.Enqueue(() => TerrainFrameReceived?.Invoke(received));
                        else
                            m_circuitCallbacks.Enqueue(() => CircuitFrameReceived?.Invoke(received));
                    }
                    else
                    {
                        if (type == MessageType.CircuitAttach || type == MessageType.CircuitAttached)
                        {
                            Stats.RecordProtocolViolation();
                            SetState(ChannelState.Failed, "unexpected-circuit-transport-frame");
                            return;
                        }
                        RaiseFrameReceived(new ReceivedFrame(0, seq, type, payload));
                    }
                }
            }
            catch (Exception e) { SetState(ChannelState.Failed, e.GetType().Name); }
            finally { TransportDiagnostics.ReturnBuffer(header); }
        }

        private async Task SendLoopAsync()
        {
            try
            {
                while (m_running)
                {
                    SendFrame frame = default;
                    bool hasFrame = false;
                    lock (m_sendQueue)
                    {
                        if (m_sendQueue.Count > 0)
                        {
                            frame = m_sendQueue.Dequeue();
                            hasFrame = true;
                            m_sendWake.Wait(0);
                        }
                    }
                    if (!hasFrame)
                    {
                        try { await m_sendWake.WaitAsync(5).ConfigureAwait(false); }
                        catch (ObjectDisposedException) { return; }
                        continue;
                    }
                    try
                    {
                        await m_stream.WriteAsync(frame.Buffer, 0, frame.Length).ConfigureAwait(false);
                        Stats.RecordSent(frame.Type, frame.Length);
                        lock (m_sendQueue)
                        {
                            if (m_sendQueue.Count < m_queueCapacity && State == ChannelState.Backpressured)
                                SetState(ChannelState.Connected, "队列回落");
                        }
                    }
                    finally
                    {
                        TransportDiagnostics.ReturnBuffer(frame.Buffer);
                    }
                }
            }
            catch (Exception e) { SetState(ChannelState.Failed, e.GetType().Name); }
        }

        // ---- TCP KeepAlive（客户端侧）----
        //
        // 与服务端 TcpTerrainServer 里的同名实现保持一致性：两个类各自独立，
        // 私有静态方法不跨界共享，所以两边各留一份（逻辑与注释同源，改一处必须同改另一处）。
        //
        // 客户端更需要它：手机 WiFi 在"不操作"时会进入省电 / 被 AP 回收 NAT 映射，
        // 没有 KeepAlive 的连接会静默半开（读阻塞、写成功），上层看不出主机已经不可达，
        // 直到某个时机才突然报错并直接退出房间。
        private const int KeepAliveTimeSeconds = 30;      // 空闲多久开始探测
        private const int KeepAliveIntervalSeconds = 5;   // 探测间隔
        private const int KeepAliveRetryCount = 3;        // 连续几次无响应判死（30+5×3=45s 内）

        /// <summary>
        /// 给已建立的 socket 打开 TCP KeepAlive（客户端版）。
        /// 平台差异：Windows / Linux 支持 TcpKeepAliveTime / Interval / RetryCount 精确参数；
        /// Android 对精细选项支持不完整（可能忽略或抛异常），但 KeepAlive 本身可用
        /// （退回系统默认节奏）。精细参数失败只记一行，因为开着的 KeepAlive 已达成主要目标。
        /// </summary>
        private static void EnableTcpKeepAlive(Socket socket, string role)
        {
            if (socket == null) return;
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            catch (Exception e)
            {
                ModLog.Warn("[SuMP][net] Failed to enable TCP KeepAlive (" + role + "）：" +
                    e.GetType().Name + " " + e.Message + " → 空闲连接可能被中间设备回收");
                return;
            }
            bool fine = true;
            fine &= TrySetKeepAliveOption(socket, SocketOptionName.TcpKeepAliveTime,
                KeepAliveTimeSeconds);
            fine &= TrySetKeepAliveOption(socket, SocketOptionName.TcpKeepAliveInterval,
                KeepAliveIntervalSeconds);
            fine &= TrySetKeepAliveOption(socket, SocketOptionName.TcpKeepAliveRetryCount,
                KeepAliveRetryCount);
            if (!fine)
            {
                ModLog.Info("[SuMP][net] TCP KeepAlive enabled, but the platform does not support fine-grained parameters" +
                    "（" + role + "）→ 使用系统默认探测节奏；连接不会被无限期挂住");
            }
        }

        /// <summary>设置单个 KeepAlive 精细选项；不支持时返回 false（不抛给调用方）。</summary>
        private static bool TrySetKeepAliveOption(Socket socket, SocketOptionName option, int value)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Tcp, option, value);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset).ConfigureAwait(false);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        public virtual bool TrySend(MessageType type, byte[] payload)
        {
            // 死连接不假装"发成功"：Failed/Disconnected 之后入队只会越堆越多，
            // 调用方的 posSent 会继续涨而主机永远收不到（实机故障就是这样静默的）。
            if (!IsAlive || m_circuitMode &&
                (type != MessageType.CircuitAttach || m_circuitAttached)) return false;
            payload = payload ?? Array.Empty<byte>();
            long sequence = Interlocked.Increment(ref m_sendSequence);
            TerrainFrame.Validate(type, payload);          // 先校验再借缓冲（非法负载不占池）
            int length = TerrainFrame.HeaderBytes + payload.Length;
            byte[] frame = TransportDiagnostics.RentBuffer(length);
            bool enqueued = false;
            try
            {
                TerrainFrame.EncodeTo(frame, sequence, type, payload);
                bool dead, full;
                lock (m_sendQueue)
                {
                    dead = !m_running;
                    full = !dead && m_sendQueue.Count >= m_queueCapacity;
                    if (!dead) Stats.ObserveSendQueue(m_sendQueue.Count);
                    if (!dead && !full)
                    {
                        m_sendQueue.Enqueue(new SendFrame(frame, length, type));
                        Stats.ObserveSendQueue(m_sendQueue.Count);
                        m_sendWake.Release();
                        enqueued = true;
                    }
                }
                if (dead) return false;
                if (full)
                {
                    Stats.RecordBackpressure();
                    SetState(ChannelState.Backpressured, "发送队列已满（显式背压）");
                    return false;
                }
                return true;
            }
            finally
            {
                // 成功入队时所有权转交发送循环；其余路径（背压 / 断开 / 编码异常）当场归还。
                if (!enqueued) TransportDiagnostics.ReturnBuffer(frame);
            }
        }

        public virtual bool TrySendCircuit(MessageType type, byte[] payload)
        {
            if (!IsCircuitAttached || m_secondaryKind != SecondaryChannelKind.Circuit ||
                type == MessageType.CircuitAttach ||
                type == MessageType.CircuitAttached) return false;
            return TrySendCore(type, payload);
        }

        public virtual bool TrySendTerrain(MessageType type, byte[] payload)
        {
            if (!IsTerrainAttached || type == MessageType.CircuitAttach ||
                type == MessageType.CircuitAttached) return false;
            return TrySendCore(type, payload);
        }

        private bool TrySendCore(MessageType type, byte[] payload)
        {
            if (!IsAlive) return false;
            payload = payload ?? Array.Empty<byte>();
            long sequence = Interlocked.Increment(ref m_sendSequence);
            TerrainFrame.Validate(type, payload);
            int length = TerrainFrame.HeaderBytes + payload.Length;
            byte[] frame = TransportDiagnostics.RentBuffer(length);
            bool enqueued = false;
            try
            {
                TerrainFrame.EncodeTo(frame, sequence, type, payload);
                bool dead, full;
                lock (m_sendQueue)
                {
                    dead = !m_running;
                    full = !dead && m_sendQueue.Count >= m_queueCapacity;
                    if (!dead) Stats.ObserveSendQueue(m_sendQueue.Count);
                    if (!dead && !full)
                    {
                        m_sendQueue.Enqueue(new SendFrame(frame, length, type));
                        Stats.ObserveSendQueue(m_sendQueue.Count);
                        m_sendWake.Release();
                        enqueued = true;
                    }
                }
                if (dead) return false;
                if (full)
                {
                    Stats.RecordBackpressure();
                    SetState(ChannelState.Backpressured, "发送队列已满（显式背压）");
                    return false;
                }
                return true;
            }
            finally
            {
                if (!enqueued) TransportDiagnostics.ReturnBuffer(frame);
            }
        }

        protected virtual void SetState(ChannelState state, string detail)
        {
            if (State == state) return;
            State = state;
            if (!string.IsNullOrEmpty(detail)) StateDetail = detail;
            RaiseStateChanged(state, detail);
        }

        /// <summary>等待发送队列清空（测试/关闭前用）。</summary>
        public virtual bool WaitDrained(int timeoutMs)
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

        public virtual void Dispose()
        {
            m_running = false;
            m_circuitAttached = false;
            CloseSocket();
            m_sendWake.Release();
            DrainSendQueue();
            if (m_connectionCounted)
            {
                m_connectionCounted = false;
                TransportDiagnostics.OnConnectionClosed();
            }
            SetState(ChannelState.Disconnected, "已关闭");
        }

        private void DrainSendQueue()
        {
            while (true)
            {
                SendFrame frame;
                lock (m_sendQueue)
                {
                    if (m_sendQueue.Count == 0) return;
                    frame = m_sendQueue.Dequeue();
                }
                TransportDiagnostics.ReturnBuffer(frame.Buffer);
            }
        }
    }
}
