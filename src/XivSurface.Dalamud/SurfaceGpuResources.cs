using TerraFX.Interop.DirectX;
using XivSurface.Core;

namespace XivSurface.Dalamud;

/// <summary>
/// Independently owned D3D11 resources for the surface compositor. Uses the same
/// device/resource creation approach as Ghostty's depth pass, not its plugin or
/// state. Call and dispose on the render thread; recreate after device reset.
/// Does not install a hook, submit draws, or change the context's pipeline state.
/// </summary>
public sealed unsafe class SurfaceGpuResources : IDisposable
{
    private ID3D11PixelShader* shader;
    private ID3D11Buffer* constants;
    private ID3D11SamplerState* sampler;
    private ID3D11Device* device;
    private bool disposed;

    public nint PixelShader => (nint)shader;
    public nint ConstantBuffer => (nint)constants;
    public nint MapSampler => (nint)sampler;
    public nint Device => (nint)device;

    private SurfaceGpuResources() { }

    public static SurfaceGpuResources Create(nint devicePointer) => Create(devicePointer, SurfaceShader.ReadBytecode());

    public static SurfaceGpuResources Create(nint devicePointer, ReadOnlySpan<byte> dxbc)
    {
        if (devicePointer == 0) throw new ArgumentException("No D3D11 device.", nameof(devicePointer));
        if (dxbc.Length < 32 || !dxbc[..4].SequenceEqual("DXBC"u8))
            throw new ArgumentException("Compiled DXBC shader required.", nameof(dxbc));
        var result = new SurfaceGpuResources { device = (ID3D11Device*)devicePointer };
        result.device->AddRef();
        try
        {
            ID3D11PixelShader* shader = null;
            fixed (byte* code = dxbc)
                Check(result.device->CreatePixelShader(code, (nuint)dxbc.Length, null, &shader), "CreatePixelShader");
            result.shader = shader;
            var description = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)sizeof(SurfaceConstants), Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            };
            ID3D11Buffer* buffer = null;
            Check(result.device->CreateBuffer(&description, null, &buffer), "CreateBuffer");
            result.constants = buffer;
            var sampling = new D3D11_SAMPLER_DESC
            {
                Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
                MaxLOD = float.MaxValue, MaxAnisotropy = 1,
            };
            ID3D11SamplerState* sampler = null;
            Check(result.device->CreateSamplerState(&sampling, &sampler), "CreateSamplerState");
            result.sampler = sampler;
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void Check(int hr, string operation)
    {
        if (hr < 0) throw new System.Runtime.InteropServices.COMException(operation, hr);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (sampler != null) { sampler->Release(); sampler = null; }
        if (constants != null) { constants->Release(); constants = null; }
        if (shader != null) { shader->Release(); shader = null; }
        if (device != null) { device->Release(); device = null; }
    }
}
