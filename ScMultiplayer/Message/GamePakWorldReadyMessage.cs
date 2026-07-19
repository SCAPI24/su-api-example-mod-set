using Comms;
using System;

namespace ScMultiplayer
{
    [Serializable]
    public class GamePakWorldReadyMessage : Message
    {
        public int TransferId;
        public bool IsProjectReady;

        public GamePakWorldReadyMessage()
        {
        }

        public GamePakWorldReadyMessage(int transferId, bool isProjectReady = true)
        {
            TransferId = transferId;
            IsProjectReady = isProjectReady;
        }

        protected override void Read(SuReader reader)
        {
            TransferId = reader.ReadInt32();
            IsProjectReady = reader.ReadBoolean();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(TransferId);
            writer.WriteBoolean(IsProjectReady);
        }
    }
}
