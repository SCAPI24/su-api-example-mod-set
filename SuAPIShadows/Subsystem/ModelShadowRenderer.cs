using System;
using System.Collections;
using System.Collections.Generic;
using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;
using SuAPI;

namespace SuAPIShadows;

// Source: SubsystemModelsRenderer.cs:PrepareModel/DrawInstancedModels; ComponentModel.cs:CalculateAbsoluteBonesTransforms.
internal sealed class ModelShadowRenderer : IDisposable
{
    internal sealed class Caster
    {
        public ComponentModel Component;
        public Matrix[] WorldBones;
        public Matrix[] SavedBones;
        public Model Model;
        public Texture2D Texture;
        public float Opacity;
        public BoundingBox Bounds;
        public bool Active;
        public bool Seen;
        public int[] MeshOrders;
        public HeldItemShadowRenderer.HeldItemState HeldItem;
        public int HeldValue;
        public Matrix HeldWorldMatrix;
        public long HeldRevision;
        public long PoseRevision;
        public long Sequence;
        public AppearanceKey Appearance;
    }

    // Source: ComponentClothing.cs:SetClothes/UpdateRenderTargets; its texture can be redrawn without changing identity.
    internal sealed class AppearanceKey
    {
        internal ComponentClothing Clothing;
        internal string SkinName;
        internal bool TexturesEnabled;
        internal int[] WearValues;
    }

    internal sealed class FrozenCaster
    {
        internal Caster Identity;
        internal long PoseRevision;
        internal Matrix[] WorldBones;
        internal int[] MeshOrders;
        internal Model Model;
        internal Texture2D Texture;
        internal float Opacity;
        internal BoundingBox Bounds;
        internal InstancedModelData Geometry;
        internal HeldItemShadowRenderer.HeldItemState HeldItem;
        internal AppearanceKey Appearance;
    }

    // Source: ModelShadowRenderer.Prepare; each six-face job consumes one completed, independently owned pose batch.
    internal sealed class PointSnapshot : IDisposable
    {
        internal readonly ModelShadowRenderer Owner;
        internal readonly Camera Camera;
        internal readonly Vector3 LightPosition;
        internal readonly float LightRadius;
        internal readonly long ResourceEpoch;
        internal readonly FrozenCaster[] Casters;
        internal bool Disposed;
        public bool IsEmpty => Casters.Length == 0;

        internal PointSnapshot(ModelShadowRenderer owner, Camera camera, Vector3 position, float radius,
            long resourceEpoch, FrozenCaster[] casters)
        {
            Owner = owner; Camera = camera; LightPosition = position; LightRadius = radius;
            ResourceEpoch = resourceEpoch; Casters = casters;
        }

        public void Dispose()
        {
            if (Disposed) return;
            VerifyRenderingThread();
            Disposed = true;
            foreach (FrozenCaster caster in Casters) caster.HeldItem?.Dispose();
            Owner.m_pointSnapshots.Remove(this);
        }
    }

    private readonly Dictionary<Entity, List<Caster>> m_entities = new Dictionary<Entity, List<Caster>>();
    private readonly List<Caster> m_candidates = new List<Caster>();
    private readonly List<Caster> m_prepared = new List<Caster>();
    private readonly Dictionary<int, ModelDepthShader[]> m_shaders = new Dictionary<int, ModelDepthShader[]>();
    private readonly HeldItemShadowRenderer m_heldItems = new HeldItemShadowRenderer();
    private readonly HashSet<PointSnapshot> m_pointSnapshots = new HashSet<PointSnapshot>();
    private Camera m_camera;
    private Project m_project;
    private SubsystemModelsRenderer m_nativeRenderer;
    private IDictionary m_nativeModels;
    private bool m_entitiesChanged = true;
    private float m_preparedRange;
    private double m_nextPrepareTime;
    private long m_resourceEpoch;
    private long m_nextCasterSequence;
    private bool m_disposed;
    public long Revision { get; private set; }

    public ModelShadowRenderer()
    {
        Display.DeviceReset += InvalidatePointSnapshots;
    }

