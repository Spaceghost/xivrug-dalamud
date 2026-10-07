using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using XivSurface.Dalamud;
using IDXGISwapChain = TerraFX.Interop.DirectX.IDXGISwapChain;

namespace XivRug.Plugin;

/// <summary>
/// Development-only directional-shadow observer. The verified native prepare
/// operation has finished binding shaders, constants, textures and samplers;
/// its caller changes only primitive topology before issuing Draw. Observe
/// after Original, for at most sixteen actual presentation epochs. No rendering
/// resources or game state are changed; this does not itself cast a shadow.
/// </summary>
internal sealed unsafe class NativeShadowAtlasCapture : IDisposable
{
    private const string PackagePath = "shader/sm5/shpk/directionalshadow.shpk";
    private static readonly Guid VerifiedBindings = new("7fda2a82-4ca8-49d0-9687-4c8eac011509");
    private delegate void PrepareDraw(ImmediateContext* context, void* drawState, uint instances);
    private readonly Hook<PrepareDraw> hook;
    private readonly IPluginLog log;
    private readonly Func<nint> deviceProvider;
    private readonly object lifecycleGate = new(), observationGate = new();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> variants = new();
    private volatile nint[] shaders = [];
    private volatile bool disposed, faulted, complete;
    private int activeCallbacks, observedFrames, frameDraws, totalDraws;
    private nint lastSwapChain;
    private uint lastPresent;
    private long nextDiscovery;
    public string Status { get; private set; } = "Waiting for native directional-shadow shaders.";

