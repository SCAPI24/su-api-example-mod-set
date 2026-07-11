using Engine;
using System;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public class ExplosionSyncMessage : Message
    {
        public Vector3 Position;
        public float Radius;
        public int Damage;
        public bool IsIncendiary;
        public bool NoExplosionSound;
        public float SmokeSize;
        public string Cause;

        public ExplosionSyncMessage() { }

        public ExplosionSyncMessage(Vector3 position, float radius, int damage, bool isIncendiary, bool noSound, float smokeSize = 0f, string cause = null)
        {
            Position = position; Radius = radius; Damage = damage;
            IsIncendiary = isIncendiary; NoExplosionSound = noSound;
            SmokeSize = smokeSize; Cause = cause ?? string.Empty;
        }

        protected override void Read(SuReader reader)
        {
            Position = reader.ReadVector3(reader);
            Radius = reader.ReadSingle();
            Damage = reader.ReadInt32();
            IsIncendiary = reader.ReadBoolean();
            NoExplosionSound = reader.ReadBoolean();
            SmokeSize = reader.ReadSingle();
            Cause = reader.ReadString();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteVector3(writer, Position);
            writer.WriteSingle(Radius);
            writer.WriteInt32(Damage);
            writer.WriteBoolean(IsIncendiary);
            writer.WriteBoolean(NoExplosionSound);
            writer.WriteSingle(SmokeSize);
            writer.WriteString(Cause ?? string.Empty);
        }
    }
}
