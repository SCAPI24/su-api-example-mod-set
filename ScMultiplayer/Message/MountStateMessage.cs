using System;
using Comms;
using Engine;

namespace ScMultiplayer
{
    public enum MountStateKind : byte
    {
        Rejected,
        Mounting,
        Mounted,
        Dismounting,
        Dismounted
    }

    [Serializable]
    public class MountStateMessage : Message
    {
        public int PlayerIndex;
        public int ActionSequence;
        public int StateSequence;
        public MountStateKind State;
        public ushort MountEntityId;
        public int ServerTick;
        public Vector3 MountPosition;
        public Quaternion MountRotation;

        public MountStateMessage()
        {
        }

        public MountStateMessage(int playerIndex, int actionSequence, int stateSequence,
            MountStateKind state, ushort mountEntityId, int serverTick,
            Vector3 mountPosition, Quaternion mountRotation)
        {
            PlayerIndex = playerIndex;
            ActionSequence = actionSequence;
            StateSequence = stateSequence;
            State = state;
            MountEntityId = mountEntityId;
            ServerTick = serverTick;
            MountPosition = mountPosition;
            MountRotation = mountRotation;
        }

        // Source: Survivalcraft/Game/ComponentRider.cs:ComponentRider.Mount
        // The result always carries the host's actual parent mount identity. During the native
        // dismount animation that identity remains nonzero until ParentBody is released.
        public bool IsRiding => MountEntityId != 0;

        protected override void Read(SuReader reader)
        {
            PlayerIndex = reader.ReadInt32();
            ActionSequence = reader.ReadInt32();
            StateSequence = reader.ReadInt32();
            State = (MountStateKind)reader.ReadByte();
            MountEntityId = (ushort)reader.ReadPackedInt32();
            ServerTick = reader.ReadInt32();
            MountPosition = reader.ReadVector3(reader);
            MountRotation = reader.ReadQuaternion(reader);
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(PlayerIndex);
            writer.WriteInt32(ActionSequence);
            writer.WriteInt32(StateSequence);
            writer.WriteByte((byte)State);
            writer.WritePackedInt32(MountEntityId);
            writer.WriteInt32(ServerTick);
            writer.WriteVector3(writer, MountPosition);
            writer.WriteQuaternion(writer, MountRotation);
        }
    }
}
