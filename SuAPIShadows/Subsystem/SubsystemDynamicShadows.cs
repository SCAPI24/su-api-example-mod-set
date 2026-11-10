using System;
using System.Collections.Generic;
using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;
using SuAPI;
using TemplatesDatabase;
using SuAPIShadows.Foliage;

namespace SuAPIShadows;

public sealed partial class SubsystemDynamicShadows : SubsystemBlockBehavior, IDrawable, IUpdateable
{
    // Source: Block.GetEmittedLightAmount; local electric lights use a 16-cell falloff range.
    private const float ElectricLightRadius = 16f;
    // Source: SubsystemPickables.Draw; small dropped blocks need a bounded shadow pass.
    private const int MaxPickableShadows = 16;
    // Source: Engine/Matrix.cs:Matrix.CreateLookAt; one shared atlas carries a bounded set of
    // local terrain point shadows. Each selected lamp costs six cube faces.
    private const int MaxTerrainPointShadowLights = 10;
    // Source: Block.GetEmittedLightAmount; overlapping local lights compete inside the same
    // physical 16-cell influence area, so cap the number of point-shadow casters per area.
    private const int MaxTerrainPointShadowLightsPerArea = 3;

    // Source: Point2.GetHashCode; avoid X+Y collisions in dense light grids.
    private sealed class Point2Comparer : IEqualityComparer<Point2>
    {
        public bool Equals(Point2 first, Point2 second)
        {
            return first.X == second.X && first.Y == second.Y;
        }

        public int GetHashCode(Point2 point)
        {
            return unchecked((point.X * 397) ^ point.Y);
        }
    }

    // Source: Point3.GetHashCode; spatial light buckets require a 3D hash.
    private sealed class Point3Comparer : IEqualityComparer<Point3>
    {
        public bool Equals(Point3 first, Point3 second)
        {
            return first.X == second.X && first.Y == second.Y && first.Z == second.Z;
        }

        public int GetHashCode(Point3 point)
        {
            return unchecked(((point.X * 397) ^ point.Y) * 397 ^ point.Z);
        }
    }

    private readonly struct PointLight
    {
        public PointLight(
            object key,
            Vector3 position,
            Vector3 direction,
            float strength,
            float radius,
            bool isDirectional,
            bool castsTerrainShadow)
        {
            Key = key;
            Position = position;
            Direction = direction;
            Strength = strength;
            Radius = radius;
            IsDirectional = isDirectional;
            CastsTerrainShadow = castsTerrainShadow;
        }

        public object Key { get; }

        public Vector3 Position { get; }

        public Vector3 Direction { get; }

        public float Strength { get; }

        public float Radius { get; }

        public bool IsDirectional { get; }

        public bool CastsTerrainShadow { get; }
    }

    private sealed class ShadowLayer
    {
        public bool Initialized;
        public object LightKey;
        public TerrainRaycastResult ReceiverHit;
        public Vector3 Center;
        public Vector3 Direction = Vector3.UnitZ;
        public Vector3 Normal = Vector3.UnitY;
        public float Length;
        public float Width;
        public float Alpha;
        public Vector3 TargetCenter;
        public Vector3 TargetDirection = Vector3.UnitZ;
        public Vector3 TargetNormal = Vector3.UnitY;
        public float TargetLength;
        public float TargetWidth;
        public float TargetAlpha;
        public Vector3 RayDirection = -Vector3.UnitY;
        public Vector3 TargetRayDirection = -Vector3.UnitY;
    }

    private sealed class TerrainShadowState
    {
        public RenderTarget2D Target;
        public VegetationMotionFrame VegetationSnapshot;
        public double NextVegetationUpdate;
        public RenderTarget2D DynamicTarget;
        public RenderTarget2D NearDynamicTarget;
        public Matrix NearDynamicMatrix;
        public Vector2 NearDynamicOrigin;
        public Vector3 NearDynamicCenter;
        public bool NearDynamicValid;
        public bool DynamicValid;
        public long DynamicRevision = long.MinValue;
        public double NextDynamicUpdate;
        public Vector3 LastLightDirection;
        public float Coverage;
        public PointShadowState PointShadows;
        public ShadowGeometrySnapshot CachedGeometry = new ShadowGeometrySnapshot();
        public ShadowGeometrySnapshot PendingGeometry = new ShadowGeometrySnapshot();
        public Matrix ShadowMatrix;
        public Vector2 ShadowOrigin;
        public readonly object[] PointLightKeys = new object[MaxTerrainPointShadowLights];
        public readonly Vector2[] PointShadowOrigins = new Vector2[MaxTerrainPointShadowLights];
        public readonly Vector4[] PointLightPositionRadius = new Vector4[MaxTerrainPointShadowLights];
        public readonly Vector4[] PointLightDirectionStrength = new Vector4[MaxTerrainPointShadowLights];
        public Vector3 AnchorCenter;
        public double NextUpdateTime;
        public bool HasAnchorCenter;
        public bool IsValid;
        public bool IsPointValid;
        public int PointLightCount;
        public int Resolution;
        public int LastDrawFrame;
    }

    private static readonly int[] s_drawOrders = { -1, 2, 199 };
    // Source: Engine/Matrix.cs:Matrix.CreateLookAt; six 90-degree views form a cube atlas.
    private static readonly Vector3[] s_cubeDirections =
    {
        Vector3.UnitX,
        -Vector3.UnitX,
        Vector3.UnitY,
        -Vector3.UnitY,
        Vector3.UnitZ,
        -Vector3.UnitZ
    };
    private static readonly Vector3[] s_cubeUps =
    {
        Vector3.UnitY,
        Vector3.UnitY,
        Vector3.UnitZ,
        Vector3.UnitZ,
        Vector3.UnitY,
        Vector3.UnitY
    };
    private static readonly object s_celestialKey = new object();
    private static int[] s_handledBlocks;

    private readonly Dictionary<ComponentModel, bool> m_suppressedOriginalShadows =
        new Dictionary<ComponentModel, bool>();
    private readonly ModelShadowRenderer m_modelShadowRenderer = new ModelShadowRenderer();
    private EmissiveTerrainRenderer m_emissiveRenderer;
    private readonly Dictionary<Point3, PointLight> m_blockLights =
        new Dictionary<Point3, PointLight>(new Point3Comparer());
    private readonly Dictionary<Point3, List<Point3>> m_blockLightBuckets =
        new Dictionary<Point3, List<Point3>>(new Point3Comparer());
    private readonly Dictionary<Point2, List<Point3>> m_chunkLights =
        new Dictionary<Point2, List<Point3>>(new Point2Comparer());
    private readonly Dictionary<Point3, List<PointLight>> m_glowLightBuckets =
        new Dictionary<Point3, List<PointLight>>(new Point3Comparer());
    private Dictionary<GlowPoint, PointLight> m_glowLights =
        new Dictionary<GlowPoint, PointLight>();
    private Dictionary<GlowPoint, PointLight> m_nextGlowLights =
        new Dictionary<GlowPoint, PointLight>();
    private readonly PointLight[] m_selectedTerrainPointLights =
        new PointLight[MaxTerrainPointShadowLights];
    private readonly Dictionary<Point3, int> m_lightBucketRevisions =
        new Dictionary<Point3, int>(new Point3Comparer());
    private readonly List<PointLight> m_candidates = new List<PointLight>(16);
    private readonly float[] m_terrainPointLightScores =
        new float[MaxTerrainPointShadowLights];
    private readonly PrimitivesRenderer3D m_renderer = new PrimitivesRenderer3D();
    private readonly ShadowLayer m_pickableShadowLayer = new ShadowLayer();
    private readonly Dictionary<Camera, TerrainShadowState> m_terrainShadowStates =
        new Dictionary<Camera, TerrainShadowState>();

