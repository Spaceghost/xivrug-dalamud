using System.Numerics;
using System.Diagnostics;

namespace XivSurface.RenderedGeometry;

/// <summary>No retained native pointers. ResourceId is an observation key, NOT a content hash.</summary>
public readonly record struct TerrainInstance(ulong InstanceKey, string ModelPath, uint ResourceId, Matrix4x4 World,
    Vector3 Minimum, Vector3 Maximum, bool StaticTransformEligible = true);
public readonly record struct TerrainInterest(Vector3 Center, Vector3 HalfExtent);
public enum TerrainCaptureCompleteness { Incomplete, CompleteTerrainPlates }
public readonly record struct TerrainTriangle(Vector3 A, Vector3 B, Vector3 C, ulong InstanceKey, uint ResourceId,
    string ModelPath, string ContentSha256, int SourceMesh, int SourceSubmesh, int SourceTriangle,
    uint CullingGrid, string Material);

public sealed class TerrainCandidateBatch
{
    private readonly TerrainTriangle[] triangles;
    internal TerrainCandidateBatch(uint zone, ulong generation, ulong sourceObservation, string revision, TerrainInterest interest, TerrainTriangle[] triangles,double buildMilliseconds,int unreviewedMaterialTriangles)
    { Zone = zone; GeometryGeneration = generation; SourceObservationGeneration=sourceObservation; ProviderRevision = revision; Interest = interest; this.triangles = (TerrainTriangle[])triangles.Clone(); BuildMilliseconds=buildMilliseconds; UnreviewedMaterialTriangles=unreviewedMaterialTriangles; }
    public uint Zone { get; }
    public ulong GeometryGeneration { get; }
    public ulong SourceObservationGeneration { get; }
    public double BuildMilliseconds { get; }
    public string ProviderRevision { get; }
    public TerrainInterest Interest { get; }
    /// <summary>Faces whose world bounds intersect this interest box, omitted because their material
    /// is unreviewed. Zero is only absence of this particular omission, not complete-world proof.</summary>
    public int UnreviewedMaterialTriangles { get; }
    public string Confidence => "AssumedKnownVanilla";
    public bool LoadedContentVerified => false;
    public bool AuthorizesWorldSupport => false;
    public bool CoversAllSceneMaterials => false;
    public int Lod => 0;
    public ReadOnlySpan<TerrainTriangle> Triangles => triangles;
}

public readonly record struct TerrainStreamState(string Phase, TerrainCandidateBatch? Batch, bool WorkerPending,
    ulong ObservationGeneration,double ObservationCapturedSeconds)
{
    /// <summary>Freshness for the latest polled diagnostic batch, not render-frame proof.
    /// Consumers must poll again before use after lifecycle/replacement invalidation.</summary>
    public TerrainCandidateBatch? CandidateAt(uint zone,double now,double maximumAge=.25)
        => Phase=="CandidateReady"&&Batch is{} batch&&zone!=0&&batch.Zone==zone&&double.IsFinite(now)
            &&double.IsFinite(maximumAge)&&maximumAge is >0 and <=.5&&now>=ObservationCapturedSeconds
            &&now-ObservationCapturedSeconds<=maximumAge ? batch:null;
}

