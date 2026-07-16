using System;
using Engine;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public class GamePlayerPositionMessage : Message
    {
        public int PlayerIndex;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector2 LookAngles;

        public bool IsCrouching;
        public bool IsFlying;
        public bool IsRiding;
        public int ActiveSlotIndex;
        public int HandItemValue;
        public int HandItemCount;
        public Vector3 ItemOffset;
        public Vector3 ItemRotation;
        public float AimHandAngle;
        public int[] SlotValues;
        public int[] SlotCounts;

        public GamePlayerPositionMessage() { }

        public GamePlayerPositionMessage(int playerIndex, Vector3 position, Quaternion rotation, Vector3 velocity, Vector2 lookAngles,
            bool isCrouching, bool isFlying, bool isRiding,
            int activeSlotIndex, int handItemValue, int handItemCount,
            Vector3 itemOffset, Vector3 itemRotation, float aimHandAngle,
            int[] slotValues, int[] slotCounts)
        {
            PlayerIndex = playerIndex;
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            LookAngles = lookAngles;
            IsCrouching = isCrouching;
            IsFlying = isFlying;
            IsRiding = isRiding;
            ActiveSlotIndex = activeSlotIndex;
            HandItemValue = handItemValue;
            HandItemCount = handItemCount;
            ItemOffset = itemOffset;
            ItemRotation = itemRotation;
            AimHandAngle = aimHandAngle;
            SlotValues = slotValues ?? Array.Empty<int>();
            SlotCounts = slotCounts ?? Array.Empty<int>();
        }

        protected override void Read(SuReader reader)
        {
            PlayerIndex = reader.ReadInt32();
            Position = reader.ReadVector3(reader);
            Rotation = reader.ReadQuaternion(reader);
            Velocity = reader.ReadVector3(reader);
            LookAngles = reader.ReadVector2(reader);
            IsCrouching = reader.ReadBoolean();
            IsFlying = reader.ReadBoolean();
            IsRiding = reader.ReadBoolean();
            ActiveSlotIndex = reader.ReadInt32();
            HandItemValue = reader.ReadInt32();
            HandItemCount = reader.ReadInt32();
            ItemOffset = reader.ReadVector3(reader);
            ItemRotation = reader.ReadVector3(reader);
            AimHandAngle = reader.ReadSingle();
            int slotsCount = reader.ReadPackedInt32();
            SlotValues = new int[slotsCount];
            SlotCounts = new int[slotsCount];
            for (int i = 0; i < slotsCount; i++)
            {
                SlotValues[i] = reader.ReadInt32();
                SlotCounts[i] = reader.ReadInt32();
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(PlayerIndex);
            writer.WriteVector3(writer, Position);
            writer.WriteQuaternion(writer, Rotation);
            writer.WriteVector3(writer, Velocity);
            writer.WriteVector2(writer, LookAngles);
            writer.WriteBoolean(IsCrouching);
            writer.WriteBoolean(IsFlying);
            writer.WriteBoolean(IsRiding);
            writer.WriteInt32(ActiveSlotIndex);
            writer.WriteInt32(HandItemValue);
            writer.WriteInt32(HandItemCount);
            writer.WriteVector3(writer, ItemOffset);
            writer.WriteVector3(writer, ItemRotation);
            writer.WriteSingle(AimHandAngle);
            int slotsCount = Math.Min(SlotValues?.Length ?? 0, SlotCounts?.Length ?? 0);
            writer.WritePackedInt32(slotsCount);
            for (int i = 0; i < slotsCount; i++)
            {
                writer.WriteInt32(SlotValues[i]);
                writer.WriteInt32(SlotCounts[i]);
            }
        }






    }
}
