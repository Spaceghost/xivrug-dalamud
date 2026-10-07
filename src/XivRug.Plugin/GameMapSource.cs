using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using XivSurface.Core;

namespace XivRug.Plugin;

/// <summary>
/// Reads the actual CURRENT game map (not the player's browsed map), including its optional background layer.
/// Call TryAcquire on UiBuilder.Draw. Transfer the returned owned lease under the renderer's lock, and dispose
/// the replaced lease under that same lock so no render callback can borrow an already-released SRV.
/// </summary>
internal sealed unsafe class GameMapSource(IDataManager data, ITextureProvider textures) : IDisposable
{
    private uint cachedTerritory;
    private uint cachedMap;
    private WorldMapCoordinates coordinates;
    private string mainPath = "";
    private string backgroundPath = "";
    private ISharedImmediateTexture? main;
    private ISharedImmediateTexture? background;
    private bool disposed;
    public string Status { get; private set; } = "Waiting for the current game map.";

    public bool TryAcquire(uint territoryId, uint mapId, [NotNullWhen(true)] out GameMapFrame? frame)
    {
        frame = null;
        if (disposed) return false;
        try
        {
            var agent = AgentMap.Instance();
            if (territoryId == 0 || mapId == 0 || agent == null ||
                agent->CurrentTerritoryId != territoryId || agent->CurrentMapId != mapId)
                return Unavailable("Waiting for the game's current map after a zone/floor change.");

            // Mappy uses SelectedMapPath/SelectedMapBgPath + .tex. Their Current counterparts prevent
            // browsing another zone's map from replacing the floor map around the character.
            // Source: Mappy/MapRenderer/MapRenderer.Core.cs, DrawBackgroundTexture / LoadTexture.
            var currentMain = TexturePath(agent->CurrentMapPath.ToString());
            var currentBackground = TexturePath(agent->CurrentMapBgPath.ToString());
            if (currentMain.Length == 0) return Unavailable("This location has no current map texture.");
            if (cachedTerritory != territoryId || cachedMap != mapId || currentMain != mainPath || currentBackground != backgroundPath)
            {
                ClearCache();
                if (!data.GetExcelSheet<Map>().TryGetRow(mapId, out var row) ||
                    (row.TerritoryType.RowId != 0 && row.TerritoryType.RowId != territoryId))
                    return Unavailable("The current map metadata does not match this territory.");
                coordinates = new WorldMapCoordinates(row.SizeFactor, row.OffsetX, row.OffsetY);
                if (!coordinates.IsValid) return Unavailable("The current map has no usable scale.");
                main = textures.GetFromGame(currentMain);
                background = currentBackground.Length == 0 ? null : textures.GetFromGame(currentBackground);
                cachedTerritory = territoryId;
                cachedMap = mapId;
                mainPath = currentMain;
                backgroundPath = currentBackground;
            }

            if (main is null) return Unavailable("Waiting for the current map texture provider.");
            if (!main.TryGetWrap(out var mainWrap, out var error))
            {
                Status = error is null ? "Loading the current map texture." : "Could not load the current map texture.";
                return false;
            }
            IDalamudTextureWrap? backgroundWrap = null;
            if (background is not null && !background.TryGetWrap(out backgroundWrap, out error))
            {
                Status = error is null ? "Loading the current map background." : "Could not load the current map background.";
                return false;
            }

            frame = GameMapFrame.Acquire(territoryId, mapId, coordinates, mainPath, backgroundPath, mainWrap, backgroundWrap);
            Status = $"Game map {mapId}: {mainWrap.Width}×{mainWrap.Height}" + (backgroundWrap is null ? "." : " with background layer.");
            return true;
        }
        catch (Exception)
        {
            Status = "Game map texture is unavailable; no stale map will be used.";
            return false;
        }
    }

    private bool Unavailable(string status)
    {
        ClearCache();
        Status = status;
        return false;
    }

