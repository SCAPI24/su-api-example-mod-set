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

        public GameModifiedCellsMessage() { }

        public GameModifiedCellsMessage(Dictionary<Point3, bool> modifiedCells)
        {
            ModifiedCells = modifiedCells;
            List<int> cellValues = new List<int>();
            var subsystemTerrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(throwOnError: true);
            foreach (var item in ModifiedCells)
            {
                int cellValueFast = Terrain.ReplaceLight(subsystemTerrain.Terrain.GetCellValueFast(item.Key.X, item.Key.Y, item.Key.Z), 0);
                cellValues.Add(cellValueFast);
            }
            CellValues = cellValues;
        }

        protected override void Read(SuReader reader)
        {
            // 读取 ModifiedCells
            int count = reader.ReadPackedInt32();
            ModifiedCells = new Dictionary<Point3, bool>(count);

            for (int i = 0; i < count; i++)
            {
                Point3 point = reader.ReadPoint3();
                bool value = reader.ReadBoolean();
                ModifiedCells[point] = value;
            }

            // 读取 CellValues
            int valuesCount = reader.ReadPackedInt32();
            if (valuesCount > 0)
            {
                CellValues = new List<int>(valuesCount);
                for (int i = 0; i < valuesCount; i++)
                {
                    CellValues.Add(reader.ReadInt32());
                }
            }
            else
            {
                CellValues = null;
            }
        }

        protected override void Write(SuWriter writer)
        {
            // 写入 ModifiedCells
            writer.WritePackedInt32(ModifiedCells.Count);
            foreach (var kvp in ModifiedCells)
            {
                writer.WritePoint3(kvp.Key);
                writer.WriteBoolean(kvp.Value);
            }

            // 写入 CellValues
            if (CellValues != null && CellValues.Count > 0)
            {
                writer.WritePackedInt32(CellValues.Count);
                foreach (int value in CellValues)
                {
                    writer.WriteInt32(value);
                }
            }
            else
            {
                writer.WritePackedInt32(0); // 写入0表示没有值
            }
        }
    }
}