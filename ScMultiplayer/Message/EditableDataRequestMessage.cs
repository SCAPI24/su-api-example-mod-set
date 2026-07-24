using System;
using Comms;
using Engine;

namespace ScMultiplayer
{
    public enum EditableDataKind : byte
    {
        MemoryBank = 1,
        TruthTable = 2,
        AdjustableDelay = 3,
        SwitchVoltage = 4,
        ButtonVoltage = 5,
        Piston = 6
    }

    public enum EditableDataScope : byte
    {
        InventoryItem = 1,
        Block = 2
    }

    // Source: Survivalcraft/Game/SubsystemEditableItemBehavior.cs:SubsystemEditableItemBehavior<T>
    [Serializable]
    public sealed class EditableDataRequestMessage : Message
    {
        public int RequestId;
        public EditableDataKind Kind;
        public EditableDataScope Scope;
        public int SlotIndex = -1;
        public int ExpectedValue;
        public Point3 Coordinates;
        public string Payload = string.Empty;

        protected override void Read(SuReader reader)
        {
            RequestId = reader.ReadInt32();
            Kind = (EditableDataKind)reader.ReadByte();
            Scope = (EditableDataScope)reader.ReadByte();
            SlotIndex = reader.ReadInt32();
            ExpectedValue = reader.ReadInt32();
            Coordinates = reader.ReadPoint3();
            Payload = reader.ReadString();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(RequestId);
            writer.WriteByte((byte)Kind);
            writer.WriteByte((byte)Scope);
            writer.WriteInt32(SlotIndex);
            writer.WriteInt32(ExpectedValue);
            writer.WritePoint3(Coordinates);
            writer.WriteString(Payload ?? string.Empty);
        }
    }
}
