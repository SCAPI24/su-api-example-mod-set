using CmdBridgeMod;

namespace PlayerAiMod
{
    /// <summary>
    /// 只做 UI 点击的兜底执行器：**世界还没加载/没有角色**时用。
    ///
    /// 为什么需要它：「进入游戏」这类动作包整段都发生在主菜单里（点 Play → 选世界 → Play!），
    /// 这时没有 `ComponentPlayer`，拿不到角色的执行器；但 UI 点击本来就不需要角色 ——
    /// 它只需要引擎里的软光标 + 鼠标状态，也就是 CM-1 那套注入。
    ///
    /// 铁律不变：只注入输入层（UI 点击），不写任何游戏状态；
    /// 除 UI 点击之外的方法一律**空实现** —— 世界外的"按住 W"没有任何意义，
    /// 与其假装成功，不如什么都不做（回放里那些帧也就成了空转）。
    /// </summary>
    internal sealed class UiOnlyActuator : IAiActuator
    {
        public bool IsReady
        {
            get { return CmdBridgeActuator.IsAvailable; }
        }

        /// <summary>
        /// 点击一个 UI 目标。**交给 CmdBridge 的 UI 服务**（`ui.clickElement` 那条服务）：
        /// 目标解析（控件名/路径/文本、`list:列表@文字`、`list:列表#行号`）与坐标现算都在那边做，
        /// 这里不再自己判断目标类型 —— 两边各解析一次曾经就是"编辑器能点、回放点空"的来源。
        /// </summary>
        public bool UiClick(string selectorOrPoint)
        {
            return UiClick(selectorOrPoint, "direct");
        }

        /// <summary>同上，指定点法（`direct` / `input` / `invoke`）。</summary>
        public bool UiClick(string selectorOrPoint, string mode)
        {
            CmdBridgeInput facade = CmdBridgeActuator.FacadeOrNull;
            if (facade == null || string.IsNullOrEmpty(selectorOrPoint))
                return false;

            return facade.UiClickTarget(selectorOrPoint, string.IsNullOrEmpty(mode) ? "direct" : mode);
        }

        // ---- 以下是"世界外没有意义"的动作：空实现（不假装成功） ----

        public void Look(float yawRadians, float pitchRadians)
        {
        }

        public void LookAt(Engine.Vector3 worldPoint)
        {
        }

        public void LookDelta(float yawRadians, float pitchRadians)
        {
        }

        public void HoldKey(string key, bool down)
        {
        }

        public void PulseKey(string key, int holdMilliseconds)
        {
        }

        public void MouseButton(string button, bool down)
        {
        }

        public void MouseClick(string button, int holdMilliseconds)
        {
        }

        public void Wheel(int delta)
        {
        }

        public void ReleaseAll()
        {
            // UI 点击自己会收尾（清 m_mouseDownPoint / 关软光标）；
            // 这里仍然清一次注入器的残留状态，避免回放中断后留下按住。
            CmdBridgeInput facade = CmdBridgeActuator.FacadeOrNull;
            if (facade != null)
                facade.ReleaseAll();
        }
    }
}
