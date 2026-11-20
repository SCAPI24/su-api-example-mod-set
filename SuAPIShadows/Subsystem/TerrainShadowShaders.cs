namespace SuAPIShadows;

internal static class TerrainShadowShaders
{
    // Source: Pak/Shaders/Opaque.fsh and Survivalcraft/Game/TerrainRenderer.cs:
    // TerrainRenderer.DrawOpaque, TerrainRenderer.DrawAlphaTested
    public const string TerrainVertex = """
// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='COLOR' Attribute='a_color' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />

uniform vec2 u_origin;
uniform mat4 u_viewProjectionMatrix;
uniform vec3 u_viewPosition;
uniform float u_fogYMultiplier;
uniform vec3 u_fogBottomTopDensity;
uniform vec2 u_hazeStartDensity;
uniform vec2 u_shadowOrigin;
uniform mat4 u_shadowMatrix;
uniform mat4 u_nearDynamicShadowMatrix;
uniform vec2 u_nearDynamicShadowOrigin;
uniform float u_fogShadowFactor;

attribute vec3 a_position;
attribute vec4 a_color;
attribute vec2 a_texcoord;

varying vec4 v_color;
varying vec2 v_texcoord;
varying float v_fog;
varying vec3 v_shadowCoord;
varying vec3 v_nearDynamicShadowCoord;
#ifdef POINT_SHADOWS
varying vec3 v_worldPosition;
#endif
varying float v_celestialShadowVisibility;

""" + VegetationMotionShaders.Shared + """
float fogIntegral(float y)
{
    return smoothstep(u_fogBottomTopDensity.x, u_fogBottomTopDensity.y, y) *
        (u_fogBottomTopDensity.y - u_fogBottomTopDensity.x) +
        u_fogBottomTopDensity.x;
}

float calculateFog(vec3 position)
{
    vec3 fogDelta = u_viewPosition - position;
    fogDelta.y *= u_fogYMultiplier;
    float fogDistance = length(fogDelta);
    float denominator = u_viewPosition.y - position.y;
    float fogFactor = abs(denominator) > 0.0001
        ? (fogIntegral(u_viewPosition.y) - fogIntegral(position.y)) / denominator
        : 0.0;
    return clamp(clamp(u_hazeStartDensity.y *
        (fogDistance - u_hazeStartDensity.x), 0.0, 1.0) +
        fogFactor * u_fogBottomTopDensity.z * fogDistance, 0.0, 1.0);
}

void main()
{
    vec3 position = vegetationPosition(a_position, a_color.a);
    v_texcoord = a_texcoord;
    v_color = vec4(a_color.rgb, vegetationAlpha(a_color.a));
    v_fog = calculateFog(position);
    vec4 shadow = u_shadowMatrix * vec4(
        position.x - u_shadowOrigin.x,
        position.y,
        position.z - u_shadowOrigin.y,
        1.0);
    // Source: Matrix.CreateOrthographic; affine light coordinates interpolate exactly without per-fragment divides.
    v_shadowCoord = vec3(0.5 + 0.5 * shadow.x, 0.5 - 0.5 * shadow.y, shadow.z);
    // Source: ModelShadowRenderer.cs:Draw — near casters use a separate world-origin projection.
    vec4 nearShadow = u_nearDynamicShadowMatrix * vec4(
        position.x - u_nearDynamicShadowOrigin.x,
        position.y,
        position.z - u_nearDynamicShadowOrigin.y,
        1.0);
    v_nearDynamicShadowCoord = vec3(0.5 + 0.5 * nearShadow.x, 0.5 - 0.5 * nearShadow.y, nearShadow.z);
#ifdef POINT_SHADOWS
    v_worldPosition = position;
#endif
    // Source: SubsystemSky.ViewFogBottom/ViewFogTop; fog blocks celestial shadows below its
    // layer, fades through the layer and leaves terrain above it unaffected.
    v_celestialShadowVisibility = mix(
        1.0,
        smoothstep(u_fogBottomTopDensity.x, u_fogBottomTopDensity.y, position.y),
        u_fogShadowFactor);
    gl_Position = u_viewProjectionMatrix * vec4(
        position.x - u_origin.x,
        position.y,
        position.z - u_origin.y,
        1.0);
    OPENGL_POSITION_FIX;
}
""";

