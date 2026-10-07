using System.Numerics;

namespace XivSurface.Core;

public enum LayerQueryResult { Unknown, Pending, Success }
public interface ILayerFloorScene
{
    bool CanQuery { get; }
    bool TryFloor(ClothFloorProbe probe, out LayerFloorHit hit);
    bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle);
}

/// <summary>Opt-in copied collision evidence for cloth discovery. This is
/// still one actual bounded downward query, not an inferred floor. The raw
/// result may be a finite upward steep face with LayerFloorHit.Valid=false;
/// layer admission, full footprint and elevated clearance remain separate.
/// Ordinary TryFloor must retain its player-walkable semantics.</summary>
public interface ILayerMeasuredClothScene : ILayerFloorScene
{
    bool TryMeasuredClothFloor(ClothFloorProbe probe, out LayerFloorHit rawHit);
}

/// <summary>Optional native-attempt receipt. The nonnegative count must advance
/// for every actual floor/wall query, including misses, and must not reset
/// during one call. It may reset between caller-owned framework updates.</summary>
public interface ILayerFloorQueryAttempts
{
    int Raycasts { get; }
}

/// <summary>Bounded resumable discovery over real ray-hit triangles. No query
/// result is replaced with plane height. The native adapter owns the global
/// scene deadline/call budget. This helper can consume several updates to
/// establish an actual edge chain through small collision triangles.</summary>
public sealed class LayerFloorDiscovery
{
    public const double PendingLifetimeSeconds = 2;
    private sealed class Work
    {
        public ClothFloorProbe Probe;
        public bool Frontier;
        public bool FrontierAfterMiss;
        public int MissedAtFaceCount = -1;
        public int Phase, LayersSkipped, Discoveries;
        public LayerFloorHit Candidate;
        public Vector3 RiserStart, RiserEnd;
        public readonly LayerTriangle[] Risers = new LayerTriangle[2];
        public int RiserCount;
        public double CandidateAt;
        public double LastRequested;
    }
    private readonly Dictionary<Vector2, Work> pending = [];
    private readonly List<Vector2> retired = [];
    private double now;
    private readonly bool measuredCloth;
    public LocalFloorLayer Layer { get; }
    public LayerFloorDiscovery() : this(LayerSupportScope.WalkableOnly) { }
    public LayerFloorDiscovery(LayerSupportScope scope)
    {
        Layer = new(scope);
        measuredCloth = scope == LayerSupportScope.MeasuredCloth;
    }
    public int Pending => pending.Count;
    public bool ProvesPlanarCell(LayerFloorHit center,Vector3 a,Vector3 b,Vector3 c,Vector3 d,
        Func<bool>? withinDeadline=null) => Layer.ProvesPlanarCell(center,a,b,c,d,withinDeadline);
    public bool Seed(LayerFloorHit hit) { pending.Clear(); return Layer.Seed(hit); }
    public bool BeginFrame(LayerFloorHit hit, double time, Vector2? center = null, float retainRadius = 5)
    {
        now = time;
        if (Layer.BeginFrame(hit, time, center, retainRadius))
        {
            retired.Clear();
            foreach (var pair in pending)
                if (time - pair.Value.LastRequested > PendingLifetimeSeconds) retired.Add(pair.Key);
            foreach (var key in retired) pending.Remove(key);
            return true;
        }
        pending.Clear(); return false;
    }

    public bool Refresh(ILayerFloorScene scene, int maximumCalls = 2)
    {
        for (var i = 0; i < maximumCalls && scene.CanQuery && Layer.TryRefresh(out var triangle); i++)
        {
            var at = (triangle.A + triangle.B + triangle.C) / 3;
            var before = AttemptCount(scene);
            bool valid;
            // Raw upward geometry is not a role: a slightly slanted ordinary
            // curb also satisfies it. Only an admitted graph surface gets the
            // downward cloth refresh; curb-only evidence retains its wall ray.
            if (triangle.Walkable || measuredCloth && Layer.Contains(triangle)
                && Layer.IsMeasuredSupport(new(at, triangle)))
            {
                var hit = ReadFloor(scene, new(new(at.X, at.Z), at.Y + .05f, .1f, at.Y - .05f, at.Y + .05f), out var floor);
                if (!hit && NotAttempted(scene, before)) return true;
                valid = hit && CandidateValid(floor) && floor.Triangle == triangle;
            }
            else
            {
                var hit = scene.TryWall(at + triangle.Normal * .03f, at - triangle.Normal * .03f, out var wall);
                if (!hit && NotAttempted(scene, before)) return true;
                valid = hit && wall == triangle;
            }
            if (!valid) { Seed(default); return false; }
            Layer.ObserveExact(triangle);
        }
        return true;
    }

