using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using TemplatesDatabase;

namespace RateReminder
{
    public class RateReminderMod : IMod
    {
        public string Name => "好评提醒";
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
            // Step 1: Create ComponentTemplate for RateReminder
            DatabaseObject componentTemplate = new DatabaseObject(
                database.FindDatabaseObjectType("ComponentTemplate", true),
                new Guid("A7E3C201-4D5B-8E2F-9013-B6F8A2D4E571"),
                "RateReminder",
                null);
            componentTemplate.Description = "";
            // Inherit existing ComponentTemplate (same parent as WatchMod)
            componentTemplate.ExplicitInheritanceParent = database.FindDatabaseObject(
                new Guid("b05700ed-7e4e-4679-98f5-b597f421496b"),
                database.FindDatabaseObjectType("ComponentTemplate", true),
                true);
            // NestingParent: Gameplay is Folder type
            componentTemplate.NestingParent = database.FindDatabaseObject(
                "Gameplay",
                database.FindDatabaseObjectType("Folder", true),
                true);

            // Step 2: Create Parameter "Class" pointing to RateReminderComponent
            DatabaseObject parameterClass = new DatabaseObject(
                database.FindDatabaseObjectType("Parameter", true),
                new Guid("C9F5D302-6E7A-1F4B-8325-D1E3C7A9B685"),
                "Class",
                "RateReminder.RateReminderComponent");
            parameterClass.NestingParent = componentTemplate;

            // Step 3: Create MemberComponentTemplate to register as Player member
            DatabaseObject memberComponent = new DatabaseObject(
                database.FindDatabaseObjectType("MemberComponentTemplate", true),
                new Guid("D8A4E603-7F9B-2C5D-9436-E2F4D8B0C796"),
                "RateReminder",
                null);
            memberComponent.Description = "";
            // Inherit from our ComponentTemplate
            memberComponent.ExplicitInheritanceParent = database.FindDatabaseObject(
                new Guid("A7E3C201-4D5B-8E2F-9013-B6F8A2D4E571"),
                database.FindDatabaseObjectType("ComponentTemplate", true),
                true);
            // NestingParent: Player is EntityTemplate type
            memberComponent.NestingParent = database.FindDatabaseObject(
                "Player",
                database.FindDatabaseObjectType("EntityTemplate", true),
                true);

            Log.Information("[RateReminder] Registered RateReminder ComponentTemplate");
            return new object[] { true, database };
        }

        public void OnUnload() { }
    }
}