    private SubsystemTerrain m_terrain;
    private SubsystemSky m_sky;
    private SubsystemTimeOfDay m_timeOfDay;
    private SubsystemWeather m_weather;
    private SubsystemGlow m_glow;
    private SubsystemElectricity m_electricity;
    private SubsystemPickables m_pickables;
    private TexturedBatch3D m_batch;
    private Shader m_originalOpaqueTerrainShader;
    private Shader m_originalAlphaTestedTerrainShader;
    private Shader m_shadowedOpaqueTerrainShader;
    private Shader m_shadowedAlphaTestedTerrainShader;
    private Shader m_shadowMapShader;
    private Shader m_pointShadowMapShader;
    private bool m_terrainShadowsAvailable;
    private Exception m_renderFailure;
    private bool m_renderFailureReported;
    private ModFieldRef<SubsystemGlow, Dictionary<GlowPoint, bool>> m_glowPointsRef;
    private double m_nextGlowRefresh;
    private float m_averageFrameDuration = 1f / 60f;
    private float m_overBudgetTime;
    private float m_recoveryTime;
    private int m_qualityPenalty;
    private int m_nextLightRevision;

    public override int[] HandledBlocks
    {
        get
        {
            if (s_handledBlocks == null)
                s_handledBlocks = FindLightEmittingBlocks();
            return s_handledBlocks;
        }
    }

    public int[] DrawOrders => s_drawOrders;

    public UpdateOrder UpdateOrder => UpdateOrder.Default;

    public void Update(float dt)
    {
        // Source: SubsystemDrawing.cs:Draw swallows exceptions; surface a recorded failure once at the update boundary.
        if (m_renderFailure != null)
        {
            if (!m_renderFailureReported)
            {
                m_renderFailureReported = true;
                ExceptionManager.ReportExceptionToUser("[SuAPIShadows] Shadow rendering stopped.", m_renderFailure);
                DialogsManager.ShowDialog(null, new MessageDialog("SuAPIShadows",
                    "光影绘制已停止，请查看日志中的具体原因。\n" + m_renderFailure.Message, "确定", null, null));
            }
            return;
        }
        try
        {
            m_vegetation.Update();
        }
        catch (Exception error)
        {
            m_renderFailure = new InvalidOperationException("[SuAPIShadows] Vegetation update failed.", error);
            return;
        }
        // Source: Engine.Time.FrameDuration and SubsystemGlow.Draw
        UpdatePerformanceBudget(dt);
        if (Time.RealTime >= m_nextGlowRefresh)
        {
            RefreshGlowLights();
            m_nextGlowRefresh = Time.RealTime + 0.1;
        }
    }

    // Source: SubsystemDrawing.cs:Draw; SubsystemTerrain.cs:Draw — -1 binds, 2 restores emission, 199 builds next frame's maps.
    public void Draw(Camera camera, int drawOrder)
    {
        if (m_renderFailure != null) return;
        try { DrawCore(camera, drawOrder); }
        catch (Exception error)
        {
            try { m_vegetationBounds.End(); }
            catch (Exception cleanupError) { error = new AggregateException(error, cleanupError); }
            m_renderFailure = new InvalidOperationException(
                "[SuAPIShadows] Drawing stage " + drawOrder + " failed.", error);
        }
    }

    private void DrawCore(Camera camera, int drawOrder)
    {
        if (camera == null) return;
        if (drawOrder == -1)
        {
            CaptureVegetation(camera);
            PrepareTerrainShadowReceiver(camera);
            ApplyVegetation(m_shadowedOpaqueTerrainShader, m_vegetationFrame);
            ApplyVegetation(m_shadowedAlphaTestedTerrainShader, m_vegetationFrame);
            if (m_vegetationFrame.Enabled) m_vegetationBounds.Begin(m_terrain);
            return;
        }
        if (drawOrder == 2)
        {
            m_vegetationBounds.End();
            m_emissiveRenderer.Draw(camera, m_terrain, m_sky);
            return;
        }
        if (!SettingsManager.ObjectsShadowsEnabled) return;
        CaptureVegetation(camera);
        m_modelShadowRenderer.Prepare(camera, 128f);
        UpdateTerrainShadowMap(camera);
        if (m_terrainShadowStates.TryGetValue(camera, out TerrainShadowState state))
        {
            UpdateDynamicSunShadow(camera, state);
            UpdatePointShadows(camera, state);
        }
        DrawPickableShadows(camera);
        RemoveStaleTerrainShadowStates(camera);
        m_renderer.Flush(camera.ViewProjectionMatrix);
    }

    // Source: SubsystemModelsRenderer.cs:OnEntityAdded/DrawModelsExtras — register actual models before suppressing native circles.
    protected override void OnEntityAdded(Entity entity)
    {
        m_modelShadowRenderer.AddEntity(entity);
        foreach (ComponentModel model in entity.FindComponents<ComponentModel>())
        {
            if (!model.CastsShadow) continue;
            m_suppressedOriginalShadows.Add(model, true);
            model.CastsShadow = false;
        }
    }

    protected override void OnEntityRemoved(Entity entity)
    {
        m_modelShadowRenderer.RemoveEntity(entity);
        foreach (ComponentModel model in entity.FindComponents<ComponentModel>())
            RestoreOriginalModelShadow(model);
    }

    public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
    {
        // Source: TerrainUpdater.GenerateChunkLightSources
        UpdateBlockLight(value, x, y, z);
        m_emissiveRenderer.Observe(value, x, y, z);
    }

    public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
    {
        UpdateBlockLight(value, x, y, z);
        m_emissiveRenderer.Observe(value, x, y, z);
        InvalidateKelpColumn(value, oldValue, x, z);
    }

    public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
    {
        RemoveBlockLight(new Point3(x, y, z));
        m_emissiveRenderer.Observe(newValue, x, y, z);
        InvalidateKelpColumn(newValue, value, x, z);
    }

    public override void OnBlockModified(int value, int oldValue, int x, int y, int z)
    {
        UpdateBlockLight(value, x, y, z);
        m_emissiveRenderer.Observe(value, x, y, z);
        InvalidateKelpColumn(value, oldValue, x, z);
    }

    public override void OnChunkDiscarding(TerrainChunk chunk)
    {
        // Source: SubsystemBlockBehavior.OnChunkDiscarding
        m_emissiveRenderer.DiscardChunk(chunk);
        m_vegetationGeometry.Discard(chunk);
        Point2 chunkKey = chunk.Coords;
        if (!m_chunkLights.TryGetValue(chunkKey, out List<Point3> points))
            return;

        Point3[] copy = new Point3[points.Count];
        points.CopyTo(copy);
        for (int i = 0; i < copy.Length; i++)
            RemoveBlockLight(copy[i]);
    }

    protected override void Load(ValuesDictionary valuesDictionary)
    {
        base.Load(valuesDictionary);
        ShadowsMod.CheckFoliage();
        m_terrain = Project.FindSubsystem<SubsystemTerrain>(true);
        m_sky = Project.FindSubsystem<SubsystemSky>(true);
        m_timeOfDay = Project.FindSubsystem<SubsystemTimeOfDay>(true);
        m_weather = Project.FindSubsystem<SubsystemWeather>(true);
        m_glow = Project.FindSubsystem<SubsystemGlow>(true);
        m_electricity = Project.FindSubsystem<SubsystemElectricity>(true);
        m_pickables = Project.FindSubsystem<SubsystemPickables>(true);

        // Source: SubsystemGlow.m_glowPoints and IModParentField.BindFieldRef
        m_glowPointsRef = ModManager.Instance.ModParentField
            .BindFieldRef<SubsystemGlow, Dictionary<GlowPoint, bool>>("m_glowPoints");
        m_batch = m_renderer.TexturedBatch(
            ContentManager.Get<Texture2D>("Textures/Shadow"),
            useAlphaTest: false,
            0,
            DepthStencilState.DepthRead,
            RasterizerState.CullNoneScissor,
            BlendState.AlphaBlend,
            SamplerState.LinearClamp);
        InitializeTerrainShadows();
        m_emissiveRenderer = new EmissiveTerrainRenderer();
        m_vegetation = new VegetationMotionState(m_terrain, Project.FindSubsystem<SubsystemTime>(true),
            Project.FindSubsystem<SubsystemSeasons>(true), m_weather, m_terrain.SubsystemGameInfo,
            Project.FindSubsystem<SubsystemBodies>(true));
        Display.DeviceReset += InvalidateShadowTargets;
    }

