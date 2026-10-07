using System.Numerics;

namespace XivSurface.Core;

/// <summary>World X/Z to normalized game map texture coordinates. Map texture space is canonically 2048 pixels.</summary>
public readonly record struct WorldMapCoordinates(ushort SizeFactor, short OffsetX, short OffsetY)
{
    public bool IsValid => SizeFactor > 0;

    /// <summary>Shader convention: uv = (worldXZ + Transform.xy) * Transform.zw + 0.5.</summary>
    public Vector4 Transform => IsValid
        ? new(OffsetX, OffsetY, SizeFactor / 100f / 2048f, SizeFactor / 100f / 2048f)
        : Vector4.Zero;

    /// <summary>Texture coverage in world coordinates: minimum X/Z, maximum X/Z.</summary>
    public Vector4 WorldBounds
    {
        get
        {
            if (!IsValid) return Vector4.Zero;
            var halfSize = 1024f / (SizeFactor / 100f);
            return new(-OffsetX - halfSize, -OffsetY - halfSize, -OffsetX + halfSize, -OffsetY + halfSize);
        }
    }

    public bool TryWorldToUv(Vector2 worldXZ, out Vector2 uv)
    {
        uv = default;
        if (!IsValid || !float.IsFinite(worldXZ.X) || !float.IsFinite(worldXZ.Y)) return false;
        var scale = SizeFactor / 100f / 2048f;
        uv = (worldXZ + new Vector2(OffsetX, OffsetY)) * scale + new Vector2(0.5f);
        return float.IsFinite(uv.X) && float.IsFinite(uv.Y);
    }

    /// <summary>
    /// A rug displays a neighborhood rather than a literal rug-sized map fragment. normalizedRug is -1..1
    /// across its interior, and mapHalfExtent is the requested neighborhood radius/half-width in world yalms.
    /// </summary>
    public bool TryNeighborhoodUv(Vector2 playerXZ, Vector2 normalizedRug, Vector2 mapHalfExtent, out Vector2 uv)
    {
        uv = default;
        if (!float.IsFinite(normalizedRug.X) || !float.IsFinite(normalizedRug.Y) ||
            !float.IsFinite(mapHalfExtent.X) || !float.IsFinite(mapHalfExtent.Y) ||
            mapHalfExtent.X <= 0 || mapHalfExtent.Y <= 0) return false;
        return TryWorldToUv(playerXZ + normalizedRug * mapHalfExtent, out uv);
    }
}