    // Source: Pak/Shaders/Opaque.fsh; Shader.Android.cs:PrependShaderMacros (GLSL ES 100).
    public const string TerrainPixel = TerrainPixelPrefix + "\r\n" + PointLightPixelFunctions + "\r\n" + TerrainPixelSuffix;

    private const string TerrainPixelPrefix = """
// <Sampler Name='u_samplerState' Texture='u_texture' />
// <Sampler Name='u_shadowSamplerState' Texture='u_shadowTexture' />
// <Sampler Name='u_dynamicShadowSamplerState' Texture='u_dynamicShadowTexture' />
// <Sampler Name='u_nearDynamicShadowSamplerState' Texture='u_nearDynamicShadowTexture' />

#ifdef GL_ES
#extension GL_OES_standard_derivatives : require
precision highp float;
#endif

uniform sampler2D u_texture;
// Source: GLSL ES 1.00 sampler precision controls texture2D's result independently of float precision.
// Keep material sampling unchanged; all following depth samplers and sampler parameters are highp.
#ifdef GL_ES
precision highp sampler2D;
#endif
uniform sampler2D u_shadowTexture;
uniform sampler2D u_dynamicShadowTexture;
uniform sampler2D u_nearDynamicShadowTexture;
uniform float u_dynamicShadowEnabled;
uniform float u_nearDynamicShadowEnabled;
uniform vec2 u_dynamicShadowTexelSize;
uniform vec2 u_nearDynamicShadowTexelSize;
uniform vec4 u_dynamicShadowBounds;
uniform vec4 u_nearDynamicShadowBounds;
uniform vec3 u_fogColor;
uniform float u_shadowEnabled;
uniform float u_shadowStrength;
uniform float u_celestialLightFloor;
uniform vec2 u_shadowTexelSize;
""" + "\r\n" + PointLightUniforms + "\r\n" + """
#ifdef ALPHATESTED
uniform float u_alphaThreshold;
#endif

varying vec4 v_color;
varying vec2 v_texcoord;
varying float v_fog;
varying vec3 v_shadowCoord;
varying vec3 v_nearDynamicShadowCoord;
#ifdef POINT_SHADOWS
varying vec3 v_worldPosition;
#endif
varying float v_celestialShadowVisibility;

float unpackDepth(vec4 packedDepth)
{
    return min(packedDepth.r + packedDepth.g / 255.0, 1.0);
}

// Source: ModelDepthShader.Pixel — compare each point-filtered depth before interpolating coverage.
// Filtering packed depth itself creates false intermediate occluders at model silhouettes.
float modelShadowPCF(sampler2D shadowTexture, vec2 uv, vec2 texelSize, float receiverDepth)
{
    vec2 pixel = uv / texelSize - vec2(0.5);
    vec2 weight = fract(pixel);
    vec2 baseUV = (floor(pixel) + vec2(0.5)) * texelSize;
    float a = step(receiverDepth, unpackDepth(texture2D(shadowTexture, baseUV)));
    float b = step(receiverDepth, unpackDepth(texture2D(shadowTexture, baseUV + vec2(texelSize.x, 0.0))));
    float c = step(receiverDepth, unpackDepth(texture2D(shadowTexture, baseUV + vec2(0.0, texelSize.y))));
    float d = step(receiverDepth, unpackDepth(texture2D(shadowTexture, baseUV + texelSize)));
    return 1.0 - mix(mix(a, b, weight.x), mix(c, d, weight.x), weight.y);
}

// Source: TerrainShadowShaders.TerrainPixel:main; SubsystemDynamicShadows.SetTerrainShadowParameters.
// Compare each actual point-filtered texel against the same receiver plane before filtering visibility.
float terrainShadowLit(vec2 tapUV, vec2 receiverUV, float receiverDepth, vec3 receiverPlane)
{
    vec2 halfTexel = 0.5 * u_shadowTexelSize;
    tapUV = clamp(tapUV, halfTexel, vec2(1.0) - halfTexel);
    float casterDepth = unpackDepth(texture2D(u_shadowTexture, tapUV));
    // Source: SubsystemDynamicShadows.RenderTerrainShadowMap — clear depth 1 means no caster.
    // A tangent receiver can extrapolate past the far plane; empty texels must remain fully lit.
    float emptyTexel = step(1.0, casterDepth);
    // Retain the existing 0.0003 depth tolerance (about 18 cm over the 599 m span).
    // Both sides are multiplied by abs(det); no division by a nearly tangent projection is needed.
    float expectedDepth = (receiverDepth - 0.0003) * receiverPlane.z +
        dot(receiverPlane.xy, tapUV - receiverUV);
    return max(emptyTexel, step(expectedDepth, casterDepth * receiverPlane.z));
}

float terrainShadowPCF(vec2 uv, float receiverDepth, vec3 receiverPlane)
{
    // A receiver exactly parallel to the light has zero projected area and no static shadow coverage.
    // This is a geometric boundary, not a substitute gradient for a failed inverse projection.
    if (receiverPlane.z == 0.0)
        return 0.0;
    vec2 pixel = uv / u_shadowTexelSize - vec2(0.5);
    vec2 weight = fract(pixel);
    vec2 baseUV = (floor(pixel) + vec2(0.5)) * u_shadowTexelSize;
    float a = terrainShadowLit(baseUV, uv, receiverDepth, receiverPlane);
    float b = terrainShadowLit(baseUV + vec2(u_shadowTexelSize.x, 0.0), uv, receiverDepth, receiverPlane);
    float c = terrainShadowLit(baseUV + vec2(0.0, u_shadowTexelSize.y), uv, receiverDepth, receiverPlane);
    float d = terrainShadowLit(baseUV + u_shadowTexelSize, uv, receiverDepth, receiverPlane);
    return 1.0 - mix(mix(a, b, weight.x), mix(c, d, weight.x), weight.y);
}

float shadowEdge(vec2 uv)
{
    return min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
}

// Source: ModelShadowRenderer.DrawSun; bounds include all submitted casters plus the PCF support.
bool withinShadowBounds(vec2 uv, vec4 bounds)
{
    return all(greaterThanEqual(uv, bounds.xy)) && all(lessThanEqual(uv, bounds.zw));
}

float sampleShadow(vec3 shadow, vec2 uv, vec3 receiverPlane)
{
    float edge = shadowEdge(uv);
    bool farInRange = edge > 0.0 && shadow.z > 0.0 && shadow.z < 1.0;
    float staticAmount = 0.0;
    if (u_shadowEnabled > 0.5 && farInRange)
    {
        staticAmount = terrainShadowPCF(uv, shadow.z, receiverPlane) * smoothstep(0.0, 0.08, edge);
    }

    float nearAmount = 0.0;
    float nearWeight = 0.0;
    if (u_nearDynamicShadowEnabled > 0.5)
    {
        vec3 nearShadow = v_nearDynamicShadowCoord;
        vec2 nearUV = nearShadow.xy;
        float nearEdge = shadowEdge(nearUV);
        if (nearEdge > 0.0 && nearShadow.z > 0.0 && nearShadow.z < 1.0)
        {
            nearWeight = smoothstep(0.0, 0.08, nearEdge);
            // Keep the near-layer blend weight even where its cached image is empty.
            if (withinShadowBounds(nearUV, u_nearDynamicShadowBounds))
                nearAmount = modelShadowPCF(u_nearDynamicShadowTexture, nearUV,
                    u_nearDynamicShadowTexelSize, nearShadow.z - 0.00005);
        }
    }
    // Source: ModelDepthShader.Pixel — retain distant animals without letting coarse far texels cover the near layer.
    float farAmount = 0.0;
    if (nearWeight < 0.999 && u_dynamicShadowEnabled > 0.5 && farInRange &&
        withinShadowBounds(uv, u_dynamicShadowBounds))
        farAmount = modelShadowPCF(u_dynamicShadowTexture, uv,
            u_dynamicShadowTexelSize, shadow.z - 0.00005) * smoothstep(0.0, 0.08, edge);
    float modelAmount = nearWeight >= 0.999 ? nearAmount : mix(farAmount, nearAmount, nearWeight);
    return max(staticAmount, modelAmount);
}
""";

