using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using TemplatesDatabase;

namespace ConsoleMod
{
    public class ConsoleMod : IMod
    {
        public string Name => "控制台";
        public string Version => "1.0.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; }
        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            // GUID from: Database.xml line 3746, SubsystemGameWidgets Class Parameter
            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                return HandleGameDatabase((Database)args[0]);
            }, EventPriority.HIGHEST);
        }

        public object[] HandleGameDatabase(Database database)
        {
            // GUID from: Database.xml line 3746
            var param = database.FindDatabaseObject(
                new Guid("6bf14dc6-32e7-4e8c-b3c4-438e0eee13ad"),
                database.FindDatabaseObjectType("Parameter", true),
                true);
            param.Value = "ConsoleMod.ConsoleSubsystemGameWidgets";
            Log.Information("[ConsoleMod] Replaced SubsystemGameWidgets");
            return new object[] { true, database };
        }

        public void OnUnload() { }
    }
}
