// Shared fallback for point lights that do not fit URP's native visible-light budget.
#ifndef WORLDSIDES_OVERFLOW_POINT_LIGHTS
#define WORLDSIDES_OVERFLOW_POINT_LIGHTS

// C# uploads center XYZ, inverse squared range in W, and intensity-scaled RGB.
int _WorldsidesOverflowCount;
float4 _WorldsidesOverflowPositions[128];
half4 _WorldsidesOverflowColors[128];

// Evaluate each uploaded light without shadow maps; only ground uses normal-based diffuse.
half3 WorldsidesOverflowLighting(float3 positionWS, bool ground)
{
    half3 result = 0;

    // Stop at the uploaded count rather than processing all 128 reserved array entries.
    [loop]
    for (int i = 0; i < _WorldsidesOverflowCount; i++)
    {
        // Inverse-square falloff brightens close pixels; clamp distance to avoid division by zero.
        float3 offset = _WorldsidesOverflowPositions[i].xyz - positionWS;
        float distanceSquared = max(dot(offset, offset), 0.0001);

        // Smoothly fade to zero at the light's range to avoid a visible hard ring.
        float rangeFactor = distanceSquared * _WorldsidesOverflowPositions[i].w;
        float smoothFactor = saturate(1 - rangeFactor * rangeFactor);

        // Ground faces upward; billboard sprites deliberately receive light from all directions.
        float diffuse = ground ? saturate(offset.y * rsqrt(distanceSquared)) : 1;
        result += _WorldsidesOverflowColors[i].rgb * (smoothFactor * smoothFactor / distanceSquared) * diffuse;
    }
    return result;
}
#endif
