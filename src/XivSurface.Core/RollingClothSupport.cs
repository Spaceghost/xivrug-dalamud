using System.Numerics;

namespace XivSurface.Core;

/// <summary>Exact provenance for queries in one fixed world-space support patch.
/// Layer/revision are explicit caller invalidations, not an invented guarantee
/// that the engine's dynamic collision scene has not changed.</summary>
public readonly record struct SupportQueryIdentity(uint Zone, long GeometryGeneration, long Layer,
    Vector3 CompressionOrigin, float DownwardStart, float DownwardLength)
{
    public bool Valid => Zone != 0 && GeometryGeneration >= 0 && Layer >= 0
        && MathEx.Finite(CompressionOrigin) && float.IsFinite(DownwardStart)
        && float.IsFinite(DownwardLength) && DownwardLength > 0;
}

/// <summary>Runtime adapter must perform the original two rays for each vertex
/// (wall compression, then downward floor), and the original downward cell
/// midpoint query. False is unknown: never substitute the patch anchor.</summary>
public interface IClothSupportQueries
{
    bool TryVertex(SupportQueryIdentity identity, Vector2 nominal, out Vector3 contact);
    bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling);
}

/// <summary>Optional adapter for resumable multi-ray discovery. Pending work
/// retains old evidence until normal expiry; Unknown invalidates it. The
/// adapter must enforce the supplied call allowance AND its wall-clock limit.</summary>
public interface IIncrementalClothSupportQueries
{
    int Raycasts { get; }
    LayerQueryResult LastResult { get; }
    void PrepareQuery(int rayAllowance);
}

/// <summary>Optional proof supplied by the successful TryCell call, never
/// inferred by the cache from equal sample heights. False is conservative.</summary>
public interface IPlanarClothSupportQueries
{
    bool LastCellPlanar { get; }
}

/// <summary>Optional measured residual above both possible contact-triangle
/// diagonals. NaN means no conformal proof: retain the ordinary cell ceiling.</summary>
public interface IConformalClothSupportQueries
{
    float LastCellLift { get; }
}

/// <summary>An immutable collision-input publication, not a new floor proof.
/// Global cell parity must be retained when generating its fine visual mesh.
/// Support interpolation has the same finite-sample limits as the existing cloth.
/// Final-world foot exclusion, scene depth and cell ceilings remain mandatory.</summary>
public sealed record RollingSupportSnapshot(SupportQueryIdentity Identity, int MinX, int MinZ,
    int Width, int Height, float Spacing, Vector3[] Contacts, float[] CellCeilings, bool[]? PlanarCells = null,
    float[]? CellLifts = null)
{
    public Vector2 Center => new((MinX + (Width - 1) * .5f) * Spacing,
        (MinZ + (Height - 1) * .5f) * Spacing);
    public Vector2 Half => new((Width - 1) * .5f * Spacing, (Height - 1) * .5f * Spacing);
    public int DiagonalParity => (MinX + MinZ) & 1;

    /// <summary>Mismatch guard against a separately measured current-center
    /// floor ray. This does NOT replace that ray or prove unsampled geometry.
    /// Reject any overlapping contact triangle belonging to a different height.
    /// </summary>
    public bool MatchesCenterFloor(Vector2 position, float freshHeight)
    {
        if (!MathEx.Finite(position) || !float.IsFinite(freshHeight) || Width is < 2 or > 129 || Height is < 2 or > 129
            || Contacts.Length != Width * Height) return false;
        var found = false;
        for (var z = 0; z < Height - 1; z++)
        for (var x = 0; x < Width - 1; x++)
        {
            var a = z * Width + x; var b = a + 1; var c = a + Width; var d = c + 1;
            if (((x + z + DiagonalParity) & 1) == 0)
            { if (!Match(a, c, b) || !Match(b, c, d)) return false; }
            else if (!Match(a, d, b) || !Match(a, c, d)) return false;
        }
        return found;
        bool Match(int ia, int ib, int ic)
        {
            var a = Contacts[ia]; var b = Contacts[ib]; var c = Contacts[ic];
            if (!MathEx.Finite(a) || !MathEx.Finite(b) || !MathEx.Finite(c)) return false;
            var v0 = new Vector2(b.X - a.X, b.Z - a.Z); var v1 = new Vector2(c.X - a.X, c.Z - a.Z);
            var v2 = position - new Vector2(a.X, a.Z);
            var denominator = Cross(v0, v1);
            if (Math.Abs(denominator) < 1e-8) return true;
            var u = Cross(v2, v1) / denominator; var v = Cross(v0, v2) / denominator;
            if (u < -.0001 || v < -.0001 || u + v > 1.0001) return true;
            found = true;
            return Math.Abs(a.Y + (b.Y - a.Y) * u + (c.Y - a.Y) * v - freshHeight) <= .35;
        }
        static double Cross(Vector2 a, Vector2 b) => (double)a.X * b.Y - (double)a.Y * b.X;
    }
}

