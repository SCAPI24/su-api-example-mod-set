using Engine;
using Game;
using Sumod;
using SuMod.Tools;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using TemplatesDatabase;
using XmlUtilities;
using static Game.Program;

namespace SuMod
{
    // Example mod implementation
    public class TemperatureImmunity : IMod
    {
        public string Name => "输出文本";
        public string Version => "1.0.1";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {

            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                ; return HandleGameDatabase((Database)args[0]);
            }, EventPriority.HIGHEST);

            /*modInjector.Register("Game.ComponentFlu", "Sumod.SumodComponentFlu");
            modInjector.Register("Game.ComponentVitalStats", "Sumod.SumodComponentVitalStats");*/
        }

     
        public object[] HandleGameDatabase(Database database)
        {
            var componentFlu = database.FindDatabaseObject(new Guid("88c778ff-b238-4303-b1c5-468cb0f6c73a"), database.FindDatabaseObjectType("Parameter", true), true);
            componentFlu.Value = "Sumod.SumodComponentFlu";
            var componentVitalStats = database.FindDatabaseObject(new Guid("aa7f845d-165e-4fff-95f0-453cd4e14cea"), database.FindDatabaseObjectType("Parameter", true), true);
            componentVitalStats.Value = "Sumod.SumodComponentVitalStats";
            Log.Information($"HandleDatabase");
            return new object[] { true, database };
        }

    

        public void OnUnload()
        {
            Log.Information($"OnUnload");
        }
    }
}