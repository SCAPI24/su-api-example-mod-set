using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Net;
using SuMultiplayer.Terrain;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端**熔炉火焰表现**（只维护 FireParticleSystem；不烧炼、不消耗燃料、不写格、不生成实体）。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/SubsystemFurnaceBlockBehavior.cs）：
    ///   · <c>AddFire</c>（:102-109）只在 <c>Terrain.ExtractContents(value) == 65</c> 时调用，
    ///     参数固定 <c>FireParticleSystem(cell + (0.5,0.2,0.5), 0.15f, 16f)</c>；
    ///   · <c>RemoveFire</c>（:111-116）只 <c>SubsystemParticles.RemoveParticleSystem</c>；
    ///   · <c>OnChunkDiscarding</c>（:80-94）把该区块内的火焰全部收掉。
    /// 客户端的方块行为被替换成只表现外壳（SuSubsystemFurnaceBlockBehavior，不进 base：
    /// base 会 AddEntity / DropAllItems / RemoveEntity），所以火焰改由这里维护。
    ///
    /// 数据来源（两条都是主机已落地的权威状态）：
    ///   · <c>ContainerReplicaEngine.ApplyFurnacePresentation</c> 收到 FURNACE_STATE 后调用
    ///     <see cref="Present(FurnaceStateData, out string)"/>：heat/fireTime 是"是否在烧"的权威值；
    ///   · <c>SuSubsystemFurnaceBlockBehavior</c> 的 OnBlockGenerated / OnBlockAdded / OnBlockModified /
    ///     OnBlockRemoved 调用 <see cref="PresentCell"/> / <see cref="Forget"/>（区块生成，
    ///     以及 TerrainBridge.ApplyCells 落地主机 CellChange 后由 ClientBlockBehaviorNotifier 补发的通知）；
    ///   · 1 秒一次的有界越界扫除（counter: furnaceFlameStaleSweep）：万一通知漏了一拍，
    ///     把"格子已经不是燃烧熔炉"的粒子收掉。
    ///
    /// 关键不变式：
    ///   · 同一格最多一个粒子（重复状态 = Keep，数据变化只 Keep —— 引擎的熔炉火焰与 data 无关）；
    ///   · 熄灭 / 方块被移除 / 区块丢弃 → 立即收掉，之后不再重建；
    ///   · 建粒子前必须确认该区块已加载且格子此刻确实是 65（否则明确计数 + 限流日志，
    ///     绝不把火焰建在空气 / 未加载区块上）；
    ///   · 越界扫除只遍历**本类自己的** m_particles，从不触碰别的粒子系统。
    ///
    /// 线程：只由主线程调用（ApplyCells / 方块行为回调 / 模块消息都在引擎线程）。
    /// 无静态状态：每个 mod 会话一个实例，由组合根挂在 <c>TerrainBridge.FurnacePresenter</c>。
    /// </summary>
    public sealed class FurnaceReplicaPresentation
    {
        private const double SweepIntervalSeconds = 1.0;
        private const double SummaryLogSeconds = 10.0;
        private const long ProblemLogMs = 10000;

        private readonly FurnaceFlameState m_state = new FurnaceFlameState();
        private readonly Dictionary<Point3, FireParticleSystem> m_particles =
            new Dictionary<Point3, FireParticleSystem>();
        private readonly List<Point3> m_forget = new List<Point3>(8);
        private readonly List<Point3> m_stale = new List<Point3>(16);

        private Project m_project;
        private SubsystemParticles m_particlesSubsystem;
        private SubsystemTerrain m_terrain;
        private double m_nextSweepTime;
        private double m_nextLogTime;
        private long m_lastLoggedAdded;
        private long m_lastLoggedRemoved;
        private long m_lastLoggedStale;
        private long m_lastLoggedSkippedChunk;
        private long m_lastProblemLogMs;
        private bool m_missingTerrainLogged;
        private bool m_missingParticlesLogged;

        private long m_skippedChunk;
        private long m_deniedWrites;
        private long m_cellMismatch;
        private long m_invalidCell;
        private long m_missingParticles;
        private long m_missingTerrain;
        private long m_rejectedStates;
        private long m_particleFaults;
        private long m_chunkForgets;
        private long m_resets;

        // ---------------------------------------------------------------- 进入点

        /// <summary>
        /// 主机权威炉况落地后调用（FURNACE_STATE / 打开面板时的缓存重放）。返回 false 表示
        /// 这次炉况不能用于火焰表现（原因写进 <paramref name="reason"/>，调用方计数 + 记录）。
        /// 只做表现：不写格、不生成实体。
        /// </summary>
        public bool Present(FurnaceStateData state, out string reason)
        {
            reason = null;
            EnsureWorld();
            if (state == null)
            {
                reason = "state-null";
                return false;
            }
            if (!IsClientReplica())
            {
                // 主机 / 单机绝不允许进入客户端表现：引擎自己维护火焰，再建一份 = 双份粒子。
                m_deniedWrites++;
                LogProblem("熔炉火焰表现被非客户端调用（" + state.Describe() +
                    "）→ 拒绝（furnaceFlameDeniedWrites=" + m_deniedWrites +
                    "；主机火焰由引擎维护）");
                reason = "not-client";
                return false;
            }
            if (!FurnaceFlamePolicy.IsFurnaceContents(state.Contents))
            {
                m_rejectedStates++;
                LogProblem("FurnaceState 的格子不是熔炉（contents=" + state.Contents + " @ " +
                    state.X + "," + state.Y + "," + state.Z + "）→ 不建火焰（累计 " +
                    m_rejectedStates + " 次）");
                reason = "not-furnace-contents:" + state.Contents;
                return false;
            }
            bool burning = FurnaceFlamePolicy.IsBurning(state.Contents, state.HeatLevel,
                state.FireTimeRemaining, hasAuthoritativeState: true);
            ApplyAndExecute(burning, state.X, state.Y, state.Z);
            return true;
        }

        /// <summary>
        /// 一格的主机权威格子值落地后调用（区块生成 / CellChange 行为通知）。
        /// 只有 contents 65 才建火焰（与引擎 AddFire 的调用点完全一致）；
        /// 其它内容（含空气）会把该格已有的火焰收掉。
        /// </summary>
        public FurnaceFlameAction PresentCell(int value, int x, int y, int z)
        {
            EnsureWorld();
            if (!IsClientReplica())
            {
                m_deniedWrites++;
                LogProblem("熔炉火焰表现被非客户端调用（cell " + x + "," + y + "," + z +
                    "）→ 拒绝（furnaceFlameDeniedWrites=" + m_deniedWrites + "）");
                return FurnaceFlameAction.None;
            }
            int contents = Game.Terrain.ExtractContents(value);
            bool burning = FurnaceFlamePolicy.IsBurning(contents, 0f, 0f,
                hasAuthoritativeState: false);
            return ApplyAndExecute(burning, x, y, z);
        }

        /// <summary>这一格明确移除（OnBlockRemoved）：收掉粒子，不再跟踪。</summary>
        public void Forget(int x, int y, int z)
        {
            EnsureWorld();
            var cell = new Point3(x, y, z);
            bool tracked = m_state.Forget(x, y, z);
            if (!m_particles.TryGetValue(cell, out FireParticleSystem system)) return;
            m_particles.Remove(cell);
            Stop(system);
            if (!tracked) m_cellMismatch++;   // 槽位与跟踪表不一致：暴露出来，不静默
        }

        /// <summary>区块丢弃：该块里的熔炉火焰一并收掉（不会随区块重新加载而复用旧对象）。</summary>
        public void ForgetChunk(TerrainChunk chunk)
        {
            if (chunk == null || m_particles.Count == 0) return;
            EnsureWorld();
            int originX = chunk.Origin.X;
            int originZ = chunk.Origin.Y;
            m_forget.Clear();
            foreach (Point3 cell in m_particles.Keys)
            {
                if (cell.X < originX || cell.X >= originX + 16) continue;
                if (cell.Z < originZ || cell.Z >= originZ + 16) continue;
                m_forget.Add(cell);
            }
            for (int i = 0; i < m_forget.Count; i++)
            {
                Point3 cell = m_forget[i];
                m_state.Forget(cell.X, cell.Y, cell.Z);
                if (!m_particles.TryGetValue(cell, out FireParticleSystem system)) continue;
                m_particles.Remove(cell);
                Stop(system);
            }
            if (m_forget.Count > 0) m_chunkForgets += m_forget.Count;
            m_forget.Clear();
        }

        /// <summary>
        /// 客户端每帧：1 秒一次有界越界扫除（counter: furnaceFlameStaleSweep）。只检查本类
        /// 跟踪的粒子；区块未加载的格跳过（不当成"不是熔炉"误删）。
        /// </summary>
        public void Tick(float dt)
        {
            EnsureWorld();
            if (!IsClientReplica() || m_particles.Count == 0) return;
            double now = Time.RealTime;
            if (now < m_nextSweepTime) return;
            m_nextSweepTime = now + SweepIntervalSeconds;
            m_stale.Clear();
            m_state.Sweep(ReadCellValue, m_stale, FurnaceFlamePolicy.MaxSweepChecksPerRun);
            for (int i = 0; i < m_stale.Count; i++)
            {
                Point3 cell = m_stale[i];
                if (!m_particles.TryGetValue(cell, out FireParticleSystem system)) continue;
                m_particles.Remove(cell);
                Stop(system);
            }
            if (m_stale.Count > 0)
            {
                LogProblem("熔炉火焰越界扫除：收掉 " + m_stale.Count + " 个格子的陈旧粒子（" +
                    FurnaceFlamePolicy.StaleSweepCounter + " 累计 " + m_state.StaleSwept +
                    "；有界检查 " + FurnaceFlamePolicy.MaxSweepChecksPerRun + " 格/秒）");
            }
            MaybeLog(now);
        }

        // ---------------------------------------------------------------- 表现执行

        private FurnaceFlameAction ApplyAndExecute(bool burning, int x, int y, int z)
        {
            var cell = new Point3(x, y, z);
            if (burning && !m_state.IsTracked(cell))
            {
                // 建粒子前先确认"这一格此刻真的能显示火焰"。失败 → 计数 + 限流日志并**不跟踪**，
                // 等下一次权威状态 / 格子变化再重试（不是静默丢弃）。
                if (!CanCreateFlame(cell, out string reason)) return FurnaceFlameAction.None;
            }
            FurnaceFlameAction action = m_state.Apply(burning, x, y, z);
            switch (action)
            {
                case FurnaceFlameAction.Add:
                    AddParticle(cell);
                    break;
                case FurnaceFlameAction.Remove:
                    if (m_particles.TryGetValue(cell, out FireParticleSystem removed))
                    {
                        m_particles.Remove(cell);
                        Stop(removed);
                    }
                    break;
                case FurnaceFlameAction.Keep:
                case FurnaceFlameAction.None:
                    break;
                case FurnaceFlameAction.Rejected:
                    LogProblem("客户端熔炉火焰数达到上限 " +
                        FurnaceFlamePolicy.MaxTrackedParticles + "（capHits=" +
                        m_state.CapHits + "）→ 本格不建粒子");
                    break;
            }
            return action;
        }

        /// <summary>
        /// 建粒子前的显式前置条件。返回 false 时已经计数；调用方不再跟踪该格。
        /// 区块未加载 → furnaceFlameSkippedChunk；格子已不是 65 → cellMismatch。
        /// </summary>
        private bool CanCreateFlame(Point3 cell, out string reason)
        {
            reason = null;
            if (cell.Y < 0 || cell.Y > 255)
            {
                m_invalidCell++;
                reason = "cell-y:" + cell.Y;
                LogProblem("熔炉火焰格坐标非法（" + cell.X + "," + cell.Y + "," + cell.Z +
                    "）→ 不建粒子（累计 " + m_invalidCell + "）");
                return false;
            }
            SubsystemTerrain terrain = EnsureTerrain();
            if (terrain?.Terrain == null)
            {
                m_missingTerrain++;
                reason = "no-terrain";
                LogProblemOnce(ref m_missingTerrainLogged,
                    "熔炉火焰表现：当前没有 SubsystemTerrain → 本帧不建粒子（累计 " +
                    m_missingTerrain + "；等世界加载后由格子 / 炉况重试）");
                return false;
            }
            TerrainChunk chunk = terrain.Terrain.GetChunkAtCell(cell.X, cell.Z);
            if (chunk == null || !ChunkContentStages.IsContentValid((int)chunk.State))
            {
                m_skippedChunk++;
                reason = "chunk-not-loaded";
                LogProblem("熔炉火焰跳过未加载区块（" + cell.X + "," + cell.Y + "," + cell.Z +
                    "）→ 不建粒子（furnaceFlameSkippedChunk=" + m_skippedChunk +
                    "；区块加载后由格子 / 炉况重试）");
                return false;
            }
            int actual = Game.Terrain.ExtractContents(
                terrain.Terrain.GetCellValueFast(cell.X, cell.Y, cell.Z));
            if (actual != FurnaceStateData.LitContents)
            {
                m_cellMismatch++;
                reason = "cell-not-lit:" + actual;
                LogProblem("熔炉火焰的目标格此刻不是燃烧熔炉（" + cell.X + "," + cell.Y + "," +
                    cell.Z + " contents=" + actual + "，期望 65）→ 不建粒子（cellMismatch=" +
                    m_cellMismatch + "）");
                return false;
            }
            return true;
        }

        private void AddParticle(Point3 cell)
        {
            SubsystemParticles particles = EnsureParticlesSubsystem();
            if (particles == null)
            {
                m_missingParticles++;
                LogProblemOnce(ref m_missingParticlesLogged,
                    "熔炉火焰表现：找不到 SubsystemParticles → 客户端火焰不可用（主机格子仍同步；累计 " +
                    m_missingParticles + "）");
                m_state.Untrack(cell);
                return;
            }
            try
            {
                // 与引擎 AddFire 完全相同的参数（位置 / 尺寸 / 可见距离）。
                var system = new FireParticleSystem(
                    new Vector3(cell.X, cell.Y, cell.Z) + FurnaceFlamePolicy.ParticleOffset,
                    FurnaceFlamePolicy.ParticleSize,
                    FurnaceFlamePolicy.MaxVisibilityDistance);
                particles.AddParticleSystem(system);
                m_particles[cell] = system;
            }
            catch (Exception e)
            {
                // 粒子创建失败：撤销跟踪（下一拍可重试），明确计数 + 限流日志，不假装已渲染。
                m_state.Untrack(cell);
                m_particleFaults++;
                LogProblem("熔炉火焰粒子创建失败（" + cell.X + "," + cell.Y + "," + cell.Z +
                    "）：" + e.GetType().Name + " " + e.Message + "（累计 " +
                    m_particleFaults + " 次）");
            }
        }

        /// <summary>与引擎一致：只 IsStopped，让引擎在剩余粒子寿命结束后自动摘除（自然收尾）。</summary>
        private static void Stop(FireParticleSystem system)
        {
            if (system == null) return;
            system.IsStopped = true;
        }

        // ---------------------------------------------------------------- 世界 / 引擎对象

        private bool IsClientReplica()
        {
            Project project = GameManager.Project;
            if (project == null) return false;
            return project.FindSubsystem<SubsystemTerrain>(false) is SuSubsystemTerrain shell &&
                   shell.ClientReplica;
        }

        private void EnsureWorld()
        {
            Project project = GameManager.Project;
            if (ReferenceEquals(project, m_project)) return;
            // 换世界 / 重新加入：先收掉旧粒子，再清账本（保留累计计数）。
            StopAllParticles();
            m_project = project;
            m_particlesSubsystem = null;
            m_terrain = null;
            m_state.ClearTracked();
            m_missingTerrainLogged = false;
            m_missingParticlesLogged = false;
            m_forget.Clear();
            m_stale.Clear();
            m_nextSweepTime = 0.0;
            m_lastLoggedAdded = m_state.Added;
            m_lastLoggedRemoved = m_state.Removed;
            m_lastLoggedStale = m_state.StaleSwept;
            m_lastLoggedSkippedChunk = m_skippedChunk;
            m_resets++;
        }

        private SubsystemParticles EnsureParticlesSubsystem()
        {
            if (m_particlesSubsystem != null) return m_particlesSubsystem;
            m_particlesSubsystem = m_project?.FindSubsystem<SubsystemParticles>(false);
            return m_particlesSubsystem;
        }

        private SubsystemTerrain EnsureTerrain()
        {
            if (m_terrain != null) return m_terrain;
            m_terrain = m_project?.FindSubsystem<SubsystemTerrain>(false);
            return m_terrain;
        }

        /// <summary>读一格（越界 / 区块未加载 → -1，扫除不会误删）。</summary>
        private int ReadCellValue(int x, int y, int z)
        {
            if (y < 0 || y > 255) return -1;
            SubsystemTerrain terrain = EnsureTerrain();
            if (terrain?.Terrain == null) return -1;
            if (terrain.Terrain.GetChunkAtCell(x, z) == null) return -1;
            return terrain.Terrain.GetCellValueFast(x, y, z);
        }

        private void StopAllParticles()
        {
            m_forget.Clear();
            foreach (Point3 cell in m_particles.Keys) m_forget.Add(cell);
            for (int i = 0; i < m_forget.Count; i++)
            {
                if (m_particles.TryGetValue(m_forget[i], out FireParticleSystem system))
                    Stop(system);
            }
            m_forget.Clear();
            m_particles.Clear();
        }

        // ---------------------------------------------------------------- 诊断

        /// <summary>10 秒一行的限流摘要（计数有变化 / 有活动时才写，不用一次性标志）。</summary>
        public void MaybeLog(double now)
        {
            if (now < m_nextLogTime) return;
            bool changed = m_state.Added != m_lastLoggedAdded ||
                           m_state.Removed != m_lastLoggedRemoved ||
                           m_state.StaleSwept != m_lastLoggedStale ||
                           m_skippedChunk != m_lastLoggedSkippedChunk;
            if (!changed) return;
            m_nextLogTime = now + SummaryLogSeconds;
            m_lastLoggedAdded = m_state.Added;
            m_lastLoggedRemoved = m_state.Removed;
            m_lastLoggedStale = m_state.StaleSwept;
            m_lastLoggedSkippedChunk = m_skippedChunk;
            ModLog.Info("[SuMP] " + Describe());
        }

        private void LogProblemOnce(ref bool logged, string message)
        {
            if (logged) return;
            logged = true;
            Log.Warning("[SuMP] " + message);
        }

        private void LogProblem(string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemLogMs != 0 && nowMs - m_lastProblemLogMs < ProblemLogMs) return;
            m_lastProblemLogMs = nowMs;
            Log.Warning("[SuMP] " + message);
        }

        public int ActiveCount => m_particles.Count;
        public long Added => m_state.Added;
        public long Removed => m_state.Removed;
        public long StaleSwept => m_state.StaleSwept;
        public long SkippedChunk => m_skippedChunk;
        public long DeniedWrites => m_deniedWrites;

        public string Describe() =>
            "furnacePresentation " + m_state.Describe() +
            " active=" + m_particles.Count +
            " furnaceFlameSkippedChunk=" + m_skippedChunk +
            " furnaceFlameDeniedWrites=" + m_deniedWrites +
            " cellMismatch=" + m_cellMismatch + " invalidCell=" + m_invalidCell +
            " rejectedStates=" + m_rejectedStates +
            " chunkForgets=" + m_chunkForgets +
            " missingParticles=" + m_missingParticles +
            " missingTerrain=" + m_missingTerrain +
            " particleFaults=" + m_particleFaults + " resets=" + m_resets;

        /// <summary>世界切换 / mod 下线：先收掉本端还活着的粒子，再清账本（保留计数）。</summary>
        public void Reset()
        {
            StopAllParticles();
            m_state.ClearTracked();
            m_project = null;
            m_particlesSubsystem = null;
            m_terrain = null;
            m_missingTerrainLogged = false;
            m_missingParticlesLogged = false;
            m_nextSweepTime = 0.0;
            m_nextLogTime = 0.0;
            m_lastLoggedAdded = m_state.Added;
            m_lastLoggedRemoved = m_state.Removed;
            m_lastLoggedStale = m_state.StaleSwept;
            m_lastLoggedSkippedChunk = m_skippedChunk;
            m_resets++;
        }
    }
}
