using System;
using System.Collections.Generic;
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

        /// <summary>主机权威的 WorldSettings 快照（字段名→值），客户端据此自行应用。</summary>
        public Dictionary<string, string> WorldSettings =
            new Dictionary<string, string>(StringComparer.Ordinal);

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
            if (reader.Position < reader.Length)
            {
                int settingsCount = reader.ReadPackedInt32();
                WorldSettings = new Dictionary<string, string>(settingsCount,
                    StringComparer.Ordinal);
                for (int i = 0; i < settingsCount; i++)
                    WorldSettings[reader.ReadString()] = reader.ReadString();
            }
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
            writer.WritePackedInt32(WorldSettings?.Count ?? 0);
            if (WorldSettings != null)
            {
                foreach (KeyValuePair<string, string> item in WorldSettings)
                {
                    writer.WriteString(item.Key);
                    writer.WriteString(item.Value ?? string.Empty);
                }
            }
        }
    }
}
