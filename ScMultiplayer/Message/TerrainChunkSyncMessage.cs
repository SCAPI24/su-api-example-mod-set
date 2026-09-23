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
        // Source: ScMultiplayerTerrainHandlers.cs:SendHostTerrainChunkSync
        // 主机为这次区块校验一共发了几个 Data 批。客户端只有收齐 `TotalBatches` 个批才允许
        // 把区块 revision 推到 `Revision`：可靠有序流会在停顿时丢弃消息
        // （Comms/Comm.cs:RecoverStalledReliableSequence），少收一个批却照样 finalize 会把
        // 这些格子永久钉死（主机认为已同步，后续校验按 `Sequence > knownRevision` 过滤掉）。
        public int TotalBatches;
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
            TotalBatches = reader.ReadPackedInt32();
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
            writer.WritePackedInt32(TotalBatches);
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
