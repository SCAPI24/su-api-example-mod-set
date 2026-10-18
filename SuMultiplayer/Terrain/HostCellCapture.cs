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

        // ------------------------------------------------------------------ 流体捕获窗口
        //
        // 为什么需要"窗口"（实机 desync 的第二个根因，2026-10-18）：
        //   SubsystemFluidBlockBehavior.SpreadFluid（Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:194）
        //   在**一次 Update 里跑两遍**（for i in 0..1），每一遍末尾都自己调
        //   SubsystemTerrain.ProcessModifiedCells()（:312）—— 它先把 m_modifiedCells 整表清空，
        //   再逐个通知邻居：
        //     · 第 0 遍写下的格子，在第 1 遍末尾被清掉；
        //     · 第 1 遍处理的 m_toUpdate 是**第 0 遍末尾那次 ProcessModifiedCells 才排进来的新前沿**，
        //       它不在 base.Update 之前的候选快照里；
        //     · UpdateIsTop 的 data 直写（:41-55）不经过 ChangeCell，只能靠行为回调前后的 before/after
        //       对比发现；这个登记若发生在第 0 遍，也会被第 1 遍的清表抹掉。
        //   ⇒ 只靠"base.Update 前后一次快照"必然漏掉第 1 遍的写入。窗口把这些格子收在**独立集合**里，
        //   base.Update 返回后再一次性并回 m_modifiedCells，内部清表再也抹不掉（每格只并一次）。
        //
        // 计数口径（模块状态行 / 限流摘要都会写出来）：
        //   · FluidDataBroadcasts：内容未变、只有 data 变了的格，**真的并进下发批次**的累计数；
        //   · FluidDataSkipped   ：候选格读前读后相同（或只有光照变化）→ 明确跳过的累计数；
        //   · FluidDataDuplicates：同一格被多条路径重复报告（内容+data、回调+候选复核、同帧两次改写）
        //                          → 合并成一次下发的累计数。

        /// <summary>一次流体 Update 内捕获到的一格（<see cref="DataOnly"/> = 内容未变、只有 data 变）。</summary>
        public readonly struct FluidCapturedCell
        {
            public FluidCapturedCell(Point3 cell, bool dataOnly)
            {
                Cell = cell;
                DataOnly = dataOnly;
            }

            public Point3 Cell { get; }
            public bool DataOnly { get; }
        }

        private readonly Dictionary<Point3, bool> m_fluidWindow = new Dictionary<Point3, bool>();
        private bool m_fluidWindowActive;
        private long m_fluidDataBroadcasts;
        private long m_fluidDataSkipped;
        private long m_fluidDataDuplicates;
        private long m_fluidCandidateCells;
        private long m_fluidCandidateCapHits;
        private long m_fluidCellsMerged;
        private long m_fluidDropped;
        private long m_fluidWindows;

        /// <summary>当前是否处在一次流体 Update 的捕获窗口内。</summary>
        public bool FluidWindowActive => m_fluidWindowActive;
        /// <summary>窗口内还没并回引擎改动表的格数。</summary>
        public int PendingFluidCount => m_fluidWindow.Count;
        /// <summary>内容未变、data 变了的格**真的进了下发批次**的累计数。</summary>
        public long FluidDataBroadcasts => m_fluidDataBroadcasts;
        /// <summary>候选格读前读后相同（不是真实改动）而跳过的累计数。</summary>
        public long FluidDataSkipped => m_fluidDataSkipped;
        /// <summary>同一格被重复报告、合并为一次下发的累计数。</summary>
        public long FluidDataDuplicates => m_fluidDataDuplicates;
        /// <summary>参与 before/after 比较的候选格累计数（工作量口径）。</summary>
        public long FluidCandidateCells => m_fluidCandidateCells;
        /// <summary>候选键超过每帧上限而**没有比较**的键累计数（回调捕获仍覆盖这些格）。</summary>
        public long FluidCandidateCapHits => m_fluidCandidateCapHits;
        /// <summary>窗口里并进引擎改动表的格累计数（含内容变化 + data-only）。</summary>
        public long FluidCellsMerged => m_fluidCellsMerged;
        /// <summary>窗口关闭时引擎改动表绑定失败 / 无会话而**明确丢弃**的格累计数（不静默）。</summary>
        public long FluidCellsDropped => m_fluidDropped;
        /// <summary>流体捕获窗口打开次数（>0 说明主机侧捕获确实在跑；=0 时先查会话 / 客户端副本闸门）。</summary>
        public long FluidCaptureWindows => m_fluidWindows;

        /// <summary>
        /// 打开一次流体 Update 的捕获窗口（主机、有会话时才调用；客户端不走这里）。
        /// 窗口**不设人为格数上限**：这里只收真的发生了的写入（每格一条），设上限就等于丢改动、
        /// 正是本次要修的 desync；候选**扫描**仍沿用每帧 64 键上限（见 FluidCandidateCapHits）。
        /// </summary>
        public void BeginFluidWindow()
        {
            // 正常路径是 Begin → End 成对；若上一次异常没关窗口，旧内容留在集合里继续参与合并，
            // 不丢格（End 时才清空）。
            m_fluidWindowActive = true;
            m_fluidWindows++;
        }

        /// <summary>
        /// 记录一次**确定发生**的流体写入。<paramref name="dataOnly"/>：
        /// true = ChangeCell 的 OnBlockModified（内容不变、data 变了）；false = OnBlockAdded /
        /// OnBlockRemoved（内容变了）或内容+data 一起变。
        /// </summary>
        public void NoteFluidChange(Point3 cell, bool dataOnly)
        {
            if (cell.Y < 0 || cell.Y > 255) return;
            if (m_fluidWindow.TryGetValue(cell, out bool existingDataOnly))
            {
                m_fluidDataDuplicates++;
                // 同一格既有内容变化又有 data 变化 ⇒ 仍只下发一次，按"内容变了"记账。
                if (!dataOnly) m_fluidWindow[cell] = false;
                else if (existingDataOnly) m_fluidWindow[cell] = true;
                return;
            }
            m_fluidWindow[cell] = dataOnly;
        }

        /// <summary>
        /// 比较候选格在一次调用前后的值（contents+data）。只有真实变化才进窗口：
        ///   · 内容变了 → 按内容变化记（与 OnBlockAdded/OnBlockRemoved 的回调捕获合并）；
        ///   · 内容不变、data 变了 → data-only（UpdateIsTop 直写 / ChangeCell 水位变化）；
        ///   · 完全相同或只有光照变化 → 明确计入 skipped，不入队。
        /// </summary>
        public void NoteFluidCandidate(Point3 cell, int before, int after)
        {
            m_fluidCandidateCells++;
            if (cell.Y < 0 || cell.Y > 255) return;
            if (before == after)
            {
                m_fluidDataSkipped++;
                return;
            }
            int beforeContents = before & 0x3FF;
            int afterContents = after & 0x3FF;
            if (beforeContents != afterContents)
            {
                NoteFluidChange(cell, dataOnly: false);
                return;
            }
            int beforeData = (int)((uint)before >> 14);
            int afterData = (int)((uint)after >> 14);
            if (beforeData == afterData)
            {
                // 只有光照位不同：摘要只含 contents+data，不下发（避免把无意义改动塞进广播）。
                m_fluidDataSkipped++;
                return;
            }
            NoteFluidChange(cell, dataOnly: true);
        }

        /// <summary>
        /// 关闭窗口并取出**去重后**的格子（<paramref name="output"/> 会被清空）。返回格数。
        /// 调用方随后把它们逐格并入引擎改动表（`MarkCellModifiedForBroadcast` 的同一张表）。
        /// </summary>
        public int EndFluidWindow(List<FluidCapturedCell> output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            output.Clear();
            if (!m_fluidWindowActive)
            {
                return 0;
            }
            foreach (KeyValuePair<Point3, bool> pair in m_fluidWindow)
            {
                output.Add(new FluidCapturedCell(pair.Key, pair.Value));
            }
            m_fluidWindow.Clear();
            m_fluidWindowActive = false;
            return output.Count;
        }

        /// <summary>窗口格子并回引擎改动表之后记账：merged / 其中 data-only / 表中已存在而去重。</summary>
        public void NoteFluidMergeStats(int merged, int dataOnlyMerged, int duplicates)
        {
            if (merged > 0) m_fluidCellsMerged += merged;
            if (dataOnlyMerged > 0) m_fluidDataBroadcasts += dataOnlyMerged;
            if (duplicates > 0) m_fluidDataDuplicates += duplicates;
        }

        /// <summary>窗口有格但无法并回（改动表绑定失败 / 会话结束）：明确计数，不假装发过。</summary>
        public void NoteFluidDropped(int count)
        {
            if (count > 0) m_fluidDropped += count;
        }

        /// <summary>候选键因每帧上限没有参与比较：只记账（这些格仍由行为回调窗口捕获）。</summary>
        public void NoteFluidCandidateCapHit(int skippedKeys)
        {
            if (skippedKeys > 0) m_fluidCandidateCapHits += skippedKeys;
        }

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
        /// 调用方必须**明确计数**（不能假装它们已经发过）。返回丢弃的格数（接住 + 流体窗口）。
        /// </summary>
        public int Clear()
        {
            int dropped = m_rescued.Count + m_fluidWindow.Count;
            m_rescued.Clear();
            m_pending.Clear();
            m_fluidWindow.Clear();
            m_fluidWindowActive = false;
            if (dropped > 0) m_fluidDropped += dropped;
            return dropped;
        }
    }
}
