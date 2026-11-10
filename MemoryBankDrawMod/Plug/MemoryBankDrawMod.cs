using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;

namespace MemoryBankDrawMod
{
    public class MemoryBankDrawMod : IMod
    {
        private IModEventBus m_eventBus;
        private EventSubscriptionToken m_frameToken;

        public string Name => "Memory Bank Draw";
        public string Version => "1.1.0";
        public IEnumerable<string> Dependencies => Array.Empty<string>();
        public bool IsEnabled { get; set; } = true;
        public bool IsMergeLib => true;

        public void OnLoad(IModEventBus eventBus, IModInjector modInjector)
        {
            if (m_eventBus != null) throw new InvalidOperationException("[MemoryBankDrawMod] The Mod is already loaded.");
            MemoryBankDialogPresenter.ValidateContract();
            m_eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            // Source: Program.cs:Run; SubsystemMemoryBankBlockBehavior.cs:OnEditInventoryItem/OnEditBlock.
            // Keep the real subsystem and its save/network callback; this existing presentation event is the sole enhancement entry.
            m_frameToken = eventBus.SubscribeEvent("Frame.Update", args =>
            {
                try { MemoryBankDialogPresenter.EnhanceOpenDialogs(); }
                catch (Exception ex)
                {
                    if (m_frameToken != null) m_eventBus.UnsubscribeEvent(m_frameToken);
                    m_frameToken = null;
                    Log.Error("[MemoryBankDrawMod] Dialog enhancement failed: " + ex);
                    // Source: ModEventBus.cs:TriggerEvent — report explicitly before its compatibility catch consumes the exception.
                    ExceptionManager.ReportExceptionToUser("Memory Bank Draw could not enhance the memory editor.", ex);
                    throw;
                }
                return args;
            }, EventPriority.LOWEST);
        }

        public void OnUnload()
        {
            if (m_eventBus != null && m_frameToken != null)
            {
                m_eventBus.UnsubscribeEvent(m_frameToken);
            }
            m_frameToken = null;
            m_eventBus = null;
            MemoryBankDialogPresenter.CloseOwnedDialogs();
        }
    }
}
