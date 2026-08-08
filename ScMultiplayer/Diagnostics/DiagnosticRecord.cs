using Comms;
using System;

namespace ScMultiplayer.Diagnostics
{
    internal enum DiagnosticRecordKind
    {
        Audit = 0,
        Retransmit = 1,
        RouterError = 2,
        QueueDrop = 3,
        IngressSummary = 4
    }

    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:104 异常隔离与可观测性
    // Records are immutable and contain only correlation data. Formatting and disk I/O happen
    // after the game-thread apply phase, never from a transport callback.
    internal readonly struct DiagnosticRecord
    {
        private DiagnosticRecord(DiagnosticRecordKind kind, string eventName, int clientId,
            string playerName, string details, ReliableRetransmitInfo retransmit,
            NetworkIngressMetricsSnapshot ingressMetrics)
        {
            Kind = kind;
            EventName = eventName ?? string.Empty;
            ClientId = clientId;
            PlayerName = playerName ?? string.Empty;
            Details = details ?? string.Empty;
            Retransmit = retransmit;
            IngressMetrics = ingressMetrics;
        }

        public DiagnosticRecordKind Kind { get; }

        public string EventName { get; }

        public int ClientId { get; }

        public string PlayerName { get; }

        public string Details { get; }

        public ReliableRetransmitInfo Retransmit { get; }

        public NetworkIngressMetricsSnapshot IngressMetrics { get; }

        public static DiagnosticRecord Audit(string eventName, int clientId,
            string playerName, string details) =>
            new DiagnosticRecord(DiagnosticRecordKind.Audit, eventName, clientId,
                playerName, details, default, default);

        public static DiagnosticRecord Retransmission(ReliableRetransmitInfo info) =>
            new DiagnosticRecord(DiagnosticRecordKind.Retransmit, string.Empty, 0,
                string.Empty, string.Empty, info, default);

        public static DiagnosticRecord RouterFailure(string details, int clientId) =>
            new DiagnosticRecord(DiagnosticRecordKind.RouterError, "router.error", clientId,
                string.Empty, details, default, default);

        public static DiagnosticRecord IngressSummary(
            NetworkIngressMetricsSnapshot snapshot) =>
            new DiagnosticRecord(DiagnosticRecordKind.IngressSummary, "ingress.summary", 0,
                string.Empty, string.Empty, default, snapshot);
    }
}
