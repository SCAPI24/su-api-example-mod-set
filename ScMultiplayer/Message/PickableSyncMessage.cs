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
            SetFlyTo,
            Acquire,
            WaterSplash,
            RequestAcquire
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
        public int RequestId;
        public int CollectorClientId = -1;
        public int ServerTick;
        // 拾取只同步"被拾取的那件物品进入的格子"：HasInventoryDelta 为真时，下面三个数组是
        // 稀疏增量（长度相同，SlotIndices[i] 格应写成 SlotValues[i]/SlotCounts[i]），
        // 而不是整包背包。整包背包（创造模式 1622 格 ≈ 12.7 KB）以前每次拾取都广播给所有人，
        // 是"捡东西时带宽 30KB/s→280KB/s"的根因。
        public bool HasInventoryDelta;
        public int[] SlotIndices = Array.Empty<int>();
        public int[] SlotValues = Array.Empty<int>();
        // 方案 1：该背包结果的宿主版本号（主机 MarkHostInventoryAuthoritative 每次自增）。
        public int InventoryVersion;
        public int[] SlotCounts = Array.Empty<int>();

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
                case PickAction.Acquire:
                    Id = (ushort)reader.ReadPackedInt32();
                    RequestId = reader.ReadInt32();
                    CollectorClientId = reader.ReadInt32();
                    ServerTick = reader.ReadInt32();
                    Count = reader.ReadPackedInt32();
                    PlaySound = reader.ReadBoolean();
                    HasInventoryDelta = reader.ReadBoolean();
                    if (HasInventoryDelta)
                    {
                        int indicesCount = reader.ReadPackedInt32();
                        SlotIndices = new int[indicesCount];
                        for (int i = 0; i < indicesCount; i++)
                            SlotIndices[i] = reader.ReadPackedInt32();
                    }
                    int slotsCount = reader.ReadPackedInt32();
                    SlotValues = new int[slotsCount];
                    SlotCounts = new int[slotsCount];
                    for (int i = 0; i < slotsCount; i++)
                    {
                        SlotValues[i] = reader.ReadInt32();
                        SlotCounts[i] = reader.ReadInt32();
                    }
                    // 方案 1：背包同步版本号（主机单调递增；客户端只应用更新的一份）
                    InventoryVersion = reader.ReadInt32();
                    break;
                case PickAction.WaterSplash:
                    Id = (ushort)reader.ReadPackedInt32();
                    Position = reader.ReadVector3(reader);
                    break;
                case PickAction.RequestAcquire:
                    Id = (ushort)reader.ReadPackedInt32();
                    RequestId = reader.ReadInt32();
                    Position = reader.ReadVector3(reader);
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
                case PickAction.Acquire:
                    writer.WritePackedInt32(Id);
                    writer.WriteInt32(RequestId);
                    writer.WriteInt32(CollectorClientId);
                    writer.WriteInt32(ServerTick);
                    writer.WritePackedInt32(Count);
                    writer.WriteBoolean(PlaySound);
                    writer.WriteBoolean(HasInventoryDelta);
                    if (HasInventoryDelta)
                    {
                        int indicesCount = Math.Min(SlotIndices?.Length ?? 0,
                            Math.Min(SlotValues?.Length ?? 0, SlotCounts?.Length ?? 0));
                        writer.WritePackedInt32(indicesCount);
                        for (int i = 0; i < indicesCount; i++)
                            writer.WritePackedInt32(SlotIndices[i]);
                    }
                    int slotsCount = Math.Min(SlotValues?.Length ?? 0,
                        SlotCounts?.Length ?? 0);
                    writer.WritePackedInt32(slotsCount);
                    for (int i = 0; i < slotsCount; i++)
                    {
                        writer.WriteInt32(SlotValues[i]);
                        writer.WriteInt32(SlotCounts[i]);
                    }
                    // 方案 1：背包同步版本号
                    writer.WriteInt32(InventoryVersion);
                    break;
                case PickAction.WaterSplash:
                    writer.WritePackedInt32(Id);
                    writer.WriteVector3(writer, Position);
                    break;
                case PickAction.RequestAcquire:
                    writer.WritePackedInt32(Id);
                    writer.WriteInt32(RequestId);
                    writer.WriteVector3(writer, Position);
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
