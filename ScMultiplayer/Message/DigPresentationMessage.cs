using Comms;

namespace ScMultiplayer
{
    // Source: Survivalcraft/Game/ComponentDiggingCracks.cs:ComponentDiggingCracks.Draw
    [System.Serializable]
    public sealed class DigPresentationMessage : Message
    {
        public int PlayerIndex;
        public int Sequence;
        public bool IsActive;
        public int X;
        public int Y;
        public int Z;
        public int Face;
        public float Progress;

        public DigPresentationMessage() { }

        public DigPresentationMessage(int playerIndex, int sequence, bool isActive,
            int x, int y, int z, int face, float progress)
        {
            PlayerIndex = playerIndex;
            Sequence = sequence;
            IsActive = isActive;
            X = x;
            Y = y;
            Z = z;
            Face = face;
            Progress = progress;
        }

        protected override void Read(SuReader reader)
        {
            PlayerIndex = reader.ReadInt32();
            Sequence = reader.ReadInt32();
            IsActive = reader.ReadBoolean();
            X = reader.ReadInt32();
            Y = reader.ReadInt32();
            Z = reader.ReadInt32();
            Face = reader.ReadPackedInt32();
            Progress = reader.ReadSingle();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(PlayerIndex);
            writer.WriteInt32(Sequence);
            writer.WriteBoolean(IsActive);
            writer.WriteInt32(X);
            writer.WriteInt32(Y);
            writer.WriteInt32(Z);
            writer.WritePackedInt32(Face);
            writer.WriteSingle(Progress);
        }
    }
}
