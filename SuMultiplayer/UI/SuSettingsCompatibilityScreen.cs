using System;
using System.Xml.Linq;
using Engine;
using Game;
using SuMultiplayer.Engine;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// 引擎 Settings → Compatibility 屏的替换版：在原三行（单线程地形更新 / 查看日志 /
    /// 恢复默认）下面追加一行 <c>Network Proxy:</c>，按钮三态 Default/On/Off。
    ///
    /// 入口与 Play 屏相同：组合根在 Loading.Initialize 里 ReplaceItem
    /// "Initialize SettingsCompatibilityScreen"。不修改引擎源码。
    /// </summary>
    public sealed class SuSettingsCompatibilityScreen : SettingsCompatibilityScreen
    {
        private ButtonWidget m_proxyButton;

        public SuSettingsCompatibilityScreen()
        {
            ProxySettings.Load(out _);
            ButtonWidget resetButton = Children.Find<ButtonWidget>("ResetDefaultsButton");
            if (resetButton == null) return;

            // 行样式照抄原 XML 行：Label + BevelledButton，按原有间距插在 Reset 行后面。
            XElement rowNode = XElement.Parse(
                "<UniformSpacingPanelWidget Direction=\"Horizontal\" xmlns=\"runtime-namespace:Game\">" +
                "  <LabelWidget Text=\"Network Proxy:\" Font=\"{Fonts/Pericles18}\" " +
                "HorizontalAlignment=\"Far\" VerticalAlignment=\"Center\" Margin=\"20, 0\" />" +
                "  <BevelledButtonWidget Name=\"NetworkProxyButton\" " +
                "Style=\"{Styles/ButtonStyle_310x60}\" VerticalAlignment=\"Center\" " +
                "Margin=\"20, 0\" Text=\"Default\" />" +
                "</UniformSpacingPanelWidget>");
            ContainerWidget row = Widget.LoadWidget(this, rowNode, null) as ContainerWidget;
            if (row == null) return;

            Widget resetRow = resetButton.ParentWidget;
            ContainerWidget stack = resetRow?.ParentWidget as ContainerWidget;
            if (stack == null) return;
            int index = -1;
            for (int i = 0; i < stack.Children.Count; i++)
            {
                if (ReferenceEquals(stack.Children[i], resetRow)) { index = i; break; }
            }
            if (index < 0) return;
            stack.Children.Insert(index + 1, row);
            m_proxyButton = row.Children.Find<ButtonWidget>("NetworkProxyButton");
            UpdateProxyButtonText();
        }

        public override void Update()
        {
            base.Update();
            if (m_proxyButton == null) return;
            if (m_proxyButton.IsClicked)
            {
                if (!ProxySettings.CycleMode(out ProxyMode mode, out string error))
                {
                    DialogsManager.ShowDialog(null, new MessageDialog("Network Proxy",
                        "Unable to save proxy setting: " + (error ?? "unknown error"), "OK",
                        null, null));
                }
                else
                {
                    global::Engine.Log.Information("[SuMP][proxy] UI 切换代理模式 → " +
                        ProxySettings.ModeName(mode) + "（" + ProxySettings.Describe + "）");
                }
            }
            // 不缓存点击次数：每帧都从实际运行状态刷新按钮文字。
            UpdateProxyButtonText();
        }

        private void UpdateProxyButtonText()
        {
            if (m_proxyButton == null) return;
            string text = ProxySettings.ModeName(ProxySettings.Mode);
            if (!string.Equals(m_proxyButton.Text, text, StringComparison.Ordinal))
                m_proxyButton.Text = text;
        }
    }
}
