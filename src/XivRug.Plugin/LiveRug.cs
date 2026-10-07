using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using XivSurface.Core;
using XivSurface.Dalamud;
using XivCloth.Core;
using XivRug.Rendering;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace XivRug.Plugin;

internal sealed unsafe partial class LiveRug : IDisposable
{
    private sealed record Snapshot(uint Territory, long Epoch, Vector2 Center, Vector2 HalfSize, float AnchorY, float[] Heights, GeometryFloorAtlas? Atlas = null);
    private sealed record FloorBuild(long Epoch, uint Territory, Snapshot? Frame);
    private sealed record RouteRequest(uint Territory, Vector3 Center, Vector2 HalfSize, Vector3[] Points, float Scale, bool MapMode);
    private sealed record RouteTexture(RouteRequest Request, float[] Values, NavigationVisual? Visual = null);
    private sealed record Visual(uint Territory, long Epoch, Vector2 Center, Vector2 HalfSize, Vector2 MapCenter, float Height, float MapScale, Vector4 Travel);
    private readonly IDalamudPluginInterface pi;
    private readonly ICallGateSubscriber<bool> ready;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> query;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearest;
    private readonly ICallGateSubscriber<nint,uint,uint,nint> worldDepth;
    private readonly Func<nint,uint,uint,nint> acquireWorldDepth;
    private readonly IPluginLog log;
    private readonly IGameInteropProvider interop;
    private readonly GameMapSource maps;
    private GameMapFrame? mapFrame;
    private uint loggedMap;
    private readonly RugFollowController follow = new();
    private volatile Visual? visual;
    private readonly LiveRugGpu gpu = new();
    private readonly ClothGpu clothGpu = new();
    private readonly ClothContactSampler cloth = new();
    private readonly ClothContactSolver clothContact = new();
    private long clothContactEpoch = long.MinValue;
    private readonly PianoRugSource piano;
    public string MusicStatus => piano.Status;
    private readonly CarpetLiftController jump = new();
    private volatile float clothLift;
    public ClothPressure[] FootPressure { get; set; } = [];
    // Publish contacts, their zone and their sampling time as one immutable
    // envelope. The native thread never follows game/skeleton pointers.
    public volatile ClothFootRenderFrame? FootFrame;
    internal NativeFootFrameCapture? FinalizedFeet { get; set; }
    private ClothFootPresentationFrame.Lease? submittingFinalizedFeet;
    private readonly Func<bool> finalizedSubmissionAllowed;
    private bool CanSubmitFinalizedFeet() => FinalizedFeet?.CanSubmit(submittingFinalizedFeet) == true;
    // Legacy character-pose assistance must not move feet to fit physical cloth.
    public bool HasCloth => !physicalMode && clothFrame is not null;
    public ClothMesh? SupportedCloth => physicalMode ? null : clothFrame?.Mesh;
    private volatile ClothContactSampler.Frame? clothFrame;
    private readonly PhysicalClothPresentation physicalPresentation = new();
    private double nextShadowObservation;
    private readonly Func<bool> physicalSubmissionAllowed;
    private PhysicalClothDrawFrame? submittingPhysical;
    private volatile bool physicalMode;
    public bool PhysicalClothEnabled => physicalMode;
    private readonly NativeRugPipeline nativePipeline = new();
    private NativeUiStage? nativeStage;
    private bool nativeMode, nativeStageFailed, loggedNativeDraw;
    private bool lastDrawWasCloth, loggedNativeCloth;
    private string nativeStatus = "Native composition is off.";
    private readonly object stageLock = new();
    private readonly ImDrawCallback callback;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private volatile Snapshot? current;
    private Snapshot? pending;
    private readonly HashSet<FloorTriangle> pendingTriangles = [];
    private Task<FloorBuild>? floorWork;
    private long samplingEpoch;
    private int cursor, valid, navHits, collisionHits;
    private bool loggedProbe;
    private uint territory;
    private float sampleY;
    private double refresh;
    private double nextClothTiming;
    private int clothUpdates, clothPublished, clothRays, clothDeferredRefresh, clothContinuedOrigin;
    private readonly int[] clothVisibilityCounts = new int[8];
    private double maximumClothUpdateMilliseconds, maximumClothBuildMilliseconds;
    private float maximumClothSpeed;
    private Vector2? previousClothCenter;
    private double previousClothTime;
    private double nextFootTiming;
    private int footAccepted, footExpired, footUntracked, footOther, maximumFootAgeMicroseconds;
    private Configuration? settings;
    private volatile bool disposed;
    private volatile bool gpuFaulted;
    private volatile bool footRenderBlocked;
    private readonly object renderLock = new();
    private RouteRequest? wantedRoute;
    private NavigationVisual? wantedRouteVisual;
    private Task<RouteTexture>? routeWork;
    private volatile RouteTexture? routeTexture;
    public string Status { get; private set; } = "Sampling ground…";
    public string MapStatus { get; private set; } = "Waiting for the current game's map.";
    public string CompositionStatus => (settings?.NativeUiComposition == true
        ? (nativeStage?.Status ?? nativeStatus)
        : "Background compositor: behind plugin windows, not native game UI.")
        + (footRenderBlocked ? physicalMode
            ? " · Hidden: waiting for a current foot-checked 3-D pose."
            : " · Hidden: waiting for fresh complete foot tracking." : "");

    public LiveRug(IDalamudPluginInterface pi, IPluginLog log, IGameInteropProvider interop, IDataManager data, ITextureProvider textures)
    {
        this.pi = pi; this.log = log; this.interop = interop;
        maps = new(data, textures);
        piano = new(pi);
        ready = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        query = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        nearest = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable");
        worldDepth = pi.GetIpcSubscriber<nint,uint,uint,nint>("XivWayfinder.v1.AcquireWorldDepth");
        acquireWorldDepth = AcquireWorldDepth;
        callback = Render;
        physicalSubmissionAllowed = CanSubmitPhysicalCloth;
        finalizedSubmissionAllowed = CanSubmitFinalizedFeet;
    }

