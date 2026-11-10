using System;
using System.Collections.Generic;
using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;
using SuAPI;

namespace SuAPIShadows;

// Source: ComponentHumanModel.cs:DrawExtras; BasePrimitivesRenderer.cs:FlatBatches/TexturedBatches/FontBatches.
internal sealed class HeldItemShadowRenderer : IDisposable
{
    internal sealed class HeldItemState
    {
        public int Value { get; internal set; }
        public Matrix WorldMatrix { get; internal set; }
        public BoundingBox Bounds { get; internal set; }
        public long Revision { get; internal set; }
        internal readonly List<Part> Parts = new List<Part>();
    }

    internal sealed class Part : IDisposable
    {
        internal VertexBuffer Vertices;
        internal IndexBuffer Indices;
        internal VertexPositionColorTexture[] Source;
        internal ushort[] IndexSource;
        internal Texture2D Texture;
        internal SamplerState Sampler;
        internal bool AlphaTest;
        internal bool AlphaBlend;
        internal PrimitiveType Primitive;
        public void Dispose() { Vertices?.Dispose(); Indices?.Dispose(); }
    }

    private readonly Dictionary<ComponentHumanModel, HeldItemState> m_states = new Dictionary<ComponentHumanModel, HeldItemState>();
    private readonly PrimitivesRenderer3D m_capture = new PrimitivesRenderer3D();
    private HeldItemDepthShader m_sunShader;
    private HeldItemDepthShader m_pointShader;

    public HeldItemShadowRenderer()
    {
        Display.DeviceReset += RestoreBuffers;
    }

    // Source: VertexBuffer.cs:HandleDeviceReset; restored GPU buffers are empty until their source data is uploaded.
    private void RestoreBuffers()
    {
        foreach (HeldItemState state in m_states.Values)
        {
            foreach (Part part in state.Parts)
            {
                part.Vertices.SetData(part.Source, 0, part.Source.Length);
                part.Indices.SetData(part.IndexSource, 0, part.IndexSource.Length);
            }
            state.Revision++;
        }
    }

