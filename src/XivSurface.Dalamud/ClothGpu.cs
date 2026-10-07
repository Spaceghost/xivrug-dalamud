using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using XivSurface.Core;

namespace XivSurface.Dalamud;

[StructLayout(LayoutKind.Sequential)]
internal struct ClothVertex(Vector3 position, Vector3 normal, Vector2 uv, float groundMinimum, float contactCeiling)
{
    public Vector3 Position = position, Normal = normal;
    public Vector2 UV = uv;
    public float GroundMinimum = groundMinimum;
    public float ContactCeiling = contactCeiling;
}

/// <summary>Continuous world-space cloth. Scene depth occludes the surface; a
/// private depth buffer resolves folds. Use inside NativeRugPipeline's envelope
/// under the caller's render lifetime lock. Never writes the game's depth texture.</summary>
public sealed unsafe class ClothGpu : IDisposable
{
    private ID3D11Device* device;
    private ID3D11VertexShader* vertex;
    private ID3D11VertexShader* indexedVertex;
    private ID3D11PixelShader* pixel;
    private ID3D11Buffer* constants;
    private ID3D11Buffer* vertices;
    private ID3D11InputLayout* layout;
    private ID3D11InputLayout* indexedLayout;
    private ID3D11SamplerState* sampler;
    private ID3D11Texture2D* route;
    private ID3D11ShaderResourceView* routeView;
    private Vector3[] presentedPositions = [], presentedNormals = [];
    private float[] presentedCeilings = [];
    private float[]? uploadedRoute;
    private int vertexCount;
    private ID3D11DepthStencilState* depthState;
    private ID3D11Texture2D* ownDepth;
    private ID3D11DepthStencilView* ownDepthView;
    private uint width, height;
    private readonly WorldClothLighting worldLighting = new();
    private readonly NativeClothShadow nativeShadow = new();
    private const int Capacity = 128 * 128 * 6;
    public string Status { get; private set; } = "Waiting for cloth geometry.";
    public long Submissions { get; private set; }
    public long SharedDepthSubmissions { get; private set; }
    public string LightingDiagnostic => worldLighting.Diagnostic;
    public string ShadowDiagnostic => nativeShadow.Diagnostic;