    // Source: SubsystemDynamicShadows.cs:SubsystemDynamicShadows.InitializeTerrainShadows;
    // SubsystemDynamicShadows.PointShadows.cs:SubsystemDynamicShadows.SetPointShadowParameters.
    // All receiver-count variants consume the same light contract.
    public const string PointLightUniforms = """
#ifdef POINT_SHADOWS
// <Sampler Name='u_pointShadowSamplerState' Texture='u_pointShadowTexture' />
// <Sampler Name='u_pointDynamicShadowSamplerState' Texture='u_pointDynamicShadowTexture' />
uniform sampler2D u_pointShadowTexture;
uniform sampler2D u_pointDynamicShadowTexture;
#ifndef POINT_SHADOW_LIGHT_LIMIT
#define POINT_SHADOW_LIGHT_LIMIT 10
#endif
uniform float u_pointShadowEnabled;
uniform float u_pointShadowCount;
uniform vec2 u_pointShadowOrigins[POINT_SHADOW_LIGHT_LIMIT];
uniform vec4 u_pointShadowLightPositionRadius[POINT_SHADOW_LIGHT_LIMIT];
uniform vec4 u_pointShadowLightDirectionStrength[POINT_SHADOW_LIGHT_LIMIT];
uniform vec2 u_pointShadowValid[POINT_SHADOW_LIGHT_LIMIT];
uniform vec2 u_pointShadowSlots[POINT_SHADOW_LIGHT_LIMIT];
#endif
""";

