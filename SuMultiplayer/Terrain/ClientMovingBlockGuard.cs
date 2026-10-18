using System;
using System.Collections.Generic;
using Engine;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 一个移动组的只读几何快照（客户端"移动组 base 写格"防护窗口用）。
    /// 刻意不引用 Game 的 IMovingBlockSet：本文件要与 <c>P:\tmp\tsverify</c> 直接编译（不依赖 Game），
    /// 引擎侧适配器（Engine/ClientReplicaSubsystems.cs 的 SuSubsystemMovingBlocks）负责把
    /// set.Position + 方块偏移范围填进来。
    /// </summary>
    public readonly struct ClientMovingBlockSetRef
    {
        public ClientMovingBlockSetRef(string source, Vector3 position, Point3 minOffset,
            Point3 maxOffset)
        {
            if (string.IsNullOrEmpty(source))
                throw new ArgumentException("source 不能为空", nameof(source));
            Source = source;
            Position = position;
            MinOffset = minOffset;
            MaxOffset = maxOffset;
        }

        /// <summary>写格来源口径：<see cref="ClientMovingBlockWriteGuard.PistonSource"/> 等。</summary>
        public string Source { get; }

        public Vector3 Position { get; }

        public Point3 MinOffset { get; }

        public Point3 MaxOffset { get; }
    }

    /// <summary>
    /// 防护窗口的宿主。生产实现 = <c>SuSubsystemTerrain</c>（读格 / 恢复原值 / 登记计数）；
    /// 自检用假世界（记录哪一格被写了几次、恢复是否成功）。
    /// </summary>
    public interface IClientMovingBlockWatchHost
    {
        /// <summary>读当前格值（y 越界返回 0，与 ReplicaGates.ReadCellValue 同口径）。</summary>
        int ReadCell(Point3 cell);

        /// <summary>
        /// 把格恢复成窗口开始前的值，并把该格移出本地编辑捕获（否则仍会被当成玩家编辑提交）。
        /// 返回 false = 恢复失败（调用方计入 clientShellBaseWriteNotReverted，明确失败而不是静默）。
        /// </summary>
        bool RestoreCell(Point3 cell, int value);

        /// <summary>登记一次"base 写格被拦下"（生产实现累计 clientShellBaseWriteAttempts 等）。</summary>
        void NoteBaseWriteAttempt(string source, int count);
    }

    /// <summary>
    /// 客户端"移动组 base 写格"防护窗口（**item 3 的兜底**，纯托管、无 Game 依赖、无静态状态）。
    ///
    /// 为什么需要：引擎 <c>SubsystemPistonBlockBehavior.MovingBlocksCollidedWithTerrain</c> /
    /// <c>SubsystemCollapsingBlockBehavior.MovingBlocksCollidedWithTerrain</c> 直接调用
    /// <c>SubsystemTerrain.DestroyCell</c> / <c>ChangeCell</c>，而这两个方法**不是虚方法**，
    /// 外壳无法 override 拦下。正常路径由 Piston / Collapsing 外壳把这两个私有委托从
    /// <c>SubsystemMovingBlocks</c> 的事件上摘掉（见 <see cref="ClientReplicaEventDetach"/>）；
    /// 万一摘除失败 / 未来引擎改事件名，本窗口在 <c>SubsystemMovingBlocks.Update</c> 前后对
    /// "移动组会碰到的格子"做一次快照，把窗口内任何变化恢复原值 + 移出本地编辑捕获，并计数。
    /// 这样即使 base 回调仍在，客户端也不会把写格提交给主机。
    ///
    /// 窗口范围：每个被监视移动组（Id=="Piston" / "CollapsingBlock"）当前 Position 下的
    /// 方块偏移包围盒，每轴外扩 <see cref="Expand"/> 格（引擎碰撞回调用的是"移动后的位置"，
    /// 外扩覆盖这一帧的位移与取整；piston 到点瞬移发生在距离 &lt; speed*dt 时，位置已贴住目标）。
    /// </summary>
    public sealed class ClientMovingBlockWriteGuard
    {
        public const string PistonSource = "piston";
        public const string CollapsingSource = "collapsing";

        /// <summary>碰撞回调用的是"移动后的位置"，快照按窗口开始时的位置取，外扩 2 格覆盖这一帧的位移。</summary>
        private const int Expand = 2;

        /// <summary>单个移动组允许快照的格子上限。引擎数据（活塞 ≤17 块 / 塌落一列）远低于此；超限明确计数。</summary>
        private const int MaxCellsPerSet = 20000;

        private readonly Dictionary<Point3, GuardCell> m_cells = new Dictionary<Point3, GuardCell>();

        private readonly struct GuardCell
        {
            public GuardCell(int value, string source)
            {
                Value = value;
                Source = source;
            }

            public int Value { get; }
            public string Source { get; }
        }

        /// <summary>本次窗口快照的格子数（Begin 后 / End 前可读）。</summary>
        public int WatchedCells => m_cells.Count;

        /// <summary>累计快照格数（审计用）。</summary>
        public long TotalWatched { get; private set; }

        /// <summary>本次窗口内被恢复的格数。</summary>
        public int LastReverted { get; private set; }

        /// <summary>本次窗口内恢复失败（格仍被改 / 读不到）的数量。</summary>
        public int LastRestoreFailures { get; private set; }

        /// <summary>本次窗口内活塞来源的写格次数。</summary>
        public int LastPistonAttempts { get; private set; }

        /// <summary>本次窗口内塌落来源的写格次数。</summary>
        public int LastCollapsingAttempts { get; private set; }

        /// <summary>本次 Begin 因几何异常被跳过的移动组数（不静默：调用方必须写日志）。</summary>
        public int LastSkippedSets { get; private set; }

        /// <summary>快照所有被监视移动组会碰到的格子；返回快照格数。</summary>
        public int Begin(IReadOnlyList<ClientMovingBlockSetRef> sets,
            IClientMovingBlockWatchHost host)
        {
            m_cells.Clear();
            LastRestoreFailures = 0;
            LastReverted = 0;
            LastPistonAttempts = 0;
            LastCollapsingAttempts = 0;
            LastSkippedSets = 0;
            if (sets == null || host == null || sets.Count == 0) return 0;
            for (int s = 0; s < sets.Count; s++)
            {
                ClientMovingBlockSetRef set = sets[s];
                if (string.IsNullOrEmpty(set.Source)) continue;
                long x0 = (long)MathUtils.Floor(set.Position.X + set.MinOffset.X) - Expand;
                long x1 = (long)MathUtils.Ceiling(set.Position.X + set.MaxOffset.X) + Expand;
                long y0 = (long)MathUtils.Floor(set.Position.Y + set.MinOffset.Y) - Expand;
                long y1 = (long)MathUtils.Ceiling(set.Position.Y + set.MaxOffset.Y) + Expand;
                long z0 = (long)MathUtils.Floor(set.Position.Z + set.MinOffset.Z) - Expand;
                long z1 = (long)MathUtils.Ceiling(set.Position.Z + set.MaxOffset.Z) + Expand;
                if (y0 < 0) y0 = 0;
                if (y1 > 255) y1 = 255;
                if (x1 < x0 || y1 < y0 || z1 < z0)
                {
                    LastSkippedSets++;
                    continue;
                }
                long volume = (x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
                if (volume <= 0 || volume > MaxCellsPerSet)
                {
                    // 几何异常 / 超出上限：不猜、不静默 —— 调用方按 LastSkippedSets 明确告警。
                    LastSkippedSets++;
                    continue;
                }
                for (long x = x0; x <= x1; x++)
                {
                    for (long y = y0; y <= y1; y++)
                    {
                        for (long z = z0; z <= z1; z++)
                        {
                            var cell = new Point3((int)x, (int)y, (int)z);
                            if (m_cells.ContainsKey(cell)) continue;
                            m_cells.Add(cell, new GuardCell(host.ReadCell(cell), set.Source));
                        }
                    }
                }
            }
            TotalWatched += m_cells.Count;
            return m_cells.Count;
        }

        /// <summary>
        /// 关闭窗口：把窗口内**真的变了**的格恢复原值并移出本地编辑捕获，按来源计数。
        /// 返回恢复成功的格数（恢复失败的数量见 <see cref="LastRestoreFailures"/>）。
        /// </summary>
        public int End(IClientMovingBlockWatchHost host)
        {
            LastReverted = 0;
            LastRestoreFailures = 0;
            LastPistonAttempts = 0;
            LastCollapsingAttempts = 0;
            if (host == null || m_cells.Count == 0) return 0;
            foreach (KeyValuePair<Point3, GuardCell> pair in m_cells)
            {
                int now = host.ReadCell(pair.Key);
                if (now == pair.Value.Value) continue;
                if (host.RestoreCell(pair.Key, pair.Value.Value)) LastReverted++;
                else LastRestoreFailures++;
                if (string.Equals(pair.Value.Source, PistonSource, StringComparison.Ordinal))
                    LastPistonAttempts++;
                else
                    LastCollapsingAttempts++;
            }
            if (LastPistonAttempts > 0) host.NoteBaseWriteAttempt(PistonSource, LastPistonAttempts);
            if (LastCollapsingAttempts > 0)
                host.NoteBaseWriteAttempt(CollapsingSource, LastCollapsingAttempts);
            m_cells.Clear();
            return LastReverted;
        }
    }
}
