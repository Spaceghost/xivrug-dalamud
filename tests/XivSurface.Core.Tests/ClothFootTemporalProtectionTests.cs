using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothFootTemporalProtectionTests
{
    private static ClothFootContact[] Boots(float radius = .22f, float sole = .004f) =>
    [
        new(new(-.3f,-.1f), sole, radius), new(new(-.3f,.1f), sole, radius),
        new(new(.3f,-.1f), sole, radius), new(new(.3f,.1f), sole, radius),
    ];

    [Theory]
    [InlineData(.11f)] [InlineData(.22f)] [InlineData(.34f)]
    public void FeatherStartsOutsideTheEntireEstimatedBootEvenAtSampleTime(float radius)
    {
        var raw = Boots(radius);
        var frame = new ClothFootRenderFrame(1, 0, raw);
        Span<ClothFootContact> render = stackalloc ClothFootContact[4];
        Assert.True(frame.TryGetRenderContacts(1, 0, render));
        // Previously only 70% of the estimated footprint was solid. At 85%
        // the renderer still drew half-opacity cloth over the boot's margin.
        var oldFeather = new Vector3(.3f + radius * .85f, .035f, 0);
        Assert.InRange(ClothFootClearance.Exclusion(oldFeather, raw), .4999f, .5001f);
        Assert.Equal(1, ClothFootClearance.Exclusion(oldFeather, render));
        Assert.InRange(ClothFootClearance.Exclusion(new(.3f + radius, .035f, 0), render), .9999f, 1);
        // Softness is retained outside the protected footprint, not removed.
        var newFeather = new Vector3(.3f + render[2].Radius * .85f, .035f, 0);
        Assert.InRange(ClothFootClearance.Exclusion(newFeather, render), .4999f, .5001f);
    }

    [Theory]
    [InlineData(.008)] [InlineData(.016)] [InlineData(.033)]
    public void MovingLeadingBootCoreRemainsFullyExcludedWhenOldMaskAlreadyMisses(double age)
    {
        var raw = Boots(.11f); var frame = new ClothFootRenderFrame(1, 0, raw);
        Span<ClothFootContact> render = stackalloc ClothFootContact[4];
        Assert.True(frame.TryGetRenderContacts(1, age, render));
        // Lateral travel escapes the heel/toe segment, so bridging alone is
        // not protection. Six yalms/s is a test trajectory, not a game limit.
        var x = .3f + (float)(6 * age) + .11f;
        var point = new Vector3(x, .035f, 0);
        Assert.Equal(0, ClothFootClearance.Exclusion(point, raw));
        Assert.InRange(ClothFootClearance.Exclusion(point, render), .9999f, 1);
    }

    [Theory]
    [InlineData(.008)] [InlineData(.016)] [InlineData(.033)]
    public void DescendingBootsNeedLowerSoleCeilingAsWellAsHorizontalPadding(double age)
    {
        var raw = Boots(sole: .7f); var frame = new ClothFootRenderFrame(1, 0, raw);
        Span<ClothFootContact> render = stackalloc ClothFootContact[4];
        Assert.True(frame.TryGetRenderContacts(1, age, render));
        var currentSole = .7f - (float)(ClothFootRenderFrame.DownwardSpeedLimit * age);
        var overlappingCloth = new Vector3(.3f, currentSole + .01f, 0);
        Assert.Equal(0, ClothFootClearance.Exclusion(overlappingCloth, raw));
        Assert.InRange(ClothFootClearance.Exclusion(overlappingCloth, render), .9999f, 1);
    }

    [Theory]
    [InlineData(0)] [InlineData(.008)] [InlineData(.016)] [InlineData(.033)]
    public void BoundedFutureCapsulesAreContainedAcrossDirectionsRadiiAndDescent(double age)
    {
        // Property-style finite coverage: independent heel/toe movement within
        // the declared bound, including translation, rotation and shortening.
        // This proves the policy under that assumption, not actual game speed.
        var travel = (float)(ClothFootRenderFrame.HorizontalSpeedLimit * age);
        var descent = (float)(ClothFootRenderFrame.DownwardSpeedLimit * age);
        var render = new ClothFootContact[4];
        foreach (var radius in new[] { .11f, .22f, .34f })
        {
            var raw = Boots(radius, .7f); var frame = new ClothFootRenderFrame(1, 0, raw);
            Assert.True(frame.TryGetRenderContacts(1, age, render));
            for (var heading = 0; heading < 24; heading++)
            for (var bend = -1; bend <= 1; bend++)
            {
                var angle = heading * MathF.Tau / 24;
                var a = raw[2].Center + travel * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                var b = raw[3].Center + travel * new Vector2(MathF.Cos(angle + bend * .2f), MathF.Sin(angle + bend * .2f));
                Assert.True(Vector2.Distance(a, b) < .75f);
                for (var along = 0; along <= 4; along++)
                for (var around = 0; around < 24; around++)
                {
                    var direction = around * MathF.Tau / 24;
                    var at = Vector2.Lerp(a, b, along / 4f)
                        + radius * new Vector2(MathF.Cos(direction), MathF.Sin(direction));
                    var point = new Vector3(at.X, .7f - descent + .01f, at.Y);
                    Assert.InRange(ClothFootClearance.Exclusion(point, render), .9999f, 1);
                }
            }
        }
    }

    [Fact]
    public void ExpansionIsRenderOnlyKeepsPairsAndFitsTheExistingGpuEncoding()
    {
        var raw = Boots(.34f); var original = raw.ToArray(); var frame = new ClothFootRenderFrame(1, 0, raw);
        Span<ClothFootContact> render = stackalloc ClothFootContact[4];
        Assert.True(frame.TryGetRenderContacts(1, ClothFootRenderFrame.MaximumAgeSeconds, render));
        Assert.Equal(original, raw); Assert.Equal(original, frame.Contacts.ToArray());
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(raw[i].Center, render[i].Center);
            Assert.InRange(render[i].Radius, 1.438f, ClothFootRenderFrame.MaximumRenderRadius);
            Assert.InRange(raw[i].FootY - render[i].FootY, .66f, ClothFootRenderFrame.MaximumDownwardPadding);
            Assert.NotEqual(Vector4.Zero, ClothFootClearance.Pack(render, i));
        }
        Assert.Equal(render[0].Radius, render[1].Radius); Assert.Equal(render[0].FootY, render[1].FootY);
        Assert.Equal(render[2].Radius, render[3].Radius); Assert.Equal(render[2].FootY, render[3].FootY);
    }

    [Theory]
    [InlineData(.033334)] [InlineData(.1)] [InlineData(-.001)]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void ExpiredOrMalformedClockClearsOutputInsteadOfClampingUncertainty(double age)
    {
        var frame = new ClothFootRenderFrame(1, 0, Boots()); var output = Boots();
        Assert.False(frame.TryGetRenderContacts(1, age, output));
        Assert.All(output, value => Assert.Equal(default, value));
    }

    [Fact]
    public void InvalidContactsFallbackShortBuffersAndExcessiveCoordinatesFailClosed()
    {
        var missing = new ClothFootRenderFrame(1, 0, ClothFootClearance.WithFallback([], Vector3.Zero));
        var output = Boots(); Assert.False(missing.TryGetRenderContacts(1, .001, output));
        Assert.All(output, value => Assert.Equal(default, value));
        var invalid = Boots(); invalid[0] = invalid[0] with { Radius = .35f };
        Assert.False(new ClothFootRenderFrame(1, 0, invalid).TryGetRenderContacts(1, .01, output));
        invalid = Boots(); invalid[1] = invalid[1] with { FootY = float.NaN };
        Assert.False(new ClothFootRenderFrame(1, 0, invalid).TryGetRenderContacts(1, .01, output));
        Assert.False(new ClothFootRenderFrame(1, 0, Boots()).TryGetRenderContacts(1, .01, output.AsSpan(0, 3)));
        // The expanded sole would leave the admitted finite coordinate domain.
        Assert.False(new ClothFootRenderFrame(1, 0, Boots(sole: -1_000_000)).TryGetRenderContacts(1, .01, output));
        Assert.All(output, value => Assert.Equal(default, value));
    }
}
