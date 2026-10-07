using System.Numerics;
namespace XivSurface.Core;

/// <summary>Exact subdivision of the already accepted piecewise-affine cloth
/// triangles. Adds raster/weave vertices, not collision evidence. Every fine
/// triangle stays within one original triangle; diagonal parity is inherited,
/// not re-created as a new fine-grid checkerboard that could cut across a fold.</summary>
public static class SupportVisualRefinement
{
    /// <summary>Apply to a newly built, unpublished mesh only. Rest UVs map to
    /// the exact material center; compressed world XZ must never replace them.</summary>
    public static void MapMaterial(ClothMesh mesh, Vector2 supportCenter, Vector2 supportHalf,
        Vector2 materialCenter, Vector2 materialHalf)
    {
        if (!MathEx.Finite(supportCenter) || !MathEx.Finite(supportHalf) || !MathEx.Finite(materialCenter)
            || !MathEx.Finite(materialHalf) || supportHalf.X <= 0 || supportHalf.Y <= 0
            || materialHalf.X <= 0 || materialHalf.Y <= 0) throw new ArgumentException("Invalid material window.");
        var scale = supportHalf / materialHalf;
        var offset = new Vector2(.5f) + (supportCenter - supportHalf - materialCenter) / (2 * materialHalf);
        if (!MathEx.Finite(scale) || !MathEx.Finite(offset)) throw new ArgumentException("Nonfinite material transform.");
        for (var i = 0; i < mesh.UV.Length; i++) mesh.UV[i] = mesh.UV[i] * scale + offset;
    }

    public static ClothMesh Refine(ClothMesh coarse, int factor)
    {
        ArgumentNullException.ThrowIfNull(coarse);
        if (factor is < 1 or > 4 || coarse.Size < 2 || coarse.Size > ClothSurface.MaximumSize)
            throw new ArgumentOutOfRangeException(nameof(factor));
        var n = coarse.Size; var size = (n - 1) * factor + 1;
        if (size > ClothSurface.MaximumSize || coarse.Positions.Length != n * n
            || coarse.Indices.Length != (n - 1) * (n - 1) * 6) throw new ArgumentException("Invalid support lattice.");
        var main = new bool[(n - 1) * (n - 1)];
        for (var z = 0; z < n - 1; z++)
        for (var x = 0; x < n - 1; x++)
        {
            var a = z * n + x; var b = a + 1; var c = a + n; var d = c + 1;
            var indices = coarse.Indices.AsSpan((z * (n - 1) + x) * 6, 6);
            if (indices[0] == a && indices[1] == d && indices[2] == b && indices[3] == a && indices[4] == c && indices[5] == d)
                main[z * (n - 1) + x] = true;
            else if (!(indices[0] == a && indices[1] == c && indices[2] == b && indices[3] == b && indices[4] == c && indices[5] == d))
                throw new ArgumentException("Unexpected support topology.");
        }
        foreach (var position in coarse.Positions) if (!MathEx.Finite(position)) throw new ArgumentException("Nonfinite support vertex.");
        if (coarse.GroundMinimum is { } bounds && (bounds.Length != coarse.Positions.Length
            || bounds.Any(value => !float.IsFinite(value)))) throw new ArgumentException("Invalid ground constraints.");
        var positions = new Vector3[size * size]; var uv = new Vector2[size * size];
        var ground = coarse.GroundMinimum is null ? null : new float[size * size];
        for (var z = 0; z < size; z++)
        for (var x = 0; x < size; x++)
        {
            var cellX = Math.Min(x / factor, n - 2); var cellZ = Math.Min(z / factor, n - 2);
            var u = (float)(x - cellX * factor) / factor; var v = (float)(z - cellZ * factor) / factor;
            var at = cellZ * n + cellX;
            var a = coarse.Positions[at]; var b = coarse.Positions[at + 1];
            var c = coarse.Positions[at + n]; var d = coarse.Positions[at + n + 1];
            positions[z * size + x] = main[cellZ * (n - 1) + cellX]
                ? u >= v ? a * (1 - u) + b * (u - v) + d * v : a * (1 - v) + c * (v - u) + d * u
                : u + v <= 1 ? a * (1 - u - v) + b * u + c * v : b * (1 - v) + c * (1 - u) + d * (u + v - 1);
            if (ground is not null && coarse.GroundMinimum is { } source)
            {
                var ga = source[at]; var gb = source[at + 1];
                var gc = source[at + n]; var gd = source[at + n + 1];
                ground[z * size + x] = main[cellZ * (n - 1) + cellX]
                    ? u >= v ? ga * (1 - u) + gb * (u - v) + gd * v : ga * (1 - v) + gc * (v - u) + gd * u
                    : u + v <= 1 ? ga * (1 - u - v) + gb * u + gc * v : gb * (1 - v) + gc * (1 - u) + gd * (u + v - 1);
            }
            uv[z * size + x] = new((float)x / (size - 1), (float)z / (size - 1));
        }
        var result = new int[(size - 1) * (size - 1) * 6]; var cursor = 0;
        for (var z = 0; z < size - 1; z++)
        for (var x = 0; x < size - 1; x++)
        {
            var a = z * size + x; var b = a + 1; var c = a + size; var d = c + 1;
            if (main[(z / factor) * (n - 1) + x / factor])
            { result[cursor++] = a; result[cursor++] = d; result[cursor++] = b; result[cursor++] = a; result[cursor++] = c; result[cursor++] = d; }
            else
            { result[cursor++] = a; result[cursor++] = c; result[cursor++] = b; result[cursor++] = b; result[cursor++] = c; result[cursor++] = d; }
        }
        var normals = new Vector3[positions.Length];
        for (var i = 0; i < result.Length; i += 3)
        {
            var a = result[i]; var b = result[i + 1]; var c = result[i + 2];
            var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            if (normal.Y < 0) normal = -normal;
            normals[a] += normal; normals[b] += normal; normals[c] += normal;
        }
        for (var i = 0; i < normals.Length; i++) normals[i] = MathEx.Finite(normals[i]) && normals[i].LengthSquared() > 1e-12f
            ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
        return new(positions, normals, uv, result, size, ground);
    }
}
