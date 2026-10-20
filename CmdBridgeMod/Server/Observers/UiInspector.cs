using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Text;

namespace CmdBridgeMod
{
    /// <summary>
    /// 界面元素读取：把当前界面上"能被玩家操作的东西"连同坐标导出。
    ///
    /// 关键设计（doc/cmd-bridge-plan.md §2.2/§2.3/§4.2）：
    ///   · hittable 用游戏自己的 Widget.HitTestGlobal(元素中心) 判定，
    ///     与真实点击走同一条命中路径，因此"报告可点 = 真的点得到"。
    ///   · 同时导出 clientPoint（客户区像素）与 screenPoint（+ 窗口位置 = 屏幕绝对坐标）。
    ///   · 隐藏元素也会列出（visible=false），因为键鼠输入通道不因 UI 隐藏而失效。
    /// </summary>
    internal static class UiInspector
    {
        private static readonly string[] s_interactiveTypes =
        {
            "ButtonWidget", "BitmapButtonWidget", "BevelledButtonWidget", "TextButtonWidget",
            "ClickableWidget", "SliderWidget", "CheckboxWidget", "LinkWidget",
            "ScrollPanelWidget", "ListPanelWidget", "InventorySlotWidget", "BlockIconWidget",
            "CraftingRecipeSlotWidget", "StarRatingWidget", "TextBoxWidget"
        };
        // 注意：面板/容器类型（FullInventoryWidget、CreativeInventoryWidget、ShortInventoryWidget、
        // ClothingWidget、ChestWidget、FurnaceWidget、ScrollPanelWidget、CraftingRecipeWidget 等）
        // 故意不列入可交互类型：它们只是容器，一旦列入，去重规则会吞掉整个子树，
        // 导致背包槽位等真正的可点控件无法被导出。

        // ---------------------------------------------------------------- 枚举范围（屏幕 + 活动对话框）

        /// <summary>按**文字**选控件的前缀：`text:OK` / `text:Cancel`（见 <see cref="Resolve"/>）。</summary>
        public const string TextSelectorPrefix = "text:";

        /// <summary>
        /// 当前全部可观察控件 = `RootWidget` 整棵树 + `ScreensManager.CurrentScreen` 自己的子树
        /// + `DialogsManager.Dialogs` 里**未挂在根树下**的对话框子树（按引用去重）。
        ///
        /// 为什么要显式接上"当前屏"：`ScreensManager.SwitchScreen()` **立即**更新 `CurrentScreen`，
        /// 但新屏要等转屏动画走到中段（`UpdateAnimation` 的 factor ≥ 0.5）才被
        /// `RootWidget.Children.Insert(0, ...)`（Source: Survivalcraft/Game/ScreensManager.cs:297-311）。
        /// 也就是说存在一个真实窗口：`status.screen` 已是目标屏，`ui` 却只看到旧屏的树。
        /// 接上 `CurrentScreen` 后这个窗口里也能拿到新屏控件与 path（`mounted:false` +
        /// `scope:"screen"`，点击报 `screen_not_mounted`），自动化不会把"还在转屏"误判成
        /// "这个屏没有 Cancel"。已挂载时 `seen` 会把整棵子树去重 → **现有路径与顺序完全不变**。
        ///
        /// 为什么要显式接上对话框列表：`obs.ui` 原来只走 `RootWidget.AllChildren`。
        /// 对话框挂在 `GuiWidget` 下时本来就在根树里；但**屏幕被切走**时（例如玩家从 HUD 打开
        /// 换肤屏/设置屏），整棵 `GameScreen` 会离开 `RootWidget`，而对话框仍在
        /// `DialogsManager.Dialogs` 里（父控件是已卸载的 `GuiWidget`）。这类对话框必须能被
        /// `ui` 报出来（`mounted:false`），否则自动化会以为"没有对话框"，随后把点击落到下面那层。
        /// 已挂载的对话框仍按原顺序出现在原位置，**现有屏幕的 path 与顺序完全不变**。
        /// </summary>
        public static List<Widget> EnumerateWidgets(ContainerWidget root)
        {
            var widgets = new List<Widget>();
            var seen = new HashSet<Widget>();
            if (root != null)
            {
                foreach (Widget widget in root.AllChildren)
                {
                    if (seen.Add(widget))
                        widgets.Add(widget);
                }
            }

            // 当前屏（转屏中段之前它还没被插进 RootWidget.Children）。已挂载时整棵子树
            // 都在 seen 里，这里一个也不会重复加入，顺序不变。
            Screen current = ScreensManager.CurrentScreen;
            if (current != null && !ReferenceEquals(current, root))
            {
                if (seen.Add(current))
                    widgets.Add(current);
                foreach (Widget widget in current.AllChildren)
                {
                    if (seen.Add(widget))
                        widgets.Add(widget);
                }
            }

            ReadOnlyList<Dialog> dialogs = DialogsManager.Dialogs;
            for (int i = 0; i < dialogs.Count; i++)
            {
                Dialog dialog = dialogs[i];
                if (dialog == null || !seen.Add(dialog))
                    continue;
                widgets.Add(dialog);
                foreach (Widget widget in dialog.AllChildren)
                {
                    if (seen.Add(widget))
                        widgets.Add(widget);
                }
            }
            return widgets;
        }

        /// <summary>控件是否在当前屏幕树里（= 会被布局/绘制，因而可能被点到）。</summary>
        public static bool IsUnderRoot(Widget widget)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            for (Widget current = widget; current != null; current = current.ParentWidget)
            {
                if (ReferenceEquals(current, root))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 控件是否属于 `ScreensManager.CurrentScreen` 的子树 —— 已挂载与"转屏中段之前还没挂载"
        /// 两种都算。后者只用于让 `ui` 报告即将出现的控件：点击必须明确失败
        /// （`screen_not_mounted`），绝不拿旧布局的坐标点到下面那层。
        /// </summary>
        public static bool IsUnderCurrentScreen(Widget widget)
        {
            Screen current = ScreensManager.CurrentScreen;
            if (current == null || widget == null)
                return false;
            for (Widget w = widget; w != null; w = w.ParentWidget)
            {
                if (ReferenceEquals(w, current))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 控件归属，写进元素的 `scope` 字段，也决定"未挂载"时用哪个错误码：
        ///   · `root`   —— 挂在 `ScreensManager.RootWidget` 下（正常绘制、可点）；
        ///   · `screen` —— 属于当前屏但还没进根树（转屏动画中段之前：未绘制、不可点）；
        ///   · `dialog` —— 属于活动但未挂载的对话框（父屏已切走：未绘制、不可点）；
        ///   · `orphan` —— 都不属于（例如已销毁屏的残留子树）。
        /// </summary>
        public static string ScopeOf(Widget widget)
        {
            if (widget == null)
                return null;
            if (IsUnderRoot(widget))
                return "root";
            if (IsUnderCurrentScreen(widget))
                return "screen";
            if (FindAncestorDialog(widget) != null)
                return "dialog";
            return "orphan";
        }

        /// <summary>
        /// 控件若"属于当前界面但还没挂载"（转屏动画中段之前的当前屏，或父屏已切走的活动对话框），
        /// 点击会落到它下面那一层 —— 必须**明确失败**，并说清是哪种情况。
        /// `selector` 只用于错误消息，便于调用方知道是哪个目标。
        /// </summary>
        public static void RequireMounted(Widget widget, string selector)
        {
            if (widget == null || IsUnderRoot(widget))
                return;
            if (IsUnderCurrentScreen(widget))
            {
                throw new BridgeCommandException("screen_not_mounted",
                    "Target '" + (selector ?? "?") + "' is inside the current screen '"
                    + (ScreenName() ?? "?") + "', but the screen switch has not mounted it yet"
                    + " (path: " + BuildPath(widget) + "). ScreensManager inserts the incoming"
                    + " screen into RootWidget only at the midpoint of the transition animation"
                    + " (Survivalcraft/Game/ScreensManager.cs:297-311), so the target is neither"
                    + " drawn nor clickable yet; wait for 'screen.animating.false'"
                    + " (or 'screen.is:" + (ScreenName() ?? "<name>") + "'), then click.");
            }
            throw new BridgeCommandException("dialog_not_mounted",
                "Target '" + (selector ?? "?") + "' is inside an active dialog ("
                + (FindAncestorDialog(widget)?.GetType().Name ?? "unknown")
                + ") that is not mounted on the current screen (path: " + BuildPath(widget)
                + "). Its parent screen was switched away, so it is neither drawn nor clickable;"
                + " switch back to that screen, then click, or close it with act.closeui.");
        }

        /// <summary>当前活动的对话框（含已挂在屏幕上的与未挂载的）。</summary>
        public static List<Dictionary<string, object>> DescribeActiveDialogs()
        {
            var list = new List<Dictionary<string, object>>();
            ReadOnlyList<Dialog> dialogs = DialogsManager.Dialogs;
            for (int i = 0; i < dialogs.Count; i++)
            {
                Dialog dialog = dialogs[i];
                if (dialog == null)
                    continue;
                bool mounted = IsUnderRoot(dialog);
                list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = dialog.GetType().Name,
                    ["name"] = dialog.Name,
                    ["path"] = BuildPath(dialog),
                    ["mounted"] = mounted,
                    ["visible"] = dialog.IsVisibleGlobal,
                    ["parent"] = dialog.ParentWidget != null ? ShortName(dialog.ParentWidget) : null
                });
            }
            return list;
        }

        // ---------------------------------------------------------------- 导出

        public static Dictionary<string, object> Describe(
            CmdBridgeConfig config, bool includeAll, int maxElements, string filter,
            int? gameWidget = null)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (root == null)
            {
                result["elements"] = new List<Dictionary<string, object>>();
                result["totalCount"] = 0;
                result["truncated"] = false;
                result["ready"] = false;
                result["dialogs"] = DescribeActiveDialogs();
                result["views"] = new List<Dictionary<string, object>>();
                result["primaryGameWidget"] = null;
                return result;
            }

            List<Dictionary<string, object>> elements = Collect(
                EnumerateWidgets(root), includeAll, maxElements, filter, gameWidget,
                out int total, out bool truncated);

            result["elements"] = elements;
            result["totalCount"] = total;
            result["truncated"] = truncated;
            result["ready"] = true;
            result["shown"] = elements.Count;
            // 对话框摘要：即使某个对话框的控件树因为布局/时序原因没有被枚举到，
            // 调用方也能从这一块看出"现在开着什么对话框"（并且能拿到它的 path）。
            result["dialogs"] = DescribeActiveDialogs();
            // 分屏视图摘要：每个视图占屏幕哪块、输入面能不能用鼠标、谁是主视图。
            // 同名元素在各视图各有一份（`MoreButton` / `SuMultiplayerRoomButton` …），
            // 元素上的 `gameWidget` 字段指的就是这里的槽位号。
            result["views"] = DescribeViews();
            int primary = PrimaryGameWidgetIndex();
            result["primaryGameWidget"] = primary == UiSplitView.NoView ? (int?)null : primary;
            return result;
        }

