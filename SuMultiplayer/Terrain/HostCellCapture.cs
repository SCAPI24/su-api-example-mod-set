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

        // ------------------------------------------------------------------ 火捕获窗口
        //
        // 为什么火也必须有自己的窗口（实机 2026-10-19：主机火格烧尽/熄灭后客户端永远显示燃烧的 FireBlock）：
        //   · 引擎火的**所有**写格都在 `SubsystemFireBlockBehavior` 里：
        //       :231 烧尽目标块 `ChangeCell(...,0)`、:109 `SetCellOnFire` → ChangeCell、
        //       :305-310 邻居回调里把失去支撑的火格改成空气 → ChangeCell；
        //   · 但引擎会在火 Update **之外**、每 0.25s 由流体子系统调用
        //     `ProcessModifiedCells()`（Survivalcraft/Game/SubsystemFluidBlockBehavior.cs:312，
        //     `SpreadFluid` 的 `for i in 0..1` 两遍各调一次；水/岩浆/水下/水草/海参五个子系统都可能在跑，
        //     即使附近没有流体也照跑），它在 SubsystemTerrain.cs:71 **先清空整张改动表再通知邻居**；
        //   · 火格被邻居回调改成空气的那一刻**正好发生在那次通知内部**（ChangeCell 在清表之后入表），
        //     又被同一轮 / 下一轮清表抹掉 ⇒ 主机本地变空、客户端永远收不到，火永远画着。
        //   ⇒ 火外壳（Engine/ClientReplicaSubsystems.cs:SuSubsystemFireBlockBehavior）把
        //     Update 前后的候选格差异与行为回调里**确定发生**的写入收进本窗口；窗口（和窗口外的
        //     邻居回调）一律进 <see cref="m_fireCarry"/> 这个**跨清表存活**的集合，
        //     由 <see cref="Collect"/> 与引擎改动表合并去重 —— 与流体窗口同一个汇合点，绝不二次下发。
        //
        // 计数口径（限流摘要会写出来）：
        //   · FireCellCaptured  ：真的并进下发批次、交给桥的火格累计数（每格一次）；
        //   · FireCellSkipped   ：候选格读前读后相同（不是真实改动）→ 明确跳过的累计数；
        //   · FireCellDuplicates：同一格被多条路径重复报告（表里已有 / 回调 + 候选复核 / 同帧两次改写）
        //                        → 合并成一次下发的累计数。

        private readonly Dictionary<Point3, bool> m_fireWindow = new Dictionary<Point3, bool>();
        /// <summary>窗口 / 窗口外回调捕获的格子：**跨引擎清表存活**，由 Collect 与改动表合并。</summary>
        private readonly Dictionary<Point3, bool> m_fireCarry = new Dictionary<Point3, bool>();
        private bool m_fireWindowActive;
        private long m_fireCellCaptured;
        private long m_fireCellSkipped;
        private long m_fireCellDuplicates;
        private long m_fireCandidateCells;
        private long m_fireCandidateCapHits;
        private long m_fireWindows;
        private long m_fireDropped;

        // ------------------------------------------------------------------ 引信捕获窗口
        //
        // 为什么引信也必须有自己的窗口（实机 2026-10-19：主机点燃火药桶后客户端只看到爆炸、看不到引信在烧）：
        //   · 引擎的引信**不在格子里**：`SubsystemExplosivesBlockBehavior` 的
        //     `m_explosiveDataByPoint`（:33）→ `ExplosiveData.TimeToExplosion`（:16）是主机私有表；
        //     `IgniteFuse`（:41-55）/ `Update`（:57-92）**都不写格子**；主机看到的引信只是
        //     `FuseParticleSystem`（:69-73，仅火药桶）—— 本端表现，不随 CellChange 下发。
        //   · 联机时由主机外壳（Engine/ClientReplicaSubsystems.cs:SuSubsystemExplosivesBlockBehavior）
        //     把"剩余引信 tick"写进火药桶格子的 data（见 Terrain/FuseCellState.cs），
        //     这一笔 ChangeCell 与火的写格一样会落进 `m_modifiedCells`，而流体子系统每 0.25s 的
        //     `ProcessModifiedCells()`（SubsystemFluidBlockBehavior.cs:312）会先清表再通知邻居
        //     （SubsystemTerrain.cs:71）⇒ 只靠引擎改动表，引信状态可能一条都不下发。
        //   ⇒ 与火同一套窗口：窗口里的格子进**跨清表存活**的 carry，由 Collect 与引擎改动表合并去重，
        //     每格恰好下发一次。
        //
        // 计数口径：
        //   · FuseCellCaptured  ：真的并进下发批次、交给桥的引信格累计数（每格一次）；
        //   · FuseCellSkipped   ：候选格读前读后相同（不是真实改动）→ 明确跳过的累计数；
        //   · FuseCellDuplicates：同一格被多条路径重复报告（表里已有 / 回调 + 候选复核 / 同帧两次改写）
        //                        → 合并成一次下发的累计数。

        private readonly Dictionary<Point3, bool> m_fuseWindow = new Dictionary<Point3, bool>();
        /// <summary>窗口内捕获的引信格：**跨引擎清表存活**，由 Collect 与改动表合并。</summary>
        private readonly Dictionary<Point3, bool> m_fuseCarry = new Dictionary<Point3, bool>();
        private bool m_fuseWindowActive;
        private long m_fuseCellCaptured;
        private long m_fuseCellSkipped;
        private long m_fuseCellDuplicates;
        private long m_fuseCandidateCells;
        private long m_fuseCandidateCapHits;
        private long m_fuseWindows;
        private long m_fuseDropped;

        /// <summary>当前是否处在一次火 Update 的捕获窗口内。</summary>
        public bool FireWindowActive => m_fireWindowActive;
        /// <summary>已捕获、等下一次 Collect 合并进下发批次的火格数。</summary>
        public int PendingFireCount => m_fireCarry.Count;
        /// <summary>真的进了下发批次（交给桥）的火格累计数 = fireCellCaptured。</summary>
        public long FireCellCaptured => m_fireCellCaptured;
        /// <summary>候选格读前读后相同（不是真实改动）而跳过的累计数 = fireCellSkipped。</summary>
        public long FireCellSkipped => m_fireCellSkipped;
        /// <summary>同一格被重复报告、合并为一次下发的累计数 = fireCellDuplicates。</summary>
        public long FireCellDuplicates => m_fireCellDuplicates;
        /// <summary>参与 before/after 比较的火候选格累计数（工作量口径）。</summary>
        public long FireCandidateCells => m_fireCandidateCells;
        /// <summary>候选键超过上限而没有比较的键累计数（行为回调仍覆盖这些格）。</summary>
        public long FireCandidateCapHits => m_fireCandidateCapHits;
        /// <summary>火捕获窗口打开次数（&gt;0 说明主机侧火捕获确实在跑）。</summary>
        public long FireCaptureWindows => m_fireWindows;
        /// <summary>因没有桥 / 会话结束而**明确丢弃**的火格累计数（不静默）。</summary>
        public long FireCellsDropped => m_fireDropped;

        /// <summary>打开一次火 Update 的捕获窗口（主机、有会话时才调用；客户端不走这里）。</summary>
        public void BeginFireWindow()
        {
            m_fireWindowActive = true;
            m_fireWindows++;
        }

        /// <summary>
        /// 记录一次**确定发生**的火写入。<paramref name="dataOnly"/>：true = 内容不变、只有 data 变
        /// （ChangeCell 的 OnBlockModified）；false = 内容变了（OnBlockAdded / OnBlockRemoved）。
        /// 窗口内进窗口；窗口外（邻居回调从 ProcessModifiedCells 里打进来）直接进 carry，照样跨清表存活。
        /// </summary>
        public void NoteFireChange(Point3 cell, bool dataOnly)
        {
            if (cell.Y < 0 || cell.Y > 255) return;
            Dictionary<Point3, bool> table = m_fireWindowActive ? m_fireWindow : m_fireCarry;
            if (table.TryGetValue(cell, out bool existingDataOnly))
            {
                m_fireCellDuplicates++;
                // 同一格既有内容变化又有 data 变化 ⇒ 仍只下发一次，按"内容变了"记账。
                if (!dataOnly) table[cell] = false;
                else if (existingDataOnly) table[cell] = true;
                return;
            }
            table[cell] = dataOnly;
        }

        /// <summary>
        /// 比较候选格在一次调用前后的值（contents+data）。只有真实变化才登记：
        ///   · 内容变了 → 按内容变化记；内容不变、data 变了 → data-only；
        ///   · 完全相同或只有光照变化 → 明确计入 skipped，不入队。
        /// </summary>
        public void NoteFireCandidate(Point3 cell, int before, int after)
        {
            m_fireCandidateCells++;
            if (cell.Y < 0 || cell.Y > 255) return;
            if (before == after)
            {
                m_fireCellSkipped++;
                return;
            }
            int beforeContents = before & 0x3FF;
            int afterContents = after & 0x3FF;
            if (beforeContents != afterContents)
            {
                NoteFireChange(cell, dataOnly: false);
                return;
            }
            int beforeData = (int)((uint)before >> 14);
            int afterData = (int)((uint)after >> 14);
            if (beforeData == afterData)
            {
                // 只有光照位不同：摘要只含 contents+data，不下发。
                m_fireCellSkipped++;
                return;
            }
            NoteFireChange(cell, dataOnly: true);
        }

        /// <summary>
        /// 关闭火窗口：把窗口里的格子并进**跨清表存活**的 carry（去重），由 <see cref="Collect"/> 与引擎
        /// 改动表合并。返回窗口里的格数（0 = 本次火 Update 没有任何真实改动）。
        /// </summary>
        public int EndFireWindow()
        {
            if (!m_fireWindowActive) return 0;
            m_fireWindowActive = false;
            int count = m_fireWindow.Count;
            if (count > 0)
            {
                foreach (KeyValuePair<Point3, bool> pair in m_fireWindow)
                {
                    if (m_fireCarry.TryGetValue(pair.Key, out bool existingDataOnly))
                    {
                        m_fireCellDuplicates++;
                        if (!pair.Value) m_fireCarry[pair.Key] = false;
                        else if (existingDataOnly) m_fireCarry[pair.Key] = true;
                        continue;
                    }
                    m_fireCarry[pair.Key] = pair.Value;
                }
                m_fireWindow.Clear();
            }
            return count;
        }

        /// <summary>候选键因每帧上限没有参与比较：只记账（这些格仍由行为回调覆盖）。</summary>
        public void NoteFireCandidateCapHit(int skippedKeys)
        {
            if (skippedKeys > 0) m_fireCandidateCapHits += skippedKeys;
        }

        /// <summary>当前是否处在一次引信 Update 的捕获窗口内。</summary>
        public bool FuseWindowActive => m_fuseWindowActive;
        /// <summary>已捕获、等下一次 Collect 合并进下发批次的引信格数。</summary>
        public int PendingFuseCount => m_fuseCarry.Count;
        /// <summary>真的进了下发批次（交给桥）的引信格累计数 = fuseCellCaptured。</summary>
        public long FuseCellCaptured => m_fuseCellCaptured;
        /// <summary>候选格读前读后相同（不是真实改动）而跳过的累计数 = fuseCellSkipped。</summary>
        public long FuseCellSkipped => m_fuseCellSkipped;
        /// <summary>同一格被重复报告、合并为一次下发的累计数 = fuseCellDuplicates。</summary>
        public long FuseCellDuplicates => m_fuseCellDuplicates;
        /// <summary>参与 before/after 比较的引信候选格累计数（工作量口径）。</summary>
        public long FuseCandidateCells => m_fuseCandidateCells;
        /// <summary>候选键超过上限而没有比较的键累计数（行为回调仍覆盖这些格）。</summary>
        public long FuseCandidateCapHits => m_fuseCandidateCapHits;
        /// <summary>引信捕获窗口打开次数（&gt;0 说明主机侧引信捕获确实在跑）。</summary>
        public long FuseCaptureWindows => m_fuseWindows;
        /// <summary>因没有桥 / 会话结束而**明确丢弃**的引信格累计数（不静默）。</summary>
        public long FuseCellsDropped => m_fuseDropped;

        /// <summary>打开一次引信 Update 的捕获窗口（主机、有会话时才调用；客户端不走这里）。</summary>
        public void BeginFuseWindow()
        {
            m_fuseWindowActive = true;
            m_fuseWindows++;
        }

        /// <summary>
        /// 记录一次**确定发生**的引信写入。<paramref name="dataOnly"/>：true = 内容不变、只有 data 变
        /// （火药桶引信 tick 就是这一种）；false = 内容也变了。
        /// 窗口内进窗口；窗口外直接进 carry，照样跨清表存活。
        /// </summary>
        public void NoteFuseChange(Point3 cell, bool dataOnly)
        {
            if (cell.Y < 0 || cell.Y > 255) return;
            Dictionary<Point3, bool> table = m_fuseWindowActive ? m_fuseWindow : m_fuseCarry;
            if (table.TryGetValue(cell, out bool existingDataOnly))
            {
                m_fuseCellDuplicates++;
                if (!dataOnly) table[cell] = false;
                else if (existingDataOnly) table[cell] = true;
                return;
            }
            table[cell] = dataOnly;
        }

        /// <summary>
        /// 比较候选格在一次调用前后的值（contents+data）。只有真实变化才登记：
        ///   · 内容变了 → 按内容变化记；
        ///   · 内容不变、data 变了 → data-only（引信 tick 的正常路径）；
        ///   · 完全相同或只有光照变化 → 明确计入 skipped，不入队。
        /// </summary>
        public void NoteFuseCandidate(Point3 cell, int before, int after)
        {
            m_fuseCandidateCells++;
            if (cell.Y < 0 || cell.Y > 255) return;
            if (before == after)
            {
                m_fuseCellSkipped++;
                return;
            }
            int beforeContents = before & 0x3FF;
            int afterContents = after & 0x3FF;
            if (beforeContents != afterContents)
            {
                NoteFuseChange(cell, dataOnly: false);
                return;
            }
            int beforeData = (int)((uint)before >> 14);
            int afterData = (int)((uint)after >> 14);
            if (beforeData == afterData)
            {
                m_fuseCellSkipped++;
                return;
            }
            NoteFuseChange(cell, dataOnly: true);
        }

        /// <summary>
        /// 关闭引信窗口：把窗口里的格子并进**跨清表存活**的 carry（去重），由 <see cref="Collect"/>
        /// 与引擎改动表合并。返回窗口里的格数（0 = 本次引信 Update 没有任何真实改动）。
        /// </summary>
        public int EndFuseWindow()
        {
            if (!m_fuseWindowActive) return 0;
            m_fuseWindowActive = false;
            int count = m_fuseWindow.Count;
            if (count > 0)
            {
                foreach (KeyValuePair<Point3, bool> pair in m_fuseWindow)
                {
                    if (m_fuseCarry.TryGetValue(pair.Key, out bool existingDataOnly))
                    {
                        m_fuseCellDuplicates++;
                        if (!pair.Value) m_fuseCarry[pair.Key] = false;
                        else if (existingDataOnly) m_fuseCarry[pair.Key] = true;
                        continue;
                    }
                    m_fuseCarry[pair.Key] = pair.Value;
                }
                m_fuseWindow.Clear();
            }
            return count;
        }

        // ------------------------------------------------------------------ 世界演化捕获（主机）
        //
        // 为什么需要（实机 2026-10-19：主机水漫过草方后草变泥土，主机显示泥土、客户端仍是草）：
        //   · 草的转换写在 `SubsystemGrassBlockBehavior.OnNeighborBlockChanged`（:76/:82/:86）/
        //     `OnExplosion`（:94）/`Update`（:128）；土 / 常春藤 / 树苗 / 植物 / 仙人掌 /
        //     腐坏 / 地毯 / 落叶 / 季相同样在各自的回调里 ChangeCell；
        //   · 这些邻居回调**恰恰是在** `SubsystemFluidBlockBehavior.SpreadFluid` 末尾的
        //     `ProcessModifiedCells()` 通知循环里被调用的（SubsystemTerrain.cs:71 先清表、
        //     再通知邻居），写入发生在清表**之后**；而 SpreadFluid 一次 Update 跑两遍、
        //     每遍末尾都清表（:312），第二遍的清表就把这些格抹掉 ⇒ 地形 Update（Terrain=100）
        //     再读表已没有它：主机本地变了、客户端永远收不到。
        //   ⇒ 演化外壳在每个会写格的回调 / Update 之后立刻 **rescue**（接进本捕获器的 carry），
        //     与流体 / 火 / 引信同一个汇合点：<see cref="Collect"/> 与引擎改动表合并去重，
        //     每格恰好下发一次。
        //
        // 计数口径：
        //   · EvolutionCellCaptured  ：真的并进下发批次（交给桥）的演化格累计数（每格一次）；
        //   · EvolutionCellDuplicates：同一格被重复报告（多次 rescue / 表里已有）而只下发一次的累计数；
        //   · EvolutionRescueCalls   ：外壳调用 rescue 的次数（0 = 接线没生效）；
        //   · EvolutionCellsDropped  ：因无桥 / 会话结束而**明确丢弃**的演化格累计数（不静默）。

        /// <summary>被演化 rescue 接住、还没并进下发批次的格子（与流体 / 火 / 引信共用 m_rescued，
        /// 这里只记录来源，用于精确计数）。</summary>
        private readonly HashSet<Point3> m_evolutionRescued = new HashSet<Point3>();
        private long m_evolutionRescueCalls;
        private long m_evolutionCellCaptured;
        private long m_evolutionCellDuplicates;
        private long m_evolutionDropped;

        /// <summary>外壳调用 evolution rescue 的累计次数。</summary>
        public long EvolutionRescueCalls => m_evolutionRescueCalls;
        /// <summary>真的进了下发批次的演化格累计数 = evolutionCellCaptured。</summary>
        public long EvolutionCellCaptured => m_evolutionCellCaptured;
        /// <summary>同一格被重复报告、合并为一次下发的累计数 = evolutionCellDuplicates。</summary>
        public long EvolutionCellDuplicates => m_evolutionCellDuplicates;
        /// <summary>因无桥 / 会话结束而明确丢弃的演化格累计数（不静默）。</summary>
        public long EvolutionCellsDropped => m_evolutionDropped;
        /// <summary>已接住、等下一次 Collect 合并的演化格数。</summary>
        public int PendingEvolutionCount => m_evolutionRescued.Count;

        /// <summary>
        /// 在一次演化回调 / Update 之后把引擎改动表里的格子接进 carry（跨清表存活）。
        /// 传入的必须是引擎 <c>m_modifiedCells.Keys</c>（没有待下发格时传 null 也要调用 ——
        /// 这样 evolutionRescueCalls 能证明接线确实在跑）；值到 <see cref="Collect"/> 时才读，
        /// 因此同帧后续改写以最终值为准。返回本次**新接住**的格数。
        /// </summary>
        public int RescueEvolution(IEnumerable<Point3> pendingCells)
        {
            m_evolutionRescueCalls++;
            if (pendingCells == null) return 0;
            int captured = 0;
            foreach (Point3 cell in pendingCells)
            {
                if (cell.Y < 0 || cell.Y > 255) continue;
                if (!m_rescued.Add(cell))
                {
                    // 已经在 carry 里（流体 / 火 / 引信 / 上一个演化回调接住）⇒ 只计数，不重复入队。
                    m_evolutionCellDuplicates++;
                    continue;
                }
                m_rescuedTotal++;
                m_evolutionRescued.Add(cell);
                captured++;
            }
            return captured;
        }

        /// <summary>候选键因每帧上限没有参与比较：只记账（这些格仍由行为回调覆盖）。</summary>
        public void NoteFuseCandidateCapHit(int skippedKeys)
        {
            if (skippedKeys > 0) m_fuseCandidateCapHits += skippedKeys;
        }

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
        /// 合并"引擎改动表 ∪ 接住集合（流体 / 世界演化）∪ 火 carry ∪ 引信 carry"，
        /// 输出**去重后**的待下发清单
        /// （<paramref name="output"/> 会被清空）。返回本次去重掉的格子数；累计值见 <see cref="Duplicates"/>。
        ///
        /// 火格 / 引信格必须并在这里（而不是只依赖引擎改动表）：引擎的流体子系统每 0.25s 会
        /// <c>ProcessModifiedCells()</c> 两遍（SubsystemTerrain.cs:71 先清表再通知邻居），
        /// 火格被邻居回调改成空气的那一刻正好在通知内部、且在清表之后入表 ⇒ 只靠表必然丢。
        /// 引信 tick 是主机外壳在火药桶格子上的 data-only 写入，同样会被这次清表抹掉；
        /// 世界演化的邻居回调（草 → 泥土等）也是从这次通知里打进来、落在清表之后的，
        /// 所以它们都在回调 / Update 之后立刻进 <see cref="RescueEvolution"/> 的 carry。
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
                    bool evolution = m_evolutionRescued.Contains(cell);
                    // 引擎表里已经有它 ⇒ 这次合并只需下发一份（表里那份，值是最新的）。
                    if (m_pending.Contains(cell))
                    {
                        duplicates++;
                        if (evolution) m_evolutionCellDuplicates++;
                        continue;
                    }
                    if (!m_pending.Add(cell)) continue;
                    output.Add(cell);
                    if (evolution) m_evolutionCellCaptured++;
                }
                m_rescued.Clear();
                m_evolutionRescued.Clear();
            }
            if (m_fireCarry.Count > 0)
            {
                foreach (KeyValuePair<Point3, bool> pair in m_fireCarry)
                {
                    // 引擎表 / 接住集合里已经有它 ⇒ 只下发一份（值以读表那一刻的最终值为准）。
                    if (m_pending.Contains(pair.Key)) { m_fireCellDuplicates++; duplicates++; continue; }
                    m_pending.Add(pair.Key);
                    output.Add(pair.Key);
                    m_fireCellCaptured++;
                }
                m_fireCarry.Clear();
            }
            if (m_fuseCarry.Count > 0)
            {
                foreach (KeyValuePair<Point3, bool> pair in m_fuseCarry)
                {
                    // 引擎表 / 接住集合里已经有它 ⇒ 只下发一份（值以读表那一刻的最终值为准）。
                    if (m_pending.Contains(pair.Key)) { m_fuseCellDuplicates++; duplicates++; continue; }
                    m_pending.Add(pair.Key);
                    output.Add(pair.Key);
                    m_fuseCellCaptured++;
                }
                m_fuseCarry.Clear();
            }
            m_pending.Clear();
            m_duplicateTotal += duplicates;
            m_capturedTotal += output.Count;
            return duplicates;
        }

        /// <summary>
        /// 放弃所有接住的格子（没有桥 / 表绑定失败 / 会话结束）：这些格子不会被下发，
        /// 调用方必须**明确计数**（不能假装它们已经发过）。返回丢弃的格数
        /// （接住 + 流体窗口 + 火窗口/carry + 引信窗口/carry；火/引信/世界演化另计入各自 Dropped）。
        /// </summary>
        public int Clear()
        {
            int droppedFire = m_fireWindow.Count + m_fireCarry.Count;
            int droppedFuse = m_fuseWindow.Count + m_fuseCarry.Count;
            int droppedEvolution = m_evolutionRescued.Count;
            int dropped = m_rescued.Count + m_fluidWindow.Count + droppedFire + droppedFuse;
            m_rescued.Clear();
            m_evolutionRescued.Clear();
            m_pending.Clear();
            m_fluidWindow.Clear();
            m_fluidWindowActive = false;
            m_fireWindow.Clear();
            m_fireCarry.Clear();
            m_fireWindowActive = false;
            m_fuseWindow.Clear();
            m_fuseCarry.Clear();
            m_fuseWindowActive = false;
            if (droppedFire > 0) m_fireDropped += droppedFire;
            if (droppedFuse > 0) m_fuseDropped += droppedFuse;
            if (droppedEvolution > 0) m_evolutionDropped += droppedEvolution;
            if (dropped > 0) m_fluidDropped += dropped;
            return dropped;
        }
    }
}
