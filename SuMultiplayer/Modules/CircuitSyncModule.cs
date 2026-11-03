using Engine;
using Google.Protobuf;
using SuMultiplayer.Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using SuMultiplayer.Protocol.Circuit;
using SuMultiplayer.Protocol.Telemetry;
using SuMultiplayer.Terrain;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SuMultiplayer.Modules
{
    // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
    // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
    // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer
    public sealed class CircuitSyncModule : IMultiplayerModule
    {
        private const int SyncIntervalMs = 50;
        private const int HashIntervalMs = 200;
        private const int MaximumSnapshotElements = 128;
        private const int MaximumInterestChunks = 512;
        private const int MaximumPendingSnapshots = 1024;
        private const int MaximumPendingOperations = 256;
        private const int MaximumCircuitFrameBytes = 60 * 1024;
        private const int SnapshotTimeoutMs = 10000;
        private const int ClientScopeRefreshIntervalMs = 250;
        private const int ClientScopeRequestTimeoutMs = 2000;

        private sealed class SnapshotAssembly
        {
            public uint Epoch;
            public ulong SnapshotId;
            public int HostStep;
            public int ChunkX;
            public int ChunkZ;
            public int PartCount;
            public bool Full;
            public int LastCommittedEventSequence;
            public uint ChunkCommittedEventSequence;
            public int TimelineGeneration;
            public bool HasContext;
            public long CreatedMs;
            public long RequiredTerrainSequence;
            public long RequiredTopologyGeneration;
            public readonly SortedDictionary<int, CircuitSnapshotPart> Parts =
                new SortedDictionary<int, CircuitSnapshotPart>();
        }

        private sealed class PeerState
        {
            // A paused clock is a state barrier: each peer must receive one
            // complete snapshot at the host's paused step before it can hold
            // that step as authoritative.
            public bool LastPaused;
            public bool AccelerationResumePending;
            public bool PauseBarrierActive;
            public bool PauseBarrierComplete;
            public ulong PendingScopeRequestId;
            public readonly HashSet<ChunkKey> PendingScopeTerrainChunks = new HashSet<ChunkKey>();
            public readonly HashSet<ChunkKey> CircuitScope = new HashSet<ChunkKey>();
            public readonly HashSet<ulong> PendingOperationIds = new HashSet<ulong>();
            public int PauseBarrierStep;
            public int PauseBarrierTimelineGeneration;
            public readonly HashSet<ChunkKey> PauseBarrierChunks = new HashSet<ChunkKey>();
            public readonly HashSet<ChunkKey> PauseBarrierApplied = new HashSet<ChunkKey>();
            public readonly Dictionary<ulong, ChunkKey> PauseBarrierSnapshots =
                new Dictionary<ulong, ChunkKey>();
            public readonly Dictionary<ulong, long> PauseBarrierSnapshotTimes =
                new Dictionary<ulong, long>();
            public readonly HashSet<ChunkKey> NeedsFullSnapshot = new HashSet<ChunkKey>();
            public readonly Dictionary<ChunkKey, SnapshotReason> NeedsFullSnapshotReasons =
                new Dictionary<ChunkKey, SnapshotReason>();
            public readonly Dictionary<ChunkKey, Dictionary<string, byte[]>> LastStates =
                new Dictionary<ChunkKey, Dictionary<string, byte[]>>();
            public readonly Dictionary<ChunkKey, Dictionary<string, uint>> LastElementTypes =
                new Dictionary<ChunkKey, Dictionary<string, uint>>();
            public readonly HashSet<ChunkKey> RepairRequests = new HashSet<ChunkKey>();
            public readonly Dictionary<ChunkKey, SnapshotReason> RepairRequestReasons =
                new Dictionary<ChunkKey, SnapshotReason>();
            public readonly Dictionary<ulong, CircuitOperationResult> OperationResults =
                new Dictionary<ulong, CircuitOperationResult>();
            public readonly Queue<ulong> OperationResultOrder = new Queue<ulong>();
            public readonly Queue<long> OperationTimes = new Queue<long>();
            public readonly Dictionary<ulong, SnapshotBoundary> SnapshotBoundaries =
                new Dictionary<ulong, SnapshotBoundary>();
        }

        private sealed class PeerSyncScope
        {
            public int ClientId;
            public PeerState Peer;
            public readonly List<ChunkKey> Chunks = new List<ChunkKey>(64);
        }

        private sealed class PendingHostOperation
        {
            public int ClientId;
            public int HostStep;
            public long TerrainSequenceBeforeApply;
            public int AuthoritativeCellValue;
            public bool HostUseExecuted;
            public bool WaitingForTerrainPublication;
            public CircuitEventRequest Request;
            public BlockUseRequestData UseRequest;
            public CircuitEvent Event;
        }

        private const int MaximumCircuitRttMs = 200;
        private const int OperationStepMarginMs = 20;
        private const int CircuitStepDurationMs = 10;

        private enum SnapshotReason : byte
        {
            InitialScope,
            HashMismatch,
            RepairAckMismatch,
            ClientRequest,
            NeedsFullRecovery,
            PauseBarrier,
            TopologyChange,
            SendFailureRecovery
        }

        private readonly struct SnapshotBoundary
        {
            public SnapshotBoundary(ChunkKey chunk, HashBoundary boundary, bool full,
                uint chunkCommittedEventSequence, SnapshotReason? reason, int stateCount)
            {
                Chunk = chunk;
                Boundary = boundary;
                Full = full;
                ChunkCommittedEventSequence = chunkCommittedEventSequence;
                Reason = reason;
                StateCount = stateCount;
            }

            public ChunkKey Chunk { get; }
            public HashBoundary Boundary { get; }
            public bool Full { get; }
            public uint ChunkCommittedEventSequence { get; }
            public SnapshotReason? Reason { get; }
            public int StateCount { get; }
        }

        private readonly struct HashBoundary
        {
            public HashBoundary(int step, long terrainSequence, long topologyGeneration,
                uint committedEventSequence, int timelineGeneration, uint hash)
            {
                Step = step;
                TerrainSequence = terrainSequence;
                TopologyGeneration = topologyGeneration;
                CommittedEventSequence = committedEventSequence;
                TimelineGeneration = timelineGeneration;
                Hash = hash;
            }

            public int Step { get; }
            public long TerrainSequence { get; }
            public long TopologyGeneration { get; }
            public uint CommittedEventSequence { get; }
            public int TimelineGeneration { get; }
            public uint Hash { get; }
        }

        private readonly struct AppliedBoundary : IEquatable<AppliedBoundary>
        {
            public AppliedBoundary(int step, long terrainSequence, long topologyGeneration,
                int committedEventSequence, int timelineGeneration)
            {
                Step = step;
                TerrainSequence = terrainSequence;
                TopologyGeneration = topologyGeneration;
                CommittedEventSequence = committedEventSequence;
                TimelineGeneration = timelineGeneration;
            }

            public int Step { get; }
            public long TerrainSequence { get; }
            public long TopologyGeneration { get; }
            public int CommittedEventSequence { get; }
            public int TimelineGeneration { get; }

            public bool Equals(AppliedBoundary other) => Step == other.Step &&
                TerrainSequence == other.TerrainSequence &&
                CommittedEventSequence == other.CommittedEventSequence &&
                TimelineGeneration == other.TimelineGeneration &&
                TopologyGeneration == other.TopologyGeneration;

            public override bool Equals(object obj) => obj is AppliedBoundary other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Step, TerrainSequence,
                TopologyGeneration, CommittedEventSequence, TimelineGeneration);
        }

        private readonly ModuleDescriptor m_descriptor;
        private readonly ICircuitWorldPort m_world;
        private readonly ICircuitPresentationPort m_presentation;
        private readonly Func<double> m_clock;
        private readonly Func<uint> m_randomSeedProvider;
        private readonly Func<ChunkKey, IList<CircuitElementState>, int, string> m_reconcileSnapshot;
        private readonly List<int> m_peers = new List<int>(8);
        private readonly List<ChunkKey> m_chunks = new List<ChunkKey>(64);
        private readonly List<PeerSyncScope> m_peerSyncScopes = new List<PeerSyncScope>(8);
        private readonly List<ChunkKey> m_captureChunkUnion = new List<ChunkKey>(128);
        private readonly HashSet<ChunkKey> m_captureChunkSet = new HashSet<ChunkKey>();
        private readonly HashSet<ChunkKey> m_hostCircuitChunks = new HashSet<ChunkKey>();
        private readonly List<CircuitElementState> m_states = new List<CircuitElementState>(256);
        private CircuitProbeRecorder m_probe = new CircuitProbeRecorder();
        private CircuitProbeRecorder m_hostProbe = new CircuitProbeRecorder();
        private CircuitProbeRecorder m_activeProbe;
        private readonly Dictionary<ChunkKey, List<CircuitElementState>> m_captureStatesByChunk =
            new Dictionary<ChunkKey, List<CircuitElementState>>();
        private readonly Dictionary<ChunkKey, string> m_captureErrorsByChunk =
            new Dictionary<ChunkKey, string>();
        private readonly Dictionary<ChunkKey, uint> m_hostCheckpointHashes =
            new Dictionary<ChunkKey, uint>();
        private readonly Dictionary<int, PeerState> m_peerStates = new Dictionary<int, PeerState>();
        private readonly Dictionary<ulong, CircuitOperationResult> m_pendingOperations =
            new Dictionary<ulong, CircuitOperationResult>();
        private readonly Dictionary<ulong, SnapshotAssembly> m_snapshotAssemblies =
            new Dictionary<ulong, SnapshotAssembly>();
        // Snapshot parts are assembled in PartIndex order; reuse this main-thread scratch list.
        private readonly List<CircuitElementState> m_snapshotStates =
            new List<CircuitElementState>(MaximumSnapshotElements);
        private readonly List<CircuitElementState> m_deltaAckStates =
            new List<CircuitElementState>(MaximumSnapshotElements);
        private readonly SortedDictionary<int, List<PendingHostOperation>> m_hostOperationsByStep =
            new SortedDictionary<int, List<PendingHostOperation>>();
        private readonly List<PendingHostOperation> m_committedOperationsWaitingTerrain =
            new List<PendingHostOperation>();
        private readonly SortedDictionary<int, CircuitHashCheckpoint> m_clientHashCheckpoints =
            new SortedDictionary<int, CircuitHashCheckpoint>();
        private readonly List<ChunkKey> m_checkpointChunks = new List<ChunkKey>(64);
        private readonly Dictionary<ChunkKey, CircuitChunkHash> m_checkpointExpected =
            new Dictionary<ChunkKey, CircuitChunkHash>();
        private readonly Dictionary<ChunkKey, uint> m_clientChunkCommittedEventSequences =
            new Dictionary<ChunkKey, uint>();
        private readonly Dictionary<ChunkKey, uint> m_hostChunkCommittedEventSequences =
            new Dictionary<ChunkKey, uint>();
        private readonly Dictionary<ChunkKey, List<CircuitEvent>> m_lateCommittedOperations =
            new Dictionary<ChunkKey, List<CircuitEvent>>();
        private readonly HashSet<ChunkKey> m_clientRepairRequests = new HashSet<ChunkKey>();
        private readonly HashSet<ChunkKey> m_clientEventTopologyResyncRequests =
            new HashSet<ChunkKey>();
        private readonly Dictionary<ChunkKey, AppliedBoundary> m_clientChunkBoundaries =
            new Dictionary<ChunkKey, AppliedBoundary>();
        private readonly HashSet<ChunkKey> m_clientRequestedScope = new HashSet<ChunkKey>();
        private readonly HashSet<ChunkKey> m_clientInitializedScope = new HashSet<ChunkKey>();
        private readonly HashSet<ChunkKey> m_clientCircuitScope = new HashSet<ChunkKey>();
        private readonly HashSet<ChunkKey> m_clientDesiredCircuitScope = new HashSet<ChunkKey>();
        private readonly Dictionary<ChunkKey, long> m_hostTopologyGenerations = new Dictionary<ChunkKey, long>();
        private readonly Dictionary<ChunkKey, long> m_clientTopologyGenerations = new Dictionary<ChunkKey, long>();
        private readonly Dictionary<ChunkKey, long> m_hostTopologyTerrainSequences = new Dictionary<ChunkKey, long>();
        private readonly Dictionary<ChunkKey, long> m_clientTopologyTerrainSequences =
            new Dictionary<ChunkKey, long>();
        private readonly Dictionary<ChunkKey, Dictionary<string, int>> m_hostCircuitCellValues =
            new Dictionary<ChunkKey, Dictionary<string, int>>();
        private readonly Dictionary<ChunkKey, Dictionary<string, int>> m_clientCircuitCellValues =
            new Dictionary<ChunkKey, Dictionary<string, int>>();
        private readonly HashSet<ChunkKey> m_hostTopologyChangedChunks = new HashSet<ChunkKey>();
        private readonly HashSet<ChunkKey> m_clientScopeMembershipDirtyChunks = new HashSet<ChunkKey>();
        private readonly HashSet<ChunkKey> m_clientTopologyDirtyChunks = new HashSet<ChunkKey>();
        private readonly HashSet<ChunkKey> m_clientTopologyAwaitingSnapshot = new HashSet<ChunkKey>();
        private readonly List<CircuitOperationResult> m_deferredOperationResults =
            new List<CircuitOperationResult>();
        private readonly List<CircuitEvent> m_deferredEvents = new List<CircuitEvent>();

        private IModuleContext m_context;
        private ICircuitTransportPort m_transport;
        private ICircuitUseAuthorityPort m_useAuthority;
        private ICircuitInteractionHostPort m_interactionHost;
        private bool m_attached;
        private bool m_clientScopeRequested;
        private bool m_clientScopeAcknowledged;
        private bool m_clientScopeResponseReceived;
        private ulong m_clientPendingScopeRequestId;
        private bool m_wasCircuitAttached;
        private uint m_epoch;
        private uint m_nextOperationSequence;
        private uint m_nextCommittedEventSequence;
        private ulong m_nextSnapshotId;
        private long m_nextSyncMs;
        private long m_nextHostHashMs;
        private long m_nextClientScopeRefreshMs;
        private long m_clientScopeRequestSentMs;
        private string m_sessionId;
        private int m_lastCircuitStep;
        private int m_lastScheduledOperationStep;
        private int m_clientLastCommittedEventSequence;
        private int m_timelineGeneration;
        private bool m_hasConfiguredRandomSeed;
        private bool m_randomSeedAuthority;
        private bool m_clientClockInitialized;
        private bool m_clientClockPaused;
        private bool m_clientClockAccelerated;
        private bool m_clientResumePending;
        private bool m_hostWasAccelerated;
        private bool m_hostCircuitIndexInitialized;
        private uint m_configuredRandomSeed;
        private CircuitClock m_pendingClock;
        private CircuitScopeSnapshot m_pendingScopeSnapshot;
        private bool m_debugPauseRequested;
        private int m_debugPauseStep = int.MinValue;
        private readonly Dictionary<ChunkKey, List<CircuitElementState>> m_debugPauseStates =
            new Dictionary<ChunkKey, List<CircuitElementState>>();
        private long m_hostSyncRounds;
        private long m_hostAttachedPeers;
        private long m_hostScopeChunks;
        private long m_hostSevenSegmentStates;
        private long m_hostSnapshotChunksSent;
        private long m_hostSnapshotElementsSent;
        private long m_hostSnapshotPartsSent;
        private long m_hostDeltaStatesSent;
        private long m_hostSendFailures;
        private long m_clientScopeRequests;
        private long m_clientScopeChunks;
        private long m_clientSnapshotPartsReceived;
        private long m_clientSnapshotsApplied;
        private long m_clientStatesApplied;
        private long m_clientApplyFailures;
        private long m_clientWatermarkExpired;
        private long m_hostSyncWindowCalls;
        private long m_hostSyncWindowTicks;
        private long m_hostSyncWindowMaxTicks;
        private long m_hostHashMismatchesObserved;
        private long m_hostHashRepairChunksQueued;
        private long m_hostRepairAckHashMismatches;
        private long m_hostRepairAckChunksQueued;
        private long m_hostSnapshotRequestChunks;
        private long m_hostNeedsFullSnapshotChunksQueued;
        private long m_hostSnapshotsInitialScope;
        private long m_hostSnapshotsHashRepair;
        private long m_hostSnapshotsRepairAckRepair;
        private long m_hostSnapshotsClientRequest;
        private long m_hostSnapshotsNeedsFullRecovery;
        private long m_hostSnapshotsPauseBarrier;
        private long m_hostSnapshotsTopologyChange;
        private long m_hostSnapshotsSendFailureRecovery;
        private long m_hostSnapshotSendFailures;
        private long m_hostBatchCaptureFailures;
        private long m_hostBatchCaptureChunksFailed;
        private long m_hostRequestedSnapshotCaptureFailures;
        private long m_clientCheckpointMismatches;
        private long m_clientCheckpointCapturesFailed;
        private long m_clientCheckpointsProcessed;
        private long m_clientCheckpointChunksChecked;
        private long m_hostHashCheckpointsSent;
        private long m_hostHashChunksSent;
        private long m_hostRepairAckStepMismatches;
        private long m_clientSnapshotTimeouts;
        private long m_clientTopologyGenerationAlignments;
        private long m_clientStaleDeltasDiscarded;
        private long m_lastClientElementRecoveryMs;
        private long m_nextHostCaptureWarningMs;
        private int m_activePeerSyncScopeCount;
        private string m_probeTrigger = "none";
        private int m_probeScopeChunks;
        private int m_probeDirtyChunks;
        private int m_probePendingSnapshots;
        private int m_probePendingStates;
        private int m_probeHashChunks;
        private string m_clientSimulationBlockReason = "not-client";
        private string m_lastTopologyMismatch = "none";
        private string m_lastCheckpointMismatch = "none";
        private long m_hostTopologyBumps;
        private long m_hostUnknownTopologyBumps;
        private long m_clientTopologyBumps;
        private long m_clientUnknownTopologyBumps;
        private string m_lastHostTopologyBump = "none";
        private string m_lastClientTopologyBump = "none";

        public string StatusLine() => "circuit-sync: role=" +
            (m_transport?.IsHosting == true ? "host" : m_transport?.IsClient == true ? "client" : "idle") +
            " epoch=" + m_epoch + " step=" + m_lastCircuitStep +
            " host(rounds=" + m_hostSyncRounds + " attached=" + m_hostAttachedPeers +
            " scopeChunks=" + m_hostScopeChunks + " sevenSegments=" + m_hostSevenSegmentStates +
            " hashCheckpoints=" + m_hostHashCheckpointsSent + "/" + m_hostHashChunksSent +
            " snapshots=" + m_hostSnapshotChunksSent + "/" + m_hostSnapshotElementsSent +
            "/" + m_hostSnapshotPartsSent + " deltaStates=" + m_hostDeltaStatesSent +
            " sendFail=" + m_hostSendFailures + ")" +
            " client(scopeRequests=" + m_clientScopeRequests + "/" + m_clientScopeChunks +
            " parts=" + m_clientSnapshotPartsReceived + " applied=" +
            m_clientSnapshotsApplied + "/" + m_clientStatesApplied + " applyFail=" +
            m_clientApplyFailures + " checkpoints=" + m_clientCheckpointsProcessed + "/" +
            m_clientCheckpointChunksChecked + " mismatches=" +
            m_clientCheckpointMismatches + " repairAckStepSkips=" +
            m_hostRepairAckStepMismatches + " watermarkExpired=" + m_clientWatermarkExpired +
            " topologyAlign=" + m_clientTopologyGenerationAlignments +
            " staleDeltas=" + m_clientStaleDeltasDiscarded +
            " pendingSnapshots=" + DescribePendingSnapshotDiagnostics() +
            " circuitStep=" + CircuitSimulationPauseSignal.ClientHostStep +
            " localStep=" + DescribeLocalCircuitStep() +
            " circuitSim=" + CircuitSimulationPauseSignal.ClientSimulationEnabled +
            " clientBlock=" + m_clientSimulationBlockReason +
            " lastTopologyMismatch=" + m_lastTopologyMismatch +
            " topologyBumps=host(" + m_hostTopologyBumps + "/" +
            m_hostUnknownTopologyBumps + "Unknown) client(" + m_clientTopologyBumps + "/" +
            m_clientUnknownTopologyBumps + "Unknown)" +
            " lastHostTopologyBump=" + m_lastHostTopologyBump +
            " lastClientTopologyBump=" + m_lastClientTopologyBump +
            " lastCheckpointMismatch=" + m_lastCheckpointMismatch +
            " accelerated=" + CircuitSimulationPauseSignal.ClientAccelerated + ")" +
            " debugPaused=" + m_debugPauseRequested +
            " " + m_world.DescribeDiagnostics();

        public CircuitSyncSample SnapshotTelemetry()
        {
            long calls = m_hostSyncWindowCalls;
            long elapsed = m_hostSyncWindowTicks;
            long maximum = m_hostSyncWindowMaxTicks;
            m_hostSyncWindowCalls = 0;
            m_hostSyncWindowTicks = 0;
            m_hostSyncWindowMaxTicks = 0;
            var sample = new CircuitSyncSample
            {
            Rounds = (ulong)m_hostSyncRounds,
            AttachedPeers = (ulong)m_hostAttachedPeers,
            ScopeChunks = (ulong)m_hostScopeChunks,
            SevenSegmentStates = (ulong)m_hostSevenSegmentStates,
            SnapshotChunks = (ulong)m_hostSnapshotChunksSent,
            SnapshotElements = (ulong)m_hostSnapshotElementsSent,
            SnapshotParts = (ulong)m_hostSnapshotPartsSent,
            DeltaStates = (ulong)m_hostDeltaStatesSent,
            SendFailures = (ulong)m_hostSendFailures,
            ClientScopeRequests = (ulong)m_clientScopeRequests,
            ClientScopeChunks = (ulong)m_clientScopeChunks,
            ClientSnapshotParts = (ulong)m_clientSnapshotPartsReceived,
            ClientSnapshotsApplied = (ulong)m_clientSnapshotsApplied,
            ClientStatesApplied = (ulong)m_clientStatesApplied,
            ClientApplyFailures = (ulong)m_clientApplyFailures,
            ClientHashReports = 0,
            ClientHashChunks = 0,
            ClientWatermarkExpired = (ulong)m_clientWatermarkExpired,
                ClientSimulationEnabled = CircuitSimulationPauseSignal.ClientSimulationEnabled,
                HostSyncWindowCalls = (ulong)calls,
                HostSyncWindowMs = (float)(elapsed * 1000.0 / Stopwatch.Frequency),
                HostSyncWindowMaxMs = (float)(maximum * 1000.0 / Stopwatch.Frequency),
                HashMismatchesObserved = (ulong)m_hostHashMismatchesObserved,
                HashRepairChunksQueued = (ulong)m_hostHashRepairChunksQueued,
                RepairAckHashMismatches = (ulong)m_hostRepairAckHashMismatches,
                RepairAckChunksQueued = (ulong)m_hostRepairAckChunksQueued,
                SnapshotRequestChunks = (ulong)m_hostSnapshotRequestChunks,
                NeedsFullSnapshotChunksQueued = (ulong)m_hostNeedsFullSnapshotChunksQueued,
                SnapshotsInitialScope = (ulong)m_hostSnapshotsInitialScope,
                SnapshotsHashRepair = (ulong)m_hostSnapshotsHashRepair,
                SnapshotsRepairAckRepair = (ulong)m_hostSnapshotsRepairAckRepair,
                SnapshotsClientRequest = (ulong)m_hostSnapshotsClientRequest,
                SnapshotsNeedsFullRecovery = (ulong)m_hostSnapshotsNeedsFullRecovery,
                SnapshotsPauseBarrier = (ulong)m_hostSnapshotsPauseBarrier,
                SnapshotsTopologyChange = (ulong)m_hostSnapshotsTopologyChange,
                SnapshotsSendFailureRecovery = (ulong)m_hostSnapshotsSendFailureRecovery,
                SnapshotSendFailures = (ulong)m_hostSnapshotSendFailures,
                HostBatchCaptureFailures = (ulong)m_hostBatchCaptureFailures,
                HostBatchCaptureChunksFailed = (ulong)m_hostBatchCaptureChunksFailed,
                RequestedSnapshotCaptureFailures = (ulong)m_hostRequestedSnapshotCaptureFailures
            };
            m_hostProbe.AppendWindowAndReset(sample.Stages);
            return sample;
        }

        public ClientCircuitSample SnapshotClientTelemetry()
        {
            var sample = new ClientCircuitSample
            {
                HashCaptureFailures = 0,
                HashCaptureChunksSkipped = 0,
                HashReportsSent = 0,
                HashChunksSent = 0,
                ScopeRequestsSent = (ulong)m_clientScopeRequests,
                ScopeRequestChunksSent = (ulong)m_clientScopeChunks,
                SnapshotPartsReceived = (ulong)m_clientSnapshotPartsReceived,
                SnapshotsApplied = (ulong)m_clientSnapshotsApplied,
                StatesApplied = (ulong)m_clientStatesApplied,
                ApplyFailures = (ulong)m_clientApplyFailures,
                WatermarkExpired = (ulong)m_clientWatermarkExpired,
                SnapshotTimeouts = (ulong)m_clientSnapshotTimeouts
            };
            m_probe.AppendWindowAndReset(sample.Stages);
            return sample;
        }

        public bool DebugPauseRequested => m_debugPauseRequested;

        public bool IsHosting => m_transport?.IsHosting == true;

        public bool TrySetDebugPause(bool paused, out string error)
        {
            error = null;
            if (m_transport == null || !m_transport.IsHosting)
            {
                error = "Circuit debug pause is only available on an active host.";
                return false;
            }
            if (m_debugPauseRequested == paused) return true;
            m_debugPauseRequested = paused;
            m_debugPauseStep = int.MinValue;
            m_debugPauseStates.Clear();
            CircuitSimulationPauseSignal.Set(paused);
            return true;
        }

        public CircuitSyncModule(ICircuitWorldPort world, ICircuitPresentationPort presentation,
            Func<double> clock = null,
            Func<ChunkKey, IList<CircuitElementState>, int, string> reconcileSnapshot = null,
            Func<uint> randomSeed = null)
        {
            m_world = world ?? throw new ArgumentNullException(nameof(world));
            m_presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
            m_clock = clock ?? (() => Time.RealTime);
            m_reconcileSnapshot = reconcileSnapshot;
            m_randomSeedProvider = randomSeed;
            if (world is CircuitWorldEngine circuitWorld)
                circuitWorld.SetCircuitProbe(m_probe);
            m_descriptor = new ModuleDescriptor("circuit-sync", "Circuit synchronization")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "circuit.sync" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public void SetCircuitProbe(CircuitProbeRecorder probe)
        {
            m_probe = probe ?? throw new ArgumentNullException(nameof(probe));
            m_activeProbe = m_probe;
            if (m_world is CircuitWorldEngine circuitWorld)
                circuitWorld.SetCircuitProbe(m_probe);
        }

        public void SetHostCircuitProbe(CircuitProbeRecorder probe) =>
            m_hostProbe = probe ?? throw new ArgumentNullException(nameof(probe));

        public void Attach(IModuleContext context)
        {
            m_context = context ?? throw new ArgumentNullException(nameof(context));
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Warn("Circuit transport port is unavailable.");
                return;
            }
            context.TryGetPort(out m_useAuthority);
            context.TryGetPort(out m_interactionHost);
            m_hostTopologyBumps = 0;
            m_hostUnknownTopologyBumps = 0;
            m_clientTopologyBumps = 0;
            m_clientUnknownTopologyBumps = 0;
            m_lastHostTopologyBump = "none";
            m_lastClientTopologyBump = "none";
            m_transport.FrameArrived += HandleFrame;
            m_transport.PeerAttached += HandlePeerAttached;
            m_transport.PeerDetached += HandlePeerDetached;
            m_world.HostCellPublished += HandleHostCellChanged;
            m_world.ReplicaElectricCellChanged += HandleReplicaElectricCellChanged;
            m_world.ReplicaElectricTopologyChanged += HandleReplicaElectricTopologyChanged;
            m_presentation.SetCircuitRequestSink(SubmitOperation);
            CircuitSimulationPauseSignal.SetClientStepGate(BeforeClientCircuitStep);
            CircuitSimulationPauseSignal.SetClientStepCompleted(
                ProcessReadyClientCircuitBoundary);
            CircuitSimulationPauseSignal.SetHostStepBoundary(ProcessHostOperationsBeforeStep);
            m_sessionId = m_transport.SessionId;
            m_nextHostHashMs = 0;
            m_clientClockInitialized = false;
            m_clientClockPaused = false;
            m_clientClockAccelerated = false;
            m_clientResumePending = false;
            m_clientScopeAcknowledged = false;
            m_clientScopeResponseReceived = false;
            m_clientPendingScopeRequestId = 0;
            m_clientScopeRequestSentMs = 0;
            m_clientCircuitScope.Clear();
            m_clientHashCheckpoints.Clear();
            m_clientChunkCommittedEventSequences.Clear();
            m_hostChunkCommittedEventSequences.Clear();
            m_clientRepairRequests.Clear();
            m_clientEventTopologyResyncRequests.Clear();
            m_hostWasAccelerated = false;
            m_nextHostCaptureWarningMs = 0;
            m_clientInitializedScope.Clear();
            CircuitSimulationPauseSignal.ClearClientClock();
            // The host owns the circuit epoch. A client must wait for the first
            // CircuitClock instead of inventing a local epoch, including after a
            // reconnect where the previous host session may have been discarded.
            if (m_transport.IsHosting && m_epoch == 0)
                m_epoch = 1;
            else if (!m_transport.IsHosting)
                m_epoch = 0;
            m_attached = true;
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached || m_transport == null) return;
            m_activeProbe = m_transport.IsHosting ? m_hostProbe : m_probe;
            m_probeTrigger = m_world is CircuitWorldEngine circuitWorld
                ? circuitWorld.LastPumpTrigger : "none";
            if (m_world is CircuitWorldEngine worldEngine)
                worldEngine.SetCircuitProbe(m_activeProbe);
            BeginProbeFrame();
            long probeStarted = m_probe.Start();
            m_world.Pump();
            RecordProbe("pump", probeStarted);
            probeStarted = m_probe.Start();
            ResetForSessionChange();
            if (m_pendingClock != null && m_world.IsReady)
            {
                CircuitClock pending = m_pendingClock;
                m_pendingClock = null;
                ApplyClock(pending);
            }
            RecordProbe("session-clock", probeStarted);
            bool circuitAttached = m_transport.IsHosting
                ? true
                : m_transport.IsClientCircuitAttached;
            if (m_wasCircuitAttached && !circuitAttached)
            {
                m_clientScopeRequested = false;
                m_clientScopeAcknowledged = false;
                m_clientScopeResponseReceived = false;
                m_clientPendingScopeRequestId = 0;
                m_clientScopeRequestSentMs = 0;
                m_pendingOperations.Clear();
                m_deferredOperationResults.Clear();
                m_deferredEvents.Clear();
                m_snapshotAssemblies.Clear();
                m_clientChunkBoundaries.Clear();
                m_clientRequestedScope.Clear();
                m_clientInitializedScope.Clear();
                m_clientCircuitScope.Clear();
                m_clientHashCheckpoints.Clear();
                m_clientChunkCommittedEventSequences.Clear();
                m_clientRepairRequests.Clear();
                m_clientEventTopologyResyncRequests.Clear();
                ResetClientSyncMetadata();
                m_pendingClock = null;
                m_clientClockInitialized = false;
                m_clientClockPaused = false;
                m_clientClockAccelerated = false;
                m_clientResumePending = false;
                CircuitSimulationPauseSignal.ClearClientClock();
                m_presentation.DetachPresentation();
            }
            if (!m_wasCircuitAttached && circuitAttached)
            {
                m_world.ResetClientCircuitState();
                m_clientScopeRequested = false;
            }
            m_wasCircuitAttached = circuitAttached;
            probeStarted = m_probe.Start();
            ReconcileClientTopologyChanges();
            RecordProbe("topology-reconcile", probeStarted);
            probeStarted = m_probe.Start();
            ProcessPendingScopeSnapshot();
            RecordProbe("scope-snapshot", probeStarted);
            probeStarted = m_probe.Start();
            ProcessReadySnapshots();
            RecordProbe("ready-snapshots", probeStarted);
            if (m_transport.IsClient && m_world.TryGetClock(out int boundaryStep,
                    out _, out _, out _, out _))
                ProcessReadyClientCircuitBoundary(boundaryStep);
            probeStarted = m_probe.Start();
            ProcessDeferredAuthoritativeMessages();
            RecordProbe("deferred-authority", probeStarted);
            long nowMs = (long)(m_clock() * 1000.0);
            if (m_transport.IsHosting && m_world.IsReady && nowMs >= m_nextSyncMs)
            {
                HostSync(nowMs);
                m_nextSyncMs = nowMs + SyncIntervalMs;
            }
            if (m_transport.IsClient && m_transport.IsClientCircuitAttached &&
                m_world.IsReady && m_epoch != 0)
            {
                if (nowMs >= m_nextClientScopeRefreshMs)
                {
                    m_nextClientScopeRefreshMs = nowMs + ClientScopeRefreshIntervalMs;
                    probeStarted = m_probe.Start();
                    bool requestTimedOut = m_clientScopeRequested &&
                        !m_clientScopeResponseReceived &&
                        nowMs - m_clientScopeRequestSentMs >= ClientScopeRequestTimeoutMs;
                    // A scope response can be waiting for terrain catch-up. Do not replace
                    // that response with another full-map request every refresh interval;
                    // the pending response is the authoritative scope for this request id.
                    if (m_pendingScopeSnapshot == null &&
                        (!m_clientScopeRequested || ClientScopeChanged() || requestTimedOut))
                        RequestClientScope(nowMs);
                    RecordProbe("scope-check", probeStarted);
                }
            }
            probeStarted = m_probe.Start();
            UpdateClientSimulationClock();
            RecordProbe("simulation-clock", probeStarted);
        }

        private void RecordProbe(string id, long started)
        {
            (m_activeProbe ?? m_probe).Record(id, started, m_probeTrigger,
                m_probeScopeChunks,
                m_probeDirtyChunks, m_probePendingSnapshots,
                m_probePendingStates, m_probeHashChunks);
        }

        private void BeginProbeFrame()
        {
            m_probeScopeChunks = m_clientCircuitScope.Count;
            m_probeDirtyChunks = m_clientTopologyDirtyChunks.Count +
                m_clientScopeMembershipDirtyChunks.Count;
            m_probePendingSnapshots = m_snapshotAssemblies.Count;
            m_probePendingStates = 0;
            m_probeHashChunks = 0;
        }

        public void Detach(ModuleDetachReason reason)
        {
            if (m_transport != null)
            {
                m_transport.FrameArrived -= HandleFrame;
                m_transport.PeerAttached -= HandlePeerAttached;
                m_transport.PeerDetached -= HandlePeerDetached;
            }
            m_world.HostCellPublished -= HandleHostCellChanged;
            m_world.ReplicaElectricCellChanged -= HandleReplicaElectricCellChanged;
            m_world.ReplicaElectricTopologyChanged -= HandleReplicaElectricTopologyChanged;
            m_presentation.SetCircuitRequestSink(null);
            m_presentation.DetachPresentation();
            m_world.SetDeterministicRandomSeed(0, hostAuthority: false);
            m_hasConfiguredRandomSeed = false;
            m_randomSeedAuthority = false;
            m_peerStates.Clear();
            m_pendingOperations.Clear();
            m_snapshotAssemblies.Clear();
            m_clientChunkBoundaries.Clear();
            m_clientRequestedScope.Clear();
            m_clientInitializedScope.Clear();
            m_clientCircuitScope.Clear();
            ResetClientSyncMetadata();
            m_deferredOperationResults.Clear();
            m_deferredEvents.Clear();
            m_peers.Clear();
            m_chunks.Clear();
            m_peerSyncScopes.Clear();
            m_activePeerSyncScopeCount = 0;
            m_captureChunkUnion.Clear();
            m_captureChunkSet.Clear();
            m_states.Clear();
            m_transport = null;
            m_context = null;
            m_attached = false;
            m_clientScopeRequested = false;
            m_clientScopeResponseReceived = false;
            m_clientScopeAcknowledged = false;
            m_clientPendingScopeRequestId = 0;
            m_clientScopeRequestSentMs = 0;
            m_wasCircuitAttached = false;
            m_debugPauseRequested = false;
            m_debugPauseStep = int.MinValue;
            m_debugPauseStates.Clear();
            m_captureStatesByChunk.Clear();
            m_captureErrorsByChunk.Clear();
            m_nextHostCaptureWarningMs = 0;
            m_pendingClock = null;
            m_pendingScopeSnapshot = null;
            m_clientClockInitialized = false;
            CircuitSimulationPauseSignal.SetClientStepGate(null);
            CircuitSimulationPauseSignal.SetClientStepCompleted(null);
            CircuitSimulationPauseSignal.SetHostStepBoundary(null);
            m_hostOperationsByStep.Clear();
            m_clientHashCheckpoints.Clear();
            m_clientChunkCommittedEventSequences.Clear();
            m_hostChunkCommittedEventSequences.Clear();
            m_clientRepairRequests.Clear();
            m_clientEventTopologyResyncRequests.Clear();
            m_clientClockPaused = false;
            m_clientClockAccelerated = false;
            m_clientResumePending = false;
            m_hostWasAccelerated = false;
            CircuitSimulationPauseSignal.Set(false);
            CircuitSimulationPauseSignal.ClearClientClock();
        }

        private void HostSync(long nowMs)
        {
            long started = Stopwatch.GetTimestamp();
            m_probeTrigger = "host-sync";
            long probeStarted = m_hostProbe.Start();
            CircuitProbeRecorder previousProbe = m_activeProbe;
            CircuitProbeRecorder previousWorldProbe = null;
            m_activeProbe = m_hostProbe;
            if (m_world is CircuitWorldEngine hostWorld)
            {
                previousWorldProbe = hostWorld.CurrentCircuitProbe;
                hostWorld.SetCircuitProbe(m_hostProbe);
            }
            try
            {
                HostSyncCore(nowMs);
            }
            finally
            {
                RecordProbe("host-sync", probeStarted);
                m_activeProbe = previousProbe;
                if (m_world is CircuitWorldEngine restoreWorld)
                    restoreWorld.SetCircuitProbe(previousWorldProbe);
                long elapsed = Stopwatch.GetTimestamp() - started;
                m_hostSyncWindowCalls++;
                m_hostSyncWindowTicks += elapsed;
                if (elapsed > m_hostSyncWindowMaxTicks) m_hostSyncWindowMaxTicks = elapsed;
            }
        }

        private void HostSyncCore(long nowMs)
        {
            m_hostSyncRounds++;
            m_probeScopeChunks = 0;
            m_probeDirtyChunks = m_hostTopologyChangedChunks.Count;
            m_probePendingStates = 0;
            m_probePendingSnapshots = 0;
            if (!m_world.TryGetClock(out int step, out double elapsed, out double dayOffset,
                    out bool paused, out string clockError))
            {
                Warn("Host clock capture failed: " + clockError);
                return;
            }
            if (m_debugPauseRequested)
            {
                if (m_debugPauseStep == int.MinValue)
                    m_debugPauseStep = step;
                step = m_debugPauseStep;
                paused = true;
            }
            if (m_randomSeedProvider == null)
            {
                Warn("Host circuit random seed provider is missing.");
                return;
            }
            if (step < m_lastCircuitStep)
            {
                m_timelineGeneration = m_timelineGeneration == int.MaxValue
                    ? 1 : m_timelineGeneration + 1;
                m_hostOperationsByStep.Clear();
                m_committedOperationsWaitingTerrain.Clear();
                m_nextCommittedEventSequence = 0;
                m_hostChunkCommittedEventSequences.Clear();
                m_lastScheduledOperationStep = step;
                m_lateCommittedOperations.Clear();
                foreach (PeerState existingPeer in m_peerStates.Values)
                {
                    existingPeer.LastPaused = false;
                    ClearPauseBarrier(existingPeer);
                    existingPeer.LastStates.Clear();
                    existingPeer.LastElementTypes.Clear();
                    existingPeer.PendingOperationIds.Clear();
                    existingPeer.OperationResults.Clear();
                    existingPeer.OperationResultOrder.Clear();
                    existingPeer.SnapshotBoundaries.Clear();
                    existingPeer.NeedsFullSnapshot.Clear();
                    existingPeer.NeedsFullSnapshotReasons.Clear();
                    existingPeer.RepairRequests.Clear();
                    existingPeer.RepairRequestReasons.Clear();
                }
            }
            uint randomSeed = m_randomSeedProvider();
            if (!m_hasConfiguredRandomSeed || m_configuredRandomSeed != randomSeed ||
                !m_randomSeedAuthority)
            {
                m_world.SetDeterministicRandomSeed(randomSeed, hostAuthority: true);
                m_configuredRandomSeed = randomSeed;
                m_hasConfiguredRandomSeed = true;
                m_randomSeedAuthority = true;
            }
            m_lastCircuitStep = step;
            bool accelerated = m_world.IsWorldAccelerated;
            bool accelerationEnded = m_hostWasAccelerated && !accelerated;
            m_hostWasAccelerated = accelerated;
            m_transport.CopyAttachedPeerIds(m_peers);
            if (m_peers.Count == 0)
            {
                m_activePeerSyncScopeCount = 0;
                ClearInactivePeerSyncScopes();
                return;
            }

            if (!m_hostCircuitIndexInitialized)
            {
                m_chunks.Clear();
                m_world.CopyLoadedChunks(m_chunks);
                if (m_chunks.Count > 0 && !m_world.TryCaptureChunksBestEffort(m_chunks,
                        m_captureStatesByChunk, m_captureErrorsByChunk,
                        out string indexCaptureError))
                {
                    Warn("Initial loaded circuit chunk index capture failed: " + indexCaptureError);
                    return;
                }
                if (m_captureErrorsByChunk.Count > 0)
                {
                    KeyValuePair<ChunkKey, string> firstFailure = m_captureErrorsByChunk.First();
                    Warn("Initial loaded circuit chunk index capture had " +
                        m_captureErrorsByChunk.Count + " chunk failure(s); first failure at " +
                        firstFailure.Key + ": " + firstFailure.Value);
                    return;
                }
                m_hostCircuitChunks.Clear();
                foreach (KeyValuePair<ChunkKey, List<CircuitElementState>> pair in m_captureStatesByChunk)
                {
                    if (pair.Value.Count == 0) continue;
                    m_hostCircuitChunks.Add(pair.Key);
                    CaptureCircuitCellValues(pair.Key, pair.Value, m_hostCircuitCellValues);
                    m_hostTopologyGenerations[pair.Key] = 1L;
                    m_hostTopologyTerrainSequences[pair.Key] =
                        m_transport.TerrainSequenceForChunk(pair.Key);
                }
                m_hostCircuitIndexInitialized = true;
            }
            ReconcileHostTopologyChanges();

            m_activePeerSyncScopeCount = 0;
            m_captureChunkUnion.Clear();
            m_captureChunkSet.Clear();
            foreach (int clientId in m_peers)
            {
                if (!m_transport.IsHostCircuitAttached(clientId)) continue;
                m_hostAttachedPeers++;
                PeerState peer = GetPeerState(clientId);
                if (accelerationEnded)
                    peer.AccelerationResumePending = true;
                m_chunks.Clear();
                m_transport.CopyClientChunkScope(clientId, m_chunks);
                if (m_chunks.Count > MaximumInterestChunks)
                {
                    Warn("Client " + clientId + " circuit interest exceeds " +
                        MaximumInterestChunks + " chunks; synchronization is rejected.");
                    continue;
                }
                peer.PendingScopeTerrainChunks.IntersectWith(m_chunks);
                var circuitChunks = new List<ChunkKey>();
                foreach (ChunkKey chunk in m_chunks)
                    if (peer.CircuitScope.Contains(chunk) && m_hostCircuitChunks.Contains(chunk))
                        circuitChunks.Add(chunk);
                m_hostScopeChunks += circuitChunks.Count;
                if (m_activePeerSyncScopeCount == m_peerSyncScopes.Count)
                    m_peerSyncScopes.Add(new PeerSyncScope());
                PeerSyncScope peerScope = m_peerSyncScopes[m_activePeerSyncScopeCount++];
                peerScope.ClientId = clientId;
                peerScope.Peer = peer;
                peerScope.Chunks.Clear();
                peerScope.Chunks.AddRange(circuitChunks);
                m_probeScopeChunks += circuitChunks.Count;
                m_probePendingSnapshots += peer.NeedsFullSnapshot.Count + peer.RepairRequests.Count;
                foreach (ChunkKey chunk in peerScope.Chunks)
                    if (m_captureChunkSet.Add(chunk))
                        m_captureChunkUnion.Add(chunk);
            }
            ClearInactivePeerSyncScopes();

            m_captureStatesByChunk.Clear();
            m_captureErrorsByChunk.Clear();
            if (m_captureChunkUnion.Count > 0 &&
                !m_world.TryCaptureChunksBestEffort(m_captureChunkUnion,
                    m_captureStatesByChunk, m_captureErrorsByChunk,
                    out string batchCaptureError))
            {
                m_hostBatchCaptureFailures++;
                Warn("Host circuit batch capture failed: " + batchCaptureError);
                return;
            }
            if (m_captureErrorsByChunk.Count > 0)
            {
                m_hostBatchCaptureFailures++;
                m_hostBatchCaptureChunksFailed += m_captureErrorsByChunk.Count;
                if (nowMs >= m_nextHostCaptureWarningMs)
                {
                    KeyValuePair<ChunkKey, string> firstFailure = m_captureErrorsByChunk.First();
                    Warn("Host circuit batch capture skipped " + m_captureErrorsByChunk.Count +
                        " chunk(s); first failure at " + firstFailure.Key + ": " +
                        firstFailure.Value);
                    m_nextHostCaptureWarningMs = nowMs + 5000;
                }
            }
            foreach (List<CircuitElementState> captured in m_captureStatesByChunk.Values)
                m_probePendingStates += captured.Count;
            if (m_debugPauseRequested)
            {
                foreach (KeyValuePair<ChunkKey, List<CircuitElementState>> pair in m_captureStatesByChunk)
                    if (!m_debugPauseStates.ContainsKey(pair.Key))
                        m_debugPauseStates[pair.Key] = pair.Value.Select(state => state.Clone()).ToList();
                foreach (ChunkKey chunk in m_captureChunkUnion)
                    if (m_debugPauseStates.TryGetValue(chunk, out List<CircuitElementState> frozen))
                        m_captureStatesByChunk[chunk] = frozen.Select(state => state.Clone()).ToList();
            }

            bool checkpointDue = nowMs >= m_nextHostHashMs;
            if (checkpointDue)
            {
                m_nextHostHashMs = nowMs + HashIntervalMs;
                m_hostCheckpointHashes.Clear();
                foreach (KeyValuePair<ChunkKey, List<CircuitElementState>> pair in
                             m_captureStatesByChunk)
                {
                    if (m_captureErrorsByChunk.ContainsKey(pair.Key)) continue;
                    m_hostCheckpointHashes[pair.Key] = ComputeCircuitSyncHash(pair.Value);
                }
            }

            for (int scopeIndex = 0; scopeIndex < m_activePeerSyncScopeCount; scopeIndex++)
            {
                PeerSyncScope peerScope = m_peerSyncScopes[scopeIndex];
                int clientId = peerScope.ClientId;
                PeerState peer = peerScope.Peer;
                bool resumeSyncComplete = true;
                int checkpointChunksToCount = 0;
                m_chunks.Clear();
                m_chunks.AddRange(peerScope.Chunks);
                PrunePeerScope(peer, m_chunks);
                if (paused)
                    UpdatePauseBarrier(peer, m_chunks, step, m_timelineGeneration, nowMs);
                else if (peer.PauseBarrierActive)
                    ClearPauseBarrier(peer);

                var batch = new CircuitSyncBatch { Epoch = m_epoch, HostCircuitStep = step };
                var clockMessage = new CircuitClock
                {
                    Epoch = m_epoch,
                    HostCircuitStep = step,
                    LastCommittedEventSequence = unchecked((int)m_nextCommittedEventSequence),
                    TerrainSequence = m_transport.TerrainSequence,
                    TotalElapsedGameTime = elapsed,
                    TimeOfDayOffset = dayOffset,
                    TimelineGeneration = m_timelineGeneration,
                    RandomSeed = randomSeed,
                    Paused = paused,
                    Accelerated = accelerated || peer.AccelerationResumePending
                };
                AddPayload(batch, MessageType.CircuitClock, clockMessage);

                var changedStates = new Dictionary<ChunkKey, List<CircuitElementState>>();
                var pendingSnapshotIds = new Dictionary<ChunkKey, ulong>();
                foreach (ChunkKey chunk in m_chunks)
                {
                    if (m_captureErrorsByChunk.ContainsKey(chunk))
                    {
                        resumeSyncComplete = false;
                        continue;
                    }
                    if (!m_captureStatesByChunk.TryGetValue(chunk, out List<CircuitElementState> captured))
                    {
                        Warn("Host circuit batch capture omitted " + chunk + ".");
                        resumeSyncComplete = false;
                        continue;
                    }
                    if (paused && peer.PauseBarrierActive &&
                        peer.PauseBarrierChunks.Contains(chunk) &&
                        !peer.PauseBarrierApplied.Contains(chunk))
                    {
                        if (HasPauseBarrierSnapshotForChunk(peer, chunk))
                            continue;
                        MarkNeedsFullSnapshot(peer, chunk, SnapshotReason.PauseBarrier);
                    }
                    long terrainBoundary = m_transport.TerrainSequenceForChunk(chunk);
                    SnapshotReason snapshotReason;
                    bool needsSnapshot = TryTakeFullSnapshotReason(peer, chunk,
                        out snapshotReason);
                    if (!needsSnapshot)
                        needsSnapshot = TryTakeRepairReason(peer, chunk, out snapshotReason);
                    if (!needsSnapshot && !peer.LastStates.ContainsKey(chunk))
                    {
                        needsSnapshot = true;
                        snapshotReason = SnapshotReason.InitialScope;
                    }
                    if (needsSnapshot)
                    {
                        if (SendSnapshot(clientId, peer, chunk, step,
                                terrainBoundary,
                                CurrentHostTopologyGeneration(chunk), captured, snapshotReason,
                                out ulong snapshotId))
                        {
                            RememberStates(peer, chunk, captured);
                            peer.RepairRequests.Remove(chunk);
                            peer.RepairRequestReasons.Remove(chunk);
                            TrackPauseBarrierSnapshot(peer, chunk, step, snapshotId, nowMs);
                        }
                        else
                        {
                            MarkNeedsFullSnapshot(peer, chunk,
                                SnapshotReason.SendFailureRecovery);
                            resumeSyncComplete = false;
                        }
                        continue;
                    }

                    List<CircuitElementState> changed = FindChangedStates(peer, chunk, captured);
                    if (TryTakeFullSnapshotReason(peer, chunk,
                            out SnapshotReason recoveryReason))
                    {
                        if (SendSnapshot(clientId, peer, chunk, step,
                                terrainBoundary,
                                CurrentHostTopologyGeneration(chunk), captured, recoveryReason,
                                out ulong snapshotId))
                        {
                            RememberStates(peer, chunk, captured);
                            peer.RepairRequests.Remove(chunk);
                            peer.RepairRequestReasons.Remove(chunk);
                            TrackPauseBarrierSnapshot(peer, chunk, step, snapshotId, nowMs);
                        }
                        else
                        {
                            MarkNeedsFullSnapshot(peer, chunk,
                                SnapshotReason.SendFailureRecovery);
                            resumeSyncComplete = false;
                        }
                    }
                    else
                    {
                        if (changed.Count > 0)
                        {
                            m_hostDeltaStatesSent += changed.Count;
                            ulong snapshotId = NextSnapshotId();
                            changedStates[chunk] = changed;
                            uint deltaHash = ComputeCircuitSyncHash(changed);
                            RegisterSnapshotBoundary(peer, snapshotId, chunk, step,
                                terrainBoundary,
                                CurrentHostTopologyGeneration(chunk), deltaHash, full: false,
                                unchecked(m_nextCommittedEventSequence),
                                GetHostChunkCommittedEventSequence(chunk),
                                reason: null, stateCount: changed.Count);
                            pendingSnapshotIds[chunk] = snapshotId;
                        }
                        RememberStates(peer, chunk, captured);
                    }
                }

                foreach (KeyValuePair<ChunkKey, List<CircuitElementState>> pair in changedStates)
                {
                    List<CircuitPayload> payloads;
                    try
                    {
                        payloads = BuildStatePayloads(pair.Key, step,
                            m_transport.TerrainSequenceForChunk(pair.Key),
                            CurrentHostTopologyGeneration(pair.Key), pair.Value, full: false,
                            pendingSnapshotIds[pair.Key],
                            unchecked(m_nextCommittedEventSequence),
                            GetHostChunkCommittedEventSequence(pair.Key));
                    }
                    catch (Exception e)
                    {
                        Warn("Circuit delta framing failed for " + pair.Key + ": " +
                            e.GetType().Name + ": " + e.Message);
                        MarkNeedsFullSnapshot(peer, pair.Key,
                            SnapshotReason.SendFailureRecovery);
                        peer.SnapshotBoundaries.Remove(pendingSnapshotIds[pair.Key]);
                        RemovePauseBarrierSnapshot(peer, pendingSnapshotIds[pair.Key], pair.Key);
                        resumeSyncComplete = false;
                        continue;
                    }
                    bool appended = true;
                    foreach (CircuitPayload payload in payloads)
                    {
                        if (!AppendPayload(clientId, ref batch, clockMessage, payload))
                        {
                            MarkNeedsFullSnapshot(peer, pair.Key,
                                SnapshotReason.SendFailureRecovery);
                            peer.SnapshotBoundaries.Remove(pendingSnapshotIds[pair.Key]);
                            RemovePauseBarrierSnapshot(peer, pendingSnapshotIds[pair.Key], pair.Key);
                            appended = false;
                            resumeSyncComplete = false;
                            break;
                        }
                    }
                    if (appended)
                        TrackPauseBarrierSnapshot(peer, pair.Key, step,
                            pendingSnapshotIds[pair.Key], nowMs);
                }
                if (checkpointDue)
                {
                    var checkpoint = new CircuitHashCheckpoint
                    {
                        Epoch = m_epoch,
                        HostCircuitStep = step,
                        TimelineGeneration = m_timelineGeneration
                    };
                    foreach (ChunkKey chunk in m_chunks)
                    {
                        if (!m_hostCheckpointHashes.TryGetValue(chunk, out uint hash)) continue;
                        long terrainSequence = m_transport.TerrainSequenceForChunk(chunk);
                        long topologyGeneration = CurrentHostTopologyGeneration(chunk);
                        uint committedEventSequence = GetHostChunkCommittedEventSequence(chunk);
                        checkpoint.Chunks.Add(new CircuitChunkHash
                        {
                            ChunkX = chunk.X,
                            ChunkZ = chunk.Z,
                            Hash = hash,
                            TopologyGeneration = topologyGeneration,
                            TerrainSequence = terrainSequence,
                            ChunkCommittedEventSequence = committedEventSequence
                        });
                    }
                    if (checkpoint.Chunks.Count > 0 &&
                        !AppendPayload(clientId, ref batch, clockMessage,
                            CreatePayload(MessageType.CircuitHashCheckpoint, checkpoint)))
                    {
                        resumeSyncComplete = false;
                        Warn("Circuit hash checkpoint could not be appended for client " +
                            clientId + ".");
                    }
                else if (checkpoint.Chunks.Count > 0)
                        checkpointChunksToCount = checkpoint.Chunks.Count;
                }
                bool releaseAcceleration = peer.AccelerationResumePending &&
                    resumeSyncComplete && peer.NeedsFullSnapshot.Count == 0;
                if (releaseAcceleration)
                {
                    for (int i = batch.Payloads.Count - 1; i >= 0; i--)
                        if ((MessageType)batch.Payloads[i].MessageType == MessageType.CircuitClock)
                            batch.Payloads.RemoveAt(i);
                    CircuitClock resumedClock = clockMessage.Clone();
                    resumedClock.Accelerated = false;
                    AddPayload(batch, MessageType.CircuitClock, resumedClock);
                }
                bool batchSent = batch.Payloads.Count == 0 || SendBatch(clientId, batch);
                if (!batchSent)
                {
                    m_hostSendFailures++;
                    Warn("Circuit sync batch send failed for client " + clientId + ".");
                    resumeSyncComplete = false;
                    foreach (ChunkKey chunk in changedStates.Keys)
                    {
                        MarkNeedsFullSnapshot(peer, chunk,
                            SnapshotReason.SendFailureRecovery);
                        if (pendingSnapshotIds.TryGetValue(chunk, out ulong id))
                        {
                            peer.SnapshotBoundaries.Remove(id);
                            RemovePauseBarrierSnapshot(peer, id, chunk);
                        }
                    }
                }
                else if (releaseAcceleration)
                    peer.AccelerationResumePending = false;
                if (batchSent && checkpointChunksToCount > 0)
                {
                    m_hostHashCheckpointsSent++;
                    m_hostHashChunksSent += checkpointChunksToCount;
                }
                if (paused)
                {
                    ExpirePauseBarrierSnapshots(peer, nowMs);
                    peer.PauseBarrierComplete = IsPauseBarrierComplete(peer, m_chunks);
                    peer.LastPaused = peer.PauseBarrierComplete;
                }
                else
                    peer.LastPaused = false;
            }
        }

        private void HandleHostCellChanged(ChunkKey key, CellEdit edit)
        {
            if (m_transport?.IsHosting != true) return;
            ProcessHostTopologyCellChange(key, edit, m_transport.TerrainSequenceForChunk(key));
        }

        private void ProcessHostTopologyCellChange(ChunkKey key, CellEdit edit,
            long terrainSequence)
        {
            int x = key.X * 16 + edit.X;
            int z = key.Z * 16 + edit.Z;
            int newValue = m_world.GetCellValue(x, edit.Y, z);
            string cellKey = CircuitCellKey(x, edit.Y, z);
            if (!m_hostCircuitCellValues.TryGetValue(key,
                    out Dictionary<string, int> cellValues))
            {
                cellValues = new Dictionary<string, int>(StringComparer.Ordinal);
                m_hostCircuitCellValues.Add(key, cellValues);
            }
            bool wasKnown = cellValues.TryGetValue(cellKey, out int oldValue);
            bool newIsElectric = m_world.IsElectricTopologyElementAt(x, edit.Y, z);
            bool changed = wasKnown
                ? m_world.IsElectricTopologyChange(oldValue, newValue, x, edit.Y, z)
                : newIsElectric;
            if (newIsElectric) cellValues[cellKey] = newValue;
            else cellValues.Remove(cellKey);
            if (!changed) return;
            foreach (ChunkKey affected in CollectCircuitNeighborhood(key, edit))
            {
                long previousGeneration = CurrentGeneration(m_hostTopologyGenerations, affected);
                long generation = BumpTopologyGeneration(m_hostTopologyGenerations, affected);
                m_hostTopologyBumps++;
                if (!wasKnown) m_hostUnknownTopologyBumps++;
                m_lastHostTopologyBump = DescribeTopologyBump(affected, x, edit.Y, z,
                    oldValue, newValue, wasKnown, terrainSequence, previousGeneration,
                    generation);
                m_hostTopologyTerrainSequences[affected] = terrainSequence;
                m_hostTopologyChangedChunks.Add(affected);
            }
        }

        private void HandleReplicaElectricTopologyChanged(ChunkKey key, CellEdit edit)
        {
            if (m_transport?.IsClient != true) return;
            int x = key.X * 16 + edit.X;
            int z = key.Z * 16 + edit.Z;
            int value = m_world.GetCellValue(x, edit.Y, z);
            string cellKey = CircuitCellKey(x, edit.Y, z);
            if (!m_clientCircuitCellValues.TryGetValue(key,
                    out Dictionary<string, int> cellValues))
            {
                cellValues = new Dictionary<string, int>(StringComparer.Ordinal);
                m_clientCircuitCellValues.Add(key, cellValues);
            }
            bool wasKnown = cellValues.TryGetValue(cellKey, out int oldValue);
            bool isElectric = m_world.IsElectricTopologyElementAt(x, edit.Y, z);
            bool changed = wasKnown
                ? m_world.IsElectricTopologyChange(oldValue, value, x, edit.Y, z)
                : isElectric;
            if (isElectric) cellValues[cellKey] = value;
            else cellValues.Remove(cellKey);
            foreach (ChunkKey affected in CollectCircuitNeighborhood(key, edit))
            {
                if (changed)
                {
                    long terrainSequence = m_transport.TerrainSequenceForChunk(affected);
                    long previousGeneration = CurrentGeneration(m_clientTopologyGenerations, affected);
                    long generation = BumpTopologyGeneration(m_clientTopologyGenerations, affected);
                    m_clientTopologyBumps++;
                    if (!wasKnown) m_clientUnknownTopologyBumps++;
                    m_lastClientTopologyBump = DescribeTopologyBump(affected, x, edit.Y, z,
                        oldValue, value, wasKnown, terrainSequence, previousGeneration,
                        generation);
                    m_clientTopologyTerrainSequences[affected] = terrainSequence;
                    m_clientTopologyDirtyChunks.Add(affected);
                    m_clientTopologyAwaitingSnapshot.Add(affected);
                }
                m_clientChunkBoundaries.Remove(affected);
            }
            if (changed) m_clientScopeRequested = false;
        }

        private void HandleReplicaElectricCellChanged(ChunkKey key, CellEdit edit)
        {
            if (m_transport?.IsClient != true) return;
            int x = key.X * 16 + edit.X;
            int z = key.Z * 16 + edit.Z;
            int value = m_world.GetCellValue(x, edit.Y, z);
            string identity = CircuitCellKey(x, edit.Y, z);
            if (!m_clientCircuitCellValues.TryGetValue(key,
                    out Dictionary<string, int> cellValues))
            {
                cellValues = new Dictionary<string, int>(StringComparer.Ordinal);
                m_clientCircuitCellValues.Add(key, cellValues);
            }
            if (m_world.IsElectricTopologyElementAt(x, edit.Y, z))
                cellValues[identity] = value;
            else
                cellValues.Remove(identity);
            foreach (ChunkKey affected in CollectCircuitNeighborhood(key, edit))
                m_clientChunkBoundaries.Remove(affected);
        }

        private void ReconcileClientTopologyChunks()
        {
            if (m_clientTopologyDirtyChunks.Count == 0) return;
            m_probeTrigger = "terrain-topology-change";
            m_chunks.Clear();
            m_chunks.AddRange(m_clientTopologyDirtyChunks);
            m_probeScopeChunks = m_chunks.Count;
            m_probePendingStates = 0;
            long probeStarted = m_probe.Start();
            if (!m_world.TryCaptureChunksBestEffort(m_chunks, m_captureStatesByChunk,
                    m_captureErrorsByChunk, out string error))
            {
                RecordProbe("topology-capture", probeStarted);
                Warn("Client circuit topology capture failed: " + error);
                return;
            }
            foreach (ChunkKey key in m_chunks)
            {
                if (m_captureErrorsByChunk.ContainsKey(key) ||
                    !m_captureStatesByChunk.TryGetValue(key, out List<CircuitElementState> states))
                    continue;
                m_probePendingStates += states.Count;
                bool hasCircuit = states.Count > 0;
                if (hasCircuit)
                {
                    m_clientDesiredCircuitScope.Add(key);
                    CaptureCircuitCellValues(key, states, m_clientCircuitCellValues);
                }
                else
                {
                    m_clientDesiredCircuitScope.Remove(key);
                    m_clientCircuitCellValues.Remove(key);
                }
                m_clientTopologyDirtyChunks.Remove(key);
            }
            RecordProbe("topology-capture", probeStarted);
            m_clientScopeAcknowledged = false;
        }

        private HashSet<ChunkKey> CollectCircuitNeighborhood(ChunkKey key, CellEdit edit)
        {
            var chunks = new HashSet<ChunkKey> { key };
            AddNeighbor(chunks, new ChunkKey(key.X - 1, key.Z), edit.X == 0);
            AddNeighbor(chunks, new ChunkKey(key.X + 1, key.Z), edit.X == 15);
            AddNeighbor(chunks, new ChunkKey(key.X, key.Z - 1), edit.Z == 0);
            AddNeighbor(chunks, new ChunkKey(key.X, key.Z + 1), edit.Z == 15);
            AddNeighbor(chunks, new ChunkKey(key.X - 1, key.Z - 1), edit.X == 0 && edit.Z == 0);
            AddNeighbor(chunks, new ChunkKey(key.X - 1, key.Z + 1), edit.X == 0 && edit.Z == 15);
            AddNeighbor(chunks, new ChunkKey(key.X + 1, key.Z - 1), edit.X == 15 && edit.Z == 0);
            AddNeighbor(chunks, new ChunkKey(key.X + 1, key.Z + 1), edit.X == 15 && edit.Z == 15);
            return chunks;
        }

        private void AddNeighbor(HashSet<ChunkKey> chunks, ChunkKey key, bool touchesBoundary)
        {
            if (touchesBoundary && m_world.IsChunkReady(key)) chunks.Add(key);
        }

        private static string CircuitCellKey(int x, int y, int z) =>
            x + ":" + y + ":" + z;

        private void CaptureCircuitCellValues(ChunkKey key, IList<CircuitElementState> states,
            Dictionary<ChunkKey, Dictionary<string, int>> target)
        {
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            if (!m_world.TryCaptureChunkTopologyValues(key, values, out string error))
                Warn("Circuit topology baseline capture failed for " + key + ": " + error);
            target[key] = values;
        }

        private void ReconcileHostTopologyChanges()
        {
            var changed = new List<ChunkKey>();
            var membershipChunks = new HashSet<ChunkKey>();
            var statesByChunk = new Dictionary<ChunkKey, List<CircuitElementState>>();
            foreach (ChunkKey key in m_hostTopologyChangedChunks.ToArray())
            {
                if (!m_world.TryCaptureChunkTopology(key, m_states, out string error))
                {
                    Warn("Host topology capture failed for " + key + ": " + error);
                    continue;
                }
                bool hasCircuit = m_states.Count > 0;
                bool hadCircuit = m_hostCircuitChunks.Contains(key);
                if (hasCircuit) m_hostCircuitChunks.Add(key);
                else m_hostCircuitChunks.Remove(key);
                CaptureCircuitCellValues(key, m_states, m_hostCircuitCellValues);
                m_hostTopologyChangedChunks.Remove(key);
                changed.Add(key);
                if (hasCircuit != hadCircuit) membershipChunks.Add(key);
                statesByChunk[key] = m_states.Select(state => state.Clone()).ToList();
            }
            if (changed.Count == 0) return;

            m_transport.CopyAttachedPeerIds(m_peers);
            foreach (int clientId in m_peers)
            {
                if (!m_transport.IsHostCircuitAttached(clientId) ||
                    !m_peerStates.TryGetValue(clientId, out PeerState peer)) continue;
                bool peerMembershipChanged = false;
                foreach (ChunkKey key in changed)
                {
                    if (!peer.PendingScopeTerrainChunks.Contains(key)) continue;
                    bool hasCircuit = m_hostCircuitChunks.Contains(key);
                    bool hadCircuit = peer.CircuitScope.Contains(key);
                    if (hasCircuit && !hadCircuit)
                    {
                        peer.CircuitScope.Add(key);
                        MarkNeedsFullSnapshot(peer, key, SnapshotReason.TopologyChange);
                    }
                    if (!hasCircuit && !hadCircuit) continue;
                    List<CircuitElementState> states = statesByChunk[key];
                    if (!SendSnapshot(clientId, peer, key, m_lastCircuitStep,
                            CurrentHostTopologyTerrainSequence(key),
                            CurrentHostTopologyGeneration(key), states,
                            SnapshotReason.TopologyChange, out ulong snapshotId))
                    {
                        MarkNeedsFullSnapshot(peer, key, SnapshotReason.SendFailureRecovery);
                        continue;
                    }
                    RememberStates(peer, key, states);
                    TrackPauseBarrierSnapshot(peer, key, m_lastCircuitStep, snapshotId,
                        (long)(m_clock() * 1000.0));
                    if (!hasCircuit)
                    {
                        peer.CircuitScope.Remove(key);
                        ForgetPeerChunk(peer, key);
                    }
                    peerMembershipChanged |= membershipChunks.Contains(key);
                }
                if (!peerMembershipChanged) continue;
                var update = new CircuitScopeSnapshot
                {
                    Epoch = m_epoch,
                    RequiredTerrainSequence = peer.CircuitScope.Count == 0 ? 0L :
                        peer.CircuitScope.Max(CurrentHostTopologyTerrainSequence)
                };
                foreach (ChunkKey key in peer.PendingScopeTerrainChunks)
                {
                    bool hasCircuit = peer.CircuitScope.Contains(key);
                    update.Topologies.Add(new CircuitChunkTopology
                    {
                        Key = new CircuitChunkKey { X = key.X, Z = key.Z },
                        Generation = CurrentHostTopologyGeneration(key),
                        TerrainSequence = CurrentHostTopologyTerrainSequence(key),
                        HasCircuit = hasCircuit
                    });
                    if (hasCircuit)
                        update.Chunks.Add(new CircuitChunkKey { X = key.X, Z = key.Z });
                }
                var batch = new CircuitSyncBatch { Epoch = m_epoch, HostCircuitStep = m_lastCircuitStep };
                AddPayload(batch, MessageType.CircuitScopeSnapshot, update);
                if (!SendBatch(clientId, batch))
                    Warn("Circuit scope membership update could not be queued for client " + clientId + ".");
            }
        }

        private void ReconcileClientTopologyChanges()
        {
            bool membershipChanged = false;
            m_probeTrigger = "terrain-topology-change";
            m_probeScopeChunks = m_clientTopologyDirtyChunks.Count;
            foreach (ChunkKey key in m_clientTopologyDirtyChunks.ToArray())
            {
                long captureStarted = m_probe.Start();
                if (!m_world.TryCaptureChunkTopology(key, m_states, out string error))
                {
                    RecordProbe("topology-capture", captureStarted);
                    Warn("Client topology capture failed for " + key + ": " + error);
                    continue;
                }
                m_probePendingStates += m_states.Count;
                RecordProbe("topology-capture", captureStarted);
                CaptureCircuitCellValues(key, m_states, m_clientCircuitCellValues);
                bool hadCircuit = m_clientDesiredCircuitScope.Contains(key);
                bool hasCircuit = m_states.Count > 0;
                if (hasCircuit) m_clientDesiredCircuitScope.Add(key);
                else m_clientDesiredCircuitScope.Remove(key);
                membershipChanged |= hadCircuit != hasCircuit;
                m_clientTopologyTerrainSequences[key] =
                    m_transport.TerrainSequenceForChunk(key);
                m_clientTopologyDirtyChunks.Remove(key);
            }
            if (membershipChanged)
            {
                m_clientScopeAcknowledged = false;
                m_clientScopeRequested = false;
                m_nextClientScopeRefreshMs = 0;
            }
        }

        private CircuitChunkTopology CreateHostTopologyBoundary(ChunkKey key) =>
            new CircuitChunkTopology
            {
                Key = new CircuitChunkKey { X = key.X, Z = key.Z },
                Generation = m_hostTopologyGenerations.TryGetValue(key, out long generation)
                    ? generation : 0L,
                TerrainSequence = m_hostTopologyTerrainSequences.TryGetValue(key,
                    out long terrainSequence) ? terrainSequence : 0L
            };

        private long CurrentHostTopologyGeneration(ChunkKey key) =>
            m_hostTopologyGenerations.TryGetValue(key, out long generation) ? generation : 0L;

        private long CurrentHostTopologyTerrainSequence(ChunkKey key) =>
            m_hostTopologyTerrainSequences.TryGetValue(key, out long sequence)
                ? sequence : 0L;

        private uint GetHostChunkCommittedEventSequence(ChunkKey key) =>
            m_hostChunkCommittedEventSequences.TryGetValue(key, out uint sequence)
                ? sequence : 0u;

        private long RequiredClientTerrainSequence(long hostSequence) => Math.Max(0L, hostSequence);

        private long CurrentClientTopologyGeneration(ChunkKey key) =>
            m_clientTopologyGenerations.TryGetValue(key, out long generation) ? generation : 0L;

        private long CurrentClientTopologyTerrainSequence(ChunkKey key) =>
            m_clientTopologyTerrainSequences.TryGetValue(key, out long sequence)
                ? sequence : 0L;

        private static long BumpTopologyGeneration(Dictionary<ChunkKey, long> generations,
            ChunkKey key)
        {
            generations.TryGetValue(key, out long generation);
            generation = generation == long.MaxValue ? 1L : generation + 1L;
            generations[key] = generation;
            return generation;
        }

        private static long CurrentGeneration(Dictionary<ChunkKey, long> generations,
            ChunkKey key) => generations.TryGetValue(key, out long generation) ? generation : 0L;

        private static string DescribeTopologyBump(ChunkKey affected, int x, int y, int z,
            int oldValue, int newValue, bool wasKnown, long terrainSequence,
            long previousGeneration, long generation)
        {
            int oldContents = Game.Terrain.ExtractContents(oldValue);
            int newContents = Game.Terrain.ExtractContents(newValue);
            int oldData = Game.Terrain.ExtractData(oldValue);
            int newData = Game.Terrain.ExtractData(newValue);
            string oldCell = wasKnown ? oldContents + ":" + oldData : "unknown";
            return affected + " cell=(" + x + "," + y + "," + z + ") known=" + wasKnown +
                " value=" + oldCell + "->" + newContents + ":" + newData +
                " terrain=" + terrainSequence + " generation=" + previousGeneration +
                "->" + generation;
        }

        // Source: CircuitStateCodec.CaptureChunkTopology and BuildStatePayloads
        private bool SnapshotTopologyMatches(ChunkKey key,
            IList<CircuitElementState> expected, out string mismatch)
        {
            mismatch = null;
            if (!m_world.TryCaptureChunkTopology(key, m_states, out string error))
            {
                mismatch = "capture-error=" + error;
                m_lastTopologyMismatch = key + " " + mismatch;
                return false;
            }
            var expectedIdentities = new HashSet<(int X, int Y, int Z, uint Face, uint Type)>();
            foreach (CircuitElementState state in expected)
            {
                if (!expectedIdentities.Add((state.X, state.Y, state.Z,
                        state.MountingFace, state.ElementType)))
                {
                    mismatch = "duplicate-host=" + DescribeTopologyState(state);
                    m_lastTopologyMismatch = key + " " + mismatch;
                    return false;
                }
            }
            foreach (CircuitElementState state in m_states)
            {
                var identity = (state.X, state.Y, state.Z, state.MountingFace,
                    state.ElementType);
                if (!expectedIdentities.Remove(identity))
                {
                    mismatch = "extra-client=" + DescribeTopologyState(state) +
                        " hostCount=" + expected.Count + " clientCount=" + m_states.Count;
                    m_lastTopologyMismatch = key + " " + mismatch;
                    return false;
                }
            }
            if (expectedIdentities.Count > 0)
            {
                var missing = expectedIdentities.First();
                mismatch = "missing-client=(" + missing.X + "," + missing.Y + "," +
                    missing.Z + ") face=" + missing.Face + " type=0x" +
                    missing.Type.ToString("X8",
                        System.Globalization.CultureInfo.InvariantCulture) +
                    " hostCount=" + expected.Count + " clientCount=" + m_states.Count;
                m_lastTopologyMismatch = key + " " + mismatch;
                return false;
            }
            m_lastTopologyMismatch = "none";
            return true;
        }

        private static string DescribeTopologyState(CircuitElementState state) =>
            "(" + state.X + "," + state.Y + "," + state.Z + ") face=" +
            state.MountingFace + " type=0x" + state.ElementType.ToString("X8",
                System.Globalization.CultureInfo.InvariantCulture);

        private string DescribeLocalCircuitStep() =>
            m_world.TryGetClock(out int step, out _, out _, out _, out _) ?
                step.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unavailable";

        private void ClearInactivePeerSyncScopes()
        {
            for (int i = m_activePeerSyncScopeCount; i < m_peerSyncScopes.Count; i++)
            {
                m_peerSyncScopes[i].Peer = null;
                m_peerSyncScopes[i].Chunks.Clear();
            }
        }

        private List<CircuitElementState> FindChangedStates(PeerState peer, ChunkKey chunk,
            List<CircuitElementState> current)
        {
            if (!peer.LastStates.TryGetValue(chunk, out Dictionary<string, byte[]> previous))
            {
                MarkNeedsFullSnapshot(peer, chunk, SnapshotReason.NeedsFullRecovery);
                return new List<CircuitElementState>();
            }
            var changed = new List<CircuitElementState>();
            var currentKeys = new HashSet<string>(StringComparer.Ordinal);
            if (!peer.LastElementTypes.TryGetValue(chunk, out Dictionary<string, uint> oldTypes))
                oldTypes = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (CircuitElementState state in current)
            {
                string key = ElementKey(state);
                currentKeys.Add(key);
                if (oldTypes.TryGetValue(key, out uint oldType) && oldType != state.ElementType)
                {
                    MarkNeedsFullSnapshot(peer, chunk, SnapshotReason.TopologyChange);
                    return new List<CircuitElementState>();
                }
                if (CircuitStateCodec.IsDisplayElementType(state.ElementType))
                {
                    if (!previous.ContainsKey(key))
                        MarkNeedsFullSnapshot(peer, chunk, SnapshotReason.TopologyChange);
                    continue;
                }
                byte[] bytes = state.ToByteArray();
                if (!previous.TryGetValue(key, out byte[] old) || !ByteArraysEqual(bytes, old))
                    changed.Add(state);
            }
            if (previous.Keys.Any(key => !currentKeys.Contains(key)))
            {
                MarkNeedsFullSnapshot(peer, chunk, SnapshotReason.TopologyChange);
                return new List<CircuitElementState>();
            }
            return changed;
        }

        private uint ComputeCircuitSyncHash(IList<CircuitElementState> states) =>
            m_world.ComputeCapturedHash(states);

        private void MarkNeedsFullSnapshot(PeerState peer, ChunkKey chunk,
            SnapshotReason reason)
        {
            bool added = peer.NeedsFullSnapshot.Add(chunk);
            if (added)
                m_hostNeedsFullSnapshotChunksQueued++;
            if (added && reason == SnapshotReason.RepairAckMismatch)
                m_hostRepairAckChunksQueued++;
            if (!peer.NeedsFullSnapshotReasons.TryGetValue(chunk,
                    out SnapshotReason existing) || SnapshotReasonPriority(reason) >
                    SnapshotReasonPriority(existing))
                peer.NeedsFullSnapshotReasons[chunk] = reason;
        }

        private void ClearNeedsFullSnapshot(PeerState peer, ChunkKey chunk)
        {
            peer.NeedsFullSnapshot.Remove(chunk);
            peer.NeedsFullSnapshotReasons.Remove(chunk);
        }

        private void QueueRepair(PeerState peer, ChunkKey chunk, SnapshotReason reason)
        {
            if (peer.RepairRequests.Add(chunk))
            {
                if (reason == SnapshotReason.HashMismatch)
                    m_hostHashRepairChunksQueued++;
                else if (reason == SnapshotReason.RepairAckMismatch)
                    m_hostRepairAckChunksQueued++;
            }
            if (!peer.RepairRequestReasons.TryGetValue(chunk,
                    out SnapshotReason existing) || SnapshotReasonPriority(reason) >
                    SnapshotReasonPriority(existing))
                peer.RepairRequestReasons[chunk] = reason;
        }

        private bool TryTakeFullSnapshotReason(PeerState peer, ChunkKey chunk,
            out SnapshotReason reason)
        {
            if (!peer.NeedsFullSnapshot.Remove(chunk))
            {
                reason = default;
                return false;
            }
            if (!peer.NeedsFullSnapshotReasons.TryGetValue(chunk, out reason))
                throw new InvalidOperationException("Queued circuit snapshot has no reason for " + chunk + ".");
            peer.NeedsFullSnapshotReasons.Remove(chunk);
            return true;
        }

        private bool TryTakeRepairReason(PeerState peer, ChunkKey chunk,
            out SnapshotReason reason)
        {
            if (!peer.RepairRequests.Remove(chunk))
            {
                reason = default;
                return false;
            }
            if (!peer.RepairRequestReasons.TryGetValue(chunk, out reason))
                throw new InvalidOperationException("Queued circuit repair has no reason for " + chunk + ".");
            peer.RepairRequestReasons.Remove(chunk);
            return true;
        }

        private static int SnapshotReasonPriority(SnapshotReason reason)
        {
            switch (reason)
            {
                case SnapshotReason.InitialScope: return 0;
                case SnapshotReason.HashMismatch: return 1;
                case SnapshotReason.RepairAckMismatch: return 2;
                case SnapshotReason.ClientRequest: return 3;
                case SnapshotReason.NeedsFullRecovery: return 4;
                case SnapshotReason.TopologyChange: return 5;
                case SnapshotReason.PauseBarrier: return 6;
                case SnapshotReason.SendFailureRecovery: return 7;
                default: throw new ArgumentOutOfRangeException(nameof(reason), reason,
                    "Unknown circuit snapshot reason.");
            }
        }

        private static void RememberStates(PeerState peer, ChunkKey chunk,
            List<CircuitElementState> states)
        {
            var cache = new Dictionary<string, byte[]>(states.Count, StringComparer.Ordinal);
            var types = new Dictionary<string, uint>(states.Count, StringComparer.Ordinal);
            foreach (CircuitElementState state in states)
            {
                string key = ElementKey(state);
                types[key] = state.ElementType;
                if (!CircuitStateCodec.IsDisplayElementType(state.ElementType))
                    cache[key] = state.ToByteArray();
                else
                    cache[key] = Array.Empty<byte>();
            }
            peer.LastStates[chunk] = cache;
            peer.LastElementTypes[chunk] = types;
        }

        private bool SendSnapshot(int clientId, PeerState peer, ChunkKey chunk, int hostStep,
            long terrainSequence, long topologyGeneration,
            List<CircuitElementState> states, SnapshotReason reason,
            out ulong snapshotId)
        {
            snapshotId = NextSnapshotId();
            List<CircuitPayload> payloads;
            try
            {
                payloads = BuildStatePayloads(chunk, hostStep,
                    terrainSequence, topologyGeneration, states, full: true, snapshotId,
                    m_nextCommittedEventSequence,
                    GetHostChunkCommittedEventSequence(chunk));
            }
            catch (Exception e)
            {
                m_hostSnapshotSendFailures++;
                Warn("Circuit snapshot framing failed for " + chunk + ": " +
                    e.GetType().Name + ": " + e.Message);
                return false;
            }
            var batch = new CircuitSyncBatch { Epoch = m_epoch, HostCircuitStep = hostStep };
            foreach (CircuitPayload payload in payloads)
            {
                CircuitSyncBatch candidate = batch.Clone();
                candidate.Payloads.Add(payload);
                if (candidate.CalculateSize() <= MaximumCircuitFrameBytes)
                {
                    batch = candidate;
                    continue;
                }
                if (batch.Payloads.Count > 0 && !SendBatch(clientId, batch))
                {
                    m_hostSnapshotSendFailures++;
                    Warn("Full circuit snapshot send failed for client " + clientId + " chunk " + chunk + ".");
                    return false;
                }
                batch = new CircuitSyncBatch { Epoch = m_epoch, HostCircuitStep = hostStep };
                candidate = batch.Clone();
                candidate.Payloads.Add(payload);
                if (candidate.CalculateSize() > MaximumCircuitFrameBytes || !SendBatch(clientId, candidate))
                {
                    m_hostSnapshotSendFailures++;
                    Warn("Circuit snapshot part exceeds the frame limit for client " + clientId +
                        " chunk " + chunk + ".");
                    return false;
                }
                batch = new CircuitSyncBatch { Epoch = m_epoch, HostCircuitStep = hostStep };
            }
            if (batch.Payloads.Count > 0 && !SendBatch(clientId, batch))
            {
                m_hostSnapshotSendFailures++;
                Warn("Full circuit snapshot send failed for client " + clientId + " chunk " + chunk + ".");
                return false;
            }
            uint snapshotHash = ComputeCircuitSyncHash(states);
            RegisterSnapshotBoundary(peer, snapshotId, chunk, hostStep, terrainSequence,
                topologyGeneration, snapshotHash, full: true,
                m_nextCommittedEventSequence,
                GetHostChunkCommittedEventSequence(chunk),
                reason, states.Count);
            if (reason == SnapshotReason.HashMismatch || reason == SnapshotReason.RepairAckMismatch)
                LogCircuitHashDiagnostic("event=repair-snapshot-sent clientId=" + clientId +
                    " snapshotId=" + snapshotId + " snapshotKind=full reason=" + reason +
                    " chunk=" + chunk + " hostStep=" + hostStep +
                    " terrain=" + terrainSequence + " topology=" + topologyGeneration +
                    " committedSequence=" + m_nextCommittedEventSequence +
                    " timeline=" + m_timelineGeneration +
                    " states=" + states.Count + " hostHash=" + FormatHash(snapshotHash));
            m_hostSnapshotChunksSent++;
            m_hostSnapshotElementsSent += states.Count;
            m_hostSnapshotPartsSent += payloads.Count - 1;
            m_hostSevenSegmentStates += CircuitStateCodec.CountSevenSegmentStates(states);
            RecordSnapshotReason(reason);
            return true;
        }

        private void RecordSnapshotReason(SnapshotReason reason)
        {
            switch (reason)
            {
                case SnapshotReason.InitialScope: m_hostSnapshotsInitialScope++; break;
                case SnapshotReason.HashMismatch: m_hostSnapshotsHashRepair++; break;
                case SnapshotReason.RepairAckMismatch: m_hostSnapshotsRepairAckRepair++; break;
                case SnapshotReason.ClientRequest: m_hostSnapshotsClientRequest++; break;
                case SnapshotReason.NeedsFullRecovery: m_hostSnapshotsNeedsFullRecovery++; break;
                case SnapshotReason.PauseBarrier: m_hostSnapshotsPauseBarrier++; break;
                case SnapshotReason.TopologyChange: m_hostSnapshotsTopologyChange++; break;
                case SnapshotReason.SendFailureRecovery: m_hostSnapshotsSendFailureRecovery++; break;
                default: throw new ArgumentOutOfRangeException(nameof(reason), reason,
                    "Unknown circuit snapshot reason.");
            }
        }

        private List<CircuitPayload> BuildStatePayloads(ChunkKey chunk, int hostStep,
            long terrainSequence, long topologyGeneration,
            List<CircuitElementState> states, bool full, ulong snapshotId,
            uint lastEventSequence, uint chunkEventSequence)
        {
            var groups = new List<List<CircuitElementState>>();
            var current = new List<CircuitElementState>(MaximumSnapshotElements);
            foreach (CircuitElementState state in states)
            {
                current.Add(state);
                CircuitSnapshotPart candidate = CreateSnapshotPart(chunk, hostStep,
                    terrainSequence, topologyGeneration, current, full, snapshotId, 0, 1,
                    lastEventSequence, chunkEventSequence);
                if (current.Count <= MaximumSnapshotElements &&
                    candidate.CalculateSize() + 64 <= MaximumCircuitFrameBytes)
                    continue;
                current.RemoveAt(current.Count - 1);
                if (current.Count == 0)
                    throw new InvalidOperationException("A single circuit element state exceeds the frame limit.");
                groups.Add(current);
                current = new List<CircuitElementState>(MaximumSnapshotElements) { state };
                candidate = CreateSnapshotPart(chunk, hostStep, terrainSequence,
                    topologyGeneration, current, full, snapshotId, 0, 1,
                    lastEventSequence, chunkEventSequence);
                if (candidate.CalculateSize() + 64 > MaximumCircuitFrameBytes)
                    throw new InvalidOperationException("A single circuit element state exceeds the frame limit.");
            }
            if (current.Count > 0 || groups.Count == 0) groups.Add(current);

            var payloads = new List<CircuitPayload>(groups.Count + 1);
            payloads.Add(CreatePayload(MessageType.CircuitRepairBarrier,
                new CircuitRepairBarrier
                {
                    Epoch = m_epoch,
                    SnapshotId = snapshotId,
                    HostCircuitStep = hostStep,
                    ChunkX = chunk.X,
                    ChunkZ = chunk.Z,
                    PartCount = (uint)groups.Count,
                    Reason = full ? "full" : "delta"
                }));
            for (int i = 0; i < groups.Count; i++)
            {
                CircuitSnapshotPart message = CreateSnapshotPart(chunk, hostStep,
                    terrainSequence, topologyGeneration, groups[i], full, snapshotId, i,
                    groups.Count, lastEventSequence, chunkEventSequence);
                payloads.Add(CreatePayload(MessageType.CircuitSnapshotPart, message));
            }
            return payloads;
        }

        private CircuitSnapshotPart CreateSnapshotPart(ChunkKey chunk, int hostStep,
            long terrainSequence, long topologyGeneration,
            IList<CircuitElementState> states, bool full,
            ulong snapshotId, int partIndex, int partCount,
            uint lastEventSequence, uint chunkEventSequence)
        {
            var message = new CircuitSnapshotPart
            {
                Epoch = m_epoch,
                SnapshotId = snapshotId,
                HostCircuitStep = hostStep,
                ChunkX = chunk.X,
                ChunkZ = chunk.Z,
                PartIndex = (uint)partIndex,
                PartCount = (uint)partCount,
                Full = full,
                RequiredTerrainSequence = terrainSequence,
                RequiredTopologyGeneration = topologyGeneration,
                LastCommittedEventSequence = unchecked((int)lastEventSequence),
                TimelineGeneration = m_timelineGeneration,
                ChunkCommittedEventSequence = chunkEventSequence
            };
            message.Elements.Add(states);
            return message;
        }

        private static CircuitPayload CreatePayload<T>(MessageType type, T message)
            where T : IMessage<T> => new CircuitPayload
            {
                MessageType = (uint)type,
                Payload = ByteString.CopyFrom(message.ToByteArray())
            };

        private bool AppendPayload(int clientId, ref CircuitSyncBatch batch,
            CircuitClock clockMessage, CircuitPayload payload)
        {
            CircuitSyncBatch candidate = batch.Clone();
            candidate.Payloads.Add(payload);
            if (candidate.CalculateSize() <= MaximumCircuitFrameBytes)
            {
                batch = candidate;
                return true;
            }
            if (batch.Payloads.Count > 0 && !SendBatch(clientId, batch)) return false;
            var clockOnly = new CircuitSyncBatch { Epoch = m_epoch,
                HostCircuitStep = clockMessage.HostCircuitStep };
            AddPayload(clockOnly, MessageType.CircuitClock, clockMessage);
            candidate = clockOnly.Clone();
            candidate.Payloads.Add(payload);
            if (candidate.CalculateSize() <= MaximumCircuitFrameBytes)
            {
                batch = candidate;
                return true;
            }
            if (!SendBatch(clientId, clockOnly)) return false;
            var standalone = new CircuitSyncBatch { Epoch = m_epoch,
                HostCircuitStep = clockMessage.HostCircuitStep };
            standalone.Payloads.Add(payload);
            if (standalone.CalculateSize() > MaximumCircuitFrameBytes)
            {
                Warn("A circuit state payload exceeds the transport frame limit.");
                return false;
            }
            batch = standalone;
            return true;
        }

        private void RequestClientScope(long nowMs)
        {
            m_chunks.Clear();
            if (m_world.CopyLoadedChunks(m_chunks) < 0 || m_chunks.Count > MaximumInterestChunks)
            {
                Warn("Client loaded circuit scope exceeds the protocol limit.");
                return;
            }
            var request = new CircuitSnapshotRequest
            {
                Epoch = m_epoch,
                RequestId = NextSnapshotId(),
                ReplaceScope = true,
                ScopeQuery = true
            };
            foreach (ChunkKey key in m_chunks)
                request.Chunks.Add(new CircuitChunkKey { X = key.X, Z = key.Z });
            if (SendHost(MessageType.CircuitSnapshotRequest, request.ToByteArray()))
            {
                m_clientScopeRequested = true;
                m_clientScopeAcknowledged = false;
                m_clientScopeResponseReceived = false;
                m_clientPendingScopeRequestId = request.RequestId;
                m_clientScopeRequestSentMs = nowMs;
                m_clientRequestedScope.Clear();
                foreach (ChunkKey key in m_chunks)
                    m_clientRequestedScope.Add(key);
                m_clientScopeRequests++;
                m_clientScopeChunks += request.Chunks.Count;
            }
            else
                Warn("Initial circuit snapshot request could not be queued.");
            UpdateClientSimulationClock();
        }

        private bool ClientScopeChanged()
        {
            m_chunks.Clear();
            int count = m_world.CopyLoadedChunks(m_chunks);
            if (count < 0 || count > MaximumInterestChunks)
                return true;
            return m_clientRequestedScope.Count != m_chunks.Count ||
                !m_clientRequestedScope.SetEquals(m_chunks);
        }

        private void RequestChunkSnapshot(ChunkKey chunk, bool hashMismatch = false)
        {
            if (!m_clientCircuitScope.Contains(chunk)) return;
            var request = new CircuitSnapshotRequest
            {
                Epoch = m_epoch,
                RequestId = NextSnapshotId(),
                ReplaceScope = false,
                HashMismatch = hashMismatch
            };
            request.Chunks.Add(new CircuitChunkKey { X = chunk.X, Z = chunk.Z });
            if (!SendHost(MessageType.CircuitSnapshotRequest, request.ToByteArray()))
                Warn("Circuit repair request could not be queued for " + chunk + ".");
        }

        private static bool IsSnapshotExpired(SnapshotAssembly assembly, long nowMs) =>
            nowMs - assembly.CreatedMs >= SnapshotTimeoutMs;

        private void RequestSnapshotRecovery(ChunkKey chunk, SnapshotAssembly assembly,
            string reason)
        {
            if (!m_clientCircuitScope.Contains(chunk)) return;
            RequestChunkSnapshot(chunk);
            Warn("Circuit snapshot discarded after " + reason + " at " + chunk +
                " (required generation=" + assembly.RequiredTopologyGeneration +
                ", local generation=" + CurrentClientTopologyGeneration(chunk) +
                "); requested an authoritative full snapshot.");
        }

        private void QueueHashCheckpoint(CircuitHashCheckpoint checkpoint)
        {
            if (checkpoint == null || checkpoint.Epoch != m_epoch ||
                checkpoint.Chunks.Count > MaximumInterestChunks ||
                checkpoint.TimelineGeneration != m_timelineGeneration)
                return;
            if (m_clientHashCheckpoints.Count >= 32 &&
                !m_clientHashCheckpoints.ContainsKey(checkpoint.HostCircuitStep))
                m_clientHashCheckpoints.Remove(m_clientHashCheckpoints.Keys.First());
            m_clientHashCheckpoints[checkpoint.HostCircuitStep] = checkpoint;
            RefreshClientStepBarrier();
        }

        private void RefreshClientStepBarrier()
        {
            if (m_transport?.IsClient != true || !m_clientClockInitialized ||
                !m_world.TryGetClock(out int currentStep, out _, out _, out _, out _))
            {
                CircuitSimulationPauseSignal.ClearClientStepBarrier();
                return;
            }
            int barrierStep = int.MaxValue;
            foreach (SnapshotAssembly assembly in m_snapshotAssemblies.Values)
                if (assembly.TimelineGeneration == m_timelineGeneration)
                    barrierStep = Math.Min(barrierStep, Math.Max(currentStep,
                        assembly.HostStep));
            foreach (int step in m_clientHashCheckpoints.Keys)
                barrierStep = Math.Min(barrierStep, Math.Max(currentStep, step));
            if (m_clientResumePending || m_clientClockAccelerated)
            {
                CircuitSimulationPauseSignal.SetClientStepBarrier(int.MaxValue);
                return;
            }
            foreach (CircuitEvent circuitEvent in m_deferredEvents)
            {
                if (circuitEvent.TimelineGeneration != m_timelineGeneration) continue;
                if (circuitEvent.Committed || circuitEvent.Cancelled)
                {
                    barrierStep = Math.Min(barrierStep, Math.Max(currentStep,
                        circuitEvent.HostCircuitStep));
                    continue;
                }
                int targetBoundary = circuitEvent.HostCircuitStep - 2;
                barrierStep = Math.Min(barrierStep, Math.Max(currentStep, targetBoundary));
            }
            CircuitSimulationPauseSignal.SetClientStepBarrier(barrierStep);
        }

        private bool BeforeClientCircuitStep(int currentStep)
        {
            int nextStep = currentStep == int.MaxValue ? int.MaxValue : currentStep + 1;
            if (!HasClientCircuitBoundaryWork(currentStep, nextStep)) return true;
            ProcessReadyClientCircuitBoundary(currentStep);
            if (CircuitSimulationPauseSignal.ClientSimulationTargetStep <= currentStep)
                return false;
            return ProcessClientCircuitEventsForStep(nextStep);
        }

        private bool ProcessClientCircuitEventsForStep(int targetStep)
        {
            for (int i = m_deferredEvents.Count - 1; i >= 0; i--)
            {
                CircuitEvent circuitEvent = m_deferredEvents[i];
                if (circuitEvent.HostCircuitStep != targetStep) continue;
                if (!circuitEvent.Committed && !circuitEvent.Cancelled) continue;
                if (circuitEvent.Cancelled)
                {
                    RejectCancelledOperation(circuitEvent);
                    m_deferredEvents.RemoveAt(i);
                    continue;
                }
                ChunkKey chunk = new ChunkKey(circuitEvent.X >> 4,
                    circuitEvent.Z >> 4);
                if (CurrentClientTopologyGeneration(chunk) <=
                    circuitEvent.RequiredTopologyGeneration) continue;
                m_deferredEvents.RemoveAt(i);
                HandleSupersededCircuitEvent(circuitEvent);
            }
            int count = 0;
            foreach (CircuitEvent circuitEvent in m_deferredEvents)
                if (circuitEvent.HostCircuitStep == targetStep) count++;
            if (count == 0) return true;
            for (int i = 0; i < m_deferredEvents.Count; i++)
            {
                CircuitEvent circuitEvent = m_deferredEvents[i];
                if (circuitEvent.HostCircuitStep != targetStep) continue;
                if (circuitEvent.Cancelled) continue;
                if (!circuitEvent.Committed || circuitEvent.CommittedEventSequence == 0)
                    return false;
                ChunkKey chunk = new ChunkKey(circuitEvent.X >> 4,
                    circuitEvent.Z >> 4);
                if (HasSnapshotForChunkAtOrBefore(chunk, targetStep - 1)) return false;
                if (!IsClientEventReady(circuitEvent, chunk)) return false;
            }
            for (int i = 0; i < m_deferredEvents.Count;)
            {
                CircuitEvent circuitEvent = m_deferredEvents[i];
                if (circuitEvent.HostCircuitStep != targetStep ||
                    (!circuitEvent.Committed && !circuitEvent.Cancelled))
                {
                    i++;
                    continue;
                }
                if (!ApplyClientEvent(circuitEvent)) return false;
                m_deferredEvents.RemoveAt(i);
            }
            return true;
        }

        private bool IsClientEventReady(CircuitEvent circuitEvent, ChunkKey chunk)
        {
            if (circuitEvent.TimelineGeneration != m_timelineGeneration ||
                m_transport.TerrainSequence < RequiredClientTerrainSequence(
                    circuitEvent.RequiredTerrainSequence)) return false;
            long localGeneration = CurrentClientTopologyGeneration(chunk);
            if (localGeneration < circuitEvent.RequiredTopologyGeneration)
            {
                m_clientScopeRequested = false;
                m_nextClientScopeRefreshMs = 0;
                if (m_clientEventTopologyResyncRequests.Add(chunk))
                    m_world.MarkClientCircuitTopologyDirty(chunk);
                return false;
            }
            return localGeneration == circuitEvent.RequiredTopologyGeneration;
        }

        private void HandleSupersededCircuitEvent(CircuitEvent circuitEvent)
        {
            ChunkKey chunk = new ChunkKey(circuitEvent.X >> 4, circuitEvent.Z >> 4);
            if (circuitEvent.OriginClientId == unchecked((uint)m_transport.LocalClientId) &&
                circuitEvent.RequestId != 0)
            {
                int? currentCell = m_world.TryGetCellValue(circuitEvent.X,
                    circuitEvent.Y, circuitEvent.Z, out int value) ? value : (int?)null;
                m_pendingOperations.Remove(circuitEvent.RequestId);
                m_presentation.RejectPresentation(unchecked((long)circuitEvent.RequestId),
                    currentCell);
            }
            m_clientChunkBoundaries.Remove(chunk);
            RequestChunkSnapshot(chunk);
        }

        private void RejectCancelledOperation(CircuitEvent circuitEvent)
        {
            ChunkKey chunk = new ChunkKey(circuitEvent.X >> 4, circuitEvent.Z >> 4);
            if (circuitEvent.OriginClientId != unchecked((uint)m_transport.LocalClientId) ||
                circuitEvent.RequestId == 0) return;
            int? currentCell = m_world.TryGetCellValue(circuitEvent.X,
                circuitEvent.Y, circuitEvent.Z, out int value) ? value : (int?)null;
            m_pendingOperations.Remove(circuitEvent.RequestId);
            m_presentation.RejectPresentation(unchecked((long)circuitEvent.RequestId),
                currentCell);
        }

        // Source: Mod/SuMultiplayer/Engine/CircuitStateCodec.cs:CircuitStateCodec.ApplyOperationEvent
        private bool ApplyClientEvent(CircuitEvent circuitEvent)
        {
            ChunkKey chunk = new ChunkKey(circuitEvent.X >> 4, circuitEvent.Z >> 4);
            if (circuitEvent.Cancelled)
            {
                RejectCancelledOperation(circuitEvent);
                return true;
            }
            if (!circuitEvent.Committed || circuitEvent.CommittedEventSequence == 0) return false;
            if (!IsClientEventReady(circuitEvent, chunk)) return false;
            m_presentation.ApplyAuthoritativeCell(circuitEvent.X, circuitEvent.Y,
                circuitEvent.Z, (int)circuitEvent.MountingFace,
                unchecked((int)circuitEvent.Value));
            if (!m_world.ApplyOperationEvent(circuitEvent, out string operationError))
            {
                Warn("Circuit operation event failed at target step " +
                    circuitEvent.HostCircuitStep + ": " + operationError +
                    "; requested an authoritative chunk snapshot.");
                m_clientChunkBoundaries.Remove(chunk);
                RequestChunkSnapshot(chunk);
                return false;
            }
            m_clientEventTopologyResyncRequests.Remove(chunk);
            if (IsNewerEventSequence(circuitEvent.CommittedEventSequence,
                    unchecked((uint)m_clientLastCommittedEventSequence)))
                m_clientLastCommittedEventSequence =
                    unchecked((int)circuitEvent.CommittedEventSequence);
            if (circuitEvent.OriginClientId == unchecked((uint)m_transport.LocalClientId) &&
                circuitEvent.RequestId != 0)
            {
                ulong requestId = circuitEvent.RequestId;
                m_pendingOperations.Remove(requestId);
                m_presentation.ResolvePresentation(unchecked((long)requestId),
                    unchecked((int)circuitEvent.Value));
            }
            m_clientChunkBoundaries[chunk] = new AppliedBoundary(circuitEvent.HostCircuitStep,
                circuitEvent.RequiredTerrainSequence, circuitEvent.RequiredTopologyGeneration,
                unchecked((int)circuitEvent.CommittedEventSequence),
                circuitEvent.TimelineGeneration);
            return true;
        }

        private void AdvanceClientCommittedEventSequence(ChunkKey chunk, uint sequence)
        {
            if (!m_clientChunkCommittedEventSequences.TryGetValue(chunk,
                    out uint currentSequence) ||
                IsNewerEventSequence(sequence, currentSequence))
                m_clientChunkCommittedEventSequences[chunk] = sequence;
        }

        private void ProcessReadyClientCircuitBoundary(int step)
        {
            if (m_transport?.IsClient != true || m_epoch == 0) return;
            int nextStep = step == int.MaxValue ? int.MaxValue : step + 1;
            if (!HasClientCircuitBoundaryWork(step, nextStep)) return;
            ProcessReadySnapshots();
            for (int i = m_deferredEvents.Count - 1; i >= 0; i--)
            {
                CircuitEvent pending = m_deferredEvents[i];
                if (pending.HostCircuitStep > step) continue;
                m_deferredEvents.RemoveAt(i);
                HandleLateCircuitEvent(pending);
            }
            foreach (int expiredStep in m_clientHashCheckpoints.Keys
                         .Where(candidate => candidate < step).ToArray())
                m_clientHashCheckpoints.Remove(expiredStep);
            if (m_clientHashCheckpoints.TryGetValue(step,
                    out CircuitHashCheckpoint checkpoint))
            {
                m_checkpointChunks.Clear();
                m_checkpointExpected.Clear();
                var waitingForChunks = new List<CircuitChunkHash>();
                foreach (CircuitChunkHash expected in checkpoint.Chunks)
                {
                    ChunkKey chunk = new ChunkKey(expected.ChunkX, expected.ChunkZ);
                    if (!m_clientCircuitScope.Contains(chunk)) continue;
                    if (HasSnapshotForChunkAtOrBefore(chunk, step))
                    {
                        waitingForChunks.Add(expected);
                        continue;
                    }
                    long localGeneration = CurrentClientTopologyGeneration(chunk);
                    long localTerrainSequence = m_transport.TerrainSequenceForChunk(chunk);
                    uint localCommittedEventSequence =
                        m_clientChunkCommittedEventSequences.TryGetValue(chunk,
                            out uint knownCommittedEventSequence)
                            ? knownCommittedEventSequence : 0;
                    if (localGeneration < expected.TopologyGeneration ||
                        localTerrainSequence < expected.TerrainSequence ||
                        (localCommittedEventSequence !=
                            expected.ChunkCommittedEventSequence &&
                            IsNewerEventSequence(expected.ChunkCommittedEventSequence,
                                localCommittedEventSequence)))
                    {
                        waitingForChunks.Add(expected);
                        continue;
                    }
                    if (localGeneration != expected.TopologyGeneration ||
                        localTerrainSequence != expected.TerrainSequence ||
                        localCommittedEventSequence !=
                            expected.ChunkCommittedEventSequence)
                        continue;
                    m_checkpointChunks.Add(chunk);
                    m_checkpointExpected[chunk] = expected;
                }
                m_clientCheckpointChunksChecked += m_checkpointChunks.Count;
                if (m_checkpointChunks.Count > 0 &&
                    !m_world.TryCaptureChunksBestEffort(m_checkpointChunks,
                        m_captureStatesByChunk, m_captureErrorsByChunk,
                        out string captureError))
                {
                    m_clientCheckpointCapturesFailed += m_checkpointChunks.Count;
                    Warn("Circuit checkpoint capture failed: " + captureError);
                }
                else
                {
                    m_clientCheckpointCapturesFailed += m_captureErrorsByChunk.Count;
                    foreach (ChunkKey chunk in m_checkpointChunks)
                    {
                        if (m_captureErrorsByChunk.ContainsKey(chunk) ||
                            !m_captureStatesByChunk.TryGetValue(chunk,
                                out List<CircuitElementState> captured)) continue;
                        CircuitChunkHash expected = m_checkpointExpected[chunk];
                        uint actualHash = ComputeCircuitSyncHash(captured);
                        if (actualHash == expected.Hash) continue;
                        m_clientCheckpointMismatches++;
                        int localStep = m_world.TryGetClock(out int capturedStep,
                            out _, out _, out _, out _) ? capturedStep : -1;
                        m_lastCheckpointMismatch = "chunk=" + chunk + " checkpointStep=" +
                            step + " localStep=" + localStep + " hostHash=" +
                            FormatHash(expected.Hash) + " clientHash=" + FormatHash(actualHash) +
                            " committed=" + expected.ChunkCommittedEventSequence;
                        QueueClientHashRepair(chunk);
                    }
                }
                checkpoint.Chunks.Clear();
                checkpoint.Chunks.Add(waitingForChunks);
                if (waitingForChunks.Count == 0)
                {
                    m_clientHashCheckpoints.Remove(step);
                    m_clientCheckpointsProcessed++;
                }
            }
            if (step < int.MaxValue)
                ProcessClientCircuitEventsForStep(nextStep);
            RefreshClientStepBarrier();
        }

        private bool HasClientCircuitBoundaryWork(int completedStep, int nextStep)
        {
            foreach (SnapshotAssembly assembly in m_snapshotAssemblies.Values)
                if (assembly.HostStep <= completedStep) return true;
            foreach (int step in m_clientHashCheckpoints.Keys)
                if (step <= completedStep) return true;
            foreach (CircuitEvent circuitEvent in m_deferredEvents)
                if (circuitEvent.HostCircuitStep <= nextStep) return true;
            return false;
        }

        private void QueueClientHashRepair(ChunkKey chunk)
        {
            if (!m_clientRepairRequests.Add(chunk)) return;
            RequestChunkSnapshot(chunk, hashMismatch: true);
        }

        private void HandleFrame(int clientId, ReceivedFrame frame)
        {
            if (!m_attached || frame.Payload == null) return;
            if (m_transport.IsHosting)
            {
                if (clientId <= 0 || !m_transport.IsHostCircuitAttached(clientId))
                {
                    Warn("Circuit frame arrived without an attached primary client identity.");
                    return;
                }
                switch (frame.Type)
                {
                    case MessageType.CircuitEventRequest:
                        HandleOperationRequest(clientId, CircuitEventRequest.Parser.ParseFrom(frame.Payload));
                        break;
                    case MessageType.CircuitSnapshotRequest:
                        HandleSnapshotRequest(clientId, CircuitSnapshotRequest.Parser.ParseFrom(frame.Payload));
                        break;
                    case MessageType.CircuitRepairApplied:
                        HandleRepairApplied(clientId, CircuitRepairApplied.Parser.ParseFrom(frame.Payload));
                        break;
                    default:
                        Warn("Host rejected unexpected circuit message " + frame.Type + ".");
                        break;
                }
                return;
            }
            if (m_transport.IsClient && clientId == 0 && m_transport.IsClientCircuitAttached)
            {
                if (frame.Type == MessageType.CircuitSyncBatch)
                    HandleBatch(CircuitSyncBatch.Parser.ParseFrom(frame.Payload));
                else
                    Warn("Client rejected unexpected standalone circuit message " + frame.Type + ".");
            }
        }

        private void HandleOperationRequest(int clientId, CircuitEventRequest request)
        {
            if (request == null || request.RequestId == 0 || request.MountingFace > 5 ||
                request.Operation < CircuitOperation.ButtonPress ||
                request.Operation > CircuitOperation.FurnitureSwitch || m_useAuthority == null ||
                m_interactionHost == null)
            {
                SendOperationResult(clientId, request?.RequestId ?? 0, false,
                    "invalid-circuit-operation", request == null ? null :
                        GetAuthoritativeCell(request.X, request.Y, request.Z), request);
                return;
            }
            PeerState peer = GetPeerState(clientId);
            if (peer.OperationResults.TryGetValue(request.RequestId,
                    out CircuitOperationResult cachedResult))
            {
                SendTo(clientId, MessageType.CircuitOperationResult, cachedResult.ToByteArray());
                return;
            }
            if (peer.PendingOperationIds.Contains(request.RequestId)) return;
            long nowMs = (long)(m_clock() * 1000.0);
            while (peer.OperationTimes.Count > 0 && nowMs - peer.OperationTimes.Peek() >= 1000)
                peer.OperationTimes.Dequeue();
            if (peer.OperationTimes.Count >= 32)
            {
                SendOperationResult(clientId, request.RequestId, false,
                    "circuit-operation-rate-limit",
                    GetAuthoritativeCell(request.X, request.Y, request.Z), request);
                return;
            }
            peer.OperationTimes.Enqueue(nowMs);
            if (request.ClientCircuitStep < 0 || request.ClientCircuitStep >
                (long)m_lastCircuitStep + MaximumCircuitRttMs / CircuitStepDurationMs)
            {
                SendOperationResult(clientId, request.RequestId, false,
                    "circuit-client-step-out-of-range",
                    GetAuthoritativeCell(request.X, request.Y, request.Z), request);
                return;
            }
            int queuedHostOperations = m_hostOperationsByStep.Values.Sum(list => list.Count);
            if (queuedHostOperations >= MaximumPendingOperations)
            {
                SendOperationResult(clientId, request.RequestId, false,
                    "circuit-operation-rate-limit",
                    GetAuthoritativeCell(request.X, request.Y, request.Z), request);
                return;
            }
            if (!m_world.TryGetClock(out int hostStep, out _, out _, out bool hostPaused,
                    out string hostClockError))
            {
                SendOperationResult(clientId, request.RequestId, false,
                    "host-circuit-clock-unavailable: " + hostClockError,
                    GetAuthoritativeCell(request.X, request.Y, request.Z), request);
                return;
            }
            if (hostPaused)
            {
                SendOperationResult(clientId, request.RequestId, false,
                    "circuit-paused", GetAuthoritativeCell(request.X,
                        request.Y, request.Z), request);
                return;
            }
            if (hostStep == int.MaxValue)
            {
                SendOperationResult(clientId, request.RequestId, false,
                    "host-circuit-step-exhausted",
                    GetAuthoritativeCell(request.X, request.Y, request.Z), request);
                return;
            }
            CircuitInteractionKind kind = (CircuitInteractionKind)request.Operation;
            var data = new CircuitInteractionRequestData(unchecked((long)request.RequestId), kind,
                request.X, request.Y, request.Z, (int)request.MountingFace,
                unchecked((int)request.Value), request.RayX, request.RayY, request.RayZ,
                request.DirectionX, request.DirectionY, request.DirectionZ, request.Reach);
            if (!m_interactionHost.TryBuildHostUseRequest(clientId, in data,
                    out BlockUseRequestData useRequest, out string reason))
            {
                SendOperationResult(clientId, request.RequestId, false, reason,
                    GetAuthoritativeCell(request.X, request.Y, request.Z), request);
                return;
            }
            int rttMs = request.RoundTripMs;
            if ((rttMs < 0 && m_transport.LastRoundTripMs >= 0) ||
                rttMs > MaximumCircuitRttMs)
            {
                SendOperationResult(clientId, request.RequestId, false,
                    "circuit-rtt-out-of-range",
                    GetAuthoritativeCell(request.X, request.Y, request.Z), request);
                return;
            }
            if (rttMs < 0) rttMs = 0;
            long targetStepValue = (long)hostStep + Math.Max(2,
                (rttMs + OperationStepMarginMs + CircuitStepDurationMs - 1) /
                    CircuitStepDurationMs);
            targetStepValue = Math.Max(targetStepValue, m_lastScheduledOperationStep);
            if (targetStepValue > int.MaxValue)
            {
                SendOperationResult(clientId, request.RequestId, false,
                    "host-circuit-step-exhausted",
                    GetAuthoritativeCell(request.X, request.Y, request.Z), request);
                return;
            }
            int targetStep = (int)targetStepValue;
            request = request.Clone();
            uint sequence = m_nextOperationSequence == uint.MaxValue
                ? 1 : m_nextOperationSequence + 1;
            var circuitEvent = new CircuitEvent
            {
                OperationSequence = sequence,
                HostCircuitStep = targetStep,
                X = request.X,
                Y = request.Y,
                Z = request.Z,
                MountingFace = request.MountingFace,
                Operation = request.Operation,
                RequiredTerrainSequence = CurrentHostTopologyTerrainSequence(
                    new ChunkKey(request.X >> 4, request.Z >> 4)),
                RequiredTopologyGeneration = CurrentHostTopologyGeneration(
                    new ChunkKey(request.X >> 4, request.Z >> 4)),
                OriginClientId = unchecked((uint)clientId),
                RequestId = request.RequestId,
                TimelineGeneration = m_timelineGeneration,
                Committed = false,
                CommittedEventSequence = 0
            };
            m_nextOperationSequence = sequence;
            var operation = new PendingHostOperation
            {
                ClientId = clientId,
                HostStep = targetStep,
                TerrainSequenceBeforeApply = m_transport.TerrainSequenceForChunk(
                    new ChunkKey(request.X >> 4, request.Z >> 4)),
                Request = request,
                UseRequest = useRequest,
                Event = circuitEvent
            };
            if (!m_hostOperationsByStep.TryGetValue(targetStep,
                    out List<PendingHostOperation> operations))
            {
                operations = new List<PendingHostOperation>();
                m_hostOperationsByStep.Add(targetStep, operations);
            }
            operations.Add(operation);
            peer.PendingOperationIds.Add(request.RequestId);
            if (!BroadcastScheduledOperation(operation))
            {
                operations.Remove(operation);
                if (operations.Count == 0) m_hostOperationsByStep.Remove(targetStep);
                peer.PendingOperationIds.Remove(request.RequestId);
                operation.Event.Cancelled = true;
                if (BroadcastCircuitEvent(operation.ClientId, operation.Event))
                    GetPeerState(operation.ClientId).PendingOperationIds.Remove(
                        operation.Request.RequestId);
                SendOperationResult(clientId, request.RequestId, false,
                    "circuit-operation-schedule-broadcast-failed",
                    GetAuthoritativeCell(request.X, request.Y, request.Z), request);
            }
            else
            {
                m_lastScheduledOperationStep = targetStep;
                SendOperationResult(clientId, request.RequestId, true,
                    "circuit-operation-scheduled",
                    GetAuthoritativeCell(request.X, request.Y, request.Z), request,
                    sequence, targetStep);
            }
        }

        private bool BroadcastScheduledOperation(PendingHostOperation operation)
        {
            return BroadcastCircuitEvent(operation.ClientId, operation.Event);
        }

        private bool BroadcastCommittedOperation(PendingHostOperation operation)
        {
            return BroadcastCircuitEvent(operation.ClientId, operation.Event);
        }

        // Source: Mod/SuMultiplayer/Engine/ClientReplicaSubsystems.cs:SuSubsystemElectricity.Update
        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
        private static int CompareOperationSequence(uint left, uint right) =>
            unchecked((int)(left - right));

        // Source: Mod/SuMultiplayer/Engine/ClientReplicaSubsystems.cs:SuSubsystemElectricity.Update
        private bool ProcessHostOperationsBeforeStep(int nextStep)
        {
            if (m_transport?.IsHosting != true || CircuitSimulationPauseSignal.IsPaused)
                return m_transport?.IsHosting != true;
            while (m_hostOperationsByStep.Count > 0)
            {
                KeyValuePair<int, List<PendingHostOperation>> pending =
                    m_hostOperationsByStep.First();
                if (pending.Key > nextStep) break;
                if (pending.Key < nextStep)
                {
                    m_hostOperationsByStep.Remove(pending.Key);
                    foreach (PendingHostOperation missed in pending.Value)
                    {
                        BroadcastCancelledOperation(missed);
                        GetPeerState(missed.ClientId).PendingOperationIds.Remove(
                            missed.Request.RequestId);
                        SendOperationResult(missed.ClientId,
                            missed.Request.RequestId, false,
                            "host-circuit-step-missed",
                            GetAuthoritativeCell(missed.Request.X,
                                missed.Request.Y, missed.Request.Z), missed.Request);
                    }
                    continue;
                }
                pending.Value.Sort((left, right) =>
                    CompareOperationSequence(left.Event.OperationSequence,
                        right.Event.OperationSequence));
                for (int i = 0; i < pending.Value.Count;)
                {
                    PendingHostOperation operation = pending.Value[i];
                    if (!m_transport.IsHostCircuitAttached(operation.ClientId))
                    {
                        GetPeerState(operation.ClientId).PendingOperationIds.Remove(
                            operation.Request.RequestId);
                        pending.Value.RemoveAt(i);
                        continue;
                    }
                    if (!operation.HostUseExecuted)
                    {
                        operation.TerrainSequenceBeforeApply =
                            m_transport.TerrainSequenceForChunk(new ChunkKey(
                                operation.Request.X >> 4, operation.Request.Z >> 4));
                        if (!m_useAuthority.TryExecuteCircuitUse(operation.ClientId,
                                in operation.UseRequest, out string reason))
                        {
                            BroadcastCancelledOperation(operation);
                            GetPeerState(operation.ClientId).PendingOperationIds.Remove(
                                operation.Request.RequestId);
                            SendOperationResult(operation.ClientId, operation.Request.RequestId,
                                false, reason, GetAuthoritativeCell(operation.Request.X,
                                    operation.Request.Y, operation.Request.Z), operation.Request);
                            pending.Value.RemoveAt(i);
                            continue;
                        }
                        operation.HostUseExecuted = true;
                        if (!m_world.TryGetCellValue(operation.Request.X, operation.Request.Y,
                                operation.Request.Z, out int cellValue))
                        {
                            Warn("Host accepted a circuit operation but could not read its authoritative cell.");
                            BroadcastCancelledOperation(operation);
                            SendOperationResult(operation.ClientId, operation.Request.RequestId,
                                false, "authoritative-cell-unavailable-after-use", null,
                                operation.Request);
                            GetPeerState(operation.ClientId).PendingOperationIds.Remove(
                                operation.Request.RequestId);
                            QueueChunkRecoveryForPeers(operation.Request.X >> 4,
                                operation.Request.Z >> 4);
                            pending.Value.RemoveAt(i);
                            continue;
                        }
                        operation.AuthoritativeCellValue = cellValue;
                        operation.WaitingForTerrainPublication = cellValue !=
                            unchecked((int)operation.Request.Value);
                        if (operation.WaitingForTerrainPublication)
                            m_committedOperationsWaitingTerrain.Add(operation);
                    }
                    if (operation.WaitingForTerrainPublication)
                    {
                        ChunkKey chunk = new ChunkKey(operation.Request.X >> 4,
                            operation.Request.Z >> 4);
                        if (m_transport.TerrainSequenceForChunk(chunk) <=
                            operation.TerrainSequenceBeforeApply)
                            return false;
                    }
                    if (!CommitHostOperation(operation)) return false;
                    m_committedOperationsWaitingTerrain.Remove(operation);
                    pending.Value.RemoveAt(i);
                }
                m_hostOperationsByStep.Remove(pending.Key);
            }
            return true;
        }

        private bool CommitHostOperation(PendingHostOperation operation)
        {
            ChunkKey chunk = new ChunkKey(operation.Request.X >> 4,
                operation.Request.Z >> 4);
            uint committedEventSequence = m_nextCommittedEventSequence == uint.MaxValue
                ? 1u : m_nextCommittedEventSequence + 1u;
            operation.Event.Value = unchecked((uint)operation.AuthoritativeCellValue);
            operation.Event.Committed = true;
            operation.Event.CommittedEventSequence = committedEventSequence;
            operation.Event.HostCircuitStep = operation.HostStep;
            operation.Event.RequiredTerrainSequence =
                CurrentHostTopologyTerrainSequence(chunk);
            operation.Event.RequiredTopologyGeneration =
                CurrentHostTopologyGeneration(chunk);
            if (!BroadcastCommittedOperation(operation))
            {
                QueueChunkRecoveryForPeers(chunk.X, chunk.Z);
                return false;
            }
            m_nextCommittedEventSequence = committedEventSequence;
            m_hostChunkCommittedEventSequences[chunk] = committedEventSequence;
            SendOperationResult(operation.ClientId, operation.Request.RequestId,
                true, string.Empty, operation.AuthoritativeCellValue,
                operation.Request, operation.Event.OperationSequence, operation.HostStep);
            GetPeerState(operation.ClientId).PendingOperationIds.Remove(
                operation.Request.RequestId);
            return true;
        }

        private bool BroadcastCircuitEvent(int originClientId, CircuitEvent circuitEvent)
        {
            CircuitEvent published = circuitEvent.Clone();
            var eventBatch = new CircuitEventBatch
            {
                Epoch = m_epoch,
                HostCircuitStep = published.HostCircuitStep,
                FirstOperationSequence = published.OperationSequence
            };
            eventBatch.Events.Add(published);
            var batch = new CircuitSyncBatch
            {
                Epoch = m_epoch,
                HostCircuitStep = published.HostCircuitStep
            };
            AddPayload(batch, MessageType.CircuitEventBatch, eventBatch);
            m_transport.CopyAttachedPeerIds(m_peers);
            ChunkKey eventChunk = new ChunkKey(published.X >> 4,
                published.Z >> 4);
            bool originSent = false;
            foreach (int peerId in m_peers)
            {
                if (peerId <= 0 || !m_transport.IsHostCircuitAttached(peerId)) continue;
                m_chunks.Clear();
                int scopeCount = m_transport.CopyClientChunkScope(peerId, m_chunks);
                if (scopeCount < 0 || scopeCount > MaximumInterestChunks ||
                    (peerId != originClientId && !m_chunks.Contains(eventChunk))) continue;
                if (!SendBatch(peerId, batch))
                {
                    m_hostSendFailures++;
                    if (m_peerStates.TryGetValue(peerId, out PeerState failedPeer) &&
                        failedPeer.CircuitScope.Contains(eventChunk))
                        MarkNeedsFullSnapshot(failedPeer, eventChunk,
                            SnapshotReason.SendFailureRecovery);
                    continue;
                }
                if (peerId == originClientId) originSent = true;
            }
            circuitEvent.OperationSequence = published.OperationSequence;
            return originSent;
        }

        private void BroadcastCancelledOperation(PendingHostOperation operation)
        {
            operation.Event.Cancelled = true;
            if (!BroadcastCircuitEvent(operation.ClientId, operation.Event))
                QueueChunkRecoveryForPeers(operation.Request.X >> 4,
                    operation.Request.Z >> 4);
            GetPeerState(operation.ClientId).PendingOperationIds.Remove(
                operation.Request.RequestId);
        }

        private void QueueChunkRecoveryForPeers(int chunkX, int chunkZ)
        {
            var chunk = new ChunkKey(chunkX, chunkZ);
            foreach (PeerState peer in m_peerStates.Values)
                if (peer.CircuitScope.Contains(chunk))
                    MarkNeedsFullSnapshot(peer, chunk, SnapshotReason.NeedsFullRecovery);
        }

        private void SendOperationResult(int clientId, ulong requestId, bool accepted,
            string reason, int? cellValue, CircuitEventRequest request = null,
            uint sequence = 0, int? hostStep = null)
        {
            ChunkKey operationChunk = request == null
                ? default : new ChunkKey(request.X >> 4, request.Z >> 4);
            var result = new CircuitOperationResult
            {
                RequestId = requestId,
                Accepted = accepted,
                Reason = reason ?? string.Empty,
                OperationSequence = sequence,
                HostCircuitStep = hostStep ?? m_lastCircuitStep,
                Epoch = m_epoch,
                RequiredTerrainSequence = request == null
                    ? 0 : CurrentHostTopologyTerrainSequence(operationChunk),
                RequiredTopologyGeneration = request == null
                    ? 0 : CurrentHostTopologyGeneration(operationChunk),
                TimelineGeneration = m_timelineGeneration
            };
            if (request != null)
            {
                result.X = request.X;
                result.Y = request.Y;
                result.Z = request.Z;
            }
            result.HasCellValue = cellValue.HasValue;
            if (cellValue.HasValue) result.CellValue = unchecked((uint)cellValue.Value);
            if (requestId != 0)
            {
                PeerState peer = GetPeerState(clientId);
                if (!peer.OperationResults.ContainsKey(requestId))
                    peer.OperationResultOrder.Enqueue(requestId);
                peer.OperationResults[requestId] = result;
                while (peer.OperationResultOrder.Count > 256)
                    peer.OperationResults.Remove(peer.OperationResultOrder.Dequeue());
            }
            if (m_transport?.IsHosting == true)
            {
                var batch = new CircuitSyncBatch
                {
                    Epoch = m_epoch,
                    HostCircuitStep = result.HostCircuitStep
                };
                AddPayload(batch, MessageType.CircuitOperationResult, result);
                if (!SendBatch(clientId, batch))
                    Warn("Circuit operation result could not be queued for client " +
                        clientId + " request " + requestId + ".");
            }
            else
                SendTo(clientId, MessageType.CircuitOperationResult, result.ToByteArray());
        }

        private int? GetAuthoritativeCell(int x, int y, int z) =>
            m_world.TryGetCellValue(x, y, z, out int value) ? value : (int?)null;

        private void RegisterSnapshotBoundary(PeerState peer, ulong snapshotId, ChunkKey chunk,
            int step, long terrainSequence, long topologyGeneration, uint hash,
            bool full, uint eventSequence, uint chunkEventSequence,
            SnapshotReason? reason, int stateCount)
        {
            var boundary = new HashBoundary(step, terrainSequence, topologyGeneration,
                eventSequence,
                m_timelineGeneration, hash);
            peer.SnapshotBoundaries[snapshotId] = new SnapshotBoundary(chunk, boundary,
                full, chunkEventSequence, reason, stateCount);
            while (peer.SnapshotBoundaries.Count > 64)
                peer.SnapshotBoundaries.Remove(peer.SnapshotBoundaries.Keys.First());
        }

        private static void ClearPauseBarrier(PeerState peer)
        {
            foreach (ulong snapshotId in peer.PauseBarrierSnapshots.Keys.ToArray())
                peer.SnapshotBoundaries.Remove(snapshotId);
            peer.PauseBarrierActive = false;
            peer.PauseBarrierComplete = false;
            peer.PauseBarrierChunks.Clear();
            peer.PauseBarrierApplied.Clear();
            peer.PauseBarrierSnapshots.Clear();
            peer.PauseBarrierSnapshotTimes.Clear();
        }

        private void UpdatePauseBarrier(PeerState peer, IList<ChunkKey> scope,
            int step, int timelineGeneration, long nowMs)
        {
            if (!peer.PauseBarrierActive || peer.PauseBarrierStep != step ||
                peer.PauseBarrierTimelineGeneration != timelineGeneration)
            {
                ClearPauseBarrier(peer);
                peer.PauseBarrierActive = true;
                peer.PauseBarrierStep = step;
                peer.PauseBarrierTimelineGeneration = timelineGeneration;
            }

            var visible = new HashSet<ChunkKey>(scope);
            foreach (ChunkKey old in peer.PauseBarrierChunks
                .Where(key => !visible.Contains(key)).ToArray())
            {
                peer.PauseBarrierChunks.Remove(old);
                peer.PauseBarrierApplied.Remove(old);
                ClearNeedsFullSnapshot(peer, old);
            }
            foreach (ulong snapshotId in peer.PauseBarrierSnapshots
                .Where(item => !visible.Contains(item.Value)).Select(item => item.Key).ToArray())
            {
                peer.PauseBarrierSnapshots.Remove(snapshotId);
                peer.PauseBarrierSnapshotTimes.Remove(snapshotId);
                peer.SnapshotBoundaries.Remove(snapshotId);
            }
            foreach (ChunkKey chunk in visible)
            {
                if (peer.PauseBarrierChunks.Add(chunk))
                {
                    peer.PauseBarrierApplied.Remove(chunk);
                    MarkNeedsFullSnapshot(peer, chunk, SnapshotReason.PauseBarrier);
                }
            }
            peer.PauseBarrierComplete = false;
        }

        private static void TrackPauseBarrierSnapshot(PeerState peer, ChunkKey chunk,
            int hostStep, ulong snapshotId, long nowMs)
        {
            if (!peer.PauseBarrierActive || peer.PauseBarrierStep != hostStep ||
                !peer.PauseBarrierChunks.Contains(chunk)) return;
            peer.PauseBarrierSnapshots[snapshotId] = chunk;
            peer.PauseBarrierSnapshotTimes[snapshotId] = nowMs;
            peer.PauseBarrierApplied.Remove(chunk);
        }

        private static bool HasPauseBarrierSnapshotForChunk(PeerState peer, ChunkKey chunk) =>
            peer.PauseBarrierSnapshots.Values.Any(value => value.Equals(chunk));

        private static void RemovePauseBarrierSnapshot(PeerState peer, ulong snapshotId,
            ChunkKey chunk)
        {
            if (!peer.PauseBarrierSnapshots.Remove(snapshotId)) return;
            peer.PauseBarrierSnapshotTimes.Remove(snapshotId);
            peer.PauseBarrierApplied.Remove(chunk);
        }

        private void ExpirePauseBarrierSnapshots(PeerState peer, long nowMs)
        {
            foreach (ulong snapshotId in peer.PauseBarrierSnapshotTimes
                .Where(item => nowMs - item.Value >= SnapshotTimeoutMs)
                .Select(item => item.Key).ToArray())
            {
                if (peer.PauseBarrierSnapshots.TryGetValue(snapshotId, out ChunkKey chunk))
                {
                    MarkNeedsFullSnapshot(peer, chunk, SnapshotReason.PauseBarrier);
                    peer.PauseBarrierApplied.Remove(chunk);
                }
                peer.PauseBarrierSnapshots.Remove(snapshotId);
                peer.PauseBarrierSnapshotTimes.Remove(snapshotId);
                peer.SnapshotBoundaries.Remove(snapshotId);
            }
        }

        private static bool IsPauseBarrierComplete(PeerState peer, IList<ChunkKey> scope)
        {
            if (!peer.PauseBarrierActive || peer.PauseBarrierSnapshots.Count != 0)
                return false;
            var visible = new HashSet<ChunkKey>(scope);
            return peer.PauseBarrierChunks.IsSubsetOf(visible) &&
                peer.PauseBarrierApplied.SetEquals(peer.PauseBarrierChunks) &&
                !peer.PauseBarrierChunks.Any(chunk => peer.NeedsFullSnapshot.Contains(chunk));
        }

        private void PrunePeerScope(PeerState peer, List<ChunkKey> scope)
        {
            var visible = new HashSet<ChunkKey>(scope);
            var tracked = new HashSet<ChunkKey>(peer.LastStates.Keys);
            tracked.UnionWith(peer.LastElementTypes.Keys);
            tracked.UnionWith(peer.NeedsFullSnapshot);
            tracked.UnionWith(peer.RepairRequests);
            foreach (ChunkKey key in tracked.Where(key => !visible.Contains(key)).ToArray())
            {
                peer.LastStates.Remove(key);
                peer.LastElementTypes.Remove(key);
                ClearNeedsFullSnapshot(peer, key);
                peer.RepairRequests.Remove(key);
                peer.RepairRequestReasons.Remove(key);
            }
            foreach (ulong id in peer.SnapshotBoundaries
                .Where(item => !visible.Contains(item.Value.Chunk)).Select(item => item.Key).ToArray())
                peer.SnapshotBoundaries.Remove(id);
        }

        private void HandleSnapshotRequest(int clientId, CircuitSnapshotRequest request)
        {
            if (request == null || request.Epoch != m_epoch || request.Chunks.Count > MaximumInterestChunks)
                return;
            var scope = new HashSet<ChunkKey>();
            m_chunks.Clear();
            int scopeCount = m_transport.CopyClientChunkScope(clientId, m_chunks);
            if (scopeCount < 0 || scopeCount > MaximumInterestChunks) return;
            foreach (CircuitChunkKey encoded in request.Chunks)
            {
                var key = new ChunkKey(encoded.X, encoded.Z);
                if (m_chunks.Contains(key)) scope.Add(key);
            }
            PeerState peer = GetPeerState(clientId);
            if (request.ScopeQuery)
            {
                RefreshPeerCircuitScope(clientId, peer, scope, request.RequestId);
                return;
            }
            if (!m_world.TryGetClock(out int snapshotStep, out _, out _, out _,
                    out string snapshotClockError))
            {
                Warn("Requested circuit snapshot has no authoritative step: " +
                    snapshotClockError);
                return;
            }
            SnapshotReason requestReason = request.HashMismatch
                ? SnapshotReason.HashMismatch : SnapshotReason.ClientRequest;
            if (request.HashMismatch)
            {
                foreach (ChunkKey key in scope)
                {
                    if (!peer.CircuitScope.Contains(key)) continue;
                    m_hostHashMismatchesObserved++;
                    m_hostHashRepairChunksQueued++;
                }
            }
            var requestedSequence = new Dictionary<ChunkKey, long>();
            var requestedGeneration = new Dictionary<ChunkKey, long>();
            foreach (ChunkKey key in scope)
            {
                requestedSequence[key] = CurrentHostTopologyTerrainSequence(key);
                requestedGeneration[key] = CurrentHostTopologyGeneration(key);
            }
            m_hostSnapshotRequestChunks += scope.Count;
            m_chunks.Clear();
            m_chunks.AddRange(scope);
            if (m_chunks.Count > 0 && !m_world.TryCaptureChunksBestEffort(m_chunks,
                    m_captureStatesByChunk, m_captureErrorsByChunk,
                    out string requestedCaptureError))
            {
                m_hostRequestedSnapshotCaptureFailures += m_chunks.Count;
                foreach (ChunkKey key in m_chunks)
                    MarkNeedsFullSnapshot(peer, key, requestReason);
                Warn("Requested circuit snapshot table capture failed for " + clientId +
                    "; " + m_chunks.Count + " chunk(s) need retry: " + requestedCaptureError);
                return;
            }
            if (m_captureErrorsByChunk.Count > 0)
            {
                m_hostRequestedSnapshotCaptureFailures += m_captureErrorsByChunk.Count;
                foreach (KeyValuePair<ChunkKey, string> failed in m_captureErrorsByChunk)
                    MarkNeedsFullSnapshot(peer, failed.Key, requestReason);
                KeyValuePair<ChunkKey, string> firstFailure = m_captureErrorsByChunk.First();
                Warn("Requested circuit snapshot capture skipped " +
                    m_captureErrorsByChunk.Count + " chunk(s); first failure at " +
                    firstFailure.Key + ": " + firstFailure.Value);
            }
            foreach (ChunkKey key in m_chunks)
            {
                if (m_captureErrorsByChunk.ContainsKey(key)) continue;
                if (!m_captureStatesByChunk.TryGetValue(key,
                        out List<CircuitElementState> captured))
                {
                    m_hostRequestedSnapshotCaptureFailures++;
                    Warn("Requested circuit snapshot capture omitted " + key + ".");
                    continue;
                }
                if (request.HashMismatch && !peer.CircuitScope.Contains(key)) continue;
                if (SendSnapshot(clientId, peer, key, snapshotStep,
                        requestedSequence[key], requestedGeneration[key], captured,
                        requestReason,
                        out ulong snapshotId))
                {
                    RememberStates(peer, key, captured);
                    ClearNeedsFullSnapshot(peer, key);
                    peer.RepairRequests.Remove(key);
                    peer.RepairRequestReasons.Remove(key);
                    TrackPauseBarrierSnapshot(peer, key, snapshotStep, snapshotId,
                        (long)(m_clock() * 1000.0));
                }
                else
                    MarkNeedsFullSnapshot(peer, key, SnapshotReason.SendFailureRecovery);
            }
            if (request.ReplaceScope)
            {
                foreach (ChunkKey old in peer.LastStates.Keys.Where(key => !scope.Contains(key)).ToArray())
                {
                    peer.LastStates.Remove(old);
                    peer.LastElementTypes.Remove(old);
                    ClearNeedsFullSnapshot(peer, old);
                    peer.RepairRequests.Remove(old);
                    peer.RepairRequestReasons.Remove(old);
                }
            }
        }

        private void RefreshPeerCircuitScope(int clientId, PeerState peer,
            HashSet<ChunkKey> terrainScope, ulong requestId)
        {
            m_chunks.Clear();
            foreach (ChunkKey key in terrainScope) m_chunks.Add(key);
            if (m_chunks.Count > 0 && !m_world.TryCaptureChunksBestEffort(m_chunks,
                    m_captureStatesByChunk, m_captureErrorsByChunk,
                    out string captureError))
            {
                m_hostRequestedSnapshotCaptureFailures += m_chunks.Count;
                Warn("Circuit scope query capture failed for client " + clientId + ": " + captureError);
                return;
            }
            if (m_captureErrorsByChunk.Count > 0)
            {
                Warn("Circuit scope query skipped " + m_captureErrorsByChunk.Count +
                    " chunk(s) because their circuit state could not be captured.");
                return;
            }

            var nextScope = new HashSet<ChunkKey>();
            foreach (ChunkKey key in terrainScope)
                if (m_captureStatesByChunk.TryGetValue(key, out List<CircuitElementState> states) &&
                    states.Count > 0)
                    nextScope.Add(key);
            foreach (ChunkKey key in terrainScope)
            {
                bool hasCircuit = nextScope.Contains(key);
                bool wasCircuit = m_hostCircuitChunks.Contains(key);
                if (hasCircuit == wasCircuit)
                {
                    if (hasCircuit) m_hostCircuitChunks.Add(key);
                    else m_hostCircuitChunks.Remove(key);
                }
                else
                    m_hostTopologyChangedChunks.Add(key);
                if (!m_hostTopologyGenerations.ContainsKey(key))
                    m_hostTopologyGenerations[key] = hasCircuit ? 1L : 0L;
                if (hasCircuit)
                    m_hostTopologyTerrainSequences[key] = Math.Max(
                        m_hostTopologyTerrainSequences.TryGetValue(key, out long oldSequence)
                            ? oldSequence : 0L,
                        m_transport.TerrainSequenceForChunk(key));
                else if (!m_hostTopologyTerrainSequences.ContainsKey(key))
                    m_hostTopologyTerrainSequences[key] = 0L;
                if (hasCircuit && m_captureStatesByChunk.TryGetValue(key,
                        out List<CircuitElementState> baseline))
                    CaptureCircuitCellValues(key, baseline, m_hostCircuitCellValues);
                if (hasCircuit != wasCircuit)
                    m_hostTopologyChangedChunks.Add(key);
            }
            peer.PendingScopeTerrainChunks.Clear();
            peer.PendingScopeTerrainChunks.UnionWith(terrainScope);
            foreach (ChunkKey removed in peer.CircuitScope.Where(key =>
                    !nextScope.Contains(key)).ToArray())
            {
                if (terrainScope.Contains(removed) &&
                    m_captureStatesByChunk.TryGetValue(removed,
                        out List<CircuitElementState> emptyStates) && emptyStates.Count == 0)
                    SendSnapshot(clientId, peer, removed, m_lastCircuitStep,
                        CurrentHostTopologyTerrainSequence(removed),
                        CurrentHostTopologyGeneration(removed), emptyStates,
                        SnapshotReason.TopologyChange, out _);
                ForgetPeerChunk(peer, removed);
                peer.CircuitScope.Remove(removed);
            }
            foreach (ChunkKey added in nextScope.Where(key => !peer.CircuitScope.Contains(key)))
                MarkNeedsFullSnapshot(peer, added, SnapshotReason.InitialScope);

            var response = new CircuitScopeSnapshot
            {
                Epoch = m_epoch,
                RequestId = requestId,
                RequiredTerrainSequence = terrainScope.Count == 0 ? 0L :
                    terrainScope.Max(key => m_transport.TerrainSequenceForChunk(key))
            };
            foreach (ChunkKey key in terrainScope.OrderBy(key => key.X).ThenBy(key => key.Z))
            {
                var topology = new CircuitChunkTopology
                {
                    Key = new CircuitChunkKey { X = key.X, Z = key.Z },
                    Generation = m_hostTopologyGenerations[key],
                    TerrainSequence = CurrentHostTopologyTerrainSequence(key),
                    HasCircuit = nextScope.Contains(key)
                };
                response.Topologies.Add(topology);
                if (topology.HasCircuit)
                    response.Chunks.Add(topology.Key.Clone());
            }
            var batch = new CircuitSyncBatch { Epoch = m_epoch, HostCircuitStep = m_lastCircuitStep };
            AddPayload(batch, MessageType.CircuitScopeSnapshot, response);
            if (!SendBatch(clientId, batch))
            {
                Warn("Circuit scope response could not be queued for client " + clientId + ".");
                return;
            }
            peer.CircuitScope.Clear();
            peer.CircuitScope.UnionWith(nextScope);
            peer.PendingScopeRequestId = requestId;
            m_nextSyncMs = 0;
        }

        private void ProcessPendingScopeSnapshot()
        {
            if (m_pendingScopeSnapshot == null) return;
            CircuitScopeSnapshot snapshot = m_pendingScopeSnapshot;
            m_pendingScopeSnapshot = null;
            if (!ClientScopeTerrainReady(snapshot, out string terrainWaitReason))
            {
                m_pendingScopeSnapshot = snapshot;
                m_clientSimulationBlockReason = "scope-terrain-wait:" + terrainWaitReason;
                return;
            }
            var nextCircuitScope = new HashSet<ChunkKey>();
            var nextTerrainScope = new HashSet<ChunkKey>();
            foreach (CircuitChunkTopology topology in snapshot.Topologies)
            {
                if (topology.Key == null) continue;
                var key = new ChunkKey(topology.Key.X, topology.Key.Z);
                nextTerrainScope.Add(key);
                long translatedTopologySequence = RequiredClientTerrainSequence(topology.TerrainSequence);
                if (!m_clientTopologyDirtyChunks.Contains(key) ||
                    CurrentClientTopologyTerrainSequence(key) <= translatedTopologySequence)
                {
                    m_clientTopologyGenerations[key] = topology.Generation;
                    m_clientTopologyTerrainSequences[key] = translatedTopologySequence;
                }
                if (topology.HasCircuit) nextCircuitScope.Add(key);
            }
            m_chunks.Clear();
            m_chunks.AddRange(nextTerrainScope);
            if (m_chunks.Count > 0 && !m_world.TryCaptureChunksBestEffort(m_chunks,
                    m_captureStatesByChunk, m_captureErrorsByChunk, out string captureError))
            {
                Warn("Client circuit scope baseline capture failed: " + captureError);
                m_pendingScopeSnapshot = snapshot;
                return;
            }
            if (m_captureErrorsByChunk.Count > 0)
            {
                Warn("Client circuit scope baseline capture skipped " +
                    m_captureErrorsByChunk.Count + " chunk(s).");
                m_pendingScopeSnapshot = snapshot;
                return;
            }
            foreach (ChunkKey key in nextTerrainScope)
                if (m_captureStatesByChunk.TryGetValue(key,
                        out List<CircuitElementState> states))
                    CaptureCircuitCellValues(key, states, m_clientCircuitCellValues);
            PruneSnapshotsOutsideCircuitScope(nextCircuitScope);
            foreach (ChunkKey removed in m_clientCircuitScope.Where(key =>
                    !nextCircuitScope.Contains(key)).ToArray())
            {
                if (!nextTerrainScope.Contains(removed))
                    m_world.ClearAuthoritativeStatesForChunk(removed);
                m_clientInitializedScope.Remove(removed);
                m_clientChunkBoundaries.Remove(removed);
                m_clientChunkCommittedEventSequences.Remove(removed);
                m_clientRepairRequests.Remove(removed);
                m_clientEventTopologyResyncRequests.Remove(removed);
            }
            m_deferredEvents.RemoveAll(item =>
                !nextCircuitScope.Contains(new ChunkKey(item.X >> 4, item.Z >> 4)));
            if (snapshot.RequestId != 0)
            {
                m_clientRequestedScope.Clear();
                m_clientRequestedScope.UnionWith(nextTerrainScope);
                m_clientScopeAcknowledged = true;
                m_clientScopeRequested = true;
                m_clientPendingScopeRequestId = 0;
            }
            m_clientCircuitScope.Clear();
            m_clientCircuitScope.UnionWith(nextCircuitScope);
            m_clientDesiredCircuitScope.Clear();
            m_clientDesiredCircuitScope.UnionWith(nextCircuitScope);
            m_clientInitializedScope.IntersectWith(nextCircuitScope);
            RefreshClientStepBarrier();
            UpdateClientSimulationClock();
        }

        private bool ClientScopeTerrainReady(CircuitScopeSnapshot snapshot,
            out string waitReason)
        {
            waitReason = string.Empty;
            if (snapshot == null) return false;
            foreach (CircuitChunkTopology topology in snapshot.Topologies)
            {
                if (topology.Key == null) continue;
                var key = new ChunkKey(topology.Key.X, topology.Key.Z);
                long required = RequiredClientTerrainSequence(topology.TerrainSequence);
                long local = m_transport.TerrainSequenceForChunk(key);
                if (local >= required) continue;
                waitReason = key + " local=" + local + " required=" + required;
                return false;
            }
            return true;
        }

        private void ForgetPeerChunk(PeerState peer, ChunkKey key)
        {
            peer.LastStates.Remove(key);
            peer.LastElementTypes.Remove(key);
            ClearNeedsFullSnapshot(peer, key);
            peer.RepairRequests.Remove(key);
            peer.RepairRequestReasons.Remove(key);
        }

        private void HandleRepairApplied(int clientId, CircuitRepairApplied report)
        {
            if (report == null || report.Epoch != m_epoch) return;
            var key = new ChunkKey(report.ChunkX, report.ChunkZ);
            PeerState peer = GetPeerState(clientId);
            if (!peer.SnapshotBoundaries.TryGetValue(report.SnapshotId,
                    out SnapshotBoundary expected) || !expected.Chunk.Equals(key))
                return;
            HashBoundary boundary = expected.Boundary;
            if (boundary.TerrainSequence != report.TerrainSequence ||
                boundary.TopologyGeneration != report.TopologyGeneration ||
                expected.ChunkCommittedEventSequence !=
                    report.ChunkCommittedEventSequence ||
                boundary.CommittedEventSequence !=
                    unchecked((uint)report.LastCommittedEventSequence) ||
                boundary.TimelineGeneration != report.TimelineGeneration)
            {
                Warn("Circuit repair acknowledgment has a mismatched snapshot boundary.");
                return;
            }
            bool pauseSnapshot = peer.PauseBarrierSnapshots.TryGetValue(report.SnapshotId,
                out ChunkKey pauseChunk);
            if (pauseSnapshot && (!peer.PauseBarrierActive ||
                peer.PauseBarrierStep != boundary.Step ||
                peer.PauseBarrierTimelineGeneration != boundary.TimelineGeneration ||
                !peer.PauseBarrierChunks.Contains(key) || !pauseChunk.Equals(key)))
            {
                Warn("Circuit pause barrier acknowledgment is outside the active paused scope.");
                return;
            }
            if (boundary.Step != report.ClientCircuitStep ||
                expected.ChunkCommittedEventSequence !=
                    report.ChunkCommittedEventSequence)
            {
                peer.SnapshotBoundaries.Remove(report.SnapshotId);
                peer.PauseBarrierSnapshots.Remove(report.SnapshotId);
                peer.PauseBarrierSnapshotTimes.Remove(report.SnapshotId);
                m_hostRepairAckStepMismatches++;
                if (pauseSnapshot)
                {
                    MarkNeedsFullSnapshot(peer, key, SnapshotReason.RepairAckMismatch);
                    peer.PauseBarrierApplied.Remove(key);
                }
                return;
            }
            peer.SnapshotBoundaries.Remove(report.SnapshotId);
            peer.PauseBarrierSnapshots.Remove(report.SnapshotId);
            peer.PauseBarrierSnapshotTimes.Remove(report.SnapshotId);
            if (boundary.Hash != report.StateHash)
            {
                m_hostRepairAckHashMismatches++;
                LogCircuitHashDiagnostic("event=repair-ack-mismatch clientId=" + clientId +
                    " snapshotId=" + report.SnapshotId + " snapshotKind=" +
                    (expected.Full ? "full" : "delta") + " snapshotReason=" +
                    (expected.Reason.HasValue ? expected.Reason.Value.ToString() : "delta") +
                    " chunk=" + key + " hostStep=" + boundary.Step +
                    " terrain=" + boundary.TerrainSequence + " topology=" +
                    boundary.TopologyGeneration + " committedSequence=" +
                    boundary.CommittedEventSequence +
                    " timeline=" + boundary.TimelineGeneration + " states=" + expected.StateCount +
                    " hostHash=" + FormatHash(boundary.Hash) +
                    " clientHash=" + FormatHash(report.StateHash));
                if (pauseSnapshot)
                {
                    MarkNeedsFullSnapshot(peer, key, SnapshotReason.RepairAckMismatch);
                    peer.PauseBarrierApplied.Remove(key);
                }
                else
                    QueueRepair(peer, key, SnapshotReason.RepairAckMismatch);
            }
            else if (pauseSnapshot)
            {
                ClearNeedsFullSnapshot(peer, key);
                peer.PauseBarrierApplied.Add(key);
            }
        }

        private void HandleBatch(CircuitSyncBatch batch)
        {
            if (batch == null) return;
            if (!m_transport.IsHosting && m_epoch == 0)
                m_epoch = batch.Epoch;
            if (batch.Epoch != m_epoch) return;
            foreach (CircuitPayload payload in batch.Payloads)
            {
                MessageType type = (MessageType)payload.MessageType;
                switch (type)
                {
                    case MessageType.CircuitClock:
                        ApplyClock(CircuitClock.Parser.ParseFrom(payload.Payload));
                        break;
                    case MessageType.CircuitSnapshotPart:
                        QueueSnapshotPart(CircuitSnapshotPart.Parser.ParseFrom(payload.Payload));
                        break;
                    case MessageType.CircuitScopeSnapshot:
                        ApplyScopeSnapshot(CircuitScopeSnapshot.Parser.ParseFrom(payload.Payload));
                        break;
                    case MessageType.CircuitOperationResult:
                        ApplyOperationResult(CircuitOperationResult.Parser.ParseFrom(payload.Payload));
                        break;
                    case MessageType.CircuitRepairBarrier:
                        RegisterRepairBarrier(CircuitRepairBarrier.Parser.ParseFrom(payload.Payload));
                        break;
                    case MessageType.CircuitEventBatch:
                        ApplyEventBatch(CircuitEventBatch.Parser.ParseFrom(payload.Payload));
                        break;
                    case MessageType.CircuitHashCheckpoint:
                        QueueHashCheckpoint(CircuitHashCheckpoint.Parser.ParseFrom(
                            payload.Payload));
                        break;
                    default:
                        Warn("Client ignored unknown circuit batch payload " + payload.MessageType + ".");
                        break;
                }
            }
        }

        private void RegisterRepairBarrier(CircuitRepairBarrier barrier)
        {
            if (barrier == null || barrier.Epoch != m_epoch || barrier.PartCount == 0 ||
                barrier.PartCount > 4096) return;
            var chunk = new ChunkKey(barrier.ChunkX, barrier.ChunkZ);
            if (m_clientScopeAcknowledged && !m_clientCircuitScope.Contains(chunk) &&
                !string.Equals(barrier.Reason, "full", StringComparison.Ordinal)) return;
            ulong key = barrier.SnapshotId;
            if (!m_snapshotAssemblies.TryGetValue(key, out SnapshotAssembly assembly))
            {
                if (m_snapshotAssemblies.Count >= MaximumPendingSnapshots)
                    m_snapshotAssemblies.Remove(m_snapshotAssemblies.Keys.First());
                assembly = new SnapshotAssembly
                {
                    Epoch = barrier.Epoch,
                    SnapshotId = barrier.SnapshotId,
                    HostStep = barrier.HostCircuitStep,
                    ChunkX = barrier.ChunkX,
                    ChunkZ = barrier.ChunkZ,
                    PartCount = (int)barrier.PartCount
                };
                assembly.CreatedMs = (long)(m_clock() * 1000.0);
                assembly.Full = string.Equals(barrier.Reason, "full", StringComparison.Ordinal);
                m_snapshotAssemblies.Add(key, assembly);
            }
            else if (assembly.PartCount != (int)barrier.PartCount ||
                assembly.HostStep != barrier.HostCircuitStep ||
                assembly.ChunkX != barrier.ChunkX || assembly.ChunkZ != barrier.ChunkZ)
            {
                m_snapshotAssemblies.Remove(key);
                Warn("Circuit repair barrier metadata conflicts with pending snapshot parts.");
            }
            RefreshClientStepBarrier();
        }

        private void QueueSnapshotPart(CircuitSnapshotPart part)
        {
            if (part == null || part.Epoch != m_epoch || part.PartCount == 0 ||
                part.PartCount > 4096 || part.PartIndex >= part.PartCount) return;
            var chunk = new ChunkKey(part.ChunkX, part.ChunkZ);
            if (m_clientScopeAcknowledged && !m_clientCircuitScope.Contains(chunk) &&
                !part.Full) return;
            if (!m_snapshotAssemblies.TryGetValue(part.SnapshotId, out SnapshotAssembly assembly))
            {
                if (m_snapshotAssemblies.Count >= MaximumPendingSnapshots)
                    m_snapshotAssemblies.Remove(m_snapshotAssemblies.Keys.First());
                assembly = new SnapshotAssembly
                {
                    Epoch = part.Epoch,
                    SnapshotId = part.SnapshotId,
                    HostStep = part.HostCircuitStep,
                    ChunkX = part.ChunkX,
                    ChunkZ = part.ChunkZ,
                    PartCount = (int)part.PartCount,
                    LastCommittedEventSequence = part.LastCommittedEventSequence,
                    ChunkCommittedEventSequence = part.ChunkCommittedEventSequence,
                    TimelineGeneration = part.TimelineGeneration,
                    HasContext = true,
                    CreatedMs = (long)(m_clock() * 1000.0),
                    RequiredTerrainSequence = part.RequiredTerrainSequence,
                    RequiredTopologyGeneration = part.RequiredTopologyGeneration
                };
                assembly.Full = part.Full;
                m_snapshotAssemblies.Add(part.SnapshotId, assembly);
            }
            if (assembly.PartCount != (int)part.PartCount ||
                assembly.ChunkX != part.ChunkX || assembly.ChunkZ != part.ChunkZ ||
                assembly.HostStep != part.HostCircuitStep || assembly.Full != part.Full ||
                (assembly.HasContext && assembly.LastCommittedEventSequence !=
                    part.LastCommittedEventSequence) ||
                (assembly.HasContext && assembly.ChunkCommittedEventSequence !=
                    part.ChunkCommittedEventSequence) ||
                (assembly.HasContext && assembly.TimelineGeneration != part.TimelineGeneration) ||
                (assembly.HasContext && assembly.RequiredTerrainSequence != part.RequiredTerrainSequence) ||
                (assembly.HasContext && assembly.RequiredTopologyGeneration != part.RequiredTopologyGeneration) ||
                part.RequiredTerrainSequence < 0)
            {
                m_snapshotAssemblies.Remove(part.SnapshotId);
                m_clientScopeRequested = false;
                Warn("Circuit snapshot parts disagree on metadata.");
                return;
            }
            assembly.LastCommittedEventSequence = part.LastCommittedEventSequence;
            assembly.ChunkCommittedEventSequence = part.ChunkCommittedEventSequence;
            assembly.TimelineGeneration = part.TimelineGeneration;
            assembly.HasContext = true;
            assembly.RequiredTerrainSequence = part.RequiredTerrainSequence;
            assembly.RequiredTopologyGeneration = part.RequiredTopologyGeneration;
            assembly.Parts[(int)part.PartIndex] = part;
            m_clientSnapshotPartsReceived++;
            m_clientChunkBoundaries.Remove(new ChunkKey(part.ChunkX, part.ChunkZ));
            RefreshClientStepBarrier();
        }

        private void ProcessReadySnapshots()
        {
            long nowMs = (long)(m_clock() * 1000.0);
            bool displayRefreshNeeded = false;
            bool replayingAcceleratedState = m_clientClockAccelerated ||
                m_clientResumePending;
            foreach (SnapshotAssembly assembly in m_snapshotAssemblies.Values
                         .OrderBy(candidate => candidate.HostStep)
                         .ThenBy(candidate => candidate.SnapshotId).ToArray())
            {
                m_snapshotStates.Clear();
                ChunkKey key = new ChunkKey(assembly.ChunkX, assembly.ChunkZ);
                if (m_transport?.IsClient == true && !m_clientClockInitialized)
                    continue;
                if (m_transport?.IsClient == true && !replayingAcceleratedState)
                {
                    if (!m_world.TryGetClock(out int clientStep, out _, out _, out _, out _))
                        continue;
                    if (assembly.HostStep > clientStep && !replayingAcceleratedState) continue;
                    if (assembly.HostStep < clientStep && !replayingAcceleratedState)
                    {
                        m_snapshotAssemblies.Remove(assembly.SnapshotId);
                        if (!assembly.Full) m_clientStaleDeltasDiscarded++;
                        RequestSnapshotRecovery(key, assembly, "snapshot-step-expired");
                        continue;
                    }
                }
                if (assembly.Parts.Count != assembly.PartCount)
                {
                    if (nowMs - assembly.CreatedMs >= SnapshotTimeoutMs)
                    {
                        m_snapshotAssemblies.Remove(assembly.SnapshotId);
                        RequestSnapshotRecovery(key, assembly, "parts-timeout");
                        m_clientSnapshotTimeouts++;
                        Warn("Circuit snapshot timed out before its terrain barrier or all parts arrived.");
                    }
                    continue;
                }
                long requiredClientTerrainSequence = RequiredClientTerrainSequence(
                    assembly.RequiredTerrainSequence);
                if (m_transport.TerrainSequence < requiredClientTerrainSequence)
                {
                    if (IsSnapshotExpired(assembly, nowMs))
                    {
                        m_snapshotAssemblies.Remove(assembly.SnapshotId);
                        RequestSnapshotRecovery(key, assembly, "terrain-timeout");
                    }
                    continue;
                }
                // A full snapshot contains the authoritative topology and can still be
                // applied after the terrain stream has advanced: reconciliation below
                // verifies that the newer terrain has the same circuit topology. A delta
                // cannot make that guarantee, so it must be discarded and replaced by a
                // fresh full snapshot.
                long localGeneration = CurrentClientTopologyGeneration(key);
                if (localGeneration != assembly.RequiredTopologyGeneration)
                {
                    if (localGeneration > assembly.RequiredTopologyGeneration)
                    {
                        m_snapshotAssemblies.Remove(assembly.SnapshotId);
                        RequestSnapshotRecovery(key, assembly, "local-generation-newer");
                        m_clientWatermarkExpired++;
                        Warn("Circuit state crossed topology generation for " + key +
                            "; requested a chunk snapshot.");
                    }
                    else if (assembly.Full && m_clientCircuitScope.Contains(key) &&
                        assembly.TimelineGeneration == m_timelineGeneration &&
                        !m_clientTopologyDirtyChunks.Contains(key))
                    {
                        // A topology-complete full snapshot is authoritative for this chunk.
                    }
                    else if (!assembly.Full)
                    {
                        m_snapshotAssemblies.Remove(assembly.SnapshotId);
                        m_clientStaleDeltasDiscarded++;
                        RequestSnapshotRecovery(key, assembly, "delta-generation-mismatch");
                    }
                    else if (IsSnapshotExpired(assembly, nowMs))
                    {
                        m_snapshotAssemblies.Remove(assembly.SnapshotId);
                        RequestSnapshotRecovery(key, assembly, "generation-timeout");
                    }
                    else
                    {
                        continue;
                    }
                    if (!m_snapshotAssemblies.ContainsKey(assembly.SnapshotId)) continue;
                }
                bool applied = true;
                foreach (CircuitSnapshotPart part in assembly.Parts.Values)
                    m_snapshotStates.AddRange(part.Elements);
                // A join-time scope request can arrive before the target terrain chunk is
                // valid. Keep the complete snapshot queued until the terrain is ready instead
                // of counting this transient loading state as an element-apply failure.
                if (!m_world.IsChunkReady(key))
                {
                    if (IsSnapshotExpired(assembly, nowMs))
                    {
                        m_snapshotAssemblies.Remove(assembly.SnapshotId);
                        RequestSnapshotRecovery(key, assembly, "chunk-not-ready");
                    }
                    continue;
                }
                if (!m_world.AreElementsReady(m_snapshotStates))
                {
                    if (nowMs - m_lastClientElementRecoveryMs >= 250)
                    {
                        m_world.ResetClientCircuitState();
                        m_lastClientElementRecoveryMs = nowMs;
                    }
                    if (IsSnapshotExpired(assembly, nowMs))
                    {
                        m_snapshotAssemblies.Remove(assembly.SnapshotId);
                        RequestSnapshotRecovery(key, assembly, "elements-not-ready");
                    }
                    continue;
                }
                if (assembly.Full)
                {
                    if (!SnapshotTopologyMatches(key, m_snapshotStates,
                            out string topologyMismatch))
                    {
                        if (IsSnapshotExpired(assembly, nowMs))
                        {
                            m_snapshotAssemblies.Remove(assembly.SnapshotId);
                            RequestSnapshotRecovery(key, assembly,
                                "full-topology-mismatch:" + topologyMismatch);
                        }
                        continue;
                    }
                    m_probeTrigger = "full-snapshot-reconcile";
                    m_probeScopeChunks = 1;
                    m_probePendingStates = m_snapshotStates.Count;
                    long reconcileStarted = m_probe.Start();
                    string reconcileError = m_reconcileSnapshot?.Invoke(key, m_snapshotStates,
                        assembly.HostStep);
                    RecordProbe("snapshot-reconcile", reconcileStarted);
                    if (m_reconcileSnapshot == null || !string.IsNullOrEmpty(reconcileError))
                    {
                        Warn("Full circuit snapshot cannot reconcile chunk topology: " +
                            (m_reconcileSnapshot == null ? "world reconciliation port is missing." :
                                reconcileError));
                        applied = false;
                    }
                }
                else
                {
                    m_probeTrigger = "delta-snapshot-apply";
                    m_probeScopeChunks = 1;
                    m_probePendingStates = m_snapshotStates.Count;
                    long applyStarted = m_probe.Start();
                    foreach (CircuitElementState state in m_snapshotStates)
                    {
                        if (!m_world.Apply(state, assembly.HostStep, out string error))
                        {
                            Warn("Circuit state apply failed: " + error);
                            applied = false;
                            break;
                        }
                    }
                    RecordProbe("snapshot-apply", applyStarted);
                }
                bool captured = applied;
                uint appliedHash = 0;
                if (applied && assembly.Full)
                {
                    long captureStarted = m_probe.Start();
                    captured = m_world.TryCaptureChunk(key, m_states, out string captureError);
                    RecordProbe("snapshot-capture", captureStarted);
                    if (captured)
                    {
                        long ackHashStarted = m_probe.Start();
                        appliedHash = ComputeCircuitSyncHash(m_states);
                        RecordProbe("repair-ack-hash", ackHashStarted);
                    }
                    else
                        Warn("Circuit full snapshot acknowledgment capture failed: " + captureError);
                }
                else if (applied)
                {
                    long captureStarted = m_probe.Start();
                    captured = m_world.TryCaptureElements(m_snapshotStates,
                        m_deltaAckStates, out string deltaCaptureError);
                    RecordProbe("delta-ack-capture", captureStarted);
                    if (captured)
                    {
                        long ackHashStarted = m_probe.Start();
                        appliedHash = ComputeCircuitSyncHash(m_deltaAckStates);
                        RecordProbe("repair-ack-hash", ackHashStarted);
                    }
                    else
                        Warn("Circuit delta acknowledgment state capture failed: " +
                            deltaCaptureError);
                }
                if (applied && !captured) applied = false;
                int appliedCircuitStep = 0;
                if (applied && captured && !m_world.TryGetClock(out appliedCircuitStep,
                        out _, out _, out _, out string ackClockError))
                {
                    captured = false;
                    Warn("Circuit snapshot acknowledgment step capture failed: " +
                        ackClockError);
                }
                if (applied && !captured) applied = false;
                if (applied && captured)
                {
                    displayRefreshNeeded = true;
                    if (assembly.Full && CurrentClientTopologyGeneration(key) <
                        assembly.RequiredTopologyGeneration)
                    {
                        m_clientTopologyGenerations[key] = assembly.RequiredTopologyGeneration;
                        m_clientTopologyTerrainSequences[key] = assembly.RequiredTerrainSequence;
                        m_clientTopologyGenerationAlignments++;
                    }
                    var appliedMessage = new CircuitRepairApplied
                    {
                        Epoch = m_epoch,
                        SnapshotId = assembly.SnapshotId,
                        ChunkX = key.X,
                        ChunkZ = key.Z,
                        StateHash = appliedHash,
                        TerrainSequence = assembly.RequiredTerrainSequence,
                        TopologyGeneration = assembly.RequiredTopologyGeneration,
                        LastCommittedEventSequence = assembly.LastCommittedEventSequence,
                        TimelineGeneration = assembly.TimelineGeneration,
                        ClientCircuitStep = appliedCircuitStep,
                        ChunkCommittedEventSequence = assembly.ChunkCommittedEventSequence
                    };
                    SendHost(MessageType.CircuitRepairApplied, appliedMessage.ToByteArray());
                    m_clientChunkBoundaries[key] = new AppliedBoundary(assembly.HostStep,
                        assembly.RequiredTerrainSequence, assembly.RequiredTopologyGeneration,
                        assembly.LastCommittedEventSequence,
                        assembly.TimelineGeneration);
                    m_clientChunkCommittedEventSequences[key] =
                        assembly.ChunkCommittedEventSequence;
                    if (assembly.Full)
                    {
                        m_clientInitializedScope.Add(key);
                        m_clientRepairRequests.Remove(key);
                        foreach (SnapshotAssembly older in m_snapshotAssemblies.Values
                                     .Where(candidate => candidate.SnapshotId != assembly.SnapshotId &&
                                         candidate.ChunkX == key.X && candidate.ChunkZ == key.Z &&
                                         candidate.TimelineGeneration == assembly.TimelineGeneration &&
                                         candidate.HostStep <= assembly.HostStep &&
                                         candidate.RequiredTopologyGeneration <=
                                             assembly.RequiredTopologyGeneration).ToArray())
                            m_snapshotAssemblies.Remove(older.SnapshotId);
                    }
                    m_clientTopologyAwaitingSnapshot.Remove(key);
                    m_clientSnapshotsApplied++;
                    m_clientStatesApplied += m_snapshotStates.Count;
                    uint snapshotSequence = unchecked((uint)assembly.LastCommittedEventSequence);
                    uint currentEventSequence =
                        unchecked((uint)m_clientLastCommittedEventSequence);
                    if (IsNewerEventSequence(snapshotSequence, currentEventSequence))
                        m_clientLastCommittedEventSequence =
                            assembly.LastCommittedEventSequence;
                    for (int i = m_deferredEvents.Count - 1; i >= 0; i--)
                    {
                        CircuitEvent pendingEvent = m_deferredEvents[i];
                        if ((pendingEvent.X >> 4) != key.X ||
                            (pendingEvent.Z >> 4) != key.Z ||
                            pendingEvent.HostCircuitStep > assembly.HostStep ||
                            !pendingEvent.Cancelled &&
                            (!pendingEvent.Committed ||
                                IsNewerEventSequence(pendingEvent.CommittedEventSequence,
                                    assembly.ChunkCommittedEventSequence))) continue;
                        m_deferredEvents.RemoveAt(i);
                        if (pendingEvent.Cancelled)
                            RejectCancelledOperation(pendingEvent);
                        else if (pendingEvent.OriginClientId ==
                                     unchecked((uint)m_transport.LocalClientId) &&
                                 pendingEvent.RequestId != 0)
                        {
                            int authoritativeCell = m_world.GetCellValue(pendingEvent.X,
                                pendingEvent.Y, pendingEvent.Z);
                            m_pendingOperations.Remove(pendingEvent.RequestId);
                            m_presentation.ResolvePresentation(
                                unchecked((long)pendingEvent.RequestId), authoritativeCell);
                        }
                    }
                    if (m_lateCommittedOperations.TryGetValue(key,
                            out List<CircuitEvent> lateOperations))
                    {
                        for (int i = lateOperations.Count - 1; i >= 0; i--)
                        {
                            CircuitEvent lateOperation = lateOperations[i];
                            if (lateOperation.HostCircuitStep > assembly.HostStep ||
                                IsNewerEventSequence(lateOperation.CommittedEventSequence,
                                    assembly.ChunkCommittedEventSequence)) continue;
                            if (lateOperation.OriginClientId ==
                                    unchecked((uint)m_transport.LocalClientId) &&
                                lateOperation.RequestId != 0)
                            {
                                m_pendingOperations.Remove(lateOperation.RequestId);
                                m_presentation.ResolvePresentation(
                                    unchecked((long)lateOperation.RequestId),
                                    m_world.GetCellValue(lateOperation.X,
                                        lateOperation.Y, lateOperation.Z));
                            }
                            lateOperations.RemoveAt(i);
                        }
                        if (lateOperations.Count == 0)
                            m_lateCommittedOperations.Remove(key);
                        else if (assembly.Full)
                        {
                            m_clientRepairRequests.Remove(key);
                            RequestChunkSnapshot(key);
                        }
                    }
                }
                else if (!applied)
                {
                    m_clientApplyFailures++;
                    RequestChunkSnapshot(key);
                    Warn("Circuit snapshot apply or acknowledgment verification failed; requested a full chunk snapshot.");
                }
                m_snapshotAssemblies.Remove(assembly.SnapshotId);
            }
            if (displayRefreshNeeded)
                m_world.RefreshClientDisplayElements();
            UpdateClientSimulationClock();
        }

        private void ApplyClock(CircuitClock message)
        {
            if (message == null) return;
            bool wasClientCircuitStopped = m_clientClockPaused ||
                m_clientClockAccelerated;
            if (!m_transport.IsHosting && m_epoch == 0)
                m_epoch = message.Epoch;
            if (message.Epoch != m_epoch || message.TimelineGeneration < m_timelineGeneration ||
                (message.TimelineGeneration == m_timelineGeneration &&
                    message.HostCircuitStep < m_lastCircuitStep)) return;
            bool timelineChanged = message.TimelineGeneration > m_timelineGeneration;
            if (timelineChanged)
            {
                m_pendingOperations.Clear();
                m_deferredOperationResults.Clear();
                m_deferredEvents.Clear();
                m_snapshotAssemblies.Clear();
                m_clientChunkBoundaries.Clear();
                m_clientInitializedScope.Clear();
                m_clientHashCheckpoints.Clear();
                m_clientChunkCommittedEventSequences.Clear();
                m_clientLastCommittedEventSequence = 0;
                m_lateCommittedOperations.Clear();
                m_clientRepairRequests.Clear();
                m_clientEventTopologyResyncRequests.Clear();
                CircuitSimulationPauseSignal.ClearClientStepBarrier();
                m_clientClockInitialized = false;
                m_clientClockPaused = false;
                m_clientClockAccelerated = false;
                m_clientResumePending = false;
                CircuitSimulationPauseSignal.SetClientClock(false,
                    message.HostCircuitStep, message.Paused, message.Accelerated);
                m_presentation.DetachPresentation();
                m_clientScopeRequested = false;
            }
            if (!m_world.ApplyClock(message.TotalElapsedGameTime, message.TimeOfDayOffset,
                    out string error))
            {
                if (!m_transport.IsHosting)
                {
                    m_pendingClock = message.Clone();
                    Warn("Authoritative circuit clock deferred until the client world is ready: " + error);
                }
                else
                    Warn("Authoritative circuit clock apply failed: " + error);
                return;
            }
            if (m_transport.IsClient && message.Paused && !timelineChanged &&
                m_clientClockInitialized)
            {
                if (!m_world.TryGetClock(out int pausedClientStep, out _, out _, out _,
                        out string pausedClockError))
                {
                    Warn("Paused circuit clock step capture failed: " + pausedClockError);
                    return;
                }
                if (pausedClientStep < message.HostCircuitStep &&
                    !m_world.AlignClientCircuitStep(message.HostCircuitStep, false,
                        out string pausedAlignError))
                {
                    Warn("Paused client circuit step alignment failed: " + pausedAlignError);
                    return;
                }
            }
            if (m_transport.IsClient && (!m_clientClockInitialized || timelineChanged))
            {
                if (!m_world.AlignClientCircuitStep(message.HostCircuitStep, false,
                        out string alignError))
                {
                    Warn("Client circuit clock alignment failed: " + alignError);
                    return;
                }
                m_clientClockInitialized = true;
            }
            if (m_transport.IsClient)
            {
                m_clientClockPaused = message.Paused;
                m_clientClockAccelerated = message.Accelerated;
                if (!timelineChanged && wasClientCircuitStopped &&
                    !m_clientClockPaused && !m_clientClockAccelerated)
                    m_clientResumePending = true;
                CircuitSimulationPauseSignal.SetClientClock(false,
                    message.HostCircuitStep, message.Paused, message.Accelerated);
            }
            else
                CircuitSimulationPauseSignal.Set(message.Paused);
            m_lastCircuitStep = message.HostCircuitStep;
            m_timelineGeneration = message.TimelineGeneration;
            if (m_transport.IsClient && message.LastCommittedEventSequence >= 0 &&
                IsNewerEventSequence(unchecked((uint)message.LastCommittedEventSequence),
                    unchecked((uint)m_clientLastCommittedEventSequence)))
                m_clientLastCommittedEventSequence = message.LastCommittedEventSequence;
            if (!m_hasConfiguredRandomSeed || m_configuredRandomSeed != message.RandomSeed ||
                m_randomSeedAuthority)
            {
                m_world.SetDeterministicRandomSeed(message.RandomSeed, hostAuthority: false);
                m_configuredRandomSeed = message.RandomSeed;
                m_hasConfiguredRandomSeed = true;
                m_randomSeedAuthority = false;
            }
            RefreshClientStepBarrier();
            UpdateClientSimulationClock();
            // A clock only advances the host timeline. It does not advance the
            // state actually installed in any client chunk. Keep each chunk's
            // applied boundary unchanged until its snapshot or event has been
            // applied successfully; otherwise an old voltage would be reported
            // as a current host state and a paused host could be acknowledged
            // with a false hash.
        }

        private void ApplyScopeSnapshot(CircuitScopeSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Epoch != m_epoch ||
                (snapshot.RequestId != 0 && snapshot.RequestId != m_clientPendingScopeRequestId)) return;
            if (snapshot.RequestId != 0)
                m_clientScopeResponseReceived = true;
            m_pendingScopeSnapshot = snapshot.Clone();
            ProcessPendingScopeSnapshot();
        }

        private bool HasPendingSnapshotForChunk(ChunkKey chunk)
        {
            foreach (SnapshotAssembly assembly in m_snapshotAssemblies.Values)
                if (assembly.ChunkX == chunk.X && assembly.ChunkZ == chunk.Z)
                    return true;
            return false;
        }

        private bool HasPendingFullSnapshotForChunk(ChunkKey chunk)
        {
            foreach (SnapshotAssembly assembly in m_snapshotAssemblies.Values)
                if (assembly.Full && assembly.ChunkX == chunk.X &&
                    assembly.ChunkZ == chunk.Z &&
                    assembly.TimelineGeneration == m_timelineGeneration)
                    return true;
            return false;
        }

        private bool HasSnapshotForChunkAtOrBefore(ChunkKey chunk, int step)
        {
            foreach (SnapshotAssembly assembly in m_snapshotAssemblies.Values)
                if (assembly.ChunkX == chunk.X && assembly.ChunkZ == chunk.Z &&
                    assembly.HostStep <= step)
                    return true;
            return false;
        }

        private bool HasPendingCircuitEventForChunkAtOrBefore(ChunkKey chunk, int step)
        {
            foreach (CircuitEvent circuitEvent in m_deferredEvents)
                if ((circuitEvent.X >> 4) == chunk.X &&
                    (circuitEvent.Z >> 4) == chunk.Z &&
                    circuitEvent.HostCircuitStep <= step &&
                    !circuitEvent.Committed && !circuitEvent.Cancelled)
                    return true;
            return false;
        }

        // Source: Mod/SuMultiplayer/Protocol/circuit.proto:CircuitEvent.operation_sequence
        private void ApplyEventBatch(CircuitEventBatch batch)
        {
            if (batch == null || batch.Epoch != m_epoch || batch.Events.Count == 0) return;
            foreach (CircuitEvent circuitEvent in batch.Events)
            {
                if (circuitEvent.TimelineGeneration < m_timelineGeneration) continue;
                if (circuitEvent.HostCircuitStep <= 0)
                {
                    Warn("Host circuit operation has an invalid target step.");
                    continue;
                }
                CircuitEvent existingEvent = m_deferredEvents.FirstOrDefault(item =>
                    item.OperationSequence == circuitEvent.OperationSequence);
                if (existingEvent != null)
                {
                    if (existingEvent.HostCircuitStep != circuitEvent.HostCircuitStep ||
                        existingEvent.TimelineGeneration != circuitEvent.TimelineGeneration)
                    {
                        Warn("Circuit operation schedule and commit disagree on event identity.");
                        continue;
                    }
                    if (circuitEvent.Cancelled || circuitEvent.Committed)
                    {
                        existingEvent.MergeFrom(circuitEvent);
                        if (circuitEvent.Cancelled && existingEvent.Committed) continue;
                        RefreshClientStepBarrier();
                    }
                    continue;
                }
                ChunkKey chunk = new ChunkKey(circuitEvent.X >> 4, circuitEvent.Z >> 4);
                if (m_deferredEvents.Any(item =>
                        item.OperationSequence == circuitEvent.OperationSequence)) continue;
                if (m_deferredEvents.Count >= MaximumPendingOperations)
                {
                    Warn("Client circuit event queue is full; requesting authoritative state for " +
                        chunk + ".");
                    RequestChunkSnapshot(chunk);
                    continue;
                }
                int currentStep = m_world.TryGetClock(out int step, out _, out _, out _, out _)
                    ? step : m_lastCircuitStep;
                if (circuitEvent.Committed && circuitEvent.HostCircuitStep <= currentStep)
                {
                    HandleLateCircuitEvent(circuitEvent);
                    continue;
                }
                CircuitEvent copy = circuitEvent.Clone();
                int index = m_deferredEvents.FindIndex(item =>
                    item.HostCircuitStep > copy.HostCircuitStep ||
                    (item.HostCircuitStep == copy.HostCircuitStep &&
                        CompareOperationSequence(item.OperationSequence,
                            copy.OperationSequence) > 0));
                if (index < 0) m_deferredEvents.Add(copy);
                else m_deferredEvents.Insert(index, copy);
            }
            if (m_clientClockAccelerated || m_clientClockPaused)
            {
                int currentStep = m_world.TryGetClock(out int step, out _, out _, out _, out _)
                    ? step : m_lastCircuitStep;
                foreach (CircuitEvent scheduled in m_deferredEvents)
                    if (scheduled.HostCircuitStep > currentStep)
                        m_lastCircuitStep = Math.Max(m_lastCircuitStep,
                            scheduled.HostCircuitStep - 1);
            }
            RefreshClientStepBarrier();
        }

        private void HandleLateCircuitEvent(CircuitEvent circuitEvent)
        {
            ChunkKey chunk = new ChunkKey(circuitEvent.X >> 4, circuitEvent.Z >> 4);
            if (circuitEvent.Committed)
            {
                if (IsNewerEventSequence(circuitEvent.CommittedEventSequence,
                        unchecked((uint)m_clientLastCommittedEventSequence)))
                    m_clientLastCommittedEventSequence =
                        unchecked((int)circuitEvent.CommittedEventSequence);
                if (!m_lateCommittedOperations.TryGetValue(chunk,
                        out List<CircuitEvent> pendingOperations))
                {
                    pendingOperations = new List<CircuitEvent>();
                    m_lateCommittedOperations.Add(chunk, pendingOperations);
                }
                if (!pendingOperations.Any(item =>
                        item.CommittedEventSequence == circuitEvent.CommittedEventSequence))
                    pendingOperations.Add(circuitEvent.Clone());
            }
            else if (circuitEvent.Cancelled)
            {
                RejectCancelledOperation(circuitEvent);
                return;
            }
            m_clientChunkBoundaries.Remove(chunk);
            RequestChunkSnapshot(chunk);
            Warn("Circuit operation arrived after its target step at " + chunk +
                "; deferred confirmation to a fresh authoritative circuit snapshot.");
        }

        // Source: Mod/SuMultiplayer/Protocol/circuit.proto:CircuitEvent.committed_event_sequence
        private static bool IsNewerEventSequence(uint candidate, uint current)
        {
            if (candidate == 0) return false;
            if (current == 0) return true;
            return unchecked((int)(candidate - current)) > 0;
        }

        private void ApplyOperationResult(CircuitOperationResult result)
        {
            if (result == null || !m_pendingOperations.ContainsKey(result.RequestId)) return;
            if (result.Epoch != m_epoch)
            {
                m_pendingOperations.Remove(result.RequestId);
                m_presentation.RejectPresentation(unchecked((long)result.RequestId), null);
                return;
            }
            if (result.Accepted)
            {
                if (string.Equals(result.Reason, "circuit-operation-scheduled",
                        StringComparison.Ordinal)) return;
                return;
            }
            ChunkKey chunk = new ChunkKey(result.X >> 4, result.Z >> 4);
            if (m_transport.TerrainSequence < RequiredClientTerrainSequence(
                    result.RequiredTerrainSequence))
            {
                if (!m_deferredOperationResults.Any(item => item.RequestId == result.RequestId) &&
                    m_deferredOperationResults.Count < MaximumPendingOperations)
                    m_deferredOperationResults.Add(result.Clone());
                return;
            }
            if (result.TimelineGeneration > m_timelineGeneration)
            {
                if (!m_deferredOperationResults.Any(item => item.RequestId == result.RequestId) &&
                    m_deferredOperationResults.Count < MaximumPendingOperations)
                    m_deferredOperationResults.Add(result.Clone());
                return;
            }
            long currentTopologyGeneration = CurrentClientTopologyGeneration(chunk);
            if (result.RequiredTopologyGeneration != currentTopologyGeneration)
            {
                if (result.RequiredTopologyGeneration > currentTopologyGeneration)
                {
                    if (!m_deferredOperationResults.Any(item => item.RequestId == result.RequestId) &&
                        m_deferredOperationResults.Count < MaximumPendingOperations)
                        m_deferredOperationResults.Add(result.Clone());
                    m_clientScopeRequested = false;
                    m_nextClientScopeRefreshMs = 0;
                    return;
                }
                m_pendingOperations.Remove(result.RequestId);
                int? currentCell = m_world.TryGetCellValue(result.X, result.Y, result.Z,
                    out int valueAtTarget) ? valueAtTarget : (int?)null;
                m_presentation.RejectPresentation(unchecked((long)result.RequestId), currentCell);
                return;
            }
            if (result.TimelineGeneration < m_timelineGeneration)
            {
                m_pendingOperations.Remove(result.RequestId);
                m_presentation.RejectPresentation(unchecked((long)result.RequestId), null);
                return;
            }
            m_pendingOperations.Remove(result.RequestId);
            int? value = result.HasCellValue ? unchecked((int)result.CellValue) : (int?)null;
            m_presentation.RejectPresentation(unchecked((long)result.RequestId), value);
        }

        private void ProcessDeferredAuthoritativeMessages()
        {
            for (int i = m_deferredOperationResults.Count - 1; i >= 0; i--)
            {
                CircuitOperationResult result = m_deferredOperationResults[i];
                ChunkKey chunk = new ChunkKey(result.X >> 4, result.Z >> 4);
                if (result.RequiredTopologyGeneration > CurrentClientTopologyGeneration(chunk) ||
                    m_transport.TerrainSequence < RequiredClientTerrainSequence(result.RequiredTerrainSequence)) continue;
                m_deferredOperationResults.RemoveAt(i);
                ApplyOperationResult(result);
            }
        }

        private bool SubmitOperation(CircuitInteractionRequestData request)
        {
            if (m_transport == null || !m_transport.IsClient ||
                !m_transport.IsClientCircuitAttached ||
                m_pendingOperations.Count >= MaximumPendingOperations)
                return false;
            if (!m_world.TryGetClock(out int step, out _, out _, out _, out string error))
            {
                Warn("Circuit operation rejected because the local clock is unavailable: " + error);
                return false;
            }
            var message = new CircuitEventRequest
            {
                RequestId = unchecked((ulong)request.RequestId),
                ClientCircuitStep = step,
                RoundTripMs = m_transport.LastRoundTripMs,
                X = request.X,
                Y = request.Y,
                Z = request.Z,
                MountingFace = (uint)request.MountingFace,
                Operation = (CircuitOperation)request.Kind,
                Value = unchecked((uint)request.ExpectedCellValue),
                RayX = request.RayX,
                RayY = request.RayY,
                RayZ = request.RayZ,
                DirectionX = request.DirectionX,
                DirectionY = request.DirectionY,
                DirectionZ = request.DirectionZ,
                Reach = request.Reach
            };
            if (!SendHost(MessageType.CircuitEventRequest, message.ToByteArray()))
            {
                Warn("Circuit operation request send failed.");
                return false;
            }
            m_pendingOperations[message.RequestId] = new CircuitOperationResult();
            return true;
        }

        private PeerState GetPeerState(int clientId)
        {
            if (!m_peerStates.TryGetValue(clientId, out PeerState state))
            {
                state = new PeerState();
                m_peerStates.Add(clientId, state);
            }
            return state;
        }

        private void HandlePeerAttached(int clientId)
        {
            if (clientId <= 0) return;
            m_peerStates.Remove(clientId);
            m_peerStates.Add(clientId, new PeerState());
        }

        private void HandlePeerDetached(int clientId, string reason) => m_peerStates.Remove(clientId);

        private void UpdateClientSimulationClock()
        {
            if (m_transport?.IsClient != true || !m_clientClockInitialized ||
                !m_transport.IsClientCircuitAttached || !m_clientScopeRequested ||
                !m_world.IsReady)
            {
                m_clientSimulationBlockReason = m_transport?.IsClient != true
                    ? "not-client"
                    : !m_clientClockInitialized ? "waiting-clock"
                    : !m_transport.IsClientCircuitAttached ? "circuit-not-attached"
                    : !m_clientScopeRequested ? "scope-not-requested"
                    : "world-not-ready";
                if (m_transport?.IsClient == true)
                    CircuitSimulationPauseSignal.SetClientClock(false,
                        m_lastCircuitStep, m_clientClockPaused, m_clientClockAccelerated);
                return;
            }

            m_chunks.Clear();
            m_chunks.AddRange(m_clientCircuitScope);
            bool scopeReady = m_clientScopeAcknowledged && m_clientScopeRequested &&
                m_chunks.Count <= MaximumInterestChunks;
            if (!scopeReady)
                m_clientSimulationBlockReason = "scope-not-ready:" +
                    (m_clientScopeAcknowledged ? m_chunks.Count.ToString() : "unacknowledged");
            if (scopeReady && m_clientTopologyAwaitingSnapshot.Any(
                    key => m_clientCircuitScope.Contains(key) &&
                        !HasPendingFullSnapshotForChunk(key)))
            {
                scopeReady = false;
                m_clientSimulationBlockReason = "topology-awaiting-full-snapshot";
            }
            if (scopeReady)
            {
                foreach (ChunkKey chunk in m_clientCircuitScope)
                {
                    bool initialized = m_clientInitializedScope.Contains(chunk);
                    bool hasPendingFullSnapshot = HasPendingFullSnapshotForChunk(chunk);
                    bool hasPendingSnapshot = HasPendingSnapshotForChunk(chunk);
                    if (!initialized && !hasPendingFullSnapshot)
                    {
                        scopeReady = false;
                        m_clientSimulationBlockReason = "waiting-full-snapshot:" + chunk;
                        break;
                    }
                    if (initialized && !hasPendingSnapshot &&
                        (!m_clientChunkBoundaries.TryGetValue(chunk,
                             out AppliedBoundary boundary) ||
                         boundary.TimelineGeneration != m_timelineGeneration ||
                         boundary.TopologyGeneration != CurrentClientTopologyGeneration(chunk)))
                    {
                        scopeReady = false;
                        m_clientSimulationBlockReason = "invalid-applied-boundary:" + chunk;
                        break;
                    }
                }
            }

            bool stopped = m_clientClockPaused || m_clientClockAccelerated;
            if (scopeReady)
                m_clientSimulationBlockReason = stopped ? "host-stopped" :
                    m_clientResumePending ? "resume-alignment" : "ready";
            if (scopeReady && m_clientResumePending && !stopped)
            {
                if (!m_world.AlignClientCircuitStep(m_lastCircuitStep, true,
                        out string error))
                {
                    Warn("Client circuit resume alignment failed: " + error);
                    scopeReady = false;
                }
                else
                    m_clientResumePending = false;
            }

            CircuitSimulationPauseSignal.SetClientClock(
                scopeReady && !stopped && !m_clientResumePending,
                m_lastCircuitStep, m_clientClockPaused, m_clientClockAccelerated);
            if (stopped || m_clientResumePending)
                CircuitSimulationPauseSignal.SetClientStepBarrier(int.MaxValue);
            else
                RefreshClientStepBarrier();
        }

        private string DescribePendingSnapshotDiagnostics()
        {
            if (m_transport?.IsClientCircuitAttached != true)
                return m_snapshotAssemblies.Count + "/0";

            int inScope = 0;
            int waitingForClock = 0;
            int waitingForParts = 0;
            int waitingForTerrain = 0;
            int waitingForTopology = 0;
            int waitingForWorld = 0;
            int localGenerationBehind = 0;
            int localGenerationAhead = 0;
            long oldestMs = 0;
            long nowMs = (long)(m_clock() * 1000.0);
            foreach (SnapshotAssembly assembly in m_snapshotAssemblies.Values)
            {
                var key = new ChunkKey(assembly.ChunkX, assembly.ChunkZ);
                if (!m_clientCircuitScope.Contains(key)) continue;
                inScope++;
                long ageMs = Math.Max(0L, nowMs - assembly.CreatedMs);
                if (ageMs > oldestMs) oldestMs = ageMs;
                if (!m_clientClockInitialized)
                {
                    waitingForClock++;
                    continue;
                }
                if (assembly.Parts.Count != assembly.PartCount)
                {
                    waitingForParts++;
                    continue;
                }
                if (m_transport.TerrainSequence < RequiredClientTerrainSequence(
                        assembly.RequiredTerrainSequence))
                {
                    waitingForTerrain++;
                    continue;
                }
                long localGeneration = CurrentClientTopologyGeneration(key);
                if (localGeneration != assembly.RequiredTopologyGeneration)
                {
                    waitingForTopology++;
                    if (localGeneration < assembly.RequiredTopologyGeneration)
                        localGenerationBehind++;
                    else
                        localGenerationAhead++;
                    continue;
                }
                waitingForWorld++;
            }
            return m_snapshotAssemblies.Count + "/" + inScope +
                " wait(clock=" + waitingForClock + " parts=" + waitingForParts +
                " terrain=" + waitingForTerrain + " topology=" + waitingForTopology +
                "[localBehind=" + localGenerationBehind + " localAhead=" +
                localGenerationAhead + "] world=" + waitingForWorld +
                " dirtyTopology=" + m_clientTopologyDirtyChunks.Count +
                " oldestMs=" + oldestMs + ")";
        }

        private void PruneSnapshotsOutsideCircuitScope(HashSet<ChunkKey> nextCircuitScope)
        {
            foreach (SnapshotAssembly assembly in m_snapshotAssemblies.Values.ToArray())
            {
                var key = new ChunkKey(assembly.ChunkX, assembly.ChunkZ);
                if (nextCircuitScope.Contains(key) || IsCompleteEmptyFullSnapshot(assembly))
                    continue;
                m_snapshotAssemblies.Remove(assembly.SnapshotId);
            }
        }

        private static bool IsCompleteEmptyFullSnapshot(SnapshotAssembly assembly)
        {
            if (!assembly.Full || assembly.Parts.Count != assembly.PartCount) return false;
            foreach (CircuitSnapshotPart part in assembly.Parts.Values)
                if (part.Elements.Count != 0) return false;
            return true;
        }

        private void ResetForSessionChange()
        {
            string current = m_transport.SessionId ?? string.Empty;
            // The primary TCP link may be down for a few frames while the
            // circuit link reconnects. Keep the current host session and epoch
            // across that transient gap; an empty id is not a new room.
            if (string.IsNullOrEmpty(current)) return;
            if (string.Equals(current, m_sessionId, StringComparison.Ordinal)) return;
            m_sessionId = current;
            if (m_transport.IsClient)
                m_world.ResetClientCircuitState();
            // Only the host creates a new circuit epoch. A client must not advance
            // an epoch locally when the transport session changes; it waits for
            // the host's first CircuitClock after the reconnect.
            m_epoch = m_transport.IsHosting
                ? (m_epoch == uint.MaxValue ? 1u : m_epoch + 1u)
                : 0u;
            m_nextCommittedEventSequence = 0;
            m_nextOperationSequence = 0;
            m_clientLastCommittedEventSequence = 0;
            m_lastCircuitStep = 0;
            m_nextHostHashMs = 0;
            m_clientScopeRequested = false;
            m_clientScopeResponseReceived = false;
            m_clientScopeAcknowledged = false;
            m_clientPendingScopeRequestId = 0;
            m_clientScopeRequestSentMs = 0;
            m_wasCircuitAttached = false;
            m_clientClockInitialized = false;
            m_clientClockPaused = false;
            m_clientClockAccelerated = false;
            m_clientResumePending = false;
            m_hostWasAccelerated = false;
            m_clientInitializedScope.Clear();
            m_clientCircuitScope.Clear();
            ResetClientSyncMetadata();
            CircuitSimulationPauseSignal.ClearClientClock();
            m_peerStates.Clear();
            m_hostOperationsByStep.Clear();
            m_committedOperationsWaitingTerrain.Clear();
            m_lateCommittedOperations.Clear();
            m_pendingOperations.Clear();
            m_snapshotAssemblies.Clear();
            m_clientChunkBoundaries.Clear();
            m_clientHashCheckpoints.Clear();
            m_clientChunkCommittedEventSequences.Clear();
            m_hostChunkCommittedEventSequences.Clear();
            m_clientRepairRequests.Clear();
            m_clientEventTopologyResyncRequests.Clear();
            m_deferredOperationResults.Clear();
            m_deferredEvents.Clear();
            m_world.SetDeterministicRandomSeed(0, hostAuthority: false);
            m_pendingClock = null;
            m_hasConfiguredRandomSeed = false;
            m_randomSeedAuthority = false;
        }

        private void ResetClientSyncMetadata()
        {
            m_clientScopeMembershipDirtyChunks.Clear();
            m_clientTopologyDirtyChunks.Clear();
            m_clientTopologyAwaitingSnapshot.Clear();
            m_clientTopologyGenerations.Clear();
            m_clientTopologyTerrainSequences.Clear();
            m_clientCircuitCellValues.Clear();
            m_clientDesiredCircuitScope.Clear();
        }

        private bool SendBatch(int clientId, CircuitSyncBatch batch) =>
            SendChecked(clientId, MessageType.CircuitSyncBatch, batch);

        private bool SendTo(int clientId, MessageType type, byte[] payload) =>
            m_transport != null && payload != null &&
            (payload.Length <= MaximumCircuitFrameBytes || LogOversizedFrame(payload.Length)) &&
            m_transport.TrySendTo(clientId, type, payload);

        private bool SendHost(MessageType type, byte[] payload) =>
            m_transport != null && payload != null &&
            (payload.Length <= MaximumCircuitFrameBytes || LogOversizedFrame(payload.Length)) &&
            m_transport.TrySendHost(type, payload);

        private bool LogOversizedFrame(int size)
        {
            Warn("Circuit frame of " + size + " bytes exceeds the " + MaximumCircuitFrameBytes +
                " byte transport limit.");
            return false;
        }

        private bool SendChecked<T>(int clientId, MessageType type, T message)
            where T : IMessage<T>
        {
            byte[] payload = message.ToByteArray();
            if (payload.Length > MaximumCircuitFrameBytes)
            {
                Warn("Circuit frame exceeds " + MaximumCircuitFrameBytes +
                    " bytes and was rejected explicitly.");
                return false;
            }
            return SendTo(clientId, type, payload);
        }

        private static void AddPayload<T>(CircuitSyncBatch batch, MessageType type, T message)
            where T : IMessage<T>
        {
            batch.Payloads.Add(new CircuitPayload
            {
                MessageType = (uint)type,
                Payload = ByteString.CopyFrom(message.ToByteArray())
            });
        }

        private ulong NextSnapshotId()
        {
            m_nextSnapshotId = m_nextSnapshotId == ulong.MaxValue ? 1 : m_nextSnapshotId + 1;
            return m_nextSnapshotId;
        }

        private void LogCircuitHashDiagnostic(string message) =>
            m_context?.Diagnostics?.Info(m_descriptor.Id, "circuit-hash " + message);

        private static string FormatHash(uint hash) =>
            "0x" + hash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

        private static string ElementKey(CircuitElementState state) =>
            state.X + ":" + state.Y + ":" + state.Z + ":" + state.MountingFace;

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private void Warn(string message) =>
            m_context?.Diagnostics?.Warn(m_descriptor.Id, message);
    }
}