    // Source: InstancedModelsManager.cs:static constructor; reset disposes its cached buffers before the next frame.
    private void InvalidatePointSnapshots()
    {
        m_resourceEpoch++;
        m_entitiesChanged = true;
        m_nextPrepareTime = 0.0;
    }

    // Source: SubsystemModelsRenderer.cs:OnEntityAdded — retain every originally eligible model, including clothing.
    public void AddEntity(Entity entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        if (m_entities.ContainsKey(entity)) return;
        var casters = new List<Caster>();
        foreach (ComponentModel model in entity.FindComponents<ComponentModel>())
            if (model.CastsShadow) casters.Add(new Caster { Component = model, Sequence = checked(++m_nextCasterSequence) });
        if (casters.Count == 0) return;
        m_entities.Add(entity, casters);
        m_entitiesChanged = true;
        Revision++;
    }

    public void RemoveEntity(Entity entity)
    {
        if (!m_entities.TryGetValue(entity, out List<Caster> removed)) return;
        m_entities.Remove(entity);
        m_heldItems.ReleaseEntity(entity);
        foreach (Caster caster in removed)
        {
            caster.Active = false;
            caster.PoseRevision++;
            m_prepared.Remove(caster);
            m_candidates.Remove(caster);
        }
        m_entitiesChanged = true;
        Revision++;
    }

