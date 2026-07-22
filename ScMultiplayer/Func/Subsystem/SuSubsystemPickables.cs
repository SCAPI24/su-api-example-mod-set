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
        private readonly List<Pickable> m_waterSplashCandidates = new List<Pickable>();
        private SubsystemFluidBlockBehavior m_subsystemFluidBlockBehavior;

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            base.Load(valuesDictionary);
            // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.m_componentPlayers
            SubsystemPlayers subsystemPlayers = Project.FindSubsystem<SubsystemPlayers>(true);
            m_componentPlayers = ScMultiplayer.ModManager.ModParentField
                .GetParentField<List<ComponentPlayer>>(
                    subsystemPlayers, "m_componentPlayers", typeof(SubsystemPlayers));
            m_subsystemFluidBlockBehavior = Project.FindSubsystem<SubsystemFluidBlockBehavior>(true);
        }

        // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
        void IUpdateable.Update(float dt)
        {
            if (ScMultiplayer.IsHost)
            {
                bool publishSplash = ScMultiplayer.client?.IsConnected == true;
                m_waterSplashCandidates.Clear();
                if (publishSplash)
                {
                    foreach (Pickable pickable in Pickables)
                    {
                        if (pickable != null && !pickable.ToRemove && !pickable.SplashGenerated)
                            m_waterSplashCandidates.Add(pickable);
                    }
                }
                base.Update(dt);
                if (publishSplash)
                {
                    foreach (Pickable pickable in m_waterSplashCandidates)
                    {
                        if (pickable == null || pickable.ToRemove || !pickable.SplashGenerated)
                            continue;
                        m_subsystemFluidBlockBehavior.CalculateFlowSpeed(
                            Terrain.ToCell(pickable.Position.X),
                            Terrain.ToCell(pickable.Position.Y + 0.1f),
                            Terrain.ToCell(pickable.Position.Z),
                            out FluidBlock surfaceBlock, out _);
                        if (surfaceBlock is WaterBlock)
                            ScMultiplayer.currentInstance?.PublishPickableWaterSplash(pickable);
                    }
                }
                m_waterSplashCandidates.Clear();
                return;
            }

            if (ScMultiplayer.client?.IsConnected != true ||
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
                // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
                // Remote pickables are positioned by the host. Re-running the local water-entry
                // effect at a shoreline correction can generate an endless splash on clients.
                foreach (Pickable pickable in Pickables)
                    if (pickable != null) pickable.SplashGenerated = true;
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
