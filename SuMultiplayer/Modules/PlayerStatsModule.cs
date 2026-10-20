using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using SuMultiplayer.Protocol;
using P = SuMultiplayer.Protocol;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 玩家统计 + 成就同步（**每人自己的统计由本端计算 / 主机统一存储与判定成就**）。
    ///
    /// 引擎事实（先读源码再写代码）：
    ///   · 统计 = <c>Game.PlayerStats</c> 的 [Stat] 字段，按 PlayerIndex 存在
    ///     <c>SubsystemPlayerStats</c> 里，随 Project.xml 持久化
    ///     （Source: Survivalcraft/Game/SubsystemPlayerStats.cs:SubsystemPlayerStats.Save）；
    ///   · **统计只有玩家自己的那一端算得准**（它才有真实的 ComponentMiner / ComponentLocomotion），
    ///     所以主机上某个客户端的化身 <c>PlayerStats</c> 不是那个人的进度；主机**绝不**把
    ///     联机化身的引擎统计当成权威值（PlayerIndex 还会被复用、旧条目不会被清）；
    ///   · 引擎里**没有成就子系统**（Survivalcraft/Game 下没有任何 Achievement 类型）：
    ///     成就由本模组的 <see cref="AchievementCatalog"/> 按**上报上来的统计**判定，
    ///     id + UTC 解锁时间存进世界作用域角色记录（SuMultiplayerPlayers.xml）；
    ///   · 统计面板（游戏内 Back → GameMenuDialog）读的就是本机 <c>ComponentPlayer.PlayerStats</c>，
    ///     Source: Survivalcraft/Game/GameMenuDialog.cs:46-118 —— 所以客户端只把
    ///     **本机身份**记录里的统计写回 <c>SubsystemPlayerStats.GetPlayerStats(本机 PlayerIndex)</c>
    ///     （加入 / 重连时的一次性恢复），原面板就显示自己的数据。
    ///
    /// 协议（append-only，Protocol/stats.proto）：
    ///   · 主机 → 玩家端：加入 / 重连发**分片完整快照**（PlayerStatsSnapshot，id 91）
    ///     = 该身份记录里的统计（没有记录时发空快照，表示"从你自己的当前统计开始"）；
    ///     主机自己的统计变化 → 发**分片增量**（PlayerStatsDelta，id 92）给所有客户端；
    ///   · 客户端 → 主机：本机统计变化 → 发**分片完整快照**（同一个 id 91，identity = 本机身份；
    ///     主机按发送者校验身份后写进该身份的角色记录，并转发增量给其他人）；
    ///   · 客户端只把**本机身份**的恢复快照写进本机 <c>PlayerStats</c>；其它身份的数据只留在
    ///     只读视图里（绝不碰本机 PlayerStats）。恢复之后本机统计由本机行为继续累加并上报。
    ///
    /// 客户端进世界时还必须先把**下载世界自带的主机统计**从本机槽位分出去
    /// （<see cref="IPlayerStatsPort.EnsureLocalStatsOwnership"/>，旧实现 SeparateDownloadedPlayerStats）。
    ///
    /// 无静态状态；引擎读写 / 记录落盘 / 本机玩家定位全部通过 <see cref="IPlayerStatsPort"/>。
    /// </summary>
    public sealed class PlayerStatsModule : IMultiplayerModule, ISuMPMessageModule, ISyncedPlayerStatsView
    {
        /// <summary>模块 id（也是消息处理器归属）。</summary>
        public const string ModuleIdText = "player-stats";

        private const int SummaryLogMs = 10000;
        private const int ProblemLogMs = 10000;
        private const int HostPollIntervalMs = 1000;
        /// <summary>客户端检查周期（本机统计上报 / 恢复重试）。</summary>
        private const int ClientReconcileIntervalMs = 500;
        /// <summary>本机统计两次上报的最小间隔（统计没变化不发；旧实现同样 2 秒级）。</summary>
        private const int ClientReportIntervalMs = 1000;
        /// <summary>统计没变化时的上报心跳（丢了 / 主机当时没有跟踪这个身份时靠它补上）。</summary>
        private const int ClientReportHeartbeatMs = 10000;
        /// <summary>连上多久还没收到"本机身份"的恢复快照就明确记一行（不静默）。</summary>
        private const int SelfRestorePendingLogMs = 10000;
        private const int MaxDisplayPlayers = 8;
        private const int MaxReportStats = 40;
        private const int MaxReportAchievements = 32;

        private sealed class HostPeerState
        {
            public int ClientId;
            public string Identity = string.Empty;
            public string Name = string.Empty;
            public string RecordKey = string.Empty;
            public uint Generation;
            /// <summary>
            /// 已经向这个玩家端发过**本机身份的恢复快照**（记录里的统计，或没有记录时的空快照）。
            /// false = 还没发过（等加入 / 重连事件）；主机自己的条目发首份统计快照时也会置位。
            /// </summary>
            public bool SentSnapshot;
            /// <summary>上一次**成功**下发 / 应用的值（差分的基线；null = 还没发过）。</summary>
            public PlayerStatSet LastSent;
            /// <summary>最近一次已知的该身份统计（记录 / 客户端上报；离开时落盘用）。</summary>
            public PlayerStatSet DisplayStats;
            /// <summary>角色记录里的统计（仅用于"加入时从记录发恢复快照"）。</summary>
            public PlayerStatSet RecordStats;
            /// <summary>成就账本（主机判定；记录里的未知 id 原样保留）。</summary>
            public readonly AchievementLedger Achievements = new AchievementLedger();
        }

        private sealed class ClientViewState
        {
            public string Identity = string.Empty;
            public string Name = string.Empty;
            public bool IsHost;
            public PlayerStatSet Stats = new PlayerStatSet();
            public readonly AchievementLedger Achievements = new AchievementLedger();
        }

        private sealed class PendingSnapshot
        {
            public uint Generation;
            public uint ChunkCount;
            public bool[] Received;
            public int ReceivedCount;
            public string Name = string.Empty;
            public bool IsHost;
            public readonly PlayerStatSet Stats = new PlayerStatSet();
            public readonly AchievementLedger Achievements = new AchievementLedger();
        }

        /// <summary>主机侧：正在拼的统计上报。</summary>
        private sealed class PendingReport
        {
            public uint Generation;
            public uint ChunkCount;
            public bool[] Received;
            public int ReceivedCount;
            public readonly PlayerStatSet Stats = new PlayerStatSet();
        }

        /// <summary>主机侧：加入时角色记录读取失败、需要下个轮询重试建立跟踪的玩家。</summary>
        private sealed class PendingJoin
        {
            public string Name = string.Empty;
            public string Identity = string.Empty;
        }

        private readonly ModuleDescriptor m_descriptor;
        private IPlayerStatsPort m_port;
        private readonly Func<double> m_now;

        private IModuleContext m_context;
        private ITransportPort m_transport;
        private ITransportControlPort m_control;
        private IMessageRegistryPort m_messages;
        private bool m_attached;

        // ---------------------------------------------------------------- 主机
        private readonly Dictionary<int, HostPeerState> m_hostPeers =
            new Dictionary<int, HostPeerState>();
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>主机侧：clientId → 正在拼的统计上报。</summary>
        private readonly Dictionary<int, PendingReport> m_pendingReports =
            new Dictionary<int, PendingReport>();
        /// <summary>主机侧：clientId → 加入时记录读取失败、等待下个轮询重试建立跟踪的玩家。</summary>
        private readonly Dictionary<int, PendingJoin> m_pendingJoins =
            new Dictionary<int, PendingJoin>();
        private ulong m_revision;
        private uint m_generationCounter;
        private long m_lastHostPollMs;
        private bool m_wasHosting;

        // ---------------------------------------------------------------- 客户端
        private readonly Dictionary<string, ClientViewState> m_clientViews =
            new Dictionary<string, ClientViewState>(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingSnapshot> m_pendingSnapshots =
            new Dictionary<string, PendingSnapshot>(StringComparer.Ordinal);
        private readonly HashSet<string> m_unknownSelfNames = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>本机身份记录里恢复出来的统计（只在这个集合非空时重试写回本机 PlayerStats）。</summary>
        private PlayerStatSet m_desiredSelf;
        /// <summary>主机已经按本机身份发过恢复快照（可能是空快照 = 记录里还没有统计）。</summary>
        private bool m_selfRestoreReceived;
        /// <summary>本机统计已经恢复完 / 无需恢复，可以开始把本机数据上报给主机。</summary>
        private bool m_selfRestored;
        /// <summary>上一次**成功**上报的本机统计（差分基线；null = 还没上报过）。</summary>
        private PlayerStatSet m_lastReportedSelf;
        private long m_lastReportMs;
        private long m_lastReportSentMs;
        /// <summary>连上之后等"本机身份恢复快照"的起始时刻（毫秒；0 = 还没开始等）。</summary>
        private long m_selfRestoreWaitSinceMs;
        private bool m_selfRestorePendingLogged;
        private ulong m_lastRevision;
        private int m_lastLocalClientId = -1;
        private string m_lastHostIdentity = string.Empty;
        private bool m_clientWasConnected;
        /// <summary>连接断过：下一次连上时要清空上一轮快照（首次连接不清）。</summary>
        private bool m_sessionStale;
        private long m_lastReconcileMs;

        // ---------------------------------------------------------------- 计数（10 s 状态行 / 汇总日志）
        private long m_statsSent, m_statsRecv, m_statsApplied, m_statsRejected, m_statsSkipped;
        private long m_statsMalformed, m_statsSendFailures, m_statsNoRecipients;
        private long m_statsApplyFailures, m_statsCaptureFailures, m_statsMessagesSent, m_statsMessagesRecv;
        private long m_achvSent, m_achvRecv, m_achvApplied, m_achvUnknown, m_achvMalformed;
        private long m_achvEvaluateSkipped, m_achvRecordRejected;
        private long m_encodeFailures, m_decodeFailures, m_staleDeltas, m_duplicateChunks;
        private long m_hostPolls, m_hostChanges, m_hostCaptureFailures;
        private long m_recordLoads, m_recordMissing, m_recordLoadFailures, m_recordStores, m_recordStoreFailures;
        private long m_identityDegraded, m_identityChanges, m_duplicateIdentities;
        private long m_snapshotsSent, m_deltasSent, m_snapshotsApplied, m_deltasApplied;
        private long m_statsApplyRuns;
        private long m_reportsSent, m_reportChunks, m_reportSendFailures, m_reportsSkipped;
        private long m_reportsRecv, m_reportChunksRecv, m_reportsApplied, m_reportRejected;
        private long m_reportMalformed, m_reportIdentityMismatch, m_reportSenderUnknown;
        private long m_reportUnchanged;
        private long m_selfEchoStatsSkipped, m_reportIdentityMissing;
        private long m_selfSnapshotDuplicates;
        private long m_ownershipFailures;
        private long m_suppressedProblems;
        private long m_lastSummaryMs;

        public PlayerStatsModule(IPlayerStatsPort port, Func<double> now)
        {
            m_port = port ?? throw new ArgumentNullException(nameof(port));
            m_now = now ?? throw new ArgumentNullException(nameof(now));
            m_descriptor = new ModuleDescriptor(ModuleIdText,
                "玩家统计 / 成就同步（各端统计自己的 + 主机存储 / 判定成就）")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "player.stats" },
                HotPluggable = true
            };
        }

        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => ModuleIdText;

        // ---------------------------------------------------------------- 生命周期

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport) || m_transport == null)
            {
                Error("没有拿到传输端口（transport 能力缺失）→ 玩家统计 / 成就同步不启用");
                return;
            }
            if (!context.TryGetPort(out m_control) || m_control == null)
            {
                Error("没有拿到通道控制端口（ITransportControlPort）→ 无法判断本端是主机还是客户端，" +
                    "统计 / 成就同步不启用（不猜方向）");
                return;
            }
            if (!context.TryGetPort(out m_port) || m_port == null)
            {
                Error("没有拿到统计引擎端口（IPlayerStatsPort）→ 统计 / 成就同步不启用" +
                    "（组合根必须注册 PlayerStatsStore；不拿本机 PlayerData 猜）");
                return;
            }
            if (!context.TryGetPort(out m_messages) || m_messages == null)
            {
                Error("没有拿到消息注册表（IMessageRegistryPort）→ 收不到统计快照 / 增量");
                return;
            }
            m_transport.ClientJoined += OnClientJoined;
            m_transport.ClientLeft += OnClientLeft;
            m_messages.Registry.RegisterModule(this);
            m_attached = true;
            Info("玩家统计 / 成就同步就绪：主机 = 按身份存储客户端上报的统计 + 自己的统计变化增量 + " +
                "角色记录落盘；客户端 = 先把下载世界的主机统计从本机槽位分离，再只应用**本机身份**的" +
                "恢复快照，之后把本机统计上报给主机（每 " + ClientReconcileIntervalMs + " ms 检查）");
        }

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)MessageType.PlayerStatsSnapshot, P.PlayerStatsSnapshot.Parser,
                new DelegateMessageHandler<P.PlayerStatsSnapshot>(OnSnapshot), ModuleId);
            registry.Register((int)MessageType.PlayerStatsDelta, P.PlayerStatsDelta.Parser,
                new DelegateMessageHandler<P.PlayerStatsDelta>(OnDelta), ModuleId);
        }

        public void Detach(ModuleDetachReason reason)
        {
            string summary = Summary();
            if (m_transport != null)
            {
                try { m_transport.ClientJoined -= OnClientJoined; } catch (Exception) { }
                try { m_transport.ClientLeft -= OnClientLeft; } catch (Exception) { }
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleId); } catch (Exception) { }
            }
            if (m_control?.IsHosting == true) FlushAllPeers("模块下线（主机）");
            m_hostPeers.Clear();
            m_pendingReports.Clear();
            m_pendingJoins.Clear();
            m_clientViews.Clear();
            m_pendingSnapshots.Clear();
            m_desiredSelf = null;
            m_selfRestoreReceived = false;
            m_selfRestored = false;
            m_lastReportedSelf = null;
            m_unknownSelfNames.Clear();
            m_lastProblemMs.Clear();
            m_messages = null;
            m_control = null;
            m_transport = null;
            m_attached = false;
            LogInfo("玩家统计 / 成就同步下线（" + reason + "）：" + summary);
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            long nowMs = (long)(tick.Now * 1000.0);
            bool hosting = m_control != null && m_control.IsHosting;
            if (hosting && !m_wasHosting)
            {
                // 不清 m_hostPeers：玩家端可能在"开始建房"和第一个 Tick 之间就完成了加入握手
                // （加入事件会先建好跟踪项）。房间关闭时 FlushAllPeers 已经清过表，这里重复清空
                // 会把刚加入的玩家从跟踪表里删掉 → 它的上报全被当成"没有跟踪这个玩家"拒绝。
                m_revision = 0;
                m_generationCounter = 0;
                m_lastHostPollMs = 0;
                Info("房间开始：增量 revision 已重置（本机世界与上一局无关）；已有的玩家跟踪项保留");
            }
            if (!hosting && m_wasHosting) FlushAllPeers("房间已关闭");
            m_wasHosting = hosting;

            if (hosting) TickHost(nowMs);
            else TickClient(nowMs);

            if (nowMs - m_lastSummaryMs >= SummaryLogMs)
            {
                m_lastSummaryMs = nowMs;
                LogInfo(StatusLine());
            }
        }

        private void TickHost(long nowMs)
        {
            if (nowMs - m_lastHostPollMs < HostPollIntervalMs) return;
            m_lastHostPollMs = nowMs;
            RetryPendingJoins();
            // 只抓**主机自己**的统计。联机角色（clientId>0）的统计由那个玩家端上报：
            // 主机上的化身不跑客户端的挖掘 / 移动逻辑，它的 PlayerStats 不是真人进度
            // （实机缺陷：主机从这里把世界存档里旧角色的统计当成了客户端身份的数据）。
            if (!m_port.TryCaptureHost(out PlayerStatsWorldView view, out string captureReason))
            {
                m_hostCaptureFailures++;
                Problem("host-capture", "主机自己的统计抓取失败：" +
                    (captureReason ?? "端口没有给原因"));
                return;
            }
            m_hostPolls++;
            if (view == null || view.Stats == null)
            {
                m_hostCaptureFailures++;
                Problem("host-view", "主机自己的统计视图缺少 PlayerData / 统计对象 → 本帧不同步");
                return;
            }
            HostPeerState peer = GetOrCreateHostPeer(view);
            if (peer == null) return;
            peer.DisplayStats = view.Stats.Clone();
            if (!peer.SentSnapshot)
            {
                if (!SendSnapshotToAll(peer, view.Stats, peer.Achievements,
                        "主机首份统计快照", out string snapshotReason))
                {
                    Problem("host-snapshot:" + peer.RecordKey,
                        "主机首份统计快照下发失败（下个轮询重试，不假装成功）：" +
                        snapshotReason);
                    return;
                }
                peer.LastSent = view.Stats.Clone();
                peer.SentSnapshot = true;
                PersistPeer(peer, view.Stats, "主机首份快照");
                return;
            }
            List<KeyValuePair<string, string>> changed = view.Stats.ChangedFrom(peer.LastSent);
            var newAchievements = EvaluateAchievements(peer, view.Stats, "host");
            if (changed.Count == 0 && newAchievements.Count == 0) return;
            PersistPeer(peer, view.Stats, "主机统计 / 成就变化");
            if (!SendDeltaToAll(peer, changed, newAchievements, out string deltaReason))
            {
                Problem("host-delta:" + peer.RecordKey,
                    "统计增量下发失败（基线不前进，下个轮询重发完整变化集）：" + deltaReason);
                return;
            }
            peer.LastSent = view.Stats.Clone();
            m_hostChanges++;
            Info("主机统计变化已下发：身份 " + DescribeKey(peer.Identity) + " · 统计 " +
                changed.Count + " 项 · 新成就 " + newAchievements.Count +
                " 项 · revision=" + m_revision);
        }

        /// <summary>
        /// 用主机持有的统计评估成就，并把新解锁的成就写进账本。
        /// 返回**本次新增**的解锁项（供增量下发）。判据解析失败只计数 + 限流日志（不当成 0）。
        /// </summary>
        private List<AchievementUnlock> EvaluateAchievements(HostPeerState peer,
            PlayerStatSet stats, string source)
        {
            var newAchievements = new List<AchievementUnlock>();
            var unlocked = new List<string>();
            AchievementCatalog.Evaluate(stats, peer.Achievements, unlocked, out int unparsable);
            if (unparsable > 0)
            {
                m_achvEvaluateSkipped += unparsable;
                Problem("achv-eval:" + peer.RecordKey,
                    "有 " + unparsable + " 项成就判据的统计文本无法解析（跳过该判据，不当成 0）：" +
                    stats.DescribeNames(8));
            }
            long utcSeconds = (long)Math.Floor(m_now());
            foreach (string id in unlocked)
            {
                if (!peer.Achievements.TryUnlock(id, utcSeconds, out bool added,
                        out string unlockReason))
                {
                    m_achvMalformed++;
                    Problem("achv-unlock:" + id,
                        "解锁成就 " + id + " 失败：" + unlockReason + "（跳过该条）");
                    continue;
                }
                if (!added) continue;
                newAchievements.Add(new AchievementUnlock(id, utcSeconds));
                m_achvApplied++;
                Info("成就解锁：身份 " + DescribeKey(peer.Identity) + " · " + id + "（" +
                    AchievementCatalog.DisplayName(id) + "）· UTC " + utcSeconds +
                    " · 来源=" + source);
            }
            return newAchievements;
        }

        private HostPeerState GetOrCreateHostPeer(PlayerStatsWorldView view)
        {
            string identity = !string.IsNullOrEmpty(view.Identity) ? view.Identity : view.RecordKey;
            if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(view.RecordKey))
            {
                m_identityDegraded++;
                Problem("peer-key:" + view.ClientId,
                    "玩家 clientId=" + view.ClientId + " 既没有身份也没有记录键 → 不同步它的统计" +
                    "（绝不按 PlayerIndex / 名字猜身份）");
                return null;
            }
            if (string.IsNullOrEmpty(view.Identity))
            {
                m_identityDegraded++;
                Problem("peer-identity:" + view.ClientId,
                    "玩家 clientId=" + view.ClientId + " 的身份不可用 → 按记录键 " +
                    DescribeKey(view.RecordKey) + " 绑定（名字绑定；不能保证是同一个人）");
            }
            m_hostPeers.TryGetValue(view.ClientId, out HostPeerState peer);
            if (peer != null)
            {
                if (!string.Equals(peer.Identity, identity, StringComparison.Ordinal) ||
                    !string.Equals(peer.RecordKey, view.RecordKey, StringComparison.Ordinal))
                {
                    m_identityChanges++;
                    Info("玩家 clientId=" + view.ClientId + " 的身份变化：" +
                        DescribeKey(peer.Identity) + " → " + DescribeKey(identity) +
                        " → 重置该端的统计状态（旧记录已落盘）");
                    PersistPeer(peer, peer.DisplayStats, "身份变化前落盘");
                    m_hostPeers.Remove(view.ClientId);
                    peer = null;
                }
                else
                {
                    if (!string.IsNullOrEmpty(view.Name) &&
                        !string.Equals(peer.Name, view.Name, StringComparison.Ordinal))
                        peer.Name = view.Name;
                    return peer;
                }
            }
            // 同一身份（记录键）只允许一个联机角色：与联机角色模块的裁决一致，绝不静默合并。
            foreach (HostPeerState other in m_hostPeers.Values)
            {
                if (other.ClientId == view.ClientId) continue;
                if (!string.Equals(other.RecordKey, view.RecordKey, StringComparison.Ordinal)) continue;
                m_duplicateIdentities++;
                Problem("duplicate:" + view.ClientId,
                    "拒绝同步统计：clientId=" + view.ClientId + " 的身份 " +
                    DescribeKey(view.RecordKey) + " 已被 clientId=" + other.ClientId +
                    " 占用（同一个身份只能有一个联机角色）");
                return null;
            }
            peer = new HostPeerState
            {
                ClientId = view.ClientId,
                Identity = identity,
                Name = view.Name ?? string.Empty,
                RecordKey = view.RecordKey,
                DisplayStats = view.Stats.Clone()
            };
            if (LoadPeerRecord(peer, out string loadReason) == PlayerStatsRecordLoad.Failed)
            {
                // 读不到记录时**不建立跟踪**：否则第一次 PersistPeer 会用空成就覆盖记录里的成就。
                Problem("record-load:" + peer.RecordKey,
                    "读取身份 " + DescribeKey(peer.RecordKey) + " 的角色记录失败：" +
                    loadReason + "（本轮不建立统计跟踪，下个轮询重试；不覆盖记录）");
                return null;
            }
            m_hostPeers.Add(view.ClientId, peer);
            Info("开始跟踪玩家统计：clientId=" + view.ClientId + " · 身份=" +
                DescribeKey(peer.Identity) + " · 记录=" + DescribeKey(peer.RecordKey) +
                " · 记录里已有 " + (peer.RecordStats?.Count ?? 0) + " 项统计 / " +
                peer.Achievements.Count + " 个成就");
            return peer;
        }

        /// <summary>
        /// 读一个身份的记录并把成就 / 记录统计装进 peer。返回 Failed 时调用方**不得**建立跟踪
        /// （否则第一次落盘会用空账本覆盖记录里的成就）。
        /// </summary>
        private PlayerStatsRecordLoad LoadPeerRecord(HostPeerState peer, out string reason)
        {
            PlayerStatsRecordLoad result = m_port.TryLoadRecord(peer.RecordKey,
                out PlayerStatSet recordStats, out AchievementLedger achievements,
                out int rejectedAchievements, out reason);
            switch (result)
            {
                case PlayerStatsRecordLoad.Loaded:
                    m_recordLoads++;
                    m_achvRecordRejected += rejectedAchievements;
                    if (achievements != null) peer.Achievements.TryMergeAll(achievements);
                    if (recordStats != null && recordStats.Count > 0)
                        peer.RecordStats = recordStats.Clone();
                    break;
                case PlayerStatsRecordLoad.Missing:
                    m_recordMissing++;
                    Info("身份 " + DescribeKey(peer.RecordKey) +
                        " 还没有角色记录里的统计（新玩家 / 旧记录）→ 等主机抓取到后发首份快照");
                    break;
                case PlayerStatsRecordLoad.Failed:
                    m_recordLoadFailures++;
                    break;
            }
            return result;
        }

        // ---------------------------------------------------------------- 主机：加入 / 离开

        private void OnClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!m_attached || m_control?.IsHosting != true) return;
            if (clientId <= 0)
            {
                m_statsRejected++;
                Problem("join-id", "收到非法 clientId=" + clientId + " 的加入事件 → 不同步统计");
                return;
            }
            string trimmed = (identity ?? string.Empty).Trim();
            string recordKey = m_port.BuildRecordKey(trimmed, name);
            if (string.IsNullOrEmpty(recordKey))
            {
                m_identityDegraded++;
                Problem("join-key:" + clientId,
                    "clientId=" + clientId + " 的加入事件既没有身份也没有名字 → 无法建立稳定的" +
                    "记录键，不同步它的统计（不猜）");
                return;
            }
            if (trimmed.Length == 0)
            {
                m_identityDegraded++;
                Problem("join-identity:" + clientId,
                    "clientId=" + clientId + "（名字 " + (name ?? string.Empty) +
                    "）的身份不可用 → 按名字绑定记录键 " + DescribeKey(recordKey) +
                    "（不能保证是同一个人）");
            }
            string displayName = (name ?? string.Empty).Trim();
            m_hostPeers.TryGetValue(clientId, out HostPeerState peer);
            if (peer != null && !string.Equals(peer.RecordKey, recordKey, StringComparison.Ordinal))
            {
                m_identityChanges++;
                PersistPeer(peer, peer.DisplayStats, "重新加入前落盘");
                m_hostPeers.Remove(clientId);
                peer = null;
            }
            if (peer == null)
            {
                peer = new HostPeerState
                {
                    ClientId = clientId,
                    Identity = trimmed.Length > 0 ? trimmed : recordKey,
                    Name = displayName,
                    RecordKey = recordKey
                };
                if (LoadPeerRecord(peer, out string loadReason) == PlayerStatsRecordLoad.Failed)
                {
                    m_pendingJoins[clientId] = new PendingJoin
                    {
                        Name = displayName,
                        Identity = trimmed
                    };
                    Problem("record-load:" + recordKey,
                        "读取 clientId=" + clientId + " 的角色记录失败：" + loadReason +
                        "（本次加入先不建立统计跟踪，下个轮询重试；不覆盖记录）");
                    return;
                }
                m_hostPeers.Add(clientId, peer);
            }
            else if (displayName.Length > 0)
            {
                peer.Name = displayName;
            }

            // 重连（rejoin）或重复加入：把当前已知的该身份统计**再发一次**（幂等；客户端整份替换）。
            if (peer.SentSnapshot && peer.LastSent != null)
            {
                if (SendSnapshotToClient(clientId, peer, peer.LastSent, peer.Achievements,
                        "重连重发", out string resendReason))
                    Info("clientId=" + clientId + (rejoin ? " 重连" : " 重复加入") +
                        " → 已重发该身份统计的恢复快照（" + peer.LastSent.Count + " 项统计 / " +
                        peer.Achievements.Count + " 个成就）");
                else
                    Problem("join-resend:" + clientId, "重连重发快照失败：" + resendReason);
            }
            else if (peer.RecordStats != null && peer.RecordStats.Count > 0)
            {
                if (SendSnapshotToClient(clientId, peer, peer.RecordStats, peer.Achievements,
                        "加入快照（角色记录）", out string snapshotReason))
                {
                    peer.LastSent = peer.RecordStats.Clone();
                    peer.DisplayStats = peer.LastSent.Clone();
                    peer.SentSnapshot = true;
                    Info("clientId=" + clientId + " 加入 → 已按角色记录发送**该身份自己**的统计恢复快照（" +
                        peer.LastSent.Count + " 项统计 / " + peer.Achievements.Count + " 个成就）");
                }
                else
                {
                    Problem("join-snapshot:" + clientId, "加入恢复快照下发失败：" + snapshotReason);
                }
            }
            else
            {
                // 记录里还没有统计（新玩家 / 旧记录）：发**空恢复快照**告诉它"没有可恢复的旧进度"，
                // 客户端据此开始上报自己的统计。不能再等主机从化身抓取 —— 那个对象不是它的真实进度。
                var empty = new PlayerStatSet();
                if (SendSnapshotToClient(clientId, peer, empty, peer.Achievements,
                        "加入快照（记录里没有统计）", out string emptyReason))
                {
                    peer.LastSent = empty;
                    peer.DisplayStats = new PlayerStatSet();
                    peer.SentSnapshot = true;
                    Info("clientId=" + clientId + " 加入时角色记录里还没有统计 → 已发空恢复快照" +
                        "（客户端从自己的当前统计开始，之后由它上报；主机不从联机化身抓统计）");
                }
                else
                {
                    Problem("join-empty-snapshot:" + clientId,
                        "空恢复快照下发失败（客户端会等下一次重连；不假装已恢复）：" + emptyReason);
                }
            }

            m_pendingJoins.Remove(clientId);
            // 新客户端还要拿到房间里其它人的当前统计（主机自己的 + 其他客户端），
            // 只用于只读视图 / 名册（绝不写进它的本机 PlayerStats）。
            SendKnownSnapshotsTo(clientId);
        }

        /// <summary>
        /// 主机：加入时角色记录读取失败 → 下个轮询重试建立跟踪（加入事件本身不会再来）。
        /// 记录仍然读不到时 <see cref="OnClientJoined"/> 会继续留在待重试表里并写限流日志。
        /// </summary>
        private void RetryPendingJoins()
        {
            if (m_pendingJoins.Count == 0) return;
            foreach (KeyValuePair<int, PendingJoin> pair in
                     new List<KeyValuePair<int, PendingJoin>>(m_pendingJoins))
            {
                OnClientJoined(pair.Key, pair.Value.Name, pair.Value.Identity, rejoin: true);
                if (m_hostPeers.ContainsKey(pair.Key)) m_pendingJoins.Remove(pair.Key);
            }
        }

        private void OnClientLeft(int clientId, string reason)
        {
            if (!m_attached) return;
            m_pendingReports.Remove(clientId);
            m_pendingJoins.Remove(clientId);
            if (!m_hostPeers.TryGetValue(clientId, out HostPeerState peer)) return;
            PersistPeer(peer, peer.DisplayStats, "玩家离开");
            m_hostPeers.Remove(clientId);
            Info("玩家离开：clientId=" + clientId + "（" + (reason ?? string.Empty) +
                "）→ 统计记录已落盘并停止跟踪（记录保留，重连按身份复用；不做静默删除）");
        }

        // ---------------------------------------------------------------- 主机：下发

        private bool SendSnapshotToAll(HostPeerState peer, PlayerStatSet stats,
            AchievementLedger achievements, string why, out string reason)
        {
            List<int> recipients = CollectClientRecipients();
            if (recipients.Count == 0)
            {
                m_statsNoRecipients++;
                Problem("no-recipients-snapshot",
                    "主机有权威统计（身份 " + DescribeKey(peer.Identity) + "）但此刻没有在线玩家端 → " +
                    why + "只写角色记录，不下发（不假装发成功）");
                reason = null;
                return true;
            }
            peer.Generation = ++m_generationCounter;
            if (!PlayerStatsWire.TryEncodeSnapshot(peer.ClientId, peer.Identity, peer.Name,
                    peer.Generation, stats, AchievementList(achievements), out var messages,
                    out reason))
            {
                m_encodeFailures++;
                return false;
            }
            foreach (int clientId in recipients)
            {
                foreach (P.PlayerStatsSnapshot message in messages)
                {
                    if (!m_transport.TrySendTo(clientId, MessageType.PlayerStatsSnapshot,
                            message.ToByteArray()))
                    {
                        m_statsSendFailures++;
                        reason = "TrySendTo 失败（clientId=" + clientId + "，" + why + "）";
                        return false;
                    }
                    m_statsMessagesSent++;
                    m_statsSent += message.Stats.Count;
                    m_achvSent += message.Achievements.Count;
                }
            }
            m_snapshotsSent++;
            return true;
        }

        private bool SendSnapshotToClient(int clientId, HostPeerState peer, PlayerStatSet stats,
            AchievementLedger achievements, string why, out string reason)
        {
            peer.Generation = ++m_generationCounter;
            if (!PlayerStatsWire.TryEncodeSnapshot(peer.ClientId, peer.Identity, peer.Name,
                    peer.Generation, stats, AchievementList(achievements), out var messages,
                    out reason))
            {
                m_encodeFailures++;
                return false;
            }
            foreach (P.PlayerStatsSnapshot message in messages)
            {
                if (!m_transport.TrySendTo(clientId, MessageType.PlayerStatsSnapshot,
                        message.ToByteArray()))
                {
                    m_statsSendFailures++;
                    reason = "TrySendTo 失败（clientId=" + clientId + "，" + why + "）";
                    return false;
                }
                m_statsMessagesSent++;
                m_statsSent += message.Stats.Count;
                m_achvSent += message.Achievements.Count;
            }
            m_snapshotsSent++;
            return true;
        }

        private void SendKnownSnapshotsTo(int clientId)
        {
            foreach (HostPeerState other in new List<HostPeerState>(m_hostPeers.Values))
            {
                if (other.ClientId == clientId) continue;
                if (other.LastSent == null) continue;
                if (SendSnapshotToClient(clientId, other, other.LastSent, other.Achievements,
                        "房间当前统计", out string reason)) continue;
                Problem("room-snapshot:" + clientId,
                    "把身份 " + DescribeKey(other.Identity) + " 的当前统计发给 clientId=" +
                    clientId + " 失败：" + reason);
            }
        }

        private bool SendDeltaToAll(HostPeerState peer,
            IReadOnlyList<KeyValuePair<string, string>> changedStats,
            IReadOnlyList<AchievementUnlock> newAchievements, out string reason)
        {
            List<int> recipients = CollectClientRecipients();
            if (recipients.Count == 0)
            {
                m_statsNoRecipients++;
                Problem("no-recipients-delta",
                    "主机统计有变化（身份 " + DescribeKey(peer.Identity) + "）但没有在线玩家端 → " +
                    "只写角色记录（statsNoRecipients 计数，不假装发成功）");
                reason = null;
                return true;
            }
            m_revision++;
            peer.Generation = ++m_generationCounter;
            if (!PlayerStatsWire.TryEncodeDelta(peer.ClientId, peer.Identity, peer.Name,
                    peer.Generation, m_revision, changedStats, newAchievements, out var messages,
                    out reason))
            {
                m_encodeFailures++;
                return false;
            }
            foreach (int clientId in recipients)
            {
                foreach (P.PlayerStatsDelta message in messages)
                {
                    if (!m_transport.TrySendTo(clientId, MessageType.PlayerStatsDelta,
                            message.ToByteArray()))
                    {
                        m_statsSendFailures++;
                        reason = "TrySendTo 失败（clientId=" + clientId + "，增量 revision=" +
                            m_revision + "）";
                        return false;
                    }
                    m_statsMessagesSent++;
                    m_statsSent += message.Stats.Count;
                    m_achvSent += message.Achievements.Count;
                }
            }
            m_deltasSent++;
            return true;
        }

        private List<int> CollectClientRecipients()
        {
            var recipients = new List<int>();
            foreach (HostPeerState peer in m_hostPeers.Values)
            {
                if (peer.ClientId <= 0) continue;
                recipients.Add(peer.ClientId);
            }
            recipients.Sort();
            return recipients;
        }

        private void PersistPeer(HostPeerState peer, PlayerStatSet stats, string why)
        {
            if (peer == null || string.IsNullOrEmpty(peer.RecordKey)) return;
            PlayerStatSet values = stats ?? peer.DisplayStats ?? peer.LastSent;
            if (values == null)
            {
                Problem("record-empty:" + peer.RecordKey,
                    "身份 " + DescribeKey(peer.RecordKey) + " 没有可落盘的统计值（" + why + "）→ 跳过记录写入");
                return;
            }
            if (!m_port.StoreRecord(peer.RecordKey, values, peer.Achievements, out string storeReason))
            {
                m_recordStoreFailures++;
                Problem("record-store:" + peer.RecordKey,
                    "写角色记录失败（身份 " + DescribeKey(peer.RecordKey) + "，" + why + "）：" +
                    storeReason);
                return;
            }
            m_recordStores++;
            if (!m_port.FlushRecords(out string flushReason))
            {
                m_recordStoreFailures++;
                Problem("record-flush",
                    "角色记录落盘失败（" + why + "）：" + flushReason);
            }
        }

        private void FlushAllPeers(string why)
        {
            foreach (HostPeerState peer in new List<HostPeerState>(m_hostPeers.Values))
                PersistPeer(peer, peer.DisplayStats ?? peer.LastSent, why);
            m_hostPeers.Clear();
            // 正在拼的上报 / 待重试的加入属于这一局：房间关了就不再等剩下的分片
            // （下个房间重新按加入握手建）。
            m_pendingReports.Clear();
            m_pendingJoins.Clear();
            Info("主机统计跟踪已清空（" + why + "）：所有身份的最后一份统计 / 成就已尝试落盘");
        }

        // ---------------------------------------------------------------- 接收

        private void OnSnapshot(MessageContext context, P.PlayerStatsSnapshot message)
        {
            if (!m_attached) return;
            if (m_control?.IsHosting == true)
            {
                if (context.ClientId <= 0)
                {
                    m_statsRejected += message?.Stats?.Count ?? 0;
                    Problem("snapshot-direction",
                        "主机收到一份没有发送者编号的统计快照 → 整条拒绝（快照是玩家端上报 / 主机下发，" +
                        "主机不会收到自己的消息）");
                    return;
                }
                // 主机：玩家端上报**它自己的**统计（identity 必须等于加入握手里的身份）。
                HandleClientReport(context.ClientId, message);
                return;
            }
            if (context.ClientId > 0)
            {
                m_statsRejected += message?.Stats?.Count ?? 0;
                Problem("snapshot-direction",
                    "客户端收到来自其它玩家端（clientId=" + context.ClientId +
                    "）的统计快照 → 整条拒绝：客户端只接受主机下发的数据");
                return;
            }
            m_statsMessagesRecv++;
            if (!PlayerStatsWire.TryDecodeSnapshot(message, out PlayerStatsChunk chunk,
                    out PlayerStatsWireError error, out string reason))
            {
                CountWireFailure(error);
                Problem("snapshot-decode", "统计快照解码失败（整条丢弃）：" + reason);
                return;
            }
            m_statsRecv += chunk.Stats.Count;
            m_achvRecv += chunk.Achievements.Count;
            foreach (AchievementUnlock unlock in chunk.Achievements)
                if (!AchievementCatalog.IsKnown(unlock.Id)) m_achvUnknown++;
            m_pendingSnapshots.TryGetValue(chunk.Identity, out PendingSnapshot pending);
            if (pending == null || chunk.ChunkIndex == 0 ||
                pending.Generation != chunk.Generation || pending.ChunkCount != chunk.ChunkCount)
            {
                pending = new PendingSnapshot
                {
                    Generation = chunk.Generation,
                    ChunkCount = chunk.ChunkCount,
                    Received = new bool[chunk.ChunkCount],
                    Name = chunk.PlayerName,
                    IsHost = chunk.ClientId == 0
                };
                m_pendingSnapshots[chunk.Identity] = pending;
            }
            if (pending.Received[chunk.ChunkIndex])
            {
                m_duplicateChunks++;
                m_statsSkipped += chunk.Stats.Count;
                Problem("snapshot-dup:" + chunk.Identity,
                    "统计快照分片重复（身份 " + DescribeKey(chunk.Identity) + " chunk " +
                    (chunk.ChunkIndex + 1) + "/" + chunk.ChunkCount + "）→ 跳过该片");
                return;
            }
            pending.Received[chunk.ChunkIndex] = true;
            pending.ReceivedCount++;
            MergeStats(chunk, pending.Stats, "snapshot:" + chunk.Identity);
            MergeAchievements(chunk, pending.Achievements, "snapshot:" + chunk.Identity);
            if (pending.ReceivedCount < pending.ChunkCount) return;
            m_pendingSnapshots.Remove(chunk.Identity);
            CompleteSnapshot(chunk.Identity, pending);
        }

        /// <summary>
        /// 主机：一个玩家端上报自己的统计（完整快照；按发送者分片组装，identity 必须与握手身份一致）。
        ///
        /// 为什么是客户端上报：统计只有玩家自己的那一端算得准（真实输入驱动的 ComponentMiner /
        /// ComponentLocomotion）。主机上的化身不跑那些逻辑、PlayerIndex 还会复用，从那里抓到的
        /// 不是这个人的进度（旧实现同样由客户端周期上报：ScMultiplayerProfileHandlers.cs:500）。
        /// </summary>
        private void HandleClientReport(int senderClientId, P.PlayerStatsSnapshot message)
        {
            if (!m_hostPeers.TryGetValue(senderClientId, out HostPeerState peer))
            {
                m_reportSenderUnknown++;
                m_reportRejected++;
                Problem("report-no-peer:" + senderClientId,
                    "收到 clientId=" + senderClientId + " 的统计上报，但本端没有跟踪这个玩家" +
                    "（还没加入 / 记录读取失败 / 已离开）→ 整条拒绝；客户端会周期重发");
                return;
            }
            if (!PlayerStatsWire.TryDecodeSnapshot(message, out PlayerStatsChunk chunk,
                    out PlayerStatsWireError error, out string reason))
            {
                m_reportMalformed++;
                CountWireFailure(error);
                Problem("report-decode:" + senderClientId,
                    "客户端统计上报解码失败（整条丢弃）：" + reason);
                return;
            }
            if (chunk.Achievements.Count > 0)
            {
                // 成就由主机按统计判定：客户端自报的成就一律拒绝（不允许伪造解锁）。
                m_reportRejected++;
                Problem("report-achievements:" + senderClientId,
                    "客户端上报里带有 " + chunk.Achievements.Count + " 个成就 → 整条拒绝" +
                    "（成就只能由主机判定）");
                return;
            }
            if (chunk.ClientId != 0 && chunk.ClientId != senderClientId)
            {
                m_reportRejected++;
                Problem("report-client-id:" + senderClientId,
                    "客户端上报里的 clientId=" + chunk.ClientId + " 与发送者 " + senderClientId +
                    " 不一致 → 整条拒绝");
                return;
            }
            if (string.IsNullOrEmpty(peer.Identity) ||
                !string.Equals(chunk.Identity, peer.Identity, StringComparison.Ordinal))
            {
                m_reportIdentityMismatch++;
                m_reportRejected++;
                Problem("report-identity:" + senderClientId,
                    "客户端上报的身份 " + DescribeKey(chunk.Identity) + " 与加入握手里的身份 " +
                    DescribeKey(peer.Identity) + " 不一致 → 整条拒绝" +
                    "（绝不把别人的统计写进这条记录）");
                return;
            }
            m_statsMessagesRecv++;
            m_statsRecv += chunk.Stats.Count;
            m_reportChunksRecv++;
            m_pendingReports.TryGetValue(senderClientId, out PendingReport pending);
            if (pending == null || chunk.ChunkIndex == 0 ||
                pending.Generation != chunk.Generation || pending.ChunkCount != chunk.ChunkCount)
            {
                pending = new PendingReport
                {
                    Generation = chunk.Generation,
                    ChunkCount = chunk.ChunkCount,
                    Received = new bool[chunk.ChunkCount]
                };
                m_pendingReports[senderClientId] = pending;
            }
            if (pending.Received[chunk.ChunkIndex])
            {
                m_duplicateChunks++;
                Problem("report-dup:" + senderClientId,
                    "客户端统计上报分片重复（clientId=" + senderClientId + " chunk " +
                    (chunk.ChunkIndex + 1) + "/" + chunk.ChunkCount + "）→ 跳过该片");
                return;
            }
            pending.Received[chunk.ChunkIndex] = true;
            pending.ReceivedCount++;
            MergeStats(chunk, pending.Stats, "report:" + senderClientId);
            if (pending.ReceivedCount < pending.ChunkCount) return;
            m_pendingReports.Remove(senderClientId);
            ApplyClientReport(peer, pending.Stats);
        }

        /// <summary>主机：把一份完整上报写进该身份的角色记录、判定成就、并把变化转发给其他玩家端。</summary>
        private void ApplyClientReport(HostPeerState peer, PlayerStatSet stats)
        {
            m_reportsRecv++;
            m_reportsApplied++;
            peer.DisplayStats = stats.Clone();
            List<KeyValuePair<string, string>> changed = stats.ChangedFrom(peer.LastSent);
            List<AchievementUnlock> newAchievements = EvaluateAchievements(peer, stats, "client-report");
            if (changed.Count == 0 && newAchievements.Count == 0)
            {
                m_reportUnchanged++;
                return;
            }
            PersistPeer(peer, stats, "客户端上报");
            if (!SendDeltaToAll(peer, changed, newAchievements, out string deltaReason))
            {
                Problem("report-delta:" + peer.RecordKey,
                    "客户端上报后的统计增量下发失败（基线不前进，下次上报重发变化集）：" + deltaReason);
                return;
            }
            peer.LastSent = stats.Clone();
            m_hostChanges++;
            Info("客户端统计已写进角色记录并转发：身份 " + DescribeKey(peer.Identity) +
                " · 统计 " + changed.Count + " 项 · 新成就 " + newAchievements.Count +
                " 项 · revision=" + m_revision);
        }

        /// <summary>该身份是不是**本机**的身份（客户端只允许把本机身份的恢复快照写进本机 PlayerStats）。</summary>
        private bool IsSelfIdentity(string identity)
        {
            string local = m_port?.LocalIdentity ?? string.Empty;
            return local.Length > 0 && string.Equals(identity, local, StringComparison.Ordinal);
        }

        private void CompleteSnapshot(string identity, PendingSnapshot pending)
        {
            bool self = IsSelfIdentity(identity);
            if (pending.Stats.Count == 0 && pending.Achievements.Count == 0)
            {
                if (!self)
                {
                    m_statsSkipped++;
                    Problem("snapshot-empty:" + identity,
                        "身份 " + DescribeKey(identity) + " 的完整快照既没有统计也没有成就 → 不建立视图" +
                        "（空快照没有展示价值）");
                    return;
                }
                if (m_selfRestored)
                {
                    // 已经恢复过：重复加入时主机会重发恢复快照，绝不能把本机继续累积的统计冲回去。
                    m_selfSnapshotDuplicates++;
                    return;
                }
                // 本机身份的**空恢复快照** = 主机记录里还没有本机统计。
                // 这不是"把统计清成 0"：本机 PlayerStats 一个字段都不动（本机槽位已经归本机身份所有）。
                m_selfRestoreReceived = true;
                m_selfRestored = true;
                m_desiredSelf = null;
                m_lastReportedSelf = null;
                m_lastReportMs = 0;
                m_lastReportSentMs = 0;
                m_snapshotsApplied++;
                Info("本机身份没有可恢复的统计（主机发的恢复快照为空）→ 本机统计保持当前值，" +
                    "之后由本机上报给主机");
                return;
            }
            m_clientViews.TryGetValue(identity, out ClientViewState previous);
            int newAchievements = 0;
            if (previous == null) newAchievements = pending.Achievements.Count;
            else
            {
                foreach (AchievementUnlock unlock in pending.Achievements.Entries)
                    if (!previous.Achievements.Contains(unlock.Id)) newAchievements++;
            }
            var view = new ClientViewState
            {
                Identity = identity,
                Name = pending.Name,
                IsHost = pending.IsHost
            };
            foreach (KeyValuePair<string, string> entry in pending.Stats.Entries)
                view.Stats.TrySet(entry.Key, entry.Value, out _);
            view.Achievements.TryMergeAll(pending.Achievements);
            m_clientViews[identity] = view;
            m_snapshotsApplied++;
            m_achvApplied += newAchievements;
            Info("已应用统计快照视图：身份 " + DescribeKey(identity) + "（" +
                (pending.IsHost ? "主机" : "客户端") + "）· " + pending.Stats.Count +
                " 项统计 · " + pending.Achievements.Count + " 个成就（新 " + newAchievements + "）" +
                (self ? " · 本机身份：作为恢复值写回本机 PlayerStats" : " · 只进视图，不碰本机 PlayerStats"));
            if (!self) return;
            m_selfRestoreReceived = true;
            if (m_selfRestored)
            {
                // 已经恢复过：主机在"重复加入"时会重发恢复快照，不能覆盖本机继续累积的统计。
                m_selfSnapshotDuplicates++;
                return;
            }
            m_desiredSelf = pending.Stats.Clone();
            TryApplySelfRestore("快照");
        }

        /// <summary>
        /// 把**本机身份**记录里的统计写回本机 PlayerStats（加入 / 重连的一次性恢复）。
        /// 失败保留 <see cref="m_desiredSelf"/> 等下一个周期重试（不假装成功）。
        /// </summary>
        private void TryApplySelfRestore(string why)
        {
            if (m_desiredSelf == null) return;
            if (!m_port.TryApplyLocal(m_desiredSelf, out PlayerStatSet applied, out string reason))
            {
                m_statsApplyFailures++;
                Problem("client-apply",
                    "把**本机身份**记录里的统计写回本机玩家失败（会每 " + ClientReconcileIntervalMs +
                    " ms 重试，不假装成功）：" + reason);
                return;
            }
            m_statsApplyRuns++;
            int skipped = 0;
            foreach (KeyValuePair<string, string> entry in m_desiredSelf.Entries)
            {
                if (applied != null && applied.Contains(entry.Key)) continue;
                if (m_unknownSelfNames.Add(entry.Key)) skipped++;
            }
            if (skipped > 0)
            {
                m_statsSkipped += skipped;
                Problem("client-unknown-stats",
                    "本机引擎不认识 " + skipped + " 项记录里的统计（跨版本）→ 已跳过并计数" +
                    "（统计面板不会显示这些字段）");
            }
            m_statsApplied += applied?.Count ?? 0;
            m_clientViews.TryGetValue(m_port.LocalIdentity, out ClientViewState selfView);
            if (selfView != null && applied != null)
            {
                foreach (KeyValuePair<string, string> entry in applied.Entries)
                    selfView.Stats.TrySet(entry.Key, entry.Value, out _);
            }
            m_desiredSelf = null;
            m_selfRestored = true;
            m_lastReportedSelf = null;
            m_lastReportMs = 0;
            m_lastReportSentMs = 0;
            Info("已按主机记录恢复**本机身份**的统计（" + (applied?.Count ?? 0) + " 项；" + why +
                "）→ 之后本机统计由本机行为继续累加并上报（不再被主机值回填）");
        }

        private void OnDelta(MessageContext context, P.PlayerStatsDelta message)
        {
            if (!m_attached) return;
            if (m_control?.IsHosting == true)
            {
                m_statsRejected += message?.Stats?.Count ?? 0;
                Problem("delta-direction",
                    "主机收到统计增量（clientId=" + context.ClientId +
                    "）→ 整条拒绝：增量只由主机下发，玩家端只上报完整快照");
                return;
            }
            if (context.ClientId > 0)
            {
                m_statsRejected += message?.Stats?.Count ?? 0;
                Problem("delta-direction",
                    "客户端收到来自其它玩家端（clientId=" + context.ClientId +
                    "）的统计增量 → 整条拒绝：只接受主机下发的数据");
                return;
            }
            m_statsMessagesRecv++;
            if (!PlayerStatsWire.TryDecodeDelta(message, out PlayerStatsChunk chunk,
                    out PlayerStatsWireError error, out string reason))
            {
                CountWireFailure(error);
                Problem("delta-decode", "统计增量解码失败（整条丢弃）：" + reason);
                return;
            }
            m_statsRecv += chunk.Stats.Count;
            m_achvRecv += chunk.Achievements.Count;
            if (chunk.Revision < m_lastRevision)
            {
                m_statsSkipped += chunk.Stats.Count + chunk.Achievements.Count;
                m_staleDeltas++;
                Problem("delta-stale:" + chunk.Identity,
                    "统计增量 revision=" + chunk.Revision + " < 已应用 " + m_lastRevision +
                    "（身份 " + DescribeKey(chunk.Identity) + "）→ 跳过（旧增量不覆盖新值）");
                return;
            }
            if (chunk.Revision > m_lastRevision) m_lastRevision = chunk.Revision;
            if (!m_clientViews.TryGetValue(chunk.Identity, out ClientViewState view))
            {
                view = new ClientViewState
                {
                    Identity = chunk.Identity,
                    Name = chunk.PlayerName,
                    IsHost = chunk.ClientId == 0
                };
                m_clientViews[chunk.Identity] = view;
            }
            else if (!string.IsNullOrEmpty(chunk.PlayerName))
            {
                view.Name = chunk.PlayerName;
            }
            int newAchievements = 0;
            foreach (AchievementUnlock unlock in chunk.Achievements)
            {
                if (!AchievementCatalog.IsKnown(unlock.Id)) m_achvUnknown++;
                if (!view.Achievements.TryMerge(unlock, out bool added, out string mergeReason))
                {
                    m_achvMalformed++;
                    Problem("delta-achv:" + chunk.Identity,
                        "合并成就 " + unlock.Id + " 失败：" + mergeReason + "（跳过该条）");
                    continue;
                }
                if (added) newAchievements++;
            }
            m_achvApplied += newAchievements;
            foreach (KeyValuePair<string, string> entry in chunk.Stats.Entries)
            {
                if (!view.Stats.TrySet(entry.Key, entry.Value, out string setReason))
                {
                    m_statsMalformed++;
                    Problem("delta-stat:" + chunk.Identity,
                        "合并统计 " + entry.Key + " 失败：" + setReason + "（跳过该条）");
                }
            }
            m_deltasApplied++;
            if (!IsSelfIdentity(chunk.Identity)) return;
            if (m_selfRestored)
            {
                // 恢复完成后本机统计由本机行为产生：主机对自己上报的回显不再写回本机 PlayerStats
                // （写回会把最近一秒刚积累的进度冲掉）。计数可见，不刷屏。
                m_selfEchoStatsSkipped += chunk.Stats.Count;
                return;
            }
            if (m_desiredSelf == null) m_desiredSelf = new PlayerStatSet();
            foreach (KeyValuePair<string, string> entry in chunk.Stats.Entries)
                m_desiredSelf.TrySet(entry.Key, entry.Value, out _);
            if (m_selfRestoreReceived) TryApplySelfRestore("增量（恢复前）");
        }

        private void MergeStats(PlayerStatsChunk chunk, PlayerStatSet target, string key)
        {
            foreach (KeyValuePair<string, string> entry in chunk.Stats.Entries)
            {
                if (target.TrySet(entry.Key, entry.Value, out string reason)) continue;
                m_statsMalformed++;
                Problem("merge-stat:" + key, "合并统计 " + entry.Key + " 失败：" + reason);
            }
        }

        private void MergeAchievements(PlayerStatsChunk chunk, AchievementLedger target, string key)
        {
            foreach (AchievementUnlock unlock in chunk.Achievements)
            {
                if (target.TryMerge(unlock, out _, out string reason)) continue;
                m_achvMalformed++;
                Problem("merge-achv:" + key, "合并成就 " + unlock.Id + " 失败：" + reason);
            }
        }

        private void CountWireFailure(PlayerStatsWireError error)
        {
            switch (error)
            {
                case PlayerStatsWireError.AchievementMalformed:
                    m_achvMalformed++;
                    break;
                case PlayerStatsWireError.StatsMalformed:
                    m_statsMalformed++;
                    break;
                default:
                    m_decodeFailures++;
                    break;
            }
        }

        // ---------------------------------------------------------------- 客户端：恢复 / 上报

        private void TickClient(long nowMs)
        {
            bool connected = m_transport != null && m_transport.IsConnected;
            if (!connected)
            {
                if (m_clientWasConnected)
                {
                    m_clientWasConnected = false;
                    m_sessionStale = true;
                    Info("统计同步：连接已断开 → 停止上报，本机统计留在本机" +
                        "（重连后主机会重发本机身份的恢复快照）");
                }
                return;
            }
            string hostIdentity = m_transport.HostIdentity ?? string.Empty;
            int localClientId = m_transport.LocalClientId;
            if (!m_clientWasConnected)
            {
                m_clientWasConnected = true;
                // 首次连接**不能**清空已经收到的快照：统计帧可能由地形模块先于本模块 Tick 派发，
                // 清空会把刚恢复的本机值丢掉（之后只有重连才会有新快照）。
                bool hostChanged = m_lastHostIdentity.Length > 0 &&
                    !string.Equals(m_lastHostIdentity, hostIdentity, StringComparison.Ordinal);
                bool idChanged = m_lastLocalClientId >= 0 && localClientId != m_lastLocalClientId;
                if (m_sessionStale || hostChanged || idChanged)
                    ResetClientSession("重连 / 换了主机");
                m_sessionStale = false;
                m_lastLocalClientId = localClientId;
                m_lastHostIdentity = hostIdentity;
            }
            else if (localClientId != m_lastLocalClientId ||
                     !string.Equals(m_lastHostIdentity, hostIdentity, StringComparison.Ordinal))
            {
                m_lastLocalClientId = localClientId;
                m_lastHostIdentity = hostIdentity;
                ResetClientSession("重连 / 通道编号或主机身份变化");
            }
            if (nowMs - m_lastReconcileMs < ClientReconcileIntervalMs) return;
            m_lastReconcileMs = nowMs;
            // ① 本机统计槽位归属：下载世界自带的是**主机角色**的统计，必须先搬走（一次 / 世界）。
            EnsureLocalStatsOwnership();
            // ② 恢复还没完成：重试写回本机身份的记录值（失败不假装成功）。
            if (m_desiredSelf != null)
            {
                TryApplySelfRestore("重试");
                return;
            }
            if (!m_selfRestored)
            {
                if (m_selfRestoreWaitSinceMs == 0) m_selfRestoreWaitSinceMs = nowMs;
                if (!m_selfRestorePendingLogged &&
                    nowMs - m_selfRestoreWaitSinceMs >= SelfRestorePendingLogMs)
                {
                    m_selfRestorePendingLogged = true;
                    Problem("self-restore-pending",
                        "还没有收到主机按本机身份下发的统计恢复快照 → 本机统计只在本机显示，暂不上报" +
                        "（避免用未恢复的值覆盖记录里的旧进度；主机恢复后开始上报）");
                }
                return;
            }
            // ③ 本机统计是唯一算得准的来源：变化了（或每 10 s 心跳）就上报给主机入库。
            ReportSelf(nowMs);
        }

        /// <summary>
        /// 客户端：把**下载世界带来的主机统计**从本机槽位分出去（幂等；由端口记录一次 / 世界）。
        /// 失败必须计数 + 限流日志（否则统计面板会一直显示主机角色的数据）。
        /// </summary>
        private void EnsureLocalStatsOwnership()
        {
            if (m_port == null) return;
            LocalStatsOwnership ownership = m_port.EnsureLocalStatsOwnership(out string reason);
            if (ownership == LocalStatsOwnership.Failed)
            {
                m_ownershipFailures++;
                Problem("local-ownership",
                    "无法把下载世界带来的主机统计从本机玩家槽位分离" +
                    "（统计面板会显示主机角色的数据）：" + reason);
            }
        }

        /// <summary>
        /// 客户端：把本机统计上报给主机（完整快照，identity = 本机身份）。
        /// 统计没变化时按 <see cref="ClientReportHeartbeatMs"/> 重发，保证主机在丢帧 / 记录读取
        /// 失败后仍能拿到（不依赖"下一次统计变化"）。失败必须计数，基线不前进。
        /// </summary>
        private void ReportSelf(long nowMs)
        {
            if (nowMs - m_lastReportMs < ClientReportIntervalMs) return;
            string identity = m_port.LocalIdentity ?? string.Empty;
            if (identity.Length == 0)
            {
                m_reportIdentityMissing++;
                Problem("report-identity-missing",
                    "本机身份不可用（UserId.dat 为空）→ 无法把本机统计上报给主机" +
                    "（主机也无法按稳定身份绑定；本机统计仍在本机显示）");
                return;
            }
            if (!m_port.TryCaptureLocal(out PlayerStatsWorldView local, out string captureReason) ||
                local?.Stats == null)
            {
                m_statsCaptureFailures++;
                Problem("client-capture", "读取本机玩家统计失败（本机上报跳过）：" + captureReason);
                return;
            }
            m_lastReportMs = nowMs;
            bool changed = m_lastReportedSelf == null ||
                !local.Stats.ContentEquals(m_lastReportedSelf);
            if (!changed && nowMs - m_lastReportSentMs < ClientReportHeartbeatMs)
            {
                m_reportsSkipped++;
                return;
            }
            int clientId = m_transport.LocalClientId;
            if (clientId < 0) clientId = 0;
            if (!PlayerStatsWire.TryEncodeSnapshot(clientId, identity, local.Name,
                    ++m_generationCounter, local.Stats, Array.Empty<AchievementUnlock>(),
                    out List<P.PlayerStatsSnapshot> messages, out string encodeReason))
            {
                m_encodeFailures++;
                Problem("report-encode", "本机统计上报编码失败（下个周期重试）：" + encodeReason);
                return;
            }
            foreach (P.PlayerStatsSnapshot message in messages)
            {
                if (!m_transport.TrySend(MessageType.PlayerStatsSnapshot, message.ToByteArray()))
                {
                    m_reportSendFailures++;
                    Problem("report-send", "本机统计上报发送失败（通道背压 / 未连接；下个周期重试，" +
                        "不假装成功）");
                    return;
                }
                m_statsMessagesSent++;
                m_statsSent += message.Stats.Count;
            }
            m_reportsSent++;
            m_reportChunks += messages.Count;
            m_lastReportSentMs = nowMs;
            m_lastReportedSelf = local.Stats.Clone();
        }

        private void ResetClientSession(string why)
        {
            m_clientViews.Clear();
            m_pendingSnapshots.Clear();
            m_desiredSelf = null;
            m_selfRestoreReceived = false;
            m_selfRestored = false;
            m_lastReportedSelf = null;
            m_lastReportMs = 0;
            m_lastReportSentMs = 0;
            m_selfRestoreWaitSinceMs = 0;
            m_selfRestorePendingLogged = false;
            m_lastRevision = 0;
            m_unknownSelfNames.Clear();
            Info("统计同步会话已重置（" + why + "）：清空上一轮主机的统计 / 成就副本；" +
                "本机 PlayerStats 保持本机身份的值，等主机重发本机身份的恢复快照");
        }

        // ---------------------------------------------------------------- UI 视图

        public IReadOnlyList<SyncedPlayerState> SnapshotPlayers()
        {
            var list = new List<SyncedPlayerState>();
            string localIdentity = m_port?.LocalIdentity ?? string.Empty;
            if (m_control?.IsHosting == true)
            {
                foreach (HostPeerState peer in m_hostPeers.Values)
                {
                    if (list.Count >= MaxDisplayPlayers) break;
                    list.Add(ToState(peer.Identity, peer.Name, peer.ClientId == 0,
                        string.Equals(peer.Identity, localIdentity, StringComparison.Ordinal),
                        peer.DisplayStats ?? peer.LastSent, peer.Achievements));
                }
            }
            else
            {
                foreach (ClientViewState view in m_clientViews.Values)
                {
                    if (list.Count >= MaxDisplayPlayers) break;
                    list.Add(ToState(view.Identity, view.Name, view.IsHost,
                        string.Equals(view.Identity, localIdentity, StringComparison.Ordinal),
                        view.Stats, view.Achievements));
                }
            }
            list.Sort((a, b) =>
            {
                if (a.IsHost != b.IsHost) return a.IsHost ? -1 : 1;
                if (a.IsSelf != b.IsSelf) return a.IsSelf ? -1 : 1;
                return string.CompareOrdinal(a.Name, b.Name);
            });
            return list;
        }

        private static SyncedPlayerState ToState(string identity, string name, bool isHost,
            bool isSelf, PlayerStatSet stats, AchievementLedger achievements)
        {
            var unlocks = new List<AchievementUnlock>();
            if (achievements != null) unlocks.AddRange(achievements.Entries);
            return new SyncedPlayerState(identity, name, isSelf, isHost,
                stats?.Clone() ?? new PlayerStatSet(), unlocks);
        }

        public bool TryGetSelf(out SyncedPlayerState self, out string reason)
        {
            self = null;
            string identity = m_port?.LocalIdentity ?? string.Empty;
            if (identity.Length == 0)
            {
                reason = "本机身份不可用（UserId.dat 为空）→ 无法对应主机的统计身份";
                return false;
            }
            if (m_control?.IsHosting == true)
            {
                foreach (HostPeerState peer in m_hostPeers.Values)
                {
                    if (!string.Equals(peer.Identity, identity, StringComparison.Ordinal)) continue;
                    self = ToState(peer.Identity, peer.Name, true, true,
                        peer.DisplayStats ?? peer.LastSent, peer.Achievements);
                    reason = null;
                    return true;
                }
                reason = "主机本机角色还没有进入世界（等主机抓取到本机 PlayerData）";
                return false;
            }
            if (!m_clientViews.TryGetValue(identity, out ClientViewState view))
            {
                reason = "还没有收到主机为本机身份下发的统计快照" +
                    (m_clientWasConnected ? string.Empty : "（当前没有连接）");
                return false;
            }
            self = ToState(view.Identity, view.Name, view.IsHost, true, view.Stats.Clone(),
                view.Achievements);
            reason = null;
            return true;
        }

        public string BuildReport()
        {
            var text = new StringBuilder();
            text.AppendLine("Statistics & achievements synced through the host.");
            text.AppendLine("Each player's statistics are computed on that player's own client; " +
                "the host stores them per identity and decides achievements.");
            text.AppendLine();
            IReadOnlyList<SyncedPlayerState> players = SnapshotPlayers();
            if (players.Count == 0)
            {
                text.AppendLine("No synced player data yet.");
                text.AppendLine(m_control?.IsHosting == true
                    ? "The host has not captured its own or any client player yet."
                    : "Not connected to a room, or the host has not sent the first snapshot yet.");
                text.AppendLine();
                text.AppendLine(StatusLine());
                return text.ToString();
            }
            foreach (SyncedPlayerState player in players)
            {
                string tag = player.IsHost ? "[Host]" : player.IsSelf ? "[You]" : "[Client]";
                text.AppendLine(tag + " " + (string.IsNullOrEmpty(player.Name) ? "(no name)" : player.Name) +
                    "  (" + DescribeKey(player.Identity) + ")");
                text.AppendLine("  Statistics:");
                int shown = 0;
                foreach (KeyValuePair<string, string> entry in player.Stats.Entries)
                {
                    if (shown++ >= MaxReportStats)
                    {
                        text.AppendLine("    … (" + (player.Stats.Count - MaxReportStats) +
                            " more)");
                        break;
                    }
                    text.AppendLine("    " + entry.Key + " = " + entry.Value);
                }
                if (player.Stats.Count == 0) text.AppendLine("    (none synced)");
                text.AppendLine("  Achievements:");
                int achievementCount = 0;
                foreach (AchievementUnlock unlock in player.Achievements)
                {
                    if (achievementCount++ >= MaxReportAchievements)
                    {
                        text.AppendLine("    …");
                        break;
                    }
                    text.AppendLine("    " + AchievementCatalog.Describe(unlock.Id) + " · " +
                        FormatUtc(unlock.UnlockedUtcSeconds));
                }
                if (player.Achievements.Count == 0) text.AppendLine("    (none unlocked)");
                text.AppendLine();
            }
            text.AppendLine(StatusLine());
            return text.ToString();
        }

        private static string FormatUtc(long utcSeconds)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(utcSeconds).UtcDateTime
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
            }
            catch (ArgumentOutOfRangeException)
            {
                return utcSeconds.ToString(CultureInfo.InvariantCulture) + " (raw UTC seconds)";
            }
        }

        // ---------------------------------------------------------------- 诊断

        public string StatusLine()
        {
            return "player-stats: role=" + (m_control?.IsHosting == true ? "host" : "client") +
                " attached=" + m_attached +
                " peers=" + m_hostPeers.Count + " views=" + m_clientViews.Count +
                " statsSent=" + m_statsSent + " statsRecv=" + m_statsRecv +
                " statsApplied=" + m_statsApplied + " statsRejected=" + m_statsRejected +
                " statsSkipped=" + m_statsSkipped +
                " statsMalformed=" + m_statsMalformed +
                " statsSendFailures=" + m_statsSendFailures +
                " statsNoRecipients=" + m_statsNoRecipients +
                " statsApplyFailures=" + m_statsApplyFailures +
                " statsCaptureFailures=" + m_statsCaptureFailures +
                " statsMessagesSent=" + m_statsMessagesSent +
                " statsMessagesRecv=" + m_statsMessagesRecv +
                " reportsSent=" + m_reportsSent + " reportChunks=" + m_reportChunks +
                " reportSendFailures=" + m_reportSendFailures +
                " reportsSkipped=" + m_reportsSkipped +
                " reportsRecv=" + m_reportsRecv + " reportChunksRecv=" + m_reportChunksRecv +
                " reportsApplied=" + m_reportsApplied +
                " reportsUnchanged=" + m_reportUnchanged +
                " reportRejected=" + m_reportRejected +
                " reportMalformed=" + m_reportMalformed +
                " reportIdentityMismatch=" + m_reportIdentityMismatch +
                " reportSenderUnknown=" + m_reportSenderUnknown +
                " reportIdentityMissing=" + m_reportIdentityMissing +
                " selfEchoSkipped=" + m_selfEchoStatsSkipped +
                " selfSnapshotDuplicates=" + m_selfSnapshotDuplicates +
                " selfRestored=" + m_selfRestored +
                " ownershipFailures=" + m_ownershipFailures +
                " achvSent=" + m_achvSent + " achvRecv=" + m_achvRecv +
                " achvApplied=" + m_achvApplied + " achvUnknown=" + m_achvUnknown +
                " achvMalformed=" + m_achvMalformed +
                " achvEvaluateSkipped=" + m_achvEvaluateSkipped +
                " achvRecordRejected=" + m_achvRecordRejected +
                " snapshotsSent=" + m_snapshotsSent + " deltasSent=" + m_deltasSent +
                " snapshotsApplied=" + m_snapshotsApplied + " deltasApplied=" + m_deltasApplied +
                " staleDeltas=" + m_staleDeltas + " duplicateChunks=" + m_duplicateChunks +
                " encodeFailures=" + m_encodeFailures + " decodeFailures=" + m_decodeFailures +
                " hostPolls=" + m_hostPolls + " hostChanges=" + m_hostChanges +
                " hostCaptureFailures=" + m_hostCaptureFailures +
                " recordLoads=" + m_recordLoads + " recordMissing=" + m_recordMissing +
                " recordLoadFailures=" + m_recordLoadFailures +
                " recordStores=" + m_recordStores + " recordStoreFailures=" + m_recordStoreFailures +
                " identityDegraded=" + m_identityDegraded +
                " identityChanges=" + m_identityChanges +
                " duplicateIdentities=" + m_duplicateIdentities +
                " applyRuns=" + m_statsApplyRuns +
                " logSuppressed=" + m_suppressedProblems;
        }

        private string Summary() => StatusLine();

        private void Info(string message) => m_context?.Diagnostics.Info(m_descriptor.Id, message);

        private void Error(string message) => m_context?.Diagnostics.Error(m_descriptor.Id, message, null);

        private void LogInfo(string message) => Info(message);

        /// <summary>
        /// 显式问题日志：同一 key 每 <see cref="ProblemLogMs"/> 毫秒最多一条（计数不丢；
        /// 被压掉的条数在状态行 logSuppressed）。绝不静默。
        /// </summary>
        private void Problem(string key, string message)
        {
            long nowMs = (long)(m_now() * 1000.0);
            if (m_lastProblemMs.TryGetValue(key, out long last) &&
                nowMs - last < ProblemLogMs)
            {
                m_suppressedProblems++;
                return;
            }
            m_lastProblemMs[key] = nowMs;
            m_context?.Diagnostics.Warn(m_descriptor.Id,
                message + "（同类日志每 " + (ProblemLogMs / 1000) + " 秒最多一条）");
        }

        private static List<AchievementUnlock> AchievementList(AchievementLedger ledger)
        {
            var list = new List<AchievementUnlock>();
            if (ledger != null) list.AddRange(ledger.Entries);
            return list;
        }

        /// <summary>日志 / UI 用身份描述：user: 键只显示前 8 位身份（不泄露完整 GUID）。</summary>
        public static string DescribeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "(none)";
            const string userPrefix = "user:";
            if (!key.StartsWith(userPrefix, StringComparison.Ordinal)) return key;
            string identity = key.Substring(userPrefix.Length);
            if (identity.Length <= 8) return key;
            return userPrefix + identity.Substring(0, 8) + "...";
        }
    }
}
