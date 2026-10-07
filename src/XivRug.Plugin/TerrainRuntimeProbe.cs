using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Data;
using Lumina.Data.Structs;
using XivSurface.Core;
using XivSurface.RenderedGeometry;

namespace XivRug.Plugin;

/// <summary>Explicit development diagnostic only. A ten-second window copies
/// actual nearby terrain descriptors on Framework.Update at most ten times/sec;
/// one worker reads and hashes bounded installed archive resources. No physics,
/// renderer, saved configuration, model replacement or game input is changed.</summary>
internal sealed class TerrainRuntimeProbe : IDisposable
{
    private const double WindowSeconds = 10, CaptureInterval = .1;
    private static readonly Vector3 HalfExtent = new(3, 2, 3);
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private readonly StaticRenderInventory inventory;
    private readonly IPluginLog log;
    private readonly IDataManager data;
    private readonly RenderedTerrainStream stream;
    private readonly TerrainContentProfile profile = TerrainContentProfile.OptInKnownVanillaStairs();
    private int requested;
    private uint zone;
    private double expiresAt, nextCapture, nextReport, previousNow;
    private bool active, disposed;
    private StaticRenderInventory.Snapshot? capture;

    public TerrainRuntimeProbe(StaticRenderInventory inventory, IDataManager data, IPluginLog log)
    {
        this.inventory = inventory; this.data = data; this.log = log;
        stream = new(new ProfiledTerrainLoader(new InstalledArchiveReader(data)));
    }

    // Safe from the command caller: native/file work starts only in Update.
    public void Request() => Interlocked.Exchange(ref requested, 1);

    public void Update(bool permitted, uint territory, Vector3? player)
    {
        if (disposed) return;
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        try
        {
            var start = Interlocked.Exchange(ref requested, 0) != 0;
            if (start)
            {
                Stop();
                if (!permitted || territory == 0 || player is null || !StaticRenderDescriptorPolicy.Finite(player.Value)
                    || data.HasModifiedGameDataFiles)
                { log.Information("XivRug terrain probe unavailable: scene or installed archive gate refused."); return; }
                active = true; zone = territory; expiresAt = now + WindowSeconds;
                previousNow = now; nextCapture = nextReport = now;
                log.Information("XivRug terrain probe started for10s: assumed known-vanilla DEV profile; archive hashes do not prove loaded content; no physics activation.");
            }
            if (!active) return;
            if (!permitted || territory != zone || player is null || !StaticRenderDescriptorPolicy.Finite(player.Value)
                || now < previousNow || data.HasModifiedGameDataFiles)
            { log.Information("XivRug terrain probe stopped: scene/archive/clock changed."); Stop(); return; }
            previousNow = now;
            if (now >= expiresAt)
            { Report(stream.Poll(), final: true); Stop(); return; }
            if (now >= nextCapture)
            {
                nextCapture = now + CaptureInterval;
                // Stable local chart avoids rebuilding for sub-millimetre movement.
                // Radius5 encloses the entire3x2x3 interest half-box, not just its XZ centre.
                var p = player.Value;
                var center = new Vector3(MathF.Round(p.X * 2) * .5f, MathF.Round(p.Y * 2) * .5f, MathF.Round(p.Z * 2) * .5f);
                capture = inventory.CaptureTerrain(zone, center, 5);
                var instances = capture.Instances.Where(d => d.Kind == StaticRenderKind.TerrainPlate)
                    .Select(d => new TerrainInstance(d.InstanceKey, d.ModelPath, d.ResourceId, d.World,
                        d.BoundsMinimum, d.BoundsMaximum, d.Issues == StaticRenderIssue.None)).ToArray();
                var complete = capture.TerrainOnly && capture.Complete && capture.UnresolvedTerrain == 0;
                stream.Request(zone, capture.Generation, instances,
                    complete ? TerrainCaptureCompleteness.CompleteTerrainPlates : TerrainCaptureCompleteness.Incomplete,
                    new(center, HalfExtent), capture.CapturedSeconds, profile, "DEV-installed-archive-assumption");
            }
            var state = stream.Poll();
            if (now >= nextReport) { nextReport = now + 1; Report(state, final: false); }
        }
        catch (Exception error)
        {
            // No paths/file contents/exception payloads in failure diagnostics.
            log.Warning("XivRug terrain probe stopped: {Kind}", error.GetType().Name);
            Stop();
        }
    }

