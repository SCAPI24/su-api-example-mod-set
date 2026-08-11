using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public sealed class SuComponentSleep : ComponentSleep, IUpdateable
    {
        private ComponentPlayer m_componentPlayer;
        private bool m_wasClientSleeping;
        private bool m_clientWakeInputReleased;
        private bool m_clientWakeRequested;

        protected override void Load(ValuesDictionary valuesDictionary,
            IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>(true);
        }

        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Update
        // The host owns every non-manual wake boundary. A client keeps only the native sleep
        // presentation and sends manual wake requests; it must not execute the native wake
        // predicates against locally simulated health, wetness, or time.
        void IUpdateable.Update(float dt)
        {
            ScMultiplayer owner = ScMultiplayer.currentInstance;
            if (owner == null || ScMultiplayer.IsHost ||
                ScMultiplayer.client?.IsConnected != true)
            {
                ResetClientSleepInputState();
                base.Update(dt);
                return;
            }

            if (!IsSleeping)
            {
                ResetClientSleepInputState();
                base.Update(dt);
                return;
            }

            if (!m_wasClientSleeping)
            {
                m_wasClientSleeping = true;
                m_clientWakeInputReleased = false;
                m_clientWakeRequested = false;
            }

            bool allowManualWake = ScMultiplayer.ModManager.ModParentField.GetParentField<bool>(
                this, "m_allowManualWakeUp", typeof(ComponentSleep));
            WidgetInput input = m_componentPlayer?.GameWidget?.Input;
            bool hasWakeInput = input != null && input.Any &&
                !DialogsManager.HasDialogs(m_componentPlayer.GameWidget);
            if (!hasWakeInput)
                m_clientWakeInputReleased = true;
            // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Update
            // The press that started sleeping must be released before another press can request
            // waking. Keep the local presentation asleep until the host confirms the request.
            if (allowManualWake && m_clientWakeInputReleased && hasWakeInput &&
                !m_clientWakeRequested)
            {
                m_clientWakeRequested = owner.RequestClientWakeUp(m_componentPlayer);
                if (m_clientWakeRequested)
                    input.Clear();
            }

            // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Update
            // Reproduce only the presentation part of the native sleeping branch. Calling
            // base.Update here would run local HealthChange, wetness, attack and time predicates
            // and could wake the client before the host's authoritative edge arrives.
            UpdateClientSleepPresentation(dt, allowManualWake);
        }

        // Source: Survivalcraft/Game/ComponentSleep.cs:ComponentSleep.Update
        private void UpdateClientSleepPresentation(float dt, bool allowManualWake)
        {
            float sleepFactor = ScMultiplayer.ModManager.ModParentField.GetParentField<float>(
                this, "m_sleepFactor", typeof(ComponentSleep));
            // Keep the local factor below the native all-players-sleep threshold. The host owns
            // the accelerated timeline; a connected client must never enter UpdatesPerFrame=20.
            sleepFactor = MathUtils.Min(sleepFactor + 0.33f * Time.FrameDuration, 0.999f);
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(
                this, "m_sleepFactor", sleepFactor, typeof(ComponentSleep));

            ComponentScreenOverlays overlays = m_componentPlayer?.ComponentScreenOverlays;
            if (overlays == null) return;
            overlays.BlackoutFactor = MathUtils.Max(overlays.BlackoutFactor, sleepFactor);
            if (sleepFactor > 0.01f)
            {
                overlays.FloatingMessage = "Zzz...";
                overlays.FloatingMessageFactor = MathUtils.Saturate(10f * (sleepFactor - 0.9f));
            }

            float messageFactor = ScMultiplayer.ModManager.ModParentField.GetParentField<float>(
                this, "m_messageFactor", typeof(ComponentSleep));
            if (allowManualWake)
            {
                messageFactor = MathUtils.Min(messageFactor + 0.5f * Time.FrameDuration, 1f);
                ScMultiplayer.ModManager.ModParentField.ModifyParentField(
                    this, "m_messageFactor", messageFactor, typeof(ComponentSleep));
                overlays.Message = "Tap to wake up early";
                overlays.MessageFactor = messageFactor;
            }
        }

        private void ResetClientSleepInputState()
        {
            m_wasClientSleeping = false;
            m_clientWakeInputReleased = false;
            m_clientWakeRequested = false;
        }
    }
}
