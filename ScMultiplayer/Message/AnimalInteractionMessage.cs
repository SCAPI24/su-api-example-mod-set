using Comms;
using Engine;
using System;

namespace ScMultiplayer
{
    [Serializable]
    public class AnimalInteractionMessage : Message
    {
        public ushort TargetAnimalId;
        public int ClientTick;
        public Vector3 HitPoint;
        public Vector3 HitDirection;

        public AnimalInteractionMessage()
        {
        }

        public AnimalInteractionMessage(ushort targetAnimalId, int clientTick,
            Vector3 hitPoint, Vector3 hitDirection)
        {
            TargetAnimalId = targetAnimalId;
            ClientTick = clientTick;
            HitPoint = hitPoint;
            HitDirection = hitDirection;
        }

        protected override void Read(SuReader reader)
        {
            TargetAnimalId = (ushort)reader.ReadPackedInt32();
            ClientTick = reader.ReadInt32();
            HitPoint = reader.ReadVector3(reader);
            HitDirection = reader.ReadVector3(reader);
        }

        protected override void Write(SuWriter writer)
        {
            writer.WritePackedInt32(TargetAnimalId);
            writer.WriteInt32(ClientTick);
            writer.WriteVector3(writer, HitPoint);
            writer.WriteVector3(writer, HitDirection);
        }
    }
}
