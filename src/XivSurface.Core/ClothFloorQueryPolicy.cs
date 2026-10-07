using System.Numerics;

namespace XivSurface.Core;

public readonly record struct ClothFloorProbe(Vector2 Position, float StartY, float Length,
    float MinimumY, float MaximumY)
{
    public bool Valid => MathEx.Finite(Position) && float.IsFinite(StartY) && float.IsFinite(Length)
        && float.IsFinite(MinimumY) && float.IsFinite(MaximumY) && Length > 0
        && MinimumY <= MaximumY && MaximumY <= StartY && MinimumY >= StartY - Length - .0001f;
}

/// <summary>Conservative collision-only layer guard, not a universal layer ID.
/// Begin at the actual actor's ground/root reference instead of above its head;
/// then constrain the rug to a local band around that accepted player support.
/// Missing/too-airborne support is unknown and must hide, not select a deck above.
/// Raw triangles without an oriented supplied normal remain a game-query limit.
/// </summary>
public static class ClothFloorQueryPolicy
{
    public const float LayerRefreshTopMargin = .05f;

    /// <summary>Refresh before a fixed-origin downward ray starts beneath the
    /// freshly measured player floor. This check must run even without a
    /// completed cache snapshot. The caller retains its separate downward and
    /// large-height-change invalidation rules.</summary>
    public static bool RequiresLayerRefresh(float playerSupportY, float queryStartY) =>
        !float.IsFinite(playerSupportY) || !float.IsFinite(queryStartY)
        || playerSupportY >= queryStartY - LayerRefreshTopMargin;

    public static bool TryPlayerProbe(Vector3 player, out ClothFloorProbe probe)
    {
        probe = default;
        if (!MathEx.Finite(player) || Math.Max(Math.Max(Math.Abs(player.X), Math.Abs(player.Y)), Math.Abs(player.Z)) > 1_000_000) return false;
        probe = new(new(player.X, player.Z), player.Y + .12f, 1.12f, player.Y - 1, player.Y + .06f);
        return probe.Valid;
    }

    public static bool TryLocalProbe(Vector2 position, float playerSupportY, out ClothFloorProbe probe)
    {
        probe = default;
        if (!MathEx.Finite(position) || !float.IsFinite(playerSupportY) || Math.Abs(playerSupportY) > 1_000_000) return false;
        probe = new(position, playerSupportY + .35f, 1.35f, playerSupportY - 1, playerSupportY + .35f);
        return probe.Valid;
    }

    public static bool Accept(ClothFloorProbe probe, Vector3 hit, Vector3 normal, out float height)
    {
        height = 0;
        if (!probe.Valid || !MathEx.Finite(hit) || !MathEx.Finite(normal)
            || Vector2.DistanceSquared(probe.Position, new(hit.X, hit.Z)) > .02f * .02f
            || hit.Y < probe.MinimumY || hit.Y > probe.MaximumY) return false;
        var length = normal.Length();
        if (!float.IsFinite(length) || length < 1e-6f || normal.Y / length < .5f) return false;
        height = hit.Y;
        return true;
    }

    /// <summary>Raw candidate policy for the explicit cloth discovery adapter.
    /// Keeps the same native ray band and point tolerance; permits finite
    /// upward steep geometry without calling it walkable or admitting it to a
    /// layer. The actual shared-portal, footprint and clearance proofs follow.
    /// Never use this policy for the player seed or near-feet floor query.</summary>
    public static bool AcceptMeasuredCloth(ClothFloorProbe probe, LayerFloorHit candidate, out float height)
    {
        height = 0;
        var hit = candidate.Position;
        if (!probe.Valid || !MathEx.Finite(hit)
            || Vector2.DistanceSquared(probe.Position, new(hit.X, hit.Z)) > .02f * .02f
            || hit.Y < probe.MinimumY || hit.Y > probe.MaximumY) return false;
        var connector = !candidate.Triangle.Walkable;
        if (!ClothConnectorGeometry.Contains(candidate.Triangle, hit, connector)) return false;
        height = hit.Y;
        return true;
    }
}
