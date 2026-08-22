using Comms;
using Comms.Drt;
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
            if (!IsHost || !ScMultiplayerSettings.ServerDiagnosticsEnabled)
                return;
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
                EmitServerAudit(ServerRetransmitAuditEventName, value,
                    allowGameLogFallback: false);
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
                EmitServerAudit(ServerAuditEventName, value,
                    allowGameLogFallback: true);
                return;
            }

            if (record.Kind == Diagnostics.DiagnosticRecordKind.JoinTrace)
            {
                JoinDiagnosticData data = record.JoinDiagnostic;
                string value = "event=join.transport stage=" +
                    NormalizeServerAuditValue(data.StageName, 32) +
                    " game=" + data.GameID.ToString(CultureInfo.InvariantCulture) +
                    " client=" + data.ClientID.ToString(CultureInfo.InvariantCulture) +
                    " endpoint=\"" + NormalizeServerAuditValue(
                        data.Address?.ToString(), 96) + "\"" +
                    " player=\"" + NormalizeServerAuditValue(data.ClientName, 64) + "\"" +
                    " bytes=" + data.PayloadBytes.ToString(CultureInfo.InvariantCulture) +
                    " queue=" + data.QueueCount.ToString(CultureInfo.InvariantCulture) +
                    " tick=" + data.Tick.ToString(CultureInfo.InvariantCulture) +
                    " step=" + data.Step.ToString(CultureInfo.InvariantCulture) +
                    " stateBytes=" + data.StateBytes.ToString(CultureInfo.InvariantCulture) +
                    " tickMessages=" + data.TickMessages.ToString(CultureInfo.InvariantCulture);
                EmitServerAudit(ServerAuditEventName, value,
                    allowGameLogFallback: true);
                return;
            }

            string audit = "event=" + NormalizeServerAuditValue(record.EventName, 48) +
                " client=" + record.ClientId.ToString(CultureInfo.InvariantCulture) +
                " player=\"" + NormalizeServerAuditValue(record.PlayerName, 64) + "\"";
            if (!string.IsNullOrWhiteSpace(record.Details))
                audit += " " + NormalizeServerAuditValue(record.Details, 256);
            EmitServerAudit(ServerAuditEventName, audit,
                allowGameLogFallback: true);
        }

        // Source: Mod/Comms/Comms.Drt/Func/Server/Server.cs:Server.JoinDiagnostic
        // The Comms alarm thread performs only a bounded non-blocking enqueue.
        private void HandleServerJoinDiagnostic(JoinDiagnosticData data)
        {
            if (!IsHost || !ScMultiplayerSettings.ServerDiagnosticsEnabled)
                return;
            m_controlUnit?.Diagnostics.TryRecord(Diagnostics.DiagnosticRecord.Join(data));
        }

        // Source: EntitySystem/SuAPI/ModEventBus.cs:ModEventBus.TriggerEvent
        // Headless owns packet-level file I/O. A normal host falls back to Game.log only for
        // low-frequency records; retransmit detail remains summarized by network.summary.
        private void EmitServerAudit(string eventName, string value,
            bool allowGameLogFallback)
        {
            object[][] results = m_eventBus.TriggerEvent(eventName, new object[] { value });
            if (allowGameLogFallback && results.Length == 0)
                Log.Information("[ScMP][ServerAudit] " + value);
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
            EmitServerAudit(name,
                prefix + count.ToString(CultureInfo.InvariantCulture),
                allowGameLogFallback: true);
        }

        // Source: Mod/ScMultiplayer/Func/Server/ScMultiplayerSettings.cs:
        // ScMultiplayerSettings.ServerDiagnosticsEnabled
        private void ApplyServerDiagnosticsSetting()
        {
            bool previousEnabled = m_controlUnit?.Diagnostics.Enabled == true;
            bool enabled = ScMultiplayerSettings.ServerDiagnosticsEnabled && IsHost;
            if (previousEnabled && !enabled && IsHost)
                EnqueueServerAudit("diagnostic.disabled", 0, FormatDiagnosticIdentity());

            if (m_controlUnit != null)
            {
                m_controlUnit.Diagnostics.Enabled = enabled;
                m_controlUnit.Context.IngressDiagnostics.Enabled = enabled;
            }
            if (server != null)
                server.JoinDiagnosticsEnabled = enabled;

            if (!previousEnabled && enabled && IsHost)
                PublishServerSystemAudit("diagnostic.enabled", FormatDiagnosticIdentity());
        }

        private static string FormatDiagnosticIdentity()
        {
            string protocol = Message.ProtocolHash.Length > 12
                ? Message.ProtocolHash.Substring(0, 12)
                : Message.ProtocolHash;
            string build = Message.BuildFingerprint.Length > 12
                ? Message.BuildFingerprint.Substring(0, 12)
                : Message.BuildFingerprint;
            return "mod=" + Message.ModVersion +
                " protocol=" + Message.ProtocolVersion.ToString(CultureInfo.InvariantCulture) +
                "/" + protocol + " build=" + build;
        }
    }
}
