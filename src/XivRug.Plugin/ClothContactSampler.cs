using System.Diagnostics;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using XivSurface.Core;

namespace XivRug.Plugin;

/// <summary>World-anchored rolling collision support, separately refined for
/// the woven textile. Native collision queries remain mandatory. A confirmed
/// complete requested material footprint is required; incomplete coverage never
/// resizes the rug. Cached contacts stay at measured world positions.</summary>
internal sealed unsafe class ClothContactSampler : IClothSupportQueries, IIncrementalClothSupportQueries, IPlanarClothSupportQueries, IConformalClothSupportQueries, ILayerMeasuredClothScene, ILayerFloorQueryAttempts
{
    public enum VisibilityState { InvalidScene, Visible, PlayerFloor, CenterFloor, ChangedEvidence, FootprintCoverage, OriginClearance, CenterMismatch }
    public VisibilityState Visibility { get; private set; }
    public sealed record Frame(Vector2 Center, Vector2 HalfSize, float AnchorY, ClothMesh Mesh, int Supported, long SupportEpoch);
    public sealed record Timing(int Frames, int Raycasts, double AgeMilliseconds,
        double MaximumQueryMilliseconds, double BuildMilliseconds, float TargetDrift);
    private const double QueryBudgetMilliseconds = 2;
    private const int MaximumRaysPerUpdate = 100;
    // Explicit tradeoff: .4y support / ~.133y visual vertices at default2y
    // radius. Large rugs keep a bounded support count at a coarser spacing.
    private const float MinimumSupportSpacing = .4f;
    private const float ReanchorDistance = 3, MaximumOriginDrift = 6;
    // Cloth may follow an actually measured upward bevel without making that
    // bevel a valid player floor. Near-feet queries below stay walkable-only.
    private readonly LayerFloorDiscovery layer = new(LayerSupportScope.MeasuredCloth);
    private readonly ClothPlayerSupportTracker playerSupport = new();
    private readonly ClothClearanceTrace clearance = new();
    private readonly ClothCollisionCapture replay = new();
    private bool replayCopiedThisUpdate;
    private ClothFloorRayContext floorRayContext;
    private Vector2? floorRequestedTarget;
    public bool BeginReplay(uint zone, double now) => replay.Begin(zone, now);
    public bool FinishReplay(uint zone, double now, out ClothCollisionReport? report) => replay.TryFinish(zone, now, out report);
    public void CancelReplay() => replay.Cancel();
    private readonly Func<Vector3, Vector3, ClothClearanceCast> castClearance;
    private double queryTime;
    private bool clearanceHitFloor;
    private int floorChordHits, wallHits, pathPending, pathUnknown;
    private int chordObservationAttempts, retainedChordHits;
    private int cellSuccess, cellPending, cellUnknown;
    private string firstCellFailure = "none";
    private string firstClearanceFailure = "none";
    private RollingClothSupport? support;
    private SupportQueryIdentity requestedOrigin;
    private Vector2 half;
    private float spacing;
    private uint territory;
    private long epoch;
    private int rays;
    private int queryAllowanceEnd;
    private long queryStarted;
    private RollingSupportSnapshot? diagnosticSupport;
    private ClothMesh? diagnosticCoarse;
    private Vector3 diagnosticPlayerNormal;
    private float diagnosticPlayerGap;
    private readonly Func<bool> withinDeadline;
    public Frame? Current { get; private set; }
    public float? AcceptedPlayerSupportHeight { get; private set; }
    public int RaycastsThisUpdate => rays;
    public bool RefreshDeferredThisUpdate { get; private set; }
    public bool ContinuedOriginThisUpdate { get; private set; }
    public int Raycasts => rays;
    public LayerQueryResult LastResult { get; private set; }
    public bool LastCellPlanar { get; private set; }
    public float LastCellLift { get; private set; } = float.NaN;
    public bool CanQuery => rays < Math.Min(MaximumRaysPerUpdate, queryAllowanceEnd) && withinDeadline();
    public void PrepareQuery(int rayAllowance)
    { queryAllowanceEnd = Math.Min(MaximumRaysPerUpdate, rays + Math.Max(0, rayAllowance)); LastResult = LayerQueryResult.Pending; }
    public Timing? LastTiming { get; private set; }
    public string Status { get; private set; } = "Waiting for rolling cloth support.";