    // Source: TerrainShadowShaders.cs:TerrainShadowShaders.TerrainPixel.
    public const string PointLightPixelFunctions = """
#ifdef POINT_SHADOWS
// Source: ModelShadowRenderer.cs:ModelShadowRenderer.DrawPointSnapshot;
// SubsystemDynamicShadows.PointShadows.cs:SubsystemDynamicShadows.ClearPointShadowFace.
// Each layer stores six face columns and eleven physical-slot rows in one atlas.
// Static and dynamic layers publish their physical rows independently.
// valid.y is zero for an incomplete dynamic cache, otherwise 64 + its six-face
// caster mask. The completion bit preserves the light of a valid empty map.
// Comparing min(static, dynamic) is already dark when the static depth fails.
float samplePointVisibility(float faceIndex, vec2 localUv, float receiverDepth,
    vec2 slots, vec2 valid, float faceBit)
{
    float visibility = 1.0;
    if (valid.x > 0.5)
    {
        vec2 staticUv = (vec2(faceIndex, slots.x) + localUv) / vec2(6.0, 11.0);
        visibility = step(receiverDepth, unpackDepth(texture2D(u_pointShadowTexture, staticUv)));
    }
    if (visibility > 0.5)
    {
        float dynamicFaceMask = mod(valid.y, 64.0);
        if (mod(floor(dynamicFaceMask / faceBit), 2.0) > 0.5)
        {
            vec2 dynamicUv = (vec2(faceIndex, slots.y) + localUv) / vec2(6.0, 11.0);
            visibility = step(receiverDepth, unpackDepth(texture2D(u_pointDynamicShadowTexture, dynamicUv)));
        }
    }
    return visibility;
}

vec4 samplePointLightFor(
    vec2 origin,
    vec4 positionRadius,
    vec4 directionStrength,
    vec2 slots, vec2 valid)
{
    if (u_pointShadowEnabled < 0.5 || max(valid.x, valid.y) < 0.5)
        return vec4(0.0);
    vec3 direction = vec3(
        v_worldPosition.x - origin.x,
        v_worldPosition.y,
        v_worldPosition.z - origin.y) - positionRadius.xyz;
    // Reject receivers outside this light before square root and cube-face projection.
    float distanceSquared = dot(direction, direction);
    if (distanceSquared <= 0.0064 || distanceSquared >= positionRadius.w * positionRadius.w)
        return vec4(0.0);
    // Source: SubsystemDynamicShadows.cs:SubsystemDynamicShadows.GetDirectionalAttenuation; normalization
    // cannot change the hemisphere, so reject its back before square root.
    if (directionStrength.w > 0.0 && dot(direction, directionStrength.xyz) < 0.0)
        return vec4(0.0);
    float distanceToLight = sqrt(distanceSquared);
    vec3 absoluteDirection = abs(direction);

    vec2 projected;
    float faceIndex;
    float majorAxis;
    float faceBit;
    if (absoluteDirection.x >= absoluteDirection.y &&
        absoluteDirection.x >= absoluteDirection.z)
    {
        majorAxis = absoluteDirection.x;
        if (direction.x >= 0.0)
        {
            projected = vec2(direction.z, -direction.y) / majorAxis;
            faceIndex = 0.0;
            faceBit = 1.0;
        }
        else
        {
            projected = vec2(-direction.z, -direction.y) / majorAxis;
            faceIndex = 1.0;
            faceBit = 2.0;
        }
    }
    else if (absoluteDirection.y >= absoluteDirection.z)
    {
        majorAxis = absoluteDirection.y;
        if (direction.y >= 0.0)
        {
            projected = vec2(direction.x, -direction.z) / majorAxis;
            faceIndex = 2.0;
            faceBit = 4.0;
        }
        else
        {
            projected = vec2(-direction.x, -direction.z) / majorAxis;
            faceIndex = 3.0;
            faceBit = 8.0;
        }
    }
    else
    {
        majorAxis = absoluteDirection.z;
        if (direction.z >= 0.0)
        {
            projected = vec2(-direction.x, -direction.y) / majorAxis;
            faceIndex = 4.0;
            faceBit = 16.0;
        }
        else
        {
            projected = vec2(direction.x, -direction.y) / majorAxis;
            faceIndex = 5.0;
            faceBit = 32.0;
        }
    }

    vec2 localUv = 0.5 + 0.5 * projected;
    // Source: SubsystemDynamicShadows.PointShadows.cs:SubsystemDynamicShadows.SetPointShadowParameters;
    // retain the previous 1.5 dynamic-texel
    // padding in local face coordinates for both layer atlases.
    vec2 localPadding = vec2(1.5 / 64.0);
    localUv = clamp(localUv, localPadding, vec2(1.0) - localPadding);
    float receiverDepth = distanceToLight / positionRadius.w - 0.012;
    // Source: TerrainShadowShaders.cs:TerrainShadowShaders.samplePointVisibility;
    // a single shared face position preserves the
    // static/dynamic depth union with at most one fetch from each layer.
    float visibility = samplePointVisibility(faceIndex, localUv, receiverDepth, slots, valid, faceBit);
    // Source: Survivalcraft/Game/TerrainUpdater.PropagateLightSource. Terrain
    // lighting is already baked in v_color; this value only decides whether
    // this light's shadow contribution is culled on its illuminated receiver.
    float localLight = abs(directionStrength.w);
    return vec4((1.0 - visibility) * localLight,
        visibility * localLight, localLight, 0.0);
}

vec4 samplePointLight()
{
    vec4 result = vec4(0.0);
    for (int i = 0; i < POINT_SHADOW_LIGHT_LIMIT; i++)
    {
        if (float(i) >= u_pointShadowCount)
            break;
        // Source: TerrainShadowShaders.cs:TerrainShadowShaders.samplePointLightFor;
        // no component can exceed this light's
        // strength, and result.z already dominates result.x and result.y.
        float contribution = abs(u_pointShadowLightDirectionStrength[i].w);
        if (contribution <= min(result.x, result.y))
            continue;
        result = max(result, samplePointLightFor(
            u_pointShadowOrigins[i],
            u_pointShadowLightPositionRadius[i],
            u_pointShadowLightDirectionStrength[i],
            u_pointShadowSlots[i], u_pointShadowValid[i]));
    }
    return result;
}
#endif
""";

