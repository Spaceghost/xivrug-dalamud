using System.Numerics;

namespace XivSurface.Core;

public sealed partial class LocalFloorLayer
{
    /// <summary>
    /// Exercise bounded managed retirement before entering a scene's query
    /// deadline. The first collection-removal path can otherwise spend several
    /// milliseconds preparing runtime code during live movement.
    /// All synthetic geometry belongs to this isolated local instance: it
    /// never reaches native queries, a caller's layer or rendered support.
    /// Call during owner initialization, not from a framework/render update.
    /// This reduces a measured cold path, not arbitrary scheduler/GC stalls.
    /// </summary>
    public static void PrepareMaintenance()
    {
        var warmup = new LocalFloorLayer(LayerSupportScope.MeasuredCloth);
        var left = new LayerTriangle(new(-2, 0, -1), new(0, 0, -1), new(0, 0, 1), Vector3.UnitY);
        var right = new LayerTriangle(new(0, 0, -1), new(2, 0, 1), new(0, 0, 1), Vector3.UnitY);
        var root = new LayerFloorHit(new(-.2f, 0, 0), left);
        warmup.Seed(root);
        warmup.BeginFrame(new(new(.2f, 0, 0), right), .1);
        warmup.BeginFrame(root, .2);
        // Retire the adjacent face outside the halo, clean its portal, and
        // traverse the retained component through the actual maintenance path.
        warmup.BeginFrame(root, .3, new(-.2f, 0), .1f);
    }
}
