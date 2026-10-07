namespace XivSurface.Core;

/// <summary>Bounded phase shared by the2rad/s hem/route and1.7rad/s fringe.
/// Reduce the monotonic double before converting to GPU float:256 seconds is
/// not either oscillator's period and produces a visible discontinuity.</summary>
public static class ClothRenderClock
{
    public const double CommonPeriodSeconds=20*Math.PI;
    public static float Phase(double seconds)=>double.IsFinite(seconds) && seconds>=0
        ? (float)(seconds%CommonPeriodSeconds):0;
}
