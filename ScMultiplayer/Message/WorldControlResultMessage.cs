using System;
using Comms;

namespace ScMultiplayer
{
    public enum WorldControlTimeResult : byte
    {
        None,
        Dawn,
        Noon,
        Dusk,
        Midnight
    }

    [Serializable]
    public class WorldControlResultMessage : Message
    {
        public int RequestId;
        public WorldControlAction Actions;
        public WorldControlTimeResult TimeResult;
        public bool PrecipitationStarted;
        public bool FogStarted;
        public bool LightningTriggered;

        public WorldControlResultMessage()
        {
        }

        public WorldControlResultMessage(int requestId, WorldControlAction actions)
        {
            RequestId = requestId;
            Actions = actions;
        }

        // Source: Mod/ScMultiplayer/Message/WorldControlRequestMessage.cs:WorldControlRequestMessage.Read
        protected override void Read(SuReader reader)
        {
            RequestId = reader.ReadInt32();
            Actions = (WorldControlAction)reader.ReadByte();
            TimeResult = (WorldControlTimeResult)reader.ReadByte();
            PrecipitationStarted = reader.ReadBoolean();
            FogStarted = reader.ReadBoolean();
            LightningTriggered = reader.ReadBoolean();
        }

        // Source: Mod/ScMultiplayer/Message/WorldControlRequestMessage.cs:WorldControlRequestMessage.Write
        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(RequestId);
            writer.WriteByte((byte)Actions);
            writer.WriteByte((byte)TimeResult);
            writer.WriteBoolean(PrecipitationStarted);
            writer.WriteBoolean(FogStarted);
            writer.WriteBoolean(LightningTriggered);
        }
    }
}
