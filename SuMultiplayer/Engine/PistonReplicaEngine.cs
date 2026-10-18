using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 移动组（活塞 / 塌落）运动表现的**引擎适配层**（<see cref="IPistonMovePort"/> 的实现，
    /// 组合根注入）。同一个适配器服务两种引擎集合（kind 区分，
    /// SuMultiplayer.Net.MovingBlockKind）：
    ///   · PISTON     —— Id=="Piston"（<c>SubsystemPistonBlockBehavior</c> 的推 / 拉 / 收回）；
    ///   · COLLAPSING —— Id=="CollapsingBlock"（<c>SubsystemCollapsingBlockBehavior</c> 的沙子 / 沙砾塌落）。
    /// 两者共享 SubsystemMovingBlocks 的插值、距离 / 区块 / 上限判定、看门狗与放弃口径；
    /// 差别只在"客户端本地集合的 Id / 起点格校验 / 引擎残留集合的匹配方式"。
    ///
    /// 主机（由两个外壳直接调用 <see cref="OnHostMoveStarted"/> / <see cref="OnHostMoveEnded"/>）：
    ///   · 开始时把引擎 AddMovingBlockSet 的**全部参数**（含种类）转给模块 → PistonMoveEvent；
    ///   · 引擎把集合移除时（正常到点 / 碰撞中断 / 活塞被拆 / 区块卸载 / 塌落落地）
    ///     把 completed 转给模块 → PistonMoveEnd。
    ///
    /// 客户端：
    ///   · <see cref="Pump"/> 在 Project 换新时重新挂接注入后的两个外壳与 SubsystemMovingBlocks；
    ///   · <see cref="Present"/> 只按主机同一份参数建**本地只表现集合**
    ///     （PISTON → id:"PistonReplica"，COLLAPSING → id:"CollapsingBlockReplica"，
    ///     都 <c>testCollision:false</c>）：不写格、不生成实体、不伤害；
    ///     距离 / 区块 / 起点格 / 上限判定都在这里；
    ///   · 本地集合到点（活塞）/ 落地（塌落）后**继续停在原地**，直到主机的 PistonMoveEnd
    ///     到达才移除（避免"本地先消失、主机最终格后到"的闪烁）；最终格一律取主机 CellChange；
    ///   · 看门狗：结束消息迟迟不到 / 区块被卸载时明确放弃（计数 + 事件）。
    ///
    /// 无静态状态：化身表 / 兴趣窗口解析器 / 诊断都由组合根以实例注入。
    /// </summary>
    public sealed class PistonReplicaEngine : IPistonMovePort, IPistonPeerViewportPort
    {
        private const int ProblemLogMs = 5000;
        private const int LocalMoveLogMs = 10000;

        private sealed class LocalMove
        {
            public IMovingBlockSet Set;
            public PistonMoveData Move;
            public uint StartEventId;
            public bool Arrived;
            public long ArrivedMs;
            public long DeadlineMs;
        }

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly PistonClientRangeResolver m_rangeResolver;
        private readonly Dictionary<MovingBlockKey, LocalMove> m_local =
            new Dictionary<MovingBlockKey, LocalMove>();
        private readonly List<MovingBlockKey> m_removals = new List<MovingBlockKey>();
        private readonly List<IMovingBlockSet> m_untracked = new List<IMovingBlockSet>();
        private readonly MovingBlockSetMotion m_motion = new MovingBlockSetMotion();
        /// <summary>每个 (kind, 格) 最近一次收到的开始 event_id（结束消息的键级排序用）。</summary>
        private readonly Dictionary<MovingBlockKey, uint> m_lastSeenStart =
            new Dictionary<MovingBlockKey, uint>();
        /// <summary>因为主机权威格变化而提前移除的本地集合（随后的结束消息仍算"正常结束"）。</summary>
        private readonly HashSet<MovingBlockKey> m_authoritativelyRemoved =
            new HashSet<MovingBlockKey>();
        private readonly HashSet<string> m_loggedOnce = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> m_lastProblemMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        private Project m_project;
        private SuSubsystemPistonBlockBehavior m_piston;
        private SuSubsystemCollapsingBlockBehavior m_collapsing;
        private SubsystemMovingBlocks m_movingBlocks;
        private SubsystemTerrain m_terrain;
        private bool m_clientReplica;
        private bool m_stoppedSubscribed;

        // ---------------------------------------------------------------- 计数
        private long m_hostStartedMove;
        private long m_hostStartedCollapsing;
        private long m_hostEndedMove;
        private long m_hostEndedCollapsing;
        private long m_hostRejected;
        private long m_hostNoListener;
        private long m_created;
        private long m_createdCollapsing;
        private long m_replaced;
        private long m_removedFinished;
        private long m_removedAborted;
        private long m_skippedDistance;
        private long m_skippedCap;
        private long m_skippedChunk;
        private long m_skippedCell;
        private long m_skippedNoPlayer;
        private long m_presentFailed;
        private long m_addFailed;
        private long m_startReadFailures;
        private long m_authoritativeCancelled;
        private long m_authoritativePenetrations;
        private long m_endBeforeStart;
        private long m_watchdog;
        private long m_projectResets;
        private long m_stoppedEvents;
        private long m_untrackedRemoved;
        private long m_straySuperseded;
        private long m_lastLocalMs;

        public PistonReplicaEngine(NetworkAvatarSpawner spawner, IModuleDiagnostics diagnostics,
            PistonClientRangeResolver rangeResolver, string ownerId)
        {
            m_spawner = spawner;
            m_diagnostics = diagnostics;
            m_rangeResolver = rangeResolver;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "piston-sync" : ownerId;
        }

        public event Action<PistonMoveData> HostPistonMoveStarted;
        public event Action<PistonMoveEndData> HostPistonMoveEnded;
        public event Action<MovingBlockKind, Point3, string> ClientMoveAborted;

        public bool IsClientReplica => m_clientReplica;

        // ------------------------------------------------------------ 挂接

        public void Pump()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_project))
            {
                AbortAll("project-changed", out _, out _);
                DetachShells();
                m_lastSeenStart.Clear();
                m_authoritativelyRemoved.Clear();
                m_project = project;
                m_projectResets++;
            }
            if (project == null) return;

            m_clientReplica = project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain shell &&
                              shell.ClientReplica;
            SuSubsystemPistonBlockBehavior piston =
                project.FindSubsystem<SuSubsystemPistonBlockBehavior>(false);
            SuSubsystemCollapsingBlockBehavior collapsing =
                project.FindSubsystem<SuSubsystemCollapsingBlockBehavior>(false);
            if (piston == null && collapsing == null)
            {
                LogOnce("moving-block-subsystem-missing",
                    "既没有注入后的 SuSubsystemPistonBlockBehavior，也没有 " +
                    "SuSubsystemCollapsingBlockBehavior（引擎注入可能未生效）→ " +
                    "移动组运动同步无法挂接（主机不会广播，客户端也收不到；最终格仍由 CellChange 同步）");
                return;
            }
            if (piston == null)
            {
                LogOnce("piston-subsystem-missing",
                    "没有找到注入后的 SuSubsystemPistonBlockBehavior（引擎注入可能未生效）→ " +
                    "活塞运动不会广播（塌落复制不受影响；最终格仍由 CellChange 同步）");
            }
            if (collapsing == null)
            {
                LogOnce("collapsing-subsystem-missing",
                    "没有找到注入后的 SuSubsystemCollapsingBlockBehavior（引擎注入可能未生效）→ " +
                    "塌落运动不会广播（客户端仍看不到沙子 / 沙砾下落，只看到最终格；活塞复制不受影响）");
            }
            if (!ReferenceEquals(piston, m_piston) || !ReferenceEquals(collapsing, m_collapsing))
            {
                DetachShells();
                m_piston = piston;
                m_collapsing = collapsing;
                m_movingBlocks = project.FindSubsystem<SubsystemMovingBlocks>(false);
                m_terrain = project.FindSubsystem<SubsystemTerrain>(false);
                if (m_piston != null) m_piston.Replica = this;
                if (m_collapsing != null) m_collapsing.Replica = this;
                if (m_movingBlocks != null && !m_stoppedSubscribed)
                {
                    m_movingBlocks.Stopped += OnMovingBlockSetStopped;
                    m_stoppedSubscribed = true;
                }
                Log("已挂接注入后的移动组外壳（" + (m_clientReplica ? "客户端=表现" : "主机=权威") +
                    "）：piston=" + (m_piston != null ? m_piston.GetType().Name : "缺失") +
                    " collapsing=" + (m_collapsing != null ? m_collapsing.GetType().Name : "缺失") +
                    " · movingBlocks=" + (m_movingBlocks != null ? "ok" : "缺失"));
            }
            else
            {
                if (m_piston != null && m_piston.Replica == null)
                {
                    m_piston.Replica = this;
                    Log("活塞子系统的挂接点被清空 → 已重新挂接：" + m_piston.GetType().Name);
                }
                if (m_collapsing != null && m_collapsing.Replica == null)
                {
                    m_collapsing.Replica = this;
                    Log("塌落子系统的挂接点被清空 → 已重新挂接：" + m_collapsing.GetType().Name);
                }
            }
            if (!m_clientReplica) return;
            RemoveUntrackedReplicaSets();
            UpdateClientWatchdog();
        }

        /// <summary>
        /// 客户端：移除非本会话创建的只表现集合（存档里残留的主机中途副本 / 上一次会话遗留）。
        /// 只表现集合一旦落进存档，重载后没有人会再发结束消息 ⇒ 会永远停在目标位置；
        /// 这里按"是否在本地账本里"明确清理并计数（不静默）。
        /// </summary>
        private void RemoveUntrackedReplicaSets()
        {
            SubsystemMovingBlocks moving = m_movingBlocks;
            if (moving == null) return;
            m_untracked.Clear();
            for (int i = 0; i < moving.MovingBlockSets.Count; i++)
            {
                IMovingBlockSet set = moving.MovingBlockSets[i];
                if (set == null) continue;
                if (!PistonReplicaPresentation.IsReplicaId(set.Id)) continue;
                if (IsLocallyTracked(set)) continue;
                m_untracked.Add(set);
            }
            for (int i = 0; i < m_untracked.Count; i++)
            {
                moving.RemoveMovingBlockSet(m_untracked[i]);
                m_untrackedRemoved++;
                LogProblem("untracked-replica", "移除了不在本地账本里的只表现移动组集合（" +
                    m_untracked[i].Id + "；存档残留 / 上次会话遗留，不写格）；累计 " +
                    m_untrackedRemoved);
            }
            m_untracked.Clear();
        }

        private bool IsLocallyTracked(IMovingBlockSet set)
        {
            foreach (KeyValuePair<MovingBlockKey, LocalMove> pair in m_local)
            {
                if (ReferenceEquals(pair.Value.Set, set)) return true;
            }
            return false;
        }

        /// <summary>Mod 卸载 / 组合根关闭：摘掉挂接点并放弃本地动画。</summary>
        public void Shutdown()
        {
            AbortAll("shutdown", out _, out _);
            DetachShells();
            m_project = null;
            m_clientReplica = false;
        }

        /// <summary>摘掉两个外壳的挂接点（活塞 / 塌落共用同一个 SubsystemMovingBlocks）。</summary>
        private void DetachShells()
        {
            if (m_movingBlocks != null && m_stoppedSubscribed)
            {
                try { m_movingBlocks.Stopped -= OnMovingBlockSetStopped; } catch (Exception) { }
                m_stoppedSubscribed = false;
            }
            if (m_piston != null)
            {
                try { m_piston.Replica = null; } catch (Exception) { }
                m_piston = null;
            }
            if (m_collapsing != null)
            {
                try { m_collapsing.Replica = null; } catch (Exception) { }
                m_collapsing = null;
            }
            m_movingBlocks = null;
            m_terrain = null;
        }

        // ------------------------------------------------------------ 主机：外壳上报

        /// <summary>
        /// 由两个外壳在检测到**引擎刚刚创建**的集合时调用（每次运动开始时一次）：
        /// <c>SuSubsystemPistonBlockBehavior</c>（Id=="Piston"）/
        /// <c>SuSubsystemCollapsingBlockBehavior</c>（Id=="CollapsingBlock"）。
        /// </summary>
        public void OnHostMoveStarted(in PistonMoveData move)
        {
            if (move.Kind == MovingBlockKind.Collapsing) m_hostStartedCollapsing++;
            m_hostStartedMove++;
            if (m_clientReplica)
            {
                m_hostRejected++;
                LogProblem("host-start-on-client", "客户端副本上的外壳上报了运动开始（" +
                    move.Describe() + "）→ 丢弃（客户端绝不广播）");
                return;
            }
            Action<PistonMoveData> handler = HostPistonMoveStarted;
            if (handler == null)
            {
                m_hostNoListener++;
                LogOnce("start-no-listener", "外壳上报了运动开始，但还没有监听者（模块未装配）→ " +
                    "本批只计数，不广播（累计 " + m_hostNoListener + " 条）");
                return;
            }
            try
            {
                handler(move);
            }
            catch (Exception e)
            {
                LogProblem("emit-start:" + e.GetType().Name, "运动开始广播处理器异常（" +
                    move.Describe() + "）：" + e.GetType().Name + " " + e.Message +
                    " → 本帧其余运动与本地引擎表现不受影响");
            }
        }

        /// <summary>由两个外壳在集合被引擎移除后调用（每次运动结束一次，种类见 end.Kind）。</summary>
        public void OnHostMoveEnded(in PistonMoveEndData end)
        {
            if (end.Kind == MovingBlockKind.Collapsing) m_hostEndedCollapsing++;
            m_hostEndedMove++;
            if (m_clientReplica) return;
            Action<PistonMoveEndData> handler = HostPistonMoveEnded;
            if (handler == null)
            {
                m_hostNoListener++;
                LogOnce("end-no-listener", "外壳上报了运动结束，但还没有监听者（模块未装配）→ " +
                    "只计数，不广播（累计 noListener=" + m_hostNoListener + "）");
                return;
            }
            try
            {
                handler(end);
            }
            catch (Exception e)
            {
                LogProblem("emit-end:" + e.GetType().Name, "运动结束广播处理器异常（" +
                    end.Describe() + "）：" + e.GetType().Name + " " + e.Message);
            }
        }

        // ------------------------------------------------------------ 客户端：本地只表现集合

        public PistonPresentOutcome Present(uint startEventId, in PistonMoveData move,
            out string reason)
        {
            if (!IsLiveClientReplica())
            {
                reason = "not-client-replica";
                return PistonPresentOutcome.Failed;
            }
            if (startEventId == 0)
            {
                m_presentFailed++;
                reason = "start-event-id-zero";
                return PistonPresentOutcome.Failed;
            }
            // 消息分发（地形模块 tick）可能先于本模块 tick 的 Pump：Project 换新时先重新挂接，
            // 否则会把本地集合建到上一个（已卸载）世界的 SubsystemMovingBlocks 里。
            if (!ReferenceEquals(GameManager.Project, m_project)) Pump();
            if (m_project == null || m_movingBlocks == null || m_terrain?.Terrain == null)
            {
                m_presentFailed++;
                reason = "no-world";
                return PistonPresentOutcome.Failed;
            }
            if (m_spawner == null)
            {
                m_presentFailed++;
                reason = "no-spawner";
                return PistonPresentOutcome.Failed;
            }
            if (!m_spawner.TryGetLocalPlayer(m_project, out PlayerData local, out reason))
            {
                m_skippedNoPlayer++;
                return PistonPresentOutcome.SkippedNoPlayer;
            }
            if (local.ComponentPlayer?.ComponentBody == null)
            {
                m_skippedNoPlayer++;
                reason = "no-local-body";
                return PistonPresentOutcome.SkippedNoPlayer;
            }
            SubsystemSky sky = m_project.FindSubsystem<SubsystemSky>(false);
            if (sky == null)
            {
                m_presentFailed++;
                reason = "no-sky";
                return PistonPresentOutcome.Failed;
            }
            Vector3 observer = local.ComponentPlayer.ComponentBody.Position;
            if (!PistonPresentationPolicy.IsWithinClientView(in move, observer,
                    sky.VisibilityRange, out float distance))
            {
                m_skippedDistance++;
                reason = "distance-" + distance.ToString("F1");
                return PistonPresentOutcome.SkippedDistance;
            }
            TerrainChunk chunk = m_terrain.Terrain.GetChunkAtCell(move.X, move.Z);
            if (chunk == null || chunk.State <= TerrainChunkState.InvalidContents4)
            {
                m_skippedChunk++;
                reason = "chunk-not-ready";
                return PistonPresentOutcome.SkippedChunk;
            }
            int cellValue = m_terrain.Terrain.GetCellValue(move.X, move.Y, move.Z);
            int contents = Game.Terrain.ExtractContents(cellValue);
            bool cellOk;
            if (move.Kind == MovingBlockKind.Collapsing)
            {
                // 引擎在 AddMovingBlockSet 之后**立刻** ChangeCell 清掉源格；主机的 CellChange 可能
                // 还没到客户端，所以两种状态都算正常：空（清源格已到）或 沙子 / 沙砾（还没到）。
                // 其它 contents = 世界不一致 → 不表现（明确计数，不猜）。
                cellOk = contents == 0 || PistonMovePolicy.IsCollapsibleContents(contents);
            }
            else
            {
                cellOk = contents == PistonBlock.Index;
            }
            if (!cellOk)
            {
                m_skippedCell++;
                reason = "cell-" + contents;
                return PistonPresentOutcome.SkippedCellMismatch;
            }
            if (!m_local.ContainsKey(move.Key) &&
                m_local.Count >= PistonMovePolicy.MaxConcurrentLocalMoves)
            {
                m_skippedCap++;
                reason = "cap-" + PistonMovePolicy.MaxConcurrentLocalMoves;
                return PistonPresentOutcome.SkippedCap;
            }
            // 下载世界里可能残留同一运动对象的引擎中途集合（存档里的 MovingBlockSets）：客户端不跑
            // 活塞 / 塌落逻辑，这条残留没有任何人负责移除 —— 用主机这条事件把它替换掉（纯表现，不写格）。
            RemoveStrayEngineSet(move.Kind, move.Cell);
            bool replaced = false;
            if (m_local.TryGetValue(move.Key, out LocalMove existing))
            {
                // 同一活塞上一段动画还没结束：先移除旧的（计数 aborted），再建新的。
                RemoveLocal(existing, "replaced", notifyAbort: true);
                replaced = true;
            }
            var blocks = new List<MovingBlock>(move.Blocks.Length);
            for (int i = 0; i < move.Blocks.Length; i++)
            {
                PistonBlockData block = move.Blocks[i];
                blocks.Add(new MovingBlock { Offset = block.Offset, Value = block.Value });
            }
            IMovingBlockSet set;
            try
            {
                set = m_movingBlocks.AddMovingBlockSet(move.Start, move.Target, move.Speed,
                    move.Acceleration, move.Drag, move.Smoothness, blocks,
                    PistonReplicaPresentation.ReplicaIdFor(move.Kind), move.Cell,
                    testCollision: false);
            }
            catch (Exception e)
            {
                m_addFailed++;
                reason = "add-exception:" + e.GetType().Name;
                return PistonPresentOutcome.Failed;
            }
            if (set == null)
            {
                m_addFailed++;
                reason = "add-null";
                return PistonPresentOutcome.Failed;
            }
            long nowMs = NowMs();
            m_local[move.Key] = new LocalMove
            {
                Set = set,
                Move = move,
                StartEventId = startEventId,
                Arrived = false,
                ArrivedMs = 0L,
                DeadlineMs = nowMs + PistonPresentationPolicy.WatchdogMs(in move)
            };
            m_created++;
            if (move.Kind == MovingBlockKind.Collapsing) m_createdCollapsing++;
            if (m_lastSeenStart.Count > 4096)
            {
                // 正常不会到这里（每个键只保留最近一次开始）；上限只防"结束永远不来"的异常积累。
                m_lastSeenStart.Clear();
                m_authoritativelyRemoved.Clear();
                LogProblem("seen-start-cap", "本地开始 event_id 记录表超过 4096 条（结束消息长期缺失？）" +
                    "→ 已清空（后续结束按 end-before-start 计数，本地动画由看门狗移除）");
            }
            m_lastSeenStart[move.Key] = startEventId;
            if (replaced)
            {
                m_replaced++;
                reason = null;
                return PistonPresentOutcome.Replaced;
            }
            reason = null;
            return PistonPresentOutcome.Presented;
        }

        /// <summary>
        /// 本端此刻是不是联机客户端副本：直接读引擎外壳的唯一事实（SuSubsystemTerrain.ClientReplica），
        /// 不依赖上一帧 Pump 缓存的标志 —— 消息分发（地形模块 tick）可能早于本模块 tick 更新缓存。
        /// </summary>
        private bool IsLiveClientReplica()
        {
            SuSubsystemTerrain shell = m_project?.FindSubsystem<SubsystemTerrain>(false)
                as SuSubsystemTerrain;
            bool replica = shell != null && shell.ClientReplica;
            if (replica) m_clientReplica = true;
            return replica;
        }

        /// <summary>
        /// 客户端：移除同一运动对象上残留的引擎集合（来自下载世界存档）：
        ///   · PISTON     —— Id=="Piston" 且 Tag == 活塞格；
        ///   · COLLAPSING —— Id=="CollapsingBlock" 且 **StartPosition 所在格** == 起点格
        ///                    （引擎给塌落集合的 Tag 是 null，只能按起点位置匹配；
        ///                     StartPosition 读不到时明确计数，绝不猜）。
        /// 只调用 RemoveMovingBlockSet —— 绝不调用引擎 StopPiston / 塌落落地回调
        /// （它们会 DestroyCell / ChangeCell，客户端禁止写格）。
        /// </summary>
        private void RemoveStrayEngineSet(MovingBlockKind kind, Point3 cell)
        {
            SubsystemMovingBlocks moving = m_movingBlocks;
            if (moving == null) return;
            m_untracked.Clear();
            for (int i = 0; i < moving.MovingBlockSets.Count; i++)
            {
                IMovingBlockSet set = moving.MovingBlockSets[i];
                if (set == null) continue;
                bool match;
                if (kind == MovingBlockKind.Collapsing)
                {
                    if (!string.Equals(set.Id, PistonReplicaPresentation.EngineCollapsingId,
                            StringComparison.Ordinal)) continue;
                    if (!m_motion.TryReadStartPosition(set, out Vector3 start,
                            out string readReason))
                    {
                        m_startReadFailures++;
                        LogProblem("stray-start:" + readReason,
                            "读不到塌落集合的 StartPosition（" + readReason +
                            "）→ 无法判断它是否就是这条运动的残留引擎集合" +
                            "（累计 " + m_startReadFailures + "；本地表现集合照建，最终格仍取 CellChange）");
                        continue;
                    }
                    match = Game.Terrain.ToCell(start.X) == cell.X &&
                            Game.Terrain.ToCell(start.Y) == cell.Y &&
                            Game.Terrain.ToCell(start.Z) == cell.Z;
                }
                else
                {
                    if (!string.Equals(set.Id, PistonReplicaPresentation.EnginePistonId,
                            StringComparison.Ordinal)) continue;
                    match = set.Tag is Point3 tag && tag == cell;
                }
                if (!match) continue;
                m_untracked.Add(set);
            }
            for (int i = 0; i < m_untracked.Count; i++)
            {
                moving.RemoveMovingBlockSet(m_untracked[i]);
                m_straySuperseded++;
                LogProblem("stray-superseded", "移除了同一运动对象上残留的引擎中途集合（" +
                    m_untracked[i].Id + "，下载世界存档），改用主机事件建本地表现集合；累计 " +
                    m_straySuperseded);
            }
            m_untracked.Clear();
        }

        public bool Cancel(in PistonMoveEndData end, out bool found, out string reason)
        {
            found = false;
            if (!IsLiveClientReplica())
            {
                reason = "not-client-replica";
                return false;
            }
            if (m_movingBlocks == null)
            {
                reason = "no-world";
                return false;
            }
            bool hasLive = m_local.TryGetValue(end.Key, out LocalMove existing);
            bool hasSeenStart = m_lastSeenStart.TryGetValue(end.Key, out uint lastSeenStart);
            PistonEndDisposition disposition = PistonLocalSetOrdering.Classify(hasLive,
                hasLive ? existing.StartEventId : 0u, hasSeenStart, lastSeenStart,
                end.StartEventId);
            switch (disposition)
            {
                case PistonEndDisposition.StaleEnd:
                    // 该格已经有更新的一轮动画：旧结束消息不能删掉新动画（明确计数，不误删）。
                    reason = "event-mismatch-" + end.StartEventId + "-" + existing.StartEventId;
                    return true;
                case PistonEndDisposition.EndBeforeStart:
                    m_endBeforeStart++;
                    reason = "end-before-start-" + end.StartEventId;
                    return true;
                case PistonEndDisposition.NoLocalSet:
                    if (m_authoritativelyRemoved.Remove(end.Key))
                    {
                        // 本地集合已被主机权威格变化提前移除：这条结束仍然对应一次真实运动
                        // → 按正常结束计数（不是 endUnknown，也不重复移除）。
                        found = true;
                        CountFinished(end);
                        reason = null;
                        return true;
                    }
                    // 正常：当时因距离 / 区块 / 上限跳过了这条运动（或已被看门狗放弃）。
                    reason = null;
                    return true;
                case PistonEndDisposition.Apply:
                    break;
                default:
                    reason = "disposition-" + disposition;
                    return true;
            }
            found = true;
            RemoveLocal(existing, end.Completed ? "finished" : "host-early", notifyAbort: false);
            m_authoritativelyRemoved.Remove(end.Key);
            CountFinished(end);
            reason = null;
            return true;
        }

        /// <summary>
        /// 结束消息的计数口径（客户端）：
        ///   · 塌落：completed=false（撞支撑落地，reason=landed）是引擎的**正常**结束；
        ///     completed=true（整列没撞到任何方块掉到目标点）单独写一条可审计日志。
        ///   · 活塞：completed=true = 正常到点；false = 提前结束（碰撞 / 被拆 / 区块卸载）。
        /// </summary>
        private void CountFinished(in PistonMoveEndData end)
        {
            if (end.Kind == MovingBlockKind.Collapsing)
            {
                m_removedFinished++;
                if (end.Completed)
                {
                    LogProblem("collapsing-target:" + (end.Reason ?? ""),
                        "主机报告塌落列掉到目标点（没有撞到任何方块；" + end.Describe() +
                        "）→ 客户端已移除本地表现集合，最终格以主机 CellChange 为准");
                }
                return;
            }
            if (end.Completed)
            {
                m_removedFinished++;
                return;
            }
            m_removedAborted++;
            LogProblem("host-early:" + (end.Reason ?? ""), "主机报告活塞运动提前结束（" +
                end.Describe() + "）→ 客户端已移除本地动画，最终格以主机 CellChange 为准");
        }

        /// <summary>
        /// 客户端：主机权威格（CellChange / 整块下发）在本地落地后逐格通知。
        /// 判定口径见 Net/PistonCellChange.cs（PistonLocalSetPolicy）：运动**开始**时引擎清源格 /
        /// 写活塞 extended 位属于正常开始状态，不能取消本地动画；运动**结束**时写回的权威格
        /// 必须立刻接受 —— 移除本地表现集合，避免它继续插值穿过被推方块 / 落到已落地的沙子上。
        /// 只移除 SubsystemMovingBlocks 里的表现对象，绝不写格（值已经是主机的值）。
        /// </summary>
        public void OnAuthoritativeCellsApplied(ChunkKey key, IReadOnlyList<CellEdit> edits)
        {
            if (!m_clientReplica || m_local.Count == 0) return;
            if (edits == null || edits.Count == 0) return;
            if (m_movingBlocks == null) return;
            int baseX = key.X * 16;
            int baseZ = key.Z * 16;
            m_removals.Clear();
            foreach (KeyValuePair<MovingBlockKey, LocalMove> pair in m_local)
            {
                LocalMove move = pair.Value;
                for (int i = 0; i < edits.Count; i++)
                {
                    CellEdit edit = edits[i];
                    if (edit.Y < 0 || edit.Y > 255) continue;
                    var cell = new Point3(baseX + edit.X, edit.Y, baseZ + edit.Z);
                    PistonCellChangeOutcome outcome = PistonLocalSetPolicy.Classify(in move.Move,
                        move.Set.Position, cell, edit.Contents, out string reason);
                    if (outcome != PistonCellChangeOutcome.Cancel &&
                        outcome != PistonCellChangeOutcome.CancelPenetration) continue;
                    if (outcome == PistonCellChangeOutcome.CancelPenetration)
                        m_authoritativePenetrations++;
                    else
                        m_authoritativeCancelled++;
                    LogProblem("authoritative:" + reason,
                        "主机权威格 (" + cell.X + "," + cell.Y + "," + cell.Z + ") 落在本地" +
                        (move.Move.Kind == MovingBlockKind.Collapsing ? "塌落" : "活塞") +
                        "表现集合的影响区内（" + reason + "，" + move.Move.Describe() +
                        "）→ 立刻移除本地集合接受主机值（累计 cancelled=" +
                        m_authoritativeCancelled + " penetration=" +
                        m_authoritativePenetrations + "）");
                    m_removals.Add(pair.Key);
                    break;
                }
            }
            for (int i = 0; i < m_removals.Count; i++)
            {
                if (!m_local.TryGetValue(m_removals[i], out LocalMove move)) continue;
                m_authoritativelyRemoved.Add(m_removals[i]);
                RemoveLocal(move, "authoritative-cell", notifyAbort: false);
            }
            m_removals.Clear();
        }

        public int AbortAll(string reason, out int pistonAborted, out int collapsingAborted)
        {
            pistonAborted = 0;
            collapsingAborted = 0;
            if (m_local.Count == 0) return 0;
            m_removals.Clear();
            foreach (MovingBlockKey key in m_local.Keys) m_removals.Add(key);
            int removed = 0;
            int piston = 0;
            int collapsing = 0;
            for (int i = 0; i < m_removals.Count; i++)
            {
                if (!m_local.TryGetValue(m_removals[i], out LocalMove move)) continue;
                if (move.Move.Kind == MovingBlockKind.Collapsing) collapsing++;
                else piston++;
                RemoveLocal(move, reason, notifyAbort: false);
                removed++;
            }
            m_removals.Clear();
            m_removedAborted += removed;
            pistonAborted = piston;
            collapsingAborted = collapsing;
            if (removed > 0)
                LogProblem("abort-all:" + reason, "放弃 " + removed + " 条本地运动动画（原因 " +
                    reason + "，活塞 " + piston + " / 塌落 " + collapsing +
                    "）→ 最终状态等主机 CellChange 覆盖（累计 aborted=" + m_removedAborted + "）");
            return removed;
        }

        /// <summary>
        /// 移除一条本地集合（只动 SubsystemMovingBlocks，绝不写格）。
        /// 计数口径：<paramref name="notifyAbort"/> = true 时计入 aborted 并抛
        /// <see cref="ClientMoveAborted"/>（引擎侧主动放弃：被新一轮替换 / 看门狗）；
        /// 由主机结束消息驱动的移除由调用方按 completed 计 finished / aborted。
        /// </summary>
        private void RemoveLocal(LocalMove move, string reason, bool notifyAbort)
        {
            if (move == null) return;
            if (m_movingBlocks != null)
            {
                try { m_movingBlocks.RemoveMovingBlockSet(move.Set); }
                catch (Exception e)
                {
                    LogProblem("remove-exception:" + e.GetType().Name,
                        "移除本地运动集合异常（" + move.Move.Describe() + "）：" +
                        e.GetType().Name + " " + e.Message + " → 该对象会留在 SubsystemMovingBlocks 里" +
                        "（明确计数，不静默）");
                }
            }
            m_local.Remove(move.Move.Key);
            if (!notifyAbort) return;
            m_removedAborted++;
            try
            {
                ClientMoveAborted?.Invoke(move.Move.Kind, move.Move.Cell, reason);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 引擎把集合停下（活塞：到达 TargetPosition；塌落：撞到支撑方块落地 / 到达目标点；
        /// 每帧都会抛，直到有人把它移除）：只标记一次，之后按 EndGraceMs 等主机的结束消息。
        /// </summary>
        private void OnMovingBlockSetStopped(IMovingBlockSet movingBlockSet)
        {
            if (!m_clientReplica || movingBlockSet == null) return;
            if (!(movingBlockSet.Tag is Point3 cell)) return;
            LocalMove move;
            if (!m_local.TryGetValue(new MovingBlockKey(MovingBlockKind.Piston, cell), out move) &&
                !m_local.TryGetValue(new MovingBlockKey(MovingBlockKind.Collapsing, cell), out move))
                return;
            if (!ReferenceEquals(move.Set, movingBlockSet)) return;
            m_stoppedEvents++;
            if (move.Arrived) return;
            move.Arrived = true;
            move.ArrivedMs = NowMs();
            LogLocal("本地" + (move.Move.Kind == MovingBlockKind.Collapsing
                    ? "塌落动画已落地" : "活塞动画已到目标位置") + "（" + move.Move.Describe() +
                "）→ 保持在原地等主机的 PistonMoveEnd（不在本地写格）");
        }

        /// <summary>结束消息迟迟不到 / 运动所在区块被卸载时明确放弃（计数 + ClientMoveAborted 事件）。</summary>
        private void UpdateClientWatchdog()
        {
            if (m_local.Count == 0) return;
            m_removals.Clear();
            long nowMs = NowMs();
            foreach (KeyValuePair<MovingBlockKey, LocalMove> pair in m_local)
            {
                LocalMove move = pair.Value;
                TerrainChunk chunk = m_terrain?.Terrain == null
                    ? null
                    : m_terrain.Terrain.GetChunkAtCell(move.Move.X, move.Move.Z);
                bool chunkReady = chunk != null && chunk.State > TerrainChunkState.InvalidContents4;
                if (!chunkReady)
                {
                    m_removals.Add(pair.Key);
                    continue;
                }
                long limit = move.Arrived ? move.ArrivedMs + PistonMovePolicy.EndGraceMs
                    : move.DeadlineMs;
                if (nowMs < limit) continue;
                m_removals.Add(pair.Key);
            }
            for (int i = 0; i < m_removals.Count; i++)
            {
                if (!m_local.TryGetValue(m_removals[i], out LocalMove move)) continue;
                m_watchdog++;
                RemoveLocal(move, move.Arrived ? "end-timeout" : "duration-timeout", notifyAbort: true);
            }
            m_removals.Clear();
        }

        // ------------------------------------------------------------ 玩家端视图

        /// <summary>
        /// 主机：该玩家端的位置（主机侧联机角色的 ComponentBody.Position）与兴趣半径。
        /// 两者都可能暂时读不到（角色刚建 / 还没上报窗口）—— 返回 false 并写原因，由模块计数。
        /// </summary>
        public bool TryGetPeerView(int clientId, out PistonPeerView view)
        {
            bool hasPosition = false;
            Vector3 position = Vector3.Zero;
            string reason = null;
            if (m_spawner != null)
                hasPosition = m_spawner.TryGetAvatarCell(clientId, out position, out reason);
            else
                reason = "没有化身表（组合根未注入 NetworkAvatarSpawner）";
            bool hasWindow = false;
            int radius = 0;
            if (m_rangeResolver != null)
                hasWindow = m_rangeResolver(clientId, out _, out _, out radius);
            view = new PistonPeerView(hasPosition, position, hasWindow, radius, reason);
            return hasPosition || hasWindow;
        }

        // ------------------------------------------------------------ 诊断

        public string Describe() => "movingBlockReplica(kind=piston+collapsing) host started=" +
            m_hostStartedMove + " (collapsing=" + m_hostStartedCollapsing + ")" +
            " ended=" + m_hostEndedMove + " (collapsing=" + m_hostEndedCollapsing + ")" +
            " rejected=" + m_hostRejected + " noListener=" + m_hostNoListener +
            " · client local created=" + m_created + " (collapsing=" + m_createdCollapsing + ")" +
            " replaced=" + m_replaced +
            " finished=" + m_removedFinished + " aborted=" + m_removedAborted +
            " skippedDistance=" + m_skippedDistance + " skippedCap=" + m_skippedCap +
            " skippedChunk=" + m_skippedChunk + " skippedCell=" + m_skippedCell +
            " skippedNoPlayer=" + m_skippedNoPlayer + " presentFailed=" + m_presentFailed +
            " addFailed=" + m_addFailed + " startReadFailures=" + m_startReadFailures +
            " authoritativeCancelled=" + m_authoritativeCancelled +
            " authoritativePenetrations=" + m_authoritativePenetrations +
            " endBeforeStart=" + m_endBeforeStart +
            " watchdog=" + m_watchdog + " stoppedEvents=" + m_stoppedEvents +
            " untrackedRemoved=" + m_untrackedRemoved +
            " straySuperseded=" + m_straySuperseded +
            " projectResets=" + m_projectResets + " alive=" + m_local.Count;

        private long NowMs() => (long)(Time.RealTime * 1000.0);

        private void Log(string message) => m_diagnostics?.Info(m_ownerId, message);

        /// <summary>正常但值得看见的事实（到点等待 / 结束消息）按 10s 一条限流。</summary>
        private void LogLocal(string message)
        {
            long nowMs = NowMs();
            if (m_lastLocalMs != 0 && nowMs - m_lastLocalMs < LocalMoveLogMs) return;
            m_lastLocalMs = nowMs;
            m_diagnostics?.Info(m_ownerId, message);
        }

        private void LogOnce(string key, string message)
        {
            if (!m_loggedOnce.Add(key)) return;
            m_diagnostics?.Warn(m_ownerId, message);
        }

        private void LogProblem(string key, string message)
        {
            long nowMs = NowMs();
            if (m_lastProblemMs.TryGetValue(key ?? string.Empty, out long last) &&
                nowMs - last < ProblemLogMs) return;
            m_lastProblemMs[key ?? string.Empty] = nowMs;
            m_diagnostics?.Warn(m_ownerId, message);
        }
    }
}
