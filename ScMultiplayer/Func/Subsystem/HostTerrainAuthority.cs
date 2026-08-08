using Engine;
using Game;
using System;

namespace ScMultiplayer
{
    internal static class HostTerrainAuthority
    {
        internal static bool IsAuthoritative =>
            ScMultiplayer.client?.IsConnected != true || ScMultiplayer.IsHost;

        internal static bool IsReadyForAuthoritativeMutation(
            SubsystemTerrain subsystemTerrain, int x, int z)
        {
            if (!IsAuthoritative)
                return false;
            if (ScMultiplayer.client?.IsConnected != true)
                return true;
            TerrainChunk chunk = subsystemTerrain?.Terrain?.GetChunkAtCell(x, z);
            return chunk != null && chunk.State >= TerrainChunkState.Valid &&
                chunk.AreBehaviorsNotified;
        }

        // Source: Survivalcraft/Game/SubsystemExplosions.cs:SubsystemExplosions.SimulateExplosion
        // Pressure is the only radius-independent input available before the native propagation
        // starts. Hold a host explosion while every already-allocated chunk in its conservative
        // pressure envelope is still being initialized. Missing chunks remain untouched by the
        // native Terrain fast path and do not need to be allocated just for an explosion.
        internal static bool IsExplosionEnvelopeReady(
            SubsystemTerrain subsystemTerrain, Point3 center, float pressure)
        {
            if (!IsAuthoritative || ScMultiplayer.client?.IsConnected != true)
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
