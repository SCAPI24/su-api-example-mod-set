using Comms;
using Engine;
using System;

namespace ScMultiplayer
{
    // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:
    // SubsystemFurnitureBlockBehavior.ScanDesign
    [Serializable]
    public sealed class FurnitureBuildRequestMessage : Message
    {
        public int RequestId;
        public Point3 Start;
        public int Resolution;
        public string Name = string.Empty;
        public byte InteractionMode;
        public int[] Values = Array.Empty<int>();
        public Point3[] SourceCells = Array.Empty<Point3>();
        public int[] SourceValues = Array.Empty<int>();

        protected override void Read(SuReader reader)
        {
            RequestId = reader.ReadInt32();
            Start = reader.ReadPoint3();
            Resolution = reader.ReadPackedInt32();
            Name = reader.ReadString();
            InteractionMode = reader.ReadByte();
            int valuesCount = reader.ReadPackedInt32();
            if (Resolution < 2 || Resolution > 16 || valuesCount < 0 ||
                valuesCount > 4096 || valuesCount != Resolution * Resolution * Resolution ||
                Name == null || Name.Length > 20)
                throw new InvalidOperationException("Invalid furniture design request.");
            Values = new int[valuesCount];
            for (int i = 0; i < valuesCount; i++)
                Values[i] = reader.ReadInt32();
            int sourceCount = reader.ReadPackedInt32();
            if (sourceCount <= 0 || sourceCount > 4096)
                throw new InvalidOperationException("Invalid furniture source count.");
            SourceCells = new Point3[sourceCount];
            SourceValues = new int[sourceCount];
            for (int i = 0; i < sourceCount; i++)
            {
                SourceCells[i] = reader.ReadPoint3();
                SourceValues[i] = reader.ReadInt32();
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(RequestId);
            writer.WritePoint3(Start);
            writer.WritePackedInt32(Resolution);
            writer.WriteString(Name ?? string.Empty);
            writer.WriteByte(InteractionMode);
            int valuesCount = Values?.Length ?? 0;
            writer.WritePackedInt32(valuesCount);
            for (int i = 0; i < valuesCount; i++)
                writer.WriteInt32(Values[i]);
            int sourceCount = Math.Min(SourceCells?.Length ?? 0,
                SourceValues?.Length ?? 0);
            writer.WritePackedInt32(sourceCount);
            for (int i = 0; i < sourceCount; i++)
            {
                writer.WritePoint3(SourceCells[i]);
                writer.WriteInt32(SourceValues[i]);
            }
        }
    }
}
