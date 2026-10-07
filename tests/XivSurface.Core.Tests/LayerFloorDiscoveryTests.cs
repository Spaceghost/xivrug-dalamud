using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class LayerFloorDiscoveryTests
{
    private static LayerTriangle Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (normal.Y < 0) normal = -normal;
        return new(a, b, c, normal);
    }
    private static LayerTriangle[] Strip(float x, float width, float z, float depth, float slope = .2f, float height = 0) =>
    [Triangle(new(x, x * slope + height, z), new(x + width, (x + width) * slope + height, z), new(x, x * slope + height, z + depth)),
     Triangle(new(x + width, (x + width) * slope + height, z), new(x + width, (x + width) * slope + height, z + depth), new(x, x * slope + height, z + depth))];

    [Fact]
    public void SmallRealTriangleTopologyIsDiscoveredIncrementallyWithinEveryCallAllowance()
    {
        var scene = new Scene();
        for (var i = -10; i < 20; i++) scene.Triangles.AddRange(Strip(i * .1f, .1f, -1, 2));
        var discovery = Seed(scene, new(-.05f, -.01f, 0));
        var result = LayerQueryResult.Pending; var frames = 0; var target = new Vector2(.8f, 0);
        while (result != LayerQueryResult.Success && frames++ < 80)
        {
            scene.ResetBudget(3);
            result = discovery.Query(target, scene, out var hit);
            Assert.InRange(scene.Calls, 0, 3);
            if (result == LayerQueryResult.Success) Assert.Equal(.16f, hit.Position.Y, 4);
        }
        Assert.Equal(LayerQueryResult.Success, result); Assert.True(frames > 1);
        Assert.InRange(discovery.Layer.Count, 8, 50);
    }

    [Fact]
    public void NativeStyleWallHitsProvideBothRealCurbFacesOverSeveralUpdates()
    {
        var scene = new Scene();
        scene.Triangles.AddRange(Strip(-1, 1, -1, 2, 0));
        scene.Triangles.AddRange(Strip(0, 1, -1, 2, 0, .2f));
        var risers = new[] {
            Triangle(new(0,0,-1), new(0,0,1), new(0,.2f,1)),
            Triangle(new(0,0,-1), new(0,.2f,1), new(0,.2f,-1)) };
        scene.Triangles.AddRange(risers);
        var discovery = Seed(scene, new(-.1f, 0, 0));
        var result = LayerQueryResult.Pending;
        for (var i = 0; i < 30 && result != LayerQueryResult.Success; i++)
        {
            scene.ResetBudget(1); result = discovery.Query(new(.1f, 0), scene, out var hit);
            Assert.InRange(scene.Calls, 0, 1);
            if (result == LayerQueryResult.Success) Assert.Equal(.2f, hit.Position.Y, 4);
        }
        Assert.Equal(LayerQueryResult.Success, result);
        Assert.All(risers, face => Assert.True(discovery.Layer.IsVerifiedCurb(face)));
    }

    [Fact]
    public void NearbyFloatingDeckIsSkippedWithoutEverJoiningTheGroundLayer()
    {
        var scene = new Scene();
        scene.Triangles.AddRange(Strip(-2, 4, -2, 4, 0));
        var deck = Strip(-1, 2, -1, 2, 0, .2f); scene.Triangles.AddRange(deck);
        // Seed below the deck, equivalent to the existing player-near-feet ray.
        var discovery = new LayerFloorDiscovery();
        var ground = scene.Triangles.First(t => t.Walkable && t.Contains(Vector3.Zero));
        Assert.True(discovery.Seed(new(Vector3.Zero, ground)));
        scene.ResetBudget(8);
        Assert.Equal(LayerQueryResult.Success, discovery.Query(new(.1f, .1f), scene, out var hit));
        Assert.Equal(0, hit.Position.Y); Assert.InRange(scene.Calls, 2, 8);
        Assert.All(deck, face => Assert.False(discovery.Layer.Contains(face)));
    }

    [Fact]
    public void ARealGapDoesNotBecomeAConnectedBridgeEvenWithUnlimitedRetries()
    {
        var scene = new Scene(); scene.Triangles.AddRange(Strip(-1, 1, -1, 2, 0));
        scene.Triangles.AddRange(Strip(.02f, 1, -1, 2, 0));
        var discovery = Seed(scene, new(-.1f, 0, 0));
        for (var i = 0; i < 20; i++)
        {
            scene.ResetBudget(8);
            Assert.NotEqual(LayerQueryResult.Success, discovery.Query(new(.1f, 0), scene, out _));
            Assert.InRange(scene.Calls, 0, 8);
        }
    }

    [Fact]
    public void ActualIncrementalQueryLedgerKeepsMovingFineTriangleSlopeCovered()
    {
        var scene = new Scene();
        for (var i = -30; i < 130; i++) scene.Triangles.AddRange(Strip(i * .2f, .2f, -4, 8));
        var discovery = Seed(scene, Vector3.Zero);
        var query = new IncrementalQueries(scene, discovery);
        var cache = new RollingClothSupport();
        var identity = new SupportQueryIdentity(1, 1, 0, new(0,.18f,0), .35f,1.35f);
        var missing = 0; var resets = 0;
        for (var i = 0; i < 180; i++)
        {
            var now = i / 30d; var x = (float)Math.Max(0, now - 3) * 6; var center = new Vector2(x, 0);
            scene.ResetBudget(80);
            Assert.True(scene.TryFloor(new(center, x * .2f + .12f, 1.12f, x * .2f - 1, x * .2f + .12f), out var seed));
            if (!discovery.BeginFrame(seed, now, center, 4.1f)) { cache.Reset(); resets++; }
            Assert.True(discovery.Refresh(scene));
            var before = scene.Calls;
            var status = cache.Update(identity, center, now, 100 - scene.Calls, query, () => scene.Calls < 80);
            Assert.InRange(scene.Calls, 0, 80); Assert.Equal(scene.Calls - before, status.Rays);
            if (i >= 90 && !cache.TrySnapshot(center, now, out _)) missing++;
        }
        Assert.Equal(0, missing); Assert.Equal(0, resets);
    }

    [Fact]
    public void ExpiredConnectionEvidenceCannotRemainAValidLayerForever()
    {
        var scene = new Scene(); scene.Triangles.AddRange(Strip(-1, 2, -1, 2, 0));
        var discovery = Seed(scene, new(-.5f, 0, -.5f)); scene.ResetBudget(8);
        Assert.Equal(LayerQueryResult.Success, discovery.Query(new(.5f,.5f),scene,out _));
        var seed = new LayerFloorHit(new(-.5f, 0, -.5f), scene.Triangles[0]);
        Assert.False(discovery.BeginFrame(seed, 3));
        Assert.Equal(1, discovery.Layer.Count);
    }

    [Fact]
    public void AbandonedMovingCoordinatesRetireWithoutResettingTheActualLayer()
    {
        var scene = new Scene(); scene.Triangles.AddRange(Strip(-4,8,-4,8,0));
        var discovery = Seed(scene,Vector3.Zero);
        var seed = new LayerFloorHit(Vector3.Zero,scene.Triangles[0]);
        for (var frame = 0; frame < 900; frame++)
        {
            var now = frame/100d;
            Assert.True(discovery.BeginFrame(seed,now));
            scene.ResetBudget(0);
            Assert.Equal(LayerQueryResult.Pending,discovery.Query(new(frame*.001f,0),scene,out _));
            Assert.InRange(discovery.Pending,1,202);
            Assert.Equal(0,scene.Calls);
        }
        Assert.True(discovery.BeginFrame(seed,9.01)); scene.ResetBudget(2);
        Assert.Equal(LayerQueryResult.Success,discovery.Query(new(.5f,-.5f),scene,out _));
        Assert.InRange(scene.Calls,1,2); Assert.Equal(1,discovery.Layer.Count);
    }

    [Fact]
    public void CapacityRecoversAndRepeatedActiveDeadlineRetriesAreNotRetired()
    {
        var scene = new Scene(); scene.Triangles.AddRange(Strip(-4,8,-4,8,0));
        var discovery = Seed(scene,Vector3.Zero);
        var seed = new LayerFloorHit(Vector3.Zero,scene.Triangles[0]);
        var active = new Vector2(-.5f,-.5f);
        scene.ResetBudget(0);
        Assert.Equal(LayerQueryResult.Pending,discovery.Query(active,scene,out _));
        for (var i = 0; i < 511; i++)
            Assert.Equal(LayerQueryResult.Pending,discovery.Query(new(i*.001f,0),scene,out _));
        Assert.Equal(512,discovery.Pending);
        Assert.Equal(LayerQueryResult.Unknown,discovery.Query(new(1,-1),scene,out _));
        Assert.True(discovery.BeginFrame(seed,1.5));
        Assert.Equal(LayerQueryResult.Pending,discovery.Query(active,scene,out _));
        Assert.True(discovery.BeginFrame(seed,2)); Assert.Equal(512,discovery.Pending); // exact cutoff retained
        Assert.True(discovery.BeginFrame(seed,2.01)); Assert.Equal(1,discovery.Pending);
        Assert.Equal(LayerQueryResult.Pending,discovery.Query(active,scene,out _));
        scene.ResetBudget(2);
        Assert.Equal(LayerQueryResult.Success,discovery.Query(new(1,-1),scene,out _));
        Assert.Equal(1,discovery.Pending); Assert.Equal(1,scene.Calls);
        Assert.True(discovery.BeginFrame(seed,3.6)); // active last touched 2.01
        scene.ResetBudget(2);
        Assert.Equal(LayerQueryResult.Success,discovery.Query(active,scene,out _));
        Assert.Equal(0,discovery.Pending);
    }

    private sealed class IncrementalQueries(Scene scene, LayerFloorDiscovery layer) : IClothSupportQueries, IIncrementalClothSupportQueries
    {
        public int Raycasts => scene.Calls;
        public LayerQueryResult LastResult { get; private set; }
        public void PrepareQuery(int rayAllowance) => scene.Limit = Math.Min(scene.AbsoluteLimit, scene.Calls + rayAllowance);
        public bool TryVertex(SupportQueryIdentity identity, Vector2 nominal, out Vector3 contact)
        {
            contact = default; LastResult = layer.Query(nominal, scene, out var hit);
            if (LastResult != LayerQueryResult.Success) return false;
            if (!scene.CanQuery) { LastResult = LayerQueryResult.Pending; return false; }
            // Original wall-compression ray remains real, not assumed absent.
            Assert.False(scene.TryWall(identity.CompressionOrigin, hit.Position + Vector3.UnitY*.18f, out _));
            contact = hit.Position; return true;
        }
        public bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        {
            var at = (a + b + c + d) / 4; LastResult = layer.Query(new(at.X, at.Z),scene,out var hit);
            ceiling = hit.Position.Y; return LastResult == LayerQueryResult.Success;
        }
    }

    private static LayerFloorDiscovery Seed(Scene scene, Vector3 point)
    {
        var triangle = scene.Triangles.First(t => t.Walkable && t.Contains(point));
        var discovery = new LayerFloorDiscovery(); Assert.True(discovery.Seed(new(point, triangle))); return discovery;
    }

    private sealed class Scene : ILayerFloorScene
    {
        public readonly List<LayerTriangle> Triangles = [];
        public int Calls, Limit, AbsoluteLimit;
        public bool CanQuery => Calls < Math.Min(Limit, AbsoluteLimit);
        public void ResetBudget(int limit) { Calls = 0; Limit = AbsoluteLimit = limit; }
        public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit hit)
        {
            Assert.True(CanQuery); Calls++; hit = default; var highest = float.NegativeInfinity;
            foreach (var triangle in Triangles)
            {
                if (!triangle.TryHeight(probe.Position, out var height) || height > probe.StartY + .0001f || height < probe.MinimumY
                    || height <= highest || !triangle.Contains(new(probe.Position.X, height, probe.Position.Y))) continue;
                highest = height; hit = new(new(probe.Position.X, height, probe.Position.Y), triangle);
            }
            return hit.Valid;
        }
        public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle hit)
        {
            Assert.True(CanQuery); Calls++; hit = default; var nearest = float.PositiveInfinity;
            foreach (var triangle in Triangles)
            {
                var direction = to - from; var ab = triangle.B - triangle.A; var ac = triangle.C - triangle.A;
                var p = Vector3.Cross(direction, ac); var determinant = Vector3.Dot(ab, p);
                if (Math.Abs(determinant) < 1e-7f) continue;
                var inverse = 1 / determinant; var delta = from - triangle.A;
                var u = Vector3.Dot(delta, p) * inverse; if (u < 0 || u > 1) continue;
                var q = Vector3.Cross(delta, ab); var v = Vector3.Dot(direction, q) * inverse;
                if (v < 0 || u + v > 1) continue;
                var t = Vector3.Dot(ac, q) * inverse;
                if (t < 0 || t > 1 || t >= nearest) continue;
                nearest = t; hit = triangle;
            }
            return hit.Valid;
        }
    }
}
