using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;

namespace CmdBridgeMod
{
    /// <summary>
    /// 阻塞界面的检测与关闭（注入前 UI 检查 + `act.closeui`）。
    ///
    /// ============================ 为什么必须要有它 ============================
    /// 世界内输入（移动 / 视角 / 鼠标键 / 交互）只在"没有模态界面"时才被引擎读取：
    ///   Source: Survivalcraft/Game/ComponentInput.cs:ComponentInput.UpdateInputFromMouseAndKeyboard
    ///           if (m_componentGui.ModalPanelWidget != null || DialogsManager.HasDialogs(GuiWidget))
    ///               → 切到光标模式，整份 Move/Look/Dig/Hit/Aim/Interact 一个都不写。
    /// 于是"`CR` 建房间弹出的确认框"、"`E` 打开的背包/创造背包/衣物面板"、
    /// "`MoreButton` 展开的二级 HUD"都会让 `act.move` / `act.lookat` / `act.use` 变成
    /// "命令返回成功、人却不动"。本类在每次注入前先**用引擎自己的路径**把这些界面收掉，
    /// 并把"收掉了什么"逐条回报给调用方（`dismissedUi` + 计数）。
    ///
    /// ============================ 用的都是引擎自己的 API ============================
    /// 1) `MessageDialog`（确认框 / 提示框）：`MessageDialog.Dismiss(MessageDialogButton)` 是引擎
    ///    "按下 OK/接受按钮"的唯一路径 —— `MessageDialog.Update` 的 `Input.Ok` / Button1 分支调的就是它；
    ///    它做两件事：AutoHide → `DialogsManager.HideDialog(this)`，然后调用构造时给的 handler。
    ///    Source: Survivalcraft/Game/MessageDialog.cs:MessageDialog.Update
    ///    Source: Survivalcraft/Game/MessageDialog.cs:MessageDialog.Dismiss
    ///    Dismiss 是 private，所以经 SuAPI 的 `ModParentMethod.InvokeParentMethod` 调用
    ///    （与本 Mod 其它反射点同一套 API）。**不用合成键盘事件冒充 OK**：`WidgetInput.Ok`
    ///    只有手柄/触摸会置位，WidgetInput.ClearInput 每帧清它，合成按键到不了那条分支，
    ///    而且会顺带把"用户按 Esc = 取消"的语义搅乱。
    /// 2) `TextBoxDialog`（含 ScMP 的 WindowsTalkDialog）：`TextBoxDialog.Dismiss(null)` = 引擎自己的
    ///    Cancel 分支（不发送任何内容），同样经 ModParentMethod 调用。
    ///    Source: Survivalcraft/Game/TextBoxDialog.cs:TextBoxDialog.Update
    /// 3) 其它 `Dialog`（`GameMenuDialog` / `KeyboardHelpDialog` / 模组自定义对话框…）：
    ///    `DialogsManager.HideDialog(dialog)` = 引擎自己关对话框的那条路径
    ///    （`GameMenuDialog.Update` 的 Resume/Back 分支、`KeyboardHelpDialog.Update` 的 OK 分支）。
    ///    Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.HideDialog
    ///    它立刻把 dialog 从 `m_dialogs` 移除，所以 `HasDialogs(GuiWidget)` 当帧就变 false。
    /// 4) 背包 / 创造背包 / 衣物 / 箱子 / 熔炉 / 工作台等"模态面板"（`ComponentGui.ModalPanelWidget`）：
    ///    引擎自己的关闭路径就是**把它置 null**（`ComponentGui.HandleInput` 的 `Input.Cancel/Back` 分支）。
    ///    Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.HandleInput
    ///    ⚠️ 置 null 之后还有一段 6*dt 的收起动画（`ComponentGui.UpdateModalPanelAnimation`），
    ///    期间 `ModalPanelWidget` **仍非 null**、`ComponentInput` 仍按"模态"处理 —— 短的键盘脉冲
    ///    会正好落在里面。所以调用方（`CommandRouter`）必须等它真的变 null 再注入，
    ///    见 `CommandRouter.WaitForModalPanelHidden`。
    /// 5) 世界内 HUD 的 `MoreContents` 二级面板：引擎每帧把 `MoreContents.IsVisible` 绑在
    ///    `MoreButton.IsChecked` 上（`ComponentGui.UpdateWidgets`），所以"收起"= 把
    ///    `MoreButton.IsChecked` 置 false（与真人再点一下 MoreButton 完全相同）。
    ///    Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.UpdateWidgets
    ///    MoreButton / MoreContents 的取法与引擎自己一模一样：`player.GuiWidget.Children.Find<T>(name)`
    ///    （ComponentGui.cs:230-231），不按全局名字解析（家具面板里也有一个 MoreButton）。
    ///
    /// ============================ 边界 ============================
    /// · 只在**世界内**（`ScreensManager.CurrentScreen is GameScreen`）动手。菜单/设置这类 Screen
    ///   不是本类的职责：那会把用户正在点的菜单顶掉，而且 Screen 没有"Close()"这种通用关闭路径。
    ///   不在世界内时如实回报 `uiSkipped`，命令自己的检查（`world_not_active` / `element_missing`）照旧报错。
    /// · 关闭是**有语义的**：确认框按的是**接受**按钮（引擎的 OK 路径），不是"把框藏起来"。
    ///   所以 `act.key escape` 这种"用引擎自己的 Cancel/Back 关界面"的按键**不触发**自动关闭
    ///   （见 `CommandRouter.IsEngineCloseKey`），否则会把用户想取消的确认框替成"确定"。
    /// · **不碰真实鼠标 / 键盘 / 窗口**：这里没有任何真实输入写入、没有 P/Invoke，全部是对引擎 UI
    ///   对象自己的方法/属性调用（项目铁律：命令行桥存在时禁止抢占真实鼠标与窗口）。
    /// </summary>
    internal static class UiBlocker
    {
        /// <summary>
        /// 一轮最多关多少个对话框之后的"复查轮数"。
        ///
        /// MessageDialog 的 handler 可能**又弹一个**对话框（例如"创建房间失败 → 再弹提示"）。
        /// 只关一轮就会把"还有框盖着"留给注入 = 静默无效。这里最多复查
        /// <see cref="MaxDismissRounds"/> 轮，之后仍有关不掉的就明确报 `ui_blocking`。
        /// </summary>
        internal const int MaxDismissRounds = 3;