    // Called by the existing 20-second logger, never per-vertex/native draw.
    // No extra raycasts, scene writes, or stale-evidence authorization.
    public string DescribeGeometry()
    {
        var geometry = layer.Layer.DescribeGeometry();
        var planar = 0; var cells = 0; var uplift = 0f;
        if (diagnosticSupport is { } sample && diagnosticCoarse?.GroundMinimum is { } ground)
        {
            cells = sample.CellCeilings.Length;
            if (sample.PlanarCells is { } proofs) foreach (var proof in proofs) if (proof) planar++;
            for (var i = 0; i < sample.Contacts.Length; i++)
                uplift = Math.Max(uplift, ground[i] - sample.Contacts[i].Y);
        }
        return FormattableString.Invariant($"collision faces flat={geometry.FlatFaces}, sloped={geometry.SlopedFaces}, risers={geometry.RiserFaces}, min normalY={geometry.MinimumNormalY:F4}; player normal=({diagnosticPlayerNormal.X:F4},{diagnosticPlayerNormal.Y:F4},{diagnosticPlayerNormal.Z:F4}), floor gap={diagnosticPlayerGap:F4}y; planar cells={planar}/{cells}, max cell uplift={uplift:F4}y; current support={Current is not null}; clearance floor chords={floorChordHits}, retained exact floor hits={retainedChordHits}/{chordObservationAttempts}, walls={wallHits}, path pending/unknown={pathPending}/{pathUnknown}, first={firstClearanceFailure}; cell success/pending/unknown={cellSuccess}/{cellPending}/{cellUnknown}, first={firstCellFailure}");
    }

    public ClothContactSampler()
    {
        withinDeadline = () => Stopwatch.GetElapsedTime(queryStarted).TotalMilliseconds < QueryBudgetMilliseconds;
        castClearance = CastClearance;
    }

    public void Reset()
    {
        support = null; Current = null; AcceptedPlayerSupportHeight = null; LastTiming = null; requestedOrigin = default;
        clearance.Reset();
        playerSupport.Reset();
        diagnosticSupport = null; diagnosticCoarse = null; diagnosticPlayerNormal = default; diagnosticPlayerGap = 0;
        layer.Seed(default);
        territory = 0; epoch++;
    }

