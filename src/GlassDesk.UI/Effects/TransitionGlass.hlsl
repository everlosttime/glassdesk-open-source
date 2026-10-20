// Lens profile/expanded-radius gradient adapted from Kyant0/AndroidLiquidGlass,
// Copyright 2025 Kyant, Apache-2.0. See docs/third-party/android-liquid-glass/NOTICE.md.
// Moving glass edge: geometry parameters arrive in the same WPF render frame as
// the shared clip. Input is a filtered, unmasked desktop texture in screen coordinates.
sampler2D Backdrop : register(s0);
float4 Panel : register(c0); // x,y,width,height, normalized to the backdrop
float4 Material : register(c1); // backdrop width/height DIP, radius DIP, strength
float4 Dispersion : register(c2); // strength ratio, inner gain ratio, outer gain ratio; no clarity
float4 Profile : register(c3); // x: large launchpad lens; Dock remains on the compact path

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 size = Material.xy;
    float2 panelSize = Panel.zw * size;
    float2 p = (uv - Panel.xy) * size;
    float2 distanceToSide = min(p, panelSize - p);
    float radius = max(.001, min(Material.z, min(panelSize.x, panelSize.y) * .5));
    float2 corner = saturate(1 - distanceToSide / radius);
    float2 powers = pow(max(corner, .00001), 2.6);
    float2 gradient = 2.6 * pow(max(corner, .00001), 1.6) / radius;
    float cornerDepth = (1 - powers.x - powers.y) / max(length(gradient), .00001);
    float depth = min(distanceToSide.x, distanceToSide.y);
    depth = (distanceToSide.x < radius && distanceToSide.y < radius) ? cornerDepth : depth;
    depth = max(0, depth);

    // Same expanded-radius lens gradient and displacement profile as the resting material.
    float2 center = p - panelSize * .5;
    float expandedRadius = min(radius * 1.5, min(panelSize.x, panelSize.y) * .5);
    float2 q = abs(center) - panelSize * .5 + expandedRadius;
    float2 g = max(q, 0);
    float2 straight = q.x >= q.y ? float2(1, 0) : float2(0, 1);
    float2 normal = ((q.x >= 0 || q.y >= 0) && length(g) > .00001) ? g / max(length(g), .00001) : straight;
    normal *= float2(center.x < 0 ? -1 : 1, center.y < 0 ? -1 : 1);
    float band = max(.001, min(24, panelSize.y * .5));
    float t = saturate(1 - depth / band);
    float displacement = -(1 - sqrt(max(0, 1 - t * t))) * 32 * Material.w;
    float2 lensOffset = displacement * normal;
    if (Profile.x > .5)
    {
        float2 axisBand = max(.001, min(48, panelSize * .5));
        float2 axisT = saturate(1 - distanceToSide / axisBand);
        float2 sideSign = float2(center.x < 0 ? -1 : 1, center.y < 0 ? -1 : 1);
        lensOffset = -(1 - sqrt(max(0, 1 - axisT * axisT))) * min(64, axisBand * (4.0 / 3.0)) * Material.w * sideSign;
        if (q.x >= 0 && q.y >= 0)
        {
            float cornerBand = min(axisBand.x, axisBand.y);
            float cornerT = saturate(1 - depth / cornerBand);
            float cornerOffset = -(1 - sqrt(max(0, 1 - cornerT * cornerT))) * min(64, cornerBand * (4.0 / 3.0)) * Material.w;
            lensOffset = lensOffset * .35 + cornerOffset * normal * .65;
        }
    }
    float2 lensUv = uv + lensOffset / size;
    float4 color = tex2D(Backdrop, saturate(lensUv));
    float displacementLength = Profile.x > .5 ? length(lensOffset) : abs(displacement);
    float2 dispersionNormal = Profile.x > .5 && displacementLength > .00001 ? -lensOffset / max(displacementLength, .00001) : normal;
    float refracting = saturate(displacementLength / 8);
    float curvature = saturate(2 * abs(normal.x * normal.y));
    float splitDip = min(.75, displacementLength * .035) * refracting * refracting * (.65 + .35 * curvature);
    float outerBlend = exp(-depth / 5);
    float regionGain = lerp(Dispersion.y, Dispersion.z, outerBlend);
    float2 splitUv = splitDip * Dispersion.x * regionGain * dispersionNormal / size;
    // Split only transmitted RGB. Green, coverage and bevel lighting remain unchanged.
    color.r = tex2D(Backdrop, saturate(lensUv + splitUv)).r;
    color.b = tex2D(Backdrop, saturate(lensUv - splitUv)).b;
    float bevel = pow(saturate(1 - depth / (Profile.x > .5 ? 18 : 10)), 2);
    float light = dot(normal, float2(-.6, -.8));
    float highlight = pow(max(0, light), 2) * .18 * bevel * Material.w;
    highlight += abs(normal.x + normal.y) * .35355339 * saturate(.75 - depth) * Material.w;
    float shade = pow(max(0, -light), 2) * .20 * bevel * Material.w;
    color.rgb = saturate(color.rgb * (1 - shade) + highlight * color.a);
    return color;
}