    private nint AcquireWorldDepth(nint device,uint width,uint height)
    {
        // The optional provider can unload between HasFunction and InvokeFunc.
        // Absence keeps the rug's existing private fold-depth path available.
        try { return worldDepth.HasFunction ? worldDepth.InvokeFunc(device,width,height) : 0; }
        catch (Exception) { return 0; }
    }

    // Framework-only diagnostic: the sampler copies existing evidence; this
    // never invokes an additional native query or changes render admission.
    public bool BeginClothReplay(uint zone) => cloth.BeginReplay(zone, clock.Elapsed.TotalSeconds);
    public bool FinishClothReplay(uint zone, out ClothCollisionReport? report)
        => cloth.FinishReplay(zone, clock.Elapsed.TotalSeconds, out report);
    public void CancelClothReplay() => cloth.CancelReplay();

    // Runtime-controller seam, intentionally not a persisted/user-facing switch
    // yet. Turning it on never silently substitutes the old heightfield solver.
    public void SetPhysicalClothEnabled(bool enabled)
    {
        lock (renderLock)
        {
            if (disposed || physicalMode == enabled) return;
            physicalPresentation.Invalidate();
            physicalMode = enabled;
            current = pending = null; clothFrame = null; visual = null;
            cloth.Reset(); clothContact.Reset(); jump.Reset(); clothLift = 0;
            samplingEpoch++;
        }
    }

    // Called at the start of every framework observation, on capture failure,
    // and before solver/scene/owner resets. The lock covers actual GPU use too.
    public void InvalidatePhysicalCloth()
    {
        if (!physicalMode) return;
        lock (renderLock) physicalPresentation.Invalidate();
    }

