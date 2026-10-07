using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using XivSurface.Core;

namespace XivSurface.Dalamud;

/// <summary>Bounded emissive world-space music triangles. Call synchronously
/// INSIDE NativeRugPipeline's native-before-UI envelope and under the caller's
/// render/disposal lock. Input XYZ must already be grounded by the publisher.
/// Real scene depth occludes the triangles; no game depth/stencil is written.
/// Additive RGB is order independent and preserves destination alpha. This is
/// not a lighting/shadow source, a floor detector, or a renderer-stage selector.</summary>
public sealed unsafe class MusicGpu : IDisposable
{
    public const int MaximumVertices = 2304;
    private ID3D11Device* device;
    private ID3D11VertexShader* vertex;
    private ID3D11PixelShader* pixel;
    private ID3D11InputLayout* layout;
    private ID3D11Buffer* vertices;
    private ID3D11Buffer* constants;
    private ID3D11BlendState* blend;
    private ID3D11DepthStencilState* depthState;
    private bool drawing;
    public long Submissions { get; private set; }
    public string Status { get; private set; } = "Waiting for played-audio geometry.";

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct UploadVertex { public Vector3 Position; public Vector4 Color; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProjection, InverseViewProjection;
        public Vector4 Camera, View;
    }

    /// <summary>Copies at most2304 triangle-list vertices. Refuses nonfinite or
    /// out-of-budget geometry. Caller keeps the input immutable for this call.
    /// canSubmit runs after uploads immediately before
    /// CPU submission; it does not attest GPU presentation-time freshness.</summary>
    public void Draw(nint devicePointer, ReadOnlySpan<MusicVertex> worldVertices, Func<bool>? canSubmit = null)
    {
        if (drawing || devicePointer == 0 || worldVertices.Length is < 3 or > MaximumVertices
            || worldVertices.Length % 3 != 0) return;
        foreach (var item in worldVertices)
            if (!Finite(item.Position) || !Finite(item.Color)
                || item.Color.X is < 0 or > 1 || item.Color.Y is < 0 or > 1
                || item.Color.Z is < 0 or > 1 || item.Color.W is < 0 or > 1) return;
        var control = Control.Instance(); var targets = RenderTargetManager.Instance();
        var cameras = CameraManager.Instance(); var camera = cameras == null ? null : cameras->GetActiveCamera();
        if (control == null || camera == null || targets == null || targets->DepthStencil == null) return;
        var depth = targets->DepthStencil;
        if (depth->D3D11ShaderResourceView == null || depth->ActualWidth is 0 or > 16384 || depth->ActualHeight is 0 or > 16384
            || depth->ActualWidth > depth->AllocatedWidth || depth->ActualHeight > depth->AllocatedHeight) return;
        Matrix4x4 vp = control->ViewProjectionMatrix;
        if (!Finite(vp) || !Matrix4x4.Invert(vp, out var inverse) || !Finite(inverse)) return;
        var cameraPosition = (Vector3)camera->SceneCamera.Position;
        if (!Finite(cameraPosition)) return;
        if ((nint)device != devicePointer) { Dispose(); Create((ID3D11Device*)devicePointer); }
        ID3D11DeviceContext* baseContext = null;
        ID3D11DeviceContext1* ctx = null;
        var scene = (ID3D11ShaderResourceView*)depth->D3D11ShaderResourceView;
        scene->AddRef();
        drawing = true;
        try
        {
            device->GetImmediateContext(&baseContext);
            if (baseContext == null) return;
            var iid = IID.IID_ID3D11DeviceContext1; void* queried = null;
            Check(baseContext->QueryInterface(&iid, &queried)); ctx = (ID3D11DeviceContext1*)queried;
            if (ctx == null) return;
            var viewport = new D3D11_VIEWPORT(); uint count = 1;
            ctx->RSGetViewports(&count, &viewport);
            if (count != 1 || viewport.TopLeftX != 0 || viewport.TopLeftY != 0
                || !float.IsFinite(viewport.Width) || !float.IsFinite(viewport.Height)
                || viewport.Width is <= 0 or > 16384 || viewport.Height is <= 0 or > 16384) return;
            // The outer envelope must have removed game depth OUTPUT before
            // we bind its SRV. Never replace an unknown target/depth here.
            ID3D11RenderTargetView** targetsNow = stackalloc ID3D11RenderTargetView*[8];
            new Span<nint>(targetsNow, 8).Clear(); ID3D11DepthStencilView* depthNow = null;
            ctx->OMGetRenderTargets(8, targetsNow, &depthNow);
            var envelope = targetsNow[0] != null && depthNow == null;
            for (var i = 1; i < 8; i++) envelope &= targetsNow[i] == null;
            for (var i = 0; i < 8; i++) if (targetsNow[i] != null) targetsNow[i]->Release();
            if (depthNow != null) depthNow->Release();
            if (!envelope) { Status = "Refused: native-before-UI pipeline envelope required."; return; }
            D3D11_SHADER_RESOURCE_VIEW_DESC sceneDescription;
            scene->GetDesc(&sceneDescription);
            if (sceneDescription.ViewDimension != D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D) return;

            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(ctx->Map((ID3D11Resource*)vertices, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped));
            try
            {
                var output = new Span<UploadVertex>(mapped.pData, worldVertices.Length);
                for (var i = 0; i < output.Length; i++) output[i] = new() { Position = worldVertices[i].Position, Color = worldVertices[i].Color };
            }
            finally { ctx->Unmap((ID3D11Resource*)vertices, 0); }
            var values = new Constants { ViewProjection = vp, InverseViewProjection = inverse,
                Camera = new(cameraPosition, 1), View = new(viewport.Width, viewport.Height, depth->ActualWidth, depth->ActualHeight) };
            Check(ctx->Map((ID3D11Resource*)constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped));
            try { *(Constants*)mapped.pData = values; }
            finally { ctx->Unmap((ID3D11Resource*)constants, 0); }

            ID3D11PixelShader* oldPs = null; ID3D11VertexShader* oldVs = null; ID3D11GeometryShader* oldGs = null;
            ID3D11ClassInstance** psClasses = stackalloc ID3D11ClassInstance*[256];
            ID3D11ClassInstance** vsClasses = stackalloc ID3D11ClassInstance*[256];
            ID3D11ClassInstance** gsClasses = stackalloc ID3D11ClassInstance*[256];
            uint psCount = 256, vsCount = 256, gsCount = 256;
            ctx->PSGetShader(&oldPs, psClasses, &psCount); ctx->VSGetShader(&oldVs, vsClasses, &vsCount); ctx->GSGetShader(&oldGs, gsClasses, &gsCount);
            ID3D11ShaderResourceView* oldView = null; ctx->PSGetShaderResources(0, 1, &oldView);
            ID3D11Buffer* oldPsBuffer = null; ID3D11Buffer* oldVsBuffer = null;
            uint psFirst = 0, psLength = 0, vsFirst = 0, vsLength = 0;
            ctx->PSGetConstantBuffers1(0, 1, &oldPsBuffer, &psFirst, &psLength);
            ctx->VSGetConstantBuffers1(0, 1, &oldVsBuffer, &vsFirst, &vsLength);
            ID3D11InputLayout* oldLayout = null; D3D_PRIMITIVE_TOPOLOGY oldTopology;
            ctx->IAGetInputLayout(&oldLayout); ctx->IAGetPrimitiveTopology(&oldTopology);
            ID3D11Buffer* oldVertices = null; uint oldStride = 0, oldOffset = 0;
            ctx->IAGetVertexBuffers(0, 1, &oldVertices, &oldStride, &oldOffset);
            ID3D11BlendState* oldBlend = null; float* oldFactors = stackalloc float[4]; uint oldMask = 0;
            ctx->OMGetBlendState(&oldBlend, oldFactors, &oldMask);
            ID3D11DepthStencilState* oldDepthState = null; uint oldStencil = 0;
            ctx->OMGetDepthStencilState(&oldDepthState, &oldStencil);
            try
            {
                var cb = constants; var vb = vertices; uint stride = (uint)sizeof(UploadVertex), offset = 0;
                ctx->VSSetConstantBuffers(0, 1, &cb); ctx->PSSetConstantBuffers(0, 1, &cb);
                ctx->VSSetShader(vertex, null, 0); ctx->PSSetShader(pixel, null, 0); ctx->GSSetShader(null, null, 0);
                ctx->PSSetShaderResources(0, 1, &scene);
                ctx->IASetInputLayout(layout); ctx->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
                ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                ctx->OMSetBlendState(blend, null, uint.MaxValue); ctx->OMSetDepthStencilState(depthState, 0);
                // D3D can silently replace an SRV with null on an output
                // hazard. A null depth read looks like background; refuse it
                // instead of accidentally drawing through the whole scene.
                ID3D11ShaderResourceView* boundDepth = null; ctx->PSGetShaderResources(0, 1, &boundDepth);
                var depthBound = boundDepth == scene;
                if (boundDepth != null) boundDepth->Release();
                if (!depthBound) { Status = "Refused: scene-depth input was not bound."; return; }
                if (canSubmit is not null && !canSubmit()) return;
                ctx->Draw((uint)worldVertices.Length, 0);
                Submissions++; Status = "Depth-occluded music geometry submitted.";
            }
            finally
            {
                ctx->PSSetShaderResources(0, 1, &oldView);
                ctx->VSSetShader(oldVs, vsClasses, vsCount); ctx->PSSetShader(oldPs, psClasses, psCount); ctx->GSSetShader(oldGs, gsClasses, gsCount);
                ctx->PSSetConstantBuffers1(0, 1, &oldPsBuffer, &psFirst, &psLength); ctx->VSSetConstantBuffers1(0, 1, &oldVsBuffer, &vsFirst, &vsLength);
                ctx->IASetInputLayout(oldLayout); ctx->IASetPrimitiveTopology(oldTopology); ctx->IASetVertexBuffers(0, 1, &oldVertices, &oldStride, &oldOffset);
                ctx->OMSetBlendState(oldBlend, oldFactors, oldMask); ctx->OMSetDepthStencilState(oldDepthState, oldStencil);
                if (oldPs != null) oldPs->Release(); if (oldVs != null) oldVs->Release(); if (oldGs != null) oldGs->Release();
                if (oldView != null) oldView->Release(); if (oldPsBuffer != null) oldPsBuffer->Release(); if (oldVsBuffer != null) oldVsBuffer->Release();
                if (oldLayout != null) oldLayout->Release(); if (oldVertices != null) oldVertices->Release();
                if (oldBlend != null) oldBlend->Release(); if (oldDepthState != null) oldDepthState->Release();
                for (var i = 0; i < psCount; i++) if (psClasses[i] != null) psClasses[i]->Release();
                for (var i = 0; i < vsCount; i++) if (vsClasses[i] != null) vsClasses[i]->Release();
                for (var i = 0; i < gsCount; i++) if (gsClasses[i] != null) gsClasses[i]->Release();
            }
        }
        finally
        {
            if (ctx != null) ctx->Release(); if (baseContext != null) baseContext->Release();
            scene->Release(); drawing = false;
        }
    }

