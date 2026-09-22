using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace ScMultiplayer
{
    /// <summary>
    /// `ComponentSickness` 的联机替换：**主机算、客户端播**（本地不做任何预测/模拟）。
    ///
    /// 引擎里"吃坏肚子"的每一样表现都在 `ComponentSickness` 内（ComponentSickness.cs:48-120）：
    /// `NauseaEffect()` 会 `PlayMoanSound`、按随机/时间条件呕吐（`PukeParticleSystem` +
    /// `PlayPukeSound`）或提示 "You feel nauseous"，还有 `GreenoutFactor` 绿屏。
    ///
    /// 这个组件原本**没有被替换**，于是客户端会自己跑一遍：本地用 `m_sicknessDuration` 递减、
    /// 自己 roll 恶心/呕吐时机、还在本地 `Injure`。而病情状态是主机的，主机那份
    /// `SicknessDuration` 一般为 0 → 刚出现的呕吐表现立刻被抹掉（实测"吃东西呕吐那些也没有看见"），
    /// 属于典型的"本地模拟 + 主机权威"双重来源。
    ///
    /// 现在的分工：
    ///   · **主机**照常跑原生逻辑，把每次恶心/呕吐在**边沿**上编成单调递增的 `NauseaSequence`
    ///     （并标明这一次是否真的吐了），加上绿屏剩余秒数一起发给客户端。
    ///   · **客户端**完全不跑疾病模拟：收到新序号就放呻吟/呕吐音效、复现呕吐粒子与低头姿势、
    ///     或提示 "You feel nauseous"，并驱动绿屏。序号幂等，漏收不补、重复收不重播。
    /// </summary>
    public class SuComponentSickness : ComponentSickness, IUpdateable
    {
        private ComponentPlayer m_componentPlayer;
        private SubsystemTerrain m_subsystemTerrain;
        private SubsystemParticles m_subsystemParticles;
        private int m_nauseaSequence;
        private int m_lastAuthoritativeNauseaSequence;
        private float m_clientGreenoutSeconds;
        private float m_clientGreenoutFactor;

        /// <summary>
        /// 客户端复现用的呕吐粒子系统。存在**引擎自己的** `m_pukeParticleSystem` 字段里，
        /// 这样 `ComponentSickness.IsPuking`（被 `ComponentLevel` 读）在客户端也正确。
        /// </summary>
        private PukeParticleSystem ClientPuke
        {
            // 注意：**不要**用泛型 `GetParentField<PukeParticleSystem>` —— 它内部只做 `value is T`，
            // 字段为 null（玩家当前没在呕吐，这是常态）时会抛 "Member ... is not of type ..."。
            // 用返回 object 的非泛型重载 + `as` 才是 null 安全的。
            get => ScMultiplayer.ModManager.ModParentField.GetParentField(
                this, "m_pukeParticleSystem", typeof(ComponentSickness)) as PukeParticleSystem;
            set => ScMultiplayer.ModManager.ModParentField.ModifyParentField(
                this, "m_pukeParticleSystem", value, typeof(ComponentSickness));
        }

        public int NauseaSequence => m_nauseaSequence;

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>(true);
            m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(false);
            m_subsystemParticles = Project.FindSubsystem<SubsystemParticles>(false);
        }

        private static double GetParentTime(object owner, string name)
        {
            object value = ScMultiplayer.ModManager.ModParentField.GetParentField(
                owner, name, typeof(ComponentSickness));
            return value is double time ? time : -1000.0;
        }

        // Source: Survivalcraft/Game/ComponentSickness.cs:ComponentSickness.Update
        void IUpdateable.Update(float dt)
        {
            if (ScMultiplayer.client?.IsConnected != true || ScMultiplayer.IsHost)
            {
                double lastNausea = GetParentTime(this, "m_lastNauseaTime");
                double lastPuke = GetParentTime(this, "m_lastPukeTime");
                base.Update(dt);
                double nausea = GetParentTime(this, "m_lastNauseaTime");
                if (nausea > lastNausea + 0.0001)
                {
                    m_nauseaSequence = m_nauseaSequence == int.MaxValue ? 1 : m_nauseaSequence + 1;
                    NauseaPuked = GetParentTime(this, "m_lastPukeTime") > lastPuke + 0.0001;
                }
                GreenoutSeconds = ScMultiplayer.ModManager.ModParentField.GetParentField<float>(
                    this, "m_greenoutDuration", typeof(ComponentSickness));
                return;
            }

            // 客户端：不跑任何疾病模拟，只播主机发来的边沿。
            UpdateClientPresentation(dt);
        }

        /// <summary>主机上一次恶心/呕吐是否伴随真正的呕吐（用于客户端选择粒子还是文字提示）。</summary>
        public bool NauseaPuked { get; private set; }

        /// <summary>主机侧绿屏剩余秒数。</summary>
        public float GreenoutSeconds { get; private set; }

        // Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerHealthWorldControlHandlers.cs:
        // ScMultiplayer.ApplyAuthoritativePlayerEffects
        internal void ApplyAuthoritativeNausea(int sequence, bool puked, float greenoutSeconds)
        {
            m_clientGreenoutSeconds = MathUtils.Max(m_clientGreenoutSeconds, greenoutSeconds);
            if (sequence == m_lastAuthoritativeNauseaSequence) return;
            m_lastAuthoritativeNauseaSequence = sequence;
            if (sequence <= 0) return;
            m_componentPlayer?.ComponentCreatureSounds?.PlayMoanSound();
            if (puked)
            {
                StartClientPuke();
                m_clientGreenoutSeconds = MathUtils.Max(m_clientGreenoutSeconds, 0.8f);
            }
            else if (ClientPuke == null)
            {
                m_componentPlayer?.ComponentGui?.DisplaySmallMessage(
                    "You feel nauseous", Color.White, blinking: true, playNotificationSound: true);
            }
        }

        // Source: Survivalcraft/Game/ComponentSickness.cs:ComponentSickness.NauseaEffect
        private void StartClientPuke()
        {
            if (ClientPuke != null || m_subsystemTerrain == null ||
                m_subsystemParticles == null)
                return;
            ClientPuke = new PukeParticleSystem(m_subsystemTerrain);
            m_subsystemParticles.AddParticleSystem(ClientPuke);
            m_componentPlayer?.ComponentCreatureSounds?.PlayPukeSound();
        }

        // Source: Survivalcraft/Game/ComponentSickness.cs:ComponentSickness.Update
        // 只复现原生代码里的表现：呕吐粒子的位置/朝向、低头姿势与绿屏升降。
        private void UpdateClientPresentation(float dt)
        {
            if (ClientPuke != null && m_componentPlayer != null)
            {
                ComponentLocomotion locomotion = m_componentPlayer.ComponentLocomotion;
                if (locomotion != null)
                {
                    float look = MathUtils.DegToRad(MathUtils.Lerp(-35f, -60f,
                        SimplexNoise.Noise(2f * (float)MathUtils.Remainder(
                            GameManager.Project.FindSubsystem<SubsystemTime>(false)?.GameTime ?? 0.0,
                            10000.0))));
                    locomotion.LookOrder = new Vector2(locomotion.LookOrder.X,
                        MathUtils.Clamp(look - locomotion.LookAngles.Y, -2f, 2f));
                }
                ComponentCreatureModel model = m_componentPlayer.ComponentCreatureModel;
                if (model != null)
                {
                    Vector3 up = model.EyeRotation.GetUpVector();
                    Vector3 forward = model.EyeRotation.GetForwardVector();
                    ClientPuke.Position =
                        model.EyePosition - 0.08f * up + 0.3f * forward;
                    ClientPuke.Direction =
                        Vector3.Normalize(forward + 0.5f * up);
                }
                if (ClientPuke.IsStopped)
                    ClientPuke = null;
            }

            if (m_clientGreenoutSeconds > 0f)
            {
                m_clientGreenoutSeconds = MathUtils.Max(m_clientGreenoutSeconds - dt, 0f);
                m_clientGreenoutFactor = MathUtils.Min(m_clientGreenoutFactor + 0.5f * dt, 0.95f);
            }
            else if (m_clientGreenoutFactor > 0f)
            {
                m_clientGreenoutFactor = MathUtils.Max(m_clientGreenoutFactor - 0.5f * dt, 0f);
            }
            if (m_clientGreenoutFactor > 0f && m_componentPlayer?.ComponentScreenOverlays != null)
            {
                m_componentPlayer.ComponentScreenOverlays.GreenoutFactor = MathUtils.Max(
                    m_clientGreenoutFactor, m_componentPlayer.ComponentScreenOverlays.GreenoutFactor);
            }
        }
    }
}
