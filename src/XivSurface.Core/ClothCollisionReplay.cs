using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Serialization;

namespace XivSurface.Core;

// Diagnostic data only. These records never authorize a live surface and carry
// no native addresses. Immutable arrays preserve the exact failed-call state.
public enum FloorReplayFaceRole { WalkableFloor, ClothConnector, Riser }
public sealed record FloorReplayFace(LayerTriangle Triangle, double ObservedAt, bool WalkableGraph)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FloorReplayFaceRole? Role { get; init; }
}
public sealed record FloorReplayPortal(int From, int To, ImmutableArray<int> Risers,
    Vector3 ExitA, Vector3 ExitB, Vector3 EntryA, Vector3 EntryB);
public sealed record FloorReplayState(int Version, double Now, LayerTriangle Root, Vector3 RootPoint,
    ImmutableArray<FloorReplayFace> Faces, ImmutableArray<FloorReplayPortal> Portals)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayerSupportScope? Scope { get; init; }
}
public sealed record ClothPathReplay(FloorReplayState Layer, Vector3 From, Vector3 To,
    LayerQueryResult Result, string Reason, Vector2? MissingWitness)
{
    public long? SupportEpoch { get; init; }
    public Vector2? DiscoveryTarget { get; init; }
    public LayerQueryResult? DiscoveryResult { get; init; }
    public LayerQueryResult? AttemptResult { get; init; }
    public double SnapshotCopyMilliseconds { get; init; }
}
public sealed record ClothCellReplay(FloorReplayState Layer, LayerFloorHit Center,
    Vector3 A, Vector3 B, Vector3 C, Vector3 D, LayerQueryResult Result, string Reason, Vector2? MissingWitness)
{
    public long? SupportEpoch { get; init; }
    public Vector2? DiscoveryTarget { get; init; }
    public LayerQueryResult? DiscoveryResult { get; init; }
    public LayerQueryResult? AttemptResult { get; init; }
    public double SnapshotCopyMilliseconds { get; init; }
}
public enum ClothFloorRayContext { Direct, LayerTarget, PathMissingWitness, CellMissingWitness }
public enum ClothFloorRayOutcome
{
    Miss, Accepted, InvalidProbeOrPoint, OutsideProbe, InvalidNormal, SteepNormal, InvalidFloorTriangle,
    // Appended diagnostic only. The actual adapter must have accepted the
    // candidate; Describe never infers this from a steep ray or replay state.
    AcceptedClothConnector,
}

/// <summary>Receipt of an ALREADY executed ray, never floor authorization.
/// QueryTime is the sampler's elapsed clock; NativeStarted/CompletedSeconds
/// use the process-wide monotonic clock and are not report elapsed times.
/// RequestedTarget is the actual layer.Query key, not inferred from Probe.XZ:
/// a resumed frontier can cast at a different position within that call.</summary>
public readonly record struct ClothFloorRayReplay(uint Zone, long SupportEpoch, double QueryTime,
    double NativeStartedSeconds, double NativeCompletedSeconds, int RayOrdinal,
    ClothFloorRayContext Context, Vector2? RequestedTarget, ClothFloorProbe Probe,
    bool NativeHit, Vector3 HitPoint, Vector3 RawNormal, LayerTriangle Triangle, ClothFloorRayOutcome Outcome)
{
    public bool IsWitness => Context is ClothFloorRayContext.PathMissingWitness or ClothFloorRayContext.CellMissingWitness;

    // Diagnostic reason only. The runtime retains its ORIGINAL Accept/Valid
    // decisions; this never replaces them or changes a native query's result.
    public static ClothFloorRayOutcome Describe(ClothFloorProbe probe, bool nativeHit,
        Vector3 point, Vector3 normal, bool validFloorTriangle)
    {
        if (!nativeHit) return ClothFloorRayOutcome.Miss;
        if (!probe.Valid || !MathEx.Finite(point)) return ClothFloorRayOutcome.InvalidProbeOrPoint;
        if (Vector2.DistanceSquared(probe.Position,new(point.X,point.Z)) > .02f*.02f
            || point.Y < probe.MinimumY || point.Y > probe.MaximumY) return ClothFloorRayOutcome.OutsideProbe;
        var length=normal.Length();
        if (!MathEx.Finite(normal) || !float.IsFinite(length) || length < 1e-6f) return ClothFloorRayOutcome.InvalidNormal;
        if (normal.Y/length < .5f) return ClothFloorRayOutcome.SteepNormal;
        return validFloorTriangle ? ClothFloorRayOutcome.Accepted : ClothFloorRayOutcome.InvalidFloorTriangle;
    }
}
public sealed record ClothCollisionReport(int Version, uint Zone, double RequestedAt, double CompletedAt,
    string Completion, ClothPathReplay? PathUnknown, ClothCellReplay? CellPending)
{
    // Additive v1 diagnostic field. Old reports deserialize to an empty array.
    public ImmutableArray<ClothFloorRayReplay> RawFloorRays { get; init; } = [];
}
public sealed record ClothReplayResult(LayerQueryResult Result, string Reason, Vector2? MissingWitness,
    int DeadlineChecks, ImmutableArray<Vector3> Path, float Ceiling, float Lift);

