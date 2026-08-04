using Comms;
using Engine;
using System;

namespace ScMultiplayer
{
    [Serializable]
    public class MeleeHitResultMessage : Message
    {
        public int RequestSequence;
        public int ServerTick;
        public Vector3 HitPoint;
        public Vector3 HitDirection;
        public Vector3 AttackerVelocity;
        public float Damage;

        public MeleeHitResultMessage()
        {
        }

        public MeleeHitResultMessage(int requestSequence, int serverTick,
            Vector3 hitPoint, Vector3 hitDirection, Vector3 attackerVelocity, float damage)
        {
            RequestSequence = requestSequence;
            ServerTick = serverTick;
            HitPoint = hitPoint;
            HitDirection = hitDirection;
            AttackerVelocity = attackerVelocity;
            Damage = damage;
        }

        protected override void Read(SuReader reader)
        {
            RequestSequence = reader.ReadInt32();
            ServerTick = reader.ReadInt32();
            HitPoint = reader.ReadVector3(reader);
            HitDirection = reader.ReadVector3(reader);
            AttackerVelocity = reader.ReadVector3(reader);
            Damage = reader.ReadSingle();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(RequestSequence);
            writer.WriteInt32(ServerTick);
            writer.WriteVector3(writer, HitPoint);
            writer.WriteVector3(writer, HitDirection);
            writer.WriteVector3(writer, AttackerVelocity);
            writer.WriteSingle(Damage);
        }
    }
}
