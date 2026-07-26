using System;
using Comms;

namespace ScMultiplayer
{
    [Flags]
    public enum WorldControlAction : byte
    {
        None = 0,
        TimeOfDay = 1,
        Precipitation = 2,
        Fog = 4,
        Lightning = 8
    }

    [Serializable]
    public class WorldControlRequestMessage : Message
    {
        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.TrySendWorldControlRequest
        public int RequestId;
        public WorldControlAction Actions;

        public WorldControlRequestMessage()
        {
        }

        public WorldControlRequestMessage(int requestId, WorldControlAction actions)
        {
            RequestId = requestId;
            Actions = actions;
        }

        protected override void Read(SuReader reader)
        {
            RequestId = reader.ReadInt32();
            Actions = (WorldControlAction)reader.ReadByte();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(RequestId);
            writer.WriteByte((byte)Actions);
        }
    }
}
