using Game;
using SuAPI;
using SuAPICore;
using System;

namespace ScMultiplayer
{
    /// <summary>
    /// 带中文 IME 支持的聊天输入对话框。
    ///
    /// 输入接管整套改用内置的 <see cref="SuAPITextInput"/>：原版 TextBoxWidget 一帧只插
    /// `Input.LastChar`，中文连打（IME 一次上屏多字）会只剩最后一个字。
    /// 这里原先自带 ImeInputCaptureWidget / TextBoxAccessor / WindowsIme 三套实现，
    /// 已迁进 SuAPICore 供所有 Mod 复用。
    ///
    /// Source: EntitySystem/SuAPICore/SuAPITextInput.cs:SuAPITextInput.Attach
    /// </summary>
    internal sealed class WindowsTalkDialog : TextBoxDialog
    {
        private readonly Widget m_textBox;
        private IDisposable m_textInputLease;
        private bool m_inputSessionClosed;

        public WindowsTalkDialog(string title, string text, int maximumLength,
            Action<string> handler)
            : base(title, text, maximumLength, handler)
        {
            m_textBox = Children.Find<Widget>("TextBoxDialog.TextBox", true);

            // 世界里带角色的自绘编辑器必须持有 IME 租约：租约摘掉时按键会直通游戏，
            // 背后的角色跟着一起动 —— 这正是租约当初被加上的原因。
            // 挂载时 SuAPITextInput 还会 Keyboard.Clear() 清掉「已经按住」的键，
            // 并在输入法组字期间吞掉 Backspace / Enter。
            m_textInputLease = SuAPITextInput.Attach(m_textBox, true);
        }

        public override void Update()
        {
            base.Update();
            if (!m_inputSessionClosed && Input.Devices == WidgetInputDevice.None)
                CloseInputSession();
        }

        // Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.HideDialog
        // 隐藏对话框会把层级输入换成 WidgetInputDevice.None。这里释放一次，
        // 让排空积压字符与 Keyboard.Clear() 都跑完，第一个游戏按键才不会被
        // 残留的输入状态吃掉。
        private void CloseInputSession()
        {
            m_inputSessionClosed = true;
            Game.Program.ModManager.ModParentField.ModifyParentField(
                m_textBox, "HasFocus", false, m_textBox.GetType());
            m_textInputLease?.Dispose();
            m_textInputLease = null;
        }
    }
}
