using System;

namespace PlayerAiMod
{
    /// <summary>
    /// **控制器宿主**：行为树真正绑定的对象。**它不是角色**（用户明确要求："行为树不应该绑定到
    /// 角色上，而是角色的控制器"）—— 角色会随世界来来去去，控制器一直在这儿。
    ///
    /// <code>
    ///                         ┌──────────── ControllerTreeHost ────────────┐
    ///   行为树 / 黑板 / 模式层  │  树运行时 + 黑板 + 模式层（未接管/待机/运行树/录制/暂停/故障） │
    ///   输入路由              │  ControllerActuator ──[有世界]──> AiActor 的玩家执行器        │
    ///                         │                     ──[无世界]──> UiOnlyActuator（只点 UI）      │
    ///   观察层                │  ControllerSensor   ──[有世界]──> PlayerSensor                │
    ///                         │                     ──[无世界]──> 无传感器（漂移检查跳过）      │
    ///                         └──────────────────────────────────────────────┘
    /// </code>
    ///
    /// 于是同一棵树可以跨越"主菜单 → 世界 → 主菜单"：中途只换**输入路由**（Bind/Unbind），
    /// 树本身既不卸载也不重跑，运行态一路保留 —— 这正是"进游戏前点按钮、退出世界后干别的"
    /// 这件事能成立的前提。
    ///
    /// 边界不变：只注入输入层（UI 点击 / 玩家控制器输入），不写任何游戏状态。
    /// </summary>
    internal sealed class ControllerTreeHost : IAiTreeHost
    {
        private readonly BtRuntime m_tree;
        private readonly ControllerActuator m_actuator;
        private readonly ControllerSensor m_sensor;
        private bool m_releasedForNotReady;
        private bool m_releasedForDisabled;
        private IPlayerInputProvider m_world;

        /// <param name="uiActuator">
        /// 世界外的输入兜底（生产上是 `UiOnlyActuator`；自检传假执行器，
        /// 这样"控制器宿主"能在没有游戏、没有 CmdBridge 的纯逻辑自检里被验证）。
        /// </param>
        public ControllerTreeHost(IAiActuator uiActuator)
        {
            Blackboard = new AiBlackboard();
            m_actuator = new ControllerActuator(uiActuator);
            m_sensor = new ControllerSensor();
            Actuators = m_actuator;
            Sensors = m_sensor;
            m_tree = new BtRuntime(Blackboard, Sensors, Actuators);

            // 模式层（未接管 / 待机 / 运行树 / 录制中）跟着控制器走，而不是跟着角色走：
            // 角色没了不等于"AI 该停"，只是"现在只能点 UI 了"。
            Machine = PlayerAiBehaviour.Create("controller");
            Machine.Bind(this);
        }

        // ---------------------------------------------------------------- 世界输入来源

        /// <summary>当前接上的世界输入来源（null = 主菜单 / 没有可驱动角色）。</summary>
        public IPlayerInputProvider World
        {
            get { return m_world; }
        }

        /// <summary>
        /// 接上/换下世界输入来源。**只换路由，不动树**（这是这一层存在的全部理由）。
        /// 幂等：同一个来源重复 Bind 什么都不做。
        /// </summary>
        public void Bind(IPlayerInputProvider world)
        {
            if (ReferenceEquals(m_world, world))
                return;

            m_actuator.Bind(world);
            m_sensor.Bind(world);
            m_world = world;
            m_releasedForNotReady = false;
        }

        /// <summary>现在有没有一个真能驱动机角色的输入来源。</summary>
        public bool HasWorld
        {
            get { return m_world != null; }
        }

        /// <summary>`"world"` / `"menu"`：控制面与编辑器用它说明"现在在哪一层操作"。</summary>
        public string Situation
        {
            get { return m_world != null && m_world.IsReady ? "world" : "menu"; }
        }

        /// <summary>被驱动的角色名（世界外为 null）。</summary>
        public string PlayerName
        {
            get { return m_world != null && m_world.IsReady ? m_world.Name : null; }
        }

        // ---------------------------------------------------------------- IAiTreeHost

        public string HostName
        {
            get { return "controller"; }
        }

        /// <summary>宿主种类：这是控制器，不是某个角色。</summary>
        public string HostKind
        {
            get { return "controller"; }
        }

        /// <summary>
        /// 能不能干活 = 至少有一条输入通道（世界里的玩家输入，或世界外的 UI 注入）。
        /// 两条都没有（CmdBridgeMod 不在）就如实报 not ready，而不是假装在跑。
        /// </summary>
        public bool IsReady
        {
            get { return Actuators != null && Actuators.IsReady && PlayerAiConfig.Enabled; }
        }

        /// <summary>接管开关（`ai.enable` / `ai.disable` 就是翻它）。</summary>
        public bool Enabled { get; set; }

        public AiMode Mode
        {
            get
            {
                if (!Enabled || !IsReady)
                    return AiMode.Inactive;
                if (Paused)
                    return AiMode.Paused;
                if (m_tree.LastError != null)
                    return AiMode.Fault;
                return HasTree ? AiMode.Tree : AiMode.Idle;
            }
        }

