using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class RollingClothRecoveryTests
{
    private static readonly SupportQueryIdentity Old = new(129, 4, 0, new(-6, .18f, -5), .35f, 1.35f);
    private static readonly SupportQueryIdentity Here = Old with { CompressionOrigin = new(0, .18f, 0) };

    // Models one expensive obsolete-origin floor-path query exhausting the
    // frame deadline, regardless of its small *ray* charge. Replacement-origin
    // queries are real, individually accounted fake observations, not anchors.
    private sealed class Queries : IClothSupportQueries, IIncrementalClothSupportQueries
    {
        public int Raycasts { get; private set; }
        public LayerQueryResult LastResult { get; private set; }
        public int TimeUnits = 10000, OldCalls, NewCalls;
        public bool ExpensiveOld, UnknownNew;
        public bool WithinDeadline() => TimeUnits > 0;
        public void PrepareQuery(int allowance) { }
        public bool TryVertex(SupportQueryIdentity identity, Vector2 at, out Vector3 contact)
        {
            var success = Observe(identity, 2);
            contact = new(at.X, identity == Old ? 0 : .2f, at.Y);
            return success;
        }
        public bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        { ceiling = identity == Old ? 0 : .2f; return Observe(identity, 1); }
        private bool Observe(SupportQueryIdentity identity, int rays)
        {
            Raycasts += rays;
            if (identity == Old)
            { OldCalls++; TimeUnits = ExpensiveOld ? 0 : TimeUnits - 1; }
            else { NewCalls++; TimeUnits--; }
            LastResult = identity != Old && UnknownNew ? LayerQueryResult.Unknown : LayerQueryResult.Success;
            return LastResult == LayerQueryResult.Success;
        }
    }

    private static RollingClothSupport Warm(Queries queries)
    {
        var cache = new RollingClothSupport();
        for (var i = 0; i < 30; i++) cache.Update(Old, Vector2.Zero, i / 30d, 100, queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 29 / 30d, out _));
        return cache;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PendingOriginMakesProgressBeforeOldQueryConsumesWholeDeadline(bool preferRequested)
    {
        var queries = new Queries(); var cache = Warm(queries);
        queries.ExpensiveOld = true;
        var reached = false;
        // Expire the old sample refresh timer. With active-first work, every
        // frame spends its wall time on the old origin and NewCalls stays zero.
        for (var i = 0; i < 55; i++)
        {
            queries.TimeUnits = 20;
            var before = queries.Raycasts;
            var now = 2 + i / 30d;
            var result = cache.Update(Here, Vector2.Zero, now, 100, queries,
                queries.WithinDeadline, originReserve: 45, preferRequestedOrigin: preferRequested);
            Assert.Equal(queries.Raycasts - before, result.Rays);
            Assert.InRange(result.Rays, 0, 100);
            if (!cache.TrySnapshot(Vector2.Zero, now, out var frame, Here)) continue;
            Assert.Equal(Here, frame!.Identity);
            Assert.All(frame.Contacts, point => Assert.Equal(.2f, point.Y));
            reached = true; break;
        }
        Assert.True(queries.NewCalls > 0);
        Assert.True(reached, "A valid full-size replacement must finish despite expensive obsolete-origin queries.");
    }

    [Fact]
    public void IneligibleOldOriginReceivesNoOptionalWorkAndCannotPassRequiredSnapshotGate()
    {
        var queries = new Queries(); var cache = Warm(queries);
        var oldCalls = queries.OldCalls;
        queries.UnknownNew = true;
        cache.Update(Here, Vector2.Zero, 1, 100, queries, preferRequestedOrigin: true);
        Assert.Equal(oldCalls, queries.OldCalls);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 1, out _)); // still cached, not current-origin authorization
        Assert.False(cache.TrySnapshot(Vector2.Zero, 1, out _, Here));
        for (var i = 1; i < 60; i++)
        {
            cache.Update(Here, Vector2.Zero, 1 + i / 30d, 100, queries, preferRequestedOrigin: true);
            Assert.False(cache.TrySnapshot(Vector2.Zero, 1 + i / 30d, out _, Here));
        }
        Assert.Equal(oldCalls, queries.OldCalls);
    }

    [Fact]
    public void OrdinaryMovingRequestDoesNotDiscardPendingWorkButExplicitObsoleteOriginReplacementDoes()
    {
        var queries = new Queries(); var cache = Warm(queries);
        cache.Update(Here, Vector2.Zero, 1, 2, queries, preferRequestedOrigin: true);
        Assert.Equal(Here, cache.PendingIdentity);
        var later = Here with { CompressionOrigin = new(7, .18f, 0) };
        cache.Update(later, new(7, 0), 1.01, 2, queries, preferRequestedOrigin: true);
        Assert.Equal(Here, cache.PendingIdentity);
        cache.Update(later, new(7, 0), 1.02, 0, queries, preferRequestedOrigin: true, replacePendingOrigin: true);
        Assert.Equal(later, cache.PendingIdentity);
        Assert.False(cache.TrySnapshot(new(7, 0), 1.02, out _, Here));
        Assert.False(cache.TrySnapshot(new(7, 0), 1.02, out _, later));
        for (var i = 0; i < 30; i++)
            cache.Update(later, new(7, 0), 1.03 + i / 30d, 100, queries, preferRequestedOrigin: true);
        Assert.True(cache.TrySnapshot(new(7, 0), 1.03 + 29 / 30d, out var frame, later));
        Assert.Equal(later, frame!.Identity);
    }

    [Fact]
    public void FullSizeRecoveryNeverPromotesCentralPartialCoverageAsWholeRug()
    {
        var queries = new Queries(); var cache = new RollingClothSupport();
        var center = new Vector2(.13f, -.07f); var wantedHalf = new Vector2(2);
        var partialObserved = false; RollingSupportSnapshot? complete = null;
        for (var i = 0; i < 90; i++)
        {
            var now = i / 30d;
            cache.Update(Here, center, now, 12, queries, preferRequestedOrigin: true);
            if (cache.TrySnapshot(center, now, out complete, Here)) break;
            partialObserved |= cache.TrySupportedSnapshot(center, now, out _);
        }
        Assert.True(partialObserved);
        Assert.NotNull(complete);
        Assert.True(complete!.Half.X - Math.Abs(complete.Center.X - center.X) >= wantedHalf.X);
        Assert.True(complete.Half.Y - Math.Abs(complete.Center.Y - center.Y) >= wantedHalf.Y);
        var mesh = ClothSurface.Build(complete.Center, complete.Half, .2f, complete.Contacts,
            complete.Width, 3, false, complete.CellCeilings, diagonalParity: complete.DiagonalParity,
            materialCenter: center, materialHalf: wantedHalf);
        SupportVisualRefinement.MapMaterial(mesh, complete.Center, complete.Half, center, wantedHalf);
        // The full configured rest chart, not the partial-cache radius, maps
        // back onto every material lattice point. No triangle is removed.
        for (var z = 0; z < complete.Width; z++) for (var x = 0; x < complete.Width; x++)
        {
            var at = z * complete.Width + x;
            var mapped = center + (mesh.UV[at] * 2 - Vector2.One) * wantedHalf;
            var expected = new Vector2((complete.MinX + x) * complete.Spacing, (complete.MinZ + z) * complete.Spacing);
            Assert.InRange(Vector2.Distance(mapped, expected), 0, .00001f);
        }
        Assert.Equal((complete.Width - 1) * (complete.Width - 1) * 6, mesh.Indices.Length);
    }

    [Fact]
    public void ExhaustedDeadlineAndChangedSceneCannotPublishEitherOriginAsFresh()
    {
        var queries = new Queries(); var cache = Warm(queries);
        cache.Update(Here, Vector2.Zero, 4, 100, queries, () => false, preferRequestedOrigin: true);
        Assert.False(cache.TrySnapshot(Vector2.Zero, 4, out _, Here));
        Assert.False(cache.TrySnapshot(Vector2.Zero, 4, out _, Old));
        var changed = Here with { GeometryGeneration = 5 };
        cache.Update(changed, Vector2.Zero, 4.01, 0, queries, preferRequestedOrigin: true);
        Assert.False(cache.TrySnapshot(Vector2.Zero, 4.01, out _, changed));
        Assert.Null(cache.PendingIdentity);
    }
}
