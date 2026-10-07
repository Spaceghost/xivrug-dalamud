using System.Numerics;

namespace XivSurface.Core.Tests;

/// <summary>Actual collision triangles for a stationary rug over a convex rock.
/// The old endpoint chord crosses the rock even though every support face is
/// walkable and connected. No floor or wall result is synthesized from height.
/// </summary>
public sealed class RockDrapeSupportTests
{
    private static readonly Vector3 Peak = new(0, 1, 0);
    private static readonly SupportQueryIdentity Identity = new(1, 1, 0, Peak + Vector3.UnitY * .18f, 1.35f, 1.35f);

    [Fact]
    public void ConvexRockBlocksTheOldSingleChordDespiteWalkableConnectedFaces()
    {
        var scene = Rock();
        Assert.All(scene.Triangles, triangle => Assert.True(triangle.Walkable));
        scene.ResetBudget(100);
        Assert.True(scene.Trace(Identity.CompressionOrigin, new(2, .18f, 0), out var point, out var face));
        Assert.InRange(point.X, .4499f, .4501f);
        Assert.InRange(point.Y, .9549f, .9551f);
        Assert.True(face.Normal.Y > .99f);
    }

    [Fact]
    public void OneUnknownOuterCornerPreventsWholeSquarePublicationUntilItRecovers()
    {
        var cache = new RollingClothSupport();
        var queries = new MissingCornerQueries();
        for (var frame = 0; frame < 180; frame++)
        {
            cache.Update(Identity, Vector2.Zero, frame / 30d, 100, queries);
            Assert.False(cache.TrySnapshot(Vector2.Zero, frame / 30d, out _));
        }
        // (2,2) is outside a circle of radius 2, but the current square-grid
        // contract still requires it. This documents the publication gate;
        // permitting a guessed floor here would violate that contract.
        Assert.True(queries.Misses > 0);
        queries.Missing = false;
        for (var frame = 180; frame < 270; frame++)
            cache.Update(Identity, Vector2.Zero, frame / 30d, 100, queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 269 / 30d, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StationaryConvexRockPublishesAndStaysCoveredAcrossRefreshes(bool wallBehindRidge)
    {
        var scene = Rock(wallBehindRidge);
        var layer = new LayerFloorDiscovery();
        var cache = new RollingClothSupport();
        var queries = new SurfaceQueries(scene, layer);
        var published = 0;
        for (var frame = 0; frame < 240; frame++)
        {
            var now = frame / 30d;
            scene.ResetBudget(100); queries.Now = now;
            Assert.True(scene.TryFloor(new(Vector2.Zero, 1.12f, 1.12f, 0, 1.06f), out var playerFloor));
            if (!layer.BeginFrame(playerFloor, now, Vector2.Zero, 4.1f))
            { cache.Reset(); queries.Clearance.Reset(); }
            Assert.True(layer.Refresh(scene));
            cache.Update(Identity, Vector2.Zero, now, 100 - scene.Calls, queries, () => scene.Calls < scene.AbsoluteLimit);
            Assert.InRange(scene.Calls, 1, 100);
            var ready = cache.TrySnapshot(Vector2.Zero, now, out var snapshot);
            if (frame < 90) continue;
            Assert.True(cache.TrySupportedSnapshot(Vector2.Zero, now, out var gathered), "Even the confirmed central cloth patch disappeared.");
            // Full material and its collision window must remain present even
            // when wall compression folds a contact quad in XZ.
            Assert.True(ready, $"No complete stationary support at frame {frame}; layer faces={layer.Layer.Count}, pending={layer.Pending}, clearance pending={queries.Clearance.PendingCount}, last={queries.LastResult}. {string.Join("; ", queries.Failures.TakeLast(24))}");
            Assert.NotNull(snapshot);
            Assert.Equal(new Vector2(2),snapshot.Half);
            Assert.True(snapshot.MatchesCenterFloor(Vector2.Zero, 1));
            var usableHalf = snapshot.Half - Vector2.Abs(snapshot.Center);
            Assert.True(usableHalf.X >= .39f && usableHalf.Y >= .39f, "A nominally valid patch must retain visible material around the player.");
            Assert.All(snapshot.Contacts, point => Assert.True(float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z)));
            if (wallBehindRidge)
            {
                Assert.All(snapshot.Contacts, point => Assert.True(point.X < 1.3999f, $"Contact crossed wall: {point}."));
            }
            else
                Assert.Contains(snapshot.Contacts, point => point.X == 2 && Math.Abs(point.Z) < .00001f && Math.Abs(point.Y) < .00001f);
            var mesh = ClothSurface.Build(snapshot.Center, snapshot.Half, 1, snapshot.Contacts, snapshot.Width,
                (float)now, false, snapshot.CellCeilings, diagonalParity: snapshot.DiagonalParity, planarCells: snapshot.PlanarCells,
                cellLifts: snapshot.CellLifts);
            Assert.Equal((snapshot.Width - 1) * (snapshot.Height - 1) * 6, mesh.Indices.Length);
            published++;
        }
        // Five stationary seconds after warm-up: more than two 2-second TTLs.
        Assert.Equal(150, published);
        if (wallBehindRidge) Assert.True(queries.WallHits > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OffCenterPlayerNearWallRetainsCompleteMaterialAndGathersBeforeBothWalls(bool corner)
    {
        var scene=new Scene();
        scene.Add(new(-66,18,-16),new(-54,18,-16),new(-54,18,-4));
        scene.Add(new(-66,18,-16),new(-54,18,-4),new(-66,18,-4));
        const float wallX=-59.8f,wallZ=-9.8f;
        scene.Add(new(wallX,17,-15),new(wallX,21,-15),new(wallX,21,-5));
        scene.Add(new(wallX,17,-15),new(wallX,21,-5),new(wallX,17,-5));
        if(corner)
        {
            scene.Add(new(-65,17,wallZ),new(-55,21,wallZ),new(-65,21,wallZ));
            scene.Add(new(-65,17,wallZ),new(-55,17,wallZ),new(-55,21,wallZ));
        }
        var player=new Vector2(-60.2f,-10.1f);
        var identity=new SupportQueryIdentity(1,1,0,new(player.X,18.18f,player.Y),18.35f,1.35f);
        var layer=new LayerFloorDiscovery();var cache=new RollingClothSupport();var queries=new SurfaceQueries(scene,layer);
        for(var frame=0;frame<180;frame++)
        {
            var now=frame/30d;scene.ResetBudget(100);queries.Now=now;
            Assert.True(scene.TryFloor(new(player,18.12f,.5f,17.99f,18.01f),out var floor));
            if(!layer.BeginFrame(floor,now,player,4.1f)){cache.Reset();queries.Clearance.Reset();}
            Assert.True(layer.Refresh(scene));
            cache.Update(identity,player,now,100-scene.Calls,queries,()=>scene.Calls<scene.AbsoluteLimit);
            if(frame<60)continue;
            Assert.True(cache.TrySnapshot(player,now,out var snapshot),string.Join("; ",queries.Failures.TakeLast(10)));
            Assert.NotNull(snapshot);
            var usable=snapshot.Half-Vector2.Abs(snapshot.Center-player);
            Assert.True(usable.X>=1.99999f && usable.Y>=1.99999f);
            var mesh=ClothSurface.Build(snapshot.Center,snapshot.Half,18,snapshot.Contacts,snapshot.Width,(float)now,false,
                snapshot.CellCeilings,diagonalParity:snapshot.DiagonalParity,materialCenter:player,materialHalf:new Vector2(2),
                planarCells:snapshot.PlanarCells,cellLifts:snapshot.CellLifts);
            Assert.Equal((snapshot.Width-1)*(snapshot.Height-1)*6,mesh.Indices.Length);
            Assert.All(mesh.Positions,p=>{Assert.True(p.X<wallX);if(corner)Assert.True(p.Z<wallZ);Assert.True(p.Y>=18+ClothSurface.Clearance);});
            Assert.Contains(mesh.Positions,p=>p.Y>18.10f); // actual gathered folds, not cropped material
        }
        Assert.True(queries.WallHits>0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvexRockAndVerifiedCurbClearanceStillChecksTheWallBeyond(bool wallBehindCurb)
    {
        var scene = new Scene();
        Strip(-1, 1, x => 1 - .1f * x);
        Strip(1, 1.5f, x => .9f - .9f * (x - 1));
        Strip(1.5f, 2, x => .65f - .9f * (x - 1.5f));
        Strip(2, 3, _ => .2f);
        scene.Add(new(1.5f, .45f, -1), new(1.5f, .65f, -1), new(1.5f, .65f, 1));
        scene.Add(new(1.5f, .45f, -1), new(1.5f, .65f, 1), new(1.5f, .45f, 1));
        if (wallBehindCurb)
        {
            scene.Add(new(1.7f, -1, -1.5f), new(1.7f, 3, -1.5f), new(1.7f, 3, 1.5f));
            scene.Add(new(1.7f, -1, -1.5f), new(1.7f, 3, 1.5f), new(1.7f, -1, 1.5f));
        }
        var discovery = new LayerFloorDiscovery();
        scene.ResetBudget(100);
        Assert.True(scene.TryFloor(new(Vector2.Zero, 1.12f, 1.12f, 0, 1.06f), out var playerFloor));
        LayerFloorHit targetFloor = default; double now = 0; var frame = 0;
        // Discover the neighboring floors in order, as the rolling lattice's
        // nearest-first support queries do, then test the entire clearance path.
        foreach (var target in new[] { new Vector2(.9f, 0), new(1.4f, 0), new(1.51f, 0), new(2, 0) })
        {
            var result = LayerQueryResult.Pending;
            for (var attempt = 0; attempt < 40 && result != LayerQueryResult.Success; attempt++)
            {
                now = frame++ / 30d; scene.ResetBudget(100);
                discovery.BeginFrame(playerFloor, now);
                result = discovery.Query(target, scene, out targetFloor);
            }
            Assert.True(result == LayerQueryResult.Success, $"Floor discovery failed at {target}: {result}.");
        }
        Assert.Equal(LayerQueryResult.Success, discovery.Layer.TrySurfacePath(playerFloor.Position, targetFloor.Position, out var path));
        Assert.Contains(path.Zip(path.Skip(1)), pair => Math.Abs(pair.First.X - 1.5f) < .00001f
            && pair.First.X == pair.Second.X && pair.First.Z == pair.Second.Z && pair.Second.Y - pair.First.Y > .19f);
        for (var i = 0; i < path.Length; i++) path[i] += Vector3.UnitY * .18f;
        scene.ResetBudget(100);
        Assert.True(scene.Trace(path[0], path[^1], out _, out var chordHit));
        Assert.True(chordHit.Walkable); // This is why the surface path is needed.
        var topEdges = 0;
        var permitTop = false;
        scene.ResetBudget(100);
        Assert.Equal(ClothClearanceResult.Unknown, Run(false, out _));
        scene.ResetBudget(100);
        var corrected = Run(true, out var blocking);
        Assert.True(topEdges > 0, "The actual upper floor edge must be encountered, not silently absent from the fixture.");
        Assert.Equal(wallBehindCurb ? ClothClearanceResult.Blocked : ClothClearanceResult.Clear, corrected);
        if (wallBehindCurb) Assert.Equal(1.7f, blocking.X, 4);

        void Strip(float x0, float x1, Func<float, float> height)
        {
            var a = new Vector3(x0, height(x0), -1); var b = new Vector3(x1, height(x1), -1);
            var c = new Vector3(x0, height(x0), 1); var d = new Vector3(x1, height(x1), 1);
            scene.Add(a, b, c); scene.Add(b, d, c);
        }
        ClothClearanceResult Run(bool permitVerifiedTop, out Vector3 blocking)
        {
            permitTop = permitVerifiedTop;
            return new ClothClearanceTrace().Query(1, playerFloor.Position, targetFloor.Position, path, now, Cast, out blocking);
        }
        ClothClearanceCast Cast(Vector3 from, Vector3 to)
        {
            var direction = Vector3.Normalize(to - from); var length = Vector3.Distance(from, to); var travelled = 0f;
            for (var step = 0; step < 4; step++)
            {
                if (!scene.CanQuery) return new(ClothClearanceResult.Pending);
                if (!scene.Trace(from + direction * travelled, to, out var point, out var face)) return new(ClothClearanceResult.Clear);
                var top = permitTop && discovery.Layer.IsVerifiedCurbTopCrossing(from, to, point, face);
                if (top || discovery.Layer.IsVerifiedCurb(face))
                {
                    if (top) topEdges++;
                    var next = Vector3.Dot(point - from, direction) + .005f;
                    if (next <= travelled) return new(ClothClearanceResult.Unknown);
                    if (next >= length) return new(ClothClearanceResult.Clear);
                    travelled = next; continue;
                }
                return new(Math.Abs(face.Normal.Y) > .45f ? ClothClearanceResult.Unknown : ClothClearanceResult.Blocked, point);
            }
            return new(ClothClearanceResult.Unknown);
        }
    }

    private sealed class SurfaceQueries(Scene scene, LayerFloorDiscovery layer) : IClothSupportQueries, IIncrementalClothSupportQueries, IPlanarClothSupportQueries, IConformalClothSupportQueries
    {
        public readonly ClothClearanceTrace Clearance = new();
        public readonly List<string> Failures = [];
        public double Now;
        public int WallHits;
        public int Raycasts => scene.Calls;
        public LayerQueryResult LastResult { get; private set; }
        public bool LastCellPlanar { get; private set; }
        public float LastCellLift { get; private set; } = float.NaN;
        public void PrepareQuery(int rayAllowance) => scene.Limit = Math.Min(scene.AbsoluteLimit, scene.Calls + rayAllowance);
        public bool TryVertex(SupportQueryIdentity identity, Vector2 nominal, out Vector3 contact)
        {
            contact = default;
            LastResult = layer.Query(nominal, scene, out var floor);
            if (LastResult != LayerQueryResult.Success) { Failures.Add($"floor {nominal} {LastResult}"); return false; }
            var origin = identity.CompressionOrigin - Vector3.UnitY * .18f;
            if (Vector2.DistanceSquared(new(origin.X, origin.Z), nominal) < .000001f)
            { contact = floor.Position; return true; }
            LastResult = layer.Layer.TrySurfacePath(origin, floor.Position, out var path, () => scene.CanQuery);
            if (LastResult != LayerQueryResult.Success) { Failures.Add($"path {origin}->{floor.Position} {LastResult}"); return false; }
            for (var i = 0; i < path.Length; i++) path[i] += Vector3.UnitY * .18f;
            var result = Clearance.Query(identity.GeometryGeneration, origin, floor.Position, path, Now, Cast, out var blocking);
            if (result == ClothClearanceResult.Clear)
            { contact = floor.Position; return true; }
            if (result == ClothClearanceResult.Blocked)
            {
                var from = new Vector2(origin.X, origin.Z);
                var delta = nominal - from;
                var length = delta.Length();
                var distance = Vector2.Dot(new(blocking.X - origin.X, blocking.Z - origin.Z), delta / length);
                var compressed = from + delta / length * Math.Max(.03f, distance - .06f);
                LastResult = layer.Query(compressed, scene, out floor);
                if (LastResult == LayerQueryResult.Success) { contact = floor.Position; return true; }
                Failures.Add($"compressed floor {compressed} {LastResult}"); return false;
            }
            LastResult = result == ClothClearanceResult.Pending ? LayerQueryResult.Pending : LayerQueryResult.Unknown;
            Failures.Add($"clearance {nominal} {LastResult}");
            return false;
        }
        private ClothClearanceCast Cast(Vector3 from, Vector3 to)
        {
            if (!scene.CanQuery) return new(ClothClearanceResult.Pending);
            if (!scene.Trace(from, to, out var point, out var triangle)) return new(ClothClearanceResult.Clear);
            // The fixture has no risers. A remaining floor/unknown hit cannot
            // be skipped to reach a wall behind it.
            if (Math.Abs(triangle.Normal.Y) > .45f)
            { Failures.Add($"unexpected terrain {from}->{to} hit {point} normal={triangle.Normal}"); return new(ClothClearanceResult.Unknown); }
            WallHits++;
            return new(ClothClearanceResult.Blocked, point);
        }
        public bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        {
            LastCellPlanar = false;
            LastCellLift = float.NaN;
            var at = (a + b + c + d) / 4;
            LastResult = layer.Query(new(at.X, at.Z), scene, out var floor);
            ceiling = floor.Position.Y;
            if (LastResult != LayerQueryResult.Success) Failures.Add($"cell {at} {LastResult}");
            if (LastResult != LayerQueryResult.Success) return false;
            LastCellPlanar = layer.ProvesPlanarCell(floor, a, b, c, d);
            if (LastCellPlanar) LastCellLift = 0;
            if (!LastCellPlanar)
            {
                // Exercise the runtime's complete footprint gate, including
                // bounded discovery of faces that no corner/center ray hit.
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    LastResult = layer.Layer.TryCellCeiling(floor, a, b, c, d, out ceiling);
                    if (LastResult != LayerQueryResult.Unknown || layer.Layer.CellCeilingMissingWitness is not { } missing) break;
                    LastResult = layer.Query(missing, scene, out _);
                    if (LastResult != LayerQueryResult.Success) break;
                    LastResult = LayerQueryResult.Pending;
                }
                if (LastResult != LayerQueryResult.Success)
                    Failures.Add($"cell footprint {at} {LastResult}: {layer.Layer.CellCeilingFailure}");
                else LastCellLift = layer.Layer.CellCeilingLift;
            }
            return LastResult == LayerQueryResult.Success;
        }
    }

    private sealed class MissingCornerQueries : IClothSupportQueries
    {
        public bool Missing = true;
        public int Misses;
        public bool TryVertex(SupportQueryIdentity identity, Vector2 nominal, out Vector3 contact)
        {
            contact = new(nominal.X, 0, nominal.Y);
            if (Missing && Vector2.DistanceSquared(nominal, new(2, 2)) < .000001f)
            { Misses++; return false; }
            return true;
        }
        public bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        { ceiling = 0; return true; }
    }

    private static Scene Rock(bool wall = false)
    {
        var scene = new Scene();
        Vector3[] inner = [new(-1, .9f, -1), new(1, .9f, -1), new(1, .9f, 1), new(-1, .9f, 1)];
        Vector3[] outer = [new(-2, 0, -2), new(2, 0, -2), new(2, 0, 2), new(-2, 0, 2)];
        Vector3[] apron = [new(-4, 0, -4), new(4, 0, -4), new(4, 0, 4), new(-4, 0, 4)];
        for (var i = 0; i < 4; i++)
        {
            var next = (i + 1) % 4;
            scene.Add(Peak, inner[i], inner[next]);
            scene.Add(inner[i], outer[i], inner[next]);
            scene.Add(inner[next], outer[i], outer[next]);
            scene.Add(outer[i], apron[i], outer[next]);
            scene.Add(outer[next], apron[i], apron[next]);
        }
        if (wall)
        {
            scene.Add(new(1.4f, -1, -3), new(1.4f, 3, -3), new(1.4f, 3, 3));
            scene.Add(new(1.4f, -1, -3), new(1.4f, 3, 3), new(1.4f, -1, 3));
        }
        return scene;
    }

    private sealed class Scene : ILayerFloorScene
    {
        public readonly List<LayerTriangle> Triangles = [];
        public int Calls, Limit, AbsoluteLimit;
        public bool CanQuery => Calls < Math.Min(Limit, AbsoluteLimit);
        public void ResetBudget(int limit) { Calls = 0; Limit = AbsoluteLimit = limit; }
        public void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            if (normal.Y < 0) normal = -normal;
            Triangles.Add(new(a, b, c, normal));
        }
        public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit hit)
        {
            hit = default;
            if (!Trace(new(probe.Position.X, probe.StartY, probe.Position.Y),
                    new(probe.Position.X, probe.StartY - probe.Length, probe.Position.Y), out var point, out var triangle)
                || !ClothFloorQueryPolicy.Accept(probe, point, triangle.Normal, out _)) return false;
            hit = new(point, triangle);
            return hit.Valid;
        }
        public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle) => Trace(from, to, out _, out triangle);
        public bool Trace(Vector3 from, Vector3 to, out Vector3 point, out LayerTriangle triangle)
        {
            Assert.True(CanQuery); Calls++;
            point = default; triangle = default;
            var nearest = float.PositiveInfinity;
            var direction = to - from;
            foreach (var candidate in Triangles)
            {
                var ab = candidate.B - candidate.A; var ac = candidate.C - candidate.A;
                var p = Vector3.Cross(direction, ac); var determinant = Vector3.Dot(ab, p);
                if (Math.Abs(determinant) < 1e-7f) continue;
                var inverse = 1 / determinant; var delta = from - candidate.A;
                var u = Vector3.Dot(delta, p) * inverse; if (u < -.000001f || u > 1.000001f) continue;
                var q = Vector3.Cross(delta, ab); var v = Vector3.Dot(direction, q) * inverse;
                if (v < -.000001f || u + v > 1.000001f) continue;
                var t = Vector3.Dot(ac, q) * inverse;
                if (t < 0 || t > 1 || t >= nearest) continue;
                nearest = t; triangle = candidate; point = from + direction * t;
            }
            return triangle.Valid;
        }
    }
}
