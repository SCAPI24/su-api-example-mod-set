using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 池运行时的装配点：由 <c>PlayerAiRuntime</c> 在启动时写入。
    /// **抽这层是为了让 <see cref="BtPoolCallTask"/> 能被编辑器编译**
    /// （编辑器复用纯逻辑层，但不带游戏侧胶水 —— 与 <c>LayaRuntimeHost</c> 同一套路）。
    /// </summary>
    public static class PoolRuntimeHost
    {
        /// <summary>取树库（可能为 null：编辑器/自检里没有包目录）。</summary>
        public static Func<TreeLibrary> LibraryProvider { get; set; }

        public static TreeLibrary Library
        {
            get { return LibraryProvider != null ? LibraryProvider() : null; }
        }
    }
    /// <summary>池里的一项（一个可执行的树包）。</summary>
    public sealed class PoolEntry
    {
        public PoolEntry(string key, string entryId = null)
        {
            Key = key;
            EntryId = entryId;
        }

        /// <summary>包名或路径（交给 <c>TreeLibrary.Switch</c>）。</summary>
        public string Key { get; }

        /// <summary>包内入口节点 id；null = 该包的入口。</summary>
        public string EntryId { get; }

        /// <summary>被调度过几次（观测用）。</summary>
        public long RunCount { get; set; }

        public override string ToString()
        {
            return Key + (string.IsNullOrEmpty(EntryId) ? string.Empty : "#" + EntryId);
        }
    }

    /// <summary>调度器要做的事（一次决策）。</summary>
    public enum PoolAction
    {
        /// <summary>什么都不用做（还在跑，或没有可跑的了）。</summary>
        None,

        /// <summary>跑这一项（当前没有活动树，或轮到下一个）。</summary>
        Run,

        /// <summary>进安全态：释放输入 + 等待（没有可跑的项，或全部失败）。</summary>
        SafeIdle
    }

    /// <summary>一次调度的结果。</summary>
    public sealed class PoolDecision
    {
        public PoolAction Action;
        public PoolEntry Entry;

        /// <summary>人类可读的原因（日志/事件环用）。</summary>
        public string Reason;

        /// <summary>是否因为"还没到步骤边界"而没有切换（观测用：这是 S1 的直接证据）。</summary>
        public bool DeferredUntilStepBoundary;

        public string Describe()
        {
            return Action + (Entry != null ? " " + Entry : string.Empty)
                + (string.IsNullOrEmpty(Reason) ? string.Empty : " (" + Reason + ")")
                + (DeferredUntilStepBoundary ? " [deferred: not at a step boundary]" : string.Empty);
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// 行为树池的调度器（plan §4.8）。**纯逻辑、不依赖游戏**，所以能在临时工程里逐条自检。
    ///
    /// 用户的原则："Laya 只管策略级判断 → 策略之间的无缝流程由原行为树包承担；
    /// Laya 的决定不直接产生动作，而是**调整池的执行调度顺序**"。
    ///
    /// 三条无缝性不变量（都在这里落成规则）：
    ///   **S1 只在"步骤边界"切换** —— 活动树还在跑（Running）时绝不打断；要切换必须等它结束。
    ///       实机依据：`TreeLibrary.Switch` 明确用 `ReplaceRoot(migrate:false)`（跨树**不迁移**运行态），
    ///       所以中途换树会把"挖到一半"的那一步直接丢掉。
    ///   **S2 常驻树无残留运行态** —— 切换前由 TreeLibrary 侧 `ResetSubtreeState()`；调度器只负责"何时切"。
    ///   **S3 切换瞬间先释放再接管** —— 由调用方执行 `ReleaseAll()`；调度器在 <see cref="PoolDecision"/>
    ///       里明确给出"要切了"，调用方据此先释放。
    ///
    /// 池顺序的可调部分是**黑板上的几个键**（Laya / 树 / 人都能写，无需新节点）：
    ///   `pool.next`     —— 下一个跑哪一项（一次性，用完即清）
    ///   `pool.sequence` —— 逗号分隔的顺序；当前项跑完后按它取下一个
    ///   `pool.fallback` —— 活动树失败时用哪一项（一次机会，再失败就进安全态）
    /// </summary>
    public sealed class PoolScheduler
    {
        public const string KeyNext = "pool.next";
        public const string KeySequence = "pool.sequence";
        public const string KeyFallback = "pool.fallback";

        private readonly List<PoolEntry> m_pool = new List<PoolEntry>();
        private readonly List<string> m_sequence = new List<string>();

        private PoolEntry m_active;
        private bool m_safeIdle;
        private int m_consecutiveFailures;

        /// <summary>连续失败多少次后进安全态（不无限重试）。</summary>
        public int MaxConsecutiveFailures { get; set; } = 2;

        /// <summary>
        /// **自动前进**（默认 true）：一棵树成功跑完之后，按顺序取池里的下一个。
        ///
        /// 什么时候要关掉它：**相位绑定**（§4.13）接管了"该跑哪棵树"的时候 ——
        /// 否则池会在每次成功后自己往下轮，把绑定刚切过去的那棵树又换掉
        /// （实机踩过：绑定说"世界内跑 demo.laya"，池却在下一个边界按自然顺序切回了 demo.front）。
        /// 关掉之后只有明确的来源能动树：`pool.next`（人写的、或相位绑定写的）、
        /// `pool.fallback`、以及显式的 `sequence`（顺序表仍然生效 —— 那是人明确要的轮换）。
        /// </summary>
        public bool AutoAdvance { get; set; } = true;

        public IReadOnlyList<PoolEntry> Pool
        {
            get { return m_pool; }
        }

        public IReadOnlyList<string> Sequence
        {
            get { return m_sequence; }
        }

        /// <summary>当前活动项（null = 没有在跑）。</summary>
        public PoolEntry Active
        {
            get { return m_active; }
        }

        /// <summary>是否处于安全态（全失败/池空 → 停手等指令）。</summary>
        public bool SafeIdle
        {
            get { return m_safeIdle; }
        }

        public long Decisions { get; private set; }
        public long Switches { get; private set; }
        public long Deferrals { get; private set; }

        /// <summary>装池（包名列表）。</summary>
        public void SetPool(IEnumerable<string> keys)
        {
            m_pool.Clear();
            if (keys != null)
            {
                foreach (string key in keys)
                {
                    if (!string.IsNullOrEmpty(key))
                        m_pool.Add(new PoolEntry(key.Trim()));
                }
            }
            m_active = null;
            m_safeIdle = false;
            m_consecutiveFailures = 0;
        }

        /// <summary>直接设顺序（人/编辑器兜底用；Laya 通常写黑板键）。</summary>
        public void SetSequence(IEnumerable<string> keys)
        {
            m_sequence.Clear();
            if (keys != null)
            {
                foreach (string key in keys)
                {
                    if (!string.IsNullOrEmpty(key))
                        m_sequence.Add(key.Trim());
                }
            }
        }

        public PoolEntry Find(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            for (int i = 0; i < m_pool.Count; i++)
            {
                if (string.Equals(m_pool[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return m_pool[i];
            }
            return null;
        }

        /// <summary>
        /// 从黑板读一遍"可调顺序"的键（Laya / 树 / 人都能写）。
        /// **读完即清 <see cref="KeyNext"/>**：它是"只影响下一个"的一次性意图。
        /// </summary>
        public void ApplyBlackboard(AiBlackboard blackboard)
        {
            if (blackboard == null)
                return;

            string sequence;
            if (blackboard.TryGet<string>(KeySequence, out sequence) && !string.IsNullOrEmpty(sequence))
            {
                var keys = new List<string>();
                string[] parts = sequence.Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    string key = parts[i].Trim();
                    if (key.Length > 0)
                        keys.Add(key);
                }
                if (keys.Count > 0)
                    SetSequence(keys);
            }
        }

        /// <summary>取一次性的"下一个"（读后清除）。</summary>
        public string TakeNext(AiBlackboard blackboard)
        {
            string next = PeekNext(blackboard);
            if (next != null && blackboard != null)
                blackboard.Set(new AiBlackboardKey<string>(KeyNext), string.Empty);
            return next;
        }

        /// <summary>**看一眼**"下一个"是谁，但不消费它（只读诊断用）。</summary>
        public string PeekNext(AiBlackboard blackboard)
        {
            if (blackboard == null)
                return null;
            string next;
            if (!blackboard.TryGet<string>(KeyNext, out next) || string.IsNullOrEmpty(next))
                return null;
            return next.Trim();
        }

        /// <summary>
        /// 调度一次。参数是**当前活动树的真实状态**，由调用方（游戏侧）如实提供：
        ///   <paramref name="hasActiveTree"/> —— 现在有没有树在跑
        ///   <paramref name="running"/>       —— 有树在跑 **且没跑完**（还在某个步骤里）
        ///   <paramref name="lastSucceeded"/> —— 活动树刚刚跑完且成功
        ///   <paramref name="lastFailed"/>    —— 活动树刚刚失败
        /// 三者互斥；都没给表示"有树但状态未知"（这时**不动**，等价于继续跑）。
        ///
        /// ⚠️ **本方法会改状态**（计数、活动项、消费 `pool.next`）—— 只读诊断请用 <see cref="Peek"/>。
        /// </summary>
        public PoolDecision Decide(AiBlackboard blackboard, bool hasActiveTree, bool running,
            bool lastSucceeded, bool lastFailed)
        {
            return DecideCore(blackboard, hasActiveTree, running, lastSucceeded, lastFailed, true);
        }

        /// <summary>
        /// **只读地"演一遍"**：算出会做什么决定，但不计数、不改活动项、**不消费 `pool.next`**。
        ///
        /// 为什么必须有它：`ai.pool.status` 以前直接调 `Decide`，于是"看一眼状态"这件事
        /// 会（a）把 switch 计数加上去、（b）**把别人写的 `pool.next` 吃掉** ——
        /// 实机踩过：相位绑定写的 `pool.next=demo.front` 被状态查询消费掉，
        /// 于是调度器以为切过了、树却还是旧的，两边状态分叉。
        /// </summary>
        public PoolDecision Peek(AiBlackboard blackboard, bool hasActiveTree, bool running,
            bool lastSucceeded, bool lastFailed)
        {
            return DecideCore(blackboard, hasActiveTree, running, lastSucceeded, lastFailed, false);
        }

        private PoolDecision DecideCore(AiBlackboard blackboard, bool hasActiveTree, bool running,
            bool lastSucceeded, bool lastFailed, bool commit)
        {
            if (commit)
                Decisions++;

            if (m_pool.Count == 0)
            {
                if (commit)
                    m_safeIdle = true;
                return new PoolDecision
                {
                    Action = PoolAction.SafeIdle,
                    Reason = "the pool is empty"
                };
            }

            // S1：活动树还在跑 → 绝不打断（这就是"无缝流程"的全部秘密）
            if (hasActiveTree && running)
            {
                if (commit)
                    Deferrals++;
                return new PoolDecision
                {
                    Action = PoolAction.None,
                    Entry = m_active,
                    Reason = "the active tree is still running",
                    DeferredUntilStepBoundary = true
                };
            }

            // 到了步骤边界：先看"一次性下一个"（Laya / 相位绑定写的）
            string next = commit ? TakeNext(blackboard) : PeekNext(blackboard);
            if (!string.IsNullOrEmpty(next))
            {
                PoolEntry entry = Find(next);
                if (entry == null)
                {
                    // 写了池里没有的名字：**明确拒绝并说明**，不猜、也不静默忽略
                    return new PoolDecision
                    {
                        Action = PoolAction.None,
                        Reason = "pool.next='" + next + "' is not in the pool (known: " + DescribePool() + ")"
                    };
                }
                return Switch(entry, "pool.next", commit);
            }

            // 失败 → 先看显式 fallback（"它失败就用这个"是人明确要的），
            // 否则：自动前进开着才轮换/进安全态；关着（相位绑定管事）就**原地不动**。
            if (lastFailed)
            {
                if (commit)
                    m_consecutiveFailures++;

                string fallback;
                if (blackboard != null
                    && blackboard.TryGet<string>(KeyFallback, out fallback)
                    && !string.IsNullOrEmpty(fallback))
                {
                    PoolEntry explicitFallback = Find(fallback.Trim());
                    if (explicitFallback != null)
                        return Switch(explicitFallback, "pool.fallback after a failure", commit);
                }

                // ⚠️ 相位绑定在管事时，**失败也不许自己换树**：这棵树是这个相位的那一棵，
                //    它自己内部有兜底阶梯（`demo.laya` 的连败阶梯），池跟着轮换只会把它换掉。
                //    实机踩过：世界里 demo.laya 动作失败 → 池按自然顺序切回了 demo.front（界面树）。
                if (!AutoAdvance)
                {
                    return new PoolDecision
                    {
                        Action = PoolAction.None,
                        Entry = m_active,
                        Reason = "auto-advance is off (a phase binding owns tree selection):"
                            + " a failure does not rotate the pool"
                    };
                }

                if (m_consecutiveFailures >= Math.Max(1, MaxConsecutiveFailures))
                {
                    if (commit)
                    {
                        m_safeIdle = true;
                        m_active = null;
                    }
                    return new PoolDecision
                    {
                        Action = PoolAction.SafeIdle,
                        Reason = "failed " + m_consecutiveFailures + " times in a row"
                    };
                }

                PoolEntry alternate = NextInSequenceAfterActive();
                if (alternate != null)
                    return Switch(alternate, "next in sequence after a failure", commit);
            }

            // 成功（或首次启动）→ 按顺序取下一个
            if (!hasActiveTree || lastSucceeded)
            {
                if (commit && lastSucceeded)
                    m_consecutiveFailures = 0;

                // 自动前进关掉时（相位绑定在管事）：
                //   · 已经有活动项 → **原地不动**（不换树、也不进安全态）；
                //   · 显式给了 sequence → 仍然按它轮换（那是人明确要的）；
                //   · 还没有活动项 → 仍然要从池里起一棵（否则永远起不来）。
                PoolEntry entry = null;
                if (m_sequence.Count > 0)
                    entry = NextInSequenceAfterActive();
                if (entry == null && m_active != null && !AutoAdvance)
                {
                    return new PoolDecision
                    {
                        Action = PoolAction.None,
                        Entry = m_active,
                        Reason = "auto-advance is off (a phase binding owns tree selection)"
                    };
                }
                if (entry == null)
                    entry = AutoAdvance ? NextInSequenceAfterActive() : null;
                if (entry == null && m_active == null && m_pool.Count > 0)
                    entry = m_pool[0];   // 还没有活动项：从池的第一项开始
                if (entry == null)
                {
                    if (commit)
                        m_safeIdle = true;
                    return new PoolDecision
                    {
                        Action = PoolAction.SafeIdle,
                        Reason = "nothing left in the sequence"
                    };
                }
                return Switch(entry, lastSucceeded ? "sequence after a success" : "first run", commit);
            }

            return new PoolDecision
            {
                Action = PoolAction.None,
                Entry = m_active,
                Reason = "no state change"
            };
        }

        private PoolEntry NextInSequenceAfterActive()
        {
            if (m_pool.Count == 0)
                return null;

            // 顺序表为空 → 用池的自然顺序
            List<string> order = m_sequence.Count > 0 ? m_sequence : null;

            if (m_active == null)
            {
                if (order != null)
                    return Find(order[0]);
                return m_pool[0];
            }

            if (order == null)
            {
                int index = m_pool.IndexOf(m_active);
                if (index < 0)
                    return m_pool[0];
                return m_pool[(index + 1) % m_pool.Count];
            }

            for (int i = 0; i < order.Count; i++)
            {
                if (string.Equals(order[i], m_active.Key, StringComparison.OrdinalIgnoreCase))
                    return Find(order[(i + 1) % order.Count]);
            }
            return Find(order[0]);
        }

        private PoolDecision Switch(PoolEntry entry, string reason, bool commit)
        {
            if (commit)
            {
                m_active = entry;
                m_safeIdle = false;
                entry.RunCount++;
                Switches++;
            }
            return new PoolDecision
            {
                Action = PoolAction.Run,
                Entry = entry,
                Reason = reason
            };
        }

        public string DescribePool()
        {
            var parts = new List<string>();
            for (int i = 0; i < m_pool.Count; i++)
                parts.Add(m_pool[i].ToString());
            return parts.Count == 0 ? "<empty>" : string.Join(",", parts.ToArray());
        }

        public Dictionary<string, object> Describe()
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["pool"] = DescribePool(),
                ["sequence"] = m_sequence.Count > 0 ? string.Join(",", m_sequence.ToArray()) : "<natural order>",
                ["autoAdvance"] = AutoAdvance,
                ["active"] = m_active != null ? m_active.ToString() : null,
                ["safeIdle"] = m_safeIdle,
                ["consecutiveFailures"] = m_consecutiveFailures,
                ["decisions"] = Decisions,
                ["switches"] = Switches,
                ["deferrals"] = Deferrals
            };
            return info;
        }

        public override string ToString()
        {
            return "pool(" + DescribePool() + " active=" + (m_active != null ? m_active.ToString() : "-")
                + (m_safeIdle ? " SAFE-IDLE" : string.Empty) + ")";
        }
    }
}
