using System.Numerics;

namespace XivSurface.Core;

/// <summary>
/// Smooths publication of whole collision-contact meshes without extrapolating
/// beyond measured endpoints. Topology, UVs and every interior face are retained.
/// Scene-depth rejection remains authoritative between contact updates.
/// </summary>
public sealed class ClothPresentation
{
    public const double DefaultResponseSeconds = .06;
    public const double MinimumResponseSeconds = .04;
    public const double MaximumResponseSeconds = .12;
    private const float SettleDistanceSquared = .0001f * .0001f;
    private ClothMesh? target, shown, intervalStart;
    private Vector3[] velocities = [];
    private Vector2 targetCenter, startCenter;
    private double previous, published, response = DefaultResponseSeconds;
    public Vector2 Center { get; private set; }
    public Vector2 Velocity { get; private set; }

    public void Reset()
    {
        target = shown = intervalStart = null; velocities = []; previous = published = 0;
        response = DefaultResponseSeconds; Center = Velocity = default;
    }

    public ClothMesh Update(ClothMesh measured, Vector2 measuredCenter, double time)
    {
        ArgumentNullException.ThrowIfNull(measured);
        if (!MathEx.Finite(measuredCenter) || !double.IsFinite(time) || time < 0)
            throw new ArgumentOutOfRangeException(nameof(time));
        if (target is null || shown is null || time < previous || time - previous > 1
            || measured.Size != target.Size || measured.Positions.Length != target.Positions.Length
            || Vector2.Distance(targetCenter, measuredCenter) > 4)
        {
            target = shown = intervalStart = measured; targetCenter = startCenter = Center = measuredCenter;
            velocities = new Vector3[measured.Positions.Length]; Velocity = default;
            published = previous = time; response = DefaultResponseSeconds; return measured;
        }
        var elapsed = time - previous;
        previous = time;
        if (!ReferenceEquals(measured, target))
        {
            // Keep physical velocity across ordinary publications. Restarting
            // a first-order approach to every frozen contact batch creates a
            // visible speed pulse, even when none of its frames fully stop.
            response = Math.Clamp((time - published) * .5, MinimumResponseSeconds, MaximumResponseSeconds);
            published = time; target = measured; targetCenter = measuredCenter;
            intervalStart = shown; startCenter = Center;
            Velocity = Compatible(Center, targetCenter, Velocity);
            for (var i = 0; i < velocities.Length; i++)
                velocities[i] = Compatible(shown.Positions[i], target.Positions[i], velocities[i]);
            return shown; // publication itself cannot move the displayed cloth
        }
        if (ReferenceEquals(shown, target) || elapsed <= 0) return shown;
        // Exact critically damped solution for a fixed measured target. With
        // omega*T ~= 2 the usual steady batch ripple is about 1.6x, rather than
        // the old exponential filter's 7.4x. This trades at most .12s additional
        // mean filter lag for a continuous velocity; batch latency remains the
        // sampler's responsibility. Never predict new collision geometry.
        var omega = 1 / response;
        var decay = Math.Exp(-omega * elapsed);
        var weight = (float)(1 - decay);
        var remaining = Vector2.DistanceSquared(Center, targetCenter);
        var center = new Vector2(
            Step(Center.X, Velocity.X, startCenter.X, targetCenter.X, out var vx),
            Step(Center.Y, Velocity.Y, startCenter.Y, targetCenter.Y, out var vz));
        var velocity = new Vector2(vx, vz);
        var moving = velocity.LengthSquared();
        var positions = new Vector3[target.Positions.Length];
        var normals = new Vector3[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            remaining = Math.Max(remaining, Vector3.DistanceSquared(shown.Positions[i], target.Positions[i]));
            var at = shown.Positions[i]; var goal = target.Positions[i]; var start = intervalStart!.Positions[i];
            positions[i] = new(
                Step(at.X, velocities[i].X, start.X, goal.X, out var x),
                Step(at.Y, velocities[i].Y, start.Y, goal.Y, out var y),
                Step(at.Z, velocities[i].Z, start.Z, goal.Z, out var z));
            velocities[i] = new(x, y, z);
            moving = Math.Max(moving, velocities[i].LengthSquared());
            var normal = Vector3.Lerp(shown.Normals[i], target.Normals[i], weight);
            normals[i] = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
        }
        // Stop allocations/uploads only after sub-millimetre convergence, not
        // at the publication cadence. This snap is below visible cloth detail.
        if (remaining <= SettleDistanceSquared && moving <= .0001f)
        { shown = target; Center = targetCenter; Velocity = default; Array.Clear(velocities); return target; }
        Center = center; Velocity = velocity;
        shown = new(positions, normals, target.UV, target.Indices, target.Size);
        return shown;

        float Step(float at, float speed, float start, float goal, out float nextSpeed)
        {
            var error = (double)at - goal;
            var coefficient = speed + omega * error;
            var next = goal + (error + coefficient * elapsed) * decay;
            var nextVelocity = (speed - omega * coefficient * elapsed) * decay;
            var lower = Math.Min(start, goal); var upper = Math.Max(start, goal);
            // A stop/reversal or newly constrained corner may invalidate the
            // old tangent. Endpoint safety wins over C1 at those boundaries:
            // cancel outward motion rather than overshoot, rebound or invent
            // a ground point beyond the measured per-coordinate interval.
            if (next <= lower) { next = lower; nextVelocity = Math.Max(0, nextVelocity); }
            if (next >= upper) { next = upper; nextVelocity = Math.Min(0, nextVelocity); }
            nextSpeed = (float)nextVelocity;
            return (float)next;
        }
    }

    private static float Compatible(float at, float goal, float velocity) =>
        (double)(goal - at) * velocity > 0 ? velocity : 0;
    private static Vector2 Compatible(Vector2 at, Vector2 goal, Vector2 velocity) =>
        new(Compatible(at.X, goal.X, velocity.X), Compatible(at.Y, goal.Y, velocity.Y));
    private static Vector3 Compatible(Vector3 at, Vector3 goal, Vector3 velocity) =>
        new(Compatible(at.X, goal.X, velocity.X), Compatible(at.Y, goal.Y, velocity.Y), Compatible(at.Z, goal.Z, velocity.Z));
}
