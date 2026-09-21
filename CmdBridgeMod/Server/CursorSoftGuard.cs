using Game;
using GameEntitySystem;

namespace CmdBridgeMod
{
    /// <summary>
    /// 把"失焦时绝不动真实鼠标"的断言放到**正确的那一瞬**：注册进当前项目的
    /// `SubsystemUpdate`，`UpdateOrder` 取 `UpdateOrder.Input - 1`（= -11，枚举里没被占用），
    /// 紧贴在 `ComponentInput` 之前执行。
    ///
    /// 为什么必须放在这里（实测链）：
    ///   · `GameWidget.Update()` 每帧都会按输入设备把本层输入面的 `UseSoftMouseCursor`
    ///     强制置 false（`GameWidget.cs:108-115`，普通鼠标走 else 分支）——在帧首断言会被它覆盖；
    ///   · 紧接着，`ComponentInput.UpdateInputFromMouseAndKeyboard` 在"3D 视角 → 鼠标光标"的
    ///     一次性转换里写 `input.MousePosition = 视角中心`（`ComponentInput.cs:145-151`）；
    ///   · `WidgetInput.MousePosition` 的 setter 在软光标关闭时调 `Mouse.SetMousePosition`
    ///     （`WidgetInput.cs:221-224` → `Mouse.cs:31-34`，全工程**唯一**会挪真实光标的地方）。
    /// 于是表现就是"弹出游戏统计时，真实鼠标被一下拽到游戏里"。
    /// 本类在 ComponentInput 之前把软光标重新置真，引擎怎么写都只动虚拟光标；真实鼠标纹丝不动。
    /// 只在真失焦（`realFocus == false`）时生效，恢复焦点后由 FocusPolicy 切回真实鼠标。
    /// </summary>
    internal sealed class CursorSoftGuard : IUpdateable
    {
        private readonly InputInjector m_injector;

        public CursorSoftGuard(InputInjector injector)
        {
            m_injector = injector;
        }

        public UpdateOrder UpdateOrder => (UpdateOrder)((int)UpdateOrder.Input - 1);

        public void Update(float dt)
        {
            m_injector?.Focus?.ApplyUnfocusedSoftCursor();
        }
    }
}
