cbuffer Music : register(b0) {
    row_major float4x4 ViewProjection;
    row_major float4x4 InverseViewProjection;
    float4 Camera;
    float4 View;
};
struct Vertex { float3 Position : POSITION; float4 Color : COLOR; };
struct Fragment { float4 Position : SV_POSITION; float3 World : TEXCOORD0; float4 Color : COLOR; };
Fragment main(Vertex input) {
    Fragment output;
    output.Position = mul(float4(input.Position, 1), ViewProjection);
    output.World = input.Position;
    output.Color = input.Color;
    return output;
}
