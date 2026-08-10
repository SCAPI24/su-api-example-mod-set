using Engine;
using Game;

namespace ScMultiplayer
{
    public sealed class SuSubsystemElectricity : SubsystemElectricity, IUpdateable
    {
        private const int MaximumCatchUpStepsPerFrame = 10;

        private CircuitSynchronizer m_synchronizer;
        private float m_remainingNetworkSimulationTime;

        internal void AttachSynchronizer(CircuitSynchronizer synchronizer)
        {
            if (ReferenceEquals(m_synchronizer, synchronizer)) return;
            m_synchronizer = synchronizer;
            m_remainingNetworkSimulationTime = 0f;
        }

        internal void DetachSynchronizer(CircuitSynchronizer synchronizer)
        {
            if (!ReferenceEquals(m_synchronizer, synchronizer)) return;
            m_synchronizer = null;
            m_remainingNetworkSimulationTime = 0f;
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
        void IUpdateable.Update(float dt)
        {
            if (ScMultiplayer.currentInstance?.IsNetworkSessionActive(Project) != true)
            {
                m_remainingNetworkSimulationTime = 0f;
                base.Update(dt);
                return;
            }
            // Source: Survivalcraft/Game/SubsystemUpdate.cs:SubsystemUpdate.Update
            // SubsystemTime.NextFrame decides the logical loop count before updateables run.
            // Apply the client authority rule before any circuit/synchronizer early return, so
            // maps without circuits cannot accidentally execute the whole client world 20 times.
            ScMultiplayer.currentInstance?.MaintainClientAuthoritativeTimeFlow(Project);
            if (m_synchronizer == null)
            {
                // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
                // CircuitSynchronizer.EnsureBound
                // A connected client must not run the native bootstrap circuit before the
                // synchronizer has attached and received an authoritative fence/snapshot. The
                // project loader queues newly created elements for simulation; executing that
                // queue here can create a client-only edge before host state is applied.
                if (!ScMultiplayer.IsHost &&
                    ScMultiplayer.client?.IsConnected == true)
                {
                    // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
                    // A newly loaded client Project can run one native bootstrap step before
                    // CircuitSynchronizer.EnsureBound. Holding every connected client here,
                    // rather than relying on the join-state flag, prevents a client-only
                    // voltage edge during that short binding window.
                    m_remainingNetworkSimulationTime = 0f;
                    return;
                }
                base.Update(dt);
                return;
            }
            if (ScMultiplayer.client?.IsConnected != true)
            {
                // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:
                // ScMultiplayer.UpdateReliableTransportHealth
                // Do not let the client run an independent circuit timeline while a stalled
                // reliable transport is being replaced and an authoritative rejoin is pending.
                if (ScMultiplayer.currentInstance?.ShouldHoldCircuitForReconnect == true ||
                    ScMultiplayer.currentInstance?.ShouldHoldClientCircuitBeforeBinding == true)
                {
                    m_remainingNetworkSimulationTime = 0f;
                    return;
                }
                base.Update(dt);
                return;
            }
            if (m_synchronizer.IsSimulationPaused)
            {
                m_remainingNetworkSimulationTime = 0f;
                return;
            }

            int steps = ScMultiplayer.IsHost
                ? GetHostStepCount(dt)
                : GetClientStepCount(dt);
            for (int i = 0; i < steps; i++)
            {
                m_synchronizer.PrepareCircuitStep(CircuitStep + 1);
                SuSubsystemTerrain terrain = !ScMultiplayer.IsHost
                    ? SubsystemTerrain as SuSubsystemTerrain
                    : null;
                terrain?.BeginClientCircuitStep();
                try
                {
                    // Calling the native update with exactly one circuit interval advances one
                    // step. Its private fractional remainder is preserved across calls.
                    base.Update(CircuitStepDuration);
                    m_synchronizer.CompleteCircuitStep(CircuitStep);
                }
                finally
                {
                    terrain?.EndClientCircuitStep();
                }
            }
        }

        // Source: Survivalcraft/Game/SubsystemElectricity.cs:SubsystemElectricity.Update
        private int GetHostStepCount(float dt)
        {
            m_remainingNetworkSimulationTime = MathUtils.Min(
                m_remainingNetworkSimulationTime + dt, 0.1f);
            int steps = MathUtils.Min(
                (int)(m_remainingNetworkSimulationTime / CircuitStepDuration),
                MaximumCatchUpStepsPerFrame);
            m_remainingNetworkSimulationTime -= steps * CircuitStepDuration;
            return steps;
        }

        // Source: Mod/ScMultiplayer/Func/Circuit/CircuitSynchronizer.cs:
        // CircuitSynchronizer.GetCircuitStepTarget
        private int GetClientStepCount(float dt)
        {
            int? target = m_synchronizer.GetCircuitStepTarget();
            if (target.HasValue)
            {
                m_remainingNetworkSimulationTime = 0f;
                return MathUtils.Clamp(target.Value - CircuitStep, 0,
                    MaximumCatchUpStepsPerFrame);
            }
            return GetHostStepCount(dt);
        }
    }
}
