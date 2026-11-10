using System;
using System.Collections.Generic;
using Engine;
using Engine.Graphics;
using Game;

namespace SuAPIShadows;

public sealed partial class SubsystemDynamicShadows
{
    // Source: GLWrapper.cs:GLWrapper.Clear; Clear disables scissor, so cached atlas tiles
    // must be cleared by a depth-writing draw restricted to the selected face viewport.
    private const string PointTileClearVertex = """
// <Semantic Name='POSITION' Attribute='a_position' />
attribute vec3 a_position;
void main()
{
    gl_Position = vec4(a_position.xy, 1.0, 1.0);
    OPENGL_POSITION_FIX;
}
""";

    private const string PointTileClearPixel = """
#ifdef GL_ES
precision highp float;
#endif
void main()
{
    gl_FragColor = vec4(1.0);
}
""";

    private static readonly VertexPositionColor[] s_pointClearVertices =
    {
        new VertexPositionColor(new Vector3(-1f, -1f, 1f), Color.White),
        new VertexPositionColor(new Vector3(1f, -1f, 1f), Color.White),
        new VertexPositionColor(new Vector3(1f, 1f, 1f), Color.White),
        new VertexPositionColor(new Vector3(-1f, 1f, 1f), Color.White)
    };
    private static readonly ushort[] s_pointClearIndices = { 0, 1, 2, 0, 2, 3 };
    private static readonly DepthStencilState s_pointClearDepthState = new DepthStencilState
    {
        DepthBufferTestEnable = true,
        DepthBufferWriteEnable = true,
        DepthBufferFunction = CompareFunction.Always
    };

    // Source: TerrainRenderer.cs:TerrainRenderer.SetupTerrainChunkGeometryVertexIndexBuffers;
    // actual GPU buffer identities and ranges change when uploaded geometry is replaced.
    // Retained CPU slice hashes alone do not establish that these buffers are unchanged.
    private sealed class ShadowGeometrySnapshot
    {
        public readonly List<TerrainChunk> Chunks = new List<TerrainChunk>();
        public readonly List<object> Identities = new List<object>();
        public readonly List<int> Values = new List<int>();

        public void Clear()
        {
            Chunks.Clear();
            Identities.Clear();
            Values.Clear();
        }

        public bool Matches(ShadowGeometrySnapshot other)
        {
            if (Identities.Count != other.Identities.Count || Values.Count != other.Values.Count)
                return false;
            for (int i = 0; i < Identities.Count; i++)
                if (!ReferenceEquals(Identities[i], other.Identities[i]))
                    return false;
            for (int i = 0; i < Values.Count; i++)
                if (Values[i] != other.Values[i])
                    return false;
            return true;
        }
    }

    private sealed class PointShadowSlot
    {
        public object Key;
        public PointLight Light;
        public bool Selected;
        public bool StaticValid;
        public bool DynamicValid;
        public bool StaticDirty;
        public long DynamicRevision = long.MinValue;
        public double NextDynamicCheck;
        public double LastTaskTime;
        public ShadowGeometrySnapshot Cached = new ShadowGeometrySnapshot();
        public ShadowGeometrySnapshot Pending = new ShadowGeometrySnapshot();
    }

    private sealed class PointShadowState
    {
        public readonly PointShadowSlot[] Slots = new PointShadowSlot[MaxTerrainPointShadowLights];
        public readonly Vector2[] Valid = new Vector2[MaxTerrainPointShadowLights];
        public RenderTarget2D StaticTarget;
        public RenderTarget2D DynamicTarget;
        public Shader ClearShader;
        public double NextSelection;
        public int LastUpdateFrame = -1;
        public int LastTaskFrame = -1;
        public int NextSlot;

        public PointShadowState()
        {
            for (int i = 0; i < Slots.Length; i++)
                Slots[i] = new PointShadowSlot();
            Display.DeviceReset += InvalidateTargets;
        }

        // Source: RenderTarget2D.cs:HandleDeviceReset; recreated GPU storage has no cached contents.
        public void InvalidateTargets()
        {
            foreach (PointShadowSlot slot in Slots)
            {
                slot.StaticValid = false;
                slot.DynamicValid = false;
                slot.StaticDirty = true;
                slot.NextDynamicCheck = 0.0;
            }
            NextSelection = 0.0;
            LastUpdateFrame = -1;
            LastTaskFrame = -1;
        }
    }

