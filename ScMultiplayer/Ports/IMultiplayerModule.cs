using System;
using ScMultiplayer.Core;

namespace ScMultiplayer.Ports
{
    internal enum ModuleResetReason
    {
        SessionChanged,
        WorldChanged,
        ClientDisconnected,
        ModUnloaded
    }

    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:5 外围模块职责
    // One lifecycle contract is enough for the first migration stage. Business-specific
    // interfaces are added only when a real boundary requires them.
    internal interface IMultiplayerModule : IDisposable
    {
        string Name { get; }

        RuntimeStateDomain StateDomain { get; }

        void Initialize(MultiplayerContext context);

        void Tick(in ModuleTickContext tickContext);

        void Reset(ModuleResetReason reason);
    }
}
