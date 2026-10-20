using System;
using System.Collections.Generic;
using Engine;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 客户端**熔炉火焰表现**的纯策略（无 Game 依赖；P:\tmp\tsverify 直接编译同一份源码）。
    ///
    /// 引擎事实（Source: Survivalcraft/Game/SubsystemFurnaceBlockBehavior.cs:102-109）：
    ///   · 只有 contents == 65（LitFurnaceBlock）的格子才建火焰：
    ///     <c>new FireParticleSystem(new Vector3(x,y,z) + (0.5,0.2,0.5), 0.15f, 16f)</c>；
    ///   · 火焰是**本地粒子**，不随方块状态同步到达客户端，必须由客户端按同一份权威状态重建。
    /// 主机侧"在烧"的唯一外观就是 contents 65：ComponentFurnace.Update 每帧
    /// <c>ChangeCell(ReplaceContents(cell, m_heatLevel &gt; 0f ? 65 : 64))</c>
    /// （Source: Survivalcraft/Game/ComponentFurnace.cs:Update 末尾），燃料耗尽时 heat 当帧归零。
    /// 因此 contents 65 ⇔ 主机 heat &gt; 0；FURNACE_STATE 路径还额外要求 heat/fireTime 至少一个 &gt; 0，
    /// 这样"heat 已归零、格子外观还没来得及变"的那一拍也会立刻收掉火焰。
    /// </summary>
    public static class FurnaceFlamePolicy
    {
        /// <summary>counters 统一口径（状态行 / 日志 / 自检用同一个 token）。</summary>
        public const string StaleSweepCounter = "furnaceFlameStaleSweep";

        /// <summary>客户端最多同时维护的熔炉火焰数（超出明确拒绝并计数，不无界增长）。</summary>
        public const int MaxTrackedParticles = 512;

        /// <summary>一次越界扫除最多检查的格数（有界，不卡主线程）。</summary>
        public const int MaxSweepChecksPerRun = 64;

        /// <summary>与引擎完全一致的粒子尺寸（Source: SubsystemFurnaceBlockBehavior.AddFire）。</summary>
        public const float ParticleSize = 0.15f;

        /// <summary>与引擎完全一致的可见距离（Source: SubsystemFurnaceBlockBehavior.AddFire）。</summary>
        public const float MaxVisibilityDistance = 16f;

        /// <summary>与引擎完全一致的粒子偏移（Source: SubsystemFurnaceBlockBehavior.AddFire）。</summary>
        public static readonly Vector3 ParticleOffset = new Vector3(0.5f, 0.2f, 0.5f);

        /// <summary>contents 是不是熔炉（64 = 熄灭 / 65 = 燃烧）。</summary>
        public static bool IsFurnaceContents(int contents) =>
            contents == FurnaceStateData.UnlitContents ||
            contents == FurnaceStateData.LitContents;

        /// <summary>
        /// 从完整 block value 取 contents（与 Game.Terrain.ExtractContents 同一位段；
        /// Net/ 不能依赖 Game 程序集，所以在这里按位取）。
        /// </summary>
        public static int ContentsOf(int cellValue) => cellValue & 0x3FF;

        /// <summary>
        /// 这一格此刻是否应该显示火焰。
        ///   · 只有 contents 65 才可能有火焰（引擎 AddFire 的原判据）；
        ///   · 有权威炉况（FURNACE_STATE）时还要求 heat &gt; 0 或 fireTime &gt; 0 ——
        ///     主机把"在烧"定义为 heat &gt; 0（ComponentFurnace.Update），fireTime 是同一份燃料
        ///     的剩余时间，两者任一为正都表示有火；
        ///   · 只有格子值（区块生成 / CellChange 行为通知）时按引擎原判据：contents 65 就建火焰。
        /// 这不是"兜底"：两条路径的输入本来就不同（一条有炉况、一条只有已下发的权威格子），
        /// 这里把各自的判据显式写出来。
        /// </summary>
        public static bool IsBurning(int contents, float heatLevel, float fireTimeRemaining,
            bool hasAuthoritativeState)
        {
            if (contents != FurnaceStateData.LitContents) return false;
            if (!hasAuthoritativeState) return true;
            return heatLevel > 0f || fireTimeRemaining > 0f;
        }
    }

    /// <summary>一次表现动作（诊断 / 状态机 / 测试直接断言）。</summary>
    public enum FurnaceFlameAction
    {
        None = 0,
        Add = 1,
        Keep = 2,
        Remove = 3,
        Rejected = 4
    }

    /// <summary>
    /// 客户端熔炉火焰的**跟踪状态机**（纯逻辑，只记账不碰引擎）：
    ///   · 同一格最多一个火焰：重复的燃烧状态 = Keep（duplicateSuppressed 计数）；
    ///   · 熄灭 / 方块被移除 = Remove；之后不再重建；
    ///   · 有界越界扫除只收"格子已经不是燃烧熔炉"的格（区块读不到 → 跳过，不误删）；
    ///   · 无静态状态：每个 mod 会话一个实例（组合根挂在 TerrainBridge.FurnacePresenter 的表现层里，
    ///     表现层只持有本状态机）。
    /// 粒子对象本身由 Engine/FurnaceReplicaPresentation.cs 按这里的动作创建 / 停止。
    /// </summary>
    public sealed class FurnaceFlameState
    {
        private readonly HashSet<Point3> m_tracked = new HashSet<Point3>();
        private readonly List<Point3> m_sweepKeys = new List<Point3>(64);
        private int m_sweepCursor;

        /// <summary>累计创建的火焰粒子数（counter: furnaceFlameAdded）。</summary>
        public long Added { get; private set; }

        /// <summary>累计收掉的火焰粒子数（counter: furnaceFlameRemoved）。</summary>
        public long Removed { get; private set; }

        /// <summary>重复燃烧状态被抑制的次数（同一格不会叠加第二个粒子）。</summary>
        public long DuplicateSuppressed { get; private set; }

        /// <summary>活动粒子数达到上限而拒绝新建的次数。</summary>
        public long CapHits { get; private set; }

        /// <summary>越界扫除收掉的陈旧粒子数（counter: furnaceFlameStaleSweep）。</summary>
        public long StaleSwept { get; private set; }

        public long SweepRuns { get; private set; }
        public long SweepChecks { get; private set; }
        public long SweepSkippedUnreadable { get; private set; }

        public int TrackedCount => m_tracked.Count;

        public bool IsTracked(Point3 cell) => m_tracked.Contains(cell);

        /// <summary>世界切换 / 重连：清空跟踪（粒子由表现层先收掉）。</summary>
        public void ClearTracked()
        {
            m_tracked.Clear();
            m_sweepCursor = 0;
        }

        /// <summary>
        /// 按"这一格现在是否在烧"推进状态机（调用方已完成权威判定）。
        /// 返回的动作就是表现层要执行的动作。
        /// </summary>
        public FurnaceFlameAction Apply(bool burning, int x, int y, int z)
        {
            var cell = new Point3(x, y, z);
            if (burning)
            {
                if (m_tracked.Contains(cell))
                {
                    // 同一格重复下发（状态轮询 / 同一帧内状态 + 行为通知）→ 不再建第二个粒子。
                    DuplicateSuppressed++;
                    return FurnaceFlameAction.Keep;
                }
                if (m_tracked.Count >= FurnaceFlamePolicy.MaxTrackedParticles)
                {
                    CapHits++;
                    return FurnaceFlameAction.Rejected;
                }
                m_tracked.Add(cell);
                Added++;
                return FurnaceFlameAction.Add;
            }
            if (!m_tracked.Remove(cell)) return FurnaceFlameAction.None;
            Removed++;
            return FurnaceFlameAction.Remove;
        }

        /// <summary>明确移除一格（OnBlockRemoved）：收掉跟踪，之后不再重建。</summary>
        public bool Forget(int x, int y, int z) => m_tracked.Remove(new Point3(x, y, z));

        /// <summary>粒子创建失败时撤销跟踪（下一拍可重试；不计入 Removed）。</summary>
        public bool Untrack(Point3 cell) => m_tracked.Remove(cell);

        /// <summary>
        /// 有界越界扫除（counter: furnaceFlameStaleSweep）：只检查本状态机跟踪的格，
        /// 从不触碰别的粒子系统。readValue 返回 -1（区块未加载 / 坐标非法）→ 跳过，
        /// 不当成"不是熔炉"误删。
        /// </summary>
        public void Sweep(Func<int, int, int, int> readValue, List<Point3> staleCells,
            int maxChecks = FurnaceFlamePolicy.MaxSweepChecksPerRun)
        {
            if (readValue == null) throw new ArgumentNullException(nameof(readValue));
            if (staleCells == null) throw new ArgumentNullException(nameof(staleCells));
            SweepRuns++;
            if (m_tracked.Count == 0)
            {
                m_sweepCursor = 0;
                return;
            }
            int limit = maxChecks <= 0 ? 0 : maxChecks;
            m_sweepKeys.Clear();
            foreach (Point3 cell in m_tracked) m_sweepKeys.Add(cell);
            if (m_sweepKeys.Count == 0) return;
            int start = m_sweepCursor % m_sweepKeys.Count;
            int checks = 0;
            for (int i = 0; i < m_sweepKeys.Count && checks < limit; i++)
            {
                Point3 cell = m_sweepKeys[(start + i) % m_sweepKeys.Count];
                checks++;
                int value = readValue(cell.X, cell.Y, cell.Z);
                if (value < 0)
                {
                    SweepSkippedUnreadable++;
                    continue;
                }
                if (FurnaceFlamePolicy.IsBurning(
                        FurnaceFlamePolicy.ContentsOf(value), 0f, 0f, false))
                {
                    continue;
                }
                if (!m_tracked.Remove(cell)) continue;   // 同一轮已被其它路径收掉
                staleCells.Add(cell);
                StaleSwept++;
            }
            SweepChecks += checks;
            m_sweepCursor = (start + checks) % m_sweepKeys.Count;
        }

        public string Describe() =>
            "furnaceFlameAdded=" + Added + " furnaceFlameRemoved=" + Removed +
            " duplicateSuppressed=" + DuplicateSuppressed + " capHits=" + CapHits +
            " active=" + m_tracked.Count +
            " furnaceFlameStaleSweep=" + StaleSwept + " sweepRuns=" + SweepRuns +
            " sweepChecks=" + SweepChecks + " sweepUnreadable=" + SweepSkippedUnreadable;
    }
}
