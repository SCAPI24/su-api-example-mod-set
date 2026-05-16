using System.Collections.Generic;
using System.Globalization;
using Engine;
using Engine.Audio;
using GameEntitySystem;
using SuMod;
using TemplatesDatabase;

namespace Sumod
{

    public class SumodComponentVitalStats : Game.ComponentVitalStats
    {

        public /*mod*/override/*...mod*/ void Update(float dt)
        {
            Game.Program.ModManager.ModParentField.ModifyParentField(this, "m_targetTemperature", 12f, typeof(Game.ComponentVitalStats));
            base.Update(dt);
        }
    }

}