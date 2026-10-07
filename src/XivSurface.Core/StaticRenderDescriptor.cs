using System.Numerics;

namespace XivSurface.Core;

public enum StaticRenderKind { BackgroundPart, TerrainPlate }

/// <summary>Invalid bounds cannot establish that an unresolved resource is far away.</summary>
public enum StaticRenderBoundsDisposition { Invalid, OutsideInterest, Near }

[Flags]
public enum StaticRenderIssue
{
    None = 0, Animated = 1, ParentTransformUnproven = 2,
    LayoutTransformMismatch = 4, NestedLayout = 8,
}

/// <summary>Copied diagnostic evidence only. No native pointer, GPU resource,
/// rendered-material classification or permission to replace collision support.</summary>
public sealed record StaticRenderDescriptor(StaticRenderKind Kind, ulong InstanceKey,
    string ModelPath, uint ResourceId, Matrix4x4 World, Vector3 BoundsMinimum,
    Vector3 BoundsMaximum, StaticRenderIssue Issues)
{
    public bool PhysicsAuthorized => false;
}

public static class StaticRenderDescriptorPolicy
{
    public const int MaximumPathBytes = 512;

    public static bool ValidPath(ReadOnlySpan<byte> path)
    {
        if (path.Length is < 8 or > MaximumPathBytes || !path.StartsWith("bg/"u8)
            || !path.EndsWith(".mdl"u8)) return false;
        foreach (var part in path)
            if (part is < 0x21 or > 0x7e || part is (byte)'\\' or (byte)':' or (byte)'|') return false;
        return path.IndexOf(".."u8) < 0 && path.IndexOf("//"u8) < 0;
    }

    public static bool TryTransform(Vector3 translation, Quaternion rotation, Vector3 scale, out Matrix4x4 world)
    {
        world = default;
        if (!Finite(translation) || !Finite(scale) || !float.IsFinite(rotation.LengthSquared())
            || rotation.LengthSquared() is < .999f or > 1.001f
            || Math.Max(Math.Abs(translation.X), Math.Max(Math.Abs(translation.Y), Math.Abs(translation.Z))) > 1_000_000
            || Math.Min(scale.X, Math.Min(scale.Y, scale.Z)) < .0001f
            || Math.Max(scale.X, Math.Max(scale.Y, scale.Z)) > 1000) return false;
        // Same row-vector S*R, then translation contract as installed LayoutEngine.Transform.Compose.
        world = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation);
        world.Translation = translation;
        return true;
    }

    public static bool Near(Vector3 minimum, Vector3 maximum, Matrix4x4 world, Vector3 center, float radius)
        => ClassifyBounds(minimum, maximum, world, center, radius) == StaticRenderBoundsDisposition.Near;

    public static StaticRenderBoundsDisposition ClassifyBounds(Vector3 minimum, Vector3 maximum,
        Matrix4x4 world, Vector3 center, float radius)
    {
        if (!Finite(minimum) || !Finite(maximum) || !Finite(center) || !float.IsFinite(radius)
            || radius is <= 0 or > 30 || minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z
            || world.M14 != 0 || world.M24 != 0 || world.M34 != 0 || world.M44 != 1)
            return StaticRenderBoundsDisposition.Invalid;
        var lo = new Vector3(float.PositiveInfinity); var hi = new Vector3(float.NegativeInfinity);
        for (var bits = 0; bits < 8; bits++)
        {
            var corner = Vector3.Transform(new Vector3((bits & 1) == 0 ? minimum.X : maximum.X,
                (bits & 2) == 0 ? minimum.Y : maximum.Y, (bits & 4) == 0 ? minimum.Z : maximum.Z), world);
            if (!Finite(corner) || corner.LengthSquared() > 3e12f) return StaticRenderBoundsDisposition.Invalid;
            lo = Vector3.Min(lo, corner); hi = Vector3.Max(hi, corner);
        }
        return Vector3.DistanceSquared(center, Vector3.Clamp(center, lo, hi)) <= radius * radius
            ? StaticRenderBoundsDisposition.Near : StaticRenderBoundsDisposition.OutsideInterest;
    }

    public static bool Equivalent(Matrix4x4 a, Matrix4x4 b)
    {
        // Compare affine action at the origin and unit axes, not quaternion sign.
        return Vector3.DistanceSquared(a.Translation, b.Translation) <= .0001f
            && Vector3.DistanceSquared(Vector3.TransformNormal(Vector3.UnitX, a), Vector3.TransformNormal(Vector3.UnitX, b)) <= .000001f
            && Vector3.DistanceSquared(Vector3.TransformNormal(Vector3.UnitY, a), Vector3.TransformNormal(Vector3.UnitY, b)) <= .000001f
            && Vector3.DistanceSquared(Vector3.TransformNormal(Vector3.UnitZ, a), Vector3.TransformNormal(Vector3.UnitZ, b)) <= .000001f;
    }

    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
