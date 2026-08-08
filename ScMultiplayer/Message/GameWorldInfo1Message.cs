using System;
using Comms;
using Game;
using Engine;

namespace ScMultiplayer
{
    [Serializable]
    public class GameWorldInfoMessage1 : Message
    {
        public int ServerTick;
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
        public long TerrainSequence;
        public int WorldTimeRevision;
        public bool IsTimeAccelerated;

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
            // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.TriggerNetworkTick
            ServerTick = reader.ReadInt32();
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
            TerrainSequence = reader.Position + 8 <= reader.Length
                ? reader.ReadInt64()
                : 0L;
            WorldTimeRevision = reader.ReadPackedInt32(1, int.MaxValue);
            IsTimeAccelerated = reader.Position < reader.Length && reader.ReadBoolean();
        }

        protected override void Write(SuWriter writer)
        {
            // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.TriggerNetworkTick
            if (WorldTimeRevision <= 0)
                throw new InvalidOperationException("Invalid world-time revision.");
            writer.WriteInt32(ServerTick);
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
            writer.WriteInt64(TerrainSequence);
            writer.WritePackedInt32(WorldTimeRevision);
            writer.WriteBoolean(IsTimeAccelerated);
        }
    }
}