    // Called only by the owning model Prepare batch (at most 12 Hz); no capture work occurs per light/face.
    public HeldItemState Capture(ComponentHumanModel human, Matrix[] worldBones, Camera camera)
    {
        if (human == null) throw new ArgumentNullException(nameof(human));
        if (worldBones == null) throw new ArgumentNullException(nameof(worldBones));
        if (camera == null) throw new ArgumentNullException(nameof(camera));
        ComponentCreature creature = human.Entity.FindComponent<ComponentCreature>(true);
        ComponentMiner miner = human.Entity.FindComponent<ComponentMiner>();
        int value = miner != null && creature.ComponentHealth.Health > 0f ? miner.ActiveBlockValue : 0;
        if (value == 0) { Remove(human); return null; }
        Block block = BlocksManager.Blocks[Terrain.ExtractContents(value)];
        ModelBone hand = ModManager.Instance.ModParentField.GetParentField<ModelBone>(human, "m_hand2Bone");
        if (hand.Index < 0 || hand.Index >= worldBones.Length)
            throw new InvalidOperationException("[SuAPI] Held-item hand bone is outside the prepared world-bone array.");
        Matrix handMatrix = worldBones[hand.Index];
        handMatrix.Right = Vector3.Normalize(handMatrix.Right);
        handMatrix.Up = Vector3.Normalize(handMatrix.Up);
        handMatrix.Forward = Vector3.Normalize(handMatrix.Forward);
        Vector3 rotation = ModManager.Instance.ModParentField.GetParentField<Vector3>(human, "m_inHandItemRotation");
        Vector3 offset = ModManager.Instance.ModParentField.GetParentField<Vector3>(human, "m_inHandItemOffset");
        Matrix world = Matrix.CreateRotationY(MathUtils.DegToRad(block.InHandRotation.Y) + rotation.Y) *
            Matrix.CreateRotationZ(MathUtils.DegToRad(block.InHandRotation.Z) + rotation.Z) *
            Matrix.CreateRotationX(MathUtils.DegToRad(block.InHandRotation.X) + rotation.X) *
            Matrix.CreateTranslation(block.InHandOffset + offset) *
            Matrix.CreateTranslation(new Vector3(0.05f, 0.05f, -0.56f) * (creature.ComponentBody.BoxSize.Y / 1.77f)) * handMatrix;
        SubsystemTerrain terrain = human.Project.FindSubsystem<SubsystemTerrain>(true);
        ComponentPlayer player = human.Entity.FindComponent<ComponentPlayer>();
        int x = Terrain.ToCell(world.Translation.X), y = Terrain.ToCell(world.Translation.Y), z = Terrain.ToCell(world.Translation.Z);
        var environment = new DrawBlockEnvironmentData
        {
            DrawBlockMode = DrawBlockMode.ThirdPerson,
            InWorldMatrix = world,
            Humidity = terrain.Terrain.GetSeasonalHumidity(x, z),
            Temperature = terrain.Terrain.GetSeasonalTemperature(x, z) + SubsystemWeather.GetTemperatureAdjustmentAtHeight(y),
            Light = terrain.Terrain.GetCellLight(x, y, z),
            EnvironmentTemperature = player != null ? player.ComponentVitalStats.EnvironmentTemperature : null,
            BillboardDirection = -Vector3.UnitZ,
            SubsystemTerrain = terrain
        };
        m_capture.Clear();
        Matrix drawMatrix = world * camera.ViewMatrix;
        block.DrawBlock(m_capture, value, Color.White, block.InHandScale, ref drawMatrix, environment);
        // Native DrawBlock batches are camera-space. Cache item-local geometry once; every light uses the same buffers.
        Matrix toLocal = camera.InvertedViewMatrix * Matrix.Invert(world);
        var parts = new List<Part>();
        try
        {
            foreach (TexturedBatch3D batch in m_capture.TexturedBatches)
                CapturePart(parts, batch.TriangleVertices, batch.TriangleIndices, toLocal,
                    batch.Texture, batch.SamplerState, batch.UseAlphaTest, batch.BlendState == BlendState.AlphaBlend, PrimitiveType.TriangleList);
            foreach (FlatBatch3D batch in m_capture.FlatBatches)
            {
                CaptureFlat(parts, batch.TriangleVertices, batch.TriangleIndices, toLocal, batch.BlendState == BlendState.AlphaBlend, PrimitiveType.TriangleList);
                CaptureFlat(parts, batch.LineVertices, batch.LineIndices, toLocal, batch.BlendState == BlendState.AlphaBlend, PrimitiveType.LineList);
            }
            foreach (FontBatch3D batch in m_capture.FontBatches)
                CapturePart(parts, batch.TriangleVertices, batch.TriangleIndices, toLocal,
                    batch.Font.Texture, batch.SamplerState, false, batch.BlendState == BlendState.AlphaBlend, PrimitiveType.TriangleList);
        }
        catch { foreach (Part part in parts) part.Dispose(); throw; }
        finally { m_capture.Clear(); }
        if (!m_states.TryGetValue(human, out HeldItemState state))
        {
            state = new HeldItemState();
            m_states.Add(human, state);
        }
        bool geometryChanged = !SameGeometry(state.Parts, parts);
        bool changed = state.Value != value || !state.WorldMatrix.Equals(world) || geometryChanged;
        if (geometryChanged)
        {
            try { foreach (Part part in parts) Upload(part); }
            catch { foreach (Part part in parts) part.Dispose(); throw; }
            foreach (Part old in state.Parts) old.Dispose();
            state.Parts.Clear();
            state.Parts.AddRange(parts);
        }
        else foreach (Part part in parts) part.Dispose();
        if (changed) state.Revision++;
        state.Value = value;
        state.WorldMatrix = world;
        bool haveBounds = false;
        BoundingBox bounds = default(BoundingBox);
        foreach (Part part in state.Parts)
            foreach (VertexPositionColorTexture vertex in part.Source)
            {
                Vector3 position = Vector3.Transform(vertex.Position, world);
                bounds = haveBounds ? BoundingBox.Union(bounds, position) : new BoundingBox(position, position);
                haveBounds = true;
            }
        // An empty DrawBlock result has no shadow geometry; never replace it with a fabricated box.
        if (!haveBounds) { Remove(human); return null; }
        state.Bounds = bounds;
        return state;
    }