    public override void Dispose()
    {
        Display.DeviceReset -= InvalidateShadowTargets;
        m_vegetationBounds.Dispose();
        m_vegetationGeometry.Clear();
        m_vegetation = null;
        m_vegetationFrame = null;
        m_modelShadowRenderer.Dispose();
        m_emissiveRenderer?.Dispose();
        RestoreOriginalModelShadows();
        RestoreOriginalTerrainShaders();
        DisposeTerrainShadowStates();
        m_shadowedOpaqueTerrainShader?.Dispose();
        m_shadowedAlphaTestedTerrainShader?.Dispose();
        m_shadowMapShader?.Dispose();
        m_pointShadowMapShader?.Dispose();
        base.Dispose();
    }

    private void InitializeTerrainShadows()
    {
        try
        {
            // Source: Survivalcraft/Game/TerrainRenderer.cs:TerrainRenderer.TerrainRenderer
            TerrainRenderer renderer = m_terrain.TerrainRenderer;
            m_originalOpaqueTerrainShader = ModManager.Instance.ModParentField
                .GetParentField<Shader>(renderer, "m_opaqueShader", typeof(TerrainRenderer));
            m_originalAlphaTestedTerrainShader = ModManager.Instance.ModParentField
                .GetParentField<Shader>(renderer, "m_alphaTestedShader", typeof(TerrainRenderer));
            m_shadowedOpaqueTerrainShader = new Shader(
                TerrainShadowShaders.TerrainVertex,
                TerrainShadowShaders.TerrainPixel);
            m_shadowedAlphaTestedTerrainShader = new Shader(
                TerrainShadowShaders.TerrainVertex,
                TerrainShadowShaders.TerrainPixel,
                new ShaderMacro("ALPHATESTED"));
            m_shadowMapShader = new Shader(
                TerrainShadowShaders.ShadowMapVertex,
                TerrainShadowShaders.ShadowMapPixel);
            m_pointShadowMapShader = new Shader(
                TerrainShadowShaders.PointShadowMapVertex,
                TerrainShadowShaders.PointShadowMapPixel);
            ModManager.Instance.ModParentField.ModifyParentField(
                renderer, "m_opaqueShader", m_shadowedOpaqueTerrainShader,
                typeof(TerrainRenderer));
            ModManager.Instance.ModParentField.ModifyParentField(
                renderer, "m_alphaTestedShader", m_shadowedAlphaTestedTerrainShader,
                typeof(TerrainRenderer));
            m_terrainShadowsAvailable = true;
        }
        catch (Exception ex)
        {
            FailTerrainShadows(ex);
        }
    }

    private void PrepareTerrainShadowReceiver(Camera camera)
    {
        if (!m_terrainShadowsAvailable || camera == null)
            return;
        try
        {
            if (!m_terrainShadowStates.TryGetValue(camera, out TerrainShadowState state))
            {
                state = new TerrainShadowState();
                m_terrainShadowStates.Add(camera, state);
            }
            state.LastDrawFrame = Time.FrameIndex;
            EnsureTerrainShadowTarget(state, 512);
            EnsureDynamicShadowTarget(state);

            bool hasCelestialLight = TryGetCelestialShadow(
                out _,
                out float celestialStrength);
            bool enabled = SettingsManager.ObjectsShadowsEnabled &&
                state.IsValid && hasCelestialLight;
            float celestialLightFloor = GetCelestialLightFloor();
            SetTerrainShadowParameters(
                m_shadowedOpaqueTerrainShader,
                state,
                enabled,
                celestialStrength,
                celestialLightFloor,
                m_weather.FogIntensity);
            SetTerrainShadowParameters(
                m_shadowedAlphaTestedTerrainShader,
                state,
                enabled,
                celestialStrength,
                celestialLightFloor,
                m_weather.FogIntensity);
        }
        catch (Exception ex)
        {
            FailTerrainShadows(ex);
        }
    }

    private float GetTerrainPointShadowSearchRadius()
    {
        // Source: TerrainRenderer visible chunk selection. This is a candidate search radius,
        // not a light falloff radius; the selected light still must contain the receiver point.
        return m_qualityPenalty switch
        {
            0 => 48f,
            1 => 40f,
            _ => 32f
        };
    }

    // Source: SubsystemDynamicShadows.PointShadows.cs:UpdatePointShadows — budget work, not supported lights.
    private int GetTerrainPointShadowLightLimit() => MaxTerrainPointShadowLights;

    private Vector3 GetTerrainPointShadowSample(Camera camera)
    {
        // Source: ComponentMiner.Raycast and TerrainRenderer.Draw. Point-light shadow maps
        // should follow the terrain the player is looking at, not a fixed point four cells
        // in front of the camera; otherwise torch shadows disappear as soon as the player
        // steps back while still looking at the lit area.
        TerrainRaycastResult? hit = m_terrain.Raycast(
            camera.ViewPosition,
            camera.ViewPosition + 80f * camera.ViewDirection,
            useInteractionBoxes: false,
            skipAirBlocks: true,
            (value, rayDistance) =>
                BlocksManager.Blocks[Terrain.ExtractContents(value)].ObjectShadowStrength > 0.25f ||
                IsTerrainPointLightBlock(value));
        if (hit.HasValue)
        {
            int value = m_terrain.Terrain.GetCellValue(
                hit.Value.CellFace.X,
                hit.Value.CellFace.Y,
                hit.Value.CellFace.Z);
            if (IsTerrainPointLightBlock(value))
                return camera.ViewPosition;
            return hit.Value.HitPoint(0.02f);
        }
        return camera.ViewPosition + 8f * camera.ViewDirection;
    }

    private static bool IsTerrainPointLightBlock(int value)
    {
        // Source: TorchBlock, WickerLampBlock, LightbulbBlock and LED electric
        // elements.  Looking directly at a mounted light should keep that light
        // selected for its own surface, instead of sampling the back side of the
        // light geometry and dropping its directional score to zero.
        Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
        return block is TorchBlock ||
            block is WickerLampBlock ||
            block is LightbulbBlock ||
            block is LedBlock ||
            block is OneLedBlock ||
            block is FourLedBlock ||
            block is MulticoloredLedBlock ||
            block is SevenSegmentDisplayBlock;
    }

    private static Vector3 GetStableShadowAnchor(
        TerrainShadowState state, Vector3 cameraPosition)
    {
        // Source: Survivalcraft/Game/SubsystemSky.cs:SubsystemSky.ViewHazeStart
        // Keep the projection fixed inside a small camera dead zone. This prevents the shadow
        // map from being reprojected for every movement sample while preserving ample coverage.
        Vector2 offset = cameraPosition.XZ - state.AnchorCenter.XZ;
        if (!state.HasAnchorCenter || offset.LengthSquared() > 36f ||
            MathUtils.Abs(cameraPosition.Y - state.AnchorCenter.Y) > 6f)
        {
            state.AnchorCenter = cameraPosition;
            state.HasAnchorCenter = true;
        }
        return state.AnchorCenter;
    }

