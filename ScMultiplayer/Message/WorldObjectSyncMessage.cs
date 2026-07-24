using Comms;
using Engine;
using Game;
using System;
using System.Collections.Generic;

namespace ScMultiplayer
{
    public enum WorldObjectSyncStage : byte
    {
        SnapshotChunk = 1,
        FurnaceBatch = 2,
        PistonBatch = 3,
        SnapshotRequest = 4
    }

    public enum WorldObjectSnapshotKind : byte
    {
        Furniture = 1,
        Signs = 2
    }

    public sealed class FurnaceStateRecord
    {
        public Point3 Point;
        public float FireTimeRemaining;
        public float HeatLevel;
        public float SmeltingProgress;
    }

    public sealed class PistonMotionRecord
    {
        public Point3 Point;
        public Vector3 StartPosition;
        public Vector3 Position;
        public Vector3 TargetPosition;
        public float Speed;
        public float Acceleration;
        public float Drag;
        public Vector2 Smoothness;
        public readonly List<MovingBlock> Blocks = new List<MovingBlock>();
    }

    // Source: Survivalcraft/Game/SubsystemFurnitureBlockBehavior.cs:
    // SubsystemFurnitureBlockBehavior.Save
    // Source: Survivalcraft/Game/SubsystemSignBlockBehavior.cs:
    // SubsystemSignBlockBehavior.Save
    // Source: Survivalcraft/Game/ComponentFurnace.cs:ComponentFurnace.Update
    // Source: Survivalcraft/Game/SubsystemMovingBlocks.cs:SubsystemMovingBlocks.Save
    [Serializable]
    public sealed class WorldObjectSyncMessage : Message
    {
        private const int MaximumChunkBytes = 24576;
        private const int MaximumFurnaces = 64;
        private const int MaximumPistons = 64;
        private const int MaximumPistonBlocks = 16;

        public WorldObjectSyncStage Stage;
        public WorldObjectSnapshotKind SnapshotKind;
        public bool IsRequest;
        public int SnapshotId;
        public int Revision;
        public int ChunkIndex;
        public int ChunkCount;
        public int TotalLength;
        public int MotionSequence;
        public bool IsComplete;
        public byte[] Chunk = Array.Empty<byte>();
        public readonly List<FurnaceStateRecord> Furnaces =
            new List<FurnaceStateRecord>();
        public readonly List<PistonMotionRecord> Pistons =
            new List<PistonMotionRecord>();