    public PhysicalClothObservation? BeginPhysicalClothObservation(FootProxyPose? accepted,
        FootProxyPose? inspected, long sceneGeneration)
    {
        lock (renderLock)
        {
            if (disposed || gpuFaulted || !physicalMode || settings is not { Enabled: true, ClothSurface: true })
            { physicalPresentation.Invalidate(); return null; }
            // The check clock is sampled AFTER entering the serialization lock.
            // Foot observations retain their original immutable sample times.
            return physicalPresentation.BeginObservation(accepted, inspected, sceneGeneration,
                Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        }
    }

    public bool PublishPhysicalCloth(PhysicalClothObservation? observation, XpbdFootCapture capture,
        PhysicalClothChart chart)
    {
        lock (renderLock)
        {
            // Reject old jobs before any failure can revoke a newer packet.
            if (!physicalPresentation.IsCurrent(observation)) return false;
            if (disposed || gpuFaulted || !physicalMode || settings is not { Enabled: true, ClothSurface: true } config
                || visual is not { } appearance || appearance.Territory != territory
                || observation?.InspectedFeet.Identity.Zone != territory || !PhysicalChartMatches(config, chart))
            { physicalPresentation.Invalidate(); return false; }
            if (!physicalPresentation.TryPublish(observation, capture, chart,
                Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency)) return false;
            // This is projection metadata only. Solved XYZ are never translated
            // to the follow target or passed through old floor/foot ceilings.
            current = new(territory, samplingEpoch, chart.Center, chart.HalfSize, appearance.Height, []);
            visual = appearance with { Center = chart.Center, HalfSize = chart.HalfSize };
            Status = "Experimental 3-D cloth: foot-checked mesh published.";
            return true;
        }
    }

    private static bool PhysicalChartMatches(Configuration config, PhysicalClothChart chart)
    {
        var circle = config.Shape == FootprintShape.Circle;
        var half = circle ? new Vector2(config.Radius) : new Vector2(config.HalfWidth, config.HalfLength);
        return chart.Valid && chart.Circle == circle && chart.HalfSize == half
            && chart.Corner == (circle ? 0 : config.CornerRadius);
    }

    public void Update(uint zone, Vector3? player, Configuration config, NavigationRoute? route,
        Vector2? diagnosticFollowTarget = null, ClothFootRenderFrame? physicsFeet = null, bool airborne = false)
    {
        if (disposed) return;
        settings = config;
        ReportFootTiming();
        if (gpuFaulted) return;
        if (!config.Enabled || player is null || zone == 0)
        { InvalidatePhysicalCloth(); samplingEpoch++; current = pending = null; wantedRoute = null; routeTexture = null; visual = null; follow.Reset(); cloth.Reset(); clothContact.Reset(); piano.Reset(); clothFrame=null; jump.Reset(); clothLift=0; return; }
        if (territory != zone) { InvalidatePhysicalCloth(); samplingEpoch++; current = pending = null; territory = zone; refresh = 0; follow.Reset(); cloth.Reset(); clothContact.Reset(); piano.Reset(); clothFrame=null; jump.Reset(); clothLift=0; }
        var visualHalf = config.Shape == FootprintShape.Circle ? new Vector2(config.Radius) : new Vector2(config.HalfWidth, config.HalfLength);
        var center = new Vector2(player.Value.X, player.Value.Z);
        if (!follow.Step(diagnosticFollowTarget ?? center, clock.Elapsed.TotalSeconds, config.DragRadius, config.DragResponse, config.RugMotion))
        { InvalidatePhysicalCloth(); visual = null; current = pending = null; return; }
        visual = new(zone, samplingEpoch, follow.Center, visualHalf, center, player.Value.Y, config.MapRadius / Math.Min(visualHalf.X, visualHalf.Y), follow.Travel);
        center = follow.Center;
        piano.Update(clock.Elapsed.TotalSeconds, center, config.ClothSurface && config.RugMotion && config.PianoResponse, config.PianoStrength,
            config.ClothSurface && config.RugMotion && config.MusicVisualizationEnabled);
        if (physicalMode)
        {
            // Maintain map/route metadata while the runtime owner prepares an
            // independently admitted pose. No old sampling/foot correction is
            // used as a substitute when physical publication is unavailable.
            clothFrame = null;
            current = new(zone, samplingEpoch, center, visualHalf, player.Value.Y, []);
            if (!config.ClothSurface) InvalidatePhysicalCloth();
            Status = "Experimental 3-D cloth: waiting for a fresh admitted pose.";
            UpdateRoute(config.ShowRoute ? route : null);
            return;
        }
        // Floor samples are cached in world space independently from the moving
        // textile. The halo lets the visual anchor slide without moving a flat
        // ground plane or waiting for an entire replacement sampling sweep.
        if(config.ClothSurface)
        {
            try
            {
                var clothStarted = Stopwatch.GetTimestamp();
                cloth.Update(zone,center,visualHalf,player.Value,clock.Elapsed.TotalSeconds,config.RugMotion,FootPressure,airborne);
                // Reuse the sampler's verified actual-player layer. A separate
                // broad ray from above the player can hit a bridge overhead.
                if(cloth.AcceptedPlayerSupportHeight is { } playerFloor)
                    clothLift=jump.Update(player.Value.Y,playerFloor,clock.Elapsed.TotalSeconds,config.JumpCatch);
                else { jump.Reset(); clothLift=0; }
                var sampledCloth = cloth.Current;
                if (sampledCloth is not null)
                {
                    if (clothContactEpoch != sampledCloth.SupportEpoch)
                    { clothContact.Reset(); clothContactEpoch = sampledCloth.SupportEpoch; }
                    Span<ClothFootContact> contacts = stackalloc ClothFootContact[ClothFootClearance.MaximumContacts];
                    // Physics uses the earlier framework capture; render feet
                    // are independently reread/published after framework work.
                    var feet = physicsFeet;
                    if (feet is not null)
                        feet.TryGetClothContacts(zone, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, contacts, out _);
                    var mesh = clothContact.Update(sampledCloth.Mesh, contacts, clock.Elapsed.TotalSeconds, clothLift, piano.Impulses);
                    clothFrame = sampledCloth with { Mesh = mesh };
                }
                else clothFrame = null;
                if(clothFrame is {} fabric)
                {
                    current=new(zone,samplingEpoch,fabric.Center,fabric.HalfSize,fabric.AnchorY,[]);
                    visual=new(zone,samplingEpoch,fabric.Center,visualHalf,new(player.Value.X,player.Value.Z),player.Value.Y,config.MapRadius/Math.Min(visualHalf.X,visualHalf.Y),follow.Travel);
                }
                else current=null;
                ReportClothTiming(Stopwatch.GetElapsedTime(clothStarted).TotalMilliseconds);
                Status=$"{cloth.Status} {clothGpu.Status}";
                UpdateRoute(config.ShowRoute ? route : null);
            }
            catch(Exception ex) { Status="Cloth preparation stopped: "+ex.GetType().Name; log.Error(ex,"XivRug cloth sampling failed"); clothFrame=null; current=null; }
            return;
        }
        clothFrame=null; clothContact.Reset();
        var half = visualHalf + new Vector2(SamplingPadding(config));
        try
        {
            if (!ready.InvokeFunc()) { samplingEpoch++; current = pending = null; wantedRoute = null; routeTexture = null; Status = "Waiting for this zone's navmesh."; return; }
            if (floorWork is { IsCompleted: true } completed)
            {
                if (completed.IsCompletedSuccessfully && completed.Result.Epoch == samplingEpoch && completed.Result.Territory == territory)
                {
                    var candidate = completed.Result.Frame;
                    // Validate before publication: the native render thread
                    // must never see an obsolete build even for one draw.
                    current = candidate is not null && candidate.HalfSize == half
                        && ContainsFootprint(candidate, center, visualHalf)
                        && Math.Abs(candidate.AnchorY - player.Value.Y) <= 1 ? candidate : null;
                    refresh = clock.Elapsed.TotalSeconds + 2;
                    Status = current?.Atlas is { } geometry
                        ? $"Ground geometry: {geometry.TriangleCount} collision triangles, {geometry.OverflowedBins} overflow bins. {gpu.Status}"
                        : "Ground triangle validation failed; surface hidden.";
                }
                else if (completed.IsFaulted) log.Warning(completed.Exception, "Rug floor binning failed");
                floorWork = null;
            }
            if (current is not null && (current.HalfSize != half || !ContainsFootprint(current, center, visualHalf)
                || Math.Abs(current.AnchorY - player.Value.Y) > 1)) current = null;
            if (pending is not null && (pending.HalfSize != half || Vector2.Distance(pending.Center, center) > SamplingPadding(config)
                || Math.Abs(pending.AnchorY - player.Value.Y) > 1)) pending = null;
            if (pending is null && floorWork is null && (current is null || clock.Elapsed.TotalSeconds >= refresh
                || Vector2.Distance(current.Center, center) > 1.5f))
            {
                pending = new(territory, samplingEpoch, center, half, player.Value.Y, new float[LiveRugGpu.GridSize * LiveRugGpu.GridSize * 2]);
                cursor = valid = navHits = collisionHits = 0;
                pendingTriangles.Clear();
                sampleY = player.Value.Y;
            }
            if (pending is null) { UpdateRoute(config.ShowRoute ? route : null); return; }
            // Fixed query budget: never hundreds of IPC calls in a single frame.
            const int n = LiveRugGpu.GridSize;
            var started = Stopwatch.GetTimestamp();
            for (var budget = 0; budget < 48 && cursor < n * n
                && (budget == 0 || Stopwatch.GetElapsedTime(started).TotalMilliseconds < 2); budget++, cursor++)
            {
                var xz = pending.Center + (new Vector2(cursor % n, cursor / n) / (n - 1) * 2 - Vector2.One) * pending.HalfSize;
                // The final IPC argument is HORIZONTAL search extent, not ray
                // length. A wide search selects a nearby higher floor instead.
                var hit = query.InvokeFunc(new Vector3(xz.X, sampleY + 3, xz.Y), false, 0.05f);
                // Navmesh erosion leaves a narrow gap near walls/curbs. A
                // nearby reachable mesh point can anchor the layer, but only
                // actual collision directly beneath this texel fills it.
                if (hit is null) hit = nearest.InvokeFunc(new Vector3(xz.X, sampleY, xz.Y), 0.5f, 3);
                if (hit is not { } p || !float.IsFinite(p.Y)) continue;
                navHits++;
                var origin = new Vector3(xz.X, p.Y + 0.8f, xz.Y);
                var collided = BGCollisionModule.RaycastMaterialFilter(origin, -Vector3.UnitY, out var collision, 1.6f);
                if (collided) collisionHits++;
                var normal = FloorSamplePolicy.CollisionNormal(collision.Normal, collision.V1, collision.V2, collision.V3);
                if (!loggedProbe)
                {
                    loggedProbe = true;
                    log.Information("XivRug floor probe: mesh={Mesh}, collision={Hit}, at={Point}, normal={Normal}, triangle=({A}, {B}, {C})", p, collided, collision.Point, normal, collision.V1, collision.V2, collision.V3);
                }
                if (!collided || !FloorSamplePolicy.TryRefine(xz, sampleY, p, collision.Point, normal, out var floorY)) continue;
                var triangle = CanonicalTriangle(collision.V1, collision.V2, collision.V3);
                if (!GeometryFloorAtlas.TryValidateHit(triangle, collision.Point)) continue;
                // Keep fabric away from low solid geometry. Non-colliding grass
                // and foliage naturally do not participate in these probes.
                var clearanceOrigin = new Vector3(xz.X, floorY + 0.12f, xz.Y);
                const float probe = 0.24f;
                if (BGCollisionModule.RaycastMaterialFilter(clearanceOrigin, Vector3.UnitY, out _, 0.45f)
                    || BGCollisionModule.RaycastMaterialFilter(clearanceOrigin - Vector3.UnitX * probe, Vector3.UnitX, out _, probe * 2)
                    || BGCollisionModule.RaycastMaterialFilter(clearanceOrigin - Vector3.UnitZ * probe, Vector3.UnitZ, out _, probe * 2)) continue;
                pendingTriangles.Add(triangle);
                pending.Heights[cursor * 2] = floorY;
                pending.Heights[cursor * 2 + 1] = 1;
                valid++;
            }
            if (cursor == n * n)
            {
                var snapshot = pending;
                var triangles = pendingTriangles.ToArray();
                var epoch = samplingEpoch;
                var sampledTerritory = territory;
                floorWork = Task.Run(() =>
                {
                    ObstacleClearance.Apply(snapshot.Heights, LiveRugGpu.GridSize, snapshot.HalfSize);
                    return new FloorBuild(epoch, sampledTerritory,
                    GeometryFloorAtlas.TryBuild(triangles, snapshot.Center, snapshot.HalfSize, out var atlas)
                        && atlas is { TriangleCount: > 0 } ? snapshot with { Atlas = atlas } : null);
                });
                pending = null;
                Status = $"Validating {triangles.Length} floor triangles ({valid}/{n * n} samples). {gpu.Status}";
            }
            else Status = $"Sampling ground: {cursor}/{n * n}, {valid} matched (collision {collisionHits}). {gpu.Status}";
            UpdateRoute(config.ShowRoute ? route : null);
        }
        catch (Exception) { samplingEpoch++; current = pending = null; wantedRoute = null; routeTexture = null; Status = "Navmesh floor queries unavailable."; }
    }

    // Count hidden updates too: logging only a published mesh conceals cache
    // starvation and can make a failed collision path look healthy.
    private void ReportClothTiming(double updateMilliseconds)
    {
        var now = clock.Elapsed.TotalSeconds;
        clothUpdates++;
        clothVisibilityCounts[(int)cloth.Visibility]++;
        clothRays += cloth.RaycastsThisUpdate;
        maximumClothUpdateMilliseconds = Math.Max(maximumClothUpdateMilliseconds, updateMilliseconds);
        maximumClothBuildMilliseconds = Math.Max(maximumClothBuildMilliseconds, cloth.LastTiming?.BuildMilliseconds ?? 0);
        if (clothFrame is { } frame)
        {
            clothPublished++;
            if (cloth.RefreshDeferredThisUpdate) clothDeferredRefresh++;
            if (cloth.ContinuedOriginThisUpdate) clothContinuedOrigin++;
            if (previousClothCenter is { } previous && now > previousClothTime && now - previousClothTime < .2)
                maximumClothSpeed = Math.Max(maximumClothSpeed, Vector2.Distance(previous, frame.Center) / (float)(now - previousClothTime));
            previousClothCenter = frame.Center;
            previousClothTime = now;
        }
        else previousClothCenter = null;
        if (now < nextClothTiming) return;
        nextClothTiming = now + 20;
        log.Information("XivRug rolling cloth: updates={Updates}, published={Published}, hidden={Hidden}, rays={Rays}, max update={UpdateMs:F2}ms, max build={BuildMs:F2}ms, max visible center speed={Speed:F3}y/s, deferred refresh publications={Deferred}, continuous anchor handoffs={Continued}; {State}",
            clothUpdates, clothPublished, clothUpdates - clothPublished, clothRays,
            maximumClothUpdateMilliseconds, maximumClothBuildMilliseconds, maximumClothSpeed, clothDeferredRefresh, clothContinuedOrigin, cloth.Status);
        log.Information("XivRug music response: {State}; active cloth impulses={Count}; mode={Mode}, GPU submissions={Submissions}, renderer={Renderer}",
            piano.Status, piano.Impulses.Length, settings?.MusicVisualization.ToString() ?? "Unavailable", musicGpu.Submissions, musicGpu.Status);
        log.Information("XivRug support outcome counts: invalid={Invalid}, visible={Visible}, player floor={Player}, center floor={Center}, changed evidence={Changed}, incomplete footprint={Footprint}, origin clearance={Origin}, center mismatch={Mismatch}",
            clothVisibilityCounts[0], clothVisibilityCounts[1], clothVisibilityCounts[2], clothVisibilityCounts[3],
            clothVisibilityCounts[4], clothVisibilityCounts[5], clothVisibilityCounts[6], clothVisibilityCounts[7]);
        log.Information("XivRug floor geometry: {Geometry}; global cloth lift={Lift:F4}y", cloth.DescribeGeometry(), clothLift);
        log.Information("XivRug world lighting: {Lighting}", clothGpu.LightingDiagnostic);
        log.Information("XivRug native shadows: {Shadow}", clothGpu.ShadowDiagnostic);
        clothUpdates = clothPublished = clothRays = clothDeferredRefresh = clothContinuedOrigin = 0;
        Array.Clear(clothVisibilityCounts);
        maximumClothUpdateMilliseconds = maximumClothBuildMilliseconds = 0;
        maximumClothSpeed = 0;
    }

    private static FloorTriangle CanonicalTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        static int Compare(Vector3 x, Vector3 y)
        {
            var order = x.X.CompareTo(y.X);
            if (order == 0) order = x.Y.CompareTo(y.Y);
            return order == 0 ? x.Z.CompareTo(y.Z) : order;
        }
        if (Compare(a, b) > 0) (a, b) = (b, a);
        if (Compare(b, c) > 0) (b, c) = (c, b);
        if (Compare(a, b) > 0) (a, b) = (b, a);
        return new(a, b, c);
    }

