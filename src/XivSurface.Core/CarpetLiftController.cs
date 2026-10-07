namespace XivSurface.Core;

/// <summary>
/// A visual carpet follows a jump without moving the character. It eases upward, catches descending feet,
/// and settles onto confirmed ground. The caller supplies current measured feet and ground heights.
/// </summary>
public sealed class CarpetLiftController
{
    public const float FootGap = 0.06f;
    public const float MaximumLift = 8f;
    private const float RiseSeconds = 0.07f;
    private const float FallSeconds = 0.14f;
    private bool initialized;
    private double lastTime;
    private float previousFeet;

    public float Lift { get; private set; }

    /// <summary>Invalid measurements retain the last finite offset; they never stand in for a confirmed floor.</summary>
    public float Update(float playerFeetY, float confirmedGroundY, double now, bool enabled)
    {
        if (!float.IsFinite(playerFeetY) || !float.IsFinite(confirmedGroundY) || !double.IsFinite(now)) return Lift;
        var desired = enabled ? HeightDifference(playerFeetY, confirmedGroundY, FootGap) : 0;
        if (!initialized || now < lastTime)
        {
            initialized = true;
            lastTime = now;
            previousFeet = playerFeetY;
            // When enabled while already in the air, create the carpet beneath the feet rather than on a remote floor.
            Lift = desired;
            return Lift;
        }

        var elapsed = (float)Math.Clamp(now - lastTime, 0, 0.1);
        var descending = playerFeetY < previousFeet - 0.0001f;
        lastTime = now;
        previousFeet = playerFeetY;
        var response = desired > Lift ? RiseSeconds : FallSeconds;
        var eased = Lift + (desired - Lift) * (1 - MathF.Exp(-elapsed / response));
        if (enabled)
        {
            if (descending) eased = MathF.Max(eased, desired);
            // The mesh already includes contact clearance. Never let easing carry its top through the feet.
            var belowFeet = HeightDifference(playerFeetY, confirmedGroundY, ClothSurface.Clearance);
            eased = MathF.Min(eased, belowFeet);
        }
        Lift = Math.Clamp(eased, 0, MaximumLift);
        if (Lift < 0.00001f) Lift = 0;
        return Lift;
    }

    public void Reset()
    {
        Lift = 0;
        initialized = false;
        lastTime = 0;
        previousFeet = 0;
    }

    private static float HeightDifference(float feet, float ground, float clearance) =>
        (float)Math.Clamp((double)feet - ground - clearance, 0, MaximumLift);
}
