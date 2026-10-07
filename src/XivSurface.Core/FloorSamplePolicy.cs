using System.Numerics;

namespace XivSurface.Core;

/// <summary>Refine coarse walkable-mesh heights using a local static-collision hit.</summary>
public static class FloorSamplePolicy
{
    /// <summary>Some game raycasts populate triangle vertices but leave Normal zero.</summary>
    public static Vector3 CollisionNormal(Vector3 supplied, Vector3 a, Vector3 b, Vector3 c)
    {
        if (MathEx.Finite(supplied) && supplied.LengthSquared() > 1e-10f) return supplied;
        if (!MathEx.Finite(a) || !MathEx.Finite(b) || !MathEx.Finite(c)) return Vector3.Zero;
        var normal = Vector3.Cross(b - a, c - a);
        // Orient an otherwise arbitrary triangle winding toward the origin of
        // our downward ray. The subsequent slope test still rejects walls.
        return normal.Y < 0 ? -normal : normal;
    }

    public static bool TryRefine(Vector2 at, float playerHeight, Vector3? nav,
        Vector3? collision, Vector3 normal, out float height)
    {
        height = 0;
        if (!MathEx.Finite(at) || !float.IsFinite(playerHeight) || nav is not { } n
            || collision is not { } c || !MathEx.Finite(n) || !MathEx.Finite(c)
            || !MathEx.Finite(normal)) return false;
        if (Vector2.Distance(at, new(n.X, n.Z)) > 0.55f
            || n.Y < playerHeight - 5 || n.Y > playerHeight + 3
            || Vector2.Distance(at, new(c.X, c.Z)) > 0.02f
            || Math.Abs(c.Y - n.Y) > 0.65f) return false;
        var length = normal.Length();
        if (!float.IsFinite(length) || length < 1e-6f || normal.Y / length < 0.5f) return false;
        height = c.Y;
        return true;
    }
}
