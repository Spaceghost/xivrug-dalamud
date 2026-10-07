namespace XivSurface.Core.Tests;

public sealed class CarpetLiftControllerTests
{
    [Fact]
    public void StandingStillCannotMakeTheCarpetLiftItself()
    {
        var lift = new CarpetLiftController();
        for (var frame = 0; frame < 1200; frame++)
            Assert.Equal(0, lift.Update(7.02f, 7, frame / 60d, true));
    }

    [Fact]
    public void AJumpRisesSmoothlyAndCatchesDescendingFeet()
    {
        var lift = new CarpetLiftController();
        var previousFeet = 0f;
        var previousLift = 0f;
        for (var frame = 0; frame <= 48; frame++)
        {
            var now = frame / 60d;
            var feet = MathF.Max(0, 1.5f * MathF.Sin((float)(Math.PI * now / 0.8)));
            var value = lift.Update(feet, 0, now, true);
            var target = MathF.Max(0, feet - CarpetLiftController.FootGap);
            Assert.InRange(value, 0, MathF.Max(0, feet - ClothSurface.Clearance) + 1e-6f);
            if (feet < previousFeet - 0.0001f) Assert.True(value >= target - 1e-6f);
            Assert.True(MathF.Abs(value - previousLift) < 0.2f);
            if (frame == 5) Assert.True(value < target); // The ascent eases upward instead of snapping to the feet.
            previousFeet = feet;
            previousLift = value;
        }
        Assert.Equal(0, lift.Lift);
    }

    [Fact]
    public void AQuickDescendingCatchDoesNotKeepAnEarlierAscentLag()
    {
        var lift = new CarpetLiftController();
        lift.Update(0, 0, 0, true);
        var rising = lift.Update(2, 0, 1d / 60, true);
        Assert.InRange(rising, 0.01f, 1f);
        var caught = lift.Update(1.9f, 0, 2d / 60, true);
        Assert.InRange(caught, 1.9f - CarpetLiftController.FootGap, 1.9f - ClothSurface.Clearance);
    }

    [Fact]
    public void RiseResponseUsesElapsedTimeRatherThanFrameCount()
    {
        var a = new CarpetLiftController();
        var b = new CarpetLiftController();
        a.Update(0, 0, 0, true); b.Update(0, 0, 0, true);
        for (var i = 1; i <= 6; i++) a.Update(1.5f, 0, i / 60d, true);
        b.Update(1.5f, 0, 0.1, true);
        Assert.InRange(MathF.Abs(a.Lift - b.Lift), 0, 1e-5f);
    }

    [Fact]
    public void DisablingLiftSettlesItWithoutAnAbruptDrop()
    {
        var lift = new CarpetLiftController();
        lift.Update(2, 0, 0, true);
        var previous = lift.Lift;
        for (var frame = 1; frame <= 180; frame++)
        {
            var value = lift.Update(2, 0, frame / 60d, false);
            Assert.InRange(value, 0, previous);
            if (frame == 1) Assert.True(value > 1);
            previous = value;
        }
        Assert.Equal(0, lift.Lift);
    }

    [Fact]
    public void InvalidReadingsNeverBecomeAGuessedGroundPlaneOrNonFiniteOffset()
    {
        var lift = new CarpetLiftController();
        var valid = lift.Update(2, 0, 0, true);
        Assert.Equal(valid, lift.Update(float.NaN, 0, 1, true));
        Assert.Equal(valid, lift.Update(2, float.PositiveInfinity, 1, true));
        Assert.Equal(valid, lift.Update(2, 0, double.NaN, true));
        Assert.Equal(CarpetLiftController.MaximumLift, lift.Update(float.MaxValue, -float.MaxValue, -1, true));
        lift.Reset();
        Assert.Equal(0, lift.Lift);
        Assert.Equal(0, lift.Update(200, 200, 1, true));
    }
}
