using System;
using Engine;
using Engine.Graphics;
using Game;

namespace SuAPIShadows;

public sealed partial class SubsystemDynamicShadows
{
    // Source: SubsystemDynamicShadows.PointShadows:UpdatePointShadows; six faces build away from the published slot.
    private sealed class PointStaticBuild
    {
        public PointShadowSlot Slot;
        public int Generation;
        public PointLight Light;
        public int PhysicalSlot;
        public int Face;
        public VegetationMotionFrame Vegetation;
        public ShadowGeometrySnapshot Geometry;
    }

    private sealed class PointDynamicBuild : IDisposable
    {
        public PointShadowSlot Slot;
        public int Generation;
        public PointLight Light;
        public int PhysicalSlot;
        public int Face;
        public int FaceMask;
        public double StartedAt;
        public ModelShadowRenderer.PointSnapshot Models;
        public void Dispose() { Models?.Dispose(); Models = null; }
    }

    private static void CancelPointBuilds(PointShadowState points, PointShadowSlot slot)
    {
        if (points.StaticBuild != null && ReferenceEquals(points.StaticBuild.Slot, slot))
            points.StaticBuild = null;
        if (points.DynamicBuild != null && ReferenceEquals(points.DynamicBuild.Slot, slot))
        {
            points.DynamicBuild.Dispose();
            points.DynamicBuild = null;
        }
    }

    // Source: UpdatePointShadowSelection; a logical light keeps exclusive ownership of both published physical slots.
    private static void InvalidatePointSlot(PointShadowState points, PointShadowSlot slot)
    {
        CancelPointBuilds(points, slot);
        slot.Generation++;
        slot.StaticValid = false;
        slot.DynamicValid = false;
        slot.DynamicFaceMask = 0;
        slot.StaticDirty = true;
        slot.CompletedModels?.Dispose();
        slot.CompletedModels = null;
        slot.NextDynamicCheck = 0.0;
        slot.NextGeometryCheck = 0.0;
        slot.PendingFrame = -1;
    }

    // Source: TerrainRenderer.SetupTerrainChunkGeometryVertexIndexBuffers; buffers may be replaced between build frames.
    // Validate the retained range without traversing AllocatedChunks or touching a released GPU buffer.
    private bool PointGeometryStillCurrent(ShadowGeometrySnapshot snapshot)
    {
        int identity = 0, value = 0;
        while (identity < snapshot.Identities.Count)
        {
            TerrainChunk chunk = (TerrainChunk)snapshot.Identities[identity++];
            object geometry = snapshot.Identities[identity++];
            if (!ReferenceEquals(m_terrain.Terrain.GetChunkAtCoords(chunk.Coords.X, chunk.Coords.Y), chunk) ||
                !ReferenceEquals(chunk.Geometry, geometry)) return false;
            if ((int)chunk.State != snapshot.Values[value++] || chunk.ModificationCounter != snapshot.Values[value++] ||
                (chunk.NewGeometryData ? 1 : 0) != snapshot.Values[value++]) return false;
            int count = snapshot.Values[value++];
            if (geometry == null) { if (count != -1) return false; continue; }
            if (chunk.Geometry.Buffers.Count != count) return false;
            foreach (TerrainChunkGeometry.Buffer buffer in chunk.Geometry.Buffers)
            {
                if (!ReferenceEquals(buffer, snapshot.Identities[identity++]) ||
                    !ReferenceEquals(buffer.VertexBuffer, snapshot.Identities[identity++]) ||
                    !ReferenceEquals(buffer.IndexBuffer, snapshot.Identities[identity++])) return false;
                for (int subset = 0; subset < 7; subset++)
                    if (buffer.SubsetIndexBufferStarts[subset] != snapshot.Values[value++] ||
                        buffer.SubsetIndexBufferEnds[subset] != snapshot.Values[value++]) return false;
            }
        }
        return value == snapshot.Values.Count;
    }

