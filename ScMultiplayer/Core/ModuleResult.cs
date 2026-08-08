using System;

namespace ScMultiplayer.Core
{
    [Flags]
    internal enum ModuleResultFlags
    {
        None = 0,
        DidWork = 1,
        HasPendingWork = 2,
        Defer = 4,
        ResetRequested = 8
    }

    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:5 外围模块职责
    // A small value result keeps scheduling intent explicit without introducing a shared event bus.
    internal readonly struct ModuleResult
    {
        public ModuleResult(ModuleResultFlags flags, int workItems = 0)
        {
            Flags = flags;
            WorkItems = Math.Max(0, workItems);
        }

        public ModuleResultFlags Flags { get; }

        public int WorkItems { get; }

        public bool DidWork => (Flags & ModuleResultFlags.DidWork) != 0;

        public bool HasPendingWork => (Flags & ModuleResultFlags.HasPendingWork) != 0;

        public static ModuleResult None => new ModuleResult(ModuleResultFlags.None);

        public static ModuleResult Work(int workItems = 1) =>
            new ModuleResult(ModuleResultFlags.DidWork, workItems);
    }
}
