using System.Numerics;

namespace XivSurface.Core;

/// <summary>
/// Bounded collision-triangle bins for a GPU floor mask: actual triangle coverage
/// and triangle planes, never bilinear interpolation between unrelated surfaces.
/// This does not prove reachability or select a navmesh layer. The caller must first
/// anchor each accepted collision hit to that layer, and carry territory/epoch identity.
/// </summary>
public sealed class GeometryFloorAtlas
{
    public const int BinSize = 32;
    public const int SlotsPerBin = 16;
    public const int MaxTriangles = 1024;
    public const int IndexTextureWidth = BinSize * (SlotsPerBin / 4);
    public const int IndexTextureHeight = BinSize;
    public const int TriangleTextureWidth = 3;
    public const int TriangleTextureHeight = MaxTriangles;
    public const float MaximumHitPlaneError = 0.01f;
    private const float MaxCoordinate = 5000;
    private const double InsideEpsilon = 1e-8;

    private readonly FloorTriangle[] triangles;
    private readonly int[] counts;
    public Vector2 Center { get; }
    public Vector2 HalfSize { get; }
    public int TriangleCount => triangles.Length;
    public int OverflowedBins { get; }

    /// <summary>
    /// RGBA32F, 3 pixels wide by MaxTriangles rows: A.xyz, B.xyz, C.xyz (W=0).
    /// Upload without modification; changing the array invalidates atlas consistency.
    /// </summary>
    public float[] TriangleTexels { get; }

    /// <summary>
    /// RGBA32F, 128 pixels wide by 32 rows. For bin (x,z), four pixels at
    /// (4*x+group,z) supply its sixteen triangle IDs. ID=index+1, 0=empty.
    /// Every slot of an overflowing bin is -1; consumers must reject that bin.
    /// Upload without modification; changing the array invalidates atlas consistency.
    /// </summary>
    public float[] IndexTexels { get; }

    private GeometryFloorAtlas(FloorTriangle[] triangles, Vector2 center, Vector2 halfSize,
        int[] counts, float[] triangleTexels, float[] indexTexels, int overflowedBins)
    {
        this.triangles = triangles;
        this.counts = counts;
        Center = center;
        HalfSize = halfSize;
        TriangleTexels = triangleTexels;
        IndexTexels = indexTexels;
        OverflowedBins = overflowedBins;
    }

    /// <summary>
    /// Validate the raycast's vertices before trusting them as world-space geometry.
    /// The hit must lie inside the XZ triangle, within 1 cm of its height plane.
    /// Navmesh reachability, selected layer, and collision ownership are external checks.
    /// </summary>
    public static bool TryValidateHit(FloorTriangle triangle, Vector3 hit) =>
        ValidPoint(hit) && ValidTriangle(triangle) &&
        TryHeight(triangle, hit.X, hit.Z, out var y) && Math.Abs(y - hit.Y) <= MaximumHitPlaneError;

    /// <summary>
    /// Inputs are copied. Invalid geometry/footprint or more than MaxTriangles fails
    /// the whole build, without truncation. A local bin overflow is explicitly marked
    /// invalid, so an omitted nearer layer can never reveal another stored layer.
    /// Worst-case construction tests MaxTriangles * BinSize squared triangle/bin pairs.
    /// </summary>
    public static bool TryBuild(IReadOnlyList<FloorTriangle>? input, Vector2 center, Vector2 halfSize,
        out GeometryFloorAtlas? atlas)
    {
        atlas = null;
        if (input is null || input.Count > MaxTriangles || !MathEx.Finite(center) || !MathEx.Finite(halfSize)
            || MathF.Abs(center.X) > MaxCoordinate || MathF.Abs(center.Y) > MaxCoordinate
            || halfSize.X <= 0 || halfSize.Y <= 0 || halfSize.X > MaxCoordinate || halfSize.Y > MaxCoordinate)
            return false;
        var minX = (double)center.X - halfSize.X;
        var minZ = (double)center.Y - halfSize.Y;
        var maxX = (double)center.X + halfSize.X;
        var maxZ = (double)center.Y + halfSize.Y;
        if (!(maxX > minX) || !(maxZ > minZ)) return false;
        var stepX = (maxX - minX) / BinSize;
        var stepZ = (maxZ - minZ) / BinSize;
        var triangles = new FloorTriangle[input.Count];
        for (var i = 0; i < triangles.Length; ++i)
        {
            triangles[i] = input[i];
            if (!ValidTriangle(triangles[i])) return false;
        }

        var triangleTexels = new float[TriangleTextureWidth * TriangleTextureHeight * 4];
        var indexTexels = new float[IndexTextureWidth * IndexTextureHeight * 4];
        var counts = new int[BinSize * BinSize];
        var overflowedBins = 0;
        for (var i = 0; i < triangles.Length; ++i)
        {
            var t = triangles[i];
            StoreVertex(triangleTexels, i * 12, t.A);
            StoreVertex(triangleTexels, i * 12 + 4, t.B);
            StoreVertex(triangleTexels, i * 12 + 8, t.C);
            var left = Math.Max(minX, Math.Min(t.A.X, Math.Min(t.B.X, t.C.X)));
            var right = Math.Min(maxX, Math.Max(t.A.X, Math.Max(t.B.X, t.C.X)));
            var top = Math.Max(minZ, Math.Min(t.A.Z, Math.Min(t.B.Z, t.C.Z)));
            var bottom = Math.Min(maxZ, Math.Max(t.A.Z, Math.Max(t.B.Z, t.C.Z)));
            if (left > right || top > bottom) continue;

            // Clip BEFORE converting. A boundary belongs to both adjacent bins.
            var firstX = Math.Clamp((int)Math.Ceiling((left - minX) / stepX) - 1, 0, BinSize - 1);
            var lastX = Math.Clamp((int)Math.Floor((right - minX) / stepX), 0, BinSize - 1);
            var firstZ = Math.Clamp((int)Math.Ceiling((top - minZ) / stepZ) - 1, 0, BinSize - 1);
            var lastZ = Math.Clamp((int)Math.Floor((bottom - minZ) / stepZ), 0, BinSize - 1);
            for (var z = firstZ; z <= lastZ; ++z)
            for (var x = firstX; x <= lastX; ++x)
            {
                var bin = z * BinSize + x;
                if (counts[bin] < 0 || !IntersectsBin(t, minX + x * stepX, minZ + z * stepZ, stepX, stepZ)) continue;
                var start = bin * SlotsPerBin;
                if (counts[bin] == SlotsPerBin)
                {
                    Array.Fill(indexTexels, -1, start, SlotsPerBin);
                    counts[bin] = -1;
                    ++overflowedBins;
                    continue;
                }
                indexTexels[start + counts[bin]++] = i + 1;
            }
        }
        atlas = new(triangles, center, halfSize, counts, triangleTexels, indexTexels, overflowedBins);
        return true;
    }

