using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 相位交接的**一步**（plan §4.13）。纯数据 + 纯判定，运行时照着执行，自检照着钉。
    /// </summary>
    public struct PhaseStep
    {
        /// <summary>真的跨了边界（首次观测不算交接，见 <see cref="PhaseHandover.Plan"/>）。</summary>
        public bool Changed;

        public string From;
        public string To;

        /// <summary>释放旧相位的输入（按住的键不许漏进新相位）。</summary>
        public bool ReleaseInput;

        /// <summary>取消在途判定 + 清答案缓存（跨相位的答案**绝不能**落地）。</summary>
        public bool ClearLaya;

        /// <summary>过渡态：树一拍都不推进（§4.13「过渡态只 obs.waitFor」）。</summary>
        public bool GateTree;

        /// <summary>事件日志那一行（人复盘时看的就是它）。</summary>
        public string Event;

        public string Describe()
        {
            if (!Changed)
                return "no change (" + (To ?? "?") + ")";
            return Event ?? (From + " -> " + To);
        }
    }

    /// <summary>
    /// 进出世界的交接协议（plan §4.13）。
    ///
    /// **为什么边界必须做事**：世界切换的那一瞬间，三样东西在"跨越边界"——
    ///   · **物理输入**：世界里按住的 W/A/S/D 或鼠标键，到主菜单还按着 → 菜单自己乱走；
    ///   · **在途判定**：世界外问的那道题（`front_goal`）1 秒后才回来，
    ///     落到世界内的树上 → 树拿着"主菜单该怎么点"的答案去驱动角色（plan G8）；
    ///   · **树的阶段**：世界加载中（还没角色）若继续跑世界树，节点只会连续失败，
    ///     把兜底分支和失败计数全部打脏。
    ///
    /// **首次观测不交接**：进程刚起来时 `from` 为空，这一刻没有"旧相位"的任何残留，
    /// 交接动作全是白做（还会在日志里留下一条假的边界事件）。所以先记相位、不动作。
    /// </summary>
    public static class PhaseHandover
    {
        /// <summary>由"旧相位 → 新相位"给出这一步要做什么。</summary>
        public static PhaseStep Plan(string from, string to)
        {
            var step = new PhaseStep();
            step.From = from;
            step.To = string.IsNullOrEmpty(to) ? PhaseNames.Front : to;

            // 首次观测：只记相位。没变：什么都不做。
            if (string.IsNullOrEmpty(from) || string.Equals(from, step.To, StringComparison.Ordinal))
                return step;

            step.Changed = true;
            step.ReleaseInput = true;
            step.ClearLaya = true;

            // 过渡态闸门：世界已加载、角色还没就绪。只有这一段"什么都不推进"。
            step.GateTree = PhaseNames.IsLoading(step.To);

            string extra = step.GateTree ? ", tree gated" : string.Empty;
            if (!PhaseNames.IsKnown(step.To))
                extra += extra.Length == 0 ? ", unknown phase (conservative)" : ", unknown phase";
            step.Event = from + " -> " + step.To + " (input released, laya cleared" + extra + ")";
            return step;
        }
    }

    /// <summary>
    /// 相位跟踪 + **去抖**（plan §4.13 的 G15）：连续 N 帧观测到同一新相位才算真的切换。
    ///
    /// 为什么必须去抖：`GameManager.Project` 在**换世界的过程中会短暂为 null**（平板实测：
    /// 进世界时出现 `world -> front -> world`，中间隔了 655 ms），单帧抖动更是常态。
    /// 不去抖的话每次抖动都要"释放输入 + 清 Laya 缓存"——**释放输入会清掉引擎输入数组里的按下沿**，
    /// 人在世界里按住 W 走路时被判成一次相位切换，角色就会一顿一顿地抽搐。
    ///
    /// 去抖**不改变交接内容**（那仍然只有 <see cref="PhaseHandover.Plan"/> 一处定义），
    /// 只改变"什么时候承认它变了"。确认帧数取小值（默认 5 帧 ≈ 83 ms）：真实边界（世界加载几秒）
    /// 一秒都不会被拖慢，而单帧抖动会被吃掉。
    /// </summary>
    public sealed class PhaseTracker
    {
        private readonly int m_confirmFrames;

        private string m_phase = PhaseNames.Front;
        private bool m_known;
        private string m_candidate;
        private int m_candidateFrames;

        public PhaseTracker(int confirmFrames)
        {
            m_confirmFrames = confirmFrames < 1 ? 1 : confirmFrames;
        }

        /// <summary>已确认的相位。</summary>
        public string Phase
        {
            get { return m_phase; }
        }

        /// <summary>观测过至少一帧没有（没观测过时 <see cref="Phase"/> 只是初值）。</summary>
        public bool Known
        {
            get { return m_known; }
        }

        /// <summary>**已确认**的切换次数（抖动不算）。</summary>
        public long Changes { get; private set; }

        /// <summary>正在等的候选相位（没在等就是 null）—— `ai.status` 看不出它，日志才看得出"在等什么"。</summary>
        public string Candidate
        {
            get { return m_candidate; }
        }

        public int CandidateFrames
        {
            get { return m_candidateFrames; }
        }

        /// <summary>
        /// 喂一帧观测，返回**这一步要不要交接**。
        /// `Changed=false` 有三种含义，用 `From`/`To` 区分：首次观测（`From == null`）、
        /// 没变（`From == To`）、**还在等确认**（`From == 当前相位` 且 `Candidate != null`）。
        /// </summary>
        public PhaseStep Observe(string observed)
        {
            if (string.IsNullOrEmpty(observed))
                observed = PhaseNames.Front;

            // 首次观测：只记相位，不交接（此刻没有"旧相位"的任何残留）。
            if (!m_known)
            {
                m_known = true;
                m_phase = observed;
                return PhaseHandover.Plan(null, observed);
            }

            if (string.Equals(observed, m_phase, StringComparison.Ordinal))
            {
                // 回到当前相位：候选作废（这就是"抖动被吃掉"的那一步）
                m_candidate = null;
                m_candidateFrames = 0;
                return PhaseHandover.Plan(m_phase, m_phase);
            }

            // 新候选从 0 开始数；同一候选连续累计（`CandidateFrames` = 已经连续看了几帧）
            if (!string.Equals(observed, m_candidate, StringComparison.Ordinal))
                m_candidateFrames = 0;
            m_candidate = observed;
            m_candidateFrames++;

            if (m_candidateFrames < m_confirmFrames)
                return PhaseHandover.Plan(m_phase, m_phase);

            PhaseStep step = PhaseHandover.Plan(m_phase, observed);
            m_phase = observed;
            m_candidate = null;
            m_candidateFrames = 0;
            Changes++;
            return step;
        }
    }
}