public readonly record struct RollingSupportStatus(int Rays, bool CurrentCovered, bool Reanchoring,
    int ResidentSamples, int OriginGeneration);

/// <summary>Rolling collision cache. No center-latched batch and no
/// guessed triangles: every new vertex and cell receives the original queries.
/// At most two fixed-origin generations coexist. Origin refresh has an explicit
/// budget and may fail to keep up; a missing current window returns false.
/// Time-to-live is an engineering freshness bound, NOT dynamic-scene immunity.</summary>
public sealed class RollingClothSupport
{
    private readonly record struct VertexKey(int X, int Z);
    private readonly record struct CellKey(int X, int Z);
    private sealed class Vertex
    {
        public Vector3 Contact;
        public bool Known;
        public double At = double.NegativeInfinity, Retry;
        public int Revision;
        public double AttemptedAt = double.NegativeInfinity, PendingAt = double.NegativeInfinity;
    }
    private sealed class Cell
    {
        public float Ceiling;
        public float Lift = float.NaN;
        public bool Known, Planar;
        public double At = double.NegativeInfinity, Retry;
        public int A, B, C, D;
        public double AttemptedAt = double.NegativeInfinity, PendingAt = double.NegativeInfinity;
    }
    private sealed class Generation(SupportQueryIdentity identity, int number)
    {
        public readonly SupportQueryIdentity Identity = identity;
        public double OpportunityAt = double.NegativeInfinity;
        public bool CellFirst;
        public Vertex? VertexContinuation;
        public int VertexContinuationAttempts;
        public double VertexContinuationStarted;
        public Cell? CellContinuation;
        public int CellContinuationAttempts;
        public double CellContinuationStarted;
        // A completed/expired burst owes the other dependency class one
        // opportunity before another burst can monopolize a short frame.
        public bool? ContinuationTurnIsCell;
        public bool WorkAttempted;
        public readonly int Number = number;
        public readonly Dictionary<VertexKey, Vertex> Vertices = [];
        public readonly Dictionary<CellKey, Cell> Cells = [];
        public readonly List<KeyValuePair<VertexKey, Vertex>> VertexWork = [];
        public readonly List<KeyValuePair<CellKey, Cell>> CellWork = [];
        public readonly List<VertexKey> RetireVertices = [];
        public readonly List<CellKey> RetireCells = [];
    }
    private readonly record struct Bounds(int X0, int Z0, int X1, int Z1)
    {
        public bool Contains(int x, int z) => x >= X0 && x <= X1 && z >= Z0 && z <= Z1;
    }

    private readonly float spacing, half, halo;
    private readonly double ttl;
    private Generation? active, pending;
    private int generation;
    private double clock = double.NegativeInfinity;
    private Generation? handoffOwner;
    private int handoffOpportunities;
    private double handoffOpportunityAt = double.NegativeInfinity;
    public SupportQueryIdentity? ActiveIdentity => active?.Identity;
    public SupportQueryIdentity? PendingIdentity => pending?.Identity;
    public bool Reanchoring => pending is not null;

    public RollingClothSupport(float spacing = .4f, float half = 2, float halo = .5f, double ttl = 2)
    {
        if (!float.IsFinite(spacing) || spacing is < .1f or > 8
            || !float.IsFinite(half) || half is < .1f or > 30
            || !float.IsFinite(halo) || halo is < 0 or > 2
            || !double.IsFinite(ttl) || ttl is <= 0 or > 5
            || (half + halo) * 2 / spacing > 64) throw new ArgumentOutOfRangeException(nameof(spacing));
        this.spacing = spacing; this.half = half; this.halo = halo; this.ttl = ttl;
    }

    public void Reset()
    { active = pending = handoffOwner = null; clock = handoffOpportunityAt = double.NegativeInfinity; handoffOpportunities = 0; }

