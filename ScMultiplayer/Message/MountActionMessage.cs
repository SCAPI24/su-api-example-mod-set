using System;
using Comms;
using Engine;

namespace ScMultiplayer
{
    public enum MountActionKind : byte
    {
        Mount,
        Dismount
    }

    [Serializable]
    public class MountActionMessage : Message
    {
        public int PlayerIndex;
        public int ActionSequence;
        public MountActionKind Action;
        public ushort MountEntityId;
        public Vector3 BodyPosition;
        public Quaternion BodyRotation;
        public Vector2 LookAngles;
        public int ClientTick;

        public MountActionMessage()
        {
        }

        public MountActionMessage(int playerIndex, int actionSequence,
            MountActionKind action, ushort mountEntityId, Vector3 bodyPosition,
            Quaternion bodyRotation, Vector2 lookAngles, int clientTick)
        {
            PlayerIndex = playerIndex;
            ActionSequence = actionSequence;
            Action = action;
            MountEntityId = mountEntityId;
            BodyPosition = bodyPosition;
            BodyRotation = bodyRotation;
            LookAngles = lookAngles;
            ClientTick = clientTick;
        }

        protected override void Read(SuReader reader)
        {
            PlayerIndex = reader.ReadInt32();
            ActionSequence = reader.ReadInt32();
            Action = (MountActionKind)reader.ReadByte();
            MountEntityId = (ushort)reader.ReadPackedInt32();
            BodyPosition = reader.ReadVector3(reader);
            BodyRotation = reader.ReadQuaternion(reader);
            LookAngles = reader.ReadVector2(reader);
            ClientTick = reader.ReadInt32();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(PlayerIndex);
            writer.WriteInt32(ActionSequence);
            writer.WriteByte((byte)Action);
            writer.WritePackedInt32(MountEntityId);
            writer.WriteVector3(writer, BodyPosition);
            writer.WriteQuaternion(writer, BodyRotation);
            writer.WriteVector2(writer, LookAngles);
            writer.WriteInt32(ClientTick);
        }
    }
}
