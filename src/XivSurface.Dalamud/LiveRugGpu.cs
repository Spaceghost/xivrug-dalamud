using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using XivSurface.Core;

namespace XivSurface.Dalamud;

/// <summary>Visual footprint is independent of the cached ground-sampling area.</summary>
public readonly record struct RugProjection(Vector2 Center, Vector2 HalfSize,
    nint MapTexture, Vector4 MapCoordinates, Vector2 MapCenter, float MapScale, Vector4 RouteArea, nint MapBackground = 0,
    NavigationVisual? RouteVisual = null);

/// <summary>Independent rug GPU pass, invoked by the native pre-UI stage or the
/// optional legacy background compositor. This is not the separate strict
/// floor-classification SurfacePixelScope prototype.</summary>
public sealed unsafe class LiveRugGpu : IDisposable
{
    private ID3D11Device* device;
    private ID3D11PixelShader* pixel;
    private ID3D11VertexShader* vertex;
    private ID3D11Buffer* buffer;
    private ID3D11Texture2D* floor;
    private ID3D11ShaderResourceView* floorView;
    private ID3D11Texture2D* route;
    private ID3D11ShaderResourceView* routeView;
    private ID3D11Texture2D* triangles;
    private ID3D11ShaderResourceView* triangleView;
    private ID3D11Texture2D* triangleIndices;
    private ID3D11ShaderResourceView* triangleIndexView;
    private GeometryFloorAtlas? uploadedAtlas;
    private ID3D11SamplerState* mapSampler;
    private float[]? uploadedRoute;
    // Published floor snapshots are immutable. Upload only when their identity
    // changes; rewriting the same DEFAULT texture every frame needlessly
    // contends with the previous frame's shader reads.
    private float[]? uploadedHeights;
    public const int GridSize = 33;
    public string Status { get; private set; } = "Waiting for draw callback.";
    public long Submissions { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 Inverse;
        public Vector4 View, Footprint, Style, Material, Edges, Ground, MapCoordinates, MapView, RouteArea, Travel;
        public Vector4 RouteColor, RouteProgress, RouteGuide, RouteAhead, Finish;
        public Vector4 Foot0, Foot1, Foot2, Foot3;
    }

