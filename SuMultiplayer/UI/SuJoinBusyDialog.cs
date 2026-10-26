using System;
using System.Xml.Linq;
using Engine;
using Game;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// 客户端加入过程对话框（引擎 BusyDialog 的加 Cancel 版，2026-10-26）：
    ///   · LargeLabel / SmallLabel / BusyBar 与引擎 BusyDialog 相同（状态行由 SuPlayScreen 驱动：
    ///     Connecting → Waiting for approval (+Ping) → hold → Downloading…）；
    ///   · **Cancel 按钮**（用户口径）：点击 = 中止加入过程（回调 + 关窗）；重新加入 = 全程从头；
    ///   · Esc 同 Cancel（Input.Cancel / Input.Back）。
    /// </summary>
    public sealed class SuJoinBusyDialog : Dialog
    {
        private readonly LabelWidget m_largeLabel;
        private readonly LabelWidget m_smallLabel;
        private readonly ButtonWidget m_cancelButton;
        private readonly Action m_cancelled;
        private bool m_finished;

        public SuJoinBusyDialog(string largeMessage, string smallMessage, Action cancelled)
        {
            XElement node = ContentManager.Get<XElement>("Mod/Dialogs/SuJoinBusyDialog");
            LoadContents(this, node);
            m_largeLabel = Children.Find<LabelWidget>("SuJoinBusyDialog.LargeLabel");
            m_smallLabel = Children.Find<LabelWidget>("SuJoinBusyDialog.SmallLabel");
            m_cancelButton = Children.Find<ButtonWidget>("SuJoinBusyDialog.Cancel");
            m_cancelled = cancelled;
            LargeMessage = largeMessage;
            SmallMessage = smallMessage;
        }

        public string LargeMessage
        {
            get => m_largeLabel.Text;
            set
            {
                m_largeLabel.Text = value ?? string.Empty;
                m_largeLabel.IsVisible = !string.IsNullOrEmpty(value);
            }
        }

        public string SmallMessage
        {
            get => m_smallLabel.Text;
            set
            {
                m_smallLabel.Text = value ?? string.Empty;
                m_smallLabel.IsVisible = !string.IsNullOrEmpty(value);
            }
        }

        public override void Update()
        {
            if (m_finished) return;
            if (m_cancelButton.IsClicked || Input.Cancel || Input.Back)
            {
                m_finished = true;
                DialogsManager.HideDialog(this);
                m_cancelled?.Invoke();
            }
        }

        /// <summary>加入流程自然走完（进入世界 / 失败提示已另弹）时由 SuPlayScreen 调用：只关窗不触发 Cancel。</summary>
        public void CloseQuietly()
        {
            if (m_finished) return;
            m_finished = true;
            if (DialogsManager.Dialogs.Contains(this)) DialogsManager.HideDialog(this);
        }
    }
}
