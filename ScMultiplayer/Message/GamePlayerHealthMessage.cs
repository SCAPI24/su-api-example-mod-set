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
        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:NetworkMessageSender.SendPlayerJumpRequest
        // Deduplicates the immediate copies and fences post-hit position snapshots.
        public int KnockbackSequence;
        public int KnockbackServerTick;
        // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
        // Keeps the owning client under the same native locomotion stun as the host.
        public float KnockbackStunTime;
        public bool IsSleeping;
        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Sleep
        // Host time is the shared sleep session boundary. Clients must not derive it from
        // the local request arrival time, otherwise automatic wake-up happens at different times.
        public double SleepStartTime;
        public float SleepFactor;
        public float FireDuration;
        public float FluDuration;
        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.Update
        // 「受冻积累」计时（`m_fluOnset`）：客户端不跑流感模拟，这里只是把主机的病程状态带全，
        // 供 HUD/调试使用（会不会得流感完全由主机判定）。
        public float FluOnset;
        public float SicknessDuration;
        // ====================================================================
        // 表现事件（主机产生 → 客户端播放）。
        // 分工：本地**不做任何预测/模拟**，只按这些边沿把效果播出来；主机那边这些组件照常跑
        // 原生逻辑，跑完把事件编成单调递增的序号发出去。序号是幂等标记 —— 漏收不会重复播，
        // 收到新序号才播一次。
        // ====================================================================
        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.Sneeze / Cough
        public int SneezeSequence;
        public bool IsSneezing;
        public int CoughSequence;
        public bool IsCoughing;
        // Source: Survivalcraft/Game/ComponentSickness.cs:ComponentSickness.NauseaEffect
        public int NauseaSequence;
        public bool NauseaPuked;
        public bool IsPuking;
        public float GreenoutSeconds;
        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.FluEffect
        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.UpdateTemperature
        // 黑屏（流感发作 / 过热）剩余秒数：取主机两份计时里的较大者，客户端按引擎同样的
        // 上升 0.5/s、衰减 0.5/s 规则驱动 ScreenOverlays.BlackoutFactor。
        public float BlackoutSeconds;
        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.DisplaySmallMessage
        // 主机显示过的提示（流感/寒冷过热/饥饿疲劳/溺水/潮湿/进食…）：连文本一起转给客户端播。
        public int HintSequence;
        public string HintText;
        public string CauseOrSource;   // 伤害/治疗来源
        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Update
        public int DamageSequence;
        // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:SendAuthoritativePlayerHealth
        // Monotonically increases on the host so delayed reliable snapshots cannot restore
        // an older sleep, temperature or vital-stat state on a client.
        public int AuthoritativeStateSequence;
        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Sleep
        // Correlates a client sleep/wake request with the host's immediate authoritative reply.
        // Periodic host snapshots keep this at zero and therefore cannot acknowledge a newer
        // local sleep request by accident.
        public int SleepRequestSequence;

        public GamePlayerHealthMessage() { }

        public GamePlayerHealthMessage(int playerIndex, float health, float maxHealth,
            float healthChange, bool isDead, float air, float food, float stamina,
            float sleep, float temperature, float targetTemperature, float wetness, float level,
            Vector3 bodyVelocity, bool hasKnockback, bool isSleeping, float fireDuration,
            float fluDuration, float fluOnset, float sicknessDuration,
            string cause = null, double sleepStartTime = 0.0,
            float sleepFactor = 0f)
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
            SleepStartTime = sleepStartTime;
            SleepFactor = sleepFactor;
            FireDuration = fireDuration;
            FluDuration = fluDuration;
            FluOnset = fluOnset;
            SicknessDuration = sicknessDuration;
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
            if (HasKnockback)
            {
                KnockbackSequence = reader.ReadInt32();
                KnockbackServerTick = reader.ReadInt32();
                KnockbackStunTime = reader.ReadSingle();
            }
            IsSleeping = reader.ReadBoolean();
            SleepStartTime = reader.Position + 8 <= reader.Length
                ? reader.ReadDouble()
                : 0.0;
            SleepFactor = reader.Position + 4 <= reader.Length
                ? reader.ReadSingle()
                : 0f;
            FireDuration = reader.ReadSingle();
            FluDuration = reader.ReadSingle();
            FluOnset = reader.ReadSingle();
            SicknessDuration = reader.ReadSingle();
            // 表现事件边沿
            SneezeSequence = reader.ReadPackedInt32();
            IsSneezing = reader.ReadBoolean();
            CoughSequence = reader.ReadPackedInt32();
            IsCoughing = reader.ReadBoolean();
            NauseaSequence = reader.ReadPackedInt32();
            NauseaPuked = reader.ReadBoolean();
            IsPuking = reader.ReadBoolean();
            GreenoutSeconds = reader.ReadSingle();
            BlackoutSeconds = reader.ReadSingle();
            HintSequence = reader.ReadPackedInt32();
            HintText = reader.ReadString();
            DamageSequence = reader.ReadInt32();
            AuthoritativeStateSequence = reader.ReadInt32();
            SleepRequestSequence = reader.Position + 4 <= reader.Length
                ? reader.ReadInt32()
                : 0;
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
            if (HasKnockback)
            {
                writer.WriteInt32(KnockbackSequence);
                writer.WriteInt32(KnockbackServerTick);
                writer.WriteSingle(KnockbackStunTime);
            }
            writer.WriteBoolean(IsSleeping);
            writer.WriteDouble(SleepStartTime);
            writer.WriteSingle(SleepFactor);
            writer.WriteSingle(FireDuration);
            writer.WriteSingle(FluDuration);
            writer.WriteSingle(FluOnset);
            writer.WriteSingle(SicknessDuration);
            // 表现事件边沿
            writer.WritePackedInt32(SneezeSequence);
            writer.WriteBoolean(IsSneezing);
            writer.WritePackedInt32(CoughSequence);
            writer.WriteBoolean(IsCoughing);
            writer.WritePackedInt32(NauseaSequence);
            writer.WriteBoolean(NauseaPuked);
            writer.WriteBoolean(IsPuking);
            writer.WriteSingle(GreenoutSeconds);
            writer.WriteSingle(BlackoutSeconds);
            writer.WritePackedInt32(HintSequence);
            writer.WriteString(HintText ?? string.Empty);
            writer.WriteInt32(DamageSequence);
            writer.WriteInt32(AuthoritativeStateSequence);
            writer.WriteInt32(SleepRequestSequence);
        }
    }
}
