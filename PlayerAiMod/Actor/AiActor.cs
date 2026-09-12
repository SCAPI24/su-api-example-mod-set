using Engine;
using Game;
using GameEntitySystem;
using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 玩家 AI 角色：把一个 <see cref="ComponentPlayer"/> 绑定为"可被 AI 操作的玩家"，
    /// 持有黑板、状态机与传感器/执行器两层。
    ///
    /// 与游戏的关系：角色不是新实体，而是给已有玩家加一层决策。决策只产出"输入意图"，
    /// 由执行器（默认是复用 CmdBridgeMod 注入器的 <see cref="CmdBridgeActuator"/>）
    /// 落到玩家控制器上 —— 绝不直接改游戏状态。
    ///
    /// 本阶段只接管**本端玩家**：远端角色的操作必须在远端执行、本端只做复现，
    /// 在主机端直接改远端角色会两边不同步（见 <see cref="IsLocalPlayer"/>）。
    /// </summary>
    public sealed class AiActor : IAiTreeHost
    {
        private bool m_releasedForNotReady;
        private readonly BtRuntime m_tree;

        public AiActor(
            ComponentPlayer player,
            IAiSensor sensors = null,
            IAiActuator actuators = null,
            Func<AiActor, AiStateMachine> machineFactory = null)
        {
            Player = player;
            Blackboard = new AiBlackboard();
            Sensors = sensors ?? new PlayerSensor(this);
            Actuators = actuators ?? CreateDefaultActuator(this);

            // 先备好黑板/传感器/执行器，再让工厂建状态机 —— 工厂里会用到它们。
            // 模式层：默认由 PlayerAiBehaviour 组装（未接管 / 待机 / 运行树 / 录制中）。
            // 决策不在这里 —— 决策在行为树包里。
            Machine = machineFactory != null
                ? machineFactory(this)
                : PlayerAiBehaviour.Create("actor:" + Name);
            Machine.Bind(this);

            // 行为树运行时与状态机共用同一套黑板/传感器/执行器：
            // 于是"树改的参数"和"状态机读的黑板"是同一份数据，模式层与决策层不会各说各话。
            m_tree = new BtRuntime(Blackboard, Sensors, Actuators);
        }

        public ComponentPlayer Player { get; }

        /// <summary>玩家名（联机时用于区分角色；PlayerData.Name）。</summary>
        public string Name
        {
            get
            {
                try
                {
                    return Player != null && Player.PlayerData != null ? Player.PlayerData.Name : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public int PlayerIndex
        {
            get
            {
                try
                {
                    return Player != null && Player.PlayerData != null ? Player.PlayerData.PlayerIndex : -1;
                }
                catch (Exception)
                {
                    return -1;
                }
            }
        }

        public AiBlackboard Blackboard { get; }

        public AiStateMachine Machine { get; }

        public IAiSensor Sensors { get; }

        public IAiActuator Actuators { get; }

        /// <summary>是否由 AI 接管这个角色。默认关闭，避免装上 Mod 就抢玩家操作。</summary>
        public bool Enabled { get; set; }

        // ---------------------------------------------------------------- 行为树宿主（IAiTreeHost）

        /// <summary>行为树运行时（没装树时是一棵空树，永远不为 null）。</summary>
        public BtRuntime Tree
        {
            get { return m_tree; }
        }

        /// <summary>
        /// 当前模式（未接管 / 待机 / 运行树 / 暂停 / 故障）。
        /// **从模式层状态推导**，不由各处手工维护 —— 于是"控制面直接往运行时塞树"也不会模式不同步。
        /// </summary>
        public AiMode Mode
        {
            get
            {
                bool paused = PlayerAiRuntime.Instance != null && PlayerAiRuntime.Instance.Paused;
                bool faulted = Machine.IsFaulted
                    || (m_tree != null && m_tree.LastError != null);
                return AiModeMap.FromState(Machine.CurrentStateId, paused, faulted);
            }
        }

        /// <summary>是否已经装载了行为树（看运行时的树标识，避免两处标志不同步）。</summary>
        public bool HasTree
        {
            get { return m_tree.TreeId != null || m_tree.SourcePackage != null; }
        }

        /// <summary>是否正在录制动作包（全局会话，由 PlayerAiRuntime 持有）。</summary>
        public bool IsRecording
        {
            get
            {
                PlayerAiRuntime runtime = PlayerAiRuntime.Instance;
                return runtime != null && runtime.Recording.IsActive;
            }
        }

        public string HostName
        {
            get { return Name; }
        }

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

        /// <summary>
        /// 装载并（可选）启动一棵编译好的树。装载成功后角色进入"运行树"模式：
        /// **树的决策优先于旧 FSM 示例**（状态机退居模式层，见 doc/player-ai-plan.md §3.5）。
        /// </summary>
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

        /// <summary>加载后是否已经"在跑树"（模式层下一帧会推导到运行树模式）。</summary>
        public bool IsTreeRunning
        {
            get { return HasTree && m_tree.IsRunning; }
        }

        /// <summary>卸下当前树：正常收尾（释放输入）并回到待机。</summary>
        public void StopTree(string reason)
        {
            m_tree.Stop(reason ?? "tree stopped");
            m_tree.ReplaceRoot(null, false);
            ReleaseInput();
        }

        /// <summary>释放该角色注入的全部输入。</summary>
        public void ReleaseInput()
        {
            if (Actuators != null)
                Actuators.ReleaseAll();
        }

        /// <summary>
        /// 是否是本端玩家（判定实现见 <see cref="PlayerAiRuntime.IsLocalPlayerOf"/>）。
        /// 远端角色必须在远端执行、本端复现，不能在本机直接操控。
        /// </summary>
        public bool IsLocalPlayer
        {
            get { return PlayerAiRuntime.IsLocalPlayerOf(Player); }
        }

        /// <summary>角色是否可以运行：世界就绪、活着、传感器可用、且（按配置）必须是本端玩家。</summary>
        public bool IsReady
        {
            get
            {
                if (Player == null || Player.ComponentHealth == null || Player.ComponentBody == null)
                    return false;
                if (!PlayerAiConfig.Enabled)
                    return false;
                if (Player.ComponentHealth.Health <= 0f)
                    return false;
                if (PlayerAiConfig.OnlyLocalPlayer && !IsLocalPlayer)
                    return false;
                return Sensors != null && Sensors.IsReady;
            }
        }

        /// <summary>本帧输入是否真会被游戏采纳（窗口前台 + PlayerData 就绪）。</summary>
        public bool IsInputAccepted
        {
            get { return Sensors != null && Sensors.IsInputAccepted; }
        }

        /// <summary>
        /// 每帧在帧首调用一次。**一律由模式层驱动**：
        /// 模式层决定"现在该不该动、谁来动"（待机 / 运行树 / 未接管），具体决策在树里。
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (!Enabled)
            {
                if (Machine.CurrentStateId != AiStateIds.Inactive)
                    Machine.Reset();
                return;
            }

            if (!IsReady)
            {
                // 不可用期间不推进决策：停掉模式层与树并释放输入，避免"按住 W 却没人管"。
                if (!m_releasedForNotReady)
                {
                    Machine.Reset();
                    if (HasTree)
                        m_tree.Stop("actor not ready");
                    ReleaseInput();
                    m_releasedForNotReady = true;
                }
                return;
            }

            m_releasedForNotReady = false;

            // 自愈：接管中却停在"未接管"（首帧顺序、世界重载、故障回退都可能造成）→ 补一次接管请求。
            // 模式层的目标状态由现实决定，不该依赖"某次 Raise 有没有赶上第一帧"。
            if (Machine.CurrentStateId == AiStateIds.Inactive)
                Machine.Raise(AiTriggers.Start);

            Machine.Tick(deltaTime);
        }

        /// <summary>接管角色（模式层从"未接管"进入"待机/运行树"）。</summary>
        public void Enable(string reason)
        {
            Enabled = true;
            m_releasedForNotReady = false;
            Machine.Raise(AiTriggers.Start);

            if (PlayerAiConfig.VerboseLogging)
                Engine.Log.Information("[PlayerAi] enable (" + (reason ?? "?") + "): " + this);
        }

        /// <summary>关闭接管：走模式层的急停转移（进未接管态并释放全部输入）。</summary>
        public void Disable()
        {
            Enabled = false;
            Machine.Raise(AiTriggers.Stop);

            // 急停要立刻生效，不能等下一帧的模式层 tick。
            if (HasTree)
                m_tree.Stop("actor disabled");
            ReleaseInput();
            Machine.ForceTransition(AiStateIds.Inactive, "disabled");
        }

        public AiStateMachineSnapshot Snapshot()
        {
            return Machine.Snapshot();
        }

        /// <summary>
        /// 默认执行器：能用 CmdBridgeMod 的注入器就用它，否则退回占位实现（只记意图、不动玩家）。
        /// </summary>
        private static IAiActuator CreateDefaultActuator(AiActor actor)
        {
            if (CmdBridgeActuator.IsAvailable)
                return new CmdBridgeActuator(actor);
            return new PlaceholderActuator(actor);
        }

        public override string ToString()
        {
            return "AiActor(" + (Name ?? "?") + " idx=" + PlayerIndex.ToString()
                + " local=" + IsLocalPlayer.ToString()
                + " enabled=" + Enabled.ToString() + ")";
        }
    }
}
