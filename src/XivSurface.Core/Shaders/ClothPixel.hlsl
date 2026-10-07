// Continuous, folded world geometry. Only the OUTER material outline is cut;
// obstacle response belongs to the deformed cloth mesh, never an interior mask.
Texture2D<float> SceneDepth : register(t0);
Texture2D<float4> GameMap : register(t1);
Texture2D<float4> MapBackground : register(t2);
Texture2D<float4> RouteField : register(t3);
Texture2D<float> NativeShadowMask : register(t4);
SamplerState MapSampler : register(s0);
cbuffer Cloth : register(b0) {
    row_major float4x4 ViewProjection;
    row_major float4x4 InverseViewProjection;
    float4 Camera;
    float4 View; // viewport width/height, scene depth width/height
    float4 Footprint; // undeformed centre x/z and half size x/z
    float4 Style; // circle0/rounded rectangle1, corner radius, hem enabled, opacity
    float4 MapCoordinates; // map world offset x/z and world-to-UV scales
    float4 MapView; // map world centre x/z, yalms per rug yalm, enabled/background
    float4 RouteArea; // route raster centre x/z and half size x/z
    float4 Material; // clock, motion, route enabled, route grid size
    float4 RouteColor; // shared Wayfinder trail RGBA
    float4 RouteProgress; // player, guide, look-ahead arcs; visual metadata enabled
    float4 RouteGuide; // guide world position; enabled
    float4 RouteAhead; // look-ahead world position; enabled
    float4 Finish; // optional wear 0..1; reserved
    float4 Foot0;
    float4 Foot1;
    float4 Foot2;
    float4 Foot3;
    float4 LightAmbient;
    float4 LightSun;
    float4 LightMoon;
    float4 LightDirection; // celestial direction; w=1 only for floating wisps
    float4 Wisp0;
    float4 Wisp1;
    float4 Wisp2;
    float4 NativeShadow; // mask dimensions, available, maximum receiver separation
};
struct Fragment { float4 Position : SV_POSITION; float3 World : TEXCOORD0; float3 Normal : TEXCOORD1; float2 UV : TEXCOORD2; };
struct Output { float4 Color : SV_TARGET; float Depth : SV_DEPTH; };
float filterYarn(float2 p) { return 1-smoothstep(0.3,0.75,max(fwidth(p.x),fwidth(p.y))); }
float filterThread(float p) { return 1-smoothstep(.28,.65,fwidth(p)); }
float wearHash(float2 p) { return frac(sin(dot(p,float2(127.1,311.7)))*43758.5453); }
float wearNoise(float2 p) {
    float2 cell=floor(p),f=frac(p); f=f*f*(3-2*f);
    return lerp(lerp(wearHash(cell),wearHash(cell+float2(1,0)),f.x),
        lerp(wearHash(cell+float2(0,1)),wearHash(cell+1),f.x),f.y);
}
float3 wispLight(float3 world,float3 normal,float4 wisp,float3 tint) {
    float3 offset=wisp.xyz-world;
    float distance2=dot(offset,offset);
    float facing=saturate(dot(normal,offset*rsqrt(max(distance2,.0001))));
    return tint*wisp.w*.065*exp(-distance2/.22)*facing;
}
Output main(Fragment input) {
    float2 screenUV=input.Position.xy/View.xy;
    float sceneDepth=SceneDepth.Load(int3(min(uint2(screenUV*View.zw),uint2(View.zw)-1),0));
    float3 nativeReceiver=0;
    bool hasNativeReceiver=false;
    if(sceneDepth>0 && sceneDepth<1) {
        float4 h=mul(float4(screenUV.x*2-1,1-screenUV.y*2,sceneDepth,1),InverseViewProjection);
        if(abs(h.w)>1e-7) {
            nativeReceiver=h.xyz/h.w;
            hasNativeReceiver=all(abs(nativeReceiver)<1e10);
            if(hasNativeReceiver && length(input.World-Camera.xyz)>length(nativeReceiver-Camera.xyz)+0.001) discard;
        }
    }
    if(LightDirection.w>.5) {
        float2 p=input.UV*2-1;
        float r2=dot(p,p);
        float halo=exp(-r2*5.5),core=exp(-r2*72);
        float intensity=max(input.Normal.r,max(input.Normal.g,input.Normal.b));
        float alpha=(halo*.16+core*.68)*intensity;
        if(r2>1 || alpha<.008) discard;
        Output fairy;
        fairy.Color=float4(input.Normal*(.7+core*.3),alpha);
        float distance=length(input.World-Camera.xyz);
        fairy.Depth=distance/(1+distance);
        return fairy;
    }
    float2 local=(input.UV-.5)*2*Footprint.zw;
    float edge;
    if(Style.x<.5) edge=length(local)-Footprint.z;
    else {
        float2 q=abs(local)-Footprint.zw+Style.y;
        edge=length(max(q,0))+min(max(q.x,q.y),0)-Style.y;
    }
    if(edge>=0) discard;
    float inward=-edge;
    float aa=max(fwidth(edge),.001);
    float alpha=Style.w*smoothstep(0,aa,inward);
    // Small yarns carry the material. Filter the two directions separately so
    // oblique views stay soft instead of producing a large checker or moire.
    float2 yarn=local*float2(78,93);
    float yarnLOD=filterYarn(yarn);
    float warp=sin(yarn.x*6.2831853),weft=sin(yarn.y*6.2831853);
    float coarse=warp*.012*filterThread(yarn.x)+weft*.010*filterThread(yarn.y);
    float2 fineYarn=local*float2(251,283);
    float fine=(sin(fineYarn.x*6.2831853)*filterThread(fineYarn.x)
        +sin(fineYarn.y*6.2831853)*filterThread(fineYarn.y))*.004;
    float slub=(wearNoise(local*17)-.5)*.011+(wearNoise(local*47)-.5)*.004;

    float3 flax=float3(.66,.59,.43);
    float3 color=lerp(float3(.055,.19,.20),float3(.095,.30,.29),.5+.3*sin(length(local)*5));
    float angle=atan2(local.y,local.x);
    float medallion=1-smoothstep(.018,.018+aa,abs(length(local)-(.48+.09*cos(angle*8))));
    color=lerp(color,float3(.75,.56,.26),medallion*.55);
    float2 mapWorld=MapView.xy+local*MapView.z;
    if(MapView.w>.5) {
        float2 mapUV=(mapWorld+MapCoordinates.xy)*MapCoordinates.zw+.5;
        color=flax;
        if(all(mapUV>=0) && all(mapUV<=1)) {
            float4 ink=GameMap.SampleGrad(MapSampler,mapUV,ddx(mapUV),ddy(mapUV));
            if(MapView.w>1.5) {
                float4 backing=MapBackground.SampleGrad(MapSampler,mapUV,ddx(mapUV),ddy(mapUV));
                ink=float4(ink.rgb*backing.rgb,backing.a);
            }
            // Pigment remains part of the material: subdued saturation, warm
            // yarn beneath, and all lighting/weave applied after cartography.
            float luminance=dot(ink.rgb,float3(.2126,.7152,.0722));
            float3 dyed=lerp(ink.rgb,luminance.xxx,.18)*float3(.96,.90,.79);
            color=lerp(flax,dyed,.87*ink.a);
        }
    }

    float wear=saturate(Finish.x),wearPatches=0,scuff=0;
    if(wear>0) {
        wearPatches=.65*wearNoise(local*1.7)+.35*wearNoise(local*6.1);
        scuff=(1-smoothstep(.025,.22,inward))*smoothstep(.28,.74,wearPatches);
        float luminance=dot(color,float3(.2126,.7152,.0722));
        float3 faded=lerp(color,luminance*float3(1.07,1.01,.90),.35+.2*wearPatches);
        faded=lerp(faded,float3(.76,.71,.59),.08*wearPatches+.30*scuff);
        // Material wear is under the route, with reduced strength on map ink.
        color=lerp(color,faded,wear*(.3+.7*scuff)*(MapView.w>.5 ? .35 : 1));
    }
    if(Material.z>.5 && all(RouteArea.zw>0)) {
        float2 routeWorld=MapView.w>.5 ? mapWorld : Footprint.xy+local;
        float2 routeUV=(routeWorld-RouteArea.xy)/(2*RouteArea.zw)+.5;
        if(all(routeUV>=0) && all(routeUV<=1)) {
            float4 path=RouteField.Load(int3(int2(routeUV*(Material.w-1)+.5),0));
            float scale=MapView.w>.5 ? MapView.z : 1;
            float width=.035;
            float pathAA=max(aa,max(RouteArea.z,RouteArea.w)*2/(Material.w-1)/scale*.35);
            float ribbon=1-smoothstep(width-pathAA,width+pathAA,path.x/scale);
            float pulse=.5+.5*sin(path.y/max(scale,1)*8-Material.x*2*Material.y);
            float aheadMask=RouteProgress.w>.5 ? smoothstep(-.5,.2,path.y-RouteProgress.x) : 1;
            float3 tint=saturate(RouteColor.rgb);
            float visible=path.w*RouteColor.a*((MapView.w>.5 || abs(input.World.y-path.z)<1.25) ? 1 : 0);
            color=lerp(color,lerp(tint,float3(1,1,1),pulse*.3),ribbon*visible*aheadMask*.85);
            float guideDistance=length(routeWorld-RouteGuide.xz)/scale;
            float nextDistance=length(routeWorld-RouteAhead.xz)/scale;
            float pool=exp(-guideDistance*guideDistance/.045)*RouteGuide.w*.20
                +exp(-nextDistance*nextDistance/.065)*RouteAhead.w*.12;
            color=lerp(color,lerp(tint,float3(1,1,1),.25),pool*visible);
        }
    }
    // A close, warm bound edge rather than a dark frame. The gold thread is
    // tucked just inside the binding and inherits the cloth's lighting.
    float hem=Style.z*(1-smoothstep(.018,.032,inward));
    float stitchLine=1-smoothstep(.0025,.0025+aa,abs(inward-.021));
    float perimeter=Style.x<.5 ? angle*Footprint.z :
        (abs(local.x)/Footprint.z>abs(local.y)/Footprint.w ? local.y : local.x);
    float fringe=Style.z*(1-smoothstep(.045,.085,inward));
    float threads=perimeter*24+.12*Material.y*sin(perimeter*3-Material.x*1.7);
    float strandDistance=abs(frac(threads)-.5);
    float strandAA=max(fwidth(threads),.035);
    float strands=1-smoothstep(.24-strandAA,.24+strandAA,strandDistance);
    alpha*=lerp(1,strands,fringe);
    color=lerp(color,lerp(float3(.81,.74,.59),flax,wear*.55)*(1+.08*sin(threads*25)),fringe*.8);
    float stitchLOD=filterThread(perimeter*33);
    float stitch=(.70+.30*sin(perimeter*207.345)*stitchLOD)*stitchLine*Style.z;
    float3 hemColor=lerp(float3(.48,.36,.22),float3(.57,.48,.34),wear*scuff*.55);
    float hemThread=sin(perimeter*439.823)*filterThread(perimeter*70)*.025;
    color=lerp(color,hemColor*(1+hemThread*(1-wear*.5)),hem*.78);
    float3 stitchColor=lerp(float3(.83,.69,.43),float3(.76,.71,.59),wear*(.35+.5*wearPatches));
    color=lerp(color,stitchColor,stitch*(.58-wear*scuff*.12));
    // A narrow seam valley and a soft rim highlight support the actual raised
    // surface without a wide frame or an artificial internal hole.
    // pow(negative, 2) is undefined in HLSL and produced a black NaN band
    // under vkd3d. Square the signed seam distances explicitly.
    float shadowDistance=(inward-.034)/.009,lightDistance=(inward-.010)/.007;
    float seamShadow=exp(-shadowDistance*shadowDistance)*Style.z;
    float seamLight=exp(-lightDistance*lightDistance)*Style.z;
    color*=1+(coarse+fine)*(1-wear*.35)+slub-seamShadow*.045+seamLight*.035;

    float3 dx=ddx(input.World),dy=ddy(input.World);
    float3 surfaceNormal=cross(dx,dy);
    // Uploaded normals are area-weighted shared-vertex normals of the FINAL
    // contact-constrained pose. Replacing them with ddx/ddy face normals made
    // every triangle visibly flat-shaded, even after visual subdivision.
    float3 n=input.Normal;
    // Bounded comparisons also reject NaN/Inf; the pinned HLSL compiler
    // does not implement isfinite(). Real smooth normals have unit length.
    if(!all(abs(n)<1e10) || dot(n,n)<=1e-12)
        n=all(abs(surfaceNormal)<1e10) && dot(surfaceNormal,surfaceNormal)>1e-12
            ? surfaceNormal : float3(0,1,0);
    n=normalize(n);
    if(n.y<0) n=-n;
    float3 clothNormal=n;
    float2 ux=ddx(input.UV),uy=ddy(input.UV);
    float3 tangent=dx*uy.y-dy*ux.y;
    tangent-=n*dot(tangent,n);
    float tangentLength=length(tangent);
    if(tangentLength>1e-6) {
        tangent/=tangentLength;
        float3 bitangent=normalize(cross(n,tangent));
        float2 bump=float2(cos(yarn.x*6.2831853),cos(yarn.y*6.2831853))*.025*yarnLOD;
        n=normalize(n+tangent*bump.x+bitangent*bump.y);
    }
    float3 viewDirection=normalize(Camera.xyz-input.World);
    float3 direction=normalize(LightDirection.xyz);
    // The native mask already includes the game's filtered cast shadows.
    // It belongs to the visible scene receiver, so use it only for cloth near
    // that same surface. Never project an unrelated foreground/ground shadow
    // onto airborne cloth. Ambient sky light remains when direct light is blocked.
    float visibility=1;
    if(NativeShadow.z>.5 && hasNativeReceiver
        && length(nativeReceiver-input.World)<=NativeShadow.w)
        visibility=saturate(NativeShadowMask.Load(int3(min(uint2(screenUV*NativeShadow.xy),uint2(NativeShadow.xy)-1),0)));
    float3 illumination=LightAmbient.rgb*(.7+.3*saturate(n.y))
        +(LightSun.rgb*saturate(dot(n,direction))+LightMoon.rgb*saturate(dot(n,-direction)))*visibility;
    float3 localLight=wispLight(input.World,n,Wisp0,float3(1,.82,.46))
        +wispLight(input.World,n,Wisp1,float3(.52,.82,1))
        +wispLight(input.World,n,Wisp2,float3(1,.82,.46));
    float fiberRim=pow(saturate(1-abs(dot(n,viewDirection))),4)*.045;
    // Gentle valley darkening reads folds as cloth, while preserving map ink.
    float foldShade=lerp(.88,1,saturate(abs(clothNormal.y)));
    // All yarn, dye, hem and sheen inherit the current environment. Only tiny
    // nearby fairy pools add light; there is no blanket self-lit fabric term.
    color=color*(min(illumination,1.25)+localLight)*foldShade
        +flax*fiberRim*min(illumination,1);
    Output o;
    o.Color=float4(color,alpha);
    float distance=length(input.World-Camera.xyz);
    o.Depth=distance/(1+distance);
    return o;
}
