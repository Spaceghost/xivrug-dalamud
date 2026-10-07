using System.Numerics;

namespace XivSurface.Core;

public sealed partial class LocalFloorLayer
{
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