    public NativeShadowAtlasCapture(IGameInteropProvider interop, ISigScanner scanner,
        IPluginLog log, Func<nint> deviceProvider)
    {
        this.log = log; this.deviceProvider = deviceProvider;
        var address = ResolvePrepareDraw(scanner);
        hook = interop.HookFromAddress<PrepareDraw>(address, Detour);
        try
        {
            FindShaders();
            hook.Enable();
            log.Information("XivRug shadow atlas observer: verified native pre-draw boundary {Address:X}; bounded to 16 Present epochs, 32 draws/epoch and 64 unique metadata records; no GPU copies or rendering writes.", address);
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Retry resource discovery on Framework.Update; no resource loads.</summary>
    public void FindShaders()
    {
        if (disposed || faulted || complete || shaders.Length != 0) return;
        var now = Environment.TickCount64;
        if (now < nextDiscovery) return;
        nextDiscovery = now + 1000;
        var manager = ResourceManager.Instance();
        if (manager == null || manager->ResourceGraph == null) return;
        var category = ResourceCategory.Shader; uint type = 0x7368706B;
        var crc = Lumina.Misc.Crc32.Get(PackagePath);
        foreach (var hashValue in new[] { ~crc, crc })
        {
            var hash = hashValue;
            var handle = manager->ResourceGraph->FindResourceHandle(&category, &type, &hash);
            if (handle == null || handle->FileType != type
                || handle->Type.Category != ResourceHandleType.HandleCategory.Shader
                || handle->FileName.Length > 256
                || !string.Equals(handle->FileName.ToString(), PackagePath, StringComparison.OrdinalIgnoreCase)) continue;
            var package = ((ShaderPackageResourceHandle*)handle)->ShaderPackage;
            // Exact installed package SHA256 1778b7a38122451a4fd3392a21e43f01817ddf1d446cec3ae4dae219ff516f1a.
            // Indices below are meaningful only for its 17 native pixel shaders.
            if (package == null || package->PixelShaders.Vector.Count != 17)
            { Status = "Directional-shadow package does not have the verified 17 pixel shaders."; continue; }
            var found = new nint[17];
            for (var i = 0; i < found.Length; i++) found[i] = (nint)package->PixelShaders.Vector[i].Value;
            if (found.Any(value => value == 0)) continue;
            shaders = found;
            Status = "Found 17 directional-shadow shaders; waiting for native draws.";
            log.Information("XivRug shadow atlas observer found directionalshadow.shpk, 17 pixel shader variants.");
            return;
        }
    }

    private void Detour(ImmediateContext* context, void* drawState, uint instances)
    {
        PrepareDraw original;
        lock (lifecycleGate) { original = hook.OriginalDisposeSafe; activeCallbacks++; }
        try
        {
            original(context, drawState, instances);
            if (disposed || faulted || complete || context == null) return;
            var current = context->CurrentPixelShader;
            if (current == null) return;
            var index = Array.IndexOf(shaders, (nint)current);
            if (index < 0) return;
            try { lock (observationGate) Observe(context, current, index, instances); }
            catch (Exception error)
            {
                faulted = true;
                Status = "Shadow observer stopped: " + error.GetType().Name;
                log.Warning(error, "XivRug shadow atlas observer stopped; game rendering is unchanged.");
            }
        }
        finally
        {
            lock (lifecycleGate) if (--activeCallbacks == 0) Monitor.PulseAll(lifecycleGate);
        }
    }

    private void Observe(ImmediateContext* context, PixelShader* shader, int variant, uint instances)
    {
        if (disposed || faulted || complete || context->D3D11DeviceContext_2 == null) return;
        var device = Device.Instance();
        if (device == null || device->SwapChain == null || device->SwapChain->DXGISwapChain == null) return;
        var swapChain = (IDXGISwapChain*)device->SwapChain->DXGISwapChain;
        uint present = 0;
        swapChain->AddRef();
        int result;
        try { result = swapChain->GetLastPresentCount(&present); }
        finally { swapChain->Release(); }
        if (result < 0) return;
        if (lastSwapChain != (nint)swapChain || lastPresent != present || observedFrames == 0)
        {
            if (observedFrames >= 16)
            {
                complete = true;
                Status = $"Observed {totalDraws} directional-shadow draws across {observedFrames} Present epochs.";
                log.Information("XivRug shadow atlas observer complete: {Draws} draws/{Frames} Present epochs; variants {Variants}; {Records} unique metadata records. No world-space replay enabled.",
                    totalDraws, observedFrames, string.Join(", ", variants.OrderBy(x => x.Key).Select(x => $"PS{x.Key:D3}:{x.Value}")), seen.Count);
                return;
            }
            observedFrames++; frameDraws = 0; lastSwapChain = (nint)swapChain; lastPresent = present;
        }
        if (++frameDraws > 32) return;
        totalDraws++; variants[variant] = variants.GetValueOrDefault(variant) + 1;
        if (seen.Count >= 64) return;
        var metadata = NativeShadowBindings.Capture((nint)context->D3D11DeviceContext_2,
            deviceProvider(), (nint)shader->DirectXObject);
        var key = $"PS{variant:D3} instances={instances} {metadata}";
        if (seen.Add(key))
            log.Information("XivRug shadow atlas observation present={Present}, epoch={Epoch}: {Metadata}", present, observedFrames, key);
    }

    private static nint ResolvePrepareDraw(ISigScanner scanner)
    {
        if (typeof(ImmediateContext).Module.ModuleVersionId != VerifiedBindings)
            throw new NotSupportedException("Shadow observer requires the verified native bindings.");
        var textStart = scanner.Module.BaseAddress + (nint)scanner.TextSectionOffset;
        var textEnd = textStart + scanner.TextSectionSize;
        var execute = (nint)ImmediateContext.MemberFunctionPointers.ExecuteCommands;
        if (execute < textStart || execute > textEnd - 0x400)
            throw new NotSupportedException("Native command executor is outside game text.");
        nint target = 0;
        // Verified installed executable: Draw, DrawIndexed and DrawIndexedInstanced
        // all call the same complete-state preparation operation before topology/Draw.
        foreach (var offset in new[] { 0x2D2, 0x35A, 0x3E7 })
        {
            var call = (byte*)(execute + offset);
            if (*call != 0xE8) throw new NotSupportedException("Native pre-draw call sites changed.");
            var resolved = (nint)(call + 5 + *(int*)(call + 1));
            if (resolved < textStart || resolved > textEnd - 15 || target != 0 && resolved != target)
                throw new NotSupportedException("Native draw cases no longer share the verified preparation.");
            target = resolved;
        }
        ReadOnlySpan<byte> prolog = [0x40, 0x56, 0x41, 0x56, 0x48, 0x83, 0xEC, 0x48, 0x80, 0x7A, 0x49, 0x00, 0x4C, 0x8B, 0xF2];
        if (!new ReadOnlySpan<byte>((void*)target, prolog.Length).SequenceEqual(prolog))
            throw new NotSupportedException("Native draw preparation prolog changed or is already intercepted.");
        return target;
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            disposed = true;
            if (hook.IsDisposed) return;
            hook.Disable();
            while (activeCallbacks != 0) Monitor.Wait(lifecycleGate);
            hook.Dispose();
            shaders = [];
        }
    }
}
