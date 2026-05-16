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

        public GamePlayerPositionMessage() { }

        public GamePlayerPositionMessage(int playerIndex, Vector3 position, Quaternion rotation, Vector3 velocity, Vector2 lookAngles,
            bool isCrouching, bool isFlying, bool isRiding,
            int activeSlotIndex, int handItemValue, int handItemCount,
            Vector3 itemOffset, Vector3 itemRotation, float aimHandAngle)
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
        }






    }
}