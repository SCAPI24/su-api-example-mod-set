using System;
using System.Collections.Generic;
using SuAPI;
using TemplatesDatabase;

namespace ControlAnimal;

public class ControlAnimalMod : IMod
{
    public string Name => "控制动物";
    public string Version => "1.0.0";
    public IEnumerable<string> Dependencies => Array.Empty<string>();
    public bool IsEnabled { get; set; } = true;
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
        // Source: WatchMod/Plug/WatchMod.cs:WatchMod.HandleGameDatabase
        DatabaseObject componentTemplate = new DatabaseObject(
            database.FindDatabaseObjectType("ComponentTemplate", true),
            new Guid("C87007A5-9269-1362-A0E7-DFEA4AC68E04"),
            "ControlAnimal",
            null);
        componentTemplate.Description = "";
        componentTemplate.ExplicitInheritanceParent = database.FindDatabaseObject(
            new Guid("b05700ed-7e4e-4679-98f5-b597f421496b"),
            database.FindDatabaseObjectType("ComponentTemplate", true),
            true);
        componentTemplate.NestingParent = database.FindDatabaseObject(
            "Gameplay",
            database.FindDatabaseObjectType("Folder", true),
            true);

        DatabaseObject parameterClass = new DatabaseObject(
            database.FindDatabaseObjectType("Parameter", true),
            new Guid("D13D2D65-46A7-D038-8111-DE8FCBA58FBD"),
            "Class",
            "ControlAnimal.ControlAnimalComponent");
        parameterClass.NestingParent = componentTemplate;

        DatabaseObject memberComponent = new DatabaseObject(
            database.FindDatabaseObjectType("MemberComponentTemplate", true),
            new Guid("E36FC2A9-9B0A-2E00-F7C8-95A4A6811FEF"),
            "ControlAnimal",
            null);
        memberComponent.Description = "";
        memberComponent.ExplicitInheritanceParent = database.FindDatabaseObject(
            new Guid("C87007A5-9269-1362-A0E7-DFEA4AC68E04"),
            database.FindDatabaseObjectType("ComponentTemplate", true),
            true);
        memberComponent.NestingParent = database.FindDatabaseObject(
            "Player",
            database.FindDatabaseObjectType("EntityTemplate", true),
            true);

        // Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.UpdateWidgets
        DatabaseObject uiComponentTemplate = new DatabaseObject(
            database.FindDatabaseObjectType("ComponentTemplate", true),
            new Guid("AF114376-D091-4E7A-971B-C49D57A88E32"),
            "ControlAnimalUi",
            null);
        uiComponentTemplate.Description = "";
        uiComponentTemplate.ExplicitInheritanceParent = database.FindDatabaseObject(
            new Guid("b05700ed-7e4e-4679-98f5-b597f421496b"),
            database.FindDatabaseObjectType("ComponentTemplate", true),
            true);
        uiComponentTemplate.NestingParent = database.FindDatabaseObject(
            "Gameplay",
            database.FindDatabaseObjectType("Folder", true),
            true);

        DatabaseObject uiParameterClass = new DatabaseObject(
            database.FindDatabaseObjectType("Parameter", true),
            new Guid("3E72A839-C564-4AC8-B9C8-6730D6A0B65E"),
            "Class",
            "ControlAnimal.ControlAnimalUiComponent");
        uiParameterClass.NestingParent = uiComponentTemplate;

        DatabaseObject uiMemberComponent = new DatabaseObject(
            database.FindDatabaseObjectType("MemberComponentTemplate", true),
            new Guid("96C34B02-78E6-4D65-B44D-BDF91BA5F978"),
            "ControlAnimalUi",
            null);
        uiMemberComponent.Description = "";
        uiMemberComponent.ExplicitInheritanceParent = uiComponentTemplate;
        uiMemberComponent.NestingParent = database.FindDatabaseObject(
            "Player",
            database.FindDatabaseObjectType("EntityTemplate", true),
            true);

        return new object[] { true, database };
    }

    public void OnUnload()
    {
    }
}
