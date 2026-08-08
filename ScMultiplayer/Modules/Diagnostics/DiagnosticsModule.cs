using ScMultiplayer.Core;
using ScMultiplayer.Diagnostics;
using ScMultiplayer.Ports;

namespace ScMultiplayer.Modules.Diagnostics
{
    // Source: Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerUpdateLoop.cs:
    // FlushReliableRetransmitRecords
    // Diagnostics is a scheduler-owned module, but its flush is explicitly called after Apply.
    internal sealed class DiagnosticsModule : IMultiplayerModule
    {
        private DiagnosticRecorder m_recorder;
        private NetworkIngressDiagnosticsCollector m_ingressDiagnostics;
        private IDiagnosticSink m_sink;

        public string Name => "Diagnostics";

        public RuntimeStateDomain StateDomain => RuntimeStateDomain.Diagnostics;

        public void Initialize(MultiplayerContext context)
        {
            m_recorder = context?.Diagnostics;
            m_ingressDiagnostics = context?.IngressDiagnostics;
            m_sink = context?.DiagnosticSink;
        }

        public void Tick(in ModuleTickContext tickContext)
        {
            // FlushAfterApply owns the timing. Keeping Tick empty prevents diagnostics from
            // running before the normal frame queues have finished applying.
        }

        public int FlushAfterApply(int maximumRecords)
        {
            if (m_recorder == null || m_sink == null)
                return 0;
            if (m_ingressDiagnostics != null &&
                m_ingressDiagnostics.TryTakeSnapshot(System.Diagnostics.Stopwatch.GetTimestamp(),
                    out NetworkIngressMetricsSnapshot ingressSnapshot))
                m_recorder.TryRecord(DiagnosticRecord.IngressSummary(ingressSnapshot));
            int processed = m_recorder.Drain(record => m_sink.Consume(in record),
                maximumRecords);
            for (int i = 0; i <= (int)DiagnosticRecordKind.IngressSummary; i++)
            {
                DiagnosticRecordKind kind = (DiagnosticRecordKind)i;
                long dropped = m_recorder.TakeDropped(kind);
                if (dropped > 0)
                    m_sink.ConsumeDrop(kind, dropped);
            }
            return processed;
        }

        public void Reset(ModuleResetReason reason)
        {
            m_recorder?.Reset();
            m_ingressDiagnostics?.Reset();
        }

        public void Dispose()
        {
            m_recorder = null;
            m_ingressDiagnostics = null;
            m_sink = null;
        }
    }
}
