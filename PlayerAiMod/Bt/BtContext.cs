using Engine;
using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 行为树执行上下文：节点通过它读黑板、访问传感器/执行器、记账单与时间。
    ///
    /// 与状态机那边的 <see cref="AiStateContext"/> 一样：**每次 Tick 复用同一个实例**，
    /// 节点不得跨帧保存它（只允许保存自己的运行态）。
    /// </summary>
    public sealed class BtContext
    {
        internal BtContext(
            BtRuntime runtime,
            AiBlackboard blackboard,
            IAiSensor sensors,
            IAiActuator actuators)
        {
            Runtime = runtime;
            Blackboard = blackboard;
            Sensors = sensors;
            Actuators = actuators;
        }

        public BtRuntime Runtime { get; }

        public AiBlackboard Blackboard { get; }

        public IAiSensor Sensors { get; }

        public IAiActuator Actuators { get; }

        /// <summary>本帧时间步长（秒）。</summary>
        public float DeltaTime { get; internal set; }

        /// <summary>树启动以来的累计游戏时间（秒）。</summary>
        public double Time { get; internal set; }

        /// <summary>本棵树的第几次 tick（从 1 开始）。</summary>
        public long TickIndex { get; internal set; }

        /// <summary>本帧已访问的节点数（用于帧预算）。</summary>
        public int NodesVisited { get; internal set; }

        /// <summary>帧预算是否已耗尽：耗尽时组合节点应停止本帧继续深入，返回 InProgress。</summary>
        public bool BudgetExhausted
        {
            get { return Runtime != null && NodesVisited >= Runtime.TickBudgetPerFrame; }
        }

        internal void Visit()
        {
            NodesVisited++;
        }

        /// <summary>
        /// 记一行（`Task.Log`）。
        ///
        /// **两路都写、用途不同**：
        ///   · **事件日志**（`BtLogSink` → `ai.logs`）—— 这是给人和复盘看的"决策痕迹"，
        ///     与 `VerboseLogging` 无关，默认就记（"Laya 说 craft → 跑 craft_once.aeact"）；
        ///   · **引擎日志**（`[PlayerAi][BT]`）—— 只在 `VerboseLogging` 打开时写，
        ///     因为它是每帧级别的高频输出，默认开会把 Game.log 刷满。
        /// </summary>
        public void Log(string message)
        {
            BtLogSink.Emit(BtLogSink.TreeKind, message);
            if (PlayerAiConfig.VerboseLogging)
                Engine.Log.Information("[PlayerAi][BT] " + message);
        }

        public void Warn(string message)
        {
            Engine.Log.Warning("[PlayerAi][BT] " + message);
        }
    }
}
