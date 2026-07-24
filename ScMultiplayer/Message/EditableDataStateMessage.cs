using System;
using Comms;
using Engine;

namespace ScMultiplayer
{
    // Source: Survivalcraft/Game/SubsystemEditableItemBehavior.cs:SubsystemEditableItemBehavior<T>
    [Serializable]
    public sealed class EditableDataStateMessage : Message
    {
        public int Revision;
        public int ExecuteHostCircuitStep;
        public EditableDataKind Kind;
        public EditableDataScope Scope;
        public int DataId;
        public Point3 Coordinates;
        public string Payload = string.Empty;

        protected override void Read(SuReader reader)
        {
            Revision = reader.ReadInt32();
            ExecuteHostCircuitStep = reader.ReadPackedInt32();
            Kind = (EditableDataKind)reader.ReadByte();
            Scope = (EditableDataScope)reader.ReadByte();
            DataId = reader.ReadInt32();
            Coordinates = reader.ReadPoint3();
            Payload = reader.ReadString();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(Revision);
            writer.WritePackedInt32(ExecuteHostCircuitStep);
            writer.WriteByte((byte)Kind);
            writer.WriteByte((byte)Scope);
            writer.WriteInt32(DataId);
            writer.WritePoint3(Coordinates);
            writer.WriteString(Payload ?? string.Empty);
        }
    }
}