        /// <summary>
        /// 是否正在录制。由运行时每帧同步进来（同 <see cref="Paused"/> 的理由：
        /// 不直接引用 `PlayerAiRuntime`，这个类才能进纯逻辑自检）。
        /// </summary>
        public bool IsRecording { get; set; }

        public BtRuntime Tree
        {
            get { return m_tree; }
        }

        public AiBlackboard Blackboard { get; }

        public IAiSensor Sensors { get; }

        public IAiActuator Actuators { get; }

        public bool HasTree
        {
            get { return m_tree.TreeId != null || m_tree.SourcePackage != null; }
        }

        public bool LoadTree(CompiledTree compiled, bool start)
        {
            if (compiled == null || compiled.Root == null || compiled.HasErrors)
                return false;

            m_tree.ReplaceRoot(compiled.Root, false, compiled.PackageId,
                compiled.SourcePath, compiled.SourceHash);
            if (start)
                m_tree.Start();
            return true;
        }

        public void StopTree(string reason)
        {
            m_tree.Stop(reason ?? "tree stopped");
            m_tree.ReplaceRoot(null, false);
            ReleaseInput();
        }

        /// <summary>
        /// 显式释放（停止树 / 未就绪 / `ai.input.release`）：世界输入与 UI 注入都撤
        /// —— 这些时刻确实应该把在飞的软光标点击一起取消。
        /// （任务级的 `Actuators.ReleaseAll()` 不走这里，见 <see cref="ControllerActuator.ReleaseAll"/>。）
        /// </summary>
        public void ReleaseInput()
        {
            if (Actuators != null)
                Actuators.ReleaseAll();
            m_actuator.CancelUiInjection();
        }

        /// <summary>
        /// 模式层的状态机。**每帧由 <see cref="PlayerAiRuntime.TickFrameStart"/> 推进一次**
        /// （世界内外都推）—— 树的 tick 就在 `AiTreeState` 里，所以"有没有世界"不再决定树跑不跑。
        /// </summary>
        public AiStateMachine Machine { get; }

        /// <summary>
        /// 全局暂停。由运行时每帧同步进来（不直接读运行时单例，好让这个类进纯逻辑自检）。
        /// </summary>
        public bool Paused { get; set; }

        /// <summary>每帧帧首推进一次（世界内外都调）。</summary>
        public void Tick(float deltaTime)
        {
            if (!Enabled)
            {
                // 未接管：模式层回"未接管"，但**树不卸载**（`ai.disable` 只是停手，不是卸树）。
                if (Machine.CurrentStateId != AiStateIds.Inactive)
                    Machine.Reset();
                if (m_tree.IsRunning)
                    m_tree.Stop("controller disabled");

                // ⚠️ 只在"刚变成未接管"的**那一帧**释放一次，不能每帧释放。
                //
                // `ReleaseInput()` 会 `CancelUiInjection()`，而 UI 那套"释放"的语义是
                // **取消整个软光标注入会话**；一次软光标点击要走"移动 → 按下 → 抬起"好几个帧
                // （`UiMouseSession` 一步一帧），中途被清一次就永远派生不出 Click。
                // 这个分支在世界外是**每帧**都会走到的常态（启动后还没进世界、`ai.disable` 之后），
                // 于是表现就是"光标移过去了、界面纹丝不动"——菜单里播放入口树的 Play 点不动
                // （2026-09-12 实测，`host.enabled=false` + `situation=menu`）。
                if (!m_releasedForDisabled)
                {
                    ReleaseInput();
                    m_releasedForDisabled = true;
                }
                return;
            }

            m_releasedForDisabled = false;

            if (!IsReady)
            {
                // 一条输入通道都没有：停树 + 释放输入，避免"按住 W 却没人管"。
                if (!m_releasedForNotReady)
                {
                    Machine.Reset();
                    if (HasTree)
                        m_tree.Stop("controller not ready");
                    ReleaseInput();
                    m_releasedForNotReady = true;
                }
                return;
            }

            m_releasedForNotReady = false;

            // 自愈：接管中却停在"未接管"（首帧顺序、世界切换都可能造成）→ 补一次接管请求。
            if (Machine.CurrentStateId == AiStateIds.Inactive)
                Machine.Raise(AiTriggers.Start);

            Machine.Tick(deltaTime);
        }

        // 模式层摘要（控制面显示"卡在哪一层"）：

        public string MachineStateId
        {
            get { return Machine.CurrentStateId; }
        }

        public string MachinePreviousStateId
        {
            get { return Machine.PreviousStateId; }
        }

        public float MachineTimeInState
        {
            get { return Machine.TimeInState; }
        }

        public int MachineTransitionCount
        {
            get { return Machine.TransitionCount; }
        }

        public bool MachineFaulted
        {
            get { return Machine.IsFaulted; }
        }

        public AiStateMachineSnapshot Snapshot()
        {
            return Machine.Snapshot();
        }

        public override string ToString()
        {
            return "ControllerTreeHost(enabled=" + Enabled.ToString()
                + " ready=" + IsReady.ToString()
                + " situation=" + Situation
                + " tree=" + (HasTree ? m_tree.TreeId : "<none>") + ")";
        }
    }
}
