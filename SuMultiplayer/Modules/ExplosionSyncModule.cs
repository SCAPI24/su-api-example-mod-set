using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using P = SuMultiplayer.Protocol.Explosion;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 爆炸同步模块（**主机权威，客户端纯表现**）。
    ///
    /// 主机侧：
    ///   · 引擎的 SubsystemExplosions 执行每一个爆炸（点火 / 投掷物 / 撞击 / 火 / 岩浆 /
    ///     连锁 / 烟花），引擎适配层把"已执行"的实例上报到这里；
    ///   · 这里编码 <c>ExplosionEvent</c> 广播给所有玩家端（含发起者，便于确认）；
    ///   · 地形仍走既有主机同步（CellChange），不在这里重复；掉落物 / 投射物属于**实体域**，
    ///     由 Modules/EntityReplicationModule.cs（主机权威，32Hz 变化驱动）负责；本模块绝不伪造实体，
    ///     也绝不因为"客户端看不到掉落物"就本地 AddPickable；
    ///   · 玩家受伤由引擎结算，只额外广播 <c>PlayerDamaged</c> 供表现；客户端**不**改血量。
    ///
    /// 客户端侧：
    ///   · 收到 ExplosionEvent 先按 (server_tick, event_id) 排序、按 event_id 去重；
    ///   · 只调用 <see cref="IExplosionReplicaPort.Present"/>（粒子 / 声音 / 震动）；
    ///   · 烟花（cosmetic_only）只表现；绝不本地跑 SubsystemExplosions。
    ///
    /// 客户端用爆炸物：
    ///   · 投掷 / 点燃引信 → <c>ExplosionRequest</c>（每连接单调序号）；
    ///   · 主机校验（玩家端、距离、方块/道具合法性）→ 执行 → <c>ExplosionResult</c>；
    ///   · 被拒绝**不**放本地爆炸，写明确原因 + 计数。
    ///
    /// 无静态状态；只依赖端口与 Net/ExplosionNet.cs 的纯逻辑。
    /// </summary>
    public sealed class ExplosionSyncModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 5000;
        private const int MaxPendingRequests = 32;
        private const long RequestTimeoutMs = 5000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IExplosionReplicaPort m_port;
        private readonly ExplosionEventOrderer m_eventOrderer = new ExplosionEventOrderer();
        private readonly ExplosionEventOrderer m_damageOrderer = new ExplosionEventOrderer();
        private readonly Dictionary<long, PendingRequest> m_pendingRequests =
            new Dictionary<long, PendingRequest>();
        private readonly Dictionary<int, long> m_lastRequestSequence = new Dictionary<int, long>();
        private readonly Dictionary<int, Dictionary<long, ExplosionResultData>> m_recentResults =
            new Dictionary<int, Dictionary<long, ExplosionResultData>>();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IPeerBroadcastPort m_broadcast;
        private IMessageRegistryPort m_messages;
        private Action<int, string, string, bool> m_clientJoinedHandler;
        private Action<int, string> m_clientLeftHandler;
        private bool m_attached;
        private bool m_wasConnected;
        private long m_nextRequestSequence;
        private long m_lastSummaryMs;

        // ---------------------------------------------------------------- 计数（状态行可审计）
        private long m_explosionsExecuted;
        private long m_explosionsSent;
        private long m_explosionsSentPeers;
        private long m_explosionSendFailures;
        private long m_explosionsWithoutRecipients;
        private long m_cosmeticSent;
        private long m_eventsReceived;
        private long m_eventsApplied;
        private long m_eventsDeduped;
        private long m_eventsOutOfOrder;
        private long m_eventsMalformed;
        private long m_eventsRejected;
        private long m_cosmeticPresented;
        private long m_presentFailures;
        private long m_requestsSubmitted;
        private long m_requestsSent;
        private long m_requestSendFailures;
        private long m_requestsReceived;
        private long m_requestsAccepted;
        private long m_requestsRejected;
        private long m_requestsDuplicate;
        private long m_resultsReceived;
        private long m_resultsAccepted;
        private long m_resultsRejected;
        private long m_resultsUnknown;
        private long m_resultSendFailures;
        private long m_requestTimeouts;
        private long m_itemConsumed;
        private long m_itemConsumeFailures;
        private long m_playerDamagesSent;
        private long m_playerDamagesReceived;
        private long m_playerDamagesPresented;
        private long m_playerDamageSendFailures;
        private bool m_loggedSelfDamage;

        private readonly struct PendingRequest
        {
            public PendingRequest(in ExplosionRequestData request, long sentMs)
            {
                Request = request;
                SentMs = sentMs;
            }

            public ExplosionRequestData Request { get; }
            public long SentMs { get; }
        }

        public ExplosionSyncModule(IExplosionReplicaPort port)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_descriptor = new ModuleDescriptor("explosion-sync", "爆炸同步（主机权威 / 客户端表现）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "explosion.sync" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "爆炸同步：" + Summary() + " · " + m_port.Describe();

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 爆炸同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "爆炸同步不启用（不猜方向）");
                return;
            }
            if (!context.TryGetPort(out m_broadcast) || m_broadcast == null)
            {
                Log("警告：没有拿到广播端口（IPeerBroadcastPort）→ 主机无法把爆炸事件广播给所有客户端");
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到爆炸事件 / 请求");
            }
            m_port.HostExplosionExecuted += OnHostExplosion;
            m_port.HostPlayerDamaged += OnHostPlayerDamaged;
            m_port.ClientRequestSubmitted += OnClientRequestSubmitted;
            m_clientJoinedHandler = HandleClientJoined;
            m_clientLeftHandler = HandleClientLeft;
            m_transport.ClientJoined += m_clientJoinedHandler;
            m_transport.ClientLeft += m_clientLeftHandler;
            m_attached = true;
            Log("爆炸同步模块就绪：主机 = 引擎执行后广播；客户端 = 排序 / 去重后只做表现" +
                "（投掷 / 点燃引信走请求，拒绝不放本地爆炸）");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.ExplosionEvent, P.ExplosionEvent.Parser,
                new DelegateMessageHandler<P.ExplosionEvent>(OnExplosionEvent), ModuleId);
            registry.Register((int)MessageType.ExplosionRequest, P.ExplosionRequest.Parser,
                new DelegateMessageHandler<P.ExplosionRequest>(OnExplosionRequest), ModuleId);
            registry.Register((int)MessageType.ExplosionResult, P.ExplosionResult.Parser,
                new DelegateMessageHandler<P.ExplosionResult>(OnExplosionResult), ModuleId);
            registry.Register((int)MessageType.PlayerDamaged, P.PlayerDamaged.Parser,
                new DelegateMessageHandler<P.PlayerDamaged>(OnPlayerDamaged), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            m_port.HostExplosionExecuted -= OnHostExplosion;
            m_port.HostPlayerDamaged -= OnHostPlayerDamaged;
            m_port.ClientRequestSubmitted -= OnClientRequestSubmitted;
            if (m_transport != null)
            {
                if (m_clientJoinedHandler != null)
                    m_transport.ClientJoined -= m_clientJoinedHandler;
                if (m_clientLeftHandler != null)
                    m_transport.ClientLeft -= m_clientLeftHandler;
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            m_pendingRequests.Clear();
            m_lastRequestSequence.Clear();
            m_recentResults.Clear();
            m_eventOrderer.Reset("detach");
            m_damageOrderer.Reset("detach");
            m_attached = false;
            Log("爆炸同步模块下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_port.Pump();
            bool connected = m_transport != null && m_transport.IsConnected;
            if (connected && !m_wasConnected)
            {
                m_eventOrderer.Reset("connect");
                m_damageOrderer.Reset("connect");
                m_pendingRequests.Clear();
                m_lastRequestSequence.Clear();
                m_recentResults.Clear();
                m_nextRequestSequence = 0;
                Log("爆炸同步：连接已建立 → 事件闸门 / 请求表已重置（旧会话的序号不再复用）");
            }
            m_wasConnected = connected;
            long nowMs = (long)(tick.Now * 1000.0);
            ExpirePendingRequests(nowMs);
            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary() + " · " + m_port.Describe());
            }
        }

        // ------------------------------------------------------------ 主机：引擎已执行的爆炸

        private void OnHostExplosion(ExplosionEventData explosion)
        {
            m_explosionsExecuted++;
            if (explosion.CosmeticOnly) m_cosmeticSent++;
            if (m_broadcast == null)
            {
                m_explosionsWithoutRecipients++;
                LogProblem("no-broadcast-port", "主机执行了爆炸 " + explosion.Describe() +
                    "，但没有广播端口 → 没有客户端能看到（明确计数，不静默）");
                return;
            }
            if (m_broadcast.PlayerPeerCount <= 0)
            {
                m_explosionsWithoutRecipients++;
                LogProblem("no-peers", "主机执行了爆炸 " + explosion.Describe() +
                    "，此刻没有在线玩家端 → 没有下发目标（计数 explosionsWithoutRecipients）");
                return;
            }
            byte[] payload = ExplosionEventCodec.Encode(explosion).ToByteArray();
            int peers = m_broadcast.BroadcastToPlayers(MessageType.ExplosionEvent, payload);
            if (peers <= 0)
            {
                m_explosionSendFailures++;
                LogProblem("explosion-send-failed", "爆炸事件 " + explosion.Describe() +
                    " 广播失败（" + m_broadcast.PlayerPeerCount + " 个玩家端，入队 0）");
                return;
            }
            m_explosionsSent++;
            m_explosionsSentPeers += peers;
        }

        private void OnHostPlayerDamaged(PlayerDamageData damage)
        {
            if (m_broadcast == null || m_broadcast.PlayerPeerCount <= 0) return;
            byte[] payload = ExplosionEventCodec.EncodeDamage(damage).ToByteArray();
            int peers = m_broadcast.BroadcastToPlayers(MessageType.PlayerDamaged, payload);
            if (peers <= 0)
            {
                m_playerDamageSendFailures++;
                LogProblem("damage-send-failed", "玩家受伤事件 clientId=" + damage.ClientId +
                    " name=" + damage.Name + " damage=" + damage.Damage.ToString("F3") +
                    " 广播失败（" + m_broadcast.PlayerPeerCount + " 个玩家端，入队 0）");
                return;
            }
            m_playerDamagesSent++;
        }

        // ------------------------------------------------------------ 客户端：事件表现

        private void OnExplosionEvent(MessageContext context, P.ExplosionEvent message)
        {
            m_eventsReceived++;
            if (m_control?.IsHosting == true)
            {
                m_eventsRejected++;
                LogProblem("event-on-host", "主机收到了 ExplosionEvent（方向错误，clientId=" +
                    context.ClientId + "）→ 丢弃");
                return;
            }
            if (!ExplosionEventCodec.TryDecode(message, out ExplosionEventData explosion,
                    out string reason))
            {
                m_eventsMalformed++;
                LogProblem("event-malformed:" + reason, "ExplosionEvent 解码失败（" + reason +
                    "，clientId=" + context.ClientId + "）→ 整条丢弃");
                return;
            }
            if (!m_eventOrderer.TryAccept(in explosion, out string orderReason))
            {
                if (orderReason == "duplicate")
                {
                    m_eventsDeduped++;
                    LogProblem("event-duplicate", "重复的 ExplosionEvent id=" + explosion.EventId +
                        " → 只播一次（deduped）");
                }
                else
                {
                    m_eventsOutOfOrder++;
                    LogProblem("event-stale", "过期/越序的 ExplosionEvent " + explosion.Describe() +
                        "（last tick=" + m_eventOrderer.LastTick + " id=" +
                        m_eventOrderer.LastEventId + "）→ 丢弃");
                }
                return;
            }
            if (!m_port.Present(in explosion, out string presentReason))
            {
                m_presentFailures++;
                LogProblem("present-failed:" + presentReason, "爆炸表现失败 " + explosion.Describe() +
                    "（" + presentReason + "）→ 本次没有画面（明确计数，不静默）");
                return;
            }
            m_eventsApplied++;
            if (explosion.CosmeticOnly) m_cosmeticPresented++;
        }

        private void OnPlayerDamaged(MessageContext context, P.PlayerDamaged message)
        {
            m_playerDamagesReceived++;
            if (m_control?.IsHosting == true)
            {
                LogProblem("damage-on-host", "主机收到了 PlayerDamaged（方向错误）→ 丢弃");
                return;
            }
            if (!ExplosionEventCodec.TryDecodeDamage(message, out PlayerDamageData damage,
                    out string reason))
            {
                LogProblem("damage-malformed:" + reason, "PlayerDamaged 解码失败（" + reason +
                    "）→ 整条丢弃");
                return;
            }
            if (!m_damageOrderer.TryAccept(new ExplosionEventData(damage.EventId,
                    damage.ServerTick, damage.Position, ExplosionKinds.Block, 0f, 0u, false, 0f,
                    false), out string orderReason))
            {
                LogProblem("damage-order", "PlayerDamaged eventId=" + damage.EventId + " → " +
                    orderReason);
                return;
            }
            if (!m_port.PresentPlayerDamage(in damage, out string presentReason))
            {
                LogProblem("damage-present-failed:" + presentReason,
                    "PlayerDamaged clientId=" + damage.ClientId + " 表现失败（" + presentReason + "）");
                return;
            }
            m_playerDamagesPresented++;
            if (m_transport != null && damage.ClientId == m_transport.LocalClientId &&
                !m_loggedSelfDamage)
            {
                m_loggedSelfDamage = true;
                Log("本机玩家收到主机爆炸伤害事件 clientId=" + damage.ClientId + " damage=" +
                    damage.Damage.ToString("F3") + " healthAfter=" +
                    damage.HealthAfter.ToString("F3") +
                    " → 只做记录（血量同步是后续项；客户端绝不本地扣血）");
            }
        }

        // ------------------------------------------------------------ 客户端：请求

        private void OnClientRequestSubmitted(ExplosionRequestData request)
        {
            if (m_messages == null)
            {
                m_requestSendFailures++;
                LogProblem("no-message-layer", "客户端要提交爆炸请求 " + request.Describe() +
                    "，但没有消息层 → 请求没有发出（不本地放爆炸）");
                return;
            }
            if (m_control?.IsHosting == true) return;    // 主机自己走引擎原路径
            long sequence = ++m_nextRequestSequence;
            var sequenced = new ExplosionRequestData(sequence, request.Kind, request.Position,
                request.Direction, request.ItemValue, request.ActiveSlot, request.HasReach,
                request.Reach);
            m_requestsSubmitted++;
            if (m_pendingRequests.Count >= MaxPendingRequests)
            {
                long oldest = long.MaxValue;
                foreach (long key in m_pendingRequests.Keys) if (key < oldest) oldest = key;
                if (oldest != long.MaxValue) m_pendingRequests.Remove(oldest);
                LogProblem("request-table-full", "爆炸请求表已满（" + MaxPendingRequests +
                    "）→ 丢弃最旧的在途请求 seq=" + oldest);
            }
            m_pendingRequests[sequence] = new PendingRequest(in sequenced,
                (long)(Time.RealTime * 1000.0));
            if (!m_messages.Sender.Send(0, ExplosionEventCodec.EncodeRequest(in sequenced)))
            {
                m_requestSendFailures++;
                m_pendingRequests.Remove(sequence);
                LogProblem("request-send-failed", "爆炸请求 " + sequenced.Describe() +
                    " 发送失败（通道不可用 / 未注册）→ 客户端不放本地爆炸");
                return;
            }
            m_requestsSent++;
        }

        private void OnExplosionResult(MessageContext context, P.ExplosionResult message)
        {
            m_resultsReceived++;
            if (m_control?.IsHosting == true)
            {
                LogProblem("result-on-host", "主机收到了 ExplosionResult（方向错误）→ 丢弃");
                return;
            }
            if (!ExplosionEventCodec.TryDecodeResult(message, out ExplosionResultData result,
                    out string reason))
            {
                LogProblem("result-malformed:" + reason, "ExplosionResult 解码失败（" + reason +
                    "）→ 整条丢弃");
                return;
            }
            if (!m_pendingRequests.TryGetValue(result.Sequence, out PendingRequest pending))
            {
                m_resultsUnknown++;
                LogProblem("result-unknown", "收到未知请求序号 " + result.Sequence +
                    " 的 ExplosionResult（" + (result.Accepted ? "accepted" : "rejected: " +
                    result.Reason) + "）→ 不应用");
                return;
            }
            m_pendingRequests.Remove(result.Sequence);
            if (!result.Accepted)
            {
                m_resultsRejected++;
                LogProblem("request-rejected:" + InteractionReachGate.ReasonCode(result.Reason),
                    "主机拒绝了爆炸请求 seq=" +
                    result.Sequence + "（" + pending.Request.Describe() + "）→ " + result.Reason +
                    "；客户端不放本地爆炸");
                return;
            }
            m_resultsAccepted++;
            ExplosionRequestData consumedRequest = pending.Request;
            if (!m_port.TryConsumeRequestItem(in consumedRequest, out string consumeReason))
            {
                m_itemConsumeFailures++;
                LogProblem("item-consume-failed:" + consumeReason,
                    "主机接受了请求 seq=" + result.Sequence + "，但本机扣道具失败（" +
                    consumeReason + "）→ 道具可能未被扣除（明确计数）");
            }
            else
            {
                m_itemConsumed++;
            }
        }

        private void HandleClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            m_lastRequestSequence.Remove(clientId);
            m_recentResults.Remove(clientId);
        }

        private void HandleClientLeft(int clientId, string reason)
        {
            m_lastRequestSequence.Remove(clientId);
            m_recentResults.Remove(clientId);
        }

        private void ExpirePendingRequests(long nowMs)
        {
            if (m_pendingRequests.Count == 0) return;
            List<long> expired = null;
            foreach (KeyValuePair<long, PendingRequest> item in m_pendingRequests)
            {
                if (nowMs - item.Value.SentMs < RequestTimeoutMs) continue;
                (expired ??= new List<long>()).Add(item.Key);
            }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++)
            {
                m_pendingRequests.Remove(expired[i]);
                m_requestTimeouts++;
                LogProblem("request-timeout", "爆炸请求 seq=" + expired[i] +
                    " 超过 " + RequestTimeoutMs + " ms 没有回执 → 放弃（客户端不放本地爆炸）");
            }
        }

        // ------------------------------------------------------------ 主机：客户端请求

        private void OnExplosionRequest(MessageContext context, P.ExplosionRequest message)
        {
            m_requestsReceived++;
            if (m_control?.IsHosting != true)
            {
                m_requestsRejected++;
                LogProblem("request-on-client", "客户端收到了 ExplosionRequest（方向错误）→ 丢弃");
                return;
            }
            if (m_broadcast == null || !m_broadcast.IsPlayerClient(context.ClientId))
            {
                m_requestsRejected++;
                SendResult(context.ClientId, new ExplosionResultData(message.Sequence, false,
                    "not-player-peer", Vector3.Zero));
                LogProblem("request-not-player", "拒绝非玩家端 clientId=" + context.ClientId +
                    " 的爆炸请求 seq=" + message.Sequence + "（not-player-peer）");
                return;
            }
            if (!ExplosionEventCodec.TryDecodeRequest(message, out ExplosionRequestData request,
                    out string decodeReason))
            {
                m_requestsRejected++;
                SendResult(context.ClientId, new ExplosionResultData(message.Sequence, false,
                    "malformed:" + decodeReason, Vector3.Zero));
                LogProblem("request-malformed:" + decodeReason, "爆炸请求解码失败（" + decodeReason +
                    "，clientId=" + context.ClientId + "）→ 拒绝");
                return;
            }
            if (m_lastRequestSequence.TryGetValue(context.ClientId, out long last) &&
                request.Sequence <= last)
            {
                m_requestsDuplicate++;
                if (m_recentResults.TryGetValue(context.ClientId,
                        out Dictionary<long, ExplosionResultData> results) &&
                    results.TryGetValue(request.Sequence, out ExplosionResultData lastResult))
                    SendResult(context.ClientId, lastResult);
                else
                    SendResult(context.ClientId, new ExplosionResultData(request.Sequence, false,
                        "duplicate", Vector3.Zero));
                LogProblem("request-duplicate", "重复的爆炸请求 clientId=" + context.ClientId +
                    " seq=" + request.Sequence + "（上次 " + last + "）→ 回放上次裁决，不重复执行");
                return;
            }
            m_lastRequestSequence[context.ClientId] = request.Sequence;
            bool executed;
            Vector3 resultPosition;
            string reason;
            try
            {
                executed = m_port.TryExecuteRequest(context.ClientId, in request,
                    out resultPosition, out reason);
            }
            catch (Exception e)
            {
                executed = false;
                resultPosition = Vector3.Zero;
                reason = "execution-exception:" + e.GetType().Name;
                LogProblem("request-execute-exception:" + e.GetType().Name,
                    "主机执行爆炸请求 clientId=" + context.ClientId + " " + request.Describe() +
                    " 异常：" + e.GetType().Name + " " + e.Message);
            }
            if (!executed)
            {
                m_requestsRejected++;
                var rejected = new ExplosionResultData(request.Sequence, false, reason,
                    Vector3.Zero);
                RememberResult(context.ClientId, rejected);
                SendResult(context.ClientId, rejected);
                LogProblem("request-rejected:" + InteractionReachGate.ReasonCode(reason),
                    "拒绝爆炸请求 clientId=" +
                    context.ClientId + " " + request.Describe() + " → " + reason);
                return;
            }
            m_requestsAccepted++;
            var accepted = new ExplosionResultData(request.Sequence, true, string.Empty,
                resultPosition);
            RememberResult(context.ClientId, accepted);
            SendResult(context.ClientId, accepted);
        }

        private void RememberResult(int clientId, in ExplosionResultData result)
        {
            if (!m_recentResults.TryGetValue(clientId,
                    out Dictionary<long, ExplosionResultData> results))
            {
                results = new Dictionary<long, ExplosionResultData>();
                m_recentResults[clientId] = results;
            }
            results[result.Sequence] = result;
            if (results.Count <= 8) return;
            long oldest = long.MaxValue;
            foreach (long key in results.Keys) if (key < oldest) oldest = key;
            if (oldest != long.MaxValue) results.Remove(oldest);
        }

        private void SendResult(int clientId, in ExplosionResultData result)
        {
            if (m_messages == null)
            {
                m_resultSendFailures++;
                LogProblem("no-message-layer-result", "无法发送爆炸回执 seq=" + result.Sequence +
                    "（没有消息层）");
                return;
            }
            if (!m_messages.Sender.Send(clientId, ExplosionEventCodec.EncodeResult(in result)))
            {
                m_resultSendFailures++;
                LogProblem("result-send-failed", "爆炸回执 seq=" + result.Sequence +
                    " 发送失败（clientId=" + clientId + "）→ 客户端会按超时处理");
            }
        }

        // ------------------------------------------------------------ 日志 / 摘要

        private void Log(string message) =>
            m_context?.Diagnostics.Info(ModuleId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs) return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }

        private string Summary() => "host executed=" + m_explosionsExecuted +
            " eventsSent=" + m_explosionsSent + "/peers=" + m_explosionsSentPeers +
            " sendFailed=" + m_explosionSendFailures + " noRecipients=" +
            m_explosionsWithoutRecipients + " cosmetic=" + m_cosmeticSent +
            " · client recv=" + m_eventsReceived + " applied=" + m_eventsApplied +
            " deduped=" + m_eventsDeduped + " stale=" + m_eventsOutOfOrder +
            " malformed=" + m_eventsMalformed + " rejected=" + m_eventsRejected +
            " cosmeticPresented=" + m_cosmeticPresented + " ignoredCosmetic=" +
            m_cosmeticPresented + " presentFailed=" + m_presentFailures +
            " · requests submitted=" + m_requestsSubmitted + " sent=" + m_requestsSent +
            " sendFailed=" + m_requestSendFailures + " received=" + m_requestsReceived +
            " accepted=" + m_requestsAccepted + " rejected=" + m_requestsRejected +
            " duplicate=" + m_requestsDuplicate + " timeouts=" + m_requestTimeouts +
            " results=" + m_resultsReceived + "/" + m_resultsAccepted + "/" + m_resultsRejected +
            " unknown=" + m_resultsUnknown + " itemConsumed=" + m_itemConsumed +
            " itemFailed=" + m_itemConsumeFailures + " · damage sent=" + m_playerDamagesSent +
            " recv=" + m_playerDamagesReceived + " presented=" + m_playerDamagesPresented;
    }
}
