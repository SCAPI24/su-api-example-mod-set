using System;
using System.Collections.Generic;
using Game;
using SuAPI;

namespace TemperatureImmunity
{
    public sealed class TemperatureImmunityMod : IMod
    {
        private UpdateExtensionToken m_vitalStats;
        private UpdateExtensionToken m_flu;

        public string Name => "体温保持";
        public string Version => "1.0.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;

        // Source: UpdateExtensionRegistry.cs:RegisterUpdateExtension; ComponentVitalStats.cs:Update.
        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            if (eventBus == null) throw new ArgumentNullException(nameof(eventBus));
            if (m_vitalStats != null || m_flu != null)
                throw new InvalidOperationException("TemperatureImmunity is already registered.");
            var fields = Program.ModManager.ModParentField;
            var methods = Program.ModManager.ModParentMethod;
            var vitalStats = new VitalStatsTemperatureBindings(fields, methods);
            var flu = new FluImmunityBindings(fields);
            m_vitalStats = eventBus.RegisterUpdateExtension<ComponentVitalStats>("TemperatureImmunity",
                host => new VitalStatsTemperatureExtension(host, vitalStats), EventPriority.HIGHEST);
            try
            {
                m_flu = eventBus.RegisterUpdateExtension<ComponentFlu>("TemperatureImmunity",
                    host => new FluImmunityExtension(host, flu), EventPriority.HIGHEST);
            }
            catch
            {
                m_vitalStats.Dispose();
                m_vitalStats = null;
                throw;
            }
        }

        // Source: UpdateExtensionRegistry.cs:UpdateExtensionToken.Dispose.
        public void OnUnload()
        {
            m_vitalStats?.Dispose();
            m_flu?.Dispose();
            m_vitalStats = null;
            m_flu = null;
        }
    }
}
