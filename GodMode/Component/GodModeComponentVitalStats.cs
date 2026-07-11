using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using TemplatesDatabase;

namespace GodMode
{
    // Source: ComponentVitalStats.cs - replaces Game.ComponentVitalStats
    // Keeps Food/Stamina/Sleep/Temperature at ideal values every frame
    public class GodModeComponentVitalStats : Game.ComponentVitalStats
    {
        public /*mod*/override/*...mod*/ void Update(float dt)
        {
            // Force ideal vital stats every frame
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_food", 0.9f, typeof(Game.ComponentVitalStats));
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_stamina", 1f, typeof(Game.ComponentVitalStats));
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_sleep", 0.9f, typeof(Game.ComponentVitalStats));
            // Temperature 12f = comfortable (range 0-24, ideal ~12)
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_temperature", 12f, typeof(Game.ComponentVitalStats));
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_wetness", 0f, typeof(Game.ComponentVitalStats));
            // Target temperature also 12f to prevent temperature drift
            Program.ModManager.ModParentField.ModifyParentField(
                this, "m_targetTemperature", 12f, typeof(Game.ComponentVitalStats));

            base.Update(dt);
        }
    }
}