    private static bool ContainsFootprint(Snapshot floor, Vector2 center, Vector2 halfSize)
    {
        var extent = Vector2.Abs(center - floor.Center) + halfSize;
        return extent.X <= floor.HalfSize.X && extent.Y <= floor.HalfSize.Y;
    }

    // Fine visual slack must not shrink the cache's walking/sampling allowance.
    private static float SamplingPadding(Configuration config) => Math.Max(4.5f, config.DragRadius + 3);

    // Rasterization runs on managed immutable data, never on game pointers or
    // IPC from a worker. Only one job exists, and obsolete results cannot flash
    // the previous journey/layer on screen while the next job is being built.
    private void UpdateRoute(NavigationRoute? route)
    {
        if (current is not { } floor || visual is not { } appearance || route is not { Points.Length: >= 2 })
        { wantedRoute = null; wantedRouteVisual = null; routeTexture = null; }
        else
        {
            wantedRouteVisual = route.Visual;
            var mapMode = settings?.ShowGameMap == true;
            var areaCenter = mapMode ? new Vector2(MathF.Round(appearance.MapCenter.X / 2) * 2, MathF.Round(appearance.MapCenter.Y / 2) * 2) : floor.Center;
            var center = new Vector3(areaCenter.X, floor.AnchorY, areaCenter.Y);
            var areaHalf = mapMode ? appearance.HalfSize * appearance.MapScale : floor.HalfSize;
            var scale = mapMode ? appearance.MapScale : 1;
            if (wantedRoute is not { } old || old.Territory != territory || old.Center != center
                || old.HalfSize != areaHalf || old.Scale != scale || old.MapMode != mapMode || !old.Points.AsSpan().SequenceEqual(route.Points))
            {
                wantedRoute = new(territory, center, areaHalf, (Vector3[])route.Points.Clone(), scale, mapMode);
                routeTexture = null;
            }
        }
        if (routeWork is { IsCompleted: true } completed)
        {
            if (completed.IsCompletedSuccessfully && ReferenceEquals(completed.Result.Request, wantedRoute))
                routeTexture = completed.Result with { Visual = wantedRouteVisual };
            else if (completed.IsFaulted) log.Warning(completed.Exception, "Rug route rasterization failed");
            routeWork = null;
        }
        if (routeWork is null && routeTexture is null && wantedRoute is { } request)
            routeWork = Task.Run(() => new RouteTexture(request,
                RouteRibbonField.Build(request.Points, request.Center, request.HalfSize, 0.6f * request.Scale)));
        // Progress/tint refreshes only the immutable envelope, not the expensive
        // raster or its GPU upload. Geometry and visual state publish together.
        if (routeTexture is { } readyRoute && ReferenceEquals(readyRoute.Request, wantedRoute)
            && !Equals(readyRoute.Visual, wantedRouteVisual))
            routeTexture = readyRoute with { Visual = wantedRouteVisual };
    }

