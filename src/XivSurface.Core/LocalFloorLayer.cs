using System.Numerics;

namespace XivSurface.Core;

/// <summary>Copied world-space collision hit geometry, never a navmesh polygon
/// or a plane invented from contact samples. Supplied normal preserves the
/// collision face orientation independently of the triangle's index order.</summary>
public readonly record struct LayerTriangle(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)
{
    public bool Valid => MathEx.Finite(A) && MathEx.Finite(B) && MathEx.Finite(C) && MathEx.Finite(Normal)
        && Math.Max(Math.Max(A.LengthSquared(), B.LengthSquared()), C.LengthSquared()) <= 3e12f
        && Vector3.Cross(B - A, C - A).LengthSquared() > 1e-10f
        && Normal.LengthSquared() is > .9f and < 1.1f
        && Math.Abs(Vector3.Dot(Vector3.Normalize(Vector3.Cross(B - A, C - A)), Normal)) > .99f;
    public bool Walkable => Valid && Normal.Y >= .5f
        && Math.Abs(Vector3.Normalize(Vector3.Cross(B-A,C-A)).Y) >= .5f;
    public float MinimumY => Math.Min(A.Y, Math.Min(B.Y, C.Y));
    public float MaximumY => Math.Max(A.Y, Math.Max(B.Y, C.Y));
    public bool TryHeight(Vector2 position, out float height)
    {
        height = 0;
        if (!Walkable || !MathEx.Finite(position)) return false;
        var plane = Vector3.Cross(B-A,C-A);
        height = A.Y - (plane.X * (position.X - A.X) + plane.Z * (position.Y - A.Z)) / plane.Y;
        return float.IsFinite(height);
    }
    public bool Contains(Vector3 position)
    {
        if (position.X < Math.Min(A.X, Math.Min(B.X, C.X)) - LocalFloorLayer.SeamTolerance
            || position.X > Math.Max(A.X, Math.Max(B.X, C.X)) + LocalFloorLayer.SeamTolerance
            || position.Z < Math.Min(A.Z, Math.Min(B.Z, C.Z)) - LocalFloorLayer.SeamTolerance
            || position.Z > Math.Max(A.Z, Math.Max(B.Z, C.Z)) + LocalFloorLayer.SeamTolerance) return false;
        if (!TryHeight(new(position.X, position.Z), out var y) || Math.Abs(y - position.Y) > LocalFloorLayer.SeamTolerance) return false;
        var a = new Vector2(A.X, A.Z); var ab = new Vector2(B.X - A.X, B.Z - A.Z);
        var ac = new Vector2(C.X - A.X, C.Z - A.Z); var ap = new Vector2(position.X, position.Z) - a;
        var determinant = Cross(ab, ac);
        if (Math.Abs(determinant) < 1e-8) return false;
        var u = Cross(ap, ac) / determinant; var v = Cross(ab, ap) / determinant;
        return u >= -1e-5 && v >= -1e-5 && u + v <= 1.00001;
    }
    public float DistanceXZ(Vector2 point)
    {
        if (TryHeight(point, out var y) && Contains(new(point.X, y, point.Y))) return 0;
        return Math.Min(SegmentDistance(A, B), Math.Min(SegmentDistance(B, C), SegmentDistance(C, A)));
        float SegmentDistance(Vector3 from, Vector3 to)
        {
            var a = new Vector2(from.X, from.Z); var d = new Vector2(to.X - from.X, to.Z - from.Z);
            var t = d.LengthSquared() > 1e-12f ? Math.Clamp(Vector2.Dot(point - a, d) / d.LengthSquared(), 0, 1) : 0;
            return Vector2.Distance(point, a + d * t);
        }
    }
    private static double Cross(Vector2 a, Vector2 b) => (double)a.X * b.Y - (double)a.Y * b.X;
}

public readonly record struct LayerFloorHit(Vector3 Position, LayerTriangle Triangle)
{
    public const float PlanarHeightTolerance = .0001f;
    public bool Valid => MathEx.Finite(Position) && Triangle.Contains(Position);

    /// <summary>A single measured, convex collision face contains the entire
    /// compressed cell, not merely five agreeing samples. Both possible cloth
    /// diagonals and all their subdivisions remain inside that actual face.
    /// Callers must retain at least PlanarHeightTolerance vertical clearance.</summary>
    public bool ProvesPlanarCell(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        if (!Valid) return false;
        var midpoint = (a+b+c+d)/4;
        if (!MathEx.Finite(midpoint) || Vector2.DistanceSquared(new(Position.X,Position.Z),new(midpoint.X,midpoint.Z))
            > PlanarHeightTolerance*PlanarHeightTolerance) return false;
        Span<Vector3> corners = stackalloc Vector3[] { a,b,c,d };
        foreach (var point in corners)
            if (!Triangle.Contains(point) || !Triangle.TryHeight(new(point.X,point.Z),out var height)
                || Math.Abs(point.Y-height) > PlanarHeightTolerance) return false;
        return true;
    }
}

/// <summary>Layer-local query guidance plus geometric
/// acceptance. A predicted plane only moves the query origin; a real collision
/// hit and an edge-connected witness are mandatory. Does not prove that the
/// intervening unsampled scene is obstacle-free, nor provide native discovery,
/// scene freshness, compression or a replacement for final foot/depth guards.</summary>
public sealed partial class LocalFloorLayer
{
    public const float SeamTolerance = .002f;
    public const float MaximumCurbHeight = .35f;
    public const float MaximumQueryAdvance = .6f;
    public const int MaximumTriangles = 1024;
    public const int MaximumRiserFaces = 4096;
    public const int MaximumDirectedPortals = 8192;
    public const int MaximumPortalVisits = 4096;
    public const int MaximumSurfacePathPoints = 257;
    private readonly record struct SurfacePathNode(LayerTriangle Triangle, float Enter, float Exit, int Parent, Connection? Via);
    private readonly List<LayerTriangle> connected = [];
    private readonly List<LayerTriangle> curbFaces = [];
    private readonly Dictionary<LayerTriangle, double> observed = [];
    private sealed record Connection(LayerTriangle To, LayerTriangle[] Risers, Vector3 ExitA, Vector3 ExitB, Vector3 EntryA, Vector3 EntryB);
    private readonly Dictionary<LayerTriangle, List<Connection>> links = [];
    private readonly List<LayerTriangle> retirement = [];
    private readonly HashSet<LayerTriangle> reachable = [];
    private readonly HashSet<LayerTriangle> usedCurbs = [];
    private readonly Queue<LayerTriangle> traversal = [];
    private readonly Dictionary<LayerTriangle, float> approach = [];
    private readonly CoplanarCellCoverage planarCoverage = new();
    private readonly List<LayerTriangle> planarFaces = new(CoplanarCellCoverage.MaximumTriangles);
    private readonly HashSet<LayerTriangle> planarVisited = [];
    private readonly Queue<LayerTriangle> planarPending = [];
    private readonly List<LayerTriangle> ceilingFaces = [];
    private readonly HashSet<LayerTriangle> ceilingVisited = [];
    private readonly Queue<LayerTriangle> ceilingPending = [];
    private readonly ClothCellCeiling cellCeiling = new();
    private LayerTriangle root;
    private Vector3 rootPoint;
    private double now;
    private int portalCount;
    public int Count => connected.Count;
    public string CellCeilingFailure { get; private set; } = "not queried";
    public Vector2? CellCeilingMissingWitness { get; private set; }
    public float CellCeilingLift { get; private set; } = float.NaN;
    /// <summary>Discovery hint only, immediately beyond a fresh corridor
    /// boundary. A new actual floor query remains mandatory.</summary>
    public Vector2? SurfacePathMissingWitness { get; private set; }

