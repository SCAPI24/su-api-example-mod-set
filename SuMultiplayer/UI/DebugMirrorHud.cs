using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// 调试镜像的**小 HUD 按钮**（debug-only）：挂在每个 GameWidget 的右上角 MoreContents 里
    /// （与 CR / SK 按钮同一排），文字见 <see cref="DebugMirrorController.HudText"/>。
    ///
    /// 为什么用 HUD 按钮而不是 CmdBridge 命令：CmdBridge 只反射**已存在的**聊天钩子
    /// （<c>SuChatAutomation</c>），没有通用"调用任意 mod 方法"的命令；为了一个调试功能
    /// 去改 CmdBridgeMod 会越过本任务的边界。按钮是显式的、可见的、只影响调试镜像本身。
    ///
    /// 按钮只在**配置开启 + 正在主持房间**时出现；配置默认关闭时完全不存在（不占屏幕）。
    /// 点一次：关闭 → 开启（用配置目标）；开启 → 循环到下一个可用远端角色；循环完 → 关闭。
    /// </summary>
    public sealed class DebugMirrorHud
    {
        private const string ButtonName = "SuMultiplayerDebugMirrorButton";

        private readonly DebugMirrorController m_controller;
        private readonly Func<bool> m_hosting;
        private readonly Action<string> m_info;
        private readonly Action<string> m_warn;
        private readonly List<BevelledButtonWidget> m_buttons = new List<BevelledButtonWidget>();
        private readonly HashSet<BevelledButtonWidget> m_consumedClicks =
            new HashSet<BevelledButtonWidget>();
        private readonly Dictionary<string, long> m_lastLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private long m_moreContentsMisses;
        private long m_tickFailures;

        public DebugMirrorHud(DebugMirrorController controller, Func<bool> hosting,
            Action<string> info, Action<string> warn)
        {
            m_controller = controller ?? throw new ArgumentNullException(nameof(controller));
            m_hosting = hosting;
            m_info = info;
            m_warn = warn;
        }

        public void Tick()
        {
            try
            {
                TickCore();
            }
            catch (Exception e)
            {
                m_tickFailures++;
                WarnRateLimited("tick", "[SuMP][debug-mirror-ui] HUD 帧驱动异常（已隔离，下一帧继续）：" +
                    e.GetType().Name + " " + e.Message);
            }
        }

        private void TickCore()
        {
            Project project = GameManager.Project;
            if (project == null)
            {
                m_buttons.Clear();
                m_consumedClicks.Clear();
                return;
            }
            SubsystemGameWidgets widgets = project.FindSubsystem<SubsystemGameWidgets>(false);
            if (widgets == null) return;
            bool visible = m_controller.IsConfigured && (m_hosting?.Invoke() ?? false);
            m_buttons.Clear();
            for (int i = 0; i < widgets.GameWidgets.Count; i++)
            {
                GameWidget gameWidget = widgets.GameWidgets[i];
                if (gameWidget == null) continue;
                StackPanelWidget moreContents =
                    gameWidget.Children.Find<StackPanelWidget>("MoreContents", false);
                if (moreContents == null)
                {
                    m_moreContentsMisses++;
                    WarnRateLimited("morecontents",
                        "[SuMP][debug-mirror-ui] GameWidget 里没有 MoreContents（引擎 HUD 结构变化）→ " +
                        "MIR 按钮本帧不挂载（下一帧继续尝试）");
                    continue;
                }
                BevelledButtonWidget button =
                    moreContents.Children.Find<BevelledButtonWidget>(ButtonName, false);
                if (button == null && visible)
                {
                    button = new BevelledButtonWidget
                    {
                        Name = ButtonName,
                        Text = m_controller.HudText(),
                        Size = new Vector2(76f, 64f),
                        Margin = new Vector2(3f, 0f),
                        Color = Color.White,
                        CenterColor = new Color(105, 70, 30),
                        BevelColor = new Color(120, 120, 120)
                    };
                    moreContents.Children.Add(button);
                    InfoRateLimited("mounted", "[SuMP][debug-mirror-ui] MIR 调试按钮已挂到 GameWidget#" +
                        gameWidget.GameWidgetIndex + " 的 MoreContents");
                }
                if (button == null) continue;
                button.IsVisible = visible;
                if (!visible) continue;
                string text = m_controller.HudText();
                if (button.Text != text) button.Text = text;
                m_buttons.Add(button);
                if (TryConsumeClick(button)) m_controller.CycleFromHud();
            }
            m_consumedClicks.RemoveWhere(delegate (BevelledButtonWidget b)
            {
                return !b.IsClicked && !m_buttons.Contains(b);
            });
        }

        /// <summary>
        /// 只消费 <c>IsClicked</c> 的上升沿：控件树停止 Update 时 IsClicked 会冻结为 true
        /// （与 RoomUiAttacher 同一处理，Source: Survivalcraft/Game/ClickableWidget.cs:28-52）。
        /// </summary>
        private bool TryConsumeClick(BevelledButtonWidget button)
        {
            if (button == null) return false;
            if (!button.IsClicked)
            {
                m_consumedClicks.Remove(button);
                return false;
            }
            if (m_consumedClicks.Contains(button)) return false;
            m_consumedClicks.Add(button);
            return true;
        }

        public string DescribeCounters() =>
            "debug-mirror-ui: buttons=" + m_buttons.Count +
            " moreContentsMissing=" + m_moreContentsMisses +
            " tickFail=" + m_tickFailures + " " + m_controller.DescribeCounters();

        private void InfoRateLimited(string key, string message)
        {
            if (!ShouldLog(key)) return;
            if (m_info != null) m_info(message);
            else global::Engine.Log.Information(message);
        }

        private void WarnRateLimited(string key, string message)
        {
            if (!ShouldLog(key)) return;
            if (m_warn != null) m_warn(message);
            else global::Engine.Log.Warning(message);
        }

        private bool ShouldLog(string key)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastLogMs.TryGetValue(key, out long last) && nowMs - last < 5000) return false;
            m_lastLogMs[key] = nowMs;
            return true;
        }
    }
}
