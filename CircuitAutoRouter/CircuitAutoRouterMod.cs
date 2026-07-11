using System;
using System.Collections.Generic;
using Engine;
using Game;
using SuAPI;
using SuAPI;
using TemplatesDatabase;

namespace CircuitAutoRouter
{
    public class CircuitAutoRouterMod : IMod
    {
        public string Name => "电路自动排线";
        public string Version => "1.0.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            // Register SubsystemCircuitRouter as a new subsystem via GameDatabase manipulation
            // Source: Database.xml SubsystemTemplate/MemberSubsystemTemplate structure
            eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
            {
                return HandleGameDatabase((Database)args[0]);
            }, EventPriority.HIGHEST);

            // Make Rod block editable so OnEditInventoryItem gets called
            // Source: BlocksManager.Initialize event fires after blocks are loaded
            // Source: Block.cs line 54 — IsEditable is a public bool field
            // Source: RodBlock Index=195, BlocksData.csv IsEditable=FALSE (column 22)
            eventBus.SubscribeEvent("BlocksManager.Initialize", args =>
            {
                try
                {
                    Block rodBlock = BlocksManager.Blocks[195];
                    rodBlock.IsEditable = true;
                    Log.Information("[CircuitAutoRouter] Rod IsEditable set to true");
                }
                catch (Exception ex)
                {
                    Log.Error($"[CircuitAutoRouter] Failed to set Rod IsEditable: {ex.Message}");
                }
                // Return format: { bool success, Block[] blocks }
                return new object[] { true, args[0] };
            }, EventPriority.LOWEST);
        }

        public object[] HandleGameDatabase(Database database)
        {
            try
            {
                var subystemTemplateType = database.FindDatabaseObjectType("SubsystemTemplate", true);
                var folderType = database.FindDatabaseObjectType("Folder", true);
                var paramType = database.FindDatabaseObjectType("Parameter", true);
                var memberSubType = database.FindDatabaseObjectType("MemberSubsystemTemplate", true);
                var projectTemplateType = database.FindDatabaseObjectType("ProjectTemplate", true);

                // Inherit from BlockBehavior base SubsystemTemplate
                // GUID from: Database.xml line 3815
                var blockBehaviorParent = database.FindDatabaseObject(
                    new Guid("fefb9590-4972-4893-b02a-76063611b745"),
                    subystemTemplateType, true);

                // NestingParent: Subsystems/BlockBehaviors folder
                // GUID from: Database.xml line 3404
                var blockBehaviorsFolder = database.FindDatabaseObject(
                    new Guid("00c97f0f-731e-481c-9909-eae9cc5ee940"),
                    folderType, true);

                // Project template
                var projectTemplate = database.FindDatabaseObject(
                    new Guid("85023bf8-1c90-4dd1-9442-e6c13691d078"),
                    projectTemplateType, true);

                DatabaseObject subsystemTemplate = new DatabaseObject(
                    subystemTemplateType,
                    new Guid("A7B1C2D3-E4F5-6789-ABCD-EF0000000001"),
                    "CircuitRouter",
                    null);
                subsystemTemplate.Description = "Circuit auto-router subsystem";
                subsystemTemplate.ExplicitInheritanceParent = blockBehaviorParent;
                subsystemTemplate.NestingParent = blockBehaviorsFolder;

                // Parameter "Class" pointing to our subsystem class
                DatabaseObject parameterClass = new DatabaseObject(
                    paramType,
                    new Guid("A7B1C2D3-E4F5-6789-ABCD-EF0000000002"),
                    "Class",
                    "CircuitAutoRouter.SubsystemCircuitRouter");
                parameterClass.NestingParent = subsystemTemplate;

                // MemberSubsystemTemplate to add to Project
                DatabaseObject memberSubsystem = new DatabaseObject(
                    memberSubType,
                    new Guid("A7B1C2D3-E4F5-6789-ABCD-EF0000000003"),
                    "CircuitRouter",
                    null);
                memberSubsystem.Description = "";
                memberSubsystem.ExplicitInheritanceParent = subsystemTemplate;
                memberSubsystem.NestingParent = projectTemplate;

                Log.Information("[CircuitAutoRouter] Registered SubsystemCircuitRouter");
                return new object[] { true, database };
            }
            catch (Exception ex)
            {
                Log.Error($"[CircuitAutoRouter] HandleGameDatabase ERROR: {ex.Message}");
                return new object[] { false, database };
            }
        }

        public void OnUnload() { }
    }
}
