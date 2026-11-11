#pragma once
// A synthetic source deliberately independent of the production material. It
// establishes alpha, transport and cadence before wallpaper/material migration.
inline constexpr char GD_CalibrationShader[] = R"hlsl(
cbuffer Frame : register(b0) { float4 viewportTimePattern; float4 settings; };
float4 VS(uint vertex : SV_VertexID) : SV_POSITION {
    return float4(vertex == 2 ? 3 : -1, vertex == 1 ? 3 : -1, 0, 1);
}
float4 PS(float4 position : SV_POSITION) : SV_TARGET {
    float2 uv = position.xy / viewportTimePattern.xy;
    if (viewportTimePattern.w < .5) {
        float a = uv.x < (1.0 / 3.0) ? 0 : uv.x < (2.0 / 3.0) ? .5 : 1;
        a *= settings.x;
        return float4(float3(.8, .4, .2) * a, a);
    }
    float2 size = viewportTimePattern.xy;
    float2 center = size * .5 + float2(sin(viewportTimePattern.z * 2) * size.x * .12, 0);
    float2 halfSize = size * .32;
    float radius = min(settings.y, min(halfSize.x, halfSize.y));
    float2 q = abs(position.xy - center) - halfSize + radius;
    float distance = length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
    float a = saturate(.5 - distance) * settings.x;
    float checker = fmod(floor(position.x / 24) + floor(position.y / 24), 2);
    float3 color = lerp(float3(.12, .35, .85), float3(.8, .3, .15), uv.x);
    color *= .8 + .2 * checker;
    return float4(color * a, a);
}
)hlsl";
