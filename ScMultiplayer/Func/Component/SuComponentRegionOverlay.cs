using Engine;
using Engine.Graphics;
using Engine.Input;
using Engine.Media;
using Game;
using GameEntitySystem;
using TemplatesDatabase;
using System.Collections.Generic;

namespace ScMultiplayer
{
    /// <summary>
    /// 《玩家领地》P1/P4：**选区线框 + 已登记领地的区域展示**（挂在玩家实体上的可绘制组件）。
    ///
    /// 为什么用组件而不是新增子系统：引擎的 `SubsystemDrawing` 除子系统外**也会收集实体组件里的
    /// `IDrawable`**（`SubsystemDrawing.cs:60-68`），所以挂一个 `Component, IDrawable` 到 Player 实体
    /// 就能画 3D 世界线框，不必改 `Pak/Database.xml` 的子系统节点。
    ///
    /// 画法/判定参考引擎既有实现：
    ///   · `Survivalcraft/Game/ComponentBlockHighlight.cs` —— 自建 `PrimitivesRenderer3D`、
    ///     `FlatBatch3D.QueueBoundingBox` / `QueueLine`、`Flush(camera.ViewProjectionMatrix)`，
    ///     并用 `camera.GameWidget.PlayerData == 自己的 PlayerData` 保证**只画本端视角**；
    ///   · `Mod/CircuitAutoRouter/SubsystemCircuitRouter.cs` —— 世界线框与面朝向文字的写法
    ///     （`FontBatch3D.QueueText` + 每面各自的 right/down 向量 + 居中对齐）。
    ///
    /// P1：选区（点1/点2 → 全高线框 + 地面/顶部矩形），不填充面；
    /// P4：**区域展示**（设计稿 §3）—— 已登记领地画 12 条棱 + **除底面外的 5 个外表面**半透明填充
    ///     + 这 5 个面中央的**领地编号**；颜色跟随拥有者 userId（稳定散列 → paint 调色板 16 色）；
    ///     按距离分级（近处填充+编号，中距离只画线框），开关在 GmMod 面板。
    /// </summary>
    public class SuComponentRegionOverlay : Component, IDrawable
    {
        // 与 ComponentBlockHighlight 同量级即可：2000 = 世界绘制之后（线框压在地形上）
        private static readonly int[] s_drawOrders = new int[] { 2000 };

        // 分级绘制阈值（设计稿 §3「性能」：近处填充+编号 / 中距离线框 / 更远不画）
        private const int MaximumFilledClaims = 6;
        private const int MaximumWireframeClaims = 18;
        // 编号字号（设计稿 §3：约 0.04）与面外偏
        private const float NumberSize = 0.04f;
        private const float FaceOffset = 0.02f;
        // 填充透明度（"同色、低透明度"）
        private const byte FillAlpha = 48;
        // 贴地编号用不着每帧扫地形：按"领地+列"缓存 1 秒
        private const double SurfaceHeightCacheLifetime = 1.0;

        private readonly PrimitivesRenderer3D m_renderer = new PrimitivesRenderer3D();
        private readonly List<RegionClaim> m_filledClaims = new List<RegionClaim>();
        private readonly List<RegionClaim> m_wireframeClaims = new List<RegionClaim>();
        private readonly Dictionary<long, SurfaceHeightCache> m_surfaceHeights =
            new Dictionary<long, SurfaceHeightCache>();
        private ComponentPlayer m_componentPlayer;
        private SubsystemTerrain m_subsystemTerrain;
        private BitmapFont m_font;
        private bool m_fontLoadFailed;

        public int[] DrawOrders => s_drawOrders;

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>();
            m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(false);
        }

