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
        public WorldControlAction Actions;

        public WorldControlRequestMessage()
        {
        }

        public WorldControlRequestMessage(WorldControlAction actions)
        {
            Actions = actions;
        }

        protected override void Read(SuReader reader)
        {
            Actions = (WorldControlAction)reader.ReadByte();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteByte((byte)Actions);
        }
    }
}
