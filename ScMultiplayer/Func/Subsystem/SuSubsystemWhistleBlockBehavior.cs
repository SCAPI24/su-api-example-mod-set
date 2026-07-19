using Engine;
using Game;

namespace ScMultiplayer
{
    public class SuSubsystemWhistleBlockBehavior : SubsystemWhistleBlockBehavior
    {
        // Source: Survivalcraft/Game/SubsystemWhistleBlockBehavior.cs:SubsystemWhistleBlockBehavior.OnUse
        public override bool OnUse(Ray3 ray, ComponentMiner componentMiner)
        {
            bool handled = base.OnUse(ray, componentMiner);
            if (handled)
            {
                ScMultiplayer.currentInstance?.PublishAuthoritativeWhistle(
                    componentMiner, ray.Position);
            }
            return handled;
        }
    }
}
