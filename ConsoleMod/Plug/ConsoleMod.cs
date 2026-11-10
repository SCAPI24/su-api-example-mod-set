using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using TemplatesDatabase;

namespace ConsoleMod
{
    public class ConsoleMod : IMod
    {
        private const string InputName = "ConsoleModInput";
        private const string TemplateName = "ConsoleModInputTemplate";
        private IModEventBus m_eventBus;
        private EventSubscriptionToken m_databaseToken;
        private IDisposable m_updateToken;
        private bool m_inputEnabled;

        public string Name => "控制台";
        public string Version => "1.0.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            if (m_eventBus != null) throw new InvalidOperationException("[ConsoleMod] The Mod is already loaded.");
            m_eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            try
            {
                // Source: SubsystemUpdate.cs:Load — independent early input participates before the first substep.
                m_databaseToken = eventBus.SubscribeEvent("GameDatabase.GameDatabase", args =>
                {
                    if (args == null || args.Length != 1 || !(args[0] is Database database))
                        throw new InvalidOperationException("[ConsoleMod] Invalid GameDatabase.GameDatabase arguments.");
                    return HandleGameDatabase(database);
                }, EventPriority.HIGHEST);
                m_updateToken = eventBus.RegisterUpdateExtension<SubsystemGameWidgets>("ConsoleMod", host =>
                {
                    ConsoleInputSubsystem input = host.Project.FindSubsystem<ConsoleInputSubsystem>(InputName, true);
                    return input.GetController(host);
                }, EventPriority.NORMAL);
                ConsoleInputSubsystem.EnableModule();
                m_inputEnabled = true;
            }
            catch (Exception originalError)
            {
                try { OnUnload(); }
                catch (Exception cleanupError) { throw new AggregateException(originalError, cleanupError); }
                throw;
            }
        }

        // Source: SuAPICore/UpdateExtensionBootstrap.cs:InstallIntoDatabase — add a legal independent Member, never replace Views.
        public object[] HandleGameDatabase(Database database)
        {
            DatabaseObjectType subsystemType = database.FindDatabaseObjectType("SubsystemTemplate", true);
            DatabaseObjectType memberType = database.FindDatabaseObjectType("MemberSubsystemTemplate", true);
            DatabaseObjectType projectType = database.FindDatabaseObjectType("ProjectTemplate", true);
            DatabaseObjectType parameterType = database.FindDatabaseObjectType("Parameter", true);
            string className = typeof(ConsoleInputSubsystem).FullName;
            DatabaseObject template = database.Root.FindExplicitNestedChild(TemplateName, null, true, false);
            if (template == null)
            {
                template = new DatabaseObject(subsystemType, Guid.NewGuid(), TemplateName, null);
                AddParameter(template, parameterType, "Class", className);
                AddParameter(template, parameterType, "IsOptional", false);
                template.NestingParent = database.Root;
            }
            else if (template.Type != subsystemType || template.GetNestedValue<string>("Class") != className)
                throw new InvalidOperationException("[ConsoleMod] The input template name is occupied by another definition.");

            int installed = 0;
            foreach (DatabaseObject project in database.Root.GetExplicitNestingChildren(projectType, false).ToArray())
            {
                DatabaseObject[] members = project.GetEffectiveNestingChildren(memberType, true).ToArray();
                DatabaseObject views = members.SingleOrDefault(m => m.Name == "Views");
                if (views == null) continue;
                if (!members.Any(m => m.Name == "Update"))
                    throw new InvalidOperationException("[ConsoleMod] Project '" + project.Name + "' has Views but no Update subsystem.");
                DatabaseObject member = project.FindExplicitNestedChild(InputName, null, true, false);
                if (member == null)
                {
                    member = new DatabaseObject(memberType, Guid.NewGuid(), InputName, null)
                    {
                        ExplicitInheritanceParent = template
                    };
                    AddParameter(member, parameterType, "Dependencies", "Update,Views");
                    member.NestingParent = project;
                }
                else if (member.Type != memberType || member.GetNestedValue<string>("Class") != className
                    || member.GetNestedValue<string>("Dependencies") != "Update,Views")
                    throw new InvalidOperationException("[ConsoleMod] The input Member in project '" + project.Name + "' is incompatible.");
                installed++;
            }
            if (installed == 0) throw new InvalidOperationException("[ConsoleMod] No project contains the required Views subsystem.");
            return new object[] { true, database };
        }

        private static void AddParameter(DatabaseObject parent, DatabaseObjectType type, string name, object value)
        {
            new DatabaseObject(type, Guid.NewGuid(), name, value) { NestingParent = parent };
        }

        public void OnUnload()
        {
            var errors = new List<Exception>();
            try { m_updateToken?.Dispose(); }
            catch (Exception ex) { errors.Add(ex); }
            m_updateToken = null;
            try
            {
                if (m_eventBus != null && m_databaseToken != null) m_eventBus.UnsubscribeEvent(m_databaseToken);
            }
            catch (Exception ex) { errors.Add(ex); }
            m_databaseToken = null;
            m_eventBus = null;
            if (m_inputEnabled)
            {
                m_inputEnabled = false;
                try { ConsoleInputSubsystem.DisableModule(); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count != 0) throw new AggregateException("[ConsoleMod] Unload failed after cleanup.", errors);
        }
    }
}
