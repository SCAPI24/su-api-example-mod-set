using System;

namespace ScMultiplayer.Core
{
    // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Update
    // This is a comparison-only value object. It owns no entity, network message or send state.
    internal readonly struct AuthoritativePlayerStateSnapshot
    {
        public AuthoritativePlayerStateSnapshot(float health, float air, float food,
            float stamina, float sleep, float temperature, float targetTemperature,
            float wetness, int wholeLevel, bool isSleeping)
        {
            Health = health;
            Air = air;
            Food = food;
            Stamina = stamina;
            Sleep = sleep;
            Temperature = temperature;
            TargetTemperature = targetTemperature;
            Wetness = wetness;
            WholeLevel = wholeLevel;
            IsSleeping = isSleeping;
        }

        public float Health { get; }
        public float Air { get; }
        public float Food { get; }
        public float Stamina { get; }
        public float Sleep { get; }
        public float Temperature { get; }
        public float TargetTemperature { get; }
        public float Wetness { get; }
        public int WholeLevel { get; }
        public bool IsSleeping { get; }

        public bool HasMeaningfulChangeFrom(AuthoritativePlayerStateSnapshot previous)
        {
            return Math.Abs(Health - previous.Health) > 0.0001f ||
                Math.Abs(Food - previous.Food) > 0.0001f ||
                Math.Abs(Air - previous.Air) >= 0.01f ||
                Math.Abs(Stamina - previous.Stamina) >= 0.02f ||
                Math.Abs(Sleep - previous.Sleep) >= 0.02f ||
                Math.Abs(Temperature - previous.Temperature) >= 0.1f ||
                Math.Abs(TargetTemperature - previous.TargetTemperature) >= 0.1f ||
                Math.Abs(Wetness - previous.Wetness) >= 0.02f ||
                WholeLevel != previous.WholeLevel || IsSleeping != previous.IsSleeping;
        }
    }
}