    public void Draw(nint devicePointer, Vector2 center, Vector2 halfSize, float[] heights,
        Vector2 viewport, bool circle, float corner, float seconds, bool motion,
        bool rugEdges, float feather, float[]? routeField = null, RugProjection? projection = null,
        GeometryFloorAtlas? floorAtlas = null, Vector4 travel = default, float wear = 0, ReadOnlySpan<ClothFootContact> feet = default)
    {
        if (devicePointer == 0 || heights.Length != GridSize * GridSize * 2) return;
        if (routeField is not null && routeField.Length != RouteRibbonField.GridSize * RouteRibbonField.GridSize * 4) return;
        if (floorAtlas is not null && (floorAtlas.Center != center || floorAtlas.HalfSize != halfSize
            || (!ReferenceEquals(uploadedAtlas, floorAtlas) && !ValidAtlas(floorAtlas))))
        {
            Status = "Skipped: floor atlas is invalid or does not match the cached ground domain.";
            return;
        }
        var visualCenter = projection?.Center ?? center;
        var visualHalf = projection?.HalfSize ?? halfSize;
        if (!Finite(visualCenter) || !Finite(visualHalf) || visualHalf.X <= 0 || visualHalf.Y <= 0) return;
        if (projection is { MapTexture: not 0 } map && (!Finite(map.MapCenter) || !float.IsFinite(map.MapScale) || map.MapScale <= 0
            || !float.IsFinite(map.MapCoordinates.X) || !float.IsFinite(map.MapCoordinates.Y)
            || !float.IsFinite(map.MapCoordinates.Z) || !float.IsFinite(map.MapCoordinates.W)
            || map.MapCoordinates.Z <= 0 || map.MapCoordinates.W <= 0)) return;
        if (!Finite(center) || !Finite(halfSize) || halfSize.X <= 0 || halfSize.Y <= 0
            || !Finite(viewport) || viewport.X <= 0 || viewport.Y <= 0
            || !float.IsFinite(corner) || corner < 0 || corner > Math.Min(visualHalf.X, visualHalf.Y)
            || !float.IsFinite(feather) || feather < 0 || feather > Math.Min(visualHalf.X, visualHalf.Y)
            || !float.IsFinite(seconds) || (circle && visualHalf.X != visualHalf.Y)) return;
        var control = Control.Instance();
        var targets = RenderTargetManager.Instance();
        if (control == null || targets == null || targets->DepthStencil == null) return;
        Matrix4x4 vp = control->ViewProjectionMatrix;
        if (!Finite(vp) || !Matrix4x4.Invert(vp, out var inverse) || !Finite(inverse)) return;
        var depth = targets->DepthStencil;
        if (!new DepthDimensions(depth->ActualWidth, depth->ActualHeight,
            depth->AllocatedWidth, depth->AllocatedHeight).IsValid) return;
        var depthView = (ID3D11ShaderResourceView*)depth->D3D11ShaderResourceView;
        if (depthView == null) return;
        if (!ReferenceEquals(uploadedHeights, heights) && !ValidHeights(heights)) return;
        if ((nint)device != devicePointer) { Dispose(); Create((ID3D11Device*)devicePointer); }
        ID3D11DeviceContext* baseContext = null;
        device->GetImmediateContext(&baseContext);
        var iid = IID.IID_ID3D11DeviceContext1;
        void* queried = null;
        var hr = baseContext->QueryInterface(&iid, &queried);
        baseContext->Release();
        Check(hr);
        var ctx = (ID3D11DeviceContext1*)queried;
        try
        {
            // SV_POSITION is in framebuffer pixels, not ImGui logical pixels.
            // Use the active background-pass viewport to cover high-DPI output
            // correctly. A non-origin/multi-viewport pass needs an explicit
            // projection contract and is deliberately not guessed here.
            D3D11_VIEWPORT* renderViewports = stackalloc D3D11_VIEWPORT[16];
            uint viewportCount = 16;
            ctx->RSGetViewports(&viewportCount, renderViewports);
            if (viewportCount != 1 || renderViewports[0].TopLeftX != 0 || renderViewports[0].TopLeftY != 0) return;
            viewport = new(renderViewports[0].Width, renderViewports[0].Height);
            if (!Finite(viewport) || viewport.X <= 0 || viewport.Y <= 0 || viewport.X > 16384 || viewport.Y > 16384) return;
            var constants = new Constants
            {
                Inverse = inverse,
                View = new(viewport.X, viewport.Y, depth->ActualWidth, depth->ActualHeight),
                Footprint = new(visualCenter.X, visualCenter.Y, visualHalf.X, visualHalf.Y),
                Style = new(circle ? 0 : 1, corner, 0.88f, 1),
                // Sign selects the floor representation without changing the
                // shipped constant-buffer ABI or loosening height acceptance.
                Material = new(seconds % 256, motion ? 1 : 0, 0.22f,
                    floorAtlas is null ? GridSize : -GeometryFloorAtlas.BinSize),
                Edges = new(rugEdges ? 1 : 0, feather, routeField is null ? 0 : 1, RouteRibbonField.GridSize),
                Ground = new(center.X, center.Y, halfSize.X, halfSize.Y),
                MapCoordinates = projection?.MapCoordinates ?? default,
                MapView = projection is { MapTexture: not 0 } mapping
                    ? new(mapping.MapCenter.X, mapping.MapCenter.Y, mapping.MapScale, mapping.MapBackground == 0 ? 1 : 2) : default,
                RouteArea = projection?.RouteArea ?? new(center.X, center.Y, halfSize.X, halfSize.Y),
                Travel = NormalizeTravel(travel, motion && rugEdges),
                RouteColor = projection?.RouteVisual?.Color ?? NavigationVisual.LegacyColor,
                RouteProgress = projection?.RouteVisual?.Arcs ?? default,
                RouteGuide = projection?.RouteVisual is { } guideVisual ? new(guideVisual.GuidePosition, 1) : default,
                RouteAhead = projection?.RouteVisual is { } aheadVisual ? new(aheadVisual.LookAheadPosition, 1) : default,
                Finish = new(float.IsFinite(wear) ? Math.Clamp(wear, 0, 1) : 0, 0, 0, 0),
                Foot0 = ClothFootClearance.Pack(feet, 0), Foot1 = ClothFootClearance.Pack(feet, 1),
                Foot2 = ClothFootClearance.Pack(feet, 2), Foot3 = ClothFootClearance.Pack(feet, 3),
            };
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(ctx->Map((ID3D11Resource*)buffer, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped));
            *(Constants*)mapped.pData = constants;
            ctx->Unmap((ID3D11Resource*)buffer, 0);
            if (!ReferenceEquals(uploadedHeights, heights))
            {
                fixed (float* values = heights)
                    ctx->UpdateSubresource((ID3D11Resource*)floor, 0, null, values, GridSize * 8, 0);
                uploadedHeights = heights;
            }
            if (floorAtlas is not null && !ReferenceEquals(uploadedAtlas, floorAtlas))
            {
                fixed (float* values = floorAtlas.TriangleTexels)
                    ctx->UpdateSubresource((ID3D11Resource*)triangles, 0, null, values,
                        GeometryFloorAtlas.TriangleTextureWidth * 16, 0);
                fixed (float* values = floorAtlas.IndexTexels)
                    ctx->UpdateSubresource((ID3D11Resource*)triangleIndices, 0, null, values,
                        GeometryFloorAtlas.IndexTextureWidth * 16, 0);
                uploadedAtlas = floorAtlas;
            }
            if (routeField is not null && !ReferenceEquals(uploadedRoute, routeField))
            {
                fixed (float* values = routeField)
                    ctx->UpdateSubresource((ID3D11Resource*)route, 0, null, values, RouteRibbonField.GridSize * 16, 0);
                uploadedRoute = routeField;
            }

            ID3D11PixelShader* oldPs = null;
            ID3D11VertexShader* oldVs = null;
            ID3D11GeometryShader* oldGs = null;
            ID3D11ClassInstance** psClasses = stackalloc ID3D11ClassInstance*[256];
            ID3D11ClassInstance** vsClasses = stackalloc ID3D11ClassInstance*[256];
            ID3D11ClassInstance** gsClasses = stackalloc ID3D11ClassInstance*[256];
            uint psCount = 256, vsCount = 256, gsCount = 256;
            ctx->PSGetShader(&oldPs, psClasses, &psCount);
            ctx->VSGetShader(&oldVs, vsClasses, &vsCount);
            ctx->GSGetShader(&oldGs, gsClasses, &gsCount);
            ID3D11ShaderResourceView** oldViews = stackalloc ID3D11ShaderResourceView*[7];
            ctx->PSGetShaderResources(1, 7, oldViews);
            ID3D11SamplerState* oldSampler = null;
            ctx->PSGetSamplers(0, 1, &oldSampler);
            ID3D11Buffer* oldBuffer = null;
            uint first = 0, count = 0;
            ctx->PSGetConstantBuffers1(0, 1, &oldBuffer, &first, &count);
            ID3D11InputLayout* oldLayout = null;
            D3D_PRIMITIVE_TOPOLOGY topology;
            ctx->IAGetInputLayout(&oldLayout);
            ctx->IAGetPrimitiveTopology(&topology);
            RECT* rectangles = stackalloc RECT[16];
            uint rectangleCount = 16;
            ctx->RSGetScissorRects(&rectangleCount, rectangles);
            try
            {
                var cb = buffer;
                ID3D11ShaderResourceView** views = stackalloc ID3D11ShaderResourceView*[7];
                views[0] = depthView; views[1] = floorView; views[2] = routeField is null ? null : routeView;
                views[3] = (ID3D11ShaderResourceView*)(projection?.MapTexture ?? 0);
                views[4] = (ID3D11ShaderResourceView*)(projection?.MapBackground ?? 0);
                views[5] = floorAtlas is null ? null : triangleView;
                views[6] = floorAtlas is null ? null : triangleIndexView;
                ctx->PSSetShaderResources(1, 7, views);
                var sampler = mapSampler;
                ctx->PSSetSamplers(0, 1, &sampler);
                ctx->PSSetConstantBuffers(0, 1, &cb);
                ctx->PSSetShader(pixel, null, 0);
                ctx->VSSetShader(vertex, null, 0);
                ctx->GSSetShader(null, null, 0);
                ctx->IASetInputLayout(null);
                ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                var rectangle = new RECT(0, 0, (int)viewport.X, (int)viewport.Y);
                ctx->RSSetScissorRects(1, &rectangle);
                ctx->Draw(3, 0);
                Submissions++;
                Status = "Rug draw submitted.";
            }
            finally
            {
                ctx->PSSetShader(oldPs, psClasses, psCount);
                ctx->VSSetShader(oldVs, vsClasses, vsCount);
                ctx->GSSetShader(oldGs, gsClasses, gsCount);
                ctx->PSSetShaderResources(1, 7, oldViews);
                ctx->PSSetSamplers(0, 1, &oldSampler);
                ctx->PSSetConstantBuffers1(0, 1, &oldBuffer, &first, &count);
                ctx->IASetInputLayout(oldLayout);
                ctx->IASetPrimitiveTopology(topology);
                ctx->RSSetScissorRects(rectangleCount, rectangles);
                if (oldPs != null) oldPs->Release();
                if (oldVs != null) oldVs->Release();
                if (oldGs != null) oldGs->Release();
                if (oldLayout != null) oldLayout->Release();
                if (oldBuffer != null) oldBuffer->Release();
                for (var i = 0; i < 7; i++) if (oldViews[i] != null) oldViews[i]->Release();
                if (oldSampler != null) oldSampler->Release();
                for (var i = 0; i < psCount; i++) psClasses[i]->Release();
                for (var i = 0; i < vsCount; i++) vsClasses[i]->Release();
                for (var i = 0; i < gsCount; i++) gsClasses[i]->Release();
            }
        }
        finally { ctx->Release(); }
    }

