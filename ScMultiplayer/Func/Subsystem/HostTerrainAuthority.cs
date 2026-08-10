using Engine;
using Game;
using System;

namespace ScMultiplayer
{
    internal static class HostTerrainAuthority
    {
        private static bool s_networkMutationClosureActive;

        internal static bool IsNetworkMutationClosureActive =>
            s_networkMutationClosureActive;

        internal static bool IsAuthoritative =>
            ScMultiplayer.currentInstance?.IsNetworkSessionActive(GameManager.Project) != true ||
            ScMultiplayer.currentInstance?.IsNetworkHost(GameManager.Project) == true;

        // Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.ProcessModifiedCells
        // A direct network mutation has already proven the target chunk is usable. Allow its
        // native neighbor closure to run while behavior-notification bookkeeping catches up.
        internal static void BeginNetworkMutationClosure()
        {
            s_networkMutationClosureActive = true;
        }

        internal static void EndNetworkMutationClosure()
        {
            s_networkMutationClosureActive = false;
        }

        internal static bool IsReadyForAuthoritativeMutation(
            SubsystemTerrain subsystemTerrain, int x, int z)
        {
            if (!IsAuthoritative)
                return false;
            if (ScMultiplayer.currentInstance?.IsNetworkSessionActive(GameManager.Project) != true)
                return true;
            TerrainChunk chunk = subsystemTerrain?.Terrain?.GetChunkAtCell(x, z);
            if (chunk == null)
                return false;
            if (s_networkMutationClosureActive)
                return chunk.State >= TerrainChunkState.InvalidLight;
            return chunk.State >= TerrainChunkState.Valid && chunk.AreBehaviorsNotified;
        }

        // Source: Survivalcraft/Game/SubsystemExplosions.cs:SubsystemExplosions.SimulateExplosion
        // Pressure is the only radius-independent input available before the native propagation
        // starts. Hold a host explosion while every already-allocated chunk in its conservative
        // pressure envelope is still being initialized. Missing chunks remain untouched by the
        // native Terrain fast path and do not need to be allocated just for an explosion.
        internal static bool IsExplosionEnvelopeReady(
            SubsystemTerrain subsystemTerrain, Point3 center, float pressure)
        {
            if (!IsAuthoritative ||
                ScMultiplayer.currentInstance?.IsNetworkSessionActive(GameManager.Project) != true)
                return true;
            if (subsystemTerrain?.Terrain == null)
                return false;

            int radius = Math.Max(1, (int)MathUtils.Ceiling(MathUtils.Abs(pressure)));
            int minChunkX = (center.X - radius) >> 4;
            int maxChunkX = (center.X + radius) >> 4;
            int minChunkZ = (center.Z - radius) >> 4;
            int maxChunkZ = (center.Z + radius) >> 4;
            for (int chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                for (int chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
                {
                    TerrainChunk chunk = subsystemTerrain.Terrain.GetChunkAtCoords(
                        chunkX, chunkZ);
                    if (chunk != null && (chunk.State < TerrainChunkState.Valid ||
                        !chunk.AreBehaviorsNotified))
                        return false;
                }
            }
            return true;
        }
    }
}
