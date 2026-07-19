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
        public Matrix? StuckMatrix;
        public bool PlaySound;

        // For batch update
        public List<PickablePos> Positions = new List<PickablePos>();

        public PickableSyncMessage() { }

        public PickableSyncMessage(PickAction action, ushort id, int value, int count,
            Vector3 pos, Vector3 vel, Vector3? flyTo = null, bool playSound = false,
            Matrix? stuckMatrix = null)
        {
            Action = action; Id = id; Value = value; Count = count;
            Position = pos; Velocity = vel; FlyToPosition = flyTo; PlaySound = playSound;
            StuckMatrix = stuckMatrix;
        }

        public struct PickablePos
        {
            public ushort Id;
            public Vector3 Position;
            public Vector3 Velocity;
            public Vector3? FlyToPosition;
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
                    FlyToPosition = reader.ReadBoolean() ? reader.ReadVector3(reader) : (Vector3?)null;
                    StuckMatrix = reader.ReadBoolean() ? ReadMatrix(reader) : (Matrix?)null;
                    break;
                case PickAction.UpdatePosition:
                    int cnt = reader.ReadPackedInt32();
                    Positions.Clear();
                    for (int i = 0; i < cnt; i++)
                        Positions.Add(new PickablePos
                        {
                            Id = (ushort)reader.ReadPackedInt32(),
                            Position = reader.ReadVector3(reader),
                            Velocity = reader.ReadVector3(reader),
                            FlyToPosition = reader.ReadBoolean() ? reader.ReadVector3(reader) : (Vector3?)null
                        });
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
                    writer.WriteBoolean(FlyToPosition.HasValue);
                    if (FlyToPosition.HasValue) writer.WriteVector3(writer, FlyToPosition.Value);
                    writer.WriteBoolean(StuckMatrix.HasValue);
                    if (StuckMatrix.HasValue) WriteMatrix(writer, StuckMatrix.Value);
                    break;
                case PickAction.UpdatePosition:
                    writer.WritePackedInt32(Positions.Count);
                    foreach (var p in Positions)
                    {
                        writer.WritePackedInt32(p.Id);
                        writer.WriteVector3(writer, p.Position);
                        writer.WriteVector3(writer, p.Velocity);
                        writer.WriteBoolean(p.FlyToPosition.HasValue);
                        if (p.FlyToPosition.HasValue) writer.WriteVector3(writer, p.FlyToPosition.Value);
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

        private static Matrix ReadMatrix(SuReader reader)
        {
            return new Matrix(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static void WriteMatrix(SuWriter writer, Matrix matrix)
        {
            writer.WriteSingle(matrix.M11); writer.WriteSingle(matrix.M12);
            writer.WriteSingle(matrix.M13); writer.WriteSingle(matrix.M14);
            writer.WriteSingle(matrix.M21); writer.WriteSingle(matrix.M22);
            writer.WriteSingle(matrix.M23); writer.WriteSingle(matrix.M24);
            writer.WriteSingle(matrix.M31); writer.WriteSingle(matrix.M32);
            writer.WriteSingle(matrix.M33); writer.WriteSingle(matrix.M34);
            writer.WriteSingle(matrix.M41); writer.WriteSingle(matrix.M42);
            writer.WriteSingle(matrix.M43); writer.WriteSingle(matrix.M44);
        }
    }
}
