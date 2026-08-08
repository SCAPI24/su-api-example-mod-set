using ScMultiplayer.Core;
using ScMultiplayer.Ports;

namespace ScMultiplayer.Modules.Runtime
{
    internal abstract class RuntimePhaseModule : IMultiplayerModule
    {
        protected IMultiplayerRuntimeHost Runtime;

        public abstract string Name { get; }

        public abstract RuntimeStateDomain StateDomain { get; }

        public virtual void Initialize(MultiplayerContext context)
        {
            Runtime = context?.Runtime;
        }

        public abstract void Tick(in ModuleTickContext tickContext);

        public virtual void Reset(ModuleResetReason reason)
        {
        }

        public virtual void Dispose()
        {
            Runtime = null;
        }
    }

    // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.UpdateFrame
    internal sealed class SessionRuntimeModule : RuntimePhaseModule
    {
        public override string Name => "SessionRuntime";

        public override RuntimeStateDomain StateDomain => RuntimeStateDomain.Session;

        public override void Tick(in ModuleTickContext tickContext) =>
            Runtime?.RunSessionPhase(in tickContext);
    }

    // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.UpdateFrame
    internal sealed class JoinTransferModule : RuntimePhaseModule
    {
        public override string Name => "JoinTransfer";

        public override RuntimeStateDomain StateDomain => RuntimeStateDomain.Join;

        public override void Tick(in ModuleTickContext tickContext) =>
            Runtime?.RunJoinPhase(in tickContext);
    }

    // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.UpdateFrame
    internal sealed class WorldControlModule : RuntimePhaseModule
    {
        public override string Name => "WorldControl";

        public override RuntimeStateDomain StateDomain => RuntimeStateDomain.WorldControl;

        public override void Tick(in ModuleTickContext tickContext) =>
            Runtime?.RunWorldControlPhase(in tickContext);
    }

    // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:CircuitSynchronizer.Update
    internal sealed class CircuitRuntimeModule : RuntimePhaseModule
    {
        public override string Name => "Circuit";

        public override RuntimeStateDomain StateDomain => RuntimeStateDomain.Circuit;

        public override void Tick(in ModuleTickContext tickContext) =>
            Runtime?.RunCircuitPhase(in tickContext);
    }

    // Source: Survivalcraft/Game/GameManager.cs:GameManager.UpdateProject
    internal sealed class WorldRuntimeModule : RuntimePhaseModule
    {
        public override string Name => "World";

        public override RuntimeStateDomain StateDomain => RuntimeStateDomain.World;

        public override void Tick(in ModuleTickContext tickContext) =>
            Runtime?.RunWorldPhase(in tickContext);
    }

    // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
    internal sealed class PlayerRuntimeModule : RuntimePhaseModule
    {
        public override string Name => "Player";

        public override RuntimeStateDomain StateDomain => RuntimeStateDomain.Player;

        public override void Tick(in ModuleTickContext tickContext) =>
            Runtime?.RunPlayerPhase(in tickContext);
    }

    // Source: Mod/ScMultiplayer/Func/WorldObjectSynchronizer.cs:WorldObjectSynchronizer.Update
    internal sealed class EntityRuntimeModule : RuntimePhaseModule
    {
        public override string Name => "Entity";

        public override RuntimeStateDomain StateDomain => RuntimeStateDomain.Entity;

        public override void Tick(in ModuleTickContext tickContext) =>
            Runtime?.RunEntityPhase(in tickContext);
    }

    // Source: Mod/ScMultiplayer/Func/Screen/SuNetPlayScreen.cs:SuNetPlayScreen.Update
    internal sealed class UiRuntimeModule : RuntimePhaseModule
    {
        public override string Name => "UI";

        public override RuntimeStateDomain StateDomain => RuntimeStateDomain.Ui;

        public override void Tick(in ModuleTickContext tickContext) =>
            Runtime?.RunUiPhase(in tickContext);
    }
}
