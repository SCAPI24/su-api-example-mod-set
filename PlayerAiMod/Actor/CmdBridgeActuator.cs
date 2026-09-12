using CmdBridgeMod;
using Engine;
using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 真实执行器：把 AI 的输入意图转交给 CmdBridgeMod 的玩家控制器注入门面。
    ///
    /// 为什么复用 CmdBridgeMod 而不是自己再写一份注入：
    ///   · 注入是细活 —— 写入必须在"下一帧帧首"、只能写输入层白名单字段、UI 点击是两帧动作、
    ///     还要清 WidgetInput.m_mouseDownPoint、窗口失焦时全部输入会被游戏丢弃。
    ///     这些坑只应该有一份实现，本 Mod 只负责决策。
    ///   · 本 Mod 的角度单位是弧度（游戏原生），CmdBridge 门面用度，这里做换算。
    ///
    /// 依赖不可用（CmdBridgeMod 未安装 / 注入开关关闭 / 游戏未就绪）时 <see cref="IsReady"/> 为 false，
    /// 状态机的守卫据此不会产生动作。
    /// </summary>
    public sealed class CmdBridgeActuator : IAiActuator
    {
        private const float RadiansToDegrees = 180f / MathUtils.PI;

        private readonly AiActor m_actor;

        public CmdBridgeActuator(AiActor actor)
        {
            if (actor == null)
                throw new ArgumentNullException(nameof(actor));
            m_actor = actor;
        }

        /// <summary>门面是否可用（依赖 Mod 已加载且注入已启用）。</summary>
        public static bool IsAvailable
        {
            get
            {
                CmdBridgeInput facade = Facade;
                return facade != null && facade.IsAvailable;
            }
        }

        public bool IsReady
        {
            get { return IsAvailable; }
        }

        public void Look(float yawRadians, float pitchRadians)
        {
            CmdBridgeInput facade = Facade;
            if (facade == null)
                return;
            facade.Look(yawRadians * RadiansToDegrees, pitchRadians * RadiansToDegrees);
        }

        public void LookAt(Vector3 worldPoint)
        {
            CmdBridgeInput facade = Facade;
            if (facade == null)
                return;
            facade.LookAt(worldPoint.X, worldPoint.Y, worldPoint.Z);
        }

        public void LookDelta(float yawRadians, float pitchRadians)
        {
            CmdBridgeInput facade = Facade;
            if (facade == null)
                return;
            facade.LookDelta(yawRadians * RadiansToDegrees, pitchRadians * RadiansToDegrees);
        }

        public void HoldKey(string key, bool down)
        {
            CmdBridgeInput facade = Facade;
            if (facade == null)
                return;
            facade.KeyHold(key, down);
        }

        public void PulseKey(string key, int holdMilliseconds)
        {
            CmdBridgeInput facade = Facade;
            if (facade == null)
                return;
            facade.KeyPulse(key, holdMilliseconds);
        }

        public void MouseButton(string button, bool down)
        {
            CmdBridgeInput facade = Facade;
            if (facade == null)
                return;
            facade.MouseAction(button, down ? "down" : "up", 0);
        }

        public void MouseClick(string button, int holdMilliseconds)
        {
            CmdBridgeInput facade = Facade;
            if (facade == null)
                return;
            facade.MouseAction(button, "click", holdMilliseconds);
        }

        public void Wheel(int delta)
        {
            CmdBridgeInput facade = Facade;
            if (facade == null)
                return;
            facade.Wheel(delta);
        }

        /// <summary>
        /// UI 点击。**交给 CmdBridge 的 UI 服务**：目标解析（控件名/路径/文本、`list:列表@文字`、
        /// `list:列表#行号`）与"点在哪个像素"都在那边**点击那一刻**现算 —— 与编辑器/行为树用的是
        /// 同一份实现，不会出现"编辑器能点、回放点空"。
        /// </summary>
        public bool UiClick(string selectorOrPoint)
        {
            return UiClick(selectorOrPoint, "direct");
        }

        /// <summary>同上，指定点法（`direct` / `input` / `invoke`）。</summary>
        public bool UiClick(string selectorOrPoint, string mode)
        {
            CmdBridgeInput facade = Facade;
            if (facade == null || string.IsNullOrEmpty(selectorOrPoint))
                return false;

            return facade.UiClickTarget(selectorOrPoint, string.IsNullOrEmpty(mode) ? "direct" : mode);
        }

        /// <summary>
        /// 释放这个角色占住的输入。
        ///
        /// ⚠️ 走**窄释放**（只放掉注入并按住的那些）：这个方法会被"任务收尾 / 世界卸载 /
        /// 回放结束 / 世界外每帧兜底"频繁调用，而宽版本（`ReleaseAll`）会连
        /// **真实鼠标的按下沿**一起清掉 —— 主菜单里就变成"按钮有按下效果、点不动"
        /// （用户实测报的正是这个）。要彻底停手走 <see cref="ReleaseAllImmediate"/>。
        /// </summary>
        public void ReleaseAll()
        {
            CmdBridgeInput facade = Facade;
            if (facade == null)
                return;
            facade.ReleaseInjectedInput();
        }

        /// <summary>立即释放（不等帧首）：Mod 卸载/世界卸载时用，避免残留"按住 W"。</summary>
        public static void ReleaseAllImmediate()
        {
            CmdBridgeInput facade = Facade;
            if (facade == null)
                return;
            facade.ReleaseAllImmediate();
        }

        /// <summary>注入门面（供只读输入快照使用；CmdBridgeMod 未加载时为 null）。</summary>
        public static CmdBridgeInput FacadeOrNull
        {
            get { return Facade; }
        }

        /// <summary>依赖 Mod 的注入门面；CmdBridgeMod 未加载时为 null。</summary>
        private static CmdBridgeInput Facade
        {
            get
            {
                CmdBridgeMod.CmdBridgeMod mod = CmdBridgeMod.CmdBridgeMod.Instance;
                return mod != null ? mod.Input : null;
            }
        }

        public override string ToString()
        {
            return "CmdBridgeActuator(" + (m_actor != null ? m_actor.Name : "?") + ")";
        }
    }
}
