using System;
using System.Collections.Generic;
using Engine;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 主机侧"改动捕获"的**唯一事实来源**（纯托管，不依赖 Game：自检直接编译本文件）。
    ///
    /// 引擎事实（Survivalcraft/Game/SubsystemTerrain.cs）：
    ///   · <c>ChangeCell</c> / <c>DestroyCell</c> 会把格子写进私有 <c>m_modifiedCells</c>；
    ///   · <c>ProcessModifiedCells</c> 在**开始**就把整张表清空，再逐格通知邻居。
    ///
    /// 关键坑（实机 desync 根因）：<c>SubsystemFluidBlockBehavior.SpreadFluid</c>
    /// （Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:312）在**水 / 岩浆自己的 Update 里**
    /// 调了一次 <c>ProcessModifiedCells()</c>。水的 UpdateOrder 是 <c>Default = 0</c>，
    /// 而地形子系统的 UpdateOrder 是 <c>Terrain = 100</c>；同一个 UpdateOrder 内的先后
    /// 由 <c>GetHashCode</c> 决定（Survivalcraft/Game/SubsystemUpdate.cs:26），因此
    /// **玩家组件（ComponentPlayer / ComponentMiner，同为 Default）的挖掘 / 放置有可能排在
    /// 流体 Update 之前**：那一帧玩家刚写进 <c>m_modifiedCells</c> 的格子会被流体的
    /// <c>ProcessModifiedCells</c> 清掉，等到 <c>SubsystemTerrain.Update</c> 再读表时已经不存在
    /// ⇒ 主机本地改了、客户端永远收不到（日志里那一格没有"下发改动"）。
    ///
    /// 所以捕获分两步，且都在同一个实例上（没有静态状态）：
    ///   ① <see cref="Rescue"/>：在**任何会清表的引擎调用之前**（目前只有流体 Update）把当时
    ///      待下发的格子接住；
    ///   ② <see cref="Collect"/>：在地形 Update 里把"引擎改动表 ∪ 接住的格子"合并、去重、
    ///      按唯一一份清单交给桥下发。
    /// 这样一次真实改动恰好下发一次：同一格既在表里又被登记（例如流体 data 直写后
    /// <c>MarkCellModifiedForBroadcast</c>）时只保留一份；接住后又被流体改写时，
    /// 仍在表中（Value 以读表那一刻为准）→ 不会下发两次，也不会下发旧值。
    /// </summary>
    public sealed class HostCellCapture
    {
        /// <summary>被 <see cref="Rescue"/> 接住、等待与引擎改动表合并的格子。</summary>
        private readonly HashSet<Point3> m_rescued = new HashSet<Point3>();
        /// <summary>合并时判断"引擎表里已经有这一格"的复用集合（避免每次分配）。</summary>
        private readonly HashSet<Point3> m_pending = new HashSet<Point3>();

        private long m_rescuedTotal;
        private long m_capturedTotal;
        private long m_duplicateTotal;

        /// <summary>累计接住的格子数（rescue 次数 × 格数，用于诊断清表竞争）。</summary>
        public long Rescued => m_rescuedTotal;
        /// <summary>累计交给下发的格子数（每一格一次 = hostCellCaptured）。</summary>
        public long Captured => m_capturedTotal;
        /// <summary>累计被去重掉的格子数（同时出现在引擎表与接住集合里，broadcastDuplicates）。</summary>
        public long Duplicates => m_duplicateTotal;
        /// <summary>当前还挂着、尚未合并的接住格子数。</summary>
        public int PendingRescuedCount => m_rescued.Count;

        /// <summary>
        /// 在会清空引擎改动表的调用之前接住当前待下发格子。
        /// 传入的必须是引擎 <c>m_modifiedCells.Keys</c>（只记录键；值在 <see cref="Collect"/> 时才读，
        /// 因此后续同帧内对该格的改写会以最终值为准）。
        /// </summary>
        public void Rescue(IEnumerable<Point3> pendingCells)
        {
            if (pendingCells == null) return;
            foreach (Point3 cell in pendingCells)
            {
                if (cell.Y < 0 || cell.Y > 255) continue;
                if (m_rescued.Add(cell)) m_rescuedTotal++;
            }
        }

        /// <summary>
        /// 合并"引擎改动表 ∪ 接住集合"，输出**去重后**的待下发清单（<paramref name="output"/> 会被清空）。
        /// 返回本次去重掉的格子数；累计值见 <see cref="Duplicates"/>。
        /// </summary>
        public int Collect(IEnumerable<Point3> pendingCells, List<Point3> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            output.Clear();
            m_pending.Clear();
            if (pendingCells != null)
            {
                foreach (Point3 cell in pendingCells)
                {
                    if (cell.Y < 0 || cell.Y > 255) continue;
                    if (!m_pending.Add(cell)) continue;
                    output.Add(cell);
                }
            }
            int duplicates = 0;
            if (m_rescued.Count > 0)
            {
                foreach (Point3 cell in m_rescued)
                {
                    // 引擎表里已经有它 ⇒ 这次合并只需下发一份（表里那份，值是最新的）。
                    if (m_pending.Contains(cell)) { duplicates++; continue; }
                    output.Add(cell);
                }
                m_rescued.Clear();
            }
            m_pending.Clear();
            m_duplicateTotal += duplicates;
            m_capturedTotal += output.Count;
            return duplicates;
        }

        /// <summary>
        /// 放弃所有接住的格子（没有桥 / 表绑定失败 / 会话结束）：这些格子不会被下发，
        /// 调用方必须**明确计数**（不能假装它们已经发过）。返回丢弃的格数。
        /// </summary>
        public int Clear()
        {
            int dropped = m_rescued.Count;
            m_rescued.Clear();
            m_pending.Clear();
            return dropped;
        }
    }
}
