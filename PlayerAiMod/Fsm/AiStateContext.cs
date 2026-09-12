using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 状态执行上下文：状态只通过它读写黑板、访问传感器/执行器、请求转移。
    ///
    /// 约定：
    ///   · 每次 Tick 由状态机新建，状态不得跨帧缓存它（避免拿到过期引用）。
    ///   · <see cref="Raise"/> 只入队，不立刻换状态 —— 转移一律在 tick 边界统一裁决，
    ///     这样一次 Tick 中途的多次 Raise 不会造成半初始化状态。
    ///   · 组合状态内部的子机 tick 会拿到 Machine = 子机 的上下文；要交回父机时用
    ///     <c>context.Host</c> 或组合状态提供的交接回调。
    /// </summary>
    public sealed class AiStateContext
    {
        internal AiStateContext(AiStateMachine machine, IAiTreeHost host, float deltaTime)
        {
            Machine = machine;
            Host = host;
            DeltaTime = deltaTime < 0f ? 0f : deltaTime;
        }

        /// <summary>正在执行的状态所属的状态机（子机 tick 时是子机）。</summary>
        public AiStateMachine Machine { get; }

        /// <summary>
        /// 本状态机所服务的 AI 宿主；未绑定时为 null。
        /// 用接口而不是具体的 <see cref="AiActor"/>：模式层因此不依赖游戏类型，可以在无游戏环境下自检。
        /// </summary>
        public IAiTreeHost Host { get; }

        public AiBlackboard Blackboard
        {
            get { return Host != null ? Host.Blackboard : null; }
        }

        public IAiSensor Sensors
        {
            get { return Host != null ? Host.Sensors : null; }
        }

        public IAiActuator Actuators
        {
            get { return Host != null ? Host.Actuators : null; }
        }

        public float DeltaTime { get; }

        public float TimeInState
        {
            get { return Machine != null ? Machine.TimeInState : 0f; }
        }

        public float TimeInMachine
        {
            get { return Machine != null ? Machine.TimeInMachine : 0f; }
        }

        public string CurrentStateId
        {
            get { return Machine != null ? Machine.CurrentStateId : null; }
        }

        /// <summary>请求一次触发式转移（在下一个 tick 边界裁决）。</summary>
        public void Raise(string trigger)
        {
            if (Machine != null)
                Machine.Raise(trigger);
        }

        /// <summary>立刻转移到指定状态（跳过转移表）。仅用于安全回退与外部强制指令。</summary>
        public void ForceTransition(string stateId, string reason)
        {
            if (Machine != null)
                Machine.ForceTransition(stateId, reason);
        }

        public void Log(string message)
        {
            // 热路径不刷日志：只有打开详细日志时才写。
            if (PlayerAiConfig.VerboseLogging)
                Engine.Log.Information("[PlayerAi] " + message);
        }

        public void Warn(string message)
        {
            Engine.Log.Warning("[PlayerAi] " + message);
        }
    }
}
