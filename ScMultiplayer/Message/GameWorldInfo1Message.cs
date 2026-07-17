using System;
using Comms;
using Game;
using Engine;

namespace ScMultiplayer
{
    [Serializable]
    public class GameWorldInfoMessage1 : Message
    {
        public double TimeOfDayOffset;
        public double TotalElapsedGameTime;
        public TimeOfDayMode CurrentTimeMode;
        public bool IsPrecipitationStarted;
        public float PrecipitationIntensity;
        public bool IsFogStarted;
        public float FogProgress;
        public float FogIntensity;
        public int FogSeed;
        public bool HasLightningStrike;
        public Vector3 LightningStrikePosition;

        public GameWorldInfoMessage1()
        {
        }

        public GameWorldInfoMessage1(double timeOfDayOffset, double totalElapsedGameTime, TimeOfDayMode currentTimeMode,
            bool isPrecipitationStarted, float precipitationIntensity, bool isFogStarted,
            float fogProgress, float fogIntensity, int fogSeed,
            bool hasLightningStrike, Vector3 lightningStrikePosition)
        {
            TimeOfDayOffset = timeOfDayOffset;
            TotalElapsedGameTime = totalElapsedGameTime;
            CurrentTimeMode = currentTimeMode;
            IsPrecipitationStarted = isPrecipitationStarted;
            PrecipitationIntensity = precipitationIntensity;
            IsFogStarted = isFogStarted;
            FogProgress = fogProgress;
            FogIntensity = fogIntensity;
            FogSeed = fogSeed;
            HasLightningStrike = hasLightningStrike;
            LightningStrikePosition = lightningStrikePosition;
        }

        protected override void Read(SuReader reader)
        {
            TimeOfDayOffset = reader.ReadDouble();
            TotalElapsedGameTime = reader.ReadDouble();
            CurrentTimeMode = (TimeOfDayMode)reader.ReadInt32();
            IsPrecipitationStarted = reader.ReadBoolean();
            PrecipitationIntensity = reader.ReadSingle();
            IsFogStarted = reader.ReadBoolean();
            FogProgress = reader.ReadSingle();
            FogIntensity = reader.ReadSingle();
            FogSeed = reader.ReadInt32();
            HasLightningStrike = reader.ReadBoolean();
            if (HasLightningStrike) LightningStrikePosition = reader.ReadVector3(reader);
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteDouble(TimeOfDayOffset);
            writer.WriteDouble(TotalElapsedGameTime);
            writer.WriteInt32((int)CurrentTimeMode);
            writer.WriteBoolean(IsPrecipitationStarted);
            writer.WriteSingle(PrecipitationIntensity);
            writer.WriteBoolean(IsFogStarted);
            writer.WriteSingle(FogProgress);
            writer.WriteSingle(FogIntensity);
            writer.WriteInt32(FogSeed);
            writer.WriteBoolean(HasLightningStrike);
            if (HasLightningStrike) writer.WriteVector3(writer, LightningStrikePosition);
        }
    }
}
