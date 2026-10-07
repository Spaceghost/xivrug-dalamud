using System.Numerics;

namespace XivSurface.Core.Tests;

/// <summary>Cache contract plus an explicit modeled caller-selection boundary.
/// These tests do not claim to exercise native wall rays. The sampler must
/// obtain current-center clearance independently; cached support cannot grant it.</summary>
public sealed class RollingClothContinuityTests
{
    private static readonly SupportQueryIdentity Old = new(129, 7, 1, new(-3.2f, .18f, 0), .35f, 1.35f);
    private static readonly SupportQueryIdentity Requested = Old with { CompressionOrigin = new(0, .18f, 0) };

    private sealed class Queries : IClothSupportQueries, IIncrementalClothSupportQueries
    {
        public int Raycasts { get; private set; }
        public LayerQueryResult LastResult { get; private set; }
        public LayerQueryResult CellResult = LayerQueryResult.Success;
        public int Cells;
        public void PrepareQuery(int allowance) { }
        public bool TryVertex(SupportQueryIdentity identity, Vector2 at, out Vector3 contact)
        {
            Raycasts += 2; LastResult = LayerQueryResult.Success;
            contact = new(at.X, 0, at.Y); return true;
        }
        public bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        {
            Raycasts++; Cells++; ceiling = 0; LastResult = CellResult;
            return CellResult == LayerQueryResult.Success;
        }
    }

