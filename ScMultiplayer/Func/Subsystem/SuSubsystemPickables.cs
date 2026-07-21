using Game;
using System.Collections.Generic;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public class SuSubsystemPickables : SubsystemPickables, IUpdateable
    {
        private List<ComponentPlayer> m_componentPlayers;
        private readonly List<ComponentPlayer> m_savedComponentPlayers =
            new List<ComponentPlayer>();

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.m_componentPlayers
            SubsystemPlayers subsystemPlayers = Project.FindSubsystem<SubsystemPlayers>(true);
            m_componentPlayers = ScMultiplayer.ModManager.ModParentField
                .GetParentField<List<ComponentPlayer>>(
                    subsystemPlayers, "m_componentPlayers", typeof(SubsystemPlayers));
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        void IUpdateable.Update(float dt)
        {
            if (ScMultiplayer.IsHost || ScMultiplayer.client?.IsConnected != true ||
                m_componentPlayers == null || m_componentPlayers.Count == 0)
            {
                base.Update(dt);
                return;
            }

            // Client pickables retain native terrain collision, water physics and draw behavior.
            // Only player acquisition is host-authoritative, so hide pickup candidates solely for
            // the duration of the base update and restore the exact list before other subsystems run.
            m_savedComponentPlayers.Clear();
            m_savedComponentPlayers.AddRange(m_componentPlayers);
            m_componentPlayers.Clear();
            try
            {
                base.Update(dt);
            }
            finally
            {
                m_componentPlayers.AddRange(m_savedComponentPlayers);
                m_savedComponentPlayers.Clear();
            }
        }
    }
}
