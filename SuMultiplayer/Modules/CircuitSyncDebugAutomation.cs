using System;
using System.Collections.Generic;

namespace SuMultiplayer.Modules
{
    public sealed class CircuitSyncDebugAutomationException : Exception
    {
        public CircuitSyncDebugAutomationException(string code, string message)
            : base(message)
        {
            Code = string.IsNullOrEmpty(code) ? "circuit_debug_failed" : code;
        }

        public string Code { get; }
    }

    /// <summary>
    /// CmdBridge 调试入口。只改变 CircuitSync 的同步暂停信号，不修改游戏时间或电路求解器。
    /// </summary>
    public sealed class CircuitSyncDebugAutomation
    {
        private readonly CircuitSyncModule m_module;

        internal CircuitSyncDebugAutomation(CircuitSyncModule module)
        {
            m_module = module ?? throw new ArgumentNullException(nameof(module));
        }

        public bool IsHosting => m_module.IsHosting;

        public bool IsPaused => m_module.DebugPauseRequested;

        public string StatusLine => m_module.StatusLine();

        public void SetPaused(bool paused)
        {
            if (!m_module.TrySetDebugPause(paused, out string error))
                throw new CircuitSyncDebugAutomationException(
                    m_module.IsHosting ? "circuit_debug_unavailable" : "circuit_debug_host_only",
                    string.IsNullOrEmpty(error) ? "Circuit debug pause request failed." : error);
        }

        public Dictionary<string, object> Status()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ok"] = true,
                ["hosting"] = IsHosting,
                ["paused"] = IsPaused,
                ["status"] = StatusLine,
                ["note"] = "debug pause only affects circuit synchronization; game time is unchanged"
            };
        }
    }
}