    public void Update(uint zone, Vector2 wantedCenter, Vector2 wantedHalf, Vector3 player, double now,
        bool motion, ReadOnlySpan<ClothPressure> pressures = default, bool airborne = false)
    {
        Current = null;
        Visibility = VisibilityState.InvalidScene;
        diagnosticSupport = null; diagnosticCoarse = null; diagnosticPlayerNormal = default; diagnosticPlayerGap = 0;
        AcceptedPlayerSupportHeight = null;
        LastTiming = null;
        RefreshDeferredThisUpdate = false;
        ContinuedOriginThisUpdate = false;
        queryTime = now;
        replayCopiedThisUpdate = false;
        floorChordHits = wallHits = pathPending = pathUnknown = 0;
        chordObservationAttempts = retainedChordHits = 0;
        cellSuccess = cellPending = cellUnknown = 0;
        firstCellFailure = "none";
        firstClearanceFailure = "none";
        rays = 0; queryAllowanceEnd = MaximumRaysPerUpdate; queryStarted = Stopwatch.GetTimestamp();
        if (zone == 0 || !Finite(wantedCenter) || !Finite(wantedHalf) || wantedHalf.X <= 0 || wantedHalf.Y <= 0
            || !double.IsFinite(now) || now < 0) { Reset(); return; }
        if (zone != territory || wantedHalf != half)
        {
            Reset(); territory = zone; half = wantedHalf;
            var maximumHalf = Math.Max(half.X, half.Y);
            spacing = Math.Max(MinimumSupportSpacing, maximumHalf / 8);
            support = new(spacing, maximumHalf, .5f, 2);
        }
        // During a jump, measure the already reached floor layer below the
        // current XZ. A short feet ray misses it; a broad airborne ray can
        // wrongly select a bridge overhead. Both paths require a new real hit.
        var playerQuery = playerSupport.Plan(zone, player, now, airborne, out var playerProbe);
        LayerFloorHit playerHit = default;
        var playerMeasured = playerQuery switch
        {
            ClothPlayerSupportQuery.NearFeet => TryFloor(playerProbe, out playerHit),
            ClothPlayerSupportQuery.RetainedLayer => QueryFloor(new(player.X, player.Z), out playerHit) == LayerQueryResult.Success,
            _ => false,
        };
        if (!playerMeasured || !playerSupport.Confirm(playerHit, layer.Layer))
        { Visibility = VisibilityState.PlayerFloor; Status = "Hidden: no nearby confirmed player floor layer."; return; }
        var playerFloor = playerHit.Position.Y;
        diagnosticPlayerNormal = playerHit.Triangle.Normal;
        diagnosticPlayerGap = player.Y - playerFloor;
        AcceptedPlayerSupportHeight = playerFloor;
        if (!layer.BeginFrame(playerHit, now, wantedCenter, Math.Max(half.X, half.Y) * 1.414214f + 1.2f))
        { support!.Reset(); clearance.Reset(); requestedOrigin = default; epoch++; }
        // At rest, the requested center is the very point measured by the
        // mandatory player ray. Reuse that actual hit even when the native
        // call overran this frame's budget; do not hide a fresh supported rug
        // merely because a duplicate center ray cannot run.
        var centerHit = playerHit;
        var centerResult = Vector2.DistanceSquared(wantedCenter, new(playerHit.Position.X, playerHit.Position.Z)) <= 1e-10f
            ? LayerQueryResult.Success : QueryFloor(wantedCenter, out centerHit);
        if (centerResult != LayerQueryResult.Success)
        { Visibility = VisibilityState.CenterFloor; Status = $"Hidden: current center layer discovery {centerResult}."; return; }
        var centerFloor = centerHit.Position.Y;
        // A completed current-center hit remains valid if its native call
        // overran the work deadline. Stop optional refresh queries, not a
        // still-fresh, fully covered publication. All mandatory gates below
        // remain: layer refresh failure, required wall query, origin drift,
        // complete cache coverage/TTL and fresh-center height agreement.
        RefreshDeferredThisUpdate = !withinDeadline();
        var active = support!.ActiveIdentity;
        var replacePending = false;
        var checkedOrigin = default(SupportQueryIdentity);
        // Select a current origin BEFORE an obsolete-origin clearance path can
        // consume this frame's entire native-query deadline. A pending origin
        // is stable while it remains local, not replaced on every walking frame.
        if (requestedOrigin.Valid)
        {
            var requestedDrift = Vector2.Distance(wantedCenter,
                new(requestedOrigin.CompressionOrigin.X, requestedOrigin.CompressionOrigin.Z));
            var limit = support.Reanchoring ? MaximumOriginDrift : ReanchorDistance;
            if (requestedDrift > limit)
            { requestedOrigin = default; replacePending = support.Reanchoring; }
        }
        // Keep an old patch only on the same reachable side of its compression
        // origin. This ray is also counted in the update budget; a corner can
        // trigger an early rebase rather than dragging cloth back to a wall.
        var centerBlocked = false;
        if (requestedOrigin.Valid && !support.Reanchoring)
        {
            if (!TryCompress(wantedCenter, centerFloor, requestedOrigin.CompressionOrigin, out var compressed))
            {
                // Missing clearance is not permission to render the old patch.
                // It also must not prevent sampling from the independently
                // measured current-center floor on the next frame.
                centerBlocked = true;
                requestedOrigin = default;
            }
            else if (Vector2.Distance(compressed, wantedCenter) > .06f)
            { centerBlocked = true; requestedOrigin = default; }
            else checkedOrigin = requestedOrigin;
        }
        if (!requestedOrigin.Valid)
        {
            requestedOrigin = new(zone, epoch, 0, new(wantedCenter.X, centerFloor + .18f, wantedCenter.Y), playerFloor + .35f, 1.35f);
            // This origin is the current floor hit itself. There is no wall
            // segment to omit; all outgoing vertex/cell queries still run.
            checkedOrigin = requestedOrigin;
        }

        // An origin-only rebase is not a change of floor. Check the existing
        // full-size patch's route to THIS center before optional discovery can
        // consume the deadline. It may bridge preparation of its replacement,
        // but only with fresh current coverage below: never freeze an old mesh,
        // move cached contacts, extend their TTL, or cross an unchecked wall.
        SupportQueryIdentity? continuityOrigin = null;
        if (active is { } previousOrigin && previousOrigin != requestedOrigin
            && previousOrigin.Zone == requestedOrigin.Zone
            && previousOrigin.GeometryGeneration == requestedOrigin.GeometryGeneration
            && previousOrigin.Layer == requestedOrigin.Layer
            && Vector2.Distance(wantedCenter, new(previousOrigin.CompressionOrigin.X,
                previousOrigin.CompressionOrigin.Z)) <= MaximumOriginDrift
            && TryCompress(wantedCenter, centerFloor, previousOrigin.CompressionOrigin, out var previousCenter)
            && Vector2.Distance(previousCenter, wantedCenter) <= .06f)
            continuityOrigin = previousOrigin;

        // First keep the visible side checked, then validate the replacement.
        // An expensive replacement check must not consume the old side's
        // mandatory clearance opportunity. Unknown clearance delays promotion.
        if (support.PendingIdentity is { } candidateOrigin && candidateOrigin == requestedOrigin
            && checkedOrigin != candidateOrigin
            && TryCompress(wantedCenter, centerFloor, candidateOrigin.CompressionOrigin, out var candidatePosition)
            && Vector2.Distance(candidatePosition, wantedCenter) <= .06f)
            checkedOrigin = candidateOrigin;

        // Both mandatory origin checks precede optional evidence refresh.
        // A changed scene still invalidates everything before publication.
        if (!layer.Refresh(this))
        { support.Reset(); playerSupport.Reset(); requestedOrigin = default; epoch++; Visibility = VisibilityState.ChangedEvidence; Status = "Hidden: connected floor evidence changed during refresh."; return; }
        var checkedReplacement = checkedOrigin == requestedOrigin
            && (replacePending || support.PendingIdentity is null || support.PendingIdentity == requestedOrigin);

        support.Update(requestedOrigin, wantedCenter, now, Math.Max(0, MaximumRaysPerUpdate - rays), this,
            withinDeadline, originReserve: 45,
            // Keep feeding the moving old window while its safe replacement
            // warms. Give all work to replacement only when the old side is
            // unavailable; otherwise starving it would undo the handoff.
            preferRequestedOrigin: centerBlocked || active != requestedOrigin && continuityOrigin is null,
            replacePendingOrigin: replacePending, allowPendingPromotion: checkedReplacement);
        var queryMilliseconds = Stopwatch.GetElapsedTime(queryStarted).TotalMilliseconds;
        active = support.ActiveIdentity;
        var originDrift = active is { } currentOrigin
            ? Vector2.Distance(wantedCenter, new(currentOrigin.CompressionOrigin.X, currentOrigin.CompressionOrigin.Z)) : float.PositiveInfinity;
        RollingSupportSnapshot? snapshot = null;
        var continuing = false;
        if (!support.TrySnapshot(wantedCenter, now, out snapshot, requestedOrigin) || snapshot is null)
        {
            continuing = continuityOrigin is { } verifiedOrigin
                && support.TrySnapshot(wantedCenter, now, out snapshot, verifiedOrigin) && snapshot is not null;
            if (!continuing)
            {
                Visibility = VisibilityState.FootprintCoverage;
                Status = $"Hidden: sampling full-size cloth support ({spacing:F2}y spacing, {rays} rays; {(support.Reanchoring ? "reanchoring" : "coverage incomplete")}).";
                LastTiming = new(1, rays, queryMilliseconds, queryMilliseconds, 0, originDrift);
                return;
            }
        }
        // A newly complete origin still needs current-center wall clearance.
        // A deadline here can hide this frame, but the completed replacement is
        // now active: next frame this mandatory query precedes optional refresh.
        if (checkedOrigin != snapshot!.Identity && continuityOrigin != snapshot.Identity
            && (!TryCompress(wantedCenter, centerFloor, snapshot.Identity.CompressionOrigin, out var candidateCenter)
            || Vector2.Distance(candidateCenter, wantedCenter) > .06f))
        { Visibility = VisibilityState.OriginClearance; Status = "Hidden: validating full-size cloth on the current side of the obstacle."; return; }
        if (snapshot.Width != snapshot.Height) throw new InvalidOperationException("Cloth support must publish a square lattice.");
        if (!snapshot.MatchesCenterFloor(wantedCenter, centerFloor))
        {
            support.Reset(); requestedOrigin = default; epoch++;
            Visibility = VisibilityState.CenterMismatch;
            Status = "Hidden: cached cloth layer does not match the current center floor.";
            return;
        }
        // Material dimensions are a user choice, never a cache-progress signal.
        // Full support was required above; don't map the whole rug onto a tiny
        // confirmed center patch or change its radius while coverage catches up.
        var materialHalf = wantedHalf;
        var buildStarted = Stopwatch.GetTimestamp();
        var coarse = ClothSurface.Build(snapshot.Center, snapshot.Half, centerFloor, snapshot.Contacts,
            snapshot.Width, (float)now, motion, snapshot.CellCeilings, pressures,
            snapshot.DiagonalParity, wantedCenter, materialHalf, planarCells: snapshot.PlanarCells, cellLifts: snapshot.CellLifts);
        var fine = SupportVisualRefinement.Refine(coarse, 3);
        diagnosticSupport = snapshot; diagnosticCoarse = coarse;
        SupportVisualRefinement.MapMaterial(fine, snapshot.Center, snapshot.Half, wantedCenter, materialHalf);
        Current = new(wantedCenter, materialHalf, centerFloor, fine, snapshot.Contacts.Length, epoch);
        Visibility = VisibilityState.Visible;
        ContinuedOriginThisUpdate = continuing;
        var buildMilliseconds = Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds;
        LastTiming = new(1, rays, queryMilliseconds + buildMilliseconds, queryMilliseconds, buildMilliseconds, originDrift);
        Status = $"Rolling full-size cloth · {spacing:F2}y collision support / {spacing / 3:F3}y visual mesh · {fine.Indices.Length / 3:N0} triangles · {rays} rays/update"
            + (continuing ? " · continuous anchor handoff" : "");
    }

