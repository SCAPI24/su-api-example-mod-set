using System;
using System.Collections.Generic;
using Engine;
using SuMultiplayer.Net;
using SuMultiplayer.Protocol;
using EngineRandom = global::Engine.Random;
using PM = SuMultiplayer.Protocol.Melee;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 主机权威近战攻击模块（**主机决定命中 / 伤害 / 剩余生命**，客户端只做预测表现）。
    ///
    /// 主机侧：
    ///   · 收到客户端的 <c>MeleeAttackRequest</c> → <see cref="IMeleeWorldPort.TryExecuteAuthoritative"/>
    ///     （解析权威攻击者 / 目标 → 距离 / 锥角 / 遮挡 / 冷却 → 调**一次**引擎
    ///     <c>ComponentMiner.Hit</c> → 用玩家统计的 MeleeHits 增量取引擎自己的命中判定）；
    ///   · 主机自己角色的攻击走同一条裁决（不另开一套规则），同样广播结果；
    ///   · 结果 <c>MeleeAttackResult</c> 广播给**全部玩家端**（含攻击者；观测端按同一份
    ///     落点 / 效果位播命中音 / 伤害数字 / Miss 标记）。
    ///
    /// 客户端侧：
    ///   · 攻击输入被 <c>IHitInputSink</c> 吞掉（原生攻击分支不执行：**不 roll、不扣血**）；
    ///   · 立刻展示乐观预测（挥手 / 挥击声 / 红色命中标记），然后上行请求；
    ///   · 收到结果：命中 → 确认预测 + 用主机给的伤害数字；未命中 / 拒绝 → **显式回滚**
    ///     （删掉预测粒子、不播命中音、按原因码计数 + 限流日志）。
    ///
    /// 距离：allowed = min(请求 reach + 0.5, 主机 meleereachmax, 主机引擎 reach)；
    /// 客户端预检与主机裁决调用同一个 <see cref="MeleeReachRule"/>（结果里回传主机上限，
    /// 客户端下一次预检即用同一组值）。客户端预检认为够不到时只发**空气挥击**并本地按
    /// 未命中展示；主机拒绝 <c>out-of-reach</c> 时客户端同样按未命中收尾（两条路径都计数）。
    ///
    /// 无静态状态：端口 / 传输 / 计数全部是实例字段，随模块生命周期创建 / 清空。
    /// </summary>
    public sealed class MeleeSyncModule : IMultiplayerModule, ISuMPMessageModule
    {
        public const string ModuleIdText = "melee-sync";

        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 5000;
        private const int MaxPendingLocalAttacks = 64;

        private readonly ModuleDescriptor m_descriptor;
        private readonly IMeleeWorldPort m_port;
        private readonly MeleeResultDedupe m_dedupe = new MeleeResultDedupe();
        private readonly Queue<MeleeLocalAttack> m_localAttacks = new Queue<MeleeLocalAttack>();
        private readonly Dictionary<string, long> m_rejectedByReason =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        // ---- 共享战斗随机流（主机每玩家一条；客户端镜像自己的槽位）----
        private readonly CombatRngTelemetry m_combatTelemetry = new CombatRngTelemetry();
        private readonly EngineRandom m_seedRandom = new EngineRandom();
        private readonly HashSet<int> m_pendingSync = new HashSet<int>();
        private CombatRngHostState m_hostRng;
        private CombatRngClientState m_clientRng;
        private Action<int, string, string, bool> m_clientJoinedHandler;
        private Action<int, string> m_clientLeftHandler;
        private int m_lastLocalClientId = int.MinValue;

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IPeerBroadcastPort m_broadcast;
        private IMessageRegistryPort m_messages;
        private bool m_attached;
        private long m_nextSequence;
        private long m_lastSummaryMs;

        // ---------------------------------------------------------------- 计数（状态行可审计）
        private long m_requestsSent;
        private long m_requestSendFailures;
        private long m_requestsReceived;
        private long m_requestsMalformed;
        private long m_requestsFromNonPlayer;
        private long m_requestsOnClient;
        private long m_hostExecuted;
        private long m_hostExecuteFailures;
        private long m_accepted;
        private long m_rejected;
        private long m_hits;
        private long m_misses;
        private long m_airSwings;
        private long m_resultsSent;
        private long m_resultsSentPeers;
        private long m_resultEncodeFailures;
        private long m_resultsReceived;
        private long m_resultDecodeFailures;
        private long m_resultsOnHost;
        private long m_resultsAccepted;
        private long m_resultsRejected;
        private long m_resultsDuplicate;
        private long m_resultApplyFailures;
        private long m_observed;
        private long m_localOverflow;
        private long m_predictionNotSent;
        private long m_outOfReach;
        private long m_cooldown;
        private long m_targetMissing;

        public MeleeSyncModule(IMeleeWorldPort port, Func<double> now)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            Now = now ?? throw new ArgumentNullException(nameof(now));
            m_descriptor = new ModuleDescriptor(ModuleIdText,
                "近战攻击（主机裁决命中 / 伤害，客户端只做预测表现并回滚）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "player.melee" },
                HotPluggable = true
            };
        }

        /// <summary>时间源（毫秒）；测试可注入，生产 = Time.RealTime。</summary>
        public Func<double> Now { get; }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => ModuleIdText;

        public long RequestsSent => m_requestsSent;
        public long RequestsReceived => m_requestsReceived;
        public long Accepted => m_accepted;
        public long Rejected => m_rejected;
        public long Hits => m_hits;
        public long Misses => m_misses;
        public long Observed => m_observed;
        public long ResultsReceived => m_resultsReceived;
        public long ResultsAccepted => m_resultsAccepted;
        public long ResultsRejected => m_resultsRejected;
        public long ResultsDuplicate => m_resultsDuplicate;
        public long ResultDecodeFailures => m_resultDecodeFailures;
        public long RequestsMalformed => m_requestsMalformed;

        public string RejectedReasonDetail()
        {
            if (m_rejectedByReason.Count == 0) return "none";
            var builder = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, long> pair in m_rejectedByReason)
            {
                if (builder.Length > 0) builder.Append(',');
                builder.Append(pair.Key).Append('=').Append(pair.Value);
            }
            return builder.ToString();
        }

        public string Summary() => "meleeReqSent=" + m_requestsSent +
            " meleeReqSendFailed=" + m_requestSendFailures +
            " meleeReqRecv=" + m_requestsReceived +
            " meleeReqMalformed=" + m_requestsMalformed +
            " meleeReqFromNonPlayer=" + m_requestsFromNonPlayer +
            " meleeHostExecuted=" + m_hostExecuted +
            " meleeHostFailed=" + m_hostExecuteFailures +
            " meleeAccepted=" + m_accepted +
            " meleeRejected=" + m_rejected +
            " meleeRejected(" + RejectedReasonDetail() + ")" +
            " meleeHit=" + m_hits +
            " meleeMiss=" + m_misses +
            " meleeAirSwing=" + m_airSwings +
            " meleeResultsSent=" + m_resultsSent +
            " meleeResultsSentPeers=" + m_resultsSentPeers +
            " meleeResultEncodeFailed=" + m_resultEncodeFailures +
            " meleeResultsRecv=" + m_resultsReceived +
            " meleeResultDecodeFailed=" + m_resultDecodeFailures +
            " meleeResultOnHost=" + m_resultsOnHost +
            " meleeResultAccepted=" + m_resultsAccepted +
            " meleeResultRejected=" + m_resultsRejected +
            " meleeResultDuplicate=" + m_resultsDuplicate +
            " meleeResultApplyFailed=" + m_resultApplyFailures +
            " meleeObserved=" + m_observed +
            " meleeOutOfReach=" + m_outOfReach +
            " meleeCooldown=" + m_cooldown +
            " meleeTargetMissing=" + m_targetMissing +
            " meleePredictionNotSent=" + m_predictionNotSent +
            " meleeLocalOverflow=" + m_localOverflow +
            " · " + m_combatTelemetry.Describe();

        public string StatusLine() => "melee: " + Summary() + " · " + m_port.Describe();

        // ---------------------------------------------------------------- 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Error("没有拿到传输端口（transport 能力缺失）→ 近战攻击不启用（两端都走原生）");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Error("没有拿到通道控制端口（ITransportControlPort）→ 不启用在近战（不猜方向）");
                return;
            }
            if (!context.TryGetPort(out m_messages) || m_messages == null)
            {
                Error("没有拿到消息注册表（IMessageRegistryPort）→ 收不到攻击请求 / 结果");
                return;
            }
            if (!context.TryGetPort(out m_broadcast) || m_broadcast == null)
            {
                Error("没有拿到玩家端广播端口（IPeerBroadcastPort）→ 主机无法把裁决广播给观测端，" +
                    "近战不启用（不猜发送目标）");
                return;
            }
            // 战斗随机流：模块持有唯一实例；引擎适配层通过端口读写同一份状态。
            m_hostRng = new CombatRngHostState(NextCombatSeed, m_combatTelemetry);
            m_clientRng = new CombatRngClientState(m_combatTelemetry);
            m_port.CombatRngHost = m_hostRng;
            m_port.CombatRngClient = m_clientRng;
            m_port.CombatTelemetry = m_combatTelemetry;
            m_clientJoinedHandler = OnCombatClientJoined;
            m_clientLeftHandler = OnCombatClientLeft;
            m_transport.ClientJoined += m_clientJoinedHandler;
            m_transport.ClientLeft += m_clientLeftHandler;

            m_port.SequenceAllocator = AllocateSequence;
            m_port.LocalAttackCaptured += OnLocalAttackCaptured;
            m_messages.Registry.RegisterModule(this);
            m_attached = true;
            Info("近战攻击就绪：主机裁决命中 / 伤害 / 剩余生命并广播给攻击者 + 观测端；" +
                "客户端用**同一条战斗随机流**在攻击当帧预测命中 / Miss（结果确认或回滚）；" +
                "距离 = min(请求 reach + 0.5 m, " + Fmt(m_port.ServerMaxReach) + " m 配置上限, " +
                Fmt(m_port.HostEngineReach) + " m 引擎 reach)；" +
                "共享战斗随机流 = 每 (clientId, slot) 一条 LCG（append-only id 99）");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.MeleeAttackRequest, PM.MeleeAttackRequest.Parser,
                new DelegateMessageHandler<PM.MeleeAttackRequest>(OnRequestMessage), ModuleId);
            registry.Register((int)MessageType.MeleeAttackResult, PM.MeleeAttackResult.Parser,
                new DelegateMessageHandler<PM.MeleeAttackResult>(OnResultMessage), ModuleId);
            registry.Register((int)MessageType.CombatRngSync, PM.CombatRngSync.Parser,
                new DelegateMessageHandler<PM.CombatRngSync>(OnCombatRngSyncMessage), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = StatusLine();
            if (m_transport != null)
            {
                if (m_clientJoinedHandler != null)
                    m_transport.ClientJoined -= m_clientJoinedHandler;
                if (m_clientLeftHandler != null)
                    m_transport.ClientLeft -= m_clientLeftHandler;
            }
            if (m_port != null)
            {
                try { m_port.LocalAttackCaptured -= OnLocalAttackCaptured; } catch (Exception) { }
                try { m_port.SequenceAllocator = null; } catch (Exception) { }
                try
                {
                    m_port.CombatRngClient = null;
                    m_port.CombatRngHost = null;
                    m_port.CombatTelemetry = null;
                }
                catch (Exception) { }
                try { m_port.Active = false; } catch (Exception) { }
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
            }
            m_localAttacks.Clear();
            m_dedupe.Reset();
            m_rejectedByReason.Clear();
            m_lastProblemMs.Clear();
            m_pendingSync.Clear();
            m_hostRng?.Reset();
            m_clientRng?.Reset();
            m_hostRng = null;
            m_clientRng = null;
            m_lastLocalClientId = int.MinValue;
            m_messages = null;
            m_broadcast = null;
            m_control = null;
            m_transport = null;
            m_attached = false;
            Info("近战攻击下线（" + reason + "）：" + summary);
        }

        // ---------------------------------------------------------------- 每帧

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            try { m_port.Pump(); }
            catch (Exception e)
            {
                Problem("pump", "近战引擎适配器 Pump 异常（" + e.GetType().Name + " " + e.Message +
                    "）→ 本帧不接管攻击输入");
                m_port.Active = false;
                return;
            }
            bool hosting = m_control != null && m_control.IsHosting;
            bool connected = m_transport != null && m_transport.IsConnected;
            m_port.Active = tick.IsConnected && (hosting || connected);
            if (m_port.SequenceAllocator == null) m_port.SequenceAllocator = AllocateSequence;
            if (hosting) FlushPendingSync();
            else PumpClientRngIdentity(connected);

            // 本帧排队的**主机本机角色**攻击（客户端请求在采集当帧就上行，见 OnLocalAttackCaptured）；
            // 这里执行 = 不在引擎实体 Update 中间改世界。
            while (m_localAttacks.Count > 0)
            {
                MeleeLocalAttack attack = m_localAttacks.Dequeue();
                MeleeAttackRequestData request = attack.Request;
                if (hosting) HandleHostAttack(0, in request);
                else
                {
                    m_predictionNotSent++;
                    Problem("not-hosted", "本端在排队期间不再是主机 → 攻击 " +
                        request.Describe() + " 没有执行（不本地扣血、不伪造结果）");
                    m_port.NoteRequestNotSent(in request, "not-hosted");
                }
            }

            long nowMs = (long)(Now() * 1000.0);
            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                // 单机 / 未联机时一帧都不会有近战消息：不写零计数摘要（避免刷日志）；
                // 一旦有会话或有过请求 / 结果，就按 10 s 如实打一行。
                if (hosting || connected || m_requestsSent > 0 || m_requestsReceived > 0 ||
                    m_resultsSent > 0 || m_resultsReceived > 0)
                    Info(StatusLine());
            }
        }

        // ---------------------------------------------------------------- 共享战斗随机流

        /// <summary>会话种子：主机会话 RNG 生成 64 位（低 48 位进入 LCG）。</summary>
        private ulong NextCombatSeed()
        {
            ulong high = (uint)m_seedRandom.Int();
            ulong low = (uint)m_seedRandom.Int();
            return (high << 32) | low;
        }

        private void OnCombatClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (clientId <= 0 || m_control == null || !m_control.IsHosting) return;
            if (m_hostRng == null) return;
            int streams = m_hostRng.EnsureClient(clientId);
            m_pendingSync.Add(clientId);
            bool sent = SendCombatRngSync(clientId, rejoin);
            Info("共享战斗随机流：玩家端 clientId=" + clientId + (rejoin ? "（重连）" : "") +
                " 建立 " + streams + " 条流 → " + (sent ? "已发送 seed+index" : "发送失败，将在下一拍重试"));
        }

        private void OnCombatClientLeft(int clientId, string reason)
        {
            if (m_hostRng == null) return;
            int removed = m_hostRng.RemoveClient(clientId);
            m_pendingSync.Remove(clientId);
            if (removed > 0)
                Info("共享战斗随机流：玩家端 clientId=" + clientId + " 离开（" + reason +
                    "）→ 丢弃它的 " + removed + " 条会话流");
        }

        private void FlushPendingSync()
        {
            if (m_pendingSync.Count == 0 || m_transport == null) return;
            var clients = new List<int>(m_pendingSync);
            for (int i = 0; i < clients.Count; i++)
            {
                int clientId = clients[i];
                if (!m_transport.IsClientSendable(clientId)) continue;
                SendCombatRngSync(clientId, reset: false);
            }
        }

        private bool SendCombatRngSync(int clientId, bool reset)
        {
            if (m_transport == null || m_hostRng == null)
            {
                Problem("rng-sync-no-port", "共享战斗随机流：没有传输 / 主机流状态 → 无法发送同步");
                return false;
            }
            CombatRngSyncEntry[] streams = m_hostRng.SnapshotClient(clientId);
            if (streams.Length == 0)
            {
                Problem("rng-sync-empty", "共享战斗随机流：clientId=" + clientId +
                    " 没有任何流 → 不发送空同步（明确失败）");
                return false;
            }
            var data = new CombatRngSyncData(ServerTickNow(), reset, streams);
            byte[] payload;
            try
            {
                payload = CombatRngCodec.Encode(in data);
            }
            catch (Exception e)
            {
                Problem("rng-sync-encode", "共享战斗随机流编码失败（" + e.GetType().Name + " " +
                    e.Message + "）→ 玩家端继续用旧流（下一拍重试）");
                return false;
            }
            bool sent;
            try
            {
                sent = m_transport.TrySendTo(clientId, MessageType.CombatRngSync, payload);
            }
            catch (Exception e)
            {
                sent = false;
                Problem("rng-sync-exception", "共享战斗随机流发送异常（" + e.GetType().Name + " " +
                    e.Message + "）");
            }
            if (!sent)
            {
                m_pendingSync.Add(clientId);
                Problem("rng-sync-send", "共享战斗随机流没有发给 clientId=" + clientId +
                    "（连接不可达 / 背压）→ 保留待发，下一拍重试");
                return false;
            }
            m_pendingSync.Remove(clientId);
            m_combatTelemetry.CombatRngSyncSent++;
            return true;
        }

        private void PumpClientRngIdentity(bool connected)
        {
            int local = m_transport?.LocalClientId ?? -1;
            if (local <= 0)
            {
                if (m_lastLocalClientId != int.MinValue)
                {
                    m_clientRng?.Reset();
                    m_lastLocalClientId = int.MinValue;
                    m_lastProblemMs.Remove("rng-client-reset");
                }
                return;
            }
            if (local == m_lastLocalClientId) return;
            if (m_lastLocalClientId != int.MinValue)
            {
                m_clientRng?.Reset();
                m_combatTelemetry.CombatRngResync++;
                Problem("rng-client-reset", "客户端传输编号已更换（" + m_lastLocalClientId + " → " +
                    local + "）→ 丢弃旧战斗随机流，等待主机重发 seed+index（不重放缺失结果）");
            }
            m_lastLocalClientId = local;
        }

        private void OnCombatRngSyncMessage(MessageContext context, PM.CombatRngSync message)
        {
            m_combatTelemetry.CombatRngSyncRecv++;
            bool hosting = m_control != null && m_control.IsHosting;
            if (hosting)
            {
                Problem("rng-sync-on-host", "主机收到了 COMBAT_RNG_SYNC（方向错误）→ 忽略");
                return;
            }
            if (!CombatRngCodec.TryDecode(message, out CombatRngSyncData data, out string reason))
            {
                Problem("rng-sync-malformed", "共享战斗随机流同步畸形（" + reason + "）→ 整条拒绝");
                return;
            }
            int local = m_transport?.LocalClientId ?? -1;
            if (local <= 0)
            {
                Problem("rng-sync-no-client", "收到共享战斗随机流同步，但本地传输编号还没分配（" +
                    local + "）→ 丢弃，等握手完成");
                return;
            }
            if (m_clientRng == null)
            {
                Problem("rng-sync-no-state", "收到共享战斗随机流同步，但客户端流状态不存在 → 丢弃");
                return;
            }
            long before = m_clientRng.IndexOf(0);
            if (!m_clientRng.ApplySync(local, data.Streams, data.Reset, out int applied,
                    out string applyReason))
            {
                Problem("rng-sync-apply", "共享战斗随机流同步无法应用：" +
                    (applyReason ?? "端口没有给原因") + " → 保持旧流");
                return;
            }
            if (data.Reset) m_combatTelemetry.CombatRngResync++;
            Info("共享战斗随机流：收到主机同步 clientId=" + local + " 流=" + applied +
                (data.Reset ? "（重连重建）" : "") + " index0 " + before + "→" +
                m_clientRng.IndexOf(0) + " · " + m_clientRng.Describe());
        }

        // ---------------------------------------------------------------- 请求上行（客户端）

        private void SendRequest(in MeleeAttackRequestData request)
        {
            if (m_messages == null)
            {
                m_requestSendFailures++;
                m_predictionNotSent++;
                Problem("no-message-layer", "攻击请求 " + request.Describe() +
                    " 没有消息层可发 → 回滚预测（客户端绝不本地扣血）");
                m_port.NoteRequestNotSent(in request, "no-message-layer");
                return;
            }
            m_requestsSent++;
            bool sent;
            try
            {
                sent = m_messages.Sender.Send(0, MeleeCodec.EncodeRequest(in request));
            }
            catch (Exception e)
            {
                sent = false;
                Problem("send-exception", "攻击请求 " + request.Describe() + " 发送异常（" +
                    e.GetType().Name + " " + e.Message + "）");
            }
            if (!sent)
            {
                m_requestSendFailures++;
                m_predictionNotSent++;
                Problem("send-failed", "攻击请求 " + request.Describe() +
                    " 发送失败（通道不可用 / 未注册）→ 立刻回滚预测，不本地扣血");
                m_port.NoteRequestNotSent(in request, "send-failed");
            }
        }

        // ---------------------------------------------------------------- 主机：执行 + 广播

        private void OnRequestMessage(MessageContext context, PM.MeleeAttackRequest message)
        {
            m_requestsReceived++;
            bool hosting = m_control != null && m_control.IsHosting;
            if (!hosting)
            {
                m_requestsOnClient++;
                Problem("request-on-client", "客户端收到了近战请求（方向错误）→ 忽略");
                return;
            }
            if (context.ClientId <= 0)
            {
                m_requestsFromNonPlayer++;
                Problem("request-source", "近战请求的发送方 clientId=" + context.ClientId +
                    " 不是玩家端连接 → 拒绝（不猜发起者）");
                return;
            }
            if (m_broadcast != null && !m_broadcast.IsPlayerClient(context.ClientId))
            {
                m_requestsFromNonPlayer++;
                Problem("request-debug-peer", "clientId=" + context.ClientId +
                    " 是调试端（只读）→ 拒绝它的近战请求");
                return;
            }
            if (!MeleeCodec.TryDecode(message, out MeleeAttackRequestData request,
                    out string reason))
            {
                m_requestsMalformed++;
                Problem("request-malformed", "近战请求畸形（" + reason + "）→ 整条拒绝，不执行");
                return;
            }
            bool indexMismatch = false;
            if (m_hostRng != null)
            {
                var key = new CombatRngKey(context.ClientId, request.AttackerSlot);
                long expected = m_hostRng.NextIndex(in key);
                if (request.AttackIndex != expected)
                {
                    indexMismatch = true;
                    m_combatTelemetry.CombatRngMismatch++;
                    Problem("rng-index-mismatch", "近战请求的 attack_index=" +
                        request.AttackIndex + " 与主机流位置 " + expected + " 不一致" +
                        "（attacker=" + context.ClientId + ":" + request.AttackerSlot +
                        "）→ 主机按自己的 index 结算，并给该客户端补发 seed+index");
                }
            }
            HandleHostAttack(context.ClientId, in request);
            if (indexMismatch && context.ClientId > 0)
            {
                // 结算后再发：客户端拿到的是**结算后**的流位置（下一发 index）。
                m_pendingSync.Add(context.ClientId);
                SendCombatRngSync(context.ClientId, reset: false);
            }
        }

        private void HandleHostAttack(int sourceClientId, in MeleeAttackRequestData request)
        {
            m_hostExecuted++;
            MeleeAttackResultData result;
            string reason;
            bool executed;
            try
            {
                executed = m_port.TryExecuteAuthoritative(sourceClientId, in request,
                    out result, out reason);
            }
            catch (Exception e)
            {
                executed = false;
                result = default;
                reason = "host-execute-exception-" + e.GetType().Name;
                Problem("execute-exception", "主机执行近战 " + request.Describe() + " 抛异常（" +
                    e.GetType().Name + " " + e.Message + "）");
            }
            if (!executed)
            {
                m_hostExecuteFailures++;
                result = MeleeResults.Rejected(in request, sourceClientId, request.AttackerSlot,
                    string.IsNullOrEmpty(reason) ? MeleeReasons.HostUnavailable : reason,
                    ServerTickNow(), 0f, 0f, m_port.ServerMaxReach, m_port.HostEngineReach,
                    request.EyePosition, request.LookDirection);
                Problem("host-execute", "主机未能执行近战 " + request.Describe() + "：" +
                    (reason ?? "端口没有给原因") + " → 回执明确拒绝");
            }
            // 主机侧计数：按**权威结论**记（不是按请求意图记）。
            if (result.Accepted)
            {
                m_accepted++;
                if (result.Target.IsAir) m_airSwings++;
                else if (result.Hit) m_hits++;
                else m_misses++;
            }
            else
            {
                m_rejected++;
                BumpReason(result.Reason);
            }
            BroadcastResult(in result);
        }

        private void BroadcastResult(in MeleeAttackResultData result)
        {
            if (m_broadcast == null)
            {
                m_resultEncodeFailures++;
                Problem("no-broadcast", "近战结果 seq=" + result.Sequence +
                    " 没有广播端口 → 攻击者 / 观测端收不到（明确失败）");
                return;
            }
            byte[] payload;
            try
            {
                payload = MeleeCodec.Encode(in result);
            }
            catch (Exception e)
            {
                m_resultEncodeFailures++;
                Problem("result-encode", "近战结果 " + result.Describe() + " 编码失败（" +
                    e.GetType().Name + " " + e.Message + "）→ 本拍不广播");
                return;
            }
            int peers;
            try
            {
                peers = m_broadcast.BroadcastToPlayers(MessageType.MeleeAttackResult, payload);
            }
            catch (Exception e)
            {
                m_resultEncodeFailures++;
                Problem("result-broadcast", "近战结果 seq=" + result.Sequence + " 广播异常（" +
                    e.GetType().Name + " " + e.Message + "）");
                return;
            }
            m_resultsSent++;
            m_resultsSentPeers += peers;
            // peers = 0 是合法事实（主机一个人）：不写成失败，但状态行能看见。
        }

        // ---------------------------------------------------------------- 客户端：结果

        private void OnResultMessage(MessageContext context, PM.MeleeAttackResult message)
        {
            m_resultsReceived++;
            bool hosting = m_control != null && m_control.IsHosting;
            if (hosting)
            {
                m_resultsOnHost++;
                Problem("result-on-host", "主机收到了近战结果（方向错误）→ 忽略");
                return;
            }
            if (!MeleeCodec.TryDecode(message, out MeleeAttackResultData result, out string reason))
            {
                m_resultDecodeFailures++;
                Problem("result-malformed", "近战结果畸形（" + reason + "）→ 整条拒绝，不做表现");
                return;
            }
            if (!m_dedupe.TryAccept(result.AttackerClientId, result.Sequence))
            {
                m_resultsDuplicate++;
                Problem("result-duplicate", "近战结果重复 / 陈旧（attacker=" +
                    result.AttackerClientId + " seq=" + result.Sequence + "）→ 忽略，不重放");
                return;
            }
            if (result.Accepted) m_resultsAccepted++;
            else
            {
                m_resultsRejected++;
                BumpReason(result.Reason);
            }
            if (m_transport != null && result.AttackerClientId != m_transport.LocalClientId)
                m_observed++;
            try
            {
                if (!m_port.ApplyResult(in result, out string applyReason))
                {
                    m_resultApplyFailures++;
                    Problem("result-apply", "近战结果 " + result.Describe() +
                        " 表现失败：" + (applyReason ?? "端口没有给原因"));
                }
            }
            catch (Exception e)
            {
                m_resultApplyFailures++;
                Problem("result-apply-exception", "近战结果 " + result.Describe() + " 表现异常（" +
                    e.GetType().Name + " " + e.Message + "）");
            }
        }

        // ---------------------------------------------------------------- 本地采集

        private long AllocateSequence()
        {
            m_nextSequence++;
            if (m_nextSequence <= 0) m_nextSequence = 1;
            return m_nextSequence;
        }

        private void OnLocalAttackCaptured(MeleeLocalAttack attack)
        {
            if (!m_attached) return;
            bool hosting = m_control != null && m_control.IsHosting;
            bool connected = m_transport != null && m_transport.IsConnected;
            if (!hosting)
            {
                // 客户端：采集当帧就上行（预测已经在适配层展示），少等一帧网络延迟。
                MeleeAttackRequestData request = attack.Request;
                if (connected) SendRequest(in request);
                else
                {
                    m_predictionNotSent++;
                    Problem("not-connected", "本端既不是主机也没有连接 → 攻击 " +
                        request.Describe() + " 没有发出（不本地扣血、不伪造结果）");
                    m_port.NoteRequestNotSent(in request, "not-connected");
                }
                return;
            }
            // 主机本机角色：排队到 Tick 执行（不在引擎实体 Update 中间改世界）。
            if (m_localAttacks.Count >= MaxPendingLocalAttacks)
            {
                m_localOverflow++;
                Problem("local-overflow", "本地攻击意图队列已满（" + MaxPendingLocalAttacks +
                    "）→ 丢弃 " + attack.Request.Describe() + "（回滚它的预测）");
                m_predictionNotSent++;
                MeleeAttackRequestData dropped = attack.Request;
                m_port.NoteRequestNotSent(in dropped, "local-queue-full");
                return;
            }
            m_localAttacks.Enqueue(attack);
        }

        // ---------------------------------------------------------------- 工具

        private long ServerTickNow() => m_port.AuthoritativeTick;

        private void BumpReason(string reason)
        {
            string key = MeleeReasons.Key(reason);
            m_rejectedByReason.TryGetValue(key, out long count);
            m_rejectedByReason[key] = count + 1;
            // 三条高频原因的独立计数（设备检查单直接读它们）。
            // 说明：主机与客户端各有一份同名计数（主机 = 自己拒绝的请求，客户端 = 收到的拒绝）。
            switch (key)
            {
                case MeleeReasons.OutOfReach: m_outOfReach++; break;
                case MeleeReasons.Cooldown: m_cooldown++; break;
                case MeleeReasons.TargetMissing: m_targetMissing++; break;
            }
        }

        private static string Fmt(float value) =>
            float.IsFinite(value) ? value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "NaN";

        private void Info(string message) => m_context?.Diagnostics.Info(ModuleId, message);

        private void Error(string message) => m_context?.Diagnostics.Error(ModuleId, message, null);

        private void Problem(string key, string message)
        {
            long nowMs = (long)(Now() * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) && nowMs - last < ProblemLogMs)
                return;
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Warn(ModuleId, message);
        }
    }

}