    private void RenderTerrainShadowMap(
        RenderTarget2D target,
        Vector2 shadowOrigin,
        Matrix shadowMatrix,
        Vector3 center,
        float coverage, VegetationMotionFrame vegetation)
    {
        RenderTarget2D previousTarget = Display.RenderTarget;
        Viewport previousViewport = Display.Viewport;
        Rectangle previousScissor = Display.ScissorRectangle;
        BlendState previousBlend = Display.BlendState;
        DepthStencilState previousDepth = Display.DepthStencilState;
        RasterizerState previousRasterizer = Display.RasterizerState;
        try
        {
            Display.RenderTarget = target;
            Display.Clear(Color.White, 1f, 0);
            Display.BlendState = BlendState.Opaque;
            Display.DepthStencilState = DepthStencilState.Default;
            Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;
            ApplyVegetation(m_shadowMapShader, vegetation);
            m_shadowMapShader.GetParameter("u_shadowOrigin").SetValue(shadowOrigin);
            m_shadowMapShader.GetParameter("u_shadowMatrix").SetValue(shadowMatrix);
            m_shadowMapShader.GetParameter("u_texture").SetValue(
                m_terrain.SubsystemAnimatedTextures.AnimatedBlocksTexture);
            m_shadowMapShader.GetParameter("u_samplerState").SetValue(
                SamplerState.PointClamp);
            m_shadowMapShader.GetParameter("u_alphaThreshold").SetValue(0.5f);

            float rangeSquared = MathUtils.Sqr(0.8f * coverage);
            foreach (TerrainChunk chunk in m_terrain.Terrain.AllocatedChunks)
            {
                if (chunk.Geometry == null || chunk.Geometry.Buffers.Count == 0 ||
                    Vector2.DistanceSquared(chunk.Center, center.XZ) > rangeSquared)
                    continue;
                DrawTerrainSubsets(m_shadowMapShader, chunk.Geometry, 63);
            }
        }
        finally
        {
            Display.RenderTarget = previousTarget;
            Display.Viewport = previousViewport;
            Display.ScissorRectangle = previousScissor;
            Display.BlendState = previousBlend;
            Display.DepthStencilState = previousDepth;
            Display.RasterizerState = previousRasterizer;
        }
    }

