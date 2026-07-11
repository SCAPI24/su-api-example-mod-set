using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using TemplatesDatabase;

namespace SuApocalypse
{
    public class SuApocalypseMod : IMod
    {
        public string Name => "Apocalypse";
        public string Version => "0.1.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; }
        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            // 替换 ComponentVitalStats → 只保留体力
            modInjector.Register("Game.ComponentVitalStats", "SuApocalypse.SuApocalypseVitalStats");

            // 注册 Apocalypse 管理组件(ComponentTemplate → Player)
            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                return HandleGameDatabase((Database)args[0]);
            }, EventPriority.HIGHEST);

            Log.Information("[SuApocalypse] Mod loaded");
        }

        public object[] HandleGameDatabase(Database database)
        {
            var componentTemplateType = database.FindDatabaseObjectType("ComponentTemplate", true);
            var parameterType = database.FindDatabaseObjectType("Parameter", true);
            var memberComponentTemplateType = database.FindDatabaseObjectType("MemberComponentTemplate", true);

            // Step 1: Create ComponentTemplate for ApocalypseManager
            var componentTemplate = new DatabaseObject(
                componentTemplateType,
                new Guid("A7B1C2D3-E4F5-6789-ABCD-EF0123456789"),
                "ApocalypseManager",
                null);
            componentTemplate.Description = "";
            // Inherit existing ComponentTemplate (same as WatchMod/MiniMap)
            componentTemplate.ExplicitInheritanceParent = database.FindDatabaseObject(
                new Guid("b05700ed-7e4e-4679-98f5-b597f421496b"),
                componentTemplateType,
                true);
            // NestingParent: Gameplay is Folder type
            componentTemplate.NestingParent = database.FindDatabaseObject(
                "Gameplay",
                database.FindDatabaseObjectType("Folder", true),
                true);

            // Step 2: Create Parameter "Class"
            var parameterClass = new DatabaseObject(
                parameterType,
                new Guid("B8C2D3E4-F5A6-7890-BCDE-F01234567890"),
                "Class",
                "SuApocalypse.SuApocalypseComponent");
            parameterClass.NestingParent = componentTemplate;

            // Step 3: Create MemberComponentTemplate → Player
            var memberComponent = new DatabaseObject(
                memberComponentTemplateType,
                new Guid("C9D3E4F5-A6B7-8901-CDEF-012345678901"),
                "ApocalypseManager",
                null);
            memberComponent.Description = "";
            memberComponent.ExplicitInheritanceParent = database.FindDatabaseObject(
                new Guid("A7B1C2D3-E4F5-6789-ABCD-EF0123456789"),
                componentTemplateType,
                true);
            // NestingParent: Player is EntityTemplate type
            memberComponent.NestingParent = database.FindDatabaseObject(
                "Player",
                database.FindDatabaseObjectType("EntityTemplate", true),
                true);

            Log.Information("[SuApocalypse] Registered ApocalypseManager ComponentTemplate");
            return new object[] { true, database };
        }

        public void OnUnload() { }
    }
}
