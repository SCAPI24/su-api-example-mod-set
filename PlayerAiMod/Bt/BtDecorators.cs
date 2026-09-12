using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 黑板条件装饰器（对齐 UE 的 `BTDecorator_Blackboard`）：
    /// 检查某个黑板键是否已设置、或与给定常量做比较。
    /// </summary>
    public sealed class BtBlackboardDecorator : BtDecorator
    {
        /// <summary>黑板键名。</summary>
        public string Key { get; set; }

        /// <summary>IsSet | IsNotSet | Compare</summary>
        public string Query { get; set; } = "IsSet";

        /// <summary>比较运算符：== != &lt; &lt;= &gt; &gt;=</summary>
        public string Operator { get; set; } = "==";

        /// <summary>bool | int | float | string</summary>
        public string ValueKind { get; set; } = "bool";

        public bool BoolValue { get; set; } = true;

        public int IntValue { get; set; }

        public float FloatValue { get; set; }

        public string StringValue { get; set; }

        public override string NodeType
        {
            get { return "Blackboard"; }
        }

        protected override bool EvaluateCondition(BtContext context)
        {
            AiBlackboard blackboard = context.Blackboard;
            if (blackboard == null || string.IsNullOrEmpty(Key))
                return false;

            string query = (Query ?? "IsSet").Trim().ToLowerInvariant();
            if (query == "isset" || query == "is set")
                return blackboard.Has(Key);
            if (query == "isnotset" || query == "is not set")
                return !blackboard.Has(Key);

            switch ((ValueKind ?? "bool").Trim().ToLowerInvariant())
            {
                case "bool":
                {
                    bool value;
                    return blackboard.TryGet(Key, out value)
                        && Compare(value.CompareTo(BoolValue), Operator);
                }
                case "int":
                {
                    int value;
                    return blackboard.TryGet(new AiBlackboardKey<int>(Key), out value)
                        && Compare(value.CompareTo(IntValue), Operator);
                }
                case "float":
                {
                    float value;
                    if (!blackboard.TryGet(Key, out value))
                        return false;
                    float delta = value - FloatValue;
                    if (Math.Abs(delta) < 1e-6f)
                        delta = 0f;
                    return Compare(Math.Sign(delta), Operator);
                }
                default:
                {
                    string value;
                    return blackboard.TryGet(new AiBlackboardKey<string>(Key), out value)
                        && Compare(string.CompareOrdinal(value ?? string.Empty, StringValue ?? string.Empty), Operator);
                }
            }
        }

        private static bool Compare(int comparison, string op)
        {
            switch ((op ?? "==").Trim())
            {
                case "==": return comparison == 0;
                case "!=": return comparison != 0;
                case "<": return comparison < 0;
                case "<=": return comparison <= 0;
                case ">": return comparison > 0;
                case ">=": return comparison >= 0;
                default: return false;
            }
        }
    }

    /// <summary>
    /// 冷却装饰器（对齐 UE 的 `BTDecorator_Cooldown`）：节点成功执行后锁定一段时间。
    /// </summary>
    public sealed class BtCooldownDecorator : BtDecorator
    {
        private double m_lockedUntil = double.NegativeInfinity;

        public float CooldownSeconds { get; set; } = 5f;

        public override string NodeType
        {
            get { return "Cooldown"; }
        }

        protected override bool EvaluateCondition(BtContext context)
        {
            return context.Time >= m_lockedUntil;
        }

        public override BtResult ModifyResult(BtContext context, BtResult result)
        {
            if (result == BtResult.Succeeded || result == BtResult.Failed)
                m_lockedUntil = context.Time + Math.Max(0f, CooldownSeconds);
            return result;
        }

        // 注意：**不重写 ResetState**。冷却是一种"持久效果"（UE 里也存在节点内存中跨树重启保留）：
        // 运行态重置（节点失活、整棵树跑完重跑）不应把冷却清掉，否则冷却形同虚设
        // —— 这个 bug 正是行为树自检抓出来的。
    }

    /// <summary>
    /// 限时装饰器（对齐 UE 的 `BTDecorator_TimeLimit`）：
    /// 超时判失败。计时随"每次重新获得焦点"重置（UE 行为，doc/player-ai-plan.md §3.1）。
    /// </summary>
    public sealed class BtTimeLimitDecorator : BtDecorator
    {
        public float LimitSeconds { get; set; } = 5f;

        public override string NodeType
        {
            get { return "TimeLimit"; }
        }

        protected override bool EvaluateCondition(BtContext context)
        {
            // 条件版：节点已超时就不可用（未激活时 ActiveTime 为 0，等于不限制）
            return Host == null || Host.ActiveTime <= LimitSeconds;
        }

        public override BtResult ModifyResult(BtContext context, BtResult result)
        {
            if (result == BtResult.InProgress && Host != null && Host.ActiveTime > LimitSeconds)
                return BtResult.Failed;
            return result;
        }
    }

    /// <summary>
    /// 循环装饰器（对齐 UE 的 `BTDecorator_Loop`）：把子节点的成功"折回"成运行中，从而重新执行。
    /// 一定要有次数或超时兜底，否则会无限循环。
    /// </summary>
    public sealed class BtLoopDecorator : BtDecorator
    {
        private int m_loopsDone;
        private double m_startTime = double.NaN;

        public int NumLoops { get; set; } = 1;

        public bool InfiniteLoop { get; set; }

        public float InfiniteLoopTimeoutSeconds { get; set; } = 10f;

        public override string NodeType
        {
            get { return "Loop"; }
        }

        protected override bool EvaluateCondition(BtContext context)
        {
            if (double.IsNaN(m_startTime))
                m_startTime = context.Time;

            if (InfiniteLoop)
                return InfiniteLoopTimeoutSeconds <= 0f
                    || context.Time - m_startTime < InfiniteLoopTimeoutSeconds;

            return m_loopsDone < NumLoops;
        }

        public override BtResult ModifyResult(BtContext context, BtResult result)
        {
            if (result != BtResult.Succeeded)
                return result;

            m_loopsDone++;

            if (InfiniteLoop)
            {
                bool timedOut = InfiniteLoopTimeoutSeconds > 0f
                    && !double.IsNaN(m_startTime)
                    && context.Time - m_startTime >= InfiniteLoopTimeoutSeconds;
                if (timedOut)
                {
                    m_loopsDone = 0;
                    m_startTime = double.NaN;
                    return BtResult.Succeeded;
                }
                return BtResult.InProgress;
            }

            if (m_loopsDone < NumLoops)
                return BtResult.InProgress;

            // 一轮循环跑完：计数归零，下一次激活从头开始（中断后也不会残留计数）
            m_loopsDone = 0;
            m_startTime = double.NaN;
            return BtResult.Succeeded;
        }

        public override void ResetState()
        {
            base.ResetState();
            m_loopsDone = 0;
            m_startTime = double.NaN;
        }
    }

    /// <summary>强制成功（对齐 UE 的 `BTDecorator_ForceSuccess`）：把 Failed 改成 Succeeded（不改 Aborted）。</summary>
    public sealed class BtForceSuccessDecorator : BtDecorator
    {
        public override string NodeType
        {
            get { return "ForceSuccess"; }
        }

        protected override bool EvaluateCondition(BtContext context)
        {
            return true;
        }

        public override BtResult ModifyResult(BtContext context, BtResult result)
        {
            return result == BtResult.Failed ? BtResult.Succeeded : result;
        }
    }

    /// <summary>取反（对齐 UE 的 Inverse Condition）：Succeeded ↔ Failed，Aborted 不动。</summary>
    public sealed class BtInverterDecorator : BtDecorator
    {
        public override string NodeType
        {
            get { return "Inverter"; }
        }

        protected override bool EvaluateCondition(BtContext context)
        {
            return true;
        }

        public override BtResult ModifyResult(BtContext context, BtResult result)
        {
            if (result == BtResult.Succeeded)
                return BtResult.Failed;
            if (result == BtResult.Failed)
                return BtResult.Succeeded;
            return result;
        }
    }

    /// <summary>委托条件装饰器：把自定义判断写成 lambda（原型、测试、以及依赖运行时状态的条件）。</summary>
    public sealed class BtConditionDecorator : BtDecorator
    {
        public Func<BtContext, bool> Condition { get; set; }

        public override string NodeType
        {
            get { return "Condition"; }
        }

        protected override bool EvaluateCondition(BtContext context)
        {
            return Condition != null && Condition(context);
        }
    }
}
