using Game;
using System;

namespace ScMultiplayer
{
    public class SuSubsystemSpawn : SubsystemSpawn
    {
        public override void Update(float dt)
        {
            // Source: Survivalcraft/Game/SubsystemSpawn.cs:SubsystemSpawn.Update
            // Keep local views on the native path. Remote chunks are activated gradually after the
            // base update so joining a distant player cannot spawn an entire radius in one frame.
            ScMultiplayer instance = ScMultiplayer.currentInstance;
            // Source: Survivalcraft/Game/SubsystemSpawn.cs:SubsystemSpawn.SpawnChunks
            // A connected client displays host replicas only. Running the native chunk spawner here
            // creates a second, locally-random animal population until reconciliation removes it.
            if (instance != null && ScMultiplayer.client?.IsConnected == true &&
                !ScMultiplayer.IsHost)
                return;
            instance?.SanitizeRunawayCreatureState(base.Project);
            IDisposable despawnScope = instance?
                .BeginRemoteDespawnProtectionScope(base.Project);
            try
            {
                base.Update(dt);
                instance?.MaintainRemoteCreatureSpawning(base.Project, this);
            }
            finally
            {
                despawnScope?.Dispose();
            }
        }
    }
}