    private static void DrawTerrainSubsets(
        Shader shader, TerrainChunkGeometry geometry, int subsetsMask)
    {
        // Source: Survivalcraft/Game/TerrainRenderer.cs:
        // TerrainRenderer.DrawTerrainChunkGeometrySubsets
        foreach (TerrainChunkGeometry.Buffer buffer in geometry.Buffers)
        {
            if (buffer.VertexBuffer == null || buffer.IndexBuffer == null)
                continue;
            int start = int.MaxValue;
            int end = 0;
            for (int i = 0; i < 7; i++)
            {
                if ((subsetsMask & (1 << i)) != 0 &&
                    buffer.SubsetIndexBufferEnds[i] > 0)
                {
                    if (start == int.MaxValue)
                        start = buffer.SubsetIndexBufferStarts[i];
                    end = buffer.SubsetIndexBufferEnds[i];
                }
                else if (end > start)
                {
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader,
                        buffer.VertexBuffer, buffer.IndexBuffer, start, end - start);
                    start = int.MaxValue;
                }
            }
            if (end > start)
            {
                Display.DrawIndexed(PrimitiveType.TriangleList, shader,
                    buffer.VertexBuffer, buffer.IndexBuffer, start, end - start);
            }
        }
    }

    private void EnsureTerrainShadowTarget(TerrainShadowState state, int resolution)
    {
        if (state.Target != null && state.Resolution == resolution)
            return;

        RenderTarget2D target = null;
        RenderTarget2D displayTarget = Display.RenderTarget;
        Viewport previousViewport = Display.Viewport;
        Rectangle previousScissor = Display.ScissorRectangle;
        try
        {
            target = new RenderTarget2D(
                resolution, resolution, 1, ColorFormat.Rgba8888, DepthFormat.Depth16);
            try
            {
                // Source: Engine/Graphics/RenderTarget2D.cs:RenderTarget2D and
                // Engine/Graphics/Display.cs:Display.RenderTarget
                Display.RenderTarget = target;
                Display.Clear(Color.White, 1f, 0);
            }
            finally
            {
                Display.RenderTarget = displayTarget;
                Display.Viewport = previousViewport;
                Display.ScissorRectangle = previousScissor;
            }
        }
        catch
        {
            target?.Dispose();
            throw;
        }
        state.Target?.Dispose();
        state.Target = target;
        state.Resolution = resolution;
        state.IsValid = false;
    }

    private float GetCelestialLightFloor()
    {
        // Source: Survivalcraft/Game/SubsystemSky.UpdateLightAndViewParameters and
        // Survivalcraft/Game/LightingManager.CalculateLightingTables.
        int light = MathUtils.Clamp(m_sky.SkyLightValue, 0, 15);
        return LightingManager.LightIntensityByLightValue[light];
    }

    private void FailTerrainShadows(Exception exception)
    {
        m_terrainShadowsAvailable = false;
        RestoreOriginalTerrainShaders();
        DisposeTerrainShadowStates();
        throw new InvalidOperationException("[SuAPIShadows] Terrain shadow setup failed.", exception);
    }

    private void RestoreOriginalTerrainShaders()
    {
        if (m_terrain?.TerrainRenderer == null)
            return;
        TerrainRenderer renderer = m_terrain.TerrainRenderer;
        try
        {
            Shader opaque = ModManager.Instance.ModParentField.GetParentField<Shader>(
                renderer, "m_opaqueShader", typeof(TerrainRenderer));
            if (ReferenceEquals(opaque, m_shadowedOpaqueTerrainShader) &&
                m_originalOpaqueTerrainShader != null)
            {
                ModManager.Instance.ModParentField.ModifyParentField(
                    renderer, "m_opaqueShader", m_originalOpaqueTerrainShader,
                    typeof(TerrainRenderer));
            }
            Shader alphaTested = ModManager.Instance.ModParentField.GetParentField<Shader>(
                renderer, "m_alphaTestedShader", typeof(TerrainRenderer));
            if (ReferenceEquals(alphaTested, m_shadowedAlphaTestedTerrainShader) &&
                m_originalAlphaTestedTerrainShader != null)
            {
                ModManager.Instance.ModParentField.ModifyParentField(
                    renderer, "m_alphaTestedShader", m_originalAlphaTestedTerrainShader,
                    typeof(TerrainRenderer));
            }
        }
        catch
        {
        }
    }

    private void DisposeTerrainShadowStates()
    {
        foreach (TerrainShadowState state in m_terrainShadowStates.Values)
        {
            state.Target?.Dispose();
            state.DynamicTarget?.Dispose();
            state.NearDynamicTarget?.Dispose();
            DisposePointShadows(state);
        }
        m_terrainShadowStates.Clear();
    }

    private void RemoveStaleTerrainShadowStates(Camera activeCamera)
    {
        if (Time.FrameIndex % 120 != 0)
            return;
        List<Camera> stale = null;
        foreach (KeyValuePair<Camera, TerrainShadowState> pair in m_terrainShadowStates)
        {
            if (ReferenceEquals(pair.Key, activeCamera) ||
                Time.FrameIndex - pair.Value.LastDrawFrame <= 240)
                continue;
            stale ??= new List<Camera>();
            stale.Add(pair.Key);
        }
        if (stale == null)
            return;
        for (int i = 0; i < stale.Count; i++)
        {
            m_terrainShadowStates[stale[i]].Target?.Dispose();
            m_terrainShadowStates[stale[i]].DynamicTarget?.Dispose();
            m_terrainShadowStates[stale[i]].NearDynamicTarget?.Dispose();
            DisposePointShadows(m_terrainShadowStates[stale[i]]);
            m_terrainShadowStates.Remove(stale[i]);
        }
    }

    private void RestoreOriginalModelShadows()
    {
        foreach (KeyValuePair<ComponentModel, bool> pair in m_suppressedOriginalShadows)
            pair.Key.CastsShadow = pair.Value;
        m_suppressedOriginalShadows.Clear();
    }

    private void RestoreOriginalModelShadow(ComponentModel model)
    {
        if (m_suppressedOriginalShadows.TryGetValue(model, out bool castsShadow))
        {
            model.CastsShadow = castsShadow;
            m_suppressedOriginalShadows.Remove(model);
        }
    }

    private static int[] FindLightEmittingBlocks()
    {
        // Source: TorchBlock.Index, WickerLampBlock.Index and LightbulbBlock.Index
        return new[]
        {
            TorchBlock.Index,
            WickerLampBlock.Index,
            LightbulbBlock.Index,
            ChristmasTreeBlock.Index,
            JackOLanternBlock.Index,
            TallGrassBlock.Index, RedFlowerBlock.Index, WhiteFlowerBlock.Index, PurpleFlowerBlock.Index,
            RyeBlock.Index, CottonBlock.Index, PumpkinBlock.Index, RottenPumpkinBlock.Index,
            DryBushBlock.Index, LargeDryBushBlock.Index, KelpBlock.Index, SeagrassBlock.Index,
            BirchLeavesBlock.Index, MimosaLeavesBlock.Index, OakLeavesBlock.Index, PoplarLeavesBlock.Index,
            SpruceLeavesBlock.Index, TallSpruceLeavesBlock.Index
        };
    }

    private void UpdateBlockLight(int value, int x, int y, int z)
    {
        Point3 point = new Point3(x, y, z);
        Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
        int amount = block.GetEmittedLightAmount(value);
        bool isDirectional = block is LightbulbBlock;
        bool castsTerrainShadow =
            isDirectional ||
            block is TorchBlock ||
            block is WickerLampBlock;
        if (amount <= 0)
        {
            RemoveBlockLight(point);
            return;
        }

        float strength = amount / 15f;
        Vector3 direction = isDirectional
            ? CellFace.FaceToVector3(((LightbulbBlock)block).GetFace(value))
            : Vector3.Zero;
        PointLight light = new PointLight(
            point,
            GetBlockLightPosition(block, value, x, y, z),
            direction,
            strength,
            castsTerrainShadow ? ElectricLightRadius : 3f + 12f * strength,
            isDirectional,
            castsTerrainShadow);
        if (m_blockLights.TryGetValue(point, out PointLight previous))
        {
            if (!LightsEqual(previous, light))
                MarkLightChanged(previous.Position, light.Position);
            m_blockLights[point] = light;
            return;
        }

        m_blockLights.Add(point, light);
        AddPointToBucket(m_blockLightBuckets, GetBucket(light.Position), point);
        AddPointToBucket(m_chunkLights, new Point2(x >> 4, z >> 4), point);
        MarkLightChanged(light.Position);
    }

    private static Vector3 GetBlockLightPosition(
        Block block, int value, int x, int y, int z)
    {
        // Source: LightbulbBlock.GetFace and LedElectricElement.OnAdded
        Vector3 center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
        if (block is LightbulbBlock lightbulb)
            return center - 0.36f * CellFace.FaceToVector3(lightbulb.GetFace(value));
        return center + 0.05f * Vector3.UnitY;
    }

    private void RemoveBlockLight(Point3 point)
    {
        if (!m_blockLights.TryGetValue(point, out PointLight light))
            return;

        m_blockLights.Remove(point);
        RemovePointFromBucket(m_blockLightBuckets, GetBucket(light.Position), point);
        RemovePointFromBucket(m_chunkLights, new Point2(point.X >> 4, point.Z >> 4), point);
        MarkLightChanged(light.Position);
    }

    private void RefreshGlowLights()
    {
        // Source: SubsystemGlow.Draw and LED electric element GlowPoint updates
        m_glowLightBuckets.Clear();
        m_nextGlowLights.Clear();
        ref Dictionary<GlowPoint, bool> glowPoints = ref m_glowPointsRef(m_glow);
        foreach (GlowPoint glowPoint in glowPoints.Keys)
        {
            if (glowPoint.Color.A == 0 ||
                !TryGetLedInputVoltage(glowPoint, out float voltage))
                continue;

            float strength = MathUtils.Saturate(voltage);
            if (strength <= 0.02f)
                continue;

            PointLight light = new PointLight(
                glowPoint,
                glowPoint.Position - 0.05f * glowPoint.Forward,
                SafeNormalize(glowPoint.Forward),
                strength,
                ElectricLightRadius,
                isDirectional: true,
                castsTerrainShadow: true);
            m_nextGlowLights.Add(glowPoint, light);
            Point3 bucket = GetBucket(light.Position);
            if (!m_glowLightBuckets.TryGetValue(bucket, out List<PointLight> lights))
            {
                lights = new List<PointLight>();
                m_glowLightBuckets.Add(bucket, lights);
            }
            lights.Add(light);
        }

        foreach (KeyValuePair<GlowPoint, PointLight> pair in m_glowLights)
        {
            if (!m_nextGlowLights.TryGetValue(pair.Key, out PointLight current))
                MarkLightChanged(pair.Value.Position);
            else if (!LightsEqual(pair.Value, current))
                MarkLightChanged(pair.Value.Position, current.Position);
        }
        foreach (KeyValuePair<GlowPoint, PointLight> pair in m_nextGlowLights)
        {
            if (!m_glowLights.ContainsKey(pair.Key))
                MarkLightChanged(pair.Value.Position);
        }

        Dictionary<GlowPoint, PointLight> previous = m_glowLights;
        m_glowLights = m_nextGlowLights;
        m_nextGlowLights = previous;
    }

    private bool TryGetLedInputVoltage(GlowPoint glowPoint, out float voltage)
    {
        // Source: LedElectricElement.CalculateVoltage and
        // MulticoloredLedElectricElement.Simulate
        voltage = 0f;
        float bestDistanceSquared = 0.25f;
        ElectricElement bestElement = null;
        Vector3 position = glowPoint.Position;
        int x = Terrain.ToCell(position.X);
        int y = Terrain.ToCell(position.Y);
        int z = Terrain.ToCell(position.Z);
        for (int i = x - 1; i <= x + 1; i++)
        {
            for (int j = y - 1; j <= y + 1; j++)
            {
                for (int k = z - 1; k <= z + 1; k++)
                {
                    int value = m_terrain.Terrain.GetCellValue(i, j, k);
                    Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
                    if (!(block is LedBlock || block is OneLedBlock ||
                        block is FourLedBlock || block is MulticoloredLedBlock ||
                        block is SevenSegmentDisplayBlock) ||
                        block is not MountedElectricElementBlock mountedBlock)
                        continue;

                    int face = mountedBlock.GetFace(value);
                    if (Vector3.Dot(
                        glowPoint.Forward,
                        CellFace.FaceToVector3(face)) < 0.99f)
                    {
                        continue;
                    }
                    Vector3 lightPosition = new Vector3(i + 0.5f, j + 0.5f, k + 0.5f) -
                        0.4375f * CellFace.FaceToVector3(face);
                    float distanceSquared = Vector3.DistanceSquared(position, lightPosition);
                    if (distanceSquared >= bestDistanceSquared)
                        continue;
                    ElectricElement element = m_electricity.GetElectricElement(i, j, k, face);
                    if (element == null)
                        continue;
                    bestDistanceSquared = distanceSquared;
                    bestElement = element;
                }
            }
        }

        if (bestElement == null)
            return false;
        for (int i = 0; i < bestElement.Connections.Count; i++)
        {
            ElectricConnection connection = bestElement.Connections[i];
            if (connection.ConnectorType != ElectricConnectorType.Output &&
                connection.NeighborConnectorType != ElectricConnectorType.Input)
            {
                voltage = MathUtils.Max(voltage,
                    connection.NeighborElectricElement.GetOutputVoltage(
                        connection.NeighborConnectorFace));
            }
        }
        voltage = MathUtils.Saturate(voltage);
        return true;
    }

    private void DrawPickableShadows(Camera camera)
    {
        // Source: SubsystemPickables.Draw; reproduce the displayed item position so its
        // projected shadow follows the spawn lift, bobbing animation and stuck transform.
        if (m_qualityPenalty >= 2 || m_pickables == null)
            return;

        double gameTime = m_terrain.SubsystemGameInfo.TotalElapsedGameTime;
        int limit = m_qualityPenalty == 0 ? MaxPickableShadows : MaxPickableShadows / 2;
        int drawn = 0;
        float range = m_qualityPenalty == 0 ? 32f : 20f;
        float rangeSquared = range * range;
        for (int i = 0; i < m_pickables.Pickables.Count && drawn < limit; i++)
        {
            Pickable pickable = m_pickables.Pickables[i];
            Vector3 position = pickable.Position;
            float age = (float)(gameTime - pickable.CreationTime);
            if (pickable.StuckMatrix.HasValue)
            {
                position = pickable.StuckMatrix.Value.Translation;
            }
            else
            {
                position.Y += 0.25f * MathUtils.Saturate(3f * age);
                position.Y += 0.03f * (MathUtils.Sin(3f * age) + 1f);
            }

            Vector3 toPickable = position - camera.ViewPosition;
            float forwardDistance = Vector3.Dot(camera.ViewDirection, toPickable);
            float distanceSquared = toPickable.LengthSquared();
            if (forwardDistance < -0.5f || distanceSquared > rangeSquared)
                continue;

            float distance = MathUtils.Sqrt(distanceSquared);
            float fade = MathUtils.Saturate((range - distance) / 12f) *
                (1f - m_sky.CalculateFog(camera.ViewPosition, position));
            if (fade <= 0.02f)
                continue;

            bool queued = false;
            if (TryGetCelestialShadow(out Vector3 celestialDirection, out float celestialStrength) &&
                IsCelestialVisible(position, celestialDirection, 0))
            {
                queued |= QueuePickableShadow(
                    s_celestialKey,
                    position,
                    -celestialDirection,
                    10f,
                    0.3f,
                    0.3f,
                    celestialStrength * (0.38f / 0.52f) * fade);
            }

            CollectNearbyLights(position, ElectricLightRadius);
            if (TrySelectVisibleLight(position, null, out PointLight light))
            {
                Vector3 fromLight = position - light.Position;
                float lightDistance = fromLight.Length();
                if (lightDistance >= 0.2f)
                {
                    // Source: TerrainUpdater.GenerateChunkLightSources; block light drops by
                    // one level per cell, so use linear attenuation for visible dropped items.
                    float attenuation = MathUtils.Saturate(1f - lightDistance / light.Radius);
                    queued |= QueuePickableShadow(
                        light.Key,
                        position,
                        fromLight / lightDistance,
                        MathUtils.Min(light.Radius, 8f),
                        0.3f,
                        0.3f,
                        0.26f * light.Strength * attenuation * fade);
                }
            }

            if (queued)
                drawn++;
        }
    }

    private bool QueuePickableShadow(
        object lightKey,
        Vector3 position,
        Vector3 rayDirection,
        float maxDistance,
        float width,
        float height,
        float alpha)
    {
        // Source: SubsystemShadows.QueueShadow; a dropped block has no ComponentModel, so a
        // small immediate projected quad avoids allocating a per-item model or cache.
        m_pickableShadowLayer.Initialized = false;
        if (!SetProjectedShadow(
            m_pickableShadowLayer,
            lightKey,
            position,
            rayDirection,
            maxDistance,
            width,
            height,
            alpha))
        {
            return false;
        }

        QueueLayer(m_pickableShadowLayer);
        return true;
    }

    private bool SetProjectedShadow(
        ShadowLayer layer,
        object lightKey,
        Vector3 start,
        Vector3 rayDirection,
        float maxDistance,
        float bodyWidth,
        float bodyHeight,
        float alpha,
        bool preferFootReceiver = false)
    {
        TerrainRaycastResult? hit = m_terrain.Raycast(
            start,
            start + maxDistance * rayDirection,
            useInteractionBoxes: false,
            skipAirBlocks: true,
            (value, rayDistance) =>
                BlocksManager.Blocks[Terrain.ExtractContents(value)].ObjectShadowStrength > 0f);
        if (!hit.HasValue)
        {
            layer.TargetAlpha = 0f;
            return false;
        }

        TerrainRaycastResult receiverHit = hit.Value;
        Vector3 normal = CellFace.FaceToVector3(receiverHit.CellFace.Face);
        if (preferFootReceiver && MathUtils.Abs(normal.Y) < 0.5f &&
            TryGetFootReceiver(start, out TerrainRaycastResult footHit))
        {
            receiverHit = footHit;
            normal = CellFace.FaceToVector3(receiverHit.CellFace.Face);
        }

        Vector3 tangent = rayDirection - normal * Vector3.Dot(rayDirection, normal);
        if (tangent.LengthSquared() < 0.0001f)
            tangent = Vector3.Cross(normal, Vector3.UnitX);
        if (tangent.LengthSquared() < 0.0001f)
            tangent = Vector3.Cross(normal, Vector3.UnitZ);
        tangent = Vector3.Normalize(tangent);

        float incidence = MathUtils.Max(MathUtils.Abs(Vector3.Dot(rayDirection, normal)), 0.22f);
            layer.LightKey = lightKey;
            layer.TargetCenter = receiverHit.HitPoint(0.008f);
        layer.TargetDirection = tangent;
        layer.TargetNormal = normal;
        layer.TargetRayDirection = rayDirection;
        layer.ReceiverHit = receiverHit;
        layer.TargetLength = MathUtils.Clamp(bodyHeight * 0.65f / incidence, bodyWidth, 7f);
        layer.TargetWidth = MathUtils.Clamp(bodyWidth * 1.15f, 0.25f, 2.5f);
        layer.TargetAlpha = alpha;
        if (!layer.Initialized)
        {
            // Source: SubsystemShadows.QueueShadow immediate first-frame placement
            layer.Initialized = true;
            layer.Center = layer.TargetCenter;
            layer.Direction = layer.TargetDirection;
            layer.Normal = layer.TargetNormal;
            layer.RayDirection = layer.TargetRayDirection;
            layer.Length = layer.TargetLength;
            layer.Width = layer.TargetWidth;
            layer.Alpha = layer.TargetAlpha;
        }
        return true;
    }

    private bool TryGetFootReceiver(Vector3 start, out TerrainRaycastResult receiverHit)
    {
        // Source: SubsystemShadows.QueueShadow; foot circles prefer the supporting surface
        // below the body, so a nearby wall should not steal the whole character shadow.
        TerrainRaycastResult? hit = m_terrain.Raycast(
            start + 0.05f * Vector3.UnitY,
            start - 2.2f * Vector3.UnitY,
            useInteractionBoxes: false,
            skipAirBlocks: true,
            (value, rayDistance) =>
                BlocksManager.Blocks[Terrain.ExtractContents(value)].ObjectShadowStrength > 0f);
        if (hit.HasValue &&
            CellFace.FaceToVector3(hit.Value.CellFace.Face).Y > 0.5f)
        {
            receiverHit = hit.Value;
            return true;
        }

        receiverHit = default(TerrainRaycastResult);
        return false;
    }

    private void QueueLayer(ShadowLayer layer)
    {
        // Source: SubsystemShadows.DrawShadowOverQuad
        Vector3 side = Vector3.Cross(layer.Normal, layer.Direction);
        if (side.LengthSquared() < 0.0001f)
            return;
        side = Vector3.Normalize(side);
        Vector3 forward = 0.5f * layer.Length * layer.Direction;
        Vector3 right = 0.5f * layer.Width * side;
        Vector3 p1 = layer.Center - forward - right;
        Vector3 p2 = layer.Center - forward + right;
        Vector3 p3 = layer.Center + forward + right;
        Vector3 p4 = layer.Center + forward - right;
        Color color = new Color(0f, 0f, 0f, MathUtils.Saturate(layer.Alpha));
        m_batch.QueueQuad(
            p1, p2, p3, p4,
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f),
            color);
    }

    private Vector3 CalculateSunDirection()
    {
        // Source: SubsystemSky.DrawSunAndMoon and CalculateSeasonAngle
        float phase = 2f * (float)Math.PI * (m_timeOfDay.TimeOfDay - m_timeOfDay.Midday);
        float seasonAngle = -0.4f - 0.7f *
            (0.5f - 0.5f * MathUtils.Cos(
                (m_terrain.SubsystemGameInfo.WorldSettings.TimeOfYear -
                 SubsystemSeasons.MidSummer) * 2f * (float)Math.PI));
        Matrix rotation = Matrix.CreateRotationZ(-phase) *
            Matrix.CreateRotationX(seasonAngle);
        return Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, rotation));
    }

    private bool TryGetCelestialShadow(out Vector3 direction, out float strength)
    {
        // Source: SubsystemSky.DrawSunAndMoon, UpdateMoonPhase and
        // UpdateLightAndViewParameters. The moon is opposite the sun, phase 0 is full,
        // and phase 4 is the original moonless/eclipsed appearance.
        Vector3 sunDirection = CalculateSunDirection();
        Vector3 moonDirection = -sunDirection;
        // Source: SubsystemWeather.PrecipitationIntensity; full rain hides the celestial disk.
        float weatherFactor = 1f - m_weather.PrecipitationIntensity;
        float sunStrength = 0.52f * m_sky.SkyLightIntensity * weatherFactor *
            GetHorizonShadowFactor(sunDirection.Y);

        int moonPhase = m_sky.MoonPhase;
        float moonPhaseFactor = moonPhase == 4
            ? 0f
            : 0.5f + 0.5f * MathUtils.Cos(
                moonPhase * (float)Math.PI / 4f);
        float moonStrength = 0.26f * (1f - m_sky.SkyLightIntensity) *
            moonPhaseFactor * weatherFactor *
            GetHorizonShadowFactor(moonDirection.Y);

        if (sunStrength >= moonStrength)
        {
            direction = sunDirection;
            strength = sunStrength;
        }
        else
        {
            direction = moonDirection;
            strength = moonStrength;
        }
        return strength > 0.0002f;
    }

    private static float GetHorizonShadowFactor(float elevation)
    {
        // Source: SubsystemSky.CalculateLightIntensity; fade grazing celestial light before
        // low-angle depth precision turns flat snow into moving self-shadow bands.
        return MathUtils.SmoothStep(0.12f, 0.35f, elevation);
    }

    private bool IsCelestialVisible(Vector3 position, Vector3 lightDirection, int lod)
    {
        int sunlightHeight = m_terrain.Terrain.GetSunlightHeight(
            Terrain.ToCell(position.X),
            Terrain.ToCell(position.Z));
        if (position.Y + 0.5f < sunlightHeight)
            return false;
        if (lod > 1)
            return true;

        TerrainRaycastResult? hit = m_terrain.Raycast(
            position,
            position + 24f * lightDirection,
            useInteractionBoxes: false,
            skipAirBlocks: true,
            (value, rayDistance) =>
                BlocksManager.Blocks[Terrain.ExtractContents(value)].ObjectShadowStrength > 0.25f);
        return !hit.HasValue;
    }

    private bool IsLightOccluded(Vector3 lightPosition, Vector3 target)
    {
        float distance = Vector3.Distance(lightPosition, target);
        TerrainRaycastResult? hit = m_terrain.Raycast(
            lightPosition,
            target,
            useInteractionBoxes: false,
            skipAirBlocks: true,
            (value, rayDistance) =>
                !BlocksManager.Blocks[Terrain.ExtractContents(value)].IsTransparent);
        return hit.HasValue && hit.Value.Distance < distance - 0.2f;
    }

    private void CollectNearbyLights(Vector3 position, float radius)
    {
        m_candidates.Clear();
        Vector3 extent = new Vector3(radius, radius, radius);
        Point3 min = GetBucket(position - extent);
        Point3 max = GetBucket(position + extent);
        float radiusSquared = radius * radius;
        for (int x = min.X; x <= max.X; x++)
        {
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                {
                    Point3 bucket = new Point3(x, y, z);
                    if (m_blockLightBuckets.TryGetValue(bucket, out List<Point3> points))
                    {
                        for (int i = 0; i < points.Count; i++)
                        {
                            PointLight light = m_blockLights[points[i]];
                            if (Vector3.DistanceSquared(light.Position, position) <= radiusSquared)
                                m_candidates.Add(light);
                        }
                    }
                    if (m_glowLightBuckets.TryGetValue(bucket, out List<PointLight> glows))
                    {
                        for (int i = 0; i < glows.Count; i++)
                        {
                            PointLight light = glows[i];
                            if (Vector3.DistanceSquared(light.Position, position) <= radiusSquared)
                                m_candidates.Add(light);
                        }
                    }
                }
            }
        }
    }

    private bool TrySelectVisibleLight(
        Vector3 sample,
        object currentKey,
        out PointLight selected)
    {
        // Source: SubsystemTerrain.Raycast and Block.GetEmittedLightAmount
        while (TrySelectLight(sample, currentKey, out PointLight candidate))
        {
            RemoveCandidate(candidate.Key);
            float distance = Vector3.Distance(sample, candidate.Position);
            if (distance >= 0.2f && distance < candidate.Radius &&
                !IsLightOccluded(candidate.Position, sample))
            {
                selected = candidate;
                return true;
            }
        }

        selected = default(PointLight);
        return false;
    }

    private int SelectTerrainShadowLights(
        Camera camera,
        Vector3 sample,
        object[] currentKeys,
        PointLight[] selected,
        int limit)
    {
        // Source: Block.GetEmittedLightAmount and SubsystemGlow.Draw. Select a tiny stable
        // set of terrain point-shadow casters, because each selected lamp costs six terrain
        // renders into the cube atlas.
        Array.Clear(selected, 0, selected.Length);
        Array.Clear(m_terrainPointLightScores, 0, m_terrainPointLightScores.Length);
        int count = 0;
        for (int i = 0; i < m_candidates.Count; i++)
        {
            PointLight light = m_candidates[i];
            if (!light.CastsTerrainShadow)
                continue;
            float distance = Vector3.Distance(sample, light.Position);
            if (distance >= light.Radius)
                continue;
            float attenuation = MathUtils.Saturate(1f - distance / light.Radius);
            float directionalAttenuation = MathUtils.Max(
                GetDirectionalAttenuation(light, sample),
                GetDirectionalAttenuation(light, camera.ViewPosition));
            Vector3 toLight = light.Position - camera.ViewPosition;
            float cameraDistance = toLight.Length();
            float viewFactor = cameraDistance > 0.2f
                ? MathUtils.Saturate(0.35f + 0.65f *
                    Vector3.Dot(camera.ViewDirection, toLight / cameraDistance))
                : 1f;
            float cameraFactor = MathUtils.Saturate(1f - cameraDistance / 96f);
            float score = light.Strength * attenuation * attenuation *
                directionalAttenuation * (0.45f + 0.55f * viewFactor) *
                (0.55f + 0.45f * cameraFactor);
            if (score <= 0.002f)
                continue;
            for (int j = 0; j < currentKeys.Length; j++)
            {
                if (currentKeys[j] != null && Equals(currentKeys[j], light.Key))
                {
                    score *= 1.2f;
                    break;
                }
            }
            int weakestOverlap = FindWeakestOverlappingTerrainPointLight(
                selected,
                m_terrainPointLightScores,
                count,
                light,
                out int overlapCount);
            if (overlapCount >= MaxTerrainPointShadowLightsPerArea)
            {
                if (weakestOverlap < 0 ||
                    score <= m_terrainPointLightScores[weakestOverlap])
                {
                    continue;
                }
                for (int j = weakestOverlap; j < count - 1; j++)
                {
                    selected[j] = selected[j + 1];
                    m_terrainPointLightScores[j] = m_terrainPointLightScores[j + 1];
                }
                count--;
            }

            int insert = count;
            for (int j = 0; j < count; j++)
            {
                if (score > m_terrainPointLightScores[j])
                {
                    insert = j;
                    break;
                }
            }
            if (insert >= limit)
                continue;

            int upper = MathUtils.Min(count, limit - 1);
            for (int j = upper; j > insert; j--)
            {
                m_terrainPointLightScores[j] = m_terrainPointLightScores[j - 1];
                selected[j] = selected[j - 1];
            }
            m_terrainPointLightScores[insert] = score;
            selected[insert] = light;
            count = MathUtils.Min(count + 1, limit);
        }
        return count;
    }

    private static int FindWeakestOverlappingTerrainPointLight(
        PointLight[] selected,
        float[] scores,
        int count,
        PointLight light,
        out int overlapCount)
    {
        overlapCount = 0;
        int weakest = -1;
        float weakestScore = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            PointLight other = selected[i];
            float radius = light.Radius + other.Radius;
            if (Vector3.DistanceSquared(light.Position, other.Position) > radius * radius)
                continue;
            overlapCount++;
            if (scores[i] < weakestScore)
            {
                weakestScore = scores[i];
                weakest = i;
            }
        }
        return weakest;
    }

    private bool TrySelectLight(
        Vector3 sample,
        object currentKey,
        out PointLight selected)
    {
        selected = default(PointLight);
        float bestScore = 0f;
        float currentScore = 0f;
        PointLight current = default(PointLight);
        for (int i = 0; i < m_candidates.Count; i++)
        {
            PointLight light = m_candidates[i];
            float distance = Vector3.Distance(sample, light.Position);
            float attenuation = MathUtils.Saturate(1f - distance / light.Radius);
            float directionalAttenuation = GetDirectionalAttenuation(light, sample);
            float score = light.Strength * attenuation * attenuation *
                directionalAttenuation;
            if (score > bestScore)
            {
                bestScore = score;
                selected = light;
            }
            if (currentKey != null && Equals(light.Key, currentKey))
            {
                current = light;
                currentScore = score;
            }
        }

        // Source: SubsystemSky cloud/fog temporal smoothing patterns.
        if (currentScore >= 0.85f * bestScore)
            selected = current;
        return bestScore > 0f;
    }

    private static float GetDirectionalAttenuation(PointLight light, Vector3 target)
    {
        // Source: LedElectricElement.OnAdded and LightbulbBlock.GetFace; mounted electric
        // lights emit into their front hemisphere, while torches and lamps remain omnidirectional.
        if (!light.IsDirectional)
            return 1f;
        Vector3 toTarget = target - light.Position;
        float lengthSquared = toTarget.LengthSquared();
        if (lengthSquared <= 0.0001f)
            return 1f;
        float cosine = Vector3.Dot(
            toTarget / MathUtils.Sqrt(lengthSquared),
            light.Direction);
        float factor = MathUtils.Saturate(4f * cosine);
        return factor * factor * (3f - 2f * factor);
    }

    private void RemoveCandidate(object key)
    {
        for (int i = m_candidates.Count - 1; i >= 0; i--)
        {
            if (Equals(m_candidates[i].Key, key))
                m_candidates.RemoveAt(i);
        }
    }

    private void UpdatePerformanceBudget(float dt)
    {
        // Source: Time.FrameDuration; hysteresis keeps quality from oscillating.
        m_averageFrameDuration = MathUtils.Lerp(
            m_averageFrameDuration,
            Time.FrameDuration,
            MathUtils.Saturate(2f * dt));
        if (m_averageFrameDuration > 0.03f)
        {
            m_overBudgetTime += dt;
            m_recoveryTime = 0f;
            if (m_overBudgetTime >= 1f && m_qualityPenalty < 2)
            {
                m_qualityPenalty++;
                m_overBudgetTime = 0f;
            }
        }
        else if (m_averageFrameDuration < 0.022f)
        {
            m_recoveryTime += dt;
            m_overBudgetTime = 0f;
            if (m_recoveryTime >= 4f && m_qualityPenalty > 0)
            {
                m_qualityPenalty--;
                m_recoveryTime = 0f;
            }
        }
        else
        {
            m_overBudgetTime = 0f;
            m_recoveryTime = 0f;
        }
    }

    private static Vector3 SafeNormalize(Vector3 value)
    {
        return value.LengthSquared() > 0.0001f ? Vector3.Normalize(value) : Vector3.UnitZ;
    }

    private static Point3 GetBucket(Vector3 position)
    {
        return new Point3(
            (int)MathUtils.Floor(position.X / 16f),
            (int)MathUtils.Floor(position.Y / 16f),
            (int)MathUtils.Floor(position.Z / 16f));
    }

    private int GetNearbyLightRevision(Vector3 position, float radius)
    {
        // Source: TerrainUpdater.GenerateChunkLightSources 16x16 chunk locality
        Vector3 extent = new Vector3(radius, radius, radius);
        Point3 min = GetBucket(position - extent);
        Point3 max = GetBucket(position + extent);
        int revision = 0;
        for (int x = min.X; x <= max.X; x++)
        {
            for (int y = min.Y; y <= max.Y; y++)
            {
                for (int z = min.Z; z <= max.Z; z++)
                {
                    if (m_lightBucketRevisions.TryGetValue(
                        new Point3(x, y, z),
                        out int bucketRevision))
                    {
                        revision = MathUtils.Max(revision, bucketRevision);
                    }
                }
            }
        }
        return revision;
    }

    private void MarkLightChanged(Vector3 position)
    {
        int revision = ++m_nextLightRevision;
        m_lightBucketRevisions[GetBucket(position)] = revision;
    }

    private void MarkLightChanged(Vector3 first, Vector3 second)
    {
        int revision = ++m_nextLightRevision;
        m_lightBucketRevisions[GetBucket(first)] = revision;
        m_lightBucketRevisions[GetBucket(second)] = revision;
    }

    private static bool LightsEqual(PointLight first, PointLight second)
    {
        return Vector3.DistanceSquared(first.Position, second.Position) < 0.0001f &&
            Vector3.DistanceSquared(first.Direction, second.Direction) < 0.0001f &&
            MathUtils.Abs(first.Strength - second.Strength) < 0.001f &&
            MathUtils.Abs(first.Radius - second.Radius) < 0.001f &&
            first.IsDirectional == second.IsDirectional &&
            first.CastsTerrainShadow == second.CastsTerrainShadow;
    }

    private static void AddPointToBucket(
        Dictionary<Point2, List<Point3>> buckets,
        Point2 key,
        Point3 point)
    {
        if (!buckets.TryGetValue(key, out List<Point3> points))
        {
            points = new List<Point3>();
            buckets.Add(key, points);
        }
        points.Add(point);
    }

    private static void AddPointToBucket(
        Dictionary<Point3, List<Point3>> buckets,
        Point3 key,
        Point3 point)
    {
        if (!buckets.TryGetValue(key, out List<Point3> points))
        {
            points = new List<Point3>();
            buckets.Add(key, points);
        }
        points.Add(point);
    }

    private static void RemovePointFromBucket(
        Dictionary<Point2, List<Point3>> buckets,
        Point2 key,
        Point3 point)
    {
        if (!buckets.TryGetValue(key, out List<Point3> points))
            return;
        points.Remove(point);
        if (points.Count == 0)
            buckets.Remove(key);
    }

    private static void RemovePointFromBucket(
        Dictionary<Point3, List<Point3>> buckets,
        Point3 key,
        Point3 point)
    {
        if (!buckets.TryGetValue(key, out List<Point3> points))
            return;
        points.Remove(point);
        if (points.Count == 0)
            buckets.Remove(key);
    }

}
