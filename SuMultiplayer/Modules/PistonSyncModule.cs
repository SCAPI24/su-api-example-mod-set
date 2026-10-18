using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using P = SuMultiplayer.Protocol.Piston;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 活塞运动表现同步（**主机权威，客户端纯表现**）。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/SubsystemPistonBlockBehavior.cs）：
    ///   · 推 / 拉 / 收回的中间过程不是格子数据：MovePiston 往 <c>SubsystemMovingBlocks</c> 里放一个
    ///     Id=="Piston" 的 MovingBlockSet，由 SubsystemMovingBlocks.Update 插值移动、Draw 直接画；
    ///   · 只有运动结束（Stopped → StopPiston）才把最终结果写回格子（走既有 CellChange 下发）；
    ///   · 客户端不跑活塞逻辑（电路已由 SuSubsystemElectricity 挡住）⇒ 中间过程完全看不到。
    ///
    /// 主机侧（本模块 + Engine/PistonReplicaEngine.cs）：
    ///   · 活塞外壳在每次 <c>base.Update</c> 之后比对 MovingBlockSet 集合，把**新出现的** Piston 集合
    ///     连同引擎原始参数（起始 / 目标 / 速度 / 加速度 / 阻力 / 平滑度 / 方块）上报；
    ///   · 同一 tick 的多个活塞先入队，再按 <see cref="PistonMovePolicy.MaxMovesPerMessage"/> 拆批，
    ///     每条消息一个新 event_id；只发给**兴趣窗口覆盖得到**的玩家端（远处端不产生本地集合）；
    ///   · 集合被引擎移除时上报结束（completed=false = 碰撞 / 拆除 / 区块卸载等提前结束），
    ///     结束消息带上对应开始的 event_id，客户端只移除同一次开起的本地动画。
    ///
    /// 客户端侧：
    ///   · 严格解码 → 按 (server_tick, event_id) 排序去重 → 距离 / 区块 / 方块判定 →
    ///     在本地 <c>SubsystemMovingBlocks</c> 里建只表现集合（不写格 / 不生成实体 / 不伤害）；
    ///   · 运动结束由 PistonMoveEnd 移除本地对象；本地动画到点后**继续停在目标位置**直到结束消息
    ///     到达（避免"本地先消失、主机最终格后到"的闪烁）；结束消息迟迟不到由看门狗放弃；
    ///   · 最终格一律取主机的 CellChange，客户端绝不写格。
    ///
    /// 无静态状态；只依赖端口与 Net/PistonNet.cs 的纯逻辑。
    /// </summary>
    public sealed class PistonSyncModule : IMultiplayerModule, ISuMPMessageModule
    {
        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 5000;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IPistonMovePort m_port;
        private readonly PistonMoveOrderer m_orderer = new PistonMoveOrderer();
        private readonly PistonMoveAccumulator m_pendingStarts = new PistonMoveAccumulator();
        private readonly List<PistonMoveEndData> m_pendingEnds =
            new List<PistonMoveEndData>(PistonMovePolicy.MaxMovesPerMessage * 2);
        private readonly Dictionary<MovingBlockKey, uint> m_sentStartIds =
            new Dictionary<MovingBlockKey, uint>();
        private readonly HashSet<int> m_peers = new HashSet<int>();
        private readonly List<int> m_peerScratch = new List<int>();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IPeerBroadcastPort m_broadcast;
        private IMessageRegistryPort m_messages;
        private IPistonPeerViewportPort m_peerView;
        private bool m_attached;
        private bool m_wasConnected;
        private long m_lastSummaryMs;
        private uint m_nextEventId;

        // ---------------------------------------------------------------- 主机计数
        private long m_moveSent;
        private long m_moveCells;
        private long m_peersSent;
        private long m_sendFailed;
        private long m_startMessages;
        private long m_endMessages;
        private long m_noRecipients;
        private long m_skippedPeers;
        private long m_peerUnknownPosition;
        private long m_peerUnknownWindow;
        private long m_peerViewUnavailable;
        private long m_pendingDropped;
        private long m_encodeFailed;
        private long m_hostRejected;
        private long m_hostIdle;
        private long m_endUnsent;

        // ---------------------------------------------------------------- 客户端计数
        private long m_recvMessages;
        private long m_recvMoves;
        private long m_applied;
        private long m_presented;
        private long m_replaced;
        private long m_skippedDistance;
        private long m_skippedCap;
        private long m_skippedChunk;
        private long m_skippedCell;
        private long m_skippedNoPlayer;
        private long m_malformed;
        private long m_duplicate;
        private long m_stale;
        private long m_rejectedDirection;
        private long m_presentFailed;
        private long m_finished;
        private long m_aborted;
        private long m_endUnknown;

        // ---------------------------------------------------------------- 塌落计数（kind=COLLAPSING）
        // 与活塞计数**分开**：pistonMove* 仍然只统计活塞，塌落用 collapsingMove*，
        // 状态行两个口径都写出来（不把两种集合混成一个数字）。
        private long m_collapsingSent;
        private long m_collapsingEndSent;
        private long m_collapsingEndUnsent;
        private long m_collapsingRecv;
        private long m_collapsingEndRecv;
        private long m_collapsingApplied;
        private long m_collapsingPresented;
        private long m_collapsingReplaced;
        private long m_collapsingSkippedDistance;
        private long m_collapsingSkippedCap;
        private long m_collapsingSkippedChunk;
        private long m_collapsingSkippedCell;
        private long m_collapsingSkippedNoPlayer;
        private long m_collapsingPresentFailed;
        private long m_collapsingMalformed;
        private long m_collapsingDuplicate;
        private long m_collapsingStale;
        private long m_collapsingFinished;
        private long m_collapsingAborted;
        private long m_collapsingEndUnknown;
        private long m_collapsingHostRejected;
        private long m_collapsingTarget;

        public PistonSyncModule(IPistonMovePort port)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_descriptor = new ModuleDescriptor("piston-sync",
                "移动组运动表现同步（活塞 / 塌落，主机权威 / 客户端表现）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "piston.presentation" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public string StatusLine() => "移动组运动（活塞 / 塌落）：" + Summary() + " · " +
            m_port.Describe();

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Log("没有拿到传输端口（transport 能力缺失）→ 活塞运动同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Log("警告：没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "活塞运动同步不启用（不猜方向）");
                return;
            }
            if (context.TryGetPort(out m_broadcast) && m_broadcast != null)
            {
                // 只需要 PlayerPeerCount 做诊断（本模块按 clientId 定向发送，不做全量广播）。
            }
            else
            {
                m_broadcast = null;
                Log("警告：没有拿到广播端口（IPeerBroadcastPort）→ 只能按 ClientJoined/ClientLeft 维护玩家端列表" +
                    "（拿不到在线人数诊断）");
            }
            if (context.TryGetPort(out m_messages) && m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Log("警告：没有拿到消息注册表（IMessageRegistryPort）→ 收不到活塞运动消息");
            }
            if (!context.TryGetPort(out m_peerView) || m_peerView == null)
            {
                m_peerView = null;
                Log("警告：没有拿到玩家端视图端口（IPistonPeerViewportPort）→ 无法按距离过滤，" +
                    "所有在线玩家端都会收到活塞运动（客户端仍按可见范围自行过滤并计数）");
            }
            m_port.HostPistonMoveStarted += OnHostMoveStarted;
            m_port.HostPistonMoveEnded += OnHostMoveEnded;
            m_port.ClientMoveAborted += OnClientMoveAborted;
            m_transport.ClientJoined += OnPeerJoined;
            m_transport.ClientLeft += OnPeerLeft;
            m_attached = true;
            Log("移动组运动同步模块就绪（活塞 / 塌落）：主机 = 每次运动开始 / 结束各一条" +
                "（同 tick 合批，按兴趣窗口定向发送）；" +
                "客户端 = 解码 / 排序去重 / 距离判定后在本地 SubsystemMovingBlocks 建只表现集合，" +
                "结束消息移除（不写格 / 不生成实体）");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.PistonMoveEvent, P.PistonMoveEvent.Parser,
                new DelegateMessageHandler<P.PistonMoveEvent>(OnPistonMoveEvent), ModuleId);
            registry.Register((int)MessageType.PistonMoveEnd, P.PistonMoveEnd.Parser,
                new DelegateMessageHandler<P.PistonMoveEnd>(OnPistonMoveEnd), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            m_port.HostPistonMoveStarted -= OnHostMoveStarted;
            m_port.HostPistonMoveEnded -= OnHostMoveEnded;
            m_port.ClientMoveAborted -= OnClientMoveAborted;
            if (m_transport != null)
            {
                try { m_transport.ClientJoined -= OnPeerJoined; } catch (Exception) { }
                try { m_transport.ClientLeft -= OnPeerLeft; } catch (Exception) { }
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
                m_messages = null;
            }
            int aborted = m_port.AbortAll("detach", out int pistonAborted,
                out int collapsingAborted);
            m_aborted += pistonAborted;
            m_collapsingAborted += collapsingAborted;
            m_pendingStarts.Reset();
            m_pendingEnds.Clear();
            m_sentStartIds.Clear();
            m_peers.Clear();
            m_orderer.Reset();
            m_lastProblemMs.Clear();
            m_attached = false;
            Log("移动组运动同步模块下线（" + reason + "，放弃本地动画 " + aborted + " 条：活塞 " +
                pistonAborted + " / 塌落 " + collapsingAborted + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            m_port.Pump();
            bool connected = m_transport != null && m_transport.IsConnected;
            if (connected && !m_wasConnected)
            {
                m_orderer.Reset();
                Log("活塞运动：连接已建立 → 事件闸门已重置（旧会话的 event_id 不再复用）");
            }
            else if (!connected && m_wasConnected)
            {
                int aborted = m_port.AbortAll("disconnected", out int pistonAborted,
                    out int collapsingAborted);
                m_aborted += pistonAborted;
                m_collapsingAborted += collapsingAborted;
                m_peers.Clear();
                m_sentStartIds.Clear();
                m_pendingStarts.Reset();
                m_pendingEnds.Clear();
                Log("移动组运动：连接已断开 → 放弃 " + aborted + " 条本地动画（活塞 " +
                    pistonAborted + " / 塌落 " + collapsingAborted + "）并清空待发送队列" +
                    "（最终状态等重新连上后由主机 CellChange 覆盖）");
            }
            m_wasConnected = connected;
            if (connected && m_control?.IsHosting == true) FlushHostQueues();
            long nowMs = (long)(tick.Now * 1000.0);
            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                Log(Summary() + " · " + m_port.Describe());
            }
        }

        // ------------------------------------------------------------ 玩家端列表

        private void OnPeerJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (clientId <= 0) return;
            if (m_peers.Add(clientId))
                Log("活塞运动：玩家端 clientId=" + clientId + "（" + name + "）已加入 → 它开始收活塞运动"
                    + (rejoin ? "（重连）" : ""));
        }

        private void OnPeerLeft(int clientId, string reason)
        {
            m_peers.Remove(clientId);
            // 不在这里清 m_sentStartIds：已经发出去的运动（其它玩家端持有的那条）仍然要有对应的结束消息。
            if (m_control?.IsHosting != true) return;
            Log("活塞运动：玩家端 clientId=" + clientId + " 离开（" + reason + "）→ 不再向它发送" +
                "（它的本地动画在它那一端按看门狗 / 断线放弃）");
        }

        // ------------------------------------------------------------ 主机：发送

        private void OnHostMoveStarted(PistonMoveData move)
        {
            if (m_control?.IsHosting != true)
            {
                if (m_port.IsClientReplica)
                {
                    if (move.Kind == MovingBlockKind.Collapsing) m_collapsingHostRejected++;
                    else m_hostRejected++;
                    LogProblem("start-on-client", "客户端副本上的" +
                        (move.Kind == MovingBlockKind.Collapsing ? "塌落" : "活塞") +
                        "外壳上报了运动开始（" + move.Describe() + "）→ 丢弃（客户端绝不广播）");
                }
                else
                {
                    m_hostIdle++;
                }
                return;
            }
            if (!m_pendingStarts.TryAdd(in move))
            {
                m_pendingDropped++;
                LogProblem("pending-full", "移动组运动待发送队列已满（上限 " +
                    PistonMovePolicy.MaxPendingMoves + "）→ 丢弃 " + move.Describe() +
                    "（累计 " + m_pendingDropped + " 条，明确计数不静默）");
            }
        }

        private void OnHostMoveEnded(PistonMoveEndData end)
        {
            if (m_control?.IsHosting != true) return;
            if (!m_sentStartIds.TryGetValue(end.Key, out uint startId))
            {
                // 这次开始没有被任何玩家端收到（无在线端 / 全部被距离过滤）→ 没有客户端持有这条动画。
                if (end.Kind == MovingBlockKind.Collapsing) m_collapsingEndUnsent++;
                else m_endUnsent++;
                return;
            }
            m_sentStartIds.Remove(end.Key);
            if (m_pendingEnds.Count >= PistonMovePolicy.MaxPendingMoves)
            {
                m_pendingDropped++;
                LogProblem("pending-end-full", "移动组结束待发送队列已满（上限 " +
                    PistonMovePolicy.MaxPendingMoves + "）→ 丢弃 " + end.Describe() +
                    "（累计 " + m_pendingDropped + " 条，明确计数不静默）");
                return;
            }
            m_pendingEnds.Add(new PistonMoveEndData(end.Kind, end.X, end.Y, end.Z, end.Completed,
                end.Reason, startId));
        }

        private void FlushHostQueues()
        {
            while (m_pendingStarts.Count > 0)
            {
                PistonMoveData[] moves = m_pendingStarts.Take(PistonMovePolicy.MaxMovesPerMessage);
                if (moves.Length == 0) break;
                uint eventId = NextEventId();
                var batch = new PistonMoveStartBatch(eventId, NowTick(), moves);
                if (!PistonMoveCodec.TryEncodeStart(in batch, out P.PistonMoveEvent message,
                        out string reason))
                {
                    m_encodeFailed++;
                    m_sendFailed++;
                    LogProblem("encode-start:" + reason, "PistonMoveEvent 无法编码（" + reason +
                        "，本批 " + moves.Length + " 条）→ 本批不下发（不会静默）");
                    continue;
                }
                int cells = 0;
                int collapsingMoves = 0;
                for (int i = 0; i < moves.Length; i++)
                {
                    cells += moves[i].Blocks.Length;
                    if (moves[i].Kind == MovingBlockKind.Collapsing) collapsingMoves++;
                }
                int peers = SendToPeers(MessageType.PistonMoveEvent, message.ToByteArray(), moves);
                m_startMessages++;
                m_moveSent += moves.Length - collapsingMoves;
                m_collapsingSent += collapsingMoves;
                m_moveCells += cells;
                m_peersSent += peers;
                if (peers <= 0) continue;
                for (int i = 0; i < moves.Length; i++)
                    m_sentStartIds[moves[i].Key] = eventId;
                if (m_sentStartIds.Count > 4096)
                {
                    // 正常不会到这里（每条开始都会在集合被移除时收到结束）；上限只防"结束永远
                    // 不来"的异常积累。清空后这些运动的结束会按 endUnsent 计数，客户端由看门狗收尾。
                    m_sentStartIds.Clear();
                    LogProblem("sent-tracking-cap", "活塞运动开始记录表超过 4096 条（结束消息长期缺失？）" +
                        "→ 已清空，后续结束按 pistonMoveEndUnsent 计数（客户端由看门狗移除本地动画）");
                }
            }
            m_pendingStarts.Reset();
            while (m_pendingEnds.Count > 0)
            {
                int take = Math.Min(m_pendingEnds.Count, PistonMovePolicy.MaxMovesPerMessage);
                var ends = new PistonMoveEndData[take];
                for (int i = 0; i < take; i++) ends[i] = m_pendingEnds[i];
                m_pendingEnds.RemoveRange(0, take);
                uint eventId = NextEventId();
                var batch = new PistonMoveEndBatch(eventId, NowTick(), ends);
                if (!PistonMoveCodec.TryEncodeEnd(in batch, out P.PistonMoveEnd message,
                        out string reason))
                {
                    m_encodeFailed++;
                    m_sendFailed++;
                    LogProblem("encode-end:" + reason, "PistonMoveEnd 无法编码（" + reason +
                        "，本批 " + ends.Length + " 条）→ 本批不下发（客户端由看门狗放弃本地动画）");
                    continue;
                }
                int peers = SendToPeers(MessageType.PistonMoveEnd, message.ToByteArray(), null);
                m_endMessages++;
                m_peersSent += peers;
                for (int i = 0; i < ends.Length; i++)
                {
                    if (ends[i].Kind == MovingBlockKind.Collapsing) m_collapsingEndSent++;
                }
            }
        }

        private int SendToPeers(MessageType type, byte[] payload, PistonMoveData[] moves)
        {
            if (m_peers.Count == 0)
            {
                m_noRecipients++;
                LogProblem("no-peers", "活塞运动消息没有在线玩家端（" + type +
                    "，本批 " + (moves == null ? 0 : moves.Length) + " 条）→ 没有下发目标" +
                    "（累计 " + m_noRecipients + " 条，明确计数；广播端在线玩家数=" +
                    (m_broadcast == null ? -1 : m_broadcast.PlayerPeerCount) + "）");
                return 0;
            }
            m_peerScratch.Clear();
            foreach (int clientId in m_peers) m_peerScratch.Add(clientId);
            int sent = 0;
            for (int i = 0; i < m_peerScratch.Count; i++)
            {
                int clientId = m_peerScratch[i];
                if (!m_transport.IsClientSendable(clientId))
                {
                    m_sendFailed++;
                    LogProblem("not-sendable:" + clientId, "玩家端 clientId=" + clientId +
                        " 此刻不可发（断线 / 队列满）→ 活塞运动 " + type + " 没发给它" +
                        "（累计 sendFailed=" + m_sendFailed + "）");
                    continue;
                }
                if (moves != null && !PeerInRange(clientId, moves))
                {
                    m_skippedPeers++;
                    LogProblem("peer-far", "玩家端 clientId=" + clientId +
                        " 的兴趣窗口覆盖不到本批活塞（" + moves.Length + " 条）→ 不发给它" +
                        "（累计 skippedPeers=" + m_skippedPeers + "；它在远处看不到这些活塞）");
                    continue;
                }
                if (!m_transport.TrySendTo(clientId, type, payload))
                {
                    m_sendFailed++;
                    LogProblem("send-failed:" + clientId, "活塞运动 " + type +
                        " 发送失败（clientId=" + clientId + "）→ 该端不会有这段动画" +
                        "（累计 sendFailed=" + m_sendFailed + "）");
                    continue;
                }
                sent++;
            }
            return sent;
        }

        /// <summary>
        /// 这个玩家端要不要收这批活塞运动：只有兴趣窗口覆盖得到时才发（远处端不产生本地集合）。
        /// 读不到玩家端位置 / 兴趣窗口时**发**（客户端自己按可见范围过滤并计数），并明确计数。
        /// </summary>
        private bool PeerInRange(int clientId, PistonMoveData[] moves)
        {
            if (m_peerView == null)
            {
                m_peerViewUnavailable++;
                return true;
            }
            if (!m_peerView.TryGetPeerView(clientId, out PistonPeerView view))
            {
                m_peerViewUnavailable++;
                LogProblem("peer-view:" + clientId, "读不到玩家端 clientId=" + clientId +
                    " 的位置 / 兴趣窗口 → 本批照发（客户端自行过滤）" +
                    "（累计 peerViewUnavailable=" + m_peerViewUnavailable + "）");
                return true;
            }
            if (!view.HasPosition)
            {
                m_peerUnknownPosition++;
                LogProblem("peer-no-position:" + clientId, "玩家端 clientId=" + clientId +
                    " 的联机角色位置还读不到（" + view.Reason + "）→ 本批照发（客户端自行过滤）" +
                    "（累计 peerUnknownPosition=" + m_peerUnknownPosition + "）");
                return true;
            }
            if (!view.HasInterestWindow)
            {
                m_peerUnknownWindow++;
                LogProblem("peer-no-window:" + clientId, "玩家端 clientId=" + clientId +
                    " 还没上报兴趣窗口 → 本批照发（客户端自行过滤）" +
                    "（累计 peerUnknownWindow=" + m_peerUnknownWindow + "）");
                return true;
            }
            for (int i = 0; i < moves.Length; i++)
            {
                if (PistonPresentationPolicy.IsPeerInRange(in moves[i], view.Position,
                        view.RadiusChunks, out _))
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------ 客户端：接收

        private void OnPistonMoveEvent(MessageContext context, P.PistonMoveEvent message)
        {
            m_recvMessages++;
            if (m_control?.IsHosting == true)
            {
                m_rejectedDirection++;
                LogProblem("start-on-host", "主机收到了 PistonMoveEvent（方向错误，clientId=" +
                    context.ClientId + "）→ 丢弃");
                return;
            }
            bool mentionsCollapsing = MentionsCollapsing(message);
            if (!PistonMoveCodec.TryDecodeStart(message, out PistonMoveStartBatch batch,
                    out string reason))
            {
                m_malformed++;
                if (mentionsCollapsing) m_collapsingMalformed++;
                LogProblem("start-malformed:" + reason, "PistonMoveEvent 解码失败（" + reason +
                    "，clientId=" + context.ClientId +
                    (mentionsCollapsing ? "，含塌落运动" : "") + "）→ 整条丢弃");
                return;
            }
            if (!m_orderer.TryAccept(batch.EventId, batch.ServerTick, out string orderReason))
            {
                if (orderReason == "duplicate")
                {
                    m_duplicate++;
                    if (mentionsCollapsing) m_collapsingDuplicate++;
                    LogProblem("start-duplicate", "重复的 PistonMoveEvent " + batch.Describe() +
                        " → 只表现一次（deduped）");
                }
                else
                {
                    m_stale++;
                    if (mentionsCollapsing) m_collapsingStale++;
                    LogProblem("start-stale", "过期 / 越序的 PistonMoveEvent " + batch.Describe() +
                        "（last tick=" + m_orderer.LastTick + " id=" + m_orderer.LastEventId +
                        "）→ 丢弃");
                }
                return;
            }
            for (int i = 0; i < batch.Moves.Length; i++)
            {
                PistonMoveData move = batch.Moves[i];
                bool collapsing = move.Kind == MovingBlockKind.Collapsing;
                if (collapsing) m_collapsingRecv++;
                else m_recvMoves++;
                PistonPresentOutcome outcome = m_port.Present(batch.EventId, in move,
                    out string presentReason);
                switch (outcome)
                {
                    case PistonPresentOutcome.Presented:
                        if (collapsing)
                        {
                            m_collapsingApplied++;
                            m_collapsingPresented++;
                        }
                        else
                        {
                            m_applied++;
                            m_presented++;
                        }
                        break;
                    case PistonPresentOutcome.Replaced:
                        if (collapsing)
                        {
                            m_collapsingApplied++;
                            m_collapsingPresented++;
                            m_collapsingReplaced++;
                        }
                        else
                        {
                            m_applied++;
                            m_presented++;
                            m_replaced++;
                        }
                        break;
                    case PistonPresentOutcome.SkippedDistance:
                        if (collapsing)
                        {
                            m_collapsingApplied++;
                            m_collapsingSkippedDistance++;
                        }
                        else
                        {
                            m_applied++;
                            m_skippedDistance++;
                        }
                        break;
                    case PistonPresentOutcome.SkippedCap:
                        if (collapsing)
                        {
                            m_collapsingApplied++;
                            m_collapsingSkippedCap++;
                            LogProblem("present-cap-collapsing", "本地表现集合已达上限 " +
                                PistonMovePolicy.MaxConcurrentLocalMoves + " → 跳过塌落 " +
                                move.Describe() + "（累计 collapsingSkippedCap=" +
                                m_collapsingSkippedCap + "）");
                        }
                        else
                        {
                            m_applied++;
                            m_skippedCap++;
                            LogProblem("present-cap", "本地活塞表现集合已达上限 " +
                                PistonMovePolicy.MaxConcurrentLocalMoves + " → 跳过 " +
                                move.Describe() + "（累计 skippedCap=" + m_skippedCap + "）");
                        }
                        break;
                    case PistonPresentOutcome.SkippedChunk:
                        if (collapsing)
                        {
                            m_collapsingApplied++;
                            m_collapsingSkippedChunk++;
                            LogProblem("present-chunk-collapsing", "塌落列所在区块未加载 / " +
                                "内容未定稿 → 跳过 " + move.Describe() +
                                "（累计 collapsingSkippedChunk=" + m_collapsingSkippedChunk + "）");
                        }
                        else
                        {
                            m_applied++;
                            m_skippedChunk++;
                            LogProblem("present-chunk", "活塞所在区块未加载 / 内容未定稿 → 跳过 " +
                                move.Describe() + "（累计 skippedChunk=" + m_skippedChunk + "）");
                        }
                        break;
                    case PistonPresentOutcome.SkippedCellMismatch:
                        if (collapsing)
                        {
                            m_collapsingApplied++;
                            m_collapsingSkippedCell++;
                            LogProblem("present-cell-collapsing", "本地起点格既不是空格（主机已清源格）" +
                                "也不是沙子 / 沙砾（主机的格变化还没到 / 世界不一致）→ 跳过 " +
                                move.Describe() + "（累计 collapsingSkippedCell=" +
                                m_collapsingSkippedCell + "）");
                        }
                        else
                        {
                            m_applied++;
                            m_skippedCell++;
                            LogProblem("present-cell", "本地该格不是活塞方块（主机的格变化还没到 / " +
                                "世界不一致）→ 跳过 " + move.Describe() + "（累计 skippedCell=" +
                                m_skippedCell + "）");
                        }
                        break;
                    case PistonPresentOutcome.SkippedNoPlayer:
                        if (collapsing)
                        {
                            m_collapsingApplied++;
                            m_collapsingSkippedNoPlayer++;
                        }
                        else
                        {
                            m_applied++;
                            m_skippedNoPlayer++;
                        }
                        LogProblem("present-no-player", "本机玩家 / 身体还没就绪 → 跳过 " +
                            move.Describe() + "（累计 skippedNoPlayer=" + m_skippedNoPlayer +
                            " / collapsingSkippedNoPlayer=" + m_collapsingSkippedNoPlayer + "）");
                        break;
                    default:
                        if (collapsing)
                        {
                            m_collapsingPresentFailed++;
                            LogProblem("present-failed-collapsing:" + presentReason,
                                "塌落表现失败 " + move.Describe() + "（" + presentReason +
                                "）→ 这次没有画面（累计 collapsingPresentFailed=" +
                                m_collapsingPresentFailed + "）");
                        }
                        else
                        {
                            m_presentFailed++;
                            LogProblem("present-failed:" + presentReason, "活塞表现失败 " +
                                move.Describe() + "（" + presentReason +
                                "）→ 这次没有画面（明确计数，不静默）");
                        }
                        break;
                }
            }
        }

        private void OnPistonMoveEnd(MessageContext context, P.PistonMoveEnd message)
        {
            m_recvMessages++;
            if (m_control?.IsHosting == true)
            {
                m_rejectedDirection++;
                LogProblem("end-on-host", "主机收到了 PistonMoveEnd（方向错误，clientId=" +
                    context.ClientId + "）→ 丢弃");
                return;
            }
            bool mentionsCollapsing = MentionsCollapsing(message);
            if (!PistonMoveCodec.TryDecodeEnd(message, out PistonMoveEndBatch batch,
                    out string reason))
            {
                m_malformed++;
                if (mentionsCollapsing) m_collapsingMalformed++;
                LogProblem("end-malformed:" + reason, "PistonMoveEnd 解码失败（" + reason +
                    "，clientId=" + context.ClientId +
                    (mentionsCollapsing ? "，含塌落运动" : "") + "）→ 整条丢弃");
                return;
            }
            if (!m_orderer.TryAccept(batch.EventId, batch.ServerTick, out string orderReason))
            {
                if (orderReason == "duplicate")
                {
                    m_duplicate++;
                    if (mentionsCollapsing) m_collapsingDuplicate++;
                    LogProblem("end-duplicate", "重复的 PistonMoveEnd " + batch.Describe() +
                        " → 只应用一次（deduped）");
                }
                else
                {
                    m_stale++;
                    if (mentionsCollapsing) m_collapsingStale++;
                    LogProblem("end-stale", "过期 / 越序的 PistonMoveEnd " + batch.Describe() +
                        "（last tick=" + m_orderer.LastTick + " id=" + m_orderer.LastEventId +
                        "）→ 丢弃");
                }
                return;
            }
            for (int i = 0; i < batch.Moves.Length; i++)
            {
                PistonMoveEndData end = batch.Moves[i];
                bool collapsing = end.Kind == MovingBlockKind.Collapsing;
                if (collapsing) m_collapsingEndRecv++;
                if (!m_port.Cancel(in end, out bool found, out string cancelReason))
                {
                    if (collapsing) m_collapsingPresentFailed++;
                    else m_presentFailed++;
                    LogProblem("cancel-failed:" + cancelReason, "移除本地运动动画失败 " +
                        end.Describe() + "（" + cancelReason + "）→ 本地对象可能残留" +
                        "（明确计数，不静默）");
                    continue;
                }
                if (!found)
                {
                    if (collapsing) m_collapsingEndUnknown++;
                    else m_endUnknown++;
                    LogProblem("end-unknown", "PistonMoveEnd 指向的本地动画不存在 " +
                        end.Describe() + "（当时因距离 / 区块跳过，或已被新一轮替换）→ 忽略" +
                        "（累计 endUnknown=" + m_endUnknown + " / collapsingEndUnknown=" +
                        m_collapsingEndUnknown + "）");
                    continue;
                }
                if (collapsing)
                {
                    // 塌落：completed=true 才是异常（整列没撞到任何方块就掉到目标点）；
                    // completed=false（撞支撑方块落地，reason=landed）是引擎的正常结束。
                    m_collapsingFinished++;
                    if (end.Completed)
                    {
                        m_collapsingTarget++;
                        LogProblem("collapsing-target", "主机报告塌落列掉到目标点（没有撞到任何方块）：" +
                            end.Describe() + " → 本地已移除表现集合" +
                            "（累计 collapsingMoveTarget=" + m_collapsingTarget + "）");
                    }
                }
                else if (end.Completed)
                {
                    m_finished++;
                }
                else
                {
                    m_aborted++;
                }
            }
        }

        private void OnClientMoveAborted(MovingBlockKind kind, Point3 cell, string reason)
        {
            if (kind == MovingBlockKind.Collapsing) m_collapsingAborted++;
            else m_aborted++;
            LogProblem("abort:" + reason, "本地" +
                (kind == MovingBlockKind.Collapsing ? "塌落" : "活塞") + "动画被放弃（格 " +
                cell.X + "," + cell.Y + "," + cell.Z + "，原因 " + reason + "；累计 aborted=" +
                m_aborted + " / collapsingAborted=" + m_collapsingAborted + "）");
        }

        /// <summary>
        /// 这条消息里是否**可能**含塌落运动：显式 kind=COLLAPSING，或未知 kind 数值
        /// （无法判断，按塌落口径也计一次并写进日志，避免"畸形消息里的塌落被算成活塞"）。
        /// 只用于计数归属与日志，不参与解码（解码在 TryDecodeStart / TryDecodeEnd 里整条拒绝）。
        /// </summary>
        private static bool MentionsCollapsing(P.PistonMoveEvent message)
        {
            if (message == null) return false;
            for (int i = 0; i < message.Moves.Count; i++)
            {
                P.PistonMove move = message.Moves[i];
                if (move == null) continue;
                int wire = (int)move.Kind;
                if (wire != 0 && wire != (int)P.MovingBlockKind.Piston) return true;
            }
            return false;
        }

        private static bool MentionsCollapsing(P.PistonMoveEnd message)
        {
            if (message == null) return false;
            for (int i = 0; i < message.Moves.Count; i++)
            {
                P.PistonMoveEndEntry move = message.Moves[i];
                if (move == null) continue;
                int wire = (int)move.Kind;
                if (wire != 0 && wire != (int)P.MovingBlockKind.Piston) return true;
            }
            return false;
        }

        // ------------------------------------------------------------ 日志 / 摘要

        private uint NextEventId()
        {
            uint id = ++m_nextEventId;
            if (id == 0) id = ++m_nextEventId;
            return id;
        }

        private long NowTick() => (long)Time.FrameIndex;

        private void Log(string message) => m_context?.Diagnostics.Info(ModuleId, message);

        private void LogProblem(string key, string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs) return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }

        /// <summary>
        /// 一行可审计的计数。主机：pistonMoveSent（运动条数）/ pistonMoveCells（方块数）/
        /// pistonMovePeers（成功入队的目标端次数）/ pistonMoveSendFailed；
        /// 客户端：pistonMoveRecv（消息条数）/ pistonMoveApplied / pistonMovePresented /
        /// pistonMoveSkippedDistance / pistonMoveMalformed / pistonMoveFinished / pistonMoveAborted。
        /// </summary>
        private string Summary() => "host pistonMoveSent=" + m_moveSent +
            " pistonMoveCells=" + m_moveCells +
            " pistonMovePeers=" + m_peersSent +
            " pistonMoveSendFailed=" + m_sendFailed +
            " pistonMoveStartMessages=" + m_startMessages +
            " pistonMoveEndMessages=" + m_endMessages +
            " pistonMoveNoRecipients=" + m_noRecipients +
            " pistonMoveSkippedPeers=" + m_skippedPeers +
            " pistonMovePeerViewUnknown=" +
            (m_peerViewUnavailable + m_peerUnknownPosition + m_peerUnknownWindow) +
            " pistonMovePendingDropped=" + m_pendingDropped +
            " pistonMoveEncodeFailed=" + m_encodeFailed +
            " pistonMoveHostRejected=" + m_hostRejected +
            " pistonMoveHostIdle=" + m_hostIdle +
            " pistonMoveEndUnsent=" + m_endUnsent +
            " · client pistonMoveRecv=" + m_recvMessages +
            " pistonMoveMoves=" + m_recvMoves +
            " pistonMoveApplied=" + m_applied +
            " pistonMovePresented=" + m_presented +
            " pistonMoveReplaced=" + m_replaced +
            " pistonMoveSkippedDistance=" + m_skippedDistance +
            " pistonMoveSkippedCap=" + m_skippedCap +
            " pistonMoveSkippedChunk=" + m_skippedChunk +
            " pistonMoveSkippedCell=" + m_skippedCell +
            " pistonMoveSkippedNoPlayer=" + m_skippedNoPlayer +
            " pistonMoveMalformed=" + m_malformed +
            " pistonMoveDuplicate=" + m_duplicate +
            " pistonMoveStale=" + m_stale +
            " pistonMoveRejected=" + m_rejectedDirection +
            " pistonMovePresentFailed=" + m_presentFailed +
            " pistonMoveFinished=" + m_finished +
            " pistonMoveAborted=" + m_aborted +
            " pistonMoveEndUnknown=" + m_endUnknown +
            // 塌落（kind=COLLAPSING）：与活塞同口径的独立计数，绝不混进 pistonMove*。
            " · collapsingMoveSent=" + m_collapsingSent +
            " collapsingMoveEndSent=" + m_collapsingEndSent +
            " collapsingMoveEndUnsent=" + m_collapsingEndUnsent +
            " collapsingMoveHostRejected=" + m_collapsingHostRejected +
            " · client collapsingMoveRecv=" + m_collapsingRecv +
            " collapsingMoveEndRecv=" + m_collapsingEndRecv +
            " collapsingMoveApplied=" + m_collapsingApplied +
            " collapsingMovePresented=" + m_collapsingPresented +
            " collapsingMoveReplaced=" + m_collapsingReplaced +
            " collapsingMoveSkippedDistance=" + m_collapsingSkippedDistance +
            " collapsingMoveSkippedCap=" + m_collapsingSkippedCap +
            " collapsingMoveSkippedChunk=" + m_collapsingSkippedChunk +
            " collapsingMoveSkippedCell=" + m_collapsingSkippedCell +
            " collapsingMoveSkippedNoPlayer=" + m_collapsingSkippedNoPlayer +
            " collapsingMoveMalformed=" + m_collapsingMalformed +
            " collapsingMoveDuplicate=" + m_collapsingDuplicate +
            " collapsingMoveStale=" + m_collapsingStale +
            " collapsingMovePresentFailed=" + m_collapsingPresentFailed +
            " collapsingMoveFinished=" + m_collapsingFinished +
            " collapsingMoveAborted=" + m_collapsingAborted +
            " collapsingMoveEndUnknown=" + m_collapsingEndUnknown +
            " collapsingMoveTarget=" + m_collapsingTarget;
    }
}
