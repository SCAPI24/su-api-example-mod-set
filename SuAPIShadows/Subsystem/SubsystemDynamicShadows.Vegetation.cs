using Engine;
using Engine.Graphics;
using Game;
using SuAPIShadows.Foliage;

namespace SuAPIShadows;

public sealed partial class SubsystemDynamicShadows
{
    private VegetationMotionState m_vegetation;
    private VegetationMotionFrame m_vegetationFrame;
    private readonly MotionChunkBounds m_vegetationBounds = new MotionChunkBounds();
    private readonly VegetationGeometryCache m_vegetationGeometry = new VegetationGeometryCache();

    private VegetationMotionFrame CaptureVegetation(Camera camera)
    {
        ShadowsMod.CheckFoliage();
        m_vegetationFrame = m_vegetation.Capture(camera);
        return m_vegetationFrame;
    }

    // Source: VegetationMotionShaders.Shared; each six-face/static task receives one immutable frame.
    private static void ApplyVegetation(Shader shader, VegetationMotionFrame frame)
    {
        shader.GetParameter("u_vegetationEnabled").SetValue(frame != null && frame.Enabled ? 1f : 0f);
        if (frame == null) return;
        shader.GetParameter("u_vegetationWind").SetValue(frame.Wind);
        float nearRadius = 0f;
        for (int i = 0; i < 8; i++)
            if (frame.InfluencePositionRadius[i].W > 0f) { nearRadius = 22f; break; }
        shader.GetParameter("u_vegetationView").SetValue(new Vector4(frame.ViewPosition, nearRadius));
        shader.GetParameter("u_vegetationSources").SetValue(frame.InfluencePositionRadius, 8);
        shader.GetParameter("u_vegetationMotions").SetValue(frame.InfluenceDirectionStrength, 8);
        shader.GetParameter("u_vegetationAges").SetValue(frame.InfluenceTimingMedium, 8);
    }

    private static bool HasActiveVegetation(int flags, VegetationMotionFrame frame)
    {
        if (frame == null || !frame.Enabled) return false;
        if ((flags & 1) != 0) return true;
        if ((flags & 2) != 0)
            for (int i = 0; i < 8; i++)
                if (frame.InfluencePositionRadius[i].W > 0f && frame.InfluenceTimingMedium[i].Z > 0.5f)
                    return true;
        return false;
    }

    // Source: FoliageVertexEncoding.FindKelpRoot; root changes affect all generated slices in this column's chunk.
    private void InvalidateKelpColumn(int value, int previousValue, int x, int z)
    {
        if (Terrain.ExtractContents(value) == KelpBlock.Index || Terrain.ExtractContents(previousValue) == KelpBlock.Index)
            m_terrain.TerrainUpdater.DowngradeChunkNeighborhoodState(new Point2(x >> 4, z >> 4), 0,
                TerrainChunkState.InvalidVertices1, forceGeometryRegeneration: true);
    }
}