    // Source: SubsystemDynamicShadows.cs:SelectTerrainShadowLights; preserve its selection
    // policy while retaining the atlas index of every light that stays selected.
    private void UpdatePointShadowSelection(Camera camera, TerrainShadowState state)
    {
        PointShadowState points = state.PointShadows;
        Vector3 sample = GetTerrainPointShadowSample(camera);
        CollectNearbyLights(sample, GetTerrainPointShadowSearchRadius());
        int count = SelectTerrainShadowLights(camera, sample, state.PointLightKeys,
            m_selectedTerrainPointLights, GetTerrainPointShadowLightLimit());
        for (int i = 0; i < points.Slots.Length; i++)
            points.Slots[i].Selected = false;

        // Reserve continuing slots first, before allocating slots for new lights.
        for (int i = 0; i < count; i++)
        {
            PointLight light = m_selectedTerrainPointLights[i];
            for (int j = 0; j < points.Slots.Length; j++)
            {
                PointShadowSlot slot = points.Slots[j];
                if (slot.Key != null && Equals(slot.Key, light.Key))
                {
                    AssignPointLight(slot, light);
                    break;
                }
            }
        }
        for (int i = 0; i < points.Slots.Length; i++)
        {
            PointShadowSlot slot = points.Slots[i];
            if (!slot.Selected)
            {
                slot.Key = null;
                slot.StaticValid = false;
                slot.DynamicValid = false;
                slot.Cached.Clear();
                slot.Pending.Clear();
            }
        }
        for (int i = 0; i < count; i++)
        {
            PointLight light = m_selectedTerrainPointLights[i];
            bool assigned = false;
            for (int j = 0; j < points.Slots.Length; j++)
                assigned |= points.Slots[j].Selected && Equals(points.Slots[j].Key, light.Key);
            if (assigned)
                continue;
            for (int j = 0; j < points.Slots.Length; j++)
            {
                if (points.Slots[j].Key != null)
                    continue;
                AssignPointLight(points.Slots[j], light);
                break;
            }
        }
        for (int i = 0; i < points.Slots.Length; i++)
        {
            PointShadowSlot slot = points.Slots[i];
            state.PointLightKeys[i] = slot.Key;
            if (!slot.Selected)
                continue;
            CapturePointGeometry(slot.Light, slot.Pending);
            slot.StaticDirty = !slot.StaticValid || !slot.Cached.Matches(slot.Pending);
            if (slot.StaticDirty)
                slot.StaticValid = false;
        }
        state.PointLightCount = count;
        state.IsPointValid = count > 0;
        points.NextSelection = Time.RealTime + 0.1;
    }

    private static void AssignPointLight(PointShadowSlot slot, PointLight light)
    {
        bool moved = slot.Key == null || !Equals(slot.Key, light.Key) ||
            slot.Light.Position != light.Position || slot.Light.Radius != light.Radius;
        slot.Key = light.Key;
        slot.Light = light;
        slot.Selected = true;
        if (moved)
        {
            slot.StaticValid = false;
            slot.DynamicValid = false;
            slot.StaticDirty = true;
            slot.DynamicRevision = long.MinValue;
            slot.NextDynamicCheck = 0.0;
        }
    }

    private void CapturePointGeometry(PointLight light, ShadowGeometrySnapshot snapshot)
    {
        snapshot.Clear();
        float rangeSquared = MathUtils.Sqr(light.Radius + 12f);
        foreach (TerrainChunk chunk in m_terrain.Terrain.AllocatedChunks)
        {
            if (Vector2.DistanceSquared(chunk.Center, light.Position.XZ) > rangeSquared)
                continue;
            snapshot.Identities.Add(chunk);
            snapshot.Identities.Add(chunk.Geometry);
            snapshot.Values.Add((int)chunk.State);
            snapshot.Values.Add(chunk.ModificationCounter);
            snapshot.Values.Add(chunk.NewGeometryData ? 1 : 0);
            if (chunk.Geometry == null)
            {
                snapshot.Values.Add(-1);
                continue;
            }
            snapshot.Values.Add(chunk.Geometry.Buffers.Count);
            foreach (TerrainChunkGeometry.Buffer buffer in chunk.Geometry.Buffers)
            {
                snapshot.Identities.Add(buffer);
                snapshot.Identities.Add(buffer.VertexBuffer);
                snapshot.Identities.Add(buffer.IndexBuffer);
                for (int subset = 0; subset < 7; subset++)
                {
                    snapshot.Values.Add(buffer.SubsetIndexBufferStarts[subset]);
                    snapshot.Values.Add(buffer.SubsetIndexBufferEnds[subset]);
                }
            }
            if (!chunk.NewGeometryData && chunk.Geometry.Buffers.Count > 0)
                snapshot.Chunks.Add(chunk);
        }
    }

