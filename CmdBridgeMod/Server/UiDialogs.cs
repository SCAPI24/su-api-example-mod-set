using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace CmdBridgeMod
{
    /// <summary>
    /// 对话框的**文本编辑**与**接受/取消**动作（`act.settext` / `act.accept` / `act.dialog`）。
    ///
    /// ============================ 为什么是这两个机制 ============================
    /// 1) 文本：写 `TextBoxWidget.Text`（它的公开 setter）。
    ///    setter 内部就是 `m_text = ...; CaretPosition = CaretPosition; TextChanged?.Invoke(this)`
    ///    （Source: `Survivalcraft/Game/TextBoxWidget.cs:47-63`）。`TextChanged` 与真人敲键盘
    ///    触发的是**同一个事件**，所以对话框的临时状态会一起更新：
    ///    `EditMemoryBankDialog.TextBox_TextChanged` 把 16 行拼起来 `LoadString`，
    ///    `EditTruthTableDialog` 的 LinearText 同理。聚焦不是必需条件（键盘路径才要求 `HasFocus`）。
    ///
    /// 2) 接受：**在 OK 按钮上合成一次引擎自己的 Tap+Click**（`WidgetInput` → `ClickableWidget.Update`
    ///    → `IsClicked`），而不是直接调对话框的 `Dismiss`。
    ///    为什么不能调 `Dismiss`：内存条/真值表的**提交发生在 `Update` 的 `IsClicked` 分支里**，
    ///    `Dismiss` 只负责隐藏和回调 ——
    ///      `EditMemoryBankDialog.cs:114-118`：`m_memoryBankData.Data = m_tmpMemoryBankData.Data;
    ///      Dismiss(result: true);`
    ///      `EditTruthTableDialog.cs:95-99`：`m_truthTableData.Data = m_tmpTruthTableData.Data;
    ///      Dismiss(result: true);`
    ///    直接调 `Dismiss(true)` 只会关窗、把用户刚改的数据丢掉。所以必须让引擎自己的
    ///    `m_okButton.IsClicked` 分支跑起来 → 唯一通用做法就是让按钮真的"被点一下"。
    ///
    /// 点击走的是 `UiService.ClickDirectCore`：它把"按过一下"写进**目标控件所在那一层**的
    /// `WidgetInput`（软光标位置、`m_mouseDownPoint`、`m_mouseDownButton` 都是每面一份），
    /// 引擎自己在同一帧派生 `Tap`/`Click`。按钮的 `IsClicked` 因此与真人点它完全等价。
    ///
    /// ============================ 边界 ============================
    /// · 只操作**当前挂载**的对话框（`UiInspector.IsUnderRoot`）；未挂载明确报 `dialog_not_mounted`。
    /// · 不猜按钮：按稳定 Name 排名（`.OK` / `OkButton` → `.Accept` / `.Yes` / `.Button1`；
    ///   取消：`.Cancel` / `.CancelButton` / `.No` / `.Button2`）。多个同级命中报
    ///   `ambiguous_selector` 并列出候选 path；一个都没有报 `*_button_not_found`，可用
    ///   `act.accept <button-selector>` 显式指定。
    /// · 点完**逐帧复查** `DialogsManager.Dialogs`：只有对话框真的离开了列表才算成功；
    ///   否则报 `accept_failed` / `cancel_failed`，并带上落点与命中控件 —— 绝不"命令返回成功、
    ///   界面纹丝不动"。
    /// · 全程不碰真实鼠标/窗口（`focus` 策略负责窗口活跃状态），与项目铁律一致。
    /// </summary>
    internal sealed class UiDialogs
    {
        /// <summary>点完按钮后最多等多少帧/毫秒确认对话框已关闭。</summary>
        private const int CloseTimeoutMilliseconds = 2500;

        /// <summary>单次复查的等待上限（游戏停止出帧时不会把命令线程永久挂住）。</summary>
        private const int PollWaitMilliseconds = 300;

        private readonly InputInjector m_injector;

        internal UiDialogs(InputInjector injector)
        {
            if (injector == null)
                throw new ArgumentNullException(nameof(injector));
            m_injector = injector;
        }

        // ---------------------------------------------------------------- 文本

        /// <summary>
        /// `act.settext &lt;selector&gt; &lt;text&gt;`：用 `TextBoxWidget.Text` 的公开 setter 替换整个值。
        /// 返回里同时给出 `applied`（setter 返回后立即读到的值）与 `final`（下一帧、对话框
        /// `Update` 跑过之后的值），因为有些对话框会把界面值规范化（例如 LinearText 会按
        /// `SaveString` 重新铺满 256 字符）—— 那是引擎自己的表示，不是失败。
        /// </summary>
        internal object SetText(string selector, string text)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                throw new BridgeCommandException(
                    "invalid_argument",
                    "act.settext needs a text box selector (path/name, e.g. 'EditMemoryBankDialog.Line0').");
            }
            string requested = text ?? string.Empty;
            return m_injector.RunOnFrame(delegate { return SetTextCore(selector, requested); });
        }

        private object SetTextCore(string selector, string requested)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                throw new BridgeCommandException("not_ready", "The UI is not ready yet.");

            Widget widget = UiInspector.Resolve(root, selector, false, default(Vector2));
            UiInspector.RequireMounted(widget, selector);
            if (!TextBoxAccess.IsTextBox(widget))
            {
                throw new BridgeCommandException(
                    "not_a_textbox",
                    "'" + selector + "' resolved to " + widget.GetType().Name
                    + ", not a TextBoxWidget. Use 'ui --filter' to find the text box name.");
            }
            // 不要求 IsVisibleGlobal：真值表的 `LinearText` 在**隐藏的 LinearPanel** 里
            // （`EditTruthTableDialog.cs:47` 把它设为不可见），但写它的 `Text` 仍会触发
            // `TextChanged` 把数据写进 `m_tmpTruthTableData`，正是我们要的效果。
            // 只要求"已挂载"（`RequireMounted` 已经在上面做过）。

            int maximumLength = TextBoxAccess.MaximumLength(widget);
            string before = TextBoxAccess.Text(widget);
            if (requested.Length > maximumLength)
            {
                // 引擎 setter 会静默截断；自动化不能接受"少写几个字符还报成功"。
                throw new BridgeCommandException(
                    "text_rejected",
                    "'" + selector + "' has MaximumLength=" + maximumLength
                    + " but the requested text is " + requested.Length + " characters."
                    + " Nothing was written (the engine's Text setter would silently truncate).");
            }

            string applied;
            try
            {
                TextBoxAccess.SetText(widget, requested);
                applied = TextBoxAccess.Text(widget);
            }
            catch (BridgeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException(
                    "text_rejected",
                    "TextBoxWidget.Text setter threw " + Describe(exception));
            }

            if (!string.Equals(applied, requested, StringComparison.Ordinal))
            {
                throw new BridgeCommandException(
                    "text_rejected",
                    "the TextBoxWidget.Text setter did not keep the requested value: requested '"
                    + requested + "' (" + requested.Length + "), applied '" + applied + "' ("
                    + applied.Length + ").");
            }

            // 下一帧再读一次：对话框的 Update 可能把界面值规范化（LinearText 会补满 256 字符）。
            // 用帧首泵而不是 RunOnFrame 的嵌套派发：无论调用线程是谁，它都保证"下一帧"。
            string final = null;
            bool readbackTimedOut = false;
            bool readbackSkipped = false;
            if (GameThreadInvoker.IsGameThread())
            {
                // 在游戏线程上等待下一帧会把这帧卡住（帧不推进，泵也跑不了）。
                // 写入已经完成；读回留给调用方在下一帧自己看，这里如实标注。
                readbackSkipped = true;
            }
            else
            {
                var done = new ManualResetEventSlim(false);
                try
                {
                    m_injector.Pump.Enqueue(delegate
                    {
                        try
                        {
                            final = TextBoxAccess.Text(widget);
                        }
                        catch (Exception exception)
                        {
                            final = "<readback failed: " + Describe(exception) + ">";
                        }
                        finally
                        {
                            done.Set();
                        }
                    });
                    if (!done.Wait(CloseTimeoutMilliseconds))
                        readbackTimedOut = true;
                }
                finally
                {
                    done.Dispose();
                }
            }

            if (!readbackTimedOut && !readbackSkipped && final != null
                && string.Equals(final, before, StringComparison.Ordinal)
                && !string.Equals(before, requested, StringComparison.Ordinal))
            {
                throw new BridgeCommandException(
                    "text_rejected",
                    "'" + selector + "' reverted to its previous value ('" + Shorten(before)
                    + "') on the next frame. The dialog did not keep the text; nothing was accepted.");
            }

            Dialog dialog = UiInspector.FindAncestorDialog(widget);
            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["selector"] = selector,
                ["path"] = UiInspector.BuildPath(widget),
                ["type"] = widget.GetType().Name,
                ["dialog"] = dialog != null ? dialog.GetType().Name : null,
                ["before"] = before,
                ["requested"] = requested,
                ["applied"] = applied,
                ["final"] = readbackTimedOut || readbackSkipped ? null : final,
                ["changed"] = !string.Equals(before, applied, StringComparison.Ordinal),
                ["normalized"] = !readbackTimedOut && !readbackSkipped && final != null
                    && !string.Equals(final, applied, StringComparison.Ordinal),
                ["readbackTimedOut"] = readbackTimedOut,
                ["readbackSkipped"] = readbackSkipped,
                ["visible"] = widget.IsVisibleGlobal,
                ["maximumLength"] = maximumLength,
                ["hasFocus"] = TextBoxAccess.HasFocus(widget),
                ["overwriteMode"] = TextBoxAccess.OverwriteMode(widget),
                ["caretPosition"] = TextBoxAccess.CaretPosition(widget),
                ["mechanism"] = "TextBoxWidget.Text public setter -> TextChanged (same event as typing)"
                    + " (Source: Survivalcraft/Game/TextBoxWidget.cs:47-63)"
            };
            return result;
        }

        // ---------------------------------------------------------------- 接受 / 取消

        /// <summary>`act.accept [dialog-selector] [button-selector]`：接受当前（或指定）对话框。</summary>
        internal object Accept(string dialogTarget, string buttonTarget)
        {
            return RunDialogAction("accept", dialogTarget, buttonTarget);
        }

        /// <summary>`act.dialog cancel`：取消当前（或指定）对话框。</summary>
        internal object Cancel(string dialogTarget, string buttonTarget)
        {
            return RunDialogAction("cancel", dialogTarget, buttonTarget);
        }

        private object RunDialogAction(string action, string dialogTarget, string buttonTarget)
        {
            // 本动作必须在**命令线程**上跑：它要跨帧等对话框真的关闭。游戏线程上等待会把帧卡住，
            // 于是"永远等不到下一帧"。明确拒绝，而不是假装成功/无谓地卡 2.5 秒。
            if (GameThreadInvoker.IsGameThread())
            {
                throw new BridgeCommandException(
                    "invalid_argument",
                    "act." + action + " must be invoked through the command channel (TCP/CLI);"
                    + " it waits across frames for the dialog to close.");
            }

            // 第一步：在游戏线程解析"对话框 + 要点的按钮"，并把按钮 path 带回来。
            var planned = (Dictionary<string, object>)m_injector.RunOnFrame(
                delegate { return Plan(action, dialogTarget, buttonTarget); });

            Dialog dialog = (Dialog)planned["dialogObject"];
            string buttonPath = (string)planned["buttonPath"];
            string dialogPath = (string)planned["dialogPath"];
            string buttonName = (string)planned["buttonName"];

            // 第二步：点它。ClickDirectCore 写的是按钮所在那一层输入面，并校验窗口活跃 / 命中目标。
            Dictionary<string, object> clicked = null;
            try
            {
                clicked = (Dictionary<string, object>)m_injector.RunOnFrame(
                    delegate { return m_injector.Ui.ClickDirectCore(buttonPath); });
            }
            catch (BridgeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException(
                    action + "_failed",
                    "clicking '" + buttonPath + "' threw " + Describe(exception));
            }

            object onTarget;
            if (clicked.TryGetValue("clickTargetOnTarget", out onTarget)
                && onTarget is bool isOnTarget && !isOnTarget)
            {
                throw new BridgeCommandException(
                    action + "_failed",
                    "the click point on '" + buttonPath + "' hit "
                    + (clicked.ContainsKey("clickTarget") ? clicked["clickTarget"] : "another widget")
                    + " instead of the target button. Nothing was clicked; the dialog is still open."
                    + " Retry with an explicit button selector.");
            }

            // 第三步：逐帧复查对话框是否真的从 DialogsManager.Dialogs 里消失。
            bool closed = WaitForDialogClosed(dialog, CloseTimeoutMilliseconds);
            if (!closed)
            {
                throw new BridgeCommandException(
                    action + "_failed",
                    "clicked '" + (buttonName ?? "?") + "' (" + buttonPath + ")"
                    + " but the dialog '" + dialog.GetType().Name
                    + "' is still in DialogsManager.Dialogs after "
                    + CloseTimeoutMilliseconds + " ms. clickPoint="
                    + DescribeClickPoint(clicked)
                    + "; hit=" + (clicked.ContainsKey("clickTarget") ? clicked["clickTarget"] : "?")
                    + ". The dialog did not run its accept/cancel branch.");
            }

            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["action"] = action,
                ["dialog"] = dialog.GetType().Name,
                ["dialogPath"] = dialogPath,
                ["button"] = buttonName,
                ["buttonPath"] = buttonPath,
                ["closed"] = true,
                ["click"] = clicked,
                ["mechanism"] = "engine input path: synthetic Tap+Click through WidgetInput on the"
                    + " button's own input surface -> ClickableWidget.IsClicked -> the dialog's own"
                    + " Update branch performs the commit"
                    + " (Source: Survivalcraft/Game/WidgetInput.cs:735-769,"
                    + " Survivalcraft/Game/ClickableWidget.cs:24-52,"
                    + " Survivalcraft/Game/EditMemoryBankDialog.cs:114-118)",
                ["note"] = action == "accept"
                    ? "The dialog's IsClicked branch ran: EditMemoryBankDialog/EditTruthTableDialog"
                        + " copy their temporary data to the block data *before* Dismiss (that is why"
                        + " an OK click is used instead of invoking Dismiss directly)."
                    : "The dialog's own cancel button was clicked."
            };
            return result;
        }

        /// <summary>在游戏线程解析对话框与按钮；返回的对象里只有简单值 + `dialogObject`。</summary>
        private Dictionary<string, object> Plan(string action, string dialogTarget, string buttonTarget)
        {
            Widget explicitButton = null;
            Dialog dialog = null;

            if (!string.IsNullOrWhiteSpace(dialogTarget))
            {
                dialog = ResolveDialog(dialogTarget, out explicitButton);
            }
            if (!string.IsNullOrWhiteSpace(buttonTarget))
            {
                Widget button = ResolveWidget(buttonTarget);
                Dialog owner = UiInspector.FindAncestorDialog(button);
                if (dialog == null)
                    dialog = owner;
                else if (owner != null && !ReferenceEquals(owner, dialog))
                {
                    throw new BridgeCommandException(
                        "invalid_argument",
                        "button '" + buttonTarget + "' belongs to " + owner.GetType().Name
                        + ", not to " + dialog.GetType().Name + ".");
                }
                explicitButton = button;
            }

            if (dialog == null)
                dialog = TopmostMountedDialog();
            if (dialog == null)
            {
                throw new BridgeCommandException(
                    "dialog_not_found",
                    "No active dialog is mounted on the current screen. Open one first"
                    + " (obs.dialogs lists the active dialogs).");
            }
            if (!UiInspector.IsUnderRoot(dialog))
            {
                throw new BridgeCommandException(
                    "dialog_not_mounted",
                    "Dialog '" + dialog.GetType().Name + "' (" + UiInspector.BuildPath(dialog)
                    + ") is active but not mounted on the current screen; switch back to its screen"
                    + " before accepting/cancelling.");
            }

            Widget chosen = explicitButton;
            if (chosen != null && !(chosen is ButtonWidget))
            {
                throw new BridgeCommandException(
                    "invalid_argument",
                    "'" + buttonTarget + "' is " + chosen.GetType().Name
                    + ", not a ButtonWidget (clickable dialog button).");
            }
            if (chosen == null)
                chosen = FindDialogButton(dialog, action);

            if (chosen == null)
            {
                throw new BridgeCommandException(
                    action == "accept" ? "accept_button_not_found" : "cancel_button_not_found",
                    "no visible " + action + " button in dialog '" + dialog.GetType().Name
                    + "'. Candidate buttons: " + DescribeButtons(dialog)
                    + ". Pass one explicitly: act." + (action == "accept" ? "accept" : "dialog cancel")
                    + " --button <button-selector>.");
            }
            if (!chosen.IsVisibleGlobal || !chosen.IsEnabledGlobal)
            {
                throw new BridgeCommandException(
                    action + "_failed",
                    "the " + action + " button '" + UiInspector.BuildPath(chosen)
                    + "' is not visible/enabled right now.");
            }

            var result = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["dialogObject"] = dialog,
                ["dialogPath"] = UiInspector.BuildPath(dialog),
                ["buttonPath"] = UiInspector.BuildPath(chosen),
                ["buttonName"] = chosen.Name ?? chosen.GetType().Name,
                ["buttonType"] = chosen.GetType().Name
            };
            return result;
        }

        /// <summary>
        /// 解析对话框：先按**活动对话框的类型名/Name**精确匹配（最直观），再退回 UI 选择器
        /// （path / name / text: / id）。解析到按钮时同时把它当作显式按钮返回。
        /// </summary>
        private static Dialog ResolveDialog(string target, out Widget explicitButton)
        {
            explicitButton = null;
            string wanted = target.Trim();

            ReadOnlyList<Dialog> dialogs = DialogsManager.Dialogs;
            var matches = new List<Dialog>();
            for (int i = 0; i < dialogs.Count; i++)
            {
                Dialog dialog = dialogs[i];
                if (dialog == null)
                    continue;
                if (string.Equals(dialog.GetType().Name, wanted, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(dialog.Name)
                        && string.Equals(dialog.Name, wanted, StringComparison.OrdinalIgnoreCase)))
                {
                    matches.Add(dialog);
                }
            }
            if (matches.Count == 1)
                return matches[0];
            if (matches.Count > 1)
            {
                throw new BridgeCommandException(
                    "ambiguous_selector",
                    "more than one active dialog matches '" + wanted + "': "
                    + DescribeDialogs(matches) + ". Use the dialog path instead.");
            }

            // 退回 UI 选择器（path / name / text: / id）。这里要把 element_missing 换成 dialog_not_found，
            // 因为命令的语义是"找对话框"，不是"找任意控件"。
            Widget widget;
            try
            {
                widget = ResolveWidget(wanted);
            }
            catch (BridgeCommandException exception)
            {
                if (string.Equals(exception.Code, "element_missing", StringComparison.Ordinal))
                {
                    throw new BridgeCommandException(
                        "dialog_not_found",
                        "no active dialog matches '" + wanted + "' and no UI element matches it"
                        + " either. obs.dialogs lists the active dialogs.");
                }
                throw;
            }

            if (widget is ButtonWidget)
                explicitButton = widget;
            Dialog ancestor = UiInspector.FindAncestorDialog(widget);
            if (ancestor == null)
            {
                throw new BridgeCommandException(
                    "dialog_not_found",
                    "'" + wanted + "' resolves to " + widget.GetType().Name
                    + " (" + UiInspector.BuildPath(widget) + "), which is not inside a dialog.");
            }
            return ancestor;
        }

        private static Widget ResolveWidget(string selector)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                throw new BridgeCommandException("not_ready", "The UI is not ready yet.");
            Widget widget = UiInspector.Resolve(root, selector, false, default(Vector2));
            UiInspector.RequireMounted(widget, selector);
            return widget;
        }

        private static Dialog TopmostMountedDialog()
        {
            ReadOnlyList<Dialog> dialogs = DialogsManager.Dialogs;
            for (int i = dialogs.Count - 1; i >= 0; i--)
            {
                Dialog dialog = dialogs[i];
                if (dialog != null && UiInspector.IsUnderRoot(dialog))
                    return dialog;
            }
            return null;
        }

        // ---------------------------------------------------------------- 按钮选择

        /// <summary>
        /// 在对话框子树里按 Name 找接受/取消按钮。**只认稳定 Name，不认本地化文字**
        /// （装了翻译 Mod 时 `Text` 会变，Name 不变）。
        /// </summary>
        private static Widget FindDialogButton(Dialog dialog, string action)
        {
            var candidates = new List<Widget>();
            int bestRank = int.MaxValue;
            foreach (Widget widget in dialog.AllChildren)
            {
                var button = widget as ButtonWidget;
                if (button == null || !widget.IsVisibleGlobal || !widget.IsEnabledGlobal)
                    continue;
                int rank = RankButton(widget.Name, action);
                if (rank < 0)
                    continue;
                if (rank < bestRank)
                {
                    bestRank = rank;
                    candidates.Clear();
                    candidates.Add(widget);
                }
                else if (rank == bestRank)
                {
                    candidates.Add(widget);
                }
            }

            if (candidates.Count == 0)
            {
                // MessageDialog 的 Cancel 分支：Button2 可见就按 Button2，否则按 Button1
                // （Source: Survivalcraft/Game/MessageDialog.cs:63-72）。
                if (action == "cancel" && dialog is MessageDialog)
                {
                    foreach (Widget widget in dialog.AllChildren)
                    {
                        var button = widget as ButtonWidget;
                        if (button == null || !widget.IsVisibleGlobal || !widget.IsEnabledGlobal)
                            continue;
                        if (RankButton(widget.Name, "accept") >= 0 && IsButton1(widget.Name))
                            return widget;
                    }
                }
                return null;
            }
            if (candidates.Count > 1)
            {
                throw new BridgeCommandException(
                    "ambiguous_selector",
                    "dialog '" + dialog.GetType().Name + "' has " + candidates.Count
                    + " equally-ranked " + action + " buttons: " + DescribeWidgets(candidates)
                    + ". Pass one explicitly with 'button'.");
            }
            return candidates[0];
        }

        private static int RankButton(string name, string action)
        {
            string value = name ?? string.Empty;
            if (value.Length == 0)
                return -1;

            if (action == "accept")
            {
                if (EndsWithName(value, "OK") || EndsWithName(value, "OkButton")
                    || EndsWithName(value, "OkayButton"))
                {
                    return 0;
                }
                if (EndsWithName(value, "Accept") || EndsWithName(value, "AcceptButton")
                    || EndsWithName(value, "Yes") || EndsWithName(value, "YesButton")
                    || EndsWithName(value, "Confirm") || EndsWithName(value, "ConfirmButton"))
                {
                    return 1;
                }
                if (IsButton1(value))
                    return 2;
                return -1;
            }

            if (EndsWithName(value, "Cancel") || EndsWithName(value, "CancelButton")
                || EndsWithName(value, "No") || EndsWithName(value, "NoButton"))
            {
                return 0;
            }
            if (IsButton2(value))
                return 1;
            return -1;
        }

        /// <summary>名字的"尾段"匹配：`OK`、`Dialog.OK`、`Dialog.OkButton` 都算，`NotOK` 不算。</summary>
        private static bool EndsWithName(string name, string suffix)
        {
            if (name.Length < suffix.Length)
                return false;
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;
            if (name.Length == suffix.Length)
                return true;
            char previous = name[name.Length - suffix.Length - 1];
            if (previous == '.' || previous == '_' || previous == '-')
                return true;
            // CamelCase 边界：`DialogOK` / `MyOkButton` 算，`NOTOK` 不算（前一个字符必须小写）。
            return char.IsUpper(suffix[0]) && char.IsLower(previous);
        }

        private static bool IsButton1(string name)
        {
            return EndsWithName(name, "Button1");
        }

        private static bool IsButton2(string name)
        {
            return EndsWithName(name, "Button2");
        }

        private static string DescribeButtons(Dialog dialog)
        {
            var buttons = new List<Widget>();
            foreach (Widget widget in dialog.AllChildren)
            {
                if (widget is ButtonWidget && widget.IsVisibleGlobal)
                    buttons.Add(widget);
            }
            return buttons.Count == 0 ? "<none>" : DescribeWidgets(buttons);
        }

        private static string DescribeWidgets(List<Widget> widgets)
        {
            var parts = new List<string>();
            for (int i = 0; i < widgets.Count; i++)
            {
                Widget widget = widgets[i];
                parts.Add((string.IsNullOrEmpty(widget.Name) ? widget.GetType().Name : widget.Name)
                    + " (" + UiInspector.BuildPath(widget) + ")");
            }
            return string.Join(", ", parts.ToArray());
        }

        private static string DescribeDialogs(List<Dialog> dialogs)
        {
            var parts = new List<string>();
            for (int i = 0; i < dialogs.Count; i++)
            {
                parts.Add(dialogs[i].GetType().Name + " (" + UiInspector.BuildPath(dialogs[i]) + ")");
            }
            return string.Join(", ", parts.ToArray());
        }

        // ---------------------------------------------------------------- 复查

        private static bool IsDialogActive(Dialog dialog)
        {
            ReadOnlyList<Dialog> dialogs = DialogsManager.Dialogs;
            for (int i = 0; i < dialogs.Count; i++)
            {
                if (ReferenceEquals(dialogs[i], dialog))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 逐帧检查对话框是否已从 `DialogsManager.Dialogs` 移除。
        /// `DialogsManager.HideDialog` 在游戏线程上是**立即**执行的（`Dispatcher.Dispatch` 主线程内联，
        /// Source: `Engine/Engine/Dispatcher.cs:40-44`），所以按钮生效的同一帧它就已经不在列表里了；
        /// 这里等的是"下一帧确认"，不是动画结束。
        /// </summary>
        private bool WaitForDialogClosed(Dialog dialog, int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                bool active = true;
                var done = new ManualResetEventSlim(false);
                try
                {
                    m_injector.Pump.Enqueue(delegate
                    {
                        try
                        {
                            active = IsDialogActive(dialog);
                        }
                        finally
                        {
                            done.Set();
                        }
                    });
                    if (!done.Wait(PollWaitMilliseconds))
                    {
                        // 游戏没在出帧：继续等到 deadline（每次最多 300ms，不会永久阻塞）。
                        Thread.Sleep(20);
                        continue;
                    }
                }
                finally
                {
                    done.Dispose();
                }
                if (!active)
                    return true;
                Thread.Sleep(20);
            }
            return false;
        }

        private static string DescribeClickPoint(Dictionary<string, object> clicked)
        {
            object point;
            if (clicked != null && clicked.TryGetValue("clickPoint", out point))
            {
                var values = point as Dictionary<string, object>;
                if (values != null && values.ContainsKey("x") && values.ContainsKey("y"))
                {
                    return "(" + values["x"] + "," + values["y"] + ")";
                }
            }
            return "<unknown>";
        }

        private static string Describe(Exception exception)
        {
            Exception inner = exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException
                : exception;
            return inner.GetType().Name + ": " + inner.Message;
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return text.Length <= 48 ? text : text.Substring(0, 47) + "…";
        }
    }
}
