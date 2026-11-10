using System.Collections.Generic;
using Engine.Graphics;
using Game;

namespace SuAPIShadows;

// Source: TerrainUpdater.cs:GenerateChunkVertices; TerrainRenderer.cs:SetupTerrainChunkGeometryVertexIndexBuffers.
// Inspect retained alpha markers only once after a stable GPU upload, not every cell or every frame.
internal sealed class VegetationGeometryCache
{
    private sealed class Entry
    {
        public VertexBuffer[] Buffers;
        public int Flags;
    }

    private readonly Dictionary<TerrainChunk, Entry> m_entries = new Dictionary<TerrainChunk, Entry>();

    // bit 1: air-driven vegetation; bit 2: water vegetation.
    public int Flags(TerrainChunk chunk)
    {
        TerrainChunkGeometry geometry = chunk.Geometry;
        if (geometry == null) return 0;
        if (m_entries.TryGetValue(chunk, out Entry entry) && Matches(entry, geometry)) return entry.Flags;
        // Old GPU geometry remains live while the CPU builds its replacement. Never label it using unfinished CPU data.
        if (chunk.State != TerrainChunkState.Valid || chunk.NewGeometryData) return entry != null ? entry.Flags : 0;
        lock (geometry)
        {
            if (chunk.State != TerrainChunkState.Valid || chunk.NewGeometryData) return entry != null ? entry.Flags : 0;
            int flags = 0;
            foreach (TerrainChunkSliceGeometry slice in geometry.Slices)
            {
                var vertices = slice.SubsetAlphaTest.Vertices;
                for (int i = 0; i < vertices.Count; i++)
                {
                    int code = 255 - vertices.Array[i].Color.A;
                    if (code < 1 || code > 160 || (code - 1) % 32 == 0) continue;
                    flags |= code > 128 ? 2 : 1;
                    if (flags == 3) break;
                }
                if (flags == 3) break;
            }
            var buffers = new VertexBuffer[geometry.Buffers.Count];
            for (int i = 0; i < buffers.Length; i++) buffers[i] = geometry.Buffers.Array[i].VertexBuffer;
            m_entries[chunk] = new Entry { Buffers = buffers, Flags = flags };
            return flags;
        }
    }

    private static bool Matches(Entry entry, TerrainChunkGeometry geometry)
    {
        if (entry.Buffers.Length != geometry.Buffers.Count) return false;
        for (int i = 0; i < entry.Buffers.Length; i++)
            if (!ReferenceEquals(entry.Buffers[i], geometry.Buffers.Array[i].VertexBuffer)) return false;
        return true;
    }

    public void Discard(TerrainChunk chunk) => m_entries.Remove(chunk);
    public void Clear() => m_entries.Clear();
}