    // Source: SubsystemShadowMap.cs:ShouldRenderCascade; reuse valid results and spread
    // changed work over frames. One camera performs at most one complete lamp task per frame.
    private void UpdatePointShadows(Camera camera, TerrainShadowState state)
    {
        if (!m_terrainShadowsAvailable || camera == null)
            return;
        PointShadowState points = state.PointShadows ??= new PointShadowState();
        if (points.LastUpdateFrame == Time.FrameIndex)
            return;
        points.LastUpdateFrame = Time.FrameIndex;
        if (Time.RealTime >= points.NextSelection)
            UpdatePointShadowSelection(camera, state);
        if (state.PointLightCount == 0 || points.LastTaskFrame == Time.FrameIndex)
            return;

        bool needsCheck = false;
        for (int i = 0; i < points.Slots.Length; i++)
        {
            PointShadowSlot slot = points.Slots[i];
            needsCheck |= slot.Selected && (slot.StaticDirty ||
                !slot.DynamicValid || Time.RealTime >= slot.NextDynamicCheck);
        }
        if (!needsCheck)
            return;

        // Source: SubsystemDynamicShadows.cs:Draw; its camera batch prepares models once
        // with the shared range, before solar and local-light work consume the revision.
        long revision = m_modelShadowRenderer.Revision;
        int selectedSlot = -1;
        double bestPriority = double.MinValue;
        for (int offset = 0; offset < points.Slots.Length; offset++)
        {
            int index = (points.NextSlot + offset) % points.Slots.Length;
            PointShadowSlot slot = points.Slots[index];
            if (!slot.Selected)
                continue;
            bool due = Time.RealTime >= slot.NextDynamicCheck;
            bool dynamicDirty = !slot.DynamicValid || (due && slot.DynamicRevision != revision);
            if (due && !dynamicDirty)
                slot.NextDynamicCheck = Time.RealTime + PointDynamicInterval(camera, slot.Light);
            if (!slot.StaticDirty && !dynamicDirty)
                continue;
            double priority = Time.RealTime - slot.LastTaskTime;
            if (!slot.StaticValid || !slot.DynamicValid)
                priority += 10000.0;
            if (priority > bestPriority)
            {
                bestPriority = priority;
                selectedSlot = index;
            }
        }
        if (selectedSlot < 0)
            return;
        EnsurePointShadowResources(points);
        PointShadowSlot task = points.Slots[selectedSlot];
        // Geometry may have uploaded since the 10 Hz selection pass. Capture at submission.
        CapturePointGeometry(task.Light, task.Pending);
        task.StaticDirty |= !task.Cached.Matches(task.Pending);
        RenderPointShadowTask(camera, state, selectedSlot, revision);
        points.LastTaskFrame = Time.FrameIndex;
        points.NextSlot = (selectedSlot + 1) % points.Slots.Length;
    }

    private static double PointDynamicInterval(Camera camera, PointLight light)
    {
        return Vector3.DistanceSquared(camera.ViewPosition, light.Position) < 256f ? 0.1 : 0.25;
    }

