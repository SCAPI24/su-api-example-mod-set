using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using TemplatesDatabase;

namespace GodMode
{
    // Source: ComponentFlu.cs - replaces Game.ComponentFlu
    // Prevents flu from starting
    public class GodModeComponentFlu : Game.ComponentFlu
    {
        public /*mod*/override/*...mod*/ void Update(float dt)
        {
            // Cancel any flu onset and duration
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_fluOnset", 0f, typeof(Game.ComponentFlu));
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_fluDuration", 0f, typeof(Game.ComponentFlu));
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_coughDuration", 0f, typeof(Game.ComponentFlu));
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_sneezeDuration", 0f, typeof(Game.ComponentFlu));
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_blackoutDuration", 0f, typeof(Game.ComponentFlu));
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_blackoutFactor", 0f, typeof(Game.ComponentFlu));

            base.Update(dt);
        }
    }
}
