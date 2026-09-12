using Engine;
using Engine.Graphics;
using Game;
using System;
using System.Collections.Generic;

namespace CmdBridgeMod
{
    /// <summary>
    /// **落点标记**：在游戏里把"这个目标现在到底在哪个像素"画出来，亮 2 秒后自己消失。
    ///
    /// 用户要求（原话）："点定位，在游戏中看不到明显的标记，可以在点击定位处渲染 2s 直接 5 像素的
    /// 红色圆点，方便定位。"
    ///
    /// 实现方式：往 `ScreensManager.RootWidget` 的最末尾塞一个**自己的小控件**
    /// （子控件顺序 = 绘制顺序，最后一个画在最上面），它只做一件事 —— 在 `Draw` 里
    /// 往 2D 批次里塞一个方块。要点：
    ///
    ///   · `IsHitTestVisible = false`：**绝不吃点击**（它是一个覆盖层，不是 UI）；
    ///   · `DesiredSize` 必须非零：`Widget.CollateDrawItems` 会用 `GlobalBounds` 与屏幕求交，
    ///     空尺寸的控件会被直接跳过（`Widget.cs:57`），所以给它 1x1 的"画布位"，
    ///     真正要画的地方由 `Draw` 自己按局部坐标算；
    ///   · 坐标换算：RootWidget 的子控件活在**设计坐标**里，`GlobalScale` 才是"设计 → 客户区像素"
    ///     的比例（`ScreensManager.cs:420-430`），所以客户区点 = 设计点 × GlobalScale；
    ///   · 到点自己摘下来（`Remove`），不留常驻控件；没到点则每帧自查一次。
    ///
    /// 它**只画像素**：不改任何游戏状态、不动控件树里别人的东西，纯粹是给编辑器用的调试标记。
    /// </summary>
    internal static class UiMarker
    {
        /// <summary>默认亮多久（秒）与多大（客户区像素）。</summary>
        public const float DefaultSeconds = 2f;
        public const float DefaultDiameterPixels = 5f;

        private static UiMarkerWidget s_widget;

        /// <summary>标记"真的被画出来过"的计数（诊断用：证明引擎确实调了它的 Draw）。</summary>
        private static int s_drawFrames;

        /// <summary>最近一次标记的落点与目标帧数（诊断用）。</summary>
        private static Vector2 s_lastPoint;

        /// <summary>标记的当前状态（`ui.marker` 命令用；只读、不给游戏装任何东西）。</summary>
        public static Dictionary<string, object> Describe()
        {
            UiMarkerWidget widget = s_widget;
            bool attached = widget != null && widget.ParentWidget != null;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["shown"] = attached,
                ["drawFrames"] = s_drawFrames,
                ["point"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = s_lastPoint.X,
                    ["y"] = s_lastPoint.Y
                },
                ["expiresInSeconds"] = attached
                    ? Math.Max(0.0, widget.ExpireRealTime - Time.RealTime) : 0.0,
                ["diameterPixels"] = widget != null ? widget.DiameterPixels : 0f
            };
        }

        /// <summary>在客户区坐标（像素）处亮一个红点；`seconds` 后自动消失。</summary>
        public static bool Show(InputInjector injector, Vector2 clientPoint, float seconds,
            float diameterPixels)
        {
            ContainerWidget root = ScreensManager.RootWidget;
            if (root == null)
                return false;

            UiMarkerWidget widget = s_widget;
            if (widget == null || widget.ParentWidget == null)
            {
                widget = new UiMarkerWidget();
                s_widget = widget;
                root.Children.Add(widget);   // 加在最后 = 画在所有屏之上
            }

            widget.ClientPoint = clientPoint;
            widget.DiameterPixels = diameterPixels > 0f ? diameterPixels : DefaultDiameterPixels;
            widget.ExpireRealTime = Time.RealTime + MathUtils.Max(0.05f, seconds);
            widget.IsDrawEnabled = true;
            s_lastPoint = clientPoint;
            s_drawFrames = 0;

            ScheduleRemoval(injector, widget);
            return true;
        }

