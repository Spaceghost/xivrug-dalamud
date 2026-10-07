using XivSurface.Core;

namespace XivSurface.Core.Tests;

public class RugTests
{
    [Fact]
    public void DefaultIsBoundedAndClockIsPeriodic()
    {
        Assert.True(RugStyle.Default.TryPack(2, out var g, out var m));
        Assert.True(RugStyle.Default.TryPack(18, out var g2, out var m2));
        Assert.Equal(g, g2);
        Assert.Equal(m, m2);
        Assert.Equal(1, m.Z);
    }

    [Fact]
    public void MotionAndStyleCanBeDisabledIndependently()
    {
        var style = RugStyle.Default with { Animate = false };
        Assert.True(style.TryPack(3, out var g, out var m));
        Assert.Equal(0, g.W);
        Assert.Equal(1, m.Z);
        Assert.True((style with { Enabled = false }).TryPack(3, out _, out m));
        Assert.Equal(0, m.Z);
    }

    [Fact]
    public void InvalidMaterialNeverReachesGpu()
    {
        foreach (var style in new[] { RugStyle.Default with { FringeLength = float.NaN },
            RugStyle.Default with { BorderWidth = 0 }, RugStyle.Default with { ThreadsPerYalm = 1000 },
            RugStyle.Default with { DetailStrength = -1 } })
            Assert.False(style.TryPack(0, out _, out _));
        var constants = new SurfaceConstants { FrameValidated = 1 };
        Assert.False(constants.SetRug(RugStyle.Default, float.PositiveInfinity));
        Assert.Equal(0u, constants.FrameValidated);
    }
}
