using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public sealed class SuComponentSleep : ComponentSleep, IUpdateable
    {
        private ComponentPlayer m_componentPlayer;

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
                ScMultiplayer.client?.IsConnected != true ||
                !owner.ShouldHoldClientSleepTimeline(m_componentPlayer))
            {
                base.Update(dt);
                return;
            }

            bool allowManualWake = ScMultiplayer.ModManager.ModParentField.GetParentField<bool>(
                this, "m_allowManualWakeUp", typeof(ComponentSleep));
            bool manualWake = allowManualWake && m_componentPlayer?.GameWidget?.Input != null &&
                m_componentPlayer.GameWidget.Input.Any &&
                !DialogsManager.HasDialogs(m_componentPlayer.GameWidget);
            if (manualWake)
            {
                base.Update(dt);
                return;
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
    }
}
