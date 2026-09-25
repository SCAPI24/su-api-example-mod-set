using Engine;
using Game;
using System.Collections;
using System.Collections.Generic;

namespace ScMultiplayer
{
    /// <summary>
    /// 《玩家领地》P7c：流体（水 / 岩浆）"改回式"守卫的共用实现。
    ///
    /// 为什么只能"改回式"（源码依据，勿凭记忆改）：
    ///   · 流体写入发生在 <c>SubsystemFluidBlockBehavior.SpreadFluid()</c> 内，是**非虚**流程：
    ///     写入先汇聚到私有 <c>m_toSet</c>，在同文件 293-310 行经
    ///     <c>SubsystemTerrain.ChangeCell</c> / <c>DestroyCell</c> 落地；
    ///   · 该方法末尾（同文件 312 行）**自己**调用 <c>SubsystemTerrain.ProcessModifiedCells()</c>，
    ///     把引擎私有 <c>m_modifiedCells</c> 冲刷清空 —— 所以"在地形子系统里过滤格集"对流体无效
    ///     （等 <c>SuSubsystemTerrain</c> 看到时已经空了）；
    ///   · <c>SubsystemTerrain.ChangeCell</c>（引擎 229 行）与 <c>ProcessModifiedCells</c>（64 行）
    ///     都没有 <c>virtual</c>，无法覆写拦截。
    ///
    /// 因此做法与 <see cref="SuSubsystemFireBlockBehavior"/> 的既有范式一致：
    /// 在 <c>base.Update(dt)</c> / <c>base.OnBlockAdded(…)</c> 之前，对本窗口**可能被写入的格**
    /// 做值快照；跑完引擎逻辑后，把"新流进领地"的格改回旧值（<c>ChangeCell</c> 会走主机地形广播），
    /// 并走统一否决通知 <c>NotifyRegionModificationDenied(…, "fluid", …)</c>。
    ///
    /// 口径（与设计稿 §5「主机侧执法点（统一入口）」一致）：
    ///   · 用 <c>CanRegionModifyCell(0, …)</c>（主机本机玩家 id=0）——**拥有者可以在自己领地里放水**；
    ///   · 只挡"**新流进来**"：领地里本来就是同类流体的格，只改水位数据时不冻结、不通知
    ///     （否则领地内的天然水池会被逐帧回滚并刷屏）。
    /// </summary>
    internal sealed class SuFluidClaimGuard
    {
        // Source: Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:SubsystemFluidBlockBehavior.m_sideNeighbors
        private static readonly Point2[] m_sideOffsets = new Point2[4]
        {
            new Point2(-1, 0),
            new Point2(1, 0),
            new Point2(0, -1),
            new Point2(0, 1)
        };

        private readonly Dictionary<Point3, int> m_snapshot = new Dictionary<Point3, int>();
        private readonly List<Point3> m_revertCells = new List<Point3>();
        private readonly List<int> m_revertValues = new List<int>();
        private readonly List<RegionClaim> m_revertClaims = new List<RegionClaim>();

        /// <summary>
        /// 快照流体本帧的工作列表：引擎只会在"自身格 / 4 侧邻 / 下方格"三个方向上写入
        /// （见 <c>SpreadFluid()</c> 的 <c>Set(...)</c>、<c>FlowTo(...)</c> 与
        /// <c>OnFluidInteract(...)</c> 落点）。
        /// </summary>
        internal void CaptureFluidWorkList(ScMultiplayer mod, Terrain terrain, IDictionary toUpdate)
        {
            m_snapshot.Clear();
            if (mod == null || terrain == null || toUpdate == null || toUpdate.Count == 0)
                return;
            foreach (object key in toUpdate.Keys)
            {
                if (!(key is Point3 point))
                    continue;
                // 只挡"**从领地外流进领地内**"：工作列表里的这个流体格若自身已在领地内，
                // 它的扩散属于"领地内流体自身演化"（§34.3 第 3 面"不冻结"）⇒ 整格跳过，
                // 否则拥有者自己放的水也会被逐帧回滚（归属按主机身份判定，必然不是拥有者）。
                if (mod.OwnerClaimAt(point) != null)
                    continue;
                Capture(mod, terrain, point.X, point.Y, point.Z);
                for (int i = 0; i < m_sideOffsets.Length; i++)
                {
                    Capture(mod, terrain,
                        point.X + m_sideOffsets[i].X, point.Y,
                        point.Z + m_sideOffsets[i].Y);
                }
                Capture(mod, terrain, point.X, point.Y - 1, point.Z);
            }
        }