        protected override void Read(SuReader reader)
        {
            Stage = (WorldObjectSyncStage)reader.ReadByte();
            if (Stage == WorldObjectSyncStage.SnapshotRequest)
            {
                SnapshotKind = (WorldObjectSnapshotKind)reader.ReadByte();
                if (SnapshotKind != WorldObjectSnapshotKind.Furniture &&
                    SnapshotKind != WorldObjectSnapshotKind.Signs)
                    throw new InvalidOperationException("Invalid world-object snapshot kind.");
                return;
            }
            if (Stage == WorldObjectSyncStage.SnapshotChunk)
            {
                SnapshotKind = (WorldObjectSnapshotKind)reader.ReadByte();
                if (SnapshotKind != WorldObjectSnapshotKind.Furniture &&
                    SnapshotKind != WorldObjectSnapshotKind.Signs)
                    throw new InvalidOperationException("Invalid world-object snapshot kind.");
                byte request = reader.ReadByte();
                if (request > 1) throw new InvalidOperationException("Invalid snapshot request flag.");
                IsRequest = request != 0;
                SnapshotId = reader.ReadPackedInt32(1, int.MaxValue);
                Revision = reader.ReadPackedInt32(0, int.MaxValue);
                ChunkIndex = reader.ReadPackedInt32(0, 4095);
                ChunkCount = reader.ReadPackedInt32(1, 4096);
                TotalLength = reader.ReadPackedInt32(0, 16 * 1024 * 1024);
                Chunk = reader.ReadBytes();
                if (Chunk.Length > MaximumChunkBytes || ChunkIndex >= ChunkCount)
                    throw new InvalidOperationException("Invalid world-object snapshot chunk.");
                return;
            }
            if (Stage == WorldObjectSyncStage.FurnaceBatch)
            {
                int count = reader.ReadPackedInt32(0, MaximumFurnaces);
                for (int i = 0; i < count; i++)
                {
                    Furnaces.Add(new FurnaceStateRecord
                    {
                        Point = reader.ReadPoint3Fixed(),
                        FireTimeRemaining = reader.ReadSingle(),
                        HeatLevel = reader.ReadSingle(),
                        SmeltingProgress = reader.ReadSingle()
                    });
                }
                return;
            }
            if (Stage == WorldObjectSyncStage.PistonBatch)
            {
                MotionSequence = reader.ReadPackedInt32(1, int.MaxValue);
                IsComplete = reader.ReadBoolean();
                int count = reader.ReadPackedInt32(0, MaximumPistons);
                for (int i = 0; i < count; i++)
                {
                    var record = new PistonMotionRecord
                    {
                        Point = reader.ReadPoint3Fixed(),
                        StartPosition = reader.ReadVector3(reader),
                        Position = reader.ReadVector3(reader),
                        TargetPosition = reader.ReadVector3(reader),
                        Speed = reader.ReadSingle(),
                        Acceleration = reader.ReadSingle(),
                        Drag = reader.ReadSingle(),
                        Smoothness = reader.ReadVector2(reader)
                    };
                    int blocks = reader.ReadPackedInt32(0, MaximumPistonBlocks);
                    for (int j = 0; j < blocks; j++)
                    {
                        record.Blocks.Add(new MovingBlock
                        {
                            Value = reader.ReadInt32(),
                            Offset = reader.ReadPoint3Fixed()
                        });
                    }
                    Pistons.Add(record);
                }
                return;
            }
            throw new InvalidOperationException("Invalid world-object sync stage.");
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteByte((byte)Stage);
            if (Stage == WorldObjectSyncStage.SnapshotRequest)
            {
                if (SnapshotKind != WorldObjectSnapshotKind.Furniture &&
                    SnapshotKind != WorldObjectSnapshotKind.Signs)
                    throw new InvalidOperationException("Invalid world-object snapshot kind.");
                writer.WriteByte((byte)SnapshotKind);
                return;
            }
            if (Stage == WorldObjectSyncStage.SnapshotChunk)
            {
                if (Chunk == null || Chunk.Length > MaximumChunkBytes ||
                    ChunkCount < 1 || ChunkIndex < 0 || ChunkIndex >= ChunkCount)
                    throw new InvalidOperationException("Invalid world-object snapshot chunk.");
                if (SnapshotKind != WorldObjectSnapshotKind.Furniture &&
                    SnapshotKind != WorldObjectSnapshotKind.Signs)
                    throw new InvalidOperationException("Invalid world-object snapshot kind.");
                writer.WriteByte((byte)SnapshotKind);
                writer.WriteByte(IsRequest ? (byte)1 : (byte)0);
                writer.WritePackedInt32(SnapshotId);
                writer.WritePackedInt32(Revision);
                writer.WritePackedInt32(ChunkIndex);
                writer.WritePackedInt32(ChunkCount);
                writer.WritePackedInt32(TotalLength);
                writer.WriteBytes(Chunk);
                return;
            }
            if (Stage == WorldObjectSyncStage.FurnaceBatch)
            {
                if (Furnaces.Count > MaximumFurnaces)
                    throw new InvalidOperationException("Too many furnace states.");
                writer.WritePackedInt32(Furnaces.Count);
                foreach (FurnaceStateRecord record in Furnaces)
                {
                    writer.WritePoint3Fixed(record.Point);
                    writer.WriteSingle(record.FireTimeRemaining);
                    writer.WriteSingle(record.HeatLevel);
                    writer.WriteSingle(record.SmeltingProgress);
                }
                return;
            }
            if (Stage == WorldObjectSyncStage.PistonBatch)
            {
                if (MotionSequence <= 0)
                    throw new InvalidOperationException("Invalid piston motion sequence.");
                if (Pistons.Count > MaximumPistons)
                    throw new InvalidOperationException("Too many piston states.");
                writer.WritePackedInt32(MotionSequence);
                writer.WriteBoolean(IsComplete);
                writer.WritePackedInt32(Pistons.Count);
                foreach (PistonMotionRecord record in Pistons)
                {
                    if (record.Blocks.Count > MaximumPistonBlocks)
                        throw new InvalidOperationException("Too many piston moving blocks.");
                    writer.WritePoint3Fixed(record.Point);
                    writer.WriteVector3(writer, record.StartPosition);
                    writer.WriteVector3(writer, record.Position);
                    writer.WriteVector3(writer, record.TargetPosition);
                    writer.WriteSingle(record.Speed);
                    writer.WriteSingle(record.Acceleration);
                    writer.WriteSingle(record.Drag);
                    writer.WriteVector2(writer, record.Smoothness);
                    writer.WritePackedInt32(record.Blocks.Count);
                    foreach (MovingBlock block in record.Blocks)
                    {
                        writer.WriteInt32(block.Value);
                        writer.WritePoint3Fixed(block.Offset);
                    }
                }
                return;
            }
            throw new InvalidOperationException("Invalid world-object sync stage.");
        }
    }
}
