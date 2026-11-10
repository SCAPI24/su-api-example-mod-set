using System;
using System.Collections.Generic;
using Game;
using SuAPI;
using SuUndergroundWorld.Core;

namespace SuUndergroundWorld.Plug
{
    // Source: SuDeepWorldHook.cs:Attached/Update/Detached; shared real Terrain host, one manager per Project.
    public sealed class SuUndergroundWorldMod : IMod
    {
        private UpdateExtensionToken m_updateToken;
        public string Name => "SuUndergroundWorld";
        public string Version => "0.1.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            if (m_updateToken != null) throw new InvalidOperationException("[SuUndergroundWorld] The Mod is already loaded.");
            if (eventBus == null) throw new ArgumentNullException(nameof(eventBus));
            m_updateToken = eventBus.RegisterUpdateExtension<SubsystemTerrain>(Name,
                host => new SuDeepWorldHook(host), EventPriority.LOWEST);
        }

        public void OnUnload()
        {
            m_updateToken?.Dispose();
            m_updateToken = null;
        }
    }
}