        /// <summary>
        /// 字体**懒加载在绘制期**（与 `CircuitAutoRouter` 同样的取法）：在 `Load()` 阶段
        /// `ContentManager.Get` 拿不到字体（实测：编号一个字都不画），换到绘制期即可。
        /// </summary>
        private BitmapFont ResolveFont()
        {
            if (m_font != null || m_fontLoadFailed)
                return m_font;
            try
            {
                m_font = ContentManager.Get<BitmapFont>("Fonts/Pericles18");
            }
            catch (System.Exception)
            {
                m_fontLoadFailed = true;
            }
            return m_font;
        }

        public void Draw(Camera camera, int drawOrder)
        {
            ScMultiplayer mod = ScMultiplayer.currentInstance;
            if (mod == null || m_componentPlayer?.PlayerData == null)
                return;
            // 只画本端视角（同 ComponentBlockHighlight.Draw 的判定）
            if (camera?.GameWidget?.PlayerData != m_componentPlayer.PlayerData)
                return;

            if (mod.RegionDisplayEnabled)
                DrawRegisteredClaims(mod, camera);
            if (mod.RegionSelectionPreviewEnabled)
                DrawSelectionPreview(mod, camera);
        }

        // ---------------------------------------------------------------- P4：已登记领地

        private void DrawRegisteredClaims(ScMultiplayer mod, Camera camera)
        {
            Vector3 position = m_componentPlayer.ComponentBody?.Position ?? Vector3.Zero;
            mod.CollectRegionClaimsForDisplay(position, MaximumFilledClaims,
                MaximumWireframeClaims, m_filledClaims, m_wireframeClaims);
            if (m_filledClaims.Count == 0 && m_wireframeClaims.Count == 0)
                return;

            FlatBatch3D flat = m_renderer.FlatBatch(0, DepthStencilState.None);
            BitmapFont bitmapFont = ResolveFont();
            FontBatch3D font = bitmapFont != null
                ? m_renderer.FontBatch(bitmapFont, 1, DepthStencilState.None)
                : null;

            for (int i = 0; i < m_wireframeClaims.Count; i++)
            {
                RegionClaim claim = m_wireframeClaims[i];
                Color color = ClaimColor(mod, claim);
                flat.QueueBoundingBox(GetClaimBox(claim), color);
            }
            for (int i = 0; i < m_filledClaims.Count; i++)
            {
                RegionClaim claim = m_filledClaims[i];
                Color color = ClaimColor(mod, claim);
                DrawClaimFaces(flat, claim, color);
                flat.QueueBoundingBox(GetClaimBox(claim), Color.Lerp(color, Color.White, 0.35f));
                if (font != null)
                    DrawClaimNumbers(font, claim, color);
            }
            m_renderer.Flush(camera.ViewProjectionMatrix);
        }

        private static BoundingBox GetClaimBox(RegionClaim claim) => new BoundingBox(
            new Vector3(claim.MinX, claim.MinY, claim.MinZ),
            new Vector3(claim.MaxX + 1, claim.MaxY + 1, claim.MaxZ + 1));

        /// <summary>除底面（-Y）外的 5 个外表面：4 个侧面 + 顶面，半透明同色填充。</summary>
        private static void DrawClaimFaces(FlatBatch3D flat, RegionClaim claim, Color color)
        {
            var fill = new Color(color.R, color.G, color.B, FillAlpha);
            float x1 = claim.MinX;
            float x2 = claim.MaxX + 1;
            float y1 = claim.MinY;
            float y2 = claim.MaxY + 1;
            float z1 = claim.MinZ;
            float z2 = claim.MaxZ + 1;
            // -X / +X
            flat.QueueQuad(new Vector3(x1, y1, z1), new Vector3(x1, y1, z2),
                new Vector3(x1, y2, z2), new Vector3(x1, y2, z1), fill);
            flat.QueueQuad(new Vector3(x2, y1, z2), new Vector3(x2, y1, z1),
                new Vector3(x2, y2, z1), new Vector3(x2, y2, z2), fill);
            // -Z / +Z
            flat.QueueQuad(new Vector3(x2, y1, z1), new Vector3(x1, y1, z1),
                new Vector3(x1, y2, z1), new Vector3(x2, y2, z1), fill);
            flat.QueueQuad(new Vector3(x1, y1, z2), new Vector3(x2, y1, z2),
                new Vector3(x2, y2, z2), new Vector3(x1, y2, z2), fill);
            // +Y（顶面）；底面不画（设计稿 §3：除底面外的其余 5 个外表面）
            flat.QueueQuad(new Vector3(x1, y2, z1), new Vector3(x2, y2, z1),
                new Vector3(x2, y2, z2), new Vector3(x1, y2, z2), fill);
        }

