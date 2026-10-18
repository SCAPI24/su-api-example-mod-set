using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Terrain;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 客户端**引信表现**（只读主机下发的格子 data；不倒计时、不点火、不爆炸）。
    ///
    /// 数据来源只有两处，且都是"主机已经落地的格子状态"：
    ///   · <c>TerrainBridge.ApplyCells</c>：主机 CellChange / 整块下发落到客户端之后（单一漏斗）；
    ///   · <c>SuSubsystemExplosivesBlockBehavior</c> 的 OnBlockGenerated / OnBlockAdded / OnBlockModified：
    ///     客户端从本地存档加载区块时由引擎扫描器回调（与引擎火/篝火的表现挂接方式一致）。
    ///
    /// 引信格子状态见 <see cref="FuseCellState"/>：火药桶 data 第 8..13 位 = 剩余 tick（主机写）。
    /// 客户端只判 <c>IsLit</c>：亮着就一直渲染 <c>FuseParticleSystem</c>，直到：
    ///   · 主机清位（熄灭 / 卸下）→ CellChange 到达 → 本类收掉粒子；
    ///   · 方块被爆炸/挖掉（内容不再是火药桶）→ 同上；
    ///   · 区块卸载 → <c>ForgetChunk</c>。
    /// 客户端**不**根据 tick 数自行递减、也**不**在 0 时触发任何东西 —— 爆炸只由主机的
    /// ExplosionEvent + CellChanges 驱动（见 Engine/ExplosionReplicaEngine.cs）。
    ///
    /// 线程：只由主线程调用（ApplyCells / 方块行为回调都在引擎线程）。
    /// 无静态状态：每个 mod 会话一个实例，由组合根挂在 <c>TerrainBridge.FusePresenter</c> 上。
    /// </summary>
    public sealed class FuseReplicaPresentation
    {
        private readonly Dictionary<Point3, FuseParticleSystem> m_particles =
            new Dictionary<Point3, FuseParticleSystem>();
        private readonly List<Point3> m_forget = new List<Point3>(8);

        private Project m_project;
        private SubsystemParticles m_particlesSubsystem;
        /// <summary>客户端副本判定的缓存（整块下发一次会上千格，不能每格 FindSubsystem）。</summary>
        private Project m_shellProject;
        private SuSubsystemTerrain m_shell;
        private long m_presented;
        private long m_removed;
        private long m_chunkForgets;
        private long m_missingParticlesSubsystem;
        private long m_resets;
        private bool m_missingLogged;

        /// <summary>主机 CellChange / 整块下发落地后调用（只对客户端副本生效）。</summary>
        public void Apply(ChunkKey key, IReadOnlyList<CellEdit> edits)
        {
            if (edits == null || edits.Count == 0) return;
            int baseX = key.X * 16;
            int baseZ = key.Z * 16;
            for (int i = 0; i < edits.Count; i++)
            {
                CellEdit edit = edits[i];
                Present(baseX + edit.X, edit.Y, baseZ + edit.Z, edit.Contents, edit.Data);
            }
        }

        /// <summary>按一格的主机权威值更新表现：火药桶 + 引信位亮 = 有火；其它情况 = 没有。</summary>
        public void Present(int x, int y, int z, int contents, int data)
        {
            EnsureWorld();
            var cell = new Point3(x, y, z);
            if (y < 0 || y > 255)
            {
                Forget(cell);
                return;
            }
            if (!IsClientReplica())
            {
                Forget(cell);
                return;
            }
            if (!(BlockOf(contents) is GunpowderKegBlock keg) || !FuseCellState.IsLit(data))
            {
                Forget(cell);
                return;
            }
            if (m_particles.ContainsKey(cell)) return;      // 引信 tick 只改 data：不重建粒子（避免每 0.25s 闪一下）
            SubsystemParticles particles = EnsureParticlesSubsystem();
            if (particles == null)
            {
                // 找不到粒子子系统（Project 正在卸载 / 引擎字段变化）：明确计数 + 限流一行，不假装已经渲染。
                m_missingParticlesSubsystem++;
                if (!m_missingLogged)
                {
                    m_missingLogged = true;
                    Log.Warning("[SuMP] fusePresentation: 找不到 SubsystemParticles → 客户端引信表现不可用" +
                        "（主机权威格子 data 仍然同步；计数 " + m_missingParticlesSubsystem + "）");
                }
                return;
            }
            var system = new FuseParticleSystem(new Vector3(x, y, z) + keg.FuseOffset);
            particles.AddParticleSystem(system);
            m_particles[cell] = system;
            m_presented++;
        }

        /// <summary>这一格不再有引信（卸下 / 熄灭 / 被爆炸或挖掘替换）。</summary>
        public void Forget(int x, int y, int z) => Forget(new Point3(x, y, z));

        private void Forget(Point3 cell)
        {
            if (!m_particles.TryGetValue(cell, out FuseParticleSystem system)) return;
            m_particles.Remove(cell);
            m_removed++;
            Stop(system);
        }

        /// <summary>区块卸载：该块里的引信粒子一并收掉（不会随区块重新加载而复用旧对象）。</summary>
        public void ForgetChunk(TerrainChunk chunk)
        {
            if (chunk == null || m_particles.Count == 0) return;
            int originX = chunk.Origin.X;
            int originZ = chunk.Origin.Y;
            m_forget.Clear();
            foreach (Point3 cell in m_particles.Keys)
            {
                if (cell.X < originX || cell.X >= originX + 16) continue;
                if (cell.Z < originZ || cell.Z >= originZ + 16) continue;
                m_forget.Add(cell);
            }
            for (int i = 0; i < m_forget.Count; i++) Forget(m_forget[i]);
            m_forget.Clear();
            m_chunkForgets++;
        }

        /// <summary>
        /// 世界切换 / mod 下线：先按粒子的所有者子系统正常摘掉（mod 卸载时世界可能还活着），
        /// 再清账本；保留计数，便于状态行回答"表现层到底跑没跑"。
        /// </summary>
        public void Reset()
        {
            m_forget.Clear();
            foreach (Point3 cell in m_particles.Keys) m_forget.Add(cell);
            for (int i = 0; i < m_forget.Count; i++) Forget(m_forget[i]);
            m_forget.Clear();
            m_particles.Clear();
            m_project = null;
            m_particlesSubsystem = null;
            m_shellProject = null;
            m_shell = null;
            m_missingLogged = false;
            m_resets++;
        }

        public long Presented => m_presented;
        public long Removed => m_removed;
        public long ChunkForgets => m_chunkForgets;
        public long MissingParticlesSubsystem => m_missingParticlesSubsystem;
        public int ActiveCount => m_particles.Count;

        public string Describe() =>
            "fusePresentation presented=" + m_presented + " active=" + m_particles.Count +
            " removed=" + m_removed + " chunkForgets=" + m_chunkForgets +
            " missingParticles=" + m_missingParticlesSubsystem + " resets=" + m_resets;

        private void Stop(FuseParticleSystem system)
        {
            if (system == null) return;
            try
            {
                // 用粒子的**所有者**子系统（AddParticleSystem 时引擎写入 SubsystemParticles 字段），
                // 不在世界切换后把旧粒子拿到新子系统上 Remove。
                SubsystemParticles particles = system.SubsystemParticles ?? EnsureParticlesSubsystem();
                particles?.RemoveParticleSystem(system);
            }
            catch (Exception)
            {
                // 世界正在卸载时粒子子系统可能已不可用：对象随世界销毁，账本已经摘掉（不重复 Stop）。
            }
        }

        private SubsystemParticles EnsureParticlesSubsystem()
        {
            EnsureWorld();
            Project project = m_project;
            if (m_particlesSubsystem == null && project != null)
                m_particlesSubsystem = project.FindSubsystem<SubsystemParticles>(false);
            return m_particlesSubsystem;
        }

        /// <summary>
        /// 世界切换：旧世界的粒子对象已经随旧 Project 销毁，不能在下个世界按坐标复用
        /// （同坐标的新火药桶会被 `ContainsKey` 误判成"已经有粒子"）。这里只清账本。
        /// </summary>
        private void EnsureWorld()
        {
            Project project = GameManager.Project;
            if (ReferenceEquals(project, m_project)) return;
            m_project = project;
            m_particles.Clear();
            m_particlesSubsystem = null;
        }

        /// <summary>客户端副本才做表现（主机由引擎自己的 FuseParticleSystem 渲染，绝不重复建粒子）。</summary>
        private bool IsClientReplica()
        {
            Project project = GameManager.Project;
            if (!ReferenceEquals(project, m_shellProject) || m_shell == null)
            {
                m_shellProject = project;
                m_shell = project?.FindSubsystem<SubsystemTerrain>(false) as SuSubsystemTerrain;
            }
            return m_shell != null && m_shell.ClientReplica;
        }

        private static Block BlockOf(int contents) =>
            contents > 0 && BlocksManager.Blocks != null && contents < BlocksManager.Blocks.Length
                ? BlocksManager.Blocks[contents]
                : null;
    }
}