/// <summary>Single-worker, latest-request coordinator. Request/Poll never read files, wait for
/// tasks, or invoke native APIs. A canceled but uncooperative provider occupies the only slot;
/// it cannot create abandoned-worker fanout. Caller supplies current copied observations and
/// explicitly opts in; a null profile is unavailable. Dispose is nonblocking.</summary>
public sealed class RenderedTerrainStream : IDisposable
{
    public const int MaximumInstances = 4, MaximumTriangles = 8192;
    private sealed record RequestData(long Serial, uint Zone, ulong Generation, TerrainInstance[] Instances,
        TerrainInterest Interest, TerrainContentProfile Profile, string Revision, long InvalidationEpoch,double CapturedSeconds);
    private sealed record Completion(long Serial, TerrainCandidateBatch? Batch, string Status);
    private readonly object gate = new();
    private readonly ITerrainCandidateLoader loader;
    // Worker-only cache. Revision in key ensures replacement-provider invalidation.
    private readonly Dictionary<(string Revision, string Path, uint ResourceId), RenderedMdlGeometry> cache = new();
    private Task<Completion>? worker;
    private RequestData? active;
    private CancellationTokenSource? cancellation;
    private Task? cancellationCallbacks;
    private RequestData? pending;
    private RequestData? observed;
    private TerrainCandidateBatch? current;
    private long serial;
    private long invalidationEpoch;
    private ulong lastObservationGeneration;
    private double lastCapturedSeconds;
    private string phase = "ProfileUnavailable";
    private bool disposed;
    public RenderedTerrainStream(ITerrainCandidateLoader loader) => this.loader = loader;

