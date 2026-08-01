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
uniform float u_fogShadowFactor;

attribute vec3 a_position;
attribute vec4 a_color;
attribute vec2 a_texcoord;

varying vec4 v_color;
varying vec2 v_texcoord;
varying float v_fog;
varying vec4 v_shadowPosition;
varying vec3 v_worldPosition;
varying float v_celestialShadowVisibility;

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
    v_texcoord = a_texcoord;
    v_color = a_color;
    v_fog = calculateFog(a_position);
    v_shadowPosition = u_shadowMatrix * vec4(
        a_position.x - u_shadowOrigin.x,
        a_position.y,
        a_position.z - u_shadowOrigin.y,
        1.0);
    v_worldPosition = a_position;
    // Source: SubsystemSky.ViewFogBottom/ViewFogTop; fog blocks celestial shadows below its
    // layer, fades through the layer and leaves terrain above it unaffected.
    v_celestialShadowVisibility = mix(
        1.0,
        smoothstep(u_fogBottomTopDensity.x, u_fogBottomTopDensity.y, a_position.y),
        u_fogShadowFactor);
    gl_Position = u_viewProjectionMatrix * vec4(
        a_position.x - u_origin.x,
        a_position.y,
        a_position.z - u_origin.y,
        1.0);
    OPENGL_POSITION_FIX;
}
""";

    // Source: Pak/Shaders/Opaque.fsh and Engine/Graphics/SamplerState.LinearClamp
    public const string TerrainPixel = """
// <Sampler Name='u_samplerState' Texture='u_texture' />
// <Sampler Name='u_shadowSamplerState' Texture='u_shadowTexture' />
// <Sampler Name='u_pointShadowSamplerState' Texture='u_pointShadowTexture' />

#ifdef GL_ES
precision highp float;
#endif

uniform sampler2D u_texture;
uniform sampler2D u_shadowTexture;
uniform sampler2D u_pointShadowTexture;
uniform vec3 u_fogColor;
uniform float u_shadowEnabled;
uniform float u_shadowStrength;
uniform float u_celestialLightFloor;
uniform vec2 u_shadowTexelSize;
uniform float u_pointShadowEnabled;
uniform float u_pointShadowCount;
uniform vec2 u_pointShadowOrigins[10];
uniform vec4 u_pointShadowLightPositionRadius[10];
uniform vec4 u_pointShadowLightDirectionStrength[10];
uniform vec2 u_pointShadowTexelSize;
#ifdef ALPHATESTED
uniform float u_alphaThreshold;
#endif

varying vec4 v_color;
varying vec2 v_texcoord;
varying float v_fog;
varying vec4 v_shadowPosition;
varying vec3 v_worldPosition;
varying float v_celestialShadowVisibility;

float unpackDepth(vec4 packedDepth)
{
    return min(packedDepth.r + packedDepth.g / 255.0, 1.0);
}

float sampleShadow()
{
    if (u_shadowEnabled < 0.5)
        return 0.0;
    vec3 shadow = v_shadowPosition.xyz / v_shadowPosition.w;
    vec2 uv = vec2(0.5 + 0.5 * shadow.x, 0.5 - 0.5 * shadow.y);
    float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
    if (edge <= 0.0 || shadow.z <= 0.0 || shadow.z >= 1.0)
        return 0.0;

    float receiverDepth = shadow.z - 0.0018;
    // Four bilinear PCF samples keep the same fetch count as the previous point-filtered path,
    // but remove the hard texel transitions that shimmer while the view is moving.
    vec2 offset = 0.75 * u_shadowTexelSize;
    float lit = 0.0;
    lit += step(receiverDepth, unpackDepth(texture2D(
        u_shadowTexture, uv + vec2(-offset.x, -offset.y))));
    lit += step(receiverDepth, unpackDepth(texture2D(
        u_shadowTexture, uv + vec2(offset.x, -offset.y))));
    lit += step(receiverDepth, unpackDepth(texture2D(
        u_shadowTexture, uv + vec2(-offset.x, offset.y))));
    lit += step(receiverDepth, unpackDepth(texture2D(
        u_shadowTexture, uv + vec2(offset.x, offset.y))));
    return (1.0 - 0.25 * lit) * smoothstep(0.0, 0.08, edge);
}

