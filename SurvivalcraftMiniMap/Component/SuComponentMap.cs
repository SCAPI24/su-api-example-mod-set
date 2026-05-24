using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TemplatesDatabase;

namespace SurvivalcraftMiniMap
{
    public class SuComponentMap : Component, IDrawable, IUpdateable
    {
        private int mapRadius = 100;
        private float MapScale = 0.5f;
        private float RmapRadius = 0.30f;
        private BitmapButtonWidget mapButton;
        private Point2 m_lastRenderedCell;
        private Vector2 m_displayCenter;
        private Vector2 m_renderCenter;
        private Vector2 LookVector;
        private Texture2D MapTexture;
        public ComponentPlayer m_componentPlayer;
        public SubsystemTerrain subsystemTerrain;

        private PrimitivesRenderer2D m_prUp1 = new PrimitivesRenderer2D();
        private PrimitivesRenderer2D m_prUp2 = new PrimitivesRenderer2D();
        private PrimitivesRenderer2D m_prDown1 = new PrimitivesRenderer2D();
        private PrimitivesRenderer2D m_prDown2 = new PrimitivesRenderer2D();
        private PrimitivesRenderer2D m_prIndicator = new PrimitivesRenderer2D();

        public static int[] m_drawOrders = new int[] { 1246 };

        private TexturedBatch2D m_activeUp;
        private TexturedBatch2D m_activeDown;
        private TexturedBatch2D m_renderUp;
        private TexturedBatch2D m_renderDown;
        private TexturedBatch2D m_indicatorBatch;

        private volatile int m_renderState;
        private volatile bool m_hasFirstFrame;
        private float m_renderCooldown;
        private const float MIN_RENDER_INTERVAL = 1f / 30f;
        private int m_frameCount;

        public bool MapType = false;
        public UpdateOrder UpdateOrder => UpdateOrder.Default;

        private void HandleInput()
        {
            if (m_componentPlayer == null) return;
            if (m_componentPlayer.GameWidget.Input.IsKeyDownOnce(Engine.Input.Key.M) || mapButton.IsClicked)
                MapType = !MapType;
        }

        public void Update(float dt)
        {
            if (m_componentPlayer == null) return;
            HandleInput();
            if (MapType) return;

            m_frameCount++;
            m_renderCooldown += dt;

            Point2 currentCell = Terrain.ToCell(this.m_componentPlayer.ComponentBody.Position.XZ);
            bool cellChanged = currentCell != m_lastRenderedCell;
            bool periodicCheck = m_frameCount % 120 == 0;
            if (cellChanged) m_lastRenderedCell = currentCell;

            if (m_renderState == 0 && m_renderCooldown >= MIN_RENDER_INTERVAL &&
                (!m_hasFirstFrame || cellChanged || periodicCheck))
            {
                m_renderCooldown = 0f;
                m_renderState = 1;
                Point2 renderCell = currentCell;
                TexturedBatch2D batchUp = m_renderUp;
                TexturedBatch2D batchDown = m_renderDown;
                Task.Run(() =>
                {
                    try { HandleRoundMap(batchUp, batchDown, renderCell); }
                    catch (Exception e) { Log.Warning("MiniMap render: " + e.Message); }
                });
            }
        }