        private static List<Dictionary<string, object>> Collect(
            List<Widget> widgets,
            bool includeAll,
            int maxElements,
            string filter,
            int? gameWidget,
            out int total,
            out bool truncated)
        {
            int limit = maxElements > 0 ? maxElements : 512;
            var elements = new List<Dictionary<string, object>>();
            total = 0;
            truncated = false;
            int id = 0;

            foreach (Widget widget in widgets)
            {
                bool interactive = IsInteractive(widget, out bool hasInteractiveAncestor);
                if (!includeAll && (!interactive || hasInteractiveAncestor))
                    continue;

                if (gameWidget.HasValue
                    && GameWidgetIndexOf(widget) != gameWidget.Value)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(filter) && !MatchesFilter(widget, filter))
                    continue;

                total++;
                if (elements.Count >= limit)
                {
                    truncated = true;
                    continue;
                }

                id++;
                Dictionary<string, object> element = DescribeElement(widget, id);
                if (!includeAll)
                    element["interactive"] = true;
                elements.Add(element);
            }

            return elements;
        }

        public static Dictionary<string, object> DescribeElement(Widget widget, int id)
        {
            var element = new Dictionary<string, object>(StringComparer.Ordinal);
            BoundingRectangle bounds = widget.GlobalBounds;
            Vector2 center = new Vector2(
                (bounds.Min.X + bounds.Max.X) * 0.5f,
                (bounds.Min.Y + bounds.Max.Y) * 0.5f);
            float width = bounds.Max.X - bounds.Min.X;
            float height = bounds.Max.Y - bounds.Min.Y;

            // 有没有被布局过：判据只有"矩形非退化"。`StackPanelWidget` 测量/排布时**跳过**不可见的
            // 子控件（Source: Survivalcraft/Game/StackPanelWidget.cs:22,80），于是被折叠的面板
            // （`MoreContents` 二级收起时）里的控件从来没被 `Widget.Arrange` 过：
            // `GlobalBounds` 恒为 (0,0,0,0)。旧实现把它的"中心"当成 (0,0) 报出去，
            // 消费者照着那个像素点，命中的是屏幕左上角**另一个视图**的 `ViewWidget`
            // —— 这就是 `clientPoint=0,0 / hittable=no (ViewWidget(View))` 的真实来源。
            // 没有矩形就是没有可点的位置：如实报 null + 原因，绝不报 (0,0)。
            bool laidOut = width > 0.001f && height > 0.001f;
            // 可见性（含祖先）与"谁把它藏了"。隐藏的元素**没有遮挡者**：它自己没被画，
            // 报一个 ViewWidget 当遮挡者只会把排查带偏（实测：H2/H3 的 HUD 整条是隐藏的）。
            bool hidden = !widget.IsVisibleGlobal;

            ContainerWidget root = ScreensManager.RootWidget;
            bool mounted = IsUnderRoot(widget);
            string scope = ScopeOf(widget);
            GameWidget gameWidget = FindGameWidget(widget);
            // 未挂载的子树（转屏中段之前的当前屏 / 父屏已切走的对话框）不在 RootWidget 里：
            // 对它的中心做根树命中只会命中"下面那层"，那不是它的可点性，必须报 false
            // （并把原因写进 clickReason）。
            Widget hit = mounted && laidOut && root != null ? root.HitTestGlobal(center) : null;
            bool hittable = mounted && laidOut && IsSelfOrDescendant(hit, widget);
            // 同一个点在**它自己的视图**里会被谁接住。多视图下全局命中可能是别的视图的
            // `ViewWidget`，只有按归属视图重走一遍才能回答"这个视图认不认这个像素"。
            Widget viewHit = mounted && laidOut && gameWidget != null
                ? HitTestInGameWidget(gameWidget, center)
                : null;
            bool hittableInView = mounted && laidOut && gameWidget != null
                && IsSelfOrDescendant(viewHit, widget);

            element["id"] = id;
            element["path"] = BuildPath(widget);
            element["name"] = widget.Name;
            element["type"] = widget.GetType().Name;
            element["text"] = GetText(widget);
            element["dialog"] = FindAncestorDialog(widget)?.GetType().Name;
            // 分屏槽位（H1=0/H2=1/H3=2…，`GameWidget.GameWidgetIndex`）。不在任何视图里（菜单/对话框）
            // 报 null；每个视图的矩形/布局/输入设备见 `obs.ui` 的 `views` 块。
            element["gameWidget"] = gameWidget != null ? gameWidget.GameWidgetIndex : (int?)null;
            element["mounted"] = mounted;
            element["scope"] = scope;
            element["rect"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = bounds.Min.X,
                ["y"] = bounds.Min.Y,
                ["w"] = width,
                ["h"] = height
            };
            element["center"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = center.X,
                ["y"] = center.Y
            };
            element["laidOut"] = laidOut;
            element["hiddenBy"] = hidden ? ShortName(FindHiddenAncestor(widget)) : null;
            element["clientPoint"] = laidOut ? element["center"] : null;
            Vector2? screenPoint = laidOut ? ToScreenPoint(center) : null;
            element["screenPoint"] = screenPoint.HasValue
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = screenPoint.Value.X,
                    ["y"] = screenPoint.Value.Y
                }
                : null;
            element["visible"] = widget.IsVisibleGlobal;
            element["enabled"] = widget.IsEnabledGlobal;
            element["hitTestVisible"] = widget.IsHitTestVisible;
            element["hittable"] = hittable;
            element["hitTargetType"] = hit != null ? hit.GetType().Name : null;
            // 没布局过 / 被隐藏都**没有遮挡者**：报一个 `ViewWidget` 只会把人往"被别的东西挡住"上带。
            element["blockedBy"] = hittable || !laidOut || hidden ? null : ShortName(hit);
            element["hittableInView"] = hittableInView;
            element["hitTargetInView"] = viewHit != null ? ShortName(viewHit) : null;

            // hittable 只说明"几何上命中得到"，不代表游戏真会派生一次点击。有两道**引擎硬门**：
            //   ① 该层输入面的 `Devices` 必须含 Mouse/MultiMice —— 没有鼠标设备时
            //      `IsMouseCursorVisible` 的 getter 恒为 false（WidgetInput.cs:152-166），
            //      于是 `Press/Tap/Click` 的派生整段被门控（WidgetInput.cs:741）。
            //      本地分屏里 H2/H3 的 GameWidget 设备是 GamePad/Touch（`GameWidget.DetermineInputDevices`
            //      `GameWidget.cs:122-162`），**鼠标点击在它们身上永远派生不出来**；
            //   ② 即便有鼠标设备，世界内 ComponentInput 每帧还会把该层 `IsMouseCursorVisible` 置 false
            //      （鼠标交给视角，ComponentInput.cs:155），打开模态面板/对话框后才恢复（:143-152）。
            WidgetInput elementInput = widget.Input;
            bool mouseDevice = HasMouseDevice(elementInput);
            bool mouseDerivable = elementInput != null && elementInput.IsMouseCursorVisible;
            element["inputDevices"] = elementInput != null ? elementInput.Devices.ToString() : null;
            element["mouseClickable"] = hittable && mouseDevice && mouseDerivable;
            element["clickable"] = hittable && mouseDerivable;
            element["clickReason"] = hittable
                ? (mouseDerivable
                    ? null
                    : (mouseDevice
                        ? "mouse cursor is captured (IsMouseCursorVisible=false); use keys instead"
                        : "this element's input surface has no mouse device (devices="
                          + (elementInput != null ? elementInput.Devices.ToString() : "<none>")
                          + "), and the engine only derives a mouse Click for Mouse/MultiMice inputs"
                          + " (Survivalcraft/Game/WidgetInput.cs:152-166,741); use --gamewidget with"
                          + " the view that owns the mouse, or drive it with keys/touch"))
                : (!laidOut
                    ? ExplainNotLaidOut(widget, gameWidget)
                    : (hidden
                        ? ExplainHidden(widget, gameWidget)
                        : (scope == "screen"
                        ? "the current screen has not been mounted yet (screen switch animation is"
                          + " still before the midpoint); it is not drawn and cannot be clicked"
                          + " - wait for 'screen.animating.false'"
                            : (!mounted
                                ? "active dialog is not mounted on the current screen (its parent screen was"
                                  + " switched away); it is not drawn and cannot be clicked"
                                : ExplainUnhittable(widget, hit, bounds, center, root)))));

            if (widget is ListPanelWidget listPanel)
                element["list"] = DescribeList(listPanel);
            if (widget is ButtonWidget button)
            {
                element["checked"] = button.IsChecked;
                // `clicked` 是**本帧**的点击边沿（ClickableWidget.Update 每帧重算）。
                // 排查"点了切屏后按钮一直像被按住"这类问题时，它是唯一能直接看到的证据：
                // 切屏会把 RootWidget.IsUpdateEnabled 置 false → 控件树停止 Update →
                // clicked 冻结为 true（实机 1.1.33：SK 按钮就是这样每帧重开换肤屏的）。
                element["clicked"] = button.IsClicked;
            }
            if (widget is ClickableWidget clickable)
            {
                element["checked"] = clickable.IsChecked;
                element["clicked"] = clickable.IsClicked;
            }
            if (widget is SliderWidget slider)
                element["value"] = slider.Value;

