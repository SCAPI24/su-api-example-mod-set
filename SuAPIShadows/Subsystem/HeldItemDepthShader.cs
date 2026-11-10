using System;
using Engine;
using Engine.Graphics;

namespace SuAPIShadows;

// Source: BaseTexturedBatch.FlushWithDeviceState; ModelDepthShader.Pixel; BaseFlatBatch/FontBatch.
internal sealed class HeldItemDepthShader : Shader
{
    private readonly bool m_point;
    public HeldItemDepthShader(bool point) : base(Vertex, Pixel, new ShaderMacro("POINT_DEPTH", point ? "1" : "0"))
    {
        m_point = point;
    }

    public void Set(Matrix localWorld, Matrix shadowMatrix, Vector3 lightPosition, float radius,
        float opacity, Texture2D texture, SamplerState sampler, bool alphaTest, bool alphaBlend)
    {
        GetParameter("u_world").SetValue(localWorld);
        GetParameter("u_shadowMatrix").SetValue(shadowMatrix);
        GetParameter("u_texture").SetValue(texture);
        GetParameter("u_samplerState").SetValue(sampler);
        GetParameter("u_hasTexture").SetValue(texture != null ? 1f : 0f);
        GetParameter("u_clipAlpha").SetValue(alphaTest || alphaBlend ? 1f : 0f);
        GetParameter("u_opacity").SetValue(opacity);
        if (m_point)
        {
            GetParameter("u_lightPosition").SetValue(lightPosition);
            GetParameter("u_lightRadius").SetValue(radius);
        }
    }

    private const string Vertex = """
// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='COLOR' Attribute='a_color' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />
#ifdef GL_ES
precision highp float;
#endif
uniform mat4 u_world;
uniform mat4 u_shadowMatrix;
attribute vec3 a_position;
attribute vec4 a_color;
attribute vec2 a_texcoord;
varying vec2 v_texcoord;
varying float v_alpha;
#if POINT_DEPTH
varying vec3 v_localPosition;
#else
varying float v_depth;
#endif
void main()
{
    vec4 local = u_world * vec4(a_position, 1.0);
    vec4 clip = u_shadowMatrix * local;
    v_texcoord = a_texcoord;
    v_alpha = a_color.a;
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
uniform float u_clipAlpha;
uniform float u_opacity;
varying vec2 v_texcoord;
varying float v_alpha;
#if POINT_DEPTH
uniform vec3 u_lightPosition;
uniform float u_lightRadius;
varying vec3 v_localPosition;
#else
varying float v_depth;
#endif
void main()
{
    if (u_opacity <= 0.0) discard;
    // Native UseAlphaTest uses threshold zero; opaque batches must retain their full geometric coverage.
    if (u_clipAlpha > 0.5)
    {
        float alpha = v_alpha * u_opacity;
        if (u_hasTexture > 0.5) alpha *= texture2D(u_texture, v_texcoord).a;
        if (alpha <= 0.0) discard;
    }
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
