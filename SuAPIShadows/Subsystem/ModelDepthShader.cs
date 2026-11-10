using System;
using System.Globalization;
using Engine;
using Engine.Graphics;

namespace SuAPIShadows;

// Source: InstancedModelsManager.cs:InstancedVertex; TerrainShadowShaders.cs:ShadowMapPixel/PointShadowMapPixel.
// Raster depth selects the nearest caster; RG stores exactly the receiver's existing depth encoding.
internal sealed class ModelDepthShader : Shader
{
    private readonly Matrix[] m_localBones;
    private readonly bool m_point;
    public ModelDepthShader(int boneCount, bool point) : base(Vertex, Pixel,
        new ShaderMacro("BONE_COUNT", boneCount.ToString(CultureInfo.InvariantCulture)),
        new ShaderMacro("POINT_DEPTH", point ? "1" : "0"))
    {
        if (boneCount < 1) throw new ArgumentOutOfRangeException(nameof(boneCount));
        m_localBones = new Matrix[boneCount];
        m_point = point;
    }

    public void Set(Matrix[] worldBones, Matrix matrix, Vector3 origin,
        Texture2D texture, float opacity, Vector3 lightPosition, float radius)
    {
        if (worldBones.Length != m_localBones.Length) throw new InvalidOperationException("[SuAPI] Shadow bone count changed without its shader.");
        Matrix shift = Matrix.CreateTranslation(-origin);
        for (int i = 0; i < worldBones.Length; i++) m_localBones[i] = worldBones[i] * shift;
        GetParameter("u_bones").SetValue(m_localBones, m_localBones.Length);
        GetParameter("u_shadowMatrix").SetValue(matrix);
        GetParameter("u_hasTexture").SetValue(texture != null ? 1f : 0f);
        GetParameter("u_texture").SetValue(texture);
        GetParameter("u_samplerState").SetValue(SamplerState.PointClamp);
        GetParameter("u_opacity").SetValue(opacity);
        if (m_point)
        {
            GetParameter("u_lightPosition").SetValue(lightPosition);
            GetParameter("u_lightRadius").SetValue(radius);
        }
    }

    private const string Vertex = """
// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />
// <Semantic Name='INSTANCE' Attribute='a_instance' />
#ifdef GL_ES
precision highp float;
#endif
uniform mat4 u_bones[BONE_COUNT];
uniform mat4 u_shadowMatrix;
attribute vec3 a_position;
attribute vec2 a_texcoord;
attribute float a_instance;
varying vec2 v_texcoord;
#if POINT_DEPTH
varying vec3 v_localPosition;
#else
varying float v_depth;
#endif
void main()
{
    vec4 local = u_bones[int(a_instance)] * vec4(a_position, 1.0);
    vec4 clip = u_shadowMatrix * local;
    v_texcoord = a_texcoord;
#if POINT_DEPTH
    v_localPosition = local.xyz;
#else
    v_depth = clip.z / clip.w;
#endif
    gl_Position = clip;
    OPENGL_POSITION_FIX;
}
""";
    private const string Pixel = """
// <Sampler Name='u_samplerState' Texture='u_texture' />
#ifdef GL_ES
precision highp float;
#endif
uniform sampler2D u_texture;
uniform float u_hasTexture;
uniform float u_opacity;
varying vec2 v_texcoord;
#if POINT_DEPTH
uniform vec3 u_lightPosition;
uniform float u_lightRadius;
varying vec3 v_localPosition;
#else
varying float v_depth;
#endif
void main()
{
    float alpha = u_opacity;
    if (u_hasTexture > 0.5) alpha *= texture2D(u_texture, v_texcoord).a;
    if (alpha <= 0.5) discard;
#if POINT_DEPTH
    float depth = length(v_localPosition - u_lightPosition) / u_lightRadius;
    if (depth > 1.0) discard;
#else
    float depth = v_depth;
#endif
    float scaled = clamp(depth, 0.0, 1.0) * 255.0;
    gl_FragColor = vec4(floor(scaled) / 255.0, fract(scaled), 0.0, 1.0);
}
""";
}
