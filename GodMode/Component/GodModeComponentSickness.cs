using Engine;
using Game;
using GameEntitySystem;
using SuMod;
using TemplatesDatabase;

namespace GodMode
{
    // Source: ComponentSickness.cs - replaces Game.ComponentSickness
    // Prevents sickness from occurring
    public class GodModeComponentSickness : Game.ComponentSickness
    {
        public /*mod*/override/*...mod*/ void Update(float dt)
        {
            // Cancel any sickness duration
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_sicknessDuration", 0f, typeof(Game.ComponentSickness));
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_greenoutDuration", 0f, typeof(Game.ComponentSickness));
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_greenoutFactor", 0f, typeof(Game.ComponentSickness));

            base.Update(dt);
        }
    }
}
