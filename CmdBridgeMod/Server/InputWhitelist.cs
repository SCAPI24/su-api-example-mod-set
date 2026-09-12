using System;

namespace CmdBridgeMod
{
    /// <summary>
    /// 命令执行失败（带机器可读错误码）。客户端据此区分"点不到"与"真失败"。
    /// </summary>
    internal sealed class BridgeCommandException : Exception
    {
        public BridgeCommandException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>
    /// 输入注入白名单 —— 本 Mod 唯一允许写入的游戏成员，全部位于"输入层"。
    ///
    /// 铁律（doc/cmd-bridge-plan.md §1.2/§1.4）：
    ///   优势 = 无限读取 + 超人输入速度（允许）
    ///   特权 = 直接改游戏状态（禁止）
    /// 因此本文件之外的任何 ModParentField/ModifyStaticField 调用都必须指向下列常量之一。
    /// </summary>
    internal static class InputWhitelist
    {
        // 键盘（Engine/Engine/Input/Keyboard.cs）
        internal const string KeyboardDownArray = "m_keysDownArray";
        internal const string KeyboardDownOnceArray = "m_keysDownOnceArray";
        internal const string KeyboardRepeatArray = "m_keysDownRepeatArray";
        internal const string KeyboardLastKey = "m_lastKey";
        internal const string KeyboardLastChar = "m_lastChar";

        // 鼠标（Engine/Engine/Input/Mouse.cs）
        internal const string MouseDownArray = "m_mouseButtonsDownArray";
        internal const string MouseDownOnceArray = "m_mouseButtonsDownOnceArray";
        internal const string MouseWheelMovement = "MouseWheelMovement";
        internal const string MouseLastWheelValue = "m_lastMouseWheelValue";

        // 视角俯仰（Survivalcraft/Game/ComponentLocomotion.cs，private set 的后端字段）
        internal const string LocomotionLookAngles = "m_lookAngles";

        // WidgetInput 的"按下起点"（Game/WidgetInput.cs）。合成点击后若它残留，
        // 派生字段 Click 会在后续每一帧继续生成，导致一次点击被消费多次。
        internal const string WidgetInputMouseDownPoint = "m_mouseDownPoint";

        // 焦点/共控（CM-2）：让引擎"以为"窗口活跃、保持系统光标可见、切断真实鼠标。
        // Source: Engine/Engine/Window.cs:218（IsActive => m_state == State.Active；枚举值 Uncreated/Inactive/Active）
        internal const string WindowState = "m_state";

        /// <summary>
        /// OpenTK 的 GameWindow：**真实焦点**的权威来源（`Window.m_gameWindow.Focused`）。
        /// 必须用它而不是 `Window.IsActive`/Activated 事件 —— 后者的状态位正是本 Mod 会改写的那个，
        /// 于是「我们强制活跃」会让焦点恢复事件永不触发（只读，不写）。
        /// </summary>
        public const string WindowGameWindow = "m_gameWindow";
        // Source: Engine/Engine/Input/Mouse.cs:9-21（私有后端字段与派生量）、:48-70（增量计算位置）
        internal const string MouseLastPosition = "m_lastMousePosition";
        internal const string MouseMovement = "MouseMovement";

        internal static readonly string[] All =
        {
            KeyboardDownArray,
            KeyboardDownOnceArray,
            KeyboardRepeatArray,
            KeyboardLastKey,
            KeyboardLastChar,
            MouseDownArray,
            MouseDownOnceArray,
            MouseWheelMovement,
            MouseLastWheelValue,
            LocomotionLookAngles,
            WidgetInputMouseDownPoint,
            WindowState,
            WindowGameWindow,
            MouseLastPosition,
            MouseMovement
        };

        internal static bool Contains(string memberName)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (string.Equals(All[i], memberName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
