#pragma once
// Port of AndroidLiquidGlassRenderer's production prefilter. The existing WPF
// TransitionGlass shader still owns refraction/dispersion and moving silhouette.
// UNORM targets quantize after every pass, matching the legacy byte buffers.
constexpr char GD_StaticMaterialShader[] = R"hlsl(
Texture2D<float4> Input : register(t0);
cbuffer Filter : register(b0) { float4 Dimensions; float4 Material; };
float4 PS(float4 position : SV_Position) : SV_Target {
    int2 p = int2(position.xy);
    int2 maximum = int2(Dimensions.xy) - 1;
    float4 color = Input.Load(int3(p, 0));
    if (Material.w < .5) {
        float luminance = dot(color.rgb, float3(.213, .715, .072));
        color.rgb = saturate(luminance + (color.rgb - luminance) * (1 + .5 * Material.x));
    } else if (Material.w < 2.5) {
        int radius = int(Dimensions.z);
        int2 direction = Material.w < 1.5 ? int2(1,0) : int2(0,1);
        float4 sum = 0;
        [loop] for (int i = -radius; i <= radius; ++i)
            sum += Input.Load(int3(clamp(p + direction * i, int2(0,0), maximum), 0));
        // Legacy integer box rounds half up at each axis.
        color = floor(sum * 255 / (radius * 2 + 1) + .5) / 255;
    } else {
        color.rgb = color.rgb * (1 - Material.z) + Material.y * color.a * Material.z;
    }
    return color;
}
)hlsl";
