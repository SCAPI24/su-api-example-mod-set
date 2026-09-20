namespace PlayerAiMod
{
    /// <summary>
    /// "玩家输入来源"：世界里那个可以被驱动的东西（真实实现是 <see cref="AiActor"/>，
    /// 它包着一个 <c>ComponentPlayer</c>；自检里用假件）。
    ///
    /// 为什么单独抽一层：**行为树不绑定角色，绑定的是"控制器"**（用户明确要求）——
    /// 控制器在世上有输入来源时就把它接进来，没有（主菜单 / 刚退出世界）就退回只点 UI。
    /// 于是同一棵树可以从主菜单点进游戏、在世界里开人、退出世界后接着干别的，
    /// 中途不需要"换宿主"（换宿主就意味着树被卸下/重装、运行态丢失）。
    /// </summary>
    public interface IPlayerInputProvider
    {
        /// <summary>玩家名（没有名字时可能是 null）。</summary>
        string Name { get; }

        /// <summary>现在能不能真的驱动它（世界就绪、角色活着、是本端玩家…）。</summary>
        bool IsReady { get; }

        /// <summary>只读观察层（世界外为 null，见 <see cref="ControllerSensor"/>）。</summary>
        IAiSensor Sensors { get; }

        /// <summary>玩家控制器动作层（注入到输入层，绝不直接改游戏状态）。</summary>
        IAiActuator Actuators { get; }

        /// <summary>立刻释放它注入的输入（切换绑定、世界卸载、禁用时都要调）。</summary>
        void ReleaseInput();
    }
}
