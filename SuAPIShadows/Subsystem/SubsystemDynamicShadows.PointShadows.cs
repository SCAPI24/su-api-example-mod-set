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
        public int VegetationFlags;

        public void Clear()
        {
            Chunks.Clear();
            Identities.Clear();
            Values.Clear();
            VegetationFlags = 0;
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
        public int Generation;
        public int StaticPhysicalSlot;
        public int DynamicPhysicalSlot;
        public int DynamicFaceMask;
        public int PendingFrame = -1;
        public ModelShadowRenderer.PointSnapshot CompletedModels;
        public double NextDynamicCheck;
        public VegetationMotionFrame VegetationSnapshot;
        public double NextVegetationUpdate;
        public double NextGeometryCheck;
        public ShadowGeometrySnapshot Cached = new ShadowGeometrySnapshot();
        public ShadowGeometrySnapshot Pending = new ShadowGeometrySnapshot();
    }

    private sealed class PointShadowState
    {
        public readonly PointShadowSlot[] Slots = new PointShadowSlot[MaxTerrainPointShadowLights];
        public RenderTarget2D StaticTarget;
        public RenderTarget2D DynamicTarget;
        public int StaticFreeSlot = MaxTerrainPointShadowLights;
        public int DynamicFreeSlot = MaxTerrainPointShadowLights;
        public PointStaticBuild StaticBuild;
        public PointDynamicBuild DynamicBuild;
        public readonly ShadowGeometrySnapshot VerifyGeometry = new ShadowGeometrySnapshot();
        public Shader ClearShader;
        public double NextSelection;
        public int LastUpdateFrame = -1;
        public int NextStaticSlot;
        public int NextDynamicSlot;
        public int NextGeometrySlot;

        public PointShadowState()
        {
            for (int i = 0; i < Slots.Length; i++)
                Slots[i] = new PointShadowSlot { StaticPhysicalSlot = i, DynamicPhysicalSlot = i };
            Display.DeviceReset += InvalidateTargets;
        }

        // Source: RenderTarget2D.cs:HandleDeviceReset; recreated GPU storage has no cached contents.
        public void InvalidateTargets()
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                PointShadowSlot slot = Slots[i];
                InvalidatePointSlot(this, slot);
                slot.StaticPhysicalSlot = i;
                slot.DynamicPhysicalSlot = i;
            }
            StaticFreeSlot = DynamicFreeSlot = MaxTerrainPointShadowLights;
            NextSelection = 0.0;
            LastUpdateFrame = -1;
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
                    AssignPointLight(points, slot, light);
                    break;
                }
            }
        }
        for (int i = 0; i < points.Slots.Length; i++)
        {
            PointShadowSlot slot = points.Slots[i];
            if (!slot.Selected && slot.Key != null)
            {
                InvalidatePointSlot(points, slot);
                slot.Key = null;
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
                AssignPointLight(points, points.Slots[j], light);
                break;
            }
        }
        for (int i = 0; i < points.Slots.Length; i++)
        {
            PointShadowSlot slot = points.Slots[i];
            state.PointLightKeys[i] = slot.Key;
        }
        state.PointLightCount = count;
        state.IsPointValid = count > 0;
        points.NextSelection = Time.RealTime + 0.1;
    }

    // Source: CapturePointGeometry; selection and dynamic poses do not require scanning static GPU buffers.
    private void CheckPointShadowGeometry(PointShadowState points)
    {
        double now = Time.RealTime;
        foreach (PointShadowSlot slot in points.Slots)
        {
            if (!slot.Selected) continue;
            bool controlChanged = slot.VegetationSnapshot == null || slot.VegetationSnapshot.Version != m_vegetationFrame.Version;
            if (controlChanged)
            {
                slot.StaticValid = false;
                slot.StaticDirty = true;
                // The old front version stays invalid while its replacement builds; do not restart that same version every frame.
                if (points.StaticBuild != null && ReferenceEquals(points.StaticBuild.Slot, slot) &&
                    points.StaticBuild.Vegetation.Version != m_vegetationFrame.Version) points.StaticBuild = null;
            }
            bool moving = HasActiveVegetation(slot.Cached.VegetationFlags, m_vegetationFrame) ||
                HasActiveVegetation(slot.Cached.VegetationFlags, slot.VegetationSnapshot);
            bool vegetationDue = moving && m_vegetationFrame.SimulationTime >= slot.NextVegetationUpdate;
            slot.StaticDirty |= vegetationDue;
            if (slot.DynamicValid && !m_modelShadowRenderer.PointSnapshotResourcesValid(slot.CompletedModels))
            {
                slot.DynamicValid = false;
                slot.DynamicFaceMask = 0;
            }
        }

        // Source: RequestTerrainGeometryCheck; simultaneous invalidations stay requests, with one fair scan per frame.
        for (int offset = 0; offset < points.Slots.Length; offset++)
        {
            int index = (points.NextGeometrySlot + offset) % points.Slots.Length;
            PointShadowSlot slot = points.Slots[index];
            if (!slot.Selected || slot.StaticDirty || now < slot.NextGeometryCheck ||
                (points.StaticBuild != null && ReferenceEquals(points.StaticBuild.Slot, slot))) continue;
            CapturePointGeometry(slot.Light, slot.Pending);
            slot.PendingFrame = Time.FrameIndex;
            slot.NextGeometryCheck = now + GetTerrainGeometryInterval();
            if (!slot.Cached.Matches(slot.Pending))
            {
                slot.StaticValid = false;
                slot.StaticDirty = true;
            }
            points.NextGeometrySlot = (index + 1) % points.Slots.Length;
            break;
        }
    }

    private static void AssignPointLight(PointShadowState points, PointShadowSlot slot, PointLight light)
    {
        bool moved = slot.Key == null || !Equals(slot.Key, light.Key) ||
            slot.Light.Position != light.Position || slot.Light.Radius != light.Radius;
        if (moved) InvalidatePointSlot(points, slot);
        slot.Key = light.Key;
        slot.Light = light;
        slot.Selected = true;
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
            {
                snapshot.Chunks.Add(chunk);
                snapshot.VegetationFlags |= m_vegetationGeometry.Flags(chunk);
            }
        }
    }

    // Source: SurvivalcraftMiniMap.SuComponentMap.SwapBuffers; keep completed fronts while independent layers build.
    // GPU calls remain on the rendering thread. CPU tasks cannot reduce the receiver's GPU work.
    private void UpdatePointShadows(Camera camera, TerrainShadowState state)
    {
        if (!m_terrainShadowsAvailable || camera == null) return;
        PointShadowState points = state.PointShadows ??= new PointShadowState();
        if (points.LastUpdateFrame == Time.FrameIndex) return;
        points.LastUpdateFrame = Time.FrameIndex;
        if (Time.RealTime >= points.NextSelection) UpdatePointShadowSelection(camera, state);
        if (state.PointLightCount == 0) return;
        CheckPointShadowGeometry(points);
        // Allocate at most one target/frame; no partially initialized atlas is published.
        if (!EnsurePointShadowResources(points)) return;
        if (points.DynamicBuild == null) StartPointDynamicBuild(camera, points);
        if (points.StaticBuild == null) StartPointStaticBuild(points);
        AdvancePointBuilds(camera, points);
    }

    private static double PointDynamicInterval(Camera camera, PointLight light)
    {
        return Vector3.DistanceSquared(camera.ViewPosition, light.Position) < 256f ? 0.1 : 0.25;
    }

    // Source: RenderTarget2D.RenderTarget2D; six horizontal faces leave eleven rows within GLES 2.0's 2048px limit.
    private static bool EnsurePointShadowResources(PointShadowState points)
    {
        RenderTarget2D previous = Display.RenderTarget;
        Viewport viewport = Display.Viewport;
        Rectangle scissor = Display.ScissorRectangle;
        try
        {
            if (points.ClearShader == null) points.ClearShader = new Shader(PointTileClearVertex, PointTileClearPixel);
            if (points.StaticTarget == null)
                points.StaticTarget = CreatePointTarget(96, MaxTerrainPointShadowLights + 1);
            else if (points.DynamicTarget == null)
                points.DynamicTarget = CreatePointTarget(64, MaxTerrainPointShadowLights + 1);
            return points.StaticTarget != null && points.DynamicTarget != null;
        }
        finally
        {
            Display.RenderTarget = previous;
            Display.Viewport = viewport;
            Display.ScissorRectangle = scissor;
        }
    }

    private static RenderTarget2D CreatePointTarget(int faceSize, int slots)
    {
        RenderTarget2D target = new RenderTarget2D(6 * faceSize, slots * faceSize,
            1, ColorFormat.Rgba8888, DepthFormat.Depth16);
        try
        {
            Display.RenderTarget = target;
            Display.Clear(Color.White, 1f);
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    private static void ClearPointShadowFace(PointShadowState points, RenderTarget2D target,
        int index, int face, int faceSize)
    {
        int x = face * faceSize;
        int y = index * faceSize;
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

    // Source: UpdatePointShadowSelection; compact receiver uniforms without moving stable atlas slots.
    private static void PreparePointShadowReceiver(TerrainShadowState state)
    {
        state.PointReceiverCount = 0;
        PointShadowState points = state.PointShadows;
        if (!SettingsManager.ObjectsShadowsEnabled || points == null ||
            points.StaticTarget == null || points.DynamicTarget == null) return;
        for (int i = 0; i < points.Slots.Length; i++)
        {
            PointShadowSlot slot = points.Slots[i];
            if (!slot.Selected || (!slot.StaticValid && !slot.DynamicValid)) continue;
            int index = state.PointReceiverCount++;
            PointLight light = slot.Light;
            Vector3 origin = new Vector3(MathUtils.Floor(light.Position.X), 0f,
                MathUtils.Floor(light.Position.Z));
            Vector3 local = light.Position - origin;
            state.PointShadowOrigins[index] = origin.XZ;
            state.PointLightPositionRadius[index] = new Vector4(local.X, local.Y, local.Z, light.Radius);
            state.PointLightDirectionStrength[index] = new Vector4(light.Direction.X,
                light.Direction.Y, light.Direction.Z,
                light.IsDirectional ? 0.55f * light.Strength : -0.55f * light.Strength);
            state.PointReceiverValid[index] = new Vector2(slot.StaticValid ? 1f : 0f, slot.DynamicValid ? 64f + slot.DynamicFaceMask : 0f);
            state.PointReceiverSlots[index] = new Vector2(slot.StaticPhysicalSlot, slot.DynamicPhysicalSlot);
        }
    }

    private static void SetPointShadowParameters(Shader shader, TerrainShadowState state)
    {
        PointShadowState points = state.PointShadows;
        int count = state.PointReceiverCount;
        shader.GetParameter("u_pointShadowEnabled").SetValue(1f);
        shader.GetParameter("u_pointShadowCount").SetValue((float)count);
        shader.GetParameter("u_pointShadowOrigins").SetValue(state.PointShadowOrigins, count);
        shader.GetParameter("u_pointShadowLightPositionRadius").SetValue(state.PointLightPositionRadius, count);
        shader.GetParameter("u_pointShadowLightDirectionStrength").SetValue(state.PointLightDirectionStrength, count);
        shader.GetParameter("u_pointShadowValid").SetValue(state.PointReceiverValid, count);
        shader.GetParameter("u_pointShadowSlots").SetValue(state.PointReceiverSlots, count);
        shader.GetParameter("u_pointShadowTexture").SetValue(points.StaticTarget);
        shader.GetParameter("u_pointShadowSamplerState").SetValue(SamplerState.PointClamp);
        shader.GetParameter("u_pointDynamicShadowTexture").SetValue(points.DynamicTarget);
        shader.GetParameter("u_pointDynamicShadowSamplerState").SetValue(SamplerState.PointClamp);
    }

    private static void DisposePointShadows(TerrainShadowState state)
    {
        PointShadowState points = state.PointShadows;
        if (points == null)
            return;
        Display.DeviceReset -= points.InvalidateTargets;
        foreach (PointShadowSlot slot in points.Slots) InvalidatePointSlot(points, slot);
        points.StaticTarget?.Dispose();
        points.DynamicTarget?.Dispose();
        points.ClearShader?.Dispose();
        state.PointShadows = null;
    }
}
