using System;
using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using P = SuMultiplayer.Protocol;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 多人聊天（**主机权威、纯瞬态**）。
    ///
    /// 语义（Source: Mod/ScMultiplayer/Message/ChatMessage.cs:ChatMessage 及它的处理器）：
    ///   · 一条聊天 = 发送者身份 / 名字 / 文本；文本有长度上限并做净化；
    ///   · 主机是唯一的广播者：客户端只发 <c>ChatSendRequest</c>，主机校验 / 限速 /
    ///     净化后分配给 <c>message_id</c> + <c>server_tick</c>，再广播 <c>ChatMessage</c>
    ///     给**所有**已加入的玩家端（含发送者本人一次）；发送者本地**不预显示**，
    ///     只认主机回显（sender echo only once，靠 pending request + message_id 去重）；
    ///   · 拒绝必须显式：<c>ChatRejected</c> 带 request id / 原因 / retry_after；
    ///     本地非法（空 / 超长 / 限速）同样计数并回原因，绝不静默丢消息；
    ///   · 聊天是瞬态的：**不落盘、不重放**；断线重连 / 会话变化时消息流直接清空；
    ///   · 主机自己的聊天走同一条校验 + 广播链，但因为广播只发给远端玩家端，
    ///     主机会把这条消息**本地追加一次**（不是第二次回显）。
    ///
    /// 模块层不引用 Game；UI 直接读 <see cref="Feed"/> 并通过
    /// <see cref="TrySendLocalText"/> 发言。无静态状态。
    /// </summary>
    public sealed class ChatModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 10000;
        /// <summary>客户端在途请求超时：超过它仍未收到回显 / 拒绝 → 明确失败（不假装成功）。</summary>
        private const int PendingTimeoutMs = 12000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly Func<string> m_localName;
        private readonly Func<string> m_localIdentity;
        private readonly Func<double> m_clock;
        private readonly ChatFeed m_feed = new ChatFeed();
        private readonly ChatRateLimiter m_rateLimiter = new ChatRateLimiter();
        /// <summary>主机：已加入玩家端 → 显示名 / 身份（ClientJoined 事件维护，ClientLeft 清除）。</summary>
        private readonly Dictionary<int, ChatPeer> m_peers = new Dictionary<int, ChatPeer>();
        /// <summary>客户端：在途请求（request_id → 内容 / 发出时间），用于"只认回显一次"。</summary>
        private readonly Dictionary<long, PendingRequest> m_pending =
            new Dictionary<long, PendingRequest>();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_rejectedByReason =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IPeerBroadcastPort m_broadcast;
        private IMessageRegistryPort m_messages;
        private Action<int, string, string, bool> m_clientJoinedHandler;
        private Action<int, string> m_clientLeftHandler;
        private Action<string> m_registryProblemHandler;
        private bool m_attached;
        private bool m_wasInSession;
        private int m_lastLocalClientId = int.MinValue;
        private long m_lastSummaryMs;
        private long m_nextMessageId;
        private long m_serverTick;
        private long m_nextRequestId;

        // ---------------------------------------------------------------- 计数
        private long m_sent;
        private long m_recv;
        private long m_broadcastCount;
        private long m_rejected;
        private long m_malformed;
        private long m_rateLimited;
        private long m_bubblesShown;
        private long m_sendFailures;
        private long m_broadcastPeers;
        private long m_noRecipients;
        private long m_duplicates;
        private long m_directionErrors;
        private long m_timeouts;
        private long m_rejectSendFailures;
        private long m_nameFallbacks;
        private long m_orphanEchoes;
        private long m_unknownRejects;
        private long m_systemLines;

        public ChatModule(Func<string> localName = null, Func<string> localIdentity = null,
            Func<double> clock = null)
        {
            m_localName = localName;
            m_localIdentity = localIdentity;
            m_clock = clock ?? (() => Time.RealTime);
            m_descriptor = new ModuleDescriptor("chat", "多人聊天（主机权威 / 底部消息流）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "chat" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        /// <summary>最近 8 条消息（UI 只读；淡出由 <see cref="Tick"/> 推进）。</summary>
        public ChatFeed Feed => m_feed;

        /// <summary>
        /// 当前是否处于联机会话（主机正在监听，或客户端已完成握手）。
        /// UI 用它决定聊天按钮 / 消息流是否出现。
        /// </summary>
        public bool IsInSession
        {
            get
            {
                if (!m_attached || m_transport == null || m_control == null) return false;
                if (m_control.IsHosting) return true;
                return m_transport.IsConnected && m_transport.LocalClientId >= 0;
            }
        }

        public string StatusLine() => "聊天：" + Summary();

        // ================================================================= 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 聊天不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "聊天不启用（不猜方向）");
                return;
            }
            if (context.TryGetPort(out m_broadcast) && m_broadcast != null)
            {
                // 主机广播的唯一通道；缺失时主机消息只能本地显示（下面显式计数 + 告警）。
            }
            else
            {
                m_broadcast = null;
                Log("警告：没有拿到广播端口（IPeerBroadcastPort）→ 主机无法把聊天发给客户端");
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
                // 线上 protobuf 非法（字节解析失败）在注册表里就被整帧丢弃，处理器收不到；
                // 订阅 Problem 让 chatMalformed 也覆盖这一类（只筛聊天三个 id）。
                m_registryProblemHandler = HandleRegistryProblem;
                m_messages.Registry.Problem += m_registryProblemHandler;
            }
            else
            {
                m_messages = null;
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到任何聊天消息");
            }
            m_clientJoinedHandler = HandleClientJoined;
            m_clientLeftHandler = HandleClientLeft;
            m_transport.ClientJoined += m_clientJoinedHandler;
            m_transport.ClientLeft += m_clientLeftHandler;
            m_lastLocalClientId = int.MinValue;
            m_wasInSession = false;
            m_attached = true;
            Log("聊天就绪：客户端只发请求；主机净化 / 限速 / 分配 id 后广播给所有玩家端；" +
                "消息流最近 " + ChatFeed.MaxEntries + " 条、" + ChatFeed.LifetimeSeconds + " 秒淡出");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.ChatSendRequest, P.ChatSendRequest.Parser,
                new DelegateMessageHandler<P.ChatSendRequest>(OnChatSendRequest), ModuleId);
            registry.Register((int)MessageType.ChatMessage, P.ChatMessage.Parser,
                new DelegateMessageHandler<P.ChatMessage>(OnChatMessage), ModuleId);
            registry.Register((int)MessageType.ChatRejected, P.ChatRejected.Parser,
                new DelegateMessageHandler<P.ChatRejected>(OnChatRejected), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            if (m_transport != null)
            {
                if (m_clientJoinedHandler != null)
                    m_transport.ClientJoined -= m_clientJoinedHandler;
                if (m_clientLeftHandler != null)
                    m_transport.ClientLeft -= m_clientLeftHandler;
            }
            if (m_messages != null)
            {
                try
                {
                    if (m_registryProblemHandler != null)
                        m_messages.Registry.Problem -= m_registryProblemHandler;
                    m_messages.Registry.UnregisterOwner(ModuleId);
                }
                catch (Exception) { }
                m_messages = null;
                m_registryProblemHandler = null;
            }
            ResetTransientState("detach");
            m_lastProblemMs.Clear();
            m_attached = false;
            Log("聊天下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            double now = m_clock();
            m_feed.Advance(now);

            bool inSession = IsInSession;
            int localClientId = m_transport.LocalClientId;
            if (inSession != m_wasInSession)
            {
                if (inSession)
                {
                    ResetTransientState("进入联机会话");
                    Log("聊天会话开始（" + (IsHosting ? "主机" : "客户端") +
                        "）：消息流从空开始（聊天不重放历史）");
                }
                else
                {
                    ResetTransientState("离开联机会话");
                    Log("聊天会话结束：在途请求 / 消息流已清空（聊天不落盘、不重放）");
                }
                m_wasInSession = inSession;
            }
            else if (!IsHosting && localClientId != m_lastLocalClientId)
            {
                // 客户端换了握手编号 = 重连 / 重新加入：旧会话的聊天绝不带过来。
                ResetTransientState("客户端连接编号变化（重连）");
                Log("聊天：客户端重连（clientId=" + m_lastLocalClientId + " → " +
                    localClientId + "）→ 消息流 / 在途请求已清空，历史不重放");
                m_wasInSession = inSession;
            }
            m_lastLocalClientId = localClientId;

            if (inSession)
            {
                if (IsHosting)
                {
                    // 主机：ClientLeft 事件负责移除；这里只做过期请求（主机没有在途请求）。
                }
                else
                {
                    ExpirePendingRequests(now);
                }
            }

            long nowMs = (long)(now * 1000.0);
            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary());
            }
        }

        // ================================================================= 本地发言入口（UI）

        /// <summary>
        /// 本机发言（UI 唯一入口）。成功 = 请求已发出 / 主机消息已广播；
        /// 失败必须看 <paramref name="reason"/>（空 / 超长 / 限速 / 不在会话 / 发送失败）。
        /// 客户端**不会**在本地预显示自己的消息：只等主机回显。
        /// </summary>
        public bool TrySendLocalText(string raw, out string reason)
        {
            reason = null;
            if (!m_attached || m_transport == null || m_control == null)
            {
                RecordRejected(ChatRejectReason.ChatRejectNotInRoom);
                reason = ChatRejectText.Describe(ChatRejectReason.ChatRejectNotInRoom);
                LogProblem("send-not-attached", "发送聊天失败：模块未装配 / 传输端口缺失 → " + reason);
                return false;
            }
            if (!IsInSession)
            {
                RecordRejected(ChatRejectReason.ChatRejectNotInRoom);
                reason = ChatRejectText.Describe(ChatRejectReason.ChatRejectNotInRoom);
                LogProblem("send-not-in-session", "发送聊天失败：当前不在联机会话 → " + reason);
                return false;
            }
            if (!ChatTextPolicy.TrySanitize(raw, out string text,
                    out ChatRejectReason textReason))
            {
                RecordRejected(textReason);
                reason = ChatRejectText.Describe(textReason);
                LogProblem("send-local-" + ChatRejectText.Key(textReason),
                    "本地聊天被拒绝（" + ChatRejectText.Key(textReason) + "）→ " + reason);
                if (textReason != ChatRejectReason.ChatRejectEmpty)
                    AppendLocalNotice(reason);
                return false;
            }
            if (IsHosting) return HostAcceptLocal(text, out reason);
            return ClientSendRequest(text, out reason);
        }

        /// <summary>UI 的本地提示行（例如发送失败 / 被主机拒绝）；不是玩家消息，也参与淡出。</summary>
        public void AppendLocalNotice(string text)
        {
            string detail = ChatTextPolicy.SanitizeDetail(text);
            if (string.IsNullOrWhiteSpace(detail)) return;
            AppendFeed(new ChatFeedEntry
            {
                MessageId = 0,
                SenderClientId = -1,
                SenderName = "System",
                SenderIdentity = string.Empty,
                Text = detail,
                ServerTick = m_serverTick,
                Channel = ChatChannel.All,
                RequestId = 0,
                IsSelf = false
            }, true);
        }

        // ================================================================= 客户端发送 / 接收

        private bool ClientSendRequest(string text, out string reason)
        {
            reason = null;
            double now = m_clock();
            if (!m_rateLimiter.TryAccept(m_transport.LocalClientId, now,
                    out ChatRejectReason rateReason, out int retryAfterMs))
            {
                RecordRejected(rateReason);
                reason = ChatRejectText.Describe(rateReason) +
                    (retryAfterMs > 0 ? "（约 " + retryAfterMs + " ms 后重试）" : string.Empty);
                LogProblem("client-" + ChatRejectText.Key(rateReason),
                    "客户端本地限速拒绝了聊天（" + ChatRejectText.Key(rateReason) + "，" +
                    retryAfterMs + " ms 后可重试）→ 未发送请求");
                AppendLocalNotice(reason);
                return false;
            }
            long requestId = ++m_nextRequestId;
            var request = new P.ChatSendRequest
            {
                RequestId = requestId,
                Text = text,
                Channel = ChatChannel.All,
                SenderName = SafeLocalName(),
                SenderIdentity = SafeLocalIdentity(),
                ClientTick = requestId
            };
            byte[] payload;
            try
            {
                payload = ChatCodec.EncodeRequest(request);
            }
            catch (Exception e)
            {
                m_sendFailures++;
                RecordRejected(ChatRejectReason.ChatRejectMalformed);
                reason = "聊天请求编码失败";
                LogProblem("client-encode",
                    "客户端聊天请求编码失败（" + e.GetType().Name + " " + e.Message + "）→ 未发送");
                return false;
            }
            m_pending[requestId] = new PendingRequest(requestId, text, now);
            if (!m_transport.TrySend(MessageType.ChatSendRequest, payload))
            {
                m_pending.Remove(requestId);
                m_sendFailures++;
                reason = "聊天请求发送失败（通道不可用 / 背压）";
                LogProblem("client-send-failed",
                    "聊天请求发送失败（request=" + requestId + "，通道 " +
                    m_transport.StateText + "）→ 消息未发出");
                return false;
            }
            m_sent++;
            return true;
        }

        private void OnChatMessage(MessageContext context, P.ChatMessage message)
        {
            m_recv++;
            if (IsHosting)
            {
                m_directionErrors++;
                LogProblem("message-on-host",
                    "主机收到了 ChatMessage（方向错误；只有主机能广播，clientId=" +
                    context.ClientId + "）→ 丢弃");
                return;
            }
            if (!ChatCodec.TryValidateMessage(message, out string error))
            {
                m_malformed++;
                LogProblem("message-malformed:" + error,
                    "ChatMessage 字段非法（" + error + "，clientId=" + context.ClientId +
                    "）→ 整条丢弃");
                return;
            }
            bool self = message.SenderClientId == m_transport.LocalClientId;
            if (self && message.RequestId > 0)
            {
                if (!m_pending.Remove(message.RequestId))
                {
                    m_orphanEchoes++;
                    LogProblem("echo-orphan:" + message.RequestId,
                        "收到本机聊天回显（request=" + message.RequestId +
                        "，message=" + message.MessageId + "），但没有对应的在途请求" +
                        "（已超时 / 已处理过）→ 丢弃（保证发送者只显示一次）");
                    return;
                }
            }
            if (m_feed.ContainsMessageId(message.MessageId))
            {
                m_duplicates++;
                LogProblem("message-duplicate:" + message.MessageId,
                    "重复的 ChatMessage（message_id=" + message.MessageId +
                    "）→ 只显示一次");
                return;
            }
            AppendFeed(new ChatFeedEntry
            {
                MessageId = message.MessageId,
                SenderClientId = message.SenderClientId,
                SenderIdentity = message.SenderIdentity ?? string.Empty,
                SenderName = message.SenderName,
                Text = message.Text,
                ServerTick = message.ServerTick,
                Channel = message.Channel,
                RequestId = message.RequestId,
                IsSelf = self
            }, true);
        }

        private void OnChatRejected(MessageContext context, P.ChatRejected message)
        {
            m_recv++;
            if (IsHosting)
            {
                m_directionErrors++;
                LogProblem("rejected-on-host",
                    "主机收到了 ChatRejected（方向错误；拒绝只由主机发给请求者，clientId=" +
                    context.ClientId + "）→ 丢弃");
                return;
            }
            if (!ChatCodec.TryValidateRejected(message, out string error))
            {
                m_malformed++;
                LogProblem("rejected-malformed:" + error,
                    "ChatRejected 字段非法（" + error + "，clientId=" + context.ClientId +
                    "）→ 丢弃");
                return;
            }
            if (!m_pending.Remove(message.RequestId))
            {
                m_unknownRejects++;
                LogProblem("rejected-unknown:" + message.RequestId,
                    "收到不认识的聊天拒绝（request=" + message.RequestId + "）→ 丢弃");
                return;
            }
            RecordRejected(message.Reason);
            string detail = string.IsNullOrWhiteSpace(message.Detail)
                ? ChatRejectText.Describe(message.Reason)
                : message.Detail;
            AppendLocalNotice("消息未发送：" + detail);
            LogProblem("rejected-" + ChatRejectText.Key(message.Reason) + ":" +
                message.RequestId,
                "聊天被主机拒绝（request=" + message.RequestId + "，" +
                ChatRejectText.Key(message.Reason) + "）：" + detail);
        }

        // ================================================================= 主机接收 / 广播

        private void OnChatSendRequest(MessageContext context, P.ChatSendRequest request)
        {
            m_recv++;
            if (!IsHosting)
            {
                m_directionErrors++;
                LogProblem("request-on-client",
                    "客户端收到了 ChatSendRequest（方向错误，clientId=" + context.ClientId +
                    "）→ 丢弃");
                return;
            }
            if (!ChatCodec.TryValidateRequest(request, out string error))
            {
                m_malformed++;
                LogProblem("request-malformed:" + error,
                    "ChatSendRequest 字段非法（" + error + "，clientId=" + context.ClientId +
                    "）→ 拒绝");
                SendReject(context.ClientId, Math.Max(request.RequestId, 0),
                    ChatRejectReason.ChatRejectMalformed, "请求字段非法：" + error, 0);
                return;
            }
            if (m_broadcast == null || !m_broadcast.IsPlayerClient(context.ClientId))
            {
                m_directionErrors++;
                LogProblem("request-not-player:" + context.ClientId,
                    "非玩家端请求聊天（clientId=" + context.ClientId +
                    "；调试端只读）→ 拒绝");
                SendReject(context.ClientId, request.RequestId, ChatRejectReason.ChatRejectNotPlayer,
                    "只有已加入的玩家端可以发言", 0);
                return;
            }
            if (request.Channel != ChatChannel.All)
            {
                RecordRejected(ChatRejectReason.ChatRejectUnsupportedChannel);
                SendReject(context.ClientId, request.RequestId,
                    ChatRejectReason.ChatRejectUnsupportedChannel,
                    "当前只支持全体频道（nearby 未启用）", 0);
                return;
            }
            if (!ChatTextPolicy.TrySanitize(request.Text, out string text,
                    out ChatRejectReason textReason))
            {
                RecordRejected(textReason);
                SendReject(context.ClientId, request.RequestId, textReason,
                    ChatRejectText.Describe(textReason), 0);
                return;
            }
            double now = m_clock();
            if (!m_rateLimiter.TryAccept(context.ClientId, now,
                    out ChatRejectReason rateReason, out int retryAfterMs))
            {
                RecordRejected(rateReason);
                SendReject(context.ClientId, request.RequestId, rateReason,
                    ChatRejectText.Describe(rateReason), retryAfterMs);
                LogProblem("host-" + ChatRejectText.Key(rateReason) + ":" + context.ClientId,
                    "客户端 " + context.ClientId + " 的聊天被限速（" +
                    ChatRejectText.Key(rateReason) + "，" + retryAfterMs +
                    " ms 后可重试）→ 已回 ChatRejected");
                return;
            }

            ResolveSender(context.ClientId, request.SenderName, request.SenderIdentity,
                out string senderName, out string senderIdentity);
            AcceptAndBroadcast(context.ClientId, senderName, senderIdentity, text,
                ChatChannel.All, request.RequestId, out string acceptError);
            if (acceptError != null)
            {
                SendReject(context.ClientId, request.RequestId, ChatRejectReason.ChatRejectMalformed,
                    acceptError, 0);
            }
        }

        private bool HostAcceptLocal(string text, out string reason)
        {
            reason = null;
            double now = m_clock();
            // 主机自己的 clientId = 0，与远端走同一套限速（防止主机脚本刷屏）。
            if (!m_rateLimiter.TryAccept(0, now, out ChatRejectReason rateReason,
                    out int retryAfterMs))
            {
                RecordRejected(rateReason);
                reason = ChatRejectText.Describe(rateReason) +
                    (retryAfterMs > 0 ? "（约 " + retryAfterMs + " ms 后重试）" : string.Empty);
                LogProblem("host-local-" + ChatRejectText.Key(rateReason),
                    "主机本地聊天被限速（" + ChatRejectText.Key(rateReason) + "）→ " + reason);
                AppendLocalNotice(reason);
                return false;
            }
            string name = SafeLocalName();
            string identity = SafeLocalIdentity();
            if (AcceptAndBroadcast(0, name, identity, text, ChatChannel.All, 0,
                    out string acceptError))
            {
                return true;
            }
            reason = acceptError ?? "聊天广播失败";
            LogProblem("host-local-accept", "主机本地聊天失败：" + reason);
            return false;
        }

        private bool AcceptAndBroadcast(int senderClientId, string senderName,
            string senderIdentity, string text, ChatChannel channel, long requestId,
            out string error)
        {
            error = null;
            var message = new P.ChatMessage
            {
                MessageId = ++m_nextMessageId,
                SenderClientId = senderClientId,
                SenderIdentity = senderIdentity ?? string.Empty,
                SenderName = senderName,
                Text = text,
                ServerTick = ++m_serverTick,
                Channel = channel,
                RequestId = requestId
            };
            if (!ChatCodec.TryValidateMessage(message, out string validationError))
            {
                m_malformed++;
                error = "聊天消息字段非法：" + validationError;
                LogProblem("accept-invalid", error);
                return false;
            }
            byte[] payload;
            try
            {
                payload = ChatCodec.EncodeMessage(message);
            }
            catch (Exception e)
            {
                m_sendFailures++;
                error = "聊天消息编码失败";
                LogProblem("accept-encode",
                    "聊天消息编码失败（" + e.GetType().Name + " " + e.Message + "）→ 未广播");
                return false;
            }
            m_sent++;
            m_broadcastCount++;
            if (m_broadcast != null)
            {
                int peers = m_broadcast.BroadcastToPlayers(MessageType.ChatMessage, payload);
                m_broadcastPeers += peers;
                if (peers <= 0)
                {
                    m_noRecipients++;
                    if (m_broadcast.PlayerPeerCount > 0)
                    {
                        LogProblem("broadcast-zero",
                            "聊天广播入队 0 个玩家端（在线 " + m_broadcast.PlayerPeerCount +
                            "）→ 该消息只有本端可见（显式计数，不静默）");
                    }
                }
            }
            else
            {
                m_noRecipients++;
                LogProblem("no-broadcast-port",
                    "主机接受了一条聊天，但没有广播端口 → 只有本端可见（显式计数）");
            }
            AppendFeed(new ChatFeedEntry
            {
                MessageId = message.MessageId,
                SenderClientId = message.SenderClientId,
                SenderIdentity = message.SenderIdentity,
                SenderName = message.SenderName,
                Text = message.Text,
                ServerTick = message.ServerTick,
                Channel = message.Channel,
                RequestId = message.RequestId,
                IsSelf = senderClientId == 0
            }, true);
            return true;
        }

        private void SendReject(int clientId, long requestId, ChatRejectReason reason,
            string detail, int retryAfterMs)
        {
            if (m_transport == null) return;
            if (requestId <= 0)
            {
                // 没有 request_id 就无法让请求者对上号；只能计数 + 限流日志（已经写过）。
                m_rejectSendFailures++;
                LogProblem("reject-no-request-id:" + clientId,
                    "要拒绝一条聊天请求，但 request_id 非法（" + requestId +
                    "）→ 无法回执给 clientId=" + clientId);
                return;
            }
            var message = new P.ChatRejected
            {
                RequestId = requestId,
                Reason = reason,
                Detail = ChatTextPolicy.SanitizeDetail(detail),
                ServerTick = m_serverTick,
                RetryAfterMs = retryAfterMs
            };
            byte[] payload;
            try
            {
                payload = ChatCodec.EncodeRejected(message);
            }
            catch (Exception e)
            {
                m_rejectSendFailures++;
                LogProblem("reject-encode",
                    "ChatRejected 编码失败（" + e.GetType().Name + " " + e.Message + "）");
                return;
            }
            if (!m_transport.TrySendTo(clientId, MessageType.ChatRejected, payload))
            {
                m_rejectSendFailures++;
                LogProblem("reject-send-failed:" + clientId,
                    "ChatRejected 发送失败（clientId=" + clientId + "，request=" +
                    requestId + "，" + ChatRejectText.Key(reason) +
                    "）→ 请求者收不到明确拒绝（计数 chatRejectSendFailures）");
            }
        }

        // ================================================================= 身份 / 名册

        private sealed class ChatPeer
        {
            public ChatPeer(string name, string identity)
            {
                Name = name ?? string.Empty;
                Identity = identity ?? string.Empty;
            }

            public string Name { get; }
            public string Identity { get; }
        }

        private readonly struct PendingRequest
        {
            public PendingRequest(long requestId, string text, double sentAt)
            {
                RequestId = requestId;
                Text = text;
                SentAt = sentAt;
            }

            public long RequestId { get; }
            public string Text { get; }
            public double SentAt { get; }
        }

        private void HandleClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            string sanitized = string.Empty;
            if (!ChatTextPolicy.TrySanitizeName(name, out sanitized))
            {
                m_nameFallbacks++;
                sanitized = string.Empty;
            }
            m_peers[clientId] = new ChatPeer(sanitized,
                ChatTextPolicy.SanitizeIdentity(identity));
            Log("聊天：玩家端 " + clientId + " 加入（名字长度=" + sanitized.Length +
                (rejoin ? "，重连" : string.Empty) + "）");
        }

        private void HandleClientLeft(int clientId, string reason)
        {
            m_peers.Remove(clientId);
            m_rateLimiter.Remove(clientId);
            Log("聊天：玩家端 " + clientId + " 离开（" + reason + "）→ 名册 / 限速状态已清");
        }

        /// <summary>
        /// 主机侧的发送者身份：**优先**用 ClientJoined 记录（握手名字 / 身份）；
        /// 记录缺失或净化失败时，用请求里携带的显示提示；仍然没有 → "Player" 并计数。
        /// 名称 / 身份只用于显示，绝不用于绑定（绑定仍走 UserId.dat 的玩家记录）。
        /// </summary>
        private void ResolveSender(int clientId, string hintName, string hintIdentity,
            out string senderName, out string senderIdentity)
        {
            senderName = string.Empty;
            senderIdentity = string.Empty;
            if (m_peers.TryGetValue(clientId, out ChatPeer peer))
            {
                senderName = peer.Name;
                senderIdentity = peer.Identity;
            }
            if (string.IsNullOrEmpty(senderName))
            {
                if (ChatTextPolicy.TrySanitizeName(hintName, out string fromHint))
                {
                    senderName = fromHint;
                }
            }
            if (string.IsNullOrEmpty(senderName))
            {
                m_nameFallbacks++;
                senderName = "Player";
                LogProblem("sender-name-fallback:" + clientId,
                    "玩家端 " + clientId + " 的聊天没有可用显示名（ClientJoined 与请求提示都为空）" +
                    "→ 使用 \"Player\"（显式计数 chatNameFallbacks）");
            }
            if (string.IsNullOrEmpty(senderIdentity))
                senderIdentity = ChatTextPolicy.SanitizeIdentity(hintIdentity);
        }

        private string SafeLocalName()
        {
            string raw = null;
            try { raw = m_localName?.Invoke(); }
            catch (Exception e)
            {
                LogProblem("local-name-fault",
                    "取本机聊天显示名失败（" + e.GetType().Name + " " + e.Message + "）");
            }
            if (ChatTextPolicy.TrySanitizeName(raw, out string name)) return name;
            m_nameFallbacks++;
            return "Player";
        }

        private string SafeLocalIdentity()
        {
            try { return ChatTextPolicy.SanitizeIdentity(m_localIdentity?.Invoke()); }
            catch (Exception e)
            {
                LogProblem("local-identity-fault",
                    "取本机身份失败（" + e.GetType().Name + " " + e.Message + "）");
                return string.Empty;
            }
        }

        // ================================================================= 状态 / 计数

        private bool IsHosting => m_control?.IsHosting == true;

        private void ExpirePendingRequests(double now)
        {
            long nowMs = (long)(now * 1000.0);
            List<long> expired = null;
            foreach (KeyValuePair<long, PendingRequest> pair in m_pending)
            {
                if (nowMs - (long)(pair.Value.SentAt * 1000.0) < PendingTimeoutMs) continue;
                (expired ??= new List<long>()).Add(pair.Key);
            }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++)
            {
                long requestId = expired[i];
                if (!m_pending.Remove(requestId)) continue;
                m_timeouts++;
                LogProblem("pending-timeout:" + requestId,
                    "聊天请求 " + requestId + " 超过 " + PendingTimeoutMs +
                    " ms 没有收到主机回显 / 拒绝 → 明确失败（计数 chatTimeouts）");
                AppendLocalNotice("消息发送超时（主机没有回应）");
            }
        }

        private void AppendFeed(ChatFeedEntry entry, bool countBubble)
        {
            int removed = m_feed.Append(entry, m_clock());
            if (countBubble) m_bubblesShown++;
            if (entry.SenderClientId < 0) m_systemLines++;
            if (removed > 0)
            {
                // 有界窗口：只丢最旧的可见行，不是丢协议消息（不影响聊天送达）。
            }
        }

        private void RecordRejected(ChatRejectReason reason)
        {
            m_rejected++;
            string key = ChatRejectText.Key(reason);
            m_rejectedByReason.TryGetValue(key, out long count);
            m_rejectedByReason[key] = count + 1;
            if (reason == ChatRejectReason.ChatRejectRateLimited || reason == ChatRejectReason.ChatRejectFlood)
                m_rateLimited++;
        }

        private void ResetTransientState(string reason)
        {
            m_feed.Clear();
            m_pending.Clear();
            m_peers.Clear();
            m_rateLimiter.Reset();
            m_nextMessageId = 0;
            m_serverTick = 0;
            m_nextRequestId = 0;
            if (!string.IsNullOrEmpty(reason))
            {
                // 只为诊断，不写任何持久状态。
            }
        }

        // ================================================================= 日志 / 快照

        /// <summary>
        /// 注册表的载荷解析失败事件：只认聊天三个消息 id，计入 chatMalformed 并限流告警。
        /// 文本形状来自 Net/MessageChannel.cs:ReportMalformed（"消息载荷解析失败：type=83（…"）。
        /// </summary>
        private void HandleRegistryProblem(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (!message.Contains("消息载荷解析失败")) return;
            bool chat = message.Contains("type=" + (int)MessageType.ChatSendRequest + "（") ||
                message.Contains("type=" + (int)MessageType.ChatMessage + "（") ||
                message.Contains("type=" + (int)MessageType.ChatRejected + "（");
            if (!chat) return;
            m_malformed++;
            LogProblem("wire-malformed", "线上聊天载荷解析失败（整帧丢弃）：" + message);
        }

        private void Log(string message) => m_context?.Diagnostics.Info(ModuleId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(m_clock() * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs) return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }

        public ChatCountersSnapshot GetSnapshot()
        {
            var byReason = new Dictionary<string, long>(m_rejectedByReason,
                StringComparer.Ordinal);
            return new ChatCountersSnapshot(m_sent, m_recv, m_broadcastCount, m_rejected,
                byReason, m_malformed, m_rateLimited, m_bubblesShown, m_sendFailures,
                m_broadcastPeers, m_noRecipients, m_duplicates, m_directionErrors,
                m_timeouts, m_rejectSendFailures, m_nameFallbacks, m_orphanEchoes,
                m_unknownRejects, m_systemLines);
        }

        private string Summary()
        {
            return "chatSent=" + m_sent +
                " chatRecv=" + m_recv +
                " chatBroadcast=" + m_broadcastCount +
                " chatRejected=" + m_rejected +
                " chatMalformed=" + m_malformed +
                " chatRateLimited=" + m_rateLimited +
                " chatBubblesShown=" + m_bubblesShown +
                " broadcastPeers=" + m_broadcastPeers +
                " noRecipients=" + m_noRecipients +
                " duplicates=" + m_duplicates +
                " directionErrors=" + m_directionErrors +
                " timeouts=" + m_timeouts +
                " sendFailures=" + m_sendFailures +
                " rejectSendFailures=" + m_rejectSendFailures +
                " nameFallbacks=" + m_nameFallbacks +
                " orphanEchoes=" + m_orphanEchoes +
                " unknownRejects=" + m_unknownRejects +
                " systemLines=" + m_systemLines +
                " rejected[" + DescribeRejectedByReason() + "]";
        }

        private string DescribeRejectedByReason()
        {
            if (m_rejectedByReason.Count == 0) return "none";
            var keys = new List<string>(m_rejectedByReason.Keys);
            keys.Sort(StringComparer.Ordinal);
            var parts = new List<string>(keys.Count);
            for (int i = 0; i < keys.Count; i++)
                parts.Add(keys[i] + "=" + m_rejectedByReason[keys[i]]);
            return string.Join(",", parts);
        }
    }

    /// <summary>聊天计数器快照（诊断 / tsverify 断言用；字典是副本）。</summary>
    public readonly struct ChatCountersSnapshot
    {
        public ChatCountersSnapshot(long sent, long recv, long broadcast, long rejected,
            IReadOnlyDictionary<string, long> rejectedByReason, long malformed,
            long rateLimited, long bubblesShown, long sendFailures, long broadcastPeers,
            long noRecipients, long duplicates, long directionErrors, long timeouts,
            long rejectSendFailures, long nameFallbacks, long orphanEchoes,
            long unknownRejects, long systemLines)
        {
            Sent = sent;
            Recv = recv;
            Broadcast = broadcast;
            Rejected = rejected;
            RejectedByReason = rejectedByReason;
            Malformed = malformed;
            RateLimited = rateLimited;
            BubblesShown = bubblesShown;
            SendFailures = sendFailures;
            BroadcastPeers = broadcastPeers;
            NoRecipients = noRecipients;
            Duplicates = duplicates;
            DirectionErrors = directionErrors;
            Timeouts = timeouts;
            RejectSendFailures = rejectSendFailures;
            NameFallbacks = nameFallbacks;
            OrphanEchoes = orphanEchoes;
            UnknownRejects = unknownRejects;
            SystemLines = systemLines;
        }

        public long Sent { get; }
        public long Recv { get; }
        public long Broadcast { get; }
        public long Rejected { get; }
        public IReadOnlyDictionary<string, long> RejectedByReason { get; }
        public long Malformed { get; }
        public long RateLimited { get; }
        public long BubblesShown { get; }
        public long SendFailures { get; }
        public long BroadcastPeers { get; }
        public long NoRecipients { get; }
        public long Duplicates { get; }
        public long DirectionErrors { get; }
        public long Timeouts { get; }
        public long RejectSendFailures { get; }
        public long NameFallbacks { get; }
        public long OrphanEchoes { get; }
        public long UnknownRejects { get; }
        public long SystemLines { get; }

        public long RejectedCount(string key)
        {
            if (RejectedByReason == null || string.IsNullOrEmpty(key)) return 0;
            return RejectedByReason.TryGetValue(key, out long value) ? value : 0;
        }
    }
}