    // Source: SubsystemModelsRenderer.cs:PrepareModel — share LastAnimateFrame with the native renderer.
    public void Prepare(Camera camera, float maxDistance)
    {
        if (camera == null) throw new ArgumentNullException(nameof(camera));
        if (!(maxDistance > 0f)) throw new ArgumentOutOfRangeException(nameof(maxDistance));
        // Source: Time.cs:RealTime — animation snapshots are limited to 12 Hz per unchanged camera batch.
        // A camera switch always rebuilds; cached matrices must never cross player views.
        if (!m_entitiesChanged && ReferenceEquals(camera, m_camera) &&
            maxDistance == m_preparedRange && Time.RealTime < m_nextPrepareTime && PreparedResourcesMatch()) return;
        m_camera = camera;
        m_candidates.Clear();
        m_prepared.Clear();
        foreach (KeyValuePair<Entity, List<Caster>> pair in m_entities)
        {
            ComponentFrame frame = pair.Key.FindComponent<ComponentFrame>();
            foreach (Caster caster in pair.Value)
            {
                caster.Seen = false;
                if (!pair.Key.IsAddedToProject || caster.Component.Model == null) continue;
                if (frame == null) throw new InvalidOperationException("[SuAPI] Shadow model has no ComponentFrame.");
                if (Vector3.DistanceSquared(frame.Position, camera.ViewPosition) > maxDistance * maxDistance) continue;
                m_candidates.Add(caster);
            }
        }
        // Source: ComponentModel.PrepareOrder; stable ties keep distant entity changes out of local snapshot comparisons.
        m_candidates.Sort((a, b) =>
        {
            int order = a.Component.PrepareOrder.CompareTo(b.Component.PrepareOrder);
            return order != 0 ? order : a.Sequence.CompareTo(b.Sequence);
        });
        foreach (Caster caster in m_candidates)
        {
            ComponentModel model = caster.Component;
            // Source: SubsystemModelsRenderer.cs:m_componentModels — one live registry per owning Project.
            if (m_project == null)
            {
                m_project = model.Project;
                m_nativeRenderer = m_project.FindSubsystem<SubsystemModelsRenderer>(true);
                m_nativeModels = ModManager.Instance.ModParentField.GetParentField<IDictionary>(m_nativeRenderer, "m_componentModels");
            }
            if (!ReferenceEquals(model.Project, m_project))
                throw new InvalidOperationException("[SuAPI] Shadow renderer cannot combine models from different Projects.");
            if (!m_nativeModels.Contains(model)) throw new InvalidOperationException("[SuAPI] Shadow model is not registered with the native model renderer.");
            object data = m_nativeModels[model];
            int lastFrame = ModManager.Instance.ModParentField.GetParentField<int>(data, "LastAnimateFrame");
            if (Time.FrameIndex > lastFrame)
            {
                model.Animate();
                ModManager.Instance.ModParentField.ModifyParentField(data, "LastAnimateFrame", Time.FrameIndex);
            }
            float opacity = model.Opacity ?? 1f;
            if (!(opacity > 0.02f)) continue;
            // Source: ComponentModel.cs:SetModel — assigning a non-null Model allocates this array before its first calculation.
            // Zero/uncomputed matrices are normal here and are replaced by CalculateAbsoluteBonesTransforms below.
            Matrix[] bones = model.AbsoluteBoneTransformsForCamera;
            if (bones == null || bones.Length == 0 || bones.Length != model.Model.Bones.Count)
                throw new InvalidOperationException("[SuAPI] Shadow model bone array does not match its model.");
            bool changed = !caster.Active || !ReferenceEquals(caster.Model, model.Model) ||
                !ReferenceEquals(caster.Texture, model.TextureOverride) || caster.Opacity != opacity;
            if (!AppearanceMatches(model.Entity, caster.Appearance))
            {
                caster.Appearance = CaptureAppearance(model.Entity);
                changed = true;
            }
            if (caster.WorldBones == null || caster.WorldBones.Length != bones.Length)
            {
                caster.WorldBones = new Matrix[bones.Length];
                caster.SavedBones = new Matrix[bones.Length];
                changed = true;
            }
            Array.Copy(bones, caster.SavedBones, bones.Length);
            // Source: Dawnrift BodyCameraAdapter/DrawLocalPlayerShadow; ComponentModel.cs:ProcessBoneHierarchy.
            Camera boneCamera = camera;
            if (camera.GameWidget.IsEntityFirstPersonTarget(model.Entity))
            {
                ComponentBody body = model.Entity.FindComponent<ComponentBody>(true);
                Matrix bodyView = Matrix.CreateLookAt(body.Position,
                    body.Position + body.Matrix.Forward, body.Matrix.Up);
                boneCamera = new ShadowBodyCamera(camera, bodyView);
            }
            try
            {
                model.CalculateAbsoluteBonesTransforms(boneCamera);
                Matrix inverseView = boneCamera.InvertedViewMatrix;
                for (int i = 0; i < bones.Length; i++)
                {
                    Matrix world = bones[i] * inverseView;
                    if (!SamePose(caster.WorldBones[i], world))
                    {
                        changed = true;
                        caster.WorldBones[i] = world;
                    }
                }
            }
            finally { Array.Copy(caster.SavedBones, bones, bones.Length); }
            if (caster.MeshOrders == null || caster.MeshOrders.Length != model.MeshDrawOrders.Length)
            {
                caster.MeshOrders = new int[model.MeshDrawOrders.Length];
                changed = true;
            }
            for (int i = 0; i < caster.MeshOrders.Length; i++)
            {
                if (caster.MeshOrders[i] != model.MeshDrawOrders[i]) changed = true;
                caster.MeshOrders[i] = model.MeshDrawOrders[i];
            }
            caster.Model = model.Model;
            caster.Texture = model.TextureOverride;
            caster.Opacity = opacity;
            // Source: ModelMesh.cs:BoundingBox; BoundingBox.cs:Transform — actual posed mesh bounds, not a default body size.
            bool haveBounds = false;
            BoundingBox bounds = default(BoundingBox);
            foreach (ModelMesh mesh in model.Model.Meshes)
            {
                BoundingBox posed = BoundingBox.Transform(mesh.BoundingBox, caster.WorldBones[mesh.ParentBone.Index]);
                bounds = haveBounds ? BoundingBox.Union(bounds, posed) : posed;
                haveBounds = true;
            }
            if (!haveBounds) throw new InvalidOperationException("[SuAPI] Shadow model has no meshes.");
            // Source: ComponentHumanModel.cs:DrawExtras — held items are drawn outside the skeletal model.
            // Capture once per pose batch; every sun/local-light draw reuses the resulting GPU geometry.
            HeldItemShadowRenderer.HeldItemState held = model is ComponentHumanModel human
                ? m_heldItems.Capture(human, caster.WorldBones, camera) : null;
            int heldValue = held != null ? held.Value : 0;
            long heldRevision = held != null ? held.Revision : 0;
            Matrix heldMatrix = held != null ? held.WorldMatrix : Matrix.Identity;
            if (caster.HeldValue != heldValue || caster.HeldRevision != heldRevision ||
                !SamePose(caster.HeldWorldMatrix, heldMatrix)) changed = true;
            caster.HeldItem = held;
            caster.HeldValue = heldValue;
            caster.HeldRevision = heldRevision;
            caster.HeldWorldMatrix = heldMatrix;
            if (held != null) bounds = BoundingBox.Union(bounds, held.Bounds);
            ComponentBody actualBody = model.Entity.FindComponent<ComponentBody>();
            if (actualBody != null) bounds = BoundingBox.Union(bounds, actualBody.BoundingBox);
            if (!caster.Bounds.Equals(bounds)) changed = true;
            caster.Bounds = bounds;
            caster.Active = true;
            caster.Seen = true;
            m_prepared.Add(caster);
            if (changed) { caster.PoseRevision++; Revision++; }
        }
        foreach (List<Caster> casters in m_entities.Values)
            foreach (Caster caster in casters)
                if (caster.Active && !caster.Seen) { caster.Active = false; caster.PoseRevision++; Revision++; }
        m_preparedRange = maxDistance;
        m_nextPrepareTime = Time.RealTime + 1.0 / 12.0;
        m_entitiesChanged = false;
    }

