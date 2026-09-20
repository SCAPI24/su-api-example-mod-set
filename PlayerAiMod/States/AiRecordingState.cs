namespace PlayerAiMod
{
    /// <summary>
    /// 录制中（模式层的一个模式，计划 §3.5 / §6.1）。
    ///
    /// 语义：**AI 停手，人来操作，全程被录**。
    ///   · 进入：释放 AI 按住的全部输入（否则"AI 按着 W，人又在按 A"，录下来的意图是混的）；
    ///   · 停留：不 tick 行为树 —— 决策停摆但**运行态保留**（录制结束回到运行树模式时接着跑）；
    ///   · 离开：什么都不做（树的运行态与"暂停"语义一致，恢复后从原处继续）。
    ///
    /// 为什么不在录制期间用全局暂停（`ai.pause`）：全局暂停会让 `PlayerAiRuntime` 直接跳过整帧的
    /// 模式层 tick，模式也就无法表达"录制中"了。两层各管一件事：暂停 = 人按的开关，
    /// 录制 = 会话状态，两者都能独立生效。
    /// </summary>
    public sealed class AiRecordingState : AiState
    {
        public AiRecordingState()
            : base(AiStateIds.Recording)
        {
        }

        public override string DisplayName
        {
            get { return "录制中"; }
        }

        public override string[] Tags
        {
            get { return s_tags; }
        }

        private static readonly string[] s_tags = { "recording", "human-in-control" };

        public override void Enter(AiStateContext context)
        {
            if (context.Actuators != null)
                context.Actuators.ReleaseAll();

            context.Log("recording: enter (AI hands over, tree is not ticked)");
        }

        public override void Tick(AiStateContext context)
        {
            // 录制期间不做任何 AI 决策：树不 tick、不按键。
            // （采样在 PlayerAiRuntime 的帧首钩子里推进 —— 那里才拿得到稳定的帧步长。）
        }

        public override void Exit(AiStateContext context)
        {
            context.Log("recording: leave (tree runtime state kept)");
        }
    }
}
