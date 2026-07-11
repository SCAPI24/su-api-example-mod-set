using Engine;
using System;
using System.Collections.Generic;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public class PickableSyncMessage : Message
    {
        public enum PickAction : byte
        {
            Create,
            UpdatePosition,
            Delete,
            SetFlyTo
        }

        public PickAction Action;
        public ushort Id;
        public int Value;
        public int Count;
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3? FlyToPosition;
        public bool PlaySound;

        // For batch update
        public List<PickablePos> Positions = new List<PickablePos>();

        public PickableSyncMessage() { }

        public PickableSyncMessage(PickAction action, ushort id, int value, int count, Vector3 pos, Vector3 vel, Vector3? flyTo = null, bool playSound = false)
        {
            Action = action; Id = id; Value = value; Count = count;
            Position = pos; Velocity = vel; FlyToPosition = flyTo; PlaySound = playSound;
        }

        public struct PickablePos
        {
            public ushort Id;
            public Vector3 Position;
        }

        protected override void Read(SuReader reader)
        {
            Action = (PickAction)reader.ReadByte();
            switch (Action)
            {
                case PickAction.Create:
                    Id = (ushort)reader.ReadPackedInt32();
                    Count = reader.ReadPackedInt32();
                    Value = reader.ReadInt32();
                    Position = reader.ReadVector3(reader);
                    Velocity = reader.ReadVector3(reader);
                    break;
                case PickAction.UpdatePosition:
                    int cnt = reader.ReadPackedInt32();
                    Positions.Clear();
                    for (int i = 0; i < cnt; i++)
                        Positions.Add(new PickablePos { Id = (ushort)reader.ReadPackedInt32(), Position = reader.ReadVector3(reader) });
                    break;
                case PickAction.Delete:
                    Id = (ushort)reader.ReadPackedInt32();
                    PlaySound = reader.ReadBoolean();
                    break;
                case PickAction.SetFlyTo:
                    Id = (ushort)reader.ReadPackedInt32();
                    FlyToPosition = reader.ReadVector3(reader);
                    break;
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteByte((byte)Action);
            switch (Action)
            {
                case PickAction.Create:
                    writer.WritePackedInt32(Id);
                    writer.WritePackedInt32(Count);
                    writer.WriteInt32(Value);
                    writer.WriteVector3(writer, Position);
                    writer.WriteVector3(writer, Velocity);
                    break;
                case PickAction.UpdatePosition:
                    writer.WritePackedInt32(Positions.Count);
                    foreach (var p in Positions)
                    {
                        writer.WritePackedInt32(p.Id);
                        writer.WriteVector3(writer, p.Position);
                    }
                    break;
                case PickAction.Delete:
                    writer.WritePackedInt32(Id);
                    writer.WriteBoolean(PlaySound);
                    break;
                case PickAction.SetFlyTo:
                    writer.WritePackedInt32(Id);
                    writer.WriteVector3(writer, FlyToPosition.Value);
                    break;
            }
        }
    }
}