    // Source: ComponentClothing.UpdateRenderTargets; a material replacement cannot wait for the next pose timer.
    private bool PreparedResourcesMatch()
    {
        foreach (Caster caster in m_prepared)
        {
            ComponentModel model = caster.Component;
            if (!model.Entity.IsAddedToProject || !ReferenceEquals(model.Model, caster.Model) ||
                !ReferenceEquals(model.TextureOverride, caster.Texture) || !AppearanceMatches(model.Entity, caster.Appearance)) return false;
        }
        return true;
    }

    // Source: ComponentClothing.cs:GetClothes/UpdateRenderTargets; compare the full ordered appearance, never a hash.
    private static AppearanceKey CaptureAppearance(Entity entity)
    {
        ComponentClothing clothing = entity.FindComponent<ComponentClothing>();
        if (clothing == null) return null;
        int count = 4;
        for (int slot = 0; slot < 4; slot++) count += clothing.GetClothes((ClothingSlot)slot).Count;
        var values = new int[count];
        int offset = 0;
        for (int slot = 0; slot < 4; slot++)
        {
            ReadOnlyList<int> clothes = clothing.GetClothes((ClothingSlot)slot);
            values[offset++] = clothes.Count;
            for (int i = 0; i < clothes.Count; i++) values[offset++] = clothes[i];
        }
        return new AppearanceKey
        {
            Clothing = clothing,
            SkinName = entity.FindComponent<ComponentPlayer>(true).PlayerData.CharacterSkinName,
            TexturesEnabled = ComponentClothing.DrawClothedTexture,
            WearValues = values
        };
    }

    // Source: ComponentClothing.SetClothes; keep counts so moving an equal value between slots also changes the key.
    private static bool AppearanceMatches(Entity entity, AppearanceKey key)
    {
        ComponentClothing clothing = entity.FindComponent<ComponentClothing>();
        if (key == null) return clothing == null;
        if (!ReferenceEquals(clothing, key.Clothing) || key.TexturesEnabled != ComponentClothing.DrawClothedTexture ||
            key.SkinName != entity.FindComponent<ComponentPlayer>(true).PlayerData.CharacterSkinName) return false;
        int offset = 0;
        for (int slot = 0; slot < 4; slot++)
        {
            ReadOnlyList<int> clothes = clothing.GetClothes((ClothingSlot)slot);
            if (offset >= key.WearValues.Length || key.WearValues[offset++] != clothes.Count) return false;
            for (int i = 0; i < clothes.Count; i++)
                if (offset >= key.WearValues.Length || key.WearValues[offset++] != clothes[i]) return false;
        }
        return offset == key.WearValues.Length;
    }

