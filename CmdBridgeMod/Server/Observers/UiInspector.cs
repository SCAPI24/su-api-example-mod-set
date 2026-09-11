using Engine;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;

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

        // ---------------------------------------------------------------- 导出

        public static Dictionary<string, object> Describe(
            CmdBridgeConfig config, bool includeAll, int maxElements, string filter)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (root == null)
            {
                result["elements"] = new List<Dictionary<string, object>>();
                result["totalCount"] = 0;
                result["truncated"] = false;
                result["ready"] = false;
                return result;
            }

            List<Dictionary<string, object>> elements = Collect(
                root, includeAll, maxElements, filter, out int total, out bool truncated);

            result["elements"] = elements;
            result["totalCount"] = total;
            result["truncated"] = truncated;
            result["ready"] = true;
            result["shown"] = elements.Count;
            return result;
        }

        private static List<Dictionary<string, object>> Collect(
            ContainerWidget root,
            bool includeAll,
            int maxElements,
            string filter,
            out int total,
            out bool truncated)
        {
            int limit = maxElements > 0 ? maxElements : 512;
            var elements = new List<Dictionary<string, object>>();
            total = 0;
            truncated = false;
            int id = 0;

            foreach (Widget widget in root.AllChildren)
            {
                bool interactive = IsInteractive(widget, out bool hasInteractiveAncestor);
                if (!includeAll && (!interactive || hasInteractiveAncestor))
                    continue;

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

            ContainerWidget root = ScreensManager.RootWidget;
            Widget hit = root != null ? root.HitTestGlobal(center) : null;
            bool hittable = IsSelfOrDescendant(hit, widget);

            element["id"] = id;
            element["path"] = BuildPath(widget);
            element["name"] = widget.Name;
            element["type"] = widget.GetType().Name;
            element["text"] = GetText(widget);
            element["rect"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = bounds.Min.X,
                ["y"] = bounds.Min.Y,
                ["w"] = bounds.Max.X - bounds.Min.X,
                ["h"] = bounds.Max.Y - bounds.Min.Y
            };
            element["center"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = center.X,
                ["y"] = center.Y
            };
            element["clientPoint"] = element["center"];
            Vector2? screenPoint = ToScreenPoint(center);
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
            element["blockedBy"] = hittable ? null : ShortName(hit);

            // hittable 只说明"几何上命中得到"，不代表游戏真会派生一次点击：
            // 世界内 ComponentInput 会把该层级的 IsMouseCursorVisible 置 false（鼠标交给视角，
            // ComponentInput.cs:155），而 WidgetInput 的 Press/Tap/Click 派生整段被它门控
            // （WidgetInput.cs:741）——所以世界内 HUD 按钮用鼠标点不到，玩家只能用按键。
            // 打开模态面板/对话框后游戏会恢复光标（ComponentInput.cs:143-152），此时才可点。
            WidgetInput elementInput = widget.Input;
            bool mouseDerivable = elementInput != null && elementInput.IsMouseCursorVisible;
            element["clickable"] = hittable && mouseDerivable;
            element["clickReason"] = hittable
                ? (mouseDerivable
                    ? null
                    : "mouse cursor is captured (IsMouseCursorVisible=false); use keys instead")
                : "blocked by " + ShortName(hit);

            if (widget is ListPanelWidget listPanel)
                element["list"] = DescribeList(listPanel);
            if (widget is ButtonWidget button)
                element["checked"] = button.IsChecked;
            if (widget is ClickableWidget clickable)
                element["checked"] = clickable.IsChecked;
            if (widget is SliderWidget slider)
                element["value"] = slider.Value;

            return element;
        }

        // ---------------------------------------------------------------- 选择器解析

        /// <summary>
        /// 把选择器解析成控件。支持：完整 path、唯一 Name、唯一文本、当前枚举序号。
        /// 找不到 → element_missing；多个匹配 → ambiguous_selector（绝不猜）。
        /// </summary>
        public static Widget Resolve(
            ContainerWidget root, string selector, bool hasPoint, Vector2 point)
        {
            if (root == null)
                throw new BridgeCommandException("not_ready", "The UI is not ready yet.");
            if (string.IsNullOrWhiteSpace(selector))
            {
                throw new BridgeCommandException(
                    "invalid_argument", "An element selector is required (path, name, text or id).");
            }

            string wanted = selector.Trim();
            var all = new List<Widget>();
            foreach (Widget widget in root.AllChildren)
                all.Add(widget);

            var exactPaths = new List<Widget>();
            var names = new List<Widget>();
            var texts = new List<Widget>();
            for (int i = 0; i < all.Count; i++)
            {
                Widget widget = all[i];
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
                    texts.Add(widget);
                }
            }

            List<Widget> chosen;
            if (exactPaths.Count > 0)
                chosen = exactPaths;
            else if (names.Count > 0)
                chosen = names;
            else if (texts.Count > 0)
                chosen = texts;
            else if (int.TryParse(wanted, out int index) && index >= 1)
            {
                // 序号与 obs.ui 的默认列表同口径（已做过父子去重的可交互元素）。
                var listed = new List<Widget>();
                foreach (Widget widget in root.AllChildren)
                {
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

            if (chosen.Count == 0)
            {
                throw new BridgeCommandException(
                    "element_missing",
                    "No element matches '" + wanted + "' on the current screen.");
            }
            if (chosen.Count > 1)
            {
                throw new BridgeCommandException(
                    "ambiguous_selector",
                    "'" + wanted + "' matches " + chosen.Count + " elements; use the full path.");
            }

            return chosen[0];
        }

        // ---------------------------------------------------------------- 可达性报告

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
                Widget hit = root.HitTestGlobal(center);
                if (IsSelfOrDescendant(hit, widget))
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
                        ["blockedBy"] = ShortName(hit),
                        ["screenPoint"] = ToScreenPoint(center)
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

        /// <summary>几何上命中得到：HitTestGlobal(中心) 解析到该控件或其子控件。</summary>
        public static bool IsHittable(Widget widget)
        {
            if (widget == null)
                return false;
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
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
        /// 虚拟列表项导出。SC 的 ListPanelWidget 自绘条目（条目不是控件），
        /// 因此必须按"面板 + 索引"算出每一行可点击坐标。
        /// 行几何来自游戏自身：第 i 行位于 i*ItemSize - ScrollPosition，宽度为面板宽度。
        /// Source: Survivalcraft/Game/ListPanelWidget.cs:226-248, 287-295
        /// </summary>
        private static Dictionary<string, object> DescribeList(ListPanelWidget panel)
        {
            var block = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                ReadOnlyList<object> items = panel.Items;
                float itemSize = panel.ItemSize;
                float scroll = panel.ScrollPosition;
                Vector2 panelSize = panel.ActualSize;
                bool horizontal = (int)panel.Direction == 0;

                block["itemsCount"] = items.Count;
                block["itemSize"] = itemSize;
                block["scrollPosition"] = scroll;
                block["horizontal"] = horizontal;
                block["selectedIndex"] = panel.SelectedIndex;

                var rows = new List<Dictionary<string, object>>();
                int limit = Math.Min(items.Count, 64);
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
                        ["clientPoint"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["x"] = client.X,
                            ["y"] = client.Y
                        },
                        ["visibleInPanel"] = inside
                    };
                    Vector2? screenPoint = ToScreenPoint(client);
                    row["screenPoint"] = screenPoint.HasValue
                        ? new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["x"] = screenPoint.Value.X,
                            ["y"] = screenPoint.Value.Y
                        }
                        : null;
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
