using Comms;
using System;

namespace ScMultiplayer
{
    public enum GamePakWorldReadyStage
    {
        LoadingProject,
        ProjectReady,
        CatchUpBatchApplied,
        CatchUpBatchComplete,
        ReadyToPlay
    }

    [Serializable]
    public class GamePakWorldReadyMessage : Message
    {
        public int TransferId;
        public GamePakWorldReadyStage Stage;

        public GamePakWorldReadyMessage()
        {
        }

        public GamePakWorldReadyMessage(int transferId, GamePakWorldReadyStage stage)
        {
            TransferId = transferId;
            Stage = stage;
        }

        protected override void Read(SuReader reader)
        {
            TransferId = reader.ReadInt32();
            Stage = (GamePakWorldReadyStage)reader.ReadInt32();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(TransferId);
            writer.WriteInt32((int)Stage);
        }
    }
}