    public void Queue()
    {
        // This is the UiBuilder.Draw boundary, after this frame's native game
        // drawing. Finish an unused native draw claim before arming the next
        // frame here, never in Framework.Update.
        lock (stageLock)
        {
            if (disposed) return;
            PrepareMap();
            var requested = settings?.NativeUiComposition == true;
            var changedMode = requested != nativeMode;
            if (changedMode)
            {
                StopNativeStage(); // never dispose a hook while holding renderLock
                lock (renderLock)
                {
                    gpu.Dispose(); clothGpu.Dispose(); nativePipeline.Dispose(); gpuFaulted = false;
                    loggedNativeDraw = false;
                }
                nativeMode = requested;
                nativeStageFailed = false;
                nativeStatus = requested ? "Waiting for a floor before creating the native stage." : "Native composition is off.";
            }
            if (gpuFaulted || settings?.Enabled != true)
            { StopNativeStage(); return; }
            if (current is null)
            {
                FinalizedFeet?.ObserveUiBoundary();
                // Observe only at the actual UI/render boundary while coverage
                // is still being repaired; this does not create or submit cloth.
                if (requested && clock.Elapsed.TotalSeconds >= nextShadowObservation)
                {
                    nextShadowObservation = clock.Elapsed.TotalSeconds + 20;
                    try
                    {
                        lock (renderLock)
                            NativeRugPipeline.VisitImmediateContext(pi.UiBuilder.DeviceHandle, context =>
                            { clothGpu.InspectNativeShadow(context); return true; });
                    }
                    catch (Exception error) { log.Warning(error, "XivRug native shadow observation unavailable"); }
                }
                // A temporary support gap is not a stage lifecycle change.
                // Keep shader discovery/the hook, but revoke the pending draw.
                nativeStage?.Disarm();
                return;
            }
            if (requested)
            {
                // Fail closed: a missing or failed native stage must never
                // silently fall back to painting over the game's HUD.
                if (nativeStageFailed) return;
                try
                {
                    nativeStage ??= new NativeUiStage(interop, log, RenderNative, () => pi.UiBuilder.DeviceHandle);
                    nativeStage.FindShaders();
                    nativeStage.TryDrawUiFallback();
                    nativeStage.NewFrame();
                }
                catch (Exception ex)
                {
                    StopNativeStage(); nativeStageFailed = true;
                    nativeStatus = "Native stage unavailable: " + ex.GetType().Name;
                    log.Error(ex, "XivRug native composition stopped; background fallback is disabled");
                }
                return;
            }
            // When switching off, this frame may already contain a native
            // draw. Wait until the following frame before background painting.
            if (!changedMode) ImGui.GetBackgroundDrawList().AddCallback(callback, null);
        }
    }

