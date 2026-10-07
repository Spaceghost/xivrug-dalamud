using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivSurface.Core;

/// <summary>Bounded offline diagnostic format, never a runtime geometry input.</summary>
public static class ClothCollisionReportFile
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true, IgnoreReadOnlyProperties = true, MaxDepth = 24,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static byte[] Encode(ClothCollisionReport report)
    {
        Validate(report);
        using var stream = new LimitedStream();
        JsonSerializer.Serialize(stream, report, Options);
        return stream.ToArray();
    }

    public static ClothCollisionReport Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException("Replay file size rejected.");
        var report = JsonSerializer.Deserialize<ClothCollisionReport>(bytes, Options)
            ?? throw new InvalidDataException("Missing replay report.");
        Validate(report);
        return report;
    }

    private static void Validate(ClothCollisionReport report)
    {
        if (report is null || report.Version is not (1 or 2) || report.Zone == 0
            || !double.IsFinite(report.RequestedAt) || report.RequestedAt < 0
            || !double.IsFinite(report.CompletedAt) || report.CompletedAt < report.RequestedAt
            || report.Completion is null || report.Completion.Length > 160)
            throw new InvalidDataException("Replay report header rejected.");
        if (report.PathUnknown is { } path)
        {
            Check(path.Layer, path.Reason, path.SupportEpoch);
            if (report.Version == 2 && (!MathEx.Finite(path.From) || !MathEx.Finite(path.To)
                || !Metadata(path.Result, path.MissingWitness, path.DiscoveryTarget, path.DiscoveryResult,
                    path.AttemptResult, path.SnapshotCopyMilliseconds))) throw new InvalidDataException("Replay path metadata rejected.");
        }
        if (report.CellPending is { } cell)
        {
            Check(cell.Layer, cell.Reason, cell.SupportEpoch);
            if (report.Version == 2 && (!MathEx.Finite(cell.Center.Position) || !cell.Center.Triangle.Valid
                || !MathEx.Finite(cell.A) || !MathEx.Finite(cell.B) || !MathEx.Finite(cell.C) || !MathEx.Finite(cell.D)
                || !Metadata(cell.Result, cell.MissingWitness, cell.DiscoveryTarget, cell.DiscoveryResult,
                    cell.AttemptResult, cell.SnapshotCopyMilliseconds))) throw new InvalidDataException("Replay cell metadata rejected.");
        }
        if (report.RawFloorRays.IsDefault || report.RawFloorRays.Length > ClothCollisionCapture.MaximumRawRays)
            throw new InvalidDataException("Replay floor receipt cap rejected.");
        var ordinary = 0; var witness = 0;
        foreach (var ray in report.RawFloorRays)
        {
            if (ray.IsWitness) witness++; else ordinary++;
            if (ordinary > ClothCollisionCapture.MaximumOrdinaryRays || witness > ClothCollisionCapture.MaximumWitnessRays
                || ray.Zone != report.Zone || ray.SupportEpoch < 0 || ray.RayOrdinal is < 1 or > 100
                || !double.IsFinite(ray.QueryTime) || ray.QueryTime < report.RequestedAt || ray.QueryTime > report.CompletedAt
                || !double.IsFinite(ray.NativeStartedSeconds) || ray.NativeStartedSeconds < 0
                || !double.IsFinite(ray.NativeCompletedSeconds) || ray.NativeCompletedSeconds < ray.NativeStartedSeconds
                || !Enum.IsDefined(ray.Context) || !Enum.IsDefined(ray.Outcome) || !ray.Probe.Valid
                || (ray.Context == ClothFloorRayContext.Direct ? ray.RequestedTarget is not null
                    : ray.RequestedTarget is not { } target || !MathEx.Finite(target))
                || !MathEx.Finite(ray.HitPoint) || !MathEx.Finite(ray.RawNormal)
                || !MathEx.Finite(ray.Triangle.A) || !MathEx.Finite(ray.Triangle.B) || !MathEx.Finite(ray.Triangle.C)
                || !MathEx.Finite(ray.Triangle.Normal)
                || ray.Outcome == ClothFloorRayOutcome.AcceptedClothConnector
                    && (report.Version != 2 || !ValidConnectorReceipt(ray))
                || !ray.NativeHit && (ray.Outcome != ClothFloorRayOutcome.Miss || ray.HitPoint != default
                    || ray.RawNormal != default || ray.Triangle != default)
                || ray.NativeHit && ray.Outcome == ClothFloorRayOutcome.Miss)
                throw new InvalidDataException("Replay floor receipt metadata rejected.");
        }
        void Check(FloorReplayState layer, string reason, long? supportEpoch)
        {
            if (supportEpoch is < 0 || layer is null || layer.Version is not (1 or 2)
                || report.Version == 1 && layer.Version != 1 || layer.Faces.IsDefault
                || layer.Faces.Length > LocalFloorLayer.MaximumReplayFaces || layer.Portals.IsDefault
                || layer.Portals.Length > LocalFloorLayer.MaximumDirectedPortals
                || reason is null || reason.Length > 160)
                throw new InvalidDataException("Replay geometry cap rejected.");
            foreach (var edge in layer.Portals)
                if (edge is null || edge.Risers.IsDefault || edge.Risers.Length > 4)
                    throw new InvalidDataException("Replay portal cap rejected.");
            if (layer.Version == 1 && (layer.Scope is not null || layer.Faces.Any(face => face is null || face.Role is not null)))
                throw new InvalidDataException("Version 1 cannot carry explicit cloth roles.");
            // V1 uses its original restore predicates; v2 additionally checks
            // explicit roles and finite topology. Neither exposes a live layer.
            try { LocalFloorLayer.ValidateReplayState(layer); }
            catch (ArgumentException error) { throw new InvalidDataException("Replay roles or topology rejected.", error); }
        }
        static bool Metadata(LayerQueryResult result, System.Numerics.Vector2? witness, System.Numerics.Vector2? target,
            LayerQueryResult? discovery, LayerQueryResult? attempt, double milliseconds)
            => Enum.IsDefined(result) && (witness is not { } w || MathEx.Finite(w))
                && (target is not { } t || MathEx.Finite(t)) && (discovery is not { } d || Enum.IsDefined(d))
                && (attempt is not { } a || Enum.IsDefined(a)) && double.IsFinite(milliseconds) && milliseconds >= 0;
        static bool ValidConnectorReceipt(ClothFloorRayReplay ray)
        {
            // Match the actual floor reader, including its small-normal
            // fallback threshold. Keep RawNormal untouched in the receipt.
            var normal = FloorSamplePolicy.CollisionNormal(ray.RawNormal,
                ray.Triangle.A, ray.Triangle.B, ray.Triangle.C);
            var length = normal.Length();
            // Eligibility depends on BOTH the supplied and geometric slopes.
            // Describe's ordinary-floor label alone cannot represent that
            // conjunction near the walkability threshold.
            return ray.NativeHit && ClothConnectorGeometry.Eligible(ray.Triangle)
                && ClothConnectorGeometry.Contains(ray.Triangle, ray.HitPoint, true)
                && ray.Probe.Valid && Vector2.DistanceSquared(ray.Probe.Position,new(ray.HitPoint.X,ray.HitPoint.Z)) <= .02f*.02f
                && ray.HitPoint.Y >= ray.Probe.MinimumY && ray.HitPoint.Y <= ray.Probe.MaximumY
                && float.IsFinite(length) && length >= 1e-6f && normal.Y > 0
                && Vector3.Dot(Vector3.Normalize(normal), ray.Triangle.Normal) >= .99f;
        }
    }

    /// <summary>Call on an owned worker. Unique CreateNew file, never overwrite.
    /// A cancellation can leave a completed diagnostic, never a changed config.</summary>
    public static string WriteNew(string directory, ClothCollisionReport report, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var bytes = Encode(report);
        cancellation.ThrowIfCancellationRequested();
        var name = "cloth-replay-" + Guid.NewGuid().ToString("N") + ".json";
        var path = Path.Combine(directory, name);
        // Fixed Dalamud config path supplied at construction, never a command
        // argument. First diagnostic use may precede directory creation.
        Directory.CreateDirectory(directory);
        cancellation.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush();
        return name;
    }

    public static ClothCollisionReport Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Replay file size rejected.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Replay file grew during read.");
        return Decode(bytes);
    }

    private sealed class LimitedStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        private void Check(int count)
        { if (count > MaximumBytes - Position) throw new InvalidDataException("Replay output cap exceeded."); }
    }
}
