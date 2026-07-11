using Engine;
using System;
using System.Collections.Generic;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public class BodyUpdateMessage : Message
    {
        [Flags]
        public enum ChangeFlag : byte
        {
            None = 0,
            Position = 1,
            Rotation = 2,
            Velocity = 4,
            LookAngles = 8,
            FlyOrder = 16
        }

        public List<BodyItem> Bodies = new List<BodyItem>();

        public BodyUpdateMessage() { }

        public BodyUpdateMessage(List<BodyItem> bodies)
        {
            Bodies = bodies;
        }

        public struct BodyItem
        {
            public ushort EntityId;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Velocity;
            public Vector2 LookAngles;
            public Vector3? FlyOrder;
            public ChangeFlag Flags;
        }

        protected override void Read(SuReader reader)
        {
            int count = reader.ReadPackedInt32();
            Bodies.Clear();
            for (int i = 0; i < count; i++)
            {
                BodyItem item = new BodyItem();
                item.Flags = (ChangeFlag)reader.ReadByte();
                item.EntityId = (ushort)reader.ReadPackedInt32();
                if (item.Flags.HasFlag(ChangeFlag.Position))
                    item.Position = reader.ReadVector3(reader);
                if (item.Flags.HasFlag(ChangeFlag.Rotation))
                    item.Rotation = reader.ReadQuaternion(reader);
                if (item.Flags.HasFlag(ChangeFlag.Velocity))
                    item.Velocity = reader.ReadVector3(reader);
                if (item.Flags.HasFlag(ChangeFlag.LookAngles))
                    item.LookAngles = reader.ReadVector2(reader);
                if (item.Flags.HasFlag(ChangeFlag.FlyOrder))
                    item.FlyOrder = reader.ReadVector3(reader);
                Bodies.Add(item);
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WritePackedInt32(Bodies.Count);
            foreach (var item in Bodies)
            {
                writer.WriteByte((byte)item.Flags);
                writer.WritePackedInt32(item.EntityId);
                if (item.Flags.HasFlag(ChangeFlag.Position))
                    writer.WriteVector3(writer, item.Position);
                if (item.Flags.HasFlag(ChangeFlag.Rotation))
                    writer.WriteQuaternion(writer, item.Rotation);
                if (item.Flags.HasFlag(ChangeFlag.Velocity))
                    writer.WriteVector3(writer, item.Velocity);
                if (item.Flags.HasFlag(ChangeFlag.LookAngles))
                    writer.WriteVector2(writer, item.LookAngles);
                if (item.Flags.HasFlag(ChangeFlag.FlyOrder))
                    writer.WriteVector3(writer, item.FlyOrder.Value);
            }
        }
    }
}
