using Engine;
using Game;
using System.Collections.Generic;

namespace ScMultiplayer
{
    public class SuSubsystemGrassBlockBehavior : SubsystemGrassBlockBehavior
    {
        private static bool IsAuthoritative =>
            ScMultiplayer.client?.IsConnected != true || ScMultiplayer.IsHost;

        public override void OnPoll(int value, int x, int y, int z, int pollPass)
        {
            // Source: Survivalcraft/Game/SubsystemGrassBlockBehavior.cs:SubsystemGrassBlockBehavior.OnPoll
            // Grass spreading uses a local Random. Only the host may enqueue these changes.
            if (IsAuthoritative) base.OnPoll(value, x, y, z, pollPass);
        }

        public override void OnNeighborBlockChanged(
            int x, int y, int z, int neighborX, int neighborY, int neighborZ)
        {
            // Source: Survivalcraft/Game/SubsystemGrassBlockBehavior.cs:SubsystemGrassBlockBehavior.OnNeighborBlockChanged
            // Host terrain batches contain the resulting grass value, including its snow-cover data.
            if (IsAuthoritative)
                base.OnNeighborBlockChanged(x, y, z, neighborX, neighborY, neighborZ);
        }

        public override void OnExplosion(int value, int x, int y, int z, float damage)
        {
            // Source: Survivalcraft/Game/SubsystemGrassBlockBehavior.cs:SubsystemGrassBlockBehavior.OnExplosion
            if (IsAuthoritative) base.OnExplosion(value, x, y, z, damage);
        }

        public override void Update(float dt)
        {
            // Source: Survivalcraft/Game/SubsystemGrassBlockBehavior.cs:SubsystemGrassBlockBehavior.Update
            if (IsAuthoritative)
            {
                base.Update(dt);
                return;
            }

            Dictionary<Point3, int> pending = ScMultiplayer.ModManager.ModParentField
                .GetParentField<Dictionary<Point3, int>>(
                    this, "m_toUpdate", typeof(SubsystemGrassBlockBehavior));
            pending?.Clear();
        }
    }
}