    private static string TexturePath(string gamePath)
    {
        if (string.IsNullOrEmpty(gamePath) || gamePath.Length > 256 ||
            !gamePath.StartsWith("ui/map/", StringComparison.Ordinal) || gamePath.Contains("..", StringComparison.Ordinal)) return "";
        return gamePath.EndsWith(".tex", StringComparison.Ordinal) ? gamePath : gamePath + ".tex";
    }

    private void ClearCache()
    {
        // ISharedImmediateTexture is provider-owned. Do not dispose/cache its borrowed per-frame wraps.
        cachedTerritory = cachedMap = 0;
        main = background = null;
        mainPath = backgroundPath = "";
        coordinates = default;
    }

    public void Dispose()
    {
        disposed = true;
        ClearCache();
    }
}

/// <summary>
/// Owned D3D11 map SRV leases. QueryInterface holds a COM reference independently of Dalamud's per-frame wraps.
/// Background absent means white; map RGB = foreground RGB * background RGB, matching the game's map layers.
/// Lifetime/access is synchronized by the consumer's render lock; pointers must not escape this lease.
/// </summary>
internal sealed unsafe class GameMapFrame : IDisposable
{
    private nint main;
    private nint background;
    public nint ShaderResourceView => main;
    public nint BackgroundShaderResourceView => background;
    public uint TerritoryId { get; }
    public uint MapId { get; }
    public Vector4 Transform { get; }
    public Vector4 WorldBounds { get; }
    public string TexturePath { get; }
    public string BackgroundTexturePath { get; }
    public int Width { get; }
    public int Height { get; }

    private GameMapFrame(uint territoryId, uint mapId, WorldMapCoordinates coordinates, string path, string backgroundPath,
        int width, int height, nint mainView, nint backgroundView)
    {
        TerritoryId = territoryId; MapId = mapId;
        Transform = coordinates.Transform; WorldBounds = coordinates.WorldBounds;
        TexturePath = path; BackgroundTexturePath = backgroundPath;
        Width = width; Height = height;
        main = mainView; background = backgroundView;
    }

    internal static GameMapFrame Acquire(uint territoryId, uint mapId, WorldMapCoordinates coordinates, string path,
        string backgroundPath, IDalamudTextureWrap mainWrap, IDalamudTextureWrap? backgroundWrap)
    {
        if (mainWrap.Width <= 0 || mainWrap.Height <= 0) throw new InvalidOperationException("Map texture is empty.");
        nint mainView = 0, backgroundView = 0;
        try
        {
            mainView = OwnShaderView(mainWrap);
            if (backgroundWrap is not null) backgroundView = OwnShaderView(backgroundWrap);
            var frame = new GameMapFrame(territoryId, mapId, coordinates, path, backgroundPath,
                mainWrap.Width, mainWrap.Height, mainView, backgroundView);
            mainView = backgroundView = 0; // references transferred to the lease
            return frame;
        }
        finally
        {
            if (mainView != 0) ((ID3D11ShaderResourceView*)mainView)->Release();
            if (backgroundView != 0) ((ID3D11ShaderResourceView*)backgroundView)->Release();
        }
    }

    private static nint OwnShaderView(IDalamudTextureWrap wrap)
    {
        var handle = wrap.Handle.Handle;
        if (handle == 0) throw new InvalidOperationException("Map texture has no resource.");
        var iid = IID.IID_ID3D11ShaderResourceView;
        void* result = null;
        Marshal.ThrowExceptionForHR(((IUnknown*)handle)->QueryInterface(&iid, &result));
        if (result == null) throw new InvalidOperationException("Map texture has no shader view.");
        return (nint)result;
    }

    public void Dispose()
    {
        var oldMain = Interlocked.Exchange(ref main, 0);
        var oldBackground = Interlocked.Exchange(ref background, 0);
        if (oldMain != 0) ((ID3D11ShaderResourceView*)oldMain)->Release();
        if (oldBackground != 0) ((ID3D11ShaderResourceView*)oldBackground)->Release();
    }
}
