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
        private readonly Action m_advanced;
        private readonly Action m_close;
        private readonly BevelledButtonWidget m_autoHostButton;
        private readonly BevelledButtonWidget m_autoApproveButton;
        /// <summary>模块日志模式（上线 / Debug）。只有主机能改；客户端点了会明确提示由主机调度。</summary>
        private readonly Func<bool> m_moduleLogPersist;
        private readonly Action m_toggleModuleLog;
        private readonly BevelledButtonWidget m_moduleLogButton;
        /// <summary>主机角色幽灵身体（不可见 / 不可攻击 / 睡眠）。只对主机自己的角色生效。</summary>
        private readonly Func<bool> m_hostGhost;
        private readonly Action m_toggleHostGhost;
        private readonly BevelledButtonWidget m_hostGhostButton;
        private readonly CanvasWidget m_panel;
        private readonly BevelledButtonWidget[] m_row1;
        private readonly BevelledButtonWidget[] m_row2;
        private readonly BevelledButtonWidget[] m_row3;
        private float m_lastPanelWidth = -1f;

        public HostSettingsWidget(Func<bool> autoHost, Func<bool> autoApprove,
            Action setAutoHost, Action setAutoApprove, Action advanced,
            Action banKick, Action banId, Action kick, Action removeBan, Action close,
            Func<bool> moduleLogPersist = null, Action toggleModuleLog = null,
            Func<bool> hostGhost = null, Action toggleHostGhost = null)
        {
            m_hostGhost = hostGhost;
            m_toggleHostGhost = toggleHostGhost;
            m_moduleLogPersist = moduleLogPersist;
            m_toggleModuleLog = toggleModuleLog;
            m_autoHost = autoHost;
            m_autoApprove = autoApprove;
            m_setAutoHost = setAutoHost;
            m_setAutoApprove = setAutoApprove;
            m_banKick = banKick;
            m_banId = banId;
            m_kick = kick;
            m_removeBan = removeBan;
            m_advanced = advanced;
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
                Children.Find<BevelledButtonWidget>("Advanced"),
                Children.Find<BevelledButtonWidget>("Close")
            };
            m_moduleLogButton = Children.Find<BevelledButtonWidget>("ModuleLog");
            m_hostGhostButton = Children.Find<BevelledButtonWidget>("HostBody");
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
            if (m_moduleLogButton != null)
            {
                m_moduleLogButton.Text = "MODULE LOG: " +
                    (m_moduleLogPersist?.Invoke() == true ? "DEBUG" : "ONLINE");
            }
            if (Children.Find<BevelledButtonWidget>("Advanced").IsClicked) m_advanced?.Invoke();
            if (Children.Find<BevelledButtonWidget>("Close").IsClicked) m_close?.Invoke();
            if (m_moduleLogButton != null && m_moduleLogButton.IsClicked) m_toggleModuleLog?.Invoke();
            if (m_hostGhostButton != null)
            {
                m_hostGhostButton.Text = "HOST BODY: " +
                    (m_hostGhost?.Invoke() == true ? "GHOST" : "NORMAL");
                if (m_hostGhostButton.IsClicked) m_toggleHostGhost?.Invoke();
            }
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
            if (m_moduleLogButton != null)
                m_moduleLogButton.Size = new Vector2(inner - 12f, m_moduleLogButton.Size.Y);
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
