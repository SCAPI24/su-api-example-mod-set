using System;
using Engine;
using Engine.Graphics;
using Game;

namespace SuAPIShadows;

public sealed partial class SubsystemDynamicShadows
{
    // Source: TerrainRenderer.cs:SetupTerrainChunkGeometryVertexIndexBuffers — compare the actual uploaded buffers.
    private void CaptureSunGeometry(Vector3 center, float coverage, ShadowGeometrySnapshot snapshot)
    {
        snapshot.Clear();
        float rangeSquared = MathUtils.Sqr(0.8f * coverage);
        foreach (TerrainChunk chunk in m_terrain.Terrain.AllocatedChunks)
        {
            if (Vector2.DistanceSquared(chunk.Center, center.XZ) > rangeSquared) continue;
            snapshot.Identities.Add(chunk);
            snapshot.Identities.Add(chunk.Geometry);
            snapshot.Values.Add((int)chunk.State);
            snapshot.Values.Add(chunk.ModificationCounter);
            snapshot.Values.Add(chunk.NewGeometryData ? 1 : 0);
            if (chunk.Geometry == null) { snapshot.Values.Add(-1); continue; }
            snapshot.VegetationFlags |= m_vegetationGeometry.Flags(chunk);
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
        }
    }

    // Source: Dawnrift SubsystemShadowMap.ShouldRenderCascade; keep this Mod's existing 512px projection.
    private void UpdateTerrainShadowMap(Camera camera)
    {
        if (!m_terrainShadowsAvailable || !m_terrainShadowStates.TryGetValue(camera, out TerrainShadowState state)) return;
        state.LastDrawFrame = Time.FrameIndex;
        if (!TryGetCelestialShadow(out Vector3 direction, out _))
        {
            state.IsValid = false;
            state.DynamicValid = false;
            state.NearDynamicValid = false;
            return;
        }
        bool controlChanged = state.VegetationSnapshot == null || state.VegetationSnapshot.Version != m_vegetationFrame.Version;
        if (Time.RealTime < state.NextUpdateTime && state.IsValid && !controlChanged) return;
        state.NextUpdateTime = Time.RealTime + GetTerrainShadowInterval();
        float coverage = m_qualityPenalty == 0 ? 80f : 64f;
        Vector3 previousCenter = state.AnchorCenter;
        Vector3 center = GetStableShadowAnchor(state, camera.ViewPosition);
        CaptureSunGeometry(center, coverage, state.PendingGeometry);
        bool moving = HasActiveVegetation(state.PendingGeometry.VegetationFlags, m_vegetationFrame) ||
            HasActiveVegetation(state.PendingGeometry.VegetationFlags, state.VegetationSnapshot);
        bool vegetationDue = moving && m_vegetationFrame.SimulationTime >= state.NextVegetationUpdate;
        if (!controlChanged && !vegetationDue && state.IsValid && center == previousCenter && coverage == state.Coverage &&
            Vector3.DistanceSquared(direction, state.LastLightDirection) < 0.000004f &&
            state.CachedGeometry.Matches(state.PendingGeometry)) return;

        Vector3 up = MathUtils.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.92f ? Vector3.UnitZ : Vector3.UnitY;
        Vector3 origin = new Vector3(MathUtils.Floor(center.X), 0f, MathUtils.Floor(center.Z));
        Vector3 localCenter = center - origin;
        Matrix matrix = Matrix.CreateLookAt(localCenter + 300f * direction, localCenter, up) *
            Matrix.CreateOrthographic(coverage, coverage, 1f, 600f);
        VegetationMotionFrame vegetation = m_vegetationFrame.Clone();
        RenderTerrainShadowMap(state.Target, origin.XZ, matrix, center, coverage, vegetation);
        state.VegetationSnapshot = vegetation;
        state.NextVegetationUpdate = m_vegetationFrame.SimulationTime + 0.5;
        state.ShadowOrigin = origin.XZ;
        state.ShadowMatrix = matrix;
        state.LastLightDirection = direction;
        state.Coverage = coverage;
        state.IsValid = true;
        state.DynamicValid = false;
        ShadowGeometrySnapshot old = state.CachedGeometry;
        state.CachedGeometry = state.PendingGeometry;
        state.PendingGeometry = old;
    }

