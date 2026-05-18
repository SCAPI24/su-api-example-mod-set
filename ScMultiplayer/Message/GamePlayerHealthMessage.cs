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
        public string CauseOrSource;   // 伤害/治疗来源

        public GamePlayerHealthMessage() { }

        public GamePlayerHealthMessage(int playerIndex, float health, float maxHealth,
            float healthChange, bool isDead, string cause = null)
        {
            PlayerIndex = playerIndex;
            Health = health;
            MaxHealth = maxHealth;
            HealthChange = healthChange;
            IsDead = isDead;
            CauseOrSource = cause ?? string.Empty;
        }

        protected override void Read(SuReader reader)
        {
            PlayerIndex = reader.ReadInt32();
            Health = reader.ReadSingle();
            MaxHealth = reader.ReadSingle();
            HealthChange = reader.ReadSingle();
            IsDead = reader.ReadBoolean();
            CauseOrSource = reader.ReadString();
        }

        protected override void Write(SuWriter writer)
        {
            writer.WriteInt32(PlayerIndex);
            writer.WriteSingle(Health);
            writer.WriteSingle(MaxHealth);
            writer.WriteSingle(HealthChange);
            writer.WriteBoolean(IsDead);
            writer.WriteString(CauseOrSource ?? string.Empty);
        }
    }
}