    // Source: ModelShadowRenderer.DrawCore; the same sphere test defines capture, invalidation and face drawing.
    private static bool IntersectsLight(BoundingBox bounds, Vector3 lightPosition, float lightRadius)
    {
        Vector3 closest = new Vector3(MathUtils.Clamp(lightPosition.X, bounds.Min.X, bounds.Max.X),
            MathUtils.Clamp(lightPosition.Y, bounds.Min.Y, bounds.Max.Y),
            MathUtils.Clamp(lightPosition.Z, bounds.Min.Z, bounds.Max.Z));
        return Vector3.DistanceSquared(closest, lightPosition) <= lightRadius * lightRadius;
    }

    // Source: Dispatcher.MainThreadId; graphics resources and model registries belong to the frame thread.
    private static void VerifyRenderingThread()
    {
        if (Environment.CurrentManagedThreadId != Dispatcher.MainThreadId)
            throw new InvalidOperationException("[SuAPI] Point-shadow snapshots must be used on the rendering thread.");
    }

    // Source: InstancedModelsManager.GetInstancedModelData; borrow immutable cached geometry until reset, with no GPU copy.
    public PointSnapshot CapturePointSnapshot(Camera camera, Vector3 lightPosition, float lightRadius)
    {
        VerifyRenderingThread();
        if (m_disposed) throw new ObjectDisposedException(nameof(ModelShadowRenderer));
        if (camera == null) throw new ArgumentNullException(nameof(camera));
        if (!(lightRadius > 0f)) throw new ArgumentOutOfRangeException(nameof(lightRadius));
        if (!ReferenceEquals(camera, m_camera))
            throw new InvalidOperationException("[SuAPI] Prepare must precede capturing this camera's point-shadow batch.");
        var frozen = new List<FrozenCaster>();
        try
        {
            foreach (Caster caster in m_prepared)
            {
                if (!IntersectsLight(caster.Bounds, lightPosition, lightRadius)) continue;
                var copy = new FrozenCaster
                {
                    Identity = caster, PoseRevision = caster.PoseRevision,
                    WorldBones = (Matrix[])caster.WorldBones.Clone(), MeshOrders = (int[])caster.MeshOrders.Clone(),
                    Model = caster.Model, Texture = caster.Texture, Opacity = caster.Opacity,
                    Bounds = caster.Bounds, Appearance = caster.Appearance
                };
                frozen.Add(copy);
                copy.Geometry = InstancedModelsManager.GetInstancedModelData(copy.Model, copy.MeshOrders);
                copy.HeldItem = caster.HeldItem?.Freeze();
            }
            var snapshot = new PointSnapshot(this, camera, lightPosition, lightRadius, m_resourceEpoch, frozen.ToArray());
            if (!PointSnapshotResourcesValid(snapshot))
                throw new InvalidOperationException("[SuAPI] Point-shadow capture contains invalid model or material resources.");
            m_pointSnapshots.Add(snapshot);
            return snapshot;
        }
        catch { foreach (FrozenCaster caster in frozen) caster.HeldItem?.Dispose(); throw; }
    }

    // Source: ModelShadowRenderer.Prepare; compare only this light's complete ordered caster set and local pose revisions.
    public bool PointSnapshotMatches(PointSnapshot snapshot, Vector3 lightPosition, float lightRadius)
    {
        VerifyRenderingThread();
        if (!PointSnapshotResourcesValid(snapshot) || !ReferenceEquals(snapshot.Camera, m_camera) ||
            snapshot.LightPosition != lightPosition || snapshot.LightRadius != lightRadius) return false;
        int index = 0;
        foreach (Caster caster in m_prepared)
        {
            if (!IntersectsLight(caster.Bounds, lightPosition, lightRadius)) continue;
            if (index >= snapshot.Casters.Length) return false;
            FrozenCaster frozen = snapshot.Casters[index++];
            if (!ReferenceEquals(frozen.Identity, caster) || frozen.PoseRevision != caster.PoseRevision) return false;
        }
        return index == snapshot.Casters.Length;
    }

