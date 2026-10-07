using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace XivSurface.Dalamud;

/// <summary>
/// Read-only lease of the game's directional visibility at its visible scene
/// receiver. Acquire synchronously at the verified native/backbuffer render
/// boundary, and dispose after restoring shader resources. This is NOT a world
/// shadow atlas: a consumer must reject pixels whose cloth world position does
/// not match the receiver reconstructed from scene depth.
/// </summary>
public sealed unsafe class NativeClothShadow
{
    private static readonly Guid VerifiedBindings = new("7fda2a82-4ca8-49d0-9687-4c8eac011509");
    private static readonly bool Compatible = VerifyBindings();
    public string Diagnostic { get; private set; } = "Native screen shadow has not been acquired.";

    // Matched RenderTargetManager: Shadow at0x50, DepthStencil at0x70.
    // Exact installed executable Initialize0x1402D52E0 creates Shadow as
    // TextureFormat.R8_UNORM (0x1132) at the full scene dimensions.
    // directionallighting.shpk SHA2562fc73ec9285e54a47727d96a83b56f576c90c1fb2429a4c84e07496e08b98ec3:
    // PS002 samples g_SamplerShadowMask(t8).r at the same UV as depth/G-buffers
    // and multiplies direct lighting. Native directionalshadow PS002 writes
    // comparison visibility unchanged:1 lit,0 shadow. No inversion/gamma.
    public Lease? Acquire(nint contextPointer, uint sceneDepthWidth, uint sceneDepthHeight)
    {
        if (!Compatible) return Missing("Native screen shadow bindings changed.");
        if (contextPointer == 0 || sceneDepthWidth is 0 or >16384 || sceneDepthHeight is 0 or >16384)
            return Missing("Native screen shadow needs a current context and scene depth dimensions.");
        var ctx = (ID3D11DeviceContext*)contextPointer;
        if (ctx->GetType() != D3D11_DEVICE_CONTEXT_TYPE.D3D11_DEVICE_CONTEXT_IMMEDIATE)
            return Missing("Native screen shadow requires the immediate context.");
        var manager = RenderTargetManager.Instance();
        if (manager == null) return Missing("Native render targets are unavailable.");
        var shadow = *(Texture**)((byte*)manager + 0x50);
        if (shadow == null || shadow->D3D11ShaderResourceView == null)
            return Missing("Native screen shadow texture/view is unavailable.");
        var width = shadow->ActualWidth; var height = shadow->ActualHeight;
        if (width != sceneDepthWidth || height != sceneDepthHeight
            || shadow->TextureFormat != TextureFormat.R8_UNORM)
            return Missing($"Native screen shadow rejected: format={shadow->TextureFormat}, actual={width}x{height}, scene={sceneDepthWidth}x{sceneDepthHeight}.");

        var view = (ID3D11ShaderResourceView*)shadow->D3D11ShaderResourceView;
        view->AddRef();
        ID3D11Resource* resource = null;
        ID3D11Texture2D* texture = null;
        ID3D11Device* contextDevice = null;
        ID3D11Device* viewDevice = null;
        try
        {
            D3D11_SHADER_RESOURCE_VIEW_DESC viewDescription;
            view->GetDesc(&viewDescription);
            if (viewDescription.Format != DXGI_FORMAT.DXGI_FORMAT_R8_UNORM
                || viewDescription.ViewDimension != D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D
                || viewDescription.Texture2D.MostDetailedMip != 0)
                return Missing($"Native screen shadow rejected view: {viewDescription.Format}/{viewDescription.ViewDimension}.");
            ctx->GetDevice(&contextDevice); view->GetDevice(&viewDevice);
            if (contextDevice == null || viewDevice == null || !SameObject((IUnknown*)contextDevice, (IUnknown*)viewDevice))
                return Missing("Native screen shadow belongs to a different device.");
            view->GetResource(&resource);
            if (resource == null) return Missing("Native screen shadow view has no resource.");
            var iid = IID.IID_ID3D11Texture2D; void* queried = null;
            if (resource->QueryInterface(&iid, &queried).FAILED || queried == null)
                return Missing("Native screen shadow is not a Texture2D.");
            texture = (ID3D11Texture2D*)queried;
            if (shadow->D3D11Texture2D == null || !SameObject((IUnknown*)texture, (IUnknown*)shadow->D3D11Texture2D))
                return Missing("Native screen shadow view/resource identity disagrees.");
            D3D11_TEXTURE2D_DESC description;
            texture->GetDesc(&description);
            if (description.Format != DXGI_FORMAT.DXGI_FORMAT_R8_UNORM || description.ArraySize != 1
                || description.SampleDesc.Count != 1 || description.MipLevels != 1
                || description.Width is 0 or >16384 || description.Height is 0 or >16384
                || width > description.Width || height > description.Height
                || description.Width != shadow->AllocatedWidth || description.Height != shadow->AllocatedHeight
                || (description.BindFlags & (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE) == 0)
                return Missing($"Native screen shadow rejected texture: {description.Format}, {description.Width}x{description.Height}, mips={description.MipLevels}, array={description.ArraySize}, samples={description.SampleDesc.Count}.");
            Diagnostic = $"Native screen shadow R8_UNORM red visibility: actual={width}x{height}, allocated={description.Width}x{description.Height}; same scene dimensions/device/resource; receiver depth match required.";
            var result = new Lease((nint)view, width, height, description.Width, description.Height);
            view = null; // The lease retains the COM reference, including its resource.
            return result;
        }
        finally
        {
            if (texture != null) texture->Release();
            if (resource != null) resource->Release();
            if (viewDevice != null) viewDevice->Release();
            if (contextDevice != null) contextDevice->Release();
            if (view != null) view->Release();
        }
    }

    public sealed class Lease : IDisposable
    {
        private nint view;
        public nint ShaderResourceView => view;
        public uint Width { get; }
        public uint Height { get; }
        public uint AllocatedWidth { get; }
        public uint AllocatedHeight { get; }
        internal Lease(nint view, uint width, uint height, uint allocatedWidth, uint allocatedHeight)
        { this.view = view; Width = width; Height = height; AllocatedWidth = allocatedWidth; AllocatedHeight = allocatedHeight; }
        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref view, 0);
            if (previous != 0) ((ID3D11ShaderResourceView*)previous)->Release();
        }
    }

    private Lease? Missing(string reason) { Diagnostic = reason; return null; }
    private static bool VerifyBindings()
    {
        try
        {
            return typeof(RenderTargetManager).Assembly.ManifestModule.ModuleVersionId == VerifiedBindings
                && sizeof(RenderTargetManager) == 0x740
                && Marshal.OffsetOf<RenderTargetManager>("Shadow").ToInt64() == 0x50
                && Marshal.OffsetOf<RenderTargetManager>(nameof(RenderTargetManager.DepthStencil)).ToInt64() == 0x70;
        }
        catch (ArgumentException) { return false; }
    }
    private static bool SameObject(IUnknown* left, IUnknown* right)
    {
        if (left == null || right == null) return false;
        var iid = IID.IID_IUnknown; void* a = null; void* b = null;
        try
        {
            return left->QueryInterface(&iid, &a).SUCCEEDED && right->QueryInterface(&iid, &b).SUCCEEDED && a == b;
        }
        finally
        {
            if (a != null) ((IUnknown*)a)->Release();
            if (b != null) ((IUnknown*)b)->Release();
        }
    }
}
