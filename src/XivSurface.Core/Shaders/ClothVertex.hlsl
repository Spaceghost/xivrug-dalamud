cbuffer Cloth : register(b0) {
    row_major float4x4 ViewProjection;
    row_major float4x4 InverseViewProjection;
    float4 Camera;
    float4 View;
    float4 Footprint;
    float4 Style;
    float4 MapCoordinates;
    float4 MapView;
    float4 RouteArea;
    float4 Material;
    float4 RouteColor;
    float4 RouteProgress;
    float4 RouteGuide;
    float4 RouteAhead;
    float4 Finish;
    float4 Foot0;
    float4 Foot1;
    float4 Foot2;
    float4 Foot3;
    float4 LightAmbient;
    float4 LightSun;
    float4 LightMoon;
    float4 LightDirection;
    float4 Wisp0;
    float4 Wisp1;
    float4 Wisp2;
    float4 NativeShadow; // mask dimensions, available, maximum receiver separation
};
struct Vertex { float3 Position : POSITION; float3 Normal : NORMAL; float2 UV : TEXCOORD0; float GroundMinimum : TEXCOORD1; float ContactCeiling : TEXCOORD2; };
struct Fragment { float4 Position : SV_POSITION; float3 World : TEXCOORD0; float3 Normal : TEXCOORD1; float2 UV : TEXCOORD2; };
Fragment main(Vertex input) {
    Fragment o;
    // CPU render preparation applies lift/flutter and current shared contact
    // bounds before deriving smooth shared-vertex normals. Do not displace
    // again here: normals and the uploaded positions describe the same pose.
    float3 world=input.Position;
    // Idempotent last safety clamp; depth and foot/floor invariants remain.
    if(LightDirection.w<.5) world.y=max(input.GroundMinimum+.001,min(world.y,input.ContactCeiling));
    o.Position=mul(float4(world,1),ViewProjection);
    o.World=world; o.Normal=input.Normal; o.UV=input.UV;
    return o;
}
