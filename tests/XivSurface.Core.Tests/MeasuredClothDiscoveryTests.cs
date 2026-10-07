using System.Numerics;

namespace XivSurface.Core.Tests;

/// <summary>Managed discovery integration only. Synthetic downward triangle
/// hits are measured here; success never asserts native elevated clearance.</summary>
public sealed class MeasuredClothDiscoveryTests
{
    private static LayerTriangle Face(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (normal.Y < 0) normal = -normal;
        return new(a, b, c, normal);
    }

    private static readonly LayerTriangle Floor = Face(new(-1, 0, -1), new(0, 0, -1), new(0, 0, 1));
    private static readonly LayerTriangle BevelA = Face(new(0, 0, -1), new(.1f, .3f, -1), new(0, 0, 1));
    private static readonly LayerTriangle BevelB = Face(new(.1f, .3f, -1), new(.1f, .3f, 1), new(0, 0, 1));
    private static readonly LayerTriangle Upper = Face(new(.1f, .3f, -1), new(1, .3f, -1), new(.1f, .3f, 1));
    private static readonly LayerFloorHit Root = new(new(-.3f, 0, 0), Floor);
    private static readonly Vector3 Destination = new(.2f, .3f, 0);

    public enum FloorBehavior { Hit, Miss, Defer, UncountedMiss, ChangedFace }

    private sealed class Scene : ILayerMeasuredClothScene, ILayerFloorQueryAttempts
    {
        public readonly List<LayerTriangle> Faces = [Floor, BevelA, BevelB, Upper];
        public readonly List<LayerTriangle> Walls = [];
        public readonly List<ClothFloorProbe> Probes = [];
        public readonly List<(Vector3 From, Vector3 To)> ClearanceCalls = [];
        public int Raycasts { get; private set; }
        public int MeasuredCalls { get; private set; }
        public int OrdinaryCalls { get; private set; }
        public int WallCalls { get; private set; }
        public int Limit { get; private set; }
        public bool CanQuery => Raycasts < Limit;
        public FloorBehavior Behavior;
        public bool ExhaustOnMiss = true;

        public void Reset(int limit, FloorBehavior behavior = FloorBehavior.Hit)
        { Raycasts = 0; Limit = limit; Behavior = behavior; }

        public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit hit)
        {
            OrdinaryCalls++;
            return Read(probe, out hit) && ClothFloorQueryPolicy.Accept(probe, hit.Position, hit.Triangle.Normal, out _)
                && hit.Valid;
        }

        public bool TryMeasuredClothFloor(ClothFloorProbe probe, out LayerFloorHit hit)
        {
            MeasuredCalls++;
            return Read(probe, out hit) && ClothFloorQueryPolicy.AcceptMeasuredCloth(probe, hit, out _);
        }

        private bool Read(ClothFloorProbe probe, out LayerFloorHit hit)
        {
            Assert.True(CanQuery); Assert.True(probe.Valid);
            Probes.Add(probe); hit = default;
            if (Behavior == FloorBehavior.Defer) { Limit = Raycasts; return false; }
            if (Behavior == FloorBehavior.UncountedMiss) return false;
            Raycasts++;
            if (Behavior == FloorBehavior.Miss)
            { if (ExhaustOnMiss) Limit = Raycasts; return false; }
            var nearest = float.PositiveInfinity;
            var start = new Vector3(probe.Position.X, probe.StartY, probe.Position.Y);
            foreach (var face in Faces)
            {
                // Independent finite 3-D ray/triangle intersection: unlike
                // LayerTriangle.TryHeight this does not hide steep faces.
                if (!Intersect(face, start, -Vector3.UnitY, probe.Length, out var distance) || distance >= nearest) continue;
                nearest = distance; hit = new(start - Vector3.UnitY * distance, face);
            }
            if (!float.IsFinite(nearest)) return false;
            if (Behavior == FloorBehavior.ChangedFace)
                hit = hit with { Triangle = Face(hit.Triangle.A, hit.Triangle.B,
                    hit.Triangle.C + new Vector3(0, 0, -.001f)) };
            return true;
        }

