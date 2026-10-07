using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public class RouteRibbonFieldTests
{
    private static readonly Vector2 HalfSize = new(4, 4);
    private static Vector4 Sample(float[] field, int x, int z)
    {
        var i = (z * RouteRibbonField.GridSize + x) * RouteRibbonField.Channels;
        return new(field[i], field[i + 1], field[i + 2], field[i + 3]);
    }

    [Fact]
    public void StraightRouteHasWorldDistanceForwardArcAndInterpolatedHeight()
    {
        var field = RouteRibbonField.Build([new(-4, 2, 0), new(4, 6, 0)], new(0, 4, 0), HalfSize);
        Assert.Equal(RouteRibbonField.FloatCount, field.Length);
        Assert.Equal(new Vector4(0, 4, 4, 1), Sample(field, 64, 64));
        Assert.Equal(new Vector4(0.5f, 4, 4, 1), Sample(field, 64, 72));
        Assert.Equal(new Vector4(0, 0, 2, 1), Sample(field, 0, 64));
        Assert.Equal(new Vector4(0, 8, 6, 1), Sample(field, 128, 64));
        Assert.Equal(Vector4.Zero, Sample(field, 64, 128));
    }

    [Fact]
    public void ReversingRouteReversesArcDirection()
    {
        var forward = RouteRibbonField.Build([new(-4, 0, 0), new(4, 0, 0)], Vector3.Zero, HalfSize);
        var reverse = RouteRibbonField.Build([new(4, 0, 0), new(-4, 0, 0)], Vector3.Zero, HalfSize);
        Assert.Equal(2, Sample(forward, 32, 64).Y);
        Assert.Equal(6, Sample(reverse, 32, 64).Y);
        Assert.Equal(6, Sample(forward, 96, 64).Y);
        Assert.Equal(2, Sample(reverse, 96, 64).Y);
    }

    [Fact]
    public void CornerKeepsArcLengthAcrossSegments()
    {
        var field = RouteRibbonField.Build([new(-4, 0, -4), new(0, 0, -4), new(0, 0, 4)], Vector3.Zero, HalfSize);
        Assert.Equal(new Vector4(0, 8, 0, 1), Sample(field, 64, 64));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 10)]
    public void CrossingSelectsNearestVerticalLayerOnExactHorizontalTie(float centerY, float expectedY)
    {
        Vector3[] points = [new(-4, 0, 0), new(4, 0, 0), new(float.NaN), new(0, 10, -4), new(0, 10, 4)];
        var field = RouteRibbonField.Build(points, new(0, centerY, 0), HalfSize);
        Assert.Equal(new Vector4(0, 4, expectedY, 1), Sample(field, 64, 64));
    }

    [Fact]
    public void EqualDistanceSameLayerPrefersEarlierRouteSegment()
    {
        Vector3[] points = [new(-4, 0, 0), new(4, 0, 0), new(4, 0, -4), new(0, 0, -4), new(0, 0, 4)];
        var field = RouteRibbonField.Build(points, Vector3.Zero, HalfSize);
        Assert.Equal(4, Sample(field, 64, 64).Y);
    }

    [Fact]
    public void NearestHorizontalSegmentWinsAwayFromTheCrossing()
    {
        Vector3[] points = [new(-4, 0, 0), new(4, 0, 0), new(float.NaN), new(0, 10, -4), new(0, 10, 4)];
        var field = RouteRibbonField.Build(points, Vector3.Zero, HalfSize);
        Assert.Equal(new Vector4(0, 4.5f, 10, 1), Sample(field, 64, 72));
    }

    [Fact]
    public void OffFootprintSegmentsStillContributeArcLength()
    {
        var field = RouteRibbonField.Build([new(-100, 0, 0), new(-50, 0, 0), new(100, 0, 0)], Vector3.Zero, HalfSize);
        Assert.Equal(new Vector4(0, 100, 0, 1), Sample(field, 64, 64));
    }

    [Fact]
    public void InvalidPointBreaksRouteWithoutInventingBridge()
    {
        Vector3[] points = [new(-4, 0, 0), new(-3, 0, 0), new(float.NaN), new(3, 0, 0), new(4, 0, 0)];
        var field = RouteRibbonField.Build(points, Vector3.Zero, HalfSize);
        Assert.Equal(Vector4.Zero, Sample(field, 64, 64));
        Assert.Equal(1, Sample(field, 0, 64).W);
        Assert.Equal(new Vector4(0, 1, 0, 1), Sample(field, 128, 64));
    }

    [Fact]
    public void ExtremeCoordinateIsInvalidAndDoesNotConnectItsNeighbours()
    {
        Vector3[] points = [new(-4, 0, 0), new(-3, 0, 0), new(float.MaxValue, 0, 0), new(3, 0, 0), new(4, 0, 0)];
        var field = RouteRibbonField.Build(points, Vector3.Zero, HalfSize);
        Assert.Equal(Vector4.Zero, Sample(field, 64, 64));
        Assert.All(field, value => Assert.True(float.IsFinite(value)));
    }

    [Fact]
    public void DuplicateAndVerticalOnlySegmentsDoNotDrawOrPoisonFollowingSegment()
    {
        var empty = RouteRibbonField.Build([new(0, 0, 0), new(0, 10, 0), new(0, 10, 0)], Vector3.Zero, HalfSize);
        Assert.All(empty, value => Assert.Equal(0, value));
        var field = RouteRibbonField.Build([new(0, 0, 0), new(0, 0, 0), new(4, 0, 0)], Vector3.Zero, HalfSize);
        Assert.Equal(new Vector4(0, 2, 0, 1), Sample(field, 96, 64));
    }

    [Fact]
    public void SegmentJustOutsideFootprintContributesOnlyItsNearbyBand()
    {
        var field = RouteRibbonField.Build([new(-5, 0, 4.5f), new(5, 0, 4.5f)], Vector3.Zero, HalfSize);
        Assert.Equal(new Vector4(0.5f, 5, 0, 1), Sample(field, 64, 128));
        Assert.Equal(Vector4.Zero, Sample(field, 64, 64));
    }

    [Fact]
    public void SegmentFarOutsideFootprintLeavesAnEmptyField()
    {
        var field = RouteRibbonField.Build([new(-5000, 0, 4000), new(5000, 0, 4000)], Vector3.Zero, HalfSize);
        Assert.All(field, value => Assert.Equal(0, value));
    }

    [Fact]
    public void MaximumRouteSizeAndHugeFiniteBandRemainBoundedAndFinite()
    {
        var points = Enumerable.Range(0, RouteRibbonField.MaxPoints)
            .Select(i => new Vector3(i % 2 == 0 ? -5000 : 5000, 0, i % 3 == 0 ? -5000 : 5000)).ToArray();
        var field = RouteRibbonField.Build(points, Vector3.Zero, HalfSize, float.MaxValue);
        Assert.Equal(RouteRibbonField.FloatCount, field.Length);
        Assert.All(field, value => Assert.True(float.IsFinite(value)));
        Assert.Equal(1, Sample(field, 64, 64).W);
    }

    [Fact]
    public void TooManyPointsFailClosedInsteadOfSilentlyTruncating()
    {
        var points = Enumerable.Repeat(Vector3.Zero, RouteRibbonField.MaxPoints + 1).ToArray();
        var field = RouteRibbonField.Build(points, Vector3.Zero, HalfSize);
        Assert.All(field, value => Assert.Equal(0, value));
    }

    [Fact]
    public void EmptyRoutesAndInvalidFootprintsFailClosed()
    {
        Vector3[] points = [new(-4, 0, 0), new(4, 0, 0)];
        var fields = new[]
        {
            RouteRibbonField.Build(null, Vector3.Zero, HalfSize),
            RouteRibbonField.Build([], Vector3.Zero, HalfSize),
            RouteRibbonField.Build([Vector3.Zero], Vector3.Zero, HalfSize),
            RouteRibbonField.Build(points, new(float.NaN), HalfSize),
            RouteRibbonField.Build(points, Vector3.Zero, new(0, 1)),
            RouteRibbonField.Build(points, Vector3.Zero, new(1, float.PositiveInfinity)),
            RouteRibbonField.Build(points, Vector3.Zero, new(float.MaxValue, 1)),
            RouteRibbonField.Build(points, Vector3.Zero, HalfSize, -1),
            RouteRibbonField.Build(points, Vector3.Zero, HalfSize, float.NaN),
        };
        foreach (var field in fields) Assert.All(field, value => Assert.Equal(0, value));
    }

    [Fact]
    public void NonSquareTranslatedFootprintUsesIndependentWorldAxes()
    {
        var field = RouteRibbonField.Build([new(8, 3, 20), new(12, 3, 20)], new(10, 3, 20), new(2, 8));
        Assert.Equal(new Vector4(0, 2, 3, 1), Sample(field, 64, 64));
        Assert.Equal(new Vector4(0.5f, 2, 3, 1), Sample(field, 64, 68));
        Assert.Equal(new Vector4(0, 0, 3, 1), Sample(field, 0, 64));
    }
}
