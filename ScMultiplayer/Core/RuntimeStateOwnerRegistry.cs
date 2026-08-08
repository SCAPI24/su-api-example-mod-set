using System;

namespace ScMultiplayer.Core
{
    internal enum RuntimeStateDomain
    {
        Core,
        Session,
        Join,
        Player,
        Terrain,
        Entity,
        Circuit,
        World,
        WorldControl,
        Ui,
        Diagnostics
    }

    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:6.2 状态所有权
    // This registry tracks reset generations while the legacy partial class is being strangled.
    // It prevents a future module from silently retaining state across worlds or clients.
    internal sealed class RuntimeStateOwnerRegistry
    {
        private readonly long[] m_generations = new long[Enum.GetValues<RuntimeStateDomain>().Length];

        public long GetGeneration(RuntimeStateDomain domain) => m_generations[(int)domain];

        public long Reset(RuntimeStateDomain domain)
        {
            int index = (int)domain;
            return ++m_generations[index];
        }

        public void ResetAll()
        {
            for (int i = 0; i < m_generations.Length; i++)
                m_generations[i]++;
        }
    }
}
