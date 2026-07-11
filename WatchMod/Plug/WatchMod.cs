using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using TemplatesDatabase;

namespace WatchMod
{
    public class WatchMod : IMod
    {
        public string Name => "手表";
        public string Version => "1.0.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => false;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                return HandleGameDatabase((Database)args[0]);
            }, EventPriority.HIGHEST);
        }

        public object[] HandleGameDatabase(Database database)
        {
            // Source: MiniMap HandleGameDatabase — same pattern
            // Step 1: Create ComponentTemplate for Watch
            DatabaseObject componentTemplate = new DatabaseObject(
                database.FindDatabaseObjectType("ComponentTemplate", true),
                new Guid("387007A5-9269-1362-A0E7-DFEA4AC68E04"), // WatchMod GUID (unique, last nibble changed)
                "Watch",
                null);
            componentTemplate.Description = "";
            // Inherit existing ComponentTemplate
            // GUID from: MiniMap reference
            componentTemplate.ExplicitInheritanceParent = database.FindDatabaseObject(
                new Guid("b05700ed-7e4e-4679-98f5-b597f421496b"),
                database.FindDatabaseObjectType("ComponentTemplate", true),
                true);
            // NestingParent: Gameplay is Folder type
            componentTemplate.NestingParent = database.FindDatabaseObject(
                "Gameplay",
                database.FindDatabaseObjectType("Folder", true),
                true);

            // Step 2: Create Parameter "Class" pointing to SuWatchComponent
            DatabaseObject parameterClass = new DatabaseObject(
                database.FindDatabaseObjectType("Parameter", true),
                new Guid("B13D2D65-46A7-D038-8111-DE8FCBA58FBD"), // WatchMod Parameter GUID (unique)
                "Class",
                "WatchMod.SuWatchComponent");
            parameterClass.NestingParent = componentTemplate;

            // Step 3: Create MemberComponentTemplate to register Watch as Player member
            DatabaseObject memberComponent = new DatabaseObject(
                database.FindDatabaseObjectType("MemberComponentTemplate", true),
                new Guid("736FC2A9-9B0A-2E00-F7C8-95A4A6811FEF"), // WatchMod Member GUID (unique)
                "Watch",
                null);
            memberComponent.Description = "";
            // Inherit from our ComponentTemplate
            memberComponent.ExplicitInheritanceParent = database.FindDatabaseObject(
                new Guid("387007A5-9269-1362-A0E7-DFEA4AC68E04"),
                database.FindDatabaseObjectType("ComponentTemplate", true),
                true);
            // NestingParent: Player is EntityTemplate type
            memberComponent.NestingParent = database.FindDatabaseObject(
                "Player",
                database.FindDatabaseObjectType("EntityTemplate", true),
                true);

            Log.Information("[WatchMod] Registered Watch ComponentTemplate");
            return new object[] { true, database };
        }

        public void OnUnload() { }
    }
}