using System.Numerics;

namespace XivSurface.Core;

/// <summary>
/// A bounded, standalone XZ distance field for a route ribbon. Texels are RGBA floats:
/// distance in metres, forward XZ arc length in metres, route world Y, and validity.
/// Samples include both footprint edges; use round(uv * (GridSize - 1)) for texture.Load.
/// </summary>
public static class RouteRibbonField
{
    public const int GridSize = 129;
    public const int MaxPoints = 512;
    public const int Channels = 4;
    public const int FloatCount = GridSize * GridSize * Channels;
    private const float MaxCoordinate = 5000;

    /// <summary>
    /// Builds a new immutable-by-convention texture upload. Invalid/out-of-band samples
    /// are all zero. Invalid points break the polyline; they are never skipped over to
    /// create a new connection. An invalid footprint or an oversized route is empty.
    /// Work is bounded by (MaxPoints - 1) * GridSize squared distance tests, and normally
    /// much smaller because each segment is clipped to its narrow raster bounding box.
    /// </summary>
    /// <remarks>
    /// One texel holds one route layer. At equal XZ distances, the layer nearest center.Y
    /// wins, then the earliest segment. The consumer must also reject mismatching world
    /// heights against the visible floor; this field alone cannot classify scene pixels.
    /// Zero-length XZ segments, including vertical-only travel, do not produce a ribbon.
    /// Arc length restarts after an invalid point, but not after off-footprint segments.
    /// </remarks>
    public static float[] Build(IReadOnlyList<Vector3>? points, Vector3 center, Vector2 halfSize,
        float corridorHalfWidth = 0.6f)
    {
        var result = new float[FloatCount];
        if (points is null || points.Count < 2 || points.Count > MaxPoints ||
            !ValidPoint(center) || !float.IsFinite(halfSize.X) || !float.IsFinite(halfSize.Y) ||
            halfSize.X <= 0 || halfSize.Y <= 0 || halfSize.X > MaxCoordinate || halfSize.Y > MaxCoordinate ||
            !float.IsFinite(corridorHalfWidth) || corridorHalfWidth < 0)
            return result;

        var minX = (double)center.X - halfSize.X;
        var minZ = (double)center.Z - halfSize.Y;
        var maxX = (double)center.X + halfSize.X;
        var maxZ = (double)center.Z + halfSize.Y;
        var stepX = (2d * halfSize.X) / (GridSize - 1);
        var stepZ = (2d * halfSize.Y) / (GridSize - 1);
        // Include a texel-diagonal halo so nearest-texel sampling cannot expose holes
        // at the band boundary. The shader applies the final visual ribbon width.
        var band = corridorHalfWidth + Math.Sqrt(stepX * stepX + stepZ * stepZ);
        var bandSquared = band * band;
        var nearestSquared = new double[GridSize * GridSize];
        Array.Fill(nearestSquared, double.PositiveInfinity);
        var arc = 0d;

        for (var i = 1; i < points.Count; ++i)
        {
            var a = points[i - 1];
            var b = points[i];
            if (!ValidPoint(a) || !ValidPoint(b))
            {
                arc = 0;
                continue;
            }

            var dx = (double)b.X - a.X;
            var dz = (double)b.Z - a.Z;
            var lengthSquared = dx * dx + dz * dz;
            if (lengthSquared == 0) continue;
            var length = Math.Sqrt(lengthSquared);
            var startArc = arc;
            arc += length;

            // Clip in double precision BEFORE conversion to integer texel indices.
            // Even extreme finite width values cannot overflow an integer conversion.
            var left = Math.Max(minX, Math.Min(a.X, b.X) - band);
            var right = Math.Min(maxX, Math.Max(a.X, b.X) + band);
            var top = Math.Max(minZ, Math.Min(a.Z, b.Z) - band);
            var bottom = Math.Min(maxZ, Math.Max(a.Z, b.Z) + band);
            if (left > right || top > bottom) continue;
            var firstX = Math.Clamp((int)Math.Ceiling((left - minX) / stepX), 0, GridSize - 1);
            var lastX = Math.Clamp((int)Math.Floor((right - minX) / stepX), 0, GridSize - 1);
            var firstZ = Math.Clamp((int)Math.Ceiling((top - minZ) / stepZ), 0, GridSize - 1);
            var lastZ = Math.Clamp((int)Math.Floor((bottom - minZ) / stepZ), 0, GridSize - 1);
            for (var z = firstZ; z <= lastZ; ++z)
            {
                var worldZ = minZ + z * stepZ;
                for (var x = firstX; x <= lastX; ++x)
                {
                    var worldX = minX + x * stepX;
                    var t = Math.Clamp(((worldX - a.X) * dx + (worldZ - a.Z) * dz) / lengthSquared, 0, 1);
                    var offsetX = worldX - (a.X + t * dx);
                    var offsetZ = worldZ - (a.Z + t * dz);
                    var distanceSquared = offsetX * offsetX + offsetZ * offsetZ;
                    if (distanceSquared > bandSquared) continue;

                    var texel = z * GridSize + x;
                    var index = texel * Channels;
                    var oldDistanceSquared = nearestSquared[texel];
                    var y = a.Y + t * ((double)b.Y - a.Y);
                    // Tiny numerical differences at shared crossings should not select
                    // an upstairs route over an equally near route on the current floor.
                    const double tieEpsilon = 1e-10;
                    if (distanceSquared > oldDistanceSquared + tieEpsilon ||
                        (Math.Abs(distanceSquared - oldDistanceSquared) <= tieEpsilon &&
                         Math.Abs(y - center.Y) >= Math.Abs((double)result[index + 2] - center.Y)))
                        continue;

                    nearestSquared[texel] = distanceSquared;
                    result[index] = (float)Math.Sqrt(distanceSquared);
                    result[index + 1] = (float)(startArc + t * length);
                    result[index + 2] = (float)y;
                    result[index + 3] = 1;
                }
            }
        }
        return result;
    }

    private static bool ValidPoint(Vector3 point) => MathEx.Finite(point) &&
        MathF.Abs(point.X) <= MaxCoordinate && MathF.Abs(point.Y) <= MaxCoordinate && MathF.Abs(point.Z) <= MaxCoordinate;
}
