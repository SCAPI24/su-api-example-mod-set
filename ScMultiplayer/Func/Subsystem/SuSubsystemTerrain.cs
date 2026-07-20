using Engine;
using Game;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ScMultiplayer
{
    public class SuSubsystemTerrain : SubsystemTerrain, IUpdateable
    {
        private static readonly ConcurrentQueue<GameModifiedCellsMessage> m_receivedBatches =
            new ConcurrentQueue<GameModifiedCellsMessage>();
        private readonly HashSet<Point3> m_networkReceivedCells = new HashSet<Point3>();
        private readonly Dictionary<Point3, int> m_appliedCellTicks = new Dictionary<Point3, int>();
        private Dictionary<Point3, bool> m_modifiedCells;
        private bool m_isInitialized;

        public static int LastAppliedTerrainTick { get; private set; }

        public static void EnqueueNetworkBatch(GameModifiedCellsMessage message)
        {
            if (message != null) m_receivedBatches.Enqueue(message);
        }

        public static void ResetNetworkState()
        {
            while (m_receivedBatches.TryDequeue(out _))
            {
            }
            LastAppliedTerrainTick = 0;
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
            m_networkReceivedCells.Clear();
        }

        private void PublishAllModifiedCells()
        {
            if (ScMultiplayer.client?.IsConnected != true || m_modifiedCells.Count == 0)
                return;
            var localChanges = new Dictionary<Point3, bool>();
            foreach (KeyValuePair<Point3, bool> item in m_modifiedCells)
            {
                if (!m_networkReceivedCells.Contains(item.Key))
                    localChanges[item.Key] = item.Value;
            }
            if (localChanges.Count > 0)
                ScMultiplayer.currentInstance?.PublishTerrainChanges(localChanges);
        }

        private void ApplyNetworkBatches()
        {
            while (m_receivedBatches.TryDequeue(out GameModifiedCellsMessage message))
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
                            m_networkReceivedCells.Add(item.Key);
                            ChangeCell(item.Key.X, item.Key.Y, item.Key.Z, message.CellValues[index], true);
                        }
                    }
                    index++;
                }
                LastAppliedTerrainTick = System.Math.Max(LastAppliedTerrainTick, message.Tick);
            }
        }
    }
}
