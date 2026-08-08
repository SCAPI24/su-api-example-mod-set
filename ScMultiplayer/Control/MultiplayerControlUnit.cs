using ScMultiplayer.Core;
using ScMultiplayer.Diagnostics;
using ScMultiplayer.Modules.Runtime;
using ScMultiplayer.Modules.Session;
using ScMultiplayer.Modules.Diagnostics;
using ScMultiplayer.Ports;

namespace ScMultiplayer.Control
{
    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:4 控制单元职责
    // This is the Mod-side composition root. It intentionally has no Engine, Game or Comms
    // dependency until a concrete module is migrated behind a port.
    internal sealed class MultiplayerControlUnit : System.IDisposable
    {
        private readonly MultiplayerContext m_context;
        private readonly ModuleScheduler m_scheduler = new ModuleScheduler();
        private readonly SessionStateModule m_sessionModule = new SessionStateModule();
        private readonly PlayerConnectionRegistryModule m_connectionRegistryModule =
            new PlayerConnectionRegistryModule();
        private readonly SessionRuntimeModule m_sessionRuntimeModule = new SessionRuntimeModule();
        private readonly JoinTransferModule m_joinTransferModule = new JoinTransferModule();
        private readonly WorldControlModule m_worldControlModule = new WorldControlModule();
        private readonly CircuitRuntimeModule m_circuitRuntimeModule = new CircuitRuntimeModule();
        private readonly WorldRuntimeModule m_worldRuntimeModule = new WorldRuntimeModule();
        private readonly PlayerRuntimeModule m_playerRuntimeModule = new PlayerRuntimeModule();
        private readonly EntityRuntimeModule m_entityRuntimeModule = new EntityRuntimeModule();
        private readonly UiRuntimeModule m_uiRuntimeModule = new UiRuntimeModule();
        private readonly DiagnosticsModule m_diagnosticsModule = new DiagnosticsModule();
        private long m_frameIndex;
        private bool m_initialized;

        public MultiplayerControlUnit(IMultiplayerRuntimeHost runtime, IDiagnosticSink diagnosticSink)
        {
            m_context = new MultiplayerContext(runtime, diagnosticSink);
        }

        public MultiplayerContext Context => m_context;

        public ModuleScheduler Scheduler => m_scheduler;

        public void Initialize()
        {
            if (m_initialized)
                return;
            m_scheduler.Register(m_sessionModule);
            m_scheduler.Register(m_connectionRegistryModule);
            m_scheduler.Register(m_sessionRuntimeModule);
            m_scheduler.Register(m_joinTransferModule);
            m_scheduler.Register(m_worldControlModule);
            m_scheduler.Register(m_circuitRuntimeModule);
            m_scheduler.Register(m_worldRuntimeModule);
            m_scheduler.Register(m_playerRuntimeModule);
            m_scheduler.Register(m_entityRuntimeModule);
            m_scheduler.Register(m_uiRuntimeModule);
            m_scheduler.Register(m_diagnosticsModule);
            m_scheduler.Initialize(m_context);
            m_initialized = true;
        }

        public void Tick(float deltaTime, double now, bool isHost, bool isConnected)
        {
            Initialize();
            ModuleTickContext tickContext = new ModuleTickContext(
                ++m_frameIndex,
                now,
                deltaTime,
                isHost,
                isConnected,
                messageBudget: 0,
                timeBudgetMicroseconds: 0);
            m_scheduler.Tick(in tickContext);
        }

        public void Reset(ModuleResetReason reason)
        {
            m_scheduler.Reset(reason);
            m_context.Session.Reset();
            m_context.StateOwners.ResetAll();
        }

        public DiagnosticRecorder Diagnostics => m_context.Diagnostics;

        public int FlushDiagnostics(int maximumRecords = 24) =>
            m_diagnosticsModule.FlushAfterApply(maximumRecords);

        public void Dispose()
        {
            if (!m_initialized)
                return;
            Reset(ModuleResetReason.ModUnloaded);
            m_scheduler.Dispose();
            m_initialized = false;
        }
    }
}