    private void StartPointStaticBuild(PointShadowState points)
    {
        for (int offset = 0; offset < points.Slots.Length; offset++)
        {
            int index = (points.NextStaticSlot + offset) % points.Slots.Length;
            PointShadowSlot slot = points.Slots[index];
            if (!slot.Selected || (!slot.StaticDirty && slot.StaticValid)) continue;
            if (slot.PendingFrame != Time.FrameIndex)
                CapturePointGeometry(slot.Light, slot.Pending);
            slot.PendingFrame = Time.FrameIndex;
            slot.NextGeometryCheck = Time.RealTime + GetTerrainGeometryInterval();
            points.StaticBuild = new PointStaticBuild
            {
                Slot = slot, Generation = slot.Generation, Light = slot.Light,
                PhysicalSlot = points.StaticFreeSlot, Geometry = slot.Pending,
                Vegetation = m_vegetationFrame.Clone()
            };
            points.NextStaticSlot = (index + 1) % points.Slots.Length;
            return;
        }
    }

    private void StartPointDynamicBuild(Camera camera, PointShadowState points)
    {
        for (int offset = 0; offset < points.Slots.Length; offset++)
        {
            int index = (points.NextDynamicSlot + offset) % points.Slots.Length;
            PointShadowSlot slot = points.Slots[index];
            if (!slot.Selected || (slot.DynamicValid && Time.RealTime < slot.NextDynamicCheck)) continue;
            if (slot.DynamicValid && m_modelShadowRenderer.PointSnapshotMatches(slot.CompletedModels,
                slot.Light.Position, slot.Light.Radius))
            {
                slot.NextDynamicCheck = Time.RealTime + PointDynamicInterval(camera, slot.Light);
                continue;
            }
            ModelShadowRenderer.PointSnapshot models = m_modelShadowRenderer.CapturePointSnapshot(
                camera, slot.Light.Position, slot.Light.Radius);
            points.NextDynamicSlot = (index + 1) % points.Slots.Length;
            if (models.IsEmpty)
            {
                // A completed empty set removes old actors immediately without clearing or sampling six white faces.
                slot.CompletedModels?.Dispose();
                slot.CompletedModels = models;
                slot.DynamicValid = true;
                slot.DynamicFaceMask = 0;
                slot.NextDynamicCheck = Time.RealTime + PointDynamicInterval(camera, slot.Light);
                return;
            }
            points.DynamicBuild = new PointDynamicBuild
            {
                Slot = slot, Generation = slot.Generation, Light = slot.Light,
                PhysicalSlot = points.DynamicFreeSlot, StartedAt = Time.RealTime, Models = models
            };
            return;
        }
    }

    // Source: Matrix.CreateLookAt/CreatePerspectiveFieldOfView; all six faces use one frozen light and vegetation/model frame.
    private static Matrix PointFaceMatrix(PointLight light, int face, out Vector3 origin)
    {
        origin = new Vector3(MathUtils.Floor(light.Position.X), 0f, MathUtils.Floor(light.Position.Z));
        Vector3 local = light.Position - origin;
        return Matrix.CreateLookAt(local, local + s_cubeDirections[face], s_cubeUps[face]) *
            Matrix.CreatePerspectiveFieldOfView((float)Math.PI / 2f, 1f, 0.08f, light.Radius);
    }

    private static RenderTarget2D PointPhysicalTarget(PointShadowState points, bool dynamic)
    {
        return dynamic ? points.DynamicTarget : points.StaticTarget;
    }

