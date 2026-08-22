using System;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public sealed class PlayerCapabilityMessage : Message
    {
        public int TargetClientId;
        public int Sequence;
        public PlayerCapabilityFlags Capabilities;

        protected override void Read(SuReader reader)
        {
            TargetClientId = reader.ReadInt32();
            Sequence = reader.ReadInt32();
            Capabilities = (PlayerCapabilityFlags)reader.ReadByte();
            Validate();
        }

        protected override void Write(SuWriter writer)
        {
            Validate();
            writer.WriteInt32(TargetClientId);
            writer.WriteInt32(Sequence);
            writer.WriteByte((byte)Capabilities);
        }

        private void Validate()
        {
            const PlayerCapabilityFlags supported = PlayerCapabilityFlags.CreativeFly |
                PlayerCapabilityFlags.WorldControl | PlayerCapabilityFlags.CreativeInventory;
            if (TargetClientId <= 0 || Sequence <= 0 ||
                (Capabilities & ~supported) != PlayerCapabilityFlags.None)
                throw new InvalidOperationException("Invalid player capability message.");
        }
    }
}