/// <summary>Single bounded observation window. No queries, geometry mutation,
/// clock retimestamping, or automatic retry. The owner calls it on framework.</summary>
public sealed class ClothCollisionCapture
{
    public const double MaximumSeconds = 10;
    public const int MaximumOrdinaryRays = 32, MaximumWitnessRays = 8;
    public const int MaximumRawRays = MaximumOrdinaryRays + MaximumWitnessRays;
    private readonly ClothFloorRayReplay[] ordinaryRays = new ClothFloorRayReplay[MaximumOrdinaryRays];
    private readonly ClothFloorRayReplay[] witnessRays = new ClothFloorRayReplay[MaximumWitnessRays];
    private int ordinaryCount, witnessCount, witnessCursor;
    private uint zone;
    private double started, last;
    private ClothPathReplay? path;
    private ClothCellReplay? cell;
    private string? failure;
    public bool Active => zone != 0;
    public bool WantsPath => Active && path is null;
    public bool WantsCell => Active && cell is null;
    public bool Begin(uint territory, double now)
    {
        if (Active || territory == 0 || !double.IsFinite(now) || now < 0) return false;
        zone = territory; started = last = now; path = null; cell = null; failure = null;
        ordinaryCount = witnessCount = witnessCursor = 0; return true;
    }
    public void RecordPath(ClothPathReplay value)
    { if (WantsPath && value.Result == LayerQueryResult.Unknown) path = value; }
    public void RecordCell(ClothCellReplay value)
    { if (WantsCell && value.Result != LayerQueryResult.Success
        && (value.AttemptResult ?? value.Result) == LayerQueryResult.Pending) cell = value; }
    public void RecordPathDiscovery(Vector2 target, LayerQueryResult result)
    { if (path is not null && path.DiscoveryResult is null) path = path with { DiscoveryTarget = target, DiscoveryResult = result }; }
    public void RecordPathAttemptResult(LayerQueryResult result)
    { if (path is not null) path = path with { AttemptResult = result }; }
    public void FailCapture() { if (Active) failure = "managed snapshot invariant rejected"; }
    public bool WantsFloorRay(ClothFloorRayContext context) => Active &&
        (context is ClothFloorRayContext.PathMissingWitness or ClothFloorRayContext.CellMissingWitness
            ? witnessCount < MaximumWitnessRays || !HasCapturedWitnessReceipt() : ordinaryCount < MaximumOrdinaryRays);
    public void RecordFloorRay(ClothFloorRayReplay value)
    {
        if (!Active || value.Zone != zone || !WantsFloorRay(value.Context)) return;
        if (value.IsWitness)
        {
            if (witnessCount < MaximumWitnessRays) witnessRays[witnessCount++] = value;
            // Unrelated early witnesses cannot consume the reserved capacity
            // forever. Replace bounded unmatched observations until an actual
            // captured-target receipt arrives; never overwrite that receipt.
            else if (!HasCapturedWitnessReceipt())
            { witnessRays[witnessCursor] = value; witnessCursor = (witnessCursor+1)%MaximumWitnessRays; }
        }
        else ordinaryRays[ordinaryCount++] = value;
    }
    private bool HasCapturedWitnessReceipt()
    {
        for (var i=0;i<witnessCount;i++)
        {
            var ray=witnessRays[i];
            if (ray.RequestedTarget is not { } target) continue;
            if (ray.Context==ClothFloorRayContext.PathMissingWitness && path is not null
                && ray.QueryTime >= path.Layer.Now && (path.SupportEpoch is null || ray.SupportEpoch == path.SupportEpoch)
                && (target==path.MissingWitness || target==path.DiscoveryTarget)) return true;
            if (ray.Context==ClothFloorRayContext.CellMissingWitness && cell is not null
                && ray.QueryTime >= cell.Layer.Now && (cell.SupportEpoch is null || ray.SupportEpoch == cell.SupportEpoch)
                && (target==cell.MissingWitness || target==cell.DiscoveryTarget)) return true;
        }
        return false;
    }
    public bool TryFinish(uint territory, double now, out ClothCollisionReport? report)
    {
        report = null; if (!Active) return false;
        var invalidClock = !double.IsFinite(now) || now < last;
        var reason = invalidClock ? "clock invalidated" : territory != zone ? "zone changed" : failure is not null ? failure
            : path is not null && cell is not null
                && (path.MissingWitness is null && path.DiscoveryTarget is null
                    && cell.MissingWitness is null && cell.DiscoveryTarget is null || HasCapturedWitnessReceipt()) ? "both failures captured"
            : now >= started + MaximumSeconds ? (path is not null && cell is not null
                ? "window ended; failures captured but witness ray was not observed"
                : "window ended; missing failures were not observed") : null;
        if (reason is null) { last = now; return false; }
        var rays = ImmutableArray.CreateBuilder<ClothFloorRayReplay>(ordinaryCount+witnessCount);
        for (var i=0;i<ordinaryCount;i++) rays.Add(ordinaryRays[i]);
        for (var i=0;i<witnessCount;i++) rays.Add(witnessRays[i]);
        var receipts = rays.MoveToImmutable();
        var version = path?.Layer.Version == 2 || cell?.Layer.Version == 2
            || receipts.Any(ray => ray.Outcome == ClothFloorRayOutcome.AcceptedClothConnector) ? 2 : 1;
        report = new(version, zone, started, invalidClock ? last : now, reason, path, cell)
            { RawFloorRays = receipts };
        Cancel(); return true;
    }
    public void Cancel()
    {
        zone = 0; path = null; cell = null; failure = null;
        Array.Clear(ordinaryRays,0,ordinaryCount); Array.Clear(witnessRays,0,witnessCount);
        ordinaryCount = witnessCount = witnessCursor = 0;
    }
}