        /// <summary>
        /// 领地编号：画在 4 个侧面、**贴着该面所在地表**（用户 2026-09-24 决定：只保留贴地那一份）。
        ///
        /// 为什么不是"面中央"：领地高度是 0–255（全高），面中心在 y≈128，站在地上几乎看不见
        /// （面侧对镜头，字只有几像素）。改成按该面中心列的地表高度定位后，正常视角即可读。
        /// 字号仍按设计稿 §3 的 0.04。
        /// Source: Mod/CircuitAutoRouter/SubsystemCircuitRouter.cs:SubsystemCircuitRouter.DrawNumbers
        /// </summary>
        private void DrawClaimNumbers(FontBatch3D font, RegionClaim claim, Color color)
        {
            string text = claim.Id.ToString();
            Color bright = Color.Lerp(color, Color.White, 0.6f);
            float centerX = (claim.MinX + claim.MaxX + 1) * 0.5f;
            float centerZ = (claim.MinZ + claim.MaxZ + 1) * 0.5f;
            var right = new Vector3(NumberSize, 0f, 0f);
            var down = new Vector3(0f, -NumberSize, 0f);
            TextAnchor anchor = TextAnchor.HorizontalCenter | TextAnchor.VerticalCenter;
            const float aboveGround = 1.2f;

            // +Z / -Z（取该面中心列的地表高度）
            float yPlusZ = GetSurfaceHeight(claim, (int)centerX, claim.MaxZ) + aboveGround;
            float yMinusZ = GetSurfaceHeight(claim, (int)centerX, claim.MinZ) + aboveGround;
            font.QueueText(text, new Vector3(centerX, yPlusZ, claim.MaxZ + 1f + FaceOffset),
                right, down, bright, anchor);
            font.QueueText(text, new Vector3(centerX, yMinusZ, claim.MinZ - FaceOffset),
                new Vector3(-NumberSize, 0f, 0f), down, bright, anchor);
            // +X / -X
            float yPlusX = GetSurfaceHeight(claim, claim.MaxX, (int)centerZ) + aboveGround;
            float yMinusX = GetSurfaceHeight(claim, claim.MinX, (int)centerZ) + aboveGround;
            font.QueueText(text, new Vector3(claim.MaxX + 1f + FaceOffset, yPlusX, centerZ),
                new Vector3(0f, 0f, -NumberSize), down, bright, anchor);
            font.QueueText(text, new Vector3(claim.MinX - FaceOffset, yMinusX, centerZ),
                new Vector3(0f, 0f, NumberSize), down, bright, anchor);
        }

        /// <summary>
        /// 该列（x,z）在该领地高度范围内的**地表高度**（自上而下第一个非空气格）。
        /// 结果是按"领地 + 面"缓存的（1 秒 TTL）：地表会变，但不必每帧扫 256 格。
        /// </summary>
        private float GetSurfaceHeight(RegionClaim claim, int x, int z)
        {
            long key = ((long)claim.Id << 4) ^ ((long)(x & 0xFFFF) << 4) ^ (long)(z & 0xFFFF);
            if (m_surfaceHeights.TryGetValue(key, out SurfaceHeightCache cached) &&
                Time.RealTime - cached.Time < SurfaceHeightCacheLifetime)
                return cached.Height;

            float height = claim.MinY;
            SubsystemTerrain terrain = m_subsystemTerrain ??
                (m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(false));
            if (terrain?.Terrain != null)
            {
                for (int y = claim.MaxY; y >= claim.MinY; y--)
                {
                    if (terrain.Terrain.GetCellContents(x, y, z) != 0)
                    {
                        height = y + 1;
                        break;
                    }
                }
            }
            m_surfaceHeights[key] = new SurfaceHeightCache
            {
                Height = height,
                Time = Time.RealTime
            };
            if (m_surfaceHeights.Count > 512)
                m_surfaceHeights.Clear();
            return height;
        }

