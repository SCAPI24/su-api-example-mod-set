using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Engine;
using Engine.Graphics;
using Engine.Input;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Voice;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// 游戏内语音 HUD（**唯一**把语音状态画到引擎控件上的地方）：
    ///   · **语音状态图标**（按钮名 <c>SuMPVoiceIcon</c>）：右下角、**聊天按钮正上方**，
    ///     紧凑的麦克风图标按钮。**唯一的语音开关**：点一下切换 ON/OFF；ON = 绿底白麦，
    ///     OFF = 灰底灰麦 + 红色斜杠，状态一眼可辨（进房间默认 OFF，离开房间自动 OFF
    ///     并释放麦克风）。Windows 上同一个开关也绑 **U 键**（边沿触发，一次按下 =
    ///     一次切换；聊天输入 / 文本框 / 对话框 / 软键盘打开时忽略）。
    ///   · 右上角状态面板（不重复放开关）：状态行、SPEAKING 行、可选 PTT 模式下的
    ///     `TALK`（**只有触摸点按，不绑定任何键盘键**）、`MUTE ALL` + 每个远端的静音按钮。
    ///
    /// 挂载方式与 RoomUiAttacher 相同：每帧在 SubsystemGameWidgets 的每个
    /// GameWidget 上找/建同一个面板与图标 —— 不替换任何子系统，也没有静态状态。
    /// 面板 / 图标只读模块的公开状态，所有动作都调用模块的公开方法。
    /// </summary>
    public sealed class VoiceHud
    {
        private const string PanelName = "SuMultiplayerVoicePanel";
        private const string StatusName = "SuMPVoiceStatus";
        private const string SpeakingName = "SuMPVoiceSpeaking";
        private const string ButtonRowName = "SuMPVoiceButtons";
        private const string PeerRowName = "SuMPVoicePeers";
        private const string IconName = "SuMPVoiceIcon";
        private const string IconGlyphName = "SuMPVoiceIconGlyph";
        private const string TalkName = "SuMPVoiceTalk";
        private const string MuteName = "SuMPVoiceMute";

        private sealed class PanelRefs
        {
            public StackPanelWidget Panel;
            public StackPanelWidget PeerRow;
            public LabelWidget Status;
            public LabelWidget Speaking;
            public BevelledButtonWidget Talk;
            public BevelledButtonWidget Mute;
            public BevelledButtonWidget Icon;
            public VoiceMicIconWidget IconGlyph;
            public readonly VoiceIconStateTracker IconState = new VoiceIconStateTracker();
            public bool IconClickConsumed;
            public string PeerSignature = string.Empty;
            public readonly List<BevelledButtonWidget> PeerButtons =
                new List<BevelledButtonWidget>();
            public readonly List<int> PeerIds = new List<int>();
        }

        private readonly VoiceChatModule m_module;
        private readonly Func<bool> m_isTextInputOpen;
        private readonly VoiceToggleGate m_toggleGate = new VoiceToggleGate();
        private readonly Dictionary<StackPanelWidget, PanelRefs> m_refs =
            new Dictionary<StackPanelWidget, PanelRefs>();
        private readonly List<StackPanelWidget> m_panels = new List<StackPanelWidget>();

        private MethodInfo m_isKeyDownOnce;
        private object m_uKey;
        private bool m_keyboardReady;
        private bool m_keyboardFailed;
        private bool m_textInputProbeFailed;

        // 帧驱动计数：面板 / 图标缺失、重建、更新异常都只计数 + 限流写日志，绝不每帧抛。
        private long m_panelCreates;
        private long m_panelMisses;
        private long m_structuralRebuilds;
        private long m_updateFailures;
        private long m_tickFailures;
        private long m_keyboardFailures;
        // 图标专用计数：创建 / 状态视觉更新 / 点击切换 / U 键切换 / 父容器缺失 / 清理残留。
        private long m_iconCreates;
        private long m_iconUpdates;
        private long m_iconToggles;
        private long m_iconKeyToggles;
        private long m_iconParentMisses;
        private long m_iconOrphans;
        private readonly Dictionary<string, long> m_lastLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public VoiceHud(VoiceChatModule module, Func<bool> isTextInputOpen = null)
        {
            m_module = module ?? throw new ArgumentNullException(nameof(module));
            m_isTextInputOpen = isTextInputOpen;
        }

        public void Tick()
        {
            // 兜底隔离：即使某条引擎控件路径意外抛异常，也只计数 + 限流一行，
            // 不让异常冒泡到 Frame.Update（那会变成每帧 ERROR 洪水）。
            try
            {
                TickCore();
            }
            catch (Exception e)
            {
                m_tickFailures++;
                WarnRateLimited("voice-hud-tick",
                    "[SuMP][ui] 语音 HUD 帧驱动异常（已隔离，下一帧继续）：" +
                    e.GetType().Name + " " + e.Message);
            }
        }

        private void TickCore()
        {
            Project project = GameManager.Project;
            if (project == null)
            {
                m_refs.Clear();
                m_panels.Clear();
                m_toggleGate.Reset();
                return;
            }
            SubsystemGameWidgets widgets = project.FindSubsystem<SubsystemGameWidgets>(false);
            if (widgets == null) return;

            // 先读 U 键：同一个按键帧里图标就能显示新状态（点击图标也是先切换再套视觉）。
            try
            {
                UpdateKeyboard();
            }
            catch (Exception e)
            {
                m_keyboardFailures++;
                WarnRateLimited("voice-hud-keyboard",
                    "[SuMP][ui] 语音快捷键读取异常（已隔离，HUD 图标仍可用）：" +
                    e.GetType().Name + " " + e.Message);
            }

            ReadOnlyList<GameWidget> gameWidgets = widgets.GameWidgets;
            for (int i = 0; i < gameWidgets.Count; i++)
            {
                GameWidget gameWidget = gameWidgets[i];
                if (gameWidget == null) continue;
                EnsurePanel(gameWidget);
            }
        }

        /// <summary>
        /// 每个 GameWidget 上**幂等**地保证语音面板 + 图标存在：
        ///   · 找不到面板 → 计数 + 创建（创建失败不抛，下一帧继续尝试）；
        ///   · 面板在但结构不完整（被别的代码改过 / 旧版本留下的半面板 / 图标丢了）
        ///     → 摘掉面板与图标重建；
        ///   · 更新单个面板失败只计数 + 限流日志，不打断其它 GameWidget，也不冒泡到 Frame.Update。
        /// </summary>
        private void EnsurePanel(GameWidget gameWidget)
        {
            StackPanelWidget panel = gameWidget.Children.Find<StackPanelWidget>(PanelName, false);
            PanelRefs refs = null;
            if (panel != null)
            {
                if (!m_refs.TryGetValue(panel, out refs) || refs.Panel != panel)
                {
                    refs = BindPanel(gameWidget, panel);
                    if (refs == null)
                    {
                        m_structuralRebuilds++;
                        WarnRateLimited("voice-hud-rebuild",
                            "[SuMP][ui] 语音 HUD 面板/图标结构不完整 → 已移除，本帧重建");
                        RemovePanel(gameWidget, panel);
                        panel = null;
                    }
                }
            }
            if (panel == null)
            {
                m_panelMisses++;
                refs = CreatePanel(gameWidget);
                if (refs == null)
                {
                    WarnRateLimited("voice-hud-missing",
                        "[SuMP][ui] 语音 HUD 面板/图标缺失且本帧创建失败" +
                        "（下一帧继续尝试；语音图标暂不可见）");
                    return;
                }
            }
            try
            {
                UpdatePanel(refs);
            }
            catch (Exception e)
            {
                m_updateFailures++;
                WarnRateLimited("voice-hud-update",
                    "[SuMP][ui] 语音 HUD 更新异常（面板/图标保留，下一帧继续）：" +
                    e.GetType().Name + " " + e.Message);
            }
        }

        private void RemovePanel(GameWidget gameWidget, StackPanelWidget panel)
        {
            if (panel == null) return;
            if (m_refs.TryGetValue(panel, out PanelRefs refs) && refs != null)
            {
                RemoveIcon(refs.Icon);
            }
            else if (gameWidget != null && gameWidget.GuiWidget != null)
            {
                // 结构损坏时 refs 可能没建立：按名字把可能残留的图标一起摘掉。
                int orphans = RemoveIconsByName(gameWidget.GuiWidget);
                if (orphans > 0) m_iconOrphans += orphans;
            }
            m_refs.Remove(panel);
            m_panels.Remove(panel);
            if (panel.ParentWidget != null) panel.ParentWidget.Children.Remove(panel);
        }

        public void Detach()
        {
            StackPanelWidget[] panels = m_panels.ToArray();
            for (int i = 0; i < panels.Length; i++) RemovePanel(null, panels[i]);
            m_panels.Clear();
            m_refs.Clear();
            m_toggleGate.Reset();
        }

        private PanelRefs CreatePanel(GameWidget gameWidget)
        {
            ContainerWidget gui = gameWidget.GuiWidget;
            if (gui == null)
            {
                m_iconParentMisses++;
                WarnRateLimited("voice-icon-parent",
                    "[SuMP][ui] GameWidget#" + gameWidget.GameWidgetIndex +
                    " 没有 GuiWidget → 语音图标（聊天按钮正上方）无法创建");
                return null;
            }
            // 上一次异常 / 旧版本可能留下没有面板的图标：先明确清理并计数，绝不叠两个。
            int orphans = RemoveIconsByName(gui);
            if (orphans > 0)
            {
                m_iconOrphans += orphans;
                WarnRateLimited("voice-icon-orphan",
                    "[SuMP][ui] 发现 " + orphans + " 个残留语音图标（无面板）→ 已移除，本帧重建");
            }

            // 右上角状态面板：**不再放 VOICE 开关**（开关只在右下角图标上，避免重复）。
            // 靠近房间 HUD 按钮，同时避开右下角的聊天按钮与语音图标。
            var panel = new StackPanelWidget
            {
                Name = PanelName,
                Direction = LayoutDirection.Vertical,
                HorizontalAlignment = WidgetAlignment.Far,
                VerticalAlignment = WidgetAlignment.Near,
                Margin = new Vector2(20f, 190f),
                IsVisible = false
            };
            panel.Children.Add(new LabelWidget
            {
                Name = StatusName,
                Text = "VOICE: off · press U",
                TextAnchor = TextAnchor.HorizontalCenter,
                FontScale = 0.85f,
                Color = Color.White,
                Size = new Vector2(420f, 26f)
            });
            panel.Children.Add(new LabelWidget
            {
                Name = SpeakingName,
                Text = string.Empty,
                TextAnchor = TextAnchor.HorizontalCenter,
                FontScale = 0.85f,
                Color = new Color(255, 240, 120),
                Size = new Vector2(420f, 26f),
                IsVisible = false
            });
            var buttons = new StackPanelWidget
            {
                Name = ButtonRowName,
                Direction = LayoutDirection.Horizontal,
                HorizontalAlignment = WidgetAlignment.Center
            };
            // TALK 只在 voicemode=ptt 时显示，且只有触摸点按（键盘 V 已彻底移除）。
            buttons.Children.Add(CreateButton(TalkName, "TALK", 96f, 52f));
            buttons.Children.Add(CreateButton(MuteName, "MUTE ALL", 130f, 52f));
            panel.Children.Add(buttons);
            panel.Children.Add(new StackPanelWidget
            {
                Name = PeerRowName,
                Direction = LayoutDirection.Horizontal,
                HorizontalAlignment = WidgetAlignment.Center
            });

            BevelledButtonWidget icon = CreateIcon();
            gui.Children.Add(icon);
            gameWidget.Children.Add(panel);
            PanelRefs refs = BindPanel(gameWidget, panel);
            if (refs == null)
            {
                // 不把半面板 / 孤立图标留在控件树里；调用方计 missing，下一帧重试。
                gameWidget.Children.Remove(panel);
                RemoveIcon(icon);
                return null;
            }
            if (!m_panels.Contains(panel)) m_panels.Add(panel);
            m_panelCreates++;
            m_iconCreates++;
            InfoRateLimited("voice-hud-create",
                "[SuMP][ui] 语音 HUD 已挂到 GameWidget#" + gameWidget.GameWidgetIndex +
                "（右下角语音图标 + 右上状态行 / TALK / MUTE ALL）");
            InfoRateLimited("voice-icon-create",
                "[SuMP][ui] 语音图标已挂到 GameWidget#" + gameWidget.GameWidgetIndex +
                " 右下角、聊天按钮正上方 · " + VoiceHudLayout.Describe());
            return refs;
        }

        /// <summary>
        /// 紧凑的图标按钮：BevelledButtonWidget 只提供斜面底（绿 / 灰），
        /// 麦克风图形由 <see cref="VoiceMicIconWidget"/> 自己画（不依赖字体 / 贴图）。
        /// </summary>
        private static BevelledButtonWidget CreateIcon()
        {
            VoiceIconVisual initial = VoiceIconVisual.For(false);
            var icon = new BevelledButtonWidget
            {
                Name = IconName,
                Text = string.Empty,
                Size = new Vector2(VoiceHudLayout.IconWidth, VoiceHudLayout.IconHeight),
                HorizontalAlignment = WidgetAlignment.Far,
                VerticalAlignment = WidgetAlignment.Far,
                Margin = new Vector2(VoiceHudLayout.IconMarginX, VoiceHudLayout.IconMarginY),
                Color = Color.White,
                CenterColor = ToColor(initial.CenterRgb),
                BevelColor = ToColor(initial.BevelRgb),
                IsVisible = false
            };
            icon.Children.Add(new VoiceMicIconWidget
            {
                Name = IconGlyphName,
                Size = new Vector2(32f, 32f),
                HorizontalAlignment = WidgetAlignment.Center,
                VerticalAlignment = WidgetAlignment.Center,
                GlyphColor = ToColor(initial.GlyphRgb),
                SlashColor = ToColor(initial.SlashRgb),
                ShowSlash = initial.ShowSlash,
                IsHitTestVisible = false
            });
            return icon;
        }

        private PanelRefs BindPanel(GameWidget gameWidget, StackPanelWidget panel)
        {
            var status = panel.Children.Find<LabelWidget>(StatusName, false);
            var speaking = panel.Children.Find<LabelWidget>(SpeakingName, false);
            var buttons = panel.Children.Find<StackPanelWidget>(ButtonRowName, false);
            var peers = panel.Children.Find<StackPanelWidget>(PeerRowName, false);
            if (status == null || speaking == null || buttons == null || peers == null)
            {
                WarnRateLimited("voice-hud-bind",
                    "[SuMP][ui] 语音 HUD 面板结构不完整 → 本面板不更新（将移除并重建）");
                return null;
            }
            var refs = new PanelRefs
            {
                Panel = panel,
                Status = status,
                Speaking = speaking,
                PeerRow = peers,
                Talk = buttons.Children.Find<BevelledButtonWidget>(TalkName, false),
                Mute = buttons.Children.Find<BevelledButtonWidget>(MuteName, false)
            };
            if (refs.Talk == null || refs.Mute == null)
            {
                WarnRateLimited("voice-hud-bind",
                    "[SuMP][ui] 语音 HUD 按钮缺失 → 本面板不更新（将移除并重建）");
                return null;
            }
            // 图标与聊天按钮同在 GuiWidget：同一右侧锚点，保证“聊天按钮正上方”。
            ContainerWidget gui = gameWidget.GuiWidget;
            refs.Icon = gui?.Children.Find<BevelledButtonWidget>(IconName, false);
            refs.IconGlyph = refs.Icon?.Children.Find<VoiceMicIconWidget>(IconGlyphName, false);
            if (refs.Icon == null || refs.IconGlyph == null)
            {
                WarnRateLimited("voice-icon-bind",
                    "[SuMP][ui] 语音图标 / 麦克风图形缺失 → 本面板不更新（将移除并重建）");
                return null;
            }
            m_refs[panel] = refs;
            return refs;
        }

        private static int RemoveIconsByName(ContainerWidget gui)
        {
            int removed = 0;
            while (gui != null)
            {
                BevelledButtonWidget icon =
                    gui.Children.Find<BevelledButtonWidget>(IconName, false);
                if (icon == null) break;
                gui.Children.Remove(icon);
                removed++;
            }
            return removed;
        }

        private static void RemoveIcon(BevelledButtonWidget icon)
        {
            if (icon != null && icon.ParentWidget != null) icon.ParentWidget.Children.Remove(icon);
        }

        private static BevelledButtonWidget CreateButton(string name, string text, float width,
            float height)
        {
            return new BevelledButtonWidget
            {
                Name = name,
                Text = text,
                Size = new Vector2(width, height),
                Margin = new Vector2(3f, 0f),
                Color = Color.White,
                CenterColor = new Color(45, 115, 75),
                BevelColor = new Color(120, 120, 120)
            };
        }

        private void UpdatePanel(PanelRefs refs)
        {
            // 只要模块已挂到联机会话就显示：主机遇不到（0 个客户端）也要能看到语音图标。
            // 单机 / 主菜单不显示（那时 IsAttached 也可能为 true，但项目里没有联机角色）。
            bool visible = m_module.IsAttached && (m_module.IsHosting || m_module.IsConnected);
            refs.Panel.IsVisible = visible;
            refs.Icon.IsVisible = visible;
            if (!visible)
            {
                refs.IconClickConsumed = false;
                return;
            }

            // 1) 先处理图标点击，再读模块状态 → 点击后的**同一帧**图标就显示新状态。
            if (refs.Icon.IsClicked)
            {
                if (!refs.IconClickConsumed)
                {
                    refs.IconClickConsumed = true;
                    bool before = m_module.IsVoiceOn;
                    m_module.ToggleVoice();
                    // 只把**真正翻转了状态**的点击计为 toggle（权限待答复 / 不在房间时不算）。
                    if (m_module.IsVoiceOn != before)
                    {
                        refs.IconState.NoteToggle();
                        m_iconToggles++;
                    }
                    InfoRateLimited("voice-icon-toggle",
                        "[SuMP][ui] 点击语音图标 → " + (m_module.IsVoiceOn ? "ON" : "OFF") +
                        (m_module.IsVoiceOn == before
                            ? "（未生效，原因见上方 voice 日志）"
                            : string.Empty) +
                        "（图标切换 " + refs.IconState.Toggles + " 次）");
                }
            }
            else
            {
                refs.IconClickConsumed = false;
            }

            // 2) 状态视觉：只在 ON/OFF 真正变化时计一次“图标更新”（不是每帧 +1）。
            if (refs.IconState.Apply(m_module.IsVoiceOn))
            {
                m_iconUpdates++;
                InfoRateLimited("voice-icon-state",
                    "[SuMP][ui] 语音图标状态更新 → " + refs.IconState.Current.Label +
                    "（图标在聊天按钮正上方）");
            }
            ApplyIconVisual(refs);

            refs.Status.Text = m_module.HudText();
            refs.Speaking.Text = m_module.SpeakingText((long)(Time.RealTime * 1000.0));
            refs.Speaking.IsVisible = refs.Speaking.Text.Length > 0;

            refs.Talk.IsVisible = m_module.IsPushToTalk;
            refs.Talk.Text = m_module.IsPttActive ? "TALK ON" : "TALK";
            refs.Talk.CenterColor = m_module.IsPttActive
                ? new Color(160, 120, 30)
                : new Color(80, 90, 110);
            refs.Mute.Text = m_module.IsMutedAll ? "UNMUTE ALL" : "MUTE ALL";
            refs.Mute.CenterColor = m_module.IsMutedAll
                ? new Color(130, 60, 60)
                : new Color(80, 90, 110);

            if (refs.Talk.IsClicked && m_module.IsPushToTalk) m_module.TogglePttLatch();
            if (refs.Mute.IsClicked) m_module.ToggleMuteAll();

            RebuildPeerButtonsIfNeeded(refs);
            for (int i = 0; i < refs.PeerButtons.Count; i++)
            {
                if (refs.PeerButtons[i].IsClicked) m_module.TogglePeerMute(refs.PeerIds[i]);
            }
        }

        private static void ApplyIconVisual(PanelRefs refs)
        {
            VoiceIconVisual visual = refs.IconState.Current;
            refs.Icon.CenterColor = ToColor(visual.CenterRgb);
            refs.Icon.BevelColor = ToColor(visual.BevelRgb);
            refs.IconGlyph.GlyphColor = ToColor(visual.GlyphRgb);
            refs.IconGlyph.SlashColor = ToColor(visual.SlashRgb);
            refs.IconGlyph.ShowSlash = visual.ShowSlash;
        }

        private static Color ToColor(uint rgb) =>
            new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)255);

        private void RebuildPeerButtonsIfNeeded(PanelRefs refs)
        {
            VoicePeerState[] peers = m_module.Roster.Snapshot();
            var signature = new System.Text.StringBuilder(64);
            for (int i = 0; i < peers.Length; i++)
            {
                if (peers[i].ClientId == m_module.LocalClientId) continue;
                if (m_module.IsHosting && peers[i].ClientId == 0) continue;
                signature.Append(peers[i].ClientId).Append(':').Append(peers[i].Name)
                    .Append(':').Append(m_module.IsPeerMuted(peers[i].ClientId) ? '1' : '0')
                    .Append(';');
            }
            if (signature.ToString() == refs.PeerSignature) return;
            refs.PeerSignature = signature.ToString();

            refs.PeerRow.Children.Clear();
            refs.PeerButtons.Clear();
            refs.PeerIds.Clear();
            for (int i = 0; i < peers.Length; i++)
            {
                if (peers[i].ClientId == m_module.LocalClientId) continue;
                if (m_module.IsHosting && peers[i].ClientId == 0) continue;
                int clientId = peers[i].ClientId;
                bool muted = m_module.IsPeerMuted(clientId);
                string name = peers[i].Name ?? ("Player " + clientId);
                if (name.Length > 10) name = name.Substring(0, 10);
                var button = CreateButton("SuMPVoicePeer_" + clientId,
                    muted ? name + " *" : name, 118f, 46f);
                button.CenterColor = muted ? new Color(130, 60, 60) : new Color(60, 90, 110);
                refs.PeerRow.Children.Add(button);
                refs.PeerButtons.Add(button);
                refs.PeerIds.Add(clientId);
            }
        }

        /// <summary>
        /// 帧驱动计数（5 秒状态行 / 限流日志用）：反复出现的缺失 / 异常看这里，而不是每帧写日志。
        /// 图标计数：created=创建成功；updated=ON/OFF 视觉真正变化；toggles=图标点击；
        /// keyToggles=U 键；parentMissing=GameWidget 无 GuiWidget；orphans=清理的残留图标。
        /// </summary>
        public string DescribeCounters() =>
            "voice-hud: panels=" + m_panels.Count +
            " created=" + m_panelCreates +
            " missing=" + m_panelMisses +
            " rebuilt=" + m_structuralRebuilds +
            " updateFail=" + m_updateFailures +
            " tickFail=" + m_tickFailures +
            " keyFail=" + m_keyboardFailures +
            " iconCreated=" + m_iconCreates +
            " iconUpdated=" + m_iconUpdates +
            " iconToggles=" + m_iconToggles +
            " iconKeyToggles=" + m_iconKeyToggles +
            " iconParentMissing=" + m_iconParentMisses +
            " iconOrphans=" + m_iconOrphans;

        private void WarnRateLimited(string key, string message, int intervalMs = 5000)
        {
            if (!ShouldLog(key, intervalMs)) return;
            global::Engine.Log.Warning(message + " · " + DescribeCounters());
        }

        private void InfoRateLimited(string key, string message, int intervalMs = 5000)
        {
            if (!ShouldLog(key, intervalMs)) return;
            global::Engine.Log.Information(message + " · " + DescribeCounters());
        }

        /// <summary>同一个 key 在 intervalMs 内只允许写一行；计数器本身每帧都真实累加。</summary>
        private bool ShouldLog(string key, int intervalMs)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastLogMs.TryGetValue(key, out long last) && nowMs - last < intervalMs)
                return false;
            m_lastLogMs[key] = nowMs;
            return true;
        }

        /// <summary>
        /// Windows 快捷键（引擎 <c>Engine.Input.Keyboard</c>，与游戏自己的 UI 读键同一条路径）：
        ///   · <b>U</b>：语音 ON/OFF。用 <c>IsKeyDownOnce</c> 做**边沿**判定（一次按下 = 一次切换，
        ///     按住不会连发）；聊天输入 / 文本框 / 对话框 / 软键盘打开时不下发；
        ///   · **不绑定 V**：没有任何按住说话 / PTT 键盘路径（可选 PTT 只剩 HUD 的 TALK 点按）。
        /// 读取失败时只禁用快捷键（HUD 图标仍然可用），并打一条明确警告。
        /// </summary>
        private void UpdateKeyboard()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            if (!m_keyboardReady)
            {
                if (m_keyboardFailed) return;
                InitKeyboard();
                if (!m_keyboardReady) return;
            }
            bool uDown;
            try
            {
                uDown = (bool)m_isKeyDownOnce.Invoke(null, new object[] { m_uKey });
            }
            catch (Exception e)
            {
                m_keyboardFailed = true;
                m_keyboardFailures++;
                global::Engine.Log.Warning("[SuMP][voice] 读取语音快捷键失败（U 不可用，" +
                    "HUD 的语音图标仍可用）：" + e.GetType().Name + " " + e.Message);
                return;
            }
            // 文本输入打开时这次按下不算（也不留到下一帧）。
            if (m_toggleGate.Update(uDown, uDown && IsTextInputOpen()))
            {
                bool before = m_module.IsVoiceOn;
                m_module.ToggleVoice();
                // 与点击一致：只统计真正翻转状态的 U 键切换。
                if (m_module.IsVoiceOn != before) m_iconKeyToggles++;
                InfoRateLimited("voice-icon-key",
                    "[SuMP][ui] U 键切换语音 → " + (m_module.IsVoiceOn ? "ON" : "OFF") +
                    (m_module.IsVoiceOn == before
                        ? "（未生效，原因见上方 voice 日志）"
                        : "（图标同步）"));
            }
        }

        /// <summary>
        /// U 键必须让位于文本输入：聊天输入行、引擎 TextBoxWidget（在对话框里）、
        /// 系统软键盘、以及任何已打开的对话框都不触发开关。
        /// </summary>
        private bool IsTextInputOpen()
        {
            if (m_isTextInputOpen != null && !m_textInputProbeFailed)
            {
                try
                {
                    if (m_isTextInputOpen()) return true;
                }
                catch (Exception e)
                {
                    m_textInputProbeFailed = true;
                    global::Engine.Log.Warning("[SuMP][voice] 查询聊天输入状态失败（U 键的文本输入抑制" +
                        "只剩对话框 / 软键盘两项）：" + e.GetType().Name + " " + e.Message);
                }
            }
            if (DialogsManager.Dialogs.Count > 0) return true;
            if (Keyboard.IsKeyboardVisible) return true;
            return false;
        }

        private void InitKeyboard()
        {
            try
            {
                Type keyboardType = Type.GetType("Engine.Input.Keyboard, Engine");
                MethodInfo downOnce = keyboardType?.GetMethod("IsKeyDownOnce");
                if (downOnce == null)
                {
                    m_keyboardFailed = true;
                    global::Engine.Log.Warning("[SuMP][voice] 找不到 Engine.Input.Keyboard." +
                        "IsKeyDownOnce（语音快捷键不可用，HUD 图标仍可用）");
                    return;
                }
                Type keyType = downOnce.GetParameters()[0].ParameterType;
                m_uKey = Enum.Parse(keyType, "U");
                m_isKeyDownOnce = downOnce;
                m_keyboardReady = true;
                global::Engine.Log.Information(
                    "[SuMP][voice] 快捷键：U = 语音 ON/OFF（边沿触发；文本框/对话框打开时忽略）；" +
                    "V 不绑定任何语音功能（可选 PTT 只剩 TALK 点按）");
            }
            catch (Exception e)
            {
                m_keyboardFailed = true;
                global::Engine.Log.Warning("[SuMP][voice] 初始化语音快捷键失败（HUD 图标仍可用）：" +
                    e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>
        /// 麦克风图形（纯绘制，不用字体 / 贴图）：圆头机身 + 下半圈托架 + 底座。
        /// OFF 时再画一条红色斜杠 —— 与灰色底一起构成双重视觉区分。
        /// </summary>
        private sealed class VoiceMicIconWidget : Widget
        {
            private const float CanvasSize = 32f;

            public Vector2 Size { get; set; } = new Vector2(CanvasSize, CanvasSize);

            public Color GlyphColor { get; set; } = Color.White;

            public Color SlashColor { get; set; } = new Color(224, 106, 90);

            public bool ShowSlash { get; set; }

            public override void MeasureOverride(Vector2 parentAvailableSize)
            {
                IsDrawRequired = true;
                DesiredSize = Size;
            }

            public override void Draw(DrawContext dc)
            {
                if (ActualSize.X <= 1f || ActualSize.Y <= 1f) return;
                FlatBatch2D batch = dc.PrimitivesRenderer2D.FlatBatch(1, DepthStencilState.None);
                int start = batch.TriangleVertices.Count;
                float sx = ActualSize.X / CanvasSize;
                float sy = ActualSize.Y / CanvasSize;
                Color glyph = GlyphColor * GlobalColorTransform;

                // 机身：上圆 + 矩形 + 下圆。
                batch.QueueDisc(new Vector2(16f * sx, 8.5f * sy),
                    new Vector2(5.5f * sx, 5.5f * sy), 0f, glyph);
                batch.QueueQuad(new Vector2(10.5f * sx, 8.5f * sy),
                    new Vector2(21.5f * sx, 19.5f * sy), 0f, glyph);
                batch.QueueDisc(new Vector2(16f * sx, 19.5f * sy),
                    new Vector2(5.5f * sx, 5.5f * sy), 0f, glyph);

                // 下半圈托架（0.5π → 1.5π 经过底部）。
                Vector2 center = new Vector2(16f * sx, 16.5f * sy);
                float radiusX = 9.5f * sx;
                float radiusY = 9.5f * sy;
                float width = 2.2f * sx;
                Vector2 previous = ArcPoint(center, radiusX, radiusY, 0.5f * (float)Math.PI);
                const int segments = 12;
                for (int i = 1; i <= segments; i++)
                {
                    float angle = (0.5f + i / (float)segments) * (float)Math.PI;
                    Vector2 next = ArcPoint(center, radiusX, radiusY, angle);
                    QueueThickLine(batch, previous, next, width, glyph);
                    previous = next;
                }

                // 立杆 + 底座。
                QueueThickLine(batch, new Vector2(16f * sx, 25.5f * sy),
                    new Vector2(16f * sx, 29f * sy), width, glyph);
                QueueThickLine(batch, new Vector2(10.5f * sx, 29.5f * sy),
                    new Vector2(21.5f * sx, 29.5f * sy), width, glyph);

                if (ShowSlash)
                {
                    QueueThickLine(batch, new Vector2(7f * sx, 5.5f * sy),
                        new Vector2(25f * sx, 28.5f * sy), 2.6f * sx,
                        SlashColor * GlobalColorTransform);
                }
                batch.TransformTriangles(GlobalTransform, start);
            }

            private static Vector2 ArcPoint(Vector2 center, float radiusX, float radiusY,
                float angle) => center + new Vector2(
                    (float)Math.Sin(angle) * radiusX,
                    -(float)Math.Cos(angle) * radiusY);

            private static void QueueThickLine(FlatBatch2D batch, Vector2 a, Vector2 b,
                float width, Color color)
            {
                Vector2 direction = b - a;
                float length = direction.Length();
                if (length < 0.001f) return;
                Vector2 normal = new Vector2(-direction.Y, direction.X) *
                    (width * 0.5f / length);
                batch.QueueQuad(a + normal, a - normal, b - normal, b + normal, 0f, color);
            }
        }
    }
}
