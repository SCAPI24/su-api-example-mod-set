using ScMultiplayer.Core;
using ScMultiplayer.Ports;

namespace ScMultiplayer.Modules.Session
{
    // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.UpdateFrame
    // Session metadata is the first migrated module. It does not own game objects or transport
    // state; it only advances the shared session snapshot at the scheduler's fixed point.
    internal sealed class SessionStateModule : IMultiplayerModule
    {
        private MultiplayerSessionState m_session;

        public string Name => "SessionState";

        public RuntimeStateDomain StateDomain => RuntimeStateDomain.Core;

        public void Initialize(MultiplayerContext context)
        {
            m_session = context?.Session;
        }

        public void Tick(in ModuleTickContext tickContext)
        {
            m_session?.Update(tickContext.IsHost, tickContext.IsConnected);
        }

        public void Reset(ModuleResetReason reason)
        {
            m_session?.Reset();
        }

        public void Dispose()
        {
            m_session = null;
        }
    }
}
