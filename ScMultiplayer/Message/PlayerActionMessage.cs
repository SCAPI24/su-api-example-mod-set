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
        Whistle
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
        public Vector3 Position;

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
            }
            if (Action == PlayerActionType.RespawnRequest ||
                Action == PlayerActionType.DropRequest ||
                Action == PlayerActionType.Whistle)
                Position = reader.ReadVector3(reader);
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
            }
            if (Action == PlayerActionType.RespawnRequest ||
                Action == PlayerActionType.DropRequest ||
                Action == PlayerActionType.Whistle)
                writer.WriteVector3(writer, Position);
        }
    }
}
