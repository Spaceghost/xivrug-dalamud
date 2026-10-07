using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace XivSurface.Dalamud;

/// <summary>
/// Native-stage pipeline envelope for LiveRugGpu. Use and dispose on the render
/// thread, or under the same lifetime lock as the draw callback. The nested
/// draw still owns PS/VS/GS, PS resources/constant buffers, IA and scissor state.
/// This class owns the remaining graphics state that ImGui normally establishes.
/// It does not identify the correct frame stage by itself.
/// </summary>
public sealed unsafe class NativeRugPipeline : IDisposable
{
    private ID3D11Device* device;
    private ID3D11BlendState* blend;
    private ID3D11DepthStencilState* depth;
    private ID3D11RasterizerState* raster;
    private bool drawing;
    public string Status { get; private set; } = "Native pipeline has not drawn yet.";

    /// <summary>
    /// Obtain the native immediate context from a known D3D device (for example
    /// UiBuilder.DeviceHandle), with a scoped COM reference. Do not substitute
    /// a guessed game-structure field for this interface. The callback runs
    /// synchronously on the calling render thread and must not retain context.
    /// </summary>
    public static bool VisitImmediateContext(nint devicePointer, Func<nint, bool> visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        if (devicePointer == 0) return false;
        ID3D11DeviceContext* context = null;
        try
        {
            try { ((ID3D11Device*)devicePointer)->GetImmediateContext(&context); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Native context probe failed at known-device GetImmediateContext.", ex);
            }
            return context != null && visit((nint)context);
        }
        finally { if (context != null) context->Release(); }
    }

    /// <summary>
    /// Only accepts the immediate context with the expected backbuffer as its
    /// sole color target. Pass the borrowed Texture.D3D11Texture2D pointer, not
    /// the game's Texture wrapper or a shader-resource-view pointer.
    /// </summary>
    public static bool IsBackBufferTarget(nint contextPointer, nint expectedBackBuffer)
        => IsBackBufferTarget(contextPointer, expectedBackBuffer, out _);

    /// <summary>Read-only target probe with a concise diagnostic for the first attempted frame.</summary>
    public static bool IsBackBufferTarget(nint contextPointer, nint expectedBackBuffer, out string diagnostic)
    {
        diagnostic = "Missing context or backbuffer texture.";
        if (contextPointer == 0 || expectedBackBuffer == 0) return false;
        var context = (ID3D11DeviceContext*)contextPointer;
        D3D11_DEVICE_CONTEXT_TYPE contextType;
        try { contextType = context->GetType(); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Native target probe failed at context.GetType (D3D11 vtable slot112).", ex);
        }
        if (contextType != D3D11_DEVICE_CONTEXT_TYPE.D3D11_DEVICE_CONTEXT_IMMEDIATE)
        { diagnostic = "Context is not immediate: " + contextType; return false; }
        ID3D11RenderTargetView** targets = stackalloc ID3D11RenderTargetView*[8];
        new Span<nint>(targets, 8).Clear();
        try { context->OMGetRenderTargets(8, targets, null); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Native target probe failed at context.OMGetRenderTargets (D3D11 vtable slot89).", ex);
        }
        ID3D11Resource* resource = null;
        try
        {
            if (targets[0] == null) { diagnostic = "Immediate context has no color target0."; return false; }
            for (var i = 1; i < 8; i++)
                if (targets[i] != null) { diagnostic = "Multiple color targets are still bound."; return false; }
            try { targets[0]->GetResource(&resource); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Native target probe failed at RTV0.GetResource.", ex);
            }
            if (resource == null) { diagnostic = "Color target0 returned no resource."; return false; }
            var matches = SameObject((IUnknown*)resource, (IUnknown*)expectedBackBuffer);
            diagnostic = matches ? "Known immediate context targets the expected backbuffer." : "Color target0 is not the expected backbuffer.";
            return matches;
        }
        finally
        {
            if (resource != null) resource->Release();
            for (var i = 0; i < 8; i++) if (targets[i] != null) targets[i]->Release();
        }
    }

