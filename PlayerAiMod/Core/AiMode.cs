namespace PlayerAiMod
{
    /// <summary>
    /// AI 的**模式层**（计划 §3.5）：状态机退居这里，只表达"角色现在处于哪种大模式"，
    /// 具体决策由行为树负责。这样"谁在决策"永远只有一个答案，调试时也能一眼看出卡在哪一层。
    /// </summary>
    public enum AiMode
    {
        /// <summary>未接管：没启用、世界没就绪、或不是本端玩家（不产生任何动作）。</summary>
        Inactive,

        /// <summary>待机：接管中但没有活动行为树（旧 FSM 示例状态机在这个模式下跑）。</summary>
        Idle,

        /// <summary>运行行为树：决策权在行为树。</summary>
        Tree,

        /// <summary>录制中：行为树暂停、人类操作被录下来（P0-9 接入）。</summary>
        Recording,

        /// <summary>已暂停：保留运行态、释放输入、不推进决策。</summary>
        Paused,

        /// <summary>故障：行为树 tick 抛异常或状态机 faulted —— 已释放输入，等人/控制面处理。</summary>
        Fault
    }

    public static class AiModeExtensions
    {
        /// <summary>稳定的英文名（控制面 JSON 用它，别用中文：脚本要按值判断）。</summary>
        public static string ToWireName(this AiMode mode)
        {
            switch (mode)
            {
                case AiMode.Tree: return "tree";
                case AiMode.Recording: return "recording";
                case AiMode.Paused: return "paused";
                case AiMode.Fault: return "fault";
                case AiMode.Idle: return "idle";
                default: return "inactive";
            }
        }

        public static string Describe(this AiMode mode)
        {
            switch (mode)
            {
                case AiMode.Tree: return "运行行为树";
                case AiMode.Recording: return "录制中";
                case AiMode.Paused: return "已暂停";
                case AiMode.Fault: return "故障";
                case AiMode.Idle: return "待机";
                default: return "未接管";
            }
        }
    }
}
