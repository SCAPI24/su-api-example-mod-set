using Engine;
using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 执行器占位实现：记录"AI 想做什么"，但**不做任何注入**。
    ///
    /// 为什么要占位：真正接入输入层之前，先把"意图 → 动作"的调用链和状态机跑通，
    /// 这样方案确定后只需要替换这一个类，状态与转移表不用改。
    ///
    /// 接入真实输入层时的清单（全部来自 CmdBridgeMod 的实测结论，见 README §7）：
    ///   1. 写入时机：在"下一帧帧首"写。Frame.Update 里 Dispatcher.Dispatch，动作会在
    ///      Dispatcher.BeforeFrame() 执行 —— 早于键盘/鼠标设备读取与整个帧体。
    ///   2. 写入范围：只写输入层白名单字段
    ///      Keyboard.m_keysDownArray / m_keysDownOnceArray / m_keysDownRepeatArray、
    ///      Mouse.m_mouseButtonsDownArray / m_mouseButtonsDownOnceArray / MouseWheelMovement、
    ///      ComponentLocomotion.m_lookAngles（俯仰，±82°）、WidgetInput.m_mouseDownPoint。
    ///   3. 视角：yaw 写 ComponentBody.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)；
    ///      pitch 写 ComponentLocomotion.m_lookAngles.Y 并自行 clamp。
    ///   4. 生效前置：先看 IAiSensor.IsInputAccepted（窗口前台 + PlayerData 就绪），否则白写。
    ///   5. UI 点击是两帧动作（按下帧派生 Press/Tap，松开帧派生 Click），并且要清掉
    ///      WidgetInput.m_mouseDownPoint，避免一次点击被多帧重复消费。
    ///   6. 卸载/禁用/进入安全状态时必须 ReleaseAll，避免游戏里残留"按住 W"。
    /// </summary>
    public sealed class PlaceholderActuator : IAiActuator
    {
        /// <summary>只记意图的次数（占位执行器不动玩家）。</summary>
        public int LookDeltaCalls;

        private readonly AiActor m_actor;
        private readonly string m_tag;

        public PlaceholderActuator(AiActor actor)
        {
            if (actor == null)
                throw new ArgumentNullException(nameof(actor));
            m_actor = actor;
            m_tag = "actor:" + (actor.Name ?? "?");
        }

        /// <summary>被 AI 请求过的动作次数（占位期用于确认调用链是否接通）。</summary>
        public int RequestCount { get; private set; }

        /// <summary>占位实现永远不可用：它不会真的操作玩家。</summary>
        public bool IsReady
        {
            get { return false; }
        }

        public void Look(float yawRadians, float pitchRadians)
        {
            Record("Look(yaw=" + yawRadians.ToString("0.000") + ", pitch=" + pitchRadians.ToString("0.000") + ")");
        }

        public void LookAt(Vector3 worldPoint)
        {
            Record("LookAt(" + worldPoint.ToString() + ")");
        }

        public void LookDelta(float yawRadians, float pitchRadians)
        {
            LookDeltaCalls++;
        }

        public void HoldKey(string key, bool down)
        {
            Record("HoldKey(" + key + ", " + down.ToString() + ")");
        }

        public void PulseKey(string key, int holdMilliseconds)
        {
            Record("PulseKey(" + key + ", " + holdMilliseconds.ToString() + "ms)");
        }

        public void MouseButton(string button, bool down)
        {
            Record("MouseButton(" + button + ", " + down.ToString() + ")");
        }

        public void MouseClick(string button, int holdMilliseconds)
        {
            Record("MouseClick(" + button + ", " + holdMilliseconds.ToString() + "ms)");
        }

        public void Wheel(int delta)
        {
            Record("Wheel(" + delta.ToString() + ")");
        }

        public bool UiClick(string selectorOrPoint)
        {
            return UiClick(selectorOrPoint, "direct");
        }

        public bool UiClick(string selectorOrPoint, string mode)
        {
            Record("UiClick(" + selectorOrPoint + ", " + mode + ")");
            return true;
        }

        public void ReleaseAll()
        {
            Record("ReleaseAll()");
        }

        private void Record(string action)
        {
            RequestCount++;
            if (PlayerAiConfig.VerboseLogging)
            {
                Engine.Log.Information("[PlayerAi] " + m_tag + " actuator (placeholder) "
                    + action + " [total " + RequestCount.ToString() + "]");
            }
        }
    }
}