    private void Create(ID3D11Device* value)
    {
        device = value; device->AddRef();
        try
        {
            if (sizeof(UploadVertex) != 28 || sizeof(Constants) != 160) throw new InvalidOperationException("Music shader ABI mismatch.");
            var vs = Bytecode("MusicVertex"); var ps = Bytecode("MusicPixel");
            ID3D11VertexShader* v = null; ID3D11PixelShader* p = null;
            fixed (byte* b = vs) Check(device->CreateVertexShader(b, (nuint)vs.Length, null, &v)); vertex = v;
            fixed (byte* b = ps) Check(device->CreatePixelShader(b, (nuint)ps.Length, null, &p)); pixel = p;
            var bd = new D3D11_BUFFER_DESC { Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER, ByteWidth = (uint)sizeof(Constants) };
            ID3D11Buffer* buffer = null; Check(device->CreateBuffer(&bd, null, &buffer)); constants = buffer;
            bd.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER; bd.ByteWidth = MaximumVertices * 28;
            buffer = null; Check(device->CreateBuffer(&bd, null, &buffer)); vertices = buffer;
            fixed (byte* position = "POSITION\0"u8, color = "COLOR\0"u8, code = vs)
            {
                D3D11_INPUT_ELEMENT_DESC* elements = stackalloc D3D11_INPUT_ELEMENT_DESC[2];
                elements[0] = new() { SemanticName = (sbyte*)position, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT, AlignedByteOffset = 0 };
                elements[1] = new() { SemanticName = (sbyte*)color, Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT, AlignedByteOffset = 12 };
                ID3D11InputLayout* result = null; Check(device->CreateInputLayout(elements, 2, code, (nuint)vs.Length, &result)); layout = result;
            }
            var blendDescription = new D3D11_BLEND_DESC();
            blendDescription.RenderTarget[0] = new() { BlendEnable = true,
                SrcBlend = D3D11_BLEND.D3D11_BLEND_ONE, DestBlend = D3D11_BLEND.D3D11_BLEND_ONE,
                BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ZERO, DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE,
                BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL };
            ID3D11BlendState* createdBlend = null; Check(device->CreateBlendState(&blendDescription, &createdBlend)); blend = createdBlend;
            var dd = new D3D11_DEPTH_STENCIL_DESC { DepthEnable = false,
                DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO,
                DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS, StencilEnable = false };
            ID3D11DepthStencilState* d = null; Check(device->CreateDepthStencilState(&dd, &d)); depthState = d;
        }
        catch { Dispose(); throw; }
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)
        && Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))) <= 100000;
    private static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);
    private static bool Finite(Matrix4x4 m) => Finite(new Vector4(m.M11,m.M12,m.M13,m.M14)) && Finite(new Vector4(m.M21,m.M22,m.M23,m.M24))
        && Finite(new Vector4(m.M31,m.M32,m.M33,m.M34)) && Finite(new Vector4(m.M41,m.M42,m.M43,m.M44));
    private static byte[] Bytecode(string name)
    {
        using var stream = typeof(XivSurface.Core.Footprint).Assembly.GetManifestResourceStream("XivSurface." + name + ".dxbc")
            ?? throw new InvalidOperationException("Music shader was not embedded: " + name);
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    public void Dispose()
    {
        if (drawing) throw new InvalidOperationException("Cannot dispose music resources during submission.");
        if (depthState != null) { depthState->Release(); depthState = null; }
        if (blend != null) { blend->Release(); blend = null; }
        if (layout != null) { layout->Release(); layout = null; }
        if (constants != null) { constants->Release(); constants = null; }
        if (vertices != null) { vertices->Release(); vertices = null; }
        if (pixel != null) { pixel->Release(); pixel = null; }
        if (vertex != null) { vertex->Release(); vertex = null; }
        if (device != null) { device->Release(); device = null; }
    }
}
