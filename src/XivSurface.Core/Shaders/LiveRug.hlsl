// Live depth-reconstructed rug. Native pre-UI pass or optional legacy ImGui
// background pass. No flat world plane or displaced scene geometry.
Texture2D<float> SceneDepth : register(t1);
Texture2D<float2> FloorHeights : register(t2);
Texture2D<float4> RouteField : register(t3);
Texture2D<float4> GameMap : register(t4);
Texture2D<float4> MapBackground : register(t5);
Texture2D<float4> FloorTriangles : register(t6);
Texture2D<float4> FloorTriangleIndices : register(t7);
SamplerState MapSampler : register(s0);
cbuffer Rug : register(b0) {
    row_major float4x4 InverseViewProjection;
    float4 View; // rendered viewport width/height, depth actual width/height
    float4 Footprint; // centre x,z; half size x,z
    float4 Style; // circle=0 / rounded rectangle=1, corner radius, opacity, enabled
    float4 Material; // seconds, motion, height tolerance, grid size or negative atlas bin size
    float4 Edges; // woven border/fringe enabled, feather, route enabled, route grid size
    float4 Ground; // cached ground-sampling centre x,z; half size x,z
    float4 MapCoordinates; // game map offset x,z; world-to-texture scales x,z
    float4 MapView; // map world centre x,z; map yalms per rug yalm; enabled
    float4 RouteArea; // route raster's world centre x,z and half size x,z
    float4 Travel; // unit travel heading x,z; activity 0..1; distance phase 0..2pi
    float4 RouteColor; // actual Wayfinder trail RGBA (neutral when an old publisher has no visual metadata)
    float4 RouteProgress; // player, guide and look-ahead arc lengths; shared visual state enabled
    float4 RouteGuide; // guide world x,y,z; enabled
    float4 RouteAhead; // look-ahead world x,y,z; enabled
    float4 Finish; // optional wear 0..1; reserved
    float4 Foot0;
    float4 Foot1;
    float4 Foot2;
    float4 Foot3;
};
float wearHash(float2 p) { return frac(sin(dot(p,float2(127.1,311.7)))*43758.5453); }
float wearNoise(float2 p) {
    float2 cell=floor(p),f=frac(p); f=f*f*(3-2*f);
    return lerp(lerp(wearHash(cell),wearHash(cell+float2(1,0)),f.x),
        lerp(wearHash(cell+float2(0,1)),wearHash(cell+1),f.x),f.y);
}
float footOcclusion(float3 world, float4 foot) {
    if(foot.w<=0 || world.y<=foot.y-.012+.0001) return 0;
    float d=length(world.xz-foot.xz)/foot.w;
    float f=saturate((1-d)/.3);
    return f*f*(3-2*f);
}
// Match ClothFootClearance: only fixed heel/toe pairs, never left/right.
float footPairOcclusion(float3 world, float4 a, float4 b) {
    if(a.w<=0 || b.w<=0 || abs(a.y-b.y)>.0001 || abs(a.w-b.w)>.0001) return 0;
    float2 segment=b.xz-a.xz;
    float squared=dot(segment,segment);
    if(squared>.5625) return 0;
    float t=squared>.000001 ? saturate(dot(world.xz-a.xz,segment)/squared) : 0;
    float2 closest=a.xz+segment*t;
    return footOcclusion(world,float4(closest.x,min(a.y,b.y),closest.y,min(a.w,b.w)));
}
float4 main(float4 screen : SV_POSITION) : SV_Target {
    if (Style.w < 0.5) discard;
    float2 uv = screen.xy / View.xy;
    float depth = SceneDepth.Load(int3(min(uint2(uv * View.zw), uint2(View.zw)-1),0));
    if (depth <= 0 || depth >= 1) discard;
    float4 h = mul(float4(uv.x * 2 - 1, 1 - uv.y * 2, depth, 1), InverseViewProjection);
    if (h.w <= 1e-7) discard;
    float3 world = h.xyz / h.w;
    float bootMask=max(max(footOcclusion(world,Foot0),footOcclusion(world,Foot1)),
        max(footOcclusion(world,Foot2),footOcclusion(world,Foot3)));
    bootMask=max(bootMask,max(footPairOcclusion(world,Foot0,Foot1),footPairOcclusion(world,Foot2,Foot3)));
    if(bootMask>=.9999) discard;
    float3 normal = cross(ddx(world), ddy(world));
    float len = length(normal);
    if (len < 1e-8 || abs(normal.y) / len < 0.5) discard;
    float2 local = world.xz - Footprint.xy;
    float2 fuv = (world.xz - Ground.xy) / (2 * Ground.zw) + 0.5;
    if (any(fuv < 0) || any(fuv >= 1)) discard;
    // Bilinear obstacle clearance is independent of the exact triangle
    // check: a triangle spanning a wall cannot fill its exclusion region.
    float2 clearanceGrid = fuv * 32;
    int2 clearanceCell = int2(floor(clearanceGrid));
    float2 clearanceT = frac(clearanceGrid);
    float ca = FloorHeights.Load(int3(clearanceCell,0)).y;
    float cb = FloorHeights.Load(int3(clearanceCell+int2(1,0),0)).y;
    float cc = FloorHeights.Load(int3(clearanceCell+int2(0,1),0)).y;
    float cd = FloorHeights.Load(int3(clearanceCell+int2(1,1),0)).y;
    if (min(min(ca,cb),min(cc,cd)) <= 0) discard;
    float clearance = lerp(lerp(ca,cb,clearanceT.x),lerp(cc,cd,clearanceT.x),clearanceT.y)*1.5;
    if (Material.w < 0) {
        // Exact collision coverage: each bin stores at most sixteen triangle
        // IDs (1-based), four per RGBA texel. Overflow bins are entirely -1.
        if (Material.w != -32) discard;
        int2 bin = int2(floor(fuv * 32));
        if (any(bin < 0) || any(bin > 31)) discard;
        if (FloorTriangleIndices.Load(int3(bin.x*4,bin.y,0)).x < 0) discard;
        bool matched = false;
        [loop] for (int slot = 0; slot < 16; ++slot) {
            float4 ids = FloorTriangleIndices.Load(int3(bin.x*4+slot/4,bin.y,0));
            float id = ids[slot%4];
            if (id <= 0) break;
            if (id > 1024 || id != floor(id)) continue;
            int triangleRow = int(id)-1;
            float3 a = FloorTriangles.Load(int3(0,triangleRow,0)).xyz;
            float3 b = FloorTriangles.Load(int3(1,triangleRow,0)).xyz;
            float3 c = FloorTriangles.Load(int3(2,triangleRow,0)).xyz;
            float2 ab = b.xz-a.xz;
            float2 ac = c.xz-a.xz;
            float2 at = world.xz-a.xz;
            float area = ab.x*ac.y-ab.y*ac.x;
            if (abs(area) <= 1e-10) continue;
            float wb = (at.x*ac.y-at.y*ac.x)/area;
            float wc = (ab.x*at.y-ab.y*at.x)/area;
            float wa = 1-wb-wc;
            // No broadening across holes or triangle edges. Only the actual
            // barycentric interior can authorize a visible floor pixel.
            if (wa < 0 || wb < 0 || wc < 0) continue;
            float floorY = a.y+wb*(b.y-a.y)+wc*(c.y-a.y);
            if (abs(world.y-floorY) <= Material.z) { matched = true; break; }
        }
        if (!matched) discard;
    } else {
        float2 grid = fuv * (Material.w - 1);
        int2 cell = int2(floor(grid));
        float2 t = frac(grid);
        float2 a = FloorHeights.Load(int3(cell,0));
        float2 b = FloorHeights.Load(int3(cell + int2(1,0),0));
        float2 c = FloorHeights.Load(int3(cell + int2(0,1),0));
        float2 d = FloorHeights.Load(int3(cell + int2(1,1),0));
        if (min(min(a.y,b.y),min(c.y,d.y)) <= 0) discard;
        float floorY = lerp(lerp(a.x,b.x,t.x),lerp(c.x,d.x,t.x),t.y);
        if (abs(world.y-floorY) > Material.z) discard;
    }
    float distance;
    if (Style.x < 0.5) distance = length(local)-Footprint.z;
    else {
        float2 q = abs(local)-Footprint.zw+Style.y;
        distance = length(max(q,0))+min(max(q.x,q.y),0)-Style.y;
    }
    if (distance >= 0) discard;
    float obstacleInward = max(0, clearance - 0.06);
    float inward = min(-distance, obstacleInward);
    float aa = max(length(ddx(local))+length(ddy(local)),0.002);
    float angle = atan2(local.y,local.x);
    float fringe = min(0.12, min(Footprint.z,Footprint.w)*0.06);
    // A small traveling gather follows actual rug travel, not wall-clock time.
    // All phase terms are 2pi-periodic so distance wrapping is seamless. This
    // only changes the narrow woven edge: floor tests, footprint and map UVs
    // remain untouched, and a resting/default motion state is perfectly quiet.
    float travelStrength = saturate(Travel.z) * Material.y * Edges.x;
    float along = dot(local,Travel.xy);
    float across = dot(local,float2(-Travel.y,Travel.x));
    float travelWave = sin(along*4.8-Travel.w+0.45*sin(across*3+Travel.w));
    fringe *= 1 + 0.18*travelStrength*travelWave;
    float threads = Style.x < 0.5 ? (angle+3.14159265)*Footprint.z*10 :
        (abs(local.x)/Footprint.z > abs(local.y)/Footprint.w ? local.y : local.x)*24;
    float sway = 0.24*travelStrength*travelWave*saturate(1-inward/fringe);
    float fiber = 1-smoothstep(0.26-aa*10,0.26+aa*10,abs(frac(threads+sway)-0.5));
    fiber = lerp(fiber,0.6,saturate(aa*12));
    float body = Edges.x > 0.5 ? smoothstep(fringe-aa,fringe+aa,inward) : 1;
    float edgeFade = smoothstep(0,max(Edges.y,aa),inward);
    float alpha = lerp(fiber,1,body)*Style.z*edgeFade;
    float radius = length(local);
    float star = abs(sin(angle*4))*0.55+0.7;
    float medallion = 1-smoothstep(0.04,0.04+aa,abs(radius-star*1.9));
    float ornament = pow(abs(sin(angle*12+radius*2.5)),16);
    float borderWidth = min(0.24,min(Footprint.z,Footprint.w)*0.12);
    float border = 1-smoothstep(borderWidth-aa,borderWidth+aa,inward);
    float braid = 0.5+0.5*sin(threads*3.14159+inward*24+travelStrength*travelWave*0.7);
    float weave = sin(local.x*220)*sin(local.y*220)*(1-saturate(aa*80));
    float3 navy = float3(0.025,0.11,0.15);
    float3 gold = float3(0.84,0.57,0.24);
    float3 color = lerp(navy, float3(0.08,0.28,0.31),0.35+0.35*cos(radius*1.8));
    color = lerp(color,gold,saturate(medallion+ornament*0.25));
    color = lerp(color,lerp(float3(0.16,0.06,0.08),gold,braid),border*body*Edges.x);
    color = lerp(float3(0.92,0.82,0.63),color,body)*(0.96+weave*0.04);
    float flourish = pow(saturate(0.5+0.5*travelWave),6);
    color = lerp(color,gold,0.22*travelStrength*flourish*border*body);
    if (MapView.w > 0.5) {
        float2 mapWorld = MapView.xy + local*MapView.z;
        float2 mapUV = (mapWorld+MapCoordinates.xy)*MapCoordinates.zw+0.5;
        if (all(mapUV >= 0) && all(mapUV <= 1)) {
            float4 cartography = GameMap.SampleLevel(MapSampler,mapUV,0);
            if (MapView.w > 1.5) {
                float4 background = MapBackground.SampleLevel(MapSampler,mapUV,0);
                cartography = float4(cartography.rgb*background.rgb,background.a);
            }
            color = lerp(color,cartography.rgb*(0.98+weave*0.02),
                cartography.a*body*(1-border));
        }
        // The map is centred on the character; a stable location marker is
        // part of the same material and never floats above occluding geometry.
        float marker = 1-smoothstep(0.10,0.10+aa,length(local));
        float ring = 1-smoothstep(0.035,0.035+aa,abs(length(local)-0.24));
        color = lerp(color,float3(0.12,0.85,1),saturate(marker+ring)*body);
    }
    if (Finish.x > 0) {
        // Fixed in textile coordinates: broad faded pigment, smaller scuffs
        // and exposed pale fibers. Cartography stays readable; route ink is
        // applied afterwards and the silhouette/depth acceptance never change.
        float patches=.65*wearNoise(local*1.7)+.35*wearNoise(local*6.1);
        float scuff=(1-smoothstep(.04,.38,inward))*smoothstep(.28,.74,patches);
        float wear=saturate(Finish.x)*(.3+.7*scuff);
        float luminance=dot(color,float3(.2126,.7152,.0722));
        float3 faded=lerp(color,luminance*float3(1.07,1.01,.90),.35+.2*patches);
        faded=lerp(faded,float3(.76,.71,.59),.08*patches+.30*scuff);
        float mapProtection=MapView.w>.5 ? lerp(.3,1,border) : 1;
        color=lerp(color,faded,wear*mapProtection);
        color+=float3(.13,.12,.09)*max(0,weave)*wear*scuff;
        alpha*=1-.08*saturate(Finish.x)*(1-body)*patches;
    }
    if (Edges.z > 0.5) {
        // This is part of the depth-tested material, not an ImGui foreground
        // line. Walls, the rug outline, and invalid floor pixels already failed
        // above. The route's own elevation prevents painting another storey.
        float2 routeWorld = MapView.w > 0.5 ? MapView.xy+local*MapView.z : world.xz;
        float2 routeUV = (routeWorld-RouteArea.xy)/(2*RouteArea.zw)+0.5;
        float4 path = RouteField.Load(int3(int2(routeUV * (Edges.w-1) + 0.5),0));
        float scale = MapView.w > 0.5 ? MapView.z : 1;
        float aheadMask = RouteProgress.w > 0.5 ? smoothstep(-0.5,0.2,path.y-RouteProgress.x) : 1;
        path.xy /= scale;
        float texel = max(RouteArea.z,RouteArea.w)*2/(Edges.w-1)/scale;
        float pathAA = max(aa,texel*0.4);
        float width = max(0.22,texel*0.8);
        float ribbon = 1-smoothstep(width-pathAA,width+pathAA,path.x);
        float thread = 1-smoothstep(width*0.3,width*0.3+pathAA,path.x);
        float bead = 1-smoothstep(0.11,0.11+pathAA,abs(frac(path.y/1.5)-0.5)*1.5);
        float visible = path.w * ((MapView.w > 0.5 || abs(world.y-path.z) < 1.25) ? 1 : 0) * RouteColor.a;
        float3 tint = saturate(RouteColor.rgb);
        float3 routeColor = lerp(tint,float3(1,1,1),saturate(thread+bead)*0.3);
        color = lerp(color,routeColor,ribbon*visible*body*aheadMask*0.85);
        // A restrained pool of the SAME light at the guide/look-ahead roots:
        // part of the map material, never a billboard above walls or UI.
        float guideDistance = length(routeWorld-RouteGuide.xz)/scale;
        float nextDistance = length(routeWorld-RouteAhead.xz)/scale;
        float guideFloor = MapView.w > 0.5 || abs(world.y-RouteGuide.y)<1.25 ? 1 : 0;
        float nextFloor = MapView.w > 0.5 || abs(world.y-RouteAhead.y)<1.25 ? 1 : 0;
        float pool = exp(-guideDistance*guideDistance/0.045)*RouteGuide.w*guideFloor*0.20
            + exp(-nextDistance*nextDistance/0.065)*RouteAhead.w*nextFloor*0.12;
        color = lerp(color,lerp(tint,float3(1,1,1),0.25),pool*visible*body);
    }
    // Stationary narrow pleats suggest fabric gathering at a solid edge.
    // This shades the existing ground; it never displaces scene geometry.
    float gathering = (1-smoothstep(0.12,0.65,clearance))*smoothstep(0.04,0.16,clearance);
    float pleat = sin(clearance*48 + sin(world.x*3+world.z*4)*0.6);
    color *= 1 + gathering*pleat*0.16;
    alpha *= smoothstep(0.04,0.16,clearance);
    return float4(color,alpha*(1-bootMask)); // straight-alpha blend state
}
