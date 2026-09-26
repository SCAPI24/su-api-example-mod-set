using Engine;
using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 真实动作执行器：把动作层的**一帧动作输入**转交给 <see cref="IAiActuator"/>
    /// （后者再转交 CmdBridgeMod 的注入门面）。
    ///
    /// 单位约定（与既有代码一致，别搞混）：
    ///   · 动作层/verb 用**度**；
    ///   · <see cref="IAiActuator.Look"/> / <c>LookDelta</c> 用**弧度**（游戏原生）；
    ///   换算只在这一层做一次。
    ///
    /// 为什么本类必须单独存在：动作层要能在没有游戏的临时工程里自检（见 `ActionSelfTest`），
    /// 所以它只认 <see cref="IActionExecutor"/>；把"引擎类型 + 单位换算"关在这里。
    /// </summary>
    public sealed class GameActionExecutor : IActionExecutor
    {
        private const float DegreesToRadians = MathUtils.PI / 180f;

        private readonly IAiActuator m_actuator;

        public GameActionExecutor(IAiActuator actuator)
        {
            m_actuator = actuator;
        }

        /// <summary>底层执行器（观测/日志用）。</summary>
        public IAiActuator Actuator
        {
            get { return m_actuator; }
        }

        public bool IsReady
        {
            get { return m_actuator != null && m_actuator.IsReady; }
        }

        /// <summary>
        /// 底层用**方法内吞异常 + 返回值表示是否执行**的风格：这里统一成 false = 这次没执行，
        /// 由动作层的轨迹如实记账（不静默当成功）。
        /// </summary>
        private bool Try(Action<IAiActuator> body)
        {
            if (m_actuator == null || !m_actuator.IsReady)
                return false;
            try
            {
                body(m_actuator);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool HoldKeys(IReadOnlyList<string> keys, bool down)
        {
            if (keys == null || keys.Count == 0)
                return true; // "松开空集合"是合法且成功的
            bool ok = false;
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (string.IsNullOrEmpty(key))
                    continue;
                if (Try(a => a.HoldKey(key, down)))
                    ok = true;
            }
            return ok;
        }

        public bool PulseKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            return Try(a => a.PulseKey(key, 0));
        }

        /// <summary>`click` 走 <c>MouseClick</c>（引擎派生"按下一次/击打"），down/up 走 <c>MouseButton</c>。</summary>
        public bool MouseAction(string button, string action)
        {
            string name = string.IsNullOrEmpty(button) ? "left" : button.Trim().ToLowerInvariant();
            string what = string.IsNullOrEmpty(action) ? "click" : action.Trim().ToLowerInvariant();

            if (what == "click")
                return Try(a => a.MouseClick(name, 0));
            bool down = what == "down";
            if (what != "down" && what != "up")
                return false;
            return Try(a => a.MouseButton(name, down));
        }

        public bool Wheel(int notches)
        {
            if (notches == 0)
                return true;
            return Try(a => a.Wheel(notches));
        }

        /// <summary>快捷栏槽位 1..10 → 数字键（10 = 数字 0），与游戏一致。</summary>
        public bool SelectSlot(int slot)
        {
            string key = ScriptCompiler.SlotKey(slot);
            if (key == null)
                return false;
            return Try(a => a.PulseKey(key, 0));
        }

        /// <summary>绝对朝向：动作层给的是**度**，执行器吃**弧度**。</summary>
        public bool Look(float yawDegrees, float pitchDegrees)
        {
            float yaw = yawDegrees * DegreesToRadians;
            float pitch = pitchDegrees * DegreesToRadians;
            return Try(a => a.Look(yaw, pitch));
        }

        public bool LookDelta(float yawDeltaDegrees, float pitchDeltaDegrees)
        {
            float yaw = yawDeltaDegrees * DegreesToRadians;
            float pitch = pitchDeltaDegrees * DegreesToRadians;
            return Try(a => a.LookDelta(yaw, pitch));
        }

        public bool LookAt(float x, float y, float z)
        {
            var point = new Vector3(x, y, z);
            return Try(a => a.LookAt(point));
        }

        public bool UiClick(string selector)
        {
            if (string.IsNullOrEmpty(selector))
                return false;
            return Try(a => a.UiClick(selector, "direct"));
        }

        /// <summary>
        /// 打字：`IAiActuator` 面上没有这个方法（文本输入只对"命名/搜索框"有意义），
        /// 所以这里直接走 CmdBridgeMod 的门面；门面不在就如实返回 false（轨迹会记 `refused:`）。
        /// </summary>
        public bool TypeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return true;
            CmdBridgeMod.CmdBridgeInput facade = CmdBridgeActuator.FacadeOrNull;
            if (facade == null || !facade.IsAvailable)
                return false;
            try
            {
                return facade.TypeText(text);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 释放输入：走**窄释放**（只放掉注入并按住的），
        /// 与 <see cref="CmdBridgeActuator.ReleaseAll"/> 的既有取舍一致 ——
        /// 宽释放会连真实鼠标的按下沿一起清掉（主菜单里表现为"按钮按不动"，用户实测报过）。
        /// </summary>
        public void ReleaseAll()
        {
            Try(a => a.ReleaseAll());
        }

        public override string ToString()
        {
            return "GameActionExecutor(ready=" + IsReady + ")";
        }
    }
}