    /// <summary>
    /// UiBuilder.Draw only: visit an already-bound exact backbuffer, or bind a
    /// temporary view when ALL color outputs are empty. Never replace another
    /// render target. Restore the complete output/input binding scope before
    /// releasing its COM references. The callback still uses the normal fully
    /// restoring pipeline and must finish synchronously.
    /// </summary>
    public static bool VisitUiBackBuffer(nint contextPointer, nint expectedBackBuffer,
        Func<nint, bool> draw, out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(draw);
        diagnostic = "Missing context or backbuffer texture.";
        if (contextPointer == 0 || expectedBackBuffer == 0) return false;
        var ctx = (ID3D11DeviceContext*)contextPointer;
        if (ctx->GetType() != D3D11_DEVICE_CONTEXT_TYPE.D3D11_DEVICE_CONTEXT_IMMEDIATE)
        { diagnostic = "UI fallback context is not immediate."; return false; }

        ID3D11RenderTargetView** oldTargets = stackalloc ID3D11RenderTargetView*[8];
        ID3D11UnorderedAccessView** oldUavs = stackalloc ID3D11UnorderedAccessView*[64];
        ID3D11UnorderedAccessView** oldComputeUavs = stackalloc ID3D11UnorderedAccessView*[64];
        ID3D11UnorderedAccessView** emptyUavs = stackalloc ID3D11UnorderedAccessView*[64];
        ID3D11ShaderResourceView** oldViews = stackalloc ID3D11ShaderResourceView*[6 * 128];
        uint* keepCounters = stackalloc uint[64];
        new Span<nint>(oldTargets, 8).Clear();
        new Span<nint>(oldUavs, 64).Clear();
        new Span<nint>(oldComputeUavs, 64).Clear();
        new Span<nint>(emptyUavs, 64).Clear();
        new Span<nint>(oldViews, 6 * 128).Clear();
        new Span<uint>(keepCounters, 64).Fill(uint.MaxValue);
        ID3D11DepthStencilView* oldDepth = null;
        ID3D11RenderTargetView* temporaryTarget = null;
        ID3D11Device* contextDevice = null;
        ID3D11Device* textureDevice = null;
        ID3D11DeviceContext* immediate = null;
        var texture = (ID3D11Texture2D*)expectedBackBuffer;
        texture->AddRef();
        uint uavSlots = 0;
        var bindingsChanged = false;
        try
        {
            ctx->OMGetRenderTargets(8, oldTargets, &oldDepth);
            var hasColorTarget = false;
            for (var i = 0; i < 8; i++) hasColorTarget |= oldTargets[i] != null;
            if (hasColorTarget)
            {
                if (!IsBackBufferTarget(contextPointer, expectedBackBuffer, out diagnostic)) return false;
                return draw(contextPointer);
            }

            ctx->GetDevice(&contextDevice);
            texture->GetDevice(&textureDevice);
            if (contextDevice == null || textureDevice == null
                || !SameObject((IUnknown*)contextDevice, (IUnknown*)textureDevice))
            { diagnostic = "UI fallback backbuffer belongs to a different device."; return false; }
            contextDevice->GetImmediateContext(&immediate);
            if (immediate == null || !SameObject((IUnknown*)immediate, (IUnknown*)ctx))
            { diagnostic = "UI fallback is not this device's immediate context."; return false; }
            D3D11_TEXTURE2D_DESC desc;
            texture->GetDesc(&desc);
            if (desc.Width is 0 or > 16384 || desc.Height is 0 or > 16384
                || desc.ArraySize != 1 || desc.SampleDesc.Count != 1
                || (desc.BindFlags & (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET) == 0)
            { diagnostic = "UI fallback backbuffer dimensions, sampling or bindings are unsupported."; return false; }

            uavSlots = contextDevice->GetFeatureLevel() >= D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1 ? 64u : 8u;
            ctx->OMGetRenderTargetsAndUnorderedAccessViews(0, null, null, 0, uavSlots, oldUavs);
            ctx->CSGetUnorderedAccessViews(0, uavSlots, oldComputeUavs);
            UiShaderResources(ctx, oldViews, restore: false);
            Check(contextDevice->CreateRenderTargetView((ID3D11Resource*)texture, null, &temporaryTarget));
            if (temporaryTarget == null)
            { diagnostic = "UI fallback could not create a backbuffer view."; return false; }

            bindingsChanged = true;
            // With zero RTVs, OM UAV slot zero can be occupied. Save/clear it
            // too. Compute UAVs and any stage's SRVs may alias the backbuffer;
            // binding the RTV can otherwise silently destroy those bindings.
            ctx->OMSetRenderTargetsAndUnorderedAccessViews(0, null, null, 0, uavSlots, emptyUavs, keepCounters);
            ctx->CSSetUnorderedAccessViews(0, uavSlots, emptyUavs, keepCounters);
            ctx->OMSetRenderTargets(1, &temporaryTarget, null);
            if (!IsBackBufferTarget(contextPointer, expectedBackBuffer, out diagnostic)) return false;
            diagnostic = "UI fallback temporarily bound the verified backbuffer from empty color outputs.";
            return draw(contextPointer);
        }
        finally
        {
            if (bindingsChanged)
            {
                // The nested pipeline has already restored its own shaders and
                // SRVs. Restore the initially empty color outputs, every OM/CS
                // UAV with KEEP counters, then inputs which the RTV may unbind.
                ctx->OMSetRenderTargetsAndUnorderedAccessViews(0, null, oldDepth, 0, uavSlots, oldUavs, keepCounters);
                ctx->CSSetUnorderedAccessViews(0, uavSlots, oldComputeUavs, keepCounters);
                UiShaderResources(ctx, oldViews, restore: true);
            }
            for (var i = 0; i < 8; i++) if (oldTargets[i] != null) oldTargets[i]->Release();
            for (var i = 0; i < 64; i++)
            {
                if (oldUavs[i] != null) oldUavs[i]->Release();
                if (oldComputeUavs[i] != null) oldComputeUavs[i]->Release();
            }
            for (var i = 0; i < 6 * 128; i++) if (oldViews[i] != null) oldViews[i]->Release();
            if (oldDepth != null) oldDepth->Release();
            if (temporaryTarget != null) temporaryTarget->Release();
            if (immediate != null) immediate->Release();
            if (textureDevice != null) textureDevice->Release();
            if (contextDevice != null) contextDevice->Release();
            texture->Release();
        }
    }

    private static void UiShaderResources(ID3D11DeviceContext* ctx, ID3D11ShaderResourceView** views, bool restore)
    {
        if (restore)
        {
            ctx->VSSetShaderResources(0, 128, views);
            ctx->HSSetShaderResources(0, 128, views + 128);
            ctx->DSSetShaderResources(0, 128, views + 256);
            ctx->GSSetShaderResources(0, 128, views + 384);
            ctx->PSSetShaderResources(0, 128, views + 512);
            ctx->CSSetShaderResources(0, 128, views + 640);
        }
        else
        {
            ctx->VSGetShaderResources(0, 128, views);
            ctx->HSGetShaderResources(0, 128, views + 128);
            ctx->DSGetShaderResources(0, 128, views + 256);
            ctx->GSGetShaderResources(0, 128, views + 384);
            ctx->PSGetShaderResources(0, 128, views + 512);
            ctx->CSGetShaderResources(0, 128, views + 640);
        }
    }

    /// <summary>
    /// Establishes a standalone fullscreen straight-alpha graphics pass,
    /// invokes draw with its borrowed D3D device, and restores all envelope
    /// state even when draw throws. A true result means submission ran, not
    /// that any shader pixels survived clipping or that ordering is verified.
    /// </summary>
    public bool TryDraw(nint contextPointer, nint expectedBackBuffer, Action<nint> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (drawing) throw new InvalidOperationException("Native rug draw cannot be re-entered.");
        if (!IsBackBufferTarget(contextPointer, expectedBackBuffer))
        { Status = "Skipped: not the sole backbuffer render target."; return false; }
        var ctx = (ID3D11DeviceContext*)contextPointer;
        D3D11_TEXTURE2D_DESC targetDescription;
        ((ID3D11Texture2D*)expectedBackBuffer)->GetDesc(&targetDescription);
        if (targetDescription.Width == 0 || targetDescription.Height == 0
            || targetDescription.Width > 16384 || targetDescription.Height > 16384
            || targetDescription.ArraySize != 1 || targetDescription.SampleDesc.Count != 1)
        { Status = "Skipped: unsupported backbuffer dimensions or sampling."; return false; }

        ID3D11Device* currentDevice = null;
        ID3D11DeviceContext* immediate = null;
        ctx->GetDevice(&currentDevice);
        try
        {
            if (currentDevice == null) return false;
            currentDevice->GetImmediateContext(&immediate);
            if (immediate == null || !SameObject((IUnknown*)immediate, (IUnknown*)ctx))
            { Status = "Skipped: callback is not this device's immediate context."; return false; }
            EnsureDevice(currentDevice);
        }
        finally
        {
            if (immediate != null) immediate->Release();
            if (currentDevice != null) currentDevice->Release();
        }

        ID3D11RenderTargetView** oldTargets = stackalloc ID3D11RenderTargetView*[8];
        ID3D11UnorderedAccessView** oldUavs = stackalloc ID3D11UnorderedAccessView*[64];
        ID3D11UnorderedAccessView** emptyUavs = stackalloc ID3D11UnorderedAccessView*[64];
        uint* keepCounters = stackalloc uint[64];
        new Span<nint>(oldTargets, 8).Clear();
        new Span<nint>(oldUavs, 64).Clear();
        new Span<nint>(emptyUavs, 64).Clear();
        new Span<uint>(keepCounters, 64).Fill(uint.MaxValue);
        var uavSlots = device->GetFeatureLevel() >= D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1 ? 64u : 8u;
        ID3D11DepthStencilView* oldDepthView = null;
        ctx->OMGetRenderTargets(8, oldTargets, &oldDepthView);
        ctx->OMGetRenderTargetsAndUnorderedAccessViews(0, null, null, 0, uavSlots, oldUavs);
        ID3D11BlendState* oldBlend = null;
        float* oldBlendFactors = stackalloc float[4];
        uint oldSampleMask = 0;
        ctx->OMGetBlendState(&oldBlend, oldBlendFactors, &oldSampleMask);
        ID3D11DepthStencilState* oldDepthState = null;
        uint oldStencilReference = 0;
        ctx->OMGetDepthStencilState(&oldDepthState, &oldStencilReference);
        ID3D11RasterizerState* oldRaster = null;
        ctx->RSGetState(&oldRaster);
        D3D11_VIEWPORT* oldViewports = stackalloc D3D11_VIEWPORT[16];
        uint oldViewportCount = 16;
        ctx->RSGetViewports(&oldViewportCount, oldViewports);
        ID3D11HullShader* oldHull = null;
        ID3D11DomainShader* oldDomain = null;
        ID3D11ClassInstance** hullClasses = stackalloc ID3D11ClassInstance*[256];
        ID3D11ClassInstance** domainClasses = stackalloc ID3D11ClassInstance*[256];
        uint hullCount = 256, domainCount = 256;
        ctx->HSGetShader(&oldHull, hullClasses, &hullCount);
        ctx->DSGetShader(&oldDomain, domainClasses, &domainCount);
        ID3D11Buffer** oldStreams = stackalloc ID3D11Buffer*[4];
        new Span<nint>(oldStreams, 4).Clear();
        ctx->SOGetTargets(4, oldStreams);
        ID3D11Predicate* oldPredicate = null;
        BOOL oldPredicateValue = default;
        ctx->GetPredication(&oldPredicate, &oldPredicateValue);

        drawing = true;
        try
        {
            // Remove the scene-depth OUTPUT binding before LiveRugGpu binds
            // its SRV. Otherwise D3D11 silently replaces that SRV with null.
            // UAV bindings are saved/restored with KEEP counters, not reset.
            ctx->OMSetRenderTargetsAndUnorderedAccessViews(1, oldTargets, null,
                1, uavSlots - 1, emptyUavs, null);
            ctx->OMSetBlendState(blend, null, uint.MaxValue);
            ctx->OMSetDepthStencilState(depth, 0);
            ctx->RSSetState(raster);
            var viewport = new D3D11_VIEWPORT(0, 0, targetDescription.Width, targetDescription.Height, 0, 1);
            ctx->RSSetViewports(1, &viewport);
            ctx->HSSetShader(null, null, 0);
            ctx->DSSetShader(null, null, 0);
            ctx->SOSetTargets(0, null, null);
            ctx->SetPredication(null, false);
            draw((nint)device);
            Status = "Native-stage pass submitted; visual verification required.";
            return true;
        }
        finally
        {
            // The nested draw must have restored its SRVs before this depth
            // output is rebound. Game-side state caches remain untouched.
            ctx->OMSetRenderTargetsAndUnorderedAccessViews(1, oldTargets, oldDepthView,
                1, uavSlots - 1, oldUavs + 1, keepCounters);
            ctx->OMSetBlendState(oldBlend, oldBlendFactors, oldSampleMask);
            ctx->OMSetDepthStencilState(oldDepthState, oldStencilReference);
            ctx->RSSetState(oldRaster);
            ctx->RSSetViewports(oldViewportCount, oldViewports);
            ctx->HSSetShader(oldHull, hullClasses, hullCount);
            ctx->DSSetShader(oldDomain, domainClasses, domainCount);
            // SOGetTargets specifies append offsets when restoring targets;
            // this preserves each target's existing write cursor.
            ctx->SOSetTargets(4, oldStreams, keepCounters);
            ctx->SetPredication(oldPredicate, oldPredicateValue);

            for (var i = 0; i < 8; i++) if (oldTargets[i] != null) oldTargets[i]->Release();
            for (var i = 0; i < uavSlots; i++) if (oldUavs[i] != null) oldUavs[i]->Release();
            for (var i = 0; i < 4; i++) if (oldStreams[i] != null) oldStreams[i]->Release();
            for (var i = 0; i < hullCount; i++) if (hullClasses[i] != null) hullClasses[i]->Release();
            for (var i = 0; i < domainCount; i++) if (domainClasses[i] != null) domainClasses[i]->Release();
            if (oldDepthView != null) oldDepthView->Release();
            if (oldBlend != null) oldBlend->Release();
            if (oldDepthState != null) oldDepthState->Release();
            if (oldRaster != null) oldRaster->Release();
            if (oldHull != null) oldHull->Release();
            if (oldDomain != null) oldDomain->Release();
            if (oldPredicate != null) oldPredicate->Release();
            drawing = false;
        }
    }

    private void EnsureDevice(ID3D11Device* value)
    {
        if (device == value) return;
        Dispose();
        device = value;
        device->AddRef();
        try
        {
            var bd = new D3D11_BLEND_DESC();
            bd.RenderTarget[0] = new D3D11_RENDER_TARGET_BLEND_DESC
            {
                BlendEnable = true,
                SrcBlend = D3D11_BLEND.D3D11_BLEND_SRC_ALPHA,
                DestBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
                BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE,
                DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
                BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
            };
            ID3D11BlendState* b = null;
            Check(device->CreateBlendState(&bd, &b)); blend = b;
            var dd = new D3D11_DEPTH_STENCIL_DESC
            {
                DepthEnable = false,
                DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO,
                DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS,
                StencilEnable = false,
                StencilReadMask = byte.MaxValue,
                StencilWriteMask = byte.MaxValue,
                FrontFace = new D3D11_DEPTH_STENCILOP_DESC
                {
                    StencilFailOp = D3D11_STENCIL_OP.D3D11_STENCIL_OP_KEEP,
                    StencilDepthFailOp = D3D11_STENCIL_OP.D3D11_STENCIL_OP_KEEP,
                    StencilPassOp = D3D11_STENCIL_OP.D3D11_STENCIL_OP_KEEP,
                    StencilFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS,
                },
            };
            dd.BackFace = dd.FrontFace;
            ID3D11DepthStencilState* d = null;
            Check(device->CreateDepthStencilState(&dd, &d)); depth = d;
            var rd = new D3D11_RASTERIZER_DESC
            {
                FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
                CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
                DepthClipEnable = true,
                ScissorEnable = false,
            };
            ID3D11RasterizerState* r = null;
            Check(device->CreateRasterizerState(&rd, &r)); raster = r;
        }
        catch { Dispose(); throw; }
    }

    private static bool SameObject(IUnknown* left, IUnknown* right)
    {
        if (left == null || right == null) return false;
        var iid = IID.IID_IUnknown;
        void* leftIdentity = null;
        void* rightIdentity = null;
        try
        {
            int leftResult, rightResult;
            try { leftResult = left->QueryInterface(&iid, &leftIdentity); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Native identity probe failed at first resource.QueryInterface(IUnknown).", ex);
            }
            if (leftResult < 0) return false;
            try { rightResult = right->QueryInterface(&iid, &rightIdentity); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Native identity probe failed at expected resource.QueryInterface(IUnknown).", ex);
            }
            return rightResult >= 0 && leftIdentity != null && leftIdentity == rightIdentity;
        }
        finally
        {
            if (leftIdentity != null) ((IUnknown*)leftIdentity)->Release();
            if (rightIdentity != null) ((IUnknown*)rightIdentity)->Release();
        }
    }

    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }

    public void Dispose()
    {
        if (drawing) throw new InvalidOperationException("Cannot release native pipeline states during a draw.");
        if (raster != null) { raster->Release(); raster = null; }
        if (depth != null) { depth->Release(); depth = null; }
        if (blend != null) { blend->Release(); blend = null; }
        if (device != null) { device->Release(); device = null; }
    }
}
