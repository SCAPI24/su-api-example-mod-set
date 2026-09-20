using Engine;
using Game;
using GameEntitySystem;
using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 玩家 AI 角色：把一个 <see cref="ComponentPlayer"/> 绑定成"可被 AI 操作的玩家"，
    /// 提供**世界里的输入来源**（传感器 + 执行器）。
    ///
    /// 与游戏的关系：角色不是新实体，而是给已有玩家加一层"可被驱动"的通道。决策只产出
    /// "输入意图"，由执行器（默认是复用 CmdBridgeMod 注入器的 <see cref="CmdBridgeActuator"/>）
    /// 落到玩家控制器上 —— 绝不直接改游戏状态。
    ///
    /// **它不再是行为树宿主**（用户明确要求："行为树不应该绑定到角色上，而是角色的控制器"）：
    /// 树、黑板、模式层都在 <see cref="ControllerTreeHost"/> 里，角色会随世界来来去去，
    /// 控制器一直在。角色只负责"我在场时，输入从这个口子出去"。
    ///
    /// 本阶段只接管**本端玩家**：远端角色的操作必须在远端执行、本端只做复现，
    /// 在主机端直接改远端角色会两边不同步（见 <see cref="IsLocalPlayer"/>）。
    /// </summary>
    public sealed class AiActor : IPlayerInputProvider
    {
        public AiActor(
            ComponentPlayer player,
            IAiSensor sensors = null,
            IAiActuator actuators = null)
        {
            Player = player;
            Sensors = sensors ?? new PlayerSensor(this);
            Actuators = actuators ?? CreateDefaultActuator(this);
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

        public IAiSensor Sensors { get; }

        public IAiActuator Actuators { get; }

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

        /// <summary>释放这个角色注入的全部输入（换绑、世界卸载、禁用时都要调）。</summary>
        public void ReleaseInput()
        {
            if (Actuators != null)
                Actuators.ReleaseAll();
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
                + " local=" + IsLocalPlayer.ToString() + ")";
        }
    }
}
