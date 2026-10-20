using System;

namespace CmdBridgeMod
{
    /// <summary>分屏布局（`SettingsManager.ScreenLayout1..4` 的取值，名字与引擎枚举逐字一致）。</summary>
    internal enum SplitViewLayoutKind
    {
        Unknown = 0,
        Single,
        DoubleVertical,
        DoubleHorizontal,
        DoubleOpposite,
        TripleVertical,
        TripleHorizontal,
        TripleEven,
        TripleOpposite,
        Quadruple,
        QuadrupleOpposite
    }

    /// <summary>
    /// 一个分屏视图在 `GamesWidget` 里的单元格：位置/尺寸（`GamesWidget` 本地逻辑单位）
    /// 与 `LayoutTransform` 的线性部分（引擎 `Matrix` 的 M11/M12/M21/M22）。
    /// </summary>
    internal struct SplitViewCell
    {
        public SplitViewLayoutKind Kind;
        public int ChildCount;
        public int Index;

        /// <summary>单元格左上角（`GamesWidget` 本地坐标，未乘 `LayoutTransform`）。</summary>
        public float X;
        public float Y;

        /// <summary>单元格尺寸（同上）。引擎把它当作子控件的 `parentActualSize` 传进 `Widget.Arrange`。</summary>
        public float Width;
        public float Height;

        /// <summary>`LayoutTransform` 的线性部分（引擎 `Matrix` 的 M11/M12/M21/M22 顺序）。</summary>
        public float M11;
        public float M12;
        public float M21;
        public float M22;

        /// <summary>X 轴的缩放倍数（<c>sqrt(M11²+M12²)</c>）。</summary>
        public float ScaleX
        {
            get { return (float)Math.Sqrt(M11 * M11 + M12 * M12); }
        }

        /// <summary>Y 轴的缩放倍数（<c>sqrt(M21²+M22²)</c>）。</summary>
        public float ScaleY
        {
            get { return (float)Math.Sqrt(M21 * M21 + M22 * M22); }
        }

        /// <summary>
        /// 是否为 90° 旋转的视图（`DoubleOpposite` / `TripleOpposite` 的各视图）：
        /// 内容是转过来画的，AABB 的宽高与单元格的宽高互换，所以"相对quad"对这类视图不给结论。
        /// </summary>
        public bool Rotated
        {
            get { return Math.Abs(M11) < 0.0001f && Math.Abs(M22) < 0.0001f; }
        }
    }

    /// <summary>
    /// 分屏视图（`GamesWidget` → `GameWidget`）的**纯数学**描述：每个视图占哪个格子、缩放多少。
    ///
    /// 用它做什么、不做什么（这两条必须分清，否则就会写出"第二套坐标"）：
    ///   · **不做什么**：坐标与命中一律用引擎自己算好的 `Widget.GlobalBounds` / `Widget.HitTest`
    ///     （Source: `Survivalcraft/Game/Widget.cs:628-712`）—— 那才是窗口像素的唯一真相。
    ///     这里再算一遍"元素坐标"只会与引擎漂移。
    ///   · **做什么**：① `ui` 的 `views` 块要能说出"每个视图在屏幕上占哪块、谁是可点的主视图"；
    ///     ② 交叉核对引擎实际摆出来的 `GameWidget.GlobalBounds` 是否仍符合这份格子算式
    ///     （`cellAgrees`）—— 引擎布局/默认布局改了，这里会先亮红灯，而不是悄悄错位。
    ///
    /// 算式逐行照抄 `Survivalcraft/Game/GamesWidget.cs:ArrangeOverride`（2/3/4 视图共 8 种布局），
    /// 连 `m_spacing` 的取值（12 / 20）与缩放（0.5 / 0.48 / 窗口宽高比）都一致：
    ///   · 单元格 c2 = c1 + (w, h) / scale，于是子控件 `ActualSize` = (w, h) / scale
    ///     （`Widget.Arrange` 对纯缩放变换算出的 `ActualSize` 等于传进来的 `parentActualSize`）；
    ///   · 画出来占 `GamesWidget` 的 `(w, h)` —— 所以"相对quad" = `(w * scale) / GamesWidget 尺寸`。
    ///
    /// 单位：`width`/`height` 是 `GamesWidget.ActualSize`（引擎本地逻辑单位，不是像素）。
    /// 换算成窗口像素由引擎的全局变换完成（`RootWidget.LayoutTransform` 带 uiScale，
    /// Source: `Survivalcraft/Game/ScreensManager.cs:LayoutAndDrawWidgets`）。
    /// </summary>
    internal static class UiSplitView
    {
        /// <summary>引擎在 `GamesWidget.ArrangeOverride` 里用的间距（与源码逐字一致）。</summary>
        internal const float Spacing12 = 12f;