    public LayerQueryResult Query(Vector2 wanted, ILayerFloorScene scene, out LayerFloorHit result)
    {
        result = default;
        if (!MathEx.Finite(wanted)) return LayerQueryResult.Unknown;
        if (!pending.TryGetValue(wanted, out var work))
        {
            if (pending.Count >= 512) return LayerQueryResult.Unknown;
            work = new(); pending.Add(wanted, work);
        }
        work.LastRequested = now;
        if (work.Phase >= 2 && (!double.IsFinite(now) || now < work.CandidateAt || now - work.CandidateAt > .1))
        { work.Phase = 0; work.RiserCount = 0; }
        // Separate structural cap from native deadline: even a fake zero-cost
        // provider cannot make one request traverse an unlimited mesh chain.
        for (var steps = 0; steps < 12; steps++)
        {
            if (!scene.CanQuery) return LayerQueryResult.Pending;
            if (work.Discoveries > 128 || work.LayersSkipped > 4)
                return Finish(LayerQueryResult.Unknown);
            if (work.Phase == 0)
            {
                work.Frontier = false;
                if (work.FrontierAfterMiss || !Layer.TryProbe(wanted, out work.Probe))
                {
                    if (!Layer.TryFrontier(wanted, out work.Probe, () => scene.CanQuery))
                        return scene.CanQuery ? Finish(LayerQueryResult.Unknown) : LayerQueryResult.Pending;
                    work.Frontier = true;
                }
                work.FrontierAfterMiss = false;
                work.Phase = 1;
            }
            if (work.Phase == 1)
            {
                if (!scene.CanQuery) return LayerQueryResult.Pending;
                var before = AttemptCount(scene);
                var found = ReadFloor(scene, work.Probe, out var hit);
                if (!found && NotAttempted(scene, before)) return LayerQueryResult.Pending;
                if (!found || !CandidateValid(hit))
                {
                    // A local plane guides only the ray origin. At a real
                    // crease its extrapolation may start below the next face,
                    // or stop above it. Discover through the existing measured
                    // boundary instead, without widening the query band.
                    // A missed frontier is terminal; the unchanged discovery
                    // and structural caps still bound successful retries.
                    if (work.Frontier || work.MissedAtFaceCount == Layer.Count)
                        return Finish(LayerQueryResult.Unknown);
                    work.MissedAtFaceCount = Layer.Count;
                    work.FrontierAfterMiss = true;
                    work.Phase = 0;
                    continue;
                }
                if (Accept(work.Probe, hit))
                {
                    if (!work.Frontier)
                    {
                        if (Vector2.DistanceSquared(wanted, new(hit.Position.X, hit.Position.Z)) > .02f * .02f) return Finish(LayerQueryResult.Unknown);
                        result = hit; return Finish(LayerQueryResult.Success);
                    }
                    work.Discoveries++; work.Phase = 0; continue;
                }
                work.Candidate = hit;
                work.CandidateAt = now;
                if (hit.Valid && TryRiserSegment(hit, out work.RiserStart, out work.RiserEnd))
                { work.RiserCount = 0; work.Phase = 2; }
                else work.Phase = 4;
            }
            if (work.Phase is 2 or 3)
            {
                if (!scene.CanQuery) return LayerQueryResult.Pending;
                var fraction = work.Phase == 2 ? 1 / 3f : 2 / 3f;
                var height = float.Lerp(work.RiserStart.Y, work.RiserEnd.Y, fraction);
                var from = work.RiserStart with { Y = height }; var to = work.RiserEnd with { Y = height };
                var before = AttemptCount(scene);
                var found = scene.TryWall(from, to, out var riser);
                if (!found && NotAttempted(scene, before)) return LayerQueryResult.Pending;
                if (found && riser.Valid && Math.Abs(riser.Normal.Y) <= .1f)
                    work.Risers[work.RiserCount++] = riser;
                work.Phase++;
                if (work.Phase == 3) continue;
                if (Layer.Accept(work.Probe, work.Candidate, work.Risers.AsSpan(0, work.RiserCount)))
                {
                    if (!work.Frontier)
                    {
                        if (Vector2.DistanceSquared(wanted, new(work.Candidate.Position.X, work.Candidate.Position.Z)) > .02f * .02f) return Finish(LayerQueryResult.Unknown);
                        result = work.Candidate; return Finish(LayerQueryResult.Success);
                    }
                    work.Discoveries++; work.Phase = 0; continue;
                }
            }
            if (work.Phase == 4)
            {
                if (!Layer.TryNearest(work.Probe.Position, out var parent, out _)
                    || !TrySupportHeight(parent, work.Probe.Position, out var predicted)) return Finish(LayerQueryResult.Unknown);
                if (work.Candidate.Position.Y > predicted + .01f)
                {
                    // A disconnected deck is not our support. Inspect beneath
                    // its face while retaining the original local lower bound.
                    var start = work.Candidate.Position.Y - .005f;
                    if (start <= work.Probe.MinimumY) return Finish(LayerQueryResult.Unknown);
                    work.Probe = work.Probe with { StartY = start, MaximumY = start, Length = start - work.Probe.MinimumY };
                    work.LayersSkipped++; work.Phase = 1; continue;
                }
                if (work.Frontier) return Finish(LayerQueryResult.Unknown);
                if (!Layer.TryFrontier(wanted, out var frontierProbe, () => scene.CanQuery))
                    return scene.CanQuery ? Finish(LayerQueryResult.Unknown) : LayerQueryResult.Pending;
                work.Probe = frontierProbe; work.Frontier = true; work.Phase = 1; continue;
            }
        }
        return LayerQueryResult.Pending;

        LayerQueryResult Finish(LayerQueryResult status) { pending.Remove(wanted); return status; }
    }

