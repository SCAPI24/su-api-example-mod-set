namespace PlayerAiMod
{
    /// <summary>
    /// "行为树宿主"：接收并执行行为树的对象（真实实现是 <see cref="AiActor"/>，自检里用假件）。
    ///
    /// 为什么要这层抽象：控制面命令（`ai.*`）与热重载都只跟"宿主"打交道，
    /// 于是命令层可以在**不依赖游戏类型**的情况下自检 —— 这是我能在不重启游戏的前提下
    /// 验证 `ai.tree.load / reload / pause` 的前提。
    /// </summary>
    public interface IAiTreeHost
    {
        /// <summary>宿主名（真实实现里就是玩家名）。</summary>
        string HostName { get; }

        /// <summary>
        /// 宿主种类（稳定的英文名，控制面 JSON 用它）：
        /// `"player"` = 接管了一个真实角色（世界里）；`"menu"` = 无角色宿主（主菜单，
        /// 只能注入 UI 点击）。编辑器靠它显示"（主菜单，无角色）"这种诚实的状态。
        /// </summary>
        string HostKind { get; }

        /// <summary>是否由 AI 接管（false = 不产生任何动作）。</summary>
        bool Enabled { get; set; }

        /// <summary>现在能不能跑（世界就绪、角色活着、是本端玩家…）。</summary>
        bool IsReady { get; }

        /// <summary>当前模式（未接管 / 待机 / 运行树 / 暂停 / 故障）。</summary>
        AiMode Mode { get; }

        /// <summary>
        /// 是否正在录制动作包（P0-9）。录制中行为树**不被 tick**、AI 输入被释放，
        /// 人类的操作照常生效并被记录 —— 这正是"录一段人的操作"的前提。
        /// </summary>
        bool IsRecording { get; }

        /// <summary>该宿主的行为树运行时（永远不为 null，没树时是空树）。</summary>
        BtRuntime Tree { get; }

        AiBlackboard Blackboard { get; }

        /// <summary>只读观察层（模式层与状态通过它看世界）。</summary>
        IAiSensor Sensors { get; }

        /// <summary>玩家控制器动作层（模式层与状态通过它动角色）。</summary>
        IAiActuator Actuators { get; }

        /// <summary>是否已经装载了行为树（区别于"运行时存在"）。</summary>
        bool HasTree { get; }

        /// <summary>装载并（可选）启动一棵编译好的树。返回是否成功。</summary>
        bool LoadTree(CompiledTree compiled, bool start);

        /// <summary>卸下当前树：正常收尾（释放输入）+ 回到待机模式。</summary>
        void StopTree(string reason);

        /// <summary>立刻释放该宿主注入的输入（禁用、故障、卸载时都要调）。</summary>
        void ReleaseInput();

        // 模式层（状态机）的只读摘要 —— 命令层要显示"卡在哪一层"，但不该认识游戏类型。

        /// <summary>模式层当前状态 id（没有状态机时为空）。</summary>
        string MachineStateId { get; }

        string MachinePreviousStateId { get; }

        float MachineTimeInState { get; }

        int MachineTransitionCount { get; }

        /// <summary>模式层是否已进入故障态（异常隔离后停机）。</summary>
        bool MachineFaulted { get; }
    }
}