        public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle)
        {
            Assert.True(CanQuery); WallCalls++; Raycasts++; triangle = default;
            var length = Vector3.Distance(from, to); Assert.True(length > 0);
            var direction = (to - from) / length; var nearest = float.PositiveInfinity;
            foreach (var face in Walls)
                if (Intersect(face, from, direction, length, out var distance) && distance < nearest)
                { nearest = distance; triangle = face; }
            // The ordinary bevel fixture has no separately measured wall
            // faces: a missed wall cannot invent a vertical curb shortcut.
            return float.IsFinite(nearest);
        }

        public ClothClearanceCast CastClearance(Vector3 from, Vector3 to)
        {
            if (!CanQuery) return new(ClothClearanceResult.Pending);
            Raycasts++; ClearanceCalls.Add((from, to));
            var length = Vector3.Distance(from, to); Assert.True(length > 0);
            var direction = (to - from) / length;
            var nearest = float.PositiveInfinity; var isWall = false;
            foreach (var face in Faces)
                if (Intersect(face, from, direction, length, out var distance) && distance < nearest)
                { nearest = distance; isWall = false; }
            foreach (var face in Walls)
                if (Intersect(face, from, direction, length, out var distance) && distance < nearest)
                { nearest = distance; isWall = true; }
            if (!float.IsFinite(nearest)) return new(ClothClearanceResult.Clear);
            // Floor connectivity does not clear an intersected segment. These
            // actual finite bevels never use the vertical-curb skip exception.
            return isWall ? new(ClothClearanceResult.Blocked, from + direction * nearest)
                : new(ClothClearanceResult.Unknown);
        }

        private static bool Intersect(LayerTriangle face, Vector3 start, Vector3 direction,
            float maximumDistance, out float distance)
        {
            distance = 0;
            var ab = face.B - face.A; var ac = face.C - face.A;
            var p = Vector3.Cross(direction, ac); var determinant = Vector3.Dot(ab, p);
            if (Math.Abs(determinant) < 1e-8f) return false;
            var inverse = 1 / determinant; var delta = start - face.A;
            var u = Vector3.Dot(delta, p) * inverse; if (u < -1e-6f || u > 1.000001f) return false;
            var q = Vector3.Cross(delta, ab); var v = Vector3.Dot(direction, q) * inverse;
            if (v < -1e-6f || u + v > 1.000001f) return false;
            distance = Vector3.Dot(ac, q) * inverse;
            return distance >= 0 && distance <= maximumDistance;
        }
    }

    private sealed class Legacy(Scene scene) : ILayerFloorScene, ILayerFloorQueryAttempts
    {
        public bool CanQuery => scene.CanQuery;
        public int Raycasts => scene.Raycasts;
        public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit hit) => scene.TryFloor(probe, out hit);
        public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle) => scene.TryWall(from, to, out triangle);
    }

    private sealed class WithoutReceipt(Scene scene) : ILayerMeasuredClothScene
    {
        public bool CanQuery => scene.CanQuery;
        public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit hit) => scene.TryFloor(probe, out hit);
        public bool TryMeasuredClothFloor(ClothFloorProbe probe, out LayerFloorHit hit) => scene.TryMeasuredClothFloor(probe, out hit);
        public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle) => scene.TryWall(from, to, out triangle);
    }

    private static LayerFloorDiscovery New(LayerSupportScope scope = LayerSupportScope.MeasuredCloth)
    {
        var discovery = new LayerFloorDiscovery(scope);
        Assert.True(discovery.Seed(Root)); Assert.True(discovery.BeginFrame(Root, 0));
        return discovery;
    }

    private static LayerFloorHit Discover(LayerFloorDiscovery discovery, Scene scene, Vector2 wanted)
    {
        for (var update = 0; update < 128; update++)
        {
            scene.Reset(1);
            var status = discovery.Query(wanted, scene, out var hit);
            Assert.InRange(scene.Raycasts, 0, 1);
            Assert.NotEqual(LayerQueryResult.Unknown, status);
            if (status == LayerQueryResult.Success) return hit;
        }
        Assert.Fail("One-ray discovery never completed within its bounded test allowance.");
        return default;
    }

    private static LayerFloorDiscovery Complete(out Scene scene)
    {
        var discovery = New(); scene = new();
        Discover(discovery, scene, new(.025f, 0));
        Discover(discovery, scene, new(.075f, 0));
        Discover(discovery, scene, new(.2f, 0));
        return discovery;
    }

    [Fact]
    public void OneRayUpdatesDiscoverActualBevelAndCompleteEveryPathAndCellFace()
    {
        var discovery = New(); var scene = new Scene();
        var result = Discover(discovery, scene, new(Destination.X, Destination.Z));
        Assert.InRange(Vector3.Distance(result.Position, Destination), 0, 1e-6f);
        Assert.Equal(4, discovery.Layer.Count);
        Assert.All(scene.Faces, face => Assert.True(discovery.Layer.Contains(face)));
        Assert.True(scene.MeasuredCalls > 1); Assert.Equal(0, scene.OrdinaryCalls);
        Assert.Equal(LayerQueryResult.Success, discovery.Layer.TrySurfacePath(Root.Position, Destination, out var path));
        Assert.Contains(path, p => Math.Abs(p.X - .05f) < 1e-6f && Math.Abs(p.Y - .15f) < 1e-6f);
        Assert.True(path.Length >= 5);
        var center = Discover(discovery, scene, new(.05f, 0));
        Assert.Equal(LayerQueryResult.Success, discovery.Layer.TryCellCeiling(center,
            new(-.05f, 0, -.25f), new(.15f, .3f, -.25f), new(-.05f, 0, .25f), new(.15f, .3f, .25f), out var ceiling));
        Assert.InRange(ceiling, .3f, .30001f);
        Assert.False(discovery.Layer.IsVerifiedCurb(BevelA)); Assert.False(discovery.Layer.IsVerifiedCurb(BevelB));
        // Elevated native segment queries are deliberately outside this test.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingMeasuredConnectorNeverBecomesFullPathOrCell(bool keepFirst)
    {
        var discovery = New(); var scene = new Scene(); scene.Faces.Remove(BevelB);
        if (!keepFirst) scene.Faces.Remove(BevelA);
        for (var update = 0; update < 24; update++)
        {
            scene.Reset(1);
            Assert.NotEqual(LayerQueryResult.Success, discovery.Query(new(.2f, 0), scene, out _));
            Assert.InRange(scene.Raycasts, 0, 1);
        }
        Assert.Equal(LayerQueryResult.Unknown, discovery.Layer.TrySurfacePath(Root.Position, Destination, out var path));
        Assert.Empty(path);
        Assert.Equal(LayerQueryResult.Unknown, discovery.Layer.TryCellCeiling(new(new(-.025f, 0, 0), Floor),
            new(-.05f, 0, -.25f), new(.15f, .3f, -.25f), new(-.05f, 0, .25f), new(.15f, .3f, .25f), out _));
    }

    [Fact]
    public void DefaultDiscoveryAndLegacyAdapterDoNotOptIntoMeasuredSteepFaces()
    {
        Assert.NotNull(typeof(LayerFloorDiscovery).GetConstructor(Type.EmptyTypes));
        foreach (var scope in new[] { LayerSupportScope.WalkableOnly, LayerSupportScope.MeasuredCloth })
        {
            var discovery = New(scope); var scene = new Scene();
            ILayerFloorScene provider = scope == LayerSupportScope.WalkableOnly ? scene : new Legacy(scene);
            for (var update = 0; update < 8; update++)
            {
                scene.Reset(1);
                Assert.NotEqual(LayerQueryResult.Success, discovery.Query(new(.025f, 0), provider, out _));
            }
            Assert.Equal(0, scene.MeasuredCalls); Assert.True(scene.OrdinaryCalls > 0);
            Assert.False(discovery.Layer.Contains(BevelA));
        }
    }

    [Fact]
    public void RawCandidatePolicyDoesNotChangePlayerSeedOrNearFeetWalkability()
    {
        var hit = new LayerFloorHit(new(.025f, .075f, 0), BevelA);
        Assert.True(ClothFloorQueryPolicy.TryPlayerProbe(hit.Position, out var probe));
        Assert.True(ClothFloorQueryPolicy.AcceptMeasuredCloth(probe, hit, out _));
        Assert.False(ClothFloorQueryPolicy.Accept(probe, hit.Position, hit.Triangle.Normal, out _));
        Assert.False(hit.Valid);
        var discovery = New(); Assert.False(discovery.Seed(hit)); Assert.Equal(0, discovery.Layer.Count);
        Assert.False(discovery.BeginFrame(hit, .01));
        var shifted = hit with { Position = hit.Position + Vector3.UnitX * .03f };
        Assert.False(ClothFloorQueryPolicy.AcceptMeasuredCloth(probe, shifted, out _));
        Assert.False(ClothFloorQueryPolicy.AcceptMeasuredCloth(probe, hit with { Position = hit.Position + Vector3.UnitY }, out _));
    }

    [Fact]
    public void DirectSteepHitUsesNoRiserProbeAndDoesNotClaimVerticalCurbRole()
    {
        var discovery = New(); var scene = new Scene();
        var hit = Discover(discovery, scene, new(.025f, 0));
        Assert.Equal(BevelA, hit.Triangle); Assert.False(hit.Valid);
        Assert.Equal(0, scene.WallCalls); Assert.Equal(1, scene.MeasuredCalls);
        Assert.False(discovery.Layer.IsVerifiedCurb(BevelA));
    }

    [Fact]
    public void FloatingDeckRetryNarrowsRayAndStillAdmitsActualUnderlyingConnector()
    {
        var discovery = New(); var scene = new Scene();
        var deck = Face(new(-.1f, .22f, -.5f), new(.3f, .22f, -.5f), new(-.1f, .22f, .5f));
        scene.Faces.Add(deck);
        var result = Discover(discovery, scene, new(.025f, 0));
        Assert.Equal(BevelA, result.Triangle); Assert.False(discovery.Layer.Contains(deck));
        Assert.True(scene.Probes.Count >= 2);
        var first = scene.Probes[0]; var narrowed = scene.Probes.First(p => p.StartY < first.StartY);
        Assert.Equal(first.MinimumY, narrowed.MinimumY);
        Assert.InRange(narrowed.MaximumY, first.MinimumY, first.MaximumY);
        Assert.InRange(narrowed.StartY - narrowed.Length, first.StartY - first.Length - 1e-6f,
            first.StartY - first.Length + 1e-6f);
        Assert.True(narrowed.StartY < deck.A.Y);
    }

    private static LayerFloorDiscovery Refreshable(out Scene scene)
    {
        var discovery = New(); scene = new(); Discover(discovery, scene, new(.025f, 0));
        Assert.True(discovery.BeginFrame(Root, 1.2));
        Assert.True(discovery.Layer.TryRefresh(out var due)); Assert.Equal(BevelA, due);
        return discovery;
    }

    [Fact]
    public void SteepRefreshIsOneMeasuredDownwardRayNotAWallAndRenewsOnLastAttempt()
    {
        var discovery = Refreshable(out var scene); var walls = scene.WallCalls;
        scene.Reset(1); Assert.True(discovery.Refresh(scene, 1));
        Assert.Equal(1, scene.Raycasts); Assert.False(scene.CanQuery); Assert.Equal(walls, scene.WallCalls);
        Assert.Equal(0, scene.OrdinaryCalls); Assert.False(discovery.Layer.TryRefresh(out _));
        Assert.True(discovery.BeginFrame(Root, 2.001)); Assert.True(discovery.Layer.Contains(BevelA));
    }

    [Fact]
    public void SlantedOrdinaryCurbRefreshKeepsItsWallRoleDespiteRawConnectorEligibility()
    {
        var discovery = New();
        var risers = new[] {
            Face(new(0, 0, -1), new(.01f, .2f, -1), new(0, 0, 1)),
            Face(new(.01f, .2f, -1), new(.01f, .2f, 1), new(0, 0, 1)) };
        var upper = Face(new(.01f, .2f, -1), new(1, .2f, -1), new(.01f, .2f, 1));
        Assert.True(discovery.Layer.TryProbe(new(.2f, 0), out var probe));
        Assert.True(discovery.Layer.Accept(probe, new(new(.2f, .2f, 0), upper), risers));
        Assert.True(discovery.BeginFrame(Root, 1.2));
        Assert.True(discovery.Layer.TryRefresh(out var due)); Assert.Equal(risers[0], due);
        var centroid = (due.A + due.B + due.C) / 3;
        Assert.True(discovery.Layer.IsMeasuredSupport(new(centroid, due)));
        Assert.True(discovery.Layer.IsVerifiedCurb(due)); Assert.False(discovery.Layer.Contains(due));
        var scene = new Scene(); scene.Faces.Clear(); scene.Faces.AddRange([Floor, upper]);
        scene.Walls.AddRange(risers); scene.Reset(1);
        Assert.True(discovery.Refresh(scene, 1));
        Assert.Equal(1, scene.Raycasts); Assert.Equal(1, scene.WallCalls);
        Assert.Equal(0, scene.MeasuredCalls); Assert.Equal(0, scene.OrdinaryCalls);
        Assert.True(discovery.Layer.IsVerifiedCurb(due)); Assert.False(discovery.Layer.Contains(due));
    }

    [Theory]
    [InlineData(FloorBehavior.Miss)]
    [InlineData(FloorBehavior.ChangedFace)]
    public void AttemptedMissingOrChangedConnectorRefreshInvalidates(FloorBehavior behavior)
    {
        var discovery = Refreshable(out var scene); scene.Reset(1, behavior);
        Assert.False(discovery.Refresh(scene, 1)); Assert.Equal(1, scene.Raycasts);
        Assert.Equal(0, discovery.Layer.Count); Assert.Equal(0, scene.WallCalls);
    }

    [Fact]
    public void ReceiptProvenUnattemptedRefreshDoesNotRenewOrDestroyOriginalEvidence()
    {
        var discovery = Refreshable(out var scene); scene.Reset(1, FloorBehavior.Defer);
        Assert.True(discovery.Refresh(scene, 1)); Assert.Equal(0, scene.Raycasts);
        Assert.True(discovery.Layer.TryRefresh(out var due)); Assert.Equal(BevelA, due);
        Assert.True(discovery.Layer.Contains(BevelA));
        Assert.False(discovery.BeginFrame(Root, 2.001)); Assert.False(discovery.Layer.Contains(BevelA));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingReceiptOrStillOpenBudgetCannotTurnMissIntoDeferredRefresh(bool receipt)
    {
        var discovery = Refreshable(out var scene);
        scene.Reset(1, receipt ? FloorBehavior.UncountedMiss : FloorBehavior.Defer);
        ILayerFloorScene provider = receipt ? scene : new WithoutReceipt(scene);
        Assert.False(discovery.Refresh(provider, 1)); Assert.Equal(0, discovery.Layer.Count);
    }

    [Fact]
    public void ExpiredAttemptReceiptRetainsExactDirectProbeUntilNextUpdate()
    {
        var discovery = New(); var scene = new Scene(); scene.Reset(1, FloorBehavior.Defer);
        Assert.Equal(LayerQueryResult.Pending, discovery.Query(new(.025f, 0), scene, out _));
        Assert.Equal(0, scene.Raycasts); Assert.Equal(1, discovery.Pending);
        var unattempted = Assert.Single(scene.Probes);
        scene.Reset(1);
        Assert.Equal(LayerQueryResult.Success, discovery.Query(new(.025f, 0), scene, out var hit));
        Assert.Equal(BevelA, hit.Triangle); Assert.Equal(1, scene.Raycasts);
        Assert.Equal(unattempted, scene.Probes[1]); Assert.Equal(0, discovery.Pending);
    }

    [Fact]
    public void ActualMissConsumesItsProbeRatherThanRepeatingItAsUnattempted()
    {
        var discovery = New(); var scene = new Scene(); scene.Reset(1, FloorBehavior.Miss);
        Assert.Equal(LayerQueryResult.Pending, discovery.Query(new(.025f, 0), scene, out _));
        Assert.Equal(1, scene.Raycasts); var attempted = Assert.Single(scene.Probes);
        scene.Reset(1); discovery.Query(new(.025f, 0), scene, out _);
        Assert.Equal(1, scene.Raycasts); Assert.NotEqual(attempted, scene.Probes[1]);
    }

    [Fact]
    public void ZeroBudgetDoesNoNativeWorkAndLayerProofDeadlineReturnsNoPartialPath()
    {
        var discovery = Complete(out var scene); scene.Reset(0);
        var count = discovery.Layer.Count;
        Assert.Equal(LayerQueryResult.Pending, discovery.Query(new(.2f, 0), scene, out _));
        Assert.True(discovery.Refresh(scene, 2)); Assert.Equal(0, scene.Raycasts);
        Assert.Equal(count, discovery.Layer.Count);
        Assert.Equal(LayerQueryResult.Pending, discovery.Layer.TrySurfacePath(Root.Position, Destination, out var path, () => false));
        Assert.Empty(path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasuredBevelPathRequiresEveryLiftedSegmentAndLaterWallStillBlocks(bool wall)
    {
        var discovery = Complete(out var scene);
        Assert.Equal(LayerQueryResult.Success, discovery.Layer.TrySurfacePath(Root.Position, Destination, out var path));
        var lifted = path.Select(point => point + Vector3.UnitY * .18f).ToArray();
        Assert.True(lifted.Length >= 5); Assert.Empty(scene.ClearanceCalls);
        if (wall) scene.Walls.Add(Face(new(.15f, .25f, -1), new(.15f, 1, -1), new(.15f, .25f, 1)));
        var trace = new ClothClearanceTrace();
        scene.Reset(0);
        Assert.Equal(ClothClearanceResult.Pending, trace.Query(7, Root.Position, Destination,
            lifted, 0, scene.CastClearance, out _));
        Assert.Empty(scene.ClearanceCalls); // Graph success is not clearance.
        var verdict = ClothClearanceResult.Pending; var blockingPoint = Vector3.Zero;
        for (var segment = 0; segment < lifted.Length - 1; segment++)
        {
            scene.Reset(1);
            verdict = trace.Query(7, Root.Position, Destination, lifted, (segment + 1) * .01,
                scene.CastClearance, out blockingPoint);
            Assert.Equal(1, scene.Raycasts);
            Assert.Equal(segment + 1, scene.ClearanceCalls.Count);
            Assert.Equal((lifted[segment], lifted[segment + 1]), scene.ClearanceCalls[segment]);
            if (segment < lifted.Length - 2) Assert.Equal(ClothClearanceResult.Pending, verdict);
        }
        Assert.Equal(wall ? ClothClearanceResult.Blocked : ClothClearanceResult.Clear, verdict);
        Assert.Equal(0, trace.PendingCount);
        if (wall) Assert.InRange(Vector3.Distance(blockingPoint, new(.15f, .48f, 0)), 0, 1e-6f);
        Assert.False(discovery.Layer.IsVerifiedCurb(BevelA)); Assert.False(discovery.Layer.IsVerifiedCurb(BevelB));
    }

    [Fact]
    public void ExpiredMeasuredBevelClearancePrefixRestartsItsRealSegmentQueries()
    {
        var discovery = Complete(out var scene);
        Assert.Equal(LayerQueryResult.Success, discovery.Layer.TrySurfacePath(Root.Position, Destination, out var path));
        var lifted = path.Select(point => point + Vector3.UnitY * .18f).ToArray();
        var trace = new ClothClearanceTrace(); scene.Reset(1);
        Assert.Equal(ClothClearanceResult.Pending, trace.Query(1, Root.Position, Destination,
            lifted, 0, scene.CastClearance, out _));
        Assert.Single(scene.ClearanceCalls);
        scene.Reset(0);
        Assert.Equal(ClothClearanceResult.Pending, trace.Query(1, Root.Position, Destination,
            lifted, .6, scene.CastClearance, out _));
        Assert.Single(scene.ClearanceCalls);
        scene.Reset(1);
        Assert.Equal(ClothClearanceResult.Pending, trace.Query(1, Root.Position, Destination,
            lifted, .61, scene.CastClearance, out _));
        Assert.Equal(2, scene.ClearanceCalls.Count);
        Assert.Equal(scene.ClearanceCalls[0], scene.ClearanceCalls[1]);
        scene.Reset(lifted.Length);
        Assert.Equal(ClothClearanceResult.Clear, trace.Query(1, Root.Position, Destination,
            lifted, .62, scene.CastClearance, out _));
        Assert.Equal(lifted.Length, scene.ClearanceCalls.Count); // Whole path plus expired first segment.
    }

    [Fact]
    public void MeasuredConnectedFloorIntersectionStillRefusesRatherThanSkippingKnownBevel()
    {
        var discovery = Complete(out var scene);
        Assert.Equal(LayerQueryResult.Success, discovery.Layer.TrySurfacePath(Root.Position, Destination, out _));
        scene.Reset(1);
        var result = scene.CastClearance(new(-.02f, .075f, 0), new(.08f, .075f, 0));
        Assert.Equal(ClothClearanceResult.Unknown, result.Result);
        Assert.Single(scene.ClearanceCalls); Assert.Equal(1, scene.Raycasts);
        Assert.True(discovery.Layer.Contains(BevelA)); Assert.False(discovery.Layer.IsVerifiedCurb(BevelA));
    }
}
