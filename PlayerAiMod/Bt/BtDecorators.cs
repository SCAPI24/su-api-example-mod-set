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
    /// 两个黑板键比较（对齐 UE 的 `BTDecorator_CompareBBEntries`）：
    /// 两边都有值、且类型能比时才成立；缺一个键、或类型两边对不上，都判**条件不成立**。
    ///
    /// 为什么需要它：`Blackboard` 装饰器只能拿键和**常量**比，
    /// "目标距离比上次更近了""两个键指向的是不是同一个目标"这类**相对判断**表达不出来。
    ///
    /// 类型不必手填：按黑板里实际存的 CLR 类型自动选比较方式（数值优先，然后 bool / 字符串 /
    /// actor 比名字），数值之间 int 与 float 混用也能比（`targetDistance` 是 float、
    /// `alertDistance` 常写成 int）。
    /// </summary>
    public sealed class BtCompareBlackboardDecorator : BtDecorator
    {
        /// <summary>左键。</summary>
        public string KeyA { get; set; }

        /// <summary>右键。</summary>
        public string KeyB { get; set; }

        /// <summary>比较运算符：== != &lt; &lt;= &gt; &gt;=</summary>
        public string Operator { get; set; } = "==";

        public override string NodeType
        {
            get { return "CompareBBEntries"; }
        }

        protected override bool EvaluateCondition(BtContext context)
        {
            AiBlackboard blackboard = context.Blackboard;
            if (blackboard == null || string.IsNullOrEmpty(KeyA) || string.IsNullOrEmpty(KeyB))
                return false;
            // 两边都得有值：UE 里缺一边就是条件不成立（不是"拿默认值瞎比"）
            if (!blackboard.Has(KeyA) || !blackboard.Has(KeyB))
                return false;

            int comparison;
            if (!TryCompare(blackboard, KeyA, KeyB, out comparison))
            {
                context.Log("CompareBBEntries: '" + KeyA + "' 与 '" + KeyB
                    + "' 类型没法比较（数值/bool/字符串/actor 才能比）-> 条件不成立");
                return false;
            }
            return CompareSign(comparison, Operator);
        }

        private static bool TryCompare(AiBlackboard blackboard, string keyA, string keyB,
            out int comparison)
        {
            comparison = 0;

            double numberA;
            double numberB;
            if (TryNumber(blackboard, keyA, out numberA) && TryNumber(blackboard, keyB, out numberB))
            {
                double delta = numberA - numberB;
                comparison = Math.Abs(delta) < 1e-6 ? 0 : Math.Sign(delta);
                return true;
            }

            bool boolA;
            bool boolB;
            if (blackboard.TryGet(keyA, out boolA) && blackboard.TryGet(keyB, out boolB))
            {
                comparison = boolA == boolB ? 0 : (boolA ? 1 : -1);
                return true;
            }

            string textA;
            string textB;
            if (blackboard.TryGet(new AiBlackboardKey<string>(keyA), out textA)
                && blackboard.TryGet(new AiBlackboardKey<string>(keyB), out textB))
            {
                comparison = string.CompareOrdinal(textA ?? string.Empty, textB ?? string.Empty);
                return true;
            }

            AiActorView actorA;
            AiActorView actorB;
            if (blackboard.TryGet(new AiBlackboardKey<AiActorView>(keyA), out actorA)
                && blackboard.TryGet(new AiBlackboardKey<AiActorView>(keyB), out actorB))
            {
                // actor 按名字比：用来判"两个键指向的是不是同一个玩家/生物"
                comparison = string.CompareOrdinal(actorA.Name ?? string.Empty, actorB.Name ?? string.Empty);
                return true;
            }

            return false;
        }

        /// <summary>数值：float 与 int 都算（同一格只可能是其中一种）。</summary>
        private static bool TryNumber(AiBlackboard blackboard, string key, out double value)
        {
            float asFloat;
            if (blackboard.TryGet(key, out asFloat))
            {
                value = asFloat;
                return true;
            }

            int asInt;
            if (blackboard.TryGet(new AiBlackboardKey<int>(key), out asInt))
            {
                value = asInt;
                return true;
            }

            value = 0.0;
            return false;
        }

        private static bool CompareSign(int comparison, string op)
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

    /// <summary>
    /// 强制失败（`ForceSuccess` 的反面）：把 Succeeded 改成 Failed，Aborted / InProgress 不动。
    ///
    /// 以前只能拿 `Inverter + ForceSuccess` 拼（还要注意两者顺序），物料区少这一块时
    /// 用户很难猜到该怎么拼；现在直接给一个。
    /// </summary>
    public sealed class BtForceFailureDecorator : BtDecorator
    {
        public override string NodeType
        {
            get { return "ForceFailure"; }
        }

        protected override bool EvaluateCondition(BtContext context)
        {
            return true;
        }

        public override BtResult ModifyResult(BtContext context, BtResult result)
        {
            return result == BtResult.Succeeded ? BtResult.Failed : result;
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
