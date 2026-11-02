using Google.Protobuf;
using SuMultiplayer.Engine;
using SuMultiplayer.Join;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using SuMultiplayer.Terrain;
using SuMultiplayer.Transfer;
using PTransfer = SuMultiplayer.Protocol.Transfer;

internal static class Program
{
    private const string SessionId = "0123456789abcdef0123456789abcdef";

    private static int Main()
    {
        try
        {
            VerifyClientMissingGate();
            VerifyLateAndCompletedMissingRequests();
            Console.WriteLine("World transfer regressions passed.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static void VerifyClientMissingGate()
    {
        // Source: SuMultiplayer/Transfer/WorldTransfer.cs:WorldTransferClient.RequestMissing
        var channel = new RecordingChannel();
        var client = new WorldTransferClient(channel);
        const int transferId = 71001;
        var begin = PTransfer.WorldBegin.Parser.ParseFrom(WorldTransferCodec.EncodeBegin(
            transferId, "TestWorld", "data:/Worlds/Test", 8, 2, 4, SessionId, 0));
        Require(client.HandleBegin(begin), "valid WorldBegin was rejected");
        Require(client.RequestMissing(1000) == 0 && channel.Sent.Count == 0,
            "client requested missing chunks before WorldEnd");

        byte[] first = { 1, 2, 3, 4 };
        byte[] second = { 5, 6, 7, 8 };
        client.HandleChunk(PTransfer.WorldChunk.Parser.ParseFrom(WorldTransferCodec.EncodeChunk(
            transferId, 0, 2, WorldTransferReceiver.ComputeChecksum(first), first, 0, first.Length)));
        Require(!client.HandleEnd(new PTransfer.WorldEnd { TransferId = transferId, Ok = true }, 2000),
            "incomplete WorldEnd unexpectedly completed the transfer");
        Require(client.RequestMissing(2000) == 1 && channel.Sent.Count == 1,
            "client did not request the missing chunk after WorldEnd");
        Require(WorldTransferCodec.TryDecodeMissing(channel.Sent[0].Payload,
                out int requestedId, out List<int> missing) && requestedId == transferId &&
            missing.Count == 1 && missing[0] == 1,
            "missing request did not identify the active transfer and missing index");

        client.HandleChunk(PTransfer.WorldChunk.Parser.ParseFrom(WorldTransferCodec.EncodeChunk(
            transferId, 1, 2, WorldTransferReceiver.ComputeChecksum(second), second, 0, second.Length)));
        Require(client.HandleEnd(new PTransfer.WorldEnd { TransferId = transferId, Ok = true }, 3000),
            "transfer did not complete after the missing chunk arrived");
        Require(!client.HandleEnd(new PTransfer.WorldEnd { TransferId = transferId + 1, Ok = true }, 4000),
            "WorldEnd from another transfer was accepted");
    }

    private static void VerifyLateAndCompletedMissingRequests()
    {
        // Source: SuMultiplayer/Modules/WorldTransferModule.cs:WorldTransferModule.OnWorldMissing
        var transport = new RecordingTransport();
        var diagnostics = new RecordingDiagnostics();
        var registry = new MessageRegistry();
        var sender = new MessageChannel(registry,
            (clientId, type, payload) => transport.TrySendTo(clientId, type, payload),
            "world-transfer-regression");
        var context = new TestModuleContext(diagnostics);
        context.Add<ITransportPort>(transport);
        context.Add<ITransportControlPort>(new TestTransportControl());
        context.Add<IWorldSessionPort>(new TestWorldSession());
        context.Add<IMessageRegistryPort>(sender);

        byte[] pak = new byte[640 * 1024];
        for (int i = 0; i < pak.Length; i++) pak[i] = (byte)(i * 31 + 7);
        var module = new WorldTransferModule(new SyncConfig(), new TestExporter(pak),
            new TestImporter(), new TestDownloadedStore());
        module.Attach(context);
        transport.RaiseClientJoined(1, "test-client", "test-identity", false);

        long frame = 0;
        void Tick() => module.Tick(new ModuleTickContext(++frame, frame / 60.0,
            1f / 60f, true, true));
        bool WaitUntil(Func<bool> condition, int maxTicks = 240)
        {
            for (int i = 0; i < maxTicks; i++)
            {
                Tick();
                if (condition()) return true;
            }
            return condition();
        }

        Tick();
        int firstTransferId = FindBeginTransferId(transport.Sent, 0);
        Require(firstTransferId != 0, "host did not start the initial transfer");
        Require(WaitUntil(() => CountChunkIndex(transport.Sent, firstTransferId, 0) == 1),
            "host did not send the first chunk of the initial transfer");
        int chunkZeroBefore = CountChunkIndex(transport.Sent, firstTransferId, 0);
        Require(chunkZeroBefore == 1, "initial transfer did not send chunk zero once");

        DispatchMissing(registry, firstTransferId + 1);
        Require(diagnostics.Contains("活动传输不匹配"),
            "active transfer accepted a WorldMissing with a different transfer id");
        Require(WaitUntil(() => CountMessages(transport.Sent, MessageType.WorldEnd) == 1),
            "initial transfer did not finish");
        Require(CountChunkIndex(transport.Sent, firstTransferId, 0) == chunkZeroBefore,
            "mismatched active request retransmitted a chunk");

        int initialBegins = CountMessages(transport.Sent, MessageType.WorldBegin);
        DispatchMissing(registry, firstTransferId + 1);
        Tick();
        Require(CountMessages(transport.Sent, MessageType.WorldBegin) == initialBegins,
            "late request from an unknown transfer started another full world send");
        Require(diagnostics.Contains("不属于活动或最近完成的传输"),
            "late unknown transfer request was not reported as stale");

        DispatchMissing(registry, firstTransferId);
        Require(WaitUntil(() => CountMessages(transport.Sent, MessageType.WorldEnd) == 2),
            "matching completed transfer request did not start a recovery snapshot");
        int secondTransferId = FindBeginTransferId(transport.Sent, 1);
        Require(secondTransferId != firstTransferId,
            "recovery snapshot reused the completed transfer id");
        Require(diagnostics.Contains("完整 End 后仍缺片"),
            "valid post-End repair request was not diagnosed");

        module.Detach(ModuleDetachReason.HostShutdown);
    }

    private static void DispatchMissing(MessageRegistry registry, int transferId)
    {
        byte[] payload = WorldTransferCodec.EncodeMissing(transferId, new[] { 0 });
        var frame = new ReceivedFrame(1, transferId, MessageType.WorldMissing, payload);
        Require(registry.Dispatch(in frame), "WorldMissing was not dispatched");
    }

    private static int FindBeginTransferId(IReadOnlyList<SentMessage> sent, int ordinal)
    {
        for (int i = 0; i < sent.Count; i++)
        {
            if (sent[i].Type != MessageType.WorldBegin) continue;
            if (ordinal-- != 0) continue;
            Require(WorldTransferCodec.TryDecodeBegin(sent[i].Payload,
                    out int transferId, out _, out _, out _, out _, out _, out _, out _),
                "host sent an invalid WorldBegin");
            return transferId;
        }
        return 0;
    }

    private static int CountMessages(IReadOnlyList<SentMessage> sent, MessageType type)
    {
        int count = 0;
        for (int i = 0; i < sent.Count; i++)
            if (sent[i].Type == type) count++;
        return count;
    }

    private static int CountChunkIndex(IReadOnlyList<SentMessage> sent, int transferId, int index)
    {
        int count = 0;
        for (int i = 0; i < sent.Count; i++)
        {
            if (sent[i].Type != MessageType.WorldChunk) continue;
            PTransfer.WorldChunk chunk = PTransfer.WorldChunk.Parser.ParseFrom(sent[i].Payload);
            if (chunk.TransferId == transferId && chunk.Index == index) count++;
        }
        return count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record SentMessage(int ClientId, MessageType Type, byte[] Payload);

    private sealed class RecordingChannel : ITransferChannel
    {
        public readonly List<SentMessage> Sent = new();
        public bool IsConnected => true;
        public string StateText => "recording";
        public bool TrySend(MessageType type, byte[] payload)
        {
            Sent.Add(new SentMessage(0, type, payload));
            return true;
        }
    }

    private sealed class RecordingTransport : ITransportPort
    {
        public readonly List<SentMessage> Sent = new();
        private event Action<int, string, string, bool> m_clientJoined;
        public bool IsConnected => true;
        public string StateText => "recording-host";
        public string HostIdentity => string.Empty;
        public string HostName => string.Empty;
        public int LocalClientId => -1;
        public event Action<ReceivedFrame> FrameArrived { add { } remove { } }
        public event Action<int, string, string, bool> ClientJoined
        {
            add { m_clientJoined += value; }
            remove { m_clientJoined -= value; }
        }
        public event Action<int, string> ClientLeft { add { } remove { } }
        public bool TrySend(MessageType type, byte[] payload) => TrySendTo(1, type, payload);
        public bool TrySendTo(int clientId, MessageType type, byte[] payload)
        {
            Sent.Add(new SentMessage(clientId, type, payload));
            return true;
        }
        public bool IsClientSendable(int clientId) => clientId == 1;
        public void RaiseClientJoined(int id, string name, string identity, bool rejoin) =>
            m_clientJoined?.Invoke(id, name, identity, rejoin);
    }

    private sealed class TestTransportControl : ITransportControlPort
    {
        public bool StartHostServer(int port, out string reason) { reason = null; return true; }
        public bool ConnectTo(string host, int port, out string reason) { reason = "host test"; return false; }
        public void StopChannel() { }
        public void StopChannelWithBye(string reason) { }
        public bool IsChannelRunning => true;
        public bool IsHosting => true;
        public int ConnectedPlayerCount => 1;
        public string ChannelStateText => "test-host";
    }

    private sealed class TestWorldSession : IWorldSessionPort
    {
        public string SessionId => SessionIdValue;
        public const string SessionIdValue = "0123456789abcdef0123456789abcdef";
        public long CurrentChangeSequence => 0;
        public void OnClientWorldInstalled(string worldDirectory, string sessionId, long baseSequence) { }
    }

    private sealed class TestExporter : IWorldExportSource
    {
        private readonly byte[] m_pak;
        public TestExporter(byte[] pak) { m_pak = pak; }
        public bool TryExport(out byte[] pak, out string worldName, out string directoryName, out string reason)
        {
            pak = m_pak;
            worldName = "TestWorld";
            directoryName = "data:/Worlds/TestWorld";
            reason = null;
            return true;
        }
    }

    private sealed class TestImporter : IWorldImporter
    {
        public bool TryImport(byte[] pak, out string installedDirectory, out string reason)
        {
            installedDirectory = null;
            reason = "not used by host-side regression";
            return false;
        }
    }

    private sealed class TestDownloadedStore : IDownloadedWorldStore
    {
        public IReadOnlyList<string> Registered => Array.Empty<string>();
        public void Register(string directory) { }
        public void SaveRegistry(IReadOnlyList<string> directories) { }
        public bool Delete(string directory, out string reason) { reason = null; return true; }
        public void RegisterSession(string directory, string sessionId, long baseSequence) { }
        public bool TryGetSession(string directory, out string sessionId, out long baseSequence)
        { sessionId = string.Empty; baseSequence = 0; return false; }
        public void ForgetSession(string directory) { }
    }

    private sealed class TestModuleContext : IModuleContext
    {
        private readonly Dictionary<Type, object> m_ports = new();
        public TestModuleContext(IModuleDiagnostics diagnostics) { Diagnostics = diagnostics; }
        public string ModuleId => "world-transfer-regression";
        public ICapabilityRegistry Capabilities { get; } = new TestCapabilities();
        public IModuleDiagnostics Diagnostics { get; }
        public void Add<T>(T port) where T : class => m_ports[typeof(T)] = port;
        public bool TryGetPort<T>(out T port) where T : class
        {
            if (m_ports.TryGetValue(typeof(T), out object value))
            {
                port = (T)value;
                return true;
            }
            port = null;
            return false;
        }
    }

    private sealed class TestCapabilities : ICapabilityRegistry
    {
        public bool Has(string capability) => false;
        public IReadOnlyCollection<string> Active => Array.Empty<string>();
        public event Action<string, bool> CapabilityChanged { add { } remove { } }
    }

    private sealed class RecordingDiagnostics : IModuleDiagnostics
    {
        private readonly List<string> m_lines = new();
        public void Info(string moduleId, string message) => m_lines.Add(message);
        public void Warn(string moduleId, string message) => m_lines.Add(message);
        public void Error(string moduleId, string message, Exception exception) => m_lines.Add(message);
        public bool Contains(string value) => m_lines.Any(line => line.Contains(value, StringComparison.Ordinal));
    }
}