    // Source: GraphicsResource.m_isDisposed; ComponentClothing.Dispose/UpdateRenderTargets; ordinary pose motion is allowed.
    public bool PointSnapshotResourcesValid(PointSnapshot snapshot)
    {
        VerifyRenderingThread();
        if (m_disposed || snapshot == null || snapshot.Disposed || !ReferenceEquals(snapshot.Owner, this) ||
            snapshot.ResourceEpoch != m_resourceEpoch) return false;
        foreach (FrozenCaster caster in snapshot.Casters)
        {
            ComponentModel model = caster.Identity.Component;
            Entity entity = model.Entity;
            if (!entity.IsAddedToProject || !m_entities.TryGetValue(entity, out List<Caster> live) ||
                !live.Contains(caster.Identity) || !ReferenceEquals(model.Model, caster.Model) ||
                !ReferenceEquals(model.TextureOverride, caster.Texture) || !AppearanceMatches(entity, caster.Appearance) ||
                !HeldItemShadowRenderer.ResourceValid(caster.Geometry.VertexBuffer) ||
                !HeldItemShadowRenderer.ResourceValid(caster.Geometry.IndexBuffer) ||
                (caster.Texture != null && !HeldItemShadowRenderer.ResourceValid(caster.Texture)) ||
                (caster.HeldItem != null && !caster.HeldItem.ResourcesValid())) return false;
        }
        return true;
    }