    private void Report(TerrainStreamState state, bool final)
    {
        // CaptureTerrain timestamped its observation after Update's initial
        // scheduling clock. Evaluate freshness AFTER capture and polling, not
        // against that earlier time; never retimestamp the source to compensate.
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var batch = state.CandidateAt(zone, now);
        log.Information("XivRug terrain probe: phase={Phase}, final={Final}, completeTraversal={Complete}, unresolved={Unresolved}, outside={Outside}, nearby={Nearby}, shown={Shown}, workerPending={Pending}, freshTriangles={Triangles}, excludedMaterialTriangles={Omissions}; confidence=AssumedKnownVanilla, loadedContentVerified=false, collisionWorldComplete=false, physicsActivated=false; descriptors={Descriptors}",
            state.Phase, final, capture?.Complete ?? false, capture?.UnresolvedTerrain ?? 0,
            capture?.OutsideInterestTerrain ?? 0, capture?.Instances.Count ?? 0, Math.Min(capture?.Instances.Count ?? 0, 8),
            state.WorkerPending, batch?.Triangles.Length ?? 0, batch?.UnreviewedMaterialTriangles ?? -1,
            JsonSerializer.Serialize(capture?.Instances.Take(8).ToArray() ?? [], Json));
    }

    private void Stop()
    {
        active = false; capture = null;
        stream.Invalidate(); // Nonblocking; an uncancellable read retains the sole worker slot.
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; active = false; Interlocked.Exchange(ref requested, 0); capture = null;
        stream.Dispose();
    }

    /// <summary>Borrowed IDataManager; never disposed by this diagnostic.
    /// The reviewed installed Lumina allocates exactly RawFileSize then reconstructs
    /// into a non-expanding MemoryStream over that byte[]. Metadata and read both
    /// use the same SqPack model/standard header prefix. Immutable installed
    /// archives during the call are a TRUST ASSUMPTION: pre/post checks cannot
    /// defeat a privileged concurrent rewrite between those operations.
    /// HasModifiedGameDataFiles=false is not proof that Penumbra replaces nothing.</summary>
    private sealed class InstalledArchiveReader(IDataManager data) : ITerrainAssetReader
    {
        public ValueTask<ReadOnlyMemory<byte>?> ReadAsync(string path, int maximumBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.HasModifiedGameDataFiles) throw new InvalidDataException("Modified archive");
            var metadata = data.GameData.GetFileMetadata(path);
            if (metadata is not { } info) return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            var before = Header(info);
            if (!TerrainArchiveReadPolicy.Allows(before, maximumBytes, path.EndsWith(".mdl", StringComparison.Ordinal)))
                throw new InvalidDataException("Archive metadata cap");
            cancellationToken.ThrowIfCancellationRequested();
            // Already on the stream's sole worker. No second Task.Run or detached IO.
            var resource = data.GetFile<FileResource>(path);
            cancellationToken.ThrowIfCancellationRequested();
            var after = data.GameData.GetFileMetadata(path);
            if (data.HasModifiedGameDataFiles || resource is null || after is not { } current
                || !TerrainArchiveReadPolicy.Matches(before, Header(current), (uint)resource.FileInfo.Type,
                    resource.FileInfo.RawFileSize, resource.Data.Length))
                throw new InvalidDataException("Archive changed");
            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(resource.Data);
        }

        private static TerrainArchiveHeader Header(SqPackFileInfo info) => new(info.Size, (uint)info.Type, info.RawFileSize, info.NumberOfBlocks);
    }
}
