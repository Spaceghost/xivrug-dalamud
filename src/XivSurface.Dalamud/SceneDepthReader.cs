// Adapted from GhosttyDalamud HostApi.GetSceneDepth (MIT).
// Copyright (c) 2026 Johnneylee Jack Rollins. See THIRD-PARTY-NOTICES.md.
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using XivSurface.Core;

namespace XivSurface.Dalamud;

/// <summary>
/// Independent game-depth access. Call on the game's draw thread. Resource
/// discovery is NOT proof of a pre-UI stage or valid floor classification.
/// </summary>
public static unsafe class SceneDepthReader
{
    /// <summary>
    /// Synchronous consumer only. Never cache, release, or transfer the borrowed
    /// handles to another thread/frame. Queued GPU callbacks need a separately
    /// validated lifetime strategy. This method changes no graphics state.
    /// </summary>
    public static bool Visit(nint uiDevice, DepthConsumer consume)
    {
        ArgumentNullException.ThrowIfNull(consume);
        if (uiDevice == 0) return false;
        var targets = RenderTargetManager.Instance();
        if (targets == null || targets->DepthStencil == null) return false;
        var depth = targets->DepthStencil;
        var view = (nint)depth->D3D11ShaderResourceView;
        var dimensions = new DepthDimensions(depth->ActualWidth, depth->ActualHeight,
            depth->AllocatedWidth, depth->AllocatedHeight);
        if (view == 0 || !dimensions.IsValid) return false;
        consume(new BorrowedSceneDepth(view, uiDevice, dimensions));
        return true;
    }
}

public delegate void DepthConsumer(BorrowedSceneDepth depth);

/// <summary>Stack-only borrowed handles. No ownership transfer or disposal.</summary>
public readonly ref struct BorrowedSceneDepth(nint shaderResourceView, nint device, DepthDimensions dimensions)
{
    public nint ShaderResourceView { get; } = shaderResourceView;
    public nint Device { get; } = device;
    public DepthDimensions Dimensions { get; } = dimensions;
}