    public void Retry()
    {
        lock (stageLock)
        {
            if (disposed) return;
            StopNativeStage();
            lock (renderLock)
            {
                gpu.Dispose(); clothGpu.Dispose(); musicGpu.Dispose(); musicFaulted = false; nativePipeline.Dispose(); gpuFaulted = false;
                loggedNativeDraw = false;
                Status = "Retrying rug renderer…";
            }
            nativeStageFailed = false;
            nativeStatus = "Retrying native shader discovery…";
        }
    }

    private void Render(ImDrawList* list, ImDrawCmd* command)
    {
        lock (renderLock)
        {
            if (disposed || gpuFaulted || settings?.NativeUiComposition == true || nativeStage is not null) return;
            try
            {
                CommonDraw(pi.UiBuilder.DeviceHandle, ImGui.GetIO().DisplaySize);
            }
            catch (Exception ex) { StopAfterDrawFailure(ex); }
        }
    }

    private void RenderNative(nint context)
    {
        lock (renderLock)
        {
            if (disposed || gpuFaulted || current is null
                || settings is not { Enabled: true, NativeUiComposition: true }) return;
            try
            {
                var device = Device.Instance();
                if (device == null || device->SwapChain == null || device->SwapChain->BackBuffer == null) return;
                if (clock.Elapsed.TotalSeconds >= nextShadowObservation)
                {
                    nextShadowObservation = clock.Elapsed.TotalSeconds + 20;
                    clothGpu.InspectNativeShadow(context);
                }
                var backbuffer = device->SwapChain->BackBuffer;
                var viewport = new Vector2(backbuffer->ActualWidth, backbuffer->ActualHeight);
                var submitted = false;
                // No ImGui calls are permitted at the game's render-command
                // boundary. The envelope supplies an actual D3D device and
                // restores the native pipeline around the common draw.
                nativePipeline.TryDraw(context, (nint)backbuffer->D3D11Texture2D,
                    nativeDevice => submitted = CommonDraw(nativeDevice, viewport, nativeStageDraw: true));
                if (submitted && (!loggedNativeDraw || loggedNativeCloth != lastDrawWasCloth))
                {
                    loggedNativeDraw = true;
                    loggedNativeCloth = lastDrawWasCloth;
                    log.Information("XivRug first native GPU submission: renderer={Renderer}; stage={Stage}; pipeline={Pipeline}; gpu={Gpu}; submissions={Count}; sharedDepthSubmissions={SharedDepthCount}",
                        lastDrawWasCloth ? "cloth mesh" : "floor projection",
                        nativeStage?.Status ?? "UI shader boundary", nativePipeline.Status,
                        lastDrawWasCloth ? clothGpu.Status : gpu.Status,
                        lastDrawWasCloth ? clothGpu.Submissions : gpu.Submissions,
                        lastDrawWasCloth ? clothGpu.SharedDepthSubmissions : 0);
                }
            }
            catch (Exception ex) { StopAfterDrawFailure(ex); }
        }
    }

