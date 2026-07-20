using Game;
using System;

namespace ScMultiplayer
{
    public class SuSubsystemCreatureSpawn : SubsystemCreatureSpawn, IUpdateable
    {
        void IUpdateable.Update(float dt)
        {
            // Source: Survivalcraft/Game/SubsystemCreatureSpawn.cs:SubsystemCreatureSpawn.Update
            // Keep random creature selection on the original split-screen code path while exposing
            // detached network views only for the duration of the native update.
            ScMultiplayer instance = ScMultiplayer.currentInstance;
            // Source: Survivalcraft/Game/SubsystemCreatureSpawn.cs:SubsystemCreatureSpawn.Update
            // Creature selection is host-authoritative in multiplayer. Client-side random spawning
            // uses a different RNG timeline and cannot converge to the host population.
            if (instance != null && ScMultiplayer.client?.IsConnected == true &&
                !ScMultiplayer.IsHost)
                return;
            IDisposable scope = instance?
                .BeginRemoteSimulationViewScope(base.Project);
            try
            {
                base.Update(dt);
            }
            finally
            {
                scope?.Dispose();
            }
        }
    }
}