        internal const float Spacing20 = 20f;

        /// <summary>
        /// 布局名 → 枚举（名字与 `Game.ScreenLayout` 逐字一致，大小写不敏感）。
        ///
        /// 用**字面量 switch**而不是 `Enum.Parse`：`SettingsManager.ScreenLayoutX.ToString()`
        /// 给的是**引擎**枚举名，本枚举名只是"同名副本"；一旦本程序集被混淆器改写枚举成员名
        /// （`RenameEnums`），反射匹配就会在制品上悄悄失配。字面量不依赖任何反射。
        /// </summary>
        public static SplitViewLayoutKind ParseLayout(string name)
        {
            if (string.IsNullOrEmpty(name))
                return SplitViewLayoutKind.Unknown;
            switch (name.Trim().ToLowerInvariant())
            {
                case "single": return SplitViewLayoutKind.Single;
                case "doublevertical": return SplitViewLayoutKind.DoubleVertical;
                case "doublehorizontal": return SplitViewLayoutKind.DoubleHorizontal;
                case "doubleopposite": return SplitViewLayoutKind.DoubleOpposite;
                case "triplevertical": return SplitViewLayoutKind.TripleVertical;
                case "triplehorizontal": return SplitViewLayoutKind.TripleHorizontal;
                case "tripleeven": return SplitViewLayoutKind.TripleEven;
                case "tripleopposite": return SplitViewLayoutKind.TripleOpposite;
                case "quadruple": return SplitViewLayoutKind.Quadruple;
                case "quadrupleopposite": return SplitViewLayoutKind.QuadrupleOpposite;
                default: return SplitViewLayoutKind.Unknown;
            }
        }

        /// <summary>枚举 → 名字（写进 `obs.ui` 的 `views[].layout`；同样是字面量，不依赖反射）。</summary>
        public static string LayoutName(SplitViewLayoutKind kind)
        {
            switch (kind)
            {
                case SplitViewLayoutKind.Single: return "Single";
                case SplitViewLayoutKind.DoubleVertical: return "DoubleVertical";
                case SplitViewLayoutKind.DoubleHorizontal: return "DoubleHorizontal";
                case SplitViewLayoutKind.DoubleOpposite: return "DoubleOpposite";
                case SplitViewLayoutKind.TripleVertical: return "TripleVertical";
                case SplitViewLayoutKind.TripleHorizontal: return "TripleHorizontal";
                case SplitViewLayoutKind.TripleEven: return "TripleEven";
                case SplitViewLayoutKind.TripleOpposite: return "TripleOpposite";
                case SplitViewLayoutKind.Quadruple: return "Quadruple";
                case SplitViewLayoutKind.QuadrupleOpposite: return "QuadrupleOpposite";
                default: return "Unknown";
            }
        }

        /// <summary>该布局属于几个视图（`Unknown` → 0）。</summary>
        public static int ChildCountOf(SplitViewLayoutKind kind)
        {
            switch (kind)
            {
                case SplitViewLayoutKind.Single: return 1;
                case SplitViewLayoutKind.DoubleVertical:
                case SplitViewLayoutKind.DoubleHorizontal:
                case SplitViewLayoutKind.DoubleOpposite: return 2;
                case SplitViewLayoutKind.TripleVertical:
                case SplitViewLayoutKind.TripleHorizontal:
                case SplitViewLayoutKind.TripleEven:
                case SplitViewLayoutKind.TripleOpposite: return 3;
                case SplitViewLayoutKind.Quadruple:
                case SplitViewLayoutKind.QuadrupleOpposite: return 4;
                default: return 0;
            }
        }

        /// <summary>
        /// `GameWidget` 数量 → 该看哪个 `ScreenLayout` 设置，并校验"布局名与视图数一致"。
        /// 不一致（例如 3 个视图却读到 `Quadruple`）→ `Unknown`：引擎的 `ArrangeOverride`
        /// 那时一条分支都不会进，视图不会被摆到任何格子里 —— 必须如实报"布局未知"，不能猜。
        /// </summary>
        public static SplitViewLayoutKind ResolveLayout(int childCount,
            string layout1, string layout2, string layout3, string layout4)
        {
            string name;
            switch (childCount)
            {
                case 1: name = layout1; break;
                case 2: name = layout2; break;
                case 3: name = layout3; break;
                case 4: name = layout4; break;
                default: return SplitViewLayoutKind.Unknown;
            }
            SplitViewLayoutKind kind = ParseLayout(name);
            return ChildCountOf(kind) == childCount ? kind : SplitViewLayoutKind.Unknown;
        }

