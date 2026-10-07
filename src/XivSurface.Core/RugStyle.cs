using System.Numerics;

namespace XivSurface.Core;

/// <summary>Ground-projected textile detail; never displaces scene geometry.</summary>
public readonly record struct RugStyle(bool Enabled, float BorderWidth, float FringeLength,
    float ThreadsPerYalm, float DetailStrength, bool Animate)
{
    public static RugStyle Default => new(true, 0.18f, 0.22f, 48, 0.16f, true);
    public bool IsValid => float.IsFinite(BorderWidth) && BorderWidth is >= 0.02f and <= 0.5f
        && float.IsFinite(FringeLength) && FringeLength is >= 0.02f and <= 0.5f
        && float.IsFinite(ThreadsPerYalm) && ThreadsPerYalm is >= 8 and <= 96
        && float.IsFinite(DetailStrength) && DetailStrength is >= 0 and <= 0.35f;

    /// <summary>Two HLSL registers. Periodic clock avoids large-time precision loss.</summary>
    public bool TryPack(float seconds, out Vector4 geometry, out Vector4 material)
    {
        geometry = material = default;
        if (!IsValid || !float.IsFinite(seconds) || seconds < 0) return false;
        geometry = new(BorderWidth, FringeLength, ThreadsPerYalm, Animate ? 1 : 0);
        // Motion is sin(time * 2pi / 16), hence wraps continuously every 16s.
        material = new(seconds % 16, DetailStrength, Enabled ? 1 : 0, 0);
        return true;
    }
}
