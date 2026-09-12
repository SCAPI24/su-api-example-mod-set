namespace PlayerAiMod
{
    /// <summary>
    /// 运行行为树（模式层的一个模式，计划 §3.5）。
    ///
    /// 这是"模式层"与"决策层"的接缝：进入本模式后，每个 tick 交给行为树；
    /// 树本身不碰状态机，状态机也不管树里怎么选分支 —— 两边各自纯粹。
    ///
    /// 收尾约定（与状态机同一条铁律）：
    ///   · 进入时若树没在跑就 `Start()`（重新开始，不带着上一段的半开运行态）；
    ///   · 离开时**不主动停树**（暂停/失焦要保留运行态），但**故障离开时必须停**
    ///     —— 否则一棵抛过异常的树会在没人 tick 的情况下继续"按着键"。
    /// </summary>
    public sealed class AiTreeState : AiState
    {
        public AiTreeState()
            : base(AiStateIds.Tree)
        {
        }

        public override string DisplayName
        {
            get { return "运行行为树"; }
        }

        public override string[] Tags
        {
            get { return s_tags; }
        }

        private static readonly string[] s_tags = { "active", "tree" };

        public override void Enter(AiStateContext context)
        {
            BtRuntime tree = TreeOf(context);
            if (tree == null)
            {
                context.Warn("tree mode entered without a tree -> back to idle");
                context.Raise(AiTriggers.Stop);
                return;
            }

            if (!tree.IsRunning)
                tree.Start();

            context.Log("tree: enter " + tree);
        }

        public override void Tick(AiStateContext context)
        {
            BtRuntime tree = TreeOf(context);
            if (tree == null)
            {
                // 树被卸下了：让模式层下一帧推导回待机
                return;
            }

            tree.Tick(context.DeltaTime);

            if (tree.LastError != null)
                context.Warn("tree tick error: " + tree.LastError);
        }

        public override void Exit(AiStateContext context)
        {
            BtRuntime tree = TreeOf(context);
            if (tree == null)
                return;

            // 故障离开：必须收尾，避免"没人 tick 的树还按着键"。
            if (tree.LastError != null)
            {
                tree.Stop("leaving tree mode after a fault");
                context.Log("tree: stopped after fault");
            }
            else
            {
                context.Log("tree: leave (runtime state kept)");
            }
        }

        private static BtRuntime TreeOf(AiStateContext context)
        {
            return context != null && context.Host != null ? context.Host.Tree : null;
        }
    }
}
