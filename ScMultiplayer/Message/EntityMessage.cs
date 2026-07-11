using Engine;
using System;
using System.Collections.Generic;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public class EntityMessage : Message
    {
        public enum EntityAction : byte
        {
            Add,
            Remove
        }

        public EntityAction Action;
        public ushort EntityId;
        public string TemplateName; // Only for Add
        // For Add: serialized entity values dict
        public byte[] EntityData; // MessagePack-serialized ValuesDictionary

        public EntityMessage() { }

        public EntityMessage(ushort entityId, EntityAction action, string templateName = null, byte[] entityData = null)
        {
            EntityId = entityId;
            Action = action;
            TemplateName = templateName ?? string.Empty;
            EntityData = entityData ?? Array.Empty<byte>();
        }

        protected override void Read(SuReader reader)
        {
            Action = (EntityAction)reader.ReadByte();
            EntityId = (ushort)reader.ReadPackedInt32();
            TemplateName = reader.ReadString();
            int dataLen = reader.ReadPackedInt32();
            EntityData = dataLen > 0 ? reader.ReadFixedBytes(dataLen) : Array.Empty<byte>();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteByte((byte)Action);
            writer.WritePackedInt32(EntityId);
            writer.WriteString(TemplateName ?? string.Empty);
            writer.WritePackedInt32(EntityData?.Length ?? 0);
            if (EntityData != null && EntityData.Length > 0)
                writer.WriteFixedBytes(EntityData);
        }
    }
}