        /// <summary>`MessageDialog` 里两个按钮的子控件名（`Content/Dialogs/MessageDialog.xml`）。</summary>
        private const string MessageDialogButton1Name = "MessageDialog.Button1";
        private const string MessageDialogButton2Name = "MessageDialog.Button2";
        private const string MessageDialogLargeLabelName = "MessageDialog.LargeLabel";
        private const string MessageDialogSmallLabelName = "MessageDialog.SmallLabel";

        // ---------------------------------------------------------------- 结果模型

        /// <summary>一条"被关掉的界面"。</summary>
        internal sealed class Dismissal
        {
            /// <summary>`dialog` / `modalPanel` / `hudPanel`。</summary>
            internal string Kind;

            /// <summary>引擎类型名（`MessageDialog` / `FullInventoryWidget` / `MoreContents`…）。</summary>
            internal string Type;

            /// <summary>对话框挂在谁身上（`GameScreen` / `GameWidget`…）；面板/ HUD 为 null。</summary>
            internal string Parent;

            /// <summary>对话框标题（`MessageDialog.LargeLabel` 的文本）。</summary>
            internal string Title;

            /// <summary>对话框正文（`MessageDialog.SmallLabel` 的文本）。</summary>
            internal string Text;

            /// <summary>`accept` / `cancel` / `hide` / `close` / `collapse`。</summary>
            internal string Action;

            /// <summary>按的是哪个按钮（只有 MessageDialog 有：`Button1` / `Button2`）。</summary>
            internal string Button;

            /// <summary>对话框自己的 handler 是否真的被调用过（只有对话框有）。</summary>
            internal bool? HandlerInvoked;