    private const string TerrainPixelSuffix = """
void main()
{
    // Source: GLSL ES 1.00 derivatives — evaluate in uniform control flow, before alpha discard.
    vec3 receiverDx = dFdx(v_shadowCoord);
    vec3 receiverDy = dFdy(v_shadowCoord);
    vec4 result = v_color * texture2D(u_texture, v_texcoord);
#ifdef ALPHATESTED
    if (result.a <= u_alphaThreshold)
        discard;
#endif
    // Only derivatives must precede discard; transparent fragments need no plane reconstruction.
    vec3 receiverNormal = cross(receiverDx, receiverDy);
    vec3 receiverPlane = vec3(-receiverNormal.xy * sign(receiverNormal.z), abs(receiverNormal.z));
    // Source: Survivalcraft/Game/TerrainUpdater.PropagateLightSources and
    // BlockGeometryGenerator.SetupCubeVertexFace*.  Treat the original
    // terrain vertex color as the no-shadow scanline result.  Shadow masks
    // merge as a union (max darkness) instead of multiplying, while visible
    // lights keep their own brightness floor.
#ifdef POINT_SHADOWS
    vec4 pointLight = samplePointLight();
#else
    vec4 pointLight = vec4(0.0);
#endif
    float celestialOcclusion = sampleShadow(v_shadowCoord, v_shadowCoord.xy, receiverPlane) * v_celestialShadowVisibility;
    float celestialShadow = u_shadowStrength * celestialOcclusion;
    float celestialVisible = u_celestialLightFloor * (1.0 - celestialShadow);
    float bakedLight = max(max(v_color.r, v_color.g), v_color.b);
    float originalMax = max(bakedLight, max(u_celestialLightFloor, pointLight.z));
    float visibleMax = max(celestialVisible, pointLight.y);
    float unionShadow = max(celestialShadow, pointLight.x);
    float shadowMultiplier = 1.0 - unionShadow;
    float visibleLightFloor = originalMax > 0.001
        ? clamp(visibleMax / originalMax, 0.0, 1.0)
        : 1.0;
    result.rgb *= max(shadowMultiplier, visibleLightFloor);
    result.rgb = mix(result.rgb, u_fogColor * v_color.a, v_fog);
    gl_FragColor = result;
}
""";