    private void RenderPointStaticFace(PointShadowState points)
    {
        PointStaticBuild build = points.StaticBuild;
        if (build == null) return;
        PointShadowSlot slot = build.Slot;
        if (!slot.Selected || slot.Generation != build.Generation || build.Vegetation.Version != m_vegetationFrame.Version ||
            !PointGeometryStillCurrent(build.Geometry))
        {
            slot.StaticValid = false;
            slot.StaticDirty = true;
            slot.PendingFrame = -1;
            points.StaticBuild = null;
            return;
        }
        Matrix matrix = PointFaceMatrix(build.Light, build.Face, out Vector3 origin);
        BoundingFrustum frustum = new BoundingFrustum(matrix);
        ClearPointShadowFace(points, PointPhysicalTarget(points, false), build.PhysicalSlot, build.Face, 96);
        ApplyVegetation(m_pointShadowMapShader, build.Vegetation);
        m_pointShadowMapShader.GetParameter("u_texture").SetValue(m_terrain.SubsystemAnimatedTextures.AnimatedBlocksTexture);
        m_pointShadowMapShader.GetParameter("u_samplerState").SetValue(SamplerState.PointClamp);
        m_pointShadowMapShader.GetParameter("u_alphaThreshold").SetValue(0.5f);
        m_pointShadowMapShader.GetParameter("u_shadowOrigin").SetValue(origin.XZ);
        m_pointShadowMapShader.GetParameter("u_lightPosition").SetValue(build.Light.Position - origin);
        m_pointShadowMapShader.GetParameter("u_lightRadius").SetValue(build.Light.Radius);
        m_pointShadowMapShader.GetParameter("u_shadowMatrix").SetValue(matrix);
        foreach (TerrainChunk chunk in build.Geometry.Chunks)
        {
            Vector3 margin = new Vector3(0.11f, 0f, 0.11f);
            BoundingBox local = new BoundingBox(chunk.BoundingBox.Min - origin - margin, chunk.BoundingBox.Max - origin + margin);
            if (frustum.Intersection(local)) DrawTerrainSubsets(m_pointShadowMapShader, chunk.Geometry, 63);
        }
        if (++build.Face != 6) return;

        // Include newly allocated/unloaded chunks as well as the resources checked before each face.
        CapturePointGeometry(build.Light, points.VerifyGeometry);
        if (!build.Geometry.Matches(points.VerifyGeometry))
        {
            slot.StaticValid = false;
            slot.StaticDirty = true;
            slot.PendingFrame = -1;
            points.StaticBuild = null;
            return;
        }
        int previous = slot.StaticPhysicalSlot;
        slot.StaticPhysicalSlot = build.PhysicalSlot;
        points.StaticFreeSlot = previous;
        ShadowGeometrySnapshot old = slot.Cached;
        slot.Cached = build.Geometry;
        slot.Pending = old;
        slot.PendingFrame = -1;
        slot.StaticDirty = false;
        slot.StaticValid = true;
        slot.VegetationSnapshot = build.Vegetation;
        slot.NextVegetationUpdate = m_vegetationFrame.SimulationTime + 3.0;
        slot.NextGeometryCheck = Time.RealTime + GetTerrainGeometryInterval();
        points.StaticBuild = null;
    }

    private void RenderPointDynamicFaces(Camera camera, PointShadowState points)
    {
        PointDynamicBuild build = points.DynamicBuild;
        if (build == null) return;
        PointShadowSlot slot = build.Slot;
        if (!slot.Selected || slot.Generation != build.Generation ||
            !m_modelShadowRenderer.PointSnapshotResourcesValid(build.Models))
        {
            slot.DynamicValid = false;
            slot.DynamicFaceMask = 0;
            points.DynamicBuild = null;
            build.Dispose();
            return;
        }
        // Three faces/frame supplies 180 dynamic faces/s at 60 FPS; static work has a separate one-face allowance.
        for (int allowance = 0; allowance < 3 && build.Face < 6; allowance++)
        {
            int face = build.Face++;
            Matrix matrix = PointFaceMatrix(build.Light, face, out Vector3 origin);
            ClearPointShadowFace(points, PointPhysicalTarget(points, true), build.PhysicalSlot, face, 64);
            if (m_modelShadowRenderer.DrawPointSnapshot(build.Models, matrix, origin.XZ, new BoundingFrustum(matrix),
                build.Light.Position, build.Light.Radius)) build.FaceMask |= 1 << face;
        }
        if (build.Face != 6) return;
        int previous = slot.DynamicPhysicalSlot;
        slot.DynamicPhysicalSlot = build.PhysicalSlot;
        points.DynamicFreeSlot = previous;
        slot.CompletedModels?.Dispose();
        slot.CompletedModels = build.Models;
        build.Models = null;
        slot.DynamicFaceMask = build.FaceMask;
        slot.DynamicValid = true;
        slot.NextDynamicCheck = build.StartedAt + PointDynamicInterval(camera, build.Light);
        points.DynamicBuild = null;
        build.Dispose();
    }

    private void AdvancePointBuilds(Camera camera, PointShadowState points)
    {
        RenderTarget2D previousTarget = Display.RenderTarget;
        Viewport previousViewport = Display.Viewport;
        Rectangle previousScissor = Display.ScissorRectangle;
        BlendState previousBlend = Display.BlendState;
        DepthStencilState previousDepth = Display.DepthStencilState;
        RasterizerState previousRasterizer = Display.RasterizerState;
        try
        {
            RenderPointDynamicFaces(camera, points);
            RenderPointStaticFace(points);
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
}