    private void Create(ID3D11Device* value)
    {
        device = value;
        device->AddRef();
        try
        {
            var ps = Bytecode("LiveRug");
            var vs = Bytecode("LiveRugVertex");
            ID3D11PixelShader* p = null;
            fixed (byte* data = ps) Check(device->CreatePixelShader(data, (nuint)ps.Length, null, &p));
            pixel = p;
            ID3D11VertexShader* v = null;
            fixed (byte* data = vs) Check(device->CreateVertexShader(data, (nuint)vs.Length, null, &v));
            vertex = v;
            var desc = new D3D11_BUFFER_DESC { ByteWidth = (uint)sizeof(Constants), Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER, CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE };
            ID3D11Buffer* b = null;
            Check(device->CreateBuffer(&desc, null, &b)); buffer = b;
            var td = new D3D11_TEXTURE2D_DESC { Width = GridSize, Height = GridSize, MipLevels = 1, ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT, SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT, BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE };
            ID3D11Texture2D* texture = null;
            Check(device->CreateTexture2D(&td, null, &texture)); floor = texture;
            ID3D11ShaderResourceView* srv = null;
            Check(device->CreateShaderResourceView((ID3D11Resource*)floor, null, &srv)); floorView = srv;
            td.Width = td.Height = RouteRibbonField.GridSize;
            td.Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT;
            texture = null;
            Check(device->CreateTexture2D(&td, null, &texture)); route = texture;
            srv = null;
            Check(device->CreateShaderResourceView((ID3D11Resource*)route, null, &srv)); routeView = srv;
            td.Width = GeometryFloorAtlas.TriangleTextureWidth;
            td.Height = GeometryFloorAtlas.TriangleTextureHeight;
            texture = null;
            Check(device->CreateTexture2D(&td, null, &texture)); triangles = texture;
            srv = null;
            Check(device->CreateShaderResourceView((ID3D11Resource*)triangles, null, &srv)); triangleView = srv;
            td.Width = GeometryFloorAtlas.IndexTextureWidth;
            td.Height = GeometryFloorAtlas.IndexTextureHeight;
            texture = null;
            Check(device->CreateTexture2D(&td, null, &texture)); triangleIndices = texture;
            srv = null;
            Check(device->CreateShaderResourceView((ID3D11Resource*)triangleIndices, null, &srv)); triangleIndexView = srv;
            var sd = new D3D11_SAMPLER_DESC
            {
                Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
                MaxLOD = float.MaxValue,
            };
            ID3D11SamplerState* s = null;
            Check(device->CreateSamplerState(&sd, &s)); mapSampler = s;
        }
        catch { Dispose(); throw; }
    }

