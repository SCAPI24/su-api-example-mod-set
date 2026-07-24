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
            Movement = 16,
            Template = 32,
            BehaviorState = 64,
            Health = 128
        }

        public List<BodyItem> Bodies = new List<BodyItem>();
        public int ServerTick;
        public bool IsFullSnapshot;

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
            public Vector2? WalkOrder;
            public Vector3? SwimOrder;
            public Vector2 TurnOrder;
            public float JumpOrder;
            public bool AttackOrder;
            public bool FeedOrder;
            public string TemplateName;
            public byte SyncTier;
            public string ActiveBehaviorState;
            public int TargetEntityId;
            public string HerdName;
            public int SimulationSeed;
            public string ShapeshiftTarget;
            public float Health;
            public ChangeFlag Flags;
        }

        protected override void Read(SuReader reader)
        {
            ServerTick = reader.ReadInt32();
            IsFullSnapshot = reader.ReadBoolean();
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
                if (item.Flags.HasFlag(ChangeFlag.Movement))
                {
                    if (reader.ReadBoolean()) item.WalkOrder = reader.ReadVector2(reader);
                    if (reader.ReadBoolean()) item.FlyOrder = reader.ReadVector3(reader);
                    if (reader.ReadBoolean()) item.SwimOrder = reader.ReadVector3(reader);
                    item.TurnOrder = reader.ReadVector2(reader);
                    item.JumpOrder = reader.ReadSingle();
                    item.AttackOrder = reader.ReadBoolean();
                    item.FeedOrder = reader.ReadBoolean();
                }
                if (item.Flags.HasFlag(ChangeFlag.Template))
                    item.TemplateName = reader.ReadString();
                if (item.Flags.HasFlag(ChangeFlag.BehaviorState))
                {
                    item.SyncTier = reader.ReadByte();
                    item.ActiveBehaviorState = reader.ReadString();
                    item.TargetEntityId = reader.ReadInt32();
                    item.HerdName = reader.ReadString();
                    item.SimulationSeed = reader.ReadInt32();
                    item.ShapeshiftTarget = reader.ReadString();
                }
                if (item.Flags.HasFlag(ChangeFlag.Health))
                    item.Health = reader.ReadSingle();
                Bodies.Add(item);
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(ServerTick);
            writer.WriteBoolean(IsFullSnapshot);
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
                if (item.Flags.HasFlag(ChangeFlag.Movement))
                {
                    writer.WriteBoolean(item.WalkOrder.HasValue);
                    if (item.WalkOrder.HasValue) writer.WriteVector2(writer, item.WalkOrder.Value);
                    writer.WriteBoolean(item.FlyOrder.HasValue);
                    if (item.FlyOrder.HasValue) writer.WriteVector3(writer, item.FlyOrder.Value);
                    writer.WriteBoolean(item.SwimOrder.HasValue);
                    if (item.SwimOrder.HasValue) writer.WriteVector3(writer, item.SwimOrder.Value);
                    writer.WriteVector2(writer, item.TurnOrder);
                    writer.WriteSingle(item.JumpOrder);
                    writer.WriteBoolean(item.AttackOrder);
                    writer.WriteBoolean(item.FeedOrder);
                }
                if (item.Flags.HasFlag(ChangeFlag.Template))
                    writer.WriteString(item.TemplateName ?? string.Empty);
                if (item.Flags.HasFlag(ChangeFlag.BehaviorState))
                {
                    writer.WriteByte(item.SyncTier);
                    writer.WriteString(item.ActiveBehaviorState ?? string.Empty);
                    writer.WriteInt32(item.TargetEntityId);
                    writer.WriteString(item.HerdName ?? string.Empty);
                    writer.WriteInt32(item.SimulationSeed);
                    writer.WriteString(item.ShapeshiftTarget ?? string.Empty);
                }
                if (item.Flags.HasFlag(ChangeFlag.Health))
                    writer.WriteSingle(item.Health);
            }
        }
    }
}
