using System;
using Engine;
using Comms;

namespace ScMultiplayer
{
    /// <summary>
    /// 玩家生命值同步消息
    /// </summary>
    [Serializable]
    public class GamePlayerHealthMessage : Message
    {
        public int PlayerIndex;
        public float Health;
        public float MaxHealth;
        public float HealthChange;     // 正=治疗,负=受伤
        public bool IsDead;
        public float Air;
        public float Food;
        public float Stamina;
        public float Sleep;
        public float Temperature;
        public float TargetTemperature;
        public float Wetness;
        public float Level;
        public Vector3 BodyVelocity;
        public bool HasKnockback;
        public bool IsSleeping;
        public float FireDuration;
        public float FluDuration;
        public float SicknessDuration;
        public int CoughSequence;
        public bool IsCoughing;
        public string CauseOrSource;   // 伤害/治疗来源

        public GamePlayerHealthMessage() { }

        public GamePlayerHealthMessage(int playerIndex, float health, float maxHealth,
            float healthChange, bool isDead, float air, float food, float stamina,
            float sleep, float temperature, float targetTemperature, float wetness, float level,
            Vector3 bodyVelocity, bool hasKnockback, bool isSleeping, float fireDuration,
            float fluDuration, float sicknessDuration, int coughSequence,
            bool isCoughing, string cause = null)
        {
            PlayerIndex = playerIndex;
            Health = health;
            MaxHealth = maxHealth;
            HealthChange = healthChange;
            IsDead = isDead;
            CauseOrSource = cause ?? string.Empty;
            Air = air;
            Food = food;
            Stamina = stamina;
            Sleep = sleep;
            Temperature = temperature;
            TargetTemperature = targetTemperature;
            Wetness = wetness;
            Level = level;
            BodyVelocity = bodyVelocity;
            HasKnockback = hasKnockback;
            IsSleeping = isSleeping;
            FireDuration = fireDuration;
            FluDuration = fluDuration;
            SicknessDuration = sicknessDuration;
            CoughSequence = coughSequence;
            IsCoughing = isCoughing;
        }

        protected override void Read(SuReader reader)
        {
            PlayerIndex = reader.ReadInt32();
            Health = reader.ReadSingle();
            MaxHealth = reader.ReadSingle();
            HealthChange = reader.ReadSingle();
            IsDead = reader.ReadBoolean();
            CauseOrSource = reader.ReadString();
            Air = reader.ReadSingle();
            Food = reader.ReadSingle();
            Stamina = reader.ReadSingle();
            Sleep = reader.ReadSingle();
            Temperature = reader.ReadSingle();
            TargetTemperature = reader.ReadSingle();
            Wetness = reader.ReadSingle();
            Level = reader.ReadSingle();
            BodyVelocity = reader.ReadVector3(reader);
            HasKnockback = reader.ReadBoolean();
            IsSleeping = reader.ReadBoolean();
            FireDuration = reader.ReadSingle();
            FluDuration = reader.ReadSingle();
            SicknessDuration = reader.ReadSingle();
            CoughSequence = reader.ReadInt32();
            IsCoughing = reader.ReadBoolean();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(PlayerIndex);
            writer.WriteSingle(Health);
            writer.WriteSingle(MaxHealth);
            writer.WriteSingle(HealthChange);
            writer.WriteBoolean(IsDead);
            writer.WriteString(CauseOrSource ?? string.Empty);
            writer.WriteSingle(Air);
            writer.WriteSingle(Food);
            writer.WriteSingle(Stamina);
            writer.WriteSingle(Sleep);
            writer.WriteSingle(Temperature);
            writer.WriteSingle(TargetTemperature);
            writer.WriteSingle(Wetness);
            writer.WriteSingle(Level);
            writer.WriteVector3(writer, BodyVelocity);
            writer.WriteBoolean(HasKnockback);
            writer.WriteBoolean(IsSleeping);
            writer.WriteSingle(FireDuration);
            writer.WriteSingle(FluDuration);
            writer.WriteSingle(SicknessDuration);
            writer.WriteInt32(CoughSequence);
            writer.WriteBoolean(IsCoughing);
        }
    }
}
