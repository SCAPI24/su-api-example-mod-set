using Comms;
using System;
using System.Net;

namespace ScMultiplayer
{
    [Serializable]
    public class GamePakWorldRepairRequestMessage : Message
    {
        public int TransferId;
        public bool RequestManifest;
        public int HighestContiguousChunkIndex = -1;
        public int HighestReceivedChunkIndex = -1;
        public int[] MissingChunkIndices = Array.Empty<int>();

        protected override void Read(SuReader reader)
        {
            TransferId = reader.ReadInt32();
            RequestManifest = reader.ReadBoolean();
            HighestContiguousChunkIndex = reader.ReadInt32();
            HighestReceivedChunkIndex = reader.ReadInt32();
            int count = reader.ReadPackedInt32();
            if (count < 0 || count > 256)
                throw new ProtocolViolationException("Invalid world repair chunk count.");
            MissingChunkIndices = new int[count];
            for (int i = 0; i < count; i++)
                MissingChunkIndices[i] = reader.ReadInt32();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(TransferId);
            writer.WriteBoolean(RequestManifest);
            writer.WriteInt32(HighestContiguousChunkIndex);
            writer.WriteInt32(HighestReceivedChunkIndex);
            writer.WritePackedInt32(MissingChunkIndices?.Length ?? 0);
            if (MissingChunkIndices == null) return;
            foreach (int index in MissingChunkIndices)
                writer.WriteInt32(index);
        }
    }
}
