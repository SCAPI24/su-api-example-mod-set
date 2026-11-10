using System;
using Game;
using SuAPI;

namespace SuMultiplayer.Engine
{
    // Source: Survivalcraft/SuAPI/UpdateExtensionRegistry.cs:SuspendHostUpdates,ResumeHostUpdates,RegisterHostUpdateScope
    // Stateless access to the existing ModManager bus; state and ownership stay in SuAPI's project binding.
    internal static class MultiplayerUpdateScheduling
    {
        private static UpdateExtensionRegistry Registry
        {
            get
            {
                ModManager manager = Game.Program.ModManager
                    ?? throw new InvalidOperationException("[SuMP] ModManager is unavailable for feature-aware update scheduling.");
                return UpdateExtensionRegistry.For(manager.ModEventBus);
            }
        }

        public static void Suspend(string owner, IUpdateable host) => Registry.SuspendHostUpdates(owner, host);
        public static void Resume(string owner, IUpdateable host) => Registry.ResumeHostUpdates(owner, host);
        public static IDisposable RegisterScope(string owner, IUpdateable host, Func<float, IDisposable> enter)
            => Registry.RegisterHostUpdateScope(owner, host, enter);
    }
}
