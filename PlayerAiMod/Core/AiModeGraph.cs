using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 模式层的**转移表**（计划 §3.5：状态机只表达模式/生命周期，不表达决策）。
    ///
    /// 模式是**从宿主与行为树的真实状态推导出来的**，而不是靠谁记得去 Raise：
    ///   · 装了树 → Tree；树被卸下 → Idle；树 tick 出错 → Inactive（安全态）；
    ///   · 未接管 → Inactive；接管但没树 → Idle。
    /// 这样"控制面直接往运行时里塞了一棵树"也不会出现模式不同步（它会被下一帧推导出来）。
    ///
    /// 表放在这里而不是散在构造函数里，是为了让它能被自检直接核对（见 <see cref="AiModeSelfTest"/>）。
    /// </summary>
    public static class AiModeGraph
    {
        /// <summary>条件式转移：Trigger 为 null 表示"每帧只看守卫"（见 AiStateMachine 的说明）。</summary>
        public const string Conditional = null;

        /// <summary>
        /// 把模式层的转移装进状态机。
        ///
        /// 一张表的转移**全部显式关掉最短驻留**（`MinDwellSeconds = -1`）：
        /// 防抖是给"决策"用的（避免战斗/逃跑来回横跳），而模式层表达的是客观事实
        /// —— 有没有树、树有没有故障。给事实加 0.15 秒延迟只会让 `ai.status` 撒谎、
        /// 让"编辑器保存后立刻看到行为变化"变成"过一会儿才变"。
        /// </summary>
        public static void Apply(AiStateMachine machine)
        {
            if (machine == null)
                return;

            // 接管：未接管 → 待机（有没有树由下面的条件式转移继续推导）
            Connect(machine, AiStateIds.Inactive, AiTriggers.Start, AiStateIds.Idle,
                null, 10, "接管角色");

            // 模式推导：待机 ⇄ 运行树
            // 未接管态也接一条：首次装载、或"故障后热重载恢复"时，树装上了就该开跑。
            // （真正的"放弃接管"由 Enabled=false 拦住 —— 那时 Actor 根本不 tick 模式层。）
            Connect(machine, AiStateIds.Inactive, Conditional, AiStateIds.Tree,
                TreeReady, 15, "未接管但已装树 → 直接运行");
            Connect(machine, AiStateIds.Idle, Conditional, AiStateIds.Tree,
                HasLoadedTree, 20, "有树 → 运行行为树");
            Connect(machine, AiStateIds.Tree, Conditional, AiStateIds.Idle,
                HasNoTree, 20, "树被卸下 → 待机");

            // 录制：录制中不 tick 树（AI 停手、人类操作被录），运行态保留
            Connect(machine, AiStateIds.Idle, Conditional, AiStateIds.Recording,
                IsRecording, 25, "开始录制 → 录制模式");
            Connect(machine, AiStateIds.Tree, Conditional, AiStateIds.Recording,
                IsRecording, 26, "开始录制 → 录制模式（AI 停手）");
            Connect(machine, AiStateIds.Recording, Conditional, AiStateIds.Tree,
                RecordingFinishedWithTree, 27, "录制结束 → 回到运行树");
            Connect(machine, AiStateIds.Recording, Conditional, AiStateIds.Idle,
                RecordingFinishedWithoutTree, 28, "录制结束 → 待机");

            // 故障：树 tick 抛过异常 → 回安全态（InactiveState 会释放全部输入）
            Connect(machine, AiStateIds.Tree, Conditional, AiStateIds.Inactive,
                TreeFaulted, 30, "行为树故障 → 未接管");

            // 全局兜底：任何模式下 Stop/Failed/ReleaseInput 都回到未接管
            Connect(machine, AiStateMachine.AnyState, AiTriggers.Stop, AiStateIds.Inactive,
                null, 100, "外部急停");
            Connect(machine, AiStateMachine.AnyState, AiTriggers.Failed, AiStateIds.Inactive,
                null, 100, "失败兜底");
            Connect(machine, AiStateMachine.AnyState, AiTriggers.ReleaseInput, AiStateIds.Inactive,
                null, 200, "释放输入");
        }

        /// <summary>模式层专用连接：一律 `MinDwellSeconds = -1`（见 <see cref="Apply"/> 的说明）。</summary>
        private static void Connect(AiStateMachine machine, string from, string trigger, string to,
            Func<AiStateContext, bool> guard, int priority, string description)
        {
            machine.Add(new AiTransition(from, trigger, to)
            {
                Guard = guard,
                Priority = priority,
                Description = description,
                MinDwellSeconds = -1f
            });
        }

        /// <summary>已装载行为树（由运行时的树标识推导，不额外维护标志）。</summary>
        public static bool HasLoadedTree(AiStateContext context)
        {
            return context != null && context.Host != null && context.Host.HasTree;
        }

        public static bool HasNoTree(AiStateContext context)
        {
            return !HasLoadedTree(context);
        }

        /// <summary>装了树且当前没有故障标记（可以开跑）。</summary>
        public static bool TreeReady(AiStateContext context)
        {
            return HasLoadedTree(context) && !TreeFaulted(context);
        }

        /// <summary>正在录制动作包。</summary>
        public static bool IsRecording(AiStateContext context)
        {
            return context != null && context.Host != null && context.Host.IsRecording;
        }

        /// <summary>录制结束且树还在 → 回到运行树。</summary>
        public static bool RecordingFinishedWithTree(AiStateContext context)
        {
            return !IsRecording(context) && TreeReady(context);
        }

        /// <summary>录制结束且没有树 → 回待机。</summary>
        public static bool RecordingFinishedWithoutTree(AiStateContext context)
        {
            return !IsRecording(context) && !TreeReady(context);
        }

        /// <summary>行为树 tick 出过错（异常被运行时隔离并记录）。</summary>
        public static bool TreeFaulted(AiStateContext context)
        {
            return context != null && context.Host != null && context.Host.Tree != null
                && context.Host.Tree.LastError != null;
        }
    }

    /// <summary>模式 id（状态机的状态 = 模式）。</summary>
    public static class AiModeIds
    {
        public const string Inactive = AiStateIds.Inactive;
        public const string Idle = AiStateIds.Idle;
        public const string Tree = AiStateIds.Tree;

        /// <summary>录制中（P0-9 接入；先占好名字，避免各处散落字符串）。</summary>
        public const string Recording = AiStateIds.Recording;
    }

    /// <summary>
    /// 状态/运行态 → 对外模式名（控制面 `ai.status` 与日志用）。
    /// 单独抽出来是为了让"模式映射"也有唯一实现、并可被自检覆盖。
    /// </summary>
    public static class AiModeMap
    {
        public static AiMode FromState(string stateId, bool paused, bool faulted)
        {
            if (faulted)
                return AiMode.Fault;

            if (string.Equals(stateId, AiStateIds.Tree, System.StringComparison.Ordinal))
                return paused ? AiMode.Paused : AiMode.Tree;

            if (string.Equals(stateId, AiStateIds.Recording, System.StringComparison.Ordinal))
                return AiMode.Recording;

            if (string.Equals(stateId, AiStateIds.Inactive, System.StringComparison.Ordinal))
                return AiMode.Inactive;

            if (string.IsNullOrEmpty(stateId))
                return AiMode.Inactive;

            return paused ? AiMode.Paused : AiMode.Idle;
        }
    }
}
