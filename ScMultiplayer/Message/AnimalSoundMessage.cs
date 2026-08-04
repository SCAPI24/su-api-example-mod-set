using Comms;
using Engine;
using System;

namespace ScMultiplayer
{
    public enum AnimalSoundType : byte
    {
        Idle,
        Attack,
        Howl
    }

    [Serializable]
    public class AnimalSoundMessage : Message
    {
        public ushort EntityId;
        public int Sequence;
        public int ServerTick;
        public AnimalSoundType SoundType;
        public Vector3 Position;

        public AnimalSoundMessage()
        {
        }

        public AnimalSoundMessage(ushort entityId, int sequence, int serverTick,
            AnimalSoundType soundType, Vector3 position)
        {
            EntityId = entityId;
            Sequence = sequence;
            ServerTick = serverTick;
            SoundType = soundType;
            Position = position;
        }

        protected override void Read(SuReader reader)
        {
            EntityId = (ushort)reader.ReadPackedInt32();
            Sequence = reader.ReadInt32();
            ServerTick = reader.ReadInt32();
            SoundType = (AnimalSoundType)reader.ReadByte();
            Position = reader.ReadVector3(reader);
        }

        protected override void Write(SuWriter writer)
        {
            writer.WritePackedInt32(EntityId);
            writer.WriteInt32(Sequence);
            writer.WriteInt32(ServerTick);
            writer.WriteByte((byte)SoundType);
            writer.WriteVector3(writer, Position);
        }
    }
}