    public bool TryVertex(SupportQueryIdentity identity, Vector2 nominal, out Vector3 contact)
    {
        contact = default;
        LastResult = QueryFloor(nominal, out var floor);
        if (LastResult == LayerQueryResult.Pending) return false;
        if (LastResult == LayerQueryResult.Unknown)
        {
            // Behind a wall, the nominal floor can be unknown even though its
            // compressed point is valid. Plane height guides this wall query
            // only; the compressed contact still needs its own actual floor.
            if (!layer.Layer.TryNearest(nominal, out var witness, out _)
                || !layer.Layer.TryMeasuredHeight(witness, nominal, out var hint)
                || !TryCompress(nominal, hint, identity.CompressionOrigin, out var fallback)
                || Vector2.DistanceSquared(fallback, nominal) < 1e-10f) return false;
            LastResult = QueryFloor(fallback, out floor);
            if (LastResult != LayerQueryResult.Success) return false;
            contact = floor.Position; return true;
        }
        if (!TryCompress(nominal, floor.Position.Y, identity.CompressionOrigin, out var point))
            return false;
        if (Vector2.DistanceSquared(point, nominal) < 1e-10f)
        { contact = floor.Position; LastResult = LayerQueryResult.Success; return true; }
        LastResult = QueryFloor(point, out floor);
        if (LastResult != LayerQueryResult.Success) return false;
        contact = floor.Position; return true;
    }

