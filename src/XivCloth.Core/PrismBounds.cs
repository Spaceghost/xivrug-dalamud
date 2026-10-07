using System.Numerics;

namespace XivCloth.Core;

/// <summary>Conservative whole-cloth exclusions for the existing one-sided
/// support prism. No finite 3-D AABB is substituted for an infinite extrusion.
/// A box is excluded only by a support or edge half-space of that same prism.
/// Interval bounds round outward; uncertain boxes keep the original face test.</summary>
internal static class PrismBounds
{
    // Keep the clipping footprint's existing outward edge tolerance exactly.
    private const double EdgeTolerance = 1e-9;

    internal static bool ProvesClear(Box3 box, MeasuredTriangle support, float clearance)
        =>ProvesClear(box,new PreparedTriangle(support),clearance);
    internal static bool ProvesClear(Box3 box, in PreparedTriangle support, float clearance)
    {
        // The extra tolerance only makes this optional exclusion stricter.
        // Intersecting/threshold cases always fall back to exact old clipping.
        if (Project(box, support.A, support.Normal).Lo > clearance + EdgeTolerance) return true;
        for (var edge = 0; edge < 3; edge++)
        {
            if (Project(box, support.Vertex(edge), support.Inward(edge)).Hi < -EdgeTolerance) return true;
        }
        return false;
    }

    private static Range Project(Box3 box, D3 origin, D3 direction) =>
        (new Range(box.Minimum.X, box.Maximum.X) - Range.Point(origin.X)) * direction.X
        + (new Range(box.Minimum.Y, box.Maximum.Y) - Range.Point(origin.Y)) * direction.Y
        + (new Range(box.Minimum.Z, box.Maximum.Z) - Range.Point(origin.Z)) * direction.Z;
}
