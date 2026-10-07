using System.Numerics;

namespace XivSurface.Core;

/// <summary>Observer-owned equality tokens, never dereferenced here. Equipment
/// and scale participate separately from the existing skeleton generation.
/// Identical pointers cannot reveal an unobserved in-place resource rebuild.</summary>
public readonly record struct RawFootIdentity(ClothFootCaptureIdentity Actor,
    nint FeetModel, nint FeetResource, Vector3 Scale, long CaptureGeneration,
    uint EnabledAttributes = 0, uint EnabledShapes = 0)
{
    public bool Valid => Actor.Bound && FeetModel != 0 && FeetResource != 0 && CaptureGeneration > 0
        && FootModelMath.Finite(Scale) && Scale.X is >= .2f and <= 4
        && Scale.Y is >= .2f and <= 4 && Scale.Z is >= .2f and <= 4;
}

public readonly record struct RawFootMarker(Vector3 Position, Quaternion Rotation)
{
    // Model-space joint transform scale, independent of Skeleton.Transform.Scale.
    // The current calibrated model refuses non-unit joint scale rather than
    // silently pretending translation/rotation describe a complete transform.
    public Vector3 LocalScale { get; init; } = Vector3.One;
}

/// <summary>Four actual model-space markers transformed by the observer into
/// world space, in heel/toe left then heel/toe right order. Not shoe vertices,
/// planted-state proof, or a GPU-frame attestation. No render-sole inset.</summary>
public sealed class RawFootFrame
{
    public const double MaximumReadAge = 1d / 30;
    private readonly RawFootMarker[] markers;
    public RawFootIdentity Identity { get; }
    public long Sequence { get; }
    public double SampledAt { get; }
    public double ReadCompletedAt { get; }
    public Vector3 PlayerPosition { get; }
    public ReadOnlySpan<RawFootMarker> Markers => markers;
    private RawFootFrame(RawFootIdentity identity, long sequence, double start, double end,
        Vector3 player, RawFootMarker[] markers)
    { Identity = identity; Sequence = sequence; SampledAt = start; ReadCompletedAt = end;
        PlayerPosition = player; this.markers = markers; }
    public bool FreshAt(double now) => double.IsFinite(now) && now >= ReadCompletedAt
        && now <= SampledAt + MaximumReadAge;

    public static bool TryCapture(RawFootIdentity before, RawFootIdentity after, long sequence,
        double readStartedAt, double readCompletedAt, Vector3 player,
        ReadOnlySpan<RawFootMarker> markers, out RawFootFrame? frame)
    {
        frame = null;
        if (!before.Valid || before != after || sequence <= 0 || markers.Length != 4
            || !double.IsFinite(readStartedAt) || readStartedAt < 0
            || !double.IsFinite(readCompletedAt) || readCompletedAt < readStartedAt
            || readCompletedAt > readStartedAt + MaximumReadAge || !FootModelMath.World(player)) return false;
        foreach (var marker in markers)
        {
            if (!FootModelMath.World(marker.Position) || !FootModelMath.Valid(marker.Rotation)
                || !FootModelMath.Finite(marker.LocalScale) || marker.LocalScale.X is < .2f or > 4
                || marker.LocalScale.Y is < .2f or > 4 || marker.LocalScale.Z is < .2f or > 4
                || Vector2.Distance(new(marker.Position.X, marker.Position.Z), new(player.X, player.Z)) > 2
                || Math.Abs(marker.Position.Y - player.Y) > 1.5f) return false;
        }
        for (var i = 0; i < 4; i += 2)
            if (Vector3.Distance(markers[i].Position, markers[i + 1].Position) is < .015f or > .75f) return false;
        var owned = markers.ToArray();
        for (var i = 0; i < owned.Length; i++) owned[i] = owned[i] with { Rotation = Quaternion.Normalize(owned[i].Rotation) };
        frame = new(before, sequence, readStartedAt, readCompletedAt, player, owned);
        return true;
    }
}

internal static class FootModelMath
{
    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    internal static bool World(Vector3 v) => Finite(v) && Math.Abs(v.X) <= 5000 && Math.Abs(v.Y) <= 5000 && Math.Abs(v.Z) <= 5000;
    internal static bool Valid(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z)
        && float.IsFinite(q.W) && q.LengthSquared() is >= .25f and <= 4;
}
