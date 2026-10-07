using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class MeasuredFloorContactTests
{
    private static LayerTriangle Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (normal.Y < 0) normal = -normal;
        return new(a, b, c, normal);
    }

    private static LayerTriangle Lower => Triangle(new(-2, 0, -1), new(0, 0, -1), new(0, 0, 1));
    private static LayerTriangle Crease => Triangle(new(0, 0, -1), new(.4f, .2f, 0), new(0, 0, 1));
    private static LocalFloorLayer Seeded()
    {
        var layer = new LocalFloorLayer();
        Assert.False(layer.BeginFrame(new(new(-.2f, 0, 0), Lower), 1));
        return layer;
    }

    [Fact]
    public void ActualAdjacentCreaseIsRetainedWithoutChangingRootOrOldEvidenceAge()
    {
        var layer = Seeded(); var before = layer.CaptureReplay();
        var hit = new LayerFloorHit(new(.2f, .1f, 0), Crease);
        Assert.True(hit.Valid);
        Assert.False(layer.ObserveExact(Crease)); // Refresh is not admission.
        Assert.Equal(LayerQueryResult.Unknown, layer.TrySurfacePath(before.RootPoint, hit.Position, out _));
        Assert.True(layer.TryRetainMeasuredFloorHit(hit));
        Assert.Equal(2, layer.Count);
        var after = layer.CaptureReplay();
        Assert.Equal(before.Root, after.Root); Assert.Equal(before.RootPoint, after.RootPoint);
        Assert.Equal(before.Now, after.Now); Assert.Equal(before.Faces[0], after.Faces[0]);
        Assert.Equal(LayerQueryResult.Success, layer.TrySurfacePath(before.RootPoint, hit.Position, out var path));
        Assert.Equal(hit.Position, path[^1]);
        Assert.True(path.Length >= 3); // The crease remains in the floor path.
        // The retained triangle is not permission to extrapolate beyond it,
        // and this helper never performs the separate native wall proof.
        Assert.Equal(LayerQueryResult.Unknown,
            layer.TrySurfacePath(before.RootPoint, new(.8f, .4f, 0), out _));
    }

    [Fact]
    public void UnseededLayerCannotChooseItsRootFromAChordHit()
    {
        var layer = new LocalFloorLayer();
        Assert.False(layer.TryRetainMeasuredFloorHit(new(new(.2f, .1f, 0), Crease)));
        Assert.Equal(0, layer.Count);
    }

    [Fact]
    public void ValidHitOutsideExistingLocalHeightBandCannotBroadenIt()
    {
        var layer = Seeded();
        var steep = Triangle(new(0, 0, -1), new(.5f, .75f, 0), new(0, 0, 1));
        var hit = new LayerFloorHit(new(.3f, .45f, 0), steep);
        Assert.True(hit.Valid); Assert.True(steep.Walkable);
        Assert.True(LocalFloorLayer.SharesBoundary(Lower, steep));
        Assert.False(layer.TryRetainMeasuredFloorHit(hit)); Assert.Equal(1, layer.Count);
    }

    [Fact]
    public void OverheadDeckInsideHeightBandDoesNotReplaceLocallyReachedFloor()
    {
        var layer = Seeded();
        var deck = Lower with { A = Lower.A + Vector3.UnitY * .2f,
            B = Lower.B + Vector3.UnitY * .2f, C = Lower.C + Vector3.UnitY * .2f };
        Assert.False(layer.TryRetainMeasuredFloorHit(new(new(-.2f, .2f, 0), deck)));
        Assert.Equal(1, layer.Count);
    }

    [Fact]
    public void GraphConnectedDistantRampDoesNotMakeOverlappingDeckTheCurrentLayer()
    {
        var lowerA = Triangle(new(-2, 0, -1), new(2, 0, -1), new(-2, 0, 1));
        var lowerB = Triangle(new(2, 0, -1), new(2, 0, 1), new(-2, 0, 1));
        var rampA = Triangle(new(2, 0, -1), new(4, .2f, -1), new(2, 0, 1));
        var rampB = Triangle(new(4, .2f, -1), new(4, .2f, 1), new(2, 0, 1));
        var upperA = Triangle(new(-2, .2f, -1), new(4, .2f, -1), new(-2, .2f, 1));
        var upperB = Triangle(new(4, .2f, -1), new(4, .2f, 1), new(-2, .2f, 1));
        var layer = new LocalFloorLayer(); Assert.True(layer.Seed(new(new(-1, 0, 0), lowerA)));
        Add(new(1, 0, 0), lowerB); Add(new(2.2f, .02f, -.6f), rampA); Add(new(3.2f, .12f, .1f), rampB);
        Assert.False(layer.BeginFrame(new(new(3.9f, .2f, .7f), upperB), .1));
        Add(new(0, .2f, 0), upperA);
        Assert.False(layer.BeginFrame(new(Vector3.Zero, lowerA), .2));
        Assert.True(layer.Contains(upperA)); Assert.Equal(6, layer.Count);
        var before = layer.CaptureReplay();
        Assert.False(layer.TryRetainMeasuredFloorHit(new(new(0, .2f, 0), upperA)));
        var after = layer.CaptureReplay();
        Assert.Equal(before.Root, after.Root); Assert.Equal(before.RootPoint, after.RootPoint);
        Assert.Equal(before.Faces, after.Faces);

        void Add(Vector3 at, LayerTriangle triangle)
        {
            Assert.True(layer.TryProbe(new(at.X, at.Z), out var probe));
            Assert.True(layer.Accept(probe, new(at, triangle)));
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(.01f)]
    public void PointTouchAndPositiveGapCannotInventAnEdge(float gap)
    {
        var first = Triangle(new(-1, 0, -1), new(0, 0, 0), new(-1, 0, 1));
        var candidate = Triangle(new(gap, 0, 0), new(.4f + gap, 0, .4f), new(.4f + gap, 0, -.4f));
        var layer = new LocalFloorLayer(); Assert.True(layer.Seed(new(new(-.2f, 0, 0), first)));
        var hit = new LayerFloorHit(new(.2f + gap, 0, 0), candidate);
        Assert.True(hit.Valid); Assert.False(LocalFloorLayer.SharesBoundary(first, candidate));
        Assert.False(layer.TryRetainMeasuredFloorHit(hit)); Assert.Equal(1, layer.Count);
    }

    [Theory]
    [InlineData(.451f)] [InlineData(.475f)] [InlineData(.499f)]
    public void FloorChordThresholdDoesNotRelaxWalkableNormal(float normalY)
    {
        var slope = MathF.Sqrt(1 / (normalY * normalY) - 1);
        var face = Triangle(new(0, 0, -1), new(.4f, .4f * slope, 0), new(0, 0, 1));
        Assert.True(face.Valid); Assert.InRange(face.Normal.Y, .45f, .5f);
        var layer = Seeded();
        Assert.False(layer.TryRetainMeasuredFloorHit(new(new(.1f, .1f * slope, 0), face)));
        Assert.Equal(1, layer.Count);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void InvalidActualHitCannotBeReplacedWithAPlaneGuess(int malformed)
    {
        var layer = Seeded();
        var hit = malformed switch
        {
            0 => new LayerFloorHit(new(float.NaN, .1f, 0), Crease),
            1 => new LayerFloorHit(new(.2f, .11f, 0), Crease),
            2 => new LayerFloorHit(new(.8f, .4f, 0), Crease),
            _ => new LayerFloorHit(new(.2f, .1f, 0), Crease with { Normal = -Crease.Normal }),
        };
        Assert.False(hit.Valid);
        Assert.False(layer.TryRetainMeasuredFloorHit(hit)); Assert.Equal(1, layer.Count);
    }

    [Fact]
    public void RaisedCurbStillRequiresMeasuredRiserEvidence()
    {
        var upper = Triangle(new(0, .2f, -1), new(2, .2f, 1), new(0, .2f, 1));
        var layer = Seeded(); var hit = new LayerFloorHit(new(.2f, .2f, 0), upper);
        Assert.True(hit.Valid);
        Assert.False(layer.TryRetainMeasuredFloorHit(hit)); Assert.Equal(1, layer.Count);
    }

    [Fact]
    public void RetentionDoesNotExtendUnobservedFacesOrBypassNormalExpiry()
    {
        var layer = Seeded();
        Assert.True(layer.TryRetainMeasuredFloorHit(new(new(.2f, .1f, 0), Crease)));
        Assert.False(layer.BeginFrame(new(new(-.2f, 0, 0), Lower), 3.01));
        Assert.False(layer.Contains(Crease)); Assert.Equal(1, layer.Count);
    }
}
