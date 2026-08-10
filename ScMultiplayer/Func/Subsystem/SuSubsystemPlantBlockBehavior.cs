using Game;

namespace ScMultiplayer
{
    public class SuSubsystemPlantBlockBehavior : SubsystemPlantBlockBehavior
    {
        // Source: Survivalcraft/Game/SubsystemPlantBlockBehavior.cs:
        // SubsystemPlantBlockBehavior.OnPoll/OnBlockGenerated/OnNeighborBlockChanged
        // Plant growth and lifecycle are persistent terrain changes. Only the host may produce
        // them; clients receive the resulting authoritative terrain values.
        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            if (HostTerrainAuthority.IsReadyForAuthoritativeMutation(
                SubsystemTerrain, x, z))
                base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnBlockGenerated(
            int value, int x, int y, int z, bool isLoaded)
        {
            if (HostTerrainAuthority.IsReadyForAuthoritativeMutation(
                SubsystemTerrain, x, z))
                base.OnBlockGenerated(value, x, y, z, isLoaded);
        }

        public override void OnNeighborBlockChanged(
            int x, int y, int z, int neighborX, int neighborY, int neighborZ)
        {
            // Source: Survivalcraft/Game/SubsystemPlantBlockBehavior.cs:
            // SubsystemPlantBlockBehavior.OnNeighborBlockChanged
            // The host must keep the native support check even while a chunk is finishing
            // behavior notification. Clients remain presentation-only through the readiness gate.
            if (HostTerrainAuthority.IsAuthoritative ||
                HostTerrainAuthority.IsReadyForAuthoritativeMutation(
                    SubsystemTerrain, x, z))
                base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }
    }
}
