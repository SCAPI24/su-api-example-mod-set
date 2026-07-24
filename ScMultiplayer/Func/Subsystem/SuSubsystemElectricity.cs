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
            if (ScMultiplayer.client?.IsConnected != true || m_synchronizer == null)
            {
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
