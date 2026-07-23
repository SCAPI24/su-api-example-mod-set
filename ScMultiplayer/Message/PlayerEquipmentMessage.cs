using System;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public sealed class PlayerEquipmentMessage : Message
    {
        public int ClientId;
        public int Revision;
        public int ActiveSlotIndex;
        public int[] SlotValues = Array.Empty<int>();
        public int[] SlotCounts = Array.Empty<int>();
        public int[][] Clothes = CreateEmptyClothes();

        public PlayerEquipmentMessage()
        {
        }

        public PlayerEquipmentMessage(int clientId, int revision, int activeSlotIndex,
            int[] slotValues, int[] slotCounts, int[][] clothes)
        {
            ClientId = clientId;
            Revision = revision;
            ActiveSlotIndex = activeSlotIndex;
            SlotValues = CloneArray(slotValues);
            SlotCounts = CloneArray(slotCounts);
            Clothes = CloneClothes(clothes);
        }

        protected override void Read(SuReader reader)
        {
            ClientId = reader.ReadInt32();
            Revision = reader.ReadInt32();
            ActiveSlotIndex = reader.ReadInt32();
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
                int count = reader.ReadPackedInt32();
                Clothes[slot] = new int[count];
                for (int i = 0; i < count; i++) Clothes[slot][i] = reader.ReadInt32();
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(ClientId);
            writer.WriteInt32(Revision);
            writer.WriteInt32(ActiveSlotIndex);
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

        private static int[] CloneArray(int[] values) =>
            values == null ? Array.Empty<int>() : (int[])values.Clone();

        private static int[][] CreateEmptyClothes() =>
            new[] { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };

        private static int[][] CloneClothes(int[][] clothes)
        {
            int[][] result = CreateEmptyClothes();
            if (clothes == null) return result;
            for (int i = 0; i < result.Length && i < clothes.Length; i++)
                result[i] = clothes[i] == null ? Array.Empty<int>() : (int[])clothes[i].Clone();
            return result;
        }
    }
}
