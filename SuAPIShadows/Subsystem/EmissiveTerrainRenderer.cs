using System;
using System.Collections.Generic;
using System.Reflection;
using Engine;
using Engine.Graphics;
using Game;

namespace SuAPIShadows;

// Source: TorchBlock, LightbulbBlock, ChristmasTreeBlock, JackOLanternBlock.GenerateTerrainVertices.
// Restore only real emissive triangles after terrain rendering. No block replacement or framework hook.
internal sealed class EmissiveTerrainRenderer : IDisposable
{
    private sealed class MeshSet
    {
        public BlockMesh[] Variants;
        public Type OriginalType;
    }

    private sealed class Batch : IDisposable
    {
        public VertexBuffer Vertices;
        public IndexBuffer Indices;
        public void Dispose() { Vertices?.Dispose(); Indices?.Dispose(); }
    }

    private sealed class ChunkCache : IDisposable
    {
        public VertexBuffer[] SourceBuffers;
        public Batch Opaque;
        public Batch AlphaTested;
        public void Dispose() { Opaque?.Dispose(); AlphaTested?.Dispose(); }
    }

    private readonly Dictionary<Block, MeshSet> m_meshes = new();
    private readonly Dictionary<TerrainChunk, ChunkCache> m_chunks = new();
    private readonly Dictionary<Point2, HashSet<Point3>> m_cells = new();
    private readonly HashSet<TerrainChunk> m_allocated = new();
    private readonly List<TerrainChunk> m_removed = new();
    private readonly Shader m_opaque = new(TerrainShadowShaders.TerrainVertex, Pixel);
    private readonly Shader m_alpha = new(TerrainShadowShaders.TerrainVertex, Pixel, new ShaderMacro("ALPHATESTED"));
    private readonly DepthStencilState m_depth = new()
    {
        DepthBufferTestEnable = true,
        DepthBufferWriteEnable = false,
        DepthBufferFunction = CompareFunction.Equal
    };
    private readonly SamplerState m_point = new()
    {
        AddressModeU = TextureAddressMode.Clamp, AddressModeV = TextureAddressMode.Clamp,
        FilterMode = TextureFilterMode.Point, MaxLod = 0f
    };
    private readonly SamplerState m_mips = new()
    {
        AddressModeU = TextureAddressMode.Clamp, AddressModeV = TextureAddressMode.Clamp,
        FilterMode = TextureFilterMode.PointMipLinear, MaxLod = 4f
    };

    public EmissiveTerrainRenderer()
    {
        // Reflection runs once per world renderer, never per cell or draw call.
        foreach (Block block in BlocksManager.Blocks)
        {
            if (block == null || m_meshes.ContainsKey(block)) continue;
            if (block is TorchBlock) Capture(block, typeof(TorchBlock), "m_blockMeshesByVariant");
            else if (block is LightbulbBlock) Capture(block, typeof(LightbulbBlock), "m_bulbBlockMeshesLit");
            else if (block is ChristmasTreeBlock) Capture(block, typeof(ChristmasTreeBlock), "m_litDecorationsBlockMesh");
            else if (block is JackOLanternBlock) Capture(block, typeof(JackOLanternBlock), "m_blockMeshesByData");
        }
        Display.DeviceReset += DeviceReset;
    }

    private void Capture(Block block, Type owner, string name)
    {
        MethodInfo generation = block.GetType().GetMethod("GenerateTerrainVertices");
        if (generation == null || generation.DeclaringType != owner)
            throw new InvalidOperationException("[SuAPIShadows] Cannot reconstruct overridden emissive terrain geometry: " + block.GetType().FullName);
        FieldInfo field = owner.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        object value = field?.GetValue(block);
        BlockMesh[] variants = value is BlockMesh mesh ? new[] { mesh } : value as BlockMesh[];
        if (variants == null || variants.Length == 0 || Array.Exists(variants, m => m == null))
            throw new InvalidOperationException("[SuAPIShadows] Missing initialized emissive mesh contract: " + owner.Name + "." + name);
        m_meshes.Add(block, new MeshSet { Variants = variants, OriginalType = owner });
    }

