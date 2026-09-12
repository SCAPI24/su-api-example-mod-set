using Engine;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 行为图（**模式层**，计划 §3.5）。
    ///
    /// P0-8 起这里**只表达模式/生命周期**，不再表达任何决策：
    /// <code>
    /// Inactive --start--> Idle --(有树)--> Tree --(树卸下)--> Idle
    ///        \--stop/failed/release（全局兜底，任何模式）-----------> Inactive
    ///                   Tree --(树 tick 出错)------------------------> Inactive
    /// </code>
    /// "看向 basil → 走近 → 待机"这类具体行为**全部搬进了行为树包**
    /// （出厂示例 `demo.greet.scbtpak`），改行为改包即可，不必改代码。
    ///
    /// 转移表本身在 <see cref="AiModeGraph"/>（可被自检直接核对），这里只负责组装。
    /// </summary>
    public static class PlayerAiBehaviour
    {
        /// <summary>建立模式层状态机（含启动自检）。宿主由 <see cref="AiActor"/> 稍后 bind。</summary>
        public static AiStateMachine Create(string machineId = "player")
        {
            var machine = new AiStateMachine(string.IsNullOrEmpty(machineId) ? "player" : machineId);

            machine.Register(
                new AiInactiveState(),
                new AiIdleState(),
                new AiTreeState(),
                new AiRecordingState());

            // 初始模式是"未接管"：接管动作显式发生（组件启用或控制面 ai.enable），避免装上 Mod 就动手。
            machine.SetInitialState(AiStateIds.Inactive);

            AiModeGraph.Apply(machine);

            IReadOnlyList<string> problems = machine.Validate();
            for (int i = 0; i < problems.Count; i++)
                Log.Warning("[PlayerAi] mode layer check: " + problems[i]);

            if (PlayerAiConfig.VerboseLogging)
            {
                List<string> graph = machine.DescribeGraph();
                for (int i = 0; i < graph.Count; i++)
                    Log.Information("[PlayerAi] mode graph: " + graph[i]);
            }

            return machine;
        }
    }
}
