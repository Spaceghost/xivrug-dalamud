using System.Numerics;

namespace XivSurface.Core;

/// <summary>
/// A tiny circular pull wakes the carpet, then a critically damped glide matches
/// the traveller's velocity and settles underneath them. Unlike a positional
/// rubber band it does not keep stretching farther behind a steady runner.
/// Only the visual rug moves; no player or game input is touched.
/// </summary>
public sealed class RugFollowController
{
    public const float TeleportDistance = 30;
    public const float MaximumRadius = 8;
    public const float MinimumResponseSeconds = 0.05f;
    public const float MaximumResponseSeconds = 2;
    public const float MaximumFlourishOffset = 0.09f;
    public const float MaximumTrackingSpeed = 40;
    public const double MaximumFrameGap = 1;

    private double centerX, centerZ, velocityX, velocityZ, previousTime;
    private double heading, activity, phase;
    private Vector2 previousPlayer, flourish;
    private bool pulling;
    public bool HasCenter { get; private set; }
    public Vector2 Anchor => new((float)centerX, (float)centerZ);
    public Vector2 Center => Anchor + flourish;
    public Vector2 Velocity => new((float)velocityX, (float)velocityZ);
    /// <summary>Floor heading XY, movement activity Z, distance phase W (0..2π).</summary>
    public Vector4 Travel { get; private set; }

    public void Reset()
    {
        HasCenter = pulling = false;
        centerX = centerZ = velocityX = velocityZ = previousTime = 0;
        heading = activity = phase = 0;
        previousPlayer = flourish = Vector2.Zero;
        Travel = Vector4.Zero;
    }

    private void Reanchor(Vector2 player, double timeSeconds)
    {
        Reset();
        centerX = player.X;
        centerZ = player.Y;
        previousTime = timeSeconds;
        previousPlayer = player;
        HasCenter = true;
    }

    /// <summary>
    /// Radius is the wake-up circle, not the trailing distance. Response is
    /// approximately the 95% settle time for a fixed target. Motion follows an
    /// analytic, non-oscillating damped curve and continuous linear player
    /// motion, so normal tracking does not depend on the rendering frame rate.
    /// Teleports, invalid input, clock resets and long frame gaps discard motion.
    /// Disabling flourishes keeps the glide but suppresses sway and shader motion.
    /// </summary>
    public bool Step(Vector2 player, double timeSeconds, float radius, float responseSeconds, bool animate = true)
    {
        if (!MathEx.Finite(player) || !double.IsFinite(timeSeconds) || timeSeconds < 0
            || !float.IsFinite(radius) || radius < 0 || radius > MaximumRadius
            || !float.IsFinite(responseSeconds) || responseSeconds < MinimumResponseSeconds
            || responseSeconds > MaximumResponseSeconds)
        {
            Reset();
            return false;
        }

        var elapsed = timeSeconds - previousTime;
        var dx = (double)player.X - previousPlayer.X;
        var dz = (double)player.Y - previousPlayer.Y;
        var moved = Math.Sqrt(dx * dx + dz * dz);
        if (!HasCenter || elapsed < 0 || elapsed > MaximumFrameGap || moved > TeleportDistance
            || (elapsed > 0 && moved / elapsed > MaximumTrackingSpeed))
        {
            Reanchor(player, timeSeconds);
            return true;
        }
        if (elapsed == 0)
        {
            if (!animate)
            {
                flourish = Vector2.Zero;
                Travel = Travel with { Z = 0 };
            }
            return true;
        }

        var targetStartX = (double)previousPlayer.X;
        var targetStartZ = (double)previousPlayer.Y;
        var targetVelocityX = dx / elapsed;
        var targetVelocityZ = dz / elapsed;
        previousTime = timeSeconds;
        previousPlayer = player;
        var activeTime = elapsed;
        var distanceX = (double)player.X - centerX;
        var distanceZ = (double)player.Y - centerZ;
        if (!pulling)
        {
            if (distanceX * distanceX + distanceZ * distanceZ <= radius * radius)
                activeTime = 0;
            else
            {
                pulling = true;
                // Start exactly at the circle crossing, not one whole frame
                // early. This keeps the tiny trigger consistent at 30/60/144 Hz.
                var sx = targetStartX - centerX;
                var sz = targetStartZ - centerZ;
                var a = dx * dx + dz * dz;
                var b = 2 * (sx * dx + sz * dz);
                var c = sx * sx + sz * sz - radius * radius;
                if (a > 0 && c <= 0)
                {
                    var fraction = Math.Clamp((-b + Math.Sqrt(Math.Max(0, b * b - 4 * a * c))) / (2 * a), 0, 1);
                    targetStartX += dx * fraction;
                    targetStartZ += dz * fraction;
                    activeTime *= 1 - fraction;
                }
            }
        }

        var oldX = centerX;
        var oldZ = centerZ;
        if (pulling && activeTime > 0)
        {
            var omega = 4.75 / responseSeconds;
            var decay = Math.Exp(-omega * activeTime);
            var errorX = centerX - targetStartX;
            var errorZ = centerZ - targetStartZ;
            var relativeVelocityX = velocityX - targetVelocityX;
            var relativeVelocityZ = velocityZ - targetVelocityZ;
            var stepX = (relativeVelocityX + omega * errorX) * activeTime;
            var stepZ = (relativeVelocityZ + omega * errorZ) * activeTime;
            centerX = player.X + (errorX + stepX) * decay;
            centerZ = player.Y + (errorZ + stepZ) * decay;
            velocityX = targetVelocityX + (relativeVelocityX - omega * stepX) * decay;
            velocityZ = targetVelocityZ + (relativeVelocityZ - omega * stepZ) * decay;

            // A stopped traveller should not get a spring rebound. Finish the
            // glide underfoot when it reaches them; the border flourish then fades.
            var stopped = moved / elapsed < 0.025;
            var remainingX = (double)player.X - centerX;
            var remainingZ = (double)player.Y - centerZ;
            if (stopped && (distanceX * remainingX + distanceZ * remainingZ <= 0
                || (remainingX * remainingX + remainingZ * remainingZ < 0.0001
                    && velocityX * velocityX + velocityZ * velocityZ < 0.01)))
            {
                centerX = player.X;
                centerZ = player.Y;
                velocityX = velocityZ = 0;
                pulling = false;
            }
        }

        var travelled = Math.Sqrt((centerX - oldX) * (centerX - oldX) + (centerZ - oldZ) * (centerZ - oldZ));
        var speed = Math.Sqrt(velocityX * velocityX + velocityZ * velocityZ);
        if (speed > 0.05)
        {
            var wantedHeading = Math.Atan2(velocityZ, velocityX);
            var turn = Math.Atan2(Math.Sin(wantedHeading - heading), Math.Cos(wantedHeading - heading));
            heading += turn * (1 - Math.Exp(-elapsed / 0.18));
        }
        phase = (phase + travelled * 1.6) % Math.Tau;
        var wantedActivity = Math.Clamp(speed / 4, 0, 1);
        activity += (wantedActivity - activity) * (1 - Math.Exp(-elapsed / (wantedActivity > activity ? 0.16 : 0.32)));
        if (activity < 0.0001) activity = 0;
        var strength = animate ? activity : 0;
        var sway = MaximumFlourishOffset * strength * Math.Sin(phase);
        flourish = new((float)(-Math.Sin(heading) * sway), (float)(Math.Cos(heading) * sway));
        Travel = new((float)Math.Cos(heading), (float)Math.Sin(heading), (float)strength, (float)phase);
        return true;
    }
}