    /// <summary>CPU reference for the shader's at-most-sixteen-candidate floor match.</summary>
    public bool TryMatch(Vector3 visibleWorldPosition, out float floorY, float maximumHeightError = 0.22f)
    {
        floorY = 0;
        if (!ValidPoint(visibleWorldPosition) || !float.IsFinite(maximumHeightError)
            || maximumHeightError < 0 || maximumHeightError > 0.25f) return false;
        var x = ((double)visibleWorldPosition.X - Center.X) / (2d * HalfSize.X) + 0.5;
        var z = ((double)visibleWorldPosition.Z - Center.Y) / (2d * HalfSize.Y) + 0.5;
        if (x < 0 || x > 1 || z < 0 || z > 1) return false;
        var bx = Math.Min((int)(x * BinSize), BinSize - 1);
        var bz = Math.Min((int)(z * BinSize), BinSize - 1);
        var bin = bz * BinSize + bx;
        var best = double.PositiveInfinity;
        var found = false;
        for (var i = 0; i < counts[bin]; ++i)
        {
            var triangleId = (int)IndexTexels[bin * SlotsPerBin + i] - 1;
            var t = triangles[triangleId];
            if (!TryHeight(t, visibleWorldPosition.X, visibleWorldPosition.Z, out var y)) continue;
            var error = Math.Abs(visibleWorldPosition.Y - y);
            if (error > maximumHeightError || error >= best) continue;
            best = error;
            floorY = (float)y;
            found = true;
        }
        return found;
    }

    private static void StoreVertex(float[] texels, int offset, Vector3 p)
    { texels[offset] = p.X; texels[offset + 1] = p.Y; texels[offset + 2] = p.Z; }

    private static bool ValidPoint(Vector3 point) => MathEx.Finite(point)
        && MathF.Abs(point.X) <= MaxCoordinate && MathF.Abs(point.Y) <= MaxCoordinate && MathF.Abs(point.Z) <= MaxCoordinate;

    private static bool ValidTriangle(FloorTriangle t)
    {
        if (!ValidPoint(t.A) || !ValidPoint(t.B) || !ValidPoint(t.C)) return false;
        var abX = (double)t.B.X - t.A.X; var abY = (double)t.B.Y - t.A.Y; var abZ = (double)t.B.Z - t.A.Z;
        var acX = (double)t.C.X - t.A.X; var acY = (double)t.C.Y - t.A.Y; var acZ = (double)t.C.Z - t.A.Z;
        var nx = abY * acZ - abZ * acY;
        var ny = abZ * acX - abX * acZ;
        var nz = abX * acY - abY * acX;
        var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return Math.Abs(ny) > 1e-10 && Math.Abs(ny) / length >= 0.5;
    }

    private static bool TryHeight(FloorTriangle t, double x, double z, out double y)
    {
        y = 0;
        var abX = (double)t.B.X - t.A.X; var abZ = (double)t.B.Z - t.A.Z;
        var acX = (double)t.C.X - t.A.X; var acZ = (double)t.C.Z - t.A.Z;
        var px = x - t.A.X; var pz = z - t.A.Z;
        var area = abX * acZ - abZ * acX;
        var b = (px * acZ - pz * acX) / area;
        var c = (abX * pz - abZ * px) / area;
        var a = 1 - b - c;
        if (a < -InsideEpsilon || b < -InsideEpsilon || c < -InsideEpsilon) return false;
        y = a * t.A.Y + b * t.B.Y + c * t.C.Y;
        return true;
    }

    private static bool IntersectsBin(FloorTriangle t, double minX, double minZ, double width, double depth)
    {
        var centerX = minX + width * 0.5;
        var centerZ = minZ + depth * 0.5;
        var orientation = Math.Sign(((double)t.B.X - t.A.X) * ((double)t.C.Z - t.A.Z)
            - ((double)t.B.Z - t.A.Z) * ((double)t.C.X - t.A.X));
        return InsideEdge(t.A, t.B) && InsideEdge(t.B, t.C) && InsideEdge(t.C, t.A);

        bool InsideEdge(Vector3 a, Vector3 b)
        {
            var nx = -((double)b.Z - a.Z) * orientation;
            var nz = ((double)b.X - a.X) * orientation;
            var projectedCenter = nx * (centerX - a.X) + nz * (centerZ - a.Z);
            var projectedRadius = Math.Abs(nx) * width * 0.5 + Math.Abs(nz) * depth * 0.5;
            return projectedCenter + projectedRadius >= -1e-9;
        }
    }
}
