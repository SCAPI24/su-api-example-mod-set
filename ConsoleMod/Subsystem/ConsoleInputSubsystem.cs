using System;
using System.Collections.Generic;
using System.Linq;
using Engine;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace ConsoleMod
{
    // Source: ConsoleSubsystemGameWidgets.cs:original UpdateOrder/Update; SubsystemUpdate.cs:Load/AddUpdateable.
    // This is Console's own input task; it neither replaces nor updates the real Views subsystem.
    public sealed class ConsoleInputSubsystem : Subsystem
    {
        // Source: SubsystemUpdate.cs:AddUpdateable/RemoveUpdateable — each activation gets a distinct scheduling identity.
        private sealed class ConsoleEarlyUpdate : IUpdateable
        {
            private readonly ConsoleInputSubsystem m_owner;
            internal bool Active = true;
            internal ConsoleEarlyUpdate(ConsoleInputSubsystem owner) { m_owner = owner; }
            public UpdateOrder UpdateOrder => UpdateOrder.Reset; // -100, before Input(-10).
            public void Update(float dt)
            {
                if (Active) m_owner.UpdateInput(this, dt);
            }
        }

        private static readonly HashSet<ConsoleInputSubsystem> s_live = new HashSet<ConsoleInputSubsystem>();
        private static bool s_moduleEnabled;
        private SubsystemUpdate m_scheduler;
        private SubsystemGameWidgets m_host;
        private ConsoleSubsystemGameWidgets m_controller;
        private ConsoleEarlyUpdate m_inputTask;
        private bool m_running;
        private bool m_disposed;
        private Exception m_inputFailure;

        internal static void EnableModule()
        {
            if (s_moduleEnabled) throw new InvalidOperationException("[ConsoleMod] The input module is already enabled.");
            s_moduleEnabled = true;
            try
            {
                foreach (ConsoleInputSubsystem input in s_live.ToArray()) input.Start();
            }
            catch (Exception originalError)
            {
                try { DisableModule(); }
                catch (Exception cleanupError) { throw new AggregateException(originalError, cleanupError); }
                throw;
            }
        }

        internal static void DisableModule()
        {
            s_moduleEnabled = false;
            var errors = new List<Exception>();
            foreach (ConsoleInputSubsystem input in s_live.ToArray())
            {
                try { input.Stop(); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count != 0) throw new AggregateException("[ConsoleMod] Failed to release input controllers.", errors);
        }

        protected override void Load(ValuesDictionary valuesDictionary)
        {
            m_scheduler = Project.FindSubsystem<SubsystemUpdate>(true);
            m_host = Project.FindSubsystem<SubsystemGameWidgets>(true);
            s_live.Add(this);
            if (s_moduleEnabled) Start();
            // Existing metadata can outlive a Mod unload; no input task is registered while the module is disabled.
        }

        private void Start()
        {
            if (m_running || m_disposed) return;
            if (m_host.GetType() != typeof(SubsystemGameWidgets))
            {
                Log.Warning("[ConsoleMod] Console input is inactive: Views was replaced by '" + m_host.GetType().FullName + "'.");
                return;
            }
            m_controller = new ConsoleSubsystemGameWidgets(m_host);
            m_inputFailure = null;
            m_inputTask = new ConsoleEarlyUpdate(this);
            m_running = true;
            // Source: Project.LoadSubsystem — register after dependencies load, before the first update queue is consumed.
            m_scheduler.AddUpdateable(m_inputTask);
        }

        internal ConsoleSubsystemGameWidgets GetController(SubsystemGameWidgets host)
        {
            if (m_inputFailure != null)
                throw new InvalidOperationException("[ConsoleMod] Early input initialization/update failed before the Views attachment.", m_inputFailure);
            if (!m_running || m_controller == null || !ReferenceEquals(host, m_host))
                throw new InvalidOperationException("[ConsoleMod] The shared Views controller was requested before its input task was initialized.");
            return m_controller;
        }

        private void UpdateInput(ConsoleEarlyUpdate inputTask, float dt)
        {
            if (!m_running || !ReferenceEquals(inputTask, m_inputTask)) return;
            try { m_controller.UpdateInput(dt); }
            catch (Exception ex)
            {
                m_running = false;
                m_inputFailure = ex;
                inputTask.Active = false;
                m_scheduler.RemoveUpdateable(inputTask);
                // Source: ConsoleSubsystemGameWidgets.Dispose: input failures must immediately
                // release this controller's open-state ownership, UI and keyboard session.
                ConsoleSubsystemGameWidgets controller = m_controller;
                m_controller = null;
                Exception reportedError = ex;
                try { controller?.Dispose(); }
                catch (Exception cleanupError)
                {
                    reportedError = new AggregateException("Console input and controller cleanup both failed.", ex, cleanupError);
                    m_inputFailure = reportedError;
                }
                Log.Error("[ConsoleMod] Early input update failed: " + reportedError);
                // Source: SubsystemUpdate.cs:Update — its catch is empty; report before crossing that boundary.
                ExceptionManager.ReportExceptionToUser("Console input update failed.", reportedError);
            }
        }

        private void Stop()
        {
            m_running = false;
            if (m_inputTask != null)
            {
                m_inputTask.Active = false;
                m_scheduler.RemoveUpdateable(m_inputTask);
                m_inputTask = null;
            }
            ConsoleSubsystemGameWidgets controller = m_controller;
            m_controller = null;
            controller?.Dispose();
        }

        public override void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            try { Stop(); }
            finally { s_live.Remove(this); }
        }
    }
}
