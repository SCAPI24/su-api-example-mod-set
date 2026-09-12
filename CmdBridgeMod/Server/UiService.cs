using Engine;
using Engine.Input;
using Game;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace CmdBridgeMod
{
    /// <summary>
    /// **UI 定位 / 点击服务**（用户要求："把相应的方法做成 CmdBridgeMod 能提供的服务，
    /// 在行为树编辑器中，要能够使用来获取坐标或点击对象"）。
    ///
    /// 对外就是两条命令（`ui.locate` / `ui.click`），编辑器的 `/api/game/ui/*` 直接转发它们，
    /// 行为树的 `Task.UiClick` 也走同一条路 —— 于是"编辑器里试一下"和"树里跑一下"用的是同一份实现，
    /// 不会出现"编辑器能点、回放点空"的错位。
    ///
    /// 三种点击方式（`mode`）：
    ///
    /// | mode | 怎么点 | 用在哪 | 代价 |
    /// |------|--------|--------|------|
    /// | `direct`（默认） | **单帧合成"按下→抬起"**：只往输入层的 `m_mouseDownOnce` 数组 + `m_mouseDownPoint/m_mouseDownButton` + 软光标位置写一次，引擎自己的 `UpdateInputFromMouse` 派生 `Tap`+`Click` | 编辑器试点击、回放、行为树 | 一帧；不依赖光标活过好几帧 |
    /// | `invoke` | **直接触发控件自己的按下事件**（`ListPanelWidget.ItemClicked`，必要时先摆好它处理器的前置状态） | 明确要"直接发事件"的场合 | **绕过输入层**，AI 默认不用 |
    /// | `input` | 多步软光标会话（移动 → 按下 → 抬起，一步一帧） | 对照 / 兼容旧行为 | 跨 5 帧，依赖会话不被打断 |
    ///
    /// 为什么 `direct` 是"单帧"而不是"同帧按下再抬起"：引擎的 `Click` 派生要求
    /// "这一帧没按着 + 上一帧留了按下点"（Source: Game/WidgetInput.cs:755-769）。
    /// 同帧把 down 数组按下又抬起，两件事在同一帧里看不到先后 —— 于是什么也派生不出来
    /// （早期"直注入点击没反应"就是这个原因）。把"按过一下"只写进 **downOnce** 数组、
    /// down 数组保持没按，`Tap` 与 `Click` 就会在这一帧同时成立，和真人快速点一下完全等价。
    /// </summary>
    internal sealed class UiService
    {
        private readonly InputInjector m_injector;

        public UiService(InputInjector injector)
        {
            m_injector = injector ?? throw new ArgumentNullException(nameof(injector));
        }

        // ---------------------------------------------------------------- 定位

        /// <summary>
        /// 解析语义目标 → **当前真实的**坐标与可点性。
        /// 返回值就是命令的 `result`，字段命名与 `obs.ui` 对齐，编辑器前端可以直接复用。
        ///
        /// `mark` 打开时顺便在游戏里那个像素上亮一个红点（默认 2 秒 / 5 像素）——
        /// 编辑器点「定位」时用，人眼一眼就能看出"它认为目标在哪"。
        /// </summary>
        public Dictionary<string, object> Locate(string target, bool mark, float markSeconds,
            float markPixels)
        {
            return (Dictionary<string, object>)m_injector.OnGameThreadForUi(delegate
            {
                Dictionary<string, object> located = LocateCore(target);
                if (mark)
                    MarkLocation(located, markSeconds, markPixels);
                return located;
            });
        }

        /// <summary>在定位结果那个像素上亮一下标记（`mark` 开关用）。</summary>
        internal void MarkLocation(Dictionary<string, object> located, float markSeconds,
            float markPixels)
        {
            if (located == null)
                return;
            Vector2 point;
            if (!TryPoint(located, out point))
                return;

            // 屏幕外的点不画：画了也看不见，还会让人以为"标记功能坏了"。
            ContainerWidget root = ScreensManager.RootWidget;
            if (root != null && !root.GlobalBounds.Contains(point))
            {
                located["marked"] = false;
                located["markSkipped"] = "落点在屏幕外（"
                    + point.X.ToString("0.#") + "," + point.Y.ToString("0.#")
                    + "），没有画标记 —— 先看这个控件是不是被折叠/隐藏了";
                return;
            }

            bool shown = UiMarker.Show(m_injector, point,
                markSeconds > 0f ? markSeconds : UiMarker.DefaultSeconds,
                markPixels > 0f ? markPixels : UiMarker.DefaultDiameterPixels);
            located["marked"] = shown;
            if (shown)
            {
                located["markSeconds"] = markSeconds > 0f ? markSeconds : UiMarker.DefaultSeconds;
                located["markPixels"] = markPixels > 0f ? markPixels : UiMarker.DefaultDiameterPixels;
            }
        }

        private static bool TryPoint(Dictionary<string, object> located, out Vector2 point)
        {
            point = default(Vector2);
            try
            {
                point = PointOf(located);
                return true;
            }
            catch (BridgeCommandException)
            {
                return false;
            }
        }

        /// <summary>游戏线程内的定位实现（`Click` 与命令层共用）。</summary>
        internal Dictionary<string, object> LocateCore(string target)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["target"] = target;

            UiTarget.Parsed parsed;
            if (!UiTarget.TryParse(target, out parsed))
            {
                throw new BridgeCommandException("invalid_argument",
                    "Cannot parse the UI target '" + (target ?? "<null>") + "'.");
            }
            result["kind"] = parsed.Kind.ToString();

            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                throw new BridgeCommandException("not_ready", "The UI is not ready yet.");

            Vector2 point;
            Widget widget;

            if (parsed.Kind == UiTarget.TargetKind.Point)
            {
                // 坐标兜底：点在哪就点哪，能命中什么就报什么（不假装它是某个控件）。
                point = new Vector2(parsed.X, parsed.Y);
                widget = root.HitTestGlobal(point);
                result["pointSource"] = "given";
                var described = widget != null
                    ? UiInspector.DescribeElement(widget, 0)
                    : new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> pair in described)
                    result[pair.Key] = pair.Value;
                result["hittable"] = widget != null;
                result["clickable"] = widget != null && UiInspector.IsHittable(widget);
                DescribePoint(result, point);
                result["warning"] = "坐标目标会随窗口尺寸/UI 缩放失配，能用控件名或 list: 就别用坐标。";
                return result;
            }

            if (parsed.Kind == UiTarget.TargetKind.ListRow)
            {
                Widget list = UiInspector.Resolve(root, parsed.Selector, false, default(Vector2));
                if (list == null)
                {
                    throw new BridgeCommandException("element_missing",
                        "No list matches '" + parsed.Selector + "'.");
                }

                Vector2 rowPoint;
                int index;
                string text;
                if (!UiInspector.TryResolveListRow(list, parsed.RowIndex, parsed.RowText,
                    out rowPoint, out index, out text))
                {
                    var listInfo = UiInspector.DescribeElement(list, 0);
                    string hint = ListHint(listInfo);
                    throw new BridgeCommandException("element_missing",
                        "No such row in '" + parsed.Selector + "' (row=" + parsed.RowIndex
                        + " text=" + (parsed.RowText ?? "<none>") + ")." + hint);
                }

                point = rowPoint;
                widget = root.HitTestGlobal(point);
                result["list"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["selector"] = parsed.Selector,
                    ["index"] = index,
                    ["text"] = text,
                    ["count"] = ListCount(list)
                };
                var describedList = UiInspector.DescribeElement(list, 0);
                result["name"] = describedList["name"];
                result["path"] = describedList["path"];
                result["type"] = describedList["type"];
                result["text"] = text;
                result["pointSource"] = "listRow";
                result["hittable"] = widget != null;
                result["clickable"] = widget != null && UiInspector.IsHittable(widget);
                result["hitTargetType"] = widget != null ? widget.GetType().Name : null;
                DescribePoint(result, point);
                return result;
            }

            widget = UiInspector.Resolve(root, parsed.Selector, false, default(Vector2));
            if (widget == null)
            {
                throw new BridgeCommandException("element_missing",
                    "No element matches '" + parsed.Selector + "'.");
            }

            point = UiInspector.CenterOf(widget);
            var element = UiInspector.DescribeElement(widget, 0);
            foreach (KeyValuePair<string, object> pair in element)
                result[pair.Key] = pair.Value;
            result["pointSource"] = "element";
            DescribePoint(result, point);
            return result;
        }

        private static void DescribePoint(Dictionary<string, object> result, Vector2 point)
        {
            result["clientPoint"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = point.X,
                ["y"] = point.Y
            };
            Vector2? screen = UiInspector.ToScreenPoint(point);
            result["screenPoint"] = screen.HasValue
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = screen.Value.X,
                    ["y"] = screen.Value.Y
                }
                : null;
        }

        private static string ListHint(Dictionary<string, object> listInfo)
        {
            object list;
            if (!listInfo.TryGetValue("list", out list))
                return string.Empty;
            var info = list as Dictionary<string, object>;
            if (info == null)
                return string.Empty;
            object items;
            if (!info.TryGetValue("items", out items))
                return string.Empty;
            var rows = items as List<Dictionary<string, object>>;
            if (rows == null || rows.Count == 0)
                return " The list is empty right now.";

            var names = new List<string>();
            for (int i = 0; i < rows.Count && i < 12; i++)
            {
                object text;
                rows[i].TryGetValue("text", out text);
                names.Add(text as string ?? "<null>");
            }
            return " Current rows: " + string.Join(" | ", names.ToArray());
        }

        private static int ListCount(Widget widget)
        {
            var panel = widget as ListPanelWidget;
            return panel != null ? panel.Items.Count : 0;
        }

        // ---------------------------------------------------------------- 点击

        /// <summary>
        /// 按语义目标点击。`mode`：`direct`（默认）/ `invoke` / `input` / `auto`
        /// （`auto` = 能发事件就发事件，否则单帧合成）。
        /// `mark` 打开时在点的那个像素上亮一下标记（编辑器"点一下"用；AI 回放不要开，免得刷屏）。
        /// </summary>
        public Dictionary<string, object> Click(string target, string mode, int holdMilliseconds,
            bool mark, float markSeconds, float markPixels)
        {
            string wanted = string.IsNullOrEmpty(mode) ? "direct" : mode.Trim().ToLowerInvariant();
            if (wanted == "auto")
                wanted = "invoke-first";

            string used = wanted;
            object invoked = null;

            if (wanted == "invoke-first" || wanted == "invoke")
            {
                object attempt = m_injector.OnGameThreadForUi(delegate
                {
                    return TryInvokeCore(target);
                });
                var info = attempt as Dictionary<string, object>;
                if (info != null)
                {
                    invoked = info;
                    used = "invoke";
                }
                else if (wanted == "invoke")
                {
                    throw new BridgeCommandException("invoke_unavailable",
                        "The target has no click event to raise; use mode=direct instead.");
                }
                else
                {
                    used = "direct";
                }
            }
            else if (wanted != "direct" && wanted != "input")
            {
                throw new BridgeCommandException("invalid_argument",
                    "mode must be direct, invoke, auto or input (got '" + mode + "').");
            }

            if (used == "invoke")
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                var info = invoked as Dictionary<string, object>;
                foreach (KeyValuePair<string, object> pair in info)
                    result[pair.Key] = pair.Value;
                result["mode"] = "invoke";
                result["modeReason"] = "控件自己的按下事件被直接触发（绕过输入层）";
                if (mark)
                {
                    var located = (Dictionary<string, object>)m_injector.OnGameThreadForUi(delegate
                    {
                        Dictionary<string, object> found = LocateCore(target);
                        MarkLocation(found, markSeconds, markPixels);
                        return found;
                    });
                    object marked;
                    if (located.TryGetValue("marked", out marked))
                        result["marked"] = marked;
                }
                return result;
            }

            if (used == "input")
                return ClickViaSession(target, holdMilliseconds);

            var direct = (Dictionary<string, object>)m_injector.OnGameThreadForUi(delegate
            {
                Dictionary<string, object> clicked = ClickDirectCore(target);
                if (mark)
                    MarkLocation(clicked, markSeconds, markPixels);
                return clicked;
            });
            return direct;
        }

        /// <summary>
        /// 只求"点在哪"（行为树 / 门面的非阻塞路径用）：语义目标 → **当前**客户区坐标。
        /// 必须在游戏线程、且在点击那一刻调用（坐标随窗口尺寸与 UI 缩放变）。
        /// </summary>
        internal Vector2 ResolvePointCore(string target)
        {
            return PointOf(LocateCore(target));
        }

        /// <summary>
        /// 求"要点的那个控件"：**输入面必须取自它**。
        ///
        /// 引擎里每个 `WidgetsHierarchyInput` 是独立一份输入面：主菜单的屏用根输入面，
        /// 世界内 HUD 用 `GameWidget` 那层自己的输入面（`GameWidget.cs:104-106`）。
        /// 软光标位置 / `IsMouseCursorVisible` / `m_mouseDownPoint` 都是**每面一份**，
        /// 写错面 = 那一层什么都没变 = 点不动（实测：世界内 MoreButton）。
        /// 取不到（没命中任何控件）时返回 null，由调用方退回根输入面。
        /// </summary>
        internal Widget ResolveTargetCore(string target)
        {
            Dictionary<string, object> located = LocateCore(target);
            ContainerWidget root = ScreensManager.RootWidget;
            return root != null ? root.HitTestGlobal(PointOf(located)) : null;
        }

        /// <summary>
        /// 单帧合成点击：解析目标 → 把"按过一下"写进**目标那一层**的输入面
        /// → 引擎自己在同一帧派生 `Tap`+`Click`。
        /// </summary>
        internal Dictionary<string, object> ClickDirectCore(string target)
        {
            Dictionary<string, object> located = LocateCore(target);
            Vector2 point = PointOf(located);

            ContainerWidget root = ScreensManager.RootWidget;
            Widget widget = root != null ? root.HitTestGlobal(point) : null;
            if (widget == null)
            {
                // 点不到就是点不到：**绝不**改成"点屏幕角落"糊过去。
                // 用户实测踩到的正是这里：世界内 HUD 那条触屏控制栏在 Windows 下被平移到屏幕外，
                // 它的中心 (2852,55) 落在屏幕外，以前这里会照点，于是命中左上角某个控件 ——
                // 用户看到"点了一下，什么都没发生"，而且回包还写着命中了别的控件。
                throw new BridgeCommandException("element_off_screen",
                    "目标现在的落点 (" + point.X.ToString("0.#") + "," + point.Y.ToString("0.#")
                    + ") 上什么都没有：多半是屏幕外/被折叠的控件（例如 Windows 上触屏专用的 HUD 控制栏），"
                    + "或者它还没被布局。先用「定位」看它的真实坐标与不可点原因。");
            }





            // 落点要落在"目标自己或它内部的可点件"上：按钮中心常盖着图标子控件，直接点中心会点空。
            Widget wanted = ResolveElementCore(target) ?? widget;
            Vector2 clickPoint = PickClickPoint(root, wanted, point);
            bool adjusted = (clickPoint - point).Length() > 0.01f;
            Widget hitAtClick = root != null ? (root.HitTestGlobal(clickPoint) ?? widget) : widget;

            // 引擎在"窗口不活跃"时整段输入都不算（`WidgetInput.Update()` 里 `if (Window.IsActive)`）——
            // 这时合成点击会被**无声无息地丢掉**。共控模式（focus.attach）不强制活跃，
            // 所以真实焦点不在游戏里时必然是这个状态（实测：刚启动游戏、窗口还没拿到焦点时点 Play 点不动）。
            // 与其让用户以为"功能坏了"，不如如实拒绝并说清怎么让它能点。
            if (!Window.IsActive)
            {
                throw new BridgeCommandException("window_not_active",
                    "游戏窗口现在不是活跃窗口（引擎认为它不在前台），输入会被整段忽略："
                    + "让游戏窗口拿到焦点，或用 focus.detach / focus.auto 让本 Mod 接管输入，再点一次。");
            }
            m_injector.ApplyDirectClick(clickPoint, hitAtClick);
            located["mode"] = "direct";
            located["modeReason"] = "单帧合成 Tap+Click（引擎自己的控件逻辑照常跑）";
            located["clickPoint"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = clickPoint.X,
                ["y"] = clickPoint.Y
            };
            if (adjusted)
                located["clickPointAdjusted"] = true;
            located["clickTarget"] = hitAtClick.Name ?? hitAtClick.GetType().Name;
            located["clickTargetOnTarget"] = IsClickConsumer(hitAtClick, wanted);
            return located;
        }

        /// <summary>
        /// 这个命中结果"算不算点到了目标"：要么就是它自己，要么是它子孙里的 `ClickableWidget`
        /// （复合按钮的内部可点件）。
        /// </summary>
        internal static bool IsClickConsumer(Widget hit, Widget target)
        {
            if (hit == null || target == null)
                return false;
            for (Widget current = hit; current != null; current = current.ParentWidget)
            {
                if (ReferenceEquals(current, target))
                    return hit is ClickableWidget || ReferenceEquals(hit, target);
            }
            return false;
        }

        /// <summary>
        /// 解析"目标控件**本身**"（不是它中心盖着的那个子控件）。
        /// 点按钮时要的是按钮、或它内部那个真正的可点件，所以这个才是"想在哪儿点"的参照物。
        /// </summary>
        internal Widget ResolveElementCore(string target)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                return null;
            UiTarget.Parsed parsed;
            if (!UiTarget.TryParse(target, out parsed))
                return null;
            if (parsed.Kind == UiTarget.TargetKind.Selector)
                return UiInspector.Resolve(root, parsed.Selector, false, default(Vector2));
            Vector2 point = PointOf(LocateCore(target));
            return root.HitTestGlobal(point);
        }

        /// <summary>
        /// 在目标控件里挑一个"真的会被消费"的落点。
        ///
        /// 引擎里有两种形状，**别按同一种处理**：
        ///   · `ClickableWidget` 自己就是被点的那一个：`ClickableWidget.Update` 判 `HitTestGlobal(...) == this`
        ///     （`ClickableWidget.cs:39`），所以必须正好命中它自己；
        ///   · 复合按钮（`BitmapButtonWidget` 这类 `ButtonWidget`）内部**包着一个** `ClickableWidget`
        ///     （`BitmapButtonWidget.cs:12,18`：`IsClicked => m_clickableWidget.IsClicked`），
        ///     真正要被命中的是那个**子控件** —— 它没有名字，所以日志里看着像"点错了"。
        ///
        /// 所以规则是：落点命中"目标自己或其子孙里的 ClickableWidget"就算对；中心不行就在矩形里
        /// 5×5 网格扫一圈（内缩 15%）；都不行才退回中心（调用方会如实标注）。
        /// </summary>
        internal static Vector2 PickClickPoint(ContainerWidget root, Widget target, Vector2 fallback)
        {
            if (root == null || target == null)
                return fallback;
            if (IsClickConsumer(root.HitTestGlobal(fallback), target))
                return fallback;

            BoundingRectangle bounds = target.GlobalBounds;
            float width = bounds.Max.X - bounds.Min.X;
            float height = bounds.Max.Y - bounds.Min.Y;
            if (width <= 0.001f || height <= 0.001f)
                return fallback;

            for (int row = 0; row < 5; row++)
            {
                for (int column = 0; column < 5; column++)
                {
                    float fx = 0.15f + 0.175f * column;
                    float fy = 0.15f + 0.175f * row;
                    var candidate = new Vector2(bounds.Min.X + width * fx, bounds.Min.Y + height * fy);
                    if (ReferenceEquals(root.HitTestGlobal(candidate), target))
                        return candidate;
                }
            }
            return fallback;
        }

        /// <summary>老的软光标会话（移动 → 按下 → 抬起，一步一帧）。</summary>
        private Dictionary<string, object> ClickViaSession(string target, int holdMilliseconds)
        {
            UiTarget.Parsed parsed;
            if (!UiTarget.TryParse(target, out parsed))
            {
                throw new BridgeCommandException("invalid_argument",
                    "Cannot parse the UI target '" + (target ?? "<null>") + "'.");
            }

            // 复用 `act.uiclick` 那条已经验证过的路（会话 + 现算坐标）
            object clicked;
            switch (parsed.Kind)
            {
                case UiTarget.TargetKind.ListRow:
                    clicked = m_injector.UiClickSession(parsed.Selector, false, 0f, 0f,
                        parsed.RowIndex, parsed.RowText, holdMilliseconds);
                    break;
                case UiTarget.TargetKind.Point:
                    clicked = m_injector.UiClickSession(null, true, parsed.X, parsed.Y, -1, null,
                        holdMilliseconds);
                    break;
                default:
                    clicked = m_injector.UiClickSession(parsed.Selector, false, 0f, 0f, -1, null,
                        holdMilliseconds);
                    break;
            }

            var result = clicked as Dictionary<string, object>;
            if (result == null)
            {
                throw new BridgeCommandException("failed",
                    "The UI click session did not return a result.");
            }
            result["mode"] = "input";
            result["modeReason"] = "多帧软光标会话（移动→按下→抬起）";
            return result;
        }

        private static Vector2 PointOf(Dictionary<string, object> located)
        {
            object value;
            if (located.TryGetValue("clientPoint", out value))
            {
                var point = value as Dictionary<string, object>;
                if (point != null)
                {
                    float x;
                    float y;
                    if (TryNumber(point, "x", out x) && TryNumber(point, "y", out y))
                        return new Vector2(x, y);
                }
            }
            throw new BridgeCommandException("failed", "The located target carries no clientPoint.");
        }

        private static bool TryNumber(Dictionary<string, object> source, string key, out float value)
        {
            value = 0f;
            object raw;
            if (!source.TryGetValue(key, out raw) || raw == null)
                return false;
            if (raw is float)
            {
                value = (float)raw;
                return true;
            }
            if (raw is double)
            {
                value = (float)(double)raw;
                return true;
            }
            if (raw is int)
            {
                value = (int)raw;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 直接触发控件事件

        /// <summary>
        /// 能直接"发出按下"的就直接发：目前引擎里唯一带点击事件的是
        /// `ListPanelWidget.ItemClicked`（`Game/ListPanelWidget.cs:122`）。
        ///
        /// ⚠️ 两个坑（都踩过/查过源码）：
        ///   1. `PlayScreen` 的处理器写着 `if (selectedItem == item) Play(item)` ——
        ///      `SelectedItem` 是 `ListPanelWidget.Update` 在**发事件之后**才更新的
        ///      （`ListPanelWidget.cs:258-269`）。所以只发事件**不会**进世界：
        ///      必须先把 `SelectedIndex` 摆到这一行，再发事件，才等价于"用户点了这一行"。
        ///   2. 因此 `invoke` 实质上替界面做了"选中"这个决定 —— 它是**绕过输入层**的，
        ///      默认不给 AI 用（AI 用 `direct`：引擎自己跑完整逻辑）。
        /// </summary>
        internal Dictionary<string, object> TryInvokeCore(string target)
        {
            UiTarget.Parsed parsed;
            if (!UiTarget.TryParse(target, out parsed) || parsed.Kind != UiTarget.TargetKind.ListRow)
                return null;

            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                throw new BridgeCommandException("not_ready", "The UI is not ready yet.");

            Widget listWidget = UiInspector.Resolve(root, parsed.Selector, false, default(Vector2));
            var panel = listWidget as ListPanelWidget;
            if (panel == null)
                return null;

            ReadOnlyList<object> items = panel.Items;
            int index = -1;
            if (!string.IsNullOrEmpty(parsed.RowText))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    string text = UiInspector.DescribeListItemText(items[i]);
                    if (text != null && text.IndexOf(parsed.RowText, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        index = i;
                        break;
                    }
                }
            }
            else if (parsed.RowIndex >= 0 && parsed.RowIndex < items.Count)
            {
                index = parsed.RowIndex;
            }

            if (index < 0)
            {
                throw new BridgeCommandException("element_missing",
                    "No such row in '" + parsed.Selector + "' (row=" + parsed.RowIndex
                    + " text=" + (parsed.RowText ?? "<none>") + ")."
                    + ListHint(UiInspector.DescribeElement(panel, 0)));
            }

            EventInfo clicked = typeof(ListPanelWidget).GetEvent("ItemClicked");
            if (clicked == null)
                return null;

            panel.SelectedIndex = index;        // 先摆好处理器的前置状态（见上面的坑 1）
            Delegate handler = GetEventHandler(panel, "ItemClicked");
            if (handler == null)
            {
                throw new BridgeCommandException("invoke_unavailable",
                    "Nothing is listening to this list's ItemClicked event.");
            }

            object item = items[index];
            handler.DynamicInvoke(item);

            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["name"] = panel.Name;
            result["path"] = UiInspector.BuildPath(panel);
            result["type"] = panel.GetType().Name;
            result["text"] = UiInspector.DescribeListItemText(item);
            result["list"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["selector"] = parsed.Selector,
                ["index"] = index,
                ["text"] = UiInspector.DescribeListItemText(item),
                ["count"] = items.Count
            };
            return result;
        }

        /// <summary>取某个事件的委托（`ItemClicked` 这类字段式事件）。</summary>
        private static Delegate GetEventHandler(object target, string eventName)
        {
            Type type = target.GetType();
            FieldInfo field = type.GetField(eventName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
                return null;
            return field.GetValue(target) as Delegate;
        }
    }
}
