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
        /// 引擎内 UI 点击（**语义目标**；`selectorOrPoint` 形如 `Play`、`[Screen]/…/Play`、
        /// `list:WorldsList@世界名`、`list:WorldsList#0`，坐标只是最后兜底）。
        /// 坐标由 CmdBridge 的 UI 服务在**点击那一刻**现解析 —— 所以改窗口大小/UI 缩放都点不空
        /// （用户要求：能用 UI 的真实位置就别用录下来的像素）。
        /// 菜单/背包这类操作在原始输入层里没有痕迹，只能这样重放。
        /// 返回 false = 这次没点到（元素不在/被挡住/未启用），调用方应如实记录，不能当成功。
        /// </summary>
        bool UiClick(string selectorOrPoint);

        /// <summary>
        /// 同上，但指定点法（`mode`）：
        ///   · `direct`（默认）：单帧合成"按下→抬起"，引擎自己派生 `Tap`+`Click`，
        ///     控件自己的逻辑（`IsClicked`、列表选中、点击音）照常跑；
        ///   · `input`：多帧软光标会话（移动 → 按下 → 抬起，一步一帧）；
        ///   · `invoke`：直接触发控件自己的"按下事件"（能触发才有效，**绕过输入层**，
        ///     只给明确要这么做的场合用）。
        /// </summary>
        bool UiClick(string target, string mode);

        /// <summary>释放全部按键与鼠标状态。禁用、失焦、卸载、进入安全状态时必须调用。</summary>
        void ReleaseAll();
    }
}
