using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using TemplatesDatabase;

namespace GodMode
{
    // Source: ComponentHealth.cs - replaces Game.ComponentHealth
    // Makes player invulnerable: IsInvulnerable=true, Health=1f, Air=1f every frame
    public class GodModeComponentHealth : Game.ComponentHealth
    {
        private SubsystemGameInfo m_subsystemGameInfo;

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_subsystemGameInfo = base.Project.FindSubsystem<SubsystemGameInfo>(throwOnError: true);
            // Force invulnerable on load
            Program.ModManager.ModParentField.ModifyParentField(
                this, "IsInvulnerable", true, typeof(Game.ComponentHealth));
        }

        public /*mod*/override/*...mod*/ void Update(float dt)
        {
            // Force invulnerable every frame
            Program.ModManager.ModParentField.ModifyParentField(
                this, "IsInvulnerable", true, typeof(Game.ComponentHealth));
            // Force full health
            Program.ModManager.ModParentField.ModifyParentField(
                this, "Health", 1f, typeof(Game.ComponentHealth));
            // Force full air
            Program.ModManager.ModParentField.ModifyParentField(
                this, "Air", 1f, typeof(Game.ComponentHealth));

            base.Update(dt);
        }
    }
}
