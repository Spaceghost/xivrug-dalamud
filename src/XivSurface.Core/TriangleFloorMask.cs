using System.Numerics;

namespace XivSurface.Core;

/// <summary>Triangles supplied by a validated navmesh adapter, never guessed from depth.</summary>
public readonly record struct FloorTriangle(Vector3 A, Vector3 B, Vector3 C);

/// <summary>
/// Immutable reference mask for one reachable navmesh layer. Samples only inside
/// actual triangles: no nearest-point fill across holes or interpolation between
/// stacked floors. For cached mask generation, not per-screen-pixel IPC.
/// </summary>
public sealed class TriangleFloorMask : IFloorMask
{
    private readonly FloorTriangle[] triangles;
    private readonly uint territory;
    private readonly long epoch;
    private readonly long layer;
    private readonly float maximumHeightError;

    public TriangleFloorMask(ReadOnlySpan<FloorTriangle> triangles, uint territory,
        long epoch, long layer, float maximumHeightError = 0.25f)
    {
        if (territory == 0 || epoch < 0 || layer < 0 || !float.IsFinite(maximumHeightError)
            || maximumHeightError < 0 || maximumHeightError > 0.25f
            || triangles.Length > 65536)
            throw new ArgumentOutOfRangeException(nameof(territory), "Invalid floor-mask bounds or identity.");
        foreach (var t in triangles)
        {
            if (!Valid(t.A) || !Valid(t.B) || !Valid(t.C)
                || Math.Abs(Area(t.A, t.B, t.C.X, t.C.Z)) < 1e-10)
                throw new ArgumentException("Floor triangles must be finite, nondegenerate in XZ, and inside world bounds.", nameof(triangles));
        }
        this.triangles = triangles.ToArray();
        this.territory = territory;
        this.epoch = epoch;
        this.layer = layer;
        this.maximumHeightError = maximumHeightError;
    }

    public bool TryMatch(Vector3 visibleWorldPosition, out FloorMatch match)
    {
        match = default;
        if (!Valid(visibleWorldPosition)) return false;
        var found = false;
        var best = double.PositiveInfinity;
        float height = 0;
        foreach (var t in triangles)
        {
            // Signed-area barycentrics work for either winding; using doubles
            // avoids overflow/cancellation in ordinary zone-coordinate ranges.
            var area = Area(t.A, t.B, t.C.X, t.C.Z);
            var c = Area(t.A, t.B, visibleWorldPosition.X, visibleWorldPosition.Z) / area;
            var a = Area(t.B, t.C, visibleWorldPosition.X, visibleWorldPosition.Z) / area;
            var b = 1 - a - c;
            if (a < 0 || b < 0 || c < 0) continue;
            var y = a * t.A.Y + b * t.B.Y + c * t.C.Y;
            var error = Math.Abs(y - visibleWorldPosition.Y);
            if (error > maximumHeightError || error >= best) continue;
            best = error;
            height = (float)y;
            found = true;
        }
        if (!found) return false;
        match = new(new Vector3(visibleWorldPosition.X, height, visibleWorldPosition.Z),
            layer, territory, epoch, true);
        return true;
    }

    private static double Area(Vector3 a, Vector3 b, float x, float z) =>
        ((double)b.X - a.X) * ((double)z - a.Z) - ((double)b.Z - a.Z) * ((double)x - a.X);

    private static bool Valid(Vector3 p) => MathEx.Finite(p)
        && MathF.Abs(p.X) <= 5000 && MathF.Abs(p.Y) <= 5000 && MathF.Abs(p.Z) <= 5000;
}