    // Source: Survivalcraft/Game/TerrainVertex.cs:TerrainVertex.VertexDeclaration and
    // Survivalcraft/Game/TerrainRenderer.cs:DrawTerrainChunkGeometrySubsets
    public const string ShadowMapVertex = """
// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='COLOR' Attribute='a_color' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />

uniform vec2 u_shadowOrigin;
uniform mat4 u_shadowMatrix;

attribute vec3 a_position;
attribute vec4 a_color;
attribute vec2 a_texcoord;

varying vec2 v_texcoord;
varying float v_depth;
""" + VegetationMotionShaders.Shared + """
void main()
{
    vec3 movingPosition = vegetationPosition(a_position, a_color.a);
    v_texcoord = a_texcoord;
    vec4 position = u_shadowMatrix * vec4(
        movingPosition.x - u_shadowOrigin.x,
        movingPosition.y,
        movingPosition.z - u_shadowOrigin.y,
        1.0);
    v_depth = position.z / position.w;
    gl_Position = position;
    OPENGL_POSITION_FIX;
}
""";

    // Source: Survivalcraft/Game/TerrainRenderer.cs:TerrainRenderer.DrawAlphaTested
    public const string ShadowMapPixel = """
// <Sampler Name='u_samplerState' Texture='u_texture' />

#ifdef GL_ES
precision highp float;
#endif

uniform sampler2D u_texture;
uniform float u_alphaThreshold;

varying vec2 v_texcoord;
varying float v_depth;

void main()
{
    if (texture2D(u_texture, v_texcoord).a <= u_alphaThreshold)
        discard;
    float depth = clamp(v_depth, 0.0, 1.0);
    float scaled = depth * 255.0;
    float highPart = floor(scaled) / 255.0;
    float lowPart = fract(scaled);
    gl_FragColor = vec4(highPart, lowPart, 0.0, 1.0);
}
""";

    // Source: LightbulbBlock.GetEmittedLightAmount, LedElectricElement.OnAdded and
    // Engine/Matrix.cs:Matrix.CreatePerspectiveFieldOfView
    public const string PointShadowMapVertex = """
// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='COLOR' Attribute='a_color' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />

uniform vec2 u_shadowOrigin;
uniform mat4 u_shadowMatrix;
uniform vec3 u_lightPosition;
uniform float u_lightRadius;

attribute vec3 a_position;
attribute vec4 a_color;
attribute vec2 a_texcoord;

varying vec2 v_texcoord;
varying vec3 v_localPosition;

""" + VegetationMotionShaders.Shared + """
void main()
{
    vec3 movingPosition = vegetationPosition(a_position, a_color.a);
    v_texcoord = a_texcoord;
    vec3 position = vec3(
        movingPosition.x - u_shadowOrigin.x,
        movingPosition.y,
        movingPosition.z - u_shadowOrigin.y);
    v_localPosition = position;
    gl_Position = u_shadowMatrix * vec4(position, 1.0);
    OPENGL_POSITION_FIX;
}
""";

    public const string PointShadowMapPixel = """
// <Sampler Name='u_samplerState' Texture='u_texture' />

#ifdef GL_ES
precision highp float;
#endif

uniform sampler2D u_texture;
uniform float u_alphaThreshold;
uniform vec3 u_lightPosition;
uniform float u_lightRadius;

varying vec2 v_texcoord;
varying vec3 v_localPosition;

void main()
{
    if (texture2D(u_texture, v_texcoord).a <= u_alphaThreshold)
        discard;
    float depth = clamp(length(v_localPosition - u_lightPosition) / u_lightRadius, 0.0, 1.0);
    float scaled = depth * 255.0;
    float highPart = floor(scaled) / 255.0;
    float lowPart = fract(scaled);
    gl_FragColor = vec4(highPart, lowPart, 0.0, 1.0);
}
""";
}