    private static void EnsurePointShadowResources(PointShadowState points)
    {
        RenderTarget2D previous = Display.RenderTarget;
        Viewport viewport = Display.Viewport;
        Rectangle scissor = Display.ScissorRectangle;
        try
        {
            if (points.ClearShader == null)
                points.ClearShader = new Shader(PointTileClearVertex, PointTileClearPixel);
            if (points.StaticTarget == null)
            {
                points.StaticTarget = new RenderTarget2D(3 * 96, 2 * MaxTerrainPointShadowLights * 96,
                    1, ColorFormat.Rgba8888, DepthFormat.Depth16);
                Display.RenderTarget = points.StaticTarget;
                Display.Clear(Color.White, 1f);
            }
            if (points.DynamicTarget == null)
            {
                points.DynamicTarget = new RenderTarget2D(3 * 64, 2 * MaxTerrainPointShadowLights * 64,
                    1, ColorFormat.Rgba8888, DepthFormat.Depth16);
                Display.RenderTarget = points.DynamicTarget;
                Display.Clear(Color.White, 1f);
            }
        }
        finally
        {
            Display.RenderTarget = previous;
            Display.Viewport = viewport;
            Display.ScissorRectangle = scissor;
        }
    }

    private void RenderPointShadowTask(Camera camera, TerrainShadowState state, int index, long revision)
    {
        PointShadowState points = state.PointShadows;
        PointShadowSlot slot = points.Slots[index];
        PointLight light = slot.Light;
        Vector3 origin = new Vector3(MathUtils.Floor(light.Position.X), 0f,
            MathUtils.Floor(light.Position.Z));
        Vector3 localLight = light.Position - origin;
        Matrix projection = Matrix.CreatePerspectiveFieldOfView((float)Math.PI / 2f,
            1f, 0.08f, light.Radius);
        RenderTarget2D previousTarget = Display.RenderTarget;
        Viewport previousViewport = Display.Viewport;
        Rectangle previousScissor = Display.ScissorRectangle;
        BlendState previousBlend = Display.BlendState;
        DepthStencilState previousDepth = Display.DepthStencilState;
        RasterizerState previousRasterizer = Display.RasterizerState;
        // A partially completed task must never be sampled after an explicit render failure.
        bool renderStatic = slot.StaticDirty || !slot.StaticValid;
        if (renderStatic)
            slot.StaticValid = false;
        slot.DynamicValid = false;
        try
        {
            if (renderStatic)
            {
                m_pointShadowMapShader.GetParameter("u_texture").SetValue(
                    m_terrain.SubsystemAnimatedTextures.AnimatedBlocksTexture);
                m_pointShadowMapShader.GetParameter("u_samplerState").SetValue(SamplerState.PointClamp);
                m_pointShadowMapShader.GetParameter("u_alphaThreshold").SetValue(0.5f);
                m_pointShadowMapShader.GetParameter("u_shadowOrigin").SetValue(origin.XZ);
                m_pointShadowMapShader.GetParameter("u_lightPosition").SetValue(localLight);
                m_pointShadowMapShader.GetParameter("u_lightRadius").SetValue(light.Radius);
            }
            for (int face = 0; face < 6; face++)
            {
                Matrix matrix = Matrix.CreateLookAt(localLight,
                    localLight + s_cubeDirections[face], s_cubeUps[face]) * projection;
                BoundingFrustum frustum = new BoundingFrustum(matrix);
                if (renderStatic)
                {
                    ClearPointShadowFace(points, points.StaticTarget, index, face, 96);
                    m_pointShadowMapShader.GetParameter("u_shadowMatrix").SetValue(matrix);
                    foreach (TerrainChunk chunk in slot.Pending.Chunks)
                    {
                        BoundingBox localBox = new BoundingBox(chunk.BoundingBox.Min - origin,
                            chunk.BoundingBox.Max - origin);
                        if (frustum.Intersection(localBox))
                            DrawTerrainSubsets(m_pointShadowMapShader, chunk.Geometry, 63);
                    }
                }
                ClearPointShadowFace(points, points.DynamicTarget, index, face, 64);
                m_modelShadowRenderer.Draw(camera, matrix, origin.XZ, frustum, light.Position, light.Radius);
            }
            if (renderStatic)
            {
                ShadowGeometrySnapshot previous = slot.Cached;
                slot.Cached = slot.Pending;
                slot.Pending = previous;
                slot.StaticDirty = false;
                slot.StaticValid = true;
            }
            slot.DynamicValid = true;
            slot.DynamicRevision = revision;
            slot.NextDynamicCheck = Time.RealTime + PointDynamicInterval(camera, light);
            slot.LastTaskTime = Time.RealTime;
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

    private static void ClearPointShadowFace(PointShadowState points, RenderTarget2D target,
        int index, int face, int faceSize)
    {
        int x = face % 3 * faceSize;
        int y = (index * 2 + face / 3) * faceSize;
        Display.RenderTarget = target;
        Display.Viewport = new Viewport(x, y, faceSize, faceSize);
        Display.ScissorRectangle = new Rectangle(x, y, faceSize, faceSize);
        Display.RasterizerState = RasterizerState.CullNoneScissor;
        Display.BlendState = BlendState.Opaque;
        Display.DepthStencilState = s_pointClearDepthState;
        Display.DrawUserIndexed(PrimitiveType.TriangleList, points.ClearShader,
            VertexPositionColor.VertexDeclaration, s_pointClearVertices, 0,
            s_pointClearVertices.Length, s_pointClearIndices, 0, s_pointClearIndices.Length);
        Display.DepthStencilState = DepthStencilState.Default;
        Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;
    }

    private static void SetPointShadowParameters(Shader shader, TerrainShadowState state)
    {
        PointShadowState points = state.PointShadows;
        bool enabled = SettingsManager.ObjectsShadowsEnabled && points != null &&
            points.StaticTarget != null && points.DynamicTarget != null && state.PointLightCount > 0;
        shader.GetParameter("u_pointShadowEnabled").SetValue(enabled ? 1f : 0f);
        // Stable slots can contain holes; validity flags, rather than a compact count, select lights.
        shader.GetParameter("u_pointShadowCount").SetValue(enabled ? (float)MaxTerrainPointShadowLights : 0f);
        if (!enabled)
            return;
        for (int i = 0; i < points.Slots.Length; i++)
        {
            PointShadowSlot slot = points.Slots[i];
            points.Valid[i] = new Vector2(slot.Selected && slot.StaticValid ? 1f : 0f,
                slot.Selected && slot.DynamicValid ? 1f : 0f);
            if (!slot.Selected)
                continue;
            PointLight light = slot.Light;
            Vector3 origin = new Vector3(MathUtils.Floor(light.Position.X), 0f,
                MathUtils.Floor(light.Position.Z));
            Vector3 local = light.Position - origin;
            state.PointShadowOrigins[i] = origin.XZ;
            state.PointLightPositionRadius[i] = new Vector4(local.X, local.Y, local.Z, light.Radius);
            state.PointLightDirectionStrength[i] = new Vector4(light.Direction.X,
                light.Direction.Y, light.Direction.Z,
                light.IsDirectional ? 0.55f * light.Strength : -0.55f * light.Strength);
        }
        shader.GetParameter("u_pointShadowOrigins").SetValue(state.PointShadowOrigins, MaxTerrainPointShadowLights);
        shader.GetParameter("u_pointShadowLightPositionRadius").SetValue(state.PointLightPositionRadius, MaxTerrainPointShadowLights);
        shader.GetParameter("u_pointShadowLightDirectionStrength").SetValue(state.PointLightDirectionStrength, MaxTerrainPointShadowLights);
        shader.GetParameter("u_pointShadowValid").SetValue(points.Valid, MaxTerrainPointShadowLights);
        shader.GetParameter("u_pointShadowTexture").SetValue(points.StaticTarget);
        shader.GetParameter("u_pointShadowSamplerState").SetValue(SamplerState.PointClamp);
        shader.GetParameter("u_pointShadowTexelSize").SetValue(new Vector2(1f / (3f * 96),
            1f / (2f * MaxTerrainPointShadowLights * 96)));
        shader.GetParameter("u_pointDynamicShadowTexture").SetValue(points.DynamicTarget);
        shader.GetParameter("u_pointDynamicShadowSamplerState").SetValue(SamplerState.PointClamp);
        shader.GetParameter("u_pointDynamicShadowTexelSize").SetValue(new Vector2(1f / (3f * 64),
            1f / (2f * MaxTerrainPointShadowLights * 64)));
    }

    private static void DisposePointShadows(TerrainShadowState state)
    {
        PointShadowState points = state.PointShadows;
        if (points == null)
            return;
        Display.DeviceReset -= points.InvalidateTargets;
        points.StaticTarget?.Dispose();
        points.DynamicTarget?.Dispose();
        points.ClearShader?.Dispose();
        state.PointShadows = null;
    }
}
