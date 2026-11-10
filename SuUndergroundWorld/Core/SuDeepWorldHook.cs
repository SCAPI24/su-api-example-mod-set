using System;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;

namespace SuUndergroundWorld.Core
{
    // Source: SubsystemTerrain.Update; UpdateExtension.Update — body physics already ran; Terrain runs after all votes.
    internal sealed class SuDeepWorldHook : UpdateExtension<SubsystemTerrain>
    {
        private readonly SuWorldManager m_manager = new SuWorldManager();
        private bool m_stopped;
        private bool m_subscribed;

        public SuDeepWorldHook(SubsystemTerrain host) : base(host) { }

        public override void Attached()
        {
            // Source: UpdateExtensionProjectBinding.SynchronizeAttachments — first normal update follows complete Project loading.
            GameManager.ProjectDisposed += OnProjectDisposing;
            m_subscribed = true;
            if (!m_manager.TryBind(Host.Project))
                throw new InvalidOperationException("[SuUndergroundWorld] The loaded Project could not bind its required terrain pipeline.");
        }

        public override void Update(float dt)
        {
            if (!m_stopped) m_manager.Tick();
            // A vote permits the unique host call; never invoke Host.Update ourselves.
            base.Update(dt);
        }

        private void OnProjectDisposing(Project project)
        {
            if (!ReferenceEquals(project, Host.Project)) return;
            try { Stop(); }
            catch (Exception error)
            {
                // Source: GameManager.DisposeProject — callbacks run before native resources are disposed.
                // Report without preventing the game's remaining Project disposal handlers from running.
                ExceptionManager.ReportExceptionToUser("[SuUndergroundWorld] World shutdown failed.", error);
            }
        }

        private void Stop()
        {
            if (m_stopped) return;
            m_stopped = true;
            if (m_subscribed) GameManager.ProjectDisposed -= OnProjectDisposing;
            m_subscribed = false;
            m_manager.Shutdown();
        }

        public override void Detached() => Stop();
    }
}