    // Source: SubsystemBlockBehavior.OnBlockGenerated/Added/Modified/Removed.
    // Track model-bearing cells even while switched off; CPU topology is consumed only
    // after the matching new terrain GPU buffers have been uploaded.
    public void Observe(int value, int x, int y, int z)
    {
        Point2 key = new(x >> 4, z >> 4);
        Point3 point = new(x, y, z);
        Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
        if (m_meshes.ContainsKey(block))
        {
            if (!m_cells.TryGetValue(key, out HashSet<Point3> cells))
            {
                cells = new HashSet<Point3>();
                m_cells.Add(key, cells);
            }
            cells.Add(point);
        }
        else if (m_cells.TryGetValue(key, out HashSet<Point3> cells))
        {
            cells.Remove(point);
            if (cells.Count == 0) m_cells.Remove(key);
        }
        // Keep the old pass while old GPU terrain is displayed. Matches() invalidates
        // its geometry together with the terrain upload, rather than ahead of it.
    }

    public void DiscardChunk(TerrainChunk chunk)
    {
        m_cells.Remove(new Point2(chunk.Origin.X >> 4, chunk.Origin.Y >> 4));
        if (m_chunks.TryGetValue(chunk, out ChunkCache cache))
        {
            cache.Dispose();
            m_chunks.Remove(chunk);
        }
    }

    // Call after terrain opaque/alpha-tested drawing; Equal depth prevents drawing through terrain/models.
    // Source: TerrainRenderer.PrepareForDrawing/DrawOpaque/DrawAlphaTested.
    public void Draw(Camera camera, SubsystemTerrain terrain, SubsystemSky sky)
    {
        TerrainChunk[] chunks = terrain.Terrain.AllocatedChunks;
        m_allocated.Clear();
        foreach (TerrainChunk chunk in chunks) m_allocated.Add(chunk);
        m_removed.Clear();
        foreach (TerrainChunk chunk in m_chunks.Keys)
            if (!m_allocated.Contains(chunk)) m_removed.Add(chunk);
        foreach (TerrainChunk chunk in m_removed) { m_chunks[chunk].Dispose(); m_chunks.Remove(chunk); }

        Vector3 view = camera.InvertedViewMatrix.Translation;
        Vector3 origin = new(MathUtils.Floor(view.X), 0f, MathUtils.Floor(view.Z));
        Matrix projection = Matrix.CreateTranslation(origin - view) * camera.ViewMatrix.OrientationMatrix * camera.ProjectionMatrix;
        BlendState oldBlend = Display.BlendState;
        DepthStencilState oldDepth = Display.DepthStencilState;
        RasterizerState oldRasterizer = Display.RasterizerState;
        try
        {
            Display.BlendState = BlendState.Opaque;
            Display.DepthStencilState = m_depth;
            Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;
            SetParameters(m_opaque, camera, terrain, sky, view, origin, projection);
            SetParameters(m_alpha, camera, terrain, sky, view, origin, projection);
            foreach (TerrainChunk chunk in chunks)
            {
                if (chunk.Geometry.Buffers.Count == 0 ||
                    Vector2.DistanceSquared(view.XZ, chunk.Center) > sky.VisibilityRange * sky.VisibilityRange ||
                    !camera.ViewFrustum.Intersection(chunk.BoundingBox)) continue;
                bool hasMatchingCache = m_chunks.TryGetValue(chunk, out ChunkCache cache) && Matches(cache, chunk);
                if (!hasMatchingCache)
                {
                    // Source: TerrainRenderer.PrepareForDrawing. Keep drawing a matching cached
                    // emission pass while old GPU terrain remains displayed during regeneration;
                    // only rebuilding needs stable CPU cells and the newly uploaded GPU buffers.
                    if (chunk.State != TerrainChunkState.Valid || chunk.NewGeometryData) continue;
                    ChunkCache replacement = Build(chunk, terrain);
                    cache?.Dispose();
                    m_chunks[chunk] = cache = replacement;
                }
                float end = MathUtils.Min(chunk.HazeEnds[camera.GameWidget.GameWidgetIndex], sky.ViewHazeStart + 1f / sky.ViewHazeDensity);
                float start = MathUtils.Min(sky.ViewHazeStart, end - 1f);
                Vector2 haze = new(start, 1f / (end - start));
                DrawBatch(m_opaque, cache.Opaque, haze);
                DrawBatch(m_alpha, cache.AlphaTested, haze);
            }
        }
        finally
        {
            Display.BlendState = oldBlend;
            Display.DepthStencilState = oldDepth;
            Display.RasterizerState = oldRasterizer;
        }
    }