vec3 samplePointLightFor(
    vec2 origin,
    vec4 positionRadius,
    vec4 directionStrength,
    float rowOffset)
{
    if (u_pointShadowEnabled < 0.5)
        return vec3(0.0);
    vec3 direction = vec3(
        v_worldPosition.x - origin.x,
        v_worldPosition.y,
        v_worldPosition.z - origin.y) - positionRadius.xyz;
    vec3 absoluteDirection = abs(direction);
    float distanceToLight = length(direction);
    if (distanceToLight <= 0.08 || distanceToLight >= positionRadius.w)
        return vec3(0.0);
    float directionalMask = 1.0;
    if (directionStrength.w > 0.0)
    {
        float cosine = dot(direction / distanceToLight, directionStrength.xyz);
        directionalMask = step(0.0, cosine);
        if (directionalMask <= 0.0)
            return vec3(0.0);
    }

    vec2 projected;
    vec2 tile;
    float majorAxis;
    if (absoluteDirection.x >= absoluteDirection.y &&
        absoluteDirection.x >= absoluteDirection.z)
    {
        majorAxis = absoluteDirection.x;
        if (direction.x >= 0.0)
        {
            projected = vec2(direction.z, -direction.y) / majorAxis;
            tile = vec2(0.0, 0.0);
        }
        else
        {
            projected = vec2(-direction.z, -direction.y) / majorAxis;
            tile = vec2(1.0, 0.0);
        }
    }
    else if (absoluteDirection.y >= absoluteDirection.z)
    {
        majorAxis = absoluteDirection.y;
        if (direction.y >= 0.0)
        {
            projected = vec2(direction.x, -direction.z) / majorAxis;
            tile = vec2(2.0, 0.0);
        }
        else
        {
            projected = vec2(-direction.x, -direction.z) / majorAxis;
            tile = vec2(0.0, 1.0);
        }
    }
    else
    {
        majorAxis = absoluteDirection.z;
        if (direction.z >= 0.0)
        {
            projected = vec2(-direction.x, -direction.y) / majorAxis;
            tile = vec2(1.0, 1.0);
        }
        else
        {
            projected = vec2(direction.x, -direction.y) / majorAxis;
            tile = vec2(2.0, 1.0);
        }
    }

    vec2 localUv = 0.5 + 0.5 * projected;
    vec2 localPadding = vec2(
        4.5 * u_pointShadowTexelSize.x,
        3.0 * u_pointShadowTexelSize.y);
    localUv = clamp(localUv, localPadding, vec2(1.0) - localPadding);
    tile.y += rowOffset;
    vec2 atlasUv = (tile + localUv) / vec2(3.0, 20.0);
    float receiverDepth = distanceToLight / positionRadius.w - 0.012;
    vec2 offset = 0.65 * u_pointShadowTexelSize;
    float lit = 0.0;
    lit += step(receiverDepth, unpackDepth(texture2D(
        u_pointShadowTexture, atlasUv + vec2(-offset.x, -offset.y))));
    lit += step(receiverDepth, unpackDepth(texture2D(
        u_pointShadowTexture, atlasUv + vec2(offset.x, -offset.y))));
    lit += step(receiverDepth, unpackDepth(texture2D(
        u_pointShadowTexture, atlasUv + vec2(-offset.x, offset.y))));
    lit += step(receiverDepth, unpackDepth(texture2D(
        u_pointShadowTexture, atlasUv + vec2(offset.x, offset.y))));
    // Source: Survivalcraft/Game/TerrainUpdater.PropagateLightSource. Terrain
    // lighting is already baked in v_color; this value only decides whether
    // this light's shadow contribution is culled on its illuminated receiver.
    float localLight = abs(directionStrength.w) * directionalMask;
    float visibility = 0.25 * lit;
    return vec3((1.0 - visibility) * localLight,
        visibility * localLight,
        localLight);
}

vec3 samplePointLight()
{
    vec3 result = vec3(0.0);
    for (int i = 0; i < 10; i++)
    {
        if (float(i) >= u_pointShadowCount)
            continue;
        result = max(result, samplePointLightFor(
            u_pointShadowOrigins[i],
            u_pointShadowLightPositionRadius[i],
            u_pointShadowLightDirectionStrength[i],
            2.0 * float(i)));
    }
    return result;
}

void main()
{
    vec4 result = v_color * texture2D(u_texture, v_texcoord);
#ifdef ALPHATESTED
    if (result.a <= u_alphaThreshold)
        discard;
#endif
    // Source: Survivalcraft/Game/TerrainUpdater.PropagateLightSources and
    // BlockGeometryGenerator.SetupCubeVertexFace*.  Treat the original
    // terrain vertex color as the no-shadow scanline result.  Shadow masks
    // merge as a union (max darkness) instead of multiplying, while visible
    // lights keep their own brightness floor.
    vec3 pointLight = samplePointLight();
    float celestialOcclusion = sampleShadow() * v_celestialShadowVisibility;
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
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />

uniform vec2 u_shadowOrigin;
uniform mat4 u_shadowMatrix;

attribute vec3 a_position;
attribute vec2 a_texcoord;

varying vec2 v_texcoord;
varying float v_depth;
void main()
{
    v_texcoord = a_texcoord;
    vec4 position = u_shadowMatrix * vec4(
        a_position.x - u_shadowOrigin.x,
        a_position.y,
        a_position.z - u_shadowOrigin.y,
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
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />

uniform vec2 u_shadowOrigin;
uniform mat4 u_shadowMatrix;
uniform vec3 u_lightPosition;
uniform float u_lightRadius;

attribute vec3 a_position;
attribute vec2 a_texcoord;

varying vec2 v_texcoord;
varying float v_depth;

void main()
{
    v_texcoord = a_texcoord;
    vec3 position = vec3(
        a_position.x - u_shadowOrigin.x,
        a_position.y,
        a_position.z - u_shadowOrigin.y);
    v_depth = length(position - u_lightPosition) / u_lightRadius;
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
}