        /// <summary>立刻摘掉标记（停止 / 卸载 / 手动清屏时用）。</summary>
        public static void Clear()
        {
            UiMarkerWidget widget = s_widget;
            s_widget = null;
            if (widget != null && widget.ParentWidget != null)
            {
                try
                {
                    widget.ParentWidget.Children.Remove(widget);
                }
                catch (Exception)
                {
                    // 控件树正在被重构（切屏）时摘不掉也无所谓：它自己已经过了 ExpireRealTime，
                    // 下一次 Draw 什么也不会画。
                }
            }
        }

        /// <summary>到点之后在帧首摘掉它（每帧自查一次；没到点就再排一次）。</summary>
        private static void ScheduleRemoval(InputInjector injector, UiMarkerWidget widget)
        {
            if (injector == null)
                return;
            try
            {
                injector.Pump.Enqueue(delegate
                {
                    if (Time.RealTime < widget.ExpireRealTime)
                    {
                        ScheduleRemoval(injector, widget);
                        return;
                    }
                    if (ReferenceEquals(s_widget, widget))
                        Clear();
                });
            }
            catch (Exception)
            {
                // 排不进帧首泵（游戏没在出帧）不影响：Draw 自己会按 ExpireRealTime 停止绘制。
            }
        }

        /// <summary>真正画点的那个控件。</summary>
        private sealed class UiMarkerWidget : Widget
        {
            /// <summary>客户区坐标（像素）。</summary>
            public Vector2 ClientPoint;

            /// <summary>直径（客户区像素）。</summary>
            public float DiameterPixels = DefaultDiameterPixels;

            /// <summary>过这个时刻就不画了（`Time.RealTime`）。</summary>
            public double ExpireRealTime;

            public UiMarkerWidget()
            {
                IsHitTestVisible = false;   // 覆盖层：绝不吃点击
                IsUpdateEnabled = false;    // 不需要每帧 Update
            }

            public override void MeasureOverride(Vector2 parentAvailableSize)
            {
                // 非零尺寸：否则 GlobalBounds 为空，引擎会跳过整个控件的绘制（Widget.cs:57）。
                DesiredSize = new Vector2(1f, 1f);
                IsDrawRequired = true;
            }

            public override void Draw(DrawContext dc)
            {
                if (Time.RealTime > ExpireRealTime)
                    return;
                s_drawFrames++;

                float scale = 1f;
                if (RootWidget != null && RootWidget.GlobalScale > 0f)
                    scale = RootWidget.GlobalScale;

                Vector2 center = ClientPoint / scale;              // 客户区像素 → 设计坐标
                float radius = MathUtils.Max(0.5f, DiameterPixels * 0.5f / scale);

                FlatBatch2D batch = dc.PrimitivesRenderer2D.FlatBatch(0, DepthStencilState.None);
                int count = batch.TriangleVertices.Count;

                // 红点（5 像素）+ 1 像素深红外框：浅色/深色背景上都看得见，尺寸仍然是 5 像素。
                Vector2 min = center - new Vector2(radius, radius);
                Vector2 max = center + new Vector2(radius, radius);
                batch.QueueQuad(min, max, 0f, Color.Red);

                float edge = MathUtils.Max(0.25f, 0.5f / scale);
                batch.QueueQuad(min - new Vector2(edge, edge), new Vector2(max.X + edge, min.Y),
                    0f, Color.DarkRed);
                batch.QueueQuad(new Vector2(min.X - edge, max.Y), max + new Vector2(edge, edge),
                    0f, Color.DarkRed);
                batch.QueueQuad(min - new Vector2(edge, 0f), new Vector2(min.X, max.Y),
                    0f, Color.DarkRed);
                batch.QueueQuad(new Vector2(max.X, min.Y), max + new Vector2(edge, 0f),
                    0f, Color.DarkRed);

                batch.TransformTriangles(GlobalTransform, count);
            }
        }
    }
}
