using System.Numerics;
namespace XivSurface.Core.Tests;

public sealed class RollingClothCellContinuationTests(ITestOutputHelper output)
{
    private static readonly SupportQueryIdentity Origin = new(1, 1, 1, new(-.01f, .18f, .02f), .35f, 1.35f);
    private static readonly Vector3 Player = new(-.01f, 0, .02f);
    private static readonly Vector2[] Centers = [new(-.2f, -.2f), new(.2f, -.2f), new(-.2f, .2f), new(.2f, .2f)];
    private static LayerTriangle Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (normal.Y < 0) normal = -normal;
        return new(a, b, c, normal);
    }

    // Four finite raised square treads, each inside a distinct cell. The nine
    // measured cell corners lie on the connected lower grid outside the treads.
    // Lower triangles leave actual holes: no flat floor overlaps a tread.
    private sealed class Terrain : IClothSupportQueries, IIncrementalClothSupportQueries,
        ILayerFloorScene, ILayerFloorQueryAttempts
    {
        public readonly LayerFloorDiscovery Discovery = new();
        public readonly List<LayerTriangle> Lower = [], Upper = [], Risers = [];
        public readonly Dictionary<Vector2, List<int>> CellFrames = [];
        private readonly LayerFloorHit playerHit;
        public int Raycasts { get; private set; }
        public int Floors, Walls, MidpointSuccesses;
        public bool ZeroAttemptVertices;
        public readonly HashSet<Vector2> AdmittedMidpoints = [];
        private int allowance;
        public int Frame { get; private set; }
        public int Capacity = 1;
        public bool WarmCornersOnly;
        public LayerQueryResult LastResult { get; private set; }
        public bool CanQuery => Raycasts < Capacity && Raycasts < allowance;
        public bool Deadline() => Raycasts < Capacity;
        public void PrepareQuery(int value) => allowance = Raycasts + value;

        public Terrain()
        {
            float[] grid = [-.4f, -.35f, -.05f, 0, .05f, .35f, .4f];
            for (var z = 0; z < grid.Length - 1; z++)
            for (var x = 0; x < grid.Length - 1; x++)
            {
                if (x is 1 or 4 && z is 1 or 4) continue;
                var a = new Vector3(grid[x], 0, grid[z]); var b = new Vector3(grid[x + 1], 0, grid[z]);
                var c = new Vector3(grid[x], 0, grid[z + 1]); var d = new Vector3(grid[x + 1], 0, grid[z + 1]);
                Lower.Add(Triangle(a, b, c)); Lower.Add(Triangle(b, d, c));
            }
            foreach (var center in Centers)
            {
                var x0 = center.X < 0 ? -.35f : .05f; var x1 = center.X < 0 ? -.05f : .35f;
                var z0 = center.Y < 0 ? -.35f : .05f; var z1 = center.Y < 0 ? -.05f : .35f;
                Vector3[] ring = [new(x0, 0, z0), new(x1, 0, z0), new(x1, 0, z1), new(x0, 0, z1)];
                var nearest = center.X > 0 ? center.Y > 0 ? 0 : 3 : center.Y > 0 ? 1 : 2;
                var a = ring[nearest] + Vector3.UnitY * .2f;
                var b = ring[(nearest + 1) % 4] + Vector3.UnitY * .2f;
                var c = ring[(nearest + 3) % 4] + Vector3.UnitY * .2f;
                var d = ring[(nearest + 2) % 4] + Vector3.UnitY * .2f;
                Upper.Add(Triangle(a, b, c)); Upper.Add(Triangle(b, d, c));
                for (var edge = 0; edge < 4; edge++)
                {
                    var p = ring[edge]; var q = ring[(edge + 1) % 4];
                    Risers.Add(Triangle(p, q, q + Vector3.UnitY * .2f));
                    Risers.Add(Triangle(p, q + Vector3.UnitY * .2f, p + Vector3.UnitY * .2f));
                }
            }
            playerHit = new(Player, Lower.First(t => t.Contains(Player)));
            Assert.True(Discovery.Seed(playerHit));
            // Fixture setup represents previously measured real lower faces.
            // Adjacent root observations record them without fabricating edges.
            var remaining = Lower.Where(t => t != playerHit.Triangle).ToList();
            while (remaining.Count > 0)
            {
                var next = remaining.FindIndex(t => Lower.Any(parent => Discovery.Layer.Contains(parent)
                    && LocalFloorLayer.SharesBoundary(parent, t)));
                Assert.True(next >= 0, "Actual lower grid must be edge-connected.");
                var face = remaining[next]; remaining.RemoveAt(next);
                Discovery.BeginFrame(new((face.A + face.B + face.C) / 3, face), 0, Vector2.Zero, 5);
            }
            Discovery.BeginFrame(playerHit, 0, Vector2.Zero, 5);
            Assert.Equal(Lower.Count, Discovery.Layer.Count);
            Assert.All(Centers, at => Assert.DoesNotContain(Lower, t => t.Contains(new(at.X, 0, at.Y))));
        }

        public void Begin(int frame)
        {
            Frame = frame; Raycasts = 0; allowance = Capacity;
            Discovery.BeginFrame(playerHit, frame / 30d, Vector2.Zero, 5);
        }
        public bool TryVertex(SupportQueryIdentity _, Vector2 at, out Vector3 contact)
        {
            // These are independently measured actual corners, not guesses at
            // the cell interior. Warm-up isolates the subsequent cell stage;
            // this fixture does not claim full native wall-route integration.
            if (ZeroAttemptVertices) { contact = default; LastResult = LayerQueryResult.Pending; return false; }
            var probe = new ClothFloorProbe(at, .35f, 1.35f, -1, .35f);
            var ok = TryFloor(probe, out var hit);
            LastResult = ok ? LayerQueryResult.Success : CanQuery ? LayerQueryResult.Unknown : LayerQueryResult.Pending;
            contact = hit.Position; return ok;
        }
        public bool TryCell(SupportQueryIdentity _, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        {
            ceiling = float.NaN;
            if (WarmCornersOnly) { LastResult = LayerQueryResult.Pending; return false; }
            var at3 = (a + b + c + d) / 4; var at = new Vector2(at3.X, at3.Z);
            if (!CellFrames.TryGetValue(at, out var frames)) CellFrames.Add(at, frames = []);
            frames.Add(Frame);
            LastResult = Discovery.Query(at, this, out var hit);
            if (LastResult != LayerQueryResult.Success) return false;
            MidpointSuccesses++; AdmittedMidpoints.Add(at);
            // Real full-cell proof remains separate; don't call an admitted
            // midpoint a successfully published cloth cell.
            LastResult = Discovery.Layer.TryCellCeiling(hit, a, b, c, d, out ceiling, Deadline);
            return LastResult == LayerQueryResult.Success;
        }
        public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit hit)
        {
            hit = default; if (!CanQuery) return false;
            Raycasts++; Floors++;
            foreach (var t in Upper.Concat(Lower))
            {
                var at = new Vector3(probe.Position.X, t.A.Y, probe.Position.Y);
                if (!t.Contains(at) || !ClothFloorQueryPolicy.Accept(probe, at, t.Normal, out _)) continue;
                hit = new(at, t); return true;
            }
            return false;
        }
        public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle)
        {
            triangle = default; if (!CanQuery) return false;
            Raycasts++; Walls++;
            var nearest = float.PositiveInfinity;
            foreach (var t in Risers)
            {
                var direction = to - from; var e1 = t.B - t.A; var e2 = t.C - t.A;
                var p = Vector3.Cross(direction, e2); var determinant = Vector3.Dot(e1, p);
                if (Math.Abs(determinant) < 1e-8f) continue;
                var s = from - t.A; var u = Vector3.Dot(s, p) / determinant;
                var q = Vector3.Cross(s, e1); var v = Vector3.Dot(direction, q) / determinant;
                var distance = Vector3.Dot(e2, q) / determinant;
                if (u < -1e-6f || v < -1e-6f || u + v > 1.000001f || distance < 0 || distance > 1 || distance >= nearest) continue;
                nearest = distance; triangle = t;
            }
            return triangle.Valid;
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(100, false)]
    [InlineData(100, true)]
    [InlineData(1, true)]
    public void FourRealCellMidpointsFinishFiniteCurbDiscovery(int rayBudget, bool zeroAttemptVertices)
    {
        var scene = new Terrain { Capacity = 1000, WarmCornersOnly = true };
        var cache = new RollingClothSupport(.4f, .4f, 0);
        scene.Begin(0); cache.Update(Origin, Vector2.Zero, 0, 100, scene, scene.Deadline);
        Assert.Equal(9, scene.Floors); Assert.Equal(0, scene.Walls);
        scene.Capacity = 1; scene.WarmCornersOnly = false;
        scene.ZeroAttemptVertices = zeroAttemptVertices;
        var start = zeroAttemptVertices ? 36 : 1;
        for (var frame = start; frame < start + 30; frame++)
        {
            scene.Begin(frame);
            cache.Update(Origin, Vector2.Zero, frame / 30d, rayBudget, scene, scene.Deadline);
            Assert.InRange(scene.Raycasts, 0, 1);
        }
        Describe(scene);
        Assert.Equal(4, scene.AdmittedMidpoints.Count);
        Assert.True(scene.Walls >= 8);
        // Midpoint admission is NOT a full-cell coverage proof. These cells
        // include distinct lower floor, vertical risers and incomplete tops.
        Assert.False(cache.TrySnapshot(Vector2.Zero, scene.Frame / 30d, out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Control_ThreeConsecutiveActualCallsAdmitEachRealTread(int cell)
    {
        var scene = new Terrain(); var before = scene.Discovery.Layer.Count;
        for (var frame = 0; frame < 3; frame++)
        {
            scene.Begin(frame);
            var status = scene.Discovery.Query(Centers[cell], scene, out var hit);
            output.WriteLine($"cell={cell} frame={frame} status={status} floors={scene.Floors} walls={scene.Walls} faces={scene.Discovery.Layer.Count}");
            Assert.Equal(frame == 2 ? LayerQueryResult.Success : LayerQueryResult.Pending, status);
            if (frame == 2) Assert.Equal(.2f, hit.Position.Y);
        }
        Assert.Equal(1, scene.Floors); Assert.Equal(2, scene.Walls);
        Assert.Equal(before + 1, scene.Discovery.Layer.Count);
    }

    private void Describe(Terrain scene) => output.WriteLine(
        $"frames={scene.Frame}; keys={scene.CellFrames.Count}; floors={scene.Floors}; walls={scene.Walls}; midpointSuccesses={scene.MidpointSuccesses}; faces={scene.Discovery.Layer.Count}; pending={scene.Discovery.Pending}");

    [Fact]
    public void AcceptedMidpointNeedsAnIndependentActualFullCellProof()
    {
        var scene = new Terrain();
        LayerFloorHit midpoint = default;
        for (var frame = 0; frame < 3; frame++)
        {
            scene.Begin(frame);
            Assert.Equal(frame == 2 ? LayerQueryResult.Success : LayerQueryResult.Pending,
                scene.Discovery.Query(Centers[3], scene, out midpoint));
        }
        // This smaller quad is wholly inside the ACTUAL admitted upper face.
        // Unlike the whole coarse cell, its complete footprint has a proof.
        Vector3 a = new(.075f, .2f, .075f), b = new(.115f, .2f, .075f);
        Vector3 c = new(.075f, .2f, .115f), d = new(.115f, .2f, .115f);
        var interior = new LayerFloorHit((a + b + c + d) / 4, midpoint.Triangle);
        Assert.Equal(LayerQueryResult.Success,
            scene.Discovery.Layer.TryCellCeiling(interior, a, b, c, d, out var ceiling));
        Assert.Equal(.2f, ceiling, 5);
        Assert.Equal(1, scene.Floors); Assert.Equal(2, scene.Walls);
    }

    [Fact]
    public void RealCandidateStillExpiresInsteadOfRetainingItsFirstRiser()
    {
        var scene = new Terrain(); var faces = scene.Discovery.Layer.Count;
        foreach (var frame in new[] { 0, 1, 5 })
        {
            scene.Begin(frame);
            Assert.Equal(LayerQueryResult.Pending, scene.Discovery.Query(Centers[3], scene, out _));
        }
        Assert.Equal(2, scene.Floors); Assert.Equal(1, scene.Walls);
        Assert.Equal(faces, scene.Discovery.Layer.Count);
        scene.Begin(6);
        Assert.Equal(LayerQueryResult.Pending, scene.Discovery.Query(Centers[3], scene, out _));
        scene.Begin(7);
        Assert.Equal(LayerQueryResult.Success, scene.Discovery.Query(Centers[3], scene, out _));
        Assert.Equal(3, scene.Walls);
    }

    // Scheduling-only adapter: no physical floor proof is claimed here.
    // The real geometry tests above exercise the actual discovery machine.
    private sealed class SchedulingQueries : IClothSupportQueries, IIncrementalClothSupportQueries
    {
        public readonly List<(double Time, bool Cell, Vector2 Key, int Cost)> Calls = [];
        public bool Warm = true, ZeroVertices, ZeroCells;
        public int Capacity = 1000;
        public int Raycasts { get; private set; }
        public LayerQueryResult LastResult { get; private set; }
        private int allowance;
        private double now;
        public void Begin(double time) { now = time; Raycasts = 0; allowance = Capacity; }
        public bool Deadline() => Raycasts < Capacity;
        public void PrepareQuery(int rayAllowance) => allowance = rayAllowance;
        public bool TryVertex(SupportQueryIdentity _, Vector2 at, out Vector3 contact)
        { contact = new(at.X, 0, at.Y); return Record(false, at, ZeroVertices); }
        public bool TryCell(SupportQueryIdentity _, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        { ceiling = 0; var at = (a + b + c + d) / 4; return Record(true, new(at.X, at.Z), ZeroCells); }
        private bool Record(bool cell, Vector2 at, bool zero)
        {
            Assert.True(Deadline()); Assert.True(allowance > 0);
            var cost = zero ? 0 : 1;
            Calls.Add((now, cell, at, cost)); Raycasts += cost;
            LastResult = Warm ? LayerQueryResult.Success : LayerQueryResult.Pending;
            return Warm;
        }
    }

    private static (RollingClothSupport Cache, SchedulingQueries Scene) WarmScheduler()
    {
        var cache = new RollingClothSupport(.4f, .4f, 0);
        var scene = new SchedulingQueries(); scene.Begin(0);
        cache.Update(Origin, Vector2.Zero, 0, 100, scene, scene.Deadline);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 0, out _));
        scene.Warm = false; scene.Capacity = 1; scene.Calls.Clear();
        return (cache, scene);
    }

    [Fact]
    public void PermanentPendingClassesAlternateBoundedBurstsWithoutRefreshingEvidence()
    {
        var (cache, scene) = WarmScheduler();
        for (var frame = 0; frame < 18; frame++)
        {
            var now = 1.2 + frame / 30d; scene.Begin(now);
            cache.Update(Origin, Vector2.Zero, now, 100, scene, scene.Deadline);
            Assert.Equal(1, scene.Raycasts);
        }
        Assert.Equal(9, scene.Calls.Count(c => c.Cell));
        Assert.Equal(9, scene.Calls.Count(c => !c.Cell));
        Assert.Equal(3, scene.Calls.Where(c => c.Cell).Select(c => c.Key).Distinct().Count());
        Assert.Equal(3, scene.Calls.Where(c => !c.Cell).Select(c => c.Key).Distinct().Count());
        for (var i = 3; i < scene.Calls.Count; i++)
            Assert.False(scene.Calls.Skip(i - 3).Take(4).All(c => c.Cell == scene.Calls[i].Cell));
        Assert.False(cache.TrySnapshot(Vector2.Zero, 2.000001, out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ZeroAttemptOppositeClassDoesNotPreventRealClassBursts(bool zeroCells)
    {
        var (cache, scene) = WarmScheduler();
        scene.ZeroCells = zeroCells; scene.ZeroVertices = !zeroCells;
        for (var frame = 0; frame < 12; frame++)
        {
            var now = 1.2 + frame / 30d; scene.Begin(now);
            cache.Update(Origin, Vector2.Zero, now, 100, scene, scene.Deadline);
            Assert.Equal(1, scene.Raycasts);
        }
        var spent = scene.Calls.Where(c => c.Cost > 0).ToArray();
        Assert.Equal(12, spent.Length);
        for (var start = 0; start < 12; start += 3)
            Assert.All(spent.Skip(start).Take(3), c => Assert.Equal(spent[start].Key, c.Key));
        Assert.True(spent.Select(c => c.Key).Distinct().Count() >= 3);
    }

    [Fact]
    public void SameTimestampNeverRepeatsPendingKeyEvenWhenBothProvidersUseNoQueries()
    {
        var (cache, scene) = WarmScheduler(); scene.ZeroCells = scene.ZeroVertices = true;
        for (var repeat = 0; repeat < 3; repeat++)
        {
            scene.Begin(1.2);
            cache.Update(Origin, Vector2.Zero, 1.2, 100, scene, scene.Deadline);
            Assert.Equal(0, scene.Raycasts);
        }
        Assert.Equal(13, scene.Calls.Count);
        Assert.All(scene.Calls.GroupBy(c => (c.Time, c.Cell, c.Key)), group => Assert.Single(group));
        Assert.False(cache.TrySnapshot(Vector2.Zero, 2.000001, out _));
    }

    [Fact]
    public void ExhaustedDeadlineDoesNotSpendOrRenewAnOwedOpportunity()
    {
        var (cache, scene) = WarmScheduler();
        for (var frame = 0; frame < 3; frame++)
        {
            var now = 1.2 + frame / 30d; scene.Begin(now);
            cache.Update(Origin, Vector2.Zero, now, 100, scene, scene.Deadline);
        }
        var previousClass = scene.Calls[^1].Cell;
        scene.Capacity = 0; scene.Begin(1.3);
        cache.Update(Origin, Vector2.Zero, 1.3, 100, scene, scene.Deadline);
        Assert.Equal(3, scene.Calls.Count);
        scene.Capacity = 1; scene.Begin(1.31);
        cache.Update(Origin, Vector2.Zero, 1.31, 100, scene, scene.Deadline);
        Assert.NotEqual(previousClass, scene.Calls[^1].Cell);
    }

    [Fact]
    public void ZeroAttemptActiveCellReleasesItsBurstToARealVertexImmediately()
    {
        var (cache, scene) = WarmScheduler(); scene.ZeroVertices = true;
        scene.Begin(1.2); cache.Update(Origin, Vector2.Zero, 1.2, 100, scene, scene.Deadline);
        Assert.True(scene.Calls[^1].Cell); Assert.Equal(1, scene.Calls[^1].Cost);
        var cell = scene.Calls[^1].Key;
        scene.ZeroVertices = false; scene.ZeroCells = true;
        scene.Begin(1.2 + 1 / 30d);
        cache.Update(Origin, Vector2.Zero, 1.2 + 1 / 30d, 100, scene, scene.Deadline);
        Assert.Contains(scene.Calls, c => c.Time > 1.2 && c.Cell && c.Key == cell && c.Cost == 0);
        Assert.False(scene.Calls[^1].Cell); Assert.Equal(1, scene.Raycasts);
        var vertex = scene.Calls[^1].Key;
        scene.Begin(1.2 + 2 / 30d);
        cache.Update(Origin, Vector2.Zero, 1.2 + 2 / 30d, 100, scene, scene.Deadline);
        Assert.False(scene.Calls[^1].Cell); Assert.Equal(vertex, scene.Calls[^1].Key);
    }

    [Fact]
    public void CellBurstExpiresAtOriginalDeadlineAndDoesNotJumpAheadOfOlderPeers()
    {
        var (cache, scene) = WarmScheduler(); scene.ZeroVertices = true;
        scene.Begin(1.2); cache.Update(Origin, Vector2.Zero, 1.2, 100, scene, scene.Deadline);
        var first = scene.Calls[^1]; Assert.True(first.Cell);
        scene.Begin(1.301); cache.Update(Origin, Vector2.Zero, 1.301, 100, scene, scene.Deadline);
        var next = scene.Calls[^1]; Assert.True(next.Cell);
        Assert.NotEqual(first.Key, next.Key);
        Assert.False(cache.TrySnapshot(Vector2.Zero, 2.000001, out _));
    }
}
