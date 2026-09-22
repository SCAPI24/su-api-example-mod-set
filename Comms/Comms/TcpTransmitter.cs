using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Comms;

// Source: Comms/Comms/ITransmitter.cs:ITransmitter.IsReliableStream
// A TCP transport that presents itself to Comm as a datagram endpoint keyed by the peer's UDP
// address, so the identity rules of the datagram path (a message carries the sender's port) keep
// working. One TCP connection per peer:
//   · the first record on a connection is an 8-byte handshake that tells the peer which UDP port
//     this stream belongs to (the IP comes from the socket, which also survives loopback and
//     multi-homed hosts);
//   · every Comm packet then travels as one length-prefixed record, so message boundaries survive
//     the byte stream;
//   · TCP already guarantees delivery, ordering and no duplication, so Comm skips its own
//     ACK/resend bookkeeping (see Comm.SendDataPacket / Comm.ProcessReceivedPacket).
// Sending never blocks the caller: records are queued per connection and written by a dedicated
// thread. The queue depth is exposed through GetPendingSendCount so Comm can keep reporting a
// flow-control signal to its callers.
public class TcpTransmitter : ITransmitter, IDisposable
{
    // Source: Comms/Comms/Comm.cs:Comm.PacketType
    // Comm writes its packet type in the low nibble of the first byte. The hybrid transport uses
    // it to decide whether a reliable packet rides the stream or the datagram path.
    public const byte PacketTypeMask = 0x0F;

    public const byte PacketTypeReliableData = 3;

    private const uint HandshakeMagic = 0x50435453u;

    // Source: Comms/Comms/Comm.cs:Comm.PacketHeader.DatagramTokenFlag
    // 2 adds the datagram-token peer identity (Comm packet header 0x40 flag), so a peer that still
    // speaks 1 must be rejected during the handshake instead of having its packets mis-parsed.
    private const byte ProtocolVersion = 2;

    private const int HandshakeRecordSize = 8;

    private const int LengthPrefixSize = 4;

    private const int MaximumRecordSize = 1024 * 1024;

    private const int MaximumQueuedRecords = 4096;

    private const long MaximumQueuedBytes = 8L * 1024 * 1024;

    private const int HandshakeTimeoutMilliseconds = 5000;

    private const int ThreadJoinMilliseconds = 500;

    // 64 KB records: a stream has no datagram boundary to respect, so Comm rarely needs to split.
    public const int DefaultMaxPacketSize = 64 * 1024;

    private sealed class Connection
    {
        public Socket? Socket;

        public IPEndPoint? PeerAddress;

        public Thread? ReaderThread;

        public Thread? WriterThread;

        public readonly BlockingCollection<byte[]> SendQueue =
            new(new ConcurrentQueue<byte[]>());

        public long QueuedBytes;

        public volatile bool Closed;

        public bool CloseHandled;

        public bool IsOutgoing;

        public bool IsHandshakeComplete;
    }

    private readonly object Lock = new();

    private readonly Dictionary<IPEndPoint, Connection> Connections = new();

    private readonly bool AllowOutgoing;

    private Socket? Listener;

    private Thread? AcceptThread;

    private volatile bool IsDisposed;

    private int Disposed;

    public int MaxPacketSize { get; set; } = DefaultMaxPacketSize;

    // TCP is the reliable stream Comm must not duplicate with its own ACK/resend layer.
    public bool IsReliableStream => true;

    // The datagram endpoint this stream speaks for. All peer identity stays in datagram terms.
    public IPEndPoint Address { get; }

    public event Action<Exception>? Error;

    public event Action<string>? Debug;

    public event Action<Packet>? PacketReceived;

