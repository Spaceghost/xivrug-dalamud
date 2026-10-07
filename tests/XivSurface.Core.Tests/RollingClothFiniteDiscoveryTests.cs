using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class RollingClothFiniteDiscoveryTests
{
    private static readonly SupportQueryIdentity Origin = new(1, 1, 1, new(-.1f, .18f, 0), .35f, 1.35f);
    private static readonly Vector2 Center = new(.3f, 0);
    private static LayerTriangle Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (normal.Y < 0) normal = -normal;
        return new(a, b, c, normal);
    }
    private static readonly LayerTriangle Lower = Triangle(new(-1, 0, -2), new(0, 0, -2), new(0, 0, 2));
    private static readonly LayerTriangle Upper = Triangle(new(0, .2f, -2), new(1, .2f, 2), new(0, .2f, 2));
    private static readonly LayerTriangle RiserLow = Triangle(new(0, 0, -2), new(0, 0, 2), new(0, .2f, 2));
    private static readonly LayerTriangle RiserHigh = Triangle(new(0, 0, -2), new(0, .2f, 2), new(0, .2f, -2));

    // The actual production discovery and rolling cache, with deterministic
    // native ray results. Each framework update permits exactly ONE real ray.
    // The raised face is inadmissible until both finite risers were measured.
    private sealed class Terrain : IClothSupportQueries, IIncrementalClothSupportQueries,
        ILayerFloorScene, ILayerFloorQueryAttempts
    {
        public readonly LayerFloorDiscovery Discovery = new();
        public int Raycasts { get; private set; }
        public int FloorCalls, WallCalls, VertexSuccesses, CellSuccesses;
        public int FrameBudget = 1;
        private int allowance;
        public bool CanQuery => Raycasts < FrameBudget && Raycasts < allowance;
        public LayerQueryResult LastResult { get; private set; }
        public bool Deadline() => Raycasts < FrameBudget;
        public void Begin(double now)
        {
            Raycasts = 0; allowance = FrameBudget;
            Discovery.BeginFrame(new(new(-.1f, 0, 0), Lower), now, Center, 5);
        }
        public void PrepareQuery(int rayAllowance) => allowance = Raycasts + rayAllowance;
        public bool TryVertex(SupportQueryIdentity _, Vector2 nominal, out Vector3 contact)
        {
            LastResult = Discovery.Query(nominal, this, out var hit); contact = hit.Position;
            if (LastResult == LayerQueryResult.Success) VertexSuccesses++;
            return LastResult == LayerQueryResult.Success;
        }
        public bool TryCell(SupportQueryIdentity _, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        {
            var at = (a + b + c + d) / 4;
            LastResult = Discovery.Query(new(at.X, at.Z), this, out var hit);
            ceiling = hit.Position.Y;
            if (LastResult == LayerQueryResult.Success) CellSuccesses++;
            return LastResult == LayerQueryResult.Success;
        }
        public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit hit)
        {
            hit = default; if (!CanQuery) return false;
            Raycasts++; FloorCalls++;
            var triangle = probe.Position.X > 0 ? Upper : Lower;
            var at = new Vector3(probe.Position.X, triangle.A.Y, probe.Position.Y);
            if (!ClothFloorQueryPolicy.Accept(probe, at, triangle.Normal, out _)) return false;
            hit = new(at, triangle); return hit.Valid;
        }
        public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle)
        {
            triangle = default; if (!CanQuery) return false;
            Raycasts++; WallCalls++;
            if (from.X >= 0 || to.X <= 0 || from.Y is <= 0 or >= .2f || Math.Abs(from.Z) >= 2) return false;
            var diagonalY = .2f * (from.Z + 2) / 4;
            triangle = from.Y <= diagonalY ? RiserLow : RiserHigh;
            return true;
        }
    }

    [Theory]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(60)]
    public void BoundedContinuationCompletesRealFloorAndTwoRiserDiscoveryBeforeEvidenceExpires(int framesPerSecond)
    {
        var cache = new RollingClothSupport(.1f, .1f, 0); var scene = new Terrain();
        RollingSupportSnapshot? snapshot = null;
        for (var frame = 0; frame < 40; frame++)
        {
            var now = frame / (double)framesPerSecond;
            scene.Begin(now);
            cache.Update(Origin, Center, now, 100, scene, scene.Deadline);
            Assert.InRange(scene.Raycasts, 0, 1);
            if (cache.TrySnapshot(Center, now, out snapshot)) break;
        }
        Assert.True(scene.VertexSuccesses >= 9,
            "Finite floor + two-riser discovery must finish inside its original100ms candidate lifetime.");
        Assert.True(scene.WallCalls >= 2);
        Assert.NotNull(snapshot);
        Assert.Equal(new Vector2(.1f), snapshot.Half);
        Assert.All(snapshot.Contacts, contact => Assert.Equal(.2f, contact.Y));
    }

    [Fact]
    public void ControlThreeConsecutiveCallsCompleteSameGeometryWithoutWideningItsNativeQueryBands()
    {
        var scene = new Terrain();
        for (var frame = 0; frame < 3; frame++)
        {
            scene.Begin(frame / 30d);
            var status = scene.Discovery.Query(Center, scene, out var hit);
            Assert.Equal(frame == 2 ? LayerQueryResult.Success : LayerQueryResult.Pending, status);
            if (frame == 2) Assert.Equal(.2f, hit.Position.Y);
        }
        Assert.Equal(1, scene.FloorCalls); Assert.Equal(2, scene.WallCalls);
        Assert.Equal(2, scene.Discovery.Layer.Count);
    }

    [Fact]
    public void ReadyCellRefreshDoesNotInterruptShortActualRiserContinuation()
    {
        var cache = new RollingClothSupport(.1f, .2f, 0);
        var scene = new Terrain { FrameBudget = 1000 };
        var oldCenter = new Vector2(-.2f, 0); var newCenter = new Vector2(-.1f, 0);
        scene.Begin(0);
        cache.Update(Origin, oldCenter, 0, 100, scene, scene.Deadline);
        Assert.True(cache.TrySnapshot(oldCenter, 0, out _));
        Assert.Equal(0, scene.WallCalls); Assert.Equal(1, scene.Discovery.Layer.Count);

        // Old cells are still valid but due for refresh. The new right-hand
        // column needs actual floor+riser+riser. Each consumes its full frame.
        scene.FrameBudget = 1;
        for (var frame = 0; frame < 3; frame++)
        {
            var now = 1.2 + frame / 30d;
            scene.Begin(now);
            cache.Update(Origin, newCenter, now, 100, scene, scene.Deadline);
            Assert.Equal(1, scene.Raycasts);
        }
        Assert.Equal(2, scene.WallCalls);
        Assert.Equal(2, scene.Discovery.Layer.Count);
        Assert.True(scene.VertexSuccesses > 25);
    }
}
