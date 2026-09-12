using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 状态机内核：注册状态与转移表，按 tick 统一裁决"下一步去哪个状态"，并调用状态行为。
    ///
    /// 设计要点（对应"能够执行复杂状态切换"）：
    ///   1. **转移只在 tick 边界发生**：<see cref="Raise"/> 只入队；守卫判定与状态切换都在
    ///      <see cref="Tick"/> 内完成，单帧转移次数有上限（PlayerAiConfig.MaxTransitionsPerTick），
    ///      不会出现"一帧里来回横跳"或"半个状态"。
    ///   2. **多条候选按优先级竞争**：同一触发/同一条件可以写多条转移，用 Priority + Guard 分级，
    ///      配合 MinDwellSeconds（最短驻留）与 CooldownSeconds（本条冷却）抑制抖动。
    ///   3. **全局面转移**：From = <see cref="AnyState"/>，例如"任何状态下血量过低就撤"。
    ///   4. **条件式转移**：Trigger 为 null 时每帧只按守卫判定一次，用于持续条件（如超时、目标丢失）。
    ///   5. **历史栈**：<see cref="PushState"/> / <see cref="PopState"/> 支持"打断—恢复"；
    ///      转移目标还可以写 <c>&lt;previous&gt;</c>（回到上一个状态）与 <c>&lt;pop&gt;</c>（弹出返回地址）。
    ///   6. **异常隔离**：状态抛异常不会让游戏崩溃 —— 记录日志并回退到安全状态，或把状态机置为
    ///      faulted 停止工作（由 <see cref="Reset"/> 恢复）。
    ///   7. **可观测**：<see cref="Snapshot"/> 提供一致视图，<see cref="DescribeGraph"/> 打印状态图。
    /// </summary>
    public sealed class AiStateMachine
    {
        /// <summary>From 写这个值表示"任意状态"的全局面转移。</summary>
        public const string AnyState = "*";

        /// <summary>目标写这个值表示"回到上一个状态"。</summary>
        public const string PreviousStateTarget = "<previous>";

        /// <summary>目标写这个值表示"弹出历史栈里的返回地址"。</summary>
        public const string PopStateTarget = "<pop>";

        private const int MaxPendingTriggers = 32;
        private const int MaxHistoryDepth = 16;

        private readonly string m_id;
        private readonly Dictionary<string, AiState> m_states =
            new Dictionary<string, AiState>(StringComparer.Ordinal);
        private readonly List<AiTransition> m_transitions = new List<AiTransition>();
        private readonly List<AiTransition> m_globalTransitions = new List<AiTransition>();
        private readonly Queue<string> m_pendingTriggers = new Queue<string>();
        private readonly List<string> m_scratchTriggers = new List<string>();
        private readonly List<string> m_history = new List<string>();
        private readonly List<string> m_transitionLog = new List<string>();

        private IAiTreeHost m_host;
        private AiStateContext m_tickContext;
        private AiState m_current;
        private AiState m_previous;
        private string m_initialStateId;
        private float m_timeInState;
        private float m_timeInMachine;
        private float m_clock;
        private int m_transitionCount;
        private bool m_started;
        private bool m_faulted;

        public AiStateMachine(string id)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("State machine id must not be empty.", nameof(id));
            m_id = id;
        }

        public string Id
        {
            get { return m_id; }
        }

        /// <summary>所属角色（子状态机会与父机绑定同一个角色）。</summary>
        /// <summary>本状态机服务的 AI 宿主（未绑定时为 null）。</summary>
        public IAiTreeHost Host
        {
            get { return m_host; }
        }

        public bool IsStarted
        {
            get { return m_started; }
        }

        /// <summary>true = 已停止工作（状态异常无法恢复）；需要 <see cref="Reset"/> 才能继续。</summary>
        public bool IsFaulted
        {
            get { return m_faulted; }
        }

        public AiState Current
        {
            get { return m_current; }
        }

        public string CurrentStateId
        {
            get { return m_current != null ? m_current.Id : null; }
        }

        public string PreviousStateId
        {
            get { return m_previous != null ? m_previous.Id : null; }
        }

        public float TimeInState
        {
            get { return m_timeInState; }
        }

        public float TimeInMachine
        {
            get { return m_timeInMachine; }
        }

        public int TransitionCount
        {
            get { return m_transitionCount; }
        }

        public int PendingTriggerCount
        {
            get { return m_pendingTriggers.Count; }
        }

        /// <summary>状态栈（返回地址），最后一项是最内层。</summary>
        public IReadOnlyList<string> History
        {
            get { return m_history; }
        }

        public IEnumerable<string> StateIds
        {
            get { return m_states.Keys; }
        }

        // ------------------------------------------------------------ 配置

        public AiStateMachine Bind(IAiTreeHost host)
        {
            m_host = host;
            return this;
        }

        public AiStateMachine Register(AiState state)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (m_states.ContainsKey(state.Id))
                throw new InvalidOperationException(
                    "State '" + state.Id + "' is already registered in machine '" + m_id + "'.");
            m_states.Add(state.Id, state);
            return this;
        }

        public AiStateMachine Register(params AiState[] states)
        {
            if (states != null)
            {
                for (int i = 0; i < states.Length; i++)
                    Register(states[i]);
            }
            return this;
        }

        public AiStateMachine Add(AiTransition transition)
        {
            if (transition == null)
                throw new ArgumentNullException(nameof(transition));
            m_transitions.Add(transition);
            if (string.Equals(transition.From, AnyState, StringComparison.Ordinal))
                m_globalTransitions.Add(transition);
            return this;
        }

        /// <summary>快捷注册：<c>Add("Idle", AiTriggers.Start, "Follow")</c>。</summary>
        public AiStateMachine Add(string from, string trigger, string to)
        {
            return Add(new AiTransition(from, trigger, to));
        }

        /// <summary>带守卫/优先级/说明的转移注册。</summary>
        public AiStateMachine Connect(
            string from, string trigger, string to,
            Func<AiStateContext, bool> guard = null,
            int priority = 0, string description = null)
        {
            var transition = new AiTransition(from, trigger, to)
            {
                Guard = guard,
                Priority = priority,
                Description = description
            };
            return Add(transition);
        }

        public void SetInitialState(string stateId)
        {
            if (!HasState(stateId))
                throw new InvalidOperationException(
                    "Initial state '" + stateId + "' is not registered in machine '" + m_id + "'.");
            m_initialStateId = stateId;
        }

        public bool HasState(string stateId)
        {
            return !string.IsNullOrEmpty(stateId) && m_states.ContainsKey(stateId);
        }

        public AiState GetState(string stateId)
        {
            AiState state;
            return stateId != null && m_states.TryGetValue(stateId, out state) ? state : null;
        }

        /// <summary>启动前自检：返回问题列表（空 = 没问题）。用于"复杂状态切换"的静态排错。</summary>
        public IReadOnlyList<string> Validate()
        {
            var problems = new List<string>();

            string initial = m_initialStateId;
            if (string.IsNullOrEmpty(initial))
            {
                if (m_states.Count > 0)
                {
                    // 未显式指定初始状态时，取注册顺序里的第一个，并作为提示报出来。
                    foreach (string key in m_states.Keys)
                    {
                        initial = key;
                        break;
                    }
                    problems.Add("No initial state set; falling back to first registered state '"
                        + initial + "'.");
                }
                else
                {
                    problems.Add("Machine '" + m_id + "' has no registered states.");
                }
            }
            else if (!HasState(initial))
            {
                problems.Add("Initial state '" + initial + "' is not registered.");
            }

            var reachable = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(initial))
                reachable.Add(initial);

            for (int i = 0; i < m_transitions.Count; i++)
            {
                AiTransition transition = m_transitions[i];

                if (!string.Equals(transition.From, AnyState, StringComparison.Ordinal)
                    && !HasState(transition.From))
                {
                    problems.Add("Transition source '" + transition.From + "' is not registered ("
                        + transition + ").");
                }

                if (string.Equals(transition.To, PreviousStateTarget, StringComparison.Ordinal)
                    || string.Equals(transition.To, PopStateTarget, StringComparison.Ordinal))
                {
                    // 动态目标：运行期解析，这里只提示它依赖历史栈。
                    continue;
                }

                if (!HasState(transition.To))
                {
                    problems.Add("Transition target '" + transition.To + "' is not registered ("
                        + transition + ").");
                    continue;
                }

                if (string.Equals(transition.From, AnyState, StringComparison.Ordinal))
                {
                    // 全局面转移可以到达任何地方，但仍需源状态可达才有意义。
                    continue;
                }

                if (reachable.Contains(transition.From))
                    reachable.Add(transition.To);
            }

            foreach (KeyValuePair<string, AiState> pair in m_states)
            {
                if (!reachable.Contains(pair.Key))
                    problems.Add("State '" + pair.Key + "' is unreachable from the initial state.");
            }

            if (m_transitions.Count == 0 && m_states.Count > 1)
                problems.Add("Machine has multiple states but no transitions.");

            return problems;
        }

        // ------------------------------------------------------------ 生命周期

        /// <summary>清空运行期状态（不动注册表），可反复调用。</summary>
        public void Reset()
        {
            m_current = null;
            m_previous = null;
            m_timeInState = 0f;
            m_timeInMachine = 0f;
            m_transitionCount = 0;
            m_pendingTriggers.Clear();
            m_scratchTriggers.Clear();
            m_history.Clear();
            m_transitionLog.Clear();
            m_started = false;
            m_faulted = false;
            for (int i = 0; i < m_transitions.Count; i++)
                m_transitions[i].LastFiredClock = float.NegativeInfinity;
        }

        /// <summary>进入初始状态。Tick 会自动调用，也可以显式提前调用。</summary>
        public void Start()
        {
            if (m_started)
                return;

            m_started = true;

            string initial = ResolveInitialStateId();
            if (string.IsNullOrEmpty(initial))
            {
                Engine.Log.Warning("[PlayerAi] Machine '" + m_id + "' has no initial state.");
                m_faulted = true;
                return;
            }

            AiStateContext context = new AiStateContext(this, m_host, 0f);
            PerformEnter(initial, null, "start", context);
        }

        public void Raise(string trigger)
        {
            if (string.IsNullOrEmpty(trigger))
                return;
            if (m_pendingTriggers.Count >= MaxPendingTriggers)
            {
                // 队列满了说明状态处理不过来：丢掉最旧的一条，保证最新意图不丢。
                m_pendingTriggers.Dequeue();
            }
            m_pendingTriggers.Enqueue(trigger);
        }

        /// <summary>跳过转移表直接切换（安全回退、外部强制指令、Push/Pop 内部使用）。</summary>
        public void ForceTransition(string stateId, string reason)
        {
            if (!m_started)
                m_started = true;
            if (!HasState(stateId))
            {
                Engine.Log.Warning("[PlayerAi] ForceTransition to unknown state '" + stateId
                    + "' ignored (" + reason + ").");
                m_faulted = true;
                return;
            }

            AiStateContext context = m_tickContext ?? new AiStateContext(this, m_host, 0f);
            PerformEnter(stateId, null, reason, context);
        }

        /// <summary>打断进入：把当前状态压栈，之后可用 <see cref="PopState"/> 回来。</summary>
        public void PushState(string stateId, string reason = null)
        {
            if (!HasState(stateId))
            {
                Engine.Log.Warning("[PlayerAi] PushState to unknown state '" + stateId + "' ignored.");
                return;
            }

            if (m_current != null && !string.Equals(m_current.Id, stateId, StringComparison.Ordinal))
            {
                if (m_history.Count >= MaxHistoryDepth)
                    m_history.RemoveAt(0);
                m_history.Add(m_current.Id);
            }

            ForceTransition(stateId, reason ?? ("push:" + stateId));
        }

        /// <summary>弹出历史栈并回到该状态。栈空返回 false。</summary>
        public bool PopState(string reason = null)
        {
            while (m_history.Count > 0)
            {
                string target = m_history[m_history.Count - 1];
                m_history.RemoveAt(m_history.Count - 1);
                if (HasState(target))
                {
                    ForceTransition(target, reason ?? ("pop:" + target));
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------ 每帧

        /// <summary>推进状态机：裁决转移 → 执行当前状态行为。必须每帧在帧首调用一次。</summary>
        public void Tick(float deltaTime)
        {
            if (deltaTime < 0f)
                deltaTime = 0f;
            if (deltaTime > 0.5f)
                deltaTime = 0.5f; // 卡顿/断点后不要一次补太多时间

            if (!m_started)
                Start();
            if (m_faulted || m_current == null)
                return;

            m_clock += deltaTime;
            m_timeInState += deltaTime;
            m_timeInMachine += deltaTime;

            m_tickContext = new AiStateContext(this, m_host, deltaTime);
            try
            {
                // 1) 转移裁决（有上限，且禁止一帧内反复横跳）
                DrainPendingTriggers();
                int budget = PlayerAiConfig.MaxTransitionsPerTick;
                while (budget-- > 0)
                {
                    if (!ApplyOneTransition(m_tickContext))
                        break;
                }

                // 2) 当前状态行为（异常隔离）
                if (m_current == null || m_faulted)
                    return;

                try
                {
                    m_current.Tick(m_tickContext);
                }
                catch (Exception exception)
                {
                    HandleStateException(m_current, exception);
                }
            }
            finally
            {
                m_tickContext = null;
                m_scratchTriggers.Clear();
            }
        }

        // ------------------------------------------------------------ 内部：转移裁决

        private void DrainPendingTriggers()
        {
            int count = m_pendingTriggers.Count;
            for (int i = 0; i < count; i++)
                m_scratchTriggers.Add(m_pendingTriggers.Dequeue());
        }

        private bool ApplyOneTransition(AiStateContext context)
        {
            // 触发式转移：按入队顺序处理；一旦成功切换，剩余触发全部作废（世界已经变了）。
            while (m_scratchTriggers.Count > 0)
            {
                string trigger = m_scratchTriggers[0];
                m_scratchTriggers.RemoveAt(0);

                AiTransition transition = SelectTransition(trigger, false, context);
                if (transition == null)
                    continue;

                m_scratchTriggers.Clear();
                return ApplyTransition(transition, "trigger:" + trigger, context);
            }

            // 条件式转移：每帧至多一条。
            AiTransition conditional = SelectTransition(null, true, context);
            if (conditional == null)
                return false;

            return ApplyTransition(conditional, "condition", context);
        }

        private AiTransition SelectTransition(string trigger, bool conditionOnly, AiStateContext context)
        {
            if (m_current == null)
                return null;

            AiTransition best = null;

            // 两轮扫描：先"当前状态的专属转移"，再"全局面转移"。
            // 同优先级下先扫描的赢，因此专属转移默认压过全局面转移。
            for (int pass = 0; pass < 2; pass++)
            {
                List<AiTransition> source = pass == 0 ? m_transitions : m_globalTransitions;
                for (int i = 0; i < source.Count; i++)
                {
                    AiTransition transition = source[i];

                    if (pass == 0 && !string.Equals(transition.From, m_current.Id, StringComparison.Ordinal))
                        continue;

                    if (conditionOnly)
                    {
                        if (transition.Trigger != null)
                            continue;
                    }
                    else if (!string.Equals(transition.Trigger, trigger, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (best != null && transition.Priority <= best.Priority)
                        continue;

                    if (!IsTransitionUsable(transition, context))
                        continue;

                    best = transition;
                }
            }

            return best;
        }

        private bool IsTransitionUsable(AiTransition transition, AiStateContext context)
        {
            string resolved;
            if (!TryResolveTarget(transition.To, out resolved))
                return false;

            if (transition.RequiresInterruptible && m_current != null && !m_current.IsInterruptible)
                return false;

            // MinDwellSeconds > 0 = 本条显式指定；< 0 = 本条显式关闭限制；== 0 = 用全局下限。
            float minimumDwell = transition.MinDwellSeconds < 0f
                ? 0f
                : (transition.MinDwellSeconds > 0f
                    ? transition.MinDwellSeconds
                    : PlayerAiConfig.DefaultMinDwellSeconds);
            if (minimumDwell > 0f && m_timeInState < minimumDwell)
                return false;

            if (transition.CooldownSeconds > 0f
                && m_clock - transition.LastFiredClock < transition.CooldownSeconds)
            {
                return false;
            }

            if (transition.Guard != null)
            {
                try
                {
                    if (!transition.Guard(context))
                        return false;
                }
                catch (Exception exception)
                {
                    Engine.Log.Warning("[PlayerAi] Guard of transition " + transition
                        + " threw " + exception.GetType().Name + ": " + exception.Message
                        + " -> treated as false.");
                    return false;
                }
            }

            return true;
        }

        private bool ApplyTransition(AiTransition transition, string reason, AiStateContext context)
        {
            string target;
            if (!TryResolveTarget(transition.To, out target))
                return false;

            if (string.Equals(transition.To, PopStateTarget, StringComparison.Ordinal)
                && m_history.Count > 0)
            {
                m_history.RemoveAt(m_history.Count - 1);
            }

            PerformEnter(target, transition, reason, context);
            return true;
        }

        private void PerformEnter(string stateId, AiTransition source, string reason, AiStateContext context)
        {
            AiState next = GetState(stateId);
            if (next == null)
            {
                Engine.Log.Warning("[PlayerAi] Cannot enter unknown state '" + stateId + "'.");
                m_faulted = true;
                return;
            }

            if (m_current != null)
            {
                try
                {
                    m_current.Exit(context);
                }
                catch (Exception exception)
                {
                    Engine.Log.Warning("[PlayerAi] Exit of state '" + m_current.Id + "' threw "
                        + exception.GetType().Name + ": " + exception.Message);
                }
            }

            m_previous = m_current;
            m_current = next;
            m_timeInState = 0f;
            m_transitionCount++;
            if (source != null)
                source.LastFiredClock = m_clock;

            RecordTransition(source, stateId, reason);

            try
            {
                next.Enter(context);
            }
            catch (Exception exception)
            {
                Engine.Log.Warning("[PlayerAi] Enter of state '" + stateId + "' threw "
                    + exception.GetType().Name + ": " + exception.Message
                    + " -> machine faulted.");
                m_faulted = true;
            }
        }

        private void RecordTransition(AiTransition source, string targetStateId, string reason)
        {
            string line = (m_previous != null ? m_previous.Id : "<none>") + " -> " + targetStateId
                + " (" + (reason ?? "?") + ")"
                + (source != null && !string.IsNullOrEmpty(source.Description)
                    ? " [" + source.Description + "]"
                    : string.Empty);

            m_transitionLog.Add(line);
            while (m_transitionLog.Count > PlayerAiConfig.TransitionLogCapacity)
                m_transitionLog.RemoveAt(0);

            if (PlayerAiConfig.VerboseLogging)
                Engine.Log.Information("[PlayerAi] " + m_id + ": " + line);
        }

        private bool TryResolveTarget(string to, out string stateId)
        {
            stateId = null;
            if (string.IsNullOrEmpty(to))
                return false;

            if (string.Equals(to, PreviousStateTarget, StringComparison.Ordinal))
            {
                if (m_previous == null)
                    return false;
                stateId = m_previous.Id;
                return HasState(stateId);
            }

            if (string.Equals(to, PopStateTarget, StringComparison.Ordinal))
            {
                if (m_history.Count == 0)
                    return false;
                stateId = m_history[m_history.Count - 1];
                return HasState(stateId);
            }

            if (!HasState(to))
                return false;

            stateId = to;
            return true;
        }

        private void HandleStateException(AiState state, Exception exception)
        {
            Engine.Log.Warning("[PlayerAi] State '" + state.Id + "' threw "
                + exception.GetType().Name + ": " + exception.Message);

            string safe = PlayerAiConfig.SafeStateId;
            if (!string.IsNullOrEmpty(safe) && HasState(safe)
                && !string.Equals(safe, state.Id, StringComparison.Ordinal))
            {
                try
                {
                    AiStateContext context = m_tickContext ?? new AiStateContext(this, m_host, 0f);
                    PerformEnter(safe, null, "safe-fallback", context);
                    return;
                }
                catch (Exception fallbackException)
                {
                    Engine.Log.Warning("[PlayerAi] Safe fallback to '" + safe + "' failed: "
                        + fallbackException.Message);
                }
            }

            // 没有安全状态可退：停机，等 Reset。
            m_faulted = true;
            m_current = null;
        }

        private string ResolveInitialStateId()
        {
            if (!string.IsNullOrEmpty(m_initialStateId))
                return m_initialStateId;
            foreach (string key in m_states.Keys)
                return key;
            return null;
        }

        // ------------------------------------------------------------ 观测

        public AiStateMachineSnapshot Snapshot()
        {
            var snapshot = new AiStateMachineSnapshot
            {
                MachineId = m_id,
                CurrentStateId = CurrentStateId,
                PreviousStateId = PreviousStateId,
                TimeInState = m_timeInState,
                TimeInMachine = m_timeInMachine,
                TransitionCount = m_transitionCount,
                PendingTriggerCount = m_pendingTriggers.Count,
                IsStarted = m_started,
                IsFaulted = m_faulted,
                LastTransition = m_transitionLog.Count > 0
                    ? m_transitionLog[m_transitionLog.Count - 1]
                    : null,
                History = new List<string>(m_history),
                RecentTransitions = new List<string>(m_transitionLog)
            };
            return snapshot;
        }

        /// <summary>状态图文本（每行一条），用于日志与自检。</summary>
        public List<string> DescribeGraph()
        {
            var lines = new List<string>();
            lines.Add("machine " + m_id + " (" + m_states.Count + " states, "
                + m_transitions.Count + " transitions)");

            foreach (KeyValuePair<string, AiState> pair in m_states)
                lines.Add("  state " + pair.Key);

            for (int i = 0; i < m_transitions.Count; i++)
                lines.Add("  " + m_transitions[i]);

            return lines;
        }
    }
}
