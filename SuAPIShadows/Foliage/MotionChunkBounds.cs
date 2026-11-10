using System;
using System.Collections.Generic;
using Engine;
using Game;

namespace SuAPIShadows;

// Source: TerrainRenderer.cs:PrepareForDrawing; the native view-frustum test reads
// TerrainChunk.BoundingBox before drawing any of its vegetation vertices.
internal sealed class MotionChunkBounds : IDisposable
{
    private struct Entry
    {
        public TerrainChunk Chunk;
        public BoundingBox Original;
        public BoundingBox Expanded;
    }

    private readonly List<Entry> m_entries = new List<Entry>();
    private bool m_active;

    public void Begin(SubsystemTerrain terrain)
    {
        if (terrain == null) throw new ArgumentNullException(nameof(terrain));
        if (m_active) throw new InvalidOperationException("[SuAPIShadows] Vegetation bounds scope was not ended before the next Begin.");
        m_active = true;
        try
        {
            Vector3 margin = new Vector3(0.11f, 0f, 0.11f);
            foreach (TerrainChunk chunk in terrain.Terrain.AllocatedChunks)
            {
                if (chunk.Geometry == null) continue;
                BoundingBox original = chunk.BoundingBox;
                BoundingBox expanded = new BoundingBox(original.Min - margin, original.Max + margin);
                m_entries.Add(new Entry { Chunk = chunk, Original = original, Expanded = expanded });
                chunk.BoundingBox = expanded;
            }
        }
        catch
        {
            End();
            throw;
        }
    }

    public void End()
    {
        if (!m_active) return;
        bool conflict = false;
        try
        {
            foreach (Entry entry in m_entries)
            {
                // Never overwrite a different owner's later write. Restore all remaining
                // owned entries before reporting the conflict to the drawing error boundary.
                if (entry.Chunk.BoundingBox != entry.Expanded)
                {
                    conflict = true;
                    continue;
                }
                entry.Chunk.BoundingBox = entry.Original;
            }
        }
        finally
        {
            m_entries.Clear();
            m_active = false;
        }
        if (conflict) throw new InvalidOperationException("[SuAPIShadows] Terrain chunk bounds changed inside the vegetation scope; another owner's bounds were not overwritten.");
    }

    public void Dispose() => End();
}
