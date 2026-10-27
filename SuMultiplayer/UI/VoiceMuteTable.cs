using System;
using System.Collections.Generic;
using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Voice;

namespace SuMultiplayer.UI
{
    /// <summary>
    /// 游戏内**语音静音入口**（产品口径 2026-10-21）：
    ///   · MoreContents 里**只放一个** <c>VO</c> 按钮（名字 <c>SuMultiplayerVoiceButton</c>），
    ///     与 CR 按钮同排、同风格（<see cref="BevelledButtonWidget"/>）；
    ///   · 点开一张表格对话框：标题 <c>VOICE</c>；第一行 <c>MUTE ALL</c> / <c>UNMUTE ALL</c>；
    ///     表格体每个远端一行（名称列 + MUTE/UNMUTE 按钮列，已静音 = 红底 + 名字后缀 " *"）；
    ///     行多时用 <see cref="ScrollPanelWidget"/> 滚动；底部 <c>CLOSE</c>。
    ///   · 点击纪律：一次按下只动作一次（<see cref="VoiceClickEdgeGate"/>，与
    ///     <c>RoomUiAttacher.TryConsumeClick</c> 同一套边沿规则）。
    ///   · 生命周期与 CR 按钮一致：游戏屏 / 世界里挂上，离开世界或卸载时摘掉并关闭表格。
    ///   · 右下角麦克风图标（<c>SuMPVoiceIcon</c>）仍在 <see cref="VoiceHud"/>，本文件不碰它。
    ///
    /// Source: Survivalcraft/Game/Dialog.cs、MessageDialog.cs（Dialog + 黑色遮罩由
    ///         DialogsManager.ShowDialog 负责）；Source: Survivalcraft/Game/ScrollPanelWidget.cs；
    /// Source: UI/RoomUiAttacher.cs（MoreContents 按钮挂载 / 边沿消费 / 计数 + 限流日志）。
    /// </summary>
    public sealed class VoiceMuteTableAttacher
    {
        private const string ButtonName = "SuMultiplayerVoiceButton";
        private const string ButtonText = "VO";
        private const string DialogName = "SuMultiplayerVoiceTableDialog";

        private readonly VoiceChatModule m_module;
        private readonly VoiceMuteTableCounters m_counters = new VoiceMuteTableCounters();
        private readonly VoiceClickEdgeGate m_clicks = new VoiceClickEdgeGate();
        private readonly List<BevelledButtonWidget> m_buttons = new List<BevelledButtonWidget>();
        private readonly Dictionary<string, long> m_lastLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private VoiceMuteTableDialog m_dialog;

        public VoiceMuteTableAttacher(VoiceChatModule module)
        {
            m_module = module ?? throw new ArgumentNullException(nameof(module));
        }

        public void Tick()
        {
            try
            {
                TickCore();
            }
            catch (Exception e)
            {
                m_counters.TickFailures++;
                WarnRateLimited("vo-tick",
                    "[SuMP][ui] VO 按钮 / 静音表格帧驱动异常（已隔离，下一帧继续）：" +
                    e.GetType().Name + " " + e.Message);
            }
        }

