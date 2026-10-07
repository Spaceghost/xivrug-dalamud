using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class PartialClothSupportTests
{
    private static readonly SupportQueryIdentity Origin = new(1, 1, 0, new(0, .18f, 0), .35f, 1.35f);

    [Fact]
    public void OneMissingOuterCornerCanGatherIntoLargestConfirmedSquare()
    {
        var cache = new RollingClothSupport();
        var queries = new Queries { VertexAllowed = (_, at) => Vector2.DistanceSquared(at, new(2, 2)) > .000001f };
        Warm(cache, queries);
        Assert.False(cache.TrySnapshot(Vector2.Zero, 89 / 30d, out _));
        Assert.True(cache.TrySupportedSnapshot(Vector2.Zero, 89 / 30d, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(10, snapshot.Width); Assert.Equal(snapshot.Width, snapshot.Height);
        Assert.Equal(81, snapshot.CellCeilings.Length);
        Assert.DoesNotContain(snapshot.Contacts, point => Vector2.DistanceSquared(new(point.X, point.Z), new(2, 2)) < .000001f);
        Assert.True(snapshot.MatchesCenterFloor(Vector2.Zero, 0));
        Assert.True(snapshot.Center.X - snapshot.Half.X < 0 && snapshot.Center.X + snapshot.Half.X > 0);
        Assert.True(snapshot.Center.Y - snapshot.Half.Y < 0 && snapshot.Center.Y + snapshot.Half.Y > 0);
    }

    [Fact]
    public void AFullConfirmedWindowKeepsTheSameBoundsAndContacts()
    {
        var cache = new RollingClothSupport(); var queries = new Queries();
        Warm(cache, queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 89 / 30d, out var full));
        Assert.True(cache.TrySupportedSnapshot(Vector2.Zero, 89 / 30d, out var supported));
        Assert.Equal(full!.MinX, supported!.MinX); Assert.Equal(full.MinZ, supported.MinZ);
        Assert.Equal(full.Width, supported.Width); Assert.Equal(full.Height, supported.Height);
        Assert.Equal(full.Contacts, supported.Contacts); Assert.Equal(full.CellCeilings, supported.CellCeilings);
    }

    [Fact]
    public void AnUnknownCellTouchingTheCenterCannotBecomeACompactPatch()
    {
        var cache = new RollingClothSupport();
        var queries = new Queries { CellAllowed = (_, at) => Vector2.DistanceSquared(at, new(.2f, .2f)) > .000001f };
        Warm(cache, queries);
        Assert.False(cache.TrySupportedSnapshot(Vector2.Zero, 89 / 30d, out _));
    }

    [Fact]
    public void ExactPendingOriginRequiresOptInAndNeverPromotesIt()
    {
        var cache = new RollingClothSupport();
        var next = Origin with { CompressionOrigin = new(.2f, .18f, 0) };
        var queries = new Queries
        {
            VertexAllowed = (identity, at) => identity == Origin || Math.Max(Math.Abs(at.X), Math.Abs(at.Y)) < .81f,
            Height = identity => identity == Origin ? 0 : .2f
        };
        Warm(cache, queries);
        for (var frame = 90; frame < 180; frame++) cache.Update(next, Vector2.Zero, frame / 30d, 100, queries);
        Assert.True(cache.Reanchoring);
        Assert.True(cache.TrySupportedSnapshot(Vector2.Zero, 179 / 30d, out var active));
        Assert.Equal(Origin, active!.Identity); Assert.Equal(11, active.Width);
        Assert.True(cache.TrySupportedSnapshot(Vector2.Zero, 179 / 30d, out var pending, next));
        Assert.Equal(next, pending!.Identity); Assert.Equal(5, pending.Width);
        Assert.All(pending.Contacts, point => Assert.Equal(.2f, point.Y));
        Assert.True(pending.MatchesCenterFloor(Vector2.Zero, .2f));
        Assert.Equal(Origin, cache.ActiveIdentity); Assert.True(cache.Reanchoring);
        Assert.False(cache.TrySupportedSnapshot(Vector2.Zero, 179 / 30d, out _, next with { Layer = 5 }));
    }

    [Fact]
    public void ExpiredOrResetEvidenceCannotKeepACompactPatchVisible()
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache, queries);
        Assert.False(cache.TrySupportedSnapshot(Vector2.Zero, 6, out _));
        cache.Reset();
        Assert.False(cache.TrySupportedSnapshot(Vector2.Zero, 89 / 30d, out _));
    }

    [Fact]
    public void AUsableCenteredPatchWinsOverALargerPatchThatWouldShrinkTheRugToNothing()
    {
        var cache = new RollingClothSupport();
        var desired = new Vector2(.3999f, 0);
        var queries = new Queries { VertexAllowed = (_, at) =>
            at.X >= -1.60001f && at.X <= .40001f && Math.Abs(at.Y) <= 2.00001f
            || at.X >= -.40001f && at.X <= 1.20001f && Math.Abs(at.Y) <= .40001f };
        for (var frame = 0; frame < 90; frame++) cache.Update(Origin, desired, frame / 30d, 100, queries);
        Assert.True(cache.TrySupportedSnapshot(desired, 89 / 30d, out var snapshot));
        Assert.NotNull(snapshot);
        var usableHalf = snapshot.Half - Vector2.Abs(snapshot.Center - desired);
        Assert.True(usableHalf.X > .39f && usableHalf.Y > .39f,
            $"Material would collapse despite a confirmed centered patch: {usableHalf}.");
        Assert.Equal(3, snapshot.Width);
    }

    [Theory]
    [InlineData(0, .4f, .2f, false)]
    [InlineData(0, .8f, 0, false)]
    [InlineData(-.4f, .4f, 0, true)]
    public void RequiresThreeByThreeNodesAndCenterStrictlyInside(float minimum, float maximum, float center, bool expected)
    {
        var cache = new RollingClothSupport();
        var queries = new Queries { VertexAllowed = (_, at) => at.X >= minimum - .00001f && at.X <= maximum + .00001f
            && at.Y >= minimum - .00001f && at.Y <= maximum + .00001f };
        var desired = new Vector2(center, center);
        for (var frame = 0; frame < 90; frame++) cache.Update(Origin, desired, frame / 30d, 100, queries);
        Assert.Equal(expected, cache.TrySupportedSnapshot(desired, 89 / 30d, out var snapshot));
        if (expected) { Assert.NotNull(snapshot); Assert.Equal(3, snapshot.Width); }
    }

    private static void Warm(RollingClothSupport cache, Queries queries)
    {
        for (var frame = 0; frame < 90; frame++) cache.Update(Origin, Vector2.Zero, frame / 30d, 100, queries);
    }

    private sealed class Queries : IClothSupportQueries
    {
        public Func<SupportQueryIdentity, Vector2, bool> VertexAllowed = (_, _) => true;
        public Func<SupportQueryIdentity, Vector2, bool> CellAllowed = (_, _) => true;
        public Func<SupportQueryIdentity, float> Height = _ => 0;
        public bool TryVertex(SupportQueryIdentity identity, Vector2 nominal, out Vector3 contact)
        { contact = new(nominal.X, Height(identity), nominal.Y); return VertexAllowed(identity, nominal); }
        public bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        {
            ceiling = Height(identity); var midpoint = (a + b + c + d) / 4;
            return CellAllowed(identity, new(midpoint.X, midpoint.Z));
        }
    }
}
