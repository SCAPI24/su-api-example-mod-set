using Engine;
using Game;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ScMultiplayer
{
    public class SuSubsystemTerrain : SubsystemTerrain, IUpdateable
    {
        private static readonly ConcurrentDictionary<long, GameModifiedCellsMessage>
            m_receivedSequencedBatches =
                new ConcurrentDictionary<long, GameModifiedCellsMessage>();
        private static readonly ConcurrentQueue<GameModifiedCellsMessage> m_receivedRepairs =
            new ConcurrentQueue<GameModifiedCellsMessage>();
        private readonly Dictionary<Point3, int> m_networkReceivedCellValues =
            new Dictionary<Point3, int>();
        private readonly Dictionary<Point3, int> m_appliedCellTicks = new Dictionary<Point3, int>();
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
            LastAppliedTerrainTick = 0;
            LastAppliedTerrainSequence = 0;
        }

        void IUpdateable.Update(float dt)
        {
            if (!m_isInitialized)
            {
                // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.m_modifiedCells
                m_modifiedCells = Game.Program.ModManager.ModParentField
                    .GetParentField<Dictionary<Point3, bool>>(this, "m_modifiedCells", base.GetType());
                if (m_modifiedCells == null)
                {
                    base.Update(dt);
                    return;
                }
                m_isInitialized = true;
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
        }

        private void PublishAllModifiedCells()
        {
            if (ScMultiplayer.client?.IsConnected != true || m_modifiedCells.Count == 0)
                return;
            var localChanges = new Dictionary<Point3, bool>();
            foreach (KeyValuePair<Point3, bool> item in m_modifiedCells)
            {
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

        private void ApplyNetworkBatches()
        {
            while (m_receivedSequencedBatches.TryRemove(
                LastAppliedTerrainSequence + 1, out GameModifiedCellsMessage message))
            {
                ApplyNetworkBatch(message);
                LastAppliedTerrainSequence = message.Sequence;
            }
            while (m_receivedRepairs.TryDequeue(out GameModifiedCellsMessage repair))
                ApplyNetworkBatch(repair);
        }

        private void ApplyNetworkBatch(GameModifiedCellsMessage message)
        {
            int index = 0;
            foreach (KeyValuePair<Point3, bool> item in message.ModifiedCells)
            {
                if (message.CellValues != null && index < message.CellValues.Count)
                {
                    if (!m_appliedCellTicks.TryGetValue(item.Key, out int appliedTick) ||
                        message.Tick >= appliedTick)
                    {
                        m_appliedCellTicks[item.Key] = message.Tick;
                        int networkValue = message.CellValues[index];
                        m_networkReceivedCellValues[item.Key] = networkValue;
                        ChangeCell(item.Key.X, item.Key.Y, item.Key.Z, networkValue, true);
                    }
                }
                index++;
            }
            LastAppliedTerrainTick = System.Math.Max(LastAppliedTerrainTick, message.Tick);
        }
    }
}
