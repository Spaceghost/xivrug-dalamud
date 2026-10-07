// Emissive real world geometry, not a fullscreen overlay. RGB is premultiplied
// for ONE+ONE additive blending; destination alpha is preserved by blend state.
Texture2D<float> SceneDepth : register(t0);
cbuffer Music : register(b0) {
    row_major float4x4 ViewProjection;
    row_major float4x4 InverseViewProjection;
    float4 Camera;
    float4 View; // viewport width/height, actual scene depth width/height
};
struct Fragment { float4 Position : SV_POSITION; float3 World : TEXCOORD0; float4 Color : COLOR; };
float4 main(Fragment input) : SV_TARGET {
    float2 uv = input.Position.xy / View.xy;
    if(any(uv < 0) || any(uv >= 1)) discard;
    float sceneDepth = SceneDepth.Load(int3(min(uint2(uv * View.zw),uint2(View.zw)-1),0));
    if(!(sceneDepth >= 0 && sceneDepth <= 1)) discard;
    if(sceneDepth > 0 && sceneDepth < 1) {
        float4 h = mul(float4(uv.x*2-1,1-uv.y*2,sceneDepth,1),InverseViewProjection);
        if(!(abs(h.w) > 1e-7)) discard;
        float3 receiver = h.xyz / h.w;
        if(!all(abs(receiver) < 1e10)) discard;
        if(length(input.World-Camera.xyz) > length(receiver-Camera.xyz)+.001) discard;
    }
    float alpha = saturate(input.Color.a);
    if(alpha < .001) discard;
    return float4(saturate(input.Color.rgb) * alpha,0);
}