            /// <summary>补充说明（引擎路径 / handler 抛异常等）。</summary>
            internal string Note;

            internal Dictionary<string, object> ToDictionary()
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["kind"] = Kind,
                    ["type"] = Type,
                    ["action"] = Action
                };
                if (Parent != null)
                    result["parent"] = Parent;
                if (Title != null)
                    result["title"] = Title;
                if (Text != null)
                    result["text"] = Text;
                if (Button != null)
                    result["button"] = Button;
                if (HandlerInvoked.HasValue)
                    result["handlerInvoked"] = HandlerInvoked.Value;
                if (Note != null)
                    result["note"] = Note;
                return result;
            }
        }

        /// <summary>一次检查的结果：关掉了什么、还剩什么、为什么没动手。</summary>
        internal sealed class Report
        {
            internal readonly List<Dismissal> Items = new List<Dismissal>();

            /// <summary>关完之后**仍然挡着世界输入**的东西（`dialog:<Type>@<Parent>` / `modalPanel:<Type>`）。</summary>
            internal readonly List<string> Remaining = new List<string>();

            internal string Screen;
            internal bool WorldLoaded;

            /// <summary>没动手的原因（`world_not_loaded` / `screen_not_game` / `player_not_found`）。</summary>
            internal string Skipped;
            internal string SkipDetail;

            /// <summary>本次关掉了几类（计数，供回包与累计统计用）。</summary>
            internal int DialogCount;
            internal int ModalPanelCount;
            internal int HudPanelCount;

            /// <summary>本次是否关过模态面板（关过就要等收起动画结束再注入）。</summary>
            internal bool ClosedModalPanel;

            /// <summary>
            /// 关闭过程中世界被卸载/重载了（对话框 handler 做的，例如 ScMP 的 `CR` → `Create`
            /// 会 `DisposeProject` + 切到 `GameLoading`）。此时已经无法再检查/注入，
            /// 命令自己的 `world_not_active` / `player_not_ready` 会报真实原因。
            /// </summary>
            internal bool WorldUnloaded;

            internal int TotalCount
            {
                get { return Items.Count; }
            }

            /// <summary>一句话摘要（`MessageDialog(accept)`, `FullInventoryWidget(close)`…），给错误信息用。</summary>
            internal string Summary()
            {
                if (Items.Count == 0)
                    return "<none>";
                var parts = new List<string>();
                for (int i = 0; i < Items.Count; i++)
                    parts.Add(Items[i].Type + "(" + Items[i].Action + ")");
                return string.Join(", ", parts.ToArray());
            }

            internal Dictionary<string, object> ToDictionary()
            {
                var items = new List<Dictionary<string, object>>();
                for (int i = 0; i < Items.Count; i++)
                    items.Add(Items[i].ToDictionary());

                var byKind = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["dialogs"] = DialogCount,
                    ["modalPanels"] = ModalPanelCount,
                    ["hudPanels"] = HudPanelCount
                };

                var result = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["dismissedUi"] = items,
                    ["dismissedCount"] = Items.Count,
                    ["dismissedByKind"] = byKind,
                    ["uiScreen"] = Screen,
                    ["uiWorldLoaded"] = WorldLoaded
                };
                if (WorldUnloaded)
                    result["uiWorldUnloadedByDismissal"] = true;
                if (Skipped != null)
                {
                    result["uiSkipped"] = Skipped;
                    if (SkipDetail != null)
                        result["uiSkipDetail"] = SkipDetail;
                }
                if (Remaining.Count > 0)
                    result["uiBlocking"] = new List<string>(Remaining);
                return result;
            }
        }

        // ---------------------------------------------------------------- 入口

        /// <summary>
        /// 关掉当前挡着世界输入的界面。**必须在游戏线程调用**（CommandRouter 通过
        /// `OnGameThread` 进来），因为 UI 控件树不是线程安全的。
        ///
        /// 不抛"还剩什么"的错：由调用方按场景决定（注入前 = 报 `ui_blocking`；
        /// `act.closeui` = 只有真的什么都没有时才报 `ui_not_blocking`）。
        /// </summary>
        /// <param name="messageButton">
        /// MessageDialog 按哪个按钮：1 = 接受（引擎的 OK 路径，`dismissedUi.action = accept`），
        /// 2 = 取消（`Cancel` 路径；对话框没有第二个按钮时退回 Button1，与 `MessageDialog.Update` 一致）。
        /// </param>
        internal static Report Dismiss(int messageButton)
        {
            var report = new Report();
            report.Screen = UiInspector.ScreenName();
            report.WorldLoaded = GameManager.Project != null;
            if (!report.WorldLoaded)
            {
                report.Skipped = "world_not_loaded";
                return report;
            }

            Screen current = ScreensManager.CurrentScreen;
            if (!(current is GameScreen))
            {
                // 菜单/设置/帮助这类界面：不替调用方做切屏决定（那不是"关界面"，是"导航"）。
                report.Skipped = "screen_not_game";
                report.SkipDetail = current != null ? current.GetType().Name : "null";
                return report;
            }

            ComponentPlayer player = GetLocalPlayer();
            if (player == null)
            {
                report.Skipped = "player_not_found";
                return report;
            }

            // ---- ① 对话框：关到没有为止（handler 可能再弹一个，最多 MaxDismissRounds 轮）----
            // 每轮都重新取快照：DialogsManager.HideDialog / MessageDialog.Dismiss 都会**立刻**
            // 从 m_dialogs 里摘掉元素，直接边遍历边摘会踩掉枚举器。
            for (int round = 0; round < MaxDismissRounds; round++)
            {
                Dialog[] dialogs = SnapshotDialogs();
                if (dialogs.Length == 0)
                    break;
                for (int i = 0; i < dialogs.Length; i++)
                    DismissDialog(dialogs[i], messageButton, report);
            }

            // ⚠️ 对话框的 handler 可能把**整个世界**卸载/重载：ScMP 的 `CR` → `Create`
            // 就是 `GameManager.DisposeProject()` + `ScreensManager.SwitchScreen("GameLoading", ...)`
            // （Source: Mod/ScMultiplayer/Modules/Runtime/ScMultiplayerUpdateLoop.cs:CreateRoomFromCurrentWorld）。
            // 那时玩家/ComponentGui 已不属于任何 Project，再去读控件树只会抛异常，
            // 把一次"成功按下了 Create"变成命令失败。这里如实标记并停止后续检查。
            if (GameManager.Project == null)
            {
                report.WorldUnloaded = true;
                return report;
            }

            // ---- ② 模态面板（背包 / 创造背包 / 衣物 / 容器…）----
            ComponentGui gui = null;
            Widget panel = null;
            try
            {
                gui = player.ComponentGui;
                if (gui != null)
                    panel = gui.ModalPanelWidget;
            }
            catch (Exception exception)
            {
                // 世界正在被卸载/组件已释放：不把"读不到"伪装成"没有面板"，也不伪装成
                // "关不掉的阻塞界面"——如实标记成"世界在变化过程中"，交给命令自己的
                // `world_not_active` / `player_not_ready` 报真实原因。
                report.WorldUnloaded = true;
                report.SkipDetail = "ui_unavailable_while_world_changing: " + Describe(exception);
                return report;
            }

            if (panel != null)
            {
                // 引擎自己的关闭路径就是这个赋值（ComponentGui.HandleInput 的 Cancel/Back 分支）。
                try
                {
                    gui.ModalPanelWidget = null;
                }
                catch (Exception exception)
                {
                    // 组件正在被卸载：不谎报"已关闭"。
                    report.WorldUnloaded = true;
                    report.SkipDetail = "modal_panel_close_failed: " + Describe(exception);
                    return report;
                }
                report.Items.Add(new Dismissal
                {
                    Kind = "modalPanel",
                    Type = panel.GetType().Name,
                    Action = "close",
                    Note = "ComponentGui.ModalPanelWidget = null (the engine's own close path, "
                        + "ComponentGui.HandleInput; Source: Survivalcraft/Game/ComponentGui.cs)"
                });
                report.ModalPanelCount++;
                report.ClosedModalPanel = true;
            }

            // ---- ③ 世界内 HUD 的 MoreContents 二级面板 ----
            CollapseMoreContents(player, report);

            // ---- ④ 复查：还有谁挡着？----
            CollectRemaining(gui, report);
            return report;
        }

        /// <summary>
        /// 给"没有可关的界面"一句能直接看懂的实测状态（`act.closeui` 报错时用）。
        /// </summary>
        internal static string DescribeCurrentUi()
        {
            var parts = new List<string>();
            parts.Add("screen=" + (UiInspector.ScreenName() ?? "<null>"));
            try
            {
                parts.Add("dialogs=" + DialogsManager.Dialogs.Count);
            }
            catch (Exception)
            {
                parts.Add("dialogs=<unavailable>");
            }
            try
            {
                ComponentPlayer player = GetLocalPlayer();
                ComponentGui gui = player != null ? player.ComponentGui : null;
                parts.Add("modalPanel=" + (gui != null && gui.ModalPanelWidget != null
                    ? gui.ModalPanelWidget.GetType().Name
                    : "<none>"));
                ContainerWidget guiWidget = player != null ? player.GuiWidget : null;
                ButtonWidget moreButton = guiWidget != null
                    ? guiWidget.Children.Find<ButtonWidget>(UiInspector.MoreToggleName, false)
                    : null;
                parts.Add("[" + UiInspector.MoreContentsName + "].expanded="
                    + (moreButton != null ? moreButton.IsChecked.ToString().ToLowerInvariant() : "<unavailable>"));
            }
            catch (Exception)
            {
                parts.Add("modalPanel=<unavailable>");
            }
            return string.Join(", ", parts.ToArray());
        }

        // ---------------------------------------------------------------- 对话框

        /// <summary>
        /// 关一个对话框。三类走三条**引擎自己的**路径：
        /// MessageDialog → `Dismiss(MessageDialogButton)`；TextBoxDialog → `Dismiss(null)`（Cancel）；
        /// 其它 → `DialogsManager.HideDialog`。
        /// </summary>
        private static void DismissDialog(Dialog dialog, int messageButton, Report report)
        {
            if (dialog == null)
                return;

            var item = new Dismissal
            {
                Kind = "dialog",
                Type = dialog.GetType().Name,
                Parent = ParentName(dialog)
            };

            if (dialog is MessageDialog)
            {
                MessageDialog message = (MessageDialog)dialog;
                item.Title = GetLabelText(message, MessageDialogLargeLabelName);
                item.Text = GetLabelText(message, MessageDialogSmallLabelName);

                // `MessageDialog.Update` 的 Cancel 分支：有 Button2 就按 Button2，否则按 Button1。
                bool cancel = messageButton == 2;
                if (cancel && !HasVisibleButton(message, MessageDialogButton2Name))
                    cancel = false;
                item.Button = cancel ? "Button2" : "Button1";
                item.Action = cancel ? "cancel" : "accept";
                item.Note = "MessageDialog.Dismiss(...) - the engine's own accept/cancel path "
                    + "(Source: Survivalcraft/Game/MessageDialog.cs:MessageDialog.Update)";

                DismissMessageDialog(message, cancel, item);
                report.Items.Add(item);
                report.DialogCount++;
                return;
            }

            if (dialog is TextBoxDialog)
            {
                item.Action = "cancel";
                item.Note = "TextBoxDialog.Dismiss(null) - the engine's own Cancel path "
                    + "(Source: Survivalcraft/Game/TextBoxDialog.cs:TextBoxDialog.Update)";
                try
                {
                    // 显式参数类型 + 一个 null 实参：`Dismiss(string result)` 的 Cancel 分支就是 Dismiss(null)。
                    // ⚠️ 必须写成 new object[] { null }：params 直接传 (object)null 会变成"没有实参"。
                    ModManager.Instance.ModParentMethod.InvokeParentMethod<object>(
                        dialog, "Dismiss", new[] { typeof(string) }, new object[] { null });
                    item.HandlerInvoked = true;
                }
                catch (Exception exception)
                {
                    item.HandlerInvoked = false;
                    item.Note += "; Dismiss threw " + Describe(exception);
                }
                report.Items.Add(item);
                report.DialogCount++;
                return;
            }

            // 其它对话框：引擎自己的关闭路径（GameMenuDialog 的 Resume/Back 走的就是这一句）。
            item.Action = "hide";
            item.HandlerInvoked = false;
            item.Note = "DialogsManager.HideDialog(dialog) - the engine's own close path "
                + "(Source: Survivalcraft/Game/DialogsManager.cs:DialogsManager.HideDialog); "
                + "the dialog's own handler is NOT invoked (nothing is accepted on its behalf)";
            try
            {
                DialogsManager.HideDialog(dialog);
            }
            catch (Exception exception)
            {
                item.Note += "; HideDialog threw " + Describe(exception);
            }
            report.Items.Add(item);
            report.DialogCount++;
        }

        /// <summary>
        /// 按 MessageDialog 自己的接受/取消路径关它，并把"handler 是否真的跑了"如实写进结果。
        ///
        /// `Dismiss` 是 private 实例方法，签名 `Dismiss(MessageDialogButton)`；用显式参数类型调用，
        /// 避免任何重载猜测。handler 自己抛异常时 `Dismiss` 已经在抛之前做过 HideDialog，
        /// 所以"对话框是否还在 `DialogsManager.Dialogs` 里"是判断它到底关没关的唯一可靠依据。
        /// </summary>
        private static void DismissMessageDialog(MessageDialog dialog, bool cancel, Dismissal item)
        {
            MessageDialogButton button = cancel
                ? MessageDialogButton.Button2
                : MessageDialogButton.Button1;
            try
            {
                ModManager.Instance.ModParentMethod.InvokeParentMethod<object>(
                    dialog, "Dismiss", new[] { typeof(MessageDialogButton) }, button);
                item.HandlerInvoked = true;
            }
            catch (Exception exception)
            {
                // 异常可能来自 handler 本身（Dismiss 先 HideDialog 再调 handler）。
                // 用"还在不在 m_dialogs 里"区分"接受失败"和"接受成功但 handler 抛了"。
                bool stillShown = IsDialogShown(dialog);
                item.HandlerInvoked = !stillShown;
                item.Note += stillShown
                    ? "; Dismiss threw " + Describe(exception)
                    : "; the dialog was accepted and closed, but its handler threw "
                        + Describe(exception);
            }
        }

        // ---------------------------------------------------------------- HUD

        /// <summary>
        /// 收起世界内 HUD 的 `MoreContents` 二级面板：`MoreButton.IsChecked = false`。
        ///
        /// 控件取法与引擎自己完全一致（`ComponentGui.cs:230-231`）：从玩家自己的 `GuiWidget` 里按名字找。
        /// 不做全局名字解析 —— 家具面板里也有一个 `MoreButton`，全局找会拿到另一个开关。
        /// </summary>
        private static void CollapseMoreContents(ComponentPlayer player, Report report)
        {
            ButtonWidget moreButton;
            Widget moreContents;
            try
            {
                // `player.GuiWidget` 也可能在世界卸载过程中失效，所以连它一起放进 try 里。
                ContainerWidget guiWidget = player != null ? player.GuiWidget : null;
                if (guiWidget == null)
                    return;
                moreButton = guiWidget.Children.Find<ButtonWidget>(UiInspector.MoreToggleName, false);
                moreContents = guiWidget.Children.Find<Widget>(UiInspector.MoreContentsName, false);
            }
            catch (Exception exception)
            {
                report.Remaining.Add("hudPanel:" + UiInspector.MoreContentsName
                    + "(lookup_failed: " + Describe(exception) + ")");
                return;
            }

            if (moreButton == null)
                return;   // HUD 结构里没有这个开关：没有可收起的二级面板

            if (!moreButton.IsChecked)
                return;   // 本来就是收起的：不制造"关了一次"的假记录

            // 引擎每帧把 MoreContents.IsVisible 绑在 IsChecked 上（ComponentGui.UpdateWidgets），
            // 所以改这一个状态就等于真人再点一下 MoreButton。
            try
            {
                moreButton.IsChecked = false;
            }
            catch (Exception exception)
            {
                report.Remaining.Add("hudPanel:" + UiInspector.MoreContentsName
                    + "(collapse_failed: " + Describe(exception) + ")");
                return;
            }
            report.Items.Add(new Dismissal
            {
                Kind = "hudPanel",
                Type = UiInspector.MoreContentsName,
                Action = "collapse",
                Button = UiInspector.MoreToggleName,
                Note = UiInspector.MoreToggleName + ".IsChecked = false; the engine binds "
                    + UiInspector.MoreContentsName + ".IsVisible to it every frame "
                    + "(Source: Survivalcraft/Game/ComponentGui.cs:ComponentGui.UpdateWidgets)"
            });
            report.HudPanelCount++;

            if (moreContents == null)
            {
                // 开关在、内容容器不在 = HUD 结构与引擎不符：不能假装收起了。
                report.Remaining.Add("hudPanel:" + UiInspector.MoreContentsName + "(container_missing)");
            }
        }

        // ---------------------------------------------------------------- 复查 / 工具

        /// <summary>复查还有谁挡着世界输入。只剩真正会吃掉输入的两类（对话框、模态面板）。</summary>
        private static void CollectRemaining(ComponentGui gui, Report report)
        {
            try
            {
                ReadOnlyList<Dialog> dialogs = DialogsManager.Dialogs;
                for (int i = 0; i < dialogs.Count; i++)
                {
                    Dialog dialog = dialogs[i];
                    report.Remaining.Add("dialog:" + dialog.GetType().Name + "@" + ParentName(dialog));
                }
            }
            catch (Exception exception)
            {
                report.Remaining.Add("dialog:<unavailable>(" + Describe(exception) + ")");
            }

            try
            {
                if (gui != null && gui.ModalPanelWidget != null)
                    report.Remaining.Add("modalPanel:" + gui.ModalPanelWidget.GetType().Name);
            }
            catch (Exception exception)
            {
                // 读不到 = 组件正在被卸载；不谎报"没有面板"，也不谎报"有面板"。
                report.Remaining.Add("modalPanel:<unavailable>(" + Describe(exception) + ")");
            }
        }

        /// <summary>当前所有对话框的快照（先复制，再关：HideDialog 会立刻改 `m_dialogs`）。</summary>
        private static Dialog[] SnapshotDialogs()
        {
            ReadOnlyList<Dialog> all = DialogsManager.Dialogs;
            var copy = new Dialog[all.Count];
            for (int i = 0; i < all.Count; i++)
                copy[i] = all[i];
            return copy;
        }

        private static bool IsDialogShown(Dialog dialog)
        {
            ReadOnlyList<Dialog> all = DialogsManager.Dialogs;
            for (int i = 0; i < all.Count; i++)
            {
                if (ReferenceEquals(all[i], dialog))
                    return true;
            }
            return false;
        }

        private static string ParentName(Dialog dialog)
        {
            if (dialog == null || dialog.ParentWidget == null)
                return "null";
            return dialog.ParentWidget.GetType().Name;
        }

        private static string GetLabelText(ContainerWidget root, string name)
        {
            try
            {
                LabelWidget label = root.Children.Find<LabelWidget>(name, false);
                return label != null ? label.Text : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool HasVisibleButton(ContainerWidget root, string name)
        {
            try
            {
                ButtonWidget button = root.Children.Find<ButtonWidget>(name, false);
                return button != null && button.IsVisible;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>本地玩家（与世界内输入注入同一个：`SubsystemPlayers.ComponentPlayers[0]`）。</summary>
        private static ComponentPlayer GetLocalPlayer()
        {
            if (GameManager.Project == null)
                return null;
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null || players.ComponentPlayers.Count == 0)
                return null;
            return players.ComponentPlayers[0];
        }

        private static string Describe(Exception exception)
        {
            if (exception == null)
                return "<null>";
            return exception.GetType().Name + ": " + exception.Message;
        }
    }
}
