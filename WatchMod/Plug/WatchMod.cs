using Game;
using SuAPI;
using System;
using System.Collections.Generic;

namespace WatchMod
{
    public class WatchMod : IMod
    {
        private UpdateExtensionToken m_updateToken;

        public string Name => "手表";
        public string Version => "1.0.1";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            if (m_updateToken != null)
                throw new InvalidOperationException("[WatchMod] The Mod is already loaded.");
            if (eventBus == null) throw new ArgumentNullException(nameof(eventBus));

            // Source: UpdateExtensionRegistry.cs:UpdateExtensionEventBusExtensions.RegisterUpdateExtension
            m_updateToken = eventBus.RegisterUpdateExtension<SubsystemGameWidgets>(
                "WatchMod", host => new WatchGameWidgetsExtension(host));
        }

        public void OnUnload()
        {
            // Source: UpdateExtensionRegistry.cs:UpdateExtensionToken.Dispose
            m_updateToken?.Dispose();
            m_updateToken = null;
        }
    }
}
