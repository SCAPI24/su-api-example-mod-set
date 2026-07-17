using Game;
using System;
using System.Collections.Generic;
using Comms;
using Engine;

namespace ScMultiplayer
{
    [Serializable]
    public class GamePakWorldMessage : Message
    {
        public string Name;
        public byte[] WorldData;
        public DateTime LastSaveTime;
        public int TargetClientId = -1;
        public int RandomSeed;
        public Dictionary<string, long> RandomStates = new Dictionary<string, long>();
        public string PlayerName = string.Empty;
        public PlayerClass PlayerClass;
        public string SkinName = string.Empty;
        public Vector3 PlayerPosition;
        public float PlayerLevel = 1f;
        public float PlayerHealth = 1f;
        public int[] SlotValues = Array.Empty<int>();
        public int[] SlotCounts = Array.Empty<int>();
        public int[][] Clothes = CreateEmptyClothes();

        public GamePakWorldMessage()
        {
        }

        public GamePakWorldMessage(string name, byte[] worldData, DateTime lastSaveTime,
            int targetClientId, int randomSeed, Dictionary<string, long> randomStates,
            NetworkPlayerRecord playerRecord)
        {
            Name = name;
            WorldData = worldData;
            LastSaveTime = lastSaveTime;
            TargetClientId = targetClientId;
            RandomSeed = randomSeed;
            RandomStates = randomStates ?? new Dictionary<string, long>();
            PlayerName = playerRecord?.Name ?? string.Empty;
            PlayerClass = playerRecord?.PlayerClass ?? PlayerClass.Male;
            SkinName = playerRecord?.SkinName ?? string.Empty;
            PlayerPosition = playerRecord?.Position ?? Vector3.Zero;
            PlayerLevel = playerRecord?.Level ?? 1f;
            PlayerHealth = playerRecord?.Health ?? 1f;
            SlotValues = playerRecord?.SlotValues != null
                ? (int[])playerRecord.SlotValues.Clone() : Array.Empty<int>();
            SlotCounts = playerRecord?.SlotCounts != null
                ? (int[])playerRecord.SlotCounts.Clone() : Array.Empty<int>();
            Clothes = CloneClothes(playerRecord?.Clothes);
        }

        protected override void Read(SuReader reader)
        {
            Name = reader.ReadString();
            int dataLength = reader.ReadPackedInt32();
            WorldData = dataLength > 0 ? reader.ReadFixedBytes(dataLength) : null;
            LastSaveTime = DateTime.FromBinary(reader.ReadInt64());
            TargetClientId = reader.ReadInt32();
            RandomSeed = reader.ReadInt32();
            int count = reader.ReadPackedInt32();
            RandomStates = new Dictionary<string, long>(count);
            for (int i = 0; i < count; i++)
                RandomStates[reader.ReadString()] = reader.ReadInt64();
            PlayerName = reader.ReadString();
            PlayerClass = (PlayerClass)reader.ReadInt32();
            SkinName = reader.ReadString();
            PlayerPosition = reader.ReadVector3(reader);
            PlayerLevel = reader.ReadSingle();
            PlayerHealth = reader.ReadSingle();
            int slotsCount = reader.ReadPackedInt32();
            SlotValues = new int[slotsCount];
            SlotCounts = new int[slotsCount];
            for (int i = 0; i < slotsCount; i++)
            {
                SlotValues[i] = reader.ReadInt32();
                SlotCounts[i] = reader.ReadInt32();
            }
            Clothes = new int[4][];
            for (int slot = 0; slot < Clothes.Length; slot++)
            {
                int clothesCount = reader.ReadPackedInt32();
                Clothes[slot] = new int[clothesCount];
                for (int i = 0; i < clothesCount; i++) Clothes[slot][i] = reader.ReadInt32();
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteString(Name);
            writer.WritePackedInt32(WorldData?.Length ?? 0);
            if (WorldData != null && WorldData.Length > 0)
                writer.WriteFixedBytes(WorldData);
            writer.WriteInt64(LastSaveTime.ToBinary());
            writer.WriteInt32(TargetClientId);
            writer.WriteInt32(RandomSeed);
            writer.WritePackedInt32(RandomStates?.Count ?? 0);
            if (RandomStates != null)
            {
                foreach (KeyValuePair<string, long> item in RandomStates)
                {
                    writer.WriteString(item.Key);
                    writer.WriteInt64(item.Value);
                }
            }
            writer.WriteString(PlayerName ?? string.Empty);
            writer.WriteInt32((int)PlayerClass);
            writer.WriteString(SkinName ?? string.Empty);
            writer.WriteVector3(writer, PlayerPosition);
            writer.WriteSingle(PlayerLevel);
            writer.WriteSingle(PlayerHealth);
            int slotsCount = Math.Min(SlotValues?.Length ?? 0, SlotCounts?.Length ?? 0);
            writer.WritePackedInt32(slotsCount);
            for (int i = 0; i < slotsCount; i++)
            {
                writer.WriteInt32(SlotValues[i]);
                writer.WriteInt32(SlotCounts[i]);
            }
            int[][] clothes = CloneClothes(Clothes);
            for (int slot = 0; slot < clothes.Length; slot++)
            {
                writer.WritePackedInt32(clothes[slot].Length);
                foreach (int value in clothes[slot]) writer.WriteInt32(value);
            }
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
