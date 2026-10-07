// Independent surface-decal pixel shader prototype, not wired into the game.
// Submit BEFORE native UI with premultiplied blending (ONE, INV_SRC_ALPHA).
// All inputs must describe the same frame/viewport. No guessed resource slots
// or hard-coded game offsets are part of this shader's contract.
Texture2D<float> SceneDepth : register(t0);
// Decoded world-space normal in xyz; w=1 only for positively identified floor.
// This is an adapter-produced texture, NOT an assumed FFXIV gbuffer format.
Texture2D<float4> FloorNormal : register(t1);
// Selected navmesh layer only, world-XZ footprint space: x=height, y=valid.
// A producer must not interpolate across holes, layers, or unreachable areas.
Texture2D<float2> SelectedFloor : register(t2);
Texture2D<float4> MapColor : register(t3);
SamplerState MapSampler : register(s0);

cbuffer Surface : register(b0) {
    row_major float4x4 InverseViewProjection;
    float2 ViewOrigin;
    float2 ViewSize;
    uint2 DepthOrigin;
    uint2 DepthSize;
    uint2 FloorMaskSize;
    uint FrameValidated; // supplied only by the validated before-UI backend
    uint Shape; // 0=circle, 1=rounded rectangle
    float2 CenterXZ;
    float2 HalfSize;
    float2 RotationCS; // cos,sin; CPU validated, unit length
    float CornerRadius;
    float Feather;
    float ClearDepth;
    float FloorTolerance;
    float MinimumNormalY;
    float Opacity;
    float4 RugGeometry; // border width, inward fringe length, threads/yalm, animate
    float4 RugMaterial; // time [0,16), detail strength, enabled, reserved
};

// vkd3d's HLSL frontend does not implement isfinite. Ordered comparison rejects
// both NaN and infinity without depending on that intrinsic.
bool finite1(float x) { return abs(x) <= 3.402823466e+38; }
bool finite2(float2 x) { return all(abs(x) <= 3.402823466e+38); }
bool finite3(float3 x) { return all(abs(x) <= 3.402823466e+38); }
bool finite4(float4 x) { return all(abs(x) <= 3.402823466e+38); }

// Arc length around the footprint, continuous at rounded corners and the seam.
float edgeArc(float2 p, out float perimeter) {
    if (Shape == 0) {
        perimeter = 6.283185307 * HalfSize.x;
        return (atan2(p.y, p.x) + 3.141592654) * HalfSize.x;
    }
    float2 b = HalfSize - CornerRadius;
    float2 a = abs(p);
    float quarter = b.x + b.y + 1.570796327 * CornerRadius;
    float arc;
    if (a.y <= b.y) arc = a.y;
    else if (a.x <= b.x) arc = b.y + 1.570796327 * CornerRadius + b.x - a.x;
    else arc = b.y + CornerRadius * atan2(a.y - b.y, a.x - b.x);
    perimeter = 4 * quarter;
    if (p.x < 0) arc = 2 * quarter - arc;
    if (p.y < 0) arc = 4 * quarter - arc;
    return arc;
}

