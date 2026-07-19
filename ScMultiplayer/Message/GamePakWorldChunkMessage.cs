using Comms;
using System;

namespace ScMultiplayer
{
    [Serializable]
    public class GamePakWorldChunkMessage : Message
    {
        public int TransferId;
        public int TargetClientId;
        public int ChunkIndex;
        public int ChunkCount;
        public int TotalLength;
        public byte[] Data = Array.Empty<byte>();

        protected override void Read(SuReader reader)
        {
            TransferId = reader.ReadInt32();
            TargetClientId = reader.ReadInt32();
            ChunkIndex = reader.ReadInt32();
            ChunkCount = reader.ReadInt32();
            TotalLength = reader.ReadInt32();
            Data = reader.ReadBytes();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(TransferId);
            writer.WriteInt32(TargetClientId);
            writer.WriteInt32(ChunkIndex);
            writer.WriteInt32(ChunkCount);
            writer.WriteInt32(TotalLength);
            writer.WriteBytes(Data ?? Array.Empty<byte>());
        }
    }
}
