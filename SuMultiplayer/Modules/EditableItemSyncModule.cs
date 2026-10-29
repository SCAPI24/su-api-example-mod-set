using System;
using System.Collections.Generic;
using Engine;
using Game;
using Google.Protobuf;
using SuMultiplayer.Engine;
using SuMultiplayer.Net;
using P = SuMultiplayer.Protocol;
using MessageType = SuMultiplayer.Protocol.MessageType;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// Host-authoritative SubsystemEditableItemBehavior.m_itemsData sync
    /// (truth-table / memory-bank item payloads). Block payloads remain in
    /// EditableSyncModule; this module only carries the item registry.
    /// </summary>
    public sealed class EditableItemSyncModule : IMultiplayerModule, ISuMPMessageModule,
        IEditableItemRequestSink
    {
        private const int PollMs = 1000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly EditableItemBridge m_bridge;
        private readonly List<EditableItemBridge.ItemEntry> m_capture =
            new List<EditableItemBridge.ItemEntry>(64);
        private readonly List<EditableItemBridge.ItemEntry> m_known =
            new List<EditableItemBridge.ItemEntry>(64);
        private readonly List<P.EditableItemEntry> m_payloadEntries =
            new List<P.EditableItemEntry>(64);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private IPeerBroadcastPort m_broadcast;
        private bool m_attached;
        private bool m_hasKnown;
        private bool m_wasConnected;
        private int m_lastSessionSerial = int.MinValue;
        private long m_fingerprint;
        private long m_nextPollMs;
        private int m_revision;
        private long m_pendingSequence;
        private int m_pendingSlot = -1;
        private int m_pendingExpectedValue;
        private int m_pendingKind;

        public EditableItemSyncModule(EditableItemBridge bridge)
        {
            m_bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            m_descriptor = new ModuleDescriptor("editable-item-sync",
                "真值表 / 内存条物品内容同步（主机权威）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "editable.item.sync" },
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
                Log("没有传输端口 → 物品内容同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("没有通道控制端口 → 物品内容同步不启用");
                return;
            }
            context.TryGetPort(out m_broadcast);
            m_bridge.SetRequestSink(this);
            if (context.TryGetPort(out m_messages) && m_messages != null)
                m_messages.Registry.RegisterModule(this);
            m_transport.ClientJoined += OnClientJoined;
            m_attached = true;
            Log("物品内容同步就绪（真值表 / 内存条 m_itemsData）");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.Register((int)MessageType.EditableItemState,
                P.EditableItemState.Parser,
                new DelegateMessageHandler<P.EditableItemState>(OnState), ModuleId);
            registry.Register((int)MessageType.EditableItemSyncRequest,
                P.EditableItemSyncRequest.Parser,
                new DelegateMessageHandler<P.EditableItemSyncRequest>(OnSyncRequest), ModuleId);
            registry.Register((int)MessageType.EditableItemEditRequest,
                P.EditableItemEditRequest.Parser,
                new DelegateMessageHandler<P.EditableItemEditRequest>(OnItemEditRequest), ModuleId);
            registry.Register((int)MessageType.EditableItemEditResult,
                P.EditableItemEditResult.Parser,
                new DelegateMessageHandler<P.EditableItemEditResult>(OnItemEditResult), ModuleId);
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
            m_capture.Clear();
            m_known.Clear();
            Log("物品内容同步下线（" + reason + "）");
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            bool connected = m_transport.IsConnected;
            if (m_wasConnected && !connected) m_hasKnown = false;
            m_wasConnected = connected;
            if (m_bridge.SessionSerial != m_lastSessionSerial)
            {
                m_lastSessionSerial = m_bridge.SessionSerial;
                m_hasKnown = false;
            }
            long nowMs = (long)(tick.Now * 1000.0);
            if (nowMs < m_nextPollMs) return;
            m_nextPollMs = nowMs + PollMs;
            bool hosting = m_control.IsHosting;
            if (hosting) HostTick();
            else ClientTick();
        }

        private void HostTick()
        {
            if (!m_bridge.TryCapture(m_capture, out long fingerprint, out string reason))
            {
                LogFailure("host-capture:" + ReasonCode(reason), "主机抓取物品内容失败：" + reason);
                return;
            }
            if (m_hasKnown && fingerprint == m_fingerprint) return;
            m_hasKnown = true;
            m_fingerprint = fingerprint;
            m_revision++;
            BroadcastFull();
        }

        private void ClientTick()
        {
            if (!m_transport.IsConnected) return;
            if (m_hasKnown)
            {
                // Host changes arrive as full states; no local upload path yet.
                return;
            }
            if (!m_bridge.TryCapture(m_capture, out _, out _)) return;
            var request = new P.EditableItemSyncRequest
            {
                KnownRevision = m_revision,
                Reason = "join"
            };
            if (m_transport.TrySend(MessageType.EditableItemSyncRequest,
                    request.ToByteArray()))
            {
                m_hasKnown = true;
                Log("客户端请求物品内容全量");
            }
        }

        private void OnClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!m_control.IsHosting || m_transport == null || clientId <= 0) return;
            if (!m_bridge.TryCapture(m_capture, out long fingerprint, out string reason))
            {
                LogFailure("join-capture:" + ReasonCode(reason), "新客户端加入时抓取物品内容失败：" + reason);
                return;
            }
            m_fingerprint = fingerprint;
            m_hasKnown = true;
            if (m_revision <= 0) m_revision = 1;
            SendFullTo(clientId, "join");
        }

        private void OnSyncRequest(MessageContext context, P.EditableItemSyncRequest message)
        {
            if (!m_control.IsHosting || context.ClientId <= 0) return;
            if (!m_bridge.TryCapture(m_capture, out long fingerprint, out string reason))
            {
                LogFailure("sync-capture:" + ReasonCode(reason), "应答物品同步时抓取失败：" + reason);
                return;
            }
            m_fingerprint = fingerprint;
            m_hasKnown = true;
            if (m_revision <= 0) m_revision = 1;
            SendFullTo(context.ClientId, "request");
        }

        public bool TrySubmitItemEdit(in EditableItemEditRequestData request, out string reason)
        {
            reason = null;
            if (m_transport == null || !m_transport.IsConnected)
            {
                reason = "item-edit-not-connected";
                return false;
            }
            m_pendingSequence++;
            m_pendingSlot = request.SlotIndex;
            m_pendingExpectedValue = request.ExpectedValue;
            m_pendingKind = request.Kind;
            var message = new P.EditableItemEditRequest
            {
                Sequence = m_pendingSequence,
                Kind = (P.EditableKind)request.Kind,
                SlotIndex = request.SlotIndex,
                ExpectedValue = request.ExpectedValue,
                BaseData = ByteString.CopyFrom(request.BaseData ?? Array.Empty<byte>()),
                Data = ByteString.CopyFrom(request.Data ?? Array.Empty<byte>())
            };
            if (!m_transport.TrySend(MessageType.EditableItemEditRequest, message.ToByteArray()))
            {
                reason = "item-edit-send-failed";
                return false;
            }
            return true;
        }

        private void OnItemEditResult(MessageContext context, P.EditableItemEditResult message)
        {
            if (m_control.IsHosting) return;
            if (!message.Accepted)
            {
                m_pendingSlot = -1;
                LogFailure("item-edit-rejected:" + message.Sequence,
                    "物品编辑被主机拒绝：" + (message.Reason ?? string.Empty));
                return;
            }
            if (message.DataId < 0 || message.AuthoritativeValue == 0)
            {
                LogFailure("item-edit-result:" + message.Sequence, "物品编辑结果缺少权威值");
                return;
            }
            int slot = m_pendingSlot;
            int kind = m_pendingKind;
            m_pendingSlot = -1;
            if (slot < 0) return;
            IInventory inventory = m_bridge.FindLocalInventory(out _);
            if (!m_bridge.TryApplyAuthoritativeItem(kind, message.DataId,
                    message.AuthoritativeValue, inventory, slot, out string reason))
                LogFailure("item-apply:" + ReasonCode(reason), reason);
            else
                Log("客户端物品编辑已落地：kind=" + kind + " slot=" + slot + " dataId=" +
                    message.DataId + " value=" + message.AuthoritativeValue);
        }

        private void OnItemEditRequest(MessageContext context, P.EditableItemEditRequest message)
        {
            if (!m_control.IsHosting || context.ClientId <= 0) return;
            var request = new EditableItemEditRequestData(message.Sequence,
                (int)message.Kind, message.SlotIndex, message.ExpectedValue,
                message.BaseData?.ToByteArray() ?? Array.Empty<byte>(),
                message.Data?.ToByteArray() ?? Array.Empty<byte>());
            if (!m_bridge.TryApplyHostItemEdit(context.ClientId, in request, out int dataId,
                    out int authoritativeValue, out byte[] authoritativeData, out string reason))
            {
                SendItemEditResult(context.ClientId, message.Sequence, false, reason, -1, 0);
                LogFailure("item-host-rejected:" + ReasonCode(reason), reason);
                return;
            }
            SendItemEditResult(context.ClientId, message.Sequence, true, string.Empty, dataId,
                authoritativeValue);
            BroadcastFull();
            string payloadPreview = authoritativeData == null ? "" :
                System.Text.Encoding.UTF8.GetString(authoritativeData);
            if (payloadPreview.Length > 64) payloadPreview = payloadPreview.Substring(0, 64);
            Log("主机落地客户端物品编辑 clientId=" + context.ClientId + " dataId=" +
                dataId + " payloadBytes=" + (authoritativeData?.Length ?? 0) +
                " payload=" + payloadPreview);
        }

        private void SendItemEditResult(int clientId, long sequence, bool accepted, string reason,
            int dataId, int authoritativeValue)
        {
            if (m_transport == null) return;
            var result = new P.EditableItemEditResult
            {
                Sequence = sequence,
                Accepted = accepted,
                Reason = reason ?? string.Empty,
                DataId = dataId,
                AuthoritativeValue = authoritativeValue
            };
            m_transport.TrySendTo(clientId, MessageType.EditableItemEditResult,
                result.ToByteArray());
        }

        private void OnState(MessageContext context, P.EditableItemState message)
        {
            if (m_control.IsHosting) return;
            var entries = new List<EditableItemBridge.ItemEntry>(message.Entries.Count);
            for (int i = 0; i < message.Entries.Count; i++)
            {
                P.EditableItemEntry entry = message.Entries[i];
                if (entry == null) continue;
                int kind = (int)entry.Kind;
                if (kind != EditableKinds.TruthTable && kind != EditableKinds.MemoryBank)
                {
                    LogFailure("state-kind:" + kind, "物品内容 state 含未知 kind=" + kind);
                    return;
                }
                if (entry.DataId < 0 || entry.DataId >= 1000)
                {
                    LogFailure("state-id:" + entry.DataId, "物品内容 dataId 越界：" + entry.DataId);
                    return;
                }
                if (entry.Data != null && entry.Data.Length > 1024)
                {
                    LogFailure("state-size:" + entry.Data.Length, "物品内容载荷过大");
                    return;
                }
                entries.Add(new EditableItemBridge.ItemEntry(kind, entry.DataId,
                    entry.Data?.ToByteArray() ?? Array.Empty<byte>()));
            }
            if (!m_bridge.TryApply(entries, out string applyReason))
            {
                LogFailure("state-apply:" + ReasonCode(applyReason),
                    "应用主机物品内容失败：" + applyReason);
                return;
            }
            m_known.Clear();
            m_known.AddRange(entries);
            m_hasKnown = true;
            m_revision = message.Revision;
            Log("客户端应用物品内容 rev=" + message.Revision + " entries=" + entries.Count);
        }

        private void BroadcastFull()
        {
            m_payloadEntries.Clear();
            for (int i = 0; i < m_capture.Count; i++)
            {
                EditableItemBridge.ItemEntry entry = m_capture[i];
                m_payloadEntries.Add(new P.EditableItemEntry
                {
                    Kind = (P.EditableKind)entry.Kind,
                    DataId = entry.DataId,
                    Data = ByteString.CopyFrom(entry.Data ?? Array.Empty<byte>())
                });
            }
            if (m_broadcast != null)
            {
                m_broadcast.BroadcastToPlayers(MessageType.EditableItemState,
                    EncodeState());
            }
            else
            {
                var players = new List<PlayerPeerUser>(8);
                m_broadcast?.CapturePlayerUsers(players);
            }
            Log("主机广播物品内容 rev=" + m_revision + " entries=" + m_payloadEntries.Count);
        }

        private void SendFullTo(int clientId, string reason)
        {
            m_payloadEntries.Clear();
            for (int i = 0; i < m_capture.Count; i++)
            {
                EditableItemBridge.ItemEntry entry = m_capture[i];
                m_payloadEntries.Add(new P.EditableItemEntry
                {
                    Kind = (P.EditableKind)entry.Kind,
                    DataId = entry.DataId,
                    Data = ByteString.CopyFrom(entry.Data ?? Array.Empty<byte>())
                });
            }
            m_transport?.TrySendTo(clientId, MessageType.EditableItemState, EncodeState());
            Log("向 clientId=" + clientId + " 发送物品内容 entries=" + m_payloadEntries.Count);
        }

        private byte[] EncodeState()
        {
            var state = new P.EditableItemState
            {
                Revision = Math.Max(1, m_revision),
                Full = true
            };
            state.Entries.AddRange(m_payloadEntries);
            return state.ToByteArray();
        }

        private static string ReasonCode(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "none";
            int colon = reason.IndexOf(':');
            return colon > 0 ? reason.Substring(0, colon) : reason;
        }

        private static void Log(string message) =>
            ModLog.Info("[SuMP][editable-item-sync] " + message);

        private void LogFailure(string key, string message)
        {
            ModLog.Warn("[SuMP][editable-item-sync][" + key + "] " + message);
        }
    }
}
