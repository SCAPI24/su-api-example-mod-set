using System;
using System.Xml.Linq;
using Engine;
using Game;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// IF 设置容器：参考 CreatorWand 的 CreatorWidget，用 CanvasWidget + 固定按钮网格，
    /// 不是一行一个选项的 ListSelectionDialog。作为 ModalPanelWidget 挂在游戏内，
    /// 每个按钮点击后弹对应的参数/选择对话框，设置容器本身保持打开。
    /// </summary>
    public sealed class HostSettingsWidget : CanvasWidget
    {
        private readonly Func<bool> m_autoHost;
        private readonly Func<bool> m_autoApprove;
        private readonly Action m_setAutoHost;
        private readonly Action m_setAutoApprove;
        private readonly Action m_banKick;
        private readonly Action m_banId;
        private readonly Action m_kick;
        private readonly Action m_removeBan;
        private readonly Action m_changePort;
        private readonly Action m_close;
        private readonly BevelledButtonWidget m_autoHostButton;
        private readonly BevelledButtonWidget m_autoApproveButton;
        private readonly CanvasWidget m_panel;
        private readonly BevelledButtonWidget[] m_row1;
        private readonly BevelledButtonWidget[] m_row2;
        private readonly BevelledButtonWidget[] m_row3;
        private float m_lastPanelWidth = -1f;

        public HostSettingsWidget(Func<bool> autoHost, Func<bool> autoApprove,
            Action setAutoHost, Action setAutoApprove, Action banKick, Action banId,
            Action kick, Action removeBan, Action changePort, Action close)
        {
            m_autoHost = autoHost;
            m_autoApprove = autoApprove;
            m_setAutoHost = setAutoHost;
            m_setAutoApprove = setAutoApprove;
            m_banKick = banKick;
            m_banId = banId;
            m_kick = kick;
            m_removeBan = removeBan;
            m_changePort = changePort;
            m_close = close;
            XElement node = ContentManager.Get<XElement>("Mod/Dialogs/HostSettingsWidget");
            LoadChildren(this, node);
            m_autoHostButton = Children.Find<BevelledButtonWidget>("AutoHost");
            m_autoApproveButton = Children.Find<BevelledButtonWidget>("AutoApprove");
            m_panel = Children.Find<CanvasWidget>("HostSettingsWidget.Panel");
            m_row1 = new[]
            {
                m_autoHostButton,
                m_autoApproveButton,
                Children.Find<BevelledButtonWidget>("BanKick")
            };
            m_row2 = new[]
            {
                Children.Find<BevelledButtonWidget>("BanId"),
                Children.Find<BevelledButtonWidget>("Kick"),
                Children.Find<BevelledButtonWidget>("RemoveBan")
            };
            m_row3 = new[]
            {
                Children.Find<BevelledButtonWidget>("ChangePort"),
                Children.Find<BevelledButtonWidget>("Close")
            };
        }

        public override void Update()
        {
            AdaptLayoutToScreen();
            m_autoHostButton.Text = "AUTO HOST: " + (m_autoHost?.Invoke() == true ? "ON" : "OFF");
            m_autoApproveButton.Text = "AUTO APPROVE: " +
                (m_autoApprove?.Invoke() == true ? "ON" : "OFF");
            if (Children.Find<BevelledButtonWidget>("AutoHost").IsClicked) m_setAutoHost?.Invoke();
            if (Children.Find<BevelledButtonWidget>("AutoApprove").IsClicked) m_setAutoApprove?.Invoke();
            if (Children.Find<BevelledButtonWidget>("BanKick").IsClicked) m_banKick?.Invoke();
            if (Children.Find<BevelledButtonWidget>("BanId").IsClicked) m_banId?.Invoke();
            if (Children.Find<BevelledButtonWidget>("Kick").IsClicked) m_kick?.Invoke();
            if (Children.Find<BevelledButtonWidget>("RemoveBan").IsClicked) m_removeBan?.Invoke();
            if (Children.Find<BevelledButtonWidget>("ChangePort").IsClicked) m_changePort?.Invoke();
            if (Children.Find<BevelledButtonWidget>("Close").IsClicked) m_close?.Invoke();
        }

        /// <summary>
        /// 容器宽度不能写死：不同设备 UI 缩放不同，1180 在平板上会超出可显示区域。
        /// 以父控件可用宽度为准缩到 1180 以内，同时按行重新分配按钮宽度。
        /// </summary>
        private void AdaptLayoutToScreen()
        {
            float available = ParentWidget?.ActualSize.X ?? 0f;
            if (available <= 0f || m_panel == null) return;
            float panelWidth = MathUtils.Clamp(available - 24f, 620f, 1180f);
            if (MathUtils.Abs(panelWidth - m_lastPanelWidth) < 0.5f) return;
            m_lastPanelWidth = panelWidth;
            m_panel.Size = new Vector2(panelWidth, m_panel.Size.Y);
            float inner = panelWidth - 28f;
            SetRowWidth(m_row1, 3, inner);
            SetRowWidth(m_row2, 3, inner);
            SetRowWidth(m_row3, 2, inner);
        }

        private static void SetRowWidth(BevelledButtonWidget[] row, int columns, float available)
        {
            if (row == null || columns <= 0) return;
            float width = MathUtils.Max(120f, available / columns - 12f);
            for (int i = 0; i < row.Length; i++)
            {
                if (row[i] == null) continue;
                row[i].Size = new Vector2(width, row[i].Size.Y);
            }
        }
    }
}
