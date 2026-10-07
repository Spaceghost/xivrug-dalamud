using System.Numerics;

namespace XivSurface.Core;

/// <summary>A bounded, opt-in rendering diagnostic. Produces a synthetic rug
/// follow target only; never a character position, foot pose or map coordinate.
/// Stops if the actual player moves, the scene changes or the clock misbehaves.</summary>
public sealed class RugMotionProbe
{
    public const double Duration = 15;
    public const float MaximumOffset = 1.2f;
    public const float PlayerMovementTolerance = .05f;
    private uint zone;
    private Vector3 player;
    private double started, previous;
    private float amplitude;
    public bool Active { get; private set; }

    public bool Start(uint currentZone, Vector3 actualPlayer, double now, float shortestRugHalf)
    {
        Stop();
        if (currentZone == 0 || !MathEx.Finite(actualPlayer) || !double.IsFinite(now) || now < 0
            || !float.IsFinite(shortestRugHalf) || shortestRugHalf < 1) return false;
        zone = currentZone; player = actualPlayer; started = previous = now;
        amplitude = Math.Min(MaximumOffset, shortestRugHalf - .55f);
        Active = true; return true;
    }

    public void Stop() => Active = false;

    public bool TryTarget(uint currentZone, Vector3 actualPlayer, double now, bool sceneAllowed, out Vector2 target)
    {
        target = default;
        if (!Active) return false;
        var age = now - started;
        if (!sceneAllowed || currentZone != zone || !MathEx.Finite(actualPlayer) || !double.IsFinite(now)
            || now < previous || now - previous > RugFollowController.MaximumFrameGap
            || age < 0 || age >= Duration || Vector3.DistanceSquared(player, actualPlayer) > PlayerMovementTolerance * PlayerMovementTolerance)
        { Stop(); return false; }
        previous = now;
        // One-second cosine fades avoid a target-position or velocity step at
        // either end. At full amplitude the diagonal target peaks at4.8y/s;
        // this exercises real sampling, not a claim of walking acceptance.
        var fade = Math.Clamp(Math.Min(age, Duration - age), 0, 1);
        var envelope = .5 - .5 * Math.Cos(Math.PI * fade);
        var displacement = amplitude * envelope * Math.Sin(age * 4);
        target = new Vector2(player.X, player.Z) + Vector2.Normalize(Vector2.One) * (float)displacement;
        return MathEx.Finite(target);
    }
}