        private void TickCore()
        {
            PruneDialog();

            Project project = GameManager.Project;
            if (project == null)
            {
                // 离开世界 = 没有可静音的会话：按钮摘掉、表格关闭（与 CR 的离开语义一致）。
                RemoveButtons();
                CloseDialog("world-unload");
                m_clicks.Clear();
                return;
            }
            SubsystemGameWidgets widgets = project.FindSubsystem<SubsystemGameWidgets>(false);
            if (widgets == null) return;

            ReadOnlyList<GameWidget> gameWidgets = widgets.GameWidgets;
            m_buttons.Clear();
            for (int i = 0; i < gameWidgets.Count; i++)
            {
                GameWidget gameWidget = gameWidgets[i];
                if (gameWidget == null) continue;
                // required:false：HUD 结构变化不能让帧驱动每帧抛异常（与 CR 按钮同一口径）。
                StackPanelWidget moreContents =
                    gameWidget.Children.Find<StackPanelWidget>("MoreContents", false);
                if (moreContents == null)
                {
                    m_counters.MoreContentsMisses++;
                    WarnRateLimited("vo-morecontents",
                        "[SuMP][ui] GameWidget 里没有 MoreContents（引擎 HUD 结构变化）→ " +
                        "VO 按钮本帧不挂载（下一帧继续尝试）");
                    continue;
                }
                BevelledButtonWidget button =
                    moreContents.Children.Find<BevelledButtonWidget>(ButtonName, false);
                if (button == null)
                {
                    button = CreateButton(moreContents, gameWidget.GameWidgetIndex);
                    if (button == null) continue;
                }
                if (button.Text != ButtonText) button.Text = ButtonText;
                m_buttons.Add(button);
                if (m_clicks.TryConsume(button, button.IsClicked)) OpenTable(gameWidget);
            }
            // 只丢掉"控件已不在本帧活动列表、且 IsClicked 已回 false"的记录；
            // 冻结在 true 的记录必须留着，否则重新出现时会把同一次旧点击再触发一遍。
            m_clicks.Prune(delegate (object key)
            {
                var button = key as BevelledButtonWidget;
                return button != null && (m_buttons.Contains(button) || button.IsClicked);
            });
        }

        private BevelledButtonWidget CreateButton(StackPanelWidget moreContents, int gameWidgetIndex)
        {
            try
            {
                // 与 CR 按钮同排、同风格（Source: UI/RoomUiAttacher.cs:CreateButton）。
                var button = new BevelledButtonWidget
                {
                    Name = ButtonName,
                    Text = ButtonText,
                    Size = new Vector2(76f, 64f),
                    Margin = new Vector2(3f, 0f),
                    Color = Color.White,
                    CenterColor = new Color(45, 115, 75),
                    BevelColor = new Color(120, 120, 120)
                };
                moreContents.Children.Add(button);
                m_counters.ButtonCreates++;
                InfoRateLimited("vo-button-create",
                    "[SuMP][ui] VO mute button attached to GameWidget#" + gameWidgetIndex +
                    " MoreContents (same row as CR)");
                return button;
            }
            catch (Exception e)
            {
                m_counters.ButtonCreateFailures++;
                WarnRateLimited("vo-button-create",
                    "[SuMP][ui] VO 按钮创建失败（GameWidget#" + gameWidgetIndex + "）：" +
                    e.GetType().Name + " " + e.Message + " → 本帧不挂载，下一帧重试");
                return null;
            }
        }

        /// <summary>
        /// 打开静音表格。父子关系按引擎对话框的既有落点：<c>ComponentPlayer.GuiWidget</c>
        /// （Source: Survivalcraft/Game/DialogsManager.cs:ShowDialog；MessageDialog 同源）。
        /// 拿不到本地玩家 / GuiWidget 时**明确失败**（计数 + 限流日志），不换别的父控件硬开。
        /// </summary>
        private void OpenTable(GameWidget gameWidget)
        {
            if (m_dialog != null && !m_dialog.IsDismissed)
            {
                m_counters.AlreadyOpen++;
                WarnRateLimited("vo-table-already-open",
                    "[SuMP][ui] VOICE 静音表格已经打开，忽略本次 VO 点击（不叠第二张表）");
                return;
            }
            ComponentPlayer player = gameWidget.PlayerData?.ComponentPlayer;
            ContainerWidget guiWidget = player?.GuiWidget;
            if (guiWidget == null)
            {
                m_counters.TableFailures++;
                WarnRateLimited("vo-table-open",
                    "[SuMP][ui] GameWidget#" + gameWidget.GameWidgetIndex +
                    " 没有本地 ComponentPlayer.GuiWidget → VOICE 表格无法打开（明确失败，不换父控件）");
                return;
            }
            VoiceMuteTableDialog dialog;
            try
            {
                dialog = new VoiceMuteTableDialog(m_module, m_counters, DialogName);
            }
            catch (Exception e)
            {
                m_counters.TableFailures++;
                WarnRateLimited("vo-table-build",
                    "[SuMP][ui] VOICE 表格构造失败：" + e.GetType().Name + " " + e.Message +
                    " → 不打开（不计为已打开）");
                return;
            }
            try
            {
                DialogsManager.ShowDialog(guiWidget, dialog);
            }
            catch (Exception e)
            {
                m_counters.TableFailures++;
                WarnRateLimited("vo-table-show",
                    "[SuMP][ui] VOICE 表格挂到对话框管理器失败：" + e.GetType().Name + " " +
                    e.Message + " → 不打开（不计为已打开）");
                return;
            }
            m_dialog = dialog;
            m_counters.TableOpens++;
            InfoRateLimited("vo-table-open",
                "[SuMP][ui] VOICE 静音表格已打开（rows=" + dialog.VisibleRowCount +
                "，GameWidget#" + gameWidget.GameWidgetIndex + "）· " + DescribeCounters());
        }

