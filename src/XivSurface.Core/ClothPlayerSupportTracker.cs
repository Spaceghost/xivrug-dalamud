using System.Numerics;

namespace XivSurface.Core;

public enum ClothPlayerSupportQuery { Unknown, NearFeet, RetainedLayer }

/// <summary>
/// Keeps a bounded takeoff reference while each airborne update obtains NEW
/// collision evidence from the previously verified local floor layer. It never
/// returns a cached floor height as current support. The caller supplies the
/// game's actual airborne state and performs the selected collision query.
/// </summary>
public sealed class ClothPlayerSupportTracker
{
    public const double MaximumAirborneSeconds = 3;
    public const double MaximumSampleGapSeconds = .35;
    public const float MaximumHorizontalTravel = 16;
    public const float GroundedGap = .12f;
    public const float MaximumFeetAboveGround = CarpetLiftController.MaximumLift + CarpetLiftController.FootGap;
    private uint zone;
    private LayerFloorHit grounded;
    private double groundedAt = double.NaN, sampleAt = double.NaN, airborneAt = double.NaN;
    private double lastPlanAt = double.NaN;
    private Vector3 requestedPlayer;
    private double requestedAt;
    private ClothFloorProbe requestedProbe;
    private ClothPlayerSupportQuery requested;

    public void Reset()
    {
        zone = 0; grounded = default;
        groundedAt = sampleAt = airborneAt = lastPlanAt = double.NaN;
        requested = ClothPlayerSupportQuery.Unknown;
    }

    /// <summary>
    /// NearFeet: run the returned ordinary player probe. RetainedLayer: call
    /// LayerFloorDiscovery.Query at the CURRENT player's XZ, before BeginFrame,
    /// using its retained measured ground geometry. Do not run a broad ray from
    /// airborne feet, substitute grounded.Y, or re-seed the layer. Unknown means
    /// no query is authorized by this tracker. Confirm every successful result.
    /// </summary>
    public ClothPlayerSupportQuery Plan(uint territory, Vector3 player, double now, bool airborne,
        out ClothFloorProbe nearFeetProbe)
    {
        nearFeetProbe = default; requested = ClothPlayerSupportQuery.Unknown;
        if (territory == 0 || !double.IsFinite(now) || now < 0
            || !ClothFloorQueryPolicy.TryPlayerProbe(player, out var ordinary))
        { Reset(); return requested; }
        if (territory != zone || double.IsFinite(lastPlanAt) && (now < lastPlanAt || now - lastPlanAt > MaximumSampleGapSeconds))
            Reset();
        zone = territory; lastPlanAt = requestedAt = now; requestedPlayer = player;
        if (!airborne)
        {
            requestedProbe = nearFeetProbe = ordinary;
            return requested = ClothPlayerSupportQuery.NearFeet;
        }
        if (!grounded.Valid || !double.IsFinite(sampleAt) || now < sampleAt
            || now - sampleAt > MaximumSampleGapSeconds
            || !double.IsFinite(airborneAt) && now - groundedAt > MaximumSampleGapSeconds)
        { Reset(); return requested; }
        if (!double.IsFinite(airborneAt)) airborneAt = now;
        var height = player.Y - grounded.Position.Y;
        if (now - airborneAt > MaximumAirborneSeconds || Math.Abs(height) > MaximumFeetAboveGround
            || Vector2.DistanceSquared(new(player.X, player.Z), new(grounded.Position.X, grounded.Position.Z))
                > MaximumHorizontalTravel * MaximumHorizontalTravel)
        { Reset(); return requested; }
        return requested = ClothPlayerSupportQuery.RetainedLayer;
    }

    /// <summary>
    /// Accept only an actual result of the query selected by the most recent
    /// Plan. Airborne hits must already belong to the locally reached layer;
    /// a graph-connected deck at another height is insufficient. Returning
    /// false does not permit rendering any previously accepted floor instead.
    /// </summary>
    public bool Confirm(LayerFloorHit hit, LocalFloorLayer? layer = null)
    {
        var kind = requested; requested = ClothPlayerSupportQuery.Unknown;
        if (kind == ClothPlayerSupportQuery.Unknown || !hit.Valid
            || Vector2.DistanceSquared(new(hit.Position.X, hit.Position.Z), new(requestedPlayer.X, requestedPlayer.Z)) > .02f * .02f)
            return false;
        var gap = requestedPlayer.Y - hit.Position.Y;
        if (gap < -.06f || gap > MaximumFeetAboveGround) return false;
        if (kind == ClothPlayerSupportQuery.NearFeet)
        {
            if (!ClothFloorQueryPolicy.Accept(requestedProbe, hit.Position, hit.Triangle.Normal, out _)) return false;
            if (gap <= GroundedGap)
            { grounded = hit; groundedAt = requestedAt; airborneAt = double.NaN; }
        }
        else if (layer is null || !layer.Contains(hit.Triangle)
            || !layer.TryNearest(new(hit.Position.X, hit.Position.Z), out var witness, out _)
            || !witness.Contains(hit.Position)) return false;
        sampleAt = requestedAt;
        return true;
    }
}