        private void HandleRoundMap(TexturedBatch2D batchUp, TexturedBatch2D batchDown, Point2 cell)
        {
            if (m_componentPlayer == null) { m_renderState = 0; return; }

            Vector2 screenSize = m_componentPlayer.GameWidget.ActiveCamera.ViewportSize;
            // 小地图定位：右上角，留出边距避开游戏UI
            float visualRadiusPx = mapRadius * MapScale;
#if WINDOWS
            float marginX = screenSize.Y * 0.10f;
            float marginY = screenSize.Y * 0.10f;
#else
            float marginX = screenSize.Y * 0.15f;
            float marginY = screenSize.Y * 0.15f;
#endif
            Vector2 center = new Vector2(
                screenSize.X - visualRadiusPx - marginX,
                visualRadiusPx + marginY);


            batchUp.Clear();
            batchDown.Clear();

            for (int Y = -mapRadius; Y <= mapRadius; Y++)
            {
                int limit = (int)MathUtils.Round(MathUtils.Sqrt(MathUtils.Sqr(mapRadius) - MathUtils.Sqr(Y)));
                for (int X = -limit; X <= limit; X++)
                {
                    int cellContent = GetTopContent(cell.X + X, cell.Y + Y);
                    Vector4 slotCoord = Tool.SlotTexCoords[BlocksManager.Blocks[cellContent].DefaultTextureSlot];

                    Vector2 c1 = new Vector2(screenSize.X / 2 + X, screenSize.Y / 2 + Y);
                    Vector2 c2 = new Vector2(screenSize.X / 2 + X + 1, screenSize.Y / 2 + Y + 1);

                    Color color;
                    var block = BlocksManager.Blocks[cellContent];
                    if (block is CrossBlock) color = Color.MintGreen;
                    else if (block is WoodBlock) color = Color.White;
                    else if (block is GrassBlock || block is LeavesBlock || block is IvyBlock) color = Color.Green;
                    else if (block is BottomSuckerBlock) color = Color.DarkBlue;
                    else if (block is FluidBlock) color = Color.Blue;
                    else color = Color.White;

                    if (Y < 0)
                        batchUp.QueueQuad(c1, c2, 0f,
                            new Vector2(slotCoord.X, slotCoord.W),
                            new Vector2(slotCoord.Z, slotCoord.Y), color);
                    else
                        batchDown.QueueQuad(c1, c2, 0f,
                            new Vector2(slotCoord.X, slotCoord.W),
                            new Vector2(slotCoord.Z, slotCoord.Y), color);
                }
            }

            Matrix matrix = Matrix.CreateTranslation(-(screenSize.X / 2), -(screenSize.Y / 2), 0)
                * Matrix.CreateRotationZ(-Vector2.Angle(-Vector2.UnitY, m_componentPlayer.ComponentBody.Matrix.Forward.XZ))
                * Matrix.CreateScale(MapScale)
                * Matrix.CreateTranslation(center.X, center.Y, 0);

            batchUp.TransformTriangles(matrix);
            batchDown.TransformTriangles(matrix);
            m_renderCenter = center;
            m_renderState = 2;
        }

        public void Draw(Camera camera, int drawOrder)
        {
            if (m_componentPlayer == null || MapType) return;
            if (m_renderState == 2) { SwapBuffers(); m_renderState = 0; if (!m_hasFirstFrame) m_hasFirstFrame = true; }
            if (!m_hasFirstFrame) return;
            DrawMap(m_activeUp, m_activeDown, m_displayCenter);
        }

        private void SwapBuffers()
        {
            var tmpUp = m_activeUp; m_activeUp = m_renderUp; m_renderUp = tmpUp;
            var tmpDown = m_activeDown; m_activeDown = m_renderDown; m_renderDown = tmpDown;
            m_displayCenter = m_renderCenter;
        }

        private void DrawMap(TexturedBatch2D batchUp, TexturedBatch2D batchDown, Vector2 center)
        {
            if (batchUp == null || batchDown == null) return;

            Matrix matrix = Matrix.CreateTranslation(-center.X, -center.Y, 0)
                * Matrix.CreateRotationZ(-Vector2.Angle(LookVector, m_componentPlayer.ComponentBody.Matrix.Forward.XZ))
                * Matrix.CreateTranslation(center.X, center.Y, 0);

            batchUp.TransformTriangles(matrix);
            batchDown.TransformTriangles(matrix);

            m_indicatorBatch.Clear();
            Matrix arrowMatrix = Matrix.CreateTranslation(-center.X, -center.Y, 0)
                * Matrix.CreateRotationZ(0.5633f)
                * Matrix.CreateTranslation(center.X, center.Y, 0);

            m_indicatorBatch.QueueQuad(
                new Vector2(center.X - 4, center.Y - 4),
                new Vector2(center.X + 4, center.Y + 4),
                1f, Vector2.Zero, Vector2.One, Color.LightYellow);
            m_indicatorBatch.TransformTriangles(arrowMatrix, m_indicatorBatch.TriangleVertices.Count);

            batchUp.Flush(false);
            batchDown.Flush(false);
            m_indicatorBatch.Flush();
            LookVector = m_componentPlayer.ComponentBody.Matrix.Forward.XZ;
        }

