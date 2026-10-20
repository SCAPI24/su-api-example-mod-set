using System;
using System.Collections.Generic;
using System.Text;
using Engine;
using Engine.Graphics;
using Engine.Input;
using Engine.Media;
using Game;
using GameEntitySystem;
using SuAPICore;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// 聊天 HUD：挂在每个 <see cref="GameWidget"/> 的 GuiWidget 上。
    ///
    /// 布局（**底部**、**非蓝色**）：
    ///   · 消息流 = 最近 8 条，垂直堆叠在底部中央、半透明暖色深底面板上；
    ///     每条 = "发送者：正文"，颜色来自暖色/中性调色板（金 / 橙 / 奶油 / 浅灰 …），
    ///     7 秒后开始淡出、8 秒移除；面板高度跟随消息实际排版高度；
    ///   · 输入行 = 底部输入框 + "发送"按钮（打开时才出现）；关闭时整块
    ///     <c>IsHitTestVisible=false</c>，不挡任何游戏输入；
    ///   · 触摸端常驻一个 "聊天" 按钮（右下、快捷栏上方），点它打开输入。
    ///
    /// 输入路径（引擎自己的做法，不破坏 Android 软键盘）：
    ///   · Windows / 桌面：HUD 输入控件自己读 <c>WidgetInput.LastChar / LastKey</c>
    ///     （与 <c>TextBoxWidget.Update</c> 同一条路径），并挂
    ///     <see cref="SuAPITextInput"/> 的 IME 采集器解决中文一次上屏多字；
    ///     输入期间每帧 <c>Keyboard.Clear()</c>，让 WASD / T（引擎的昼夜切换）等
    ///     游戏键不落到角色身上 —— 关闭时绝不调用，T/E 等正常交给游戏；
    ///   · Android / 触摸：直接走 <see cref="Keyboard.ShowKeyboard"/>（引擎原生
    ///     软键盘路径），回调文本 = 按"确定"，等价于回车发送。
    ///
    /// 无静态状态；所有控件 / IME 租约都由本实例持有，离开世界时 Dispose 清干净。
    /// </summary>
    internal sealed class ChatOverlayAttacher
    {
        private readonly ChatModule m_chat;
        private readonly Action<string> m_logInfo;
        private readonly Action<string> m_logWarn;
        private readonly List<Overlay> m_overlays = new List<Overlay>(2);

        public ChatOverlayAttacher(ChatModule chat, Action<string> logInfo = null,
            Action<string> logWarn = null)
        {
            m_chat = chat ?? throw new ArgumentNullException(nameof(chat));
            m_logInfo = logInfo;
            m_logWarn = logWarn;
        }

        public void Tick()
        {
            if (m_chat == null) return;
            Project project = GameManager.Project;
            if (project == null)
            {
                RemoveAll("离开世界");
                return;
            }
            SubsystemGameWidgets widgets = project.FindSubsystem<SubsystemGameWidgets>(false);
            if (widgets == null || widgets.GameWidgets.Count == 0)
            {
                RemoveAll("没有 GameWidget");
                return;
            }

            // 已经不在当前 GameWidget 列表里的 overlay（重进世界 / 分屏布局变化）必须摘掉。
            for (int i = m_overlays.Count - 1; i >= 0; i--)
            {
                Overlay overlay = m_overlays[i];
                bool present = false;
                for (int j = 0; j < widgets.GameWidgets.Count; j++)
                {
                    if (ReferenceEquals(widgets.GameWidgets[j], overlay.GameWidget))
                    {
                        present = true;
                        break;
                    }
                }
                if (present && overlay.IsAttached) continue;
                overlay.Dispose();
                m_overlays.RemoveAt(i);
            }

            bool session = m_chat.IsInSession;
            for (int i = 0; i < widgets.GameWidgets.Count; i++)
            {
                GameWidget gameWidget = widgets.GameWidgets[i];
                if (gameWidget == null || gameWidget.GuiWidget == null) continue;
                Overlay overlay = Find(gameWidget);
                if (overlay == null)
                {
                    overlay = new Overlay(gameWidget, m_chat, m_logInfo, m_logWarn);
                    if (!overlay.IsAttached)
                    {
                        overlay.Dispose();
                        continue;
                    }
                    m_overlays.Add(overlay);
                    m_logInfo?.Invoke("[SuMP][chat-ui] 聊天 HUD 已挂到 GameWidget#" +
                        gameWidget.GameWidgetIndex + "（底部消息流 + 暖色面板）");
                }
                overlay.Tick(session);
            }

            // 键盘入口只在"主 GameWidget"（分屏的第一个玩家）上处理，避免一个按键
            // 同时打开多个分屏的输入行。触摸按钮则每个 overlay 各自处理。
            if (session && TryOpenFromKeyboard())
            {
                Overlay primary = Find(widgets.GameWidgets[0]);
                primary?.OpenInput(fromTouch: false);
            }
        }

        public void Dispose() => RemoveAll("卸载");

        /// <summary>
        /// 是否有任何一个聊天输入行正打开（语音 U 键用：文本输入期间不能触发语音开关）。
        /// 只读查询，不改任何状态。
        /// </summary>
        public bool HasOpenInput
        {
            get
            {
                for (int i = 0; i < m_overlays.Count; i++)
                    if (m_overlays[i].IsAttached && m_overlays[i].IsInputOpen) return true;
                return false;
            }
        }

        // ---------------------------------------------------------------- 自动化入口
        // 只由同程序集的 ChatAutomation 调用（CmdBridge 经反射找它，不直接引用本类型）。
        // 失败字符串带稳定前缀；ChatAutomation 把它们翻译成明确错误码：
        //   not_in_session → chat_not_in_session / not_open → chat_not_open /
        //   overlay_* → chat_unavailable / 其余 → chat_inject_failed。

        /// <summary>当前是否在联机会话里（聊天 HUD 是否允许打开输入）。</summary>
        public bool IsInSession => m_chat != null && m_chat.IsInSession;

        /// <summary>当前打开的输入行正文（没有打开时为空串）。</summary>
        public string AutomationInputText
        {
            get
            {
                Overlay overlay = FindOpenOverlay();
                return overlay != null ? overlay.InputText : string.Empty;
            }
        }

        /// <summary>打开主 GameWidget 的输入行（自动化强制走 HUD 内联输入，不弹软键盘）。</summary>
        internal bool TryAutomationOpen(out string error)
        {
            error = null;
            if (!IsInSession)
            {
                error = "not_in_session: the local player is not in a multiplayer session "
                    + "(host not started / client not joined)";
                return false;
            }
            // 分屏下别为了"主 GameWidget"再开第二个输入行：任何一个已打开就是已打开。
            if (HasOpenInput) return true;
            Overlay primary = FindPrimaryAttached();
            if (primary == null)
            {
                error = "overlay_missing: the chat HUD is not attached to a GameWidget yet "
                    + "(enter a world and wait one frame)";
                return false;
            }
            return primary.TryOpenInput(automation: true, out error);
        }

        internal bool TryAutomationInject(string text, out string error)
        {
            Overlay overlay = FindOpenOverlay();
            if (overlay == null)
            {
                error = "not_open: the chat input line is closed (run chat.open first)";
                return false;
            }
            return overlay.TryInject(text, out error);
        }

        internal bool TryAutomationSubmit(out string error)
        {
            Overlay overlay = FindOpenOverlay();
            if (overlay == null)
            {
                error = "not_open: the chat input line is closed (run chat.open first)";
                return false;
            }
            return overlay.TrySubmitAutomation(out error);
        }

        internal bool TryAutomationCancel(out string error)
        {
            Overlay overlay = FindOpenOverlay();
            if (overlay == null)
            {
                error = "not_open: the chat input line is closed";
                return false;
            }
            overlay.CloseInput(clearText: true);
            error = null;
            return true;
        }

        private Overlay FindPrimaryAttached()
        {
            for (int i = 0; i < m_overlays.Count; i++)
                if (m_overlays[i].IsAttached) return m_overlays[i];
            return null;
        }

        private Overlay FindOpenOverlay()
        {
            for (int i = 0; i < m_overlays.Count; i++)
                if (m_overlays[i].IsAttached && m_overlays[i].IsInputOpen) return m_overlays[i];
            return null;
        }

        private Overlay Find(GameWidget gameWidget)
        {
            for (int i = 0; i < m_overlays.Count; i++)
                if (ReferenceEquals(m_overlays[i].GameWidget, gameWidget)) return m_overlays[i];
            return null;
        }

        private void RemoveAll(string reason)
        {
            if (m_overlays.Count == 0) return;
            for (int i = 0; i < m_overlays.Count; i++) m_overlays[i].Dispose();
            m_overlays.Clear();
            m_logInfo?.Invoke("[SuMP][chat-ui] 聊天 HUD 已移除（" + reason + "）");
        }

        /// <summary>
        /// 只有 Enter 打开输入（产品要求；T 不再打开聊天、关闭时直接交给游戏）。只在**正常游戏屏、无对话框、软键盘未打开**时生效；
        /// 打开时立刻 <c>Keyboard.Clear()</c>（在 <see cref="Overlay.OpenInput"/> 里），
        /// 所以打开输入的那次 Enter 不会同时落进输入框。
        /// </summary>
        private bool TryOpenFromKeyboard()
        {
            if (!(ScreensManager.CurrentScreen is GameScreen)) return false;
            if (DialogsManager.Dialogs.Count > 0) return false;
            if (Keyboard.IsKeyboardVisible) return false;
            if (!Keyboard.IsKeyDownOnce(Key.Enter)) return false;
            for (int i = 0; i < m_overlays.Count; i++)
                if (m_overlays[i].IsInputOpen) return false;
            return true;
        }
    }

    /// <summary>一个 GameWidget 上的聊天 HUD（消息流 / 输入行 / 触摸按钮）。</summary>
    internal sealed class Overlay
    {
        private const float PanelWidth = 520f;
        private const float InputRowHeight = 46f;
        private const float FeedBottomMargin = 8f;
        private const float InputOpenFeedLift = 54f;

        private readonly GameWidget m_gameWidget;
        private readonly ChatModule m_chat;
        private readonly Action<string> m_logInfo;
        private readonly Action<string> m_logWarn;
        private readonly CanvasWidget m_root;
        private readonly RectangleWidget m_feedPanel;
        private readonly StackPanelWidget m_feedStack;
        private readonly CanvasWidget m_inputRow;
        private readonly ChatTextBoxWidget m_input;
        private readonly BevelledButtonWidget m_sendButton;
        private readonly BevelledButtonWidget m_chatButton;
        private readonly List<FeedRow> m_rows = new List<FeedRow>(ChatFeed.MaxEntries);
        private readonly Color[] m_palette;
        private readonly Color m_textColor = new Color(245, 238, 225);
        private readonly Color m_systemColor = new Color(235, 198, 128);
        private readonly Color m_panelFill = new Color(32, 24, 16, 176);
        private readonly Color m_panelOutline = new Color(138, 104, 62, 200);
        private BitmapFont m_font;
        private IDisposable m_imeLease;
        private string m_feedSignature = string.Empty;
        private bool m_inputOpen;
        private bool m_nativeKeyboardOpen;
        private bool m_chatButtonConsumed;
        private bool m_sendButtonConsumed;
        private bool m_disposed;

        public Overlay(GameWidget gameWidget, ChatModule chat, Action<string> logInfo,
            Action<string> logWarn)
        {
            m_gameWidget = gameWidget;
            m_chat = chat;
            m_logInfo = logInfo;
            m_logWarn = logWarn;
            // 暖色 / 中性调色板：**没有任何蓝色**。同一发送者稳定取色。
            m_palette = new[]
            {
                new Color(255, 216, 150), // 金
                new Color(250, 178, 120), // 暖橙
                new Color(235, 205, 175), // 奶油
                new Color(240, 168, 158), // 珊瑚
                new Color(214, 196, 132), // 麦色
                new Color(222, 190, 168), // 暖玫瑰
                new Color(196, 214, 176), // 嫩绿
                new Color(226, 226, 214)  // 浅灰
            };
            try
            {
                m_font = SuAPIFonts.GetPericles18();
            }
            catch (Exception e)
            {
                m_logWarn?.Invoke("[SuMP][chat-ui] 加载 SuAPI 中文字体失败（" +
                    e.GetType().Name + " " + e.Message + "）→ 聊天 HUD 不创建" +
                    "（不退回会丢中文的默认字体）");
                return;
            }
            if (m_font == null)
            {
                m_logWarn?.Invoke("[SuMP][chat-ui] 没有拿到 SuAPI 中文字体（SuAPIFonts.GetPericles18）" +
                    " → 聊天 HUD 不创建（不退回会丢字的默认字体）");
                return;
            }
            ContainerWidget gui = gameWidget.GuiWidget;
            if (gui == null)
            {
                m_logWarn?.Invoke("[SuMP][chat-ui] GameWidget#" + gameWidget.GameWidgetIndex +
                    " 没有 GuiWidget → 聊天 HUD 不创建");
                return;
            }

            m_root = new CanvasWidget
            {
                Name = "SuMultiplayerChatOverlay",
                Size = new Vector2(PanelWidth, 420f),
                HorizontalAlignment = WidgetAlignment.Center,
                VerticalAlignment = WidgetAlignment.Far,
                Margin = new Vector2(0f, 140f),
                IsHitTestVisible = false,
                ClampToBounds = false,
                IsVisible = false
            };
            gui.Children.Add(m_root);

            m_feedPanel = new RectangleWidget
            {
                Name = "SuMultiplayerChatPanel",
                Size = new Vector2(PanelWidth, 0f),
                HorizontalAlignment = WidgetAlignment.Center,
                VerticalAlignment = WidgetAlignment.Far,
                Margin = new Vector2(0f, FeedBottomMargin),
                FillColor = m_panelFill,
                OutlineColor = m_panelOutline,
                OutlineThickness = 1f,
                IsHitTestVisible = false,
                IsVisible = false
            };
            m_root.Children.Add(m_feedPanel);

            m_feedStack = new StackPanelWidget
            {
                Name = "SuMultiplayerChatFeed",
                Direction = LayoutDirection.Vertical,
                HorizontalAlignment = WidgetAlignment.Center,
                VerticalAlignment = WidgetAlignment.Far,
                Margin = new Vector2(10f, FeedBottomMargin + 6f),
                IsHitTestVisible = false,
                IsVisible = false
            };
            m_root.Children.Add(m_feedStack);

            m_inputRow = new CanvasWidget
            {
                Name = "SuMultiplayerChatInputRow",
                Size = new Vector2(PanelWidth, InputRowHeight),
                HorizontalAlignment = WidgetAlignment.Center,
                VerticalAlignment = WidgetAlignment.Far,
                IsHitTestVisible = true,
                IsVisible = false
            };
            m_root.Children.Add(m_inputRow);

            var inputBackground = new RectangleWidget
            {
                Name = "SuMultiplayerChatInputBackground",
                Size = new Vector2(PanelWidth, InputRowHeight),
                HorizontalAlignment = WidgetAlignment.Center,
                VerticalAlignment = WidgetAlignment.Center,
                FillColor = m_panelFill,
                OutlineColor = m_panelOutline,
                OutlineThickness = 1f,
                IsHitTestVisible = false
            };
            m_inputRow.Children.Add(inputBackground);

            m_input = new ChatTextBoxWidget(m_font)
            {
                Name = "SuMultiplayerChatInput",
                Size = new Vector2(PanelWidth - 128f, InputRowHeight - 8f),
                HorizontalAlignment = WidgetAlignment.Near,
                VerticalAlignment = WidgetAlignment.Center,
                Margin = new Vector2(10f, 0f),
                TextColor = m_textColor,
                MaximumLength = ChatTextPolicy.MaxTextLength
            };
            m_input.Enter += OnInputEnter;
            m_input.Escape += OnInputEscape;
            m_inputRow.Children.Add(m_input);

            m_sendButton = CreateButton("SuMultiplayerChatSendButton", "发送", 96f, 36f,
                new Color(122, 88, 44));
            m_sendButton.HorizontalAlignment = WidgetAlignment.Far;
            m_sendButton.VerticalAlignment = WidgetAlignment.Center;
            m_sendButton.Margin = new Vector2(6f, 0f);
            m_sendButton.Font = m_font;
            m_inputRow.Children.Add(m_sendButton);
            // 点"发送"按钮不能被输入框当成"点输入框之外 = 取消"。
            m_input.ClickExempt = m_sendButton;

            // 触摸端的常驻入口：右下、快捷栏上方。桌面也同样可见（鼠标可点）。
            m_chatButton = CreateButton("SuMultiplayerChatButton", "聊天", 76f, 56f,
                new Color(122, 88, 44));
            m_chatButton.HorizontalAlignment = WidgetAlignment.Far;
            m_chatButton.VerticalAlignment = WidgetAlignment.Far;
            m_chatButton.Margin = new Vector2(0f, 236f);
            m_chatButton.Font = m_font;
            m_chatButton.IsVisible = false;
            gui.Children.Add(m_chatButton);

            IsAttached = true;
        }

        public GameWidget GameWidget => m_gameWidget;

        public bool IsAttached { get; private set; }

        public bool IsInputOpen => m_inputOpen;

        public void Tick(bool session)
        {
            if (m_disposed || !IsAttached) return;
            m_root.IsVisible = session;
            m_chatButton.IsVisible = session;
            if (!session)
            {
                if (m_inputOpen) CloseInput(clearText: true);
                m_feedSignature = string.Empty;
                m_feedStack.Children.Clear();
                m_rows.Clear();
                m_feedPanel.IsVisible = false;
                m_feedStack.IsVisible = false;
                return;
            }

            double now = Time.RealTime;
            UpdateFeed(now);
            UpdateChatButton();
            UpdateSendButton();
            // 输入行打开期间：对话框出现（触摸打开背包等）或软键盘被系统关掉时，
            // 明确关闭输入，避免焦点留在看不见的控件上。
            if (m_inputOpen && !m_nativeKeyboardOpen && DialogsManager.Dialogs.Count > 0)
                CloseInput(clearText: true);
            if (m_inputOpen && m_nativeKeyboardOpen && !Keyboard.IsKeyboardVisible)
            {
                // 系统软键盘被系统 / 返回键关掉：回调可能不会来，这里按取消处理。
                CloseInput(clearText: true);
            }
        }

        // ------------------------------------------------------------ 消息流

        private void UpdateFeed(double now)
        {
            IReadOnlyList<ChatFeedEntry> entries = m_chat.Feed.Entries;
            var signature = new StringBuilder(entries.Count * 12 + 8);
            signature.Append(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                signature.Append(':').Append(entries[i].MessageId);
                signature.Append('@').Append(entries[i].SenderClientId);
            }
            string value = signature.ToString();
            if (!string.Equals(value, m_feedSignature, StringComparison.Ordinal))
            {
                m_feedSignature = value;
                RebuildRows(entries);
            }
            for (int i = 0; i < m_rows.Count && i < entries.Count; i++)
            {
                FeedRow row = m_rows[i];
                float alpha = entries[i].Alpha(now);
                row.Label.Color = WithAlpha(RowColor(entries[i]), alpha);
                row.Label.IsVisible = alpha > 0.001f;
            }
            bool any = entries.Count > 0;
            m_feedPanel.IsVisible = any;
            m_feedStack.IsVisible = any;
            if (any)
            {
                float height = MathUtils.Max(m_feedStack.ParentDesiredSize.Y + 12f, 20f);
                m_feedPanel.Size = new Vector2(PanelWidth, height);
                float lift = m_inputOpen && m_inputRow.IsVisible ? InputOpenFeedLift : 0f;
                m_feedPanel.Margin = new Vector2(0f, FeedBottomMargin + lift);
                m_feedStack.Margin = new Vector2(10f, FeedBottomMargin + 6f + lift);
            }
        }

        private void RebuildRows(IReadOnlyList<ChatFeedEntry> entries)
        {
            m_feedStack.Children.Clear();
            m_rows.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                ChatFeedEntry entry = entries[i];
                var label = new LabelWidget
                {
                    Name = "SuMultiplayerChatLine" + i,
                    Text = FormatLine(entry),
                    Font = m_font,
                    FontScale = 1f,
                    FontSpacing = new Vector2(0.5f, 0f),
                    Color = RowColor(entry),
                    DropShadow = true,
                    TextureLinearFilter = true,
                    Size = new Vector2(PanelWidth - 28f, -1f),
                    WordWrap = true,
                    MaxLines = 2,
                    Ellipsis = true,
                    IsHitTestVisible = false
                };
                m_feedStack.Children.Add(label);
                m_rows.Add(new FeedRow { Entry = entry, Label = label });
            }
        }

        private static string FormatLine(ChatFeedEntry entry)
        {
            if (entry.SenderClientId < 0) return entry.Text ?? string.Empty;
            string name = string.IsNullOrEmpty(entry.SenderName) ? "Player" : entry.SenderName;
            return name + "：" + (entry.Text ?? string.Empty);
        }

        /// <summary>暖色 / 中性调色板取色（**绝不返回蓝色**）；系统提示固定暖黄。</summary>
        private Color RowColor(ChatFeedEntry entry)
        {
            if (entry.SenderClientId < 0) return m_systemColor;
            int hash = 17;
            string key = string.IsNullOrEmpty(entry.SenderIdentity)
                ? "client:" + entry.SenderClientId
                : entry.SenderIdentity;
            for (int i = 0; i < key.Length; i++) hash = unchecked(hash * 31 + key[i]);
            return m_palette[(hash & int.MaxValue) % m_palette.Length];
        }

        private static Color WithAlpha(Color color, float alpha) =>
            new Color(color.R, color.G, color.B,
                (byte)MathUtils.Clamp(color.A * alpha, 0f, 255f));

        private sealed class FeedRow
        {
            public ChatFeedEntry Entry;
            public LabelWidget Label;
        }

        // ------------------------------------------------------------ 输入

        private void UpdateSendButton()
        {
            if (!m_inputOpen || m_sendButton == null) return;
            if (!m_sendButton.IsClicked)
            {
                m_sendButtonConsumed = false;
                return;
            }
            if (m_sendButtonConsumed) return;
            m_sendButtonConsumed = true;
            SubmitInput();
        }

        private void UpdateChatButton()
        {
            if (!m_chatButton.IsClicked)
            {
                m_chatButtonConsumed = false;
                return;
            }
            if (m_chatButtonConsumed) return;
            m_chatButtonConsumed = true;
            OpenInput(fromTouch: true);
        }

        public string InputText => m_input != null ? m_input.Text : string.Empty;

        /// <summary>
        /// 打开输入。Android / iOS 走引擎原生 <see cref="Keyboard.ShowKeyboard"/>；
        /// 桌面（键盘 Enter 或点屏幕按钮）走 HUD 内联输入 + SuAPI IME 采集。
        /// <paramref name="fromTouch"/> 只用于日志（平台能力决定路径，不由调用点猜）。
        /// 打不开时写一行明确原因（调用者不再自行猜测）。
        /// </summary>
        public void OpenInput(bool fromTouch)
        {
            if (TryOpenInput(automation: false, out string error)) return;
            m_logWarn?.Invoke("[SuMP][chat-ui] 聊天输入未打开：" + error);
        }

        /// <summary>
        /// 打开输入的唯一实现。<paramref name="automation"/> = true 时**强制**走 HUD 内联输入
        /// （自动化不敲软键盘，也不持有 IME 租约），其余与手打开完全同一条路径。
        /// </summary>
        internal bool TryOpenInput(bool automation, out string error)
        {
            error = null;
            if (m_disposed || !IsAttached)
            {
                error = "overlay_not_attached: this chat overlay is detached";
                return false;
            }
            if (m_inputOpen) return true;
            if (!m_chat.IsInSession)
            {
                error = "not_in_session: the local player is not in a multiplayer session";
                return false;
            }
            m_inputOpen = true;
            m_sendButtonConsumed = false;
            m_input.Text = string.Empty;
            m_input.CaretPosition = 0;
            // 触摸端 / Android 走引擎原生软键盘；桌面（包括点屏幕上的"聊天"按钮）
            // 走 HUD 内联输入。Windows 的 Keyboard.ShowKeyboard 是空实现（只会 cancel），
            // 不能按 fromTouch 直接走原生路径。自动化**永远**走内联输入。
            if (!automation && (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()))
            {
                OpenNativeKeyboard();
                return true;
            }
            m_nativeKeyboardOpen = false;
            m_inputRow.IsVisible = true;
            DrainPendingKeyboardInput();
            m_input.HasFocus = true;
            if (!automation)
            {
                try
                {
                    m_imeLease = SuAPITextInput.Attach(m_input, holdImeContext: true);
                }
                catch (Exception e)
                {
                    m_logWarn?.Invoke("[SuMP][chat-ui] 聊天输入挂 IME 采集器失败（" +
                        e.GetType().Name + " " + e.Message + "）→ 仅单字符输入；中文 IME 可能丢字" +
                        "（这是显式的降级，不是正常路径）");
                }
            }
            m_logInfo?.Invoke(automation
                ? "[SuMP][chat-ui] 聊天输入已由自动化打开（HUD 内联输入，不弹软键盘）"
                : "[SuMP][chat-ui] 聊天输入已打开（回车发送 / Esc 取消）");
            return true;
        }

        /// <summary>把外部文本追加到当前输入缓冲区（净化 / 上限走 ChatInjectionPolicy）。</summary>
        internal bool TryInject(string raw, out string error)
        {
            error = null;
            if (m_disposed || !IsAttached)
            {
                error = "overlay_not_attached: this chat overlay is detached";
                return false;
            }
            if (!m_inputOpen)
            {
                error = "not_open: the chat input line is closed";
                return false;
            }
            if (m_nativeKeyboardOpen)
            {
                error = "native_keyboard_open: the native soft keyboard owns this input; "
                    + "injection is only supported on the HUD inline input";
                return false;
            }
            if (!ChatInjectionPolicy.TryCompose(m_input.Text, raw, out string combined, out error))
                return false;
            m_input.Text = combined;
            m_input.CaretPosition = m_input.Text.Length;
            m_input.HasFocus = true;
            m_logInfo?.Invoke("[SuMP][chat-ui] 自动化注入聊天文本（最终 " + m_input.Text.Length +
                " UTF-16 码元）");
            return true;
        }

        /// <summary>与按回车完全同一条发送路径（净化 / 限速 / 网络 / 主机拒绝都由 ChatModule 负责）。</summary>
        internal bool TrySubmitAutomation(out string error)
        {
            error = null;
            if (m_disposed || !IsAttached)
            {
                error = "overlay_not_attached: this chat overlay is detached";
                return false;
            }
            if (!m_inputOpen)
            {
                error = "not_open: the chat input line is closed";
                return false;
            }
            if (m_nativeKeyboardOpen)
            {
                error = "native_keyboard_open: the native soft keyboard owns this input; "
                    + "submit is only supported on the HUD inline input";
                return false;
            }
            if (!TryConsumeAndSend(out string text, out error)) return false;
            m_logInfo?.Invoke("[SuMP][chat-ui] 自动化已提交聊天（" + text.Length +
                " 字符），等待主机回显");
            return true;
        }

        private void OpenNativeKeyboard()
        {
            m_nativeKeyboardOpen = true;
            m_inputRow.IsVisible = false;
            m_input.HasFocus = false;
            m_logInfo?.Invoke("[SuMP][chat-ui] 聊天输入：打开系统软键盘（确定 = 发送）");
            Keyboard.ShowKeyboard("聊天", "输入消息（确定发送，取消返回游戏）", string.Empty,
                false, OnNativeKeyboardAccepted, OnNativeKeyboardCanceled);
        }

        private void OnNativeKeyboardAccepted(string text)
        {
            m_nativeKeyboardOpen = false;
            if (m_disposed) return;
            m_input.Text = text ?? string.Empty;
            SubmitInput();
        }

        private void OnNativeKeyboardCanceled()
        {
            m_nativeKeyboardOpen = false;
            if (m_disposed) return;
            CloseInput(clearText: true);
        }

        private void OnInputEnter(ChatTextBoxWidget widget) => SubmitInput();

        private void OnInputEscape(ChatTextBoxWidget widget) => CloseInput(clearText: true);

        private void SubmitInput()
        {
            if (m_disposed) return;
            if (TryConsumeAndSend(out string text, out string error))
            {
                m_logInfo?.Invoke("[SuMP][chat-ui] 聊天已提交（" + text.Length +
                    " 字符），等待主机回显");
                return;
            }
            if (error.StartsWith("empty_text:", StringComparison.Ordinal))
                m_logInfo?.Invoke("[SuMP][chat-ui] 空消息未发送（空白消息被明确拒绝，不广播）");
            else
                m_logWarn?.Invoke("[SuMP][chat-ui] 聊天未发送：" + error);
        }

        /// <summary>
        /// 取走输入框文本并交给 ChatModule（关输入 → 空消息明确拒绝 → 真实发送链）。
        /// 手按回车与自动化提交共用这一条路径，区别只在日志。
        /// </summary>
        private bool TryConsumeAndSend(out string text, out string error)
        {
            error = null;
            text = m_input != null ? m_input.Text : string.Empty;
            CloseInput(clearText: true);
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "empty_text: the chat input is empty (whitespace-only messages are rejected)";
                return false;
            }
            if (!m_chat.TrySendLocalText(text, out string reason))
            {
                error = "send_failed: " + (string.IsNullOrEmpty(reason) ? "ChatModule rejected the message" : reason);
                return false;
            }
            return true;
        }

        public void CloseInput(bool clearText)
        {
            if (!m_inputOpen) return;
            m_inputOpen = false;
            if (m_input == null) return;
            m_inputRow.IsVisible = false;
            m_input.HasFocus = false;
            if (m_imeLease != null)
            {
                try { m_imeLease.Dispose(); }
                catch (Exception e)
                {
                    m_logWarn?.Invoke("[SuMP][chat-ui] 释放 IME 租约失败：" + e.GetType().Name +
                        " " + e.Message);
                }
                m_imeLease = null;
            }
            m_nativeKeyboardOpen = false;
            if (clearText) m_input.Text = string.Empty;
            m_input.CaretPosition = 0;
            DrainPendingKeyboardInput();
            m_logInfo?.Invoke("[SuMP][chat-ui] 聊天输入已关闭");
        }

        private static void DrainPendingKeyboardInput()
        {
            // 与 TextBoxWidget 相同的"打开 / 关闭时排空"：启动以来的积压字符绝不能灌进
            // 刚打开的输入框；按住的方向键也必须在交还游戏输入前清掉。
            // Source: Survivalcraft/Game/TextBoxWidget.cs:TextBoxWidget.HasFocus
            KeyboardInput.GetInput();
            KeyboardInput.DeletePressed = false;
            Keyboard.Clear();
        }

        // ------------------------------------------------------------ 生命周期

        public void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            if (m_inputOpen) CloseInput(clearText: true);
            if (m_input != null)
            {
                m_input.Enter -= OnInputEnter;
                m_input.Escape -= OnInputEscape;
            }
            if (m_root != null && m_root.ParentWidget != null)
                m_root.ParentWidget.Children.Remove(m_root);
            if (m_chatButton != null && m_chatButton.ParentWidget != null)
                m_chatButton.ParentWidget.Children.Remove(m_chatButton);
            m_rows.Clear();
            if (m_feedStack != null) m_feedStack.Children.Clear();
            IsAttached = false;
        }

        private static BevelledButtonWidget CreateButton(string name, string text, float width,
            float height, Color centerColor)
        {
            return new BevelledButtonWidget
            {
                Name = name,
                Text = text,
                Size = new Vector2(width, height),
                Color = Color.White,
                CenterColor = centerColor,
                BevelColor = new Color(150, 130, 100)
            };
        }
    }

    /// <summary>
    /// HUD 内联文本输入控件。
    ///
    /// 它刻意提供与引擎 <c>TextBoxWidget</c> 同名的公开属性（Text / CaretPosition /
    /// MaximumLength / HasFocus），这样 <see cref="SuAPITextInput.Attach"/> 的
    /// <c>TextBoxAccessor</c> 能直接接管它（SuAPITextInput 通过反射读这几个属性，
    /// 注释见 EntitySystem/SuAPICore/SuAPITextInput.cs:TextBoxAccessor）。
    ///
    /// 按键处理与 <c>TextBoxWidget.Update</c> 完全同源：LastChar 单字符、LastKey 的
    /// Backspace/Delete/Left/Right/Home/End/Enter/Escape，处理后 <c>Input.Clear()</c>。
    /// 额外做的一步：输入期间每帧 <c>Keyboard.Clear()</c>，让 WASD / T 等游戏键
    /// 不落到角色上（关闭输入后不再调用，正常游戏输入完全不受影响）。
    /// </summary>
    internal sealed class ChatTextBoxWidget : Widget
    {
        private readonly BitmapFont m_font;
        private string m_text = string.Empty;
        private int m_caretPosition;
        private bool m_hasFocus;
        private int m_maximumLength = ChatTextPolicy.MaxTextLength;

        public ChatTextBoxWidget(BitmapFont font)
        {
            m_font = font ?? throw new ArgumentNullException(nameof(font));
            ClampToBounds = true;
            IsHitTestVisible = true;
            TextColor = Color.White;
        }

        public Vector2 Size { get; set; } = new Vector2(-1f);

        public Color TextColor { get; set; }

        /// <summary>点在这个控件（或其子树）上不算"点输入框之外"（例如同排的发送按钮）。</summary>
        public Widget ClickExempt { get; set; }

        public string Text
        {
            get => m_text;
            set
            {
                string text = value ?? string.Empty;
                if (text.Length > m_maximumLength)
                    text = TruncateUtf16(text, m_maximumLength);
                if (string.Equals(text, m_text, StringComparison.Ordinal)) return;
                m_text = text;
                if (m_caretPosition > m_text.Length) m_caretPosition = m_text.Length;
            }
        }

        public int MaximumLength
        {
            get => m_maximumLength;
            set
            {
                m_maximumLength = MathUtils.Max(value, 0);
                if (m_text.Length > m_maximumLength) Text = TruncateUtf16(m_text, m_maximumLength);
            }
        }

        public int CaretPosition
        {
            get => m_caretPosition;
            set => m_caretPosition = MathUtils.Clamp(value, 0, m_text.Length);
        }

        public bool HasFocus
        {
            get => m_hasFocus;
            set => m_hasFocus = value;
        }

        public event Action<ChatTextBoxWidget> Enter;
        public event Action<ChatTextBoxWidget> Escape;

        public override void Update()
        {
            WidgetInput input = Input;
            if (input == null) return;
            if (!m_hasFocus)
            {
                if (input.Click.HasValue)
                {
                    Widget hit = HitTestGlobal(input.Click.Value.Start);
                    if (ReferenceEquals(hit, this)) m_hasFocus = true;
                }
                return;
            }

            // 点击输入框之外 = 取消（与对话框点遮罩关闭一致），但保留已输入文本由调用方决定。
            if (input.Click.HasValue)
            {
                Widget hit = HitTestGlobal(input.Click.Value.Start);
                if (!ReferenceEquals(hit, this) && !IsWithinExempt(hit))
                {
                    m_hasFocus = false;
                    Escape?.Invoke(this);
                    return;
                }
            }

            // 1) IME 一次上屏的整串（SuAPITextInput 的采集器通常已先处理并清空这里；
            //    没挂上采集器时这一段仍然能拿到整串，避免中文连打丢字）。
            if (KeyboardInput.Chars.Count > 0)
            {
                string committed = KeyboardInput.GetInput();
                InsertPrintable(committed);
                input.Clear();
            }

            // 2) 单字符（与 TextBoxWidget.cs:164-172 同一条路径）。
            if (input.LastChar.HasValue)
            {
                char character = input.LastChar.Value;
                if (!char.IsControl(character)) Insert(character.ToString());
                input.Clear();
            }

            // 3) 编辑键 / 回车 / Esc（与 TextBoxWidget.cs:174-237 同源）。
            if (input.LastKey.HasValue)
            {
                HandleKey(input.LastKey.Value);
                input.Clear();
            }

            // 4) 输入期间抑制游戏键：引擎的移动是 Keyboard.IsKeyDown 轮询，
            //    必须清掉按住状态；关闭输入后不再调用。
            Keyboard.Clear();
        }

        private bool IsWithinExempt(Widget hit)
        {
            Widget current = hit;
            while (current != null)
            {
                if (ReferenceEquals(current, ClickExempt)) return true;
                current = current.ParentWidget;
            }
            return false;
        }

        private void HandleKey(Key key)
        {
            switch (key)
            {
                case Key.BackSpace:
                    if (m_caretPosition > 0)
                    {
                        int length = 1;
                        if (m_caretPosition >= 2 &&
                            char.IsLowSurrogate(m_text[m_caretPosition - 1]) &&
                            char.IsHighSurrogate(m_text[m_caretPosition - 2]))
                        {
                            length = 2;
                        }
                        m_text = m_text.Remove(m_caretPosition - length, length);
                        m_caretPosition -= length;
                    }
                    break;
                case Key.Delete:
                    if (m_caretPosition < m_text.Length)
                    {
                        int length = 1;
                        if (m_caretPosition + 1 < m_text.Length &&
                            char.IsHighSurrogate(m_text[m_caretPosition]) &&
                            char.IsLowSurrogate(m_text[m_caretPosition + 1]))
                        {
                            length = 2;
                        }
                        m_text = m_text.Remove(m_caretPosition, length);
                    }
                    break;
                case Key.LeftArrow:
                    m_caretPosition = MathUtils.Max(0, m_caretPosition - 1);
                    if (m_caretPosition > 0 && char.IsLowSurrogate(m_text[m_caretPosition]))
                        m_caretPosition--;
                    break;
                case Key.RightArrow:
                    m_caretPosition = MathUtils.Min(m_text.Length, m_caretPosition + 1);
                    if (m_caretPosition < m_text.Length &&
                        char.IsHighSurrogate(m_text[m_caretPosition - 1]))
                        m_caretPosition++;
                    break;
                case Key.Home:
                    m_caretPosition = 0;
                    break;
                case Key.End:
                    m_caretPosition = m_text.Length;
                    break;
                case Key.Enter:
                    m_hasFocus = false;
                    Enter?.Invoke(this);
                    break;
                case Key.Escape:
                    m_hasFocus = false;
                    Escape?.Invoke(this);
                    break;
            }
        }

        private void InsertPrintable(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
                if (!char.IsControl(character)) builder.Append(character);
            if (builder.Length == 0) return;
            Insert(builder.ToString());
        }

        private void Insert(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            int available = m_maximumLength - m_text.Length;
            if (available <= 0) return;
            if (value.Length > available) value = TruncateUtf16(value, available);
            if (value.Length == 0) return;
            m_text = m_text.Insert(MathUtils.Clamp(m_caretPosition, 0, m_text.Length), value);
            m_caretPosition += value.Length;
        }

        /// <summary>按 UTF-16 长度截断，但绝不把代理对切成两半。</summary>
        private static string TruncateUtf16(string text, int maximum)
        {
            if (text.Length <= maximum) return text;
            int length = MathUtils.Max(maximum, 0);
            if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, length);
        }

        public override void MeasureOverride(Vector2 parentAvailableSize)
        {
            IsDrawRequired = true;
            DesiredSize = new Vector2(
                Size.X < 0f ? 200f : Size.X,
                Size.Y < 0f ? m_font.GlyphHeight * m_font.Scale + 8f : Size.Y);
        }

        public override void Draw(DrawContext dc)
        {
            Color color = TextColor * GlobalColorTransform;
            float available = ActualSize.X - 4f;
            if (available <= 0f) return;
            string text = m_text;
            float textWidth = text.Length == 0
                ? 0f
                : m_font.MeasureText(text, Vector2.One, Vector2.Zero).X;
            float caretX = m_font.CalculateCharacterPosition(text, m_caretPosition,
                Vector2.One, Vector2.Zero);
            float scroll = 0f;
            if (caretX > available) scroll = caretX - available + 2f;
            Vector2 position = new Vector2(2f - scroll, ActualSize.Y / 2f);
            SamplerState sampler = SamplerState.LinearClamp;
            FontBatch2D fontBatch = dc.PrimitivesRenderer2D.FontBatch(m_font, 1,
                DepthStencilState.None, null, null, sampler);
            int vertexCount = fontBatch.TriangleVertices.Count;
            if (text.Length > 0)
            {
                fontBatch.QueueText(text, position, 0f, color, TextAnchor.VerticalCenter,
                    Vector2.One, Vector2.Zero);
            }
            else if (m_hasFocus)
            {
                fontBatch.QueueText("输入消息…", position, 0f, color * 0.55f,
                    TextAnchor.VerticalCenter, Vector2.One, Vector2.Zero);
            }
            fontBatch.TransformTriangles(GlobalTransform, vertexCount);

            if (!m_hasFocus) return;
            if (MathUtils.Remainder(Time.RealTime, 0.5) >= 0.25) return;
            float x = 2f + caretX - scroll;
            if (x < 0f || x > ActualSize.X) return;
            FlatBatch2D flatBatch = dc.PrimitivesRenderer2D.FlatBatch(1,
                DepthStencilState.None);
            int flatCount = flatBatch.TriangleVertices.Count;
            flatBatch.QueueQuad(
                new Vector2(x, ActualSize.Y / 2f - m_font.GlyphHeight * 0.5f),
                new Vector2(x + 1f, ActualSize.Y / 2f + m_font.GlyphHeight * 0.5f),
                0f, color);
            flatBatch.TransformTriangles(GlobalTransform, flatCount);
            _ = textWidth;
        }
    }
}