    private bool ReadFloor(ILayerFloorScene scene, ClothFloorProbe probe, out LayerFloorHit hit)
        => measuredCloth && scene is ILayerMeasuredClothScene measured
            ? measured.TryMeasuredClothFloor(probe, out hit) : scene.TryFloor(probe, out hit);

    private bool CandidateValid(LayerFloorHit hit)
        => measuredCloth ? Layer.IsMeasuredSupport(hit) : hit.Valid;

    private bool Accept(ClothFloorProbe probe, LayerFloorHit hit)
        => hit.Valid ? Layer.Accept(probe, hit)
            : measuredCloth && Layer.AcceptMeasuredClothConnector(probe, hit);

    private bool TrySupportHeight(LayerTriangle triangle, Vector2 position, out float height)
        => measuredCloth ? Layer.TryMeasuredHeight(triangle, position, out height)
            : triangle.TryHeight(position, out height);

    private static int AttemptCount(ILayerFloorScene scene)
        => scene is ILayerFloorQueryAttempts attempts ? attempts.Raycasts : -1;

    // A false return alone cannot distinguish a miss from budget expiry
    // between CanQuery and the adapter's own check. Only an opt-in, unchanged
    // valid receipt AND an exhausted budget authorizes deferral. A real miss
    // that used the final ray retains the ordinary invalidation/phase behavior.
    private static bool NotAttempted(ILayerFloorScene scene, int before)
        => before >= 0 && scene is ILayerFloorQueryAttempts attempts
            && attempts.Raycasts == before && !scene.CanQuery;

    private bool TryRiserSegment(LayerFloorHit hit, out Vector3 from, out Vector3 to)
    {
        from = to = default;
        if (!Layer.TryNearest(new(hit.Position.X, hit.Position.Z), out _, out var boundary)) return false;
        var rise = hit.Position.Y - boundary.Y;
        var delta = new Vector2(hit.Position.X - boundary.X, hit.Position.Z - boundary.Z);
        if (Math.Abs(rise) is < .015f or > LocalFloorLayer.MaximumCurbHeight
            || delta.Length() is < .001f or > LocalFloorLayer.MaximumQueryAdvance) return false;
        var direction = Vector2.Normalize(delta);
        // Begin on the lower floor's open side, not inside the step's solid.
        if (rise > 0)
        { from = boundary - new Vector3(direction.X, 0, direction.Y) * .02f; to = hit.Position; }
        else
        { from = hit.Position + new Vector3(direction.X, 0, direction.Y) * .02f; to = boundary; }
        return true;
    }
}
