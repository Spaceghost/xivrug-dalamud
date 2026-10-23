using System.Numerics;
using XivSurface.Core;

namespace XivRug.Rendering;

internal sealed record SupportedClothFrame(Vector2 Center, Vector2 HalfSize, float AnchorY,
    ClothMesh Mesh, int Supported, long SupportEpoch);

/// <summary>One immutable render envelope. The producer transfers an owned
/// mesh whose arrays are not mutated after publication. Map coordinates and
/// lift belong to that same sample, never separately read framework fields.</summary>
internal sealed record ClothDrawPublication(SupportedClothFrame Cloth, uint Territory, long SceneEpoch,
    Vector2 MapCenter, float MapScale, float Lift);

/// <summary>Serializes publication/invalidation, not sampling or GPU work.
/// A reset revokes outstanding producer tickets as well as the current packet.
/// Renderers recheck packet identity immediately before each GPU submission.</summary>
internal sealed class ClothRenderPublication
{
    private readonly object gate = new();
    private long revision;
    private ClothDrawPublication? current;
    public ClothDrawPublication? Current => Volatile.Read(ref current);

    public long BeginUpdate()
    {
        // A still-fresh preceding packet may render while sampling proceeds;
        // the ordinary terrain and foot gates remain mandatory.
        lock (gate) return ++revision;
    }

    public bool Complete(long ticket, ClothDrawPublication? result)
    {
        lock (gate)
        {
            if (ticket <= 0 || ticket != revision) return false;
            // Consume the ticket even on failure; a completed update cannot
            // later replace its own result with a second publication.
            revision++;
            Volatile.Write(ref current, result);
            return true;
        }
    }

    public void Invalidate()
    {
        lock (gate) { revision++; Volatile.Write(ref current, null); }
    }

    public bool IsCurrent(ClothDrawPublication? expected, uint territory, long sceneEpoch)
        => expected is not null && expected.Territory == territory && expected.SceneEpoch == sceneEpoch
            && ReferenceEquals(expected, Current);
}
