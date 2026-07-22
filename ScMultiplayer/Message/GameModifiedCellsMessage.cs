using Engine;
using Game;
using System;
using System.Collections.Generic;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public class GameModifiedCellsMessage : Message
    {
        public Dictionary<Point3, bool> ModifiedCells;
        public List<int> CellValues;
        public int Tick;
        public long Sequence;
        public bool IsCatchUp;
        public int TargetClientId = -1;

        public GameModifiedCellsMessage()
        {
        }

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ChangeCell
        public GameModifiedCellsMessage(Dictionary<Point3, bool> modifiedCells, int tick = 0,
            bool isCatchUp = false, int targetClientId = -1)
        {
            ModifiedCells = new Dictionary<Point3, bool>(modifiedCells);
            Tick = tick;
            IsCatchUp = isCatchUp;
            TargetClientId = targetClientId;
            CellValues = new List<int>(ModifiedCells.Count);
            SubsystemTerrain terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(true);
            foreach (KeyValuePair<Point3, bool> item in ModifiedCells)
            {
                int value = terrain.Terrain.GetCellValueFast(item.Key.X, item.Key.Y, item.Key.Z);
                CellValues.Add(Terrain.ReplaceLight(value, 0));
            }
        }

        public GameModifiedCellsMessage(Dictionary<Point3, bool> modifiedCells, List<int> cellValues,
            int tick, bool isCatchUp, int targetClientId, long sequence = 0)
        {
            ModifiedCells = new Dictionary<Point3, bool>(modifiedCells);
            CellValues = new List<int>(cellValues);
            Tick = tick;
            Sequence = sequence;
            IsCatchUp = isCatchUp;
            TargetClientId = targetClientId;
        }

        protected override void Read(SuReader reader)
        {
            Tick = reader.ReadInt32();
            IsCatchUp = reader.ReadBoolean();
            TargetClientId = reader.ReadInt32();
            Sequence = reader.ReadInt64();
            int count = reader.ReadPackedInt32();
            ModifiedCells = new Dictionary<Point3, bool>(count);
            CellValues = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                Point3 point = reader.ReadPoint3();
                ModifiedCells[point] = reader.ReadBoolean();
                CellValues.Add(reader.ReadInt32());
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(Tick);
            writer.WriteBoolean(IsCatchUp);
            writer.WriteInt32(TargetClientId);
            writer.WriteInt64(Sequence);
            writer.WritePackedInt32(ModifiedCells?.Count ?? 0);
            if (ModifiedCells == null) return;

            int index = 0;
            foreach (KeyValuePair<Point3, bool> item in ModifiedCells)
            {
                writer.WritePoint3(item.Key);
                writer.WriteBoolean(item.Value);
                writer.WriteInt32(CellValues != null && index < CellValues.Count ? CellValues[index] : 0);
                index++;
            }
        }
    }
}