    // Caller holds renderLock. Both entry points use precisely the same floor
    // snapshot and optional route field; only their composition stage differs.
    private bool CommonDraw(nint device, Vector2 viewport, bool nativeStageDraw = false)
    {
        if (physicalMode) return DrawPhysicalCloth(device, nativeStageDraw);
        // Old/missing character protection is not permission to draw through
        // feet. Resume automatically on the next valid framework snapshot.
        var feet = FootFrame;
        Span<ClothFootContact> renderFeet = stackalloc ClothFootContact[ClothFootClearance.MaximumContacts];
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var gate = ClothFootRenderGate.MissingFrame;
        ClothFootPresentationFrame.Lease? finalLease = null;
        var finalized = nativeStageDraw && settings?.ClothSurface == true && FinalizedFeet is { } capture
            && capture.TryAcquire(territory, now, renderFeet, out finalLease);
        using var presentationLease = finalLease;
        var protectedFrame = finalized || feet is not null && (settings?.ClothSurface == true
            ? feet.TryGetClothRenderContacts(territory, now, renderFeet, out gate)
            : feet.TryGetRenderContacts(territory, now, renderFeet, out gate));
        if (finalized) gate = ClothFootRenderGate.Allowed;
        RecordFootTiming(gate, finalized ? now - finalLease!.Frame.SampledAt : feet is null ? double.NaN : now - feet.SampledAt);
        if (!protectedFrame)
        { footRenderBlocked = true; return false; }
        footRenderBlocked = false;
        if(settings is { Enabled:true, ClothSurface:true } clothConfig && clothFrame is {} fabric)
        {
            var clothMap=clothConfig.ShowGameMap && mapFrame?.TerritoryId==territory ? mapFrame : null;
            if(clothConfig.ShowGameMap && clothMap is null) return false;
            var routeCandidate=routeTexture;
            var clothNavigation=clothConfig.ShowRoute && routeCandidate?.Request.Territory==territory
                && routeCandidate.Request.MapMode==clothConfig.ShowGameMap ? routeCandidate : null;
            var clothArea=clothNavigation?.Request;
            var clothAppearance=visual;
            if(clothAppearance is null || clothAppearance.Territory!=territory) return false;
            var clothProjection=new RugProjection(fabric.Center,fabric.HalfSize,clothMap?.ShaderResourceView??0,clothMap?.Transform??default,
                clothAppearance.MapCenter,clothAppearance.MapScale,
                clothArea is null ? default : new Vector4(clothArea.Center.X,clothArea.Center.Z,clothArea.HalfSize.X,clothArea.HalfSize.Y),clothMap?.BackgroundShaderResourceView??0,
                clothNavigation?.Visual);
            var submitted=clothGpu.Submissions;
            var clothDrawSeconds = clock.Elapsed.TotalSeconds;
            var clothDrawLift = clothLift;
            submittingFinalizedFeet = finalLease;
            try
            {
                clothGpu.Draw(device,fabric.Mesh,clothProjection,clothConfig.Shape==FootprintShape.Circle,clothConfig.CornerRadius,
                    clothDrawSeconds,clothConfig.RugMotion,clothConfig.RugEdges,.98f,clothNavigation?.Values, wear: clothConfig.RugWear, lift: clothDrawLift, feet: renderFeet,
                    acquirePrivateDepth: nativeStageDraw && nativeMode ? acquireWorldDepth : null,
                    canSubmit: finalized ? finalizedSubmissionAllowed : null);
                if (nativeStageDraw && clothGpu.Submissions != submitted)
                    DrawMusic(device, fabric.Mesh, fabric.HalfSize, clothConfig, renderFeet, clothDrawSeconds, clothDrawLift,
                        finalized ? finalizedSubmissionAllowed : null);
            }
            finally { submittingFinalizedFeet = null; }
            lastDrawWasCloth = true;
            return clothGpu.Submissions!=submitted;
        }
        if (current is not { Atlas: not null } frame || visual is not { } appearance || settings is not { Enabled: true } config) return false;
        if (frame.Territory != appearance.Territory || frame.Territory != territory
            || frame.Epoch != appearance.Epoch || frame.Epoch != samplingEpoch
            || frame.HalfSize != appearance.HalfSize + new Vector2(SamplingPadding(config))
            || !ContainsFootprint(frame, appearance.Center, appearance.HalfSize)
            || Math.Abs(frame.AnchorY - appearance.Height) > 1) return false;
        var map = config.ShowGameMap && mapFrame?.TerritoryId == territory ? mapFrame : null;
        if (config.ShowGameMap && map is null) return false;
        var candidate = routeTexture;
        var navigation = config.ShowRoute && candidate?.Request.Territory == territory
            && candidate.Request.MapMode == config.ShowGameMap ? candidate : null;
        var area = navigation?.Request;
        var projection = new RugProjection(appearance.Center, appearance.HalfSize,
            map?.ShaderResourceView ?? 0, map?.Transform ?? default, appearance.MapCenter, appearance.MapScale,
            area is null ? default : new Vector4(area.Center.X, area.Center.Z, area.HalfSize.X, area.HalfSize.Y),
            map?.BackgroundShaderResourceView ?? 0, navigation?.Visual);
        var before = gpu.Submissions;
        gpu.Draw(device, frame.Center, frame.HalfSize, frame.Heights,
            viewport, config.Shape == FootprintShape.Circle, config.CornerRadius,
            (float)clock.Elapsed.TotalSeconds, config.RugMotion, config.RugEdges, config.Feather,
            navigation?.Values, projection, frame.Atlas, appearance.Travel, wear: config.RugWear, feet: renderFeet);
        lastDrawWasCloth = false;
        return gpu.Submissions != before;
    }

