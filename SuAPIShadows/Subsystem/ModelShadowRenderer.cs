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
    private sealed class Caster
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
    }

    private readonly Dictionary<Entity, List<Caster>> m_entities = new Dictionary<Entity, List<Caster>>();
    private readonly List<Caster> m_candidates = new List<Caster>();
    private readonly List<Caster> m_prepared = new List<Caster>();
    private readonly Dictionary<int, ModelDepthShader[]> m_shaders = new Dictionary<int, ModelDepthShader[]>();
    private readonly HeldItemShadowRenderer m_heldItems = new HeldItemShadowRenderer();
    private Camera m_camera;
    private Project m_project;
    private SubsystemModelsRenderer m_nativeRenderer;
    private IDictionary m_nativeModels;
    private bool m_entitiesChanged = true;
    private float m_preparedRange;
    private double m_nextPrepareTime;
    public long Revision { get; private set; }

    // Source: SubsystemModelsRenderer.cs:OnEntityAdded — retain every originally eligible model, including clothing.
    public void AddEntity(Entity entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        if (m_entities.ContainsKey(entity)) return;
        var casters = new List<Caster>();
        foreach (ComponentModel model in entity.FindComponents<ComponentModel>())
            if (model.CastsShadow) casters.Add(new Caster { Component = model });
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
            maxDistance == m_preparedRange && Time.RealTime < m_nextPrepareTime) return;
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
        m_candidates.Sort((a, b) => a.Component.PrepareOrder.CompareTo(b.Component.PrepareOrder));
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
            if (changed) Revision++;
        }
        foreach (List<Caster> casters in m_entities.Values)
            foreach (Caster caster in casters)
                if (caster.Active && !caster.Seen) { caster.Active = false; Revision++; }
        m_preparedRange = maxDistance;
        m_nextPrepareTime = Time.RealTime + 1.0 / 12.0;
        m_entitiesChanged = false;
    }

    // Source: TerrainShadowShaders.cs:ShadowMapVertex/PointShadowMapVertex — same origin, depth convention and RG packing.
    // lightPosition is WORLD space; frustum is shadowOrigin-local. The caller owns the render target and viewport.
    public void Draw(Camera camera, Matrix shadowMatrix, Vector2 shadowOrigin,
        BoundingFrustum frustum, Vector3? lightPosition, float lightRadius)
    {
        if (!ReferenceEquals(camera, m_camera)) throw new InvalidOperationException("[SuAPI] Prepare must precede drawing this camera's shadow batch.");
        if (lightPosition.HasValue && !(lightRadius > 0f)) throw new ArgumentOutOfRangeException(nameof(lightRadius));
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
                int count = caster.WorldBones.Length;
                if (!m_shaders.TryGetValue(count, out ModelDepthShader[] shaders))
                {
                    ModelDepthShader sun = new ModelDepthShader(count, false);
                    try { shaders = new[] { sun, new ModelDepthShader(count, true) }; }
                    catch { sun.Dispose(); throw; }
                    m_shaders.Add(count, shaders);
                }
                ModelDepthShader shader = shaders[lightPosition.HasValue ? 1 : 0];
                shader.Set(caster.WorldBones, shadowMatrix, origin, caster.Texture, caster.Opacity,
                    lightPosition.HasValue ? lightPosition.Value - origin : Vector3.Zero, lightRadius);
                InstancedModelData geometry = InstancedModelsManager.GetInstancedModelData(caster.Model, caster.MeshOrders);
                Display.DrawIndexed(PrimitiveType.TriangleList, shader, geometry.VertexBuffer,
                    geometry.IndexBuffer, 0, geometry.IndexBuffer.IndicesCount);
                m_heldItems.Draw(caster.HeldItem, shadowMatrix, shadowOrigin,
                    lightPosition, lightRadius, caster.Opacity);
            }
        }
        finally
        {
            Display.RasterizerState = previousRasterizer;
            Display.BlendState = previousBlend;
            Display.DepthStencilState = previousDepth;
        }
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
