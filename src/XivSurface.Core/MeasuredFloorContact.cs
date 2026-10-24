using System.Numerics;

namespace XivSurface.Core;

public sealed partial class LocalFloorLayer
{
    /// <summary>A real clearance hit can belong to a raised object whose
    /// sloped face overlaps the current floor. Slope alone does not make it a
    /// continuation of that floor. This classifies a blocker only: callers
    /// must stop before the actual hit and query the compressed contact and
    /// complete cell again. No hit, height or edge is added to the floor graph.</summary>
    public bool IsRaisedObstacle(Vector3 from, Vector3 to, LayerFloorHit hit,
        Func<bool>? withinDeadline = null)
    {
        if (withinDeadline?.Invoke() == false || !MathEx.Finite(from) || !MathEx.Finite(to)
            || !hit.Valid || Contains(hit.Triangle)) return false;
        var delta = to - from; var lengthSquared = delta.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-10f) return false;
        var fraction = Vector3.Dot(hit.Position - from, delta) / lengthSquared;
        if (!float.IsFinite(fraction) || fraction <= 0 || fraction > 1
            || Vector3.DistanceSquared(hit.Position, from + delta * fraction) > .002f * .002f) return false;
        var at = new Vector2(hit.Position.X, hit.Position.Z);
        return TryNearest(at, out var floor, out _, withinDeadline)
            && TryMeasuredHeight(floor, at, out var height)
            && SurfaceContains(floor, new(at.X, height, at.Y))
            && hit.Position.Y - height > SeamTolerance
            && withinDeadline?.Invoke() != false;
    }

    /// <summary>Retain an actual, current native collision hit only through
    /// the ordinary local-floor acceptance rules. The caller owns observation
    /// freshness, scene identity, deadline and attempt limits. This does not
    /// re-root the layer, infer a riser, or authorize a clearance segment: even
    /// after success the complete floor corridor and native wall checks remain
    /// necessary. A plane guides the acceptance band, never replaces the hit.</summary>
    public bool TryRetainMeasuredFloorHit(LayerFloorHit hit)
    {
        if (Count == 0 || !hit.Triangle.Walkable || !hit.Valid) return false;
        return TryProbe(new Vector2(hit.Position.X, hit.Position.Z), out var probe)
            && Accept(probe, hit);
    }
}