        /// <summary>
        /// 快照邻域：岩浆的邻域效应
        /// （<c>SubsystemMagmaBlockBehavior.ApplyMagmaNeighborhoodEffect</c>，私有方法，会点燃可燃物、
        /// 并把 61/62 号方块 <c>DestroyCell</c> 掉）在 <c>OnBlockAdded</c> /
        /// <c>OnNeighborBlockChanged</c> 里对 3×3×3 邻域生效，不经过 <c>SpreadFluid()</c>
        /// 那条窗口，必须单独开窗。
        /// </summary>
        internal void CaptureNeighborhood(ScMultiplayer mod, Terrain terrain, int x, int y, int z,
            int radius)
        {
            m_snapshot.Clear();
            if (mod == null || terrain == null)
                return;
            // 同 Update 窗口的口径：岩浆自身已在领地内 ⇒ 它的邻域效应对领地内方块的影响
            // 属于"领地内流体自身演化"，不拦（只挡领地里"新进来"的流体）。
            if (mod.OwnerClaimAt(new Point3(x, y, z)) != null)
                return;
            for (int i = -radius; i <= radius; i++)
            {
                for (int j = -radius; j <= radius; j++)
                {
                    for (int k = -radius; k <= radius; k++)
                    {
                        Capture(mod, terrain, x + i, y + j, z + k);
                    }
                }
            }
        }

        private void Capture(ScMultiplayer mod, Terrain terrain, int x, int y, int z)
        {
            if (y < 0 || y > 255)
                return;
            var cell = new Point3(x, y, z);
            if (m_snapshot.ContainsKey(cell))
                return;
            // 只有领地内的格才需要守卫：领地外原样放行，快照开销也随之收敛到"领地边界附近"
            if (mod.OwnerClaimAt(cell) == null)
                return;
            m_snapshot[cell] = terrain.GetCellValue(x, y, z);
        }

        /// <summary>把本窗口内"新流进领地"的格改回旧值，并逐格走统一否决通知。</summary>
        internal void Revert(ScMultiplayer mod, SubsystemTerrain subsystemTerrain, int fluidBlockIndex)
        {
            if (m_snapshot.Count == 0)
                return;
            if (mod == null || subsystemTerrain?.Terrain == null)
            {
                m_snapshot.Clear();
                return;
            }
            Terrain terrain = subsystemTerrain.Terrain;
            m_revertCells.Clear();
            m_revertValues.Clear();
            m_revertClaims.Clear();
            // 先收集再落地：ChangeCell 会触发邻居通知，可能重入本类的 Capture*（会清空 m_snapshot），
            // 所以绝不能在枚举 m_snapshot 的过程中写格。
            foreach (KeyValuePair<Point3, int> item in m_snapshot)
            {
                Point3 cell = item.Key;
                int oldValue = item.Value;
                // 领地内本来就是同类流体：只改水位/顶面数据，不冻结、不通知
                if (Terrain.ExtractContents(oldValue) == fluidBlockIndex)
                    continue;
                int nowValue = terrain.GetCellValue(cell.X, cell.Y, cell.Z);
                if (nowValue == oldValue)
                    continue;
                // 不是"这种流体流进来"（例如领地内的水自然干掉变空气）→ 不干预
                if (Terrain.ExtractContents(nowValue) != fluidBlockIndex)
                    continue;
                if (mod.CanRegionModifyCell(0, cell, out RegionClaim claim, out string _))
                    continue;
                m_revertCells.Add(cell);
                m_revertValues.Add(oldValue);
                m_revertClaims.Add(claim);
            }
            m_snapshot.Clear();
            // 落地前再复制一份：ChangeCell 会触发邻居通知，岩浆的 OnNeighborBlockChanged 会**嵌套**
            // 进入本类开新窗口并复用这几个列表，直接按下标遍历原列表会被嵌套调用改写。
            Point3[] cells = m_revertCells.ToArray();
            int[] values = m_revertValues.ToArray();
            RegionClaim[] claims = m_revertClaims.ToArray();
            for (int i = 0; i < cells.Length; i++)
            {
                subsystemTerrain.ChangeCell(cells[i].X, cells[i].Y, cells[i].Z, values[i]);
                mod.NotifyRegionModificationDenied(0, cells[i], claims[i], "fluid", null);
            }
        }

        /// <summary>窗口结束时丢弃未消费的快照（例如主机判定分支没走到 Revert）。</summary>
        internal void Reset()
        {
            m_snapshot.Clear();
        }
    }
}