    // Source: Comms/Comms/ITransmitter.cs:ITransmitter.Address
    // datagramAddress is the UDP endpoint of the same channel; listenPort is the TCP port to
    // listen on (the same number the datagram socket uses), or 0 for a client that only dials.
    public TcpTransmitter(int listenPort, IPEndPoint datagramAddress, bool allowOutgoing)
    {
        Address = datagramAddress ?? throw new ArgumentNullException(nameof(datagramAddress));
        AllowOutgoing = allowOutgoing;
        if (listenPort > 0)
        {
            Listener = CreateListener(listenPort);
            AcceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "TcpTransmitter.Accept"
            };
            AcceptThread.Start();
        }
    }

    // Source: System.Net.Sockets.Socket.DualMode
    // One listener covers IPv4 and IPv6 when the platform supports dual mode sockets; otherwise
    // fall back to an IPv4-only listener so a v4-only peer can still connect.
    private static Socket CreateListener(int port)
    {
        try
        {
            Socket socket = new(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            socket.DualMode = true;
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            socket.Listen(16);
            return socket;
        }
        catch (Exception)
        {
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                socket.Listen(16);
                return socket;
            }
            catch (Exception)
            {
                socket.Dispose();
                throw;
            }
        }
    }

    // Source: Comms/Comms/UdpTransmitter.cs:UdpTransmitter.Address
    // A dual mode listener reports IPv4 peers as IPv4-mapped IPv6 endpoints; map them back so the
    // peer key matches the IPv4 endpoint the datagram path uses for the same peer.
    private static IPEndPoint NormalizeAddress(EndPoint endPoint)
    {
        IPEndPoint? ipEndPoint = endPoint as IPEndPoint;
        if (ipEndPoint == null)
        {
            return new IPEndPoint(IPAddress.None, 0);
        }
        IPAddress address = ipEndPoint.Address;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        return new IPEndPoint(address, ipEndPoint.Port);
    }

    // True when this side may dial a peer that has no stream yet (the client role). A server only
    // answers streams its peers opened, so reliable packets to unknown addresses are reported
    // instead of silently dialed.
    public bool CanDialOut => AllowOutgoing;

    public bool HasConnection(IPEndPoint address)
    {
        if (address == null)
        {
            return false;
        }
        lock (Lock)
        {
            return Connections.TryGetValue(address, out Connection connection) &&
                connection.IsHandshakeComplete;
        }
    }

    // Source: Comms/Comms/Comm.cs:Comm.MoveConnection
    // Moves the key of an established connection without touching its socket, reader/writer
    // threads, send queue or handshake state: the peer's datagram address changed, the stream did
    // not. Outgoing streams are re-keyed as well, so the dialing side does not open a second
    // stream to the same peer after the address repair.
    public bool TryRebindDatagramAddress(IPEndPoint known, IPEndPoint observed)
    {
        if (known == null || observed == null || known.Equals(observed))
        {
            return false;
        }
        lock (Lock)
        {
            if (IsDisposed)
            {
                return false;
            }
            if (!Connections.TryGetValue(known, out Connection connection) || connection == null)
            {
                return false;
            }
            if (connection.Closed || !connection.IsHandshakeComplete)
            {
                return false;
            }
            if (Connections.ContainsKey(observed))
            {
                return false;
            }
            Connections.Remove(known);
            connection.PeerAddress = observed;
            Connections.Add(observed, connection);
        }
        InvokeDebug($"TCP peer address moved from {known} to {observed}");
        return true;
    }

    public void SendPacket(Packet packet)
    {
        CheckNotDisposed();
        byte[]? bytes = packet.Bytes;
        if (bytes == null || bytes.Length == 0 || packet.Address == null)
        {
            return;
        }
        Connection? connection = GetOrCreateConnection(packet.Address);
        if (connection == null)
        {
            // Reliable traffic reached a peer this side may not dial (a server talking to an
            // address that never opened a stream). Report instead of silently dropping.
            InvokeError(new InvalidOperationException(
                $"No TCP connection to {packet.Address} and this transport does not dial out; " +
                "dropping a reliable packet"));
            return;
        }
        if (connection.SendQueue.Count >= MaximumQueuedRecords ||
            Interlocked.Read(ref connection.QueuedBytes) + bytes.Length > MaximumQueuedBytes)
        {
            InvokeError(new InvalidOperationException(
                $"TCP send queue for {connection.PeerAddress} is full " +
                $"({connection.SendQueue.Count} records, " +
                $"{Interlocked.Read(ref connection.QueuedBytes)} bytes); dropping a packet"));
            return;
        }
        Interlocked.Add(ref connection.QueuedBytes, bytes.Length);
        try
        {
            connection.SendQueue.Add(bytes);
        }
        catch (Exception)
        {
            Interlocked.Add(ref connection.QueuedBytes, -bytes.Length);
        }
    }

    public int GetPendingSendCount(IPEndPoint address)
    {
        if (address == null)
        {
            return 0;
        }
        lock (Lock)
        {
            return Connections.TryGetValue(address, out Connection connection)
                ? connection.SendQueue.Count
                : 0;
        }
    }

    private Connection? GetOrCreateConnection(IPEndPoint address)
    {
        lock (Lock)
        {
            if (IsDisposed)
            {
                return null;
            }
            if (Connections.TryGetValue(address, out Connection existing))
            {
                return existing;
            }
            if (!AllowOutgoing)
            {
                return null;
            }
            // One dial attempt per peer; packets sent before the handshake finishes queue behind it.
            Connection connection = new()
            {
                PeerAddress = address,
                IsOutgoing = true
            };
            Connections[address] = connection;
            Thread starter = new(() => ConnectOutgoing(connection))
            {
                IsBackground = true,
                Name = "TcpTransmitter.Connect"
            };
            starter.Start();
            return connection;
        }
    }

    private void ConnectOutgoing(Connection connection)
    {
        IPEndPoint? peer = connection.PeerAddress;
        Socket? socket = null;
        try
        {
            if (peer == null)
            {
                CloseConnection(connection);
                return;
            }
            socket = new Socket(peer.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                ReceiveTimeout = HandshakeTimeoutMilliseconds
            };
            socket.Connect(peer);
            connection.Socket = socket;
            WriteHandshake(socket, Address);
            InvokeDebug($"TCP connected to {peer}");
            connection.ReaderThread = new Thread(() => ReadLoop(connection))
            {
                IsBackground = true,
                Name = "TcpTransmitter.Read"
            };
            connection.ReaderThread.Start();
        }
        catch (Exception error)
        {
            socket?.Dispose();
            connection.Socket = null;
            if (!IsDisposed)
            {
                InvokeError(new InvalidOperationException(
                    $"TCP connect to {peer} failed: {error.Message}"));
            }
            CloseConnection(connection);
        }
    }

    private void AcceptLoop()
    {
        while (!IsDisposed)
        {
            Socket? accepted = null;
            try
            {
                Socket? listener = Listener;
                if (listener == null)
                {
                    break;
                }
                accepted = listener.Accept();
                accepted.NoDelay = true;
                accepted.ReceiveTimeout = HandshakeTimeoutMilliseconds;
                Connection connection = new()
                {
                    Socket = accepted,
                    IsOutgoing = false
                };
                connection.ReaderThread = new Thread(() => ReadLoop(connection))
                {
                    IsBackground = true,
                    Name = "TcpTransmitter.Read"
                };
                connection.ReaderThread.Start();
            }
            catch (Exception error)
            {
                accepted?.Dispose();
                if (IsDisposed)
                {
                    break;
                }
                if (error is SocketException socketError &&
                    (socketError.SocketErrorCode == SocketError.Interrupted ||
                     socketError.SocketErrorCode == SocketError.OperationAborted))
                {
                    break;
                }
                InvokeError(error);
                Thread.Sleep(50);
            }
        }
    }

    private void ReadLoop(Connection connection)
    {
        try
        {
            Socket? socket = connection.Socket;
            if (socket == null)
            {
                return;
            }
            byte[] prefix = new byte[LengthPrefixSize];
            bool expectHandshake = true;
            while (!connection.Closed && !IsDisposed)
            {
                if (!ReadExactly(socket, prefix, LengthPrefixSize))
                {
                    break;
                }
                int length = BitConverter.ToInt32(prefix, 0);
                if (length <= 0 || length > MaximumRecordSize ||
                    (expectHandshake && length != HandshakeRecordSize))
                {
                    InvokeError(new InvalidOperationException(
                        $"Invalid TCP record length {length} from {connection.PeerAddress}"));
                    break;
                }
                byte[] payload = new byte[length];
                if (!ReadExactly(socket, payload, length))
                {
                    break;
                }
                if (expectHandshake)
                {
                    expectHandshake = false;
                    if (!CompleteHandshake(connection, socket, payload))
                    {
                        break;
                    }
                    continue;
                }
                Action<Packet>? handler = PacketReceived;
                if (handler != null)
                {
                    // Source: Comms/Comms/Packet.cs:Packet.FromStream
                    // Mark the delivery channel: only stream-delivered packets may establish the
                    // session identity (datagram token) that a datagram address repair relies on.
                    handler(new Packet(connection.PeerAddress!, payload) { FromStream = true });
                }
            }
        }
        catch (Exception error)
        {
            if (!connection.Closed && !IsDisposed)
            {
                InvokeError(error);
            }
        }
        finally
        {
            CloseConnection(connection);
        }
    }

    private bool CompleteHandshake(Connection connection, Socket socket, byte[] payload)
    {
        if (BitConverter.ToUInt32(payload, 0) != HandshakeMagic)
        {
            InvokeError(new InvalidOperationException(
                $"TCP handshake from {connection.PeerAddress} has a bad magic value; closing"));
            return false;
        }
        if (payload[4] != ProtocolVersion)
        {
            InvokeError(new InvalidOperationException(
                $"TCP handshake from {connection.PeerAddress} uses protocol version {payload[4]}, " +
                $"this build speaks {ProtocolVersion}; closing"));
            return false;
        }
        IPEndPoint? socketPeer = socket.RemoteEndPoint == null
            ? null
            : NormalizeAddress(socket.RemoteEndPoint);
        if (socketPeer == null)
        {
            return false;
        }
        if (connection.IsOutgoing)
        {
            // Outgoing connections are already keyed by the address that was dialed.
            connection.PeerAddress ??= socketPeer;
        }
        else
        {
            // Source: Comms/Comms/TcpTransmitter.cs:CompleteHandshake
            // The dialing side announces its datagram port; the IP comes from the socket so a
            // loopback or multi-homed host binds to the endpoint the datagram path really uses.
            int datagramPort = BitConverter.ToUInt16(payload, 6);
            if (datagramPort <= 0)
            {
                InvokeError(new InvalidOperationException(
                    $"TCP handshake from {socketPeer} announced port {datagramPort}; closing"));
                return false;
            }
            IPEndPoint peerAddress = new(socketPeer.Address, datagramPort);
            lock (Lock)
            {
                if (IsDisposed)
                {
                    return false;
                }
                if (Connections.TryGetValue(peerAddress, out Connection replaced) &&
                    replaced != connection)
                {
                    // A reconnect replaces the previous stream for the same peer.
                    CloseConnectionLocked(replaced);
                }
                connection.PeerAddress = peerAddress;
                Connections[peerAddress] = connection;
            }
            // Answer with our own handshake so the peer can verify the version as well. It must be
            // the first record this side writes, so it happens before the writer thread starts.
            WriteHandshake(socket, Address);
            InvokeDebug($"TCP accepted from {peerAddress}");
        }
        socket.ReceiveTimeout = 0;
        connection.IsHandshakeComplete = true;
        connection.WriterThread = new Thread(() => WriteLoop(connection))
        {
            IsBackground = true,
            Name = "TcpTransmitter.Write"
        };
        connection.WriterThread.Start();
        return true;
    }

    private static void WriteHandshake(Socket socket, IPEndPoint datagramAddress)
    {
        byte[] payload = new byte[HandshakeRecordSize];
        BitConverter.GetBytes(HandshakeMagic).CopyTo(payload, 0);
        payload[4] = ProtocolVersion;
        payload[5] = 0;
        BitConverter.GetBytes((ushort)datagramAddress.Port).CopyTo(payload, 6);
        WriteRecord(socket, payload);
    }

    private void WriteLoop(Connection connection)
    {
        try
        {
            foreach (byte[] payload in connection.SendQueue.GetConsumingEnumerable())
            {
                if (connection.Closed || IsDisposed)
                {
                    break;
                }
                Socket? socket = connection.Socket;
                if (socket == null || !WriteRecord(socket, payload))
                {
                    break;
                }
                Interlocked.Add(ref connection.QueuedBytes, -payload.Length);
            }
        }
        catch (Exception error)
        {
            if (!connection.Closed && !IsDisposed)
            {
                InvokeError(error);
            }
        }
        finally
        {
            CloseConnection(connection);
        }
    }

    private static bool WriteRecord(Socket socket, byte[] payload)
    {
        byte[] record = new byte[LengthPrefixSize + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(record, 0);
        payload.CopyTo(record, LengthPrefixSize);
        int sent = 0;
        while (sent < record.Length)
        {
            int written = socket.Send(record, sent, record.Length - sent, SocketFlags.None);
            if (written <= 0)
            {
                return false;
            }
            sent += written;
        }
        return true;
    }

    private static bool ReadExactly(Socket socket, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int received = socket.Receive(buffer, read, count - read, SocketFlags.None);
            if (received <= 0)
            {
                return false;
            }
            read += received;
        }
        return true;
    }

    private void CloseConnection(Connection connection)
    {
        if (connection.CloseHandled)
        {
            return;
        }
        lock (Lock)
        {
            if (connection.CloseHandled)
            {
                return;
            }
            connection.CloseHandled = true;
        }
        connection.Closed = true;
        try
        {
            connection.SendQueue.CompleteAdding();
        }
        catch (Exception)
        {
        }
        Socket? socket = connection.Socket;
        if (socket != null)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
            }
            try
            {
                socket.Dispose();
            }
            catch (Exception)
            {
            }
        }
        IPEndPoint? peer = connection.PeerAddress;
        if (peer != null)
        {
            lock (Lock)
            {
                if (Connections.TryGetValue(peer, out Connection mapped) && mapped == connection)
                {
                    Connections.Remove(peer);
                }
            }
            InvokeDebug($"TCP connection to {peer} closed");
        }
    }

    private void CloseConnectionLocked(Connection connection)
    {
        connection.Closed = true;
        try
        {
            connection.SendQueue.CompleteAdding();
        }
        catch (Exception)
        {
        }
        try
        {
            connection.Socket?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref Disposed, 1) != 0)
        {
            return;
        }
        IsDisposed = true;
        try
        {
            Listener?.Dispose();
        }
        catch (Exception)
        {
        }
        Listener = null;
        List<Connection> all;
        lock (Lock)
        {
            all = new List<Connection>(Connections.Values);
            Connections.Clear();
        }
        foreach (Connection connection in all)
        {
            CloseConnection(connection);
        }
        AcceptThread?.Join(ThreadJoinMilliseconds);
        foreach (Connection connection in all)
        {
            connection.ReaderThread?.Join(ThreadJoinMilliseconds);
            connection.WriterThread?.Join(ThreadJoinMilliseconds);
        }
    }

    private void CheckNotDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException("TcpTransmitter");
        }
    }

    private void InvokeError(Exception error)
    {
        Action<Exception>? handler = Error;
        if (handler != null)
        {
            try
            {
                handler(error);
            }
            catch (Exception)
            {
            }
        }
    }

    [Conditional("DEBUG")]
    private void InvokeDebug(string message)
    {
        Action<string>? handler = Debug;
        if (handler != null)
        {
            try
            {
                handler(message);
            }
            catch (Exception)
            {
            }
        }
    }
}
