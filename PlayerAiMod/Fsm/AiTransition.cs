using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 一条转移定义：<c>From --[Trigger + Guard]--> To</c>。
    ///
    /// 判定顺序（见 AiStateMachine.SelectTransition）：
    ///   1. From 匹配（具体状态优先于 <see cref="AiStateMachine.AnyState"/> 全局面转移）
    ///   2. Trigger 匹配（<c>null</c> 表示"只看守卫"的条件式转移）
    ///   3. 守卫 Guard 为真
    ///   4. 源状态已驻留 ≥ MinDwellSeconds（含全局下限）
    ///   5. 本条转移自身冷却已过
    ///   6. 源状态允许被打断（当 RequiresInterruptible 为真时）
    /// 同优先级下，先注册的赢。
    /// </summary>
    public sealed class AiTransition
    {
        public AiTransition(string from, string trigger, string to)
        {
            if (string.IsNullOrEmpty(from))
                throw new ArgumentException("Transition source must not be empty.", nameof(from));
            if (string.IsNullOrEmpty(to))
                throw new ArgumentException("Transition target must not be empty.", nameof(to));
            From = from;
            Trigger = trigger;
            To = to;
        }

        /// <summary>来源状态 ID，或 <see cref="AiStateMachine.AnyState"/> 表示全局面转移。</summary>
        public string From { get; }

        /// <summary>触发名；<c>null</c> 表示不依赖触发、每帧只看守卫的条件式转移。</summary>
        public string Trigger { get; }

        /// <summary>目标状态 ID，或特殊目标 <c>&lt;previous&gt;</c> / <c>&lt;pop&gt;</c>。</summary>
        public string To { get; }

        /// <summary>守卫：返回 false 则本条不可用。null = 无条件。</summary>
        public Func<AiStateContext, bool> Guard { get; set; }

        /// <summary>优先级：数值大的先判。同优先级先注册者优先。</summary>
        public int Priority { get; set; }

        /// <summary>
        /// 源状态最短驻留秒数，用于抑制抖动：
        /// &gt; 0 本条显式指定；== 0 使用全局下限（PlayerAiConfig.DefaultMinDwellSeconds）；
        /// &lt; 0 本条显式关闭限制（"做完立刻交棒"的转移用这个）。
        /// </summary>
        public float MinDwellSeconds { get; set; }

        /// <summary>本条转移的冷却秒数（0 = 无冷却）。</summary>
        public float CooldownSeconds { get; set; }

        /// <summary>为真时要求源状态 <see cref="AiState.IsInterruptible"/>，否则本条不可用。</summary>
        public bool RequiresInterruptible { get; set; } = true;

        /// <summary>备注：写清"什么情况下该走这条"，调试与复盘时非常有用。</summary>
        public string Description { get; set; }

        /// <summary>上次命中时状态机秒表读数（由状态机维护）。</summary>
        internal float LastFiredClock { get; set; } = float.NegativeInfinity;

        public override string ToString()
        {
            string trigger = Trigger == null ? "(condition)" : Trigger;
            return From + " --[" + trigger + "]--> " + To
                + (Priority != 0 ? " (p" + Priority.ToString() + ")" : string.Empty);
        }
    }
}