    /// <summary>Read-only diagnostic of retained collision evidence. This is
    /// not rendered-world geometry or an authorization to reuse its heights.</summary>
    public (int FlatFaces, int SlopedFaces, int RiserFaces, float MinimumNormalY) DescribeGeometry()
    {
        var flat = 0; var sloped = 0; var minimumNormalY = 1f;
        foreach (var face in connected)
        {
            if (face.Normal.Y >= .9999f) flat++; else sloped++;
            minimumNormalY = Math.Min(minimumNormalY, face.Normal.Y);
        }
        return (flat, sloped, curbFaces.Count, minimumNormalY);
    }

    public bool Seed(LayerFloorHit playerFloor)
    {
        connected.Clear(); clothConnectors.Clear(); curbFaces.Clear(); observed.Clear(); links.Clear(); approach.Clear(); root = default; portalCount = 0;
        if (!playerFloor.Valid) return false;
        root = playerFloor.Triangle;
        rootPoint = playerFloor.Position;
        connected.Add(root); observed[root] = now; links[root] = []; return true;
    }

    /// <summary>Prune expired/out-of-window evidence and re-root the recorded
    /// actual edge graph at this frame's player triangle. Expired evidence in
    /// the retained area invalidates the adapter's support cache. Routine
    /// retirement outside its halo does not force a whole-window restart.</summary>
    public bool BeginFrame(LayerFloorHit player, double time, Vector2? center = null, float retainRadius = 5, double ttl = 2)
    {
        if (!player.Valid || !double.IsFinite(time) || time < 0 || time < now)
        { Seed(default); now = double.IsFinite(time) ? Math.Max(0, time) : 0; return false; }
        now = time;
        var addedRoot = false;
        if (!connected.Contains(player.Triangle))
        {
            if (connected.Count >= MaximumTriangles) { Seed(player); return false; }
            // The actual near-feet player hit can establish a new root across
            // a real adjacent edge even where that floor doubles back above
            // the previous one. Arbitrary support probes cannot do this.
            foreach (var parent in connected)
                if (SharesBoundary(parent, player.Triangle))
                { if (!Add(parent,player.Triangle,[])) { Seed(player); return false; } addedRoot = true; break; }
            if (!addedRoot) { Seed(player); return false; }
        }
        // A real player switch between overlapping levels invalidates cached
        // contacts even when both levels belong to one connected ramp graph.
        // Ordinary adjacent coplanar triangle changes are not layer switches.
        var changedRoot = root != player.Triangle;
        var layerChanged = false; var rootAdditionCompatible = !addedRoot;
        if (changedRoot)
        {
            var reached = TryNearest(new(player.Position.X, player.Position.Z), out var oldApproach, out _);
            layerChanged = !reached || SurfaceHeight(oldApproach,new(player.Position.X, player.Position.Z), out var oldHeight)
                && SurfaceContains(oldApproach,new(player.Position.X, oldHeight, player.Position.Z))
                && Math.Abs(oldHeight - player.Position.Y) > SeamTolerance;
            // A newly observed adjacent face extends the same measured layer
            // when its actual player point is reached through local portals
            // at the same height. Keep fresh world-space samples on that walk;
            // a nearest boundary or distant graph connection is insufficient.
            rootAdditionCompatible |= reached && SurfaceContains(oldApproach,player.Position);
        }
        root = player.Triangle;
        rootPoint = player.Position;
        observed[player.Triangle] = now;
        retirement.Clear(); var invalidated = false;
        foreach (var pair in observed)
        {
            if (pair.Key == player.Triangle) continue;
            var outside = center is { } at && SurfaceDistance(pair.Key,at) > retainRadius;
            var expired = now - pair.Value > ttl;
            if (outside || expired) retirement.Add(pair.Key);
            if (expired && !outside) invalidated = true;
        }
        if (retirement.Count == 0)
        { return !layerChanged && rootAdditionCompatible; }
        foreach (var triangle in retirement) { observed.Remove(triangle); clothConnectors.Remove(triangle); connected.Remove(triangle); curbFaces.Remove(triangle); links.Remove(triangle); }
        reachable.Clear(); traversal.Clear(); reachable.Add(player.Triangle); traversal.Enqueue(player.Triangle);
        while (traversal.TryDequeue(out var from))
        {
            if (!links.TryGetValue(from, out var adjacent)) continue;
            for (var i = adjacent.Count - 1; i >= 0; i--)
            {
                var edge = adjacent[i]; var fresh = observed.ContainsKey(edge.To);
                foreach (var riser in edge.Risers) fresh &= observed.ContainsKey(riser);
                if (!fresh) { adjacent.RemoveAt(i); continue; }
                if (reachable.Add(edge.To)) traversal.Enqueue(edge.To);
            }
        }
        for (var i = connected.Count - 1; i >= 0; i--)
        {
            var triangle = connected[i]; if (reachable.Contains(triangle)) continue;
            invalidated |= center is null || SurfaceDistance(triangle,center.Value) <= retainRadius;
            connected.RemoveAt(i); clothConnectors.Remove(triangle); observed.Remove(triangle); links.Remove(triangle);
        }
        usedCurbs.Clear();
        foreach (var triangle in connected)
            foreach (var edge in links[triangle])
                if (reachable.Contains(edge.To)) foreach (var riser in edge.Risers) usedCurbs.Add(riser);
        for (var i = curbFaces.Count - 1; i >= 0; i--)
            if (!usedCurbs.Contains(curbFaces[i])) { observed.Remove(curbFaces[i]); curbFaces.RemoveAt(i); }
        portalCount = 0;
        foreach (var entry in links) portalCount += entry.Value.Count;
        return !invalidated && !layerChanged && rootAdditionCompatible;
    }