    /// <summary>Read-only native-stage observation, including frames whose cloth
    /// ground coverage is not ready. No texture copy, binding or draw.</summary>
    public void InspectNativeShadow(nint context)
    {
        var targets = RenderTargetManager.Instance();
        if (targets == null || targets->DepthStencil == null) return;
        var depth = targets->DepthStencil;
        using var observation = nativeShadow.Acquire(context, depth->ActualWidth, depth->ActualHeight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProjection, InverseViewProjection;
        public Vector4 Camera, View, Footprint, Style, MapCoordinates, MapView, RouteArea, Material;
        public Vector4 RouteColor, RouteProgress, RouteGuide, RouteAhead, Finish;
        public Vector4 Foot0, Foot1, Foot2, Foot3;
        public Vector4 LightAmbient, LightSun, LightMoon, LightDirection, Wisp0, Wisp1, Wisp2, NativeShadow;
    }

    public void Draw(nint devicePointer, ClothMesh mesh, RugProjection projection, bool circle, float corner,
        double seconds, bool motion = true, bool edges = true, float opacity = .98f, float[]? routeField = null, float wear = 0, float lift = 0,
        ReadOnlySpan<ClothFootContact> feet = default, Func<nint,uint,uint,nint>? acquirePrivateDepth = null,
        Func<bool>? canSubmit = null)
        => DrawCore(devicePointer, mesh, null, projection, circle, corner, seconds, motion, edges,
            opacity, routeField, wear, lift, feet, acquirePrivateDepth, canSubmit);

    /// <summary>Draws an already accepted full 3-D pose through the same native
    /// composition/depth envelope. Does not invent ground bounds, interpolate
    /// frames, or add visual displacement. Caller must validate current scene,
    /// whole-surface contacts and foot clearance before publishing this pose.
    /// LiveRug's physical branch uses this entry point but remains default-inactive
    /// until the runtime collision/foot controller is ready. canSubmit, when
    /// provided, is rechecked after preparation immediately before each CPU
    /// draw call; it does not establish GPU presentation-time freshness.</summary>
    public void DrawIndexed(nint devicePointer, IndexedClothPose pose, RugProjection projection,
        bool circle, float corner, double seconds, bool motion = true, bool edges = true,
        float opacity = .98f, float[]? routeField = null, float wear = 0,
        Func<nint,uint,uint,nint>? acquirePrivateDepth = null, Func<bool>? canSubmit = null)
    {
        ArgumentNullException.ThrowIfNull(pose);
        DrawCore(devicePointer, null, pose, projection, circle, corner, seconds, motion, edges,
            opacity, routeField, wear, 0, default, acquirePrivateDepth, canSubmit);
    }

    private void DrawCore(nint devicePointer, ClothMesh? mesh, IndexedClothPose? indexed,
        RugProjection projection, bool circle, float corner, double seconds, bool motion,
        bool edges, float opacity, float[]? routeField, float wear, float lift,
        ReadOnlySpan<ClothFootContact> feet, Func<nint,uint,uint,nint>? acquirePrivateDepth, Func<bool>? canSubmit)
    {
        var indexCount = indexed?.Indices.Length ?? mesh!.Indices.Length;
        if (devicePointer == 0 || indexCount == 0 || indexCount > Capacity) return;
        if (indexed is null)
        {
            if (mesh!.Positions.Length is <3 or >ClothSurface.MaximumSize*ClothSurface.MaximumSize)
                throw new ArgumentException("Cloth rendering requires bounded shared vertices.",nameof(mesh));
            if (mesh.GroundMinimum is null || mesh.GroundMinimum.Length != mesh.Positions.Length) return;
        }
        if (routeField is not null && routeField.Length != RouteRibbonField.FloatCount) throw new ArgumentException("Invalid cloth route field.", nameof(routeField));
        if (!float.IsFinite(projection.HalfSize.X) || !float.IsFinite(projection.HalfSize.Y) || projection.HalfSize.X <= 0 || projection.HalfSize.Y <= 0) return;
        var renderTime = ClothRenderClock.Phase(seconds);
        var renderLift = float.IsFinite(lift) ? Math.Clamp(lift, 0, 8) : 0;
        var renderCorner = float.IsFinite(corner) ? Math.Clamp(corner, 0, Math.Min(projection.HalfSize.X, projection.HalfSize.Y)) : 0;
        var control = Control.Instance(); var targets = RenderTargetManager.Instance();
        var manager = CameraManager.Instance(); var camera = manager == null ? null : manager->GetActiveCamera();
        if (control == null || camera == null || targets == null || targets->DepthStencil == null) return;
        var depth = targets->DepthStencil;
        if (depth->D3D11ShaderResourceView == null || depth->ActualWidth == 0 || depth->ActualHeight == 0
            || depth->ActualWidth > depth->AllocatedWidth || depth->ActualHeight > depth->AllocatedHeight) return;
        Matrix4x4 vp = control->ViewProjectionMatrix;
        if (!Matrix4x4.Invert(vp, out var inverse)) return;
        if ((nint)device != devicePointer) { Dispose(); Create((ID3D11Device*)devicePointer); }
        if (indexed is not null) EnsureIndexedResources();
        ID3D11DeviceContext* baseContext = null;
        device->GetImmediateContext(&baseContext);
        var iid = IID.IID_ID3D11DeviceContext1; void* queried = null;
        var hr = baseContext->QueryInterface(&iid, &queried); baseContext->Release(); Check(hr);
        var ctx = (ID3D11DeviceContext1*)queried;
        ID3D11DepthStencilView* sharedDepth = null;
        try
        {
            using var shadowLease = nativeShadow.Acquire((nint)ctx, depth->ActualWidth, depth->ActualHeight);
            var viewport = new D3D11_VIEWPORT(); uint viewportCount = 1;
            ctx->RSGetViewports(&viewportCount, &viewport);
            if (viewportCount != 1 || viewport.TopLeftX != 0 || viewport.TopLeftY != 0
                || viewport.Width <= 0 || viewport.Height <= 0 || viewport.Width > 16384 || viewport.Height > 16384) return;
            // The native-stage provider owns frame clearing. This AddRef'd
            // lease remains alive through every upload and pipeline restore.
            sharedDepth = (ID3D11DepthStencilView*)(acquirePrivateDepth?.Invoke(devicePointer,
                (uint)viewport.Width, (uint)viewport.Height) ?? 0);
            if (sharedDepth == null) EnsureDepth((uint)viewport.Width, (uint)viewport.Height);
            D3D11_MAPPED_SUBRESOURCE mapped;
            // Clock/lift and swept contacts can change while the immutable
            // framework mesh stays the same. Prepare the actual final pose
            // on EVERY draw, then derive shared smooth normals from that pose.
            if (indexed is not null)
            {
                Check(ctx->Map((ID3D11Resource*)vertices, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped));
                try { indexed.WriteTriangleList(new Span<IndexedClothVertex>(mapped.pData,indexCount)); }
                finally { ctx->Unmap((ID3D11Resource*)vertices, 0); }
                vertexCount = indexCount;
            }
            else
            {
                var count = mesh!.Positions.Length;
                // Rolling windows alternate dimensions at lattice boundaries.
                // Retain high-water capacity instead of reallocating on shrink.
                if (presentedPositions.Length < count)
                { presentedPositions = new Vector3[count]; presentedNormals = new Vector3[count]; presentedCeilings = new float[count]; }
                var positions = presentedPositions.AsSpan(0,count); var normals = presentedNormals.AsSpan(0,count);
                var ceilings = presentedCeilings.AsSpan(0,count);
                ClothContactConstraint.BuildRenderCeilings(mesh, feet, renderLift, ceilings);
                ClothRenderPose.Prepare(mesh, ceilings, projection.HalfSize, circle, renderCorner,
                    renderTime, motion, edges, renderLift, positions, normals);
                Check(ctx->Map((ID3D11Resource*)vertices, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped));
                try { WriteVertices(mesh,positions,normals,new Span<ClothVertex>(mapped.pData,mesh.Indices.Length),ceilings); }
                finally { ctx->Unmap((ID3D11Resource*)vertices, 0); }
                vertexCount = mesh.Indices.Length;
            }
            if (routeField is not null && !ReferenceEquals(uploadedRoute, routeField))
            {
                fixed (float* values = routeField) ctx->UpdateSubresource((ID3D11Resource*)route, 0, null, values, RouteRibbonField.GridSize * 16, 0);
                uploadedRoute = routeField;
            }

            ID3D11PixelShader* oldPs = null; ID3D11VertexShader* oldVs = null; ID3D11GeometryShader* oldGs = null;
            ID3D11ClassInstance** psClasses = stackalloc ID3D11ClassInstance*[256];
            ID3D11ClassInstance** vsClasses = stackalloc ID3D11ClassInstance*[256];
            ID3D11ClassInstance** gsClasses = stackalloc ID3D11ClassInstance*[256];
            uint psCount = 256, vsCount = 256, gsCount = 256;
            ctx->PSGetShader(&oldPs, psClasses, &psCount); ctx->VSGetShader(&oldVs, vsClasses, &vsCount); ctx->GSGetShader(&oldGs, gsClasses, &gsCount);
            ID3D11ShaderResourceView** oldViews = stackalloc ID3D11ShaderResourceView*[5]; ctx->PSGetShaderResources(0, 5, oldViews);
            ID3D11SamplerState* oldSampler = null; ctx->PSGetSamplers(0, 1, &oldSampler);
            ID3D11Buffer* oldPsBuffer = null; ID3D11Buffer* oldVsBuffer = null;
            uint psFirst = 0, psLength = 0, vsFirst = 0, vsLength = 0;
            ctx->PSGetConstantBuffers1(0, 1, &oldPsBuffer, &psFirst, &psLength);
            ctx->VSGetConstantBuffers1(0, 1, &oldVsBuffer, &vsFirst, &vsLength);
            ID3D11InputLayout* oldLayout = null; D3D_PRIMITIVE_TOPOLOGY oldTopology;
            ctx->IAGetInputLayout(&oldLayout); ctx->IAGetPrimitiveTopology(&oldTopology);
            ID3D11Buffer* oldVertices = null; uint oldStride = 0, oldOffset = 0;
            ctx->IAGetVertexBuffers(0, 1, &oldVertices, &oldStride, &oldOffset);
            ID3D11RenderTargetView* target = null; ID3D11DepthStencilView* oldDepth = null;
            ctx->OMGetRenderTargets(1, &target, &oldDepth);
            ID3D11DepthStencilState* oldDepthState = null; uint oldStencil = 0; ctx->OMGetDepthStencilState(&oldDepthState, &oldStencil);
            try
            {
                if (sharedDepth == null)
                    ctx->ClearDepthStencilView(ownDepthView, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, 1, 0);
                ctx->OMSetRenderTargets(1, &target, sharedDepth != null ? sharedDepth : ownDepthView); ctx->OMSetDepthStencilState(depthState, 0);
                var cb = constants; var vb = vertices;
                uint stride = indexed is null ? (uint)sizeof(ClothVertex) : (uint)sizeof(IndexedClothVertex), offset = 0;
                ctx->VSSetConstantBuffers(0, 1, &cb); ctx->PSSetConstantBuffers(0, 1, &cb);
                ctx->VSSetShader(indexed is null ? vertex : indexedVertex, null, 0); ctx->PSSetShader(pixel, null, 0); ctx->GSSetShader(null, null, 0);
                ctx->IASetInputLayout(indexed is null ? layout : indexedLayout); ctx->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
                ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                var light = worldLighting.Sample(seconds);
                Span<Vector4> wisps = stackalloc Vector4[3];
                BuildWisps(indexed is null ? presentedPositions.AsSpan(0, mesh!.Positions.Length) : indexed.Positions,
                    projection, renderTime, light.Darkness, wisps);
                var values = new Constants { ViewProjection = vp, InverseViewProjection = inverse,
                    Camera = new((Vector3)camera->SceneCamera.Position, 1),
                    View = new(viewport.Width, viewport.Height, depth->ActualWidth, depth->ActualHeight),
                    Footprint = new(projection.Center.X, projection.Center.Y, projection.HalfSize.X, projection.HalfSize.Y),
                    Style = new(circle ? 0 : 1, renderCorner, edges ? 1 : 0, Math.Clamp(opacity,0,1)),
                    MapCoordinates = projection.MapCoordinates,
                    MapView = new(projection.MapCenter.X,projection.MapCenter.Y,projection.MapScale,projection.MapTexture == 0 ? 0 : projection.MapBackground == 0 ? 1 : 2),
                    RouteArea = projection.RouteArea,
                    Material = new(renderTime,motion ? 1 : 0,routeField is null ? 0 : 1,RouteRibbonField.GridSize),
                    RouteColor = projection.RouteVisual?.Color ?? NavigationVisual.LegacyColor,
                    RouteProgress = projection.RouteVisual?.Arcs ?? default,
                    RouteGuide = projection.RouteVisual is { } guideVisual ? new(guideVisual.GuidePosition, 1) : default,
                    RouteAhead = projection.RouteVisual is { } aheadVisual ? new(aheadVisual.LookAheadPosition, 1) : default,
                    Finish = new(float.IsFinite(wear) ? Math.Clamp(wear, 0, 1) : 0, renderLift, 0, 0),
                    Foot0 = ClothFootClearance.Pack(feet, 0), Foot1 = ClothFootClearance.Pack(feet, 1),
                    Foot2 = ClothFootClearance.Pack(feet, 2), Foot3 = ClothFootClearance.Pack(feet, 3),
                    LightAmbient = new(light.Ambient, 0), LightSun = new(light.Sun, 0), LightMoon = new(light.Moon, 0),
                    LightDirection = new(light.Direction, 0), Wisp0 = wisps[0], Wisp1 = wisps[1], Wisp2 = wisps[2],
                    NativeShadow = shadowLease is null ? default : new(shadowLease.Width, shadowLease.Height, 1, .18f),
                };
                Check(ctx->Map((ID3D11Resource*)constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped));
                *(Constants*)mapped.pData = values; ctx->Unmap((ID3D11Resource*)constants, 0);
                ID3D11ShaderResourceView** views = stackalloc ID3D11ShaderResourceView*[5];
                views[0] = (ID3D11ShaderResourceView*)depth->D3D11ShaderResourceView;
                views[1] = (ID3D11ShaderResourceView*)projection.MapTexture;
                views[2] = (ID3D11ShaderResourceView*)projection.MapBackground;
                views[3] = routeView;
                views[4] = (ID3D11ShaderResourceView*)(shadowLease?.ShaderResourceView ?? 0);
                var sample = sampler; ctx->PSSetSamplers(0, 1, &sample);
                ctx->PSSetShaderResources(0, 5, views);
                // Shader/device/depth creation, IPC or Map can stall after the
                // caller's entry check. Never refresh the original foot clock.
                // Both finally blocks still restore state/release COM on refusal.
                if (canSubmit is not null && !canSubmit()) return;
                ctx->Draw((uint)vertexCount, 0);
                if (light.Darkness > .015f) DrawWisps(ctx, ref values, wisps, (Vector3)camera->SceneCamera.Position, canSubmit);
                Status = $"Cloth mesh submitted: {vertexCount/3:N0} triangles.";
                Submissions++;
                if (sharedDepth != null) SharedDepthSubmissions++;
            }
            finally
            {
                ctx->PSSetShaderResources(0, 5, oldViews);
                ctx->OMSetRenderTargets(1, &target, oldDepth); ctx->OMSetDepthStencilState(oldDepthState, oldStencil);
                ctx->VSSetShader(oldVs, vsClasses, vsCount); ctx->PSSetShader(oldPs, psClasses, psCount); ctx->GSSetShader(oldGs, gsClasses, gsCount);
                ctx->PSSetSamplers(0, 1, &oldSampler);
                ctx->PSSetConstantBuffers1(0, 1, &oldPsBuffer, &psFirst, &psLength); ctx->VSSetConstantBuffers1(0, 1, &oldVsBuffer, &vsFirst, &vsLength);
                ctx->IASetInputLayout(oldLayout); ctx->IASetPrimitiveTopology(oldTopology); ctx->IASetVertexBuffers(0, 1, &oldVertices, &oldStride, &oldOffset);
                if (oldPs != null) oldPs->Release(); if (oldVs != null) oldVs->Release(); if (oldGs != null) oldGs->Release();
                if (oldPsBuffer != null) oldPsBuffer->Release(); if (oldVsBuffer != null) oldVsBuffer->Release();
                if (oldLayout != null) oldLayout->Release(); if (oldVertices != null) oldVertices->Release();
                if (oldSampler != null) oldSampler->Release(); if (target != null) target->Release();
                if (oldDepth != null) oldDepth->Release(); if (oldDepthState != null) oldDepthState->Release();
                for (var i = 0; i < 5; i++) if (oldViews[i] != null) oldViews[i]->Release();
                for (var i = 0; i < psCount; i++) psClasses[i]->Release();
                for (var i = 0; i < vsCount; i++) vsClasses[i]->Release();
                for (var i = 0; i < gsCount; i++) gsClasses[i]->Release();
            }
        }
        finally
        {
            // The inner finally has restored the caller's render targets and
            // shaders before releasing this cross-plugin COM lease, even when
            // a later upload/draw failed. Game depth is never used as an output.
            if (sharedDepth != null) sharedDepth->Release();
            ctx->Release();
        }
    }

    private static void BuildWisps(ReadOnlySpan<Vector3> surface, RugProjection projection, float time,
        float darkness, Span<Vector4> wisps)
    {
        for (var i = 0; i < wisps.Length; i++)
        {
            var phase = i * MathF.Tau / 3;
            var angle = phase + time * .075f + .1f * MathF.Sin(time * .43f + phase);
            var radius = .57f + .06f * MathF.Sin(time * .37f + phase);
            var wanted = projection.Center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * projection.HalfSize * radius;
            var nearest = float.PositiveInfinity; var floor = Vector3.Zero;
            foreach (var point in surface)
            {
                var distance = Vector2.DistanceSquared(wanted, new(point.X, point.Z));
                if (distance >= nearest) continue;
                nearest = distance; floor = point;
            }
            // Anchor to an actual rendered cloth point, including an indexed
            // 3-D fold, then float a small local light above that point.
            floor.Y += .26f + .055f * MathF.Sin(time * .8f + phase);
            wisps[i] = new(floor, darkness * (.78f + .12f * MathF.Sin(time * 1.3f + phase)));
        }
    }

    private void DrawWisps(ID3D11DeviceContext1* ctx, ref Constants values, ReadOnlySpan<Vector4> wisps, Vector3 camera,
        Func<bool>? canSubmit)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(ctx->Map((ID3D11Resource*)vertices, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped));
        try
        {
            var output = new Span<ClothVertex>(mapped.pData, 18);
            for (var i = 0; i < wisps.Length; i++)
            {
                var center = new Vector3(wisps[i].X, wisps[i].Y, wisps[i].Z);
                var toward = camera - center;
                toward = toward.LengthSquared() > 1e-8f ? Vector3.Normalize(toward) : Vector3.UnitZ;
                var right = Vector3.Cross(Vector3.UnitY, toward);
                right = right.LengthSquared() > 1e-8f ? Vector3.Normalize(right) : Vector3.UnitX;
                var up = Vector3.Normalize(Vector3.Cross(toward, right));
                var tint = i == 1 ? new Vector3(.52f, .82f, 1) : new Vector3(1, .82f, .46f);
                tint *= wisps[i].W;
                const float size = .085f;
                var a = center - right * size - up * size; var b = center + right * size - up * size;
                var c = center - right * size + up * size; var d = center + right * size + up * size;
                var at = i * 6;
                output[at] = new(a, tint, new(0, 1), 0, 0); output[at + 1] = new(b, tint, new(1, 1), 0, 0);
                output[at + 2] = new(c, tint, new(0, 0), 0, 0); output[at + 3] = new(b, tint, new(1, 1), 0, 0);
                output[at + 4] = new(d, tint, new(1, 0), 0, 0); output[at + 5] = new(c, tint, new(0, 0), 0, 0);
            }
        }
        finally { ctx->Unmap((ID3D11Resource*)vertices, 0); }
        values.LightDirection.W = 1;
        Check(ctx->Map((ID3D11Resource*)constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped));
        *(Constants*)mapped.pData = values; ctx->Unmap((ID3D11Resource*)constants, 0);
        var buffer = vertices; uint stride = (uint)sizeof(ClothVertex), offset = 0;
        ctx->VSSetShader(vertex, null, 0); ctx->IASetInputLayout(layout);
        ctx->IASetVertexBuffers(0, 1, &buffer, &stride, &offset);
        // Same real scene-depth rejection and private/shared depth test as the
        // cloth. The pixel shader discards the empty corners of each glow.
        if (canSubmit is not null && !canSubmit()) return;
        ctx->Draw(18, 0);
    }

