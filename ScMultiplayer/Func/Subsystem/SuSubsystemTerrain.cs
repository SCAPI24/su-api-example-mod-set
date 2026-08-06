using Engine;
using Game;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ScMultiplayer
{
    public class SuSubsystemTerrain : SubsystemTerrain, IUpdateable
    {
        private const int MaximumNetworkTerrainCellsPerFrame = 128;
        // Source: ScMultiplayer.cs:ScMultiplayer.ProcessEndOfFrameActions
        // Checkpoint input is now dispatched above the 40-request/s producer rate. Keep its
        // landing budget above the old 64-cell equilibrium so a burst can drain without creating
        // another verification round, while the normal terrain budget remains unchanged.
        private const int MaximumNetworkChunkCheckpointCellsPerFrame = 128;
        private const int MaximumDeferredNetworkTerrainCells = 8192;
        private const int MaximumDeferredNetworkRetriesPerFrame = 128;
        private const double DeferredNetworkCellRetryInterval = 0.1;

        private static readonly ConcurrentDictionary<long, GameModifiedCellsMessage>
            m_receivedSequencedBatches =
                new ConcurrentDictionary<long, GameModifiedCellsMessage>();
        private static readonly ConcurrentQueue<GameModifiedCellsMessage> m_receivedRepairs =
            new ConcurrentQueue<GameModifiedCellsMessage>();
        private static readonly ConcurrentQueue<GameModifiedCellsMessage>
            m_receivedPriorityRepairs = new ConcurrentQueue<GameModifiedCellsMessage>();
        // Source: ScMultiplayer.cs:ScMultiplayer.HandleTerrainChunkSyncMessage
        // Chunk checkpoints are authoritative snapshots and must not occupy the generic repair
        // queue, whose per-frame work is shared with live terrain changes.
        private static readonly ConcurrentQueue<GameModifiedCellsMessage>
            m_receivedChunkCheckpoints = new ConcurrentQueue<GameModifiedCellsMessage>();
        private static int m_networkStateGeneration;
        private static volatile bool m_sequenceBatchInProgress;
        private readonly Dictionary<Point3, int> m_networkReceivedCellValues =
            new Dictionary<Point3, int>();
        private readonly Dictionary<Point3, int> m_appliedCellTicks = new Dictionary<Point3, int>();
        private readonly Dictionary<Point3, long> m_appliedCellSequences =
            new Dictionary<Point3, long>();
        private sealed class DeferredNetworkCell
        {
            public bool IsModified;
            public int CellValue;
            public int Tick;
            public long Sequence;
            public PendingChunkCheckpointBatch CheckpointBatch;
            public double NextRetryTime;
            public bool IsRetryQueued;
        }

        private sealed class PendingChunkCheckpointBatch
        {
            public Point2 Coordinates;
            public long Revision;
            public int RemainingCells;
            public bool Finished;
        }

        private readonly Dictionary<Point3, DeferredNetworkCell> m_deferredNetworkCells =
            new Dictionary<Point3, DeferredNetworkCell>();
        private readonly Queue<Point3> m_deferredNetworkCellRetryQueue =
            new Queue<Point3>();
        private readonly HashSet<Point3> m_pendingNetworkGeometryCells =
            new HashSet<Point3>();
        private readonly HashSet<Point3> m_clientCircuitBaselineCells =
            new HashSet<Point3>();
        private readonly HashSet<Point3> m_clientCircuitGeneratedCells =
            new HashSet<Point3>();
        private readonly HashSet<Point3> m_invalidatedNetworkGeometrySlices =
            new HashSet<Point3>();
        private GameModifiedCellsMessage m_activeNetworkBatch;
        private KeyValuePair<Point3, bool>[] m_activeNetworkCells =
            Array.Empty<KeyValuePair<Point3, bool>>();
        private int m_activeNetworkCellIndex;
        private int m_observedNetworkStateGeneration;
        private bool m_activeNetworkBatchIsSequenced;
        private GameModifiedCellsMessage m_activeChunkCheckpoint;
        private KeyValuePair<Point3, bool>[] m_activeChunkCheckpointCells =
            Array.Empty<KeyValuePair<Point3, bool>>();
        private int m_activeChunkCheckpointCellIndex;
        private PendingChunkCheckpointBatch m_activeChunkCheckpointBatch;
        private Dictionary<Point3, bool> m_modifiedCells;
        private bool m_isInitialized;

        public static int LastAppliedTerrainTick { get; private set; }

        public static long LastAppliedTerrainSequence { get; private set; }

        internal static int PendingChunkCheckpointCount => m_receivedChunkCheckpoints.Count;

        public static void EnqueueNetworkBatch(GameModifiedCellsMessage message)
        {
            if (message == null) return;
            if (message.Sequence > 0)
            {
                if (message.Sequence > LastAppliedTerrainSequence)
                    m_receivedSequencedBatches.TryAdd(message.Sequence, message);
            }
            else
            {
                m_receivedRepairs.Enqueue(message);
            }
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        // Direct action results must not wait behind a continuous stream of terrain sequences.
        public static void EnqueuePriorityNetworkBatch(GameModifiedCellsMessage message)
        {
            if (message == null) return;
            if (message.ChunkCheckpointCoordinates.HasValue &&
                message.ChunkCheckpointRevision > 0)
            {
                m_receivedChunkCheckpoints.Enqueue(message);
                return;
            }
            if ((message.ModifiedCells?.Count ?? 0) <= 8)
                m_receivedPriorityRepairs.Enqueue(message);
            else
                m_receivedRepairs.Enqueue(message);
        }

        // Source: ScMultiplayer.cs:ScMultiplayer.HandleGamePakWorldMessage
        public static void ConfigureTerrainSequence(long baseline)
        {
            ResetNetworkState();
            LastAppliedTerrainSequence = System.Math.Max(baseline, 0L);
        }

        // Source: ScMultiplayer.cs:ScMultiplayer.SendClientTerrainRecoveryRequest
        public static List<TerrainSequenceRange> GetBufferedSequenceRanges(
            long afterSequence, int maximumRanges)
        {
            var result = new List<TerrainSequenceRange>();
            long rangeStart = 0;
            long previous = 0;
            foreach (long sequence in m_receivedSequencedBatches.Keys
                .Where(value => value > afterSequence).OrderBy(value => value))
            {
                if (rangeStart == 0)
                {
                    rangeStart = sequence;
                    previous = sequence;
                    continue;
                }
                if (sequence == previous + 1)
                {
                    previous = sequence;
                    continue;
                }
                result.Add(new TerrainSequenceRange(rangeStart, previous));
                if (result.Count >= maximumRanges) return result;
                rangeStart = previous = sequence;
            }
            if (rangeStart > 0 && result.Count < maximumRanges)
                result.Add(new TerrainSequenceRange(rangeStart, previous));
            return result;
        }

        public static bool HasBufferedSequenceGap()
        {
            if (m_receivedSequencedBatches.IsEmpty) return false;
            long next = LastAppliedTerrainSequence + 1;
            return !m_receivedSequencedBatches.ContainsKey(next) &&
                m_receivedSequencedBatches.Keys.Any(sequence => sequence > next);
        }

        public static bool HasPendingSequenceWork()
        {
            return m_sequenceBatchInProgress ||
                m_receivedSequencedBatches.ContainsKey(LastAppliedTerrainSequence + 1);
        }

        public static void ResetNetworkState()
        {
            m_receivedSequencedBatches.Clear();
            while (m_receivedRepairs.TryDequeue(out _))
            {
            }
            while (m_receivedPriorityRepairs.TryDequeue(out _))
            {
            }
            while (m_receivedChunkCheckpoints.TryDequeue(out _))
            {
            }
            LastAppliedTerrainTick = 0;
            LastAppliedTerrainSequence = 0;
            Interlocked.Increment(ref m_networkStateGeneration);
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
        internal void BeginClientCircuitStep()
        {
            if (!EnsureModifiedCellsBound()) return;
            m_clientCircuitBaselineCells.Clear();
            foreach (Point3 point in m_modifiedCells.Keys)
                m_clientCircuitBaselineCells.Add(point);
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
        internal void EndClientCircuitStep()
        {
            if (!EnsureModifiedCellsBound()) return;
            foreach (Point3 point in m_modifiedCells.Keys)
            {
                if (!m_clientCircuitBaselineCells.Contains(point))
                    m_clientCircuitGeneratedCells.Add(point);
            }
            m_clientCircuitBaselineCells.Clear();
        }

        void IUpdateable.Update(float dt)
        {
            if (!m_isInitialized)
            {
                if (!EnsureModifiedCellsBound())
                {
                    base.Update(dt);
                    return;
                }
            }

            ApplyNetworkBatches();

            // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.Update
            // TerrainUpdater runs fluid, electricity, fire, piston, weather and pollable block
            // behavior changes. Capture the modification dictionary after it completes, before
            // ProcessModifiedCells clears it, so every ChangeCell/DestroyCell result is sent.
            TerrainUpdater.Update();
            PublishAllModifiedCells();
            ProcessModifiedCells();
            // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ProcessModifiedCells
            // Support removal can destroy a mounted circuit element while neighbor callbacks are
            // running. Publish and process that derived terrain wave before another subsystem can
            // clear it, while keeping the native neighbor algorithm authoritative on the host.
            if (m_modifiedCells.Count > 0)
            {
                PublishAllModifiedCells();
                ProcessModifiedCells();
            }
            m_networkReceivedCellValues.Clear();
            m_clientCircuitGeneratedCells.Clear();
            m_clientCircuitBaselineCells.Clear();
        }

        private void PublishAllModifiedCells()
        {
            if (ScMultiplayer.client?.IsConnected != true || m_modifiedCells.Count == 0)
                return;
            var localChanges = new Dictionary<Point3, bool>();
            foreach (KeyValuePair<Point3, bool> item in m_modifiedCells)
            {
                if (!ScMultiplayer.IsHost &&
                    m_clientCircuitGeneratedCells.Contains(item.Key))
                    continue;
                int currentValue = Terrain.GetCellValue(item.Key.X, item.Key.Y, item.Key.Z);
                bool hasNetworkValue = m_networkReceivedCellValues.TryGetValue(
                    item.Key, out int networkValue);
                bool isLocalChange = !hasNetworkValue || currentValue != networkValue;
                if (isLocalChange)
                    localChanges[item.Key] = item.Value;
            }
            if (localChanges.Count > 0)
                ScMultiplayer.currentInstance?.PublishTerrainChanges(localChanges);
        }

        private bool EnsureModifiedCellsBound()
        {
            if (m_modifiedCells != null) return true;
            try
            {
                // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.m_modifiedCells
                m_modifiedCells = Game.Program.ModManager.ModParentField
                    .GetParentField<Dictionary<Point3, bool>>(this, "m_modifiedCells",
                        typeof(SubsystemTerrain));
                m_isInitialized = m_modifiedCells != null;
            }
            catch
            {
                m_modifiedCells = null;
                m_isInitialized = false;
            }
            return m_modifiedCells != null;
        }

        private void ApplyNetworkBatches()
        {
            m_invalidatedNetworkGeometrySlices.Clear();
            int generation = Volatile.Read(ref m_networkStateGeneration);
            if (m_observedNetworkStateGeneration != generation)
            {
                ClearActiveNetworkBatch();
                ClearActiveChunkCheckpointBatch();
                m_appliedCellTicks.Clear();
                m_appliedCellSequences.Clear();
                m_deferredNetworkCells.Clear();
                m_deferredNetworkCellRetryQueue.Clear();
                m_pendingNetworkGeometryCells.Clear();
                m_observedNetworkStateGeneration = generation;
            }

            ApplyDeferredNetworkCells();
            ApplyChunkCheckpointBatches();
            ApplyPriorityNetworkBatches();

            // Source: Mod/ScMultiplayer/Func/Subsystem/SuSubsystemTerrain.cs:
            // SuSubsystemTerrain.ApplyNetworkBatch
            // Large terrain broadcasts are spread across frames to keep dense seasonal or
            // explosive changes from monopolizing one render frame.
            int remainingCells = MaximumNetworkTerrainCellsPerFrame;
            while (remainingCells > 0)
            {
                if (m_activeNetworkBatch == null && !TryStartNextNetworkBatch())
                    break;

                while (remainingCells > 0 &&
                    m_activeNetworkCellIndex < m_activeNetworkCells.Length)
                {
                    KeyValuePair<Point3, bool> item =
                        m_activeNetworkCells[m_activeNetworkCellIndex];
                    int valueIndex = m_activeNetworkCellIndex++;
                    remainingCells--;
                    if (m_activeNetworkBatch.CellValues == null ||
                        valueIndex >= m_activeNetworkBatch.CellValues.Count)
                    {
                        continue;
                    }
                    int networkValue = m_activeNetworkBatch.CellValues[valueIndex];
                    if (!TryApplyNetworkCell(item.Key, networkValue,
                        m_activeNetworkBatch.Tick, m_activeNetworkBatch.Sequence))
                    {
                        DeferNetworkCell(item.Key, item.Value,
                            networkValue, m_activeNetworkBatch.Tick,
                            m_activeNetworkBatch.Sequence);
                    }
                }

                if (m_activeNetworkCellIndex >= m_activeNetworkCells.Length)
                    CompleteActiveNetworkBatch();
            }
            FlushPendingNetworkGeometry();
            m_invalidatedNetworkGeometrySlices.Clear();
        }

        private bool TryStartNextNetworkBatch()
        {
            long nextSequence = LastAppliedTerrainSequence + 1;
            if (m_receivedSequencedBatches.TryGetValue(
                nextSequence, out GameModifiedCellsMessage sequenced))
            {
                StartNetworkBatch(sequenced, isSequenced: true);
                return true;
            }
            if (m_receivedRepairs.TryDequeue(out GameModifiedCellsMessage repair))
            {
                StartNetworkBatch(repair, isSequenced: false);
                return true;
            }
            return false;
        }

        // Source: ScMultiplayer.cs:ScMultiplayer.HandleTerrainChunkSyncMessage
        // A checkpoint is spread by cells across frames. Its completion callback remains tied to
        // the same batch, so the client never acknowledges a revision before every cell lands.
        private void ApplyChunkCheckpointBatches()
        {
            int remainingCells = MaximumNetworkChunkCheckpointCellsPerFrame;
            while (remainingCells > 0)
            {
                if (m_activeChunkCheckpoint == null)
                {
                    if (!m_receivedChunkCheckpoints.TryDequeue(
                        out GameModifiedCellsMessage checkpoint))
                        break;
                    m_activeChunkCheckpoint = checkpoint;
                    m_activeChunkCheckpointCells = checkpoint.ModifiedCells?.ToArray() ??
                        Array.Empty<KeyValuePair<Point3, bool>>();
                    m_activeChunkCheckpointCellIndex = 0;
                    int count = Math.Min(m_activeChunkCheckpointCells.Length,
                        checkpoint.CellValues?.Count ?? 0);
                    m_activeChunkCheckpointBatch = checkpoint.ChunkCheckpointCoordinates.HasValue &&
                        checkpoint.ChunkCheckpointRevision > 0
                        ? new PendingChunkCheckpointBatch
                        {
                            Coordinates = checkpoint.ChunkCheckpointCoordinates.Value,
                            Revision = checkpoint.ChunkCheckpointRevision,
                            RemainingCells = count
                        }
                        : null;
                    if (m_activeChunkCheckpointBatch != null && count == 0)
                        CompleteCheckpointBatch(m_activeChunkCheckpointBatch, applied: true);
                }

                int countToApply = Math.Min(remainingCells,
                    m_activeChunkCheckpointCells.Length - m_activeChunkCheckpointCellIndex);
                while (countToApply-- > 0)
                {
                    int valueIndex = m_activeChunkCheckpointCellIndex++;
                    KeyValuePair<Point3, bool> item =
                        m_activeChunkCheckpointCells[valueIndex];
                    remainingCells--;
                    if (m_activeChunkCheckpoint.CellValues == null ||
                        valueIndex >= m_activeChunkCheckpoint.CellValues.Count)
                        continue;
                    int networkValue = m_activeChunkCheckpoint.CellValues[valueIndex];
                    if (!TryApplyNetworkCell(item.Key, networkValue,
                        m_activeChunkCheckpoint.Tick, m_activeChunkCheckpoint.Sequence,
                        isChunkCheckpoint: true))
                    {
                        DeferNetworkCell(item.Key, item.Value, networkValue,
                            m_activeChunkCheckpoint.Tick, m_activeChunkCheckpoint.Sequence,
                            m_activeChunkCheckpointBatch);
                    }
                    else
                    {
                        CompleteCheckpointBatchCell(m_activeChunkCheckpointBatch, applied: true);
                    }
                }

                if (m_activeChunkCheckpointCellIndex < m_activeChunkCheckpointCells.Length)
                    break;

                m_activeChunkCheckpoint = null;
                m_activeChunkCheckpointCells = Array.Empty<KeyValuePair<Point3, bool>>();
                m_activeChunkCheckpointCellIndex = 0;
                m_activeChunkCheckpointBatch = null;
            }
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        private void ApplyPriorityNetworkBatches()
        {
            int remainingBatches = 128;
            while (remainingBatches-- > 0 &&
                m_receivedPriorityRepairs.TryDequeue(out GameModifiedCellsMessage message))
            {
                KeyValuePair<Point3, bool>[] cells = message.ModifiedCells?.ToArray() ??
                    Array.Empty<KeyValuePair<Point3, bool>>();
                int count = Math.Min(cells.Length, message.CellValues?.Count ?? 0);
                PendingChunkCheckpointBatch checkpointBatch =
                    message.ChunkCheckpointCoordinates.HasValue &&
                    message.ChunkCheckpointRevision > 0
                    ? new PendingChunkCheckpointBatch
                    {
                        Coordinates = message.ChunkCheckpointCoordinates.Value,
                        Revision = message.ChunkCheckpointRevision,
                        RemainingCells = count
                    }
                    : null;
                if (checkpointBatch != null && count == 0)
                    CompleteCheckpointBatch(checkpointBatch, applied: true);
                for (int i = 0; i < count; i++)
                {
                    Point3 point = cells[i].Key;
                    int networkValue = message.CellValues[i];
                    bool isChunkCheckpoint = checkpointBatch != null;
                    if (!TryApplyNetworkCell(point, networkValue, message.Tick,
                        message.Sequence, isChunkCheckpoint))
                    {
                        DeferNetworkCell(point, cells[i].Value, networkValue,
                            message.Tick, message.Sequence, checkpointBatch);
                    }
                    else
                    {
                        CompleteCheckpointBatchCell(checkpointBatch, applied: true);
                    }
                }
                LastAppliedTerrainTick = Math.Max(LastAppliedTerrainTick, message.Tick);
            }
        }

        // Source: ScMultiplayer.ScMultiplayer.HandleTerrainChunkSyncMessage
        // A Chunk revision is usable only after every checkpoint cell has reached terrain storage.
        // Failed or displaced deferred entries intentionally leave the revision unconfirmed so the
        // owner schedules another compact authoritative checkpoint.
        private static void CompleteCheckpointBatchCell(PendingChunkCheckpointBatch batch,
            bool applied)
        {
            if (batch == null || batch.Finished) return;
            if (!applied)
            {
                CompleteCheckpointBatch(batch, applied: false);
                return;
            }
            batch.RemainingCells--;
            if (batch.RemainingCells <= 0)
                CompleteCheckpointBatch(batch, applied: true);
        }

        private static void CompleteCheckpointBatch(PendingChunkCheckpointBatch batch,
            bool applied)
        {
            if (batch == null || batch.Finished) return;
            batch.Finished = true;
            ScMultiplayer.currentInstance?.OnClientTerrainChunkCheckpointBatchApplied(
                batch.Coordinates, batch.Revision, applied);
        }

        private void ApplyDeferredNetworkCells()
        {
            if (m_deferredNetworkCells.Count == 0) return;
            int attempts = Math.Min(MaximumDeferredNetworkRetriesPerFrame,
                m_deferredNetworkCellRetryQueue.Count);
            double now = Time.RealTime;
            while (attempts-- > 0 && m_deferredNetworkCellRetryQueue.Count > 0)
            {
                Point3 point = m_deferredNetworkCellRetryQueue.Dequeue();
                if (!m_deferredNetworkCells.TryGetValue(point, out DeferredNetworkCell deferred))
                    continue;
                deferred.IsRetryQueued = false;
                if (now < deferred.NextRetryTime)
                {
                    QueueDeferredNetworkCellRetry(point, deferred);
                    continue;
                }
                if (!TryApplyNetworkCell(point, deferred.CellValue, deferred.Tick,
                    deferred.Sequence, deferred.CheckpointBatch != null))
                {
                    deferred.NextRetryTime = now + DeferredNetworkCellRetryInterval;
                    QueueDeferredNetworkCellRetry(point, deferred);
                    continue;
                }
                m_deferredNetworkCells.Remove(point);
                CompleteCheckpointBatchCell(deferred.CheckpointBatch, applied: true);
            }
        }

        private bool TryApplyNetworkCell(Point3 point, int networkValue, int tick,
            long sequence, bool isChunkCheckpoint = false)
        {
            // Terrain.SetCellValueFast silently ignores an unloaded chunk. Do not mark the
            // network cell as applied until its chunk exists, otherwise a later chunk load
            // restores the stale snapshot permanently.
            TerrainChunk chunk = Terrain.GetChunkAtCell(point.X, point.Z);
            if (chunk == null || chunk.State < TerrainChunkState.Valid)
                return false;
            // Source: ScMultiplayer.HandleTerrainChunkSyncMessage
            // A directed chunk checkpoint is the host's final value for this cell. It must
            // overwrite local bookkeeping from an earlier live sequence; otherwise a received
            // but unsuccessfully applied update can make its own repair a no-op forever.
            if (!isChunkCheckpoint && sequence > 0 && m_appliedCellSequences.TryGetValue(point,
                    out long appliedSequence) && sequence <= appliedSequence)
                return true;
            if (!isChunkCheckpoint && m_appliedCellTicks.TryGetValue(point, out int appliedTick) &&
                tick < appliedTick)
                return true;
            if (sequence > 0)
                m_appliedCellSequences[point] = sequence;
            m_appliedCellTicks[point] = tick;
            m_networkReceivedCellValues[point] = networkValue;
            // Source: Survivalcraft/Game/SubsystemCollapsingBlockBehavior.cs:
            // SubsystemCollapsingBlockBehavior.MovingBlocksStopped
            // The authoritative landing cell owns the final terrain value. Remove a matching
            // client-side falling prediction before it can write the same column again.
            ScMultiplayer.currentInstance?.ReconcileAuthoritativeCollapsingCell(
                point, networkValue);
            ChangeCell(point.X, point.Y, point.Z, networkValue, true);
            int appliedValue = Terrain.ReplaceLight(
                Terrain.GetCellValue(point.X, point.Y, point.Z), 0);
            if (appliedValue != Terrain.ReplaceLight(networkValue, 0))
                ScMultiplayer.currentInstance?.OnClientTerrainCellApplicationFailed(
                    point, sequence);
            m_pendingNetworkGeometryCells.Add(point);
            LastAppliedTerrainTick = Math.Max(LastAppliedTerrainTick, tick);
            return true;
        }

        private void DeferNetworkCell(Point3 point, bool isModified, int cellValue,
            int tick, long sequence, PendingChunkCheckpointBatch checkpointBatch = null)
        {
            if (m_deferredNetworkCells.TryGetValue(point,
                out DeferredNetworkCell existing) &&
                (sequence < existing.Sequence ||
                sequence == existing.Sequence && tick < existing.Tick))
                return;
            if (existing?.CheckpointBatch != null &&
                !ReferenceEquals(existing.CheckpointBatch, checkpointBatch))
            {
                CompleteCheckpointBatchCell(existing.CheckpointBatch, applied: false);
            }
            DeferredNetworkCell deferred = new DeferredNetworkCell
            {
                IsModified = isModified,
                CellValue = cellValue,
                Tick = tick,
                Sequence = sequence,
                CheckpointBatch = checkpointBatch,
                NextRetryTime = Time.RealTime
            };
            m_deferredNetworkCells[point] = deferred;
            QueueDeferredNetworkCellRetry(point, deferred);
            if (m_deferredNetworkCells.Count <= MaximumDeferredNetworkTerrainCells)
                return;
            Point3 oldest = m_deferredNetworkCells
                .OrderBy(item => item.Value.Sequence)
                .ThenBy(item => item.Value.Tick)
                .First().Key;
            DeferredNetworkCell removed = m_deferredNetworkCells[oldest];
            m_deferredNetworkCells.Remove(oldest);
            CompleteCheckpointBatchCell(removed.CheckpointBatch, applied: false);
            if (removed.CheckpointBatch == null)
                ScMultiplayer.currentInstance?.OnClientTerrainCellApplicationFailed(
                    oldest, removed.Sequence);
        }

        // Source: Survivalcraft/Game/TerrainUpdater.cs:TerrainUpdater.Update
        // Retry only a bounded set of still-unready cells. Polling every deferred cell on every
        // frame delayed normal terrain work and exhausted diagnostics before gameplay began.
        private void QueueDeferredNetworkCellRetry(Point3 point, DeferredNetworkCell deferred)
        {
            if (deferred.IsRetryQueued) return;
            deferred.IsRetryQueued = true;
            m_deferredNetworkCellRetryQueue.Enqueue(point);
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        // ChangeCell can make its Chunk temporarily invalid. Invalidate geometry after all network
        // cells scheduled for this frame are written, so sibling cells do not defer themselves.
        private void FlushPendingNetworkGeometry()
        {
            foreach (Point3 point in m_pendingNetworkGeometryCells)
                ForceNetworkCellGeometry(point);
            m_pendingNetworkGeometryCells.Clear();
        }

        // Source: Survivalcraft/Game/TerrainUpdater.cs:
        // TerrainUpdater.DowngradeChunkNeighborhoodState
        // Network state can already equal the terrain cell while its rendered geometry is stale.
        // Invalidate hashes independently of chunk State, then wake the normal terrain updater.
        internal void ForceNetworkCellGeometry(Point3 point)
        {
            TerrainChunk sourceChunk = Terrain.GetChunkAtCell(point.X, point.Z);
            if (sourceChunk == null) return;
            TerrainUpdater.DowngradeChunkNeighborhoodState(sourceChunk.Coords, 1,
                TerrainChunkState.InvalidLight, forceGeometryRegeneration: false);
            int localX = point.X & 15;
            int localZ = point.Z & 15;
            int slice = MathUtils.Clamp(point.Y >> 4, 0, 15);
            for (int x = -1; x <= 1; x++)
            {
                if (x < 0 && localX != 0 || x > 0 && localX != 15) continue;
                for (int z = -1; z <= 1; z++)
                {
                    if (z < 0 && localZ != 0 || z > 0 && localZ != 15) continue;
                    var coordinates = new Point2(sourceChunk.Coords.X + x,
                        sourceChunk.Coords.Y + z);
                    TerrainChunk chunk = Terrain.GetChunkAtCoords(
                        coordinates.X, coordinates.Y);
                    if (chunk == null) continue;
                    int firstSlice = point.Y > 0 && (point.Y & 15) == 0
                        ? slice - 1
                        : slice;
                    int lastSlice = point.Y < 255 && (point.Y & 15) == 15
                        ? slice + 1
                        : slice;
                    for (int sliceIndex = MathUtils.Max(firstSlice, 0);
                        sliceIndex <= MathUtils.Min(lastSlice, 15); sliceIndex++)
                    {
                        var key = new Point3(coordinates.X, sliceIndex, coordinates.Y);
                        if (!m_invalidatedNetworkGeometrySlices.Add(key)) continue;
                        lock (chunk.Geometry)
                            chunk.Geometry.Slices[sliceIndex].GeometryHash = 0;
                    }
                }
            }
        }

        private void StartNetworkBatch(GameModifiedCellsMessage message, bool isSequenced)
        {
            m_activeNetworkBatch = message;
            m_activeNetworkCells = message.ModifiedCells?.ToArray() ??
                Array.Empty<KeyValuePair<Point3, bool>>();
            m_activeNetworkCellIndex = 0;
            m_activeNetworkBatchIsSequenced = isSequenced;
            if (isSequenced) m_sequenceBatchInProgress = true;
        }

        private void CompleteActiveNetworkBatch()
        {
            GameModifiedCellsMessage message = m_activeNetworkBatch;
            LastAppliedTerrainTick = Math.Max(LastAppliedTerrainTick, message.Tick);
            if (m_activeNetworkBatchIsSequenced)
            {
                m_receivedSequencedBatches.TryRemove(message.Sequence, out _);
                LastAppliedTerrainSequence = message.Sequence;
            }
            else if (message.IsCatchUp && message.HeadSequence > LastAppliedTerrainSequence)
            {
                // Join catch-up is a coalesced snapshot, not a replayable event stream. Advance the
                // custom sequence baseline only after its batch has been applied.
                LastAppliedTerrainSequence = message.HeadSequence;
            }
            ClearActiveNetworkBatch();
        }

        private void ClearActiveNetworkBatch()
        {
            m_activeNetworkBatch = null;
            m_activeNetworkCells = Array.Empty<KeyValuePair<Point3, bool>>();
            m_activeNetworkCellIndex = 0;
            m_activeNetworkBatchIsSequenced = false;
            m_sequenceBatchInProgress = false;
        }

        private void ClearActiveChunkCheckpointBatch()
        {
            m_activeChunkCheckpoint = null;
            m_activeChunkCheckpointCells = Array.Empty<KeyValuePair<Point3, bool>>();
            m_activeChunkCheckpointCellIndex = 0;
            m_activeChunkCheckpointBatch = null;
        }
    }
}