    private double GetTerrainShadowInterval() => m_qualityPenalty switch { 0 => 0.18, 1 => 0.3, _ => 0.45 };

    // Source: RenderTarget2D.cs:RenderTarget2D; retain the wide model map and add a small near field.
    private static void EnsureDynamicShadowTarget(TerrainShadowState state)
    {
        if (state.DynamicTarget == null) state.DynamicTarget = CreateClearedSunTarget(256);
        if (state.NearDynamicTarget == null) state.NearDynamicTarget = CreateClearedSunTarget(512);
    }

    private static RenderTarget2D CreateClearedSunTarget(int resolution)
    {
        RenderTarget2D previousTarget = Display.RenderTarget;
        Viewport previousViewport = Display.Viewport;
        Rectangle previousScissor = Display.ScissorRectangle;
        RenderTarget2D target = null;
        try
        {
            target = new RenderTarget2D(resolution, resolution, 1, ColorFormat.Rgba8888, DepthFormat.Depth16);
            Display.RenderTarget = target;
            Display.Clear(Color.White, 1f, 0);
            return target;
        }
        catch { target?.Dispose(); throw; }
        finally
        {
            Display.RenderTarget = previousTarget;
            Display.Viewport = previousViewport;
            Display.ScissorRectangle = previousScissor;
        }
    }

    // Source: Matrix.CreateLookAt/CreateOrthographic; use 24m for near actors without changing distant coverage.
    private bool UpdateNearDynamicProjection(Camera camera, TerrainShadowState state)
    {
        Vector3 direction = state.LastLightDirection;
        Vector3 up = MathUtils.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.92f ? Vector3.UnitZ : Vector3.UnitY;
        Matrix orientation = Matrix.CreateLookAt(Vector3.Zero, -direction, up);
        Matrix inverse = Matrix.Invert(orientation);
        Vector3 snapped = Vector3.Transform(camera.ViewPosition, orientation);
        const float step = 24f / 512f;
        snapped.X = MathUtils.Floor(snapped.X / step + 0.5f) * step;
        snapped.Y = MathUtils.Floor(snapped.Y / step + 0.5f) * step;
        snapped.Z = MathUtils.Floor(snapped.Z * 2f + 0.5f) * 0.5f;
        Vector3 center = Vector3.Transform(snapped, inverse);
        Vector3 origin = new Vector3(MathUtils.Floor(center.X), 0f, MathUtils.Floor(center.Z));
        Vector3 localCenter = center - origin;
        Matrix matrix = Matrix.CreateLookAt(localCenter + 300f * direction, localCenter, up) *
            Matrix.CreateOrthographic(24f, 24f, 1f, 600f);
        bool moved = !state.NearDynamicValid || center != state.NearDynamicCenter || matrix != state.NearDynamicMatrix;
        if (moved)
        {
            state.NearDynamicCenter = center;
            state.NearDynamicOrigin = origin.XZ;
            state.NearDynamicMatrix = matrix;
        }
        return moved;
    }

    // Source: ModelShadowRenderer.Draw — update cached depth at the existing 12 Hz actor cadence.
    private void UpdateDynamicSunShadow(Camera camera, TerrainShadowState state)
    {
        if (!state.IsValid || !m_terrainShadowsAvailable) return;
        if (state.DynamicValid && state.NearDynamicValid && Time.RealTime < state.NextDynamicUpdate) return;
        bool nearMoved = UpdateNearDynamicProjection(camera, state);
        bool poseChanged = state.DynamicRevision != m_modelShadowRenderer.Revision;
        bool renderWide = !state.DynamicValid || poseChanged;
        bool renderNear = !state.NearDynamicValid || poseChanged || nearMoved;
        if (!renderWide && !renderNear) return;
        RenderTarget2D oldTarget = Display.RenderTarget;
        Viewport oldViewport = Display.Viewport;
        Rectangle oldScissor = Display.ScissorRectangle;
        try
        {
            if (renderWide)
            {
                DrawDynamicSunTarget(camera, state.DynamicTarget, state.ShadowMatrix, state.ShadowOrigin);
                state.DynamicValid = true;
            }
            if (renderNear)
            {
                DrawDynamicSunTarget(camera, state.NearDynamicTarget, state.NearDynamicMatrix, state.NearDynamicOrigin);
                state.NearDynamicValid = true;
            }
            state.DynamicRevision = m_modelShadowRenderer.Revision;
            state.NextDynamicUpdate = Time.RealTime + 1.0 / 12.0;
        }
        finally
        {
            Display.RenderTarget = oldTarget;
            Display.Viewport = oldViewport;
            Display.ScissorRectangle = oldScissor;
        }
    }