    public void Request(uint zone, ulong generation, IReadOnlyList<TerrainInstance> instances, TerrainCaptureCompleteness completeness,
        TerrainInterest interest, double capturedSeconds, TerrainContentProfile? profile = null, string providerRevision = "unobserved-vanilla-assumption")
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var validation = Validate(zone, generation, instances, completeness, interest, profile, providerRevision);
            var metadataValid=zone!=0&&generation!=0&&double.IsFinite(capturedSeconds)&&capturedSeconds>=0;
            if(!metadataValid||capturedSeconds<lastCapturedSeconds||generation<=lastObservationGeneration)
                validation="StaleObservation";
            else
            {
                // A fresh incomplete/unsupported/unavailable observation still supersedes all
                // older scene observations. It must not let late pre-invalidation geometry revive.
                lastObservationGeneration=generation;lastCapturedSeconds=capturedSeconds;
            }
            if(validation!="Pending")
            {
                current=null;pending=null;observed=null;serial=checked(serial+1);phase=validation;
                CancelOutstanding();return;
            }
            var owned = new TerrainInstance[instances.Count];
            for (var i = 0; i < owned.Length; i++) owned[i] = instances[i];
            var next=new RequestData(serial,zone,generation,owned,interest,profile!,providerRevision,invalidationEpoch,capturedSeconds);
            if(observed is{} previous&&SameObservation(previous,next))
            {
                // Same geometry and complete fresh evidence: preserve batch and in-flight work.
                // Terminal load failures also remain stable until explicit invalidate/retry.
                observed=next;return;
            }
            current=null;pending=null;serial=checked(serial+1);phase="Pending";
            observed=next with{Serial=serial};pending=observed;
            // Refreshing the same content observations must not repeatedly cancel a cold
            // archive read before it can warm the cache. Its old batch is still discarded.
            if (active is { } prior && !SameContent(prior, pending)) CancelOutstanding();
            StartIfIdle();
        }
    }

    public TerrainStreamState Poll()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (worker is { IsCompleted: true } && cancellationCallbacks?.IsCompleted!=false)
            {
                var done = worker.GetAwaiter().GetResult(); // Completed only; Run contains exceptions.
                worker = null; active = null; _=cancellationCallbacks?.Exception; cancellationCallbacks=null;
                cancellation?.Dispose(); cancellation = null;
                if (done.Serial == serial && pending is null) { current = done.Batch; phase = done.Status; }
            }
            StartIfIdle();
            return new(phase, current, worker is not null,observed?.Generation??0,observed?.CapturedSeconds??0);
        }
    }

    public void Invalidate(string reason = "Invalidated")
    {
        lock (gate)
        {
            if (disposed) return;
            serial = checked(serial + 1); current = null; pending = null; observed=null; CancelOutstanding();
            invalidationEpoch = checked(invalidationEpoch + 1);
            phase = reason is "ZoneChanged" or "ReplacementChanged" or "Disposed" ? reason : "Invalidated";
        }
    }

    private void StartIfIdle()
    {
        if (worker is not null || pending is null) return;
        var request = pending; pending = null; cancellation = new();
        active = request;
        var token = cancellation.Token;
        worker = Task.Run(() => Run(request, token));
    }
    private void CancelOutstanding()
    {
        if(cancellation is not null)cancellationCallbacks??=cancellation.CancelAsync();
    }

    private static bool SameContent(RequestData a, RequestData b) => a.Zone == b.Zone
        && a.Revision == b.Revision && a.InvalidationEpoch == b.InvalidationEpoch && a.Profile.Fingerprint == b.Profile.Fingerprint
        && a.Instances.Length == b.Instances.Length
        && a.Instances.All(old => b.Instances.Any(now => old.ModelPath == now.ModelPath && old.ResourceId == now.ResourceId));
    private static bool SameObservation(RequestData a,RequestData b)=>SameContent(a,b)&&a.Interest==b.Interest
        &&a.Instances.All(old=>b.Instances.Contains(old));

    private async Task<Completion> Run(RequestData request, CancellationToken ct)
    {
        try
        {
            var began=Stopwatch.GetTimestamp();
            var contentRevision = $"{request.Profile.Fingerprint}:{request.InvalidationEpoch}:{request.Revision}";
            var wanted = request.Instances.Select(i => (contentRevision, i.ModelPath, i.ResourceId)).ToHashSet();
            foreach (var key in cache.Keys.Where(k => !wanted.Contains(k)).ToArray()) cache.Remove(key);
            var triangles = new List<TerrainTriangle>();
            var unreviewedMaterialTriangles = 0;
            foreach (var instance in request.Instances)
            {
                ct.ThrowIfCancellationRequested();
                var key = (contentRevision, instance.ModelPath, instance.ResourceId);
                if (!cache.TryGetValue(key, out var geometry))
                {
                    geometry = await loader.LoadAsync(instance.ModelPath, request.Profile, contentRevision, ct);
                    ct.ThrowIfCancellationRequested();
                    if (geometry is null) return new(request.Serial, null, "ContentProfileMismatch");
                    if (cache.Count >= MaximumInstances) return new(request.Serial, null, "CacheBudget");
                    cache[key] = geometry;
                }
                if (!request.Profile.TryAsset(instance.ModelPath, out var asset)
                    || geometry.Identifier != instance.ModelPath || geometry.Sha256 != asset.Sha256)
                    return new(request.Serial, null, "ContentProfileMismatch");
                if (!Append(geometry, instance, asset, request.Profile,request.Interest, triangles, ref unreviewedMaterialTriangles, ct))
                    return new(request.Serial, null, "GeometryBudgetOrInvalid");
            }
            ct.ThrowIfCancellationRequested();
            return new(request.Serial, new(request.Zone, (ulong)request.Serial,request.Generation, request.Revision, request.Interest, triangles.ToArray(),Stopwatch.GetElapsedTime(began).TotalMilliseconds,unreviewedMaterialTriangles), "CandidateReady");
        }
        catch (OperationCanceledException) { return new(request.Serial, null, "Canceled"); }
        catch (Exception) { return new(request.Serial, null, "SourceUnavailable"); } // No raw paths/errors in status.
    }

    private static bool Append(RenderedMdlGeometry geometry, TerrainInstance instance, TerrainAssetProfile asset,TerrainContentProfile profile,
        TerrainInterest interest, List<TerrainTriangle> output, ref int unreviewedMaterialTriangles, CancellationToken ct)
    {
        var meshes = geometry.Meshes.ToArray().ToDictionary(m => m.SourceMesh);
        var minimum = interest.Center - interest.HalfExtent; var maximum = interest.Center + interest.HalfExtent;
        foreach (var range in geometry.DrawRanges)
        {
            ct.ThrowIfCancellationRequested();
            if (range.RawVisibilityField >= asset.CullingGridCount || !meshes.TryGetValue(range.SourceMesh, out var mesh)) return false;
            var material = geometry.Materials[mesh.Material];
            var reviewedMaterial = profile.IncludesMaterial(material);
            for (var at = range.FirstIndex; at < range.FirstIndex + range.IndexCount; at += 3)
            {
                if ((at & 255) == 0) ct.ThrowIfCancellationRequested();
                var a = Vector3.Transform(geometry.Positions[geometry.Indices[at]], instance.World);
                var b = Vector3.Transform(geometry.Positions[geometry.Indices[at + 1]], instance.World);
                var c = Vector3.Transform(geometry.Positions[geometry.Indices[at + 2]], instance.World);
                if (!Finite(a) || !Finite(b) || !Finite(c)) return false;
                var lo = Vector3.Min(a, Vector3.Min(b, c)); var hi = Vector3.Max(a, Vector3.Max(b, c));
                if (lo.X > maximum.X || hi.X < minimum.X || lo.Y > maximum.Y || hi.Y < minimum.Y || lo.Z > maximum.Z || hi.Z < minimum.Z) continue;
                // Report nearby omitted material instead of making a partial profile appear
                // locally complete. Far-away material is not an omission in this interest box.
                if (!reviewedMaterial) { unreviewedMaterialTriangles++; continue; }
                if (output.Count == MaximumTriangles) return false; // Never silently truncate.
                output.Add(new(a, b, c, instance.InstanceKey, instance.ResourceId, geometry.Identifier, geometry.Sha256,
                    mesh.SourceMesh, range.SourceSubmesh, (at - mesh.FirstIndex) / 3, range.RawVisibilityField, material));
            }
        }
        return true;
    }

    private static string Validate(uint zone, ulong generation, IReadOnlyList<TerrainInstance> instances, TerrainCaptureCompleteness completeness,
        TerrainInterest interest, TerrainContentProfile? profile, string revision)
    {
        if (profile is null) return "ProfileUnavailable";
        if (completeness != TerrainCaptureCompleteness.CompleteTerrainPlates) return "IncompleteTerrainInventory";
        if (zone == 0 || generation == 0 || instances.Count is < 1 or > MaximumInstances || revision.Length is < 1 or > 128
            || revision.Any(char.IsControl) || !Finite(interest.Center) || !Finite(interest.HalfExtent)
            || interest.HalfExtent.X is <= 0 or > 8 || interest.HalfExtent.Y is <= 0 or > 8 || interest.HalfExtent.Z is <= 0 or > 8)
            return "InvalidObservation";
        var seen = new HashSet<ulong>();
        foreach (var item in instances)
        {
            if (!seen.Add(item.InstanceKey) || !item.StaticTransformEligible || !ValidTransform(item.World)
                || !Finite(item.Minimum) || !Finite(item.Maximum) || item.Minimum.X > item.Maximum.X || item.Minimum.Y > item.Maximum.Y || item.Minimum.Z > item.Maximum.Z)
                return "InvalidObservation";
            if (!profile.TryAsset(item.ModelPath, out _)) return "UnsupportedModel";
        }
        return "Pending";
    }

    private static bool ValidTransform(Matrix4x4 m)
    {
        // First profile covers actual static terrain translation; do not assume unknown parent
        // scale/rotation composition. Reflection, nonuniform scale and projective transforms fail.
        var t = m.Translation; m.Translation = Vector3.Zero;
        return Finite(t) && m == Matrix4x4.Identity;
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)
        && Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))) <= 1_000_000;
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; current = null; pending = null; CancelOutstanding(); phase = "Disposed";
            // CTS cleanup after the sole worker settles; no sync wait on caller/UI thread.
            var source = cancellation; cancellation = null;
            if (worker is { } running) _ = Task.WhenAll(running,cancellationCallbacks??Task.CompletedTask)
                .ContinueWith(done => { _=done.Exception; source?.Dispose(); }, TaskScheduler.Default);
            else source?.Dispose();
        }
    }
}