        private struct SurfaceHeightCache
        {
            public float Height;
            public double Time;
        }

        /// <summary>领地颜色：拥有者 userId 的稳定散列 → paint 调色板（见 ScMultiplayer.RegionClaimPaletteIndex）。</summary>
        private Color ClaimColor(ScMultiplayer mod, RegionClaim claim)
        {
            SubsystemTerrain terrain = m_subsystemTerrain ??
                (m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(false));
            try
            {
                if (terrain != null)
                    return SubsystemPalette.GetColor(terrain, mod.RegionClaimPaletteIndex(claim));
            }
            catch (System.Exception)
            {
            }
            return new Color(0, 255, 255);
        }

        // ---------------------------------------------------------------- P1：选区预览

        private void DrawSelectionPreview(ScMultiplayer mod, Camera camera)
        {
            if (!mod.TryGetRegionSelectionBox(out Point3 min, out Point3 max))
                return;

            Color color = GetSelectionColor(mod);
            Vector3 corner1 = new Vector3(min.X, min.Y, min.Z);
            Vector3 corner2 = new Vector3(max.X + 1, max.Y + 1, max.Z + 1);
            BoundingBox box = new BoundingBox(corner1, corner2);

            FlatBatch3D flat = m_renderer.FlatBatch(0, DepthStencilState.None);
            // 全高长方体（12 条棱）
            flat.QueueBoundingBox(box, color);
            // 地面矩形 + 顶部矩形：亮一档并内缩 0.02，避免与棱重合并加强可读性（用户要求）
            Color bright = Color.Lerp(color, Color.White, 0.55f);
            DrawRectangle(flat, min.X + 0.02f, max.X + 0.98f, min.Z + 0.02f, max.Z + 0.98f, min.Y + 0.02f, bright);
            DrawRectangle(flat, min.X + 0.02f, max.X + 0.98f, min.Z + 0.02f, max.Z + 0.98f, max.Y + 0.98f, bright);
            m_renderer.Flush(camera.ViewProjectionMatrix);
        }

        private static void DrawRectangle(FlatBatch3D flat, float x1, float x2, float z1, float z2, float y, Color color)
        {
            flat.QueueLine(new Vector3(x1, y, z1), new Vector3(x2, y, z1), color);
            flat.QueueLine(new Vector3(x2, y, z1), new Vector3(x2, y, z2), color);
            flat.QueueLine(new Vector3(x2, y, z2), new Vector3(x1, y, z2), color);
            flat.QueueLine(new Vector3(x1, y, z2), new Vector3(x1, y, z1), color);
        }

        // Source: Survivalcraft/Game/SubsystemPalette.cs:SubsystemPalette.GetColor(SubsystemTerrain, int?)
        // Source: Survivalcraft/Game/WireBlock.cs —— 导线的颜色就是这套 paint 调色板（16 色）
        private Color GetSelectionColor(ScMultiplayer mod)
        {
            int paletteIndex = mod.RegionSelectionPaletteIndex();
            SubsystemTerrain terrain = m_subsystemTerrain ??
                (m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(false));
            try
            {
                if (terrain != null)
                    return SubsystemPalette.GetColor(terrain, paletteIndex);
            }
            catch (System.Exception)
            {
                // 调色板不可用时退回可辨识的青色
            }
            return new Color(0, 255, 255);
        }
    }
}
