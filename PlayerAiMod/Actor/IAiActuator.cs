using Engine;

namespace PlayerAiMod
{
    /// <summary>
    /// 玩家控制器动作层。铁律：**只允许注入输入层**，不允许直接改游戏状态。
    ///
    /// 允许（＝真人也能做，只是做得慢）：
    ///   视角（yaw/pitch）、键盘按下/松开/脉冲、鼠标按键、滚轮、UI 点击。
    /// 禁止：
    ///   直接写生命/背包/方块/位置/时间，跳过界面层级，跳过交互前置条件。
    ///
    /// 所有实现都必须把写入安排在**帧首**（见 PlayerAiRuntime 的时序说明），
    /// 因为 Keyboard/Mouse 的 downOnce 数组会在帧末被清空。
    /// </summary>
    public interface IAiActuator
    {
        /// <summary>执行器是否可用（输入层是否已接入）。占位实现永远返回 false。</summary>
        bool IsReady { get; }

        /// <summary>瞬时转向指定 yaw（弧度）。人手做不到的角速度，属于"优势"。</summary>
        void Look(float yawRadians, float pitchRadians);

        /// <summary>看向世界坐标点。</summary>
        void LookAt(Vector3 worldPoint);

        /// <summary>
        /// 视角增量（弧度）—— 与引擎把 `PlayerInput.Look` 加进 lookAngles 的量同单位。
        /// 动作包回放用它还原"人这一帧转了多少"，而不是去猜鼠标计数（灵敏度设置不影响回放）。
        /// </summary>
        void LookDelta(float yawRadians, float pitchRadians);

        /// <summary>按住/松开某个键（跨帧有效）。</summary>
        void HoldKey(string key, bool down);

        /// <summary>按一下某个键（holdMilliseconds 为按住时长，0 = 一帧）。</summary>
        void PulseKey(string key, int holdMilliseconds);

        /// <summary>鼠标按键按下/松开。</summary>
        void MouseButton(string button, bool down);

        /// <summary>鼠标点击（按下 + 松开，两帧完成）。</summary>
        void MouseClick(string button, int holdMilliseconds);

        /// <summary>滚轮（正 = 向上；用于切快捷栏/列表）。</summary>
        void Wheel(int delta);

        /// <summary>
        /// 引擎内 UI 点击（按控件路径；`selectorOrPoint` 形如 `[Screen]/…/Play` 或 `x,y`）。
        /// 菜单/背包这类操作在原始输入层里没有痕迹，只能这样重放 —— 走的仍是 CM-1 的软光标路径，
        /// 不碰物理鼠标，也不跳级（不可点就如实失败）。
        /// 返回 false = 注入器拒绝了这次点击（元素不在/被挡住/未启用），调用方应如实记录，不能当成功。
        /// </summary>
        bool UiClick(string selectorOrPoint);

        /// <summary>释放全部按键与鼠标状态。禁用、失焦、卸载、进入安全状态时必须调用。</summary>
        void ReleaseAll();
    }
}
