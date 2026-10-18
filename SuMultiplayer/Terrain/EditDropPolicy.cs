using System;
using System.Globalization;
using System.Text;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 主机落地一次客户端编辑时，"被销毁方块的掉落"没有 / 不需要生成的原因。
    /// 每一种都进状态行（<see cref="EditDropLedger.Describe"/>），禁止静默跳过。
    /// </summary>
    public enum EditDropSkipReason : byte
    {
        /// <summary>不需要跳过（生成成功，或这次编辑本来就没有销毁方块）。</summary>
        None = 0,
        /// <summary>编辑前该格是空气：没有方块可掉。</summary>
        AirBefore = 1,
        /// <summary>落地后旧方块还在（同 contents 的 data 变化，或行为把格子改回去了）。</summary>
        NotDestroyed = 2,
        /// <summary>引擎自己的落地路径（ChangeCell 期间的行为）已经在该格生成过拾取物 → 不重复生成。</summary>
        EngineSpawned = 3,
        /// <summary>拿不到该客户端手持物的 ToolLevel（化身背包还没同步）→ 无法按引擎口径算掉落。</summary>
        NoToolLevel = 4,
        /// <summary>方块的 GetDropValues 什么都没产出（草 / 花盆 / 水……）。</summary>
        NoDropValues = 5,
        /// <summary>主机掉落端口没有装配（组合根异常）→ 明确计数 + 告警，不假装生成。</summary>
        NoHostDrops = 6,
        /// <summary>引擎 GetDropValues / OnItemHarvested / AddPickable 抛异常。</summary>
        SpawnFailed = 7,
        Count = 8
    }

    public static class EditDropSkipReasonNames
    {
        private static readonly string[] s_names =
        {
            "none", "air-before", "not-destroyed", "engine-spawned", "no-tool-level",
            "no-drop-values", "no-host-drops", "spawn-failed"
        };

        public static string Of(EditDropSkipReason reason)
        {
            int index = (int)reason;
            return index >= 0 && index < s_names.Length ? s_names[index] : "unknown";
        }

        public static int Count => s_names.Length;
    }

    /// <summary>
    /// 主机落地一次编辑请求的结果：客户端请求值 / 旧值 / 权威结果值 + "掉落由谁生成"的事实。
    ///
    /// 纯数据（不依赖引擎），由 <c>Engine/TerrainBridge.TryApplyHostChange</c> 填充，
    /// <see cref="ClientEditAuthority"/> 只读它更新计数，P:\tmp\tsverify 用假世界直接构造。
    /// </summary>
    public struct EditApplyOutcome
    {
        /// <summary>落地后该格的权威 contents / data（客户端据此对齐）。</summary>
        public int ResultContents;
        public int ResultData;
        /// <summary>这次落地真的移除了一个非空气旧方块（dig / 放置挤掉 / 行为替换）。</summary>
        public bool DestroyedOldBlock;
        /// <summary>引擎自己的落地路径已经在该格生成过拾取物（再生成就是双份）。</summary>
        public bool EngineSpawnedPickables;
        /// <summary>本 mod 的主机掉落路径为这次销毁生成了拾取物。</summary>
        public bool HostSpawnedDrops;
        /// <summary>本次生成的拾取物数量（雪块 1..3 个雪球 → 计数 1..3，不是"一次事件"）。</summary>
        public int SpawnedPickables;
        /// <summary>落地后的值与客户端请求值不同（方块行为改写了它，客户端会收到权威 CellChange 收敛）。</summary>
        public bool ResultDiffersFromRequest;
        /// <summary>没有生成掉落时的明确原因（<see cref="EditDropSkipReason.None"/> = 生成成功）。</summary>
        public EditDropSkipReason SkipReason;
    }

    /// <summary>
    /// "改格前后该不该补生成掉落"的**纯策略**（引擎桥与自检假世界都调用这一份，不各写一套）。
    ///
    /// 口径（与引擎 <c>SubsystemTerrain.DestroyCell</c> 一致）：
    ///   · 只有**旧格非空气、且请求把 contents 改成别的**才是"销毁方块"（data-only 改动不是）；
    ///   · 改格后旧 contents 还在 = 没销毁（行为把格子改回去了）→ 不生成；
    ///   · 旧格是空气 = AirBefore；同 contents 的 data 变化 = NotDestroyed。
    /// </summary>
    public static class EditDropPolicy
    {
        /// <summary>这次编辑是否在销毁 / 替换一个非空气旧方块（= 需要提前准备掉落）。</summary>
        public static bool DestroysOldBlock(int oldContents, int requestedContents) =>
            oldContents != 0 && oldContents != requestedContents;

        /// <summary>没有发生销毁时的跳过原因（旧格空气 / 只是 data 变化）。</summary>
        public static EditDropSkipReason NoDestructionReason(int oldContents) =>
            oldContents == 0 ? EditDropSkipReason.AirBefore : EditDropSkipReason.NotDestroyed;
    }

    /// <summary>
    /// 客户端编辑 → 方块销毁掉落的计数账本（实例对象，无静态状态）。
    ///
    /// 计数口径（与需求逐条对应）：
    ///   · <see cref="Spawned"/> = dropFromEditSpawned：主机为客户端编辑补生成的**拾取物个数**
    ///     （雪块掉 1..3 个雪球就 +1..3，不是"编辑次数"）；
    ///   · <see cref="Skipped"/> (dropFromEditSkipped[reason])：没有生成 / 不需要生成的格数，按原因分开；
    ///   · <see cref="Mismatch"/> = dropFromEditMismatch：落地后的权威值与客户端请求值不同
    ///     （方块行为改写，例如放置被行为拒绝 / 水草留水）；掉落仍按旧方块生成，但这是两端预测分歧的证据。
    ///
    /// 热路径只做加减；<see cref="TryTakeSummary"/> 用 10s 限流把增量写进日志，且计数没变化就不写。
    /// </summary>
    public sealed class EditDropLedger
    {
        /// <summary>汇总日志的最小间隔（毫秒）。</summary>
        public const long SummaryIntervalMs = 10000;

        private readonly long[] m_skipped = new long[EditDropSkipReasonNames.Count];
        private readonly long[] m_loggedSkipped = new long[EditDropSkipReasonNames.Count];
        private long m_lastSummaryMs;
        private long m_loggedSpawned;
        private long m_loggedMismatch;
        private long m_totalEdits;

        /// <summary>累计为客户端编辑补生成的拾取物个数（dropFromEditSpawned）。</summary>
        public long Spawned { get; private set; }
        /// <summary>累计两端预测分歧的次数（dropFromEditMismatch）。</summary>
        public long Mismatch { get; private set; }
        /// <summary>参与统计的编辑落地次数（含没有销毁方块的普通放置）。</summary>
        public long Edits => m_totalEdits;

        public long Skipped(EditDropSkipReason reason)
        {
            int index = (int)reason;
            return index >= 0 && index < m_skipped.Length ? m_skipped[index] : 0;
        }

        /// <summary>记录一次已成功落地的编辑。</summary>
        public void Record(in EditApplyOutcome outcome)
        {
            m_totalEdits++;
            if (outcome.ResultDiffersFromRequest) Mismatch++;
            if (outcome.HostSpawnedDrops)
            {
                Spawned += outcome.SpawnedPickables > 0 ? outcome.SpawnedPickables : 1;
                return;
            }
            EditDropSkipReason reason = outcome.SkipReason;
            if (reason == EditDropSkipReason.None)
                reason = outcome.DestroyedOldBlock
                    ? EditDropSkipReason.SpawnFailed
                    : EditDropSkipReason.NotDestroyed;
            int index = (int)reason;
            if (index >= 0 && index < m_skipped.Length) m_skipped[index]++;
        }

        /// <summary>
        /// 距上次汇总 ≥ <see cref="SummaryIntervalMs"/> 且计数有变化 → 返回一行可写日志的摘要。
        /// 热路径（每次编辑）调用，绝大多数情况下直接返回 false。
        /// </summary>
        public bool TryTakeSummary(long nowMs, out string summary)
        {
            summary = null;
            long spawned = Spawned;
            long mismatch = Mismatch;
            bool changed = spawned != m_loggedSpawned || mismatch != m_loggedMismatch;
            if (!changed)
            {
                for (int i = 0; i < m_skipped.Length; i++)
                {
                    if (m_skipped[i] == m_loggedSkipped[i]) continue;
                    changed = true;
                    break;
                }
            }
            if (!changed) return false;
            if (m_lastSummaryMs != 0 && nowMs - m_lastSummaryMs < SummaryIntervalMs) return false;
            m_lastSummaryMs = nowMs;
            long deltaSpawned = spawned - m_loggedSpawned;
            long deltaMismatch = mismatch - m_loggedMismatch;
            m_loggedSpawned = spawned;
            m_loggedMismatch = mismatch;
            var builder = new StringBuilder(192);
            builder.Append("[SuMP] editDrop: dropFromEditSpawned +").Append(deltaSpawned)
                .Append("（累计 ").Append(spawned).Append("）")
                .Append(" dropFromEditMismatch +").Append(deltaMismatch)
                .Append("（累计 ").Append(mismatch).Append("）");
            bool anySkip = false;
            for (int i = 0; i < m_skipped.Length; i++)
            {
                long delta = m_skipped[i] - m_loggedSkipped[i];
                m_loggedSkipped[i] = m_skipped[i];
                if (delta == 0) continue;
                builder.Append(anySkip ? " · " : " · dropFromEditSkipped[")
                    .Append(EditDropSkipReasonNames.Of((EditDropSkipReason)i)).Append("] +")
                    .Append(delta).Append('（').Append(m_skipped[i]).Append('）');
                anySkip = true;
            }
            if (!anySkip) builder.Append(" · dropFromEditSkipped 无变化");
            builder.Append(" · edits=").Append(m_totalEdits);
            summary = builder.ToString();
            return true;
        }

        /// <summary>可审计的一行（状态行 / 自检）：每一级计数都可见。</summary>
        public string Describe()
        {
            var builder = new StringBuilder(192);
            builder.Append("dropFromEditSpawned=").Append(Spawned.ToString(CultureInfo.InvariantCulture));
            builder.Append(" dropFromEditMismatch=").Append(Mismatch.ToString(CultureInfo.InvariantCulture));
            builder.Append(" dropFromEditSkipped[");
            bool any = false;
            for (int i = 0; i < m_skipped.Length; i++)
            {
                if (m_skipped[i] == 0) continue;
                if (any) builder.Append(',');
                any = true;
                builder.Append(EditDropSkipReasonNames.Of((EditDropSkipReason)i));
                builder.Append('=').Append(m_skipped[i].ToString(CultureInfo.InvariantCulture));
            }
            if (!any) builder.Append("none");
            builder.Append(']');
            return builder.ToString();
        }
    }
}
