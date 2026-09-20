namespace PlayerAiMod
{
    /// <summary>
    /// 状态 ID 词汇表。骨架期只有占位状态；计划确定后在这里登记真实状态
    /// （例如 Follow / Explore / Mine / Build / Combat / Flee / Trade ...），
    /// 状态类放在 States/ 下，ID 一律从这里取，避免散落的字符串字面量。
    /// </summary>
    public static class AiStateIds
    {
        /// <summary>待机：已接管但没有活动行为树。</summary>
        public const string Idle = "Idle";

        /// <summary>未接管：绝对安全状态，所有输入已释放（卸载/关闭/故障回退都用它）。</summary>
        public const string Inactive = "Inactive";

        /// <summary>运行行为树：决策权在树里（P0-8 起这是默认工作模式）。</summary>
        public const string Tree = "Tree";

        /// <summary>录制中：行为树暂停、人类操作被录下来（P0-9 接入）。</summary>
        public const string Recording = "Recording";
    }

    /// <summary>
    /// 触发词汇表。触发是状态机转移的"事件名"：状态用 <c>ctx.Raise(trigger)</c> 请求，
    /// 具体换成哪个状态由转移表（AiTransition）裁决，状态本身不关心。
    /// </summary>
    public static class AiTriggers
    {
        /// <summary>开始执行任务。</summary>
        public const string Start = "start";

        /// <summary>停止当前任务，回到安全状态。</summary>
        public const string Stop = "stop";

        /// <summary>子任务完成。</summary>
        public const string Done = "done";

        /// <summary>子任务失败/不可继续。</summary>
        public const string Failed = "failed";

        /// <summary>外部要求释放全部输入（掉线、失焦、被禁用）。</summary>
        public const string ReleaseInput = "release_input";
    }
}
