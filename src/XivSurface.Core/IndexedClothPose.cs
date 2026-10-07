using System.Numerics;
using System.Runtime.InteropServices;

namespace XivSurface.Core;

/// <summary>
/// An owned, immutable final cloth pose for direct rendering. It can fold in any
/// direction and does not imply a heightfield, square lattice, floor bound or
/// foot-clearance certificate. The publisher owns collision/freshness checks.
/// Render this exact pose: endpoint interpolation across several safe physics
/// substeps is not necessarily a safe path between the endpoints.
/// </summary>
public sealed class IndexedClothPose
{
    public const int MaximumVertices = 16384;
    public const int MaximumIndices = 128 * 128 * 6;
    public const float MaximumCoordinate = 10000;
    private readonly Vector3[] positions, normals;
    private readonly Vector2[] uv;
    private readonly int[] indices;

    public ReadOnlySpan<Vector3> Positions => positions;
    public ReadOnlySpan<Vector3> Normals => normals;
    public ReadOnlySpan<Vector2> UV => uv;
    public ReadOnlySpan<int> Indices => indices;

    public IndexedClothPose(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> uv,
        ReadOnlySpan<int> indices)
    {
        if (positions.Length is < 3 or > MaximumVertices || uv.Length != positions.Length
            || indices.Length is < 3 or > MaximumIndices || indices.Length % 3 != 0)
            throw new ArgumentException("Invalid bounded indexed cloth pose.");
        this.positions = positions.ToArray();
        this.uv = uv.ToArray();
        this.indices = indices.ToArray();
        normals = new Vector3[positions.Length];
        var firstNormal = new Vector3[positions.Length];
        var used = new bool[positions.Length];
        for (var i = 0; i < this.positions.Length; i++)
        {
            var p = this.positions[i]; var texture = this.uv[i];
            if (!Finite(p) || Math.Max(Math.Max(Math.Abs(p.X), Math.Abs(p.Y)), Math.Abs(p.Z)) > MaximumCoordinate
                || !float.IsFinite(texture.X) || !float.IsFinite(texture.Y)
                || texture.X is < 0 or > 1 || texture.Y is < 0 or > 1)
                throw new ArgumentException("Invalid indexed cloth position or material coordinate.");
        }
        for (var i = 0; i < this.indices.Length; i += 3)
        {
            var a = this.indices[i]; var b = this.indices[i + 1]; var c = this.indices[i + 2];
            if ((uint)a >= (uint)this.positions.Length || (uint)b >= (uint)this.positions.Length
                || (uint)c >= (uint)this.positions.Length || a == b || a == c || b == c)
                throw new ArgumentException("Invalid indexed cloth triangle.");
            var normal = Vector3.Cross(this.positions[b] - this.positions[a], this.positions[c] - this.positions[a]);
            if (!Finite(normal) || normal.LengthSquared() < 1e-12f)
                throw new ArgumentException("Degenerate indexed cloth triangle.");
            Add(a); Add(b); Add(c);
            void Add(int index)
            {
                normals[index] += normal;
                if (!used[index]) firstNormal[index] = normal;
                used[index] = true;
            }
        }
        for (var i = 0; i < normals.Length; i++)
        {
            if (!used[i]) throw new ArgumentException("Indexed cloth contains unused material particles.");
            // Preserve the material winding, including vertical/overhanging
            // folds. Near cancellation uses an actual incident face, not +Y.
            if (normals[i].LengthSquared() < 1e-12f) normals[i] = firstNormal[i];
            normals[i] = Vector3.Normalize(normals[i]);
        }
    }

    /// <summary>Expands indexed shared vertices into an existing upload buffer,
    /// without moving, lifting, clamping or interpolating the material.</summary>
    public void WriteTriangleList(Span<IndexedClothVertex> destination)
    {
        if (destination.Length != indices.Length)
            throw new ArgumentException("Upload buffer must cover exactly the indexed pose.", nameof(destination));
        for (var i = 0; i < indices.Length; i++)
        {
            var index = indices[i];
            destination[i] = new(positions[index], normals[index], uv[index]);
        }
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>Direct-position vertex layout: no fabricated ground/contact fields.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct IndexedClothVertex(Vector3 position, Vector3 normal, Vector2 uv)
{
    public readonly Vector3 Position = position;
    public readonly Vector3 Normal = normal;
    public readonly Vector2 UV = uv;
}
