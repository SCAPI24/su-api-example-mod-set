using Comms;
using Engine;
using System;

namespace ScMultiplayer
{
    // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Dig
    [Serializable]
    public class TerrainDigRequestMessage : Message
    {
        public int RequestId;
        public Point3 Cell;
        public int ExpectedValue;
        public int PredictedValue;
        public Ray3 DigRay;
        public int HitFace;
        public int StartClientTick;
        public int CompletedClientTick;
        public int ActiveSlotIndex;
        public int ToolValue;
        public int ToolCount;
        public Vector3 BodyPosition;

        public TerrainDigRequestMessage()
        {
        }

        public TerrainDigRequestMessage(int requestId, Point3 cell, int expectedValue,
            int predictedValue, Ray3 digRay, int startClientTick, int completedClientTick)
            : this(requestId, cell, expectedValue, predictedValue, digRay,
                -1, startClientTick, completedClientTick, -1, 0)
        {
        }

        public TerrainDigRequestMessage(int requestId, Point3 cell, int expectedValue,
            int predictedValue, Ray3 digRay, int startClientTick, int completedClientTick,
            int activeSlotIndex, int toolValue)
            : this(requestId, cell, expectedValue, predictedValue, digRay,
                -1, startClientTick, completedClientTick, activeSlotIndex, toolValue)
        {
        }

        public TerrainDigRequestMessage(int requestId, Point3 cell, int expectedValue,
            int predictedValue, Ray3 digRay, int hitFace, int startClientTick,
            int completedClientTick, int activeSlotIndex, int toolValue)
        {
            RequestId = requestId;
            Cell = cell;
            ExpectedValue = expectedValue;
            PredictedValue = predictedValue;
            DigRay = digRay;
            HitFace = hitFace;
            StartClientTick = startClientTick;
            CompletedClientTick = completedClientTick;
            ActiveSlotIndex = activeSlotIndex;
            ToolValue = toolValue;
        }

        protected override void Read(SuReader reader)
        {
            RequestId = reader.ReadInt32();
            Cell = reader.ReadPoint3();
            ExpectedValue = reader.ReadInt32();
            PredictedValue = reader.ReadInt32();
            DigRay = reader.ReadRay3(reader);
            HitFace = reader.ReadInt32();
            StartClientTick = reader.ReadInt32();
            CompletedClientTick = reader.ReadInt32();
            ActiveSlotIndex = reader.ReadInt32();
            ToolValue = reader.ReadInt32();
            ToolCount = reader.ReadInt32();
            BodyPosition = reader.ReadVector3(reader);
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(RequestId);
            writer.WritePoint3(Cell);
            writer.WriteInt32(ExpectedValue);
            writer.WriteInt32(PredictedValue);
            writer.WriteRay3(writer, DigRay);
            writer.WriteInt32(HitFace);
            writer.WriteInt32(StartClientTick);
            writer.WriteInt32(CompletedClientTick);
            writer.WriteInt32(ActiveSlotIndex);
            writer.WriteInt32(ToolValue);
            writer.WriteInt32(ToolCount);
            writer.WriteVector3(writer, BodyPosition);
        }
    }
}
