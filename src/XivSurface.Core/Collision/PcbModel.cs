using System.Numerics;

namespace XivSurface.Core.Collision;

public enum PcbDecodeStatus { Complete, UnsupportedVersion, InvalidData, InvalidRequest, LimitExceeded, Cancelled }

public readonly record struct PcbBounds(Vector3 Min, Vector3 Max)
{
    internal bool IsValid => Numeric.Finite(Min) && Numeric.Finite(Max)
        && Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z;

    // Conservative triangle-AABB broadphase, not a triangle/box intersection claim.
    internal bool MayContain(Vector3 a, Vector3 b, Vector3 c)
    {
        var low = Vector3.Min(a, Vector3.Min(b, c));
        var high = Vector3.Max(a, Vector3.Max(b, c));
        return MathF.BitDecrement(low.X) <= Max.X && MathF.BitIncrement(high.X) >= Min.X
            && MathF.BitDecrement(low.Y) <= Max.Y && MathF.BitIncrement(high.Y) >= Min.Y
            && MathF.BitDecrement(low.Z) <= Max.Z && MathF.BitIncrement(high.Z) >= Min.Z;
    }
}

public readonly record struct PcbQuery(ulong LayerMask, ulong MaterialMask, ulong MaterialValue, PcbBounds? Bounds = null)
{
    /// <summary>Profiled native ray filter, not a walkability or connected-floor proof.</summary>
    public static PcbQuery GameFloor => new(1, 0x4000, 0x4000);
    internal bool IsValid => LayerMask != 0 && (!Bounds.HasValue || Bounds.Value.IsValid);
    internal bool Matches(ulong material) => MaterialValue == 0
        ? (material & MaterialMask) != 0 : (material & MaterialMask) == MaterialValue;
}

public readonly record struct PcbCollider(Matrix4x4 World, ulong LayerMask, ulong ObjectMaterialMask, ulong ObjectMaterialValue)
{
    public static PcbCollider Identity => new(Matrix4x4.Identity, 1, 0, 0);
    // The game's zero-object-mask fast path ignores ObjectMaterialValue.
    internal ulong Material(ulong primitive) => ObjectMaterialMask == 0
        ? primitive : (primitive & ~ObjectMaterialMask) | ObjectMaterialValue;
}

public readonly record struct PcbDecodeLimits(int Bytes, int Nodes, int Primitives, int Vertices)
{
    public static PcbDecodeLimits Maximum => new(8 * 1024 * 1024, 8192, 16384, 262144);
    public static PcbDecodeLimits Default => new(2 * 1024 * 1024, 2048, 8192, 65536);
    internal bool IsValid => Bytes >= 64 && Bytes <= Maximum.Bytes && Nodes >= 1 && Nodes <= Maximum.Nodes
        && Primitives >= 0 && Primitives <= Maximum.Primitives && Vertices >= 0 && Vertices <= Maximum.Vertices;
}

public readonly record struct PcbTriangle(Vector3 A, Vector3 B, Vector3 C,
    ulong PrimitiveMaterial, ulong EffectiveMaterial, int NodeOffset, int PrimitiveIndex);

/// <summary>Owned, filtered collision geometry from one complete supplied PCB buffer.
/// This does not establish native lifetime, complete scene coverage or rendered-model equivalence.</summary>
public sealed class OwnedPcbMesh
{
    private readonly PcbTriangle[] triangles;
    internal OwnedPcbMesh(PcbTriangle[] triangles, int version, int nodes, int primitives, int vertices)
    { this.triangles = triangles; Version = version; NodeCount = nodes; SourcePrimitiveCount = primitives; SourceVertexCount = vertices; }
    public int Version { get; }
    public int NodeCount { get; }
    public int SourcePrimitiveCount { get; }
    public int SourceVertexCount { get; }
    public ReadOnlySpan<PcbTriangle> Triangles => triangles;
}

public readonly record struct PcbDecodeResult(PcbDecodeStatus Status, OwnedPcbMesh? Mesh)
{
    internal static PcbDecodeResult Refuse(PcbDecodeStatus status) => new(status, null);
}

internal static class Numeric
{
    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    internal static bool TransformValid(Matrix4x4 m)
    {
        if (!Finite(new(m.M11, m.M12, m.M13)) || !Finite(new(m.M21, m.M22, m.M23))
            || !Finite(new(m.M31, m.M32, m.M33)) || !Finite(new(m.M41, m.M42, m.M43))
            || m.M14 != 0 || m.M24 != 0 || m.M34 != 0 || m.M44 != 1) return false;
        var determinant = (double)m.M11 * ((double)m.M22 * m.M33 - (double)m.M23 * m.M32)
            - (double)m.M12 * ((double)m.M21 * m.M33 - (double)m.M23 * m.M31)
            + (double)m.M13 * ((double)m.M21 * m.M32 - (double)m.M22 * m.M31);
        return double.IsFinite(determinant) && determinant != 0;
    }
    internal static bool Nondegenerate(Vector3 a, Vector3 b, Vector3 c)
    {
        double x = (double)b.X - a.X, y = (double)b.Y - a.Y, z = (double)b.Z - a.Z;
        double u = (double)c.X - a.X, v = (double)c.Y - a.Y, w = (double)c.Z - a.Z;
        return y * w - z * v != 0 || z * u - x * w != 0 || x * v - y * u != 0;
    }
}
