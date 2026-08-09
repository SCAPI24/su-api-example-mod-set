using Engine;
using System;
using System.Collections.Generic;
using Comms;

namespace ScMultiplayer
{
    public enum TerrainChunkSyncStage : byte
    {
        Request,
        Data,
        Complete,
        Interest
    }

    [Serializable]
    public sealed class TerrainChunkSyncMessage : Message
    {
        public TerrainChunkSyncStage Stage;
        public int ChunkX;
        public int ChunkZ;
        public long KnownRevision;
        public long Revision;
        public int ServerTick;
        public int InterestRadius;
        public List<Point3> Cells = new List<Point3>();
        public List<int> CellValues = new List<int>();

        // Source: ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.HandleTerrainChunkSyncMessage
        protected override void Read(SuReader reader)
        {
            Stage = (TerrainChunkSyncStage)reader.ReadByte();
            ChunkX = reader.ReadInt32();
            ChunkZ = reader.ReadInt32();
            KnownRevision = reader.ReadInt64();
            Revision = reader.ReadInt64();
            ServerTick = reader.ReadInt32();
            InterestRadius = reader.ReadPackedInt32();
            int count = reader.ReadPackedInt32();
            if (count < 0 || count > 65536)
                throw new InvalidOperationException("Invalid terrain chunk sync cell count.");
            Cells = new List<Point3>(count);
            CellValues = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                Cells.Add(reader.ReadPoint3());
                CellValues.Add(reader.ReadInt32());
            }
        }

        // Source: ScMultiplayer/Plug/ScMultiplayer.cs:
        // ScMultiplayer.SendHostTerrainChunkSync
        protected override void Write(SuWriter writer)
        {
            writer.WriteByte((byte)Stage);
            writer.WriteInt32(ChunkX);
            writer.WriteInt32(ChunkZ);
            writer.WriteInt64(KnownRevision);
            writer.WriteInt64(Revision);
            writer.WriteInt32(ServerTick);
            writer.WritePackedInt32(InterestRadius);
            int count = Math.Min(Cells?.Count ?? 0, CellValues?.Count ?? 0);
            writer.WritePackedInt32(count);
            for (int i = 0; i < count; i++)
            {
                writer.WritePoint3(Cells[i]);
                writer.WriteInt32(CellValues[i]);
            }
        }
    }
}