    public bool TryRefresh(out LayerTriangle triangle)
    {
        triangle = default; var oldest = now - 1.1;
        foreach (var pair in observed)
            if (pair.Value < oldest) { oldest = pair.Value; triangle = pair.Key; }
        return triangle.Valid;
    }
    public bool ObserveExact(LayerTriangle triangle)
    {
        if (!observed.ContainsKey(triangle)) return false;
        observed[triangle] = now; return true;
    }

    public bool Contains(LayerTriangle triangle) => connected.Contains(triangle);
    public bool IsVerifiedCurb(LayerTriangle triangle) => curbFaces.Contains(triangle) && !IsConnector(triangle);

    /// <summary>Recognize only the upper-floor hit made by a lifted vertical
    /// segment at an already measured curb portal. This is not permission to
    /// skip ordinary floor hits, a horizontal approach, or a wall beyond it.
    /// The caller must resume its ray after this exact hit.</summary>
    public bool IsVerifiedCurbTopCrossing(Vector3 from, Vector3 to, Vector3 hitPoint,
        LayerTriangle hitFace, float clearance = .18f)
    {
        if (!MathEx.Finite(from) || !MathEx.Finite(to) || !MathEx.Finite(hitPoint)
            || !float.IsFinite(clearance) || clearance is <= 0 or > 1
            || !Fresh(hitFace) || !hitFace.Walkable
            || Vector2.DistanceSquared(new(from.X, from.Z), new(to.X, to.Z)) > SeamTolerance * SeamTolerance)
            return false;
        var fromFloor = from - Vector3.UnitY * clearance;
        var toFloor = to - Vector3.UnitY * clearance;
        var upper = fromFloor.Y > toFloor.Y ? fromFloor : toFloor;
        var lower = fromFloor.Y > toFloor.Y ? toFloor : fromFloor;
        var rise = upper.Y - lower.Y;
        if (rise <= SeamTolerance || rise > MaximumCurbHeight + SeamTolerance
            || !SurfaceContains(hitFace,upper) || Vector3.DistanceSquared(hitPoint, upper) > SeamTolerance * SeamTolerance
            || !OnSegment(hitPoint, from, to) || !links.TryGetValue(hitFace, out var adjacent)) return false;
        foreach (var edge in adjacent)
        {
            if (edge.Risers.Length == 0 || !edge.To.Walkable || !Fresh(edge.To) || !SurfaceContains(edge.To,lower)
                || !OnSegment(upper, edge.ExitA, edge.ExitB) || !OnSegment(lower, edge.EntryA, edge.EntryB)) continue;
            var freshRisers = true;
            foreach (var riser in edge.Risers) freshRisers &= Fresh(riser);
            if (freshRisers) return true;
        }
        return false;

        bool Fresh(LayerTriangle face) => observed.TryGetValue(face, out var at)
            && now >= at && now - at <= 2;
        static bool OnSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            var delta = b - a; var length = delta.LengthSquared();
            if (length <= 1e-12f || !float.IsFinite(length)) return false;
            var fraction = Math.Clamp(Vector3.Dot(point - a, delta) / length, 0, 1);
            return Vector3.DistanceSquared(point, a + delta * fraction) <= SeamTolerance * SeamTolerance;
        }
    }

    /// <summary>
    /// Follow the straight XZ segment across fresh measured floor portals,
    /// retaining the actual height of each face rather than a chord through a
    /// ridge. Both supplied endpoints must match their measured floors. A
    /// verified curb contributes its lower and upper portal points. Success
    /// proves recorded surface continuity only: callers still have to query
    /// walls above EVERY returned segment. No collision queries run here.
    /// Pending means the caller's deadline expired; Unknown means missing,
    /// ambiguous or over-budget evidence. Failed calls publish no partial path.
    /// </summary>
    public LayerQueryResult TrySurfacePath(Vector3 fromFloor, Vector3 toFloor, out Vector3[] points,
        Func<bool>? withinDeadline = null)
    {
        points = [];
        SurfacePathMissingWitness = null;
        SurfacePathFailure = "none";
        if (!MathEx.Finite(fromFloor) || !MathEx.Finite(toFloor) || connected.Count == 0)
            return Fail("invalid endpoints or empty layer");
        if (withinDeadline?.Invoke() == false) return Fail("entry deadline", LayerQueryResult.Pending);
        var from = new Vector2(fromFloor.X, fromFloor.Z);
        var to = new Vector2(toFloor.X, toFloor.Z);
        var delta = to - from;
        var queue = new PriorityQueue<(LayerTriangle Triangle, float Enter, int Parent, Connection? Via), float>();
        var reached = new Dictionary<LayerTriangle, float>();
        var nodes = new List<SurfacePathNode>();
        foreach (var face in connected)
        {
            if (withinDeadline?.Invoke() == false) return Fail("start-face deadline", LayerQueryResult.Pending);
            if (!Fresh(face) || !SurfaceContains(face,fromFloor)) continue;
            reached[face] = 0; queue.Enqueue((face, 0, -1, null), 0);
        }
        if (queue.Count == 0) return Fail("no fresh face contains the start");
        if (delta.LengthSquared() < 1e-12f)
        {
            if (Math.Abs(fromFloor.Y - toFloor.Y) > SeamTolerance
                || !reached.Keys.Any(face => SurfaceContains(face,toFloor))) return Fail("coincident endpoints disagree");
            points = [fromFloor, toFloor]; return LayerQueryResult.Success;
        }

        var visits = 0; var last = -1;
        while (queue.TryDequeue(out var current, out _))
        {
            if (withinDeadline?.Invoke() == false) return Fail("corridor deadline", LayerQueryResult.Pending);
            if (current.Enter > reached[current.Triangle] + 1e-5f) continue;
            if (!Interval(current.Triangle, from, to, out var enter, out var exit)
                || enter > current.Enter + 1e-5f || exit < current.Enter - 1e-5f) continue;
            if (nodes.Count >= MaximumTriangles) return Fail("corridor node cap");
            var index = nodes.Count;
            nodes.Add(new(current.Triangle, Math.Max(enter, current.Enter), exit, current.Parent, current.Via));
            if (exit >= 1 - 1e-5f && SurfaceContains(current.Triangle,toFloor) && last < 0) last = index;
            if (!links.TryGetValue(current.Triangle, out var adjacent)) continue;
            foreach (var edge in adjacent)
            {
                if (withinDeadline?.Invoke() == false) return Fail("portal deadline", LayerQueryResult.Pending);
                if (++visits > MaximumPortalVisits) return Fail("portal visit cap");
                if (!Fresh(edge.To)) continue;
                var freshRisers = true;
                foreach (var riser in edge.Risers) freshRisers &= Fresh(riser);
                if (!freshRisers
                    || !PortalCrossing(from, to, edge.ExitA, edge.ExitB, current.Enter, out var crossing)
                    || crossing < enter - 1e-5f || crossing > exit + 1e-5f
                    || !PortalCrossing(from, to, edge.EntryA, edge.EntryB, crossing, out var arrival)
                    || Math.Abs(crossing - arrival) > 1e-5f
                    || !Interval(edge.To, from, to, out var nextEnter, out var nextExit)
                    || nextEnter > arrival + 1e-5f || nextExit < arrival - 1e-5f) continue;
                arrival = Math.Clamp(arrival, 0, 1);
                if (reached.TryGetValue(edge.To, out var earlier) && earlier <= arrival + 1e-5f) continue;
                reached[edge.To] = arrival;
                queue.Enqueue((edge.To, arrival, index, edge), arrival);
            }
        }
        // A fork can have two recorded faces over the same horizontal span.
        // Matching only the final endpoint would silently choose one deck.
        // Sweep overlapping reachable spans and reject differing heights;
        // zero-length seams remain valid, including measured vertical curbs.
        var ordered = nodes.OrderBy(node => node.Enter).ToArray();
        var overlapping = new List<SurfacePathNode>();
        foreach (var node in ordered)
        {
            if (withinDeadline?.Invoke() == false) return Fail("overlap deadline", LayerQueryResult.Pending);
            for (var i = overlapping.Count - 1; i >= 0; i--)
            {
                if (withinDeadline?.Invoke() == false) return Fail("overlap pair deadline", LayerQueryResult.Pending);
                if (++visits > MaximumPortalVisits) return Fail("overlap pair cap");
                var other = overlapping[i];
                if (other.Exit <= node.Enter + 1e-5f) { overlapping.RemoveAt(i); continue; }
                var end = Math.Min(node.Exit, other.Exit);
                if (end <= node.Enter + 1e-5f) continue;
                if (!SameHeight(node.Triangle, other.Triangle, node.Enter)
                    || !SameHeight(node.Triangle, other.Triangle, end)) return Fail("overlapping corridor floors differ");
            }
            overlapping.Add(node);
        }
        if (last < 0)
        {
            // Request actual collision discovery just beyond the furthest
            // measured boundary on THIS origin-to-target corridor. Do not
            // replace missing floor with its extrapolated plane. Ambiguous
            // intervals above have already failed without a witness.
            var furthest = -1f; var boundaryHeight = 0f; var ambiguous = false;
            foreach (var node in nodes)
            {
                if (withinDeadline?.Invoke() == false) return Fail("missing-witness deadline", LayerQueryResult.Pending);
                var at = from + delta * node.Exit;
                if (!SurfaceHeight(node.Triangle,at, out var height)
                    || !SurfaceContains(node.Triangle,new(at.X, height, at.Y))) continue;
                if (node.Exit > furthest + 1e-5f)
                { furthest = node.Exit; boundaryHeight = height; ambiguous = false; }
                else if (Math.Abs(node.Exit - furthest) <= 1e-5f && Math.Abs(height - boundaryHeight) > SeamTolerance)
                    ambiguous = true;
            }
            if (!ambiguous && furthest >= 0 && furthest < 1 - 1e-5f)
            {
                var next = Math.Min(1, furthest + .005f / delta.Length());
                if (next > furthest) SurfacePathMissingWitness = from + delta * next;
            }
            return Fail(SurfacePathMissingWitness is null ? "corridor endpoint not reached; no unambiguous witness" : "uncovered corridor; discovery witness available");
        }

        var chain = new List<int>();
        for (var index = last; index >= 0; index = nodes[index].Parent)
        {
            if (withinDeadline?.Invoke() == false) return Fail("chain deadline", LayerQueryResult.Pending);
            if (chain.Count >= MaximumSurfacePathPoints) return Fail("chain point cap");
            chain.Add(index);
        }
        var path = new List<Vector3> { fromFloor };
        var previousCrossing = 0d;
        for (var i = chain.Count - 2; i >= 0; i--)
        {
            if (withinDeadline?.Invoke() == false) return Fail("path append deadline", LayerQueryResult.Pending);
            var node = nodes[chain[i]];
            var at = from + delta * node.Enter;
            var parent = nodes[node.Parent].Triangle;
            // At a measured shared edge, rounding an otherwise exact crossing
            // can fall just outside one narrow face. Select only a neighboring
            // representable point accepted by BOTH unchanged face predicates;
            // never snap a missing endpoint or widen a containment tolerance.
            if (!ContainsAt(parent, at) || !ContainsAt(node.Triangle, at))
            {
                if (node.Via is null || !TryPortalGridPoint(parent, node.Triangle, node.Via,
                    from, to, node.Enter, previousCrossing, at, out at))
                    return Fail("portal point does not match its floor");
            }
            var crossing = PathFraction(from, to, at);
            if (crossing < previousCrossing || crossing > 1) return Fail("portal crossings not ordered");
            previousCrossing = crossing;
            if (!Append(nodes[node.Parent].Triangle, at) || !Append(node.Triangle, at))
                return Fail("portal point does not match its floor");
        }
        if (path.Count >= MaximumSurfacePathPoints) return Fail("path point cap");
        if (path[^1] != toFloor) path.Add(toFloor);
        points = path.ToArray(); return LayerQueryResult.Success;

        LayerQueryResult Fail(string reason, LayerQueryResult result = LayerQueryResult.Unknown)
        { SurfacePathFailure = reason; return result; }

        bool Fresh(LayerTriangle face) => observed.TryGetValue(face, out var at)
            && now >= at && now - at <= 2;
        bool SameHeight(LayerTriangle a, LayerTriangle b, float t)
        {
            var at = from + delta * t;
            return SurfaceHeight(a,at, out var first) && SurfaceHeight(b,at, out var second)
                && Math.Abs(first - second) <= SeamTolerance;
        }
        bool Append(LayerTriangle face, Vector2 at)
        {
            if (!SurfaceHeight(face,at, out var y)) return false;
            var point = new Vector3(at.X, y, at.Y);
            if (!SurfaceContains(face,point)) return false;
            if (Vector3.DistanceSquared(path[^1], point) <= 1e-12f) return true;
            if (path.Count >= MaximumSurfacePathPoints - 1) return false;
            path.Add(point); return true;
        }
    }

    private bool ContainsAt(LayerTriangle face, Vector2 at) =>
        SurfaceHeight(face,at, out var height) && SurfaceContains(face,new(at.X, height, at.Y));

    private static double PathFraction(Vector2 from, Vector2 to, Vector2 at)
    {
        var dx = (double)to.X - from.X; var dz = (double)to.Y - from.Y;
        return (((double)at.X - from.X) * dx + ((double)at.Y - from.Y) * dz) / (dx * dx + dz * dz);
    }

    private bool TryPortalGridPoint(LayerTriangle before, LayerTriangle after, Connection edge,
        Vector2 from, Vector2 to, float crossing, double minimum, Vector2 rounded, out Vector2 result)
    {
        result = default;
        Span<float> xs = stackalloc float[] { MathF.BitDecrement(rounded.X), rounded.X, MathF.BitIncrement(rounded.X) };
        Span<float> zs = stackalloc float[] { MathF.BitDecrement(rounded.Y), rounded.Y, MathF.BitIncrement(rounded.Y) };
        var gx = Math.Max((double)rounded.X - xs[0], (double)xs[2] - rounded.X);
        var gz = Math.Max((double)rounded.Y - zs[0], (double)zs[2] - rounded.Y);
        var gridSquared = gx * gx + gz * gz;
        // Large-world float spacing is not permission for a larger correction.
        if (!double.IsFinite(gridSquared) || gridSquared > (double)SeamTolerance * SeamTolerance) return false;
        var best = double.PositiveInfinity;
        foreach (var x in xs)
        foreach (var z in zs)
        {
            var at = new Vector2(x, z);
            var fraction = PathFraction(from, to, at);
            if (!double.IsFinite(fraction) || fraction < minimum || fraction > 1
                || Math.Abs(fraction - crossing) > 1e-5) continue;
            if (!SurfaceHeight(before,at, out var firstY) || !SurfaceHeight(after,at, out var secondY)) continue;
            var first = new Vector3(x, firstY, z); var second = new Vector3(x, secondY, z);
            if (!SurfaceContains(before,first) || !SurfaceContains(after,second)
                || !OnPortal(first, edge.ExitA, edge.ExitB) || !OnPortal(second, edge.EntryA, edge.EntryB)) continue;
            var dx = (double)x - rounded.X; var dz = (double)z - rounded.Y;
            var distance = dx * dx + dz * dz;
            if (distance > gridSquared || distance >= best) continue;
            best = distance; result = at;
        }
        return double.IsFinite(best);

        static bool OnPortal(Vector3 point, Vector3 a, Vector3 b)
        {
            var dx = (double)b.X - a.X; var dy = (double)b.Y - a.Y; var dz = (double)b.Z - a.Z;
            var px = (double)point.X - a.X; var py = (double)point.Y - a.Y; var pz = (double)point.Z - a.Z;
            var length = dx * dx + dy * dy + dz * dz;
            if (!double.IsFinite(length) || length <= 1e-12) return false;
            var t = Math.Clamp((px * dx + py * dy + pz * dz) / length, 0, 1);
            px -= dx * t; py -= dy * t; pz -= dz * t;
            return px * px + py * py + pz * pz <= (double)SeamTolerance * SeamTolerance;
        }
    }

    /// <summary>Measure the highest recorded terrain within a compressed cell.
    /// Only fresh faces reached from the actual center through portals inside
    /// the cell participate. A ramp elsewhere cannot pull an overlapping deck
    /// into this proof. The geometry helper additionally requires complete
    /// coverage and rejects locally overlapping, inconsistent heights.</summary>
    public LayerQueryResult TryCellCeiling(LayerFloorHit center, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        out float ceiling, Func<bool>? withinDeadline = null)
    {
        ceiling = 0;
        CellCeilingFailure = "none"; CellCeilingMissingWitness = null; CellCeilingLift = float.NaN;
        var timedOut = false;
        if (!CheckDeadline()) return Fail("wrapper deadline", LayerQueryResult.Pending);
        if (!MathEx.Finite(center.Position) || !SurfaceContains(center.Triangle,center.Position) || !Fresh(center.Triangle) || !connected.Contains(center.Triangle))
            return Fail("wrapper center missing, invalid or expired", LayerQueryResult.Unknown);
        if (!TryNearest(new(center.Position.X, center.Position.Z), out var witness, out _, CheckDeadline))
            return Fail(timedOut ? "wrapper witness deadline" : "wrapper witness unavailable",
                timedOut ? LayerQueryResult.Pending : LayerQueryResult.Unknown);
        if (!SurfaceContains(witness,center.Position)) return Fail("wrapper center differs from local witness", LayerQueryResult.Unknown);
        ceilingFaces.Clear(); ceilingVisited.Clear(); ceilingPending.Clear();
        ceilingVisited.Add(center.Triangle); ceilingPending.Enqueue(center.Triangle);
        var visits = 0;
        while (ceilingPending.TryDequeue(out var face))
        {
            if (!CheckDeadline()) return Fail("wrapper gather deadline", LayerQueryResult.Pending);
            if (!Fresh(face)) return Fail("wrapper gathered face expired", LayerQueryResult.Unknown);
            if (ceilingFaces.Count >= CoplanarCellCoverage.MaximumTriangles) return Fail("wrapper face cap", LayerQueryResult.Unknown);
            ceilingFaces.Add(face);
            if (!links.TryGetValue(face, out var adjacent)) continue;
            foreach (var edge in adjacent)
            {
                if (!CheckDeadline()) return Fail("wrapper portal deadline", LayerQueryResult.Pending);
                if (++visits > MaximumPortalVisits) return Fail("wrapper portal cap", LayerQueryResult.Unknown);
                if (ceilingVisited.Contains(edge.To) || !Fresh(edge.To)) continue;
                var freshRisers = true;
                foreach (var riser in edge.Risers) freshRisers &= Fresh(riser);
                if (!freshRisers
                    || !ClothCellCeiling.PortalIntersectsCell(edge.ExitA, edge.ExitB, a, b, c, d)
                    || !ClothCellCeiling.PortalIntersectsCell(edge.EntryA, edge.EntryB, a, b, c, d)) continue;
                ceilingVisited.Add(edge.To); ceilingPending.Enqueue(edge.To);
            }
        }
        var result = cellCeiling.MeasureSupport(center, a, b, c, d, ceilingFaces, out ceiling, connectorPredicate, CheckDeadline);
        CellCeilingFailure = cellCeiling.LastFailure;
        CellCeilingMissingWitness = cellCeiling.MissingWitness;
        CellCeilingLift = cellCeiling.LastLift;
        return result;

        LayerQueryResult Fail(string reason, LayerQueryResult result)
        { CellCeilingFailure = reason; return result; }

        bool Fresh(LayerTriangle triangle) => observed.TryGetValue(triangle, out var time)
            && now >= time && now - time <= 2;
        bool CheckDeadline()
        {
            if (withinDeadline?.Invoke() == false) timedOut = true;
            return !timedOut;
        }
    }

    /// <summary>Optional true union proof across fresh coplanar face seams.
    /// The actual accepted center chooses the local layer. Flood only direct
    /// same-plane edges, never a riser/ramp path to an overlapping deck. The
    /// scratch union routine is bounded and shares the native query deadline.
    /// A false result retains the conservative per-cell ceiling.</summary>
    public bool ProvesPlanarCell(LayerFloorHit center, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        Func<bool>? withinDeadline = null)
    {
        if (withinDeadline?.Invoke()==false || !center.Valid || !Fresh(center.Triangle)
            || !connected.Contains(center.Triangle)) return false;
        if (!TryNearest(new(center.Position.X,center.Position.Z),out var witness,out _,withinDeadline)
            || !SurfaceHeight(witness,new(center.Position.X,center.Position.Z),out var expected)
            || Math.Abs(expected-center.Position.Y)>LayerFloorHit.PlanarHeightTolerance) return false;
        if (center.ProvesPlanarCell(a,b,c,d)) return true;
        var minimum=Vector2.Min(Vector2.Min(new(a.X,a.Z),new(b.X,b.Z)),Vector2.Min(new(c.X,c.Z),new(d.X,d.Z)));
        var maximum=Vector2.Max(Vector2.Max(new(a.X,a.Z),new(b.X,b.Z)),Vector2.Max(new(c.X,c.Z),new(d.X,d.Z)));
        if (!MathEx.Finite(minimum) || !MathEx.Finite(maximum)) return false;
        planarFaces.Clear(); planarVisited.Clear(); planarPending.Clear();
        planarVisited.Add(center.Triangle); planarPending.Enqueue(center.Triangle);
        var visits=0; var edgesVisited=0;
        while(planarPending.TryDequeue(out var triangle))
        {
            if (++visits>MaximumTriangles || withinDeadline?.Invoke()==false) return false;
            if (!Fresh(triangle) || !triangle.Walkable || !CoplanarCellCoverage.OnPlane(center.Triangle,triangle.A)
                || !CoplanarCellCoverage.OnPlane(center.Triangle,triangle.B) || !CoplanarCellCoverage.OnPlane(center.Triangle,triangle.C)) continue;
            var faceMin=Vector2.Min(new(triangle.A.X,triangle.A.Z),Vector2.Min(new(triangle.B.X,triangle.B.Z),new(triangle.C.X,triangle.C.Z)));
            var faceMax=Vector2.Max(new(triangle.A.X,triangle.A.Z),Vector2.Max(new(triangle.B.X,triangle.B.Z),new(triangle.C.X,triangle.C.Z)));
            if (faceMax.X<minimum.X || faceMax.Y<minimum.Y || faceMin.X>maximum.X || faceMin.Y>maximum.Y) continue;
            if (planarFaces.Count>=CoplanarCellCoverage.MaximumTriangles) return false;
            planarFaces.Add(triangle);
            foreach(var edge in links[triangle])
            {
                if (++edgesVisited>MaximumDirectedPortals || withinDeadline?.Invoke()==false) return false;
                if (edge.Risers.Length==0 && planarVisited.Add(edge.To)) planarPending.Enqueue(edge.To);
            }
        }
        return planarCoverage.Proves(center,a,b,c,d,planarFaces,withinDeadline);

        bool Fresh(LayerTriangle triangle) => observed.TryGetValue(triangle,out var time) && now>=time && now-time<=2;
    }

    public bool TryNearest(Vector2 position, out LayerTriangle triangle, out Vector3 boundary, Func<bool>? withinDeadline = null)
    {
        triangle = default; boundary = default;
        if (!MathEx.Finite(position)) return false;
        if (SurfaceHeight(root,position,out var rootHeight) && SurfaceContains(root,new(position.X,rootHeight,position.Y)))
        { triangle = root; boundary = new(position.X,rootHeight,position.Y); return true; }
        // Follow the root-to-query segment through actual shared portals.
        // A faraway ramp cannot switch floors under this local material
        // window, and arbitrary triangulation density is not a distance.
        var origin = new Vector2(rootPoint.X,rootPoint.Z);
        approach.Clear(); traversal.Clear(); approach[root] = 0; traversal.Enqueue(root);
        var furthest = -1f; var best = float.PositiveInfinity; var visits = 0;
        float? coveredHeight = null; var ambiguous = false;
        while (traversal.TryDequeue(out var candidate))
        {
            if (withinDeadline?.Invoke() == false) { triangle = default; return false; }
            if (!Interval(candidate,origin,position,out var enter,out var exit)) continue;
            var reached = approach[candidate];
            var distance = SurfaceDistance(candidate,position);
            if (distance == 0 && SurfaceHeight(candidate,position,out var height))
            {
                if (coveredHeight is { } previousHeight && Math.Abs(previousHeight-height) > SeamTolerance) ambiguous = true;
                coveredHeight ??= height;
            }
            if (exit > furthest + 1e-5f || Math.Abs(exit-furthest) <= 1e-5f && distance < best)
            { furthest = exit; best = distance; triangle = candidate; }
            if (!links.TryGetValue(candidate,out var adjacent)) continue;
            foreach (var edge in adjacent)
            {
                if (++visits > MaximumPortalVisits || withinDeadline?.Invoke() == false)
                { triangle = default; return false; }
                if (!PortalCrossing(origin,position,edge.ExitA,edge.ExitB,reached,out var cross)
                    || cross > exit + 1e-5f || cross < enter - 1e-5f
                    || !PortalCrossing(origin,position,edge.EntryA,edge.EntryB,cross,out var arrival)
                    || Math.Abs(cross-arrival) > 1e-5f
                    || !Interval(edge.To,origin,position,out var nextEnter,out var nextExit)
                    || nextEnter > cross + 1e-5f || nextExit < cross - 1e-5f) continue;
                if (approach.TryGetValue(edge.To,out var previous) && previous <= cross + 1e-5f) continue;
                approach[edge.To] = cross; traversal.Enqueue(edge.To);
            }
        }
        if (!triangle.Valid || ambiguous) { triangle = default; return false; }
        var witness = triangle;
        if (best == 0 && SurfaceHeight(triangle,position, out var floor))
        { boundary = new(position.X, floor, position.Y); return true; }
        var nearest = float.PositiveInfinity;
        Vector3 foundBoundary = default;
        Find(witness.A, witness.B); Find(witness.B, witness.C); Find(witness.C, witness.A);
        boundary = foundBoundary;
        return true;
        void Find(Vector3 a, Vector3 b)
        {
            var origin = new Vector2(a.X, a.Z); var delta = new Vector2(b.X - a.X, b.Z - a.Z);
            var t = delta.LengthSquared() > 1e-12f ? Math.Clamp(Vector2.Dot(position - origin, delta) / delta.LengthSquared(), 0, 1) : 0;
            var point = Vector3.Lerp(a, b, t); var distance = Vector2.DistanceSquared(position, new(point.X, point.Z));
            if (distance < nearest) { nearest = distance; foundBoundary = point; }
        }
    }

    public bool TryFrontier(Vector2 target, out ClothFloorProbe probe, Func<bool>? withinDeadline = null)
    {
        probe = default; var best = float.PositiveInfinity; var found = default(ClothFloorProbe);
        // Continue only the locally reached face, not a graph-connected deck
        // that happens to be closer in XZ. At most three edges are inspected.
        if (withinDeadline?.Invoke() == false || !TryNearest(target,out var triangle,out _,withinDeadline)) return false;
        {
            if (withinDeadline?.Invoke() == false) return false;
            var centroid = (triangle.A + triangle.B + triangle.C) / 3;
            TryEdge(triangle, triangle.A, triangle.B, centroid);
            TryEdge(triangle, triangle.B, triangle.C, centroid);
            TryEdge(triangle, triangle.C, triangle.A, centroid);
        }
        probe = found; return probe.Valid;
        void TryEdge(LayerTriangle triangle, Vector3 a, Vector3 b, Vector3 center)
        {
            var origin = new Vector2(a.X, a.Z); var delta = new Vector2(b.X - a.X, b.Z - a.Z);
            if (delta.LengthSquared() < 1e-8f) return;
            var t = Math.Clamp(Vector2.Dot(target - origin, delta) / delta.LengthSquared(), .02f, .98f);
            var edge = origin + delta * t;
            var outward = Vector2.Normalize(new Vector2(-delta.Y, delta.X));
            if (Vector2.Dot(outward, new Vector2(center.X, center.Z) - edge) > 0) outward = -outward;
            var at = edge + outward * .005f;
            var score = Vector2.DistanceSquared(target, at);
            if (score >= best || !SurfaceHeight(triangle,at, out var y)) return;
            foreach (var known in connected)
            {
                if (withinDeadline?.Invoke() == false) return;
                if (SurfaceHeight(known,at, out var height) && Math.Abs(height - y) < SeamTolerance && SurfaceContains(known,new(at.X, height, at.Y))) return;
            }
            if (!ClothFloorQueryPolicy.TryLocalProbe(at, y, out var candidate)) return;
            best = score; found = candidate;
        }
    }

    public bool TryProbe(Vector2 position, out ClothFloorProbe probe)
    {
        probe = default;
        if (!MathEx.Finite(position) || !TryNearest(position, out var triangle, out _)
            || SurfaceDistance(triangle,position) > MaximumQueryAdvance || !SurfaceHeight(triangle,position, out var height)) return false;
        // Identical local reach to the existing policy, but anchored to actual
        // nearby connected geometry, not one horizontal player-height plane.
        return ClothFloorQueryPolicy.TryLocalProbe(position, height, out probe);
    }

    public bool Accept(ClothFloorProbe probe, LayerFloorHit candidate, ReadOnlySpan<LayerTriangle> measuredRisers = default)
    {
        if (!candidate.Valid || measuredRisers.Length > 4
            || !ClothFloorQueryPolicy.Accept(probe, candidate.Position, candidate.Triangle.Normal, out _)) return false;
        if (!TryNearest(probe.Position, out var witness, out _) || !SurfaceHeight(witness,probe.Position, out var expected)) return false;
        // Membership in the graph is not layer evidence: an upper deck can be
        // connected by a faraway ramp. Continue the locally chosen approach.
        // If that witness already covers this XZ, another height cannot replace
        // it merely because the downward ray hit that other floor first.
        if (SurfaceContains(witness,new(probe.Position.X, expected, probe.Position.Y))
            && Math.Abs(candidate.Position.Y - expected) > SeamTolerance) return false;
        if (candidate.Triangle == witness) { observed[candidate.Triangle] = now; return true; }
        var known = connected.Contains(candidate.Triangle);
        // At a shared vertex the native ray may return a different recorded
        // face than the local approach selected. The real hit still lies on
        // that locally proved floor. Refresh this endpoint without inventing
        // a point-only graph edge or admitting an unmeasured triangle.
        if (known && SurfaceContains(witness,candidate.Position))
        { observed[candidate.Triangle] = now; return true; }
        if (!known && (connected.Count >= MaximumTriangles || portalCount + 2 > MaximumDirectedPortals)) return false;
        var floor = witness;
        if (SharesBoundary(floor, candidate.Triangle))
        {
            if (!known) return Add(floor, candidate.Triangle, []);
            observed[candidate.Triangle] = now;
            return true;
        }
        // The connector role accounts for a positive-XZ surface interval and
        // must never inherit the legacy wall-riser clearance-skip authority.
        foreach (var riser in measuredRisers) if (IsConnector(riser)) return false;
        var mask = CurbConnects(floor, candidate.Triangle, measuredRisers);
        if (mask != 0)
        {
            var newRisers = 0;
            for (var i = 0; i < measuredRisers.Length; i++)
                if ((mask & (1 << i)) != 0 && !curbFaces.Contains(measuredRisers[i])) newRisers++;
            if (curbFaces.Count + newRisers > MaximumRiserFaces) return false;
            for (var i = 0; i < measuredRisers.Length; i++)
                if ((mask & (1 << i)) != 0)
                {
                    if (!curbFaces.Contains(measuredRisers[i])) curbFaces.Add(measuredRisers[i]);
                    observed[measuredRisers[i]] = now;
                }
            if (!known)
            {
                var used = new List<LayerTriangle>();
                for (var i = 0; i < measuredRisers.Length; i++) if ((mask & (1 << i)) != 0) used.Add(measuredRisers[i]);
                return Add(floor, candidate.Triangle, used.ToArray());
            }
            else observed[candidate.Triangle] = now;
            return true;
        }
        return false;
    }

    private bool Add(LayerTriangle parent, LayerTriangle child, LayerTriangle[] risers)
    {
        if (portalCount + 2 > MaximumDirectedPortals) return false;
        Vector3 exitA = default, exitB = default, entryA = default, entryB = default;
        if (risers.Length == 0) { TryPortal(parent,child,out exitA,out exitB); entryA = exitA; entryB = exitB; }
        else
        {
            foreach (var face in risers) if (TryPortal(parent,face,out exitA,out exitB)) break;
            foreach (var face in risers) if (TryPortal(child,face,out entryA,out entryB)) break;
        }
        links[parent].Add(new(child,risers,exitA,exitB,entryA,entryB));
        links[child] = [new(parent,risers,entryA,entryB,exitA,exitB)];
        portalCount += 2;
        // Retain all actual neighboring portals. A first-discovery tree alone
        // can force a detour around a fan of triangles even on a flat floor.
        foreach (var other in connected)
        {
            if (portalCount + 2 > MaximumDirectedPortals) break;
            if (other == parent || !TryPortal(other,child,out var a,out var b)) continue;
            links[other].Add(new(child,[],a,b,a,b)); links[child].Add(new(other,[],a,b,a,b));
            portalCount += 2;
        }
        connected.Add(child); observed[child] = now;
        return true;
    }

    private static bool Interval(LayerTriangle triangle, Vector2 from, Vector2 to, out float enter, out float exit)
    {
        enter = 0; exit = 1;
        var a = new Vector2(triangle.A.X,triangle.A.Z); var b = new Vector2(triangle.B.X,triangle.B.Z);
        var c = new Vector2(triangle.C.X,triangle.C.Z); var determinant = Cross(b-a,c-a);
        if (Math.Abs(determinant) < 1e-10) return false;
        var u0 = Cross(from-a,c-a)/determinant; var v0 = Cross(b-a,from-a)/determinant;
        var u1 = Cross(to-a,c-a)/determinant; var v1 = Cross(b-a,to-a)/determinant;
        return Clip(u0,u1,ref enter,ref exit) && Clip(v0,v1,ref enter,ref exit)
            && Clip(1-u0-v0,1-u1-v1,ref enter,ref exit);
        static bool Clip(double start,double end,ref float low,ref float high)
        {
            var change = end-start;
            if (Math.Abs(change) < 1e-12) return start >= -1e-6;
            var crossing = (float)(-start/change);
            if (change > 0) low = Math.Max(low,crossing); else high = Math.Min(high,crossing);
            return low <= high + 1e-5f;
        }
    }

    private static bool PortalCrossing(Vector2 from,Vector2 to,Vector3 edgeA,Vector3 edgeB,float minimum,out float t)
    {
        t = 0;
        var direction = to-from; var a = new Vector2(edgeA.X,edgeA.Z); var b = new Vector2(edgeB.X,edgeB.Z);
        var edge = b-a; var denominator = Cross(direction,edge);
        if (Math.Abs(denominator) > 1e-10)
        {
            t = (float)(Cross(a-from,edge)/denominator);
            var u = Cross(a-from,direction)/denominator;
            return t >= minimum-1e-5f && t <= 1+1e-5f && u >= -1e-5 && u <= 1.00001;
        }
        if (direction.LengthSquared() < 1e-12f || Math.Abs(Cross(a-from,direction)) > 1e-6) return false;
        var first = Vector2.Dot(a-from,direction)/direction.LengthSquared();
        var last = Vector2.Dot(b-from,direction)/direction.LengthSquared();
        t = Math.Max(minimum,Math.Min(first,last));
        return t <= Math.Min(1,Math.Max(first,last))+1e-5f;
    }
    private static double Cross(Vector2 a,Vector2 b) => (double)a.X*b.Y-(double)a.Y*b.X;

    private static int CurbConnects(LayerTriangle from, LayerTriangle to, ReadOnlySpan<LayerTriangle> risers)
    {
        if (!from.Walkable || !to.Walkable || risers.IsEmpty) return 0;
        Span<int> paths = stackalloc int[4];
        var minimum = float.PositiveInfinity; var maximum = float.NegativeInfinity;
        for (var i = 0; i < risers.Length; i++)
        {
            var face = risers[i];
            if (!face.Valid || Math.Abs(face.Normal.Y) > .1f) return 0;
            minimum = Math.Min(minimum, face.MinimumY); maximum = Math.Max(maximum, face.MaximumY);
        }
        if (maximum - minimum > MaximumCurbHeight + SeamTolerance) return 0;
        // A curb must attach lower/upper WALKABLE faces at the riser's height
        // range; neither a ceiling side nor an unrelated nearby wall suffices.
        if (from.MinimumY > maximum + SeamTolerance || from.MaximumY < minimum - SeamTolerance
            || to.MinimumY > maximum + SeamTolerance || to.MaximumY < minimum - SeamTolerance) return 0;
        for (var pass = 0; pass < risers.Length; pass++)
        for (var i = 0; i < risers.Length; i++)
        {
            if (paths[i] == 0)
            {
                if (SharesBoundary(from, risers[i])) paths[i] = 1 << i;
                for (var j = 0; j < risers.Length && paths[i] == 0; j++)
                    if (paths[j] != 0 && SharesBoundary(risers[j], risers[i])) paths[i] = paths[j] | (1 << i);
            }
            if (paths[i] != 0 && SharesBoundary(risers[i], to)) return paths[i];
        }
        return 0;
    }

    public static bool SharesBoundary(LayerTriangle a, LayerTriangle b) => TryPortal(a,b,out _,out _);

    private static bool TryPortal(LayerTriangle a, LayerTriangle b, out Vector3 start, out Vector3 end)
    {
        start = end = default;
        if (!a.Valid || !b.Valid) return false;
        Span<Vector3> av = stackalloc Vector3[] { a.A, a.B, a.C };
        Span<Vector3> bv = stackalloc Vector3[] { b.A, b.B, b.C };
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            if (Overlaps(av[i], av[(i + 1) % 3], bv[j], bv[(j + 1) % 3],out start,out end)) return true;
        return false;
    }

    private static bool Overlaps(Vector3 a, Vector3 b, Vector3 c, Vector3 d,out Vector3 first,out Vector3 last)
    {
        first = last = default;
        var direction = b - a; var length = direction.Length();
        if (length <= 2 * SeamTolerance) return false;
        direction /= length;
        var cDistance = Vector3.Dot(c - a, direction); var dDistance = Vector3.Dot(d - a, direction);
        if (Vector3.DistanceSquared(c, a + direction * cDistance) > SeamTolerance * SeamTolerance
            || Vector3.DistanceSquared(d, a + direction * dDistance) > SeamTolerance * SeamTolerance) return false;
        var start = Math.Max(0,Math.Min(cDistance,dDistance)); var end = Math.Min(length,Math.Max(cDistance,dDistance));
        if (end-start <= 2*SeamTolerance) return false;
        first = a + direction * start; last = a + direction * end; return true;
    }
}
