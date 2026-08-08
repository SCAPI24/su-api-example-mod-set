using System;
using ScMultiplayer.Core;

namespace ScMultiplayer.Ports
{
    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:6.1 依赖方向
    // This is the only control-to-business callback surface. It preserves the existing
    // method order while allowing the composition root to schedule domains independently.
    internal interface IMultiplayerRuntimeHost
    {
        void RunSessionPhase(in ModuleTickContext tickContext);

        void RunJoinPhase(in ModuleTickContext tickContext);

        void RunWorldControlPhase(in ModuleTickContext tickContext);

        void RunCircuitPhase(in ModuleTickContext tickContext);

        void RunWorldPhase(in ModuleTickContext tickContext);

        void RunPlayerPhase(in ModuleTickContext tickContext);

        void RunEntityPhase(in ModuleTickContext tickContext);

        void RunUiPhase(in ModuleTickContext tickContext);
    }

    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:6.1 依赖方向
    // Transport mechanics stay separate from reliable policy. No module may call Comms directly.
    internal interface IReliableChannel
    {
        int PendingCount { get; }

        void Send(int targetClientId, byte[] payload, bool sequenced, bool latest);

        void Reset(int clientId);
    }

    internal interface IGameThreadDispatcher
    {
        void Enqueue(Action action);

        void EnqueueEndOfFrame(Action action);
    }

    internal interface IAuthoritativeWorld
    {
        bool IsReady { get; }

        void ResetClient(int clientId);
    }

    internal interface IWorldSnapshotStore
    {
        bool TryGetRevision(long coordinateKey, out int revision);

        void Reset(int clientId);
    }

    internal interface IPlayerStateStore
    {
        bool TryGetPlayer(int clientId, out object state);

        void Reset(int clientId);
    }
}
