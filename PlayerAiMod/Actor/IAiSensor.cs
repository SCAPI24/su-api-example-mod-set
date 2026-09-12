using Engine;

namespace PlayerAiMod
{
    /// <summary>观察到的某个角色/实体的只读快照（值类型，可安全跨帧保存）。</summary>
    public struct AiActorView
    {
        public string Name;
        public int PlayerIndex;
        public Vector3 Position;
        public Vector3 Velocity;
        public float Distance;
        public bool IsSelf;
        public bool IsPlayer;

        public override string ToString()
        {
            return (Name ?? "<unnamed>") + (IsSelf ? "(self)" : string.Empty)
                + " pos=" + Position.ToString()
                + " dist=" + Distance.ToString("0.00");
        }
    }

    /// <summary>
    /// 只读观察层。铁律：传感器**只读**，绝不写游戏状态。
    ///
    /// 允许：无限读取（世界/实体/背包/界面/事件）。优势而非特权 —— 读得再快也不改变游戏。
    /// </summary>
    public interface IAiSensor
    {
        /// <summary>世界与角色是否就绪（有 Project、有 ComponentBody 等）。</summary>
        bool IsReady { get; }

        /// <summary>
        /// 本帧注入的输入是否真的会被游戏采纳。
        /// Source: Survivalcraft/Game/ComponentInput.cs:91-94 —— 窗口不在前台或 PlayerData 未就绪时，
        /// 游戏会把 PlayerInput 整个置空；此时"按了键但没反应"，AI 必须能看出来，否则会误判。
        /// </summary>
        bool IsInputAccepted { get; }

        string PlayerName { get; }

        Vector3 Position { get; }
        Vector3 EyePosition { get; }
        Vector3 Velocity { get; }

        /// <summary>水平朝向（弧度），取自身体旋转。</summary>
        float YawRadians { get; }

        /// <summary>俯仰角（弧度）。需要读 ComponentLocomotion 的私有后端字段，移植输入层时一起补。</summary>
        bool TryGetPitch(out float pitchRadians);

        float Health { get; }

        /// <summary>按玩家名查找（大小写不敏感）。联机时远端玩家也是普通 ComponentPlayer。</summary>
        bool TryFindPlayer(string name, out AiActorView view);

        /// <summary>找最近的其它玩家。</summary>
        bool TryFindNearestPlayer(out AiActorView view);
    }
}
