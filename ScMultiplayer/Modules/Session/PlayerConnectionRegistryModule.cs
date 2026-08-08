using ScMultiplayer.Core;
using ScMultiplayer.Ports;

namespace ScMultiplayer.Modules.Session
{
    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:Phase 2
    // Lifecycle registration is performed by the existing Client/Join adapters. The module owns
    // reset boundaries so connection metadata cannot survive a world or mod lifecycle reset.
    internal sealed class PlayerConnectionRegistryModule : IMultiplayerModule
    {
        private PlayerConnectionRegistry m_registry;

        public string Name => "PlayerConnectionRegistry";

        public RuntimeStateDomain StateDomain => RuntimeStateDomain.Session;

        public void Initialize(MultiplayerContext context)
        {
            m_registry = context?.Connections;
        }

        public void Tick(in ModuleTickContext tickContext)
        {
        }

        public void Reset(ModuleResetReason reason)
        {
            m_registry?.Reset();
        }

        public void Dispose()
        {
            m_registry = null;
        }
    }
}
