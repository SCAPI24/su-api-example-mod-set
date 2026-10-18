using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Net;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端**营火表现**（只维护 FireParticleSystem；不烧燃料、不写格、不生成实体）。
    ///
    /// 数据来源只有两处，都是"主机已经落地的格子状态"：
    ///   · <c>SuSubsystemCampfireBlockBehavior</c> 的 OnBlockGenerated / OnBlockAdded /
    ///     OnBlockModified / OnBlockRemoved：区块从本地存档生成，以及
    ///     <c>TerrainBridge.ApplyCells</c> 落地主机 CellChange / 整块下发后由
    ///     <c>ClientBlockBehaviorNotifier</c> 补发的行为通知（单一漏斗）；
    ///   · 1 秒一次的有界越界扫除：万一通知漏了一拍（host 烧尽与区块边界竞争），
    ///     把"格子已经不是燃烧营火"的粒子收掉（counter: campfireStaleSweep）。
    ///
    /// 关键不变式：
    ///   · 同一格最多一个粒子（燃料 data 变化 = 旧粒子 IsStopped + 新粒子，绝不叠加）；
    ///   · 烧成灰烬（contents 209 / data 0）或被移除 → 立即收掉，之后不再重建；
    ///   · 越界扫除只遍历**本类自己的** m_particles，从不触碰别的粒子系统。
    ///
    /// 线程：只由主线程调用（ApplyCells / 方块行为回调都在引擎线程）。
    /// 无静态状态：每个 mod 会话一个实例，由组合根挂在 <c>TerrainBridge.CampfirePresenter</c>。
    /// </summary>
    public sealed class CampfireReplicaPresentation
    {
        /// <summary>counters 统一口径（状态行 / 日志 / 自检用同一个 token）。</summary>
        private const string StaleSweepCounter = "campfireStaleSweep";

        private readonly Dictionary<Point3, FireParticleSystem> m_particles =
            new Dictionary<Point3, FireParticleSystem>();
        private readonly CampfireReplicaState m_state = new CampfireReplicaState();
        private readonly CampfireExtinguishDetector m_detector = new CampfireExtinguishDetector();
        private readonly List<Point3> m_forget = new List<Point3>(8);
        private readonly List<Point3> m_stale = new List<Point3>(16);

        private Project m_project;
        private SubsystemParticles m_particlesSubsystem;
        private SubsystemTerrain m_terrain;
        private bool m_missingParticlesLogged;
        private long m_missingParticles;
        private double m_nextSweepTime;
        private double m_nextLogTime;
        private long m_lastLoggedAdded;
        private long m_lastLoggedRemoved;
        private long m_lastLoggedStale;
        private long m_lastLoggedHostEvents;
        private long m_lastLoggedChunkForgets;
        private long m_lastProblemLogMs;

        private long m_hostDetected;
        private long m_hostEvents;
        private long m_chunkForgets;
        private long m_particleFaults;
        private long m_resets;

        /// <summary>主机：一格刚刚烧尽（引擎在本端已经播过 Sizzles；由声音同步模块转给其它端）。</summary>
        public event Action<Point3[]> HostExtinguished;

        // ---------------------------------------------------------------- 进入点

        /// <summary>
        /// 一格的主机权威值落地后调用（<paramref name="value"/> = 完整 block value）。
        /// 返回本次执行的表现动作（诊断 / 测试直接断言）。主机 / 单机不进入表现层。
        /// </summary>
        public CampfirePresentationAction Present(int value, int x, int y, int z)
        {
            EnsureWorld();
            if (!IsClientReplica()) return CampfirePresentationAction.None;
            if (y < 0 || y > 255) return CampfirePresentationAction.None;
            // 统一走状态机：燃烧营火 = Add/Keep/Replace；其它任何内容 = Remove（之前有的话）
            // 或 None（本来就没有）。绝不在这里写格 / 生成实体。
            return ApplyAndExecute(value, x, y, z);
        }

        /// <summary>这一格明确移除（OnBlockRemoved）：收掉粒子，不再跟踪。</summary>
        public void Forget(int x, int y, int z)
        {
            EnsureWorld();
            if (!m_state.Forget(new Point3(x, y, z))) return;
            if (m_particles.TryGetValue(new Point3(x, y, z), out FireParticleSystem system))
            {
                m_particles.Remove(new Point3(x, y, z));
                Stop(system);
            }
        }

        /// <summary>区块丢弃：该块里的营火粒子一并收掉（不会随区块重新加载而复用旧对象）。</summary>
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
                m_state.Forget(m_forget[i]);
                if (!m_particles.TryGetValue(m_forget[i], out FireParticleSystem system)) continue;
                m_particles.Remove(m_forget[i]);
                Stop(system);
            }
            if (m_forget.Count > 0) m_chunkForgets += m_forget.Count;
            m_forget.Clear();
        }

        /// <summary>
        /// 客户端每帧：1 秒一次有界越界扫除（counter: campfireStaleSweep）。只检查本类
        /// 跟踪的粒子；区块未加载的格跳过（不当成"不是营火"误删）。
        /// </summary>
        public void Tick(float dt)
        {
            EnsureWorld();
            if (!IsClientReplica() || m_particles.Count == 0) return;
            double now = Time.RealTime;
            if (now < m_nextSweepTime) return;
            m_nextSweepTime = now + 1.0;
            m_stale.Clear();
            // counter: campfireStaleSweep（只收格子已不是燃烧营火的粒子；每轮有界）。
            m_state.Sweep(ReadCellValue, m_stale,
                CampfirePresentationPolicy.MaxSweepChecksPerRun);
            for (int i = 0; i < m_stale.Count; i++)
            {
                Point3 cell = m_stale[i];
                if (!m_particles.TryGetValue(cell, out FireParticleSystem system)) continue;
                m_particles.Remove(cell);
                Stop(system);
            }
            if (m_stale.Count > 0)
            {
                LogProblem("营火粒子越界扫除：收掉 " + m_stale.Count +
                    " 个格子的陈旧粒子（" + StaleSweepCounter + " 累计 " +
                    m_state.StaleSwept + "；有界检查 " +
                    CampfirePresentationPolicy.MaxSweepChecksPerRun + " 格/秒）");
            }
            MaybeLog(now);
        }

        // ---------------------------------------------------------------- 主机：烧尽检测

        /// <summary>base.Update 之前：快照引擎营火表里的格子（只读）。</summary>
        public void BeginHostUpdate(SubsystemCampfireBlockBehavior behavior)
        {
            EnsureWorld();
            if (IsClientReplica() || behavior == null) return;
            m_detector.Begin(behavior.Campfires);
        }

        /// <summary>
        /// base.Update 之后：找出"刚烧尽"的格（contents 209 / data 0，且之前是燃烧营火）。
        /// 引擎已经在本端播过 Audio/Sizzles；这里只把格交给声音同步模块转给其它端。
        /// </summary>
        public void EndHostUpdate()
        {
            EnsureWorld();
            if (IsClientReplica()) return;
            Point3[] cells = m_detector.End(ReadCellValue);
            if (cells.Length == 0) return;
            m_hostDetected += cells.Length;
            m_hostEvents++;
            Action<Point3[]> handler = HostExtinguished;
            if (handler == null) return;
            try
            {
                handler(cells);
            }
            catch (Exception e)
            {
                LogProblem("营火烧尽事件上报异常（" + cells.Length + " 格）：" +
                    e.GetType().Name + " " + e.Message + "（本端引擎表现不受影响）");
            }
        }

        // ---------------------------------------------------------------- 表现执行

        private CampfirePresentationAction ApplyAndExecute(int value, int x, int y, int z)
        {
            var cell = new Point3(x, y, z);
            bool burning = CampfirePresentationPolicy.IsBurningValue(value);
            if (burning && EnsureParticlesSubsystem() == null)
            {
                // 粒子子系统不可用（Project 正在卸载 / 引擎字段变化）：明确计数 + 限流一行，
                // 不把状态机标成"已建"（否则之后永远不会重试）。
                m_missingParticles++;
                LogProblemOnce("campfirePresentation: 找不到 SubsystemParticles → 客户端营火粒子不可用" +
                    "（主机权威格子仍同步；计数 " + m_missingParticles + "）");
                return CampfirePresentationAction.None;
            }
            CampfirePresentationAction action = m_state.Apply(value, x, y, z);
            switch (action)
            {
                case CampfirePresentationAction.Add:
                    AddParticle(cell, CampfirePresentationPolicy.DataOf(value));
                    break;
                case CampfirePresentationAction.Replace:
                    if (m_particles.TryGetValue(cell, out FireParticleSystem replaced))
                    {
                        m_particles.Remove(cell);
                        Stop(replaced);
                    }
                    AddParticle(cell, CampfirePresentationPolicy.DataOf(value));
                    break;
                case CampfirePresentationAction.Remove:
                    if (m_particles.TryGetValue(cell, out FireParticleSystem removed))
                    {
                        m_particles.Remove(cell);
                        Stop(removed);
                    }
                    break;
                case CampfirePresentationAction.Keep:
                case CampfirePresentationAction.None:
                    break;
                case CampfirePresentationAction.Rejected:
                    LogProblem("客户端营火粒子达到上限 " +
                        CampfirePresentationPolicy.MaxTrackedParticles +
                        "（campfireParticleCapHits=" + m_state.CapHits + "）→ 本格不建粒子");
                    break;
            }
            return action;
        }

        private void AddParticle(Point3 cell, int data)
        {
            SubsystemParticles particles = EnsureParticlesSubsystem();
            if (particles == null) return;      // 调用方已经计数 / 记录
            try
            {
                var system = new FireParticleSystem(
                    new Vector3(cell.X, cell.Y, cell.Z) + CampfirePresentationPolicy.ParticleOffset,
                    CampfirePresentationPolicy.ParticleSize(data),
                    CampfirePresentationPolicy.MaxVisibilityDistance);
                particles.AddParticleSystem(system);
                m_particles[cell] = system;
            }
            catch (Exception e)
            {
                // 粒子创建失败：摘掉跟踪（下一拍可重试），明确计数 + 限流日志，不假装已渲染。
                m_state.Untrack(cell);
                m_particleFaults++;
                LogProblem("营火粒子创建失败（" + cell.X + "," + cell.Y + "," + cell.Z + "）：" +
                    e.GetType().Name + " " + e.Message + "（累计 " + m_particleFaults + " 次）");
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
            m_project = project;
            m_particles.Clear();
            m_particlesSubsystem = null;
            m_terrain = null;
            m_state.ClearTracked();
            m_detector.Reset();
            m_forget.Clear();
            m_stale.Clear();
            m_nextSweepTime = 0.0;
            m_lastLoggedAdded = m_state.Added;
            m_lastLoggedRemoved = m_state.Removed;
            m_lastLoggedStale = m_state.StaleSwept;
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

        // ---------------------------------------------------------------- 诊断

        /// <summary>10 秒一行的限流摘要（计数有变化 / 有活动时才写，不用一次性标志）。</summary>
        public void MaybeLog(double now)
        {
            if (now < m_nextLogTime) return;
            bool changed = m_state.Added != m_lastLoggedAdded ||
                           m_state.Removed != m_lastLoggedRemoved ||
                           m_state.StaleSwept != m_lastLoggedStale ||
                           m_hostEvents != m_lastLoggedHostEvents ||
                           m_chunkForgets != m_lastLoggedChunkForgets;
            if (!changed) return;
            m_nextLogTime = now + 10.0;
            m_lastLoggedAdded = m_state.Added;
            m_lastLoggedRemoved = m_state.Removed;
            m_lastLoggedStale = m_state.StaleSwept;
            m_lastLoggedHostEvents = m_hostEvents;
            m_lastLoggedChunkForgets = m_chunkForgets;
            Log.Information("[SuMP] " + Describe());
        }

        private void LogProblemOnce(string message)
        {
            if (m_missingParticlesLogged) return;
            m_missingParticlesLogged = true;
            Log.Warning("[SuMP] " + message);
        }

        private void LogProblem(string message)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastProblemLogMs != 0 && nowMs - m_lastProblemLogMs < 10000) return;
            m_lastProblemLogMs = nowMs;
            Log.Warning("[SuMP] " + message);
        }

        public int ActiveCount => m_particles.Count;
        public long HostDetected => m_hostDetected;
        public long HostEvents => m_hostEvents;

        public string Describe() =>
            "campfirePresentation " + m_state.Describe() +
            " active=" + m_particles.Count +
            " chunkForgets=" + m_chunkForgets +
            " hostExtinguished=" + m_hostDetected + " hostEvents=" + m_hostEvents +
            " missingParticles=" + m_missingParticles +
            " particleFaults=" + m_particleFaults + " resets=" + m_resets;

        /// <summary>世界切换 / mod 下线：先收掉本端还活着的粒子，再清账本（保留计数）。</summary>
        public void Reset()
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
            m_state.ClearTracked();
            m_detector.Reset();
            m_project = null;
            m_particlesSubsystem = null;
            m_terrain = null;
            m_missingParticlesLogged = false;
            m_nextSweepTime = 0.0;
            m_nextLogTime = 0.0;
            m_resets++;
        }
    }
}
