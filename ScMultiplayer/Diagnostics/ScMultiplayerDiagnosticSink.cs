using Comms;
using Engine;
using System.Globalization;
using ScMultiplayer.Ports;

namespace ScMultiplayer
{
    public partial class ScMultiplayer : IDiagnosticSink
    {
        internal void RecordRouterFailure(int clientId, string details)
        {
            Log.Error("[ScMP] " + details);
            m_controlUnit?.Diagnostics.TryRecord(
                Diagnostics.DiagnosticRecord.RouterFailure(details, clientId));
        }

        // Source: Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerUpdateLoop.cs:
        // PublishServerAudit / FlushReliableRetransmitRecords
        // Formatting is bounded to the post-Apply drain and preserves the existing Headless event
        // names, so older HeadlessRenderingMod versions keep receiving the same log records.
        void IDiagnosticSink.Consume(in Diagnostics.DiagnosticRecord record)
        {
            if (!IsHost || m_eventBus == null)
                return;

            if (record.Kind == Diagnostics.DiagnosticRecordKind.Retransmit)
            {
                ReliableRetransmitInfo info = record.Retransmit;
                string content = Message.DescribeRetransmission(info.Payload);
                string value = "endpoint=\"" + NormalizeServerAuditValue(
                    info.Address?.ToString(), 96) + "\" packet=" +
                    info.PacketId.ToString(CultureInfo.InvariantCulture) +
                    " retry=" + info.RetryNumber.ToString(CultureInfo.InvariantCulture) +
                    " bytes=" + info.Bytes.ToString(CultureInfo.InvariantCulture) +
                    " source=" + NormalizeServerAuditValue(info.Source, 96) +
                    " " + content;
                m_eventBus.TriggerEvent(ServerRetransmitAuditEventName,
                    new object[] { value });
                return;
            }

            if (record.Kind == Diagnostics.DiagnosticRecordKind.IngressSummary)
            {
                Diagnostics.NetworkIngressMetricsSnapshot metrics = record.IngressMetrics;
                string value = "event=ingress.summary windowMs=" +
                    metrics.WindowMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    " receive=" + metrics.Received.ToString(CultureInfo.InvariantCulture) +
                    " bytes=" + metrics.PayloadBytes.ToString(CultureInfo.InvariantCulture) +
                    " enqueue=" + metrics.Enqueued.ToString(CultureInfo.InvariantCulture) +
                    " apply=" + metrics.Applied.ToString(CultureInfo.InvariantCulture) +
                    " ok=" + metrics.Succeeded.ToString(CultureInfo.InvariantCulture) +
                    " fail=" + metrics.Failed.ToString(CultureInfo.InvariantCulture) +
                    " rxToQueueP95Ms=" + metrics.ReceiveToEnqueueP95Milliseconds
                        .ToString("0.###", CultureInfo.InvariantCulture) +
                    " queueP95Ms=" + metrics.QueueP95Milliseconds
                        .ToString("0.###", CultureInfo.InvariantCulture) +
                    " applyP95Ms=" + metrics.ApplyP95Milliseconds
                        .ToString("0.###", CultureInfo.InvariantCulture) +
                    " top=" + metrics.TopKind +
                    " topCount=" + metrics.TopCount.ToString(CultureInfo.InvariantCulture);
                m_eventBus.TriggerEvent(ServerAuditEventName, new object[] { value });
                return;
            }

            string audit = "event=" + NormalizeServerAuditValue(record.EventName, 48) +
                " client=" + record.ClientId.ToString(CultureInfo.InvariantCulture) +
                " player=\"" + NormalizeServerAuditValue(record.PlayerName, 64) + "\"";
            if (!string.IsNullOrWhiteSpace(record.Details))
                audit += " " + NormalizeServerAuditValue(record.Details, 256);
            m_eventBus.TriggerEvent(ServerAuditEventName, new object[] { audit });
        }

        void IDiagnosticSink.ConsumeDrop(Diagnostics.DiagnosticRecordKind kind, long count)
        {
            if (!IsHost || m_eventBus == null || count <= 0)
                return;
            string name = kind == Diagnostics.DiagnosticRecordKind.Retransmit
                ? ServerRetransmitAuditEventName
                : ServerAuditEventName;
            string prefix = kind == Diagnostics.DiagnosticRecordKind.Retransmit
                ? "event=retransmit.queue_drop count="
                : "event=diagnostic.queue_drop kind=" + kind + " count=";
            m_eventBus.TriggerEvent(name, new object[]
            {
                prefix + count.ToString(CultureInfo.InvariantCulture)
            });
        }
    }
}
