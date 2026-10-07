using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class RollingClothVertexFairnessTests
{
    private static readonly SupportQueryIdentity Origin = new(1, 0, 0, new(0, .18f, 0), .35f, 1.35f);

    private sealed class Queries : IClothSupportQueries, IIncrementalClothSupportQueries
    {
        public int Raycasts { get; private set; }
        public LayerQueryResult LastResult { get; private set; }
        public int TimeLeft = 10000;
        public bool AllPending, CenterPending, VertexConsumesFrame;
        public int RayCharge = 2;
        public LayerQueryResult? Override;
        public float Height;
        public readonly List<(SupportQueryIdentity Identity, Vector2 Point)> Calls = [];
        public void PrepareQuery(int allowance) { }
        public bool Deadline() => TimeLeft > 0;
        public void BeginFrame() { Calls.Clear(); TimeLeft = 10000; }
        public bool TryVertex(SupportQueryIdentity identity, Vector2 nominal, out Vector3 contact)
        {
            Calls.Add((identity, nominal)); Raycasts += RayCharge;
            TimeLeft = VertexConsumesFrame ? 0 : TimeLeft - 1;
            contact = new(nominal.X, Height, nominal.Y);
            LastResult = Override ?? (AllPending || CenterPending && nominal == Vector2.Zero
                ? LayerQueryResult.Pending : LayerQueryResult.Success);
            return LastResult == LayerQueryResult.Success;
        }
        public bool TryCell(SupportQueryIdentity identity, Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ceiling)
        {
            Raycasts++; TimeLeft--; ceiling = Height;
            LastResult = LayerQueryResult.Success; return true;
        }
    }

    [Fact]
    public void NearestPendingVertexCannotConsumeEveryFramesEntireOpportunity()
    {
        var cache = new RollingClothSupport(.4f, .4f, 0);
        var query = new Queries { CenterPending = true, VertexConsumesFrame = true };
        var visited = new HashSet<Vector2>();
        for (var frame = 0; frame < 12; frame++)
        {
            query.BeginFrame();
            cache.Update(Origin, Vector2.Zero, frame / 30d, 100, query, query.Deadline);
            foreach (var call in query.Calls) visited.Add(call.Point);
        }
        Assert.Equal(9, visited.Count);
        // Fair scheduling is not evidence: the unresolved center still forbids
        // a full-size publication despite all other vertices receiving service.
        Assert.False(cache.TrySnapshot(Vector2.Zero, 11 / 30d, out _));
    }

    [Fact]
    public void PermanentlyPendingVertexCanReceiveOnlyThreeConsecutiveOpportunities()
    {
        var cache = new RollingClothSupport(.4f, .4f, 0);
        var query = new Queries { AllPending = true, VertexConsumesFrame = true };
        Vector2? previous = null; var run = 0; var longest = 0; var visited = new HashSet<Vector2>();
        for (var frame = 0; frame < 27; frame++)
        {
            query.BeginFrame();
            cache.Update(Origin, Vector2.Zero, frame / 30d, 100, query, query.Deadline);
            var point = Assert.Single(query.Calls).Point; visited.Add(point);
            run = previous == point ? run + 1 : 1; longest = Math.Max(longest, run); previous = point;
        }
        Assert.Equal(3, longest); Assert.Equal(9, visited.Count);
    }

    [Fact]
    public void PendingWithoutAnyNativeAttemptDoesNotReceiveAContinuationLease()
    {
        var cache = new RollingClothSupport(.4f, .4f, 0);
        var query = new Queries { AllPending = true, VertexConsumesFrame = true, RayCharge = 0 };
        var visited = new HashSet<Vector2>();
        for (var frame = 0; frame < 9; frame++)
        {
            query.BeginFrame();
            cache.Update(Origin, Vector2.Zero, frame / 30d, 100, query, query.Deadline);
            visited.Add(Assert.Single(query.Calls).Point);
        }
        Assert.Equal(9, visited.Count); Assert.Equal(0, query.Raycasts);
    }

    [Fact]
    public void LongUpdateGapRetiresContinuationRatherThanRenewingItsShortLease()
    {
        var cache = new RollingClothSupport(.4f, .4f, 0);
        var query = new Queries { AllPending = true, VertexConsumesFrame = true };
        cache.Update(Origin, Vector2.Zero, 0, 100, query, query.Deadline);
        var first = Assert.Single(query.Calls).Point;
        query.BeginFrame();
        cache.Update(Origin, Vector2.Zero, .101, 100, query, query.Deadline);
        Assert.NotEqual(first, Assert.Single(query.Calls).Point);
    }

    [Fact]
    public void PendingVerticesGetOnlyOneAttemptAcrossMultiplePassesAtSameTimestamp()
    {
        var cache = new RollingClothSupport(.4f, .4f, 0);
        var query = new Queries { AllPending = true };
        cache.Update(Origin, Vector2.Zero, 0, 100, query, query.Deadline);
        cache.Update(Origin, Vector2.Zero, 0, 100, query, query.Deadline);
        Assert.Equal(9, query.Calls.Count);
        Assert.All(query.Calls.GroupBy(c => c), group => Assert.Single(group));
        query.BeginFrame();
        cache.Update(Origin, Vector2.Zero, .01, 100, query, query.Deadline);
        Assert.Equal(9, query.Calls.Count);
    }

    [Fact]
    public void RequestedOriginSecondWorkPassDoesNotRepeatItsPendingVertices()
    {
        var cache = new RollingClothSupport(.4f, .4f, 0); var query = new Queries();
        cache.Update(Origin, Vector2.Zero, 0, 100, query, query.Deadline);
        query.BeginFrame(); query.AllPending = true;
        var changed = Origin with { CompressionOrigin = new(.1f, .18f, 0) };
        cache.Update(changed, Vector2.Zero, .1, 100, query, query.Deadline, originReserve: 8);
        Assert.Equal(9, query.Calls.Count);
        Assert.All(query.Calls, c => Assert.Equal(changed, c.Identity));
        Assert.All(query.Calls.GroupBy(c => c), group => Assert.Single(group));
        Assert.True(cache.TrySnapshot(Vector2.Zero, .1, out _, Origin));
        Assert.False(cache.TrySnapshot(Vector2.Zero, .1, out _, changed));
    }

    [Fact]
    public void FairOrderStillPrioritizesRequiredVisibleVerticesOverHalo()
    {
        var cache = new RollingClothSupport(.4f, .4f, .4f);
        var query = new Queries { AllPending = true, VertexConsumesFrame = true };
        var visited = new List<Vector2>();
        for (var frame = 0; frame < 27; frame++)
        {
            query.BeginFrame();
            cache.Update(Origin, Vector2.Zero, frame / 30d, 100, query, query.Deadline);
            visited.Add(Assert.Single(query.Calls).Point);
        }
        Assert.Equal(9, visited.Distinct().Count());
        Assert.All(visited, p => { Assert.InRange(p.X, -.4f, .4f); Assert.InRange(p.Y, -.4f, .4f); });
    }

    [Fact]
    public void PendingRefreshPreservesExistingFreshEvidenceButNeverExtendsItsTtl()
    {
        var cache = new RollingClothSupport(.4f, .4f, 0); var query = new Queries();
        cache.Update(Origin, Vector2.Zero, 0, 100, query, query.Deadline);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 0, out var original));
        query.BeginFrame(); query.AllPending = true;
        cache.Update(Origin, Vector2.Zero, 1.2, 100, query, query.Deadline);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 1.2, out var retained));
        Assert.Equal(original!.Contacts, retained!.Contacts);
        query.BeginFrame();
        cache.Update(Origin, Vector2.Zero, 2.001, 100, query, query.Deadline);
        Assert.False(cache.TrySnapshot(Vector2.Zero, 2.001, out _));
    }

    [Fact]
    public void ExpiredKnownPendingRenewalCannotLockOutNewVisibleVertices()
    {
        var cache = new RollingClothSupport(.4f, .4f, 0); var query = new Queries();
        cache.Update(Origin, Vector2.Zero, 0, 100, query, query.Deadline);
        query.AllPending = true; query.VertexConsumesFrame = true;
        var visited = new HashSet<Vector2>();
        for (var frame = 0; frame < 27; frame++)
        {
            query.BeginFrame();
            cache.Update(Origin, new(.4f, 0), 2.01 + frame / 30d, 100, query, query.Deadline);
            visited.Add(Assert.Single(query.Calls).Point);
        }
        Assert.Equal(9, visited.Count);
        Assert.Contains(visited, p => p.X == .8f);
        Assert.False(cache.TrySnapshot(new(.4f, 0), 2.01 + 26 / 30d, out _));
    }

    [Fact]
    public void UnknownStillInvalidatesAndBacksOffInsteadOfMasqueradingAsPending()
    {
        var cache = new RollingClothSupport(.4f, .4f, 0); var query = new Queries();
        cache.Update(Origin, Vector2.Zero, 0, 100, query, query.Deadline);
        query.BeginFrame(); query.Override = LayerQueryResult.Unknown;
        cache.Update(Origin, Vector2.Zero, 1.2, 100, query, query.Deadline);
        Assert.False(cache.TrySnapshot(Vector2.Zero, 1.2, out _));
        query.BeginFrame();
        cache.Update(Origin, Vector2.Zero, 1.25, 100, query, query.Deadline);
        Assert.Empty(query.Calls);
        query.BeginFrame(); query.Override = null;
        cache.Update(Origin, Vector2.Zero, 1.301, 100, query, query.Deadline);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 1.301, out _));
    }

    [Fact]
    public void ActuallyChangedVertexStillInvalidatesDependentCellRevision()
    {
        var cache = new RollingClothSupport(.4f, .4f, 0); var query = new Queries();
        cache.Update(Origin, Vector2.Zero, 0, 100, query, query.Deadline);
        Assert.True(cache.TrySnapshot(Vector2.Zero, 0, out _));
        query.BeginFrame(); query.Height = .1f;
        cache.Update(Origin, Vector2.Zero, 1.2, 2, query, query.Deadline);
        Assert.Single(query.Calls);
        Assert.False(cache.TrySnapshot(Vector2.Zero, 1.2, out _));
    }

    [Fact]
    public void RealRecoveryCanPublishFullSizeOnlyAfterEveryVertexAndCellIsConfirmed()
    {
        var cache = new RollingClothSupport(.4f, .4f, 0); var query = new Queries { AllPending = true };
        cache.Update(Origin, Vector2.Zero, 0, 100, query, query.Deadline);
        Assert.False(cache.TrySnapshot(Vector2.Zero, 0, out _));
        query.BeginFrame(); query.AllPending = false;
        cache.Update(Origin, Vector2.Zero, .01, 100, query, query.Deadline);
        Assert.True(cache.TrySnapshot(Vector2.Zero, .01, out var snapshot));
        Assert.Equal(new Vector2(.4f), snapshot!.Half);
        Assert.Equal(9, snapshot.Contacts.Length);
        Assert.Equal(4, snapshot.CellCeilings.Length);
    }
}
