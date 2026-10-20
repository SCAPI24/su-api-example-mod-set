using System;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// 聊天自动化的**显式错误**：每个失败都带稳定 <see cref="Code"/>，由 CmdBridge 反射读取并
    /// 转成 CLI 错误码（chat_not_in_session / chat_not_open / chat_unavailable / chat_inject_failed …）。
    /// 不静默、不降级。
    /// </summary>
    public sealed class ChatAutomationException : Exception
    {
        public ChatAutomationException(string code, string message) : base(message)
        {
            Code = string.IsNullOrEmpty(code) ? "chat_inject_failed" : code;
        }

        /// <summary>稳定错误码（CmdBridge 按它分类，不解析人类可读文本）。</summary>
        public string Code { get; }
    }

    /// <summary>
    /// 聊天自动化钩子（SuMultiplayer 唯一的自动化入口；组合根创建并挂在
    /// <c>SuMultiplayerMod.ChatAutomation</c> 上，无静态可变状态）。
    ///
    /// 调用链与控制台手打完全同源：
    ///   <see cref="OpenChatInput"/> → 聊天 HUD 输入行（桌面内联 / 自动化强制内联）；
    ///   <see cref="InjectChatText"/> → <see cref="Net.ChatInjectionPolicy"/>（复用
    ///     <see cref="Net.ChatTextPolicy"/> 的净化 + 长度上限）→ 输入缓冲区；
    ///   <see cref="SubmitChatInput"/> → 与按回车同一条
    ///     <c>ChatModule.TrySendLocalText</c>（净化 / 限速 / 客户端请求 / 主机校验 / 广播）。
    ///
    /// 本类不依赖 CmdBridge；CmdBridge 只通过反射找
    /// <c>SuMultiplayer.SuMultiplayerMod.ChatAutomation</c> 属性并调用下面这些公开成员。
    /// </summary>
    public sealed class ChatAutomation
    {
        private readonly ChatOverlayAttacher m_overlay;

        internal ChatAutomation(ChatOverlayAttacher overlay)
        {
            m_overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        }

        /// <summary>本端是否在联机会话里（主机已监听 / 客户端已完成握手）。</summary>
        public bool IsInSession => m_overlay.IsInSession;

        /// <summary>聊天输入行当前是否打开。</summary>
        public bool IsChatInputOpen => m_overlay.HasOpenInput;

        /// <summary>当前输入缓冲区正文（未打开时为空串）；用于自动化复核注入/净化结果。</summary>
        public string CurrentChatInputText => m_overlay.AutomationInputText;

        /// <summary>
        /// 打开聊天输入行。已打开时是幂等的成功。
        /// 失败：不在联机会话 → <c>chat_not_in_session</c>；HUD 未挂载 → <c>chat_unavailable</c>。
        /// </summary>
        public void OpenChatInput()
        {
            if (!m_overlay.TryAutomationOpen(out string error))
                throw Failure("chat_unavailable", error);
        }

        /// <summary>
        /// 追加一段文本到当前输入缓冲区（净化 / 上限见 ChatInjectionPolicy）。
        /// 失败：没打开 → <c>chat_not_open</c>；空 / 超长 / 原生软键盘占用 → <c>chat_inject_failed</c>。
        /// </summary>
        public void InjectChatText(string text)
        {
            if (!m_overlay.TryAutomationInject(text, out string error))
                throw Failure("chat_inject_failed", error);
        }

        /// <summary>
        /// 提交当前输入缓冲区（与按回车同一条路径）。成功只表示"请求已发出 / 主机已广播"，
        /// 客户端仍只认主机回显；限速 / 空消息 / 非会话会明确失败。
        /// </summary>
        public void SubmitChatInput()
        {
            if (!m_overlay.TryAutomationSubmit(out string error))
                throw Failure("chat_inject_failed", error);
        }

        /// <summary>关闭输入行并清空缓冲区（等价按 Esc）。没打开时显式失败。</summary>
        public void CancelChatInput()
        {
            if (!m_overlay.TryAutomationCancel(out string error))
                throw Failure("chat_not_open", error);
        }

        private ChatAutomationException Failure(string fallbackCode, string error)
        {
            string code = fallbackCode;
            if (!string.IsNullOrEmpty(error))
            {
                if (error.StartsWith("not_in_session", StringComparison.Ordinal))
                    code = "chat_not_in_session";
                else if (error.StartsWith("not_open", StringComparison.Ordinal))
                    code = "chat_not_open";
                else if (error.StartsWith("overlay_", StringComparison.Ordinal))
                    code = "chat_unavailable";
                else if (error.StartsWith("send_failed", StringComparison.Ordinal))
                    code = "chat_send_failed";
                else if (error.StartsWith("empty_text", StringComparison.Ordinal)
                    || error.StartsWith("too_long", StringComparison.Ordinal)
                    || error.StartsWith("native_keyboard_open", StringComparison.Ordinal))
                    code = "chat_inject_failed";
            }
            return new ChatAutomationException(code,
                string.IsNullOrEmpty(error) ? "chat automation failed without a reason" : error);
        }
    }
}