    public bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
    {
        ClothCellReplay? capturedCell = null;
        var witnessProgress = new ClothWitnessProgress();
        LastCellPlanar = false;
        LastCellLift = float.NaN;
        var at = (a + b + c + d) / 4;
        LastResult = QueryFloor(new(at.X, at.Z), out var floor);
        ceiling = floor.Position.Y;
        if (LastResult != LayerQueryResult.Success) return CellResult(at, "floor query");
        LastCellPlanar = layer.ProvesPlanarCell(floor, a, b, c, d, withinDeadline);
        // Corners and a center ray can all miss a narrow ridge. On a curved
        // cell, bound every measured triangle over its complete footprint.
        // Keep an actual planar proof so ordinary slopes remain smooth.
        if (!LastCellPlanar)
        {
            // Complete the actual footprint proof: corner/center rays alone
            // need never visit a small intervening face. Probe the uncovered
            // fragment, then retry with the newly measured geometry. Discovery
            // itself resumes across frames and shares the native ray budget.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                LastResult = layer.Layer.TryCellCeiling(floor, a, b, c, d, out ceiling, withinDeadline);
                if (LastResult != LayerQueryResult.Success && replay.WantsCell && !replayCopiedThisUpdate)
                {
                    replayCopiedThisUpdate = true;
                    var started = Stopwatch.GetTimestamp();
                    try { capturedCell = new(layer.Layer.CaptureReplay(), floor, a, b, c, d, LastResult,
                        layer.Layer.CellCeilingFailure, layer.Layer.CellCeilingMissingWitness)
                        { SupportEpoch = epoch, SnapshotCopyMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds }; }
                    catch (Exception) { replay.FailCapture(); }
                }
                if (LastResult != LayerQueryResult.Unknown || layer.Layer.CellCeilingMissingWitness is not { } missing) break;
                // A ray can succeed on an already-known, locally disconnected
                // floor patch. The same uncovered witness after that success
                // is still Unknown, not resumable work: do not spend four
                // identical rays or keep claiming per-call progress. A new
                // framework attempt starts fresh; all geometry gates remain.
                var beforeDiscovery = layer.Layer.DiscoveryStamp;
                if (!witnessProgress.CanDiscover(missing, beforeDiscovery)) break;
                LastResult = QueryFloor(missing, out _, ClothFloorRayContext.CellMissingWitness);
                witnessProgress.Record(missing, LastResult, beforeDiscovery, layer.Layer.DiscoveryStamp);
                if (capturedCell is { DiscoveryResult: null })
                    capturedCell = capturedCell with { DiscoveryTarget = missing, DiscoveryResult = LastResult };
                if (LastResult != LayerQueryResult.Success) break;
                // A measured witness alone is not complete-cell coverage.
                LastResult = LayerQueryResult.Pending;
            }
        }
        if (capturedCell is not null) replay.RecordCell(capturedCell with { AttemptResult = LastResult });
        if (LastResult == LayerQueryResult.Success)
            LastCellLift = LastCellPlanar ? 0 : layer.Layer.CellCeilingLift;
        return CellResult(at, LastCellPlanar ? "planar" : $"surface coverage ({layer.Layer.CellCeilingFailure})");
    }

    private bool CellResult(Vector3 at, string phase)
    {
        if (LastResult == LayerQueryResult.Success) { cellSuccess++; return true; }
        if (LastResult == LayerQueryResult.Pending) cellPending++; else cellUnknown++;
        if (firstCellFailure == "none") firstCellFailure = $"{phase} {LastResult} at {at}";
        return false;
    }

    private bool TryCompress(Vector2 wanted, float floor, Vector3 origin, out Vector2 compressed)
    {
        compressed = wanted;
        LastResult = LayerQueryResult.Unknown;
        var center = new Vector2(origin.X, origin.Z);
        var delta = wanted - center; var length = delta.Length();
        if (length < .05f) { LastResult = LayerQueryResult.Success; return true; }
        var targetFloor = new Vector3(wanted.X, floor, wanted.Y);
        var direct = CastClearance(origin, targetFloor + Vector3.UnitY * .18f);
        var result = direct.Result;
        var blockingPoint = direct.Point;
        if (result == ClothClearanceResult.Unknown && clearanceHitFloor)
        {
            // The straight endpoint chord can enter a convex rock. Follow only
            // actual shared floor portals, and still test EVERY segment for a
            // wall. Membership in the same connected graph is not clearance.
            var originFloor = origin - Vector3.UnitY * .18f;
            var pathResult = LayerQueryResult.Pending;
            var capturedThisCall = false;
            var witnessProgress = new ClothWitnessProgress();
            Vector3[] path = [];
            for (var attempt = 0; attempt < 4; attempt++)
            {
                pathResult = layer.Layer.TrySurfacePath(originFloor, targetFloor, out path, withinDeadline);
                if (pathResult == LayerQueryResult.Unknown && replay.WantsPath && !replayCopiedThisUpdate)
                {
                    // Copy only after the actual failed query. At most ONE
                    // copy per update so its cost cannot manufacture the other
                    // captured query's deadline failure in this same frame.
                    replayCopiedThisUpdate = true;
                    var started = Stopwatch.GetTimestamp();
                    try { replay.RecordPath(new(layer.Layer.CaptureReplay(), originFloor, targetFloor, pathResult,
                        layer.Layer.SurfacePathFailure, layer.Layer.SurfacePathMissingWitness)
                        { SupportEpoch = epoch, SnapshotCopyMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds }); capturedThisCall = true; }
                    catch (Exception) { replay.FailCapture(); }
                }
                if (pathResult != LayerQueryResult.Unknown || layer.Layer.SurfacePathMissingWitness is not { } missing) break;
                // Success on an already-known face is not new corridor proof.
                // The cell path uses this same bounded progress policy; don't
                // burn four identical discoveries here while other work waits.
                var beforeDiscovery = layer.Layer.DiscoveryStamp;
                if (!witnessProgress.CanDiscover(missing, beforeDiscovery)) break;
                pathResult = QueryFloor(missing, out _, ClothFloorRayContext.PathMissingWitness);
                witnessProgress.Record(missing, pathResult, beforeDiscovery, layer.Layer.DiscoveryStamp);
                if (capturedThisCall) replay.RecordPathDiscovery(missing, pathResult);
                if (pathResult != LayerQueryResult.Success) break;
                pathResult = LayerQueryResult.Pending;
            }
            if (capturedThisCall) replay.RecordPathAttemptResult(pathResult);
            if (pathResult != LayerQueryResult.Success)
            {
                LastResult = pathResult;
                if (pathResult == LayerQueryResult.Pending) pathPending++; else pathUnknown++;
                return false;
            }
            for (var i = 0; i < path.Length; i++) path[i] += Vector3.UnitY * .18f;
            result = clearance.Query(epoch, originFloor, targetFloor, path, queryTime, castClearance, out blockingPoint);
        }
        switch (result)
        {
            case ClothClearanceResult.Clear:
                LastResult = LayerQueryResult.Success;
                return true;
            case ClothClearanceResult.Pending:
                LastResult = LayerQueryResult.Pending;
                pathPending++;
                return false;
            case ClothClearanceResult.Blocked:
                var distance = Vector2.Dot(new(blockingPoint.X - origin.X, blockingPoint.Z - origin.Z), delta / length);
                if (distance < .05f || distance > length) return false;
                compressed = center + delta / length * Math.Max(.03f, distance - .06f);
                LastResult = LayerQueryResult.Success;
                return true;
            default:
                pathUnknown++;
                return false;
        }
    }

    private ClothClearanceCast CastClearance(Vector3 from, Vector3 to)
    {
        clearanceHitFloor = false;
        var distance = Vector3.Distance(from, to);
        if (!float.IsFinite(distance)) return new(ClothClearanceResult.Unknown);
        if (distance <= .001f) return new(ClothClearanceResult.Clear);
        var direction = (to - from) / distance;
        var travelled = 0f;
        for (var segment = 0; segment < 4; segment++)
        {
            if (!CanQuery) return new(ClothClearanceResult.Pending);
            rays++;
            if (!BGCollisionModule.RaycastMaterialFilter(from + direction * travelled, direction, out var hit, distance - travelled))
                return new(ClothClearanceResult.Clear);
            var normal = FloorSamplePolicy.CollisionNormal(hit.Normal, hit.V1, hit.V2, hit.V3);
            if (normal.LengthSquared() < 1e-10f) return new(ClothClearanceResult.Unknown);
            var triangle = new LayerTriangle(hit.V1, hit.V2, hit.V3, Vector3.Normalize(normal));
            if (layer.Layer.IsVerifiedCurb(triangle)
                || layer.Layer.IsVerifiedCurbTopCrossing(from, to, hit.Point, triangle))
            {
                layer.Layer.ObserveExact(triangle);
                var next = Vector3.Dot(hit.Point - from, direction) + .005f;
                if (next <= travelled) return new(ClothClearanceResult.Unknown);
                if (next >= distance) return new(ClothClearanceResult.Clear);
                travelled = next; continue;
            }
            if (firstClearanceFailure == "none")
                firstClearanceFailure = FormattableString.Invariant($"from={from}, to={to}, hit={hit.Point}, normalY={triangle.Normal.Y:F4}, connected={layer.Layer.Contains(triangle)}");
            // A steep face is floor-chord evidence only after the cloth layer
            // actually admits its finite shared portal. An arbitrary slanted
            // wall does not become a clear segment or a traversable floor.
            var connector = false;
            var measuredHit = new LayerFloorHit(hit.Point, triangle);
            if (!triangle.Walkable && layer.Layer.IsMeasuredSupport(measuredHit))
            {
                connector = layer.Layer.Contains(triangle);
                if (connector) layer.Layer.ObserveExact(triangle);
                else if (chordObservationAttempts < 2 && withinDeadline())
                {
                    chordObservationAttempts++;
                    connector = layer.Layer.TryProbe(new(hit.Point.X, hit.Point.Z), out var probe)
                        && layer.Layer.AcceptMeasuredClothConnector(probe, measuredHit);
                    if (connector) retainedChordHits++;
                }
            }
            if (Math.Abs(triangle.Normal.Y) > .45f || connector)
            {
                floorChordHits++;
                clearanceHitFloor = triangle.Normal.Y > .45f || connector;
                // The engine already returned this exact floor face. Retain
                // at most two novel observations under the existing deadline,
                // through the SAME local band/shared-edge admission as a floor
                // ray. Never seed a new layer from this hit or call it clear:
                // the surface path and every lifted segment must still pass.
                if (triangle.Walkable && !layer.Layer.Contains(triangle)
                    && chordObservationAttempts < 2 && withinDeadline())
                {
                    chordObservationAttempts++;
                    if (layer.Layer.TryRetainMeasuredFloorHit(new(hit.Point, triangle))) retainedChordHits++;
                }
                return new(ClothClearanceResult.Unknown);
            }
            wallHits++;
            return new(ClothClearanceResult.Blocked, hit.Point);
        }
        return new(ClothClearanceResult.Unknown);
    }

    private LayerQueryResult QueryFloor(Vector2 target, out LayerFloorHit floor,
        ClothFloorRayContext context = ClothFloorRayContext.LayerTarget)
    {
        // Diagnostic context follows the actual discovery call, including any
        // resumed frontier ray at another XZ. It is NOT inferred from hit XZ.
        var previousContext = floorRayContext; var previousTarget = floorRequestedTarget;
        floorRayContext = context; floorRequestedTarget = target;
        try { return layer.Query(target, this, out floor); }
        finally { floorRayContext = previousContext; floorRequestedTarget = previousTarget; }
    }

    public bool TryFloor(ClothFloorProbe probe, out LayerFloorHit floor)
        => TryFloor(probe, out floor, measuredCloth: false);

    public bool TryMeasuredClothFloor(ClothFloorProbe probe, out LayerFloorHit floor)
        => TryFloor(probe, out floor, measuredCloth: true);

    private bool TryFloor(ClothFloorProbe probe, out LayerFloorHit floor, bool measuredCloth)
    {
        floor = default;
        if (!probe.Valid || !CanQuery) return false;
        var capture = replay.WantsFloorRay(floorRayContext);
        var started = capture ? Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency : 0;
        rays++;
        var found = BGCollisionModule.RaycastMaterialFilter(new(probe.Position.X, probe.StartY, probe.Position.Y), -Vector3.UnitY, out var hit, probe.Length);
        var completed = capture ? Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency : 0;
        if (!found)
        {
            if (capture) replay.RecordFloorRay(new(territory, epoch, queryTime, started, completed, rays,
                floorRayContext, floorRequestedTarget, probe, false, default, default, default, ClothFloorRayOutcome.Miss));
            return false;
        }
        var normal = FloorSamplePolicy.CollisionNormal(hit.Normal, hit.V1, hit.V2, hit.V3);
        var measuredNormal = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : normal;
        var triangle = new LayerTriangle(hit.V1, hit.V2, hit.V3, measuredNormal);
        var candidate = new LayerFloorHit(hit.Point, triangle);
        var valid = measuredCloth
            ? ClothFloorQueryPolicy.AcceptMeasuredCloth(probe, candidate, out _)
            : ClothFloorQueryPolicy.Accept(probe, hit.Point, normal, out _) && candidate.Valid;
        if (valid) floor = candidate;
        if (capture)
        {
            // Preserve already-returned native geometry even after rejection.
            // Recording itself adds no ray, graph copy or layer admission.
            // An accepted steep candidate is not yet connected support; the
            // distinct diagnostic outcome must not label it player-walkable.
            var outcome = valid && !triangle.Walkable
                ? ClothFloorRayOutcome.AcceptedClothConnector
                : ClothFloorRayReplay.Describe(probe, true, hit.Point, normal, valid);
            replay.RecordFloorRay(new(territory, epoch, queryTime, started, completed, rays,
                floorRayContext, floorRequestedTarget, probe, true, hit.Point, hit.Normal, triangle,
                outcome));
        }
        return valid;
    }

    public bool TryWall(Vector3 from, Vector3 to, out LayerTriangle triangle)
    {
        triangle = default;
        var length = Vector3.Distance(from, to);
        if (!CanQuery || !float.IsFinite(length) || length <= .001f || length > 1) return false;
        rays++;
        if (!BGCollisionModule.RaycastMaterialFilter(from, (to - from) / length, out var hit, length)) return false;
        var normal = FloorSamplePolicy.CollisionNormal(hit.Normal, hit.V1, hit.V2, hit.V3);
        if (normal.LengthSquared() < 1e-10f) return false;
        triangle = new(hit.V1, hit.V2, hit.V3, Vector3.Normalize(normal));
        return triangle.Valid;
    }

    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
