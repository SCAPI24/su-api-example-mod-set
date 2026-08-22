using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using TemplatesDatabase;

namespace MemoryBankDrawMod
{
    public class MemoryBankDrawMod : IMod
    {
        private IModEventBus m_eventBus;
        private EventSubscriptionToken m_databaseToken;
        private EventSubscriptionToken m_frameToken;

        public string Name => "Memory Bank Draw";
        public string Version => "1.1.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            m_eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            // Source: Database.xml line 3535 — SubsystemMemoryBankBlockBehavior Class Parameter GUID
            m_databaseToken = eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                return HandleGameDatabase((Database)args[0]);
            }, EventPriority.HIGHEST);

            // Source: Program.FrameHandler — replace native dialogs opened by other subsystem Mods.
            m_frameToken = eventBus.SubscribeEvent("Frame.Update", args =>
            {
                MemoryBankDialogCompatibility.ReplaceNativeDialogs();
                return args;
            }, EventPriority.LOWEST);
        }

        public object[] HandleGameDatabase(Database database)
        {
            // GUID from: Database.xml line 3535
            var param = database.FindDatabaseObject(
                new Guid("32a2d9ef-b01a-4f80-a6f8-5d2d5e9e9275"),
                database.FindDatabaseObjectType("Parameter", true),
                true);
            param.Value = "MemoryBankDrawMod.SuSubsystemMemoryBankBlockBehavior";
            return new object[] { true, database };
        }

        public void OnUnload()
        {
            if (m_eventBus != null && m_databaseToken != null)
            {
                m_eventBus.UnsubscribeEvent(m_databaseToken);
            }
            if (m_eventBus != null && m_frameToken != null)
            {
                m_eventBus.UnsubscribeEvent(m_frameToken);
            }
            m_databaseToken = null;
            m_frameToken = null;
            m_eventBus = null;
        }
    }
}
