using System.Numerics;

namespace XivSurface.Core;

/// <summary>Local monotonic counts across a single discovery call only, NOT a
/// persistent geometry identity. Discovery only appends graph entries; frame
/// pruning/re-rooting must not occur inside that framework-owned call.</summary>
public readonly record struct ClothDiscoveryStamp(int Faces, int Risers, int Portals);

public sealed partial class LocalFloorLayer
{
    public ClothDiscoveryStamp DiscoveryStamp => new(connected.Count, curbFaces.Count, portalCount);
}

/// <summary>One pure-query/discovery retry loop, never a cross-frame cache or
/// floor proof. If a successful actual query leaves the exact same uncovered
/// witness, immediately issuing that same query again cannot prove coverage.
/// Retain Unknown and let the normal later-update policy retry the scene.</summary>
public struct ClothWitnessProgress
{
    private Vector2? lastSuccessful;
    private ClothDiscoveryStamp unchanged;
    public bool CanDiscover(Vector2 witness, ClothDiscoveryStamp current)
        => MathEx.Finite(witness) && (witness != lastSuccessful || current != unchanged);
    public void Record(Vector2 witness, LayerQueryResult result, ClothDiscoveryStamp before, ClothDiscoveryStamp after)
    {
        lastSuccessful = result == LayerQueryResult.Success && MathEx.Finite(witness) && before == after ? witness : null;
        unchanged = after;
    }
}
