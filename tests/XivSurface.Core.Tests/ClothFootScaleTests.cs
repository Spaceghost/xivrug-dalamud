using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothFootScaleTests
{
    private static readonly Vector3 Foot = new(0, .65f, 0), Toe = new(0, .6f, .2f);

    [Theory]
    [InlineData(1.5f, .5f, .5f)]
    [InlineData(.5f, 1.5f, .5f)]
    [InlineData(.5f, .5f, 1.5f)]
    public void EverySkeletonAxisContributesToTheConservativeBootEnvelope(float x, float y, float z)
    {
        Assert.True(ClothFootClearance.TryBoot(Foot, Toe, Vector3.Zero, new Vector3(x,y,z), out var heel, out var toe));
        Assert.Equal(.33f, heel.Radius, 5);
        Assert.Equal(.6f - ClothFootClearance.SoleInset * 1.5f, heel.FootY, 5);
        Assert.Equal(heel.FootY, toe.FootY); Assert.Equal(heel.Radius, toe.Radius);
    }

    [Fact]
    public void TallNarrowModelCannotUseItsWidthAsSoleDepth()
    {
        var scale = new Vector3(.5f, 1.5f, .5f);
        Assert.True(ClothFootClearance.TryBoot(Foot, Toe, Vector3.Zero, .5f, out var oldHeel, out var oldToe));
        Assert.True(ClothFootClearance.TryBoot(Foot, Toe, Vector3.Zero, scale, out var heel, out var toe));
        // This cloth fragment was admitted underneath the falsely high sole.
        var overlapping = new Vector3(0, .55f, .1f);
        Assert.Equal(0, ClothFootClearance.Exclusion(overlapping, [oldHeel, oldToe]));
        Assert.Equal(1, ClothFootClearance.Exclusion(overlapping, [heel, toe]));
    }

    [Theory]
    [InlineData(.2f)] [InlineData(.5f)] [InlineData(1)] [InlineData(1.5f)]
    public void UniformScalePreservesTheExistingFootEstimate(float scale)
    {
        Assert.True(ClothFootClearance.TryBoot(Foot, Toe, Vector3.Zero, scale, out var a, out var b));
        Assert.True(ClothFootClearance.TryBoot(Foot, Toe, Vector3.Zero, new Vector3(scale), out var c, out var d));
        Assert.Equal(a, c); Assert.Equal(b, d);
    }

    [Theory]
    [InlineData(1.55f)] [InlineData(2)] [InlineData(4)]
    public void OversizedBootIsRejectedInsteadOfShrinkingItsProtection(float scale)
    {
        Assert.True(ClothFootClearance.NominalBootRadius * scale > ClothFootClearance.MaximumBootRadius);
        Assert.False(ClothFootClearance.TryBoot(Foot, Toe, Vector3.Zero, scale, out var heel, out var toe));
        Assert.Equal(default, heel); Assert.Equal(default, toe);
        var guard = ClothFootClearance.WithFallback([heel, toe, heel, toe], Vector3.Zero);
        Assert.False(new ClothFootRenderFrame(1, 0, guard).CanRender(1, 0));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(.1f)] [InlineData(5)]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)]
    public void InvalidAxisCannotBeHiddenByAnotherValidAxis(float invalid)
    {
        foreach (var scale in new[] { new Vector3(invalid,1,1), new Vector3(1,invalid,1), new Vector3(1,1,invalid) })
        {
            Assert.False(ClothFootClearance.TryBoot(Foot, Toe, Vector3.Zero, scale, out var heel, out var toe));
            Assert.Equal(default, heel); Assert.Equal(default, toe);
        }
    }

    [Fact]
    public void LargestAdmittedEstimateStillFitsTheTemporalGpuBudget()
    {
        var scale = ClothFootClearance.MaximumBootRadius / ClothFootClearance.NominalBootRadius;
        Assert.True(ClothFootClearance.TryBoot(Foot, Toe, Vector3.Zero, new Vector3(scale), out var heel, out var toe));
        Assert.Equal(ClothFootClearance.MaximumBootRadius, heel.Radius, 6);
        var frame = new ClothFootRenderFrame(1, 0, [heel, toe, heel, toe]);
        Assert.True(frame.CanRender(1, ClothFootRenderFrame.MaximumAgeSeconds));
        Assert.False(ClothFootClearance.TryBoot(Foot, Toe, Vector3.Zero, scale + .00001f, out _, out _));
    }
}
