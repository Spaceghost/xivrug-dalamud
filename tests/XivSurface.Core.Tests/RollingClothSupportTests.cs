using System.Numerics;
using System.Diagnostics;
using Xunit;
using XivSurface.Core;

namespace RugSupportPrototype;

public sealed class RollingClothSupportTests(ITestOutputHelper output)
{
    private static readonly SupportQueryIdentity Origin = new(1, 1, 1, Vector3.Zero, 2.5f, 6);
    private sealed class Queries : IClothSupportQueries
    {
        public int Rays;
        public bool Unknown, NaN;
        public float Height;
        public bool TryVertex(SupportQueryIdentity identity, Vector2 nominal, out Vector3 contact)
        { Rays += 2; contact = new(nominal.X, NaN ? float.NaN : Height, nominal.Y); return !Unknown; }
        public bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        { Rays++; ceiling = NaN ? float.NaN : Height; return !Unknown; }
    }
    private static void Warm(RollingClothSupport cache, Queries queries, SupportQueryIdentity? origin = null)
    {
        for (var i = 0; i < 90; i++) cache.Update(origin ?? Origin, Vector2.Zero, i / 30d, 100, queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 89 / 30d, out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(.785398163f)] [InlineData(1.570796326f)]
    public void IncrementalRuntimeQueriesKeepActualRunningWindowCovered(float heading)
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache, queries);
        var direction = new Vector2(MathF.Cos(heading), MathF.Sin(heading));
        var missed = 0; var resident = 0;
        for (var i = 90; i < 600; i++)
        {
            var at = direction * ((i - 90) * 6 / 30f); var before = queries.Rays;
            var status = cache.Update(Origin, at, i / 30d, 100, queries);
            Assert.Equal(queries.Rays - before, status.Rays); Assert.InRange(status.Rays, 0, 100);
            if (!cache.TrySnapshot(at, i / 30d, out var snapshot)) missed++;
            else Assert.Contains(snapshot!.Contacts, p => p.X >= at.X && p.Z >= at.Y);
            resident = Math.Max(resident, status.ResidentSamples);
        }
        output.WriteLine($"heading={heading}, missing={missed}, rays={queries.Rays}, resident={resident}");
        Assert.Equal(0, missed); Assert.InRange(resident, 1, 1000);
    }

    [Theory]
    [InlineData(0)] [InlineData(.785398163f)]
    public void ReanchorsConcurrentlyWithoutCancelingBecauseWindowMoved(float heading)
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache, queries);
        var direction = new Vector2(MathF.Cos(heading), MathF.Sin(heading));
        var origin = Origin; var nextRebase = 3.5; var missed = 0; var rebases = 0; var pendingFrames = 0;
        for (var i = 90; i < 600; i++)
        {
            var now = i / 30d; var at = direction * ((i - 90) * 6 / 30f);
            if (now >= nextRebase && !cache.Reanchoring)
            { origin = origin with { CompressionOrigin = new(at.X, 0, at.Y) }; nextRebase = now + .5; rebases++; }
            var before = queries.Rays; var status = cache.Update(origin, at, now, 100, queries, originReserve: 45);
            Assert.InRange(queries.Rays - before, 0, 100);
            if (!status.CurrentCovered) missed++;
            if (status.Reanchoring) pendingFrames++;
        }
        output.WriteLine($"rebase heading={heading}, missing={missed}, rebases={rebases}, pending frames={pendingFrames}, rays={queries.Rays}");
        Assert.True(rebases >= 20); Assert.True(pendingFrames > 0); Assert.Equal(0, missed);
    }

    [Fact]
    public void EveryNewCellAndVertexHasRealQueriesUnknownIsNotAnAnchorFallback()
    {
        var cache = new RollingClothSupport(); var queries = new Queries { Unknown = true };
        for (var i = 0; i < 120; i++)
        {
            var status = cache.Update(Origin, Vector2.Zero, i / 30d, 100, queries);
            Assert.False(status.CurrentCovered); Assert.InRange(status.Rays, 0, 100);
        }
        Assert.False(cache.TrySnapshot(Vector2.Zero, 4, out _)); Assert.True(queries.Rays > 0);
    }

    [Fact]
    public void DesiredWindowOutsideCoverageFailsClosedInsteadOfLaggingBehind()
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache, queries);
        Assert.False(cache.Update(Origin, new(30, 30), 3, 10, queries).CurrentCovered);
        Assert.False(cache.TrySnapshot(new(30, 30), 3, out _));
        Assert.True(cache.TrySnapshot(Vector2.Zero, 3, out _) == false); // bounded cache retired remote old patch
    }

    [Fact]
    public void ZoneOrSceneRevisionCannotUseOldPatchWhilePending()
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache, queries);
        Assert.False(cache.Update(Origin with { Zone = 2 }, Vector2.Zero, 3, 0, queries).CurrentCovered);
        Assert.False(cache.TrySnapshot(Vector2.Zero, 3, out _));
    }

    [Fact]
    public void ChangedVertexInvalidatesItsCellUntilNewMidpointIsMeasured()
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache, queries);
        queries.Height = .5f;
        // Advance into refresh with budget just sufficient for one vertex.
        var status = cache.Update(Origin, Vector2.Zero, 4, 2, queries);
        Assert.False(status.CurrentCovered); Assert.False(cache.TrySnapshot(Vector2.Zero, 4, out _));
        for (var i = 121; i < 180; i++) cache.Update(Origin, Vector2.Zero, i / 30d, 100, queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 179 / 30d, out var snapshot));
        Assert.All(snapshot!.Contacts, p => Assert.Equal(.5f, p.Y));
        Assert.All(snapshot.CellCeilings, y => Assert.Equal(.5f, y));
    }

    [Fact]
    public void ExhaustedWorkDeadlineDoesNotEraseAlreadyFreshCompleteCoverage()
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache, queries);
        var before = queries.Rays;
        var status = cache.Update(Origin, Vector2.Zero, 3, 100, queries, () => false);
        Assert.True(status.CurrentCovered); Assert.Equal(0, status.Rays);
        Assert.Equal(before, queries.Rays);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 3, out var snapshot));
        Assert.True(snapshot!.MatchesCenterFloor(Vector2.Zero, 0));
        // The runtime still obtains this fresh center measurement and rejects
        // incompatible heights; cached coverage alone is not floor evidence.
        Assert.False(snapshot.MatchesCenterFloor(Vector2.Zero, 1));
    }

    [Fact]
    public void ExhaustedWorkDeadlineCannotAuthorizeUnknownOrChangedWorldCoverage()
    {
        var cold = new RollingClothSupport(); var queries = new Queries();
        Assert.False(cold.Update(Origin, Vector2.Zero, 0, 100, queries, () => false).CurrentCovered);
        Assert.Equal(0, queries.Rays);
        foreach (var identity in new[] { Origin with { Zone = 2 }, Origin with { Layer = 2 }, Origin with { GeometryGeneration = 2 } })
        {
            var cache = new RollingClothSupport(); Warm(cache, queries);
            var before = queries.Rays;
            Assert.False(cache.Update(identity, Vector2.Zero, 3, 100, queries, () => false).CurrentCovered);
            Assert.False(cache.TrySnapshot(Vector2.Zero, 3, out _));
            Assert.Equal(before, queries.Rays);
        }
        var moved = new RollingClothSupport(); Warm(moved, queries);
        Assert.False(moved.Update(Origin, new(20, 20), 3, 100, queries, () => false).CurrentCovered);
        Assert.False(moved.TrySnapshot(new(20, 20), 3, out _));
    }

    [Fact]
    public void ExpiryAndDeadlineStayHardLimits()
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache, queries);
        var before = queries.Rays;
        Assert.Equal(0, cache.Update(Origin, Vector2.Zero, 6, 100, queries, () => false).Rays);
        Assert.Equal(before, queries.Rays); Assert.False(cache.TrySnapshot(Vector2.Zero, 6, out _));
    }

    [Fact]
    public void NonfiniteQueryResultsFailClosed()
    {
        var cache = new RollingClothSupport(); var queries = new Queries { NaN = true };
        for (var i = 0; i < 60; i++) Assert.False(cache.Update(Origin, Vector2.Zero, i / 30d, 100, queries).CurrentCovered);
    }

    [Fact]
    public void SnapshotArraysDoNotAlterCacheAndNegativeWorldParityIsExplicit()
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache, queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 89 / 30d, out var first));
        var expected = first!.Contacts[0]; first.Contacts[0] = new(999);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 89 / 30d, out var second));
        Assert.Equal(expected, second!.Contacts[0]); Assert.Equal((second.MinX + second.MinZ) & 1, second.DiagonalParity);
    }

    [Fact]
    public void MeasuresCoordinatorAndFineMeshOverheadWithoutNativeQueries()
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache, queries);
        var times = new List<double>(); var bytes = new List<long>();
        var origin = Origin; var nextRebase = 3.5; var missing = 0;
        for (var i = 90; i < 600; i++)
        {
            var now = i / 30d; var at = new Vector2((i - 90) * 6 / 30f / MathF.Sqrt(2));
            if (now >= nextRebase && !cache.Reanchoring)
            { origin = origin with { CompressionOrigin = new(at.X, 0, at.Y) }; nextRebase = now + .5; }
            var allocated = GC.GetAllocatedBytesForCurrentThread(); var started = Stopwatch.GetTimestamp();
            // Three queries are reserved for runtime player/center floor/LOS.
            cache.Update(origin, at, now, 97, queries);
            if (cache.TrySnapshot(at, now, out var snapshot))
            {
                Assert.Equal(snapshot!.Width, snapshot.Height);
                var coarse = ClothSurface.Build(snapshot.Center, snapshot.Half, 0, snapshot.Contacts,
                    snapshot.Width, (float)now, true, snapshot.CellCeilings, diagonalParity: snapshot.DiagonalParity,
                    materialCenter: at, materialHalf: new(2));
                var fine = SupportVisualRefinement.Refine(coarse, 3);
                SupportVisualRefinement.MapMaterial(fine, snapshot.Center, snapshot.Half, at, new(2));
                Assert.InRange(fine.Size, 31, 37);
            }
            else missing++;
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            bytes.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
        }
        times.Sort(); bytes.Sort();
        output.WriteLine($"Fake-query coordinator+build+refine: median={times[times.Count/2]:F3}ms, p95={times[(int)(times.Count*.95)]:F3}ms, median allocation={bytes[bytes.Count/2]}B/update, max={bytes[^1]}B; missing={missing}");
        Assert.Equal(0, missing); Assert.InRange(bytes[^1], 0, 250_000);
    }
}
