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
        // The host owns the sleep session boundary. A client must keep the native sleep
        // presentation, but it must not locally auto-wake or let SubsystemTime infer the
        // 20x world loop from a replicated player reaching SleepFactor == 1.
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

            // Source: Survivalcraft/Game/SubsystemTime.cs:SubsystemTime.NextFrame
            // A connected client never owns the accelerated sleep timeline. Start guarding on
            // the first sleeping frame, before the host acceleration edge can arrive, so the
            // local SleepFactor cannot make SubsystemTime enter UpdatesPerFrame=20.
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

            object sleepStartValue = ScMultiplayer.ModManager.ModParentField.GetParentField(
                this, "m_sleepStartTime", typeof(ComponentSleep));
            if (!(sleepStartValue is double sleepStartTime))
            {
                base.Update(dt);
                return;
            }

            // ComponentSleep.Update uses this value only for the automatic wake boundary.
            // Temporarily move that boundary out of range, then restore the host value so
            // save data, health snapshots and the next authoritative message remain intact.
            ScMultiplayer.ModManager.ModParentField.ModifyParentField(
                this, "m_sleepStartTime", double.MaxValue, typeof(ComponentSleep));
            try
            {
                base.Update(dt);
            }
            finally
            {
                ScMultiplayer.ModManager.ModParentField.ModifyParentField(
                    this, "m_sleepStartTime", (double?)sleepStartTime,
                    typeof(ComponentSleep));
                float sleepFactor = ScMultiplayer.ModManager.ModParentField.GetParentField<float>(
                    this, "m_sleepFactor", typeof(ComponentSleep));
                if (sleepFactor >= 1f)
                    ScMultiplayer.ModManager.ModParentField.ModifyParentField(
                        this, "m_sleepFactor", 0.999f, typeof(ComponentSleep));
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
