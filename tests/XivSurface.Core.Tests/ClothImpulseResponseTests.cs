using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class ClothImpulseResponseTests
{
    // Measured synthetic PCM through actual AudioFeatureTracker->JSON->
    // PianoClothResponse at playback volume.6 and default rug strength.55.
    // These are event amplitudes, not fabricated production beat timers.
    [Theory]
    [InlineData(.789137f, .146267f, .016f, .022f)]
    [InlineData(.789137f, .481752f, .016f, .022f)]
    [InlineData(.789137f, .845808f, .016f, .022f)]
    [InlineData(.479460f, .146267f, .009f, .014f)]
    [InlineData(.151869f, .146267f, .0028f, .0045f)]
    [InlineData(.041216f, .146267f, .0007f, .0013f)]
    public void ActualPcmDerivedAttacksProduceBoundedVisibleSpringSwellsAndFade(float strength, float tone,
        float minimumPeak, float maximumPeak)
    {
        var measured = Measure([new(Vector2.Zero, .2, strength, tone)]);
        Assert.InRange(measured.Peak, minimumPeak, maximumPeak);
        Assert.True(measured.FarTime > measured.NearTime + .3); // outward travel, not whole-rug flashing
        Assert.True(measured.FarPeak > measured.Peak * .55f);
        Assert.True(measured.AfterFade < .0002f);
    }

    [Theory] [InlineData(0f)] [InlineData(.5f)] [InlineData(1f)]
    public void RepresentativePoint55AttackSurvivesPhysicalSpringAcrossBandBalance(float tone)
    {
        var measured = Measure([new(Vector2.Zero, .2, .55f, tone)]);
        // Old high-frequency forcing only moved the actual cloth1--3mm.
        // This assertion measures solved vertices, not an unsimulated target.
        Assert.InRange(measured.Peak, .010f, .015f);
        Assert.True(measured.AfterFade < .0002f);
    }

    [Fact]
    public void FullSixteenEventBurstKeepsFortyMillimetreCapFloorAndContactConstraints()
    {
        var events = Enumerable.Repeat(new ClothImpulse(Vector2.Zero, .2, 1, .5f), ClothImpulse.MaximumEvents).ToArray();
        var measured = Measure(events);
        Assert.InRange(measured.Peak, .025f, ClothImpulse.MaximumDisplacement);
        Assert.True(measured.AfterFade < .0002f);
    }

    [Fact]
    public void SmoothAttackAndLifetimeReleaseDoNotCreateDiscontinuityOrInventEvents()
    {
        ClothImpulse[] impulse = [new(Vector2.Zero, 1, 1, .5f)];
        Assert.Equal(0, ClothImpulse.Displacement(Vector2.Zero, 1, impulse));
        Assert.InRange(Math.Abs(ClothImpulse.Displacement(Vector2.Zero, 1.00001, impulse)), 0, .00000001f);
        var end = 1 + ClothImpulse.LifetimeSeconds;
        var front = new Vector2((float)(ClothImpulse.LifetimeSeconds * .95), 0);
        Assert.InRange(Math.Abs(ClothImpulse.Displacement(front, end - .00001, impulse)), 0, .00000001f);
        Assert.Equal(0, ClothImpulse.Displacement(front, end, impulse));
        Assert.Equal(0, ClothImpulse.Displacement(front, end + 1, impulse));
        Assert.Equal(0, ClothImpulse.Displacement(front, 2, []));
    }

    [Fact]
    public void ActualSwellRemainsSimilarAtThirtySixtyAndOneTwentyFramesPerSecond()
    {
        ClothImpulse[] impulse = [new(Vector2.Zero, .2, .55f, .5f)];
        var thirty = Measure(impulse, 30); var sixty = Measure(impulse, 60); var fast = Measure(impulse, 120);
        Assert.InRange(Math.Abs(thirty.Peak - sixty.Peak), 0, .001f);
        Assert.InRange(Math.Abs(fast.Peak - sixty.Peak), 0, .001f);
        Assert.InRange(Math.Abs(thirty.NearTime - fast.NearTime), 0, .05);
        Assert.InRange(Math.Abs(thirty.FarTime - fast.FarTime), 0, .05);
    }

    private static Measurement Measure(ClothImpulse[] impulses, int fps = 60)
    {
        const int size = 25;
        var ground = new float[size * size];
        var mesh = ClothSurface.Build(Vector2.Zero, new(1.6f), 0, ground, size, 0, false)
            with { GroundMinimum = ground };
        ClothFootContact[] feet = [new(new(-.18f, -.15f), .004f, .22f), new(new(-.18f, .15f), .004f, .22f),
            new(new(.18f, -.15f), .004f, .22f), new(new(.18f, .15f), .004f, .22f)];
        var active = new ClothContactSolver(); var quiet = new ClothContactSolver();
        float peak = 0, nearPeak = 0, farPeak = 0, afterFade = 0;
        double nearTime = 0, farTime = 0;
        for (var frame = 0; frame <= fps * 4; frame++)
        {
            var now = frame / (double)fps;
            var moving = active.Update(mesh, feet, now, impulses: impulses);
            var baseline = quiet.Update(mesh, feet, now);
            Assert.Equal(ClothContactConstraint.Clearance, moving.Positions[(size * size) / 2].Y, 6);
            for (var i = 0; i < moving.Positions.Length; i++)
            {
                var p = moving.Positions[i]; var at = new Vector2(p.X, p.Z);
                Assert.True(p.Y >= ClothContactConstraint.Clearance);
                var forcing = ClothImpulse.Displacement(at, now, impulses);
                Assert.InRange(forcing, -ClothImpulse.MaximumDisplacement, ClothImpulse.MaximumDisplacement);
                var difference = Math.Abs(p.Y - baseline.Positions[i].Y);
                peak = Math.Max(peak, difference);
                var radius = at.Length();
                if (radius is > .7f and < .85f && difference > nearPeak) { nearPeak = difference; nearTime = now; }
                if (radius is > 1.5f and < 1.65f && difference > farPeak) { farPeak = difference; farTime = now; }
                if (now >= 3.8) afterFade = Math.Max(afterFade, difference);
            }
        }
        return new(peak, farPeak, nearTime, farTime, afterFade);
    }

    private readonly record struct Measurement(float Peak, float FarPeak, double NearTime, double FarTime, float AfterFade);
}