        /// <summary>对话框被用户 / 引擎关掉后清引用（ParentWidget 在淡出动画结束后才为 null）。</summary>
        private void PruneDialog()
        {
            if (m_dialog == null) return;
            if (m_dialog.ParentWidget == null && (m_dialog.IsDismissed ||
                !DialogsManager.Dialogs.Contains(m_dialog)))
            {
                m_dialog = null;
            }
        }

        private void CloseDialog(string reason)
        {
            VoiceMuteTableDialog dialog = m_dialog;
            if (dialog == null) return;
            m_dialog = null;
            if (!dialog.IsDismissed) dialog.DismissFromOwner(reason);
        }

        private void RemoveButtons()
        {
            for (int i = 0; i < m_buttons.Count; i++)
            {
                BevelledButtonWidget button = m_buttons[i];
                if (button?.ParentWidget == null) continue;
                button.ParentWidget.Children.Remove(button);
                m_counters.ButtonRemovals++;
            }
            if (m_buttons.Count > 0)
            {
                InfoRateLimited("vo-button-remove",
                    "[SuMP][ui] 已离开世界 → 摘掉 " + m_buttons.Count + " 个 VO 按钮");
            }
            m_buttons.Clear();
        }

        /// <summary>模块卸载时由组合根调用：按钮、表格、边沿记录一起清掉。</summary>
        public void Detach()
        {
            CloseDialog("detach");
            RemoveButtons();
            m_clicks.Clear();
        }

        public string DescribeCounters() => m_counters.Describe();

        private void WarnRateLimited(string key, string message, int intervalMs = 10000)
        {
            if (!ShouldLog(key, intervalMs)) return;
            global::Engine.Log.Warning(message + " · " + DescribeCounters());
        }

        private void InfoRateLimited(string key, string message, int intervalMs = 10000)
        {
            if (!ShouldLog(key, intervalMs)) return;
            SuMultiplayer.Engine.ModLog.Info(message + " · " + DescribeCounters());
        }

