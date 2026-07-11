using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using System;
using System.Collections.Generic;
using TemplatesDatabase;

namespace GodMode
{
    public class GodModeMod : IMod
    {
        public string Name => "无敌";
        public string Version => "1.0.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; }
        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                return HandleGameDatabase((Database)args[0]);
            }, EventPriority.HIGHEST);
        }

        public object[] HandleGameDatabase(Database database)
        {
            // Source: Pak/Database.xml - ComponentHealth Class Parameter
            var componentHealth = database.FindDatabaseObject(
                new Guid("4e14ce27-fdef-46ca-8ea0-26af43c215e5"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentHealth.Value = "GodMode.GodModeComponentHealth";

            // Source: Pak/Database.xml - ComponentVitalStats Class Parameter
            var componentVitalStats = database.FindDatabaseObject(
                new Guid("aa7f845d-165e-4fff-95f0-453cd4e14cea"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentVitalStats.Value = "GodMode.GodModeComponentVitalStats";

            // Source: Pak/Database.xml - ComponentOnFire Class Parameter
            var componentOnFire = database.FindDatabaseObject(
                new Guid("70e6fe52-8205-464a-88d9-40d4faf39d74"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentOnFire.Value = "GodMode.GodModeComponentOnFire";

            // Source: Pak/Database.xml - ComponentFlu Class Parameter
            var componentFlu = database.FindDatabaseObject(
                new Guid("88c778ff-b238-4303-b1c5-468cb0f6c73a"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentFlu.Value = "GodMode.GodModeComponentFlu";

            // Source: Pak/Database.xml - ComponentSickness Class Parameter
            var componentSickness = database.FindDatabaseObject(
                new Guid("2ecdc324-1a9e-444f-941d-f313447c00a5"),
                database.FindDatabaseObjectType("Parameter", true), true);
            componentSickness.Value = "GodMode.GodModeComponentSickness";

            Log.Information("[GodMode] All invincibility replacements registered");
            return new object[] { true, database };
        }

        public void OnUnload() { }
    }
}
