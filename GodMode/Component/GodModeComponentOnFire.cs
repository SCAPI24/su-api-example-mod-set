using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using TemplatesDatabase;

namespace GodMode
{
    // Source: ComponentOnFire.cs - replaces Game.ComponentOnFire
    // Prevents player from catching fire
    public class GodModeComponentOnFire : Game.ComponentOnFire
    {
        public /*mod*/override/*...mod*/ void Update(float dt)
        {
            // Extinguish any fire immediately
            if (IsOnFire)
            {
                Extinguish();
            }
            // Prevent fire touch count from accumulating
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_fireTouchCount", 0, typeof(Game.ComponentOnFire));
            // Prevent fire duration
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_fireDuration", 0f, typeof(Game.ComponentOnFire));

            base.Update(dt);
        }
    }
}
