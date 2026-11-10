using Engine;
using Game;

namespace SuAPIShadows;

// Source: Dawnrift BodyCameraAdapter; ComponentModel.cs:CalculateAbsoluteBonesTransforms.
// Only the calculation's view basis changes; the live camera and live bone array are not retained or mutated.
internal sealed class ShadowBodyCamera : Camera
{
    private readonly Camera m_original;
    private readonly Matrix m_view;
    public ShadowBodyCamera(Camera original, Matrix view) : base(original.GameWidget)
    {
        m_original = original;
        m_view = view;
    }
    public override Vector3 ViewPosition => m_original.ViewPosition;
    public override Vector3 ViewDirection => m_original.ViewDirection;
    public override Vector3 ViewUp => m_original.ViewUp;
    public override Vector3 ViewRight => m_original.ViewRight;
    public override Matrix ViewMatrix => m_view;
    public override Matrix InvertedViewMatrix => Matrix.Invert(m_view);
    public override Matrix ProjectionMatrix => m_original.ProjectionMatrix;
    public override Matrix ScreenProjectionMatrix => m_original.ScreenProjectionMatrix;
    public override Matrix InvertedProjectionMatrix => m_original.InvertedProjectionMatrix;
    public override Matrix ViewProjectionMatrix => m_view * ProjectionMatrix;
    public override Vector2 ViewportSize => m_original.ViewportSize;
    public override Matrix ViewportMatrix => m_original.ViewportMatrix;
    public override BoundingFrustum ViewFrustum => m_original.ViewFrustum;
    public override bool UsesMovementControls => m_original.UsesMovementControls;
    public override bool IsEntityControlEnabled => m_original.IsEntityControlEnabled;
    public override void Update(float dt) { }
}
