using Comms;
using System;

namespace ScMultiplayer
{
    [Serializable]
    public class GamePakWorldReadyMessage : Message
    {
        public int TransferId;

        public GamePakWorldReadyMessage()
        {
        }

        public GamePakWorldReadyMessage(int transferId)
        {
            TransferId = transferId;
        }

        protected override void Read(SuReader reader)
        {
            TransferId = reader.ReadInt32();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(TransferId);
        }
    }
}
