using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 状态机快照：只读副本，用于调试输出、界面显示与外部观测。
    ///
    /// 之所以单独出快照而不是直接暴露内部字段：外部（日志/调试/未来的观察接口）
    /// 拿到的是某个瞬间的一致视图，不会随状态机继续运行而变化。
    /// </summary>
    public sealed class AiStateMachineSnapshot
    {
        public string MachineId { get; internal set; }
        public string CurrentStateId { get; internal set; }
        public string PreviousStateId { get; internal set; }
        public float TimeInState { get; internal set; }
        public float TimeInMachine { get; internal set; }
        public int TransitionCount { get; internal set; }
        public int PendingTriggerCount { get; internal set; }
        public bool IsStarted { get; internal set; }
        public bool IsFaulted { get; internal set; }
        public string LastTransition { get; internal set; }

        /// <summary>状态栈（返回地址），最后一项是最内层。</summary>
        public IReadOnlyList<string> History { get; internal set; }

        /// <summary>最近的转移记录，最新在后。</summary>
        public IReadOnlyList<string> RecentTransitions { get; internal set; }

        public override string ToString()
        {
            return MachineId + ": " + (CurrentStateId ?? "<none>")
                + " t=" + TimeInState.ToString("0.00") + "s"
                + " transitions=" + TransitionCount.ToString()
                + " depth=" + (History == null ? 0 : History.Count)
                + (IsFaulted ? " FAULTED" : string.Empty);
        }
    }
}
