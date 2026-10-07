using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class ClothFloorQueryPolicyTests
{
    [Theory]
    [InlineData(0f)] [InlineData(5.4f)] [InlineData(-4.2f)]
    public void LayerRefreshUsesExactUpperBandMarginWithoutResettingAnUnchangedFloor(float ground)
    {
        var start = ground + .35f;
        var boundary = start - ClothFloorQueryPolicy.LayerRefreshTopMargin;
        Assert.False(ClothFloorQueryPolicy.RequiresLayerRefresh(ground, start));
        Assert.False(ClothFloorQueryPolicy.RequiresLayerRefresh(MathF.BitDecrement(boundary), start));
        Assert.True(ClothFloorQueryPolicy.RequiresLayerRefresh(boundary, start));
        Assert.True(ClothFloorQueryPolicy.RequiresLayerRefresh(MathF.BitIncrement(boundary), start));
        Assert.True(ClothFloorQueryPolicy.RequiresLayerRefresh(ground + .5f, start));
    }

    [Fact]
    public void InvalidLayerRefreshEvidenceFailsClosed()
    {
        Assert.True(ClothFloorQueryPolicy.RequiresLayerRefresh(float.NaN, .35f));
        Assert.True(ClothFloorQueryPolicy.RequiresLayerRefresh(0, float.PositiveInfinity));
    }

    [Fact]
    public void HalfYalmStepRecoversWhileStationaryWithoutACompletedSnapshot()
    {
        var cache = new RollingClothSupport();
        var queries = new LayerQueries();
        var origin = new SupportQueryIdentity(1, 1, 0, new(0, .18f, 0), .35f, 1.35f);
        for (var i = 0; i < 30; i++) cache.Update(origin, Vector2.Zero, i / 30d, 100, queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 29 / 30d, out _));
        queries.Height = .5f;
        // Old evidence expires. Every new ray begins below the raised solid
        // floor, so the legacy0.75y rule could never regain a complete snapshot.
        for (var i = 0; i < 30; i++) cache.Update(origin, Vector2.Zero, 4 + i / 30d, 100, queries);
        Assert.False(cache.TrySnapshot(Vector2.Zero, 4 + 29 / 30d, out _));
        Assert.InRange(Math.Abs(queries.Height - (origin.DownwardStart - .35f)), 0, .75f);
        Assert.True(ClothFloorQueryPolicy.RequiresLayerRefresh(queries.Height, origin.DownwardStart));
        cache.Reset();
        origin = origin with { CompressionOrigin = new(0, queries.Height + .18f, 0), DownwardStart = queries.Height + .35f };
        Assert.False(ClothFloorQueryPolicy.RequiresLayerRefresh(queries.Height, origin.DownwardStart));
        for (var i = 0; i < 30; i++) cache.Update(origin, Vector2.Zero, 6 + i / 30d, 100, queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 6 + 29 / 30d, out var snapshot));
        Assert.All(snapshot!.Contacts, contact => Assert.Equal(.5f, contact.Y));
    }

    private sealed class LayerQueries : IClothSupportQueries
    {
        public float Height;
        public bool TryVertex(SupportQueryIdentity identity, Vector2 nominal, out Vector3 contact)
        { contact = new(nominal.X, Height, nominal.Y); return Within(identity); }
        public bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        { ceiling = Height; return Within(identity); }
        private bool Within(SupportQueryIdentity identity) => Height <= identity.DownwardStart
            && Height >= identity.DownwardStart - identity.DownwardLength;
    }

    [Fact]
    public void PlayerProbeStartsAtFeetReferenceRatherThanAboveOverlappingDeck()
    {
        Assert.True(ClothFloorQueryPolicy.TryPlayerProbe(new(10, 2, 20), out var probe));
        Assert.Equal(2.12f, probe.StartY, 4);
        Assert.True(ClothFloorQueryPolicy.Accept(probe, new(10, 2, 20), Vector3.UnitY, out _));
        Assert.False(ClothFloorQueryPolicy.Accept(probe, new(10, 3.5f, 20), Vector3.UnitY, out _));
        Assert.False(ClothFloorQueryPolicy.Accept(probe, new(10, 2.1f, 20), Vector3.UnitY, out _));
    }

    [Theory]
    [InlineData(1)] [InlineData(0)] [InlineData(-1)]
    public void UndersideAndWallsAreNotWalkableSupport(float normalY)
    {
        ClothFloorQueryPolicy.TryPlayerProbe(Vector3.Zero, out var probe);
        Assert.Equal(normalY > .5f, ClothFloorQueryPolicy.Accept(probe, Vector3.Zero, new(0, normalY, 0), out _));
    }

    [Fact]
    public void DesiredCenterUsesAcceptedPlayerSupportBandNotPlayerHeightPlusTwoPointFive()
    {
        Assert.True(ClothFloorQueryPolicy.TryLocalProbe(new(4, 8), 1, out var probe));
        Assert.True(ClothFloorQueryPolicy.Accept(probe, new(4, 1.2f, 8), Vector3.UnitY, out _));
        Assert.True(ClothFloorQueryPolicy.Accept(probe, new(4, .2f, 8), Vector3.UnitY, out _));
        Assert.False(ClothFloorQueryPolicy.Accept(probe, new(4, 2, 8), Vector3.UnitY, out _));
        Assert.False(ClothFloorQueryPolicy.Accept(probe, new(4, -.1f, 8), Vector3.UnitY, out _));
    }

    [Fact]
    public void SlopesSupportedButLateralWrongRayAndNonfiniteEvidenceFailClosed()
    {
        ClothFloorQueryPolicy.TryLocalProbe(Vector2.Zero, 0, out var probe);
        Assert.True(ClothFloorQueryPolicy.Accept(probe, Vector3.Zero, new(.6f, .8f, 0), out _));
        Assert.False(ClothFloorQueryPolicy.Accept(probe, new(.1f, 0, 0), Vector3.UnitY, out _));
        Assert.False(ClothFloorQueryPolicy.Accept(probe, Vector3.Zero, new(float.NaN), out _));
        Assert.False(ClothFloorQueryPolicy.TryPlayerProbe(new(float.NaN), out _));
    }

    [Fact]
    public void APlayerAboveTheBoundedProbeDoesNotSelectAnArbitraryLowerFloor()
    {
        ClothFloorQueryPolicy.TryPlayerProbe(new(0, 2, 0), out var probe);
        Assert.False(ClothFloorQueryPolicy.Accept(probe, Vector3.Zero, Vector3.UnitY, out _));
        Assert.False(ClothFloorQueryPolicy.Accept(default, Vector3.Zero, Vector3.UnitY, out _));
    }

    [Fact]
    public void OldCachedHigherFloorCannotPassMerelyBecauseOriginResetThresholdIsLarger()
    {
        var snapshot = new RollingSupportSnapshot(default, 0, 0, 2, 2, 1,
            [new(0, .5f, 0), new(1, .5f, 0), new(0, .5f, 1), new(1, .5f, 1)], [ .5f ]);
        Assert.False(snapshot.MatchesCenterFloor(new(.5f), 0));
        Assert.True(snapshot.MatchesCenterFloor(new(.5f), .5f));
        Assert.True(snapshot.MatchesCenterFloor(new(.5f), .3f));
        Assert.False(snapshot.MatchesCenterFloor(new(2), .5f));
        Assert.False(snapshot.MatchesCenterFloor(new(.5f), float.NaN));
    }
}