    private void Create(ID3D11Device* value)
    {
        device = value; device->AddRef();
        try
        {
            var vs = Bytecode("ClothVertex"); var ps = Bytecode("ClothPixel");
            ID3D11VertexShader* v = null; ID3D11PixelShader* p = null;
            fixed (byte* b = vs) Check(device->CreateVertexShader(b, (nuint)vs.Length, null, &v)); vertex = v;
            fixed (byte* b = ps) Check(device->CreatePixelShader(b, (nuint)ps.Length, null, &p)); pixel = p;
            var desc = new D3D11_BUFFER_DESC { Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE, BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER, ByteWidth = (uint)sizeof(Constants) };
            ID3D11Buffer* buffer = null; Check(device->CreateBuffer(&desc, null, &buffer)); constants = buffer;
            desc.ByteWidth = (uint)(Capacity * sizeof(ClothVertex)); desc.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER;
            buffer = null; Check(device->CreateBuffer(&desc, null, &buffer)); vertices = buffer;
            fixed (byte* position = "POSITION\0"u8, normal = "NORMAL\0"u8, uv = "TEXCOORD\0"u8)
            fixed (byte* bytecode = vs)
            {
                D3D11_INPUT_ELEMENT_DESC* elements = stackalloc D3D11_INPUT_ELEMENT_DESC[5];
                elements[0] = new() { SemanticName = (sbyte*)position, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT, AlignedByteOffset = 0 };
                elements[1] = new() { SemanticName = (sbyte*)normal, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT, AlignedByteOffset = 12 };
                elements[2] = new() { SemanticName = (sbyte*)uv, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT, AlignedByteOffset = 24 };
                elements[3] = new() { SemanticName = (sbyte*)uv, SemanticIndex = 1, Format = DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT, AlignedByteOffset = 32 };
                elements[4] = new() { SemanticName = (sbyte*)uv, SemanticIndex = 2, Format = DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT, AlignedByteOffset = 36 };
                ID3D11InputLayout* l = null; Check(device->CreateInputLayout(elements, 5, bytecode, (nuint)vs.Length, &l)); layout = l;
            }
            var sd = new D3D11_SAMPLER_DESC { Filter = D3D11_FILTER.D3D11_FILTER_ANISOTROPIC,
                AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                MaxAnisotropy = 8, MaxLOD = float.MaxValue, ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS };
            ID3D11SamplerState* sample = null; Check(device->CreateSamplerState(&sd,&sample)); sampler=sample;
            var td = new D3D11_TEXTURE2D_DESC { Width=RouteRibbonField.GridSize, Height=RouteRibbonField.GridSize,
                MipLevels=1,ArraySize=1,Format=DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,SampleDesc=new(1,0),
                Usage=D3D11_USAGE.D3D11_USAGE_DEFAULT,BindFlags=(uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE };
            ID3D11Texture2D* texture=null; Check(device->CreateTexture2D(&td,null,&texture)); route=texture;
            ID3D11ShaderResourceView* srv=null; Check(device->CreateShaderResourceView((ID3D11Resource*)route,null,&srv)); routeView=srv;
            var dd = new D3D11_DEPTH_STENCIL_DESC { DepthEnable = true, DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL,
                DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_LESS_EQUAL };
            ID3D11DepthStencilState* d = null; Check(device->CreateDepthStencilState(&dd, &d)); depthState = d;
        }
        catch { Dispose(); throw; }
    }

    // Lazy: the existing live heightfield path does not create or depend on
    // these resources. Publish both only after successful construction, so
    // failure leaves the legacy device resources usable and retryable.
    private void EnsureIndexedResources()
    {
        if (indexedVertex != null && indexedLayout != null) return;
        var bytecode = Bytecode("IndexedClothVertex");
        ID3D11VertexShader* nextVertex = null;
        ID3D11InputLayout* nextLayout = null;
        try
        {
            fixed (byte* compiled = bytecode)
            {
                Check(device->CreateVertexShader(compiled, (nuint)bytecode.Length, null, &nextVertex));
                fixed (byte* position = "POSITION\0"u8, normal = "NORMAL\0"u8, uv = "TEXCOORD\0"u8)
                {
                    D3D11_INPUT_ELEMENT_DESC* elements = stackalloc D3D11_INPUT_ELEMENT_DESC[3];
                    elements[0] = new() { SemanticName = (sbyte*)position, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT, AlignedByteOffset = 0 };
                    elements[1] = new() { SemanticName = (sbyte*)normal, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT, AlignedByteOffset = 12 };
                    elements[2] = new() { SemanticName = (sbyte*)uv, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT, AlignedByteOffset = 24 };
                    Check(device->CreateInputLayout(elements, 3, compiled, (nuint)bytecode.Length, &nextLayout));
                }
            }
            indexedVertex = nextVertex; nextVertex = null;
            indexedLayout = nextLayout; nextLayout = null;
        }
        finally
        {
            if (nextLayout != null) nextLayout->Release();
            if (nextVertex != null) nextVertex->Release();
        }
    }

    private static void WriteVertices(ClothMesh mesh,ReadOnlySpan<Vector3> positions,ReadOnlySpan<Vector3> normals,
        Span<ClothVertex> data,ReadOnlySpan<float> contactCeilings)
    {
        if(mesh.Positions.Length != mesh.Normals.Length || mesh.Positions.Length != mesh.UV.Length || mesh.Indices.Length%3 != 0)
            throw new ArgumentException("Inconsistent cloth geometry.", nameof(mesh));
        if(mesh.GroundMinimum is null || mesh.GroundMinimum.Length != mesh.Positions.Length)
            throw new ArgumentException("Cloth requires measured ground constraints.", nameof(mesh));
        if(contactCeilings.Length != mesh.Positions.Length)
            throw new ArgumentException("Cloth requires complete current contact bounds.", nameof(contactCeilings));
        if(positions.Length != mesh.Positions.Length || normals.Length != mesh.Positions.Length)
            throw new ArgumentException("Cloth requires complete final render geometry.", nameof(positions));
        for(var i=0;i<data.Length;i++)
        {
            var index=mesh.Indices[i];
            if((uint)index >= (uint)mesh.Positions.Length) throw new ArgumentException("Invalid cloth index.",nameof(mesh));
            var p=positions[index]; var n=normals[index]; var uv=mesh.UV[index];
            if(!float.IsFinite(p.X+p.Y+p.Z) || !float.IsFinite(n.X+n.Y+n.Z) || !float.IsFinite(uv.X+uv.Y))
                throw new ArgumentException("Invalid cloth vertex.",nameof(mesh));
            var ground=mesh.GroundMinimum[index];
            if(!float.IsFinite(ground) || ground>p.Y+.0001f)
                throw new ArgumentException("Invalid cloth ground constraint.",nameof(mesh));
            var ceiling=contactCeilings[index];
            if(!float.IsFinite(ceiling) || ceiling<ground)
                throw new ArgumentException("Invalid cloth contact ceiling.",nameof(contactCeilings));
            data[i]=new(p,n,uv,ground,ceiling);
        }
    }

    private void EnsureDepth(uint w, uint h)
    {
        if (width == w && height == h && ownDepthView != null) return;
        if (ownDepthView != null) { ownDepthView->Release(); ownDepthView = null; }
        if (ownDepth != null) { ownDepth->Release(); ownDepth = null; }
        var desc = new D3D11_TEXTURE2D_DESC { Width = w, Height = h, MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT, SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT, BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_DEPTH_STENCIL };
        ID3D11Texture2D* t = null; Check(device->CreateTexture2D(&desc, null, &t)); ownDepth = t;
        ID3D11DepthStencilView* view = null; Check(device->CreateDepthStencilView((ID3D11Resource*)t, null, &view)); ownDepthView = view;
        width = w; height = h;
    }
    private static byte[] Bytecode(string name)
    {
        using var stream = typeof(XivSurface.Core.Footprint).Assembly.GetManifestResourceStream("XivSurface." + name + ".dxbc")
            ?? throw new InvalidOperationException("Cloth shader was not embedded: " + name);
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    public void Dispose()
    {
        if (ownDepthView != null) { ownDepthView->Release(); ownDepthView = null; }
        if (ownDepth != null) { ownDepth->Release(); ownDepth = null; } width = height = 0;
        if (depthState != null) { depthState->Release(); depthState = null; }
        if(sampler != null) { sampler->Release(); sampler=null; }
        if(routeView != null) { routeView->Release(); routeView=null; }
        if(route != null) { route->Release(); route=null; }
        presentedPositions=[]; presentedNormals=[]; presentedCeilings=[]; uploadedRoute=null; vertexCount=0;
        if (layout != null) { layout->Release(); layout = null; }
        if (indexedLayout != null) { indexedLayout->Release(); indexedLayout = null; }
        if (constants != null) { constants->Release(); constants = null; }
        if (vertices != null) { vertices->Release(); vertices = null; }
        if (pixel != null) { pixel->Release(); pixel = null; }
        if (vertex != null) { vertex->Release(); vertex = null; }
        if (indexedVertex != null) { indexedVertex->Release(); indexedVertex = null; }
        if (device != null) { device->Release(); device = null; }
    }
}
