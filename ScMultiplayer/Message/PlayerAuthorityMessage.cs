using Comms;
using Engine;
using System;

namespace ScMultiplayer
{
    public enum PlayerAuthorityAction : byte
    {
        RespawnAnchor = 0,
        Teleport = 1
    }

    [Serializable]
    public sealed class PlayerAuthorityMessage : Message
    {
        public PlayerAuthorityAction Action;
        public int TargetClientId;
        public int Sequence;
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 SpawnPosition;

        protected override void Read(SuReader reader)
        {
            Action = (PlayerAuthorityAction)reader.ReadByte();
            TargetClientId = reader.ReadInt32();
            Sequence = reader.ReadInt32();
            Position = reader.ReadVector3(reader);
            Velocity = reader.ReadVector3(reader);
            SpawnPosition = reader.ReadVector3(reader);
            Validate();
        }

        protected override void Write(SuWriter writer)
        {
            Validate();
            writer.WriteByte((byte)Action);
            writer.WriteInt32(TargetClientId);
            writer.WriteInt32(Sequence);
            writer.WriteVector3(writer, Position);
            writer.WriteVector3(writer, Velocity);
            writer.WriteVector3(writer, SpawnPosition);
        }

        private void Validate()
        {
            if (!Enum.IsDefined(typeof(PlayerAuthorityAction), Action) ||
                TargetClientId <= 0 || Sequence <= 0 ||
                !IsFinite(Position) || !IsFinite(Velocity) || !IsFinite(SpawnPosition))
                throw new InvalidOperationException("Invalid player authority message.");
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