public sealed partial class LocalFloorLayer
{
    public string SurfacePathFailure { get; private set; } = "not queried";
    public const int MaximumReplayFaces = MaximumTriangles + MaximumRiserFaces;

    /// <summary>Copies existing managed evidence once; does not perform native
    /// queries or alter any expiry. Caller bounds request frequency and owns
    /// serialization away from framework/render threads.</summary>
    public FloorReplayState CaptureReplay()
    {
        if (observed.Count > MaximumReplayFaces || portalCount > MaximumDirectedPortals)
            throw new InvalidOperationException("Collision replay record cap exceeded.");
        var indices = new Dictionary<LayerTriangle, int>();
        var faces = ImmutableArray.CreateBuilder<FloorReplayFace>(observed.Count);
        // Preserve connected enumeration order: equal-cost corridor branches
        // and their diagnostics must replay in the same order as the live call.
        foreach (var triangle in connected) Add(triangle, true);
        foreach (var pair in observed) if (!indices.ContainsKey(pair.Key)) Add(pair.Key, false);
        var portals = ImmutableArray.CreateBuilder<FloorReplayPortal>();
        foreach (var from in connected)
        {
            if (!links.TryGetValue(from, out var edges)) continue;
            foreach (var edge in edges)
            {
                if (portals.Count >= MaximumDirectedPortals || edge.Risers.Length > 4)
                    throw new InvalidOperationException("Collision replay portal cap exceeded.");
                portals.Add(new(indices[from], indices[edge.To], edge.Risers.Select(r => indices[r]).ToImmutableArray(),
                    edge.ExitA, edge.ExitB, edge.EntryA, edge.EntryB));
            }
        }
        var cloth = supportScope == LayerSupportScope.MeasuredCloth;
        return new(cloth ? 2 : 1, now, root, rootPoint, faces.ToImmutable(), portals.ToImmutable())
            { Scope = cloth ? LayerSupportScope.MeasuredCloth : null };
        void Add(LayerTriangle triangle, bool inGraph)
        {
            indices.Add(triangle, faces.Count);
            faces.Add(new(triangle, observed[triangle], inGraph)
            {
                Role = supportScope != LayerSupportScope.MeasuredCloth ? null
                    : !inGraph ? FloorReplayFaceRole.Riser : IsConnector(triangle)
                        ? FloorReplayFaceRole.ClothConnector : FloorReplayFaceRole.WalkableFloor,
            });
        }
    }

