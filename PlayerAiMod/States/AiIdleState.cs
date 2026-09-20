namespace PlayerAiMod
{
    /// <summary>
    /// 待机状态（占位）：AI 已接管角色但没有任务时停留在这里。
    ///
    /// 现在只做两件事：进入时把"上一件事"的输入残渣清掉，之后什么都不做。
    /// 真实待机行为（环顾、跟随、等待指令……）等方案确定后再写。
    /// 它同时是 <see cref="PlayerAiConfig.SafeStateId"/> 的默认值：
    /// 任何状态抛异常时都会回退到这里，保证不会卡在"按着键不动"的坏状态。
    /// </summary>
    public sealed class AiIdleState : AiState
    {
        public AiIdleState()
            : base(AiStateIds.Idle)
        {
        }

        public override string DisplayName
        {
            get { return "待机"; }
        }

        public override string[] Tags
        {
            get { return s_tags; }
        }

        private static readonly string[] s_tags = { "safe" };

        public override void Enter(AiStateContext context)
        {
            // 从别的状态过来时，先把按住不放的键/鼠标释放掉，避免"待机却在走路"。
            if (context.Actuators != null)
                context.Actuators.ReleaseAll();

            context.Log("idle: entered");
        }

        public override void Tick(AiStateContext context)
        {
            // 占位：待机期间不产生任何动作。
            // 计划确定后在这里实现：等待指令、观察环境、（可选）空闲动作。
        }

        public override void Exit(AiStateContext context)
        {
            context.Log("idle: exit after " + context.TimeInState.ToString("0.00") + "s");
        }
    }
}
