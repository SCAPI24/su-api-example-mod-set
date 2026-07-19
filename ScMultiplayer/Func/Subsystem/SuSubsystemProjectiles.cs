using Engine;
using Game;
using System.Collections.Generic;

namespace ScMultiplayer
{
    public class SuSubsystemProjectiles : SubsystemProjectiles
    {
        private sealed class ProjectileHitCandidate
        {
            public Projectile Projectile;
            public int OwnerClientId;
            public ComponentHealth Health;
            public float HealthBefore;
            public Vector3 HitPoint;
            public Vector3 HitDirection;
        }

        // Source: Survivalcraft/Game/SubsystemProjectiles.cs:SubsystemProjectiles.Update
        public override void Update(float dt)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer == null || ScMultiplayer.client?.IsConnected != true)
            {
                base.Update(dt);
                return;
            }

            if (ScMultiplayer.IsHost)
            {
                List<ProjectileHitCandidate> candidates = CaptureBodyHits(multiplayer, dt);
                base.Update(dt);
                foreach (ProjectileHitCandidate candidate in candidates)
                {
                    float damage = (candidate.HealthBefore - candidate.Health.Health) *
                        candidate.Health.AttackResilience;
                    if (damage > 0f)
                    {
                        multiplayer.PublishAuthoritativeProjectileHit(
                            candidate.Projectile, candidate.OwnerClientId,
                            candidate.HitPoint, candidate.HitDirection, damage);
                    }
                }
                return;
            }

            // Source: Survivalcraft/Game/ComponentMiner.cs:ComponentMiner.AttackBody
            // Client projectiles are visual predictions. Temporarily remove their owner so the
            // prediction cannot create a second damage number before the host confirms the hit.
            var owners = new List<KeyValuePair<Projectile, ComponentCreature>>();
            foreach (Projectile projectile in Projectiles)
            {
                if (!multiplayer.IsLocalPredictedProjectile(projectile)) continue;
                owners.Add(new KeyValuePair<Projectile, ComponentCreature>(projectile, projectile.Owner));
                projectile.Owner = null;
            }
            try
            {
                base.Update(dt);
            }
            finally
            {
                foreach (KeyValuePair<Projectile, ComponentCreature> item in owners)
                {
                    if (item.Key != null && item.Key.Owner == null)
                        item.Key.Owner = item.Value;
                }
            }
        }

        private List<ProjectileHitCandidate> CaptureBodyHits(
            ScMultiplayer multiplayer, float dt)
        {
            var result = new List<ProjectileHitCandidate>();
            SubsystemBodies bodies = Project.FindSubsystem<SubsystemBodies>(false);
            SubsystemTerrain terrain = Project.FindSubsystem<SubsystemTerrain>(false);
            if (bodies == null || terrain == null) return result;

            foreach (Projectile projectile in Projectiles)
            {
                if (projectile == null || projectile.ToRemove ||
                    projectile.Velocity.LengthSquared() <= 100f)
                    continue;
                int ownerClientId = multiplayer.GetProjectileOwnerClientIdForHit(projectile);
                if (ownerClientId <= 0) continue;

                Block block = BlocksManager.Blocks[Terrain.ExtractContents(projectile.Value)];
                Vector3 direction = Vector3.Normalize(projectile.Velocity);
                Vector3 tipOffset = block.ProjectileTipOffset * direction;
                Vector3 start = projectile.Position + tipOffset;
                Vector3 end = projectile.Position + projectile.Velocity * dt + tipOffset;
                BodyRaycastResult? bodyHit = bodies.Raycast(start, end, 0.2f,
                    (ComponentBody body, float distance) => true);
                if (!bodyHit.HasValue) continue;
                TerrainRaycastResult? terrainHit = terrain.Raycast(start, end,
                    useInteractionBoxes: false, skipAirBlocks: true,
                    (int value, float distance) =>
                        BlocksManager.Blocks[Terrain.ExtractContents(value)].IsCollidable);
                if (terrainHit.HasValue && bodyHit.Value.Distance >= terrainHit.Value.Distance)
                    continue;

                ComponentHealth health = bodyHit.Value.ComponentBody.Entity
                    .FindComponent<ComponentHealth>();
                if (health == null) continue;
                result.Add(new ProjectileHitCandidate
                {
                    Projectile = projectile,
                    OwnerClientId = ownerClientId,
                    Health = health,
                    HealthBefore = health.Health,
                    HitPoint = bodyHit.Value.HitPoint(),
                    HitDirection = direction
                });
            }
            return result;
        }
    }
}