    /// <summary>Offline-only result from the SAME implementation. Restored
    /// state is never returned to a runtime caller. A deterministic check limit
    /// emulates one deadline slice, not a claim of resumable native work.</summary>
    public static ClothReplayResult Replay(ClothPathReplay query, int deadlineChecks = int.MaxValue)
    {
        var layer = RestoreReplay(query.Layer); var checks = 0;
        var result = layer.TrySurfacePath(query.From, query.To, out var path, Deadline);
        return new(result, layer.SurfacePathFailure, layer.SurfacePathMissingWitness, checks, path.ToImmutableArray(), 0, float.NaN);
        bool Deadline() => ++checks <= deadlineChecks;
    }
    public static ClothReplayResult Replay(ClothCellReplay query, int deadlineChecks = int.MaxValue)
    {
        var layer = RestoreReplay(query.Layer); var checks = 0;
        var result = layer.TryCellCeiling(query.Center, query.A, query.B, query.C, query.D, out var ceiling, Deadline);
        return new(result, layer.CellCeilingFailure, layer.CellCeilingMissingWitness, checks, [], ceiling, layer.CellCeilingLift);
        bool Deadline() => ++checks <= deadlineChecks;
    }
    private static LocalFloorLayer RestoreReplay(FloorReplayState state)
    {
        if (state is null || state.Version is not (1 or 2) || !double.IsFinite(state.Now) || state.Now < 0
            || state.Faces.IsDefaultOrEmpty || state.Faces.Length > MaximumReplayFaces
            || state.Portals.IsDefault || state.Portals.Length > MaximumDirectedPortals
            || !state.Root.Valid || !MathEx.Finite(state.RootPoint) || !state.Root.Contains(state.RootPoint)) throw Invalid();
        var cloth = state.Version == 2;
        if (cloth ? state.Scope != LayerSupportScope.MeasuredCloth : state.Scope is not null) throw Invalid();
        var result = new LocalFloorLayer(cloth ? LayerSupportScope.MeasuredCloth : LayerSupportScope.WalkableOnly)
            { now = state.Now, root = state.Root, rootPoint = state.RootPoint };
        foreach (var face in state.Faces)
        {
            if (face is null || !face.Triangle.Valid || !double.IsFinite(face.ObservedAt)
                || face.ObservedAt < 0 || face.ObservedAt > state.Now
                || !result.observed.TryAdd(face.Triangle, face.ObservedAt)) throw Invalid();
            if (cloth)
            {
                if (face.Role is not { } role || !Enum.IsDefined(role)
                    || face.WalkableGraph != (role != FloorReplayFaceRole.Riser)
                    || role == FloorReplayFaceRole.WalkableFloor && !face.Triangle.Walkable
                    || role == FloorReplayFaceRole.ClothConnector && !ClothConnectorGeometry.Eligible(face.Triangle)
                    || role == FloorReplayFaceRole.Riser && (face.Triangle.Walkable || Math.Abs(face.Triangle.Normal.Y) > .1f))
                    throw Invalid();
                if (role == FloorReplayFaceRole.ClothConnector) result.clothConnectors.Add(face.Triangle);
            }
            else if (face.Role is not null) throw Invalid();
            if (face.WalkableGraph)
            {
                if ((!face.Triangle.Walkable && !result.IsConnector(face.Triangle)) || result.connected.Count >= MaximumTriangles) throw Invalid();
                result.connected.Add(face.Triangle); result.links.Add(face.Triangle, []);
            }
            else
            {
                if (result.curbFaces.Count >= MaximumRiserFaces) throw Invalid();
                result.curbFaces.Add(face.Triangle);
            }
        }
        if (!result.connected.Contains(state.Root)) throw Invalid();
        var directed = cloth ? new Dictionary<(int, int), FloorReplayPortal>() : null;
        foreach (var portal in state.Portals)
        {
            if (portal is null || (uint)portal.From >= state.Faces.Length || (uint)portal.To >= state.Faces.Length
                || !state.Faces[portal.From].WalkableGraph || !state.Faces[portal.To].WalkableGraph
                || portal.Risers.IsDefault || portal.Risers.Length > 4
                || !MathEx.Finite(portal.ExitA) || !MathEx.Finite(portal.ExitB)
                || !MathEx.Finite(portal.EntryA) || !MathEx.Finite(portal.EntryB)) throw Invalid();
            var risers = new LayerTriangle[portal.Risers.Length];
            for (var i = 0; i < risers.Length; i++)
            {
                var index = portal.Risers[i];
                if ((uint)index >= state.Faces.Length) throw Invalid();
                if (cloth && (state.Faces[index].Role != FloorReplayFaceRole.Riser
                    || portal.Risers.IndexOf(index) != i)) throw Invalid();
                risers[i] = state.Faces[index].Triangle;
            }
            if (cloth)
            {
                var from = state.Faces[portal.From].Triangle;
                var to = state.Faces[portal.To].Triangle;
                if (portal.From == portal.To || !directed!.TryAdd((portal.From, portal.To), portal)) throw Invalid();
                if (risers.Length == 0)
                {
                    if (!TryPortal(from, to, out var a, out var b)
                        || !Matches(portal.ExitA, portal.ExitB, a, b)
                        || !Matches(portal.EntryA, portal.EntryB, a, b)
                        || (result.IsConnector(from) || result.IsConnector(to))
                            && Vector2.DistanceSquared(new(a.X, a.Z), new(b.X, b.Z)) <= 1e-10f) throw Invalid();
                }
                else
                {
                    // Only original walkable curb endpoints may carry the
                    // special riser chain; a cloth connector cannot inherit it.
                    if (!from.Walkable || !to.Walkable || CurbConnects(from, to, risers) != (1 << risers.Length) - 1) throw Invalid();
                    var exit = false; var entry = false;
                    foreach (var riser in risers)
                    {
                        if (TryPortal(from, riser, out var a, out var b) && Matches(portal.ExitA, portal.ExitB, a, b)) exit = true;
                        if (TryPortal(to, riser, out a, out b) && Matches(portal.EntryA, portal.EntryB, a, b)) entry = true;
                    }
                    if (!exit || !entry) throw Invalid();
                }
            }
            result.links[state.Faces[portal.From].Triangle].Add(new(state.Faces[portal.To].Triangle, risers,
                portal.ExitA, portal.ExitB, portal.EntryA, portal.EntryB));
        }
        if (cloth)
        {
            foreach (var portal in state.Portals)
                if (!directed!.TryGetValue((portal.To, portal.From), out var reverse)
                    || !portal.Risers.AsSpan().SequenceEqual(reverse.Risers.AsSpan())
                    || !Matches(portal.ExitA, portal.ExitB, reverse.EntryA, reverse.EntryB)
                    || !Matches(portal.EntryA, portal.EntryB, reverse.ExitA, reverse.ExitB)) throw Invalid();
            // Restore only a connected measured graph, never unattached tagged
            // steep faces. Unused observed risers remain diagnostic-only.
            var seen = new HashSet<LayerTriangle> { state.Root };
            var pending = new Queue<LayerTriangle>(); pending.Enqueue(state.Root);
            while (pending.TryDequeue(out var from))
                foreach (var edge in result.links[from])
                {
                    if (seen.Add(edge.To)) pending.Enqueue(edge.To);
                }
            if (seen.Count != result.connected.Count) throw Invalid();
        }
        result.portalCount = state.Portals.Length; return result;
        static bool Matches(Vector3 a, Vector3 b, Vector3 expectedA, Vector3 expectedB)
        {
            const float squared = SeamTolerance * SeamTolerance;
            return Vector3.DistanceSquared(a, expectedA) <= squared && Vector3.DistanceSquared(b, expectedB) <= squared
                || Vector3.DistanceSquared(a, expectedB) <= squared && Vector3.DistanceSquared(b, expectedA) <= squared;
        }
        static ArgumentException Invalid() => new("Invalid bounded collision replay state.");
    }

    // Report validation is offline and bounded. Discard the restored object;
    // importing a diagnostic never yields runtime geometry authority.
    internal static void ValidateReplayState(FloorReplayState state) => _ = RestoreReplay(state);
}
