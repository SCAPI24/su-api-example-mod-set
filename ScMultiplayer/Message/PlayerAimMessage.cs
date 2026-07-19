using Comms;
using Engine;
using System;

namespace ScMultiplayer
{
    public enum PlayerAimAction : byte
    {
        Start,
        Update,
        Release,
        Cancel
    }

    // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
    // Aim release is an edge-triggered action and must not depend on a later null PlayerInput
    // surviving tick batching. The host still executes the original ComponentMiner aim behavior.
    [Serializable]
    public class PlayerAimMessage : Message
    {
        public int Sequence;
        public PlayerAimAction Action;
        public Ray3 Aim;
        public int ActiveSlotIndex;
        public int ItemValue;
        public int ItemCount;
        public Vector3 BodyPosition;
        public Quaternion BodyRotation;

        public PlayerAimMessage()
        {
        }

        public PlayerAimMessage(int sequence, PlayerAimAction action, Ray3 aim,
            int activeSlotIndex, int itemValue, int itemCount, Vector3 bodyPosition,
            Quaternion bodyRotation)
        {
            Sequence = sequence;
            Action = action;
            Aim = aim;
            ActiveSlotIndex = activeSlotIndex;
            ItemValue = itemValue;
            ItemCount = itemCount;
            BodyPosition = bodyPosition;
            BodyRotation = bodyRotation;
        }

        protected override void Read(SuReader reader)
        {
            Sequence = reader.ReadInt32();
            Action = (PlayerAimAction)reader.ReadByte();
            Aim = reader.ReadRay3(reader);
            ActiveSlotIndex = reader.ReadInt32();
            ItemValue = reader.ReadInt32();
            ItemCount = reader.ReadInt32();
            BodyPosition = reader.ReadVector3(reader);
            BodyRotation = reader.ReadQuaternion(reader);
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(Sequence);
            writer.WriteByte((byte)Action);
            writer.WriteRay3(writer, Aim);
            writer.WriteInt32(ActiveSlotIndex);
            writer.WriteInt32(ItemValue);
            writer.WriteInt32(ItemCount);
            writer.WriteVector3(writer, BodyPosition);
            writer.WriteQuaternion(writer, BodyRotation);
        }
    }
}
