using System;
using System.Collections.Generic;
using Game;
using SuAPI;

namespace RainWithoutDawn
{
    public class RainWithoutDawn : IMod
    {
        private UpdateExtensionToken m_updateRegistration;
        public string Name => "冷雨夜";
        public string Version => "1.0.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; }
        public bool IsMergeLib => true;

        // Source: UpdateExtensionRegistry.cs:UpdateExtensionEventBusExtensions.RegisterUpdateExtension
        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            m_updateRegistration = eventBus.RegisterUpdateExtension<SubsystemWeather>(
                "RainWithoutDawn", host => new RWDWeatherExtension(host));
        }

        // Source: UpdateExtensionRegistry.cs:UpdateExtensionToken.Dispose
        public void OnUnload()
        {
            m_updateRegistration?.Dispose();
            m_updateRegistration = null;
        }
    }
}
