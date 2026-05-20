using Engine;
using Game;
using SuMod;
using SuMod.Tools;
using System;
using System.Collections.Generic;
using TemplatesDatabase;

namespace MemoryBankDrawMod
{
    public class MemoryBankDrawMod : IMod
    {
        public string Name => "Memory Bank Draw";
        public string Version => "1.0.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            // Source: Database.xml line 3535 — SubsystemMemoryBankBlockBehavior Class Parameter GUID
            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                return HandleGameDatabase((Database)args[0]);
            }, EventPriority.HIGHEST);
        }

        public object[] HandleGameDatabase(Database database)
        {
            // GUID from: Database.xml line 3535
            var param = database.FindDatabaseObject(
                new Guid("32a2d9ef-b01a-4f80-a6f8-5d2d5e9e9275"),
                database.FindDatabaseObjectType("Parameter", true),
                true);
            param.Value = "MemoryBankDrawMod.SuSubsystemMemoryBankBlockBehavior";
            Log.Information("[MemoryBankDraw] Replaced SubsystemMemoryBankBlockBehavior");
            return new object[] { true, database };
        }

        public void OnUnload() { }
    }
}
