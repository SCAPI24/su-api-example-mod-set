using System;
using Comms;
using Game;

namespace ScMultiplayer
{
    [Serializable]
    public class GameWorldInfoMessage1 : Message
    {
        public double TimeOfDayOffset;
        public double TotalElapsedGameTime;
        public TimeOfDayMode CurrentTimeMode;

        public GameWorldInfoMessage1()
        {
        }

        public GameWorldInfoMessage1(double timeOfDayOffset, double totalElapsedGameTime, TimeOfDayMode currentTimeMode)
        {
            TimeOfDayOffset = timeOfDayOffset;
            TotalElapsedGameTime = totalElapsedGameTime;
            CurrentTimeMode = currentTimeMode;
        }

        protected override void Read(SuReader reader)
        {
            TimeOfDayOffset = reader.ReadDouble();
            TotalElapsedGameTime = reader.ReadDouble();
            CurrentTimeMode = (TimeOfDayMode)reader.ReadInt32();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteDouble(TimeOfDayOffset);
            writer.WriteDouble(TotalElapsedGameTime);
            writer.WriteInt32((int)CurrentTimeMode);
        }
    }
}