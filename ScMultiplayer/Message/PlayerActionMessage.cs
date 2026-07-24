using Comms;
using Engine;
using System;

namespace ScMultiplayer
{
    public enum PlayerActionType : byte
    {
        HitRequest,
        Poke,
        InteractRequest,
        LeaveRequest,
        RespawnRequest,
        DropRequest,
        Whistle,
        InteractResult
    }

    [Serializable]
    public class PlayerActionMessage : Message
    {
        public PlayerActionType Action;
        public int PlayerIndex;
        public int Sequence;
        public Ray3 HitRay;
        public int ActiveSlotIndex = -1;
        public int ItemValue;
        public int ItemCount;
        public int DropCount;
        public Vector3 Position;
        public Vector3 Velocity;
        public bool HasTerrainPrediction;
        public int RequestId;
        public Point3 Cell;
        public int ExpectedValue;
        public int PredictedValue;
        public bool Accepted;
        public int AuthoritativeValue;
        public int ServerTick;

        public PlayerActionMessage()
        {
        }

        public PlayerActionMessage(PlayerActionType action, int playerIndex,
            int sequence, Ray3 hitRay, int activeSlotIndex = -1,
            int itemValue = 0, int itemCount = 0)
        {
            Action = action;
            PlayerIndex = playerIndex;
            Sequence = sequence;
            HitRay = hitRay;
            ActiveSlotIndex = activeSlotIndex;
            ItemValue = itemValue;
            ItemCount = itemCount;
        }

        protected override void Read(SuReader reader)
        {
            Action = (PlayerActionType)reader.ReadByte();
            PlayerIndex = reader.ReadInt32();
            Sequence = reader.ReadInt32();
            if (Action == PlayerActionType.HitRequest ||
                Action == PlayerActionType.InteractRequest)
                HitRay = reader.ReadRay3(reader);
            if (Action == PlayerActionType.InteractRequest ||
                Action == PlayerActionType.DropRequest)
            {
                ActiveSlotIndex = reader.ReadInt32();
                ItemValue = reader.ReadInt32();
                ItemCount = reader.ReadInt32();
                if (Action == PlayerActionType.DropRequest)
                    DropCount = reader.ReadInt32();
            }
            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Place
            if (Action == PlayerActionType.InteractRequest)
            {
                HasTerrainPrediction = reader.ReadBoolean();
                if (HasTerrainPrediction)
                {
                    RequestId = reader.ReadInt32();
                    Cell = reader.ReadPoint3();
                    ExpectedValue = reader.ReadInt32();
                    PredictedValue = reader.ReadInt32();
                }
            }
            if (Action == PlayerActionType.InteractResult)
            {
                RequestId = reader.ReadInt32();
                Cell = reader.ReadPoint3();
                Accepted = reader.ReadBoolean();
                AuthoritativeValue = reader.ReadInt32();
                ServerTick = reader.ReadInt32();
            }
            if (Action == PlayerActionType.RespawnRequest ||
                Action == PlayerActionType.DropRequest ||
                Action == PlayerActionType.Whistle)
                Position = reader.ReadVector3(reader);
            if (Action == PlayerActionType.DropRequest)
                Velocity = reader.ReadVector3(reader);
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteByte((byte)Action);
            writer.WriteInt32(PlayerIndex);
            writer.WriteInt32(Sequence);
            if (Action == PlayerActionType.HitRequest ||
                Action == PlayerActionType.InteractRequest)
                writer.WriteRay3(writer, HitRay);
            if (Action == PlayerActionType.InteractRequest ||
                Action == PlayerActionType.DropRequest)
            {
                writer.WriteInt32(ActiveSlotIndex);
                writer.WriteInt32(ItemValue);
                writer.WriteInt32(ItemCount);
                if (Action == PlayerActionType.DropRequest)
                    writer.WriteInt32(DropCount);
            }
            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.Place
            if (Action == PlayerActionType.InteractRequest)
            {
                writer.WriteBoolean(HasTerrainPrediction);
                if (HasTerrainPrediction)
                {
                    writer.WriteInt32(RequestId);
                    writer.WritePoint3(Cell);
                    writer.WriteInt32(ExpectedValue);
                    writer.WriteInt32(PredictedValue);
                }
            }
            if (Action == PlayerActionType.InteractResult)
            {
                writer.WriteInt32(RequestId);
                writer.WritePoint3(Cell);
                writer.WriteBoolean(Accepted);
                writer.WriteInt32(AuthoritativeValue);
                writer.WriteInt32(ServerTick);
            }
            if (Action == PlayerActionType.RespawnRequest ||
                Action == PlayerActionType.DropRequest ||
                Action == PlayerActionType.Whistle)
                writer.WriteVector3(writer, Position);
            if (Action == PlayerActionType.DropRequest)
                writer.WriteVector3(writer, Velocity);
        }
    }
}
