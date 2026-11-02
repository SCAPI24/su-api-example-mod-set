using Engine;
using System;
using System.Collections.Generic;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;
using SuMultiplayer.Protocol.Circuit;
using SuMultiplayer.Protocol;

namespace SuMultiplayer.Modules
{
    public enum CircuitInteractionKind : byte
    {
        ButtonPress = 1,
        SwitchToggle = 2,
        Rotate = 3,
        FurnitureSwitch = 4
    }

    public readonly struct CircuitInteractionRequestData
    {
        public CircuitInteractionRequestData(long requestId, CircuitInteractionKind kind,
            int x, int y, int z, int mountingFace, int expectedCellValue,
            float rayX, float rayY, float rayZ, float directionX, float directionY,
            float directionZ, float reach)
        {
            RequestId = requestId;
            Kind = kind;
            X = x;
            Y = y;
            Z = z;
            MountingFace = mountingFace;
            ExpectedCellValue = expectedCellValue;
            RayX = rayX;
            RayY = rayY;
            RayZ = rayZ;
            DirectionX = directionX;
            DirectionY = directionY;
            DirectionZ = directionZ;
            Reach = reach;
        }

        public long RequestId { get; }
        public CircuitInteractionKind Kind { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int MountingFace { get; }
        public int ExpectedCellValue { get; }
        public float RayX { get; }
        public float RayY { get; }
        public float RayZ { get; }
        public float DirectionX { get; }
        public float DirectionY { get; }
        public float DirectionZ { get; }
        public float Reach { get; }
    }

    public interface ICircuitTransportPort
    {
        bool IsHosting { get; }
        bool IsClient { get; }
        bool IsClientCircuitAttached { get; }
        int LocalClientId { get; }
        string SessionId { get; }
        long TerrainSequence { get; }
        event Action<int, ReceivedFrame> FrameArrived;
        event Action<int> PeerAttached;
        event Action<int, string> PeerDetached;
        bool TrySendTo(int clientId, MessageType type, byte[] payload);
        bool TrySendHost(MessageType type, byte[] payload);
        bool TryCreateHostTicket(int clientId, out byte[] ticket);
        bool IsHostCircuitAttached(int clientId);
        int CopyClientChunkScope(int clientId, List<ChunkKey> target);
        void CopyAttachedPeerIds(List<int> target);
    }

    public interface ICircuitUseAuthorityPort
    {
        bool TryExecuteCircuitUse(int clientId, in BlockUseRequestData request,
            out string reason);
    }

    public interface ICircuitInteractionHostPort
    {
        bool TryBuildHostUseRequest(int clientId,
            in CircuitInteractionRequestData circuitRequest,
            out BlockUseRequestData useRequest, out string reason);
    }

    public interface ICircuitWorldPort
    {
        bool IsClientReplica { get; }
        bool IsWorldAccelerated { get; }
        bool IsReady { get; }
        bool TryGetClock(out int circuitStep, out double totalElapsedGameTime,
            out double timeOfDayOffset, out bool paused, out string error);
        int CopyLoadedChunks(List<ChunkKey> target);
        // Source: Survivalcraft/Game/TerrainChunk.cs:TerrainChunk.State
        bool IsChunkReady(ChunkKey key);
        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.GetElectricElement
        bool AreElementsReady(IList<CircuitElementState> states);
        bool TryCaptureChunk(ChunkKey key, List<CircuitElementState> target,
            out string error);
        bool TryCaptureChunks(IList<ChunkKey> keys,
            IDictionary<ChunkKey, List<CircuitElementState>> target, out string error);
        bool TryCaptureChunksBestEffort(IList<ChunkKey> keys,
            IDictionary<ChunkKey, List<CircuitElementState>> target,
            IDictionary<ChunkKey, string> chunkErrors, out string error);
        bool Apply(CircuitElementState state, int hostStep, out string error);
        bool AlignClientCircuitStep(int hostStep, bool restoreAuthoritativeStates,
            out string error);
        bool ApplyClock(double totalElapsedGameTime, double timeOfDayOffset,
            out string error);
        void SetDeterministicRandomSeed(uint seed, bool hostAuthority);
        uint ComputeHash(IEnumerable<CircuitElementState> states);
        uint ComputeCapturedHash(IList<CircuitElementState> states);
        bool TryGetCellValue(int x, int y, int z, out int value);
        string DescribeDiagnostics();
        void Pump();
        event Action<ChunkKey, CellEdit> HostCellChanged;
        event Action<ChunkKey, CellEdit> ReplicaCellApplied;
    }

    public interface ICircuitPresentationPort : ICircuitInteractionHostPort
    {
        void SetCircuitRequestSink(Func<CircuitInteractionRequestData, bool> sink);
        void ResolvePresentation(long requestId, int authoritativeCellValue);
        void RejectPresentation(long requestId, int? authoritativeCellValue);
        void ApplyAuthoritativeCell(int x, int y, int z, int mountingFace,
            int authoritativeCellValue);
        void DetachPresentation();
    }

}
