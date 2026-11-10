namespace SuAPIShadows;

// Source: DawnFoliage.MarkPlant/MarkLeaves; TerrainShadowShaders.TerrainVertex,
// ShadowMapVertex and PointShadowMapVertex share this exact world-space displacement.
internal static class VegetationMotionShaders
{
    public const string Shared = """
uniform float u_vegetationEnabled;
uniform vec4 u_vegetationWind;
uniform vec4 u_vegetationView;
uniform vec4 u_vegetationSources[8];
uniform vec4 u_vegetationMotions[8];
uniform vec4 u_vegetationAges[8];

float vegetationCode(float alpha)
{
    return 255.0 - floor(alpha * 255.0 + 0.5);
}

float vegetationAlpha(float alpha)
{
    float code = vegetationCode(alpha);
    return code >= 1.0 && code <= 160.0 ? 1.0 : alpha;
}

vec3 vegetationPosition(vec3 worldPosition, float alpha)
{
    float code = vegetationCode(alpha);
    if (u_vegetationEnabled < 0.5 || code < 1.0 || code > 160.0)
        return worldPosition;
    float index = code - 1.0;
    float kind = floor(index / 32.0) + 1.0;
    float weight = mod(index, 32.0) / 31.0;
    if (weight <= 0.0)
        return worldPosition;
    bool leaves = kind > 3.5 && kind < 4.5;
    bool water = kind > 4.5;
    float limit = kind < 1.5 ? 0.10 :
        (kind < 2.5 ? 0.07 : (kind < 3.5 ? 0.04 : (leaves ? 0.03 : 0.025)));
    // Leaves use one continuous world-position field: coincident vertices on adjacent
    // faces/chunks cannot acquire different cell-local phases or tear at cell borders.
    float anchor = leaves ? 1.0 : weight * weight;
    vec2 displacement = vec2(0.0);
    if (!water)
    {
        vec2 direction = u_vegetationWind.xy;
        vec2 lateral = vec2(-direction.y, direction.x);
        float phase = dot(worldPosition.xz, vec2(0.071, 0.053)) + worldPosition.y * 0.017;
        // Integer temporal harmonics are also continuous at the shared 2*pi*1024 period.
        float gust = 0.68 + 0.20 * sin(u_vegetationWind.w + phase) +
            0.12 * sin(2.0 * u_vegetationWind.w + phase * 0.73);
        float flutter = sin(3.0 * u_vegetationWind.w + phase * 1.41);
        float altitude = 1.0 + 0.2 * clamp((worldPosition.y - 64.0) / 128.0, 0.0, 1.0);
        displacement = (direction * gust + lateral * (0.08 * flutter)) *
            (limit * clamp(u_vegetationWind.z, 0.0, 1.0) * altitude);
    }
    // Only the explicit finite source list participates. Water never receives the air
    // term, and leaves never receive a character/animal interaction term.
    vec3 viewDelta = worldPosition - u_vegetationView.xyz;
    if (!leaves && u_vegetationView.w > 0.0 && dot(viewDelta, viewDelta) < u_vegetationView.w * u_vegetationView.w)
    {
        for (int i = 0; i < 8; i++)
        {
            vec4 source = u_vegetationSources[i];
            vec4 motion = u_vegetationMotions[i];
            vec4 age = u_vegetationAges[i];
            bool waterSource = age.z > 0.5;
            if (water != waterSource || source.w <= 0.0)
                continue;
            vec3 delta = worldPosition - source.xyz;
            float distanceSquared = dot(delta, delta);
            if (abs(delta.y) > age.w || distanceSquared >= source.w * source.w)
                continue;
            if (age.x < 0.0 || age.y <= 0.0 || age.x >= age.y)
                continue;
            float distanceToSource = sqrt(distanceSquared);
            float fade = 1.0 - distanceToSource / source.w;
            fade *= fade * (1.0 - age.x / age.y);
            vec2 away = delta.xz / max(length(delta.xz), 0.05);
            if (water)
            {
                float wave = sin(age.x * 8.0 - distanceToSource * 3.0);
                displacement += (away * 0.75 + motion.xy * 0.25) *
                    (clamp(motion.z, 0.0, 0.025) * fade * wave);
            }
            else
            {
                displacement += (away * 0.45 + motion.xy * 0.55) *
                    (clamp(motion.z, 0.0, limit) * fade);
            }
        }
    }
    displacement *= anchor;
    float lengthSquared = dot(displacement, displacement);
    if (lengthSquared > limit * limit)
        displacement *= limit / sqrt(lengthSquared);
    return worldPosition + vec3(displacement.x, 0.0, displacement.y);
}
""";
}
