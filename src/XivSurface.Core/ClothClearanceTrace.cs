using System.Numerics;

namespace XivSurface.Core;

public enum ClothClearanceResult { Unknown, Pending, Clear, Blocked }

/// <summary>The adapter classifies actual native collision hits. Pending means
/// this segment has not been proved clear; Point is used only for Blocked.</summary>
public readonly record struct ClothClearanceCast(ClothClearanceResult Result, Vector3 Point = default);

/// <summary>Resumes bounded wall checks over an already proved, above-floor
/// polyline. This helper does not discover floor connectivity or classify hits.
/// Successful prefixes expire from their first real clear query, never from a
/// later request, and cannot survive changed path geometry or floor identity.</summary>
public sealed class ClothClearanceTrace
{
    public const int MaximumEntries = 1024;
    public const int MaximumPoints = 257;
    public const double DefaultEvidenceLifetimeSeconds = .5;
    private const float HitTolerance = .02f;
    private readonly record struct Key(long Generation, Vector3 OriginFloor, Vector3 TargetFloor);
    private sealed class Work(Vector3[] path, double requestedAt)
    {
        public readonly Vector3[] Path = path;
        public int NextSegment;
        public double FirstClearAt = double.NaN;
        public double LastRequested = requestedAt;
    }
    private readonly Dictionary<Key, Work> pending = [];
    private readonly List<Key> retired = [];
    private readonly double lifetime;
    private double clock = double.NegativeInfinity;
    public int PendingCount => pending.Count;

    public ClothClearanceTrace(double evidenceLifetimeSeconds = DefaultEvidenceLifetimeSeconds)
    {
        if (!double.IsFinite(evidenceLifetimeSeconds) || evidenceLifetimeSeconds is <= 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(evidenceLifetimeSeconds));
        lifetime = evidenceLifetimeSeconds;
    }

    public void Reset()
    {
        pending.Clear(); retired.Clear(); clock = double.NegativeInfinity;
    }

    public ClothClearanceResult Query(long generation, Vector3 originFloor, Vector3 targetFloor,
        ReadOnlySpan<Vector3> aboveFloorPath, double now,
        Func<Vector3, Vector3, ClothClearanceCast> cast, out Vector3 blockingPoint)
    {
        ArgumentNullException.ThrowIfNull(cast);
        blockingPoint = default;
        if (!double.IsFinite(now) || now < 0) { Reset(); return ClothClearanceResult.Unknown; }
        if (now < clock) Reset();
        if (now != clock)
        {
            // One bounded sweep per update time, not per lattice vertex.
            retired.Clear();
            foreach (var pair in pending)
                if (now - pair.Value.LastRequested > lifetime
                    || double.IsFinite(pair.Value.FirstClearAt) && now - pair.Value.FirstClearAt > lifetime)
                    retired.Add(pair.Key);
            foreach (var keyToRemove in retired) pending.Remove(keyToRemove);
            clock = now;
        }
        if (generation < 0 || !Finite(originFloor) || !Finite(targetFloor)) return ClothClearanceResult.Unknown;
        var key = new Key(generation, originFloor, targetFloor);
        if (!ValidPath(aboveFloorPath, originFloor, targetFloor))
        { pending.Remove(key); return ClothClearanceResult.Unknown; }
        if (pending.TryGetValue(key, out var work) && !aboveFloorPath.SequenceEqual(work.Path))
        { pending.Remove(key); work = null; }
        if (work is null)
        {
            if (pending.Count >= MaximumEntries) return ClothClearanceResult.Unknown;
            work = new(aboveFloorPath.ToArray(), now);
            pending.Add(key, work);
        }
        work.LastRequested = now;
        try
        {
            // At most MaximumPoints-1 callbacks, even with a zero-cost adapter.
            while (work.NextSegment < work.Path.Length - 1)
            {
                var from = work.Path[work.NextSegment]; var to = work.Path[work.NextSegment + 1];
                if (from == to) { work.NextSegment++; continue; }
                var result = cast(from, to);
                switch (result.Result)
                {
                    case ClothClearanceResult.Pending:
                        return ClothClearanceResult.Pending;
                    case ClothClearanceResult.Clear:
                        if (!double.IsFinite(work.FirstClearAt)) work.FirstClearAt = now;
                        work.NextSegment++;
                        break;
                    case ClothClearanceResult.Blocked:
                        pending.Remove(key);
                        if (!OnSegment(result.Point, from, to)) return ClothClearanceResult.Unknown;
                        blockingPoint = result.Point;
                        return ClothClearanceResult.Blocked;
                    default:
                        pending.Remove(key);
                        return ClothClearanceResult.Unknown;
                }
            }
            // Only unfinished work is retained. Refreshing a completed vertex
            // must obtain fresh clearance rather than reuse a cached verdict.
            pending.Remove(key);
            return ClothClearanceResult.Clear;
        }
        catch
        {
            pending.Remove(key);
            throw;
        }
    }

    private static bool ValidPath(ReadOnlySpan<Vector3> path, Vector3 origin, Vector3 target)
    {
        if (path.Length is < 2 or > MaximumPoints) return false;
        foreach (var point in path) if (!Finite(point)) return false;
        // Floor identity is separate from the adapter's clearance height. Do
        // not permit a caller to attach a path to different XZ endpoints.
        return path[0].X == origin.X && path[0].Z == origin.Z
            && path[^1].X == target.X && path[^1].Z == target.Z
            && path[0].Y >= origin.Y && path[^1].Y >= target.Y;
    }

    private static bool OnSegment(Vector3 point, Vector3 from, Vector3 to)
    {
        if (!Finite(point)) return false;
        var delta = to - from; var length = delta.LengthSquared();
        if (!float.IsFinite(length) || length <= 0) return false;
        var fraction = Vector3.Dot(point - from, delta) / length;
        var closest = from + Math.Clamp(fraction, 0, 1) * delta;
        return Vector3.DistanceSquared(point, closest) <= HitTolerance * HitTolerance;
    }

    private static bool Finite(Vector3 point) => MathEx.Finite(point)
        && Math.Max(Math.Max(Math.Abs(point.X), Math.Abs(point.Y)), Math.Abs(point.Z)) <= 1_000_000;
}
