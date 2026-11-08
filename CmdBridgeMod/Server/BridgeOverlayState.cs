using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;
using System;
using System.Collections.Generic;

namespace CmdBridgeMod
{
    /// <summary>一个待绘制的边框（固定世界坐标；纯内存，不落盘）。</summary>
    internal sealed class BridgeOverlayBox
    {
        /// <summary>包围盒（各轴已按 min/max 排好；单方块时 max = 格坐标 +1）。</summary>
        public BoundingBox Bounds;
        public Color Color;
        /// <summary>创建序号（`box.list` / `box.remove` 用）。</summary>
        public int Id;
    }

    /// <summary>
    /// 边框绘制的**状态与挂载**（调试辅助：直观看到某个方块 / 区域在世界里的位置）。
    ///
    /// 隔离原则（避免与其他 Mod 干涉）：
    ///   · **不替换任何 Subsystem / Parameter、不注册 Injector、不改帧率**；
    ///   · 唯一的渲染是内部的 `BridgeOverlayDrawable`，通过引擎的
    ///     `SubsystemDrawing.AddDrawable(IDrawable)`（public API）注册 ——
    ///     **不挂实体组件**（`Entity` 在引擎里没有公开的 `AddComponent`，动态加组件需要
    ///     重建 ValuesDictionary，侵入性大），也不改 `Pak/Database.xml` 的子系统节点；
    ///   · 数据只存在本类的静态列表里 —— **纯内存、不落盘**，卸载即清空；
    ///   · 绘制顺序 **2001**：避开 CircuitAutoRouter 的 100 / MiniMap 的 1246 /
    ///     ScMultiplayer 领地框的 2000 / 引擎高亮的 1 与 2000，因此不会与他人抢同一档位。
    ///
    /// 为什么不做"实时跟随"：用户口径是**固定坐标**（画在指定位置不动），因此绘制期不需要
    /// 读地形、不需要每帧采样，`Draw` 只遍历已存好的包围盒。
    /// </summary>
    internal static class BridgeOverlayState
    {
        /// <summary>同时存在的边框上限（防止一次请求塞爆绘制）。</summary>
        internal const int MaximumBoxes = 256;

        /// <summary>默认颜色：绿色（用户口径）。</summary>
        internal static readonly Color DefaultColor = new Color(64, 255, 96, 255);

        private static readonly List<BridgeOverlayBox> s_boxes = new List<BridgeOverlayBox>();
        private static int s_nextId = 1;
        private static BridgeOverlayDrawable s_drawable;
        private static Project s_attachedProject;

        /// <summary>绘制期快照（返回副本，避免命令线程与绘制并发改同一个 List）。</summary>
        internal static BridgeOverlayBox[] Snapshot()
        {
            lock (s_boxes)
            {
                if (s_boxes.Count == 0) return Array.Empty<BridgeOverlayBox>();
                var copy = new BridgeOverlayBox[s_boxes.Count];
                s_boxes.CopyTo(copy);
                return copy;
            }
        }

        internal static int Count
        {
            get { lock (s_boxes) return s_boxes.Count; }
        }

        /// <summary>加一个包围盒。返回分配到的序号；超过上限时返回 -1（明确失败，不静默丢弃）。</summary>
        internal static int Add(BoundingBox bounds, Color color)
        {
            lock (s_boxes)
            {
                if (s_boxes.Count >= MaximumBoxes) return -1;
                var box = new BridgeOverlayBox
                {
                    Bounds = bounds,
                    Color = color,
                    Id = s_nextId++
                };
                s_boxes.Add(box);
                return box.Id;
            }
        }

        /// <summary>清除全部边框，返回清掉的数量（序号不重置，旧 id 不会与新 id 混淆）。</summary>
        internal static int Clear()
        {
            lock (s_boxes)
            {
                int count = s_boxes.Count;
                s_boxes.Clear();
                return count;
            }
        }

        /// <summary>按序号删一个；返回是否删到。</summary>
        internal static bool Remove(int id)
        {
            lock (s_boxes)
            {
                for (int i = 0; i < s_boxes.Count; i++)
                {
                    if (s_boxes[i].Id != id) continue;
                    s_boxes.RemoveAt(i);
                    return true;
                }
                return false;
            }
        }

        internal static BridgeOverlayBox[] List()
        {
            lock (s_boxes) return s_boxes.ToArray();
        }

        /// <summary>Mod 卸载时复位：摘掉绘制器并清空状态，不留任何残留。</summary>
        internal static void Reset()
        {
            Detach();
            Clear();
        }

        /// <summary>
        /// 确保绘制器已注册到当前世界的 `SubsystemDrawing`（**幂等**，每帧调用无副作用）。
        ///
        /// 只在**有边框要画**时才注册：没有边框时完全不碰世界对象（等于零开销、零侵入）。
        /// 换世界（Project 变化）时旧绘制器已随旧 Project 的子系统销毁，这里会重新注册。
        /// </summary>
        internal static void EnsureAttached()
        {
            if (Count == 0)
            {
                // 没有边框：主动摘掉，避免空转（也只摘自己注册的那个绘制器）。
                if (s_drawable != null) Detach();
                return;
            }

            Project project = GameManager.Project;
            if (project == null)
            {
                s_attachedProject = null;
                return;
            }
            SubsystemDrawing drawing = project.FindSubsystem<SubsystemDrawing>(false);
            if (drawing == null) return;
            if (ReferenceEquals(s_attachedProject, project) && s_drawable != null)
                return;

            Detach();
            s_drawable = new BridgeOverlayDrawable();
            drawing.AddDrawable(s_drawable);
            s_attachedProject = project;
        }

        private static void Detach()
        {
            if (s_drawable != null && s_attachedProject != null)
            {
                try
                {
                    s_attachedProject.FindSubsystem<SubsystemDrawing>(false)
                        ?.RemoveDrawable(s_drawable);
                }
                catch (Exception)
                {
                    // 旧 Project 可能已经销毁：这时本来就不需要再摘，忽略即可。
                }
            }
            s_drawable = null;
            s_attachedProject = null;
        }
    }
}
