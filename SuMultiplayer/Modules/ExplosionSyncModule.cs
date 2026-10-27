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
    ///   · 这里编码 <c>ExplosionEvent</c>，按**客户端兴趣窗口**投递：窗口外扩
    ///     <see cref="ExplosionWindowChunkMargin"/> 环（96 m 视觉 / 听觉半径）包含爆炸点的端才收
    ///     （世界是分区加载的，窗口外的端根本没有这片地形）；
    ///   · 地形仍走既有主机同步（CellChange），不在这里重复；掉落物 / 投射物属于**实体域**，
    ///     由 Modules/EntityReplicationModule.cs（主机权威，32Hz 变化驱动）负责；本模块绝不伪造实体，
    ///     也绝不因为"客户端看不到掉落物"就本地 AddPickable；
    ///   · 玩家受伤由引擎结算，<c>PlayerDamaged</c> 只发给**被伤害玩家自己的端**（最小收件人；
    ///     客户端处理器只记录 / 计数，没有任何观察者可见的表现）；客户端**不**改血量。
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

        /// <summary>爆炸表现（粒子 / 声音 / 震动）需要覆盖的半径：视觉 / 听觉按 96 m 计。</summary>
        private const int ExplosionPresentationRadiusMeters = 96;
        /// <summary>
        /// 兴趣窗口外扩环数 = 96 m ÷ 16 m/环 = 6 环
        /// （<see cref="IWindowBroadcastPort"/> 的约定：1 环 = 1 区块 = 16 m）。
        /// </summary>
        private const int ExplosionWindowChunkMargin = ExplosionPresentationRadiusMeters / 16;

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
        /// <summary>
        /// 主机：把纯表现事件按客户端兴趣窗口投递（定义见 Modules/WindowBroadcastPorts.cs）。
        /// 拿不到时爆炸下发明确失败计数（<c>m_explosionsNoWindowPort</c>），绝不退回"发给全体玩家端"。
        /// </summary>
        private IWindowBroadcastPort m_window;
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
        private long m_explosionsWithoutRecipients;
        /// <summary>没有拿到 IWindowBroadcastPort → 爆炸一次都投不出去（能力缺失，不是"没人需要"）。</summary>
        private long m_explosionsNoWindowPort;
        /// <summary>candidates == 0：此刻没有任何在线玩家端上报过兴趣窗口 → 没有可评估的收件人。</summary>
        private long m_explosionsNoWindowPeers;
        /// <summary>candidates &gt; 0 但入队 0：窗口端全部在 96 m 覆盖范围外（或连接背压拒绝入队）。</summary>
        private long m_explosionsOutOfWindow;
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
        /// <summary>clientId == 0 = 主机本机玩家：没有网络收件人（本机表现由引擎本地完成），不是失败。</summary>
        private long m_playerDamagesLocalHost;
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

        public string StatusLine() => "explosion-sync: " + Summary() + " · " + m_port.Describe();

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
                Log("警告：没有拿到广播端口（IPeerBroadcastPort）→ 主机无法校验爆炸请求的玩家端身份" +
                    "（ExplosionRequest 一律按 not-player-peer 拒绝）");
            }
            if (!context.TryGetPort(out m_window) || m_window == null)
            {
                LogProblem("no-window-port", "警告：没有拿到兴趣窗口广播端口（IWindowBroadcastPort）→ " +
                    "爆炸表现无法按客户端兴趣窗口投递（每次发送计入 explosionsNoWindowPort，" +
                    "绝不退回发给全体玩家端）");
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
                Log("explosion-sync: 连接已建立 → 事件闸门 / 请求表已重置（旧会话的序号不再复用）");
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
            // 收件人 = 兴趣窗口（外扩 ExplosionWindowChunkMargin 环 = 96 m）包含爆炸点的**在线玩家端**。
            // 依据：爆炸在客户端是纯表现（粒子 / 声音 / 震动，见 OnExplosionEvent → IExplosionReplicaPort.Present），
            // 地形 / 伤害已由主机结算并经别的路径同步；世界是分区加载的，窗口外的端根本没有这片地形，
            // 给它播爆炸只会是"凭空出现"的假象。一次编码、端口内部并发入队，不是逐端重编码。
            // 绝不退回 BroadcastToPlayers（那正是本次要消掉的带宽浪费）。
            if (m_window == null)
            {
                m_explosionsWithoutRecipients++;
                m_explosionsNoWindowPort++;
                LogProblem("no-window-port", "主机执行了爆炸 " + explosion.Describe() +
                    "，但没有兴趣窗口广播端口（IWindowBroadcastPort）→ 没有下发目标" +
                    "（计数 explosionsNoWindowPort；不退回全体广播）");
                return;
            }
            byte[] payload = ExplosionEventCodec.Encode(explosion).ToByteArray();
            int peers = m_window.BroadcastToPlayersAt(MessageType.ExplosionEvent, payload,
                explosion.Position, ExplosionWindowChunkMargin, out int candidates);
            if (peers > 0)
            {
                m_explosionsSent++;
                m_explosionsSentPeers += peers;
                return;
            }
            m_explosionsWithoutRecipients++;
            if (candidates <= 0)
            {
                // 不是"没人需要"，而是还没有任何在线玩家端上报过兴趣窗口：不能把它们当成无窗口自由投递。
                m_explosionsNoWindowPeers++;
                LogProblem("no-window-peers", "爆炸事件 " + explosion.Describe() +
                    " 没有投递目标：此刻没有已上报兴趣窗口的在线玩家端（candidates=0）" +
                    "→ 计数 explosionsNoWindowPeers（不退回全体广播）");
                return;
            }
            // candidates > 0：有窗口端，但它的窗口（外扩 6 环后）仍不包含爆炸点 → 它确实"看不到 / 听不到"；
            // 也可能是连接背压让这一帧入队失败（端口只返回入队数，无法在这里区分）。
            m_explosionsOutOfWindow++;
            LogProblem("explosion-out-of-window", "爆炸事件 " + explosion.Describe() + " 有 " +
                candidates + " 个已上报兴趣窗口的端，但入队 0（窗口全部在 " +
                ExplosionPresentationRadiusMeters + " m 覆盖范围外，或连接背压拒绝入队）→ 计数 " +
                "explosionsOutOfWindow（不退回全体广播）");
        }

        private void OnHostPlayerDamaged(PlayerDamageData damage)
        {
            // 收件人 = **被伤害玩家自己的端**（最小集合，点对点），不是全体、也不是观察者。
            // 依据（都读过源码）：
            //   · 客户端处理器 = 本文件 OnPlayerDamaged：只做 方向检查 → 解码 → (server_tick,event_id)
            //     闸门 → IExplosionReplicaPort.PresentPlayerDamage；除此之外唯一的副作用是
            //     damage.ClientId == 本机 LocalClientId 时写一行"我受伤了"的日志（m_loggedSelfDamage）。
            //   · PresentPlayerDamage（Engine/ExplosionReplicaEngine.cs）当前**没有任何视觉 / 声音 / HUD**：
            //     只 +1 计数并返回 true（注释写明"血量同步是后续项"）；PlayerDamageData.Position 在客户端
            //     处理器里也完全没有被使用 → 不存在"附近观察者看到 / 听到别人受伤"的表现。
            //   · Protocol/explosion.proto 的 PlayerDamaged 注释：presentation only；
            //     client_id = "Host-side transport id; 0 = the host player itself"。
            // 所以只发给 ClientId 对应的那一个玩家端；观察者一个都不发（省掉 人数 × 事件数 的无用流量）。
            // 点对点发送不依赖兴趣窗口端口（窗口口径只用于爆炸这种"广播表现"）：IWindowBroadcastPort
            // 缺失只影响爆炸下发（计 explosionsNoWindowPort），PlayerDamaged 仍按本节规则定向送达。
            // ClientId → 连接号的映射：PlayerDamageData.ClientId 由主机侧 ResolveClientId 取自
            // NetworkAvatarSpawner.ClientIds（= 主机通道里的传输编号），与 ITransportPort.TrySendTo 的
            // clientId 同一口径（0 = 主机本机玩家，没有网络收件人）。
            if (damage.ClientId == 0)
            {
                m_playerDamagesLocalHost++;
                LogProblem("damage-local-host", "玩家受伤 clientId=0（主机本机玩家）→ 没有网络收件人" +
                    "（本机表现由引擎本地完成；计数 playerDamagesLocalHost，不是失败）");
                return;
            }
            if (m_transport == null)
            {
                m_playerDamageSendFailures++;
                LogProblem("damage-no-transport", "玩家受伤事件 clientId=" + damage.ClientId +
                    " 无法投递：没有传输端口 → 计数 playerDamageSendFailures");
                return;
            }
            byte[] payload = ExplosionEventCodec.EncodeDamage(damage).ToByteArray();
            if (!m_transport.TrySendTo(damage.ClientId, MessageType.PlayerDamaged, payload))
            {
                m_playerDamageSendFailures++;
                LogProblem("damage-send-failed", "玩家受伤事件 clientId=" + damage.ClientId +
                    " name=" + damage.Name + " damage=" + damage.Damage.ToString("F3") +
                    " 定向发送失败（连接不可达 / 非玩家端 / 背压拒绝入队）→ 计数 " +
                    "playerDamageSendFailures（不退回全体广播）");
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
            " noRecipients=" + m_explosionsWithoutRecipients + " noWindowPort=" +
            m_explosionsNoWindowPort + " noWindowPeers=" + m_explosionsNoWindowPeers +
            " outOfWindow=" + m_explosionsOutOfWindow + " cosmetic=" + m_cosmeticSent +
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
            " sendFailed=" + m_playerDamageSendFailures + " localHost=" +
            m_playerDamagesLocalHost + " recv=" + m_playerDamagesReceived + " presented=" +
            m_playerDamagesPresented;
    }
}
