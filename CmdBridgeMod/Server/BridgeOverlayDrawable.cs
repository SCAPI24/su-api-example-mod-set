using Engine;
using Engine.Graphics;
using Game;

namespace CmdBridgeMod
{
    /// <summary>
    /// 方块 / 区域边框绘制器（调试辅助：直观看到某个方块或区域在世界里的位置）。
    ///
    /// 注册方式：`SubsystemDrawing.AddDrawable(IDrawable)`（引擎 public API，
    /// 见 `Survivalcraft/Game/SubsystemDrawing.cs:18`），由 `BridgeOverlayState.EnsureAttached()`
    /// 在**确实有边框要画时**才注册 —— 不替换任何子系统、不挂实体组件、不改数据库节点，
    /// 因此与其他 Mod 互不干涉。
    ///
    /// 画法照引擎既有实现：
    ///   · `Survivalcraft/Game/ComponentBlockHighlight.cs` —— 自建 `PrimitivesRenderer3D`、
    ///     `FlatBatch3D.QueueBoundingBox`、`Flush(camera.ViewProjectionMatrix)`；
    ///   · `Mod/ScMultiplayer/Func/Component/SuComponentRegionOverlay.cs` —— 领地边框的
    ///     `QueueBoundingBox` + `DepthStencilState.None` 写法。
    ///
    /// 绘制顺序 **2001**：世界几何之后（线框压在地形上、可穿墙看见），且与现有 Mod 都不同
    /// （CircuitAutoRouter=100、MiniMap=1246、ScMultiplayer 领地框=2000、引擎高亮=1/2000）。
    /// </summary>
    internal sealed class BridgeOverlayDrawable : IDrawable
    {
        private static readonly int[] s_drawOrders = new int[] { 2001 };

        private readonly PrimitivesRenderer3D m_renderer = new PrimitivesRenderer3D();

        public int[] DrawOrders => s_drawOrders;

        public void Draw(Camera camera, int drawOrder)
        {
            if (camera == null) return;
            BridgeOverlayBox[] boxes = BridgeOverlayState.Snapshot();
            if (boxes.Length == 0) return;

            // DepthStencilState.None：关深度测试 → 边框能穿透地形看见
            // （"方块在哪"的定位辅助就是要能看见，不追求遮挡关系）。
            FlatBatch3D flat = m_renderer.FlatBatch(0, DepthStencilState.None);
            for (int i = 0; i < boxes.Length; i++)
            {
                BridgeOverlayBox box = boxes[i];
                if (box == null) continue;
                flat.QueueBoundingBox(box.Bounds, box.Color);
            }
            m_renderer.Flush(camera.ViewProjectionMatrix);
        }
    }
}
