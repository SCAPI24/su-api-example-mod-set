namespace ScMultiplayer.Core
{
    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:6.2 状态所有权
    // Only stable, game-independent handles belong here. Concrete game dependencies stay in
    // adapters and are added only when a real module needs them.
    internal sealed class MultiplayerContext
    {
        public MultiplayerContext(global::ScMultiplayer.Ports.IMultiplayerRuntimeHost runtime,
            global::ScMultiplayer.Ports.IDiagnosticSink diagnosticSink)
        {
            Session = new MultiplayerSessionState();
            Connections = new PlayerConnectionRegistry();
            StateOwners = new RuntimeStateOwnerRegistry();
            Diagnostics = new global::ScMultiplayer.Diagnostics.DiagnosticRecorder();
            IngressDiagnostics = new global::ScMultiplayer.Diagnostics
                .NetworkIngressDiagnosticsCollector();
            DiagnosticSink = diagnosticSink;
            Runtime = runtime;
        }

        public MultiplayerSessionState Session { get; }

        public PlayerConnectionRegistry Connections { get; }

        public RuntimeStateOwnerRegistry StateOwners { get; }

        public global::ScMultiplayer.Diagnostics.DiagnosticRecorder Diagnostics { get; }

        public global::ScMultiplayer.Diagnostics.NetworkIngressDiagnosticsCollector
            IngressDiagnostics { get; }

        public global::ScMultiplayer.Ports.IDiagnosticSink DiagnosticSink { get; }

        public global::ScMultiplayer.Ports.IMultiplayerRuntimeHost Runtime { get; }
    }
}
