using System;
using System.Collections.Generic;
using ScMultiplayer.Core;
using ScMultiplayer.Ports;

namespace ScMultiplayer.Control
{
    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:4 控制单元职责
    // The scheduler owns module order and lifecycle. It does not inspect or mutate game state.
    internal sealed class ModuleScheduler : IDisposable
    {
        private readonly List<IMultiplayerModule> m_modules =
            new List<IMultiplayerModule>();
        private bool m_initialized;

        public void Register(IMultiplayerModule module)
        {
            if (module == null)
                throw new ArgumentNullException(nameof(module));
            if (m_initialized)
                throw new InvalidOperationException("Modules must be registered before initialization.");
            if (m_modules.Contains(module))
                return;
            m_modules.Add(module);
        }

        public void Initialize(MultiplayerContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (m_initialized)
                return;

            for (int i = 0; i < m_modules.Count; i++)
                m_modules[i].Initialize(context);
            m_initialized = true;
        }

        public void Tick(in ModuleTickContext tickContext)
        {
            if (!m_initialized)
                return;

            for (int i = 0; i < m_modules.Count; i++)
                m_modules[i].Tick(in tickContext);
        }

        public void Reset(ModuleResetReason reason)
        {
            for (int i = m_modules.Count - 1; i >= 0; i--)
                m_modules[i].Reset(reason);
        }

        public void Dispose()
        {
            for (int i = m_modules.Count - 1; i >= 0; i--)
                m_modules[i].Dispose();
            m_modules.Clear();
            m_initialized = false;
        }
    }
}
