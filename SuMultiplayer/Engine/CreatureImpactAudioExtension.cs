using System.Collections;
using Game;
using SuAPI;

namespace SuMultiplayer.Engine
{
    // Source: ComponentChaseBehavior.cs:ComponentChaseBehavior.Update; ComponentMiner.cs:ComponentMiner.AttackBody
    internal sealed class CreatureImpactAudioExtension : UpdateExtension<ComponentChaseBehavior>
    {
        private readonly RemoteActionAudioReplica m_audio;
        private readonly NetworkAvatarSpawner m_avatars;
        private ComponentPlayer m_target;
        private PlayerStats m_stats;
        private long m_hitsBefore;
        private IList m_queue;
        private int m_startIndex;

        public CreatureImpactAudioExtension(ComponentChaseBehavior host,
            RemoteActionAudioReplica audio, NetworkAvatarSpawner avatars) : base(host)
        {
            m_audio = audio;
            m_avatars = avatars;
        }

        public override void Update(float dt)
        {
            ClearCapture();
            base.Update(dt);
            if (!m_audio.IsHostCaptureEnabled || !Host.IsActive ||
                !(Host.Target is ComponentPlayer target) ||
                !m_avatars.Contains(target.PlayerData) ||
                Host.Entity.FindComponent<ComponentCreature>()?.ComponentCreatureModel?.IsAttackHitMoment != true)
                return;

            PlayerStats stats = target.PlayerStats;
            if (stats == null || !m_audio.TryGetCreatureImpactQueue(Host.Project, out IList queue)) return;
            m_target = target;
            m_stats = stats;
            m_hitsBefore = stats.HitsReceived;
            m_queue = queue;
            m_startIndex = queue.Count;
        }

        public override void AfterUpdate(float dt, InvocationResult result)
        {
            try
            {
                // Injure increments HitsReceived on the struck player. The intended target alone
                // is insufficient because the native raycast can hit its mount instead.
                if (result.HostExecuted && m_stats != null && m_stats.HitsReceived > m_hitsBefore)
                    m_audio.CaptureCreatureImpact(Host.Project, m_queue, m_startIndex,
                        m_target.ComponentBody.Position);
            }
            finally
            {
                ClearCapture();
            }
        }

        public override void Detached() => ClearCapture();

        private void ClearCapture()
        {
            m_target = null;
            m_stats = null;
            m_queue = null;
        }
    }
}