            return element;
        }

        /// <summary>
        /// "为什么这个元素点不到"要说人话。用户实测踩到的正是这一类：
        /// 世界内 HUD 那条控制栏在 **Windows（鼠标键盘）下是触屏专用**的
        /// （`ComponentGui.UpdateSidePanelsAnimation`：非触屏且没有模态面板时 `m_sidePanelsFactor=1`，
        /// 整条栏被 `RenderTransform` 平移到屏幕外），于是 `MoreButton` 的坐标是 (2852, 55) ——
        /// 屏幕外。以前只写一句 "blocked by "（遮挡者名字还是空的），
        /// 用户看到的就是"点了没反应"，查起来毫无线索。
        /// </summary>
        public static string ExplainUnhittable(Widget widget, Widget hit, BoundingRectangle bounds,
            Vector2 center, ContainerWidget root)
        {
            float width = bounds.Max.X - bounds.Min.X;
            float height = bounds.Max.Y - bounds.Min.Y;
            if (width <= 0.001f || height <= 0.001f)
            {
                return "element has zero size (not laid out yet, or its panel is collapsed)";
            }

            if (root != null && !root.GlobalBounds.Contains(center))
            {
                return "off-screen: center (" + center.X.ToString("0.#") + "," + center.Y.ToString("0.#")
                    + ") is outside the " + DescribeRect(root.GlobalBounds)
                    + " screen area - a hidden/collapsed panel"
                    + " (on Windows the in-game HUD control bars are touch-only)";
            }

            if (hit == null)
                return "nothing is hit at its center (another screen is on top?)";
            return "blocked by " + ShortName(hit);
        }

        private static string DescribeRect(BoundingRectangle bounds)
        {
            return (bounds.Max.X - bounds.Min.X).ToString("0") + "x"
                + (bounds.Max.Y - bounds.Min.Y).ToString("0");
        }

        /// <summary>
        /// "为什么这个元素没有坐标"要说人话：没布局过（矩形恒为零）时**不是**被遮挡，
        /// 而是它（或它的某个祖先）不可见，面板测量/排布整段跳过了它
        /// （Source: `Survivalcraft/Game/StackPanelWidget.cs:22,80`）。
        /// 典型现场：`MoreContents` 二级收起时它里面的 `SuMultiplayerRoomButton`、以及
        /// 非本地主视图里整套 HUD。
        /// </summary>
        public static string ExplainNotLaidOut(Widget widget, GameWidget gameWidget)
        {
            Widget hidden = FindHiddenAncestor(widget);
            string reason = hidden != null
                ? " because '" + ShortName(hidden) + "' is invisible (IsVisible=false)"
                : " (its panel has not been measured yet)";
            return "not laid out" + ViewSuffix(gameWidget) + ": no rectangle has been computed for it"
                + reason
                + "; StackPanelWidget skips invisible children when measuring/arranging"
                + " (Survivalcraft/Game/StackPanelWidget.cs:22,80), so it has no position to click"
                + " and clientPoint is null - it is NOT occluded by anything."
                + MoreContentsSuffix(widget)
                + ViewHint(gameWidget);
        }

        /// <summary>
        /// "为什么这个元素点不到"的第二类：矩形是**存在的**（可能是隐藏前留下的旧矩形），
        /// 但它（或祖先）现在不可见 —— 没画出来就没有可点的像素，同样不是被遮挡。
        /// 典型现场：非本地主视图的整条 HUD（`ControlsContainer.IsVisible=false`）。
        /// </summary>
        public static string ExplainHidden(Widget widget, GameWidget gameWidget)
        {
            Widget hidden = FindHiddenAncestor(widget);
            return "hidden" + ViewSuffix(gameWidget) + ": '"
                + (hidden != null ? ShortName(hidden) : "an ancestor") + "' is invisible"
                + " (IsVisible=false), so this element is not drawn and cannot be clicked;"
                + " its rect is the stale geometry from when it was laid out."
                + MoreContentsSuffix(widget)
                + ViewHint(gameWidget);
        }

        private static string ViewSuffix(GameWidget gameWidget)
        {
            return gameWidget != null ? " (gameWidget=" + gameWidget.GameWidgetIndex + ")" : string.Empty;
        }

        private static string MoreContentsSuffix(Widget widget)
        {
            return IsInMoreContentsChain(widget)
                ? " It hangs inside the " + MoreContentsName + " second level: click "
                  + MoreToggleName + " (in the same view) to expand it first."
                : string.Empty;
        }

        private static string ViewHint(GameWidget gameWidget)
        {
            return gameWidget != null
                ? " Every split-screen view has its own copy of this element: pass --gamewidget "
                  + gameWidget.GameWidgetIndex + " (or another view's index) to target a specific"
                  + " one; 'obs.ui' reports 'gameWidget' per element and the view list under 'views'."
                : string.Empty;
        }

        /// <summary>祖先链（含自己）里第一个 `IsVisible == false` 的控件 —— 没布局的**直接原因**。</summary>
        public static Widget FindHiddenAncestor(Widget widget)
        {
            for (Widget current = widget; current != null; current = current.ParentWidget)
            {
                if (!current.IsVisible)
                    return current;
            }
            return null;
        }

        /// <summary>是否挂在 `MoreContents` 这条二级开关链上（不看可见性；名字与位置都要对上）。</summary>
        private static bool IsInMoreContentsChain(Widget widget)
        {
            Widget moreContents;
            return TryGetMoreContentsAncestor(widget, out moreContents)
                || (widget != null && string.Equals(widget.Name, MoreContentsName, StringComparison.Ordinal));
        }

        // ---------------------------------------------------------------- 选择器解析

        /// <summary>
        /// 把选择器解析成控件。支持：
        ///   · 完整 path（`BuildPath` 生成，含未挂载对话框的 `dialog:<Type>/...`）；
        ///   · 唯一 Name；唯一文本；`text:<文字>`（按按钮/标签文字，先精确后包含）；
        ///   · 当前枚举序号。
        /// 范围 = 屏幕树 + 活动对话框树（<see cref="EnumerateWidgets"/>）。
        /// 找不到 → element_missing；多个匹配 → ambiguous_selector（绝不猜，错误里给出候选 path）。
        /// </summary>
        public static Widget Resolve(
            ContainerWidget root, string selector, bool hasPoint, Vector2 point,
            int? gameWidget = null)
        {
            if (root == null)
                throw new BridgeCommandException("not_ready", "The UI is not ready yet.");
            if (string.IsNullOrWhiteSpace(selector))
            {
                throw new BridgeCommandException(
                    "invalid_argument", "An element selector is required (path, name, text or id).");
            }

            RequireKnownGameWidget(gameWidget);

            string wanted = selector.Trim();
            string textWanted = null;
            bool textOnly = wanted.StartsWith(TextSelectorPrefix, StringComparison.OrdinalIgnoreCase);
            if (textOnly)
            {
                textWanted = wanted.Substring(TextSelectorPrefix.Length).Trim();
                if (textWanted.Length == 0)
                {
                    throw new BridgeCommandException("invalid_argument",
                        "A text selector must look like 'text:<button text>' (e.g. text:OK).");
                }
            }

            List<Widget> all = EnumerateWidgets(root);

            var exactPaths = new List<Widget>();
            var names = new List<Widget>();
            var exactTexts = new List<Widget>();
            var partialTexts = new List<Widget>();
            for (int i = 0; i < all.Count; i++)
            {
                Widget widget = all[i];
                if (textOnly)
                {
                    string label = GetText(widget);
                    if (string.IsNullOrEmpty(label))
                        continue;
                    if (string.Equals(label, textWanted, StringComparison.OrdinalIgnoreCase))
                        exactTexts.Add(widget);
                    else if (label.IndexOf(textWanted, StringComparison.OrdinalIgnoreCase) >= 0)
                        partialTexts.Add(widget);
                    continue;
                }

                if (string.Equals(BuildPath(widget), wanted, StringComparison.OrdinalIgnoreCase))
                    exactPaths.Add(widget);
                if (!string.IsNullOrEmpty(widget.Name) &&
                    string.Equals(widget.Name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(widget);
                }
                string text = GetText(widget);
                if (!string.IsNullOrEmpty(text) &&
                    string.Equals(text, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    exactTexts.Add(widget);
                }
            }

            List<Widget> chosen;
            if (textOnly)
                chosen = exactTexts.Count > 0 ? exactTexts : partialTexts;
            else if (exactPaths.Count > 0)
                chosen = exactPaths;
            else if (names.Count > 0)
                chosen = names;
            else if (exactTexts.Count > 0)
                chosen = exactTexts;
            else if (int.TryParse(wanted, out int index) && index >= 1)
            {
                // 序号与 obs.ui 的默认列表同口径（已做过父子去重的可交互元素）。
                var listed = new List<Widget>();
                for (int i = 0; i < all.Count; i++)
                {
                    Widget widget = all[i];
                    if (!IsInteractive(widget, out bool hasInteractiveAncestor) || hasInteractiveAncestor)
                        continue;
                    listed.Add(widget);
                }
                chosen = index <= listed.Count
                    ? new List<Widget> { listed[index - 1] }
                    : new List<Widget>();
            }
            else
                chosen = new List<Widget>();

            // 显式指定分屏视图（`--gamewidget N`）：只在这个视图里找。
            // 名字在每个视图里都有一份（`MoreButton` / `SuContent...` 这类按名字取的 HUD 按钮），
            // 指定了视图还落到别的视图上就是错；这里只做过滤，找不到就**明确报**它在哪些视图里。
            if (gameWidget.HasValue && chosen.Count > 0)
            {
                var inView = new List<Widget>();
                for (int i = 0; i < chosen.Count; i++)
                {
                    if (GameWidgetIndexOf(chosen[i]) == gameWidget.Value)
                        inView.Add(chosen[i]);
                }
                if (inView.Count == 0)
                {
                    throw new BridgeCommandException("element_missing",
                        "'" + wanted + "' does not exist in gameWidget " + gameWidget.Value
                        + " (its split-screen slot); it matches " + chosen.Count + " element(s) in "
                        + DescribeViewsOf(chosen) + ". See 'obs.ui' -> views[] for the available"
                        + " slots (index/rect/inputDevices).");
                }
                chosen = inView;
            }

            // 同名同型兄弟（例如快捷栏槽位）路径仍然相同，用调用方给出的坐标消歧。
            if (chosen.Count > 1 && hasPoint)
            {
                var inside = new List<Widget>();
                for (int i = 0; i < chosen.Count; i++)
                {
                    BoundingRectangle bounds = chosen[i].GlobalBounds;
                    if (point.X >= bounds.Min.X && point.X <= bounds.Max.X &&
                        point.Y >= bounds.Min.Y && point.Y <= bounds.Max.Y)
                    {
                        inside.Add(chosen[i]);
                    }
                }
                if (inside.Count > 0)
                    chosen = inside;
            }

            // `text:` 的重复命中是**结构性的**，不是真的歧义：`BevelledButtonWidget.Text` 直接
            // 代理它内部那个 `BevelledButton.Label`（Source: Survivalcraft/Game/BevelledButtonWidget.cs:
            // 43-52），于是同一个按钮的"按钮控件"和"里面的标签"文字完全相同 ——
            // 实测 `text:SK` / `text:Cancel` 都会命中 2 个，直接报 ambiguous，按文字点按钮根本用不了。
            // 消歧规则（**不是猜**）：只保留 `obs.ui` 默认列表口径里那个"真正的可交互控件"
            // （自身是可交互类型、且祖先链上没有另一个可交互控件）。装饰性内层标签会被排除；
            // 若排除后仍有多个（例如两个不同按钮同文字）→ 照旧报 ambiguous_selector 并列出 path。
            if (chosen.Count > 1)
            {
                var interactive = new List<Widget>();
                for (int i = 0; i < chosen.Count; i++)
                {
                    Widget candidate = chosen[i];
                    if (IsInteractive(candidate, out bool hasInteractiveAncestor) && !hasInteractiveAncestor)
                        interactive.Add(candidate);
                }
                if (interactive.Count > 0)
                    chosen = interactive;
            }

            // 分屏：同一个名字（`MoreButton`、联机 Mod 的 CR/TA/MP 按钮…）在每个视图里各有一份。
            // 没有显式 `--gamewidget` 时按**主视图**唯一化（规则见 `UiSplitView.SelectPrimaryView`：
            // 接受鼠标的最小槽位，没有就取最小槽位；引擎里只有鼠标视图才派生得出 Click）。
            // 单视图/全部同一视图 → `SelectInstanceIndex` 返回 -1，行为与以前完全一致。
            if (chosen.Count > 1 && !gameWidget.HasValue)
            {
                var viewIds = new int[chosen.Count];
                for (int i = 0; i < chosen.Count; i++)
                    viewIds[i] = GameWidgetIndexOf(chosen[i]) ?? -1;
                int pick = UiSplitView.SelectInstanceIndex(viewIds, PrimaryGameWidgetIndex());
                if (pick >= 0)
                    chosen = new List<Widget> { chosen[pick] };
            }

            if (chosen.Count == 0)
            {
                throw new BridgeCommandException(
                    "element_missing",
                    "No element matches '" + wanted
                    + "' on the current screen or in an active dialog.");
            }
            if (chosen.Count > 1)
            {
                var candidates = new List<string>();
                for (int i = 0; i < chosen.Count && i < 4; i++)
                {
                    int? view = GameWidgetIndexOf(chosen[i]);
                    candidates.Add(BuildPath(chosen[i])
                        + (view.HasValue ? " (gameWidget " + view.Value + ")" : string.Empty));
                }
                throw new BridgeCommandException(
                    "ambiguous_selector",
                    "'" + wanted + "' matches " + chosen.Count + " elements ("
                    + string.Join(", ", candidates.ToArray())
                    + "); use the full path, a text: selector, or --gamewidget <N> to pick a"
                    + " split-screen view (" + DescribeViewsOf(chosen) + ").");
            }

            return chosen[0];
        }

        /// <summary>
        /// 显式 `--gamewidget N` 必须先存在：不在世界里（菜单）或没有这个槽位时**明确报错**，
        /// 绝不悄悄退回"随便找一个视图"。
        /// </summary>
        public static void RequireKnownGameWidget(int? gameWidget)
        {
            if (!gameWidget.HasValue)
                return;
            List<GameWidget> views = CurrentGameWidgets();
            if (views.Count == 0)
            {
                throw new BridgeCommandException("element_missing",
                    "gameWidget " + gameWidget.Value + " does not exist: there is no world loaded"
                    + " (split-screen views only exist inside the world's GameScreen).");
            }
            var indices = new List<string>();
            for (int i = 0; i < views.Count; i++)
            {
                indices.Add(views[i].GameWidgetIndex.ToString());
                if (views[i].GameWidgetIndex == gameWidget.Value)
                    return;
            }
            throw new BridgeCommandException("element_missing",
                "gameWidget " + gameWidget.Value + " does not exist; the loaded split-screen slots"
                + " are " + string.Join(", ", indices.ToArray())
                + " (see 'obs.ui' -> views[]).");
        }

        /// <summary>候选元素分布在哪些分屏视图里（错误消息用；不属于任何视图的记作 screen）。</summary>
        private static string DescribeViewsOf(List<Widget> widgets)
        {
            var viewTexts = new List<string>();
            for (int i = 0; i < widgets.Count; i++)
            {
                int? view = GameWidgetIndexOf(widgets[i]);
                string text = view.HasValue ? "gameWidget " + view.Value : "screen";
                if (!viewTexts.Contains(text))
                    viewTexts.Add(text);
            }
            return "found in: " + (viewTexts.Count > 0
                ? string.Join(", ", viewTexts.ToArray())
                : "nothing");
        }

        // ---------------------------------------------------------------- 可达性报告

        /// <summary>
        /// 可达性报告（`ui.reachability`）：问的是"**现在真的点得到吗**"，所以只扫
        /// `RootWidget` 里**已挂载**的那棵树。转屏中段之前的当前屏（<see cref="EnumerateWidgets"/>
        /// 会在 `ui` 里如实列出、标 `mounted:false` / `scope:"screen"`）不参与可达性统计 ——
        /// 它还没被布局/绘制，报成"可点"才是错的。
        /// </summary>
        public static Dictionary<string, object> Reachability(CmdBridgeConfig config)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (root == null)
            {
                result["ready"] = false;
                return result;
            }

            var reachable = new List<string>();
            var blocked = new List<Dictionary<string, object>>();
            var hidden = new List<string>();
            int total = 0;
            int limit = config.MaxElements;

            foreach (Widget widget in root.AllChildren)
            {
                if (!IsInteractive(widget, out bool hasInteractiveAncestor) || hasInteractiveAncestor)
                    continue;
                if (total >= limit)
                    break;
                total++;

                string path = BuildPath(widget);
                if (!widget.IsVisibleGlobal || !widget.IsEnabledGlobal)
                {
                    hidden.Add(path);
                    continue;
                }

                BoundingRectangle bounds = widget.GlobalBounds;
                Vector2 center = new Vector2(
                    (bounds.Min.X + bounds.Max.X) * 0.5f,
                    (bounds.Min.Y + bounds.Max.Y) * 0.5f);
                bool laidOut = bounds.Max.X - bounds.Min.X > 0.001f
                    && bounds.Max.Y - bounds.Min.Y > 0.001f;
                Widget hit = laidOut ? root.HitTestGlobal(center) : null;
                if (laidOut && IsSelfOrDescendant(hit, widget))
                {
                    reachable.Add(path);
                }
                else
                {
                    blocked.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["path"] = path,
                        ["type"] = widget.GetType().Name,
                        ["text"] = GetText(widget),
                        ["gameWidget"] = GameWidgetIndexOf(widget),
                        ["laidOut"] = laidOut,
                        // 没布局过就**没有遮挡者**：以前这里报 `blockedBy: ViewWidget(View)`，
                        // 把人往"被别的东西挡住"上带（实际是被折叠面板跳过了测量/排布）。
                        ["blockedBy"] = laidOut ? ShortName(hit) : null,
                        ["reason"] = laidOut
                            ? null
                            : ExplainNotLaidOut(widget, FindGameWidget(widget)),
                        // 不要在这里塞裸的 Vector2：Engine.Vector2 有个 `YX` 属性（返回新的 Vector2），
                        // System.Text.Json 会顺着 YX.YX.YX… 一路递归到"检测到循环"并抛异常。
                        // （实测：这条命令因未处理异常直接把游戏进程干掉了。）只放 {x,y} 两个数字。
                        ["screenPoint"] = laidOut ? PointToValue(ToScreenPoint(center)) : null
                    });
                }
            }

            result["ready"] = true;
            result["screen"] = ScreenName();
            result["animating"] = ScreensManager.IsAnimating;
            result["totalInteractive"] = total;
            result["reachable"] = reachable;
            result["blocked"] = blocked;
            result["hidden"] = hidden;
            result["hint"] = "Hidden or blocked elements are still driven by keyboard input; " +
                "keys are never gated by UI visibility.";
            return result;
        }

        // ---------------------------------------------------------------- 工具

        public static string BuildPath(Widget widget)
        {
            if (IsUnderRoot(widget))
            {
                // 现有屏幕路径：完全保持原样（从 RootWidget 的子级开始，不含 RootWidget 本身）。
                var parts = new List<string>();
                for (Widget current = widget; current != null; current = current.ParentWidget)
                {
                    if (ReferenceEquals(current, ScreensManager.RootWidget))
                        break;
                    parts.Add(Segment(current));
                }
                parts.Reverse();
                return string.Join("/", parts.ToArray());
            }

            // 不在 RootWidget 里 = 活动但未挂载的对话框子树（父屏幕已被切走），
            // 或**还没走完转屏动画的当前屏**。两者都要给一个不与屏幕路径冲突的根，
            // 这样同一条 `ui` 输出里的 path 可以直接回喂给 ui.locate / act.uiclick ——
            // 它们会明确报 dialog_not_mounted / screen_not_mounted，而不是点到下面那层。
            Dialog dialog = FindAncestorDialog(widget);
            if (dialog == null)
            {
                Screen pendingScreen = ScreensManager.CurrentScreen;
                if (pendingScreen != null && IsSelfOrDescendant(widget, pendingScreen))
                {
                    var screenInner = new List<string>();
                    for (Widget w = widget; w != null && !ReferenceEquals(w, pendingScreen);
                         w = w.ParentWidget)
                    {
                        screenInner.Add(Segment(w));
                    }
                    screenInner.Reverse();
                    string screenPrefix = "screen:"
                        + (ScreenName() ?? pendingScreen.GetType().Name);
                    return screenInner.Count > 0
                        ? screenPrefix + "/" + string.Join("/", screenInner.ToArray())
                        : screenPrefix;
                }

                var orphan = new List<string>();
                for (Widget current = widget; current != null; current = current.ParentWidget)
                    orphan.Add(Segment(current));
                orphan.Reverse();
                return string.Join("/", orphan.ToArray());
            }

            var inner = new List<string>();
            for (Widget current = widget; current != null && !ReferenceEquals(current, dialog);
                 current = current.ParentWidget)
            {
                inner.Add(Segment(current));
            }
            inner.Reverse();
            string prefix = "dialog:" + dialog.GetType().Name;
            return inner.Count > 0
                ? prefix + "/" + string.Join("/", inner.ToArray())
                : prefix;
        }

        internal static Dialog FindAncestorDialog(Widget widget)
        {
            for (Widget current = widget; current != null; current = current.ParentWidget)
            {
                if (current is Dialog dialog)
                    return dialog;
            }
            return null;
        }

        /// <summary>
        /// 路径段：有名字用名字；没有名字则带"父级子索引"，否则同类型兄弟会产生完全相同的路径
        /// （例如快捷栏 8 个 InventorySlotWidget 全都没有 Name），导致无法寻址。
        /// </summary>
        private static string Segment(Widget widget)
        {
            string name = widget.Name;
            if (!string.IsNullOrEmpty(name))
                return name;
            if (widget.ParentWidget is ContainerWidget parent)
            {
                int index = parent.Children.IndexOf(widget);
                if (index >= 0)
                    return "[" + widget.GetType().Name + "#" + index + "]";
            }
            return "[" + widget.GetType().Name + "]";
        }

        /// <summary>控件矩形的中心（客户区像素）。</summary>
        public static Vector2 CenterOf(Widget widget)
        {
            BoundingRectangle bounds = widget.GlobalBounds;
            return new Vector2(
                (bounds.Min.X + bounds.Max.X) * 0.5f,
                (bounds.Min.Y + bounds.Max.Y) * 0.5f);
        }

        /// <summary>
        /// 列表里某一行的**当前**客户区坐标（按序号或文字找行）。
        ///
        /// 为什么要它：列表行不是按钮，录动作包时只能记坐标 —— 可窗口一改大小、UI 一缩放，
        /// 那个像素就不再是同一行了（实测：改过窗口之后"进入游戏"的第二步点不动）。
        /// 记"哪个列表的第几行 / 哪一行文字"，回放时用这个函数**现算坐标**，就与窗口尺寸无关。
        ///
        /// **优先用列表真正的项控件**（`ListPanelWidget` 会为每个可见项建一个控件，
        /// `ArrangeOverride` 把它们依次排进 `Children`）：那才是屏幕上真实的位置。
        /// 只有拿不到项控件时才退回"按 ItemSize/ScrollPosition 算"的经验公式（老做法，
        /// 实测在某些列表上算出来的点根本不在控件上）。
        /// </summary>
        public static bool TryResolveListRow(Widget widget, int rowIndex, string rowText,
            out Vector2 clientPoint, out int resolvedIndex, out string resolvedText)
        {
            clientPoint = default(Vector2);
            resolvedIndex = -1;
            resolvedText = null;

            var panel = widget as ListPanelWidget;
            if (panel == null)
                return false;
            // 未挂载的对话框列表没有可信坐标：返回 false（调用方按 element_missing 报错），
            // 绝不拿旧布局算出的像素去点下面那层。
            if (!IsUnderRoot(panel))
                return false;
            // 没被布局过的列表（被折叠面板跳过测量）同样没有可信坐标：行坐标是拿面板矩形算的，
            // 面板矩形为零就必然算到 (0,0) 上去。
            BoundingRectangle panelBounds = panel.GlobalBounds;
            if (panelBounds.Max.X - panelBounds.Min.X <= 0.001f
                || panelBounds.Max.Y - panelBounds.Min.Y <= 0.001f)
                return false;

            ReadOnlyList<object> items = panel.Items;
            // ReadOnlyList 是值类型（不是引用），不能和 null 比 —— 只判空表。
            if (items.Count == 0)
                return false;

            int index = -1;
            if (!string.IsNullOrEmpty(rowText))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    string text = DescribeListItem(items[i]);
                    if (text != null && text.IndexOf(rowText, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0)
                    return false;
            }
            else
            {
                index = rowIndex;
                if (index < 0 || index >= items.Count)
                    return false;
                // 按序号找：先把列表滚到那一项（`ScrollToItem` 会把它带进可视区），
                // 之后就能用"找文字"那条路拿到真实控件。
                try
                {
                    panel.ScrollToItem(items[index]);
                }
                catch (Exception)
                {
                    // 滚动失败也继续：下面还有经验公式兜底
                }
            }

            if (index < 0 || index >= items.Count)
                return false;

            string wanted = DescribeListItem(items[index]);
            resolvedIndex = index;
            resolvedText = wanted;

            // ---- ① 真正的项控件（屏幕上真实的位置）
            Widget itemWidget = null;
            if (!string.IsNullOrEmpty(wanted))
                itemWidget = FindItemWidget(panel, wanted);
            if (itemWidget == null && index >= 0 && index < panel.Children.Count)
            {
                // 按序号：项控件的顺序就是可视项的顺序（ArrangeOverride 从 m_firstVisibleIndex 起排）
                int firstVisible = Math.Max(0, panel.SelectedIndex.HasValue ? 0 : 0);
                itemWidget = null;   // 顺序不可靠时不猜，交给经验公式
                if (firstVisible < 0)
                    itemWidget = null;
            }
            if (itemWidget != null && IsPointOverWidget(itemWidget))
            {
                clientPoint = CenterOf(itemWidget);
                return true;
            }

            // ---- ② 经验公式兜底（ItemSize/ScrollPosition）
            float itemSize = panel.ItemSize;
            float panelSpan = (int)panel.Direction == 0 ? panel.ActualSize.X : panel.ActualSize.Y;
            float wanted0 = index * itemSize;
            if (panelSpan > 0f)
            {
                float current = panel.ScrollPosition;
                if (wanted0 < current)
                    panel.ScrollPosition = wanted0;
                else if (wanted0 + itemSize > current + panelSpan)
                    panel.ScrollPosition = wanted0 + itemSize - panelSpan;
            }

            float scroll = panel.ScrollPosition;
            Vector2 panelSize = panel.ActualSize;
            bool horizontal = (int)panel.Direction == 0;
            Vector2 local = horizontal
                ? new Vector2(index * itemSize - scroll + itemSize * 0.5f, panelSize.Y * 0.5f)
                : new Vector2(panelSize.X * 0.5f, index * itemSize - scroll + itemSize * 0.5f);

            clientPoint = panel.WidgetToScreen(local);
            return true;
        }

        /// <summary>列表里"文字包含 wantd"的那个项控件（可见项才有控件）。</summary>
        private static Widget FindItemWidget(ListPanelWidget panel, string wanted)
        {
            foreach (Widget child in panel.Children)
            {
                string text = CollectText(child);
                if (text != null && text.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                    return child;
            }
            return null;
        }

        /// <summary>把一个控件（含子孙）里的文字拼起来，用来在列表里按文字找行。</summary>
        public static string CollectText(Widget widget)
        {
            if (widget == null)
                return null;
            var builder = new StringBuilder();
            CollectText(widget, builder, 0);
            return builder.Length > 0 ? builder.ToString() : null;
        }

        private static void CollectText(Widget widget, StringBuilder builder, int depth)
        {
            if (widget == null || depth > 8)
                return;

            var label = widget as LabelWidget;
            if (label != null && !string.IsNullOrEmpty(label.Text))
            {
                if (builder.Length > 0)
                    builder.Append(' ');
                builder.Append(label.Text);
            }
            var button = widget as BevelledButtonWidget;
            if (button != null && !string.IsNullOrEmpty(button.Text))
            {
                if (builder.Length > 0)
                    builder.Append(' ');
                builder.Append(button.Text);
            }

            var container = widget as ContainerWidget;
            if (container != null)
            {
                foreach (Widget child in container.Children)
                    CollectText(child, builder, depth + 1);
            }
        }

        /// <summary>这个控件的中心是否真的命中它自己（挡住/移出可视区时为 false）。</summary>
        private static bool IsPointOverWidget(Widget widget)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null || widget == null)
                return false;
            Widget hit = root.HitTestGlobal(CenterOf(widget));
            return hit != null && (hit == widget || IsSelfOrDescendant(hit, widget)
                || IsSelfOrDescendant(widget, hit));
        }

        // ---------------------------------------------------------------- 分屏视图（GameWidget）归属

        /// <summary>
        /// 控件归哪个分屏视图：沿父链往上找第一个 `GameWidget`。
        ///
        /// 为什么"归属"必须显式算出来：主机分屏时**每个视图各有一棵 `Gui` 子树**
        /// （`GameWidget.cs:18,55-71`：`View` + `Gui` 都是 `GameWidget` 的子控件），于是
        /// `MoreButton` / 联机 Mod 的 `SuMultiplayerRoomButton` 这类按名字取的东西**每个视图一份**。
        /// 只报一个 `clientPoint` 而不报"属于哪个视图"时，"这个名字指的是哪一份、那个像素在谁的区域里"
        /// 全看不出来 —— 这正是"三视图下 `click CR` 点了没反应"的一半原因。
        ///
        /// 菜单/对话框不在任何 `GameWidget` 下 → null（它们只有一份，没有分屏歧义）。
        /// </summary>
        public static GameWidget FindGameWidget(Widget widget)
        {
            for (Widget current = widget; current != null; current = current.ParentWidget)
            {
                var gameWidget = current as GameWidget;
                if (gameWidget != null)
                    return gameWidget;
            }
            return null;
        }

        public static int? GameWidgetIndexOf(Widget widget)
        {
            GameWidget gameWidget = FindGameWidget(widget);
            return gameWidget != null ? gameWidget.GameWidgetIndex : (int?)null;
        }

        /// <summary>
        /// 当前世界里的全部分屏视图。顺序 = `SubsystemGameWidgets.m_gameWidgets` 的顺序
        /// （Source: `SubsystemGameWidgets.cs:85-100`，`AddGameWidgetForPlayer` 同时写这个列表和
        /// `GamesWidget.Children`，所以它也是 `GamesWidget.Children` 的下标顺序）。
        /// 不在世界里（主菜单 / 世界加载中）→ 空表，**不猜**。
        /// </summary>
        public static List<GameWidget> CurrentGameWidgets()
        {
            var result = new List<GameWidget>();
            try
            {
                var project = GameManager.Project;
                var subsystem = project != null
                    ? project.FindSubsystem<SubsystemGameWidgets>(false)
                    : null;
                if (subsystem == null)
                    return result;
                ReadOnlyList<GameWidget> widgets = subsystem.GameWidgets;
                for (int i = 0; i < widgets.Count; i++)
                {
                    if (widgets[i] != null)
                        result.Add(widgets[i]);
                }
            }
            catch (Exception)
            {
                // 世界装载/销毁的瞬间 Project 会短暂不可用。这里只用于"报视图归属"，
                // 取不到就如实报空表，绝不猜一个视图出来。
            }
            return result;
        }

        /// <summary>按 `GameWidget.GameWidgetIndex`（分屏槽位 H1/H2/H3…，见 `GameWidget.cs:20,58`）找视图。</summary>
        public static GameWidget FindGameWidgetByIndex(int index)
        {
            List<GameWidget> views = CurrentGameWidgets();
            for (int i = 0; i < views.Count; i++)
            {
                if (views[i].GameWidgetIndex == index)
                    return views[i];
            }
            return null;
        }

        /// <summary>
        /// 该视图的输入面是否**接受鼠标**：`WidgetsHierarchyInput.Devices` 含 `Mouse` 或 `MultiMice`。
        /// 这不是"偏好"而是引擎硬门：`IsMouseCursorVisible` 的 getter 在没有鼠标设备时**恒为 false**
        /// （Source: `Survivalcraft/Game/WidgetInput.cs:152-166`），而 `Press/Tap/Click` 的派生整段被它门控
        /// （同文件 `:741`）—— 没有鼠标设备的视图，任何软光标点击都派生不出 Click。
        /// </summary>
        public static bool AcceptsMouse(GameWidget gameWidget)
        {
            if (gameWidget == null)
                return false;
            WidgetInput input = gameWidget.WidgetsHierarchyInput;
            return input != null && HasMouseDevice(input);
        }

        /// <summary>输入面是否含鼠标设备（`Mouse` / `MultiMice`）。</summary>
        public static bool HasMouseDevice(WidgetInput input)
        {
            if (input == null)
                return false;
            return (input.Devices & (WidgetInputDevice.Mouse | WidgetInputDevice.MultiMice)) != 0;
        }

        /// <summary>主视图的 `GameWidgetIndex`（规则见 <see cref="UiSplitView.SelectPrimaryView"/>）。</summary>
        public static int PrimaryGameWidgetIndex()
        {
            List<GameWidget> views = CurrentGameWidgets();
            var indices = new int[views.Count];
            var acceptsMouse = new bool[views.Count];
            for (int i = 0; i < views.Count; i++)
            {
                indices[i] = views[i].GameWidgetIndex;
                acceptsMouse[i] = AcceptsMouse(views[i]);
            }
            return UiSplitView.SelectPrimaryView(indices, acceptsMouse);
        }

        public static GameWidget PrimaryGameWidget()
        {
            int index = PrimaryGameWidgetIndex();
            return index == UiSplitView.NoView ? null : FindGameWidgetByIndex(index);
        }

        /// <summary>
        /// 只在**这个视图**的子树里做命中测试（与 `Widget.HitTestGlobal` 同一套语义：跳过不可见、
        /// `ClampToBounds` 的先过自己的 `HitTest`、子控件后进先出、最后才判自己）。
        /// Source: `Survivalcraft/Game/Widget.cs:836-857`（引擎的私有静态递归，这里照抄一份）。
        ///
        /// 为什么必须按视图判：`Widget.HitTestGlobal(point)` 一律从**全局根**开始，它回答的是
        /// "屏幕上这一点最终被谁消费"—— 多视图下那个"谁"完全可能是另一个视图的 `ViewWidget`。
        /// 报告要回答的是"**这个视图自己**会不会接住它"，所以要按归属视图重新走一遍。
        /// </summary>
        public static Widget HitTestInGameWidget(GameWidget gameWidget, Vector2 point)
        {
            if (gameWidget == null || !gameWidget.IsVisible)
                return null;
            if (gameWidget.ClampToBounds && !gameWidget.HitTest(point))
                return null;
            return HitTestInSubtree(gameWidget, point);
        }

        private static Widget HitTestInSubtree(Widget widget, Vector2 point)
        {
            if (widget == null || !widget.IsVisible)
                return null;
            if (widget.ClampToBounds && !widget.HitTest(point))
                return null;
            var container = widget as ContainerWidget;
            if (container != null)
            {
                for (int i = container.Children.Count - 1; i >= 0; i--)
                {
                    Widget hit = HitTestInSubtree(container.Children[i], point);
                    if (hit != null)
                        return hit;
                }
            }
            return widget.IsHitTestVisible && widget.HitTest(point) ? widget : null;
        }

        /// <summary>
        /// `ui` 的 `views` 块：每个分屏视图一行 —— 槽位、玩家、屏幕矩形、布局格子、输入设备、
        /// 是否接受鼠标、是不是主视图（`click` 默认点的那个）。
        ///
        /// `cell` / `cellAgrees` 是**交叉核对**：`UiSplitView` 按
        /// `GamesWidget.ArrangeOverride`（`Survivalcraft/Game/GamesWidget.cs:19-215`）算出该视图应占的
        /// 相对格子，再与引擎实际摆出来的 `GameWidget.GlobalBounds` 比。两者不一致说明"布局理解漂了"
        /// （引擎升级改了布局，或视图顺序变了）—— 那时元素坐标与视图归属都不可信，要立刻知道。
        /// 换算成像素一律用引擎的 `GlobalBounds`（唯一真相），这里只做报告。
        /// </summary>
        public static List<Dictionary<string, object>> DescribeViews()
        {
            var views = new List<Dictionary<string, object>>();
            List<GameWidget> gameWidgets = CurrentGameWidgets();
            if (gameWidgets.Count == 0)
                return views;

            ContainerWidget root = ScreensManager.RootWidget;
            Widget parent = gameWidgets[0].ParentWidget;
            var gamesWidget = parent as GamesWidget;
            SplitViewLayoutKind layout = UiSplitView.ResolveLayout(gameWidgets.Count,
                SettingsManager.ScreenLayout1.ToString(),
                SettingsManager.ScreenLayout2.ToString(),
                SettingsManager.ScreenLayout3.ToString(),
                SettingsManager.ScreenLayout4.ToString());
            int primary = PrimaryGameWidgetIndex();
            // `DoubleOpposite` 的缩放来自 `Window.Size`（`GamesWidget.cs:73`）。无头服务端
            // （`disableDrawing=true`）上它可能取不到 —— 只影响这一种布局的格子算式，
            // 取不到就传 0（`TryGetCell` 会如实返回 false → `views[].cell=null`），
            // 不让一条**只读诊断**把整个 `obs.ui` 带崩。
            float windowWidth = 0f;
            float windowHeight = 0f;
            try
            {
                Point2 windowSize = Window.Size;
                windowWidth = windowSize.X;
                windowHeight = windowSize.Y;
            }
            catch (Exception)
            {
            }
            BoundingRectangle gamesBounds = gamesWidget != null
                ? gamesWidget.GlobalBounds
                : default(BoundingRectangle);
            bool gamesLaidOut = gamesWidget != null && gamesBounds.Max.X - gamesBounds.Min.X > 0.001f
                && gamesBounds.Max.Y - gamesBounds.Min.Y > 0.001f;

            for (int i = 0; i < gameWidgets.Count; i++)
            {
                GameWidget gameWidget = gameWidgets[i];
                BoundingRectangle bounds = gameWidget.GlobalBounds;
                WidgetInput input = gameWidget.WidgetsHierarchyInput;
                var entry = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["gameWidget"] = gameWidget.GameWidgetIndex,
                    ["player"] = SafePlayerName(gameWidget),
                    ["rect"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["x"] = bounds.Min.X,
                        ["y"] = bounds.Min.Y,
                        ["w"] = bounds.Max.X - bounds.Min.X,
                        ["h"] = bounds.Max.Y - bounds.Min.Y
                    },
                    ["layout"] = UiSplitView.LayoutName(layout),
                    ["inputDevices"] = input != null ? input.Devices.ToString() : null,
                    ["acceptsMouse"] = AcceptsMouse(gameWidget),
                    ["mouseCursorVisible"] = input != null && input.IsMouseCursorVisible,
                    ["primary"] = gameWidget.GameWidgetIndex == primary,
                    ["inTrees"] = root != null && IsSelfOrDescendant(gameWidget, root)
                };

                // 视图在 `GamesWidget.Children` 里的下标才是布局算式的输入（≠ 槽位号）。
                int childIndex = gamesWidget != null ? gamesWidget.Children.IndexOf(gameWidget) : -1;
                SplitViewCell cell;
                if (gamesWidget != null && childIndex >= 0
                    && UiSplitView.TryGetCell(layout, gameWidgets.Count, childIndex,
                        gamesWidget.ActualSize.X, gamesWidget.ActualSize.Y,
                        windowWidth, windowHeight, out cell))
                {
                    float x, y, w, h;
                    if (UiSplitView.TryGetRelativeQuad(layout, gameWidgets.Count, childIndex,
                        gamesWidget.ActualSize.X, gamesWidget.ActualSize.Y,
                        windowWidth, windowHeight, out x, out y, out w, out h))
                    {
                        entry["cell"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["x"] = x,
                            ["y"] = y,
                            ["w"] = w,
                            ["h"] = h
                        };
                        if (gamesLaidOut)
                        {
                            float actualX = (bounds.Min.X - gamesBounds.Min.X)
                                / (gamesBounds.Max.X - gamesBounds.Min.X);
                            float actualY = (bounds.Min.Y - gamesBounds.Min.Y)
                                / (gamesBounds.Max.Y - gamesBounds.Min.Y);
                            float actualW = (bounds.Max.X - bounds.Min.X)
                                / (gamesBounds.Max.X - gamesBounds.Min.X);
                            float actualH = (bounds.Max.Y - bounds.Min.Y)
                                / (gamesBounds.Max.Y - gamesBounds.Min.Y);
                            const float tolerance = 0.02f;
                            entry["cellAgrees"] = Math.Abs(actualX - x) <= tolerance
                                && Math.Abs(actualY - y) <= tolerance
                                && Math.Abs(actualW - w) <= tolerance
                                && Math.Abs(actualH - h) <= tolerance;
                        }
                        else
                        {
                            entry["cellAgrees"] = null;
                        }
                    }
                    else
                    {
                        // 90° 旋转的视图（Opposite 布局）：AABB 宽高与格子互换、还叠加引擎的
                        // parentOffset，这里不给相对格子结论 —— 只标记"它是旋转视图"。
                        entry["cell"] = null;
                        entry["cellAgrees"] = null;
                        entry["rotated"] = cell.Rotated;
                    }
                }
                else
                {
                    entry["cell"] = null;
                    entry["cellAgrees"] = null;
                    if (gamesWidget == null)
                        entry["layoutReason"] = "the GameWidgets are not under a GamesWidget";
                    else if (childIndex < 0)
                        entry["layoutReason"] = "the GameWidget is not a child of the GamesWidget";
                }

                views.Add(entry);
            }
            return views;
        }

        /// <summary>视图对应的本地玩家名（取不到就 null；远端 avatar 没有 GameWidget，不会走到这里）。</summary>
        private static string SafePlayerName(GameWidget gameWidget)
        {
            try
            {
                return gameWidget.PlayerData != null ? gameWidget.PlayerData.Name : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- 世界内 HUD 归属

        /// <summary>
        /// 世界内 HUD（`GameWidget` 的 `Gui` 层）里那几条会**被整体平移到屏外**的容器。
        ///
        /// 依据（全部来自引擎自己的资源定义与布局代码，不是推测）：
        ///   · 层次来自 `Pak/Widgets/GameWidget.xml`：`Gui` 下直接挂着 `ControlsContainer`
        ///     （触屏控制 + `ModalPanelContainer`）、`LeftControlsContainer`、`RightControlsContainer`
        ///     （`MoreContents` / `MoreButton` 在它里面）。
        ///   · `Survivalcraft/Game/ComponentGui.cs:300-316` `UpdateSidePanelsAnimation()` 每帧把
        ///     `m_leftControlsContainerWidget` 左移 `ActualSize.X * m_sidePanelsFactor`、
        ///     `m_rightControlsContainerWidget` 右移同样的量；非触屏且**没有模态面板**时 `factor=1`
        ///     → 两条侧栏整条落在屏外（实测 `clientPoint`：`BackButton` x=-55、`MoreButton` x=2139，
        ///     客户区 2100）。
        /// </summary>
        internal static readonly string[] InWorldHudContainers =
        {
            "LeftControlsContainer", "RightControlsContainer", "MoreContents", "ControlsContainer"
        };

        /// <summary>
        /// 控件是否属于世界内 HUD。返回命中的容器名（写进错误消息用，能直接说明"挡在哪一层"）。
        /// 只看**祖先链上的名字**，不看可见性 —— 被折叠/移出屏幕的 HUD 正是要处理的场景。
        /// </summary>
        public static bool TryGetInWorldHudContainer(Widget widget, out string containerName)
        {
            containerName = null;
            for (Widget current = widget; current != null; current = current.ParentWidget)
            {
                string name = current.Name;
                if (string.IsNullOrEmpty(name))
                    continue;
                for (int i = 0; i < InWorldHudContainers.Length; i++)
                {
                    if (string.Equals(name, InWorldHudContainers[i], StringComparison.Ordinal))
                    {
                        containerName = name;
                        return true;
                    }
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- 二级开关（MoreContents / MoreButton）

        /// <summary>
        /// `MoreContents`：`MoreButton` 二级开关打开的那个容器
        /// （`Pak/Widgets/GameWidget.xml:67`；`ComponentGui.cs:231` 按这个名字取它）。
        /// </summary>
        public const string MoreContentsName = "MoreContents";

        /// <summary>
        /// `MoreButton`：二级开关本身（`Pak/Widgets/GameWidget.xml:76`，
        /// `IsAutoCheckingEnabled="true"` = 点一下展开、再点一下收起；
        /// `ComponentGui.cs:400` 每帧把 `MoreContents.IsVisible` 绑到它的 `IsChecked` 上）。
        /// </summary>
        public const string MoreToggleName = "MoreButton";

        /// <summary>
        /// 目标是否**挂在 `MoreContents` 里**（也就是在 `MoreButton` 那个二级开关后面）。
        ///
        /// 依据（引擎自己的结构与代码，不是推测）：
        ///   · `Pak/Widgets/GameWidget.xml:65-77`：`RightControlsContainer` 里那个横排
        ///     `StackPanelWidget` 下**并排**放着 `MoreContents`（一列按钮）与 `MoreButton`；
        ///   · `Survivalcraft/Game/ComponentGui.cs:231,400`：`m_moreContentsWidget =
        ///     guiWidget.Children.Find<Widget>("MoreContents")`，每帧
        ///     `m_moreContentsWidget.IsVisible = m_moreButtonWidget.IsChecked` ——
        ///     二级收起时它里面的按钮既没被布局也没被绘制
        ///     （`StackPanelWidget.MeasureOverride/ArrangeOverride` 跳过不可见子控件），于是不可点；
        ///   · 外部 Mod 就是往这里挂按钮的（联机 Mod 的 `CR`：
        ///     `Mod/ScMultiplayer/Func/Component/MultiplayerUiComponent.cs:85`）。
        ///
        /// 判定走**祖先链**（严格后代：从 `ParentWidget` 往上找名为 `MoreContents` 的控件），
        /// 并要求它确实挂在世界内 HUD 的**右侧栏**下（`GameWidget.xml:65-67`：
        /// `MoreContents` 的父控件是个没有名字的横排 `StackPanelWidget`，再往上是
        /// `RightControlsContainer`）—— 光看名字会认下别的屏上的同名容器。
        /// **不看可见性** —— "二级收着"正是要处理的场景。
        /// </summary>
        public static bool TryGetMoreContentsAncestor(Widget widget, out Widget moreContents)
        {
            moreContents = null;
            for (Widget current = widget != null ? widget.ParentWidget : null;
                 current != null;
                 current = current.ParentWidget)
            {
                if (!string.Equals(current.Name, MoreContentsName, StringComparison.Ordinal))
                    continue;
                string hudContainer;
                if (TryGetInWorldHudContainer(current.ParentWidget, out hudContainer)
                    && string.Equals(hudContainer, "RightControlsContainer", StringComparison.Ordinal))
                {
                    moreContents = current;
                    return true;
                }
                return false;   // 同名，但不在世界内 HUD 的右侧栏里：不是这个二级开关
            }
            return false;
        }

        /// <summary>
        /// `MoreContents` 的二级开关控件（`MoreButton`）：**只在同一个父容器的子树里按名字找**。
        ///
        /// 为什么不直接用全局 `Resolve(root, "MoreButton")`：家具面板里也有一个 `MoreButton`
        /// （`Survivalcraft/Game/FurnitureInventoryPanel.cs:49`），面板开着时两个开关同时在树上 ——
        /// 全局解析要么报 `ambiguous_selector`，要么更糟：拿到另一个开关（点错 = 把别的界面开开关关）。
        /// 从 `MoreContents` 的**父控件**往下找（跳过 `MoreContents` 自己那一支），命中的一定是
        /// 同一排里的那个开关（`GameWidget.xml:65-77`）。
        /// </summary>
        public static bool TryGetMoreContentsToggle(Widget moreContents, out Widget moreButton)
        {
            moreButton = null;
            if (moreContents == null || moreContents.ParentWidget == null)
                return false;
            return TryFindByName(moreContents.ParentWidget, moreContents, MoreToggleName, out moreButton);
        }

        /// <summary>`MoreContents` 当前是否已展开（引擎每帧把它绑在 `MoreButton.IsChecked` 上）。</summary>
        public static bool IsMoreContentsExpanded(Widget moreContents)
        {
            return moreContents != null && moreContents.IsVisible;
        }

        /// <summary>在子树里按名字找控件，跳过 `skip` 那一支。</summary>
        private static bool TryFindByName(Widget root, Widget skip, string name, out Widget found)
        {
            found = null;
            if (root == null || ReferenceEquals(root, skip))
                return false;
            if (string.Equals(root.Name, name, StringComparison.Ordinal))
            {
                found = root;
                return true;
            }
            var container = root as ContainerWidget;
            if (container == null)
                return false;
            foreach (Widget child in container.Children)
            {
                if (TryFindByName(child, skip, name, out found))
                    return true;
            }
            return false;
        }

        /// <summary>几何上命中得到：HitTestGlobal(中心) 解析到该控件或其子控件。</summary>
        public static bool IsHittable(Widget widget)
        {
            if (widget == null)
                return false;
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                return false;
            // 没有矩形 = 没被布局过（被折叠的面板里的控件，见 DescribeElement 的 laidOut 注释）。
            // 零尺寸控件在原点 (0,0) 上能通过自己的 `HitTest`（区间是 [0,0]），不挡掉会报出一个
            // 根本不存在的可点位置。
            BoundingRectangle bounds = widget.GlobalBounds;
            if (bounds.Max.X - bounds.Min.X <= 0.001f || bounds.Max.Y - bounds.Min.Y <= 0.001f)
                return false;
            return IsSelfOrDescendant(root.HitTestGlobal(CenterOf(widget)), widget);
        }

        /// <summary>
        /// 游戏此刻是否真会为该控件派生一次点击 = 可命中 + 该层级输入通道的光标可见。
        /// 世界内 ComponentInput 会把 IsMouseCursorVisible 置 false（鼠标交给视角，
        /// ComponentInput.cs:155），而 Press/Tap/Click 派生整段被它门控（WidgetInput.cs:741）。
        /// </summary>
        public static bool IsClickable(Widget widget)
        {
            if (!IsHittable(widget))
                return false;
            WidgetInput input = widget.Input;
            return input != null && input.IsMouseCursorVisible;
        }

        public static string ShortName(Widget widget)
        {
            if (widget == null)
                return null;
            string name = widget.Name;
            return string.IsNullOrEmpty(name)
                ? widget.GetType().Name
                : widget.GetType().Name + "(" + name + ")";
        }

        public static string ScreenName()
        {
            try
            {
                Dictionary<string, Screen> screens = ModManager.Instance.ModParentField
                    .GetStaticField<Dictionary<string, Screen>>(typeof(ScreensManager), "m_screens");
                Screen current = ScreensManager.CurrentScreen;
                if (screens != null && current != null)
                {
                    foreach (KeyValuePair<string, Screen> pair in screens)
                    {
                        if (ReferenceEquals(pair.Value, current))
                            return pair.Key;
                    }
                }
            }
            catch
            {
            }
            return ScreensManager.CurrentScreen != null
                ? ScreensManager.CurrentScreen.GetType().Name
                : null;
        }

        public static Vector2? ToScreenPoint(Vector2 clientPoint)
        {
            try
            {
                Point2 position = Window.Position;
                return new Vector2(clientPoint.X + position.X, clientPoint.Y + position.Y);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// `Vector2?` → `{x, y}` 或 null。**任何要放进 JSON 回包的坐标都必须走这里**：
        /// 裸的 `Engine.Vector2` 带一个返回 Vector2 的 `YX` 属性，序列化器会无限递归
        /// （`$.YX.YX.YX…`）直到抛 "possible object cycle" —— 而那条异常会把游戏进程带走。
        /// </summary>
        public static Dictionary<string, object> PointToValue(Vector2? point)
        {
            if (!point.HasValue)
                return null;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = point.Value.X,
                ["y"] = point.Value.Y
            };
        }

        /// <summary>
        /// 虚拟列表项导出。SC 的 ListPanelWidget 自绘条目（条目不是控件），
        /// 因此必须按"面板 + 索引"算出每一行可点击坐标。
        /// 行几何来自游戏自身：第 i 行位于 i*ItemSize - ScrollPosition，宽度为面板宽度。
        /// Source: Survivalcraft/Game/ListPanelWidget.cs:226-248, 287-295
        ///
        /// `includePoints=false` 时**不算坐标**（省掉每行两次几何换算）——给状态观察用：
        /// 摘要只要"第几行/什么字/选中没/在不在可视区"，不要像素。
        /// 几何与滚动算式**仍然只有这一份**（两处各写一份必然漂移）。
        /// </summary>
        private static Dictionary<string, object> DescribeList(ListPanelWidget panel,
            int maxItems = 64, bool includePoints = true)
        {
            var block = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                ReadOnlyList<object> items = panel.Items;
                float itemSize = panel.ItemSize;
                float scroll = panel.ScrollPosition;
                Vector2 panelSize = panel.ActualSize;
                bool horizontal = (int)panel.Direction == 0;

                block["panel"] = panel.Name;
                block["itemsCount"] = items.Count;
                block["itemSize"] = itemSize;
                block["scrollPosition"] = scroll;
                block["horizontal"] = horizontal;
                block["selectedIndex"] = panel.SelectedIndex;

                // 还能不能滚：内容长度 vs 面板长度。列表行填不满或已经贴边时是 false，
                // 这正是"要不要先滚动/翻页"的依据（G16：翻页由 C# 做，模型只选序号）。
                float content = items.Count * itemSize;
                float viewport = horizontal ? panelSize.X : panelSize.Y;
                float tolerance = Math.Max(0.5f, itemSize * 0.05f);
                block["canScrollUp"] = scroll > tolerance;
                block["canScrollDown"] = scroll + viewport + tolerance < content;

                var rows = new List<Dictionary<string, object>>();
                int limit = Math.Min(items.Count, Math.Max(0, maxItems));
                for (int i = 0; i < limit; i++)
                {
                    Vector2 local = horizontal
                        ? new Vector2(i * itemSize - scroll + itemSize * 0.5f, panelSize.Y * 0.5f)
                        : new Vector2(panelSize.X * 0.5f, i * itemSize - scroll + itemSize * 0.5f);
                    Vector2 client = panel.WidgetToScreen(local);
                    BoundingRectangle bounds = panel.GlobalBounds;
                    bool inside = client.X >= bounds.Min.X && client.X <= bounds.Max.X &&
                        client.Y >= bounds.Min.Y && client.Y <= bounds.Max.Y;

                    var row = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["index"] = i,
                        ["text"] = DescribeListItem(items[i]),
                        ["selected"] = i == panel.SelectedIndex,
                        ["visibleInPanel"] = inside
                    };
                    if (includePoints)
                    {
                        row["clientPoint"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["x"] = client.X,
                            ["y"] = client.Y
                        };
                        Vector2? screenPoint = ToScreenPoint(client);
                        row["screenPoint"] = screenPoint.HasValue
                            ? new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["x"] = screenPoint.Value.X,
                                ["y"] = screenPoint.Value.Y
                            }
                            : null;
                    }
                    rows.Add(row);
                }
                block["items"] = rows;
                if (items.Count > limit)
                    block["truncated"] = true;
            }
            catch (Exception exception)
            {
                block["error"] = exception.GetType().Name + ": " + exception.Message;
            }
            return block;
        }

        /// <summary>
        /// 屏幕上"当前这个列表"的紧凑快照（G16）—— 供**状态观察 / 摘要**用，不是给 `ui.query` 的。
        ///
        /// 为什么要有它：世界外选世界、翻页这类决策，答案就是"第几行"，
        /// 而列表行是自绘的（条目不是控件），不主动列出来模型看不见内容，只能瞎猜。
        ///
        /// 三条纪律：
        ///   · **只读**：不改滚动、不改选中。观察路径一旦有副作用，排查时看到的就不是现场（A44）；
        ///   · **有歧义要说出来**：屏幕上有多个非空列表时 `visibleCandidates` &gt; 1，
        ///     摘要会加 `?` 前缀 —— 而不是让调用方以为"就是它，放心点"；
        ///   · **判不了要说清**：取行时抛异常 → 块里带 `error`（消费侧据此判"看不到"，
        ///     **绝不能**渲染成"列表是空的" —— "读失败"和"没有世界"是两个完全不同的事实，
        ///     混起来的后果是模型决定"去创建一个新世界"）。
        /// </summary>
        public static Dictionary<string, object> DescribeActiveList(int maxRows)
        {
            try
            {
                ContainerWidget root = ScreensManager.RootWidget;
                if (root == null)
                    return null;

                BoundingRectangle rootBounds = root.GlobalBounds;
                ListPanelWidget bestFilled = null;
                ListPanelWidget bestAny = null;
                string filledPath = null;
                string anyPath = null;
                float filledArea = 0f;
                float anyArea = 0f;
                int withItems = 0;

                foreach (Widget widget in root.AllChildren)
                {
                    var panel = widget as ListPanelWidget;
                    if (panel == null || !panel.IsVisibleGlobal || !panel.IsEnabledGlobal)
                        continue;

                    float area = VisibleArea(panel, rootBounds);
                    if (area <= 0.5f)
                        continue;

                    string path = BuildPath(panel) ?? string.Empty;
                    int count = ItemCount(panel);

                    if (count > 0)
                    {
                        withItems++;
                        if (Better(panel, area, path, bestFilled, filledArea, filledPath))
                        {
                            bestFilled = panel;
                            filledArea = area;
                            filledPath = path;
                        }
                    }
                    if (Better(panel, area, path, bestAny, anyArea, anyPath))
                    {
                        bestAny = panel;
                        anyArea = area;
                        anyPath = path;
                    }
                }

                ListPanelWidget winner = bestFilled ?? bestAny;
                if (winner == null)
                    return null;

                Dictionary<string, object> full = DescribeList(winner, maxRows, false);
                var result = new Dictionary<string, object>(StringComparer.Ordinal);

                object failure;
                if (full.TryGetValue("error", out failure) && failure != null)
                {
                    // 读失败**不是**空列表：原样带出去，让消费侧判"看不到"。
                    result["error"] = failure;
                    return result;
                }

                result["panel"] = PanelName(winner);
                result["itemsCount"] = full.ContainsKey("itemsCount") ? full["itemsCount"] : 0;
                result["selectedIndex"] = full.ContainsKey("selectedIndex") ? full["selectedIndex"] : -1;
                result["canScrollUp"] = full.ContainsKey("canScrollUp") && full["canScrollUp"] is bool
                    && (bool)full["canScrollUp"];
                result["canScrollDown"] = full.ContainsKey("canScrollDown") && full["canScrollDown"] is bool
                    && (bool)full["canScrollDown"];
                result["visibleCandidates"] = Math.Max(1, withItems);

                var rows = new List<object>();
                var items = full.ContainsKey("items") ? full["items"] as List<Dictionary<string, object>> : null;
                if (items != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        Dictionary<string, object> row = items[i];
                        if (row == null)
                            continue;
                        rows.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["index"] = row.ContainsKey("index") ? row["index"] : i,
                            ["text"] = row.ContainsKey("text") ? row["text"] : null,
                            ["selected"] = row.ContainsKey("selected") && row["selected"] is bool
                                && (bool)row["selected"],
                            ["visible"] = row.ContainsKey("visibleInPanel") && row["visibleInPanel"] is bool
                                && (bool)row["visibleInPanel"]
                        });
                    }
                }
                result["rows"] = rows;
                return result;
            }
            catch (Exception exception)
            {
                // 与这个文件里其它导出方法一致：错误**放进返回值**，不吞也不另开日志通道。
                // 调用方（状态观察 → 摘要）看到 `error` 就判"看不到列表"。
                var failed = new Dictionary<string, object>(StringComparer.Ordinal);
                failed["error"] = exception.GetType().Name + ": " + exception.Message;
                return failed;
            }
        }

        /// <summary>
        /// 列表行数；取不到就当 0（**单个列表坏掉不该让整屏的列表都扫不出来**）。
        /// </summary>
        private static int ItemCount(ListPanelWidget panel)
        {
            try
            {
                return panel.Items.Count;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// 选哪个列表：**面积大的优先**（主列表通常占满屏），面积相同按路径字典序
        /// —— 必须有确定的平局规则，否则同一屏两帧可能选中不同的列表，
        /// 模型会看到自相矛盾的摘要（"同一状态两种答案"最难查）。
        /// </summary>
        private static bool Better(Widget candidate, float area, string path,
            Widget current, float currentArea, string currentPath)
        {
            if (current == null)
                return true;
            if (area > currentArea + 0.5f)
                return true;
            if (area < currentArea - 0.5f)
                return false;
            return string.CompareOrdinal(path, currentPath) < 0;
        }

        /// <summary>控件落在屏幕内的可见面积（被裁掉/移到屏幕外的部分不算）。</summary>
        private static float VisibleArea(Widget widget, BoundingRectangle root)
        {
            BoundingRectangle bounds = widget.GlobalBounds;
            float left = Math.Max(bounds.Min.X, root.Min.X);
            float right = Math.Min(bounds.Max.X, root.Max.X);
            float bottom = Math.Max(bounds.Min.Y, root.Min.Y);
            float top = Math.Min(bounds.Max.Y, root.Max.Y);
            float width = right - left;
            float height = top - bottom;
            return width <= 0f || height <= 0f ? 0f : width * height;
        }

        private static string PanelName(ListPanelWidget panel)
        {
            if (panel == null)
                return null;
            return string.IsNullOrEmpty(panel.Name) ? panel.GetType().Name : panel.Name;
        }

        private static string DescribeListItem(object item)
        {
            if (item == null)
                return null;
            if (item is WorldInfo worldInfo)
                return worldInfo.WorldSettings.Name;
            try
            {
                return ModManager.Instance.ModParentField.GetParentField(item, "Name") as string;
            }
            catch
            {
            }
            return item.ToString();
        }

        /// <summary>
        /// 列表项的文字（**对外**）：`ui.locate` / `ui.click` 报"命中了哪一行"、
        /// 以及 `UiService` 的 invoke 路径按文字找行都用它，两边必须是同一个口径。
        /// </summary>
        public static string DescribeListItemText(object item)
        {
            return DescribeListItem(item);
        }

        private static bool IsSelfOrDescendant(Widget candidate, Widget ancestor)
        {
            for (Widget widget = candidate; widget != null; widget = widget.ParentWidget)
            {
                if (ReferenceEquals(widget, ancestor))
                    return true;
            }
            return false;
        }

        private static bool IsInteractive(Widget widget, out bool hasInteractiveAncestor)
        {
            hasInteractiveAncestor = false;
            for (Widget parent = widget.ParentWidget;
                parent != null && !ReferenceEquals(parent, ScreensManager.RootWidget);
                parent = parent.ParentWidget)
            {
                if (IsInteractiveType(parent.GetType()))
                {
                    hasInteractiveAncestor = true;
                    break;
                }
            }
            return IsInteractiveType(widget.GetType());
        }

        private static bool IsInteractiveType(Type type)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                string name = current.Name;
                for (int i = 0; i < s_interactiveTypes.Length; i++)
                {
                    if (string.Equals(s_interactiveTypes[i], name, StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        private static bool MatchesFilter(Widget widget, string filter)
        {
            string name = widget.Name;
            if (!string.IsNullOrEmpty(name) &&
                name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            string type = widget.GetType().Name;
            if (type.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            string text = GetText(widget);
            return !string.IsNullOrEmpty(text) &&
                text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetText(Widget widget)
        {
            try
            {
                if (widget is ButtonWidget button)
                    return button.Text;
                if (widget is LabelWidget label)
                    return label.Text;
                if (widget is SliderWidget slider)
                    return slider.Text;
                if (widget is CheckboxWidget checkbox)
                    return checkbox.Text;
                if (widget is LinkWidget link)
                    return link.Text;
                if (string.Equals(widget.GetType().Name, "TextBoxWidget", StringComparison.Ordinal))
                {
                    // TextBoxWidget 是 internal，只能只读反射。
                    return ModManager.Instance.ModParentField.GetParentField(widget, "m_text") as string;
                }
            }
            catch
            {
            }
            return null;
        }
    }
}
