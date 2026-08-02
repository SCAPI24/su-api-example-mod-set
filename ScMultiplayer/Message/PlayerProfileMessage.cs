using Comms;
using Game;
using System;

namespace ScMultiplayer
{
    [Serializable]
    public class PlayerProfileMessage : Message
    {
        public int ClientId;
        public string Name = string.Empty;
        public PlayerClass PlayerClass;
        public string SkinName = string.Empty;
        public byte[] SkinSha256 = Array.Empty<byte>();
        public int[][] Clothes = CreateEmptyClothes();

        public PlayerProfileMessage()
        {
        }

        public PlayerProfileMessage(int clientId, NetworkPlayerRecord record)
        {
            ClientId = clientId;
            Name = record?.Name ?? string.Empty;
            PlayerClass = record?.PlayerClass ?? PlayerClass.Male;
            SkinName = record?.SkinName ?? string.Empty;
            SkinSha256 = record?.SkinSha256 != null
                ? (byte[])record.SkinSha256.Clone()
                : Array.Empty<byte>();
            Clothes = CloneClothes(record?.Clothes);
        }

        protected override void Read(SuReader reader)
        {
            ClientId = reader.ReadInt32();
            Name = reader.ReadString();
            PlayerClass = (PlayerClass)reader.ReadInt32();
            SkinName = reader.ReadString();
            Clothes = new int[4][];
            for (int slot = 0; slot < Clothes.Length; slot++)
            {
                int count = reader.ReadPackedInt32();
                Clothes[slot] = new int[count];
                for (int i = 0; i < count; i++) Clothes[slot][i] = reader.ReadInt32();
            }
            SkinSha256 = reader.Position < reader.Length
                ? reader.ReadBytes()
                : Array.Empty<byte>();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(ClientId);
            writer.WriteString(Name ?? string.Empty);
            writer.WriteInt32((int)PlayerClass);
            writer.WriteString(SkinName ?? string.Empty);
            int[][] clothes = CloneClothes(Clothes);
            for (int slot = 0; slot < clothes.Length; slot++)
            {
                writer.WritePackedInt32(clothes[slot].Length);
                foreach (int value in clothes[slot]) writer.WriteInt32(value);
            }
            writer.WriteBytes(SkinSha256 ?? Array.Empty<byte>());
        }

        private static int[][] CreateEmptyClothes() =>
            new[] { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };

        private static int[][] CloneClothes(int[][] clothes)
        {
            int[][] result = CreateEmptyClothes();
            if (clothes == null) return result;
            for (int i = 0; i < Math.Min(result.Length, clothes.Length); i++)
                result[i] = clothes[i] != null ? (int[])clothes[i].Clone() : Array.Empty<int>();
            return result;
        }
    }
}