    private void SetParameters(Shader shader, Camera camera, SubsystemTerrain terrain, SubsystemSky sky,
        Vector3 view, Vector3 origin, Matrix projection)
    {
        shader.GetParameter("u_origin").SetValue(origin.XZ);
        shader.GetParameter("u_viewProjectionMatrix").SetValue(projection);
        shader.GetParameter("u_viewPosition").SetValue(view);
        shader.GetParameter("u_texture").SetValue(terrain.SubsystemAnimatedTextures.AnimatedBlocksTexture);
        shader.GetParameter("u_samplerState").SetValue(SettingsManager.TerrainMipmapsEnabled ? m_mips : m_point);
        shader.GetParameter("u_fogYMultiplier").SetValue(sky.VisibilityRangeYMultiplier);
        shader.GetParameter("u_fogColor").SetValue(new Vector3(sky.ViewFogColor));
        shader.GetParameter("u_fogBottomTopDensity").SetValue(new Vector3(sky.ViewFogBottom, sky.ViewFogTop, sky.ViewFogDensity));
    }

    private static void DrawBatch(Shader shader, Batch batch, Vector2 haze)
    {
        if (batch == null) return;
        shader.GetParameter("u_hazeStartDensity").SetValue(haze);
        Display.DrawIndexed(PrimitiveType.TriangleList, shader, batch.Vertices, batch.Indices, 0, batch.Indices.IndicesCount);
    }

    private static bool Matches(ChunkCache cache, TerrainChunk chunk)
    {
        if (cache.SourceBuffers.Length != chunk.Geometry.Buffers.Count) return false;
        for (int i = 0; i < cache.SourceBuffers.Length; i++)
            if (!ReferenceEquals(cache.SourceBuffers[i], chunk.Geometry.Buffers.Array[i].VertexBuffer)) return false;
        return true;
    }