        private bool ShouldLog(string key, int intervalMs)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastLogMs.TryGetValue(key, out long last) && nowMs - last < intervalMs)
                return false;
            m_lastLogMs[key] = nowMs;
            return true;
        }
    }

    /// <summary>
    /// <c>VOICE</c> 静音表格对话框（引擎 <see cref="Dialog"/> + 黑色遮罩由 DialogsManager 加）：
    ///   · 标题 / MUTE ALL / 表格（名称列 + MUTE·UNMUTE 按钮列）/ CLOSE 全部在这里建；
    ///   · 打开期间每帧比较行签名（<see cref="VoiceMuteTableRules.Signature"/>）：
    ///     peers / 名字 / 静音状态变化才重建行，不每帧重建控件；
    ///   · 所有动作都调用 <see cref="VoiceChatModule"/> 的公开方法，表格不缓存静音状态。
    ///
    /// Source: Survivalcraft/Game/MessageDialog.cs（Dialog 标题 / 按钮 / Cancel 的既有写法）；
    /// Source: Survivalcraft/Game/ScrollPanelWidget.cs（Direction=Vertical 滚动）；
    /// Source: Survivalcraft/Game/CanvasWidget.cs:30（行列排布用固定 Size + 对齐）。
    /// </summary>
    internal sealed class VoiceMuteTableDialog : Dialog
    {
        private const float DialogWidth = 720f;
        private const float DialogHeight = 480f;
        private const float RowWidth = 650f;
        private const float RowHeight = 54f;
        private const float RowButtonWidth = 160f;
        private const float RowButtonHeight = 44f;

        private readonly VoiceChatModule m_module;
        private readonly VoiceMuteTableCounters m_counters;
        private readonly VoiceClickEdgeGate m_actionClicks = new VoiceClickEdgeGate();
        private readonly VoiceClickEdgeGate m_rowClicks = new VoiceClickEdgeGate();
        private readonly StackPanelWidget m_rows;
        private readonly BevelledButtonWidget m_muteAll;
        private readonly BevelledButtonWidget m_close;
        private readonly List<BevelledButtonWidget> m_rowButtons =
            new List<BevelledButtonWidget>();
        private readonly List<int> m_rowIds = new List<int>();
        private readonly Dictionary<string, long> m_lastLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private string m_rowsSignature = string.Empty;
        private bool m_dismissed;

        public VoiceMuteTableDialog(VoiceChatModule module, VoiceMuteTableCounters counters,
            string name)
        {
            m_module = module ?? throw new ArgumentNullException(nameof(module));
            m_counters = counters ?? throw new ArgumentNullException(nameof(counters));
            Name = name;
            // 与 MessageDialog 的 XML 根一样：对话框自己定尺寸，居中挂在 GuiWidget 上。
            Size = new Vector2(DialogWidth, DialogHeight);
            ClampToBounds = true;
            HorizontalAlignment = WidgetAlignment.Center;
            VerticalAlignment = WidgetAlignment.Center;

            Children.Add(new BevelledRectangleWidget
            {
                Name = "SuMPVoiceTableBackground",
                HorizontalAlignment = WidgetAlignment.Stretch,
                VerticalAlignment = WidgetAlignment.Stretch,
                CenterColor = new Color(24, 24, 24),
                BevelColor = new Color(128, 128, 128),
                AmbientLight = 1f,
                DirectionalLight = 0f,
                BevelSize = 3f,
                RoundingRadius = 10f,
                RoundingCount = 4
            });

            var layout = new StackPanelWidget
            {
                Name = "SuMPVoiceTableLayout",
                Direction = LayoutDirection.Vertical,
                HorizontalAlignment = WidgetAlignment.Stretch,
                VerticalAlignment = WidgetAlignment.Stretch,
                Margin = new Vector2(22f, 16f)
            };
            Children.Add(layout);

            layout.Children.Add(new LabelWidget
            {
                Name = "SuMPVoiceTableTitle",
                Text = VoiceMuteTableRules.DialogTitle,
                TextAnchor = TextAnchor.HorizontalCenter | TextAnchor.VerticalCenter,
                FontScale = 1.1f,
                Color = Color.White,
                Size = new Vector2(RowWidth, 40f)
            });

            m_muteAll = CreateButton("SuMPVoiceTableMuteAll", VoiceMuteTableRules.MuteAllText,
                220f, 52f);
            m_muteAll.HorizontalAlignment = WidgetAlignment.Center;
            m_muteAll.Margin = new Vector2(0f, 4f);
            layout.Children.Add(m_muteAll);

            // ScrollPanelWidget 在 StackPanel 里按"填充剩余高度"排布（DesiredSize=∞，
            // Source: Survivalcraft/Game/StackPanelWidget.cs:50-88）；行多时在这里滚动。
            var scroll = new ScrollPanelWidget
            {
                Name = "SuMPVoiceTableScroll",
                Direction = LayoutDirection.Vertical,
                ClampToBounds = true,
                HorizontalAlignment = WidgetAlignment.Stretch,
                Margin = new Vector2(0f, 8f)
            };
            scroll.ScrollPosition = 0f;
            scroll.ScrollSpeed = 0f;
            m_rows = new StackPanelWidget
            {
                Name = "SuMPVoiceTableRows",
                Direction = LayoutDirection.Vertical,
                HorizontalAlignment = WidgetAlignment.Center
            };
            scroll.Children.Add(m_rows);
            layout.Children.Add(scroll);

            m_close = CreateButton("SuMPVoiceTableClose", VoiceMuteTableRules.CloseText,
                180f, 54f);
            m_close.HorizontalAlignment = WidgetAlignment.Center;
            m_close.Margin = new Vector2(0f, 4f);
            layout.Children.Add(m_close);

            UpdateMuteAllVisual();
            RefreshRows(force: true);
        }

        public bool IsDismissed => m_dismissed;

        public int VisibleRowCount => m_rowIds.Count;

        public override void Update()
        {
            if (m_dismissed) return;
            try
            {
                UpdateCore();
            }
            catch (Exception e)
            {
                // 对话框在控件树里，异常必须自己隔离，否则会冒泡到 Frame/Update 变整屏异常。
                m_counters.UpdateFailures++;
                WarnRateLimited("vo-table-update",
                    "[SuMP][ui] VOICE 表格刷新异常（表格保留，下一帧继续）：" +
                    e.GetType().Name + " " + e.Message);
            }
        }

        private void UpdateCore()
        {
            if (Input.Cancel || m_actionClicks.TryConsume(m_close, m_close.IsClicked))
            {
                Dismiss();
                return;
            }
            if (m_actionClicks.TryConsume(m_muteAll, m_muteAll.IsClicked))
            {
                m_module.ToggleMuteAll();
                m_counters.MuteAllClicks++;
                InfoRateLimited("vo-table-muteall",
                    "[SuMP][ui] VOICE 表格：MUTE ALL → IsMutedAll=" +
                    (m_module.IsMutedAll ? "true（全部远端已静音）" : "false（全部解除）"));
                UpdateMuteAllVisual();
            }
            for (int i = 0; i < m_rowButtons.Count; i++)
            {
                BevelledButtonWidget button = m_rowButtons[i];
                if (!m_rowClicks.TryConsume(button, button.IsClicked)) continue;
                m_module.TogglePeerMute(m_rowIds[i]);
                m_counters.RowClicks++;
                InfoRateLimited("vo-table-row",
                    "[SuMP][ui] VOICE 表格：切换 clientId=" + m_rowIds[i] +
                    " → " + (m_module.IsPeerMuted(m_rowIds[i]) ? "已静音" : "已解除"));
            }
            RefreshRows(force: false);
            UpdateMuteAllVisual();
        }

        private void RefreshRows(bool force)
        {
            VoicePeerState[] peers = m_module.Roster.Snapshot();
            VoiceMuteRow[] rows = VoiceMuteTableRules.BuildRows(peers, m_module.LocalClientId,
                m_module.IsHosting, m_module.IsPeerMuted);
            string signature = VoiceMuteTableRules.Signature(rows);
            if (!force && signature == m_rowsSignature) return;
            m_rowsSignature = signature;
            m_rows.Children.Clear();
            m_rowButtons.Clear();
            m_rowIds.Clear();
            m_rowClicks.Clear();
            if (rows.Length == 0)
            {
                // 没有远端不是错误：主机 0 客户端时表格为空，明确写一行而不是空白。
                m_rows.Children.Add(new LabelWidget
                {
                    Name = "SuMPVoiceTableEmpty",
                    Text = "No remote players",
                    FontScale = 0.8f,
                    Color = new Color(150, 150, 150),
                    TextAnchor = TextAnchor.HorizontalCenter | TextAnchor.VerticalCenter,
                    Size = new Vector2(RowWidth, RowHeight)
                });
            }
            for (int i = 0; i < rows.Length; i++)
            {
                VoiceMuteRow row = rows[i];
                var canvas = new CanvasWidget
                {
                    Name = "SuMPVoiceRow_" + row.ClientId,
                    Size = new Vector2(RowWidth, RowHeight)
                };
                // 行底色：已静音 = 红底（与按钮红 / 名字后缀 " *" 一起给出双重状态），
                // 未静音 = 正常深灰蓝底。背景不参与命中（按钮才是可点目标）。
                canvas.Children.Add(new RectangleWidget
                {
                    Name = "SuMPVoiceRowBackground_" + row.ClientId,
                    Size = new Vector2(RowWidth, RowHeight),
                    FillColor = row.Muted
                        ? new Color(96, 34, 34, 150)
                        : new Color(44, 54, 64, 120),
                    OutlineColor = Color.Transparent,
                    IsHitTestVisible = false
                });
                canvas.Children.Add(new LabelWidget
                {
                    Name = "SuMPVoiceRowName_" + row.ClientId,
                    Text = row.Name,
                    FontScale = 0.8f,
                    Color = row.Muted ? new Color(255, 190, 190) : Color.White,
                    TextAnchor = TextAnchor.VerticalCenter,
                    HorizontalAlignment = WidgetAlignment.Near,
                    VerticalAlignment = WidgetAlignment.Center,
                    Margin = new Vector2(10f, 0f),
                    Size = new Vector2(RowWidth - RowButtonWidth - 30f, RowHeight)
                });
                BevelledButtonWidget button = CreateButton(
                    "SuMPVoiceRowButton_" + row.ClientId,
                    VoiceMuteTableRules.ActionText(row.Muted), RowButtonWidth, RowButtonHeight);
                button.HorizontalAlignment = WidgetAlignment.Far;
                button.VerticalAlignment = WidgetAlignment.Center;
                button.Margin = new Vector2(10f, 0f);
                button.CenterColor = ToColor(VoiceMuteTableRules.RowCenter(row.Muted));
                canvas.Children.Add(button);
                m_rows.Children.Add(canvas);
                m_rowButtons.Add(button);
                m_rowIds.Add(row.ClientId);
            }
            m_counters.RowsRebuilt++;
        }

        private void UpdateMuteAllVisual()
        {
            m_muteAll.Text = VoiceMuteTableRules.MuteAllActionText(m_module.IsMutedAll);
            m_muteAll.CenterColor = ToColor(VoiceMuteTableRules.MuteAllCenter(m_module.IsMutedAll));
        }

        private void Dismiss()
        {
            if (m_dismissed) return;
            m_dismissed = true;
            m_counters.CloseClicks++;
            DialogsManager.HideDialog(this);
            SuMultiplayer.Engine.ModLog.Info("[SuMP][ui] VOICE 静音表格已关闭 · " +
                m_counters.Describe());
        }

        /// <summary>组合根主动关闭（离开世界 / 卸载）：走同一个 Dismiss，不重复计数。</summary>
        public void DismissFromOwner(string reason)
        {
            if (m_dismissed) return;
            Dismiss();
            SuMultiplayer.Engine.ModLog.Info("[SuMP][ui] VOICE 静音表格由所有者关闭：reason=" +
                (reason ?? "unknown"));
        }

        private static BevelledButtonWidget CreateButton(string name, string text,
            float width, float height)
        {
            return new BevelledButtonWidget
            {
                Name = name,
                Text = text,
                Size = new Vector2(width, height),
                Margin = new Vector2(3f, 0f),
                Color = Color.White,
                CenterColor = new Color(60, 90, 110),
                BevelColor = new Color(120, 120, 120)
            };
        }

        private static Color ToColor(uint rgb) =>
            new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)255);

        private void WarnRateLimited(string key, string message, int intervalMs = 10000)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastLogMs.TryGetValue(key, out long last) && nowMs - last < intervalMs) return;
            m_lastLogMs[key] = nowMs;
            global::Engine.Log.Warning(message + " · " + m_counters.Describe());
        }

        private void InfoRateLimited(string key, string message, int intervalMs = 10000)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastLogMs.TryGetValue(key, out long last) && nowMs - last < intervalMs) return;
            m_lastLogMs[key] = nowMs;
            SuMultiplayer.Engine.ModLog.Info(message + " · " + m_counters.Describe());
        }
    }
}
