using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Engine;
using SuMultiplayer.Net;
using P = SuMultiplayer.Protocol;
using MessageType = SuMultiplayer.Protocol.MessageType;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// Host-authoritative sign table sync (SubsystemSignBlockBehavior.m_textsByPoint).
    ///
    /// Host:
    ///   · polls the sign table fingerprint every 500 ms; a change broadcasts a full
    ///     SignState to every online client (sign text is global world state, not
    ///     chunk-interest state);
    ///   · answers a joining client's SignSyncRequest with the current full snapshot;
    ///   · applies client SignEditRequest only after avatar/distance/sign-block checks.
    ///
    /// Client:
    ///   · keeps the last host snapshot; if the local sign table changed (the engine
    ///     sign dialog already wrote the replica), sends only the changed records;
    ///   · applies the host full snapshot over the local replica; never writes world
    ///     terrain and never invents records.
    /// </summary>
    public sealed class SignSyncModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const int PollMs = 500;
        private const int EditRetryMs = 1500;

        private readonly ModuleDescriptor m_descriptor;
        private readonly SignSyncEngine m_engine;
        private readonly List<SignRecordData> m_capture = new List<SignRecordData>(64);
        private readonly List<SignRecordData> m_known = new List<SignRecordData>(64);
        private readonly List<SignRecordData> m_edits = new List<SignRecordData>(16);
        private readonly List<PlayerPeerUser> m_peerScratch = new List<PlayerPeerUser>(8);
        private readonly List<int> m_onlineScratch = new List<int>(8);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private IPeerBroadcastPort m_broadcast;
        private bool m_attached;
        private long m_nextPollMs;
        private long m_nextEditMs;
        private int m_revision;
        private long m_hostFingerprint;
        private bool m_hasKnown;
        private bool m_wasConnected;
        private int m_lastSessionSerial = int.MinValue;
        private bool m_reportedCaptureFailure;
        private long m_nextFailureLogMs;

        public SignSyncModule(SignSyncEngine engine)
        {
            m_engine = engine ?? throw new ArgumentNullException(nameof(engine));
            m_descriptor = new ModuleDescriptor("sign-sync", "标牌板同步（主机权威）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "sign.sync" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;
        public string ModuleId => m_descriptor.Id;
        public bool IsAttached => m_attached;

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有传输端口 → 标牌同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("没有通道控制端口 → 标牌同步不启用");
                return;
            }
            context.TryGetPort(out m_broadcast);
            if (context.TryGetPort(out m_messages) && m_messages != null)
                m_messages.Registry.RegisterModule(this);
            m_transport.ClientJoined += OnClientJoined;
            m_attached = true;
            Log("标牌同步就绪：主机权威快照 / 客户端差异上传");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.Register((int)MessageType.SignState, P.SignState.Parser,
                new DelegateMessageHandler<P.SignState>(OnState), ModuleId);
            registry.Register((int)MessageType.SignSyncRequest, P.SignSyncRequest.Parser,
                new DelegateMessageHandler<P.SignSyncRequest>(OnSyncRequest), ModuleId);
            registry.Register((int)MessageType.SignEditRequest, P.SignEditRequest.Parser,
                new DelegateMessageHandler<P.SignEditRequest>(OnEditRequest), ModuleId);
            registry.Register((int)MessageType.SignEditResult, P.SignEditResult.Parser,
                new DelegateMessageHandler<P.SignEditResult>(OnEditResult), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            if (m_transport != null) m_transport.ClientJoined -= OnClientJoined;
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            m_attached = false;
            m_hasKnown = false;
            m_known.Clear();
            Log("标牌同步下线（" + reason + "）");
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_engine.Pump();
            bool connected = m_transport.IsConnected;
            if (m_wasConnected && !connected)
            {
                m_hasKnown = false;
                m_known.Clear();
            }
            m_wasConnected = connected;
            if (m_engine.SessionSerial != m_lastSessionSerial)
            {
                m_lastSessionSerial = m_engine.SessionSerial;
                m_hasKnown = false;
                m_known.Clear();
                m_nextEditMs = 0;
            }
            long nowMs = (long)(tick.Now * 1000.0);
            if (nowMs < m_nextPollMs) return;
            m_nextPollMs = nowMs + PollMs;
            if (nowMs < m_nextEditMs) return; // just sent an edit; wait for host state
            bool hosting = m_control.IsHosting;
            if (hosting) HostTick();
            else ClientTick();
        }

        private void HostTick()
        {
            if (!m_engine.TryCapture(m_capture, out long fingerprint, out string reason))
            {
                LogFailure("host-capture:" + ReasonCode(reason), "主机抓取标牌失败（" + reason + "）");
                return;
            }
            if (m_hasKnown && fingerprint == m_hostFingerprint) return;
            m_hasKnown = true;
            m_hostFingerprint = fingerprint;
            m_revision++;
            P.SignState state;
            if (!SignCodec.TryEncodeState(m_revision, true, "host-change", m_capture,
                    out state, out string encodeReason))
            {
                LogFailure("host-encode:" + ReasonCode(encodeReason),
                    "标牌快照编码失败（" + encodeReason + "）");
                return;
            }
            SendToAll(state.ToByteArray());
            Log("主机标牌表变化 → 广播 rev=" + m_revision + " signs=" + m_capture.Count);
        }

        private void ClientTick()
        {
            if (!m_transport.IsConnected) return;
            if (!m_engine.TryCapture(m_capture, out long fingerprint, out string reason))
            {
                LogFailure("client-capture:" + ReasonCode(reason), "客户端抓取标牌失败（" + reason + "）");
                return;
            }
            if (!m_hasKnown)
            {
                // Ask once after the world/sign subsystem is ready.
                var request = new P.SignSyncRequest { KnownRevision = m_revision, Reason = "join" };
                if (m_transport.TrySend(MessageType.SignSyncRequest, request.ToByteArray()))
                {
                    m_hasKnown = true;
                    m_nextEditMs = 0;
                    Log("客户端请求标牌全量同步");
                }
                return;
            }
            if (fingerprint == SignCodec.Hash(m_known)) return;
            BuildEdits(m_capture, m_known);
            if (m_edits.Count == 0)
            {
                m_known.Clear();
                m_known.AddRange(m_capture);
                return;
            }
            var edit = new P.SignEditRequest
            {
                Sequence = m_revision + 1,
                Reason = "client-edit"
            };
            for (int i = 0; i < m_edits.Count; i++) edit.Signs.Add(SignCodec.ToProto(m_edits[i]));
            if (!m_transport.TrySend(MessageType.SignEditRequest, edit.ToByteArray()))
            {
                LogFailure("edit-send", "标牌编辑请求发送失败 → 下个周期重试");
                return;
            }
            m_revision++;
            m_nextEditMs = (long)(Time.RealTime * 1000.0) + EditRetryMs;
            Log("客户端上传标牌编辑 signs=" + m_edits.Count + " seq=" + m_revision);
        }

        private void BuildEdits(IReadOnlyList<SignRecordData> local,
            IReadOnlyList<SignRecordData> known)
        {
            m_edits.Clear();
            var knownByKey = new Dictionary<string, SignRecordData>(StringComparer.Ordinal);
            if (known != null)
                for (int i = 0; i < known.Count; i++)
                    if (known[i] != null) knownByKey[Key(known[i])] = known[i];
            if (local != null)
                for (int i = 0; i < local.Count; i++)
                {
                    SignRecordData sign = local[i];
                    if (sign == null) continue;
                    if (!knownByKey.TryGetValue(Key(sign), out SignRecordData old) ||
                        !sign.SameContent(old))
                        m_edits.Add(sign);
                }
        }

        private static string Key(SignRecordData sign) =>
            sign.X.ToString() + "," + sign.Y.ToString() + "," + sign.Z.ToString();

        private void OnClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!m_control.IsHosting || m_transport == null) return;
            if (!m_engine.TryCapture(m_capture, out long fingerprint, out string reason))
            {
                LogFailure("join-capture:" + ReasonCode(reason), "新客户端加入时抓取标牌失败：" + reason);
                return;
            }
            m_hostFingerprint = fingerprint;
            m_hasKnown = true;
            if (m_revision <= 0) m_revision = 1;
            if (!SignCodec.TryEncodeState(m_revision, true, "join", m_capture,
                    out P.SignState state, out _)) return;
            m_transport.TrySendTo(clientId, MessageType.SignState, state.ToByteArray());
            Log("向 clientId=" + clientId + " 发送标牌全量 signs=" + m_capture.Count);
        }

        private void OnSyncRequest(MessageContext context, P.SignSyncRequest message)
        {
            if (!m_control.IsHosting) return;
            if (context.ClientId <= 0) return;
            if (!m_engine.TryCapture(m_capture, out long fingerprint, out string reason))
            {
                LogFailure("sync-capture:" + ReasonCode(reason), "应答标牌同步时抓取失败：" + reason);
                return;
            }
            m_hostFingerprint = fingerprint;
            m_hasKnown = true;
            if (m_revision <= 0) m_revision = 1;
            if (!SignCodec.TryEncodeState(m_revision, true, "request", m_capture,
                    out P.SignState state, out string encodeReason))
                return;
            m_transport?.TrySendTo(context.ClientId, MessageType.SignState, state.ToByteArray());
            Log("向 clientId=" + context.ClientId + " 发送标牌全量 signs=" + m_capture.Count);
        }

        private void OnState(MessageContext context, P.SignState message)
        {
            if (m_control.IsHosting) return;
            var received = new List<SignRecordData>(message.Signs.Count);
            if (!SignCodec.TryDecodeState(message, received, out int revision, out bool full,
                    out string reason))
            {
                LogFailure("state-decode:" + ReasonCode(reason), "标牌快照非法（" + reason + "）");
                return;
            }
            if (!m_engine.TryApplyHostState(received, out string applyReason))
            {
                LogFailure("state-apply:" + ReasonCode(applyReason),
                    "应用主机标牌快照失败（" + applyReason + "）");
                return;
            }
            m_known.Clear();
            m_known.AddRange(received);
            m_hasKnown = true;
            m_revision = revision;
            m_nextEditMs = (long)(Time.RealTime * 1000.0) + EditRetryMs;
            Log("客户端应用标牌快照 rev=" + revision + " signs=" + received.Count);
        }

        private void OnEditRequest(MessageContext context, P.SignEditRequest message)
        {
            if (!m_control.IsHosting) return;
            int clientId = context.ClientId;
            var edits = new List<SignRecordData>(message.Signs.Count);
            for (int i = 0; i < message.Signs.Count; i++)
            {
                if (!SignCodec.TryFromProto(message.Signs[i], out SignRecordData record,
                        out string reason))
                {
                    SendEditResult(clientId, message.Sequence, false, reason);
                    return;
                }
                edits.Add(record);
            }
            if (!m_engine.TryApplyClientEdits(clientId, edits, out string applyReason))
            {
                SendEditResult(clientId, message.Sequence, false, applyReason);
                LogFailure("edit-rejected:" + ReasonCode(applyReason),
                    "客户端标牌编辑被拒绝（" + applyReason + "）");
                return;
            }
            SendEditResult(clientId, message.Sequence, true, string.Empty);
            HostTick();
        }

        private void OnEditResult(MessageContext context, P.SignEditResult message)
        {
            if (m_control.IsHosting) return;
            m_nextEditMs = 0;
            if (!message.Accepted)
                LogFailure("edit-result:" + ReasonCode(message.Reason),
                    "标牌编辑被主机拒绝：" + message.Reason);
        }

        private void SendEditResult(int clientId, long sequence, bool accepted, string reason)
        {
            if (m_transport == null || clientId <= 0) return;
            var result = new P.SignEditResult
            {
                Sequence = sequence,
                Accepted = accepted,
                Reason = reason ?? string.Empty
            };
            m_transport.TrySendTo(clientId, MessageType.SignEditResult, result.ToByteArray());
        }

        private void SendToAll(byte[] payload)
        {
            if (m_transport == null || payload == null) return;
            var players = new List<int>(8);
            if (m_broadcast != null)
            {
                m_peerScratch.Clear();
                m_broadcast.CapturePlayerUsers(m_peerScratch);
                for (int i = 0; i < m_peerScratch.Count; i++)
                    if (m_peerScratch[i].ClientId > 0) players.Add(m_peerScratch[i].ClientId);
            }
            for (int i = 0; i < players.Count; i++)
                m_transport.TrySendTo(players[i], MessageType.SignState, payload);
        }

        private static string ReasonCode(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "none";
            int colon = reason.IndexOf(':');
            return colon > 0 ? reason.Substring(0, colon) : reason;
        }

        private static void Log(string message) => ModLog.Info("[SuMP][sign-sync] " + message);

        private void LogFailure(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (nowMs < m_nextFailureLogMs) return;
            m_nextFailureLogMs = nowMs + 5000;
            ModLog.Warn("[SuMP][sign-sync][" + key + "] " + message);
        }
    }
}
