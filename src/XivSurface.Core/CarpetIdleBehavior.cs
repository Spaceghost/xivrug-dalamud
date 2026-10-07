using System.Numerics;

namespace XivSurface.Core;

public enum CarpetIdleState { Waiting, Footwork, Resting, Riding }

/// <summary>Only schedules visual poses. It never generates character movement or rug pressure.</summary>
public sealed class CarpetIdleBehavior
{
    public const double IdleDelay = 15;
    private Vector3 anchor;
    private float anchorYaw;
    private double idleSince = double.NaN;
    private double lastTime = double.NaN;
    public CarpetIdleState State { get; private set; }
    public double IdleSeconds { get; private set; }

    public CarpetIdleState Update(bool idleActions, bool rideVisual, bool eligible,
        Vector3 position, float yaw, double now, double footworkDuration = 5.6)
    {
        if (!eligible || !double.IsFinite(now) || !MathEx.Finite(position) || !float.IsFinite(yaw))
        {
            Reset(); return State;
        }
        if (double.IsNaN(lastTime) || now < lastTime || now - lastTime > 2)
        {
            anchor = position; anchorYaw = yaw; idleSince = now;
        }
        lastTime = now;
        var turning = MathF.Abs(MathF.IEEERemainder(yaw - anchorYaw, MathF.Tau)) > .025f;
        var moving = Vector3.DistanceSquared(position, anchor) > .000225f;
        if (moving || turning || !idleActions)
        {
            anchor = position; anchorYaw = yaw; idleSince = now;
        }
        IdleSeconds = Math.Max(0, now - idleSince);
        State = idleActions && IdleSeconds >= IdleDelay
            ? IdleSeconds < IdleDelay + Math.Clamp(footworkDuration, 1, 30)
                ? CarpetIdleState.Footwork : CarpetIdleState.Resting
            : rideVisual ? CarpetIdleState.Riding : CarpetIdleState.Waiting;
        return State;
    }

    public void Reset()
    {
        lastTime = idleSince = double.NaN;
        State = CarpetIdleState.Waiting; IdleSeconds = 0;
    }
}
