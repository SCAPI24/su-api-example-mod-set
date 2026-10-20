using System;

namespace CmdBridgeMod.Tests
{
    /// <summary>
    /// 分屏视图坐标/选择规则的自检（无外部依赖，退出码即结论）。
    ///
    /// 为什么这些断言值得写：本次故障的全部症状都落在两个纯函数上 ——
    ///   · `UiSplitView.TryGetCell` / `TryGetRelativeQuad` 描述"每个视图在 `GamesWidget` 里占哪块"；
    ///     它是 `ui` 的 `views[]` 与 `cellAgrees` 的唯一算式来源，也是"元素坐标属于哪个视图"的判据；
    ///   · `UiSplitView.SelectPrimaryView` / `SelectInstanceIndex` 描述"`click` 默认点哪一份"，
    ///     必须是确定性的，并且**单视图时一个元素都不能被它改掉**。
    /// 引擎源码就是这些数字的唯一依据（`Survivalcraft/Game/GamesWidget.cs:19-215`），
    /// 下面每组期望值都是照着那段源码手算出来的，不是从实现里抄的。
    /// </summary>
    internal static class Program
    {
        private static int s_failures;

        private static int Main()
        {
            TestSingle();
            TestDouble();
            TestTriple();
            TestTripleVerifiedAgainstLiveEngine();
            TestQuadruple();
            TestResolveLayoutGuards();
            TestPrimaryViewRule();
            TestInstanceSelection();
            TestSingleViewUnchanged();

            Console.WriteLine(s_failures == 0
                ? "PASS: all UiSplitView checks passed"
                : "FAIL: " + s_failures + " check(s) failed");
            return s_failures == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- 1 视图

        private static void TestSingle()
        {
            SplitViewCell cell;
            bool ok = UiSplitView.TryGetCell(SplitViewLayoutKind.Single, 1, 0, 1000f, 600f,
                1920f, 1080f, out cell);
            Check("single: cell exists", ok);
            CheckCell("single: full size", cell, 0f, 0f, 1000f, 600f);
            Check("single: identity transform",
                Near(cell.M11, 1f) && Near(cell.M12, 0f) && Near(cell.M21, 0f) && Near(cell.M22, 1f));
            Check("single: scale 1", Near(cell.ScaleX, 1f) && Near(cell.ScaleY, 1f));
            Check("single: not rotated", !cell.Rotated);

            float x, y, w, h;
            ok = UiSplitView.TryGetRelativeQuad(SplitViewLayoutKind.Single, 1, 0, 1000f, 600f,
                1920f, 1080f, out x, out y, out w, out h);
            Check("single: relative quad is the whole GamesWidget",
                ok && Near(x, 0f) && Near(y, 0f) && Near(w, 1f) && Near(h, 1f));

            Check("single: out-of-range index rejected",
                !UiSplitView.TryGetCell(SplitViewLayoutKind.Single, 1, 1, 1000f, 600f,
                    1920f, 1080f, out cell));
        }

        // ---------------------------------------------------------------- 2 视图

        private static void TestDouble()
        {
            SplitViewCell cell;

            // DoubleVertical：spacing 12、scale 0.5、左右各半
            // Source: GamesWidget.cs:31-46
            UiSplitView.TryGetCell(SplitViewLayoutKind.DoubleVertical, 2, 0, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("double-vertical: left cell", cell, 0f, 0f, 988f, 1200f);
            Check("double-vertical: scale 0.5", Near(cell.ScaleX, 0.5f) && Near(cell.ScaleY, 0.5f));
            UiSplitView.TryGetCell(SplitViewLayoutKind.DoubleVertical, 2, 1, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("double-vertical: right cell", cell, 506f, 0f, 988f, 1200f);

            // DoubleHorizontal：spacing 12、scale 0.48、上下各半
            // Source: GamesWidget.cs:47-62
            UiSplitView.TryGetCell(SplitViewLayoutKind.DoubleHorizontal, 2, 0, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("double-horizontal: top cell", cell, 0f, 0f, 1000f / 0.48f, 294f / 0.48f);
            Check("double-horizontal: scale 0.48", Near(cell.ScaleX, 0.48f));
            UiSplitView.TryGetCell(SplitViewLayoutKind.DoubleHorizontal, 2, 1, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("double-horizontal: bottom cell",
                cell, 0f, 306f, 1000f / 0.48f, 294f / 0.48f);

            // DoubleOpposite：spacing 20、缩放 = 窗口高/宽、90° 旋转
            // Source: GamesWidget.cs:63-78
            float ratio = 1080f / 1920f;
            UiSplitView.TryGetCell(SplitViewLayoutKind.DoubleOpposite, 2, 0, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("double-opposite: left cell", cell, 0f, 0f, 490f / ratio, 600f / ratio);
            Check("double-opposite: rotated", cell.Rotated);
            Check("double-opposite: transform",
                Near(cell.M11, 0f) && Near(cell.M12, ratio) && Near(cell.M21, -ratio)
                && Near(cell.M22, 0f));
            UiSplitView.TryGetCell(SplitViewLayoutKind.DoubleOpposite, 2, 1, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("double-opposite: right cell", cell, 510f, 0f, 490f / ratio, 600f / ratio);
            Check("double-opposite: mirrored transform",
                Near(cell.M12, -ratio) && Near(cell.M21, ratio));

            Check("double-opposite: needs a window size",
                !UiSplitView.TryGetCell(SplitViewLayoutKind.DoubleOpposite, 2, 0, 1000f, 600f,
                    0f, 0f, out cell));
        }

        // ---------------------------------------------------------------- 3 视图

        private static void TestTriple()
        {
            SplitViewCell cell;

            // TripleVertical：左半满高 + 右上 + 右下（Source: GamesWidget.cs:84-102）
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleVertical, 3, 0, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("triple-vertical: left full-height", cell, 0f, 0f, 988f, 1200f);
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleVertical, 3, 1, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("triple-vertical: top-right", cell, 506f, 0f, 988f, 588f);
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleVertical, 3, 2, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("triple-vertical: bottom-right", cell, 506f, 306f, 988f, 588f);

            // TripleHorizontal：上满宽 + 左下 + 右下（Source: GamesWidget.cs:103-121）
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleHorizontal, 3, 0, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("triple-horizontal: top full-width", cell, 0f, 0f, 2000f, 588f);
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleHorizontal, 3, 1, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("triple-horizontal: bottom-left", cell, 0f, 306f, 988f, 588f);
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleHorizontal, 3, 2, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("triple-horizontal: bottom-right", cell, 506f, 306f, 988f, 588f);

            float x, y, w, h;
            UiSplitView.TryGetRelativeQuad(SplitViewLayoutKind.TripleHorizontal, 3, 0,
                1000f, 600f, 1920f, 1080f, out x, out y, out w, out h);
            Check("triple-horizontal: top quad = full width, 49% of height",
                Near(x, 0f) && Near(y, 0f) && Near(w, 1f) && Near(h, 0.49f));
            UiSplitView.TryGetRelativeQuad(SplitViewLayoutKind.TripleHorizontal, 3, 2,
                1000f, 600f, 1920f, 1080f, out x, out y, out w, out h);
            Check("triple-horizontal: bottom-right quad",
                Near(x, 0.506f) && Near(y, 0.51f) && Near(w, 0.494f) && Near(h, 0.49f));

            // TripleEven：左上 + 右上 + 底部居中（Source: GamesWidget.cs:122-139）
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleEven, 3, 0, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("triple-even: top-left", cell, 0f, 0f, 988f, 588f);
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleEven, 3, 1, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("triple-even: top-right", cell, 506f, 0f, 988f, 588f);
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleEven, 3, 2, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("triple-even: bottom-center", cell, 253f, 306f, 988f, 588f);

            // TripleOpposite：左半旋转 90°，右上旋转 180°，右下正常（Source: GamesWidget.cs:140-158）
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleOpposite, 3, 0, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("triple-opposite: left rotated cell", cell, 0f, 0f, 988f, 1200f);
            Check("triple-opposite: first cell rotated", cell.Rotated);
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleOpposite, 3, 1, 1000f, 600f,
                1920f, 1080f, out cell);
            Check("triple-opposite: second cell mirrored 180",
                Near(cell.M11, -0.5f) && Near(cell.M22, -0.5f));
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleOpposite, 3, 2, 1000f, 600f,
                1920f, 1080f, out cell);
            Check("triple-opposite: third cell upright",
                Near(cell.M11, 0.5f) && Near(cell.M22, 0.5f));

            Check("triple-opposite: rotated view gets no relative quad",
                !UiSplitView.TryGetRelativeQuad(SplitViewLayoutKind.TripleOpposite, 3, 0,
                    1000f, 600f, 1920f, 1080f, out x, out y, out w, out h));
        }

        /// <summary>
        /// 用**实机数据**钉住 TripleHorizontal 的映射（2026-10-20 主机三视图，客户区 1920x1080，
        /// uiScale 0.7 → `GamesWidget.ActualSize` = 1214.3 x 683，RootWidget 全局缩放 = 1920/1214.3）。
        ///
        /// 引擎实测（`obs.ui --all --filter GameWidget`）：
        ///   GameWidget#0 = (0,0,1920,530.5)      ← 上满宽
        ///   GameWidget#1 = (0,549.5,950.5,530.5) ← 左下
        ///   GameWidget#2 = (969.5,549.5,950.5,530.5) ← 右下
        /// 这组数字与下面的算式逐像素一致；它同时也是"元素坐标已经在窗口坐标系里"的证据：
        /// 元素不必再做二次换算，`clientPoint` 就是窗口像素。
        /// </summary>
        private static void TestTripleVerifiedAgainstLiveEngine()
        {
            const float gamesWidth = 1214.3f;
            const float gamesHeight = 683f;
            const float windowWidth = 1920f;
            const float windowHeight = 1080f;
            float rootScale = windowWidth / gamesWidth;   // RootWidget.LayoutTransform 的缩放

            SplitViewCell cell;
            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleHorizontal, 3, 0, gamesWidth,
                gamesHeight, windowWidth, windowHeight, out cell);
            CheckCell("live: top view cell", cell, 0f, 0f, 2428.6f, 671f);
            CheckNear("live: top view global rect", cell.Width * cell.ScaleX * rootScale, 1920f, 0.2f);
            CheckNear("live: top view global height", cell.Height * cell.ScaleY * rootScale, 530.5f, 0.2f);

            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleHorizontal, 3, 1, gamesWidth,
                gamesHeight, windowWidth, windowHeight, out cell);
            CheckCell("live: bottom-left cell", cell, 0f, 347.5f, 1202.3f, 671f);
            CheckNear("live: bottom-left global x", cell.X * rootScale, 0f, 0.2f);
            CheckNear("live: bottom-left global y", cell.Y * rootScale, 549.5f, 0.2f);
            CheckNear("live: bottom-left global width",
                cell.Width * cell.ScaleX * rootScale, 950.5f, 0.2f);

            UiSplitView.TryGetCell(SplitViewLayoutKind.TripleHorizontal, 3, 2, gamesWidth,
                gamesHeight, windowWidth, windowHeight, out cell);
            CheckNear("live: bottom-right global x", cell.X * rootScale, 969.5f, 0.2f);
            CheckNear("live: bottom-right global y", cell.Y * rootScale, 549.5f, 0.2f);
        }

        // ---------------------------------------------------------------- 4 视图

        private static void TestQuadruple()
        {
            SplitViewCell cell;

            // Quadruple：2x2，spacing 12、scale 0.5（Source: GamesWidget.cs:162-185）
            UiSplitView.TryGetCell(SplitViewLayoutKind.Quadruple, 4, 0, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("quadruple: top-left", cell, 0f, 0f, 988f, 588f);
            UiSplitView.TryGetCell(SplitViewLayoutKind.Quadruple, 4, 1, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("quadruple: top-right", cell, 506f, 0f, 988f, 588f);
            UiSplitView.TryGetCell(SplitViewLayoutKind.Quadruple, 4, 2, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("quadruple: bottom-left", cell, 0f, 306f, 988f, 588f);
            UiSplitView.TryGetCell(SplitViewLayoutKind.Quadruple, 4, 3, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("quadruple: bottom-right", cell, 506f, 306f, 988f, 588f);

            // QuadrupleOpposite：格子相同，前两个视图旋转 180°（Source: GamesWidget.cs:186-209）
            UiSplitView.TryGetCell(SplitViewLayoutKind.QuadrupleOpposite, 4, 0, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("quadruple-opposite: top-left cell", cell, 0f, 0f, 988f, 588f);
            Check("quadruple-opposite: top-left rotated 180",
                Near(cell.M11, -0.5f) && Near(cell.M22, -0.5f) && Near(cell.M12, 0f));
            UiSplitView.TryGetCell(SplitViewLayoutKind.QuadrupleOpposite, 4, 2, 1000f, 600f,
                1920f, 1080f, out cell);
            CheckCell("quadruple-opposite: bottom-left cell", cell, 0f, 306f, 988f, 588f);
            Check("quadruple-opposite: bottom-left upright",
                Near(cell.M11, 0.5f) && Near(cell.M22, 0.5f));

            Check("quadruple: 4 views with a 3-view layout is unknown",
                !UiSplitView.TryGetCell(SplitViewLayoutKind.TripleEven, 4, 0, 1000f, 600f,
                    1920f, 1080f, out cell));
        }

        // ---------------------------------------------------------------- 布局名解析

        private static void TestResolveLayoutGuards()
        {
            Check("layout name: parses TripleHorizontal",
                UiSplitView.ResolveLayout(3, "Single", "DoubleOpposite", "TripleHorizontal",
                    "Quadruple") == SplitViewLayoutKind.TripleHorizontal);
            Check("layout name: case-insensitive",
                UiSplitView.ParseLayout("triplevertical") == SplitViewLayoutKind.TripleVertical);
            Check("layout name: unknown text -> Unknown",
                UiSplitView.ParseLayout("NoSuchLayout") == SplitViewLayoutKind.Unknown);
            Check("layout name: null -> Unknown",
                UiSplitView.ParseLayout(null) == SplitViewLayoutKind.Unknown);
            // 视图数与布局不一致 = 引擎 `ArrangeOverride` 一条分支都不会进 → 必须报 Unknown，绝不猜
            Check("layout name: 3 views + Quadruple -> Unknown",
                UiSplitView.ResolveLayout(3, "Single", "DoubleOpposite", "Quadruple",
                    "Quadruple") == SplitViewLayoutKind.Unknown);
            Check("layout name: 0 views -> Unknown",
                UiSplitView.ResolveLayout(0, "Single", "DoubleVertical", "TripleHorizontal",
                    "Quadruple") == SplitViewLayoutKind.Unknown);
            Check("child count of layout",
                UiSplitView.ChildCountOf(SplitViewLayoutKind.QuadrupleOpposite) == 4
                && UiSplitView.ChildCountOf(SplitViewLayoutKind.DoubleHorizontal) == 2
                && UiSplitView.ChildCountOf(SplitViewLayoutKind.Single) == 1);
        }

        // ---------------------------------------------------------------- 主视图规则

        private static void TestPrimaryViewRule()
        {
            // 接受鼠标 = 只有 H1（本地分屏的常态：键盘鼠标给 H1，H2/H3 是手柄/触屏）
            Check("primary: the mouse view wins",
                UiSplitView.SelectPrimaryView(new[] { 0, 1, 2 }, new[] { true, false, false }) == 0);
            // 只有 H3 带鼠标（MultiMice 分屏）→ 主视图是 H3，而不是最小槽位
            Check("primary: mouse view wins even when it is not slot 0",
                UiSplitView.SelectPrimaryView(new[] { 0, 1, 2 }, new[] { false, false, true }) == 2);
            // 两个鼠标视图 → 取槽位最小
            Check("primary: lowest mouse slot",
                UiSplitView.SelectPrimaryView(new[] { 2, 0, 1 }, new[] { true, true, false }) == 0);
            // 一个都没有鼠标（引擎还没跑第一帧 / 全触屏）→ 最小槽位，保证确定性
            Check("primary: no mouse -> lowest slot",
                UiSplitView.SelectPrimaryView(new[] { 2, 0, 1 },
                    new[] { false, false, false }) == 0);
            // 空表 → NoView（调用方据此报"打开世界后才有分屏视图"）
            Check("primary: no views -> NoView",
                UiSplitView.SelectPrimaryView(new int[0], new bool[0]) == UiSplitView.NoView);
            Check("primary: null -> NoView",
                UiSplitView.SelectPrimaryView(null, null) == UiSplitView.NoView);
        }

        // ---------------------------------------------------------------- 同名元素跨视图选择

        private static void TestInstanceSelection()
        {
            // 三个视图各一份、主视图 0 → 选第 0 个
            Check("instance: three views pick the primary",
                UiSplitView.SelectInstanceIndex(new[] { 0, 1, 2 }, 0) == 0);
            Check("instance: three views pick the primary (primary = 2)",
                UiSplitView.SelectInstanceIndex(new[] { 0, 1, 2 }, 2) == 2);
            // 主视图里没有这个元素 → 不猜（调用方报 ambiguous_selector 并列出候选）
            Check("instance: primary has no candidate -> keep all",
                UiSplitView.SelectInstanceIndex(new[] { 1, 2 }, 0) == -1);
            // 主视图里有两份（按钮 + 内部标签）→ 也不猜，交给调用方原有的消歧
            Check("instance: two candidates in the primary -> keep all",
                UiSplitView.SelectInstanceIndex(new[] { 0, 0, 1 }, 0) == -1);
            // 菜单/对话框（不属于任何视图）与视图候选混在一起 → 按主视图取
            Check("instance: screen element vs view element",
                UiSplitView.SelectInstanceIndex(new[] { -1, 0, 1 }, 0) == 1);
            // 全在同一个视图 → 不改（这就是单视图行为不变的保证）
            Check("instance: same view -> keep all",
                UiSplitView.SelectInstanceIndex(new[] { 0, 0 }, 0) == -1);
            // 全都不属于视图（两张菜单同名的按钮）→ 不改
            Check("instance: all screen elements -> keep all",
                UiSplitView.SelectInstanceIndex(new[] { -1, -1 }, 0) == -1);
            Check("instance: single candidate -> keep all",
                UiSplitView.SelectInstanceIndex(new[] { 1 }, 0) == -1);
            Check("instance: no primary known -> keep all",
                UiSplitView.SelectInstanceIndex(new[] { 0, 1 },
                    UiSplitView.NoView) == -1);
        }

        /// <summary>
        /// **单视图行为不变**的护栏：单个 GameWidget（或不在世界里）时，两条选择规则都必须
        /// "什么都不改"——`SelectInstanceIndex` 返回 -1，`SelectPrimaryView` 返回唯一的槽位。
        /// 这正是老版本（1.1.37 之前）能点通的那条路，改多视图时不许把它带坏。
        /// </summary>
        private static void TestSingleViewUnchanged()
        {
            Check("single view: primary is the only slot",
                UiSplitView.SelectPrimaryView(new[] { 0 }, new[] { true }) == 0);
            Check("single view: primary is the only slot even without a mouse",
                UiSplitView.SelectPrimaryView(new[] { 0 }, new[] { false }) == 0);
            Check("single view: one candidate stays one candidate",
                UiSplitView.SelectInstanceIndex(new[] { 0 }, 0) == -1);
            Check("single view: two same-name candidates stay ambiguous",
                UiSplitView.SelectInstanceIndex(new[] { 0, 0 }, 0) == -1);
        }

        // ---------------------------------------------------------------- 断言工具

        private static void Check(string name, bool condition)
        {
            if (condition)
            {
                Console.WriteLine("  ok   " + name);
                return;
            }
            s_failures++;
            Console.WriteLine("  FAIL " + name);
        }

        private static void CheckCell(string name, SplitViewCell cell, float x, float y,
            float width, float height)
        {
            bool ok = Near(cell.X, x) && Near(cell.Y, y) && Near(cell.Width, width)
                && Near(cell.Height, height);
            if (ok)
            {
                Console.WriteLine("  ok   " + name);
                return;
            }
            s_failures++;
            Console.WriteLine("  FAIL " + name + " expected=(" + x + "," + y + "," + width + ","
                + height + ") actual=(" + cell.X + "," + cell.Y + "," + cell.Width + ","
                + cell.Height + ")");
        }

        private static void CheckNear(string name, float actual, float expected, float tolerance)
        {
            Check(name + " (expected " + expected.ToString("0.###") + ", got "
                + actual.ToString("0.###") + ")", Math.Abs(actual - expected) <= tolerance);
        }

        private static bool Near(float a, float b)
        {
            return Math.Abs(a - b) <= 0.01f;
        }
    }
}
