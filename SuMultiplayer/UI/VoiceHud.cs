using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// 游戏内语音 HUD（右下角图标 + 右上状态面板；静音表格在 UI/VoiceMuteTable.cs）：
    ///   · **语音状态图标**（按钮名 <c>SuMPVoiceIcon</c>）：右下角、**聊天按钮正上方**，
    ///     紧凑的麦克风图标按钮。**唯一的语音开关**：点一下切换 ON/OFF；ON = 绿底白麦 +
    ///     麦克风右侧 **5 格音量条**（按当前采集电平点亮 `round(level × 5)` 格），
    ///     OFF = 灰底灰麦 + 红色斜杠，状态一眼可辨（进房间默认 OFF，离开房间自动 OFF
    ///     并释放麦克风）。Windows 上同一个开关也绑 **U 键**（边沿触发，一次按下 =
    ///     一次切换；聊天输入 / 文本框 / 对话框 / 软键盘打开时忽略）。
    ///   · 右上角状态面板（不重复放开关）：**状态行与 SPEAKING 行都已按产品要求删除
    ///     （2026-10-21）**，只剩可选 PTT 模式下的 `TALK`（**只有触摸点按，不绑定任何键盘键**）；
    ///     `VOICE: off/on …` 状态文字只保留在 ON/OFF 切换那一行日志里，SPEAKING 状态只进
    ///     日志 / 计数（speakingChanges，不新增控件）。
    ///     **静音入口不在这里**：单个 `VO` 按钮挂在 MoreContents（UI/VoiceMuteTable.cs），
    ///     点开是静音表格（MUTE ALL + 每个远端一行）—— 产品口径 2026-10-21。
    ///
    /// 挂载方式与 RoomUiAttacher 相同：每帧在 SubsystemGameWidgets 的每个
    /// GameWidget 上找/建同一个面板与图标 —— 不替换任何子系统，也没有静态状态。
    /// 面板 / 图标只读模块的公开状态，所有动作都调用模块的公开方法。
    /// </summary>
    public sealed class VoiceHud
    {
        private const string PanelName = "SuMultiplayerVoicePanel";
        private const string ButtonRowName = "SuMPVoiceButtons";
        private const string IconName = "SuMPVoiceIcon";
        private const string IconGlyphName = "SuMPVoiceIconGlyph";
        private const string TalkName = "SuMPVoiceTalk";

        /// <summary>SPEAKING 状态只进日志 / 计数：采样间隔 1 s（不是每帧更新控件）。</summary>
        private const int SpeakingLogIntervalMs = 1000;

        private sealed class PanelRefs
        {
            public StackPanelWidget Panel;
            public BevelledButtonWidget Talk;
            public BevelledButtonWidget Icon;
            public VoiceMicIconWidget IconGlyph;
            /// <summary>图标所在的 Gui 画布：右下角位置由 <see cref="PositionIcon"/> 显式计算。</summary>
            public CanvasWidget Gui;
            public readonly VoiceIconStateTracker IconState = new VoiceIconStateTracker();
            public bool IconClickConsumed;
            /// <summary>上一次成功写进图标的显式位置签名；尺寸变化才重写 + 记一行日志。</summary>
            public string IconLayout = string.Empty;
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
        private long m_iconLayouts;
        // 5 格音量条专用计数：当前点亮的格数 + 格数真正变化的次数（不是每帧 +1）。
        private int m_micBars;
        private long m_micBarUpdates;
        // SPEAKING 只进日志 / 计数：状态文字真正变化的次数 + 上一次采样值 / 时间。
        private long m_speakingChanges;
        private string m_lastSpeakingText = string.Empty;
        private long m_lastSpeakingSampleMs;
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
            // SPEAKING 行已删除（2026-10-21）：状态只在这里按 1 s 采样进日志 / 计数。
            UpdateSpeakingLog();
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
            //
            // 2026-10-21 产品要求：**整行 `VOICE: off · press U` 状态文字与整行 SPEAKING 都已删除**
            // （连同文字控件与 Size 一起删，不留在 StackPanel 里占空行）。文字没有丢：
            // `VOICE: …` 改由 `m_module.HudText()` 写进 ON/OFF 切换那一行日志（见 UpdatePanel）；
            // SPEAKING 只按 1 s 采样写日志 / 计数（speakingChanges，见 UpdateSpeakingLog）。
            // 面板现在只剩 PTT 模式的 TALK —— 面板上唯一可点的控件（触摸点按说话必需，保留）。
            var panel = new StackPanelWidget
            {
                Name = PanelName,
                Direction = LayoutDirection.Vertical,
                HorizontalAlignment = WidgetAlignment.Far,
                VerticalAlignment = WidgetAlignment.Near,
                Margin = new Vector2(20f, 190f),
                IsVisible = false
            };
            var buttons = new StackPanelWidget
            {
                Name = ButtonRowName,
                Direction = LayoutDirection.Horizontal,
                HorizontalAlignment = WidgetAlignment.Center
            };
            // TALK 只在 voicemode=ptt 时显示，且只有触摸点按（键盘 V 已彻底移除）。
            // 静音按钮 / MUTE ALL 已按产品要求移出本面板（改由 MoreContents 的 VO 表格承担）。
            buttons.Children.Add(CreateButton(TalkName, "TALK", 96f, 52f));
            panel.Children.Add(buttons);

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
                "（右下角语音图标 + 右上 TALK（仅 ptt）；状态行 / SPEAKING 行已删除）");
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
                // 画布宽 48：左边 32 单位是麦克风（原尺寸不变），右边 16 单位是 5 格音量条。
                Size = new Vector2(48f, 32f),
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
            // SPEAKING 行已删除：面板里只允许唯一的按钮行（TALK）；缺按钮行，
            // 或还留着旧版本的状态行 / SPEAKING 行（子控件数 != 1）→ 结构不完整，重建。
            var buttons = panel.Children.Find<StackPanelWidget>(ButtonRowName, false);
            if (buttons == null || panel.Children.Count != 1)
            {
                WarnRateLimited("voice-hud-bind",
                    "[SuMP][ui] 语音 HUD 面板结构不完整（含旧版本残留行 / 缺按钮行）" +
                    " → 本面板不更新（将移除并重建）");
                return null;
            }
            var refs = new PanelRefs
            {
                Panel = panel,
                Talk = buttons.Children.Find<BevelledButtonWidget>(TalkName, false)
            };
            if (refs.Talk == null)
            {
                WarnRateLimited("voice-hud-bind",
                    "[SuMP][ui] 语音 HUD 按钮缺失 → 本面板不更新（将移除并重建）");
                return null;
            }
            // 图标与聊天按钮同在 GuiWidget：同一右侧锚点，保证“聊天按钮正上方”。
            ContainerWidget gui = gameWidget.GuiWidget;
            refs.Gui = gui as CanvasWidget;
            refs.Icon = gui?.Children.Find<BevelledButtonWidget>(IconName, false);
            refs.IconGlyph = refs.Icon?.Children.Find<VoiceMicIconWidget>(IconGlyphName, false);
            if (refs.Gui == null || refs.Icon == null || refs.IconGlyph == null)
            {
                WarnRateLimited("voice-icon-bind",
                    "[SuMP][ui] 语音图标 / 麦克风图形缺失 → 本面板不更新（将移除并重建）");
                return null;
            }
            // 位置改由 PositionIcon 显式写入（CanvasWidget.SetPosition）：CreateIcon 里的
            // Margin=300 只是设计值，若真交给引擎排布会被 ContainerWidget.ArrangeChildWidgetInCell
            // 按 `cell - 2*Margin` 夹高度（Source: Survivalcraft/Game/ContainerWidget.cs:66-121）：
            // 平板设计高 600 - 2*300 = 0 → 图标实际尺寸 0×0、画不出来（2026-10-21 实机根因）。
            // Margin 在第一次拿到有效 Gui 尺寸时由 PositionIcon 清掉，避免布局生效前跑到聊天按钮上。
            refs.IconLayout = string.Empty;
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
            //    状态行已按产品要求从面板删除（2026-10-21）：状态文字改由 HudText() 进这一行日志，
            //    仍然“显式可定位”，但屏幕上不再有不属于任何入口的文字。
            if (refs.IconState.Apply(m_module.IsVoiceOn))
            {
                m_iconUpdates++;
                InfoRateLimited("voice-icon-state",
                    "[SuMP][ui] 语音图标状态更新 → " + refs.IconState.Current.Label +
                    "（图标在聊天按钮正上方；状态行已删除，状态文字只进日志：" +
                    m_module.HudText() + "）");
            }
            ApplyIconVisual(refs);
            PositionIcon(refs);

            // SPEAKING 行已按产品要求删除（2026-10-21）：这里不再每帧写控件；
            // 说话状态只由 TickCore 里的 UpdateSpeakingLog 按 1 s 采样进日志 / 计数。

            refs.Talk.IsVisible = m_module.IsPushToTalk;
            refs.Talk.Text = m_module.IsPttActive ? "TALK ON" : "TALK";
            refs.Talk.CenterColor = m_module.IsPttActive
                ? new Color(160, 120, 30)
                : new Color(80, 90, 110);

            if (refs.Talk.IsClicked && m_module.IsPushToTalk) m_module.TogglePttLatch();
        }

        /// <summary>
        /// 说话状态（原右上角 SPEAKING 行）**只进日志 / 计数**，不再建任何控件（产品口径 2026-10-21）：
        ///   · 每 <see cref="SpeakingLogIntervalMs"/> 采样一次（不是每帧）；
        ///   · 只有状态文字真正变化才 +1（speakingChanges），日志按 10 s 限流；
        ///   · 状态文字仍来自模块 <see cref="VoiceChatModule.SpeakingText"/>（含本机 "You" / 远端名字）。
        /// </summary>
        private void UpdateSpeakingLog()
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (nowMs - m_lastSpeakingSampleMs < SpeakingLogIntervalMs) return;
            m_lastSpeakingSampleMs = nowMs;
            string text = m_module.SpeakingText(nowMs) ?? string.Empty;
            if (string.Equals(text, m_lastSpeakingText, StringComparison.Ordinal)) return;
            m_lastSpeakingText = text;
            m_speakingChanges++;
            InfoRateLimited("voice-speaking",
                "[SuMP][ui] 说话状态 → " + (text.Length == 0 ? "无" : text) +
                "（仅日志 / 计数；屏幕上的 SPEAKING 行已按产品要求删除）", 10000);
        }

        /// <summary>
        /// 图标的每帧视觉：底色 / 斜面 / 麦克风颜色 + **5 格音量条**。
        ///
        /// 音量条（2026-10-21）：数据只读 <see cref="VoiceChatModule.MicLevelBars"/>
        /// （0..5 = round(平滑电平 × 5)，采集关 / 语音关时模块直接给 0）。
        /// 每帧只写两个值（<c>ShowMeter</c> + <c>Bars</c>），格子在 <see cref="VoiceMicIconWidget.Draw"/>
        /// 里用同一个 FlatBatch 现画 —— **不新建 / 不重排任何控件**，成本最多 5 个 quad。
        /// OFF 状态整条不画：保持现有“灰底灰麦 + 红斜杠”样式。
        /// </summary>
        private void ApplyIconVisual(PanelRefs refs)
        {
            VoiceIconVisual visual = refs.IconState.Current;
            refs.Icon.CenterColor = ToColor(visual.CenterRgb);
            refs.Icon.BevelColor = ToColor(visual.BevelRgb);
            refs.IconGlyph.GlyphColor = ToColor(visual.GlyphRgb);
            refs.IconGlyph.SlashColor = ToColor(visual.SlashRgb);
            refs.IconGlyph.ShowSlash = visual.ShowSlash;

            refs.IconGlyph.ShowMeter = visual.On;
            int bars = visual.On ? m_module.MicLevelBars : 0;
            if (bars == refs.IconGlyph.Bars) return;
            refs.IconGlyph.Bars = bars;
            m_micBars = bars;
            m_micBarUpdates++;
            InfoRateLimited("voice-mic-meter",
                "[SuMP][ui] 麦克风电平 → " + bars + "/" + VoiceMicMeter.BarCount + " 格" +
                "（level=" + m_module.MicLevelNormalized.ToString("0.00",
                    CultureInfo.InvariantCulture) +
                "，图标内 5 格：亮格=麦克风同色（ON=白）/ 熄灭格=黑α" +
                VoiceMicIconWidget.UnlitAlpha + "，不新建控件）", 10000);
        }

        private static Color ToColor(uint rgb) =>
            new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)255);

        /// <summary>
        /// 右下角图标的**显式位置**：右边缘对齐聊天按钮（x = Gui宽 - 图标宽），
        /// 图标底边距屏幕底 <see cref="VoiceHudLayout.IconMarginY"/>（y = Gui高 - 边距 - 图标高）。
        ///
        /// 为什么不用 Margin 排布：<c>ContainerWidget.ArrangeChildWidgetInCell</c> 把子控件可用
        /// 高度算成 <c>cell - 2 * Margin</c>（Source: Survivalcraft/Game/ContainerWidget.cs:66-121）。
        /// 平板 2000×1200 / UIScale 0.85 → Gui 设计高 600，600 - 2×300 = 0 → 图标被排成 0 高，
        /// 于是“客户端右下角没有麦克风图标、主机（设计高 671.5）却有”（2026-10-21 实机根因）。
        /// 显式位置是引擎自己的定位通道（Source: Survivalcraft/Game/CanvasWidget.cs:17-40；
        /// SliderWidget / ConsoleMod 同样用法），绕开夹取，并在窗口 / 分屏尺寸变化后每帧跟随。
        /// </summary>
        private void PositionIcon(PanelRefs refs)
        {
            if (refs.Gui == null || refs.Icon == null) return;
            Vector2 guiSize = refs.Gui.ActualSize;
            if (guiSize.X <= 0f || guiSize.Y <= 0f) return;
            // 显式位置生效后清掉 300 的 Margin：否则 measure 里还会按 2*Margin 撑大 desired，
            // 且 ArrangeChildWidgetInCell 的 `cell - 2*Margin` 正是把图标压成 0 高的那条路径。
            refs.Icon.Margin = Vector2.Zero;
            var position = new Vector2(
                guiSize.X - VoiceHudLayout.IconWidth,
                guiSize.Y - VoiceHudLayout.IconMarginY - VoiceHudLayout.IconHeight);
            CanvasWidget.SetPosition(refs.Icon, position);
            string signature = guiSize.X.ToString("0.#", CultureInfo.InvariantCulture) + "x" +
                guiSize.Y.ToString("0.#", CultureInfo.InvariantCulture);
            if (signature == refs.IconLayout) return;
            refs.IconLayout = signature;
            m_iconLayouts++;
            InfoRateLimited("voice-icon-layout",
                "[SuMP][ui] 语音图标显式位置：Gui=" + signature + " → icon@(" +
                position.X.ToString("0.#", CultureInfo.InvariantCulture) + "," +
                position.Y.ToString("0.#", CultureInfo.InvariantCulture) + ")，" +
                "右下角 / 聊天按钮正上方（Margin=" + VoiceHudLayout.IconMarginY +
                " 仅作设计值：引擎按 cell-2*Margin 夹高度，低高度视图会把图标压成 0）", 10000);
        }

        /// <summary>
        /// 帧驱动计数（5 秒状态行 / 限流日志用）：反复出现的缺失 / 异常看这里，而不是每帧写日志。
        /// 图标计数：created=创建成功；updated=ON/OFF 视觉真正变化；toggles=图标点击；
        /// keyToggles=U 键；parentMissing=GameWidget 无 GuiWidget；orphans=清理的残留图标；
        /// micBars/micBarUpdates=图标内 5 格音量条的当前格数与变化次数；
        /// speakingChanges=SPEAKING 状态文字真正变化的次数（只进日志，屏幕上没有该行）；
        /// 末尾附模块电平计（帧数 / 拒绝数 / 停滞衰减 / level / bars）——状态行删除后，
        /// 状态文字只在 ON/OFF 切换日志里出现，这里给出电平侧的长期计数。
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
            " iconOrphans=" + m_iconOrphans +
            " iconLayouts=" + m_iconLayouts +
            " micBars=" + m_micBars + "/" + VoiceMicMeter.BarCount +
            " micBarUpdates=" + m_micBarUpdates +
            " speakingChanges=" + m_speakingChanges +
            " " + m_module.MicMeter.Describe();

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
        /// 麦克风图形（纯绘制，不用字体 / 贴图）：圆头机身 + 下半圈托架 + 底座；
        /// ON 时右侧再画 **5 格音量条**（点亮格数 = 模块的 <see cref="VoiceChatModule.MicLevelBars"/>）。
        /// OFF 时整条不画，只画一条红色斜杠 —— 与灰色底一起构成双重视觉区分。
        ///
        /// 音量条不是新控件：它就是本控件 <see cref="Draw"/> 里的 5 个 quad（同一个 FlatBatch），
        /// 每帧只由 <see cref="Bars"/> 决定点亮几格，没有任何控件创建 / 布局开销。
        /// 画布从 32 加宽到 48：左 32 单位保持麦克风原尺寸（逐像素不变），右 16 单位放音量条。
        /// </summary>
        private sealed class VoiceMicIconWidget : Widget
        {
            /// <summary>画布宽 48：左 32 = 麦克风（原尺寸），右 16 = 5 格音量条。</summary>
            private const float CanvasWidth = 48f;

            /// <summary>画布高 32：与旧版一致（麦克风不变形、不缩放）。</summary>
            private const float CanvasHeight = 32f;

            /// <summary>第 1 格左边缘（麦克风托架最右 x=25.5，留 2.5 空隙）。</summary>
            private const float BarLeft = 28f;

            /// <summary>每格宽 2.6、间距 1.3 → 5 格总宽 18.2（28 → 46.2，画布 48 内）。</summary>
            private const float BarWidth = 2.6f;
            private const float BarGap = 1.3f;

            /// <summary>音量条基线 = 麦克风底座高度（y=29.5）；从下往上叠 5 格。</summary>
            private const float BarBaseline = 29.5f;
            private const float BarMinHeight = 7f;
            private const float BarHeightStep = 4.5f;

            /// <summary>
            /// 未点亮格子的黑色不透明度（0..255）：176 ≈ 69% 黑，把格子压成绿底上的暗槽。
            /// 不能再用浅色覆盖：旧的半透明白 α70 在绿底 0x2E7D4F / 浅绿斜面 0x9AD6AE 上
            /// 合成后反而比底色更亮（≈0x67A17F / 0xB6E1C4），实机看起来 5 格永远全亮
            /// （2026-10-21 用户反馈）。点亮格用麦克风同色（ON=白）：与暗槽在绿底中心
            /// 对比约 16:1、浅绿斜面约 10:1。
            /// </summary>
            internal const byte UnlitAlpha = 176;

            public Vector2 Size { get; set; } = new Vector2(CanvasWidth, CanvasHeight);

            public Color GlyphColor { get; set; } = Color.White;

            public Color SlashColor { get; set; } = new Color(224, 106, 90);

            public bool ShowSlash { get; set; }

            /// <summary>是否画音量条（ON 才画；OFF 保持现有灰底灰麦 + 红斜杠样式）。</summary>
            public bool ShowMeter { get; set; }

            /// <summary>点亮格数 0..5（= round(平滑电平 × 5)）；由 VoiceHud 每帧写入。</summary>
            public int Bars { get; set; }

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
                float sx = ActualSize.X / CanvasWidth;
                float sy = ActualSize.Y / CanvasHeight;
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

                // 5 格音量条：未点亮 = 黑色 α176（绿底 / 浅绿斜面上都压成暗槽），
                // 点亮 = 麦克风同色（ON=白）。旧实现用半透明白 α70，合成后比底色更亮，
                // 视觉上 5 格永远全亮；数据侧 bars 一直是对的，问题只在绘制对比。
                // 每帧最多 5 个 quad，全部写进上面同一个 batch；不新建任何控件、不产生堆分配。
                if (ShowMeter)
                {
                    Color unlit = new Color(0, 0, 0, (int)UnlitAlpha) * GlobalColorTransform;
                    int lit = Bars;
                    if (lit < 0) lit = 0;
                    else if (lit > VoiceMicMeter.BarCount) lit = VoiceMicMeter.BarCount;
                    for (int i = 0; i < VoiceMicMeter.BarCount; i++)
                    {
                        float left = BarLeft + i * (BarWidth + BarGap);
                        float height = BarMinHeight + i * BarHeightStep;
                        batch.QueueQuad(
                            new Vector2(left * sx, (BarBaseline - height) * sy),
                            new Vector2((left + BarWidth) * sx, BarBaseline * sy),
                            0f, i < lit ? glyph : unlit);
                    }
                }

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
