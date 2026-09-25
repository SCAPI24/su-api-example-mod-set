using Engine;
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
        // P7b: host-local pickup enforcement (temporarily move denied pickables out of reach).
        private readonly List<Pickable> m_deniedPickables = new List<Pickable>();
        private readonly List<Vector3> m_deniedPickablePositions = new List<Vector3>();
        private double m_nextPickupDeniedNoticeTime;

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
            bool networkActive = ScMultiplayer.currentInstance?.IsNetworkSessionActive(Project) == true;
            bool networkHost = ScMultiplayer.currentInstance?.IsNetworkHost(Project) == true;
            if (!networkActive)
            {
                base.Update(dt);
                return;
            }
            if (networkHost)
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
                // Source: Mod/ScMultiplayer/Modules/Region/ScMultiplayerRegionEnforcement.cs
                // ScMultiplayer.CanRegionModifyCell / NotifyRegionModificationDenied
                // P7b: the HOST player's own pickup bypasses the acquire-request chain, so gate it here:
                // any pickable the host could collect inside someone else's claim is moved out of reach
                // for this tick and restored right after base.Update (nothing is ever collected).
                m_deniedPickables.Clear();
                m_deniedPickablePositions.Clear();
                ScMultiplayer pickupMod = ScMultiplayer.currentInstance;
                ComponentPlayer hostPlayer = null;
                if (pickupMod != null && m_componentPlayers != null)
                {
                    for (int i = 0; i < m_componentPlayers.Count; i++)
                    {
                        ComponentPlayer candidate = m_componentPlayers[i];
                        if (candidate?.PlayerData != null && pickupMod.IsLocalPlayerData(candidate.PlayerData))
                        {
                            hostPlayer = candidate;
                            break;
                        }
                    }
                }
                if (hostPlayer?.ComponentBody != null)
                {
                    Vector3 hostPosition = hostPlayer.ComponentBody.Position;
                    foreach (Pickable pickable in Pickables)
                    {
                        if (pickable == null || pickable.ToRemove)
                            continue;
                        Vector3 delta = pickable.Position - hostPosition;
                        if (delta.LengthSquared() > 64f)
                            continue;
                        var cell = new Point3(Terrain.ToCell(pickable.Position.X),
                            Terrain.ToCell(pickable.Position.Y), Terrain.ToCell(pickable.Position.Z));
                        if (pickupMod.CanRegionModifyCell(0, cell, out RegionClaim claim, out string reason))
                            continue;
                        m_deniedPickables.Add(pickable);
                        m_deniedPickablePositions.Add(pickable.Position);
                        pickable.Position = pickable.Position + new Vector3(0f, 4096f, 0f);
                        if (Time.RealTime >= m_nextPickupDeniedNoticeTime)
                        {
                            m_nextPickupDeniedNoticeTime = Time.RealTime + 1.0;
                            pickupMod.NotifyRegionModificationDenied(0, cell, claim, "pickup", reason);
                        }
                    }
                }                base.Update(dt);
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
                for (int i = 0; i < m_deniedPickables.Count; i++)
                {
                    if (m_deniedPickables[i] != null)
                        m_deniedPickables[i].Position = m_deniedPickablePositions[i];
                }
                m_deniedPickables.Clear();
                m_deniedPickablePositions.Clear();                m_waterSplashCandidates.Clear();
                return;
            }

            if (m_componentPlayers == null || m_componentPlayers.Count == 0)
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
            // Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.Update
            // Native client acquisition is suppressed above, so request the nearby authoritative
            // pickable once without allowing the local inventory to mutate speculatively.
            ScMultiplayer.currentInstance?.RequestNearbyPickableAcquisition();
        }
    }
}