    private ChunkCache Build(TerrainChunk chunk, SubsystemTerrain terrain)
    {
        var opaque = new List<TerrainVertex>();
        var alpha = new List<TerrainVertex>();
        // No terrain scan: notifications track the small set of model-bearing cells.
        Point2 key = new(chunk.Origin.X >> 4, chunk.Origin.Y >> 4);
        if (m_cells.TryGetValue(key, out HashSet<Point3> cells))
        foreach (Point3 point in cells)
        {
            // Source: TerrainUpdater.GenerateChunkVertices limits generated cells to 1..254.
            if (point.Y <= 0 || point.Y >= 255) continue;
            int value = terrain.Terrain.GetCellValue(point.X, point.Y, point.Z);
            Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
            if (!m_meshes.TryGetValue(block, out MeshSet set)) continue;
            int data = Terrain.ExtractData(value);
            int variant = 0;
            Color color = Color.White;
            if (set.OriginalType == typeof(TorchBlock) || set.OriginalType == typeof(JackOLanternBlock)) variant = data;
            else if (set.OriginalType == typeof(LightbulbBlock))
            {
                int intensity = LightbulbBlock.GetLightIntensity(data);
                if (intensity <= 0) continue;
                variant = LightbulbBlock.GetMountingFace(data);
                color = new Color((byte)(195 + intensity * 4), (byte)(180 + intensity * 5), (byte)(165 + intensity * 6));
            }
            else if (!ChristmasTreeBlock.GetLightState(data)) continue;
            // Source: native GenerateTerrainVertices emits nothing for out-of-range variants.
            if ((uint)variant >= (uint)set.Variants.Length) continue;
            Append(set.Variants[variant], color, point.X, point.Y, point.Z,
                terrain, set.OriginalType == typeof(JackOLanternBlock) ? alpha : opaque);
        }
        ChunkCache result = new() { SourceBuffers = new VertexBuffer[chunk.Geometry.Buffers.Count] };
        try
        {
            result.Opaque = Upload(opaque);
            result.AlphaTested = Upload(alpha);
            for (int i = 0; i < result.SourceBuffers.Length; i++) result.SourceBuffers[i] = chunk.Geometry.Buffers.Array[i].VertexBuffer;
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    // Source: BlockGeometryGenerator.GenerateMeshVertices/SetupVertex; preserve UV quantization,
    // original winding, per-mesh tint, block translation and neighbor-side visibility exactly.
    private static void Append(BlockMesh mesh, Color tint, int x, int y, int z,
        SubsystemTerrain terrain, List<TerrainVertex> output)
    {
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            BlockMeshVertex a = mesh.Vertices.Array[mesh.Indices.Array[i]];
            BlockMeshVertex b = mesh.Vertices.Array[mesh.Indices.Array[i + 1]];
            BlockMeshVertex c = mesh.Vertices.Array[mesh.Indices.Array[i + 2]];
            if (!a.IsEmissive && !b.IsEmissive && !c.IsEmissive) continue;
            if (!(a.IsEmissive && b.IsEmissive && c.IsEmissive))
                throw new InvalidOperationException("[SuAPIShadows] Mixed emissive/non-emissive vertices in a native light triangle.");
            int side = mesh.Sides == null ? -1 : mesh.Sides.Array[i / 3];
            if (side >= 0)
            {
                Point3 offset = CellFace.FaceToPoint3(side);
                int neighbor = terrain.Terrain.GetCellValue(x + offset.X, y + offset.Y, z + offset.Z);
                if (!BlocksManager.Blocks[Terrain.ExtractContents(neighbor)].IsFaceTransparent(terrain, CellFace.OppositeFace(side), neighbor)) continue;
            }
            Add(a); Add(b); Add(c);
        }
        void Add(BlockMeshVertex source)
        {
            Color color = new((byte)(tint.R * source.Color.R / 255),
                (byte)(tint.G * source.Color.G / 255), (byte)(tint.B * source.Color.B / 255));
            TerrainVertex vertex = default;
            BlockGeometryGenerator.SetupVertex(x + source.Position.X, y + source.Position.Y, z + source.Position.Z,
                color, source.TextureCoordinates.X, source.TextureCoordinates.Y, ref vertex);
            output.Add(vertex);
        }
    }

    private static Batch Upload(List<TerrainVertex> vertices)
    {
        if (vertices.Count == 0) return null;
        Batch batch = new();
        try
        {
            batch.Vertices = new VertexBuffer(TerrainVertex.VertexDeclaration, vertices.Count);
            batch.Vertices.SetData(vertices.ToArray(), 0, vertices.Count);
            // Large decorated chunks must not wrap 16-bit indices.
            uint[] indices = new uint[vertices.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = (uint)i;
            batch.Indices = new IndexBuffer(IndexFormat.ThirtyTwoBits, indices.Length);
            batch.Indices.SetData(indices, 0, indices.Length);
            return batch;
        }
        catch { batch.Dispose(); throw; }
    }

    private void DeviceReset()
    {
        foreach (ChunkCache cache in m_chunks.Values) cache.Dispose();
        m_chunks.Clear();
    }

    public void Dispose()
    {
        Display.DeviceReset -= DeviceReset;
        DeviceReset();
        m_opaque.Dispose();
        m_alpha.Dispose();
    }

    // Source: TerrainShadowShaders.TerrainPixel and TerrainRenderer.DrawAlphaTested.
    // Opaque restoration (not additive glow); normal fog and alpha cutout remain intact.
    private const string Pixel = """
// <Sampler Name='u_samplerState' Texture='u_texture' />
#ifdef GL_ES
precision highp float;
#endif
uniform sampler2D u_texture;
uniform vec3 u_fogColor;
varying vec4 v_color;
varying vec2 v_texcoord;
varying float v_fog;
void main()
{
    vec4 result = v_color * texture2D(u_texture, v_texcoord);
#ifdef ALPHATESTED
    if (result.a <= 0.5) discard;
#endif
    result.rgb = mix(result.rgb, u_fogColor * v_color.a, v_fog);
    gl_FragColor = result;
}
""";
}
