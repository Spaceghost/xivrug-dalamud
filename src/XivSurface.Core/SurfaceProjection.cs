using System.Numerics;

namespace XivSurface.Core;

public enum FootprintShape { Circle, RoundedRectangle }

/// <summary>World-space footprint. Does not supply or flatten surface height.</summary>
public readonly record struct Footprint(
    FootprintShape Shape, Vector2 Center, Vector2 HalfSize, float Rotation,
    float CornerRadius, float Feather)
{
    public bool IsValid => Enum.IsDefined(Shape) && MathEx.Finite(Center) && MathEx.Finite(HalfSize)
        && HalfSize.X > 0 && HalfSize.Y > 0 && float.IsFinite(Rotation)
        && float.IsFinite(CornerRadius) && CornerRadius >= 0
        && CornerRadius <= MathF.Min(HalfSize.X, HalfSize.Y)
        && float.IsFinite(Feather) && Feather >= 0
        && Feather <= MathF.Min(HalfSize.X, HalfSize.Y)
        && (Shape != FootprintShape.Circle || HalfSize.X == HalfSize.Y);

    public Vector2 Local(Vector3 world)
    {
        var p = new Vector2(world.X, world.Z) - Center;
        var c = MathF.Cos(Rotation);
        var s = MathF.Sin(Rotation);
        return new(c * p.X + s * p.Y, -s * p.X + c * p.Y);
    }

    public float Coverage(Vector3 world)
    {
        if (!IsValid || !MathEx.Finite(world)) return 0;
        var p = Local(world);
        float distance;
        if (Shape == FootprintShape.Circle) distance = p.Length() - HalfSize.X;
        else
        {
            var q = Vector2.Abs(p) - HalfSize + new Vector2(CornerRadius);
            distance = Vector2.Max(q, Vector2.Zero).Length()
                + MathF.Min(MathF.Max(q.X, q.Y), 0) - CornerRadius;
        }
        if (distance >= 0) return 0;
        if (Feather == 0) return 1;
        var t = Math.Clamp(-distance / Feather, 0, 1);
        return t * t * (3 - 2 * t);
    }
}

/// <summary>
/// CPU reference for a D3D depth reconstruction shader. Input UV is local to
/// the rendered viewport, top-left origin; depth is device Z in [0,1]. The
/// inverse matrix must belong to the same frame, including projection jitter.
/// Row-vector convention (System.Numerics). Supports standard and reverse Z
/// by using the actual projection, not a hard-coded near/far formula.
/// </summary>
public static class DepthProjection
{
    public static bool TryReconstruct(Vector2 uv, float depth, float clearDepth,
        Matrix4x4 inverseViewProjection, out Vector3 world)
    {
        world = default;
        if (!MathEx.Finite(uv) || uv.X < 0 || uv.Y < 0 || uv.X > 1 || uv.Y > 1
            || !float.IsFinite(depth) || depth < 0 || depth > 1
            || !float.IsFinite(clearDepth) || clearDepth < 0 || clearDepth > 1
            || depth == clearDepth) return false;
        var h = Vector4.Transform(new Vector4(2 * uv.X - 1, 1 - 2 * uv.Y, depth, 1), inverseViewProjection);
        if (!MathEx.Finite(h) || h.W <= 1e-7f) return false;
        var p = new Vector3(h.X, h.Y, h.Z) / h.W;
        if (!MathEx.Finite(p)) return false;
        world = p;
        return true;
    }
}

public enum CompositeStage { Unavailable, BeforeGameUi, AfterGameUi }

/// <summary>Backend attestation, not a request to draw in a later ImGui callback.</summary>
public readonly record struct SurfaceFrame(
    long FrameId, uint TerritoryId, long GeometryEpoch, CompositeStage Stage,
    bool DepthValid, bool FloorClassificationValid)
{
    public bool CanRender => FrameId >= 0 && TerritoryId != 0 && GeometryEpoch >= 0
        && Stage == CompositeStage.BeforeGameUi && DepthValid && FloorClassificationValid;
}

/// <summary>
/// Visible scene sample. Floor classification must exclude actors, props, walls,
/// water and sky; a depth-derived upward normal alone is NOT floor evidence.
/// </summary>
public readonly record struct SurfaceSample(Vector3 Position, Vector3 Normal, bool IsFloor);

/// <summary>
/// Read-only navmesh adapter result. A stable layer ID prevents stamping a map
/// onto a bridge and the floor below it. Position is nearest allowed floor, not
/// a sampled height to interpolate across holes. There are no movement methods.
/// </summary>
public readonly record struct FloorMatch(
    Vector3 Position, long LayerId, uint TerritoryId, long GeometryEpoch, bool Reachable);

public interface IFloorMask
{
    bool TryMatch(Vector3 visibleWorldPosition, out FloorMatch match);
}

public readonly record struct DecalPoint(Vector3 Position, Vector2 UV, float Coverage);

/// <summary>
/// Reference acceptance math for a reusable surface decal renderer. Production
/// rendering must batch/cache the floor mask on the GPU, never call IPC per pixel.
/// </summary>
public static class SurfaceDecal
{
    public static bool TryProject(SurfaceFrame frame, SurfaceSample sample, IFloorMask floor,
        long selectedLayer, Footprint footprint, float floorTolerance, float minimumNormalY,
        out DecalPoint decal)
    {
        decal = default;
        if (!frame.CanRender || !sample.IsFloor || !MathEx.Finite(sample.Position)
            || !MathEx.Finite(sample.Normal) || !float.IsFinite(floorTolerance)
            || floorTolerance < 0 || !float.IsFinite(minimumNormalY)
            || minimumNormalY < 0 || minimumNormalY > 1) return false;
        var length = sample.Normal.Length();
        if (!float.IsFinite(length) || length < 1e-6f || sample.Normal.Y / length < minimumNormalY)
            return false;
        var coverage = footprint.Coverage(sample.Position);
        if (coverage <= 0 || !floor.TryMatch(sample.Position, out var match)
            || !match.Reachable || match.LayerId != selectedLayer
            || match.TerritoryId != frame.TerritoryId || match.GeometryEpoch != frame.GeometryEpoch
            || !MathEx.Finite(match.Position)) return false;
        // Do not let large tolerances, nearby floors or nearest-point queries
        // fill a cliff/hole. The backend controls a small validated tolerance.
        var distance = Vector3.Distance(sample.Position, match.Position);
        if (!float.IsFinite(distance) || distance > floorTolerance) return false;
        var uv = footprint.Local(sample.Position) / (2 * footprint.HalfSize) + new Vector2(0.5f);
        decal = new(sample.Position, uv, coverage); // preserve the actual scene height
        return true;
    }
}

internal static class MathEx
{
    public static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    public static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);
}
