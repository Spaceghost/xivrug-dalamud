using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace XivSurface.Dalamud;

/// <summary>Bounded, read-only metadata snapshot taken immediately before a
/// native directional-shadow draw. Every getter's COM reference is released;
/// no native resource is retained, mapped, copied, or rebound.</summary>
public static unsafe class NativeShadowBindings
{
    public static string Capture(nint contextPointer, nint knownDevice, nint expectedPixelShader)
    {
        if (contextPointer == 0 || knownDevice == 0 || expectedPixelShader == 0)
            return "rejected: missing context/device/shader";
        var context = (ID3D11DeviceContext*)contextPointer;
        if (context->GetType() != D3D11_DEVICE_CONTEXT_TYPE.D3D11_DEVICE_CONTEXT_IMMEDIATE)
            return "rejected: deferred context";
        ID3D11Device* device = null;
        ID3D11PixelShader* pixel = null;
        ID3D11DeviceContext1* context1 = null;
        ID3D11RenderTargetView* target = null;
        ID3D11DepthStencilView* depth = null;
        ID3D11BlendState* blend = null;
        ID3D11DepthStencilState* depthState = null;
        ID3D11RasterizerState* raster = null;
        var buffers = stackalloc ID3D11Buffer*[6];
        var views = stackalloc ID3D11ShaderResourceView*[7];
        var samplers = stackalloc ID3D11SamplerState*[6];
        new Span<nint>(buffers, 6).Clear();
        new Span<nint>(views, 7).Clear();
        new Span<nint>(samplers, 6).Clear();
        var first = stackalloc uint[6]; var count = stackalloc uint[6];
        new Span<uint>(first, 6).Clear(); new Span<uint>(count, 6).Clear();
        try
        {
            context->GetDevice(&device);
            if (device == null || !SameObject((IUnknown*)device, (IUnknown*)knownDevice))
                return "rejected: different device";
            context->PSGetShader(&pixel, null, null);
            if (pixel == null || !SameObject((IUnknown*)pixel, (IUnknown*)expectedPixelShader))
                return "rejected: native/D3D pixel shader disagree";
            var iid = IID.IID_ID3D11DeviceContext1; void* queried = null;
            if (context->QueryInterface(&iid, &queried).SUCCEEDED && queried != null)
                context1 = (ID3D11DeviceContext1*)queried;
            if (context1 != null) context1->PSGetConstantBuffers1(0, 6, buffers, first, count);
            else context->PSGetConstantBuffers(0, 6, buffers);
            context->PSGetShaderResources(0, 7, views);
            context->PSGetSamplers(0, 6, samplers);
            context->OMGetRenderTargets(1, &target, &depth);
            float* factors = stackalloc float[4]; uint sampleMask = 0, stencil = 0;
            context->OMGetBlendState(&blend, factors, &sampleMask);
            context->OMGetDepthStencilState(&depthState, &stencil);
            context->RSGetState(&raster);
            var text = new StringBuilder(1024);
            text.Append("rt0=");
            if (target == null) text.Append("null");
            else
            {
                ID3D11Resource* resource = null;
                target->GetResource(&resource);
                try { text.Append(TextureMetadata(resource)); }
                finally { if (resource != null) resource->Release(); }
            }
            text.Append($" depth={(nint)depth:X} cbRanges={(context1 != null ? "16-byte" : "whole-buffer")}");
            for (var i = 0; i < 6; i++)
            {
                if (buffers[i] == null) continue;
                D3D11_BUFFER_DESC desc; buffers[i]->GetDesc(&desc);
                text.Append($" b{i}={(nint)buffers[i]:X}:{desc.ByteWidth}B/{desc.Usage},range={first[i]}+{count[i]}");
            }
            for (var i = 0; i < 7; i++)
            {
                if (views[i] == null) continue;
                D3D11_SHADER_RESOURCE_VIEW_DESC desc; views[i]->GetDesc(&desc);
                ID3D11Resource* resource = null; views[i]->GetResource(&resource);
                try { text.Append($" t{i}={desc.Format}/{desc.ViewDimension}/{TextureMetadata(resource)}"); }
                finally { if (resource != null) resource->Release(); }
            }
            for (var i = 0; i < 6; i++)
            {
                if (samplers[i] == null) continue;
                D3D11_SAMPLER_DESC desc; samplers[i]->GetDesc(&desc);
                text.Append($" s{i}={desc.Filter},compare={desc.ComparisonFunc},uvw={desc.AddressU}/{desc.AddressV}/{desc.AddressW}");
            }
            if (blend != null)
            {
                D3D11_BLEND_DESC desc; blend->GetDesc(&desc); var rt = desc.RenderTarget[0];
                text.Append($" blend={rt.BlendEnable},{rt.SrcBlend}/{rt.DestBlend}/{rt.BlendOp},write={rt.RenderTargetWriteMask:X}");
            }
            if (depthState != null)
            {
                D3D11_DEPTH_STENCIL_DESC desc; depthState->GetDesc(&desc);
                text.Append($" depthTest={desc.DepthEnable}/{desc.DepthFunc},stencil={desc.StencilEnable}/{stencil}");
            }
            if (raster != null)
            {
                D3D11_RASTERIZER_DESC desc; raster->GetDesc(&desc);
                text.Append($" cull={desc.CullMode},scissorEnabled={desc.ScissorEnable}");
            }
            uint rectangles = 16; var scissor = stackalloc RECT[16];
            context->RSGetScissorRects(&rectangles, scissor);
            for (var i = 0; i < Math.Min(rectangles, 16); i++)
                text.Append($" scissor{i}={scissor[i].left},{scissor[i].top},{scissor[i].right},{scissor[i].bottom}");
            uint viewportCount = 16; var viewport = stackalloc D3D11_VIEWPORT[16];
            context->RSGetViewports(&viewportCount, viewport);
            for (var i = 0; i < Math.Min(viewportCount, 16); i++)
                text.Append($" viewport{i}={viewport[i].TopLeftX},{viewport[i].TopLeftY},{viewport[i].Width},{viewport[i].Height}");
            return text.ToString();
        }
        finally
        {
            for (var i = 0; i < 6; i++)
            {
                if (buffers[i] != null) buffers[i]->Release();
                if (samplers[i] != null) samplers[i]->Release();
            }
            for (var i = 0; i < 7; i++) if (views[i] != null) views[i]->Release();
            if (raster != null) raster->Release();
            if (depthState != null) depthState->Release();
            if (blend != null) blend->Release();
            if (depth != null) depth->Release();
            if (target != null) target->Release();
            if (context1 != null) context1->Release();
            if (pixel != null) pixel->Release();
            if (device != null) device->Release();
        }
    }

    private static string TextureMetadata(ID3D11Resource* resource)
    {
        if (resource == null) return "null";
        var iid = IID.IID_ID3D11Texture2D; void* queried = null;
        if (resource->QueryInterface(&iid, &queried).FAILED || queried == null) return "non-Texture2D";
        var texture = (ID3D11Texture2D*)queried;
        try
        {
            D3D11_TEXTURE2D_DESC desc; texture->GetDesc(&desc);
            return $"{(nint)resource:X}:{desc.Format}:{desc.Width}x{desc.Height},array={desc.ArraySize},mips={desc.MipLevels},samples={desc.SampleDesc.Count}";
        }
        finally { texture->Release(); }
    }

    private static bool SameObject(IUnknown* a, IUnknown* b)
    {
        if (a == b) return true;
        if (a == null || b == null) return false;
        var iid = IID.IID_IUnknown; void* left = null; void* right = null;
        try
        {
            return a->QueryInterface(&iid, &left).SUCCEEDED && left != null
                && b->QueryInterface(&iid, &right).SUCCEEDED && right != null && left == right;
        }
        finally
        {
            if (right != null) ((IUnknown*)right)->Release();
            if (left != null) ((IUnknown*)left)->Release();
        }
    }
}
