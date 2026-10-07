using XivSurface.Core;
using System.Numerics;

namespace XivSurface.Core.Tests;

public class NavigationRouteTests
{
    private const string Valid = """{"version":1,"territoryId":10,"generatedAt":1000,"walkable":true,"action":"Follow","next":"Task","points":[[0,0,0],[1,2,3]]}""";
    [Fact]
    public void FreshRouteDecodesWithoutWayfinderAssembly()
    {
        Assert.True(NavigationRoute.TryRead(Valid, 10, 1100, out var route));
        Assert.Equal(2, route!.Points.Length);
        Assert.Equal(2, route.Points[1].Y);
    }
    [Theory]
    [InlineData(11, 1100)]
    [InlineData(10, 999)]
    [InlineData(10, 3001)]
    public void WrongZoneStaleOrFutureRouteIsRejected(uint zone, long now) =>
        Assert.False(NavigationRoute.TryRead(Valid, zone, now, out _));
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("garbage")]
    public void MalformedPayloadFailsClosed(string json) =>
        Assert.False(NavigationRoute.TryRead(json, 10, 1100, out _));

    private const string VisualRoute = """{"version":1,"territoryId":10,"generatedAt":1000,"walkable":true,"action":"Follow","next":"Task","points":[[0,0,0],[10,2,0],[10,4,10]],"visual":{"progress":6,"guideArc":11.5,"lookAheadArc":14,"guide":[10,2.3,1.5],"lookAhead":[10,2.8,4],"color":[0.34,0.71,0.91,1]}}""";

    [Fact]
    public void SharedProgressGuidePositionsAndTintDecodeWithoutPublisherAssembly()
    {
        Assert.True(NavigationRoute.TryRead(VisualRoute, 10, 1100, out var route));
        var visual = Assert.IsType<NavigationVisual>(route!.Visual);
        Assert.Equal(new Vector4(6, 11.5f, 14, 1), visual.Arcs);
        Assert.InRange(Vector3.Distance(new Vector3(10, 2.8f, 4), visual.LookAheadPosition), 0, .00001f);
        Assert.Equal(new Vector4(.34f, .71f, .91f, 1), visual.Color);
        Assert.True(NavigationRoute.TryRead(Valid, 10, 1100, out var legacy));
        Assert.Null(legacy!.Visual);
    }

    [Fact]
    public void IndependentCompanionBehindPlayerKeepsItsActualRootAndLookAhead()
    {
        Assert.True(NavigationRoute.TryRead(VisualRoute.Replace("\"progress\":6", "\"progress\":18"), 10, 1100, out var route));
        Assert.Equal(18, route!.Visual!.Progress);
        Assert.Equal(11.5f, route.Visual.GuideArc);
        Assert.Equal(14, route.Visual.LookAheadArc);
        var tooFar = VisualRoute.Replace("[10,4,10]", "[10,4,100]").Replace("\"progress\":6", "\"progress\":100");
        Assert.False(NavigationRoute.TryRead(tooFar, 10, 1100, out _));
    }

    [Theory]
    [InlineData("\"progress\":6", "\"progress\":-1")]
    [InlineData("\"progress\":6", "\"progress\":21")]
    [InlineData("\"guideArc\":11.5", "\"guideArc\":5")]
    [InlineData("\"lookAheadArc\":14", "\"lookAheadArc\":21")]
    [InlineData("\"color\":[0.34,0.71,0.91,1]", "\"color\":[0.34,0.71,0.91]")]
    [InlineData("\"color\":[0.34,0.71,0.91,1]", "\"color\":[2,0.71,0.91,1]")]
    [InlineData("\"guide\":[10,2.3,1.5]", "\"guide\":[0,2.3,1.5]")]
    [InlineData("\"lookAhead\":[10,2.8,4]", "\"lookAhead\":[10,30,4]")]
    [InlineData("\"lookAhead\":[10,2.8,4]", "\"lookAhead\":null")]
    [InlineData("\"progress\":6", "\"progress\":1e100")]
    public void MalformedOrUnrelatedVisualMetadataFailsClosed(string oldValue, string newValue) =>
        Assert.False(NavigationRoute.TryRead(VisualRoute.Replace(oldValue, newValue), 10, 1100, out _));
}
