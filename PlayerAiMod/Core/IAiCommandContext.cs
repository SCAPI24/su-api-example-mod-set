namespace PlayerAiMod
{
    /// <summary>
    /// 命令层需要的"运行时门面"。**只暴露命令真正会用的东西**，
    /// 于是命令语义不依赖游戏类型，能在没有游戏的临时工程里逐条自检。
    /// 真实实现由 <see cref="PlayerAiRuntime"/> 提供（见 `Core/AiCommandBridge.cs`）。
    /// </summary>
    public interface IAiCommandContext
    {
        /// <summary>行为树宿主（通常是本端玩家角色）；没有可接管角色时为 null。</summary>
        IAiTreeHost Host { get; }

        /// <summary>推送式重载器（包目录不可用时为 null）。</summary>
        PackageReloader Reloader { get; }

        /// <summary>树库：预编译常驻 + 毫秒级切换（包目录不可用时为 null）。</summary>
        TreeLibrary Library { get; }

        /// <summary>事件日志（P0-10）：改写/导出/自检等动作留痕（可为 null）。</summary>
        AiEventLog EventLog { get; }

        /// <summary>行为树是否整体暂停（`Home` 或 `ai.pause`）。</summary>
        bool Paused { get; }

        string PauseReason { get; }

        void Pause(string reason);

        void Resume(string reason);

        /// <summary>输入注入是否可用（CmdBridgeMod 已加载且启用注入）。</summary>
        bool InputAvailable { get; }

        /// <summary>录制会话（`ai.record.*`）。真实实现是 <see cref="AiRecordingSession"/>。</summary>
        IAiRecordingControl Recording { get; }

        /// <summary>动作包播放（`ai.action.play/stop`）。可为 null（没有可播放的宿主）。</summary>
        IAiActionPlayer ActionPlayer { get; }
    }
}
