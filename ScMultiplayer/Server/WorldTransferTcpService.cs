using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ScMultiplayer
{
    internal sealed class WorldTransferTcpTicket
    {
        public Guid Token;
        public byte[] Sha256;
    }

    internal sealed class WorldTransferTcpService : IDisposable
    {
        private sealed class Payload
        {
            public int TransferId;
            public int ClientId;
            public IPAddress ClientAddress;
            public byte[] Data;
            public byte[] Sha256;
        }

        private const uint ProtocolMagic = 0x31544353;
        private const byte ProtocolVersion = 1;
        private const int RequestLength = 29;
        private const int ResponseLength = 41;
        private const int StreamBufferSize = 64 * 1024;
        private const int MaximumWorldSize = 64 * 1024 * 1024;

        private readonly ConcurrentDictionary<Guid, Payload> m_payloads =
            new ConcurrentDictionary<Guid, Payload>();
        private readonly ConcurrentDictionary<TcpClient, byte> m_clients =
            new ConcurrentDictionary<TcpClient, byte>();
        private readonly Action<string> m_information;
        private readonly Action<string> m_warning;
        private readonly Action<string> m_error;
        private TcpListener m_listener;
        private CancellationTokenSource m_cancellation;
        private Task m_acceptTask;

        public WorldTransferTcpService(
            Action<string> information,
            Action<string> warning,
            Action<string> error)
        {
            m_information = information;
            m_warning = warning;
            m_error = error;
        }

        public bool IsRunning => m_listener != null && m_cancellation?.IsCancellationRequested == false;

        public int Port { get; private set; }

        // Source: System.Net.Sockets.TcpListener.Start
        public void Start(int port)
        {
            if (IsRunning)
                throw new InvalidOperationException("TCP world transfer service is already running.");
            m_cancellation = new CancellationTokenSource();
            m_listener = new TcpListener(IPAddress.Any, port);
            m_listener.Start(16);
            Port = port;
            m_acceptTask = AcceptLoopAsync(m_cancellation.Token);
            m_information?.Invoke($"TCP world transfer service listening on port {port}");
        }

        public WorldTransferTcpTicket Register(
            int transferId,
            int clientId,
            IPAddress clientAddress,
            byte[] data)
        {
            if (!IsRunning || data == null || data.Length <= 0 || data.Length > MaximumWorldSize)
                return null;
            Guid token;
            do token = Guid.NewGuid();
            while (!m_payloads.TryAdd(token, new Payload
            {
                TransferId = transferId,
                ClientId = clientId,
                ClientAddress = clientAddress,
                Data = data,
                Sha256 = SHA256.HashData(data)
            }));
            Payload payload = m_payloads[token];
            return new WorldTransferTcpTicket
            {
                Token = token,
                Sha256 = (byte[])payload.Sha256.Clone()
            };
        }

        public void Remove(Guid token)
        {
            if (token != Guid.Empty)
                m_payloads.TryRemove(token, out _);
        }

        public void ClearTransfers()
        {
            m_payloads.Clear();
        }

        public void Dispose()
        {
            CancellationTokenSource cancellation = m_cancellation;
            m_cancellation = null;
            try { cancellation?.Cancel(); }
            catch { }
            try { m_listener?.Stop(); }
            catch { }
            m_listener = null;
            foreach (TcpClient client in m_clients.Keys)
            {
                try { client.Dispose(); }
                catch { }
            }
            m_clients.Clear();
            m_payloads.Clear();
            try { m_acceptTask?.Wait(1000); }
            catch { }
            m_acceptTask = null;
            cancellation?.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await m_listener.AcceptTcpClientAsync(cancellationToken);
                    m_clients.TryAdd(client, 0);
                    _ = ServeClientAsync(client, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        m_error?.Invoke("TCP accept failed: " + exception.Message);
                }
            }
        }

        private async Task ServeClientAsync(TcpClient client, CancellationToken serviceCancellation)
        {
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(serviceCancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            CancellationToken cancellationToken = timeout.Token;
            try
            {
                client.NoDelay = true;
                client.SendBufferSize = 256 * 1024;
                using NetworkStream stream = client.GetStream();
                byte[] request = new byte[RequestLength];
                await stream.ReadExactlyAsync(request.AsMemory(), cancellationToken);
                if (BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(0, 4)) != ProtocolMagic ||
                    request[4] != ProtocolVersion)
                {
                    await WriteFailureAsync(stream, cancellationToken);
                    return;
                }

                int transferId = BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(5, 4));
                int clientId = BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(9, 4));
                Guid token = new Guid(request.AsSpan(13, 16));
                if (!m_payloads.TryGetValue(token, out Payload payload) ||
                    payload.TransferId != transferId || payload.ClientId != clientId ||
                    !IsExpectedAddress(payload.ClientAddress,
                        (client.Client.RemoteEndPoint as IPEndPoint)?.Address))
                {
                    await WriteFailureAsync(stream, cancellationToken);
                    return;
                }

                byte[] response = new byte[ResponseLength];
                BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(0, 4), ProtocolMagic);
                response[4] = 1;
                BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(5, 4), payload.Data.Length);
                payload.Sha256.CopyTo(response, 9);
                await stream.WriteAsync(response.AsMemory(), cancellationToken);

                Stopwatch stopwatch = Stopwatch.StartNew();
                for (int offset = 0; offset < payload.Data.Length; offset += StreamBufferSize)
                {
                    int count = Math.Min(StreamBufferSize, payload.Data.Length - offset);
                    await stream.WriteAsync(payload.Data.AsMemory(offset, count), cancellationToken);
                }
                await stream.FlushAsync(cancellationToken);
                m_information?.Invoke($"TCP world stream served: ClientID={clientId}, " +
                    $"Transfer={transferId}, Bytes={payload.Data.Length}, " +
                    $"Seconds={stopwatch.Elapsed.TotalSeconds:0.00}");
            }
            catch (OperationCanceledException)
            {
                if (!serviceCancellation.IsCancellationRequested)
                    m_warning?.Invoke("TCP world stream timed out");
            }
            catch (Exception exception)
            {
                if (!serviceCancellation.IsCancellationRequested)
                    m_warning?.Invoke("TCP world stream failed: " + exception.Message);
            }
            finally
            {
                m_clients.TryRemove(client, out _);
                try { client.Dispose(); }
                catch { }
            }
        }

        private static bool IsExpectedAddress(IPAddress expected, IPAddress actual)
        {
            if (expected == null || actual == null)
                return true;
            return expected.MapToIPv6().Equals(actual.MapToIPv6());
        }

        private static async Task WriteFailureAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            byte[] response = new byte[ResponseLength];
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(0, 4), ProtocolMagic);
            response[4] = 0;
            await stream.WriteAsync(response.AsMemory(), cancellationToken);
        }

        // Source: System.Net.Sockets.TcpClient.ConnectAsync
        public static async Task<byte[]> DownloadAsync(
            IPEndPoint serverAddress,
            int port,
            int transferId,
            int clientId,
            Guid token,
            int expectedLength,
            byte[] expectedSha256,
            Action<int> progress,
            CancellationToken cancellationToken)
        {
            if (serverAddress == null || port <= 0 || port > 65535 || token == Guid.Empty ||
                expectedLength <= 0 || expectedLength > MaximumWorldSize ||
                expectedSha256 == null || expectedSha256.Length != 32)
            {
                throw new InvalidOperationException("Invalid TCP world transfer manifest.");
            }

            using TcpClient client = new TcpClient(AddressFamily.InterNetwork);
            client.NoDelay = true;
            client.ReceiveBufferSize = 256 * 1024;
            await client.ConnectAsync(serverAddress.Address, port, cancellationToken);
            using NetworkStream stream = client.GetStream();

            byte[] request = new byte[RequestLength];
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(0, 4), ProtocolMagic);
            request[4] = ProtocolVersion;
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(5, 4), transferId);
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(9, 4), clientId);
            token.TryWriteBytes(request.AsSpan(13, 16));
            await stream.WriteAsync(request.AsMemory(), cancellationToken);

            byte[] response = new byte[ResponseLength];
            await stream.ReadExactlyAsync(response.AsMemory(), cancellationToken);
            if (BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(0, 4)) != ProtocolMagic ||
                response[4] != 1)
            {
                throw new InvalidOperationException("Host rejected the TCP world transfer ticket.");
            }
            int length = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(5, 4));
            byte[] responseSha256 = response.AsSpan(9, 32).ToArray();
            if (length != expectedLength ||
                !CryptographicOperations.FixedTimeEquals(responseSha256, expectedSha256))
            {
                throw new InvalidOperationException("TCP world transfer metadata did not match the manifest.");
            }

            byte[] data = new byte[length];
            int received = 0;
            while (received < data.Length)
            {
                int count = await stream.ReadAsync(
                    data.AsMemory(received, Math.Min(StreamBufferSize, data.Length - received)),
                    cancellationToken);
                if (count <= 0)
                    throw new InvalidOperationException("TCP world stream ended before the map was complete.");
                received += count;
                progress?.Invoke(received);
            }
            byte[] actualSha256 = SHA256.HashData(data);
            if (!CryptographicOperations.FixedTimeEquals(actualSha256, expectedSha256))
                throw new InvalidOperationException("TCP world transfer checksum failed.");
            return data;
        }
    }
}
