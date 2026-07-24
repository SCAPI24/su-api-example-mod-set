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

        private static readonly ConcurrentDictionary<long, GameModifiedCellsMessage>
            m_receivedSequencedBatches =
                new ConcurrentDictionary<long, GameModifiedCellsMessage>();
        private static readonly ConcurrentQueue<GameModifiedCellsMessage> m_receivedRepairs =
            new ConcurrentQueue<GameModifiedCellsMessage>();
        private static readonly ConcurrentQueue<GameModifiedCellsMessage>
            m_receivedPriorityRepairs = new ConcurrentQueue<GameModifiedCellsMessage>();
        private static int m_networkStateGeneration;
        private readonly Dictionary<Point3, int> m_networkReceivedCellValues =
            new Dictionary<Point3, int>();
        private readonly Dictionary<Point3, int> m_appliedCellTicks = new Dictionary<Point3, int>();
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
        private Dictionary<Point3, bool> m_modifiedCells;
        private bool m_isInitialized;

        public static int LastAppliedTerrainTick { get; private set; }

        public static long LastAppliedTerrainSequence { get; private set; }

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

        public static void ResetNetworkState()
        {
            m_receivedSequencedBatches.Clear();
            while (m_receivedRepairs.TryDequeue(out _))
            {
            }
            while (m_receivedPriorityRepairs.TryDequeue(out _))
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
                m_appliedCellTicks.Clear();
                m_observedNetworkStateGeneration = generation;
            }

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
                    if (!m_appliedCellTicks.TryGetValue(item.Key, out int appliedTick) ||
                        m_activeNetworkBatch.Tick >= appliedTick)
                    {
                        m_appliedCellTicks[item.Key] = m_activeNetworkBatch.Tick;
                        int networkValue = m_activeNetworkBatch.CellValues[valueIndex];
                        m_networkReceivedCellValues[item.Key] = networkValue;
                        ChangeCell(item.Key.X, item.Key.Y, item.Key.Z,
                            networkValue, true);
                        ForceNetworkCellGeometry(item.Key);
                    }
                }

                if (m_activeNetworkCellIndex >= m_activeNetworkCells.Length)
                    CompleteActiveNetworkBatch();
            }
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

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        private void ApplyPriorityNetworkBatches()
        {
            int remainingBatches = 128;
            while (remainingBatches-- > 0 &&
                m_receivedPriorityRepairs.TryDequeue(out GameModifiedCellsMessage message))
            {
                KeyValuePair<Point3, bool>[] cells = message.ModifiedCells?.ToArray() ??
                    Array.Empty<KeyValuePair<Point3, bool>>();
                for (int i = 0; i < cells.Length; i++)
                {
                    if (message.CellValues == null || i >= message.CellValues.Count)
                        continue;
                    Point3 point = cells[i].Key;
                    if (m_appliedCellTicks.TryGetValue(point, out int appliedTick) &&
                        message.Tick < appliedTick)
                        continue;
                    m_appliedCellTicks[point] = message.Tick;
                    int networkValue = message.CellValues[i];
                    m_networkReceivedCellValues[point] = networkValue;
                    ChangeCell(point.X, point.Y, point.Z, networkValue, true);
                    ForceNetworkCellGeometry(point);
                }
                LastAppliedTerrainTick = Math.Max(LastAppliedTerrainTick, message.Tick);
            }
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
            ClearActiveNetworkBatch();
        }

        private void ClearActiveNetworkBatch()
        {
            m_activeNetworkBatch = null;
            m_activeNetworkCells = Array.Empty<KeyValuePair<Point3, bool>>();
            m_activeNetworkCellIndex = 0;
            m_activeNetworkBatchIsSequenced = false;
        }
    }
}