    // Caller holds renderLock through freshness validation, texture/depth lease,
    // GPU submission and enclosing pipeline restoration. No native foot reads.
    private bool DrawPhysicalCloth(nint device, bool nativeStageDraw)
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (!physicalPresentation.TryGet(territory, now, out var packet))
        { footRenderBlocked = true; return false; }
        footRenderBlocked = false;
        if (settings is not { Enabled: true, ClothSurface: true } config
            || packet is null || !PhysicalChartMatches(config, packet.Chart)
            || visual is not { } appearance || appearance.Territory != territory) return false;
        var map = config.ShowGameMap && mapFrame?.TerritoryId == territory ? mapFrame : null;
        if (config.ShowGameMap && map is null) return false;
        var candidate = routeTexture;
        var navigation = config.ShowRoute && candidate?.Request.Territory == territory
            && candidate.Request.MapMode == config.ShowGameMap ? candidate : null;
        var area = navigation?.Request;
        var chart = packet.Chart;
        var projection = new RugProjection(chart.Center, chart.HalfSize, map?.ShaderResourceView ?? 0,
            map?.Transform ?? default, appearance.MapCenter, appearance.MapScale,
            area is null ? default : new Vector4(area.Center.X, area.Center.Z, area.HalfSize.X, area.HalfSize.Y),
            map?.BackgroundShaderResourceView ?? 0, navigation?.Visual);
        var before = clothGpu.Submissions;
        submittingPhysical = packet;
        try
        {
            clothGpu.DrawIndexed(device, packet.Pose, projection, chart.Circle, chart.Corner,
                clock.Elapsed.TotalSeconds, config.RugMotion, config.RugEdges, .98f, navigation?.Values,
                wear: config.RugWear, acquirePrivateDepth: nativeStageDraw && nativeMode ? acquireWorldDepth : null,
                canSubmit: physicalSubmissionAllowed);
        }
        finally { submittingPhysical = null; }
        lastDrawWasCloth = true;
        return clothGpu.Submissions != before;
    }

    // Cached delegate, called under renderLock by ClothGpu AFTER resource and
    // upload work. Cold shader/device creation can outlive a valid foot frame.
    private bool CanSubmitPhysicalCloth()
    {
        var allowed = !disposed && !gpuFaulted && physicalMode
            && physicalPresentation.CanSubmit(submittingPhysical, territory,
                Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        if (!allowed) footRenderBlocked = true;
        return allowed;
    }

    private void RecordFootTiming(ClothFootRenderGate gate, double age)
    {
        // No native reads, allocations or logging from the render thread.
        switch (gate)
        {
            case ClothFootRenderGate.Allowed: Interlocked.Increment(ref footAccepted); break;
            case ClothFootRenderGate.Expired: Interlocked.Increment(ref footExpired); break;
            case ClothFootRenderGate.MissingFrame:
            case ClothFootRenderGate.IncompleteTracking: Interlocked.Increment(ref footUntracked); break;
            default: Interlocked.Increment(ref footOther); break;
        }
        if (!double.IsFinite(age) || age < 0) return;
        var microseconds = (int)Math.Min(age * 1_000_000, int.MaxValue);
        var previous = Volatile.Read(ref maximumFootAgeMicroseconds);
        while (microseconds > previous)
        {
            var observed = Interlocked.CompareExchange(ref maximumFootAgeMicroseconds, microseconds, previous);
            if (observed == previous) break;
            previous = observed;
        }
    }

    private void ReportFootTiming()
    {
        if (clock.Elapsed.TotalSeconds < nextFootTiming) return;
        nextFootTiming = clock.Elapsed.TotalSeconds + 20;
        var accepted = Interlocked.Exchange(ref footAccepted, 0);
        var expired = Interlocked.Exchange(ref footExpired, 0);
        var untracked = Interlocked.Exchange(ref footUntracked, 0);
        var other = Interlocked.Exchange(ref footOther, 0);
        var age = Interlocked.Exchange(ref maximumFootAgeMicroseconds, 0) / 1000d;
        if (accepted + expired + untracked + other == 0) return;
        log.Information("XivRug foot draw gate: accepted={Accepted}, expired={Expired}, untracked={Untracked}, other blocked={Other}, maximum sample age={AgeMs:F2}ms; current cloth expiry={ExpiryMs:F2}ms",
            accepted, expired, untracked, other, age, (FootFrame?.ClothMaximumAgeSeconds ?? ClothFootRenderFrame.MaximumAgeSeconds) * 1000);
    }

    private void StopAfterDrawFailure(Exception ex)
    {
        gpuFaulted = true; // a device fault must never change the persisted preference
        Status = "Rug GPU draw failed: " + ex.GetType().Name;
        log.Error(ex, "XivRug live draw failed; rendering disabled until retry or mode change");
    }

    private void PrepareMap()
    {
        lock (renderLock)
        {
            GameMapFrame? acquired = null;
            var agent = AgentMap.Instance();
            if (settings?.ShowGameMap == true && visual is not null && agent != null
                && maps.TryAcquire(territory, agent->CurrentMapId, out acquired))
            {
                MapStatus = $"Game map {acquired!.MapId}: {acquired.TexturePath}";
                if (loggedMap != acquired.MapId)
                {
                    loggedMap = acquired.MapId;
                    log.Information("XivRug game map acquired: {Id} {Path}; transform={Transform}; background={Background}",
                        acquired.MapId, acquired.TexturePath, acquired.Transform, acquired.BackgroundTexturePath);
                }
            }
            else MapStatus = settings?.ShowGameMap == true ? maps.Status : "Game map hidden (textile only).";
            var old = mapFrame;
            mapFrame = acquired;
            old?.Dispose();
        }
    }

    // Caller holds stageLock, never renderLock. An in-flight detour can finish
    // taking renderLock while the hook is being retired without a lock cycle.
    private void StopNativeStage()
    {
        var stage = nativeStage;
        nativeStage = null;
        stage?.Dispose();
    }

    public void Dispose()
    {
        lock (stageLock)
        {
            disposed = true;
            StopNativeStage();
            lock (renderLock)
            {
                current = pending = null; wantedRoute = null; routeTexture = null;
                physicalPresentation.Invalidate();
                visual = null; mapFrame?.Dispose(); mapFrame = null; maps.Dispose();
                gpu.Dispose(); clothGpu.Dispose(); musicGpu.Dispose(); nativePipeline.Dispose();
            }
        }
    }
}
