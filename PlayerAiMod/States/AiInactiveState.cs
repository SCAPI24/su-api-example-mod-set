namespace PlayerAiMod
{
    /// <summary>
    /// 绝对安全状态（占位）：所有输入都已释放、不持有任何动作。
    ///
    /// 用途：World 卸载、Mod 即将卸载、外部急停。与 Idle 的区别是语义 ——
    /// Idle 表示"接管中的空闲"，Inactive 表示"不接管"，之后不会再自行产生动作。
    /// </summary>
    public sealed class AiInactiveState : AiState
    {
        public AiInactiveState()
            : base(AiStateIds.Inactive)
        {
        }

        public override string DisplayName
        {
            get { return "未接管"; }
        }

        public override string[] Tags
        {
            get { return s_tags; }
        }

        private static readonly string[] s_tags = { "safe", "inactive" };

        public override void Enter(AiStateContext context)
        {
            if (context.Actuators != null)
                context.Actuators.ReleaseAll();

            context.Log("inactive: all inputs released");
        }

        public override void Tick(AiStateContext context)
        {
            // 不接管期间不产生任何动作。
        }
    }
}
