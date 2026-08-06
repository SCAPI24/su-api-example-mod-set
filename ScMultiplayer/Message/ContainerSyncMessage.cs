using Comms;
using Engine;
using System;

namespace ScMultiplayer
{
    [Serializable]
    public class ContainerSyncMessage : Message
    {
        public Point3 Coordinates;
        public string ComponentType = string.Empty;
        public int Revision;
        public int RequestId;
        public int RequesterClientId;
        public int PlayerRevision;
        public bool IsRequest;
        // Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
        public bool IsDrop;
        public int DropValue;
        public int DropCount;
        public Vector3 DropPosition;
        public Vector3 DropVelocity;
        public int[] SlotValues = Array.Empty<int>();
        public int[] SlotCounts = Array.Empty<int>();
        public int[] BaseSlotValues = Array.Empty<int>();
        public int[] BaseSlotCounts = Array.Empty<int>();
        public int[] PlayerBaseSlotValues = Array.Empty<int>();
        public int[] PlayerBaseSlotCounts = Array.Empty<int>();
        public int[] PlayerSlotValues = Array.Empty<int>();
        public int[] PlayerSlotCounts = Array.Empty<int>();
        // Source: Game/ComponentCraftingTable.cs:ComponentCraftingTable.FindInteractingPlayer
        // A non-negative owner identifies the crafting inventory attached to that player's entity.
        public int OwnerClientId = -1;
        public bool IsBaselineRequest;

        protected override void Read(SuReader reader)
        {
            Coordinates = reader.ReadPoint3();
            ComponentType = reader.ReadString();
            Revision = reader.ReadInt32();
            RequestId = reader.ReadInt32();
            RequesterClientId = reader.ReadInt32();
            PlayerRevision = reader.ReadInt32();
            IsRequest = reader.ReadBoolean();
            IsDrop = reader.ReadBoolean();
            if (IsDrop)
            {
                DropValue = reader.ReadInt32();
                DropCount = reader.ReadInt32();
                DropPosition = reader.ReadVector3(reader);
                DropVelocity = reader.ReadVector3(reader);
            }
            ReadSlots(reader, out SlotValues, out SlotCounts);
            ReadSlots(reader, out BaseSlotValues, out BaseSlotCounts);
            ReadSlots(reader, out PlayerBaseSlotValues, out PlayerBaseSlotCounts);
            ReadSlots(reader, out PlayerSlotValues, out PlayerSlotCounts);
            if (reader.Position < reader.Length)
                OwnerClientId = reader.ReadInt32();
            if (reader.Position < reader.Length)
                IsBaselineRequest = reader.ReadBoolean();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WritePoint3(Coordinates);
            writer.WriteString(ComponentType ?? string.Empty);
            writer.WriteInt32(Revision);
            writer.WriteInt32(RequestId);
            writer.WriteInt32(RequesterClientId);
            writer.WriteInt32(PlayerRevision);
            writer.WriteBoolean(IsRequest);
            writer.WriteBoolean(IsDrop);
            if (IsDrop)
            {
                writer.WriteInt32(DropValue);
                writer.WriteInt32(DropCount);
                writer.WriteVector3(writer, DropPosition);
                writer.WriteVector3(writer, DropVelocity);
            }
            WriteSlots(writer, SlotValues, SlotCounts);
            WriteSlots(writer, BaseSlotValues, BaseSlotCounts);
            WriteSlots(writer, PlayerBaseSlotValues, PlayerBaseSlotCounts);
            WriteSlots(writer, PlayerSlotValues, PlayerSlotCounts);
            writer.WriteInt32(OwnerClientId);
            writer.WriteBoolean(IsBaselineRequest);
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