    public RollingSupportStatus Update(SupportQueryIdentity requestedOrigin, Vector2 desired, double now,
        int rayBudget, IClothSupportQueries queries, Func<bool>? withinDeadline = null, int originReserve = 45,
        bool preferRequestedOrigin = false, bool replacePendingOrigin = false, bool allowPendingPromotion = true)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (!requestedOrigin.Valid || !Valid(desired) || !double.IsFinite(now) || now < 0
            || rayBudget is < 0 or > 10000 || originReserve < 0) { Reset(); return default; }
        if (now < clock) Reset();
        clock = now;
        if (active is null) active = new(requestedOrigin, ++generation);
        if (requestedOrigin != active.Identity)
        {
            // Zone/layer/scene-revision changes prohibit showing the old patch.
            // An origin-only rebase may keep displaying old actual-world data
            // while it is still fresh and covers the current desired rectangle.
            if (!SameWorld(active.Identity, requestedOrigin))
            { active = new(requestedOrigin, ++generation); pending = null; }
            else if (pending is null || replacePendingOrigin && pending.Identity != requestedOrigin)
            { pending = new(requestedOrigin, ++generation); handoffOwner = null; }
            // Do not continuously abandon a partially sampled origin simply
            // because the caller's new center moved again. A completed pending
            // origin can be superseded on the next update. Explicit replacement
            // is reserved for an obsolete/unreachable pending origin, not every
            // moving target. All of its old evidence is discarded on replacement.
        }
        else pending = null;
        var visible = Region(desired, half); var padded = Region(desired, half + halo);
        Ensure(active, padded); if (pending is not null) Ensure(pending, padded);
        var spent = 0;
        if (pending is not null)
        {
            var reserve = Math.Min(rayBudget, originReserve);
            if (preferRequestedOrigin)
            {
                // An ineligible old origin must never consume the replacement's
                // wall-clock opportunity, even when its ray count is small.
                handoffOwner = null;
                spent += Work(pending, desired, visible, now, rayBudget, queries, withinDeadline);
                spent += Work(pending, desired, visible, now, rayBudget - spent, queries, withinDeadline);
            }
            else
            {
                // A ray reservation is NOT a time reservation. Share first
                // opportunities while both origins remain eligible. Due visible
                // maintenance gets bounded2:1 weighting(6 vs3 opportunities),
                // not a promise that an overloaded scene can fit its TTL.
                var first = HandoffFirst(active, pending, visible, now);
                var second = ReferenceEquals(first, active) ? pending : active;
                // Even a zero bulk reservation cannot erase the selected
                // opportunity. Two is the vertex admission minimum, capped
                // by the actual total budget (one-ray cells still work).
                Serve(first, Math.Min(rayBudget, Math.Max(2,
                    ReferenceEquals(first, pending) ? reserve : rayBudget - reserve)));
                Serve(second, rayBudget - spent);
                Serve(first, rayBudget - spent);

                void Serve(Generation source, int allowance)
                {
                    // Finish an existing finite lease in ONE callback. Otherwise
                    // completing it then starting a new lease in the same Work
                    // call could conceal the yield point indefinitely.
                    var finish = EligibleContinuation(source, visible, now);
                    spent += Work(source, desired, visible, now, allowance, queries, withinDeadline,
                        finish ? 1 : int.MaxValue);
                    if (!source.WorkAttempted) return;
                    // Pure expensive attempts count too. A zero-ray Pending can
                    // spend the entire deadline and is still an opportunity.
                    if (!ReferenceEquals(handoffOwner, source))
                    { handoffOwner = source; handoffOpportunities = 1; handoffOpportunityAt = now; }
                    else if (handoffOpportunityAt != now)
                    { handoffOpportunities++; handoffOpportunityAt = now; }
                }
            }
            // Coverage is not current-center wall clearance. The native caller
            // may prepare a replacement while deferring promotion until that
            // independent gate succeeds. Both generations retain ordinary TTL.
            if (allowPendingPromotion && Covered(pending, visible, now)) { active = pending; pending = null; }
        }
        else { handoffOwner = null; spent = Work(active, desired, visible, now, rayBudget, queries, withinDeadline); }
        Retire(active, Region(desired, half + halo + spacing));
        if (pending is not null) Retire(pending, Region(desired, half + halo + spacing));
        return new(spent, Covered(active, visible, now), pending is not null,
            active.Vertices.Count + active.Cells.Count + (pending?.Vertices.Count ?? 0) + (pending?.Cells.Count ?? 0), active.Number);
    }

    public bool TrySnapshot(Vector2 desired, double now, out RollingSupportSnapshot? snapshot,
        SupportQueryIdentity? requiredIdentity = null)
    {
        snapshot = null;
        if (active is null || !Valid(desired) || !double.IsFinite(now) || now < clock
            || requiredIdentity is { } required && active.Identity != required) return false;
        var region = Region(desired, half);
        if (!Covered(active, region, now)) return false;
        snapshot = Snapshot(active, region);
        return true;
    }

    /// <summary>Fully confirmed square with the largest usable radius around
    /// the requested center, then largest lattice area. A compact patch requires all
    /// current vertex and cell evidence; no missing floor is interpolated.
    /// An explicit identity may select the pending origin without promoting it.
    /// The caller must verify that origin still reaches the current center and
    /// map the complete material into these returned physical bounds.</summary>
    public bool TrySupportedSnapshot(Vector2 desired, double now, out RollingSupportSnapshot? snapshot,
        SupportQueryIdentity? requiredIdentity = null)
    {
        snapshot = null;
        if (active is null || !Valid(desired) || !double.IsFinite(now) || now < clock) return false;
        var source = requiredIdentity is not { } identity || active.Identity == identity ? active
            : pending?.Identity == identity ? pending : null;
        if (source is null) return false;
        var region = Region(desired, half);
        var width = region.X1 - region.X0;
        // Largest all-confirmed square ending at each cell, in O(cells) work.
        // Every smaller square at the same end is contained by that square:
        // if this one cannot contain the center, none of those can either;
        // shrinking it also cannot improve the centered usable radius.
        var previous = new int[width + 1];
        var current = new int[width + 1];
        var best = default(Bounds); var bestSize = 0;
        var bestRadius = 0f; var bestDistance = float.PositiveInfinity;
        for (var z = region.Z0; z < region.Z1; z++)
        {
            current[0] = 0;
            for (var x = region.X0; x < region.X1; x++)
            {
                var column = x - region.X0 + 1;
                var key = new CellKey(x, z);
                var size = source.Cells.TryGetValue(key, out var cell) && CellFresh(source, key, cell, now)
                    ? 1 + Math.Min(current[column - 1], Math.Min(previous[column - 1], previous[column])) : 0;
                current[column] = size;
                if (size < 2) continue;
                var candidate = new Bounds(x + 1 - size, z + 1 - size, x + 1, z + 1);
                if (desired.X <= candidate.X0 * spacing || desired.X >= candidate.X1 * spacing
                    || desired.Y <= candidate.Z0 * spacing || desired.Y >= candidate.Z1 * spacing) continue;
                var center = new Vector2((candidate.X0 + candidate.X1) * .5f * spacing,
                    (candidate.Z0 + candidate.Z1) * .5f * spacing);
                var radius = Math.Min(Math.Min(desired.X - candidate.X0 * spacing, candidate.X1 * spacing - desired.X),
                    Math.Min(desired.Y - candidate.Z0 * spacing, candidate.Z1 * spacing - desired.Y));
                var distance = Vector2.DistanceSquared(center, desired);
                if (radius < bestRadius || radius == bestRadius && (size < bestSize
                    || size == bestSize && distance >= bestDistance)) continue;
                best = candidate; bestSize = size; bestRadius = radius; bestDistance = distance;
            }
            (previous, current) = (current, previous);
        }
        if (bestSize < 2) return false;
        snapshot = Snapshot(source, best);
        return true;
    }

    private RollingSupportSnapshot Snapshot(Generation source, Bounds region)
    {
        var width = region.X1 - region.X0 + 1; var height = region.Z1 - region.Z0 + 1;
        var contacts = new Vector3[width * height]; var ceilings = new float[(width - 1) * (height - 1)];
        var planar = new bool[ceilings.Length];
        var lifts = new float[ceilings.Length]; var anyLift = false;
        for (var z = 0; z < height; z++)
        for (var x = 0; x < width; x++)
        {
            contacts[z * width + x] = source.Vertices[new(region.X0 + x, region.Z0 + z)].Contact;
            if (x < width - 1 && z < height - 1)
            {
                var cell = source.Cells[new(region.X0+x,region.Z0+z)];
                var index = z * (width - 1) + x;
                ceilings[index] = cell.Ceiling; planar[index] = cell.Planar;
                lifts[index] = cell.Lift; anyLift |= float.IsFinite(cell.Lift);
            }
        }
        return new(source.Identity, region.X0, region.Z0, width, height, spacing, contacts, ceilings,planar,
            anyLift ? lifts : null);
    }

    private Generation HandoffFirst(Generation current, Generation requested, Bounds visible, double now)
    {
        var due = VisibleWorkDue(current, visible, now);
        var maintenance = due && Covered(current, visible, now);
        if (!ReferenceEquals(handoffOwner, current) && !ReferenceEquals(handoffOwner, requested))
            return maintenance ? current : requested;
        if (ReferenceEquals(handoffOwner, current) && !due) return requested;
        // Weight preservation of a CURRENT complete window. Once it expires
        // or loses a dependency, rebuilding it must not indefinitely compete
        // at maintenance weight with a viable complete replacement.
        var quota = ReferenceEquals(handoffOwner, current) ? maintenance ? 6 : 1 : 3;
        // A floor measured on the last quota opportunity still gets its two
        // existing riser continuations. Never create a longer evidence lease.
        if (handoffOpportunities < quota || handoffOpportunities < quota + 2
            && EligibleContinuation(handoffOwner!, visible, now)) return handoffOwner!;
        return ReferenceEquals(handoffOwner, requested) && due ? current : requested;
    }

    private bool VisibleWorkDue(Generation source, Bounds visible, double now)
    {
        foreach (var (key, vertex) in source.Vertices)
            if (visible.Contains(key.X, key.Z) && now >= vertex.Retry && vertex.PendingAt != now
                && (!Fresh(vertex, now) || now - vertex.At > ttl * .55)) return true;
        foreach (var (key, cell) in source.Cells)
            if (visible.Contains(key.X, key.Z) && visible.Contains(key.X + 1, key.Z + 1)
                && now >= cell.Retry && cell.PendingAt != now
                && (!CellFresh(source, key, cell, now) || now - cell.At > ttl * .55)
                && CornersFresh(source, key, now)) return true;
        return false;
    }

    private bool EligibleContinuation(Generation source, Bounds visible, double now)
    {
        if (source.VertexContinuation is { } vertex && now >= source.VertexContinuationStarted
            && now - source.VertexContinuationStarted <= .1 && source.VertexContinuationAttempts < 3)
        {
            var ownerPriority = int.MaxValue; var best = int.MaxValue;
            foreach (var (key, candidate) in source.Vertices)
            {
                if (now < candidate.Retry || candidate.PendingAt == now
                    || Fresh(candidate, now) && now - candidate.At <= ttl * .55) continue;
                var priority = visible.Contains(key.X, key.Z) ? Fresh(candidate, now) ? 1 : 0 : 2;
                best = Math.Min(best, priority);
                if (ReferenceEquals(candidate, vertex)) ownerPriority = priority;
            }
            if (ownerPriority != int.MaxValue && ownerPriority == best) return true;
        }
        if (source.CellContinuation is { } cell && now >= source.CellContinuationStarted
            && now - source.CellContinuationStarted <= .1 && source.CellContinuationAttempts < 3)
        {
            var ownerPriority = int.MaxValue; var best = int.MaxValue;
            foreach (var (key, candidate) in source.Cells)
            {
                if (now < candidate.Retry || candidate.PendingAt == now
                    || CellFresh(source, key, candidate, now) && now - candidate.At <= ttl * .55
                    || !CornersFresh(source, key, now)) continue;
                var priority = visible.Contains(key.X, key.Z) ? CellFresh(source, key, candidate, now) ? 1 : 0 : 2;
                best = Math.Min(best, priority);
                if (ReferenceEquals(candidate, cell)) ownerPriority = priority;
            }
            if (ownerPriority != int.MaxValue && ownerPriority == best) return true;
        }
        return false;
    }

    private bool CornersFresh(Generation source, CellKey key, double now)
        => Fresh(source.Vertices[new(key.X, key.Z)], now) && Fresh(source.Vertices[new(key.X + 1, key.Z)], now)
            && Fresh(source.Vertices[new(key.X, key.Z + 1)], now) && Fresh(source.Vertices[new(key.X + 1, key.Z + 1)], now);

    private int Work(Generation source, Vector2 desired, Bounds visible, double now, int budget,
        IClothSupportQueries queries, Func<bool>? withinDeadline, int maximumWorkAttempts = int.MaxValue)
    {
        var spent = 0; var workAttempts = 0; source.WorkAttempted = false;
        // Give each dependency class the first wall-clock opportunity on
        // alternating updates. Ray reservations cannot stop a single native
        // call from consuming the remaining CPU deadline. Only one ready cell
        // goes first; outer vertex discovery receives the next opportunity.
        if (source.OpportunityAt != now)
        { source.OpportunityAt = now; source.CellFirst = !source.CellFirst; }
        // The actual discovery machine needs floor + two riser rays before
        // its 100 ms candidate expires. Permit a short, bounded continuation
        // burst, not perpetual nearest-point priority or a longer evidence TTL.
        if (source.VertexContinuation is not null
            && (now < source.VertexContinuationStarted || now - source.VertexContinuationStarted > .1))
            EndVertexContinuation();
        if (source.CellContinuation is not null
            && (now < source.CellContinuationStarted || now - source.CellContinuationStarted > .1))
            EndCellContinuation();
        var incremental = queries as IIncrementalClothSupportQueries;
        var vertices = source.VertexWork; vertices.Clear();
        foreach (var pair in source.Vertices)
            if (now >= pair.Value.Retry && pair.Value.PendingAt != now && (!Fresh(pair.Value, now) || now - pair.Value.At > ttl * .55)) vertices.Add(pair);
        vertices.Sort((a, b) =>
        {
            var priority = VertexPriority(a).CompareTo(VertexPriority(b));
            if (priority != 0) return priority;
            var continuation = ReferenceEquals(b.Value, source.VertexContinuation)
                .CompareTo(ReferenceEquals(a.Value, source.VertexContinuation));
            if (continuation != 0) return continuation;
            // Renew expired, previously proven support once before a large
            // unseen fringe. A pending renewal consumes this opportunity
            // without refreshing At, so it cannot become a priority lock.
            var renewal = NeedsFirstRenewal(b.Value).CompareTo(NeedsFirstRenewal(a.Value));
            if (renewal != 0) return renewal;
            // Discovery at one nearby vertex may consume the whole deadline.
            // Give other equally urgent vertices a turn before resuming it;
            // distance alone would retry that same vertex first indefinitely.
            var age = a.Value.AttemptedAt.CompareTo(b.Value.AttemptedAt);
            return age != 0 ? age : Compare(0, 0, a.Key.X, a.Key.Z, b.Key.X, b.Key.Z);
        });
        // Reserve a portion for dependent midpoint work: filling every halo
        // vertex first otherwise starves a moving visible window's cell query.
        var vertexLimit = Math.Max(2, budget * 2 / 3);
        // Midpoint or missing-witness discovery needs the same finite native
        // sequence as a vertex. At most ONE class owns a burst; a cell must
        // still be the first eligible cell with fresh corner dependencies.
        if (source.CellContinuation is not null) QueryCells(1, continuationOnly: true);
        // An eligible short continuation must also precede alternating cells:
        // inserting whole-frame cell work between its riser rays could age
        // the measured candidate out before ray three. A stale/retired/less
        // urgent continuation pointer does not suppress ready cell service.
        if (vertices.Count > 0 && ReferenceEquals(vertices[0].Value, source.VertexContinuation))
            QueryVertices(vertexLimit, maximumAttempts: 1);
        if (source.VertexContinuation is null && source.CellContinuation is null
            && source.ContinuationTurnIsCell is { } cellTurn)
        {
            // Eligibility is independent of the ray reservation: a one-ray
            // cell budget must not retain debt to nonexistent vertex work.
            if (!HasEligibleWork(cellTurn) || budget < (cellTurn ? 1 : 2))
                source.ContinuationTurnIsCell = null;
            else if (spent + (cellTurn ? 1 : 2) <= budget && withinDeadline?.Invoke() != false)
            {
                if (cellTurn) QueryCells(1); else QueryVertices(budget, 1);
            }
        }
        if (source.CellFirst && budget >= 3) QueryCells(1);
        QueryVertices(vertexLimit);
        QueryCells();
        QueryVertices(budget);
        QueryCells();
        return spent;

        int VertexPriority(KeyValuePair<VertexKey, Vertex> p) => visible.Contains(p.Key.X, p.Key.Z)
            ? Fresh(p.Value, now) ? 1 : 0 : 2;
        bool NeedsFirstRenewal(Vertex vertex) => vertex.Known && !Fresh(vertex, now) && vertex.AttemptedAt <= vertex.At;
        int CellPriority(KeyValuePair<CellKey, Cell> p) => visible.Contains(p.Key.X, p.Key.Z)
            ? CellFresh(source, p.Key, p.Value, now) ? 1 : 0 : 2;
        int Compare(int aPriority, int bPriority, int ax, int az, int bx, int bz)
        {
            var priority = aPriority.CompareTo(bPriority);
            return priority != 0 ? priority : Vector2.DistanceSquared(new(ax * spacing, az * spacing), desired)
                .CompareTo(Vector2.DistanceSquared(new(bx * spacing, bz * spacing), desired));
        }

        void EndVertexContinuation()
        { source.VertexContinuation = null; source.ContinuationTurnIsCell = true; }
        void EndCellContinuation()
        { source.CellContinuation = null; source.ContinuationTurnIsCell = false; }

        bool HasEligibleWork(bool cells)
        {
            if (!cells)
            {
                foreach (var pair in vertices)
                {
                    var v = pair.Value;
                    if (now >= v.Retry && v.PendingAt != now
                        && (!Fresh(v, now) || now - v.At > ttl * .55)) return true;
                }
                return false;
            }
            foreach (var (key, cell) in source.Cells)
                if (now >= cell.Retry && cell.PendingAt != now
                    && (!CellFresh(source, key, cell, now) || now - cell.At > ttl * .55)
                    && Fresh(source.Vertices[new(key.X, key.Z)], now)
                    && Fresh(source.Vertices[new(key.X + 1, key.Z)], now)
                    && Fresh(source.Vertices[new(key.X, key.Z + 1)], now)
                    && Fresh(source.Vertices[new(key.X + 1, key.Z + 1)], now)) return true;
            return false;
        }

        int QueryVertices(int limit, int maximumAttempts = int.MaxValue)
        {
            var attempts = 0;
            foreach (var (key, vertex) in vertices)
            {
                if (workAttempts >= maximumWorkAttempts || attempts >= maximumAttempts || spent + 2 > budget || spent + 2 > limit || withinDeadline?.Invoke() == false) return attempts;
                if (now < vertex.Retry || vertex.PendingAt == now || Fresh(vertex, now) && now - vertex.At <= ttl * .55) continue;
                incremental?.PrepareQuery(Math.Min(budget, limit) - spent);
                var before = incremental?.Raycasts ?? 0;
                attempts++; workAttempts++; source.WorkAttempted = true; vertex.AttemptedAt = now;
                var succeeded = queries.TryVertex(source.Identity, new(key.X * spacing, key.Z * spacing), out var contact);
                // Work can be called twice for the same generation, and each
                // call has multiple vertex passes. Preserve resumable state
                // until a later timestamp instead of spending again now.
                if (!succeeded && incremental?.LastResult == LayerQueryResult.Pending) vertex.PendingAt = now;
                var used = incremental is null ? 2 : incremental.Raycasts - before;
                spent += used;
                // Debt promises a scheduling opportunity, not that a provider
                // must spend a native query. Zero-work Pending cannot block
                // the other class from acquiring a later finite burst.
                if (source.ContinuationTurnIsCell == false) source.ContinuationTurnIsCell = null;
                if (!succeeded && incremental?.LastResult == LayerQueryResult.Pending && used > 0)
                {
                    if (source.VertexContinuation is null && source.CellContinuation is null
                        && source.ContinuationTurnIsCell != true)
                    {
                        source.VertexContinuation = vertex;
                        source.VertexContinuationAttempts = 1;
                        source.VertexContinuationStarted = now;
                    }
                    else if (ReferenceEquals(source.VertexContinuation, vertex)
                        && ++source.VertexContinuationAttempts >= 3)
                        EndVertexContinuation();
                }
                else if (ReferenceEquals(source.VertexContinuation, vertex))
                    EndVertexContinuation();
                if (succeeded && Valid(contact))
                {
                    if (!vertex.Known || vertex.Contact != contact) vertex.Revision++;
                    vertex.Contact = contact; vertex.Known = true; vertex.At = now; vertex.Retry = now;
                }
                else if (incremental?.LastResult != LayerQueryResult.Pending)
                { vertex.Known = false; vertex.Retry = now + .1; }
            }
            return attempts;
        }
        int QueryCells(int maximumAttempts = int.MaxValue, bool continuationOnly = false)
        {
            var attempts = 0;
            var work = source.CellWork; work.Clear();
            foreach (var pair in source.Cells)
                if (now >= pair.Value.Retry && pair.Value.PendingAt != now && (!CellFresh(source, pair.Key, pair.Value, now) || now - pair.Value.At > ttl * .55)) work.Add(pair);
            work.Sort((a, b) =>
            {
                var priority = CellPriority(a).CompareTo(CellPriority(b));
                if (priority != 0) return priority;
                var continuation = ReferenceEquals(b.Value, source.CellContinuation)
                    .CompareTo(ReferenceEquals(a.Value, source.CellContinuation));
                if (continuation != 0) return continuation;
                // A proof that used a whole prior frame must not permanently
                // monopolize every later frame ahead of equally urgent cells.
                var age = a.Value.AttemptedAt.CompareTo(b.Value.AttemptedAt);
                return age != 0 ? age : Compare(0, 0, a.Key.X, a.Key.Z, b.Key.X, b.Key.Z);
            });
            foreach (var (key, cell) in work)
            {
                if (workAttempts >= maximumWorkAttempts || attempts >= maximumAttempts || spent >= budget || withinDeadline?.Invoke() == false) return attempts;
                var a = source.Vertices[new(key.X, key.Z)]; var b = source.Vertices[new(key.X + 1, key.Z)];
                var c = source.Vertices[new(key.X, key.Z + 1)]; var d = source.Vertices[new(key.X + 1, key.Z + 1)];
                if (!Fresh(a, now) || !Fresh(b, now) || !Fresh(c, now) || !Fresh(d, now)) continue;
                if (continuationOnly && !ReferenceEquals(cell, source.CellContinuation)) return attempts;
                incremental?.PrepareQuery(budget - spent);
                var before = incremental?.Raycasts ?? 0;
                attempts++; workAttempts++; source.WorkAttempted = true; cell.AttemptedAt = now;
                var succeeded = queries.TryCell(source.Identity, a.Contact, b.Contact, c.Contact, d.Contact, out var ceiling);
                // Multiple passes share one Update. Retry pending discovery on
                // the next timestamp, preserving its unmodified evidence/TTL.
                if (!succeeded && incremental?.LastResult == LayerQueryResult.Pending) cell.PendingAt = now;
                var used = incremental is null ? 1 : incremental.Raycasts - before;
                spent += used;
                if (source.ContinuationTurnIsCell == true) source.ContinuationTurnIsCell = null;
                if (!succeeded && incremental?.LastResult == LayerQueryResult.Pending && used > 0)
                {
                    if (source.CellContinuation is null && source.VertexContinuation is null
                        && source.ContinuationTurnIsCell != false)
                    {
                        source.CellContinuation = cell;
                        source.CellContinuationAttempts = 1;
                        source.CellContinuationStarted = now;
                    }
                    else if (ReferenceEquals(source.CellContinuation, cell)
                        && ++source.CellContinuationAttempts >= 3)
                        EndCellContinuation();
                }
                else if (ReferenceEquals(source.CellContinuation, cell)) EndCellContinuation();
                if (succeeded
                    && float.IsFinite(ceiling) && Math.Abs(ceiling) <= 1_000_000)
                {
                    cell.Planar = (queries as IPlanarClothSupportQueries)?.LastCellPlanar == true;
                    var lift = (queries as IConformalClothSupportQueries)?.LastCellLift ?? float.NaN;
                    cell.Lift = float.IsFinite(lift) && lift is >= 0 and <= 2_000_000 ? lift : float.NaN;
                    cell.Ceiling = cell.Planar ? ceiling
                        : Math.Max(ceiling, Math.Max(Math.Max(a.Contact.Y, b.Contact.Y), Math.Max(c.Contact.Y, d.Contact.Y)));
                    cell.A = a.Revision; cell.B = b.Revision; cell.C = c.Revision; cell.D = d.Revision;
                    cell.Known = true; cell.At = now; cell.Retry = now;
                }
                else if (incremental?.LastResult != LayerQueryResult.Pending)
                { cell.Known = false; cell.Retry = now + .1; }
            }
            return attempts;
        }
    }

    private bool Covered(Generation source, Bounds region, double now)
    {
        for (var z = region.Z0; z < region.Z1; z++)
        for (var x = region.X0; x < region.X1; x++)
            if (!source.Cells.TryGetValue(new(x, z), out var cell) || !CellFresh(source, new(x, z), cell, now)) return false;
        return true;
    }
    private bool CellFresh(Generation source, CellKey key, Cell cell, double now)
    {
        if (!cell.Known || now < cell.At || now - cell.At > ttl
            || !source.Vertices.TryGetValue(new(key.X, key.Z), out var a) || !Fresh(a, now)
            || !source.Vertices.TryGetValue(new(key.X + 1, key.Z), out var b) || !Fresh(b, now)
            || !source.Vertices.TryGetValue(new(key.X, key.Z + 1), out var c) || !Fresh(c, now)
            || !source.Vertices.TryGetValue(new(key.X + 1, key.Z + 1), out var d) || !Fresh(d, now)) return false;
        return cell.A == a.Revision && cell.B == b.Revision && cell.C == c.Revision && cell.D == d.Revision;
    }
    private bool Fresh(Vertex v, double now) => v.Known && now >= v.At && now - v.At <= ttl;
    private Bounds Region(Vector2 at, float radius)
    {
        var x0 = (int)Math.Floor((at.X - radius) / spacing); var z0 = (int)Math.Floor((at.Y - radius) / spacing);
        var x1 = (int)Math.Ceiling((at.X + radius) / spacing); var z1 = (int)Math.Ceiling((at.Y + radius) / spacing);
        var cells = Math.Max(x1 - x0, z1 - z0);
        return new(x0, z0, x0 + cells, z0 + cells);
    }
    private static void Ensure(Generation source, Bounds region)
    {
        for (var z = region.Z0; z <= region.Z1; z++)
        for (var x = region.X0; x <= region.X1; x++)
        {
            var vertex = new VertexKey(x, z); if (!source.Vertices.ContainsKey(vertex)) source.Vertices.Add(vertex, new());
            if (x < region.X1 && z < region.Z1)
            { var cell = new CellKey(x, z); if (!source.Cells.ContainsKey(cell)) source.Cells.Add(cell, new()); }
        }
    }
    private static void Retire(Generation source, Bounds retained)
    {
        source.RetireCells.Clear(); source.RetireVertices.Clear();
        foreach (var key in source.Cells.Keys)
            if (!retained.Contains(key.X, key.Z) || !retained.Contains(key.X + 1, key.Z + 1)) source.RetireCells.Add(key);
        foreach (var key in source.Vertices.Keys) if (!retained.Contains(key.X, key.Z)) source.RetireVertices.Add(key);
        foreach (var key in source.RetireCells) source.Cells.Remove(key);
        foreach (var key in source.RetireVertices) source.Vertices.Remove(key);
    }
    private static bool SameWorld(SupportQueryIdentity a, SupportQueryIdentity b) => a.Zone == b.Zone && a.Layer == b.Layer && a.GeometryGeneration == b.GeometryGeneration;
    private static bool Valid(Vector2 p) => MathEx.Finite(p) && Math.Max(Math.Abs(p.X), Math.Abs(p.Y)) <= 1_000_000;
    private static bool Valid(Vector3 p) => MathEx.Finite(p) && Math.Max(Math.Max(Math.Abs(p.X), Math.Abs(p.Y)), Math.Abs(p.Z)) <= 1_000_000;
}
