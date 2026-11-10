using System;
using System.Collections.Generic;
using SuAPI;
using TemplatesDatabase;
using SuAPIShadows.Foliage;

namespace SuAPIShadows;

public sealed class ShadowsMod : IMod
{
    private IModEventBus m_events;
    private EventSubscriptionToken m_databaseToken;
    private static FoliageBlockRegistration s_foliage;

    internal static void CheckFoliage()
    {
        if (s_foliage == null) throw new InvalidOperationException("[SuAPIShadows] Foliage registration is not loaded.");
        s_foliage.Check();
    }

    public string Name => "Dynamic Shadows";

    public string Version => "1.0.1";

    public IEnumerable<string> Dependencies => Array.Empty<string>();

    public bool IsEnabled { get; set; } = true;

    public bool IsMergeLib => true;

    public void OnLoad(IModEventBus eventBus = null, IModInjector modInjector = null)
    {
        if (m_events != null || s_foliage != null) throw new InvalidOperationException("[SuAPIShadows] The Mod is already loaded.");
        m_events = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        try
        {
            s_foliage = FoliageBlockRegistration.Install(eventBus, modInjector);
            m_databaseToken = eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                if (args == null || args.Length != 1 || !(args[0] is Database database))
                    throw new InvalidOperationException("[SuAPIShadows] Invalid GameDatabase event arguments.");
                return RegisterSubsystem(database);
            }, EventPriority.HIGHEST);
        }
        catch { OnUnload(); throw; }
    }

    public void OnUnload()
    {
        if (m_databaseToken != null) m_events.UnsubscribeEvent(m_databaseToken);
        m_databaseToken = null;
        m_events = null;
        s_foliage?.Dispose();
        s_foliage = null;
    }

    private static object[] RegisterSubsystem(Database database)
    {
        // Source: CircuitAutoRouterMod:CircuitAutoRouterMod.HandleGameDatabase
        DatabaseObjectType subsystemType = database.FindDatabaseObjectType("SubsystemTemplate", true);
        DatabaseObjectType memberType = database.FindDatabaseObjectType("MemberSubsystemTemplate", true);
        DatabaseObjectType parameterType = database.FindDatabaseObjectType("Parameter", true);
        DatabaseObjectType folderType = database.FindDatabaseObjectType("Folder", true);
        DatabaseObjectType projectType = database.FindDatabaseObjectType("ProjectTemplate", true);

        DatabaseObject subsystem = new DatabaseObject(
            subsystemType,
            new Guid("9d8b4bda-5c29-5fc4-97df-c67d06642ac2"),
            "DynamicShadows",
            null)
        {
            Description = "Directional dynamic shadows",
            ExplicitInheritanceParent = database.FindDatabaseObject(
                new Guid("fefb9590-4972-4893-b02a-76063611b745"),
                subsystemType,
                true),
            NestingParent = database.FindDatabaseObject(
                new Guid("00c97f0f-731e-481c-9909-eae9cc5ee940"),
                folderType,
                true)
        };

        DatabaseObject classParameter = new DatabaseObject(
            parameterType,
            new Guid("11a8019f-3ac9-5206-a6d0-46910fd5a03c"),
            "Class",
            "SuAPIShadows.SubsystemDynamicShadows")
        {
            NestingParent = subsystem
        };

        // Source: Project.cs:Project.LoadSubsystem; SubsystemTerrain.cs:SubsystemTerrain.Load
        // TerrainRenderer is created during Terrain.Load, before our terrain shader setup.
        new DatabaseObject(
            parameterType,
            new Guid("4fd3b33d-6f85-4a66-ae5f-62a3c6750a1c"),
            "Dependencies",
            "Terrain")
        {
            NestingParent = subsystem
        };

        DatabaseObject member = new DatabaseObject(
            memberType,
            new Guid("063fdd1a-c90f-59ab-b12e-177422df74dd"),
            "DynamicShadows",
            null)
        {
            Description = string.Empty,
            ExplicitInheritanceParent = subsystem,
            NestingParent = database.FindDatabaseObject(
                new Guid("85023bf8-1c90-4dd1-9442-e6c13691d078"),
                projectType,
                true)
        };

        _ = classParameter;
        _ = member;
        return new object[] { true, database };
    }
}
