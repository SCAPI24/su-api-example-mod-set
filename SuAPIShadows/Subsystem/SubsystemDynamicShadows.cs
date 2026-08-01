using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;
using SuAPI;
using TemplatesDatabase;

namespace SuAPIShadows;

public sealed class SubsystemDynamicShadows : SubsystemBlockBehavior, IDrawable, IUpdateable
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

    // Source: InstancedModelsManager.SourceModelVertex; mirror the original model vertex layout
    // so silhouette points can reuse the source data retained by the instanced model cache.
    [StructLayout(LayoutKind.Sequential)]
    private struct SourceModelVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TextureCoordinate;
    }

    private readonly struct ShadowVertex
    {
        public ShadowVertex(Vector3 position, int boneIndex)
        {
            Position = position;
            BoneIndex = boneIndex;
        }

        public Vector3 Position { get; }

        public int BoneIndex { get; }
    }

    private sealed class ModelShadowGeometry
    {
        public ShadowVertex[] Vertices;
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
        public Vector2[] Silhouette;
        public ReceiverPatch[] ReceiverPatches;
        public bool RequireSilhouette;
    }

    private sealed class ReceiverPatch
    {
        public Vector3 Center;
        public Vector3 Direction;
        public Vector3 Normal;
        public Vector2[] Polygon;
    }

    private sealed class ShadowCache
    {
        public readonly ShadowLayer[] Layers =
        {
            new ShadowLayer(),
            new ShadowLayer(),
            new ShadowLayer()
        };

        public double NextGeometryUpdate;
        public int LastLightRevision;
        public int LastDrawFrame;
    }

    private sealed class TerrainShadowState
    {
        public RenderTarget2D Target;
        public RenderTarget2D PointTarget;
        public Matrix ShadowMatrix;
        public Vector2 ShadowOrigin;
        public readonly object[] PointLightKeys = new object[MaxTerrainPointShadowLights];
        public readonly Vector2[] PointShadowOrigins = new Vector2[MaxTerrainPointShadowLights];
        public readonly Vector4[] PointLightPositionRadius = new Vector4[MaxTerrainPointShadowLights];
        public readonly Vector4[] PointLightDirectionStrength = new Vector4[MaxTerrainPointShadowLights];
        public Vector3 AnchorCenter;
        public object PointLightKey;
        public double NextUpdateTime;
        public double NextPointUpdateTime;
        public bool HasAnchorCenter;
        public bool IsValid;
        public bool IsPointValid;
        public int PointLightCount;
        public int Resolution;
        public int PointFaceResolution;
        public int LastDrawFrame;
    }

    private static readonly int[] s_drawOrders = { -1, 199 };
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

    private readonly HashSet<ComponentBody> m_shadowBodies = new HashSet<ComponentBody>();
    private readonly Dictionary<ComponentBody, ComponentModel> m_shadowModels =
        new Dictionary<ComponentBody, ComponentModel>();
    private readonly Dictionary<ComponentModel, bool> m_suppressedOriginalShadows =
        new Dictionary<ComponentModel, bool>();
    private readonly Dictionary<Model, ModelShadowGeometry> m_modelShadowGeometry =
        new Dictionary<Model, ModelShadowGeometry>();
    private readonly List<Vector2> m_silhouettePoints = new List<Vector2>(256);
    private readonly List<Vector2> m_silhouetteHull = new List<Vector2>(128);
    private readonly List<Vector2> m_clipSubject = new List<Vector2>(128);
    private readonly List<Vector2> m_clipScratch = new List<Vector2>(128);
    private Matrix[] m_worldBoneTransforms = Array.Empty<Matrix>();
    private readonly Dictionary<Camera, Dictionary<ComponentBody, ShadowCache>> m_caches =
        new Dictionary<Camera, Dictionary<ComponentBody, ShadowCache>>();
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
    private readonly List<TerrainChunk> m_localShadowChunks = new List<TerrainChunk>(32);
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
    private FlatBatch3D m_silhouetteBatch;
    private Shader m_originalOpaqueTerrainShader;
    private Shader m_originalAlphaTestedTerrainShader;
    private Shader m_shadowedOpaqueTerrainShader;
    private Shader m_shadowedAlphaTestedTerrainShader;
    private Shader m_shadowMapShader;
    private Shader m_pointShadowMapShader;
    private bool m_terrainShadowsAvailable;
    private bool m_terrainShadowFailureReported;
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
        // Source: Engine.Time.FrameDuration and SubsystemGlow.Draw
        UpdatePerformanceBudget(dt);
        if (Time.RealTime >= m_nextGlowRefresh)
        {
            RefreshGlowLights();
            m_nextGlowRefresh = Time.RealTime + 0.1;
        }
    }

    public void Draw(Camera camera, int drawOrder)
    {
        if (drawOrder == s_drawOrders[0])
        {
            PrepareTerrainShadowReceiver(camera);
            return;
        }
        if (!SettingsManager.ObjectsShadowsEnabled || camera == null)
            return;

        UpdateTerrainShadowMap(camera);
        UpdateTerrainPointShadowMap(camera);

        if (!m_caches.TryGetValue(camera, out Dictionary<ComponentBody, ShadowCache> caches))
        {
            caches = new Dictionary<ComponentBody, ShadowCache>();
            m_caches.Add(camera, caches);
        }

        double now = Time.RealTime;
        foreach (ComponentBody body in m_shadowBodies)
        {
            if (!body.IsAddedToProject)
                continue;
            if (!m_shadowModels.TryGetValue(body, out ComponentModel model))
            {
                continue;
            }
            bool isHumanModel = model is ComponentHumanModel;
            bool isFirstPersonTarget = IsFirstPersonTarget(camera, body);
            if (!model.IsVisibleForCamera && !isHumanModel)
                continue;
            if (!model.IsVisibleForCamera)
            {
                // Source: SubsystemModelsRenderer.PrepareModel; first-person humans bypass
                // normal rendering, so refresh their bones before building the shadow mesh.
                model.Animate();
                model.CalculateAbsoluteBonesTransforms(camera);
            }

            Vector3 center = 0.5f * (body.BoundingBox.Min + body.BoundingBox.Max);
            float distanceSquared = Vector3.DistanceSquared(camera.ViewPosition, center);
            // Source: ComponentModel.CalculateIsVisible. A first-person camera sits inside
            // its human body, so the body box is outside the forward frustum even though its
            // projected shadow is visible on the ground.
            if (distanceSquared > 16384f ||
                (!isFirstPersonTarget && !camera.ViewFrustum.Intersection(body.BoundingBox)))
                continue;

            if (!caches.TryGetValue(body, out ShadowCache cache))
            {
                cache = new ShadowCache();
                caches.Add(body, cache);
            }

            float distance = MathUtils.Sqrt(distanceSquared);
            int lod = GetLod(distance);
            int lightRevision = lod == 0
                ? cache.LastLightRevision
                : GetNearbyLightRevision(center, 15f);
            if ((isFirstPersonTarget && lod == 0) ||
                now >= cache.NextGeometryUpdate ||
                lightRevision != cache.LastLightRevision)
            {
                UpdateShadowTargets(body, camera, cache, distance, lod);
                cache.NextGeometryUpdate = now + GetUpdateInterval(lod);
                cache.LastLightRevision = GetNearbyLightRevision(center, 15f);
            }

            SmoothAndDraw(cache, camera, distance);
            cache.LastDrawFrame = Time.FrameIndex;
        }

        DrawPickableShadows(camera);

        RemoveStaleCaches(caches);
        RemoveStaleTerrainShadowStates(camera);
        m_renderer.Flush(camera.ViewProjectionMatrix);
    }

    public override void OnBlockGenerated(int value, int x, int y, int z, bool isLoaded)
    {
        // Source: TerrainUpdater.GenerateChunkLightSources
        UpdateBlockLight(value, x, y, z);
    }

    public override void OnBlockAdded(int value, int oldValue, int x, int y, int z)
    {
        UpdateBlockLight(value, x, y, z);
    }

    public override void OnBlockRemoved(int value, int newValue, int x, int y, int z)
    {
        RemoveBlockLight(new Point3(x, y, z));
    }

    public override void OnBlockModified(int value, int oldValue, int x, int y, int z)
    {
        UpdateBlockLight(value, x, y, z);
    }

    public override void OnChunkDiscarding(TerrainChunk chunk)
    {
        // Source: SubsystemBlockBehavior.OnChunkDiscarding
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
        m_silhouetteBatch = m_renderer.FlatBatch(
            0,
            DepthStencilState.DepthRead,
            RasterizerState.CullNoneScissor,
            BlendState.AlphaBlend);
        InitializeTerrainShadows();
    }

    public override void Dispose()
    {
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
            DisableTerrainShadows(ex);
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
            EnsureTerrainPointShadowTarget(state, 96);

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
            DisableTerrainShadows(ex);
        }
    }

    private void UpdateTerrainPointShadowMap(Camera camera)
    {
        if (!m_terrainShadowsAvailable ||
            !m_terrainShadowStates.TryGetValue(camera, out TerrainShadowState state) ||
            Time.RealTime < state.NextPointUpdateTime)
        {
            return;
        }

        state.LastDrawFrame = Time.FrameIndex;
        state.NextPointUpdateTime = Time.RealTime + GetTerrainPointShadowInterval();
        try
        {
            Vector3 sample = GetTerrainPointShadowSample(camera);
            CollectNearbyLights(sample, GetTerrainPointShadowSearchRadius());
            int lightCount = SelectTerrainShadowLights(
                camera,
                sample,
                state.PointLightKeys,
                m_selectedTerrainPointLights,
                GetTerrainPointShadowLightLimit());
            if (lightCount == 0)
            {
                state.IsPointValid = false;
                state.PointLightCount = 0;
                state.PointLightKey = null;
                return;
            }

            RenderTerrainPointShadowMap(state, m_selectedTerrainPointLights, lightCount);
            state.PointLightCount = lightCount;
            state.PointLightKey = m_selectedTerrainPointLights[0].Key;
            state.IsPointValid = true;
        }
        catch (Exception ex)
        {
            DisableTerrainShadows(ex);
        }
    }

    private void UpdateTerrainShadowMap(Camera camera)
    {
        if (!m_terrainShadowsAvailable ||
            !m_terrainShadowStates.TryGetValue(camera, out TerrainShadowState state))
            return;

        state.LastDrawFrame = Time.FrameIndex;
        if (!TryGetCelestialShadow(
            out Vector3 celestialDirection,
            out _))
        {
            state.IsValid = false;
            return;
        }
        if (Time.RealTime < state.NextUpdateTime)
            return;

        try
        {
            // Source: Survivalcraft/Game/TerrainRenderer.cs:
            // TerrainRenderer.DrawOpaque and DrawTerrainChunkGeometrySubsets
            float coverage = m_qualityPenalty == 0 ? 80f : 64f;
            Vector3 up = MathUtils.Abs(
                Vector3.Dot(celestialDirection, Vector3.UnitY)) > 0.92f
                ? Vector3.UnitZ
                : Vector3.UnitY;
            Vector3 center = GetStableShadowAnchor(state, camera.ViewPosition);
            Vector3 origin3 = new Vector3(
                MathUtils.Floor(center.X), 0f, MathUtils.Floor(center.Z));
            Vector3 localCenter = center - origin3;
            Matrix shadowMatrix = Matrix.CreateLookAt(
                localCenter + 300f * celestialDirection, localCenter, up) *
                Matrix.CreateOrthographic(coverage, coverage, 1f, 600f);

            Vector2 shadowOrigin = origin3.XZ;
            state.ShadowOrigin = shadowOrigin;
            state.ShadowMatrix = shadowMatrix;
            RenderTerrainShadowMap(
                state.Target, shadowOrigin, shadowMatrix, center, coverage);
            state.IsValid = true;
            state.NextUpdateTime = Time.RealTime + GetTerrainShadowInterval();
        }
        catch (Exception ex)
        {
            DisableTerrainShadows(ex);
        }
    }

    private double GetTerrainShadowInterval()
    {
        // Source: SubsystemSky cloud/fog temporal smoothing patterns. Under load, lower the
        // update rate instead of disabling the shadow map, otherwise dusk shadows visibly pop.
        return m_qualityPenalty switch
        {
            0 => 0.18,
            1 => 0.3,
            _ => 0.45
        };
    }

    private double GetTerrainPointShadowInterval()
    {
        // Source: Block.GetEmittedLightAmount; point-light shadow range stays physical while
        // the render cadence is the only part reduced by LOD pressure.
        return m_qualityPenalty switch
        {
            0 => 0.28,
            1 => 0.45,
            _ => 0.7
        };
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

    private int GetTerrainPointShadowLightLimit()
    {
        // Source: Time.FrameDuration performance budget. The atlas remains fixed-size, but
        // under load we fill fewer light slots instead of rebuilding render targets or
        // disabling local shadows abruptly.
        return m_qualityPenalty switch
        {
            0 => MaxTerrainPointShadowLights,
            1 => 6,
            _ => 3
        };
    }

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
                BlocksManager.Blocks[Terrain.ExtractContents(value)].ObjectShadowStrength > 0.25f);
        if (hit.HasValue)
            return hit.Value.HitPoint(0.02f);
        return camera.ViewPosition + 8f * camera.ViewDirection;
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
        float coverage)
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

    private void RenderTerrainPointShadowMap(
        TerrainShadowState state,
        PointLight[] lights,
        int lightCount)
    {
        RenderTarget2D previousTarget = Display.RenderTarget;
        Viewport previousViewport = Display.Viewport;
        Rectangle previousScissor = Display.ScissorRectangle;
        BlendState previousBlend = Display.BlendState;
        DepthStencilState previousDepth = Display.DepthStencilState;
        RasterizerState previousRasterizer = Display.RasterizerState;
        try
        {
            int faceSize = state.PointFaceResolution;
            Display.RenderTarget = state.PointTarget;
            Display.BlendState = BlendState.Opaque;
            Display.DepthStencilState = DepthStencilState.Default;
            Display.RasterizerState = RasterizerState.CullCounterClockwise;
            Display.Clear(Color.White, 1f, 0);

            m_pointShadowMapShader.GetParameter("u_texture").SetValue(
                m_terrain.SubsystemAnimatedTextures.AnimatedBlocksTexture);
            m_pointShadowMapShader.GetParameter("u_samplerState").SetValue(
                SamplerState.PointClamp);
            m_pointShadowMapShader.GetParameter("u_alphaThreshold").SetValue(0.5f);

            for (int lightIndex = 0; lightIndex < lightCount; lightIndex++)
            {
                PointLight light = lights[lightIndex];
                Vector3 origin3 = new Vector3(
                    MathUtils.Floor(light.Position.X), 0f, MathUtils.Floor(light.Position.Z));
                Vector3 localLightPosition = light.Position - origin3;
                Matrix projection = Matrix.CreatePerspectiveFieldOfView(
                    (float)Math.PI / 2f, 1f, 0.08f, light.Radius);
                state.PointLightKeys[lightIndex] = light.Key;
                state.PointShadowOrigins[lightIndex] = origin3.XZ;
                state.PointLightPositionRadius[lightIndex] = new Vector4(
                    localLightPosition.X,
                    localLightPosition.Y,
                    localLightPosition.Z,
                    light.Radius);
                state.PointLightDirectionStrength[lightIndex] = new Vector4(
                    light.Direction.X,
                    light.Direction.Y,
                    light.Direction.Z,
                    light.IsDirectional ? 0.55f * light.Strength : -0.55f * light.Strength);

                m_pointShadowMapShader.GetParameter("u_shadowOrigin").SetValue(origin3.XZ);
                m_pointShadowMapShader.GetParameter("u_lightPosition").SetValue(localLightPosition);
                m_pointShadowMapShader.GetParameter("u_lightRadius").SetValue(light.Radius);

                m_localShadowChunks.Clear();
                float rangeSquared = MathUtils.Sqr(light.Radius + 12f);
                foreach (TerrainChunk chunk in m_terrain.Terrain.AllocatedChunks)
                {
                    if (chunk.Geometry != null && chunk.Geometry.Buffers.Count > 0 &&
                        Vector2.DistanceSquared(chunk.Center, light.Position.XZ) <= rangeSquared)
                    {
                        m_localShadowChunks.Add(chunk);
                    }
                }

                Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;
                for (int face = 0; face < 6; face++)
                {
                    int column = face % 3;
                    int row = lightIndex * 2 + face / 3;
                    Display.Viewport = new Viewport(
                        column * faceSize, row * faceSize, faceSize, faceSize);
                    Display.ScissorRectangle = new Rectangle(
                        column * faceSize, row * faceSize, faceSize, faceSize);
                    Matrix matrix = Matrix.CreateLookAt(
                        localLightPosition,
                        localLightPosition + s_cubeDirections[face],
                        s_cubeUps[face]) * projection;
                    m_pointShadowMapShader.GetParameter("u_shadowMatrix").SetValue(matrix);
                    for (int i = 0; i < m_localShadowChunks.Count; i++)
                    {
                        DrawTerrainSubsets(
                            m_pointShadowMapShader, m_localShadowChunks[i].Geometry, 63);
                    }
                }
            }
            for (int i = lightCount; i < MaxTerrainPointShadowLights; i++)
                state.PointLightKeys[i] = null;
        }
        finally
        {
            m_localShadowChunks.Clear();
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

    private void EnsureTerrainPointShadowTarget(TerrainShadowState state, int faceResolution)
    {
        if (state.PointTarget != null && state.PointFaceResolution == faceResolution)
            return;

        RenderTarget2D target = null;
        RenderTarget2D displayTarget = Display.RenderTarget;
        Viewport previousViewport = Display.Viewport;
        Rectangle previousScissor = Display.ScissorRectangle;
        try
        {
            target = new RenderTarget2D(
                3 * faceResolution,
                2 * MaxTerrainPointShadowLights * faceResolution,
                1,
                ColorFormat.Rgba8888,
                DepthFormat.Depth16);
            try
            {
                // Source: Engine/Graphics/RenderTarget2D.cs:RenderTarget2D
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
        state.PointTarget?.Dispose();
        state.PointTarget = target;
        state.PointFaceResolution = faceResolution;
        state.IsPointValid = false;
    }

    private float GetCelestialLightFloor()
    {
        // Source: Survivalcraft/Game/SubsystemSky.UpdateLightAndViewParameters and
        // Survivalcraft/Game/LightingManager.CalculateLightingTables.
        int light = MathUtils.Clamp(m_sky.SkyLightValue, 0, 15);
        return LightingManager.LightIntensityByLightValue[light];
    }

    private static void SetTerrainShadowParameters(
        Shader shader,
        TerrainShadowState state,
        bool enabled,
        float strength,
        float celestialLightFloor,
        float fogIntensity)
    {
        shader.GetParameter("u_shadowEnabled").SetValue(enabled ? 1f : 0f);
        shader.GetParameter("u_shadowStrength").SetValue(
            MathUtils.Clamp(strength, 0f, 0.55f));
        shader.GetParameter("u_celestialLightFloor").SetValue(
            MathUtils.Saturate(celestialLightFloor));
        if (state.Target == null)
            return;
        shader.GetParameter("u_shadowOrigin").SetValue(state.ShadowOrigin);
        shader.GetParameter("u_shadowMatrix").SetValue(state.ShadowMatrix);
        shader.GetParameter("u_shadowTexture").SetValue(state.Target);
        shader.GetParameter("u_shadowSamplerState").SetValue(SamplerState.LinearClamp);
        shader.GetParameter("u_shadowTexelSize").SetValue(
            new Vector2(1f / state.Resolution));
        shader.GetParameter("u_fogShadowFactor").SetValue(
            MathUtils.Saturate(fogIntensity));
        bool pointEnabled = SettingsManager.ObjectsShadowsEnabled &&
            state.IsPointValid && state.PointTarget != null && state.PointLightCount > 0;
        shader.GetParameter("u_pointShadowEnabled").SetValue(pointEnabled ? 1f : 0f);
        shader.GetParameter("u_pointShadowCount").SetValue(
            pointEnabled ? (float)state.PointLightCount : 0f);
        shader.GetParameter("u_pointShadowOrigins").SetValue(
            state.PointShadowOrigins,
            state.PointLightCount);
        shader.GetParameter("u_pointShadowLightPositionRadius").SetValue(
            state.PointLightPositionRadius,
            state.PointLightCount);
        shader.GetParameter("u_pointShadowLightDirectionStrength").SetValue(
            state.PointLightDirectionStrength,
            state.PointLightCount);
        shader.GetParameter("u_pointShadowTexture").SetValue(state.PointTarget);
        shader.GetParameter("u_pointShadowSamplerState").SetValue(SamplerState.LinearClamp);
        shader.GetParameter("u_pointShadowTexelSize").SetValue(new Vector2(
            1f / (3f * state.PointFaceResolution),
            1f / (2f * MaxTerrainPointShadowLights * state.PointFaceResolution)));
    }

    private void DisableTerrainShadows(Exception exception)
    {
        m_terrainShadowsAvailable = false;
        RestoreOriginalTerrainShaders();
        DisposeTerrainShadowStates();
        if (!m_terrainShadowFailureReported)
        {
            m_terrainShadowFailureReported = true;
            Log.Warning("[SuAPI] Terrain shadows disabled: {0}", exception.Message);
        }
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
            state.PointTarget?.Dispose();
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
            m_terrainShadowStates[stale[i]].PointTarget?.Dispose();
            m_terrainShadowStates.Remove(stale[i]);
        }
    }

    protected override void OnEntityAdded(Entity entity)
    {
        // Source: SubsystemModelsRenderer.OnEntityAdded and DrawModelsExtras
        ComponentBody body = entity.FindComponent<ComponentBody>();
        if (body == null)
            return;

        ComponentModel shadowModel = entity.FindComponent<ComponentCreatureModel>();
        ComponentModel fallbackModel = null;
        foreach (ComponentModel model in entity.FindComponents<ComponentModel>())
        {
            if (!model.CastsShadow)
                continue;

            fallbackModel ??= model;
            // Source: SubsystemModelsRenderer.DrawModelsExtras. A human may also have an
            // outer-clothing model, so suppress every original foot-circle caster together.
            m_suppressedOriginalShadows[model] = true;
            model.CastsShadow = false;
        }

        shadowModel ??= fallbackModel;
        if (shadowModel == null)
            return;
        m_shadowBodies.Add(body);
        m_shadowModels[body] = shadowModel;
    }

    protected override void OnEntityRemoved(Entity entity)
    {
        ComponentBody body = entity.FindComponent<ComponentBody>();
        if (body == null || !m_shadowBodies.Remove(body))
            return;

        if (m_shadowModels.TryGetValue(body, out ComponentModel model))
        {
            RestoreOriginalModelShadow(model);
            m_shadowModels.Remove(body);
        }

        foreach (Dictionary<ComponentBody, ShadowCache> caches in m_caches.Values)
            caches.Remove(body);
    }

    private void RestoreOriginalModelShadows()
    {
        foreach (KeyValuePair<ComponentModel, bool> pair in m_suppressedOriginalShadows)
            pair.Key.CastsShadow = pair.Value;
        m_suppressedOriginalShadows.Clear();
        m_shadowModels.Clear();
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
            LightbulbBlock.Index
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

    private void UpdateShadowTargets(
        ComponentBody body,
        Camera camera,
        ShadowCache cache,
        float distance,
        int lod)
    {
        BoundingBox box = body.BoundingBox;
        float height = box.Max.Y - box.Min.Y;
        float width = MathUtils.Max(box.Max.X - box.Min.X, box.Max.Z - box.Min.Z);
        Vector3 sample = new Vector3(
            0.5f * (box.Min.X + box.Max.X),
            box.Min.Y + 0.35f * height,
            0.5f * (box.Min.Z + box.Max.Z));
        // Source: SubsystemShadows.QueueShadow distance and fog fading
        float distanceFade = MathUtils.Saturate((128f - distance) / 24f) *
            (1f - m_sky.CalculateFog(camera.ViewPosition, sample));

        CollectNearbyLights(sample, ElectricLightRadius);
        float localLightFactor = GetVisibleLocalLightFactor(sample);
        bool hasCelestialLight = TryGetCelestialShadow(
            out Vector3 celestialDirection,
            out float celestialStrength);
        float celestialAlpha = celestialStrength * (0.5f / 0.52f) * distanceFade *
            (1f - MathUtils.Saturate(localLightFactor));
        if (hasCelestialLight && celestialAlpha > 0.02f &&
            IsCelestialVisible(sample, celestialDirection, lod))
        {
            if (SetProjectedShadow(
                cache.Layers[0],
                s_celestialKey,
                sample,
                -celestialDirection,
                14f,
                width,
                height,
                celestialAlpha))
            {
                UpdateModelSilhouette(body, camera, cache.Layers[0], lod);
            }
        }
        else
        {
            cache.Layers[0].TargetAlpha = 0f;
        }

        int pointLightCount = GetModelPointLightShadowCount(distance);
        Vector3 footSample = new Vector3(
            sample.X,
            box.Min.Y + 0.08f,
            sample.Z);
        Vector3 headSample = new Vector3(
            sample.X,
            box.Max.Y - 0.15f,
            sample.Z);
        for (int i = 0; i < 2; i++)
        {
            ShadowLayer layer = cache.Layers[i + 1];
            if (i >= pointLightCount ||
                !TrySelectVisibleModelLight(
                    sample, footSample, headSample, layer.LightKey, out PointLight light))
            {
                layer.TargetAlpha = 0f;
                continue;
            }
            RemoveCandidate(light.Key);

            Vector3 fromLight = sample - light.Position;
            float lightDistance = fromLight.Length();
            float attenuation = MathUtils.Sqr(1f - lightDistance / light.Radius);
            float alpha = 0.34f * light.Strength * attenuation * distanceFade;
            Vector3 rayDirection = fromLight / lightDistance;
            if (rayDirection.Y > -0.2f)
            {
                rayDirection = SafeNormalize(new Vector3(
                    rayDirection.X,
                    -0.35f,
                    rayDirection.Z));
            }
            if (SetProjectedShadow(
                layer,
                light.Key,
                footSample,
                rayDirection,
                MathUtils.Min(light.Radius, 10f),
                width,
                height,
                alpha,
                preferFootReceiver: true))
            {
                UpdateModelSilhouette(body, camera, layer, lod);
            }
        }
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
        m_pickableShadowLayer.Silhouette = null;
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
            layer.ReceiverPatches = null;
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
            layer.RequireSilhouette = false;
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

    private void UpdateModelSilhouette(
        ComponentBody body,
        Camera camera,
        ShadowLayer layer,
        int lod)
    {
        layer.RequireSilhouette = true;
        layer.Silhouette = null;
        layer.ReceiverPatches = null;
        ComponentModel model = null;
        if (!m_shadowModels.TryGetValue(body, out model))
            return;
        if (lod > 2 ||
            model.Model == null || model.AbsoluteBoneTransformsForCamera == null)
            return;

        ModelShadowGeometry geometry = GetModelShadowGeometry(model.Model);
        if (geometry.Vertices.Length < 3)
            return;

        Vector3 side = Vector3.Cross(layer.TargetNormal, layer.TargetDirection);
        float denominator = Vector3.Dot(layer.TargetRayDirection, layer.TargetNormal);
        if (side.LengthSquared() < 0.0001f || MathUtils.Abs(denominator) < 0.02f)
            return;
        side = Vector3.Normalize(side);

        Matrix[] absoluteBones = model.AbsoluteBoneTransformsForCamera;
        if (m_worldBoneTransforms.Length < absoluteBones.Length)
            Array.Resize(ref m_worldBoneTransforms, absoluteBones.Length);
        Matrix inverseView = Matrix.Invert(camera.ViewMatrix);
        for (int i = 0; i < absoluteBones.Length; i++)
        {
            Matrix.MultiplyRestricted(
                ref absoluteBones[i], ref inverseView, out m_worldBoneTransforms[i]);
        }
        bool hasFirstPersonCorrection = TryGetFirstPersonCorrection(
            body, camera, model, out Matrix firstPersonCorrection);

        int maximumPoints = lod == 0 ? 256 : (lod == 1 ? 96 : 48);
        int stride = MathUtils.Max(1, geometry.Vertices.Length / maximumPoints);
        m_silhouettePoints.Clear();
        for (int i = 0; i < geometry.Vertices.Length; i += stride)
        {
            ShadowVertex vertex = geometry.Vertices[i];
            if (vertex.BoneIndex < 0 || vertex.BoneIndex >= absoluteBones.Length)
                continue;

            Vector3 worldPosition = Vector3.Transform(
                vertex.Position, m_worldBoneTransforms[vertex.BoneIndex]);
            if (hasFirstPersonCorrection)
                worldPosition = Vector3.Transform(worldPosition, firstPersonCorrection);
            float rayDistance = Vector3.Dot(
                layer.TargetCenter - worldPosition, layer.TargetNormal) / denominator;
            if (rayDistance < -0.1f || rayDistance > 96f)
                continue;

            Vector3 projected = worldPosition + rayDistance * layer.TargetRayDirection;
            Vector3 offset = projected - layer.TargetCenter;
            m_silhouettePoints.Add(new Vector2(
                Vector3.Dot(offset, layer.TargetDirection),
                Vector3.Dot(offset, side)));
        }

        layer.Silhouette = CreateSilhouetteHull();
        if (layer.Silhouette != null)
        {
            layer.ReceiverPatches = CreateReceiverPatches(
                layer, geometry, absoluteBones, stride,
                hasFirstPersonCorrection, firstPersonCorrection);
        }
    }

    private static bool IsFirstPersonTarget(Camera camera, ComponentBody body)
    {
        // Source: GameWidget.IsEntityFirstPersonTarget
        return camera?.GameWidget != null &&
            body != null &&
            camera.GameWidget.IsEntityFirstPersonTarget(body.Entity);
    }

    private static bool TryGetFirstPersonCorrection(
        ComponentBody body,
        Camera camera,
        ComponentModel model,
        out Matrix correction)
    {
        correction = Matrix.Identity;
        if (!IsFirstPersonTarget(camera, body) ||
            model is not ComponentCreatureModel creatureModel)
        {
            return false;
        }

        Vector3 bodyForward = body.Matrix.Forward * new Vector3(1f, 0f, 1f);
        Vector3 eyeForward = Matrix.CreateFromQuaternion(creatureModel.EyeRotation).Forward *
            new Vector3(1f, 0f, 1f);
        if (bodyForward.LengthSquared() < 0.0001f ||
            eyeForward.LengthSquared() < 0.0001f)
        {
            return false;
        }

        bodyForward = Vector3.Normalize(bodyForward);
        eyeForward = Vector3.Normalize(eyeForward);
        float bodyYaw = MathUtils.Atan2(bodyForward.X, bodyForward.Z);
        float eyeYaw = MathUtils.Atan2(eyeForward.X, eyeForward.Z);
        float yawDelta = MathUtils.NormalizeAngle(eyeYaw - bodyYaw);
        if (MathUtils.Abs(yawDelta) < MathUtils.DegToRad(0.25f))
            return false;

        Vector3 origin = body.Position;
        correction = Matrix.CreateTranslation(-origin) *
            Matrix.CreateRotationY(yawDelta) *
            Matrix.CreateTranslation(origin);
        return true;
    }

    private ModelShadowGeometry GetModelShadowGeometry(Model model)
    {
        if (m_modelShadowGeometry.TryGetValue(model, out ModelShadowGeometry geometry))
            return geometry;

        List<ShadowVertex> vertices = new List<ShadowVertex>();
        try
        {
            foreach (ModelMesh mesh in model.Meshes)
            {
                foreach (ModelMeshPart meshPart in mesh.MeshParts)
                {
                    SourceModelVertex[] sourceVertices =
                        BlockMesh.GetVertexData<SourceModelVertex>(meshPart.VertexBuffer);
                    ushort[] indices = BlockMesh.GetIndexData<ushort>(meshPart.IndexBuffer);
                    HashSet<ushort> usedIndices = new HashSet<ushort>();
                    int end = MathUtils.Min(
                        meshPart.StartIndex + meshPart.IndicesCount, indices.Length);
                    for (int i = meshPart.StartIndex; i < end; i++)
                    {
                        ushort index = indices[i];
                        if (index < sourceVertices.Length && usedIndices.Add(index))
                        {
                            vertices.Add(new ShadowVertex(
                                sourceVertices[index].Position,
                                mesh.ParentBone.Index));
                        }
                    }
                }
            }
        }
        catch
        {
            vertices.Clear();
        }

        geometry = new ModelShadowGeometry
        {
            Vertices = vertices.ToArray()
        };
        m_modelShadowGeometry.Add(model, geometry);
        return geometry;
    }

    private Vector2[] CreateSilhouetteHull()
    {
        if (m_silhouettePoints.Count < 3)
            return null;

        m_silhouettePoints.Sort(CompareSilhouettePoints);
        m_silhouetteHull.Clear();
        for (int i = 0; i < m_silhouettePoints.Count; i++)
        {
            Vector2 point = m_silhouettePoints[i];
            while (m_silhouetteHull.Count >= 2 && Cross(
                m_silhouetteHull[m_silhouetteHull.Count - 2],
                m_silhouetteHull[m_silhouetteHull.Count - 1],
                point) <= 0.0001f)
            {
                m_silhouetteHull.RemoveAt(m_silhouetteHull.Count - 1);
            }
            m_silhouetteHull.Add(point);
        }

        int lowerCount = m_silhouetteHull.Count;
        for (int i = m_silhouettePoints.Count - 2; i >= 0; i--)
        {
            Vector2 point = m_silhouettePoints[i];
            while (m_silhouetteHull.Count > lowerCount && Cross(
                m_silhouetteHull[m_silhouetteHull.Count - 2],
                m_silhouetteHull[m_silhouetteHull.Count - 1],
                point) <= 0.0001f)
            {
                m_silhouetteHull.RemoveAt(m_silhouetteHull.Count - 1);
            }
            m_silhouetteHull.Add(point);
        }

        if (m_silhouetteHull.Count <= 3)
            return null;
        m_silhouetteHull.RemoveAt(m_silhouetteHull.Count - 1);
        return m_silhouetteHull.ToArray();
    }

    private ReceiverPatch[] CreateReceiverPatches(
        ShadowLayer layer,
        ModelShadowGeometry geometry,
        Matrix[] absoluteBones,
        int stride,
        bool hasFirstPersonCorrection,
        Matrix firstPersonCorrection)
    {
        Vector2[] silhouette = layer.Silhouette;
        if (silhouette == null || silhouette.Length < 3)
            return null;

        Vector3 side = Vector3.Cross(layer.TargetNormal, layer.TargetDirection);
        if (side.LengthSquared() < 0.0001f)
            return null;
        side = Vector3.Normalize(side);

        float minX = silhouette[0].X;
        float maxX = silhouette[0].X;
        float minY = silhouette[0].Y;
        float maxY = silhouette[0].Y;
        for (int i = 1; i < silhouette.Length; i++)
        {
            Vector2 point = silhouette[i];
            minX = MathUtils.Min(minX, point.X);
            maxX = MathUtils.Max(maxX, point.X);
            minY = MathUtils.Min(minY, point.Y);
            maxY = MathUtils.Max(maxY, point.Y);
        }

        Vector3 corner1 = layer.TargetCenter + minX * layer.TargetDirection + minY * side;
        Vector3 corner2 = layer.TargetCenter + minX * layer.TargetDirection + maxY * side;
        Vector3 corner3 = layer.TargetCenter + maxX * layer.TargetDirection + minY * side;
        Vector3 corner4 = layer.TargetCenter + maxX * layer.TargetDirection + maxY * side;
        int minXCell = Terrain.ToCell(MathUtils.Min(corner1.X, corner2.X, corner3.X, corner4.X) - 1f);
        int maxXCell = Terrain.ToCell(MathUtils.Max(corner1.X, corner2.X, corner3.X, corner4.X) + 1f);
        int minYCell = Terrain.ToCell(MathUtils.Min(corner1.Y, corner2.Y, corner3.Y, corner4.Y) - 1f);
        int maxYCell = Terrain.ToCell(MathUtils.Max(corner1.Y, corner2.Y, corner3.Y, corner4.Y) + 4f);
        int minZCell = Terrain.ToCell(MathUtils.Min(corner1.Z, corner2.Z, corner3.Z, corner4.Z) - 1f);
        int maxZCell = Terrain.ToCell(MathUtils.Max(corner1.Z, corner2.Z, corner3.Z, corner4.Z) + 1f);

        List<ReceiverPatch> patches = null;
        for (int x = minXCell; x <= maxXCell; x++)
        {
            for (int y = minYCell; y <= maxYCell; y++)
            {
                for (int z = minZCell; z <= maxZCell; z++)
                {
                    int value = m_terrain.Terrain.GetCellValue(x, y, z);
                    Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
                    if (block.ObjectShadowStrength <= 0f)
                        continue;

                    BoundingBox[] boxes = block.GetCustomCollisionBoxes(m_terrain, value);
                    if (boxes == null || boxes.Length == 0)
                        continue;

                    for (int i = 0; i < boxes.Length; i++)
                    {
                        for (int face = 0; face <= 4; face++)
                        {
                            if (!TryGetReceiverFaceQuad(
                                boxes[i], x, y, z, face,
                                out Vector3 p1, out Vector3 p2,
                                out Vector3 p3, out Vector3 p4,
                                out Vector3 normal))
                            {
                                continue;
                            }
                            if (Vector3.Dot(layer.TargetRayDirection, normal) >= -0.02f)
                                continue;
                            if (!TryCreateReceiverPatch(
                                layer,
                                geometry,
                                absoluteBones,
                                stride,
                                hasFirstPersonCorrection,
                                firstPersonCorrection,
                                p1, p2, p3, p4,
                                normal,
                                out ReceiverPatch patch))
                            {
                                continue;
                            }

                            patches ??= new List<ReceiverPatch>(8);
                            patches.Add(patch);
                        }
                    }
                }
            }
        }

        if (patches == null || patches.Count == 0)
            return null;
        return patches.ToArray();
    }

    private static bool TryGetReceiverFaceQuad(
        BoundingBox box,
        int x,
        int y,
        int z,
        int face,
        out Vector3 p1,
        out Vector3 p2,
        out Vector3 p3,
        out Vector3 p4,
        out Vector3 normal)
    {
        normal = CellFace.FaceToVector3(face);
        switch (face)
        {
        case 0:
        {
            float faceZ = box.Max.Z + z;
            p1 = new Vector3(box.Min.X + x, box.Min.Y + y, faceZ);
            p2 = new Vector3(box.Min.X + x, box.Max.Y + y, faceZ);
            p3 = new Vector3(box.Max.X + x, box.Max.Y + y, faceZ);
            p4 = new Vector3(box.Max.X + x, box.Min.Y + y, faceZ);
            return true;
        }
        case 1:
        {
            float faceX = box.Max.X + x;
            p1 = new Vector3(faceX, box.Min.Y + y, box.Min.Z + z);
            p2 = new Vector3(faceX, box.Min.Y + y, box.Max.Z + z);
            p3 = new Vector3(faceX, box.Max.Y + y, box.Max.Z + z);
            p4 = new Vector3(faceX, box.Max.Y + y, box.Min.Z + z);
            return true;
        }
        case 2:
        {
            float faceZ = box.Min.Z + z;
            p1 = new Vector3(box.Max.X + x, box.Min.Y + y, faceZ);
            p2 = new Vector3(box.Max.X + x, box.Max.Y + y, faceZ);
            p3 = new Vector3(box.Min.X + x, box.Max.Y + y, faceZ);
            p4 = new Vector3(box.Min.X + x, box.Min.Y + y, faceZ);
            return true;
        }
        case 3:
        {
            float faceX = box.Min.X + x;
            p1 = new Vector3(faceX, box.Min.Y + y, box.Max.Z + z);
            p2 = new Vector3(faceX, box.Min.Y + y, box.Min.Z + z);
            p3 = new Vector3(faceX, box.Max.Y + y, box.Min.Z + z);
            p4 = new Vector3(faceX, box.Max.Y + y, box.Max.Z + z);
            return true;
        }
        case 4:
        {
            float faceY = box.Max.Y + y;
            p1 = new Vector3(box.Min.X + x, faceY, box.Min.Z + z);
            p2 = new Vector3(box.Max.X + x, faceY, box.Min.Z + z);
            p3 = new Vector3(box.Max.X + x, faceY, box.Max.Z + z);
            p4 = new Vector3(box.Min.X + x, faceY, box.Max.Z + z);
            return true;
        }
        default:
            p1 = default(Vector3);
            p2 = default(Vector3);
            p3 = default(Vector3);
            p4 = default(Vector3);
            normal = default(Vector3);
            return false;
        }
    }

    private bool TryCreateReceiverPatch(
        ShadowLayer layer,
        ModelShadowGeometry geometry,
        Matrix[] absoluteBones,
        int stride,
        bool hasFirstPersonCorrection,
        Matrix firstPersonCorrection,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        Vector3 p4,
        Vector3 normal,
        out ReceiverPatch patch)
    {
        patch = null;
        Vector3 direction = layer.TargetRayDirection -
            normal * Vector3.Dot(layer.TargetRayDirection, normal);
        if (direction.LengthSquared() < 0.0001f)
            direction = p2 - p1;
        if (direction.LengthSquared() < 0.0001f)
            return false;
        direction = Vector3.Normalize(direction);
        Vector3 side = Vector3.Cross(normal, direction);
        if (side.LengthSquared() < 0.0001f)
            return false;
        side = Vector3.Normalize(side);
        Vector3 center = 0.25f * (p1 + p2 + p3 + p4);
        float denominator = Vector3.Dot(layer.TargetRayDirection, normal);
        if (denominator >= -0.02f)
            return false;

        m_silhouettePoints.Clear();
        for (int i = 0; i < geometry.Vertices.Length; i += stride)
        {
            ShadowVertex vertex = geometry.Vertices[i];
            if (vertex.BoneIndex < 0 || vertex.BoneIndex >= absoluteBones.Length)
                continue;

            Vector3 worldPosition = Vector3.Transform(
                vertex.Position, m_worldBoneTransforms[vertex.BoneIndex]);
            if (hasFirstPersonCorrection)
                worldPosition = Vector3.Transform(worldPosition, firstPersonCorrection);
            float rayDistance = Vector3.Dot(p1 - worldPosition, normal) / denominator;
            if (rayDistance < -0.1f || rayDistance > 96f)
                continue;

            Vector3 projected = worldPosition + rayDistance * layer.TargetRayDirection;
            Vector3 offset = projected - center;
            m_silhouettePoints.Add(new Vector2(
                Vector3.Dot(offset, direction),
                Vector3.Dot(offset, side)));
        }

        Vector2[] hull = CreateSilhouetteHull();
        if (hull == null || hull.Length < 3)
            return false;

        Vector2 q1 = new Vector2(
            Vector3.Dot(p1 - center, direction),
            Vector3.Dot(p1 - center, side));
        Vector2 q2 = new Vector2(
            Vector3.Dot(p2 - center, direction),
            Vector3.Dot(p2 - center, side));
        Vector2 q3 = new Vector2(
            Vector3.Dot(p3 - center, direction),
            Vector3.Dot(p3 - center, side));
        Vector2 q4 = new Vector2(
            Vector3.Dot(p4 - center, direction),
            Vector3.Dot(p4 - center, side));

        m_clipSubject.Clear();
        m_clipSubject.AddRange(hull);
        if (!ClipPolygonToConvexQuad(m_clipSubject, q1, q2, q3, q4) ||
            m_clipSubject.Count < 3)
        {
            return false;
        }

        patch = new ReceiverPatch
        {
            Center = center,
            Direction = direction,
            Normal = normal,
            Polygon = m_clipSubject.ToArray()
        };
        return true;
    }

    private bool ClipPolygonToConvexQuad(
        List<Vector2> polygon,
        Vector2 q1,
        Vector2 q2,
        Vector2 q3,
        Vector2 q4)
    {
        Vector2[] clip = { q1, q2, q3, q4 };
        float area = Cross(clip[0], clip[1], clip[2]) + Cross(clip[0], clip[2], clip[3]);
        if (area < 0f)
            Array.Reverse(clip);

        m_clipScratch.Clear();
        m_clipScratch.AddRange(polygon);
        List<Vector2> input = m_clipScratch;
        List<Vector2> output = polygon;
        for (int edge = 0; edge < 4; edge++)
        {
            output.Clear();
            Vector2 a = clip[edge];
            Vector2 b = clip[(edge + 1) & 3];
            if (input.Count == 0)
                return false;

            Vector2 previous = input[input.Count - 1];
            bool previousInside = Cross(a, b, previous) >= -0.0001f;
            for (int i = 0; i < input.Count; i++)
            {
                Vector2 current = input[i];
                bool currentInside = Cross(a, b, current) >= -0.0001f;
                if (currentInside != previousInside &&
                    TryIntersectSegments(previous, current, a, b, out Vector2 intersection))
                {
                    output.Add(intersection);
                }
                if (currentInside)
                    output.Add(current);
                previous = current;
                previousInside = currentInside;
            }

            if (output.Count == 0)
                return false;

            List<Vector2> swap = input;
            input = output;
            output = swap;
        }

        if (!ReferenceEquals(input, polygon))
        {
            polygon.Clear();
            polygon.AddRange(input);
        }
        return polygon.Count >= 3;
    }

    private static bool TryIntersectSegments(
        Vector2 p1,
        Vector2 p2,
        Vector2 q1,
        Vector2 q2,
        out Vector2 intersection)
    {
        Vector2 r = p2 - p1;
        Vector2 s = q2 - q1;
        float denominator = r.X * s.Y - r.Y * s.X;
        if (MathUtils.Abs(denominator) < 0.000001f)
        {
            intersection = p1;
            return false;
        }

        Vector2 qp = q1 - p1;
        float t = (qp.X * s.Y - qp.Y * s.X) / denominator;
        intersection = p1 + t * r;
        return true;
    }

    private float GetVisibleLocalLightFactor(Vector3 sample)
    {
        float best = 0f;
        for (int i = 0; i < m_candidates.Count; i++)
        {
            PointLight light = m_candidates[i];
            float distance = Vector3.Distance(sample, light.Position);
            if (distance < 0.2f || distance >= light.Radius)
                continue;
            if (IsLightOccluded(light.Position, sample))
                continue;
            float attenuation = MathUtils.Saturate(1f - distance / light.Radius);
            float score = light.Strength * attenuation * GetDirectionalAttenuation(light, sample);
            if (score > best)
                best = score;
        }
        return best;
    }

    private static int CompareSilhouettePoints(Vector2 first, Vector2 second)
    {
        int x = first.X.CompareTo(second.X);
        return x != 0 ? x : first.Y.CompareTo(second.Y);
    }

    private static float Cross(Vector2 first, Vector2 second, Vector2 third)
    {
        return (second.X - first.X) * (third.Y - first.Y) -
            (second.Y - first.Y) * (third.X - first.X);
    }

    private void SmoothAndDraw(ShadowCache cache, Camera camera, float distance)
    {
        float factor = MathUtils.Saturate(12f * Time.FrameDuration);
        for (int i = 0; i < cache.Layers.Length; i++)
        {
            ShadowLayer layer = cache.Layers[i];
            layer.Center = Vector3.Lerp(layer.Center, layer.TargetCenter, factor);
            layer.Direction = SafeNormalize(Vector3.Lerp(layer.Direction, layer.TargetDirection, factor));
            layer.Normal = SafeNormalize(Vector3.Lerp(layer.Normal, layer.TargetNormal, factor));
            layer.RayDirection = SafeNormalize(Vector3.Lerp(
                layer.RayDirection, layer.TargetRayDirection, factor));
            layer.Length = MathUtils.Lerp(layer.Length, layer.TargetLength, factor);
            layer.Width = MathUtils.Lerp(layer.Width, layer.TargetWidth, factor);
            layer.Alpha = MathUtils.Lerp(layer.Alpha, layer.TargetAlpha, factor);
            if (layer.Alpha <= 0.015f || layer.Width <= 0.01f)
                continue;

            float pixels = layer.Width * camera.ViewportSize.Y *
                MathUtils.Abs(camera.ProjectionMatrix.M22) / MathUtils.Max(2f * distance, 0.1f);
            if (pixels < 1.5f)
                continue;

            QueueLayer(layer);
        }
    }

    private void QueueLayer(ShadowLayer layer)
    {
        // Source: SubsystemShadows.DrawShadowOverQuad
        if (layer.Silhouette != null && layer.Silhouette.Length >= 3)
        {
            if (layer.ReceiverPatches != null && layer.ReceiverPatches.Length > 0)
            {
                for (int i = 0; i < layer.ReceiverPatches.Length; i++)
                    QueueSilhouette(layer, layer.ReceiverPatches[i]);
            }
            return;
        }
        if (layer.RequireSilhouette)
            return;

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

    private void QueueSilhouette(ShadowLayer layer, ReceiverPatch patch)
    {
        // Source: SubsystemShadows.DrawShadowOverQuad; a convex hull preserves the animated
        // model's projected outline while using the same depth-read alpha blending behavior.
        Vector2[] silhouette = patch.Polygon;
        if (silhouette == null || silhouette.Length < 3)
            return;

        Vector3 side = Vector3.Cross(patch.Normal, patch.Direction);
        if (side.LengthSquared() < 0.0001f)
            return;
        side = Vector3.Normalize(side);
        Vector3 origin = patch.Center + 0.01f * patch.Normal;
        Vector3 first = origin + silhouette[0].X * patch.Direction +
            silhouette[0].Y * side;
        Color color = new Color(0f, 0f, 0f,
            MathUtils.Saturate(0.72f * layer.Alpha));
        for (int i = 1; i < silhouette.Length - 1; i++)
        {
            Vector3 second = origin + silhouette[i].X * patch.Direction +
                silhouette[i].Y * side;
            Vector3 third = origin + silhouette[i + 1].X * patch.Direction +
                silhouette[i + 1].Y * side;
            m_silhouetteBatch.QueueTriangle(first, second, third, color);
        }
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

    private bool TrySelectVisibleModelLight(
        Vector3 sample,
        Vector3 footSample,
        Vector3 headSample,
        object currentKey,
        out PointLight selected)
    {
        // Source: SubsystemModelsRenderer.DrawModelsExtras and SubsystemShadows.QueueShadow.
        // A low wall can hide the body-center sample while the head or feet still receive
        // light, so character shadows test a small vertical span instead of one point.
        selected = default(PointLight);
        float bestScore = 0f;
        float currentScore = 0f;
        PointLight current = default(PointLight);
        for (int i = 0; i < m_candidates.Count; i++)
        {
            PointLight light = m_candidates[i];
            float distance = Vector3.Distance(sample, light.Position);
            if (distance < 0.2f || distance >= light.Radius)
                continue;
            if (IsLightOccluded(light.Position, sample) &&
                IsLightOccluded(light.Position, footSample) &&
                IsLightOccluded(light.Position, headSample))
            {
                continue;
            }

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

        if (currentScore >= 0.85f * bestScore)
            selected = current;
        return bestScore > 0f;
    }

    private bool TrySelectTerrainShadowLight(
        Vector3 sample,
        object currentKey,
        out PointLight selected)
    {
        // Source: Block.GetEmittedLightAmount and SubsystemGlow.Draw
        selected = default(PointLight);
        PointLight current = default(PointLight);
        float bestScore = 0f;
        float currentScore = 0f;
        for (int i = 0; i < m_candidates.Count; i++)
        {
            PointLight light = m_candidates[i];
            if (!light.CastsTerrainShadow)
                continue;
            float distance = Vector3.Distance(sample, light.Position);
            if (distance >= light.Radius)
                continue;
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

        if (currentScore >= 0.85f * bestScore)
            selected = current;
        return bestScore > 0.002f;
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
            float directionalAttenuation = GetDirectionalAttenuation(light, sample);
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

    private int GetLod(float distance)
    {
        // Source: SubsystemShadows.QueueShadow and SubsystemSky.VisibilityRange
        int lod = distance < 28f ? 0 : (distance < 72f ? 1 : (distance < 128f ? 2 : 3));
        return distance < 72f ? MathUtils.Min(lod + m_qualityPenalty, 2) : lod;
    }

    private int GetModelPointLightShadowCount(float distance)
    {
        // Source: SubsystemModelsRenderer.DrawModelsExtras and Block.GetEmittedLightAmount.
        // LOD may reduce model silhouette resolution, but it should not completely disable
        // torch/lamp-cast shadows while the entity is still inside visible object range.
        if (distance < 32f && m_qualityPenalty == 0)
            return 2;
        return distance < 128f ? 1 : 0;
    }

    private static double GetUpdateInterval(int lod)
    {
        return lod switch
        {
            0 => 1.0 / 24.0,
            1 => 0.08,
            2 => 0.18,
            _ => 0.33
        };
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

    private static void RemoveStaleCaches(Dictionary<ComponentBody, ShadowCache> caches)
    {
        if (Time.FrameIndex % 120 != 0)
            return;
        List<ComponentBody> stale = null;
        foreach (KeyValuePair<ComponentBody, ShadowCache> pair in caches)
        {
            if (Time.FrameIndex - pair.Value.LastDrawFrame <= 240)
                continue;
            stale ??= new List<ComponentBody>();
            stale.Add(pair.Key);
        }
        if (stale == null)
            return;
        for (int i = 0; i < stale.Count; i++)
            caches.Remove(stale[i]);
    }

}