    // Source: ModelShadowRenderer.DrawCore; report actual draw submission so cleared empty faces need no model sampling.
    public bool DrawPointSnapshot(PointSnapshot snapshot, Matrix shadowMatrix, Vector2 shadowOrigin,
        BoundingFrustum frustum, Vector3 lightPosition, float lightRadius)
    {
        VerifyRenderingThread();
        if (!PointSnapshotResourcesValid(snapshot))
            throw new InvalidOperationException("[SuAPI] Point-shadow snapshot resources changed before face submission.");
        if (snapshot.LightPosition != lightPosition || snapshot.LightRadius != lightRadius)
            throw new InvalidOperationException("[SuAPI] Point-shadow snapshot belongs to a different light projection.");
        Vector3 origin = new Vector3(shadowOrigin.X, 0f, shadowOrigin.Y);
        RasterizerState previousRasterizer = Display.RasterizerState;
        BlendState previousBlend = Display.BlendState;
        DepthStencilState previousDepth = Display.DepthStencilState;
        bool submitted = false;
        try
        {
            Display.RasterizerState = RasterizerState.CullNoneScissor;
            Display.BlendState = BlendState.Opaque;
            Display.DepthStencilState = DepthStencilState.Default;
            foreach (FrozenCaster caster in snapshot.Casters)
            {
                BoundingBox localBounds = new BoundingBox(caster.Bounds.Min - origin, caster.Bounds.Max - origin);
                if (!frustum.Intersection(localBounds)) continue;
                if (caster.Geometry.IndexBuffer.IndicesCount > 0)
                {
                    ModelDepthShader shader = GetShader(caster.WorldBones.Length, true);
                    shader.Set(caster.WorldBones, shadowMatrix, origin, caster.Texture, caster.Opacity,
                        lightPosition - origin, lightRadius);
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader, caster.Geometry.VertexBuffer,
                        caster.Geometry.IndexBuffer, 0, caster.Geometry.IndexBuffer.IndicesCount);
                    submitted = true;
                }
                if (caster.HeldItem != null && caster.HeldItem.Parts.Count > 0)
                {
                    m_heldItems.Draw(caster.HeldItem, shadowMatrix, shadowOrigin, lightPosition, lightRadius, caster.Opacity);
                    submitted = true;
                }
            }
        }
        finally
        {
            Display.RasterizerState = previousRasterizer;
            Display.BlendState = previousBlend;
            Display.DepthStencilState = previousDepth;
        }
        return submitted;
    }

    // Source: ModelShadowRenderer.DrawCore; solar and point draws share the existing bone-count shader cache.
    private ModelDepthShader GetShader(int count, bool point)
    {
        if (!m_shaders.TryGetValue(count, out ModelDepthShader[] shaders))
        {
            ModelDepthShader sun = new ModelDepthShader(count, false);
            try { shaders = new[] { sun, new ModelDepthShader(count, true) }; }
            catch { sun.Dispose(); throw; }
            m_shaders.Add(count, shaders);
        }
        return shaders[point ? 1 : 0];
    }

    // Source: TerrainShadowShaders.cs:ShadowMapVertex/PointShadowMapVertex — same origin, depth convention and RG packing.
    // lightPosition is WORLD space; frustum is shadowOrigin-local. The caller owns the render target and viewport.
    public void Draw(Camera camera, Matrix shadowMatrix, Vector2 shadowOrigin,
        BoundingFrustum frustum, Vector3? lightPosition, float lightRadius)
    {
        DrawCore(camera, shadowMatrix, shadowOrigin, frustum, lightPosition, lightRadius, false);
    }

    // Source: SubsystemDynamicShadows.SunShadows.cs:SubsystemDynamicShadows.DrawDynamicSunTarget; TerrainShadowShaders.cs:TerrainPixel.sampleShadow.
    // Orthographic sun maps only. Returns inclusive (minU, minV, maxU, maxV) for submitted casters,
    // or (1, 1, 0, 0) when empty. Cache alongside this map until its next draw. The caller must
    // expand by the map's PCF support; bounds are deliberately neither padded nor clamped to [0, 1].
    public Vector4 DrawSun(Camera camera, Matrix shadowMatrix, Vector2 shadowOrigin, BoundingFrustum frustum)
    {
        return DrawCore(camera, shadowMatrix, shadowOrigin, frustum, null, 0f, true);
    }

    private Vector4 DrawCore(Camera camera, Matrix shadowMatrix, Vector2 shadowOrigin,
        BoundingFrustum frustum, Vector3? lightPosition, float lightRadius, bool collectSunBounds)
    {
        if (!ReferenceEquals(camera, m_camera)) throw new InvalidOperationException("[SuAPI] Prepare must precede drawing this camera's shadow batch.");
        if (lightPosition.HasValue && !(lightRadius > 0f)) throw new ArgumentOutOfRangeException(nameof(lightRadius));
        Vector4 sunBounds = new Vector4(1f, 1f, 0f, 0f);
        Vector3 origin = new Vector3(shadowOrigin.X, 0f, shadowOrigin.Y);
        RasterizerState previousRasterizer = Display.RasterizerState;
        BlendState previousBlend = Display.BlendState;
        DepthStencilState previousDepth = Display.DepthStencilState;
        try
        {
            Display.RasterizerState = RasterizerState.CullNoneScissor;
            Display.BlendState = BlendState.Opaque;
            Display.DepthStencilState = DepthStencilState.Default;
            foreach (Caster caster in m_prepared)
            {
                BoundingBox localBounds = new BoundingBox(caster.Bounds.Min - origin, caster.Bounds.Max - origin);
                if (!frustum.Intersection(localBounds)) continue;
                if (lightPosition.HasValue)
                {
                    Vector3 light = lightPosition.Value;
                    Vector3 closest = new Vector3(
                        MathUtils.Clamp(light.X, caster.Bounds.Min.X, caster.Bounds.Max.X),
                        MathUtils.Clamp(light.Y, caster.Bounds.Min.Y, caster.Bounds.Max.Y),
                        MathUtils.Clamp(light.Z, caster.Bounds.Min.Z, caster.Bounds.Max.Z));
                    if (Vector3.DistanceSquared(closest, lightPosition.Value) > lightRadius * lightRadius) continue;
                }
                ModelDepthShader shader = GetShader(caster.WorldBones.Length, lightPosition.HasValue);
                shader.Set(caster.WorldBones, shadowMatrix, origin, caster.Texture, caster.Opacity,
                    lightPosition.HasValue ? lightPosition.Value - origin : Vector3.Zero, lightRadius);
                InstancedModelData geometry = InstancedModelsManager.GetInstancedModelData(caster.Model, caster.MeshOrders);
                Display.DrawIndexed(PrimitiveType.TriangleList, shader, geometry.VertexBuffer,
                    geometry.IndexBuffer, 0, geometry.IndexBuffer.IndicesCount);
                m_heldItems.Draw(caster.HeldItem, shadowMatrix, shadowOrigin,
                    lightPosition, lightRadius, caster.Opacity);
                // Source: ModelShadowRenderer.cs:ModelShadowRenderer.Prepare — Bounds includes posed meshes and held items.
                // Keep the shadow frustum's full depth span; camera visibility cannot exclude an offscreen caster.
                if (collectSunBounds) sunBounds = IncludeSunBounds(sunBounds, localBounds, shadowMatrix);
            }
        }
        finally
        {
            Display.RasterizerState = previousRasterizer;
            Display.BlendState = previousBlend;
            Display.DepthStencilState = previousDepth;
        }
        return sunBounds;
    }

    // Source: BoundingBox.cs:BoundingBox.Transform; Vector3.cs:Vector3.Transform — affine extrema without allocating corner arrays.
    private static Vector4 IncludeSunBounds(Vector4 bounds, BoundingBox localBounds, Matrix matrix)
    {
        Vector3 center = Vector3.Transform(0.5f * (localBounds.Min + localBounds.Max), matrix);
        Vector3 halfSize = 0.5f * (localBounds.Max - localBounds.Min);
        float radiusX = MathUtils.Abs(matrix.M11) * halfSize.X +
            MathUtils.Abs(matrix.M21) * halfSize.Y + MathUtils.Abs(matrix.M31) * halfSize.Z;
        float radiusY = MathUtils.Abs(matrix.M12) * halfSize.X +
            MathUtils.Abs(matrix.M22) * halfSize.Y + MathUtils.Abs(matrix.M32) * halfSize.Z;
        Vector4 projected = new Vector4(
            0.5f + 0.5f * (center.X - radiusX),
            0.5f - 0.5f * (center.Y + radiusY),
            0.5f + 0.5f * (center.X + radiusX),
            0.5f - 0.5f * (center.Y - radiusY));
        if (bounds.X > bounds.Z) return projected;
        return new Vector4(MathUtils.Min(bounds.X, projected.X), MathUtils.Min(bounds.Y, projected.Y),
            MathUtils.Max(bounds.Z, projected.Z), MathUtils.Max(bounds.W, projected.W));
    }

    // Source: Matrix.cs:Right/Up/Forward/Translation — ignore round-off from cancelling a moving camera basis.
    private static bool SamePose(Matrix a, Matrix b)
    {
        const float toleranceSquared = 0.00000001f;
        return Vector3.DistanceSquared(a.Right, b.Right) <= toleranceSquared &&
            Vector3.DistanceSquared(a.Up, b.Up) <= toleranceSquared &&
            Vector3.DistanceSquared(a.Forward, b.Forward) <= toleranceSquared &&
            Vector3.DistanceSquared(a.Translation, b.Translation) <= toleranceSquared;
    }

    public void Dispose()
    {
        if (m_disposed) return;
        m_disposed = true;
        Display.DeviceReset -= InvalidatePointSnapshots;
        foreach (PointSnapshot snapshot in new List<PointSnapshot>(m_pointSnapshots)) snapshot.Dispose();
        m_heldItems.Dispose();
        foreach (ModelDepthShader[] pair in m_shaders.Values)
            foreach (ModelDepthShader shader in pair) shader.Dispose();
        m_shaders.Clear();
        m_entities.Clear();
        m_prepared.Clear();
        m_candidates.Clear();
        m_camera = null;
        m_project = null;
        m_nativeRenderer = null;
        m_nativeModels = null;
        m_entitiesChanged = true;
        m_nextPrepareTime = 0.0;
    }
}