    private void DrawDynamicSunTarget(Camera camera, RenderTarget2D target, Matrix matrix, Vector2 origin)
    {
        Display.RenderTarget = target;
        Display.Viewport = new Viewport(0, 0, target.Width, target.Height);
        Display.ScissorRectangle = new Rectangle(0, 0, target.Width, target.Height);
        Display.Clear(Color.White, 1f, 0);
        m_modelShadowRenderer.Draw(camera, matrix, origin, new BoundingFrustum(matrix), null, 0f);
    }

    // Source: RenderTarget2D.cs:HandleDeviceReset — GPU contents are undefined after device restoration.
    private void InvalidateShadowTargets()
    {
        foreach (TerrainShadowState state in m_terrainShadowStates.Values)
        {
            state.IsValid = false;
            state.DynamicValid = false;
            state.NearDynamicValid = false;
            state.NextUpdateTime = 0.0;
            state.NextDynamicUpdate = 0.0;
        }
    }

    private static void SetTerrainShadowParameters(Shader shader, TerrainShadowState state,
        bool enabled, float strength, float celestialLightFloor, float fogIntensity)
    {
        shader.GetParameter("u_shadowEnabled").SetValue(enabled ? 1f : 0f);
        shader.GetParameter("u_shadowStrength").SetValue(MathUtils.Clamp(strength, 0f, 0.55f));
        shader.GetParameter("u_celestialLightFloor").SetValue(MathUtils.Saturate(celestialLightFloor));
        shader.GetParameter("u_shadowOrigin").SetValue(state.ShadowOrigin);
        shader.GetParameter("u_shadowMatrix").SetValue(state.ShadowMatrix);
        shader.GetParameter("u_shadowTexture").SetValue(state.Target);
        shader.GetParameter("u_shadowSamplerState").SetValue(SamplerState.LinearClamp);
        shader.GetParameter("u_shadowTexelSize").SetValue(new Vector2(1f / state.Resolution));
        shader.GetParameter("u_fogShadowFactor").SetValue(MathUtils.Saturate(fogIntensity));
        shader.GetParameter("u_dynamicShadowEnabled").SetValue(enabled && state.DynamicValid ? 1f : 0f);
        shader.GetParameter("u_dynamicShadowTexture").SetValue(state.DynamicTarget);
        shader.GetParameter("u_dynamicShadowSamplerState").SetValue(SamplerState.PointClamp);
        shader.GetParameter("u_dynamicShadowTexelSize").SetValue(new Vector2(1f / state.DynamicTarget.Width));
        shader.GetParameter("u_nearDynamicShadowEnabled").SetValue(enabled && state.NearDynamicValid ? 1f : 0f);
        shader.GetParameter("u_nearDynamicShadowTexture").SetValue(state.NearDynamicTarget);
        shader.GetParameter("u_nearDynamicShadowSamplerState").SetValue(SamplerState.PointClamp);
        shader.GetParameter("u_nearDynamicShadowMatrix").SetValue(state.NearDynamicMatrix);
        shader.GetParameter("u_nearDynamicShadowOrigin").SetValue(state.NearDynamicOrigin);
        shader.GetParameter("u_nearDynamicShadowTexelSize").SetValue(new Vector2(1f / state.NearDynamicTarget.Width));
        SetPointShadowParameters(shader, state);
    }
}
