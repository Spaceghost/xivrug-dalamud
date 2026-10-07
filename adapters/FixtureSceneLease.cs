using System.Numerics;
using XivCloth.Core;

namespace XivRug.Rendering;

/// <summary>A COMPLETE finite mathematical test world, not a game-world
/// authority flag. All source faces are retained, including faces outside the
/// query volume whose one-sided blocked prisms can reach it. There is no
/// production/native constructor. A future provider needs its own admission
/// proof, including unsupported colliders, loaded bytes and static lifetime.</summary>
internal sealed class FixtureCollisionWorld
{
    private readonly MeasuredTriangle[] faces;
    private long epoch = 1, nextGeneration;
    public FixtureCollisionWorld(ReadOnlySpan<MeasuredTriangle> allFaces)
    {
        // Reuse real validation and refuse >128, never trim to a cap.
        faces = new MeasuredTriangleScene(1, allFaces).Triangles.ToArray();
    }
    public void Invalidate() => epoch = checked(epoch + 1);
    public FixtureSceneLease Capture(uint zone, CollisionCoverage volume, double now,
        double lifetime = .25, bool twoSided = false)
    {
        if (zone == 0 || !volume.Valid || !double.IsFinite(now) || now < 0
            || !double.IsFinite(lifetime) || lifetime <= 0 || lifetime > .25
            || !double.IsFinite(now + lifetime) || now + lifetime <= now)
            throw new ArgumentException("Invalid fixture capture.");
        return new(this, epoch, zone, now, now + lifetime,
            new(checked(++nextGeneration), faces, twoSided, volume));
    }
    internal bool Current(long expectedEpoch) => expectedEpoch == epoch;
}

/// <summary>Owned exact geometry, closed coverage volume, immutable observation
/// time and revocable source epoch. Complete only for its synthetic source.</summary>
internal sealed class FixtureSceneLease
{
    private readonly FixtureCollisionWorld source;
    private readonly long epoch;
    public uint Zone { get; }
    public double CapturedAt { get; }
    public double ExpiresAt { get; }
    public MeasuredTriangleScene Scene { get; }
    internal FixtureSceneLease(FixtureCollisionWorld source, long epoch, uint zone,
        double start, double end, MeasuredTriangleScene scene)
    { this.source = source; this.epoch = epoch; Zone = zone; CapturedAt = start; ExpiresAt = end; Scene = scene; }
    public bool CurrentAt(double now) => source.Current(epoch) && double.IsFinite(now)
        && now >= CapturedAt && now <= ExpiresAt;
}
