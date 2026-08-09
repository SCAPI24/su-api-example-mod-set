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
        public int TransferId;
        public int ChunkCount;
        public int TotalLength;
        public byte[] WorldSha256 = Array.Empty<byte>();
        public int RandomSeed;
        public long TerrainSequenceBaseline;
        public Dictionary<string, long> RandomStates = new Dictionary<string, long>();
        public string PlayerName = string.Empty;
        public PlayerClass PlayerClass;
        public string SkinName = string.Empty;
        public byte[] SkinSha256 = Array.Empty<byte>();
        public Vector3 PlayerPosition;
        public float PlayerLevel = 1f;
        public float PlayerHealth = 1f;
        public float PlayerAir = 1f;
        public float PlayerFood = 0.9f;
        public float PlayerStamina = 1f;
        public float PlayerSleep = 0.9f;
        public float PlayerTemperature = 12f;
        public float PlayerTargetTemperature = 12f;
        public float PlayerWetness;
        public float PlayerFluDuration;
        public float PlayerFluOnset;
        public float PlayerSicknessDuration;
        public bool PlayerIsCreativeFlying;
        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:NetworkPlayerRecord.HasReceivedInitialItems
        public bool HasReceivedInitialItems = true;
        public bool InventoryWasCreative;
        public int ActiveSlotIndex;
        public int CreativeCategoryIndex;
        public int CreativePageIndex;
        public int[] SlotValues = Array.Empty<int>();
        public int[] SlotCounts = Array.Empty<int>();
        public int[][] Clothes = CreateEmptyClothes();
        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:NetworkPlayerRecord
        public Quaternion PlayerBodyRotation = Quaternion.Identity;
        public Vector2 PlayerLookAngles;
        public float PlayerFireDuration;
        public Dictionary<int, float> PlayerSatiation = new Dictionary<int, float>();
        public int[] HandcraftSlotValues = Array.Empty<int>();
        public int[] HandcraftSlotCounts = Array.Empty<int>();
        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPosition
        public Vector3 PlayerSpawnPosition;

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
            SkinSha256 = playerRecord?.SkinSha256 != null
                ? (byte[])playerRecord.SkinSha256.Clone()
                : Array.Empty<byte>();
            PlayerPosition = playerRecord?.Position ?? Vector3.Zero;
            PlayerLevel = playerRecord?.Level ?? 1f;
            PlayerHealth = playerRecord?.Health ?? 1f;
            PlayerAir = playerRecord?.Air ?? 1f;
            PlayerFood = playerRecord?.Food ?? 0.9f;
            PlayerStamina = playerRecord?.Stamina ?? 1f;
            PlayerSleep = playerRecord?.Sleep ?? 0.9f;
            PlayerTemperature = playerRecord?.Temperature ?? 12f;
            PlayerTargetTemperature = playerRecord?.TargetTemperature ?? 12f;
            PlayerWetness = playerRecord?.Wetness ?? 0f;
            PlayerFluDuration = playerRecord?.FluDuration ?? 0f;
            PlayerFluOnset = playerRecord?.FluOnset ?? 0f;
            PlayerSicknessDuration = playerRecord?.SicknessDuration ?? 0f;
            PlayerIsCreativeFlying = playerRecord?.IsCreativeFlying ?? false;
            HasReceivedInitialItems = playerRecord?.HasReceivedInitialItems ?? true;
            InventoryWasCreative = playerRecord?.InventoryWasCreative ?? false;
            ActiveSlotIndex = playerRecord?.ActiveSlotIndex ?? 0;
            CreativeCategoryIndex = playerRecord?.CreativeCategoryIndex ?? 0;
            CreativePageIndex = playerRecord?.CreativePageIndex ?? 0;
            SlotValues = playerRecord?.SlotValues != null
                ? (int[])playerRecord.SlotValues.Clone() : Array.Empty<int>();
            SlotCounts = playerRecord?.SlotCounts != null
                ? (int[])playerRecord.SlotCounts.Clone() : Array.Empty<int>();
            Clothes = CloneClothes(playerRecord?.Clothes);
            PlayerBodyRotation = playerRecord?.BodyRotation ?? Quaternion.Identity;
            PlayerLookAngles = playerRecord?.LookAngles ?? Vector2.Zero;
            PlayerFireDuration = playerRecord?.FireDuration ?? 0f;
            PlayerSatiation = playerRecord?.Satiation != null
                ? new Dictionary<int, float>(playerRecord.Satiation)
                : new Dictionary<int, float>();
            HandcraftSlotValues = playerRecord?.HandcraftSlotValues != null
                ? (int[])playerRecord.HandcraftSlotValues.Clone() : Array.Empty<int>();
            HandcraftSlotCounts = playerRecord?.HandcraftSlotCounts != null
                ? (int[])playerRecord.HandcraftSlotCounts.Clone() : Array.Empty<int>();
            PlayerSpawnPosition = playerRecord?.SpawnPosition ?? Vector3.Zero;
        }

        protected override void Read(SuReader reader)
        {
            Name = reader.ReadString();
            int dataLength = reader.ReadPackedInt32();
            WorldData = dataLength > 0 ? reader.ReadFixedBytes(dataLength) : null;
            LastSaveTime = DateTime.FromBinary(reader.ReadInt64());
            TargetClientId = reader.ReadInt32();
            TransferId = reader.ReadInt32();
            ChunkCount = reader.ReadInt32();
            TotalLength = reader.ReadInt32();
            WorldSha256 = reader.ReadBytes();
            RandomSeed = reader.ReadInt32();
            TerrainSequenceBaseline = reader.ReadInt64();
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
            PlayerAir = reader.ReadSingle();
            PlayerFood = reader.ReadSingle();
            PlayerStamina = reader.ReadSingle();
            PlayerSleep = reader.ReadSingle();
            PlayerTemperature = reader.ReadSingle();
            PlayerTargetTemperature = reader.ReadSingle();
            PlayerWetness = reader.ReadSingle();
            PlayerFluDuration = reader.ReadSingle();
            PlayerFluOnset = reader.ReadSingle();
            PlayerSicknessDuration = reader.ReadSingle();
            PlayerIsCreativeFlying = reader.ReadBoolean();
            HasReceivedInitialItems = reader.ReadBoolean();
            InventoryWasCreative = reader.ReadBoolean();
            ActiveSlotIndex = reader.ReadInt32();
            CreativeCategoryIndex = reader.ReadInt32();
            CreativePageIndex = reader.ReadInt32();
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
            SkinSha256 = reader.Position < reader.Length
                ? reader.ReadBytes()
                : Array.Empty<byte>();
            if (reader.Position < reader.Length)
                PlayerBodyRotation = reader.ReadQuaternion(reader);
            if (reader.Position < reader.Length)
                PlayerLookAngles = reader.ReadVector2(reader);
            if (reader.Position < reader.Length)
                PlayerFireDuration = reader.ReadSingle();
            if (reader.Position < reader.Length)
            {
                int satiationCount = reader.ReadPackedInt32();
                PlayerSatiation = new Dictionary<int, float>(satiationCount);
                for (int i = 0; i < satiationCount; i++)
                    PlayerSatiation[reader.ReadInt32()] = reader.ReadSingle();
            }
            if (reader.Position < reader.Length)
                ReadSlots(reader, out HandcraftSlotValues, out HandcraftSlotCounts);
            if (reader.Position < reader.Length)
                PlayerSpawnPosition = reader.ReadVector3(reader);
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteString(Name);
            writer.WritePackedInt32(WorldData?.Length ?? 0);
            if (WorldData != null && WorldData.Length > 0)
                writer.WriteFixedBytes(WorldData);
            writer.WriteInt64(LastSaveTime.ToBinary());
            writer.WriteInt32(TargetClientId);
            writer.WriteInt32(TransferId);
            writer.WriteInt32(ChunkCount);
            writer.WriteInt32(TotalLength);
            writer.WriteBytes(WorldSha256 ?? Array.Empty<byte>());
            writer.WriteInt32(RandomSeed);
            writer.WriteInt64(TerrainSequenceBaseline);
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
            writer.WriteSingle(PlayerAir);
            writer.WriteSingle(PlayerFood);
            writer.WriteSingle(PlayerStamina);
            writer.WriteSingle(PlayerSleep);
            writer.WriteSingle(PlayerTemperature);
            writer.WriteSingle(PlayerTargetTemperature);
            writer.WriteSingle(PlayerWetness);
            writer.WriteSingle(PlayerFluDuration);
            writer.WriteSingle(PlayerFluOnset);
            writer.WriteSingle(PlayerSicknessDuration);
            writer.WriteBoolean(PlayerIsCreativeFlying);
            writer.WriteBoolean(HasReceivedInitialItems);
            writer.WriteBoolean(InventoryWasCreative);
            writer.WriteInt32(ActiveSlotIndex);
            writer.WriteInt32(CreativeCategoryIndex);
            writer.WriteInt32(CreativePageIndex);
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
            writer.WriteBytes(SkinSha256 ?? Array.Empty<byte>());
            writer.WriteQuaternion(writer, PlayerBodyRotation);
            writer.WriteVector2(writer, PlayerLookAngles);
            writer.WriteSingle(PlayerFireDuration);
            writer.WritePackedInt32(PlayerSatiation?.Count ?? 0);
            if (PlayerSatiation != null)
            {
                foreach (KeyValuePair<int, float> item in PlayerSatiation)
                {
                    writer.WriteInt32(item.Key);
                    writer.WriteSingle(item.Value);
                }
            }
            WriteSlots(writer, HandcraftSlotValues, HandcraftSlotCounts);
            writer.WriteVector3(writer, PlayerSpawnPosition);
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

        private static void ReadSlots(SuReader reader, out int[] values, out int[] counts)
        {
            int count = reader.ReadPackedInt32();
            values = new int[count];
            counts = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = reader.ReadInt32();
                counts[i] = reader.ReadInt32();
            }
        }

        private static void WriteSlots(SuWriter writer, int[] values, int[] counts)
        {
            int count = Math.Min(values?.Length ?? 0, counts?.Length ?? 0);
            writer.WritePackedInt32(count);
            for (int i = 0; i < count; i++)
            {
                writer.WriteInt32(values[i]);
                writer.WriteInt32(counts[i]);
            }
        }
    }
}