    private static RollingClothSupport Warm(Queries queries)
    {
        var cache = new RollingClothSupport();
        cache.Update(Old, Vector2.Zero, 0, 10000, queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 0, out _, Old));
        return cache;
    }

    // Mirrors only strict-first snapshot selection, NOT native proof creation.
    // The nullable identity must already have been checked by the caller for
    // this center/time, same world, bounded drift and successful wall clearance.
    private static bool Select(RollingClothSupport cache, SupportQueryIdentity requested,
        Vector2 center, double now, SupportQueryIdentity? prevalidatedActive,
        out RollingSupportSnapshot? snapshot)
    {
        if (cache.TrySnapshot(center, now, out snapshot, requested)) return true;
        return prevalidatedActive is { } allowed && cache.TrySnapshot(center, now, out snapshot, allowed);
    }

    [Fact]
    public void OriginOnlyRebaseKeepsFullSizeFreshWorldContactsWhilePending()
    {
        var queries = new Queries(); var cache = Warm(queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 0, out var before, Old));
        var measuredRays = queries.Raycasts;
        var status = cache.Update(Requested, Vector2.Zero, .04, 100, queries,
            () => false, preferRequestedOrigin: true);
        Assert.True(status.Reanchoring); Assert.True(status.CurrentCovered);
        Assert.Equal(measuredRays, queries.Raycasts);
        Assert.False(cache.TrySnapshot(Vector2.Zero, .04, out _, Requested));
        Assert.True(Select(cache, Requested, Vector2.Zero, .04, Old, out var after));
        Assert.Equal(Old, after!.Identity);
        Assert.Equal(before!.Width, after.Width); Assert.Equal(before.Height, after.Height);
        Assert.Equal(before.MinX, after.MinX); Assert.Equal(before.MinZ, after.MinZ);
        Assert.Equal(before.Half, after.Half); Assert.True(after.Half.X >= 2 && after.Half.Y >= 2);
        Assert.Equal(before.Contacts, after.Contacts); Assert.Equal(before.CellCeilings, after.CellCeilings);
        Assert.NotSame(before.Contacts, after.Contacts); // new owned publication, no frozen mesh alias
        Assert.True(after.MatchesCenterFloor(Vector2.Zero, 0));
        Assert.False(after.MatchesCenterFloor(Vector2.Zero, .5f));
    }

    [Fact]
    public void HandoffUsesCurrentRequestedWindowRatherThanOldPublishedRectangle()
    {
        var queries = new Queries(); var cache = Warm(queries);
        var center = new Vector2(.2f, .2f);
        cache.Update(Requested, center, .04, 0, queries, preferRequestedOrigin: true);
        Assert.True(Select(cache, Requested, center, .04, Old, out var frame));
        Assert.True(frame!.Half.X - Math.Abs(frame.Center.X - center.X) >= 2);
        Assert.True(frame.Half.Y - Math.Abs(frame.Center.Y - center.Y) >= 2);
        // The rug's configured material chart stays unchanged. It is not shrunk
        // to a partial center patch, translated, or extrapolated beyond samples.
        var materialHalf = new Vector2(2);
        var mesh = ClothSurface.Build(frame.Center, frame.Half, 0, frame.Contacts, frame.Width,
            .04f, false, frame.CellCeilings, diagonalParity: frame.DiagonalParity,
            materialCenter: center, materialHalf: materialHalf);
        SupportVisualRefinement.MapMaterial(mesh, frame.Center, frame.Half, center, materialHalf);
        Assert.Equal((frame.Width - 1) * (frame.Height - 1) * 6, mesh.Indices.Length);
        for (var z = 0; z < frame.Height; z++) for (var x = 0; x < frame.Width; x++)
        {
            var nominal = new Vector2((frame.MinX + x) * frame.Spacing, (frame.MinZ + z) * frame.Spacing);
            var reconstructed = center + (mesh.UV[z * frame.Width + x] * 2 - Vector2.One) * materialHalf;
            Assert.InRange(Vector2.Distance(nominal, reconstructed), 0, .00001f);
        }
    }

    [Fact]
    public void CompletedRequestedOriginWinsWithoutReusingOldClearance()
    {
        var queries = new Queries(); var cache = Warm(queries);
        cache.Update(Requested, Vector2.Zero, .04, 10000, queries, preferRequestedOrigin: true);
        Assert.False(cache.Reanchoring); Assert.Equal(Requested, cache.ActiveIdentity);
        Assert.True(Select(cache, Requested, Vector2.Zero, .04, null, out var frame));
        Assert.Equal(Requested, frame!.Identity);
        Assert.False(cache.TrySnapshot(Vector2.Zero, .04, out _, Old));
    }

    [Fact]
    public void FullyPreparedReplacementWaitsForCallerPromotionWithoutDiscardingOldCoverage()
    {
        var queries = new Queries(); var cache = Warm(queries);
        cache.Update(Requested, Vector2.Zero, .04, 10000, queries,
            preferRequestedOrigin: true, allowPendingPromotion: false);
        Assert.Equal(Old, cache.ActiveIdentity); Assert.Equal(Requested, cache.PendingIdentity);
        Assert.True(cache.TrySupportedSnapshot(Vector2.Zero, .04, out var pending, Requested));
        Assert.True(pending!.Half.X >= 2 && pending.Half.Y >= 2);
        Assert.False(cache.TrySnapshot(Vector2.Zero, .04, out _, Requested));
        Assert.True(Select(cache, Requested, Vector2.Zero, .04, Old, out var continued));
        Assert.Equal(Old, continued!.Identity);

        var rays = queries.Raycasts;
        cache.Update(Requested, Vector2.Zero, .08, 0, queries,
            preferRequestedOrigin: true, allowPendingPromotion: true);
        Assert.Equal(rays, queries.Raycasts); Assert.Equal(Requested, cache.ActiveIdentity);
        Assert.Null(cache.PendingIdentity);
        Assert.True(cache.TrySnapshot(Vector2.Zero, .08, out var replacement, Requested));
        Assert.Equal(pending.Contacts, replacement!.Contacts);
        Assert.False(cache.TrySnapshot(Vector2.Zero, .08, out _, Old));
    }

    [Fact]
    public void DeferredPromotionCannotProlongExpiredOldCoverageEvenWithPreparedReplacement()
    {
        var queries = new Queries(); var cache = Warm(queries);
        cache.Update(Requested, Vector2.Zero, 1.99, 10000, queries,
            preferRequestedOrigin: true, allowPendingPromotion: false);
        Assert.True(cache.TrySupportedSnapshot(Vector2.Zero, 1.99, out _, Requested));
        cache.Update(Requested, Vector2.Zero, 2.01, 0, queries, allowPendingPromotion: false);
        Assert.Equal(Old, cache.ActiveIdentity); Assert.Equal(Requested, cache.PendingIdentity);
        Assert.False(Select(cache, Requested, Vector2.Zero, 2.01, Old, out _));
        cache.Update(Requested, Vector2.Zero, 2.02, 0, queries, allowPendingPromotion: true);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 2.02, out _, Requested));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ZoneLayerOrGeometryChangeCannotReusePreviouslyCheckedOldIdentity(int changed)
    {
        var queries = new Queries(); var cache = Warm(queries);
        var requested = changed switch
        {
            0 => Requested with { Zone = Requested.Zone + 1 },
            1 => Requested with { Layer = Requested.Layer + 1 },
            _ => Requested with { GeometryGeneration = Requested.GeometryGeneration + 1 },
        };
        cache.Update(requested, Vector2.Zero, .04, 0, queries,
            preferRequestedOrigin: true, allowPendingPromotion: false);
        Assert.False(Select(cache, requested, Vector2.Zero, .04, Old, out _));
        Assert.False(cache.Reanchoring);
    }

    [Fact]
    public void EarlierClearanceDoesNotExtendOldSampleTtl()
    {
        var queries = new Queries(); var cache = Warm(queries);
        cache.Update(Requested, Vector2.Zero, .04, 0, queries, preferRequestedOrigin: true);
        Assert.True(Select(cache, Requested, Vector2.Zero, 2, Old, out _));
        Assert.False(Select(cache, Requested, Vector2.Zero, 2.0001, Old, out _));
    }

    [Theory]
    [InlineData(LayerQueryResult.Pending, true)]
    [InlineData(LayerQueryResult.Unknown, false)]
    public void PendingMayRetainFreshEvidenceButUnknownCellImmediatelyInvalidatesIt(LayerQueryResult result, bool expected)
    {
        var queries = new Queries(); var cache = Warm(queries);
        queries.CellResult = result; var cells = queries.Cells;
        // One cell refresh after its refresh threshold, but before TTL. No
        // vertex can run on this one-ray budget, isolating cell invalidation.
        cache.Update(Old, Vector2.Zero, 1.2, 1, queries);
        Assert.Equal(cells + 1, queries.Cells);
        cache.Update(Requested, Vector2.Zero, 1.21, 0, queries, preferRequestedOrigin: true);
        Assert.Equal(expected, Select(cache, Requested, Vector2.Zero, 1.21, Old, out _));
    }

    [Fact]
    public void FullWindowMissingDoesNotFallbackToCentralSupportedPatch()
    {
        var queries = new Queries(); var cache = Warm(queries);
        var center = new Vector2(1.2f, 0);
        cache.Update(Requested, center, .04, 0, queries, preferRequestedOrigin: true);
        Assert.True(cache.TrySupportedSnapshot(center, .04, out _, Old));
        Assert.False(Select(cache, Requested, center, .04, Old, out _));
    }

    [Fact]
    public void BehindWallOrUnknownClearanceCannotBeInferredFromCompleteCachedFloor()
    {
        var queries = new Queries(); var cache = Warm(queries);
        cache.Update(Requested, Vector2.Zero, .04, 0, queries, preferRequestedOrigin: true);
        Assert.True(cache.TrySnapshot(Vector2.Zero, .04, out _, Old));
        // A blocked/unknown current-center ray gives the caller no approved
        // origin. Floor coverage is deliberately insufficient to mint one.
        Assert.False(Select(cache, Requested, Vector2.Zero, .04, null, out _));
        Assert.True(Select(cache, Requested, Vector2.Zero, .04, Old, out _));
    }

    [Fact]
    public void EarlierValidatedIdentityCannotSelectAnotherPendingOrigin()
    {
        var queries = new Queries(); var cache = Warm(queries);
        cache.Update(Requested, Vector2.Zero, .04, 0, queries, preferRequestedOrigin: true);
        var unrelated = Old with { CompressionOrigin = new(1, .18f, 0) };
        Assert.False(Select(cache, Requested, Vector2.Zero, .04, unrelated, out _));
    }
}
