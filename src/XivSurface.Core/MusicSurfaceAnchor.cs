using System.Numerics;

namespace XivSurface.Core;

/// <summary>Maps the material chart onto the actual prepared indexed cloth.
/// This only interpolates existing render geometry; it is not floor evidence.
/// Y lift is added in world-up after sampling each actual triangle, never a
/// bilinear height approximation or an assumed uncompressed world square.</summary>
public static class MusicSurfaceAnchor
{
    public static bool TryGround(ClothMesh mesh, ReadOnlySpan<Vector3> preparedPositions,
        Vector2 materialHalfSize, ReadOnlySpan<MusicVertex> local, Span<MusicVertex> output)
    {
        if (mesh is null || mesh.Size is < 2 or > ClothSurface.MaximumSize || local.Length is < 3 or > MusicVisualization.MaximumVertices
            || local.Length % 3 != 0 || output.Length < local.Length || local.Overlaps(output)
            || !Finite(materialHalfSize) || materialHalfSize.X <= 0 || materialHalfSize.Y <= 0) return false;
        var n = mesh.Size; var count = n * n;
        if (mesh.Positions is null || mesh.UV is null || mesh.Indices is null || mesh.Positions.Length != count
            || preparedPositions.Length != count || mesh.UV.Length != count || mesh.Indices.Length != (n - 1) * (n - 1) * 6) return false;
        // MapMaterial produces a rectilinear UV grid even after affine remap.
        // Use its actual axis coordinates and indices, not an assumed [0,1]
        // square or a newly invented alternating diagonal.
        for (var z = 0; z < n; z++)
        for (var x = 0; x < n; x++)
        {
            var i = z * n + x; var uv = mesh.UV[i];
            if (!Finite(uv) || !Finite(preparedPositions[i])
                || uv.X != mesh.UV[x].X || uv.Y != mesh.UV[z * n].Y
                || x > 0 && uv.X <= mesh.UV[i - 1].X || z > 0 && uv.Y <= mesh.UV[i - n].Y) return false;
        }
        for (var z = 0; z < n - 1; z++)
        for (var x = 0; x < n - 1; x++)
        {
            var a = z * n + x; var b = a + 1; var c = a + n; var d = c + 1;
            var t = mesh.Indices.AsSpan((z * (n - 1) + x) * 6, 6);
            if (!(t[0] == a && t[1] == d && t[2] == b && t[3] == a && t[4] == c && t[5] == d)
                && !(t[0] == a && t[1] == c && t[2] == b && t[3] == b && t[4] == c && t[5] == d)) return false;
        }
        // Validate the complete output first. Unknown coverage never submits a
        // clipped subset and cannot leave a partially written accepted frame.
        for (var i = 0; i < local.Length; i++)
            if (!Map(mesh, preparedPositions, materialHalfSize, local[i], out _)) return false;
        for (var i = 0; i < local.Length; i++)
        { Map(mesh, preparedPositions, materialHalfSize, local[i], out var vertex); output[i] = vertex; }
        return true;
    }

    private static bool Map(ClothMesh mesh, ReadOnlySpan<Vector3> positions, Vector2 half,
        MusicVertex local, out MusicVertex result)
    {
        result = default;
        var p = local.Position; var color = local.Color;
        if (!Finite(p) || p.Y is < 0 or > MusicVisualization.MaximumHeight
            || Math.Abs(p.X) > half.X || Math.Abs(p.Z) > half.Y
            || !float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z) || !float.IsFinite(color.W)
            || color.X is < 0 or > 1 || color.Y is < 0 or > 1 || color.Z is < 0 or > 1 || color.W is < 0 or > 1) return false;
        var uv = new Vector2(.5f + p.X / (2 * half.X), .5f + p.Z / (2 * half.Y));
        var x = Cell(mesh.UV, mesh.Size, uv.X, false); var z = Cell(mesh.UV, mesh.Size, uv.Y, true);
        if (x < 0 || z < 0) return false;
        var start = (z * (mesh.Size - 1) + x) * 6;
        for (var triangle = 0; triangle < 2; triangle++)
        {
            var i = start + triangle * 3;
            var a = mesh.Indices[i]; var b = mesh.Indices[i + 1]; var c = mesh.Indices[i + 2];
            if (!Weights(mesh.UV[a], mesh.UV[b], mesh.UV[c], uv, out var wb, out var wc)) continue;
            var world = positions[a] * (1 - wb - wc) + positions[b] * wb + positions[c] * wc + Vector3.UnitY * p.Y;
            if (!Finite(world)) return false;
            result = new(world, color); return true;
        }
        return false;
    }

    private static int Cell(Vector2[] uv, int n, float at, bool vertical)
    {
        if (!float.IsFinite(at)) return -1;
        var minimum = vertical ? uv[0].Y : uv[0].X;
        var maximum = vertical ? uv[(n - 1) * n].Y : uv[n - 1].X;
        if (at < minimum || at > maximum) return -1;
        var lo = 0; var hi = n - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2; var value = vertical ? uv[mid * n].Y : uv[mid].X;
            if (at < value) hi = mid; else lo = mid;
        }
        return Math.Min(lo, n - 2);
    }

    private static bool Weights(Vector2 a, Vector2 b, Vector2 c, Vector2 p, out float wb, out float wc)
    {
        wb = wc = 0;
        var bx = (double)b.X - a.X; var by = (double)b.Y - a.Y;
        var cx = (double)c.X - a.X; var cy = (double)c.Y - a.Y;
        var px = (double)p.X - a.X; var py = (double)p.Y - a.Y;
        var denominator = bx * cy - by * cx;
        if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-16) return false;
        var u = (px * cy - py * cx) / denominator; var v = (bx * py - by * px) / denominator;
        // Rectilinear UV domains have exact diagonal boundaries in real
        // arithmetic; a tiny relative barycentric roundoff is clamped inside
        // the same actual triangle, never across an unsupported mesh boundary.
        if (u < -1e-7 || v < -1e-7 || u + v > 1 + 1e-7) return false;
        u = Math.Max(0, u); v = Math.Max(0, v); var sum = u + v;
        if (sum > 1) { u /= sum; v /= sum; }
        wb = (float)u; wc = (float)v; return true;
    }
    private static bool Finite(Vector2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y);
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
        && Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) <= 1_000_000;
}
