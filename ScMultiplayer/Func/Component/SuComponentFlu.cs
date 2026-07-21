using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public class SuComponentFlu : ComponentFlu, IUpdateable
    {
        private readonly Engine.Random m_random = new Engine.Random();
        private ComponentPlayer m_componentPlayer;
        private SubsystemTime m_subsystemTime;
        private float m_clientCoughDuration;
        private int m_coughSequence;
        private int m_lastAuthoritativeCoughSequence;

        public int CoughSequence => m_coughSequence;

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>(true);
            m_subsystemTime = Project.FindSubsystem<SubsystemTime>(true);
        }

        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.Update
        void IUpdateable.Update(float dt)
        {
            if (ScMultiplayer.client?.IsConnected != true || ScMultiplayer.IsHost)
            {
                bool wasCoughing = IsCoughing;
                base.Update(dt);
                if (!wasCoughing && IsCoughing)
                {
                    m_coughSequence = m_coughSequence == int.MaxValue
                        ? 1
                        : m_coughSequence + 1;
                    ScMultiplayer.currentInstance?.PublishAuthoritativeCough(m_componentPlayer);
                }
                return;
            }

            UpdateClientCoughPresentation(dt);
        }

        internal void ApplyAuthoritativeCough(int sequence, bool isCoughing)
        {
            if (sequence != m_lastAuthoritativeCoughSequence)
            {
                m_lastAuthoritativeCoughSequence = sequence;
                if (sequence > 0 && isCoughing)
                {
                    m_clientCoughDuration = 4f;
                    base.Cough();
                }
            }
            else if (!isCoughing && m_clientCoughDuration > 0f)
            {
                StopClientCoughPresentation();
            }
        }

        // Source: Survivalcraft/Game/ComponentFlu.cs:ComponentFlu.Update
        private void UpdateClientCoughPresentation(float dt)
        {
            if (m_clientCoughDuration <= 0f) return;
            m_clientCoughDuration = MathUtils.Max(m_clientCoughDuration - dt, 0f);
            if (m_componentPlayer.ComponentHealth.Health > 0f &&
                !m_componentPlayer.ComponentSleep.IsSleeping)
            {
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
            if (m_clientCoughDuration <= 0f)
                StopClientCoughPresentation();
        }

        private void StopClientCoughPresentation()
        {
            m_clientCoughDuration = 0f;
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(
                this, "m_coughDuration", 0f, typeof(ComponentFlu));
        }
    }
}