float4 main(float4 screen : SV_POSITION) : SV_Target {
    if (FrameValidated != 1 || Shape > 1 || any(ViewSize <= 0)
        || any(DepthSize == 0) || any(FloorMaskSize == 0) || any(HalfSize <= 0)) discard;
    float2 viewUV = (screen.xy - ViewOrigin) / ViewSize;
    if (any(viewUV < 0) || any(viewUV >= 1)) discard;
    int2 texel = int2(DepthOrigin + min(uint2(viewUV * DepthSize), DepthSize - 1));
    float z = SceneDepth.Load(int3(texel, 0));
    if (!finite1(z) || z < 0 || z > 1 || z == ClearDepth) discard;
    float4 h = mul(float4(2 * viewUV.x - 1, 1 - 2 * viewUV.y, z, 1), InverseViewProjection);
    if (!finite4(h) || h.w <= 1e-7) discard;
    float3 world = h.xyz / h.w;
    if (!finite3(world)) discard;
    float4 floorNormal = FloorNormal.Load(int3(texel, 0));
    float len = length(floorNormal.xyz);
    if (!finite4(floorNormal) || floorNormal.w < 1 || len < 1e-6
        || floorNormal.y / len < MinimumNormalY) discard;

    float2 offset = world.xz - CenterXZ;
    float2 local = float2(dot(offset, RotationCS),
        dot(offset, float2(-RotationCS.y, RotationCS.x)));
    float2 uv = local / (2 * HalfSize) + 0.5;
    if (any(uv < 0) || any(uv >= 1)) discard;
    float2 floor = SelectedFloor.Load(int3(min(uint2(uv * FloorMaskSize), FloorMaskSize - 1), 0));
    if (!finite2(floor) || floor.y < 1 || abs(world.y - floor.x) > FloorTolerance) discard;
    float distance;
    if (Shape == 0) distance = length(local) - HalfSize.x;
    else {
        float2 q = abs(local) - HalfSize + CornerRadius;
        distance = length(max(q, 0)) + min(max(q.x, q.y), 0) - CornerRadius;
    }
    // Derivative-based detail suppression: subpixel threads become their mean
    // color rather than shimmering. No extra texture fetches or simulation.
    float pixelSize = max(length(ddx(local)), length(ddy(local)));
    if (distance >= 0) discard;
    float t = Feather > 0 ? saturate(-distance / Feather) : 1;
    float coverage = t * t * (3 - 2 * t);
    float4 color = MapColor.Sample(MapSampler, uv);
    if (RugMaterial.z > 0.5) {
        float inward = -distance;
        float border = RugGeometry.x;
        float fringe = RugGeometry.y;
        float aa = max(pixelSize, 0.0005);
        float perimeter;
        float arc = edgeArc(local, perimeter);
        float tufts = max(4, floor(perimeter * 12 + 0.5));
        float phase = arc / max(perimeter, 0.001) * tufts;
        float outer = saturate(1 - inward / fringe);
        // Tangential yarn sway, pinned at the binding. Changes fiber coverage,
        // NOT floor height. Integer spatial frequency closes the perimeter seam.
        float sway = RugGeometry.w * 0.10 * outer * outer
            * sin(RugMaterial.x * 0.392699082 + phase * 6.283185307);
        float strand = abs(frac(phase + sway) - 0.5);
        float strandAA = max(aa * tufts / max(perimeter, 0.001), 0.015);
        float fiber = 1 - smoothstep(0.25 - strandAA, 0.25 + strandAA, strand);
        float lod = saturate(1 - aa * tufts / max(perimeter, 0.001));
        fiber = lerp(0.5, fiber, lod);
        float tip = smoothstep(0, max(aa, fringe * 0.12), inward);
        float body = smoothstep(fringe - aa, fringe + aa, inward);
        coverage = lerp(fiber * tip, 1, body);
        float edgeBand = 1 - smoothstep(fringe + border - aa, fringe + border + aa, inward);
        float weaveLOD = saturate(1 - pixelSize * RugGeometry.z * 2);
        float weave = sin(local.x * RugGeometry.z * 6.283185307)
                    * sin(local.y * RugGeometry.z * 6.283185307) * weaveLOD;
        float braid = sin(phase * 6.283185307 + inward / border * 12.566370614) * lod;
        float3 binding = lerp(float3(0.10, 0.17, 0.23), float3(0.72, 0.54, 0.29), 0.5 + 0.5 * braid);
        float3 yarn = float3(0.88, 0.81, 0.65) * (0.94 + 0.06 * braid);
        float3 trim = lerp(yarn, binding, body);
        color.rgb = lerp(color.rgb * (1 + weave * RugMaterial.y), trim, edgeBand);
        color.a = lerp(color.a, 1, edgeBand);
    }
    float alpha = saturate(color.a * Opacity * coverage);
    return float4(color.rgb * alpha, alpha);
}
