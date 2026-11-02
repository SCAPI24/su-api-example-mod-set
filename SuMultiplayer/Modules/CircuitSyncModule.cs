using Engine;
using Google.Protobuf;
using SuMultiplayer.Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using SuMultiplayer.Protocol.Circuit;
using SuMultiplayer.Terrain;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SuMultiplayer.Modules
{
    // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
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

        private sealed class SnapshotAssembly
        {
            public uint Epoch;
            public ulong SnapshotId;
            public int HostStep;
            public int ChunkX;
            public int ChunkZ;
            public int PartCount;
            public bool Full;
            public int EventSequence;
            public int TimelineGeneration;
            public bool HasContext;
            public long CreatedMs;
            public long RequiredTerrainSequence;
            public readonly SortedDictionary<int, CircuitSnapshotPart> Parts =
                new SortedDictionary<int, CircuitSnapshotPart>();
        }

        private sealed class PeerState
        {
            // A paused clock is a state barrier: each peer must receive one
            // complete snapshot at the host's paused step before it can hold
            // that step as authoritative.
            public bool LastPaused;
            public bool PauseBarrierActive;
            public bool PauseBarrierComplete;
            public int PauseBarrierStep;
            public int PauseBarrierTimelineGeneration;
            public readonly HashSet<ChunkKey> PauseBarrierChunks = new HashSet<ChunkKey>();
            public readonly HashSet<ChunkKey> PauseBarrierApplied = new HashSet<ChunkKey>();
            public readonly Dictionary<ulong, ChunkKey> PauseBarrierSnapshots =
                new Dictionary<ulong, ChunkKey>();
            public readonly Dictionary<ulong, long> PauseBarrierSnapshotTimes =
                new Dictionary<ulong, long>();
            public readonly HashSet<ChunkKey> NeedsFullSnapshot = new HashSet<ChunkKey>();
            public readonly Dictionary<ChunkKey, uint> LastHashes = new Dictionary<ChunkKey, uint>();
            public readonly Dictionary<ChunkKey, Dictionary<string, byte[]>> LastStates =
                new Dictionary<ChunkKey, Dictionary<string, byte[]>>();
            public readonly Dictionary<ChunkKey, Dictionary<string, uint>> LastElementTypes =
                new Dictionary<ChunkKey, Dictionary<string, uint>>();
            public readonly HashSet<ChunkKey> RepairRequests = new HashSet<ChunkKey>();
            public readonly Dictionary<ulong, CircuitOperationResult> OperationResults =
                new Dictionary<ulong, CircuitOperationResult>();
            public readonly Queue<ulong> OperationResultOrder = new Queue<ulong>();
            public readonly Queue<long> OperationTimes = new Queue<long>();
            public readonly Dictionary<ChunkKey, Queue<HashBoundary>> HashBoundaries =
                new Dictionary<ChunkKey, Queue<HashBoundary>>();
            public readonly Dictionary<ulong, SnapshotBoundary> SnapshotBoundaries =
                new Dictionary<ulong, SnapshotBoundary>();
        }

        private readonly struct SnapshotBoundary
        {
            public SnapshotBoundary(ChunkKey chunk, HashBoundary boundary)
            {
                Chunk = chunk;
                Boundary = boundary;
            }

            public ChunkKey Chunk { get; }
            public HashBoundary Boundary { get; }
        }

        private readonly struct HashBoundary
        {
            public HashBoundary(int step, long terrainSequence, uint eventSequence,
                int timelineGeneration, uint hash)
            {
                Step = step;
                TerrainSequence = terrainSequence;
                EventSequence = eventSequence;
                TimelineGeneration = timelineGeneration;
                Hash = hash;
            }

            public int Step { get; }
            public long TerrainSequence { get; }
            public uint EventSequence { get; }
            public int TimelineGeneration { get; }
            public uint Hash { get; }
        }

        private readonly struct AppliedBoundary : IEquatable<AppliedBoundary>
        {
            public AppliedBoundary(int step, long terrainSequence, int eventSequence,
                int timelineGeneration)
            {
                Step = step;
                TerrainSequence = terrainSequence;
                EventSequence = eventSequence;
                TimelineGeneration = timelineGeneration;
            }

            public int Step { get; }
            public long TerrainSequence { get; }
            public int EventSequence { get; }
            public int TimelineGeneration { get; }

            public bool Equals(AppliedBoundary other) => Step == other.Step &&
                TerrainSequence == other.TerrainSequence && EventSequence == other.EventSequence &&
                TimelineGeneration == other.TimelineGeneration;

            public override bool Equals(object obj) => obj is AppliedBoundary other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Step, TerrainSequence,
                EventSequence, TimelineGeneration);
        }

        private readonly ModuleDescriptor m_descriptor;
        private readonly ICircuitWorldPort m_world;
        private readonly ICircuitPresentationPort m_presentation;
        private readonly Func<double> m_clock;
        private readonly Func<long> m_appliedTerrainSequence;
        private readonly Func<uint> m_randomSeedProvider;
        private readonly Func<ChunkKey, IList<CircuitElementState>, int, string> m_reconcileSnapshot;
        private readonly List<int> m_peers = new List<int>(8);
        private readonly List<ChunkKey> m_chunks = new List<ChunkKey>(64);
        private readonly List<CircuitElementState> m_states = new List<CircuitElementState>(256);
        private readonly Dictionary<int, PeerState> m_peerStates = new Dictionary<int, PeerState>();
        private readonly Dictionary<ulong, CircuitOperationResult> m_pendingOperations =
            new Dictionary<ulong, CircuitOperationResult>();
        private readonly Dictionary<ulong, SnapshotAssembly> m_snapshotAssemblies =
            new Dictionary<ulong, SnapshotAssembly>();
        private readonly Dictionary<ChunkKey, AppliedBoundary> m_clientChunkBoundaries =
            new Dictionary<ChunkKey, AppliedBoundary>();
        private readonly HashSet<ChunkKey> m_clientRequestedScope = new HashSet<ChunkKey>();
        private readonly List<CircuitOperationResult> m_deferredOperationResults =
            new List<CircuitOperationResult>();
        private readonly List<CircuitEvent> m_deferredEvents = new List<CircuitEvent>();

        private IModuleContext m_context;
        private ICircuitTransportPort m_transport;
        private ICircuitUseAuthorityPort m_useAuthority;
        private ICircuitInteractionHostPort m_interactionHost;
        private bool m_attached;
        private bool m_clientScopeRequested;
        private bool m_wasCircuitAttached;
        private uint m_epoch;
        private uint m_nextEventSequence;
        private ulong m_nextSnapshotId;
        private long m_nextSyncMs;
        private long m_nextHashMs;
        private long m_nextClientScopeRefreshMs;
        private string m_sessionId;
        private int m_lastCircuitStep;
        private int m_lastEventSequence;
        private int m_timelineGeneration;
        private bool m_hasConfiguredRandomSeed;
        private bool m_randomSeedAuthority;
        private uint m_configuredRandomSeed;
        private CircuitClock m_pendingClock;
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
        private long m_clientHashReportsSent;
        private long m_clientHashChunksSent;
        private long m_clientWatermarkExpired;

        public string StatusLine() => "circuit-sync: role=" +
            (m_transport?.IsHosting == true ? "host" : m_transport?.IsClient == true ? "client" : "idle") +
            " epoch=" + m_epoch + " step=" + m_lastCircuitStep +
            " host(rounds=" + m_hostSyncRounds + " attached=" + m_hostAttachedPeers +
            " scopeChunks=" + m_hostScopeChunks + " sevenSegments=" + m_hostSevenSegmentStates +
            " snapshots=" + m_hostSnapshotChunksSent + "/" + m_hostSnapshotElementsSent +
            "/" + m_hostSnapshotPartsSent + " deltaStates=" + m_hostDeltaStatesSent +
            " sendFail=" + m_hostSendFailures + ")" +
            " client(scopeRequests=" + m_clientScopeRequests + "/" + m_clientScopeChunks +
            " parts=" + m_clientSnapshotPartsReceived + " applied=" +
            m_clientSnapshotsApplied + "/" + m_clientStatesApplied + " applyFail=" +
            m_clientApplyFailures + " hashes=" + m_clientHashReportsSent + "/" +
            m_clientHashChunksSent + " watermarkExpired=" + m_clientWatermarkExpired + ")" +
            " debugPaused=" + m_debugPauseRequested +
            " " + m_world.DescribeDiagnostics();

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
            Func<long> appliedTerrainSequence = null, Func<double> clock = null,
            Func<ChunkKey, IList<CircuitElementState>, int, string> reconcileSnapshot = null,
            Func<uint> randomSeed = null)
        {
            m_world = world ?? throw new ArgumentNullException(nameof(world));
            m_presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
            m_appliedTerrainSequence = appliedTerrainSequence ?? (() => 0L);
            m_clock = clock ?? (() => Time.RealTime);
            m_reconcileSnapshot = reconcileSnapshot;
            m_randomSeedProvider = randomSeed;
            m_descriptor = new ModuleDescriptor("circuit-sync", "Circuit synchronization")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "circuit.sync" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

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
            m_transport.FrameArrived += HandleFrame;
            m_transport.PeerAttached += HandlePeerAttached;
            m_transport.PeerDetached += HandlePeerDetached;
            m_presentation.SetCircuitRequestSink(SubmitOperation);
            m_sessionId = m_transport.SessionId;
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
            m_world.Pump();
            ResetForSessionChange();
            if (m_pendingClock != null && m_world.IsReady)
            {
                CircuitClock pending = m_pendingClock;
                m_pendingClock = null;
                ApplyClock(pending);
            }
            bool circuitAttached = m_transport.IsHosting
                ? true
                : m_transport.IsClientCircuitAttached;
            if (m_wasCircuitAttached && !circuitAttached)
            {
                m_clientScopeRequested = false;
                m_pendingOperations.Clear();
                m_deferredOperationResults.Clear();
                m_deferredEvents.Clear();
                m_snapshotAssemblies.Clear();
                m_clientChunkBoundaries.Clear();
                m_clientRequestedScope.Clear();
                m_pendingClock = null;
                m_presentation.DetachPresentation();
            }
            if (!m_wasCircuitAttached && circuitAttached)
                m_clientScopeRequested = false;
            m_wasCircuitAttached = circuitAttached;
            ProcessReadySnapshots();
            ProcessDeferredAuthoritativeMessages();
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
                    if (!m_clientScopeRequested || ClientScopeChanged())
                        RequestClientScope();
                }
                if (nowMs >= m_nextHashMs)
                {
                    SendClientHashes();
                    m_nextHashMs = nowMs + HashIntervalMs;
                }
            }
        }

        public void Detach(ModuleDetachReason reason)
        {
            if (m_transport != null)
            {
                m_transport.FrameArrived -= HandleFrame;
                m_transport.PeerAttached -= HandlePeerAttached;
                m_transport.PeerDetached -= HandlePeerDetached;
            }
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
            m_deferredOperationResults.Clear();
            m_deferredEvents.Clear();
            m_peers.Clear();
            m_chunks.Clear();
            m_states.Clear();
            m_transport = null;
            m_context = null;
            m_attached = false;
            m_clientScopeRequested = false;
            m_wasCircuitAttached = false;
            m_debugPauseRequested = false;
            m_debugPauseStep = int.MinValue;
            m_debugPauseStates.Clear();
            m_pendingClock = null;
            CircuitSimulationPauseSignal.Set(false);
        }

        private void HostSync(long nowMs)
        {
            m_hostSyncRounds++;
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
                foreach (PeerState existingPeer in m_peerStates.Values)
                {
                    existingPeer.LastPaused = false;
                    ClearPauseBarrier(existingPeer);
                    existingPeer.LastStates.Clear();
                    existingPeer.LastElementTypes.Clear();
                    existingPeer.LastHashes.Clear();
                    existingPeer.HashBoundaries.Clear();
                    existingPeer.SnapshotBoundaries.Clear();
                    existingPeer.NeedsFullSnapshot.Clear();
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
            m_transport.CopyAttachedPeerIds(m_peers);
            if (m_peers.Count == 0) return;

            foreach (int clientId in m_peers)
            {
                if (!m_transport.IsHostCircuitAttached(clientId)) continue;
                m_hostAttachedPeers++;
                PeerState peer = GetPeerState(clientId);
                m_chunks.Clear();
                m_transport.CopyClientChunkScope(clientId, m_chunks);
                if (m_chunks.Count > MaximumInterestChunks)
                {
                    Warn("Client " + clientId + " circuit interest exceeds " +
                        MaximumInterestChunks + " chunks; synchronization is rejected.");
                    continue;
                }
                m_hostScopeChunks += m_chunks.Count;
                PrunePeerScope(peer, m_chunks);
                long barrierNowMs = nowMs;
                if (paused)
                    UpdatePauseBarrier(peer, m_chunks, step, m_timelineGeneration, barrierNowMs);
                else if (peer.PauseBarrierActive)
                    ClearPauseBarrier(peer);

                var batch = new CircuitSyncBatch { Epoch = m_epoch, HostCircuitStep = step };
                var clockMessage = new CircuitClock
                {
                    Epoch = m_epoch,
                    HostCircuitStep = step,
                    LastEventSequence = unchecked((int)m_nextEventSequence),
                    TerrainSequence = m_transport.TerrainSequence,
                    TotalElapsedGameTime = elapsed,
                    TimeOfDayOffset = dayOffset,
                    TimelineGeneration = m_timelineGeneration,
                    RandomSeed = randomSeed,
                    Paused = paused
                };
                AddPayload(batch, MessageType.CircuitClock, clockMessage);

                var changedStates = new Dictionary<ChunkKey, List<CircuitElementState>>();
                var pendingSnapshotIds = new Dictionary<ChunkKey, ulong>();
                var capturedStates = new Dictionary<ChunkKey, List<CircuitElementState>>();
                if (!m_world.TryCaptureChunks(m_chunks, capturedStates, out string batchCaptureError))
                {
                    Warn("Host circuit batch capture failed: " + batchCaptureError);
                    continue;
                }
                if (m_debugPauseRequested)
                {
                    foreach (KeyValuePair<ChunkKey, List<CircuitElementState>> pair in capturedStates)
                        if (!m_debugPauseStates.ContainsKey(pair.Key))
                            m_debugPauseStates[pair.Key] = pair.Value.Select(state => state.Clone()).ToList();
                    foreach (ChunkKey chunk in m_chunks)
                        if (m_debugPauseStates.TryGetValue(chunk, out List<CircuitElementState> frozen))
                            capturedStates[chunk] = frozen.Select(state => state.Clone()).ToList();
                }
                foreach (ChunkKey chunk in m_chunks)
                {
                    if (!capturedStates.TryGetValue(chunk, out List<CircuitElementState> captured))
                    {
                        Warn("Host circuit batch capture omitted " + chunk + ".");
                        continue;
                    }
                    if (paused && peer.PauseBarrierActive &&
                        peer.PauseBarrierChunks.Contains(chunk) &&
                        !peer.PauseBarrierApplied.Contains(chunk))
                    {
                        if (HasPauseBarrierSnapshotForChunk(peer, chunk))
                            continue;
                        peer.NeedsFullSnapshot.Add(chunk);
                    }
                    uint hash = m_world.ComputeHash(captured);
                    m_hostSevenSegmentStates += CircuitStateCodec.CountSevenSegmentStates(captured);
                    peer.LastHashes[chunk] = hash;
                    RememberHashBoundary(peer, chunk, new HashBoundary(step,
                        m_transport.TerrainSequence, m_nextEventSequence,
                        m_timelineGeneration, hash));
                    if (peer.NeedsFullSnapshot.Remove(chunk) || peer.RepairRequests.Remove(chunk) ||
                        !peer.LastStates.ContainsKey(chunk))
                    {
                        if (SendSnapshot(clientId, peer, chunk, step,
                                m_transport.TerrainSequence, captured, out ulong snapshotId))
                        {
                            RememberStates(peer, chunk, captured);
                            TrackPauseBarrierSnapshot(peer, chunk, step, snapshotId, nowMs);
                        }
                        else
                            peer.NeedsFullSnapshot.Add(chunk);
                        continue;
                    }

                    List<CircuitElementState> changed = FindChangedStates(peer, chunk, captured);
                    if (peer.NeedsFullSnapshot.Remove(chunk))
                    {
                        if (SendSnapshot(clientId, peer, chunk, step,
                                m_transport.TerrainSequence, captured, out ulong snapshotId))
                        {
                            RememberStates(peer, chunk, captured);
                            TrackPauseBarrierSnapshot(peer, chunk, step, snapshotId, nowMs);
                        }
                        else
                            peer.NeedsFullSnapshot.Add(chunk);
                    }
                    else
                    {
                        if (changed.Count > 0)
                        {
                            m_hostDeltaStatesSent += changed.Count;
                            ulong snapshotId = NextSnapshotId();
                            changedStates[chunk] = changed;
                            RegisterSnapshotBoundary(peer, snapshotId, chunk, step,
                                m_transport.TerrainSequence, hash);
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
                            m_transport.TerrainSequence, pair.Value, full: false,
                            pendingSnapshotIds[pair.Key]);
                    }
                    catch (Exception e)
                    {
                        Warn("Circuit delta framing failed for " + pair.Key + ": " +
                            e.GetType().Name + ": " + e.Message);
                        peer.NeedsFullSnapshot.Add(pair.Key);
                        peer.SnapshotBoundaries.Remove(pendingSnapshotIds[pair.Key]);
                        RemovePauseBarrierSnapshot(peer, pendingSnapshotIds[pair.Key], pair.Key);
                        continue;
                    }
                    bool appended = true;
                    foreach (CircuitPayload payload in payloads)
                    {
                        if (!AppendPayload(clientId, ref batch, clockMessage, payload))
                        {
                            peer.NeedsFullSnapshot.Add(pair.Key);
                            peer.SnapshotBoundaries.Remove(pendingSnapshotIds[pair.Key]);
                            RemovePauseBarrierSnapshot(peer, pendingSnapshotIds[pair.Key], pair.Key);
                            appended = false;
                            break;
                        }
                    }
                    if (appended)
                        TrackPauseBarrierSnapshot(peer, pair.Key, step,
                            pendingSnapshotIds[pair.Key], nowMs);
                }
                if (batch.Payloads.Count > 0 && !SendBatch(clientId, batch))
                {
                    m_hostSendFailures++;
                    Warn("Circuit sync batch send failed for client " + clientId + ".");
                    foreach (ChunkKey chunk in changedStates.Keys)
                    {
                        peer.NeedsFullSnapshot.Add(chunk);
                        if (pendingSnapshotIds.TryGetValue(chunk, out ulong id))
                        {
                            peer.SnapshotBoundaries.Remove(id);
                            RemovePauseBarrierSnapshot(peer, id, chunk);
                        }
                    }
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

        private List<CircuitElementState> FindChangedStates(PeerState peer, ChunkKey chunk,
            List<CircuitElementState> current)
        {
            if (!peer.LastStates.TryGetValue(chunk, out Dictionary<string, byte[]> previous))
            {
                peer.NeedsFullSnapshot.Add(chunk);
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
                    peer.NeedsFullSnapshot.Add(chunk);
                    return new List<CircuitElementState>();
                }
                byte[] bytes = state.ToByteArray();
                if (!previous.TryGetValue(key, out byte[] old) || !ByteArraysEqual(bytes, old))
                    changed.Add(state);
            }
            if (previous.Keys.Any(key => !currentKeys.Contains(key)))
            {
                peer.NeedsFullSnapshot.Add(chunk);
                return new List<CircuitElementState>();
            }
            return changed;
        }

        private static void RememberStates(PeerState peer, ChunkKey chunk,
            List<CircuitElementState> states)
        {
            var cache = new Dictionary<string, byte[]>(states.Count, StringComparer.Ordinal);
            var types = new Dictionary<string, uint>(states.Count, StringComparer.Ordinal);
            foreach (CircuitElementState state in states)
            {
                cache[ElementKey(state)] = state.ToByteArray();
                types[ElementKey(state)] = state.ElementType;
            }
            peer.LastStates[chunk] = cache;
            peer.LastElementTypes[chunk] = types;
        }

        private bool SendSnapshot(int clientId, PeerState peer, ChunkKey chunk, int hostStep,
            long terrainSequence, List<CircuitElementState> states, out ulong snapshotId)
        {
            snapshotId = NextSnapshotId();
            List<CircuitPayload> payloads;
            try
            {
                payloads = BuildStatePayloads(chunk, hostStep,
                    terrainSequence, states, full: true, snapshotId);
            }
            catch (Exception e)
            {
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
                    Warn("Full circuit snapshot send failed for client " + clientId + " chunk " + chunk + ".");
                    return false;
                }
                batch = new CircuitSyncBatch { Epoch = m_epoch, HostCircuitStep = hostStep };
                candidate = batch.Clone();
                candidate.Payloads.Add(payload);
                if (candidate.CalculateSize() > MaximumCircuitFrameBytes || !SendBatch(clientId, candidate))
                {
                    Warn("Circuit snapshot part exceeds the frame limit for client " + clientId +
                        " chunk " + chunk + ".");
                    return false;
                }
                batch = new CircuitSyncBatch { Epoch = m_epoch, HostCircuitStep = hostStep };
            }
            if (batch.Payloads.Count > 0 && !SendBatch(clientId, batch))
            {
                Warn("Full circuit snapshot send failed for client " + clientId + " chunk " + chunk + ".");
                return false;
            }
            RegisterSnapshotBoundary(peer, snapshotId, chunk, hostStep, terrainSequence,
                m_world.ComputeHash(states));
            m_hostSnapshotChunksSent++;
            m_hostSnapshotElementsSent += states.Count;
            m_hostSnapshotPartsSent += payloads.Count - 1;
            return true;
        }

        private List<CircuitPayload> BuildStatePayloads(ChunkKey chunk, int hostStep,
            long terrainSequence, List<CircuitElementState> states, bool full, ulong snapshotId)
        {
            var groups = new List<List<CircuitElementState>>();
            var current = new List<CircuitElementState>(MaximumSnapshotElements);
            foreach (CircuitElementState state in states)
            {
                current.Add(state);
                CircuitSnapshotPart candidate = CreateSnapshotPart(chunk, hostStep,
                    terrainSequence, current, full, snapshotId, 0, 1);
                if (current.Count <= MaximumSnapshotElements &&
                    candidate.CalculateSize() + 64 <= MaximumCircuitFrameBytes)
                    continue;
                current.RemoveAt(current.Count - 1);
                if (current.Count == 0)
                    throw new InvalidOperationException("A single circuit element state exceeds the frame limit.");
                groups.Add(current);
                current = new List<CircuitElementState>(MaximumSnapshotElements) { state };
                candidate = CreateSnapshotPart(chunk, hostStep, terrainSequence,
                    current, full, snapshotId, 0, 1);
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
                    terrainSequence, groups[i], full, snapshotId, i, groups.Count);
                payloads.Add(CreatePayload(MessageType.CircuitSnapshotPart, message));
            }
            return payloads;
        }

        private CircuitSnapshotPart CreateSnapshotPart(ChunkKey chunk, int hostStep,
            long terrainSequence, IList<CircuitElementState> states, bool full,
            ulong snapshotId, int partIndex, int partCount)
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
                LastEventSequence = unchecked((int)m_nextEventSequence),
                TimelineGeneration = m_timelineGeneration
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

        private void RequestClientScope()
        {
            m_chunks.Clear();
            if (m_world.CopyLoadedChunks(m_chunks) < 0 || m_chunks.Count > MaximumInterestChunks)
            {
                Warn("Client loaded circuit scope exceeds the protocol limit.");
                return;
            }
            if (m_chunks.Count == 0)
            {
                m_clientScopeRequested = false;
                m_clientRequestedScope.Clear();
                return;
            }

            bool replaceScope = !m_clientScopeRequested;
            var request = new CircuitSnapshotRequest
            {
                Epoch = m_epoch,
                RequestId = NextSnapshotId(),
                ReplaceScope = replaceScope
            };
            foreach (ChunkKey key in m_chunks)
                if (replaceScope || !m_clientRequestedScope.Contains(key))
                    request.Chunks.Add(new CircuitChunkKey { X = key.X, Z = key.Z });
            if (request.Chunks.Count == 0)
            {
                // Scope removal is already reflected by the primary InterestUpdate;
                // no circuit snapshot is needed. Keep the local baseline current so
                // a removal-only change does not trigger this check every interval.
                m_clientRequestedScope.Clear();
                foreach (ChunkKey key in m_chunks)
                    m_clientRequestedScope.Add(key);
                return;
            }
            if (SendHost(MessageType.CircuitSnapshotRequest, request.ToByteArray()))
            {
                m_clientScopeRequested = true;
                m_clientRequestedScope.Clear();
                foreach (ChunkKey key in m_chunks)
                    m_clientRequestedScope.Add(key);
                m_clientScopeRequests++;
                m_clientScopeChunks += request.Chunks.Count;
            }
            else
                Warn("Initial circuit snapshot request could not be queued.");
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

        private void SendClientHashes()
        {
            m_chunks.Clear();
            m_world.CopyLoadedChunks(m_chunks);
            var reports = new Dictionary<AppliedBoundary, CircuitHashReport>();
            long appliedTerrainSequence = m_appliedTerrainSequence();
            bool requestFreshScope = false;
            foreach (ChunkKey chunk in m_chunks)
            {
                if (HasPendingSnapshotForChunk(chunk)) continue;
                if (!m_clientChunkBoundaries.TryGetValue(chunk, out AppliedBoundary boundary) ||
                    boundary.TerrainSequence > appliedTerrainSequence) continue;
                if (!m_world.TryCaptureChunk(chunk, m_states, out string captureError))
                {
                    m_clientChunkBoundaries.Remove(chunk);
                    requestFreshScope = true;
                    Warn("Client circuit hash capture skipped for " + chunk +
                        "; requesting an authoritative snapshot: " + captureError);
                    continue;
                }
                if (!reports.TryGetValue(boundary, out CircuitHashReport report))
                {
                    report = new CircuitHashReport
                    {
                        Epoch = m_epoch,
                        HostCircuitStep = boundary.Step,
                        TerrainSequence = boundary.TerrainSequence,
                        LastEventSequence = boundary.EventSequence,
                        TimelineGeneration = boundary.TimelineGeneration
                    };
                    reports.Add(boundary, report);
                }
                report.Chunks.Add(new CircuitChunkHash
                {
                    ChunkX = chunk.X,
                    ChunkZ = chunk.Z,
                    Hash = m_world.ComputeHash(m_states)
                });
            }
            foreach (CircuitHashReport report in reports.Values)
            {
                if (SendHost(MessageType.CircuitHashReport, report.ToByteArray()))
                {
                    m_clientHashReportsSent++;
                    m_clientHashChunksSent += report.Chunks.Count;
                }
            }
            if (requestFreshScope)
                m_clientScopeRequested = false;
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
                    case MessageType.CircuitHashReport:
                        HandleHashReport(clientId, CircuitHashReport.Parser.ParseFrom(frame.Payload));
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
            if (m_pendingOperations.Count >= MaximumPendingOperations)
            {
                SendOperationResult(clientId, request.RequestId, false,
                    "circuit-operation-rate-limit",
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
            if (!m_useAuthority.TryExecuteCircuitUse(clientId, in useRequest, out reason))
            {
                SendOperationResult(clientId, request.RequestId, false, reason,
                    GetAuthoritativeCell(request.X, request.Y, request.Z), request);
                return;
            }
            if (!m_world.TryGetCellValue(request.X, request.Y, request.Z, out int cellValue))
            {
                Warn("Host accepted circuit operation but could not read its authoritative cell.");
                SendOperationResult(clientId, request.RequestId, false,
                    "authoritative-cell-unavailable-after-use", null, request);
                return;
            }
            uint sequence = BroadcastOperation(request, cellValue);
            SendOperationResult(clientId, request.RequestId, true, string.Empty,
                cellValue, request, sequence);
            m_nextSyncMs = 0;
        }

        private void SendOperationResult(int clientId, ulong requestId, bool accepted,
            string reason, int? cellValue, CircuitEventRequest request = null,
            uint sequence = 0)
        {
            var result = new CircuitOperationResult
            {
                RequestId = requestId,
                Accepted = accepted,
                Reason = reason ?? string.Empty,
                Sequence = sequence,
                HostCircuitStep = m_lastCircuitStep,
                Epoch = m_epoch,
                RequiredTerrainSequence = m_transport?.TerrainSequence ?? 0,
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
                peer.OperationResults[requestId] = result;
                peer.OperationResultOrder.Enqueue(requestId);
                while (peer.OperationResultOrder.Count > 256)
                    peer.OperationResults.Remove(peer.OperationResultOrder.Dequeue());
            }
            SendTo(clientId, MessageType.CircuitOperationResult, result.ToByteArray());
        }

        private int? GetAuthoritativeCell(int x, int y, int z) =>
            m_world.TryGetCellValue(x, y, z, out int value) ? value : (int?)null;

        private uint BroadcastOperation(CircuitEventRequest request, int cellValue)
        {
            m_nextEventSequence = m_nextEventSequence == uint.MaxValue ? 1 : m_nextEventSequence + 1;
            var message = new CircuitEventBatch
            {
                Epoch = m_epoch,
                HostCircuitStep = m_lastCircuitStep,
                FirstSequence = m_nextEventSequence
            };
            message.Events.Add(new CircuitEvent
            {
                Sequence = m_nextEventSequence,
                HostCircuitStep = m_lastCircuitStep,
                X = request.X,
                Y = request.Y,
                Z = request.Z,
                MountingFace = request.MountingFace,
                Operation = request.Operation,
                Value = unchecked((uint)cellValue),
                RequiredTerrainSequence = m_transport.TerrainSequence
            });
            var batch = new CircuitSyncBatch { Epoch = m_epoch, HostCircuitStep = m_lastCircuitStep };
            AddPayload(batch, MessageType.CircuitEventBatch, message);
            m_transport.CopyAttachedPeerIds(m_peers);
            ChunkKey eventChunk = new ChunkKey(request.X >> 4, request.Z >> 4);
            foreach (int peer in m_peers)
            {
                if (peer == 0 || !m_transport.IsHostCircuitAttached(peer)) continue;
                m_chunks.Clear();
                int scopeCount = m_transport.CopyClientChunkScope(peer, m_chunks);
                if (scopeCount < 0 || scopeCount > MaximumInterestChunks ||
                    !m_chunks.Contains(eventChunk)) continue;
                SendBatch(peer, batch);
            }
            return m_nextEventSequence;
        }

        private void HandleHashReport(int clientId, CircuitHashReport report)
        {
            if (report == null || report.Epoch != m_epoch || report.Chunks.Count > MaximumInterestChunks)
                return;
            PeerState peer = GetPeerState(clientId);
            m_chunks.Clear();
            int scopeCount = m_transport.CopyClientChunkScope(clientId, m_chunks);
            if (scopeCount < 0 || scopeCount > MaximumInterestChunks) return;
            foreach (CircuitChunkHash remote in report.Chunks)
            {
                var key = new ChunkKey(remote.ChunkX, remote.ChunkZ);
                if (!m_chunks.Contains(key)) continue;
                if (!peer.HashBoundaries.TryGetValue(key,
                        out Queue<HashBoundary> boundaries)) continue;
                bool found = false;
                bool ambiguous = false;
                uint expectedHash = 0;
                foreach (HashBoundary boundary in boundaries)
                {
                    if (boundary.Step != report.HostCircuitStep ||
                        boundary.TerrainSequence != report.TerrainSequence ||
                        boundary.EventSequence != unchecked((uint)report.LastEventSequence) ||
                        boundary.TimelineGeneration != report.TimelineGeneration) continue;
                    if (found && expectedHash != boundary.Hash)
                    {
                        ambiguous = true;
                        break;
                    }
                    found = true;
                    expectedHash = boundary.Hash;
                }
                if (!found || ambiguous) continue;
                if (expectedHash != remote.Hash)
                    peer.RepairRequests.Add(key);
            }
        }

        private static void RememberHashBoundary(PeerState peer, ChunkKey chunk,
            HashBoundary boundary)
        {
            if (!peer.HashBoundaries.TryGetValue(chunk, out Queue<HashBoundary> boundaries))
            {
                boundaries = new Queue<HashBoundary>();
                peer.HashBoundaries.Add(chunk, boundaries);
            }
            boundaries.Enqueue(boundary);
            while (boundaries.Count > 8) boundaries.Dequeue();
        }

        private void RegisterSnapshotBoundary(PeerState peer, ulong snapshotId, ChunkKey chunk,
            int step, long terrainSequence, uint hash)
        {
            var boundary = new HashBoundary(step, terrainSequence, m_nextEventSequence,
                m_timelineGeneration, hash);
            peer.SnapshotBoundaries[snapshotId] = new SnapshotBoundary(chunk, boundary);
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

        private static void UpdatePauseBarrier(PeerState peer, IList<ChunkKey> scope,
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
                peer.NeedsFullSnapshot.Remove(old);
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
                    peer.NeedsFullSnapshot.Add(chunk);
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

        private static void ExpirePauseBarrierSnapshots(PeerState peer, long nowMs)
        {
            foreach (ulong snapshotId in peer.PauseBarrierSnapshotTimes
                .Where(item => nowMs - item.Value >= SnapshotTimeoutMs)
                .Select(item => item.Key).ToArray())
            {
                if (peer.PauseBarrierSnapshots.TryGetValue(snapshotId, out ChunkKey chunk))
                {
                    peer.NeedsFullSnapshot.Add(chunk);
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

        private static void PrunePeerScope(PeerState peer, List<ChunkKey> scope)
        {
            var visible = new HashSet<ChunkKey>(scope);
            foreach (ChunkKey key in peer.LastStates.Keys.Where(key => !visible.Contains(key)).ToArray())
            {
                peer.LastStates.Remove(key);
                peer.LastElementTypes.Remove(key);
                peer.LastHashes.Remove(key);
                peer.HashBoundaries.Remove(key);
                peer.NeedsFullSnapshot.Remove(key);
                peer.RepairRequests.Remove(key);
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
            foreach (ChunkKey key in scope)
            {
                if (!m_world.TryCaptureChunk(key, m_states, out string error))
                {
                    Warn("Requested circuit snapshot capture failed for " + key + ": " + error);
                    continue;
                }
                if (SendSnapshot(clientId, peer, key, m_lastCircuitStep,
                        m_transport.TerrainSequence, m_states, out ulong snapshotId))
                {
                    RememberStates(peer, key, m_states);
                    peer.NeedsFullSnapshot.Remove(key);
                    TrackPauseBarrierSnapshot(peer, key, m_lastCircuitStep, snapshotId,
                        (long)(m_clock() * 1000.0));
                }
                else
                    peer.NeedsFullSnapshot.Add(key);
            }
            if (request.ReplaceScope)
            {
                foreach (ChunkKey old in peer.LastStates.Keys.Where(key => !scope.Contains(key)).ToArray())
                {
                    peer.LastStates.Remove(old);
                    peer.LastHashes.Remove(old);
                }
            }
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
                boundary.EventSequence != unchecked((uint)report.LastEventSequence) ||
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
            peer.SnapshotBoundaries.Remove(report.SnapshotId);
            peer.PauseBarrierSnapshots.Remove(report.SnapshotId);
            peer.PauseBarrierSnapshotTimes.Remove(report.SnapshotId);
            if (boundary.Hash != report.StateHash)
            {
                peer.RepairRequests.Add(key);
                if (pauseSnapshot)
                {
                    peer.NeedsFullSnapshot.Add(key);
                    peer.PauseBarrierApplied.Remove(key);
                }
            }
            else if (pauseSnapshot)
            {
                peer.NeedsFullSnapshot.Remove(key);
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
                    case MessageType.CircuitOperationResult:
                        ApplyOperationResult(CircuitOperationResult.Parser.ParseFrom(payload.Payload));
                        break;
                    case MessageType.CircuitRepairBarrier:
                        RegisterRepairBarrier(CircuitRepairBarrier.Parser.ParseFrom(payload.Payload));
                        break;
                    case MessageType.CircuitEventBatch:
                        ApplyEventBatch(CircuitEventBatch.Parser.ParseFrom(payload.Payload));
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
        }

        private void QueueSnapshotPart(CircuitSnapshotPart part)
        {
            if (part == null || part.Epoch != m_epoch || part.PartCount == 0 ||
                part.PartCount > 4096 || part.PartIndex >= part.PartCount) return;
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
                    EventSequence = part.LastEventSequence,
                    TimelineGeneration = part.TimelineGeneration,
                    HasContext = true,
                    CreatedMs = (long)(m_clock() * 1000.0),
                    RequiredTerrainSequence = part.RequiredTerrainSequence
                };
                assembly.Full = part.Full;
                m_snapshotAssemblies.Add(part.SnapshotId, assembly);
            }
            if (assembly.PartCount != (int)part.PartCount ||
                assembly.ChunkX != part.ChunkX || assembly.ChunkZ != part.ChunkZ ||
                assembly.HostStep != part.HostCircuitStep || assembly.Full != part.Full ||
                (assembly.HasContext && assembly.EventSequence != part.LastEventSequence) ||
                (assembly.HasContext && assembly.TimelineGeneration != part.TimelineGeneration) ||
                (assembly.HasContext && assembly.RequiredTerrainSequence != part.RequiredTerrainSequence) ||
                part.RequiredTerrainSequence < 0)
            {
                m_snapshotAssemblies.Remove(part.SnapshotId);
                m_clientScopeRequested = false;
                Warn("Circuit snapshot parts disagree on metadata.");
                return;
            }
            assembly.EventSequence = part.LastEventSequence;
            assembly.TimelineGeneration = part.TimelineGeneration;
            assembly.HasContext = true;
            assembly.RequiredTerrainSequence = part.RequiredTerrainSequence;
            assembly.Parts[(int)part.PartIndex] = part;
            m_clientSnapshotPartsReceived++;
            m_clientChunkBoundaries.Remove(new ChunkKey(part.ChunkX, part.ChunkZ));
        }

        private void ProcessReadySnapshots()
        {
            long appliedSequence = m_appliedTerrainSequence();
            long nowMs = (long)(m_clock() * 1000.0);
            foreach (SnapshotAssembly assembly in m_snapshotAssemblies.Values.ToArray())
            {
                if (assembly.Parts.Count != assembly.PartCount ||
                    appliedSequence < assembly.RequiredTerrainSequence)
                {
                    if (nowMs - assembly.CreatedMs >= SnapshotTimeoutMs)
                    {
                        m_snapshotAssemblies.Remove(assembly.SnapshotId);
                        m_clientScopeRequested = false;
                        Warn("Circuit snapshot timed out before its terrain barrier or all parts arrived.");
                    }
                    continue;
                }
                // A full snapshot contains the authoritative topology and can still be
                // applied after the terrain stream has advanced: reconciliation below
                // verifies that the newer terrain has the same circuit topology. A delta
                // cannot make that guarantee, so it must be discarded and replaced by a
                // fresh full snapshot.
                if (appliedSequence > assembly.RequiredTerrainSequence && !assembly.Full)
                {
                    m_snapshotAssemblies.Remove(assembly.SnapshotId);
                    m_clientScopeRequested = false;
                    m_clientWatermarkExpired++;
                    Warn("Circuit delta passed its terrain watermark; requesting a fresh full snapshot.");
                    continue;
                }
                bool applied = true;
                var states = new List<CircuitElementState>();
                foreach (CircuitSnapshotPart part in assembly.Parts.Values)
                    states.AddRange(part.Elements);
                ChunkKey key = new ChunkKey(assembly.ChunkX, assembly.ChunkZ);
                // A join-time scope request can arrive before the target terrain chunk is
                // valid. Keep the complete snapshot queued until the terrain is ready instead
                // of counting this transient loading state as an element-apply failure.
                if (!m_world.IsChunkReady(key))
                    continue;
                if (!m_world.AreElementsReady(states))
                    continue;
                if (assembly.Full)
                {
                    string reconcileError = m_reconcileSnapshot?.Invoke(key, states,
                        assembly.HostStep);
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
                    foreach (CircuitElementState state in states)
                    {
                        if (!m_world.Apply(state, assembly.HostStep, out string error))
                        {
                            Warn("Circuit state apply failed: " + error);
                            applied = false;
                            break;
                        }
                    }
                }
                if (applied && m_world.TryCaptureChunk(key, m_states, out string captureError))
                {
                    var appliedMessage = new CircuitRepairApplied
                    {
                        Epoch = m_epoch,
                        SnapshotId = assembly.SnapshotId,
                        ChunkX = key.X,
                        ChunkZ = key.Z,
                        StateHash = m_world.ComputeHash(m_states),
                        TerrainSequence = assembly.RequiredTerrainSequence,
                        LastEventSequence = assembly.EventSequence,
                        TimelineGeneration = assembly.TimelineGeneration
                    };
                    SendHost(MessageType.CircuitRepairApplied, appliedMessage.ToByteArray());
                    m_clientChunkBoundaries[key] = new AppliedBoundary(assembly.HostStep,
                        assembly.RequiredTerrainSequence, assembly.EventSequence,
                        assembly.TimelineGeneration);
                    m_clientSnapshotsApplied++;
                    m_clientStatesApplied += states.Count;
                    uint snapshotSequence = unchecked((uint)assembly.EventSequence);
                    uint currentEventSequence = unchecked((uint)m_lastEventSequence);
                    if (IsNewerEventSequence(snapshotSequence, currentEventSequence))
                        m_lastEventSequence = assembly.EventSequence;
                }
                else if (!applied)
                {
                    m_clientApplyFailures++;
                    m_clientScopeRequested = false;
                    Warn("Circuit snapshot apply stopped after an explicit element failure.");
                }
                m_snapshotAssemblies.Remove(assembly.SnapshotId);
            }
        }

        private void ApplyClock(CircuitClock message)
        {
            if (message == null) return;
            if (!m_transport.IsHosting && m_epoch == 0)
                m_epoch = message.Epoch;
            if (message.Epoch != m_epoch || message.TimelineGeneration < m_timelineGeneration ||
                (message.TimelineGeneration == m_timelineGeneration &&
                    message.HostCircuitStep < m_lastCircuitStep)) return;
            if (message.TimelineGeneration > m_timelineGeneration)
            {
                m_pendingOperations.Clear();
                m_deferredOperationResults.Clear();
                m_deferredEvents.Clear();
                m_snapshotAssemblies.Clear();
                m_clientChunkBoundaries.Clear();
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
            CircuitSimulationPauseSignal.Set(message.Paused);
            m_lastCircuitStep = message.HostCircuitStep;
            m_timelineGeneration = message.TimelineGeneration;
            if (!m_hasConfiguredRandomSeed || m_configuredRandomSeed != message.RandomSeed ||
                m_randomSeedAuthority)
            {
                m_world.SetDeterministicRandomSeed(message.RandomSeed, hostAuthority: false);
                m_configuredRandomSeed = message.RandomSeed;
                m_hasConfiguredRandomSeed = true;
                m_randomSeedAuthority = false;
            }
            // A clock only advances the host timeline. It does not advance the
            // state actually installed in any client chunk. Keep each chunk's
            // applied boundary unchanged until its snapshot or event has been
            // applied successfully; otherwise an old voltage would be reported
            // as a current host state and a paused host could be acknowledged
            // with a false hash.
        }

        private bool HasPendingSnapshotForChunk(ChunkKey chunk)
        {
            foreach (SnapshotAssembly assembly in m_snapshotAssemblies.Values)
                if (assembly.ChunkX == chunk.X && assembly.ChunkZ == chunk.Z)
                    return true;
            foreach (CircuitEvent circuitEvent in m_deferredEvents)
                if ((circuitEvent.X >> 4) == chunk.X && (circuitEvent.Z >> 4) == chunk.Z)
                    return true;
            return false;
        }

        // Source: Mod/SuMultiplayer/Protocol/circuit.proto:CircuitEvent.sequence
        private void ApplyEventBatch(CircuitEventBatch batch)
        {
            if (batch == null || batch.Epoch != m_epoch || batch.Events.Count == 0) return;
            foreach (CircuitEvent circuitEvent in batch.Events)
            {
                uint lastSequence = unchecked((uint)m_lastEventSequence);
                if (!IsNewerEventSequence(circuitEvent.Sequence, lastSequence)) continue;
                if (circuitEvent.RequiredTerrainSequence > m_appliedTerrainSequence())
                {
                    if (m_deferredEvents.Count < MaximumPendingOperations)
                    {
                        m_deferredEvents.Add(circuitEvent.Clone());
                        m_clientChunkBoundaries.Remove(new ChunkKey(
                            circuitEvent.X >> 4, circuitEvent.Z >> 4));
                    }
                    else
                        Warn("Deferred circuit event limit reached; requesting a full circuit scope.");
                    continue;
                }
                if (circuitEvent.RequiredTerrainSequence < m_appliedTerrainSequence())
                    continue;
                m_lastEventSequence = unchecked((int)circuitEvent.Sequence);
                m_presentation.ApplyAuthoritativeCell(circuitEvent.X, circuitEvent.Y,
                    circuitEvent.Z, (int)circuitEvent.MountingFace,
                    unchecked((int)circuitEvent.Value));
                m_clientChunkBoundaries[new ChunkKey(circuitEvent.X >> 4,
                    circuitEvent.Z >> 4)] = new AppliedBoundary(circuitEvent.HostCircuitStep,
                    circuitEvent.RequiredTerrainSequence, m_lastEventSequence,
                    m_timelineGeneration);
            }
        }

        // Source: Mod/SuMultiplayer/Protocol/circuit.proto:CircuitEvent.sequence
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
            if (result.RequiredTerrainSequence > m_appliedTerrainSequence() ||
                result.TimelineGeneration > m_timelineGeneration)
            {
                if (!m_deferredOperationResults.Any(item => item.RequestId == result.RequestId) &&
                    m_deferredOperationResults.Count < MaximumPendingOperations)
                    m_deferredOperationResults.Add(result.Clone());
                return;
            }
            if (result.RequiredTerrainSequence < m_appliedTerrainSequence())
            {
                m_pendingOperations.Remove(result.RequestId);
                m_presentation.RejectPresentation(unchecked((long)result.RequestId), null);
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
            if (result.Accepted)
            {
                if (!value.HasValue)
                {
                    Warn("Accepted circuit operation result has no authoritative cell value.");
                    m_presentation.RejectPresentation(unchecked((long)result.RequestId), null);
                }
                else
                    m_presentation.ResolvePresentation(unchecked((long)result.RequestId), value.Value);
            }
            else
                m_presentation.RejectPresentation(unchecked((long)result.RequestId), value);
        }

        private void ProcessDeferredAuthoritativeMessages()
        {
            for (int i = m_deferredOperationResults.Count - 1; i >= 0; i--)
            {
                CircuitOperationResult result = m_deferredOperationResults[i];
                if (result.RequiredTerrainSequence > m_appliedTerrainSequence()) continue;
                m_deferredOperationResults.RemoveAt(i);
                ApplyOperationResult(result);
            }
            for (int i = 0; i < m_deferredEvents.Count;)
            {
                CircuitEvent circuitEvent = m_deferredEvents[i];
                if (circuitEvent.RequiredTerrainSequence > m_appliedTerrainSequence())
                {
                    i++;
                    continue;
                }
                m_deferredEvents.RemoveAt(i);
                var batch = new CircuitEventBatch
                {
                    Epoch = m_epoch,
                    HostCircuitStep = circuitEvent.HostCircuitStep
                };
                batch.Events.Add(circuitEvent);
                ApplyEventBatch(batch);
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

        private void ResetForSessionChange()
        {
            string current = m_transport.SessionId ?? string.Empty;
            // The primary TCP link may be down for a few frames while the
            // circuit link reconnects. Keep the current host session and epoch
            // across that transient gap; an empty id is not a new room.
            if (string.IsNullOrEmpty(current)) return;
            if (string.Equals(current, m_sessionId, StringComparison.Ordinal)) return;
            m_sessionId = current;
            // Only the host creates a new circuit epoch. A client must not advance
            // an epoch locally when the transport session changes; it waits for
            // the host's first CircuitClock after the reconnect.
            m_epoch = m_transport.IsHosting
                ? (m_epoch == uint.MaxValue ? 1u : m_epoch + 1u)
                : 0u;
            m_nextEventSequence = 0;
            m_lastEventSequence = 0;
            m_lastCircuitStep = 0;
            m_clientScopeRequested = false;
            m_wasCircuitAttached = false;
            m_peerStates.Clear();
            m_pendingOperations.Clear();
            m_snapshotAssemblies.Clear();
            m_clientChunkBoundaries.Clear();
            m_deferredOperationResults.Clear();
            m_deferredEvents.Clear();
            m_world.SetDeterministicRandomSeed(0, hostAuthority: false);
            m_pendingClock = null;
            m_hasConfiguredRandomSeed = false;
            m_randomSeedAuthority = false;
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
