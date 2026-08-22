using ScMultiplayer.Core;
using ScMultiplayer.Ports;

namespace ScMultiplayer.Modules.World
{
    // Source: Mod/ScMultiplayer/DataModification/DataModificationCoordinator.cs:
    // DataModificationCoordinator.Update
    // Data modifications run after network ingress and before the normal world phase. The
    // coordinator owns its own bounded work queues, so a large request cannot expand the generic
    // end-of-frame queue or run on the Comms thread.
    internal sealed class DataModificationModule : IMultiplayerModule
    {
        public string Name => "DataModification";

        public RuntimeStateDomain StateDomain => RuntimeStateDomain.DataModification;

        private IMultiplayerRuntimeHost m_runtime;

        public void Initialize(MultiplayerContext context)
        {
            m_runtime = context?.Runtime;
        }

        public void Tick(in ModuleTickContext tickContext)
        {
            m_runtime?.RunDataModificationPhase(in tickContext);
        }

        public void Reset(ModuleResetReason reason)
        {
        }

        public void Dispose()
        {
            m_runtime = null;
        }
    }
}
