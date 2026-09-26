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
                    // **写空字符串 = 清掉这个键**（不是"写一个空值进去"）。
                    //
                    // 为什么这条规则必须有：`Blackboard` 装饰器的 `IsSet`/`IsNotSet` 看的是
                    // "键在不在"，而"写了空串"的键**仍然在** —— 于是"清失败标记"这个动作
                    // 闭不上门，异常分支会一直命中、每 tick 重问一次（实机踩过：旗标摆好了没人消费，
                    // 看着就是"异常处理没反应"）。真正清掉才符合所有人的直觉。
                    string text = StringValue ?? string.Empty;
                    if (text.Length == 0)
                        blackboard.Remove(new AiBlackboardKey<string>(Key));
                    else
                        blackboard.Set(new AiBlackboardKey<string>(Key), text);
                    return BtResult.Succeeded;
                default:
                    blackboard.Set(new AiBlackboardKey<float>(Key), FloatValue);
                    return BtResult.Succeeded;
            }
        }
    }

    /// <summary>
    /// **黑板计数器**（`+= delta` / 清零）：给树一个"连续发生了几次"的记忆。
    ///
    /// 为什么必须有它（而不是让模型自己记住）：§5.4 的实测结论之一，也是实机踩到的稳定循环 ——
    /// 摘要不变时模型会**每次都给同一个答案**（PC 上实测：`aim=none` + `sleep=exhausted` → 连答 7 次
    /// `goal=sleep`，而该动作在当前局面不可能成功）。**失败标记不许进摘要**（plan G22），
    /// 所以"连败几次了"这件事只能活在树里：计数器 + `Blackboard(key, >=, N)` 装饰器 = 反射层兜底，
    /// 由 C# 确定性执行，不经过模型。
    ///
    /// 语义（三条都要，否则用起来会咬人）：
    ///   · 键**缺失 = 0**（第一次 `+= 1` 得到 1，不需要初始化节点）；
    ///   · `clear = true` 时**直接清零**（不叠加）；
    ///   · 键里存的不是 int（例如被写成 string）→ 当 0 处理，**不抛异常**（树里的数据是运行时可变的）。
    /// </summary>
    public sealed class BtCounterTask : BtTaskNode
    {
        /// <summary>计数器所在的黑板键。</summary>
        public string Key { get; set; }

        /// <summary>每次执行加多少（可为负）。</summary>
        public int Delta { get; set; } = 1;

        /// <summary>true = 直接清零（忽略 <see cref="Delta"/>）。</summary>
        public bool Clear { get; set; }

        /// <summary>清零时是否**删掉键**（而不是写 0）。默认写 0，便于 `>= 3` 这类比较继续成立。</summary>
        public bool RemoveWhenClear { get; set; }

        /// <summary>最近一次的值（`ai.status` / 日志复盘用）。</summary>
        public int LastValue { get; private set; }

        public override string NodeType
        {
            get { return "Task.Counter"; }
        }

        protected override BtResult OnExecute(BtContext context)
        {
            AiBlackboard blackboard = context.Blackboard;
            if (blackboard == null || string.IsNullOrEmpty(Key))
                return BtResult.Failed;

            if (Clear && RemoveWhenClear)
            {
                blackboard.Remove(new AiBlackboardKey<int>(Key));
                LastValue = 0;
                context.Log("Counter: " + Key + " removed");
                return BtResult.Succeeded;
            }

            int current;
            if (!blackboard.TryGet(new AiBlackboardKey<int>(Key), out current))
                current = 0;

            LastValue = Clear ? 0 : current + Delta;
            blackboard.Set(new AiBlackboardKey<int>(Key), LastValue);
            context.Log("Counter: " + Key + " = " + LastValue
                + (Clear ? " (cleared)" : " (was " + current + ")"));
            return BtResult.Succeeded;
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
