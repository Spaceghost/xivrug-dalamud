using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using XivSurface.Core;

namespace XivSurface.Dalamud;

/// <summary>
/// Saves and restores exactly the pixel-stage slots used by SurfaceDecal. The
/// caller owns viewport, blend, depth, vertex stage and draw submission. This
/// is not itself a pre-UI hook. Use synchronously on the render thread only.
/// D3D11.1 is required so an existing constant-buffer subrange is preserved.
/// </summary>
public sealed unsafe class SurfacePixelScope : IDisposable
{
    private ID3D11DeviceContext1* context;
    private ID3D11PixelShader* oldShader;
    private ID3D11Buffer* oldBuffer;
    private ID3D11SamplerState* oldSampler;
    private readonly nint[] oldViews = new nint[4];
    private readonly nint[] oldClasses = new nint[256];
    private uint classCount, firstConstant, constantCount;

    private SurfacePixelScope(ID3D11DeviceContext1* context)
    {
        this.context = context; // owns QueryInterface's reference
        ID3D11PixelShader* shader = null;
        uint count = 256;
        fixed (nint* instances = oldClasses)
            context->PSGetShader(&shader, (ID3D11ClassInstance**)instances, &count);
        oldShader = shader;
        classCount = count;
        fixed (nint* views = oldViews)
            context->PSGetShaderResources(0, 4, (ID3D11ShaderResourceView**)views);
        ID3D11Buffer* buffer = null;
        uint first = 0, length = 0;
        context->PSGetConstantBuffers1(0, 1, &buffer, &first, &length);
        oldBuffer = buffer;
        firstConstant = first;
        constantCount = length;
        ID3D11SamplerState* sampler = null;
        context->PSGetSamplers(0, 1, &sampler);
        oldSampler = sampler;
    }

    /// <summary>
    /// Binds resources only for an attested pre-UI frame. All handles must remain
    /// alive through disposal, and every texture must be a shader-readable
    /// input, not simultaneously bound for writing by the caller.
    /// </summary>
    public static SurfacePixelScope Bind(SurfaceFrame frame, SurfaceConstants constants,
        nint contextPointer, SurfaceGpuResources resources, nint sceneDepth,
        nint classifiedFloorNormals, nint selectedFloor, nint mapColor)
    {
        if (!frame.CanRender || constants.FrameValidated != 1)
            throw new InvalidOperationException("Surface rendering requires a validated pre-UI frame.");
        ArgumentNullException.ThrowIfNull(resources);
        if (contextPointer == 0 || resources.Device == 0 || sceneDepth == 0
            || classifiedFloorNormals == 0 || selectedFloor == 0 || mapColor == 0)
            throw new ArgumentException("All compositor inputs must be present.");
        var source = (ID3D11DeviceContext*)contextPointer;
        RequireDevice((ID3D11DeviceChild*)source, resources.Device);
        ID3D11ShaderResourceView** views = stackalloc ID3D11ShaderResourceView*[4];
        views[0] = (ID3D11ShaderResourceView*)sceneDepth;
        views[1] = (ID3D11ShaderResourceView*)classifiedFloorNormals;
        views[2] = (ID3D11ShaderResourceView*)selectedFloor;
        views[3] = (ID3D11ShaderResourceView*)mapColor;
        for (var i = 0; i < 4; i++) RequireDevice((ID3D11DeviceChild*)views[i], resources.Device);
        // QueryInterface checks capability before any pipeline state changes.
        var iid = IID.IID_ID3D11DeviceContext1;
        void* queried = null;
        var hr = source->QueryInterface(&iid, &queried);
        if (hr < 0 || queried == null)
            throw new NotSupportedException("D3D11.1 context required for lossless state restoration.");
        var context = (ID3D11DeviceContext1*)queried;
        SurfacePixelScope? scope = null;
        try
        {
            var buffer = (ID3D11Buffer*)resources.ConstantBuffer;
            D3D11_MAPPED_SUBRESOURCE mapped;
            hr = context->Map((ID3D11Resource*)buffer, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped);
            if (hr < 0) throw new System.Runtime.InteropServices.COMException("Surface constants upload failed", hr);
            try { *(SurfaceConstants*)mapped.pData = constants; }
            finally { context->Unmap((ID3D11Resource*)buffer, 0); }
            scope = new SurfacePixelScope(context);
            context->PSSetShader((ID3D11PixelShader*)resources.PixelShader, null, 0);
            context->PSSetShaderResources(0, 4, views);
            context->PSSetConstantBuffers(0, 1, &buffer);
            var sampler = (ID3D11SamplerState*)resources.MapSampler;
            context->PSSetSamplers(0, 1, &sampler);
            return scope;
        }
        catch
        {
            if (scope is not null) scope.Dispose();
            else context->Release();
            throw;
        }
    }

    private static void RequireDevice(ID3D11DeviceChild* child, nint expected)
    {
        ID3D11Device* device = null;
        child->GetDevice(&device);
        var matches = (nint)device == expected;
        if (device != null) device->Release();
        if (!matches) throw new InvalidOperationException("Compositor resources belong to different devices.");
    }

    public void Dispose()
    {
        var ctx = context;
        if (ctx == null) return;
        context = null;
        fixed (nint* instances = oldClasses)
            ctx->PSSetShader(oldShader, (ID3D11ClassInstance**)instances, classCount);
        fixed (nint* views = oldViews)
            ctx->PSSetShaderResources(0, 4, (ID3D11ShaderResourceView**)views);
        var buffer = oldBuffer;
        var first = firstConstant;
        var length = constantCount;
        ctx->PSSetConstantBuffers1(0, 1, &buffer, &first, &length);
        var sampler = oldSampler;
        ctx->PSSetSamplers(0, 1, &sampler);
        // Every Get above AddRefs returned interfaces. Set does not consume
        // those references, so release each one after restoring the pipeline.
        if (oldShader != null) oldShader->Release();
        if (oldBuffer != null) oldBuffer->Release();
        if (oldSampler != null) oldSampler->Release();
        foreach (var view in oldViews) if (view != 0) ((ID3D11ShaderResourceView*)view)->Release();
        for (var i = 0; i < classCount; i++)
            if (oldClasses[i] != 0) ((ID3D11ClassInstance*)oldClasses[i])->Release();
        ctx->Release();
    }
}
