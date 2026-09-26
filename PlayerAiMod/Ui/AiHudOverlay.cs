using System;
using Engine;
using Engine.Media;
using Game;

namespace PlayerAiMod
{
    /// <summary>
    /// **HUD 状态行**（plan G19）：屏幕左上角一行小字，让人一眼看出 AI 现在在干嘛。
    ///
    /// 为什么要有它：共控/接管时人看不出"AI 在等 Laya / 被相位闸门拦着 / 已经放弃"，
    /// 只会觉得"AI 发呆" —— 而在玩家眼里"发呆"和"坏了"是同一件事。
    /// （失败细节仍然只走 `ai.logs` 与事件环；这一行只回答"现在是什么状态"。）
    ///
    /// 挂在哪：**`ScreensManager.RootWidget`** 而不是世界内的 HUD 控件条。
    ///   · 世界外（主菜单/选世界/对话框）同样要显示，而世界外的宿主与 HUD 毫无关系；
    ///   · 世界内那条控制栏在**非触屏平台是触屏专用的**（`ComponentGui.UpdateSidePanelsAnimation`
    ///     会把它整体平移到屏幕外，见 `UiInspector.ExplainUnhittable` 的实测记录），
    ///     往里塞东西在 Windows 上等于塞进了屏幕外。
    /// 挂在根控件上则**相位无关**，也不需要 Player 实体（世界外没有 Player）。
    ///
    /// 三条必须守住的纪律：
    ///   · **绝不参与命中测试**（`IsHitTestVisible = false`）：HUD 不能吃掉玩家的点击
    ///     —— 输入层是铁律，AI 侧的任何装饰都不许改变人的操作结果；
    ///   · **按名字 find-or-add**：世界/屏幕切换会重建控件树，静态标志判重会漏（GmMod 踩过
    ///     "复活后多一个按钮"），所以每次都按名字找、找不到才建；
    ///   · **抛异常不许带崩游戏**：这里在每帧路径上，任何异常只允许让 HUD 自己失效。
    /// </summary>
    public sealed class AiHudOverlay
    {
        /// <summary>控件名（`ui --all` 里就是靠它找到这一行）。</summary>
        public const string WidgetName = "AiHudLabel";

        /// <summary>刷新间隔：HUD 是给人看的，5 Hz 足够，也不必每帧改文本（改文本会触发重绘）。</summary>
        public const double RefreshSeconds = 0.2;

        private LabelWidget m_label;
        private string m_lastText;
        private double m_nextRefresh;
        private string m_lastFailure;

        /// <summary>开关（`ai.hud off` 会让它整行隐藏）。</summary>
        public bool Enabled = true;

        /// <summary>当前显示的那行字（自检与命令用它，不必去问控件）。</summary>
        public string LastText { get; private set; }

        /// <summary>上一次"挂控件/改文本"出错的原因（不为 null 时说明 HUD 现在是坏的）。</summary>
        public string LastFailure
        {
            get { return m_lastFailure; }
        }

        public void Update(double now, HudStatusFacts facts)
        {
            if (!Enabled)
            {
                Hide();
                return;
            }

            // 先节流再比文本：HUD 是给人看的，5 Hz 足够；
            // 文本没变就**不碰控件**（改 Text 会让 LabelWidget 重新排版/置重绘标志）。
            if (now < m_nextRefresh)
                return;
            m_nextRefresh = now + RefreshSeconds;

            string text = HudStatusLine.Compile(facts);
            LastText = text;
            if (text == m_lastText && m_label != null)
                return;
            m_lastText = text;

            try
            {
                if (!EnsureLabel())
                    return;
                m_label.Text = text ?? string.Empty;
                // 文本为空时控件自己不画（`LabelWidget.IsDrawRequired`），不必额外隐藏。
            }
            catch (Exception exception)
            {
                // 这一行不能带崩游戏：记下原因、把控件丢掉，下一次刷新再试着重建。
                m_lastFailure = exception.GetType().Name + ": " + exception.Message;
                m_label = null;
                m_lastText = null;
            }
        }

        /// <summary>把 HUD 隐藏（`ai.hud off`）。控件留着，只是不再更新文本。</summary>
        public void Hide()
        {
            if (m_label == null)
                return;
            try
            {
                m_label.Text = string.Empty;
            }
            catch (Exception)
            {
            }
            LastText = null;
            m_lastText = null;
        }

        /// <summary>彻底摘掉控件（Mod 卸载/退出时）。</summary>
        public void Detach()
        {
            try
            {
                if (m_label != null && m_label.ParentWidget != null)
                    m_label.ParentWidget.Children.Remove(m_label);
            }
            catch (Exception)
            {
            }
            m_label = null;
            m_lastText = null;
            LastText = null;
        }

        /// <summary>
        /// 按名字找控件，找不到才新建。**不用静态标志判重** —— 换世界/换屏幕会重建控件树，
        /// 那时旧引用已失效但标志还是"已挂过"，结果就是永远不再显示（或者重复挂）。
        /// </summary>
        private bool EnsureLabel()
        {
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                return false;

            if (m_label != null && m_label.ParentWidget != null)
                return true;

            m_label = null;
            // 第二个参数必须是 false：`Find<T>` 默认**找不到就抛**。
            LabelWidget found = root.Children.Find<LabelWidget>(WidgetName, false);
            if (found == null)
            {
                found = new LabelWidget
                {
                    Name = WidgetName,
                    Font = ContentManager.Get<BitmapFont>("Fonts/Pericles18"),
                    FontScale = 0.8f,
                    DropShadow = true,
                    HorizontalAlignment = WidgetAlignment.Near,
                    VerticalAlignment = WidgetAlignment.Near,
                    Margin = new Vector2(12f, 8f),
                    // 铁律：HUD 绝不吃掉玩家的点击。
                    IsHitTestVisible = false
                };
                root.Children.Add(found);
            }
            m_label = found;
            return true;
        }

        /// <summary>现状（`ai.hud` 命令与自检用）。</summary>
        public System.Collections.Generic.Dictionary<string, object> Describe()
        {
            var info = new System.Collections.Generic.Dictionary<string, object>(StringComparer.Ordinal);
            info["enabled"] = Enabled;
            info["widget"] = WidgetName;
            info["attached"] = m_label != null && m_label.ParentWidget != null;
            info["text"] = LastText;
            info["lastFailure"] = m_lastFailure;
            return info;
        }
    }
}
