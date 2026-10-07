using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class LocalFloorLayerTests
{
    private static LayerTriangle Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (normal.Y < 0) normal = -normal;
        return new(a, b, c, normal);
    }
    private static LayerTriangle Lower => Triangle(new(-2, 0, -1), new(0, 0, -1), new(0, 0, 1));
    private static LayerTriangle Upper(float height = .2f) => Triangle(new(0, height, -1), new(2, height, 1), new(0, height, 1));
    private static LayerTriangle[] Riser(float height = .2f) =>
    [
        Triangle(new(0, 0, -1), new(0, 0, 1), new(0, height, 1)),
        Triangle(new(0, 0, -1), new(0, height, 1), new(0, height, -1)),
    ];

    [Fact]
    public void GeometryDiagnosticDistinguishesAnActualRampFromTreadsAndRisers()
    {
        var ramp = Triangle(new(-10, -2, -10), new(10, 2, -10), new(0, 0, 10));
        var layer = new LocalFloorLayer();
        Assert.True(layer.Seed(new(Vector3.Zero, ramp)));
        var summary = layer.DescribeGeometry();
        Assert.Equal((0, 1, 0), (summary.FlatFaces, summary.SlopedFaces, summary.RiserFaces));
        Assert.Equal(ramp.Normal.Y, summary.MinimumNormalY);
        Assert.True(layer.Seed(new(new(-.2f, 0, 0), Lower)));
        Assert.True(layer.TryProbe(new(.2f, 0), out var probe));
        Assert.True(layer.Accept(probe, new(new(.2f, .2f, 0), Upper()), Riser()));
        summary = layer.DescribeGeometry();
        Assert.Equal((2, 0, 2), (summary.FlatFaces, summary.SlopedFaces, summary.RiserFaces));
        Assert.Equal(1, summary.MinimumNormalY);
        Assert.Equal(summary, layer.DescribeGeometry()); // observation cannot mutate evidence
        layer.Seed(default);
        summary = layer.DescribeGeometry();
        Assert.Equal((0, 0, 0), (summary.FlatFaces, summary.SlopedFaces, summary.RiserFaces));
    }

    [Fact]
    public void ElevenDegreeActualPlaneUsesItsOwnHeightNotHorizontalPlayerBand()
    {
        var plane = Triangle(new(-10, -2, -10), new(10, 2, -10), new(0, 0, 10));
        var layer = new LocalFloorLayer(); Assert.True(layer.Seed(new(Vector3.Zero, plane)));
        var at = new Vector2(2, 0); var hit = new LayerFloorHit(new(2, .4f, 0), plane);
        Assert.True(ClothFloorQueryPolicy.TryLocalProbe(at, 0, out var old));
        Assert.False(ClothFloorQueryPolicy.Accept(old, hit.Position, plane.Normal, out _));
        Assert.True(layer.TryProbe(at, out var local));
        Assert.Equal(.75f, local.StartY, 4); Assert.True(layer.Accept(local, hit));
    }

    [Fact]
    public void AdjacentActualSlopeTrianglesPropagateLayerBeyondInitialHeightBand()
    {
        var left = Triangle(new(-2, -.4f, -1), new(0, 0, -1), new(0, 0, 1));
        var right = Triangle(new(0, 0, -1), new(.4f, .08f, 0), new(0, 0, 1));
        var layer = new LocalFloorLayer(); Assert.True(layer.Seed(new(new(-.2f, -.04f, 0), left)));
        Assert.True(layer.TryProbe(new(.2f, 0), out var probe));
        Assert.True(layer.Accept(probe, new(new(.2f, .04f, 0), right)));
        Assert.Equal(2, layer.Count);
    }

    [Fact]
    public void CurbRequiresActualRiserChainNotJustSmallHeightDifference()
    {
        var layer = new LocalFloorLayer(); Assert.True(layer.Seed(new(new(-.2f, 0, 0), Lower)));
        Assert.True(layer.TryProbe(new(.2f, 0), out var probe));
        var hit = new LayerFloorHit(new(.2f, .2f, 0), Upper());
        Assert.False(layer.Accept(probe, hit));
        Assert.False(layer.Accept(probe, hit, Riser().AsSpan(0, 1)));
        Assert.True(layer.Accept(probe, hit, Riser()));
        Assert.Equal(2, layer.Count);
    }

    [Fact]
    public void OverheadDeckInsideQueryBandIsNotPromotedToThePlayerLayer()
    {
        var ground = Triangle(new(-10, 0, -10), new(10, 0, -10), new(0, 0, 10));
        var deck = ground with { A = ground.A + new Vector3(0, .2f, 0), B = ground.B + new Vector3(0, .2f, 0), C = ground.C + new Vector3(0, .2f, 0) };
        var layer = new LocalFloorLayer(); Assert.True(layer.Seed(new(Vector3.Zero, ground)));
        Assert.True(layer.TryProbe(Vector2.Zero, out var probe));
        Assert.False(layer.Accept(probe, new(new(0, .2f, 0), deck)));
        Assert.True(layer.Accept(probe, new(Vector3.Zero, ground)));
        Assert.Equal(1, layer.Count);
    }

    [Theory]
    [InlineData(.36f)] [InlineData(1f)]
    public void AHighWallIsNotAnAuthoredCurb(float height)
    {
        var layer = new LocalFloorLayer(); Assert.True(layer.Seed(new(new(-.2f, 0, 0), Lower)));
        // Deliberately broad test query does not weaken the connectivity gate.
        var probe = new ClothFloorProbe(new(.2f, 0), 2, 3, -1, 2);
        Assert.False(layer.Accept(probe, new(new(.2f, height, 0), Upper(height)), Riser(height)));
    }

    [Fact]
    public void PointTouchDisconnectedHeightAndInvalidGeometryAreNotSharedEdges()
    {
        var a = Triangle(new(0, 0, 0), new(1, 0, 0), new(0, 0, 1));
        var pointTouch = Triangle(new(0, 0, 0), new(-1, 0, 0), new(0, 0, -1));
        Assert.False(LocalFloorLayer.SharesBoundary(a, pointTouch));
        Assert.False(LocalFloorLayer.SharesBoundary(a, a with { A = a.A + Vector3.UnitY, B = a.B + Vector3.UnitY, C = a.C + Vector3.UnitY }));
        Assert.False(LocalFloorLayer.SharesBoundary(a, a with { A = new(float.NaN) }));
    }

    [Fact]
    public void PartialCollinearSeamsSupportTjunctionsButNotPositiveGaps()
    {
        var a = Triangle(new(0, 0, 0), new(0, 0, 2), new(-1, 0, 0));
        var b = Triangle(new(0, 0, .5f), new(0, 0, 1.5f), new(1, 0, 1));
        Assert.True(LocalFloorLayer.SharesBoundary(a, b));
        Assert.False(LocalFloorLayer.SharesBoundary(a, b with { A = b.A + Vector3.UnitX * .01f, B = b.B + Vector3.UnitX * .01f }));
    }

    [Fact]
    public void QueryHintCannotInventFarawaySupportOrTreatAnUndersideAsGround()
    {
        var layer = new LocalFloorLayer(); Assert.True(layer.Seed(new(new(-.2f, 0, 0), Lower)));
        Assert.False(layer.TryProbe(new(2, 0), out _));
        Assert.False(layer.Seed(new(new(-.2f, 0, 0), Lower with { Normal = -Vector3.UnitY })));
        Assert.False(layer.TryProbe(Vector2.Zero, out _));
    }

    [Fact]
    public void InvalidPlayerPublicationCannotLeaveThePreviousRootUsable()
    {
        var layer = new LocalFloorLayer(); var hit = new LayerFloorHit(new(-.2f,0,0),Lower);
        Assert.True(layer.Seed(hit));
        Assert.False(layer.BeginFrame(default,.1));
        Assert.False(layer.TryProbe(new(-.2f,0),out _));
        Assert.Equal(0,layer.Count);
        Assert.False(layer.BeginFrame(hit,.2));
        Assert.True(layer.TryProbe(new(-.2f,0),out _));
        Assert.False(layer.BeginFrame(hit,double.NaN));
        Assert.False(layer.TryProbe(new(-.2f,0),out _));
    }

    [Fact]
    public void ConnectedRampAndOverlappingDeckUseActualPlayerApproachNotGraphMembership()
    {
        var lowerA = Triangle(new(-2,0,-1),new(2,0,-1),new(-2,0,1));
        var lowerB = Triangle(new(2,0,-1),new(2,0,1),new(-2,0,1));
        var rampA = Triangle(new(2,0,-1),new(4,.2f,-1),new(2,0,1));
        var rampB = Triangle(new(4,.2f,-1),new(4,.2f,1),new(2,0,1));
        var upperA = Triangle(new(-2,.2f,-1),new(4,.2f,-1),new(-2,.2f,1));
        var upperB = Triangle(new(4,.2f,-1),new(4,.2f,1),new(-2,.2f,1));
        var layer = new LocalFloorLayer(); Assert.True(layer.Seed(new(new(-1,0,0),lowerA)));
        Add(new(1,0,0), lowerB); Add(new(2.2f,.02f,-.6f), rampA); Add(new(3.2f,.12f,.1f), rampB);
        Assert.False(layer.BeginFrame(new(new(3.9f,.2f,.7f),upperB), .1)); // newly measured actual root
        Add(new(0,.2f,0), upperA);
        Assert.Equal(6,layer.Count); // both complete surfaces remain in evidence
        Assert.False(layer.BeginFrame(new(Vector3.Zero,lowerA), .2));
        Assert.True(layer.TryProbe(Vector2.Zero,out var below)); Assert.Equal(.35f,below.StartY,4);
        Assert.False(layer.Accept(below,new(new(0,.2f,0),upperA))); // connected is not the chosen layer
        Assert.True(layer.Accept(below,new(Vector3.Zero,lowerA)));
        Assert.False(layer.BeginFrame(new(new(0,.2f,0),upperA),.3));
        Assert.True(layer.TryProbe(Vector2.Zero,out var above)); Assert.Equal(.55f,above.StartY,4);
        Assert.False(layer.Accept(above,new(Vector3.Zero,lowerA)));
        Assert.True(layer.Accept(above,new(new(0,.2f,0),upperA)));
        Assert.True(layer.BeginFrame(new(new(.01f,.2f,0),upperA),.4)); // unchanged layer does not reset
        Assert.Equal(6,layer.Count);

        void Add(Vector3 at, LayerTriangle triangle)
        {
            Assert.True(layer.TryProbe(new(at.X,at.Z),out var probe));
            Assert.True(layer.Accept(probe,new(at,triangle)));
        }
    }

    [Theory]
    [InlineData(2)] [InlineData(10)] [InlineData(40)]
    public void DenseLowerFloorDoesNotLoseToSparseUpperDeckWhenFollowingTheActualApproach(int strips)
    {
        var layer = new LocalFloorLayer(); var lower = new List<LayerTriangle>();
        var width = 4f / strips;
        for (var i = 0; i < strips; i++)
        {
            var x = -2 + i * width;
            var a = Triangle(new(x,0,-1),new(x+width,0,-1),new(x,0,1));
            var b = Triangle(new(x+width,0,-1),new(x+width,0,1),new(x,0,1));
            lower.Add(a); lower.Add(b);
            // Actual measured player support establishes each adjoining face;
            // this fixture deliberately retains both levels afterward.
            var hitA = new LayerFloorHit(new(x+width*.2f,0,-.5f),a);
            if (i == 0) Assert.True(layer.Seed(hitA)); else layer.BeginFrame(hitA,.1);
            layer.BeginFrame(new(new(x+width*.8f,0,.5f),b),.1);
        }
        var rampA = Triangle(new(2,0,-1),new(4,.2f,-1),new(2,0,1));
        var rampB = Triangle(new(4,.2f,-1),new(4,.2f,1),new(2,0,1));
        var upperA = Triangle(new(-2,.2f,-1),new(4,.2f,-1),new(-2,.2f,1));
        var upperB = Triangle(new(4,.2f,-1),new(4,.2f,1),new(-2,.2f,1));
        layer.BeginFrame(new(new(2.2f,.02f,-.6f),rampA),.1);
        layer.BeginFrame(new(new(3.2f,.12f,.1f),rampB),.1);
        layer.BeginFrame(new(new(3.9f,.2f,.7f),upperB),.1);
        layer.BeginFrame(new(new(0,.2f,0),upperA),.1);
        Assert.Equal(strips*2+4,layer.Count);
        var playerLower = lower.First(t => t.Contains(Vector3.Zero));
        Assert.False(layer.BeginFrame(new(Vector3.Zero,playerLower),.2));
        var at = new Vector3(1,0,.5f);
        var chosenLower = lower.First(t => t.Contains(at));
        Assert.True(layer.TryProbe(new(at.X,at.Z),out var below)); Assert.Equal(.35f,below.StartY,4);
        Assert.False(layer.Accept(below,new(at+Vector3.UnitY*.2f,upperB)));
        Assert.True(layer.Accept(below,new(at,chosenLower)));
        Assert.False(layer.BeginFrame(new(new(0,.2f,0),upperA),.3));
        Assert.True(layer.TryProbe(new(at.X,at.Z),out var above)); Assert.Equal(.55f,above.StartY,4);
        Assert.False(layer.Accept(above,new(at,chosenLower)));
        Assert.True(layer.Accept(above,new(at+Vector3.UnitY*.2f,upperB)));
        Assert.Equal(strips*2+4,layer.Count);
    }

    [Fact]
    public void LocallyForkedDifferentHeightsAreUnknownUntilActualPlayerChoosesABranch()
    {
        var before = Triangle(new(-2,0,0),new(0,0,-2),new(0,0,2));
        var flat = Triangle(new(0,0,-2),new(2,0,0),new(0,0,2));
        var ramp = Triangle(new(0,0,-2),new(2,.2f,0),new(0,0,2));
        var layer = new LocalFloorLayer(); var root = new LayerFloorHit(new(-1,0,0),before);
        Assert.True(layer.Seed(root));
        layer.BeginFrame(new(new(1,0,0),flat),.1);
        layer.BeginFrame(new(new(1,.1f,0),ramp),.2);
        layer.BeginFrame(root,.3);
        Assert.Equal(3,layer.Count);
        Assert.False(layer.TryProbe(new(1,0),out _));
        layer.BeginFrame(new(new(1,0,0),flat),.4);
        Assert.True(layer.TryProbe(new(1,0),out var selected)); Assert.Equal(.35f,selected.StartY,4);
    }

    [Fact]
    public void LocalPortalWalkHonorsExplicitManagedDeadline()
    {
        var a = Triangle(new(-2,0,-1),new(0,0,-1),new(0,0,1));
        var b = Triangle(new(0,0,-1),new(2,0,0),new(0,0,1));
        var layer = new LocalFloorLayer(); var root = new LayerFloorHit(new(-.5f,0,0),a);
        Assert.True(layer.Seed(root)); layer.BeginFrame(new(new(.5f,0,0),b),.1); layer.BeginFrame(root,.2);
        var checks = 0;
        Assert.False(layer.TryNearest(new(.5f,0),out _,out _,() => ++checks < 2));
        Assert.Equal(2,checks);
        Assert.True(layer.TryNearest(new(.5f,0),out var selected,out _)); Assert.Equal(b,selected);
    }

    [Fact]
    public void SingleActualFaceProvesTheWholeSlopedCompressedCellNotOnlyItsSamples()
    {
        var triangle = Triangle(new(-4,-.8f,-4),new(4,.8f,-4),new(0,0,4));
        var hit = new LayerFloorHit(Vector3.Zero,triangle);
        Vector3 a = new(-.2f,-.04f,-.2f), b = new(.2f,.04f,-.2f), c = new(-.2f,-.04f,.2f), d = new(.2f,.04f,.2f);
        Assert.True(hit.ProvesPlanarCell(a,b,c,d));
        Assert.False(hit.ProvesPlanarCell(a,b,c,d+Vector3.UnitY*.001f));
        Assert.False(hit.ProvesPlanarCell(a,b,c,new(float.NaN)));
        Assert.False((hit with { Position = new(.001f,.0002f,0) }).ProvesPlanarCell(a,b,c,d));
        var small = Triangle(new(-.1f,-.02f,-.1f),new(.1f,.02f,-.1f),new(0,0,.1f));
        Assert.False(new LayerFloorHit(Vector3.Zero,small).ProvesPlanarCell(a,b,c,d));
        Assert.False((hit with { Triangle = triangle with { Normal = -triangle.Normal } }).ProvesPlanarCell(a,b,c,d));
    }

    [Fact]
    public void ActualVerticesDefineThePlaneRatherThanAnApproximateCollisionNormal()
    {
        var triangle = Triangle(new(-4,0,-4),new(4,0,-4),new(0,0,4))
            with { Normal = Vector3.Normalize(new(.05f,1,0)) };
        Assert.True(triangle.Valid);
        Assert.True(triangle.TryHeight(new(1,0),out var height)); Assert.Equal(0,height);
        Assert.True(new LayerFloorHit(Vector3.Zero,triangle).ProvesPlanarCell(new(-.2f,0,-.2f),new(.2f,0,-.2f),new(-.2f,0,.2f),new(.2f,0,.2f)));
    }

    [Theory]
    [InlineData(30)] [InlineData(60)] [InlineData(120)]
    public void RollingWindowRetainsRayBudgetsAndCoverageOnConnectedSlope(int fps)
    {
        var queries = new SlopeQueries(); var cache = new RollingClothSupport();
        var identity = new SupportQueryIdentity(1, 1, 0, new(0, .18f, 0), .35f, 1.35f);
        var missing = 0;
        for (var i = 0; i < fps * 6; i++)
        {
            var now = i / (double)fps; var center = new Vector2((float)Math.Max(0, now - 3) * 6, 0);
            queries.FrameRays = 0;
            var status = cache.Update(identity, center, now, 97, queries, () => queries.FrameRays < 80);
            Assert.InRange(queries.FrameRays, 0, 81); // one two-ray vertex may cross the injected deadline
            Assert.Equal(queries.FrameRays, status.Rays);
            if (i >= fps * 3 && !cache.TrySnapshot(center, now, out _)) missing++;
        }
        Assert.Equal(0, missing);
    }

    private sealed class SlopeQueries : IClothSupportQueries
    {
        // A deliberately huge actual collision triangle keeps discovery out
        // of this test: it tests the policy/cache budget seam, not native mesh
        // discovery, compressed walls, elapsed CPU time or global connectivity.
        private readonly LayerTriangle plane = Triangle(new(-200, -40, -200), new(200, 40, -200), new(0, 0, 200));
        private readonly LocalFloorLayer layer = new();
        public int FrameRays;
        public SlopeQueries() => Assert.True(layer.Seed(new(Vector3.Zero, plane)));
        public bool TryVertex(SupportQueryIdentity identity, Vector2 nominal, out Vector3 contact)
        {
            FrameRays += 2; contact = new(nominal.X, nominal.X * .2f, nominal.Y);
            return layer.TryProbe(nominal, out var probe) && layer.Accept(probe, new(contact, plane));
        }
        public bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        {
            FrameRays++; var point = (a + b + c + d) / 4; ceiling = point.Y;
            return layer.TryProbe(new(point.X, point.Z), out var probe) && layer.Accept(probe, new(point, plane));
        }
    }
}