        /// <summary>
        /// 第 <paramref name="index"/> 个视图（= `GamesWidget.Children` 的下标，**不是**
        /// `GameWidget.GameWidgetIndex`）的单元格。视图数/布局/下标不合法 → false。
        ///
        /// `windowWidth`/`windowHeight` 只被 `DoubleOpposite` 用到（它拿 `Window.Size.Y / Window.Size.X`
        /// 当缩放，源码 `GamesWidget.cs:73`）。
        /// </summary>
        public static bool TryGetCell(SplitViewLayoutKind kind, int childCount, int index,
            float width, float height, float windowWidth, float windowHeight,
            out SplitViewCell cell)
        {
            cell = default(SplitViewCell);
            if (ChildCountOf(kind) != childCount || index < 0 || index >= childCount)
                return false;
            if (width <= 0f || height <= 0f)
                return false;
            if (kind == SplitViewLayoutKind.DoubleOpposite && (windowWidth <= 0f || windowHeight <= 0f))
                return false;

            cell.Kind = kind;
            cell.ChildCount = childCount;
            cell.Index = index;
            cell.M11 = 1f;
            cell.M22 = 1f;

            switch (kind)
            {
                case SplitViewLayoutKind.Single:
                    // Source: GamesWidget.cs:21-28 —— 单个视图占满，`ScreenLayout1 == Single` 时
                    // LayoutTransform 是单位矩阵（下面默认值就是）。
                    cell.X = 0f;
                    cell.Y = 0f;
                    cell.Width = width;
                    cell.Height = height;
                    return true;

                case SplitViewLayoutKind.DoubleVertical:
                {
                    // Source: GamesWidget.cs:31-46
                    const float scale = 0.5f;
                    float w = width / 2f - Spacing12 / 2f;
                    cell.M11 = scale;
                    cell.M22 = scale;
                    cell.X = index == 0 ? 0f : width / 2f + Spacing12 / 2f;
                    cell.Y = 0f;
                    cell.Width = w / scale;
                    cell.Height = height / scale;
                    return true;
                }

                case SplitViewLayoutKind.DoubleHorizontal:
                {
                    // Source: GamesWidget.cs:47-62
                    const float scale = 0.48f;
                    float h = height / 2f - Spacing12 / 2f;
                    cell.M11 = scale;
                    cell.M22 = scale;
                    cell.X = 0f;
                    cell.Y = index == 0 ? 0f : height / 2f + Spacing12 / 2f;
                    cell.Width = width / scale;
                    cell.Height = h / scale;
                    return true;
                }

                case SplitViewLayoutKind.DoubleOpposite:
                {
                    // Source: GamesWidget.cs:63-78 —— num3 = Window.Size.Y / Window.Size.X，
                    // LayoutTransform = [[0, num3], [-num3, 0]]（child0）/ [[0, -num3], [num3, 0]]（child1）。
                    float ratio = windowHeight / windowWidth;
                    float w = width / 2f - Spacing20 / 2f;
                    cell.X = index == 0 ? 0f : width / 2f + Spacing20 / 2f;
                    cell.Y = 0f;
                    cell.Width = w / ratio;
                    cell.Height = height / ratio;
                    if (index == 0)
                    {
                        cell.M11 = 0f;
                        cell.M12 = ratio;
                        cell.M21 = -ratio;
                        cell.M22 = 0f;
                    }
                    else
                    {
                        cell.M11 = 0f;
                        cell.M12 = -ratio;
                        cell.M21 = ratio;
                        cell.M22 = 0f;
                    }
                    return true;
                }

                case SplitViewLayoutKind.TripleVertical:
                {
                    // Source: GamesWidget.cs:84-102
                    const float scale = 0.5f;
                    float w = width / 2f - Spacing12 / 2f;
                    float h = height / 2f - Spacing12 / 2f;
                    cell.M11 = scale;
                    cell.M22 = scale;
                    if (index == 0)
                    {
                        cell.X = 0f;
                        cell.Y = 0f;
                        cell.Width = w / scale;
                        cell.Height = height / scale;
                    }
                    else
                    {
                        cell.X = width / 2f + Spacing12 / 2f;
                        cell.Y = index == 1 ? 0f : height / 2f + Spacing12 / 2f;
                        cell.Width = w / scale;
                        cell.Height = h / scale;
                    }
                    return true;
                }

                case SplitViewLayoutKind.TripleHorizontal:
                {
                    // Source: GamesWidget.cs:103-121 —— child0 占满宽度在**上**，
                    // child1 左下、child2 右下（实测 2026-10-20：三视图 1920x1080 时
                    // GameWidget#0 = (0,0,1920,530.5)、#1 = (0,549.5,950.5,530.5)、
                    // #2 = (969.5,549.5,950.5,530.5)，与本算式逐像素一致）。
                    const float scale = 0.5f;
                    float w = width / 2f - Spacing12 / 2f;
                    float h = height / 2f - Spacing12 / 2f;
                    cell.M11 = scale;
                    cell.M22 = scale;
                    if (index == 0)
                    {
                        cell.X = 0f;
                        cell.Y = 0f;
                        cell.Width = width / scale;
                        cell.Height = h / scale;
                    }
                    else
                    {
                        cell.X = index == 1 ? 0f : width / 2f + Spacing12 / 2f;
                        cell.Y = height / 2f + Spacing12 / 2f;
                        cell.Width = w / scale;
                        cell.Height = h / scale;
                    }
                    return true;
                }

                case SplitViewLayoutKind.TripleEven:
                {
                    // Source: GamesWidget.cs:122-139
                    const float scale = 0.5f;
                    float w = width / 2f - Spacing12 / 2f;
                    float h = height / 2f - Spacing12 / 2f;
                    cell.M11 = scale;
                    cell.M22 = scale;
                    if (index == 0)
                    {
                        cell.X = 0f;
                        cell.Y = 0f;
                    }
                    else if (index == 1)
                    {
                        cell.X = width / 2f + Spacing12 / 2f;
                        cell.Y = 0f;
                    }
                    else
                    {
                        cell.X = width / 4f + Spacing12 / 4f;
                        cell.Y = height / 2f + Spacing12 / 2f;
                    }
                    cell.Width = w / scale;
                    cell.Height = h / scale;
                    return true;
                }

                case SplitViewLayoutKind.TripleOpposite:
                {
                    // Source: GamesWidget.cs:140-158
                    const float scale = 0.5f;
                    float w = width / 2f - Spacing12 / 2f;
                    float h = height / 2f - Spacing12 / 2f;
                    if (index == 0)
                    {
                        cell.X = 0f;
                        cell.Y = 0f;
                        cell.Width = w / scale;
                        cell.Height = height / scale;
                        cell.M11 = 0f;
                        cell.M12 = scale;
                        cell.M21 = -scale;
                        cell.M22 = 0f;
                    }
                    else
                    {
                        cell.X = width / 2f + Spacing12 / 2f;
                        cell.Y = index == 1 ? 0f : height / 2f + Spacing12 / 2f;
                        cell.Width = w / scale;
                        cell.Height = h / scale;
                        if (index == 1)
                        {
                            cell.M11 = -scale;
                            cell.M12 = 0f;
                            cell.M21 = 0f;
                            cell.M22 = -scale;
                        }
                        else
                        {
                            cell.M11 = scale;
                            cell.M12 = 0f;
                            cell.M21 = 0f;
                            cell.M22 = scale;
                        }
                    }
                    return true;
                }

                case SplitViewLayoutKind.Quadruple:
                {
                    // Source: GamesWidget.cs:162-185
                    const float scale = 0.5f;
                    float w = width / 2f - Spacing12 / 2f;
                    float h = height / 2f - Spacing12 / 2f;
                    cell.M11 = scale;
                    cell.M22 = scale;
                    cell.X = (index == 0 || index == 2) ? 0f : width / 2f + Spacing12 / 2f;
                    cell.Y = index < 2 ? 0f : height / 2f + Spacing12 / 2f;
                    cell.Width = w / scale;
                    cell.Height = h / scale;
                    return true;
                }

                case SplitViewLayoutKind.QuadrupleOpposite:
                {
                    // Source: GamesWidget.cs:186-209 —— 格子与 Quadruple 相同，前两个视图旋转 180°。
                    const float scale = 0.5f;
                    float w = width / 2f - Spacing12 / 2f;
                    float h = height / 2f - Spacing12 / 2f;
                    cell.X = (index == 0 || index == 2) ? 0f : width / 2f + Spacing12 / 2f;
                    cell.Y = index < 2 ? 0f : height / 2f + Spacing12 / 2f;
                    cell.Width = w / scale;
                    cell.Height = h / scale;
                    float sign = index < 2 ? -scale : scale;
                    cell.M11 = sign;
                    cell.M12 = 0f;
                    cell.M21 = 0f;
                    cell.M22 = sign;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 这个视图在 `GamesWidget` 里占的**相对 quad**（0~1 的分数，可直接与
        /// `(GameWidget.GlobalBounds - GamesWidget.GlobalBounds) / GamesWidget 尺寸` 对比）。
        /// 旋转视图（`Cell.Rotated`）宽高互换、还要加上引擎的 `parentOffset`，这里不给结论 → false。
        /// </summary>
        public static bool TryGetRelativeQuad(SplitViewLayoutKind kind, int childCount, int index,
            float width, float height, float windowWidth, float windowHeight,
            out float x, out float y, out float quadWidth, out float quadHeight)
        {
            x = 0f;
            y = 0f;
            quadWidth = 0f;
            quadHeight = 0f;
            SplitViewCell cell;
            if (!TryGetCell(kind, childCount, index, width, height, windowWidth, windowHeight, out cell))
                return false;
            if (cell.Rotated)
                return false;
            x = cell.X / width;
            y = cell.Y / height;
            quadWidth = cell.Width * cell.ScaleX / width;
            quadHeight = cell.Height * cell.ScaleY / height;
            return true;
        }

        /// <summary>"没有视图"（不在世界里 / 视图表为空）。</summary>
        public const int NoView = int.MinValue;

        /// <summary>
        /// **主视图**的确定规则（`click` 默认点哪一个；也写进 `ui` 的 `views[].primary`）。
        ///
        /// 规则（唯一、确定性、与玩家在哪个视图上操作一致）：
        ///   1. 在所有**接受鼠标**的视图里取 `GameWidgetIndex` 最小的那个 —— 引擎里只有
        ///      `Devices` 含 `Mouse`/`MultiMice` 的输入面才可能派生 `Click`
        ///      （Source: `Survivalcraft/Game/WidgetInput.cs:152-166`、`:741`），
        ///      所以"能用鼠标点的视图"才是 `click` 说得上话的视图；
        ///   2. 一个都没有（例如引擎还没跑第一帧、`WidgetsHierarchyInput` 还是 null）→
        ///      取 `GameWidgetIndex` 最小的那个。
        /// 空列表 → <see cref="NoView"/>。
        /// </summary>
        public static int SelectPrimaryView(int[] gameWidgetIndices, bool[] acceptsMouse)
        {
            if (gameWidgetIndices == null || gameWidgetIndices.Length == 0)
                return NoView;
            int lowest = int.MaxValue;
            int lowestMouse = int.MaxValue;
            for (int i = 0; i < gameWidgetIndices.Length; i++)
            {
                int index = gameWidgetIndices[i];
                if (index < 0)
                    continue;
                if (index < lowest)
                    lowest = index;
                if (acceptsMouse != null && i < acceptsMouse.Length && acceptsMouse[i]
                    && index < lowestMouse)
                {
                    lowestMouse = index;
                }
            }
            if (lowestMouse != int.MaxValue)
                return lowestMouse;
            return lowest == int.MaxValue ? NoView : lowest;
        }

        /// <summary>
        /// 同名元素在多个视图里各有一份时，选哪一个：**只认主视图**。
        ///
        /// 返回 `-1` = 不做唯一化（候选全在同一个视图里，或者主视图里没有/不止一个候选 ——
        /// 后者是"主视图里还有歧义"，交给调用方原有的同名消歧与 `ambiguous_selector` 报错）。
        /// 这样单视图行为**完全不变**（所有候选的 gameWidget 相同 → 返回 -1）。
        /// </summary>
        public static int SelectInstanceIndex(int[] candidateGameWidgets, int primaryGameWidget)
        {
            if (candidateGameWidgets == null || candidateGameWidgets.Length <= 1)
                return -1;
            if (primaryGameWidget == NoView)
                return -1;

            // 候选是不是跨了多个视图（`-1` = 不属于任何视图，例如菜单/对话框，自成一"组"）。
            int first = candidateGameWidgets[0];
            bool multipleViews = false;
            for (int i = 1; i < candidateGameWidgets.Length; i++)
            {
                if (candidateGameWidgets[i] != first)
                {
                    multipleViews = true;
                    break;
                }
            }
            if (!multipleViews)
                return -1;   // 全在同一个视图里（含单视图、含全都不属于视图）：行为完全不变

            int inPrimary = 0;
            int primaryPosition = -1;
            for (int i = 0; i < candidateGameWidgets.Length; i++)
            {
                if (candidateGameWidgets[i] != primaryGameWidget)
                    continue;
                inPrimary++;
                primaryPosition = i;
            }
            if (inPrimary != 1)
                return -1;   // 主视图里没有，或多于一个（视图内歧义）：不猜，交给调用方报错
            return primaryPosition;
        }
    }
}
