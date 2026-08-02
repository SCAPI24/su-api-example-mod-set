using Comms;
using Game;
using System;

namespace ScMultiplayer
{
    [Serializable]
    public class PlayerSkinAssetMessage : Message
    {
        public enum SkinAssetAction : byte
        {
            Request,
            Data
        }

        public SkinAssetAction Action;
        public int ClientId;
        public string SkinName = string.Empty;
        public PlayerClass PlayerClass;
        public byte[] Sha256 = Array.Empty<byte>();
        public int TransferId;
        public int ChunkIndex;
        public int ChunkCount = 1;
        public int TotalLength;
        public byte[] Data = Array.Empty<byte>();

        public PlayerSkinAssetMessage()
        {
        }

        public PlayerSkinAssetMessage(SkinAssetAction action, int clientId,
            string skinName, PlayerClass playerClass, byte[] sha256, byte[] data = null)
        {
            Action = action;
            ClientId = clientId;
            SkinName = skinName ?? string.Empty;
            PlayerClass = playerClass;
            Sha256 = sha256 != null ? (byte[])sha256.Clone() : Array.Empty<byte>();
            TotalLength = data?.Length ?? 0;
            Data = data != null ? (byte[])data.Clone() : Array.Empty<byte>();
        }

        protected override void Read(SuReader reader)
        {
            Action = (SkinAssetAction)reader.ReadByte();
            ClientId = reader.ReadInt32();
            SkinName = reader.ReadString();
            PlayerClass = (PlayerClass)reader.ReadInt32();
            Sha256 = reader.ReadBytes();
            if (Action == SkinAssetAction.Data)
            {
                Data = reader.Position < reader.Length ? reader.ReadBytes() : Array.Empty<byte>();
                // Chunk metadata was added after the original payload. Keep old single-packet
                // assets readable while all current clients use the chunked form.
                if (reader.Position + 16 <= reader.Length)
                {
                    TransferId = reader.ReadInt32();
                    ChunkIndex = reader.ReadInt32();
                    ChunkCount = reader.ReadInt32();
                    TotalLength = reader.ReadInt32();
                }
                else
                {
                    TransferId = 0;
                    ChunkIndex = 0;
                    ChunkCount = 1;
                    TotalLength = Data.Length;
                }
            }
            else
            {
                Data = Array.Empty<byte>();
                TransferId = 0;
                ChunkIndex = 0;
                ChunkCount = 0;
                TotalLength = 0;
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteByte((byte)Action);
            writer.WriteInt32(ClientId);
            writer.WriteString(SkinName ?? string.Empty);
            writer.WriteInt32((int)PlayerClass);
            writer.WriteBytes(Sha256 ?? Array.Empty<byte>());
            if (Action == SkinAssetAction.Data)
            {
                writer.WriteBytes(Data ?? Array.Empty<byte>());
                writer.WriteInt32(TransferId);
                writer.WriteInt32(ChunkIndex);
                writer.WriteInt32(ChunkCount);
                writer.WriteInt32(TotalLength > 0 ? TotalLength : Data?.Length ?? 0);
            }
        }
    }
}
