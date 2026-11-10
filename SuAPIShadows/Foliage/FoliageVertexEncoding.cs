using System;
using Engine;
using Game;

namespace SuAPIShadows.Foliage;

internal enum FoliageKind { Grass = 1, SoftPlant = 2, Bush = 3, Leaves = 4, Water = 5 }

// Source: TerrainVertex.cs:Color; BlockGeometryGenerator.cs:GenerateCrossfaceVertices/GenerateCubeVertices.
internal static class FoliageVertexEncoding
{
    public static byte Encode(FoliageKind kind, float weight)
    {
        if ((int)kind < 1 || (int)kind > 5) throw new ArgumentOutOfRangeException(nameof(kind));
        int q = (int)MathUtils.Round(MathUtils.Saturate(weight) * 31f);
        return (byte)(255 - (1 + ((int)kind - 1) * 32 + q));
    }

    // Each generation invocation owns its own ranges: no shared generation-thread collection or GPU work.
    public struct Ranges
    {
        private TerrainGeometrySubset m_s0, m_s1, m_s2, m_s3, m_s4, m_s5, m_s6;
        private int m_i0, m_i1, m_i2, m_i3, m_i4, m_i5, m_i6;
        private int m_count;

        public Ranges(TerrainGeometry geometry) : this()
        {
            Add(geometry.SubsetAlphaTest);
            if (geometry.AlphaTestSubsetsByFace != null)
                foreach (TerrainGeometrySubset subset in geometry.AlphaTestSubsetsByFace) Add(subset);
        }

        private TerrainGeometrySubset Subset(int index) => index switch
        {
            0 => m_s0, 1 => m_s1, 2 => m_s2, 3 => m_s3,
            4 => m_s4, 5 => m_s5, 6 => m_s6,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        private int Start(int index) => index switch
        {
            0 => m_i0, 1 => m_i1, 2 => m_i2, 3 => m_i3,
            4 => m_i4, 5 => m_i5, 6 => m_i6,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

        private void Add(TerrainGeometrySubset subset)
        {
            if (subset == null) return;
            for (int i = 0; i < m_count; i++) if (ReferenceEquals(Subset(i), subset)) return;
            int start = subset.Vertices.Count;
            switch (m_count)
            {
                case 0: m_s0 = subset; m_i0 = start; break;
                case 1: m_s1 = subset; m_i1 = start; break;
                case 2: m_s2 = subset; m_i2 = start; break;
                case 3: m_s3 = subset; m_i3 = start; break;
                case 4: m_s4 = subset; m_i4 = start; break;
                case 5: m_s5 = subset; m_i5 = start; break;
                case 6: m_s6 = subset; m_i6 = start; break;
                default: throw new InvalidOperationException("[SuAPI] Foliage geometry exceeds one primary and six face subsets.");
            }
            m_count++;
        }

        public void Mark(FoliageKind kind, float rootY, float normalizationHeight, float strength = 1f)
        {
            if (!(normalizationHeight > 0f)) throw new ArgumentOutOfRangeException(nameof(normalizationHeight));
            for (int s = 0; s < m_count; s++)
            {
                var vertices = Subset(s).Vertices;
                for (int i = Start(s); i < vertices.Count; i++)
                {
                    TerrainVertex vertex = vertices.Array[i];
                    if (vertex.Color.A != 255)
                        throw new InvalidOperationException("[SuAPI] Foliage alpha marker conflict: native vertex is not opaque.");
                    float weight = kind == FoliageKind.Leaves ? 1f :
                        MathUtils.Saturate((vertex.Y - rootY) / normalizationHeight) * strength;
                    vertex.Color.A = Encode(kind, weight);
                    vertices.Array[i] = vertex;
                }
            }
        }
    }

    // Source: KelpBlock.GenerateTerrainVertices; Terrain.GetCellContents — one continuous root, not one root per voxel.
    // Fixed 8 m bend normalization avoids depending on the top: upper growth cannot invalidate unchanged lower segments.
    public static int FindKelpRoot(Terrain terrain, int x, int y, int z)
    {
        int root = y;
        while (root > 0 && terrain.GetCellContents(x, root - 1, z) == KelpBlock.Index) root--;
        return root;
    }
}
