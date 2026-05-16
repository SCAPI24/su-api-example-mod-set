using Engine;
using Game;
using SuMod;
using SuMod.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TemplatesDatabase;

namespace RainWithoutDawn
{
    public class RainWithoutDawn: IMod
    {
        public string Name => "冷雨夜";
        public string Version => "1.0.1";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {

            //modInjector.Register("Game.SubsystemTimeOfDay", );

            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                ; return HandleGameDatabase((Database)args[0]);
            }, EventPriority.HIGHEST);
        }
 

        public void OnUnload()
        {
            Log.Information($"OnUnload");
        }

        public object[] HandleGameDatabase(Database database)
        {
            var subsystemTimeOfDay = database.FindDatabaseObject(new Guid("1e884b27-fc1d-486a-bd0e-518e554b9734"), database.FindDatabaseObjectType("Parameter", true), true);
            subsystemTimeOfDay.Value = "RainWithoutDawn.RWDSubsystemTimeOfDay";
            /* DatabaseObject ComponentTemplatemap = new DatabaseObject(database.FindDatabaseObjectType("ComponentTemplate", true), new Guid("387007A5-9269-1362-A0E7-DFEA4AC68E02"), "Map", null);
             ComponentTemplatemap.Description = "";
             ComponentTemplatemap.ExplicitInheritanceParent = database.FindDatabaseObject(new Guid("b05700ed-7e4e-4679-98f5-b597f421496b"), database.FindDatabaseObjectType("ComponentTemplate", true), true);
             ComponentTemplatemap.NestingParent = database.FindDatabaseObject("Gameplay", database.FindDatabaseObjectType("Folder", true), true);

             DatabaseObject Parameterclass = new DatabaseObject(database.FindDatabaseObjectType("Parameter", true), new Guid("B13D2D65-46A7-D038-8111-DE8FCBA58FBC"), "Class", "SurvivalcraftMiniMap.SuComponentMap");
             Parameterclass.NestingParent = ComponentTemplatemap;

             DatabaseObject databaseObject1 = new DatabaseObject(database.FindDatabaseObjectType("MemberComponentTemplate", true), new Guid("736FC2A9-9B0A-2E00-F7C8-95A4A6811FEE"), "Map", null);
             databaseObject1.Description = "";
             databaseObject1.ExplicitInheritanceParent = database.FindDatabaseObject(new Guid("387007A5-9269-1362-A0E7-DFEA4AC68E02"), database.FindDatabaseObjectType("ComponentTemplate", true), true);

             databaseObject1.NestingParent = database.FindDatabaseObject("Player", database.FindDatabaseObjectType("EntityTemplate", true), true);*///挂载



            Log.Information($"HandleDatabase");
            return new object[] { true, database };
        }
    }
}
