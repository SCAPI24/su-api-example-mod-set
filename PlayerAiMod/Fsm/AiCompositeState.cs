using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 组合状态：一个状态内部再挂一台子状态机，用来表达"分层"的复杂切换。
    ///
    /// 例：
    /// <code>
    /// Root
    ///  ├── Idle
    ///  ├── Combat            &lt;- 组合状态，Child 里再分 Approach / Attack / Retreat
    ///  └── Build             &lt;- 组合状态，Child 里再分 PickBlock / PlaceBlock / Verify
    /// </code>
    ///
    /// 语义：
    ///   · 子机的 <c>Raise</c> 只作用于子机；要交回父机，用 <see cref="OnChildTick"/> 返回父机触发名，
    ///     或者在派生类里直接 <c>context.Machine.Raise(...)</c>（context 来自父机）。
    ///   · 子机 faulted 时自动向父机请求 <see cref="AiTriggers.Failed"/>。
    ///   · 每次进入组合状态，子机都从初始状态重新开始（<see cref="AiStateMachine.Reset"/>）。
    /// </summary>
    public abstract class AiCompositeState : AiState
    {
        private bool m_configured;

        protected AiCompositeState(string id)
            : base(id)
        {
            Child = new AiStateMachine(id + "/child");
        }

        /// <summary>内嵌子状态机：注册子状态与子转移。</summary>
        public AiStateMachine Child { get; }

        /// <summary>配置子状态机（只在第一次进入时调用一次）。</summary>
        protected abstract void ConfigureChild(AiStateMachine child);

        public override void Enter(AiStateContext context)
        {
            Child.Bind(context.Host);
            if (!m_configured)
            {
                ConfigureChild(Child);
                m_configured = true;
            }

            Child.Reset();
            Child.Start();
            base.Enter(context);
        }

        public override void Tick(AiStateContext context)
        {
            Child.Tick(context.DeltaTime);

            if (Child.IsFaulted)
            {
                context.Warn("Composite state '" + Id + "' child machine faulted -> parent failure.");
                context.Raise(AiTriggers.Failed);
                return;
            }

            string handOff = OnChildTick(context);
            if (!string.IsNullOrEmpty(handOff))
                context.Raise(handOff);
        }

        /// <summary>
        /// 子机 tick 之后的交接点：返回非空触发名即向父机请求转移，返回 null 表示继续留在本组合状态。
        /// 典型用法：<c>if (Child.CurrentStateId == ChildStateIds.Done) return AiTriggers.Done;</c>
        /// </summary>
        protected virtual string OnChildTick(AiStateContext context)
        {
            return null;
        }
    }
}