    private static byte[] Bytecode(string name)
    {
        using var stream = typeof(XivSurface.Core.Footprint).Assembly.GetManifestResourceStream("XivSurface." + name + ".dxbc")!;
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    // Invalid animation input only quiets the textile; it must never suppress
    // otherwise valid floor/map rendering. The caller's heading/activity/phase
    // contract is enforced again at the GPU boundary.
    private static Vector4 NormalizeTravel(Vector4 travel, bool enabled)
    {
        var heading = new Vector2(travel.X, travel.Y);
        var length = heading.Length();
        if (!enabled || !Finite(heading) || !float.IsFinite(length) || length < 1e-5f
            || !float.IsFinite(travel.Z) || !float.IsFinite(travel.W) || travel.Z <= 0) return default;
        heading /= length;
        var phase = travel.W % MathF.Tau;
        if (phase < 0) phase += MathF.Tau;
        return new(heading, Math.Clamp(travel.Z, 0, 1), phase);
    }
    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
        && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
    private static bool ValidHeights(float[] heights)
    {
        for (var i = 0; i < heights.Length; i += 2)
            if (!float.IsFinite(heights[i]) || !float.IsFinite(heights[i + 1])
                || heights[i + 1] < 0 || heights[i + 1] > 1) return false;
        return true;
    }
    private static bool ValidAtlas(GeometryFloorAtlas atlas)
    {
        if (atlas.TriangleCount < 0 || atlas.TriangleCount > GeometryFloorAtlas.MaxTriangles
            || atlas.TriangleTexels.Length != GeometryFloorAtlas.TriangleTextureWidth * GeometryFloorAtlas.TriangleTextureHeight * 4
            || atlas.IndexTexels.Length != GeometryFloorAtlas.IndexTextureWidth * GeometryFloorAtlas.IndexTextureHeight * 4) return false;
        foreach (var value in atlas.TriangleTexels)
            if (!float.IsFinite(value) || Math.Abs(value) > 5000) return false;
        foreach (var id in atlas.IndexTexels)
            if (!float.IsFinite(id) || id < -1 || id > atlas.TriangleCount || id != MathF.Truncate(id)) return false;
        return true;
    }
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    public void Dispose()
    {
        uploadedHeights = null;
        uploadedRoute = null;
        uploadedAtlas = null;
        if (triangleIndexView != null) { triangleIndexView->Release(); triangleIndexView = null; }
        if (triangleIndices != null) { triangleIndices->Release(); triangleIndices = null; }
        if (triangleView != null) { triangleView->Release(); triangleView = null; }
        if (triangles != null) { triangles->Release(); triangles = null; }
        if (mapSampler != null) { mapSampler->Release(); mapSampler = null; }
        if (routeView != null) { routeView->Release(); routeView = null; }
        if (route != null) { route->Release(); route = null; }
        if (floorView != null) { floorView->Release(); floorView = null; }
        if (floor != null) { floor->Release(); floor = null; }
        if (buffer != null) { buffer->Release(); buffer = null; }
        if (vertex != null) { vertex->Release(); vertex = null; }
        if (pixel != null) { pixel->Release(); pixel = null; }
        if (device != null) { device->Release(); device = null; }
    }
}
