using Engine;
using Game;

namespace ScMultiplayer
{
    public sealed class SuSubsystemHammerBlockBehavior : SubsystemHammerBlockBehavior
    {
        // Source: Survivalcraft/Game/SubsystemHammerBlockBehavior.cs:
        // SubsystemHammerBlockBehavior.OnUse
        public override bool OnUse(Ray3 ray, ComponentMiner componentMiner)
        {
            ScMultiplayer multiplayer = ScMultiplayer.currentInstance;
            if (multiplayer?.IsNetworkSessionActive(GameManager.Project) == true &&
                !multiplayer.IsNetworkHost(GameManager.Project))
            {
                TerrainRaycastResult? result = componentMiner?
                    .Raycast<TerrainRaycastResult>(ray, RaycastMode.Digging);
                if (result.HasValue)
                    multiplayer.CapturePendingFurnitureBuild(
                        result.Value.CellFace, componentMiner);
            }
            return base.OnUse(ray, componentMiner);
        }
    }
}