    // Source: BaseTexturedBatch.FlushWithDeviceState — preserve UseAlphaTest and its zero threshold.
    private static void CapturePart(List<Part> parts, DynamicArray<VertexPositionColorTexture> vertices,
        DynamicArray<ushort> indices, Matrix toLocal, Texture2D texture, SamplerState sampler,
        bool alphaTest, bool alphaBlend, PrimitiveType primitive)
    {
        if (indices.Count == 0) return;
        var source = new VertexPositionColorTexture[vertices.Count];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = vertices.Array[i];
            source[i].Position = Vector3.Transform(source[i].Position, toLocal);
        }
        var indexSource = new ushort[indices.Count];
        Array.Copy(indices.Array, indexSource, indexSource.Length);
        var part = new Part { Source = source, IndexSource = indexSource, Texture = texture,
            Sampler = sampler, AlphaTest = alphaTest, AlphaBlend = alphaBlend, Primitive = primitive };
        parts.Add(part);
    }

    private static void Upload(Part part)
    {
        part.Vertices = new VertexBuffer(VertexPositionColorTexture.VertexDeclaration, part.Source.Length);
        part.Indices = new IndexBuffer(IndexFormat.SixteenBits, part.IndexSource.Length);
        part.Vertices.SetData(part.Source, 0, part.Source.Length);
        part.Indices.SetData(part.IndexSource, 0, part.IndexSource.Length);
    }

    private static void CaptureFlat(List<Part> parts, DynamicArray<VertexPositionColor> vertices,
        DynamicArray<ushort> indices, Matrix toLocal, bool alphaBlend, PrimitiveType primitive)
    {
        if (indices.Count == 0) return;
        var converted = new DynamicArray<VertexPositionColorTexture>();
        for (int i = 0; i < vertices.Count; i++)
            converted.Add(new VertexPositionColorTexture(vertices.Array[i].Position, vertices.Array[i].Color, Vector2.Zero));
        CapturePart(parts, converted, indices, toLocal, null, null, false, alphaBlend, primitive);
    }

    private static bool SameGeometry(List<Part> previous, List<Part> current)
    {
        if (previous.Count != current.Count) return false;
        for (int i = 0; i < previous.Count; i++)
        {
            Part a = previous[i], b = current[i];
            if (!ReferenceEquals(a.Texture, b.Texture) || a.Sampler != b.Sampler || a.AlphaTest != b.AlphaTest ||
                a.AlphaBlend != b.AlphaBlend || a.Primitive != b.Primitive ||
                a.Source.Length != b.Source.Length || a.IndexSource.Length != b.IndexSource.Length) return false;
            // Source: ComponentHumanModel.DrawExtras — cancelling view/world transforms introduces harmless round-off.
            // Depth ignores RGB lighting; preserve only positional tolerance, exact UV and vertex alpha.
            for (int j = 0; j < a.Source.Length; j++)
            {
                VertexPositionColorTexture av = a.Source[j], bv = b.Source[j];
                if (Vector3.DistanceSquared(av.Position, bv.Position) > 0.00000001f ||
                    !av.TexCoord.Equals(bv.TexCoord) || av.Color.A != bv.Color.A) return false;
            }
            for (int j = 0; j < a.IndexSource.Length; j++) if (a.IndexSource[j] != b.IndexSource[j]) return false;
        }
        return true;
    }

    public void Draw(HeldItemState state, Matrix shadowMatrix, Vector2 origin, Vector3? lightPosition, float radius, float opacity)
    {
        if (state == null || !(opacity > 0f)) return;
        if (lightPosition.HasValue && !(radius > 0f)) throw new ArgumentOutOfRangeException(nameof(radius));
        if (m_sunShader == null) m_sunShader = new HeldItemDepthShader(false);
        if (m_pointShader == null) m_pointShader = new HeldItemDepthShader(true);
        HeldItemDepthShader shader = lightPosition.HasValue ? m_pointShader : m_sunShader;
        Vector3 origin3 = new Vector3(origin.X, 0f, origin.Y);
        RasterizerState oldRaster = Display.RasterizerState;
        DepthStencilState oldDepth = Display.DepthStencilState;
        BlendState oldBlend = Display.BlendState;
        try
        {
            Display.RasterizerState = RasterizerState.CullNoneScissor;
            Display.DepthStencilState = DepthStencilState.Default;
            Display.BlendState = BlendState.Opaque;
            foreach (Part part in state.Parts)
            {
                shader.Set(state.WorldMatrix * Matrix.CreateTranslation(-origin3), shadowMatrix,
                    lightPosition.HasValue ? lightPosition.Value - origin3 : Vector3.Zero, radius, opacity,
                    part.Texture, part.Sampler, part.AlphaTest, part.AlphaBlend);
                Display.DrawIndexed(part.Primitive, shader, part.Vertices, part.Indices, 0, part.IndexSource.Length);
            }
        }
        finally { Display.RasterizerState = oldRaster; Display.DepthStencilState = oldDepth; Display.BlendState = oldBlend; }
    }

    public void Remove(ComponentHumanModel human)
    {
        if (!m_states.TryGetValue(human, out HeldItemState state)) return;
        foreach (Part part in state.Parts) part.Dispose();
        state.Parts.Clear();
        m_states.Remove(human);
    }

    public void ReleaseEntity(Entity entity)
    {
        foreach (ComponentHumanModel human in entity.FindComponents<ComponentHumanModel>()) Remove(human);
    }

    public void Dispose()
    {
        Display.DeviceReset -= RestoreBuffers;
        foreach (HeldItemState state in m_states.Values) foreach (Part part in state.Parts) part.Dispose();
        m_states.Clear();
        m_capture.Clear();
        m_sunShader?.Dispose();
        m_pointShader?.Dispose();
        m_sunShader = null;
        m_pointShader = null;
    }
}
