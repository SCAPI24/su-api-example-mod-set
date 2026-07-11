using Engine;
using System;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public class ProjectileSyncMessage : Message
    {
        public enum ProjectileType : byte
        {
            Add,
            Remove
        }

        public ProjectileType Action;
        public int Value;
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        public Vector3 TrailOffset;
        public ushort OwnerEntityId;
        public bool IsFireProjectile;

        // Trail particle info
        public bool HasSmokeTrail;
        public byte ParticleCount;
        public float ParticleSize;
        public float ParticleDuration;
        public Color ParticleColor;

        public ProjectileSyncMessage() { }

        public ProjectileSyncMessage(ProjectileType action, int value, Vector3 pos, Vector3 vel, Vector3 angVel, Vector3 trail, ushort ownerId, bool isFire)
        {
            Action = action; Value = value; Position = pos; Velocity = vel;
            AngularVelocity = angVel; TrailOffset = trail; OwnerEntityId = ownerId; IsFireProjectile = isFire;
        }

        protected override void Read(SuReader reader)
        {
            Action = (ProjectileType)reader.ReadByte();
            Value = reader.ReadInt32();
            Position = reader.ReadVector3(reader);
            Velocity = reader.ReadVector3(reader);
            TrailOffset = reader.ReadVector3(reader);
            AngularVelocity = reader.ReadVector3(reader);
            OwnerEntityId = (ushort)reader.ReadPackedInt32();
            IsFireProjectile = reader.ReadBoolean();
            HasSmokeTrail = reader.ReadBoolean();
            if (HasSmokeTrail)
            {
                ParticleCount = reader.ReadByte();
                ParticleSize = reader.ReadSingle();
                ParticleDuration = reader.ReadSingle();
                // Read Color
                ParticleColor = new Color(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            }
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteByte((byte)Action);
            writer.WriteInt32(Value);
            writer.WriteVector3(writer, Position);
            writer.WriteVector3(writer, Velocity);
            writer.WriteVector3(writer, TrailOffset);
            writer.WriteVector3(writer, AngularVelocity);
            writer.WritePackedInt32(OwnerEntityId);
            writer.WriteBoolean(IsFireProjectile);
            writer.WriteBoolean(HasSmokeTrail);
            if (HasSmokeTrail)
            {
                writer.WriteByte(ParticleCount);
                writer.WriteSingle(ParticleSize);
                writer.WriteSingle(ParticleDuration);
                writer.WriteByte(ParticleColor.R);
                writer.WriteByte(ParticleColor.G);
                writer.WriteByte(ParticleColor.B);
                writer.WriteByte(ParticleColor.A);
            }
        }
    }
}
