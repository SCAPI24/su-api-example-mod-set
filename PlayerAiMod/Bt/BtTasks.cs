using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 任务节点基类（对齐 UE 的 `UBTTaskNode`）。
    /// 一次性任务重写 <see cref="BtNode.OnExecute"/> 即可；跨帧任务两者都重写。
    /// </summary>
    public abstract class BtTaskNode : BtNode
    {
        /// <summary>是否跨帧（Latent）。仅用于编辑器展示与校验提示。</summary>
        public virtual bool IsLatent
        {
            get { return false; }
        }
    }

    /// <summary>等待若干秒（跨帧任务的最小范例）。</summary>
    public sealed class BtWaitTask : BtTaskNode
    {
        /// <summary>等待时长（秒）。</summary>
        public float Seconds { get; set; } = 1f;

        public override string NodeType
        {
            get { return "Task.Wait"; }
        }

        public override bool IsLatent
        {
            get { return Seconds > 0f; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            return context.DeltaTime >= Seconds ? BtResult.Succeeded : BtResult.InProgress;
        }

        protected override BtResult OnTick(BtContext context)
        {
            return ActiveTime >= Seconds ? BtResult.Succeeded : BtResult.InProgress;
        }

        public override string ToString()
        {
            return base.ToString() + " " + Seconds.ToString("0.00") + "s";
        }
    }

    /// <summary>写黑板（调试与"把决策结果留给后续节点"用）。</summary>
    public sealed class BtSetBlackboardTask : BtTaskNode
    {
        public string Key { get; set; }

        /// <summary>bool | int | float | string</summary>
        public string ValueKind { get; set; } = "float";

        public bool BoolValue { get; set; }

        public int IntValue { get; set; }

        public float FloatValue { get; set; }

        public string StringValue { get; set; }

        public override string NodeType
        {
            get { return "Task.SetBlackboard"; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            AiBlackboard blackboard = context.Blackboard;
            if (blackboard == null || string.IsNullOrEmpty(Key))
                return BtResult.Failed;

            switch ((ValueKind ?? "float").Trim().ToLowerInvariant())
            {
                case "bool":
                    blackboard.Set(new AiBlackboardKey<bool>(Key), BoolValue);
                    return BtResult.Succeeded;
                case "int":
                    blackboard.Set(new AiBlackboardKey<int>(Key), IntValue);
                    return BtResult.Succeeded;
                case "string":
                    blackboard.Set(new AiBlackboardKey<string>(Key), StringValue ?? string.Empty);
                    return BtResult.Succeeded;
                default:
                    blackboard.Set(new AiBlackboardKey<float>(Key), FloatValue);
                    return BtResult.Succeeded;
            }
        }
    }

    /// <summary>写日志（默认永远成功，可用 <see cref="Succeed"/> 控制返回值）。</summary>
    public sealed class BtLogTask : BtTaskNode
    {
        public string Message { get; set; }

        public bool Succeed { get; set; } = true;

        public override string NodeType
        {
            get { return "Task.Log"; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            context.Log((string.IsNullOrEmpty(Message) ? "(log)" : Message) + " @tick " + context.TickIndex);
            return Succeed ? BtResult.Succeeded : BtResult.Failed;
        }
    }

    /// <summary>
    /// 委托任务：把"一次性动作"直接写成 lambda（测试、原型、以及不适合单独建类的场合）。
    /// <see cref="Latent"/> 为真时，<see cref="TickDelegate"/> 每帧被调用，返回 InProgress 表示继续。
    /// </summary>
    public sealed class BtLambdaTask : BtTaskNode
    {
        public Func<BtContext, BtResult> ExecuteDelegate { get; set; }

        public Func<BtContext, BtResult> TickDelegate { get; set; }

        public Action<BtContext, BtResult> ExitDelegate { get; set; }

        public bool Latent { get; set; }

        public override string NodeType
        {
            get { return "Task.Lambda"; }
        }

        public override bool IsLatent
        {
            get { return Latent; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            if (ExecuteDelegate == null)
                return Latent ? BtResult.InProgress : BtResult.Succeeded;

            BtResult result = ExecuteDelegate(context);
            if (Latent && result != BtResult.InProgress)
                return result;
            return result;
        }

        protected override BtResult OnTick(BtContext context)
        {
            if (TickDelegate == null)
                return BtResult.Succeeded;
            return TickDelegate(context);
        }

        protected override void OnExit(BtContext context, BtResult result)
        {
            if (ExitDelegate != null)
                ExitDelegate(context, result);
        }
    }
}
