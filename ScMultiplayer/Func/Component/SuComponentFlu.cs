using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace ScMultiplayer
{
    /// <summary>
    /// `ComponentFlu` 的联机替换：**主机算、客户端播**（本地不做任何预测/模拟）。
    ///
    /// 引擎里流感的每一样表现都在 `ComponentFlu.Update` 内（ComponentFlu.cs:75-152）：
    /// 提示（119/191/195）、打喷嚏 `Sneeze()`（60-65，含 `PlaySneezeSound`）、咳嗽 `Cough()`
    /// （67-73，含 `PlayCoughSound`）、黑屏 `FluEffect()`（172-207）。客户端原本整个跳过它，
    /// 只剩主机 `FluEffect` 造成的扣血经同步到达 —— 表现就是"该打喷嚏时只有扣血的声音和效果"。
    ///
    /// 现在的分工：
    ///   · **主机**照常跑原生更新，把喷嚏/咳嗽两个表现事件在**边沿**上编成单调递增的序号
    ///     （`m_sneezeDuration` / `m_coughDuration` 由 0 变正），随状态快照发出去；
    ///     黑屏剩余秒数（`m_blackoutDuration`）也一并带上。
    ///   · **客户端**完全不跑流感模拟：收到新序号就放音效、按引擎同一套姿势/黑屏规则播出来。
    ///     序号是幂等的 —— 漏收一次不会补播、也不会重复播。
    /// </summary>
    public class SuComponentFlu : ComponentFlu, IUpdateable
    {
        private readonly Engine.Random m_random = new Engine.Random();
        private ComponentPlayer m_componentPlayer;
        private SubsystemTime m_subsystemTime;
        private int m_sneezeSequence;
        private int m_coughSequence;
        private int m_lastAuthoritativeSneezeSequence;
        private int m_lastAuthoritativeCoughSequence;
        private float m_clientSneezeDuration;
        private float m_clientCoughDuration;
        private float m_clientBlackoutSeconds;
        private float m_clientBlackoutFactor;

        public int SneezeSequence => m_sneezeSequence;

        public int CoughSequence => m_coughSequence;

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>(true);
            m_subsystemTime = Project.FindSubsystem<SubsystemTime>(true);
        }

        private float GetParentFloat(string name) =>
            ScMultiplayer.ModManager.ModParentField.GetParentField<float>(
                this, name, typeof(ComponentFlu));

        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.Update
        void IUpdateable.Update(float dt)
        {
            if (ScMultiplayer.client?.IsConnected != true || ScMultiplayer.IsHost)
            {
                bool wasSneezing = GetParentFloat("m_sneezeDuration") > 0f;
                bool wasCoughing = GetParentFloat("m_coughDuration") > 0f;
                base.Update(dt);
                if (!wasSneezing && GetParentFloat("m_sneezeDuration") > 0f)
                    m_sneezeSequence = m_sneezeSequence == int.MaxValue ? 1 : m_sneezeSequence + 1;
                if (!wasCoughing && GetParentFloat("m_coughDuration") > 0f)
                    m_coughSequence = m_coughSequence == int.MaxValue ? 1 : m_coughSequence + 1;
                return;
            }

            // 客户端：不跑任何流感模拟，只播主机发来的边沿。
            UpdateClientPresentation(dt);
        }

        // Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerHealthWorldControlHandlers.cs:
        // ScMultiplayer.ApplyAuthoritativePlayerEffects
        internal void ApplyAuthoritativeCues(int sneezeSequence, bool isSneezing,
            int coughSequence, bool isCoughing, float blackoutSeconds)
        {
            if (sneezeSequence != m_lastAuthoritativeSneezeSequence)
            {
                m_lastAuthoritativeSneezeSequence = sneezeSequence;
                if (sneezeSequence > 0 && isSneezing)
                {
                    m_clientSneezeDuration = 1f;
                    m_componentPlayer?.ComponentCreatureSounds?.PlaySneezeSound();
                }
            }
            if (coughSequence != m_lastAuthoritativeCoughSequence)
            {
                m_lastAuthoritativeCoughSequence = coughSequence;
                if (coughSequence > 0 && isCoughing)
                {
                    m_clientCoughDuration = 4f;
                    m_componentPlayer?.ComponentCreatureSounds?.PlayCoughSound();
                }
            }
            m_clientBlackoutSeconds = MathUtils.Max(m_clientBlackoutSeconds, blackoutSeconds);
        }

        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.Update
        // 只复现原生代码里的表现部分（喷嚏/咳嗽的低头与抽动，以及黑屏的升降），
        // 不碰病程、不产生伤害、不做发声以外的副作用。
        private void UpdateClientPresentation(float dt)
        {
            if (m_clientCoughDuration > 0f || m_clientSneezeDuration > 0f)
            {
                if (m_componentPlayer?.ComponentHealth?.Health > 0f &&
                    m_componentPlayer.ComponentSleep?.IsSleeping != true)
                {
                    m_clientCoughDuration = MathUtils.Max(m_clientCoughDuration - dt, 0f);
                    m_clientSneezeDuration = MathUtils.Max(m_clientSneezeDuration - dt, 0f);
                    float pitch = MathUtils.DegToRad(MathUtils.Lerp(-35f, -65f,
                        SimplexNoise.Noise(4f * (float)MathUtils.Remainder(
                            m_subsystemTime.GameTime, 10000.0))));
                    ComponentLocomotion locomotion = m_componentPlayer.ComponentLocomotion;
                    locomotion.LookOrder = new Vector2(locomotion.LookOrder.X,
                        MathUtils.Clamp(pitch - locomotion.LookAngles.Y, -3f, 3f));
                    if (m_random.Bool(2f * dt))
                    {
                        m_componentPlayer.ComponentBody.ApplyImpulse(-1.2f *
                            m_componentPlayer.ComponentCreatureModel.EyeRotation.GetForwardVector());
                    }
                }
            }
            if (m_clientBlackoutSeconds > 0f)
            {
                m_clientBlackoutSeconds = MathUtils.Max(m_clientBlackoutSeconds - dt, 0f);
                m_clientBlackoutFactor = MathUtils.Min(m_clientBlackoutFactor + 0.5f * dt, 0.95f);
            }
            else if (m_clientBlackoutFactor > 0f)
            {
                m_clientBlackoutFactor = MathUtils.Max(m_clientBlackoutFactor - 0.5f * dt, 0f);
            }
            if (m_clientBlackoutFactor > 0f && m_componentPlayer?.ComponentScreenOverlays != null)
            {
                m_componentPlayer.ComponentScreenOverlays.BlackoutFactor = MathUtils.Max(
                    m_clientBlackoutFactor, m_componentPlayer.ComponentScreenOverlays.BlackoutFactor);
            }
        }
    }
}