        private static Texture2D LoadEmbeddedTexture(string resourceName)
        {
            var asm = Assembly.GetExecutingAssembly();
            var stream = asm.GetManifestResourceStream(resourceName);
            try { return Texture2D.Load(stream); }
            finally { stream.Dispose(); }
        }

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            try
            {
                base.Load(valuesDictionary, idToEntityMap);

                Tool.CalculateSlotTexCoordTables();

                m_componentPlayer = (ComponentPlayer)this.Entity.FindComponent<ComponentPlayer>(true);

                MapTexture = (Texture2D)ContentManager.Get<Texture2D>("Textures/Blocks");

                var texNormal = LoadEmbeddedTexture("SurvivalcraftMiniMap.Content.SuMapButton.png");
                var texPressed = LoadEmbeddedTexture("SurvivalcraftMiniMap.Content.SuMapButton_Pressed.png");

                StackPanelWidget stackPanelWidget = m_componentPlayer.GameWidget.Children
                    .Find<StackPanelWidget>("MoreContents", true);

                mapButton = new BitmapButtonWidget
                {
                    Text = "",
                    Size = new Vector2(68f, 64f),
                    Margin = new Vector2(4f, 0f),
                    NormalSubtexture = new Subtexture(texNormal, Vector2.Zero, Vector2.One),
                    ClickedSubtexture = new Subtexture(texPressed, Vector2.Zero, Vector2.One)
                };

                stackPanelWidget.Children.Add(mapButton);

                LookVector = m_componentPlayer.ComponentBody.Matrix.Forward.XZ;
                subsystemTerrain = this.Project.FindSubsystem<SubsystemTerrain>(true);

                m_activeUp = m_prUp1.TexturedBatch(MapTexture,
                    depthStencilState: DepthStencilState.None,
                    blendState: BlendState.Opaque,
                    samplerState: SamplerState.LinearClamp);
                m_activeDown = m_prDown1.TexturedBatch(MapTexture,
                    depthStencilState: DepthStencilState.None,
                    blendState: BlendState.Opaque,
                    samplerState: SamplerState.LinearClamp);
                m_renderUp = m_prUp2.TexturedBatch(MapTexture,
                    depthStencilState: DepthStencilState.None,
                    blendState: BlendState.Opaque,
                    samplerState: SamplerState.LinearClamp);
                m_renderDown = m_prDown2.TexturedBatch(MapTexture,
                    depthStencilState: DepthStencilState.None,
                    blendState: BlendState.Opaque,
                    samplerState: SamplerState.LinearClamp);

                m_indicatorBatch = m_prIndicator.TexturedBatch(
                    (Texture2D)ContentManager.Get<Texture2D>("Textures/Gui/SoftwareMouseCursor"),
                    depthStencilState: DepthStencilState.None,
                    blendState: BlendState.AlphaBlend,
                    samplerState: SamplerState.PointWrap);

#if WINDOWS
                mapRadius = 100; MapScale = 1.4f; RmapRadius = 0.55f;
#else
                mapRadius = 100; MapScale = 1.8f; RmapRadius = 0.55f;
#endif
            }
            catch (Exception ex)
            {
                Log.Error("SuComponentMap.Load FAILED: " + ex);
                throw;
            }
        }

        private int GetTopContent(int pointX, int pointY)
        {
            return subsystemTerrain.Terrain.GetCellContents(
                pointX,
                subsystemTerrain.Terrain.GetTopHeight(pointX, pointY),
                pointY);
        }

        public SuComponentMap() : base() { }

        public int[] DrawOrders => m_drawOrders;
    }
}
