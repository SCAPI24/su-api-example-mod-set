using System;
using System.Collections.Generic;
using Engine;
using SuMod;
using SuMod.Tools;
using TemplatesDatabase;

// Source: D:\Users\Suceru\Desktop\生存战争三件套\Survivalcraft24102mono\SurvivalcraftMiniMap\MiniMap.cs
// 参考版已验证通过，直接沿用 HandleGameDatabase 注册逻辑

public class MiniMap : IMod
{
    public string Name => "地图";

    public string Version => "1.0.1";

    public IEnumerable<string> Dependencies => Array.Empty<string>();

    public bool IsEnabled { get; set; } = true;

    public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
    {
        eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
        {
            ; return HandleGameDatabase((Database)args[0]);
        }, EventPriority.HIGHEST);
    }

    public object[] HandleGameDatabase(Database database)
    {
        // Source: 参考版 MiniMap.cs HandleGameDatabase
        // 关键：ComponentTemplate 必须设 ExplicitInheritanceParent 继承已有模板
        // 关键：NestingParent 类型要精确匹配（Gameplay=Folder, Player=EntityTemplate）

        DatabaseObject ComponentTemplatemap = new DatabaseObject(
            database.FindDatabaseObjectType("ComponentTemplate", true),
            new Guid("387007A5-9269-1362-A0E7-DFEA4AC68E02"),
            "Map", null);
        ComponentTemplatemap.Description = "";
        // GUID from: 参考版 — 继承自已有的 ComponentTemplate
        ComponentTemplatemap.ExplicitInheritanceParent = database.FindDatabaseObject(
            new Guid("b05700ed-7e4e-4679-98f5-b597f421496b"),
            database.FindDatabaseObjectType("ComponentTemplate", true),
            true);
        // Gameplay 是 Folder 类型，不是 EntityTemplate
        ComponentTemplatemap.NestingParent = database.FindDatabaseObject(
            "Gameplay",
            database.FindDatabaseObjectType("Folder", true),
            true);

        DatabaseObject Parameterclass = new DatabaseObject(
            database.FindDatabaseObjectType("Parameter", true),
            new Guid("B13D2D65-46A7-D038-8111-DE8FCBA58FBC"),
            "Class",
            "SurvivalcraftMiniMap.SuComponentMap");
        Parameterclass.NestingParent = ComponentTemplatemap;

        DatabaseObject databaseObject1 = new DatabaseObject(
            database.FindDatabaseObjectType("MemberComponentTemplate", true),
            new Guid("736FC2A9-9B0A-2E00-F7C8-95A4A6811FEE"),
            "Map", null);
        databaseObject1.Description = "";
        // MemberComponentTemplate 继承自刚才创建的 ComponentTemplate
        databaseObject1.ExplicitInheritanceParent = database.FindDatabaseObject(
            new Guid("387007A5-9269-1362-A0E7-DFEA4AC68E02"),
            database.FindDatabaseObjectType("ComponentTemplate", true),
            true);
        // Player 是 EntityTemplate 类型
        databaseObject1.NestingParent = database.FindDatabaseObject(
            "Player",
            database.FindDatabaseObjectType("EntityTemplate", true),
            true);

        Log.Information($"database{0}", databaseObject1.Name);
        return new object[] { true, database };
    }

    public void OnUnload()
    {
        Log.Information("OnUnload");
    }
}
