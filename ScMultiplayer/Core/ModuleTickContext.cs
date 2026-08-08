using System;

namespace ScMultiplayer.Core
{
    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:7.1 热点路径隔离
    // The context is a value passed through the scheduler; it does not expose game objects.
    internal readonly struct ModuleTickContext
    {
        public ModuleTickContext(
            long frameIndex,
            double now,
            float deltaTime,
            bool isHost,
            bool isConnected,
            int messageBudget,
            int timeBudgetMicroseconds)
        {
            FrameIndex = frameIndex;
            Now = now;
            DeltaTime = Math.Max(0f, deltaTime);
            IsHost = isHost;
            IsConnected = isConnected;
            MessageBudget = Math.Max(0, messageBudget);
            TimeBudgetMicroseconds = Math.Max(0, timeBudgetMicroseconds);
        }

        public long FrameIndex { get; }

        public double Now { get; }

        public float DeltaTime { get; }

        public bool IsHost { get; }

        public bool IsConnected { get; }

        public int MessageBudget { get; }

        public int TimeBudgetMicroseconds { get; }
    }
}
